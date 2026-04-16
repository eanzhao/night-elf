using Google.Protobuf;

using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Sdk.CSharp;

using SentinelProto = NightElf.Contracts.System.SentinelRegistry.Protobuf;

namespace NightElf.Contracts.System.SentinelRegistry;

public sealed partial class SentinelRegistryContract : CSharpSmartContract
{
    private const string AdminSalt = "sentinel-registry-admin";
    private const string SentinelKeyPrefix = "sentinel:";
    private const string EpochKeyPrefix = "epoch:";
    private const string CurrentEpochKey = "epoch:current";
    private const string GovernanceKey = "governance:parameters";
    private const string SentinelListKey = "sentinel:list";

    // ────────────────────────────────────────────
    // State-mutating methods
    // ────────────────────────────────────────────

    [ContractMethod(
        ReadExtractor = nameof(GetRegisterSentinelReadKeys),
        WriteExtractor = nameof(GetRegisterSentinelWriteKeys))]
    public Empty RegisterSentinel(SentinelProto.RegisterSentinelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.NodeId, nameof(input.NodeId));
        ArgumentException.ThrowIfNullOrWhiteSpace(input.EndpointHost, nameof(input.EndpointHost));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.InitialStake);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.EndpointPort);

        var governance = LoadGovernanceOrDefault();
        if (input.InitialStake < governance.MinSentinelStake)
        {
            throw new InvalidOperationException(
                $"Initial stake {input.InitialStake} is below the minimum required stake of {governance.MinSentinelStake}.");
        }

        var sentinelKey = CreateSentinelKey(input.NodeId);
        if (StateContext.StateExists(sentinelKey))
        {
            throw new InvalidOperationException($"Sentinel '{input.NodeId}' is already registered.");
        }

        var sentinelState = new SentinelProto.SentinelState
        {
            NodeId = input.NodeId,
            OwnerAddress = ExecutionContext.SenderAddress,
            Stake = input.InitialStake,
            ComputationCredits = 0,
            ReputationScore = 0,
            SessionsServed = 0,
            HonestAttestations = 0,
            PenalizedAttestations = 0,
            IsActive = true,
            RegisteredAtBlockHeight = ExecutionContext.BlockHeight,
            LastEpochParticipated = 0,
            EndpointHost = input.EndpointHost,
            EndpointPort = input.EndpointPort
        };

        StateContext.SetState(sentinelKey, sentinelState.ToByteArray());
        AddToSentinelList(input.NodeId);

        StateContext.SetState(
            CreateSentinelRegisteredEventKey(input.NodeId),
            new SentinelProto.SentinelRegistered
            {
                NodeId = input.NodeId,
                OwnerAddress = ExecutionContext.SenderAddress,
                Stake = input.InitialStake,
                BlockHeight = ExecutionContext.BlockHeight
            }.ToByteArray());

        return Empty.Value;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetExitSentinelReadKeys),
        WriteExtractor = nameof(GetExitSentinelWriteKeys))]
    public Empty ExitSentinel(SentinelProto.ExitSentinelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.NodeId, nameof(input.NodeId));

        var sentinelKey = CreateSentinelKey(input.NodeId);
        var sentinelState = LoadSentinel(sentinelKey, input.NodeId);
        EnsureSentinelOwner(sentinelState);

        if (!sentinelState.IsActive)
        {
            throw new InvalidOperationException($"Sentinel '{input.NodeId}' has already exited.");
        }

        var returnedStake = sentinelState.Stake;
        sentinelState.IsActive = false;
        sentinelState.Stake = 0;

        StateContext.SetState(sentinelKey, sentinelState.ToByteArray());
        RemoveFromSentinelList(input.NodeId);

        StateContext.SetState(
            CreateSentinelExitedEventKey(input.NodeId),
            new SentinelProto.SentinelExited
            {
                NodeId = input.NodeId,
                BlockHeight = ExecutionContext.BlockHeight,
                ReturnedStake = returnedStake
            }.ToByteArray());

        return Empty.Value;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetRecordComputationCreditReadKeys),
        WriteExtractor = nameof(GetRecordComputationCreditWriteKeys))]
    public Empty RecordComputationCredit(SentinelProto.RecordComputationCreditInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SentinelNodeId, nameof(input.SentinelNodeId));
        EnsureHash(input.SessionId, nameof(input.SessionId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.WeightedTokensServed);
        EnsureAttestationKindSpecified(input.AttestationKind);

        EnsureAdmin();

        var sentinelKey = CreateSentinelKey(input.SentinelNodeId);
        var sentinelState = LoadSentinel(sentinelKey, input.SentinelNodeId);

        if (!sentinelState.IsActive)
        {
            throw new InvalidOperationException($"Sentinel '{input.SentinelNodeId}' is not active.");
        }

        sentinelState.ComputationCredits = checked(sentinelState.ComputationCredits + input.WeightedTokensServed);
        sentinelState.SessionsServed = checked(sentinelState.SessionsServed + 1);

        switch (input.AttestationKind)
        {
            case SentinelProto.MeteringAttestationKind.Honest:
                sentinelState.ReputationScore = checked(sentinelState.ReputationScore + input.WeightedTokensServed);
                sentinelState.HonestAttestations = checked(sentinelState.HonestAttestations + 1);
                break;
            case SentinelProto.MeteringAttestationKind.Challenged:
                sentinelState.ReputationScore = checked(sentinelState.ReputationScore + input.WeightedTokensServed / 2);
                sentinelState.HonestAttestations = checked(sentinelState.HonestAttestations + 1);
                break;
            case SentinelProto.MeteringAttestationKind.Penalized:
                sentinelState.ReputationScore = checked(sentinelState.ReputationScore - input.WeightedTokensServed * 2);
                sentinelState.PenalizedAttestations = checked(sentinelState.PenalizedAttestations + 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(input.AttestationKind), input.AttestationKind, null);
        }

        StateContext.SetState(sentinelKey, sentinelState.ToByteArray());

        StateContext.SetState(
            CreateComputationCreditEventKey(input.SentinelNodeId, input.SessionId),
            new SentinelProto.ComputationCreditRecorded
            {
                SentinelNodeId = input.SentinelNodeId,
                SessionId = input.SessionId,
                WeightedTokensServed = input.WeightedTokensServed,
                TotalCredits = sentinelState.ComputationCredits,
                AttestationKind = input.AttestationKind
            }.ToByteArray());

        return Empty.Value;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetAdvanceEpochReadKeys),
        WriteExtractor = nameof(GetAdvanceEpochWriteKeys))]
    public Empty AdvanceEpoch(SentinelProto.AdvanceEpochInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.EpochNumber);

        EnsureAdmin();

        var currentEpochBytes = StateContext.GetState(CurrentEpochKey);
        if (currentEpochBytes is not null)
        {
            var currentEpoch = SentinelProto.EpochSnapshot.Parser.ParseFrom(currentEpochBytes);
            if (input.EpochNumber <= currentEpoch.EpochNumber)
            {
                throw new InvalidOperationException(
                    $"Epoch number {input.EpochNumber} must be greater than current epoch {currentEpoch.EpochNumber}.");
            }
        }

        var governance = LoadGovernanceOrDefault();
        var activeSentinelCount = governance.ActiveSentinelCount > 0 ? governance.ActiveSentinelCount : 21;

        var sentinelNodeIds = LoadSentinelList();
        var scoredSentinels = new List<(string NodeId, long Score)>();

        foreach (var nodeId in sentinelNodeIds)
        {
            var sentinelKey = CreateSentinelKey(nodeId);
            var sentinelBytes = StateContext.GetState(sentinelKey);
            if (sentinelBytes is null)
            {
                continue;
            }

            var sentinel = SentinelProto.SentinelState.Parser.ParseFrom(sentinelBytes);
            if (!sentinel.IsActive)
            {
                continue;
            }

            // Score = reputation_score * 0.7 + computation_credits * 0.3
            // Using basis points to avoid floating point: score = reputation * 7000 + credits * 3000
            var score = checked(sentinel.ReputationScore * 7000 + sentinel.ComputationCredits * 3000);
            scoredSentinels.Add((nodeId, score));
        }

        // Sort descending by score, then by nodeId for deterministic tie-breaking
        scoredSentinels.Sort((a, b) =>
        {
            var scoreComparison = b.Score.CompareTo(a.Score);
            return scoreComparison != 0
                ? scoreComparison
                : StringComparer.Ordinal.Compare(a.NodeId, b.NodeId);
        });

        var selectedNodeIds = new List<string>();
        long totalCredits = 0;

        foreach (var (nodeId, _) in scoredSentinels.Take(activeSentinelCount))
        {
            selectedNodeIds.Add(nodeId);

            var sentinelKey = CreateSentinelKey(nodeId);
            var sentinelBytes = StateContext.GetState(sentinelKey);
            if (sentinelBytes is null)
            {
                continue;
            }

            var sentinel = SentinelProto.SentinelState.Parser.ParseFrom(sentinelBytes);
            totalCredits = checked(totalCredits + sentinel.ComputationCredits);
            sentinel.LastEpochParticipated = input.EpochNumber;
            sentinel.ComputationCredits = 0;
            StateContext.SetState(sentinelKey, sentinel.ToByteArray());
        }

        var epochSnapshot = new SentinelProto.EpochSnapshot
        {
            EpochNumber = input.EpochNumber,
            TotalComputationCredits = totalCredits,
            StartedAtBlockHeight = ExecutionContext.BlockHeight
        };
        epochSnapshot.ActiveSentinelNodeIds.AddRange(selectedNodeIds);

        var epochKey = CreateEpochKey(input.EpochNumber);
        StateContext.SetState(epochKey, epochSnapshot.ToByteArray());
        StateContext.SetState(CurrentEpochKey, epochSnapshot.ToByteArray());

        StateContext.SetState(
            CreateEpochAdvancedEventKey(input.EpochNumber),
            new SentinelProto.EpochAdvanced
            {
                EpochNumber = input.EpochNumber,
                BlockHeight = ExecutionContext.BlockHeight,
                ActiveSentinelNodeIds = { selectedNodeIds }
            }.ToByteArray());

        return Empty.Value;
    }

    [ContractMethod(
        ReadExtractor = nameof(GetUpdateGovernanceReadKeys),
        WriteExtractor = nameof(GetUpdateGovernanceWriteKeys))]
    public Empty UpdateGovernance(SentinelProto.UpdateGovernanceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        EnsureAdmin();

        var governance = new SentinelProto.GovernanceParameters
        {
            MaxSessionTokenBudget = input.MaxSessionTokenBudget,
            MaxSessionDurationBlocks = input.MaxSessionDurationBlocks,
            MinSentinelStake = input.MinSentinelStake,
            ActiveSentinelCount = input.ActiveSentinelCount,
            MeteringChallengeWindowBlocks = input.MeteringChallengeWindowBlocks
        };

        StateContext.SetState(GovernanceKey, governance.ToByteArray());

        return Empty.Value;
    }

    // ────────────────────────────────────────────
    // Query methods (read-only)
    // ────────────────────────────────────────────

    [ContractMethod]
    public SentinelProto.SentinelState GetSentinel(SentinelProto.GetSentinelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.NodeId, nameof(input.NodeId));

        var sentinelKey = CreateSentinelKey(input.NodeId);
        return LoadSentinel(sentinelKey, input.NodeId);
    }

    [ContractMethod]
    public SentinelProto.EpochSnapshot GetCurrentEpoch(Empty input)
    {
        var epochBytes = StateContext.GetState(CurrentEpochKey);
        if (epochBytes is null)
        {
            throw new InvalidOperationException("No epoch has been advanced yet.");
        }

        return SentinelProto.EpochSnapshot.Parser.ParseFrom(epochBytes);
    }

    [ContractMethod]
    public SentinelProto.GovernanceParameters GetGovernanceParameters(Empty input)
    {
        return LoadGovernanceOrDefault();
    }

    // ────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────

    private SentinelProto.SentinelState LoadSentinel(string sentinelKey, string nodeId)
    {
        var sentinelBytes = StateContext.GetState(sentinelKey);
        if (sentinelBytes is null)
        {
            throw new InvalidOperationException($"Sentinel '{nodeId}' was not found.");
        }

        return SentinelProto.SentinelState.Parser.ParseFrom(sentinelBytes);
    }

    private SentinelProto.GovernanceParameters LoadGovernanceOrDefault()
    {
        var governanceBytes = StateContext.GetState(GovernanceKey);
        if (governanceBytes is not null)
        {
            return SentinelProto.GovernanceParameters.Parser.ParseFrom(governanceBytes);
        }

        return new SentinelProto.GovernanceParameters
        {
            MaxSessionTokenBudget = 1_000_000,
            MaxSessionDurationBlocks = 100,
            MinSentinelStake = 1000,
            ActiveSentinelCount = 21,
            MeteringChallengeWindowBlocks = 50
        };
    }

    private void EnsureAdmin()
    {
        var adminAddress = IdentityContext.GetVirtualAddress(ExecutionContext.CurrentContractAddress, AdminSalt);
        if (!StringComparer.OrdinalIgnoreCase.Equals(ExecutionContext.SenderAddress, adminAddress))
        {
            throw new InvalidOperationException(
                $"Sender '{ExecutionContext.SenderAddress}' is not the contract admin.");
        }
    }

    private void EnsureSentinelOwner(SentinelProto.SentinelState sentinelState)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(ExecutionContext.SenderAddress, sentinelState.OwnerAddress))
        {
            throw new InvalidOperationException(
                $"Sender '{ExecutionContext.SenderAddress}' is not the sentinel owner '{sentinelState.OwnerAddress}'.");
        }
    }

    private static void EnsureHash(Hash? hash, string parameterName)
    {
        if (hash is null || hash.Value.IsEmpty)
        {
            throw new ArgumentException("Hash payload must not be empty.", parameterName);
        }
    }

    private static void EnsureAttestationKindSpecified(SentinelProto.MeteringAttestationKind kind)
    {
        if (kind == SentinelProto.MeteringAttestationKind.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "MeteringAttestationKind must be specified.");
        }
    }

    private List<string> LoadSentinelList()
    {
        var listBytes = StateContext.GetState(SentinelListKey);
        if (listBytes is null)
        {
            return [];
        }

        // Stored as a repeated-string proto message for simplicity
        var listProto = SentinelProto.SentinelNodeIdList.Parser.ParseFrom(listBytes);
        return [.. listProto.NodeIds];
    }

    private void AddToSentinelList(string nodeId)
    {
        var list = LoadSentinelList();
        if (!list.Contains(nodeId))
        {
            list.Add(nodeId);
            SaveSentinelList(list);
        }
    }

    private void RemoveFromSentinelList(string nodeId)
    {
        var list = LoadSentinelList();
        if (list.Remove(nodeId))
        {
            SaveSentinelList(list);
        }
    }

    private void SaveSentinelList(List<string> list)
    {
        var listProto = new SentinelProto.SentinelNodeIdList();
        listProto.NodeIds.AddRange(list);
        StateContext.SetState(SentinelListKey, listProto.ToByteArray());
    }

    // ────────────────────────────────────────────
    // State key helpers
    // ────────────────────────────────────────────

    private static string CreateSentinelKey(string nodeId)
    {
        return $"{SentinelKeyPrefix}{nodeId}";
    }

    private static string CreateEpochKey(long epochNumber)
    {
        return $"{EpochKeyPrefix}{epochNumber}";
    }

    private static string CreateSentinelRegisteredEventKey(string nodeId)
    {
        return $"{CreateSentinelKey(nodeId)}:event:registered";
    }

    private static string CreateSentinelExitedEventKey(string nodeId)
    {
        return $"{CreateSentinelKey(nodeId)}:event:exited";
    }

    private static string CreateComputationCreditEventKey(string nodeId, Hash sessionId)
    {
        return $"{CreateSentinelKey(nodeId)}:event:credit:{sessionId.ToHex()}";
    }

    private static string CreateEpochAdvancedEventKey(long epochNumber)
    {
        return $"{CreateEpochKey(epochNumber)}:event:advanced";
    }

    // ────────────────────────────────────────────
    // Resource extractors for parallel execution
    // ────────────────────────────────────────────

    private static IEnumerable<string> GetRegisterSentinelReadKeys(SentinelProto.RegisterSentinelInput input)
    {
        yield return CreateSentinelKey(input.NodeId);
        yield return GovernanceKey;
        yield return SentinelListKey;
    }

    private static IEnumerable<string> GetRegisterSentinelWriteKeys(SentinelProto.RegisterSentinelInput input)
    {
        yield return CreateSentinelKey(input.NodeId);
        yield return SentinelListKey;
        yield return CreateSentinelRegisteredEventKey(input.NodeId);
    }

    private static IEnumerable<string> GetExitSentinelReadKeys(SentinelProto.ExitSentinelInput input)
    {
        yield return CreateSentinelKey(input.NodeId);
        yield return SentinelListKey;
    }

    private static IEnumerable<string> GetExitSentinelWriteKeys(SentinelProto.ExitSentinelInput input)
    {
        yield return CreateSentinelKey(input.NodeId);
        yield return SentinelListKey;
        yield return CreateSentinelExitedEventKey(input.NodeId);
    }

    private static IEnumerable<string> GetRecordComputationCreditReadKeys(SentinelProto.RecordComputationCreditInput input)
    {
        EnsureHash(input.SessionId, nameof(input.SessionId));
        yield return CreateSentinelKey(input.SentinelNodeId);
    }

    private static IEnumerable<string> GetRecordComputationCreditWriteKeys(SentinelProto.RecordComputationCreditInput input)
    {
        EnsureHash(input.SessionId, nameof(input.SessionId));
        yield return CreateSentinelKey(input.SentinelNodeId);
        yield return CreateComputationCreditEventKey(input.SentinelNodeId, input.SessionId);
    }

    private static IEnumerable<string> GetAdvanceEpochReadKeys(SentinelProto.AdvanceEpochInput input)
    {
        yield return CurrentEpochKey;
        yield return GovernanceKey;
        yield return SentinelListKey;
    }

    private static IEnumerable<string> GetAdvanceEpochWriteKeys(SentinelProto.AdvanceEpochInput input)
    {
        yield return CurrentEpochKey;
        yield return CreateEpochKey(input.EpochNumber);
        yield return CreateEpochAdvancedEventKey(input.EpochNumber);
    }

    private static IEnumerable<string> GetUpdateGovernanceReadKeys()
    {
        yield return GovernanceKey;
    }

    private static IEnumerable<string> GetUpdateGovernanceWriteKeys()
    {
        yield return GovernanceKey;
    }
}
