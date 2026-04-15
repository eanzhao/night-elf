using System.Buffers;
using System.IO;
using System.Text;

using Tsavorite.core;

using TsavoriteAllocator = Tsavorite.core.SpanByteAllocator<Tsavorite.core.StoreFunctions<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteComparer, Tsavorite.core.SpanByteRecordDisposer>>;
using TsavoriteContext = Tsavorite.core.BasicContext<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteAndMemory, int, Tsavorite.core.SpanByteFunctions<int>, Tsavorite.core.StoreFunctions<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteComparer, Tsavorite.core.SpanByteRecordDisposer>, Tsavorite.core.SpanByteAllocator<Tsavorite.core.StoreFunctions<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteComparer, Tsavorite.core.SpanByteRecordDisposer>>>;
using TsavoriteSession = Tsavorite.core.ClientSession<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteAndMemory, int, Tsavorite.core.SpanByteFunctions<int>, Tsavorite.core.StoreFunctions<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteComparer, Tsavorite.core.SpanByteRecordDisposer>, Tsavorite.core.SpanByteAllocator<Tsavorite.core.StoreFunctions<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteComparer, Tsavorite.core.SpanByteRecordDisposer>>>;
using TsavoriteSessionFunctions = Tsavorite.core.SpanByteFunctions<int>;
using TsavoriteStore = Tsavorite.core.TsavoriteKV<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.StoreFunctions<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteComparer, Tsavorite.core.SpanByteRecordDisposer>, Tsavorite.core.SpanByteAllocator<Tsavorite.core.StoreFunctions<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteComparer, Tsavorite.core.SpanByteRecordDisposer>>>;
using TsavoriteStoreFunctions = Tsavorite.core.StoreFunctions<Tsavorite.core.SpanByte, Tsavorite.core.SpanByte, Tsavorite.core.SpanByteComparer, Tsavorite.core.SpanByteRecordDisposer>;

namespace NightElf.Database.Tsavorite;

