namespace NightElf.Kernel.Core;

public enum TransactionResultStatus
{
    Unknown = 0,
    Pending = 1,
    Mined = 2,
    Failed = 3,
    Rejected = 4
}

public sealed class TransactionResultRecord
{
    public required string TransactionId { get; init; }

    public required TransactionResultStatus Status { get; init; }

    public string? Error { get; init; }

    public long BlockHeight { get; init; }

    public string? BlockHash { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
