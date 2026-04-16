using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using NightElf.Contracts.System.AgentSession.Protobuf;
using NightElf.Database;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.Launcher;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp.Tests;

public sealed class NightElfNodeTestHarness : IAsyncDisposable
{
    private readonly string _rootPath;
    private readonly Dictionary<string, string?> _configurationOverrides;
    private WebApplication? _app;

    public NightElfNodeTestHarness(
        string? rootPath = null,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        _rootPath = rootPath ?? Path.Combine(
            Path.GetTempPath(),
            "nightelf-webapp-tests",
            Guid.NewGuid().ToString("N"));
        _configurationOverrides = configurationOverrides is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(configurationOverrides, StringComparer.Ordinal);
    }

    public string RootPath => _rootPath;

    public static async Task<NightElfNodeTestHarness> CreateAsync(
        string? rootPath = null,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        var harness = new NightElfNodeTestHarness(rootPath, configurationOverrides);
        await harness.StartAsync().ConfigureAwait(false);
        return harness;
    }

    public NightElfNode.NightElfNodeClient CreateGrpcClient()
    {
        return new NightElfNode.NightElfNodeClient(CreateChannel());
    }

    public ChainSettlement.ChainSettlementClient CreateChainSettlementClient()
    {
        return new ChainSettlement.ChainSettlementClient(CreateChannel());
    }

    public T GetRequiredService<T>()
        where T : notnull
    {
        var app = _app ?? throw new InvalidOperationException("The test harness has not been started.");
        return app.Services.GetRequiredService<T>();
    }

