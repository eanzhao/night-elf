using BenchmarkDotNet.Running;

using NightElf.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
    .Run(args, new NightElfBenchmarkConfig());
