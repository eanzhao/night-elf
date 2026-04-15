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

    private static TsavoriteDatabase<TestDbContext> CreateDatabase(string rootPath)
    {
        return new TsavoriteDatabase<TestDbContext>(new TsavoriteDatabaseOptions<TestDbContext>
        {
            DataPath = Path.Combine(rootPath, "data"),
            CheckpointPath = Path.Combine(rootPath, "checkpoints"),
            IndexSize = 1L << 16,
            PageSize = 1L << 12,
            SegmentSize = 1L << 18,
            MemorySize = 1L << 20
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
