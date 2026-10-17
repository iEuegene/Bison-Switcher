using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Kright.Native;
using Microsoft.Win32;

namespace Kright.Services;

/// <summary>
/// Smooth mouse-wheel scrolling. The low-level mouse hook (owned by
/// <see cref="AutoFixEngine"/>) hands every wheel click to <see cref="OnWheel"/>,
/// which only decides and queues — it must return in microseconds, or Windows
/// silently removes the hook. The motion itself is produced on this class's own
/// thread as a stream of small wheel events tagged with
/// <see cref="NativeMethods.SCROLL_MARKER"/>, so the hook lets them through.
///
/// Left alone: high-resolution wheels (deltas that aren't whole clicks — already
/// smooth), Ctrl/Alt + wheel (zoom and app shortcuts), horizontal tilt, games,
/// excluded apps, and admin windows Windows won't let us inject into (UIPI).
/// LibreOffice Calc only scrolls by whole rows, so it gets a "row by row" mode.
/// </summary>
public sealed class SmoothScroller : IDisposable
{
    public enum Verdict { None, Smooth, Rows, HighRes, Modifier, Game, Excluded, Elevated, RowsOff, Coarse }

    /// <summary>What happened to the last wheel click, for the settings page.
    /// A class, so the UI thread never reads a half-written value.</summary>
    public sealed record LastEvent(Verdict Verdict, string App);

    private readonly object _lock = new();
    private readonly ScrollPhysics _physics = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private volatile bool _stop;

    // Smooth mode
    private ScrollFeel _feel = ScrollFeel.Standard;
    private IntPtr _root;
    private bool _shiftAtStart;
    private double _lastFrameMs;

    // Row-by-row mode (LibreOffice Calc)
    private int _rowsLeft, _rowDir, _rowMethod;
    private double _rowIntervalMs, _nextRowMs;
    private IntPtr _rowHwnd, _rowRoot;
    private NativeMethods.POINT _rowPt;
    private int _rowDelta;

    private volatile HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public LastEvent? Last { get; private set; }

