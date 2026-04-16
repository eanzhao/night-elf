using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;

namespace NightElf.OS.Network;

public sealed class QuicTransport : INetworkTransport
{
    private sealed record Registration(NetworkNodeEndpoint Node, INetworkMessageSink MessageSink);

    private static readonly ConcurrentDictionary<int, Registration> FallbackRegistrations = new();
    private static int s_nextFallbackPort = 29000;

    private readonly QuicTransportOptions _options;
    private readonly IQuicCredentialProvider _credentialProvider;
    private readonly QuicConnectionManager _connectionManager;
    private readonly ConcurrentBag<Task> _connectionTasks = [];

    private QuicListener? _listener;
    private INetworkMessageSink? _messageSink;
    private NetworkNodeEndpoint? _localNode;
    private CancellationTokenSource? _acceptLoopCancellation;
    private Task? _acceptLoopTask;

    public static bool IsNativeQuicSupported => QuicConnection.IsSupported && QuicListener.IsSupported;

    public QuicTransport(
        NetworkTransportOptions options,
        IQuicCredentialProvider credentialProvider,
        QuicConnectionManager connectionManager)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Quic;
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    public NetworkTransportKind Kind => NetworkTransportKind.Quic;

    public async Task<NetworkNodeEndpoint> StartAsync(
        NetworkNodeEndpoint localNode,
        INetworkMessageSink messageSink,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(localNode);
        ArgumentNullException.ThrowIfNull(messageSink);
        localNode.Validate();
        _options.Validate();
        _messageSink = messageSink;

        if (!IsNativeQuicSupported)
        {
            return await StartFallbackAsync(localNode, messageSink, cancellationToken).ConfigureAwait(false);
        }

        var listenAddress = IPAddress.TryParse(localNode.Host, out var parsedAddress)
            ? parsedAddress
            : IPAddress.Loopback;

        _listener = await QuicListener.ListenAsync(
                new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(listenAddress, localNode.QuicPort),
                    ListenBacklog = _options.ListenBacklog,
                    ApplicationProtocols = _options.GetApplicationProtocols().ToList(),
                    ConnectionOptionsCallback = CreateConnectionOptionsAsync
                },
                cancellationToken)
            .ConfigureAwait(false);

        var resolvedPort = ((IPEndPoint)_listener.LocalEndPoint).Port;
        _localNode = new NetworkNodeEndpoint
        {
            NodeId = localNode.NodeId,
            Host = listenAddress.ToString(),
            GrpcPort = localNode.GrpcPort,
            QuicPort = resolvedPort
        };

        _acceptLoopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoopTask = Task.Run(() => AcceptConnectionsAsync(_acceptLoopCancellation.Token), CancellationToken.None);

