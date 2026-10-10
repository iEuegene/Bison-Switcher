using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Kright.Services;

/// <summary>A release found on GitHub.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? SetupUrl);

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

    /// <summary>Downloads the installer and runs it silently. The installer closes
    /// this app, updates it and starts the new version. Returns an error message or null.</summary>
    public static async Task<string?> DownloadAndInstallAsync(ReleaseInfo r, IProgress<int>? progress = null)
    {
        if (r.SetupUrl is null) return "У релізі немає файлу інсталятора.";
        string path = Path.Combine(Path.GetTempPath(), $"BisonSwitcherSetup-{r.Version.ToString(3)}.exe");
        try
        {
            using (var resp = await Http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) return $"GitHub відповів {(int)resp.StatusCode}.";
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var dst = File.Create(path);
                var buf = new byte[81920];
                long done = 0;
                int n, last = -1;
                while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                    done += n;
                    if (total > 0)
                    {
                        int pct = (int)(done * 100 / total);
                        if (pct != last) { last = pct; progress?.Report(pct); }
                    }
                }
            }

            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception ex)
        {
            return "Не вдалося завантажити оновлення: " + ex.Message;
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
