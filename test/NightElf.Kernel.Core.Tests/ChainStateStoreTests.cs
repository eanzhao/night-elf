using NightElf.Database.Tsavorite;
using NightElf.Kernel.Core;

namespace NightElf.Kernel.Core.Tests;

public sealed class ChainStateStoreTests
{
    [Fact]
    public async Task ChainStateStore_Should_Recover_BestChain_And_State_To_The_Latest_Lib_Checkpoint()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var database = CreateDatabase(rootPath);
            var checkpointStore = new TsavoriteStateCheckpointStore<ChainStateDbContext>(database);
            var chainStateStore = new ChainStateStore(database, checkpointStore);

            var block10 = new BlockReference(10, "block-010");
            await chainStateStore.SetBestChainAsync(block10);
            await chainStateStore.ApplyChangesAsync(
                block10,
                new Dictionary<string, byte[]>
                {
                    ["state:key"] = [1]
                });

            var checkpoint = await chainStateStore.AdvanceLibCheckpointAsync(block10);

            var block11 = new BlockReference(11, "block-011");
            await chainStateStore.SetBestChainAsync(block11);
            await chainStateStore.ApplyChangesAsync(
                block11,
                new Dictionary<string, byte[]>
                {
                    ["state:key"] = [9]
                });

            await chainStateStore.RecoverToLatestLibCheckpointAsync();

            var bestChain = await chainStateStore.GetBestChainAsync();
            var recoveredState = await checkpointStore.GetVersionedStateAsync("state:key");
            var checkpoints = await chainStateStore.GetLibCheckpointsAsync();

            Assert.Equal(block10, bestChain);
            Assert.NotNull(recoveredState);
            Assert.Equal(block10.Height, recoveredState.BlockHeight);
            Assert.Equal(block10.Hash, recoveredState.BlockHash);
            Assert.Equal(new byte[] { 1 }, recoveredState.Value);
            Assert.Single(checkpoints);
            Assert.Equal(checkpoint.HybridLogCheckpointToken, checkpoints[0].HybridLogCheckpointToken);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public async Task RecoverToLatestLibCheckpoint_Should_Throw_When_No_Checkpoint_Exists()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var database = CreateDatabase(rootPath);
            var checkpointStore = new TsavoriteStateCheckpointStore<ChainStateDbContext>(database);
            var chainStateStore = new ChainStateStore(database, checkpointStore);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                chainStateStore.RecoverToLatestLibCheckpointAsync());

            Assert.Contains("No LIB checkpoint", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    private static TsavoriteDatabase<ChainStateDbContext> CreateDatabase(string rootPath)
    {
        return new TsavoriteDatabase<ChainStateDbContext>(new TsavoriteDatabaseOptions<ChainStateDbContext>
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

    private static string CreateRootPath()
    {
        return Path.Combine(Path.GetTempPath(), "nightelf-kernel-core-tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteRootPath(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
