using System.Security.Cryptography;
using System.Text;
using System.Linq;

using Google.Protobuf;

using NightElf.Contracts.System.Treaty;
using NightElf.Contracts.System.Treaty.Protobuf;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Kernel.SmartContract;
using NightElf.Sdk.CSharp;

namespace NightElf.Contracts.System.Treaty.Tests;

public sealed class TreatyContractTests
{
    [Fact]
    public void CreateTreaty_Should_Create_State_Event_And_Queryable_Permission_Matrix()
    {
        using var harness = new ContractHarness();
        var leader = CreateAddress("11".PadLeft(64, '1'));
        var worker = CreateAddress("22".PadLeft(64, '2'));
        var treatyId = Hash.Decode(
            harness.Execute(
                sender: leader.ToHex(),
                methodName: "CreateTreaty",
                payload: CreateTreatyInput.Encode(new CreateTreatyInput
                {
                    Spec = CreateSpec(leader, worker, challengeWindowBlocks: 9)
                }),
                transactionIndex: 3));

        var treatyState = TreatyState.Decode(harness.GetState(CreateTreatyKey(treatyId)));
        var createdEvent = TreatyCreated.Decode(harness.GetState(CreateCreatedEventKey(treatyId)));
        var permissionMatrix = TreatyPermissionMatrix.Decode(
            harness.Execute(
                sender: leader.ToHex(),
                methodName: "GetPermissionMatrix",
                payload: GetTreatyInput.Encode(new GetTreatyInput
                {
                    TreatyId = treatyId
                }),
                transactionIndex: 4));

        Assert.Equal(TreatyStatus.PendingParticipants, treatyState.Status);
        Assert.Equal(leader.ToHex(), treatyState.CreatedBy);
        Assert.Equal(harness.BlockHeight, treatyState.CreatedAtBlockHeight);
        Assert.Equal(1, treatyState.Settlement.JoinedParticipants);
        Assert.True(treatyState.ParticipantStates.Single(state => state.AgentAddress.ToHex() == leader.ToHex()).HasJoined);
        Assert.False(treatyState.ParticipantStates.Single(state => state.AgentAddress.ToHex() == worker.ToHex()).HasJoined);
        Assert.Equal(treatyId.ToHex(), createdEvent.TreatyId.ToHex());
        Assert.Equal(2, createdEvent.ParticipantCount);
        Assert.Equal(9, createdEvent.ChallengeWindowBlocks);
        Assert.Equal(2, permissionMatrix.Grants.Count);
        Assert.Contains(permissionMatrix.Grants, grant => grant.AgentAddress.ToHex() == leader.ToHex() &&
                                                         grant.ContractName == "AgentSession" &&
                                                         grant.MethodName == "RecordStep");
    }

