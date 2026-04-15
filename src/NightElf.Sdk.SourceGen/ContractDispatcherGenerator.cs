using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NightElf.Sdk.SourceGen;

[Generator(LanguageNames.CSharp)]
public sealed class ContractDispatcherGenerator : IIncrementalGenerator
{
    private const string ContractBaseTypeName = "NightElf.Sdk.CSharp.CSharpSmartContract";
    private const string ContractMethodAttributeName = "NightElf.Sdk.CSharp.ContractMethodAttribute";
    private const string CodecInterfaceTypeName = "NightElf.Sdk.CSharp.IContractCodec`1";

    private static readonly DiagnosticDescriptor ContractMustBePartial = new(
        id: "NE1001",
        title: "Contract must be partial",
        messageFormat: "Contract '{0}' must be declared partial for NightElf dispatch generation.",
        category: "NightElf.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidContractMethodOwner = new(
        id: "NE1002",
        title: "Contract method owner is invalid",
        messageFormat: "Method '{0}' must belong to a type derived from CSharpSmartContract.",
        category: "NightElf.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidContractMethodSignature = new(
        id: "NE1003",
        title: "Contract method signature is invalid",
        messageFormat: "Method '{0}' must be a public instance method with zero or one parameter and no ref/out modifiers.",
        category: "NightElf.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidCodecType = new(
        id: "NE1004",
        title: "Contract codec type is invalid",
        messageFormat: "Type '{0}' must implement IContractCodec<{0}> to participate in generated dispatch.",
        category: "NightElf.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateDispatchName = new(
        id: "NE1005",
        title: "Duplicate contract dispatch name",
        messageFormat: "Contract '{0}' declares multiple [ContractMethod] members for dispatch name '{1}'.",
        category: "NightElf.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contractMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: ContractMethodAttributeName,
            predicate: static (node, _) => node is MethodDeclarationSyntax,
            transform: static (generatorContext, _) => CreateMethodCandidate(generatorContext))
            .Where(static candidate => candidate is not null);

