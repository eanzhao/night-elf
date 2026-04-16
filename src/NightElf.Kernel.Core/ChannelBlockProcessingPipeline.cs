using System.Threading.Channels;

using NightElf.Database;

namespace NightElf.Kernel.Core;

public sealed class ChannelBlockProcessingPipeline : IBlockProcessingPipeline
{
    private readonly Lock _lifecycleLock = new();
    private readonly Channel<QueuedBlockProcessingItem> _queue;
    private readonly IChainStateStore _chainStateStore;
    private readonly IBlockSyncNotifier _syncNotifier;
    private readonly INonCriticalEventBus _nonCriticalEventBus;
    private readonly BlockProcessingPipelineOptions _options;

    private Task? _workerTask;
    private bool _started;
    private bool _stopRequested;
    private int _backlogCount;
    private long _enqueuedCount;
    private long _processedCount;
    private long _failedCount;
    private BlockReference? _lastProcessedBlock;
    private BlockReference? _lastFailedBlock;

    public ChannelBlockProcessingPipeline(
        IChainStateStore chainStateStore,
        IBlockSyncNotifier? syncNotifier = null,
        INonCriticalEventBus? nonCriticalEventBus = null,
        BlockProcessingPipelineOptions? options = null)
    {
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
        _syncNotifier = syncNotifier ?? new NullBlockSyncNotifier();
        _nonCriticalEventBus = nonCriticalEventBus ?? new InMemoryNonCriticalEventBus();
        _options = options ?? new BlockProcessingPipelineOptions();
        _options.Validate();
        _queue = Channel.CreateBounded<QueuedBlockProcessingItem>(_options.CreateChannelOptions());
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleLock)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            if (_stopRequested)
            {
                throw new InvalidOperationException("Block processing pipeline cannot be started after stop was requested.");
            }

            _workerTask = Task.Run(ProcessLoopAsync, CancellationToken.None);
            _started = true;
        }

        return Task.CompletedTask;
    }

    public async ValueTask<BlockProcessingTicket> EnqueueAsync(
        BlockProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        EnsureStarted();

        var completion = new TaskCompletionSource<BlockProcessingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer
            .WriteAsync(new QueuedBlockProcessingItem(request, completion), cancellationToken)
            .ConfigureAwait(false);

        var backlogCount = Interlocked.Increment(ref _backlogCount);
        var enqueuedCount = Interlocked.Increment(ref _enqueuedCount);

        await PublishTelemetryAsync(
                new BlockProcessingTelemetryEvent
                {
                    Kind = BlockProcessingTelemetryKind.Enqueued,
                    Block = request.Block,
                    Source = request.Source,
                    BacklogCount = backlogCount,
                    Details = $"enqueued={enqueuedCount}"
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new BlockProcessingTicket(completion.Task);
    }

    public BlockProcessingPipelineSnapshot GetSnapshot()
    {
        return new BlockProcessingPipelineSnapshot
        {
            Capacity = _options.Capacity,
            BacklogCount = Volatile.Read(ref _backlogCount),
            EnqueuedCount = Interlocked.Read(ref _enqueuedCount),
            ProcessedCount = Interlocked.Read(ref _processedCount),
            FailedCount = Interlocked.Read(ref _failedCount),
            IsRunning = _started && _workerTask is { IsCompleted: false },
            LastProcessedBlock = Volatile.Read(ref _lastProcessedBlock),
            LastFailedBlock = Volatile.Read(ref _lastFailedBlock)
        };
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task? workerTask;

        lock (_lifecycleLock)
        {
            if (_stopRequested)
            {
                workerTask = _workerTask;
            }
            else
            {
                _stopRequested = true;
                _queue.Writer.TryComplete();
                workerTask = _workerTask;
            }
        }

        if (workerTask is not null)
        {
            await workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Dispose is cleanup-only. Callers should observe pipeline faults from the ticket or StopAsync.
        }
    }

    private async Task ProcessLoopAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var backlogCount = Interlocked.Decrement(ref _backlogCount);

            try
            {
                var result = await ProcessItemAsync(item.Request).ConfigureAwait(false);
                Interlocked.Increment(ref _processedCount);
                Volatile.Write(ref _lastProcessedBlock, item.Request.Block);
                item.Completion.TrySetResult(result);

                await PublishTelemetryAsync(
                        new BlockProcessingTelemetryEvent
                        {
                            Kind = BlockProcessingTelemetryKind.Processed,
                            Block = item.Request.Block,
                            Source = item.Request.Source,
                            BacklogCount = backlogCount,
                            Details = result.AdvancedCheckpoint is null
                                ? "processed"
                                : $"processed checkpoint={result.AdvancedCheckpoint.Name}"
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failedCount);
                Volatile.Write(ref _lastFailedBlock, item.Request.Block);
                item.Completion.TrySetException(exception);
                _queue.Writer.TryComplete(exception);
                FailRemainingQueuedItems(exception);

                await PublishTelemetryAsync(
                        new BlockProcessingTelemetryEvent
                        {
                            Kind = BlockProcessingTelemetryKind.Failed,
                            Block = item.Request.Block,
                            Source = item.Request.Source,
                            BacklogCount = Math.Max(backlogCount, 0),
                            Details = exception.Message
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);

                throw;
            }
        }
    }

    private async Task<BlockProcessingResult> ProcessItemAsync(BlockProcessingRequest request)
    {
        await _chainStateStore.SetBestChainAsync(request.Block).ConfigureAwait(false);
        await _chainStateStore.ApplyChangesAsync(
                request.Block,
                request.Writes,
                request.Deletes)
            .ConfigureAwait(false);

        StateCheckpointDescriptor? checkpoint = null;
        if (request.AdvanceLibCheckpoint)
        {
            checkpoint = await _chainStateStore.AdvanceLibCheckpointAsync(request.Block).ConfigureAwait(false);
        }

        var notification = new BlockSyncNotification
        {
            Block = request.Block,
            Source = request.Source,
            StateWriteCount = request.Writes.Count,
            StateDeleteCount = request.Deletes?.Count ?? 0,
            AdvancedLibCheckpoint = request.AdvanceLibCheckpoint
        };

        await _syncNotifier.NotifyBlockAcceptedAsync(notification).ConfigureAwait(false);

        return new BlockProcessingResult
        {
            Block = request.Block,
            StateWriteCount = request.Writes.Count,
            StateDeleteCount = request.Deletes?.Count ?? 0,
            AdvancedCheckpoint = checkpoint,
            SyncNotification = notification
        };
    }

    private void EnsureStarted()
    {
        lock (_lifecycleLock)
        {
            if (!_started || _workerTask is null)
            {
                throw new InvalidOperationException("Block processing pipeline must be started before enqueueing blocks.");
            }

            if (_stopRequested)
            {
                throw new InvalidOperationException("Block processing pipeline is stopping and cannot accept new blocks.");
            }
        }
    }

    private void FailRemainingQueuedItems(Exception cause)
    {
        while (_queue.Reader.TryRead(out var queuedItem))
        {
            Interlocked.Decrement(ref _backlogCount);
            queuedItem.Completion.TrySetException(
                new InvalidOperationException(
                    "Block processing pipeline aborted because a prior block failed.",
                    cause));
        }
    }

    private async Task PublishTelemetryAsync(
        BlockProcessingTelemetryEvent telemetryEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await _nonCriticalEventBus.PublishAsync(telemetryEvent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Non-critical telemetry must not influence block processing control flow.
        }
    }

    private sealed record QueuedBlockProcessingItem(
        BlockProcessingRequest Request,
        TaskCompletionSource<BlockProcessingResult> Completion);
}
