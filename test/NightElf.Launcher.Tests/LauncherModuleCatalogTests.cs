using NightElf.Core;
using NightElf.Database;
using NightElf.Database.Hosting;
using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;
using NightElf.OS.Network;
using NightElf.Runtime.CSharp;

namespace NightElf.Launcher.Tests;

public sealed class LauncherModuleCatalogTests
{
    [Fact]
    public void DefaultCatalog_Should_Order_Modules_By_Dependency()
    {
        var catalog = new LauncherModuleCatalog();

        var loadOrder = catalog.ResolveLoadOrder().ToList();

        Assert.True(loadOrder.IndexOf(typeof(NightElfCoreModule)) < loadOrder.IndexOf(typeof(NightElfDatabaseModule)));
        Assert.True(loadOrder.IndexOf(typeof(NightElfDatabaseModule)) < loadOrder.IndexOf(typeof(NightElfKernelCoreModule)));
        Assert.True(loadOrder.IndexOf(typeof(NightElfKernelCoreModule)) < loadOrder.IndexOf(typeof(NightElfRuntimeCSharpModule)));
        Assert.Contains(typeof(NightElfDatabaseHostingModule), loadOrder);
        Assert.Contains(typeof(NightElfOSNetworkModule), loadOrder);
        Assert.Contains(typeof(NightElfKernelConsensusModule), loadOrder);
    }
}
