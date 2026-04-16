using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using NightElf.Contracts.System.AgentSession.Protobuf;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp.Tests;

public sealed class NightElfNodeServiceTests
{
    [Fact]
    public async Task GetChainStatus_And_GetBlockByHeight_Should_Return_Genesis_Block()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();

        var chainStatus = await harness.WaitForChainHeightAsync(1);
        var block = await harness.WaitForBlockByHeightAsync(1);

        Assert.True(chainStatus.BestChainHeight >= 1);
        Assert.False(chainStatus.BestChainHash.Value.IsEmpty);
        Assert.Equal(1, block.Header.Height);
    }

    [Fact]
    public async Task SubmitTransaction_And_GetTransactionResult_Should_Execute_OpenSession()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        var client = harness.CreateGrpcClient();
        var chainStatus = await harness.WaitForChainHeightAsync(1);
        var contractAddress = await harness.GetSystemContractAddressAsync("AgentSession");
        var transactionEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            chainStatus.BestChainHeight,
            chainStatus.BestChainHash.ToHex(),
            contractAddress,
            "OpenSession",
            seedMarker: 0x31,
            payloadFactory: senderAddress => OpenSessionInput.Encode(new OpenSessionInput
            {
                AgentAddress = senderAddress,
                TokenBudget = 42
            }));

        var submitResult = await client.SubmitTransactionAsync(transactionEnvelope.Transaction).ResponseAsync;
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
        var sessionState = await harness.GetSessionStateAsync(sessionId);

        Assert.Equal(TransactionExecutionStatus.Pending, submitResult.Status);
        Assert.Equal(transactionEnvelope.Transaction.GetTransactionId(), submitResult.TransactionId.ToHex());
        Assert.Equal(TransactionExecutionStatus.Mined, minedResult.Status);
        Assert.NotNull(sessionState);
        Assert.Equal(42, sessionState!.TokenBudget);
        Assert.Equal(transactionEnvelope.SenderAddress.ToHex(), sessionState.AgentAddress.ToHex());
    }

    [Fact]
    public async Task SubmitTransaction_Should_Reject_Invalid_Signature()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        var client = harness.CreateGrpcClient();
        var chainStatus = await harness.WaitForChainHeightAsync(1);
        var contractAddress = await harness.GetSystemContractAddressAsync("AgentSession");
        var transactionEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            chainStatus.BestChainHeight,
            chainStatus.BestChainHash.ToHex(),
            contractAddress,
            "OpenSession",
            seedMarker: 0x41,
            payloadFactory: senderAddress => OpenSessionInput.Encode(new OpenSessionInput
            {
                AgentAddress = senderAddress,
                TokenBudget = 50
            }));
        transactionEnvelope.Transaction.Signature = ByteString.CopyFrom(
            transactionEnvelope.Transaction.Signature.ToByteArray().Select(static value => (byte)(value ^ 0xFF)).ToArray());

        var result = await client.SubmitTransactionAsync(transactionEnvelope.Transaction).ResponseAsync;

        Assert.Equal(TransactionExecutionStatus.Rejected, result.Status);
        Assert.Contains("Ed25519", result.Error, StringComparison.Ordinal);
    }
}
