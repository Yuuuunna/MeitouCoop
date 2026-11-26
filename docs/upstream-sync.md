# 上游同步基准

**Verified**（2026-11-27，Git 提交区间及 GitHub 远端）：当前对齐
[VlcekM/MeitouClient](https://github.com/VlcekM/MeitouClient) 的
[`d8e3e880982a71ed4c3e7d8923de5fac3d5065c3`](https://github.com/VlcekM/MeitouClient/commit/d8e3e880982a71ed4c3e7d8923de5fac3d5065c3)，
提交时间为 2026-11-27 01:02:21（UTC+8）。相对 `e975a97d350bca4a62d9ed671c62a1c47619ae06`
共 71 个提交，含 67 个非合并提交。

**Observed**（当前实现）：客户端与服务器共同使用 `meitou-upstream-d8e3e880-authority-1`；旧构建拒绝准入，
两端须同时更新。传输 framing 仍为 protocol 21。权限、接收者隔离和编码见[网络契约](networking.md)
及 [wire 契约](networking-next.md)。

## 联机适配范围

**Observed**（源代码）：

| 功能 | 权威执行与同步 |
| --- | --- |
| 制作站名称目录 | 按接收者所选派系、友好关系及管理权限投影；字段 1073 按 canonical site ID 独立差量更新，撤权和迟到基线遵守 collection 修订规则。 |
| 取下已开锁枷锁 | 命令 156 绑定穿戴者的控制及 membership grant；执行时重验锁与装备。私有字段 2111 提供可操作状态，物品变化走现有库存同步，声音只交给该角色控制者。 |
| 背包暂停与 Drop from loot | 暂停、焦点、关闭后 0.5 秒 jobs 恢复延迟及 held-item custody 均使用实际玩家会话；服务器规则进入兼容摘要、保存恢复及远端只读设置。 |
| 城墙连线与接点 | 一条线只携带一次意图；服务器依据实时节点、研究和地形再生并核对片段，拒绝伪造 snap、错序及无效地形。仅当前命令已成功放置的工地参与 queued-ghost 规则。 |
| Extra Extensions 变量与物品动作 | 世界状态变量、条件及角色 mod 标签在权威端计算并保存；对话身份、余额和权限预检先于扣物及变量变化。可见结果复用库存、对话及世界状态组件。 |
| 流式导航 | 未加载区域请求保留已验证前缀；严格权威模式等待导航发布后继续原命令，旧 world incarnation 查询不能覆盖新世界的加载提示。 |
| 战斗、逮捕与胜利事件 | 战斗 getter、原始武器技能、动物缩放、dodge、XP、隐匿与锁门伤害由服务器计算；胜利声音与对话沿现有可见事件通道同步。 |

上游 AI 代理队长、医疗连续患者选择、生产、交易、存档、导航网格、CJK 字体、live portraits、模组物品图标、
显存管理、天空云层、FSR、编辑器、启动器及本地化更新已纳入。纯图形选项由客户端本地呈现；权威玩法结果继续通过
现有角色、建筑、库存、事件及保存链传递。保留 Coop 应用身份、发行源、权限模型和多客户端开局。

## 带宽

500 KiB/s/玩家仍为平均优化目标；无新增硬限速、降频、量化或事件丢弃。

**Verified**（2026-11-27 UTC+8，生产 publisher／replica／codec 合成诊断）：128 个制作站每秒改名一次，keyed 目录将
该项增量由 0.7694 降至 0.1177 KiB/s，减少约 84.7%。在每秒 100 个已有 turret 声音的背景上，再加入
每秒一次站点改名、10 次枷锁状态变化及 10 个新声音，合计增量为 1.0973 KiB/s。100 次静态捕获无组件重发。
单次枷锁命令含 reliable framing 为 279 B；四片城墙附一条线意图增加 168 B/次确认。

该合成组合的完整速率为 280.7000 应用 KiB/s，或 320.5250 KiB/s（逐应用消息另加 96 B 的预算模型）；
它没有复现旧基准的完整模组视觉、标签及库存负载，不能替代旧 498.00 KiB/s 场景的重新测量，也不能直接相加后
宣称真实链路达标。负载、边界和完整计量见[交互增量测量](networking.md#crafting-and-interaction-increment)。

## Coop 保留修复

**Observed**（Coop 本地实现）：以下行为继续保留，后续上游提供等价覆盖时再移除相应补丁：

- [服从奴隶的逮捕保护](game/economy.md#118-bounty-spotting-and-bounty-hunters)：涵盖 NPC 攻击、排队订单及进行中的逮捕。
- [暗杀感知与显示／执行一致性](game/combat.md#stealth-knock-out)。
- [笼内拾取地面物品](game/carry-loot.md#ground-items-from-a-cage)：保留与原版一致的允许行为。

## 验证与本地验收

**Verified**（2026-11-27，本地化检查）：九种语言的 1,945 条消息完整，模板与源代码一致。

**Verified**（生产网络诊断）：每个十秒场景均保留 100,000 条逐位一致运动记录、500 个可靠检查点及 100 次完整发布，
无 reduced-cadence；声音背景组合中的 1,100 个事件各消费一次，保留新声音类型及枷锁物品身份。

**Remaining acceptance**：本环境没有真实 Kenshi 安装、Vulkan 设备及已配置的原生传输集成产物；相关跳过不视为通过。
真实环境需要验收同／不同派系多客户端的背包暂停、Drop from loot、枷锁、制作站改名与撤权，城墙接点／续线／撤销，
流式导航、战斗与逮捕、Extra Extensions 变量及角色标签的保存恢复，以及断线重入和迟到命令。
逐玩家链路记录应覆盖首次同步、稳态、峰值、丢包重传及重连，并计入加密、ACK 和 UDP/IP 开销；96 B/消息仅为预算模型。
