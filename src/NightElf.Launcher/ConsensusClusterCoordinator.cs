using System.Security.Cryptography;
using System.Text;

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NightElf.Contracts.System.AgentSession.Protobuf;
using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.OS.Network;
using NightElf.WebApp;
using NightElf.WebApp.Protobuf;
using ApiTransactionResult = NightElf.WebApp.Protobuf.TransactionResult;
using ChainTransactionResultStatus = NightElf.Kernel.Core.TransactionResultStatus;

namespace NightElf.Launcher;

public sealed class ConsensusClusterCoordinator : INetworkMessageSink
{
    private readonly ILogger<ConsensusClusterCoordinator> _logger;
    private readonly LauncherOptions _launcherOptions;
    private readonly ConsensusEngineOptions _consensusOptions;
    private readonly IConsensusEngine _consensusEngine;
    private readonly IBlockRepository _blockRepository;
    private readonly IChainStateStore _chainStateStore;
    private readonly ITransactionPool _transactionPool;
    private readonly TransactionPoolOptions _transactionPoolOptions;
    private readonly ITransactionResultStore _transactionResultStore;
    private readonly IBlockTransactionExecutionService _transactionExecutionService;
    private readonly IBlockProcessingPipeline _blockProcessingPipeline;
    private readonly INonCriticalEventBus _eventBus;
    private readonly ClusterPeerRegistry _peerRegistry;
    private readonly IServiceProvider _serviceProvider;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _operationLock = new();

    private NetworkNodeEndpoint? _localNode;
    private Task? _currentOperation;
    private long _lastSchedulingObservationHeight;
    private string? _lastSchedulingObservationProposer;

    public ConsensusClusterCoordinator(
        ILogger<ConsensusClusterCoordinator> logger,
        LauncherOptions launcherOptions,
        ConsensusEngineOptions consensusOptions,
        IConsensusEngine consensusEngine,
        IBlockRepository blockRepository,
        IChainStateStore chainStateStore,
        ITransactionPool transactionPool,
        TransactionPoolOptions transactionPoolOptions,
        ITransactionResultStore transactionResultStore,
        IBlockTransactionExecutionService transactionExecutionService,
        IBlockProcessingPipeline blockProcessingPipeline,
        INonCriticalEventBus eventBus,
        ClusterPeerRegistry peerRegistry,
        IServiceProvider serviceProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _launcherOptions = launcherOptions ?? throw new ArgumentNullException(nameof(launcherOptions));
        _consensusOptions = consensusOptions ?? throw new ArgumentNullException(nameof(consensusOptions));
        _consensusEngine = consensusEngine ?? throw new ArgumentNullException(nameof(consensusEngine));
        _blockRepository = blockRepository ?? throw new ArgumentNullException(nameof(blockRepository));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
        _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
        _transactionPoolOptions = transactionPoolOptions ?? throw new ArgumentNullException(nameof(transactionPoolOptions));
        _transactionResultStore = transactionResultStore ?? throw new ArgumentNullException(nameof(transactionResultStore));
        _transactionExecutionService = transactionExecutionService ?? throw new ArgumentNullException(nameof(transactionExecutionService));
        _blockProcessingPipeline = blockProcessingPipeline ?? throw new ArgumentNullException(nameof(blockProcessingPipeline));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _peerRegistry = peerRegistry ?? throw new ArgumentNullException(nameof(peerRegistry));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public void AttachLocalNode(NetworkNodeEndpoint localNode)
    {
        ArgumentNullException.ThrowIfNull(localNode);
        localNode.Validate();

        _localNode = localNode;
        _peerRegistry.AttachLocalNode(localNode);
    }

    public async Task JoinClusterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var localNode = RequireLocalNode();
        foreach (var peer in _peerRegistry.GetSeedPeers())
        {
            if (string.Equals(peer.NodeId, localNode.NodeId, StringComparison.Ordinal))
            {
                continue;
            }

            var joined = false;
            for (var attempt = 1; attempt <= _launcherOptions.Network.JoinMaxAttempts && !cancellationToken.IsCancellationRequested; attempt++)
            {
                var hello = await CreatePeerHelloAsync(localNode, isAck: false, cancellationToken).ConfigureAwait(false);

                try
                {
                    await GetNetworkTransportCoordinator().SendAsync(
                            NetworkScenario.Rpc,
                            peer,
                            ConsensusClusterMessageSerializer.Serialize(hello),
                            cancellationToken)
                        .ConfigureAwait(false);
                    _peerRegistry.RegisterPeer(peer);
                    joined = true;
                    break;
                }
                catch (Exception exception) when (attempt < _launcherOptions.Network.JoinMaxAttempts)
                {
                    _logger.LogDebug(
                        exception,
                        "Join attempt {Attempt}/{MaxAttempts} to peer {PeerNodeId} failed.",
                        attempt,
                        _launcherOptions.Network.JoinMaxAttempts,
                        peer.NodeId);

                    await Task.Delay(_launcherOptions.Network.JoinRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to join peer {PeerNodeId} after {MaxAttempts} attempts.",
                        peer.NodeId,
                        _launcherOptions.Network.JoinMaxAttempts);
                }
            }

            if (joined)
            {
                _logger.LogInformation("Joined peer {PeerNodeId}.", peer.NodeId);
            }
        }
    }

