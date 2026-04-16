using System.Text;

using NightElf.Kernel.Core;
using NightElf.Vrf;

namespace NightElf.Kernel.Consensus.Tests;

public sealed class AedposConsensusEngineTests
{
    [Fact]
    public async Task ProposeBlock_Should_Derive_A_Deterministic_Block_Using_Round_Rotation()
    {
        var engine = CreateEngine();
        var context = new ConsensusContext
        {
            ExpectedHeight = 8,
            PreviousBlock = new BlockReference(7, "prev-007"),
            LastIrreversibleBlock = new BlockReference(5, "lib-005"),
            RoundNumber = 2,
            TermNumber = 3,
            ProposedAtUtc = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero),
            RandomSeed = Encoding.UTF8.GetBytes("seed-8")
        };

        var proposal = await engine.ProposeBlockAsync(context);
        var repeated = await engine.ProposeBlockAsync(context);

        Assert.Equal(ConsensusEngineKind.Aedpos, engine.Kind);
        Assert.Equal(8, proposal.Block.Height);
        Assert.Equal("validator-b", proposal.ProposerAddress);
        Assert.Equal("prev-007", proposal.ParentBlockHash);
        Assert.Equal(2, proposal.RoundNumber);
        Assert.Equal(3, proposal.TermNumber);
        Assert.Equal(5, proposal.LastIrreversibleBlockHeight);
        Assert.Equal(Encoding.UTF8.GetBytes("seed-8"), proposal.RandomSeed);
        Assert.NotEmpty(proposal.Randomness);
        Assert.NotEmpty(proposal.VrfProof);
        Assert.Equal(proposal.Block.Hash, repeated.Block.Hash);
        Assert.StartsWith("aedpos|proposer=validator-b", Encoding.UTF8.GetString(proposal.ConsensusData), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateBlock_Should_Reject_Invalid_Height_Parent_And_Validator()
    {
        var engine = CreateEngine();
        var invalidHeightProposal = new ConsensusBlockProposal
        {
            Block = new BlockReference(9, "block-009"),
            ParentBlockHash = "prev-007",
            ProposerAddress = "validator-a",
            RoundNumber = 1,
            TermNumber = 1,
            LastIrreversibleBlockHeight = 5,
            TimestampUtc = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero)
        };

        var invalidHeight = await engine.ValidateBlockAsync(
            invalidHeightProposal,
            new ConsensusValidationContext
            {
                ExpectedHeight = 8,
                PreviousBlock = new BlockReference(7, "prev-007")
            });

        Assert.False(invalidHeight.IsValid);
        Assert.Equal("unexpected_height", invalidHeight.ErrorCode);

        var invalidParentProposal = invalidHeightProposal with
        {
            Block = new BlockReference(8, "block-008"),
            ParentBlockHash = "wrong-parent"
        };
        var invalidParent = await engine.ValidateBlockAsync(
            invalidParentProposal,
            new ConsensusValidationContext
            {
                ExpectedHeight = 8,
                PreviousBlock = new BlockReference(7, "prev-007")
            });

        Assert.False(invalidParent.IsValid);
        Assert.Equal("unexpected_parent", invalidParent.ErrorCode);

        var invalidValidatorProposal = invalidHeightProposal with
        {
            Block = new BlockReference(8, "block-008"),
            ProposerAddress = "outsider"
        };
        var invalidValidator = await engine.ValidateBlockAsync(
            invalidValidatorProposal,
            new ConsensusValidationContext
            {
                ExpectedHeight = 8,
                PreviousBlock = new BlockReference(7, "prev-007")
            });

        Assert.False(invalidValidator.IsValid);
        Assert.Equal("unknown_validator", invalidValidator.ErrorCode);
    }

    [Fact]
    public async Task ForkChoice_Should_Prefer_Higher_Lib_Then_Head_Height()
    {
        var engine = CreateEngine();
        var selected = await engine.ForkChoiceAsync(new ConsensusForkChoiceContext
        {
            Candidates =
            [
                new ConsensusChainHeadCandidate
                {
                    Head = new BlockReference(21, "head-a"),
                    LastIrreversibleBlock = new BlockReference(18, "lib-018"),
                    ProducerAddress = "validator-a",
                    RoundNumber = 5,
                    TermNumber = 3
                },
                new ConsensusChainHeadCandidate
                {
                    Head = new BlockReference(20, "head-b"),
                    LastIrreversibleBlock = new BlockReference(19, "lib-019"),
                    ProducerAddress = "validator-b",
                    RoundNumber = 4,
                    TermNumber = 3
                },
                new ConsensusChainHeadCandidate
                {
                    Head = new BlockReference(22, "head-c"),
                    LastIrreversibleBlock = new BlockReference(19, "lib-019"),
                    ProducerAddress = "validator-c",
                    RoundNumber = 3,
                    TermNumber = 3
                }
            ]
        });

        Assert.Equal("head-c", selected.Head.Hash);
        Assert.Equal("validator-c", selected.ProducerAddress);
    }

