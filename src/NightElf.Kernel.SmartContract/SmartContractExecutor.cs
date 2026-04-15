using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.SmartContract;

public sealed class SmartContractExecutor
{
    public byte[] Execute(CSharpSmartContract contract, ContractInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(contract);

        return contract.Dispatch(invocation);
    }
}
