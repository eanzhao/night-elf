using System.Buffers.Binary;
using System.Security.Cryptography;

using Google.Protobuf;

using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Sdk.CSharp;

using AgentSessionProto = NightElf.Contracts.System.AgentSession.Protobuf;

namespace NightElf.Contracts.System.AgentSession;

public sealed partial class AgentSessionContract : CSharpSmartContract
{
    private const string AdminSalt = "agent-session-admin";

    [ContractMethod]
    public Hash OpenSession(AgentSessionProto.OpenSessionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureAddress(input.AgentAddress, nameof(input.AgentAddress));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.TokenBudget);

        EnsureOpenAuthorized(input.AgentAddress);

        var sessionId = CreateSessionId(input.AgentAddress);
        var sessionKey = CreateSessionKey(sessionId);
        if (StateContext.StateExists(sessionKey))
        {
            throw new InvalidOperationException($"Session '{sessionId.ToHex()}' already exists.");
        }

        var sessionState = new AgentSessionProto.SessionState
        {
            SessionId = sessionId,
            AgentAddress = input.AgentAddress,
            TokenBudget = input.TokenBudget,
            InputTokensConsumed = 0,
            OutputTokensConsumed = 0,
            IsFinalized = false,
            OpenedAtBlockHeight = ExecutionContext.BlockHeight,
            OpenedAtTransactionIndex = ExecutionContext.TransactionIndex,
            OpenedBy = ExecutionContext.SenderAddress,
            FinalizedBy = string.Empty,
            FinalizedAtBlockHeight = 0
        };

        StateContext.SetState(sessionKey, sessionState.ToByteArray());
        StateContext.SetState(
            CreateSessionOpenedEventKey(sessionId),
            new AgentSessionProto.SessionOpened
            {
                SessionId = sessionId,
                AgentAddress = input.AgentAddress,
                TokenBudget = input.TokenBudget,
                OpenedBy = ExecutionContext.SenderAddress,
                BlockHeight = ExecutionContext.BlockHeight,
                TransactionIndex = ExecutionContext.TransactionIndex
            }.ToByteArray());

