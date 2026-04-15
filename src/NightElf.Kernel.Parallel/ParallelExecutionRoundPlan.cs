namespace NightElf.Kernel.Parallel;

public sealed class ParallelExecutionRoundPlan
{
    public ParallelExecutionRoundPlan(
        int roundNumber,
        TimeSpan timeout,
        IReadOnlyList<ParallelExecutionGroupPlan> groups,
        IReadOnlyList<ParallelTransaction> committedTransactions,
        IReadOnlyList<ConflictComponent> conflictComponents,
        double conflictRatio)
    {
        RoundNumber = roundNumber;
        Timeout = timeout;
        Groups = groups;
        CommittedTransactions = committedTransactions;
        ConflictComponents = conflictComponents;
        ConflictRatio = conflictRatio;
    }

    public int RoundNumber { get; }

    public TimeSpan Timeout { get; }

    public IReadOnlyList<ParallelExecutionGroupPlan> Groups { get; }

    public IReadOnlyList<ParallelTransaction> CommittedTransactions { get; }

    public IReadOnlyList<ConflictComponent> ConflictComponents { get; }

    public double ConflictRatio { get; }
}
