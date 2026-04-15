namespace NightElf.Database.Tsavorite;

public sealed class TsavoriteStoreProfile
{
    public required TsavoriteStoreKind StoreKind { get; init; }

    public required long IndexSize { get; init; }

    public required long PageSize { get; init; }

    public required long SegmentSize { get; init; }

    public required long MemorySize { get; init; }

    public bool RemoveOutdatedCheckpoints { get; init; } = true;

    public bool TryRecoverLatest { get; init; } = true;

    public static TsavoriteStoreProfile CreateBlockStore()
    {
        return new TsavoriteStoreProfile
        {
            StoreKind = TsavoriteStoreKind.Block,
            IndexSize = 1L << 20,
            PageSize = 1L << 20,
            SegmentSize = 1L << 30,
            MemorySize = 1L << 28
        };
    }

    public static TsavoriteStoreProfile CreateStateStore()
    {
        return new TsavoriteStoreProfile
        {
            StoreKind = TsavoriteStoreKind.State,
            IndexSize = 1L << 20,
            PageSize = 1L << 18,
            SegmentSize = 1L << 29,
            MemorySize = 1L << 30
        };
    }

    public static TsavoriteStoreProfile CreateIndexStore()
    {
        return new TsavoriteStoreProfile
        {
            StoreKind = TsavoriteStoreKind.Index,
            IndexSize = 1L << 20,
            PageSize = 1L << 19,
            SegmentSize = 1L << 28,
            MemorySize = 1L << 27
        };
    }
}
