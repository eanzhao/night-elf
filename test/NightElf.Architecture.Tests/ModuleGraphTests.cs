using NightElf.Core;
using NightElf.Core.Modularity;
using NightElf.Database;
using NightElf.Database.Hosting;
using NightElf.Database.Redis;
using NightElf.Database.Tsavorite;
using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;
using NightElf.Kernel.Parallel;
using NightElf.Kernel.SmartContract;
using NightElf.Runtime.CSharp;
using NightElf.Sdk.CSharp;

namespace NightElf.Architecture.Tests;

public sealed class ModuleGraphTests
{
    [Fact]
    public void DatabaseModule_Should_Depend_On_CoreModule()
    {
        var dependency = GetDependencyAttribute<NightElfDatabaseModule>();

        Assert.Contains(typeof(NightElfCoreModule), dependency.Dependencies);
    }

    [Fact]
    public void KernelCoreModule_Should_Depend_On_DatabaseModule()
    {
        var dependency = GetDependencyAttribute<NightElfKernelCoreModule>();

        Assert.Contains(typeof(NightElfDatabaseModule), dependency.Dependencies);
    }

    [Fact]
    public void KernelConsensusModule_Should_Depend_On_KernelCoreModule()
    {
        var dependency = GetDependencyAttribute<NightElfKernelConsensusModule>();

        Assert.Contains(typeof(NightElfKernelCoreModule), dependency.Dependencies);
    }

    [Fact]
    public void KernelSmartContractModule_Should_Depend_On_KernelCoreModule()
    {
        var dependency = GetDependencyAttribute<NightElfKernelSmartContractModule>();

        Assert.Contains(typeof(NightElfKernelCoreModule), dependency.Dependencies);
        Assert.Contains(typeof(NightElfSdkCSharpModule), dependency.Dependencies);
    }

    [Fact]
    public void SdkCSharpModule_Should_Depend_On_CoreModule()
    {
        var dependency = GetDependencyAttribute<NightElfSdkCSharpModule>();

        Assert.Contains(typeof(NightElfCoreModule), dependency.Dependencies);
    }

    [Fact]
    public void KernelParallelModule_Should_Depend_On_KernelSmartContractModule()
    {
        var dependency = GetDependencyAttribute<NightElfKernelParallelModule>();

        Assert.Contains(typeof(NightElfKernelSmartContractModule), dependency.Dependencies);
    }

    [Fact]
    public void RuntimeCSharpModule_Should_Depend_On_KernelSmartContractModule()
    {
        var dependency = GetDependencyAttribute<NightElfRuntimeCSharpModule>();

        Assert.Contains(typeof(NightElfKernelSmartContractModule), dependency.Dependencies);
    }

    [Fact]
    public void DatabaseRedisModule_Should_Depend_On_DatabaseModule()
    {
        var dependency = GetDependencyAttribute<NightElfDatabaseRedisModule>();

        Assert.Contains(typeof(NightElfDatabaseModule), dependency.Dependencies);
    }

    [Fact]
    public void DatabaseHostingModule_Should_Depend_On_Redis_And_Tsavorite_Modules()
    {
        var dependency = GetDependencyAttribute<NightElfDatabaseHostingModule>();

        Assert.Contains(typeof(NightElfDatabaseRedisModule), dependency.Dependencies);
        Assert.Contains(typeof(NightElfDatabaseTsavoriteModule), dependency.Dependencies);
    }

    [Fact]
    public void ChainStateDbContext_Should_Inherit_From_KeyValueDbContext()
    {
        var context = new ChainStateDbContext();

        Assert.IsAssignableFrom<KeyValueDbContext<ChainStateDbContext>>(context);
    }

    private static DependsOnAttribute GetDependencyAttribute<TModule>()
        where TModule : NightElfModule
    {
        var attribute = Attribute.GetCustomAttribute(typeof(TModule), typeof(DependsOnAttribute))
            as DependsOnAttribute;

        Assert.NotNull(attribute);
        return attribute!;
    }
}
