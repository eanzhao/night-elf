using System.Diagnostics;
using System.Security;
using System.Text;

namespace NightElf.DynamicContracts;

public sealed class DynamicContractBuildService
{
    private const string TargetFramework = "net10.0";

    private readonly DynamicContractSourceRenderer _sourceRenderer = new();

    public async Task<DynamicContractBuildArtifact> BuildAsync(
        ContractSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        ContractSpecValidator.Validate(spec);

        var canonicalSpecBytes = ContractSpecSerializer.SerializeCanonicalBytes(spec);
        var contractNamespace = DynamicContractDefaults.NormalizeNamespace(spec.Namespace);
        var sourceCode = _sourceRenderer.Render(spec);
        var repositoryRoot = ResolveRepositoryRoot();
        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "nightelf-dynamic-contracts",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workDirectory);

        try
        {
            var projectPath = Path.Combine(workDirectory, "DynamicContract.csproj");
            var sourcePath = Path.Combine(workDirectory, "GeneratedContract.g.cs");

            await File.WriteAllTextAsync(sourcePath, sourceCode, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    projectPath,
                    RenderProjectFile(repositoryRoot, workDirectory, spec.ContractName, contractNamespace),
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);

            var buildOutput = await RunDotnetBuildAsync(projectPath, cancellationToken).ConfigureAwait(false);
            var assemblyPath = Path.Combine(workDirectory, "bin", "Release", TargetFramework, $"{spec.ContractName}.dll");
            if (!File.Exists(assemblyPath))
            {
                throw new DynamicContractBuildException(
                    $"Dynamic contract build did not produce '{assemblyPath}'.",
                    buildOutput);
            }

            return new DynamicContractBuildArtifact
            {
                ContractName = spec.ContractName,
                ContractNamespace = contractNamespace,
                EntryContractType = $"{contractNamespace}.{spec.ContractName}",
                SourceCode = sourceCode,
                AssemblyBytes = await File.ReadAllBytesAsync(assemblyPath, cancellationToken).ConfigureAwait(false),
                CanonicalSpecBytes = canonicalSpecBytes
            };
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private static string RenderProjectFile(
        string repositoryRoot,
        string workDirectory,
        string assemblyName,
        string contractNamespace)
    {
        var escapedAssemblyName = XmlEscape(assemblyName);
        var escapedRootNamespace = XmlEscape(contractNamespace);
        var escapedPathMap = XmlEscape($"{workDirectory}=/nightelf-dynamic");
        var sdkReferences = string.Join(
            Environment.NewLine,
            ResolveSdkReferencePaths(repositoryRoot)
                .Select(referencePath =>
                {
                    var includeName = XmlEscape(Path.GetFileNameWithoutExtension(referencePath));
                    var hintPath = XmlEscape(referencePath);
                    return $$"""
    <Reference Include="{{includeName}}">
      <HintPath>{{hintPath}}</HintPath>
      <Private>true</Private>
    </Reference>
""";
                }));

        return $$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{{TargetFramework}}</TargetFramework>
    <LangVersion>13</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>{{escapedAssemblyName}}</AssemblyName>
    <RootNamespace>{{escapedRootNamespace}}</RootNamespace>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
    <PathMap>{{escapedPathMap}}</PathMap>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="GeneratedContract.g.cs" />
{{sdkReferences}}
  </ItemGroup>
</Project>
""";
    }

    private static async Task<string> RunDotnetBuildAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectPath}\" -c Release --nologo -v quiet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath)
        };

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Start();

        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            var processToKill = (Process)state!;
            if (!processToKill.HasExited)
            {
                try
                {
                    processToKill.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
        }, process);

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        var buildOutput = string.Join(
            Environment.NewLine,
            new[] { standardOutput, standardError }.Where(static item => !string.IsNullOrWhiteSpace(item)));

        if (process.ExitCode != 0)
        {
            throw new DynamicContractBuildException(
                $"dotnet build failed for dynamic contract project '{projectPath}'.",
                buildOutput);
        }

        return buildOutput;
    }

    private static string ResolveRepositoryRoot()
    {
        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(DynamicContractBuildService).Assembly.Location) ?? string.Empty
        };

        foreach (var searchRoot in searchRoots.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(searchRoot)); current is not null; current = current.Parent)
            {
                var solutionPath = Path.Combine(current.FullName, "NightElf.slnx");
                var sdkProjectPath = Path.Combine(current.FullName, "src", "NightElf.Sdk.CSharp", "NightElf.Sdk.CSharp.csproj");

                if (File.Exists(solutionPath) &&
                    File.Exists(sdkProjectPath))
                {
                    return current.FullName;
                }
            }
        }

        throw new InvalidOperationException("NightElf repository root could not be resolved for dynamic contract compilation.");
    }

    private static IReadOnlyList<string> ResolveSdkReferencePaths(string repositoryRoot)
    {
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var sdkOutputDirectory = Path.Combine(repositoryRoot, "src", "NightElf.Sdk.CSharp", "bin", configuration, TargetFramework);
            if (!Directory.Exists(sdkOutputDirectory))
            {
                continue;
            }

            var referencePaths = Directory.GetFiles(sdkOutputDirectory, "NightElf*.dll")
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            if (referencePaths.Length > 0)
            {
                return referencePaths;
            }
        }

        throw new InvalidOperationException(
            "NightElf.Sdk.CSharp build outputs could not be resolved for dynamic contract compilation. Build the repository first.");
    }

    private static string XmlEscape(string value)
    {
        return SecurityElement.Escape(value) ?? value;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
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
