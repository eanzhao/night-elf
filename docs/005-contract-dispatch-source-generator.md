# Phase 3 Contract Dispatch Source Generator

Issue: `#9`

## Goal

Replace runtime reflection-based contract method lookup with compile-time dispatch generation so the contract execution path can stay deterministic and parallel-safe.

## What Landed

### `NightElf.Sdk.CSharp`

The SDK now exposes the minimum runtime contract surface needed by generated dispatch code:

- `CSharpSmartContract` as the shared base type
- `ContractMethodAttribute` as the compile-time marker
- `IContractCodec<T>` for strongly-typed input/output serialization
- `ContractInvocation` for execution requests
- `ContractMethodNotFoundException` and `ContractInputDecodeException` for failure paths

The dispatch API is synchronous and reflection-free:

```csharp
public abstract class CSharpSmartContract
{
    public byte[] Dispatch(string methodName, byte[]? input)
    {
        return DispatchCore(methodName, input is null ? ReadOnlyMemory<byte>.Empty : input);
    }

    protected abstract byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input);
}
```

### `NightElf.Sdk.SourceGen`

`ContractDispatcherGenerator` is an incremental generator that scans `[ContractMethod]` members and emits a `DispatchCore` override into the target partial contract.

Generated dispatch shape:

```csharp
protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
{
    return methodName switch
    {
        "Transfer" => ContractCodec.Encode(Transfer(ContractCodec.Decode<TransferInput>("Transfer", input.Span))),
        _ => throw new ContractMethodNotFoundException(typeof(TokenContract), methodName)
    };
}
```

This removes runtime `GetMethod` / `Invoke` lookup from the execution hot path.

### `NightElf.Kernel.SmartContract`

The kernel side now consumes the generated contract entrypoint through `SmartContractExecutor`, which accepts a `ContractInvocation` and delegates straight to `CSharpSmartContract.Dispatch(...)`.

## Generator Rules

Contracts must satisfy these rules for generation:

- the contract type must derive from `CSharpSmartContract`
- the contract type must be `partial`
- `[ContractMethod]` members must be public instance methods
- methods may take zero or one parameter
- parameter and return types must implement `IContractCodec<TSelf>`
- dispatch names must be unique per contract

Violations produce `NE1001`-`NE1005` diagnostics at compile time.

## Validation

`test/NightElf.Sdk.SourceGen.Tests` compiles sample contracts with Roslyn and validates:

- generated code dispatches the expected method through `SmartContractExecutor`
- unknown method names throw `ContractMethodNotFoundException`
- input decode failures are wrapped as `ContractInputDecodeException`
- non-partial contracts surface `NE1001`

Repository validation command:

```bash
dotnet test NightElf.slnx
```
