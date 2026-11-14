#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef AppDisplayVersion
  #define AppDisplayVersion AppVersion
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
  #define AppIdGuid "3E0BECC0-9CDB-47D6-BFFB-5A6B0DEEFC27"
  #define AppIdValue "{{3E0BECC0-9CDB-47D6-BFFB-5A6B0DEEFC27}"
  #define ExeName "IPbind Beta.exe"
  #define SetupIcon "..\app-beta.ico"
  #define OutputName "IPbind-Installer-v" + AppVersion + "-beta"
  #define ReleaseTag "beta"
#else
  #define AppTitle "IPbind"
  #define AppIdGuid "A42A329A-EDE5-4D63-B2C7-92947FE79E14"
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
AppVerName={#AppTitle} {#AppDisplayVersion}
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
Name: "autoupdatecheck"; Description: "Check for updates at launch (recommended)"; GroupDescription: "Updates (you can change this later in About):"

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
  RemoveDataRequested: Boolean;
  RemoveDataCheckBox: TNewCheckBox;

{ Inno's modern-style TNewCheckListBox draws the glyph at Offset (default 4,
  not DPI-scaled). At high scale the left edge of every checkbox square is
  clipped. TNewCheckBox sized exactly to the box also clips the themed glyph.
  Fix once for Setup and Uninstall: scale the list offset, and give standalone
  checkboxes a few pixels of padding around the square. }
procedure UnclipInnoChecks(Root: TWinControl);
var
  I, Pad, Need: Integer;
  C: TControl;
  Box: TNewCheckBox;
  List: TNewCheckListBox;
begin
  if Root = nil then
    Exit;
  Need := ScaleX(8);
  Pad := ScaleX(4);
  if Root is TNewCheckListBox then
  begin
    List := TNewCheckListBox(Root);
    if List.Offset < Need then
      List.Offset := Need;
  end
  else if Root is TNewCheckBox then
  begin
    Box := TNewCheckBox(Root);
    { Only expand once (Tag marks a box we already padded). }
    if Box.Tag <> 1391 then
    begin
      Box.Left := Box.Left - Pad;
      Box.Top := Box.Top - Pad;
      Box.Width := Box.Width + (2 * Pad);
      Box.Height := Box.Height + (2 * Pad);
      Box.Tag := 1391;
    end;
  end;
  for I := 0 to Root.ControlCount - 1 do
  begin
    C := Root.Controls[I];
    if C is TWinControl then
      UnclipInnoChecks(TWinControl(C));
  end;
end;

procedure InitializeWizard;
begin
  UnclipInnoChecks(WizardForm);
end;

