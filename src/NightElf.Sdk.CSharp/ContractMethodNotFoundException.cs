namespace NightElf.Sdk.CSharp;

public sealed class ContractMethodNotFoundException : Exception
{
    public ContractMethodNotFoundException(Type contractType, string methodName)
        : base($"Contract '{contractType.FullName}' does not define method '{methodName}'.")
    {
        ContractType = contractType;
        MethodName = methodName;
    }

    public Type ContractType { get; }

    public string MethodName { get; }
}
