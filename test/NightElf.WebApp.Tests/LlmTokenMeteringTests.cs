using System.Text;

using NightElf.Contracts.System.AgentSession;
using NightElf.Contracts.System.AgentSession.Protobuf;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp.Tests;

public sealed class LlmTokenMeteringTests
{
    [Fact]
    public void LocalModelInferenceInterceptor_Should_Count_Tokens_As_Verified()
    {
        var interceptor = new LocalModelInferenceInterceptor(new WhitespaceTextTokenizer());

        var reading = interceptor.Intercept("alpha beta gamma", "delta epsilon");

        Assert.Equal(3, reading.InputTokens);
        Assert.Equal(2, reading.OutputTokens);
        Assert.Equal(MeteringSource.Verified, reading.Source);
        Assert.Equal(MeteringSourceExtensions.VerifiedConfidenceWeightBasisPoints, reading.ConfidenceWeightBasisPoints);
        Assert.Equal(5, reading.TotalTokens);
        Assert.Equal(5, reading.WeightedTokens);
    }

    [Fact]
    public void OpenAiUsageExtractor_Should_Read_Usage_As_SelfReported()
    {
        var extractor = new OpenAiUsageExtractor();

        var reading = extractor.Extract(
            Encoding.UTF8.GetBytes("""
                                   {
                                     "usage": {
                                       "prompt_tokens": 7,
                                       "completion_tokens": 5,
                                       "total_tokens": 12
                                     }
                                   }
                                   """));

        Assert.Equal(7, reading.InputTokens);
        Assert.Equal(5, reading.OutputTokens);
        Assert.Equal(MeteringSource.SelfReported, reading.Source);
        Assert.Equal(MeteringSourceExtensions.SelfReportedConfidenceWeightBasisPoints, reading.ConfidenceWeightBasisPoints);
        Assert.Equal(12, reading.TotalTokens);
        Assert.Equal(6, reading.WeightedTokens);
    }

    [Fact]
    public async Task Verified_And_SelfReported_Metering_Should_Update_State_And_Stream_Distinct_Events()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        var settlementClient = harness.CreateChainSettlementClient();
        var localInterceptor = harness.GetRequiredService<ILocalModelInferenceInterceptor>();
        var remoteExtractor = harness.GetRequiredService<IRemoteApiUsageExtractor>();
        var chainStatus = await harness.WaitForChainHeightAsync(1);
        var contractAddress = await harness.GetSystemContractAddressAsync("AgentSession");