    public SmoothScroller() => InvalidateLists();

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Bison Switcher smooth scroll",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        System.Threading.Tasks.Task.Run(GameDetector.Refresh);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(500);
        _thread = null;
    }

    /// <summary>Rebuild the excluded-apps snapshot (UI thread, after a change).</summary>
    public void InvalidateLists() =>
        _excluded = new HashSet<string>(AppSettings.Current.ScrollExcludedApps.Select(a => a.ExeName),
            StringComparer.OrdinalIgnoreCase);

    public static ScrollFeel CurrentFeel()
    {
        var s = AppSettings.Current;
        var custom = new ScrollFeel(s.ScrollStepPercent / 100.0, s.ScrollDurationMs, s.ScrollAcceleration);
        return ScrollFeel.ForPreset(s.ScrollPreset, custom);
    }

    // ------------------------------------------------------------ hook side

    /// <summary>Called on the hook thread for WM_MOUSEWHEEL. True = swallow the
    /// original event (we'll produce the motion ourselves).</summary>
    internal bool OnWheel(in NativeMethods.MSLLHOOKSTRUCT m)
    {
        var s = AppSettings.Current;
        if (!s.SmoothScrollEnabled) return false;
        if ((m.flags & NativeMethods.LLMHF_INJECTED) != 0) return false; // ours, or another tool's

        int delta = (short)(m.mouseData >> 16);
        if (delta == 0) return false;
        if (delta % NativeMethods.WHEEL_DELTA != 0)
        {
            Last = new LastEvent(Verdict.HighRes, "");
            return false;
        }

        bool shift = Down(NativeMethods.VK_SHIFT);
        if (Down(NativeMethods.VK_CONTROL) || Down(NativeMethods.VK_MENU) || (shift && !s.ScrollShiftSmooth))
        {
            lock (_lock) CancelLocked();
            Last = new LastEvent(Verdict.Modifier, "");
            return false;
        }

        var t = WheelTargets.At(m.pt);
        if (t is null) return false;
        var target = t.Value;

        Verdict v =
            _excluded.Contains(target.ExeName) ? Verdict.Excluded :
            target.Game && s.ScrollDisableInGames ? Verdict.Game :
            target.Elevated && !Environment.IsPrivilegedProcess ? Verdict.Elevated :
            target.Coarse ? Verdict.Coarse :
            target.Calc ? (s.CalcScrollMethod is >= 0 and <= 2 ? Verdict.Rows : Verdict.RowsOff) :
            Verdict.Smooth;
        Last = new LastEvent(v, target.ExeName);

        double now = _clock.Elapsed.TotalMilliseconds;
        var feel = CurrentFeel();

        if (v == Verdict.Smooth)
        {
            lock (_lock)
            {
                if (_physics.Active && (target.Root != _root || shift != _shiftAtStart)) _physics.Cancel();
                if (!_physics.Active) _lastFrameMs = now;
                _root = target.Root;
                _shiftAtStart = shift;
                _feel = feel;
                _physics.AddClick(delta, now, feel);
            }
            _wake.Set();
            return true;
        }

        if (v == Verdict.Rows)
        {
            int lines = WheelLines();
            if (lines <= 0) return false; // "one screen per click" mode: leave it to Calc
            int clicks = Math.Abs(delta / NativeMethods.WHEEL_DELTA);
            int dir = Math.Sign(delta);
            lock (_lock)
            {
                if (_rowsLeft > 0 && (dir != _rowDir || target.Root != _rowRoot)) _rowsLeft = 0;
                if (_rowsLeft == 0) _nextRowMs = now;
                _rowDir = dir;
                _rowsLeft += clicks * lines;
                _rowMethod = s.CalcScrollMethod;
                _rowDelta = Math.Max(1, NativeMethods.WHEEL_DELTA / lines);
                _rowIntervalMs = Math.Max(feel.DurationMs, 60) / lines / Math.Max(1, Math.Min(_rowsLeft / lines, 4));
                _rowHwnd = target.Hwnd;
                _rowRoot = target.Root;
                _rowPt = m.pt;
            }
            _wake.Set();
            return true;
        }

        return false;
    }

    private static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static int _linesCache = 3;
    private static long _linesAt = long.MinValue;

    /// <summary>"Lines per click" from Windows mouse settings (re-read every 5 s).</summary>
    private static int WheelLines()
    {
        long now = Environment.TickCount64;
        if (now - _linesAt > 5000)
        {
            _linesAt = now;
            if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWHEELSCROLLLINES, 0, out int v, 0))
                _linesCache = v is > 0 and <= 100 ? v : -1; // WHEEL_PAGESCROLL = uint.MaxValue
        }
        return _linesCache;
    }

    private void CancelLocked()
    {
        _physics.Cancel();
        _rowsLeft = 0;
    }

    // ------------------------------------------------------- animation side

    private void Loop()
    {
        bool fineTimer = false;
        while (!_stop)
        {
            bool active;
            lock (_lock) active = _physics.Active || _rowsLeft > 0;
            if (!active)
            {
                if (fineTimer) { NativeMethods.timeEndPeriod(1); fineTimer = false; }
                _wake.WaitOne(1000);
                continue;
            }
            if (!fineTimer) { NativeMethods.timeBeginPeriod(1); fineTimer = true; }

            try { Frame(); } catch { lock (_lock) CancelLocked(); }
            Thread.Sleep(6);
        }
        if (fineTimer) NativeMethods.timeEndPeriod(1);
    }

    private void Frame()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        int send = 0;
        bool row = false;
        int rowMethod = 0, rowDir = 0, rowDelta = 0;
        IntPtr rowHwnd = IntPtr.Zero;
        NativeMethods.POINT rowPt = default;

        bool ctrlOrAlt = Down(NativeMethods.VK_CONTROL) || Down(NativeMethods.VK_MENU);
        IntPtr under = RootUnderCursor();

        lock (_lock)
        {
            if (_physics.Active)
            {
                // Anything that would turn the rest of the motion into something
                // else (zoom, a horizontal scroll, another window) stops it.
                if (ctrlOrAlt || Down(NativeMethods.VK_SHIFT) != _shiftAtStart || under != _root)
                    _physics.Cancel();
                else
                    send = _physics.Step(now - _lastFrameMs, _feel);
            }
            _lastFrameMs = now;

            if (_rowsLeft > 0)
            {
                if (ctrlOrAlt || under != _rowRoot) _rowsLeft = 0;
                else if (now >= _nextRowMs)
                {
                    row = true;
                    _rowsLeft--;
                    _nextRowMs = Math.Max(_nextRowMs + _rowIntervalMs, now - _rowIntervalMs);
                    rowMethod = _rowMethod; rowDir = _rowDir; rowDelta = _rowDelta;
                    rowHwnd = _rowHwnd; rowPt = _rowPt;
                }
            }
        }

        if (send != 0) SendWheel(send);
        if (row) SendRow(rowMethod, rowDir, rowDelta, rowHwnd, rowPt);
    }

    private static IntPtr RootUnderCursor() =>
        NativeMethods.GetCursorPos(out var p)
            ? NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(p), NativeMethods.GA_ROOT)
            : IntPtr.Zero;

    private static void SendWheel(int delta)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
                    dwExtraInfo = NativeMethods.SCROLL_MARKER,
                }
            }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>One row in LibreOffice Calc. Which way Calc accepts is checked on
    /// the user's machine: 0 = a third of a click via SendInput, 1 = WM_VSCROLL,
    /// 2 = a third of a click posted straight to the window.</summary>
    private static void SendRow(int method, int dir, int delta, IntPtr hwnd, NativeMethods.POINT pt)
    {
        switch (method)
        {
            case 1:
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_VSCROLL,
                    (IntPtr)(dir > 0 ? NativeMethods.SB_LINEUP : NativeMethods.SB_LINEDOWN), IntPtr.Zero);
                break;
            case 2:
                long w = ((long)(short)(dir * delta) & 0xFFFF) << 16;
                long l = ((long)(pt.Y & 0xFFFF) << 16) | (long)(pt.X & 0xFFFF);
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_MOUSEWHEEL, (IntPtr)w, (IntPtr)l);
                break;
            default:
                SendWheel(dir * delta);
                break;
        }
    }
}

