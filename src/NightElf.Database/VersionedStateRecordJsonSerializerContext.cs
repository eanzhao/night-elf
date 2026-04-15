using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightElf.Database;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VersionedStateRecord))]
internal sealed partial class VersionedStateRecordJsonSerializerContext : JsonSerializerContext
{
}
