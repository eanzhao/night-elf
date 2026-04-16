using NightElf.Database;
using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core;

public interface IBlockRepository
{
    IKeyValueDatabase<BlockStoreDbContext> BlockDatabase { get; }

    IKeyValueDatabase<ChainIndexDbContext> IndexDatabase { get; }

    Task StoreAsync(
        BlockReference blockReference,
        Block block,
        CancellationToken cancellationToken = default);

    Task<Block?> GetByHashAsync(
        string blockHash,
        CancellationToken cancellationToken = default);

    Task<Block?> GetByHeightAsync(
        long blockHeight,
        CancellationToken cancellationToken = default);

    Task<BlockReference?> GetBlockReferenceByHeightAsync(
        long blockHeight,
        CancellationToken cancellationToken = default);
}
