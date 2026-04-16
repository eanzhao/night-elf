using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Google.Protobuf;

using NightElf.Database;
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
        using var harness = CreateHarness(engineName);

        var created = await harness.Service.EnsureGenesisAsync();
        var existing = await harness.Service.EnsureGenesisAsync();
        var storedBlock = await harness.Repository.GetByHeightAsync(1);
        var bestChain = await harness.ChainStateStore.GetBestChainAsync();

        Assert.True(created.Created);
        Assert.False(existing.Created);
        Assert.NotNull(created.Proposal);
        Assert.NotNull(storedBlock);
        Assert.Equal(created.Block, bestChain);
        Assert.Equal(created.Block.Hash, existing.Block.Hash);
    }

    [Fact]
    public async Task EnsureGenesisAsync_Should_Create_Deployment_Transactions_And_State()
    {
        using var harness = CreateHarness(
            nameof(ConsensusEngineKind.SingleValidator),
            new GenesisConfig
            {
                ChainId = 24680,
                TimestampUtc = new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero),
                SystemContracts = ["AgentSession", "Treasury"]
            });

        var created = await harness.Service.EnsureGenesisAsync();
        var storedBlock = await harness.Repository.GetByHeightAsync(1);
        var deploymentBytes = await harness.ChainStateStore.Database.GetAsync("system-contract:AgentSession:deployment");
        var configBytes = await harness.ChainStateStore.Database.GetAsync("genesis:config");

        Assert.NotNull(storedBlock);
        Assert.NotNull(deploymentBytes);
        Assert.NotNull(configBytes);
        Assert.Equal(2, storedBlock!.Body.TransactionIds.Count);
        Assert.Equal("2", storedBlock.Header.ExtraData["tx_count"].ToStringUtf8());

        var deploymentRecord = VersionedStateRecord.Deserialize(deploymentBytes!);
        var configRecord = VersionedStateRecord.Deserialize(configBytes!);
        using var deploymentDocument = JsonDocument.Parse(deploymentRecord.Value);
        using var configDocument = JsonDocument.Parse(configRecord.Value);

        Assert.Equal("AgentSession", GetProperty(deploymentDocument.RootElement, "contractName").GetString());
        Assert.Equal("DeploySystemContract", GetProperty(deploymentDocument.RootElement, "deploymentMethod").GetString());
        Assert.Equal(created.Block.Hash, GetProperty(deploymentDocument.RootElement, "blockHash").GetString());
        Assert.Equal(1, GetProperty(deploymentDocument.RootElement, "blockHeight").GetInt64());
        Assert.Equal(created.Block.Hash, deploymentRecord.BlockHash);
        Assert.Equal(1, deploymentRecord.BlockHeight);
        Assert.False(deploymentRecord.IsDeleted);
        Assert.Equal(
            storedBlock.Body.TransactionIds[0].ToHex(),
            GetProperty(deploymentDocument.RootElement, "deploymentTransactionId").GetString());
        Assert.Equal(created.Block.Hash, configRecord.BlockHash);
        Assert.Equal(1, configRecord.BlockHeight);
        Assert.Equal(
            ["AgentSession", "Treasury"],
            GetProperty(configDocument.RootElement, "systemContracts")
                .EnumerateArray()
                .Select(static item => item.GetString() ?? string.Empty)
                .ToArray());
    }

    [Fact]
    public async Task EnsureGenesisAsync_Should_Produce_Different_Block_Hashes_For_Different_Genesis_Configs()
    {
        var fixedTimestamp = new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero);
        using var firstHarness = CreateHarness(
            nameof(ConsensusEngineKind.SingleValidator),
            new GenesisConfig
            {
                ChainId = 12345,
                TimestampUtc = fixedTimestamp,
                SystemContracts = ["AgentSession"]
            });
        using var secondHarness = CreateHarness(
            nameof(ConsensusEngineKind.SingleValidator),
            new GenesisConfig
            {
                ChainId = 12345,
                TimestampUtc = fixedTimestamp,
                SystemContracts = ["AgentSession", "Treasury"]
            });

        var firstGenesis = await firstHarness.Service.EnsureGenesisAsync();
        var secondGenesis = await secondHarness.Service.EnsureGenesisAsync();

        Assert.NotEqual(firstGenesis.Block.Hash, secondGenesis.Block.Hash);
    }

    [Fact]
    public void GenesisConfig_FromConfiguration_Should_Load_From_Json_File_And_Allow_Overrides()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "nightelf-genesis-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var configFilePath = Path.Combine(rootPath, "genesis.json");
            File.WriteAllText(
                configFilePath,
                JsonSerializer.Serialize(
                    new GenesisConfig
                    {
                        ChainId = 10001,
                        TimestampUtc = new DateTimeOffset(2026, 4, 15, 12, 0, 0, TimeSpan.Zero),
                        Validators = ["validator-a"],
                        SystemContracts = ["AgentSession", "Treasury"]
                    }));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [nameof(GenesisConfig.ConfigFilePath)] = configFilePath,
                    [nameof(GenesisConfig.ChainId)] = "10002",
                    [$"{nameof(GenesisConfig.SystemContracts)}:0"] = "AgentSession",
                    [$"{nameof(GenesisConfig.Validators)}:0"] = "validator-a"
                })
                .Build();

            var genesisConfig = GenesisConfig.FromConfiguration(configuration);

            Assert.Equal(configFilePath, genesisConfig.ConfigFilePath);
            Assert.Equal(10002, genesisConfig.ChainId);
            Assert.Equal(["validator-a"], genesisConfig.Validators);
            Assert.Equal(["AgentSession"], genesisConfig.SystemContracts);
            Assert.Equal(
                new DateTimeOffset(2026, 4, 15, 12, 0, 0, TimeSpan.Zero),
                genesisConfig.TimestampUtc);
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

    private static GenesisServiceHarness CreateHarness(
        string engineName,
        GenesisConfig? genesisConfig = null)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "nightelf-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        var (consensusOptions, consensusEngine, validators) = CreateConsensus(engineName);
        genesisConfig ??= new GenesisConfig
        {
            ChainId = 12345,
            SystemContracts = ["AgentSession"]
        };

        if (genesisConfig.Validators.Count == 0)
        {
            genesisConfig.Validators = [.. validators];
        }

        var launcherOptions = new LauncherOptions
        {
            DataRootPath = Path.Combine(rootPath, "data"),
            CheckpointRootPath = Path.Combine(rootPath, "checkpoints"),
            MaxProducedBlocks = 1,
            Genesis = genesisConfig
        };
        launcherOptions.Validate();

        var storage = new NightElfNodeStorage(launcherOptions);
        var repository = new BlockRepository(storage.BlockDatabase, storage.IndexDatabase);
        var chainStateStore = new ChainStateStore(storage.StateDatabase, storage.CheckpointStore);
        var service = new GenesisBlockService(
            launcherOptions,
            consensusOptions,
            consensusEngine,
            repository,
            chainStateStore);

        return new GenesisServiceHarness(rootPath, storage, repository, chainStateStore, service);
    }

    private sealed class GenesisServiceHarness : IDisposable
    {
        private readonly string _rootPath;

        public GenesisServiceHarness(
            string rootPath,
            NightElfNodeStorage storage,
            BlockRepository repository,
            ChainStateStore chainStateStore,
            GenesisBlockService service)
        {
            _rootPath = rootPath;
            Storage = storage;
            Repository = repository;
            ChainStateStore = chainStateStore;
            Service = service;
        }

        public NightElfNodeStorage Storage { get; }

        public BlockRepository Repository { get; }

        public ChainStateStore ChainStateStore { get; }

        public GenesisBlockService Service { get; }

        public void Dispose()
        {
            Storage.Dispose();

            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static JsonElement GetProperty(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var camelCaseProperty))
        {
            return camelCaseProperty;
        }

        var pascalCasePropertyName = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (root.TryGetProperty(pascalCasePropertyName, out var pascalCaseProperty))
        {
            return pascalCaseProperty;
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found in JSON payload '{root}'.");
    }
}
