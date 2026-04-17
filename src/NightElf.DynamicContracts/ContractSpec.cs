namespace NightElf.DynamicContracts;

public static class DynamicContractDefaults
{
    public const string DefaultNamespace = "NightElf.DynamicContracts.Generated";

    public static string NormalizeNamespace(string? contractNamespace)
    {
        return string.IsNullOrWhiteSpace(contractNamespace)
            ? DefaultNamespace
            : contractNamespace.Trim();
    }
}

public sealed class ContractSpec
{
    public string ContractName { get; init; } = string.Empty;

    public string Namespace { get; init; } = DynamicContractDefaults.DefaultNamespace;

    public IReadOnlyList<ContractTypeSpec> Types { get; init; } = [];

    public IReadOnlyList<ContractMethodSpec> Methods { get; init; } = [];
}

public sealed class ContractTypeSpec
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<ContractFieldSpec> Fields { get; init; } = [];
}

public sealed class ContractFieldSpec
{
    public string Name { get; init; } = string.Empty;

    public ContractPrimitiveType Type { get; init; }
}

public enum ContractPrimitiveType
{
    String = 0,
    Int64 = 1,
    Boolean = 2
}

public sealed class ContractMethodSpec
{
    public string Name { get; init; } = string.Empty;

    public string InputType { get; init; } = "Empty";

    public string OutputType { get; init; } = "Empty";

    public IReadOnlyList<ContractLogicBlockSpec> LogicBlocks { get; init; } = [];
}

public sealed class ContractLogicBlockSpec
{
    public ContractLogicBlockKind Kind { get; init; }

    public string OutputField { get; init; } = string.Empty;

    public string? InputField { get; init; }

    public string? StringLiteral { get; init; }

    public long? Int64Literal { get; init; }

    public bool? BooleanLiteral { get; init; }

    public IReadOnlyList<ContractStringSegmentSpec> Segments { get; init; } = [];
}

public enum ContractLogicBlockKind
{
    AssignLiteral = 0,
    CopyInput = 1,
    ConcatStrings = 2
}

public sealed class ContractStringSegmentSpec
{
    public ContractStringSegmentKind Kind { get; init; }

    public string Value { get; init; } = string.Empty;
}

public enum ContractStringSegmentKind
{
    Literal = 0,
    InputField = 1
}
