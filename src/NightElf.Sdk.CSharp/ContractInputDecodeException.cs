namespace NightElf.Sdk.CSharp;

public sealed class ContractInputDecodeException : Exception
{
    public ContractInputDecodeException(string methodName, Type inputType, Exception innerException)
        : base($"Failed to decode input for contract method '{methodName}' as '{inputType.FullName}'.", innerException)
    {
        MethodName = methodName;
        InputType = inputType;
    }

    public string MethodName { get; }

    public Type InputType { get; }
}
