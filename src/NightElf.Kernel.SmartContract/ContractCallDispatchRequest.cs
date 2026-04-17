using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.SmartContract;

public sealed record ContractCallDispatchRequest(
    ContractExecutionContext CallerContext,
    ContractCallTargetInfo Target,
    ContractInvocation Invocation,
    string? EffectiveCallerTreatyId,
    bool IsInline);
