namespace NightElf.Kernel.SmartContract;

public sealed class ContractCallTargetInfo
{
    public ContractCallTargetInfo(
        string contractAddress,
        string contractName,
        bool isDynamicContract = false,
        bool isSystemContract = false,
        string? owningTreatyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);
        if (owningTreatyId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(owningTreatyId);
        }

        ContractAddress = contractAddress;
        ContractName = contractName;
        IsDynamicContract = isDynamicContract;
        IsSystemContract = isSystemContract;
        OwningTreatyId = owningTreatyId;
    }

    public string ContractAddress { get; }

    public string ContractName { get; }

    public bool IsDynamicContract { get; }

    public bool IsSystemContract { get; }

    public string? OwningTreatyId { get; }
}
