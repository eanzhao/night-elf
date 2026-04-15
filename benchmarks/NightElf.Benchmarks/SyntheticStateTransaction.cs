namespace NightElf.Benchmarks;

internal sealed record SyntheticStateTransaction(
    IReadOnlyList<string> TieredReadKeys,
    IReadOnlyList<string> CachedReadKeys,
    IReadOnlyList<string> ColdReadKeys,
    IReadOnlyDictionary<string, byte[]> Writes)
{
    public static SyntheticStateTransaction Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        new Dictionary<string, byte[]>());

    public IEnumerable<KeyValuePair<string, byte[]>> GetSeedValues()
    {
        foreach (var key in TieredReadKeys)
        {
            yield return KeyValuePair.Create(key, CreateSeedPayload($"{key}:seed"));
        }

        foreach (var key in CachedReadKeys)
        {
            yield return KeyValuePair.Create(key, CreateSeedPayload($"{key}:seed"));
        }

        foreach (var key in ColdReadKeys)
        {
            yield return KeyValuePair.Create(key, CreateSeedPayload($"{key}:seed"));
        }

        foreach (var pair in Writes)
        {
            yield return pair;
        }
    }

    private static byte[] CreateSeedPayload(string text)
    {
        return System.Text.Encoding.UTF8.GetBytes(text.PadRight(96, '!'));
    }
}
