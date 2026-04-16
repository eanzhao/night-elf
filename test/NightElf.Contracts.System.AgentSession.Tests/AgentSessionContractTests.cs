using System.Buffers.Binary;
using System.Security.Cryptography;

using NightElf.Contracts.System.AgentSession;
using NightElf.Contracts.System.AgentSession.Protobuf;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Kernel.SmartContract;
using NightElf.Sdk.CSharp;

namespace NightElf.Contracts.System.AgentSession.Tests;

public sealed class AgentSessionContractTests
{
    [Fact]
    public void OpenSession_Should_Create_Session_State_And_Opened_Event_For_Owner()
    {
        using var harness = new ContractHarness();
        var agentAddress = CreateAddress(
            "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");

        var sessionId = Hash.Decode(
            harness.Execute(
                sender: agentAddress.ToHex(),
                methodName: "OpenSession",
                payload: OpenSessionInput.Encode(new OpenSessionInput
                {
                    AgentAddress = agentAddress,
                    TokenBudget = 500
                }),
                transactionIndex: 3));
        var sessionState = SessionState.Decode(harness.GetState(CreateSessionKey(sessionId)));
        var openedEvent = SessionOpened.Decode(harness.GetState(CreateOpenedEventKey(sessionId)));

        Assert.Equal(CreateExpectedSessionId(agentAddress, harness.BlockHeight, 3).ToHex(), sessionId.ToHex());
        Assert.Equal(agentAddress.ToHex(), sessionState.AgentAddress.ToHex());
        Assert.Equal(500, sessionState.TokenBudget);
        Assert.False(sessionState.IsFinalized);
        Assert.Equal(3, sessionState.OpenedAtTransactionIndex);
        Assert.Equal(agentAddress.ToHex(), sessionState.OpenedBy);
        Assert.Equal(sessionId.ToHex(), openedEvent.SessionId.ToHex());
        Assert.Equal(agentAddress.ToHex(), openedEvent.AgentAddress.ToHex());
        Assert.Equal(500, openedEvent.TokenBudget);
        Assert.Equal(agentAddress.ToHex(), openedEvent.OpenedBy);
        Assert.Equal(harness.BlockHeight, openedEvent.BlockHeight);
        Assert.Equal(3, openedEvent.TransactionIndex);
    }

