$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
[Console]::InputEncoding = [Text.UTF8Encoding]::new()
$hostName = 'com.ssl.exception.manager'
$appRoot = Join-Path $env:LOCALAPPDATA 'SSLExceptionManager'
$manifestPath = Join-Path (Join-Path $appRoot 'NativeHost') "$hostName.json"
$packageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$guideFile = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Guides\UninstallGuide.html'))
$guideUrl = ([uri]$guideFile).AbsoluteUri + '?package=' + [uri]::EscapeDataString($packageRoot)
$extensionId = $null
$extensionPath = [IO.Path]::GetFullPath((Join-Path $packageRoot 'Extension')).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
$profileMatches = @()
function Find-InstalledDaSslExtension([string]$UserData, [string]$ExpectedPath) {
    $exactMatches = @()
    $nameMatches = @()
    foreach ($profile in Get-ChildItem -LiteralPath $UserData -Directory -ErrorAction SilentlyContinue) {
        $securePreferences = Join-Path $profile.FullName 'Secure Preferences'
        if (-not (Test-Path -LiteralPath $securePreferences)) { continue }
        try {
            $preferences = Get-Content -LiteralPath $securePreferences -Raw | ConvertFrom-Json
            foreach ($entry in $preferences.extensions.settings.PSObject.Properties) {
                if ($entry.Name -notmatch '^[a-p]{32}$' -or -not $entry.Value.path) { continue }
                $registeredPath = [IO.Path]::GetFullPath([string]$entry.Value.path).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
                if ($ExpectedPath -and [string]::Equals($ExpectedPath, $registeredPath, [StringComparison]::OrdinalIgnoreCase)) {
                    $exactMatches += $entry.Name; continue
                }
                $extensionManifestPath = Join-Path $registeredPath 'manifest.json'
                if (-not (Test-Path -LiteralPath $extensionManifestPath)) { continue }
                try {
                    $extensionManifest = Get-Content -LiteralPath $extensionManifestPath -Raw | ConvertFrom-Json
                    if ([string]$extensionManifest.name -eq 'DA SSL') { $nameMatches += $entry.Name }
                } catch { }
            }
        } catch { }
    }
    $exactMatches = @($exactMatches | Select-Object -Unique)
    if ($exactMatches.Count -gt 0) { return $exactMatches }
    return @($nameMatches | Select-Object -Unique)
}
$userData = Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data'
if (Test-Path -LiteralPath $userData) {
    $profileMatches = @(Find-InstalledDaSslExtension $userData $extensionPath)
    if ($profileMatches.Count -eq 1) { $extensionId = $profileMatches[0] }
}
$idFile = Join-Path $appRoot 'extension-id.txt'
if (-not $extensionId -and (Test-Path -LiteralPath $idFile)) {
    try {
        $savedId = (Get-Content -LiteralPath $idFile -Raw).Trim().ToLowerInvariant()
        if ($savedId -cmatch '^[a-p]{32}$' -and ($profileMatches.Count -eq 0 -or $profileMatches -contains $savedId)) { $extensionId = $savedId }
    } catch { }
}
if (Test-Path -LiteralPath $manifestPath) {
    try {
        $nativeManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        foreach ($origin in $nativeManifest.allowed_origins) {
            if ([string]$origin -match '^chrome-extension://([a-p]{32})/$') {
                $candidateId = $Matches[1]
                $extensionId = $candidateId
                break
            }
        }
    } catch { }
}
$idWarning = if (-not $extensionId -and $profileMatches.Count -gt 1) { '&idStatus=ambiguous' } elseif (-not $extensionId) { '&idStatus=not-found' } else { '' }
if ($extensionId) {
    $guideUrl += '&extensionId=' + [uri]::EscapeDataString($extensionId)
    Write-Host "Detected DA SSL extension ID: $extensionId" -ForegroundColor Green
} else { Write-Warning 'Could not identify one DA SSL installation. The uninstall guide will explain how to copy its ID from Chrome.' }
$keys = @("HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName", "HKCU:\Software\Chromium\NativeMessagingHosts\$hostName")
$chromeExe = @(
    (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'),
    (Join-Path $env:LOCALAPPDATA 'Google\Chrome\Application\chrome.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $extensionId) {
    if ($chromeExe) { Start-Process -FilePath $chromeExe -ArgumentList 'chrome://extensions/' }
    else { Start-Process -FilePath 'chrome://extensions/' }
    Write-Host 'Copy the ID from the DA SSL card on chrome://extensions. Press Enter to skip.' -ForegroundColor Yellow
    $typedId = (Read-Host 'DA SSL extension ID (32 characters a-p)').Trim().ToLowerInvariant()
    if ($typedId -cmatch '^[a-p]{32}$') {
        $extensionId = $typedId
        Write-Host "Using DA SSL extension ID: $extensionId" -ForegroundColor Green
    }
}
if ($extensionId) { $guideUrl += '&extensionId=' + [uri]::EscapeDataString($extensionId) } else { $guideUrl += $idWarning }
if ($chromeExe) { Start-Process -FilePath $chromeExe -ArgumentList @($guideUrl) }
else { Start-Process -FilePath $guideUrl }
Write-Host '1. Follow the browser guide and remove DA SSL on the Chrome extensions page.' -ForegroundColor Cyan
Write-Host '2. Return to this Uninstall.cmd window and press Enter; minimize the browser if needed.' -ForegroundColor Cyan
Write-Host "App folder: $appRoot"
$null = Read-Host 'After removing the extension, press Enter (or Ctrl+C to cancel)'
foreach ($key in $keys) {
    if (Test-Path $key) {
        $registered = (Get-Item $key).GetValue('')
        if ($registered -and [IO.Path]::GetFullPath($registered) -eq [IO.Path]::GetFullPath($manifestPath)) {
            Remove-Item -LiteralPath $key -Recurse -Force
        } else { Write-Warning "Registration $key does not point to this app's manifest; it was left unchanged." }
    }
}
foreach ($process in Get-Process -Name 'SSLExceptionManager.NativeHost' -ErrorAction SilentlyContinue) {
    try {
        $processPath = [IO.Path]::GetFullPath($process.Path)
        $appRootFull = [IO.Path]::GetFullPath($appRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($processPath.StartsWith($appRootFull, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
    } catch { Write-Warning "Could not stop Native Host process $($process.Id); close Chrome and run uninstall again."; throw }
}
if (Test-Path -LiteralPath $appRoot) {
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $appRoot).Path)
    $expected = [IO.Path]::GetFullPath($appRoot)
    if ($resolved -ne $expected -or (Split-Path $resolved -Leaf) -ne 'SSLExceptionManager') { throw 'Resolved application directory did not match the expected target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host 'DA SSL has been uninstalled.' -ForegroundColor Green
