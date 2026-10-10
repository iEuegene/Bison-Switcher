using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

namespace Kright.Services;

/// <summary>A release found on GitHub.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? SetupUrl);

/// <summary>Download progress: bytes so far, total (-1 if unknown), smoothed speed.</summary>
public sealed record DownloadProgress(long Done, long Total, double BytesPerSecond);

/// <summary>
/// Checks GitHub Releases for a newer Bison Switcher and installs it on request.
/// The only network traffic the app makes: one anonymous GET to the GitHub API
/// per check (plus the installer download when the user asks for it). Nothing
/// about the user or their typing is sent.
/// </summary>
public static class UpdateService
{
    public const string Repo = "iEuegene/Bison-Switcher";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
    private const string ApiLatest = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string SetupAsset = "BisonSwitcherSetup.exe";

    // Lazy, and declared so nothing depends on static field order: the client's
    // User-Agent reads Current, which a field initializer above it would see as null.
    private static readonly Lazy<HttpClient> LazyHttp = new(CreateClient);
    private static HttpClient Http => LazyHttp.Value;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"BisonSwitcher/{Current.ToString(3)}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => CurrentVersion.Value;
    private static readonly Lazy<Version> CurrentVersion =
        new(() => Normalize(Assembly.GetExecutingAssembly().GetName().Version));

    /// <summary>Latest published release, or null if the check failed.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(ApiLatest).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseTag(tag, out var version)) return null;
            string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
            string? setup = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), SetupAsset, StringComparison.OrdinalIgnoreCase))
                        setup = a.GetProperty("browser_download_url").GetString();
            return new ReleaseInfo(version, tag, page, setup);
        }
        catch { return null; }
    }

    public static bool IsNewer(ReleaseInfo r) => r.Version > Current;

    /// <summary>"v1.4.0" / "1.4" → 1.4.0. Pre-release suffixes are ignored.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t, out var v)) { version = Normalize(v); return true; }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version? v) =>
        v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    // ---- Download ------------------------------------------------------------
    // Separate client for the installer download. Two things the API client gets
    // wrong for a 50 MB file, which made in-app downloads several times slower than
    // a browser:
    //  * .NET tries the resolved addresses one by one (no Happy Eyeballs), so on a
    //    network with a slow IPv6 route it sticks to IPv6 while the browser races
    //    both and uses IPv4. We connect IPv4 first.
    //  * One TCP stream. The file is fetched in parallel byte ranges instead, which
    //    also survives a dropped connection by resuming just that range.
    private const int Parts = 4;
    private static readonly Lazy<HttpClient> LazyDownload = new(CreateDownloadClient);
    private static HttpClient Dl => LazyDownload.Value;

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Parts + 1,
            ConnectCallback = ConnectPreferIPv4Async,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        var c = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"BisonSwitcher/{Current.ToString(3)}");
        return c;
    }

    private static async ValueTask<Stream> ConnectPreferIPv4Async(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(host.Host, ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var ip in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new IPEndPoint(ip, host.Port), cts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                if (ct.IsCancellationRequested) throw;
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Downloads the installer and runs it silently. The installer closes
    /// this app, updates it and starts the new version. Returns an error message or null.</summary>
    public static async Task<string?> DownloadAndInstallAsync(ReleaseInfo r, IProgress<DownloadProgress>? progress = null)
    {
        if (r.SetupUrl is null) return "У релізі немає файлу інсталятора.";
        string path = Path.Combine(Path.GetTempPath(), $"BisonSwitcherSetup-{r.Version.ToString(3)}.exe");
        string partial = path + ".part";
        try
        {
            await new Downloader(progress).RunAsync(r.SetupUrl, partial).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception ex)
        {
            try { File.Delete(partial); } catch { }
            return "Не вдалося завантажити оновлення: " + ex.Message;
        }
    }

    private sealed class Downloader(IProgress<DownloadProgress>? progress)
    {
        private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);
        private long _done, _total = -1;

        public async Task RunAsync(string url, string path)
        {
            var clock = Stopwatch.StartNew();
            using var stopReporting = new CancellationTokenSource();
            var reporter = ReportLoopAsync(clock, stopReporting.Token);
            try
            {
                long total;
                bool ranges;
                using (var probe = await Dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!probe.IsSuccessStatusCode) throw new IOException($"GitHub відповів {(int)probe.StatusCode}.");
                    total = probe.Content.Headers.ContentLength ?? -1;
                    ranges = probe.Headers.AcceptRanges.Contains("bytes");
                    Interlocked.Exchange(ref _total, total);

                    if (!ranges || total < (4 << 20))
                    {
                        await using var src = await probe.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                        await CopyAsync(src, dst, long.MaxValue).ConfigureAwait(false);
                        if (total > 0 && dst.Length != total) throw new IOException("файл завантажився не повністю.");
                        return;
                    }
                }

                using (var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Write)) f.SetLength(total);
                long size = (total + Parts - 1) / Parts;
                // Each range goes through the original github.com link: its redirect
                // target is a short-lived signed URL, so a retry minutes later still works.
                await Task.WhenAll(Enumerable.Range(0, Parts).Select(i =>
                    RangeAsync(url, path, i * size, Math.Min(total, (i + 1) * size) - 1))).ConfigureAwait(false);
                if (Interlocked.Read(ref _done) != total) throw new IOException("файл завантажився не повністю.");
            }
            finally
            {
                stopReporting.Cancel();
                await reporter.ConfigureAwait(false);
            }
        }

        private async Task RangeAsync(string url, string path, long from, long to)
        {
            long pos = from;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new RangeHeaderValue(pos, to);
                    using var resp = await Dl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                        throw new IOException($"сервер відповів {(int)resp.StatusCode}.");
                    await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await using var dst = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Write, 1 << 16, true);
                    dst.Position = pos;
                    pos += await CopyAsync(src, dst, to - pos + 1).ConfigureAwait(false);
                    if (pos > to) return;
                    throw new IOException("з'єднання обірвалося.");
                }
                catch when (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1)).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Copies up to <paramref name="limit"/> bytes; a read that stalls for
        /// 30 s aborts. Returns the number of bytes copied.</summary>
        private async Task<long> CopyAsync(Stream src, Stream dst, long limit)
        {
            var buf = new byte[1 << 16];
            long copied = 0;
            using var stall = new CancellationTokenSource();
            while (copied < limit)
            {
                stall.CancelAfter(Stall);
                int want = (int)Math.Min(buf.Length, limit - copied);
                int n = await src.ReadAsync(buf.AsMemory(0, want), stall.Token).ConfigureAwait(false);
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                copied += n;
                Interlocked.Add(ref _done, n);
            }
            return copied;
        }

        private async Task ReportLoopAsync(Stopwatch clock, CancellationToken ct)
        {
            long lastBytes = 0;
            double lastTime = 0, speed = 0;
            while (true)
            {
                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                long done = Interlocked.Read(ref _done);
                double now = clock.Elapsed.TotalSeconds;
                double instant = (done - lastBytes) / Math.Max(now - lastTime, 0.001);
                speed = speed == 0 ? instant : speed * 0.7 + instant * 0.3;
                lastBytes = done;
                lastTime = now;
                progress?.Report(new DownloadProgress(done, Interlocked.Read(ref _total), speed));
            }
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
