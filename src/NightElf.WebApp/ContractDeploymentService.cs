using System.Reflection;
using System.Text;
using System.Text.Json;

using Google.Protobuf;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using NightElf.DynamicContracts;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Runtime.CSharp;
using NightElf.Runtime.CSharp.Security;
using NightElf.Sdk.CSharp;
using NightElf.WebApp.Protobuf;
using ChainTransactionResultStatus = NightElf.Kernel.Core.TransactionResultStatus;

namespace NightElf.WebApp;

public sealed class ContractDeploymentService
{
    private readonly IChainStateStore _chainStateStore;
    private readonly ITransactionResultStore _transactionResultStore;
    private readonly INonCriticalEventBus _eventBus;
    private readonly DynamicContractBuildService _dynamicContractBuildService;

    public ContractDeploymentService(
        IChainStateStore chainStateStore,
        ITransactionResultStore transactionResultStore,
        INonCriticalEventBus eventBus,
        DynamicContractBuildService dynamicContractBuildService)
    {
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
        _transactionResultStore = transactionResultStore ?? throw new ArgumentNullException(nameof(transactionResultStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _dynamicContractBuildService = dynamicContractBuildService ?? throw new ArgumentNullException(nameof(dynamicContractBuildService));
    }

    public async Task<ContractDeployResult> DeployDynamicAsync(
        DynamicContractDeployRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = await DeployDynamicInternalAsync(request, cancellationToken).ConfigureAwait(false);
        return ToProtoResult(deployment);
    }

    internal async Task<ChainSettlementContractDeployExecutionResult> DeployAsync(
        ContractDeployRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedContractName = ChainSettlementSigningHelper.NormalizeContractName(request.ContractName);
        if (request.AssemblyBytes.IsEmpty)
        {
            return CreateRejectedResult("Contract assembly bytes must not be empty.");
        }

        if (!TryValidateDeploymentIdentity(request.Deployer, request.Signature, out var validationError))
        {
            return CreateRejectedResult(validationError);
        }

        var assemblyBytes = request.AssemblyBytes.ToByteArray();
        var codeHash = ChainSettlementSigningHelper.CreateCodeHash(assemblyBytes);
        var transactionId = ChainSettlementSigningHelper.CreateDeploymentTransactionId(
            request.Deployer,
            codeHash,
            request.Signature.Span);

        if (!VerifyDeploySignature(request, normalizedContractName, out var signatureError))
        {
            return await RecordRejectedAsync(transactionId, codeHash, signatureError, cancellationToken).ConfigureAwait(false);
        }

        return await DeployCompiledAssemblyAsync(
                assemblyBytes,
                normalizedContractName,
                request.Deployer,
                transactionId,
                codeHash,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ChainSettlementContractDeployExecutionResult> DeployDynamicInternalAsync(
        DynamicContractDeployRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateDeploymentIdentity(request.Deployer, request.Signature, out var validationError))
        {
            return CreateRejectedResult(validationError);
        }

        DynamicContractBuildArtifact artifact;
        try
        {
            artifact = await _dynamicContractBuildService.BuildAsync(request.Spec, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return CreateRejectedResult($"Dynamic contract build failed: {exception.Message}");
        }

        var normalizedContractName = ChainSettlementSigningHelper.NormalizeContractName(artifact.ContractName);
        var codeHash = ChainSettlementSigningHelper.CreateCodeHash(artifact.AssemblyBytes);
        var transactionId = ChainSettlementSigningHelper.CreateDeploymentTransactionId(
            request.Deployer,
            codeHash,
            request.Signature.Span);

        if (!VerifyDynamicDeploySignature(request, normalizedContractName, out var signatureError))
        {
            return await RecordRejectedAsync(transactionId, codeHash, signatureError, cancellationToken).ConfigureAwait(false);
        }

        return await DeployCompiledAssemblyAsync(
                artifact.AssemblyBytes,
                normalizedContractName,
                request.Deployer,
                transactionId,
                codeHash,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ChainSettlementContractDeployExecutionResult> DeployCompiledAssemblyAsync(
        byte[] assemblyBytes,
        string normalizedContractName,
        Address deployer,
        string transactionId,
        string codeHash,
        CancellationToken cancellationToken)
    {
        var bestChain = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false);
        if (bestChain is null)
        {
            return await RecordRejectedAsync(transactionId, codeHash, "Genesis has not been initialized yet.", cancellationToken).ConfigureAwait(false);
        }

        string entryContractType;
        try
        {
            entryContractType = ValidateAssembly(assemblyBytes, normalizedContractName);
        }
        catch (Exception exception)
        {
            return await RecordRejectedAsync(transactionId, codeHash, exception.Message, cancellationToken).ConfigureAwait(false);
        }

        var contractAddress = ChainSettlementSigningHelper.CreateContractAddress(deployer, codeHash);
        var contractAddressHex = contractAddress.ToHex();
        var existingMetadata = await _chainStateStore.Database
            .GetAsync(ChainSettlementStateKeys.GetContractMetadataKey(contractAddressHex), cancellationToken)
            .ConfigureAwait(false);
        if (existingMetadata is not null)
        {
            return await RecordRejectedAsync(
                    transactionId,
                    codeHash,
                    $"Contract address '{contractAddressHex}' has already been deployed.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var deploymentRecord = new ChainSettlementContractDeploymentRecord
        {
            ContractName = normalizedContractName,
            ContractAddressHex = contractAddressHex,
            DeployerAddressHex = deployer.ToHex(),
            TransactionId = transactionId,
            CodeHash = codeHash,
            EntryContractType = entryContractType,
            AssemblySize = assemblyBytes.Length,
            BlockHeight = bestChain.Height,
            BlockHash = bestChain.Hash,
            DeployedAtUtc = DateTimeOffset.UtcNow
        };

        var writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [ChainSettlementStateKeys.GetContractMarkerKey(contractAddressHex)] = Encoding.UTF8.GetBytes("deployed"),
            [ChainSettlementStateKeys.GetContractAssemblyKey(contractAddressHex)] = assemblyBytes,
            [ChainSettlementStateKeys.GetContractMetadataKey(contractAddressHex)] = JsonSerializer.SerializeToUtf8Bytes(
                deploymentRecord,
                ChainSettlementJsonSerializerContext.Default.ChainSettlementContractDeploymentRecord),
            [ChainSettlementStateKeys.GetContractCodeHashKey(codeHash)] = Encoding.UTF8.GetBytes(contractAddressHex),
            [ChainSettlementStateKeys.GetContractTransactionKey(transactionId)] = Encoding.UTF8.GetBytes(contractAddressHex)
        };

        await _chainStateStore.ApplyChangesAsync(bestChain, writes, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _transactionResultStore
            .RecordBlockResultAsync(transactionId, bestChain, ChainTransactionResultStatus.Mined, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var resultPayload = TransactionResultProtoConverter.Create(
            transactionId,
            ChainTransactionResultStatus.Mined,
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

    private async Task<ChainSettlementContractDeployExecutionResult> RecordRejectedAsync(
        string transactionId,
        string codeHash,
        string? error,
        CancellationToken cancellationToken)
    {
        await _transactionResultStore.RecordRejectedAsync(transactionId, error, cancellationToken).ConfigureAwait(false);
        await PublishTransactionRejectedEventAsync(transactionId, error, cancellationToken).ConfigureAwait(false);

        return new ChainSettlementContractDeployExecutionResult
        {
            TransactionId = transactionId,
            CodeHash = codeHash,
            Status = TransactionExecutionStatus.Rejected,
            Error = error ?? string.Empty
        };
    }

    private static ContractDeployResult ToProtoResult(ChainSettlementContractDeployExecutionResult deployment)
    {
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

    private static bool TryValidateDeploymentIdentity(
        Address? deployer,
        ByteString signature,
        out string error)
    {
        if (deployer is null || deployer.Value.IsEmpty)
        {
            error = "Contract deployer address must not be empty.";
            return false;
        }

        if (deployer.Value.Length != TransactionExtensions.Ed25519PublicKeyLength)
        {
            error = $"Contract deployer address must contain a {TransactionExtensions.Ed25519PublicKeyLength}-byte Ed25519 public key.";
            return false;
        }

        if (signature.Length != TransactionExtensions.Ed25519SignatureLength)
        {
            error = $"Contract deployment signature must be {TransactionExtensions.Ed25519SignatureLength} bytes long.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static ChainSettlementContractDeployExecutionResult CreateRejectedResult(string? error)
    {
        return new ChainSettlementContractDeployExecutionResult
        {
            TransactionId = string.Empty,
            CodeHash = string.Empty,
            Status = TransactionExecutionStatus.Rejected,
            Error = error ?? string.Empty
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

    private static bool VerifyDynamicDeploySignature(
        DynamicContractDeployRequest request,
        string normalizedContractName,
        out string? error)
    {
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(request.Deployer.Value.ToByteArray(), 0));
            var signingHash = ChainSettlementSigningHelper.CreateDynamicContractDeploySigningHash(
                request.Spec,
                normalizedContractName);
            verifier.BlockUpdate(signingHash, 0, signingHash.Length);

            var verified = verifier.VerifySignature(request.Signature.ToByteArray());
            error = verified ? null : "Dynamic contract deployment signature verification failed.";
            return verified;
        }
        catch (Exception exception)
        {
            error = $"Dynamic contract deployment signature verification failed: {exception.Message}";
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

            var staticAnalysisResult = new ContractAssemblyStaticAnalyzer().Analyze(assemblyPath);
            staticAnalysisResult.ThrowIfFailed();

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
            _ => throw new InvalidOperationException("Contract assembly must contain exactly one concrete CSharpSmartContract implementation in Phase 3.")
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
