; MagicMouse installer (Inno Setup 6). Build with build-installer.cmd at the repository root.
; Expects: artifacts\app (self-contained publish of MagicMouse + HidProbe) and driver\MagicTouchFilter\out.

#define AppName "MagicMouse"
#define AppExe "MagicMouse.exe"
#define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\artifacts\app\MagicMouse.exe")

[Setup]
AppId={{6B1C4F0E-2E7A-4C8D-9B3A-5D1E8F2A7C40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=MagicMouse (personal build)
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=MagicMouse-Setup-{#AppVersion}
SetupIconFile=..\src\MagicMouse.App\Assets\MagicMouse.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004 or later (the driver uses ExAllocatePool2).
MinVersion=10.0.19041
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

[Tasks]
Name: "autostart"; Description: "Start MagicMouse when I sign in"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\driver\MagicTouchFilter\out\MagicTouchFilter.inf"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "..\driver\MagicTouchFilter\out\MagicTouchFilter.sys"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "..\driver\MagicTouchFilter\out\MagicTouchFilter.cat"; DestDir: "{app}\driver"; Flags: ignoreversion
; Present only for test-signed builds; a Microsoft-signed driver needs no certificate.
Source: "..\driver\MagicTouchFilter\out\MagicTouchTest.cer"; DestDir: "{app}\driver"; Flags: ignoreversion skipifsourcedoesntexist
Source: "scripts\install-driver.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\uninstall-driver.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Autostart (the app's own "Start with Windows" setting manages the same value later).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart
; Always remove it on uninstall, whoever created it. Also drop the entry from the app's old name.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppName}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MagicTouch"; Flags: deletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start MagicMouse now"; Flags: postinstall nowait runasoriginaluser skipifsilent

[Code]
var
  NeedReboot: Boolean;

function PowerShell(const Script, Args: String): Integer;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '" ' + Args,
      '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

procedure CloseApp();
var
  Code: Integer;
begin
  // The app keeps the driver's control device open; it must be closed before the driver is replaced,
  // or Windows postpones the driver update until a reboot.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MagicMouse.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MagicTouch.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im HidProbe.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(800);
end;

function TestSigningOn(): Boolean;
var
  Options: String;
begin
  Result := RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control', 'SystemStartOptions', Options)
    and (Pos('TESTSIGNING', Uppercase(Options)) > 0);
end;

function VendorDriverPresent(): Boolean;
begin
  // Magic Utilities' mouse filter (service "MagicMouse" -> MagicMouse.sys) binds to the same devices.
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\MagicMouse')
    or FileExists(ExpandConstant('{sys}\drivers\MagicMouse.sys'));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if VendorDriverPresent() then
  begin
    Result := MsgBox('Another Magic Mouse driver (Magic Utilities) is installed. Both drivers attach to the same mouse, ' +
      'so please uninstall Magic Utilities first.' + #13#10#13#10 + 'Continue anyway?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseApp();
  Result := '';
end;

procedure EnableTestSigningIfWanted();
var
  Code: Integer;
begin
  if TestSigningOn() then Exit;
  if not FileExists(ExpandConstant('{app}\driver\MagicTouchTest.cer')) then Exit;  // properly signed build
  if MsgBox('This build of the MagicMouse driver is test-signed. Windows only loads it in test-signing mode, ' +
      'which also requires Secure Boot to be turned off in your PC''s firmware settings.' + #13#10#13#10 +
      'Turn on test-signing mode now? (Takes effect after a restart.)', mbConfirmation, MB_YESNO) = IDYES then
  begin
    if Exec(ExpandConstant('{sys}\bcdedit.exe'), '/set testsigning on', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
      NeedReboot := True
    else
      MsgBox('Could not enable test-signing mode (is Secure Boot on?). The app is installed, but multitouch ' +
        'features stay off until the driver can load.', mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Installing the MagicMouse driver...';
    Code := PowerShell(ExpandConstant('{app}\scripts\install-driver.ps1'),
      ExpandConstant('-DriverDir "{app}\driver" -LogFile "{app}\install.log"'));
    if Code = 3010 then
      NeedReboot := True
    else if Code <> 0 then
      MsgBox('The driver could not be installed (code ' + IntToStr(Code) + '). See ' +
        ExpandConstant('{app}\install.log') + '.', mbError, MB_OK);
    EnableTestSigningIfWanted();
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := NeedReboot;
end;

// ---------------------------------------------------------------- uninstall

var
  UninstallNeedsReboot: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Settings: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseApp();
    // Runs before the files are deleted, while the script is still on disk.
    Code := PowerShell(ExpandConstant('{app}\scripts\uninstall-driver.ps1'),
      ExpandConstant('-LogFile "{tmp}\MagicMouse-uninstall.log"'));
    UninstallNeedsReboot := Code = 3010;
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    Settings := ExpandConstant('{userappdata}\MagicMouse');
    if DirExists(Settings) and not UninstallSilent() then
      if MsgBox('Also delete your MagicMouse settings?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Settings, True, True, True);
    if UninstallNeedsReboot and not UninstallSilent() then
      MsgBox('Restart Windows to finish removing the MagicMouse driver.', mbInformation, MB_OK);
  end;
end;
