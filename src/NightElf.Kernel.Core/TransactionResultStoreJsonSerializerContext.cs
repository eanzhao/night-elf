using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightElf.Kernel.Core;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TransactionResultRecord))]
internal sealed partial class TransactionResultStoreJsonSerializerContext : JsonSerializerContext
{
}
