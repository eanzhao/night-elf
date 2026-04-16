# Launcher 启动路径

## 目标

issue [#24](https://github.com/eanzhao/night-elf/issues/24) 把 `NightElf.Launcher` 从空占位补成了可运行节点入口。

当前 `dotnet run --project src/NightElf.Launcher` 会完成：
- DI 容器初始化
- 模块按依赖顺序加载并输出日志
- Tsavorite `Block / State / Index` 三类 store 初始化
- Genesis block 检测与首次写入
- `ChannelBlockProcessingPipeline` 启动
- gRPC health API 启动
- 持续出块循环
- Ctrl+C 时等待当前区块完成，再 flush 最终 checkpoint 并退出

## 主要组件

- 入口：[Program.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/Program.cs)
- 选项：[LauncherOptions.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/LauncherOptions.cs)
- 模块图：[LauncherModuleCatalog.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/LauncherModuleCatalog.cs)
- 存储初始化：[NightElfNodeStorage.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/NightElfNodeStorage.cs)
- Genesis 逻辑：[GenesisBlockService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/GenesisBlockService.cs)
- 出块循环：[NodeRuntimeHostedService.cs](/Users/eanzhao/night-elf/src/NightElf.Launcher/NodeRuntimeHostedService.cs)

## 当前启动语义

### 模块加载

Launcher 现在会解析并按依赖顺序输出模块加载日志。默认根模块包括：
- `NightElfDatabaseHostingModule`
- `NightElfRuntimeCSharpModule`
- `NightElfOSNetworkModule`
- `NightElfKernelConsensusModule`

### 存储

这次没有走过渡态的 provider 切换，而是直接把 Launcher 绑定到 Tsavorite：
- `BlockStoreDbContext`
- `ChainStateDbContext`
- `ChainIndexDbContext`

区块数据通过 [BlockRepository.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/BlockRepository.cs) 存在 block store，height -> hash 映射存在 index store，状态和 checkpoint 继续走 `ChainStateStore`。

### Genesis

Launcher 会先检测 `chain:best` 是否存在：
- 存在：认为链已经初始化，跳过 genesis 生成
- 不存在：用当前共识配置生成一个最小 genesis block，并写入 block/index/state/checkpoint

当前 genesis 语义：
- validator 集来自 launcher/consensus 配置，支持通过独立 JSON genesis 文件提供
- `AgentSession` 等系统合约会生成真实 deployment transaction，并把结构化部署记录写入 state
- genesis block hash 由链配置和部署清单共同决定，是链身份的根

### gRPC API

Launcher 现在会启动一个真实的 HTTP/2 gRPC host，但只暴露 health service，用来把“节点宿主”和“API 监听生命周期”真正接起来。

`NightElfNode` 业务 API 仍留给 [#27](https://github.com/eanzhao/night-elf/issues/27)。

## 运行

```bash
dotnet run --project src/NightElf.Launcher
```

默认端口：
- gRPC API: `5005`
- P2P grpc compatibility transport: `6800`
- P2P quic transport: `6801`

默认数据目录：
- `artifacts/node/data`
- `artifacts/node/checkpoints`
