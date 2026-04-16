using System.Text.Json;

using NightElf.Database;
using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core;

public sealed class ChainStateTransactionResultStore : ITransactionResultStore
{
    private const string TransactionResultPrefix = "tx:result:";
    private readonly IKeyValueDatabase<ChainStateDbContext> _database;

    public ChainStateTransactionResultStore(IKeyValueDatabase<ChainStateDbContext> database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<TransactionResultRecord?> GetAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        var bytes = await _database.GetAsync(CreateKey(transactionId), cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : JsonSerializer.Deserialize(
                bytes,
                TransactionResultStoreJsonSerializerContext.Default.TransactionResultRecord);
    }

    public Task<TransactionResultRecord?> GetAsync(
        Hash transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionId);

        return transactionId.Value.IsEmpty
            ? Task.FromResult<TransactionResultRecord?>(null)
            : GetAsync(transactionId.ToHex(), cancellationToken);
    }

    public async Task RecordPendingAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var transactionId = transaction.GetTransactionId();
        var existing = await GetAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (existing is { Status: TransactionResultStatus.Mined })
        {
            return;
        }

        var record = new TransactionResultRecord
        {
            TransactionId = transactionId,
            Status = TransactionResultStatus.Pending,
            Error = null,
            BlockHeight = 0,
            BlockHash = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _database.SetAsync(
                CreateKey(transactionId),
                Serialize(record),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task RecordMinedAsync(
        IReadOnlyList<Transaction> transactions,
        BlockReference block,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(block);

        if (transactions.Count == 0)
        {
            return Task.CompletedTask;
        }

        var writes = new Dictionary<string, byte[]>(transactions.Count, StringComparer.Ordinal);
        var updatedAtUtc = DateTimeOffset.UtcNow;

        foreach (var transaction in transactions)
        {
            ArgumentNullException.ThrowIfNull(transaction);

            var record = new TransactionResultRecord
            {
                TransactionId = transaction.GetTransactionId(),
                Status = TransactionResultStatus.Mined,
                Error = null,
                BlockHeight = block.Height,
                BlockHash = block.Hash,
                UpdatedAtUtc = updatedAtUtc
            };

            writes[CreateKey(record.TransactionId)] = Serialize(record);
        }

        return _database.SetAllAsync(writes, cancellationToken);
    }

    private static byte[] Serialize(TransactionResultRecord record)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            record,
            TransactionResultStoreJsonSerializerContext.Default.TransactionResultRecord);
    }

    private static string CreateKey(string transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        return $"{TransactionResultPrefix}{transactionId}";
    }
}
