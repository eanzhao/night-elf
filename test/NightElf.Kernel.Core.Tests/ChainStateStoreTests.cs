namespace NightElf.Kernel.Core.Tests;

public sealed class ChainStateStoreTests
{
    [Fact]
    public async Task RecoverToLatestLibCheckpoint_Should_Restore_State_Index_And_BestChain_For_A_Single_Fork()
    {
        using var harness = new ChainStateRecoveryHarness();

        var lib10 = await harness.ApplyBlockAsync(
            10,
            "block-010-a",
            balance: "balance-10",
            stateRoot: "root-10",
            primaryIndex: "tx-alpha:block-010-a",
            secondaryIndex: "tx-stale:block-010-a");
        var checkpoint10 = await harness.AdvanceLibAsync(lib10);

        await harness.ApplyBlockAsync(
            11,
            "block-011-a",
            balance: "balance-11a",
            stateRoot: "root-11a",
            primaryIndex: "tx-alpha:block-011-a",
            deleteSecondaryIndex: true);
        await harness.ApplyBlockAsync(
            11,
            "block-011-b",
            balance: "balance-11b",
            stateRoot: "root-11b",
            primaryIndex: "tx-alpha:block-011-b",
            deleteSecondaryIndex: true);

        await harness.ChainStateStore.RecoverToLatestLibCheckpointAsync();

        await harness.AssertConsistentSnapshotAsync(
            lib10,
            expectedBalance: "balance-10",
            expectedStateRoot: "root-10",
            expectedPrimaryIndex: "tx-alpha:block-010-a",
            expectedSecondaryIndex: "tx-stale:block-010-a");

        var checkpoints = await harness.ChainStateStore.GetLibCheckpointsAsync();
        var retainedCheckpoint = Assert.Single(checkpoints);
        Assert.Equal(checkpoint10.HybridLogCheckpointToken, retainedCheckpoint.HybridLogCheckpointToken);
    }

    [Fact]
    public async Task RecoverToLatestLibCheckpoint_Should_Track_The_Newest_Lib_After_Lib_Switches()
    {
        using var harness = new ChainStateRecoveryHarness(
            retainedCheckpointCount: 4,
            removeOutdatedCheckpoints: false);

        var lib10 = await harness.ApplyBlockAsync(
            10,
            "block-010-a",
            balance: "balance-10",
            stateRoot: "root-10",
            primaryIndex: "tx-alpha:block-010-a",
            secondaryIndex: "tx-stale:block-010-a");
        await harness.AdvanceLibAsync(lib10);

        await harness.ApplyBlockAsync(
            11,
            "block-011-a",
            balance: "balance-11a",
            stateRoot: "root-11a",
            primaryIndex: "tx-alpha:block-011-a",
            deleteSecondaryIndex: true);
        await harness.ApplyBlockAsync(
            11,
            "block-011-b",
            balance: "balance-11b",
            stateRoot: "root-11b",
            primaryIndex: "tx-alpha:block-011-b",
            deleteSecondaryIndex: true);

        await harness.ChainStateStore.RecoverToLatestLibCheckpointAsync();
        await harness.AssertConsistentSnapshotAsync(
            lib10,
            expectedBalance: "balance-10",
            expectedStateRoot: "root-10",
            expectedPrimaryIndex: "tx-alpha:block-010-a",
            expectedSecondaryIndex: "tx-stale:block-010-a");

        await harness.ApplyBlockAsync(
            11,
            "block-011-a-replay",
            balance: "balance-11a",
            stateRoot: "root-11a",
            primaryIndex: "tx-alpha:block-011-a");
        var lib12 = await harness.ApplyBlockAsync(
            12,
            "block-012-a",
            balance: "balance-12a",
            stateRoot: "root-12a",
            primaryIndex: "tx-alpha:block-012-a",
            secondaryIndex: "tx-stale:block-012-a");
        var checkpoint12 = await harness.AdvanceLibAsync(lib12);

        await harness.ApplyBlockAsync(
            13,
            "block-013-a",
            balance: "balance-13a",
            stateRoot: "root-13a",
            primaryIndex: "tx-alpha:block-013-a",
            deleteSecondaryIndex: true);
        await harness.ApplyBlockAsync(
            13,
            "block-013-b",
            balance: "balance-13b",
            stateRoot: "root-13b",
            primaryIndex: "tx-alpha:block-013-b",
            deleteSecondaryIndex: true);

        await harness.ChainStateStore.RecoverToLatestLibCheckpointAsync();

        await harness.AssertConsistentSnapshotAsync(
            lib12,
            expectedBalance: "balance-12a",
            expectedStateRoot: "root-12a",
            expectedPrimaryIndex: "tx-alpha:block-012-a",
            expectedSecondaryIndex: "tx-stale:block-012-a");

        var checkpoints = await harness.ChainStateStore.GetLibCheckpointsAsync();
        Assert.Equal([10L, 12L], checkpoints.Select(checkpoint => checkpoint.BlockHeight).ToArray());
        Assert.Equal(checkpoint12.HybridLogCheckpointToken, checkpoints[^1].HybridLogCheckpointToken);
    }

