namespace NightElf.Runtime.CSharp;

public sealed class ContractAssemblyNotAllowedException : Exception
{
    public ContractAssemblyNotAllowedException(string sandboxName, string assemblyName)
        : base($"Sandbox '{sandboxName}' cannot load assembly '{assemblyName}' because it is not whitelisted.")
    {
        SandboxName = sandboxName;
        AssemblyName = assemblyName;
    }

    public string SandboxName { get; }

    public string AssemblyName { get; }
}
