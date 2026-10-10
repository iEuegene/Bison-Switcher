using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kright.Native;
using Kright.Services;
using InputLanguage = Kright.Services.InputLanguage;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;

namespace Kright;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _recording;
    private bool _loading = true;
    private LayoutSuggestion? _suggestion;
    private DispatcherTimer? _statusTimer, _contactStatusTimer, _excludeStatusTimer;
    private readonly Dictionary<RadioButton, FrameworkElement> _pages;

    private static readonly SolidColorBrush Lime = new(Color.FromRgb(0xF0, 0xFC, 0x7E));
    private static readonly SolidColorBrush Orange = new(Color.FromRgb(0xEF, 0x58, 0x3D));
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x94, 0xAD, 0xA3));
    private static readonly SolidColorBrush Faint = new(Color.FromRgb(0x5E, 0x7D, 0x71));
    private static readonly SolidColorBrush TextWhite = new(Color.FromRgb(0xF7, 0xF7, 0xF7));
    private static readonly SolidColorBrush ChipBg = new(Color.FromRgb(0x0B, 0x2A, 0x20));
    private static readonly SolidColorBrush ChipLine = new(Color.FromRgb(0x2E, 0x5E, 0x4C));

    public MainWindow()
    {
        InitializeComponent();

        _pages = new Dictionary<RadioButton, FrameworkElement>
        {
            [NavOverview] = PageOverview,
            [NavAutoFix] = PageAutoFix,
            [NavDict] = PageDict,
            [NavApps] = PageApps,
            [NavLayout] = PageLayout,
            [NavSystem] = PageSystem,
            [NavDetect] = PageDetect,
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => RefreshLive();
        IsVisibleChanged += (_, _) => { if (IsVisible) { RefreshLive(); _timer.Start(); } else _timer.Stop(); };

        try { Icon = ToImageSource(TrayIcons.Normal); } catch { }

        HotkeyText.Text = App.Hotkey.DisplayString;
        AutoLangCheck.IsChecked = App.Enforcer.Enabled;
        AddressBarCheck.IsChecked = App.Enforcer.AddressBarEnabled;

        UndoCheck.IsChecked = AppSettings.Current.AutoFixUndoOnBackspace;
        CapitalizeCheck.IsChecked = AppSettings.Current.AutoCapitalize;
        SpellCheck.IsChecked = AppSettings.Current.SpellCheckEnabled;
        RefreshSpellStatus();
        LearnCheck.IsChecked = AppSettings.Current.AutoFixLearnFromUndo && AppSettings.Current.AutoFixLearnFromHotkey;
        (AppSettings.Current.AutoFixSensitivity switch { 0 => SensCareful, 2 => SensBold, _ => SensNormal }).IsChecked = true;
        SyncAutoFixToggles();
        App.AutoFix.StateChanged += () =>
        {
            if (!IsVisible) return; // refreshed on activation instead
            SyncAutoFixToggles();
            RefreshWordLists();
        };

        RefreshExcludedList();
        RefreshWordLists();
        StartupCheck.IsChecked = StartupManager.IsEnabled;
        AutoUpdateCheck.IsChecked = AppSettings.Current.AutoCheckUpdates;
        UpdateCurrentText.Text = $"Встановлена версія {UpdateService.Current.ToString(3)}.";
        if (App.AvailableUpdate is { } pending) ShowUpdateAvailable(pending);

        DebugCheck.IsChecked = AppSettings.Current.DebugMode;
        ApplyDebugMode(AppSettings.Current.DebugMode);

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Bison Switcher (BS) {v?.ToString(3) ?? "1.0.0"}";

        AppLangCheck.IsChecked = App.AppEnforcer.Enabled;
        FillLayoutChips(AddLayoutChips, "addApp");
        RefreshRulesList();

        ContactLangCheck.IsChecked = App.ContactEnforcer.Enabled;
        FillLayoutChips(ContactLayoutChips, "addContact");
        RefreshContactRulesList();

        RefreshLayoutMap();
        _loading = false;
    }

    // ---- Window chrome: brand-green title bar on Windows 11 ----

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));        // immersive dark mode
            int caption = 0x00171F07;                                      // COLORREF of #071F17 (0x00BBGGRR)
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));     // caption colour (Win11)
            int text = 0x00F7F7F7;
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));        // caption text colour
            int border = 0x003B4A1F;                                       // #1F4A3B as BGR
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));      // border colour
        }
        catch { /* older Windows: default chrome */ }
    }

    private static ImageSource ToImageSource(System.Drawing.Icon icon) =>
        Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateAddButtonLabel();
        UpdateAddContactButtonLabel();
        UpdateExcludeButtonLabel();
        SyncAutoFixToggles();
        RefreshWordLists();
        RefreshLive();
    }

    // Keep the app alive (tray) when the window is closed.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    // ---- Navigation ----

    /// <summary>0 = overview, 1 = settings (auto-fix page), kept for App's tray calls.</summary>
    public void SelectTab(int index) => (index switch
    {
        0 => NavOverview,
        2 => NavSystem,   // updates live on the System page
        _ => NavAutoFix,
    }).IsChecked = true;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_pages is null || sender is not RadioButton rb) return;
        foreach (var (nav, page) in _pages)
            page.Visibility = nav == rb ? Visibility.Visible : Visibility.Collapsed;
        RefreshLive();
    }

    private void GoTo_Click(object sender, RoutedEventArgs e)
    {
        string? tag = (sender as FrameworkElement)?.Tag as string;
        var target = tag switch
        {
            "apps" => NavApps,
            "dict" => NavDict,
            _ => NavOverview,
        };
        target.IsChecked = true;
    }

    private void ApplyDebugMode(bool on)
    {
        NavDetect.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on && NavDetect.IsChecked == true) NavSystem.IsChecked = true;
    }

    // ---- Live data (overview + diagnostics) ----

    private void RefreshLive()
    {
        if (!IsVisible) return;
        RefreshOverview();
        if (PageDetect.Visibility == Visibility.Visible) RefreshDetect();
    }

    private void RefreshOverview()
    {
        var fx = App.AutoFix;
        bool on = fx.Enabled;
        OvHeadline.Text = on ? "Розкладка під контролем." : "Автовиправлення на паузі.";
        OvSub.Text = on
            ? "Пишіть як звикли — промахи розкладки виправляються на пробілі."
            : "Увімкніть перемикач, щоб слова, набрані не в тій розкладці, знову виправлялися.";
        OvAutoState.Text = on ? "Увімк." : "Пауза";
        OvAutoState.Foreground = on ? TextWhite : Orange;

        OvFixed.Text = fx.FixCount.ToString("N0");
        OvFixedSub.Text = fx.UndoCount > 0
            ? $"слів за цей запуск · скасовано {fx.UndoCount}"
            : "слів за цей запуск";
        OvLast.Text = fx.LastFixText ?? "";
        OvLastLabel.Visibility = fx.LastFixText is null ? Visibility.Collapsed : Visibility.Visible;

        int uk = WordRanks.Embedded.Count("uk"), en = WordRanks.Embedded.Count("en");
        OvDict.Text = $"{(uk + en) / 1000:N0} тис.";
        OvDictSub.Text = $"словоформ · UA {uk / 1000:N0} тис. · EN {en / 1000:N0} тис.";

        OvExcluded.Text = AppSettings.Current.AutoFixExcludedApps.Count.ToString();
        int never = AppSettings.Current.NeverConvert.Count, always = AppSettings.Current.AlwaysConvert.Count;
        OvWords.Text = (never + always).ToString();
        OvWordsSub.Text = $"не виправляти: {never} · нових слів: {always}";
    }

    // ---- Auto-fix toggles (sidebar, overview card, settings page) ----

    private void SyncAutoFixToggles()
    {
        bool on = App.AutoFix.Enabled;
        AutoFixCheck.IsChecked = on;
        SideToggle.IsChecked = on;
        OvToggle.IsChecked = on;
        SideState.Text = on ? "Увімкнено" : "Пауза";
        SideState.Foreground = on ? Muted : Orange;
    }

    private void SetAutoFix(bool on)
    {
        App.AutoFix.Enabled = on;
        SyncAutoFixToggles();
        RefreshOverview();
    }

    private void AutoFixCheck_Click(object sender, RoutedEventArgs e) => SetAutoFix(AutoFixCheck.IsChecked == true);
    private void SideToggle_Click(object sender, RoutedEventArgs e) => SetAutoFix(SideToggle.IsChecked == true);
    private void OvToggle_Click(object sender, RoutedEventArgs e) => SetAutoFix(OvToggle.IsChecked == true);

    private void SpellCheck_Click(object sender, RoutedEventArgs e)
    {
        bool on = SpellCheck.IsChecked == true;
        AppSettings.Current.SpellCheckEnabled = on;
        AppSettings.Save();
        if (on) App.AutoFix.RequestSpellWarmup();
        RefreshSpellStatus();
    }

    /// <summary>Which Windows spell-check dictionaries are installed.</summary>
    private void RefreshSpellStatus()
    {
        bool uk, en;
        try
        {
            var probe = new SpellService();
            uk = probe.IsAvailable("uk");
            en = probe.IsAvailable("en");
        }
        catch { uk = en = false; }

        string Mark(bool ok) => ok ? "доступна" : "не встановлена";
        SpellStatus.Text = $"Перевірка правопису Windows — українська: {Mark(uk)} · англійська: {Mark(en)}";
        SpellStatus.Foreground = uk && en ? Lime : Orange;
        SpellHelp.Visibility = uk && en ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenLanguageSettings_Click(object sender, RoutedEventArgs e)
        => UpdateService.OpenUrl("ms-settings:regionlanguage");

    private void CapitalizeCheck_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AutoCapitalize = CapitalizeCheck.IsChecked == true;
        AppSettings.Save();
    }

    private void UndoCheck_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AutoFixUndoOnBackspace = UndoCheck.IsChecked == true;
        AppSettings.Save();
    }

    private void LearnCheck_Click(object sender, RoutedEventArgs e)
    {
        bool on = LearnCheck.IsChecked == true;
        AppSettings.Current.AutoFixLearnFromUndo = on;
        AppSettings.Current.AutoFixLearnFromHotkey = on;
        AppSettings.Save();
    }

    private void Sens_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int level)) return;
        AppSettings.Current.AutoFixSensitivity = level;
        AppSettings.Save();
    }

    // ---- Diagnostics ----

    private void RefreshLayoutMap()
    {
        var langs = LanguageManager.Enabled();
        var latin = langs.FirstOrDefault(l => LanguageManager.IsLatin(l.Hkl));
        var other = langs.FirstOrDefault(l => !LanguageManager.IsLatin(l.Hkl));
        if (latin == null || other == null)
        {
            LayoutMapText.Text = "Потрібні дві розкладки: латинська і кирилична.";
            return;
        }

        var latinFwd = KeyboardLayoutMap.ForwardMap(latin.Hkl);
        var otherFwd = KeyboardLayoutMap.ForwardMap(other.Hkl);

        var sb = new StringBuilder();
        sb.AppendLine($"{latin.Name}  ↔  {other.Name}");
        (uint vk, string key)[][] letterRows =
        [
            [(0x51,"Q"),(0x57,"W"),(0x45,"E"),(0x52,"R"),(0x54,"T"),
             (0x59,"Y"),(0x55,"U"),(0x49,"I"),(0x4F,"O"),(0x50,"P")],
            [(0x41,"A"),(0x53,"S"),(0x44,"D"),(0x46,"F"),(0x47,"G"),
             (0x48,"H"),(0x4A,"J"),(0x4B,"K"),(0x4C,"L")],
            [(0x5A,"Z"),(0x58,"X"),(0x43,"C"),(0x56,"V"),(0x42,"B"),
             (0x4E,"N"),(0x4D,"M")],
        ];
        foreach (var row in letterRows)
        {
            foreach (var (vk, name) in row)
                sb.Append($"{name}:{(otherFwd.TryGetValue(vk, out var o) ? o : "?")}  ");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine($"Punctuation  (KEY: {latin.Name}→{other.Name})");
        (uint vk, string key)[] punctKeys =
            [(0xBA, ";"), (0xDE, "'"), (0xBC, ","), (0xBE, "."), (0xBF, "/"), (0xC0, "`")];
        foreach (var (vk, name) in punctKeys)
        {
            var lc = latinFwd.TryGetValue(vk, out var lv) ? lv : "?";
            var oc = otherFwd.TryGetValue(vk, out var ov) ? ov : "?";
            sb.Append($"{name}: {lc}→{oc}   ");
        }
        LayoutMapText.Text = sb.ToString();
    }

    private void RefreshDetect()
    {
        ShowLastWord(App.AutoFix.LastWord);
        AutoFixDecisionText.Text =
            $"Словники: UA {WordRanks.Embedded.Count("uk"):N0} · EN {WordRanks.Embedded.Count("en"):N0} словоформ";

        var field = FocusInspector.Focused();
        if (field is null)
        {
            AppLabel.Text = "Поле не в фокусі";
            return;
        }
        AppLabel.Text = string.IsNullOrWhiteSpace(field.Name) ? field.ControlType : field.Name;
        GuessText.Text = FieldKind(field.Guess);

        var langs = LanguageManager.Enabled();
        LanguagesText.Text = string.Join("   ", langs.Select(l => l.IsCurrent ? $"[{l.Name}]" : l.Name));

        _suggestion = LayoutConverter.Suggest(field.Value);
        if (_suggestion is { IsMeaningful: true } s)
        {
            TypedText.Text = $"Набрано: {s.Original}   ({s.FromLayout} → {s.ToLayout})";
            ConvertedText.Text = s.Converted;
            ReplaceButton.IsEnabled = true;
            var g = GibberishDetector.LooksWrongLayout(s.Original, s.Converted, s.FromLang, s.ToLang);
            GibberishText.Text = g.Wrong
                ? $"Схоже на помилку розкладки · {(int)(g.Confidence * 100)}%"
                : "Схоже на навмисний текст";
            GibberishText.Foreground = g.Wrong ? Lime : Faint;
        }
        else
        {
            TypedText.Text = "Наберіть у полі 3+ літери, щоб побачити варіант.";
            ConvertedText.Text = "";
            GibberishText.Text = "";
            ReplaceButton.IsEnabled = false;
        }

        if (App.Enforcer.LastAction is { } a) AutoLangStatus.Text = a;
    }

    private void ShowLastWord(AutoFixEngine.WordInfo? w)
    {
        if (w is null)
        {
            DiagWord.Text = "—";
            DiagVerdict.Text = "Наберіть будь-яке слово й натисніть пробіл.";
            DiagVerdict.Foreground = Muted;
            DiagReason.Text = "";
            return;
        }
        switch (w.Reason)
        {
            case "app-excluded":
                DiagWord.Text = w.Typed;
                DiagVerdict.Text = "Не перевіряється";
                DiagVerdict.Foreground = Muted;
                DiagReason.Text = "Ця програма у виключеннях (сторінка «Програми»).";
                return;
            case "spell":
                DiagWord.Text = $"{w.Typed} → {w.Alt}";
                DiagVerdict.Text = $"Виправлено описку: «{w.Alt}»";
                DiagVerdict.Foreground = Lime;
                DiagReason.Text = "Перевірка правопису Windows: слово відрізнялося від правильного на одну літеру.";
                return;
            case "elevated":
                DiagWord.Text = w.Typed;
                DiagVerdict.Text = "Не виправляється: програму запущено від адміністратора";
                DiagVerdict.Foreground = Orange;
                DiagReason.Text = "Windows не дозволяє звичайній програмі вводити текст у вікна адміністратора. "
                                + "Запустіть цю програму без прав адміністратора або Bison Switcher від адміністратора.";
                return;
            case "undo":
                DiagWord.Text = $"{w.Typed} → {w.Alt}";
                DiagVerdict.Text = "Скасовано вами (Backspace)";
                DiagVerdict.Foreground = Orange;
                DiagReason.Text = "Слово додано в «Не виправляти», якщо ввімкнено навчання.";
                return;
        }
        DiagWord.Text = $"{w.Typed} → {w.Alt}";
        DiagVerdict.Text = w.Fixed ? $"Виправлено на «{w.Alt}»" : $"Залишено як є: «{w.Typed}»";
        DiagVerdict.Foreground = w.Fixed ? Lime : TextWhite;
        DiagReason.Text = ReasonText(w.Reason, w.Fixed);
    }

    private static string ReasonText(string reason, bool fixedIt) => reason switch
    {
        "dict" => "Набране не є словом, а варіант в іншій розкладці є у словнику.",
        "dict-more-common" => "Обидва варіанти — короткі слова, але варіант в іншій мові набагато вживаніший.",
        "dict-rare-typed" => "Набране — рідкісне сполучення, а варіант в іншій розкладці — поширене слово.",
        "always-list" => "Слово є у вашому списку «Навчити нових слів».",
        "junk-in-typed" => "Посеред набраного є розділові знаки — так не пишуть жодне слово.",
        "detector" => fixedIt
            ? "Слова немає у словнику, але воно схоже на слово іншої мови."
            : "Слова немає у словнику, і воно не схоже на помилку розкладки.",
        "never-list" => "Слово є у вашому списку «Не виправляти».",
        "typed-is-word" => "Набране — справжнє слово, тому програма його не чіпає.",
        "both-words" => "Слово існує в обох мовах — програма не вгадує, що ви мали на увазі.",
        "typed-looks-native" => "Набране схоже на справжнє слово, хоч його й немає у словнику.",
        "short" or "short-unknown" => "Закоротке слово, щоб упевнено визначити мову.",
        "abbreviation" => "Усі літери великі — схоже на абревіатуру.",
        "mixed-case" => "Велика літера посеред слова — схоже на назву (як iPhone).",
        "no-model" => "Для цієї мови немає словника.",
        "junk-but-target-weak" or "alt-not-word" or "alt-leading" or "no-letters" or "length"
            => "Варіант в іншій розкладці не схожий на слово.",
        _ => reason,
    };

    private static string FieldKind(string guess) => guess switch
    {
        "Password" => "Поле пароля — автовиправлення вимкнене",
        FocusedField.AddressBarKind => "Адресний рядок браузера",
        "Search" => "Поле пошуку",
        "Email" => "Поле email",
        "URL" => "Поле посилання (URL)",
        "Phone number" => "Поле телефону",
        "Payment" => "Платіжне поле",
        "Address" => "Поле адреси",
        "Name" => "Поле імені",
        "Generic text field" => "Звичайне текстове поле",
        "Not a text field" => "Не текстове поле",
        _ => guess,
    };

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suggestion is not { } s) return;
        bool ok = FocusInspector.TrySetFocusedValue(s.FullReplacement);
        ReplaceResult.Text = ok ? "Замінено ✓" : "Поле лише для читання — скористайтеся хоткеєм.";
        ReplaceResult.Foreground = ok ? Lime : Orange;
    }

    // ---- Hotkey ----

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        _recording = !_recording;
        RecordButton.Content = _recording ? "Натисніть комбінацію… (Esc — скасувати)" : "Змінити…";
        HotkeyText.Text = _recording ? "…" : App.Hotkey.DisplayString;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (!_recording) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;

        // With Alt held the key arrives as Key.System; the real key is in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None) return;

        uint fs = 0;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) fs |= NativeMethods.MOD_CONTROL;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) fs |= NativeMethods.MOD_ALT;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) fs |= NativeMethods.MOD_SHIFT;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) fs |= NativeMethods.MOD_WIN;
        if (fs == 0) return; // require a modifier

        App.Hotkey.Update(fs, (uint)KeyInterop.VirtualKeyFromKey(key));
        StopRecording();
    }

    private void StopRecording()
    {
        _recording = false;
        RecordButton.Content = "Змінити…";
        HotkeyText.Text = App.Hotkey.DisplayString;
    }

    // ---- Updates ----

    private ReleaseInfo? _release;

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButton.Content = "Перевіряю…";
        UpdateActions.Visibility = Visibility.Collapsed;
        try
        {
            var r = await App.CheckForUpdatesAsync(notify: false);
            if (r is null)
                ShowUpdateStatus("Не вдалося зв'язатися з GitHub. Перевірте інтернет і спробуйте ще раз.", Orange);
            else if (UpdateService.IsNewer(r))
                ShowUpdateAvailable(r);
            else
                ShowUpdateStatus($"У вас остання версія BS ({UpdateService.Current.ToString(3)}).", Lime);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            CheckUpdatesButton.Content = "Перевірити оновлення";
        }
    }

    private void ShowUpdateStatus(string text, System.Windows.Media.Brush color)
    {
        UpdateStatus.Text = text;
        UpdateStatus.Foreground = color;
        UpdateStatus.Visibility = Visibility.Visible;
    }

    private void ShowUpdateAvailable(ReleaseInfo r)
    {
        _release = r;
        ShowUpdateStatus($"Доступна свіжа версія BS — {r.Version.ToString(3)}.", Lime);
        UpdateActions.Visibility = Visibility.Visible;
        InstallUpdateButton.IsEnabled = r.SetupUrl != null;
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
        => UpdateService.OpenUrl(_release?.PageUrl ?? UpdateService.ReleasesPage);

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null) return;
        InstallUpdateButton.IsEnabled = false;
        var progress = new Progress<DownloadProgress>(p => InstallUpdateButton.Content = p.Total > 0
            ? $"{p.Done * 100 / p.Total}% · {p.BytesPerSecond / 1048576:0.0} МБ/с"
            : $"Завантаження… {p.Done / 1048576} МБ");
        string? error = await UpdateService.DownloadAndInstallAsync(_release, progress);
        if (error is null)
        {
            InstallUpdateButton.Content = "Встановлюю…";
            ShowUpdateStatus("Інсталятор запущено: програма закриється й відкриється вже оновленою.", Lime);
        }
        else
        {
            InstallUpdateButton.Content = "Скачати та встановити";
            InstallUpdateButton.IsEnabled = true;
            ShowUpdateStatus(error, Orange);
        }
    }

    private void AutoUpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AutoCheckUpdates = AutoUpdateCheck.IsChecked == true;
        AppSettings.Save();
    }

    // ---- Auto English ----

    private void AutoLangCheck_Click(object sender, RoutedEventArgs e)
        => App.Enforcer.Enabled = AutoLangCheck.IsChecked == true;

    private void AddressBarCheck_Click(object sender, RoutedEventArgs e)
        => App.Enforcer.AddressBarEnabled = AddressBarCheck.IsChecked == true;

    // ---- System ----

    private void StartupCheck_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartupCheck.IsChecked == true;
        if (!StartupManager.Set(want)) StartupCheck.IsChecked = !want;
    }

    private void DebugCheck_Click(object sender, RoutedEventArgs e)
    {
        bool on = DebugCheck.IsChecked == true;
        AppSettings.Current.DebugMode = on;
        AppSettings.Save();
        ApplyDebugMode(on);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
        => System.Windows.Application.Current.Shutdown();

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ---- Layout chips (replace ComboBoxes: two layouts fit as a segmented choice) ----

    private RadioButton Chip(string text, object? tag, string group) => new()
    {
        Content = text,
        Tag = tag,
        GroupName = group,
        Style = (Style)FindResource("Chip"),
    };

    private void FillLayoutChips(StackPanel panel, string group)
    {
        panel.Children.Clear();
        bool first = true;
        foreach (var lang in LanguageManager.Enabled())
        {
            var c = Chip(lang.Name, lang, group);
            c.IsChecked = first;
            first = false;
            panel.Children.Add(c);
        }
    }

    private static InputLanguage? SelectedLayout(StackPanel panel) =>
        panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as InputLanguage;

    /// <summary>Per-row chips bound to a rule's HKL; <paramref name="save"/> stores the pick.</summary>
    private StackPanel RowChips(long hkl, Action<InputLanguage> save)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string group = "row" + Guid.NewGuid().ToString("N");
        foreach (var lang in LanguageManager.Enabled())
        {
            var c = Chip(lang.Name, lang, group);
            c.IsChecked = (long)lang.Hkl == hkl;
            c.Checked += (_, _) => save(lang);
            panel.Children.Add(c);
        }
        return panel;
    }

    private Border ListRow(UIElement? icon, string title, string? subtitle, UIElement? right, Action onDelete)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (icon != null)
        {
            if (icon is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(subtitle))
            text.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 11, Foreground = Faint, TextTrimming = TextTrimming.CharacterEllipsis
            });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (right != null)
        {
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        var del = new Button { Content = "", Style = (Style)FindResource("IconBtn"), ToolTip = "Видалити" };
        del.Click += (_, _) => onDelete();
        Grid.SetColumn(del, 3);
        grid.Children.Add(del);

        return new Border
        {
            Background = ChipBg,
            BorderBrush = ChipLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 6, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private static UIElement? AppIcon(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var src = LoadAppIcon(exePath);
        return src is null ? null : new System.Windows.Controls.Image
        {
            Source = src, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static BitmapSource? LoadAppIcon(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch { return null; }
    }

    private static void Flash(TextBlock target, ref DispatcherTimer? timer, string msg)
    {
        target.Text = msg;
        target.Visibility = Visibility.Visible;
        timer?.Stop();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) => { t.Stop(); target.Visibility = Visibility.Collapsed; };
        t.Start();
        timer = t;
    }

    // ---- Per-app layout rules ----

    private void AppLangCheck_Click(object sender, RoutedEventArgs e)
        => App.AppEnforcer.Enabled = AppLangCheck.IsChecked == true;

    private void AddAppButton_Click(object sender, RoutedEventArgs e)
    {
        var exePath = App.AppEnforcer.LastExternalExePath;
        var appName = App.AppEnforcer.LastExternalAppName;
        if (exePath == null || appName == null)
        {
            Flash(AddAppStatus, ref _statusTimer, "Спершу перейдіть у потрібну програму, потім натисніть «Додати».");
            return;
        }
        if (AppSettings.Current.AppLanguageRules.Any(r =>
            string.Equals(r.ExePath, exePath, StringComparison.OrdinalIgnoreCase)))
        {
            Flash(AddAppStatus, ref _statusTimer, $"{appName} вже в списку.");
            return;
        }
        if (SelectedLayout(AddLayoutChips) is not { } lang)
        {
            Flash(AddAppStatus, ref _statusTimer, "Спершу виберіть розкладку.");
            return;
        }
        AppSettings.Current.AppLanguageRules.Add(new AppLanguageRule
        {
            ExePath = exePath, AppName = appName, HklValue = (long)lang.Hkl, LayoutName = lang.Name
        });
        AppSettings.Save();
        RefreshRulesList();
        Flash(AddAppStatus, ref _statusTimer, $"Додано: {appName}.");
    }

    private void RefreshRulesList()
    {
        RulesPanel.Children.Clear();
        foreach (var rule in AppSettings.Current.AppLanguageRules.ToList())
        {
            var chips = RowChips(rule.HklValue, l =>
            {
                rule.HklValue = (long)l.Hkl;
                rule.LayoutName = l.Name;
                AppSettings.Save();
            });
            RulesPanel.Children.Add(ListRow(AppIcon(rule.ExePath), rule.AppName, null, chips, () =>
            {
                AppSettings.Current.AppLanguageRules.Remove(rule);
                AppSettings.Save();
                RefreshRulesList();
            }));
        }
        UpdateAddButtonLabel();
    }

    private void UpdateAddButtonLabel()
    {
        AddAppButton.Content = App.AppEnforcer.LastExternalAppName is { } n
            ? $"+ Додати «{n}»"
            : "+ Додати поточну програму";
    }

    // ---- Per-contact (Teams) rules ----

    private void ContactLangCheck_Click(object sender, RoutedEventArgs e)
        => App.ContactEnforcer.Enabled = ContactLangCheck.IsChecked == true;

    private void AddContactButton_Click(object sender, RoutedEventArgs e)
    {
        var app = App.ContactEnforcer.LastContactApp;
        var contact = App.ContactEnforcer.LastContactName;
        if (app == null || string.IsNullOrEmpty(contact))
        {
            Flash(AddContactStatus, ref _contactStatusTimer, "Спершу відкрийте чат у Teams, потім натисніть «Додати».");
            return;
        }
        if (AppSettings.Current.ContactLanguageRules.Any(r => r.App == app.Value && r.ContactName == contact))
        {
            Flash(AddContactStatus, ref _contactStatusTimer, $"{contact} вже в списку.");
            return;
        }
        if (SelectedLayout(ContactLayoutChips) is not { } lang)
        {
            Flash(AddContactStatus, ref _contactStatusTimer, "Спершу виберіть розкладку.");
            return;
        }
        AppSettings.Current.ContactLanguageRules.Add(new ContactLanguageRule
        {
            App = app.Value, ContactName = contact, HklValue = (long)lang.Hkl, LayoutName = lang.Name
        });
        AppSettings.Save();
        RefreshContactRulesList();
        Flash(AddContactStatus, ref _contactStatusTimer, $"Додано: {contact}.");
    }

    private void RefreshContactRulesList()
    {
        ContactRulesPanel.Children.Clear();
        foreach (var rule in AppSettings.Current.ContactLanguageRules.ToList())
        {
            var chips = RowChips(rule.HklValue, l =>
            {
                rule.HklValue = (long)l.Hkl;
                rule.LayoutName = l.Name;
                AppSettings.Save();
            });
            ContactRulesPanel.Children.Add(ListRow(null, rule.ContactName, rule.App.DisplayName(), chips, () =>
            {
                AppSettings.Current.ContactLanguageRules.Remove(rule);
                AppSettings.Save();
                RefreshContactRulesList();
            }));
        }
        UpdateAddContactButtonLabel();
    }

    private void UpdateAddContactButtonLabel()
    {
        AddContactButton.Content = App.ContactEnforcer.LastContactName is { } n
            ? $"+ Додати «{n}»"
            : "+ Додати поточний чат Teams";
    }

    // ---- Excluded apps ----

    private void UpdateExcludeButtonLabel()
    {
        ExcludeAppButton.Content = App.AppEnforcer.LastExternalAppName is { } n
            ? $"+ Виключити «{n}»"
            : "+ Виключити поточну програму";
    }

    private void ExcludeAppButton_Click(object sender, RoutedEventArgs e)
    {
        var exePath = App.AppEnforcer.LastExternalExePath;
        var appName = App.AppEnforcer.LastExternalAppName;
        if (exePath == null || appName == null)
        {
            Flash(ExcludeStatus, ref _excludeStatusTimer, "Спершу перейдіть у потрібну програму, потім натисніть кнопку.");
            return;
        }
        string exeName = System.IO.Path.GetFileName(exePath);
        var list = AppSettings.Current.AutoFixExcludedApps;
        if (list.Any(a => string.Equals(a.ExeName, exeName, StringComparison.OrdinalIgnoreCase)))
        {
            Flash(ExcludeStatus, ref _excludeStatusTimer, $"{appName} вже виключено.");
            return;
        }
        list.Add(new ExcludedApp { ExeName = exeName, AppName = appName, ExePath = exePath });
        AppSettings.Save();
        App.AutoFix.InvalidateLists();
        RefreshExcludedList();
        Flash(ExcludeStatus, ref _excludeStatusTimer, $"Виключено: {appName}.");
    }

    private void RefreshExcludedList()
    {
        ExcludedPanel.Children.Clear();
        foreach (var app in AppSettings.Current.AutoFixExcludedApps.ToList())
        {
            ExcludedPanel.Children.Add(ListRow(AppIcon(app.ExePath), app.AppName, app.ExeName, null, () =>
            {
                AppSettings.Current.AutoFixExcludedApps.Remove(app);
                AppSettings.Save();
                App.AutoFix.InvalidateLists();
                RefreshExcludedList();
            }));
        }
        UpdateExcludeButtonLabel();
    }

    // ---- Personal dictionary ----

    private void AddNever_Click(object sender, RoutedEventArgs e) => AddFromBox(NeverBox, never: true);
    private void AddAlways_Click(object sender, RoutedEventArgs e) => AddFromBox(AlwaysBox, never: false);

    private void NeverBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddFromBox(NeverBox, never: true); e.Handled = true; }
    }

    private void AlwaysBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddFromBox(AlwaysBox, never: false); e.Handled = true; }
    }

    private void AddFromBox(System.Windows.Controls.TextBox box, bool never)
    {
        // Several words at once: "seo, inweb, horoshop".
        foreach (var w in box.Text.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (never) UserWords.AddNever(w); else UserWords.AddAlways(w);
        }
        box.Clear();
        RefreshWordLists();
    }

    private void RefreshWordLists()
    {
        FillChips(NeverPanel, AppSettings.Current.NeverConvert);
        FillChips(AlwaysPanel, AppSettings.Current.AlwaysConvert);
    }

    private void FillChips(WrapPanel panel, List<string> list)
    {
        panel.Children.Clear();
        if (list.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Поки порожньо.", Foreground = Faint, FontSize = 12 });
            return;
        }
        foreach (var word in list.ToList())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = word, VerticalAlignment = VerticalAlignment.Center });
            var del = new Button { Content = "", Style = (Style)FindResource("IconBtn"), Margin = new Thickness(4, 0, 0, 0) };
            del.Click += (_, _) => { UserWords.Remove(list, word); RefreshWordLists(); };
            row.Children.Add(del);
            panel.Children.Add(new Border
            {
                Background = ChipBg,
                BorderBrush = ChipLine,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 3, 2, 3),
                Margin = new Thickness(0, 0, 8, 8),
                Child = row,
            });
        }
    }
}
