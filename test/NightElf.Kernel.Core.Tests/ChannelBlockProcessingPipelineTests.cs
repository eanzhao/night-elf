using System.Text;

namespace NightElf.Kernel.Core.Tests;

public sealed class ChannelBlockProcessingPipelineTests
{
    private static readonly Encoding TextEncoding = Encoding.UTF8;

    [Fact]
    public async Task EnqueueAsync_Should_Process_State_Advance_Lib_And_Notify_Sync_In_Order()
    {
        using var harness = new ChainStateRecoveryHarness(
            retainedCheckpointCount: 4,
            removeOutdatedCheckpoints: false);
        var syncNotifier = new RecordingBlockSyncNotifier();
        var eventBus = new InMemoryNonCriticalEventBus();
        var telemetry = new List<BlockProcessingTelemetryEvent>();
        using var subscription = eventBus.Subscribe<BlockProcessingTelemetryEvent>((eventData, _) =>
        {
            telemetry.Add(eventData);
            return Task.CompletedTask;
        });

        await using var pipeline = new ChannelBlockProcessingPipeline(
            harness.ChainStateStore,
            syncNotifier,
            eventBus,
            new BlockProcessingPipelineOptions
            {
                Capacity = 4
            });
        await pipeline.StartAsync();

        var block10 = new BlockReference(10, "block-010");
        var ticket10 = await pipeline.EnqueueAsync(new BlockProcessingRequest
        {
            Block = block10,
            Source = "block-sync",
            AdvanceLibCheckpoint = true,
            Writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [ChainStateRecoveryHarness.BalanceKey] = Encode("balance-10"),
                [ChainStateRecoveryHarness.StateRootKey] = Encode("root-10"),
                [ChainStateRecoveryHarness.PrimaryIndexKey] = Encode("tx-alpha:block-010"),
                [ChainStateRecoveryHarness.SecondaryIndexKey] = Encode("tx-stale:block-010")
            }
        });
        var result10 = await ticket10.Completion;

