namespace NightElf.Kernel.Consensus;

public interface IConsensusEngine
{
    ConsensusEngineKind Kind { get; }

    Task<ConsensusBlockProposal> ProposeBlockAsync(
        ConsensusContext context,
        CancellationToken cancellationToken = default);

    Task<ConsensusValidationResult> ValidateBlockAsync(
        ConsensusBlockProposal block,
        ConsensusValidationContext context,
        CancellationToken cancellationToken = default);

    Task OnBlockCommittedAsync(
        ConsensusCommitContext context,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConsensusValidator>> GetValidatorsAsync(
        ConsensusValidatorQuery query,
        CancellationToken cancellationToken = default);

    Task<ConsensusChainHeadCandidate> ForkChoiceAsync(
        ConsensusForkChoiceContext context,
        CancellationToken cancellationToken = default);
}
