namespace NightElf.Kernel.Core;

public sealed class TransactionPoolOptions
{
    public const string SectionName = "NightElf:TransactionPool";

    public int Capacity { get; set; } = 4096;

    public int DefaultBatchSize { get; set; } = 128;

    public long ReferenceBlockValidPeriod { get; set; } = 64 * 8;

    public void Validate()
    {
        if (Capacity <= 0)
        {
            throw new InvalidOperationException("NightElf:TransactionPool:Capacity must be greater than zero.");
        }

        if (DefaultBatchSize <= 0)
        {
            throw new InvalidOperationException("NightElf:TransactionPool:DefaultBatchSize must be greater than zero.");
        }

        if (ReferenceBlockValidPeriod <= 0)
        {
            throw new InvalidOperationException("NightElf:TransactionPool:ReferenceBlockValidPeriod must be greater than zero.");
        }
    }
}
