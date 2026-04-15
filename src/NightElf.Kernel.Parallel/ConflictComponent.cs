namespace NightElf.Kernel.Parallel;

public sealed class ConflictComponent
{
    public ConflictComponent(
        IReadOnlyList<ParallelTransaction> transactions,
        IReadOnlyList<TransactionConflict> conflicts)
    {
        Transactions = transactions;
        Conflicts = conflicts;
    }

    public IReadOnlyList<ParallelTransaction> Transactions { get; }

    public IReadOnlyList<TransactionConflict> Conflicts { get; }
}
