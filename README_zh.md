# NightElf

对 [AElf](https://github.com/AElfProject/AElf) 区块链的架构级重构，用现代 .NET 10 技术栈替换结构性瓶颈，同时保留 AElf 的核心设计理念。

[English](README.md)

## 为什么要重构

AElf 经过 8 年演进（23,000+ 次提交，约 72,000 行 C# 核心代码），已成为功能完整的区块链系统，具备模块化设计、并行执行和跨链能力。但若干结构性问题已深度嵌入：

- **全链路假异步** — Redis 操作标记为 `async` 但实际阻塞在 TCP socket 上；`AsyncHelper.RunSync()` 从合约层一路强制同步执行
- **并行执行被禁用** — 并发反射导致的 `ReflectionTypeLoadException` 使并行资源提取管道失效
- **合约沙箱不彻底** — 白名单 + IL 补丁缺少真正的内存隔离，合约与节点共享进程
- **状态合并非原子** — LIB 推进时逐 key 写入，依赖多步状态机做崩溃恢复
- **上帝类反模式** — `HostSmartContractBridgeContext`（417 行）同时承担状态、密码学、合约、身份、执行等职责

NightElf 从架构层面解决这些问题，而非在外围打补丁。

## 技术栈

| 组件 | 选型 | 理由 |
|------|------|------|
| 语言 | C# 13 | `params ReadOnlySpan<T>`、`field` 关键字、半自动属性 |
| 运行时 | .NET 10 (LTS) | NativeAOT、稳定 QUIC、成熟 `AssemblyLoadContext`、`System.Threading.Lock` |
| 存储 | Tsavorite（嵌入式） | 消除网络 I/O；真正的异步；增量 checkpoint 实现原子状态合并 |
| 网络 | gRPC + QUIC | gRPC 保持 RPC 兼容，QUIC 用于 P2P（UDP，NAT 友好） |
| 协议 | Protocol Buffers | 继承自 AElf — 全部 91 个 proto 定义保留 |
| 共识 | AEDPoS v2（可插拔） | `IConsensusEngine` 接口；VRF 作为独立模块 |

## 架构

```
API 层 (REST / GraphQL / gRPC Gateway)
    │
应用层 (TransactionPool / BlockSync / FeeManager / Indexer)
    │
核心引擎
  ├─ 共识（可插拔：AEDPoS v2，可扩展）
  ├─ 执行管道（Pre/Execute/Post Plugin，并行 + MVCC，Source Generator 调度）
  └─ 状态管理器（TieredCache → Tsavorite，按合约分区，增量 checkpoint）
    │
合约沙箱（AssemblyLoadContext 隔离，可卸载，系统合约 NativeAOT）
    │
网络层（gRPC + System.Net.Quic 双模）
    │
存储层（嵌入式 Tsavorite）
  ├─ BlockStore（追加写入，写一次读多次）
  ├─ StateStore（高频读写，≥1GB 内存）
  └─ IndexStore（读多写少，范围查询）
```

## 核心设计决策

### 嵌入式 Tsavorite 替代 Redis

AElf 仅使用 GET/SET/MGET/MSET/DEL/EXISTS 六种操作，不依赖 pub/sub、Lua 脚本、sorted set 等高级特性。嵌入 Tsavorite 消除 TCP 往返（毫秒级 → 微秒/纳秒级），提供真正的异步 I/O，并通过增量 checkpoint 简化状态合并。

### Source Generator 替代运行时反射

运行时反射在并行执行中导致 `ReflectionTypeLoadException`。编译时 Source Generator 生成方法调度器和资源声明，从根源消除问题，实现真正的并行交易处理。

### AssemblyLoadContext 沙箱

`isCollectible: true` 使合约卸载后内存彻底回收。按合约做程序集级隔离，防止类型泄漏。基于 `CancellationToken` 的超时机制取代分支计数。系统合约可选 NativeAOT 编译，进一步收紧沙箱边界。

### Channel 替代事件总线

关键路径（块处理、状态合并）使用 `System.Threading.Channels`，提供显式、可调试、带背压的通信。非关键路径（日志、监控）保留事件总线。

## 项目结构

```
night-elf/
├── docs/                              # 设计文档
├── src/
│   ├── NightElf.Core/                 # 核心类型、DI、模块基类
│   ├── NightElf.Database/             # 数据库抽象层
│   ├── NightElf.Database.Tsavorite/   # Tsavorite 嵌入式实现
│   ├── NightElf.Kernel.Core/          # 区块链核心（Block, Tx, State）
│   ├── NightElf.Kernel.SmartContract/ # 合约执行引擎
│   ├── NightElf.Kernel.Consensus/     # 共识抽象 + AEDPoS v2
│   ├── NightElf.Kernel.Parallel/      # 并行执行（MVCC + Source Gen）
│   ├── NightElf.Runtime.CSharp/       # C# 合约运行时（沙箱）
│   ├── NightElf.Sdk.CSharp/           # 合约开发 SDK
│   ├── NightElf.Sdk.SourceGen/        # Source Generator
│   ├── NightElf.OS.Network/           # P2P 网络（gRPC + QUIC）
│   ├── NightElf.CrossChain/           # 跨链
│   ├── NightElf.WebApp/               # API 层
│   └── NightElf.Launcher/             # 启动入口
├── contract/                           # 系统合约
├── test/                               # 测试
└── protobuf/                           # Proto 定义
```

## 实施路线

| 阶段 | 范围 | 周期 |
|------|------|------|
| 1 | **存储层** — Tsavorite 嵌入，三 Store 隔离，测试通过 | 2-3 周 |
| 2 | **异步修复** — 消除假 async，缓存命中走同步快路径，基准测试 | 1-2 周 |
| 3 | **合约执行** — Source Generator，Context 拆分，并行验证 | 2-4 周 |
| 4 | **沙箱 + 状态合并** — AssemblyLoadContext，checkpoint 合并，分叉恢复 | 2-3 周 |
| 5 | **网络 + 共识** — QUIC 传输，`IConsensusEngine` 接口，Channel 通信 | 3-4 周 |

## 与 AElf 的兼容性

### 保留不变

- 全部 Protobuf 协议定义（91 个 proto 文件）
- 系统合约逻辑和 ABI
- 合约 SDK API（`CSharpSmartContract<T>` 基类）
- 交易/区块数据结构
- Store key 前缀体系（bb, bh, bs, tx, tr, vs...）
- 模块化架构（`AElfModule` 基类）

### 变更但兼容

- `IKeyValueDatabase<T>` 接口不变，新增 Tsavorite 实现
- 模块体系保留，内部实现重构
- gRPC P2P 保留，新增 QUIC 选项

### 破坏性变更

- `AsyncHelper.RunSync()` 的消除可能影响合约执行的 threading model
- `AssemblyLoadContext` 沙箱可能影响依赖 AppDomain 行为的合约
- 状态合并流程变更，已有节点数据需一次性迁移

## 许可证

[MIT](LICENSE)