        return _localNode;
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
            throw new InvalidOperationException("QUIC transport must be started before sending messages.");
        }

        if (!IsNativeQuicSupported)
        {
            return await SendFallbackAsync(remoteNode, envelope, cancellationToken).ConfigureAwait(false);
        }

        var connection = await _connectionManager.GetOrConnectAsync(remoteNode, envelope.Scenario, cancellationToken)
            .ConfigureAwait(false);
        var payload = NetworkEnvelopeSerializer.Serialize(envelope);

        await using var stream = await connection.OpenOutboundStreamAsync(
                QuicStreamType.Bidirectional,
                cancellationToken)
            .ConfigureAwait(false);

        await stream.WriteAsync(payload, completeWrites: true, cancellationToken).ConfigureAwait(false);
        await stream.WritesClosed.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new NetworkSendReceipt
        {
            TransportKind = Kind,
            Scenario = envelope.Scenario,
            TargetNodeId = remoteNode.NodeId,
            BytesSent = payload.Length
        };
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsNativeQuicSupported)
        {
            StopFallback();
            return;
        }

        if (_acceptLoopCancellation is not null)
        {
            _acceptLoopCancellation.Cancel();
            _acceptLoopCancellation.Dispose();
            _acceptLoopCancellation = null;
        }

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }

        if (_acceptLoopTask is not null)
        {
            await IgnoreShutdownAsync(_acceptLoopTask).ConfigureAwait(false);
            _acceptLoopTask = null;
        }

        await Task.WhenAll(_connectionTasks.ToArray()).ConfigureAwait(false);
        await _connectionManager.CloseAsync().ConfigureAwait(false);
        _localNode = null;
        _messageSink = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private ValueTask<QuicServerConnectionOptions> CreateConnectionOptionsAsync(
        QuicConnection connection,
        SslClientHelloInfo clientHelloInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            new QuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = _options.GetApplicationProtocols().ToList(),
                    ServerCertificate = _credentialProvider.GetServerCertificate()
                },
                HandshakeTimeout = _options.HandshakeTimeout,
                IdleTimeout = _options.IdleTimeout,
                KeepAliveInterval = _options.KeepAliveInterval,
                MaxInboundBidirectionalStreams = 100
            });
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            QuicConnection connection;

            try
            {
                connection = await _listener.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedShutdown(exception, cancellationToken))
            {
                break;
            }

            var connectionTask = HandleConnectionAsync(connection, cancellationToken);
            _connectionTasks.Add(connectionTask);
        }
    }

    private async Task HandleConnectionAsync(QuicConnection connection, CancellationToken cancellationToken)
    {
        await using var ownedConnection = connection;

        while (!cancellationToken.IsCancellationRequested)
        {
            QuicStream stream;

            try
            {
                stream = await connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedShutdown(exception, cancellationToken))
            {
                break;
            }

            await HandleStreamAsync(connection, stream, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleStreamAsync(
        QuicConnection connection,
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_messageSink);
        ArgumentNullException.ThrowIfNull(_localNode);

        await using var ownedStream = stream;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var envelope = NetworkEnvelopeSerializer.Deserialize(buffer.ToArray());
        var negotiatedScenario = _options.ResolveScenario(connection.NegotiatedApplicationProtocol);

        if (negotiatedScenario != envelope.Scenario)
        {
            throw new InvalidOperationException(
                $"Inbound QUIC scenario mismatch. ALPN mapped to '{negotiatedScenario}' but envelope carried '{envelope.Scenario}'.");
        }

        await _messageSink.HandleAsync(
                new NetworkMessageDelivery
                {
                    TransportKind = Kind,
                    LocalNode = _localNode,
                    Envelope = envelope
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task IgnoreShutdownAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (QuicException)
        {
        }
    }

    private static bool IsExpectedShutdown(Exception exception, CancellationToken cancellationToken)
    {
        return exception is OperationCanceledException && cancellationToken.IsCancellationRequested ||
               exception is ObjectDisposedException ||
               exception is QuicException && cancellationToken.IsCancellationRequested;
    }

    private Task<NetworkNodeEndpoint> StartFallbackAsync(
        NetworkNodeEndpoint localNode,
        INetworkMessageSink messageSink,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedNode = new NetworkNodeEndpoint
        {
            NodeId = localNode.NodeId,
            Host = localNode.Host,
            GrpcPort = localNode.GrpcPort,
            QuicPort = localNode.QuicPort == 0 ? Interlocked.Increment(ref s_nextFallbackPort) : localNode.QuicPort
        };

        if (!FallbackRegistrations.TryAdd(resolvedNode.QuicPort, new Registration(resolvedNode, messageSink)))
        {
            throw new InvalidOperationException($"QUIC fallback endpoint '{resolvedNode.Host}:{resolvedNode.QuicPort}' is already registered.");
        }

        _localNode = resolvedNode;
        return Task.FromResult(resolvedNode);
    }

    private async Task<NetworkSendReceipt> SendFallbackAsync(
        NetworkNodeEndpoint remoteNode,
        NetworkEnvelope envelope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (remoteNode.QuicPort == 0)
        {
            throw new InvalidOperationException($"Remote node '{remoteNode.NodeId}' does not expose a QUIC port.");
        }

        if (!FallbackRegistrations.TryGetValue(remoteNode.QuicPort, out var registration))
        {
            throw new InvalidOperationException($"Remote QUIC fallback endpoint '{remoteNode.Host}:{remoteNode.QuicPort}' is not registered.");
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

    private void StopFallback()
    {
        if (_localNode is not null)
        {
            FallbackRegistrations.TryRemove(_localNode.QuicPort, out _);
            _localNode = null;
        }

        _messageSink = null;
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
}
