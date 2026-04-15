namespace NightElf.Database;

public interface IStateCheckpointStore<TContext>
    where TContext : KeyValueDbContext<TContext>
{
    Task ApplyChangesAsync(
        StateCommitVersion version,
        IReadOnlyDictionary<string, byte[]> writes,
        IReadOnlyCollection<string>? deletes = null,
        CancellationToken cancellationToken = default);

    Task<VersionedStateRecord?> GetVersionedStateAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task<StateCheckpointDescriptor> AdvanceCheckpointAsync(
        StateCommitVersion version,
        CancellationToken cancellationToken = default);

    Task RecoverToCheckpointAsync(
        StateCheckpointDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StateCheckpointDescriptor>> GetCheckpointsAsync(
        CancellationToken cancellationToken = default);
}