function CmdLineParamExists(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    if CompareText(ParamStr(I), Value) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function RemoveDataCommandLineRequested: Boolean;
begin
  Result := CmdLineParamExists('/REMOVEDATA=1');
end;

function UninstallRegKey: String;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#AppIdGuid}' + '}_is1';
end;

{ Apps & Features should open our pre-uninstall prompt, not Inno's Yes/No
  box. /SILENT hides ConfirmUninstall and the "successfully removed" message,
  but still shows the progress bar. QuietUninstallString is left alone so
  winget and /SILENT+/VERYSILENT stay truly silent. }
procedure PatchUninstallString;
var
  Value: String;
begin
  if not RegQueryStringValue(HKCU, UninstallRegKey, 'UninstallString', Value) then
  begin
    Log('Could not read UninstallString');
    Exit;
  end;
  if Pos('/IPBINDPROMPT', Uppercase(Value)) > 0 then
    Exit;
  Value := Value + ' /SILENT /IPBINDPROMPT';
  if not RegWriteStringValue(HKCU, UninstallRegKey, 'UninstallString', Value) then
    Log('Could not update UninstallString');
end;

function KeepDhcpRecordExists: Boolean;
var
  FindRec: TFindRec;
  Pattern: String;
begin
  Result := False;
  Pattern := ExpandConstant('{userappdata}\{#AppTitle}\keep-dhcp-*.txt');
  if FindFirst(Pattern, FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure DeletePattern(const Pattern: String);
var
  FindRec: TFindRec;
  Dir: String;
begin
  Dir := ExtractFilePath(Pattern);
  if FindFirst(Pattern, FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
          DeleteFile(Dir + FindRec.Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ Everything this channel writes under %APPDATA%\<AppTitle> and
  %LOCALAPPDATA%\<AppTitle>, leftover update helpers, and temp netsh
  scripts. The other channel's folders and the Inno uninstall key are
  left alone. }

#if Channel == "beta"
procedure WriteBetaUninstallChoice(Proceeded: Boolean; RemoveData: Boolean);
var
  Dir, Path, Line: String;
begin
  try
    Dir := ExpandConstant('{localappdata}\{#AppTitle}\logs');
    if not DirExists(Dir) then
      ForceDirectories(Dir);
    Path := Dir + '\ipbind-beta-' + GetDateTimeString('yyyy-mm-dd', '-', '-') + '.log';
    Line := GetDateTimeString('yyyy-mm-dd hh:nn:ss.zzz', '-', ':') +
      ' INFO Uninstall prompt: proceed=' + IntToStr(Ord(Proceeded)) +
      ' remove-data=' + IntToStr(Ord(RemoveData)) + #13#10;
    SaveStringToFile(Path, Line, True);
  except
  end;
end;
#else
procedure WriteBetaUninstallChoice(Proceeded: Boolean; RemoveData: Boolean);
begin
  { Prod never writes a log file. }
end;
#endif

procedure DeleteAppData;
var
  AppDir, TempDir: String;
begin
  DelTree(ExpandConstant('{userappdata}\{#AppTitle}'), True, True, True);
  DelTree(ExpandConstant('{localappdata}\{#AppTitle}'), True, True, True);
  TempDir := GetTempDir;
  DeletePattern(TempDir + 'ipbind-*.netsh');
  DeletePattern(TempDir + '.IPbind-update-helper-*.exe');
  AppDir := ExpandConstant('{app}');
  if DirExists(AppDir) then
  begin
    DeletePattern(AppDir + '\.IPbind-update-helper-*.exe');
    DeletePattern(AppDir + '\.IPbind-write-*.tmp');
    DeletePattern(AppDir + '\{#ExeName}.*.new');
    DeleteFile(AppDir + '\update-error.txt');
  end;
  Log('Removed {#AppTitle} data from this user profile');
end;

procedure DataLabelClick(Sender: TObject);
begin
  if RemoveDataCheckBox <> nil then
    RemoveDataCheckBox.Checked := not RemoveDataCheckBox.Checked;
end;

function ButtonWidthFor(Owner: TWinControl; const S: String): Integer;
var
  Tmp: TNewStaticText;
begin
  Tmp := TNewStaticText.Create(Owner);
  try
    Tmp.Parent := Owner;
    Tmp.Left := -ScaleX(2000);
    Tmp.AutoSize := True;
    Tmp.Caption := S;
    Result := Tmp.Width + ScaleX(28);
  finally
    Tmp.Free;
  end;
  if Result < ScaleX(90) then
    Result := ScaleX(90);
end;

{ Hidden AutoSize label used only to measure wrapped height, including descent. }
function MeasureWrappedHeight(Owner: TWinControl; const S: String; W: Integer): Integer;
var
  Tmp: TNewStaticText;
begin
  Tmp := TNewStaticText.Create(Owner);
  try
    Tmp.Parent := Owner;
    Tmp.Left := -ScaleX(2000);
    Tmp.Width := W;
    Tmp.WordWrap := True;
    Tmp.AutoSize := True;
    Tmp.Caption := S;
    Result := Tmp.Height;
  finally
    Tmp.Free;
  end;
end;

{ Builds the prompt's controls on Form and returns the client height the
  content needs. Run once on a throw-away form to measure, then on the real
  form, so the dialog is exactly as tall as its text (no empty band). }
function BuildUninstallPrompt(Form: TSetupForm; ShowWarn: Boolean; FormH: Integer): Integer;
var
  Title, Body, Warn, DataLabel: TNewStaticText;
  UninstallBtn, CancelBtn: TNewButton;
  MarginX, MarginY, InnerW, Y, BoxSize, LabelLeft, LabelW, NeedH, LineH: Integer;
  BtnW1, BtnW2, BtnH, BtnY: Integer;
begin
  MarginX := ScaleX(20);
  MarginY := ScaleY(16);
  InnerW := Form.ClientWidth - (MarginX * 2);
  Y := MarginY;

  Title := TNewStaticText.Create(Form);
  Title.Parent := Form;
  Title.Left := MarginX;
  Title.Top := Y;
  Title.Width := InnerW;
  Title.AutoSize := True;
  Title.WordWrap := True;
  Title.Font.Name := 'Segoe UI';
  Title.Font.Size := 11;
  Title.Font.Style := [fsBold];
  Title.Caption := 'Uninstall {#AppTitle}?';
  Y := Title.Top + Title.Height + ScaleY(10);

  Body := TNewStaticText.Create(Form);
  Body.Parent := Form;
  Body.Left := MarginX;
  Body.Top := Y;
  Body.Width := InnerW;
  Body.AutoSize := True;
  Body.WordWrap := True;
  Body.Caption := 'This removes the app and its shortcuts. Saved IP lists and settings stay on this PC unless you also choose to remove them.';
  Y := Body.Top + Body.Height + ScaleY(12);

  if ShowWarn then
  begin
    Warn := TNewStaticText.Create(Form);
    Warn.Parent := Form;
    Warn.Left := MarginX;
    Warn.Top := Y;
    Warn.Width := InnerW;
    Warn.AutoSize := True;
    Warn.WordWrap := True;
    Warn.Caption := '{#AppTitle} still has a Keep DHCP record. If those addresses are still applied, use Return to DHCP first. Uninstall will also delete the record used to remove them.';
    Y := Warn.Top + Warn.Height + ScaleY(14);
  end;

  BoxSize := ScaleY(18);
  if BoxSize < ScaleX(16) then
    BoxSize := ScaleX(16);
  LabelLeft := MarginX + BoxSize + ScaleX(8);
  LabelW := Form.ClientWidth - MarginX - LabelLeft;
  NeedH := MeasureWrappedHeight(Form, 'Also remove all {#AppTitle} data (saved IP lists, settings, and the setup reminder)', LabelW);
  LineH := MeasureWrappedHeight(Form, 'Ag', LabelW);
  { Extra room so descenders are not clipped, at every DPI. }
  if NeedH < BoxSize then
    NeedH := BoxSize;
  NeedH := NeedH + ScaleY(4);

  { The box is centred on the first line of its caption. }
  RemoveDataCheckBox := TNewCheckBox.Create(Form);
  RemoveDataCheckBox.Parent := Form;
  RemoveDataCheckBox.Caption := '';
  RemoveDataCheckBox.Left := MarginX;
  RemoveDataCheckBox.Top := Y + ((LineH - BoxSize) div 2);
  RemoveDataCheckBox.Width := BoxSize;
  RemoveDataCheckBox.Height := BoxSize;
  RemoveDataCheckBox.Checked := RemoveDataRequested;
  RemoveDataCheckBox.TabOrder := 0;
  UnclipInnoChecks(Form);

  DataLabel := TNewStaticText.Create(Form);
  DataLabel.Parent := Form;
  DataLabel.Left := LabelLeft;
  DataLabel.Top := Y;
  DataLabel.Width := LabelW;
  DataLabel.AutoSize := True;
  DataLabel.WordWrap := True;
  DataLabel.Caption := 'Also remove all {#AppTitle} data (saved IP lists, settings, and the setup reminder)';
  DataLabel.OnClick := @DataLabelClick;
  Y := Y + NeedH + ScaleY(20);

  BtnH := ScaleY(28);
  BtnW1 := ButtonWidthFor(Form, 'Uninstall');
  BtnW2 := ButtonWidthFor(Form, 'Cancel');
  BtnY := Y;
  if FormH > 0 then
    BtnY := FormH - MarginY - BtnH;
  if BtnY < Y then
    BtnY := Y;
  CancelBtn := TNewButton.Create(Form);
  CancelBtn.Parent := Form;
  CancelBtn.Caption := 'Cancel';
  CancelBtn.Width := BtnW2;
  CancelBtn.Height := BtnH;
  CancelBtn.Left := Form.ClientWidth - MarginX - BtnW2;
  CancelBtn.Top := BtnY;
  CancelBtn.Cancel := True;
  CancelBtn.ModalResult := mrCancel;
  CancelBtn.TabOrder := 2;

  UninstallBtn := TNewButton.Create(Form);
  UninstallBtn.Parent := Form;
  UninstallBtn.Caption := 'Uninstall';
  UninstallBtn.Width := BtnW1;
  UninstallBtn.Height := BtnH;
  UninstallBtn.Left := CancelBtn.Left - ScaleX(8) - BtnW1;
  UninstallBtn.Top := BtnY;
  UninstallBtn.Default := False;
  UninstallBtn.ModalResult := mrOk;
  UninstallBtn.TabOrder := 1;

  { Focus starts on the checkbox, not Uninstall, so a stray Enter or a
    double-click carried over from Installed apps does not remove anything. }
  Form.ActiveControl := RemoveDataCheckBox;
  Result := BtnY + BtnH + MarginY;
end;

function NewPromptForm(DesignH: Integer): TSetupForm;
begin
  { Inno 6.6+: size in 96-DPI design pixels (scaled by the function);
    ClientWidth/ClientHeight are read-only afterwards. }
  Result := CreateCustomForm(470, DesignH, True, True);
  Result.Caption := 'Uninstall {#AppTitle}';
  Result.BorderStyle := bsDialog;
  Result.Position := poScreenCenter;
  Result.Font.Name := 'Segoe UI';
  Result.Font.Size := 9;
  Result.Color := clBtnFace;
end;

{ Blocking first step of an interactive uninstall. Nothing has been removed
  while it is up; Cancel returns False and InitializeUninstall aborts. }
function ShowUninstallDataPrompt: Boolean;
var
  Form: TSetupForm;
  ShowWarn: Boolean;
  NeedPx, DesignH: Integer;
begin
  ShowWarn := KeepDhcpRecordExists;
  Form := NewPromptForm(400);
  try
    NeedPx := BuildUninstallPrompt(Form, ShowWarn, 0);
  finally
    RemoveDataCheckBox := nil;
    Form.Free;
  end;
  { Back to design pixels, rounded up. }
  DesignH := (NeedPx * 1000 + ScaleY(1000) - 1) div ScaleY(1000);
  Form := NewPromptForm(DesignH);
  try
    BuildUninstallPrompt(Form, ShowWarn, Form.ClientHeight);
    Result := Form.ShowModal = mrOk;
    if Result then
      RemoveDataRequested := RemoveDataCheckBox.Checked;
    Log('Uninstall prompt: ' + IntToStr(Ord(Result)) + ', remove data: ' + IntToStr(Ord(RemoveDataRequested)));
    WriteBetaUninstallChoice(Result, RemoveDataRequested);
  finally
    RemoveDataCheckBox := nil;
    Form.Free;
  end;
end;

function QuotedParam(const S: String): String;
begin
  if (Pos(' ', S) > 0) or (Pos('"', S) > 0) then
    Result := AddQuotes(S)
  else
    Result := S;
end;

{ Relaunch as /SILENT /IPBINDPROMPT so Inno skips ConfirmUninstall. }
function RelaunchWithPrompt: Boolean;
var
  Args, P: String;
  I, ResultCode: Integer;
begin
  Result := False;
  Args := '/SILENT /IPBINDPROMPT';
  for I := 1 to ParamCount do
  begin
    P := ParamStr(I);
    if (CompareText(P, '/SILENT') <> 0) and
       (CompareText(P, '/VERYSILENT') <> 0) and
       (CompareText(P, '/IPBINDPROMPT') <> 0) then
      Args := Args + ' ' + QuotedParam(P);
  end;
  if not Exec(ExpandConstant('{uninstallexe}'), Args, '', SW_SHOWNORMAL, ewNoWait, ResultCode) then
  begin
    Log('Could not relaunch the uninstaller with the data prompt');
    { Fall back to showing the prompt in this process (Inno's Yes/No will follow). }
    Result := ShowUninstallDataPrompt;
    Exit;
  end;
  Result := False;
end;

function InitializeUninstall: Boolean;
begin
  RemoveDataRequested := RemoveDataCommandLineRequested;

  if CmdLineParamExists('/IPBINDPROMPT') then
  begin
    { Our interactive path uses /SILENT + /IPBINDPROMPT. Still show the
      form unless this is a truly quiet /VERYSILENT run. }
    if CmdLineParamExists('/VERYSILENT') then
    begin
      Result := True;
      Exit;
    end;
    Result := ShowUninstallDataPrompt;
    Exit;
  end;

  if UninstallSilent then
  begin
    Result := True;
    Exit;
  end;

  Result := RelaunchWithPrompt;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { RunList is filled when the Finished page is shown; re-apply in case a
    later Inno version resets Offset. }
  if (CurPageID = wpSelectTasks) or (CurPageID = wpFinished) then
    UnclipInnoChecks(WizardForm);
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
    PatchUninstallString;
    if not RegWriteStringValue(HKCU, UninstallRegKey, 'DisplayVersion', '{#AppDisplayVersion}') then
      Log('Could not set DisplayVersion to {#AppDisplayVersion}');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and RemoveDataRequested then
    DeleteAppData;
end;
