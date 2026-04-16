using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NightElf.Vrf.Tests;

public sealed class DeterministicVrfProviderTests
{
    [Fact]
    public async Task EvaluateAndVerify_Should_Be_Deterministic_For_The_Same_Input()
    {
        var provider = new DeterministicVrfProvider(new VrfProviderOptions
        {
            DomainPrefix = "nightelf.test"
        });
        var input = new VrfInput
        {
            PublicKey = "validator-a",
            Domain = "aedpos:10:2:1",
            Seed = [1, 2, 3, 4]
        };

        var first = await provider.EvaluateAsync(input);
        var second = await provider.EvaluateAsync(input);
        var verified = await provider.VerifyAsync(new VrfVerificationContext
        {
            Input = input,
            Proof = first.Proof,
            Randomness = first.Randomness
        });

        Assert.Equal(nameof(VrfProviderKind.Deterministic), provider.Algorithm);
        Assert.Equal(first.Proof, second.Proof);
        Assert.Equal(first.Randomness, second.Randomness);
        Assert.True(verified);
    }

    [Fact]
    public async Task Verify_Should_Fail_For_Mismatched_Proof()
    {
        var provider = new DeterministicVrfProvider();
        var input = new VrfInput
        {
            PublicKey = "validator-b",
            Domain = "aedpos:11:2:2",
            Seed = [9, 8, 7]
        };

        var evaluation = await provider.EvaluateAsync(input);
        var verified = await provider.VerifyAsync(new VrfVerificationContext
        {
            Input = input,
            Proof = [.. evaluation.Proof, 1],
            Randomness = evaluation.Randomness
        });

        Assert.False(verified);
    }

    [Fact]
    public async Task Verify_Should_Fail_For_Tampered_Proof_With_Same_Length()
    {
        var provider = new DeterministicVrfProvider();
        var input = new VrfInput
        {
            PublicKey = "validator-c",
            Domain = "aedpos:12:1:3",
            Seed = [5, 6, 7]
        };

        var evaluation = await provider.EvaluateAsync(input);

        // Flip one byte in the proof (same length, different content)
        var tamperedProof = evaluation.Proof.ToArray();
        tamperedProof[0] ^= 0xFF;

        var verified = await provider.VerifyAsync(new VrfVerificationContext
        {
            Input = input,
            Proof = tamperedProof,
            Randomness = evaluation.Randomness
        });

        Assert.False(verified);
    }

    [Fact]
    public async Task Verify_Should_Fail_For_Tampered_Randomness()
    {
        var provider = new DeterministicVrfProvider();
        var input = new VrfInput
        {
            PublicKey = "validator-d",
            Domain = "aedpos:14:2:1",
            Seed = [10, 20, 30]
        };

        var evaluation = await provider.EvaluateAsync(input);

        var tamperedRandomness = evaluation.Randomness.ToArray();
        tamperedRandomness[^1] ^= 0x01;

        var verified = await provider.VerifyAsync(new VrfVerificationContext
        {
            Input = input,
            Proof = evaluation.Proof,
            Randomness = tamperedRandomness
        });

        Assert.False(verified);
    }

    [Fact]
    public void AddVrfProvider_Should_Register_Configured_Deterministic_Provider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NightElf:Vrf:Provider"] = "Deterministic",
                ["NightElf:Vrf:DomainPrefix"] = "nightelf.integration"
            })
            .Build();

        services.AddVrfProvider(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<VrfProviderOptions>();
        var provider = serviceProvider.GetRequiredService<IVrfProvider>();

        Assert.Equal(VrfProviderKind.Deterministic, options.ResolveProviderKind());
        Assert.Equal("nightelf.integration", options.DomainPrefix);
        Assert.IsType<DeterministicVrfProvider>(provider);
    }

    [Fact]
    public void AddVrfProvider_Should_Fail_For_Unsupported_Provider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NightElf:Vrf:Provider"] = "Bls"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddVrfProvider(configuration));

        Assert.Contains("Unsupported VRF provider 'Bls'", exception.Message, StringComparison.Ordinal);
    }
}
