using System.IO;

namespace NightElf.Database.Tsavorite;

public sealed class TsavoriteDatabaseOptions<TContext>
    where TContext : KeyValueDbContext<TContext>
{
    public string DataPath { get; set; } = Path.Combine("data", "tsavorite", typeof(TContext).Name);

    public string CheckpointPath { get; set; } = Path.Combine("data", "tsavorite-checkpoints", typeof(TContext).Name);

    public long IndexSize { get; set; } = 1L << 20;

    public long PageSize { get; set; } = 1L << 12;

    public long SegmentSize { get; set; } = 1L << 20;

    public long MemorySize { get; set; } = 1L << 22;

    public bool RemoveOutdatedCheckpoints { get; set; } = true;

    public bool TryRecoverLatest { get; set; } = true;
}
