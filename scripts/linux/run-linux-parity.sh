#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MAP_PRESET="${1:-rmuc2026}"
SIZE="${2:-1280x720}"
SECONDS_TO_RUN="${3:-10}"
STAMP="$(date +%Y%m%d_%H%M%S)"
OUT_DIR="${ROOT_DIR}/artifacts/linux-parity/${STAMP}"
LOG_DIR="${OUT_DIR}/logs"
SCREEN_DIR="${OUT_DIR}/screenshots"
REPORT="${OUT_DIR}/parity-report.md"
REQUIRE_DISPLAY="${REQUIRE_DISPLAY:-1}"

mkdir -p "$LOG_DIR" "$SCREEN_DIR"
cd "$ROOT_DIR"

run_and_log() {
  local name="$1"
  shift
  echo "[linux-parity] $name"
  {
    echo "\$ $*"
    "$@"
  } 2>&1 | tee "${LOG_DIR}/${name}.log"
}

append_report() {
  printf '%s\n' "$*" >> "$REPORT"
}

capture_screenshot() {
  local target_png="${SCREEN_DIR}/operator_${MAP_PRESET}_${SIZE}.png"
  local target_xwd="${SCREEN_DIR}/operator_${MAP_PRESET}_${SIZE}.xwd"

  if command -v gnome-screenshot >/dev/null 2>&1; then
    gnome-screenshot -f "$target_png" && echo "$target_png" && return 0
  fi

  if command -v spectacle >/dev/null 2>&1; then
    spectacle -b -n -o "$target_png" && echo "$target_png" && return 0
  fi

  if command -v import >/dev/null 2>&1; then
    import -window root "$target_png" && echo "$target_png" && return 0
  fi

  if command -v scrot >/dev/null 2>&1; then
    scrot "$target_png" && echo "$target_png" && return 0
  fi

  if command -v maim >/dev/null 2>&1; then
    maim "$target_png" && echo "$target_png" && return 0
  fi

  if command -v xwd >/dev/null 2>&1; then
    xwd -root -out "$target_xwd" && echo "$target_xwd" && return 0
  fi

  return 1
}

{
  echo "# Linux Native Parity Report"
  echo
  echo "- timestamp: ${STAMP}"
  echo "- map: ${MAP_PRESET}"
  echo "- size: ${SIZE}"
  echo "- run seconds: ${SECONDS_TO_RUN}"
  echo "- root: ${ROOT_DIR}"
  echo "- display: ${DISPLAY:-<none>}"
  echo "- wayland display: ${WAYLAND_DISPLAY:-<none>}"
  echo
} > "$REPORT"

{
  echo "## Environment"
  echo '```text'
  uname -a || true
  if command -v lsb_release >/dev/null 2>&1; then
    lsb_release -a || true
  fi
  dotnet --info || true
  if command -v glxinfo >/dev/null 2>&1; then
    glxinfo -B || true
  fi
  echo '```'
  echo
} >> "$REPORT"

if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" && "$REQUIRE_DISPLAY" == "1" ]]; then
  append_report "## Result"
  append_report
  append_report "- status: FAIL"
  append_report "- reason: no native Linux display session detected. Set REQUIRE_DISPLAY=0 only for headless smoke, not true parity."
  echo "[linux-parity] no DISPLAY/WAYLAND_DISPLAY; true native parity requires a desktop session" >&2
  exit 2
fi

run_and_log "01_git_status" git status --short
run_and_log "02_linux_portability" bash scripts/linux/check-linux-portability.sh
run_and_log "03_linux_solution_build" dotnet build ./Simulator.Linux.sln -c Debug
run_and_log "04_headless_diagnostics" dotnet run --project src/Simulator.Linux/Simulator.Linux.csproj -- --diagnostics --map "$MAP_PRESET"
run_and_log "05_publish_linux_x64" dotnet publish src/Simulator.Linux/Simulator.Linux.csproj -c Debug -r linux-x64 --self-contained false -o "${OUT_DIR}/publish"
run_and_log "06_published_diagnostics" "${OUT_DIR}/publish/Simulator.Linux" --diagnostics --map "$MAP_PRESET"

