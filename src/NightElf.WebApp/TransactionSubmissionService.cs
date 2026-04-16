using Google.Protobuf;
using Grpc.Core;

using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp;

public sealed class TransactionSubmissionService
{
    private readonly ITransactionPool _transactionPool;
    private readonly ITransactionResultStore _transactionResultStore;
    private readonly INonCriticalEventBus _eventBus;
    private readonly ITransactionRelayService _transactionRelayService;

    public TransactionSubmissionService(
        ITransactionPool transactionPool,
        ITransactionResultStore transactionResultStore,
        INonCriticalEventBus eventBus,
        ITransactionRelayService transactionRelayService)
    {
        _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
        _transactionResultStore = transactionResultStore ?? throw new ArgumentNullException(nameof(transactionResultStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _transactionRelayService = transactionRelayService ?? throw new ArgumentNullException(nameof(transactionRelayService));
    }

    public async Task<TransactionResult> SubmitAsync(
        Transaction request,
        CancellationToken cancellationToken = default)
    {
        return await SubmitCoreAsync(request, relayToPeers: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransactionResult> SubmitRelayedAsync(
        Transaction request,
        CancellationToken cancellationToken = default)
    {
        return await SubmitCoreAsync(request, relayToPeers: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransactionResult> SubmitCoreAsync(
        Transaction request,
        bool relayToPeers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transactionId = request.GetTransactionId();

        if (!request.VerifyCoreFields(out var fieldError))
        {
            await _transactionResultStore.RecordRejectedAsync(transactionId, fieldError, cancellationToken).ConfigureAwait(false);
            var rejected = TransactionResultProtoConverter.CreateRejected(transactionId, fieldError);
            await PublishTransactionEventAsync(rejected, cancellationToken).ConfigureAwait(false);
            return rejected;
        }

        if (!request.VerifyEd25519Signature(out var signatureError))
        {
            await _transactionResultStore.RecordRejectedAsync(transactionId, signatureError, cancellationToken).ConfigureAwait(false);
            var rejected = TransactionResultProtoConverter.CreateRejected(transactionId, signatureError);
            await PublishTransactionEventAsync(rejected, cancellationToken).ConfigureAwait(false);
            return rejected;
        }

        var submitResult = await _transactionPool.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        if (submitResult.IsAccepted)
        {
            await _transactionResultStore.RecordPendingAsync(request, cancellationToken).ConfigureAwait(false);
            if (relayToPeers)
            {
                await _transactionRelayService.RelayAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var storedResult = await _transactionResultStore.GetAsync(transactionId, cancellationToken).ConfigureAwait(false);
            return storedResult is null
                ? TransactionResultProtoConverter.CreatePending(transactionId)
                : TransactionResultProtoConverter.ToProto(storedResult);
        }

        if (submitResult.Status == TransactionPoolSubmitStatus.Duplicate)
        {
            var existing = await _transactionResultStore.GetAsync(transactionId, cancellationToken).ConfigureAwait(false);
            return existing is null
                ? TransactionResultProtoConverter.CreatePending(transactionId, submitResult.Error)
                : TransactionResultProtoConverter.ToProto(existing);
        }

        await _transactionResultStore.RecordRejectedAsync(transactionId, submitResult.Error, cancellationToken).ConfigureAwait(false);
        var poolRejected = TransactionResultProtoConverter.CreateRejected(transactionId, submitResult.Error);
        await PublishTransactionEventAsync(poolRejected, cancellationToken).ConfigureAwait(false);
        return poolRejected;
    }

    public async Task<TransactionResult> GetResultAsync(
        Hash request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Value.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Transaction hash must not be empty."));
        }

        var result = await _transactionResultStore.GetAsync(request, cancellationToken).ConfigureAwait(false);
        return result is null
            ? new TransactionResult
            {
                TransactionId = request,
                Status = TransactionExecutionStatus.NotFound
            }
            : TransactionResultProtoConverter.ToProto(result);
    }

    private Task PublishTransactionEventAsync(
        TransactionResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        return _eventBus.PublishAsync(
            new ChainSettlementEventEnvelope(
                EventId: $"tx:{result.TransactionId.ToHex()}:{result.Status}",
                EventType: ChainEventType.TransactionResult,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                BlockHeight: result.BlockHeight,
                BlockHash: result.BlockHash?.Value.IsEmpty == false ? result.BlockHash.ToHex() : null,
                TransactionId: result.TransactionId.ToHex(),
                Payload: result.ToByteArray(),
                Message: result.Status.ToString()),
            cancellationToken);
    }
}
