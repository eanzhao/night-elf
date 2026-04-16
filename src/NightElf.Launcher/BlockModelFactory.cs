using System.Text;
using System.Text.Json;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Launcher;

internal static class BlockModelFactory
{
    public static Block CreateBlock(
        ConsensusBlockProposal proposal,
        int chainId,
        IReadOnlyList<Transaction>? transactions = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var header = new BlockHeader
        {
            Version = 1,
            ChainId = chainId,
            PreviousBlockHash = string.Equals(proposal.ParentBlockHash, "GENESIS", StringComparison.Ordinal)
                ? new Hash()
                : proposal.ParentBlockHash.ToProtoHash(),
            MerkleTreeRootOfTransactions = new Hash(),
            MerkleTreeRootOfWorldState = new Hash(),
            Bloom = ByteString.Empty,
            Height = proposal.Block.Height,
            Time = Timestamp.FromDateTime(proposal.TimestampUtc.UtcDateTime),
            MerkleTreeRootOfTransactionStatus = new Hash(),
            SignerPubkey = ByteString.CopyFromUtf8(proposal.ProposerAddress),
            Signature = ByteString.CopyFrom(proposal.VrfProof)
        };

        header.ExtraData["consensus"] = ByteString.CopyFrom(proposal.ConsensusData);
        header.ExtraData["random_seed"] = ByteString.CopyFrom(proposal.RandomSeed);
        header.ExtraData["randomness"] = ByteString.CopyFrom(proposal.Randomness);
        header.ExtraData["proposer"] = ByteString.CopyFromUtf8(proposal.ProposerAddress);
        header.ExtraData["term"] = ByteString.CopyFromUtf8(proposal.TermNumber.ToString());
        header.ExtraData["round"] = ByteString.CopyFromUtf8(proposal.RoundNumber.ToString());

        var block = new Block
        {
            Header = header,
            Body = new BlockBody()
        };

        if (transactions is not null)
        {
            foreach (var transaction in transactions)
            {
                block.Body.TransactionIds.Add(transaction.GetTransactionIdHash());
            }

            header.ExtraData["tx_count"] = ByteString.CopyFromUtf8(block.Body.TransactionIds.Count.ToString());
        }

        return block;
    }

    public static byte[] CreateGenesisConfigPayload(GenesisConfig genesisConfig)
    {
        ArgumentNullException.ThrowIfNull(genesisConfig);

        return JsonSerializer.SerializeToUtf8Bytes(
            genesisConfig.ToSnapshot(),
            GenesisJsonSerializerContext.Default.GenesisConfigSnapshot);
    }
}
