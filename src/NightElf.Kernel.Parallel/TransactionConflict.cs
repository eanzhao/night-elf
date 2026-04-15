namespace NightElf.Kernel.Parallel;

public readonly record struct TransactionConflict(
    string LeftTransactionId,
    string RightTransactionId,
    TransactionConflictKind Kind);
