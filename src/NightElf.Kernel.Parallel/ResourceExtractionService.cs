using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.Parallel;

public sealed class ResourceExtractionService
{
    public ContractResourceExtractionResult Extract(CSharpSmartContract contract, ContractInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(contract);

        if (!contract.SupportsResourceExtraction)
        {
            return new ContractResourceExtractionResult(ContractResourceSet.Empty, UsedFallback: true);
        }

        return new ContractResourceExtractionResult(contract.DescribeResources(invocation), UsedFallback: false);
    }
}
