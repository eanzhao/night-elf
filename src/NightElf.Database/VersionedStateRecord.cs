using System.Text.Json;

namespace NightElf.Database;

public sealed class VersionedStateRecord
{
    public required long BlockHeight { get; init; }

    public required string BlockHash { get; init; }

    public required byte[] Value { get; init; }

    public bool IsDeleted { get; init; }

    public DateTimeOffset PersistedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static VersionedStateRecord Create(StateCommitVersion version, byte[] value, bool isDeleted)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new VersionedStateRecord
        {
            BlockHeight = version.BlockHeight,
            BlockHash = version.BlockHash,
            Value = value.ToArray(),
            IsDeleted = isDeleted,
            PersistedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            this,
            VersionedStateRecordJsonSerializerContext.Default.VersionedStateRecord);
    }

    public static VersionedStateRecord Deserialize(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var record = JsonSerializer.Deserialize(
            value,
            VersionedStateRecordJsonSerializerContext.Default.VersionedStateRecord);
        if (record is null)
        {
            throw new InvalidOperationException("Failed to deserialize VersionedStateRecord.");
        }

        return record;
    }
}
