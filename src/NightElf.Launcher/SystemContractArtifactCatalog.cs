using System.Security.Cryptography;
using System.Text;

using NightElf.Contracts.System.AgentSession;
using NightElf.Sdk.CSharp;

namespace NightElf.Launcher;

internal static class SystemContractArtifactCatalog
{
    private static readonly IReadOnlyDictionary<string, SystemContractArtifact> Artifacts =
        new Dictionary<string, SystemContractArtifact>(StringComparer.Ordinal)
        {
            ["AgentSession"] = CreateFromContractType(
                "AgentSession",
                typeof(AgentSessionContract),
                static () => new AgentSessionContract())
        };

    public static SystemContractArtifact Resolve(string contractName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);

        return Artifacts.TryGetValue(contractName, out var artifact)
            ? artifact
            : CreateFallback(contractName);
    }

    public static bool TryCreateContractInstance(
        string contractName,
        out CSharpSmartContract? contract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);

        if (Artifacts.TryGetValue(contractName, out var artifact) && artifact.ContractFactory is not null)
        {
            contract = artifact.ContractFactory();
            return true;
        }

        contract = null;
        return false;
    }

    private static SystemContractArtifact CreateFromContractType(
        string contractName,
        Type contractType,
        Func<CSharpSmartContract> contractFactory)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentNullException.ThrowIfNull(contractFactory);

        var assemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            $"{contractType.Assembly.GetName().Name}.dll");
        var codeHash = File.Exists(assemblyPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath)))
            : Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(contractType.AssemblyQualifiedName ?? contractName)));

        return new SystemContractArtifact(
            contractName,
            codeHash,
            contractType.FullName ?? contractName,
            contractFactory);
    }

    private static SystemContractArtifact CreateFallback(string contractName)
    {
        return new SystemContractArtifact(
            contractName,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contractName))),
            string.Empty,
            null);
    }
}

internal sealed record SystemContractArtifact(
    string ContractName,
    string CodeHash,
    string ImplementationType,
    Func<CSharpSmartContract>? ContractFactory);
