using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.SmartContract;

public sealed class AuthorizingContractCallHandler : IContractCallHandler
{
    private readonly Func<ContractExecutionContext> _callerContextAccessor;
    private readonly Func<string, ContractCallTargetInfo?> _targetResolver;
    private readonly Func<ContractCallDispatchRequest, byte[]> _dispatcher;
    private readonly IContractCallPermissionGrantChecker _permissionGrantChecker;
    private readonly Action<CrossContractCallDeniedEvent>? _deniedSink;

    public AuthorizingContractCallHandler(
        Func<ContractExecutionContext> callerContextAccessor,
        Func<string, ContractCallTargetInfo?> targetResolver,
        Func<ContractCallDispatchRequest, byte[]> dispatcher,
        IContractCallPermissionGrantChecker permissionGrantChecker,
        Action<CrossContractCallDeniedEvent>? deniedSink = null)
    {
        _callerContextAccessor = callerContextAccessor ?? throw new ArgumentNullException(nameof(callerContextAccessor));
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _permissionGrantChecker = permissionGrantChecker ?? throw new ArgumentNullException(nameof(permissionGrantChecker));
        _deniedSink = deniedSink;
    }

    public byte[] Call(string contractAddress, ContractInvocation invocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddress);

        return Dispatch(contractAddress, invocation, isInline: false);
    }

    public void SendInline(string contractAddress, ContractInvocation invocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddress);

        _ = Dispatch(contractAddress, invocation, isInline: true);
    }

    private byte[] Dispatch(
        string contractAddress,
        ContractInvocation invocation,
        bool isInline)
    {
        var callerContext = _callerContextAccessor();
        var target = _targetResolver(contractAddress) ??
            throw new InvalidOperationException($"Contract address '{contractAddress}' is not deployed.");

        EnsureAuthorized(callerContext, target, invocation, isInline);

        return _dispatcher(
            new ContractCallDispatchRequest(
                callerContext,
                target,
                invocation,
                ResolveEffectiveCallerTreatyId(callerContext, target),
                isInline));
    }

    private void EnsureAuthorized(
        ContractExecutionContext callerContext,
        ContractCallTargetInfo target,
        ContractInvocation invocation,
        bool isInline)
    {
        if (!callerContext.IsDynamicContract)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(callerContext.CallerTreatyId))
        {
            Deny(
                callerContext,
                target,
                invocation,
                isInline,
                $"Dynamic contract '{callerContext.CurrentContractAddress}' is not associated with a treaty and cannot call '{target.ContractName}.{invocation.MethodName}'.");
        }

        if (target.IsSystemContract)
        {
            return;
        }

        if (_permissionGrantChecker.IsAllowed(
                callerContext.CallerTreatyId!,
                callerContext.SenderAddress,
                target,
                invocation))
        {
            return;
        }

        Deny(
            callerContext,
            target,
            invocation,
            isInline,
            $"Cross-contract call '{callerContext.CurrentContractAddress}' -> '{target.ContractName}.{invocation.MethodName}' is not permitted under treaty '{callerContext.CallerTreatyId}'.");
    }

    private static string? ResolveEffectiveCallerTreatyId(
        ContractExecutionContext callerContext,
        ContractCallTargetInfo target)
    {
        if (!string.IsNullOrWhiteSpace(callerContext.CallerTreatyId))
        {
            return callerContext.CallerTreatyId;
        }

        return target.IsDynamicContract ? target.OwningTreatyId : null;
    }

    private void Deny(
        ContractExecutionContext callerContext,
        ContractCallTargetInfo target,
        ContractInvocation invocation,
        bool isInline,
        string reason)
    {
        _deniedSink?.Invoke(
            new CrossContractCallDeniedEvent(
                CallerContractAddress: callerContext.CurrentContractAddress,
                SenderAddress: callerContext.SenderAddress,
                CallerTreatyId: callerContext.CallerTreatyId,
                TargetContractAddress: target.ContractAddress,
                TargetContractName: target.ContractName,
                TargetMethodName: invocation.MethodName,
                IsInline: isInline,
                Reason: reason));

        throw new CrossContractCallDeniedException(reason);
    }
}
