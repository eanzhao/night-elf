namespace NightElf.Sdk.CSharp;

public readonly record struct Empty : IContractCodec<Empty>
{
    public static Empty Value => default;

    public static Empty Decode(ReadOnlySpan<byte> input)
    {
        if (!input.IsEmpty)
        {
            throw new FormatException("Expected an empty payload.");
        }

        return default;
    }

    public static byte[] Encode(Empty value)
    {
        return [];
    }
}
