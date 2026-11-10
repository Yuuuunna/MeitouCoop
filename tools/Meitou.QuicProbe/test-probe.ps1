param([string]$Executable, [string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
if (!$Executable) { $Executable = Join-Path $PSScriptRoot '../../artifacts/network-vnext-platform/probe-win-x64/Meitou.QuicProbe.exe' }
$Executable = (Resolve-Path $Executable).Path
if (!$EvidenceDirectory) { $EvidenceDirectory = Join-Path (Split-Path (Split-Path $Executable)) ('probe-validation-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
$EvidenceDirectory = (Resolve-Path $EvidenceDirectory).Path
$results = @()
function Assert-True($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Invoke-Probe([string]$Name, [string[]]$ProbeArguments, [bool]$ExpectedPass, [string]$Exe = $Executable) {
    $out = Join-Path $EvidenceDirectory "$Name.json"
    & $Exe @ProbeArguments --output $out | Out-Null
    $exit = $LASTEXITCODE
    $report = Get-Content -Raw -LiteralPath $out | ConvertFrom-Json
    Assert-True ($report.passed -eq $ExpectedPass) "${Name}: unexpected passed flag"
    Assert-True (($exit -eq 0) -eq $ExpectedPass) "${Name}: unexpected exit code $exit"
    Assert-True ($report.liveBuffers -eq 0 -and $report.liveConnectionHandles -eq 0 -and $report.liveStreamHandles -eq 0) "${Name}: leaked resources"
    Assert-True ($report.buffersAllocated -eq $report.buffersReleased) "${Name}: buffer ownership imbalance"
    if ($ExpectedPass) {
        Assert-True ($report.tlsProvider -eq 'OpenSSL') "${Name}: wrong TLS provider"
        foreach ($c in $report.connections) {
            Assert-True ($c.success -and $c.connected -and $c.shutdownComplete -and $c.transportStatus -eq 0) "${Name}: incomplete connection lifecycle"
            Assert-True ($c.tlsVersion -eq 12288 -and $c.cipherStrength -ge 128) "${Name}: TLS1.3 encryption absent"
            Assert-True ($c.streamApplicationAck -and $c.applicationDatagramAckMask -eq 7 -and $c.datagramReceivedMask -eq 7) "${Name}: missing application receipt"
            Assert-True ($c.datagramTerminalEvents -eq 3 -and $c.datagramCanceled -eq 0) "${Name}: incomplete DATAGRAM ownership lifecycle"
            Assert-True ($c.datagramSizes[2] -eq [Math]::Min(4096, $c.datagramLimitAtSend)) "${Name}: negotiated DATAGRAM boundary not exercised"
            Assert-True ($c.pathMtu -ge 1200 -and $c.udpSentBytes -gt 0 -and $c.udpReceivedBytes -gt 0) "${Name}: missing UDP/MTU evidence"
        }
    }
    return $report
}
function Add-Result([string]$Name, $Report) {
    $script:results += [ordered]@{ case = $Name; passed = $true; probePassed = $Report.passed; probeError = $Report.error }
}
function Quote([string]$Value) { return '"' + $Value.Replace('"', '\"') + '"' }
$certificate = Join-Path $EvidenceDirectory 'certificate'
$wrongCertificate = Join-Path $EvidenceDirectory 'wrong-certificate'
& $Executable --create-certificate $certificate | Out-Null
Assert-True ($LASTEXITCODE -eq 0) 'Certificate generation failed'
& $Executable --create-certificate $wrongCertificate | Out-Null
Assert-True ($LASTEXITCODE -eq 0) 'Wrong-CA certificate generation failed'
$pfx = Join-Path $certificate 'server.pfx'; $ca = Join-Path $certificate 'server.pem'
$baseArguments = @('--loopback','--certificate',$pfx,'--ca',$ca,'--timeout-seconds','10')
Add-Result 'loopback-reconnect' (Invoke-Probe 'loopback-reconnect' ($baseArguments + @('--rounds','2','--port','47421')) $true)
Add-Result 'wrong-hostname-rejected' (Invoke-Probe 'wrong-hostname-rejected' ($baseArguments + @('--rounds','1','--port','47423','--server-name','wrong.invalid')) $false)
Add-Result 'wrong-ca-rejected' (Invoke-Probe 'wrong-ca-rejected' @('--loopback','--certificate',$pfx,'--ca',(Join-Path $wrongCertificate 'server.pem'),'--rounds','1','--port','47424','--timeout-seconds','10') $false)
# Independent processes exercise the deployable listen/connect modes.
$serverOut = Join-Path $EvidenceDirectory 'server.json'
$serverLog = Join-Path $EvidenceDirectory 'server-stderr.txt'
$serverArgs = @('--listen','47425','--certificate',$pfx,'--rounds','2','--timeout-seconds','15','--output',$serverOut)
$serverLine = ($serverArgs | ForEach-Object { Quote $_ }) -join ' '
$process = Start-Process -FilePath $Executable -ArgumentList $serverLine -WindowStyle Hidden -PassThru -RedirectStandardError $serverLog -RedirectStandardOutput (Join-Path $EvidenceDirectory 'server-stdout.json')
$null = $process.Handle # Retain OS process handle for ExitCode on Windows PowerShell 5.1.
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if ($process.HasExited) { throw 'Listen process exited before readiness' }
        if ((Test-Path -LiteralPath $serverLog) -and (Get-Content -Raw -LiteralPath $serverLog) -match 'Listening on UDP') { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert-True ([DateTime]::UtcNow -lt $deadline) 'Listen readiness timeout'
    Add-Result 'independent-client' (Invoke-Probe 'independent-client' @('--connect','127.0.0.1','--port','47425','--ca',$ca,'--rounds','2','--timeout-seconds','10') $true)
    Assert-True ($process.WaitForExit(15000)) 'Listen process did not finish'
    Assert-True ($process.ExitCode -eq 0) 'Listen process failed'
    $server = Get-Content -Raw -LiteralPath $serverOut | ConvertFrom-Json
    Assert-True ($server.passed -and $server.liveBuffers -eq 0 -and $server.liveConnectionHandles -eq 0 -and $server.liveStreamHandles -eq 0) 'Server resource or ACK failure'
    Add-Result 'independent-server' $server
} finally { if (!$process.HasExited) { Stop-Process -Id $process.Id } }
Add-Result 'unreachable-timeout' (Invoke-Probe 'unreachable-timeout' @('--connect','127.0.0.1','--port','47426','--ca',$ca,'--rounds','1','--timeout-seconds','1') $false)
# Never alter the deployment DLL. Corrupt only a separate validation copy.
$tamperDirectory = Join-Path $EvidenceDirectory 'tampered-native'
New-Item -ItemType Directory -Force $tamperDirectory | Out-Null
Copy-Item -LiteralPath $Executable -Destination $tamperDirectory
$dll = [IO.File]::ReadAllBytes((Join-Path (Split-Path $Executable) 'msquic.dll'))
$dll[$dll.Length - 1] = $dll[$dll.Length - 1] -bxor 1
[IO.File]::WriteAllBytes((Join-Path $tamperDirectory 'msquic.dll'), $dll)
$tamperedExe = Join-Path $tamperDirectory 'Meitou.QuicProbe.exe'
$tampered = Invoke-Probe 'dll-hash-mismatch-rejected' ($baseArguments + @('--rounds','1')) $false $tamperedExe
Assert-True ($tampered.error -eq 'Pinned msquic.dll SHA256 mismatch') 'Tampered DLL was not refused before load'
Add-Result 'dll-hash-mismatch-rejected' $tampered
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'validation.json') -Encoding Ascii
Write-Output "Passed $($results.Count) native probe checks. Evidence: $EvidenceDirectory"
Write-Output 'Evidence directory contains local test PFX keys; do not share it. Share only JSON reports and public PEM as needed.'