    [Fact]
    public async Task RecoverToLatestLibCheckpoint_Should_Allow_Replaying_A_New_Fork_After_Rollback()
    {
        using var harness = new ChainStateRecoveryHarness(
            retainedCheckpointCount: 4,
            removeOutdatedCheckpoints: false);

        var checkpoint10Block = await harness.ApplyBlockAsync(
            10,
            "block-010-a",
            balance: "balance-10",
            stateRoot: "root-10",
            primaryIndex: "tx-alpha:block-010-a",
            secondaryIndex: "tx-stale:block-010-a");
        await harness.AdvanceLibAsync(checkpoint10Block);

        await harness.ApplyBlockAsync(
            11,
            "block-011-a",
            balance: "balance-11a",
            stateRoot: "root-11a",
            primaryIndex: "tx-alpha:block-011-a");
        var checkpoint12Block = await harness.ApplyBlockAsync(
            12,
            "block-012-a",
            balance: "balance-12a",
            stateRoot: "root-12a",
            primaryIndex: "tx-alpha:block-012-a",
            secondaryIndex: "tx-stale:block-012-a");
        await harness.AdvanceLibAsync(checkpoint12Block);

        await harness.ApplyBlockAsync(
            13,
            "block-013-a",
            balance: "balance-13a",
            stateRoot: "root-13a",
            primaryIndex: "tx-alpha:block-013-a",
            deleteSecondaryIndex: true);
        await harness.ChainStateStore.RecoverToLatestLibCheckpointAsync();

        await harness.AssertConsistentSnapshotAsync(
            checkpoint12Block,
            expectedBalance: "balance-12a",
            expectedStateRoot: "root-12a",
            expectedPrimaryIndex: "tx-alpha:block-012-a",
            expectedSecondaryIndex: "tx-stale:block-012-a");

        await harness.ApplyBlockAsync(
            13,
            "block-013-b",
            balance: "balance-13b",
            stateRoot: "root-13b",
            primaryIndex: "tx-alpha:block-013-b",
            deleteSecondaryIndex: true);
        var checkpoint14BranchB = await harness.ApplyBlockAsync(
            14,
            "block-014-b",
            balance: "balance-14b",
            stateRoot: "root-14b",
            primaryIndex: "tx-alpha:block-014-b",
            secondaryIndex: "tx-stale:block-014-b");
        var branchBCheckpoint = await harness.AdvanceLibAsync(checkpoint14BranchB);

        var retainedAfterBranchSwitch = await harness.ChainStateStore.GetLibCheckpointsAsync();
        Assert.Equal([10L, 12L, 14L], retainedAfterBranchSwitch.Select(checkpoint => checkpoint.BlockHeight).ToArray());
        Assert.Equal(branchBCheckpoint.HybridLogCheckpointToken, retainedAfterBranchSwitch[^1].HybridLogCheckpointToken);
    }

