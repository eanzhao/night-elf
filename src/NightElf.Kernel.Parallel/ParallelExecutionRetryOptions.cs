namespace NightElf.Kernel.Parallel;

public sealed class ParallelExecutionRetryOptions
{
    public int OptimisticGroupSize { get; set; } = 32;

    public int MaxRetryRounds { get; set; } = 1;

    public int MaxTransactionsPerRetryGroup { get; set; } = 32;

    public TimeSpan InitialGroupTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    public double RetryTimeoutMultiplier { get; set; } = 1.5d;

    public double ConflictTimeoutBias { get; set; } = 1.0d;

    public void Validate()
    {
        if (OptimisticGroupSize <= 0)
        {
            throw new InvalidOperationException("OptimisticGroupSize must be greater than zero.");
        }

        if (MaxRetryRounds < 0)
        {
            throw new InvalidOperationException("MaxRetryRounds cannot be negative.");
        }

        if (MaxTransactionsPerRetryGroup <= 0)
        {
            throw new InvalidOperationException("MaxTransactionsPerRetryGroup must be greater than zero.");
        }

        if (InitialGroupTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("InitialGroupTimeout must be greater than zero.");
        }

        if (RetryTimeoutMultiplier < 1.0d)
        {
            throw new InvalidOperationException("RetryTimeoutMultiplier must be at least 1.0.");
        }

        if (ConflictTimeoutBias < 0.0d)
        {
            throw new InvalidOperationException("ConflictTimeoutBias cannot be negative.");
        }
    }

    public TimeSpan GetRoundTimeout(int roundNumber, double previousConflictRatio)
    {
        if (roundNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundNumber));
        }

        var boundedConflictRatio = Math.Clamp(previousConflictRatio, 0.0d, 1.0d);
        var retryFactor = Math.Pow(RetryTimeoutMultiplier, roundNumber);
        var adaptiveFactor = 1.0d + (boundedConflictRatio * ConflictTimeoutBias);
        var ticks = InitialGroupTimeout.Ticks * retryFactor * adaptiveFactor;
        if (!double.IsFinite(ticks) || ticks > TimeSpan.MaxValue.Ticks)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromTicks(Math.Max(1L, (long)Math.Ceiling(ticks)));
    }
}
