namespace NightElf.Sdk.CSharp;

public static class ContractCodec
{
    public static T Decode<T>(string methodName, ReadOnlySpan<byte> input)
        where T : IContractCodec<T>
    {
        try
        {
            return T.Decode(input);
        }
        catch (Exception exception) when (exception is not ContractInputDecodeException and not OperationCanceledException)
        {
            throw new ContractInputDecodeException(methodName, typeof(T), exception);
        }
    }

    public static byte[] Encode<T>(T value)
        where T : IContractCodec<T>
    {
        return T.Encode(value);
    }
}
