namespace NightElf.Kernel.SmartContract;

public sealed record CrossContractCallDeniedEvent(
    string CallerContractAddress,
    string SenderAddress,
    string? CallerTreatyId,
    string TargetContractAddress,
    string TargetContractName,
    string TargetMethodName,
    bool IsInline,
    string Reason);
