using System.Collections.Concurrent;

namespace NightElf.OS.Network;

public sealed class GrpcCompatibilityTransport : INetworkTransport
{
    private static readonly ConcurrentDictionary<int, Registration> Registrations = new();

    private static int s_nextEphemeralPort = 19000;

    private NetworkNodeEndpoint? _localNode;

    public NetworkTransportKind Kind => NetworkTransportKind.Grpc;

    public Task<NetworkNodeEndpoint> StartAsync(
        NetworkNodeEndpoint localNode,
        INetworkMessageSink messageSink,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(localNode);
        ArgumentNullException.ThrowIfNull(messageSink);
        localNode.Validate();

        var resolvedNode = new NetworkNodeEndpoint
        {
            NodeId = localNode.NodeId,
            Host = localNode.Host,
            GrpcPort = localNode.GrpcPort == 0 ? Interlocked.Increment(ref s_nextEphemeralPort) : localNode.GrpcPort,
            QuicPort = localNode.QuicPort
        };

        if (!Registrations.TryAdd(resolvedNode.GrpcPort, new Registration(resolvedNode, messageSink)))
        {
            throw new InvalidOperationException($"gRPC compatibility transport port '{resolvedNode.GrpcPort}' is already registered.");
        }

        _localNode = resolvedNode;
        return Task.FromResult(resolvedNode);
    }

    public async Task<NetworkSendReceipt> SendAsync(
        NetworkNodeEndpoint remoteNode,
        NetworkEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(remoteNode);
        ArgumentNullException.ThrowIfNull(envelope);

        if (_localNode is null)
        {
            throw new InvalidOperationException("gRPC compatibility transport must be started before sending messages.");
        }

        if (remoteNode.GrpcPort == 0)
        {
            throw new InvalidOperationException($"Remote node '{remoteNode.NodeId}' does not expose a gRPC compatibility port.");
        }

        if (!Registrations.TryGetValue(remoteNode.GrpcPort, out var registration))
        {
            throw new InvalidOperationException($"Remote gRPC compatibility endpoint '{remoteNode.Host}:{remoteNode.GrpcPort}' is not registered.");
        }

        await registration.MessageSink.HandleAsync(
                new NetworkMessageDelivery
                {
                    TransportKind = Kind,
                    LocalNode = registration.Node,
                    Envelope = CloneEnvelope(envelope)
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new NetworkSendReceipt
        {
            TransportKind = Kind,
            Scenario = envelope.Scenario,
            TargetNodeId = remoteNode.NodeId,
            BytesSent = envelope.Payload.Length
        };
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_localNode is not null)
        {
            Registrations.TryRemove(_localNode.GrpcPort, out _);
            _localNode = null;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }

    private static NetworkEnvelope CloneEnvelope(NetworkEnvelope envelope)
    {
        return new NetworkEnvelope
        {
            MessageId = envelope.MessageId,
            SourceNodeId = envelope.SourceNodeId,
            Scenario = envelope.Scenario,
            CreatedAtUtc = envelope.CreatedAtUtc,
            Payload = envelope.Payload.ToArray()
        };
    }

    private sealed record Registration(NetworkNodeEndpoint Node, INetworkMessageSink MessageSink);
}
