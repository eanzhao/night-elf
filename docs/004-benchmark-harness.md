# Phase 2 Benchmark Harness

## Scope

This document defines the repeatable benchmark entry for the Phase 2 async and cache work.

## Benchmark Project

- Project: `benchmarks/NightElf.Benchmarks`
- Runner: `BenchmarkDotNet`
- Local entry: `./eng/run-benchmarks.sh`

Covered scenarios:

- Cache hit: `TryGetState_L1Hit`, `TryGetState_L2Hit`
- Cache miss: `GetStateAsync_ColdRead`
- Single transaction latency: `ExecuteSingleTransaction`
- Batch execution throughput: `ExecuteBatchTransactions` with `OperationsPerInvoke = 32`

The harness compares two storage backends:

- `Tsavorite`
- `RedisCompat`

`RedisCompat` is the current NightElf compatibility baseline built on the new `RedisDatabase<TContext>` wrapper and an in-memory fake client. It is useful for API-overhead comparison, but it is not the historical AElf TCP/RESP latency profile. That legacy network cost is still documented in `docs/001-refactoring-overview.md` and remains out of scope until the original client stack is ported.

## Artifact Format

Each run writes timestamped output under:

```text
artifacts/benchmarks/<UTC timestamp>/
```

Expected files:

- `results/*-report-github.md`
- `results/*-report-full.json`
- `results/*-report.csv`
- BenchmarkDotNet logs and environment metadata

This timestamped layout is the archive format for future before/after comparisons.

## Commands

Full run:

```bash
./eng/run-benchmarks.sh
```

Run a subset:

```bash
./eng/run-benchmarks.sh --filter '*StorageMissBenchmarks*'
./eng/run-benchmarks.sh --filter '*SyntheticTransactionBenchmarks*'
```

## Reading The Results

- Use `ExecuteSingleTransaction` as the single-transaction latency baseline.
- Use `ExecuteBatchTransactions` as the throughput baseline.
- Because `ExecuteBatchTransactions` sets `OperationsPerInvoke = 32`, BenchmarkDotNet reports per-operation cost while still exercising a 32-transaction batch in one benchmark invocation.
- Compare `Tsavorite` and `RedisCompat` inside the same benchmark class before comparing runs across different commits.

## Initial Baseline

Baseline snapshot date: `2026-04-15`
Baseline artifacts:

- `artifacts/benchmarks/20260415T133749Z/results/NightElf.Benchmarks.SyncFastPathBenchmarks-report-github.md`
- `artifacts/benchmarks/20260415T133749Z/results/NightElf.Benchmarks.StorageMissBenchmarks-report-github.md`
- `artifacts/benchmarks/20260415T133749Z/results/NightElf.Benchmarks.SyntheticTransactionBenchmarks-report-github.md`

Reference summary on the local Apple M3 Ultra runner:

| Scenario | Backend | Mean | Notes |
|----------|---------|------|-------|
| `TryGetState_L1Hit` | Tsavorite fast path | `5.376 ns` | Pure L1 tiered cache sync hit |
| `TryGetState_L2Hit` | Tsavorite fast path | `8.534 ns` | L2 cached store sync hit |
| `GetStateAsync_ColdRead` | `RedisCompat` | `98.13 ns` | Compatibility baseline only, no historical TCP cost |
| `GetStateAsync_ColdRead` | `Tsavorite` | `402.61 ns` | Current embedded-store cold read path |
| `ExecuteSingleTransaction` | `RedisCompat` | `1.020 us` | Synthetic single-transaction latency |
| `ExecuteSingleTransaction` | `Tsavorite` | `2.588 us` | Synthetic single-transaction latency |
| `ExecuteBatchTransactions` | `RedisCompat` | `1.248 us/op` | Approx. `~801k tx/s` |
| `ExecuteBatchTransactions` | `Tsavorite` | `2.995 us/op` | Approx. `~334k tx/s` |

Interpretation:

- The sync fast path is nanosecond-scale once reads stay inside L1/L2 cache.
- The current `RedisCompat` baseline is faster than `Tsavorite` because it is an in-memory compatibility shim, not the original AElf TCP client.
- Use this snapshot as the reproducible Phase 2 starting point, then compare future changes against the same benchmark names and artifact layout.

When collecting follow-up measurements, keep the generated markdown/json artifacts from `artifacts/benchmarks/<timestamp>/results/` and summarize the deltas in the matching issue or PR description.
