function WebView2VersionPresent(const Value: String): Boolean;
var
  Version: Int64;
begin
  Result := StrToVersion(Value, Version);
  if Result then
    Result := ComparePackedVersion(Version, PackVersionComponents(0, 0, 0, 0)) > 0;
end;

function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result := False;
  if ReadWebView2Version(HKLM32, Version) then
    Result := WebView2VersionPresent(Version);
  if not Result then
    if ReadWebView2Version(HKCU, Version) then
      Result := WebView2VersionPresent(Version);
end;

function EnsureWebView2Runtime: String;
var
  Bootstrapper: String;
  ExitCode: Integer;
begin
  Result := '';
  if WebView2Installed then Exit;
  if WebView2IsElevated then begin
    Result := 'Microsoft Edge WebView2 Runtime is missing. Close setup and open it normally, without Run as administrator, to install it for your Windows account.';
    Exit;
  end;
  try
    Bootstrapper := StageWebView2Bootstrapper;
    if CompareText(GetSHA256OfFile(Bootstrapper), '{#WebView2Sha256}') <> 0 then begin
      Result := 'The Microsoft Edge WebView2 installer failed its integrity check. Download Claude Graft setup again.';
      Exit;
    end;
    Log('Installing the missing Microsoft Edge WebView2 Runtime for this Windows account.');
    if not RunWebView2Bootstrapper(Bootstrapper, ExitCode) then
      Result := 'The Microsoft Edge WebView2 installer could not start (' + IntToStr(ExitCode) + ').'
    else if not WebView2Installed then
      Result := 'Microsoft Edge WebView2 Runtime is still missing after setup (' + IntToStr(ExitCode) + ').';
  except
    Log(GetExceptionMessage);
    Result := 'Microsoft Edge WebView2 Runtime could not be prepared.';
  end;
  if Result <> '' then
    Result := Result + ' Check your internet connection and retry. For an offline computer, install Microsoft''s Evergreen Standalone Installer from https://developer.microsoft.com/microsoft-edge/webview2/ first, then run Claude Graft setup again.';
end;
