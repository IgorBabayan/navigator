; Inno Setup script for Navigator. Structure adapted from the ED Assistant installer (same author, see
; docs/SOURCE_ORIGINALITY.md). Values marked "override" are passed by installer/windows/package.ps1.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

#define AppName "Navigator"
#define AppExe  "Navigator.exe"
#define AppPublisher "Igor Babayan"
#define AppUrl "https://github.com/IgorBabayan/navigator"

[Setup]
; Never change this GUID: Windows uses it to recognise upgrades of the same product.
AppId={{5B0E3C59-7E3A-4A92-9D7C-2F6A1C4B8E11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no UAC); the user may choose an all-users install.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\..\src\Navigator.App\Assets\navigator.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; A previous portable marker would make the installed copy update itself as a portable copy.
Type: files; Name: "{app}\.navigator-portable"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Relaunch after a silent automatic update (the updater passes /VERYSILENT).
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[Registry]
; The global shortcut agent's sign-in entry is written by Navigator itself (per user); remove it with the app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Navigator shortcut"; Flags: uninsdeletevalue dontcreatekey

[Code]
// The global shortcut agent ("Navigator.exe --shortcut-agent") has no window, so the Restart Manager cannot ask it to
// close. Stop just that process (never a Navigator window) before files are replaced or removed; Navigator starts a new
// agent the next time it runs.
procedure StopShortcutAgent();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process -Filter ''Name=''''{#AppExe}'''''' | ' +
    'Where-Object { $_.CommandLine -like ''*--shortcut-agent*'' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopShortcutAgent();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopShortcutAgent();
end;
