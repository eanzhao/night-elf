namespace NightElf.Sdk.CSharp;

public sealed class ContractCallContext
{
    private readonly IContractCallHandler _handler;

    public ContractCallContext(IContractCallHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public byte[] Call(string contractAddress, ContractInvocation invocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddress);

        return _handler.Call(contractAddress, invocation);
    }

    public void SendInline(string contractAddress, ContractInvocation invocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddress);

        _handler.SendInline(contractAddress, invocation);
    }
}
