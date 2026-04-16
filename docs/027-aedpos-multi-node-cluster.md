# AEDPoS 多节点集群

## 目标

issue [#33](https://github.com/eanzhao/night-elf/issues/33) 把 Phase 2 从“单节点可跑的 AEDPoS 抽象”推进到“2-3 节点可验证的实际集群”。

这次落地的重点不是完整 p2p，而是先把最小闭环打通：

- 静态 peer 发现与 join
- AEDPoS 多节点轮转出块
- block sync / transaction relay
- 跨节点状态一致性验证

默认 `appsettings.json` 仍保持 `SingleValidator`，多节点 AEDPoS 继续通过配置显式开启。

## 核心改动

主要文件：

- [ConsensusClusterCoordinator.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/ConsensusClusterCoordinator.cs)
- [ClusterPeerRegistry.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/ClusterPeerRegistry.cs)
- [ConsensusClusterMessages.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/ConsensusClusterMessages.cs)
- [NetworkTransactionRelayService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/NetworkTransactionRelayService.cs)
- [LauncherOptions.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/LauncherOptions.cs)
- [NodeRuntimeHostedService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/NodeRuntimeHostedService.cs)
- [TransactionSubmissionService.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/TransactionSubmissionService.cs)

当前结构：

- `ClusterPeerRegistry` 统一维护静态 seed peers 和运行期已发现 peers
- `ConsensusClusterCoordinator` 负责 `hello`、`block-sync`、`transaction-broadcast` 三类消息
- `NetworkTransactionRelayService` 从 `TransactionSubmissionService` 解耦出来，避免 `submit -> relay -> coordinator -> submit` 循环依赖

## 握手与同步

握手消息当前是单向 `hello`，不再要求同步 `ack`。

原因很直接：双向同步 `hello/ack` 在双方同时 join 时，会形成：

- A 持有本地 gate 等 B 的 `ack`
- B 持有本地 gate 等 A 的 `ack`

这会把整个 join 阶段锁死。现在改成：

- 发起方成功把 `hello` 送达就视为 join 成功
- 接收方在收到 `hello` 时直接注册对端 peer

为了解决“启动早期错过空块广播后就停在旧高度”的问题，`hello` 处理里还补了一个最小 catch-up：

- 如果本地 best height 高于对端 `BestChainHeight`
- 就顺序回放本地已持久化的空块到落后节点

当前只对空块做补齐，因为 transaction body 还没有单独持久化索引，无法在后补阶段重建带交易 block 的完整 `BlockSyncMessage`。对当前 Phase 2 启动阶段已经够用，因为卡住的问题主要发生在交易提交前的早期空块。

## 交易广播

`SubmitTransaction` 现在分成两条路径：

- 本地提交：写入本地 tx pool 后向 peers relay
- 远端 relay：只做校验和入池，不再二次广播

这样避免了广播环，同时保留：

- Ed25519 签名校验
- core field 校验
- duplicate / rejected 状态写入 `TransactionResultStore`

## 配置

多节点模式新增：

- `NightElf:Launcher:Network:Peers`
- `NightElf:Launcher:Network:JoinRetryDelay`
- `NightElf:Launcher:Network:JoinMaxAttempts`

集群测试里使用的关键配置是：

- `NightElf:Consensus:Engine = Aedpos`
- `NightElf:Consensus:Aedpos:Validators = validator-a / validator-b / validator-c`
- `NightElf:Network:BlockSyncTransport = Quic`
- `NightElf:Network:TransactionBroadcastTransport = Quic`

测试里还显式清空了多余 validator 索引，避免在 `appsettings.json` 默认 3-validator 配置之上做 2-node override 时被 .NET configuration array merge 残留污染。

## 测试

新增测试：

- [AedposMultiNodeClusterTests.cs](/Users/eanzhao/night-elf/test/NightElf.WebApp.Tests/AedposMultiNodeClusterTests.cs)

覆盖场景：

- 3 节点 peer discovery
- 3 节点出块后 block hash / session state 一致性
- `random_seed` / `randomness` 已进入区块 extra data
- 2 节点 `OpenSession` 批量提交 baseline

为了避免 Tsavorite 在同一测试进程里并行拉起过多节点实例，`NightElf.WebApp.Tests` 现在通过 [AssemblyInfo.cs](/Users/eanzhao/night-elf/test/NightElf.WebApp.Tests/AssemblyInfo.cs) 显式关闭了 xUnit 并行执行。

## 当前结果

在 `2026-04-16` 本机环境上，基线测试：

- 2 节点 AEDPoS
- 12 笔 `OpenSession`
- 得到 `131.08 tx/s`

命令：

```bash
dotnet test test/NightElf.WebApp.Tests/NightElf.WebApp.Tests.csproj --filter AedposCluster_Should_Report_MultiNode_Baseline --logger "console;verbosity=detailed"
```

当前验证命令：

```bash
dotnet test test/NightElf.Launcher.Tests/NightElf.Launcher.Tests.csproj
dotnet test test/NightElf.WebApp.Tests/NightElf.WebApp.Tests.csproj
dotnet test NightElf.slnx
NightElf__Launcher__MaxProducedBlocks=1 dotnet run --project src/NightElf.Launcher/NightElf.Launcher.csproj
```
