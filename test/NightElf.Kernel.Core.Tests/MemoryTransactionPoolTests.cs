using Google.Protobuf;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using NightElf.Database.Tsavorite;
using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core.Tests;

public sealed class MemoryTransactionPoolTests
{
    [Fact]
    public async Task SubmitAndTakeBatch_Should_Preserve_Fifo_Order()
    {
        using var harness = new TransactionPoolHarness();
        await harness.SeedBestChainAsync(5);

        var tx1 = CreateSignedTransaction(harness.GetBlockReference(5), "Transfer", seedMarker: 0x11);
        var tx2 = CreateSignedTransaction(harness.GetBlockReference(5), "Approve", seedMarker: 0x22);

        var accepted1 = await harness.TransactionPool.SubmitAsync(tx1);
        var accepted2 = await harness.TransactionPool.SubmitAsync(tx2);
        var batch = await harness.TransactionPool.TakeBatchAsync(10);
        var snapshot = harness.TransactionPool.GetSnapshot();

        Assert.True(accepted1.IsAccepted);
        Assert.True(accepted2.IsAccepted);
        Assert.Equal(["Transfer", "Approve"], batch.Select(static transaction => transaction.MethodName).ToArray());
        Assert.Equal(0, snapshot.QueuedCount);
        Assert.Equal(2, snapshot.AcceptedCount);
        Assert.Equal(2, snapshot.DequeuedCount);
    }

