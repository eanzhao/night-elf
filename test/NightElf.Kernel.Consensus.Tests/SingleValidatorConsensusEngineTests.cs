using System.Text;

using NightElf.Kernel.Core;

namespace NightElf.Kernel.Consensus.Tests;

public sealed class SingleValidatorConsensusEngineTests
{
    [Fact]
    public async Task ProposeBlock_Should_Use_Configured_Validator_And_Immediate_Finality()
    {
        var engine = CreateEngine();
        var proposedAtUtc = new DateTimeOffset(2026, 4, 16, 12, 30, 0, TimeSpan.Zero);
        var context = new ConsensusContext
        {
            ExpectedHeight = 4,
            PreviousBlock = new BlockReference(3, "prev-003"),
            LastIrreversibleBlock = new BlockReference(3, "prev-003"),
            RoundNumber = 4,
            TermNumber = 1,
            ProposedAtUtc = proposedAtUtc,
            RandomSeed = Encoding.UTF8.GetBytes("seed-4")
        };

        var proposal = await engine.ProposeBlockAsync(context);
        var repeated = await engine.ProposeBlockAsync(context);

        Assert.Equal(ConsensusEngineKind.SingleValidator, engine.Kind);
        Assert.Equal(4, proposal.Block.Height);
        Assert.Equal("node-local", proposal.ProposerAddress);
        Assert.Equal("prev-003", proposal.ParentBlockHash);
        Assert.Equal(4, proposal.RoundNumber);
        Assert.Equal(1, proposal.TermNumber);
        Assert.Equal(4, proposal.LastIrreversibleBlockHeight);
        Assert.Equal(proposedAtUtc, proposal.TimestampUtc);
        Assert.Equal(Encoding.UTF8.GetBytes("seed-4"), proposal.RandomSeed);
        Assert.Empty(proposal.Randomness);
        Assert.Empty(proposal.VrfProof);
        Assert.Equal(proposal.Block.Hash, repeated.Block.Hash);
        Assert.StartsWith("single-validator|proposer=node-local", Encoding.UTF8.GetString(proposal.ConsensusData), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProposeBlock_Should_Normalize_To_The_Next_Block_Interval()
    {
        var engine = CreateEngine(TimeSpan.FromSeconds(4));
        var committedAtUtc = new DateTimeOffset(2026, 4, 16, 12, 31, 0, TimeSpan.Zero);

        await engine.OnBlockCommittedAsync(new ConsensusCommitContext
        {
            Block = new ConsensusBlockProposal
            {
                Block = new BlockReference(1, "block-001"),
                ParentBlockHash = "GENESIS",
                ProposerAddress = "node-local",
                RoundNumber = 1,
                TermNumber = 1,
                LastIrreversibleBlockHeight = 1,
                TimestampUtc = committedAtUtc
            },
            LastIrreversibleBlock = new BlockReference(1, "block-001")
        });

        var proposal = await engine.ProposeBlockAsync(new ConsensusContext
        {
            ExpectedHeight = 2,
            PreviousBlock = new BlockReference(1, "block-001"),
            LastIrreversibleBlock = new BlockReference(1, "block-001"),
            RoundNumber = 2,
            TermNumber = 1,
            ProposedAtUtc = committedAtUtc.AddSeconds(1)
        });

        Assert.Equal(committedAtUtc.AddSeconds(4), proposal.TimestampUtc);
    }

    [Fact]
    public async Task ValidateBlock_Should_Reject_Invalid_Validator_Set_And_Early_Timestamp()
    {
        var engine = CreateEngine(TimeSpan.FromSeconds(4));
        var committedAtUtc = new DateTimeOffset(2026, 4, 16, 12, 32, 0, TimeSpan.Zero);

        await engine.OnBlockCommittedAsync(new ConsensusCommitContext
        {
            Block = new ConsensusBlockProposal
            {
                Block = new BlockReference(1, "block-001"),
                ParentBlockHash = "GENESIS",
                ProposerAddress = "node-local",
                RoundNumber = 1,
                TermNumber = 1,
                LastIrreversibleBlockHeight = 1,
                TimestampUtc = committedAtUtc
            }
        });

        var proposal = new ConsensusBlockProposal
        {
            Block = new BlockReference(2, "block-002"),
            ParentBlockHash = "block-001",
            ProposerAddress = "node-local",
            RoundNumber = 2,
            TermNumber = 1,
            LastIrreversibleBlockHeight = 2,
            TimestampUtc = committedAtUtc.AddSeconds(2)
        };

        var intervalViolation = await engine.ValidateBlockAsync(
            proposal,
            new ConsensusValidationContext
            {
                ExpectedHeight = 2,
                PreviousBlock = new BlockReference(1, "block-001")
            });

        var invalidValidatorSet = await engine.ValidateBlockAsync(
            proposal with { TimestampUtc = committedAtUtc.AddSeconds(5) },
            new ConsensusValidationContext
            {
                ExpectedHeight = 2,
                PreviousBlock = new BlockReference(1, "block-001"),
                ExpectedValidators = ["node-local", "validator-b"]
            });

        Assert.False(intervalViolation.IsValid);
        Assert.Equal("block_interval_violation", intervalViolation.ErrorCode);
        Assert.False(invalidValidatorSet.IsValid);
        Assert.Equal("unexpected_validator_set", invalidValidatorSet.ErrorCode);
    }

    [Fact]
    public async Task OnBlockCommitted_And_ForkChoice_Should_Update_State_And_Prefer_Highest_Head()
    {
        var engine = CreateEngine();
        var proposal = new ConsensusBlockProposal
        {
            Block = new BlockReference(7, "block-007"),
            ParentBlockHash = "block-006",
            ProposerAddress = "node-local",
            RoundNumber = 7,
            TermNumber = 1,
            LastIrreversibleBlockHeight = 7,
            TimestampUtc = new DateTimeOffset(2026, 4, 16, 12, 33, 0, TimeSpan.Zero)
        };

        await engine.OnBlockCommittedAsync(new ConsensusCommitContext
        {
            Block = proposal
        });

        var validators = await engine.GetValidatorsAsync(new ConsensusValidatorQuery
        {
            RoundNumber = 7,
            TermNumber = 1
        });
        var selected = await engine.ForkChoiceAsync(new ConsensusForkChoiceContext
        {
            Candidates =
            [
                new ConsensusChainHeadCandidate
                {
                    Head = new BlockReference(10, "head-a"),
                    LastIrreversibleBlock = new BlockReference(10, "lib-a"),
                    ProducerAddress = "node-local",
                    RoundNumber = 10,
                    TermNumber = 1
                },
                new ConsensusChainHeadCandidate
                {
                    Head = new BlockReference(11, "head-b"),
                    LastIrreversibleBlock = new BlockReference(10, "lib-b"),
                    ProducerAddress = "node-local",
                    RoundNumber = 11,
                    TermNumber = 1
                }
            ]
        });

        Assert.Equal(proposal.Block, engine.LastCommittedBlock);
        Assert.Equal(proposal.Block, engine.LastIrreversibleBlock);
        Assert.Equal(proposal.TimestampUtc, engine.LastCommittedTimestampUtc);
        Assert.Equal(7, engine.CurrentRoundNumber);
        Assert.Equal(1, engine.CurrentTermNumber);
        Assert.Equal(["node-local"], validators.Select(static item => item.Address).ToArray());
        Assert.Equal("head-b", selected.Head.Hash);
    }

    private static SingleValidatorConsensusEngine CreateEngine(TimeSpan? blockInterval = null)
    {
        return new SingleValidatorConsensusEngine(new SingleValidatorConsensusOptions
        {
            ValidatorAddress = "node-local",
            BlockInterval = blockInterval ?? TimeSpan.FromSeconds(4)
        });
    }
}