    public async Task<BlockReference?> TryProduceNextBlockAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return await TrackAsync(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var previousBlock = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false)
                                   ?? throw new InvalidOperationException("Genesis must exist before block production can start.");

                var nextHeight = previousBlock.Height + 1;
                var (roundNumber, termNumber) = _consensusOptions.ResolveRoundAndTerm(nextHeight);

                string? proposerAddress = null;
                if (_consensusOptions.ResolveEngineKind() == ConsensusEngineKind.Aedpos)
                {
                    var validators = await _consensusEngine.GetValidatorsAsync(
                            new ConsensusValidatorQuery
                            {
                                RoundNumber = roundNumber,
                                TermNumber = termNumber
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    var scheduledProposer = validators[0].Address;
                    var localNode = RequireLocalNode();
                    if (!string.Equals(localNode.NodeId, scheduledProposer, StringComparison.Ordinal))
                    {
                        LogSchedulingObservation(nextHeight, scheduledProposer, localNode.NodeId, willProduce: false);
                        return null;
                    }

                    LogSchedulingObservation(nextHeight, scheduledProposer, localNode.NodeId, willProduce: true);
                    proposerAddress = scheduledProposer;
                }

                var transactions = await _transactionPool
                    .TakeBatchAsync(_transactionPoolOptions.DefaultBatchSize, cancellationToken)
                    .ConfigureAwait(false);

                var proposal = await _consensusEngine.ProposeBlockAsync(
                        new ConsensusContext
                        {
                            ExpectedHeight = nextHeight,
                            PreviousBlock = previousBlock,
                            LastIrreversibleBlock = await ResolveLastIrreversibleBlockAsync(
                                    _consensusOptions.ResolveLastIrreversibleBlockHeightHint(nextHeight),
                                    cancellationToken)
                                .ConfigureAwait(false),
                            ProposerAddress = proposerAddress,
                            RoundNumber = roundNumber,
                            TermNumber = termNumber,
                            ProposedAtUtc = DateTimeOffset.UtcNow,
                            RandomSeed = SHA256.HashData(Encoding.UTF8.GetBytes(previousBlock.Hash))
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                await ValidateProposalAsync(proposal, previousBlock, cancellationToken).ConfigureAwait(false);
                await AcceptProposalCoreAsync(
                        proposal,
                        transactions,
                        source,
                        broadcastToPeers: true,
                        cancellationToken)
                    .ConfigureAwait(false);

                return proposal.Block;
            }
            finally
            {
                _gate.Release();
            }
        }).ConfigureAwait(false);
    }

    public async Task WaitForInflightAsync(TimeSpan timeout)
    {
        Task? currentOperation;
        lock (_operationLock)
        {
            currentOperation = _currentOperation;
        }

        if (currentOperation is not null)
        {
            await currentOperation.WaitAsync(timeout).ConfigureAwait(false);
        }
    }

