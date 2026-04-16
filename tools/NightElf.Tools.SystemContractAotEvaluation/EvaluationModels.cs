using System.Text.Json.Serialization;

namespace NightElf.Tools.SystemContractAotEvaluation;

public sealed record EvaluationOptions(string? ContractAssemblyPath, string? OutputPath, int WarmIterations)
{
    public static EvaluationOptions Parse(string[] args)
    {
        string? contractAssemblyPath = null;
        string? outputPath = null;
        var warmIterations = 10_000;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--contract-assembly":
                    contractAssemblyPath = ReadValue(args, ref index, "--contract-assembly");
                    break;
                case "--output":
                    outputPath = ReadValue(args, ref index, "--output");
                    break;
                case "--warm-iterations":
                    if (!int.TryParse(ReadValue(args, ref index, "--warm-iterations"), out warmIterations) || warmIterations <= 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(args), "Warm iterations must be a positive integer.");
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        return new EvaluationOptions(contractAssemblyPath, outputPath, warmIterations);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string argumentName)
    {
        if (index == args.Count - 1)
        {
            throw new ArgumentException($"Missing value for '{argumentName}'.");
        }

        index++;
        return args[index];
    }
}

public sealed record EvaluationReport(
    string EvaluatedAtUtc,
    RuntimeReport Runtime,
    ExecutionReport DirectExecution,
    SandboxReport Sandbox,
    ConclusionReport Conclusion);

public sealed record RuntimeReport(
    string FrameworkDescription,
    string RuntimeIdentifier,
    string ProcessArchitecture,
    bool NativeAotRuntime,
    bool DynamicCodeSupported,
    bool DynamicCodeCompiled,
    double StartupToMainMilliseconds,
    string ProcessPath);

public sealed record ExecutionReport(
    bool Succeeded,
    string Mode,
    double FirstDispatchMilliseconds,
    double WarmLoopMilliseconds,
    double WarmAverageNanoseconds,
    long FinalBalance,
    bool SupportsResourceExtraction,
    IReadOnlyList<string> ReadKeys,
    IReadOnlyList<string> WriteKeys,
    string? Failure);

public sealed record SandboxReport(
    bool Attempted,
    bool Succeeded,
    string? ContractAssemblyPath,
    double? FirstDispatchMilliseconds,
    double? WarmLoopMilliseconds,
    double? WarmAverageNanoseconds,
    long? FinalBalance,
    string[] ReadKeys,
    string[] WriteKeys,
    string? ExceptionType,
    string? Failure,
    string Summary);

public sealed record ConclusionReport(
    bool BuiltInAotPrototypeSucceeded,
    bool SandboxLoadSucceeded,
    string Recommendation,
    string Summary);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EvaluationReport))]
internal sealed partial class EvaluationReportJsonContext : JsonSerializerContext;
