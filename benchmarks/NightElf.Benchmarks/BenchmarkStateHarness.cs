using System.Text;

using NightElf.Database;
using NightElf.Database.Redis;
using NightElf.Database.Tsavorite;
using NightElf.Kernel.SmartContract;

namespace NightElf.Benchmarks;

internal sealed class BenchmarkStateHarness : IDisposable
{
    private const int PayloadSize = 96;

    private readonly FakeRedisDatabaseClient? _fakeRedisDatabaseClient;
    private readonly string? _rootPath;
    private readonly IDisposable? _ownedDatabase;
    private readonly SyntheticStateTransactionExecutor<BenchmarkStateDbContext> _transactionExecutor;

    private BenchmarkStateHarness(
        BenchmarkStorageProvider provider,
        IKeyValueDatabase<BenchmarkStateDbContext> database,
        FakeRedisDatabaseClient? fakeRedisDatabaseClient,
        string? rootPath)
    {
        Provider = provider;
        Database = database;
        _fakeRedisDatabaseClient = fakeRedisDatabaseClient;
        _rootPath = rootPath;
        _ownedDatabase = database as IDisposable;

        TieredCache = new TieredStateCache();
        CachedStore = new NotModifiedCachedStateStore<BenchmarkStateDbContext>(database);
        Reader = new CachedStateReader<BenchmarkStateDbContext>(TieredCache, CachedStore);
        _transactionExecutor = new SyntheticStateTransactionExecutor<BenchmarkStateDbContext>(Reader, CachedStore, Database);
    }

    public BenchmarkStorageProvider Provider { get; }

    public IKeyValueDatabase<BenchmarkStateDbContext> Database { get; }

    public TieredStateCache TieredCache { get; }

    public NotModifiedCachedStateStore<BenchmarkStateDbContext> CachedStore { get; }

    public CachedStateReader<BenchmarkStateDbContext> Reader { get; }

    public string TieredHitKey => "benchmark:l1-hit";

    public string CachedHitKey => "benchmark:l2-hit";

    public string ColdReadKey => "benchmark:cold-read";

    public SyntheticStateTransaction SingleTransaction { get; private set; } = SyntheticStateTransaction.Empty;

    public IReadOnlyList<SyntheticStateTransaction> BatchTransactions { get; private set; } = Array.Empty<SyntheticStateTransaction>();

    public static async Task<BenchmarkStateHarness> CreateAsync(BenchmarkStorageProvider provider)
    {
        FakeRedisDatabaseClient? fakeRedisDatabaseClient = null;
        string? rootPath = null;

        IKeyValueDatabase<BenchmarkStateDbContext> database = provider switch
        {
            BenchmarkStorageProvider.RedisCompat => CreateRedisDatabase(out fakeRedisDatabaseClient),
            BenchmarkStorageProvider.Tsavorite => CreateTsavoriteDatabase(out rootPath),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported benchmark storage provider.")
        };

        var harness = new BenchmarkStateHarness(provider, database, fakeRedisDatabaseClient, rootPath);
        await harness.InitializeAsync().ConfigureAwait(false);
        return harness;
    }

    public void PrepareColdRead()
    {
        CachedStore.RemoveCached(ColdReadKey);
    }

    public void PrepareTransaction(SyntheticStateTransaction transaction)
    {
        foreach (var key in transaction.ColdReadKeys)
        {
            CachedStore.RemoveCached(key);
        }
    }

    public Task<int> ExecuteAsync(SyntheticStateTransaction transaction, CancellationToken cancellationToken = default)
    {
        return _transactionExecutor.ExecuteAsync(transaction, cancellationToken);
    }

