using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NightElf.Database;
using NightElf.Database.Tsavorite;
using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;
using NightElf.OS.Network;
using NightElf.Vrf;

namespace NightElf.Launcher;

public static class LauncherServiceCollectionExtensions
{
    public static IServiceCollection AddNightElfLauncher(
        this IServiceCollection services,
        IConfiguration configuration,
        LauncherOptions launcherOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(launcherOptions);

        launcherOptions.Validate();

        services.AddSingleton(launcherOptions);
        services.AddSingleton(new LauncherModuleCatalog());
        services.AddSingleton<INonCriticalEventBus, InMemoryNonCriticalEventBus>();
        services.AddSingleton<IBlockSyncNotifier, LoggingBlockSyncNotifier>();

        var consensusOptions = ConsensusEngineOptions.FromConfiguration(configuration);
        var transactionPoolOptions = CreateTransactionPoolOptions(configuration);
        transactionPoolOptions.Validate();

        if (consensusOptions.ResolveEngineKind() == ConsensusEngineKind.Aedpos)
        {
            services.AddVrfProvider(configuration);
        }

        services.AddConsensusEngine(consensusOptions);
        services.AddNetworkTransport(configuration);
        services.AddSingleton(transactionPoolOptions);

        services.AddSingleton<NightElfNodeStorage>();
        services.AddSingleton<IKeyValueDatabase<BlockStoreDbContext>>(serviceProvider =>
            serviceProvider.GetRequiredService<NightElfNodeStorage>().BlockDatabase);
        services.AddSingleton<IKeyValueDatabase<ChainStateDbContext>>(serviceProvider =>
            serviceProvider.GetRequiredService<NightElfNodeStorage>().StateDatabase);
        services.AddSingleton<IKeyValueDatabase<ChainIndexDbContext>>(serviceProvider =>
            serviceProvider.GetRequiredService<NightElfNodeStorage>().IndexDatabase);
        services.AddSingleton<IStateCheckpointStore<ChainStateDbContext>>(serviceProvider =>
            serviceProvider.GetRequiredService<NightElfNodeStorage>().CheckpointStore);

        services.AddSingleton<IBlockRepository, BlockRepository>();
        services.AddSingleton<IChainStateStore, ChainStateStore>();
        services.AddSingleton<ITransactionPool, MemoryTransactionPool>();
        services.AddSingleton<IBlockProcessingPipeline>(serviceProvider =>
            new ChannelBlockProcessingPipeline(
                serviceProvider.GetRequiredService<IChainStateStore>(),
                serviceProvider.GetRequiredService<IBlockSyncNotifier>(),
                serviceProvider.GetRequiredService<INonCriticalEventBus>()));
        services.AddSingleton<IGenesisBlockService, GenesisBlockService>();
        services.AddHostedService<NodeRuntimeHostedService>();

        return services;
    }

    private static TransactionPoolOptions CreateTransactionPoolOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(TransactionPoolOptions.SectionName);
        return new TransactionPoolOptions
        {
            Capacity = ParseInt32(section[nameof(TransactionPoolOptions.Capacity)], 4096),
            DefaultBatchSize = ParseInt32(section[nameof(TransactionPoolOptions.DefaultBatchSize)], 128),
            ReferenceBlockValidPeriod = ParseInt64(
                section[nameof(TransactionPoolOptions.ReferenceBlockValidPeriod)],
                64 * 8)
        };
    }

    private static int ParseInt32(string? value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid integer value '{value}' in NightElf transaction pool configuration.");
    }

    private static long ParseInt64(string? value, long defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (long.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid integer value '{value}' in NightElf transaction pool configuration.");
    }
}
