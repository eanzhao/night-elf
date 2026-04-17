using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Google.Protobuf;

using NightElf.DynamicContracts;
using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp;

public sealed record ChainSettlementEventEnvelope(
    string EventId,
    ChainEventType EventType,
    DateTimeOffset OccurredAtUtc,
    long BlockHeight = 0,
    string? BlockHash = null,
    string? TransactionId = null,
    string? ContractAddress = null,
    string? StateKey = null,
    byte[]? Payload = null,
    string? Message = null);

public sealed class ChainSettlementContractDeploymentRecord
{
    public string ContractName { get; init; } = string.Empty;

    public string ContractAddressHex { get; init; } = string.Empty;

    public string DeployerAddressHex { get; init; } = string.Empty;

    public string TransactionId { get; init; } = string.Empty;

    public string CodeHash { get; init; } = string.Empty;

    public string EntryContractType { get; init; } = string.Empty;

    public int AssemblySize { get; init; }

    public long BlockHeight { get; init; }

    public string BlockHash { get; init; } = string.Empty;

    public DateTimeOffset DeployedAtUtc { get; init; }
}

internal sealed class ChainSettlementContractDeployExecutionResult
{
    public required string TransactionId { get; init; }

    public required string CodeHash { get; init; }

    public required TransactionExecutionStatus Status { get; init; }

    public string Error { get; init; } = string.Empty;

    public string ContractAddressHex { get; init; } = string.Empty;

    public long BlockHeight { get; init; }

    public string BlockHash { get; init; } = string.Empty;
}

public static class ChainSettlementStateKeys
{
    public static string GetContractMarkerKey(string contractAddressHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddressHex);
        return $"contract:{contractAddressHex}";
    }

    public static string GetContractAssemblyKey(string contractAddressHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddressHex);
        return $"contract:{contractAddressHex}:assembly";
    }

    public static string GetContractMetadataKey(string contractAddressHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddressHex);
        return $"contract:{contractAddressHex}:metadata";
    }

    public static string GetContractCodeHashKey(string codeHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        return $"contract:codehash:{codeHash}";
    }

    public static string GetContractTransactionKey(string transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        return $"contract:tx:{transactionId}";
    }
}

public static class ChainSettlementSigningHelper
{
    private static readonly byte[] DeployPrefix = Encoding.UTF8.GetBytes("nightelf:chain-settlement:deploy:v1");
    private static readonly byte[] DynamicDeployPrefix = Encoding.UTF8.GetBytes("nightelf:chain-settlement:dynamic-deploy:v1");

    public static byte[] CreateContractDeploySigningHash(
        ReadOnlySpan<byte> assemblyBytes,
        string? contractName)
    {
        var normalizedName = NormalizeContractName(contractName);
        var nameBytes = Encoding.UTF8.GetBytes(normalizedName);
        var assemblyHash = SHA256.HashData(assemblyBytes.ToArray());

        var payload = new byte[DeployPrefix.Length + assemblyHash.Length + nameBytes.Length];
        DeployPrefix.CopyTo(payload, 0);
        assemblyHash.CopyTo(payload, DeployPrefix.Length);
        nameBytes.CopyTo(payload, DeployPrefix.Length + assemblyHash.Length);

        return SHA256.HashData(payload);
    }

    public static string NormalizeContractName(string? contractName)
    {
        return string.IsNullOrWhiteSpace(contractName)
            ? "DynamicContract"
            : contractName.Trim();
    }

    public static byte[] CreateDynamicContractDeploySigningHash(
        ContractSpec spec,
        string? contractName = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var normalizedName = NormalizeContractName(contractName ?? spec.ContractName);
        var nameBytes = Encoding.UTF8.GetBytes(normalizedName);
        var specHash = SHA256.HashData(ContractSpecSerializer.SerializeCanonicalBytes(spec));

        var payload = new byte[DynamicDeployPrefix.Length + specHash.Length + nameBytes.Length];
        DynamicDeployPrefix.CopyTo(payload, 0);
        specHash.CopyTo(payload, DynamicDeployPrefix.Length);
        nameBytes.CopyTo(payload, DynamicDeployPrefix.Length + specHash.Length);

        return SHA256.HashData(payload);
    }

    public static string CreateCodeHash(ReadOnlySpan<byte> assemblyBytes)
    {
        return Convert.ToHexString(SHA256.HashData(assemblyBytes.ToArray()));
    }

    public static string CreateDeploymentTransactionId(
        Address deployer,
        string codeHash,
        ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(deployer);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);

        var codeHashBytes = Convert.FromHexString(codeHash);
        var payload = new byte[deployer.Value.Length + codeHashBytes.Length + signature.Length];
        deployer.Value.Span.CopyTo(payload);
        codeHashBytes.CopyTo(payload, deployer.Value.Length);
        signature.CopyTo(payload.AsSpan(deployer.Value.Length + codeHashBytes.Length));

        return Convert.ToHexString(SHA256.HashData(payload));
    }

    public static Address CreateContractAddress(Address deployer, string codeHash)
    {
        ArgumentNullException.ThrowIfNull(deployer);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);

        var codeHashBytes = Convert.FromHexString(codeHash);
        var payload = new byte[deployer.Value.Length + codeHashBytes.Length];
        deployer.Value.Span.CopyTo(payload);
        codeHashBytes.CopyTo(payload, deployer.Value.Length);

        return new Address
        {
            Value = ByteString.CopyFrom(SHA256.HashData(payload))
        };
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ChainSettlementContractDeploymentRecord))]
internal sealed partial class ChainSettlementJsonSerializerContext : JsonSerializerContext
{
}
