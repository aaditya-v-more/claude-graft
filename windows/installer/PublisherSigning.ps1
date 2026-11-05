# Imported only for a publisher build. The key stays in the maintainer's
# CurrentUser\My store or its hardware provider.
function Get-GraftSignature($Path) {
    Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
}

function Assert-GraftPublisherSignature($Path, $Thumbprint) {
    $signature = Get-GraftSignature $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or
        $signature.SignerCertificate.Thumbprint -ne $Thumbprint -or -not $signature.TimeStamperCertificate) {
        throw "Expected a valid, timestamped embedded signature from $Thumbprint on $Path ($($signature.Status))."
    }
}

function Get-GraftSignTool($Requested) {
    if (-not $Requested) {
        $Requested = (Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    }
    if (-not $Requested) { throw 'Supply -SignTool with the full path to Windows SDK signtool.exe.' }
    $tool = (Get-Item -LiteralPath $Requested).FullName
    $signature = Get-GraftSignature $tool
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw 'SignTool must have a valid Microsoft signature.'
    }
    return $tool
}

function Assert-GraftSigningCertificate($Thumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
    $usages = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value }
    if (-not $certificate -or -not $certificate.HasPrivateKey -or
        $usages -notcontains '1.3.6.1.5.5.7.3.3' -or
        $certificate.Subject -eq $certificate.Issuer -or -not $certificate.Verify()) {
        throw "A trusted code-signing certificate with its private key is required at CurrentUser\My\$Thumbprint."
    }
}

function Sign-GraftPayload($Root, $Tool, $Thumbprint, $TimestampUrl) {
    $firstParty = @('ClaudeGraft.exe','ClaudeGraft.dll','ClaudeGraft.Core.dll',
        'launcher\GraftLaunch.exe','launcher\GraftLaunch.dll','launcher\ClaudeGraft.Core.dll')
    $unsigned = @()
    $problems = @()
    foreach ($name in $firstParty) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $name) -PathType Leaf)) { throw "Missing first-party file: $name" }
    }

    # Verify every dependency before using the key. Catalog trust on this build
    # machine does not travel with files installed on someone else's machine.
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked staging entry: $($file.FullName)" }
        if ($file.PSIsContainer -or $file.Extension -notin '.exe','.dll','.node','.ocx','.sys','.cpl','.scr') { continue }
        $relative = $file.FullName.Substring($Root.Length + 1)
        $sig = Get-GraftSignature $file.FullName
        if ($relative -in $firstParty) {
            if ($sig.Status -eq 'NotSigned') { $unsigned += $file.FullName }
            else { Assert-GraftPublisherSignature $file.FullName $Thumbprint }
        } elseif ($sig.Status -ne 'Valid' -or $sig.SignatureType -ne 'Authenticode') {
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
            $problems += "$relative  $($sig.Status)/$($sig.SignatureType)  SHA256=$hash"
        }
    }
    if ($problems.Count) {
        throw ("Dependency signatures need review. Obtain a trusted embedded signature or explicitly review and sign each permitted unsigned dependency as described in windows/README.md." +
            [Environment]::NewLine + ($problems -join [Environment]::NewLine))
    }
    foreach ($path in $unsigned) {
        & $Tool sign /s My /sha1 $Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $path
        if ($LASTEXITCODE -ne 0) { throw "SignTool signing failed for $path ($LASTEXITCODE)." }
        Assert-GraftPublisherSignature $path $Thumbprint
    }
}

function Get-GraftInnoSignToolArgument($Tool, $Thumbprint, $TimestampUrl) {
    # ISCC expands $q to a quote and $f to the quoted temporary file name.
    $quotedTool = '$q' + $Tool.Replace('$','$$') + '$q'
    $quotedTimestamp = '$q' + $TimestampUrl.Replace('$','$$') + '$q'
    return '/Sgraftpublisher=' + $quotedTool + ' sign /s My /sha1 ' + $Thumbprint +
        ' /fd SHA256 /tr ' + $quotedTimestamp + ' /td SHA256 $f'
}
