namespace NightElf.Database;

public interface IKeyValueDatabase<TContext>
    where TContext : KeyValueDbContext<TContext>
{
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, byte[]?>> GetAllAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default);

    Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default);

    Task SetAllAsync(
        IReadOnlyDictionary<string, byte[]> values,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    Task DeleteAllAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}