    public Task HandleAsync(
        NetworkMessageDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        return TrackAsync(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                switch (delivery.Envelope.Scenario)
                {
                    case NetworkScenario.Rpc:
                        await HandlePeerHelloAsync(
                                ConsensusClusterMessageSerializer.DeserializePeerHello(delivery.Envelope.Payload),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case NetworkScenario.BlockSync:
                        await HandleBlockSyncAsync(
                                ConsensusClusterMessageSerializer.DeserializeBlockSync(delivery.Envelope.Payload),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case NetworkScenario.TransactionBroadcast:
                        await HandleTransactionBroadcastAsync(
                                ConsensusClusterMessageSerializer.DeserializeTransactionBroadcast(delivery.Envelope.Payload),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported network scenario '{delivery.Envelope.Scenario}'.");
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private async Task HandlePeerHelloAsync(
        PeerHelloMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var peer = message.Node.ToEndpoint();
        _peerRegistry.RegisterPeer(peer);

        await SyncPeerToLocalBestAsync(peer, message.BestChainHeight, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleBlockSyncAsync(
        BlockSyncMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var previousBlock = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false)
                           ?? throw new InvalidOperationException("Genesis must exist before block sync can start.");

        if (message.Proposal.Block.Height <= previousBlock.Height)
        {
            return;
        }

        if (message.Proposal.Block.Height != previousBlock.Height + 1)
        {
            _logger.LogDebug(
                "Ignoring out-of-order block {Height}:{Hash} from {SourceNodeId}; local best is {BestHeight}:{BestHash}.",
                message.Proposal.Block.Height,
                message.Proposal.Block.Hash,
                message.SourceNodeId,
                previousBlock.Height,
                previousBlock.Hash);
            return;
        }

        await ValidateProposalAsync(message.Proposal, previousBlock, cancellationToken).ConfigureAwait(false);
        var transactions = message.Transactions
            .Select(Transaction.Parser.ParseFrom)
            .ToArray();

        await AcceptProposalCoreAsync(
                message.Proposal,
                transactions,
                "network:block-sync",
                broadcastToPeers: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleTransactionBroadcastAsync(
        TransactionBroadcastMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var transaction = Transaction.Parser.ParseFrom(message.TransactionBytes);
        var transactionId = transaction.GetTransactionId();

        if (!transaction.VerifyCoreFields(out var fieldError))
        {
            await _transactionResultStore.RecordRejectedAsync(transactionId, fieldError, cancellationToken).ConfigureAwait(false);
            await PublishTransactionEventAsync(
                    TransactionResultProtoConverter.CreateRejected(transactionId, fieldError),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!transaction.VerifyEd25519Signature(out var signatureError))
        {
            await _transactionResultStore.RecordRejectedAsync(transactionId, signatureError, cancellationToken).ConfigureAwait(false);
            await PublishTransactionEventAsync(
                    TransactionResultProtoConverter.CreateRejected(transactionId, signatureError),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var submitResult = await _transactionPool.SubmitAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (submitResult.IsAccepted)
        {
            await _transactionResultStore.RecordPendingAsync(transaction, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (submitResult.Status == TransactionPoolSubmitStatus.Duplicate)
        {
            return;
        }

        await _transactionResultStore.RecordRejectedAsync(transactionId, submitResult.Error, cancellationToken).ConfigureAwait(false);
        await PublishTransactionEventAsync(
                TransactionResultProtoConverter.CreateRejected(transactionId, submitResult.Error),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AcceptProposalCoreAsync(
        ConsensusBlockProposal proposal,
        IReadOnlyList<Transaction> transactions,
        string source,
        bool broadcastToPeers,
        CancellationToken cancellationToken)
    {
        var block = BlockModelFactory.CreateBlock(proposal, _launcherOptions.Genesis.ChainId, transactions);
        await _blockRepository.StoreAsync(proposal.Block, block, cancellationToken).ConfigureAwait(false);

        var executionResult = await _transactionExecutionService
            .ExecuteAsync(
                transactions,
                proposal.Block,
                proposal.TimestampUtc,
                cancellationToken)
            .ConfigureAwait(false);

        var processingRequest = new BlockProcessingRequest
        {
            Block = proposal.Block,
            Writes = CreateStateWrites(proposal, executionResult.Writes),
            Deletes = executionResult.Deletes,
            AdvanceLibCheckpoint = false,
            Source = source
        };

        var ticket = await _blockProcessingPipeline.EnqueueAsync(processingRequest, cancellationToken).ConfigureAwait(false);
        await ticket.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);

        await _transactionPool.RemoveAsync(transactions, cancellationToken).ConfigureAwait(false);

        await _eventBus.PublishAsync(
                new ChainSettlementEventEnvelope(
                    EventId: $"block:{proposal.Block.Hash}",
                    EventType: ChainEventType.BlockAccepted,
                    OccurredAtUtc: proposal.TimestampUtc,
                    BlockHeight: proposal.Block.Height,
                    BlockHash: proposal.Block.Hash,
                    Payload: Encoding.UTF8.GetBytes($"{proposal.Block.Height}:{proposal.Block.Hash}"),
                    Message: "Block accepted"),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var outcome in executionResult.Outcomes)
        {
            await _transactionResultStore
                .RecordBlockResultAsync(
                    outcome.Transaction,
                    proposal.Block,
                    outcome.Status,
                    outcome.Error,
                    cancellationToken)
                .ConfigureAwait(false);

            var transactionResult = TransactionResultProtoConverter.Create(
                outcome.Transaction.GetTransactionId(),
                outcome.Status,
                outcome.Error,
                proposal.Block);
            await _eventBus.PublishAsync(
                    new ChainSettlementEventEnvelope(
                        EventId: $"tx:{outcome.Transaction.GetTransactionId()}:{transactionResult.Status}",
                        EventType: ChainEventType.TransactionResult,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        BlockHeight: proposal.Block.Height,
                        BlockHash: proposal.Block.Hash,
                        TransactionId: outcome.Transaction.GetTransactionId(),
                        ContractAddress: outcome.Transaction.To.ToHex(),
                        Payload: transactionResult.ToByteArray(),
                        Message: outcome.Error ?? transactionResult.Status.ToString()),
                    cancellationToken)
                .ConfigureAwait(false);

            await PublishTokenMeteringEventAsync(
                    outcome,
                    proposal.Block,
                    executionResult.Writes,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var lastIrreversibleBlock = await ResolveLastIrreversibleBlockAsync(
                proposal.LastIrreversibleBlockHeight,
                cancellationToken)
            .ConfigureAwait(false);
        await _consensusEngine.OnBlockCommittedAsync(
                new ConsensusCommitContext
                {
                    Block = proposal,
                    LastIrreversibleBlock = lastIrreversibleBlock
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (lastIrreversibleBlock is not null)
        {
            await _chainStateStore.AdvanceLibCheckpointAsync(lastIrreversibleBlock, cancellationToken).ConfigureAwait(false);
        }

        if (broadcastToPeers)
        {
            await BroadcastBlockSyncAsync(proposal, transactions, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BroadcastBlockSyncAsync(
        ConsensusBlockProposal proposal,
        IReadOnlyList<Transaction> transactions,
        CancellationToken cancellationToken)
    {
        var localNode = RequireLocalNode();
        var payload = ConsensusClusterMessageSerializer.Serialize(
            new BlockSyncMessage
            {
                SourceNodeId = localNode.NodeId,
                Proposal = proposal,
                Transactions = transactions
                    .Select(static transaction => transaction.ToByteArray())
                    .ToList()
            });

        foreach (var peer in _peerRegistry.GetPeers())
        {
            try
            {
                await GetNetworkTransportCoordinator().SendAsync(
                        NetworkScenario.BlockSync,
                        peer,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Failed to broadcast block {Height}:{Hash} to peer {PeerNodeId}.",
                    proposal.Block.Height,
                    proposal.Block.Hash,
                    peer.NodeId);
            }
        }
    }

    private async Task ValidateProposalAsync(
        ConsensusBlockProposal proposal,
        BlockReference previousBlock,
        CancellationToken cancellationToken)
    {
        var validators = await _consensusEngine.GetValidatorsAsync(
                new ConsensusValidatorQuery
                {
                    RoundNumber = proposal.RoundNumber,
                    TermNumber = proposal.TermNumber
                },
                cancellationToken)
            .ConfigureAwait(false);

        var validation = await _consensusEngine.ValidateBlockAsync(
                proposal,
                new ConsensusValidationContext
                {
                    ExpectedHeight = previousBlock.Height + 1,
                    PreviousBlock = previousBlock,
                    ExpectedValidators = validators.Select(static validator => validator.Address).ToArray()
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Consensus validation failed for block {proposal.Block.Height}:{proposal.Block.Hash}: {validation.ErrorCode} {validation.ErrorMessage}");
        }
    }

    private async Task<PeerHelloMessage> CreatePeerHelloAsync(
        NetworkNodeEndpoint localNode,
        bool isAck,
        CancellationToken cancellationToken)
    {
        var bestChain = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false);
        return new PeerHelloMessage
        {
            Node = LauncherPeerOptions.FromEndpoint(localNode),
            IsAck = isAck,
            BestChainHeight = bestChain?.Height ?? 0,
            BestChainHash = bestChain?.Hash ?? string.Empty
        };
    }

    private async Task<BlockReference?> ResolveLastIrreversibleBlockAsync(
        long blockHeight,
        CancellationToken cancellationToken)
    {
        if (blockHeight <= 0)
        {
            return null;
        }

        return await _blockRepository.GetBlockReferenceByHeightAsync(blockHeight, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishTokenMeteringEventAsync(
        BlockTransactionExecutionOutcome outcome,
        BlockReference block,
        IReadOnlyDictionary<string, byte[]> executionWrites,
        CancellationToken cancellationToken)
    {
        if (outcome.Status != ChainTransactionResultStatus.Mined ||
            !string.Equals(outcome.Transaction.MethodName, "RecordStep", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var input = RecordStepInput.Parser.ParseFrom(outcome.Transaction.Params);
            var stateKey = CreateStepRecordedStateKey(input.SessionId, input.StepContentHash);
            if (!executionWrites.TryGetValue(stateKey, out var payload))
            {
                return;
            }

            var stepRecorded = StepRecorded.Parser.ParseFrom(payload);
            await _eventBus.PublishAsync(
                    new ChainSettlementEventEnvelope(
                        EventId: $"meter:{outcome.Transaction.GetTransactionId()}:{stepRecorded.StepContentHash.ToHex()}",
                        EventType: ChainEventType.TokenMetered,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        BlockHeight: block.Height,
                        BlockHash: block.Hash,
                        TransactionId: outcome.Transaction.GetTransactionId(),
                        ContractAddress: outcome.Transaction.To.ToHex(),
                        StateKey: stateKey,
                        Payload: payload,
                        Message: stepRecorded.MeteringSource.ToString()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to publish token metering event for transaction {TransactionId}.",
                outcome.Transaction.GetTransactionId());
        }
    }

    private async Task SyncPeerToLocalBestAsync(
        NetworkNodeEndpoint peer,
        long peerBestHeight,
        CancellationToken cancellationToken)
    {
        try
        {
            var localBest = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false);
            if (localBest is null || localBest.Height <= peerBestHeight)
            {
                return;
            }

            for (var height = peerBestHeight + 1; height <= localBest.Height; height++)
            {
                var blockReference = await _blockRepository
                    .GetBlockReferenceByHeightAsync(height, cancellationToken)
                    .ConfigureAwait(false);
                if (blockReference is null)
                {
                    break;
                }

                var block = await _blockRepository.GetByHashAsync(blockReference.Hash, cancellationToken).ConfigureAwait(false);
                if (block is null)
                {
                    break;
                }

                if (block.Body.TransactionIds.Count > 0)
                {
                    _logger.LogDebug(
                        "Skipping catch-up sync for block {Height}:{Hash} to peer {PeerNodeId} because transaction bodies are not persisted yet.",
                        blockReference.Height,
                        blockReference.Hash,
                        peer.NodeId);
                    break;
                }

                await GetNetworkTransportCoordinator().SendAsync(
                        NetworkScenario.BlockSync,
                        peer,
                        ConsensusClusterMessageSerializer.Serialize(
                            new BlockSyncMessage
                            {
                                SourceNodeId = RequireLocalNode().NodeId,
                                Proposal = RehydrateProposal(blockReference, block),
                                Transactions = []
                            }),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException exception)
        {
            _logger.LogDebug(
                exception,
                "Skipping peer catch-up sync for {PeerNodeId} because node storage is already disposing.",
                peer.NodeId);
        }
        catch (InvalidOperationException exception) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                exception,
                "Skipping peer catch-up sync for {PeerNodeId} because shutdown is already in progress.",
                peer.NodeId);
        }
    }

    private static Dictionary<string, byte[]> CreateStateWrites(
        ConsensusBlockProposal proposal,
        IReadOnlyDictionary<string, byte[]>? executionWrites)
    {
        var writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"block:{proposal.Block.Height}:hash"] = Encoding.UTF8.GetBytes(proposal.Block.Hash),
            [$"block:{proposal.Block.Height}:producer"] = Encoding.UTF8.GetBytes(proposal.ProposerAddress),
            [$"block:{proposal.Block.Height}:round"] = Encoding.UTF8.GetBytes(proposal.RoundNumber.ToString()),
            [$"block:{proposal.Block.Height}:term"] = Encoding.UTF8.GetBytes(proposal.TermNumber.ToString()),
            [$"block:{proposal.Block.Height}:timestamp"] = Encoding.UTF8.GetBytes(proposal.TimestampUtc.ToString("O")),
            ["chain:last-produced-hash"] = Encoding.UTF8.GetBytes(proposal.Block.Hash)
        };

        if (executionWrites is not null)
        {
            foreach (var write in executionWrites)
            {
                writes[write.Key] = write.Value;
            }
        }

        return writes;
    }

    private static string CreateStepRecordedStateKey(Hash sessionId, Hash stepContentHash)
    {
        return $"session:{sessionId.ToHex()}:event:step:{stepContentHash.ToHex()}";
    }

    private ConsensusBlockProposal RehydrateProposal(
        BlockReference blockReference,
        Block block)
    {
        ArgumentNullException.ThrowIfNull(blockReference);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(block.Header);

        return new ConsensusBlockProposal
        {
            Block = blockReference,
            ParentBlockHash = block.Header.PreviousBlockHash.Value.IsEmpty
                ? "GENESIS"
                : block.Header.PreviousBlockHash.ToHex(),
            ProposerAddress = GetRequiredExtraDataUtf8(block, "proposer"),
            RoundNumber = long.Parse(GetRequiredExtraDataUtf8(block, "round")),
            TermNumber = long.Parse(GetRequiredExtraDataUtf8(block, "term")),
            LastIrreversibleBlockHeight = _consensusOptions.ResolveLastIrreversibleBlockHeightHint(blockReference.Height),
            TimestampUtc = block.Header.Time.ToDateTimeOffset(),
            RandomSeed = GetRequiredExtraDataBytes(block, "random_seed"),
            Randomness = GetRequiredExtraDataBytes(block, "randomness"),
            VrfProof = block.Header.Signature.ToByteArray(),
            ConsensusData = GetRequiredExtraDataBytes(block, "consensus")
        };
    }

    private static byte[] GetRequiredExtraDataBytes(Block block, string key)
    {
        if (!block.Header.ExtraData.TryGetValue(key, out var value))
        {
            throw new InvalidOperationException(
                $"Block {block.Header.Height} is missing required extra data '{key}' for cluster sync.");
        }

        return value.ToByteArray();
    }

    private static string GetRequiredExtraDataUtf8(Block block, string key)
    {
        return Encoding.UTF8.GetString(GetRequiredExtraDataBytes(block, key));
    }

    private Task PublishTransactionEventAsync(
        ApiTransactionResult result,
        CancellationToken cancellationToken)
    {
        return _eventBus.PublishAsync(
            new ChainSettlementEventEnvelope(
                EventId: $"tx:{result.TransactionId.ToHex()}:{result.Status}",
                EventType: ChainEventType.TransactionResult,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                BlockHeight: result.BlockHeight,
                BlockHash: result.BlockHash?.Value.IsEmpty == false ? result.BlockHash.ToHex() : null,
                TransactionId: result.TransactionId.ToHex(),
                Payload: result.ToByteArray(),
                Message: result.Status.ToString()),
            cancellationToken);
    }

    private NetworkNodeEndpoint RequireLocalNode()
    {
        return _localNode ?? throw new InvalidOperationException("Local node endpoint has not been attached yet.");
    }

    private void LogSchedulingObservation(
        long height,
        string scheduledProposer,
        string localNodeId,
        bool willProduce)
    {
        if (_lastSchedulingObservationHeight == height &&
            string.Equals(_lastSchedulingObservationProposer, scheduledProposer, StringComparison.Ordinal))
        {
            return;
        }

        _lastSchedulingObservationHeight = height;
        _lastSchedulingObservationProposer = scheduledProposer;

        if (willProduce)
        {
            _logger.LogDebug(
                "AEDPoS scheduling selected local node {NodeId} to produce height {Height}.",
                localNodeId,
                height);
            return;
        }

        _logger.LogDebug(
            "AEDPoS scheduling selected proposer {ProposerNodeId} for height {Height}; local node {NodeId} will wait for sync.",
            scheduledProposer,
            height,
            localNodeId);
    }

    private INetworkTransportCoordinator GetNetworkTransportCoordinator()
    {
        return _serviceProvider.GetRequiredService<INetworkTransportCoordinator>();
    }

    private Task TrackAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return TrackAsync<object?>(async () =>
        {
            await work().ConfigureAwait(false);
            return null;
        });
    }

    private async Task<T> TrackAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var task = work();
        lock (_operationLock)
        {
            _currentOperation = task;
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_operationLock)
            {
                if (ReferenceEquals(_currentOperation, task))
                {
                    _currentOperation = null;
                }
            }
        }
    }
}
