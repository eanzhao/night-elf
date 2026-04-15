using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NightElf.Database.Redis;
using NightElf.Database.Tsavorite;

namespace NightElf.Database.Hosting;

public static class KeyValueDatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddKeyValueDatabase<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        TsavoriteStoreKind storeKind = TsavoriteStoreKind.State)
        where TContext : KeyValueDbContext<TContext>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var providerOptions = configuration.GetSection(KeyValueDatabaseProviderOptions.SectionName)
                                  .Get<KeyValueDatabaseProviderOptions>()
                              ?? new KeyValueDatabaseProviderOptions();

        return services.AddKeyValueDatabase<TContext>(providerOptions, storeKind);
    }

    public static IServiceCollection AddKeyValueDatabase<TContext>(
        this IServiceCollection services,
        KeyValueDatabaseProviderOptions providerOptions,
        TsavoriteStoreKind storeKind = TsavoriteStoreKind.State)
        where TContext : KeyValueDbContext<TContext>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(providerOptions);

        providerOptions.Validate();

        services.TryAddSingleton<KeyValueDatabaseProviderOptions>(_ => providerOptions);
        services.TryAddSingleton<IRedisDatabaseClient>(serviceProvider =>
            new StackExchangeRedisDatabaseClient(serviceProvider.GetRequiredService<KeyValueDatabaseProviderOptions>().Redis));
        services.TryAddSingleton<IKeyValueDatabase<TContext>>(serviceProvider =>
            CreateDatabase<TContext>(serviceProvider, storeKind));

        return services;
    }

    private static IKeyValueDatabase<TContext> CreateDatabase<TContext>(
        IServiceProvider serviceProvider,
        TsavoriteStoreKind storeKind)
        where TContext : KeyValueDbContext<TContext>
    {
        var providerOptions = serviceProvider.GetRequiredService<KeyValueDatabaseProviderOptions>();

        return providerOptions.ResolveProvider() switch
        {
            KeyValueDatabaseProviderKind.Redis => new RedisDatabase<TContext>(
                serviceProvider.GetRequiredService<IRedisDatabaseClient>(),
                providerOptions.Redis.CreateDatabaseOptions<TContext>()),
            KeyValueDatabaseProviderKind.Tsavorite => new TsavoriteDatabase<TContext>(
                providerOptions.Tsavorite.CreateDatabaseOptions<TContext>(storeKind)),
            _ => throw new InvalidOperationException($"Unsupported database provider '{providerOptions.Provider}'.")
        };
    }
}
