using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightElf.DynamicContracts;

public static class ContractSpecSerializer
{
    public static byte[] SerializeCanonicalBytes(ContractSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return JsonSerializer.SerializeToUtf8Bytes(spec, DynamicContractJsonSerializerContext.Default.ContractSpec);
    }

    public static string SerializeCanonicalJson(ContractSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return JsonSerializer.Serialize(spec, DynamicContractJsonSerializerContext.Default.ContractSpec);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ContractSpec))]
[JsonSerializable(typeof(ContractTypeSpec))]
[JsonSerializable(typeof(ContractFieldSpec))]
[JsonSerializable(typeof(ContractMethodSpec))]
[JsonSerializable(typeof(ContractLogicBlockSpec))]
[JsonSerializable(typeof(ContractStringSegmentSpec))]
internal sealed partial class DynamicContractJsonSerializerContext : JsonSerializerContext
{
}
