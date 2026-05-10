#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MAP_PRESET="${1:-rmuc2026}"

cd "$ROOT_DIR"

echo "[verify-linux] 1/5 portability gate"
bash scripts/linux/check-linux-portability.sh

echo "[verify-linux] 2/5 headless diagnostics"
dotnet run --project src/Simulator.Linux/Simulator.Linux.csproj -- --diagnostics --map "$MAP_PRESET"

echo "[verify-linux] 3/5 linux-x64 publish"
dotnet publish src/Simulator.Linux/Simulator.Linux.csproj -c Debug -r linux-x64 --self-contained false -o .linux-smoke/publish

echo "[verify-linux] 4/5 published diagnostics"
.linux-smoke/publish/Simulator.Linux --diagnostics --map "$MAP_PRESET"

echo "[verify-linux] 5/5 OpenGL window smoke"
bash scripts/linux/smoke-linux-operator.sh "$MAP_PRESET" 1280x720 6

echo "[verify-linux] OK"
