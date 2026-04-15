using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NightElf.Kernel.Parallel;
using NightElf.Kernel.SmartContract;
using NightElf.Sdk.CSharp;
using NightElf.Sdk.SourceGen;

namespace NightElf.Sdk.SourceGen.Tests;

public sealed class ContractDispatcherGeneratorTests
{
    [Fact]
    public void Generator_Should_Dispatch_Annotated_Methods_Through_Executor()
    {
        var compilationResult = CompileSampleContract();
        var contract = compilationResult.CreateContract();
        var executor = new SmartContractExecutor();

        var result = executor.Execute(contract, new ContractInvocation("Echo", Encoding.UTF8.GetBytes("hello")));

        Assert.Equal("hello|echo", Encoding.UTF8.GetString(result));
        Assert.Contains("methodName switch", compilationResult.GeneratedSource);
        Assert.Contains("DescribeResourcesCore", compilationResult.GeneratedSource);
        Assert.DoesNotContain("System.Reflection", compilationResult.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_Should_Throw_When_Method_Is_Missing()
    {
        var contract = CompileSampleContract().CreateContract();
        var executor = new SmartContractExecutor();

        var exception = Assert.Throws<ContractMethodNotFoundException>(
            () => executor.Execute(contract, new ContractInvocation("Missing", [])));

        Assert.Equal("Missing", exception.MethodName);
        Assert.Contains("SampleContract", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_Should_Wrap_Input_Decode_Failures()
    {
        var contract = CompileSampleContract().CreateContract();
        var executor = new SmartContractExecutor();

        var exception = Assert.Throws<ContractInputDecodeException>(
            () => executor.Execute(contract, new ContractInvocation("Echo", Encoding.UTF8.GetBytes("invalid"))));

        Assert.Equal("Echo", exception.MethodName);
        Assert.Equal("SampleContracts.EchoInput", exception.InputType.FullName);
        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Fact]
    public void Generator_Should_Extract_Declared_Resources_Through_Service()
    {
        var contract = CompileSampleContract().CreateContract();
        var service = new ResourceExtractionService();

        var result = service.Extract(contract, new ContractInvocation("Echo", Encoding.UTF8.GetBytes("hello")));

        Assert.False(result.UsedFallback);
        Assert.Equal(["read:hello", "shared"], result.Resources.ReadKeys);
        Assert.Equal(["write:hello"], result.Resources.WriteKeys);
    }

    [Fact]
    public void Generator_Should_Throw_For_Unknown_Method_During_Resource_Extraction()
    {
        var contract = CompileSampleContract().CreateContract();
        var service = new ResourceExtractionService();

        var exception = Assert.Throws<ContractMethodNotFoundException>(
            () => service.Extract(contract, new ContractInvocation("Missing", [])));

        Assert.Equal("Missing", exception.MethodName);
    }

    [Fact]
    public void Generator_Should_Wrap_Input_Decode_Failures_During_Resource_Extraction()
    {
        var contract = CompileSampleContract().CreateContract();
        var service = new ResourceExtractionService();

        var exception = Assert.Throws<ContractInputDecodeException>(
            () => service.Extract(contract, new ContractInvocation("Echo", Encoding.UTF8.GetBytes("invalid"))));

        Assert.Equal("Echo", exception.MethodName);
        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Fact]
    public void ResourceExtractionService_Should_Use_Fallback_For_Contracts_Without_Generated_Metadata()
    {
        var contract = new LegacyContract();
        var service = new ResourceExtractionService();

        var result = service.Extract(contract, new ContractInvocation("Legacy", []));

        Assert.True(result.UsedFallback);
        Assert.Empty(result.Resources.ReadKeys);
        Assert.Empty(result.Resources.WriteKeys);
    }

    [Fact]
    public void Generator_Should_Report_Diagnostic_For_NonPartial_Contract()
    {
        const string source = """
using NightElf.Sdk.CSharp;

namespace SampleContracts;

public sealed class InvalidContract : CSharpSmartContract
{
    [ContractMethod]
    public Empty Ping()
    {
        return Empty.Value;
    }
}
""";

        var (diagnostics, _, _) = CompileSource(source);

        var diagnostic = Assert.Single(diagnostics.Where(static item => item.Id == "NE1001"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void Generator_Should_Report_Diagnostic_For_Invalid_Resource_Extractor()
    {
        const string source = """
using NightElf.Sdk.CSharp;

namespace SampleContracts;

public sealed partial class InvalidResourceContract : CSharpSmartContract
{
    [ContractMethod(ReadExtractor = nameof(GetReadKeys))]
    public Empty Ping()
    {
        return Empty.Value;
    }

    private string GetReadKeys()
    {
        return "not-an-enumerable";
    }
}
""";

        var (diagnostics, _, _) = CompileSource(source);

        var diagnostic = Assert.Single(diagnostics.Where(static item => item.Id == "NE1007"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    private static CompilationArtifact CompileSampleContract()
    {
        const string source = """
using System;
using System.Collections.Generic;
using System.Text;
using NightElf.Sdk.CSharp;

namespace SampleContracts;

public sealed partial class SampleContract : CSharpSmartContract
{
    [ContractMethod(ReadExtractor = nameof(GetEchoReadKeys), WriteExtractor = nameof(GetEchoWriteKeys))]
    public EchoOutput Echo(EchoInput input)
    {
        return new EchoOutput(input.Value + "|echo");
    }

    [ContractMethod(WriteExtractor = nameof(GetPingWriteKeys))]
    public Empty Ping()
    {
        return Empty.Value;
    }

    private static IEnumerable<string> GetEchoReadKeys(EchoInput input)
    {
        return new[] { "read:" + input.Value, "shared", "shared" };
    }

    private string[] GetEchoWriteKeys(EchoInput input)
    {
        return new[] { "write:" + input.Value };
    }

    private static IEnumerable<string> GetPingWriteKeys()
    {
        return new[] { "ping" };
    }
}

public readonly record struct EchoInput(string Value) : IContractCodec<EchoInput>
{
    public static EchoInput Decode(ReadOnlySpan<byte> input)
    {
        var text = Encoding.UTF8.GetString(input);
        if (text == "invalid")
        {
            throw new FormatException("invalid echo payload");
        }

        return new EchoInput(text);
    }

    public static byte[] Encode(EchoInput value)
    {
        return Encoding.UTF8.GetBytes(value.Value);
    }
}

public readonly record struct EchoOutput(string Value) : IContractCodec<EchoOutput>
{
    public static EchoOutput Decode(ReadOnlySpan<byte> input)
    {
        return new EchoOutput(Encoding.UTF8.GetString(input));
    }

    public static byte[] Encode(EchoOutput value)
    {
        return Encoding.UTF8.GetBytes(value.Value);
    }
}
""";

        var (diagnostics, generatedSource, assembly) = CompileSource(source);

        Assert.Empty(diagnostics.Where(static item => item.Severity == DiagnosticSeverity.Error));

        return new CompilationArtifact(generatedSource, assembly);
    }

    private static (ImmutableArray<Diagnostic> Diagnostics, string GeneratedSource, Assembly Assembly) CompileSource(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.CSharp13));

        var compilation = CSharpCompilation.Create(
            assemblyName: $"NightElf.GeneratedContracts.{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new ContractDispatcherGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        var diagnostics = generatorDiagnostics.AddRange(outputCompilation.GetDiagnostics());
        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error))
        {
            return (diagnostics, GetGeneratedSource(driver), typeof(ContractDispatcherGeneratorTests).Assembly);
        }

        using var stream = new MemoryStream();
        var emitResult = outputCompilation.Emit(stream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

        stream.Position = 0;
        var assembly = Assembly.Load(stream.ToArray());
        return (diagnostics, GetGeneratedSource(driver), assembly);
    }

    private static MetadataReference[] GetMetadataReferences()
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

        var references = trustedAssemblies
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(CSharpSmartContract).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(ResourceExtractionService).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SmartContractExecutor).Assembly.Location));

        return references.ToArray();
    }

    private static string GetGeneratedSource(GeneratorDriver driver)
    {
        return string.Join(
            Environment.NewLine,
            driver.GetRunResult()
                .Results
                .SelectMany(static result => result.GeneratedSources)
                .Select(static source => source.SourceText.ToString()));
    }

    private sealed record CompilationArtifact(string GeneratedSource, Assembly Assembly)
    {
        public CSharpSmartContract CreateContract()
        {
            var contractType = Assembly.GetType("SampleContracts.SampleContract");
            Assert.NotNull(contractType);

            return Assert.IsAssignableFrom<CSharpSmartContract>(Activator.CreateInstance(contractType!));
        }
    }

    private sealed class LegacyContract : CSharpSmartContract
    {
        protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
        {
            return [];
        }
    }
}
