# 上游同步基准

**Verified**（2026-11-15，UTC+8，Git 远端核对）：上游为
[VlcekM/MeitouClient](https://github.com/VlcekM/MeitouClient)，当前纳入基准为
`a69c6e9b07e58c9d078f084459aaca6beb250f5c`（提交时间 2026-11-14 16:56:10 UTC）。

复核纳入关系：`git merge-base --is-ancestor a69c6e9b07e58c9d078f084459aaca6beb250f5c HEAD`。

**Observed**（当前实现）：客户端与服务端须匹配构建身份
`meitou-upstream-a69c6e9b-authority-2`；传输 framing 仍为 protocol 21。新增命令与快照字段见
[网络契约](networking.md)，编码、身份检查和验收门禁见[传输契约](networking-next.md)。

服务端 `WorkSites` 为首次自然矿点、城镇及室内机台提供可信目标和版本；目录查询不启动生产。
自然资源源同时包含生成矿石和关卡直接放置的资源建筑。NPC 工作与资产管理权限分离，
真实奴隶工作 189 进入生产、190 保留假工作；具体边界见[联机契约](multiplayer.md)及
[生产系统](game/buildings-production.md#coop-town-work-authority)。室内共享库存、规范机台身份、
旧农田机器迁移和原生状态保存见[存档格式](formats/save.md)。

## 验证范围

**Verified**（Linux x64，SDK 11.0.100、.NET 11.0.0；构建日志及 xUnit XML；未配置 Kenshi 安装）：

| 检查 | 结果 |
|---|---|
| Solution 默认 Release 构建 | 0 警告，0 错误 |
| Release Metal 条件编译（`MeitouMetal=true`） | 0 警告，0 错误 |
| 完整既有套件，不筛选 Slow/Bench | 1641 总计，1412 通过，0 失败，0 错误，229 跳过，0 未运行 |
| 其中 `ProductionTests` | 82 通过，10 跳过 |
| 其中 `PlayerTownImportTests` | 46 通过 |

完整套件运行 `tests/Meitou.Tests/bin/Release/net11.0/Meitou.Tests.dll -parallel none -maxThreads 1`。
构建通过 SDK 的 MSBuild 入口执行，`Configuration=Release`、`UseSharedCompilation=false`；Metal 检查另加 `MeitouMetal=true`。
同步并适配上游及仓库已有测试；未额外新建测试用例。

229 项跳过按实际测试原因归类：149 项需要 Kenshi 安装，51 项需要 Windows 原生 QUIC 或受信原生构建，
23 项需要 Vulkan 设备，3 项需要特定原始存档，另各 1 项需要 Debug 配置、macOS、Windows 固定哈希环境。
这些条件未被修改为强行通过；测试保持原有断言与跳过条件。

**Unknown**：真实 Kenshi 数据与存档运行、图形化双客户端交互、首次采矿和竞争认领、NPC 机台及奴隶工作的
实际动画/寻路/产出、原生 QUIC 与 VPS 运行，以及 Windows/macOS 和 .NET 10 兼容包验收。
条件编译通过不代表对应平台或 GPU 已通过运行验收。合并前在本地游戏环境执行
[联机验收](multiplayer.md)与[传输门禁](networking-next.md)，客户端和服务端使用相同构建身份。
