using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NightElf.OS.Network.Tests;

public sealed class NetworkTransportServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddNetworkTransport_Should_Register_Grpc_By_Default()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        services.AddNetworkTransport(configuration);

        await using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<NetworkTransportOptions>();
        var coordinator = serviceProvider.GetRequiredService<INetworkTransportCoordinator>();

        Assert.Equal(NetworkTransportKind.Grpc, options.ResolveTransport(NetworkScenario.Rpc));
        Assert.Equal(NetworkTransportKind.Grpc, options.ResolveTransport(NetworkScenario.BlockSync));
        Assert.Equal(NetworkTransportKind.Grpc, options.ResolveTransport(NetworkScenario.TransactionBroadcast));
        Assert.IsType<NetworkTransportCoordinator>(coordinator);
    }

    [Fact]
    public void AddNetworkTransport_Should_Parse_Quic_Routing_Overrides()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NightElf:Network:BlockSyncTransport"] = "Quic",
            ["NightElf:Network:TransactionBroadcastTransport"] = "Quic",
            ["NightElf:Network:Quic:ServerName"] = "nightelf-peer",
            ["NightElf:Network:Quic:HandshakeTimeout"] = "00:00:12",
            ["NightElf:Network:Quic:IdleTimeout"] = "00:00:45",
            ["NightElf:Network:Quic:KeepAliveInterval"] = "00:00:03",
            ["NightElf:Network:Quic:ListenBacklog"] = "32"
        });

        services.AddNetworkTransport(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<NetworkTransportOptions>();

        Assert.Equal(NetworkTransportKind.Grpc, options.ResolveTransport(NetworkScenario.Rpc));
        Assert.Equal(NetworkTransportKind.Quic, options.ResolveTransport(NetworkScenario.BlockSync));
        Assert.Equal(NetworkTransportKind.Quic, options.ResolveTransport(NetworkScenario.TransactionBroadcast));
        Assert.Equal("nightelf-peer", options.Quic.ServerName);
        Assert.Equal(TimeSpan.FromSeconds(12), options.Quic.HandshakeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Quic.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), options.Quic.KeepAliveInterval);
        Assert.Equal(32, options.Quic.ListenBacklog);
    }

    [Fact]
    public void AddNetworkTransport_Should_Fail_When_Rpc_Transport_Is_Not_Grpc()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NightElf:Network:RpcTransport"] = "Quic"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddNetworkTransport(configuration));

        Assert.Contains("RPC transport must remain gRPC-compatible", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