/// <summary>Which app is under the mouse, cached per process so the hook stays fast.</summary>
internal static class WheelTargets
{
    /// <param name="Coarse">The window scrolls a full step on every wheel event,
    /// whatever its size — small smooth steps would fling it to the end. WPF's
    /// ScrollViewer does this (BS's own window included).</param>
    public readonly record struct Target(IntPtr Hwnd, IntPtr Root, string ExeName, bool Game, bool Elevated, bool Calc, bool Coarse);

    private sealed record ProcInfo(string ExeName, bool Game, bool Elevated, long At);

    private static readonly Dictionary<uint, ProcInfo> Cache = new();
    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    public static Target? At(NativeMethods.POINT pt)
    {
        IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return null;
        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        NativeMethods.GetWindowThreadProcessId(root, out uint pid);
        if (pid == 0) return null;

        long now = Environment.TickCount64;
        if (!Cache.TryGetValue(pid, out var info) || now - info.At > 30_000)
        {
            if (Cache.Count > 256) Cache.Clear();
            string? path = pid == OwnPid ? Environment.ProcessPath : AppLanguageEnforcer.GetProcessPath(pid);
            bool elevated = pid != OwnPid && AppLanguageEnforcer.IsElevated(pid);
            info = new ProcInfo(path is null ? "" : Path.GetFileName(path),
                path != null && GameDetector.IsGame(path), elevated, now);
            Cache[pid] = info;
        }

        bool calc = false;
        if (string.Equals(info.ExeName, "soffice.bin", StringComparison.OrdinalIgnoreCase))
        {
            // The same LibreOffice window turns from Start Center into Calc, so read the title every time.
            var sb = new StringBuilder(256);
            NativeMethods.InternalGetWindowText(root, sb, sb.Capacity);
            calc = sb.ToString().Contains("LibreOffice Calc", StringComparison.OrdinalIgnoreCase);
        }
        // GetClassName sends no message to the app, so a hung window can't stall the hook.
        var cls = new StringBuilder(64);
        NativeMethods.GetClassName(root, cls, cls.Capacity);
        bool coarse = cls.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal);
        return new Target(hwnd, root, info.ExeName, info.Game, info.Elevated, calc, coarse);
    }
}

/// <summary>Games, where synthetic wheel input breaks weapon switching and can
/// look suspicious to anti-cheat. Two signals: the exe sits in a launcher's
/// library, or Windows Game Bar has recorded it as a game.</summary>
public static class GameDetector
{
    private static readonly string[] LibraryFolders =
    {
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\",
        @"\riot games\", @"\ubisoft game launcher\games\", @"\ea games\", @"\electronic arts\",
        @"\xboxgames\",
    };

    private static volatile HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private static long _loadedAt = long.MinValue;

    public static int KnownCount => _known.Count;

    public static bool IsGame(string exePath)
    {
        if (Environment.TickCount64 - _loadedAt > 600_000)
        {
            _loadedAt = Environment.TickCount64;
            System.Threading.Tasks.Task.Run(Refresh);
        }
        foreach (var f in LibraryFolders)
            if (exePath.Contains(f, StringComparison.OrdinalIgnoreCase)) return true;
        return _known.Contains(exePath);
    }

    /// <summary>Reads HKCU\System\GameConfigStore\Children\*\MatchedExeFullPath.</summary>
    public static void Refresh()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children != null)
                foreach (var name in children.GetSubKeyNames())
                {
                    using var k = children.OpenSubKey(name);
                    if (k?.GetValue("MatchedExeFullPath") is string p && p.Length > 0) set.Add(p);
                }
        }
        catch { }
        _known = set;
        _loadedAt = Environment.TickCount64;
    }
}
