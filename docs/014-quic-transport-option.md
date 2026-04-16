# QUIC Transport Option

Issue: #18

## Goal

把 `src/NightElf.OS.Network` 从占位目录补成真实网络模块，并把“gRPC 负责 RPC 兼容、QUIC 负责块同步和交易广播”这件事落成可配置的代码边界，而不是继续停留在文档描述。

## Module Boundary

`NightElf.OS.Network` 现在提供三层职责：

- `NetworkTransportCoordinator`
  - 统一入口
  - 按场景把消息路由到 `Grpc` 或 `Quic`
- `GrpcCompatibilityTransport`
  - 保留 gRPC 兼容路径的职责位
  - 当前实现是轻量 compatibility transport，方便先验证路由和模块边界
- `QuicTransport`
  - 基于 `System.Net.Quic`
  - 负责 listener、入站连接、出站连接复用、ALPN 场景拆分
  - 当前宿主如果不支持 native QUIC，则自动退回 loopback compatibility fallback，保证路由和端到端场景仍可验证

## Scenario Split

当前定义了三个网络场景：

- `Rpc`
- `BlockSync`
- `TransactionBroadcast`

其中：

- `Rpc` 只能走 `Grpc`
- `BlockSync` 可选 `Grpc` 或 `Quic`
- `TransactionBroadcast` 可选 `Grpc` 或 `Quic`

这保证了 QUIC 是可选增强，而不是直接替换掉现有 gRPC 兼容路径。

## Configuration

配置节：

```text
NightElf:Network
```

关键项：

- `RpcTransport`
- `BlockSyncTransport`
- `TransactionBroadcastTransport`
- `Quic:ServerName`
- `Quic:HandshakeTimeout`
- `Quic:IdleTimeout`
- `Quic:KeepAliveInterval`
- `Quic:ListenBacklog`
- `Quic:BlockSyncApplicationProtocol`
- `Quic:TransactionBroadcastApplicationProtocol`

推荐启用 QUIC 的配置示例：

```json
{
  "NightElf": {
    "Network": {
      "RpcTransport": "Grpc",
      "BlockSyncTransport": "Quic",
      "TransactionBroadcastTransport": "Quic",
      "Quic": {
        "ServerName": "localhost",
        "HandshakeTimeout": "00:00:10",
        "IdleTimeout": "00:00:30",
        "KeepAliveInterval": "00:00:05",
        "ListenBacklog": 16,
        "BlockSyncApplicationProtocol": "nightelf-sync/1.0",
        "TransactionBroadcastApplicationProtocol": "nightelf-tx/1.0"
      }
    }
  }
}
```

## QUIC Design

当前 QUIC 数据面包含两块：

- `QuicTransport`
  - 启 listener
  - 接收入站连接
  - 按连接协商出的 ALPN 把消息映射到 `BlockSync` 或 `TransactionBroadcast`
  - 如果宿主缺少 native QUIC 支持，则退回同接口的 in-process fallback，但仍保持 `Quic` 路由语义
- `QuicConnectionManager`
  - 维护出站连接
  - 按 `host + port + alpn` 复用连接

这样 `BlockSync` 和 `TransactionBroadcast` 可以共享一套 QUIC 基础设施，但仍保留独立的协议名和场景边界。

## Verification

本次最小互通验证覆盖了两类路径：

- `Rpc` 在 QUIC 开启时仍走 `GrpcCompatibilityTransport`
- `BlockSync` 和 `TransactionBroadcast` 在配置为 `Quic` 时，优先通过 loopback QUIC listener 端到端发送；如果宿主不支持 native QUIC，则通过 QUIC compatibility fallback 保持同样的路由验证

这满足 issue 18 的目标：先把 QUIC 作为一个真实可选传输层接进来，同时不破坏 gRPC 兼容职责。
