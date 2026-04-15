namespace NightElf.Database;

public readonly record struct StateCommitVersion
{
    public StateCommitVersion(long blockHeight, string blockHash)
    {
        if (blockHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockHeight));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(blockHash);

        BlockHeight = blockHeight;
        BlockHash = blockHash;
    }

    public long BlockHeight { get; }

    public string BlockHash { get; }
}
