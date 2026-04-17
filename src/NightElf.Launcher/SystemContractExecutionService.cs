using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using NightElf.Contracts.System.Treaty.Protobuf;
using NightElf.Database;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Kernel.SmartContract;
using NightElf.Runtime.CSharp;
using NightElf.Sdk.CSharp;
using NightElf.WebApp;
using NightElf.WebApp.Protobuf;
using ChainTransactionResultStatus = NightElf.Kernel.Core.TransactionResultStatus;

namespace NightElf.Launcher;

public interface IBlockTransactionExecutionService
{
    Task<BlockTransactionExecutionResult> ExecuteAsync(
        IReadOnlyList<Transaction> transactions,
        BlockReference block,
        DateTimeOffset timestampUtc,
        CancellationToken cancellationToken = default);
}

public sealed class BlockTransactionExecutionResult
{
    public IReadOnlyDictionary<string, byte[]> Writes { get; init; } =
        new Dictionary<string, byte[]>(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Deletes { get; init; } = [];

    public IReadOnlyList<BlockTransactionExecutionOutcome> Outcomes { get; init; } = [];
}

public sealed class BlockTransactionExecutionOutcome
{
    public required Transaction Transaction { get; init; }

    public required ChainTransactionResultStatus Status { get; init; }

    public string? Error { get; init; }
}

public sealed class SystemContractExecutionService : IBlockTransactionExecutionService
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, ResolvedSystemContract?> _resolvedContractsByAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GenesisSystemContractDeploymentRecord?> _deploymentsByName =
        new(StringComparer.Ordinal);

    private readonly LauncherOptions _launcherOptions;
    private readonly IChainStateStore _chainStateStore;
    private readonly INonCriticalEventBus _eventBus;
    private readonly SmartContractExecutor _executor;
    private readonly ContractSandboxExecutionService _sandboxExecutionService;

    public SystemContractExecutionService(
        LauncherOptions launcherOptions,
        IChainStateStore chainStateStore,
        INonCriticalEventBus eventBus,
        SmartContractExecutor executor,
        ContractSandboxExecutionService sandboxExecutionService)
    {
        _launcherOptions = launcherOptions ?? throw new ArgumentNullException(nameof(launcherOptions));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sandboxExecutionService = sandboxExecutionService ?? throw new ArgumentNullException(nameof(sandboxExecutionService));
    }

    public async Task<BlockTransactionExecutionResult> ExecuteAsync(
        IReadOnlyList<Transaction> transactions,
        BlockReference block,
        DateTimeOffset timestampUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(block);

        if (transactions.Count == 0)
        {
            return new BlockTransactionExecutionResult();
        }

        var blockWrites = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var blockDeletes = new HashSet<string>(StringComparer.Ordinal);
        var outcomes = new List<BlockTransactionExecutionOutcome>(transactions.Count);

        for (var transactionIndex = 0; transactionIndex < transactions.Count; transactionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transaction = transactions[transactionIndex];
            var outcome = await ExecuteTransactionAsync(
                    transaction,
                    transactionIndex,
                    block,
                    timestampUtc,
                    blockWrites,
                    blockDeletes,
                    cancellationToken)
                .ConfigureAwait(false);

            outcomes.Add(outcome);
        }

        return new BlockTransactionExecutionResult
        {
            Writes = blockWrites,
            Deletes = blockDeletes.ToArray(),
            Outcomes = outcomes
        };
    }

