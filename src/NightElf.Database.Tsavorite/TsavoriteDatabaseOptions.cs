using System.IO;

namespace NightElf.Database.Tsavorite;

public sealed class TsavoriteDatabaseOptions<TContext>
    where TContext : KeyValueDbContext<TContext>
{
    public TsavoriteStoreKind StoreKind { get; set; } = TsavoriteStoreKind.State;

    public string? DataPath { get; set; }

    public string? CheckpointPath { get; set; }

    public long IndexSize { get; set; } = 1L << 20;

    public long PageSize { get; set; } = 1L << 12;

    public long SegmentSize { get; set; } = 1L << 20;

    public long MemorySize { get; set; } = 1L << 22;

    public bool RemoveOutdatedCheckpoints { get; set; } = true;

    public bool TryRecoverLatest { get; set; } = true;

    internal string ResolveDataPath()
    {
        return Path.GetFullPath(DataPath ?? Path.Combine("data", "tsavorite", StoreKind.ToDirectoryName(), typeof(TContext).Name));
    }

    internal string ResolveCheckpointPath()
    {
        return Path.GetFullPath(CheckpointPath ?? Path.Combine("data", "tsavorite-checkpoints", StoreKind.ToDirectoryName(), typeof(TContext).Name));
    }
}