    [Fact]
    public void JoinTreaty_Should_Verify_Signature_And_Activate_Treaty()
    {
        using var harness = new ContractHarness();
        var leader = CreateAddress("33".PadLeft(64, '3'));
        var worker = CreateAddress("44".PadLeft(64, '4'));
        var treatyId = CreateTreaty(harness, leader, worker, transactionIndex: 0);
        var signature = CreateJoinSignature(worker.ToHex(), treatyId, worker);

        harness.Execute(
            sender: worker.ToHex(),
            methodName: "JoinTreaty",
            payload: JoinTreatyInput.Encode(new JoinTreatyInput
            {
                TreatyId = treatyId,
                Signature = ByteString.CopyFrom(signature)
            }),
            transactionIndex: 1);

        var treatyState = TreatyState.Decode(harness.GetState(CreateTreatyKey(treatyId)));
        var joinedEvent = TreatyJoined.Decode(harness.GetState(CreateJoinedEventKey(treatyId, worker)));

        Assert.Equal(TreatyStatus.Active, treatyState.Status);
        Assert.Equal(2, treatyState.Settlement.JoinedParticipants);
        Assert.True(treatyState.ParticipantStates.All(state => state.HasJoined));
        Assert.Equal(worker.ToHex(), joinedEvent.ParticipantAddress.ToHex());
        Assert.Equal(TreatyRole.Worker, joinedEvent.Role);

        var invalidSignature = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: worker.ToHex(),
                methodName: "JoinTreaty",
                payload: JoinTreatyInput.Encode(new JoinTreatyInput
                {
                    TreatyId = CreateTreaty(harness, CreateAddress("55".PadLeft(64, '5')), worker, transactionIndex: 2),
                    Signature = ByteString.CopyFromUtf8("invalid")
                }),
                transactionIndex: 3));
        Assert.Contains("invalid", invalidSignature.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordActivity_Should_Enforce_Permission_Matrix_And_Associate_Session()
    {
        using var harness = new ContractHarness();
        var leader = CreateAddress("66".PadLeft(64, '6'));
        var worker = CreateAddress("77".PadLeft(64, '7'));
        var treatyId = CreateAndActivateTreaty(harness, leader, worker);
        var firstSessionId = "AA".PadLeft(64, 'A').ToProtoHash();
        var firstStep = "AB".PadLeft(64, 'B').ToProtoHash();
        var secondStep = "AC".PadLeft(64, 'C').ToProtoHash();

        harness.Execute(
            sender: leader.ToHex(),
            methodName: "RecordActivity",
            payload: RecordActivityInput.Encode(new RecordActivityInput
            {
                TreatyId = treatyId,
                Action = new TreatyActivity
                {
                    StepHash = firstStep,
                    ContractName = "AgentSession",
                    MethodName = "RecordStep",
                    AgentSessionId = firstSessionId,
                    Description = "leader-recorded-step"
                }
            }),
            transactionIndex: 2);

        harness.Execute(
            sender: leader.ToHex(),
            methodName: "RecordActivity",
            payload: RecordActivityInput.Encode(new RecordActivityInput
            {
                TreatyId = treatyId,
                Action = new TreatyActivity
                {
                    StepHash = secondStep,
                    ContractName = "AgentSession",
                    MethodName = "RecordStep",
                    Description = "leader-follow-up"
                }
            }),
            transactionIndex: 3);

        var treatyState = TreatyState.Decode(harness.GetState(CreateTreatyKey(treatyId)));
        var secondActivity = TreatyRecordedActivity.Decode(harness.GetState(CreateActivityKey(treatyId, secondStep)));

        Assert.Equal(2, treatyState.ActivityCount);
        Assert.Single(treatyState.SessionBindings);
        Assert.Equal(firstSessionId.ToHex(), treatyState.SessionBindings[0].SessionId.ToHex());
        Assert.Equal(firstSessionId.ToHex(), secondActivity.AssociatedSessionId.ToHex());

        var unauthorized = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: worker.ToHex(),
                methodName: "RecordActivity",
                payload: RecordActivityInput.Encode(new RecordActivityInput
                {
                    TreatyId = treatyId,
                    Action = new TreatyActivity
                    {
                        StepHash = "AD".PadLeft(64, 'D').ToProtoHash(),
                        ContractName = "AgentSession",
                        MethodName = "FinalizeSession",
                        AgentSessionId = "AE".PadLeft(64, 'E').ToProtoHash()
                    }
                }),
                transactionIndex: 4));
        Assert.Contains("not allowed", unauthorized.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Challenge_Kill_And_Finalize_Should_Update_Status_And_Emit_Events()
    {
        using var harness = new ContractHarness();
        var leader = CreateAddress("88".PadLeft(64, '8'));
        var worker = CreateAddress("99".PadLeft(64, '9'));
        var treatyId = CreateAndActivateTreaty(harness, leader, worker);
        var stepHash = "BA".PadLeft(64, 'A').ToProtoHash();

        harness.Execute(
            sender: leader.ToHex(),
            methodName: "RecordActivity",
            payload: RecordActivityInput.Encode(new RecordActivityInput
            {
                TreatyId = treatyId,
                Action = new TreatyActivity
                {
                    StepHash = stepHash,
                    ContractName = "AgentSession",
                    MethodName = "RecordStep",
                    AgentSessionId = "BB".PadLeft(64, 'B').ToProtoHash(),
                    Description = "challenge-target"
                }
            }),
            transactionIndex: 2);

        harness.Execute(
            sender: worker.ToHex(),
            methodName: "ChallengeStep",
            payload: ChallengeStepInput.Encode(new ChallengeStepInput
            {
                TreatyId = treatyId,
                StepHash = stepHash,
                Reason = "validator-dispute"
            }),
            transactionIndex: 3);

        var challengedEvent = StepChallenged.Decode(harness.GetState(CreateChallengedEventKey(treatyId, stepHash, worker)));
        var settlement = TreatySettlement.Decode(
            harness.Execute(
                sender: leader.ToHex(),
                methodName: "FinalizeTreaty",
                payload: FinalizeTreatyInput.Encode(new FinalizeTreatyInput
                {
                    TreatyId = treatyId
                }),
                transactionIndex: 4));
        var finalizedEvent = TreatyFinalized.Decode(harness.GetState(CreateFinalizedEventKey(treatyId)));

        Assert.Equal(worker.ToHex(), challengedEvent.ChallengerAddress.ToHex());
        Assert.Equal(TreatySettlementOutcome.Disputed, settlement.Outcome);
        Assert.Equal(TreatySettlementOutcome.Disputed, finalizedEvent.Outcome);

        var killedTreatyId = CreateAndActivateTreaty(harness, CreateAddress("AA".PadLeft(64, 'A')), CreateAddress("CC".PadLeft(64, 'C')), baseTransactionIndex: 10);
        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "TriggerKillSwitch",
            payload: TriggerKillSwitchInput.Encode(new TriggerKillSwitchInput
            {
                TreatyId = killedTreatyId,
                Reason = "emergency-stop"
            }),
            transactionIndex: 12);

        var killedSettlement = TreatySettlement.Decode(
            harness.Execute(
                sender: harness.AdminAddress,
                methodName: "FinalizeTreaty",
                payload: FinalizeTreatyInput.Encode(new FinalizeTreatyInput
                {
                    TreatyId = killedTreatyId
                }),
                transactionIndex: 13));
        var killEvent = KillSwitchTriggered.Decode(harness.GetState(CreateKillEventKey(killedTreatyId)));

        Assert.Equal("emergency-stop", killEvent.Reason);
        Assert.Equal(harness.AdminAddress, killEvent.TriggeredBy);
        Assert.Equal(TreatySettlementOutcome.Killed, killedSettlement.Outcome);
    }

    [Fact]
    public void ChallengeStep_Should_Reject_When_Window_Closes()
    {
        using var harness = new ContractHarness();
        var leader = CreateAddress("DD".PadLeft(64, 'D'));
        var worker = CreateAddress("EE".PadLeft(64, 'E'));
        var treatyId = CreateAndActivateTreaty(harness, leader, worker, challengeWindowBlocks: 2);
        var stepHash = "CA".PadLeft(64, 'A').ToProtoHash();

        harness.Execute(
            sender: leader.ToHex(),
            methodName: "RecordActivity",
            payload: RecordActivityInput.Encode(new RecordActivityInput
            {
                TreatyId = treatyId,
                Action = new TreatyActivity
                {
                    StepHash = stepHash,
                    ContractName = "AgentSession",
                    MethodName = "RecordStep",
                    AgentSessionId = "CB".PadLeft(64, 'B').ToProtoHash()
                }
            }),
            blockHeight: 42,
            transactionIndex: 2);

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: worker.ToHex(),
                methodName: "ChallengeStep",
                payload: ChallengeStepInput.Encode(new ChallengeStepInput
                {
                    TreatyId = treatyId,
                    StepHash = stepHash,
                    Reason = "too-late"
                }),
                blockHeight: 45,
                transactionIndex: 3));
        Assert.Contains("closed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Hash CreateTreaty(ContractHarness harness, Address leader, Address worker, int transactionIndex, int challengeWindowBlocks = 8)
    {
        return Hash.Decode(
            harness.Execute(
                sender: leader.ToHex(),
                methodName: "CreateTreaty",
                payload: CreateTreatyInput.Encode(new CreateTreatyInput
                {
                    Spec = CreateSpec(leader, worker, challengeWindowBlocks)
                }),
                transactionIndex: transactionIndex));
    }

    private static Hash CreateAndActivateTreaty(
        ContractHarness harness,
        Address leader,
        Address worker,
        int challengeWindowBlocks = 8,
        int baseTransactionIndex = 0)
    {
        var treatyId = CreateTreaty(harness, leader, worker, baseTransactionIndex, challengeWindowBlocks);
        harness.Execute(
            sender: worker.ToHex(),
            methodName: "JoinTreaty",
            payload: JoinTreatyInput.Encode(new JoinTreatyInput
            {
                TreatyId = treatyId,
                Signature = ByteString.CopyFrom(CreateJoinSignature(worker.ToHex(), treatyId, worker))
            }),
            transactionIndex: baseTransactionIndex + 1);

        return treatyId;
    }

    private static TreatySpec CreateSpec(Address leader, Address worker, int challengeWindowBlocks)
    {
        var spec = new TreatySpec
        {
            ChallengeWindowBlocks = challengeWindowBlocks,
            PermissionMatrix = new TreatyPermissionMatrix()
        };
        spec.Participants.Add(new TreatyParticipant
        {
            AgentAddress = leader,
            Role = TreatyRole.Leader
        });
        spec.Participants.Add(new TreatyParticipant
        {
            AgentAddress = worker,
            Role = TreatyRole.Worker
        });
        spec.RoleBudgets.Add(new RoleBudget
        {
            Role = TreatyRole.Leader,
            TokenLimit = 1000
        });
        spec.RoleBudgets.Add(new RoleBudget
        {
            Role = TreatyRole.Worker,
            TokenLimit = 500
        });
        spec.PermissionMatrix.Grants.Add(new PermissionGrant
        {
            AgentAddress = leader,
            ContractName = "AgentSession",
            MethodName = "RecordStep"
        });
        spec.PermissionMatrix.Grants.Add(new PermissionGrant
        {
            AgentAddress = worker,
            ContractName = "AgentSession",
            MethodName = "OpenSession"
        });
        return spec;
    }

    private static byte[] CreateJoinSignature(string senderAddress, Hash treatyId, Address participantAddress)
    {
        var joinPayload = CreateJoinPayloadHash(treatyId, participantAddress);
        var publicKeyBytes = Encoding.UTF8.GetBytes(senderAddress);
        var payload = new byte[joinPayload.Length + publicKeyBytes.Length];
        joinPayload.CopyTo(payload, 0);
        publicKeyBytes.CopyTo(payload, joinPayload.Length);
        return SHA256.HashData(payload);
    }

    private static byte[] CreateJoinPayloadHash(Hash treatyId, Address participantAddress)
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

    private static Address CreateAddress(string hex) => hex.ToProtoAddress();

    private static string CreateTreatyKey(Hash treatyId) => $"treaty:{treatyId.ToHex()}";

    private static string CreateCreatedEventKey(Hash treatyId) => $"{CreateTreatyKey(treatyId)}:event:created";

    private static string CreateJoinedEventKey(Hash treatyId, Address participantAddress) =>
        $"{CreateTreatyKey(treatyId)}:event:joined:{participantAddress.ToHex()}";

    private static string CreateActivityKey(Hash treatyId, Hash stepHash) =>
        $"{CreateTreatyKey(treatyId)}:activity:{stepHash.ToHex()}";

    private static string CreateChallengedEventKey(Hash treatyId, Hash stepHash, Address challengerAddress) =>
        $"{CreateTreatyKey(treatyId)}:event:challenge:{stepHash.ToHex()}:{challengerAddress.ToHex()}";

    private static string CreateKillEventKey(Hash treatyId) => $"{CreateTreatyKey(treatyId)}:event:kill-switch";

    private static string CreateFinalizedEventKey(Hash treatyId) => $"{CreateTreatyKey(treatyId)}:event:finalized";

    private sealed class ContractHarness : IDisposable
    {
        private readonly TestStateProvider _stateProvider = new();
        private readonly TreatyContract _contract = new();
        private readonly SmartContractExecutor _executor = new();

        public long BlockHeight => 42;

        public string ContractAddress => "treaty-contract";

        public string AdminAddress => $"virtual:{ContractAddress}:treaty-admin";

        public byte[] Execute(
            string sender,
            string methodName,
            byte[] payload,
            int transactionIndex,
            long? blockHeight = null)
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
                    blockHeight: blockHeight ?? BlockHeight,
                    blockHash: "block-42",
                    timestamp: new DateTimeOffset(2026, 4, 17, 0, 0, 0, TimeSpan.Zero),
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
            var publicKeyBytes = Encoding.UTF8.GetBytes(publicKey);
            var payload = new byte[data.Length + publicKeyBytes.Length];
            data.ToArray().CopyTo(payload, 0);
            publicKeyBytes.CopyTo(payload, data.Length);
            return signature.SequenceEqual(SHA256.HashData(payload));
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
