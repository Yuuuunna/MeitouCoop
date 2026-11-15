# 上游同步基准

**Verified**（2026-11-15，Git 远端核对）：上游为
[VlcekM/MeitouClient](https://github.com/VlcekM/MeitouClient)，当前纳入基准为
`704cc1ec281209d628c83c608b8cf5c4b7556647`（提交时间 2026-11-15 09:33:27 UTC）。
从原基准 `a69c6e9b07e58c9d078f084459aaca6beb250f5c` 起纳入 131 个提交；
Coop 主分支基线为 `ccec4b7516e1595ee7673d2157c161a96bd244e7`。
用 `git merge-base --is-ancestor <上述提交> HEAD` 分别核对三者的纳入关系。

**Observed**（当前实现）：客户端与服务端须匹配构建身份
`meitou-upstream-704cc1ec-authority-1`；传输 framing 仍为 protocol 21。新增命令与快照字段见
[网络契约](networking.md#mod-presentation-and-new-inspection-state)，带宽计量和验收门禁见
[传输契约](networking-next.md#upstream-presentation-budget)。

自定义技能、医疗条、骨骼挂件、检查建筑供电及偷窃概率均由服务器发布。
医疗条和技能只向实际使用这些面板的角色发送；静态挂件只在变更时发送引用。
编辑器开窗恢复头发使用服务器签发的会话并防止重放；远程调试器不能直接改角色属性。
囚犯对话、动物喂食/交易、持续睡眠、招募与存档载入保持各玩家的权限及会话边界。
静态建筑检查使用当前建筑列表和毁坏状态，且只授予观察，不扩大生产或库存权限。
服务端 WorkSites、NPC 工作与资产管理分离等既有契约见[联机契约](multiplayer.md)。

联机版自加补丁及回归上游条件：[空手受击经验](game/character-stats.md#levelling-xp)。

## 验证范围

**Verified**（Linux x64，SDK 11.0.100、.NET 11.0.0；构建日志及 xUnit XML；未配置 Kenshi 安装）：

| 检查 | 结果 |
|---|---|
| Solution 默认 Release 构建 | 0 警告，0 错误 |
| Release Metal 条件编译（`MeitouMetal=true`） | 0 警告，0 错误 |
| 完整既有套件，不筛选 Slow/Bench | 1681 总计，1450 通过，0 失败，0 错误，231 跳过，0 未运行 |

完整套件运行 `tests/Meitou.Tests/bin/Release/net11.0/Meitou.Tests.dll -parallel none -maxThreads 1`。
构建通过 SDK 的 MSBuild 入口执行，`Configuration=Release`、`UseSharedCompilation=false`；Metal 检查另加 `MeitouMetal=true`，
并设置 `AVALONIA_TELEMETRY_OPTOUT=1`。同步上游及仓库已有测试，未额外新建测试用例。

231 项跳过按实际测试原因归类：150 项需要 Kenshi 安装，52 项需要 Windows 原生 QUIC 或受信原生构建，
23 项需要 Vulkan 设备，3 项需要特定原始存档，另各 1 项需要 Debug 配置、macOS、Windows 固定哈希环境。
这些条件未被修改为强行通过；测试保持原有断言与跳过条件。

**Unknown**：真实 Kenshi 数据与存档运行、图形化双客户端交互、新模组面板与挂件渲染、
囚犯招募/动物交易/手术编辑实际交互、原生 QUIC 与 VPS 总流量，以及 Windows/macOS 和 .NET 10 兼容包验收。
序列化计量不包含传输开销，不能替代每玩家平均 500 KiB/s 的实机验收。
合并前在本地游戏环境执行[联机验收](multiplayer.md)与[传输门禁](networking-next.md)，
客户端和服务端使用相同构建身份。条件编译通过不代表对应平台或 GPU 已通过运行验收。
