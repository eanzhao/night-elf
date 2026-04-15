namespace NightElf.Sdk.CSharp;

public readonly record struct ContractInvocation
{
    public ContractInvocation(string methodName, byte[]? input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        MethodName = methodName;
        Input = input ?? [];
    }

    public string MethodName { get; }

    public byte[] Input { get; }
}
