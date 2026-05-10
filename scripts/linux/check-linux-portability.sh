#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

SOLUTION="Simulator.Linux.sln"
PROJECT="src/Simulator.Linux/Simulator.Linux.csproj"
TOOL_PROJECTS=(
  "src/Simulator.AutoAimCalibrationTool/Simulator.AutoAimCalibrationTool.csproj"
  "src/Simulator.LoadLargeTerrain/LoadLargeTerrain.csproj"
  "src/Simulator.Decision/Simulator.Decision.csproj"
)
SCAN_PATHS=(
  "$PROJECT"
  "src/Simulator.Linux"
  "src/Simulator.OpenTk"
  "src/Simulator.Platform"
  "src/Simulator.Core"
  "src/Simulator.Assets"
  "src/Simulator.AutoAimCalibrationTool"
  "src/Simulator.Runtime"
  "src/Simulator.LoadLargeTerrain"
  "src/Simulator.Decision"
)

echo "[linux-portability] checking project references"
solution_refs="$(dotnet sln "$SOLUTION" list)"
echo "$solution_refs"
if echo "$solution_refs" | grep -E "Simulator\.ThreeD"; then
  echo "[linux-portability] forbidden Windows shell project in Linux solution" >&2
  exit 1
fi

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
dotnet build "$SOLUTION" -c Debug

for tool_project in "${TOOL_PROJECTS[@]}"; do
  echo "[linux-portability] building cross-platform tool $tool_project"
  dotnet build "$tool_project" -c Debug
done

echo "[linux-portability] OK"
