#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROTO_ROOT="$ROOT_DIR/protobuf"
OUTPUT_ROOT="$ROOT_DIR/artifacts/clients/chain-settlement"

mkdir -p "$OUTPUT_ROOT"

echo "Generating C# stubs via NightElf.WebApp build..."
dotnet build "$ROOT_DIR/src/NightElf.WebApp/NightElf.WebApp.csproj" >/dev/null

if command -v python3 >/dev/null 2>&1 && python3 -m grpc_tools.protoc --version >/dev/null 2>&1; then
  PYTHON_OUT="$OUTPUT_ROOT/python"
  mkdir -p "$PYTHON_OUT"

  echo "Generating Python stubs into $PYTHON_OUT..."
  python3 -m grpc_tools.protoc \
    -I"$PROTO_ROOT" \
    --python_out="$PYTHON_OUT" \
    --grpc_python_out="$PYTHON_OUT" \
    "$PROTO_ROOT/aelf/core.proto" \
    "$PROTO_ROOT/kernel.proto" \
    "$PROTO_ROOT/nightelf/webapi.proto" \
    "$PROTO_ROOT/nightelf/chain_settlement.proto"
else
  echo "Skipping Python stub generation: python grpc_tools is not available."
fi

if command -v protoc >/dev/null 2>&1 && command -v protoc-gen-go >/dev/null 2>&1 && command -v protoc-gen-go-grpc >/dev/null 2>&1; then
  GO_OUT="$OUTPUT_ROOT/go"
  mkdir -p "$GO_OUT"

  GO_MAPPINGS=(
    "--go_opt=Maelf/core.proto=github.com/eanzhao/night-elf/artifacts/clients/chain-settlement/go/aelf"
    "--go_opt=Mkernel.proto=github.com/eanzhao/night-elf/artifacts/clients/chain-settlement/go/aelf"
    "--go_opt=Mnightelf/webapi.proto=github.com/eanzhao/night-elf/artifacts/clients/chain-settlement/go/nightelf/webapp"
    "--go_opt=Mnightelf/chain_settlement.proto=github.com/eanzhao/night-elf/artifacts/clients/chain-settlement/go/nightelf/settlement"
    "--go-grpc_opt=Maelf/core.proto=github.com/eanzhao/night-elf/artifacts/clients/chain-settlement/go/aelf"
    "--go-grpc_opt=Mkernel.proto=github.com/eanzhao/night-elf/artifacts/clients/chain-settlement/go/aelf"
    "--go-grpc_opt=Mnightelf/webapi.proto=github.com/eanzhao/night-elf/artifacts/clients/chain-settlement/go/nightelf/webapp"
    "--go-grpc_opt=Mnightelf/chain_settlement.proto=github.com/eanzhao/night-elf/artifacts/clients/chain-settlement/go/nightelf/settlement"
  )

  echo "Generating Go stubs into $GO_OUT..."
  protoc \
    -I"$PROTO_ROOT" \
    --go_out="$GO_OUT" \
    --go-grpc_out="$GO_OUT" \
    "${GO_MAPPINGS[@]}" \
    "$PROTO_ROOT/aelf/core.proto" \
    "$PROTO_ROOT/kernel.proto" \
    "$PROTO_ROOT/nightelf/webapi.proto" \
    "$PROTO_ROOT/nightelf/chain_settlement.proto"
else
  echo "Skipping Go stub generation: protoc-gen-go or protoc-gen-go-grpc is not available."
fi

echo "Done. C# stubs are available through the NightElf.WebApp build output."