        return sessionId;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetRecordStepReadKeys),
        WriteExtractor = nameof(GetRecordStepWriteKeys))]
    public Empty RecordStep(AgentSessionProto.RecordStepInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureHash(input.SessionId, nameof(input.SessionId));
        EnsureHash(input.StepContentHash, nameof(input.StepContentHash));
        ArgumentOutOfRangeException.ThrowIfNegative(input.InputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(input.OutputTokens);

        var sessionKey = CreateSessionKey(input.SessionId);
        var sessionState = LoadSession(sessionKey, input.SessionId);
        EnsureMutableSession(sessionState);
        EnsureSessionOwner(sessionState);

        var totalConsumed = checked(sessionState.InputTokensConsumed + sessionState.OutputTokensConsumed);
        var delta = checked(input.InputTokens + input.OutputTokens);
        var nextTotalConsumed = checked(totalConsumed + delta);
        if (nextTotalConsumed > sessionState.TokenBudget)
        {
            throw new InvalidOperationException(
                $"Recording the step would exceed the token budget for session '{input.SessionId.ToHex()}'.");
        }

        sessionState.InputTokensConsumed = checked(sessionState.InputTokensConsumed + input.InputTokens);
        sessionState.OutputTokensConsumed = checked(sessionState.OutputTokensConsumed + input.OutputTokens);
        StateContext.SetState(sessionKey, sessionState.ToByteArray());
        StateContext.SetState(
            CreateStepRecordedEventKey(input.SessionId, input.StepContentHash),
            new AgentSessionProto.StepRecorded
            {
                SessionId = input.SessionId,
                StepContentHash = input.StepContentHash,
                InputTokens = input.InputTokens,
                OutputTokens = input.OutputTokens,
                TotalTokensConsumed = nextTotalConsumed
            }.ToByteArray());

        return Empty.Value;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetFinalizeSessionReadKeys),
        WriteExtractor = nameof(GetFinalizeSessionWriteKeys))]
    public Empty FinalizeSession(AgentSessionProto.FinalizeSessionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureHash(input.SessionId, nameof(input.SessionId));

        var sessionKey = CreateSessionKey(input.SessionId);
        var sessionState = LoadSession(sessionKey, input.SessionId);
        EnsureMutableSession(sessionState);
        EnsureSessionOwner(sessionState);

        var totalConsumed = checked(sessionState.InputTokensConsumed + sessionState.OutputTokensConsumed);
        sessionState.IsFinalized = true;
        sessionState.FinalizedBy = ExecutionContext.SenderAddress;
        sessionState.FinalizedAtBlockHeight = ExecutionContext.BlockHeight;

        StateContext.SetState(sessionKey, sessionState.ToByteArray());
        StateContext.SetState(
            CreateSessionFinalizedEventKey(input.SessionId),
            new AgentSessionProto.SessionFinalized
            {
                SessionId = input.SessionId,
                TotalTokensConsumed = totalConsumed,
                RemainingBudget = sessionState.TokenBudget - totalConsumed,
                FinalizedBy = ExecutionContext.SenderAddress,
                BlockHeight = ExecutionContext.BlockHeight
            }.ToByteArray());

        return Empty.Value;
    }

    private Hash CreateSessionId(Address agentAddress)
    {
        return CreateSessionId(agentAddress, ExecutionContext.BlockHeight, ExecutionContext.TransactionIndex);
    }

    private AgentSessionProto.SessionState LoadSession(string sessionKey, Hash sessionId)
    {
        var sessionBytes = StateContext.GetState(sessionKey);
        if (sessionBytes is null)
        {
            throw new InvalidOperationException($"Session '{sessionId.ToHex()}' was not found.");
        }

        return AgentSessionProto.SessionState.Parser.ParseFrom(sessionBytes);
    }

    private void EnsureOpenAuthorized(Address agentAddress)
    {
        var agentAddressHex = agentAddress.ToHex();
        var adminAddress = IdentityContext.GetVirtualAddress(ExecutionContext.CurrentContractAddress, AdminSalt);
        if (!StringComparer.OrdinalIgnoreCase.Equals(ExecutionContext.SenderAddress, agentAddressHex) &&
            !StringComparer.OrdinalIgnoreCase.Equals(ExecutionContext.SenderAddress, adminAddress))
        {
            throw new InvalidOperationException(
                $"Sender '{ExecutionContext.SenderAddress}' is not allowed to open a session for '{agentAddressHex}'.");
        }
    }

    private void EnsureSessionOwner(AgentSessionProto.SessionState sessionState)
    {
        var ownerAddress = sessionState.AgentAddress.ToHex();
        if (!StringComparer.OrdinalIgnoreCase.Equals(ExecutionContext.SenderAddress, ownerAddress))
        {
            throw new InvalidOperationException(
                $"Sender '{ExecutionContext.SenderAddress}' is not the session owner '{ownerAddress}'.");
        }
    }

    private static void EnsureMutableSession(AgentSessionProto.SessionState sessionState)
    {
        if (sessionState.IsFinalized)
        {
            throw new InvalidOperationException(
                $"Session '{sessionState.SessionId.ToHex()}' has already been finalized.");
        }
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

    private static string CreateSessionKey(Hash sessionId)
    {
        return $"session:{sessionId.ToHex()}";
    }

    private static string CreateSessionOpenedEventKey(Hash sessionId)
    {
        return $"{CreateSessionKey(sessionId)}:event:opened";
    }

    private static string CreateStepRecordedEventKey(Hash sessionId, Hash stepContentHash)
    {
        return $"{CreateSessionKey(sessionId)}:event:step:{stepContentHash.ToHex()}";
    }

    private static string CreateSessionFinalizedEventKey(Hash sessionId)
    {
        return $"{CreateSessionKey(sessionId)}:event:finalized";
    }

    private static IEnumerable<string> GetRecordStepReadKeys(AgentSessionProto.RecordStepInput input)
    {
        EnsureHash(input.SessionId, nameof(input.SessionId));
        yield return CreateSessionKey(input.SessionId);
    }

    private static IEnumerable<string> GetFinalizeSessionReadKeys(AgentSessionProto.FinalizeSessionInput input)
    {
        EnsureHash(input.SessionId, nameof(input.SessionId));
        yield return CreateSessionKey(input.SessionId);
    }

    private static IEnumerable<string> GetRecordStepWriteKeys(AgentSessionProto.RecordStepInput input)
    {
        EnsureHash(input.SessionId, nameof(input.SessionId));
        EnsureHash(input.StepContentHash, nameof(input.StepContentHash));

        yield return CreateSessionKey(input.SessionId);
        yield return CreateStepRecordedEventKey(input.SessionId, input.StepContentHash);
    }

    private static IEnumerable<string> GetFinalizeSessionWriteKeys(AgentSessionProto.FinalizeSessionInput input)
    {
        EnsureHash(input.SessionId, nameof(input.SessionId));

        yield return CreateSessionKey(input.SessionId);
        yield return CreateSessionFinalizedEventKey(input.SessionId);
    }

    private static Hash CreateSessionId(Address agentAddress, long blockHeight, int transactionIndex)
    {
        EnsureAddress(agentAddress, nameof(agentAddress));

        var blockHeightBytes = new byte[sizeof(long)];
        var transactionIndexBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt64LittleEndian(blockHeightBytes, blockHeight);
        BinaryPrimitives.WriteInt32LittleEndian(transactionIndexBytes, transactionIndex);

        var payload = new byte[agentAddress.Value.Length + blockHeightBytes.Length + transactionIndexBytes.Length];
        agentAddress.Value.Span.CopyTo(payload);
        blockHeightBytes.CopyTo(payload, agentAddress.Value.Length);
        transactionIndexBytes.CopyTo(payload, agentAddress.Value.Length + blockHeightBytes.Length);

        return new Hash
        {
            Value = ByteString.CopyFrom(SHA256.HashData(payload))
        };
    }
}
