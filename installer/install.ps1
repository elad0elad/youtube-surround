# Installs YouTube Surround for the current Windows user.
# Copies the host, decoder, and yt-dlp, then registers native messaging for Chrome and Brave.
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Dest = Join-Path $env:LOCALAPPDATA "YouTubeSurround"
$HostName = "com.youtubesurround.host"

function Extension-Id([string]$Key) {
    $bytes = [Convert]::FromBase64String($Key)
    $sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    $map = "abcdefghijklmnop"
    $id = ""
    foreach ($b in $sha[0..15]) {
        $id += $map[[Math]::Floor($b / 16)]
        $id += $map[$b % 16]
    }
    return $id
}

function Remove-Registration {
    $roots = @(
        "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName",
        "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\$HostName",
        "HKCU:\Software\Chromium\NativeMessagingHosts\$HostName"
    )
    foreach ($key in $roots) {
        if (Test-Path $key) { Remove-Item $key -Recurse -Force }
    }
}

function Remove-PlaybackTemps {
    $temp = [System.IO.Path]::GetTempPath()
    Get-ChildItem -LiteralPath $temp -Force -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '^ys-[0-9a-fA-F]{32}\.(m4a|wav|ysp|m4a\.done|m4a\.part)$'
    } | ForEach-Object {
        try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop } catch {}
    }
}

if ($Uninstall) {
    Get-Process YouTubeSurroundHost -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-PlaybackTemps
    Remove-Registration
    if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
    Write-Host "YouTube Surround was removed."
    Write-Host "In Brave or Chrome, open the extensions page and remove YouTube Surround."
    return
}

$manifestPath = $null
foreach ($candidate in @(
    (Join-Path $PSScriptRoot "extension\manifest.json"),
    (Join-Path $Root "extension\manifest.json")
)) {
    if (Test-Path -LiteralPath $candidate) { $manifestPath = $candidate; break }
}
if (-not $manifestPath) { throw "extension manifest was not found" }
$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $manifest.key) { throw "extension manifest has no key" }
$extensionId = Extension-Id $manifest.key

function Find-File([string[]]$Candidates) {
    foreach ($path in $Candidates) {
        if ($path -and (Test-Path -LiteralPath $path)) { return (Resolve-Path -LiteralPath $path).Path }
    }
    return $null
}

$ytdlp = Find-File @(
    (Join-Path $PSScriptRoot "yt-dlp.exe"),
    (Join-Path $Root "dist\yt-dlp.exe"),
    "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\yt-dlp.yt-dlp_Microsoft.Winget.Source_8wekyb3d8bbwe\yt-dlp.exe"
)
if (-not $ytdlp) {
    $cmd = Get-Command yt-dlp.exe -ErrorAction SilentlyContinue
    if ($cmd) { $ytdlp = $cmd.Source }
}
if (-not $ytdlp) { throw "yt-dlp.exe was not found. Place it next to install.ps1." }

$hostExe = Find-File @(
    (Join-Path $PSScriptRoot "YouTubeSurroundHost.exe"),
    (Join-Path $Root "native\host\YouTubeSurroundHost.exe")
)
$decoder = Find-File @(
    (Join-Path $PSScriptRoot "ys-iamf.exe"),
    (Join-Path $Root "decoder\ys-iamf\target\release\ys-iamf.exe")
)
$deno = Find-File @(
    (Join-Path $PSScriptRoot "deno.exe"),
    "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\DenoLand.Deno_Microsoft.Winget.Source_8wekyb3d8bbwe\deno.exe"
)
if (-not $hostExe) { throw "YouTubeSurroundHost.exe was not found." }
if (-not $decoder) { throw "ys-iamf.exe was not found." }
if (-not $deno) { throw "deno.exe was not found. yt-dlp needs it to read YouTube." }

Get-Process YouTubeSurroundHost -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
Copy-Item -LiteralPath $hostExe -Destination (Join-Path $Dest "YouTubeSurroundHost.exe") -Force
Copy-Item -LiteralPath $decoder -Destination (Join-Path $Dest "ys-iamf.exe") -Force
Copy-Item -LiteralPath $ytdlp -Destination (Join-Path $Dest "yt-dlp.exe") -Force
Copy-Item -LiteralPath $deno -Destination (Join-Path $Dest "deno.exe") -Force
$extSource = Split-Path -Parent $manifestPath
$extDest = Join-Path $Dest "extension"
if (Test-Path $extDest) { Remove-Item $extDest -Recurse -Force }
Copy-Item -LiteralPath $extSource -Destination $extDest -Recurse -Force

$hostManifest = @{
    name = $HostName
    description = "YouTube Surround audio host"
    path = (Join-Path $Dest "YouTubeSurroundHost.exe")
    type = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
} | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $Dest "com.youtubesurround.host.json"), $hostManifest)

$manifestFile = Join-Path $Dest "com.youtubesurround.host.json"
foreach ($root in @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
    "HKCU:\Software\Chromium\NativeMessagingHosts"
)) {
    New-Item -Path (Join-Path $root $HostName) -Force | Out-Null
    Set-ItemProperty -Path (Join-Path $root $HostName) -Name "(default)" -Value $manifestFile
}

Write-Host "Installed to $Dest"
Write-Host "Extension id: $extensionId"
Write-Host "Load the unpacked extension from: $extDest"
Write-Host "RESULT=INSTALLED id=$extensionId"

$guideTemplate = $null
foreach ($candidate in @(
    (Join-Path $PSScriptRoot "guide.html"),
    (Join-Path $PSScriptRoot "installer\guide.html")
)) {
    if (Test-Path -LiteralPath $candidate) { $guideTemplate = $candidate; break }
}
if ($guideTemplate) {
    $utf8 = New-Object System.Text.UTF8Encoding $true
    $html = [System.IO.File]::ReadAllText($guideTemplate, [System.Text.UTF8Encoding]::new($false))
    $html = $html.Replace("__EXT_PATH__", $extDest)
    $guideOut = Join-Path $Dest "guide.html"
    [System.IO.File]::WriteAllText($guideOut, $html, $utf8)
    Start-Process $guideOut
}
Start-Process explorer.exe -ArgumentList $extDest
$brave = Join-Path $env:ProgramFiles "BraveSoftware\Brave-Browser\Application\brave.exe"
$chrome = Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe"
$chrome86 = Join-Path ${env:ProgramFiles(x86)} "Google\Chrome\Application\chrome.exe"
if (Test-Path -LiteralPath $brave) { Start-Process -FilePath $brave -ArgumentList "brave://extensions" }
elseif (Test-Path -LiteralPath $chrome) { Start-Process -FilePath $chrome -ArgumentList "chrome://extensions" }
elseif (Test-Path -LiteralPath $chrome86) { Start-Process -FilePath $chrome86 -ArgumentList "chrome://extensions" }
