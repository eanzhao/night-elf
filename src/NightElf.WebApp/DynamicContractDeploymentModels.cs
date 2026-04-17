using Google.Protobuf;

using NightElf.DynamicContracts;
using NightElf.Kernel.Core.Protobuf;

namespace NightElf.WebApp;

public sealed class DynamicContractDeployRequest
{
    public required ContractSpec Spec { get; init; }

    public required Address Deployer { get; init; }

    public ByteString Signature { get; init; } = ByteString.Empty;
}
