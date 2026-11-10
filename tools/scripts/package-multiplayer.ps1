param(
    [string] $Stage = 'artifacts/multiplayer-release',
    [string] $Output = 'artifacts/releases/20261031-multiplayer-experimental',
    [string] $ValidationNote = '本包尚未完成实机验收。'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$stageRoot = [IO.Path]::GetFullPath($Stage, $repo)
$outputRoot = [IO.Path]::GetFullPath($Output, $repo)
if (Test-Path -LiteralPath $outputRoot) { throw 'Output already exists; choose a new release folder.' }
foreach ($entry in @('client/MeitouClient.exe', 'client/meitou-tools.exe', 'server/Meitou.Server.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stageRoot $entry))) { throw "Missing published executable: $entry" }
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$readme = @'
Meitou 多人测试包 — 仅输入服务器地址 / Windows x64 / .NET 自包含

服务端和客户端都要更新到此包。两端需要自己的 Kenshi 安装，游戏版本、模组及加载顺序一致。
不含原版资产，无需另装 .NET。客户端需要 Vulkan 1.3；缺少可选 DLSS/FSR 库时回退到 TAA。

1. 启动服务器
解压服务端，双击 start-server.cmd。看到 Listening 后即可连接。默认 localhost:27800。
首次生成 server.json 和 server-data；保留 server-data，它包含身份、证书和世界存档。
输入 save 保存，输入 stop 正常停机。
局域网：停服后将 server.json 的 BindAddress 设为 0.0.0.0、AdvertisedHost 设为服务器局域网 IP，允许 TCP 和 UDP 27800 入站，再启动。TCP 只提供公开连接信息，游戏使用 QUIC/UDP。

2. 连接
解压客户端，双击 start-client.cmd，选择“多人游戏”，输入服务器 IP:端口即可。
同一电脑可填 localhost:27800。不需要账号、密码、签发账号或 connection.json。
首次连接自动建立并记住本机身份，选择出身、完成捏脸后入场；再次使用同一地址连接会恢复原角色。
同机双身份：分别双击 start-client-player1.cmd 和 start-client-player2.cmd，再连接同一服务器。
玩家 1 沿用原有身份；玩家 2 独立保存在 %LOCALAPPDATA%/Meitou/identities-player2，首次连接创建新玩家，以后恢复该玩家。
两个脚本也可在终端附加 --connect 服务器IP:端口；独立身份仅适用于按地址连接，不适用于共享的 connection.json。
新势力由服务端自动分配不重名的名称，例如“无名者 2”。出身包含服务器加载的原版和模组内容，并受服务器角色数量和种族规则限制。

3. 本机身份与重连
本机身份自动保存在 %LOCALAPPDATA%/Meitou/identities，创建恢复记录保存在 Meitou/connections。
不要删除或分享身份文件；换电脑或删除身份会被识别为新玩家。身份按服务器地址保存，建议始终使用同一地址。
不绑定服务器自签名证书指纹，重装或更换服务器不再拦截连接；QUIC/TLS 加密和账号认证仍保留。
断线后重新连接即可；结果未明的创建会按原请求恢复，不重复发放角色。
服务端 AllowDeviceRegistration 默认 true；关闭后阻止新设备注册，已有身份仍可连接。

4. 原版安装
Steam 安装通常自动发现。否则复制 meitou.local.example.json 为 meitou.local.json，并填写 kenshiPath；也可设置 KENSHI_PATH。
meitou.local.json 仅用于定位原版安装，与服务器连接和账号无关。

5. 测试与反馈
此为实验包。验证结果见下方本次构建说明，完整图形交互仍需实机验收。
旧账号仍可通过 MeitouClient.exe --connect connection.json 入场；只填地址会使用独立的设备身份。
升级时替换完整程序文件，不要单独混换 Meitou DLL；保留 server.json、server-data 和私人连接配置。
network 日志记录基线丢失、视图撤回/恢复和状态变化，用于排查暂停及角色消失。
客户端日志为 exe 旁 log.txt；服务端错误显示在控制台。报告时提供日志和复现步骤，不发送身份文件或 server.pfx。
兼容校验默认覆盖数据库和模组加载顺序，GameplayAssets 可追加玩法资产。检查点目前不会自动轮替清理。
客户端包附带 meitou-tools.exe；在终端运行 meitou-tools.exe mod-check <存档目录> 检查代码模组确定性。
'@
$readme += "`r`n本次构建验证：$ValidationNote`r`n"
[IO.File]::WriteAllText((Join-Path $outputRoot '实机测试说明.txt'), $readme)
$head = (git -C $repo rev-parse HEAD).Trim()
foreach ($kind in @('client', 'server')) {
    $name = if ($kind -eq 'client') { 'MeitouClient-Multiplayer-win-x64' } else { 'MeitouServer-Multiplayer-win-x64' }
    $destination = Join-Path $outputRoot $name
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $stageRoot $kind) -File | Where-Object {
        $_.Name -notin @('log.txt', 'meitou.local.json', 'meitou.user.json', 'server.json') -and $_.Extension -in @('.dll', '.exe', '.json', '.pdb', '.config')
    } | Copy-Item -Destination $destination
    # Vendor runtimes are optional client dependencies; retain their nested notices from publish output.
    $quicRuntime = Join-Path (Join-Path $stageRoot $kind) 'native/quic'
    foreach ($required in @('Meitou.QuicTransport.dll', 'msquic.dll', 'manifest.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $quicRuntime $required) -PathType Leaf)) { throw "Missing protocol 21 runtime: $kind/$required" }
    }
    New-Item -ItemType Directory -Path (Join-Path $destination 'native') | Out-Null
    Copy-Item -LiteralPath $quicRuntime -Destination (Join-Path $destination 'native/quic') -Recurse
    $runtimeLicenses = Join-Path (Join-Path $stageRoot $kind) 'licenses'
    if ($kind -eq 'client' -and (Test-Path -LiteralPath $runtimeLicenses -PathType Container)) {
        Copy-Item -LiteralPath $runtimeLicenses -Destination $destination -Recurse
    }
    foreach ($license in @('LICENSE', 'LICENSE-EXCEPTION.md', 'THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $repo $license) -Destination $destination }
    [IO.File]::WriteAllText((Join-Path $destination 'README-实机测试.txt'), $readme)
    [IO.File]::WriteAllText((Join-Path $destination 'meitou.local.example.json'), '{"kenshiPath":"C:\\Games\\Kenshi"}')
    if ($kind -eq 'server') {
        [IO.File]::WriteAllText((Join-Path $destination 'start-server.cmd'), "@echo off`r`ncd /d `"%~dp0`"`r`nMeitou.Server.exe --config server.json`r`npause`r`n")
    } else {
        foreach ($script in @('start-client-player1.cmd', 'start-client-player2.cmd')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $destination
        }
        [IO.File]::WriteAllText((Join-Path $destination 'start-client.cmd'), "@echo off`r`ncd /d `"%~dp0`"`r`nMeitouClient.exe`r`nif errorlevel 1 pause`r`n")
    }
    $assemblyHashes = Get-ChildItem -LiteralPath $destination -File | Where-Object { $_.Name -like 'Meitou*.dll' -or $_.Name -like 'Meitou*.exe' } | ForEach-Object {
        @{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
    @{ configuration='Release'; runtime='win-x64'; selfContained=$true; protocol=21; engine='meitou-experimental-1'; sourceHead=$head; includesUncommittedChanges=$true; assemblies=@($assemblyHashes) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'build-info.json') -Encoding utf8
    [IO.Compression.ZipFile]::CreateFromDirectory($destination, (Join-Path $outputRoot "$name.zip"), [IO.Compression.CompressionLevel]::Optimal, $true)
}
Get-ChildItem -LiteralPath $outputRoot -Filter '*.zip' | ForEach-Object { $hash=Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256; "$($hash.Hash)  $($_.Name)" } |
    Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem -LiteralPath $outputRoot -Filter '*.zip' | Select-Object FullName,Length
