using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NightElf.Kernel.SmartContract;
using NightElf.Runtime.CSharp;
using NightElf.Sdk.CSharp;

namespace NightElf.Runtime.CSharp.Tests;

public sealed class ContractSandboxTests
{
    [Fact]
    public void ContractSandbox_Should_Load_Allowed_Assemblies_In_Collectible_Context()
    {
        using var artifacts = SandboxArtifacts.Create("AllowedDependency");
        var sandbox = new ContractSandbox("allowed");
        sandbox.RegisterAllowedAssemblyPath(artifacts.DependencyAssemblyPath);

        var assembly = sandbox.LoadContractFromPath(artifacts.ContractAssemblyPath);
        var contract = CreateContractInstance(assembly);
        var result = contract.Dispatch("Ping", []);

        Assert.Equal("marker:AllowedDependency", Encoding.UTF8.GetString(result));
        Assert.Same(typeof(CSharpSmartContract).Assembly, contract.GetType().BaseType!.Assembly);
        Assert.Same(sandbox, AssemblyLoadContext.GetLoadContext(assembly));
    }

    [Fact]
    public void ContractSandbox_Should_Reject_NonWhitelisted_Assemblies()
    {
        var sandbox = new ContractSandbox("blocked");

        var exception = Assert.Throws<FileLoadException>(
            () => sandbox.LoadFromAssemblyName(new AssemblyName("BlockedDependency")));
        var innerException = Assert.IsType<ContractAssemblyNotAllowedException>(exception.InnerException);

        Assert.Equal("BlockedDependency", innerException.AssemblyName);
    }

    [Fact]
    public async Task ContractSandbox_Should_Be_Unloadable()
    {
        using var artifacts = SandboxArtifacts.Create("AllowedDependency");
        var weakReference = CreateUnloadTarget(artifacts.ContractAssemblyPath, artifacts.DependencyAssemblyPath);

        for (var attempt = 0; attempt < 10 && weakReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50);
        }

        Assert.False(weakReference.IsAlive);
    }

    [Fact]
    public async Task ContractSandboxExecutionService_Should_Throw_On_Timeout()
    {
        var service = new ContractSandboxExecutionService();

        var exception = await Assert.ThrowsAsync<ContractSandboxTimeoutException>(() =>
            service.ExecuteAsync(
                async cancellationToken =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    return 1;
                },
                timeout: TimeSpan.FromMilliseconds(50)));

        Assert.Equal(TimeSpan.FromMilliseconds(50), exception.Timeout);
    }

    [Fact]
    public async Task ContractSandboxExecutionService_Should_Execute_Contract_Within_Timeout()
    {
        var service = new ContractSandboxExecutionService();
        var executor = new SmartContractExecutor();
        var contract = new ImmediateContract();

        var result = await service.ExecuteContractAsync(
            executor,
            contract,
            new ContractInvocation("Ping", []),
            executionContext: null,
            timeout: TimeSpan.FromSeconds(1));

        Assert.Equal("ok", Encoding.UTF8.GetString(result));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnloadTarget(string contractAssemblyPath, string dependencyAssemblyPath)
    {
        var sandbox = new ContractSandbox("unload");
        sandbox.RegisterAllowedAssemblyPath(dependencyAssemblyPath);

        var assembly = sandbox.LoadContractFromPath(contractAssemblyPath);
        var contract = CreateContractInstance(assembly);
        _ = contract.Dispatch("Ping", []);

        var weakReference = new WeakReference(sandbox, trackResurrection: false);

        contract = null!;
        assembly = null!;
        sandbox.Unload();

        return weakReference;
    }

    private static CSharpSmartContract CreateContractInstance(Assembly assembly)
    {
        var contractType = assembly.GetType("SandboxContracts.SampleSandboxContract");
        Assert.NotNull(contractType);

        return Assert.IsAssignableFrom<CSharpSmartContract>(Activator.CreateInstance(contractType!));
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception switch
        {
            TypeInitializationException typeInitialization when typeInitialization.InnerException is not null =>
                Unwrap(typeInitialization.InnerException),
            TargetInvocationException targetInvocation when targetInvocation.InnerException is not null =>
                Unwrap(targetInvocation.InnerException),
            _ => exception
        };
    }

    private sealed class ImmediateContract : CSharpSmartContract
    {
        protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
        {
            return methodName switch
            {
                "Ping" => Encoding.UTF8.GetBytes("ok"),
                _ => throw new ContractMethodNotFoundException(typeof(ImmediateContract), methodName)
            };
        }
    }

    private sealed class SandboxArtifacts : IDisposable
    {
        private SandboxArtifacts(string directoryPath, string dependencyAssemblyPath, string contractAssemblyPath)
        {
            DirectoryPath = directoryPath;
            DependencyAssemblyPath = dependencyAssemblyPath;
            ContractAssemblyPath = contractAssemblyPath;
        }

        public string DirectoryPath { get; }

        public string DependencyAssemblyPath { get; }

        public string ContractAssemblyPath { get; }

        public static SandboxArtifacts Create(string dependencyAssemblyName)
        {
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                "nightelf-runtime-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);

            var dependencyAssemblyPath = Path.Combine(directoryPath, $"{dependencyAssemblyName}.dll");
            CompileAssembly(
                $$"""
                namespace {{dependencyAssemblyName}};

                public static class Helper
                {
                    public static string Message => "marker:{{dependencyAssemblyName}}";
                }
                """,
                dependencyAssemblyPath,
                references: []);

            var contractAssemblyPath = Path.Combine(directoryPath, "SampleSandboxContract.dll");
            CompileAssembly(
                $$"""
                using System;
                using System.Text;
                using NightElf.Sdk.CSharp;
                using {{dependencyAssemblyName}};

                namespace SandboxContracts;

                public sealed class SampleSandboxContract : CSharpSmartContract
                {
                    private static readonly string Marker = Helper.Message;

                    protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
                    {
                        return methodName switch
                        {
                            "Ping" => Encoding.UTF8.GetBytes(Marker),
                            _ => throw new ContractMethodNotFoundException(typeof(SampleSandboxContract), methodName)
                        };
                    }
                }
                """,
                contractAssemblyPath,
                references:
                [
                    typeof(CSharpSmartContract).Assembly.Location,
                    dependencyAssemblyPath
                ]);

            return new SandboxArtifacts(directoryPath, dependencyAssemblyPath, contractAssemblyPath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void CompileAssembly(string source, string outputPath, IEnumerable<string> references)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13));
            var compilation = CSharpCompilation.Create(
                assemblyName: Path.GetFileNameWithoutExtension(outputPath),
                syntaxTrees: [syntaxTree],
                references: GetMetadataReferences(references),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var emitResult = compilation.Emit(outputPath);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));
        }

        private static IEnumerable<MetadataReference> GetMetadataReferences(IEnumerable<string> references)
        {
            var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

            var allReferences = trustedPlatformAssemblies
                .Select(static path => MetadataReference.CreateFromFile(path))
                .ToList();

            allReferences.AddRange(references.Select(static path => MetadataReference.CreateFromFile(path)));
            return allReferences;
        }
    }
}
