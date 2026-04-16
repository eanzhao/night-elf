namespace NightElf.Vrf;

public enum VrfProviderKind
{
    Deterministic
}

public sealed class VrfProviderOptions
{
    public const string SectionName = "NightElf:Vrf";

    public string? Provider { get; set; } = nameof(VrfProviderKind.Deterministic);

    public string DomainPrefix { get; set; } = "nightelf.vrf";

    public VrfProviderKind ResolveProviderKind()
    {
        if (Enum.TryParse<VrfProviderKind>(Provider, ignoreCase: true, out var kind))
        {
            return kind;
        }

        throw new InvalidOperationException($"Unsupported VRF provider '{Provider}'.");
    }

    public void Validate()
    {
        _ = ResolveProviderKind();

        if (string.IsNullOrWhiteSpace(DomainPrefix))
        {
            throw new InvalidOperationException("NightElf:Vrf:DomainPrefix must not be empty.");
        }
    }
}
