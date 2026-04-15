using System.Text.Json;

using NightElf.Database;

namespace NightElf.Kernel.Core;

public sealed class ChainStateStore : IChainStateStore
{
    public const string BestChainKey = "chain:best";
    public const string BestChainCheckpointKey = "chain:best:checkpoint";

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

        return DeserializeBlockReference(bytes);
    }

    public async Task SetBestChainAsync(BlockReference bestChain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bestChain);

        var bytes = SerializeBlockReference(bestChain);

        await Database.SetAsync(BestChainKey, bytes, cancellationToken).ConfigureAwait(false);
        await CheckpointStore.ApplyChangesAsync(
                new StateCommitVersion(bestChain.Height, bestChain.Hash),
                new Dictionary<string, byte[]>(1, StringComparer.Ordinal)
                {
                    [BestChainCheckpointKey] = bytes
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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

    public async Task RecoverToCheckpointAsync(
        StateCheckpointDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        await CheckpointStore.RecoverToCheckpointAsync(descriptor, cancellationToken).ConfigureAwait(false);
        var bestChainRecord = await CheckpointStore.GetVersionedStateAsync(BestChainCheckpointKey, cancellationToken)
            .ConfigureAwait(false);

        if (bestChainRecord is null || bestChainRecord.IsDeleted)
        {
            throw new InvalidOperationException(
                $"Checkpoint '{descriptor.Name}' at height {descriptor.BlockHeight} does not contain a recoverable best-chain marker.");
        }

        var recoveredBestChain = DeserializeBlockReference(bestChainRecord.Value);
        if (recoveredBestChain.Height != descriptor.BlockHeight || !string.Equals(recoveredBestChain.Hash, descriptor.BlockHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Checkpoint '{descriptor.Name}' at height {descriptor.BlockHeight} recovered best-chain marker '{recoveredBestChain.Height}:{recoveredBestChain.Hash}', indicating a checkpoint/token mismatch.");
        }

        var bestChain = new BlockReference(descriptor.BlockHeight, descriptor.BlockHash);
        await Database.SetAsync(BestChainKey, SerializeBlockReference(bestChain), cancellationToken).ConfigureAwait(false);
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

    private static byte[] SerializeBlockReference(BlockReference blockReference)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            blockReference,
            ChainStateJsonSerializerContext.Default.BlockReference);
    }

    private static BlockReference DeserializeBlockReference(byte[] value)
    {
        return JsonSerializer.Deserialize(
                   value,
                   ChainStateJsonSerializerContext.Default.BlockReference)
               ?? throw new InvalidOperationException("Failed to deserialize the best-chain pointer.");
    }
}
