namespace NightElf.DynamicContracts;

internal static class ContractSpecValidator
{
    public static void Validate(ContractSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (!IsValidIdentifier(spec.ContractName))
        {
            throw new InvalidOperationException($"Contract name '{spec.ContractName}' is not a valid C# identifier.");
        }

        ValidateNamespace(DynamicContractDefaults.NormalizeNamespace(spec.Namespace));

        if (spec.Types.Count == 0)
        {
            throw new InvalidOperationException("ContractSpec must declare at least one custom codec type.");
        }

        if (spec.Methods.Count == 0)
        {
            throw new InvalidOperationException("ContractSpec must declare at least one contract method.");
        }

        var typesByName = new Dictionary<string, ContractTypeSpec>(StringComparer.Ordinal)
        {
            ["Empty"] = new ContractTypeSpec
            {
                Name = "Empty",
                Fields = []
            }
        };

        foreach (var type in spec.Types)
        {
            if (!IsValidIdentifier(type.Name))
            {
                throw new InvalidOperationException($"Type name '{type.Name}' is not a valid C# identifier.");
            }

            if (string.Equals(type.Name, spec.ContractName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Type '{type.Name}' conflicts with the generated contract class name.");
            }

            if (!typesByName.TryAdd(type.Name, type))
            {
                throw new InvalidOperationException($"Type '{type.Name}' is declared multiple times.");
            }

            if (type.Fields.Count == 0)
            {
                throw new InvalidOperationException($"Type '{type.Name}' must declare at least one field. Use Empty for zero-field payloads.");
            }

            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in type.Fields)
            {
                if (!IsValidIdentifier(field.Name))
                {
                    throw new InvalidOperationException($"Field '{type.Name}.{field.Name}' is not a valid C# identifier.");
                }

                if (!fieldNames.Add(field.Name))
                {
                    throw new InvalidOperationException($"Field '{type.Name}.{field.Name}' is declared multiple times.");
                }
            }
        }

        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in spec.Methods)
        {
            if (!IsValidIdentifier(method.Name))
            {
                throw new InvalidOperationException($"Method name '{method.Name}' is not a valid C# identifier.");
            }

            if (!methodNames.Add(method.Name))
            {
                throw new InvalidOperationException($"Method '{method.Name}' is declared multiple times.");
            }

            if (!typesByName.TryGetValue(method.InputType, out var inputType))
            {
                throw new InvalidOperationException($"Method '{method.Name}' references unknown input type '{method.InputType}'.");
            }

            if (!typesByName.TryGetValue(method.OutputType, out var outputType))
            {
                throw new InvalidOperationException($"Method '{method.Name}' references unknown output type '{method.OutputType}'.");
            }

            ValidateLogicBlocks(method, inputType, outputType);
        }
    }

    private static void ValidateLogicBlocks(
        ContractMethodSpec method,
        ContractTypeSpec inputType,
        ContractTypeSpec outputType)
    {
        if (string.Equals(outputType.Name, "Empty", StringComparison.Ordinal))
        {
            if (method.LogicBlocks.Count > 0)
            {
                throw new InvalidOperationException($"Method '{method.Name}' returns Empty and must not declare logic blocks.");
            }

            return;
        }

        if (method.LogicBlocks.Count != outputType.Fields.Count)
        {
            throw new InvalidOperationException(
                $"Method '{method.Name}' must declare exactly one logic block for each output field of '{outputType.Name}'.");
        }

        var inputFields = inputType.Fields.ToDictionary(static field => field.Name, StringComparer.Ordinal);
        var outputFields = outputType.Fields.ToDictionary(static field => field.Name, StringComparer.Ordinal);
        var assignedFields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in method.LogicBlocks)
        {
            if (!outputFields.TryGetValue(block.OutputField, out var outputField))
            {
                throw new InvalidOperationException(
                    $"Method '{method.Name}' writes to unknown output field '{block.OutputField}'.");
            }

            if (!assignedFields.Add(block.OutputField))
            {
                throw new InvalidOperationException(
                    $"Method '{method.Name}' assigns output field '{block.OutputField}' more than once.");
            }

            switch (block.Kind)
            {
                case ContractLogicBlockKind.AssignLiteral:
                    ValidateLiteralBlock(method, outputField, block);
                    break;
                case ContractLogicBlockKind.CopyInput:
                    ValidateCopyInputBlock(method, inputFields, outputField, block);
                    break;
                case ContractLogicBlockKind.ConcatStrings:
                    ValidateConcatBlock(method, inputFields, outputField, block);
                    break;
                default:
                    throw new InvalidOperationException($"Method '{method.Name}' contains unsupported logic block kind '{block.Kind}'.");
            }
        }
    }

    private static void ValidateLiteralBlock(
        ContractMethodSpec method,
        ContractFieldSpec outputField,
        ContractLogicBlockSpec block)
    {
        var valid = outputField.Type switch
        {
            ContractPrimitiveType.String => block.StringLiteral is not null,
            ContractPrimitiveType.Int64 => block.Int64Literal.HasValue,
            ContractPrimitiveType.Boolean => block.BooleanLiteral.HasValue,
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException(
                $"Method '{method.Name}' literal block for '{block.OutputField}' does not match declared type '{outputField.Type}'.");
        }
    }

    private static void ValidateCopyInputBlock(
        ContractMethodSpec method,
        IReadOnlyDictionary<string, ContractFieldSpec> inputFields,
        ContractFieldSpec outputField,
        ContractLogicBlockSpec block)
    {
        if (string.IsNullOrWhiteSpace(block.InputField))
        {
            throw new InvalidOperationException($"Method '{method.Name}' copy-input block for '{block.OutputField}' is missing InputField.");
        }

        if (!inputFields.TryGetValue(block.InputField, out var inputField))
        {
            throw new InvalidOperationException(
                $"Method '{method.Name}' copy-input block references unknown input field '{block.InputField}'.");
        }

        if (inputField.Type != outputField.Type)
        {
            throw new InvalidOperationException(
                $"Method '{method.Name}' cannot copy '{block.InputField}' ({inputField.Type}) into '{block.OutputField}' ({outputField.Type}).");
        }
    }

    private static void ValidateConcatBlock(
        ContractMethodSpec method,
        IReadOnlyDictionary<string, ContractFieldSpec> inputFields,
        ContractFieldSpec outputField,
        ContractLogicBlockSpec block)
    {
        if (outputField.Type != ContractPrimitiveType.String)
        {
            throw new InvalidOperationException(
                $"Method '{method.Name}' concat block can only target string fields. '{block.OutputField}' is '{outputField.Type}'.");
        }

        if (block.Segments.Count == 0)
        {
            throw new InvalidOperationException($"Method '{method.Name}' concat block for '{block.OutputField}' must contain at least one segment.");
        }

        foreach (var segment in block.Segments)
        {
            switch (segment.Kind)
            {
                case ContractStringSegmentKind.Literal:
                    break;
                case ContractStringSegmentKind.InputField:
                    if (!inputFields.TryGetValue(segment.Value, out var inputField))
                    {
                        throw new InvalidOperationException(
                            $"Method '{method.Name}' concat block references unknown input field '{segment.Value}'.");
                    }

                    if (inputField.Type != ContractPrimitiveType.String)
                    {
                        throw new InvalidOperationException(
                            $"Method '{method.Name}' concat block can only reference string input fields. '{segment.Value}' is '{inputField.Type}'.");
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Method '{method.Name}' contains unsupported string segment kind '{segment.Kind}'.");
            }
        }
    }

    private static void ValidateNamespace(string contractNamespace)
    {
        foreach (var segment in contractNamespace.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsValidIdentifier(segment))
            {
                throw new InvalidOperationException($"Namespace segment '{segment}' is not a valid C# identifier.");
            }
        }
    }

    private static bool IsValidIdentifier(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!(char.IsLetter(candidate[0]) || candidate[0] == '_'))
        {
            return false;
        }

        for (var i = 1; i < candidate.Length; i++)
        {
            var character = candidate[i];
            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
