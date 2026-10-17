using System.Drawing;
using System.Media;
using System.Windows;
using System.Windows.Forms; // NotifyIcon (tray)
using Kright.Services;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;
using Application = System.Windows.Application;

namespace Kright;

public partial class App : Application
{
    private NotifyIcon _tray = null!;
    private MainWindow? _window;
    private SparkleUpdater? _sparkle;

    // Sparkle auto-update feed (separate from macOS: Windows binaries + key differ).
    // Reachable over HTTPS by every install — works once the repo is public, else
    // host appcast-win.xml + the installer somewhere public and update this URL.
    private const string AppcastUrl =
        "https://raw.githubusercontent.com/walteryaron/Kright/main/appcast-win.xml";
    // Ed25519 PUBLIC key — paste the value printed by `netsparkle-generate-appcast
    // --generate-keys` (run once on the build machine; keep the private key safe).
    // SecurityMode.Strict means updates are REJECTED until this is set correctly.
    private const string Ed25519PublicKey = "uywCRbRonAfBNWIj9q2C6O7OtW4pBCinuktq094zyc4=";

    public static HotkeyManager Hotkey { get; private set; } = null!;
    public static FocusLanguageEnforcer Enforcer { get; private set; } = null!;
    public static AppLanguageEnforcer AppEnforcer { get; private set; } = null!;
    public static ContactLanguageEnforcer ContactEnforcer { get; private set; } = null!;
    public static PrivacyMonitor Privacy { get; private set; } = null!;
    public static AutoFixEngine AutoFix { get; private set; } = null!;
    /// <summary>Null until startup has created it; the mouse hook checks.</summary>
    public static SmoothScroller? Scroller { get; private set; }

    /// <summary>Newer release found by the last check, if any.</summary>
    public static ReleaseInfo? AvailableUpdate { get; set; }
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    private static System.Threading.Mutex? _singleInstance;
    private ToolStripMenuItem? _autoFixItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One copy only: autostart + installer shortcut + a manual launch would
        // otherwise stack keyboard hooks and fix every word twice.
        _singleInstance = new System.Threading.Mutex(true, @"Local\BisonSwitcher.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        StartupManager.MigrateLegacy();

        // Parse the dictionaries and bigram models off the UI thread now, so the
        // first word typed doesn't pay for it inside the keyboard hook.
        System.Threading.Tasks.Task.Run(() =>
        {
            WordRanks.Embedded.Warm("uk", "en");
            GibberishDetector.Supports("en");
        });

        Hotkey = new HotkeyManager();
        Hotkey.Triggered += FixFocusedLayout;

        Enforcer = new FocusLanguageEnforcer();
        Enforcer.StartIfEnabled();

        AppEnforcer = new AppLanguageEnforcer();
        AppEnforcer.Start();

        ContactEnforcer = new ContactLanguageEnforcer();
        ContactEnforcer.Start();

        Scroller = new SmoothScroller();
        Scroller.Start();

        AutoFix = new AutoFixEngine();
        AutoFix.Start();
        AutoFix.Undone += word => ShowInfoBalloon("Більше не виправлятиму",
            $"«{word}» додано в «Не виправляти». Прибрати — у розділі «Словник».");
        AppEnforcer.ForegroundChanged += AutoFix.FocusMoved;
        AutoFix.StateChanged += () => { if (_autoFixItem != null) _autoFixItem.Checked = AutoFix.Enabled; };

        SetupTray();

        // Blind mode: when a password field is focused, show a slashed-eye icon
        // and stop Kright reading the field (see FixFocusedLayout).
        Privacy = new PrivacyMonitor();
        Privacy.SensitiveChanged += OnSensitiveChanged;
        Privacy.Start();

        // Upstream's NetSparkle updater stays off (it would install the official
        // Kright). Bison Switcher checks its own GitHub releases instead.
        // SetupUpdater();
        SetupUpdateChecks();
    }

    // ---- Update checks (GitHub Releases) ----

    private void SetupUpdateChecks()
    {
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(1);
            var s = AppSettings.Current;
            if (!s.AutoCheckUpdates) return;
            if (s.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
            await CheckForUpdatesAsync(notify: true);
        };
        _updateTimer.Start();
    }

    /// <summary>Checks GitHub; with <paramref name="notify"/> a newer version is
    /// announced once in the tray. Returns the release, or null if the check failed.</summary>
    public static async Task<ReleaseInfo?> CheckForUpdatesAsync(bool notify)
    {
        var r = await UpdateService.GetLatestAsync();
        if (r is null) return null;
        AppSettings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
        AvailableUpdate = UpdateService.IsNewer(r) && r.SetupUrl != null ? r : null;
        if (notify && AvailableUpdate is { } u && AppSettings.Current.LastNotifiedVersion != u.Tag)
        {
            AppSettings.Current.LastNotifiedVersion = u.Tag;
            ((App)Current).ShowUpdateBalloon(u);
        }
        AppSettings.Save();
        return r;
    }

    private void ShowInfoBalloon(string title, string text)
    {
        if (_tray is null) return;
        _balloonIsUpdate = false;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = ToolTipIcon.None;
        _tray.ShowBalloonTip(4000);
    }

    private bool _balloonIsUpdate;

