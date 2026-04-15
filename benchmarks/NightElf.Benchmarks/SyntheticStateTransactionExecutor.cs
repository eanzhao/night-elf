using NightElf.Database;
using NightElf.Kernel.SmartContract;

namespace NightElf.Benchmarks;

internal sealed class SyntheticStateTransactionExecutor<TContext>
    where TContext : KeyValueDbContext<TContext>
{
    private readonly NotModifiedCachedStateStore<TContext> _cachedStore;
    private readonly IKeyValueDatabase<TContext> _database;
    private readonly CachedStateReader<TContext> _reader;

    public SyntheticStateTransactionExecutor(
        CachedStateReader<TContext> reader,
        NotModifiedCachedStateStore<TContext> cachedStore,
        IKeyValueDatabase<TContext> database)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _cachedStore = cachedStore ?? throw new ArgumentNullException(nameof(cachedStore));
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<int> ExecuteAsync(
        SyntheticStateTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var bytesRead = 0;

        foreach (var key in transaction.TieredReadKeys)
        {
            bytesRead += await ReadLengthAsync(key, cancellationToken).ConfigureAwait(false);
        }

        foreach (var key in transaction.CachedReadKeys)
        {
            bytesRead += await ReadLengthAsync(key, cancellationToken).ConfigureAwait(false);
        }

        foreach (var key in transaction.ColdReadKeys)
        {
            bytesRead += await ReadLengthAsync(key, cancellationToken).ConfigureAwait(false);
        }

        await _database.SetAllAsync(transaction.Writes, cancellationToken).ConfigureAwait(false);

        foreach (var pair in transaction.Writes)
        {
            _cachedStore.SetCached(pair.Key, pair.Value);
            bytesRead += pair.Value.Length;
        }

        return bytesRead;
    }

    private async Task<int> ReadLengthAsync(string key, CancellationToken cancellationToken)
    {
        var value = await _reader.GetStateAsync(key, cancellationToken).ConfigureAwait(false);
        return value?.Length ?? 0;
    }
}
