# Linux Migration Gap Audit

Last checked: 2026-05-11

This audit records what is still missing after the Windows `Simulator.ThreeD`
shell was removed. The current code is portable at the project/API level, but
the Linux runtime is not yet full game parity.

## Verified Portable

- `Simulator.sln` no longer contains `Simulator.ThreeD`.
- `src/Simulator.Linux/Simulator.Linux.csproj` targets `net10.0` and references
  only `Simulator.Platform`, `Simulator.OpenTk`, `Simulator.Core`, and
  `Simulator.Assets`.
- `Simulator.Platform` owns the shared runtime phase state machine, OpenGK UI
  draw list, O/P panel layout skeleton, HUD overlay skeleton, and interaction
  render list.
- `Simulator.OpenTk` owns OpenTK input mapping and a renderer bridge contract.
- `Simulator.Linux` owns the current OpenTK window, local input capture, fixed
  step local runtime, and primitive rendering.
- Current source scans do not report Windows TFM, WinForms API, Win32/WGL API,
  Windows OpenCV runtime, or GDI drawing surface dependencies in active source.

## Blocking Parity Gaps

### Scene Rendering

Current state:
- `LinuxOperatorWindow.DrawLinuxGameFrame()` loads `terraincache.lz4` and draws
  the static map terrain through `TerrainCacheOpenTkSceneRenderer`.
- Facility/entity/projectile overlays still use primitive/minimap rendering.
- `OpenTkGpuSceneRenderer` only forwards to `IOpenTkGpuSceneRuntime`; it is a
  bridge contract, not the full scene renderer owner yet.

Needed:
- Dynamic authored composite/component transforms on top of the static terrain.
- Shared scene snapshots for static terrain, split map JSON resources,
  composite transforms, interaction component transforms, and dynamic structure
  transforms.
- One rendering path for Windows/Linux parity, with no WGL/WinForms host.

### Robot Visuals And Cameras

Current state:
- Linux shows robots as primitive boxes with health and yaw indicators.
- No Linux path renders appearance profiles, chassis variants, turret/gimbal
  meshes, barrel pose, first-person vehicle body, or third-person camera anchor.

Needed:
- Appearance profile mesh loading and transform application in OpenTK.
- Shared robot render state for chassis type, gimbal pitch/yaw, roll/pitch/body
  tilt, barrel pose, HP, buffs, death state, and selected control source.
- First-person and third-person camera modes using the same body/gimbal pose
  facts as simulation and autoaim.

### Movement, Terrain, And Collision

Current state:
- Linux local input directly updates entity `X/Y` in `LinuxOperatorRuntime`.
- `RuleSimulationService` runs rules/combat/interactions, but Linux movement
  does not yet use the old terrain traversal solver, authored collision volumes,
  chassis collision model, vertical-step-first acceptance, or BEPU/projectile
  obstacle path.

Needed:
- A shared movement service outside the deleted ThreeD shell.
- Terrain height/normal sampling, step rejection, slope/downhill smoothing,
  collision volumes, buff/collision cuboids, and robot body collider debug draw.
- Collision-following transforms for animated base armor panels.

### Interaction Components

Current state:
- `InteractionSceneRenderList` can represent labels/polygons/triangles.
- Linux only adds simple buff polygons, energy labels, and base armor labels.

Needed:
- Full interaction component runtime snapshots for energy mechanism disks,
  hit rings, pending markers, 11 chevrons, arm-middle and arm-outer light rules,
  large-energy progress, small-energy activation, base armor panels, outposts,
  fort capture progress, buff volumes, and collision debug volumes.
- Rendered state must come from authored component names/transforms, not
  procedurally invented geometry.

### HUD, Room, And O/P Panels

Current state:
- `OpenGkRuntimeHudPainter` draws a status overlay and respawn overlay skeleton.
- `OpenGkRefereePanelContent` draws a local overview skeleton and basic energy
  count buttons.
- `OpenGkRoomLayout` provides layout rectangles, but Linux does not render the
  full main menu, room lobby, local preparation selection, seat state, or full
  match HUD.

Needed:
- Full OpenGK UI parity for start menu, local room, preparation selection,
  referee self-check/countdown, in-match HUD, minimap, ammo/heat/buffer arcs,
  death grayscale/respawn flow, hit vignette, O/P pages, and local event log.
- Panel actions must mutate shared runtime/rule state, not just log clicks.

### Autoaim, Projectile, And Energy Rules UI

Current state:
- Core contains combat/autoaim/energy logic, but the Linux operator does not
  expose the complete visual targeting UI, F8/F3 style diagnostics, automatic
  trigger feedback, or energy mechanism aim visualization.

Needed:
- OpenTK rendering for target candidates, predicted aim point, projectile path,
  locked disk marker, energy pending marker, and hit confirmation.
- Projectile collision and auto-trigger debug must use the same runtime target
  data as actual hit detection.

### Editors

Current state:
- `Simulator.LoadLargeTerrain` and shared editor/data projects build portably.
- Python appearance/map preview tools are intentionally retained.
- Linux game runtime does not contain a native map/buff/collision/static mesh
  editor UI.

Needed:
- Decide whether editor parity belongs in `LoadLargeTerrain` ImGui/OpenTK or a
  separate Linux editor executable.
- Buff drawing editor, collision volume editor, composite copy, static mesh
  creation, color override, and JSON split/save flow need a single Linux-safe UI
  path.

### LAN

Current state:
- Linux branch intentionally removed the LAN runtime path per current product
  direction.

Needed only if product direction changes:
- Rebuild host/client protocol, room state, input uplink, authoritative snapshot
  downlink, prediction/reconciliation, and referee panel updates in a shared
  network layer outside any Windows shell.

## Low-Cost Cleanup Still Pending

- `start_3d_simulator.bat` is now a compatibility launcher for Linux/OpenTK but
  still has an old file name.
- `py_client/appearance_editor.py` preview launches the Linux runtime, but does
  not yet pass a real appearance preview contract.
- Historical docs still reference `Simulator.ThreeD`; keep them as archive, but
  new handoff docs should point to this audit and the Linux README first.
- `OpenTkGpuSceneRenderer` is currently a thin bridge; it should become the
  owning renderer for shared scene snapshots.

## Recommended Migration Order

1. Promote static terrain cache metadata into a shared scene snapshot.
2. Render authored composite/component transforms on top of the terrain cache.
3. Port robot appearance rendering and camera state.
4. Move terrain traversal/collision into a shared runtime service.
5. Port interaction component visuals: energy, base armor, outpost, buff,
   collision debug.
6. Expand OpenGK UI parity: main menu, room, preparation, HUD, O/P panels.
7. Wire O/P panel actions into shared rule/runtime state.
8. Add or finalize Linux editor UI scope.
9. Run native Linux parity script and archive screenshots/logs/checklist.

## Validation Commands

```powershell
dotnet build Simulator.sln -c Debug -p:UseSharedCompilation=false -nodeReuse:false
powershell -ExecutionPolicy Bypass -File scripts\linux\check-linux-portability.ps1
powershell -ExecutionPolicy Bypass -File scripts\linux\report-windows-blockers.ps1 -IncludeAllSource
dotnet run --project src\Simulator.Linux\Simulator.Linux.csproj -- --diagnostics --map rmuc2026
```

Native Linux desktop:

```bash
bash scripts/linux/run-linux-parity.sh rmuc2026 1280x720 10
```
