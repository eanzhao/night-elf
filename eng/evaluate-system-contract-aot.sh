#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-osx-arm64}"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"

CONTRACT_PROJECT="$ROOT_DIR/contract/NightElf.Contracts.System.TokenAotPrototype/NightElf.Contracts.System.TokenAotPrototype.csproj"
HOST_PROJECT="$ROOT_DIR/tools/NightElf.Tools.SystemContractAotEvaluation/NightElf.Tools.SystemContractAotEvaluation.csproj"

OUTPUT_DIR="$ROOT_DIR/artifacts/nativeaot/$TIMESTAMP"
CONTRACT_BUILD_DIR="$OUTPUT_DIR/contract"
JIT_PUBLISH_DIR="$OUTPUT_DIR/jit"
AOT_PUBLISH_DIR="$OUTPUT_DIR/aot"
RESULTS_DIR="$OUTPUT_DIR/results"

mkdir -p "$CONTRACT_BUILD_DIR" "$JIT_PUBLISH_DIR" "$AOT_PUBLISH_DIR" "$RESULTS_DIR"

dotnet build "$CONTRACT_PROJECT" -c Release -o "$CONTRACT_BUILD_DIR"
CONTRACT_ASSEMBLY="$CONTRACT_BUILD_DIR/NightElf.Contracts.System.TokenAotPrototype.dll"

dotnet publish "$HOST_PROJECT" -c Release -r "$RID" --self-contained false -o "$JIT_PUBLISH_DIR"
dotnet publish "$HOST_PROJECT" -c Release -r "$RID" -p:PublishAot=true -o "$AOT_PUBLISH_DIR"

JIT_APP="$JIT_PUBLISH_DIR/NightElf.Tools.SystemContractAotEvaluation"
AOT_APP="$AOT_PUBLISH_DIR/NightElf.Tools.SystemContractAotEvaluation"

"$JIT_APP" \
  --contract-assembly "$CONTRACT_ASSEMBLY" \
  --output "$RESULTS_DIR/jit-report.json" \
  | tee "$RESULTS_DIR/jit.stdout"

"$AOT_APP" \
  --contract-assembly "$CONTRACT_ASSEMBLY" \
  --output "$RESULTS_DIR/aot-report.json" \
  | tee "$RESULTS_DIR/aot.stdout"

printf 'NativeAOT evaluation artifacts written to %s\n' "$OUTPUT_DIR"
