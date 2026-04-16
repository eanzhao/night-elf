namespace NightElf.Kernel.Consensus;

public enum ConsensusEngineKind
{
    Aedpos
}

public sealed class ConsensusEngineOptions
{
    public const string SectionName = "NightElf:Consensus";

    public string? Engine { get; set; } = nameof(ConsensusEngineKind.Aedpos);

    public AedposConsensusOptions Aedpos { get; set; } = new();

    public ConsensusEngineKind ResolveEngineKind()
    {
        if (Enum.TryParse<ConsensusEngineKind>(Engine, ignoreCase: true, out var kind))
        {
            return kind;
        }

        throw new InvalidOperationException($"Unsupported consensus engine '{Engine}'.");
    }

    public void Validate()
    {
        switch (ResolveEngineKind())
        {
            case ConsensusEngineKind.Aedpos:
                Aedpos.Validate();
                return;
            default:
                throw new InvalidOperationException($"Unsupported consensus engine '{Engine}'.");
        }
    }
}

public sealed class AedposConsensusOptions
{
    private static readonly string[] DefaultValidators = ["validator-a", "validator-b", "validator-c"];

    public List<string> Validators { get; set; } = [];

    public TimeSpan BlockInterval { get; set; } = TimeSpan.FromSeconds(4);

    public int BlocksPerRound { get; set; } = 3;

    public int IrreversibleBlockDistance { get; set; } = 8;

    public void Validate()
    {
        if (Validators.Count == 0)
        {
            Validators = [.. DefaultValidators];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var validator in Validators)
        {
            if (string.IsNullOrWhiteSpace(validator))
            {
                throw new InvalidOperationException("NightElf:Consensus:Aedpos:Validators must not contain empty items.");
            }

            if (!seen.Add(validator))
            {
                throw new InvalidOperationException(
                    $"NightElf:Consensus:Aedpos:Validators contains duplicate validator '{validator}'.");
            }
        }

        if (BlockInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("NightElf:Consensus:Aedpos:BlockInterval must be greater than zero.");
        }

        if (BlocksPerRound <= 0)
        {
            throw new InvalidOperationException("NightElf:Consensus:Aedpos:BlocksPerRound must be greater than zero.");
        }

        if (IrreversibleBlockDistance <= 0)
        {
            throw new InvalidOperationException("NightElf:Consensus:Aedpos:IrreversibleBlockDistance must be greater than zero.");
        }
    }
}
