using BenchmarkDotNet.Attributes;

namespace NightElf.Benchmarks;

public class SyncFastPathBenchmarks
{
    private BenchmarkStateHarness _harness = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _harness = await BenchmarkStateHarness.CreateAsync(BenchmarkStorageProvider.Tsavorite).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _harness.Dispose();
    }

    [Benchmark(Baseline = true, Description = "TryGetState L1 tiered cache hit")]
    public bool TryGetState_L1Hit()
    {
        return _harness.Reader.TryGetState(_harness.TieredHitKey, out _);
    }

    [Benchmark(Description = "TryGetState L2 cached store hit")]
    public bool TryGetState_L2Hit()
    {
        return _harness.Reader.TryGetState(_harness.CachedHitKey, out _);
    }
}
