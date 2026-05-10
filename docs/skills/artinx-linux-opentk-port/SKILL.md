---
name: artinx-linux-opentk-port
description: Migrate ARTINX Simulator toward a Linux-capable OpenTK runtime while preserving RMUC scene interactions, LAN authority sync, room/referee flow, all P/O panels, HUDs, map-editor annotations, energy mechanism, buffs, collision, and existing Windows behavior. Use when changing Linux compatibility, removing WinForms dependencies, converging OpenTK/OpenGK/WinForms paths, or writing docs for a partner continuing the port.
---

# ARTINX Linux OpenTK Port

Use this skill inside the ARTINX simulator repo. Prefer incremental migration: keep Windows builds working while carving a platform-neutral runtime and OpenTK client.

## First Checks

1. Confirm the repo root contains `src/Simulator.ThreeD`, `src/Simulator.Core`, `src/Simulator.LoadLargeTerrain`, `maps/rmuc2026`, and `规则`.
2. Read the exact owning path before editing:
   - `Simulator3dForm.cs`: legacy WinForms host, app state, startup phases, cameras, input, core HUD.
   - `Simulator3dForm.OpenGkUi.cs`: OpenGK-styled self-drawn room/main/HUD UI.
   - `Simulator3dForm.GpuRenderer.cs`: OpenGL scene and GPU overlay rendering.
   - `SimulatorOpenTkWindow.cs`: current OpenTK bridge; still maps into WinForms keys/mouse.
   - `Simulator3dForm.LanMultiplayer.cs`: LAN room, roster, authoritative snapshots, referee flow.
   - `src/Simulator.LoadLargeTerrain`: OpenTK/ImGui map editor; it now builds as `net10.0`, so keep future UI additions free of WinForms.
   - `src/Simulator.Platform/Runtime`: cross-platform frame loop contracts.
   - `src/Simulator.Platform/Media`: cross-platform optional media/background-video contracts.
   - `src/Simulator.OpenTk`: shared OpenTK input/window adapters that are allowed to depend on OpenTK but not on WinForms.
3. Build Windows after any code change:

```powershell
dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -c Debug --no-restore
```

4. For Linux-facing code, build the Linux-only solution:

```powershell
dotnet build Simulator.Linux.sln -c Debug
```

## Migration Strategy

Do not rewrite the game in one pass. Move these boundaries in order:

1. **Platform-neutral contracts**: input enums, OpenGK UI button registry, OpenGK draw list, window services, clipboard, cursor capture, file dialog, frame timing.
2. **Runtime extraction**: match loop, LAN pump, scene interaction updates, and rule systems must not depend on WinForms.
3. **OpenTK client**: create or extend a `net10.0` OpenTK host that feeds platform-neutral input and calls shared runtime tick/render APIs.
4. **OpenGK UI renderer**: keep the visual design, but draw through OpenGL/Skia/ImGui-compatible primitives instead of WinForms `Graphics`/`TextRenderer`.
5. **Windows compatibility**: keep `Simulator.ThreeD` as a Windows shell until the OpenTK client reaches feature parity.

## Non-Negotiable Feature Parity

Before deleting or bypassing any WinForms path, preserve the full scene/UI contract. Read `references/scene-ui-contract.md` when touching room flow, P/O panels, energy mechanism, buffs, base armor, map editor, collision annotations, camera behavior, or HUD.

For Linux/OpenTK implementation tasks, read `references/opentk-port-checklist.md`.

## Convergence Rules

- Treat **OpenTK** as the future window/input/render host.
- Treat **OpenGK** as the project UI style and immediate-mode/self-drawn UI layer, not a separate platform.
- Treat **WinForms** as a compatibility shell to retire from the game runtime.
- Use `Simulator.Platform.Ui.OpenGkUiButtonRegistry` for OpenGK button hit-testing and cached button lists in both Windows and Linux shells.
- Use `Simulator.Platform.Ui.OpenGkUiDrawList` for simple OpenGK panel/button/text primitives before adding new shell-specific drawing.
- Use `Simulator.Platform.Ui.OpenGkRoomLayout` for room-page column/sidebar/action placement.
- New gameplay, LAN, rule, and scene interaction logic must not depend on `System.Windows.Forms`, `System.Drawing.Graphics`, `TextRenderer`, or `user32.dll`.
- Do not add new Windows-only code unless it is isolated behind a small interface.
- Keep Python appearance editor and Python map preview scripts intact unless explicitly asked otherwise.

## Validation

For Windows compatibility:

```powershell
dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -c Debug --no-restore
```

For Linux portability pressure:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/linux/check-linux-portability.ps1
```

For a Linux-callable source audit:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/linux/report-windows-blockers.ps1
```

For a legacy Windows shell audit before another extraction batch:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/linux/report-windows-blockers.ps1 -IncludeLegacyWindows
```

`Simulator.Linux.sln` is the Linux branch default build graph. It includes the
Linux operator plus cross-platform helper tools and excludes the legacy
`Simulator.ThreeD`/WinForms shell. Keep helper tools out of `Simulator.Linux`
runtime references unless a specific data contract has been extracted.

## Logs

Use root `logs/` first:

- LAN: `lan_uplink.log`, `lan_downlink.log`, `lan_uplink_detail.log`, `lan_downlink_detail.log`, `lan_validation.log`
- Startup: `match_startup.log`, `simulation_bootstrap.log`
- Rendering/perf: `frame_pump.log`, `render_perf.log`, `simulation_perf.log`
- Collision/motion: `terrain_movement_block.log`, `motion_perf.log`

When changing LAN runtime, keep host authoritative: clients send input, host sends authoritative snapshots, room/roster messages must not mutate live entities after match live.
