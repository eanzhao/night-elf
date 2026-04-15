namespace NightElf.Database.Tsavorite;

internal static class TsavoriteStoreKindExtensions
{
    public static string ToDirectoryName(this TsavoriteStoreKind storeKind)
    {
        return storeKind switch
        {
            TsavoriteStoreKind.Block => "block-store",
            TsavoriteStoreKind.State => "state-store",
            TsavoriteStoreKind.Index => "index-store",
            _ => throw new ArgumentOutOfRangeException(nameof(storeKind), storeKind, "Unsupported Tsavorite store kind.")
        };
    }
}