    private async Task<BlockTransactionExecutionOutcome> ExecuteTransactionAsync(
        Transaction transaction,
        int transactionIndex,
        BlockReference block,
        DateTimeOffset timestampUtc,
        IDictionary<string, byte[]> blockWrites,
        ISet<string> blockDeletes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var resolvedContract = await ResolveContractAsync(transaction.To, cancellationToken).ConfigureAwait(false);
        if (resolvedContract is null)
        {
            return Failed(transaction, $"Contract address '{transaction.To.ToHex()}' is not deployed.");
        }

        if (!SystemContractArtifactCatalog.TryCreateContractInstance(resolvedContract.ContractName, out var contract) ||
            contract is null)
        {
            return Failed(
                transaction,
                $"Contract '{resolvedContract.ContractName}' does not have a local runtime implementation.");
        }

        var invocation = new ContractInvocation(transaction.MethodName, transaction.Params.ToByteArray());
        var transactionId = transaction.GetTransactionId();

        try
        {
            _ = await ExecuteContractInvocationAsync(
                    contract,
                    invocation,
                    transactionId,
                    transaction.From.ToHex(),
                    transaction.To.ToHex(),
                    block.Height,
                    block.Hash,
                    timestampUtc,
                    transactionIndex,
                    isDynamicContract: false,
                    callerTreatyId: null,
                    blockWrites,
                    blockDeletes,
                    cancellationToken)
                .ConfigureAwait(false);

            return new BlockTransactionExecutionOutcome
            {
                Transaction = transaction,
                Status = ChainTransactionResultStatus.Mined,
                Error = null
            };
        }
        catch (Exception exception)
        {
            return Failed(transaction, exception.Message);
        }
    }

