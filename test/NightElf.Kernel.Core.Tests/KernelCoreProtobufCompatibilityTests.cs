using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core.Tests;

public sealed class KernelCoreProtobufCompatibilityTests
{
    [Fact]
    public void Transaction_Should_RoundTrip_With_AElf_Compatible_Field_Shape()
    {
        var transaction = new Transaction
        {
            From = CreateAddress(0x01, 0x02, 0x03),
            To = CreateAddress(0x0A, 0x0B, 0x0C),
            RefBlockNumber = 1024,
            RefBlockPrefix = ByteString.CopyFrom([0xDE, 0xAD, 0xBE, 0xEF]),
            MethodName = "Transfer",
            Params = ByteString.CopyFromUtf8("payload"),
            Signature = ByteString.CopyFrom([0xAA, 0xBB, 0xCC])
        };

        var payload = transaction.ToByteArray();
        var roundTrip = Transaction.Parser.ParseFrom(payload);

        Assert.Equal(1024, roundTrip.RefBlockNumber);
        Assert.Equal("Transfer", roundTrip.MethodName);
        Assert.Equal("payload", roundTrip.Params.ToStringUtf8());
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], roundTrip.RefBlockPrefix.ToByteArray());
        Assert.Equal([0x01, 0x02, 0x03], roundTrip.From.Value.ToByteArray());
        Assert.Equal([0x0A, 0x0B, 0x0C], roundTrip.To.Value.ToByteArray());
    }

    [Fact]
    public void Block_Should_RoundTrip_And_Convert_To_BlockReference()
    {
        var blockHash = "AABBCCDD";
        var block = new Block
        {
            Header = new BlockHeader
            {
                Version = 1,
                ChainId = 9992731,
                PreviousBlockHash = CreateHash(0x01, 0x02),
                MerkleTreeRootOfTransactions = CreateHash(0x03, 0x04),
                MerkleTreeRootOfWorldState = CreateHash(0x05, 0x06),
                Bloom = ByteString.CopyFrom([0x11, 0x22]),
                Height = 42,
                Time = Timestamp.FromDateTime(DateTime.SpecifyKind(new DateTime(2026, 4, 16, 0, 0, 0), DateTimeKind.Utc)),
                MerkleTreeRootOfTransactionStatus = CreateHash(0x07, 0x08),
                SignerPubkey = ByteString.CopyFrom([0x33, 0x44]),
                Signature = ByteString.CopyFrom([0x55, 0x66])
            },
            Body = new BlockBody()
        };

        block.Header.ExtraData["consensus"] = ByteString.CopyFromUtf8("aedpos");
        block.Body.TransactionIds.Add(CreateHash(0x10, 0x20));
        block.Body.TransactionIds.Add(CreateHash(0x30, 0x40));

        var payload = block.ToByteArray();
        var roundTrip = Block.Parser.ParseFrom(payload);
        var roundTripHash = blockHash.ToProtoHash();

        Assert.Equal(42, roundTrip.Header.Height);
        Assert.Equal("aedpos", roundTrip.Header.ExtraData["consensus"].ToStringUtf8());
        Assert.Equal(2, roundTrip.Body.TransactionIds.Count);
        Assert.Equal(new BlockReference(42, blockHash), roundTrip.ToBlockReference(roundTripHash));
        Assert.Equal(blockHash, roundTripHash.ToHex());
    }

    [Fact]
    public void MerklePath_Should_RoundTrip()
    {
        var path = new MerklePath();
        path.MerklePathNodes.Add(new MerklePathNode
        {
            Hash = CreateHash(0x0A, 0x0B),
            IsLeftChildNode = true
        });
        path.MerklePathNodes.Add(new MerklePathNode
        {
            Hash = CreateHash(0x0C, 0x0D),
            IsLeftChildNode = false
        });

        var payload = path.ToByteArray();
        var roundTrip = MerklePath.Parser.ParseFrom(payload);

        Assert.Equal(2, roundTrip.MerklePathNodes.Count);
        Assert.True(roundTrip.MerklePathNodes[0].IsLeftChildNode);
        Assert.False(roundTrip.MerklePathNodes[1].IsLeftChildNode);
        Assert.Equal("0A0B", roundTrip.MerklePathNodes[0].Hash.ToHex());
        Assert.Equal("0C0D", roundTrip.MerklePathNodes[1].Hash.ToHex());
    }

    private static Address CreateAddress(params byte[] value)
    {
        return new Address
        {
            Value = ByteString.CopyFrom(value)
        };
    }

    private static Hash CreateHash(params byte[] value)
    {
        return new Hash
        {
            Value = ByteString.CopyFrom(value)
        };
    }
}
