namespace NightElf.Kernel.Parallel;

public enum TransactionConflictKind
{
    WriteWrite = 0,
    WriteRead = 1,
    ReadWrite = 2
}
