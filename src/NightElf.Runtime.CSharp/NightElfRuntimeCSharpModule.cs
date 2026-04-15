using NightElf.Core.Modularity;
using NightElf.Kernel.SmartContract;

namespace NightElf.Runtime.CSharp;

[DependsOn(typeof(NightElfKernelSmartContractModule))]
public sealed class NightElfRuntimeCSharpModule : NightElfModule
{
}
