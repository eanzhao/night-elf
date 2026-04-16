using NightElf.Sdk.CSharp;

namespace NightElf.Contracts.System.TokenAotPrototype;

public sealed partial class TokenAotPrototypeContract : CSharpSmartContract
{
    private readonly Dictionary<string, long> _balances = new(StringComparer.Ordinal);

    [ContractMethod(WriteExtractor = nameof(GetMintWriteKeys))]
    public Empty Mint(MintInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Owner);
        ArgumentOutOfRangeException.ThrowIfNegative(input.Amount);

        checked
        {
            _balances[input.Owner] = _balances.TryGetValue(input.Owner, out var balance)
                ? balance + input.Amount
                : input.Amount;
        }

        return Empty.Value;
    }

    [ContractMethod(ReadExtractor = nameof(GetBalanceReadKeys))]
    public BalanceOutput GetBalance(BalanceInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Owner);

        return new BalanceOutput(
            _balances.TryGetValue(input.Owner, out var balance)
                ? balance
                : 0);
    }

    private static IEnumerable<string> GetMintWriteKeys(MintInput input)
    {
        yield return CreateBalanceKey(input.Owner);
    }

    private static IEnumerable<string> GetBalanceReadKeys(BalanceInput input)
    {
        yield return CreateBalanceKey(input.Owner);
    }

    private static string CreateBalanceKey(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return $"balance:{owner}";
    }
}
