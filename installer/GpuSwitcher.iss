; Inno Setup script for GPU Switcher. Build with build-release.ps1 (it publishes the app first).
#define AppVersion GetVersionNumbersString("..\publish\GpuSwitcher.exe")

[Setup]
AppId={{8D3F6B1E-4C2A-4E7B-9A51-6F0C2B7D9E14}
AppName=GPU Switcher
AppVersion={#AppVersion}
AppPublisher=SammyDaBlock
AppPublisherURL=https://github.com/SammyDaBlock/gpu-switcher
AppSupportURL=https://github.com/SammyDaBlock/gpu-switcher/issues
DefaultDirName={autopf}\GPU Switcher
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\release
OutputBaseFilename=GpuSwitcher-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\GpuSwitcher.exe
UninstallDisplayName=GPU Switcher
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LicenseFile=..\LICENSE

[Tasks]
Name: "autostart"; Description: "Start GPU Switcher with Windows (in the tray, no UAC prompt)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\GpuSwitcher.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GPU Switcher"; Filename: "{app}\GpuSwitcher.exe"
Name: "{autodesktop}\GPU Switcher"; Filename: "{app}\GpuSwitcher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GpuSwitcher.exe"; Parameters: "--autostart"; Tasks: autostart; Flags: runhidden waituntilterminated
Filename: "{app}\GpuSwitcher.exe"; Description: "Open GPU Switcher"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
; close it, hand startup apps back to Windows (otherwise they'd never start again), remove the logon task, make sure every GPU is on
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM GpuSwitcher.exe"; Flags: runhidden; RunOnceId: "Kill"
Filename: "{app}\GpuSwitcher.exe"; Parameters: "--restore-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreStartup"
Filename: "{app}\GpuSwitcher.exe"; Parameters: "--autostart off"; Flags: runhidden waituntilterminated; RunOnceId: "Autostart"
Filename: "{app}\GpuSwitcher.exe"; Parameters: "--recover"; Flags: runhidden waituntilterminated; RunOnceId: "Recover"

[Code]
// An update replaces the exe, so the running copy has to go first. If it had a GPU switched off,
// --recover turns it back on (the app's watchdog would too, this is just faster).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GpuSwitcher.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if FileExists(ExpandConstant('{app}\GpuSwitcher.exe')) then
    Exec(ExpandConstant('{app}\GpuSwitcher.exe'), '--recover', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
