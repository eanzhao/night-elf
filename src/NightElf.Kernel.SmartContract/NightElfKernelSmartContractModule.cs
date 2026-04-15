using NightElf.Core.Modularity;
using NightElf.Kernel.Core;
using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.SmartContract;

[DependsOn(typeof(NightElfKernelCoreModule), typeof(NightElfSdkCSharpModule))]
public sealed class NightElfKernelSmartContractModule : NightElfModule
{
}
