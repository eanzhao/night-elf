namespace NightElf.Sdk.CSharp;

public abstract class CSharpSmartContract
{
    public byte[] Dispatch(ContractInvocation invocation)
    {
        return Dispatch(invocation.MethodName, invocation.Input);
    }

    public byte[] Dispatch(string methodName, byte[]? input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        return DispatchCore(methodName, input is null ? ReadOnlyMemory<byte>.Empty : input);
    }

    protected abstract byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input);
}
