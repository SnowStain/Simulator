# Linux / OpenTK Runtime README

This branch has removed the legacy Windows `Simulator.ThreeD` shell. The active
game entry is the OpenTK client in `src/Simulator.Linux`.

## Active Runtime Graph

```text
Simulator.Linux
  -> Simulator.OpenTk
  -> Simulator.Platform
  -> Simulator.Core
  -> Simulator.Assets
```

`Simulator.Linux` must stay free of WinForms, Windows-only target frameworks,
Windows OpenCV runtimes, and removed ThreeD shell code.

## Main Components

| Area | Owner |
| --- | --- |
| Linux executable and frame loop | `src/Simulator.Linux/LinuxOperatorWindow.cs` |
| Local runtime bootstrap and fixed-step world tick | `src/Simulator.Linux/LinuxOperatorRuntime.cs` |
| Window/render snapshot boundary | `src/Simulator.Linux/LinuxGameRenderSnapshot.cs` |
| OpenGL primitive drawing and vector text fallback | `src/Simulator.Linux/GlPrimitiveRenderer.cs` |
| OpenTK key/mouse mapping | `src/Simulator.OpenTk/Input` |
| OpenTK scene renderer contract | `src/Simulator.OpenTk/Rendering` |
| Shared runtime phases and O/P panel state | `src/Simulator.Platform/Runtime/SimulatorRuntimeStateMachine.cs` |
| OpenGK-style UI draw list and HUD painter | `src/Simulator.Platform/Ui` |
| Shared interaction render data | `src/Simulator.Platform/Rendering/InteractionSceneRenderList.cs` |
| Rules/world/entities/projectiles/buffs | `src/Simulator.Core` |
| Config/map/appearance loading | `src/Simulator.Assets` |

## Linux Local Game Loop

```text
LinuxOperatorWindow.OnUpdateFrame
  -> CaptureInputSnapshot()
  -> LinuxOperatorRuntime.ApplyInput()
  -> LinuxOperatorRuntime.Tick()
      -> SimulatorRuntimeStateMachine.Tick()
      -> ApplyLocalPlayerInput()
      -> RuleSimulationService.Run(world, facilities, 1/60, 1/60)
      -> Build LinuxGameRenderSnapshot

LinuxOperatorWindow.OnRenderFrame
  -> DrawLinuxGameFrame()
      -> draw map surface
      -> draw facilities
      -> draw shared interaction items
      -> draw projectiles/entities
      -> draw OpenGK runtime HUD / O panel
```

The Linux window consumes `LinuxGameRenderSnapshot`; it should not mutate
`SimulationWorldState` directly.

## UI And Interaction Rules

- Use `OpenGkUiDrawList` for HUD, panels, buttons, and text.
- Use `OpenGkUiButtonRegistry` for hit testing. Hidden panels must not leave
  active button regions behind.
- Use `SimulatorRuntimeStateMachine` for preparation, self-check, countdown,
  live, respawn, panel visibility, Enter skip, and mouse capture.
- Use `InteractionSceneRenderList` for buff/collision/energy/base interaction
  debug or runtime overlays.
- Add new OpenTK rendering work behind `Simulator.OpenTk/Rendering` contracts.

## Commands

Build portable solution:

```bash
dotnet build Simulator.sln -c Debug
```

Run diagnostics without a display server:

```bash
dotnet run --project src/Simulator.Linux/Simulator.Linux.csproj -- --diagnostics --map rmuc2026
```

Run the OpenTK client:

```bash
dotnet run --project src/Simulator.Linux/Simulator.Linux.csproj -- --map rmuc2026 --size 1440x900
```

Native Linux parity package:

```bash
bash scripts/linux/run-linux-parity.sh rmuc2026 1280x720 10
```

The parity script writes:

```text
artifacts/linux-parity/<timestamp>/
  parity-report.md
  logs/
  screenshots/
  publish/
```

## Validation Gates

```bash
bash scripts/linux/check-linux-portability.sh
bash scripts/linux/report-windows-blockers.sh
bash scripts/linux/smoke-linux-operator.sh rmuc2026 1280x720 6
```

Windows equivalent:

```powershell
dotnet build Simulator.sln -c Debug
dotnet run --project src\Simulator.Linux\Simulator.Linux.csproj -- --diagnostics --map rmuc2026
powershell -ExecutionPolicy Bypass -File scripts\linux\check-linux-portability.ps1
```

## Post-ThreeD Rule

Do not re-add `src/Simulator.ThreeD` or a WinForms game shell. If a missing
feature still exists only in old history, reimplement it through the current
cross-platform layers:

```text
state -> Simulator.Core / Simulator.Platform.Runtime
UI    -> Simulator.Platform.Ui
scene -> Simulator.Platform.Rendering / Simulator.OpenTk.Rendering
entry -> Simulator.Linux
```
