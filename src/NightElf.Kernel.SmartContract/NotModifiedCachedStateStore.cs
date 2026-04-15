using System.Collections.Concurrent;

using NightElf.Database;

namespace NightElf.Kernel.SmartContract;

public sealed class NotModifiedCachedStateStore<TContext>
    where TContext : KeyValueDbContext<TContext>
{
    private readonly ConcurrentDictionary<string, CachedStateEntry> _cachedValues = new(StringComparer.Ordinal);
    private readonly IKeyValueDatabase<TContext> _database;

    public NotModifiedCachedStateStore(IKeyValueDatabase<TContext> database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public bool TryGetCached(string key, out byte[]? value)
    {
        ValidateKey(key);

        if (_cachedValues.TryGetValue(key, out var entry))
        {
            value = entry.Value;
            return true;
        }

        value = null;
        return false;
    }

    public void SetCached(string key, byte[]? value)
    {
        ValidateKey(key);
        _cachedValues[key] = new CachedStateEntry(value);
    }

    public bool RemoveCached(string key)
    {
        ValidateKey(key);
        return _cachedValues.TryRemove(key, out _);
    }

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        if (TryGetCached(key, out var cachedValue))
        {
            return ValueTask.FromResult(cachedValue);
        }

        return new ValueTask<byte[]?>(GetAndCacheAsync(key, cancellationToken));
    }

    private async Task<byte[]?> GetAndCacheAsync(string key, CancellationToken cancellationToken)
    {
        var value = await _database.GetAsync(key, cancellationToken).ConfigureAwait(false);
        _cachedValues[key] = new CachedStateEntry(value);
        return value;
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
    }

    private sealed record CachedStateEntry(byte[]? Value);
}
