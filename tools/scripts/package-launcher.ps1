#requires -Version 7.0
<#
.SYNOPSIS
Builds a Windows x64 portable Coop launcher, without changing installed launchers or publishing a release.
.PARAMETER NativeAot
Use the native compiler instead of the default self-contained managed build; requires MSVC C++ build tools.
#>
[CmdletBinding()]
param(
    [string] $Output = ('artifacts/releases/launcher-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')),
    [string] $Dotnet = '',
    [switch] $NativeAot
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This portable packaging command targets Windows x64.' }

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputRoot = [IO.Path]::GetFullPath($Output, $repo)
if (Test-Path -LiteralPath $outputRoot) { throw "Output already exists: $outputRoot" }

Push-Location $repo
try {
    if (-not $Dotnet) {
        $candidates = @(
            (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue).Source
            if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
            Join-Path $env:LOCALAPPDATA 'MeitouClient-dotnet11/dotnet.exe'
            Join-Path $HOME '.dotnet/dotnet.exe'
        )
        $PSNativeCommandUseErrorActionPreference = $false
        foreach ($candidate in ($candidates | Select-Object -Unique)) {
            if (-not $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }

            $sdkVersion = & $candidate --version 2>$null
            if ($LASTEXITCODE -eq 0) { $Dotnet = $candidate; break }
        }
        if (-not $Dotnet) { throw 'No installed SDK satisfies global.json. Supply -Dotnet with a compatible dotnet.exe.' }
    }

    $changelog = [IO.File]::ReadAllText((Join-Path $repo 'launcher/CHANGELOG.md'))
    $version = [regex]::Match($changelog, '(?m)^## v(\S+)').Groups[1].Value
    $name = "MeitouCoopLauncher-$version-win-x64-portable"
    $stage = Join-Path $outputRoot $name
    $publishOptions = @(if ($NativeAot) { '-p:PublishAot=true' } else { '-p:PublishAot=false'; '-p:PublishTrimmed=false' })
    & $Dotnet publish (Join-Path $repo 'launcher/Meitou.Launcher') -c Release -r win-x64 --self-contained true `
        -p:Version=$version -o $stage @publishOptions
    if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed: $LASTEXITCODE" }

    foreach ($license in @('LICENSE', 'LICENSE-EXCEPTION.md', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $repo $license) -Destination $stage
    }
    $zip = Join-Path $outputRoot "$name.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $outputRoot 'SHA256SUMS.txt'), "$hash  $name.zip`n")
    Write-Output "Launcher: $(Join-Path $stage 'meitou-launcher.exe')"
    Write-Output "Portable package: $zip"
} finally {
    Pop-Location
}
