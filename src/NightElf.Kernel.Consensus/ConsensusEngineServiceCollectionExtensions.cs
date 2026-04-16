using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NightElf.Vrf;

namespace NightElf.Kernel.Consensus;

public static class ConsensusEngineServiceCollectionExtensions
{
    public static IServiceCollection AddConsensusEngine(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddConsensusEngine(ConsensusEngineOptions.FromConfiguration(configuration));
    }

    public static IServiceCollection AddConsensusEngine(
        this IServiceCollection services,
        ConsensusEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<IConsensusEngine>(serviceProvider => CreateConsensusEngine(
            serviceProvider.GetRequiredService<ConsensusEngineOptions>(),
            serviceProvider.GetService<IVrfProvider>()));

        return services;
    }

    private static IConsensusEngine CreateConsensusEngine(
        ConsensusEngineOptions options,
        IVrfProvider? vrfProvider)
    {
        return options.ResolveEngineKind() switch
        {
            ConsensusEngineKind.Aedpos => new AedposConsensusEngine(
                options.Aedpos,
                vrfProvider ?? throw new InvalidOperationException(
                    "IVrfProvider must be registered when using the Aedpos consensus engine.")),
            ConsensusEngineKind.SingleValidator => new SingleValidatorConsensusEngine(options.SingleValidator),
            _ => throw new InvalidOperationException($"Unsupported consensus engine '{options.Engine}'.")
        };
    }
}
