# Tsavorite LIB Checkpoints

issue 14 把状态合并从“LIB 推进时逐 key merge”改成了“平时直接写 versioned state，LIB 推进时做 checkpoint”。

## 设计

- `TsavoriteStateCheckpointStore<TContext>` 直接把状态写到最终 key，下层值是 `VersionedStateRecord`。
- 写入和删除都保留块高度 / 块哈希；删除不再走 `DeleteAllAsync`，而是写 tombstone。
- `ChainStateStore` 把 `BlockReference` 映射成 `StateCommitVersion`，提供 `ApplyChangesAsync(...)`、`AdvanceLibCheckpointAsync(...)`、`RecoverToLatestLibCheckpointAsync(...)`。

## Checkpoint 策略

- 第一个 LIB checkpoint 使用 full checkpoint，为 Tsavorite 建立 index + log 基线。
- 后续 LIB checkpoint 优先走 `TakeHybridLogCheckpointAsync(..., tryIncremental: true)`，让增量 snapshot 替代旧的逐 key merge。
- checkpoint 元数据写在 `CheckpointPath/nightelf.state-checkpoints.json`，命名格式是 `lib-<height>-<hash>`.

## 保留与恢复

- 默认只保留 1 个 LIB checkpoint，这和 `RemoveOutdatedCheckpoints=true` 的 Tsavorite 默认行为保持一致。
- 如果需要保留多个 checkpoint，必须先关闭 `RemoveOutdatedCheckpoints`，否则 catalog 会比底层物理 checkpoint 多。
- 回滚时直接按 checkpoint token 调 `RecoverAsync(...)`，恢复完成后 catalog 会裁掉该 checkpoint 之后的分叉元数据。

## 当前边界

- 现在的恢复语义已经围绕 checkpoint，而不是旧的 `BlockStateSet -> SetAllAsync/RemoveAllAsync` 状态机。
- `StoreVersion` 只作为 Tsavorite 内部恢复辅助值，catalog 的“最新 checkpoint”选择按 LIB 高度和创建时间排序，不依赖 Tsavorite 版本单调递增。
