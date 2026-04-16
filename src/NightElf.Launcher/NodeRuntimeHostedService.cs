using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;
using NightElf.OS.Network;

namespace NightElf.Launcher;

public sealed class NodeRuntimeHostedService : BackgroundService
{
    private readonly ILogger<NodeRuntimeHostedService> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly LauncherModuleCatalog _moduleCatalog;
    private readonly LauncherOptions _launcherOptions;
    private readonly NightElfNodeStorage _nodeStorage;
    private readonly IGenesisBlockService _genesisBlockService;
    private readonly ConsensusEngineOptions _consensusOptions;
    private readonly IChainStateStore _chainStateStore;
    private readonly TransactionPoolOptions _transactionPoolOptions;
    private readonly IBlockProcessingPipeline _blockProcessingPipeline;
    private readonly INetworkTransportCoordinator _networkTransportCoordinator;
    private readonly INonCriticalEventBus _nonCriticalEventBus;
    private readonly ConsensusClusterCoordinator _clusterCoordinator;

    private IDisposable? _telemetrySubscription;
    private NetworkNodeEndpoint? _localNode;

    public NodeRuntimeHostedService(
        ILogger<NodeRuntimeHostedService> logger,
        IHostApplicationLifetime applicationLifetime,
        LauncherModuleCatalog moduleCatalog,
        LauncherOptions launcherOptions,
        NightElfNodeStorage nodeStorage,
        IGenesisBlockService genesisBlockService,
        ConsensusEngineOptions consensusOptions,
        IChainStateStore chainStateStore,
        TransactionPoolOptions transactionPoolOptions,
        IBlockProcessingPipeline blockProcessingPipeline,
        INetworkTransportCoordinator networkTransportCoordinator,
        INonCriticalEventBus nonCriticalEventBus,
        ConsensusClusterCoordinator clusterCoordinator)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
        _moduleCatalog = moduleCatalog ?? throw new ArgumentNullException(nameof(moduleCatalog));
        _launcherOptions = launcherOptions ?? throw new ArgumentNullException(nameof(launcherOptions));
        _nodeStorage = nodeStorage ?? throw new ArgumentNullException(nameof(nodeStorage));
        _genesisBlockService = genesisBlockService ?? throw new ArgumentNullException(nameof(genesisBlockService));
        _consensusOptions = consensusOptions ?? throw new ArgumentNullException(nameof(consensusOptions));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
        _transactionPoolOptions = transactionPoolOptions ?? throw new ArgumentNullException(nameof(transactionPoolOptions));
        _blockProcessingPipeline = blockProcessingPipeline ?? throw new ArgumentNullException(nameof(blockProcessingPipeline));
        _networkTransportCoordinator = networkTransportCoordinator ?? throw new ArgumentNullException(nameof(networkTransportCoordinator));
        _nonCriticalEventBus = nonCriticalEventBus ?? throw new ArgumentNullException(nameof(nonCriticalEventBus));
        _clusterCoordinator = clusterCoordinator ?? throw new ArgumentNullException(nameof(clusterCoordinator));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                SubscribeTelemetry();
                LogModuleLoadOrder();
                LogStorageInitialization();

                _localNode = await _networkTransportCoordinator
                    .StartAsync(_launcherOptions.CreateLocalNodeEndpoint(), stoppingToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Network transport started for node {NodeId} at grpc={GrpcPort}, quic={QuicPort}.",
                    _localNode.NodeId,
                    _localNode.GrpcPort,
                    _localNode.QuicPort);
                _clusterCoordinator.AttachLocalNode(_localNode);

                await _blockProcessingPipeline.StartAsync(stoppingToken).ConfigureAwait(false);
                await RecoverStateFromLatestCheckpointIfNeededAsync(stoppingToken).ConfigureAwait(false);

