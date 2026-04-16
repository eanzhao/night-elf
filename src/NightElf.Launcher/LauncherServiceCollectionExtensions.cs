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

        services.AddVrfProvider(configuration);
        services.AddConsensusEngine(configuration);
        services.AddNetworkTransport(configuration);

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
        services.AddSingleton<IBlockProcessingPipeline>(serviceProvider =>
            new ChannelBlockProcessingPipeline(
                serviceProvider.GetRequiredService<IChainStateStore>(),
                serviceProvider.GetRequiredService<IBlockSyncNotifier>(),
                serviceProvider.GetRequiredService<INonCriticalEventBus>()));
        services.AddSingleton<IGenesisBlockService, GenesisBlockService>();
        services.AddHostedService<NodeRuntimeHostedService>();

        return services;
    }
}
