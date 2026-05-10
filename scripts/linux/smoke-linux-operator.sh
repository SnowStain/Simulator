#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MAP_PRESET="${1:-rmuc2026}"
SIZE="${2:-1280x720}"
SECONDS_TO_RUN="${3:-6}"

cd "$ROOT_DIR"

CMD=(dotnet run --project src/Simulator.Linux/Simulator.Linux.csproj -- --map "$MAP_PRESET" --size "$SIZE" --exit-after "$SECONDS_TO_RUN")

if command -v xvfb-run >/dev/null 2>&1; then
  xvfb-run -a "${CMD[@]}"
else
  "${CMD[@]}"
fi

if ! grep -q "load root=.*map=$MAP_PRESET" logs/linux_operator.log; then
  echo "[linux-smoke] linux_operator.log did not record a successful load for $MAP_PRESET" >&2
  exit 1
fi

echo "[linux-smoke] OK"
