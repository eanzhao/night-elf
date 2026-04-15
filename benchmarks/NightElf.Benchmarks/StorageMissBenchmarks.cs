using BenchmarkDotNet.Attributes;

namespace NightElf.Benchmarks;

public class StorageMissBenchmarks
{
    private BenchmarkStateHarness _harness = null!;

    [Params(BenchmarkStorageProvider.Tsavorite, BenchmarkStorageProvider.RedisCompat)]
    public BenchmarkStorageProvider Provider { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _harness = await BenchmarkStateHarness.CreateAsync(Provider).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _harness.Dispose();
    }

    [Benchmark(Description = "GetStateAsync cache miss -> backing store")]
    public async Task<byte[]?> GetStateAsync_ColdRead()
    {
        _harness.PrepareColdRead();
        return await _harness.Reader.GetStateAsync(_harness.ColdReadKey).ConfigureAwait(false);
    }
}
