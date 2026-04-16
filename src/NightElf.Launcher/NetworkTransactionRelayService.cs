using Google.Protobuf;

using NightElf.Kernel.Core;
using Microsoft.Extensions.Logging;

using NightElf.Kernel.Core.Protobuf;
using NightElf.OS.Network;
using NightElf.WebApp;

namespace NightElf.Launcher;

public sealed class NetworkTransactionRelayService : ITransactionRelayService
{
    private readonly ILogger<NetworkTransactionRelayService> _logger;
    private readonly LauncherOptions _launcherOptions;
    private readonly ClusterPeerRegistry _peerRegistry;
    private readonly INetworkTransportCoordinator _networkTransportCoordinator;

    public NetworkTransactionRelayService(
        ILogger<NetworkTransactionRelayService> logger,
        LauncherOptions launcherOptions,
        ClusterPeerRegistry peerRegistry,
        INetworkTransportCoordinator networkTransportCoordinator)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _launcherOptions = launcherOptions ?? throw new ArgumentNullException(nameof(launcherOptions));
        _peerRegistry = peerRegistry ?? throw new ArgumentNullException(nameof(peerRegistry));
        _networkTransportCoordinator = networkTransportCoordinator ?? throw new ArgumentNullException(nameof(networkTransportCoordinator));
    }

    public async Task RelayAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var payload = ConsensusClusterMessageSerializer.Serialize(
            new TransactionBroadcastMessage
            {
                SourceNodeId = _launcherOptions.NodeId,
                TransactionBytes = transaction.ToByteArray()
            });

        foreach (var peer in _peerRegistry.GetPeers())
        {
            try
            {
                await _networkTransportCoordinator.SendAsync(
                        NetworkScenario.TransactionBroadcast,
                        peer,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Failed to relay transaction {TransactionId} to peer {PeerNodeId}.",
                    transaction.GetTransactionId(),
                    peer.NodeId);
            }
        }
    }
}
