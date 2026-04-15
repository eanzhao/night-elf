using NightElf.Kernel.Parallel;
using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.Parallel.Tests;

public sealed class ParallelExecutionPlannerTests
{
    [Fact]
    public void ConflictDetection_Should_Separate_Committed_And_Conflicting_Transactions()
    {
        var detector = new ConflictDetectionService();
        var transactions = new[]
        {
            CreateTransaction("tx-a", readKeys: ["account/a"], writeKeys: ["account/b"]),
            CreateTransaction("tx-b", readKeys: ["account/b"], writeKeys: ["account/c"]),
            CreateTransaction("tx-c", writeKeys: ["account/z"])
        };

        var result = detector.Analyze(transactions);

        Assert.Single(result.CommittedTransactions);
        Assert.Equal("tx-c", result.CommittedTransactions[0].TransactionId);
        Assert.Single(result.ConflictComponents);
        Assert.Equal(["tx-a", "tx-b"], result.ConflictComponents[0].Transactions.Select(static item => item.TransactionId));
        Assert.Equal(2d / 3d, result.ConflictRatio, precision: 6);
    }

    [Fact]
    public void Planner_Should_Regroup_Conflicts_Into_Parallel_Retry_Groups()
    {
        var planner = new ParallelExecutionPlanner(new ParallelExecutionRetryOptions
        {
            OptimisticGroupSize = 8,
            MaxRetryRounds = 2,
            MaxTransactionsPerRetryGroup = 8,
            InitialGroupTimeout = TimeSpan.FromMilliseconds(300)
        });

        var plan = planner.BuildPlan([
            CreateTransaction("tx-a", writeKeys: ["k1"]),
            CreateTransaction("tx-b", readKeys: ["k1"], writeKeys: ["k2"]),
            CreateTransaction("tx-c", readKeys: ["k2"]),
            CreateTransaction("tx-d", writeKeys: ["k9"])
        ]);

        Assert.False(plan.RetryExhausted);
        Assert.Equal(2, plan.Rounds.Count);
        Assert.Equal(["tx-d"], plan.Rounds[0].CommittedTransactions.Select(static item => item.TransactionId));
        Assert.Equal(ParallelExecutionGroupKind.Retry, plan.Rounds[1].Groups[0].Kind);
        Assert.Contains(plan.Rounds[1].Groups, group => group.Transactions.Select(static tx => tx.TransactionId).SequenceEqual(["tx-a", "tx-c"]));
        Assert.Contains(plan.Rounds[1].Groups, group => group.Transactions.Select(static tx => tx.TransactionId).SequenceEqual(["tx-b"]));
        Assert.Equal(["tx-a", "tx-b", "tx-c", "tx-d"], plan.CommittedTransactions.Select(static item => item.TransactionId));
    }

    [Fact]
    public void Planner_Should_Stop_When_Retry_Limit_Is_Exhausted()
    {
        var planner = new ParallelExecutionPlanner(new ParallelExecutionRetryOptions
        {
            OptimisticGroupSize = 8,
            MaxRetryRounds = 0,
            InitialGroupTimeout = TimeSpan.FromMilliseconds(250)
        });

        var plan = planner.BuildPlan([
            CreateTransaction("tx-a", writeKeys: ["shared"]),
            CreateTransaction("tx-b", writeKeys: ["shared"]),
            CreateTransaction("tx-c", writeKeys: ["other"])
        ]);

        Assert.True(plan.RetryExhausted);
        Assert.Single(plan.Rounds);
        Assert.Single(plan.CommittedTransactions);
        Assert.Equal("tx-c", plan.CommittedTransactions[0].TransactionId);
        Assert.Single(plan.RemainingConflictComponents);
        Assert.Equal(["tx-a", "tx-b"], plan.RemainingConflictComponents[0].Transactions.Select(static item => item.TransactionId));
    }

    [Fact]
    public void Planner_Should_Apply_Adaptive_Timeouts_For_Retry_Rounds()
    {
        var planner = new ParallelExecutionPlanner(new ParallelExecutionRetryOptions
        {
            OptimisticGroupSize = 8,
            MaxRetryRounds = 1,
            InitialGroupTimeout = TimeSpan.FromMilliseconds(400),
            RetryTimeoutMultiplier = 2.0d,
            ConflictTimeoutBias = 0.5d
        });

        var plan = planner.BuildPlan([
            CreateTransaction("tx-a", writeKeys: ["k1"]),
            CreateTransaction("tx-b", readKeys: ["k1"], writeKeys: ["k2"]),
            CreateTransaction("tx-c", readKeys: ["k2"]),
            CreateTransaction("tx-d", writeKeys: ["k9"])
        ]);

        Assert.Equal(TimeSpan.FromMilliseconds(400), plan.Rounds[0].Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1100), plan.Rounds[1].Timeout);
    }

    [Fact]
    public void Planner_Should_Handle_HighConflict_Batches_With_Retry_Groups()
    {
        var planner = new ParallelExecutionPlanner(new ParallelExecutionRetryOptions
        {
            OptimisticGroupSize = 64,
            MaxRetryRounds = 1,
            MaxTransactionsPerRetryGroup = 64,
            InitialGroupTimeout = TimeSpan.FromMilliseconds(350)
        });

        var transactions = Enumerable.Range(0, 16)
            .Select(index => CreateTransaction($"tx-{index:D2}", writeKeys: ["shared"]))
            .ToArray();

        var plan = planner.BuildPlan(transactions);

        Assert.False(plan.RetryExhausted);
        Assert.Equal(2, plan.Rounds.Count);
        Assert.Single(plan.Rounds[0].ConflictComponents);
        Assert.Equal(16, plan.Rounds[1].Groups.Count);
        Assert.All(plan.Rounds[1].Groups, static group => Assert.Single(group.Transactions));
        Assert.Equal(16, plan.CommittedTransactions.Count);
    }

    [Fact]
    public void RetryOptions_Should_Reject_Invalid_Configuration()
    {
        var options = new ParallelExecutionRetryOptions
        {
            OptimisticGroupSize = 0
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(nameof(ParallelExecutionRetryOptions.OptimisticGroupSize), exception.Message, StringComparison.Ordinal);
    }

    private static ParallelTransaction CreateTransaction(
        string transactionId,
        IEnumerable<string>? readKeys = null,
        IEnumerable<string>? writeKeys = null)
    {
        return new ParallelTransaction(transactionId, ContractResourceSet.Create(readKeys, writeKeys));
    }
}
