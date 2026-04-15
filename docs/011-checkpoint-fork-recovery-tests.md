# Checkpoint Fork Recovery Tests

issue 15 把 checkpoint 回滚验证从单一 happy path 扩成了可复用的 fork recovery 夹具和失败注入清单。

## 自动化场景

- 单分叉回滚：当 `11a / 11b` 两条分叉都还没推进 LIB 时，恢复必须回到最近的 LIB checkpoint，而不是停留在任意分叉头。
- LIB 切换前后：先验证 “最新 LIB = 10” 时回滚到 `10`，再推进 `12a` 为新的 LIB，确认后续回滚基准切到 `12a`。
- 多分叉重放：在 `10 -> 12a` 建立 LIB 后，先执行 `13a`，再回滚到最新 LIB `12a` 并重放 `13b -> 14b`，确认新的分叉可以继续推进并形成新的 LIB checkpoint。
- 显式旧 checkpoint 诊断：在更高 fork checkpoint 已存在时，强制恢复到较旧的 `12a` token 如果不成立，异常必须带 checkpoint name/height 和 mismatch 信息，而不是静默留下错误状态。
- 失败注入：checkpoint catalog 损坏、checkpoint token 损坏两类错误都有自动化测试，异常信息会带 metadata path 或 checkpoint name/height。

## 夹具约定

`ChainStateRecoveryHarness` 用同一个 `ChainStateDbContext` 模拟三类恢复一致性：

- `chain:best`：链头指针
- `state:root` / `state:balance:alice`：状态与状态根
- `index:tx:*`：索引键，包括删除后应恢复的 tombstone 场景

当前仓库还没有独立的索引 store / 状态根模块，所以测试用这些 key 模拟“链状态 + 状态根 + 索引”同时回滚的一致性要求。

## 手动失败注入建议

- 恢复中断：对 `RecoverToCheckpointAsync(...)` 传入会超时或被取消的 `CancellationToken`，验证恢复不会把 catalog 留在半裁剪状态。
- 部分 checkpoint 损坏：删除或重命名某个 Tsavorite checkpoint token 对应的物理目录，确认异常能定位到具体 checkpoint。
- sidecar 描述符漂移：手工修改 `nightelf.state-checkpoints.json` 中的 token / `StoreVersion`，确认恢复失败时能看出是 metadata 和物理 checkpoint 不匹配。
- catalog 损坏：写入非法 JSON 或截断 metadata 文件，确认读取阶段直接失败，而不是静默回退成空 checkpoint 集合。
