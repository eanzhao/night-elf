using System.Text.Json;

using Google.Protobuf;

using NightElf.DynamicContracts;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;
using Xunit;

namespace NightElf.WebApp.Tests;

public sealed class DynamicContractDeploymentServiceTests
{
    [Fact]
    public async Task DeployDynamicAsync_Should_Compile_Analyze_And_Persist_Assembly()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        await harness.WaitForChainHeightAsync(1);

        var buildService = harness.GetRequiredService<DynamicContractBuildService>();
        var deploymentService = harness.GetRequiredService<ContractDeploymentService>();
        var settlementClient = harness.CreateChainSettlementClient();
        var spec = CreateGreetingSpec();
        var artifact = await buildService.BuildAsync(spec);
        var deployEnvelope = NightElfTransactionTestBuilder.CreateDynamicContractDeployRequest(spec, seedMarker: 0xC7);

        var deployResult = await deploymentService.DeployDynamicAsync(deployEnvelope.Request);
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
        Assert.True(assemblyState.Found);
        var storedBytes = assemblyState.Value.ToByteArray();
        Assert.NotEmpty(storedBytes);
        Assert.Equal(deployResult.CodeHash, ChainSettlementSigningHelper.CreateCodeHash(storedBytes));
        Assert.NotNull(artifact);
        Assert.Equal("GreetingContract", GetJsonProperty(metadataDocument.RootElement, "contractName").GetString());
        Assert.Equal(deployResult.ContractAddress.ToHex(), GetJsonProperty(metadataDocument.RootElement, "contractAddressHex").GetString());
    }

    [Fact]
    public async Task DeployDynamicAsync_Should_Persist_Owning_Treaty_Metadata_When_Provided()
    {
        await using var harness = await NightElfNodeTestHarness.CreateAsync();
        await harness.WaitForChainHeightAsync(1);

        var deploymentService = harness.GetRequiredService<ContractDeploymentService>();
        var settlementClient = harness.CreateChainSettlementClient();
        var spec = CreateGreetingSpec();
        var treatyId = CreateHash("AB".PadLeft(64, 'A'));
        var deployEnvelope = NightElfTransactionTestBuilder.CreateDynamicContractDeployRequest(spec, seedMarker: 0xC8, treatyId: treatyId);

        var deployResult = await deploymentService.DeployDynamicAsync(deployEnvelope.Request);
        var metadataState = await settlementClient.QueryStateAsync(new StateQuery
        {
            Key = ChainSettlementStateKeys.GetContractMetadataKey(deployResult.ContractAddress.ToHex())
        }).ResponseAsync;
        var owningTreatyState = await settlementClient.QueryStateAsync(new StateQuery
        {
            Key = ChainSettlementStateKeys.GetContractOwningTreatyKey(deployResult.ContractAddress.ToHex())
        }).ResponseAsync;
        using var metadataDocument = JsonDocument.Parse(metadataState.Value.ToByteArray());

        Assert.Equal(TransactionExecutionStatus.Mined, deployResult.Status);
        Assert.True(owningTreatyState.Found);
        Assert.Equal(treatyId.ToHex(), owningTreatyState.Value.ToStringUtf8());
        Assert.True(GetJsonProperty(metadataDocument.RootElement, "isDynamicContract").GetBoolean());
        Assert.Equal(treatyId.ToHex(), GetJsonProperty(metadataDocument.RootElement, "owningTreatyId").GetString());
    }

    private static ContractSpec CreateGreetingSpec()
    {
        return new ContractSpec
        {
            ContractName = "GreetingContract",
            Namespace = "NightElf.DynamicContracts.Integration",
            Types =
            [
                new ContractTypeSpec
                {
                    Name = "GreetingInput",
                    Fields = [new ContractFieldSpec { Name = "Name", Type = ContractPrimitiveType.String }]
                },
                new ContractTypeSpec
                {
                    Name = "GreetingOutput",
                    Fields = [new ContractFieldSpec { Name = "Message", Type = ContractPrimitiveType.String }]
                }
            ],
            Methods =
            [
                new ContractMethodSpec
                {
                    Name = "Greet",
                    InputType = "GreetingInput",
                    OutputType = "GreetingOutput",
                    LogicBlocks =
                    [
                        new ContractLogicBlockSpec
                        {
                            Kind = ContractLogicBlockKind.ConcatStrings,
                            OutputField = "Message",
                            Segments =
                            [
                                new ContractStringSegmentSpec
                                {
                                    Kind = ContractStringSegmentKind.Literal,
                                    Value = "hello "
                                },
                                new ContractStringSegmentSpec
                                {
                                    Kind = ContractStringSegmentKind.InputField,
                                    Value = "Name"
                                }
                            ]
                        }
                    ]
                }
            ]
        };
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

    private static Hash CreateHash(string hex)
    {
        return new Hash
        {
            Value = ByteString.CopyFrom(Convert.FromHexString(hex))
        };
    }
}
