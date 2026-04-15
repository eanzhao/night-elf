using System.Text.Json;

using NightElf.Database;

namespace NightElf.Kernel.Core;

public sealed class ChainStateStore : IChainStateStore
{
    public const string BestChainKey = "chain:best";

    public ChainStateStore(
        IKeyValueDatabase<ChainStateDbContext> database,
        IStateCheckpointStore<ChainStateDbContext> checkpointStore)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
        CheckpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
    }

    public IKeyValueDatabase<ChainStateDbContext> Database { get; }

    public IStateCheckpointStore<ChainStateDbContext> CheckpointStore { get; }

    public async Task<BlockReference?> GetBestChainAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await Database.GetAsync(BestChainKey, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize(
                   bytes,
                   ChainStateJsonSerializerContext.Default.BlockReference)
               ?? throw new InvalidOperationException("Failed to deserialize the best-chain pointer.");
    }

    public Task SetBestChainAsync(BlockReference bestChain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bestChain);

        return Database.SetAsync(
            BestChainKey,
            JsonSerializer.SerializeToUtf8Bytes(
                bestChain,
                ChainStateJsonSerializerContext.Default.BlockReference),
            cancellationToken);
    }

    public Task ApplyChangesAsync(
        BlockReference block,
        IReadOnlyDictionary<string, byte[]> writes,
        IReadOnlyCollection<string>? deletes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(block);

        return CheckpointStore.ApplyChangesAsync(
            new StateCommitVersion(block.Height, block.Hash),
            writes,
            deletes,
            cancellationToken);
    }

    public Task<StateCheckpointDescriptor> AdvanceLibCheckpointAsync(
        BlockReference libBlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(libBlock);

        return CheckpointStore.AdvanceCheckpointAsync(
            new StateCommitVersion(libBlock.Height, libBlock.Hash),
            cancellationToken);
    }

    public Task RecoverToCheckpointAsync(
        StateCheckpointDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return CheckpointStore.RecoverToCheckpointAsync(descriptor, cancellationToken);
    }

    public async Task RecoverToLatestLibCheckpointAsync(CancellationToken cancellationToken = default)
    {
        var checkpoints = await GetLibCheckpointsAsync(cancellationToken).ConfigureAwait(false);
        var latestCheckpoint = checkpoints
            .OrderByDescending(checkpoint => checkpoint.BlockHeight)
            .ThenByDescending(checkpoint => checkpoint.CreatedAtUtc)
            .FirstOrDefault();

        if (latestCheckpoint is null)
        {
            throw new InvalidOperationException("No LIB checkpoint is available for recovery.");
        }

        await RecoverToCheckpointAsync(latestCheckpoint, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<StateCheckpointDescriptor>> GetLibCheckpointsAsync(
        CancellationToken cancellationToken = default)
    {
        return CheckpointStore.GetCheckpointsAsync(cancellationToken);
    }
}
