using System.Text.Json;
using System.Text.Json.Serialization;

using NightElf.Kernel.Consensus;

namespace NightElf.Launcher;

internal sealed class PeerHelloMessage
{
    public required LauncherPeerOptions Node { get; init; }

    public bool IsAck { get; init; }

    public long BestChainHeight { get; init; }

    public string BestChainHash { get; init; } = string.Empty;

    public DateTimeOffset SentAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class BlockSyncMessage
{
    public required string SourceNodeId { get; init; }

    public required ConsensusBlockProposal Proposal { get; init; }

    public List<byte[]> Transactions { get; init; } = [];
}

internal sealed class TransactionBroadcastMessage
{
    public required string SourceNodeId { get; init; }

    public required byte[] TransactionBytes { get; init; }
}

internal static class ConsensusClusterMessageSerializer
{
    public static byte[] Serialize(PeerHelloMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(
            message,
            ConsensusClusterJsonSerializerContext.Default.PeerHelloMessage);
    }

    public static byte[] Serialize(BlockSyncMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(
            message,
            ConsensusClusterJsonSerializerContext.Default.BlockSyncMessage);
    }

    public static byte[] Serialize(TransactionBroadcastMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(
            message,
            ConsensusClusterJsonSerializerContext.Default.TransactionBroadcastMessage);
    }

    public static PeerHelloMessage DeserializePeerHello(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Deserialize(
                   payload,
                   ConsensusClusterJsonSerializerContext.Default.PeerHelloMessage)
               ?? throw new InvalidOperationException("Failed to deserialize peer hello payload.");
    }

    public static BlockSyncMessage DeserializeBlockSync(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Deserialize(
                   payload,
                   ConsensusClusterJsonSerializerContext.Default.BlockSyncMessage)
               ?? throw new InvalidOperationException("Failed to deserialize block sync payload.");
    }

    public static TransactionBroadcastMessage DeserializeTransactionBroadcast(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Deserialize(
                   payload,
                   ConsensusClusterJsonSerializerContext.Default.TransactionBroadcastMessage)
               ?? throw new InvalidOperationException("Failed to deserialize transaction broadcast payload.");
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PeerHelloMessage))]
[JsonSerializable(typeof(BlockSyncMessage))]
[JsonSerializable(typeof(TransactionBroadcastMessage))]
[JsonSerializable(typeof(LauncherPeerOptions))]
[JsonSerializable(typeof(ConsensusBlockProposal))]
internal sealed partial class ConsensusClusterJsonSerializerContext : JsonSerializerContext
{
}
