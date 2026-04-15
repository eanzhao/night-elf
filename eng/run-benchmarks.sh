#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
artifacts_dir="${BENCHMARK_ARTIFACTS_DIR:-$repo_root/artifacts/benchmarks/$timestamp}"

if [ "$#" -eq 0 ]; then
  benchmark_args=(--filter '*' --artifacts "$artifacts_dir")
else
  benchmark_args=(--artifacts "$artifacts_dir" "$@")
fi

dotnet run --project benchmarks/NightElf.Benchmarks/NightElf.Benchmarks.csproj -c Release -- "${benchmark_args[@]}"

echo "Benchmark artifacts: $artifacts_dir"
