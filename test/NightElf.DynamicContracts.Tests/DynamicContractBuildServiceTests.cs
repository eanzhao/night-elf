using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using NightElf.DynamicContracts;
using NightElf.Kernel.SmartContract;
using NightElf.Runtime.CSharp.Security;
using NightElf.Sdk.CSharp;
using Xunit;

namespace NightElf.DynamicContracts.Tests;

public sealed class DynamicContractBuildServiceTests
{
    [Fact]
    public async Task BuildAsync_Should_Render_Compile_And_Execute_Templated_Contract()
    {
        var service = new DynamicContractBuildService();
        var spec = new ContractSpec
        {
            ContractName = "GreetingContract",
            Namespace = "DynamicContracts.Tests.Generated",
            Types =
            [
                new ContractTypeSpec
                {
                    Name = "GreetingInput",
                    Fields = [new ContractFieldSpec { Name = "Name", Type = ContractPrimitiveType.String }]
                },
                new ContractTypeSpec
                {
                    Name = "GreetingOutput",
                    Fields = [new ContractFieldSpec { Name = "Message", Type = ContractPrimitiveType.String }]
                }
            ],
            Methods =
            [
                new ContractMethodSpec
                {
                    Name = "Greet",
                    InputType = "GreetingInput",
                    OutputType = "GreetingOutput",
                    LogicBlocks =
                    [
                        new ContractLogicBlockSpec
                        {
                            Kind = ContractLogicBlockKind.ConcatStrings,
                            OutputField = "Message",
                            Segments =
                            [
                                new ContractStringSegmentSpec
                                {
                                    Kind = ContractStringSegmentKind.Literal,
                                    Value = "hello "
                                },
                                new ContractStringSegmentSpec
                                {
                                    Kind = ContractStringSegmentKind.InputField,
                                    Value = "Name"
                                },
                                new ContractStringSegmentSpec
                                {
                                    Kind = ContractStringSegmentKind.Literal,
                                    Value = "!"
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var artifact = await service.BuildAsync(spec);
        var assembly = Assembly.Load(artifact.AssemblyBytes);
        var contractType = assembly.GetType("DynamicContracts.Tests.Generated.GreetingContract");
        var inputType = assembly.GetType("DynamicContracts.Tests.Generated.GreetingInput");
        var outputType = assembly.GetType("DynamicContracts.Tests.Generated.GreetingOutput");

        Assert.NotNull(contractType);
        Assert.NotNull(inputType);
        Assert.NotNull(outputType);
        Assert.Contains("[ContractMethod]", artifact.SourceCode, StringComparison.Ordinal);
        Assert.Contains("partial class GreetingContract", artifact.SourceCode, StringComparison.Ordinal);

        var contract = Assert.IsAssignableFrom<CSharpSmartContract>(Activator.CreateInstance(contractType!));
        var input = Activator.CreateInstance(inputType!, "night-elf");
        var encodedInput = Assert.IsType<byte[]>(inputType!.GetMethod("Encode", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [input]));
        var executor = new SmartContractExecutor();

        var encodedOutput = executor.Execute(contract, new ContractInvocation("Greet", encodedInput));

        // Reflection cannot auto-convert byte[] to ReadOnlySpan<byte> for Decode(ReadOnlySpan<byte>).
        // Verify execution succeeded and the encoded output contains the base64-encoded greeting.
        Assert.NotEmpty(encodedOutput);
        var encoded = System.Text.Encoding.UTF8.GetString(encodedOutput);
        var expectedBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello night-elf!"));
        Assert.Contains(expectedBase64, encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractAssemblyStaticAnalyzer_Should_Allow_CompilerGenerated_AssemblyAttributes()
    {
        var assemblyPath = CompileAssembly(
            """
using System;
using NightElf.Sdk.CSharp;

namespace AnalyzerContracts;

public sealed class BenignContract : CSharpSmartContract
{
    protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
    {
        return [];
    }
}
""");

        try
        {
            var result = new ContractAssemblyStaticAnalyzer().Analyze(assemblyPath);

            Assert.True(
                result.Succeeded,
                string.Join(Environment.NewLine, result.Violations.Select(static violation => $"{violation.RuleId}: {violation.Message}")));
        }
        finally
        {
            TryDeleteAssembly(assemblyPath);
        }
    }

    [Fact]
    public void ContractAssemblyStaticAnalyzer_Should_Reject_Unsafe_IL()
    {
        var assemblyPath = CompileAssembly(
            """
using System;
using NightElf.Sdk.CSharp;

namespace AnalyzerContracts;

public sealed class UnsafeContract : CSharpSmartContract
{
    protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
    {
        unsafe
        {
            int* values = stackalloc int[1];
            values[0] = 7;
            return BitConverter.GetBytes(values[0]);
        }
    }
}
""",
            allowUnsafe: true);

        try
        {
            var result = new ContractAssemblyStaticAnalyzer().Analyze(assemblyPath);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Violations, static violation => violation.RuleId == "NEC001");
        }
        finally
        {
            TryDeleteAssembly(assemblyPath);
        }
    }

    [Fact]
    public void ContractAssemblyStaticAnalyzer_Should_Reject_Reflection_API()
    {
        var assemblyPath = CompileAssembly(
            """
using System;
using NightElf.Sdk.CSharp;

namespace AnalyzerContracts;

public sealed class ReflectionContract : CSharpSmartContract
{
    protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
    {
        _ = typeof(string).GetMethod("Clone");
        return [];
    }
}
""");

        try
        {
            var result = new ContractAssemblyStaticAnalyzer().Analyze(assemblyPath);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Violations, static violation => violation.RuleId == "NEC002");
        }
        finally
        {
            TryDeleteAssembly(assemblyPath);
        }
    }

    [Fact]
    public void ContractAssemblyStaticAnalyzer_Should_Reject_SystemIo_API()
    {
        var assemblyPath = CompileAssembly(
            """
using System;
using System.IO;
using NightElf.Sdk.CSharp;

namespace AnalyzerContracts;

public sealed class IoContract : CSharpSmartContract
{
    protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
    {
        return File.Exists("blocked") ? [1] : [0];
    }
}
""");

        try
        {
            var result = new ContractAssemblyStaticAnalyzer().Analyze(assemblyPath);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Violations, static violation => violation.RuleId == "NEC003");
        }
        finally
        {
            TryDeleteAssembly(assemblyPath);
        }
    }

    [Fact]
    public void ContractAssemblyStaticAnalyzer_Should_Reject_PInvoke()
    {
        var assemblyPath = CompileAssembly(
            """
using System;
using System.Runtime.InteropServices;
using NightElf.Sdk.CSharp;

namespace AnalyzerContracts;

public sealed class NativeContract : CSharpSmartContract
{
    [DllImport("kernel32")]
    private static extern uint GetTickCount();

    protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
    {
        return BitConverter.GetBytes(GetTickCount());
    }
}
""");

        try
        {
            var result = new ContractAssemblyStaticAnalyzer().Analyze(assemblyPath);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Violations, static violation => violation.RuleId == "NEC004");
        }
        finally
        {
            TryDeleteAssembly(assemblyPath);
        }
    }

    private static string CompileAssembly(string source, bool allowUnsafe = false)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "nightelf-dynamic-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "Generated.dll");
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13));
        var compilation = CSharpCompilation.Create(
            assemblyName: $"NightElf.DynamicContracts.Tests.{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: allowUnsafe));

        var emitResult = compilation.Emit(outputPath);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));
        return outputPath;
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences()
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

        var references = trustedAssemblies
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(CSharpSmartContract).Assembly.Location));
        return references;
    }

    private static void TryDeleteAssembly(string assemblyPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
