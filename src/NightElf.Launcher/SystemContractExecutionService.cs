using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using NightElf.Database;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Kernel.SmartContract;
using NightElf.Runtime.CSharp;
using NightElf.Sdk.CSharp;
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
    private readonly SmartContractExecutor _executor;
    private readonly ContractSandboxExecutionService _sandboxExecutionService;

    public SystemContractExecutionService(
        LauncherOptions launcherOptions,
        IChainStateStore chainStateStore,
        SmartContractExecutor executor,
        ContractSandboxExecutionService sandboxExecutionService)
    {
        _launcherOptions = launcherOptions ?? throw new ArgumentNullException(nameof(launcherOptions));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
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
            key => LoadStateValueAsync(key, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult());
        var executionContext = new ContractExecutionContext(
            new ContractStateContext(stateProvider),
            new ContractCallContext(new UnsupportedContractCallHandler()),
            new ContractCryptoContext(new DefaultContractCryptoProvider()),
            new ContractIdentityContext(new DefaultContractIdentityProvider()),
            transactionId: transaction.GetTransactionId(),
            senderAddress: transaction.From.ToHex(),
            currentContractAddress: transaction.To.ToHex(),
            blockHeight: block.Height,
            blockHash: block.Hash,
            timestamp: timestampUtc,
            transactionIndex: transactionIndex);

        try
        {
            await _sandboxExecutionService.ExecuteContractAsync(
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

    private sealed class UnsupportedContractCallHandler : IContractCallHandler
    {
        public byte[] Call(string contractAddress, ContractInvocation invocation)
        {
            throw new InvalidOperationException(
                $"Cross-contract call '{contractAddress}:{invocation.MethodName}' is not enabled in Phase 1.");
        }

        public void SendInline(string contractAddress, ContractInvocation invocation)
        {
            throw new InvalidOperationException(
                $"Inline contract call '{contractAddress}:{invocation.MethodName}' is not enabled in Phase 1.");
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
