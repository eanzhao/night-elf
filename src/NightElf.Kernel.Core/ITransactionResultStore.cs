using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core;

public interface ITransactionResultStore
{
    Task<TransactionResultRecord?> GetAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    Task<TransactionResultRecord?> GetAsync(
        Hash transactionId,
        CancellationToken cancellationToken = default);

    Task RecordPendingAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default);

    Task RecordRejectedAsync(
        string transactionId,
        string? error,
        CancellationToken cancellationToken = default);

    Task RecordBlockResultAsync(
        Transaction transaction,
        BlockReference block,
        TransactionResultStatus status,
        string? error = null,
        CancellationToken cancellationToken = default);

    Task RecordBlockResultAsync(
        string transactionId,
        BlockReference block,
        TransactionResultStatus status,
        string? error = null,
        CancellationToken cancellationToken = default);

    Task RecordMinedAsync(
        IReadOnlyList<Transaction> transactions,
        BlockReference block,
        CancellationToken cancellationToken = default);
}
