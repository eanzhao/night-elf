# ChainSettlement gRPC 接口

## 目标

issue [#31](https://github.com/eanzhao/night-elf/issues/31) 为 AI agent 和 NightElf 节点之间定义一层松耦合的 settlement 接口，不要求依赖 Aevatar、Orleans 或 NightElf 内部宿主实现。

这次落的是一个最小可运行版本：

- `SubmitTransaction`
- `QueryState`
- `DeployContract`
- `SubscribeEvents`

## Proto

接口定义在：

- [chain_settlement.proto](/Users/eanzhao/night-elf/protobuf/nightelf/chain_settlement.proto)

当前 `ChainSettlement` 复用了已有的 `aelf.Transaction` 和 `nightelf.webapp.TransactionResult`，避免同一条链上交易模型在 agent-facing API 再拷一份。

## 服务端实现

主要代码：

- [ChainSettlementService.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/ChainSettlementService.cs)
- [TransactionSubmissionService.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/TransactionSubmissionService.cs)
- [ContractDeploymentService.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/ContractDeploymentService.cs)
- [ChainSettlementEventBroker.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/ChainSettlementEventBroker.cs)
- [ChainSettlementModels.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/ChainSettlementModels.cs)

其中：

- `SubmitTransaction` 走与 `NightElfNode` 相同的交易池和结果存储路径
- `QueryState` 直接查询链状态 key，并自动区分 raw bytes 与 `VersionedStateRecord`
- `DeployContract` 做三件事：
  - 校验 deployer Ed25519 签名
  - 在 `ContractSandbox` 中验证 assembly 可加载且包含唯一的 `CSharpSmartContract`
  - 把 assembly bytes、metadata、code-hash 索引和 deploy transaction 映射写入链状态
- `SubscribeEvents` 通过 `ChainSettlementEventBroker` 从节点事件总线读取 `BlockAccepted / TransactionResult / ContractDeployed` 三类事件

## 部署状态模型

当前部署状态约定如下：

- `contract:<address>`: marker
- `contract:<address>:assembly`: 原始 assembly bytes
- `contract:<address>:metadata`: JSON metadata
- `contract:codehash:<codeHash>`: `addressHex`
- `contract:tx:<transactionId>`: `addressHex`

metadata 结构见 [ChainSettlementContractDeploymentRecord](/Users/eanzhao/night-elf/src/NightElf.WebApp/ChainSettlementModels.cs)。

`transaction_id` 不是 Phase 2 新增的链上交易类型，而是基于 `deployer + codeHash + signature` 的确定性 deployment id。它会同步写入现有 `ITransactionResultStore`，因此部署结果也能通过现有交易结果查询链路回读。

## 事件流

`SubscribeEvents` 当前支持三类事件：

- `CHAIN_EVENT_TYPE_BLOCK_ACCEPTED`
- `CHAIN_EVENT_TYPE_TRANSACTION_RESULT`
- `CHAIN_EVENT_TYPE_CONTRACT_DEPLOYED`

为了避免 gRPC stream 建连时序导致首批事件丢失，`ChainSettlementEventBroker` 会保留最近一段内存缓冲，并在新订阅建立时先回放 snapshot，再继续转发 live events。

## 客户端 stub

C# stub 由 [NightElf.WebApp.csproj](/Users/eanzhao/night-elf/src/NightElf.WebApp/NightElf.WebApp.csproj) 构建时自动生成。

可选的 Python / Go stub 生成脚本在：

- [generate-chain-settlement-clients.sh](/Users/eanzhao/night-elf/eng/generate-chain-settlement-clients.sh)

脚本行为：

- 总是通过 `dotnet build src/NightElf.WebApp/NightElf.WebApp.csproj` 生成 C# stub
- 若本机存在 `python3 -m grpc_tools.protoc`，则生成 Python stub
- 若本机存在 `protoc + protoc-gen-go + protoc-gen-go-grpc`，则生成 Go stub
- 输出目录是 `artifacts/clients/chain-settlement/`

## 集成测试

覆盖场景在：

- [ChainSettlementServiceTests.cs](/Users/eanzhao/night-elf/test/NightElf.WebApp.Tests/ChainSettlementServiceTests.cs)

当前验证了：

- 通过 `ChainSettlement.SubmitTransaction` 提交 `OpenSession`
- 通过 `QueryState` 读取 session state
- 通过 `DeployContract` 部署真实的 `AgentSession` assembly，并验证 assembly bytes 与 metadata 已落链
- 通过 `SubscribeEvents` 回放并接收 `ContractDeployed` 与 `TransactionResult`

## 已知边界

- 当前 `DeployContract` 只做“可加载 + 可持久化”验证，还没有把动态部署合约接入 block execution path
- 部署事件 payload 目前是 JSON metadata，交易事件 payload 是 protobuf `TransactionResult`
- 这条接口已经与 Orleans / Aevatar 解耦，但还没有实现对应的 adapter；那部分仍留给后续 Phase 2 issue
