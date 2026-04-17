namespace NightElf.Kernel.SmartContract;

public sealed class CrossContractCallDeniedException : InvalidOperationException
{
    public CrossContractCallDeniedException(string message)
        : base(message)
    {
    }
}
