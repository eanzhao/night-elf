namespace NightElf.Database;

public sealed class StateCheckpointDescriptor
{
    public required string Name { get; init; }

    public required long BlockHeight { get; init; }

    public required string BlockHash { get; init; }

    public required long StoreVersion { get; init; }

    public required Guid HybridLogCheckpointToken { get; init; }

    public required Guid IndexCheckpointToken { get; init; }

    public required bool IsIncremental { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}
