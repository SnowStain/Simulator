# Linux Migration Status

This branch has removed the Windows `Simulator.ThreeD` shell. The remaining
migration work is feature parity inside the OpenTK/Linux runtime, not Windows
dependency cleanup.

## Completed

- `Simulator.ThreeD` source directory and project removed.
- `Simulator.sln` and `Simulator.Linux.sln` contain only portable projects.
- Linux entry is `src/Simulator.Linux/Simulator.Linux.csproj`.
- Shared input model is `GameInputSnapshot`.
- Shared runtime phase state is `SimulatorRuntimeStateMachine`.
- Shared OpenGK-style UI primitives are in `Simulator.Platform/Ui`.
- Shared interaction render data is in `Simulator.Platform/Rendering`.
- Linux runtime loads config/map/rules, bootstraps `SimulationWorldState`, runs
  a 60 Hz fixed-step local world tick, and renders from `LinuxGameRenderSnapshot`.
- Portability scans show no Windows TFM, WinForms API, Win32/WGL API, Windows
  OpenCV runtime, or GDI drawing surface in current source.
- Native Linux parity script exists at `scripts/linux/run-linux-parity.sh`.

## Remaining Feature Migration

| Area | Current Linux State | Needed For 1:1 Parity |
| --- | --- | --- |
| Scene rendering | Linux now loads `terraincache.lz4` and renders the static terrain as an OpenTK 3D scene; facilities/entities still use primitive overlays/minimap | Dynamic authored composite transforms, robot meshes, interaction components, and camera parity |
| Robot visuals | Primitive entity boxes with health/yaw indicators | Appearance profile mesh renderer, chassis variants, first/third-person vehicle model, turret/gimbal/barrel transforms |
| Robot collision debug | Core movement state only; no full visualized chassis collider parity | Shared collision model/debug draw for body, wheel/leg parts, terrain contact, and collision volumes |
| Energy mechanism visuals | Interaction labels/render-list hooks only | Full rings, chevrons, arm progress, team-colored pending markers, hit rings, and auto-trigger feedback in OpenTK |
| Base/outpost visuals | Entity markers plus interaction labels | Authored base armor panel transforms, collision-following panel movement, outpost rotation/stop visuals |
| HUD parity | Runtime status HUD and local O panel skeleton | Full match HUD, heat/ammo/buffer arcs, minimap, hit/death overlays, buffs, base/outpost HP presentation |
| Room/menu flow | Linux opens directly into local operator runtime | Full OpenGK main menu, local room, preparation selection, O/P pages, and layout parity |
| Editor UI | Cross-platform data/tool projects build; Python tools retained | Native Linux editor UI for map/buff/collision/static mesh editing if required |
| LAN | Removed from Linux runtime per branch direction | Re-add only if product direction changes; keep host/client protocol outside Linux local-only entry |
| Screenshot parity | Scripted native Linux artifact generation exists | Run `run-linux-parity.sh` on real Linux desktop and compare screenshots/manual checklist |

## Verification Commands

```powershell
dotnet build Simulator.sln -c Debug -p:UseSharedCompilation=false -nodeReuse:false
powershell -ExecutionPolicy Bypass -File scripts\linux\check-linux-portability.ps1
powershell -ExecutionPolicy Bypass -File scripts\linux\report-windows-blockers.ps1 -IncludeAllSource
dotnet run --project src\Simulator.Linux\Simulator.Linux.csproj -- --diagnostics --map rmuc2026
```

Linux desktop parity:

```bash
bash scripts/linux/run-linux-parity.sh rmuc2026 1280x720 10
```

## Next Recommended Slice

The first static terrain scene slice is now in place. Continue by layering
authored runtime state onto that scene:

1. Promote terrain scene metadata into a shared scene snapshot instead of only
   reading the cache path from the Linux runtime.
2. Render authored composite/component transforms on top of the terrain cache.
3. Replace primitive robot markers with appearance-profile mesh rendering.
4. Keep Linux input/HUD/panel code unchanged while moving only scene ownership
   into `Simulator.OpenTk`.

For the detailed gap audit and migration order, see
[`linux-migration-gap-audit.md`](linux-migration-gap-audit.md).
