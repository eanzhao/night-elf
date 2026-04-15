using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightElf.Database.Tsavorite;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StateCheckpointDescriptor))]
[JsonSerializable(typeof(TsavoriteStateCheckpointCatalog))]
internal sealed partial class TsavoriteStateCheckpointStoreJsonSerializerContext : JsonSerializerContext
{
}

internal sealed class TsavoriteStateCheckpointCatalog
{
    public IReadOnlyList<StateCheckpointDescriptor> Checkpoints { get; init; } = [];
}
