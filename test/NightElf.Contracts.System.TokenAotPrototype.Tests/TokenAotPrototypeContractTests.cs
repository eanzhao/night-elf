using NightElf.Contracts.System.TokenAotPrototype;
using NightElf.Kernel.SmartContract;
using NightElf.Sdk.CSharp;

namespace NightElf.Contracts.System.TokenAotPrototype.Tests;

public sealed class TokenAotPrototypeContractTests
{
    [Fact]
    public void TokenAotPrototype_Should_Mint_And_Read_Balance_Through_Generated_Dispatch()
    {
        var executor = new SmartContractExecutor();
        var contract = new TokenAotPrototypeContract();

        executor.Execute(
            contract,
            new ContractInvocation("Mint", MintInput.Encode(new MintInput("alice", 42))));

        var result = executor.Execute(
            contract,
            new ContractInvocation("GetBalance", BalanceInput.Encode(new BalanceInput("alice"))));

        Assert.Equal(42, BalanceOutput.Decode(result).Amount);
    }

    [Fact]
    public void TokenAotPrototype_Should_Expose_Resource_Keys()
    {
        var contract = new TokenAotPrototypeContract();

        var mintResources = contract.DescribeResources(
            new ContractInvocation("Mint", MintInput.Encode(new MintInput("alice", 42))));
        var balanceResources = contract.DescribeResources(
            new ContractInvocation("GetBalance", BalanceInput.Encode(new BalanceInput("alice"))));

        Assert.True(contract.SupportsResourceExtraction);
        Assert.Equal(["balance:alice"], mintResources.WriteKeys);
        Assert.Empty(mintResources.ReadKeys);
        Assert.Equal(["balance:alice"], balanceResources.ReadKeys);
        Assert.Empty(balanceResources.WriteKeys);
    }
}
