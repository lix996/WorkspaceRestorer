; WorkspaceRestorer per-user installer.
;
; Defaults are intentionally conservative: the user can pick any writable
; folder, but the installer never requests elevation. Files written by this
; installer are new files and therefore do not inherit the downloaded setup's
; Zone.Identifier.

#ifndef MyAppName
  #define MyAppName "WorkspaceRestorer"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.0.1"
#endif
#ifndef MyAppPublisher
  #define MyAppPublisher "lix996"
#endif
#ifndef SourceExe
  #define SourceExe "..\..\zuizhongchanwu\WorkspaceRestorer.exe"
#endif
#ifndef StageDir
  #define StageDir "..\..\build\installer\stage\en-US"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\build\installer"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "WorkspaceRestorer-Setup-v1.0.1-windows-x64"
#endif
#ifndef SetupIcon
  #define SetupIcon "..\..\assets\app.ico"
#endif
#ifndef LicenseFile
  #define LicenseFile "..\..\LICENSE"
#endif
#ifndef LanguageDir
  #define LanguageDir "."
#endif

[Setup]
AppId={{2F6C1B8E-7A43-4C91-9D5E-6B0A3C8F1D72}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/lix996/WorkspaceRestorer
AppSupportURL=https://github.com/lix996/WorkspaceRestorer/issues
AppUpdatesURL=https://github.com/lix996/WorkspaceRestorer/releases/latest
DefaultDirName={localappdata}\Programs\WorkspaceRestorer
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousAppDir=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#SetupIcon}
LicenseFile={#LicenseFile}
UninstallDisplayIcon={app}\WorkspaceRestorer.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no
AppMutex=Local\WorkspaceRestorer
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "{#LanguageDir}\ChineseSimplified.isl"

[CustomMessages]
english.CannotWriteDir=The selected folder is not writable. Choose a folder you can write to.
chinesesimplified.CannotWriteDir=所选目录不可写，请选择当前用户有写入权限的目录。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StageDir}\WorkspaceRestorer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\data\app.ico"; DestDir: "{app}\data"; Flags: ignoreversion uninsneveruninstall
Source: "{#StageDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\SOURCE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\WorkspaceRestorer.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\WorkspaceRestorer.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\WorkspaceRestorer.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function CanWriteToDirectory(const Directory: String): Boolean;
var
  Existing: String;
  Probe: String;
begin
  Result := False;

  if FileExists(Directory) then
    Exit;

  Existing := Directory;
  while (Existing <> '') and (not DirExists(Existing)) do
    Existing := ExtractFileDir(Existing);

  if Existing = '' then
    Exit;

  Probe := AddBackslash(Existing) + '.workspace-restorer-write-test.tmp';
  if SaveStringToFile(Probe, 'test', False) then
  begin
    DeleteFile(Probe);
    Result := True;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if not CanWriteToDirectory(WizardDirValue) then
    begin
      MsgBox(ExpandConstant('{cm:CannotWriteDir}'), mbError, MB_OK);
      Result := False;
    end;
  end;
end;
