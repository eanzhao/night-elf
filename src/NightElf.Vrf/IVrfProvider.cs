namespace NightElf.Vrf;

public interface IVrfProvider
{
    string Algorithm { get; }

    Task<VrfEvaluation> EvaluateAsync(
        VrfInput input,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(
        VrfVerificationContext context,
        CancellationToken cancellationToken = default);
}
