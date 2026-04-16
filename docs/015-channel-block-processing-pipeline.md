# Channel Block Processing Pipeline

Issue: #19

## Goal

把块处理主链路从“隐式事件总线触发下一步”改成显式的 channel 管道，让 `块接收 -> 状态写入 -> LIB checkpoint -> 同步通知` 的生产者/消费者边界可以直接追踪和调试。

## Audit Result

NightElf 当前代码库里已经没有在用的 `LocalEventBus` 实现，但文档里的迁移目标仍然成立：关键路径不应该再回到“谁订阅了事件、谁再去推进状态”的隐式模型。

这次通过两件事把边界落成代码：

- `ChannelBlockProcessingPipeline` 负责关键路径
- `LocalEventBusUsageTests` 守住“源码里不再回引 `LocalEventBus`”这条约束

## Critical Path

`ChannelBlockProcessingPipeline` 使用有界 `Channel<BlockProcessingRequest>`：

- producer: `EnqueueAsync(...)`
- consumer: 单 reader 后台循环 `ProcessLoopAsync()`

每个请求按固定顺序执行：

1. `SetBestChainAsync(...)`
2. `ApplyChangesAsync(...)`
3. `AdvanceLibCheckpointAsync(...)`，如果该块推进了 LIB
4. `IBlockSyncNotifier.NotifyBlockAcceptedAsync(...)`

这样块处理控制流不再依赖隐式订阅关系，断点和异常都会落在一条明确的调用链上。

## Backpressure Model

管道配置由 `BlockProcessingPipelineOptions` 控制：

- `Capacity`
- `FullMode`
- `SingleReader`
- `SingleWriter`
- `AllowSynchronousContinuations`

默认值：

- `Capacity = 128`
- `FullMode = Wait`
- `SingleReader = true`

这与文档里的目标一致：关键路径优先保序和可追踪性，队列满时直接对 producer 施加背压，而不是继续用隐式事件扇出把压力藏起来。

## Consumer Model

当前 consumer 模型是单 reader：

- 状态提交和 LIB checkpoint 仍按单条主链顺序推进
- 同步通知作为关键路径的最后一步，失败会直接 fault 当前 block ticket，并停止后续消费

之所以保留单 consumer，是因为 issue 14 的 checkpoint 语义和 best-chain 指针都要求顺序一致性。后续如果要做多 consumer，只能建立在更细的状态分区或更强的并发提交语义之上。

## Non-Critical Events

非关键路径事件总线并没有消失，而是被收敛成 `INonCriticalEventBus`：

- 用途仅限 telemetry / logging / metrics
- 当前默认实现是 `InMemoryNonCriticalEventBus`
- pipeline 对 event bus 采用 best-effort 发布，bus 自身失败不会中断块处理

这保证了日志和观测能力仍然存在，但不会再次反过来主导关键控制流。

## Verification

自动化覆盖了四类行为：

- 顺序处理：块状态写入、LIB checkpoint 和同步通知按顺序执行
- 背压：容量为 1 时，第三个 producer 会等待 consumer 释放空间
- 非关键事件隔离：event bus 抛错不会破坏关键路径
- 关键路径失败外显：sync notifier 失败会 fault pipeline，而不是被事件系统吞掉
