# VRF Module Boundary

Issue: #17

## Goal

将 VRF 随机数能力从 `NightElf.Kernel.Consensus` 中抽离出来，避免 AEDPoS 继续直接承载“随机数生成 / 校验 / 配置”细节。共识层只消费一个稳定接口，后续可以独立替换为真实 VRF、BLS 或硬件加速实现，而不必重写共识调度逻辑。

## Module Split

- `NightElf.Vrf`
  - 暴露 `IVrfProvider`
  - 定义输入输出模型 `VrfInput`、`VrfEvaluation`、`VrfVerificationContext`
  - 承载 provider 选择和注册入口 `AddVrfProvider(...)`
- `NightElf.Kernel.Consensus`
  - 在出块时构造领域化输入
  - 依赖 `IVrfProvider.EvaluateAsync(...)` 生成 `VrfProof` 和 `Randomness`
  - 在验块时依赖 `IVrfProvider.VerifyAsync(...)` 校验提议中的 proof/randomness

当前首个实现是 `DeterministicVrfProvider`。它不是生产级密码学 VRF，只是一个最小可验证 provider，用来稳定模块边界、测试注入链路和 AEDPoS 对随机数的消费方式。

## IO Contract

`VrfInput`

- `PublicKey`: 参与随机数计算的公钥或验证者标识
- `Domain`: 调用方负责提供的领域隔离字符串，避免不同场景复用同一 seed
- `Seed`: 原始随机输入

`VrfEvaluation`

- `Proof`: 可随区块传播、供验块方校验的证明
- `Randomness`: 从 proof 派生出的最终随机值，供共识/调度逻辑消费

`VrfVerificationContext`

- `Input`
- `Proof`
- `Randomness`

## AEDPoS Integration

AEDPoS 在提议区块时使用以下 domain 约定：

```text
aedpos:{height}:{term}:{round}
```

共识提议中新增三段数据：

- `RandomSeed`
- `VrfProof`
- `Randomness`

这三段数据会随 `ConsensusBlockProposal` 一起流转。验块时，AEDPoS 不再自己推导随机数细节，而是把这些字段回填成 `VrfVerificationContext` 后交给 `IVrfProvider`。

## Configuration

配置节：

```text
NightElf:Vrf
```

当前支持：

- `Provider`: `Deterministic`
- `DomainPrefix`: provider 内部 hash 计算使用的前缀，默认 `nightelf.vrf`

示例：

```json
{
  "NightElf": {
    "Vrf": {
      "Provider": "Deterministic",
      "DomainPrefix": "nightelf.integration"
    }
  }
}
```

## Responsibilities

- 共识层负责：
  - 什么时候生成随机数
  - 如何构造 domain
  - 如何把 proof/randomness 放进区块提议
- VRF 模块负责：
  - 给定输入后的 proof/randomness 生成
  - proof/randomness 一致性校验
  - provider 配置和替换

这保证了后续如果接入真实 VRF 实现，只需要在 `NightElf.Vrf` 内新增 provider，并通过 DI 切换，不需要把 AEDPoS 逻辑重新耦合回随机数细节。
