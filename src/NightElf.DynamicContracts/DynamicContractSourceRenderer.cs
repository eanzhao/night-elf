using System.Text;

namespace NightElf.DynamicContracts;

internal sealed class DynamicContractSourceRenderer
{
    public string Render(ContractSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var contractNamespace = DynamicContractDefaults.NormalizeNamespace(spec.Namespace);
        var typesByName = spec.Types.ToDictionary(static type => type.Name, StringComparer.Ordinal);
        var builder = new StringBuilder();

        builder.AppendLine("using System;");
        builder.AppendLine("using System.Text;");
        builder.AppendLine("using NightElf.Sdk.CSharp;");
        builder.AppendLine();
        builder.AppendLine($"namespace {contractNamespace};");
        builder.AppendLine();
        builder.AppendLine("internal static class GeneratedCodec");
        builder.AppendLine("{");
        builder.AppendLine("    public static string EncodeStringField(string value)");
        builder.AppendLine("    {");
        builder.AppendLine("        return \"s:\" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static string DecodeStringField(string payload)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (!payload.StartsWith(\"s:\", StringComparison.Ordinal))");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new FormatException(\"Expected a string field payload prefixed with 's:'.\");");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return Encoding.UTF8.GetString(Convert.FromBase64String(payload[2..]));");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static string EncodeInt64Field(long value)");
        builder.AppendLine("    {");
        builder.AppendLine("        return value.ToString();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static long DecodeInt64Field(string payload, string typeName, string fieldName)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (!long.TryParse(payload, out var value))");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new FormatException($\"Field '{typeName}.{fieldName}' expected an Int64 payload.\");");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return value;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static string EncodeBooleanField(bool value)");
        builder.AppendLine("    {");
        builder.AppendLine("        return value ? \"1\" : \"0\";");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static bool DecodeBooleanField(string payload, string typeName, string fieldName)");
        builder.AppendLine("    {");
        builder.AppendLine("        return payload switch");
        builder.AppendLine("        {");
        builder.AppendLine("            \"1\" => true,");
        builder.AppendLine("            \"0\" => false,");
        builder.AppendLine("            _ => throw new FormatException($\"Field '{typeName}.{fieldName}' expected a boolean payload.\")");
        builder.AppendLine("        };");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine($"public sealed partial class {spec.ContractName} : CSharpSmartContract");
        builder.AppendLine("{");

