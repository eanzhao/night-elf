using NightElf.Database;

namespace NightElf.Kernel.Core;

public interface IChainStateStore
{
    IKeyValueDatabase<ChainStateDbContext> Database { get; }

    IStateCheckpointStore<ChainStateDbContext> CheckpointStore { get; }

    Task<BlockReference?> GetBestChainAsync(CancellationToken cancellationToken = default);

    Task SetBestChainAsync(BlockReference bestChain, CancellationToken cancellationToken = default);

    Task ApplyChangesAsync(
        BlockReference block,
        IReadOnlyDictionary<string, byte[]> writes,
        IReadOnlyCollection<string>? deletes = null,
        CancellationToken cancellationToken = default);

    Task<StateCheckpointDescriptor> AdvanceLibCheckpointAsync(
        BlockReference libBlock,
        CancellationToken cancellationToken = default);

    Task RecoverToCheckpointAsync(
        StateCheckpointDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task RecoverToLatestLibCheckpointAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StateCheckpointDescriptor>> GetLibCheckpointsAsync(
        CancellationToken cancellationToken = default);
}
