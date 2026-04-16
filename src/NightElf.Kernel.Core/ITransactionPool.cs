using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core;

public enum TransactionPoolSubmitStatus
{
    Accepted,
    Duplicate,
    PoolFull,
    NoBestChain,
    InvalidTransaction,
    InvalidSignature,
    InvalidRefBlock,
    RefBlockExpired
}

public sealed class TransactionPoolSubmitResult
{
    public required TransactionPoolSubmitStatus Status { get; init; }

    public string? TransactionId { get; init; }

    public string? Error { get; init; }

    public bool IsAccepted => Status == TransactionPoolSubmitStatus.Accepted;
}

public sealed class TransactionPoolSnapshot
{
    public required int Capacity { get; init; }

    public required int QueuedCount { get; init; }

    public required long AcceptedCount { get; init; }

    public required long RejectedCount { get; init; }

    public required long DequeuedCount { get; init; }

    public required long DroppedExpiredCount { get; init; }
}

public interface ITransactionPool
{
    Task<TransactionPoolSubmitResult> SubmitAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Transaction>> TakeBatchAsync(
        int maxCount,
        CancellationToken cancellationToken = default);

    TransactionPoolSnapshot GetSnapshot();
}
