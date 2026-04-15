# NightElf.Sdk.CSharp

Contract development SDK primitives for NightElf.

Current scope:

- `CSharpSmartContract` base type with generated dispatch entrypoint
- `ContractMethodAttribute` marker used by `NightElf.Sdk.SourceGen`
- `IContractCodec<T>` contract for compile-time-safe input/output encoding
- split execution contexts for state/call/crypto/identity capabilities
- `ContractExecutionContext` facade for transaction/block scoped contract access
- dispatch exceptions for missing methods and input decode failures
