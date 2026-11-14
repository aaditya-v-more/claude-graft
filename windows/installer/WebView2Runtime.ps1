function Get-GraftWebView2Signature($Path) {
    Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
}

function Get-GraftWebView2Bootstrapper([string]$CacheDirectory, [string]$ExistingFile) {
    $url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
    if ($ExistingFile) { $path = (Get-Item -LiteralPath $ExistingFile).FullName }
    else {
        New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null
        $path = Join-Path $CacheDirectory 'MicrosoftEdgeWebview2Setup.exe'
        if (-not (Test-Path -LiteralPath $path)) {
            $temporary = Join-Path $CacheDirectory ([guid]::NewGuid().ToString('N') + '.download')
            try {
                Invoke-WebRequest -Uri $url -OutFile $temporary
                Assert-GraftWebView2Signature $temporary
                Move-Item -LiteralPath $temporary -Destination $path
            } finally {
                if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
            }
        }
    }
    # Cached and explicitly supplied files have the same trust requirement as a download.
    Assert-GraftWebView2Signature $path
    $signature = Get-GraftWebView2Signature $path
    [pscustomobject]@{
        path = $path
        source = $url
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        signerThumbprint = $signature.SignerCertificate.Thumbprint
    }
}

function Assert-GraftWebView2Signature([string]$Path) {
    $signature = Get-GraftWebView2Signature $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw "The WebView2 bootstrapper must have a valid embedded Microsoft Corporation signature: $Path ($($signature.Status)). Download it from https://developer.microsoft.com/microsoft-edge/webview2/."
    }
}
