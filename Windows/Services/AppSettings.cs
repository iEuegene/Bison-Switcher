using System.IO;
using System.Text.Json;
using Kright.Native;

namespace Kright.Services;

/// <summary>JSON-backed settings in %APPDATA%\BisonSwitcher\settings.json
/// (first run imports %APPDATA%\Kright\settings.json if present).</summary>
public sealed class AppSettings
{
    // Default hotkey: Ctrl+Alt+K (avoids clobbering single-modifier shortcuts).
    public uint HotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
    public uint HotkeyVk { get; set; } = 0x4B; // 'K'
    // On by default: switch to English on email / URL / password fields.
    public bool AutoEnglishOnLatinFields { get; set; } = true;

    /// <summary>Show the Detect tab (developer tool). Hidden by default.</summary>
    public bool DebugMode { get; set; } = false;

    /// <summary>Auto-update consent: null = not asked yet (prompt on first run),
    /// true = check automatically, false = manual only. Mirrors Sparkle's
    /// first-run "Check for updates automatically?" prompt on macOS.</summary>
    public bool? AutoUpdateCheck { get; set; } = null;

    public bool AppLanguageRulesEnabled { get; set; } = true;
    public List<AppLanguageRule> AppLanguageRules { get; set; } = new();

    // ---- Auto-fix (wrong-layout words corrected as you type) ----
    public bool AutoFixEnabled { get; set; } = true;
    /// <summary>Backspace right after an auto-fix restores what was typed.</summary>
    public bool AutoFixUndoOnBackspace { get; set; } = true;
    /// <summary>An undone word goes to <see cref="NeverConvert"/>.</summary>
    public bool AutoFixLearnFromUndo { get; set; } = true;
    /// <summary>A word fixed twice with the hotkey goes to <see cref="AlwaysConvert"/>.</summary>
    public bool AutoFixLearnFromHotkey { get; set; } = true;
    /// <summary>0 = careful, 1 = normal, 2 = bold. Only affects words that are
    /// in neither dictionary (dictionary hits are decided the same way).</summary>
    public int AutoFixSensitivity { get; set; } = 1;
    /// <summary>Words (as typed, lower-case) auto-fix must leave alone.</summary>
    public List<string> NeverConvert { get; set; } = new();
    /// <summary>Words (as they SHOULD read, lower-case) auto-fix always produces.</summary>
    public List<string> AlwaysConvert { get; set; } = new();
    public Dictionary<string, int> HotkeyFixCounts { get; set; } = new();
    /// <summary>Apps where auto-fix is off (the hotkey still works there).</summary>
    public List<ExcludedApp> AutoFixExcludedApps { get; set; } = ExcludedApp.Defaults();

    /// <summary>Switch to English when the browser address bar gets focus.</summary>
    public bool AutoEnglishInAddressBar { get; set; } = true;

    public bool ContactLanguageRulesEnabled { get; set; } = true;
    public List<ContactLanguageRule> ContactLanguageRules { get; set; } = new();

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BisonSwitcher");
    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kright", "settings.json");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            // First run after the rename: carry over the Kright settings (rules, hotkey…).
            if (File.Exists(LegacyFilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(LegacyFilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>An app excluded from auto-fix, matched by executable file name so it
/// survives app updates that move the install folder (Teams, Discord…).</summary>
public sealed class ExcludedApp
{
    public string ExeName { get; set; } = "";
    public string AppName { get; set; } = "";
    /// <summary>Full path when added from a running app (for the icon); may be empty.</summary>
    public string ExePath { get; set; } = "";

    public static List<ExcludedApp> Defaults() => new()
    {
        new() { ExeName = "WindowsTerminal.exe", AppName = "Windows Terminal" },
        new() { ExeName = "cmd.exe", AppName = "Command Prompt" },
        new() { ExeName = "powershell.exe", AppName = "Windows PowerShell" },
        new() { ExeName = "pwsh.exe", AppName = "PowerShell" },
        new() { ExeName = "Code.exe", AppName = "Visual Studio Code" },
        new() { ExeName = "devenv.exe", AppName = "Visual Studio" },
        new() { ExeName = "KeePassXC.exe", AppName = "KeePassXC" },
        new() { ExeName = "KeePass.exe", AppName = "KeePass" },
        new() { ExeName = "1Password.exe", AppName = "1Password" },
        new() { ExeName = "Bitwarden.exe", AppName = "Bitwarden" },
    };
}
