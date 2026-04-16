using System.Net.Security;

namespace NightElf.OS.Network;

public sealed class NetworkTransportOptions
{
    public const string SectionName = "NightElf:Network";

    public NetworkTransportKind RpcTransport { get; set; } = NetworkTransportKind.Grpc;

    public NetworkTransportKind BlockSyncTransport { get; set; } = NetworkTransportKind.Grpc;

    public NetworkTransportKind TransactionBroadcastTransport { get; set; } = NetworkTransportKind.Grpc;

    public QuicTransportOptions Quic { get; set; } = new();

    public NetworkTransportKind ResolveTransport(NetworkScenario scenario)
    {
        return scenario switch
        {
            NetworkScenario.Rpc => RpcTransport,
            NetworkScenario.BlockSync => BlockSyncTransport,
            NetworkScenario.TransactionBroadcast => TransactionBroadcastTransport,
            _ => throw new InvalidOperationException($"Unsupported network scenario '{scenario}'.")
        };
    }

    public bool UsesTransport(NetworkTransportKind transportKind)
    {
        return RpcTransport == transportKind ||
               BlockSyncTransport == transportKind ||
               TransactionBroadcastTransport == transportKind;
    }

    public void Validate()
    {
        if (RpcTransport != NetworkTransportKind.Grpc)
        {
            throw new InvalidOperationException("NightElf network RPC transport must remain gRPC-compatible.");
        }

        Quic.Validate();
    }
}

public sealed class QuicTransportOptions
{
    public string ServerName { get; set; } = "localhost";

    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int ListenBacklog { get; set; } = 16;

    public string BlockSyncApplicationProtocol { get; set; } = "nightelf-sync/1.0";

    public string TransactionBroadcastApplicationProtocol { get; set; } = "nightelf-tx/1.0";

    public SslApplicationProtocol GetApplicationProtocol(NetworkScenario scenario)
    {
        return scenario switch
        {
            NetworkScenario.BlockSync => new SslApplicationProtocol(BlockSyncApplicationProtocol),
            NetworkScenario.TransactionBroadcast => new SslApplicationProtocol(TransactionBroadcastApplicationProtocol),
            _ => throw new InvalidOperationException($"Scenario '{scenario}' is not mapped to QUIC.")
        };
    }

    public IReadOnlyList<SslApplicationProtocol> GetApplicationProtocols()
    {
        return
        [
            new SslApplicationProtocol(BlockSyncApplicationProtocol),
            new SslApplicationProtocol(TransactionBroadcastApplicationProtocol)
        ];
    }

    public NetworkScenario ResolveScenario(SslApplicationProtocol applicationProtocol)
    {
        if (applicationProtocol == new SslApplicationProtocol(BlockSyncApplicationProtocol))
        {
            return NetworkScenario.BlockSync;
        }

        if (applicationProtocol == new SslApplicationProtocol(TransactionBroadcastApplicationProtocol))
        {
            return NetworkScenario.TransactionBroadcast;
        }

        throw new InvalidOperationException($"Unsupported QUIC application protocol '{applicationProtocol.Protocol}'.");
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ServerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(BlockSyncApplicationProtocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(TransactionBroadcastApplicationProtocol);

        if (HandshakeTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("NightElf:Network:Quic:HandshakeTimeout must be greater than zero.");
        }

        if (IdleTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("NightElf:Network:Quic:IdleTimeout must be greater than zero.");
        }

        if (KeepAliveInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("NightElf:Network:Quic:KeepAliveInterval must be greater than zero.");
        }

        if (ListenBacklog <= 0)
        {
            throw new InvalidOperationException("NightElf:Network:Quic:ListenBacklog must be greater than zero.");
        }
    }
}
