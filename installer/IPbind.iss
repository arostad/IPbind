#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef ExePath
  #error ExePath must be supplied by Build-Installer.ps1
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef Channel
  #define Channel "prod"
#endif

; Channel "beta" builds the separate IPbind Beta app: own AppId, name, folder,
; shortcuts, uninstall entry and settings folder, so it installs side by side
; with IPbind and never overwrites it. Prod values are unchanged.
#if Channel == "beta"
  #define AppTitle "IPbind Beta"
  #define AppIdValue "{{3E0BECC0-9CDB-47D6-BFFB-5A6B0DEEFC27}"
  #define ExeName "IPbind Beta.exe"
  #define SetupIcon "..\app-beta.ico"
  #define OutputName "IPbind-Installer-v" + AppVersion + "-beta"
  #define ReleaseTag "beta"
#else
  #define AppTitle "IPbind"
  #define AppIdValue "{{A42A329A-EDE5-4D63-B2C7-92947FE79E14}"
  #define ExeName "IPbind.exe"
  #define SetupIcon "..\app.ico"
  #define OutputName "IPbind-Installer-v" + AppVersion
  #define ReleaseTag "latest"
#endif

[Setup]
AppId={#AppIdValue}
AppName={#AppTitle}
AppVersion={#AppVersion}
AppVerName={#AppTitle} {#AppVersion}
AppPublisher=Andy Rostad
AppPublisherURL=https://github.com/arostad
AppSupportURL=https://github.com/arostad/IPbind/issues
AppUpdatesURL=https://github.com/arostad/IPbind/releases/tag/{#ReleaseTag}
AppComments={#AppTitle} is released under the MIT License.
AppReadmeFile={app}\LICENSE.txt
VersionInfoCompany=Andy Rostad
VersionInfoDescription={#AppTitle} per-user installer
VersionInfoProductName={#AppTitle}
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) 2026 Andy Rostad. MIT License.
DefaultDirName={localappdata}\Programs\{#AppTitle}
DefaultGroupName={#AppTitle}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile={#SetupIcon}
UninstallDisplayName={#AppTitle}
UninstallDisplayIcon={app}\{#ExeName}
LicenseFile=..\LICENSE
InfoBeforeFile=privacy.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WizardInfoBefore={#AppTitle} privacy
InfoBeforeLabel=Please read how {#AppTitle} uses the network before continuing.

[Tasks]
; Checked by default (also for silent installs). On upgrade, Inno restores the
; previous choice (UsePreviousTasks=yes is the default). Saved to update-check.txt.
Name: "autoupdatecheck"; Description: "Check for updates at launch"; GroupDescription: "Updates (when off, check manually from About):"

[Files]
Source: "{#ExePath}"; DestDir: "{app}"; DestName: "{#ExeName}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppTitle}"; Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#AppTitle}"; Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"

[Run]
; Verb: open + shellexec = ShellExecute so UAC can prompt from unelevated Setup
; (Inno Setup 6.7 renamed the old shellexecute flag to shellexec)
Filename: "{app}\{#ExeName}"; Description: "Launch {#AppTitle}"; Flags: nowait postinstall skipifsilent shellexec; Verb: open

[Code]
var
  RemoveAppDataCheckBox: TNewCheckBox;

function RemoveDataCommandLineRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    if CompareText(ParamStr(I), '/REMOVEDATA=1') = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  SettingsDir: String;
  Mode: String;
begin
  if CurStep = ssPostInstall then
  begin
    SettingsDir := ExpandConstant('{userappdata}\{#AppTitle}');
    if WizardIsTaskSelected('autoupdatecheck') then
      Mode := 'auto'
    else
      Mode := 'manual';
    if not ForceDirectories(SettingsDir) or
       not SaveStringToFile(SettingsDir + '\update-check.txt', Mode, False) then
      Log('Could not save update-check.txt in ' + SettingsDir);
  end;
end;

procedure InitializeUninstallProgressForm;
begin
  RemoveAppDataCheckBox := TNewCheckBox.Create(UninstallProgressForm);
  RemoveAppDataCheckBox.Parent := UninstallProgressForm.InstallingPage;
  RemoveAppDataCheckBox.Left := UninstallProgressForm.ProgressBar.Left;
  RemoveAppDataCheckBox.Top :=
    UninstallProgressForm.ProgressBar.Top +
    UninstallProgressForm.ProgressBar.Height + ScaleY(8);
  RemoveAppDataCheckBox.Width := UninstallProgressForm.ProgressBar.Width;
  RemoveAppDataCheckBox.Height := ScaleY(34);
  RemoveAppDataCheckBox.Caption :=
    'Remove all application data (saved IP lists, settings, and update cache)';
  RemoveAppDataCheckBox.Checked := RemoveDataCommandLineRequested;
  RemoveAppDataCheckBox.Visible := not UninstallSilent;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and
     RemoveAppDataCheckBox.Checked then
  begin
    DelTree(ExpandConstant('{userappdata}\{#AppTitle}'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\{#AppTitle}'), True, True, True);
  end;
end;
