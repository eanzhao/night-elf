using System.Net;

namespace NightElf.OS.Network;

public enum NetworkTransportKind
{
    Grpc,
    Quic
}

public enum NetworkScenario
{
    Rpc,
    BlockSync,
    TransactionBroadcast
}

public sealed class NetworkNodeEndpoint
{
    public required string NodeId { get; init; }

    public string Host { get; init; } = IPAddress.Loopback.ToString();

    public int GrpcPort { get; init; }

    public int QuicPort { get; init; }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(NodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ValidatePort(GrpcPort, nameof(GrpcPort));
        ValidatePort(QuicPort, nameof(QuicPort));
    }

    private static void ValidatePort(int port, string propertyName)
    {
        if (port is < 0 or > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException($"Network endpoint property '{propertyName}' must be between 0 and {IPEndPoint.MaxPort}.");
        }
    }
}

public sealed class NetworkEnvelope
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");

    public required string SourceNodeId { get; init; }

    public required NetworkScenario Scenario { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public byte[] Payload { get; init; } = [];

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(MessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceNodeId);
        ArgumentNullException.ThrowIfNull(Payload);
    }
}

public sealed class NetworkMessageDelivery
{
    public required NetworkTransportKind TransportKind { get; init; }

    public required NetworkNodeEndpoint LocalNode { get; init; }

    public required NetworkEnvelope Envelope { get; init; }
}

public sealed class NetworkSendReceipt
{
    public required NetworkTransportKind TransportKind { get; init; }

    public required NetworkScenario Scenario { get; init; }

    public required string TargetNodeId { get; init; }

    public required int BytesSent { get; init; }
}

public interface INetworkMessageSink
{
    Task HandleAsync(NetworkMessageDelivery delivery, CancellationToken cancellationToken = default);
}

public interface INetworkTransport : IAsyncDisposable
{
    NetworkTransportKind Kind { get; }

    Task<NetworkNodeEndpoint> StartAsync(
        NetworkNodeEndpoint localNode,
        INetworkMessageSink messageSink,
        CancellationToken cancellationToken = default);

    Task<NetworkSendReceipt> SendAsync(
        NetworkNodeEndpoint remoteNode,
        NetworkEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface INetworkTransportCoordinator : IAsyncDisposable
{
    Task<NetworkNodeEndpoint> StartAsync(
        NetworkNodeEndpoint localNode,
        CancellationToken cancellationToken = default);

    Task<NetworkSendReceipt> SendAsync(
        NetworkScenario scenario,
        NetworkNodeEndpoint remoteNode,
        byte[] payload,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