    public void Dispose()
    {
        _ownedDatabase?.Dispose();
        _fakeRedisDatabaseClient?.Dispose();

        if (_rootPath is not null && Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private static IKeyValueDatabase<BenchmarkStateDbContext> CreateRedisDatabase(
        out FakeRedisDatabaseClient fakeRedisDatabaseClient)
    {
        fakeRedisDatabaseClient = new FakeRedisDatabaseClient();

        return new RedisDatabase<BenchmarkStateDbContext>(
            fakeRedisDatabaseClient,
            new RedisDatabaseOptions<BenchmarkStateDbContext>
            {
                ConnectionString = "localhost:6379,abortConnect=false",
                KeyPrefix = "nightelf-benchmark:"
            });
    }

    private static IKeyValueDatabase<BenchmarkStateDbContext> CreateTsavoriteDatabase(out string rootPath)
    {
        rootPath = Path.Combine(Path.GetTempPath(), "nightelf-benchmarks", Guid.NewGuid().ToString("N"));

        return new TsavoriteDatabase<BenchmarkStateDbContext>(new TsavoriteDatabaseOptions<BenchmarkStateDbContext>
        {
            StoreKind = TsavoriteStoreKind.State,
            DataPath = Path.Combine(rootPath, "data"),
            CheckpointPath = Path.Combine(rootPath, "checkpoints"),
            IndexSize = 1L << 16,
            PageSize = 1L << 12,
            SegmentSize = 1L << 18,
            MemorySize = 1L << 20
        });
    }

    private async Task InitializeAsync()
    {
        var initialValues = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [TieredHitKey] = CreatePayload("tiered-hit"),
            [CachedHitKey] = CreatePayload("cached-hit"),
            [ColdReadKey] = CreatePayload("cold-read")
        };

        SingleTransaction = CreateTransaction("single");
        BatchTransactions = Enumerable.Range(0, 32)
            .Select(index => CreateTransaction($"batch-{index:D2}"))
            .ToArray();

        foreach (var pair in SingleTransaction.GetSeedValues())
        {
            initialValues[pair.Key] = pair.Value;
        }

        foreach (var transaction in BatchTransactions)
        {
            foreach (var pair in transaction.GetSeedValues())
            {
                initialValues[pair.Key] = pair.Value;
            }
        }

        await Database.SetAllAsync(initialValues).ConfigureAwait(false);

        TieredCache.Update(new Dictionary<string, byte[]?>
        {
            [TieredHitKey] = initialValues[TieredHitKey]
        });
        CachedStore.SetCached(CachedHitKey, initialValues[CachedHitKey]);

        PrimeTransactionHotReads(SingleTransaction);
        foreach (var transaction in BatchTransactions)
        {
            PrimeTransactionHotReads(transaction);
        }
    }

    private void PrimeTransactionHotReads(SyntheticStateTransaction transaction)
    {
        TieredCache.Update(transaction.TieredReadKeys.ToDictionary(
            key => key,
            key => (byte[]?)CreatePayload($"{key}:tiered"),
            StringComparer.Ordinal));

        foreach (var key in transaction.CachedReadKeys)
        {
            CachedStore.SetCached(key, CreatePayload($"{key}:cached"));
        }
    }

    private static SyntheticStateTransaction CreateTransaction(string prefix)
    {
        var tieredReadKeys = Enumerable.Range(0, 2)
            .Select(index => $"benchmark:{prefix}:read:l1:{index}")
            .ToArray();
        var cachedReadKeys = Enumerable.Range(0, 2)
            .Select(index => $"benchmark:{prefix}:read:l2:{index}")
            .ToArray();
        var coldReadKeys = Enumerable.Range(0, 4)
            .Select(index => $"benchmark:{prefix}:read:cold:{index}")
            .ToArray();
        var writes = Enumerable.Range(0, 4)
            .ToDictionary(
                index => $"benchmark:{prefix}:write:{index}",
                index => CreatePayload($"benchmark:{prefix}:write-payload:{index}"),
                StringComparer.Ordinal);

        return new SyntheticStateTransaction(tieredReadKeys, cachedReadKeys, coldReadKeys, writes);
    }

    private static byte[] CreatePayload(string seed)
    {
        var text = seed.Length >= PayloadSize
            ? seed[..PayloadSize]
            : seed.PadRight(PayloadSize, '#');

        return Encoding.UTF8.GetBytes(text);
    }

    private sealed class FakeRedisDatabaseClient : IRedisDatabaseClient
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public Task<byte[]?> GetAsync(string key)
        {
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task<IReadOnlyDictionary<string, byte[]?>> GetAllAsync(IReadOnlyCollection<string> keys)
        {
            var results = new Dictionary<string, byte[]?>(keys.Count, StringComparer.Ordinal);

            foreach (var key in keys)
            {
                results[key] = _values.TryGetValue(key, out var value) ? value : null;
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