    [Fact]
    public async Task OnBlockCommitted_Should_Update_Consensus_State_And_Validator_Order()
    {
        var engine = CreateEngine();
        var proposal = new ConsensusBlockProposal
        {
            Block = new BlockReference(12, "block-012"),
            ParentBlockHash = "block-011",
            ProposerAddress = "validator-c",
            RoundNumber = 3,
            TermNumber = 2,
            LastIrreversibleBlockHeight = 9,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        await engine.OnBlockCommittedAsync(new ConsensusCommitContext
        {
            Block = proposal,
            LastIrreversibleBlock = new BlockReference(9, "block-009")
        });

        var validators = await engine.GetValidatorsAsync(new ConsensusValidatorQuery
        {
            RoundNumber = 3,
            TermNumber = 2
        });

        Assert.Equal(proposal.Block, engine.LastCommittedBlock);
        Assert.Equal(new BlockReference(9, "block-009"), engine.LastIrreversibleBlock);
        Assert.Equal(3, engine.CurrentRoundNumber);
        Assert.Equal(2, engine.CurrentTermNumber);
        Assert.Equal(["validator-c", "validator-a", "validator-b"], validators.Select(static item => item.Address).ToArray());
    }

    [Fact]
    public async Task ProposeAndValidate_Should_Use_The_Injected_VrfProvider()
    {
        var vrfProvider = new RecordingVrfProvider();
        var engine = new AedposConsensusEngine(
            new AedposConsensusOptions
            {
                Validators = ["validator-a", "validator-b", "validator-c"]
            },
            vrfProvider);
        var context = new ConsensusContext
        {
            ExpectedHeight = 13,
            PreviousBlock = new BlockReference(12, "prev-012"),
            RoundNumber = 1,
            TermNumber = 1,
            ProposedAtUtc = new DateTimeOffset(2026, 4, 16, 12, 1, 0, TimeSpan.Zero),
            RandomSeed = Encoding.UTF8.GetBytes("seed-13")
        };

        var proposal = await engine.ProposeBlockAsync(context);
        var validation = await engine.ValidateBlockAsync(
            proposal,
            new ConsensusValidationContext
            {
                ExpectedHeight = 13,
                PreviousBlock = new BlockReference(12, "prev-012")
            });

        Assert.True(validation.IsValid);
        Assert.Equal(1, vrfProvider.EvaluateCallCount);
        Assert.Equal(1, vrfProvider.VerifyCallCount);
        Assert.Equal("validator-a", vrfProvider.LastInput!.PublicKey);
        Assert.Equal("aedpos:13:1:1", vrfProvider.LastInput.Domain);
        Assert.Equal("proof:validator-a:aedpos:13:1:1:seed-13", Encoding.UTF8.GetString(proposal.VrfProof));
        Assert.Equal("random:validator-a:aedpos:13:1:1:seed-13", Encoding.UTF8.GetString(proposal.Randomness));
    }

    private static AedposConsensusEngine CreateEngine()
    {
        return new AedposConsensusEngine(new AedposConsensusOptions
        {
            Validators = ["validator-a", "validator-b", "validator-c"],
            BlocksPerRound = 3,
            IrreversibleBlockDistance = 5
        });
    }

    private sealed class RecordingVrfProvider : IVrfProvider
    {
        public int EvaluateCallCount { get; private set; }

        public int VerifyCallCount { get; private set; }

        public VrfInput? LastInput { get; private set; }

        public string Algorithm => "Recording";

        public Task<VrfEvaluation> EvaluateAsync(VrfInput input, CancellationToken cancellationToken = default)
        {
            EvaluateCallCount++;
            LastInput = new VrfInput
            {
                PublicKey = input.PublicKey,
                Domain = input.Domain,
                Seed = input.Seed.ToArray()
            };

            return Task.FromResult(CreateEvaluation(input));
        }

        public Task<bool> VerifyAsync(VrfVerificationContext context, CancellationToken cancellationToken = default)
        {
            VerifyCallCount++;
            var evaluation = CreateEvaluation(context.Input);

            return Task.FromResult(
                evaluation.Proof.SequenceEqual(context.Proof) &&
                evaluation.Randomness.SequenceEqual(context.Randomness));
        }

        private static VrfEvaluation CreateEvaluation(VrfInput input)
        {
            var suffix = $"{input.PublicKey}:{input.Domain}:{Encoding.UTF8.GetString(input.Seed)}";
            return new VrfEvaluation
            {
                Proof = Encoding.UTF8.GetBytes($"proof:{suffix}"),
                Randomness = Encoding.UTF8.GetBytes($"random:{suffix}")
            };
        }
    }
}
