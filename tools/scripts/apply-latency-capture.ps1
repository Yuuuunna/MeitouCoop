#requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][ValidateSet('client', 'server')][string] $Kind,
    [Parameter(Mandatory = $true)][string] $Target
)
$ErrorActionPreference = 'Stop'
$targetRoot = (Resolve-Path -LiteralPath $Target).ProviderPath
$executable = if ($Kind -eq 'server') { 'Meitou.Server.exe' } else { 'MeitouClient.exe' }
$processPath = Join-Path $targetRoot $executable
if (-not (Test-Path -LiteralPath $processPath -PathType Leaf)) { throw "Not a $Kind installation: $targetRoot" }
if (Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $processPath }) {
    throw 'Exit this application normally before installing capture instrumentation.'
}
$manifestPath = Join-Path $PSScriptRoot 'capture-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    $manifestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'capture-manifest.json'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.protocol -ne 20) { throw 'This capture bundle is only for the existing protocol-20 installation.' }
$files = @($manifest.files | Where-Object { $_.kind -eq $Kind })
if ($files.Count -eq 0) { throw 'No capture files for the requested installation.' }
foreach ($file in $files) {
    if ([IO.Path]::GetFileName($file.name) -ne $file.name) { throw 'Invalid manifest filename.' }
}
$manifestRoot = Split-Path -Parent $manifestPath
$candidates = @((Join-Path $manifestRoot $Kind), $PSScriptRoot, $manifestRoot) | Select-Object -Unique
$sourceRoot = $null
foreach ($candidate in $candidates) {
    $matches = $true
    foreach ($file in $files) {
        $source = Join-Path $candidate $file.name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { $matches = $false; break }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) { $matches = $false; break }
    }
    if ($matches) { $sourceRoot = (Resolve-Path -LiteralPath $candidate).ProviderPath; break }
}
if ($null -eq $sourceRoot) {
    throw "No complete, checksum-matching $Kind payload found beside the script or manifest. Keep the capture files together. Checked: $($candidates -join ', ')"
}
if ($sourceRoot.TrimEnd('\', '/') -eq $targetRoot.TrimEnd('\', '/')) {
    Write-Output "Capture files are already installed and verified. Start with start-latency-$Kind.cmd."
    exit 0
}
$backup = Join-Path $targetRoot ('latency-capture-backup-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup | Out-Null
$existing = @()
foreach ($file in $files) {
    $destination = Join-Path $targetRoot $file.name
    if (Test-Path -LiteralPath $destination) {
        Copy-Item -LiteralPath $destination -Destination (Join-Path $backup $file.name)
        $existing += $file.name
    }
}
try {
    foreach ($file in $files) {
        $destination = Join-Path $targetRoot $file.name
        Copy-Item -LiteralPath (Join-Path $sourceRoot $file.name) -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $file.sha256) { throw "Installed checksum mismatch: $($file.name)" }
    }
} catch {
    foreach ($name in $existing) {
        Copy-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $targetRoot $name) -Force
    }
    throw
}
Write-Output "Capture instrumentation installed. Previous files: $backup"
Write-Output "Start with start-latency-$Kind.cmd. Configuration, credentials and saves were not included in this bundle."
