using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp;

public sealed class NightElfNodeService : NightElfNode.NightElfNodeBase
{
    private readonly TransactionSubmissionService _transactionSubmissionService;
    private readonly IBlockRepository _blockRepository;
    private readonly IChainStateStore _chainStateStore;

    public NightElfNodeService(
        TransactionSubmissionService transactionSubmissionService,
        IBlockRepository blockRepository,
        IChainStateStore chainStateStore)
    {
        _transactionSubmissionService = transactionSubmissionService ?? throw new ArgumentNullException(nameof(transactionSubmissionService));
        _blockRepository = blockRepository ?? throw new ArgumentNullException(nameof(blockRepository));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
    }

    public override async Task<TransactionResult> SubmitTransaction(
        Transaction request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _transactionSubmissionService.SubmitAsync(request, context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<TransactionResult> GetTransactionResult(
        Hash request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _transactionSubmissionService.GetResultAsync(request, context.CancellationToken).ConfigureAwait(false);
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

}
