using NightElf.Database.Tsavorite;
using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;
using NightElf.Vrf;

namespace NightElf.Launcher.Tests;

public sealed class GenesisBlockServiceTests
{
    [Fact]
    public async Task EnsureGenesisAsync_Should_Create_Genesis_Only_Once()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "nightelf-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var launcherOptions = new LauncherOptions
            {
                DataRootPath = Path.Combine(rootPath, "data"),
                CheckpointRootPath = Path.Combine(rootPath, "checkpoints"),
                MaxProducedBlocks = 1,
                Genesis = new GenesisConfig
                {
                    ChainId = 12345,
                    Validators = ["validator-a", "validator-b", "validator-c"],
                    SystemContracts = ["AgentSession"]
                }
            };
            launcherOptions.Validate();

            using var storage = new NightElfNodeStorage(launcherOptions);
            var repository = new BlockRepository(storage.BlockDatabase, storage.IndexDatabase);
            var chainStateStore = new ChainStateStore(storage.StateDatabase, storage.CheckpointStore);
            var consensusOptions = new ConsensusEngineOptions
            {
                Aedpos = new AedposConsensusOptions
                {
                    Validators = ["validator-a", "validator-b", "validator-c"],
                    BlockInterval = TimeSpan.FromMilliseconds(10),
                    BlocksPerRound = 3,
                    IrreversibleBlockDistance = 8
                }
            };
            consensusOptions.Validate();

            var consensusEngine = new AedposConsensusEngine(
                consensusOptions.Aedpos,
                new DeterministicVrfProvider(new VrfProviderOptions()));
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
}
