namespace NightElf.Kernel.Parallel;

public sealed class ConflictAnalysisResult
{
    public ConflictAnalysisResult(
        IReadOnlyList<ParallelTransaction> committedTransactions,
        IReadOnlyList<ConflictComponent> conflictComponents,
        int totalTransactionCount)
    {
        CommittedTransactions = committedTransactions;
        ConflictComponents = conflictComponents;
        TotalTransactionCount = totalTransactionCount;
        ConflictTransactionCount = conflictComponents.Sum(static component => component.Transactions.Count);
        ConflictRatio = totalTransactionCount == 0
            ? 0.0d
            : (double)ConflictTransactionCount / totalTransactionCount;
    }

    public IReadOnlyList<ParallelTransaction> CommittedTransactions { get; }

    public IReadOnlyList<ConflictComponent> ConflictComponents { get; }

    public int TotalTransactionCount { get; }

    public int ConflictTransactionCount { get; }

    public double ConflictRatio { get; }

    public bool HasConflicts => ConflictComponents.Count > 0;
}
