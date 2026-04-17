using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Linq;
using System.Text;

using Google.Protobuf;

using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Sdk.CSharp;

using TreatyProto = NightElf.Contracts.System.Treaty.Protobuf;

namespace NightElf.Contracts.System.Treaty;

public sealed partial class TreatyContract : CSharpSmartContract
{
    private const string AdminSalt = "treaty-admin";

    [ContractMethod]
    public Hash CreateTreaty(TreatyProto.CreateTreatyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Spec);

        ValidateTreatySpec(input.Spec);

        var creatorAddress = ParseSenderAddress(nameof(CreateTreaty));
        var creatorAddressHex = creatorAddress.ToHex();
        if (!input.Spec.Participants.Any(participant =>
                StringComparer.OrdinalIgnoreCase.Equals(participant.AgentAddress?.ToHex(), creatorAddressHex)))
        {
            throw new InvalidOperationException("Treaty creator must be listed as a participant.");
        }

        var treatyId = CreateTreatyId();
        var treatyKey = CreateTreatyKey(treatyId);
        if (StateContext.StateExists(treatyKey))
        {
            throw new InvalidOperationException($"Treaty '{treatyId.ToHex()}' already exists.");
        }

        var treatyState = new TreatyProto.TreatyState
        {
            TreatyId = treatyId,
            Spec = input.Spec.Clone(),
            CreatedBy = ExecutionContext.SenderAddress,
            CreatedAtBlockHeight = ExecutionContext.BlockHeight,
            Status = TreatyProto.TreatyStatus.PendingParticipants,
            KillSwitchTriggered = false,
            KillSwitchReason = string.Empty,
            KillSwitchTriggeredBy = string.Empty,
            KillSwitchTriggeredAtBlockHeight = 0,
            OpenChallengeCount = 0,
            ActivityCount = 0,
            ChallengeCount = 0,
            FinalizedBy = string.Empty,
            FinalizedAtBlockHeight = 0,
            Settlement = new TreatyProto.TreatySettlement
            {
                Outcome = TreatyProto.TreatySettlementOutcome.Unspecified,
                JoinedParticipants = 0,
                ActivityCount = 0,
                ChallengeCount = 0
            }
        };

        foreach (var participant in input.Spec.Participants)
        {
            var participantState = new TreatyProto.ParticipantState
            {
                AgentAddress = participant.AgentAddress,
                Role = participant.Role,
                HasJoined = StringComparer.OrdinalIgnoreCase.Equals(participant.AgentAddress.ToHex(), creatorAddressHex),
                JoinSignature = ByteString.Empty,
                JoinedAtBlockHeight = 0,
                JoinedAtTransactionIndex = 0
            };

            if (participantState.HasJoined)
            {
                participantState.JoinedAtBlockHeight = ExecutionContext.BlockHeight;
                participantState.JoinedAtTransactionIndex = ExecutionContext.TransactionIndex;
                treatyState.Settlement.JoinedParticipants++;
            }

            treatyState.ParticipantStates.Add(participantState);
        }

        if (AreAllParticipantsJoined(treatyState))
        {
            treatyState.Status = TreatyProto.TreatyStatus.Active;
        }

        StateContext.SetState(treatyKey, treatyState.ToByteArray());
        StateContext.SetState(
            CreateTreatyCreatedEventKey(treatyId),
            new TreatyProto.TreatyCreated
            {
                TreatyId = treatyId,
                CreatedBy = ExecutionContext.SenderAddress,
                BlockHeight = ExecutionContext.BlockHeight,
                ParticipantCount = treatyState.ParticipantStates.Count,
                ChallengeWindowBlocks = input.Spec.ChallengeWindowBlocks
            }.ToByteArray());

