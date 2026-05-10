# 2026-05-10 Linux Operator Port Handoff

## Context

This work was done because `src/Simulator.ThreeD` targets `net10.0-windows` and requires `Microsoft.WindowsDesktop.App`, which is not available on the current Linux system. A new native OpenTK entry was added so Linux can at least open an operator-like RMUC 2026 view.

The implementation is **not feature-equivalent to the Windows/OpenGK client yet**. It is a working bridge for Linux visualization and rule checks, but it still misses a lot of UI interaction and authored scene behavior from the Windows version.

## Main Files Added Or Changed

- `src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj`
- `src/Simulator.LinuxOperator/Program.cs`
- `src/Simulator.Core/Gameplay/EnergyMechanismVisualLogic.cs`
- `src/Simulator.Runtime/Program.cs`
- `Simulator.sln`
- `README.md`
- `Directory.Build.props`

There are also related energy-mechanism changes in:

- `src/Simulator.Core/Gameplay/ArenaInteractionService.cs`
- `src/Simulator.Core/Gameplay/RuleSimulationService.cs`
- `src/Simulator.Core/Gameplay/SimulationCombatMath.cs`
- `src/Simulator.ThreeD/EnergyMechanismGeometry.cs`
- `src/Simulator.ThreeD/Simulator3dForm.GpuRenderer.cs`
- `src/Simulator.ThreeD/Simulator3dForm.Structures.cs`
- `src/Simulator.ThreeD/Simulator3dForm.FineTerrainActors.cs`
- `src/Simulator.ThreeD/Simulator3dForm.cs`

## What Works Now

- Linux can launch a native OpenTK operator window:

  ```bash
  dotnet run --project src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj -- --map rmuc2026
  ```

- The local login flow exists in the Linux window:
  - `1/2/3/4/7` selects hero, engineer, infantry, second infantry, sentry.
  - `Q/E` switches red/blue team.
  - left/right arrows switch spawn point.
  - `Enter` applies the seat, removes inactive controllable robots from `World.Entities`, and enters a 5-second countdown from first-person view.

- `--start-match` starts directly from the selected operator seat into the 5-second countdown:

  ```bash
  dotnet run --project src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj -- --map rmuc2026 --start-match
  ```

- Linux input capture is less invasive:
  - mouse is not grabbed by default;
  - `M` toggles mouse look capture;
  - `Alt`, focus loss, or opening the local panel releases capture and clears drive input.

- A minimal local `O/P` panel exists. It shows energy state and disables drive input while open. It is not a full Windows/OpenGK referee panel.

- The 3D terrain path loads the runtime terrain cache and suppresses annotated energy-mechanism components from the static terrain pass. This avoids drawing a static energy mechanism under the dynamic visual layer.

- Energy visual state resolution is centralized in `EnergyMechanismVisualLogic`:
  - pending arms;
  - hit rings;
  - activated-by-progress;
  - completed state.

- Large-energy test mode exists:

  ```bash
  dotnet run --project src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj -- --map rmuc2026 --large-energy-test --team blue
  ```

- The runtime energy visual check exists:

  ```bash
  dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- energy visual-check /tmp/simulator_energy_visual_check
  ```

## Verification Done

These commands passed:

```bash
dotnet build src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj --no-restore
dotnet build src/Simulator.Runtime/Simulator.Runtime.csproj --no-restore
dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- energy visual-check /tmp/simulator_energy_visual_check_final
```

Observed runtime logs:

- `--start-match` writes `phase=Countdown duration=5.000`.
- `--large-energy-test` writes repeated energy rotation samples with `status=ok`.
- terrain load reported suppression of annotated energy-mechanism static components.

## Known Problems / Not Yet Correct

The user is right that this still does **not** match the Windows version:

- The Linux UI is a simplified custom overlay, not a real port of `Simulator3dForm.OpenGkUi.cs`.
- The Windows local room/OpenGK room interaction model has not been migrated. The Linux login room is only a lightweight substitute.
- Full P/O referee panels are missing:
  - no proper page system;
  - no full local referee controls;
  - no energy-mechanism page with red/blue, small/large, waiting/activated/full, arm index selection, base armor, outpost, buff/fort controls.
- The scene still lacks many Windows-authored fine-terrain interactive actors:
  - base armor visual/collision animation;
  - outpost detailed behavior;
  - buff/facility 3D overlay parity;
  - full vehicle model/barrel parity from first-person view.
- Energy mechanism rendering is still an approximation driven by rule targets. It is closer than the previous static view, but it is not yet the Windows fine-terrain component renderer:
  - should eventually reuse annotated `灯臂`, `灯臂-中`, `灯臂-外`, `10环` component meshes directly;
  - current Linux rendering draws procedural rings/blades/chevrons instead of recoloring the original authored meshes.
- Large-energy test mode keeps the waiting state alive for visual inspection. This is useful for diagnostics but should not be mistaken for normal match behavior.
- The OpenTK Linux entry does not yet share the Windows frame pipeline, HUD composition, LAN room flow, or OpenGK visual style.

## Recommended Next Migration Steps

1. Port the OpenGK UI layer, not the simplified Linux overlay.
   - Source: `src/Simulator.ThreeD/Simulator3dForm.OpenGkUi.cs`
   - Target: a platform-neutral immediate UI renderer usable by both Windows and Linux.

2. Extract the match/room/referee flow out of `Simulator3dForm`.
   - Source: `Simulator3dForm.cs`, `Simulator3dForm.LanMultiplayer.cs`
   - Goal: Linux and Windows must use the same room model, seat source of truth, phase transitions, and panel commands.

3. Port fine-terrain actor rendering into a shared renderer.
   - Source:
     - `Simulator3dForm.FineTerrainActors.cs`
     - `Simulator3dForm.GpuRenderer.cs`
     - `FineTerrainEnergyMechanismVisualCache.cs`
   - Goal: Linux must draw/recolor actual annotated component meshes instead of procedural substitute geometry.

4. Replace the minimal Linux O/P panel with the real panel command model.
   - Energy controls must include team, small/large, waiting/activated/full, active arm count, and arm indexes.

5. Keep `EnergyMechanismVisualLogic` or an equivalent shared rule-to-visual-state adapter as the state source, but drive actual authored meshes with it.

## Useful Commands

Run Linux operator:

```bash
dotnet run --project src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj -- --map rmuc2026
```

Skip login and enter 5-second countdown:

```bash
dotnet run --project src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj -- --map rmuc2026 --start-match
```

Run large-energy diagnostic:

```bash
dotnet run --project src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj -- --map rmuc2026 --large-energy-test --team blue
```

Build and visual-check:

```bash
dotnet build src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj --no-restore
dotnet build src/Simulator.Runtime/Simulator.Runtime.csproj --no-restore
dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- energy visual-check /tmp/simulator_energy_visual_check
```

