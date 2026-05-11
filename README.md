# ARTINX A-Soul Simulator

RMUC 2026 simulator runtime and tooling. The Windows `Simulator.ThreeD`
legacy shell has been removed from this branch; the runnable game entry is now
the OpenTK/Linux-first client.

## Quick Start

Build the portable solution:

```powershell
dotnet build Simulator.sln -c Debug
```

Run the OpenTK client:

```powershell
dotnet run --project src\Simulator.Linux\Simulator.Linux.csproj -- --map rmuc2026 --size 1440x900
```

On Linux, collect parity screenshots/logs/report:

```bash
bash scripts/linux/run-linux-parity.sh rmuc2026 1280x720 10
```

## Project Layout

- `src/Simulator.Linux`: OpenTK runtime entry, local game loop, HUD/panel draw calls.
- `src/Simulator.OpenTk`: OpenTK input and renderer contracts.
- `src/Simulator.Platform`: cross-platform input, runtime state machine, UI draw list, rendering contracts.
- `src/Simulator.Core`: rules, world state, entities, combat, projectiles, buffs, energy/base state.
- `src/Simulator.Assets`: config, map presets, appearance and asset loading.
- `src/Simulator.Editors`: shared editor/data tooling.
- `src/Simulator.LoadLargeTerrain`: GLB/map import and terrain helper tooling.
- `src/Simulator.Runtime`: CLI/runtime service staging area.
- `py_client/appearance_editor.py`, `run_viewer.py`: retained Python-side editor/viewer tools.
- `maps/rmuc2026`: RMUC 2026 authored map and component annotations.
- `规则/`: rule reference images and design material.
- `docs/`: architecture, algorithms, migration notes, and project history.

## Current Runtime Rules

- `Simulator.Linux` must not reference WinForms or removed `Simulator.ThreeD` code.
- New UI should emit `OpenGkUiDrawList` commands from platform-neutral state.
- New renderer work should use OpenTK-owned GL contexts and shared render data.
- Runtime logs are rooted at `logs/`.
- Do not commit `bin/`, `obj/`, launcher output, or local publish artifacts.

## Validation

```powershell
dotnet build Simulator.sln -c Debug
dotnet run --project src\Simulator.Linux\Simulator.Linux.csproj -- --diagnostics --map rmuc2026
powershell -ExecutionPolicy Bypass -File scripts\linux\check-linux-portability.ps1
```

Linux desktop parity:

```bash
bash scripts/linux/check-linux-portability.sh
bash scripts/linux/run-linux-parity.sh rmuc2026 1280x720 10
```

The current Linux feature-parity gap list is tracked in
`docs/linux-migration-gap-audit.md`.
