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

    Task RecordMinedAsync(
        IReadOnlyList<Transaction> transactions,
        BlockReference block,
        CancellationToken cancellationToken = default);
}
