param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/network-vnext-transport/win-x64' }
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $OutputDirectory
$previousDirectory = $env:MEITOU_QUIC_NATIVE_DIRECTORY
$previousHash = $env:MEITOU_QUIC_SHIM_SHA256
try {
    $env:MEITOU_QUIC_NATIVE_DIRECTORY = (Resolve-Path $OutputDirectory).Path
    $manifest = Get-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Raw | ConvertFrom-Json
    # This manifest comes from the build performed above, rather than an untrusted remote artifact.
    $env:MEITOU_QUIC_SHIM_SHA256 = $manifest.shimSha256
    & dotnet test (Join-Path $repo 'tests/Meitou.Tests/Meitou.Tests.csproj') --filter 'FullyQualifiedName~NativeQuicTransportTests' -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Native transport smoke failed: $LASTEXITCODE" }
} finally {
    $env:MEITOU_QUIC_NATIVE_DIRECTORY = $previousDirectory
    $env:MEITOU_QUIC_SHIM_SHA256 = $previousHash
}
