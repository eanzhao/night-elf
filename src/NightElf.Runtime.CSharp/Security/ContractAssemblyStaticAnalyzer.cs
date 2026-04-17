using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection.Emit;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace NightElf.Runtime.CSharp.Security;

public sealed class ContractAssemblyStaticAnalyzer
{
    private const string UnsafeTypeName = "System.Runtime.CompilerServices.Unsafe";

    private static readonly HashSet<string> AllowedThreadingTypes = new(StringComparer.Ordinal)
    {
        "System.Threading.CancellationToken"
    };

    private static readonly HashSet<string> AllowedUnsafeHelperMethods = new(StringComparer.Ordinal)
    {
        "AsRef",
        "As",
        "Add"
    };

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static opcode => unchecked((ushort)opcode.Value));

    public ContractStaticAnalysisResult Analyze(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new InvalidOperationException($"Assembly '{assemblyPath}' does not contain ECMA-335 metadata.");
        }

        var metadataReader = peReader.GetMetadataReader();
        var violations = new Dictionary<string, ContractStaticAnalysisViolation>(StringComparer.Ordinal);

        AnalyzePInvoke(metadataReader, violations);
        AnalyzeTypeReferences(metadataReader, violations);
        AnalyzeMemberReferences(metadataReader, violations);
        AnalyzeMethodBodies(peReader, metadataReader, violations);

        return new ContractStaticAnalysisResult(violations.Values.ToArray());
    }

    private static void AnalyzePInvoke(
        MetadataReader metadataReader,
        IDictionary<string, ContractStaticAnalysisViolation> violations)
    {
        foreach (var handle in metadataReader.MethodDefinitions)
        {
            var method = metadataReader.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                AddViolation(
                    violations,
                    "NEC004",
                    "P/Invoke is not allowed in contract assemblies.");
                return;
            }
        }
    }

    private static void AnalyzeTypeReferences(
        MetadataReader metadataReader,
        IDictionary<string, ContractStaticAnalysisViolation> violations)
    {
        foreach (var handle in metadataReader.TypeReferences)
        {
            var typeReference = metadataReader.GetTypeReference(handle);
            var typeNamespace = metadataReader.GetString(typeReference.Namespace);
            var typeName = metadataReader.GetString(typeReference.Name);
            EvaluateReferencedType(violations, typeNamespace, $"{typeNamespace}.{typeName}");
        }
    }

    private static void AnalyzeMemberReferences(
        MetadataReader metadataReader,
        IDictionary<string, ContractStaticAnalysisViolation> violations)
    {
        foreach (var handle in metadataReader.MemberReferences)
        {
            var memberReference = metadataReader.GetMemberReference(handle);
            if (!TryGetReferencedTypeName(metadataReader, memberReference.Parent, out var declaringType))
            {
                continue;
            }

            var memberName = metadataReader.GetString(memberReference.Name);
            if (IsAllowedReflectionAttributeConstructor(declaringType.FullName, memberName))
            {
                continue;
            }

            EvaluateReferencedType(violations, declaringType.Namespace, $"{declaringType.FullName}.{memberName}");
        }
    }

    private static void AnalyzeMethodBodies(
        PEReader peReader,
        MetadataReader metadataReader,
        IDictionary<string, ContractStaticAnalysisViolation> violations)
    {
        foreach (var handle in metadataReader.MethodDefinitions)
        {
            var method = metadataReader.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
            var il = body.GetILBytes();
            if (ContainsUnsafeOpcodeSequence(il) || ContainsUnsafeApiCall(metadataReader, il))
            {
                AddViolation(
                    violations,
                    "NEC001",
                    "Unsafe IL instructions or helper API calls are not allowed in contract assemblies.");
                return;
            }
        }
    }

    private static void EvaluateReferencedType(
        IDictionary<string, ContractStaticAnalysisViolation> violations,
        string? typeNamespace,
        string reference)
    {
        if (string.IsNullOrWhiteSpace(typeNamespace))
        {
            return;
        }

        if (string.Equals(reference, UnsafeTypeName, StringComparison.Ordinal) ||
            reference.StartsWith(UnsafeTypeName + ".", StringComparison.Ordinal))
        {
            return;
        }

        if (IsAllowedReflectionAttributeType(reference))
        {
            return;
        }

        if (typeNamespace.StartsWith("System.Reflection", StringComparison.Ordinal))
        {
            AddViolation(
                violations,
                "NEC002",
                $"Reflection API reference '{reference}' is not allowed in contract assemblies.");
            return;
        }

        if (typeNamespace.StartsWith("System.IO", StringComparison.Ordinal))
        {
            AddViolation(
                violations,
                "NEC003",
                $"System.IO API reference '{reference}' is not allowed in contract assemblies.");
            return;
        }

        if (typeNamespace.StartsWith("System.Threading", StringComparison.Ordinal) &&
            !IsAllowedThreadingReference(reference))
        {
            AddViolation(
                violations,
                "NEC005",
                $"System.Threading API reference '{reference}' is not allowed in contract assemblies.");
        }
    }

    private static bool TryGetReferencedTypeName(
        MetadataReader metadataReader,
        EntityHandle handle,
        out ReferencedTypeInfo typeInfo)
    {
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
            {
                var memberReference = metadataReader.GetMemberReference((MemberReferenceHandle)handle);
                return TryGetReferencedTypeName(metadataReader, memberReference.Parent, out typeInfo);
            }
            case HandleKind.MethodDefinition:
            {
                var methodDefinition = metadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return TryGetReferencedTypeName(metadataReader, methodDefinition.GetDeclaringType(), out typeInfo);
            }
            case HandleKind.MethodSpecification:
            {
                var methodSpecification = metadataReader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return TryGetReferencedTypeName(metadataReader, methodSpecification.Method, out typeInfo);
            }
            case HandleKind.TypeReference:
            {
                var typeReference = metadataReader.GetTypeReference((TypeReferenceHandle)handle);
                var typeNamespace = metadataReader.GetString(typeReference.Namespace);
                var typeName = metadataReader.GetString(typeReference.Name);
                typeInfo = new ReferencedTypeInfo(typeNamespace, $"{typeNamespace}.{typeName}");
                return true;
            }
            case HandleKind.TypeDefinition:
            {
                var typeDefinition = metadataReader.GetTypeDefinition((TypeDefinitionHandle)handle);
                var typeNamespace = metadataReader.GetString(typeDefinition.Namespace);
                var typeName = metadataReader.GetString(typeDefinition.Name);
                typeInfo = new ReferencedTypeInfo(typeNamespace, $"{typeNamespace}.{typeName}");
                return true;
            }
            case HandleKind.TypeSpecification:
            default:
                typeInfo = default;
                return false;
        }
    }

    private static bool ContainsUnsafeOpcodeSequence(IReadOnlyList<byte>? il)
    {
        if (il is null || il.Count == 0)
        {
            return false;
        }

        var offset = 0;
        while (offset < il.Count)
        {
            if (!TryReadOpcode(il, ref offset, out var opcode, out var operandSize))
            {
                return false;
            }

            if (opcode == OpCodes.Calli || opcode == OpCodes.Localloc)
            {
                return true;
            }

            offset += operandSize;
        }

        return false;
    }

    private static bool ContainsUnsafeApiCall(MetadataReader metadataReader, IReadOnlyList<byte>? il)
    {
        if (il is null || il.Count == 0)
        {
            return false;
        }

        var offset = 0;
        while (offset < il.Count)
        {
            if (!TryReadOpcode(il, ref offset, out var opcode, out var operandSize))
            {
                return false;
            }

            if (opcode.OperandType == OperandType.InlineMethod &&
                (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj || opcode == OpCodes.Ldftn || opcode == OpCodes.Ldvirtftn) &&
                offset + sizeof(int) <= il.Count)
            {
                var token = ReadInt32(il, offset);
                if (TryGetReferencedMemberInfo(metadataReader, MetadataTokens.EntityHandle(token), out var memberInfo) &&
                    string.Equals(memberInfo.DeclaringType.FullName, UnsafeTypeName, StringComparison.Ordinal) &&
                    !AllowedUnsafeHelperMethods.Contains(memberInfo.MemberName))
                {
                    return true;
                }
            }

            offset += operandSize;
        }

        return false;
    }

    private static bool TryReadOpcode(
        IReadOnlyList<byte> il,
        ref int offset,
        out OpCode opcode,
        out int operandSize)
    {
        if (offset >= il.Count)
        {
            opcode = default;
            operandSize = 0;
            return false;
        }

        ushort opcodeValue = il[offset++];
        if (opcodeValue == 0xFE)
        {
            if (offset >= il.Count)
            {
                opcode = default;
                operandSize = 0;
                return false;
            }

            var secondByte = (ushort)il[offset++];
            opcodeValue = (ushort)((opcodeValue << 8) | secondByte);
        }

        if (!OpCodesByValue.TryGetValue(opcodeValue, out opcode))
        {
            operandSize = 0;
            return false;
        }

        operandSize = GetOperandSize(opcode.OperandType, il, offset);
        return true;
    }

    private static int GetOperandSize(OperandType operandType, IReadOnlyList<byte> il, int operandOffset)
    {
        return operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget => 4,
            OperandType.InlineField => 4,
            OperandType.InlineI => 4,
            OperandType.InlineMethod => 4,
            OperandType.InlineSig => 4,
            OperandType.InlineString => 4,
            OperandType.InlineTok => 4,
            OperandType.InlineType => 4,
            OperandType.ShortInlineR => 4,
            OperandType.InlineI8 => 8,
            OperandType.InlineR => 8,
            OperandType.InlineSwitch => GetSwitchOperandSize(il, operandOffset),
            _ => 0
        };
    }

    private static int GetSwitchOperandSize(IReadOnlyList<byte> il, int operandOffset)
    {
        if (operandOffset + sizeof(int) > il.Count)
        {
            return 0;
        }

        var targetCount = ReadInt32(il, operandOffset);
        return sizeof(int) + (targetCount * sizeof(int));
    }

    private static int ReadInt32(IReadOnlyList<byte> bytes, int offset)
    {
        return bytes[offset] |
               (bytes[offset + 1] << 8) |
               (bytes[offset + 2] << 16) |
               (bytes[offset + 3] << 24);
    }

    private static bool TryGetReferencedMemberInfo(
        MetadataReader metadataReader,
        EntityHandle handle,
        out ReferencedMemberInfo memberInfo)
    {
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
            {
                var memberReference = metadataReader.GetMemberReference((MemberReferenceHandle)handle);
                if (TryGetReferencedTypeName(metadataReader, memberReference.Parent, out var typeInfo))
                {
                    memberInfo = new ReferencedMemberInfo(typeInfo, metadataReader.GetString(memberReference.Name));
                    return true;
                }

                break;
            }
            case HandleKind.MethodDefinition:
            {
                var methodDefinition = metadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
                if (TryGetReferencedTypeName(metadataReader, methodDefinition.GetDeclaringType(), out var typeInfo))
                {
                    memberInfo = new ReferencedMemberInfo(typeInfo, metadataReader.GetString(methodDefinition.Name));
                    return true;
                }

                break;
            }
            case HandleKind.MethodSpecification:
            {
                var methodSpecification = metadataReader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return TryGetReferencedMemberInfo(metadataReader, methodSpecification.Method, out memberInfo);
            }
        }

        memberInfo = default;
        return false;
    }

    private static bool IsAllowedThreadingReference(string reference)
    {
        return AllowedThreadingTypes.Contains(reference) ||
               reference.StartsWith("System.Threading.CancellationToken.", StringComparison.Ordinal);
    }

    private static bool IsAllowedReflectionAttributeType(string reference)
    {
        return reference.StartsWith("System.Reflection.Assembly", StringComparison.Ordinal) &&
               reference.EndsWith("Attribute", StringComparison.Ordinal);
    }

    private static bool IsAllowedReflectionAttributeConstructor(string typeName, string memberName)
    {
        return string.Equals(memberName, ".ctor", StringComparison.Ordinal) &&
               IsAllowedReflectionAttributeType(typeName);
    }

    private static void AddViolation(
        IDictionary<string, ContractStaticAnalysisViolation> violations,
        string ruleId,
        string message)
    {
        violations.TryAdd(
            $"{ruleId}:{message}",
            new ContractStaticAnalysisViolation
            {
                RuleId = ruleId,
                Message = message
            });
    }

    private readonly record struct ReferencedTypeInfo(string Namespace, string FullName);

    private readonly record struct ReferencedMemberInfo(ReferencedTypeInfo DeclaringType, string MemberName);
}
