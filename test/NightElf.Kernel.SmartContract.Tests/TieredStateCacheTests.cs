namespace NightElf.Kernel.SmartContract.Tests;

public sealed class TieredStateCacheTests
{
    [Fact]
    public void TryGet_Should_Return_False_When_Key_Missing()
    {
        var cache = new TieredStateCache();

        var found = cache.TryGet("missing", out var value);

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void Indexer_Setter_Should_Write_To_Current_And_Parent()
    {
        var parent = new RecordingStateCache();
        var cache = new TieredStateCache(parent);

        cache["key-1"] = new byte[] { 1, 2, 3 };

        Assert.True(cache.TryGet("key-1", out var value));
        Assert.Equal(new byte[] { 1, 2, 3 }, value);
        Assert.Equal(new byte[] { 1, 2, 3 }, parent["key-1"]);
    }

    [Fact]
    public void Indexer_Setter_Should_Accept_Null_Value()
    {
        var parent = new RecordingStateCache();
        var cache = new TieredStateCache(parent);

        cache["key-null"] = null;

        Assert.True(cache.TryGet("key-null", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Update_Should_Overwrite_Prior_Indexer_Write_In_Current()
    {
        var cache = new TieredStateCache();
        cache["key"] = new byte[] { 1 };

        cache.Update(new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            ["key"] = new byte[] { 2 }
        });

        Assert.True(cache.TryGet("key", out var value));
        Assert.Equal(new byte[] { 2 }, value);
    }

    [Fact]
    public void Update_After_Indexer_Set_Should_Be_Visible_For_Same_Key()
    {
        // Regression guard: the indexer setter writes to _currentValues
        // (not _originalValues). A subsequent Update() must overwrite
        // the same _currentValues entry, not a separate dictionary.
        var cache = new TieredStateCache();
        cache["key"] = new byte[] { 1 };

        cache.Update(new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            ["key"] = new byte[] { 9 }
        });

        Assert.True(cache.TryGet("key", out var value));
        Assert.Equal(new byte[] { 9 }, value);
    }

    [Fact]
    public void TryGet_Should_Prefer_Current_Over_Original()
    {
        var parent = new RecordingStateCache();
        parent["key"] = new byte[] { 100 };
        var cache = new TieredStateCache(parent);

        // First read populates _originalValues
        Assert.True(cache.TryGet("key", out var original));
        Assert.Equal(new byte[] { 100 }, original);

        // Update writes to _currentValues
        cache.Update(new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            ["key"] = new byte[] { 200 }
        });

        Assert.True(cache.TryGet("key", out var latest));
        Assert.Equal(new byte[] { 200 }, latest);
    }

    [Fact]
    public void TryGet_Should_Fall_Through_To_Parent_And_Cache_Original()
    {
        var parent = new RecordingStateCache();
        parent["parent-key"] = new byte[] { 42 };
        var cache = new TieredStateCache(parent);

        Assert.True(cache.TryGet("parent-key", out var first));
        Assert.Equal(new byte[] { 42 }, first);
        Assert.Equal(1, parent.GetCallCount);

        // Second read should hit the cached _originalValues, not the parent.
        Assert.True(cache.TryGet("parent-key", out var second));
        Assert.Equal(new byte[] { 42 }, second);
        Assert.Equal(1, parent.GetCallCount);
    }

    [Fact]
    public void TryGet_Should_Reject_Empty_Key()
    {
        var cache = new TieredStateCache();

        Assert.Throws<ArgumentException>(() => cache.TryGet(string.Empty, out _));
    }

    [Fact]
    public void TryGet_Should_Reject_Null_Key()
    {
        var cache = new TieredStateCache();

        Assert.Throws<ArgumentNullException>(() => cache.TryGet(null!, out _));
    }

    [Fact]
    public void Indexer_Setter_Should_Reject_Empty_Key()
    {
        var cache = new TieredStateCache();

        Assert.Throws<ArgumentException>(() => cache["" ] = new byte[] { 1 });
    }

    [Fact]
    public void Update_Should_Reject_Null_Changes_Dictionary()
    {
        var cache = new TieredStateCache();

        Assert.Throws<ArgumentNullException>(() => cache.Update(null!));
    }

    [Fact]
    public void Update_Should_Reject_Empty_Key()
    {
        var cache = new TieredStateCache();
        var changes = new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            [string.Empty] = new byte[] { 1 }
        };

        Assert.Throws<ArgumentException>(() => cache.Update(changes));
    }

    [Fact]
    public void Null_Parent_Should_Fall_Back_To_NullStateCache()
    {
        var cache = new TieredStateCache(parent: null!);

        Assert.False(cache.TryGet("missing", out _));
    }

    [Fact]
    public void Multiple_Tiers_Should_Cascade_Reads_Through_All_Levels()
    {
        var grandparent = new RecordingStateCache();
        grandparent["deep"] = new byte[] { 7 };
        var middle = new TieredStateCache(grandparent);
        var top = new TieredStateCache(middle);

        Assert.True(top.TryGet("deep", out var value));
        Assert.Equal(new byte[] { 7 }, value);
    }

    [Fact]
    public void Multiple_Tiers_Should_Propagate_Writes_Down()
    {
        var grandparent = new RecordingStateCache();
        var middle = new TieredStateCache(grandparent);
        var top = new TieredStateCache(middle);

        top["key"] = new byte[] { 1 };

        Assert.True(middle.TryGet("key", out var middleValue));
        Assert.Equal(new byte[] { 1 }, middleValue);
        Assert.Equal(new byte[] { 1 }, grandparent["key"]);
    }

    [Fact]
    public void Update_Should_Not_Propagate_To_Parent_Cache()
    {
        // Update() is a bulk write that represents cache-local overrides
        // and must not escape to the parent (unlike the indexer setter).
        var parent = new RecordingStateCache();
        var cache = new TieredStateCache(parent);

        cache.Update(new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            ["key"] = new byte[] { 1 }
        });

        Assert.False(parent.TryGet("key", out _));
    }

    private sealed class RecordingStateCache : IStateCache
    {
        private readonly Dictionary<string, byte[]?> _values = new(StringComparer.Ordinal);

        public int GetCallCount { get; private set; }

        public byte[]? this[string key]
        {
            get
            {
                GetCallCount++;
                return _values.TryGetValue(key, out var value) ? value : null;
            }
            set => _values[key] = value;
        }

        public bool TryGet(string key, out byte[]? value)
        {
            GetCallCount++;
            return _values.TryGetValue(key, out value);
        }
    }
}