        return treatyId;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetJoinTreatyReadKeys),
        WriteExtractor = nameof(GetJoinTreatyWriteKeys))]
    public Empty JoinTreaty(TreatyProto.JoinTreatyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        EnsureBytes(input.Signature, nameof(input.Signature));

        var treatyKey = CreateTreatyKey(input.TreatyId);
        var treatyState = LoadTreaty(treatyKey, input.TreatyId);
        EnsureJoinableTreaty(treatyState);

        var participant = GetParticipantState(treatyState, ExecutionContext.SenderAddress);
        if (participant.HasJoined)
        {
            throw new InvalidOperationException(
                $"Participant '{ExecutionContext.SenderAddress}' has already joined treaty '{input.TreatyId.ToHex()}'.");
        }

        if (!CryptoContext.VerifySignature(
                CreateJoinApprovalPayload(input.TreatyId, participant.AgentAddress),
                input.Signature.Span,
                ExecutionContext.SenderAddress))
        {
            throw new InvalidOperationException(
                $"Join signature for participant '{ExecutionContext.SenderAddress}' is invalid.");
        }

        participant.HasJoined = true;
        participant.JoinSignature = input.Signature;
        participant.JoinedAtBlockHeight = ExecutionContext.BlockHeight;
        participant.JoinedAtTransactionIndex = ExecutionContext.TransactionIndex;
        treatyState.Settlement.JoinedParticipants = checked(treatyState.Settlement.JoinedParticipants + 1);

        if (AreAllParticipantsJoined(treatyState))
        {
            treatyState.Status = TreatyProto.TreatyStatus.Active;
        }

        StateContext.SetState(treatyKey, treatyState.ToByteArray());
        StateContext.SetState(
            CreateTreatyJoinedEventKey(input.TreatyId, participant.AgentAddress),
            new TreatyProto.TreatyJoined
            {
                TreatyId = input.TreatyId,
                ParticipantAddress = participant.AgentAddress,
                Role = participant.Role,
                BlockHeight = ExecutionContext.BlockHeight,
                TransactionIndex = ExecutionContext.TransactionIndex
            }.ToByteArray());

        return Empty.Value;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetRecordActivityReadKeys),
        WriteExtractor = nameof(GetRecordActivityWriteKeys))]
    public Empty RecordActivity(TreatyProto.RecordActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Action);
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        ValidateActivity(input.Action);

        var treatyKey = CreateTreatyKey(input.TreatyId);
        var treatyState = LoadTreaty(treatyKey, input.TreatyId);
        EnsureActiveTreaty(treatyState);

        var participant = GetJoinedParticipantState(treatyState, ExecutionContext.SenderAddress);
        EnsurePermissionAllowed(
            treatyState.Spec.PermissionMatrix,
            participant.AgentAddress.ToHex(),
            input.Action.ContractName,
            input.Action.MethodName);

        var activityKey = CreateActivityKey(input.TreatyId, input.Action.StepHash);
        if (StateContext.StateExists(activityKey))
        {
            throw new InvalidOperationException(
                $"Step '{input.Action.StepHash.ToHex()}' already exists in treaty '{input.TreatyId.ToHex()}'.");
        }

        var associatedSessionId = ResolveAssociatedSessionId(treatyState, participant.AgentAddress, input.Action.AgentSessionId);
        var activity = new TreatyProto.TreatyRecordedActivity
        {
            TreatyId = input.TreatyId,
            StepHash = input.Action.StepHash,
            ActorAddress = participant.AgentAddress,
            ContractName = input.Action.ContractName,
            MethodName = input.Action.MethodName,
            Description = input.Action.Description ?? string.Empty,
            RecordedAtBlockHeight = ExecutionContext.BlockHeight,
            RecordedAtTransactionIndex = ExecutionContext.TransactionIndex
        };

        if (HasHash(associatedSessionId))
        {
            activity.AssociatedSessionId = associatedSessionId;
        }

        treatyState.ActivityCount = checked(treatyState.ActivityCount + 1);
        treatyState.Settlement.ActivityCount = treatyState.ActivityCount;

        StateContext.SetState(treatyKey, treatyState.ToByteArray());
        StateContext.SetState(activityKey, activity.ToByteArray());

        return Empty.Value;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetChallengeStepReadKeys),
        WriteExtractor = nameof(GetChallengeStepWriteKeys))]
    public Empty ChallengeStep(TreatyProto.ChallengeStepInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        EnsureHash(input.StepHash, nameof(input.StepHash));
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Reason, nameof(input.Reason));

        var treatyKey = CreateTreatyKey(input.TreatyId);
        var treatyState = LoadTreaty(treatyKey, input.TreatyId);
        EnsureActiveTreaty(treatyState);

        var challenger = GetJoinedParticipantState(treatyState, ExecutionContext.SenderAddress);
        var activityKey = CreateActivityKey(input.TreatyId, input.StepHash);
        var activityBytes = StateContext.GetState(activityKey);
        if (activityBytes is null)
        {
            throw new InvalidOperationException(
                $"Step '{input.StepHash.ToHex()}' does not exist in treaty '{input.TreatyId.ToHex()}'.");
        }

        var activity = TreatyProto.TreatyRecordedActivity.Parser.ParseFrom(activityBytes);
        if (StringComparer.OrdinalIgnoreCase.Equals(activity.ActorAddress.ToHex(), challenger.AgentAddress.ToHex()))
        {
            throw new InvalidOperationException("Participants cannot challenge their own recorded activity.");
        }

        var challengeDeadline = checked(activity.RecordedAtBlockHeight + treatyState.Spec.ChallengeWindowBlocks);
        if (ExecutionContext.BlockHeight > challengeDeadline)
        {
            throw new InvalidOperationException(
                $"Challenge window for step '{input.StepHash.ToHex()}' has closed at block {challengeDeadline}.");
        }

        var challengeKey = CreateChallengeKey(input.TreatyId, input.StepHash, challenger.AgentAddress);
        if (StateContext.StateExists(challengeKey))
        {
            throw new InvalidOperationException(
                $"Participant '{challenger.AgentAddress.ToHex()}' has already challenged step '{input.StepHash.ToHex()}'.");
        }

        treatyState.OpenChallengeCount = checked(treatyState.OpenChallengeCount + 1);
        treatyState.ChallengeCount = checked(treatyState.ChallengeCount + 1);
        treatyState.Settlement.ChallengeCount = treatyState.ChallengeCount;

        StateContext.SetState(treatyKey, treatyState.ToByteArray());
        StateContext.SetState(
            challengeKey,
            new TreatyProto.TreatyChallengeRecord
            {
                TreatyId = input.TreatyId,
                StepHash = input.StepHash,
                ChallengerAddress = challenger.AgentAddress,
                Reason = input.Reason,
                ChallengedAtBlockHeight = ExecutionContext.BlockHeight,
                ChallengedAtTransactionIndex = ExecutionContext.TransactionIndex
            }.ToByteArray());
        StateContext.SetState(
            CreateStepChallengedEventKey(input.TreatyId, input.StepHash, challenger.AgentAddress),
            new TreatyProto.StepChallenged
            {
                TreatyId = input.TreatyId,
                StepHash = input.StepHash,
                ChallengerAddress = challenger.AgentAddress,
                Reason = input.Reason,
                BlockHeight = ExecutionContext.BlockHeight
            }.ToByteArray());

        return Empty.Value;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetTriggerKillSwitchReadKeys),
        WriteExtractor = nameof(GetTriggerKillSwitchWriteKeys))]
    public Empty TriggerKillSwitch(TreatyProto.TriggerKillSwitchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Reason, nameof(input.Reason));

        var treatyKey = CreateTreatyKey(input.TreatyId);
        var treatyState = LoadTreaty(treatyKey, input.TreatyId);
        EnsureNotFinalized(treatyState);

        if (treatyState.KillSwitchTriggered)
        {
            throw new InvalidOperationException($"Treaty '{input.TreatyId.ToHex()}' kill switch has already been triggered.");
        }

        EnsureParticipantOrAdmin(treatyState, allowListedParticipantWithoutJoin: true);

        treatyState.KillSwitchTriggered = true;
        treatyState.KillSwitchReason = input.Reason;
        treatyState.KillSwitchTriggeredBy = ExecutionContext.SenderAddress;
        treatyState.KillSwitchTriggeredAtBlockHeight = ExecutionContext.BlockHeight;
        treatyState.Status = TreatyProto.TreatyStatus.Killed;

        StateContext.SetState(treatyKey, treatyState.ToByteArray());
        StateContext.SetState(
            CreateKillSwitchTriggeredEventKey(input.TreatyId),
            new TreatyProto.KillSwitchTriggered
            {
                TreatyId = input.TreatyId,
                TriggeredBy = ExecutionContext.SenderAddress,
                Reason = input.Reason,
                BlockHeight = ExecutionContext.BlockHeight
            }.ToByteArray());

        return Empty.Value;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetFinalizeTreatyReadKeys),
        WriteExtractor = nameof(GetFinalizeTreatyWriteKeys))]
    public TreatyProto.TreatySettlement FinalizeTreaty(TreatyProto.FinalizeTreatyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureHash(input.TreatyId, nameof(input.TreatyId));

        var treatyKey = CreateTreatyKey(input.TreatyId);
        var treatyState = LoadTreaty(treatyKey, input.TreatyId);
        EnsureFinalizableTreaty(treatyState);
        EnsureParticipantOrAdmin(treatyState, allowListedParticipantWithoutJoin: false);

        treatyState.Status = TreatyProto.TreatyStatus.Finalized;
        treatyState.FinalizedBy = ExecutionContext.SenderAddress;
        treatyState.FinalizedAtBlockHeight = ExecutionContext.BlockHeight;
        treatyState.Settlement = CreateSettlement(treatyState);

        StateContext.SetState(treatyKey, treatyState.ToByteArray());
        StateContext.SetState(
            CreateTreatyFinalizedEventKey(input.TreatyId),
            new TreatyProto.TreatyFinalized
            {
                TreatyId = input.TreatyId,
                FinalizedBy = ExecutionContext.SenderAddress,
                BlockHeight = ExecutionContext.BlockHeight,
                Outcome = treatyState.Settlement.Outcome,
                JoinedParticipants = treatyState.Settlement.JoinedParticipants,
                ActivityCount = treatyState.Settlement.ActivityCount,
                ChallengeCount = treatyState.Settlement.ChallengeCount
            }.ToByteArray());

        return treatyState.Settlement;
    }

    [ContractMethod]
    public TreatyProto.TreatyState GetTreaty(TreatyProto.GetTreatyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        return LoadTreaty(CreateTreatyKey(input.TreatyId), input.TreatyId);
    }

    [ContractMethod]
    public TreatyProto.TreatyPermissionMatrix GetPermissionMatrix(TreatyProto.GetTreatyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureHash(input.TreatyId, nameof(input.TreatyId));

        var treatyState = LoadTreaty(CreateTreatyKey(input.TreatyId), input.TreatyId);
        return treatyState.Spec.PermissionMatrix?.Clone() ?? new TreatyProto.TreatyPermissionMatrix();
    }

    private Hash CreateTreatyId()
    {
        var senderBytes = Encoding.UTF8.GetBytes(ExecutionContext.SenderAddress);
        var contractBytes = Encoding.UTF8.GetBytes(ExecutionContext.CurrentContractAddress);
        var blockHeightBytes = new byte[sizeof(long)];
        var transactionIndexBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt64LittleEndian(blockHeightBytes, ExecutionContext.BlockHeight);
        BinaryPrimitives.WriteInt32LittleEndian(transactionIndexBytes, ExecutionContext.TransactionIndex);

        var payload = new byte[senderBytes.Length + contractBytes.Length + blockHeightBytes.Length + transactionIndexBytes.Length];
        senderBytes.CopyTo(payload, 0);
        contractBytes.CopyTo(payload, senderBytes.Length);
        blockHeightBytes.CopyTo(payload, senderBytes.Length + contractBytes.Length);
        transactionIndexBytes.CopyTo(payload, senderBytes.Length + contractBytes.Length + blockHeightBytes.Length);

        return new Hash
        {
            Value = ByteString.CopyFrom(SHA256.HashData(payload))
        };
    }

    private TreatyProto.TreatyState LoadTreaty(string treatyKey, Hash treatyId)
    {
        var treatyBytes = StateContext.GetState(treatyKey);
        if (treatyBytes is null)
        {
            throw new InvalidOperationException($"Treaty '{treatyId.ToHex()}' was not found.");
        }

        return TreatyProto.TreatyState.Parser.ParseFrom(treatyBytes);
    }

    private TreatyProto.ParticipantState GetParticipantState(TreatyProto.TreatyState treatyState, string senderAddress)
    {
        var participant = treatyState.ParticipantStates.FirstOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.AgentAddress?.ToHex(), senderAddress));
        if (participant is null)
        {
            throw new InvalidOperationException(
                $"Sender '{senderAddress}' is not a participant in treaty '{treatyState.TreatyId.ToHex()}'.");
        }

        return participant;
    }

    private TreatyProto.ParticipantState GetJoinedParticipantState(TreatyProto.TreatyState treatyState, string senderAddress)
    {
        var participant = GetParticipantState(treatyState, senderAddress);
        if (!participant.HasJoined)
        {
            throw new InvalidOperationException(
                $"Participant '{senderAddress}' has not joined treaty '{treatyState.TreatyId.ToHex()}'.");
        }

        return participant;
    }

    private void EnsureParticipantOrAdmin(TreatyProto.TreatyState treatyState, bool allowListedParticipantWithoutJoin)
    {
        if (IsAdmin())
        {
            return;
        }

        var participant = GetParticipantState(treatyState, ExecutionContext.SenderAddress);
        if (!allowListedParticipantWithoutJoin && !participant.HasJoined)
        {
            throw new InvalidOperationException(
                $"Participant '{ExecutionContext.SenderAddress}' has not joined treaty '{treatyState.TreatyId.ToHex()}'.");
        }
    }

    private bool IsAdmin()
    {
        var adminAddress = IdentityContext.GetVirtualAddress(ExecutionContext.CurrentContractAddress, AdminSalt);
        return StringComparer.OrdinalIgnoreCase.Equals(ExecutionContext.SenderAddress, adminAddress);
    }

    private static void ValidateTreatySpec(TreatyProto.TreatySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec.PermissionMatrix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spec.ChallengeWindowBlocks);

        if (spec.Participants.Count == 0)
        {
            throw new InvalidOperationException("Treaty must define at least one participant.");
        }

        var participantAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roles = new HashSet<TreatyProto.TreatyRole>();
        var hasLeader = false;

        foreach (var participant in spec.Participants)
        {
            EnsureAddress(participant.AgentAddress, nameof(spec.Participants));
            EnsureRoleSpecified(participant.Role, nameof(spec.Participants));

            if (!participantAddresses.Add(participant.AgentAddress.ToHex()))
            {
                throw new InvalidOperationException(
                    $"Treaty participant '{participant.AgentAddress.ToHex()}' is duplicated.");
            }

            roles.Add(participant.Role);
            hasLeader |= participant.Role == TreatyProto.TreatyRole.Leader;
        }

        if (!hasLeader)
        {
            throw new InvalidOperationException("Treaty must include at least one leader.");
        }

        var budgetRoles = new HashSet<TreatyProto.TreatyRole>();
        foreach (var roleBudget in spec.RoleBudgets)
        {
            EnsureRoleSpecified(roleBudget.Role, nameof(spec.RoleBudgets));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roleBudget.TokenLimit);

            if (!budgetRoles.Add(roleBudget.Role))
            {
                throw new InvalidOperationException($"Treaty role budget for '{roleBudget.Role}' is duplicated.");
            }
        }

        foreach (var role in roles)
        {
            if (!budgetRoles.Contains(role))
            {
                throw new InvalidOperationException($"Treaty role '{role}' does not have a token budget.");
            }
        }

        foreach (var grant in spec.PermissionMatrix.Grants)
        {
            EnsureAddress(grant.AgentAddress, nameof(spec.PermissionMatrix.Grants));
            if (!participantAddresses.Contains(grant.AgentAddress.ToHex()))
            {
                throw new InvalidOperationException(
                    $"Permission grant for '{grant.AgentAddress.ToHex()}' references a non-participant.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(grant.ContractName, nameof(spec.PermissionMatrix.Grants));
            ArgumentException.ThrowIfNullOrWhiteSpace(grant.MethodName, nameof(spec.PermissionMatrix.Grants));
        }
    }

    private static void ValidateActivity(TreatyProto.TreatyActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        EnsureHash(activity.StepHash, nameof(activity.StepHash));
        ArgumentException.ThrowIfNullOrWhiteSpace(activity.ContractName, nameof(activity.ContractName));
        ArgumentException.ThrowIfNullOrWhiteSpace(activity.MethodName, nameof(activity.MethodName));
    }

    private void EnsurePermissionAllowed(
        TreatyProto.TreatyPermissionMatrix permissionMatrix,
        string actorAddress,
        string contractName,
        string methodName)
    {
        ArgumentNullException.ThrowIfNull(permissionMatrix);

        if (!permissionMatrix.Grants.Any(grant =>
                StringComparer.OrdinalIgnoreCase.Equals(grant.AgentAddress?.ToHex(), actorAddress) &&
                StringComparer.Ordinal.Equals(grant.ContractName, contractName) &&
                StringComparer.Ordinal.Equals(grant.MethodName, methodName)))
        {
            throw new InvalidOperationException(
                $"Participant '{actorAddress}' is not allowed to call '{contractName}.{methodName}' under treaty '{ExecutionContext.CurrentContractAddress}'.");
        }
    }

    private Hash ResolveAssociatedSessionId(
        TreatyProto.TreatyState treatyState,
        Address participantAddress,
        Hash? requestedSessionId)
    {
        var binding = treatyState.SessionBindings.FirstOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.ParticipantAddress?.ToHex(), participantAddress.ToHex()));
        if (HasHash(requestedSessionId))
        {
            if (binding is not null && !StringComparer.Ordinal.Equals(binding.SessionId.ToHex(), requestedSessionId!.ToHex()))
            {
                throw new InvalidOperationException(
                    $"Participant '{participantAddress.ToHex()}' is already associated with session '{binding.SessionId.ToHex()}'.");
            }

            if (binding is null)
            {
                binding = new TreatyProto.TreatySessionBinding
                {
                    ParticipantAddress = participantAddress,
                    SessionId = requestedSessionId!
                };
                treatyState.SessionBindings.Add(binding);
            }

            return binding.SessionId;
        }

        if (binding is null)
        {
            throw new InvalidOperationException(
                $"Participant '{participantAddress.ToHex()}' must provide an agent session for its first treaty activity.");
        }

        return binding.SessionId;
    }

    private static bool AreAllParticipantsJoined(TreatyProto.TreatyState treatyState)
    {
        return treatyState.ParticipantStates.All(static participant => participant.HasJoined);
    }

    private static void EnsureJoinableTreaty(TreatyProto.TreatyState treatyState)
    {
        EnsureNotFinalized(treatyState);

        if (treatyState.KillSwitchTriggered)
        {
            throw new InvalidOperationException(
                $"Treaty '{treatyState.TreatyId.ToHex()}' has been stopped by kill switch.");
        }
    }

    private static void EnsureActiveTreaty(TreatyProto.TreatyState treatyState)
    {
        EnsureNotFinalized(treatyState);

        if (treatyState.KillSwitchTriggered || treatyState.Status == TreatyProto.TreatyStatus.Killed)
        {
            throw new InvalidOperationException(
                $"Treaty '{treatyState.TreatyId.ToHex()}' is not active because kill switch has been triggered.");
        }

        if (treatyState.Status != TreatyProto.TreatyStatus.Active)
        {
            throw new InvalidOperationException(
                $"Treaty '{treatyState.TreatyId.ToHex()}' is not active.");
        }
    }

    private static void EnsureFinalizableTreaty(TreatyProto.TreatyState treatyState)
    {
        EnsureNotFinalized(treatyState);

        if (treatyState.Status == TreatyProto.TreatyStatus.PendingParticipants && !treatyState.KillSwitchTriggered)
        {
            throw new InvalidOperationException(
                $"Treaty '{treatyState.TreatyId.ToHex()}' cannot be finalized before all participants join or the kill switch is triggered.");
        }
    }

    private static void EnsureNotFinalized(TreatyProto.TreatyState treatyState)
    {
        if (treatyState.Status == TreatyProto.TreatyStatus.Finalized)
        {
            throw new InvalidOperationException(
                $"Treaty '{treatyState.TreatyId.ToHex()}' has already been finalized.");
        }
    }

    private static TreatyProto.TreatySettlement CreateSettlement(TreatyProto.TreatyState treatyState)
    {
        var outcome = treatyState.KillSwitchTriggered
            ? TreatyProto.TreatySettlementOutcome.Killed
            : treatyState.ChallengeCount > 0
                ? TreatyProto.TreatySettlementOutcome.Disputed
                : TreatyProto.TreatySettlementOutcome.Completed;

        return new TreatyProto.TreatySettlement
        {
            Outcome = outcome,
            JoinedParticipants = treatyState.Settlement.JoinedParticipants,
            ActivityCount = treatyState.ActivityCount,
            ChallengeCount = treatyState.ChallengeCount
        };
    }

    private Address ParseSenderAddress(string methodName)
    {
        try
        {
            return ExecutionContext.SenderAddress.ToProtoAddress();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Sender '{ExecutionContext.SenderAddress}' cannot create a treaty via '{methodName}' because it is not a valid address payload.",
                exception);
        }
    }

    private static byte[] CreateJoinApprovalPayload(Hash treatyId, Address participantAddress)
    {
        var treatyBytes = treatyId.Value.ToByteArray();
        var participantBytes = participantAddress.Value.ToByteArray();
        var prefixBytes = Encoding.UTF8.GetBytes("nightelf:treaty:join");
        var payload = new byte[prefixBytes.Length + treatyBytes.Length + participantBytes.Length];
        prefixBytes.CopyTo(payload, 0);
        treatyBytes.CopyTo(payload, prefixBytes.Length);
        participantBytes.CopyTo(payload, prefixBytes.Length + treatyBytes.Length);
        return SHA256.HashData(payload);
    }

    private static bool HasHash(Hash? hash)
    {
        return hash is not null && !hash.Value.IsEmpty;
    }

    private static void EnsureAddress(Address? address, string parameterName)
    {
        if (address is null || address.Value.IsEmpty)
        {
            throw new ArgumentException("Address payload must not be empty.", parameterName);
        }
    }

    private static void EnsureHash(Hash? hash, string parameterName)
    {
        if (hash is null || hash.Value.IsEmpty)
        {
            throw new ArgumentException("Hash payload must not be empty.", parameterName);
        }
    }

    private static void EnsureBytes(ByteString? bytes, string parameterName)
    {
        if (bytes is null || bytes.IsEmpty)
        {
            throw new ArgumentException("Byte payload must not be empty.", parameterName);
        }
    }

    private static void EnsureRoleSpecified(TreatyProto.TreatyRole role, string parameterName)
    {
        if (role == TreatyProto.TreatyRole.Unspecified)
        {
            throw new ArgumentOutOfRangeException(parameterName, role, "TreatyRole must be specified.");
        }
    }

    private static string CreateTreatyKey(Hash treatyId) => $"treaty:{treatyId.ToHex()}";

    private static string CreateActivityKey(Hash treatyId, Hash stepHash) =>
        $"{CreateTreatyKey(treatyId)}:activity:{stepHash.ToHex()}";

    private static string CreateChallengeKey(Hash treatyId, Hash stepHash, Address challengerAddress) =>
        $"{CreateTreatyKey(treatyId)}:challenge:{stepHash.ToHex()}:{challengerAddress.ToHex()}";

    private static string CreateTreatyCreatedEventKey(Hash treatyId) =>
        $"{CreateTreatyKey(treatyId)}:event:created";

    private static string CreateTreatyJoinedEventKey(Hash treatyId, Address participantAddress) =>
        $"{CreateTreatyKey(treatyId)}:event:joined:{participantAddress.ToHex()}";

    private static string CreateStepChallengedEventKey(Hash treatyId, Hash stepHash, Address challengerAddress) =>
        $"{CreateTreatyKey(treatyId)}:event:challenge:{stepHash.ToHex()}:{challengerAddress.ToHex()}";

    private static string CreateKillSwitchTriggeredEventKey(Hash treatyId) =>
        $"{CreateTreatyKey(treatyId)}:event:kill-switch";

    private static string CreateTreatyFinalizedEventKey(Hash treatyId) =>
        $"{CreateTreatyKey(treatyId)}:event:finalized";

    private static IEnumerable<string> GetJoinTreatyReadKeys(TreatyProto.JoinTreatyInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        yield return CreateTreatyKey(input.TreatyId);
    }

    private static IEnumerable<string> GetJoinTreatyWriteKeys(TreatyProto.JoinTreatyInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));

        yield return CreateTreatyKey(input.TreatyId);
    }

    private static IEnumerable<string> GetRecordActivityReadKeys(TreatyProto.RecordActivityInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        ValidateActivity(input.Action);
        yield return CreateTreatyKey(input.TreatyId);
        yield return CreateActivityKey(input.TreatyId, input.Action.StepHash);
    }

    private static IEnumerable<string> GetRecordActivityWriteKeys(TreatyProto.RecordActivityInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        ValidateActivity(input.Action);
        yield return CreateTreatyKey(input.TreatyId);
        yield return CreateActivityKey(input.TreatyId, input.Action.StepHash);
    }

    private static IEnumerable<string> GetChallengeStepReadKeys(TreatyProto.ChallengeStepInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        EnsureHash(input.StepHash, nameof(input.StepHash));
        yield return CreateTreatyKey(input.TreatyId);
        yield return CreateActivityKey(input.TreatyId, input.StepHash);
    }

    private static IEnumerable<string> GetChallengeStepWriteKeys(TreatyProto.ChallengeStepInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        EnsureHash(input.StepHash, nameof(input.StepHash));
        yield return CreateTreatyKey(input.TreatyId);
        yield return CreateActivityKey(input.TreatyId, input.StepHash);
    }

    private static IEnumerable<string> GetTriggerKillSwitchReadKeys(TreatyProto.TriggerKillSwitchInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        yield return CreateTreatyKey(input.TreatyId);
    }

    private static IEnumerable<string> GetTriggerKillSwitchWriteKeys(TreatyProto.TriggerKillSwitchInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        yield return CreateTreatyKey(input.TreatyId);
        yield return CreateKillSwitchTriggeredEventKey(input.TreatyId);
    }

    private static IEnumerable<string> GetFinalizeTreatyReadKeys(TreatyProto.FinalizeTreatyInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        yield return CreateTreatyKey(input.TreatyId);
    }

    private static IEnumerable<string> GetFinalizeTreatyWriteKeys(TreatyProto.FinalizeTreatyInput input)
    {
        EnsureHash(input.TreatyId, nameof(input.TreatyId));
        yield return CreateTreatyKey(input.TreatyId);
        yield return CreateTreatyFinalizedEventKey(input.TreatyId);
    }
}
