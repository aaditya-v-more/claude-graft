#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef Architecture
  #define Architecture "x64"
#endif
#if Architecture == "arm64"
  #define AllowedArchitecture "arm64"
#else
  #define AllowedArchitecture "x64compatible"
#endif
#ifdef TestMode
  #define ProductId "ClaudeGraft-Installer-Test"
  #define OutputSuffix "-test"
#else
  #define ProductId "{{B0C98D46-A272-4C54-8EDC-A31D441BAE9B}"
  #define OutputSuffix ""
#endif

[Setup]
AppId={#ProductId}
AppName=Claude Graft
AppVersion={#AppVersion}
AppPublisher=Aaditya Vitthal More
AppPublisherURL=https://github.com/aaditya-v-more/claude-graft
AppSupportURL=https://github.com/aaditya-v-more/claude-graft/issues
DefaultDirName={localappdata}\Programs\ClaudeGraft
DefaultGroupName=Claude Graft
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#AllowedArchitecture}
ArchitecturesInstallIn64BitMode={#AllowedArchitecture}
MinVersion=10.0.19041
OutputDir={#InstallerOutput}
OutputBaseFilename=ClaudeGraft-{#AppVersion}-windows-{#Architecture}-setup{#OutputSuffix}
SetupIconFile=..\ClaudeGraft\Assets\AppIcon.ico
UninstallDisplayIcon={app}\ClaudeGraft.exe
LicenseFile=..\..\LICENSE
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=ClaudeGraft.exe
RestartApplications=no
Uninstallable=yes
#ifdef TestMode
CreateUninstallRegKey=no
UsePreviousAppDir=no
UsePreviousGroup=no
UsePreviousTasks=no
#else
CreateUninstallRegKey=yes
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Open at Login"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#VcRedist}"; DestName: "vc_redist.{#Architecture}.exe"; Flags: dontcopy

[Icons]
#ifndef TestMode
Name: "{userprograms}\Claude Graft"; Filename: "{app}\ClaudeGraft.exe"; Parameters: "--show"; WorkingDir: "{app}"; Comment: "Manage Claude Desktop profiles"
Name: "{userdesktop}\Claude Graft"; Filename: "{app}\ClaudeGraft.exe"; Parameters: "--show"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "Manage Claude Desktop profiles"
Name: "{userstartup}\Claude Graft"; Filename: "{app}\ClaudeGraft.exe"; WorkingDir: "{app}"; Tasks: startup; Comment: "Run extra Claude Desktop profiles"
#endif

[Run]
Filename: "{app}\ClaudeGraft.exe"; Parameters: "--show"; Description: "Open Claude Graft"; Flags: nowait postinstall skipifsilent

[Code]
function RuntimeInRegistry(RootKey: Integer): Boolean;
var
  Key: String;
  Installed, Major, Minor, Build: Cardinal;
begin
  Key := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{#Architecture}';
  Result :=
    RegQueryDWordValue(RootKey, Key, 'Installed', Installed) and (Installed = 1) and
    RegQueryDWordValue(RootKey, Key, 'Major', Major) and
    RegQueryDWordValue(RootKey, Key, 'Minor', Minor) and
    RegQueryDWordValue(RootKey, Key, 'Bld', Build);
  if Result then
    Result := (Major > 14) or ((Major = 14) and
      ((Minor > 50) or ((Minor = 50) and (Build >= 35719))));
end;

function NeedsRuntime: Boolean;
begin
  Result := not (RuntimeInRegistry(HKLM32) or RuntimeInRegistry(HKLM64));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if not NeedsRuntime then
    exit;
#ifdef TestMode
  Result := 'The installer test requires the Visual C++ runtime already installed.';
#else
  ExtractTemporaryFile('vc_redist.{#Architecture}.exe');
  WizardForm.StatusLabel.Caption := 'Installing the Microsoft Visual C++ runtime...';
  if not Exec(ExpandConstant('{tmp}\vc_redist.{#Architecture}.exe'),
      '/install /passive /norestart', '', SW_SHOWNORMAL,
      ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'The Microsoft Visual C++ runtime could not be started: ' + SysErrorMessage(ExitCode);
    exit;
  end;
  if ExitCode = 3010 then
    NeedsRestart := True
  else if (ExitCode <> 0) and ((ExitCode <> 1638) or NeedsRuntime) then
    Result := 'The Microsoft Visual C++ runtime was not installed (code ' +
      IntToStr(ExitCode) + '). Run setup again to retry.';
#endif
end;
