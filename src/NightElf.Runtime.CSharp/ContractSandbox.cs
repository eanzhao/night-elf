using System.Reflection;
using System.Runtime.Loader;

namespace NightElf.Runtime.CSharp;

public sealed class ContractSandbox : AssemblyLoadContext
{
    private readonly ContractSandboxOptions _options;
    private readonly Dictionary<string, string> _allowedAssemblyPaths = new(StringComparer.Ordinal);
    private AssemblyDependencyResolver? _resolver;

    public ContractSandbox(string contractName, ContractSandboxOptions? options = null)
        : base($"NightElf.ContractSandbox.{contractName}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);

        ContractName = contractName;
        _options = options ?? new ContractSandboxOptions();
    }

    public string ContractName { get; }

    public void RegisterAllowedAssemblyPath(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        var fullPath = Path.GetFullPath(assemblyPath);
        var assemblyName = AssemblyName.GetAssemblyName(fullPath).Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

        _allowedAssemblyPaths[assemblyName] = fullPath;
        _options.AllowAssembly(assemblyName);
    }

    public Assembly LoadContractFromPath(string assemblyPath)
    {
        RegisterAllowedAssemblyPath(assemblyPath);

        var fullPath = Path.GetFullPath(assemblyPath);
        _resolver ??= new AssemblyDependencyResolver(fullPath);

        return LoadFromAssemblyPath(fullPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrWhiteSpace(simpleName) || IsPlatformAssembly(simpleName))
        {
            return null;
        }

        if (_options.IsSharedAssembly(simpleName))
        {
            return ResolveSharedAssembly(assemblyName);
        }

        if (!_options.IsAllowedAssembly(simpleName))
        {
            throw new ContractAssemblyNotAllowedException(Name ?? ContractName, simpleName);
        }

        if (_allowedAssemblyPaths.TryGetValue(simpleName, out var registeredPath))
        {
            return LoadFromAssemblyPath(registeredPath);
        }

        var resolvedPath = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (!string.IsNullOrWhiteSpace(resolvedPath))
        {
            _allowedAssemblyPaths[simpleName] = resolvedPath;
            return LoadFromAssemblyPath(resolvedPath);
        }

        return null;
    }

    private static Assembly? ResolveSharedAssembly(AssemblyName assemblyName)
    {
        var loaded = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(assembly => string.Equals(
                assembly.GetName().Name,
                assemblyName.Name,
                StringComparison.Ordinal));

        return loaded ?? Assembly.Load(assemblyName);
    }

    private static bool IsPlatformAssembly(string assemblyName)
    {
        return assemblyName is "mscorlib" or "netstandard" or "System.Private.CoreLib" ||
               assemblyName.StartsWith("System.", StringComparison.Ordinal) ||
               assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal);
    }
}
