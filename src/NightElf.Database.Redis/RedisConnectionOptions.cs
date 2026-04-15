namespace NightElf.Database.Redis;

public sealed class RedisConnectionOptions
{
    public string? ConnectionString { get; set; }

    public int Database { get; set; } = -1;

    public string? KeyPrefix { get; set; } = "nightelf:";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                "NightElf:Database:Redis:ConnectionString must be configured when the Redis provider is selected.");
        }

        if (Database < -1)
        {
            throw new InvalidOperationException(
                "NightElf:Database:Redis:Database must be -1 or a non-negative integer.");
        }
    }

    public RedisDatabaseOptions<TContext> CreateDatabaseOptions<TContext>()
        where TContext : KeyValueDbContext<TContext>
    {
        Validate();

        return new RedisDatabaseOptions<TContext>
        {
            ConnectionString = ConnectionString!,
            Database = Database,
            KeyPrefix = KeyPrefix ?? string.Empty
        };
    }
}
