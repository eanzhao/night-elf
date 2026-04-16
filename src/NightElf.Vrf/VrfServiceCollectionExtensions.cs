using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NightElf.Vrf;

public static class VrfServiceCollectionExtensions
{
    public static IServiceCollection AddVrfProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(VrfProviderOptions.SectionName);
        var options = new VrfProviderOptions
        {
            Provider = section[nameof(VrfProviderOptions.Provider)] ?? nameof(VrfProviderKind.Deterministic),
            DomainPrefix = section[nameof(VrfProviderOptions.DomainPrefix)] ?? "nightelf.vrf"
        };

        return services.AddVrfProvider(options);
    }

    public static IServiceCollection AddVrfProvider(
        this IServiceCollection services,
        VrfProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<IVrfProvider>(serviceProvider => CreateProvider(
            serviceProvider.GetRequiredService<VrfProviderOptions>()));

        return services;
    }

    private static IVrfProvider CreateProvider(VrfProviderOptions options)
    {
        return options.ResolveProviderKind() switch
        {
            VrfProviderKind.Deterministic => new DeterministicVrfProvider(options),
            _ => throw new InvalidOperationException($"Unsupported VRF provider '{options.Provider}'.")
        };
    }
}
