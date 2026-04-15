namespace NightElf.Sdk.CSharp;

public interface IContractCodec<TSelf>
    where TSelf : IContractCodec<TSelf>
{
    static abstract TSelf Decode(ReadOnlySpan<byte> input);

    static abstract byte[] Encode(TSelf value);
}
