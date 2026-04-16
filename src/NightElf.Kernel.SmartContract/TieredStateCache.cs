namespace NightElf.Kernel.SmartContract;

public sealed class TieredStateCache : IStateCache
{
    private readonly Dictionary<string, byte[]?> _currentValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]?> _originalValues = new(StringComparer.Ordinal);
    private readonly IStateCache _parent;

    public TieredStateCache()
        : this(NullStateCache.Instance)
    {
    }

    public TieredStateCache(IStateCache parent)
    {
        _parent = parent ?? NullStateCache.Instance;
    }

    public byte[]? this[string key]
    {
        get => TryGet(key, out var value) ? value : null;
        set
        {
            ValidateKey(key);
            _currentValues[key] = value;
            _parent[key] = value;
        }
    }

    public bool TryGet(string key, out byte[]? value)
    {
        ValidateKey(key);

        if (_currentValues.TryGetValue(key, out value))
        {
            return true;
        }

        return TryGetOriginalValue(key, out value);
    }

    public void Update(IReadOnlyDictionary<string, byte[]?> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        foreach (var change in changes)
        {
            ValidateKey(change.Key);
            _currentValues[change.Key] = change.Value;
        }
    }

    private bool TryGetOriginalValue(string key, out byte[]? value)
    {
        if (_originalValues.TryGetValue(key, out value))
        {
            return true;
        }

        if (_parent.TryGet(key, out value))
        {
            _originalValues[key] = value;
            return true;
        }

        return false;
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
    }
}
