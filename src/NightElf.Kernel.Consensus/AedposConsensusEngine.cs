using System.Security.Cryptography;
using System.Text;

using NightElf.Kernel.Core;

namespace NightElf.Kernel.Consensus;

public sealed class AedposConsensusEngine : IConsensusEngine
{
    private static readonly Encoding HashEncoding = Encoding.UTF8;

    private readonly AedposConsensusOptions _options;

    public AedposConsensusEngine(AedposConsensusOptions? options = null)
    {
        _options = options ?? new AedposConsensusOptions();
        _options.Validate();
    }

    public ConsensusEngineKind Kind => ConsensusEngineKind.Aedpos;

    public BlockReference? LastCommittedBlock { get; private set; }

    public BlockReference? LastIrreversibleBlock { get; private set; }

    public long CurrentRoundNumber { get; private set; }

    public long CurrentTermNumber { get; private set; }

    public async Task<ConsensusBlockProposal> ProposeBlockAsync(
        ConsensusContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();

        var validators = await GetValidatorsAsync(
                new ConsensusValidatorQuery
                {
                    RoundNumber = context.RoundNumber,
                    TermNumber = context.TermNumber
                },
                cancellationToken)
            .ConfigureAwait(false);

        var proposer = string.IsNullOrWhiteSpace(context.ProposerAddress)
            ? validators[0].Address
            : context.ProposerAddress!;

        if (!validators.Any(validator => string.Equals(validator.Address, proposer, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"AEDPoS proposer '{proposer}' is not part of the validator set for round {context.RoundNumber}.");
        }

        var parentBlockHash = context.PreviousBlock?.Hash ?? "GENESIS";
        var blockHash = ComputeBlockHash(
            context.ExpectedHeight,
            parentBlockHash,
            proposer,
            context.RoundNumber,
            context.TermNumber,
            context.ProposedAtUtc,
            context.RandomSeed);

        return new ConsensusBlockProposal
        {
            Block = new BlockReference(context.ExpectedHeight, blockHash),
            ParentBlockHash = parentBlockHash,
            ProposerAddress = proposer,
            RoundNumber = context.RoundNumber,
            TermNumber = context.TermNumber,
            LastIrreversibleBlockHeight = context.LastIrreversibleBlock?.Height ?? Math.Max(0, context.ExpectedHeight - _options.IrreversibleBlockDistance),
            TimestampUtc = context.ProposedAtUtc,
            ConsensusData = BuildConsensusData(proposer, validators)
        };
    }

    public async Task<ConsensusValidationResult> ValidateBlockAsync(
        ConsensusBlockProposal block,
        ConsensusValidationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(context);

        if (block.Block.Height != context.ExpectedHeight)
        {
            return ConsensusValidationResult.Invalid(
                "unexpected_height",
                $"Consensus block height {block.Block.Height} does not match expected height {context.ExpectedHeight}.");
        }

        if (context.PreviousBlock is not null && !string.Equals(block.ParentBlockHash, context.PreviousBlock.Hash, StringComparison.Ordinal))
        {
            return ConsensusValidationResult.Invalid(
                "unexpected_parent",
                $"Consensus parent hash '{block.ParentBlockHash}' does not match expected parent '{context.PreviousBlock.Hash}'.");
        }

        if (block.LastIrreversibleBlockHeight > block.Block.Height)
        {
            return ConsensusValidationResult.Invalid(
                "invalid_lib",
                $"Consensus LIB height {block.LastIrreversibleBlockHeight} cannot exceed block height {block.Block.Height}.");
        }

        if (context.NotBeforeUtc.HasValue && block.TimestampUtc < context.NotBeforeUtc.Value)
        {
            return ConsensusValidationResult.Invalid(
                "timestamp_before_window",
                $"Consensus timestamp '{block.TimestampUtc:O}' is earlier than '{context.NotBeforeUtc.Value:O}'.");
        }

        if (context.NotAfterUtc.HasValue && block.TimestampUtc > context.NotAfterUtc.Value)
        {
            return ConsensusValidationResult.Invalid(
                "timestamp_after_window",
                $"Consensus timestamp '{block.TimestampUtc:O}' is later than '{context.NotAfterUtc.Value:O}'.");
        }

        var validators = context.ExpectedValidators ?? (await GetValidatorsAsync(
                new ConsensusValidatorQuery
                {
                    RoundNumber = block.RoundNumber,
                    TermNumber = block.TermNumber
                },
                cancellationToken)
            .ConfigureAwait(false)).Select(static validator => validator.Address).ToArray();

        if (!validators.Contains(block.ProposerAddress, StringComparer.Ordinal))
        {
            return ConsensusValidationResult.Invalid(
                "unknown_validator",
                $"Consensus proposer '{block.ProposerAddress}' is not in the validator set.");
        }

        return ConsensusValidationResult.Valid();
    }

    public Task OnBlockCommittedAsync(
        ConsensusCommitContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Block);

        LastCommittedBlock = context.Block.Block;
        LastIrreversibleBlock = context.LastIrreversibleBlock;
        CurrentRoundNumber = context.Block.RoundNumber;
        CurrentTermNumber = context.Block.TermNumber;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConsensusValidator>> GetValidatorsAsync(
        ConsensusValidatorQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);

        var normalizedRound = query.RoundNumber <= 0 ? 1 : query.RoundNumber;
        var offset = (int)((normalizedRound - 1) % _options.Validators.Count);

        var validators = _options.Validators
            .Skip(offset)
            .Concat(_options.Validators.Take(offset))
            .Select((validator, index) => new ConsensusValidator(validator, index))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ConsensusValidator>>(validators);
    }

    public Task<ConsensusChainHeadCandidate> ForkChoiceAsync(
        ConsensusForkChoiceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);

        if (context.Candidates.Count == 0)
        {
            throw new InvalidOperationException("Consensus fork choice requires at least one candidate.");
        }

        var selectedCandidate = context.Candidates
            .OrderByDescending(candidate => candidate.LastIrreversibleBlock?.Height ?? -1)
            .ThenByDescending(candidate => candidate.Head.Height)
            .ThenByDescending(candidate => candidate.TermNumber)
            .ThenByDescending(candidate => candidate.RoundNumber)
            .ThenBy(candidate => candidate.Head.Hash, StringComparer.Ordinal)
            .First();

        return Task.FromResult(selectedCandidate);
    }

    private static string ComputeBlockHash(
        long height,
        string parentBlockHash,
        string proposer,
        long roundNumber,
        long termNumber,
        DateTimeOffset proposedAtUtc,
        byte[] randomSeed)
    {
        var payload = $"{height}|{parentBlockHash}|{proposer}|{roundNumber}|{termNumber}|{proposedAtUtc.UtcTicks}|{Convert.ToHexString(randomSeed)}";
        var hash = SHA256.HashData(HashEncoding.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static byte[] BuildConsensusData(
        string proposer,
        IReadOnlyList<ConsensusValidator> validators)
    {
        return HashEncoding.GetBytes(
            $"aedpos|proposer={proposer}|validators={string.Join(",", validators.Select(static validator => validator.Address))}");
    }
}
