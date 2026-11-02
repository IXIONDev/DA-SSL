$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
[Console]::InputEncoding = [Text.UTF8Encoding]::new()
$hostName = 'com.ssl.exception.manager'
$appRoot = Join-Path $env:LOCALAPPDATA 'SSLExceptionManager'
$nativeDir = Join-Path $appRoot 'NativeHost'
$manifestPath = Join-Path $nativeDir "$hostName.json"
$project = Join-Path $PSScriptRoot '..\..\NativeHost\NativeHost.csproj'

function Read-ExtensionId([string]$Prompt, [bool]$Optional = $false) {
    while ($true) {
        $value = (Read-Host $Prompt).Trim().ToLowerInvariant()
        if ($Optional -and [string]::IsNullOrWhiteSpace($value)) { return $null }
        if ([string]::IsNullOrWhiteSpace($value)) {
            try { $value = (Get-Clipboard -Raw -ErrorAction Stop).Trim().ToLowerInvariant() } catch { $value = '' }
        }
        if ($value -cmatch '^[a-p]{32}$') { return $value }
        Write-Host 'Invalid ID. Copy the 32-character DA SSL ID and press Enter.' -ForegroundColor Yellow
    }
}

function Find-ChromeExtensionId([string]$ExtensionPath) {
    $userData = Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data'
    if (-not (Test-Path -LiteralPath $userData)) { return $null }
    $expectedPath = [IO.Path]::GetFullPath($ExtensionPath).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    foreach ($profile in Get-ChildItem -LiteralPath $userData -Directory -ErrorAction SilentlyContinue) {
        foreach ($fileName in @('Secure Preferences', 'Preferences')) {
            $preferenceFile = Join-Path $profile.FullName $fileName
            if (-not (Test-Path -LiteralPath $preferenceFile)) { continue }
            try {
                $preferences = Get-Content -LiteralPath $preferenceFile -Raw | ConvertFrom-Json
                foreach ($entry in $preferences.extensions.settings.PSObject.Properties) {
                    if ($entry.Name -notmatch '^[a-p]{32}$' -or -not $entry.Value.path) { continue }
                    $registeredPath = [IO.Path]::GetFullPath([string]$entry.Value.path).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
                    if ([string]::Equals($expectedPath, $registeredPath, [StringComparison]::OrdinalIgnoreCase)) { return $entry.Name }
                }
            } catch { continue }
        }
    }
    return $null
}

$packageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$guideFile = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Guides\InstallGuide.html'))
$guideUrl = ([uri]$guideFile).AbsoluteUri + '?package=' + [uri]::EscapeDataString($packageRoot)
$locationFile = Join-Path $packageRoot 'Extension\install-location.json'
try {
    $locationData = @{ installCmdPath = (Join-Path $packageRoot 'Install.cmd') } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($locationFile, $locationData, [Text.UTF8Encoding]::new($false))
} catch { Write-Warning 'Could not write the setup path into the extension guide.' }
$chromeExe = @(
    (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'),
    (Join-Path $env:LOCALAPPDATA 'Google\Chrome\Application\chrome.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $chromeExe) { $chromeCommand = Get-Command chrome.exe -ErrorAction SilentlyContinue; if ($chromeCommand) { $chromeExe = $chromeCommand.Source } }
if ($chromeExe) { Start-Process -FilePath $chromeExe -ArgumentList 'chrome://extensions/' }
else { Start-Process -FilePath 'chrome://extensions/' }
Write-Host ''
Write-Host "1. Follow the browser guide. The package folder is: $packageRoot" -ForegroundColor Cyan
Write-Host '2. Load the Extension folder in Chrome. Setup will detect its ID automatically.' -ForegroundColor Cyan
Write-Host 'Waiting for Chrome to register the unpacked extension (up to 30 seconds)...'
$extensionPath = Join-Path $packageRoot 'Extension'
$chromeId = $null
for ($attempt = 0; $attempt -lt 30 -and -not $chromeId; $attempt++) {
    $chromeId = Find-ChromeExtensionId $extensionPath
    if (-not $chromeId) { Start-Sleep -Seconds 1 }
}
if ($chromeId) {
    Write-Host "Detected DA SSL extension ID: $chromeId" -ForegroundColor Green
} else {
    Write-Host 'Automatic detection timed out. Copy the ID from Chrome and paste it below.' -ForegroundColor Yellow
    $chromeId = Read-ExtensionId 'Chrome extension ID (or press Enter to read from clipboard)'
}
$guideUrlWithId = $guideUrl + '&extensionId=' + [uri]::EscapeDataString($chromeId)
if ($chromeExe) { Start-Process -FilePath $chromeExe -ArgumentList @($guideUrlWithId) }
else { Start-Process -FilePath $guideUrlWithId }
$chromiumId = Read-ExtensionId 'Chromium extension ID (optional; press Enter to skip)' $true

$chromeKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
$chromiumKey = "HKCU:\Software\Chromium\NativeMessagingHosts\$hostName"
foreach ($key in @($chromeKey, $chromiumKey)) {
    if (Test-Path $key) {
        $existing = (Get-Item $key).GetValue('')
        if ($existing -and $existing -ne $manifestPath) { throw "A different registration already exists for $hostName. Setup stopped without overwriting it." }
    }
}

New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $appRoot 'extension-id.txt'), $chromeId, [Text.UTF8Encoding]::new($false))
$prebuilt = Join-Path $PSScriptRoot '..\..\NativeHost\Publish'
if (Test-Path -LiteralPath (Join-Path $prebuilt 'SSLExceptionManager.NativeHost.exe')) {
    $staging = $null
} else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The prebuilt helper is missing. Extract the complete ZIP package and try again.' }
    $staging = Join-Path $nativeDir 'publish-staging'
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the helper application.' }
    $prebuilt = $staging
}
$nativeRoot = [IO.Path]::GetFullPath($nativeDir)
foreach ($process in Get-Process -Name 'SSLExceptionManager.NativeHost' -ErrorAction SilentlyContinue) {
    try {
        $processPath = [IO.Path]::GetFullPath($process.Path)
        if ($processPath.StartsWith($nativeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
    } catch { throw "Could not stop the currently running Native Host (PID $($process.Id)). Close Chrome and retry." }
}
Copy-Item -Path (Join-Path $prebuilt '*') -Destination $nativeDir -Recurse -Force
if ($staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
$allowed = @("chrome-extension://$chromeId/")
if ($chromiumId) { $allowed += "chrome-extension://$chromiumId/" }
$manifest = [ordered]@{ name = $hostName; description = 'Local SSL exception manager for user-approved browser actions'; path = (Join-Path $nativeDir 'SSLExceptionManager.NativeHost.exe'); type = 'stdio'; allowed_origins = $allowed }
$manifestJson = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($manifestPath, $manifestJson, [Text.UTF8Encoding]::new($false))
New-Item -Path $chromeKey -Force | Out-Null
Set-Item -Path $chromeKey -Value $manifestPath
if ($chromiumId) { New-Item -Path $chromiumKey -Force | Out-Null; Set-Item -Path $chromiumKey -Value $manifestPath }
Write-Host 'The helper is installed. Reload DA SSL at chrome://extensions.' -ForegroundColor Green
Write-Host "The access list is empty. App folder: $appRoot"
