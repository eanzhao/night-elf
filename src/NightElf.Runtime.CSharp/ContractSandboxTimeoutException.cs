namespace NightElf.Runtime.CSharp;

public sealed class ContractSandboxTimeoutException : TimeoutException
{
    public ContractSandboxTimeoutException(TimeSpan timeout)
        : base($"Contract sandbox execution exceeded timeout '{timeout}'.")
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}