        var openEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            chainStatus.BestChainHeight,
            chainStatus.BestChainHash.ToHex(),
            contractAddress,
            "OpenSession",
            seedMarker: 0xC1,
            payloadFactory: senderAddress => OpenSessionInput.Encode(new OpenSessionInput
            {
                AgentAddress = senderAddress,
                TokenBudget = 100
            }));
        var openSubmit = await settlementClient.SubmitTransactionAsync(openEnvelope.Transaction).ResponseAsync;
        var openMined = await harness.WaitForTransactionStatusAsync(openSubmit.TransactionId, TransactionExecutionStatus.Mined);
        var sessionId = await ResolveSessionIdAsync(harness, openEnvelope.SenderAddress, openMined);

        var verifiedReading = localInterceptor.Intercept("alpha beta gamma", "delta epsilon");
        var verifiedReference = await harness.WaitForChainHeightAsync(openMined.BlockHeight);
        var verifiedStepHash = "A1".PadLeft(64, '0').ToProtoHash();
        var verifiedEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            verifiedReference.BestChainHeight,
            verifiedReference.BestChainHash.ToHex(),
            contractAddress,
            "RecordStep",
            seedMarker: 0xC1,
            payloadFactory: _ => RecordStepInput.Encode(new RecordStepInput
            {
                SessionId = sessionId,
                StepContentHash = verifiedStepHash,
                InputTokens = verifiedReading.InputTokens,
                OutputTokens = verifiedReading.OutputTokens,
                MeteringSource = verifiedReading.Source
            }));
        var verifiedSubmit = await settlementClient.SubmitTransactionAsync(verifiedEnvelope.Transaction).ResponseAsync;
        await harness.WaitForTransactionStatusAsync(verifiedSubmit.TransactionId, TransactionExecutionStatus.Mined);

        var remoteReading = remoteExtractor.Extract(
            Encoding.UTF8.GetBytes("""
                                   {
                                     "usage": {
                                       "prompt_tokens": 7,
                                       "completion_tokens": 5,
                                       "total_tokens": 12
                                     }
                                   }
                                   """));
        var remoteReference = await harness.WaitForChainHeightAsync(2);
        var remoteStepHash = "B2".PadLeft(64, '0').ToProtoHash();
        var remoteEnvelope = NightElfTransactionTestBuilder.CreateSignedTransaction(
            remoteReference.BestChainHeight,
            remoteReference.BestChainHash.ToHex(),
            contractAddress,
            "RecordStep",
            seedMarker: 0xC1,
            payloadFactory: _ => RecordStepInput.Encode(new RecordStepInput
            {
                SessionId = sessionId,
                StepContentHash = remoteStepHash,
                InputTokens = remoteReading.InputTokens,
                OutputTokens = remoteReading.OutputTokens,
                MeteringSource = remoteReading.Source
            }));
        var remoteSubmit = await settlementClient.SubmitTransactionAsync(remoteEnvelope.Transaction).ResponseAsync;
        await harness.WaitForTransactionStatusAsync(remoteSubmit.TransactionId, TransactionExecutionStatus.Mined);

        var stateResult = await settlementClient.QueryStateAsync(new StateQuery
        {
            Key = $"session:{sessionId.ToHex()}"
        }).ResponseAsync;
        var sessionState = SessionState.Parser.ParseFrom(stateResult.Value);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var call = settlementClient.SubscribeEvents(
            new EventFilter
            {
                EventTypes =
                {
                    ChainEventType.TokenMetered
                },
                ContractAddress = contractAddress.ToProtoAddress()
            },
            cancellationToken: cts.Token);
        _ = await call.ResponseHeadersAsync;

        var meteringEvents = new List<ChainEvent>(2);
        while (meteringEvents.Count < 2 && await call.ResponseStream.MoveNext(cts.Token))
        {
            meteringEvents.Add(call.ResponseStream.Current);
        }

        Assert.True(stateResult.Found);
        Assert.True(stateResult.IsVersioned);
        Assert.Equal(10, sessionState.InputTokensConsumed);
        Assert.Equal(7, sessionState.OutputTokensConsumed);
        Assert.Equal(3, sessionState.VerifiedInputTokensConsumed);
        Assert.Equal(2, sessionState.VerifiedOutputTokensConsumed);
        Assert.Equal(7, sessionState.SelfReportedInputTokensConsumed);
        Assert.Equal(5, sessionState.SelfReportedOutputTokensConsumed);
        Assert.Equal(11, sessionState.WeightedTokensConsumed);

        Assert.Contains(
            meteringEvents,
            chainEvent =>
            {
                var payload = StepRecorded.Parser.ParseFrom(chainEvent.Payload);
                return chainEvent.EventType == ChainEventType.TokenMetered &&
                       chainEvent.TransactionId.ToHex() == verifiedSubmit.TransactionId.ToHex() &&
                       chainEvent.StateKey == $"session:{sessionId.ToHex()}:event:step:{verifiedStepHash.ToHex()}" &&
                       payload.MeteringSource == MeteringSource.Verified &&
                       payload.ConfidenceWeightBasisPoints == MeteringSourceExtensions.VerifiedConfidenceWeightBasisPoints &&
                       payload.WeightedTokens == verifiedReading.WeightedTokens &&
                       payload.WeightedTokensConsumed == verifiedReading.WeightedTokens;
            });
        Assert.Contains(
            meteringEvents,
            chainEvent =>
            {
                var payload = StepRecorded.Parser.ParseFrom(chainEvent.Payload);
                return chainEvent.EventType == ChainEventType.TokenMetered &&
                       chainEvent.TransactionId.ToHex() == remoteSubmit.TransactionId.ToHex() &&
                       chainEvent.StateKey == $"session:{sessionId.ToHex()}:event:step:{remoteStepHash.ToHex()}" &&
                       payload.MeteringSource == MeteringSource.SelfReported &&
                       payload.ConfidenceWeightBasisPoints == MeteringSourceExtensions.SelfReportedConfidenceWeightBasisPoints &&
                       payload.WeightedTokens == remoteReading.WeightedTokens &&
                       payload.WeightedTokensConsumed == sessionState.WeightedTokensConsumed;
            });
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
