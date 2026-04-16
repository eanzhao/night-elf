using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Text;

namespace NightElf.OS.Network;

public sealed class QuicConnectionManager : IAsyncDisposable
{
    private readonly QuicTransportOptions _options;
    private readonly IQuicCredentialProvider _credentialProvider;
    private readonly ConcurrentDictionary<ConnectionKey, Lazy<Task<QuicConnection>>> _connections = new();

    public QuicConnectionManager(
        NetworkTransportOptions options,
        IQuicCredentialProvider credentialProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Quic;
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
    }

    public async Task<QuicConnection> GetOrConnectAsync(
        NetworkNodeEndpoint remoteNode,
        NetworkScenario scenario,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteNode);

        if (remoteNode.QuicPort == 0)
        {
            throw new InvalidOperationException($"Remote node '{remoteNode.NodeId}' does not expose a QUIC port.");
        }

        var protocol = _options.GetApplicationProtocol(scenario);
        var key = new ConnectionKey(
            remoteNode.Host,
            remoteNode.QuicPort,
            Encoding.ASCII.GetString(protocol.Protocol.Span));
        var lazyConnection = _connections.GetOrAdd(
            key,
            _ => new Lazy<Task<QuicConnection>>(
                () => ConnectCoreAsync(remoteNode, protocol, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazyConnection.Value.ConfigureAwait(false);
        }
        catch
        {
            _connections.TryRemove(key, out _);
            throw;
        }
    }

    public async Task CloseAsync()
    {
        foreach (var lazyConnection in _connections.Values)
        {
            if (!lazyConnection.IsValueCreated)
            {
                continue;
            }

            var connection = await lazyConnection.Value.ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private async Task<QuicConnection> ConnectCoreAsync(
        NetworkNodeEndpoint remoteNode,
        SslApplicationProtocol protocol,
        CancellationToken cancellationToken)
    {
        return await QuicConnection.ConnectAsync(
                new QuicClientConnectionOptions
                {
                    RemoteEndPoint = CreateRemoteEndPoint(remoteNode.Host, remoteNode.QuicPort),
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = [protocol],
                        TargetHost = _options.ServerName,
                        RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                            _credentialProvider.IsTrustedPeer(certificate)
                    },
                    HandshakeTimeout = _options.HandshakeTimeout,
                    IdleTimeout = _options.IdleTimeout,
                    KeepAliveInterval = _options.KeepAliveInterval
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static EndPoint CreateRemoteEndPoint(string host, int port)
    {
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            return new IPEndPoint(ipAddress, port);
        }

        return new DnsEndPoint(host, port);
    }

    private readonly record struct ConnectionKey(string Host, int Port, string Protocol);
}
