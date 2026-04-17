namespace NightElf.DynamicContracts;

public sealed class DynamicContractBuildException : InvalidOperationException
{
    public DynamicContractBuildException(string message, string buildOutput)
        : base(message)
    {
        BuildOutput = buildOutput;
    }

    public string BuildOutput { get; }
}
