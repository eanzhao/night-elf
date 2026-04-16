using Google.Protobuf;

using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core;

public static class KernelCoreProtobufCompatibilityExtensions
{
    public static string ToHex(this Address address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Convert.ToHexString(address.Value.Span);
    }

    public static string ToHex(this Hash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return Convert.ToHexString(hash.Value.Span);
    }

    public static Hash ToProtoHash(this string hashHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashHex);

        return new Hash
        {
            Value = ByteString.CopyFrom(Convert.FromHexString(hashHex))
        };
    }

    public static Address ToProtoAddress(this string addressHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressHex);

        return new Address
        {
            Value = ByteString.CopyFrom(Convert.FromHexString(addressHex))
        };
    }

    public static BlockReference ToBlockReference(this BlockHeader header, Hash blockHash)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(blockHash);

        return new BlockReference(header.Height, blockHash.ToHex());
    }

    public static BlockReference ToBlockReference(this Block block, Hash blockHash)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(block.Header);

        return block.Header.ToBlockReference(blockHash);
    }
}
