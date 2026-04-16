# Phase 1 端到端集成

## 目标

issue [#30](https://github.com/eanzhao/night-elf/issues/30) 要求把 Phase 1 已经落地的组件真正串成一条可运行链路：

- gRPC 提交交易
- 交易池验签和 ref block 校验
- 单验证人出块
- 系统合约执行
- 状态提交到 Tsavorite
- 交易结果持久化
- checkpoint 恢复后继续提供链状态和交易结果查询

## 本次落地

执行接线：
- [SystemContractExecutionService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/SystemContractExecutionService.cs)
- [NodeRuntimeHostedService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/NodeRuntimeHostedService.cs)
- [SystemContractArtifactCatalog.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/SystemContractArtifactCatalog.cs)

交易结果持久化：
- [ITransactionResultStore.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/ITransactionResultStore.cs)
- [ChainStateTransactionResultStore.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/ChainStateTransactionResultStore.cs)
- [NightElfNodeService.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/NightElfNodeService.cs)

AgentSession 资源声明补齐：
- [AgentSessionContract.cs](/Users/eanzhao/night-elf/contract/NightElf.Contracts.System.AgentSession/AgentSessionContract.cs)

端到端测试：
- [NightElfNodeTestHarness.cs](/Users/eanzhao/night-elf/test/NightElf.WebApp.Tests/NightElfNodeTestHarness.cs)
- [NightElfNodeServiceTests.cs](/Users/eanzhao/night-elf/test/NightElf.WebApp.Tests/NightElfNodeServiceTests.cs)
- [Phase1EndToEndTests.cs](/Users/eanzhao/night-elf/test/NightElf.WebApp.Tests/Phase1EndToEndTests.cs)

## 执行链路

当前 `SubmitTransaction -> GetTransactionResult` 的关键路径如下：

1. `NightElfNodeService.SubmitTransaction` 校验交易字段、Ed25519 签名和交易池约束。
2. `MemoryTransactionPool` 校验 `ref_block_number` / `ref_block_prefix`，拒绝过期引用块。
3. `NodeRuntimeHostedService` 在出块循环里取出 batch，创建 block proposal，并调用 `SystemContractExecutionService` 执行 block 内交易。
4. `SystemContractExecutionService` 根据 genesis deployment 记录把地址解析到本地系统合约实现，构造 `ContractExecutionContext`，预取资源声明对应的 state key，再通过 `SmartContractExecutor + ContractSandboxExecutionService` 执行。
5. 成功执行的 state write/delete 会并入本块的 `BlockProcessingRequest`，随 block 一起提交到 `ChannelBlockProcessingPipeline`。
6. 交易结果会在 block 被接纳后写入链状态，并且发生在 `AdvanceLibCheckpointAsync` 之前，因此 checkpoint 能恢复 `Mined` / `Failed` 结果，而不只是合约状态。

## 覆盖场景

当前测试覆盖了 issue 验收里的全部核心场景：

- 节点启动，自动生成 genesis，并部署 `AgentSession`
- 提交 `OpenSession`，区块包含交易，链上 session state 创建成功
- 提交 `RecordStep`，验证 token 计量写回 state
- 提交 `FinalizeSession`，验证 session 结算
- 提交超预算 `RecordStep`，验证合约 revert，状态不被污染
- 提交无效签名交易，验证 gRPC 直接返回 `Rejected`
- 提交过期 `ref_block` 交易，验证交易池拒绝
- Tsavorite checkpoint 后重启节点，恢复 session state 和对应 `Mined` 交易结果

## 单节点 TPS 基线

在 `2026-04-16` 本地环境上，测试 `OpenSession_Batch_Should_Report_SingleNode_Tps_Baseline` 的结果是：

- `16` 笔 `OpenSession`
- 总耗时 `0.086 s`
- 单节点端到端基线 `187.10 tx/s`

这组数字是当前 Phase 1 最小运行时的功能基线，不代表后续并行执行、真实网络拓扑或更复杂系统合约下的最终吞吐。

## 验证

本次实际执行并通过的命令：

- `dotnet test test/NightElf.Kernel.Core.Tests/NightElf.Kernel.Core.Tests.csproj`
- `dotnet test test/NightElf.Launcher.Tests/NightElf.Launcher.Tests.csproj`
- `dotnet test test/NightElf.WebApp.Tests/NightElf.WebApp.Tests.csproj`
- `dotnet test test/NightElf.WebApp.Tests/NightElf.WebApp.Tests.csproj --filter OpenSession_Batch_Should_Report_SingleNode_Tps_Baseline -l "console;verbosity=detailed"`
- `dotnet test NightElf.slnx`
- `NightElf__Launcher__MaxProducedBlocks=1 dotnet run --project src/NightElf.Launcher/NightElf.Launcher.csproj`
