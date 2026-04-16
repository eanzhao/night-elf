using System.Security.Cryptography;
using System.Text;

using NightElf.Contracts.System.AgentSession;

namespace NightElf.Launcher;

internal static class SystemContractArtifactCatalog
{
    private static readonly IReadOnlyDictionary<string, SystemContractArtifact> Artifacts =
        new Dictionary<string, SystemContractArtifact>(StringComparer.Ordinal)
        {
            ["AgentSession"] = CreateFromContractType("AgentSession", typeof(AgentSessionContract))
        };

    public static SystemContractArtifact Resolve(string contractName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);

        return Artifacts.TryGetValue(contractName, out var artifact)
            ? artifact
            : CreateFallback(contractName);
    }

    private static SystemContractArtifact CreateFromContractType(string contractName, Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);

        var assemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            $"{contractType.Assembly.GetName().Name}.dll");
        var codeHash = File.Exists(assemblyPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath)))
            : Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(contractType.AssemblyQualifiedName ?? contractName)));

        return new SystemContractArtifact(contractName, codeHash, contractType.FullName ?? contractName);
    }

    private static SystemContractArtifact CreateFallback(string contractName)
    {
        return new SystemContractArtifact(
            contractName,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contractName))),
            string.Empty);
    }
}

internal sealed record SystemContractArtifact(
    string ContractName,
    string CodeHash,
    string ImplementationType);
