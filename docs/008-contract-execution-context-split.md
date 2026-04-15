# Phase 3 Contract Execution Context Split

Issue: `#12`

## Goal

Stop growing a single host bridge object that carries every contract-facing capability. Instead, split execution responsibilities into small focused contexts and expose them through a narrow facade used only for one execution scope.

## What Landed

### Specialized contexts

`NightElf.Sdk.CSharp` now defines four focused context types:

- `ContractStateContext`
- `ContractCallContext`
- `ContractCryptoContext`
- `ContractIdentityContext`

Each context delegates to one capability-specific provider interface:

- `IContractStateProvider`
- `IContractCallHandler`
- `IContractCryptoProvider`
- `IContractIdentityProvider`

This keeps state, calls, crypto, and identity as separate seams that can be tested and implemented independently.

### Facade

`ContractExecutionContext` composes the four specialized contexts and carries execution metadata:

- `TransactionId`
- `SenderAddress`
- `CurrentContractAddress`
- `BlockHeight`
- `BlockHash`
- `Timestamp`

The contract base type no longer needs a giant bridge object. `CSharpSmartContract` receives a scoped `ContractExecutionContext` only for the duration of dispatch and then restores the previous state.

### Execution integration

`SmartContractExecutor.Execute(...)` now accepts an optional `ContractExecutionContext`.

When provided:

- the contract gets access to the execution facade during dispatch
- the facade is cleared after dispatch
- direct calls outside an execution scope still fail fast

## Validation

`test/NightElf.Sdk.CSharp.Tests` validates each focused context independently.

`test/NightElf.Kernel.SmartContract.Tests/SmartContractExecutorContextTests.cs` validates that:

- execution metadata and specialized contexts are available during dispatch
- the facade is not left attached to the contract after execution

Repository validation command:

```bash
dotnet test NightElf.slnx
```
