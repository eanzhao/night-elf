using System.Text;

using NightElf.Database;
using NightElf.Database.Tsavorite;

namespace NightElf.Kernel.Core.Tests;

internal sealed class ChainStateRecoveryHarness : IDisposable
{
    private static readonly Encoding TextEncoding = Encoding.UTF8;

    public const string BalanceKey = "state:balance:alice";
    public const string StateRootKey = "state:root";
    public const string PrimaryIndexKey = "index:tx:alpha";
    public const string SecondaryIndexKey = "index:tx:stale";

    private readonly string _rootPath;

    public ChainStateRecoveryHarness(
        int retainedCheckpointCount = 1,
        bool removeOutdatedCheckpoints = true)
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "nightelf-chainstate-recovery-tests", Guid.NewGuid().ToString("N"));
        Database = new TsavoriteDatabase<ChainStateDbContext>(new TsavoriteDatabaseOptions<ChainStateDbContext>
        {
            StoreKind = TsavoriteStoreKind.State,
            DataPath = Path.Combine(_rootPath, "data"),
            CheckpointPath = Path.Combine(_rootPath, "checkpoints"),
            IndexSize = 1L << 16,
            PageSize = 1L << 12,
            SegmentSize = 1L << 18,
            MemorySize = 1L << 20,
            RemoveOutdatedCheckpoints = removeOutdatedCheckpoints
        });

        CheckpointStore = new TsavoriteStateCheckpointStore<ChainStateDbContext>(
            Database,
            new TsavoriteStateCheckpointStoreOptions
            {
                CheckpointNamePrefix = "lib",
                RetainedCheckpointCount = retainedCheckpointCount
            });
        ChainStateStore = new ChainStateStore(Database, CheckpointStore);
    }

    public TsavoriteDatabase<ChainStateDbContext> Database { get; }

    public TsavoriteStateCheckpointStore<ChainStateDbContext> CheckpointStore { get; }

    public ChainStateStore ChainStateStore { get; }

    public async Task<BlockReference> ApplyBlockAsync(
        long height,
        string blockHash,
        string balance,
        string stateRoot,
        string primaryIndex,
        string? secondaryIndex = null,
        bool deleteSecondaryIndex = false)
    {
        var block = new BlockReference(height, blockHash);
        var writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [BalanceKey] = Encode(balance),
            [StateRootKey] = Encode(stateRoot),
            [PrimaryIndexKey] = Encode(primaryIndex)
        };

        List<string>? deletes = null;
        if (secondaryIndex is not null)
        {
            writes[SecondaryIndexKey] = Encode(secondaryIndex);
        }
        else if (deleteSecondaryIndex)
        {
            deletes = [SecondaryIndexKey];
        }

        await ChainStateStore.SetBestChainAsync(block).ConfigureAwait(false);
        await ChainStateStore.ApplyChangesAsync(block, writes, deletes).ConfigureAwait(false);
        return block;
    }

    public Task<StateCheckpointDescriptor> AdvanceLibAsync(BlockReference block)
    {
        return ChainStateStore.AdvanceLibCheckpointAsync(block);
    }

    public async Task AssertConsistentSnapshotAsync(
        BlockReference expectedBestChain,
        string expectedBalance,
        string expectedStateRoot,
        string expectedPrimaryIndex,
        string? expectedSecondaryIndex,
        bool secondaryIndexDeleted = false)
    {
        var bestChain = await ChainStateStore.GetBestChainAsync().ConfigureAwait(false);
        var balanceRecord = await CheckpointStore.GetVersionedStateAsync(BalanceKey).ConfigureAwait(false);
        var stateRootRecord = await CheckpointStore.GetVersionedStateAsync(StateRootKey).ConfigureAwait(false);
        var primaryIndexRecord = await CheckpointStore.GetVersionedStateAsync(PrimaryIndexKey).ConfigureAwait(false);
        var secondaryIndexRecord = await CheckpointStore.GetVersionedStateAsync(SecondaryIndexKey).ConfigureAwait(false);

        Assert.Equal(expectedBestChain, bestChain);
        AssertVersionedRecord(balanceRecord, expectedBestChain, expectedBalance, isDeleted: false);
        AssertVersionedRecord(stateRootRecord, expectedBestChain, expectedStateRoot, isDeleted: false);
        AssertVersionedRecord(primaryIndexRecord, expectedBestChain, expectedPrimaryIndex, isDeleted: false);

        if (secondaryIndexDeleted)
        {
            AssertVersionedRecord(secondaryIndexRecord, expectedBestChain, string.Empty, isDeleted: true);
        }
        else
        {
            AssertVersionedRecord(secondaryIndexRecord, expectedBestChain, expectedSecondaryIndex, isDeleted: false);
        }
    }

    public void Dispose()
    {
        Database.Dispose();

        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private static void AssertVersionedRecord(
        VersionedStateRecord? record,
        BlockReference expectedBlock,
        string? expectedValue,
        bool isDeleted)
    {
        Assert.NotNull(record);
        Assert.Equal(expectedBlock.Height, record.BlockHeight);
        Assert.Equal(expectedBlock.Hash, record.BlockHash);
        Assert.Equal(isDeleted, record.IsDeleted);
        Assert.Equal(expectedValue, Decode(record.Value));
    }

    private static byte[] Encode(string value)
    {
        return TextEncoding.GetBytes(value);
    }

    private static string Decode(byte[] value)
    {
        return TextEncoding.GetString(value);
    }
}
