using System.Security.Cryptography;
using System.Text;

using NightElf.Kernel.Core;

namespace NightElf.Kernel.Consensus;

public sealed class SingleValidatorConsensusEngine : IConsensusEngine
{
    private static readonly Encoding HashEncoding = Encoding.UTF8;

    private readonly SingleValidatorConsensusOptions _options;
    private readonly Lock _stateLock = new();

    private BlockReference? _lastCommittedBlock;
    private BlockReference? _lastIrreversibleBlock;
    private DateTimeOffset? _lastCommittedTimestampUtc;
    private long _currentRoundNumber;
    private long _currentTermNumber;

    public SingleValidatorConsensusEngine(SingleValidatorConsensusOptions? options = null)
    {
        _options = options ?? new SingleValidatorConsensusOptions();
        _options.ApplyDefaults();
        _options.Validate();
    }

    public ConsensusEngineKind Kind => ConsensusEngineKind.SingleValidator;

    public string ValidatorAddress => _options.ValidatorAddress;

    public TimeSpan BlockInterval => _options.BlockInterval;

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

    public DateTimeOffset? LastCommittedTimestampUtc
    {
        get { lock (_stateLock) { return _lastCommittedTimestampUtc; } }
        private set { lock (_stateLock) { _lastCommittedTimestampUtc = value; } }
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

    public Task<ConsensusBlockProposal> ProposeBlockAsync(
        ConsensusContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();

        var proposer = string.IsNullOrWhiteSpace(context.ProposerAddress)
            ? _options.ValidatorAddress
            : context.ProposerAddress!;

        if (!string.Equals(proposer, _options.ValidatorAddress, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Single-validator proposer '{proposer}' does not match configured validator '{_options.ValidatorAddress}'.");
        }

        var parentBlockHash = context.PreviousBlock?.Hash ?? "GENESIS";
        var proposedAtUtc = NormalizeTimestamp(context.ProposedAtUtc);
        var blockHash = ComputeBlockHash(
            context.ExpectedHeight,
            parentBlockHash,
            proposer,
            proposedAtUtc);

        return Task.FromResult(new ConsensusBlockProposal
        {
            Block = new BlockReference(context.ExpectedHeight, blockHash),
            ParentBlockHash = parentBlockHash,
            ProposerAddress = proposer,
            RoundNumber = context.RoundNumber,
            TermNumber = context.TermNumber,
            LastIrreversibleBlockHeight = context.ExpectedHeight,
            TimestampUtc = proposedAtUtc,
            RandomSeed = context.RandomSeed.ToArray(),
            ConsensusData = BuildConsensusData(proposer, _options.BlockInterval)
        });
    }

    public Task<ConsensusValidationResult> ValidateBlockAsync(
        ConsensusBlockProposal block,
        ConsensusValidationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(context);

        if (block.Block.Height != context.ExpectedHeight)
        {
            return Task.FromResult(ConsensusValidationResult.Invalid(
                "unexpected_height",
                $"Consensus block height {block.Block.Height} does not match expected height {context.ExpectedHeight}."));
        }

        if (context.PreviousBlock is not null && !string.Equals(block.ParentBlockHash, context.PreviousBlock.Hash, StringComparison.Ordinal))
        {
            return Task.FromResult(ConsensusValidationResult.Invalid(
                "unexpected_parent",
                $"Consensus parent hash '{block.ParentBlockHash}' does not match expected parent '{context.PreviousBlock.Hash}'."));
        }

        if (!string.Equals(block.ProposerAddress, _options.ValidatorAddress, StringComparison.Ordinal))
        {
            return Task.FromResult(ConsensusValidationResult.Invalid(
                "unknown_validator",
                $"Single-validator proposer '{block.ProposerAddress}' does not match configured validator '{_options.ValidatorAddress}'."));
        }

        if (block.LastIrreversibleBlockHeight != block.Block.Height)
        {
            return Task.FromResult(ConsensusValidationResult.Invalid(
                "invalid_lib",
                $"Single-validator consensus expects LIB height {block.Block.Height}, but proposal carried {block.LastIrreversibleBlockHeight}."));
        }

        if (context.ExpectedValidators is not null &&
            (context.ExpectedValidators.Count != 1 ||
             !string.Equals(context.ExpectedValidators[0], _options.ValidatorAddress, StringComparison.Ordinal)))
        {
            return Task.FromResult(ConsensusValidationResult.Invalid(
                "unexpected_validator_set",
                $"Single-validator consensus expects exactly one validator '{_options.ValidatorAddress}'."));
        }

        if (context.NotBeforeUtc.HasValue && block.TimestampUtc < context.NotBeforeUtc.Value)
        {
            return Task.FromResult(ConsensusValidationResult.Invalid(
                "timestamp_before_window",
                $"Consensus timestamp '{block.TimestampUtc:O}' is earlier than '{context.NotBeforeUtc.Value:O}'."));
        }

        if (context.NotAfterUtc.HasValue && block.TimestampUtc > context.NotAfterUtc.Value)
        {
            return Task.FromResult(ConsensusValidationResult.Invalid(
                "timestamp_after_window",
                $"Consensus timestamp '{block.TimestampUtc:O}' is later than '{context.NotAfterUtc.Value:O}'."));
        }

        var lastCommittedTimestampUtc = LastCommittedTimestampUtc;
        if (lastCommittedTimestampUtc.HasValue &&
            block.TimestampUtc < lastCommittedTimestampUtc.Value + _options.BlockInterval)
        {
            return Task.FromResult(ConsensusValidationResult.Invalid(
                "block_interval_violation",
                $"Single-validator timestamp '{block.TimestampUtc:O}' is earlier than the next slot '{(lastCommittedTimestampUtc.Value + _options.BlockInterval):O}'."));
        }

        return Task.FromResult(ConsensusValidationResult.Valid());
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
            _lastIrreversibleBlock = context.LastIrreversibleBlock ?? context.Block.Block;
            _lastCommittedTimestampUtc = context.Block.TimestampUtc;
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

        return Task.FromResult<IReadOnlyList<ConsensusValidator>>([new ConsensusValidator(_options.ValidatorAddress, 0)]);
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
            .OrderByDescending(candidate => candidate.Head.Height)
            .ThenByDescending(candidate => candidate.LastIrreversibleBlock?.Height ?? -1)
            .ThenBy(candidate => candidate.Head.Hash, StringComparer.Ordinal)
            .First();

        return Task.FromResult(selectedCandidate);
    }

    private DateTimeOffset NormalizeTimestamp(DateTimeOffset proposedAtUtc)
    {
        var lastCommittedTimestampUtc = LastCommittedTimestampUtc;
        if (!lastCommittedTimestampUtc.HasValue)
        {
            return proposedAtUtc;
        }

        var nextAllowedUtc = lastCommittedTimestampUtc.Value + _options.BlockInterval;
        return proposedAtUtc < nextAllowedUtc ? nextAllowedUtc : proposedAtUtc;
    }

    private static string ComputeBlockHash(
        long height,
        string parentBlockHash,
        string proposer,
        DateTimeOffset proposedAtUtc)
    {
        var payload = $"{height}|{parentBlockHash}|{proposer}|{proposedAtUtc.UtcTicks}|single-validator";
        var hash = SHA256.HashData(HashEncoding.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static byte[] BuildConsensusData(string proposer, TimeSpan blockInterval)
    {
        return HashEncoding.GetBytes(
            $"single-validator|proposer={proposer}|interval={blockInterval:c}");
    }
}
