param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/network-vnext-transport/win-x64' }
$archivePath = Join-Path $repo 'artifacts/network-vnext-platform/microsoft.native.quic.msquic.openssl.2.6.3.nupkg'
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne '41505c3861ef386c8788dd6d9e0ffea75b275b7d0651c2b940628ab729cf27bd') { throw 'Pinned package mismatch; run QuicProbe/build.ps1 to fetch package' }
$package = Join-Path $repo 'artifacts/network-vnext-transport/build-package'
New-Item -ItemType Directory -Force $package | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $package $entry.FullName))
        if (!$target.StartsWith([IO.Path]::GetFullPath($package) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package path' }
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $archive.Dispose() }
$dll = Join-Path $package 'build/native/bin/x64/msquic.dll'
if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant() -ne '290b6c8716ac09eea043913f86164e0b2f5096c666c9f1ca81c0c5c1864e1cb0') { throw 'Pinned MsQuic DLL mismatch; run QuicProbe/build.ps1 to prepare official package' }
$header = Join-Path $package 'build/native/include/msquic.h'
if (!(Test-Path -LiteralPath $header)) { throw 'Pinned package headers missing' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'MSVC x64 build tools required' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$include = Split-Path $header
$source = Join-Path $PSScriptRoot 'transport.cpp'
$output = Join-Path $OutputDirectory 'Meitou.QuicTransport.dll'
$obj = Join-Path $OutputDirectory 'transport.obj'
$implib = Join-Path $OutputDirectory 'Meitou.QuicTransport.lib'
$command = "call `"$vcvars`" >nul && cl /nologo /std:c++20 /EHsc /O2 /MT /W4 /WX /LD /DWIN32_LEAN_AND_MEAN /DNOMINMAX /D_WIN32_WINNT=0x0A00 /I`"$include`" `"$source`" /Fo`"$obj`" /Fe`"$output`" /link ws2_32.lib ntdll.lib /IMPLIB:`"$implib`" /SUBSYSTEM:WINDOWS,6.02"
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $dll -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $package 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.msquic')
Copy-Item -LiteralPath (Join-Path $package 'THIRD-PARTY-NOTICES') -Destination (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.msquic')
[ordered]@{api=1; architecture='win-x64'; msquicVersion='2.6.3'; tls='OpenSSL'; msquicSha256=(Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant(); shimSha256=(Get-FileHash $output -Algorithm SHA256).Hash.ToLowerInvariant(); sourceSha256=(Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding Ascii

Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.Meitou')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $OutputDirectory
