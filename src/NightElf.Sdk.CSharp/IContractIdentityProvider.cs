namespace NightElf.Sdk.CSharp;

public interface IContractIdentityProvider
{
    string GenerateAddress(string seed);

    string GetVirtualAddress(string contractAddress, string salt);
}
