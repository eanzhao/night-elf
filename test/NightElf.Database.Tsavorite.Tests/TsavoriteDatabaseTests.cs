using NightElf.Database.Tsavorite;

namespace NightElf.Database.Tsavorite.Tests;

public sealed class TsavoriteDatabaseTests
{
    [Fact]
    public async Task SingleCrud_Should_RoundTrip()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var database = CreateDatabase(rootPath);

            Assert.Null(await database.GetAsync("missing"));
            Assert.False(await database.ExistsAsync("missing"));

            var original = new byte[] { 1, 2, 3, 4 };
            await database.SetAsync("alpha", original);

            Assert.True(await database.ExistsAsync("alpha"));
            Assert.Equal(original, await database.GetAsync("alpha"));

            var updated = Array.Empty<byte>();
            await database.SetAsync("alpha", updated);

            Assert.Equal(updated, await database.GetAsync("alpha"));

            await database.DeleteAsync("alpha");

            Assert.False(await database.ExistsAsync("alpha"));
            Assert.Null(await database.GetAsync("alpha"));
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public async Task BatchOperations_Should_RoundTrip()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var database = CreateDatabase(rootPath);

            var values = new Dictionary<string, byte[]>
            {
                ["a"] = new byte[] { 1 },
                ["b"] = new byte[] { 2, 3 },
                ["c"] = new byte[] { 4, 5, 6 }
            };

            await database.SetAllAsync(values);

            var loaded = await database.GetAllAsync(new[] { "a", "b", "c", "missing" });

            Assert.Equal(values["a"], loaded["a"]);
            Assert.Equal(values["b"], loaded["b"]);
            Assert.Equal(values["c"], loaded["c"]);
            Assert.Null(loaded["missing"]);

            await database.DeleteAllAsync(new[] { "a", "c" });

            Assert.Null(await database.GetAsync("a"));
            Assert.Equal(values["b"], await database.GetAsync("b"));
            Assert.Null(await database.GetAsync("c"));
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public async Task StoreSet_Should_Isolate_Block_State_And_Index_Stores()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var storeSet = CreateStoreSet(rootPath);
            using var blockStore = storeSet.CreateBlockStore<TestDbContext>();
            using var stateStore = storeSet.CreateStateStore<TestDbContext>();
            using var indexStore = storeSet.CreateIndexStore<TestDbContext>();

            await blockStore.SetAsync("key", new byte[] { 1 });
            await stateStore.SetAsync("key", new byte[] { 2 });
            await indexStore.SetAsync("key", new byte[] { 3 });

            Assert.Equal(TsavoriteStoreKind.Block, blockStore.StoreKind);
            Assert.Equal(TsavoriteStoreKind.State, stateStore.StoreKind);
            Assert.Equal(TsavoriteStoreKind.Index, indexStore.StoreKind);

            Assert.Equal(new byte[] { 1 }, await blockStore.GetAsync("key"));
            Assert.Equal(new byte[] { 2 }, await stateStore.GetAsync("key"));
            Assert.Equal(new byte[] { 3 }, await indexStore.GetAsync("key"));

            Assert.NotEqual(blockStore.DataPath, stateStore.DataPath);
            Assert.NotEqual(blockStore.DataPath, indexStore.DataPath);
            Assert.NotEqual(stateStore.DataPath, indexStore.DataPath);

            Assert.NotEqual(blockStore.CheckpointPath, stateStore.CheckpointPath);
            Assert.NotEqual(blockStore.CheckpointPath, indexStore.CheckpointPath);
            Assert.NotEqual(stateStore.CheckpointPath, indexStore.CheckpointPath);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public void StoreProfiles_Should_Expose_Distinct_Tuning_Directions()
    {
        var options = new TsavoriteStoreSetOptions();

        Assert.True(options.BlockStore.SegmentSize > options.IndexStore.SegmentSize);
        Assert.True(options.StateStore.MemorySize > options.BlockStore.MemorySize);
        Assert.True(options.StateStore.PageSize < options.BlockStore.PageSize);
        Assert.NotEqual(options.BlockStore.StoreKind, options.StateStore.StoreKind);
        Assert.NotEqual(options.StateStore.StoreKind, options.IndexStore.StoreKind);
    }

    [Fact]
    public async Task DisposingStoreSet_Should_Dispose_OwnedStores()
    {
        var rootPath = CreateRootPath();
        TsavoriteDatabase<TestDbContext>? stateStore = null;

        try
        {
            using (var storeSet = CreateStoreSet(rootPath))
            {
                stateStore = storeSet.CreateStateStore<TestDbContext>();
                await stateStore.SetAsync("key", new byte[] { 9 });
            }

            await Assert.ThrowsAsync<ObjectDisposedException>(() => stateStore!.GetAsync("key"));
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    private static TsavoriteDatabase<TestDbContext> CreateDatabase(string rootPath)
    {
        return new TsavoriteDatabase<TestDbContext>(new TsavoriteDatabaseOptions<TestDbContext>
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

    private static TsavoriteStoreSet CreateStoreSet(string rootPath)
    {
        return new TsavoriteStoreSet(new TsavoriteStoreSetOptions
        {
            DataRootPath = Path.Combine(rootPath, "data"),
            CheckpointRootPath = Path.Combine(rootPath, "checkpoints"),
            BlockStore = new TsavoriteStoreProfile
            {
                StoreKind = TsavoriteStoreKind.Block,
                IndexSize = 1L << 16,
                PageSize = 1L << 12,
                SegmentSize = 1L << 18,
                MemorySize = 1L << 20
            },
            StateStore = new TsavoriteStoreProfile
            {
                StoreKind = TsavoriteStoreKind.State,
                IndexSize = 1L << 16,
                PageSize = 1L << 11,
                SegmentSize = 1L << 18,
                MemorySize = 1L << 21
            },
            IndexStore = new TsavoriteStoreProfile
            {
                StoreKind = TsavoriteStoreKind.Index,
                IndexSize = 1L << 16,
                PageSize = 1L << 12,
                SegmentSize = 1L << 17,
                MemorySize = 1L << 19
            }
        });
    }

    private static string CreateRootPath()
    {
        return Path.Combine(Path.GetTempPath(), "nightelf-tsavorite-tests", Guid.NewGuid().ToString("N"));
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
}