echo "[linux-parity] launching operator window for screenshot"
set +e
dotnet run --project src/Simulator.Linux/Simulator.Linux.csproj -- --map "$MAP_PRESET" --size "$SIZE" --exit-after "$SECONDS_TO_RUN" \
  > "${LOG_DIR}/07_operator_window.log" 2>&1 &
APP_PID=$!
set -e
sleep 3

SCREENSHOT_PATH="<not captured>"
if capture_screenshot > "${LOG_DIR}/08_screenshot.log" 2>&1; then
  SCREENSHOT_PATH="$(tail -n 1 "${LOG_DIR}/08_screenshot.log")"
else
  echo "[linux-parity] screenshot command unavailable or failed" | tee -a "${LOG_DIR}/08_screenshot.log"
fi

set +e
wait "$APP_PID"
APP_EXIT=$?
set -e
echo "$APP_EXIT" > "${LOG_DIR}/07_operator_window.exitcode"

if [[ -d logs ]]; then
  cp -a logs/. "${LOG_DIR}/runtime" || true
fi

append_report "## Automated Result"
append_report
if [[ "$APP_EXIT" == "0" ]]; then
  append_report "- status: PASS"
else
  append_report "- status: FAIL"
fi
append_report "- operator exit code: ${APP_EXIT}"
append_report "- screenshot: ${SCREENSHOT_PATH}"
append_report "- runtime logs: ${LOG_DIR}/runtime"
append_report

append_report "## Operation Flow"
append_report
append_report "1. Start native Linux desktop session."
append_report "2. Run: \`bash scripts/linux/run-linux-parity.sh ${MAP_PRESET} ${SIZE} ${SECONDS_TO_RUN}\`"
append_report "3. Confirm the window opens, map loads, HUD appears, and the process exits automatically."
append_report "4. For manual parity, rerun without \`--exit-after\` by using: \`dotnet run --project src/Simulator.Linux/Simulator.Linux.csproj -- --map ${MAP_PRESET} --size ${SIZE}\`"
append_report "5. Test: Enter skips preparation/self-check/countdown; O opens/closes local operator panel; Alt releases mouse; left click captures mouse; WASD moves only in live phase."
append_report

append_report "## Feature Checklist"
append_report
append_report "| Area | Expected parity behavior | Automated evidence | Manual result | Notes |"
append_report "| --- | --- | --- | --- | --- |"
append_report "| Build graph | Linux solution builds without Windows-only references | 02/03 logs | [ ] pass [ ] fail | |"
append_report "| Assets/config | config, map, rules, appearance data load | 04/06 logs | [ ] pass [ ] fail | |"
append_report "| OpenTK window | native GL window opens and exits cleanly | 07 log + screenshot | [ ] pass [ ] fail | |"
append_report "| Runtime phases | preparation -> self-check -> countdown -> live; Enter skip works in local mode | runtime log + manual | [ ] pass [ ] fail | |"
append_report "| Input | O panel, Alt mouse release, left-click capture, movement gated by phase | manual | [ ] pass [ ] fail | |"
append_report "| HUD/UI | status HUD, death overlay hook, local O panel render and button hit test | screenshot + manual | [ ] pass [ ] fail | |"
append_report "| Scene state | map facilities, entities, projectiles, interaction items render from real snapshot | screenshot | [ ] pass [ ] fail | |"
append_report "| Logs | logs are rooted at repo \`logs/\` and copied into artifact | runtime logs | [ ] pass [ ] fail | |"
append_report

append_report "## Artifact Index"
append_report
append_report "- report: ${REPORT}"
append_report "- logs: ${LOG_DIR}"
append_report "- screenshots: ${SCREEN_DIR}"
append_report "- publish: ${OUT_DIR}/publish"
append_report

if [[ "$APP_EXIT" != "0" ]]; then
  echo "[linux-parity] operator exited with ${APP_EXIT}; see ${REPORT}" >&2
  exit "$APP_EXIT"
fi

echo "[linux-parity] OK: ${REPORT}"
