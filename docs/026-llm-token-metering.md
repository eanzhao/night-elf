# LLM Token 计量

## 目标

issue [#32](https://github.com/eanzhao/night-elf/issues/32) 把 `AgentSession.RecordStep` 从“只记 token 数”扩成“同时记来源和置信度”，为后续算力结算留出可信度分层。

当前链上区分两类来源：

- `Verified`: 本地模型推理，由节点侧拦截器直接调用 tokenizer 计数
- `SelfReported`: 远程 API 返回的 `usage`，当前默认按 OpenAI-compatible payload 提取

## 合约与状态

proto 和合约变更在：

- [agent_session.proto](/Users/eanzhao/night-elf/protobuf/nightelf/contracts/agent_session.proto)
- [MeteringSourceExtensions.cs](/Users/eanzhao/night-elf/contract/NightElf.Contracts.System.AgentSession/MeteringSourceExtensions.cs)
- [AgentSessionContract.cs](/Users/eanzhao/night-elf/contract/NightElf.Contracts.System.AgentSession/AgentSessionContract.cs)

`RecordStepInput` 现在必须带 `metering_source`。链上状态和事件同时保留：

- raw token 累计
- `verified_*` / `self_reported_*` 分来源累计
- `weighted_tokens_consumed`

当前权重约定：

- `Verified`: `10000` bps，也就是 `1.0x`
- `SelfReported`: `5000` bps，也就是 `0.5x`

这层权重先在合约内生效，保证 settlement 读 session state 或事件时不需要自己重复实现一套规则。

## WebApp 侧计量服务

实现文件：

- [LlmTokenMetering.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/LlmTokenMetering.cs)

当前提供三块能力：

- `WhitespaceTextTokenizer`: 最小 tokenizer，按空白分词
- `LocalModelInferenceInterceptor`: 产出 `MeteringSource.Verified`
- `OpenAiUsageExtractor`: 解析 `usage.prompt_tokens / completion_tokens`，并兼容 `input_tokens / output_tokens`

这些服务通过 [NightElfWebAppExtensions.cs](/Users/eanzhao/night-elf/src/NightElf.WebApp/NightElfWebAppExtensions.cs) 注册，agent 或上层 host 可以直接从 DI 获取，再把结果填进 `RecordStepInput`。

## 链事件

`ChainSettlement` 新增了：

- `CHAIN_EVENT_TYPE_TOKEN_METERED`

发布逻辑在 [NodeRuntimeHostedService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/NodeRuntimeHostedService.cs)。节点在 `RecordStep` 成功上链后，会从本块 execution writes 里取出 `session:<id>:event:step:<hash>` 对应的 `StepRecorded` payload，再转发成 `TokenMetered` 事件。

这样外部系统不需要自己轮询 event state key，就能直接从 `SubscribeEvents` 里区分：

- `Verified`
- `SelfReported`

以及读取：

- `confidence_weight_basis_points`
- `weighted_tokens`
- `weighted_tokens_consumed`

## 测试

主要覆盖：

- [AgentSessionContractTests.cs](/Users/eanzhao/night-elf/test/NightElf.Contracts.System.AgentSession.Tests/AgentSessionContractTests.cs)
- [LlmTokenMeteringTests.cs](/Users/eanzhao/night-elf/test/NightElf.WebApp.Tests/LlmTokenMeteringTests.cs)
- [Phase1EndToEndTests.cs](/Users/eanzhao/night-elf/test/NightElf.WebApp.Tests/Phase1EndToEndTests.cs)

当前验证了：

- `Verified` / `SelfReported` 两类来源的 raw + weighted 累计
- 未指定 `MeteringSource` 时拒绝写入
- 本地 tokenizer 计数和远程 `usage` 提取
- `SubscribeEvents` 能回放 `TokenMetered` 事件，并在 payload 里保留来源和权重
- 重启恢复后 `WeightedTokensConsumed` 仍保持一致
