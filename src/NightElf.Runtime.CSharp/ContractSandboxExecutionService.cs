using NightElf.Kernel.SmartContract;
using NightElf.Sdk.CSharp;

namespace NightElf.Runtime.CSharp;

public sealed class ContractSandboxExecutionService
{
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> execution,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await execution(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new ContractSandboxTimeoutException(timeout);
        }
    }

    public Task<byte[]> ExecuteContractAsync(
        SmartContractExecutor executor,
        CSharpSmartContract contract,
        ContractInvocation invocation,
        ContractExecutionContext? executionContext,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(contract);

        return ExecuteAsync(
            _ => Task.Run(() => executor.Execute(contract, invocation, executionContext), CancellationToken.None),
            timeout,
            cancellationToken);
    }
}
