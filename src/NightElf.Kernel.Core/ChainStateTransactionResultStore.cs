using System.Text.Json;

using NightElf.Database;
using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core;

public sealed class ChainStateTransactionResultStore : ITransactionResultStore
{
    private const string TransactionResultPrefix = "tx:result:";
    private readonly IChainStateStore _chainStateStore;

    public ChainStateTransactionResultStore(IChainStateStore chainStateStore)
    {
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
    }

    public async Task<TransactionResultRecord?> GetAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        var bytes = await _chainStateStore.Database.GetAsync(CreateKey(transactionId), cancellationToken).ConfigureAwait(false);
        return Deserialize(bytes);
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

        await _chainStateStore.Database.SetAsync(
                CreateKey(transactionId),
                Serialize(record),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task RecordRejectedAsync(
        string transactionId,
        string? error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        return RecordAsync(
            new TransactionResultRecord
            {
                TransactionId = transactionId,
                Status = TransactionResultStatus.Rejected,
                Error = error,
                BlockHeight = 0,
                BlockHash = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    public Task RecordBlockResultAsync(
        Transaction transaction,
        BlockReference block,
        TransactionResultStatus status,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(block);

        var record = new TransactionResultRecord
        {
            TransactionId = transaction.GetTransactionId(),
            Status = status,
            Error = error,
            BlockHeight = block.Height,
            BlockHash = block.Hash,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        return _chainStateStore.ApplyChangesAsync(
            block,
            new Dictionary<string, byte[]>(1, StringComparer.Ordinal)
            {
                [CreateKey(record.TransactionId)] = Serialize(record)
            },
            cancellationToken: cancellationToken);
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

        return _chainStateStore.ApplyChangesAsync(block, writes, cancellationToken: cancellationToken);
    }

    private Task RecordAsync(
        TransactionResultRecord record,
        CancellationToken cancellationToken)
    {
        return _chainStateStore.Database.SetAsync(
            CreateKey(record.TransactionId),
            Serialize(record),
            cancellationToken);
    }

    private static TransactionResultRecord? Deserialize(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (HasProperty(root, "transactionId"))
        {
            return JsonSerializer.Deserialize(
                bytes,
                TransactionResultStoreJsonSerializerContext.Default.TransactionResultRecord);
        }

        if (!HasProperty(root, "value"))
        {
            throw new InvalidOperationException("Transaction result payload is neither a raw record nor a versioned state record.");
        }

        var versionedRecord = VersionedStateRecord.Deserialize(bytes);
        if (versionedRecord.IsDeleted)
        {
            return null;
        }

        return JsonSerializer.Deserialize(
            versionedRecord.Value,
            TransactionResultStoreJsonSerializerContext.Default.TransactionResultRecord);
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

    private static bool HasProperty(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out _))
        {
            return true;
        }

        var pascalCasePropertyName = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        return root.TryGetProperty(pascalCasePropertyName, out _);
    }
}
