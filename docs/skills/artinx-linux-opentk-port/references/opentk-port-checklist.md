# OpenTK Port Checklist

Use this checklist when converting mixed WinForms/OpenTK/OpenGK code into a Linux-capable OpenTK runtime.

## Current State

- `Simulator.ThreeD` is `net10.0-windows` with `UseWindowsForms=true`.
- `Simulator.LoadLargeTerrain` is also Windows-targeted mostly because of WinForms file dialogs.
- `SimulatorOpenTkWindow` exists, but still maps OpenTK keys/mouse to WinForms types.
- OpenGK is a UI style/self-drawn layer, not a standalone platform.

## Preferred Target Shape

```text
Simulator.Core          rules, entities, physics, LAN data
Simulator.Assets        maps, appearances, resource loading
Simulator.Editors      shared editor data/document logic
Simulator.Runtime      platform-neutral runtime contracts and loop
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
3. Move live-control decisions to platform-neutral input.
4. Replace `System.Windows.Forms.Timer` with a runtime tick loop interface.
5. Move cursor capture behind `ICursorCaptureService`.
6. Move clipboard behind `IClipboardService`.
7. Move file selection behind `IFileDialogService`; Linux can use ImGui path entry first.
8. Move HUD drawing away from `Graphics`/`TextRenderer`.
9. Keep GPU/OpenGL renderer as the shared scene renderer.
10. Add `Simulator.OpenTkClient` only after runtime contracts are usable without WinForms.

## Package And Project Changes

- Keep `Simulator.Core`, `Simulator.Assets`, `Simulator.Editors`, `Simulator.Runtime` on `net10.0`.
- Do not reference WinForms from pure projects.
- Replace `OpenCvSharp4.runtime.win` with conditional runtime packages or an optional feature:
  - Windows: `OpenCvSharp4.runtime.win`
  - Linux: distro-matching OpenCvSharp runtime or system OpenCV binding.
- Do not hardcode `E:\...` paths. Resolve from `ProjectLayout.Discover()` or `AppContext.BaseDirectory`.

## Build Gates

Windows:

```powershell
dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -c Debug --no-restore
```

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

## Do Not Regress

- LAN low-latency authority flow.
- Scene interaction components and map annotation transforms.
- P/O panel behavior and no hidden hitboxes.
- First/third-person camera behavior.
- Energy mechanism visual rules and hit windows.
- Base armor animation and collision transform.
- Map editor component/composite copy and collision authoring.
