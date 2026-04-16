using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core;

public sealed class MemoryTransactionPool : ITransactionPool
{
    private readonly Lock _lock = new();
    private readonly Queue<QueuedTransaction> _queue = new();
    private readonly Dictionary<string, QueuedTransaction> _queuedById = new(StringComparer.Ordinal);
    private readonly IBlockRepository _blockRepository;
    private readonly IChainStateStore _chainStateStore;
    private readonly TransactionPoolOptions _options;

    private long _acceptedCount;
    private long _rejectedCount;
    private long _dequeuedCount;
    private long _droppedExpiredCount;

    public MemoryTransactionPool(
        IBlockRepository blockRepository,
        IChainStateStore chainStateStore,
        TransactionPoolOptions? options = null)
    {
        _blockRepository = blockRepository ?? throw new ArgumentNullException(nameof(blockRepository));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
        _options = options ?? new TransactionPoolOptions();
        _options.Validate();
    }

    public async Task<TransactionPoolSubmitResult> SubmitAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transaction);

        var transactionId = transaction.GetTransactionId();
        if (!transaction.VerifyCoreFields(out var fieldError))
        {
            Interlocked.Increment(ref _rejectedCount);
            return Rejected(TransactionPoolSubmitStatus.InvalidTransaction, transactionId, fieldError);
        }

        if (!transaction.VerifyEd25519Signature(out var signatureError))
        {
            Interlocked.Increment(ref _rejectedCount);
            return Rejected(TransactionPoolSubmitStatus.InvalidSignature, transactionId, signatureError);
        }

        var referenceValidation = await ValidateReferenceBlockAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (referenceValidation.Status != TransactionPoolSubmitStatus.Accepted)
        {
            Interlocked.Increment(ref _rejectedCount);
            return Rejected(referenceValidation.Status, transactionId, referenceValidation.Error);
        }

        lock (_lock)
        {
            if (_queuedById.ContainsKey(transactionId))
            {
                Interlocked.Increment(ref _rejectedCount);
                return Rejected(
                    TransactionPoolSubmitStatus.Duplicate,
                    transactionId,
                    $"Transaction '{transactionId}' is already queued.");
            }

            if (_queue.Count >= _options.Capacity)
            {
                Interlocked.Increment(ref _rejectedCount);
                return Rejected(
                    TransactionPoolSubmitStatus.PoolFull,
                    transactionId,
                    $"Transaction pool reached capacity {_options.Capacity}.");
            }

            var queuedTransaction = new QueuedTransaction(
                transactionId,
                transaction,
                DateTimeOffset.UtcNow);

            _queue.Enqueue(queuedTransaction);
            _queuedById.Add(transactionId, queuedTransaction);
        }

        Interlocked.Increment(ref _acceptedCount);
        return new TransactionPoolSubmitResult
        {
            Status = TransactionPoolSubmitStatus.Accepted,
            TransactionId = transactionId
        };
    }

    public async Task<IReadOnlyList<Transaction>> TakeBatchAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (maxCount <= 0)
        {
            throw new InvalidOperationException("Transaction batch size must be greater than zero.");
        }

        var transactions = new List<Transaction>(Math.Min(maxCount, _options.DefaultBatchSize));

        while (transactions.Count < maxCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            QueuedTransaction? queuedTransaction;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    break;
                }

                queuedTransaction = _queue.Dequeue();
                _queuedById.Remove(queuedTransaction.TransactionId);
            }

            var referenceValidation = await ValidateReferenceBlockAsync(
                    queuedTransaction.Transaction,
                    cancellationToken)
                .ConfigureAwait(false);

            if (referenceValidation.Status == TransactionPoolSubmitStatus.Accepted)
            {
                transactions.Add(queuedTransaction.Transaction);
                Interlocked.Increment(ref _dequeuedCount);
                continue;
            }

            if (referenceValidation.Status == TransactionPoolSubmitStatus.RefBlockExpired)
            {
                Interlocked.Increment(ref _droppedExpiredCount);
            }
            else
            {
                Interlocked.Increment(ref _rejectedCount);
            }
        }

        return transactions;
    }

    public TransactionPoolSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new TransactionPoolSnapshot
            {
                Capacity = _options.Capacity,
                QueuedCount = _queue.Count,
                AcceptedCount = Interlocked.Read(ref _acceptedCount),
                RejectedCount = Interlocked.Read(ref _rejectedCount),
                DequeuedCount = Interlocked.Read(ref _dequeuedCount),
                DroppedExpiredCount = Interlocked.Read(ref _droppedExpiredCount)
            };
        }
    }

    public Task RemoveAsync(
        IReadOnlyList<Transaction> transactions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transactions);

        if (transactions.Count == 0)
        {
            return Task.CompletedTask;
        }

        var transactionIds = transactions
            .Select(static transaction =>
            {
                ArgumentNullException.ThrowIfNull(transaction);
                return transaction.GetTransactionId();
            })
            .ToHashSet(StringComparer.Ordinal);

        lock (_lock)
        {
            if (transactionIds.Count == 0 || _queue.Count == 0)
            {
                return Task.CompletedTask;
            }

            var remainingQueue = new Queue<QueuedTransaction>(_queue.Count);
            while (_queue.Count > 0)
            {
                var queuedTransaction = _queue.Dequeue();
                if (transactionIds.Contains(queuedTransaction.TransactionId))
                {
                    _queuedById.Remove(queuedTransaction.TransactionId);
                    continue;
                }

                remainingQueue.Enqueue(queuedTransaction);
            }

            while (remainingQueue.Count > 0)
            {
                _queue.Enqueue(remainingQueue.Dequeue());
            }
        }

        return Task.CompletedTask;
    }

    private async Task<ReferenceValidationResult> ValidateReferenceBlockAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        var bestChain = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false);
        if (bestChain is null)
        {
            return new ReferenceValidationResult(
                TransactionPoolSubmitStatus.NoBestChain,
                "Best chain is not available yet.");
        }

        if (transaction.RefBlockNumber > bestChain.Height)
        {
            return new ReferenceValidationResult(
                TransactionPoolSubmitStatus.InvalidRefBlock,
                $"Transaction ref block number {transaction.RefBlockNumber} is ahead of best chain height {bestChain.Height}.");
        }

        if (transaction.RefBlockNumber + _options.ReferenceBlockValidPeriod <= bestChain.Height)
        {
            return new ReferenceValidationResult(
                TransactionPoolSubmitStatus.RefBlockExpired,
                $"Transaction ref block {transaction.RefBlockNumber} expired at best chain height {bestChain.Height}.");
        }

        var refBlock = await _blockRepository.GetBlockReferenceByHeightAsync(
                transaction.RefBlockNumber,
                cancellationToken)
            .ConfigureAwait(false);

        if (refBlock is null)
        {
            return new ReferenceValidationResult(
                TransactionPoolSubmitStatus.InvalidRefBlock,
                $"Transaction ref block height {transaction.RefBlockNumber} does not exist on the current chain.");
        }

        if (!transaction.MatchesRefBlock(refBlock))
        {
            return new ReferenceValidationResult(
                TransactionPoolSubmitStatus.InvalidRefBlock,
                $"Transaction ref block prefix does not match block {refBlock.Height}:{refBlock.Hash}.");
        }

        return new ReferenceValidationResult(TransactionPoolSubmitStatus.Accepted, null);
    }

    private static TransactionPoolSubmitResult Rejected(
        TransactionPoolSubmitStatus status,
        string? transactionId,
        string? error)
    {
        return new TransactionPoolSubmitResult
        {
            Status = status,
            TransactionId = transactionId,
            Error = error
        };
    }

    private sealed record QueuedTransaction(
        string TransactionId,
        Transaction Transaction,
        DateTimeOffset EnqueuedAtUtc);

    private sealed record ReferenceValidationResult(
        TransactionPoolSubmitStatus Status,
        string? Error);
}
