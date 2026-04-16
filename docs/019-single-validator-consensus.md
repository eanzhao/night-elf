# SingleValidator 共识

issue [#25](https://github.com/eanzhao/night-elf/issues/25) 给 `IConsensusEngine` 补了一个 Phase 1 可用的最小实现：`SingleValidatorConsensusEngine`。

目标不是替换掉 AEDPoS，而是把“单节点可启动、可连续出块、可通过配置切换”这条最短路径先打通。`AedposConsensusEngine` 仍然保留，用于 Phase 2 的多验证人和 VRF 路径。

## 行为

- 固定单验证人地址，不做轮换
- 固定出块间隔，由 `NightElf:Consensus:SingleValidator:BlockInterval` 控制
- 不生成 `Randomness`、`VrfProof`
- 每个已产出的块都视为当前 LIB
- `RoundNumber = Height`，`TermNumber = 1`

`ConsensusData` 的格式也刻意保持简单：

```text
single-validator|proposer=<validator>|interval=<hh:mm:ss>
```

## 配置

当前 launcher 默认配置已经切到：

```json
{
  "NightElf": {
    "Consensus": {
      "Engine": "SingleValidator",
      "SingleValidator": {
        "ValidatorAddress": "node-local",
        "BlockInterval": "00:00:01"
      }
    }
  }
}
```

如果要切回 AEDPoS，只需要把 `Engine` 改成 `Aedpos`，并提供 `Aedpos` 段配置；`SingleValidator` 段可以保留不删。

## Launcher 集成

launcher 不再直接读取 `Aedpos.BlockInterval`、`BlocksPerRound` 和 `IrreversibleBlockDistance`。现在统一通过 `ConsensusEngineOptions` 的 helper 取：

- validator set
- block interval
- round / term
- 当前轮应查询的 LIB 高度 hint

这样 `NodeRuntimeHostedService` 的出块循环可以在 `SingleValidator` 和 `Aedpos` 之间直接切换，不需要写两套 pipeline 逻辑。

## DI 语义

- `Aedpos` 仍然依赖 `IVrfProvider`
- `SingleValidator` 不依赖 `IVrfProvider`
- 如果配置成 `Aedpos` 但没有注册 `IVrfProvider`，启动时会抛出清晰错误

launcher 也同步做了条件注册：只有在 `Aedpos` 模式下才会把 VRF provider 放进容器。
