# NightElf: AElf 区块链重构方案

## 项目定位

NightElf 是对 [AElf](https://github.com/AElfProject/AElf) 区块链的架构级重构。AElf 自 2017 年 11 月启动，经过 8 年、23,000+ 次提交的演进，积累了约 72,000 行 C# 核心代码（src + contract），涵盖共识、智能合约执行、跨链、网络、治理等完整功能。NightElf 在保留 AElf 核心设计理念（模块化、并行执行、跨链）的基础上，用 2026 年验证过的技术栈和架构模式重新构建，解决原有系统中的结构性瓶颈。

技术栈：C# 13 / .NET 10 (LTS)，存储引擎 Garnet (Tsavorite)。

---

## 一、AElf 现有架构回顾

### 1.1 整体架构

AElf 采用 DDD 四层架构 + ABP 模块化框架，核心分层：

```
┌────────────────────────────────────────────────────┐
│              API Layer (WebApp)                     │
│         REST API / Swagger / gRPC                   │
├────────────────────────────────────────────────────┤
│            Application Layer                        │
│   TransactionPool / BlockSync / FeeManager          │
├────────────────────────────────────────────────────┤
│              Kernel Layer                            │
│   Consensus(AEDPoS) / Execution / SmartContract     │
│   State(TieredStateCache) / CrossChain              │
├────────────────────────────────────────────────────┤
│               OS Layer                              │
│          gRPC P2P / Block Sync                      │
├────────────────────────────────────────────────────┤
│            Infrastructure                           │
│      Redis (自定义协议客户端) / Protobuf             │
└────────────────────────────────────────────────────┘
```

关键技术选型：
- 框架：ASP.NET Core + Volo.Abp（模块化）
- 序列化：Protocol Buffers
- 网络：gRPC（P2P 和跨链通信）
- 存储：Redis（自定义 RESP 协议客户端）+ SSDB 兼容
- 合约运行时：C# IL（Mono.Cecil 补丁 + 白名单沙箱）
- 共识：AEDPoS（AElf Delegated Proof of Stake）+ 秘密共享随机数

### 1.2 做得好的地方

**模块化设计**：59 个 src 项目通过 `AElfModule` 基类组织，依赖关系清晰，各功能域（Kernel、Consensus、CrossChain、OS）可独立演进。这在 2017 年的区块链项目中是相当先进的。

**Protobuf 贯穿始终**：从核心数据结构（Block、Transaction、TransactionResult）到合约 ABI 全部基于 Protobuf，保证了跨语言兼容性、高效序列化和明确的 schema 演进。91 个 proto 定义覆盖了完整的协议规范。

**智能合约执行管道**：三阶段设计（Pre-Plugin → Main Execution → Post-Plugin）支持交易费预扣、内联交易、状态隔离。Executive 对象池（50 个/合约，1 小时过期）减少了编译开销。

**状态版本化**：`BlockStateSet` + `VersionedState` 支持分叉恢复而无需完全重建状态树。每个块的状态变更记录为独立的 BlockStateSet，通过 PreviousHash 链接形成可回溯链。

**并行交易框架**：使用 Union-Find 算法对交易进行资源冲突分组，思路正确——在 2018 年是前沿设计。

**系统合约体系**：18 个系统合约覆盖治理（Parliament、Referendum、Association）、经济模型（Treasury、Profit、Election）、跨链验证等完整功能。

### 1.3 结构性问题

以下是需要在 NightElf 中解决的核心问题，按严重程度排列。

#### 1.3.1 数据库层：假异步 + 网络瓶颈

`RedisDatabase` 所有方法标记为 `async` 但实际是同步阻塞调用：

```csharp
// src/AElf.Database/RedisDatabase.cs
#pragma warning disable 1998

public async Task<byte[]> GetAsync(string key)
{
    return _pooledRedisLite.Get(key);  // 同步 TCP socket 往返
}
```

连接池使用 `Monitor.Wait` 做锁控制（`PooledRedisLite`，20 个连接），高 TPS 下成为串行化瓶颈。整个 async 链路从数据库层到合约执行层全部是假的——`AsyncHelper.RunSync()` 在合约侧再次将异步强制转为同步。

状态读取的完整调用链：

```
Smart Contract → AsyncHelper.RunSync()
  → SmartContractBridgeService.GetStateAsync()
    → BlockchainStateManager.GetAsync()
      → NotModifiedCachedStateStore (ConcurrentDictionary 缓存)
        → KeyValueStoreBase → KeyValueCollection
          → RedisDatabase.GetAsync()  ← 假 async，TCP 阻塞
            → PooledRedisLite.Get()
              → RedisLite.SendExpectData()  ← Raw socket I/O
```

5 层缓存能缓解部分压力，但缓存未命中时每次状态读取都要付出一次 TCP 往返。

#### 1.3.2 并行执行未完成

资源提取的并行化已被禁用：

```csharp
// src/AElf.Kernel.SmartContract.Parallel/ResourceExtractionService.cs
// TODO: Parallel processing causes ReflectionTypeLoadException
```

冲突检测是事后反应式的（执行完才发现冲突），冲突交易需要串行重新执行且不缓存第一次结果。分组超时硬编码为 500ms，无自适应策略。`ReflectionTypeLoadException` 的根因是运行时反射在并行环境下不安全。

#### 1.3.3 合约沙箱不彻底

当前沙箱依赖白名单 + Mono.Cecil IL 补丁，但合约运行在与节点相同的进程中：

- 无真正的内存隔离（合约可以无限分配）
- 调用/分支计数不等于真正的超时机制
- 白名单 `.*` 通配符匹配存在绕过风险
- 单 AppDomain 运行，合约间没有隔离边界

#### 1.3.4 状态合并复杂度

状态合并流程（LIB 推进时）逐 key 写入：

```
读取 BlockStateSet → 设置状态为 Merging
  → SetAllAsync(所有 changes) → RemoveAllAsync(所有 deletes)
  → 设置状态为 Merged → 删除 BlockStateSet → 设置状态为 Common
```

大块包含数千个 state key 变更时，这是一次性的批量写入风暴。且整个过程不是原子的——中间崩溃需要依赖状态机（COMMON → MERGING → MERGED）做恢复。

#### 1.3.5 其他问题

- **HostSmartContractBridgeContext**（417 行上帝类）：同时负责交易处理、状态访问、合约调用、内联交易、虚拟地址、签名、随机数生成、ID 生成
- **事件总线隐式耦合**：块执行、并行冲突、状态清理通过 `LocalEventBus` 通信，控制流难以追踪
- **跨链验证依赖可信索引**：缺少基于密码学证明的独立验证
- **Magic Constants 散布**：500ms 超时、Depth=0、GenesisBlockHeight+1 等硬编码值

---

## 二、NightElf 重构方案

### 2.1 核心原则

1. **嵌入式存储优先**：消除网络层，Tsavorite 作为进程内存储引擎
2. **真正的异步**：从存储层到执行层，async/await 链路完整无阻塞
3. **编译时优于运行时**：Source Generator 替代反射，消除并行执行的阻塞根因
4. **按职责隔离**：状态存储按合约分区，缓存层级明确，沙箱用 AssemblyLoadContext 隔离
5. **渐进式改进**：保留 Protobuf 协议、系统合约体系、模块化理念，替换底层实现

### 2.1.1 为什么选 .NET 10

.NET 10 是 2025 年 11 月发布的 LTS 版本（支持至 2028 年），相比 AElf 目前使用的 .NET 版本，有以下对本项目直接相关的改进：

| 特性 | 对 NightElf 的价值 |
|------|-------------------|
| **NativeAOT 成熟** | 合约沙箱可以对系统合约做 AOT 编译，消除 JIT 预热延迟；节点启动时间从秒级降到毫秒级 |
| **`System.Net.Quic` 稳定** | 经过 .NET 8/9/10 三个版本迭代，QUIC 栈已生产就绪，可用于 P2P 传输 |
| **`Span<T>` / `Memory<T>` 全栈优化** | Tsavorite 的 `SpanByte` 与 .NET 10 的 Span 优化天然契合，零拷贝路径更完整 |
| **Source Generator 增强** | Incremental Source Generator 性能更好，IDE 支持更完善，适合合约调度代码生成 |
| **`AssemblyLoadContext` 可卸载程序集** | 从 .NET 5 引入，经过 5 个版本打磨，内存回收行为稳定可靠 |
| **`System.Threading.Lock`** | .NET 9 引入的专用锁类型，比 `Monitor` 更轻量，替代当前连接池的 `Monitor.Wait` |
| **`Task.WhenEach`** | .NET 9 引入，简化并行交易组的结果收集 |
| **`params ReadOnlySpan<T>`** | C# 13 特性，减少批量操作的数组分配（状态批量读写路径） |
| **`field` 关键字** | C# 13 半自动属性，简化状态管理类的 boilerplate |
| **Server GC 改进** | .NET 10 的 GC 在高吞吐服务器场景下暂停时间更短，对区块链的确定性执行更友好 |

### 2.2 目标架构

```
┌──────────────────────────────────────────────────────────┐
│                     API Layer                            │
│          REST / GraphQL / gRPC Gateway                   │
├──────────────────────────────────────────────────────────┤
│                  Application Layer                       │
│    TransactionPool / BlockSync / FeeManager / Indexer    │
├──────────────────────────────────────────────────────────┤
│                   Core Engine                            │
│  ┌──────────────┬───────────────┬──────────────────┐     │
│  │   Consensus   │   Execution   │      State       │     │
│  │  (pluggable)  │   Pipeline    │    Manager       │     │
│  │               │               │                  │     │
│  │  AEDPoS v2    │  Pre/Execute  │  TieredCache     │     │
│  │  HotStuff     │  /Post Plugin │  → Tsavorite     │     │
│  │  (可插拔)      │  并行+MVCC    │  per-contract    │     │
│  │               │  Source Gen   │  partition        │     │
│  │  VRF 独立模块  │  调度         │  增量 checkpoint  │     │
│  └──────────────┴───────────────┴──────────────────┘     │
├──────────────────────────────────────────────────────────┤
│                  Contract Sandbox                        │
│   AssemblyLoadContext 隔离 / 可卸载 / 内存限制             │
├──────────────────────────────────────────────────────────┤
│                  Network Layer                           │
│       gRPC + System.Net.Quic (QUIC/TCP 双模)             │
├──────────────────────────────────────────────────────────┤
│                  Storage Layer                           │
│            Embedded Tsavorite (Garnet 引擎)               │
│   ┌──────────────┬──────────────┬──────────────────┐     │
│   │ BlockStore    │ StateStore   │ IndexStore       │     │
│   │ (append-only) │ (高频读写)    │ (查询优化)        │     │
│   │ 大 segment    │ 大内存分配    │ 中等内存          │     │
│   └──────────────┴──────────────┴──────────────────┘     │
└──────────────────────────────────────────────────────────┘
```

### 2.3 存储层：Embedded Tsavorite

#### 设计决策

不使用 Garnet 的 Server 模式（RESP 协议兼容），而是直接嵌入 Tsavorite 存储引擎到节点进程内。

**理由**：
- AElf 仅使用 GET/SET/MGET/MSET/DEL/EXISTS 六种操作，不依赖 Redis 的 pub/sub、Lua 脚本、sorted set 等高级特性，不需要 RESP 协议层
- 嵌入式模式消除 TCP 往返（从毫秒级降到微秒/纳秒级）
- Tsavorite 原生支持异步 I/O，修复假 async 问题
- Tsavorite 的增量 checkpoint 可以简化状态合并

#### 实现：AElf.Database.Tsavorite

新增 `IKeyValueDatabase<T>` 实现，与现有 `RedisDatabase` 平级，通过配置切换：

```csharp
public class TsavoriteDatabase<TKeyValueDbContext>(KeyValueDatabaseOptions<TKeyValueDbContext> options)
    : IKeyValueDatabase<TKeyValueDbContext>
    where TKeyValueDbContext : KeyValueDbContext<TKeyValueDbContext>
{
    private readonly TsavoriteKV<SpanByte, SpanByte> _store = CreateStore(options);

    private static TsavoriteKV<SpanByte, SpanByte> CreateStore(
        KeyValueDatabaseOptions<TKeyValueDbContext> options)
    {
        var logSettings = new LogSettings
        {
            LogDevice = Devices.CreateLogDevice(options.DataPath),
            PageSizeBits = 20,       // 1MB pages
            MemorySizeBits = 28,     // 256MB in-memory (StateStore 可调大)
            SegmentSizeBits = 30     // 1GB segments
        };

        return new TsavoriteKV<SpanByte, SpanByte>(
            size: 1L << 20,
            logSettings: logSettings,
            checkpointSettings: new CheckpointSettings
            {
                CheckpointDir = options.CheckpointPath
            }
        );
    }

    // 真正的 async —— 热数据同步返回，冷数据异步磁盘读取
    public async Task<byte[]> GetAsync(string key) { ... }

    // 批量写入，单次 I/O flush
    public async Task SetAllAsync(IDictionary<string, byte[]> values) { ... }
}
```

#### Store 实例隔离

按访问模式将单一 Redis 实例拆分为三个独立的 Tsavorite 实例：

| Store | 包含数据 | 访问特征 | 调优方向 |
|-------|---------|---------|---------|
| **BlockStore** | BlockHeader(bh), BlockBody(bb), Transaction(tx), TransactionResult(tr) | Write-once, read-many, append-only | 大 SegmentSize, 中等 MemorySize |
| **StateStore** | VersionedState(vs), BlockStateSet(bs), ChainStateInfo(cs) | 高频读写，热路径 | 最大 MemorySize (≥1GB), 小 PageSize |
| **IndexStore** | ChainBlockLink(cl), ChainBlockIndex(ci), TransactionBlockIndex(ti) | 读多写少，范围查询 | 中等配置 |

每个实例独立 checkpoint，互不阻塞。

#### 状态合并简化

用 Tsavorite 的增量 checkpoint 替代逐 key 合并：

```
当前流程（每次 LIB 推进）:
  读 BlockStateSet → 逐 key SetAllAsync → 逐 key RemoveAllAsync → 删 BlockStateSet
  代价：N 次写入 + M 次删除 + 状态机恢复逻辑

NightElf 流程:
  直接写入 Tsavorite（带块高度版本标记）
  LIB 推进 → TakeIncrementalCheckpointAsync()
  分叉恢复 → RecoverToCheckpoint(libCheckpoint)
  代价：一次 checkpoint 操作，原子性由 Tsavorite 保证
```

### 2.4 Async 链路修复

#### 问题本质

当前从数据库到合约执行的 5 层调用全部是假 async：

```
RedisDatabase (#pragma warning disable 1998)
  → KeyValueCollection (await 一个假 Task)
    → KeyValueStoreBase (await 一个假 Task)
      → NotModifiedCachedStateStore (混合缓存/假 async)
        → SmartContractBridgeService (await)
          → AsyncHelper.RunSync()  ← 在合约侧强制同步
```

#### 修复策略

**存储层**：Tsavorite 原生 async I/O，无需 `#pragma warning disable`。

**合约执行层**：引入同步快路径，避免不必要的 async 开销：

```csharp
// 替代 AsyncHelper.RunSync()
// 大多数状态读取命中 L1-L3 缓存，不需要 async
public byte[] GetState(string key)
{
    // L1: TieredStateCache (内存字典)
    if (_tieredCache.TryGet(key, out var value))
        return value;

    // L2: NotModifiedCachedStateStore (ConcurrentDictionary)
    if (_cachedStore.TryGetCached(key, out value))
        return value;

    // L3: Tsavorite in-memory page (热数据)
    // L4: Tsavorite 磁盘读取 (冷数据) —— 仅此路径需要 async
    return AsyncHelper.RunSync(() => _store.GetAsync(key));
}
```

目标：95%+ 的状态读取在 L1-L3 同步完成，仅冷数据走 async 路径。

### 2.5 并行执行修复

#### 根因

`ReflectionTypeLoadException` 发生在并行环境下的运行时反射（合约方法和资源提取都依赖反射）。

#### 方案：Source Generator 替代运行时反射

在编译时为每个合约生成方法调度器和资源声明：

```csharp
// 合约代码（开发者编写）
[ContractMethod]
public override Empty Transfer(TransferInput input) { ... }

// Source Generator 自动生成（编译时）
[GeneratedCode]
public static partial class TokenContract_Dispatcher
{
    public static Task<byte[]> Dispatch(string methodName, byte[] input) => methodName switch
    {
        "Transfer" => Execute_Transfer(input),
        "Approve" => Execute_Approve(input),
        _ => throw new ContractMethodNotFoundException(methodName)
    };
}

[GeneratedCode]
public static partial class TokenContract_Resources
{
    public static IReadOnlyList<string> GetWriteKeys(string methodName, byte[] input) => methodName switch
    {
        "Transfer" => ExtractTransferKeys(input),  // 静态分析得出的 state key 前缀
        _ => Array.Empty<string>()
    };
}
```

消除反射 = 消除 `ReflectionTypeLoadException` = 解除并行资源提取的阻塞。

#### 并行执行增强

```
                    Transaction Batch
                          │
                ┌─────────┼─────────┐
                ▼         ▼         ▼
          ┌──────────┐ ┌──────────┐ ┌──────────┐
          │ Group A  │ │ Group B  │ │ Group C  │
          │(乐观执行) │ │(乐观执行) │ │(乐观执行) │
          └────┬─────┘ └────┬─────┘ └────┬─────┘
               │            │            │
               ▼            ▼            ▼
        ┌────────────────────────────────────────┐
        │       Read-Write Set 冲突检测           │
        │                                        │
        │  无冲突组 → 批量原子提交                  │
        │  冲突交易 → 重新分组后再次尝试并行         │
        │           （而非直接退化为串行）           │
        └────────────────────────────────────────┘
```

配合 Tsavorite 的 per-contract state partition，不同合约的状态写入天然无冲突。

### 2.6 合约沙箱：AssemblyLoadContext 隔离

用 .NET 10 的 `AssemblyLoadContext` 替代当前的白名单 + IL 补丁方案。.NET 10 作为 LTS 版本，`AssemblyLoadContext` 的可卸载程序集支持已经完全成熟，配合 NativeAOT 编译可进一步收紧沙箱边界：

```csharp
public class ContractSandbox : AssemblyLoadContext
{
    public ContractSandbox(string contractName)
        : base(contractName, isCollectible: true)  // 可卸载 = 内存可回收
    {
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 白名单验证保留，但在 LoadContext 层面提供额外隔离
        if (!AllowedAssemblies.Contains(assemblyName.Name))
            return null;
        return LoadFromStream(GetAssemblyStream(assemblyName));
    }
}
```

改进点：
- `isCollectible: true`：合约可卸载，内存彻底回收（当前方案合约加载后无法卸载）。经过 .NET 5-10 六个版本的打磨，可卸载 ALC 的内存泄漏和边界情况已基本修复
- 程序集级别隔离：一个合约的类型不会泄漏到另一个合约
- 配合 `CancellationToken` + `CancellationTokenSource.CancelAfter()` 做执行超时，比分支计数更可靠
- IL 补丁保留用于 Gas 计量（不依赖沙箱安全性）
- 系统合约可选 NativeAOT 编译，进一步收紧沙箱边界（AOT 代码无反射能力）

### 2.7 网络层增强

保留 gRPC 作为主要通信协议，新增 QUIC 支持。.NET 10 的 `System.Net.Quic` 已进入稳定状态，QUIC 协议栈基于 msquic，性能和可靠性在 .NET 8-10 三个版本中持续打磨：

```csharp
// .NET 10 原生 QUIC 支持
var listener = await QuicListener.ListenAsync(new QuicListenerOptions
{
    ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
    ApplicationProtocols = new List<SslApplicationProtocol>
    {
        new SslApplicationProtocol("nightelf-sync/1.0"),
        new SslApplicationProtocol("nightelf-tx/1.0")
    },
    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions)
});
```

- 块同步和交易广播走 QUIC（UDP，弱网更友好，支持 NAT 穿透）
- RPC 调用走 gRPC（兼容现有 SDK 和工具链）
- GossipSub 式的广播替代当前的 gRPC stream 广播

### 2.8 共识层模块化

将 AEDPoS 从紧耦合重构为可插拔接口：

```csharp
public interface IConsensusEngine
{
    Task<Block> ProposeBlockAsync(ConsensusContext context, CancellationToken ct);
    Task<ValidationResult> ValidateBlockAsync(Block block, CancellationToken ct);
    Task OnBlockCommittedAsync(Block block);
    Task<IReadOnlyList<byte[]>> GetValidatorsAsync();
    Task<ChainHead> ForkChoiceAsync(IReadOnlyList<ChainHead> candidates);
}
```

VRF 随机数作为独立模块，不再与 AEDPoS 的 `SecretSharingService` 紧耦合。

### 2.9 HostSmartContractBridgeContext 拆分

将 417 行上帝类按职责拆分：

| 新类 | 职责 |
|------|------|
| `ContractStateContext` | 状态读写 |
| `ContractCallContext` | 合约间调用、内联交易 |
| `ContractCryptoContext` | 签名验证、哈希、VRF |
| `ContractIdentityContext` | 地址生成、虚拟地址 |
| `ContractExecutionContext` | 交易信息、块信息、时间戳（Facade，组合以上四个） |

### 2.10 事件通信改进

关键路径用 `System.Threading.Channels` 替代 `LocalEventBus`，提供显式、可追踪的控制流：

```csharp
// 块处理管道 —— 利用 .NET 10 的 Lock 类型替代 Monitor
private readonly Channel<Block> _acceptedBlocks = Channel.CreateBounded<Block>(
    new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true
    });

// 生产者（BlockExecutingService）
await _acceptedBlocks.Writer.WriteAsync(block, ct);

// 消费者（显式注册，可断点调试）
await foreach (var block in _acceptedBlocks.Reader.ReadAllAsync(ct))
{
    await _stateService.MergeIfNeededAsync(block);
    await _syncService.NotifyPeersAsync(block);
}
```

非关键路径（日志、监控指标）保留事件总线。

### 2.11 利用 .NET 10 / C# 13 的具体特性

#### NativeAOT 编译系统合约

系统合约（Genesis、MultiToken、Parliament 等）在链的整个生命周期内不变或极少变更。用 NativeAOT 预编译这些合约，消除 JIT 预热：

```csharp
// 系统合约可以做 AOT，用户合约保持 JIT
[assembly: SystemContract(AotCompile = true)]
public class TokenContract : CSharpSmartContract<TokenContractState>
{
    // AOT 编译后直接加载原生代码，零 JIT 开销
}
```

#### `System.Threading.Lock` 替代连接池锁

当前 `PooledRedisLite` 使用 `Monitor.Wait`/`Monitor.Pulse`，在 .NET 10 中用专用 `Lock` 类型替代：

```csharp
// 旧：Monitor.Wait(lockObject, RecheckPoolAfterMs)
// 新：.NET 9+ 的 Lock 类型，JIT 能做更好的优化
private readonly Lock _poolLock = new();

public TsavoriteSession GetSession()
{
    lock (_poolLock)  // 编译器识别 Lock 类型，生成优化代码
    {
        return _sessionPool.TryDequeue(out var session) ? session : CreateSession();
    }
}
```

#### `params ReadOnlySpan<T>` 减少批量操作分配

状态批量读写是热路径，C# 13 的 `params ReadOnlySpan<T>` 避免隐式数组分配：

```csharp
// 合约 SDK 的批量状态读取
public void GetStates(params ReadOnlySpan<string> keys)
{
    // 调用侧：contract.GetStates("balance", "allowance", "totalSupply")
    // 零数组分配，直接栈上传递
}
```

#### `Task.WhenEach` 收集并行执行结果

并行交易组的结果收集可以用 .NET 9 引入的 `Task.WhenEach` 简化：

```csharp
// 并行执行多个交易组
var groupTasks = transactionGroups.Select(g => ExecuteGroupAsync(g, ct));

await foreach (var completed in Task.WhenEach(groupTasks))
{
    var result = await completed;
    returnSetCollection.AddRange(result);
}
```

---

## 三、实施路线

### Phase 1: 存储层替换（预计 2-3 周）

**目标**：用 Tsavorite 嵌入式引擎替换 Redis，跑通现有功能。

- 新增 `NightElf.Database.Tsavorite` 项目，实现 `IKeyValueDatabase<T>`
- 按 Store 类型做 Tsavorite 实例隔离（Block / State / Index）
- 配置系统支持 Redis 和 Tsavorite 切换（过渡期保留 Redis 兼容）
- 验证：AElf 现有 108 个测试项目全部通过

**关键风险**：Tsavorite 的 `SpanByte` 语义与 Redis 的 `byte[]` 不完全等价，需要适配层处理生命周期。

### Phase 2: Async 链路修复（预计 1-2 周）

**目标**：消除假 async，建立同步快路径 + 异步慢路径模式。

- 删除 `#pragma warning disable 1998`
- 在 `NotModifiedCachedStateStore` 增加 `TryGetCached` 同步方法
- 评估 `AsyncHelper.RunSync()` 消除的可行性和范围
- 基准测试：对比修复前后的单交易延迟和吞吐量

### Phase 3: 合约执行改进（预计 2-4 周）

**目标**：Source Generator 替代反射，启用并行资源提取。

- 实现合约方法调度 Source Generator
- 实现资源声明 Source Generator
- 移除 `ResourceExtractionService` 中被注释的并行代码，用新方案替代
- 拆分 `HostSmartContractBridgeContext`
- 验证：并行交易执行在高冲突场景下的正确性

### Phase 4: 沙箱 + 状态合并（预计 2-3 周）

**目标**：AssemblyLoadContext 沙箱，Tsavorite checkpoint 简化状态合并。

- 实现 `ContractSandbox : AssemblyLoadContext`
- 用 Tsavorite 增量 checkpoint 替代逐 key 状态合并
- 分叉恢复测试：模拟多种分叉场景验证 checkpoint 回滚

### Phase 5: 网络与共识（预计 3-4 周）

**目标**：QUIC 传输层，共识接口模块化。

- 抽象 `IConsensusEngine` 接口，AEDPoS 作为第一个实现
- VRF 随机数模块独立
- QUIC 传输作为 gRPC 之外的可选项
- 关键路径 Channel 替代事件总线

---

## 四、从 AElf 迁移的兼容性

### 保留不变

- Protobuf 协议定义（core.proto 及所有合约 proto）
- 系统合约逻辑和 ABI
- 合约 SDK API（`CSharpSmartContract<T>` 基类）
- 交易/区块数据结构
- Store key 前缀体系（bb, bh, bs, tx, tr, vs...）

### 变更但兼容

- `IKeyValueDatabase<T>` 接口不变，新增 Tsavorite 实现
- `AElfModule` 模块化保留，内部实现重构
- gRPC P2P 协议保留，新增 QUIC 选项

### 破坏性变更

- `AsyncHelper.RunSync()` 的消除可能影响合约执行的 threading model
- `AssemblyLoadContext` 沙箱可能影响某些依赖 AppDomain 行为的合约
- 状态合并流程变更，已有节点数据需要一次性迁移

---

## 五、项目结构（初始规划）

```
night-elf/
├── docs/                              # 文档
│   ├── 001-refactoring-overview.md      # 本文档
│   └── ...
├── src/
│   ├── NightElf.Core/                 # 核心类型、DI、模块基类
│   ├── NightElf.Database/             # 数据库抽象层（保留接口）
│   ├── NightElf.Database.Tsavorite/   # Tsavorite 嵌入式实现
│   ├── NightElf.Kernel.Core/         # 区块链核心（Block, Tx, State）
│   ├── NightElf.Kernel.SmartContract/ # 合约执行引擎
│   ├── NightElf.Kernel.Consensus/    # 共识抽象 + AEDPoS v2
│   ├── NightElf.Kernel.Parallel/     # 并行执行（MVCC + Source Gen）
│   ├── NightElf.Runtime.CSharp/      # C# 合约运行时（沙箱）
│   ├── NightElf.Sdk.CSharp/         # 合约开发 SDK
│   ├── NightElf.Sdk.SourceGen/      # Source Generator（合约调度/资源声明）
│   ├── NightElf.OS.Network/         # P2P 网络（gRPC + QUIC）
│   ├── NightElf.CrossChain/         # 跨链
│   ├── NightElf.WebApp/             # API 层
│   └── NightElf.Launcher/           # 启动入口
├── contract/                         # 系统合约（从 AElf 迁移）
├── test/                             # 测试
├── protobuf/                         # Proto 定义（从 AElf 继承）
├── global.json                       # .NET 10 SDK 版本锁定
├── Directory.Build.props             # 全局构建属性 (net10.0 TFM)
└── NightElf.sln
```

### 5.1 构建配置

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>13</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
</Project>
```

```json
// global.json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```
