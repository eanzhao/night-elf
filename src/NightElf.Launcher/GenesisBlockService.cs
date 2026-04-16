using System.Security.Cryptography;
using System.Text;

using NightElf.Database;
using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;

namespace NightElf.Launcher;

public interface IGenesisBlockService
{
    Task<GenesisInitializationResult> EnsureGenesisAsync(CancellationToken cancellationToken = default);
}

public sealed class GenesisBlockService : IGenesisBlockService
{
    private readonly LauncherOptions _launcherOptions;
    private readonly ConsensusEngineOptions _consensusOptions;
    private readonly IConsensusEngine _consensusEngine;
    private readonly IBlockRepository _blockRepository;
    private readonly IChainStateStore _chainStateStore;

    public GenesisBlockService(
        LauncherOptions launcherOptions,
        ConsensusEngineOptions consensusOptions,
        IConsensusEngine consensusEngine,
        IBlockRepository blockRepository,
        IChainStateStore chainStateStore)
    {
        _launcherOptions = launcherOptions ?? throw new ArgumentNullException(nameof(launcherOptions));
        _consensusOptions = consensusOptions ?? throw new ArgumentNullException(nameof(consensusOptions));
        _consensusEngine = consensusEngine ?? throw new ArgumentNullException(nameof(consensusEngine));
        _blockRepository = blockRepository ?? throw new ArgumentNullException(nameof(blockRepository));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
    }

    public async Task<GenesisInitializationResult> EnsureGenesisAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _launcherOptions.ValidateAgainstConsensus(_consensusOptions);

        var existingBestChain = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false);
        if (existingBestChain is not null)
        {
            return new GenesisInitializationResult
            {
                Created = false,
                Block = existingBestChain,
                Checkpoint = null,
                Proposal = null
            };
        }

        var validators = _launcherOptions.Genesis.GetEffectiveValidators(_consensusOptions);
        var proposedAtUtc = _launcherOptions.Genesis.TimestampUtc ?? DateTimeOffset.UtcNow;
        var proposal = await _consensusEngine.ProposeBlockAsync(
                new ConsensusContext
                {
                    ExpectedHeight = 1,
                    PreviousBlock = null,
                    LastIrreversibleBlock = null,
                    ProposerAddress = validators[0],
                    RoundNumber = 1,
                    TermNumber = 1,
                    ProposedAtUtc = proposedAtUtc,
                    RandomSeed = CreateGenesisSeed(_launcherOptions.Genesis, validators)
                },
                cancellationToken)
            .ConfigureAwait(false);

        var validation = await _consensusEngine.ValidateBlockAsync(
                proposal,
                new ConsensusValidationContext
                {
                    ExpectedHeight = 1,
                    ExpectedValidators = validators
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Genesis proposal validation failed: {validation.ErrorCode} {validation.ErrorMessage}");
        }

        var block = BlockModelFactory.CreateBlock(proposal, _launcherOptions.Genesis.ChainId);
        await _blockRepository.StoreAsync(proposal.Block, block, cancellationToken).ConfigureAwait(false);

        var writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["genesis:block-hash"] = Encoding.UTF8.GetBytes(proposal.Block.Hash),
            ["genesis:chain-id"] = Encoding.UTF8.GetBytes(_launcherOptions.Genesis.ChainId.ToString()),
            ["genesis:validators"] = Encoding.UTF8.GetBytes(string.Join(",", validators)),
            ["genesis:system-contracts"] = Encoding.UTF8.GetBytes(string.Join(",", _launcherOptions.Genesis.SystemContracts)),
            ["genesis:config"] = BlockModelFactory.CreateGenesisConfigPayload(_launcherOptions.Genesis),
            ["system-contract:AgentSession"] = Encoding.UTF8.GetBytes("placeholder-deployed")
        };

        await _chainStateStore.SetBestChainAsync(proposal.Block, cancellationToken).ConfigureAwait(false);
        await _chainStateStore.ApplyChangesAsync(proposal.Block, writes, cancellationToken: cancellationToken).ConfigureAwait(false);
        var checkpoint = await _chainStateStore.AdvanceLibCheckpointAsync(proposal.Block, cancellationToken).ConfigureAwait(false);

        await _consensusEngine.OnBlockCommittedAsync(
                new ConsensusCommitContext
                {
                    Block = proposal,
                    LastIrreversibleBlock = proposal.Block
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new GenesisInitializationResult
        {
            Created = true,
            Block = proposal.Block,
            Checkpoint = checkpoint,
            Proposal = proposal
        };
    }

    private static byte[] CreateGenesisSeed(
        GenesisConfig genesisConfig,
        IReadOnlyList<string> validators)
    {
        var payload = $"{genesisConfig.ChainId}|{string.Join(",", validators)}|{string.Join(",", genesisConfig.SystemContracts)}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(payload));
    }
}

public sealed class GenesisInitializationResult
{
    public required bool Created { get; init; }

    public required BlockReference Block { get; init; }

    public StateCheckpointDescriptor? Checkpoint { get; init; }

    public ConsensusBlockProposal? Proposal { get; init; }
}
