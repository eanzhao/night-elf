namespace NightElf.Database.Redis;

public sealed class RedisDatabase<TContext> : IKeyValueDatabase<TContext>, IDisposable
    where TContext : KeyValueDbContext<TContext>
{
    private readonly IRedisDatabaseClient _client;
    private readonly RedisDatabaseOptions<TContext> _options;
    private volatile bool _disposed;

    public RedisDatabase(IRedisDatabaseClient client, RedisDatabaseOptions<TContext> options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);

        return _client.GetAsync(_options.CreateScopedKey(key));
    }

    public async Task<IReadOnlyDictionary<string, byte[]?>> GetAllAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(keys);

        var scopedKeys = new Dictionary<string, string>(keys.Count, StringComparer.Ordinal);

        foreach (var key in keys)
        {
            ValidateKey(key);
            scopedKeys[key] = _options.CreateScopedKey(key);
        }

        var values = await _client.GetAllAsync(scopedKeys.Values.ToArray()).ConfigureAwait(false);
        var results = new Dictionary<string, byte[]?>(scopedKeys.Count, StringComparer.Ordinal);

        foreach (var pair in scopedKeys)
        {
            results[pair.Key] = values[pair.Value];
        }

        return results;
    }

    public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        return _client.SetAsync(_options.CreateScopedKey(key), value);
    }

    public Task SetAllAsync(
        IReadOnlyDictionary<string, byte[]> values,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(values);

        var scopedValues = new Dictionary<string, byte[]>(values.Count, StringComparer.Ordinal);

        foreach (var pair in values)
        {
            ValidateKey(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            scopedValues[_options.CreateScopedKey(pair.Key)] = pair.Value;
        }

        return _client.SetAllAsync(scopedValues);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);

        return _client.DeleteAsync(_options.CreateScopedKey(key));
    }

    public Task DeleteAllAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(keys);

        var scopedKeys = new List<string>(keys.Count);

        foreach (var key in keys)
        {
            ValidateKey(key);
            scopedKeys.Add(_options.CreateScopedKey(key));
        }

        return _client.DeleteAllAsync(scopedKeys);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);

        return _client.ExistsAsync(_options.CreateScopedKey(key));
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
