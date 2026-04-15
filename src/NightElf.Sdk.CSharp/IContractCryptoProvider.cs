namespace NightElf.Sdk.CSharp;

public interface IContractCryptoProvider
{
    byte[] Hash(ReadOnlySpan<byte> input);

    bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKey);

    byte[] DeriveVrfProof(ReadOnlySpan<byte> seed);
}
