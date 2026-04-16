using System.Security.Cryptography;

using NightElf.Contracts.System.SentinelRegistry;
using NightElf.Contracts.System.SentinelRegistry.Protobuf;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Kernel.SmartContract;
using NightElf.Sdk.CSharp;

namespace NightElf.Contracts.System.SentinelRegistry.Tests;

public sealed class SentinelRegistryContractTests
{
    [Fact]
    public void RegisterSentinel_Should_Create_State_And_Emit_Event()
    {
        using var harness = new ContractHarness();
        var sender = "owner-address-1";

        harness.Execute(
            sender: sender,
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = "node-1",
                InitialStake = 5000,
                EndpointHost = "192.168.1.1",
                EndpointPort = 8080
            }),
            transactionIndex: 0);

        var sentinelState = SentinelState.Decode(harness.GetState("sentinel:node-1"));
        var registeredEvent = SentinelRegistered.Decode(
            harness.GetState("sentinel:node-1:event:registered"));

        Assert.Equal("node-1", sentinelState.NodeId);
        Assert.Equal(sender, sentinelState.OwnerAddress);
        Assert.Equal(5000, sentinelState.Stake);
        Assert.Equal(0, sentinelState.ComputationCredits);
        Assert.Equal(0, sentinelState.ReputationScore);
        Assert.True(sentinelState.IsActive);
        Assert.Equal(harness.BlockHeight, sentinelState.RegisteredAtBlockHeight);
        Assert.Equal("192.168.1.1", sentinelState.EndpointHost);
        Assert.Equal(8080, sentinelState.EndpointPort);

        Assert.Equal("node-1", registeredEvent.NodeId);
        Assert.Equal(sender, registeredEvent.OwnerAddress);
        Assert.Equal(5000, registeredEvent.Stake);
        Assert.Equal(harness.BlockHeight, registeredEvent.BlockHeight);
    }

    [Fact]
    public void RegisterSentinel_Should_Reject_Duplicate_Registration()
    {
        using var harness = new ContractHarness();
        var sender = "owner-address-2";

        harness.Execute(
            sender: sender,
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = "node-dup",
                InitialStake = 5000,
                EndpointHost = "10.0.0.1",
                EndpointPort = 9090
            }),
            transactionIndex: 0);

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: sender,
                methodName: "RegisterSentinel",
                payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
                {
                    NodeId = "node-dup",
                    InitialStake = 5000,
                    EndpointHost = "10.0.0.1",
                    EndpointPort = 9090
                }),
                transactionIndex: 1));

        Assert.Contains("already registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisterSentinel_Should_Reject_Insufficient_Stake()
    {
        using var harness = new ContractHarness();

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: "owner-low-stake",
                methodName: "RegisterSentinel",
                payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
                {
                    NodeId = "node-low",
                    InitialStake = 100, // below default min of 1000
                    EndpointHost = "10.0.0.1",
                    EndpointPort = 8080
                }),
                transactionIndex: 0));

        Assert.Contains("minimum required stake", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExitSentinel_Should_Mark_Inactive_And_Return_Stake()
    {
        using var harness = new ContractHarness();
        var owner = "owner-exit";

        harness.Execute(
            sender: owner,
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = "node-exit",
                InitialStake = 3000,
                EndpointHost = "10.0.0.2",
                EndpointPort = 8080
            }),
            transactionIndex: 0);

        harness.Execute(
            sender: owner,
            methodName: "ExitSentinel",
            payload: ExitSentinelInput.Encode(new ExitSentinelInput
            {
                NodeId = "node-exit"
            }),
            transactionIndex: 1);

        var sentinelState = SentinelState.Decode(harness.GetState("sentinel:node-exit"));
        var exitedEvent = SentinelExited.Decode(
            harness.GetState("sentinel:node-exit:event:exited"));

        Assert.False(sentinelState.IsActive);
        Assert.Equal(0, sentinelState.Stake);

        Assert.Equal("node-exit", exitedEvent.NodeId);
        Assert.Equal(harness.BlockHeight, exitedEvent.BlockHeight);
        Assert.Equal(3000, exitedEvent.ReturnedStake);
    }

    [Fact]
    public void ExitSentinel_Should_Reject_NonOwner()
    {
        using var harness = new ContractHarness();

        harness.Execute(
            sender: "real-owner",
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = "node-auth",
                InitialStake = 2000,
                EndpointHost = "10.0.0.3",
                EndpointPort = 8080
            }),
            transactionIndex: 0);

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: "not-the-owner",
                methodName: "ExitSentinel",
                payload: ExitSentinelInput.Encode(new ExitSentinelInput
                {
                    NodeId = "node-auth"
                }),
                transactionIndex: 1));

        Assert.Contains("not the sentinel owner", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordComputationCredit_Should_Accumulate_Credits_With_Honest_Attestation()
    {
        using var harness = new ContractHarness();
        var owner = "sentinel-owner-1";
        var sessionId = "AAAA000000000000000000000000000000000000000000000000000000000000".ToProtoHash();

        harness.Execute(
            sender: owner,
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = "compute-node",
                InitialStake = 5000,
                EndpointHost = "10.0.0.4",
                EndpointPort = 8080
            }),
            transactionIndex: 0);

        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "RecordComputationCredit",
            payload: RecordComputationCreditInput.Encode(new RecordComputationCreditInput
            {
                SentinelNodeId = "compute-node",
                SessionId = sessionId,
                WeightedTokensServed = 100,
                AttestationKind = MeteringAttestationKind.Honest
            }),
            transactionIndex: 1);

        var sentinelState = SentinelState.Decode(harness.GetState("sentinel:compute-node"));
        var creditEvent = ComputationCreditRecorded.Decode(
            harness.GetState($"sentinel:compute-node:event:credit:{sessionId.ToHex()}"));

        Assert.Equal(100, sentinelState.ComputationCredits);
        Assert.Equal(100, sentinelState.ReputationScore);
        Assert.Equal(1, sentinelState.SessionsServed);
        Assert.Equal(1, sentinelState.HonestAttestations);
        Assert.Equal(0, sentinelState.PenalizedAttestations);

        Assert.Equal("compute-node", creditEvent.SentinelNodeId);
        Assert.Equal(100, creditEvent.WeightedTokensServed);
        Assert.Equal(100, creditEvent.TotalCredits);
        Assert.Equal(MeteringAttestationKind.Honest, creditEvent.AttestationKind);
    }

    [Fact]
    public void RecordComputationCredit_Should_Apply_Challenged_Reputation_At_Half()
    {
        using var harness = new ContractHarness();
        var sessionId = "BBBB000000000000000000000000000000000000000000000000000000000000".ToProtoHash();

        harness.Execute(
            sender: "sentinel-owner-c",
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = "challenged-node",
                InitialStake = 5000,
                EndpointHost = "10.0.0.5",
                EndpointPort = 8080
            }),
            transactionIndex: 0);

        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "RecordComputationCredit",
            payload: RecordComputationCreditInput.Encode(new RecordComputationCreditInput
            {
                SentinelNodeId = "challenged-node",
                SessionId = sessionId,
                WeightedTokensServed = 200,
                AttestationKind = MeteringAttestationKind.Challenged
            }),
            transactionIndex: 1);

        var sentinelState = SentinelState.Decode(harness.GetState("sentinel:challenged-node"));

        Assert.Equal(200, sentinelState.ComputationCredits);
        Assert.Equal(100, sentinelState.ReputationScore); // 200 / 2
        Assert.Equal(1, sentinelState.HonestAttestations);
        Assert.Equal(0, sentinelState.PenalizedAttestations);
    }

    [Fact]
    public void RecordComputationCredit_Should_Apply_Penalized_Reputation_Negative()
    {
        using var harness = new ContractHarness();
        var honestSession = "CCCC000000000000000000000000000000000000000000000000000000000001".ToProtoHash();
        var penalizedSession = "CCCC000000000000000000000000000000000000000000000000000000000002".ToProtoHash();

        harness.Execute(
            sender: "sentinel-owner-p",
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = "penalized-node",
                InitialStake = 5000,
                EndpointHost = "10.0.0.6",
                EndpointPort = 8080
            }),
            transactionIndex: 0);

        // First, build up some honest reputation
        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "RecordComputationCredit",
            payload: RecordComputationCreditInput.Encode(new RecordComputationCreditInput
            {
                SentinelNodeId = "penalized-node",
                SessionId = honestSession,
                WeightedTokensServed = 500,
                AttestationKind = MeteringAttestationKind.Honest
            }),
            transactionIndex: 1);

        // Now penalize
        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "RecordComputationCredit",
            payload: RecordComputationCreditInput.Encode(new RecordComputationCreditInput
            {
                SentinelNodeId = "penalized-node",
                SessionId = penalizedSession,
                WeightedTokensServed = 100,
                AttestationKind = MeteringAttestationKind.Penalized
            }),
            transactionIndex: 2);

        var sentinelState = SentinelState.Decode(harness.GetState("sentinel:penalized-node"));

        Assert.Equal(600, sentinelState.ComputationCredits); // 500 + 100
        Assert.Equal(300, sentinelState.ReputationScore); // 500 - (100 * 2) = 300
        Assert.Equal(2, sentinelState.SessionsServed);
        Assert.Equal(1, sentinelState.HonestAttestations);
        Assert.Equal(1, sentinelState.PenalizedAttestations);
    }

    [Fact]
    public void RecordComputationCredit_Should_Reject_NonAdmin()
    {
        using var harness = new ContractHarness();

        harness.Execute(
            sender: "owner-x",
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = "admin-only-node",
                InitialStake = 5000,
                EndpointHost = "10.0.0.7",
                EndpointPort = 8080
            }),
            transactionIndex: 0);

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: "not-admin",
                methodName: "RecordComputationCredit",
                payload: RecordComputationCreditInput.Encode(new RecordComputationCreditInput
                {
                    SentinelNodeId = "admin-only-node",
                    SessionId = "DDDD000000000000000000000000000000000000000000000000000000000000".ToProtoHash(),
                    WeightedTokensServed = 50,
                    AttestationKind = MeteringAttestationKind.Honest
                }),
                transactionIndex: 1));

        Assert.Contains("not the contract admin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdvanceEpoch_Should_Select_Top_Sentinels_And_Reset_Credits()
    {
        using var harness = new ContractHarness();

        // Set governance with active_sentinel_count = 2
        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "UpdateGovernance",
            payload: UpdateGovernanceInput.Encode(new UpdateGovernanceInput
            {
                MaxSessionTokenBudget = 1_000_000,
                MaxSessionDurationBlocks = 100,
                MinSentinelStake = 1000,
                ActiveSentinelCount = 2,
                MeteringChallengeWindowBlocks = 50
            }),
            transactionIndex: 0);

        // Register 3 sentinels
        RegisterSentinel(harness, "alpha-owner", "alpha", 5000, 1);
        RegisterSentinel(harness, "beta-owner", "beta", 5000, 2);
        RegisterSentinel(harness, "gamma-owner", "gamma", 5000, 3);

        // Give them different credit/reputation levels (via admin)
        RecordCredit(harness, "alpha", "A000000000000000000000000000000000000000000000000000000000000001".ToProtoHash(), 300, MeteringAttestationKind.Honest, 4);
        RecordCredit(harness, "beta", "B000000000000000000000000000000000000000000000000000000000000001".ToProtoHash(), 100, MeteringAttestationKind.Honest, 5);
        RecordCredit(harness, "gamma", "C000000000000000000000000000000000000000000000000000000000000001".ToProtoHash(), 500, MeteringAttestationKind.Honest, 6);

        // Advance epoch
        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "AdvanceEpoch",
            payload: AdvanceEpochInput.Encode(new AdvanceEpochInput
            {
                EpochNumber = 1
            }),
            transactionIndex: 7);

        var epoch = EpochSnapshot.Decode(harness.GetState("epoch:current"));
        var epochAdvancedEvent = EpochAdvanced.Decode(harness.GetState("epoch:1:event:advanced"));

        // Score = reputation * 7000 + credits * 3000
        // gamma: 500 * 7000 + 500 * 3000 = 5_000_000 (highest)
        // alpha: 300 * 7000 + 300 * 3000 = 3_000_000 (second)
        // beta:  100 * 7000 + 100 * 3000 = 1_000_000 (lowest, excluded)
        Assert.Equal(1, epoch.EpochNumber);
        Assert.Equal(2, epoch.ActiveSentinelNodeIds.Count);
        Assert.Contains("gamma", epoch.ActiveSentinelNodeIds);
        Assert.Contains("alpha", epoch.ActiveSentinelNodeIds);
        Assert.DoesNotContain("beta", epoch.ActiveSentinelNodeIds);
        Assert.Equal(800, epoch.TotalComputationCredits); // 300 + 500
        Assert.Equal(harness.BlockHeight, epoch.StartedAtBlockHeight);

        Assert.Equal(1, epochAdvancedEvent.EpochNumber);
        Assert.Equal(harness.BlockHeight, epochAdvancedEvent.BlockHeight);

        // Verify credits were reset for selected sentinels
        var alphaState = SentinelState.Decode(harness.GetState("sentinel:alpha"));
        var gammaState = SentinelState.Decode(harness.GetState("sentinel:gamma"));
        var betaState = SentinelState.Decode(harness.GetState("sentinel:beta"));

        Assert.Equal(0, alphaState.ComputationCredits);
        Assert.Equal(1, alphaState.LastEpochParticipated);
        Assert.Equal(0, gammaState.ComputationCredits);
        Assert.Equal(1, gammaState.LastEpochParticipated);
        // beta was not selected, so its credits should remain
        Assert.Equal(100, betaState.ComputationCredits);
        Assert.Equal(0, betaState.LastEpochParticipated);
    }

    [Fact]
    public void AdvanceEpoch_Should_Reject_NonAdmin()
    {
        using var harness = new ContractHarness();

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: "random-caller",
                methodName: "AdvanceEpoch",
                payload: AdvanceEpochInput.Encode(new AdvanceEpochInput
                {
                    EpochNumber = 1
                }),
                transactionIndex: 0));

        Assert.Contains("not the contract admin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdvanceEpoch_Should_Reject_Backward_Epoch_Number()
    {
        using var harness = new ContractHarness();

        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "AdvanceEpoch",
            payload: AdvanceEpochInput.Encode(new AdvanceEpochInput
            {
                EpochNumber = 5
            }),
            transactionIndex: 0);

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: harness.AdminAddress,
                methodName: "AdvanceEpoch",
                payload: AdvanceEpochInput.Encode(new AdvanceEpochInput
                {
                    EpochNumber = 3
                }),
                transactionIndex: 1));

        Assert.Contains("must be greater", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpdateGovernance_Should_Store_Parameters()
    {
        using var harness = new ContractHarness();

        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "UpdateGovernance",
            payload: UpdateGovernanceInput.Encode(new UpdateGovernanceInput
            {
                MaxSessionTokenBudget = 2_000_000,
                MaxSessionDurationBlocks = 200,
                MinSentinelStake = 5000,
                ActiveSentinelCount = 11,
                MeteringChallengeWindowBlocks = 100
            }),
            transactionIndex: 0);

        var governance = GovernanceParameters.Decode(
            harness.GetState("governance:parameters"));

        Assert.Equal(2_000_000, governance.MaxSessionTokenBudget);
        Assert.Equal(200, governance.MaxSessionDurationBlocks);
        Assert.Equal(5000, governance.MinSentinelStake);
        Assert.Equal(11, governance.ActiveSentinelCount);
        Assert.Equal(100, governance.MeteringChallengeWindowBlocks);
    }

    [Fact]
    public void UpdateGovernance_Should_Reject_NonAdmin()
    {
        using var harness = new ContractHarness();

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: "not-admin",
                methodName: "UpdateGovernance",
                payload: UpdateGovernanceInput.Encode(new UpdateGovernanceInput
                {
                    MaxSessionTokenBudget = 100,
                    MaxSessionDurationBlocks = 10,
                    MinSentinelStake = 1,
                    ActiveSentinelCount = 1,
                    MeteringChallengeWindowBlocks = 1
                }),
                transactionIndex: 0));

        Assert.Contains("not the contract admin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSentinel_Should_Return_State()
    {
        using var harness = new ContractHarness();

        harness.Execute(
            sender: "query-owner",
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = "query-node",
                InitialStake = 2000,
                EndpointHost = "10.0.0.10",
                EndpointPort = 8080
            }),
            transactionIndex: 0);

        var resultBytes = harness.Execute(
            sender: "anyone",
            methodName: "GetSentinel",
            payload: GetSentinelInput.Encode(new GetSentinelInput
            {
                NodeId = "query-node"
            }),
            transactionIndex: 1);

        var sentinelState = SentinelState.Decode(resultBytes);

        Assert.Equal("query-node", sentinelState.NodeId);
        Assert.Equal("query-owner", sentinelState.OwnerAddress);
        Assert.Equal(2000, sentinelState.Stake);
        Assert.True(sentinelState.IsActive);
    }

    [Fact]
    public void GetGovernanceParameters_Should_Return_Defaults_When_Not_Set()
    {
        using var harness = new ContractHarness();

        var resultBytes = harness.Execute(
            sender: "anyone",
            methodName: "GetGovernanceParameters",
            payload: Empty.Encode(Empty.Value),
            transactionIndex: 0);

        var governance = GovernanceParameters.Decode(resultBytes);

        Assert.Equal(1_000_000, governance.MaxSessionTokenBudget);
        Assert.Equal(100, governance.MaxSessionDurationBlocks);
        Assert.Equal(1000, governance.MinSentinelStake);
        Assert.Equal(21, governance.ActiveSentinelCount);
        Assert.Equal(50, governance.MeteringChallengeWindowBlocks);
    }

    [Fact]
    public void GetCurrentEpoch_Should_Throw_When_No_Epoch_Exists()
    {
        using var harness = new ContractHarness();

        var exception = Assert.Throws<InvalidOperationException>(
            () => harness.Execute(
                sender: "anyone",
                methodName: "GetCurrentEpoch",
                payload: Empty.Encode(Empty.Value),
                transactionIndex: 0));

        Assert.Contains("No epoch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────
    // Test helpers
    // ────────────────────────────────────────────

    private static void RegisterSentinel(ContractHarness harness, string owner, string nodeId, long stake, int txIndex)
    {
        harness.Execute(
            sender: owner,
            methodName: "RegisterSentinel",
            payload: RegisterSentinelInput.Encode(new RegisterSentinelInput
            {
                NodeId = nodeId,
                InitialStake = stake,
                EndpointHost = $"host-{nodeId}",
                EndpointPort = 8080
            }),
            transactionIndex: txIndex);
    }

    private static void RecordCredit(
        ContractHarness harness, string nodeId, Hash sessionId, long tokens,
        MeteringAttestationKind kind, int txIndex)
    {
        harness.Execute(
            sender: harness.AdminAddress,
            methodName: "RecordComputationCredit",
            payload: RecordComputationCreditInput.Encode(new RecordComputationCreditInput
            {
                SentinelNodeId = nodeId,
                SessionId = sessionId,
                WeightedTokensServed = tokens,
                AttestationKind = kind
            }),
            transactionIndex: txIndex);
    }

    // ────────────────────────────────────────────
    // ContractHarness (mirrors AgentSession test pattern)
    // ────────────────────────────────────────────

    internal sealed class ContractHarness : IDisposable
    {
        private readonly TestStateProvider _stateProvider = new();
        private readonly SentinelRegistryContract _contract = new();
        private readonly SmartContractExecutor _executor = new();

        public long BlockHeight => 42;

        public string ContractAddress => "sentinel-registry-contract";

        public string AdminAddress => $"virtual:{ContractAddress}:sentinel-registry-admin";

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
