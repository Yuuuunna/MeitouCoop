# 上游同步基准

**Verified**（2026-11-16，Git 与 GitHub 远端核对）：上游为
[VlcekM/MeitouClient](https://github.com/VlcekM/MeitouClient)，当前纳入基准为
`5108032d29e1b64d6695d39251dedd0a48786d61`（提交时间 2026-11-16 05:45:03 UTC）。
从已对齐的 `704cc1ec281209d628c83c608b8cf5c4b7556647` 起纳入 211 个提交；
Coop 主分支基线为 `f38e53d678dce0265950254bb74965f06f6bc50f`。
用 `git merge-base --is-ancestor <上述提交> HEAD` 核对纳入关系。

**Observed**（当前实现）：客户端与服务端须匹配构建身份
`meitou-upstream-5108032d-authority-1`，传输 framing 仍为 protocol 21。
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

## 验证范围

**Verified**（2026-11-16，Linux x64、.NET SDK 11.0.100）：

| 检查 | 结果与范围 |
|---|---|
| Release 全解决方案构建 | 14 个项目成功，包含当前物品标签、压缩和权威同步实现 |
| Metal 条件编译 | Release、`MeitouMetal=true` 的 14 个项目全部成功，无警告、错误；未执行 macOS/Metal 运行验收 |
| 完整既有测试套件 | 1,725 项：1,490 通过、235 跳过、0 失败、0 错误；67.885 秒 |
| 带宽与编码诊断 | 20 组合成场景的基线/变化量往返通过；标签身份、畸形压缩输入、展开后容量和失败清理检查通过，见[计量方法与边界](networking-next.md#inventory-and-trade-budget) |
| 基准元数据 | `bench/tools/generate.py --check --ref refs/remotes/upstream/master` 通过：5 台机器、340 行、41 个提交；历史稳定标签缺失及历史非目标提交为提示 |

完整既有套件不筛选 Slow/Bench：
`tests/Meitou.Tests/bin/Release/net11.0/Meitou.Tests.dll -parallel none -maxThreads 1`。
构建通过 SDK 的 MSBuild 入口执行，`Configuration=Release`、`UseSharedCompilation=false`；
Metal 条件编译另加 `MeitouMetal=true`，并设置 `AVALONIA_TELEMETRY_OPTOUT=1`。
纳入上游测试并适配既有用例，没有额外新建测试用例。

235 项跳过包括未配置 Kenshi 安装或参考存档、不可用的 Vulkan 设备、Windows 原生 QUIC、缺少原生构建产物及平台/Debug 条件。
500 KiB/s 是每玩家平均带宽目标，本次通过固定长度标签身份和无状态压缩减小数据量，没有新增限速或降低发布频率；
合成库存/交易组合最大计量为双向应用层 357.32 KiB/s，不代表整场游戏或实际链路已达标。

真实 Kenshi 数据与存档、图形化双客户端、原生 QUIC/VPS 双向总流量与平台运行验收，按
[联机验收](multiplayer.md)及[传输门禁](networking-next.md)在安装环境完成。
条件编译和应用层编码计量不替代这些验收；客户端与服务端使用相同构建身份。
