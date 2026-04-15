using System.IO;

namespace NightElf.Database.Tsavorite;

public sealed class TsavoriteStoreSetOptions
{
    public string DataRootPath { get; set; } = Path.Combine("data", "tsavorite");

    public string CheckpointRootPath { get; set; } = Path.Combine("data", "tsavorite-checkpoints");

    public TsavoriteStoreProfile BlockStore { get; set; } = TsavoriteStoreProfile.CreateBlockStore();

    public TsavoriteStoreProfile StateStore { get; set; } = TsavoriteStoreProfile.CreateStateStore();

    public TsavoriteStoreProfile IndexStore { get; set; } = TsavoriteStoreProfile.CreateIndexStore();

    public void Validate()
    {
        ValidatePath(DataRootPath, nameof(DataRootPath));
        ValidatePath(CheckpointRootPath, nameof(CheckpointRootPath));
        ValidateProfile(BlockStore, TsavoriteStoreKind.Block, nameof(BlockStore));
        ValidateProfile(StateStore, TsavoriteStoreKind.State, nameof(StateStore));
        ValidateProfile(IndexStore, TsavoriteStoreKind.Index, nameof(IndexStore));
    }

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

    private static void ValidatePath(string path, string optionName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"NightElf:Database:Tsavorite:{optionName} must not be empty.");
        }
    }

    private static void ValidateProfile(
        TsavoriteStoreProfile profile,
        TsavoriteStoreKind expectedStoreKind,
        string optionName)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.StoreKind != expectedStoreKind)
        {
            throw new InvalidOperationException(
                $"NightElf:Database:Tsavorite:{optionName}:StoreKind must be {expectedStoreKind}.");
        }

        ValidatePositive(profile.IndexSize, optionName, nameof(profile.IndexSize));
        ValidatePositive(profile.PageSize, optionName, nameof(profile.PageSize));
        ValidatePositive(profile.SegmentSize, optionName, nameof(profile.SegmentSize));
        ValidatePositive(profile.MemorySize, optionName, nameof(profile.MemorySize));
    }

    private static void ValidatePositive(long value, string optionName, string propertyName)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException(
                $"NightElf:Database:Tsavorite:{optionName}:{propertyName} must be greater than zero.");
        }
    }
}
