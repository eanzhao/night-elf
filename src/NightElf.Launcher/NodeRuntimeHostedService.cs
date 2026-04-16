using System.Security.Cryptography;
using System.Text;

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
    private readonly IConsensusEngine _consensusEngine;
    private readonly ConsensusEngineOptions _consensusOptions;
    private readonly IBlockRepository _blockRepository;
    private readonly IChainStateStore _chainStateStore;
    private readonly IBlockProcessingPipeline _blockProcessingPipeline;
    private readonly INetworkTransportCoordinator _networkTransportCoordinator;
    private readonly INonCriticalEventBus _nonCriticalEventBus;

    private IDisposable? _telemetrySubscription;
    private Task<BlockProcessingResult>? _currentBlockCompletion;
    private NetworkNodeEndpoint? _localNode;

    public NodeRuntimeHostedService(
        ILogger<NodeRuntimeHostedService> logger,
        IHostApplicationLifetime applicationLifetime,
        LauncherModuleCatalog moduleCatalog,
        LauncherOptions launcherOptions,
        NightElfNodeStorage nodeStorage,
        IGenesisBlockService genesisBlockService,
        IConsensusEngine consensusEngine,
        ConsensusEngineOptions consensusOptions,
        IBlockRepository blockRepository,
        IChainStateStore chainStateStore,
        IBlockProcessingPipeline blockProcessingPipeline,
        INetworkTransportCoordinator networkTransportCoordinator,
        INonCriticalEventBus nonCriticalEventBus)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
        _moduleCatalog = moduleCatalog ?? throw new ArgumentNullException(nameof(moduleCatalog));
        _launcherOptions = launcherOptions ?? throw new ArgumentNullException(nameof(launcherOptions));
        _nodeStorage = nodeStorage ?? throw new ArgumentNullException(nameof(nodeStorage));
        _genesisBlockService = genesisBlockService ?? throw new ArgumentNullException(nameof(genesisBlockService));
        _consensusEngine = consensusEngine ?? throw new ArgumentNullException(nameof(consensusEngine));
        _consensusOptions = consensusOptions ?? throw new ArgumentNullException(nameof(consensusOptions));
        _blockRepository = blockRepository ?? throw new ArgumentNullException(nameof(blockRepository));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
        _blockProcessingPipeline = blockProcessingPipeline ?? throw new ArgumentNullException(nameof(blockProcessingPipeline));
        _networkTransportCoordinator = networkTransportCoordinator ?? throw new ArgumentNullException(nameof(networkTransportCoordinator));
        _nonCriticalEventBus = nonCriticalEventBus ?? throw new ArgumentNullException(nameof(nonCriticalEventBus));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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

            await _blockProcessingPipeline.StartAsync(stoppingToken).ConfigureAwait(false);

            var genesisResult = await _genesisBlockService.EnsureGenesisAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation(
                genesisResult.Created
                    ? "Genesis block created at {Height}:{Hash}."
                    : "Genesis already present at {Height}:{Hash}.",
                genesisResult.Block.Height,
                genesisResult.Block.Hash);

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

                var producedBlock = await ProduceNextBlockAsync().ConfigureAwait(false);
                producedBlocks++;

                _logger.LogInformation(
                    "Produced block {Height}:{Hash}.",
                    producedBlock.Height,
                    producedBlock.Hash);

                try
                {
                    await Task.Delay(_consensusOptions.Aedpos.BlockInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    private async Task<BlockReference> ProduceNextBlockAsync()
    {
        var previousBlock = await _chainStateStore.GetBestChainAsync().ConfigureAwait(false)
                           ?? throw new InvalidOperationException("Genesis must exist before block production can start.");

        var nextHeight = previousBlock.Height + 1;
        var roundNumber = ((nextHeight - 1) % _consensusOptions.Aedpos.BlocksPerRound) + 1;
        var termNumber = ((nextHeight - 1) / _consensusOptions.Aedpos.BlocksPerRound) + 1;
        var proposal = await _consensusEngine.ProposeBlockAsync(
                new ConsensusContext
                {
                    ExpectedHeight = nextHeight,
                    PreviousBlock = previousBlock,
                    LastIrreversibleBlock = await ResolveLastIrreversibleBlockAsync(
                            Math.Max(1, nextHeight - _consensusOptions.Aedpos.IrreversibleBlockDistance))
                        .ConfigureAwait(false),
                    RoundNumber = roundNumber,
                    TermNumber = termNumber,
                    ProposedAtUtc = DateTimeOffset.UtcNow,
                    RandomSeed = SHA256.HashData(Encoding.UTF8.GetBytes(previousBlock.Hash))
                })
            .ConfigureAwait(false);

        var validation = await _consensusEngine.ValidateBlockAsync(
                proposal,
                new ConsensusValidationContext
                {
                    ExpectedHeight = nextHeight,
                    PreviousBlock = previousBlock
                })
            .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Consensus validation failed for block {proposal.Block.Height}:{proposal.Block.Hash}: {validation.ErrorCode} {validation.ErrorMessage}");
        }

        var block = BlockModelFactory.CreateBlock(proposal, _launcherOptions.Genesis.ChainId);
        await _blockRepository.StoreAsync(proposal.Block, block).ConfigureAwait(false);

        var processingRequest = new BlockProcessingRequest
        {
            Block = proposal.Block,
            Writes = CreateStateWrites(proposal),
            AdvanceLibCheckpoint = false,
            Source = "launcher:block-loop"
        };

        var ticket = await _blockProcessingPipeline.EnqueueAsync(processingRequest).ConfigureAwait(false);
        _currentBlockCompletion = ticket.Completion;

        try
        {
            await ticket.Completion.ConfigureAwait(false);
        }
        finally
        {
            _currentBlockCompletion = null;
        }

        var lastIrreversibleBlock = await ResolveLastIrreversibleBlockAsync(proposal.LastIrreversibleBlockHeight).ConfigureAwait(false);
        await _consensusEngine.OnBlockCommittedAsync(
                new ConsensusCommitContext
                {
                    Block = proposal,
                    LastIrreversibleBlock = lastIrreversibleBlock
                })
            .ConfigureAwait(false);

        if (lastIrreversibleBlock is not null)
        {
            await _chainStateStore.AdvanceLibCheckpointAsync(lastIrreversibleBlock).ConfigureAwait(false);
        }

        return proposal.Block;
    }

    private async Task<BlockReference?> ResolveLastIrreversibleBlockAsync(long blockHeight)
    {
        if (blockHeight <= 0)
        {
            return null;
        }

        return await _blockRepository.GetBlockReferenceByHeightAsync(blockHeight).ConfigureAwait(false);
    }

    private Dictionary<string, byte[]> CreateStateWrites(ConsensusBlockProposal proposal)
    {
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"block:{proposal.Block.Height}:hash"] = Encoding.UTF8.GetBytes(proposal.Block.Hash),
            [$"block:{proposal.Block.Height}:producer"] = Encoding.UTF8.GetBytes(proposal.ProposerAddress),
            [$"block:{proposal.Block.Height}:round"] = Encoding.UTF8.GetBytes(proposal.RoundNumber.ToString()),
            [$"block:{proposal.Block.Height}:term"] = Encoding.UTF8.GetBytes(proposal.TermNumber.ToString()),
            [$"block:{proposal.Block.Height}:timestamp"] = Encoding.UTF8.GetBytes(proposal.TimestampUtc.ToString("O")),
            ["chain:last-produced-hash"] = Encoding.UTF8.GetBytes(proposal.Block.Hash)
        };
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
            "Initialized Tsavorite stores. block={BlockDataPath} state={StateDataPath} index={IndexDataPath}",
            _nodeStorage.BlockDatabase.DataPath,
            _nodeStorage.StateDatabase.DataPath,
            _nodeStorage.IndexDatabase.DataPath);
    }

    private async Task ShutdownAsync()
    {
        _logger.LogInformation("Launcher shutdown requested.");

        if (_currentBlockCompletion is not null)
        {
            try
            {
                await _currentBlockCompletion.WaitAsync(_launcherOptions.ShutdownTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Timed out waiting {Timeout} for the current block to finish during shutdown.",
                    _launcherOptions.ShutdownTimeout);
            }
        }

        var bestChain = await _chainStateStore.GetBestChainAsync().ConfigureAwait(false);
        if (bestChain is not null)
        {
            await _chainStateStore.AdvanceLibCheckpointAsync(bestChain).ConfigureAwait(false);
            _logger.LogInformation(
                "Flushed final checkpoint at {Height}:{Hash}.",
                bestChain.Height,
                bestChain.Hash);
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
