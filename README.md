# NightElf

Architecture-level rebuild of the [AElf](https://github.com/AElfProject/AElf) blockchain, replacing structural bottlenecks with modern .NET 10 solutions while preserving AElf's core design principles.

[中文文档](README_zh.md)

## Why

AElf has evolved over 8 years (23,000+ commits, ~72,000 lines of C# core code) into a full-featured blockchain with modular design, parallel execution, and cross-chain support. However, several structural issues have become deeply embedded:

- **Fake async everywhere** — Redis operations marked `async` but blocking on TCP sockets; `AsyncHelper.RunSync()` forces synchronous execution all the way up to contracts
- **Parallel execution disabled** — `ReflectionTypeLoadException` in concurrent reflection killed the parallel resource extraction pipeline
- **Incomplete contract sandbox** — whitelist + IL patching without true memory isolation; contracts share the node process
- **Non-atomic state merging** — per-key writes on LIB advancement with multi-step state machine recovery
- **God class anti-pattern** — `HostSmartContractBridgeContext` (417 lines) handling state, crypto, contracts, identities, and execution

NightElf addresses these at the architecture level rather than patching around them.

## Tech Stack

| Component | Choice | Rationale |
|-----------|--------|-----------|
| Language | C# 13 | `params ReadOnlySpan<T>`, `field` keyword, semi-auto properties |
| Runtime | .NET 10 (LTS) | NativeAOT, stable QUIC, mature `AssemblyLoadContext`, `System.Threading.Lock` |
| Storage | Tsavorite (embedded) | Eliminates network I/O; true async; incremental checkpoints for atomic state merging |
| Network | gRPC + QUIC | gRPC for RPC compatibility, QUIC for P2P (UDP, NAT-friendly) |
| Protocol | Protocol Buffers | Preserved from AElf — all 91 proto definitions carried forward |
| Consensus | AEDPoS v2 (pluggable) | `IConsensusEngine` interface; VRF as independent module |

## Architecture

```
API Layer (REST / GraphQL / gRPC Gateway)
    │
Application Layer (TransactionPool / BlockSync / FeeManager / Indexer)
    │
Core Engine
  ├─ Consensus (pluggable: AEDPoS v2, extensible)
  ├─ Execution Pipeline (Pre/Execute/Post Plugin, parallel + MVCC, Source Generator dispatch)
  └─ State Manager (TieredCache → Tsavorite, per-contract partition, incremental checkpoint)
    │
Contract Sandbox (AssemblyLoadContext isolation, unloadable, NativeAOT for system contracts)
    │
Network Layer (gRPC + System.Net.Quic dual-mode)
    │
Storage Layer (Embedded Tsavorite)
  ├─ BlockStore (append-only, write-once read-many)
  ├─ StateStore (high-frequency R/W, ≥1GB in-memory)
  └─ IndexStore (read-heavy, range queries)
```

## Key Design Decisions

### Embedded Tsavorite over Redis

AElf uses only GET/SET/MGET/MSET/DEL/EXISTS — no pub/sub, no Lua scripts, no sorted sets. Embedding Tsavorite eliminates TCP round-trips (milliseconds → microseconds), provides true async I/O, and simplifies state merging via incremental checkpoints.

### Source Generators over Reflection

Runtime reflection in parallel execution caused `ReflectionTypeLoadException`. Compile-time Source Generators produce method dispatchers and resource declarations, eliminating the root cause and enabling true parallel transaction processing.

### AssemblyLoadContext Sandbox

`isCollectible: true` enables full memory recovery on contract unload. Per-contract type isolation prevents leakage. `CancellationToken`-based timeout replaces branch counting. System contracts can be NativeAOT-compiled for additional security.

### Channel-based Control Flow

Critical paths (block processing, state merging) use `System.Threading.Channels` instead of `LocalEventBus` for explicit, debuggable, backpressure-aware communication. Non-critical paths (logging, metrics) keep the event bus.

## Project Structure

```
night-elf/
├── docs/                              # Design documents
├── src/
│   ├── NightElf.Core/                 # Core types, DI, module base
│   ├── NightElf.Database/             # Database abstraction layer
│   ├── NightElf.Database.Tsavorite/   # Tsavorite embedded implementation
│   ├── NightElf.Kernel.Core/          # Blockchain core (Block, Tx, State)
│   ├── NightElf.Kernel.SmartContract/ # Contract execution engine
│   ├── NightElf.Kernel.Consensus/     # Consensus abstraction + AEDPoS v2
│   ├── NightElf.Kernel.Parallel/      # Parallel execution (MVCC + Source Gen)
│   ├── NightElf.Runtime.CSharp/       # C# contract runtime (sandbox)
│   ├── NightElf.Sdk.CSharp/           # Contract development SDK
│   ├── NightElf.Sdk.SourceGen/        # Source Generators
│   ├── NightElf.OS.Network/           # P2P network (gRPC + QUIC)
│   ├── NightElf.CrossChain/           # Cross-chain
│   ├── NightElf.WebApp/               # API layer
│   └── NightElf.Launcher/             # Entry point
├── contract/                           # System contracts
├── test/                               # Tests
├── protobuf/                           # Proto definitions
├── Directory.Build.props               # Shared build defaults
├── global.json                         # SDK pin and roll-forward policy
└── NightElf.slnx                       # XML solution file
```

## Build Baseline

The repository is bootstrapped around `NightElf.slnx` with shared build settings in `Directory.Build.props`.

Requirements:

- .NET SDK `10.0.100` feature band or newer

Common commands:

```bash
dotnet restore NightElf.slnx
dotnet test NightElf.slnx
./eng/test-phase1-baseline.sh
```

Current bootstrap projects:

- `src/NightElf.Core`
- `src/NightElf.Database`
- `src/NightElf.Database.Redis`
- `src/NightElf.Database.Hosting`
- `src/NightElf.Database.Tsavorite`
- `src/NightElf.Kernel.Core`
- `src/NightElf.Kernel.SmartContract`
- `test/NightElf.Architecture.Tests`
- `test/NightElf.Database.Hosting.Tests`
- `test/NightElf.Database.Tsavorite.Tests`
- `test/NightElf.Kernel.SmartContract.Tests`
- `test/NightElf.Phase1.Baseline.Tests`

Phase 1 compatibility baseline:

- `docs/002-phase1-test-baseline.md`
- `.github/workflows/phase1-baseline.yml`

Phase 2 async audit baseline:

- `docs/003-runsync-audit.md`

## Roadmap

| Phase | Scope | Duration |
|-------|-------|----------|
| 1 | **Storage layer** — Tsavorite embedding, three-store isolation, test passthrough | 2-3 weeks |
| 2 | **Async fix** — Remove fake async, sync fast-path for cache hits, benchmarking | 1-2 weeks |
| 3 | **Contract execution** — Source Generators, context splitting, parallel validation | 2-4 weeks |
| 4 | **Sandbox + state merging** — AssemblyLoadContext, checkpoint-based merging, fork recovery | 2-3 weeks |
| 5 | **Network + consensus** — QUIC transport, `IConsensusEngine` interface, Channel messaging | 3-4 weeks |

## Compatibility with AElf

### Preserved

- All Protobuf protocol definitions (91 proto files)
- System contract logic and ABI
- Contract SDK API (`CSharpSmartContract<T>` base class)
- Transaction/Block data structures
- Store key prefix system (bb, bh, bs, tx, tr, vs...)
- Module architecture (`AElfModule` base class)

### Changed but Compatible

- `IKeyValueDatabase<T>` interface unchanged, new Tsavorite implementation added
- Module system preserved, internals refactored
- gRPC P2P retained, QUIC added as option

### Breaking Changes

- `AsyncHelper.RunSync()` removal may affect contract threading model
- `AssemblyLoadContext` sandbox may impact contracts relying on AppDomain behavior
- State merging process change requires one-time data migration for existing nodes

## License

[MIT](LICENSE)
