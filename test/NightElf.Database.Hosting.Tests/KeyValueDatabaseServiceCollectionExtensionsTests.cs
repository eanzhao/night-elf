using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NightElf.Database.Redis;
using NightElf.Database.Tsavorite;

namespace NightElf.Database.Hosting.Tests;

public sealed class KeyValueDatabaseServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddKeyValueDatabase_Should_Use_Tsavorite_By_Default()
    {
        var rootPath = CreateRootPath();

        try
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["NightElf:Database:Tsavorite:DataRootPath"] = Path.Combine(rootPath, "data"),
                ["NightElf:Database:Tsavorite:CheckpointRootPath"] = Path.Combine(rootPath, "checkpoints")
            });

            services.AddKeyValueDatabase<TestDbContext>(configuration);

            using var serviceProvider = services.BuildServiceProvider();
            var providerOptions = serviceProvider.GetRequiredService<KeyValueDatabaseProviderOptions>();
            var database = serviceProvider.GetRequiredService<IKeyValueDatabase<TestDbContext>>();

            Assert.Equal(KeyValueDatabaseProviderKind.Tsavorite, providerOptions.ResolveProvider());
            Assert.IsType<TsavoriteDatabase<TestDbContext>>(database);

            await AssertDatabaseContractAsync(database);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public async Task AddKeyValueDatabase_Should_Use_Redis_When_Configured()
    {
        var services = new ServiceCollection();
        var fakeClient = new FakeRedisDatabaseClient();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NightElf:Database:Provider"] = "Redis",
            ["NightElf:Database:Redis:ConnectionString"] = "localhost:6379,abortConnect=false",
            ["NightElf:Database:Redis:KeyPrefix"] = "nightelf-test:"
        });

        services.AddSingleton<IRedisDatabaseClient>(fakeClient);
        services.AddKeyValueDatabase<TestDbContext>(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var providerOptions = serviceProvider.GetRequiredService<KeyValueDatabaseProviderOptions>();
        var database = serviceProvider.GetRequiredService<IKeyValueDatabase<TestDbContext>>();

        Assert.Equal(KeyValueDatabaseProviderKind.Redis, providerOptions.ResolveProvider());
        Assert.IsType<RedisDatabase<TestDbContext>>(database);

        await AssertDatabaseContractAsync(database);
        Assert.Contains("nightelf-test:TestDbContext:alpha", fakeClient.TouchedKeys);
    }

    [Fact]
    public void AddKeyValueDatabase_Should_Fail_For_Unsupported_Provider()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NightElf:Database:Provider"] = "Sqlite"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddKeyValueDatabase<TestDbContext>(configuration));

        Assert.Contains("Unsupported database provider 'Sqlite'", exception.Message);
    }

    [Fact]
    public void AddKeyValueDatabase_Should_Fail_When_Redis_ConnectionString_Is_Missing()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NightElf:Database:Provider"] = "Redis"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddKeyValueDatabase<TestDbContext>(configuration));

        Assert.Contains("NightElf:Database:Redis:ConnectionString", exception.Message);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static async Task AssertDatabaseContractAsync(IKeyValueDatabase<TestDbContext> database)
    {
        var values = new Dictionary<string, byte[]>
        {
            ["alpha"] = new byte[] { 1, 2, 3 },
            ["beta"] = new byte[] { 4, 5 }
        };

        await database.SetAllAsync(values);

        var loaded = await database.GetAllAsync(new[] { "alpha", "beta", "missing" });

        Assert.Equal(values["alpha"], loaded["alpha"]);
        Assert.Equal(values["beta"], loaded["beta"]);
        Assert.Null(loaded["missing"]);
        Assert.True(await database.ExistsAsync("alpha"));

        await database.DeleteAsync("beta");

        Assert.Null(await database.GetAsync("beta"));

        await database.SetAsync("gamma", new byte[] { 9 });
        await database.DeleteAllAsync(new[] { "alpha", "gamma" });

        Assert.Null(await database.GetAsync("alpha"));
        Assert.Null(await database.GetAsync("gamma"));
    }

    private static string CreateRootPath()
    {
        return Path.Combine(Path.GetTempPath(), "nightelf-database-hosting-tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteRootPath(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class TestDbContext : KeyValueDbContext<TestDbContext>
    {
    }

    private sealed class FakeRedisDatabaseClient : IRedisDatabaseClient
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        private readonly HashSet<string> _touchedKeys = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> TouchedKeys => _touchedKeys.ToArray();

        public Task<byte[]?> GetAsync(string key)
        {
            _touchedKeys.Add(key);
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
        }

        public Task<IReadOnlyDictionary<string, byte[]?>> GetAllAsync(IReadOnlyCollection<string> keys)
        {
            var results = new Dictionary<string, byte[]?>(keys.Count, StringComparer.Ordinal);

            foreach (var key in keys)
            {
                _touchedKeys.Add(key);
                results[key] = _values.TryGetValue(key, out var value) ? value.ToArray() : null;
            }

            return Task.FromResult<IReadOnlyDictionary<string, byte[]?>>(results);
        }

        public Task SetAsync(string key, byte[] value)
        {
            _touchedKeys.Add(key);
            _values[key] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task SetAllAsync(IReadOnlyDictionary<string, byte[]> values)
        {
            foreach (var pair in values)
            {
                _touchedKeys.Add(pair.Key);
                _values[pair.Key] = pair.Value.ToArray();
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            _touchedKeys.Add(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task DeleteAllAsync(IReadOnlyCollection<string> keys)
        {
            foreach (var key in keys)
            {
                _touchedKeys.Add(key);
                _values.Remove(key);
            }

            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string key)
        {
            _touchedKeys.Add(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public void Dispose()
        {
        }
    }
}
