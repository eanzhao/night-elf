using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Xunit.Abstractions;

using NightElf.Contracts.System.AgentSession.Protobuf;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Launcher;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp.Tests;

public sealed class AedposMultiNodeClusterTests
{
    private readonly ITestOutputHelper _output;

    public AedposMultiNodeClusterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AedposCluster_Should_Discover_Peers_And_Keep_State_Consistent()
    {
        await using var cluster = await NightElfClusterTestHarness.CreateAsync(nodeCount: 3);
        await cluster.WaitForPeerDiscoveryAsync(expectedPeerCount: 2);
        await cluster.WaitForChainHeightAsync(4);

        var submitNode = cluster.GetNode("validator-b");
        var submitClient = submitNode.Harness.CreateGrpcClient();
        var contractAddress = await submitNode.Harness.GetSystemContractAddressAsync("AgentSession");
        var submitReference = await submitNode.Harness.WaitForChainHeightAsync(4);

        var transactionEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            submitReference.BestChainHeight,
            submitReference.BestChainHash.ToHex(),
            contractAddress,
            "OpenSession",
            seedMarker: 0xD1,
            payloadFactory: senderAddress => OpenSessionInput.Encode(new OpenSessionInput
            {
                AgentAddress = senderAddress,
                TokenBudget = 77
            }));

        var submitResult = await submitClient.SubmitTransactionAsync(transactionEnvelope.Transaction).ResponseAsync;
        var localMined = await submitNode.Harness.WaitForTransactionStatusAsync(
            submitResult.TransactionId,
            TransactionExecutionStatus.Mined,
            timeout: TimeSpan.FromSeconds(15));
        var minedAcrossNodes = await cluster.WaitForTransactionStatusOnAllNodesAsync(
            submitResult.TransactionId,
            TransactionExecutionStatus.Mined,
            timeout: TimeSpan.FromSeconds(15));
        var sessionId = await ResolveSessionIdAsync(submitNode.Harness, transactionEnvelope.SenderAddress, localMined);
        var sessionStates = await Task.WhenAll(
            cluster.Nodes.Select(node => node.Harness.GetSessionStateAsync(sessionId)).ToArray());
        var minedBlocks = await Task.WhenAll(
            cluster.Nodes.Select(node => node.Harness.WaitForBlockByHeightAsync(localMined.BlockHeight)).ToArray());

        Assert.All(
            minedAcrossNodes,
            result => Assert.Equal(TransactionExecutionStatus.Mined, result.Status));
        Assert.All(
            sessionStates,
            state =>
            {
                Assert.NotNull(state);
                Assert.Equal(77, state!.TokenBudget);
                Assert.Equal(transactionEnvelope.SenderAddress.ToHex(), state.AgentAddress.ToHex());
            });
        Assert.True(
            sessionStates
                .Skip(1)
                .All(state => SessionState.Encode(state!).SequenceEqual(SessionState.Encode(sessionStates[0]!))));
        Assert.True(
            minedBlocks
                .Skip(1)
                .All(block =>
                    NightElfTransactionTestBuilder.ComputeBlockHashHex(block) ==
                    NightElfTransactionTestBuilder.ComputeBlockHashHex(minedBlocks[0])));
        Assert.All(
            minedBlocks,
            block =>
            {
                Assert.True(block.Header.ExtraData["random_seed"].Length > 0);
                Assert.True(block.Header.ExtraData["randomness"].Length > 0);
            });
        Assert.NotEqual(
            minedBlocks[0].Header.ExtraData["random_seed"].ToByteArray(),
            minedBlocks[0].Header.ExtraData["randomness"].ToByteArray());

