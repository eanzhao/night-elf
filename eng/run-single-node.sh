#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

PORT="${NIGHTELF_PORT:-5005}"
DATA_DIR="${NIGHTELF_DATA:-$REPO_ROOT/artifacts/node/data}"
CHECKPOINT_DIR="${NIGHTELF_CHECKPOINTS:-$REPO_ROOT/artifacts/node/checkpoints}"

echo "NightElf single-node launcher"
echo "  API:         http://127.0.0.1:$PORT"
echo "  Health:      http://127.0.0.1:$PORT/health"
echo "  Data:        $DATA_DIR"
echo "  Checkpoints: $CHECKPOINT_DIR"
echo ""

dotnet run \
  --project "$REPO_ROOT/src/NightElf.Launcher/NightElf.Launcher.csproj" \
  -- \
  --NightElf:Launcher:ApiPort="$PORT" \
  --NightElf:Launcher:DataRootPath="$DATA_DIR" \
  --NightElf:Launcher:CheckpointRootPath="$CHECKPOINT_DIR" \
  "$@"
