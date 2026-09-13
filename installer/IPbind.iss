#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef ExePath
  #error ExePath must be supplied by Build-Installer.ps1
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{A42A329A-EDE5-4D63-B2C7-92947FE79E14}
AppName=IPbind
AppVersion={#AppVersion}
AppVerName=IPbind {#AppVersion}
AppPublisher=Andy Rostad
AppPublisherURL=https://github.com/arostad
AppSupportURL=https://github.com/arostad/IPbind/issues
AppUpdatesURL=https://github.com/arostad/IPbind/releases/tag/latest
AppComments=IPbind is released under the MIT License.
AppReadmeFile={app}\LICENSE.txt
VersionInfoCompany=Andy Rostad
VersionInfoDescription=IPbind per-user installer
VersionInfoProductName=IPbind
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) 2026 Andy Rostad. MIT License.
DefaultDirName={localappdata}\Programs\IPbind
DefaultGroupName=IPbind
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=IPbind-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayName=IPbind
UninstallDisplayIcon={app}\IPbind.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#ExePath}"; DestDir: "{app}"; DestName: "IPbind.exe"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\IPbind"; Filename: "{app}\IPbind.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\IPbind"; Filename: "{app}\IPbind.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\IPbind.exe"; Description: "Launch IPbind"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveAppDataCheckBox: TNewCheckBox;

procedure InitializeUninstallProgressForm;
begin
  RemoveAppDataCheckBox := TNewCheckBox.Create(UninstallProgressForm);
  RemoveAppDataCheckBox.Parent := UninstallProgressForm;
  RemoveAppDataCheckBox.Left := UninstallProgressForm.ProgressBar.Left;
  RemoveAppDataCheckBox.Top :=
    UninstallProgressForm.ProgressBar.Top +
    UninstallProgressForm.ProgressBar.Height + ScaleY(8);
  RemoveAppDataCheckBox.Width := UninstallProgressForm.ProgressBar.Width;
  RemoveAppDataCheckBox.Height := ScaleY(34);
  RemoveAppDataCheckBox.Caption :=
    'Remove all application data (saved IP lists, settings, and update cache)';
  RemoveAppDataCheckBox.Checked := False;
  RemoveAppDataCheckBox.Visible := not UninstallSilent;

  UninstallProgressForm.ClientHeight :=
    UninstallProgressForm.ClientHeight + ScaleY(42);
  UninstallProgressForm.CancelButton.Top :=
    UninstallProgressForm.CancelButton.Top + ScaleY(42);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and
     (not UninstallSilent) and RemoveAppDataCheckBox.Checked then
  begin
    DelTree(ExpandConstant('{userappdata}\IPbind'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\IPbind'), True, True, True);
  end;
end;
