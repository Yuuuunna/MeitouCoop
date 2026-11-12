# 上游同步基准

**Verified**（2026-11-12，Git 远端快照核对）：上游为
[VlcekM/MeitouClient](https://github.com/VlcekM/MeitouClient)，基准为
`e43b9ae2bf9f3b694e73e211830571c089bde4d7`。

复核纳入关系：`git merge-base --is-ancestor e43b9ae2bf9f3b694e73e211830571c089bde4d7 HEAD`。

**Observed**（当前实现）：共享内容与独立模组程序集见[架构](engine.md)；玩家身份、权限、
资源和检查点见[联机契约](multiplayer.md)。客户端与服务器须匹配[构建身份](networking.md)；
协议 21 的编码与验收门禁见[传输契约](networking-next.md)。

## 验证范围

**Verified**（Windows x64，SDK 11.0.100；构建日志及 xUnit XML）：

| 检查 | 结果 |
|---|---|
| Solution 默认构建 | 0 警告，0 错误 |
| Metal 条件编译 | 0 警告，0 错误 |
| .NET 10 兼容目标及 Metal 条件编译 | 0 警告，0 错误 |
| 最终快速复核 | 1265 总计，1263 通过，0 失败，2 平台跳过 |
| 完整套件 | 1463 总计，1435 通过，0 失败，0 错误，28 跳过，0 未运行 |

完整套件使用 `-parallel none -maxThreads 1`，不筛选 Slow/Bench；QUIC 用例启用由当前
`transport.cpp` 和仓库锁定的 MsQuic 2.6.3 本机构建、校验哈希的原生库。
跳过项：22 项 GPU 检查、2 项非 Windows 平台检查、4 项缺少存档或联机验收 fixture。
证据在仓库外 `upstream-sync-20261112-results/` 的 `full-final.xml`、`quick-final.trx` 及同名前缀的构建、测试日志。
验证范围是本次同步的代码；主工作树原有未提交修改不属于该合并提交。

**Unknown**：跳过用例覆盖的行为、完整图形联机及 VPS 验收，以及 Linux、macOS/Metal
与 .NET 10 兼容包的实机运行；Windows 条件编译不代表这些平台已验收。
