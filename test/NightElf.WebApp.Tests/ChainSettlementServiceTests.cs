using System.Text.Json;

using Google.Protobuf;

using NightElf.Contracts.System.AgentSession;
using NightElf.Contracts.System.AgentSession.Protobuf;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp.Tests;

public sealed class ChainSettlementServiceTests
{
    [Fact]
    public async Task SubmitTransaction_And_QueryState_Should_Open_Session()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        var settlementClient = harness.CreateChainSettlementClient();
        var chainStatus = await harness.WaitForChainHeightAsync(1);
        var contractAddress = await harness.GetSystemContractAddressAsync("AgentSession");
        var transactionEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            chainStatus.BestChainHeight,
            chainStatus.BestChainHash.ToHex(),
            contractAddress,
            "OpenSession",
            seedMarker: 0x91,
            payloadFactory: senderAddress => OpenSessionInput.Encode(new OpenSessionInput
            {
                AgentAddress = senderAddress,
                TokenBudget = 64
            }));

        var submitResult = await settlementClient.SubmitTransactionAsync(transactionEnvelope.Transaction).ResponseAsync;
        var minedResult = await harness.WaitForTransactionStatusAsync(
            submitResult.TransactionId,
            TransactionExecutionStatus.Mined);
        var block = await harness.WaitForBlockByHeightAsync(minedResult.BlockHeight);
        var transactionIndex = block.Body.TransactionIds
            .Select((transactionId, index) => new { TransactionId = transactionId.ToHex(), Index = index })
            .Single(item => item.TransactionId == submitResult.TransactionId.ToHex())
            .Index;
        var sessionId = NightElfTransactionTestBuilder.ComputeAgentSessionId(
            transactionEnvelope.SenderAddress,
            minedResult.BlockHeight,
            transactionIndex);

        var stateResult = await settlementClient.QueryStateAsync(new StateQuery
        {
            Key = $"session:{sessionId.ToHex()}"
        }).ResponseAsync;
        var sessionState = SessionState.Parser.ParseFrom(stateResult.Value);

        Assert.Equal(TransactionExecutionStatus.Pending, submitResult.Status);
        Assert.True(stateResult.Found);
        Assert.True(stateResult.IsVersioned);
        Assert.False(stateResult.IsDeleted);
        Assert.Equal(minedResult.BlockHeight, stateResult.BlockHeight);
        Assert.Equal(64, sessionState.TokenBudget);
        Assert.Equal(transactionEnvelope.SenderAddress.ToHex(), sessionState.AgentAddress.ToHex());
    }

    [Fact]
    public async Task DeployContract_Should_Persist_Assembly_And_Record_Transaction_Result()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        var settlementClient = harness.CreateChainSettlementClient();
        await harness.WaitForChainHeightAsync(1);
        var assemblyBytes = await File.ReadAllBytesAsync(typeof(AgentSessionContract).Assembly.Location);
        var deployEnvelope = NightElfTransactionTestBuilder.CreateContractDeployRequest(
            assemblyBytes,
            seedMarker: 0xA1,
            contractName: "DynamicAgentSession");

        var deployResult = await settlementClient.DeployContractAsync(deployEnvelope.Request).ResponseAsync;
        var storedTransactionResult = await harness.WaitForTransactionStatusAsync(
            deployResult.TransactionId,
            TransactionExecutionStatus.Mined);
        var assemblyState = await settlementClient.QueryStateAsync(new StateQuery
        {
            Key = ChainSettlementStateKeys.GetContractAssemblyKey(deployResult.ContractAddress.ToHex())
        }).ResponseAsync;
        var metadataState = await settlementClient.QueryStateAsync(new StateQuery
        {
            Key = ChainSettlementStateKeys.GetContractMetadataKey(deployResult.ContractAddress.ToHex())
        }).ResponseAsync;
        using var metadataDocument = JsonDocument.Parse(metadataState.Value.ToByteArray());

        Assert.Equal(TransactionExecutionStatus.Mined, deployResult.Status);
        Assert.False(deployResult.ContractAddress.Value.IsEmpty);
        Assert.Equal(deployResult.BlockHeight, storedTransactionResult.BlockHeight);
        Assert.True(assemblyState.Found);
        Assert.True(assemblyState.IsVersioned);
        Assert.Equal(assemblyBytes, assemblyState.Value.ToByteArray());
        Assert.True(metadataState.Found);
        Assert.Equal("DynamicAgentSession", GetJsonProperty(metadataDocument.RootElement, "contractName").GetString());
        Assert.Equal(deployResult.ContractAddress.ToHex(), GetJsonProperty(metadataDocument.RootElement, "contractAddressHex").GetString());
        Assert.Equal(deployResult.TransactionId.ToHex(), GetJsonProperty(metadataDocument.RootElement, "transactionId").GetString());
        Assert.Equal(deployResult.CodeHash, GetJsonProperty(metadataDocument.RootElement, "codeHash").GetString());
    }

    [Fact]
    public async Task SubscribeEvents_Should_Stream_Deployment_And_Transaction_Result_Events()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        var commandClient = harness.CreateChainSettlementClient();
        var streamingClient = harness.CreateChainSettlementClient();
        await harness.WaitForChainHeightAsync(1);
        var assemblyBytes = await File.ReadAllBytesAsync(typeof(AgentSessionContract).Assembly.Location);
        var deployEnvelope = NightElfTransactionTestBuilder.CreateContractDeployRequest(
            assemblyBytes,
            seedMarker: 0xB1,
            contractName: "StreamedAgentSession");

        var deployResult = await commandClient.DeployContractAsync(deployEnvelope.Request).ResponseAsync;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var call = streamingClient.SubscribeEvents(
            new EventFilter
            {
                EventTypes =
                {
                    ChainEventType.ContractDeployed,
                    ChainEventType.TransactionResult
                },
                TransactionId = deployResult.TransactionId
            },
            cancellationToken: cts.Token);
        _ = await call.ResponseHeadersAsync;

        var receivedEvents = new List<ChainEvent>(2);
        while (receivedEvents.Count < 2 && await call.ResponseStream.MoveNext(cts.Token))
        {
            var current = call.ResponseStream.Current;
            receivedEvents.Add(current);
        }

        Assert.Contains(
            receivedEvents,
            chainEvent => chainEvent.EventType == ChainEventType.ContractDeployed &&
                          chainEvent.ContractAddress.ToHex() == deployResult.ContractAddress.ToHex());
        Assert.Contains(
            receivedEvents,
            chainEvent => chainEvent.EventType == ChainEventType.TransactionResult &&
                          chainEvent.TransactionId.ToHex() == deployResult.TransactionId.ToHex());
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
