using System.Text;

using NightElf.Database;
using NightElf.Kernel.SmartContract;

namespace NightElf.Kernel.SmartContract.Tests;

public sealed class CachedStateReaderTests
{
    [Fact]
    public void TieredStateCache_Should_Use_Parent_Values_And_Current_Overrides()
    {
        var parent = new DictionaryStateCache();
        parent["alpha"] = Encoding.UTF8.GetBytes("parent-alpha");

        var cache = new TieredStateCache(parent);

        Assert.False(cache.TryGet("missing", out var missingValue));
        Assert.Null(missingValue);

        Assert.True(cache.TryGet("alpha", out var originalValue));
        Assert.Equal("parent-alpha", Encoding.UTF8.GetString(originalValue!));

        cache.Update(new Dictionary<string, byte[]?>
        {
            ["alpha"] = Encoding.UTF8.GetBytes("current-alpha"),
            ["deleted"] = null
        });

        Assert.True(cache.TryGet("alpha", out var currentValue));
        Assert.Equal("current-alpha", Encoding.UTF8.GetString(currentValue!));

        Assert.True(cache.TryGet("deleted", out var deletedValue));
        Assert.Null(deletedValue);
    }

    [Fact]
    public async Task NotModifiedCachedStateStore_Should_Expose_Synchronous_Hit_After_Load()
    {
        var database = new FakeDatabase(new Dictionary<string, byte[]?>
        {
            ["alpha"] = Encoding.UTF8.GetBytes("database-alpha")
        });
        var store = new NotModifiedCachedStateStore<TestDbContext>(database);

        Assert.False(store.TryGetCached("alpha", out var uncachedValue));
        Assert.Null(uncachedValue);

        var firstRead = store.GetAsync("alpha");
        var loaded = await firstRead;

        Assert.Equal("database-alpha", Encoding.UTF8.GetString(loaded!));
        Assert.Equal(1, database.GetCalls);
        Assert.True(store.TryGetCached("alpha", out var cachedValue));
        Assert.Equal("database-alpha", Encoding.UTF8.GetString(cachedValue!));

        var secondRead = store.GetAsync("alpha");

        Assert.True(secondRead.IsCompletedSuccessfully);
        Assert.Equal("database-alpha", Encoding.UTF8.GetString((await secondRead)!));
        Assert.Equal(1, database.GetCalls);
    }

    [Fact]
    public void CachedStateReader_Should_Prefer_Tiered_Cache_Over_NotModified_Cache()
    {
        var tieredCache = new TieredStateCache();
        tieredCache.Update(new Dictionary<string, byte[]?>
        {
            ["alpha"] = Encoding.UTF8.GetBytes("tiered-alpha")
        });

        var store = new NotModifiedCachedStateStore<TestDbContext>(new FakeDatabase());
        store.SetCached("alpha", Encoding.UTF8.GetBytes("cached-alpha"));

        var reader = new CachedStateReader<TestDbContext>(tieredCache, store);

        Assert.True(reader.TryGetState("alpha", out var value));
        Assert.Equal("tiered-alpha", Encoding.UTF8.GetString(value!));
    }

    [Fact]
    public async Task CachedStateReader_Should_Use_Synchronous_Path_For_Cache_Hits()
    {
        var database = new FakeDatabase(new Dictionary<string, byte[]?>
        {
            ["alpha"] = Encoding.UTF8.GetBytes("database-alpha")
        });
        var store = new NotModifiedCachedStateStore<TestDbContext>(database);
        store.SetCached("alpha", Encoding.UTF8.GetBytes("cached-alpha"));

        var reader = new CachedStateReader<TestDbContext>(new TieredStateCache(), store);
        var read = reader.GetStateAsync("alpha");

        Assert.True(read.IsCompletedSuccessfully);
        Assert.Equal("cached-alpha", Encoding.UTF8.GetString((await read)!));
        Assert.Equal(0, database.GetCalls);
    }

    [Fact]
    public async Task CachedStateReader_Should_Load_Miss_From_Database_And_Promote_It_To_Cached_Hit()
    {
        var database = new FakeDatabase(new Dictionary<string, byte[]?>
        {
            ["alpha"] = Encoding.UTF8.GetBytes("database-alpha")
        });
        var store = new NotModifiedCachedStateStore<TestDbContext>(database);
        var reader = new CachedStateReader<TestDbContext>(new TieredStateCache(), store);

        Assert.False(reader.TryGetState("alpha", out var beforeLoad));
        Assert.Null(beforeLoad);

        var value = await reader.GetStateAsync("alpha");

        Assert.Equal("database-alpha", Encoding.UTF8.GetString(value!));
        Assert.Equal(1, database.GetCalls);
        Assert.True(reader.TryGetState("alpha", out var cachedValue));
        Assert.Equal("database-alpha", Encoding.UTF8.GetString(cachedValue!));
    }

    [Fact]
    public async Task CachedStateReader_Should_Cache_Null_Misses_Without_Requerying_Database()
    {
        var database = new FakeDatabase();
        var store = new NotModifiedCachedStateStore<TestDbContext>(database);
        var reader = new CachedStateReader<TestDbContext>(new TieredStateCache(), store);

        Assert.Null(await reader.GetStateAsync("missing"));
        Assert.Equal(1, database.GetCalls);
        Assert.True(reader.TryGetState("missing", out var cachedValue));
        Assert.Null(cachedValue);

        var secondRead = reader.GetStateAsync("missing");

        Assert.True(secondRead.IsCompletedSuccessfully);
        Assert.Null(await secondRead);
        Assert.Equal(1, database.GetCalls);
    }

    private sealed class DictionaryStateCache : IStateCache
    {
        private readonly Dictionary<string, byte[]?> _values = new(StringComparer.Ordinal);

        public byte[]? this[string key]
        {
            get => _values.TryGetValue(key, out var value) ? value : null;
            set => _values[key] = value;
        }

        public bool TryGet(string key, out byte[]? value)
        {
            return _values.TryGetValue(key, out value);
        }
    }

    private sealed class FakeDatabase : IKeyValueDatabase<TestDbContext>
    {
        private readonly Dictionary<string, byte[]?> _values;

        public FakeDatabase()
            : this(new Dictionary<string, byte[]?>(StringComparer.Ordinal))
        {
        }

        public FakeDatabase(Dictionary<string, byte[]?> values)
        {
            _values = new Dictionary<string, byte[]?>(values, StringComparer.Ordinal);
        }

        public int GetCalls { get; private set; }

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task<IReadOnlyDictionary<string, byte[]?>> GetAllAsync(
            IReadOnlyCollection<string> keys,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, byte[]?>(keys.Count, StringComparer.Ordinal);

            foreach (var key in keys)
            {
                results[key] = _values.TryGetValue(key, out var value) ? value : null;
            }

            return Task.FromResult<IReadOnlyDictionary<string, byte[]?>>(results);
        }

        public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetAllAsync(IReadOnlyDictionary<string, byte[]> values, CancellationToken cancellationToken = default)
        {
            foreach (var pair in values)
            {
                _values[pair.Key] = pair.Value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task DeleteAllAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
        {
            foreach (var key in keys)
            {
                _values.Remove(key);
            }

            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_values.ContainsKey(key));
        }
    }

    private sealed class TestDbContext : KeyValueDbContext<TestDbContext>
    {
    }
}
