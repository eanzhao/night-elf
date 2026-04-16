using NightElf.Database.Tsavorite;
using NightElf.Kernel.Core;

namespace NightElf.Launcher;

public sealed class NightElfNodeStorage : IDisposable
{
    private readonly TsavoriteStoreSet _storeSet;
    private bool _disposed;

    public NightElfNodeStorage(LauncherOptions launcherOptions)
    {
        ArgumentNullException.ThrowIfNull(launcherOptions);

        Options = new TsavoriteStoreSetOptions
        {
            DataRootPath = launcherOptions.DataRootPath,
            CheckpointRootPath = launcherOptions.CheckpointRootPath
        };
        Options.Validate();

        _storeSet = new TsavoriteStoreSet(Options);
        BlockDatabase = _storeSet.CreateBlockStore<BlockStoreDbContext>();
        StateDatabase = _storeSet.CreateStateStore<ChainStateDbContext>();
        IndexDatabase = _storeSet.CreateIndexStore<ChainIndexDbContext>();
        CheckpointStore = new TsavoriteStateCheckpointStore<ChainStateDbContext>(StateDatabase);
    }

    public TsavoriteStoreSetOptions Options { get; }

    public TsavoriteDatabase<BlockStoreDbContext> BlockDatabase { get; }

    public TsavoriteDatabase<ChainStateDbContext> StateDatabase { get; }

    public TsavoriteDatabase<ChainIndexDbContext> IndexDatabase { get; }

    public TsavoriteStateCheckpointStore<ChainStateDbContext> CheckpointStore { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CheckpointStore.Dispose();
        _storeSet.Dispose();
        _disposed = true;
    }
}
