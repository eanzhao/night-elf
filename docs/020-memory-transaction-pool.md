# 内存交易池

issue [#26](https://github.com/eanzhao/night-elf/issues/26) 把 Phase 1 交易收集路径补成了最小可运行实现。

这次的目标不是复制 AElf 完整交易池，而是先把下面这条闭环打通：

- 接收交易
- 验证签名
- 验证 `ref_block_number` + `ref_block_prefix`
- 按 FIFO 排队
- 出块时取一批交易并写进 `BlockBody.transaction_ids`

## 模块位置

实现落在 `NightElf.Kernel.Core`：

- [ITransactionPool.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/ITransactionPool.cs)
- [MemoryTransactionPool.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/MemoryTransactionPool.cs)
- [TransactionExtensions.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/TransactionExtensions.cs)
- [TransactionPoolOptions.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/TransactionPoolOptions.cs)

launcher 集成点：

- [LauncherServiceCollectionExtensions.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/LauncherServiceCollectionExtensions.cs)
- [NodeRuntimeHostedService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/NodeRuntimeHostedService.cs)
- [BlockModelFactory.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/BlockModelFactory.cs)

## Phase 1 语义

这次明确了一个 Phase 1 专用约定：

- `Transaction.from.value` 直接承载 `32` 字节 Ed25519 公钥
- `Transaction.signature` 是对“去掉签名字段后的交易 payload 的 SHA-256 哈希”做 Ed25519 签名

这不是 AElf 正式地址体系的完整复刻。NightElf 目前还没有引入 `Address.FromPublicKey(...)` / base58 / 地址派生逻辑，所以先用这个最小语义把交易验证做成真实代码，而不是 placeholder。

## ref block 校验

和 AElf 当前实现对齐的点：

- `ref_block_prefix` 取引用区块 hash 的前 `4` 字节
- 交易必须引用当前 best chain 上已存在的块
- `ref_block_number` 不能高于当前 best chain height
- 默认有效窗口是 `512` 个块，也就是 `64 * 8`

如果当前 best chain height 已经满足：

```text
ref_block_number + reference_block_valid_period <= best_chain_height
```

交易会被视为过期并直接丢弃。

## FIFO 与消费

当前实现是有界内存队列：

- `SubmitAsync(...)` 负责验证和入池
- `TakeBatchAsync(maxCount)` 按 FIFO 取交易
- 取批次时会再次检查 ref block，自动剔除已经过期或已失效的交易

Phase 1 还没有做：

- fee / priority 排序
- block 失败后的交易回补
- 持久化交易池
- 网络/API 层的交易提交入口

这些留给后续 issue 继续扩展。
