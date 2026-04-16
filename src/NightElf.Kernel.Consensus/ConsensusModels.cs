using NightElf.Kernel.Core;

namespace NightElf.Kernel.Consensus;

public sealed class ConsensusContext
{
    public required long ExpectedHeight { get; init; }

    public BlockReference? PreviousBlock { get; init; }

    public BlockReference? LastIrreversibleBlock { get; init; }

    public string? ProposerAddress { get; init; }

    public required long RoundNumber { get; init; }

    public required long TermNumber { get; init; }

    public DateTimeOffset ProposedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public byte[] RandomSeed { get; init; } = [];

    public void Validate()
    {
        if (ExpectedHeight <= 0)
        {
            throw new InvalidOperationException("Consensus expected block height must be greater than zero.");
        }

        if (RoundNumber <= 0)
        {
            throw new InvalidOperationException("Consensus round number must be greater than zero.");
        }

        if (TermNumber <= 0)
        {
            throw new InvalidOperationException("Consensus term number must be greater than zero.");
        }

        if (PreviousBlock is not null && ExpectedHeight != PreviousBlock.Height + 1)
        {
            throw new InvalidOperationException(
                $"Consensus expected height {ExpectedHeight} must follow previous block height {PreviousBlock.Height}.");
        }
    }
}

public sealed record class ConsensusBlockProposal
{
    public required BlockReference Block { get; init; }

    public required string ParentBlockHash { get; init; }

    public required string ProposerAddress { get; init; }

    public required long RoundNumber { get; init; }

    public required long TermNumber { get; init; }

    public required long LastIrreversibleBlockHeight { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public byte[] RandomSeed { get; init; } = [];

    public byte[] Randomness { get; init; } = [];

    public byte[] VrfProof { get; init; } = [];

    public byte[] ConsensusData { get; init; } = [];
}

public sealed class ConsensusValidationContext
{
    public required long ExpectedHeight { get; init; }

    public BlockReference? PreviousBlock { get; init; }

    public IReadOnlyList<string>? ExpectedValidators { get; init; }

    public DateTimeOffset? NotBeforeUtc { get; init; }

    public DateTimeOffset? NotAfterUtc { get; init; }
}

public sealed class ConsensusValidationResult
{
    private ConsensusValidationResult(bool isValid, string? errorCode, string? errorMessage)
    {
        IsValid = isValid;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public bool IsValid { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public static ConsensusValidationResult Valid()
    {
        return new ConsensusValidationResult(true, null, null);
    }

    public static ConsensusValidationResult Invalid(string errorCode, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        return new ConsensusValidationResult(false, errorCode, errorMessage);
    }
}

public sealed class ConsensusCommitContext
{
    public required ConsensusBlockProposal Block { get; init; }

    public BlockReference? LastIrreversibleBlock { get; init; }
}

public sealed record ConsensusValidator(string Address, int Order);

public sealed class ConsensusValidatorQuery
{
    public long RoundNumber { get; init; } = 1;

    public long TermNumber { get; init; } = 1;
}

public sealed record class ConsensusChainHeadCandidate
{
    public required BlockReference Head { get; init; }

    public BlockReference? LastIrreversibleBlock { get; init; }

    public required string ProducerAddress { get; init; }

    public required long RoundNumber { get; init; }

    public required long TermNumber { get; init; }
}

public sealed class ConsensusForkChoiceContext
{
    public required IReadOnlyList<ConsensusChainHeadCandidate> Candidates { get; init; }
}
