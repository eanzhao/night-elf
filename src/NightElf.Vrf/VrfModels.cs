namespace NightElf.Vrf;

public sealed class VrfInput
{
    public required string PublicKey { get; init; }

    public required string Domain { get; init; }

    public byte[] Seed { get; init; } = [];

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PublicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(Domain);
        ArgumentNullException.ThrowIfNull(Seed);
    }
}

public sealed class VrfEvaluation
{
    public required byte[] Proof { get; init; }

    public required byte[] Randomness { get; init; }
}

public sealed class VrfVerificationContext
{
    public required VrfInput Input { get; init; }

    public required byte[] Proof { get; init; }

    public required byte[] Randomness { get; init; }
}
