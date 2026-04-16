using System.Security.Cryptography;
using System.Text;

namespace NightElf.Vrf;

public sealed class DeterministicVrfProvider : IVrfProvider
{
    private static readonly Encoding TextEncoding = Encoding.UTF8;

    private readonly VrfProviderOptions _options;

    public DeterministicVrfProvider(VrfProviderOptions? options = null)
    {
        _options = options ?? new VrfProviderOptions();
        _options.Validate();
    }

    public string Algorithm => nameof(VrfProviderKind.Deterministic);

    public Task<VrfEvaluation> EvaluateAsync(
        VrfInput input,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();

        var proof = Hash(
            $"{_options.DomainPrefix}|{input.Domain}|{input.PublicKey}|{Convert.ToHexString(input.Seed)}");
        var randomness = SHA256.HashData(proof);

        return Task.FromResult(new VrfEvaluation
        {
            Proof = proof,
            Randomness = randomness
        });
    }

    public async Task<bool> VerifyAsync(
        VrfVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Input);
        ArgumentNullException.ThrowIfNull(context.Proof);
        ArgumentNullException.ThrowIfNull(context.Randomness);

        var expected = await EvaluateAsync(context.Input, cancellationToken).ConfigureAwait(false);

        return CryptographicOperations.FixedTimeEquals(expected.Proof, context.Proof) &&
               CryptographicOperations.FixedTimeEquals(expected.Randomness, context.Randomness);
    }

    private static byte[] Hash(string value)
    {
        return SHA256.HashData(TextEncoding.GetBytes(value));
    }
}
