using NightElf.Contracts.System.AgentSession.Protobuf;

namespace NightElf.Contracts.System.AgentSession;

public static class MeteringSourceExtensions
{
    public const int BasisPointsDenominator = 10_000;
    public const int VerifiedConfidenceWeightBasisPoints = 10_000;
    public const int SelfReportedConfidenceWeightBasisPoints = 5_000;

    public static MeteringSource EnsureSpecified(this MeteringSource meteringSource)
    {
        return meteringSource == MeteringSource.Unspecified
            ? throw new ArgumentOutOfRangeException(
                nameof(meteringSource),
                "Metering source must be specified.")
            : meteringSource;
    }

    public static int GetConfidenceWeightBasisPoints(this MeteringSource meteringSource)
    {
        return meteringSource.EnsureSpecified() switch
        {
            MeteringSource.Verified => VerifiedConfidenceWeightBasisPoints,
            MeteringSource.SelfReported => SelfReportedConfidenceWeightBasisPoints,
            _ => throw new ArgumentOutOfRangeException(nameof(meteringSource), meteringSource, null)
        };
    }

    public static long ApplyConfidenceWeight(this MeteringSource meteringSource, long tokenCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokenCount);

        return checked(tokenCount * meteringSource.GetConfidenceWeightBasisPoints() / BasisPointsDenominator);
    }
}
