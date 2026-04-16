# AgentSession 系统合约

## 目标

issue [#29](https://github.com/eanzhao/night-elf/issues/29) 把 `AgentSession` 从 genesis 里的名字占位推进成第一个真实可执行的系统合约。

## 本次落地

合约项目：
- [AgentSessionContract.cs](/Users/eanzhao/night-elf/contract/NightElf.Contracts.System.AgentSession/AgentSessionContract.cs)
- [AgentSessionProtobufCodecs.cs](/Users/eanzhao/night-elf/contract/NightElf.Contracts.System.AgentSession/AgentSessionProtobufCodecs.cs)
- [agent_session.proto](/Users/eanzhao/night-elf/protobuf/nightelf/contracts/agent_session.proto)

测试：
- [AgentSessionContractTests.cs](/Users/eanzhao/night-elf/test/NightElf.Contracts.System.AgentSession.Tests/AgentSessionContractTests.cs)

genesis 接线：
- [SystemContractArtifactCatalog.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/SystemContractArtifactCatalog.cs)
- [GenesisBlockService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/GenesisBlockService.cs)

## 合约接口

当前合约提供 3 个入口：
- `OpenSession(OpenSessionInput) -> Hash`
- `RecordStep(RecordStepInput) -> Empty`
- `FinalizeSession(FinalizeSessionInput) -> Empty`

其中：
- `OpenSession` 只有 session owner 或系统 admin 可以调用
- `RecordStep` 和 `FinalizeSession` 只允许 owner 调用
- token budget 按 `input_tokens + output_tokens` 累加

## SessionId 规则

`sessionId` 按 issue 要求用以下输入做确定性哈希：
- `agentAddress`
- `ExecutionContext.BlockHeight`
- `ExecutionContext.TransactionIndex`

为了支持这条规则，`ContractExecutionContext` 新增了 `TransactionIndex`。

## Admin 规则

当前 Phase 1 还没有完整的链上权限配置模型，所以这里先采用确定性的系统 admin 地址：

- `IdentityContext.GetVirtualAddress(CurrentContractAddress, "agent-session-admin")`

这让合约能在不引入额外管理合约的前提下完成 owner/admin 双路径鉴权。

## 事件处理

当前运行时还没有正式的 contract event pipeline，所以这次没有虚构一个半成品事件总线。

取而代之的是：
- 事件消息用 protobuf 定义
- 合约执行时把事件 payload 写入 deterministic state key

具体 key：
- `session:<sessionId>:event:opened`
- `session:<sessionId>:event:step:<stepContentHash>`
- `session:<sessionId>:event:finalized`

这样测试和后续索引层都能直接验证 protobuf payload。

## Genesis 集成

launcher 现在内置了 `SystemContractArtifactCatalog`。

对于已知系统合约：
- `AgentSession` 的 `code hash` 改为取真实编译产物 DLL 的 `SHA256`

对于未知占位合约：
- 仍保留旧的 `SHA256(contractName)` fallback，避免现在的 `Treasury` 等占位名字被这次改动打断

## 验证

当前覆盖了：
- owner 打开 session
- admin 打开 session
- 非 owner/admin 打开被拒绝
- step 记账与累计 token 更新
- budget exceed 回滚
- non-owner 调用拒绝
- finalize 后拒绝继续写入
- genesis deployment record 使用真实 `AgentSession` 程序集 hash
