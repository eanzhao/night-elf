using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp.Tests;

public sealed class NightElfNodeServiceTests : IClassFixture<NightElfNodeWebApplicationFactory>
{
    private readonly NightElfNodeWebApplicationFactory _factory;

    public NightElfNodeServiceTests(NightElfNodeWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetChainStatus_And_GetBlockByHeight_Should_Return_Genesis_Block()
    {
        var client = _factory.CreateGrpcClient();
        var chainStatus = await WaitForChainHeightAsync(client, 1);
        var block = await WaitForBlockByHeightAsync(client, 1);

        Assert.True(chainStatus.BestChainHeight >= 1);
        Assert.False(chainStatus.BestChainHash.Value.IsEmpty);
        Assert.Equal(1, block.Header.Height);
    }

    [Fact]
    public async Task SubmitTransaction_And_GetTransactionResult_Should_Return_Pending_Then_Mined()
    {
        var client = _factory.CreateGrpcClient();
        var chainStatus = await WaitForChainHeightAsync(client, 1);
        var transaction = CreateSignedTransaction(
            chainStatus.BestChainHeight,
            chainStatus.BestChainHash.ToHex(),
            "Transfer",
            seedMarker: 0x31);

        var submitResult = await client.SubmitTransactionAsync(transaction).ResponseAsync;
        var minedResult = await WaitForTransactionStatusAsync(
            client,
            submitResult.TransactionId,
            TransactionExecutionStatus.Mined);
        var block = await WaitForBlockByHeightAsync(client, minedResult.BlockHeight);

        Assert.Equal(TransactionExecutionStatus.Pending, submitResult.Status);
        Assert.Equal(transaction.GetTransactionId(), submitResult.TransactionId.ToHex());
        Assert.Equal(TransactionExecutionStatus.Mined, minedResult.Status);
        Assert.Equal(minedResult.TransactionId.ToHex(), block.Body.TransactionIds.Single().ToHex());
    }

    [Fact]
    public async Task SubmitTransaction_Should_Reject_Invalid_Signature()
    {
        var client = _factory.CreateGrpcClient();
        var chainStatus = await WaitForChainHeightAsync(client, 1);
        var transaction = CreateSignedTransaction(
            chainStatus.BestChainHeight,
            chainStatus.BestChainHash.ToHex(),
            "Transfer",
            seedMarker: 0x41);
        transaction.Signature = ByteString.CopyFrom(
            transaction.Signature.ToByteArray().Select(static value => (byte)(value ^ 0xFF)).ToArray());

        var result = await client.SubmitTransactionAsync(transaction).ResponseAsync;

        Assert.Equal(TransactionExecutionStatus.Rejected, result.Status);
        Assert.Contains("Ed25519", result.Error, StringComparison.Ordinal);
    }

    private static async Task<ChainStatus> WaitForChainHeightAsync(
        NightElfNode.NightElfNodeClient client,
        long minimumHeight)
    {
        var startedAt = DateTime.UtcNow;

        while (true)
        {
            var status = await client.GetChainStatusAsync(new Empty()).ResponseAsync;
            if (status.BestChainHeight >= minimumHeight)
            {
                return status;
            }

            if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException($"Timed out waiting for chain height >= {minimumHeight}.");
            }

            await Task.Delay(50);
        }
    }

    private static async Task<TransactionResult> WaitForTransactionStatusAsync(
        NightElfNode.NightElfNodeClient client,
        Hash transactionId,
        TransactionExecutionStatus expectedStatus)
    {
        var startedAt = DateTime.UtcNow;

        while (true)
        {
            var result = await client.GetTransactionResultAsync(transactionId).ResponseAsync;
            if (result.Status == expectedStatus)
            {
                return result;
            }

            if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException(
                    $"Timed out waiting for transaction {transactionId.ToHex()} to reach status {expectedStatus}.");
            }

            await Task.Delay(50);
        }
    }

    private static async Task<Block> WaitForBlockByHeightAsync(
        NightElfNode.NightElfNodeClient client,
        long height)
    {
        var startedAt = DateTime.UtcNow;

        while (true)
        {
            try
            {
                return await client.GetBlockByHeightAsync(new Int64Value { Value = height }).ResponseAsync;
            }
            catch (Grpc.Core.RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException($"Timed out waiting for block height {height} to become readable.", exception);
                }

                await Task.Delay(50);
            }
        }
    }

    private static Transaction CreateSignedTransaction(
        long refBlockNumber,
        string refBlockHash,
        string methodName,
        byte seedMarker)
    {
        var seed = Enumerable.Repeat(seedMarker, 32).ToArray();
        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();

        var transaction = new Transaction
        {
            From = new Address
            {
                Value = ByteString.CopyFrom(publicKey)
            },
            To = new Address
            {
                Value = ByteString.CopyFrom(Enumerable.Repeat((byte)(seedMarker + 1), 32).ToArray())
            },
            RefBlockNumber = refBlockNumber,
            RefBlockPrefix = refBlockHash.GetRefBlockPrefix(),
            MethodName = methodName,
            Params = ByteString.CopyFromUtf8($"payload:{seedMarker:X2}")
        };

        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        var signingHash = transaction.GetSigningHash();
        signer.BlockUpdate(signingHash, 0, signingHash.Length);
        transaction.Signature = ByteString.CopyFrom(signer.GenerateSignature());
        return transaction;
    }
}

public sealed class NightElfNodeWebApplicationFactory : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "nightelf-webapp-tests",
        Guid.NewGuid().ToString("N"));
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_rootPath);

        _app = Program.CreateApp(
            [],
            builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NightElf:Launcher:DataRootPath"] = Path.Combine(_rootPath, "data"),
                    ["NightElf:Launcher:CheckpointRootPath"] = Path.Combine(_rootPath, "checkpoints"),
                    ["NightElf:Consensus:Engine"] = "SingleValidator",
                    ["NightElf:Consensus:SingleValidator:ValidatorAddress"] = "node-local",
                    ["NightElf:Consensus:SingleValidator:BlockInterval"] = "00:00:00.100"
                });
            });

        await _app.StartAsync();
    }

    public NightElfNode.NightElfNodeClient CreateGrpcClient()
    {
        var app = _app ?? throw new InvalidOperationException("Test host has not been initialized.");
        var server = app.GetTestServer();
        var channel = GrpcChannel.ForAddress(
            server.BaseAddress,
            new GrpcChannelOptions
            {
                HttpHandler = server.CreateHandler()
            });

        return new NightElfNode.NightElfNodeClient(channel);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().AsTask();
        }

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
}
