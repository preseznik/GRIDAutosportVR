; GRID Autosport VR installer, built by tools/Package.ps1 with Inno Setup 7 (ISCC).
; Modelled on DiRT3VR's: the launcher and its payload go into <game>\GRIDAutosportVR. Game files
; are changed only by the launcher during a VR session and restored when it ends.
#ifndef Stage
  #error Stage must point to the staged GRIDAutosportVR package folder
#endif
#ifndef PackageVersion
  #error PackageVersion must be supplied by tools/Package.ps1
#endif
#ifndef ReleaseNotes
  #error ReleaseNotes must be supplied by tools/Package.ps1
#endif
[Setup]
AppId={{536431F8-5C16-431A-93EB-21D3CCFC467E}
AppName=GRID Autosport VR
AppVersion={#PackageVersion}
AppVerName=GRID Autosport VR {#PackageVersion}
AppPublisher=Bohloney
AppPublisherURL=https://github.com/preseznik/GRIDAutosportVR
DefaultDirName={code:DefaultGameDir}
AppendDefaultDirName=no
; Keep the folder choice available when upgrading an existing install.
DisableDirPage=no
DisableProgramGroupPage=yes
DirExistsWarning=no
PrivilegesRequired=admin
; Steam libraries outside Program Files can install per user without elevation (/CURRENTUSER).
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallFilesDir={app}\GRIDAutosportVR\uninstall
UninstallDisplayIcon={app}\GRIDAutosportVR\GRIDAutosportVR.exe
UninstallDisplayName=GRID Autosport VR
OutputDir={#Stage}\..
OutputBaseFilename=GRIDAutosportVR-{#PackageVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
AppMutex=Local\GRIDAutosportVR-255220
SetupLogging=yes
SetupIconFile=..\launcher\assets\logo.ico
InfoAfterFile={#ReleaseNotes}

[Messages]
WizardSelectDir=Browse GRID Autosport Installation
SelectDirDesc=Keep the detected folder or choose a different GRID Autosport installation.
SelectDirBrowseLabel=Click Next to use this folder, or Browse to choose another installation.
BrowseDialogTitle=Browse GRID Autosport Installation
BrowseDialogLabel=Select the GRID Autosport Steam folder containing GRIDAutosport.exe.

[Files]
Source: "{#Stage}\GRIDAutosportVR.exe"; DestDir: "{app}\GRIDAutosportVR"; Flags: ignoreversion
Source: "{#Stage}\*"; DestDir: "{app}\GRIDAutosportVR"; Excludes: "\GRIDAutosportVR.exe"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{autoprograms}\GRID Autosport VR"; Filename: "{app}\GRIDAutosportVR\GRIDAutosportVR.exe"; WorkingDir: "{app}\GRIDAutosportVR"
Name: "{autodesktop}\GRID Autosport VR"; Filename: "{app}\GRIDAutosportVR\GRIDAutosportVR.exe"; WorkingDir: "{app}\GRIDAutosportVR"; Tasks: desktopicon

[Run]
Filename: "{app}\GRIDAutosportVR\GRIDAutosportVR.exe"; Description: "Open GRID Autosport VR"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  SuggestedGame: String;

function DefaultGameDir(Param: String): String;
begin
  if SuggestedGame <> '' then Result := SuggestedGame
  else Result := ExpandConstant('{commonpf32}\Steam\steamapps\common\GRID Autosport');
end;

procedure InitializeWizard();
var
  ResultCode, I: Integer;
  ExplicitDirectory: Boolean;
  Discovered: AnsiString;
begin
  ExplicitDirectory := False;
  for I := 1 to ParamCount do
    if CompareText(Copy(ParamStr(I), 1, 5), '/DIR=') = 0 then ExplicitDirectory := True;
  WizardForm.SelectDirLabel.Caption := 'Select the existing GRID Autosport Steam folder containing GRIDAutosport.exe. No game files or saves are included.';
  ExtractTemporaryFile('GRIDAutosportVR.exe');
  if Exec(ExpandConstant('{tmp}\GRIDAutosportVR.exe'), '--discover "' + ExpandConstant('{tmp}\game-path.txt') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    if LoadStringFromFile(ExpandConstant('{tmp}\game-path.txt'), Discovered) then
    begin
      SuggestedGame := UTF8Decode(Discovered);
      if not ExplicitDirectory and (SuggestedGame <> '') and not FileExists(WizardDirValue() + '\GRIDAutosport.exe') then
        WizardForm.DirEdit.Text := SuggestedGame;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ErrorText: AnsiString;
begin
  Result := '';
  if CheckForMutexes('Local\GRIDAutosportVR-255220') then
  begin
    Result := 'Close GRID Autosport and its VR session before installing.';
    exit;
  end;
  // Checks the Steam installation; refuses while the game runs or a VR session's files are staged.
  if not Exec(ExpandConstant('{tmp}\GRIDAutosportVR.exe'), '--check-install --game "' + WizardDirValue() + '" --error-log "' + ExpandConstant('{tmp}\install-error.txt') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    Result := 'Game-folder check failed. No files were installed.';
    if LoadStringFromFile(ExpandConstant('{tmp}\install-error.txt'), ErrorText) then Result := Result + #13#10 + UTF8Decode(ErrorText);
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  Launcher: String;
  ErrorText: AnsiString;
begin
  Result := False;
  if CheckForMutexes('Local\GRIDAutosportVR-255220') then
  begin
    MsgBox('Close GRID Autosport and let GRID Autosport VR restore the game files before uninstalling.', mbError, MB_OK);
    exit;
  end;
  Launcher := ExpandConstant('{app}\GRIDAutosportVR\GRIDAutosportVR.exe');
  if FileExists(Launcher) and (not Exec(Launcher, '--check-uninstall --game "' + ExpandConstant('{app}') + '" --error-log "' + ExpandConstant('{tmp}\uninstall-error.txt') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0)) then
  begin
    if LoadStringFromFile(ExpandConstant('{tmp}\uninstall-error.txt'), ErrorText) then
      MsgBox(UTF8Decode(ErrorText) + #13#10 + 'GRID Autosport VR was not uninstalled. Recovery data was preserved.', mbError, MB_OK)
    else
      MsgBox('GRID Autosport VR could not confirm that the game files were restored. It was not uninstalled; recovery data was preserved.', mbError, MB_OK);
    exit;
  end;
  Result := True;
end;
