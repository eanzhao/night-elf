namespace NightElf.Sdk.CSharp;

public sealed class ContractCryptoContext
{
    private readonly IContractCryptoProvider _provider;

    public ContractCryptoContext(IContractCryptoProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public byte[] Hash(ReadOnlySpan<byte> input)
    {
        return _provider.Hash(input);
    }

    public bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);

        return _provider.VerifySignature(data, signature, publicKey);
    }

    public byte[] DeriveVrfProof(ReadOnlySpan<byte> seed)
    {
        return _provider.DeriveVrfProof(seed);
    }
}
