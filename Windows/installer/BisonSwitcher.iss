; Bison Switcher — installer (Inno Setup 6).
; Build:  ISCC.exe installer\BisonSwitcher.iss  [/DMyAppVersion=1.2.1]
; Expects the single-file app at ..\publish-ua\BisonSwitcher.exe (build-ua.cmd / CI).
; Per-user install: no admin rights, no UAC prompt.

#ifndef MyAppVersion
  #define MyAppVersion "1.5.0"
#endif
#define MyAppName "Bison Switcher"
#define MyAppExeName "BisonSwitcher.exe"
#define MyPublisher "inweb"
#define MyUrl "https://github.com/iEuegene/Bison-Switcher"

[Setup]
; Keep this GUID: it identifies the app for upgrades and uninstall.
AppId={{B1505A17-C4E2-4F7B-9A3D-2E8F6C1B5A90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyPublisher}
AppPublisherURL=https://inweb.ua/ua/
AppSupportURL={#MyUrl}
AppUpdatesURL={#MyUrl}/releases
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} — інсталятор
DefaultDirName={localappdata}\Programs\BisonSwitcher
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableDirPage=auto
PrivilegesRequired=lowest
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\BisonSwitcher.ico
OutputDir=output
OutputBaseFilename=BisonSwitcherSetup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

; Inweb look: modern wizard, green brand panel on welcome/finish, BS mark in the header.
WizardStyle=modern
WizardImageFile=art\wizard-100.bmp,art\wizard-125.bmp,art\wizard-150.bmp,art\wizard-175.bmp,art\wizard-200.bmp,art\wizard-250.bmp
WizardSmallImageFile=art\wizard-small-100.bmp,art\wizard-small-125.bmp,art\wizard-small-150.bmp,art\wizard-small-175.bmp,art\wizard-small-200.bmp,art\wizard-small-250.bmp
WizardImageBackColor=$00293B00
WizardSmallImageBackColor=clWhite

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Messages]
ukrainian.WelcomeLabel1=Вітаємо в Bison Switcher
ukrainian.WelcomeLabel2=Автоперемикач розкладки UA ↔ EN для команди inweb.%n%nНабрали «ghbdsn» — отримали «привіт». Програма сама помічає слова, набрані не в тій розкладці, і виправляє їх на пробілі, Tab або Enter.%n%nВстановлення займе кілька секунд і не потребує прав адміністратора.
ukrainian.FinishedHeadingLabel=Bison Switcher встановлено
ukrainian.FinishedLabel=Програма працює у треї — шукайте іконку BS біля годинника. Клік по ній відкриває налаштування.%n%nЯкщо Windows колись спитає про незнайомого видавця, це нормально: програма не підписана платним сертифікатом.
ukrainian.ClickFinish=Натисніть «Завершити».

[CustomMessages]
ukrainian.TaskStartup=Запускати разом з Windows (рекомендовано)
ukrainian.TaskDesktop=Ярлик на робочому столі
ukrainian.GroupStartup=Запуск:
ukrainian.GroupIcons=Ярлики:
ukrainian.LaunchApp=Запустити Bison Switcher
ukrainian.FeaturesTitle=Що вміє Bison Switcher
ukrainian.FeaturesSubtitle=Коротко про головне — решта в налаштуваннях програми.
ukrainian.RemoveSettings=Видалити також ваші налаштування Bison Switcher (словник, виключення, правила)?

[Tasks]
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:GroupStartup}"
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:GroupIcons}"; Flags: unchecked

[Files]
Source: "..\publish-ua\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Автоперемикач розкладки UA ↔ EN"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Same autostart entry the app's own "Запускати разом з Windows" toggle uses.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BisonSwitcher"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
; Silent update started from the app: bring the app back afterwards.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent

[Code]
const
  BrandGreen = $00293B00;   { #003B29 as BGR }
  BrandLime  = $007EFCF0;   { #F0FC7E }
  BrandMuted = $00606060;

procedure StopRunningApp();
var
  Code: Integer;
begin
  { Single-file exe is locked while it runs; close it quietly before copying/removing. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM BisonSwitcherPortable.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

procedure AddFeature(Page: TWizardPage; var Top: Integer; const Title, Text: String);
var
  Mark: TPanel;
  T, D: TNewStaticText;
begin
  Mark := TPanel.Create(Page);
  Mark.Parent := Page.Surface;
  Mark.Left := 0;
  Mark.Top := Top + ScaleY(2);
  Mark.Width := ScaleX(5);
  Mark.Height := ScaleY(16);
  Mark.BevelOuter := bvNone;
  Mark.Color := BrandLime;

  T := TNewStaticText.Create(Page);
  T.Parent := Page.Surface;
  T.Left := ScaleX(16);
  T.Top := Top;
  T.Font.Style := [fsBold];
  T.Font.Size := 10;
  T.Font.Color := BrandGreen;
  T.Caption := Title;

  D := TNewStaticText.Create(Page);
  D.Parent := Page.Surface;
  D.Left := ScaleX(16);
  D.Top := T.Top + T.Height + ScaleY(2);
  D.Width := Page.SurfaceWidth - ScaleX(16);
  D.AutoSize := False;
  D.WordWrap := True;
  D.Height := ScaleY(30);
  D.Font.Color := BrandMuted;
  D.Caption := Text;

  Top := D.Top + D.Height + ScaleY(8);
end;

procedure InitializeWizard();
var
  Page: TWizardPage;
  Top: Integer;
begin
  Page := CreateCustomPage(wpWelcome, CustomMessage('FeaturesTitle'), CustomMessage('FeaturesSubtitle'));
  Top := 0;
  AddFeature(Page, Top, 'Автовиправлення розкладки',
    '«ghbdsn» → «привіт», «руддщ» → «hello». Словник на 470 тисяч словоформ, працює без інтернету.');
  AddFeature(Page, Top, 'Велика літера на початку речення',
    'Після крапки, ! і ? — сама. Скорочення на кшталт «грн.» чи «т. д.» не чіпає.');
  AddFeature(Page, Top, 'Ваші правила',
    'Власний словник винятків і нових слів, програми-виключення. Backspace одразу після заміни скасовує її.');
  AddFeature(Page, Top, 'Англійська там, де треба',
    'Поля email і паролів, адресний рядок браузера — розкладка перемикається сама.');
  AddFeature(Page, Top, 'Приватність',
    'Нічого не надсилає в інтернет і не записує набраний текст. У полях паролів вимикається повністю.');
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsDir: String;
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BisonSwitcher');

  if CurUninstallStep = usPostUninstall then
  begin
    SettingsDir := ExpandConstant('{userappdata}\BisonSwitcher');
    if DirExists(SettingsDir) and not UninstallSilent() then
      if MsgBox(CustomMessage('RemoveSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(SettingsDir, True, True, True);
  end;
end;
