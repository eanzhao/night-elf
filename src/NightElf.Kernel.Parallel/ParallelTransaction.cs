using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.Parallel;

public sealed class ParallelTransaction
{
    public ParallelTransaction(string transactionId, ContractResourceSet resources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentNullException.ThrowIfNull(resources);

        TransactionId = transactionId;
        Resources = resources;
    }

    public string TransactionId { get; }

    public ContractResourceSet Resources { get; }
}
