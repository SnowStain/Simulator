#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

PROJECT="src/Simulator.Linux/Simulator.Linux.csproj"
SCAN_PATHS=(
  "$PROJECT"
  "src/Simulator.Linux"
  "src/Simulator.Platform"
  "src/Simulator.Core"
  "src/Simulator.Assets"
  "src/Simulator.Runtime"
)

echo "[linux-portability] checking project references"
refs="$(dotnet list "$PROJECT" reference)"
echo "$refs"
if echo "$refs" | grep -E "Simulator\.ThreeD|Simulator\.LoadLargeTerrain|Simulator\.Decision|Simulator\.AutoAimCalibrationTool"; then
  echo "[linux-portability] forbidden Windows/editor project reference in Linux graph" >&2
  exit 1
fi

echo "[linux-portability] checking package graph"
packages="$(dotnet list "$PROJECT" package --include-transitive)"
echo "$packages"
if echo "$packages" | grep -E "OpenCvSharp4\.runtime\.win|System\.Windows\.Forms|Microsoft\.Windows"; then
  echo "[linux-portability] forbidden Windows package in Linux graph" >&2
  exit 1
fi

echo "[linux-portability] scanning source for Windows-only APIs"
for path in "${SCAN_PATHS[@]}"; do
  if grep -RInE \
    --exclude-dir bin \
    --exclude-dir obj \
    "net[0-9.]+-windows|UseWindowsForms|System\.Windows\.Forms|OpenCvSharp4\.runtime\.win|DllImport\\(\"(user32|gdi32|kernel32)|Microsoft\.Win32\.Registry|WGL|OpenFileDialog|SaveFileDialog|FolderBrowserDialog|System\.Drawing\.Graphics|TextRenderer" \
    "$path"; then
    echo "[linux-portability] forbidden Windows-only API found under $path" >&2
    exit 1
  fi
done

echo "[linux-portability] building Linux operator"
dotnet build "$PROJECT" -c Debug --no-restore

echo "[linux-portability] OK"
