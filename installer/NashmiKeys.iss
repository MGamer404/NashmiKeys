; NashmiKeys installer script (Inno Setup 6)
; Build with installer\build.ps1, or: ISCC.exe NashmiKeys.iss

#define MyAppName "NashmiKeys"
#define MyAppVersion "1.0"
#define MyAppPublisher "AbuNashmi"
#define MyAppExeName "NashmiKeys.exe"
#define MyAppId "{{7C4E2A91-5B3D-4F68-8E1A-9D2B6C5F0A73}"

; Uninstall keeps %APPDATA%\NashmiKeys (the user's hotkeys and settings).
; Set this to 0 to delete that folder during uninstall (adds [UninstallDelete]).
#define KEEP_USER_DATA 1

; Compile with /DFORCE_DOTNET_MISSING=1 to exercise the missing-runtime path.
#ifndef FORCE_DOTNET_MISSING
  #define FORCE_DOTNET_MISSING 0
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\NashmiKeys
DefaultGroupName=NashmiKeys
DisableProgramGroupPage=yes
DisableWelcomePage=no
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=output
OutputBaseFilename=NashmiKeys-1.0-Setup
Compression=lzma2/ultra64
SolidCompression=yes
SetupIconFile=..\src\NashmiKeys.App\app.ico
UninstallDisplayIcon={app}\NashmiKeys.exe
UninstallDisplayName=NashmiKeys 1.0
VersionInfoVersion=1.0.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Controller chord hotkeys for Windows
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
UsePreviousAppDir=yes
UsePreviousTasks=yes
; Keep detecting the pre-rename app while both builds may be on disk.
AppMutex=Local\NashmiKeys.SingleInstance.v1,Local\PadHotkeys.SingleInstance.v1
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppRunningError=Setup has detected that NashmiKeys is currently running.%n%nRight-click the NashmiKeys icon in the notification area (system tray), choose Exit, then click OK to continue.%n%nOr click Cancel to exit Setup.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "Additional tasks:"; Flags: checkedonce
Name: "autostart"; Description: "Start NashmiKeys with Windows (runs hidden in the tray)"; GroupDescription: "Additional tasks:"; Flags: checkedonce

[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{group}\NashmiKeys"; Filename: "{app}\NashmiKeys.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall NashmiKeys"; Filename: "{uninstallexe}"
Name: "{autodesktop}\NashmiKeys"; Filename: "{app}\NashmiKeys.exe"; Tasks: desktopicon; WorkingDir: "{app}"

[Run]
Filename: "{app}\NashmiKeys.exe"; Parameters: "--register-autostart --minimized"; StatusMsg: "Configuring NashmiKeys to start with Windows..."; Flags: runhidden waituntilterminated; Tasks: autostart
; runascurrentuser is required: postinstall entries default to the original
; (non-elevated) user, and NashmiKeys.exe requires elevation -> CreateProcess 740.
Filename: "{app}\NashmiKeys.exe"; Description: "Launch NashmiKeys"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; The app removes both the NashmiKeys task and any leftover PadHotkeys task.
; RunOnceId keeps one unregister entry in the uninstall log across upgrades, so
; an upgrade never duplicates the command and an upgrade never removes the task.
Filename: "{app}\NashmiKeys.exe"; Parameters: "--unregister-autostart"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveAutostart"

#if KEEP_USER_DATA == 0
[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\NashmiKeys"
#endif

[Code]
const
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';
  DotNetRegistryKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

function VersionMajor(const Version: String): Integer;
var
  I: Integer;
  Digits: String;
begin
  Digits := '';
  for I := 1 to Length(Version) do
  begin
    if (Version[I] >= '0') and (Version[I] <= '9') then
      Digits := Digits + Version[I]
    else
      Break;
  end;
  Result := StrToIntDef(Digits, -1);
end;

function HasDotNetDesktopRuntime(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  FindRec: TFindRec;
  Folder: String;
begin
  Result := False;

  if RegGetValueNames(HKLM64, DotNetRegistryKey, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if VersionMajor(Names[I]) >= 8 then
      begin
        Log('Found Microsoft.WindowsDesktop.App ' + Names[I] + ' in the .NET setup registry key.');
        Result := True;
        Exit;
      end;
    end;
  end;

  Folder := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(Folder + '\*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
         (FindRec.Name <> '.') and (FindRec.Name <> '..') and
         (VersionMajor(FindRec.Name) >= 8) then
      begin
        Log('Found Microsoft.WindowsDesktop.App ' + FindRec.Name + ' in ' + Folder + '.');
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
  Missing: Boolean;
begin
  Result := False;

#if FORCE_DOTNET_MISSING
  Missing := True;
#else
  Missing := not HasDotNetDesktopRuntime();
#endif

  if not Missing then
  begin
    Result := True;
    Exit;
  end;

  Log('NashmiKeys setup aborted: Microsoft Windows Desktop Runtime 8.0 (x64) or newer was not found.');
  Log('NashmiKeys needs the .NET 8 Desktop Runtime (or newer). Download: ' + DotNetDownloadUrl);

  if WizardSilent then
    Exit;

  if MsgBox('NashmiKeys needs the .NET 8 Desktop Runtime (or newer). Install it, then run this setup again.' + #13#10#13#10 +
            'Open the download page now?', mbError, MB_YESNO) = IDYES then
  begin
    if not ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
      Log('Could not open the download page. ShellExec error code: ' + IntToStr(ErrorCode));
  end;
end;
