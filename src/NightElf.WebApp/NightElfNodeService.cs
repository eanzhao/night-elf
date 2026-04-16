using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp;

public sealed class NightElfNodeService : NightElfNode.NightElfNodeBase
{
    private readonly ITransactionPool _transactionPool;
    private readonly ITransactionResultStore _transactionResultStore;
    private readonly IBlockRepository _blockRepository;
    private readonly IChainStateStore _chainStateStore;

    public NightElfNodeService(
        ITransactionPool transactionPool,
        ITransactionResultStore transactionResultStore,
        IBlockRepository blockRepository,
        IChainStateStore chainStateStore)
    {
        _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
        _transactionResultStore = transactionResultStore ?? throw new ArgumentNullException(nameof(transactionResultStore));
        _blockRepository = blockRepository ?? throw new ArgumentNullException(nameof(blockRepository));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
    }

    public override async Task<TransactionResult> SubmitTransaction(
        Transaction request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transactionId = request.GetTransactionId();

        if (!request.VerifyCoreFields(out var fieldError))
        {
            return CreateRejectedResult(transactionId, fieldError);
        }

        if (!request.VerifyEd25519Signature(out var signatureError))
        {
            return CreateRejectedResult(transactionId, signatureError);
        }

        var submitResult = await _transactionPool.SubmitAsync(request, context.CancellationToken).ConfigureAwait(false);
        if (submitResult.IsAccepted)
        {
            await _transactionResultStore.RecordPendingAsync(request, context.CancellationToken).ConfigureAwait(false);
            var storedResult = await _transactionResultStore.GetAsync(transactionId, context.CancellationToken).ConfigureAwait(false);
            return storedResult is null
                ? CreatePendingResult(transactionId)
                : ToProtoResult(storedResult);
        }

        if (submitResult.Status == TransactionPoolSubmitStatus.Duplicate)
        {
            var existing = await _transactionResultStore.GetAsync(transactionId, context.CancellationToken).ConfigureAwait(false);
            return existing is null
                ? CreatePendingResult(transactionId, submitResult.Error)
                : ToProtoResult(existing);
        }

        return CreateRejectedResult(transactionId, submitResult.Error);
    }

    public override async Task<TransactionResult> GetTransactionResult(
        Hash request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Value.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Transaction hash must not be empty."));
        }

        var result = await _transactionResultStore.GetAsync(request, context.CancellationToken).ConfigureAwait(false);
        return result is null
            ? new TransactionResult
            {
                TransactionId = request,
                Status = TransactionExecutionStatus.NotFound
            }
            : ToProtoResult(result);
    }

    public override async Task<Block> GetBlockByHeight(
        Int64Value request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Value <= 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Block height must be greater than zero."));
        }

        var block = await _blockRepository.GetByHeightAsync(request.Value, context.CancellationToken).ConfigureAwait(false);
        if (block is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Block height {request.Value} was not found."));
        }

        return block;
    }

    public override async Task<ChainStatus> GetChainStatus(
        Empty request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bestChain = await _chainStateStore.GetBestChainAsync(context.CancellationToken).ConfigureAwait(false);
        return bestChain is null
            ? new ChainStatus
            {
                BestChainHeight = 0,
                BestChainHash = new Hash()
            }
            : new ChainStatus
            {
                BestChainHeight = bestChain.Height,
                BestChainHash = bestChain.Hash.ToProtoHash()
            };
    }

    private static TransactionResult ToProtoResult(TransactionResultRecord record)
    {
        return new TransactionResult
        {
            TransactionId = record.TransactionId.ToProtoHash(),
            Status = record.Status switch
            {
                TransactionResultStatus.Pending => TransactionExecutionStatus.Pending,
                TransactionResultStatus.Mined => TransactionExecutionStatus.Mined,
                TransactionResultStatus.Failed => TransactionExecutionStatus.Failed,
                TransactionResultStatus.Rejected => TransactionExecutionStatus.Rejected,
                _ => TransactionExecutionStatus.Unspecified
            },
            Error = record.Error ?? string.Empty,
            BlockHeight = record.BlockHeight,
            BlockHash = string.IsNullOrWhiteSpace(record.BlockHash)
                ? new Hash()
                : record.BlockHash.ToProtoHash()
        };
    }

    private static TransactionResult CreatePendingResult(string transactionId, string? error = null)
    {
        return new TransactionResult
        {
            TransactionId = transactionId.ToProtoHash(),
            Status = TransactionExecutionStatus.Pending,
            Error = error ?? string.Empty
        };
    }

    private static TransactionResult CreateRejectedResult(string transactionId, string? error)
    {
        return new TransactionResult
        {
            TransactionId = string.IsNullOrWhiteSpace(transactionId)
                ? new Hash()
                : transactionId.ToProtoHash(),
            Status = TransactionExecutionStatus.Rejected,
            Error = error ?? string.Empty
        };
    }
}
