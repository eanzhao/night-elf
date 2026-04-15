namespace NightElf.Kernel.Parallel;

public sealed class ParallelExecutionPlan
{
    public ParallelExecutionPlan(
        IReadOnlyList<ParallelExecutionRoundPlan> rounds,
        IReadOnlyList<ParallelTransaction> committedTransactions,
        IReadOnlyList<ConflictComponent> remainingConflictComponents,
        bool retryExhausted)
    {
        Rounds = rounds;
        CommittedTransactions = committedTransactions;
        RemainingConflictComponents = remainingConflictComponents;
        RetryExhausted = retryExhausted;
    }

    public IReadOnlyList<ParallelExecutionRoundPlan> Rounds { get; }

    public IReadOnlyList<ParallelTransaction> CommittedTransactions { get; }

    public IReadOnlyList<ConflictComponent> RemainingConflictComponents { get; }

    public bool RetryExhausted { get; }
}
