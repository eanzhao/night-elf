using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Google.Protobuf;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using NightElf.Database;
using NightElf.Kernel.Consensus;
using NightElf.Kernel.Core;

namespace NightElf.Launcher;

public interface IGenesisBlockService
{
    Task<GenesisInitializationResult> EnsureGenesisAsync(CancellationToken cancellationToken = default);
}

public sealed class GenesisBlockService : IGenesisBlockService
{
    private readonly LauncherOptions _launcherOptions;
    private readonly ConsensusEngineOptions _consensusOptions;
    private readonly IConsensusEngine _consensusEngine;
    private readonly IBlockRepository _blockRepository;
    private readonly IChainStateStore _chainStateStore;

    public GenesisBlockService(
        LauncherOptions launcherOptions,
        ConsensusEngineOptions consensusOptions,
        IConsensusEngine consensusEngine,
        IBlockRepository blockRepository,
        IChainStateStore chainStateStore)
    {
        _launcherOptions = launcherOptions ?? throw new ArgumentNullException(nameof(launcherOptions));
        _consensusOptions = consensusOptions ?? throw new ArgumentNullException(nameof(consensusOptions));
        _consensusEngine = consensusEngine ?? throw new ArgumentNullException(nameof(consensusEngine));
        _blockRepository = blockRepository ?? throw new ArgumentNullException(nameof(blockRepository));
        _chainStateStore = chainStateStore ?? throw new ArgumentNullException(nameof(chainStateStore));
    }

    public async Task<GenesisInitializationResult> EnsureGenesisAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _launcherOptions.ValidateAgainstConsensus(_consensusOptions);

        var existingBestChain = await _chainStateStore.GetBestChainAsync(cancellationToken).ConfigureAwait(false);
        if (existingBestChain is not null)
        {
            return new GenesisInitializationResult
            {
                Created = false,
                Block = existingBestChain,
                Checkpoint = null,
                Proposal = null
            };
        }

        var validators = _launcherOptions.Genesis.GetEffectiveValidators(_consensusOptions);
        var proposedAtUtc = _launcherOptions.Genesis.TimestampUtc ?? DateTimeOffset.UtcNow;
        var proposal = await _consensusEngine.ProposeBlockAsync(
                new ConsensusContext
                {
                    ExpectedHeight = 1,
                    PreviousBlock = null,
                    LastIrreversibleBlock = null,
                    ProposerAddress = validators[0],
                    RoundNumber = 1,
                    TermNumber = 1,
                    ProposedAtUtc = proposedAtUtc,
                    RandomSeed = CreateGenesisSeed(_launcherOptions.Genesis, validators)
                },
                cancellationToken)
            .ConfigureAwait(false);

        var validation = await _consensusEngine.ValidateBlockAsync(
                proposal,
                new ConsensusValidationContext
                {
                    ExpectedHeight = 1,
                    ExpectedValidators = validators
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Genesis proposal validation failed: {validation.ErrorCode} {validation.ErrorMessage}");
        }

        var deploymentTransactions = CreateSystemContractDeploymentTransactions(
            _launcherOptions.Genesis,
            proposal,
            validators);
        var block = BlockModelFactory.CreateBlock(
            proposal,
            _launcherOptions.Genesis.ChainId,
            deploymentTransactions.Select(static deployment => deployment.Transaction).ToArray());
        var canonicalGenesisBlock = new BlockReference(
            proposal.Block.Height,
            ComputeCanonicalBlockHash(block));
        var canonicalProposal = proposal with
        {
            Block = canonicalGenesisBlock
        };
        await _blockRepository.StoreAsync(canonicalGenesisBlock, block, cancellationToken).ConfigureAwait(false);

        var writes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["genesis:block-hash"] = Encoding.UTF8.GetBytes(canonicalGenesisBlock.Hash),
            ["genesis:chain-id"] = Encoding.UTF8.GetBytes(_launcherOptions.Genesis.ChainId.ToString()),
            ["genesis:validators"] = Encoding.UTF8.GetBytes(string.Join(",", validators)),
            ["genesis:system-contracts"] = Encoding.UTF8.GetBytes(string.Join(",", _launcherOptions.Genesis.SystemContracts)),
            ["genesis:config"] = BlockModelFactory.CreateGenesisConfigPayload(_launcherOptions.Genesis)
        };
        foreach (var deployment in deploymentTransactions)
        {
            var deploymentRecord = new GenesisSystemContractDeploymentRecord
            {
                ContractName = deployment.ContractName,
                AddressHex = deployment.AddressHex,
                DeploymentTransactionId = deployment.Transaction.GetTransactionId(),
                DeploymentMethod = deployment.Transaction.MethodName,
                DeployerPublicKeyHex = deployment.DeployerPublicKeyHex,
                CodeHash = deployment.CodeHash,
                BlockHeight = canonicalGenesisBlock.Height,
                BlockHash = canonicalGenesisBlock.Hash,
                DeployedAtUtc = proposal.TimestampUtc
            };

            writes[$"system-contract:{deployment.ContractName}"] = Encoding.UTF8.GetBytes("deployed");
            writes[$"system-contract:{deployment.ContractName}:deployment"] = SerializeDeploymentRecord(deploymentRecord);
        }

