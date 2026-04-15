using NightElf.Database.Redis;
using NightElf.Database.Tsavorite;

namespace NightElf.Database.Hosting;

public sealed class KeyValueDatabaseProviderOptions
{
    public const string SectionName = "NightElf:Database";

    public string? Provider { get; set; } = KeyValueDatabaseProviderKind.Tsavorite.ToString();

    public RedisConnectionOptions Redis { get; set; } = new();

    public TsavoriteStoreSetOptions Tsavorite { get; set; } = new();

    public KeyValueDatabaseProviderKind ResolveProvider()
    {
        var configuredProvider = string.IsNullOrWhiteSpace(Provider)
            ? KeyValueDatabaseProviderKind.Tsavorite.ToString()
            : Provider;

        if (Enum.TryParse<KeyValueDatabaseProviderKind>(configuredProvider, ignoreCase: true, out var providerKind))
        {
            return providerKind;
        }

        throw new InvalidOperationException(
            $"Unsupported database provider '{configuredProvider}'. Supported providers: Redis, Tsavorite.");
    }

    public void Validate()
    {
        switch (ResolveProvider())
        {
            case KeyValueDatabaseProviderKind.Redis:
                Redis.Validate();
                break;
            case KeyValueDatabaseProviderKind.Tsavorite:
                Tsavorite.Validate();
                break;
            default:
                throw new InvalidOperationException($"Unsupported database provider '{Provider}'.");
        }
    }
}