        var combined = context.CompilationProvider.Combine(contractMethods.Collect());
        context.RegisterSourceOutput(combined, static (productionContext, source) =>
        {
            Execute(source.Left, source.Right!, productionContext);
        });
    }

    private static ContractMethodCandidate? CreateMethodCandidate(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol methodSymbol)
        {
            return null;
        }

        var dispatchName = methodSymbol.Name;
        if (context.Attributes.Length > 0)
        {
            var attribute = context.Attributes[0];
            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is string explicitName &&
                !string.IsNullOrWhiteSpace(explicitName))
            {
                dispatchName = explicitName;
            }
        }

        return new ContractMethodCandidate(methodSymbol.ContainingType, methodSymbol, dispatchName);
    }

    private static void Execute(
        Compilation compilation,
        ImmutableArray<ContractMethodCandidate?> candidates,
        SourceProductionContext context)
    {
        if (candidates.IsDefaultOrEmpty)
        {
            return;
        }

        var contractBaseType = compilation.GetTypeByMetadataName(ContractBaseTypeName);
        var codecInterfaceType = compilation.GetTypeByMetadataName(CodecInterfaceTypeName);
        if (contractBaseType is null || codecInterfaceType is null)
        {
            return;
        }

        var groupedCandidates = candidates
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!);

        var contracts = new Dictionary<INamedTypeSymbol, List<ContractMethodCandidate>>(SymbolEqualityComparer.Default);
        foreach (var candidate in groupedCandidates)
        {
            if (!contracts.TryGetValue(candidate.ContractSymbol, out var bucket))
            {
                bucket = [];
                contracts[candidate.ContractSymbol] = bucket;
            }

            bucket.Add(candidate);
        }

        foreach (var contractEntry in contracts)
        {
            var contractSymbol = contractEntry.Key;
            var contractGroup = contractEntry.Value;
            if (!DerivesFrom(contractSymbol, contractBaseType))
            {
                foreach (var candidate in contractGroup)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InvalidContractMethodOwner,
                        candidate.MethodSymbol.Locations.FirstOrDefault(),
                        candidate.MethodSymbol.ToDisplayString()));
                }

                continue;
            }

            if (!IsPartial(contractSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ContractMustBePartial,
                    contractSymbol.Locations.FirstOrDefault(),
                    contractSymbol.ToDisplayString()));
                continue;
            }

            var duplicateDispatchNames = contractGroup
                .GroupBy(static candidate => candidate.DispatchName, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);

            foreach (var duplicateDispatch in duplicateDispatchNames)
            {
                foreach (var candidate in duplicateDispatch.Value)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateDispatchName,
                        candidate.MethodSymbol.Locations.FirstOrDefault(),
                        contractSymbol.ToDisplayString(),
                        duplicateDispatch.Key));
                }
            }

            var validMethods = new List<ContractMethodModel>();
            foreach (var candidate in contractGroup)
            {
                if (duplicateDispatchNames.ContainsKey(candidate.DispatchName))
                {
                    continue;
                }

                if (!TryCreateMethodModel(candidate, codecInterfaceType, context, out var methodModel))
                {
                    continue;
                }

                validMethods.Add(methodModel);
            }

            var source = GenerateDispatcherSource(contractSymbol, validMethods);
            context.AddSource(GetHintName(contractSymbol), source);
        }
    }

    private static bool TryCreateMethodModel(
        ContractMethodCandidate candidate,
        INamedTypeSymbol codecInterfaceType,
        SourceProductionContext context,
        out ContractMethodModel methodModel)
    {
        methodModel = default;

        var methodSymbol = candidate.MethodSymbol;
        if (methodSymbol.MethodKind != MethodKind.Ordinary ||
            methodSymbol.IsStatic ||
            methodSymbol.DeclaredAccessibility != Accessibility.Public ||
            methodSymbol.Parameters.Length > 1 ||
            methodSymbol.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidContractMethodSignature,
                methodSymbol.Locations.FirstOrDefault(),
                methodSymbol.ToDisplayString()));
            return false;
        }

        if (!ImplementsCodec(methodSymbol.ReturnType, codecInterfaceType))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidCodecType,
                methodSymbol.Locations.FirstOrDefault(),
                methodSymbol.ReturnType.ToDisplayString()));
            return false;
        }

        ITypeSymbol? inputType = null;
        if (methodSymbol.Parameters.Length == 1)
        {
            inputType = methodSymbol.Parameters[0].Type;
            if (!ImplementsCodec(inputType, codecInterfaceType))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidCodecType,
                    methodSymbol.Locations.FirstOrDefault(),
                    inputType.ToDisplayString()));
                return false;
            }
        }

        methodModel = new ContractMethodModel(candidate.DispatchName, methodSymbol.Name, inputType, methodSymbol.ReturnType);
        return true;
    }

    private static bool ImplementsCodec(ITypeSymbol typeSymbol, INamedTypeSymbol codecInterfaceType)
    {
        if (typeSymbol is not INamedTypeSymbol namedTypeSymbol)
        {
            return false;
        }

        foreach (var interfaceSymbol in namedTypeSymbol.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(interfaceSymbol.OriginalDefinition, codecInterfaceType))
            {
                continue;
            }

            if (interfaceSymbol.TypeArguments.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(interfaceSymbol.TypeArguments[0], namedTypeSymbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DerivesFrom(INamedTypeSymbol? symbol, INamedTypeSymbol baseType)
    {
        for (var current = symbol; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPartial(INamedTypeSymbol symbol)
    {
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is not ClassDeclarationSyntax declarationSyntax ||
                !declarationSyntax.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
            {
                return false;
            }
        }

        return symbol.DeclaringSyntaxReferences.Length > 0;
    }

    private static string GenerateDispatcherSource(
        INamedTypeSymbol contractSymbol,
        IReadOnlyList<ContractMethodModel> methods)
    {
        var contractTypeName = contractSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var namespaceName = contractSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : contractSymbol.ContainingNamespace.ToDisplayString();

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");

        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            builder.Append("namespace ");
            builder.Append(namespaceName);
            builder.AppendLine(";");
            builder.AppendLine();
        }

        var accessibilityKeyword = GetAccessibilityKeyword(contractSymbol.DeclaredAccessibility);
        if (!string.IsNullOrWhiteSpace(accessibilityKeyword))
        {
            builder.Append(accessibilityKeyword);
            builder.Append(' ');
        }

        builder.Append("partial class ");
        builder.Append(contractSymbol.Name);
        builder.AppendLine();
        builder.AppendLine("{");
        builder.AppendLine("    protected override byte[] DispatchCore(string methodName, global::System.ReadOnlyMemory<byte> input)");
        builder.AppendLine("    {");
        builder.AppendLine("        return methodName switch");
        builder.AppendLine("        {");

        foreach (var method in methods.OrderBy(static item => item.DispatchName, StringComparer.Ordinal))
        {
            builder.Append("            \"");
            builder.Append(method.DispatchName);
            builder.Append("\" => ");

            var returnTypeName = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (method.InputType is null)
            {
                builder.Append("global::NightElf.Sdk.CSharp.ContractCodec.Encode<");
                builder.Append(returnTypeName);
                builder.Append(">(");
                builder.Append(method.MethodName);
                builder.AppendLine("()),");
                continue;
            }

            var inputTypeName = method.InputType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            builder.Append("global::NightElf.Sdk.CSharp.ContractCodec.Encode<");
            builder.Append(returnTypeName);
            builder.Append(">(");
            builder.Append(method.MethodName);
            builder.Append("(global::NightElf.Sdk.CSharp.ContractCodec.Decode<");
            builder.Append(inputTypeName);
            builder.Append(">(\"");
            builder.Append(method.DispatchName);
            builder.Append("\", input.Span))),");
            builder.AppendLine();
        }

        builder.Append("            _ => throw new global::NightElf.Sdk.CSharp.ContractMethodNotFoundException(typeof(");
        builder.Append(contractTypeName);
        builder.AppendLine("), methodName)");
        builder.AppendLine("        };");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string GetHintName(INamedTypeSymbol contractSymbol)
    {
        var fullName = contractSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty)
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('.', '_');

        return $"{fullName}.ContractDispatch.g.cs";
    }

    private static string? GetAccessibilityKeyword(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            _ => null
        };
    }

    private sealed class ContractMethodCandidate
    {
        public ContractMethodCandidate(
            INamedTypeSymbol contractSymbol,
            IMethodSymbol methodSymbol,
            string dispatchName)
        {
            ContractSymbol = contractSymbol;
            MethodSymbol = methodSymbol;
            DispatchName = dispatchName;
        }

        public INamedTypeSymbol ContractSymbol { get; }

        public IMethodSymbol MethodSymbol { get; }

        public string DispatchName { get; }
    }

    private readonly struct ContractMethodModel
    {
        public ContractMethodModel(
            string dispatchName,
            string methodName,
            ITypeSymbol? inputType,
            ITypeSymbol returnType)
        {
            DispatchName = dispatchName;
            MethodName = methodName;
            InputType = inputType;
            ReturnType = returnType;
        }

        public string DispatchName { get; }

        public string MethodName { get; }

        public ITypeSymbol? InputType { get; }

        public ITypeSymbol ReturnType { get; }
    }
}
