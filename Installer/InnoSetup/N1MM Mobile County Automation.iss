; ===================================================================
;  N1MM Mobile County Automation - installer
;
;  Build with Inno Setup 6 or newer (run from the repo root):
;      iscc "Installer\InnoSetup\N1MM Mobile County Automation.iss"
;
;  Expects the published self-contained single-file exe at:
;      publish\N1MMMobileCountyAutomation.exe
;  (build.ps1, at the repo root, puts it there)
; ===================================================================

#define MyAppName "N1MM Mobile County Automation"
#define MyAppPublisher "K5JSG"
#define MyAppURL "https://github.com/K5JSG/N1MM-Mobile-County-Automation"
#define MyAppExeName "N1MMMobileCountyAutomation.exe"
; This installer's AppId (see [Setup]), also used by [Code] to tell its own
; Installed Apps entry apart from older copies.
#define MyAppId "{{563E2AA0-4281-441F-8BE0-5589501E7836}"

; Overridable from the command line: iscc /DMyAppVersion=1.1.0 ...
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
; Keep this GUID stable forever: it is how Windows recognises an upgrade of
; the same product rather than a second installation.
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}

DefaultDirName={autopf}\{#MyAppPublisher}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
LicenseFile=..\..\License.txt
SetupIconFile=..\..\logo.ico

; Writing to Program Files needs admin - the app itself does not require
; elevation to run (all its own data lives under %LocalAppData%), just to
; install.
PrivilegesRequired=admin

OutputDir=..\..\dist
OutputBaseFilename={#MyAppName} Setup {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Windows 10 1809 or newer
MinVersion=10.0.17763

; Offer to shut the app down instead of demanding a reboot
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
    GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "launcher"; Description: "Add a Start menu shortcut that starts N1MM+ and {#MyAppName} together"; \
    GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "Start N1MM and County Automation.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme skipifsourcedoesntexist
Source: "..\..\License.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; The desktop shortcut must come BEFORE the Start menu ones. When it was
; created after them, every upgrade made Explorer drop the desktop icon into
; the next free spot instead of leaving it where the user had put it.
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Start N1MM+ and {#MyAppName}"; Filename: "{app}\Start N1MM and County Automation.bat"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: launcher
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; \
    Flags: postinstall nowait skipifsilent

[Code]

// Updates install IN PLACE over the existing copy (same AppId), so Installed
// Apps keeps one entry that just shows the new version number, and the
// desktop and Start menu shortcuts stay where they are. Before the new files
// go in:
//   - any OTHER installed copy of this app (an entry registered somewhere
//     other than this installer's own) is silently uninstalled;
//   - the program folder is emptied (all but the uninstaller), so no file
//     that an older version shipped and this one dropped is left behind.
// All of the app's own data lives in %LocalAppData% and is never touched.

const
  UninstallKeyRoot = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';

var
  OldInstallDirs: TArrayOfString;

function IsOurProductName(const Name: String): Boolean;
begin
  Result := CompareText(Name, '{#MyAppName}') = 0;
end;

// This installer's own Installed Apps entry: the one an upgrade updates in
// place instead of removing. Admin installs in 64-bit mode register it under
// HKLM's 64-bit view.
function IsOwnEntry(RootKey: Integer; const SubkeyName: String): Boolean;
begin
  Result := (RootKey = HKLM64) and
            (CompareText(SubkeyName, ExpandConstant('{#MyAppId}_is1')) = 0);
end;

function SameDir(const A, B: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(A), RemoveBackslashUnlessRoot(B)) = 0;
end;

procedure RememberOldInstallDir(const Dir: String);
var
  N: Integer;
begin
  if Dir = '' then Exit;
  N := GetArrayLength(OldInstallDirs);
  SetArrayLength(OldInstallDirs, N + 1);
  OldInstallDirs[N] := Dir;
end;

// Only ever touches a folder that is clearly this app's own (named after the
// product), never some general-purpose folder the user may have picked on
// the directory page.
function IsOurAppFolder(const Dir: String): Boolean;
begin
  Result := (Dir <> '') and DirExists(Dir) and
            (IsOurProductName(ExtractFileName(Dir)));
end;

procedure DeleteAppFolder(Dir: String);
begin
  Dir := RemoveBackslashUnlessRoot(Dir);
  if IsOurAppFolder(Dir) then
  begin
    DelTree(Dir, True, True, True);
    // The K5JSG publisher folder above it - RemoveDir only succeeds if empty
    RemoveDir(ExtractFileDir(Dir));
  end;
end;

// An Inno uninstaller re-launches itself from %TEMP% and deletes its own
// unins*.exe as the very last step, so this confirms it has really finished
// before the new files go in.
procedure WaitForFileGone(const FileName: String; TimeoutMs: Integer);
begin
  while FileExists(FileName) and (TimeoutMs > 0) do
  begin
    Sleep(250);
    TimeoutMs := TimeoutMs - 250;
  end;
end;

// Silently uninstalls every installed copy of this app registered under
// RootKey, except this installer's own entry (see IsOwnEntry). Returns an
// error message, or '' if everything went fine.
function UninstallOldVersions(RootKey: Integer): String;
var
  Names: TArrayOfString;
  I, ResultCode: Integer;
  Key, DisplayName, Publisher, Location, Uninstaller: String;
  IsMsi: Cardinal;
begin
  Result := '';
  if not RegGetSubkeyNames(RootKey, UninstallKeyRoot, Names) then Exit;

  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    Key := UninstallKeyRoot + '\' + Names[I];
    if not IsOwnEntry(RootKey, Names[I]) and
       RegQueryStringValue(RootKey, Key, 'DisplayName', DisplayName) and
       IsOurProductName(DisplayName) and
       RegQueryStringValue(RootKey, Key, 'Publisher', Publisher) and
       (CompareText(Publisher, '{#MyAppPublisher}') = 0) then
    begin
      if RegQueryStringValue(RootKey, Key, 'InstallLocation', Location) then
        RememberOldInstallDir(Location);

      if RegQueryDWordValue(RootKey, Key, 'WindowsInstaller', IsMsi) and (IsMsi = 1) then
      begin
        // An MSI build: the subkey name is its ProductCode, and it may not
        // have recorded InstallLocation, so fall back to the usual folder.
        RememberOldInstallDir(ExpandConstant('{autopf}\{#MyAppPublisher}\') + DisplayName);
        if not Exec(ExpandConstant('{sys}\msiexec.exe'),
                    '/x ' + Names[I] + ' /qn /norestart',
                    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
          ResultCode := -1;
        // 1605 = already gone, 3010 = done but wants a reboot
        if (ResultCode <> 0) and (ResultCode <> 1605) and (ResultCode <> 3010) then
        begin
          Result := Format('%s could not be removed automatically (error %d).', [DisplayName, ResultCode]);
          Exit;
        end;
      end
      else if RegQueryStringValue(RootKey, Key, 'UninstallString', Uninstaller) then
      begin
        Uninstaller := RemoveQuotes(Uninstaller);
        RememberOldInstallDir(ExtractFileDir(Uninstaller));
        if FileExists(Uninstaller) then
        begin
          if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
                      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
            ResultCode := -1;
          if ResultCode <> 0 then
          begin
            Result := Format('%s could not be removed automatically (error %d).', [DisplayName, ResultCode]);
            Exit;
          end;
          WaitForFileGone(Uninstaller, 60000);
        end;
        // An entry whose uninstaller is missing (folder deleted by hand)
        // would otherwise linger in Installed Apps forever.
        if RegKeyExists(RootKey, Key) then
          RegDeleteKeyIncludingSubkeys(RootKey, Key);
      end;
    end;
  end;
end;

// Removes every other installed copy (see UninstallOldVersions) and whatever
// their uninstallers left behind. Returns an error message for
// PrepareToInstall, or '' if everything went fine.
function RemoveOtherCopies(): String;
var
  I: Integer;
begin
  Result := UninstallOldVersions(HKLM64);
  if Result = '' then Result := UninstallOldVersions(HKLM32);
  if Result = '' then Result := UninstallOldVersions(HKCU);

  if Result <> '' then
  begin
    Result := Result + #13#10#13#10 +
      'Please uninstall it from Settings > Apps > Installed apps, then run this setup again.';
    Exit;
  end;

  // Files the old uninstallers didn't remove themselves. The folder being
  // upgraded is left to CleanAppFolder, which keeps its uninstaller.
  for I := 0 to GetArrayLength(OldInstallDirs) - 1 do
    if not SameDir(OldInstallDirs[I], ExpandConstant('{app}')) then
      DeleteAppFolder(OldInstallDirs[I]);
end;

// What the folder cleanup below leaves in place: the uninstaller
// (unins000.exe/.dat/.msg), which the upgraded copy carries on using.
function KeepOnUpgrade(const Name: String): Boolean;
begin
  Result := (CompareText(Copy(Name, 1, 5), 'unins') = 0);
end;

// Empties the program folder just before the new version's files are copied
// in, so nothing an older version installed and this one no longer ships is
// left behind.
procedure CleanAppFolder();
var
  Dir: String;
  FindRec: TFindRec;
begin
  Dir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if not IsOurAppFolder(Dir) then Exit;

  if FindFirst(Dir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') and not KeepOnUpgrade(FindRec.Name) then
        begin
          if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
            DelTree(Dir + '\' + FindRec.Name, True, True, True)
          else
            DeleteFile(Dir + '\' + FindRec.Name);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanAppFolder();
end;

// Stop a running instance before installing or uninstalling, otherwise the
// exe is locked and the file copy fails.
procedure StopRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'),
       '/C taskkill /F /IM "{#MyAppExeName}" >nul 2>&1',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := RemoveOtherCopies();
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;
