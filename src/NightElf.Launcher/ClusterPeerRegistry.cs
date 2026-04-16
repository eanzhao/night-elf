using NightElf.OS.Network;

namespace NightElf.Launcher;

public sealed class ClusterPeerRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, NetworkNodeEndpoint> _peersByNodeId = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<NetworkNodeEndpoint> _seedPeers;

    private NetworkNodeEndpoint? _localNode;

    public ClusterPeerRegistry(LauncherOptions launcherOptions)
    {
        ArgumentNullException.ThrowIfNull(launcherOptions);
        _seedPeers = launcherOptions.Network.Peers
            .Select(static peer => peer.ToEndpoint())
            .ToArray();
    }

    public void AttachLocalNode(NetworkNodeEndpoint localNode)
    {
        ArgumentNullException.ThrowIfNull(localNode);
        localNode.Validate();

        lock (_lock)
        {
            _localNode = localNode;
            _peersByNodeId.Remove(localNode.NodeId);
        }
    }

    public void RegisterPeer(NetworkNodeEndpoint peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        peer.Validate();

        lock (_lock)
        {
            if (_localNode is not null &&
                string.Equals(_localNode.NodeId, peer.NodeId, StringComparison.Ordinal))
            {
                return;
            }

            _peersByNodeId[peer.NodeId] = peer;
        }
    }

    public IReadOnlyList<NetworkNodeEndpoint> GetSeedPeers()
    {
        lock (_lock)
        {
            return _seedPeers
                .Where(static peer => !string.IsNullOrWhiteSpace(peer.NodeId))
                .Where(peer => _localNode is null || !string.Equals(peer.NodeId, _localNode.NodeId, StringComparison.Ordinal))
                .ToArray();
        }
    }

    public IReadOnlyList<NetworkNodeEndpoint> GetPeers()
    {
        lock (_lock)
        {
            var peers = new Dictionary<string, NetworkNodeEndpoint>(StringComparer.Ordinal);
            foreach (var seedPeer in _seedPeers)
            {
                if (!string.IsNullOrWhiteSpace(seedPeer.NodeId))
                {
                    peers[seedPeer.NodeId] = seedPeer;
                }
            }

            foreach (var discoveredPeer in _peersByNodeId)
            {
                peers[discoveredPeer.Key] = discoveredPeer.Value;
            }

            return peers.Values
                .Where(peer => _localNode is null || !string.Equals(peer.NodeId, _localNode.NodeId, StringComparison.Ordinal))
                .OrderBy(static peer => peer.NodeId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool Contains(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        lock (_lock)
        {
            return _peersByNodeId.ContainsKey(nodeId) ||
                   _seedPeers.Any(peer => string.Equals(peer.NodeId, nodeId, StringComparison.Ordinal));
        }
    }
}
