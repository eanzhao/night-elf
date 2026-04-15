namespace NightElf.Sdk.CSharp;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ContractMethodAttribute : Attribute
{
    public ContractMethodAttribute(string? name = null)
    {
        Name = name;
    }

    public string? Name { get; }

    public string? ReadExtractor { get; set; }

    public string? WriteExtractor { get; set; }
}
