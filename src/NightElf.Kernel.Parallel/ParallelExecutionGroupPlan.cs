namespace NightElf.Kernel.Parallel;

public sealed class ParallelExecutionGroupPlan
{
    public ParallelExecutionGroupPlan(
        string groupId,
        int roundNumber,
        ParallelExecutionGroupKind kind,
        TimeSpan timeout,
        IReadOnlyList<ParallelTransaction> transactions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(transactions);

        GroupId = groupId;
        RoundNumber = roundNumber;
        Kind = kind;
        Timeout = timeout;
        Transactions = transactions;
    }

    public string GroupId { get; }

    public int RoundNumber { get; }

    public ParallelExecutionGroupKind Kind { get; }

    public TimeSpan Timeout { get; }

    public IReadOnlyList<ParallelTransaction> Transactions { get; }
}
