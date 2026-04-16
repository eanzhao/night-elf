# Consensus Engine Abstraction

issue 16 把 `src/NightElf.Kernel.Consensus` 从占位目录补成了真实模块，并把 AEDPoS 放到统一的 `IConsensusEngine` 契约后面。

## 抽象边界

`IConsensusEngine` 现在明确暴露五个入口：

- `ProposeBlockAsync(...)`：区块提议
- `ValidateBlockAsync(...)`：区块验证
- `OnBlockCommittedAsync(...)`：提交后状态推进
- `GetValidatorsAsync(...)`：验证人列表查询
- `ForkChoiceAsync(...)`：分叉选择

这些入口都只吃 `Consensus*` 上下文模型，调用方不需要依赖 `AedposConsensusEngine` 内部状态或类型。

## AEDPoS v1 规则

当前 `AedposConsensusEngine` 先落了一个可验证、可替换的最小实现：

- validator 集按 `round` 做轮转，默认 `["validator-a", "validator-b", "validator-c"]`
- proposer 默认取当前轮的第一个 validator，也支持显式指定 proposer 并校验其是否属于当前 validator set
- block hash 由 `height + parent + proposer + term + round + timestamp + random seed` 做确定性哈希
- fork choice 先比较 `LIB height`，再比较 `head height`、`term`、`round`

这还不是完整的 AElf 历史 AEDPoS 迁移版，但已经把“可插拔共识接口 + 第一个独立实现”的主线打通了。

## 配置入口

共识配置统一走 `NightElf:Consensus`：

```json
{
  "NightElf": {
    "Consensus": {
      "Engine": "Aedpos",
      "Aedpos": {
        "Validators": [ "miner-1", "miner-2", "miner-3" ],
        "BlockInterval": "00:00:04",
        "BlocksPerRound": 3,
        "IrreversibleBlockDistance": 8
      }
    }
  }
}
```

`ConsensusEngineServiceCollectionExtensions` 会按 `Engine` 注册当前实现；现在只有 `Aedpos`，但配置结构已经为后续 `HotStuff` 或其他实现预留好了位置。
