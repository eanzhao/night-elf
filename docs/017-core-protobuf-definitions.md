# 核心 Protobuf 定义引入

## 目标

issue [#23](https://github.com/eanzhao/night-elf/issues/23) 要求把 NightElf 跑链所需的最小核心 proto 从 AElf 协议里按需引入，并集成到 `NightElf.Kernel.Core`。

这次只引入 Phase 1 必需的消息：
- `Transaction`
- `BlockHeader`
- `BlockBody`
- `Block`
- `Address`
- `Hash`
- `MerklePath`

## 目录与生成方式

proto 文件放在：
- [protobuf/aelf/core.proto](/Users/eanzhao/night-elf/protobuf/aelf/core.proto)
- [protobuf/kernel.proto](/Users/eanzhao/night-elf/protobuf/kernel.proto)

`NightElf.Kernel.Core` 现在通过 `Grpc.Tools` 在构建时生成 C# 消息类型，生成 namespace 统一落在 `NightElf.Kernel.Core.Protobuf`，避免和现有内核模型直接重名。

## 兼容策略

NightElf 当前已有的内核状态和共识代码还在使用轻量字符串模型，例如：
- [BlockReference](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/BlockReference.cs)

这次没有强行把整条执行链切到 protobuf message，而是先加了一层兼容扩展：
- [KernelCoreProtobufCompatibilityExtensions.cs](/Users/eanzhao/night-elf/src/NightElf.Kernel.Core/KernelCoreProtobufCompatibilityExtensions.cs)

当前提供的桥接能力：
- `string <-> Hash` 十六进制转换
- `BlockHeader + Hash -> BlockReference`
- `Block + Hash -> BlockReference`

这保证了：
- 新的链数据模型已经有正式 protobuf schema
- 现有 `BlockReference`、chain-state 和测试夹具不需要在同一个 issue 里被整体重写

## 与 AElf 的对齐边界

这次是“最小可运行子集”，不是完整镜像 AElf 的 `91` 个 proto。

保留的部分：
- 字段号
- 消息名
- `package aelf`
- 关键 wire shape

刻意裁掉的部分：
- `TransactionResult`
- `Chain`
- `BlockStateSet`
- 各类合约/网络/跨链 proto

一个需要明确说明的点是 `Transaction`：这里保留的是 AElf 实际 schema，也就是 `ref_block_prefix`，而不是抽象成完整 `Hash ref_block_hash`。这样做是为了先保证 wire compatibility，再决定 NightElf 上层是否要额外包一层更语义化的模型。
