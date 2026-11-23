; DriveGram installer (Inno Setup 6). Built by build-installer.cmd — do not compile by hand
; before `dotnet publish` has filled ..\publish.
;
; Per-user install (no admin): %LOCALAPPDATA%\Programs\DriveGram. Only WinFsp needs admin,
; and only when it is missing: its MSI asks for elevation by itself.

#define AppName "DriveGram"
#ifndef AppVersion
  #define AppVersion "1.0.2"
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
ShowLanguageDialog=auto

[Languages]
Name: "hebrew"; MessagesFile: "compiler:Languages\Hebrew.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
hebrew.Desktop=צור קיצור דרך בשולחן העבודה
hebrew.Launch=הפעל את DriveGram עכשיו
hebrew.InstallingWinFsp=מתקין את WinFsp (רכיב מערכת שנדרש לכונן)... ייתכן שיופיע חלון אישור של וינדוס: אשר אותו.
hebrew.StillRunning=DriveGram עדיין מסיים להעלות קבצים לטלגרם. הכוננים כבר מנותקים, ו־DriveGram ייסגר לבד כשההעלאה תסתיים (ההתקדמות מוצגת באייקון ליד השעון). אחר כך הפעל את ההתקנה שוב.
english.StillRunning=DriveGram is still finishing uploads to Telegram. The drives are already disconnected, and DriveGram closes by itself when the uploads finish (the tray icon shows the progress). Then run setup again.
hebrew.UninstallStillRunning=DriveGram עדיין מעלה קבצים. ההסרה ממשיכה: קבצים שלא הספיקו לעלות נשארים במחשב ויעלו אם תתקין שוב.
english.UninstallStillRunning=DriveGram is still uploading. Uninstall continues: files not yet uploaded stay on this PC and upload if you install again.
hebrew.Stopping=סוגר את DriveGram…
english.Stopping=Closing DriveGram…
hebrew.StoppingDetail=מחכה שקבצים שעוד לא עלו יסיימו לעלות לטלגרם. זה יכול לקחת כמה דקות. קבצים שפתוחים מהכונן באפליקציות (Word וכו׳) לא יהיה אפשר לשמור עד שהכונן יחזור.
english.StoppingDetail=Waiting for files not yet uploaded to finish uploading to Telegram. This can take a few minutes. Files open from the drive in apps (Word etc.) cannot be saved until the drive is back.
hebrew.CmdShortcut=DriveGram – שורת פקודה
english.CmdShortcut=DriveGram – command line
hebrew.CleanupAsk=למחוק גם את כל מה ש־DriveGram שמר במחשב הזה (הגדרות, עותקים מקומיים של קבצים, סיסמאות זכורות, יומני פעילות) ולהתנתק מטלגרם?%n%nהקבצים שבטלגרם לא נמחקים: אפשר לחבר את הכונן שוב אחרי התקנה מחדש, עם הסיסמה שלו.%n%nבחר "לא" אם אתה מתכוון להתקין שוב בקרוב.
english.CleanupAsk=Also delete everything DriveGram keeps on this PC (settings, local copies of files, remembered passwords, logs) and sign out of Telegram?%n%nThe files in Telegram are not deleted: the drive can be connected again after reinstalling, with its password.%n%nChoose "No" if you plan to reinstall soon.
hebrew.CleanupLocalOnly=⚠ יש במחשב הזה קבצים שעוד לא עלו לטלגרם, או שינויים שעוד לא נשלחו. אם תמחק עכשיו, הם יאבדו לצמיתות.%n%nלמחוק בכל זאת?
english.CleanupLocalOnly=⚠ This PC has files not yet uploaded to Telegram, or changes not yet sent. Deleting now loses them for good.%n%nDelete anyway?
hebrew.WinFspFailed=התקנת WinFsp לא הושלמה. הכונן לא יעבוד בלעדיו. אפשר להתקין אותו ידנית מהכתובת https://winfsp.dev/rel
english.Desktop=Create a desktop shortcut
english.Launch=Start DriveGram now
english.InstallingWinFsp=Installing WinFsp (a system component the drive needs)... Windows may ask for permission: approve it.
english.WinFspFailed=Installing WinFsp did not finish. The drive cannot work without it. You can install it by hand from https://winfsp.dev/rel

[Tasks]
; "Start with Windows" belongs to the app (setup wizard + tray menu); the uninstaller removes it
Name: "desktopicon"; Description: "{cm:Desktop}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\{#WinFspMsi}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWinFsp

[Icons]
Name: "{group}\DriveGram"; Filename: "{app}\drivegram-tray.exe"
Name: "{group}\{cm:CmdShortcut}"; Filename: "{cmd}"; Parameters: "/k ""cd /d ""{app}"" && drivegram help"""; IconFilename: "{app}\drivegram.ico"
Name: "{userdesktop}\DriveGram"; Filename: "{app}\drivegram-tray.exe"; Tasks: desktopicon