    public async Task RestartAsync(
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        if (configurationOverrides is not null)
        {
            _configurationOverrides.Clear();
            foreach (var overrideItem in configurationOverrides)
            {
                _configurationOverrides[overrideItem.Key] = overrideItem.Value;
            }
        }

        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    public async Task<string> GetSystemContractAddressAsync(
        string contractName,
        CancellationToken cancellationToken = default)
    {
        var chainStateStore = GetRequiredService<IChainStateStore>();
        var bytes = await chainStateStore.Database
            .GetAsync($"system-contract:{contractName}:deployment", cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
        {
            throw new InvalidOperationException($"System contract '{contractName}' is not deployed.");
        }

        var versionedRecord = VersionedStateRecord.Deserialize(bytes);
        using var document = JsonDocument.Parse(versionedRecord.Value);

        return GetJsonProperty(document.RootElement, "addressHex").GetString()
               ?? throw new InvalidOperationException($"Deployment record for '{contractName}' does not contain an address.");
    }

    public async Task<VersionedStateRecord?> GetVersionedStateAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var chainStateStore = GetRequiredService<IChainStateStore>();
        var bytes = await chainStateStore.Database.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : VersionedStateRecord.Deserialize(bytes);
    }

    public async Task<SessionState?> GetSessionStateAsync(
        Hash sessionId,
        CancellationToken cancellationToken = default)
    {
        var record = await GetVersionedStateAsync($"session:{sessionId.ToHex()}", cancellationToken).ConfigureAwait(false);
        return record is null || record.IsDeleted
            ? null
            : SessionState.Parser.ParseFrom(record.Value);
    }

    public async Task<ChainStatus> WaitForChainHeightAsync(
        long minimumHeight,
        TimeSpan? timeout = null)
    {
        var client = CreateGrpcClient();
        var startedAt = DateTime.UtcNow;
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);

        while (true)
        {
            var status = await client.GetChainStatusAsync(new Google.Protobuf.WellKnownTypes.Empty()).ResponseAsync.ConfigureAwait(false);
            if (status.BestChainHeight >= minimumHeight)
            {
                return status;
            }

            if (DateTime.UtcNow - startedAt > effectiveTimeout)
            {
                throw new TimeoutException($"Timed out waiting for chain height >= {minimumHeight}.");
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public async Task<TransactionResult> WaitForTransactionStatusAsync(
        Hash transactionId,
        TransactionExecutionStatus expectedStatus,
        TimeSpan? timeout = null)
    {
        var client = CreateGrpcClient();
        var startedAt = DateTime.UtcNow;
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);

        while (true)
        {
            var result = await client.GetTransactionResultAsync(transactionId).ResponseAsync.ConfigureAwait(false);
            if (result.Status == expectedStatus)
            {
                return result;
            }

            if (expectedStatus == TransactionExecutionStatus.Mined &&
                result.Status is TransactionExecutionStatus.Failed or TransactionExecutionStatus.Rejected)
            {
                throw new InvalidOperationException(
                    $"Transaction {transactionId.ToHex()} reached terminal status {result.Status}: {result.Error}");
            }

            if (DateTime.UtcNow - startedAt > effectiveTimeout)
            {
                throw new TimeoutException(
                    $"Timed out waiting for transaction {transactionId.ToHex()} to reach status {expectedStatus}.");
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public async Task<Block> WaitForBlockByHeightAsync(
        long height,
        TimeSpan? timeout = null)
    {
        var client = CreateGrpcClient();
        var startedAt = DateTime.UtcNow;
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);

        while (true)
        {
            try
            {
                return await client.GetBlockByHeightAsync(
                        new Google.Protobuf.WellKnownTypes.Int64Value { Value = height })
                    .ResponseAsync
                    .ConfigureAwait(false);
            }
            catch (Grpc.Core.RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                if (DateTime.UtcNow - startedAt > effectiveTimeout)
                {
                    throw new TimeoutException($"Timed out waiting for block height {height}.", exception);
                }

                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private async Task StartAsync()
    {
        if (_app is not null)
        {
            throw new InvalidOperationException("The test harness has already been started.");
        }

        Directory.CreateDirectory(_rootPath);

        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["NightElf:Launcher:NodeId"] = $"node-{Guid.NewGuid():N}",
            ["NightElf:Launcher:ApiPort"] = GetAvailableTcpPort().ToString(),
            ["NightElf:Launcher:DataRootPath"] = Path.Combine(_rootPath, "data"),
            ["NightElf:Launcher:CheckpointRootPath"] = Path.Combine(_rootPath, "checkpoints"),
            ["NightElf:Launcher:Network:Host"] = "127.0.0.1",
            ["NightElf:Launcher:Network:GrpcPort"] = GetAvailableTcpPort().ToString(),
            ["NightElf:Launcher:Network:QuicPort"] = GetAvailableTcpPort().ToString(),
            ["NightElf:Consensus:Engine"] = "SingleValidator",
            ["NightElf:Consensus:SingleValidator:ValidatorAddress"] = "node-local",
            ["NightElf:Consensus:SingleValidator:BlockInterval"] = "00:00:00.050",
            ["NightElf:TransactionPool:Capacity"] = "4096",
            ["NightElf:TransactionPool:DefaultBatchSize"] = "128",
            ["NightElf:TransactionPool:ReferenceBlockValidPeriod"] = "512"
        };

        foreach (var overrideItem in _configurationOverrides)
        {
            configuration[overrideItem.Key] = overrideItem.Value;
        }

        _app = Program.CreateApp(
            [],
            builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Configuration.AddInMemoryCollection(configuration);
            });

        await _app.StartAsync().ConfigureAwait(false);
    }

    private async Task StopAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.DisposeAsync().AsTask().ConfigureAwait(false);
        _app = null;
    }

    private GrpcChannel CreateChannel()
    {
        var app = _app ?? throw new InvalidOperationException("The test harness has not been started.");
        var server = app.GetTestServer();
        return GrpcChannel.ForAddress(
            server.BaseAddress,
            new GrpcChannelOptions
            {
                HttpHandler = server.CreateHandler()
            });
    }

    private static int GetAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static JsonElement GetJsonProperty(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var camelCaseProperty))
        {
            return camelCaseProperty;
        }

        var pascalCasePropertyName = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (root.TryGetProperty(pascalCasePropertyName, out var pascalCaseProperty))
        {
            return pascalCaseProperty;
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found in deployment JSON '{root}'.");
    }
}

public static class NightElfTransactionTestBuilder
{
    public static SignedTransactionEnvelope CreateSignedTransaction(
        long refBlockNumber,
        string refBlockHash,
        string toAddressHex,
        string methodName,
        byte seedMarker,
        Func<Address, byte[]> payloadFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refBlockHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(toAddressHex);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(payloadFactory);

        var seed = Enumerable.Repeat(seedMarker, 32).ToArray();
        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        var senderAddress = new Address
        {
            Value = ByteString.CopyFrom(publicKey)
        };

        var transaction = new Transaction
        {
            From = senderAddress,
            To = toAddressHex.ToProtoAddress(),
            RefBlockNumber = refBlockNumber,
            RefBlockPrefix = refBlockHash.GetRefBlockPrefix(),
            MethodName = methodName,
            Params = ByteString.CopyFrom(payloadFactory(senderAddress))
        };

        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        var signingHash = transaction.GetSigningHash();
        signer.BlockUpdate(signingHash, 0, signingHash.Length);
        transaction.Signature = ByteString.CopyFrom(signer.GenerateSignature());

        return new SignedTransactionEnvelope(transaction, senderAddress);
    }

    public static Hash ComputeAgentSessionId(
        Address agentAddress,
        long blockHeight,
        int transactionIndex)
    {
        ArgumentNullException.ThrowIfNull(agentAddress);

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

    public static string ComputeBlockHashHex(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return Convert.ToHexString(SHA256.HashData(block.ToByteArray()));
    }

    public static SignedContractDeployEnvelope CreateContractDeployRequest(
        byte[] assemblyBytes,
        byte seedMarker,
        string? contractName = null)
    {
        ArgumentNullException.ThrowIfNull(assemblyBytes);

        var seed = Enumerable.Repeat(seedMarker, 32).ToArray();
        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        var deployerAddress = new Address
        {
            Value = ByteString.CopyFrom(publicKey)
        };

        var signingHash = ChainSettlementSigningHelper.CreateContractDeploySigningHash(assemblyBytes, contractName);
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(signingHash, 0, signingHash.Length);

        var request = new ContractDeployRequest
        {
            AssemblyBytes = ByteString.CopyFrom(assemblyBytes),
            Signature = ByteString.CopyFrom(signer.GenerateSignature()),
            Deployer = deployerAddress,
            ContractName = contractName ?? string.Empty
        };

        return new SignedContractDeployEnvelope(request, deployerAddress);
    }
}

public sealed record SignedTransactionEnvelope(
    Transaction Transaction,
    Address SenderAddress);

public sealed record SignedContractDeployEnvelope(
    ContractDeployRequest Request,
    Address DeployerAddress);