        var block11 = new BlockReference(11, "block-011");
        var ticket11 = await pipeline.EnqueueAsync(new BlockProcessingRequest
        {
            Block = block11,
            Source = "block-sync",
            Writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [ChainStateRecoveryHarness.BalanceKey] = Encode("balance-11"),
                [ChainStateRecoveryHarness.StateRootKey] = Encode("root-11"),
                [ChainStateRecoveryHarness.PrimaryIndexKey] = Encode("tx-alpha:block-011")
            },
            Deletes = [ChainStateRecoveryHarness.SecondaryIndexKey]
        });
        var result11 = await ticket11.Completion;

        await harness.AssertConsistentSnapshotAsync(
            block11,
            expectedBalance: "balance-11",
            expectedStateRoot: "root-11",
            expectedPrimaryIndex: "tx-alpha:block-011",
            expectedSecondaryIndex: string.Empty,
            secondaryIndexDeleted: true);

        var checkpoints = await harness.ChainStateStore.GetLibCheckpointsAsync();
        var retainedCheckpoint = Assert.Single(checkpoints);

        Assert.Equal(block10, result10.Block);
        Assert.NotNull(result10.AdvancedCheckpoint);
        Assert.Equal(retainedCheckpoint.HybridLogCheckpointToken, result10.AdvancedCheckpoint!.HybridLogCheckpointToken);
        Assert.Equal(block11, result11.Block);
        Assert.Null(result11.AdvancedCheckpoint);
        Assert.Equal(
            [block10, block11],
            syncNotifier.Notifications.Select(static item => item.Block).ToArray());
        Assert.Equal(4, telemetry.Count);
        Assert.Equal(2, telemetry.Count(static item => item.Kind == BlockProcessingTelemetryKind.Enqueued));
        Assert.Equal(2, telemetry.Count(static item => item.Kind == BlockProcessingTelemetryKind.Processed));
        Assert.DoesNotContain(telemetry, static item => item.Kind == BlockProcessingTelemetryKind.Failed);
        Assert.Equal(
            [block10, block11],
            telemetry
                .Where(static item => item.Kind == BlockProcessingTelemetryKind.Processed)
                .Select(static item => item.Block)
                .ToArray());
    }

    [Fact]
    public async Task EnqueueAsync_Should_Wait_When_Channel_Is_Full_Until_The_Consumer_Makes_Progress()
    {
        using var harness = new ChainStateRecoveryHarness();
        var syncNotifier = new BlockingFirstSyncNotifier();

        await using var pipeline = new ChannelBlockProcessingPipeline(
            harness.ChainStateStore,
            syncNotifier,
            new InMemoryNonCriticalEventBus(),
            new BlockProcessingPipelineOptions
            {
                Capacity = 1
            });
        await pipeline.StartAsync();

        var firstTicket = await pipeline.EnqueueAsync(CreateRequest(10, "block-010", "value-10"));
        await syncNotifier.WaitForFirstNotificationAsync();

        var secondTicket = await pipeline.EnqueueAsync(CreateRequest(11, "block-011", "value-11"));
        var thirdEnqueueTask = pipeline.EnqueueAsync(CreateRequest(12, "block-012", "value-12")).AsTask();

        await Task.Delay(100);
        Assert.False(thirdEnqueueTask.IsCompleted);

        syncNotifier.ReleaseFirstNotification();

        var thirdTicket = await thirdEnqueueTask.WaitAsync(TimeSpan.FromSeconds(5));
        await firstTicket.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await secondTicket.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await thirdTicket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = pipeline.GetSnapshot();

        Assert.Equal(0, snapshot.BacklogCount);
        Assert.Equal(3, snapshot.EnqueuedCount);
        Assert.Equal(3, snapshot.ProcessedCount);
        Assert.Equal(0, snapshot.FailedCount);
        Assert.Equal(new BlockReference(12, "block-012"), snapshot.LastProcessedBlock);
    }

    [Fact]
    public async Task EventBus_Failures_Should_Not_Break_The_Critical_Path()
    {
        using var harness = new ChainStateRecoveryHarness();

        await using var pipeline = new ChannelBlockProcessingPipeline(
            harness.ChainStateStore,
            new RecordingBlockSyncNotifier(),
            new ThrowingNonCriticalEventBus(),
            new BlockProcessingPipelineOptions());
        await pipeline.StartAsync();

        var block = new BlockReference(10, "block-010");
        var ticket = await pipeline.EnqueueAsync(new BlockProcessingRequest
        {
            Block = block,
            Source = "block-sync",
            AdvanceLibCheckpoint = true,
            Writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [ChainStateRecoveryHarness.BalanceKey] = Encode("balance-10"),
                [ChainStateRecoveryHarness.StateRootKey] = Encode("root-10"),
                [ChainStateRecoveryHarness.PrimaryIndexKey] = Encode("tx-alpha:block-010")
            }
        });

        var result = await ticket.Completion;

        Assert.Equal(block, result.Block);
        Assert.NotNull(result.AdvancedCheckpoint);
    }

    [Fact]
    public async Task Sync_Notification_Failures_Should_Fault_The_Pipeline_And_Surface_The_Problem()
    {
        using var harness = new ChainStateRecoveryHarness();
        var notifier = new FailingBlockSyncNotifier();

        await using var pipeline = new ChannelBlockProcessingPipeline(
            harness.ChainStateStore,
            notifier,
            new InMemoryNonCriticalEventBus(),
            new BlockProcessingPipelineOptions());
        await pipeline.StartAsync();

        var block = new BlockReference(10, "block-010");
        var ticket = await pipeline.EnqueueAsync(new BlockProcessingRequest
        {
            Block = block,
            Source = "block-sync",
            Writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [ChainStateRecoveryHarness.BalanceKey] = Encode("balance-10"),
                [ChainStateRecoveryHarness.StateRootKey] = Encode("root-10"),
                [ChainStateRecoveryHarness.PrimaryIndexKey] = Encode("tx-alpha:block-010")
            }
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ticket.Completion);
        var snapshot = pipeline.GetSnapshot();

        Assert.Contains("sync notifier failure", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, snapshot.FailedCount);
        Assert.Equal(block, snapshot.LastFailedBlock);
    }

    private static BlockProcessingRequest CreateRequest(long height, string hash, string value)
    {
        return new BlockProcessingRequest
        {
            Block = new BlockReference(height, hash),
            Source = "block-sync",
            Writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [ChainStateRecoveryHarness.BalanceKey] = Encode(value),
                [ChainStateRecoveryHarness.StateRootKey] = Encode($"root:{value}"),
                [ChainStateRecoveryHarness.PrimaryIndexKey] = Encode($"tx:{value}")
            }
        };
    }

    private static byte[] Encode(string value)
    {
        return TextEncoding.GetBytes(value);
    }

    private sealed class RecordingBlockSyncNotifier : IBlockSyncNotifier
    {
        public List<BlockSyncNotification> Notifications { get; } = [];

        public Task NotifyBlockAcceptedAsync(
            BlockSyncNotification notification,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingFirstSyncNotifier : IBlockSyncNotifier
    {
        private readonly TaskCompletionSource _firstNotificationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstNotificationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _notificationCount;

        public async Task NotifyBlockAcceptedAsync(
            BlockSyncNotification notification,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Increment(ref _notificationCount) == 1)
            {
                _firstNotificationObserved.TrySetResult();
                await _firstNotificationRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public Task WaitForFirstNotificationAsync()
        {
            return _firstNotificationObserved.Task;
        }

        public void ReleaseFirstNotification()
        {
            _firstNotificationRelease.TrySetResult();
        }
    }

    private sealed class ThrowingNonCriticalEventBus : INonCriticalEventBus
    {
        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
            where TEvent : class
        {
            return NoopDisposable.Instance;
        }

        public Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default)
            where TEvent : class
        {
            throw new InvalidOperationException("non-critical event bus failure");
        }
    }

    private sealed class FailingBlockSyncNotifier : IBlockSyncNotifier
    {
        public Task NotifyBlockAcceptedAsync(
            BlockSyncNotification notification,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("sync notifier failure");
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