    /// <summary>Restart BS: through the admin task, or as a normal process. The
    /// single-instance mutex is released first so the new copy can start.</summary>
    public static void Restart(bool elevated)
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
        bool started = elevated ? StartupManager.RunElevatedTask() : StartupManager.StartUnelevated();
        if (started) Current.Shutdown();
    }

    private void ShowUpdateBalloon(ReleaseInfo u)
    {
        _balloonIsUpdate = true;
        if (_tray is null) return;
        _tray.BalloonTipTitle = "Доступна нова версія Bison Switcher";
        _tray.BalloonTipText = $"Версія {u.Version.ToString(3)} вже на GitHub. Натисніть, щоб оновити.";
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(8000);
    }

    // ---- Auto-update (NetSparkle) ----

    private void SetupUpdater()
    {
        _sparkle = new SparkleUpdater(
            AppcastUrl,
            new Ed25519Checker(SecurityMode.Strict, Ed25519PublicKey))
        {
            UIFactory = new NetSparkleUpdater.UI.WPF.UIFactory(),
            RelaunchAfterUpdate = true,
        };

        // First run: ask whether to check for updates automatically — parity with
        // Sparkle's first-run prompt on macOS, so the user consents to the only
        // network call this otherwise-offline app makes. The choice is persisted;
        // the tray's "Check for Updates…" item works regardless of the answer.
        if (AppSettings.Current.AutoUpdateCheck is null)
        {
            // Fully-qualified: System.Windows.Forms is also imported (tray) and
            // also has a MessageBox, so the bare name is ambiguous.
            var answer = System.Windows.MessageBox.Show(
                "Should Kright automatically check for updates? You can always " +
                "check for updates manually from the tray menu.",
                "Check for updates automatically?",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            AppSettings.Current.AutoUpdateCheck = answer == MessageBoxResult.Yes;
            AppSettings.Save();
        }

        // Scheduled background checks (true = also check once at launch) — only if
        // the user opted in.
        if (AppSettings.Current.AutoUpdateCheck == true)
            _ = _sparkle.StartLoop(true);
    }

    private void OnSensitiveChanged(bool sensitive)
    {
        _tray.Icon = sensitive ? TrayIcons.Blind : TrayIcons.Normal;
        _tray.Text = sensitive ? "Bison Switcher — поле пароля, не читаю" : "Bison Switcher";
    }

    // ---- Tray ----

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        _autoFixItem = new ToolStripMenuItem("Автовиправлення")
        {
            Checked = AutoFix.Enabled,
            CheckOnClick = true,
        };
        _autoFixItem.Click += (_, _) => AutoFix.Enabled = _autoFixItem.Checked;
        menu.Items.Add(_autoFixItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Налаштування", null, (_, _) => ShowWindow(tab: 1));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Вийти з Bison Switcher", null, (_, _) => Shutdown());

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Normal,
            Visible = true,
            Text = "Bison Switcher",
            ContextMenuStrip = menu
        };
        _tray.BalloonTipClicked += (_, _) => ShowWindow(tab: _balloonIsUpdate ? 2 : 3);
        _tray.MouseClick += (_, ev) =>
        {
            if (ev.Button == MouseButtons.Left) ToggleWindow();
        };
    }

    private void ToggleWindow()
    {
        if (_window is { IsVisible: true }) _window.Hide();
        else ShowWindow();
    }

    private void ShowWindow(int tab = 0)
    {
        _window ??= new MainWindow();
        _window.SelectTab(tab);
        _window.Show();
        _window.Activate();
    }

    // ---- The core action: fix the focused field's wrong-layout word ----

    public static void FixFocusedLayout()
    {
        // Right after an auto-fix the hotkey means "no, put it back".
        if (AutoFix.TryUndoLast()) return;

        var field = FocusInspector.Focused();
        if (field is null) { SystemSounds.Beep.Play(); return; }

        // Blind mode: never read or touch a password field.
        if (field.IsPassword) { SystemSounds.Beep.Play(); return; }

        // Convert the whole just-typed phrase (all words, e.g. "nrhu dktexh"),
        // falling back to the last word for long values (a console/document
        // buffer we shouldn't rewrite wholesale).
        var s = LayoutConverter.SuggestPhrase(field.Value) ?? LayoutConverter.Suggest(field.Value);
        if (s is null || !s.IsMeaningful) { SystemSounds.Beep.Play(); return; }

        // After fixing, switch the keyboard to the corrected text's language so the
        // user keeps typing in the right layout instead of more gibberish.
        if (s.ToHkl != IntPtr.Zero) LanguageManager.Switch(s.ToHkl);

        // The text under the caret changed behind auto-fix's back.
        AutoFix.Reset();
        UserWords.LearnFromHotkey(s.Original, s.Converted);

        if (FocusInspector.TrySetFocusedValue(s.FullReplacement)) return;

        // Read-only (console/terminal) → keystroke fallback on a background thread.
        System.Threading.Tasks.Task.Run(() =>
            KeystrokeReplacer.ReplaceLastWord(s.Original.Length, s.Converted));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Null-safe: a second instance shuts down before anything is created.
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        AutoFix?.Dispose();
        Scroller?.Dispose();
        Hotkey?.Dispose();
        AppEnforcer?.Dispose();
        ContactEnforcer?.Dispose();
        _sparkle?.Dispose();
        base.OnExit(e);
    }
}
