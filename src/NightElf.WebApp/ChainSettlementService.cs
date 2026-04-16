using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

using NightElf.Database;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp;

public sealed class ChainSettlementService : ChainSettlement.ChainSettlementBase
{
    private readonly TransactionSubmissionService _transactionSubmissionService;
    private readonly IChainStateStore _chainStateStore;
    private readonly ContractDeploymentService _contractDeploymentService;
    private readonly ChainSettlementEventBroker _eventBroker;

    public ChainSettlementService(
        TransactionSubmissionService transactionSubmissionService,
        IChainStateStore chainStateStore,
        ContractDeploymentService contractDeploymentService,
        ChainSettlementEventBroker eventBroker)
    {
        _transactionSubmissionService = transactionSubmissionService ?? throw new ArgumentNullException(nameof(transactionSubmissionService));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
        _contractDeploymentService = contractDeploymentService ?? throw new ArgumentNullException(nameof(contractDeploymentService));
        _eventBroker = eventBroker ?? throw new ArgumentNullException(nameof(eventBroker));
    }

    public override Task<TransactionResult> SubmitTransaction(
        Transaction request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _transactionSubmissionService.SubmitAsync(request, context.CancellationToken);
    }

    public override async Task<StateResult> QueryState(
        StateQuery request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "State query key must not be empty."));
        }

        var bytes = await _chainStateStore.Database.GetAsync(request.Key, context.CancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return new StateResult
            {
                Key = request.Key,
                Found = false
            };
        }

        if (TryReadVersionedState(bytes, out var versionedState))
        {
            return new StateResult
            {
                Key = request.Key,
                Found = true,
                IsVersioned = true,
                IsDeleted = versionedState!.IsDeleted,
                Value = ByteString.CopyFrom(versionedState.IsDeleted ? [] : versionedState.Value),
                BlockHeight = versionedState.BlockHeight,
                BlockHash = string.IsNullOrWhiteSpace(versionedState.BlockHash)
                    ? new Hash()
                    : versionedState.BlockHash.ToProtoHash()
            };
        }

        return new StateResult
        {
            Key = request.Key,
            Found = true,
            Value = ByteString.CopyFrom(bytes),
            IsVersioned = false,
            IsDeleted = false
        };
    }

    public override async Task<ContractDeployResult> DeployContract(
        ContractDeployRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = await _contractDeploymentService.DeployAsync(request, context.CancellationToken).ConfigureAwait(false);
        return new ContractDeployResult
        {
            ContractAddress = string.IsNullOrWhiteSpace(deployment.ContractAddressHex)
                ? new Address()
                : deployment.ContractAddressHex.ToProtoAddress(),
            TransactionId = string.IsNullOrWhiteSpace(deployment.TransactionId)
                ? new Hash()
                : deployment.TransactionId.ToProtoHash(),
            Status = deployment.Status,
            Error = deployment.Error,
            CodeHash = deployment.CodeHash,
            BlockHeight = deployment.BlockHeight,
            BlockHash = string.IsNullOrWhiteSpace(deployment.BlockHash)
                ? new Hash()
                : deployment.BlockHash.ToProtoHash()
        };
    }

    public override async Task SubscribeEvents(
        EventFilter request,
        IServerStreamWriter<ChainEvent> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);

        using var subscription = _eventBroker.Subscribe();

        try
        {
            foreach (var eventEnvelope in subscription.Snapshot)
            {
                if (MatchesFilter(request, eventEnvelope))
                {
                    await responseStream.WriteAsync(ToProtoEvent(eventEnvelope)).ConfigureAwait(false);
                }
            }

            while (await subscription.Reader.WaitToReadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                while (subscription.Reader.TryRead(out var eventEnvelope))
                {
                    if (MatchesFilter(request, eventEnvelope))
                    {
                        await responseStream.WriteAsync(ToProtoEvent(eventEnvelope)).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ChainEvent ToProtoEvent(ChainSettlementEventEnvelope eventEnvelope)
    {
        ArgumentNullException.ThrowIfNull(eventEnvelope);

        return new ChainEvent
        {
            EventId = eventEnvelope.EventId,
            EventType = eventEnvelope.EventType,
            OccurredAt = Timestamp.FromDateTimeOffset(eventEnvelope.OccurredAtUtc),
            BlockHeight = eventEnvelope.BlockHeight,
            BlockHash = string.IsNullOrWhiteSpace(eventEnvelope.BlockHash)
                ? new Hash()
                : eventEnvelope.BlockHash.ToProtoHash(),
            TransactionId = string.IsNullOrWhiteSpace(eventEnvelope.TransactionId)
                ? new Hash()
                : eventEnvelope.TransactionId.ToProtoHash(),
            ContractAddress = string.IsNullOrWhiteSpace(eventEnvelope.ContractAddress)
                ? new Address()
                : eventEnvelope.ContractAddress.ToProtoAddress(),
            StateKey = eventEnvelope.StateKey ?? string.Empty,
            Payload = ByteString.CopyFrom(eventEnvelope.Payload ?? []),
            Message = eventEnvelope.Message ?? string.Empty
        };
    }

    private static bool MatchesFilter(
        EventFilter filter,
        ChainSettlementEventEnvelope eventEnvelope)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(eventEnvelope);

        if (filter.EventTypes.Count > 0 && !filter.EventTypes.Contains(eventEnvelope.EventType))
        {
            return false;
        }

        if (filter.TransactionId?.Value.IsEmpty == false &&
            !string.Equals(filter.TransactionId.ToHex(), eventEnvelope.TransactionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.ContractAddress?.Value.IsEmpty == false &&
            !string.Equals(filter.ContractAddress.ToHex(), eventEnvelope.ContractAddress, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadVersionedState(
        byte[] bytes,
        out VersionedStateRecord? versionedState)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("blockHeight", out _) &&
                !document.RootElement.TryGetProperty("BlockHeight", out _))
            {
                versionedState = null;
                return false;
            }

            versionedState = VersionedStateRecord.Deserialize(bytes);
            return true;
        }
        catch (JsonException)
        {
            versionedState = null;
            return false;
        }
        catch (InvalidOperationException)
        {
            versionedState = null;
            return false;
        }
    }
}
