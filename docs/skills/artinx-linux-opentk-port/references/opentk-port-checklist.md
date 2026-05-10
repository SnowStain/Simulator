# OpenTK Port Checklist

Use this checklist when converting mixed WinForms/OpenTK/OpenGK code into a Linux-capable OpenTK runtime.

## Current State

- `Simulator.ThreeD` is `net10.0-windows` with `UseWindowsForms=true`.
- `Simulator.ThreeD` is now legacy Windows reference code for the Linux branch,
  not part of the default Linux solution.
- `Simulator.LoadLargeTerrain` and `Simulator.Decision` build as `net10.0`
  helper tools and stay in the Linux portability gate.
- `SimulatorOpenTkWindow` exists, and OpenTK input mapping is being moved into
  the shared `Simulator.OpenTk` adapter layer.
- OpenGK is a UI style/self-drawn layer, not a standalone platform.

## Preferred Target Shape

```text
Simulator.Core          rules, entities, physics, LAN data
Simulator.Assets        maps, appearances, resource loading
Simulator.Editors      shared editor data/document logic
Simulator.Runtime      platform-neutral runtime contracts and loop
Simulator.OpenTk       OpenTK-specific adapters that stay free of WinForms
Simulator.ThreeD       Windows compatibility shell
Simulator.OpenTkClient Linux/Windows OpenTK shell
LoadLargeTerrain       OpenTK/ImGui map editor, no WinForms dependency
```

## Migration Steps

1. Create platform-neutral input types:
   - `GameKey`
   - `GameMouseButton`
   - `GameInputSnapshot`
2. Convert WinForms and OpenTK input into these types.
   - `Simulator.OpenTk/Input/OpenTkGameInputMapper.cs` is the shared OpenTK mapper.
3. Move OpenGK UI button registration and hit-testing into `OpenGkUiButtonRegistry`.
4. Move simple OpenGK panel/button/text rendering into `OpenGkUiDrawList`.
   - `IOpenGkUiTextPainter<TSurface>` is the cross-platform text backend hook.
   - Windows ThreeD currently adapts it through `WinFormsOpenGkTextPainter`.
5. Move live-control decisions to platform-neutral input.
6. Replace `System.Windows.Forms.Timer` with a runtime tick loop interface.
   - `IFrameTicker` now exists in `Simulator.Platform/Runtime`.
   - Windows ThreeD currently adapts it through `WinFormsFrameTicker`.
7. Move cursor capture behind `ICursorCaptureService`.
8. Move clipboard behind `IClipboardService`.
9. Move file selection behind `IFileDialogService`; Linux can use ImGui path entry first.
10. Move HUD drawing away from `Graphics`/`TextRenderer`.
11. Keep GPU/OpenGL renderer as the shared scene renderer.
12. Keep optional media behind `IBackgroundVideoSource`; Windows uses OpenCV, Linux can use a null or Linux-native source.
13. Add or expand the OpenTK client only after runtime contracts are usable without WinForms.

## Package And Project Changes

- Keep `Simulator.Core`, `Simulator.Assets`, `Simulator.Editors`, `Simulator.Runtime` on `net10.0`.
- Do not reference WinForms from pure projects.
- Keep `OpenCvSharp4.runtime.win` conditional or behind an optional feature:
  - Windows: `OpenCvSharp4.runtime.win`
  - Linux: distro-matching OpenCvSharp runtime or system OpenCV binding.
- Do not hardcode `E:\...` paths. Resolve from `ProjectLayout.Discover()` or `AppContext.BaseDirectory`.

## Build Gates

Windows:

```powershell
dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -c Debug --no-restore
```

Portable graph:

```bash
dotnet build Simulator.Linux.sln -c Debug
bash scripts/linux/check-linux-portability.sh
```

The portability gate builds `Simulator.Linux.sln` and scans the Linux-callable
graph for Windows-only APIs. `Simulator.ThreeD` and
`Simulator.AutoAimCalibrationTool` must never be added to this solution. If a
future extraction needs code from `Simulator.ThreeD`, move the
platform-neutral contract first instead of adding a project reference.

Linux-portable core:

```bash
dotnet build src/Simulator.Core/Simulator.Core.csproj -c Debug
dotnet build src/Simulator.Assets/Simulator.Assets.csproj -c Debug
dotnet build src/Simulator.Editors/Simulator.Editors.csproj -c Debug
dotnet build src/Simulator.Runtime/Simulator.Runtime.csproj -c Debug
```

Future OpenTK client:

```bash
dotnet run --project src/Simulator.OpenTkClient/Simulator.OpenTkClient.csproj -- --start-match
```

Full repo Windows blocker report:

```bash
bash scripts/linux/report-windows-blockers.sh
```

Use the legacy-wide report only while planning the next extraction batch:

```bash
bash scripts/linux/report-windows-blockers.sh --include-legacy-windows
```

## Do Not Regress

- LAN low-latency authority flow.
- Scene interaction components and map annotation transforms.
- P/O panel behavior and no hidden hitboxes.
- First/third-person camera behavior.
- Energy mechanism visual rules and hit windows.
- Base armor animation and collision transform.
- Map editor component/composite copy and collision authoring.
