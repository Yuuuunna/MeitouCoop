# 上游同步基准

**Verified**（2026-11-16，Git 与 GitHub 远端核对）：上游为
[VlcekM/MeitouClient](https://github.com/VlcekM/MeitouClient)，当前纳入基准为
`5108032d29e1b64d6695d39251dedd0a48786d61`（提交时间 2026-11-16 05:45:03 UTC）。
从已对齐的 `704cc1ec281209d628c83c608b8cf5c4b7556647` 起纳入 211 个提交；
Coop 主分支基线为 `f38e53d678dce0265950254bb74965f06f6bc50f`。
用 `git merge-base --is-ancestor <上述提交> HEAD` 核对纳入关系。

**Observed**（当前实现）：客户端与服务端须匹配构建身份
`meitou-upstream-5108032d-authority-1-combat-sync-1`，传输 framing 仍为 protocol 21。
物品与交易同步见[网络契约](networking.md#item-actions-and-shared-stock)，
本次带宽计量见[库存与交易预算](networking-next.md#inventory-and-trade-budget)。

## 联机适配范围

**Observed**（源代码）：

- 门侧区域、跨区导航、建筑与墙体碰撞采用当前上游逻辑，保留 Coop 的异步取消、版本与世界生命周期边界。
- 新 NPC 工作、奴隶工作、搬运和尸体处理在权威模拟执行；角色、机器、音效与成交浮字通过既有授权视图和事件同步。
- 常驻城镇机器继续使用 Coop 的 canonical ID、资产权限与单一保存路径；机器库存采用原生 `in1..`、`backpack_content`，兼容旧数据。
- 建筑商店以实体容器保存库存；共享交易视图逐操作对账，保留各玩家的交易/容器会话及库存版本，拖拽物品可归还至重建后的商店。
- 物品使用回调验证角色控制权、库存版本和标签身份。完整标签保留在模拟、容器、义肢、地面物品与存档中，网络只发送固定长度摘要。
- 新静态/布局供电检查保留可见性限制；伪用电负载和缓存按城镇及派系电网分开，不跨玩家派系计费。
- `SquadXp`、`OthersXp`、`BagSearchFix` 由服务端统一配置。旧 XP 模组设置只采用一次，覆盖功能的代码模组停用，避免重复倍率。
- `animalage`、派系关系编辑等调试修改仅可在本地世界使用；远端保持命令白名单。

Coop 的离线饥饿暂停、空手受击经验、被奴役玩家队伍归属、笼内物品丢弃、门锁协作、诊断收集和 Steam 显示逻辑保留。
自加补丁及回归上游条件：[空手受击经验](game/character-stats.md#levelling-xp)。

## Coop 自行修复

**Observed**（Git 源码对照）：上游基准 `5108032d` 的 AI Think 并行调用 `TownDutySystem.StorageOf`，
会写入普通存储字典及共用功能字典。Coop 自行修复这两处缓存竞态，线程契约见[模拟线程](simulation.md#threading)。
后续同步时，若上游已修复这两处并发访问，改用上游实现并移除本地补丁及此标记；未覆盖前保留。

**Observed**（Coop 本地实现）：[暗杀感知及显示／执行一致性](game/combat.md#stealth-knock-out)为 Coop 内自行修复。
后续上游覆盖同等行为时，采用上游实现，移除对应本地补丁及此标记；未覆盖前保留。

## 验证范围

**Verified**（2026-11-16，Windows x64、.NET SDK 11.0.100，当前工作区）：

| 检查 | 结果与范围 |
|---|---|
| Release 全解决方案构建 | 14 个项目成功，0 警告、0 错误 |
| Metal 条件编译 | Release、`MeitouMetal=true` 的 14 个项目成功，0 警告、0 错误；未执行 macOS/Metal 运行验收 |
| 完整既有测试套件 | 1,745 项：1,659 通过、86 跳过、0 失败、0 错误；包括本地现有的 20 项 `ServerModSyncTests` |

完整套件包含 `GameFilesTests`、`WorldGateRegionTests`、motion、协议 ACK、Crime、Aggro、Arrest、Determinism 和 GoldenHash；
不筛选 Slow/Bench，不新增测试或削弱断言。命令：
`tests/Meitou.Tests/bin/Release/net11.0/Meitou.Tests.dll -parallel none -maxThreads 1`。
构建串行执行，`Configuration=Release`、`UseSharedCompilation=false`；
Metal 条件编译另加 `MeitouMetal=true`，并设置 `AVALONIA_TELEMETRY_OPTOUT=1`。
跳过项包括不可用的 Vulkan 设备、未配置受信任的原生 QUIC 构建、缺少参考存档及平台/Debug 条件。

**Verified**（Linux .NET 11 合成诊断）：库存/交易 20 组场景的往返、标签身份、畸形压缩输入、展开后容量和失败清理检查通过，
见[计量方法与边界](networking-next.md#inventory-and-trade-budget)。
500 KiB/s 是每玩家平均带宽目标；固定长度标签、压缩、稀疏姿态和 motion/checkpoint 合批减少冗余，未增加限速、包配额或降低更新频率。
合成库存/交易组合最大计量为双向应用层 357.32 KiB/s，不代表整场游戏或实际链路已达标。

真实存档、图形化双客户端、原生 QUIC/VPS 双向总流量及警报/群战 CPU 延迟，按
[联机验收](multiplayer.md)及[传输门禁](networking-next.md)在对应环境验收。
构建和应用层编码计量不替代这些验收；客户端与服务端使用相同构建身份。
