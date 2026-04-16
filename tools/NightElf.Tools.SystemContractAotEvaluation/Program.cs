using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

using NightElf.Contracts.System.TokenAotPrototype;
using NightElf.Kernel.SmartContract;
using NightElf.Runtime.CSharp;
using NightElf.Sdk.CSharp;

namespace NightElf.Tools.SystemContractAotEvaluation;

internal static class Program
{
    private static readonly long StartupTimestamp = AppBootstrap.StartTimestamp;

    public static int Main(string[] args)
    {
        var options = EvaluationOptions.Parse(args);
        var runtimeReport = CreateRuntimeReport();
        var directReport = EvaluateDirectExecution(options.WarmIterations);
        var sandboxReport = EvaluateSandboxExecution(options.ContractAssemblyPath, options.WarmIterations);
        var report = new EvaluationReport(
            EvaluatedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
            Runtime: runtimeReport,
            DirectExecution: directReport,
            Sandbox: sandboxReport,
            Conclusion: CreateConclusion(directReport, sandboxReport));

        var json = JsonSerializer.Serialize(report, EvaluationReportJsonContext.Default.EvaluationReport);
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            var directory = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(options.OutputPath, json);
        }

        Console.WriteLine(json);
        return directReport.Succeeded ? 0 : 1;
    }

    private static RuntimeReport CreateRuntimeReport()
    {
        return new RuntimeReport(
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            NativeAotRuntime: !RuntimeFeature.IsDynamicCodeSupported,
            DynamicCodeSupported: RuntimeFeature.IsDynamicCodeSupported,
            DynamicCodeCompiled: RuntimeFeature.IsDynamicCodeCompiled,
            StartupToMainMilliseconds: Stopwatch.GetElapsedTime(StartupTimestamp).TotalMilliseconds,
            ProcessPath: Environment.ProcessPath ?? string.Empty);
    }

    private static ExecutionReport EvaluateDirectExecution(int warmIterations)
    {
        try
        {
            var executor = new SmartContractExecutor();
            var contract = new TokenAotPrototypeContract();
            var mintInvocation = new ContractInvocation("Mint", MintInput.Encode(new MintInput("alice", 42)));
            var balanceInvocation = new ContractInvocation("GetBalance", BalanceInput.Encode(new BalanceInput("alice")));

            var firstDispatch = Measure(() => executor.Execute(contract, mintInvocation));
            var balance = BalanceOutput.Decode(executor.Execute(contract, balanceInvocation)).Amount;
            var resources = contract.DescribeResources(balanceInvocation);
            var warmLoop = Measure(() =>
            {
                for (var index = 0; index < warmIterations; index++)
                {
                    _ = executor.Execute(contract, balanceInvocation);
                }
            });

            return new ExecutionReport(
                Succeeded: true,
                Mode: "direct-reference",
                FirstDispatchMilliseconds: firstDispatch.TotalMilliseconds,
                WarmLoopMilliseconds: warmLoop.TotalMilliseconds,
                WarmAverageNanoseconds: warmLoop.TotalMilliseconds * 1_000_000d / warmIterations,
                FinalBalance: balance,
                SupportsResourceExtraction: contract.SupportsResourceExtraction,
                ReadKeys: resources.ReadKeys,
                WriteKeys: resources.WriteKeys,
                Failure: null);
        }
        catch (Exception exception)
        {
            return new ExecutionReport(
                Succeeded: false,
                Mode: "direct-reference",
                FirstDispatchMilliseconds: 0,
                WarmLoopMilliseconds: 0,
                WarmAverageNanoseconds: 0,
                FinalBalance: 0,
                SupportsResourceExtraction: false,
                ReadKeys: [],
                WriteKeys: [],
                Failure: $"{exception.GetType().FullName}: {exception.Message}");
        }
    }

    private static SandboxReport EvaluateSandboxExecution(string? contractAssemblyPath, int warmIterations)
    {
        if (string.IsNullOrWhiteSpace(contractAssemblyPath))
        {
            return new SandboxReport(
                Attempted: false,
                Succeeded: false,
                ContractAssemblyPath: null,
                FirstDispatchMilliseconds: null,
                WarmLoopMilliseconds: null,
                WarmAverageNanoseconds: null,
                FinalBalance: null,
                ReadKeys: [],
                WriteKeys: [],
                ExceptionType: null,
                Failure: null,
                Summary: "Skipped because no managed contract assembly path was supplied.");
        }

        var fullPath = Path.GetFullPath(contractAssemblyPath);
        if (!File.Exists(fullPath))
        {
            return new SandboxReport(
                Attempted: true,
                Succeeded: false,
                ContractAssemblyPath: fullPath,
                FirstDispatchMilliseconds: null,
                WarmLoopMilliseconds: null,
                WarmAverageNanoseconds: null,
                FinalBalance: null,
                ReadKeys: [],
                WriteKeys: [],
                ExceptionType: typeof(FileNotFoundException).FullName,
                Failure: $"Managed contract assembly not found at '{fullPath}'.",
                Summary: "Sandbox evaluation could not start because the managed contract assembly is missing.");
        }

        try
        {
            var executor = new SmartContractExecutor();
            var sandbox = new ContractSandbox("token-aot-prototype");
            var assembly = sandbox.LoadContractFromPath(fullPath);
            try
            {
                var contract = CreateSandboxContract(assembly);
                var mintInvocation = new ContractInvocation("Mint", MintInput.Encode(new MintInput("alice", 42)));
                var balanceInvocation = new ContractInvocation("GetBalance", BalanceInput.Encode(new BalanceInput("alice")));

                var firstDispatch = Measure(() => executor.Execute(contract, mintInvocation));
                var balance = BalanceOutput.Decode(executor.Execute(contract, balanceInvocation)).Amount;
                var resources = contract.DescribeResources(balanceInvocation);
                var warmLoop = Measure(() =>
                {
                    for (var index = 0; index < warmIterations; index++)
                    {
                        _ = executor.Execute(contract, balanceInvocation);
                    }
                });

                return new SandboxReport(
                    Attempted: true,
                    Succeeded: true,
                    ContractAssemblyPath: fullPath,
                    FirstDispatchMilliseconds: firstDispatch.TotalMilliseconds,
                    WarmLoopMilliseconds: warmLoop.TotalMilliseconds,
                    WarmAverageNanoseconds: warmLoop.TotalMilliseconds * 1_000_000d / warmIterations,
                    FinalBalance: balance,
                    ReadKeys: resources.ReadKeys.ToArray(),
                    WriteKeys: resources.WriteKeys.ToArray(),
                    ExceptionType: null,
                    Failure: null,
                    Summary: "Managed IL assembly loaded successfully through the current collectible AssemblyLoadContext sandbox.");
            }
            finally
            {
                sandbox.Unload();
            }
        }
        catch (Exception exception)
        {
            return new SandboxReport(
                Attempted: true,
                Succeeded: false,
                ContractAssemblyPath: fullPath,
                FirstDispatchMilliseconds: null,
                WarmLoopMilliseconds: null,
                WarmAverageNanoseconds: null,
                FinalBalance: null,
                ReadKeys: [],
                WriteKeys: [],
                ExceptionType: exception.GetType().FullName,
                Failure: exception.Message,
                Summary: "Current sandbox loading remains tied to managed assemblies and should be treated as incompatible with the NativeAOT system-contract deployment path.");
        }
    }

    private static ConclusionReport CreateConclusion(ExecutionReport directReport, SandboxReport sandboxReport)
    {
        var builtInAotPrototypeSucceeded = directReport.Succeeded;
        var sandboxLoadSucceeded = sandboxReport.Succeeded;
        var recommendation = builtInAotPrototypeSucceeded && !sandboxLoadSucceeded
            ? "Keep NativeAOT for built-in system contracts on the backlog until NightElf grows an explicit built-in registration path separate from the collectible AssemblyLoadContext sandbox."
            : builtInAotPrototypeSucceeded && sandboxLoadSucceeded
                ? "A NativeAOT host is viable today, but the generic sandbox still needs a separate artifact and deployment model before this should move onto the main roadmap."
                : "Do not promote NativeAOT system contracts onto the main roadmap yet; keep the work as a bounded exploration item.";

        var summary = builtInAotPrototypeSucceeded
            ? "A compile-time linked system contract can execute through the current dispatch stack under NativeAOT. The open question is deployment and loading, not contract method dispatch itself."
            : "The prototype did not complete successfully, so NativeAOT should remain a documentation-only feasibility item.";

        return new ConclusionReport(
            BuiltInAotPrototypeSucceeded: builtInAotPrototypeSucceeded,
            SandboxLoadSucceeded: sandboxLoadSucceeded,
            Recommendation: recommendation,
            Summary: summary);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Sandbox inspection intentionally loads an external managed contract assembly; this path is explicitly outside the built-in NativeAOT activation model under evaluation.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL2072",
        Justification = "The sandbox probe only activates an external managed contract type with a public parameterless constructor.")]
    private static CSharpSmartContract CreateSandboxContract(Assembly assembly)
    {
        var contractType = assembly
            .GetTypes()
            .Single(static type =>
                typeof(CSharpSmartContract).IsAssignableFrom(type) &&
                !type.IsAbstract &&
                type.IsClass);

        return (CSharpSmartContract)Activator.CreateInstance(contractType)!;
    }

    private static TimeSpan Measure(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }
}

internal static class AppBootstrap
{
    public static long StartTimestamp { get; private set; }

    [ModuleInitializer]
    public static void Initialize()
    {
        StartTimestamp = Stopwatch.GetTimestamp();
    }
}
