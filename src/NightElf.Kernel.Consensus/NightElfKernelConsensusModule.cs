using NightElf.Core.Modularity;
using NightElf.Kernel.Core;
using NightElf.Vrf;

namespace NightElf.Kernel.Consensus;

[DependsOn(typeof(NightElfKernelCoreModule), typeof(NightElfVrfModule))]
public sealed class NightElfKernelConsensusModule : NightElfModule
{
}
