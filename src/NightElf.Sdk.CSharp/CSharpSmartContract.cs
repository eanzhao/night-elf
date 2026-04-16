namespace NightElf.Sdk.CSharp;

public abstract class CSharpSmartContract
{
    private readonly ThreadLocal<ContractExecutionContext?> _executionContext = new();

    public virtual bool SupportsResourceExtraction => false;

    protected ContractExecutionContext ExecutionContext => _executionContext.Value ??
        throw new InvalidOperationException("The contract execution context has not been initialized.");

    protected ContractStateContext StateContext => ExecutionContext.State;

    protected ContractCallContext CallContext => ExecutionContext.Calls;

    protected ContractCryptoContext CryptoContext => ExecutionContext.Crypto;

    protected ContractIdentityContext IdentityContext => ExecutionContext.Identity;

    public byte[] Dispatch(ContractInvocation invocation)
    {
        return Dispatch(invocation.MethodName, invocation.Input);
    }

    public byte[] Dispatch(ContractExecutionContext executionContext, ContractInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(executionContext);

        return Dispatch(executionContext, invocation.MethodName, invocation.Input);
    }

    public byte[] Dispatch(ContractExecutionContext executionContext, string methodName, byte[]? input)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        return DispatchWithinExecutionContext(
            executionContext,
            () => DispatchCore(methodName, input is null ? ReadOnlyMemory<byte>.Empty : input));
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

    private byte[] DispatchWithinExecutionContext(ContractExecutionContext executionContext, Func<byte[]> dispatcher)
    {
        var previousContext = _executionContext.Value;
        _executionContext.Value = executionContext;

        try
        {
            return dispatcher();
        }
        finally
        {
            _executionContext.Value = previousContext;
        }
    }
}
