using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightElf.Launcher;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GenesisConfig))]
[JsonSerializable(typeof(GenesisConfigSnapshot))]
[JsonSerializable(typeof(GenesisSystemContractDeploymentRecord))]
[JsonSerializable(typeof(GenesisSystemContractDeploymentPayload))]
internal sealed partial class GenesisJsonSerializerContext : JsonSerializerContext
{
}
