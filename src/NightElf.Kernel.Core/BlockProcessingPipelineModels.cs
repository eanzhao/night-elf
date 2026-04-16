using System.Threading.Channels;

using NightElf.Database;

namespace NightElf.Kernel.Core;

public sealed class BlockProcessingPipelineOptions
{
    public int Capacity { get; set; } = 128;

    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    public bool SingleReader { get; set; } = true;

    public bool SingleWriter { get; set; }

    public bool AllowSynchronousContinuations { get; set; }

    public void Validate()
    {
        if (Capacity <= 0)
        {
            throw new InvalidOperationException("Block processing pipeline capacity must be greater than zero.");
        }
    }

    internal BoundedChannelOptions CreateChannelOptions()
    {
        Validate();

        return new BoundedChannelOptions(Capacity)
        {
            FullMode = FullMode,
            SingleReader = SingleReader,
            SingleWriter = SingleWriter,
            AllowSynchronousContinuations = AllowSynchronousContinuations
        };
    }
}

public sealed class BlockProcessingRequest
{
    public required BlockReference Block { get; init; }

    public IReadOnlyDictionary<string, byte[]> Writes { get; init; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    public IReadOnlyCollection<string>? Deletes { get; init; }

    public bool AdvanceLibCheckpoint { get; init; }

    public string Source { get; init; } = "unknown";

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Block);
        ArgumentNullException.ThrowIfNull(Writes);
        ArgumentException.ThrowIfNullOrWhiteSpace(Source);
    }
}

public sealed class BlockProcessingTicket
{
    internal BlockProcessingTicket(Task<BlockProcessingResult> completion)
    {
        Completion = completion ?? throw new ArgumentNullException(nameof(completion));
    }

    public Task<BlockProcessingResult> Completion { get; }
}

public sealed class BlockProcessingResult
{
    public required BlockReference Block { get; init; }

    public required int StateWriteCount { get; init; }

    public required int StateDeleteCount { get; init; }

    public StateCheckpointDescriptor? AdvancedCheckpoint { get; init; }

    public required BlockSyncNotification SyncNotification { get; init; }
}

public sealed class BlockSyncNotification
{
    public required BlockReference Block { get; init; }

    public required string Source { get; init; }

    public required int StateWriteCount { get; init; }

    public required int StateDeleteCount { get; init; }

    public bool AdvancedLibCheckpoint { get; init; }
}

public enum BlockProcessingTelemetryKind
{
    Enqueued,
    Processed,
    Failed
}

public sealed class BlockProcessingTelemetryEvent
{
    public required BlockProcessingTelemetryKind Kind { get; init; }

    public required BlockReference Block { get; init; }

    public required string Source { get; init; }

    public required int BacklogCount { get; init; }

    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? Details { get; init; }
}

public sealed class BlockProcessingPipelineSnapshot
{
    public required int Capacity { get; init; }

    public required int BacklogCount { get; init; }

    public required long EnqueuedCount { get; init; }

    public required long ProcessedCount { get; init; }

    public required long FailedCount { get; init; }

    public required bool IsRunning { get; init; }

    public BlockReference? LastProcessedBlock { get; init; }

    public BlockReference? LastFailedBlock { get; init; }
}

public interface IBlockSyncNotifier
{
    Task NotifyBlockAcceptedAsync(
        BlockSyncNotification notification,
        CancellationToken cancellationToken = default);
}

public sealed class NullBlockSyncNotifier : IBlockSyncNotifier
{
    public Task NotifyBlockAcceptedAsync(
        BlockSyncNotification notification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(notification);
        return Task.CompletedTask;
    }
}

public interface IBlockProcessingPipeline : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    ValueTask<BlockProcessingTicket> EnqueueAsync(
        BlockProcessingRequest request,
        CancellationToken cancellationToken = default);

    BlockProcessingPipelineSnapshot GetSnapshot();

    Task StopAsync(CancellationToken cancellationToken = default);
}