        await _chainStateStore.SetBestChainAsync(canonicalGenesisBlock, cancellationToken).ConfigureAwait(false);
        await _chainStateStore.ApplyChangesAsync(canonicalGenesisBlock, writes, cancellationToken: cancellationToken).ConfigureAwait(false);
        var checkpoint = await _chainStateStore.AdvanceLibCheckpointAsync(canonicalGenesisBlock, cancellationToken).ConfigureAwait(false);

        await _consensusEngine.OnBlockCommittedAsync(
                new ConsensusCommitContext
                {
                    Block = canonicalProposal,
                    LastIrreversibleBlock = canonicalGenesisBlock
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new GenesisInitializationResult
        {
            Created = true,
            Block = canonicalGenesisBlock,
            Checkpoint = checkpoint,
            Proposal = canonicalProposal
        };
    }

    private static byte[] CreateGenesisSeed(
        GenesisConfig genesisConfig,
        IReadOnlyList<string> validators)
    {
        var payload = $"{genesisConfig.ChainId}|{string.Join(",", validators)}|{string.Join(",", genesisConfig.SystemContracts)}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(payload));
    }

    private static IReadOnlyList<GenesisDeploymentTransaction> CreateSystemContractDeploymentTransactions(
        GenesisConfig genesisConfig,
        ConsensusBlockProposal proposal,
        IReadOnlyList<string> validators)
    {
        var deploymentTransactions = new List<GenesisDeploymentTransaction>(genesisConfig.SystemContracts.Count);
        var deployerHint = validators[0];

        foreach (var contractName in genesisConfig.SystemContracts)
        {
            var artifact = SystemContractArtifactCatalog.Resolve(contractName);
            var privateKeySeed = CreateDeterministicSeed(
                "genesis-deployer",
                genesisConfig.ChainId,
                contractName,
                deployerHint);
            var privateKey = new Ed25519PrivateKeyParameters(privateKeySeed, 0);
            var publicKey = privateKey.GeneratePublicKey().GetEncoded();
            var addressBytes = CreateDeterministicSeed(
                "system-contract-address",
                genesisConfig.ChainId,
                contractName,
                "nightelf");
            var payload = new GenesisSystemContractDeploymentPayload
            {
                ContractName = contractName,
                ChainId = genesisConfig.ChainId,
                Category = "System",
                CodeHash = artifact.CodeHash
            };

            var transaction = new NightElf.Kernel.Core.Protobuf.Transaction
            {
                From = new NightElf.Kernel.Core.Protobuf.Address
                {
                    Value = ByteString.CopyFrom(publicKey)
                },
                To = new NightElf.Kernel.Core.Protobuf.Address
                {
                    Value = ByteString.CopyFrom(addressBytes)
                },
                RefBlockNumber = proposal.Block.Height,
                RefBlockPrefix = proposal.Block.Hash.GetRefBlockPrefix(),
                MethodName = "DeploySystemContract",
                Params = ByteString.CopyFrom(
                    JsonSerializer.SerializeToUtf8Bytes(
                        payload,
                        GenesisJsonSerializerContext.Default.GenesisSystemContractDeploymentPayload))
            };

            var signer = new Ed25519Signer();
            signer.Init(true, privateKey);
            var signingHash = transaction.GetSigningHash();
            signer.BlockUpdate(signingHash, 0, signingHash.Length);
            transaction.Signature = ByteString.CopyFrom(signer.GenerateSignature());

            deploymentTransactions.Add(new GenesisDeploymentTransaction(
                transaction,
                contractName,
                Convert.ToHexString(addressBytes),
                Convert.ToHexString(publicKey),
                payload.CodeHash));
        }

        return deploymentTransactions;
    }

    private static byte[] CreateDeterministicSeed(
        string prefix,
        int chainId,
        string value,
        string extra)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{prefix}|chain={chainId}|value={value}|extra={extra}"));
    }

    private static byte[] SerializeDeploymentRecord(GenesisSystemContractDeploymentRecord record)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            record,
            GenesisJsonSerializerContext.Default.GenesisSystemContractDeploymentRecord);
    }

    private static string ComputeCanonicalBlockHash(NightElf.Kernel.Core.Protobuf.Block block)
    {
        return Convert.ToHexString(SHA256.HashData(block.ToByteArray()));
    }

    private sealed record GenesisDeploymentTransaction(
        NightElf.Kernel.Core.Protobuf.Transaction Transaction,
        string ContractName,
        string AddressHex,
        string DeployerPublicKeyHex,
        string CodeHash);
}

public sealed class GenesisInitializationResult
{
    public required bool Created { get; init; }

    public required BlockReference Block { get; init; }

    public StateCheckpointDescriptor? Checkpoint { get; init; }

    public ConsensusBlockProposal? Proposal { get; init; }
}
