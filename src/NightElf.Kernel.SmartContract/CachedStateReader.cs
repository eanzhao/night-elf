using NightElf.Database;

namespace NightElf.Kernel.SmartContract;

public sealed class CachedStateReader<TContext>
    where TContext : KeyValueDbContext<TContext>
{
    private readonly NotModifiedCachedStateStore<TContext> _cachedStore;
    private readonly IStateCache _tieredCache;

    public CachedStateReader(
        IStateCache tieredCache,
        NotModifiedCachedStateStore<TContext> cachedStore)
    {
        _tieredCache = tieredCache ?? throw new ArgumentNullException(nameof(tieredCache));
        _cachedStore = cachedStore ?? throw new ArgumentNullException(nameof(cachedStore));
    }

    public bool TryGetState(string key, out byte[]? value)
    {
        ValidateKey(key);

        if (_tieredCache.TryGet(key, out value))
        {
            return true;
        }

        return _cachedStore.TryGetCached(key, out value);
    }

    public ValueTask<byte[]?> GetStateAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        if (TryGetState(key, out var value))
        {
            return ValueTask.FromResult(value);
        }

        return _cachedStore.GetAsync(key, cancellationToken);
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
    }
}
