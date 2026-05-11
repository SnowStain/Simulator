#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

INCLUDE_ALL_SOURCE=0
if [[ "${1:-}" == "--include-all-source" ]]; then
  INCLUDE_ALL_SOURCE=1
fi

if [[ "$INCLUDE_ALL_SOURCE" -eq 1 ]]; then
  echo "[windows-blockers] full source audit"
  SCAN_ROOTS=("src")
else
  echo "[windows-blockers] Linux-callable graph audit"
  echo "[windows-blockers] pass --include-all-source to scan every source project"
  SCAN_ROOTS=(
    "src/Simulator.Linux"
    "src/Simulator.OpenTk"
    "src/Simulator.Platform"
    "src/Simulator.Core"
    "src/Simulator.Assets"
    "src/Simulator.AutoAimCalibrationTool"
    "src/Simulator.Editors"
    "src/Simulator.LoadLargeTerrain"
    "src/Simulator.Decision"
    "src/Simulator.Runtime"
  )
fi

declare -a NAMES=(
  "Windows TFM / WinForms project"
  "WinForms API"
  "Win32 / WGL API"
  "Windows OpenCV runtime"
  "GDI drawing surface"
)

declare -a PATTERNS=(
  "net[0-9.]+-windows|UseWindowsForms"
  "System\\.Windows\\.Forms|TextRenderer|OpenFileDialog|SaveFileDialog|FolderBrowserDialog"
  "DllImport\\(\"(user32|gdi32|kernel32)|WGL|wgl[A-Za-z0-9_]+|GetHicon|SendMessage"
  "OpenCvSharp4\\.runtime\\.win"
  "System\\.Drawing\\.Graphics|Graphics\\.FromImage|Bitmap\\(|DrawString|DrawImage"
)

for i in "${!NAMES[@]}"; do
  echo
  echo "## ${NAMES[$i]}"
  matches="$(grep -RInE --include='*.cs' --include='*.csproj' --exclude-dir=bin --exclude-dir=obj "${PATTERNS[$i]}" "${SCAN_ROOTS[@]}" || true)"
  if [[ -z "$matches" ]]; then
    echo "none"
  else
    echo "$matches" | head -n 80
    total="$(echo "$matches" | wc -l | tr -d ' ')"
    if [[ "$total" -gt 80 ]]; then
      echo "... $((total - 80)) more"
    fi
  fi
done

echo
echo "[windows-blockers] note: System.Drawing Color/Rectangle primitives are allowed in shared contracts; Graphics/Bitmap/TextRenderer are not."