public sealed class TsavoriteDatabase<TContext> : IKeyValueDatabase<TContext>, IDisposable
    where TContext : KeyValueDbContext<TContext>
{
    private const int SessionContext = 0;
    private static readonly Encoding KeyEncoding = Encoding.UTF8;

    private readonly TsavoriteStore _store;
    private bool _disposed;

    public TsavoriteDatabase(TsavoriteDatabaseOptions<TContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = CreateSettings(options);
        var storeFunctions = StoreFunctions<SpanByte, SpanByte>.Create();

        _store = new TsavoriteStore(
            settings,
            storeFunctions,
            static (allocatorSettings, functions) => new TsavoriteAllocator(allocatorSettings, functions));
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);

        using var session = CreateSession();
        return await GetAsync(session.BasicContext, key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, byte[]?>> GetAllAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(keys);

        using var session = CreateSession();
        var results = new Dictionary<string, byte[]?>(keys.Count, StringComparer.Ordinal);

        foreach (var key in keys)
        {
            ValidateKey(key);
            results[key] = await GetAsync(session.BasicContext, key, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    public async Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        using var session = CreateSession();
        await SetAsync(session.BasicContext, key, value, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAllAsync(
        IReadOnlyDictionary<string, byte[]> values,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(values);

        using var session = CreateSession();

        foreach (var pair in values)
        {
            ValidateKey(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            await SetAsync(session.BasicContext, pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);

        using var session = CreateSession();
        await DeleteAsync(session.BasicContext, key, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAllAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(keys);

        using var session = CreateSession();

        foreach (var key in keys)
        {
            ValidateKey(key);
            await DeleteAsync(session.BasicContext, key, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);

        using var session = CreateSession();
        return await ExistsAsync(session.BasicContext, key, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _store.Dispose();
        _disposed = true;
    }

    private static KVSettings<SpanByte, SpanByte> CreateSettings(TsavoriteDatabaseOptions<TContext> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CheckpointPath);

        if (options.IndexSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.IndexSize));
        }

        if (options.PageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PageSize));
        }

        if (options.SegmentSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SegmentSize));
        }

        if (options.MemorySize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MemorySize));
        }

        var dataPath = Path.GetFullPath(options.DataPath);
        var checkpointPath = Path.GetFullPath(options.CheckpointPath);

        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(checkpointPath);

        return new KVSettings<SpanByte, SpanByte>
        {
            IndexSize = options.IndexSize,
            LogDevice = Devices.CreateLogDevice(Path.Combine(dataPath, $"{typeof(TContext).Name}.log")),
            PageSize = options.PageSize,
            SegmentSize = options.SegmentSize,
            MemorySize = options.MemorySize,
            CheckpointDir = checkpointPath,
            RemoveOutdatedCheckpoints = options.RemoveOutdatedCheckpoints,
            TryRecoverLatest = options.TryRecoverLatest
        };
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
    }

    private TsavoriteSession CreateSession()
    {
        ThrowIfDisposed();

        return _store.NewSession<SpanByte, SpanByteAndMemory, int, TsavoriteSessionFunctions>(
            new TsavoriteSessionFunctions(MemoryPool<byte>.Shared));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static async Task<byte[]?> GetAsync(
        TsavoriteContext context,
        string key,
        CancellationToken cancellationToken)
    {
        var keyBytes = KeyEncoding.GetBytes(key);
        var (status, output) = Read(context, keyBytes);

        return await CompleteReadAsync(context, status, output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ExistsAsync(
        TsavoriteContext context,
        string key,
        CancellationToken cancellationToken)
    {
        var keyBytes = KeyEncoding.GetBytes(key);
        var (status, output) = Read(context, keyBytes);

        try
        {
            if (status.IsPending)
            {
                using var completedOutputs = await context.CompletePendingWithOutputsAsync(true, cancellationToken).ConfigureAwait(false);

                if (!completedOutputs.Next())
                {
                    return false;
                }

                ref var completed = ref completedOutputs.Current;
                DisposeOutput(ref completed.Output);
                return completed.Status.Found;
            }

            return status.Found;
        }
        finally
        {
            DisposeOutput(ref output);
        }
    }

    private static async Task SetAsync(
        TsavoriteContext context,
        string key,
        byte[] value,
        CancellationToken cancellationToken)
    {
        var keyBytes = KeyEncoding.GetBytes(key);
        var status = Upsert(context, keyBytes, value);

        await CompleteWriteAsync(context, status, "upsert", cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteAsync(
        TsavoriteContext context,
        string key,
        CancellationToken cancellationToken)
    {
        var keyBytes = KeyEncoding.GetBytes(key);
        var status = Delete(context, keyBytes);

        await CompleteWriteAsync(context, status, "delete", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> CompleteReadAsync(
        TsavoriteContext context,
        Status status,
        SpanByteAndMemory output,
        CancellationToken cancellationToken)
    {
        try
        {
            if (status.IsFaulted)
            {
                throw new InvalidOperationException("Tsavorite read failed.");
            }

            if (status.IsCanceled)
            {
                throw new OperationCanceledException("Tsavorite read was canceled.");
            }

            if (status.IsPending)
            {
                using var completedOutputs = await context.CompletePendingWithOutputsAsync(true, cancellationToken).ConfigureAwait(false);

                if (!completedOutputs.Next())
                {
                    return null;
                }

                ref var completed = ref completedOutputs.Current;
                try
                {
                    return completed.Status.Found ? completed.Output.AsReadOnlySpan().ToArray() : null;
                }
                finally
                {
                    DisposeOutput(ref completed.Output);
                }
            }

            return status.Found ? output.AsReadOnlySpan().ToArray() : null;
        }
        finally
        {
            DisposeOutput(ref output);
        }
    }

    private static async Task CompleteWriteAsync(
        TsavoriteContext context,
        Status status,
        string operation,
        CancellationToken cancellationToken)
    {
        if (status.IsFaulted)
        {
            throw new InvalidOperationException($"Tsavorite {operation} failed.");
        }

        if (status.IsCanceled)
        {
            throw new OperationCanceledException($"Tsavorite {operation} was canceled.");
        }

        if (status.IsPending)
        {
            await context.CompletePendingAsync(true, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void DisposeOutput(ref SpanByteAndMemory output)
    {
        output.Memory?.Dispose();
        output = default;
    }

    private static unsafe (Status Status, SpanByteAndMemory Output) Read(TsavoriteContext context, byte[] key)
    {
        fixed (byte* keyPointer = key)
        {
            var spanKey = SpanByte.FromPinnedPointer(keyPointer, key.Length);
            return context.Read(spanKey, SessionContext);
        }
    }

    private static unsafe Status Upsert(TsavoriteContext context, byte[] key, byte[] value)
    {
        fixed (byte* keyPointer = key)
        fixed (byte* valuePointer = value)
        {
            var spanKey = SpanByte.FromPinnedPointer(keyPointer, key.Length);
            var spanValue = SpanByte.FromPinnedPointer(valuePointer, value.Length);
            return context.Upsert(spanKey, spanValue, SessionContext);
        }
    }

    private static unsafe Status Delete(TsavoriteContext context, byte[] key)
    {
        fixed (byte* keyPointer = key)
        {
            var spanKey = SpanByte.FromPinnedPointer(keyPointer, key.Length);
            return context.Delete(spanKey, SessionContext);
        }
    }
}
