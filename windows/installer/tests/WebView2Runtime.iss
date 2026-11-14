[Setup]
AppName=Claude Graft WebView2 prerequisite test
AppVersion=1
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
OutputDir={#FixtureOutput}
OutputBaseFilename=webview2-prerequisite-test
Compression=zip/1

[Code]
var
  MachineVersion, UserVersion: String;
  Elevated, CanStart, RegistersRuntime, Corrupt, StageThrows: Boolean;
  RunCount, StageCount, Assertions: Integer;

function ReadWebView2Version(RootKey: Integer; var Version: String): Boolean;
begin
  if RootKey = HKLM32 then Version := MachineVersion
  else if RootKey = HKCU then Version := UserVersion
  else RaiseException('Unexpected registry view.');
  Result := Version <> '';
end;

function WebView2IsElevated: Boolean;
begin
  Result := Elevated;
end;

function StageWebView2Bootstrapper: String;
begin
  StageCount := StageCount + 1;
  if StageThrows then RaiseException('Fixture extraction failed.');
  Result := ExpandConstant('{tmp}\webview2-fixture.txt');
  if Corrupt then SaveStringToFile(Result, 'modified', False)
  else SaveStringToFile(Result, 'fixture', False);
end;

function RunWebView2Bootstrapper(const Filename: String; var ExitCode: Integer): Boolean;
begin
  RunCount := RunCount + 1;
  ExitCode := 0;
  if RegistersRuntime then UserVersion := '156.0.1.2';
  Result := CanStart;
end;

#include "..\WebView2Runtime.iss"

procedure Check(Condition: Boolean; const Message: String);
begin
  if not Condition then RaiseException(Message);
  Assertions := Assertions + 1;
end;

procedure Reset;
begin
  MachineVersion := '';
  UserVersion := '';
  Elevated := False;
  CanStart := True;
  RegistersRuntime := True;
  Corrupt := False;
  StageThrows := False;
  RunCount := 0;
  StageCount := 0;
end;

function InitializeSetup: Boolean;
var
  Failure: String;
begin
  Result := False;
  try
    Check(not WebView2VersionPresent(''), 'an empty version is missing');
    Check(not WebView2VersionPresent('0.0.0.0'), 'an uninstalled runtime is missing');
    Check(not WebView2VersionPresent('invalid'), 'an unreadable version is missing');
    Check(WebView2VersionPresent('156.0.1.2'), 'an installed version is present');
    Reset;
    MachineVersion := '156.0.1.2';
    Check(EnsureWebView2Runtime = '', 'a machine runtime is reused');
    Check((RunCount = 0) and (StageCount = 0), 'a present runtime is never extracted or run');
    Reset;
    MachineVersion := '0.0.0.0';
    UserVersion := '156.0.1.2';
    Check(EnsureWebView2Runtime = '', 'a per-user runtime survives an empty machine registration');
    Check(RunCount = 0, 'a per-user runtime is not installed again');
    Reset;
    Check(EnsureWebView2Runtime = '', 'a missing runtime is installed and detected again');
    Check((RunCount = 1) and (StageCount = 1), 'a missing runtime runs the bootstrapper once');
    Check(EnsureWebView2Runtime = '', 'retry after success reuses the runtime');
    Check(RunCount = 1, 'retry never repeats a completed installation');
    Reset;
    Elevated := True;
    Failure := EnsureWebView2Runtime;
    Check(Pos('without Run as administrator', Failure) > 0, 'elevated setup gives per-user recovery guidance');
    Check((RunCount = 0) and (StageCount = 0), 'elevated setup never launches a machine install');
    Reset;
    Corrupt := True;
    Failure := EnsureWebView2Runtime;
    Check(Pos('integrity check', Failure) > 0, 'modified extracted bytes are rejected');
    Check(RunCount = 0, 'modified bytes are never executed');
    Reset;
    CanStart := False;
    RegistersRuntime := False;
    Failure := EnsureWebView2Runtime;
    Check(Pos('could not start', Failure) > 0, 'blocked execution stops setup');
    Check(Pos('Standalone Installer', Failure) > 0, 'failure offers an offline recovery route');
    Reset;
    RegistersRuntime := False;
    Check(Pos('still missing', EnsureWebView2Runtime) > 0, 'a zero exit without a runtime cannot proceed');
    Reset;
    StageThrows := True;
    Check(Pos('could not be prepared', EnsureWebView2Runtime) > 0, 'extraction failure is actionable');
    Check(RunCount = 0, 'extraction failure never launches a process');
    SaveStringToFile(ExpandConstant('{param:RESULTFILE}'), 'Passed ' + IntToStr(Assertions) + ' prerequisite assertions.', False);
  except
    SaveStringToFile(ExpandConstant('{param:RESULTFILE}'), 'FAILED: ' + GetExceptionMessage, False);
  end;
end;