[Run]
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\{#WinFspMsi}"" /qb /norestart"; StatusMsg: "{cm:InstallingWinFsp}"; \
  Check: NeedsWinFsp; AfterInstall: CheckWinFspInstalled; Flags: waituntilterminated
Filename: "{app}\drivegram-tray.exe"; Description: "{cm:Launch}"; Flags: postinstall nowait skipifsilent
; a silent upgrade (e.g. an updater) stopped the running app: start it again
Filename: "{app}\drivegram-tray.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

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

{ Ask a running copy to finish uploads and unmount, then wait for it to exit (up to 10 minutes).
  Progress (setup only) shows that something is happening, instead of a window that looks frozen. }
procedure StopRunningCopy(Progress: TOutputProgressWizardPage);
var Code, Waited: Integer;
begin
  if not CheckForMutexes('DriveGramTrayRunning') then exit;
  { a copy running from somewhere else (e.g. a dev build): the user closes it }
  if not FileExists(ExpandConstant('{app}\drivegram.exe')) then exit;
  if Progress <> nil then
  begin
    Progress.SetText(CustomMessage('Stopping'), CustomMessage('StoppingDetail'));
    Progress.Show;
  end;
  try
    { `unmount` stops every drive (older versions exit on it); `quit` closes the tray app (0.5+) }
    Exec(ExpandConstant('{app}\drivegram.exe'), 'unmount', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec(ExpandConstant('{app}\drivegram.exe'), 'quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Waited := 0;
    if Progress = nil then
      { uninstall: let the program wait, so this window keeps responding meanwhile }
      Exec(ExpandConstant('{app}\drivegram.exe'), 'wait-quit', '', SW_HIDE, ewWaitUntilTerminated, Code)
    else
      while CheckForMutexes('DriveGramTrayRunning') and (Waited < 600) do
      begin
        Progress.SetProgress(Waited, 600);
        Sleep(1000);
        Waited := Waited + 1;
      end;
  finally
    if Progress <> nil then Progress.Hide;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopy(CreateOutputProgressPage(CustomMessage('Stopping'), ''));
  if CheckForMutexes('DriveGramTrayRunning') then
    Result := CustomMessage('StillRunning')
  else
    Result := '';
end;

{ Optionally remove everything DriveGram keeps on this PC and sign its accounts out of Telegram.
  Runs while drivegram.exe still exists (before files are removed). Never in a silent uninstall. }
procedure AskToCleanUp;
var Code: Integer;
    Exe: String;
begin
  Exe := ExpandConstant('{app}\drivegram.exe');
  if UninstallSilent or not FileExists(Exe) then exit;
  if not DirExists(ExpandConstant('{localappdata}\DriveGram')) then exit;
  if CheckForMutexes('DriveGramTrayRunning') then exit; { still finishing uploads: never delete under it }
  if MsgBox(CustomMessage('CleanupAsk'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
  begin
    { kept for a reinstall: remember that DriveGram started with Windows }
    if RegValueExists(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DriveGram') then
      SaveStringToFile(ExpandConstant('{localappdata}\DriveGram\autostart.reinstall'), '1', False);
    exit;
  end;
  { exit code 4: something exists only on this PC }
  { anything but "nothing only here" (0) warns: also a check that could not run }
  if (not Exec(Exe, 'uninstall-cleanup --check', '', SW_HIDE, ewWaitUntilTerminated, Code)) or (Code <> 0) then
    if MsgBox(CustomMessage('CleanupLocalOnly'), mbError, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
    begin
      if RegValueExists(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DriveGram') then
        SaveStringToFile(ExpandConstant('{localappdata}\DriveGram\autostart.reinstall'), '1', False);
      exit;
    end;
  Exec(Exe, 'uninstall-cleanup', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ drive letters still wearing DriveGram's icon/name would show any USB stick on that letter as "DriveGram" }
procedure RemoveDriveIcons;
var Letters: TArrayOfString;
    I: Integer;
    Icon, Key: String;
begin
  Key := 'Software\Classes\Applications\Explorer.exe\Drives';
  if not RegGetSubkeyNames(HKEY_CURRENT_USER, Key, Letters) then exit;
  for I := 0 to GetArrayLength(Letters) - 1 do
    if RegQueryStringValue(HKEY_CURRENT_USER, Key + '\' + Letters[I] + '\DefaultIcon', '', Icon) then
      if Pos('drivegram', Lowercase(Icon)) > 0 then
        RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, Key + '\' + Letters[I]);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Code: Integer;
begin
  { only after the user confirmed the uninstall: a "No" must never have stopped their drives }
  if CurUninstallStep = usUninstall then
  begin
    if CheckForMutexes('DriveGramTrayRunning') then
      UninstallProgressForm.StatusLabel.Caption := CustomMessage('StoppingDetail');
    StopRunningCopy(nil);
    UninstallProgressForm.StatusLabel.Caption := '';
    if CheckForMutexes('DriveGramTrayRunning') then
    begin
      { still uploading after 10 minutes: what is staged stays on disk (the profile is kept) }
      MsgBox(CustomMessage('UninstallStillRunning'), mbInformation, MB_OK);
      Exec('taskkill.exe', '/F /IM drivegram-tray.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    end
    else AskToCleanUp;
  end;
  { "Start with Windows" is set by the app, so the uninstaller removes it explicitly }
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DriveGram');
    RemoveDriveIcons;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Marker: String;
begin
  { reinstalled after an uninstall that kept the data: "start with Windows" comes back as it was }
  Marker := ExpandConstant('{localappdata}\DriveGram\autostart.reinstall');
  if (CurStep = ssPostInstall) and FileExists(Marker) then
  begin
    RegWriteStringValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DriveGram',
      '"' + ExpandConstant('{app}\drivegram-tray.exe') + '"');
    DeleteFile(Marker);
  end;
end;

{ right after the WinFsp installer itself (not before it, whatever the step order) }
procedure CheckWinFspInstalled;
begin
  if NeedsWinFsp then SuppressibleMsgBox(CustomMessage('WinFspFailed'), mbError, MB_OK, IDOK);
end;
