#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MAP_PRESET="${1:-rmuc2026}"
SIZE="${2:-1440x900}"

cd "$ROOT_DIR"
dotnet run --project src/Simulator.Linux/Simulator.Linux.csproj -- --map "$MAP_PRESET" --size "$SIZE"