    [Fact]
    public async Task RecoverToCheckpoint_Should_Report_Checkpoint_Context_When_Older_Fork_Checkpoint_Mismatches()
    {
        using var harness = new ChainStateRecoveryHarness(
            retainedCheckpointCount: 4,
            removeOutdatedCheckpoints: false);

        var checkpoint10Block = await harness.ApplyBlockAsync(
            10,
            "block-010-a",
            balance: "balance-10",
            stateRoot: "root-10",
            primaryIndex: "tx-alpha:block-010-a",
            secondaryIndex: "tx-stale:block-010-a");
        await harness.AdvanceLibAsync(checkpoint10Block);

        await harness.ApplyBlockAsync(
            11,
            "block-011-a",
            balance: "balance-11a",
            stateRoot: "root-11a",
            primaryIndex: "tx-alpha:block-011-a");
        var checkpoint12Block = await harness.ApplyBlockAsync(
            12,
            "block-012-a",
            balance: "balance-12a",
            stateRoot: "root-12a",
            primaryIndex: "tx-alpha:block-012-a",
            secondaryIndex: "tx-stale:block-012-a");
        var checkpoint12 = await harness.AdvanceLibAsync(checkpoint12Block);

        await harness.ApplyBlockAsync(
            13,
            "block-013-a",
            balance: "balance-13a",
            stateRoot: "root-13a",
            primaryIndex: "tx-alpha:block-013-a",
            deleteSecondaryIndex: true);
        var checkpoint14Block = await harness.ApplyBlockAsync(
            14,
            "block-014-a",
            balance: "balance-14a",
            stateRoot: "root-14a",
            primaryIndex: "tx-alpha:block-014-a",
            secondaryIndex: "tx-stale:block-014-a");
        await harness.AdvanceLibAsync(checkpoint14Block);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ChainStateStore.RecoverToCheckpointAsync(checkpoint12));

        Assert.Contains(checkpoint12.Name, exception.Message, StringComparison.Ordinal);
        Assert.Contains(checkpoint12.BlockHeight.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("checkpoint/token mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverToLatestLibCheckpoint_Should_Throw_When_No_Checkpoint_Exists()
    {
        using var harness = new ChainStateRecoveryHarness();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ChainStateStore.RecoverToLatestLibCheckpointAsync());

        Assert.Contains("No LIB checkpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverToLatestLibCheckpoint_Should_Handle_Forks_At_Multiple_Heights()
    {
        using var harness = new ChainStateRecoveryHarness(
            retainedCheckpointCount: 4,
            removeOutdatedCheckpoints: false);

        var lib10 = await harness.ApplyBlockAsync(
            10, "block-010-a",
            balance: "balance-10", stateRoot: "root-10",
            primaryIndex: "tx-alpha:block-010-a",
            secondaryIndex: "tx-stale:block-010-a");
        await harness.AdvanceLibAsync(lib10);

        // Fork at height 11: two competing blocks
        await harness.ApplyBlockAsync(
            11, "block-011-a",
            balance: "balance-11a", stateRoot: "root-11a",
            primaryIndex: "tx-alpha:block-011-a");
        await harness.ApplyBlockAsync(
            11, "block-011-b",
            balance: "balance-11b", stateRoot: "root-11b",
            primaryIndex: "tx-alpha:block-011-b");

        // Fork at height 12: two competing blocks (on different branch-a continuations)
        await harness.ApplyBlockAsync(
            12, "block-012-a",
            balance: "balance-12a", stateRoot: "root-12a",
            primaryIndex: "tx-alpha:block-012-a");
        await harness.ApplyBlockAsync(
            12, "block-012-b",
            balance: "balance-12b", stateRoot: "root-12b",
            primaryIndex: "tx-alpha:block-012-b",
            deleteSecondaryIndex: true);

        // Fork at height 13: two competing blocks
        await harness.ApplyBlockAsync(
            13, "block-013-a",
            balance: "balance-13a", stateRoot: "root-13a",
            primaryIndex: "tx-alpha:block-013-a");
        await harness.ApplyBlockAsync(
            13, "block-013-b",
            balance: "balance-13b", stateRoot: "root-13b",
            primaryIndex: "tx-alpha:block-013-b",
            deleteSecondaryIndex: true);

        // Recovery should roll back to LIB at height 10
        await harness.ChainStateStore.RecoverToLatestLibCheckpointAsync();

        await harness.AssertConsistentSnapshotAsync(
            lib10,
            expectedBalance: "balance-10",
            expectedStateRoot: "root-10",
            expectedPrimaryIndex: "tx-alpha:block-010-a",
            expectedSecondaryIndex: "tx-stale:block-010-a");
    }

    [Fact]
    public async Task RecoverToLatestLibCheckpoint_Should_Respect_Cancellation()
    {
        using var harness = new ChainStateRecoveryHarness();

        var lib10 = await harness.ApplyBlockAsync(
            10, "block-010-a",
            balance: "balance-10", stateRoot: "root-10",
            primaryIndex: "tx-alpha:block-010-a",
            secondaryIndex: "tx-stale:block-010-a");
        await harness.AdvanceLibAsync(lib10);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.ChainStateStore.RecoverToLatestLibCheckpointAsync(cts.Token));
    }
}
