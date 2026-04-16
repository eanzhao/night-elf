using Microsoft.Extensions.Configuration;

namespace NightElf.Kernel.Consensus;

public enum ConsensusEngineKind
{
    Aedpos,
    SingleValidator
}

public sealed class ConsensusEngineOptions
{
    public const string SectionName = "NightElf:Consensus";

    public string? Engine { get; set; } = nameof(ConsensusEngineKind.Aedpos);

    public AedposConsensusOptions Aedpos { get; set; } = new();

    public SingleValidatorConsensusOptions SingleValidator { get; set; } = new();

    public static ConsensusEngineOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var aedposSection = section.GetSection(nameof(Aedpos));
        var singleValidatorSection = section.GetSection(nameof(SingleValidator));

        var aedposOptions = new AedposConsensusOptions();
        var configuredValidators = aedposSection
            .GetSection(nameof(AedposConsensusOptions.Validators))
            .GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();

        if (configuredValidators.Count > 0)
        {
            aedposOptions.Validators = configuredValidators;
        }

        if (TimeSpan.TryParse(aedposSection[nameof(AedposConsensusOptions.BlockInterval)], out var aedposBlockInterval))
        {
            aedposOptions.BlockInterval = aedposBlockInterval;
        }

        if (int.TryParse(aedposSection[nameof(AedposConsensusOptions.BlocksPerRound)], out var blocksPerRound))
        {
            aedposOptions.BlocksPerRound = blocksPerRound;
        }

        if (int.TryParse(aedposSection[nameof(AedposConsensusOptions.IrreversibleBlockDistance)], out var irreversibleBlockDistance))
        {
            aedposOptions.IrreversibleBlockDistance = irreversibleBlockDistance;
        }

        var singleValidatorOptions = new SingleValidatorConsensusOptions
        {
            ValidatorAddress = singleValidatorSection[nameof(SingleValidatorConsensusOptions.ValidatorAddress)] ?? string.Empty
        };

        if (TimeSpan.TryParse(singleValidatorSection[nameof(SingleValidatorConsensusOptions.BlockInterval)], out var singleValidatorBlockInterval))
        {
            singleValidatorOptions.BlockInterval = singleValidatorBlockInterval;
        }

        return new ConsensusEngineOptions
        {
            Engine = section[nameof(ConsensusEngineOptions.Engine)] ?? nameof(ConsensusEngineKind.Aedpos),
            Aedpos = aedposOptions,
            SingleValidator = singleValidatorOptions
        };
    }

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
                Aedpos.ApplyDefaults();
                Aedpos.Validate();
                return;
            case ConsensusEngineKind.SingleValidator:
                SingleValidator.ApplyDefaults();
                SingleValidator.Validate();
                return;
            default:
                throw new InvalidOperationException($"Unsupported consensus engine '{Engine}'.");
        }
    }

    public IReadOnlyList<string> GetValidatorAddresses()
    {
        return ResolveEngineKind() switch
        {
            ConsensusEngineKind.Aedpos =>
                ApplyAndReturnAedposValidators(),
            ConsensusEngineKind.SingleValidator =>
                [ApplyAndReturnSingleValidatorAddress()],
            _ => throw new InvalidOperationException($"Unsupported consensus engine '{Engine}'.")
        };
    }

    public TimeSpan GetBlockInterval()
    {
        return ResolveEngineKind() switch
        {
            ConsensusEngineKind.Aedpos => ApplyAndReturnAedposBlockInterval(),
            ConsensusEngineKind.SingleValidator => ApplyAndReturnSingleValidatorBlockInterval(),
            _ => throw new InvalidOperationException($"Unsupported consensus engine '{Engine}'.")
        };
    }

    public (long RoundNumber, long TermNumber) ResolveRoundAndTerm(long blockHeight)
    {
        if (blockHeight <= 0)
        {
            throw new InvalidOperationException("Consensus block height must be greater than zero.");
        }

        return ResolveEngineKind() switch
        {
            ConsensusEngineKind.Aedpos => ResolveAedposRoundAndTerm(blockHeight),
            ConsensusEngineKind.SingleValidator => (blockHeight, 1),
            _ => throw new InvalidOperationException($"Unsupported consensus engine '{Engine}'.")
        };
    }

    public long ResolveLastIrreversibleBlockHeightHint(long blockHeight)
    {
        if (blockHeight <= 0)
        {
            throw new InvalidOperationException("Consensus block height must be greater than zero.");
        }

        return ResolveEngineKind() switch
        {
            ConsensusEngineKind.Aedpos => Math.Max(1, blockHeight - ApplyAndReturnAedposIrreversibleBlockDistance()),
            ConsensusEngineKind.SingleValidator => Math.Max(0, blockHeight - 1),
            _ => throw new InvalidOperationException($"Unsupported consensus engine '{Engine}'.")
        };
    }

    private IReadOnlyList<string> ApplyAndReturnAedposValidators()
    {
        Aedpos.ApplyDefaults();
        return Aedpos.Validators;
    }

    private TimeSpan ApplyAndReturnAedposBlockInterval()
    {
        Aedpos.ApplyDefaults();
        return Aedpos.BlockInterval;
    }

    private int ApplyAndReturnAedposIrreversibleBlockDistance()
    {
        Aedpos.ApplyDefaults();
        return Aedpos.IrreversibleBlockDistance;
    }

    private (long RoundNumber, long TermNumber) ResolveAedposRoundAndTerm(long blockHeight)
    {
        Aedpos.ApplyDefaults();
        return (
            ((blockHeight - 1) % Aedpos.BlocksPerRound) + 1,
            ((blockHeight - 1) / Aedpos.BlocksPerRound) + 1);
    }

    private string ApplyAndReturnSingleValidatorAddress()
    {
        SingleValidator.ApplyDefaults();
        return SingleValidator.ValidatorAddress;
    }

    private TimeSpan ApplyAndReturnSingleValidatorBlockInterval()
    {
        SingleValidator.ApplyDefaults();
        return SingleValidator.BlockInterval;
    }
}

public sealed class AedposConsensusOptions
{
    private static readonly string[] DefaultValidators = ["validator-a", "validator-b", "validator-c"];

    public List<string> Validators { get; set; } = [];

    public TimeSpan BlockInterval { get; set; } = TimeSpan.FromSeconds(4);

    public int BlocksPerRound { get; set; } = 3;

    public int IrreversibleBlockDistance { get; set; } = 8;

    public void ApplyDefaults()
    {
        if (Validators.Count == 0)
        {
            Validators = [.. DefaultValidators];
        }
    }

    public void Validate()
    {
        if (Validators.Count == 0)
        {
            throw new InvalidOperationException(
                "NightElf:Consensus:Aedpos:Validators must not be empty. Call ApplyDefaults() first or provide validators.");
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

public sealed class SingleValidatorConsensusOptions
{
    public string ValidatorAddress { get; set; } = string.Empty;

    public TimeSpan BlockInterval { get; set; } = TimeSpan.FromSeconds(4);

    public void ApplyDefaults()
    {
        if (string.IsNullOrWhiteSpace(ValidatorAddress))
        {
            ValidatorAddress = "node-local";
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ValidatorAddress))
        {
            throw new InvalidOperationException(
                "NightElf:Consensus:SingleValidator:ValidatorAddress must not be empty. Call ApplyDefaults() first or provide a validator address.");
        }

        if (BlockInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "NightElf:Consensus:SingleValidator:BlockInterval must be greater than zero.");
        }
    }
}
