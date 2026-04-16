using StackExchange.Redis;

namespace NightElf.Database.Redis;

public sealed class StackExchangeRedisDatabaseClient : IRedisDatabaseClient
{
    private readonly ConnectionMultiplexer _connection;
    private readonly IDatabase _database;
    private volatile bool _disposed;

    public StackExchangeRedisDatabaseClient(RedisConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _connection = ConnectionMultiplexer.Connect(options.ConnectionString!);
        _database = _connection.GetDatabase(options.Database);
    }

    public async Task<byte[]?> GetAsync(string key)
    {
        ThrowIfDisposed();
        ValidateKey(key);

        var value = await _database.StringGetAsync(key).ConfigureAwait(false);
        return value.IsNull ? null : (byte[])value!;
    }

    public async Task<IReadOnlyDictionary<string, byte[]?>> GetAllAsync(IReadOnlyCollection<string> keys)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(keys);

        var scopedKeys = keys
            .Select(static key =>
            {
                ValidateKey(key);
                return (RedisKey)key;
            })
            .ToArray();

        var values = await _database.StringGetAsync(scopedKeys).ConfigureAwait(false);
        var results = new Dictionary<string, byte[]?>(scopedKeys.Length, StringComparer.Ordinal);

        for (var i = 0; i < scopedKeys.Length; i++)
        {
            results[(string)scopedKeys[i]!] = values[i].IsNull ? null : (byte[])values[i]!;
        }

        return results;
    }

    public async Task SetAsync(string key, byte[] value)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        await _database.StringSetAsync(key, value).ConfigureAwait(false);
    }

    public async Task SetAllAsync(IReadOnlyDictionary<string, byte[]> values)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(values);

        var pairs = values
            .Select(pair =>
            {
                ValidateKey(pair.Key);
                ArgumentNullException.ThrowIfNull(pair.Value);
                return new KeyValuePair<RedisKey, RedisValue>(pair.Key, pair.Value);
            })
            .ToArray();

        await _database.StringSetAsync(pairs).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key)
    {
        ThrowIfDisposed();
        ValidateKey(key);

        await _database.KeyDeleteAsync(key).ConfigureAwait(false);
    }

    public async Task DeleteAllAsync(IReadOnlyCollection<string> keys)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(keys);

        var scopedKeys = keys
            .Select(static key =>
            {
                ValidateKey(key);
                return (RedisKey)key;
            })
            .ToArray();

        await _database.KeyDeleteAsync(scopedKeys).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string key)
    {
        ThrowIfDisposed();
        ValidateKey(key);

        return await _database.KeyExistsAsync(key).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _connection.Dispose();
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
