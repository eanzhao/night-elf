namespace NightElf.Sdk.CSharp;

public sealed class ContractResourceSet
{
    public static ContractResourceSet Empty { get; } = new([], []);

    public ContractResourceSet(IReadOnlyList<string> readKeys, IReadOnlyList<string> writeKeys)
    {
        ReadKeys = readKeys;
        WriteKeys = writeKeys;
    }

    public IReadOnlyList<string> ReadKeys { get; }

    public IReadOnlyList<string> WriteKeys { get; }

    public static ContractResourceSet Create(IEnumerable<string>? readKeys, IEnumerable<string>? writeKeys)
    {
        return new ContractResourceSet(Normalize(readKeys), Normalize(writeKeys));
    }

    private static string[] Normalize(IEnumerable<string>? keys)
    {
        if (keys is null)
        {
            return [];
        }

        HashSet<string>? seen = null;
        List<string>? normalized = null;

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(key))
            {
                continue;
            }

            normalized ??= [];
            normalized.Add(key);
        }

        return normalized?.ToArray() ?? [];
    }
}
