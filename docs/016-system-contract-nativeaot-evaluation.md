# 系统合约 NativeAOT 评估

## 目标

issue [#20](https://github.com/eanzhao/night-elf/issues/20) 要求验证一个系统合约的 NativeAOT 编译与加载链路，并给出是否进入主路线图的结论。

这次评估选了一个最小的 `MultiToken` 风格原型：
- 合约项目：[NightElf.Contracts.System.TokenAotPrototype](/Users/eanzhao/night-elf/contract/NightElf.Contracts.System.TokenAotPrototype/NightElf.Contracts.System.TokenAotPrototype.csproj)
- 评估 host：[NightElf.Tools.SystemContractAotEvaluation](/Users/eanzhao/night-elf/tools/NightElf.Tools.SystemContractAotEvaluation/NightElf.Tools.SystemContractAotEvaluation.csproj)
- 自动化脚本：[eng/evaluate-system-contract-aot.sh](/Users/eanzhao/night-elf/eng/evaluate-system-contract-aot.sh)

原型只保留两件事：
- `Mint` / `GetBalance` 通过 NightElf 现有的 source-generated dispatch 执行
- `DescribeResources` 继续输出读写 key，确认 AOT 不会破坏并行执行所依赖的资源声明

## 结论

2026 年 4 月 16 日在 `macOS 15.7 / osx-arm64 / .NET SDK 10.0.103` 上，这个结论已经可以明确：

1. 编译期静态链接的系统合约可以走通 NativeAOT。`TokenAotPrototypeContract` 直接编进宿主后，合约分发、状态型逻辑和资源声明都能正常运行。
2. JIT 宿主仍然可以通过现有沙箱加载托管 DLL；NativeAOT 宿主在同一路径上会抛 `PlatformNotSupportedException: Operation is not supported on this platform.`。
3. 现有的 `ContractSandbox` 不是 NativeAOT 系统合约的加载边界。它的核心入口 [ContractSandbox.LoadContractFromPath](/Users/eanzhao/night-elf/src/NightElf.Runtime.CSharp/ContractSandbox.cs:44) 明确依赖“从磁盘加载托管程序集 + collectible `AssemblyLoadContext`”。这和 AOT 产物作为内建原生代码的部署模型不是一条链路。
4. 因此，NativeAOT 更适合“节点内建系统合约”而不是“通用合约包”。如果 NightElf 未来要正式支持它，需要新增显式的 built-in registration / activation 路径，而不是继续复用通用 ALC 沙箱。

是否进入主路线图：`暂不进入`。

原因很直接：
- 性能收益主要体现在节点冷启动和首个调用预热
- 受益对象只覆盖少量内建系统合约
- 一旦进入主路线图，就必须长期维护两套发布模型：`built-in AOT` 和 `sandboxed IL`
- 当前主线重构收益更高的工作已经完成，AOT 还不在关键路径上

## 评估方法

脚本会做四步：

1. 普通 `Release` 构建托管合约 DLL
2. 普通 `publish` JIT host
3. `PublishAot=true` 发布 NativeAOT host
4. 运行两份 host，并把结果落到 `artifacts/nativeaot/<timestamp>/results/`

命令：

```bash
./eng/evaluate-system-contract-aot.sh
```

输出文件：
- `jit-report.json`
- `aot-report.json`
- `jit.stdout`
- `aot.stdout`

本次实测 artifact 在：
- `artifacts/nativeaot/20260416T041238Z/results/`

## 实测结果

### JIT host

- `StartupToMainMilliseconds`: `2.4492 ms`
- `DirectExecution.FirstDispatchMilliseconds`: `1.1910 ms`
- `DirectExecution.WarmAverageNanoseconds`: `148.94 ns`
- `Sandbox.Succeeded`: `true`

### NativeAOT host

- `StartupToMainMilliseconds`: `0.2952 ms`
- `DirectExecution.FirstDispatchMilliseconds`: `0.0513 ms`
- `DirectExecution.WarmAverageNanoseconds`: `31.37 ns`
- `Sandbox.Succeeded`: `false`
- `Sandbox.ExceptionType`: `System.PlatformNotSupportedException`

### 对比

- 冷启动到 `Main` 的时间从 `2.4492 ms` 降到 `0.2952 ms`，约 `8.3x` 改善。
- 首次合约 dispatch 从 `1.1910 ms` 降到 `0.0513 ms`，约 `23x` 改善。
- 热路径平均调用从 `148.94 ns` 降到 `31.37 ns`，约 `4.7x` 改善。
- JIT 宿主可以继续加载托管合约 DLL；AOT 宿主不能沿用这条通用沙箱链路。

这些数字来自单机单次评估，足够说明方向，但还不应该被当成正式性能基线。

## 原型边界

这次不是在评估“用户合约能否 AOT”，而是在评估“固定系统合约是否值得 AOT”。

因此原型刻意遵守了几条限制：
- 不依赖运行时反射做调度，继续使用 `NightElf.Sdk.SourceGen`
- 不依赖动态代码生成
- 不尝试把 AOT 产物重新塞回 `AssemblyLoadContext`
- 把“直接内建执行”和“沙箱加载托管 DLL”拆成两条独立结果，避免把部署模型混在一起

补充一点：AOT publish 过程中，`NightElf.Runtime.CSharp` 会对 [ContractSandbox.LoadContractFromPath](/Users/eanzhao/night-elf/src/NightElf.Runtime.CSharp/ContractSandbox.cs:44) 产生 trim warning。这不是这次原型的问题，反而正好说明它属于“动态装载托管程序集”的边界，不适合继续作为 NativeAOT 系统合约的主装载方式。

## 采用建议

如果将来要把这件事从 backlog 拉进主线，建议按下面的顺序做，而不是直接修改现有沙箱：

1. 先定义 `BuiltInSystemContractRegistry` 一类的显式注册层，区分 `BuiltIn` 和 `Sandboxed` 两种装载来源。
2. 只挑真正稳定、升级频率极低的系统合约先试点，比如 `Genesis` 或 `MultiToken` 的裁剪版本。
3. 把收益目标限定成“节点冷启动”和“首个系统交易延迟”，不要把它当作通用执行加速手段。
4. 在路线图层面明确：NativeAOT 不替代当前 ALC 沙箱，它只是系统合约的额外发布模式。
