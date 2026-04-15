namespace NightElf.Architecture.Tests;

public sealed class AsyncHelperUsageTests
{
    private const string RunSyncPattern = "AsyncHelper" + ".RunSync(";

    [Fact]
    public void SourceCode_Should_Not_Use_AsyncHelperRunSync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoots = new[]
        {
            Path.Combine(repositoryRoot, "src"),
            Path.Combine(repositoryRoot, "test")
        };

        var offenders = sourceRoots
            .SelectMany(static root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file => File.ReadAllText(file).Contains(RunSyncPattern, StringComparison.Ordinal))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "NightElf.slnx");
            if (File.Exists(solutionPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root from the test output directory.");
    }
}
