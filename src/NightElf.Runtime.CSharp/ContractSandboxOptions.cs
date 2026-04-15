namespace NightElf.Runtime.CSharp;

public sealed class ContractSandboxOptions
{
    private readonly HashSet<string> _allowedAssemblies = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sharedAssemblies = new(StringComparer.Ordinal)
    {
        "NightElf.Sdk.CSharp"
    };

    public IEnumerable<string> AllowedAssemblies => _allowedAssemblies;

    public IEnumerable<string> SharedAssemblies => _sharedAssemblies;

    public void AllowAssembly(string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        _allowedAssemblies.Add(assemblyName);
    }

    public void ShareAssembly(string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        _sharedAssemblies.Add(assemblyName);
    }

    internal bool IsAllowedAssembly(string assemblyName)
    {
        return _allowedAssemblies.Contains(assemblyName);
    }

    internal bool IsSharedAssembly(string assemblyName)
    {
        return _sharedAssemblies.Contains(assemblyName);
    }
}
