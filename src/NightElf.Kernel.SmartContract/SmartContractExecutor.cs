using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.SmartContract;

public sealed class SmartContractExecutor
{
    public byte[] Execute(CSharpSmartContract contract, ContractInvocation invocation)
    {
        return Execute(contract, invocation, executionContext: null);
    }

    public byte[] Execute(
        CSharpSmartContract contract,
        ContractInvocation invocation,
        ContractExecutionContext? executionContext)
    {
        ArgumentNullException.ThrowIfNull(contract);

        return executionContext is null
            ? contract.Dispatch(invocation)
            : contract.Dispatch(executionContext, invocation);
    }
}