    [Fact]
    public void OpenSession_Should_Allow_Admin_Virtual_Address()
    {
        using var harness = new ContractHarness();
        var agentAddress = CreateAddress(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        var sessionId = Hash.Decode(
            harness.Execute(
                sender: harness.AdminAddress,
                methodName: "OpenSession",
                payload: OpenSessionInput.Encode(new OpenSessionInput
                {
                    AgentAddress = agentAddress,
                    TokenBudget = 100
                }),
                transactionIndex: 0));
        var sessionState = SessionState.Decode(harness.GetState(CreateSessionKey(sessionId)));

        Assert.Equal(agentAddress.ToHex(), sessionState.AgentAddress.ToHex());
        Assert.Equal(harness.AdminAddress, sessionState.OpenedBy);
    }

    [Fact]
    public void OpenSession_Should_Reject_Sender_That_Is_Neither_Owner_Nor_Admin()
    {
        using var harness = new ContractHarness();
        var agentAddress = CreateAddress(
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: "someone-else",
                methodName: "OpenSession",
                payload: OpenSessionInput.Encode(new OpenSessionInput
                {
                    AgentAddress = agentAddress,
                    TokenBudget = 100
                }),
                transactionIndex: 1));

        Assert.Contains("not allowed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordStep_Should_Update_Token_Counters_And_Emit_Event()
    {
        using var harness = new ContractHarness();
        var agentAddress = CreateAddress(
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC");
        var stepHash = "111122223333444455556666777788889999AAAABBBBCCCCDDDDEEEEFFFF0000".ToProtoHash();
        var sessionId = Hash.Decode(
            harness.Execute(
                sender: agentAddress.ToHex(),
                methodName: "OpenSession",
                payload: OpenSessionInput.Encode(new OpenSessionInput
                {
                    AgentAddress = agentAddress,
                    TokenBudget = 90
                }),
                transactionIndex: 2));

        harness.Execute(
            sender: agentAddress.ToHex(),
            methodName: "RecordStep",
            payload: RecordStepInput.Encode(new RecordStepInput
            {
                SessionId = sessionId,
                StepContentHash = stepHash,
                InputTokens = 25,
                OutputTokens = 15
            }),
            transactionIndex: 4);

        var sessionState = SessionState.Decode(harness.GetState(CreateSessionKey(sessionId)));
        var stepEvent = StepRecorded.Decode(harness.GetState(CreateStepEventKey(sessionId, stepHash)));

        Assert.Equal(25, sessionState.InputTokensConsumed);
        Assert.Equal(15, sessionState.OutputTokensConsumed);
        Assert.Equal(sessionId.ToHex(), stepEvent.SessionId.ToHex());
        Assert.Equal(stepHash.ToHex(), stepEvent.StepContentHash.ToHex());
        Assert.Equal(40, stepEvent.TotalTokensConsumed);
    }

    [Fact]
    public void RecordStep_Should_Reject_NonOwner_And_Budget_Overruns()
    {
        using var harness = new ContractHarness();
        var agentAddress = CreateAddress(
            "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD");
        var sessionId = Hash.Decode(
            harness.Execute(
                sender: agentAddress.ToHex(),
                methodName: "OpenSession",
                payload: OpenSessionInput.Encode(new OpenSessionInput
                {
                    AgentAddress = agentAddress,
                    TokenBudget = 20
                }),
                transactionIndex: 0));

        var nonOwnerException = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: "not-owner",
                methodName: "RecordStep",
                payload: RecordStepInput.Encode(new RecordStepInput
                {
                    SessionId = sessionId,
                    StepContentHash = "AB".PadLeft(64, '0').ToProtoHash(),
                    InputTokens = 1,
                    OutputTokens = 1
                }),
                transactionIndex: 1));
        Assert.Contains("not the session owner", nonOwnerException.Message, StringComparison.OrdinalIgnoreCase);

        var budgetException = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: agentAddress.ToHex(),
                methodName: "RecordStep",
                payload: RecordStepInput.Encode(new RecordStepInput
                {
                    SessionId = sessionId,
                    StepContentHash = "CD".PadLeft(64, '0').ToProtoHash(),
                    InputTokens = 15,
                    OutputTokens = 10
                }),
                transactionIndex: 2));
        Assert.Contains("budget", budgetException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinalizeSession_Should_Emit_Event_And_Reject_Further_Writes()
    {
        using var harness = new ContractHarness();
        var agentAddress = CreateAddress(
            "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE");
        var sessionId = Hash.Decode(
            harness.Execute(
                sender: agentAddress.ToHex(),
                methodName: "OpenSession",
                payload: OpenSessionInput.Encode(new OpenSessionInput
                {
                    AgentAddress = agentAddress,
                    TokenBudget = 30
                }),
                transactionIndex: 5));

        harness.Execute(
            sender: agentAddress.ToHex(),
            methodName: "RecordStep",
            payload: RecordStepInput.Encode(new RecordStepInput
            {
                SessionId = sessionId,
                StepContentHash = "EF".PadLeft(64, '0').ToProtoHash(),
                InputTokens = 10,
                OutputTokens = 5
            }),
            transactionIndex: 6);

        var nonOwnerFinalize = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: "someone-else",
                methodName: "FinalizeSession",
                payload: FinalizeSessionInput.Encode(new FinalizeSessionInput
                {
                    SessionId = sessionId
                }),
                transactionIndex: 7));
        Assert.Contains("not the session owner", nonOwnerFinalize.Message, StringComparison.OrdinalIgnoreCase);

        harness.Execute(
            sender: agentAddress.ToHex(),
            methodName: "FinalizeSession",
            payload: FinalizeSessionInput.Encode(new FinalizeSessionInput
            {
                SessionId = sessionId
            }),
            transactionIndex: 8);

        var sessionState = SessionState.Decode(harness.GetState(CreateSessionKey(sessionId)));
        var finalizedEvent = SessionFinalized.Decode(harness.GetState(CreateFinalizedEventKey(sessionId)));

        Assert.True(sessionState.IsFinalized);
        Assert.Equal(agentAddress.ToHex(), sessionState.FinalizedBy);
        Assert.Equal(harness.BlockHeight, sessionState.FinalizedAtBlockHeight);
        Assert.Equal(15, finalizedEvent.TotalTokensConsumed);
        Assert.Equal(15, finalizedEvent.RemainingBudget);
        Assert.Equal(agentAddress.ToHex(), finalizedEvent.FinalizedBy);

