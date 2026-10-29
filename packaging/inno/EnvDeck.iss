; Per-user installer (no admin prompt) that winget installs silently.
; Built by scripts/publish.ps1, which passes AppVersion, SourceDir, OutputDir and Arch.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\dist"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{6F1C9C6E-3B1A-4C1E-9D0B-5E0F2A7C8D41}
AppName=EnvDeck
AppVersion={#AppVersion}
AppPublisher=Trần Công Minh
AppPublisherURL=https://github.com/minhtrn98/envdeck
DefaultDirName={autopf}\EnvDeck
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
ChangesEnvironment=yes
OutputDir={#OutputDir}
OutputBaseFilename=EnvDeck-{#AppVersion}-win-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=EnvDeck
UninstallDisplayIcon={app}\EnvDeck.App.exe
LicenseFile=..\..\LICENSE

[Tasks]
Name: "addtopath"; Description: "Add the envdeck command to PATH"; GroupDescription: "Command line:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\EnvDeck"; Filename: "{app}\EnvDeck.App.exe"
Name: "{autodesktop}\EnvDeck"; Filename: "{app}\EnvDeck.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\EnvDeck.App.exe"; Description: "{cm:LaunchProgram,EnvDeck}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UserEnvKey = 'Environment';
  MachineEnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function EnvRoot: Integer;
begin
  if IsAdminInstallMode then Result := HKEY_LOCAL_MACHINE else Result := HKEY_CURRENT_USER;
end;

function EnvKey: String;
begin
  if IsAdminInstallMode then Result := MachineEnvKey else Result := UserEnvKey;
end;

procedure AddToPath(Dir: String);
var
  Paths: String;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then Paths := '';
  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0 then exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then Paths := Paths + ';';
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths + Dir);
end;

procedure RemoveFromPath(Dir: String);
var
  Paths: String;
  P: Integer;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then exit;
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Paths));
  if P = 0 then exit;
  Delete(Paths, P, Length(Dir) + 1);
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Copy(Paths, 2, Length(Paths) - 2));
end;

function InitializeSetup: Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
    FindClose(FindRec)
  else if not WizardSilent then
    Result := MsgBox('EnvDeck needs the .NET 10 Desktop Runtime, which was not found.' + #13#10#13#10 +
      'Install it with:  winget install Microsoft.DotNet.DesktopRuntime.10' + #13#10#13#10 +
      'Continue anyway?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then RemoveFromPath(ExpandConstant('{app}'));
end;