    private async Task<byte[]> ExecuteContractInvocationAsync(
        CSharpSmartContract contract,
        ContractInvocation invocation,
        string transactionId,
        string senderAddress,
        string currentContractAddress,
        long blockHeight,
        string blockHash,
        DateTimeOffset timestampUtc,
        int transactionIndex,
        bool isDynamicContract,
        string? callerTreatyId,
        IDictionary<string, byte[]> blockWrites,
        ISet<string> blockDeletes,
        CancellationToken cancellationToken)
    {
        var prefetchedState = await LoadPrefetchedStateAsync(
                contract,
                invocation,
                blockWrites,
                blockDeletes,
                cancellationToken)
            .ConfigureAwait(false);
        var stateProvider = new OverlayContractStateProvider(
            prefetchedState,
            blockWrites,
            blockDeletes,
            key => LoadStateValueSync(key, cancellationToken));

        ContractExecutionContext? executionContext = null;
        var permissionGrantChecker = new StateBackedContractCallPermissionGrantChecker(
            key => LoadStateValueSync(key, cancellationToken));
        var callHandler = new AuthorizingContractCallHandler(
            () => executionContext ?? throw new InvalidOperationException("Cross-contract call context is not available."),
            ResolveCallTargetInfo,
            request => DispatchCrossContractCall(request, blockWrites, blockDeletes, cancellationToken),
            permissionGrantChecker,
            deniedEvent => PublishCrossContractCallDeniedEvent(
                deniedEvent,
                transactionId,
                blockHeight,
                blockHash,
                cancellationToken));

        executionContext = new ContractExecutionContext(
            new ContractStateContext(stateProvider),
            new ContractCallContext(callHandler),
            new ContractCryptoContext(new DefaultContractCryptoProvider()),
            new ContractIdentityContext(new DefaultContractIdentityProvider()),
            transactionId: transactionId,
            senderAddress: senderAddress,
            currentContractAddress: currentContractAddress,
            blockHeight: blockHeight,
            blockHash: blockHash,
            timestamp: timestampUtc,
            transactionIndex: transactionIndex,
            isDynamicContract: isDynamicContract,
            callerTreatyId: callerTreatyId);

        var result = await _sandboxExecutionService.ExecuteContractAsync(
                _executor,
                contract,
                invocation,
                executionContext,
                ExecutionTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var write in stateProvider.Writes)
        {
            blockDeletes.Remove(write.Key);
            blockWrites[write.Key] = write.Value;
        }

        foreach (var delete in stateProvider.Deletes)
        {
            blockWrites.Remove(delete);
            blockDeletes.Add(delete);
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, byte[]?>> LoadPrefetchedStateAsync(
        CSharpSmartContract contract,
        ContractInvocation invocation,
        IDictionary<string, byte[]> blockWrites,
        ISet<string> blockDeletes,
        CancellationToken cancellationToken)
    {
        if (!contract.SupportsResourceExtraction)
        {
            return new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        }

        var resourceSet = contract.DescribeResources(invocation);
        var keys = resourceSet.ReadKeys
            .Concat(resourceSet.WriteKeys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (keys.Length == 0)
        {
            return new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        }

        var prefetched = new Dictionary<string, byte[]?>(keys.Length, StringComparer.Ordinal);
        var keysToLoad = new List<string>(keys.Length);
        foreach (var key in keys)
        {
            if (blockWrites.TryGetValue(key, out var writtenValue))
            {
                prefetched[key] = writtenValue;
                continue;
            }

            if (blockDeletes.Contains(key))
            {
                prefetched[key] = null;
                continue;
            }

            keysToLoad.Add(key);
        }

        if (keysToLoad.Count > 0)
        {
            var loaded = await _chainStateStore.Database
                .GetAllAsync(keysToLoad, cancellationToken)
                .ConfigureAwait(false);

            foreach (var pair in loaded)
            {
                prefetched[pair.Key] = UnwrapStateValue(pair.Value);
            }
        }

        return prefetched;
    }

    private async Task<byte[]?> LoadStateValueAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var bytes = await _chainStateStore.Database.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return UnwrapStateValue(bytes);
    }

    private byte[]? LoadStateValueSync(
        string key,
        CancellationToken cancellationToken)
    {
        return LoadStateValueAsync(key, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private async Task<ResolvedSystemContract?> ResolveContractAsync(
        Address address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        var addressHex = address.ToHex();
        lock (_cacheLock)
        {
            if (_resolvedContractsByAddress.TryGetValue(addressHex, out var cached))
            {
                return cached;
            }
        }

        foreach (var contractName in _launcherOptions.Genesis.SystemContracts.Distinct(StringComparer.Ordinal))
        {
            var deployment = await LoadDeploymentAsync(contractName, cancellationToken).ConfigureAwait(false);
            if (deployment is null ||
                !StringComparer.OrdinalIgnoreCase.Equals(deployment.AddressHex, addressHex))
            {
                continue;
            }

            var resolved = new ResolvedSystemContract(contractName, addressHex);
            lock (_cacheLock)
            {
                _resolvedContractsByAddress[addressHex] = resolved;
            }

            return resolved;
        }

        lock (_cacheLock)
        {
            _resolvedContractsByAddress[addressHex] = null;
        }

        return null;
    }

    private ContractCallTargetInfo? ResolveCallTargetInfo(string contractAddressHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddressHex);

        var systemContract = ResolveSystemContractByAddressHex(contractAddressHex);
        if (systemContract is not null)
        {
            return new ContractCallTargetInfo(
                contractAddressHex,
                systemContract.ContractName,
                isDynamicContract: false,
                isSystemContract: true);
        }

        var metadataBytes = LoadStateValueSync(
            ChainSettlementStateKeys.GetContractMetadataKey(contractAddressHex),
            CancellationToken.None);
        if (metadataBytes is null)
        {
            return null;
        }

        var metadata = JsonSerializer.Deserialize<ChainSettlementContractDeploymentRecord>(
            metadataBytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return metadata is null
            ? null
            : new ContractCallTargetInfo(
                contractAddressHex,
                metadata.ContractName,
                metadata.IsDynamicContract,
                isSystemContract: false,
                metadata.OwningTreatyId);
    }

    private async Task<GenesisSystemContractDeploymentRecord?> LoadDeploymentAsync(
        string contractName,
        CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_deploymentsByName.TryGetValue(contractName, out var cached))
            {
                return cached;
            }
        }

        var recordBytes = await _chainStateStore.Database
            .GetAsync($"system-contract:{contractName}:deployment", cancellationToken)
            .ConfigureAwait(false);
        if (recordBytes is null)
        {
            lock (_cacheLock)
            {
                _deploymentsByName[contractName] = null;
            }

            return null;
        }

        var versionedRecord = VersionedStateRecord.Deserialize(recordBytes);
        var deployment = JsonSerializer.Deserialize(
            versionedRecord.Value,
            GenesisJsonSerializerContext.Default.GenesisSystemContractDeploymentRecord);

        lock (_cacheLock)
        {
            _deploymentsByName[contractName] = deployment;
        }

        return deployment;
    }

    private ResolvedSystemContract? ResolveSystemContractByAddressHex(string contractAddressHex)
    {
        lock (_cacheLock)
        {
            if (_resolvedContractsByAddress.TryGetValue(contractAddressHex, out var cached))
            {
                return cached;
            }
        }

        foreach (var contractName in _launcherOptions.Genesis.SystemContracts.Distinct(StringComparer.Ordinal))
        {
            var deployment = LoadDeploymentAsync(contractName, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            if (deployment is null ||
                !StringComparer.OrdinalIgnoreCase.Equals(deployment.AddressHex, contractAddressHex))
            {
                continue;
            }

            var resolved = new ResolvedSystemContract(contractName, contractAddressHex);
            lock (_cacheLock)
            {
                _resolvedContractsByAddress[contractAddressHex] = resolved;
            }

            return resolved;
        }

        lock (_cacheLock)
        {
            _resolvedContractsByAddress[contractAddressHex] = null;
        }

        return null;
    }

    private byte[] DispatchCrossContractCall(
        ContractCallDispatchRequest request,
        IDictionary<string, byte[]> blockWrites,
        ISet<string> blockDeletes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Target.IsSystemContract)
        {
            throw new InvalidOperationException(
                $"Cross-contract target '{request.Target.ContractName}' at '{request.Target.ContractAddress}' is deployed but not executable by the launcher runtime yet.");
        }

        var resolvedContract = ResolveSystemContractByAddressHex(request.Target.ContractAddress) ??
            throw new InvalidOperationException($"Contract address '{request.Target.ContractAddress}' is not deployed.");
        if (!SystemContractArtifactCatalog.TryCreateContractInstance(resolvedContract.ContractName, out var contract) ||
            contract is null)
        {
            throw new InvalidOperationException(
                $"Contract '{resolvedContract.ContractName}' does not have a local runtime implementation.");
        }

        return ExecuteContractInvocationAsync(
                contract,
                request.Invocation,
                request.CallerContext.TransactionId,
                request.CallerContext.SenderAddress,
                request.Target.ContractAddress,
                request.CallerContext.BlockHeight,
                request.CallerContext.BlockHash,
                request.CallerContext.Timestamp,
                request.CallerContext.TransactionIndex,
                request.Target.IsDynamicContract,
                request.EffectiveCallerTreatyId,
                blockWrites,
                blockDeletes,
                cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    private void PublishCrossContractCallDeniedEvent(
        CrossContractCallDeniedEvent deniedEvent,
        string transactionId,
        long blockHeight,
        string blockHash,
        CancellationToken cancellationToken)
    {
        _eventBus.PublishAsync(
                new ChainSettlementEventEnvelope(
                    EventId: $"cross-call-denied:{transactionId}:{deniedEvent.CallerContractAddress}:{deniedEvent.TargetContractAddress}:{deniedEvent.TargetMethodName}",
                    EventType: ChainEventType.CrossContractCallDenied,
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    BlockHeight: blockHeight,
                    BlockHash: blockHash,
                    TransactionId: transactionId,
                    ContractAddress: deniedEvent.CallerContractAddress,
                    Payload: JsonSerializer.SerializeToUtf8Bytes(deniedEvent),
                    Message: deniedEvent.Reason),
                cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    private static BlockTransactionExecutionOutcome Failed(Transaction transaction, string error)
    {
        return new BlockTransactionExecutionOutcome
        {
            Transaction = transaction,
            Status = ChainTransactionResultStatus.Failed,
            Error = error
        };
    }

    private static byte[]? UnwrapStateValue(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        var record = VersionedStateRecord.Deserialize(bytes);
        return record.IsDeleted ? null : record.Value;
    }

    private sealed record ResolvedSystemContract(
        string ContractName,
        string AddressHex);

    private sealed class OverlayContractStateProvider : IContractStateProvider
    {
        private readonly Dictionary<string, byte[]?> _baseValues;
        private readonly IReadOnlyDictionary<string, byte[]> _blockWrites;
        private readonly IReadOnlyCollection<string> _blockDeletes;
        private readonly Func<string, byte[]?> _fallbackLoader;
        private readonly Dictionary<string, byte[]> _writes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _deletes = new(StringComparer.Ordinal);

        public OverlayContractStateProvider(
            IReadOnlyDictionary<string, byte[]?> baseValues,
            IDictionary<string, byte[]> blockWrites,
            ISet<string> blockDeletes,
            Func<string, byte[]?> fallbackLoader)
        {
            _baseValues = new Dictionary<string, byte[]?>(baseValues, StringComparer.Ordinal);
            _blockWrites = new Dictionary<string, byte[]>(blockWrites ?? throw new ArgumentNullException(nameof(blockWrites)), StringComparer.Ordinal);
            _blockDeletes = new HashSet<string>(blockDeletes ?? throw new ArgumentNullException(nameof(blockDeletes)), StringComparer.Ordinal);
            _fallbackLoader = fallbackLoader ?? throw new ArgumentNullException(nameof(fallbackLoader));
        }

        public IReadOnlyDictionary<string, byte[]> Writes => _writes;

        public IReadOnlyCollection<string> Deletes => _deletes;

        public byte[]? GetState(string key)
        {
            if (_deletes.Contains(key))
            {
                return null;
            }

            if (_writes.TryGetValue(key, out var writtenValue))
            {
                return writtenValue;
            }

            if (_blockDeletes.Contains(key))
            {
                return null;
            }

            if (_blockWrites.TryGetValue(key, out var blockWrittenValue))
            {
                return blockWrittenValue;
            }

            if (_baseValues.TryGetValue(key, out var value))
            {
                return value;
            }

            var loadedValue = _fallbackLoader(key);
            _baseValues[key] = loadedValue;
            return loadedValue;
        }

        public void SetState(string key, byte[] value)
        {
            _deletes.Remove(key);
            _writes[key] = value.ToArray();
        }

        public void DeleteState(string key)
        {
            _writes.Remove(key);
            _deletes.Add(key);
        }

        public bool StateExists(string key)
        {
            return GetState(key) is not null;
        }
    }

    private sealed class StateBackedContractCallPermissionGrantChecker : IContractCallPermissionGrantChecker
    {
        private readonly Func<string, byte[]?> _stateLoader;

        public StateBackedContractCallPermissionGrantChecker(Func<string, byte[]?> stateLoader)
        {
            _stateLoader = stateLoader ?? throw new ArgumentNullException(nameof(stateLoader));
        }

        public bool IsAllowed(
            string treatyId,
            string agentAddress,
            ContractCallTargetInfo target,
            ContractInvocation invocation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(treatyId);
            ArgumentException.ThrowIfNullOrWhiteSpace(agentAddress);

            var treatyBytes = _stateLoader($"treaty:{treatyId}");
            if (treatyBytes is null)
            {
                return false;
            }

            var treatyState = TreatyState.Parser.ParseFrom(treatyBytes);
            return treatyState.Spec.PermissionMatrix.Grants.Any(grant =>
                StringComparer.OrdinalIgnoreCase.Equals(grant.AgentAddress?.ToHex(), agentAddress) &&
                (StringComparer.Ordinal.Equals(grant.ContractName, target.ContractName) ||
                 StringComparer.OrdinalIgnoreCase.Equals(grant.ContractName, target.ContractAddress)) &&
                StringComparer.Ordinal.Equals(grant.MethodName, invocation.MethodName));
        }
    }

    private sealed class DefaultContractCryptoProvider : IContractCryptoProvider
    {
        public byte[] Hash(ReadOnlySpan<byte> input)
        {
            return SHA256.HashData(input.ToArray());
        }

        public bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKey)
        {
            if (string.IsNullOrWhiteSpace(publicKey))
            {
                return false;
            }

            try
            {
                var verifier = new Ed25519Signer();
                verifier.Init(false, new Ed25519PublicKeyParameters(Convert.FromHexString(publicKey), 0));
                var payload = data.ToArray();
                verifier.BlockUpdate(payload, 0, payload.Length);
                return verifier.VerifySignature(signature.ToArray());
            }
            catch
            {
                return false;
            }
        }

        public byte[] DeriveVrfProof(ReadOnlySpan<byte> seed)
        {
            return SHA256.HashData(seed.ToArray());
        }
    }

    private sealed class DefaultContractIdentityProvider : IContractIdentityProvider
    {
        public string GenerateAddress(string seed)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(seed);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        }

        public string GetVirtualAddress(string contractAddress, string salt)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contractAddress);
            ArgumentException.ThrowIfNullOrWhiteSpace(salt);
            return $"virtual:{contractAddress}:{salt}";
        }
    }
}
