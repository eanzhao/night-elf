# 最小 gRPC API

## 目标

issue [#27](https://github.com/eanzhao/night-elf/issues/27) 要求把 `NightElf.WebApp` 从空占位目录补成最小可用的节点 gRPC API，供外部客户端和 AI agent 直接调用。

## 本次落地

新增 proto：
- [protobuf/nightelf/webapi.proto](/Users/eanzhao/night-elf/protobuf/nightelf/webapi.proto)

新增项目：
- [src/NightElf.WebApp/NightElf.WebApp.csproj](/Users/eanzhao/night-elf/src/NightElf.WebApp/NightElf.WebApp.csproj)

核心服务：
- [NightElfNodeService.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/NightElfNodeService.cs)
- [NightElfWebAppExtensions.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/NightElfWebAppExtensions.cs)

状态查询依赖：
- [ChainStateTransactionResultStore.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/ChainStateTransactionResultStore.cs)
- [ITransactionResultStore.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/ITransactionResultStore.cs)

## API 行为

### `SubmitTransaction`

- 先做交易基本字段校验
- 再做 Ed25519 签名校验
- 通过后调用 `ITransactionPool.SubmitAsync(...)`
- 入池成功后，把交易结果写成 `Pending`
- 如果是重复提交，则优先返回已有状态
- 无效签名、无效引用块、池已满等情况统一返回 `Rejected`

### `GetTransactionResult`

- 从 `ChainStateDbContext` 读取交易结果记录
- 当前最小状态只有：
  - `Pending`
  - `Mined`
  - `Rejected`
  - `NotFound`

### `GetBlockByHeight`

- 直接通过 [BlockRepository](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/BlockRepository.cs) 查询 block store

### `GetChainStatus`

- 通过 [ChainStateStore](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/ChainStateStore.cs) 返回当前 `best chain`
- 当前只返回：
  - `best_chain_height`
  - `best_chain_hash`

## Launcher 集成

[Program.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/Program.cs) 现在会：
- `AddNightElfWebApp()`
- `MapNightElfWebApp()`

同时 [NodeRuntimeHostedService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/NodeRuntimeHostedService.cs) 在区块成功入链后，会把本块内交易批量标记为 `Mined`。

## 当前边界

这次没有扩到完整执行引擎，因此 `TransactionResult` 还不是 AElf 全量语义：
- 没有 gas / fee 字段
- 没有 logs / bloom
- 没有世界状态 diff
- 没有失败回执的细粒度执行信息

它现在只是 Phase 1 所需的最小查询面：
- 交易是否成功入池
- 是否已经被某个区块接纳
- 能否按高度读取 block
- 能否查询当前链头
