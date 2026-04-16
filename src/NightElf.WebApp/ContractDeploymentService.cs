using System.Reflection;
using System.Text;
using System.Text.Json;

using Google.Protobuf;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Runtime.CSharp;
using NightElf.Sdk.CSharp;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp;

public sealed class ContractDeploymentService
{
    private readonly IChainStateStore _chainStateStore;
    private readonly ITransactionResultStore _transactionResultStore;
    private readonly INonCriticalEventBus _eventBus;

    public ContractDeploymentService(
        IChainStateStore chainStateStore,
        ITransactionResultStore transactionResultStore,
        INonCriticalEventBus eventBus)
    {
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
        _transactionResultStore = transactionResultStore ?? throw new ArgumentNullException(nameof(transactionResultStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    }

    internal async Task<ChainSettlementContractDeployExecutionResult> DeployAsync(
        ContractDeployRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedContractName = ChainSettlementSigningHelper.NormalizeContractName(request.ContractName);
        if (request.AssemblyBytes.IsEmpty)
        {
            return new ChainSettlementContractDeployExecutionResult
            {
                TransactionId = string.Empty,
                CodeHash = string.Empty,
                Status = TransactionExecutionStatus.Rejected,
                Error = "Contract assembly bytes must not be empty."
            };
        }

        if (request.Deployer is null || request.Deployer.Value.IsEmpty)
        {
            return new ChainSettlementContractDeployExecutionResult
            {
                TransactionId = string.Empty,
                CodeHash = string.Empty,
                Status = TransactionExecutionStatus.Rejected,
                Error = "Contract deployer address must not be empty."
            };
        }

        if (request.Deployer.Value.Length != TransactionExtensions.Ed25519PublicKeyLength)
        {
            return new ChainSettlementContractDeployExecutionResult
            {
                TransactionId = string.Empty,
                CodeHash = string.Empty,
                Status = TransactionExecutionStatus.Rejected,
                Error = $"Contract deployer address must contain a {TransactionExtensions.Ed25519PublicKeyLength}-byte Ed25519 public key."
            };
        }

        if (request.Signature.Length != TransactionExtensions.Ed25519SignatureLength)
        {
            return new ChainSettlementContractDeployExecutionResult
            {
                TransactionId = string.Empty,
                CodeHash = string.Empty,
                Status = TransactionExecutionStatus.Rejected,
                Error = $"Contract deployment signature must be {TransactionExtensions.Ed25519SignatureLength} bytes long."
            };
        }

        var codeHash = ChainSettlementSigningHelper.CreateCodeHash(request.AssemblyBytes.Span);
        var transactionId = ChainSettlementSigningHelper.CreateDeploymentTransactionId(
            request.Deployer,
            codeHash,
            request.Signature.Span);

        if (!VerifyDeploySignature(request, normalizedContractName, out var signatureError))
        {
            await _transactionResultStore.RecordRejectedAsync(transactionId, signatureError, cancellationToken).ConfigureAwait(false);
            await PublishTransactionRejectedEventAsync(transactionId, signatureError, cancellationToken).ConfigureAwait(false);
            return new ChainSettlementContractDeployExecutionResult
            {
                TransactionId = transactionId,
                CodeHash = codeHash,
                Status = TransactionExecutionStatus.Rejected,
                Error = signatureError ?? string.Empty
            };
        }

        var bestChain = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false);
        if (bestChain is null)
        {
            await _transactionResultStore.RecordRejectedAsync(transactionId, "Genesis has not been initialized yet.", cancellationToken).ConfigureAwait(false);
            await PublishTransactionRejectedEventAsync(transactionId, "Genesis has not been initialized yet.", cancellationToken).ConfigureAwait(false);
            return new ChainSettlementContractDeployExecutionResult
            {
                TransactionId = transactionId,
                CodeHash = codeHash,
                Status = TransactionExecutionStatus.Rejected,
                Error = "Genesis has not been initialized yet."
            };
        }

        string entryContractType;
        try
        {
            entryContractType = ValidateAssembly(request.AssemblyBytes.ToByteArray(), normalizedContractName);
        }
        catch (Exception exception)
        {
            await _transactionResultStore.RecordRejectedAsync(transactionId, exception.Message, cancellationToken).ConfigureAwait(false);
            await PublishTransactionRejectedEventAsync(transactionId, exception.Message, cancellationToken).ConfigureAwait(false);
            return new ChainSettlementContractDeployExecutionResult
            {
                TransactionId = transactionId,
                CodeHash = codeHash,
                Status = TransactionExecutionStatus.Rejected,
                Error = exception.Message
            };
        }

        var contractAddress = ChainSettlementSigningHelper.CreateContractAddress(request.Deployer, codeHash);
        var contractAddressHex = contractAddress.ToHex();
        var existingMetadata = await _chainStateStore.Database
            .GetAsync(ChainSettlementStateKeys.GetContractMetadataKey(contractAddressHex), cancellationToken)
            .ConfigureAwait(false);
        if (existingMetadata is not null)
        {
            await _transactionResultStore.RecordRejectedAsync(
                    transactionId,
                    $"Contract address '{contractAddressHex}' has already been deployed.",
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishTransactionRejectedEventAsync(
                    transactionId,
                    $"Contract address '{contractAddressHex}' has already been deployed.",
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChainSettlementContractDeployExecutionResult
            {
                TransactionId = transactionId,
                CodeHash = codeHash,
                Status = TransactionExecutionStatus.Rejected,
                Error = $"Contract address '{contractAddressHex}' has already been deployed."
            };
        }

        var deploymentRecord = new ChainSettlementContractDeploymentRecord
        {
            ContractName = normalizedContractName,
            ContractAddressHex = contractAddressHex,
            DeployerAddressHex = request.Deployer.ToHex(),
            TransactionId = transactionId,
            CodeHash = codeHash,
            EntryContractType = entryContractType,
            AssemblySize = request.AssemblyBytes.Length,
            BlockHeight = bestChain.Height,
            BlockHash = bestChain.Hash,
            DeployedAtUtc = DateTimeOffset.UtcNow
        };

        var writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [ChainSettlementStateKeys.GetContractMarkerKey(contractAddressHex)] = Encoding.UTF8.GetBytes("deployed"),
            [ChainSettlementStateKeys.GetContractAssemblyKey(contractAddressHex)] = request.AssemblyBytes.ToByteArray(),
            [ChainSettlementStateKeys.GetContractMetadataKey(contractAddressHex)] = JsonSerializer.SerializeToUtf8Bytes(
                deploymentRecord,
                ChainSettlementJsonSerializerContext.Default.ChainSettlementContractDeploymentRecord),
            [ChainSettlementStateKeys.GetContractCodeHashKey(codeHash)] = Encoding.UTF8.GetBytes(contractAddressHex),
            [ChainSettlementStateKeys.GetContractTransactionKey(transactionId)] = Encoding.UTF8.GetBytes(contractAddressHex)
        };

        await _chainStateStore.ApplyChangesAsync(bestChain, writes, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _transactionResultStore
            .RecordBlockResultAsync(transactionId, bestChain, TransactionResultStatus.Mined, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var resultPayload = TransactionResultProtoConverter.Create(
            transactionId,
            TransactionResultStatus.Mined,
            block: bestChain);
        await _eventBus.PublishAsync(
                new ChainSettlementEventEnvelope(
                    EventId: $"deploy:{transactionId}",
                    EventType: ChainEventType.ContractDeployed,
                    OccurredAtUtc: deploymentRecord.DeployedAtUtc,
                    BlockHeight: bestChain.Height,
                    BlockHash: bestChain.Hash,
                    TransactionId: transactionId,
                    ContractAddress: contractAddressHex,
                    StateKey: ChainSettlementStateKeys.GetContractMetadataKey(contractAddressHex),
                    Payload: JsonSerializer.SerializeToUtf8Bytes(
                        deploymentRecord,
                        ChainSettlementJsonSerializerContext.Default.ChainSettlementContractDeploymentRecord),
                    Message: normalizedContractName),
                cancellationToken)
            .ConfigureAwait(false);
        await _eventBus.PublishAsync(
                new ChainSettlementEventEnvelope(
                    EventId: $"tx:{transactionId}:{TransactionExecutionStatus.Mined}",
                    EventType: ChainEventType.TransactionResult,
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    BlockHeight: bestChain.Height,
                    BlockHash: bestChain.Hash,
                    TransactionId: transactionId,
                    ContractAddress: contractAddressHex,
                    Payload: resultPayload.ToByteArray(),
                    Message: TransactionExecutionStatus.Mined.ToString()),
                cancellationToken)
            .ConfigureAwait(false);

        return new ChainSettlementContractDeployExecutionResult
        {
            TransactionId = transactionId,
            CodeHash = codeHash,
            Status = TransactionExecutionStatus.Mined,
            Error = string.Empty,
            ContractAddressHex = contractAddressHex,
            BlockHeight = bestChain.Height,
            BlockHash = bestChain.Hash
        };
    }

    private static bool VerifyDeploySignature(
        ContractDeployRequest request,
        string normalizedContractName,
        out string? error)
    {
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(request.Deployer.Value.ToByteArray(), 0));
            var signingHash = ChainSettlementSigningHelper.CreateContractDeploySigningHash(
                request.AssemblyBytes.Span,
                normalizedContractName);
            verifier.BlockUpdate(signingHash, 0, signingHash.Length);

            var verified = verifier.VerifySignature(request.Signature.ToByteArray());
            error = verified ? null : "Contract deployment signature verification failed.";
            return verified;
        }
        catch (Exception exception)
        {
            error = $"Contract deployment signature verification failed: {exception.Message}";
            return false;
        }
    }

    private static string ValidateAssembly(
        byte[] assemblyBytes,
        string normalizedContractName)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "nightelf-contract-deploy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        try
        {
            var assemblyPath = Path.Combine(rootDirectory, $"{normalizedContractName}.dll");
            File.WriteAllBytes(assemblyPath, assemblyBytes);

            var contractAssemblyName = AssemblyName.GetAssemblyName(assemblyPath).Name ?? normalizedContractName;
            var sandboxOptions = new ContractSandboxOptions();
            foreach (var loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var loadedAssemblyName = loadedAssembly.GetName().Name;
                if (string.IsNullOrWhiteSpace(loadedAssemblyName) ||
                    string.Equals(loadedAssemblyName, contractAssemblyName, StringComparison.Ordinal))
                {
                    continue;
                }

                sandboxOptions.ShareAssembly(loadedAssemblyName);
            }

            var sandbox = new ContractSandbox(normalizedContractName, sandboxOptions);
            try
            {
                var assembly = sandbox.LoadContractFromPath(assemblyPath);
                var entryType = ResolveEntryContractType(assembly);
                return entryType.FullName ?? entryType.Name;
            }
            finally
            {
                sandbox.Unload();
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootDirectory))
                {
                    Directory.Delete(rootDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static Type ResolveEntryContractType(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            var firstLoaderException = exception.LoaderExceptions?.FirstOrDefault();
            throw new InvalidOperationException(
                $"Contract assembly could not be inspected: {firstLoaderException?.Message ?? exception.Message}",
                firstLoaderException ?? exception);
        }

        var contractTypes = types
            .Where(type => !type.IsAbstract && typeof(CSharpSmartContract).IsAssignableFrom(type))
            .ToArray();

        return contractTypes.Length switch
        {
            0 => throw new InvalidOperationException("Contract assembly does not contain a concrete CSharpSmartContract implementation."),
            1 => contractTypes[0],
            _ => throw new InvalidOperationException("Contract assembly must contain exactly one concrete CSharpSmartContract implementation in Phase 2.")
        };
    }

    private Task PublishTransactionRejectedEventAsync(
        string transactionId,
        string? error,
        CancellationToken cancellationToken)
    {
        var result = TransactionResultProtoConverter.CreateRejected(transactionId, error);
        return _eventBus.PublishAsync(
            new ChainSettlementEventEnvelope(
                EventId: $"tx:{transactionId}:{TransactionExecutionStatus.Rejected}",
                EventType: ChainEventType.TransactionResult,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                TransactionId: transactionId,
                Payload: result.ToByteArray(),
                Message: error),
            cancellationToken);
    }
}
