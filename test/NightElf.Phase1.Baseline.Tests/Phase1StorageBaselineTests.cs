using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using NightElf.Database;
using NightElf.Database.Hosting;
using NightElf.Database.Redis;
using NightElf.Database.Tsavorite;
using NightElf.Kernel.Core;

namespace NightElf.Phase1.Baseline.Tests;

public sealed class Phase1StorageBaselineTests
{
    private const string BestChainKey = "chain:best";

    [Fact]
    public async Task Tsavorite_StateBaseline_Should_RoundTrip_BestChain()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var serviceProvider = CreateTsavoriteServiceProvider(rootPath);
            var database = serviceProvider.GetRequiredService<IKeyValueDatabase<ChainStateDbContext>>();
            var expected = new BlockReference(12, "best-chain-hash");

            await database.SetAsync(BestChainKey, JsonSerializer.SerializeToUtf8Bytes(expected));

            var actual = await GetBestChainAsync(database);

            Assert.Equal(expected, actual);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public async Task Redis_StateBaseline_Should_RoundTrip_BestChain()
    {
        using var fakeClient = new FakeRedisDatabaseClient();
        using var serviceProvider = CreateRedisServiceProvider(fakeClient);
        var database = serviceProvider.GetRequiredService<IKeyValueDatabase<ChainStateDbContext>>();
        var expected = new BlockReference(34, "redis-best-chain-hash");

        await database.SetAsync(BestChainKey, JsonSerializer.SerializeToUtf8Bytes(expected));

        var actual = await GetBestChainAsync(database);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Tsavorite_Should_Isolate_ChainState_And_Auxiliary_State_Using_The_Same_Key()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var serviceProvider = CreateTsavoriteServiceProvider(rootPath);

            await AssertContextIsolationAsync(serviceProvider);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public async Task Redis_Should_Isolate_ChainState_And_Auxiliary_State_Using_The_Same_Key()
    {
        using var fakeClient = new FakeRedisDatabaseClient();
        using var serviceProvider = CreateRedisServiceProvider(fakeClient);

        await AssertContextIsolationAsync(serviceProvider);
    }

    private static async Task<BlockReference?> GetBestChainAsync(IKeyValueDatabase<ChainStateDbContext> database)
    {
        var bytes = await database.GetAsync(BestChainKey);
        return bytes is null ? null : JsonSerializer.Deserialize<BlockReference>(bytes);
    }

    private static async Task AssertContextIsolationAsync(ServiceProvider serviceProvider)
    {
        var chainStateDatabase = serviceProvider.GetRequiredService<IKeyValueDatabase<ChainStateDbContext>>();
        var auxiliaryDatabase = serviceProvider.GetRequiredService<IKeyValueDatabase<AuxiliaryStateDbContext>>();

        await chainStateDatabase.SetAsync("shared-key", Encoding.UTF8.GetBytes("chain-state"));
        await auxiliaryDatabase.SetAsync("shared-key", Encoding.UTF8.GetBytes("auxiliary-state"));

        Assert.Equal("chain-state", Encoding.UTF8.GetString((await chainStateDatabase.GetAsync("shared-key"))!));
        Assert.Equal("auxiliary-state", Encoding.UTF8.GetString((await auxiliaryDatabase.GetAsync("shared-key"))!));
    }

    private static ServiceProvider CreateTsavoriteServiceProvider(string rootPath)
    {
        var services = new ServiceCollection();
        var providerOptions = new KeyValueDatabaseProviderOptions
        {
            Provider = KeyValueDatabaseProviderKind.Tsavorite.ToString(),
            Tsavorite = new TsavoriteStoreSetOptions
            {
                DataRootPath = Path.Combine(rootPath, "data"),
                CheckpointRootPath = Path.Combine(rootPath, "checkpoints")
            }
        };

        services.AddKeyValueDatabase<ChainStateDbContext>(providerOptions, TsavoriteStoreKind.State);
        services.AddKeyValueDatabase<AuxiliaryStateDbContext>(providerOptions, TsavoriteStoreKind.State);

        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateRedisServiceProvider(FakeRedisDatabaseClient fakeClient)
    {
        var services = new ServiceCollection();
        var providerOptions = new KeyValueDatabaseProviderOptions
        {
            Provider = KeyValueDatabaseProviderKind.Redis.ToString(),
            Redis = new RedisConnectionOptions
            {
                ConnectionString = "localhost:6379,abortConnect=false",
                KeyPrefix = "nightelf-phase1:"
            }
        };

        services.AddSingleton<IRedisDatabaseClient>(fakeClient);
        services.AddKeyValueDatabase<ChainStateDbContext>(providerOptions, TsavoriteStoreKind.State);
        services.AddKeyValueDatabase<AuxiliaryStateDbContext>(providerOptions, TsavoriteStoreKind.State);

        return services.BuildServiceProvider();
    }

    private static string CreateRootPath()
    {
        return Path.Combine(Path.GetTempPath(), "nightelf-phase1-baseline-tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteRootPath(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class AuxiliaryStateDbContext : KeyValueDbContext<AuxiliaryStateDbContext>
    {
    }

    private sealed class FakeRedisDatabaseClient : IRedisDatabaseClient
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public Task<byte[]?> GetAsync(string key)
        {
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
        }

        public Task<IReadOnlyDictionary<string, byte[]?>> GetAllAsync(IReadOnlyCollection<string> keys)
        {
            var results = new Dictionary<string, byte[]?>(keys.Count, StringComparer.Ordinal);

            foreach (var key in keys)
            {
                results[key] = _values.TryGetValue(key, out var value) ? value.ToArray() : null;
            }

            return Task.FromResult<IReadOnlyDictionary<string, byte[]?>>(results);
        }

        public Task SetAsync(string key, byte[] value)
        {
            _values[key] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task SetAllAsync(IReadOnlyDictionary<string, byte[]> values)
        {
            foreach (var pair in values)
            {
                _values[pair.Key] = pair.Value.ToArray();
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task DeleteAllAsync(IReadOnlyCollection<string> keys)
        {
            foreach (var key in keys)
            {
                _values.Remove(key);
            }

            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string key)
        {
            return Task.FromResult(_values.ContainsKey(key));
        }

        public void Dispose()
        {
        }
    }
}
