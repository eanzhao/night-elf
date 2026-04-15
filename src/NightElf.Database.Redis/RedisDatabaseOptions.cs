namespace NightElf.Database.Redis;

public sealed class RedisDatabaseOptions<TContext>
    where TContext : KeyValueDbContext<TContext>
{
    public required string ConnectionString { get; init; }

    public int Database { get; init; } = -1;

    public string KeyPrefix { get; init; } = string.Empty;

    internal string CreateScopedKey(string key)
    {
        return $"{KeyPrefix}{typeof(TContext).Name}:{key}";
    }
}
