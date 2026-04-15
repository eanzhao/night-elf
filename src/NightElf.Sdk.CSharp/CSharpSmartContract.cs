namespace NightElf.Sdk.CSharp;

public abstract class CSharpSmartContract
{
    public virtual bool SupportsResourceExtraction => false;

    public byte[] Dispatch(ContractInvocation invocation)
    {
        return Dispatch(invocation.MethodName, invocation.Input);
    }

    public byte[] Dispatch(string methodName, byte[]? input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        return DispatchCore(methodName, input is null ? ReadOnlyMemory<byte>.Empty : input);
    }

    public ContractResourceSet DescribeResources(ContractInvocation invocation)
    {
        return DescribeResources(invocation.MethodName, invocation.Input);
    }

    public ContractResourceSet DescribeResources(string methodName, byte[]? input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        return DescribeResourcesCore(methodName, input is null ? ReadOnlyMemory<byte>.Empty : input);
    }

    protected abstract byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input);

    protected virtual ContractResourceSet DescribeResourcesCore(string methodName, ReadOnlyMemory<byte> input)
    {
        return ContractResourceSet.Empty;
    }
}
