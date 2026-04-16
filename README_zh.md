# NightElf

自主 AI Agent 的可信基础设施，基于现代 .NET 10 区块链底层构建。

[English](README.md)

## 为什么

传统区块链解决"人与人之间的信任"问题。NightElf 解决一个不同的问题：**信任自主 AI 本身。**

AI agent 是概率性的。LLM 会幻觉，多 agent 系统会产生不可预测的涌现行为。当多个 agent 跨服务器协作、管理 LLM 算力资源、执行任务、做决策时，你需要一个确定性的执行层来：

- 强制每个 agent 对其行为**签名**（密码学问责）
- 提供**原子性**状态转换（不存在部分更新）
- 通过智能合约强制执行**不变量**，任何单个 agent 都无法违反
- 让每一步都**可审计**、**可重放**（从 height 1 开始同步即可重建完整状态）
- 通过不同的 genesis block 实现**天然租户隔离**

NightElf 的区块链底层重构自 [AElf](https://github.com/AElfProject/AElf)（8 年，23,000+ 次提交），用现代 .NET 10 技术栈替换了结构性瓶颈（假异步、禁用的并行执行、不完整的沙箱），同时保留 AElf 经过验证的协议设计。

## 技术栈

| 组件 | 选型 | 理由 |
|------|------|------|
| 语言 | C# 13 | `params ReadOnlySpan<T>`、`field` 关键字、半自动属性 |
| 运行时 | .NET 10 (LTS) | NativeAOT、稳定 QUIC、成熟 `AssemblyLoadContext`、`System.Threading.Lock` |
| 存储 | Tsavorite（嵌入式） | 消除网络 I/O；真正的异步；增量 checkpoint 实现原子状态合并 |
| 网络 | gRPC + QUIC | gRPC 保持 RPC 兼容，QUIC 用于 P2P（UDP，NAT 友好） |
| 协议 | Protocol Buffers | 继承自 AElf — 全部 91 个 proto 定义保留 |
| 共识 | AEDPoS v2（可插拔） | `IConsensusEngine` 接口；VRF 作为独立模块 |

## 愿景

**AI agent 写合约，而不是调用静态 Skills。** 传统 agent 调用预定义工具。NightElf 的 agent 通过部署合约来创造新的链上能力。合约是可验证的、持久的、原子性的。

**临时条约合约（Ephemeral Treaty Contracts）。** 每次 agent 协作自动创建链上章程，定义角色、预算、权限、挑战窗口和 kill switch。NightElf 是私有 agent 组织的宪法操作系统。

**Orleans 跑 agent，NightElf 做信任层。** Agent 运行时（actor 放置、恢复、消息传递）由 Orleans 等框架处理。NightElf 处理信任层：签名、全局排序、原子执行和审计。

## 架构

```
AI Agent 层（Orleans / Aevatar / 任意 agent 框架）
    │ gRPC (IChainSettlement)
    │
API 层（gRPC Gateway）
    │
应用层（TransactionPool / BlockSync / AgentSession）
    │
核心引擎
  ├─ 共识（可插拔：SingleValidator / AEDPoS v2，可扩展）
  ├─ 执行管道（Pre/Execute/Post Plugin，并行 + MVCC，Source Generator 调度）
  └─ 状态管理器（TieredCache → Tsavorite，按合约分区，增量 checkpoint）
    │
合约沙箱（AssemblyLoadContext 隔离，可卸载，支持动态部署）
    │
网络层（gRPC + System.Net.Quic 双模）
    │
存储层（嵌入式 Tsavorite）
  ├─ BlockStore（追加写入，写一次读多次）
  ├─ StateStore（高频读写，≥1GB 内存）
  └─ IndexStore（读多写少，范围查询）
```

## 核心设计决策

### 区块链作为 AI 信任层

AI agent 是概率性的，区块链执行是确定性的。二者结合：签名的 agent 身份、全局排序的操作、原子性的资源分配、完整的审计轨迹。出块的延迟开销（秒级）相对于 LLM 推理延迟可忽略不计。

### 嵌入式 Tsavorite 替代 Redis

AElf 仅使用 GET/SET/MGET/MSET/DEL/EXISTS。嵌入 Tsavorite 消除 TCP 往返（毫秒级 → 微秒级），提供真正的异步 I/O，增量 checkpoint 简化状态合并。

### Source Generator 替代运行时反射

运行时反射在并行执行中导致 `ReflectionTypeLoadException`。编译时 Source Generator 生成方法调度器和资源声明，支持真正的并行交易处理和动态合约部署。

### AssemblyLoadContext 沙箱

`isCollectible: true` 使合约卸载后内存彻底回收。按合约做程序集级隔离。对动态合约部署至关重要：AI 生成的合约在隔离沙箱中运行，配合白名单 API、资源限制和 IL 静态分析。

### Channel 替代事件总线

关键路径（块处理、状态合并）使用 `System.Threading.Channels`，提供显式、可调试、带背压的通信。

## 项目结构

```
night-elf/
├── docs/                              # 设计文档
├── benchmarks/
│   └── NightElf.Benchmarks/           # BenchmarkDotNet 基准项目
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
├── protobuf/                           # Proto 定义
├── Directory.Build.props               # 共享构建默认值
├── global.json                         # SDK 锁定与 roll-forward 策略
└── NightElf.slnx                       # XML 解决方案文件
```

## 构建

环境要求：.NET SDK `10.0.100` feature band 或更新版本。

```bash
dotnet restore NightElf.slnx
dotnet test NightElf.slnx
./eng/test-phase1-baseline.sh
./eng/run-benchmarks.sh
```

设计文档在 `docs/` 目录。底层实现细节见 `docs/001-016`。

## 实施路线

底层基础设施（存储、异步、合约执行、沙箱、网络、共识）已基本完成。路线图现在聚焦于将这些组件装配为可运行的节点，并构建 AI agent 集成层。

| 阶段 | 范围 | 目标 |
|------|------|------|
| 1 | **最小可行链** | 节点启动、出块、交易处理、AgentSession 系统合约 |
| 2 | **Agent 结算层** | IChainSettlement gRPC 接口、LLM token 计量（verified/self-reported）、多节点 AEDPoS |
| 3 | **宪法 Agent OS** | 临时条约合约、动态合约生成、Agent 注册表 |

### 底层基础设施（已完成）

- Tsavorite 嵌入式存储，三 Store 隔离（Block/State/Index）
- 异步管道，缓存命中走同步快路径
- Source Generator 合约调度和资源声明
- AssemblyLoadContext 合约沙箱，NativeAOT 评估
- QUIC P2P 传输选项
- 可插拔共识引擎抽象（AEDPoS v2 含 VRF）
- Channel 驱动的区块处理管道

## 与 AElf 的兼容性

NightElf 保留 AElf 的协议层（Protobuf 定义、合约 SDK API、交易/区块结构、Store key 前缀体系、模块化架构），同时替换内部实现。主要变更：移除 `AsyncHelper.RunSync()`、`AssemblyLoadContext` 沙箱替代 IL 补丁、Tsavorite 替代 Redis。

## 许可证

[MIT](LICENSE)
