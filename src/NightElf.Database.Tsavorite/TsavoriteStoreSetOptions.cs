using System.IO;

namespace NightElf.Database.Tsavorite;

public sealed class TsavoriteStoreSetOptions
{
    public string DataRootPath { get; set; } = Path.Combine("data", "tsavorite");

    public string CheckpointRootPath { get; set; } = Path.Combine("data", "tsavorite-checkpoints");

    public TsavoriteStoreProfile BlockStore { get; set; } = TsavoriteStoreProfile.CreateBlockStore();

    public TsavoriteStoreProfile StateStore { get; set; } = TsavoriteStoreProfile.CreateStateStore();

    public TsavoriteStoreProfile IndexStore { get; set; } = TsavoriteStoreProfile.CreateIndexStore();

    public TsavoriteStoreProfile GetProfile(TsavoriteStoreKind storeKind)
    {
        return storeKind switch
        {
            TsavoriteStoreKind.Block => BlockStore,
            TsavoriteStoreKind.State => StateStore,
            TsavoriteStoreKind.Index => IndexStore,
            _ => throw new ArgumentOutOfRangeException(nameof(storeKind), storeKind, "Unsupported Tsavorite store kind.")
        };
    }

    public TsavoriteDatabaseOptions<TContext> CreateDatabaseOptions<TContext>(TsavoriteStoreKind storeKind)
        where TContext : KeyValueDbContext<TContext>
    {
        var profile = GetProfile(storeKind);

        return new TsavoriteDatabaseOptions<TContext>
        {
            StoreKind = profile.StoreKind,
            DataPath = Path.Combine(DataRootPath, profile.StoreKind.ToDirectoryName(), typeof(TContext).Name),
            CheckpointPath = Path.Combine(CheckpointRootPath, profile.StoreKind.ToDirectoryName(), typeof(TContext).Name),
            IndexSize = profile.IndexSize,
            PageSize = profile.PageSize,
            SegmentSize = profile.SegmentSize,
            MemorySize = profile.MemorySize,
            RemoveOutdatedCheckpoints = profile.RemoveOutdatedCheckpoints,
            TryRecoverLatest = profile.TryRecoverLatest
        };
    }
}
