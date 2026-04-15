# Phase 1 Test Baseline

## Scope

This document defines the Phase 1 validation gate for the storage-layer migration, the current NightElf smoke baseline, and the migration queue against the upstream AElf test estate.

Snapshot date: 2026-04-15  
Upstream source: `AElfProject/AElf` `dev` branch  
Snapshot command:

```bash
gh api 'repos/AElfProject/AElf/git/trees/dev?recursive=1' \
  --jq '.tree[] | select(.path|startswith("test/") and endswith(".csproj")) | .path'
```

The design doc still references "108 test projects". The current upstream `dev` branch exposes `103` `test/*.csproj` files plus shared assets `test/AllContracts.props` and `test/AllProto.props`. NightElf should treat `108` as the historical compatibility target and `103` as the current concrete migration inventory as of 2026-04-15.

## Inventory Summary

| Domain | Upstream project count | Representative upstream projects | NightElf target |
|--------|------------------------|----------------------------------|-----------------|
| Contract and system-contract suites | 46 | `AElf.Contracts.MultiToken.Tests`, `AElf.Contracts.Consensus.AEDPoS.Tests`, `AElf.Contracts.TokenConverter.Tests` | `contract/`, `src/NightElf.Kernel.SmartContract`, `src/NightElf.Runtime.CSharp`, `src/NightElf.Sdk.CSharp` |
| Kernel, execution and consensus | 28 | `AElf.Kernel.Core.Tests`, `AElf.Kernel.SmartContract.Tests`, `AElf.Kernel.SmartContractExecution.Tests`, `AElf.Kernel.Consensus.AEDPoS.Tests` | `src/NightElf.Kernel.Core`, `src/NightElf.Kernel.SmartContract`, `src/NightElf.Kernel.Consensus`, `src/NightElf.Kernel.Parallel` |
| OS and networking | 4 | `AElf.OS.Core.Tests`, `AElf.OS.Network.Grpc.Tests`, `AElf.OS.Tests` | `src/NightElf.OS.Network` |
| Runtime, SDK and code tooling | 10 | `AElf.CSharp.CodeOps.Tests`, `AElf.Runtime.CSharp.Tests`, `AElf.Sdk.CSharp.Tests`, `AElf.Parallel.Tests` | `src/NightElf.Runtime.CSharp`, `src/NightElf.Sdk.CSharp`, `src/NightElf.Sdk.SourceGen`, `src/NightElf.Kernel.Parallel` |
| Core primitives and supporting infrastructure | 12 | `AElf.Database.Tests`, `AElf.Core.Tests`, `AElf.CrossChain.Tests`, `AElf.Cryptography.Tests`, `AElf.TestBase` | `src/NightElf.Database`, `src/NightElf.CrossChain`, `src/NightElf.Core` |
| Web application | 3 | `AElf.WebApp.Application.Chain.Tests`, `AElf.WebApp.Application.Net.Tests` | `src/NightElf.WebApp` |

## Phase 1 Migration Checklist

| Priority | Upstream source | NightElf target | Gate status | Notes |
|----------|-----------------|-----------------|-------------|-------|
| P0 | `AElf.Database.Tests` | `src/NightElf.Database`, `src/NightElf.Database.Redis`, `src/NightElf.Database.Tsavorite`, `src/NightElf.Database.Hosting` | Active | Covered by provider-switching tests and Tsavorite CRUD/store-isolation tests. |
| P0 | `AElf.Kernel.Core.Tests` state-related slices | `src/NightElf.Kernel.Core` | Active | Seeded by `ChainStateDbContext` smoke tests that round-trip `BlockReference` through both providers. |
| P1 | `AElf.Kernel.SmartContract.Tests` (`TieredStateCacheTests`, `StateCacheFromPartialBlockStateSetTests`) | `src/NightElf.Kernel.SmartContract` | Pending module implementation | This is the first expansion after a real state manager lands. |
| P1 | `AElf.Kernel.SmartContractExecution.Tests` | `src/NightElf.Kernel.SmartContract`, `src/NightElf.Kernel.Parallel` | Pending module implementation | Required before switching execution/state merge flows to Tsavorite-backed storage. |
| P1 | `AElf.Kernel.Tests` | `src/NightElf.Kernel.Core`, `src/NightElf.Kernel.Consensus` | Pending module implementation | Covers block attach, mining and chain progression paths that consume state. |
| P2 | `AElf.CrossChain.Core.Tests`, `AElf.CrossChain.Tests` | `src/NightElf.CrossChain` | Backlog | Depends on chain-state primitives stabilizing. |
| P3 | `AElf.Contracts.*.Tests` and runtime/tooling suites | `contract/`, `src/NightElf.Runtime.CSharp`, `src/NightElf.Sdk.CSharp`, `src/NightElf.Sdk.SourceGen` | Backlog | Not a Phase 1 gate; these become mandatory once contract runtime work starts. |
| P4 | `AElf.OS.*.Tests` and `AElf.WebApp.*.Tests` | `src/NightElf.OS.Network`, `src/NightElf.WebApp` | Backlog | Deferred until network and API layers are implemented. |

## Current Phase 1 Gate

The current must-pass suite is the NightElf solution itself:

- `test/NightElf.Architecture.Tests`
- `test/NightElf.Database.Tsavorite.Tests`
- `test/NightElf.Database.Hosting.Tests`
- `test/NightElf.Phase1.Baseline.Tests`

Gate command:

```bash
./eng/test-phase1-baseline.sh
```

Current coverage:

- Module dependency graph stays coherent while storage projects are added.
- Tsavorite CRUD, batch operations and three-store isolation stay green.
- Redis/Tsavorite provider switching remains transparent to `IKeyValueDatabase<T>` callers.
- `ChainStateDbContext` can round-trip state payloads through both providers without key-space collisions.

## Continuous Validation Entry

- Local entry: `./eng/test-phase1-baseline.sh`
- CI entry: `.github/workflows/phase1-baseline.yml`
- Solution entry: `dotnet test NightElf.slnx`

## Expansion Path

1. Keep the Phase 1 gate green on every storage-related change.
2. When `NightElf.Kernel.SmartContract` gets a concrete state manager, port the two state-cache test slices from `AElf.Kernel.SmartContract.Tests` into NightElf integration tests.
3. When execution and checkpoint merging land, add `AElf.Kernel.SmartContractExecution.Tests`-equivalent coverage to the same gate before removing Redis compatibility.
4. Once contract runtime modules exist, split the gate into `phase1-storage`, `phase3-runtime`, and `phase5-network` tracks while preserving `NightElf.slnx` as the shared repository entry point.
