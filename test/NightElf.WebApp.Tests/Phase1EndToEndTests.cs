using System.Diagnostics;

using Google.Protobuf;
using Xunit.Abstractions;

using NightElf.Contracts.System.AgentSession.Protobuf;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp.Tests;

public sealed class Phase1EndToEndTests
{
    private readonly ITestOutputHelper _output;

    public Phase1EndToEndTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AgentSession_Flow_Should_Execute_And_Recover_After_Restart()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        var client = harness.CreateGrpcClient();
        var genesisStatus = await harness.WaitForChainHeightAsync(1);
        var contractAddress = await harness.GetSystemContractAddressAsync("AgentSession");

        var openEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            genesisStatus.BestChainHeight,
            genesisStatus.BestChainHash.ToHex(),
            contractAddress,
            "OpenSession",
            seedMarker: 0x51,
            payloadFactory: senderAddress => OpenSessionInput.Encode(new OpenSessionInput
            {
                AgentAddress = senderAddress,
                TokenBudget = 100
            }));
        var openSubmit = await client.SubmitTransactionAsync(openEnvelope.Transaction).ResponseAsync;
        var openMined = await harness.WaitForTransactionStatusAsync(openSubmit.TransactionId, TransactionExecutionStatus.Mined);
        var sessionId = await ResolveSessionIdAsync(harness, openEnvelope.SenderAddress, openMined);
        var openedState = await harness.GetSessionStateAsync(sessionId);

        Assert.NotNull(openedState);
        Assert.Equal(100, openedState!.TokenBudget);

        var recordRef = await harness.WaitForChainHeightAsync(openMined.BlockHeight);
        var recordEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            recordRef.BestChainHeight,
            recordRef.BestChainHash.ToHex(),
            contractAddress,
            "RecordStep",
            seedMarker: 0x51,
            payloadFactory: _ => RecordStepInput.Encode(new RecordStepInput
            {
                SessionId = sessionId,
                StepContentHash = "AA".PadLeft(64, '0').ToProtoHash(),
                InputTokens = 20,
                OutputTokens = 15
            }));
        var recordSubmit = await client.SubmitTransactionAsync(recordEnvelope.Transaction).ResponseAsync;
        var recordMined = await harness.WaitForTransactionStatusAsync(recordSubmit.TransactionId, TransactionExecutionStatus.Mined);
        var recordedState = await harness.GetSessionStateAsync(sessionId);

        Assert.Equal(20, recordedState!.InputTokensConsumed);
        Assert.Equal(15, recordedState.OutputTokensConsumed);

        var overBudgetRef = await harness.WaitForChainHeightAsync(recordMined.BlockHeight);
        var overBudgetEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            overBudgetRef.BestChainHeight,
            overBudgetRef.BestChainHash.ToHex(),
            contractAddress,
            "RecordStep",
            seedMarker: 0x51,
            payloadFactory: _ => RecordStepInput.Encode(new RecordStepInput
            {
                SessionId = sessionId,
                StepContentHash = "BB".PadLeft(64, '0').ToProtoHash(),
                InputTokens = 80,
                OutputTokens = 20
            }));
        var overBudgetSubmit = await client.SubmitTransactionAsync(overBudgetEnvelope.Transaction).ResponseAsync;
        var overBudgetFailed = await harness.WaitForTransactionStatusAsync(overBudgetSubmit.TransactionId, TransactionExecutionStatus.Failed);
        var stateAfterFailure = await harness.GetSessionStateAsync(sessionId);

        Assert.Contains("budget", overBudgetFailed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20, stateAfterFailure!.InputTokensConsumed);
        Assert.Equal(15, stateAfterFailure.OutputTokensConsumed);

        var finalizeRef = await harness.WaitForChainHeightAsync(recordMined.BlockHeight);
        var finalizeEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            finalizeRef.BestChainHeight,
            finalizeRef.BestChainHash.ToHex(),
            contractAddress,
            "FinalizeSession",
            seedMarker: 0x51,
            payloadFactory: _ => FinalizeSessionInput.Encode(new FinalizeSessionInput
            {
                SessionId = sessionId
            }));
        var finalizeSubmit = await client.SubmitTransactionAsync(finalizeEnvelope.Transaction).ResponseAsync;
        var finalizeMined = await harness.WaitForTransactionStatusAsync(finalizeSubmit.TransactionId, TransactionExecutionStatus.Mined);
        var finalizedState = await harness.GetSessionStateAsync(sessionId);

        Assert.True(finalizedState!.IsFinalized);
        Assert.Equal(openEnvelope.SenderAddress.ToHex(), finalizedState.FinalizedBy);

        var bestBeforeRestart = await harness.WaitForChainHeightAsync(finalizeMined.BlockHeight);
        await harness.RestartAsync();

        var recoveredStatus = await harness.WaitForChainHeightAsync(bestBeforeRestart.BestChainHeight);
        var recoveredState = await harness.GetSessionStateAsync(sessionId);
        var recoveredFinalizeResult = await harness.WaitForTransactionStatusAsync(
            finalizeSubmit.TransactionId,
            TransactionExecutionStatus.Mined);

        Assert.True(recoveredStatus.BestChainHeight >= bestBeforeRestart.BestChainHeight);
        Assert.NotNull(recoveredState);
        Assert.True(recoveredState!.IsFinalized);
        Assert.Equal(20, recoveredState.InputTokensConsumed);
        Assert.Equal(15, recoveredState.OutputTokensConsumed);
        Assert.Equal(finalizeMined.BlockHeight, recoveredFinalizeResult.BlockHeight);
        Assert.Equal(TransactionExecutionStatus.Mined, recoveredFinalizeResult.Status);
    }

    [Fact]
    public async Task SubmitTransaction_Should_Reject_Expired_RefBlock()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync(
            configurationOverrides: new Dictionary<string, string?>
            {
                ["NightElf:TransactionPool:ReferenceBlockValidPeriod"] = "1",
                ["NightElf:Consensus:SingleValidator:BlockInterval"] = "00:00:00.030"
            });
        var client = harness.CreateGrpcClient();
        await harness.WaitForChainHeightAsync(1);
        var contractAddress = await harness.GetSystemContractAddressAsync("AgentSession");
        var genesisBlock = await harness.WaitForBlockByHeightAsync(1);
        await harness.WaitForChainHeightAsync(2);

        var expiredEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            genesisBlock.Header.Height,
            NightElfTransactionTestBuilder.ComputeBlockHashHex(genesisBlock),
            contractAddress,
            "OpenSession",
            seedMarker: 0x61,
            payloadFactory: senderAddress => OpenSessionInput.Encode(new OpenSessionInput
            {
                AgentAddress = senderAddress,
                TokenBudget = 10
            }));

        var result = await client.SubmitTransactionAsync(expiredEnvelope.Transaction).ResponseAsync;

        Assert.Equal(TransactionExecutionStatus.Rejected, result.Status);
        Assert.Contains("expired", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenSession_Batch_Should_Report_SingleNode_Tps_Baseline()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        var client = harness.CreateGrpcClient();
        await harness.WaitForChainHeightAsync(1);
        var contractAddress = await harness.GetSystemContractAddressAsync("AgentSession");
        var reference = await harness.WaitForChainHeightAsync(1);

        const int transactionCount = 16;
        var envelopes = Enumerable.Range(0, transactionCount)
            .Select(index => NightElfTransactionTestBuilder.CreateSignedTransaction(
                reference.BestChainHeight,
                reference.BestChainHash.ToHex(),
                contractAddress,
                "OpenSession",
                seedMarker: (byte)(0x70 + index),
                payloadFactory: senderAddress => OpenSessionInput.Encode(new OpenSessionInput
                {
                    AgentAddress = senderAddress,
                    TokenBudget = 25 + index
                })))
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        var submitResults = new List<TransactionResult>(transactionCount);
        foreach (var envelope in envelopes)
        {
            submitResults.Add(await client.SubmitTransactionAsync(envelope.Transaction).ResponseAsync);
        }

        foreach (var submitResult in submitResults)
        {
            await harness.WaitForTransactionStatusAsync(submitResult.TransactionId, TransactionExecutionStatus.Mined);
        }

        stopwatch.Stop();
        var tps = transactionCount / stopwatch.Elapsed.TotalSeconds;

        _output.WriteLine(
            "Single-node TPS baseline: {0:F2} tx/s over {1} OpenSession transactions in {2:F3}s.",
            tps,
            transactionCount,
            stopwatch.Elapsed.TotalSeconds);

        Assert.True(tps > 0);
    }

    private static async Task<Hash> ResolveSessionIdAsync(
        NightElfNodeTestHarness harness,
        Address senderAddress,
        TransactionResult minedResult)
    {
        var block = await harness.WaitForBlockByHeightAsync(minedResult.BlockHeight);
        var transactionIndex = block.Body.TransactionIds
            .Select((transactionId, index) => new { TransactionId = transactionId.ToHex(), Index = index })
            .Single(item => item.TransactionId == minedResult.TransactionId.ToHex())
            .Index;

        return NightElfTransactionTestBuilder.ComputeAgentSessionId(
            senderAddress,
            minedResult.BlockHeight,
            transactionIndex);
    }
}
