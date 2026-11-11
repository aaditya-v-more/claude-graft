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
  #ifndef WebView2Bootstrapper
    #error WebView2Bootstrapper is required for production setup
  #endif
  #ifndef WebView2Sha256
    #error WebView2Sha256 is required for production setup
  #endif
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
#ifdef TestMode
Compression=zip/1
#else
Compression=lzma2/normal
LZMANumBlockThreads=4
#endif
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=ClaudeGraft.exe
RestartApplications=no
Uninstallable=yes
#ifdef PublisherSigned
SignTool=graftpublisher
SignedUninstaller=yes
#endif
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
#ifndef TestMode
Source: "{#WebView2Bootstrapper}"; DestName: "MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy
#endif
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#ifndef TestMode
Name: "{userprograms}\Claude Graft"; Filename: "{app}\ClaudeGraft.exe"; Parameters: "--show"; WorkingDir: "{app}"; Comment: "Manage Claude Desktop profiles"
Name: "{userdesktop}\Claude Graft"; Filename: "{app}\ClaudeGraft.exe"; Parameters: "--show"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "Manage Claude Desktop profiles"
Name: "{userstartup}\Claude Graft"; Filename: "{app}\ClaudeGraft.exe"; WorkingDir: "{app}"; Tasks: startup; Comment: "Run extra Claude Desktop profiles"
#endif

[Run]
Filename: "{app}\ClaudeGraft.exe"; Parameters: "--show"; Description: "Open Claude Graft"; Flags: nowait postinstall skipifsilent
Filename: "{app}\ClaudeGraft.exe"; Flags: nowait skipifnotsilent; Check: WasGraftUpdate

#ifndef TestMode
[UninstallRun]
Filename: "{app}\launcher\GraftLaunch.exe"; Parameters: "--stop-sharing"; Flags: runhidden waituntilterminated; RunOnceId: "StopUsageSharing"
#endif

[Code]
function WasGraftUpdate: Boolean;
begin
  Result := ExpandConstant('{param:GRAFTUPDATE|0}') = '1';
end;

#ifndef TestMode
function ReadWebView2Version(RootKey: Integer; var Version: String): Boolean;
begin
  Result := RegQueryStringValue(RootKey, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
end;

function WebView2IsElevated: Boolean;
begin
  Result := IsAdmin;
end;

function StageWebView2Bootstrapper: String;
begin
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  Result := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
end;

function RunWebView2Bootstrapper(const Filename: String; var ExitCode: Integer): Boolean;
begin
  Result := ExecAsOriginalUser(Filename, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

#include "WebView2Runtime.iss"

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := EnsureWebView2Runtime;
end;

[Registry]
; Register a candidate only. Windows asks the user to select the default.
Root: HKCU; Subkey: "Software\Classes\ClaudeGraft.Link"; ValueType: string; ValueName: ""; ValueData: "Claude Graft link"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\ClaudeGraft.Link"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\ClaudeGraft.Link\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\ClaudeGraft.exe"",0"
Root: HKCU; Subkey: "Software\Classes\ClaudeGraft.Link\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ClaudeGraft.exe"" --open-link ""%1"""
Root: HKCU; Subkey: "Software\ClaudeGraft\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Claude Graft"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\ClaudeGraft\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Choose the Claude profile for a browser sign-in."
Root: HKCU; Subkey: "Software\ClaudeGraft\Capabilities\URLAssociations"; ValueType: string; ValueName: "claude"; ValueData: "ClaudeGraft.Link"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Claude Graft"; ValueData: "Software\ClaudeGraft\Capabilities"; Flags: uninsdeletevalue
#endif
