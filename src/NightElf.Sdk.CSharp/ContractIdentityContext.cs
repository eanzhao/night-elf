namespace NightElf.Sdk.CSharp;

public sealed class ContractIdentityContext
{
    private readonly IContractIdentityProvider _provider;

    public ContractIdentityContext(IContractIdentityProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string GenerateAddress(string seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);

        return _provider.GenerateAddress(seed);
    }

    public string GetVirtualAddress(string contractAddress, string salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(salt);

        return _provider.GetVirtualAddress(contractAddress, salt);
    }
}
