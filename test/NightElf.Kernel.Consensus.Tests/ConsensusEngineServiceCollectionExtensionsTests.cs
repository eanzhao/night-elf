using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NightElf.Vrf;

namespace NightElf.Kernel.Consensus.Tests;

public sealed class ConsensusEngineServiceCollectionExtensionsTests
{
    [Fact]
    public void AddConsensusEngine_Should_Register_Aedpos_By_Default()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        services.AddConsensusEngine(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<ConsensusEngineOptions>();
        var engine = serviceProvider.GetRequiredService<IConsensusEngine>();
        var vrfProvider = serviceProvider.GetRequiredService<IVrfProvider>();

        Assert.Equal(ConsensusEngineKind.Aedpos, options.ResolveEngineKind());
        Assert.IsType<AedposConsensusEngine>(engine);
        Assert.IsType<DeterministicVrfProvider>(vrfProvider);
    }

    [Fact]
    public void AddConsensusEngine_Should_Use_Configured_Aedpos_Validators()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NightElf:Consensus:Engine"] = "Aedpos",
            ["NightElf:Consensus:Aedpos:Validators:0"] = "miner-1",
            ["NightElf:Consensus:Aedpos:Validators:1"] = "miner-2",
            ["NightElf:Consensus:Aedpos:Validators:2"] = "miner-3",
            ["NightElf:Consensus:Aedpos:BlocksPerRound"] = "5",
            ["NightElf:Consensus:Aedpos:IrreversibleBlockDistance"] = "11"
        });

        services.AddConsensusEngine(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<ConsensusEngineOptions>();

        Assert.Equal(["miner-1", "miner-2", "miner-3"], options.Aedpos.Validators);
        Assert.Equal(5, options.Aedpos.BlocksPerRound);
        Assert.Equal(11, options.Aedpos.IrreversibleBlockDistance);
    }

    [Fact]
    public void AddConsensusEngine_Should_Fail_For_Unsupported_Engine()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NightElf:Consensus:Engine"] = "HotStuff"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddConsensusEngine(configuration));

        Assert.Contains("Unsupported consensus engine 'HotStuff'", exception.Message);
    }

    [Fact]
    public void AddConsensusEngine_Should_Fail_When_Aedpos_Validators_Are_Duplicated()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NightElf:Consensus:Aedpos:Validators:0"] = "miner-1",
            ["NightElf:Consensus:Aedpos:Validators:1"] = "miner-1"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddConsensusEngine(configuration));

        Assert.Contains("duplicate validator 'miner-1'", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
