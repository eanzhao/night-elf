using Microsoft.Extensions.Configuration;

using NightElf.Kernel.Consensus;
using NightElf.OS.Network;

namespace NightElf.Launcher;

public sealed class LauncherOptions
{
    public const string SectionName = "NightElf:Launcher";

    public string NodeId { get; set; } = "node-local";

    public int ApiPort { get; set; } = 5005;

    public string DataRootPath { get; set; } = Path.Combine("artifacts", "node", "data");

    public string CheckpointRootPath { get; set; } = Path.Combine("artifacts", "node", "checkpoints");

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public int? MaxProducedBlocks { get; set; }

    public LauncherNetworkOptions Network { get; set; } = new();

    public GenesisConfig Genesis { get; set; } = new();

    public static LauncherOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var networkSection = section.GetSection(nameof(Network));
        var genesisSection = section.GetSection(nameof(Genesis));

        return new LauncherOptions
        {
            NodeId = section[nameof(NodeId)] ?? "node-local",
            ApiPort = ParseInt32(section[nameof(ApiPort)], 5005),
            DataRootPath = section[nameof(DataRootPath)] ?? Path.Combine("artifacts", "node", "data"),
            CheckpointRootPath = section[nameof(CheckpointRootPath)] ?? Path.Combine("artifacts", "node", "checkpoints"),
            ShutdownTimeout = ParseTimeSpan(section[nameof(ShutdownTimeout)], TimeSpan.FromSeconds(15)),
            MaxProducedBlocks = ParseNullableInt32(section[nameof(MaxProducedBlocks)]),
            Network = new LauncherNetworkOptions
            {
                Host = networkSection[nameof(LauncherNetworkOptions.Host)] ?? "127.0.0.1",
                GrpcPort = ParseInt32(networkSection[nameof(LauncherNetworkOptions.GrpcPort)], 6800),
                QuicPort = ParseInt32(networkSection[nameof(LauncherNetworkOptions.QuicPort)], 6801)
            },
            Genesis = GenesisConfig.FromConfiguration(genesisSection)
        };
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(NodeId);

        if (ApiPort <= 0 || ApiPort > 65535)
        {
            throw new InvalidOperationException("NightElf:Launcher:ApiPort must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(DataRootPath))
        {
            throw new InvalidOperationException("NightElf:Launcher:DataRootPath must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(CheckpointRootPath))
        {
            throw new InvalidOperationException("NightElf:Launcher:CheckpointRootPath must not be empty.");
        }

        if (ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("NightElf:Launcher:ShutdownTimeout must be greater than zero.");
        }

        if (MaxProducedBlocks is <= 0)
        {
            throw new InvalidOperationException("NightElf:Launcher:MaxProducedBlocks must be greater than zero when set.");
        }

        Network.Validate();
        Genesis.Validate();
    }

    public NetworkNodeEndpoint CreateLocalNodeEndpoint()
    {
        Validate();

        return new NetworkNodeEndpoint
        {
            NodeId = NodeId,
            Host = Network.Host,
            GrpcPort = Network.GrpcPort,
            QuicPort = Network.QuicPort
        };
    }

    public void ValidateAgainstConsensus(ConsensusEngineOptions consensusOptions)
    {
        ArgumentNullException.ThrowIfNull(consensusOptions);
        consensusOptions.Validate();
        Genesis.ValidateAgainstConsensus(consensusOptions);
    }

    private static int ParseInt32(string? value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid integer value '{value}' in NightElf launcher configuration.");
    }

    private static int? ParseNullableInt32(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid integer value '{value}' in NightElf launcher configuration.");
    }

    private static TimeSpan ParseTimeSpan(string? value, TimeSpan defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (TimeSpan.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid TimeSpan value '{value}' in NightElf launcher configuration.");
    }
}

public sealed class LauncherNetworkOptions
{
    public string Host { get; set; } = "127.0.0.1";

    public int GrpcPort { get; set; } = 6800;

    public int QuicPort { get; set; } = 6801;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);

        if (GrpcPort < 0 || GrpcPort > 65535)
        {
            throw new InvalidOperationException("NightElf:Launcher:Network:GrpcPort must be between 0 and 65535.");
        }

        if (QuicPort < 0 || QuicPort > 65535)
        {
            throw new InvalidOperationException("NightElf:Launcher:Network:QuicPort must be between 0 and 65535.");
        }
    }
}

public sealed class GenesisConfig
{
    public int ChainId { get; set; } = 9992731;

    public DateTimeOffset? TimestampUtc { get; set; }

    public List<string> Validators { get; set; } = [];

    public List<string> SystemContracts { get; set; } = ["AgentSession"];

    public static GenesisConfig FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var validators = configuration
            .GetSection(nameof(Validators))
            .GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();

        var systemContracts = configuration
            .GetSection(nameof(SystemContracts))
            .GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();

        DateTimeOffset? timestampUtc = null;
        if (DateTimeOffset.TryParse(configuration[nameof(TimestampUtc)], out var parsedTimestamp))
        {
            timestampUtc = parsedTimestamp;
        }

        return new GenesisConfig
        {
            ChainId = int.TryParse(configuration[nameof(ChainId)], out var chainId) ? chainId : 9992731,
            TimestampUtc = timestampUtc,
            Validators = validators,
            SystemContracts = systemContracts.Count == 0 ? ["AgentSession"] : systemContracts
        };
    }

    public void Validate()
    {
        if (ChainId <= 0)
        {
            throw new InvalidOperationException("NightElf:Launcher:Genesis:ChainId must be greater than zero.");
        }

        if (Validators.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("NightElf:Launcher:Genesis:Validators must not contain empty values.");
        }

        if (SystemContracts.Count == 0 || SystemContracts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("NightElf:Launcher:Genesis:SystemContracts must not be empty.");
        }
    }

    public IReadOnlyList<string> GetEffectiveValidators(ConsensusEngineOptions consensusOptions)
    {
        ArgumentNullException.ThrowIfNull(consensusOptions);

        return Validators.Count > 0
            ? Validators
            : consensusOptions.Aedpos.Validators;
    }

    public void ValidateAgainstConsensus(ConsensusEngineOptions consensusOptions)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(consensusOptions);

        var consensusValidators = consensusOptions.Aedpos.Validators;
        if (Validators.Count == 0)
        {
            return;
        }

        if (!Validators.SequenceEqual(consensusValidators, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "NightElf launcher genesis validators must match the configured consensus validator set.");
        }
    }
}