        _output.WriteLine(
            "AEDPoS 3-node mined tx {0} at height {1}; peer counts: {2}.",
            submitResult.TransactionId.ToHex(),
            localMined.BlockHeight,
            string.Join(
                ", ",
                cluster.Nodes.Select(node =>
                    $"{node.NodeId}={node.Harness.GetRequiredService<ClusterPeerRegistry>().GetPeers().Count}")));
    }

    [Fact]
    public async Task AedposCluster_Should_Report_MultiNode_Baseline()
    {
        await using var cluster = await NightElfClusterTestHarness.CreateAsync(nodeCount: 2);
        await cluster.WaitForPeerDiscoveryAsync(expectedPeerCount: 1);
        await cluster.WaitForChainHeightAsync(3);

        var submitNode = cluster.GetNode("validator-b");
        var client = submitNode.Harness.CreateGrpcClient();
        var contractAddress = await submitNode.Harness.GetSystemContractAddressAsync("AgentSession");
        var reference = await submitNode.Harness.WaitForChainHeightAsync(3);

        const int transactionCount = 12;
        var envelopes = Enumerable.Range(0, transactionCount)
            .Select(index => NightElfTransactionTestBuilder.CreateSignedTransaction(
                reference.BestChainHeight,
                reference.BestChainHash.ToHex(),
                contractAddress,
                "OpenSession",
                seedMarker: (byte)(0xE0 + index),
                payloadFactory: senderAddress => OpenSessionInput.Encode(new OpenSessionInput
                {
                    AgentAddress = senderAddress,
                    TokenBudget = 50 + index
                })))
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        var submitResults = new List<TransactionResult>(transactionCount);
        foreach (var envelope in envelopes)
        {
            submitResults.Add(await client.SubmitTransactionAsync(envelope.Transaction).ResponseAsync);
        }

        foreach (var submitResult in submitResults)
        {
            await cluster.WaitForTransactionStatusOnAllNodesAsync(
                submitResult.TransactionId,
                TransactionExecutionStatus.Mined,
                timeout: TimeSpan.FromSeconds(15));
        }

        stopwatch.Stop();
        var tps = transactionCount / stopwatch.Elapsed.TotalSeconds;

        _output.WriteLine(
            "AEDPoS multi-node baseline: {0:F2} tx/s over {1} OpenSession transactions in {2:F3}s.",
            tps,
            transactionCount,
            stopwatch.Elapsed.TotalSeconds);

        Assert.True(tps > 0);
    }

    private static async Task<Hash> ResolveSessionIdAsync(
        NightElfNodeTestHarness harness,
        Address senderAddress,
        TransactionResult minedResult)
    {
        var block = await harness.WaitForBlockByHeightAsync(minedResult.BlockHeight);
        var transactionIndex = block.Body.TransactionIds
            .Select((transactionId, index) => new { TransactionId = transactionId.ToHex(), Index = index })
            .Single(item => item.TransactionId == minedResult.TransactionId.ToHex())
            .Index;

        return NightElfTransactionTestBuilder.ComputeAgentSessionId(
            senderAddress,
            minedResult.BlockHeight,
            transactionIndex);
    }

    private sealed class NightElfClusterTestHarness : IAsyncDisposable
    {
        private readonly string _rootPath;

        private NightElfClusterTestHarness(
            string rootPath,
            IReadOnlyList<ClusterNodeHandle> nodes)
        {
            _rootPath = rootPath;
            Nodes = nodes;
        }

        public IReadOnlyList<ClusterNodeHandle> Nodes { get; }

        public static async Task<NightElfClusterTestHarness> CreateAsync(int nodeCount)
        {
            if (nodeCount is < 2 or > 3)
            {
                throw new InvalidOperationException("AEDPoS cluster tests currently support 2 or 3 nodes.");
            }

            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "nightelf-aedpos-cluster-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);

            var validatorIds = Enumerable.Range(0, nodeCount)
                .Select(index => $"validator-{(char)('a' + index)}")
                .ToArray();
            var nodeDefinitions = validatorIds
                .Select((nodeId, index) => new ClusterNodeDefinition(
                    nodeId,
                    Path.Combine(rootPath, nodeId),
                    GetAvailableTcpPort(),
                    GetAvailableTcpPort(),
                    GetAvailableTcpPort(),
                    index))
                .ToArray();

            var chainId = 9900000 + Random.Shared.Next(1, 1000);
            var genesisTimestampUtc = new DateTimeOffset(2026, 4, 16, 8, 0, 0, TimeSpan.Zero);

            var nodes = new List<ClusterNodeHandle>(nodeCount);
            foreach (var nodeDefinition in nodeDefinitions)
            {
                Directory.CreateDirectory(nodeDefinition.RootPath);
                var harness = await NightElfNodeTestHarness.CreateAsync(
                    rootPath: nodeDefinition.RootPath,
                    configurationOverrides: CreateNodeConfiguration(
                        nodeDefinition,
                        nodeDefinitions,
                        validatorIds,
                        chainId,
                        genesisTimestampUtc)).ConfigureAwait(false);

                nodes.Add(new ClusterNodeHandle(nodeDefinition.NodeId, harness));
            }

            return new NightElfClusterTestHarness(rootPath, nodes);
        }

        public ClusterNodeHandle GetNode(string nodeId)
        {
            return Nodes.Single(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal));
        }

        public async Task WaitForPeerDiscoveryAsync(
            int expectedPeerCount,
            TimeSpan? timeout = null)
        {
            var startedAt = DateTime.UtcNow;
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);

            while (true)
            {
                if (Nodes.All(node =>
                        node.Harness.GetRequiredService<ClusterPeerRegistry>().GetPeers().Count >= expectedPeerCount))
                {
                    return;
                }

                if (DateTime.UtcNow - startedAt > effectiveTimeout)
                {
                    throw new TimeoutException($"Timed out waiting for cluster peer discovery count {expectedPeerCount}.");
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        public async Task WaitForChainHeightAsync(long minimumHeight)
        {
            await Task.WhenAll(Nodes.Select(node => node.Harness.WaitForChainHeightAsync(minimumHeight))).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<TransactionResult>> WaitForTransactionStatusOnAllNodesAsync(
            Hash transactionId,
            TransactionExecutionStatus expectedStatus,
            TimeSpan? timeout = null)
        {
            return await Task.WhenAll(
                    Nodes.Select(node => node.Harness.WaitForTransactionStatusAsync(
                        transactionId,
                        expectedStatus,
                        timeout)))
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in Nodes.Reverse())
            {
                await node.Harness.DisposeAsync().ConfigureAwait(false);
            }

            if (Directory.Exists(_rootPath))
            {
                try
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }

        private static Dictionary<string, string?> CreateNodeConfiguration(
            ClusterNodeDefinition nodeDefinition,
            IReadOnlyList<ClusterNodeDefinition> allNodes,
            IReadOnlyList<string> validatorIds,
            int chainId,
            DateTimeOffset genesisTimestampUtc)
        {
            var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["NightElf:Launcher:NodeId"] = nodeDefinition.NodeId,
                ["NightElf:Launcher:ApiPort"] = nodeDefinition.ApiPort.ToString(),
                ["NightElf:Launcher:DataRootPath"] = Path.Combine(nodeDefinition.RootPath, "data"),
                ["NightElf:Launcher:CheckpointRootPath"] = Path.Combine(nodeDefinition.RootPath, "checkpoints"),
                ["NightElf:Launcher:Network:Host"] = IPAddress.Loopback.ToString(),
                ["NightElf:Launcher:Network:GrpcPort"] = nodeDefinition.GrpcPort.ToString(),
                ["NightElf:Launcher:Network:QuicPort"] = nodeDefinition.QuicPort.ToString(),
                ["NightElf:Launcher:Network:JoinRetryDelay"] = "00:00:00.100",
                ["NightElf:Launcher:Network:JoinMaxAttempts"] = "50",
                ["NightElf:Launcher:Genesis:ChainId"] = chainId.ToString(),
                ["NightElf:Launcher:Genesis:TimestampUtc"] = genesisTimestampUtc.ToString("O"),
                ["NightElf:Consensus:Engine"] = "Aedpos",
                ["NightElf:Consensus:Aedpos:BlockInterval"] = "00:00:00.050",
                ["NightElf:Consensus:Aedpos:BlocksPerRound"] = validatorIds.Count.ToString(),
                ["NightElf:Consensus:Aedpos:IrreversibleBlockDistance"] = "2",
                ["NightElf:Network:BlockSyncTransport"] = "Quic",
                ["NightElf:Network:TransactionBroadcastTransport"] = "Quic",
                ["NightElf:TransactionPool:Capacity"] = "4096",
                ["NightElf:TransactionPool:DefaultBatchSize"] = "128",
                ["NightElf:TransactionPool:ReferenceBlockValidPeriod"] = "512"
            };

            for (var index = 0; index < validatorIds.Count; index++)
            {
                configuration[$"NightElf:Consensus:Aedpos:Validators:{index}"] = validatorIds[index];
                configuration[$"NightElf:Launcher:Genesis:Validators:{index}"] = validatorIds[index];
            }

            for (var index = validatorIds.Count; index < 8; index++)
            {
                configuration[$"NightElf:Consensus:Aedpos:Validators:{index}"] = string.Empty;
                configuration[$"NightElf:Launcher:Genesis:Validators:{index}"] = string.Empty;
            }

            var peerIndex = 0;
            foreach (var peer in allNodes.Where(peer => !string.Equals(peer.NodeId, nodeDefinition.NodeId, StringComparison.Ordinal)))
            {
                configuration[$"NightElf:Launcher:Network:Peers:{peerIndex}:NodeId"] = peer.NodeId;
                configuration[$"NightElf:Launcher:Network:Peers:{peerIndex}:Host"] = IPAddress.Loopback.ToString();
                configuration[$"NightElf:Launcher:Network:Peers:{peerIndex}:GrpcPort"] = peer.GrpcPort.ToString();
                configuration[$"NightElf:Launcher:Network:Peers:{peerIndex}:QuicPort"] = peer.QuicPort.ToString();
                peerIndex++;
            }

            return configuration;
        }

        private static int GetAvailableTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed record ClusterNodeDefinition(
        string NodeId,
        string RootPath,
        int ApiPort,
        int GrpcPort,
        int QuicPort,
        int Order);

    public sealed record ClusterNodeHandle(
        string NodeId,
        NightElfNodeTestHarness Harness);
}
