using Microsoft.Extensions.Logging;

using NightElf.Kernel.Core;

namespace NightElf.Launcher;

public sealed class LoggingBlockSyncNotifier : IBlockSyncNotifier
{
    private readonly ILogger<LoggingBlockSyncNotifier> _logger;

    public LoggingBlockSyncNotifier(ILogger<LoggingBlockSyncNotifier> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task NotifyBlockAcceptedAsync(
        BlockSyncNotification notification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(notification);

        _logger.LogInformation(
            "Pipeline accepted block {Height}:{Hash} from {Source} (writes={Writes}, deletes={Deletes}, libCheckpoint={AdvanceLibCheckpoint}).",
            notification.Block.Height,
            notification.Block.Hash,
            notification.Source,
            notification.StateWriteCount,
            notification.StateDeleteCount,
            notification.AdvancedLibCheckpoint);

        return Task.CompletedTask;
    }
}
