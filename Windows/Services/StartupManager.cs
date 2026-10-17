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

    // ---- Admin mode: start at logon with highest rights via Task Scheduler ----
    // Windows (UIPI) won't let a normal process type or scroll into an admin
    // window. A logon task with RunLevel=HighestAvailable starts BS elevated
    // without a UAC prompt each time; the prompt comes once, when the task is made.

    private const string TaskName = "Bison Switcher (admin)";

    public static bool ElevatedTaskExists => RunSchtasks($"/Query /TN \"{TaskName}\"", elevate: false) == 0;

    /// <summary>Creates the task (one UAC prompt) and drops the normal autostart,
    /// so BS isn't started twice. False if declined or failed.</summary>
    public static bool EnableElevated()
    {
        if (ExePath is null) return false;
        string xmlPath = Path.Combine(Path.GetTempPath(), "BisonSwitcherAdminTask.xml");
        try
        {
            File.WriteAllText(xmlPath, TaskXml(ExePath), System.Text.Encoding.Unicode);
            if (RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{xmlPath}\"",
                    elevate: !Environment.IsPrivilegedProcess) != 0) return false;
        }
        catch { return false; }
        finally { try { File.Delete(xmlPath); } catch { } }
        Set(false);
        return true;
    }

    /// <summary>Removes the task and brings back the normal autostart.</summary>
    public static bool DisableElevated()
    {
        if (RunSchtasks($"/Delete /F /TN \"{TaskName}\"", elevate: !Environment.IsPrivilegedProcess) != 0)
            return false;
        Set(true);
        return true;
    }

    /// <summary>Starts BS through the task: elevated, no UAC prompt.</summary>
    public static bool RunElevatedTask() => RunSchtasks($"/Run /TN \"{TaskName}\"", elevate: false) == 0;

    /// <summary>From an elevated BS, start a normal one: explorer.exe launches
    /// programs at the signed-in user's normal rights.</summary>
    public static bool StartUnelevated()
    {
        if (ExePath is null) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{ExePath}\"")
            {
                UseShellExecute = false,
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Exit code of schtasks.exe, or -1 if it could not run (UAC declined).</summary>
    private static int RunSchtasks(string args, bool elevate)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = elevate,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            if (elevate) psi.Verb = "runas";
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return -1;
            if (!p.WaitForExit(20_000)) return -1;
            return p.ExitCode;
        }
        catch { return -1; }
    }

    private static string TaskXml(string exe)
    {
        static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        string user = Esc($"{Environment.UserDomainName}\\{Environment.UserName}");
        // Task defaults that would hurt a laptop tray app are switched off: no start
        // only on AC power, no stop on battery, no 72-hour limit, normal priority.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Bison Switcher з правами адміністратора</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Esc(exe)}</Command></Exec></Actions>
            </Task>
            """;
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
