using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.Parallel;

public readonly record struct ContractResourceExtractionResult(ContractResourceSet Resources, bool UsedFallback);
