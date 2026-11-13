#requires -Version 7.0
<#
.SYNOPSIS
Publishes and packages the Windows x64 multiplayer client (with mod-check tools) and server.
.DESCRIPTION
Uses a compatible installed SDK from PATH or a local user installation; no SDK downloads or permanent environment changes.
.EXAMPLE
pwsh -File ./export-packages.ps1
.EXAMPLE
pwsh -File ./export-packages.ps1 -Output artifacts/releases/my-build
.EXAMPLE
pwsh -File ./export-packages.ps1 -GraphicsRuntimePath ./dlss-runtime
.PARAMETER GraphicsRuntimePath
Optional vendor runtime folder (DLLs with licenses/ or LICENSE* files). Only the client receives it.
Defaults to MEITOU_DLSS_PATH, MEITOU_STREAMLINE_PATH, then dlss-runtime; missing optional libraries fall back to TAA.
Use tools/scripts/package-release.ps1 to fetch/build the pinned vendor runtimes for a full release.
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Output = ('artifacts/releases/multiplayer-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')),
    [ValidateNotNullOrEmpty()]
    [string] $ValidationNote = 'Not yet validated in a live game.',
    [string] $GraphicsRuntimePath = '',
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')]
    [string] $Version = ('0.0.0-local.' + (Get-Date -Format 'yyyyMMddHHmmss'))
)

$ErrorActionPreference = 'Stop'

function Resolve-CompatibleDotnet {
    $dotnetName = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
    $candidates = @(
        Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
        if ($env:LOCALAPPDATA) {
            Join-Path $env:LOCALAPPDATA "MeitouClient-dotnet11/$dotnetName"
        }
        if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT $dotnetName }
        if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Microsoft/dotnet/$dotnetName" }
        Join-Path $HOME ".dotnet/$dotnetName"
    )

    # Let global.json select the SDK; an installed host alone does not establish compatibility.
    $PSNativeCommandUseErrorActionPreference = $false
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }

        try {
            $version = (& $candidate --version 2>$null | Out-String).Trim()
            if ($LASTEXITCODE -eq 0) {
                return @{ Path = $candidate; Version = $version }
            }
        } catch {
            # An unusable host must not prevent checking the remaining installations.
            continue
        }
    }

    throw 'No installed .NET SDK satisfies global.json. Install the required SDK or set DOTNET_ROOT to a compatible installation.'
}

$repo = $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath($Output, $repo)
if (Test-Path -LiteralPath $outputRoot) {
    throw "Output already exists; choose a new folder: $outputRoot"
}
Get-Command git -ErrorAction Stop | Out-Null

# Match Directory.Build.targets' optional runtime discovery; no downloads during multiplayer exports.
if (-not $GraphicsRuntimePath) { $GraphicsRuntimePath = $env:MEITOU_DLSS_PATH }
if (-not $GraphicsRuntimePath) { $GraphicsRuntimePath = $env:MEITOU_STREAMLINE_PATH }
if (-not $GraphicsRuntimePath) { $GraphicsRuntimePath = Join-Path $repo 'dlss-runtime' }
$graphicsRuntimeRoot = [IO.Path]::GetFullPath($GraphicsRuntimePath, $repo)

# A fresh staging directory prevents stale binaries or local settings entering a package.
$stageRoot = Join-Path $repo ('artifacts/multiplayer-stage/' + [guid]::NewGuid().ToString('N'))
Push-Location $repo
try {
    $sdk = Resolve-CompatibleDotnet
    Write-Host "Using .NET SDK $($sdk.Version): $($sdk.Path)"
    New-Item -ItemType Directory -Path $stageRoot | Out-Null

    foreach ($target in @(
        @{ Kind = 'client'; Project = 'src/Meitou.Game/Meitou.Game.csproj' },
        @{ Kind = 'server'; Project = 'src/Meitou.Server/Meitou.Server.csproj' }
    )) {
        Write-Host "Publishing $($target.Kind) (Release, win-x64, self-contained)..."
        $runtimeArgs = @(if ($target.Kind -eq 'client') { "-p:StreamlineRuntimePath=$graphicsRuntimeRoot" })
        & $sdk.Path publish (Join-Path $repo $target.Project) -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false `
            -p:Version=$Version -p:InformationalVersion=$Version -p:BuildGitTag=v$Version `
            -o (Join-Path $stageRoot $target.Kind) @runtimeArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Publishing $($target.Kind) failed (exit code $LASTEXITCODE). Staging: $stageRoot"
        }
    }

    # Tools reference the game executable and carry dependency runtimeconfig/deps copies. Keep the game's own
    # publish metadata; shared engine/runtime binaries must still come from the same build.
    $toolsStage = Join-Path $stageRoot 'tools'
    $clientStage = Join-Path $stageRoot 'client'
    Write-Host 'Publishing mod-check tools (Release, win-x64, self-contained)...'
    & $sdk.Path publish (Join-Path $repo 'tools/Meitou.Tools/Meitou.Tools.csproj') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false `
        -p:Version=$Version -p:InformationalVersion=$Version -p:BuildGitTag=v$Version -o $toolsStage
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing tools failed (exit code $LASTEXITCODE). Staging: $stageRoot"
    }
    $toolFiles = @(Get-ChildItem -LiteralPath $toolsStage -File -Recurse)
    foreach ($toolFile in $toolFiles) {
        $relative = [IO.Path]::GetRelativePath($toolsStage, $toolFile.FullName)
        $clientFile = Join-Path $clientStage $relative
        if (Test-Path -LiteralPath $clientFile) {
            if ($relative -in @('MeitouClient.deps.json', 'MeitouClient.runtimeconfig.json')) { continue }
            if (-not (Test-Path -LiteralPath $clientFile -PathType Leaf) -or
                (Get-FileHash -LiteralPath $toolFile.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $clientFile -Algorithm SHA256).Hash) {
                throw "Client/tools publish mismatch: $relative. Export again from one unchanged checkout. Staging: $stageRoot"
            }
        }
    }
    foreach ($toolFile in $toolFiles) {
        $relative = [IO.Path]::GetRelativePath($toolsStage, $toolFile.FullName)
        $clientFile = Join-Path $clientStage $relative
        if (-not (Test-Path -LiteralPath $clientFile)) {
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($clientFile)) -Force | Out-Null
            Copy-Item -LiteralPath $toolFile.FullName -Destination $clientFile
        }
    }

    & (Join-Path $repo 'tools/scripts/package-multiplayer.ps1') `
        -Stage $stageRoot -Output $outputRoot -ValidationNote $ValidationNote -Version $Version
    Write-Host "Packages: $outputRoot"
    Write-Host "Published files: $stageRoot"
} finally {
    Pop-Location
}
