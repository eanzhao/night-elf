using BenchmarkDotNet.Attributes;

namespace NightElf.Benchmarks;

public class SyntheticTransactionBenchmarks
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

    [Benchmark(Description = "Execute one synthetic state transaction")]
    public async Task<int> ExecuteSingleTransaction()
    {
        _harness.PrepareTransaction(_harness.SingleTransaction);
        return await _harness.ExecuteAsync(_harness.SingleTransaction).ConfigureAwait(false);
    }

    [Benchmark(Description = "Execute 32 synthetic state transactions", OperationsPerInvoke = 32)]
    public async Task<int> ExecuteBatchTransactions()
    {
        var total = 0;

        foreach (var transaction in _harness.BatchTransactions)
        {
            _harness.PrepareTransaction(transaction);
            total += await _harness.ExecuteAsync(transaction).ConfigureAwait(false);
        }

        return total;
    }
}
