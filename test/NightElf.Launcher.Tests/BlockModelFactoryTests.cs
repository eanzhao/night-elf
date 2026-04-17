using System.Text;

using Google.Protobuf;

using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Launcher.Tests;

public sealed class BlockModelFactoryTests
{
    [Fact]
    public void CreateBlock_Should_Set_Header_Fields_From_Proposal()
    {
        var proposal = CreateProposal(height: 10, parentHash: "AABB", proposer: "validator-a");

        var block = BlockModelFactory.CreateBlock(proposal, chainId: 9992731);

        Assert.Equal(1, block.Header.Version);
        Assert.Equal(9992731, block.Header.ChainId);
        Assert.Equal(10, block.Header.Height);
        Assert.Equal("validator-a", block.Header.SignerPubkey.ToStringUtf8());
    }

    [Fact]
    public void CreateBlock_Should_Treat_Genesis_Parent_As_Empty_Hash()
    {
        var proposal = CreateProposal(height: 1, parentHash: "GENESIS", proposer: "validator-a");

        var block = BlockModelFactory.CreateBlock(proposal, chainId: 1);

        Assert.True(block.Header.PreviousBlockHash.Value.IsEmpty);
    }

    [Fact]
    public void CreateBlock_Should_Convert_Non_Genesis_Parent_To_ProtoHash()
    {
        var hexHash = "AABBCCDD";
        var proposal = CreateProposal(height: 2, parentHash: hexHash, proposer: "validator-a");

        var block = BlockModelFactory.CreateBlock(proposal, chainId: 1);

        Assert.False(block.Header.PreviousBlockHash.Value.IsEmpty);
        Assert.Equal(
            Convert.FromHexString(hexHash),
            block.Header.PreviousBlockHash.Value.ToByteArray());
    }

    [Fact]
    public void CreateBlock_Should_Set_ExtraData_Consensus_Fields()
    {
        var proposal = CreateProposal(height: 5, parentHash: "AA", proposer: "validator-b",
            roundNumber: 3, termNumber: 2);

        var block = BlockModelFactory.CreateBlock(proposal, chainId: 1);

        Assert.Equal("validator-b", block.Header.ExtraData["proposer"].ToStringUtf8());
        Assert.Equal("2", block.Header.ExtraData["term"].ToStringUtf8());
        Assert.Equal("3", block.Header.ExtraData["round"].ToStringUtf8());
        Assert.True(block.Header.ExtraData.ContainsKey("consensus"));
        Assert.True(block.Header.ExtraData.ContainsKey("random_seed"));
        Assert.True(block.Header.ExtraData.ContainsKey("randomness"));
    }

    [Fact]
    public void CreateBlock_Should_Add_Transaction_Ids_And_Count_When_Transactions_Provided()
    {
        var proposal = CreateProposal(height: 8, parentHash: "CC", proposer: "validator-c");
        var transactions = new[]
        {
            CreateTransaction("method-1"),
            CreateTransaction("method-2"),
            CreateTransaction("method-3")
        };

        var block = BlockModelFactory.CreateBlock(proposal, chainId: 1, transactions: transactions);

        Assert.Equal(3, block.Body.TransactionIds.Count);
        Assert.Equal("3", block.Header.ExtraData["tx_count"].ToStringUtf8());
    }

    [Fact]
    public void CreateBlock_Should_Have_Empty_Body_When_No_Transactions()
    {
        var proposal = CreateProposal(height: 3, parentHash: "DD", proposer: "validator-a");

        var block = BlockModelFactory.CreateBlock(proposal, chainId: 1);

        Assert.Empty(block.Body.TransactionIds);
        Assert.False(block.Header.ExtraData.ContainsKey("tx_count"));
    }

    [Fact]
    public void CreateBlock_Should_Store_Vrf_Proof_As_Signature()
    {
        var vrfProof = new byte[] { 9, 8, 7, 6, 5 };
        var proposal = new ConsensusBlockProposal
        {
            Block = new BlockReference(1, "hash-1"),
            ParentBlockHash = "GENESIS",
            ProposerAddress = "validator-a",
            RoundNumber = 1,
            TermNumber = 1,
            LastIrreversibleBlockHeight = 0,
            TimestampUtc = DateTimeOffset.UtcNow,
            VrfProof = vrfProof,
            ConsensusData = [1, 2],
            RandomSeed = [3, 4],
            Randomness = [5, 6]
        };

        var block = BlockModelFactory.CreateBlock(proposal, chainId: 1);

        Assert.Equal(vrfProof, block.Header.Signature.ToByteArray());
    }

    [Fact]
    public void CreateBlock_Should_Reject_Null_Proposal()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BlockModelFactory.CreateBlock(null!, chainId: 1));
    }

    private static ConsensusBlockProposal CreateProposal(
        long height,
        string parentHash,
        string proposer,
        long roundNumber = 1,
        long termNumber = 1)
    {
        return new ConsensusBlockProposal
        {
            Block = new BlockReference(height, $"block-{height}"),
            ParentBlockHash = parentHash,
            ProposerAddress = proposer,
            RoundNumber = roundNumber,
            TermNumber = termNumber,
            LastIrreversibleBlockHeight = Math.Max(0, height - 5),
            TimestampUtc = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero),
            ConsensusData = Encoding.UTF8.GetBytes($"aedpos|{proposer}"),
            RandomSeed = [1, 2, 3],
            Randomness = [4, 5, 6],
            VrfProof = [7, 8, 9]
        };
    }

    private static Transaction CreateTransaction(string methodName)
    {
        return new Transaction
        {
            From = new Address { Value = ByteString.CopyFrom(new byte[32]) },
            To = new Address { Value = ByteString.CopyFrom(new byte[32]) },
            MethodName = methodName,
            Params = ByteString.Empty,
            RefBlockNumber = 1,
            RefBlockPrefix = ByteString.CopyFrom(new byte[4]),
            Signature = ByteString.CopyFrom(new byte[64])
        };
    }
}
