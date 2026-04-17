namespace NightElf.DynamicContracts;

public sealed class DynamicContractBuildArtifact
{
    public required string ContractName { get; init; }

    public required string ContractNamespace { get; init; }

    public required string EntryContractType { get; init; }

    public required string SourceCode { get; init; }

    public required byte[] AssemblyBytes { get; init; }

    public required byte[] CanonicalSpecBytes { get; init; }
}
