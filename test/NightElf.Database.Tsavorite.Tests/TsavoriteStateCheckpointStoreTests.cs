using NightElf.Database.Tsavorite;

namespace NightElf.Database.Tsavorite.Tests;

public sealed class TsavoriteStateCheckpointStoreTests
{
    [Fact]
    public async Task ApplyChanges_Should_Persist_Versioned_Records_And_Tombstones()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var database = CreateDatabase(rootPath);
            var checkpointStore = new TsavoriteStateCheckpointStore<TestDbContext>(database);
            var version = new StateCommitVersion(101, "block-101");

            await checkpointStore.ApplyChangesAsync(
                version,
                new Dictionary<string, byte[]>
                {
                    ["state:alpha"] = [1, 2, 3]
                },
                ["state:beta"]);

            var writtenRecord = await checkpointStore.GetVersionedStateAsync("state:alpha");
            var tombstoneRecord = await checkpointStore.GetVersionedStateAsync("state:beta");

            Assert.NotNull(writtenRecord);
            Assert.Equal(version.BlockHeight, writtenRecord.BlockHeight);
            Assert.Equal(version.BlockHash, writtenRecord.BlockHash);
            Assert.False(writtenRecord.IsDeleted);
            Assert.Equal(new byte[] { 1, 2, 3 }, writtenRecord.Value);

            Assert.NotNull(tombstoneRecord);
            Assert.Equal(version.BlockHeight, tombstoneRecord.BlockHeight);
            Assert.Equal(version.BlockHash, tombstoneRecord.BlockHash);
            Assert.True(tombstoneRecord.IsDeleted);
            Assert.Empty(tombstoneRecord.Value);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public async Task Checkpoint_Should_Recover_State_And_Trim_To_The_Latest_Lib_Snapshot()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var database = CreateDatabase(rootPath);
            var checkpointStore = new TsavoriteStateCheckpointStore<TestDbContext>(
                database,
                new TsavoriteStateCheckpointStoreOptions
                {
                    CheckpointNamePrefix = "lib",
                    RetainedCheckpointCount = 1
                });

            var version1 = new StateCommitVersion(10, "block-010");
            await checkpointStore.ApplyChangesAsync(
                version1,
                new Dictionary<string, byte[]>
                {
                    ["state:key"] = [1]
                });

            var checkpoint1 = await checkpointStore.AdvanceCheckpointAsync(version1);

            await checkpointStore.ApplyChangesAsync(
                new StateCommitVersion(11, "block-011"),
                new Dictionary<string, byte[]>
                {
                    ["state:key"] = [9]
                });

            await checkpointStore.RecoverToCheckpointAsync(checkpoint1);

            var recoveredRecord = await checkpointStore.GetVersionedStateAsync("state:key");
            Assert.NotNull(recoveredRecord);
            Assert.Equal(version1.BlockHeight, recoveredRecord.BlockHeight);
            Assert.Equal(version1.BlockHash, recoveredRecord.BlockHash);
            Assert.Equal(new byte[] { 1 }, recoveredRecord.Value);

            await checkpointStore.ApplyChangesAsync(
                new StateCommitVersion(12, "block-012"),
                new Dictionary<string, byte[]>
                {
                    ["state:key"] = [2]
                });

            var checkpoint2 = await checkpointStore.AdvanceCheckpointAsync(new StateCommitVersion(12, "block-012"));
            var checkpoints = await checkpointStore.GetCheckpointsAsync();
            var latestCheckpoint = Assert.Single(checkpoints);

            Assert.Equal(checkpoint2.HybridLogCheckpointToken, latestCheckpoint.HybridLogCheckpointToken);
            Assert.StartsWith("lib-00000000000000000012-", latestCheckpoint.Name);
            Assert.True(latestCheckpoint.BlockHeight >= checkpoint1.BlockHeight);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public void MultiRetention_Should_Require_Tsavorite_AutoCleanup_To_Be_Disabled()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var database = CreateDatabase(rootPath, removeOutdatedCheckpoints: true);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                new TsavoriteStateCheckpointStore<TestDbContext>(
                    database,
                    new TsavoriteStateCheckpointStoreOptions
                    {
                        RetainedCheckpointCount = 2
                    }));

            Assert.Contains("RemoveOutdatedCheckpoints", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public async Task GetCheckpoints_Should_Throw_With_MetadataPath_When_Catalog_Is_Corrupted()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var database = CreateDatabase(rootPath, removeOutdatedCheckpoints: false);
            var checkpointStore = new TsavoriteStateCheckpointStore<TestDbContext>(
                database,
                new TsavoriteStateCheckpointStoreOptions
                {
                    RetainedCheckpointCount = 2
                });

            await File.WriteAllTextAsync(checkpointStore.MetadataPath, "{ not-valid-json");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                checkpointStore.GetCheckpointsAsync());

            Assert.Contains(checkpointStore.MetadataPath, exception.Message, StringComparison.Ordinal);
            Assert.NotNull(exception.InnerException);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public async Task RecoverToCheckpoint_Should_Throw_With_Checkpoint_Context_When_Tokens_Are_Invalid()
    {
        var rootPath = CreateRootPath();

        try
        {
            using var database = CreateDatabase(rootPath);
            var checkpointStore = new TsavoriteStateCheckpointStore<TestDbContext>(database);
            var version = new StateCommitVersion(21, "block-021");

            await checkpointStore.ApplyChangesAsync(
                version,
                new Dictionary<string, byte[]>
                {
                    ["state:key"] = [2, 1]
                });

            var checkpoint = await checkpointStore.AdvanceCheckpointAsync(version);
            var invalidCheckpoint = CloneCheckpoint(
                checkpoint,
                hybridLogCheckpointToken: Guid.NewGuid());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                checkpointStore.RecoverToCheckpointAsync(invalidCheckpoint));

            Assert.Contains(checkpoint.Name, exception.Message, StringComparison.Ordinal);
            Assert.Contains(checkpoint.BlockHeight.ToString(), exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    private static TsavoriteDatabase<TestDbContext> CreateDatabase(
        string rootPath,
        bool removeOutdatedCheckpoints = true)
    {
        return new TsavoriteDatabase<TestDbContext>(new TsavoriteDatabaseOptions<TestDbContext>
        {
            StoreKind = TsavoriteStoreKind.State,
            DataPath = Path.Combine(rootPath, "data"),
            CheckpointPath = Path.Combine(rootPath, "checkpoints"),
            IndexSize = 1L << 16,
            PageSize = 1L << 12,
            SegmentSize = 1L << 18,
            MemorySize = 1L << 20,
            RemoveOutdatedCheckpoints = removeOutdatedCheckpoints
        });
    }

    private static string CreateRootPath()
    {
        return Path.Combine(Path.GetTempPath(), "nightelf-tsavorite-checkpoint-tests", Guid.NewGuid().ToString("N"));
    }

    private static StateCheckpointDescriptor CloneCheckpoint(
        StateCheckpointDescriptor descriptor,
        Guid? hybridLogCheckpointToken = null,
        Guid? indexCheckpointToken = null)
    {
        return new StateCheckpointDescriptor
        {
            Name = descriptor.Name,
            BlockHeight = descriptor.BlockHeight,
            BlockHash = descriptor.BlockHash,
            StoreVersion = descriptor.StoreVersion,
            HybridLogCheckpointToken = hybridLogCheckpointToken ?? descriptor.HybridLogCheckpointToken,
            IndexCheckpointToken = indexCheckpointToken ?? descriptor.IndexCheckpointToken,
            IsIncremental = descriptor.IsIncremental,
            CreatedAtUtc = descriptor.CreatedAtUtc
        };
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