        var finalizedException = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: agentAddress.ToHex(),
                methodName: "RecordStep",
                payload: RecordStepInput.Encode(new RecordStepInput
                {
                    SessionId = sessionId,
                    StepContentHash = "12".PadLeft(64, '0').ToProtoHash(),
                    InputTokens = 1,
                    OutputTokens = 1
                }),
                transactionIndex: 9));
        Assert.Contains("finalized", finalizedException.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Hash CreateExpectedSessionId(Address agentAddress, long blockHeight, int transactionIndex)
    {
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
            Value = Google.Protobuf.ByteString.CopyFrom(SHA256.HashData(payload))
        };
    }

    private static Address CreateAddress(string hex)
    {
        return hex.ToProtoAddress();
    }

    private static string CreateSessionKey(Hash sessionId)
    {
        return $"session:{sessionId.ToHex()}";
    }

    private static string CreateOpenedEventKey(Hash sessionId)
    {
        return $"{CreateSessionKey(sessionId)}:event:opened";
    }

    private static string CreateStepEventKey(Hash sessionId, Hash stepContentHash)
    {
        return $"{CreateSessionKey(sessionId)}:event:step:{stepContentHash.ToHex()}";
    }

    private static string CreateFinalizedEventKey(Hash sessionId)
    {
        return $"{CreateSessionKey(sessionId)}:event:finalized";
    }

    private sealed class ContractHarness : IDisposable
    {
        private readonly TestStateProvider _stateProvider = new();
        private readonly AgentSessionContract _contract = new();
        private readonly SmartContractExecutor _executor = new();

        public long BlockHeight => 42;

        public string ContractAddress => "agent-session-contract";

        public string AdminAddress => $"virtual:{ContractAddress}:agent-session-admin";

        public byte[] Execute(string sender, string methodName, byte[] payload, int transactionIndex)
        {
            return _executor.Execute(
                _contract,
                new ContractInvocation(methodName, payload),
                new ContractExecutionContext(
                    new ContractStateContext(_stateProvider),
                    new ContractCallContext(new TestCallHandler()),
                    new ContractCryptoContext(new TestCryptoProvider()),
                    new ContractIdentityContext(new TestIdentityProvider()),
                    transactionId: $"tx-{transactionIndex}",
                    senderAddress: sender,
                    currentContractAddress: ContractAddress,
                    blockHeight: BlockHeight,
                    blockHash: "block-42",
                    timestamp: new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero),
                    transactionIndex: transactionIndex));
        }

        public byte[] GetState(string key)
        {
            return _stateProvider.GetState(key)
                   ?? throw new InvalidOperationException($"Expected state '{key}' to exist.");
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestStateProvider : IContractStateProvider
    {
        private readonly Dictionary<string, byte[]> _states = new(StringComparer.Ordinal);

        public byte[]? GetState(string key)
        {
            return _states.TryGetValue(key, out var value) ? value : null;
        }

        public void SetState(string key, byte[] value)
        {
            _states[key] = value;
        }

        public void DeleteState(string key)
        {
            _states.Remove(key);
        }

        public bool StateExists(string key)
        {
            return _states.ContainsKey(key);
        }
    }

    private sealed class TestCallHandler : IContractCallHandler
    {
        public byte[] Call(string contractAddress, ContractInvocation invocation)
        {
            return [];
        }

        public void SendInline(string contractAddress, ContractInvocation invocation)
        {
        }
    }

    private sealed class TestCryptoProvider : IContractCryptoProvider
    {
        public byte[] Hash(ReadOnlySpan<byte> input)
        {
            return SHA256.HashData(input.ToArray());
        }

        public bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKey)
        {
            return true;
        }

        public byte[] DeriveVrfProof(ReadOnlySpan<byte> seed)
        {
            return SHA256.HashData(seed.ToArray());
        }
    }

    private sealed class TestIdentityProvider : IContractIdentityProvider
    {
        public string GenerateAddress(string seed)
        {
            return $"addr:{seed}";
        }

        public string GetVirtualAddress(string contractAddress, string salt)
        {
            return $"virtual:{contractAddress}:{salt}";
        }
    }
}
