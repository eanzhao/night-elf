# Quick Start

## Prerequisites

- .NET SDK 10.0.100 or newer

## Run a Single Node

```bash
./eng/run-single-node.sh
```

The node starts with default settings:

| Setting | Default | Env Override |
|---------|---------|-------------|
| API port | 5005 | `NIGHTELF_PORT` |
| Data directory | `artifacts/node/data` | `NIGHTELF_DATA` |
| Checkpoint directory | `artifacts/node/checkpoints` | `NIGHTELF_CHECKPOINTS` |

Health check:

```bash
curl http://127.0.0.1:5005/health
```

## Docker

```bash
docker build -t nightelf .
docker run -p 5005:5005 -v nightelf-data:/data nightelf
```

## gRPC API

The node exposes two gRPC services on the API port (HTTP/2):

### NightElfNode

| RPC | Description |
|-----|-------------|
| `SubmitTransaction` | Submit a signed transaction |
| `GetTransactionResult` | Query transaction status by ID |
| `GetBlockByHeight` | Retrieve block at a given height |
| `GetChainStatus` | Current chain height and best block hash |

### ChainSettlement

| RPC | Description |
|-----|-------------|
| `SubmitTransaction` | Submit a settlement transaction |
| `QueryState` | Read contract state by key |
| `DeployContract` | Deploy a C# smart contract |
| `SubscribeEvents` | Stream chain events (server-streaming) |

Proto definitions: `protobuf/nightelf/webapi.proto` and `protobuf/nightelf/chain_settlement.proto`.

## Configuration

All settings can be overridden via command-line arguments, environment variables, or `appsettings.json`.

```bash
# Command-line
dotnet run --project src/NightElf.Launcher \
  -- --NightElf:Launcher:ApiPort=6000

# Environment variable (use __ as section separator)
export NightElf__Launcher__ApiPort=6000
```

### Key Configuration Sections

```json
{
  "NightElf": {
    "Launcher": {
      "NodeId": "node-local",
      "ApiPort": 5005,
      "DataRootPath": "artifacts/node/data",
      "CheckpointRootPath": "artifacts/node/checkpoints"
    },
    "Consensus": {
      "Engine": "Aedpos",
      "Aedpos": {
        "Validators": ["validator-a", "validator-b", "validator-c"],
        "BlockInterval": "00:00:04",
        "BlocksPerRound": 3
      }
    },
    "TransactionPool": {
      "Capacity": 4096,
      "DefaultBatchSize": 128
    }
  }
}
```

## Run Tests

```bash
dotnet test NightElf.slnx
```