        foreach (var method in spec.Methods)
        {
            builder.AppendLine("    [ContractMethod]");

            if (IsEmpty(method.InputType))
            {
                builder.AppendLine($"    public {method.OutputType} {method.Name}()");
            }
            else
            {
                builder.AppendLine($"    public {method.OutputType} {method.Name}({method.InputType} input)");
            }

            builder.AppendLine("    {");

            if (IsEmpty(method.OutputType))
            {
                builder.AppendLine("        return Empty.Value;");
            }
            else
            {
                var outputType = typesByName[method.OutputType];
                var arguments = outputType.Fields
                    .Select(field => RenderExpression(field, method, field.Name))
                    .ToArray();
                builder.AppendLine($"        return new {method.OutputType}({string.Join(", ", arguments)});");
            }

            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("    protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)");
        builder.AppendLine("    {");
        builder.AppendLine("        return methodName switch");
        builder.AppendLine("        {");

        foreach (var method in spec.Methods)
        {
            builder.AppendLine($"            \"{method.Name}\" => {RenderDispatchInvocation(method)},");
        }

        builder.AppendLine($"            _ => throw new ContractMethodNotFoundException(typeof({spec.ContractName}), methodName)");
        builder.AppendLine("        };");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("}");
        builder.AppendLine();

        foreach (var type in spec.Types)
        {
            builder.AppendLine($"public readonly record struct {type.Name}({RenderRecordFields(type)}) : IContractCodec<{type.Name}>");
            builder.AppendLine("{");
            builder.AppendLine($"    public static {type.Name} Decode(ReadOnlySpan<byte> input)");
            builder.AppendLine("    {");
            builder.AppendLine("        var payload = Encoding.UTF8.GetString(input);");
            builder.AppendLine("        var segments = payload.Length == 0 ? Array.Empty<string>() : payload.Split('\\n');");
            builder.AppendLine($"        if (segments.Length != {type.Fields.Count})");
            builder.AppendLine("        {");
            builder.AppendLine($"            throw new FormatException(\"Expected {type.Fields.Count} field segment(s) for '{type.Name}'.\");");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine($"        return new {type.Name}({RenderDecodeArguments(type)});");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine($"    public static byte[] Encode({type.Name} value)");
            builder.AppendLine("    {");
            builder.AppendLine($"        return Encoding.UTF8.GetBytes(string.Join(\"\\n\", new[] {{ {RenderEncodeArguments(type)} }}));");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string RenderRecordFields(ContractTypeSpec type)
    {
        return string.Join(", ", type.Fields.Select(static field => $"{RenderFieldType(field.Type)} {field.Name}"));
    }

    private static string RenderDecodeArguments(ContractTypeSpec type)
    {
        return string.Join(
            ", ",
            type.Fields.Select(
                (field, index) => field.Type switch
                {
                    ContractPrimitiveType.String => $"GeneratedCodec.DecodeStringField(segments[{index}])",
                    ContractPrimitiveType.Int64 => $"GeneratedCodec.DecodeInt64Field(segments[{index}], \"{type.Name}\", \"{field.Name}\")",
                    ContractPrimitiveType.Boolean => $"GeneratedCodec.DecodeBooleanField(segments[{index}], \"{type.Name}\", \"{field.Name}\")",
                    _ => throw new InvalidOperationException($"Unsupported primitive type '{field.Type}'.")
                }));
    }

    private static string RenderEncodeArguments(ContractTypeSpec type)
    {
        return string.Join(
            ", ",
            type.Fields.Select(
                static field => field.Type switch
                {
                    ContractPrimitiveType.String => $"GeneratedCodec.EncodeStringField(value.{field.Name})",
                    ContractPrimitiveType.Int64 => $"GeneratedCodec.EncodeInt64Field(value.{field.Name})",
                    ContractPrimitiveType.Boolean => $"GeneratedCodec.EncodeBooleanField(value.{field.Name})",
                    _ => throw new InvalidOperationException($"Unsupported primitive type '{field.Type}'.")
                }));
    }

    private static string RenderExpression(
        ContractFieldSpec outputField,
        ContractMethodSpec method,
        string outputFieldName)
    {
        var block = method.LogicBlocks.Single(candidate => string.Equals(candidate.OutputField, outputFieldName, StringComparison.Ordinal));

        return block.Kind switch
        {
            ContractLogicBlockKind.AssignLiteral => RenderLiteralExpression(outputField.Type, block),
            ContractLogicBlockKind.CopyInput => $"input.{block.InputField}",
            ContractLogicBlockKind.ConcatStrings => RenderConcatExpression(block),
            _ => throw new InvalidOperationException($"Unsupported logic block kind '{block.Kind}'.")
        };
    }

    private static string RenderLiteralExpression(ContractPrimitiveType primitiveType, ContractLogicBlockSpec block)
    {
        return primitiveType switch
        {
            ContractPrimitiveType.String => Quote(block.StringLiteral ?? string.Empty),
            ContractPrimitiveType.Int64 => $"{block.Int64Literal.GetValueOrDefault()}L",
            ContractPrimitiveType.Boolean => block.BooleanLiteral.GetValueOrDefault() ? "true" : "false",
            _ => throw new InvalidOperationException($"Unsupported primitive type '{primitiveType}'.")
        };
    }

    private static string RenderConcatExpression(ContractLogicBlockSpec block)
    {
        var segments = block.Segments.Select(
            static segment => segment.Kind switch
            {
                ContractStringSegmentKind.Literal => Quote(segment.Value),
                ContractStringSegmentKind.InputField => $"input.{segment.Value}",
                _ => throw new InvalidOperationException($"Unsupported string segment kind '{segment.Kind}'.")
            });

        return $"string.Concat({string.Join(", ", segments)})";
    }

    private static string RenderFieldType(ContractPrimitiveType primitiveType)
    {
        return primitiveType switch
        {
            ContractPrimitiveType.String => "string",
            ContractPrimitiveType.Int64 => "long",
            ContractPrimitiveType.Boolean => "bool",
            _ => throw new InvalidOperationException($"Unsupported primitive type '{primitiveType}'.")
        };
    }

    private static string RenderDispatchInvocation(ContractMethodSpec method)
    {
        var invocation = IsEmpty(method.InputType)
            ? $"{method.Name}()"
            : $"{method.Name}(ContractCodec.Decode<{method.InputType}>(\"{method.Name}\", input.Span))";

        return $"ContractCodec.Encode<{method.OutputType}>({invocation})";
    }

    private static bool IsEmpty(string typeName)
    {
        return string.Equals(typeName, "Empty", StringComparison.Ordinal);
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
