using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightElf.Kernel.Core;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BlockReference))]
internal sealed partial class ChainStateJsonSerializerContext : JsonSerializerContext
{
}
