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

**Verified**（Linux x64，SDK 11.0.100、.NET 11.0.0；Release 构建日志及 xUnit XML）：

| 检查 | 结果 |
|---|---|
| Solution 默认 Release 构建 | 0 警告，0 错误 |
| Release Metal 条件编译（`-p:MeitouMetal=true`） | 0 警告，0 错误 |
| 完整既有套件 | 1474 总计，1257 通过，0 失败，0 错误，217 跳过，0 未运行 |

完整套件运行 `tests/Meitou.Tests/bin/Release/net11.0/Meitou.Tests.dll -parallel none -maxThreads 1`，
不筛选 Slow/Bench。217 项跳过：140 项需要 Kenshi 安装，46 项需要 Windows x64 原生 QUIC 集成环境，
23 项需要 Vulkan 设备，5 项缺少可信原生 QUIC 产物，另有 macOS CrossOver、Windows 黄金哈希、
Debug 配置检查各 1 项。构建和完整套件均未使用真实游戏数据。

**Unknown**：真实 Kenshi 存档与模组兼容性、双客户端权威和可见性、Windows QUIC、
Vulkan/Metal 图形运行及 VPS 验收；本基准未复验 .NET 10 兼容构建。
条件编译通过不代表对应平台或 GPU 已通过运行验收。合并前在本地游戏环境执行
[联机验收](multiplayer.md)与[传输门禁](networking-next.md)，客户端和服务器均使用当前构建身份。
