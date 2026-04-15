using NightElf.Database;

namespace NightElf.Kernel.Core;

public interface IChainStateStore
{
    IKeyValueDatabase<ChainStateDbContext> Database { get; }

    Task<BlockReference?> GetBestChainAsync(CancellationToken cancellationToken = default);
}
