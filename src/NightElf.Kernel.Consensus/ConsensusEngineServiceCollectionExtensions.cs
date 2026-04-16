using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NightElf.Kernel.Consensus;

public static class ConsensusEngineServiceCollectionExtensions
{
    public static IServiceCollection AddConsensusEngine(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConsensusEngineOptions.SectionName);
        var aedposSection = section.GetSection("Aedpos");
        var aedposOptions = new AedposConsensusOptions();

        var configuredValidators = aedposSection
            .GetSection(nameof(AedposConsensusOptions.Validators))
            .GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();

        if (configuredValidators.Count > 0)
        {
            aedposOptions.Validators = configuredValidators;
        }

        if (TimeSpan.TryParse(aedposSection[nameof(AedposConsensusOptions.BlockInterval)], out var blockInterval))
        {
            aedposOptions.BlockInterval = blockInterval;
        }

        if (int.TryParse(aedposSection[nameof(AedposConsensusOptions.BlocksPerRound)], out var blocksPerRound))
        {
            aedposOptions.BlocksPerRound = blocksPerRound;
        }

        if (int.TryParse(aedposSection[nameof(AedposConsensusOptions.IrreversibleBlockDistance)], out var irreversibleBlockDistance))
        {
            aedposOptions.IrreversibleBlockDistance = irreversibleBlockDistance;
        }

        var options = new ConsensusEngineOptions
        {
            Engine = section[nameof(ConsensusEngineOptions.Engine)] ?? nameof(ConsensusEngineKind.Aedpos),
            Aedpos = aedposOptions
        };

        return services.AddConsensusEngine(options);
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
            serviceProvider.GetRequiredService<ConsensusEngineOptions>()));

        return services;
    }

    private static IConsensusEngine CreateConsensusEngine(ConsensusEngineOptions options)
    {
        return options.ResolveEngineKind() switch
        {
            ConsensusEngineKind.Aedpos => new AedposConsensusEngine(options.Aedpos),
            _ => throw new InvalidOperationException($"Unsupported consensus engine '{options.Engine}'.")
        };
    }
}
