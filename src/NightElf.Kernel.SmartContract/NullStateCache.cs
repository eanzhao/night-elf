namespace NightElf.Kernel.SmartContract;

public sealed class NullStateCache : IStateCache
{
    public static NullStateCache Instance { get; } = new();

    public byte[]? this[string key]
    {
        get
        {
            ValidateKey(key);
            return null;
        }
        set
        {
            ValidateKey(key);
        }
    }

    public bool TryGet(string key, out byte[]? value)
    {
        ValidateKey(key);
        value = null;
        return false;
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
    }
}
