using System.Text;

using Google.Protobuf;

using NightElf.Database;
using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core;

public sealed class BlockRepository : IBlockRepository
{
    private const string BlockByHashPrefix = "block:hash:";
    private const string BlockHeightIndexPrefix = "block:height:";

    public BlockRepository(
        IKeyValueDatabase<BlockStoreDbContext> blockDatabase,
        IKeyValueDatabase<ChainIndexDbContext> indexDatabase)
    {
        BlockDatabase = blockDatabase ?? throw new ArgumentNullException(nameof(blockDatabase));
        IndexDatabase = indexDatabase ?? throw new ArgumentNullException(nameof(indexDatabase));
    }

    public IKeyValueDatabase<BlockStoreDbContext> BlockDatabase { get; }

    public IKeyValueDatabase<ChainIndexDbContext> IndexDatabase { get; }

    public async Task StoreAsync(
        BlockReference blockReference,
        Block block,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blockReference);
        ArgumentNullException.ThrowIfNull(block);

        var blockHashKey = CreateBlockHashKey(blockReference.Hash);
        var blockHeightKey = CreateBlockHeightKey(blockReference.Height);

        await BlockDatabase.SetAsync(blockHashKey, block.ToByteArray(), cancellationToken).ConfigureAwait(false);
        await IndexDatabase.SetAsync(
                blockHeightKey,
                Encoding.UTF8.GetBytes(blockReference.Hash),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Block?> GetByHashAsync(
        string blockHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockHash);

        var payload = await BlockDatabase.GetAsync(CreateBlockHashKey(blockHash), cancellationToken).ConfigureAwait(false);
        return payload is null ? null : Block.Parser.ParseFrom(payload);
    }

    public async Task<Block?> GetByHeightAsync(
        long blockHeight,
        CancellationToken cancellationToken = default)
    {
        var blockReference = await GetBlockReferenceByHeightAsync(blockHeight, cancellationToken).ConfigureAwait(false);
        return blockReference is null
            ? null
            : await GetByHashAsync(blockReference.Hash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BlockReference?> GetBlockReferenceByHeightAsync(
        long blockHeight,
        CancellationToken cancellationToken = default)
    {
        if (blockHeight <= 0)
        {
            throw new InvalidOperationException("Block height must be greater than zero.");
        }

        var encodedHash = await IndexDatabase.GetAsync(CreateBlockHeightKey(blockHeight), cancellationToken).ConfigureAwait(false);
        if (encodedHash is null)
        {
            return null;
        }

        return new BlockReference(blockHeight, Encoding.UTF8.GetString(encodedHash));
    }

    private static string CreateBlockHashKey(string blockHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockHash);
        return $"{BlockByHashPrefix}{blockHash}";
    }

    private static string CreateBlockHeightKey(long blockHeight)
    {
        if (blockHeight <= 0)
        {
            throw new InvalidOperationException("Block height must be greater than zero.");
        }

        return $"{BlockHeightIndexPrefix}{blockHeight:D20}";
    }
}
