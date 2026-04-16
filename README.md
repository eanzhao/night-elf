# NightElf

Trust infrastructure for autonomous AI agents, built on a modern .NET 10 blockchain substrate.

[中文文档](README_zh.md)

## Why

Traditional blockchain solves "trust among strangers." NightElf solves a different problem: **trust in autonomous AI itself.**

AI agents are probabilistic. LLMs hallucinate. Multi-agent systems produce emergent behaviors that are unpredictable from individual agent testing. When multiple agents collaborate across servers, managing LLM compute resources, executing tasks, and making decisions, you need a deterministic execution layer that:

- Forces every agent to **sign** its actions (cryptographic accountability)
- Provides **atomic** state transitions (no partial updates)
- Enforces **invariants** via smart contracts that no single agent can violate
- Makes every step **auditable** and **replayable** from block height 1
- Enables **natural tenant isolation** through distinct genesis blocks

NightElf's blockchain substrate is rebuilt from [AElf](https://github.com/AElfProject/AElf) (8 years, 23,000+ commits), replacing structural bottlenecks (fake async, disabled parallelism, incomplete sandboxing) with modern .NET 10 solutions while preserving AElf's proven protocol design.

## Tech Stack

| Component | Choice | Rationale |
|-----------|--------|-----------|
| Language | C# 13 | `params ReadOnlySpan<T>`, `field` keyword, semi-auto properties |
| Runtime | .NET 10 (LTS) | NativeAOT, stable QUIC, mature `AssemblyLoadContext`, `System.Threading.Lock` |
| Storage | Tsavorite (embedded) | Eliminates network I/O; true async; incremental checkpoints for atomic state merging |
| Network | gRPC + QUIC | gRPC for RPC compatibility, QUIC for P2P (UDP, NAT-friendly) |
| Protocol | Protocol Buffers | Preserved from AElf — all 91 proto definitions carried forward |
| Consensus | AEDPoS v2 (pluggable) | `IConsensusEngine` interface; VRF as independent module |

## Vision

**AI agents write smart contracts instead of calling static skills.** Traditional agents invoke predefined tools. NightElf agents create new on-chain capabilities by deploying contracts. Contracts are verifiable, persistent, and atomic.

**Ephemeral Treaty Contracts.** Each agent collaboration auto-creates an on-chain charter defining roles, budgets, permissions, challenge windows, and kill switches. NightElf is the constitutional operating system for private agent organizations.

**Orleans runs agents, NightElf provides trust.** The agent runtime (actor placement, recovery, messaging) is handled by frameworks like Orleans. NightElf handles the trust layer: signatures, global ordering, atomic execution, and audit.

## Architecture

```
AI Agent Layer (Orleans / Aevatar / any agent framework)
    │ gRPC (IChainSettlement)
    │
API Layer (gRPC Gateway)
    │
Application Layer (TransactionPool / BlockSync / AgentSession)
    │
Core Engine
  ├─ Consensus (pluggable: SingleValidator / AEDPoS v2, extensible)
  ├─ Execution Pipeline (Pre/Execute/Post Plugin, parallel + MVCC, Source Generator dispatch)
  └─ State Manager (TieredCache → Tsavorite, per-contract partition, incremental checkpoint)
    │
Contract Sandbox (AssemblyLoadContext isolation, unloadable, dynamic deployment)
    │
Network Layer (gRPC + System.Net.Quic dual-mode)
    │
Storage Layer (Embedded Tsavorite)
  ├─ BlockStore (append-only, write-once read-many)
  ├─ StateStore (high-frequency R/W, ≥1GB in-memory)
  └─ IndexStore (read-heavy, range queries)
```

## Key Design Decisions

### Blockchain as AI Trust Layer

AI agents are probabilistic; blockchain execution is deterministic. This combination gives you: signed agent identity, globally ordered operations, atomic resource allocation, and full audit trail. The latency overhead of block production (seconds) is negligible compared to LLM inference latency.

### Embedded Tsavorite over Redis

AElf uses only GET/SET/MGET/MSET/DEL/EXISTS. Embedding Tsavorite eliminates TCP round-trips (milliseconds → microseconds), provides true async I/O, and simplifies state merging via incremental checkpoints.

### Source Generators over Reflection

Runtime reflection caused `ReflectionTypeLoadException` in parallel execution. Compile-time Source Generators produce method dispatchers and resource declarations, enabling true parallel transaction processing and dynamic contract deployment.

### AssemblyLoadContext Sandbox

`isCollectible: true` enables full memory recovery on contract unload. Per-contract type isolation prevents leakage. Critical for dynamic contract deployment: AI-generated contracts run in isolated sandboxes with whitelist API, resource limits, and IL static analysis.

### Channel-based Control Flow

Critical paths (block processing, state merging) use `System.Threading.Channels` for explicit, debuggable, backpressure-aware communication.

## Project Structure

```
night-elf/
├── docs/                              # Design documents
├── benchmarks/
│   └── NightElf.Benchmarks/           # BenchmarkDotNet harness
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

## Build

Requirements: .NET SDK `10.0.100` feature band or newer.

```bash
dotnet restore NightElf.slnx
dotnet test NightElf.slnx
./eng/test-phase1-baseline.sh
./eng/run-benchmarks.sh
```

Design documents are in `docs/`. Substrate implementation details: `docs/001-016`.

## Roadmap

The substrate layer (storage, async, contract execution, sandbox, network, consensus) is largely implemented. The roadmap now focuses on assembling these components into a running node and building the AI agent integration layer.

| Phase | Scope | Goal |
|-------|-------|------|
| 1 | **Minimal Viable Chain** | Node bootstrap, block production, transaction processing, AgentSession system contract |
| 2 | **Agent Settlement Layer** | IChainSettlement gRPC interface, LLM token metering (verified/self-reported), multi-node AEDPoS |
| 3 | **Constitutional Agent OS** | Ephemeral Treaty Contracts, dynamic contract generation, Agent Registry |

### Substrate (complete)

The following foundational work is done:

- Tsavorite embedded storage with three-store isolation (Block/State/Index)
- Async pipeline with sync fast-path for cache hits
- Source Generator contract dispatch and resource declarations
- AssemblyLoadContext contract sandbox with NativeAOT evaluation
- QUIC transport option for P2P networking
- Pluggable consensus engine abstraction (AEDPoS v2 with VRF)
- Channel-based block processing pipeline

## Compatibility with AElf

NightElf preserves AElf's protocol layer (Protobuf definitions, contract SDK API, transaction/block structures, store key prefix system, module architecture) while replacing internals. Key changes: `AsyncHelper.RunSync()` removal, `AssemblyLoadContext` sandbox replacing IL patching, Tsavorite replacing Redis.

## License

[MIT](LICENSE)
