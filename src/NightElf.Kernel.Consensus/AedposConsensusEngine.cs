using System.Security.Cryptography;
using System.Text;

using NightElf.Kernel.Core;
using NightElf.Vrf;

namespace NightElf.Kernel.Consensus;

public sealed class AedposConsensusEngine : IConsensusEngine
{
    private static readonly Encoding HashEncoding = Encoding.UTF8;

    private readonly AedposConsensusOptions _options;
    private readonly IVrfProvider _vrfProvider;
    private readonly Lock _stateLock = new();

    private BlockReference? _lastCommittedBlock;
    private BlockReference? _lastIrreversibleBlock;
    private long _currentRoundNumber;
    private long _currentTermNumber;

    public AedposConsensusEngine(
        AedposConsensusOptions? options = null,
        IVrfProvider? vrfProvider = null)
    {
        _options = options ?? new AedposConsensusOptions();
        _options.ApplyDefaults();
        _options.Validate();
        _vrfProvider = vrfProvider ?? throw new ArgumentNullException(nameof(vrfProvider));
    }

    public ConsensusEngineKind Kind => ConsensusEngineKind.Aedpos;

    public BlockReference? LastCommittedBlock
    {
        get { lock (_stateLock) { return _lastCommittedBlock; } }
        private set { lock (_stateLock) { _lastCommittedBlock = value; } }
    }

    public BlockReference? LastIrreversibleBlock
    {
        get { lock (_stateLock) { return _lastIrreversibleBlock; } }
        private set { lock (_stateLock) { _lastIrreversibleBlock = value; } }
    }

    public long CurrentRoundNumber
    {
        get { lock (_stateLock) { return _currentRoundNumber; } }
        private set { lock (_stateLock) { _currentRoundNumber = value; } }
    }

    public long CurrentTermNumber
    {
        get { lock (_stateLock) { return _currentTermNumber; } }
        private set { lock (_stateLock) { _currentTermNumber = value; } }
    }

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
        var vrfInput = CreateVrfInput(context.RandomSeed, proposer, context.ExpectedHeight, context.RoundNumber, context.TermNumber);
        var vrfEvaluation = await _vrfProvider.EvaluateAsync(vrfInput, cancellationToken).ConfigureAwait(false);
        var blockHash = ComputeBlockHash(
            context.ExpectedHeight,
            parentBlockHash,
            proposer,
            context.RoundNumber,
            context.TermNumber,
            context.ProposedAtUtc,
            vrfEvaluation.Randomness);

        return new ConsensusBlockProposal
        {
            Block = new BlockReference(context.ExpectedHeight, blockHash),
            ParentBlockHash = parentBlockHash,
            ProposerAddress = proposer,
            RoundNumber = context.RoundNumber,
            TermNumber = context.TermNumber,
            LastIrreversibleBlockHeight = context.LastIrreversibleBlock?.Height ?? Math.Max(0, context.ExpectedHeight - _options.IrreversibleBlockDistance),
            TimestampUtc = context.ProposedAtUtc,
            RandomSeed = context.RandomSeed.ToArray(),
            Randomness = vrfEvaluation.Randomness,
            VrfProof = vrfEvaluation.Proof,
            ConsensusData = BuildConsensusData(proposer, validators, vrfEvaluation.Randomness)
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

        var vrfContext = new VrfVerificationContext
        {
            Input = CreateVrfInput(block.RandomSeed, block.ProposerAddress, block.Block.Height, block.RoundNumber, block.TermNumber),
            Proof = block.VrfProof,
            Randomness = block.Randomness
        };

        if (!await _vrfProvider.VerifyAsync(vrfContext, cancellationToken).ConfigureAwait(false))
        {
            return ConsensusValidationResult.Invalid(
                "invalid_vrf",
                $"Consensus VRF proof is invalid for proposer '{block.ProposerAddress}' at height {block.Block.Height}.");
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

        lock (_stateLock)
        {
            _lastCommittedBlock = context.Block.Block;
            _lastIrreversibleBlock = context.LastIrreversibleBlock;
            _currentRoundNumber = context.Block.RoundNumber;
            _currentTermNumber = context.Block.TermNumber;
        }

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
        IReadOnlyList<ConsensusValidator> validators,
        byte[] randomness)
    {
        return HashEncoding.GetBytes(
            $"aedpos|proposer={proposer}|validators={string.Join(",", validators.Select(static validator => validator.Address))}|randomness={Convert.ToHexString(randomness)}");
    }

    private static VrfInput CreateVrfInput(
        ReadOnlySpan<byte> seed,
        string publicKey,
        long height,
        long roundNumber,
        long termNumber)
    {
        return new VrfInput
        {
            PublicKey = publicKey,
            Domain = $"aedpos:{height}:{termNumber}:{roundNumber}",
            Seed = seed.ToArray()
        };
    }
}
