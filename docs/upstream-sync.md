# 上游同步基准

**Verified**（2026-11-11，Git 远端快照核对）：上游为
[VlcekM/MeitouClient](https://github.com/VlcekM/MeitouClient)，`upstream/master` 固定于
`9a79fb6657c480a15d8e7b34b912703dafa1a1d6`。

复核纳入关系：`git merge-base --is-ancestor 9a79fb6657c480a15d8e7b34b912703dafa1a1d6 HEAD`。

**Observed**（当前实现）：共享 CPU 内容、运行时与独立模组程序集见[架构](engine.md)；
玩家身份、权限、派系资源和检查点见[联机契约](multiplayer.md)。客户端与服务器必须匹配
[构建身份及协议](networking.md)；协议 21 的编码和门禁见[传输契约](networking-next.md)。

## 验证范围

**Verified**（Windows x64，SDK 11.0.100；构建日志及 xUnit XML）：

| 检查 | 结果 |
|---|---|
| Solution 默认构建 | 0 警告，0 错误 |
| 快速套件（前置检查） | 1264 总计，1262 通过，0 失败，2 平台跳过 |
| 最终完整套件 | 1462 总计，1434 通过，0 错误，0 失败，28 跳过，0 未运行 |

最终完整套件使用 xUnit runner `-parallel none -maxThreads 1`，不筛选 Slow/Bench，覆盖快速检查后的最终修改。
跳过项：22 项 GPU 检查、2 项非 Windows 平台检查、4 项缺少存档/联机验收 fixture。
证据位于仓库外的 `upstream-test-results/full-final.xml` 与 `upstream-full-final-test.log`。

**Observed**（硬件探测）：RTX 3080 Ti 可用，但缺少 Khronos validation 层；GPU 检查跳过不代表验证通过。
**Unknown**：跳过用例覆盖的行为、完整图形联机实机验收，以及 Linux、macOS/Metal 和其他 GPU 的运行结果。
