namespace NightElf.OS.Network;

public sealed class NetworkTransportCoordinator : INetworkTransportCoordinator
{
    private readonly NetworkTransportOptions _options;
    private readonly INetworkMessageSink _messageSink;
    private readonly IReadOnlyDictionary<NetworkTransportKind, INetworkTransport> _transports;

    private NetworkNodeEndpoint? _localNode;

    public NetworkTransportCoordinator(
        NetworkTransportOptions options,
        INetworkMessageSink messageSink,
        GrpcCompatibilityTransport grpcTransport,
        QuicTransport quicTransport)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _messageSink = messageSink ?? throw new ArgumentNullException(nameof(messageSink));
        ArgumentNullException.ThrowIfNull(grpcTransport);
        ArgumentNullException.ThrowIfNull(quicTransport);

        _transports = new Dictionary<NetworkTransportKind, INetworkTransport>
        {
            [NetworkTransportKind.Grpc] = grpcTransport,
            [NetworkTransportKind.Quic] = quicTransport
        };
    }

    public async Task<NetworkNodeEndpoint> StartAsync(
        NetworkNodeEndpoint localNode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(localNode);
        localNode.Validate();
        _options.Validate();

        var resolvedNode = new NetworkNodeEndpoint
        {
            NodeId = localNode.NodeId,
            Host = localNode.Host,
            GrpcPort = localNode.GrpcPort,
            QuicPort = localNode.QuicPort
        };

        foreach (var transportKind in GetEnabledTransports())
        {
            resolvedNode = await _transports[transportKind]
                .StartAsync(resolvedNode, _messageSink, cancellationToken)
                .ConfigureAwait(false);
        }

        _localNode = resolvedNode;
        return resolvedNode;
    }

    public Task<NetworkSendReceipt> SendAsync(
        NetworkScenario scenario,
        NetworkNodeEndpoint remoteNode,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(remoteNode);
        ArgumentNullException.ThrowIfNull(payload);

        if (_localNode is null)
        {
            throw new InvalidOperationException("Network transport coordinator must be started before sending messages.");
        }

        var envelope = new NetworkEnvelope
        {
            SourceNodeId = _localNode.NodeId,
            Scenario = scenario,
            Payload = payload.ToArray()
        };

        return _transports[_options.ResolveTransport(scenario)].SendAsync(remoteNode, envelope, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var transportKind in GetEnabledTransports().Reverse())
        {
            await _transports[transportKind].StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _localNode = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        foreach (var transportKind in GetEnabledTransports().Reverse())
        {
            await _transports[transportKind].DisposeAsync().ConfigureAwait(false);
        }
    }

    private IReadOnlyList<NetworkTransportKind> GetEnabledTransports()
    {
        return Enum.GetValues<NetworkTransportKind>()
            .Where(_options.UsesTransport)
            .ToArray();
    }
}
