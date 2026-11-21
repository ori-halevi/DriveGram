; DriveGram installer (Inno Setup 6). Built by build-installer.cmd — do not compile by hand
; before `dotnet publish` has filled ..\publish.
;
; Per-user install (no admin): %LOCALAPPDATA%\Programs\DriveGram. Only WinFsp needs admin,
; and only when it is missing: its MSI asks for elevation by itself.

#define AppName "DriveGram"
#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#define WinFspMsi "winfsp-2.1.25156.msi"

[Setup]
AppId={{78F6D8C0-DCCF-4C3B-844F-0E1DF90AC0A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DriveGram
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=DriveGram-Setup-{#AppVersion}
SetupIconFile=..\assets\drivegram.ico
UninstallDisplayIcon={app}\drivegram-tray.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "hebrew"; MessagesFile: "compiler:Languages\Hebrew.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
hebrew.AutoStart=הפעל את DriveGram אוטומטית עם וינדוס
hebrew.Desktop=צור קיצור דרך בשולחן העבודה
hebrew.Launch=הפעל את DriveGram עכשיו
hebrew.InstallingWinFsp=מתקין את WinFsp (רכיב מערכת שנדרש לכונן)...
hebrew.WinFspFailed=התקנת WinFsp לא הושלמה. הכונן לא יעבוד בלעדיו. אפשר להתקין אותו ידנית מ-https://winfsp.dev/rel
english.AutoStart=Start DriveGram with Windows
english.Desktop=Create a desktop shortcut
english.Launch=Start DriveGram now
english.InstallingWinFsp=Installing WinFsp (a system component the drive needs)...
english.WinFspFailed=WinFsp was not installed. The drive cannot work without it. Install it from https://winfsp.dev/rel

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"
Name: "desktopicon"; Description: "{cm:Desktop}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\{#WinFspMsi}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWinFsp

[Icons]
Name: "{group}\DriveGram"; Filename: "{app}\drivegram-tray.exe"
Name: "{group}\DriveGram – שורת פקודה"; Filename: "{cmd}"; Parameters: "/k ""cd /d ""{app}"" && drivegram help"""; IconFilename: "{app}\drivegram.ico"
Name: "{userdesktop}\DriveGram"; Filename: "{app}\drivegram-tray.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DriveGram"; \
  ValueData: """{app}\drivegram-tray.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\{#WinFspMsi}"" /qb /norestart"; StatusMsg: "{cm:InstallingWinFsp}"; \
  Check: NeedsWinFsp; Flags: waituntilterminated
Filename: "{app}\drivegram-tray.exe"; Description: "{cm:Launch}"; Flags: postinstall nowait skipifsilent

[Code]
function WinFspInstalled: Boolean;
var Dir: String;
begin
  Result := RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', Dir) and DirExists(Dir);
end;

function NeedsWinFsp: Boolean;
begin
  Result := not WinFspInstalled;
end;

{ Ask a running copy to finish uploads and unmount, then wait for it to exit (up to 10 minutes). }
procedure StopRunningCopy;
var Code, Waited: Integer;
begin
  if not CheckForMutexes('DriveGramTrayRunning') then exit;
  { a copy running from somewhere else (e.g. a dev build): the user closes it }
  if not FileExists(ExpandConstant('{app}\drivegram.exe')) then exit;
  Exec(ExpandConstant('{app}\drivegram.exe'), 'unmount', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Waited := 0;
  while CheckForMutexes('DriveGramTrayRunning') and (Waited < 600) do
  begin
    Sleep(1000);
    Waited := Waited + 1;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopy;
  if CheckForMutexes('DriveGramTrayRunning') then
    Result := 'DriveGram עדיין פועל. סגור אותו מהאייקון ליד השעון (יציאה) והפעל את ההתקנה שוב.'
  else
    Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopRunningCopy;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and NeedsWinFsp then
    MsgBox(CustomMessage('WinFspFailed'), mbError, MB_OK);
end;
