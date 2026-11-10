param([string]$PackagePath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$PackagePath) { $PackagePath = Join-Path $repo 'artifacts/network-vnext-platform/microsoft.native.quic.msquic.openssl.2.6.3.nupkg' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/network-vnext-platform/probe-win-x64' }
$packageHash = '41505c3861ef386c8788dd6d9e0ffea75b275b7d0651c2b940628ab729cf27bd'
$dllHash = '290b6c8716ac09eea043913f86164e0b2f5096c666c9f1ca81c0c5c1864e1cb0'
if (!(Test-Path -LiteralPath $PackagePath)) {
    New-Item -ItemType Directory -Force (Split-Path $PackagePath) | Out-Null
    Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/microsoft.native.quic.msquic.openssl/2.6.3/microsoft.native.quic.msquic.openssl.2.6.3.nupkg' -OutFile $PackagePath
}
if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $packageHash) { throw 'Pinned package SHA256 mismatch' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$packageDirectory = Join-Path (Split-Path $OutputDirectory) 'probe-build-package'
New-Item -ItemType Directory -Force $packageDirectory | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Avoid relying on the overwrite overload, which is absent on Windows PowerShell 5.1.
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $PackagePath))
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $packageDirectory $entry.FullName))
        if (!$target.StartsWith([IO.Path]::GetFullPath($packageDirectory) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package path' }
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $archive.Dispose() }
$native = Join-Path $packageDirectory 'build/native'
$nativeDll = Join-Path $native 'bin/x64/msquic.dll'
if ((Get-FileHash -LiteralPath $nativeDll -Algorithm SHA256).Hash.ToLowerInvariant() -ne $dllHash) { throw 'Pinned DLL SHA256 mismatch' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'MSVC x64 build tools required' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$header = Join-Path $OutputDirectory 'probe-pin.h'
@"
#define MSQUIC_SHA256 "$dllHash"
#define MSQUIC_PACKAGE_SHA256 "$packageHash"
"@ | Set-Content -LiteralPath $header -Encoding Ascii
$exe = Join-Path $OutputDirectory 'Meitou.QuicProbe.exe'
$obj = Join-Path $OutputDirectory 'probe.obj'
$include = Join-Path $native 'include'
$source = Join-Path $PSScriptRoot 'probe.cpp'
# cmd is used only for the compiler environment, never for destructive filesystem operations.
$buildCommand = "call `"$vcvars`" >nul && cl /nologo /std:c++20 /EHsc /O2 /MT /W4 /WX /DWIN32_LEAN_AND_MEAN /DNOMINMAX /D_WIN32_WINNT=0x0A00 /I`"$include`" /FI`"$header`" `"$source`" /Fo`"$obj`" /Fe`"$exe`" /link bcrypt.lib crypt32.lib advapi32.lib ws2_32.lib ntdll.lib /SUBSYSTEM:CONSOLE,6.02"
& $env:ComSpec /d /s /c $buildCommand
if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $OutputDirectory 'msquic.dll')
Copy-Item -LiteralPath (Join-Path $packageDirectory 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.msquic')
Copy-Item -LiteralPath (Join-Path $packageDirectory 'THIRD-PARTY-NOTICES') -Destination (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.msquic')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.Meitou')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $OutputDirectory
$manifest = [ordered]@{
    schema = 1; package = 'Microsoft.Native.Quic.MsQuic.OpenSSL'; version = '2.6.3'; apiVersion = 2
    architecture = 'x64'; tlsProvider = 'OpenSSL'; packageSha256 = $packageHash; dllSha256 = $dllHash
    probeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding Ascii
# Explicit allowlist excludes all generated certificates, reports, object files and game content.
$zip = "$OutputDirectory.zip"
$files = @('Meitou.QuicProbe.exe','msquic.dll','LICENSE.msquic','THIRD-PARTY-NOTICES.msquic','LICENSE.Meitou','README.md','manifest.json') | ForEach-Object { Join-Path $OutputDirectory $_ }
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
Write-Output "Probe: $exe"
Write-Output "Deployment ZIP: $zip"
