using NightElf.Core.Modularity;
using NightElf.Database;

namespace NightElf.Kernel.Core;

[DependsOn(typeof(NightElfDatabaseModule))]
public sealed class NightElfKernelCoreModule : NightElfModule
{
}
