using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.SmartContract;

public interface IContractCallPermissionGrantChecker
{
    bool IsAllowed(
        string treatyId,
        string agentAddress,
        ContractCallTargetInfo target,
        ContractInvocation invocation);
}
