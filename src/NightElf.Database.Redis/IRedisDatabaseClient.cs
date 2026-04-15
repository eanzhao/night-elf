namespace NightElf.Database.Redis;

public interface IRedisDatabaseClient : IDisposable
{
    Task<byte[]?> GetAsync(string key);

    Task<IReadOnlyDictionary<string, byte[]?>> GetAllAsync(IReadOnlyCollection<string> keys);

    Task SetAsync(string key, byte[] value);

    Task SetAllAsync(IReadOnlyDictionary<string, byte[]> values);

    Task DeleteAsync(string key);

    Task DeleteAllAsync(IReadOnlyCollection<string> keys);

    Task<bool> ExistsAsync(string key);
}
