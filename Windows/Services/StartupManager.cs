using System.IO;
using Microsoft.Win32;

namespace Kright.Services;

/// <summary>"Start with Windows" via HKCU\…\Run (per-user, no admin). Also
/// recognises the Startup-folder shortcut the Inno Setup installer can create,
/// so the checkbox reflects either and turning it off removes both.</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BisonSwitcher";
    /// <summary>Written by earlier builds of this fork (exe was Kright.exe).</summary>
    private const string LegacyValueName = "Kright";

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Bison Switcher.lnk");

    private static string? ExePath => Environment.ProcessPath;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is string v && v.Length > 0) return true;
            }
            catch { }
            return File.Exists(ShortcutPath);
        }
    }

    /// <summary>Earlier builds of this fork wrote a "Kright" autostart entry for
    /// Kright.exe in this same folder. Move that choice over to BisonSwitcher.exe.
    /// The official Kright's entry (another folder, exe still there) is left alone.</summary>
    public static void MigrateLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is not string v) return;
            string legacyExe = v.Trim().Trim('"');
            string? ourDir = ExePath is null ? null : Path.GetDirectoryName(ExePath);
            bool ours = !File.Exists(legacyExe) ||
                        (ourDir != null && string.Equals(Path.GetDirectoryName(legacyExe), ourDir,
                            StringComparison.OrdinalIgnoreCase));
            if (!ours) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            if (ExePath != null) key.SetValue(ValueName, $"\"{ExePath}\"");
        }
        catch { }
    }

    /// <summary>Returns false if the change could not be applied.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                if (ExePath is null) return false;
                key.SetValue(ValueName, $"\"{ExePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            }
            return true;
        }
        catch { return false; }
    }
}
