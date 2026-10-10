; Inno Setup script for Bison Switcher (fork of Kright).
; Build with: ISCC.exe installer\kright.iss  (or run ..\build-installer.ps1)
; Produces a per-user installer (no admin / UAC needed), like Chrome / VS Code.

#define MyAppName "Bison Switcher"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "iEuegene (based on Kright by Walter Technologies LTD)"
#define MyAppExeName "BisonSwitcher.exe"

[Setup]
; A unique, stable GUID identifies this app for upgrades/uninstall. Keep it.
AppId={{B1505A17-C4E2-4F7B-9A3D-2E8F6C1B5A90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
; Stamp the version into the setup .exe's file metadata so NetSparkle's appcast
; generator can read the version from the installer it lists.
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\BisonSwitcher
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
; Per-user install → no UAC prompt.
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=BisonSwitcherSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startupicon"; Description: "Start {#MyAppName} automatically when I log in"; GroupDescription: "Startup:"

[Files]
; Everything dotnet publish produced (self-contained, so the .NET runtime is bundled).
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