    [Fact]
    public async Task SubmitAsync_Should_Reject_Invalid_Ed25519_Signature()
    {
        using var harness = new TransactionPoolHarness();
        await harness.SeedBestChainAsync(3);

        var transaction = CreateSignedTransaction(harness.GetBlockReference(3), "Transfer", seedMarker: 0x33);
        transaction.Signature = ByteString.CopyFrom(
            transaction.Signature.ToByteArray().Select(static b => (byte)(b ^ 0xFF)).ToArray());

        var result = await harness.TransactionPool.SubmitAsync(transaction);

        Assert.Equal(TransactionPoolSubmitStatus.InvalidSignature, result.Status);
        Assert.Contains("Ed25519", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitAsync_Should_Reject_Invalid_RefBlock()
    {
        using var harness = new TransactionPoolHarness();
        await harness.SeedBestChainAsync(4);

        var transaction = CreateSignedTransaction(
            harness.GetBlockReference(4),
            "Transfer",
            seedMarker: 0x44,
            refBlockPrefix: ByteString.CopyFrom([0xAA, 0xBB, 0xCC, 0xDD]));

        var result = await harness.TransactionPool.SubmitAsync(transaction);

        Assert.Equal(TransactionPoolSubmitStatus.InvalidRefBlock, result.Status);
        Assert.Contains("prefix", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TakeBatchAsync_Should_Drop_Expired_Transactions()
    {
        using var harness = new TransactionPoolHarness(new TransactionPoolOptions
        {
            Capacity = 8,
            DefaultBatchSize = 4,
            ReferenceBlockValidPeriod = 2
        });
        await harness.SeedBestChainAsync(3);

        var transaction = CreateSignedTransaction(harness.GetBlockReference(2), "Transfer", seedMarker: 0x55);
        var accepted = await harness.TransactionPool.SubmitAsync(transaction);
        await harness.SeedBestChainAsync(4);

        var batch = await harness.TransactionPool.TakeBatchAsync(4);
        var snapshot = harness.TransactionPool.GetSnapshot();

        Assert.True(accepted.IsAccepted);
        Assert.Empty(batch);
        Assert.Equal(1, snapshot.DroppedExpiredCount);
        Assert.Equal(0, snapshot.QueuedCount);
    }

    [Fact]
    public async Task SubmitAsync_Should_Reject_Duplicates_And_Pool_Full()
    {
        using var harness = new TransactionPoolHarness(new TransactionPoolOptions
        {
            Capacity = 1,
            DefaultBatchSize = 1,
            ReferenceBlockValidPeriod = 8
        });
        await harness.SeedBestChainAsync(2);

        var first = CreateSignedTransaction(harness.GetBlockReference(2), "Transfer", seedMarker: 0x66);
        var duplicate = first.Clone();
        var second = CreateSignedTransaction(harness.GetBlockReference(2), "Approve", seedMarker: 0x77);

        var accepted = await harness.TransactionPool.SubmitAsync(first);
        var duplicateResult = await harness.TransactionPool.SubmitAsync(duplicate);
        var fullResult = await harness.TransactionPool.SubmitAsync(second);

        Assert.True(accepted.IsAccepted);
        Assert.Equal(TransactionPoolSubmitStatus.Duplicate, duplicateResult.Status);
        Assert.Equal(TransactionPoolSubmitStatus.PoolFull, fullResult.Status);
    }

    private static Transaction CreateSignedTransaction(
        BlockReference refBlock,
        string methodName,
        byte seedMarker,
        ByteString? refBlockPrefix = null)
    {
        var seed = Enumerable.Repeat(seedMarker, 32).ToArray();
        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();

        var transaction = new Transaction
        {
            From = new Address
            {
                Value = ByteString.CopyFrom(publicKey)
            },
            To = new Address
            {
                Value = ByteString.CopyFrom(Enumerable.Repeat((byte)(seedMarker + 1), 32).ToArray())
            },
            RefBlockNumber = refBlock.Height,
            RefBlockPrefix = refBlockPrefix ?? refBlock.Hash.GetRefBlockPrefix(),
            MethodName = methodName,
            Params = ByteString.CopyFromUtf8($"payload:{seedMarker:X2}")
        };

        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        var signingHash = transaction.GetSigningHash();
        signer.BlockUpdate(signingHash, 0, signingHash.Length);
        transaction.Signature = ByteString.CopyFrom(signer.GenerateSignature());
        return transaction;
    }

    private sealed class TransactionPoolHarness : IDisposable
    {
        private readonly string _rootPath;
        private readonly TsavoriteStoreSet _storeSet;
        private readonly Dictionary<long, BlockReference> _blocksByHeight = [];
        private bool _disposed;

        public TransactionPoolHarness(TransactionPoolOptions? options = null)
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "nightelf-transaction-pool-tests",
                Guid.NewGuid().ToString("N"));

            _storeSet = new TsavoriteStoreSet(new TsavoriteStoreSetOptions
            {
                DataRootPath = Path.Combine(_rootPath, "data"),
                CheckpointRootPath = Path.Combine(_rootPath, "checkpoints")
            });

            BlockDatabase = _storeSet.CreateBlockStore<BlockStoreDbContext>();
            StateDatabase = _storeSet.CreateStateStore<ChainStateDbContext>();
            IndexDatabase = _storeSet.CreateIndexStore<ChainIndexDbContext>();
            CheckpointStore = new TsavoriteStateCheckpointStore<ChainStateDbContext>(StateDatabase);
            BlockRepository = new BlockRepository(BlockDatabase, IndexDatabase);
            ChainStateStore = new ChainStateStore(StateDatabase, CheckpointStore);
            TransactionPool = new MemoryTransactionPool(
                BlockRepository,
                ChainStateStore,
                options ?? new TransactionPoolOptions
                {
                    Capacity = 16,
                    DefaultBatchSize = 8,
                    ReferenceBlockValidPeriod = 512
                });
        }

        public TsavoriteDatabase<BlockStoreDbContext> BlockDatabase { get; }

        public TsavoriteDatabase<ChainStateDbContext> StateDatabase { get; }

        public TsavoriteDatabase<ChainIndexDbContext> IndexDatabase { get; }

        public TsavoriteStateCheckpointStore<ChainStateDbContext> CheckpointStore { get; }

        public BlockRepository BlockRepository { get; }

        public ChainStateStore ChainStateStore { get; }

        public MemoryTransactionPool TransactionPool { get; }

        public BlockReference GetBlockReference(long height)
        {
            return _blocksByHeight[height];
        }

        public async Task SeedBestChainAsync(long bestHeight)
        {
            for (var height = 1L; height <= bestHeight; height++)
            {
                if (_blocksByHeight.ContainsKey(height))
                {
                    continue;
                }

                var blockReference = new BlockReference(height, CreateBlockHash(height));
                var block = new Block
                {
                    Header = new BlockHeader
                    {
                        Height = height
                    },
                    Body = new BlockBody()
                };

                await BlockRepository.StoreAsync(blockReference, block).ConfigureAwait(false);
                _blocksByHeight.Add(height, blockReference);
            }

            await ChainStateStore.SetBestChainAsync(_blocksByHeight[bestHeight]).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CheckpointStore.Dispose();
            _storeSet.Dispose();

            if (Directory.Exists(_rootPath))
            {
                try
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }

            _disposed = true;
        }

        private static string CreateBlockHash(long height)
        {
            var payload = BitConverter.GetBytes(height).Concat(Enumerable.Repeat((byte)height, 24)).ToArray();
            return Convert.ToHexString(payload);
        }
    }
}
