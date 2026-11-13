# 上游同步基准

**Verified**（2026-11-13，Git 远端快照核对）：上游为
[VlcekM/MeitouClient](https://github.com/VlcekM/MeitouClient)，基准为
`7379edb6049b969d851d0fe0cee17d15f75b9dcc`。

复核纳入关系：`git merge-base --is-ancestor 7379edb6049b969d851d0fe0cee17d15f75b9dcc HEAD`。

**Observed**（当前实现）：共享内容与独立模组程序集见[架构](engine.md)；玩家身份、权限、
资源和检查点见[联机契约](multiplayer.md)。客户端与服务器须匹配[构建身份](networking.md)；
协议 21 的编码与验收门禁见[传输契约](networking-next.md)。矿点机器恢复与阵营归属见
[存档格式](formats/save.md)，远端照片模式的时间权限见[查看器](viewer.md)，
武器血迹的事件可见性与历史限制见[血迹](game/blood-decals.md)。

## 验证范围

**Verified**（Windows x64，SDK 11.0.100、.NET 11.0.0；真实 Kenshi 数据、Release 构建日志及 xUnit XML）：

| 检查 | 结果 |
|---|---|
| Solution 默认 Release 构建 | 0 警告，0 错误 |
| Release Metal 条件编译（`-p:MeitouMetal=true`） | 0 警告，0 错误 |
| .NET 10 目标及 Metal 条件编译 | 0 警告，0 错误 |
| 完整既有套件 | 1474 总计，1445 通过，0 失败，0 错误，29 跳过，0 未运行 |

完整套件运行 `tests/Meitou.Tests/bin/Release/net11.0/Meitou.Tests.dll -parallel none -maxThreads 1`，
不筛选 Slow/Bench。原生 QUIC 使用与当前 `transport.cpp` 内容一致（仅换行符不同）的既有本机构建，
核对清单及 DLL 的 SHA-256，MsQuic 为仓库锁定的 2.6.3。29 项跳过：22 项需要 Vulkan 设备，
2 项限其他平台，3 项需要旧检查点，1 项缺少含排队订单的存档，1 项仅在 Debug 配置运行。
既有 `HeadlessContentTests.InstalledWorldLoadsAndTicksWithoutAnyRenderingAssembly` 验证真实粒子状态在检查点恢复后继续推进一致；
粒子检查点版本与旧版读取规则见[服务器契约](server.md)。证据位于仓库外 `pr1-review-results/`，完整报告为 `full-final.xml`。

**Unknown**：未提供的旧检查点与排队订单存档、完整图形化双客户端权威和可见性、Vulkan/Metal 图形运行及 VPS 验收，
以及 Linux、macOS 和 .NET 10 兼容包的实机运行。条件编译通过不代表对应平台或 GPU 已通过运行验收。合并前在本地游戏环境执行
[联机验收](multiplayer.md)与[传输门禁](networking-next.md)，客户端和服务器均使用当前构建身份。
