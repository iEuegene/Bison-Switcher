using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kright.Native;
using Kright.Services;
using InputLanguage = Kright.Services.InputLanguage;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;

namespace Kright;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _recording;
    private LayoutSuggestion? _suggestion;
    private DispatcherTimer? _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => RefreshDetect();
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };

        HotkeyText.Text = App.Hotkey.DisplayString;
        AutoLangCheck.IsChecked = App.Enforcer.Enabled;
        AddressBarCheck.IsChecked = App.Enforcer.AddressBarEnabled;

        // Auto-fix section
        AutoFixCheck.IsChecked = App.AutoFix.Enabled;
        UndoCheck.IsChecked = AppSettings.Current.AutoFixUndoOnBackspace;
        LearnCheck.IsChecked = AppSettings.Current.AutoFixLearnFromUndo && AppSettings.Current.AutoFixLearnFromHotkey;
        SensitivityCombo.SelectedIndex = Math.Clamp(AppSettings.Current.AutoFixSensitivity, 0, 2);
        App.AutoFix.StateChanged += () =>
        {
            if (!IsVisible) return; // refreshed on activation instead
            Dispatcher.InvokeAsync(() =>
            {
                AutoFixCheck.IsChecked = App.AutoFix.Enabled;
                RefreshWordLists();
            });
        };
        RefreshExcludedList();
        RefreshWordLists();
        StartupCheck.IsChecked = StartupManager.IsEnabled;

        DebugCheck.IsChecked = AppSettings.Current.DebugMode;
        ApplyDebugMode(AppSettings.Current.DebugMode);

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Kright {v?.ToString(3) ?? "1.0.0"} · UA auto-fix fork";

        // Per-app keyboard section
        AppLangCheck.IsChecked = App.AppEnforcer.Enabled;
        foreach (var lang in LanguageManager.Enabled())
            AddLayoutCombo.Items.Add(new ComboBoxItem { Content = lang.Name, Tag = lang });
        if (AddLayoutCombo.Items.Count > 0) AddLayoutCombo.SelectedIndex = 0;
        RefreshRulesList();

        // Per-contact keyboard section
        ContactLangCheck.IsChecked = App.ContactEnforcer.Enabled;
        foreach (var lang in LanguageManager.Enabled())
            ContactLayoutCombo.Items.Add(new ComboBoxItem { Content = lang.Name, Tag = lang });
        if (ContactLayoutCombo.Items.Count > 0) ContactLayoutCombo.SelectedIndex = 0;
        RefreshContactRulesList();

        RefreshLayoutMap();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateAddButtonLabel();
        UpdateAddContactButtonLabel();
        UpdateExcludeButtonLabel();
        AutoFixCheck.IsChecked = App.AutoFix.Enabled;
        RefreshWordLists();
    }

    public void SelectTab(int index)
    {
        // Detect (index 0) may be hidden — fall back to Settings.
        if (index == 0 && DetectTab.Visibility != Visibility.Visible) index = 1;
        Tabs.SelectedIndex = index;
    }

    private void ApplyDebugMode(bool on)
    {
        DetectTab.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on && ReferenceEquals(Tabs.SelectedItem, DetectTab)) Tabs.SelectedIndex = 1; // Settings
    }

    // Keep the app alive (tray) when the window is closed.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    // ---- Detect tab ----

    private void RefreshLayoutMap()
    {
        var langs = LanguageManager.Enabled();
        var latin = langs.FirstOrDefault(l => LanguageManager.IsLatin(l.Hkl));
        var other = langs.FirstOrDefault(l => !LanguageManager.IsLatin(l.Hkl));
        if (latin == null || other == null)
        {
            LayoutMapText.Text = "Install a Latin + a non-Latin keyboard layout to view the map.";
            return;
        }

        var latinFwd = KeyboardLayoutMap.ForwardMap(latin.Hkl);
        var otherFwd = KeyboardLayoutMap.ForwardMap(other.Hkl);

        var sb = new StringBuilder();
        sb.AppendLine($"{latin.Name}  ↔  {other.Name}");

        // Letter rows (QWERTY order)
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
            {
                var oc = otherFwd.TryGetValue(vk, out var v) ? v : "?";
                sb.Append($"{name}:{oc}  ");
            }
            sb.AppendLine();
        }

        // Punctuation keys — show both EN and HE since neither is obvious
        sb.AppendLine();
        sb.AppendLine($"Punctuation  (KEY: {latin.Name}→{other.Name})");
        (uint vk, string key)[] punctKeys =
        [
            (0xBA, ";"), (0xDE, "'"), (0xBC, ","), (0xBE, "."), (0xBF, "/"), (0xC0, "`"),
        ];
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
        var field = FocusInspector.Focused();
        if (field is null)
        {
            AppLabel.Text = "No field focused";
            return;
        }
        AppLabel.Text = $"{field.ControlType}  ·  {field.Name}";
        GuessText.Text = field.Guess;

        var langs = LanguageManager.Enabled();
        LanguagesText.Text = string.Join("   ",
            langs.Select(l => l.IsCurrent ? $"[{l.Name}]" : l.Name));

        _suggestion = LayoutConverter.Suggest(field.Value);
        if (_suggestion is { IsMeaningful: true } s)
        {
            TypedText.Text = $"Typed: {s.Original}   ({s.FromLayout} → {s.ToLayout})";
            ConvertedText.Text = s.Converted;
            ReplaceButton.IsEnabled = true;

            var v = GibberishDetector.LooksWrongLayout(s.Original, s.Converted, s.FromLang, s.ToLang);
            GibberishText.Text = v.Wrong
                ? $"🧠 Likely wrong layout — {(int)(v.Confidence * 100)}% confident"
                : "🧠 Looks intentional — probably not a layout mistake";
            GibberishText.Foreground = v.Wrong
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.Gray;
        }
        else
        {
            TypedText.Text = "Type ≥3 letters in a field to see a suggestion.";
            ConvertedText.Text = "";
            GibberishText.Text = "";
            ReplaceButton.IsEnabled = false;
        }

        if (App.Enforcer.LastAction is { } a) AutoLangStatus.Text = a;
        AutoFixDecisionText.Text = (App.AutoFix.LastDecision ?? "—") +
            $"\nDictionaries: uk {WordRanks.Embedded.Count("uk"):N0} · en {WordRanks.Embedded.Count("en"):N0}";
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suggestion is not { } s) return;
        bool ok = FocusInspector.TrySetFocusedValue(s.FullReplacement);
        ReplaceResult.Text = ok ? "Replaced ✓" : "Field is read-only — use the global hotkey instead.";
        ReplaceResult.Foreground = ok
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.IndianRed;
    }

    // ---- Settings tab ----

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

        // When Alt is held, the key arrives as Key.System with the real key in
        // SystemKey — resolve that first so the guard below sees the actual key.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { StopRecording(); return; }

        // Ignore lone modifier presses (incl. when they arrive via Key.System).
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None) return;

        // Read each modifier's physical key state directly (both L/R variants).
        // This is more reliable than Keyboard.Modifiers when Alt routes the event
        // through Key.System, and on layouts where AltGr reports as Ctrl+Alt.
        uint fs = 0;
        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        bool alt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        if (ctrl) fs |= NativeMethods.MOD_CONTROL;
        if (alt) fs |= NativeMethods.MOD_ALT;
        if (shift) fs |= NativeMethods.MOD_SHIFT;
        if (win) fs |= NativeMethods.MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (fs == 0) return; // require a modifier
        App.Hotkey.Update(fs, vk);
        StopRecording();
    }

    private void StopRecording()
    {
        _recording = false;
        RecordButton.Content = "Змінити…";
        HotkeyText.Text = App.Hotkey.DisplayString;
    }

    private void AutoLangCheck_Click(object sender, RoutedEventArgs e)
        => App.Enforcer.Enabled = AutoLangCheck.IsChecked == true;

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

    // ---- Per-app keyboard ----

    private void AppLangCheck_Click(object sender, RoutedEventArgs e)
        => App.AppEnforcer.Enabled = AppLangCheck.IsChecked == true;

    private void AddAppButton_Click(object sender, RoutedEventArgs e)
    {
        var exePath = App.AppEnforcer.LastExternalExePath;
        var appName = App.AppEnforcer.LastExternalAppName;
        if (exePath == null || appName == null)
        {
            ShowAddStatus("Спершу перейдіть у потрібну програму, потім натисніть «Додати».");
            return;
        }
        if (AppSettings.Current.AppLanguageRules.Any(r =>
            string.Equals(r.ExePath, exePath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowAddStatus($"{appName} вже в списку.");
            return;
        }
        if (AddLayoutCombo.SelectedItem is not ComboBoxItem { Tag: InputLanguage lang })
        {
            ShowAddStatus("Спершу виберіть розкладку.");
            return;
        }
        AppSettings.Current.AppLanguageRules.Add(new AppLanguageRule
        {
            ExePath = exePath,
            AppName = appName,
            HklValue = (long)lang.Hkl,
            LayoutName = lang.Name
        });
        AppSettings.Save();
        RefreshRulesList();
        ShowAddStatus($"Додано: {appName}.");
    }

    private void RefreshRulesList()
    {
        RulesPanel.Children.Clear();
        var langs = LanguageManager.Enabled();
        foreach (var rule in AppSettings.Current.AppLanguageRules)
            RulesPanel.Children.Add(BuildRuleRow(rule, langs));
        UpdateAddButtonLabel();
    }

    private void UpdateAddButtonLabel()
    {
        AddAppButton.Content = App.AppEnforcer.LastExternalAppName is { } n
            ? $"+ Додати «{n}»"
            : "+ Додати поточну програму";
    }

    private UIElement BuildRuleRow(AppLanguageRule rule, List<InputLanguage> langs)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        var icon = LoadAppIcon(rule.ExePath);
        if (icon != null)
        {
            var img = new System.Windows.Controls.Image
                { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(img, 0);
            grid.Children.Add(img);
        }

        var name = new TextBlock
        {
            Text = rule.AppName, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var combo = new ComboBox { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        foreach (var lang in langs)
        {
            var item = new ComboBoxItem { Content = lang.Name, Tag = lang };
            combo.Items.Add(item);
            if ((long)lang.Hkl == rule.HklValue) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: InputLanguage l })
            {
                rule.HklValue = (long)l.Hkl;
                rule.LayoutName = l.Name;
                AppSettings.Save();
            }
        };
        Grid.SetColumn(combo, 2);
        grid.Children.Add(combo);

        var del = new Button
        {
            Content = "✕", Width = 20, Height = 20, FontSize = 11,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            VerticalAlignment = VerticalAlignment.Center
        };
        del.Click += (_, _) =>
        {
            AppSettings.Current.AppLanguageRules.Remove(rule);
            AppSettings.Save();
            RefreshRulesList();
        };
        Grid.SetColumn(del, 3);
        grid.Children.Add(del);

        return grid;
    }

    private static BitmapSource? LoadAppIcon(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch { return null; }
    }

    private void ShowAddStatus(string msg)
    {
        AddAppStatus.Text = msg;
        AddAppStatus.Visibility = Visibility.Visible;
        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            AddAppStatus.Visibility = Visibility.Collapsed;
        };
        _statusTimer.Start();
    }

    // ---- Per-contact keyboard ----

    private DispatcherTimer? _contactStatusTimer;

    private void ContactLangCheck_Click(object sender, RoutedEventArgs e)
        => App.ContactEnforcer.Enabled = ContactLangCheck.IsChecked == true;

    private void AddContactButton_Click(object sender, RoutedEventArgs e)
    {
        var app = App.ContactEnforcer.LastContactApp;
        var contact = App.ContactEnforcer.LastContactName;
        if (app == null || string.IsNullOrEmpty(contact))
        {
            ShowContactStatus("Спершу відкрийте чат у Teams, потім натисніть «Додати».");
            return;
        }
        if (AppSettings.Current.ContactLanguageRules.Any(r =>
            r.App == app.Value && r.ContactName == contact))
        {
            ShowContactStatus($"{contact} вже в списку.");
            return;
        }
        if (ContactLayoutCombo.SelectedItem is not ComboBoxItem { Tag: InputLanguage lang })
        {
            ShowContactStatus("Спершу виберіть розкладку.");
            return;
        }
        AppSettings.Current.ContactLanguageRules.Add(new ContactLanguageRule
        {
            App = app.Value,
            ContactName = contact,
            HklValue = (long)lang.Hkl,
            LayoutName = lang.Name
        });
        AppSettings.Save();
        RefreshContactRulesList();
        ShowContactStatus($"Додано: {contact}.");
    }

    private void RefreshContactRulesList()
    {
        ContactRulesPanel.Children.Clear();
        var langs = LanguageManager.Enabled();
        foreach (var rule in AppSettings.Current.ContactLanguageRules)
            ContactRulesPanel.Children.Add(BuildContactRuleRow(rule, langs));
        UpdateAddContactButtonLabel();
    }

    private void UpdateAddContactButtonLabel()
    {
        AddContactButton.Content = App.ContactEnforcer.LastContactName is { } n
            ? $"+ Додати «{n}»"
            : "+ Додати поточний чат Teams";
    }

    private UIElement BuildContactRuleRow(ContactLanguageRule rule, List<InputLanguage> langs)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = rule.ContactName, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = rule.App.DisplayName(), FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var combo = new ComboBox { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        foreach (var lang in langs)
        {
            var item = new ComboBoxItem { Content = lang.Name, Tag = lang };
            combo.Items.Add(item);
            if ((long)lang.Hkl == rule.HklValue) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: InputLanguage l })
            {
                rule.HklValue = (long)l.Hkl;
                rule.LayoutName = l.Name;
                AppSettings.Save();
            }
        };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);

        var del = new Button
        {
            Content = "✕", Width = 20, Height = 20, FontSize = 11,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            VerticalAlignment = VerticalAlignment.Center
        };
        del.Click += (_, _) =>
        {
            AppSettings.Current.ContactLanguageRules.Remove(rule);
            AppSettings.Save();
            RefreshContactRulesList();
        };
        Grid.SetColumn(del, 2);
        grid.Children.Add(del);

        return grid;
    }

    private void ShowContactStatus(string msg)
    {
        AddContactStatus.Text = msg;
        AddContactStatus.Visibility = Visibility.Visible;
        _contactStatusTimer?.Stop();
        _contactStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _contactStatusTimer.Tick += (_, _) =>
        {
            _contactStatusTimer.Stop();
            AddContactStatus.Visibility = Visibility.Collapsed;
        };
        _contactStatusTimer.Start();
    }

    // ---- Auto-fix ----

    private void AutoFixCheck_Click(object sender, RoutedEventArgs e)
        => App.AutoFix.Enabled = AutoFixCheck.IsChecked == true;

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

    private void SensitivityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SensitivityCombo.SelectedIndex < 0) return;
        AppSettings.Current.AutoFixSensitivity = SensitivityCombo.SelectedIndex;
        AppSettings.Save();
    }

    private void AddressBarCheck_Click(object sender, RoutedEventArgs e)
        => App.Enforcer.AddressBarEnabled = AddressBarCheck.IsChecked == true;

    private void StartupCheck_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartupCheck.IsChecked == true;
        if (!StartupManager.Set(want)) StartupCheck.IsChecked = !want;
    }

    // ---- Excluded apps ----

    private DispatcherTimer? _excludeStatusTimer;

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
            ShowExcludeStatus("Спершу перейдіть у потрібну програму, потім натисніть кнопку.");
            return;
        }
        string exeName = System.IO.Path.GetFileName(exePath);
        var list = AppSettings.Current.AutoFixExcludedApps;
        if (list.Any(a => string.Equals(a.ExeName, exeName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowExcludeStatus($"{appName} вже виключено.");
            return;
        }
        list.Add(new ExcludedApp { ExeName = exeName, AppName = appName, ExePath = exePath });
        AppSettings.Save();
        App.AutoFix.InvalidateLists();
        RefreshExcludedList();
        ShowExcludeStatus($"Виключено: {appName}.");
    }

    private void RefreshExcludedList()
    {
        ExcludedPanel.Children.Clear();
        foreach (var app in AppSettings.Current.AutoFixExcludedApps.ToList())
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

            if (!string.IsNullOrEmpty(app.ExePath) && LoadAppIcon(app.ExePath) is { } icon)
            {
                var img = new System.Windows.Controls.Image
                    { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(img, 0);
                grid.Children.Add(img);
            }

            var name = new TextBlock
            {
                Text = $"{app.AppName}  ({app.ExeName})", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var del = DeleteButton();
            del.Click += (_, _) =>
            {
                AppSettings.Current.AutoFixExcludedApps.Remove(app);
                AppSettings.Save();
                App.AutoFix.InvalidateLists();
                RefreshExcludedList();
            };
            Grid.SetColumn(del, 2);
            grid.Children.Add(del);

            ExcludedPanel.Children.Add(grid);
        }
        UpdateExcludeButtonLabel();
    }

    private void ShowExcludeStatus(string msg)
    {
        ExcludeStatus.Text = msg;
        ExcludeStatus.Visibility = Visibility.Visible;
        _excludeStatusTimer?.Stop();
        _excludeStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _excludeStatusTimer.Tick += (_, _) =>
        {
            _excludeStatusTimer.Stop();
            ExcludeStatus.Visibility = Visibility.Collapsed;
        };
        _excludeStatusTimer.Start();
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
        foreach (var word in list.ToList())
        {
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = word, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD))
            });
            var del = DeleteButton();
            del.Width = del.Height = 16;
            del.Margin = new Thickness(4, 0, 0, 0);
            del.Click += (_, _) => { UserWords.Remove(list, word); RefreshWordLists(); };
            row.Children.Add(del);
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 4, 2),
                Margin = new Thickness(0, 0, 6, 6),
                Child = row
            });
        }
    }

    private static Button DeleteButton() => new()
    {
        Content = "✕", Width = 20, Height = 20, FontSize = 11,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
        VerticalAlignment = VerticalAlignment.Center
    };
}
