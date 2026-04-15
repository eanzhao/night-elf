using Tsavorite.core;

namespace NightElf.Database.Tsavorite;

public sealed class TsavoriteStateCheckpointStoreOptions
{
    public string MetadataFileName { get; set; } = "nightelf.state-checkpoints.json";

    public string CheckpointNamePrefix { get; set; } = "lib";

    public int RetainedCheckpointCount { get; set; } = 1;

    public int RecoveryPreloadPages { get; set; }

    public CheckpointType CheckpointType { get; set; } = CheckpointType.Snapshot;

    public bool PreferIncrementalSnapshots { get; set; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MetadataFileName))
        {
            throw new InvalidOperationException("Tsavorite state checkpoint metadata file name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(CheckpointNamePrefix))
        {
            throw new InvalidOperationException("Tsavorite state checkpoint name prefix must not be empty.");
        }

        if (RetainedCheckpointCount <= 0)
        {
            throw new InvalidOperationException("Tsavorite retained checkpoint count must be greater than zero.");
        }

        if (RecoveryPreloadPages < 0)
        {
            throw new InvalidOperationException("Tsavorite recovery preload pages must not be negative.");
        }
    }
}
