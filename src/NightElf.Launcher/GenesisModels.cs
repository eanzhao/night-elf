namespace NightElf.Launcher;

public sealed class GenesisConfigSnapshot
{
    public int ChainId { get; init; }

    public DateTimeOffset? TimestampUtc { get; init; }

    public IReadOnlyList<string> Validators { get; init; } = [];

    public IReadOnlyList<string> SystemContracts { get; init; } = [];
}

public sealed class GenesisSystemContractDeploymentRecord
{
    public string ContractName { get; init; } = string.Empty;

    public string AddressHex { get; init; } = string.Empty;

    public string DeploymentTransactionId { get; init; } = string.Empty;

    public string DeploymentMethod { get; init; } = string.Empty;

    public string DeployerPublicKeyHex { get; init; } = string.Empty;

    public string CodeHash { get; init; } = string.Empty;

    public long BlockHeight { get; init; }

    public string BlockHash { get; init; } = string.Empty;

    public DateTimeOffset DeployedAtUtc { get; init; }
}

internal sealed class GenesisSystemContractDeploymentPayload
{
    public string ContractName { get; init; } = string.Empty;

    public int ChainId { get; init; }

    public string Category { get; init; } = string.Empty;

    public string CodeHash { get; init; } = string.Empty;
}
