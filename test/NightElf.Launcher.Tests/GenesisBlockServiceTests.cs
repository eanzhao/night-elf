using NightElf.Database.Tsavorite;
using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;
using NightElf.Vrf;

namespace NightElf.Launcher.Tests;

public sealed class GenesisBlockServiceTests
{
    [Theory]
    [MemberData(nameof(GetConsensusCases))]
    public async Task EnsureGenesisAsync_Should_Create_Genesis_Only_Once(string engineName)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "nightelf-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var (consensusOptions, consensusEngine, validators) = CreateConsensus(engineName);
            var launcherOptions = new LauncherOptions
            {
                DataRootPath = Path.Combine(rootPath, "data"),
                CheckpointRootPath = Path.Combine(rootPath, "checkpoints"),
                MaxProducedBlocks = 1,
                Genesis = new GenesisConfig
                {
                    ChainId = 12345,
                    Validators = [.. validators],
                    SystemContracts = ["AgentSession"]
                }
            };
            launcherOptions.Validate();

            using var storage = new NightElfNodeStorage(launcherOptions);
            var repository = new BlockRepository(storage.BlockDatabase, storage.IndexDatabase);
            var chainStateStore = new ChainStateStore(storage.StateDatabase, storage.CheckpointStore);
            var service = new GenesisBlockService(
                launcherOptions,
                consensusOptions,
                consensusEngine,
                repository,
                chainStateStore);

            var created = await service.EnsureGenesisAsync();
            var existing = await service.EnsureGenesisAsync();
            var storedBlock = await repository.GetByHeightAsync(1);
            var bestChain = await chainStateStore.GetBestChainAsync();

            Assert.True(created.Created);
            Assert.False(existing.Created);
            Assert.NotNull(created.Proposal);
            Assert.NotNull(storedBlock);
            Assert.Equal(created.Block, bestChain);
            Assert.Equal(created.Block.Hash, existing.Block.Hash);
        }
        finally
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    public static TheoryData<string> GetConsensusCases()
    {
        return new TheoryData<string>
        {
            nameof(ConsensusEngineKind.Aedpos),
            nameof(ConsensusEngineKind.SingleValidator)
        };
    }

    private static (ConsensusEngineOptions Options, IConsensusEngine Engine, IReadOnlyList<string> Validators) CreateConsensus(string engineName)
    {
        return Enum.Parse<ConsensusEngineKind>(engineName, ignoreCase: true) switch
        {
            ConsensusEngineKind.Aedpos => CreateAedposConsensus(),
            ConsensusEngineKind.SingleValidator => CreateSingleValidatorConsensus(),
            _ => throw new InvalidOperationException($"Unsupported test consensus engine '{engineName}'.")
        };
    }

    private static (ConsensusEngineOptions Options, IConsensusEngine Engine, IReadOnlyList<string> Validators) CreateAedposConsensus()
    {
        var options = new ConsensusEngineOptions
        {
            Engine = nameof(ConsensusEngineKind.Aedpos),
            Aedpos = new AedposConsensusOptions
            {
                Validators = ["validator-a", "validator-b", "validator-c"],
                BlockInterval = TimeSpan.FromMilliseconds(10),
                BlocksPerRound = 3,
                IrreversibleBlockDistance = 8
            }
        };
        options.Validate();

        return (
            options,
            new AedposConsensusEngine(options.Aedpos, new DeterministicVrfProvider(new VrfProviderOptions())),
            options.GetValidatorAddresses());
    }

    private static (ConsensusEngineOptions Options, IConsensusEngine Engine, IReadOnlyList<string> Validators) CreateSingleValidatorConsensus()
    {
        var options = new ConsensusEngineOptions
        {
            Engine = nameof(ConsensusEngineKind.SingleValidator),
            SingleValidator = new SingleValidatorConsensusOptions
            {
                ValidatorAddress = "node-local",
                BlockInterval = TimeSpan.FromMilliseconds(10)
            }
        };
        options.Validate();

        return (
            options,
            new SingleValidatorConsensusEngine(options.SingleValidator),
            options.GetValidatorAddresses());
    }
}
