using Google.Protobuf;

using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.Core.Protobuf;

public sealed partial class Address : IContractCodec<Address>
{
    public static Address Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(Address value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class Hash : IContractCodec<Hash>
{
    public static Hash Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(Hash value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}
