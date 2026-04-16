using System.Collections.Concurrent;
using System.Text;

namespace NightElf.OS.Network.Tests;

public sealed class NetworkTransportCoordinatorTests
{
    [Fact]
    public async Task SendAsync_Should_Keep_Rpc_On_Grpc_Path_When_Quic_Is_Enabled()
    {
        var options = CreateHybridOptions();
        var credentialProvider = new EphemeralQuicCredentialProvider();
        var serverSink = new RecordingNetworkMessageSink();
        var clientSink = new RecordingNetworkMessageSink();

        await using var server = CreateCoordinator(options, serverSink, credentialProvider);
        await using var client = CreateCoordinator(options, clientSink, credentialProvider);

        var serverNode = await server.StartAsync(new NetworkNodeEndpoint
        {
            NodeId = "server",
            Host = "127.0.0.1",
            GrpcPort = 0,
            QuicPort = 0
        });
        await client.StartAsync(new NetworkNodeEndpoint
        {
            NodeId = "client",
            Host = "127.0.0.1",
            GrpcPort = 0,
            QuicPort = 0
        });

        var payload = Encoding.UTF8.GetBytes("rpc:ping");
        var receipt = await client.SendAsync(NetworkScenario.Rpc, serverNode, payload);
        var delivery = await serverSink.WaitForMessageAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(NetworkTransportKind.Grpc, receipt.TransportKind);
        Assert.Equal(NetworkTransportKind.Grpc, delivery.TransportKind);
        Assert.Equal(NetworkScenario.Rpc, delivery.Envelope.Scenario);
        Assert.Equal("client", delivery.Envelope.SourceNodeId);
        Assert.Equal("rpc:ping", Encoding.UTF8.GetString(delivery.Envelope.Payload));
    }

    [Fact]
    public async Task SendAsync_Should_Deliver_BlockSync_And_TransactionBroadcast_Over_Quic()
    {
        var options = CreateHybridOptions();
        var credentialProvider = new EphemeralQuicCredentialProvider();
        var serverSink = new RecordingNetworkMessageSink();
        var clientSink = new RecordingNetworkMessageSink();

        await using var server = CreateCoordinator(options, serverSink, credentialProvider);
        await using var client = CreateCoordinator(options, clientSink, credentialProvider);

        var serverNode = await server.StartAsync(new NetworkNodeEndpoint
        {
            NodeId = "server",
            Host = "127.0.0.1",
            GrpcPort = 0,
            QuicPort = 0
        });
        await client.StartAsync(new NetworkNodeEndpoint
        {
            NodeId = "client",
            Host = "127.0.0.1",
            GrpcPort = 0,
            QuicPort = 0
        });

        var blockReceipt = await client.SendAsync(
            NetworkScenario.BlockSync,
            serverNode,
            Encoding.UTF8.GetBytes("block:1024"));
        var txReceipt = await client.SendAsync(
            NetworkScenario.TransactionBroadcast,
            serverNode,
            Encoding.UTF8.GetBytes("tx:0xabc"));

        var first = await serverSink.WaitForMessageAsync(TimeSpan.FromSeconds(5));
        var second = await serverSink.WaitForMessageAsync(TimeSpan.FromSeconds(5));
        var deliveries = new[] { first, second }.OrderBy(static item => item.Envelope.Scenario).ToArray();

        Assert.Equal(NetworkTransportKind.Quic, blockReceipt.TransportKind);
        Assert.Equal(NetworkTransportKind.Quic, txReceipt.TransportKind);
        Assert.Equal(NetworkScenario.BlockSync, deliveries[0].Envelope.Scenario);
        Assert.Equal("block:1024", Encoding.UTF8.GetString(deliveries[0].Envelope.Payload));
        Assert.Equal(NetworkScenario.TransactionBroadcast, deliveries[1].Envelope.Scenario);
        Assert.Equal("tx:0xabc", Encoding.UTF8.GetString(deliveries[1].Envelope.Payload));
        Assert.All(deliveries, static delivery => Assert.Equal(NetworkTransportKind.Quic, delivery.TransportKind));
    }

    private static INetworkTransportCoordinator CreateCoordinator(
        NetworkTransportOptions options,
        INetworkMessageSink messageSink,
        IQuicCredentialProvider credentialProvider)
    {
        var grpcTransport = new GrpcCompatibilityTransport();
        var connectionManager = new QuicConnectionManager(options, credentialProvider);
        var quicTransport = new QuicTransport(options, credentialProvider, connectionManager);

        return new NetworkTransportCoordinator(options, messageSink, grpcTransport, quicTransport);
    }

    private static NetworkTransportOptions CreateHybridOptions()
    {
        return new NetworkTransportOptions
        {
            RpcTransport = NetworkTransportKind.Grpc,
            BlockSyncTransport = NetworkTransportKind.Quic,
            TransactionBroadcastTransport = NetworkTransportKind.Quic,
            Quic = new QuicTransportOptions
            {
                ServerName = "localhost",
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                IdleTimeout = TimeSpan.FromSeconds(30),
                KeepAliveInterval = TimeSpan.FromSeconds(2),
                ListenBacklog = 16,
                BlockSyncApplicationProtocol = "nightelf-sync/1.0",
                TransactionBroadcastApplicationProtocol = "nightelf-tx/1.0"
            }
        };
    }

    private sealed class RecordingNetworkMessageSink : INetworkMessageSink
    {
        private readonly ConcurrentQueue<NetworkMessageDelivery> _messages = new();
        private readonly SemaphoreSlim _signal = new(0);

        public Task HandleAsync(NetworkMessageDelivery delivery, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _messages.Enqueue(delivery);
            _signal.Release();
            return Task.CompletedTask;
        }

        public async Task<NetworkMessageDelivery> WaitForMessageAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            await _signal.WaitAsync(cancellation.Token);

            if (_messages.TryDequeue(out var delivery))
            {
                return delivery;
            }

            throw new InvalidOperationException("Expected a queued network delivery after the sink was signalled.");
        }
    }
}
