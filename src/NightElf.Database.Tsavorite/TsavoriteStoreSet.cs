namespace NightElf.Database.Tsavorite;

public sealed class TsavoriteStoreSet : IDisposable
{
    private readonly List<IDisposable> _ownedStores = [];
    private bool _disposed;

    public TsavoriteStoreSet(TsavoriteStoreSetOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public TsavoriteStoreSetOptions Options { get; }

    public TsavoriteDatabase<TContext> CreateStore<TContext>(TsavoriteStoreKind storeKind)
        where TContext : KeyValueDbContext<TContext>
    {
        ThrowIfDisposed();

        var store = new TsavoriteDatabase<TContext>(Options.CreateDatabaseOptions<TContext>(storeKind));
        _ownedStores.Add(store);
        return store;
    }

    public TsavoriteDatabase<TContext> CreateBlockStore<TContext>()
        where TContext : KeyValueDbContext<TContext>
    {
        return CreateStore<TContext>(TsavoriteStoreKind.Block);
    }

    public TsavoriteDatabase<TContext> CreateStateStore<TContext>()
        where TContext : KeyValueDbContext<TContext>
    {
        return CreateStore<TContext>(TsavoriteStoreKind.State);
    }

    public TsavoriteDatabase<TContext> CreateIndexStore<TContext>()
        where TContext : KeyValueDbContext<TContext>
    {
        return CreateStore<TContext>(TsavoriteStoreKind.Index);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var store in _ownedStores)
        {
            store.Dispose();
        }

        _ownedStores.Clear();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