                var genesisResult = await _genesisBlockService.EnsureGenesisAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    genesisResult.Created
                        ? "Genesis block created at {Height}:{Hash}."
                        : "Genesis already present at {Height}:{Hash}.",
                    genesisResult.Block.Height,
                    genesisResult.Block.Hash);

                _logger.LogInformation(
                    "Starting cluster join for node {NodeId} with {PeerCount} configured peers.",
                    _launcherOptions.NodeId,
                    _launcherOptions.Network.Peers.Count);
                await _clusterCoordinator.JoinClusterAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Cluster join completed for node {NodeId}.",
                    _launcherOptions.NodeId);

                var producedBlocks = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (_launcherOptions.MaxProducedBlocks.HasValue &&
                        producedBlocks >= _launcherOptions.MaxProducedBlocks.Value)
                    {
                        _logger.LogInformation(
                            "Launcher reached MaxProducedBlocks={MaxProducedBlocks}; stopping host.",
                            _launcherOptions.MaxProducedBlocks.Value);
                        _applicationLifetime.StopApplication();
                        break;
                    }

                    var producedBlock = await _clusterCoordinator
                        .TryProduceNextBlockAsync("launcher:block-loop", stoppingToken)
                        .ConfigureAwait(false);
                    if (producedBlock is not null)
                    {
                        producedBlocks++;
                        _logger.LogInformation(
                            "Produced block {Height}:{Hash}.",
                            producedBlock.Height,
                            producedBlock.Hash);
                    }

                    try
                    {
                        await Task.Delay(_consensusOptions.GetBlockInterval(), stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug("Node runtime loop observed host cancellation.");
            }
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    private async Task RecoverStateFromLatestCheckpointIfNeededAsync(CancellationToken cancellationToken)
    {
        var bestChain = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false);
        if (bestChain is not null)
        {
            return;
        }

        var checkpoints = await _chainStateStore.GetLibCheckpointsAsync(cancellationToken).ConfigureAwait(false);
        if (checkpoints.Count == 0)
        {
            return;
        }

        await _chainStateStore.RecoverToLatestLibCheckpointAsync(cancellationToken).ConfigureAwait(false);
        var recoveredBestChain = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false);
        if (recoveredBestChain is not null)
        {
            _logger.LogInformation(
                "Recovered chain state from latest checkpoint at {Height}:{Hash}.",
                recoveredBestChain.Height,
                recoveredBestChain.Hash);
        }
    }

    private void SubscribeTelemetry()
    {
        _telemetrySubscription = _nonCriticalEventBus.Subscribe<BlockProcessingTelemetryEvent>((eventData, _) =>
        {
            _logger.LogInformation(
                "Pipeline telemetry {Kind} block={Height}:{Hash} backlog={Backlog} source={Source} details={Details}",
                eventData.Kind,
                eventData.Block.Height,
                eventData.Block.Hash,
                eventData.BacklogCount,
                eventData.Source,
                eventData.Details ?? string.Empty);

            return Task.CompletedTask;
        });
    }

    private void LogModuleLoadOrder()
    {
        var modules = _moduleCatalog.ResolveLoadOrder();

        foreach (var moduleType in modules)
        {
            _logger.LogInformation("Loaded module {ModuleName}.", moduleType.Name);
        }
    }

    private void LogStorageInitialization()
    {
        _logger.LogInformation(
            "Initialized Tsavorite stores. block={BlockDataPath} state={StateDataPath} index={IndexDataPath} txPoolCapacity={TransactionPoolCapacity} txBatchSize={TransactionPoolBatchSize}",
            _nodeStorage.BlockDatabase.DataPath,
            _nodeStorage.StateDatabase.DataPath,
            _nodeStorage.IndexDatabase.DataPath,
            _transactionPoolOptions.Capacity,
            _transactionPoolOptions.DefaultBatchSize);
    }

    private async Task ShutdownAsync()
    {
        _logger.LogInformation("Launcher shutdown requested.");

        try
        {
            await _clusterCoordinator.WaitForInflightAsync(_launcherOptions.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Timed out waiting {Timeout} for the current block to finish during shutdown.",
                _launcherOptions.ShutdownTimeout);
        }

        try
        {
            var bestChain = await _chainStateStore.GetBestChainAsync().ConfigureAwait(false);
            if (bestChain is not null)
            {
                await _chainStateStore.AdvanceLibCheckpointAsync(bestChain).ConfigureAwait(false);
                _logger.LogInformation(
                    "Flushed final checkpoint at {Height}:{Hash}.",
                    bestChain.Height,
                    bestChain.Hash);
            }
        }
        catch (ObjectDisposedException exception)
        {
            _logger.LogDebug(
                exception,
                "Skipped final checkpoint flush because the underlying storage has already been disposed.");
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to flush the final checkpoint during shutdown.");
        }

        try
        {
            await _blockProcessingPipeline.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Stopping the block processing pipeline raised an exception.");
        }

        await _networkTransportCoordinator.StopAsync().ConfigureAwait(false);
        _telemetrySubscription?.Dispose();
    }
}
