# ARTINX Simulator Linux / OpenTK Port README

This document is for the Linux migration partner. The goal is to converge the simulator from mixed WinForms/OpenTK/OpenGK code into an OpenTK-first runtime while preserving the complete RMUC 2026 scene interaction and UI behavior.

## Current Reality

The project currently mixes three layers:

- **WinForms**: legacy main window, timers, key/mouse events, clipboard, text rendering, many editor forms.
- **OpenTK**: OpenGL windowing and 3D rendering foundation; `SimulatorOpenTkWindow` already exists but still maps back to WinForms input types.
- **OpenGK**: project-specific visual/UI style for main menu, room, HUD, and panels. Treat it as a self-drawn UI layer, not as a separate platform.

The Linux target should use:

```text
OpenTK window and input
OpenGL/GPU scene rendering
OpenGK-style self-drawn UI/HUD/panels
Platform-neutral game runtime
```

WinForms should become only a Windows compatibility shell until it can be retired.

## Linux Screenshot Diagnosis

The current Linux operator screenshot shows that the 3D scene is being restored, but most of the simulator contract is still missing from the OpenTK path:

- The scene renderer is active, but the OpenGK room/HUD/panel layer is not fully called.
- The top UC HUD, referee/operator panel, startup overlays, hit/death overlays, buff toasts, energy mechanism overlays, and full minimap behavior are either absent or replaced by temporary drawing.
- Input appears to be going through a thin OpenTK-to-WinForms bridge instead of a platform-neutral input pipeline, so menu capture, third-person camera capture, O/P panel hotkeys, and live control can diverge.
- The Linux client must not rebuild these features as a new UI. It should call or extract the same component pipelines listed below.

Treat this README as the implementation map for closing that gap.

## Canonical Runtime Frame

Every Windows, Linux, local, and LAN client should converge to this frame order:

```text
1. Poll window events.
2. Convert platform input to GameInputSnapshot.
3. Pump LAN transport and enqueue room/referee/input/snapshot events.
4. Dispatch global UI hotkeys first: Esc policy, Alt mouse release, P/O panel toggle.
5. Dispatch visible panel hit-testing only if that panel is actually open.
6. Apply live movement/fire/camera input only if startup phase allows movement.
7. Tick startup/self-check/countdown or live world simulation.
8. Apply authoritative LAN snapshot on clients, or publish authoritative snapshot on host.
9. Render GPU scene.
10. Render scene-space overlays: energy mechanism, base armor, collision/buff debug, labels.
11. Render HUD overlays: top HUD, first-person UI, minimap, status bars.
12. Render modal overlays: startup, death grayscale/respawn, P/O panel, room/menu.
13. Flush logs and frame telemetry.
```

The important rule is that UI visibility and input hit-testing are the same state. A hidden P/O panel must not keep clickable regions alive.

## Must-Preserve Feature Contract

Do not remove or stub these systems during migration:

- OpenGK main menu and room layout.
- Local room and LAN room seat flow.
- LAN host/referee/player/spectator roles.
- P panel for LAN referee and O panel for local mode.
- Preparation, referee self-check, and countdown overlays.
- Top HUD, unit cards, base/outpost health, minimap, ammo/heat/supercap UI.
- First-person and third-person camera behavior.
- Death grayscale/respawn overlay and hit red-gradient feedback.
- Energy mechanism full visual and hit rules.
- Fort capture progress and buffs.
- Base armor opening animation and collision movement.
- Buff volumes and edited collision volumes from map editor.
- Map editor component/composite selection, copy, static primitives, collision and buff authoring.
- LAN authority sync: client sends input, host sends authoritative snapshots.

The detailed checklist is in:

```text
docs/skills/artinx-linux-opentk-port/references/scene-ui-contract.md
```

## Repository Orientation

```text
src/Simulator.Core
  Rules, world entities, physics-facing state, damage, projectiles, buffs, energy mechanisms.

src/Simulator.Assets
  Map, appearance, and config loading.

src/Simulator.Editors
  Shared editor documents/import/export helpers.

src/Simulator.Runtime
  Platform-neutral runtime landing zone. Put new input/window/runtime contracts here.

src/Simulator.ThreeD
  Current Windows game shell and most existing UI/render/game integration.

src/Simulator.LoadLargeTerrain
  OpenTK/ImGui terrain/map editor. Currently still Windows-targeted because of WinForms file dialogs.
```

## Component Invocation Map

Use this as the main index when porting a visible feature. The Linux shell should either call these paths directly through a temporary adapter or extract the platform-neutral part into `Simulator.Runtime`.

| Component | Current call/UI entry | Behavior/state entry | Linux port rule |
| --- | --- | --- | --- |
| OpenTK shell | `SimulatorOpenTkWindow` | `ISimulatorOpenTkRuntime` bridge: `ExternalAdvanceFrame`, `ExternalRender`, `ExternalApplyInput`, `ShouldCaptureMouseExternally` | Temporary bridge only. OpenTK already feeds `GameInputSnapshot`; replace the Form implementation with a pure runtime when ready. |
| Platform input | `src/Simulator.Runtime/Input/GameInputTypes.cs` | `GameKey`, `GameMouseButton`, `GamePointerState`, `GameInputSnapshot` | This is the convergence point. OpenTK and WinForms both translate into this model. |
| Main menu | `DrawOpenGkMainMenu`, `DrawOpenGkUnifiedHomeMenu`, `DrawOpenGkMainActions` in `Simulator3dForm.OpenGkUi.cs` | menu state fields in `Simulator3dForm.cs`, click actions via `_uiButtons` and `ResolveUiAction` | Do not use a separate Linux menu. Port the OpenGK draw primitives. |
| Local room | `OpenLocalRoom` in `Simulator3dForm.LanMultiplayer.cs`, room UI via `DrawOpenGkLanRoomScreen/Core` | local seats, preparation selection, `StartLanRoomMatchFromRoom` | Local mode uses the same room layout; old vehicle-selection lobby is retired. |
| LAN room | `DrawOpenGkLanRoomScreen`, `DrawOpenGkTeamRoomColumn`, `DrawOpenGkRefereeAndSettingsPanel` | `CreateLanRoomRoster`, `HandleLanRoster`, `HandleLanSeatClaim`, `PublishLanLobbySelection` | Clients must see host referee and only active seats. Roster changes must freeze once live starts. |
| LAN transport | `LanMultiplayerSession`, `LanProtocol`, `LanTrafficTelemetry` | `PublishLanInput`, `HandleLanPlayerInput`, `CreateLanAuthoritativeMatchSnapshot`, `HandleLanAuthoritativeSnapshot` | Commercial-style rule: clients send input, host sends authoritative snapshots; clients do not publish live snapshots. |
| Startup/preparation | `DrawMatchStartupOverlay`, `DrawMatchStartupPreparationPanel`, `DrawMatchStartupRobotConfigPanel` | `BeginMatchStartupSequence`, `UpdateMatchStartupState`, `StartMatch` | During preparation/self-check/countdown only movement keys are blocked; P/O and non-movement UI keys still work. |
| Top UC HUD | `DrawOpenGkUcTopHudV2`, `DrawOpenGkUcTeamPanelV2`, `DrawOpenGkUcUnitCardV2` | `ResolveOpenGkUcHudLayoutV2`, entity/team health state | Linux must render base/outpost/current unit HP with the same layer ordering. |
| Legacy/in-match HUD | `DrawInMatchOverlay`, `DrawInMatchOverlayUiLayer`, `DrawHud`, `DrawPlayerStatusPanelV2` | selected entity, match clock, weapon state, buffs | Use only as fallback where OpenGK HUD has not been extracted. |
| First-person HUD | `DrawGpuCrosshairPrimitive`, `DrawGpuCrosshairStatusProgressRingsPrimitive`, `DrawBufferEnergyBarNeo` | selected entity weapon heat/ammo/buffer/supercap fields | Ammo, heat, and buffer arcs belong here; camera shake and barrel shake come from live entity motion. |
| Minimap | in-match overlay/HUD drawing in `Simulator3dForm.cs` | active entity list after room seat filtering | Must hide inactive robots by removing them from world, not by visual filtering. |
| P/O referee panel | `DrawLanRefereePanel`, `DrawLanRefereeEnergyPage`, `DrawLanRefereeEnergyCard`, `DrawLanRefereeEnergyArmButtons` | `HandleLanRefereePanelAction`, local O-panel action mapping | LAN uses P; local mode uses O. Energy control is a separate page with team, large/small, arm count, and exact arm mask choices. |
| Panel click routing | `_uiButtons`, `UiButton`, `ResolveUiAction`, panel-specific action handlers | panel open/closed state, mouse capture state | Clear hit regions every frame before drawing; add buttons only for visible controls. |
| Energy mechanism visuals | `Simulator3dForm.FineTerrainActors.cs`: `DrawFineTerrainEnergyTrianglesGpu`, `DrawFineTerrainEnergyInteractionFeedback`, `DrawFineTerrainEnergyRingOverlayTriangles`, `DrawFineTerrainEnergyChevronStroke/Fill` | `FineTerrainEnergyMechanismVisualCache`, `EnergyMechanismGeometry`, team energy state | Use model interaction components from map JSON. Do not offset black interactive pieces away from authored model positions. |
| Energy mechanism rules | `SimulationCombatMath.GetEnergyMechanismTargets`, `TerrainMotionService`, `AutoAimSolverService` | energy state, active arm mask, hit validation, auto-fire key | Hits only count during pending/active windows. Activated rings stay lit; fully activated mechanism ignores further hits. |
| Base/outpost models | `Simulator3dForm.Structures.cs`, `DrawBaseExpandedArmor`, `DrawOutpostArmorPlate` | base/outpost health, fortress capture, O/P actions | Base armor panels are authored interaction components. Move visual and collision together along each panel custom frame. |
| Fine terrain actors | `Simulator3dForm.FineTerrainActors.cs` | runtime reference scene and interaction unit cache | Linux must reuse this path for authored interaction components, not coarse fallback geometry. |
| Buff volumes | `DrawGpuBuffDebuffDebugRegions`, `CollectBuffProgressEntries`, `DrawBuffProgressOverlay` | `FacilityRegion`, `TerrainMotionService` buff application | Buff geometry is 3D volume data, not 2D polygons. The buff editor must write the same structure consumed in-match. |
| Edited collision | `FineTerrainAnnotationDocument`, `ReloadTerrainCollisionAnnotations`, `TerrainMotionService.AddEditedCollisionShapes` | editor-authored boxes/cylinders/prisms | Collision dimensions, height, YPR, and local map coordinates must map exactly into runtime collision shapes. |
| Robot appearance | `Simulator3dForm.AppearanceModel.cs`, `EntityCollisionModel`, `AppearanceProfileCatalog` | runtime profile per entity and chassis variant | Do not use one big fallback box for robot collision. Visual and collision variants must switch together. |
| Movement/collision | `TerrainMotionService` | terrain footprint, edited collision, vehicle step limits, AI navigation | Vertical passability must be checked before horizontal acceptance. Mecanum step limit is stricter than special ramps. |
| Projectile physics | `Simulator3dHost.ResolveProjectileObstacle`, `ProjectileObstacleResolver`, `BepuProjectileObstacleBackend` | projectile travel, obstacle hit, reflection/suppression backend | Keep backend selectable. Use host authority in LAN; clients render received projectile snapshots. |
| Camera/live control | `Simulator3dForm.LiveControl.cs`, `ShouldCaptureMouseForCurrentView`, `ShouldCaptureMouseExternally` | selected entity pose, yaw/pitch/roll, first/third person mode | Third person remains mouse-look; only Alt releases cursor. Vehicle roll affects gimbal/camera presentation. |
| Editors | `Simulator.LoadLargeTerrain`, `TerrainEditorForm`, `AppearanceEditorForm`, Python appearance/map preview scripts | JSON map annotations, appearance profiles, composite/static mesh authoring | Keep Python appearance editor and map preview scripts. Linux editor work should remove WinForms dialogs, not tool functionality. |

## Screen UI Invocation

All screen UI should be invoked from one frame-level UI pass after the GPU scene pass. Do not let individual gameplay systems draw their own final screen UI directly from update/tick code.

Current Windows ownership:

| Screen state | Draw entry | Hit/action entry | Notes |
| --- | --- | --- | --- |
| Home/main menu | `DrawOpenGkMainMenu`, `DrawOpenGkUnifiedHomeMenu`, `DrawOpenGkMainHeader`, `DrawOpenGkMainActions` | `TryExecuteOpenGkHomeAction`, `TryResolveOpenGkMainMenuAction`, `ResolveUiAction`, `ExecuteUiAction` | Linux should call this before showing room UI. |
| Create/join modal | `DrawOpenGkLanCreateModal`, `DrawOpenGkLanJoinModal`, `DrawOpenGkLanInput`, `DrawOpenGkCheckbox` | `lan_field:*`, `lan_toggle_private`, `lan_room_connect`, `lan_room_close` | Text input must go through platform-neutral text input, not WinForms events. |
| LAN/local room | `DrawOpenGkLanRoomScreen`, `DrawOpenGkLanRoomScreenCore`, `DrawOpenGkTeamRoomColumn`, `DrawOpenGkTeamSlot` | `local_room_slot:*`, `lan_select_seat:*`, `lan_join_team:*`, `lan_toggle_ready`, `lan_room_start_match`, `local_room_start_match` | Room should select occupancy only; robot type/spawn moves to preparation. |
| Referee/settings room side panel | `DrawOpenGkRefereeAndSettingsPanel`, `DrawOpenGkRoleLine`, `DrawOpenGkSettingLine`, `DrawOpenGkAdjustableSettingLine` | `lan_role_select:*`, `lan_room_setting:*` | Includes map, rule mode, projectile physics backend, damage, level, and coin settings. |
| Match startup/preparation | `DrawMatchStartupOverlay`, `DrawMatchStartupPreparationPanel`, `DrawMatchStartupRobotConfigPanel`, `DrawPreparationChip` | `startup_team:*`, `startup_focus:*`, `startup_spawn:*`, `startup_infantry_mode:*`, `startup_hero_mode:*`, `startup_prepare_confirm` | Movement keys blocked; config keys and P/O still work. |
| Top HUD | `DrawOpenGkUcTopHudV2`, `DrawOpenGkUcCenterPanelV2`, `DrawOpenGkUcTeamPanelV2`, `DrawOpenGkUcUnitCardV2` | `match_select:*` only when selection is allowed | Structure HP labels must be above bars and unit cards only show active robots. |
| First-person combat HUD | `DrawGpuCrosshairPrimitive`, `DrawGpuCrosshairStatusProgressRingsPrimitive`, `DrawGpuAutoAimGuidancePrimitive`, `DrawBufferEnergyBarNeo` | no button hit-test | Shows ammo ring, heat ring, buffer/supercap arc, auto-aim/energy guidance. |
| Player status/buff UI | `DrawPlayerStatusPanelV2`, `DrawPlayerStatusPanelModern`, `DrawBuffProgressOverlay`, `DrawHudBuffIconRowNeo` | no button hit-test | Read from selected entity and `CollectBuffProgressEntries`. |
| Death/hit overlays | `DrawDeadSelectedEntityScreenTint`, `DrawCriticalStateOverlay`, hit feedback paths in `Simulator3dForm.cs` | no button hit-test except respawn/menu where visible | Death overlay must be topmost and grayscale/black-white background while dead. |
| P/O panel | `DrawLanRefereePanel`, `DrawPSettingsPanel`, `DrawPPanelFrame`, `DrawLanRefereeEnergyPage` | `HandleLanRefereePanelAction`, P-setting actions, `p_close`, `p_logout` | Only visible panel owns hit boxes. Local O panel reuses referee functions but localizes logs/actions. |
| Debug overlays | `DrawF3DebugPoseOverlay`, `DrawTerrainCollisionDebugOverlay`, `DrawGpuBuffDebuffDebugRegions`, `DrawFineTerrainInMatchEditorOverlay` | editor/debug hotkeys only | These must not change physics; they visualize runtime truth. |

Call order inside the Linux OpenTK frame should be:

```text
DrawGpuMatch / scene
  -> scene-space authored interaction overlays
  -> screen-space HUD
  -> modal/menu/panel UI
  -> debug overlays if enabled
```

The UI design source of truth is the OpenGK look in `Simulator3dForm.OpenGkUi.cs` plus the in-match panel/HUD code in `Simulator3dForm.cs` and `Simulator3dForm.LiveControl.cs`. If Linux needs a new renderer backend, port the drawing primitives, not the layout/behavior.

## Start Screen Feature Matrix

The start screen is not just a decorative home page. It owns these functional flows:

| Feature | UI owner | Behavior owner | Linux parity requirement |
| --- | --- | --- | --- |
| Local game | `DrawOpenGkUnifiedHomeMenu`, action `home_local_game`, mode actions `home_local_mode:uc`, `home_local_mode:1v1` | `OpenLocalRoom` | Opens OpenGK room layout, not old vehicle lobby. |
| LAN discovery | `DrawOpenGkHomeRoomList`, `home_toggle_lan_rooms`, `lan_discovery_refresh`, `lan_discovered_room:*` | `LanRoomDiscoveryService` | List should update without blocking scene render. |
| Direct connect | `home_direct_connect`, `DrawOpenGkLanJoinModal` | LAN connect flow | Fields: host, port, room/player identity. |
| Create LAN room | `home_create_game`, `DrawOpenGkCreateGameModePicker`, `DrawOpenGkLanCreateModal` | LAN host session creation | Creates UC or 1v1 room and registers host referee seat. |
| Join selected room | `home_join_selected_room` | LAN client session join | Populates join modal from discovered room. |
| Editor entry | `menu_open_terrain_editor`, `menu_open_appearance_editor`, `menu_open_rule_editor`, `menu_open_lighting_editor` | editor launch services | Linux should route to OpenTK/ImGui editors or a supported external tool. |
| Lighting toggle | `menu_toggle_lighting` | renderer/light state | Must not invalidate or reload the map. |
| Exit | `menu_exit` | app shell | OpenTK closes window cleanly and flushes logs. |

Start screen action buttons are written into `_uiButtons` by the draw functions. The Linux port should preserve this immediate-mode UI pattern until a platform-neutral UI registry is extracted.

## Call Recipes

These recipes are the intended call chains for the Linux port. If a feature is missing, compare the Linux path against the relevant recipe before adding new code.

### Boot To Scene

```text
SimulatorOpenTkWindow.OnLoad / startup
  -> create shared runtime/session options
  -> load map preset and appearance catalogs
  -> prepare GPU terrain/resources once
  -> ExternalPrepareInitialPresentation or extracted equivalent
```

Do not reload map/robot assets when a room seat, spawn point, or robot config changes. Those actions should move or reconfigure existing entities.

### Main Menu And Room

```text
OpenTK frame
  -> render scene/backdrop
  -> DrawOpenGkMainMenu or DrawOpenGkLanRoomScreen
  -> _uiButtons stores only visible menu/room controls
  -> mouse click
  -> ResolveUiAction
  -> room action handler
  -> OpenLocalRoom / LAN room claim / settings mutation
```

The old 5v5 vehicle-selection lobby should not be called by Linux. Room seats are only for player/referee/spectator/AI occupancy; robot type and spawn are chosen during the in-game preparation phase.

### Start Match

```text
Room start button
  -> StartLanRoomMatchFromRoom
  -> StartMatch
  -> BeginMatchStartupSequence(resetWorld: true)
  -> Simulator3dHost applies active seats and map annotations
  -> UpdateMatchStartupState per frame
  -> live match when preparation/self-check/countdown complete
```

Local single-player can skip self-check/countdown with Enter. LAN must follow host authority.

### P/O Panel

```text
Key P in LAN referee role, or key O in local mode
  -> toggle panel visible state
  -> release mouse cursor
  -> DrawLanRefereePanel
  -> optional DrawLanRefereeEnergyPage
  -> click ResolveUiAction
  -> HandleLanRefereePanelAction
  -> mutate referee/game state
```

When the panel closes:

```text
clear visible panel buttons
restore camera capture rules
do not allow stale hidden buttons to receive clicks
```

### LAN Live Sync

```text
Client live frame
  -> collect GameInputSnapshot
  -> build compact PlayerControlState/LanPlayerInputFrame
  -> PublishLanInput
  -> receive HandleLanAuthoritativeSnapshot
  -> smooth own pose correction and apply remote entities

Host live frame
  -> HandleLanPlayerInput for each client
  -> simulate world
  -> CreateLanAuthoritativeMatchSnapshot
  -> publish snapshot
```

Never send live client world snapshots as authority. Never let live room/roster messages rebuild entities.

### Energy Mechanism

```text
Map load
  -> FineTerrainEnergyMechanismVisualCache.TryLoad
  -> parse authored interaction units by name

Frame render
  -> DrawFineTerrainEnergyTrianglesGpu
  -> DrawFineTerrainEnergyInteractionFeedback
  -> DrawFineTerrainEnergyRingOverlayTriangles
  -> DrawFineTerrainEnergyChevronStroke/Fill when pending

Hit/auto-aim
  -> SimulationCombatMath.GetEnergyMechanismTargets
  -> AutoAimSolverService predicts target on rotor plane
  -> TerrainMotionService validates projectile hit window
  -> team energy state updates
```

The pending marker and chevrons are visual overlays attached to authored units. They should not create duplicate fake geometry away from the model.

### Base Armor

```text
Map load
  -> find authored base outer panel interaction units
  -> cache closed local transforms

Open reason changes
  -> health below 2000, enemy fortress occupation, or O/P command
  -> animate each panel for 2 seconds
  -> apply local -Z/-Y translation and local X rotation
  -> update visual transform and collision transform together
```

If a panel jitters or returns closed, some later render/cache path is reapplying the closed transform. Fix that owner instead of adding another movement layer.

### Edited Collision And Buffs

```text
Editor save
  -> FineTerrainAnnotationDocument / map split JSON
  -> ReloadTerrainCollisionAnnotations
  -> TerrainMotionService.AddEditedCollisionShapes
  -> DrawGpuBuffDebuffDebugRegions or collision debug draw uses same data
```

The runtime must consume editor height, radius/width/depth, XYZ, and YPR exactly. Buff regions and collision regions are 3D volumes.

### Editor Tools

```text
Top toolbar
  -> choose feature: composite copy, static primitive, collision, buff editor
  -> expand the selected tool panel
  -> edit shared document model
  -> save split JSON
```

The buff drawing editor should be a dedicated editor surface like terrain drawing, but it should not own composite/model authoring. Python appearance editor and Python map preview scripts remain supported tools.

## UI Layer Ownership

The Linux port should keep a single visible UI stack. The current owner files are:

```text
Simulator3dForm.OpenGkUi.cs
  Main menu, home room list, LAN/local room, OpenGK room columns,
  top UC HUD V2, unit cards, structure bars, room setting controls.

Simulator3dForm.cs
  Match startup overlay, P/O panel frame, LAN referee panel pages,
  generic HUD fallback, hit/death overlays, action hit-testing.

Simulator3dForm.LiveControl.cs
  Player status, hero lob overlays, key guide, buff progress and toasts.

Simulator3dForm.GpuRenderer.cs
  GPU terrain, in-match scene render, GPU HUD primitives, OpenGK backdrop,
  crosshair, ring arcs, supercap/buffer primitives.

Simulator3dForm.FineTerrainActors.cs
  Fine terrain interaction component rendering, energy mechanism authored
  visuals, base outer panel authored component movement, debug geometry.

Simulator3dForm.Structures.cs
  Coarse/fallback base, outpost, energy mechanism models. Use this only when
  authored map components are unavailable.
```

If a Linux screenshot is missing a UI element, first find the Windows owner above and port that draw/action path. Avoid creating another one-off OpenTK widget for the same feature.

## Behavior Ownership

These are the state machines and gameplay owners behind the UI:

```text
Simulator3dForm.LanMultiplayer.cs
  Room role claims, roster, local/LAN room start, player input transport,
  authoritative snapshots, validation/digest logs, referee reports.

LanMultiplayerSession.cs
  UDP/TCP session transport, event records, protocol message payloads.

Simulator3dHost.cs
  World reset/build, map preset application, active seat filtering,
  LAN preparation application, terrain annotation reload.

TerrainMotionService.cs
  Movement, terrain acceptance, edited collision, buffs, AI navigation,
  auto-aim application, projectile/armor contact.

AutoAimSolverService.cs
  Ballistic/aim prediction, energy mechanism pivot projection and filtering.

SimulationCombatMath
  Combat target enumeration and hit-rule helpers used by projectiles/auto-aim.
```

For Linux parity, the UI should read state from these owners; it should not keep a separate copy of match, energy, base armor, or LAN state.

## OpenTK Integration Contract

The target contract is:

```text
OpenTK KeyboardState/MouseState
  -> GameInputSnapshot
  -> shared runtime input dispatcher
  -> panel actions or live control state
  -> world tick
```

Do this in small steps:

1. Keep `SimulatorOpenTkWindow` for window creation and OpenGL context ownership.
2. Convert OpenTK key/mouse state into `GameInputSnapshot`.
3. Feed snapshots through `Simulator3dForm.ExternalApplyInput`.
4. Keep the current WinForms event path as compatibility while extracting handlers.
5. Move the snapshot dispatcher from `Simulator3dForm` into `Simulator.Runtime`.
6. Make WinForms and OpenTK both call the runtime dispatcher directly.
7. Remove direct WinForms key/mouse references from the Linux path.

Current status:

```text
SimulatorOpenTkWindow / WinForms native events
  -> GameInputSnapshotAccumulator in Simulator.Runtime
  -> GameInputSnapshot
  -> ISimulatorOpenTkRuntime.ExternalApplyInput
  -> current compatibility implementation: Simulator3dForm.ExternalApplyInput
  -> existing key/mouse behavior handlers through compatibility mapping
```

This means OpenTK no longer maps directly to `System.Windows.Forms.Keys` or `MouseButtons`, and WinForms native events no longer call the old behavior handlers directly. Both shells first produce the same snapshot type. `SimulatorOpenTkWindow` talks to `ISimulatorOpenTkRuntime`, so the OpenTK shell is no longer hard-wired to `Simulator3dForm`; the current Form-based runtime is only the Windows compatibility implementation. The remaining WinForms mapping is isolated behind the shared snapshot consumer so the next extraction can move behavior out of `Simulator3dForm` without touching OpenTK again.

OpenTK convergence checkpoint:

| Layer | Current owner | OpenTK target | Status |
| --- | --- | --- | --- |
| Window/context | `SimulatorOpenTkWindow` | OpenTK `GameWindow` | Active |
| Input collection | OpenTK + WinForms native events | `GameInputSnapshot` | Shared |
| Runtime bridge | `ISimulatorOpenTkRuntime` | Pure `Simulator.Runtime` implementation | Interface ready, compatibility Form still used |
| Scene GPU render | `Simulator3dForm.GpuRenderer.cs` | OpenTK-owned GL context with shared renderer services | Partially active through borrowed-context call |
| OpenGK UI style | `Graphics` / `TextRenderer` layout code | GL/Skia/ImGui primitives with same actions | Not extracted yet |
| Windows shell | `Simulator3dForm : Form` | Compatibility only | Keep until OpenTK runtime reaches parity |

Mouse capture rules:

- First person: captured unless a panel/menu owns the mouse.
- Third person: captured unless `Alt` is held.
- P/O panel visible: cursor released and panel hit-testing enabled.
- P/O panel hidden: no panel hit-testing and cursor follows camera rules.
- Preparation/self-check/countdown: block movement/fire keys only; still allow P/O, Enter skip in local mode, camera/menu keys where intended.

## Rendering Contract

Scene rendering and UI rendering must stay separated:

```text
Scene pass
  Terrain, map components, robots, projectiles, base/outpost/energy authored meshes.

Scene overlay pass
  3D debug lines, authored collision/buff volumes, energy pending markers,
  base panel movement debug, labels that belong in world space.

Screen HUD pass
  OpenGK top HUD, first-person crosshair/ammo/heat/buffer, minimap,
  selected unit status, hit/death overlays.

Modal UI pass
  Room, startup overlay, P/O referee pages, pause/death/respawn panels.
```

Linux should not lower UI resolution to gain performance. If the panel is slow, optimize cached text/primitive batching and stop rebuilding scene resources during panel draw.

## Interaction Components And Composites

The map contains static components, composites, and interaction units. For Linux parity, authored data must flow through one path:

```text
map split JSON / GLB
  -> runtime reference scene
  -> fine terrain visual caches
  -> GPU terrain/component renderer
  -> interaction visual overlays
  -> collision/buff/gameplay state
```

Definitions:

- **Component**: one map part or imported mesh unit with model-space geometry, transform, material/color override, role, and optional collision/buff metadata.
- **Composite**: a named group of components and interaction units, such as a base, outpost, energy mechanism, or terrain feature.
- **Interaction unit**: authored sub-component used by gameplay. Examples: energy rings, energy light arms, base outer armor panels, collision-only volumes, buff volumes.

Rules:

- JSON color overrides still apply to models where the editor says they should.
- Editor selection/debug color must not permanently overwrite the model material.
- Interaction units are rendered at authored component positions. Do not add radial offsets or fake center shifts in render code.
- If an interaction component moves, its collision and gameplay query transform moves with it.
- Coarse fallback models in `Simulator3dForm.Structures.cs` only exist for missing authored components. They should be suppressed when authored fine terrain components are available.

Runtime owner files:

```text
FineTerrainEnergyMechanismVisualCache.cs
FineTerrainOutpostVisualCache.cs
Simulator3dForm.FineTerrainActors.cs
Simulator3dForm.GpuRenderer.cs
FineTerrainAnnotationDocument.cs
TerrainMotionService.cs
```

## Energy Mechanism Port Notes

Energy visuals are authored from map interaction components. The naming convention is part of the runtime contract:

```text
红方能量机关 / 蓝方能量机关
  -x-灯臂
  -x-灯臂-中
  -x-灯臂-外
  -x-10环 / ring score components
```

Rules to preserve:

- Pending hit marker is anchored to the 10-ring center and lies on the disk plane.
- Pending marker color follows team: red `(255,0,0)`, blue `(0,0,255)`.
- `灯臂-中` shows eleven `>` chevrons only while that arm is pending; otherwise it shows progress/color state.
- Small energy: an activated arm/ring remains fully lit.
- Large energy: `灯臂` and `灯臂-中` light by progress ratio; `灯臂-外` lights only after all five arms are fully activated.
- Hits outside the pending activation window have no effect.
- Auto-fire must avoid repeated hits on the same pending target and must fire once when the predicted shot window is valid.

Detailed drawing contract:

- Ring center: use the authored 10-ring component center or `LocalCenterModel` equivalent. Do not use triangle centroid fallback when the authored center exists.
- Disk plane: all pending markers, rings, and chevrons must be coplanar with the target disk face.
- Pending marker outer diameter: scale from the authored 10-ring; outer circumscribed circle is the 10-ring circle size.
- Pending marker rings: three concentric rings; their radii scale with the 10-ring instead of hardcoded screen pixels.
- Pending marker spokes: four trapezoid spokes. The long outer edge touches the outer circle; spoke length and width follow the current RMUC rule image proportions.
- Chevrons: exactly eleven `>` arrows on `灯臂-中`, rolling outward along the authored strip. No extra boxes, dots, or decorative shapes.
- Light color: use pure team colors for active light surfaces, red `(255,0,0)` or blue `(0,0,255)`. Inactive black surfaces should be opaque.
- Small energy: pending arm shows pending marker plus chevrons; activated arm and its hit ring remain lit; fully finished mechanism no longer accepts hits.
- Large energy: when not pending, `灯臂` and `灯臂-中` show proportional radial progress; `灯臂-外` stays black until all five arms finish.
- O/P panel activation puts the mechanism into the same pending/activating state as operator activation. It must not directly mark the whole mechanism complete unless the button explicitly says so.

Gameplay contract:

```text
pending arm selection
  -> draw pending marker on that arm only
  -> accept projectile hits only during pending/active window
  -> validate hit against authored ring/disk collision
  -> light hit ring permanently
  -> advance small/large mechanism state
```

Auto-aim contract:

```text
visual target observation
  -> project observed ring/disk point onto the energy mechanism rotor plane
  -> estimate angular velocity on that vertical rotor plane
  -> EKF/filter state update
  -> predict future ring position using projectile flight time
  -> smooth yaw/pitch output
  -> auto trigger once when predicted hit window is valid
```

The fixed offset bug usually means the observation point, 10-ring center, or rotor plane basis is mixing model-space and world-space axes. Check `AutoAimSolverService.TryResolveEnergyMechanismPivotM` and the target plate returned by `SimulationCombatMath.GetEnergyMechanismTargets`.

Relevant owners:

```text
FineTerrainEnergyMechanismVisualCache.cs
Simulator3dForm.FineTerrainActors.cs
EnergyMechanismGeometry.cs
AutoAimSolverService.cs
TerrainMotionService.cs
```

## Base Armor Port Notes

Base outer panels are not generated by code. They are authored interaction components in the map composite list. The opening animation should:

- Find the three panel interaction components for the correct base/team.
- Move each panel in its own custom local coordinate frame.
- Current requested transform: local `-Z` 22 cm, local `-Y` 7 cm, local `X` rotation `-7` degrees.
- Animate from closed to open over 2 seconds.
- Move the collision volume with the rendered panel.
- Support three independent open reasons: base health below 2000, enemy fortress occupation after rule conditions, and O/P panel command.

Do not use the coarse fallback base armor renderer when the authored components exist. If the panel disappears, check whether the authored component transform is being reset after movement or whether the renderer switched back to fallback suppression.

## Robot Appearance Contract

The in-match robot model, room portrait, HUD icon, collision model, and first-person visible barrel should come from the same appearance profile resolution path.

Data and editor owners:

```text
Simulator.Assets/RobotAppearanceModels.cs
AppearanceProfileCatalog.cs
AppearanceEditorForm.cs
AppearanceProfilePreviewControl.cs
EditorPreviewGeometry.cs
```

Runtime render owners:

```text
Simulator3dHost.ResolveAppearanceProfile
Simulator3dForm.AppearanceModel.cs::DrawEntityAppearanceModelModern
Simulator3dForm.GpuRenderer.cs::DrawGpuEntities
Simulator3dForm.GpuRenderer.cs::TryDrawCachedGpuEntityAppearance
Simulator3dForm.OpenGkUi.cs::DrawOpenGkVehiclePortrait / DrawOpenGkTopHudSilhouetteModelV2
```

Collision owners:

```text
EntityCollisionModel.ResolveParts
EntityCollisionModel.ResolveGroundSupportParts
TerrainMotionService.BuildCollisionFootprints
TerrainMotionService.ResolveGroundSupportParts usage near terrain support checks
```

Rules:

- Resolve profile by role plus subtype. Infantry variants (`full`, `mecanum`, `balance`) must switch visual mesh, HUD portrait, movement parameters, and collision model together.
- Custom primitives, anchors, and links in appearance JSON are part of the model and must render in Linux too.
- First-person view should show the real gimbal/barrel from the selected robot, with barrel shake heavily damped. Camera shake may remain stronger than barrel shake.
- Vehicle roll/pitch should affect chassis, gimbal, and camera presentation consistently. Do not keep gimbal visually level if the chassis is visibly rolled.
- Collision should be layered from `EntityCollisionModel`, not a single conservative rectangle. For balance infantry, include chassis body collision in addition to wheel/leg support.
- F3/debug collision drawing must use the same parts that movement uses.

Room/startup behavior:

- Room seats create only active robots. Hidden/inactive robots must not exist in `World.Entities`.
- Robot type and spawn selection happen in preparation. Switching a chassis variant should rebind the entity profile and invalidate cached GPU appearance/portrait buffers without reloading the whole map.

## Physics Engine Contract

There are several physics-like systems. They are not interchangeable; use each one for its intended domain.

| Domain | Owner | Backend | Use for | Do not use for |
| --- | --- | --- | --- | --- |
| World movement and terrain passability | `TerrainMotionService` | native terrain grid/collision surface | robot movement, slopes, step limits, edited collision volumes, buff region queries | high-speed projectile ray tests |
| Robot/entity collision | `EntityCollisionModel` plus `TerrainMotionService` footprints | native layered footprint SAT/capsule checks | robot-vs-wall, robot-vs-robot, chassis/wheel/body collision | visual mesh rendering |
| Projectile obstacle hits | `Simulator3dHost.ResolveProjectileObstacle` | selectable `native` or `bepu` | projectile-vs-entity/map obstacle hit and reflection decisions | robot chassis movement |
| BEPU projectile backend | `BepuProjectileObstacleBackend` with `BepuPhysics` package | BEPU 2.4 | precise projectile obstacle checks where configured | full world physics simulation |
| Combat/rule hit validation | `SimulationCombatMath`, `TerrainMotionService`, `ArenaInteractionService` | rule logic | armor plate hit eligibility, energy mechanism hit windows, damage, buffs | low-level geometry transforms |
| Rendering collision/debug | `Simulator3dForm.FineTerrainActors.cs`, F3 debug paths | renderer only | show what physics is using | gameplay authority |

Projectile backend selection:

```text
Room settings / config
  -> Simulator3dHost.SetProjectilePhysicsBackend
  -> ProjectilePhysicsBackend stored in config and LAN digest
  -> ResolveProjectileObstacle chooses native or BEPU
```

LAN authority:

- Host owns projectile simulation, obstacle hits, damage, buffs, energy hits, and base/fortress state.
- Clients render `LanProjectileSnapshot` and authoritative entity snapshots.
- Clients may predict local aim/camera, but projectile hit results come from host snapshots/events.

Movement rules:

- Vertical passability is checked before horizontal acceptance.
- Mecanum normal step limit is 25 cm; balance infantry wheel step should be much lower unless special mode (`X`) is active.
- Known ramp/fly-slope regions are explicit passable exceptions; random high ledges are not.
- Downhill smoothing belongs in support-height sampling and pose smoothing, not by loosening wall collision.
- Edited collision volumes and moving base armor collision are first-class blockers.

Performance rules:

- Do not create BEPU worlds or large buffers per frame.
- Cache GPU mesh buffers by appearance profile and invalidate only when profile/subtype changes.
- Keep map resources loaded for the whole game process; seat/spawn changes should not reload GLB/map JSON.
- P/O panel opening should reuse cached layout/text primitives and must not rebuild scene resources.

## LAN Authority Contract

Use this flow for commercial-grade LAN behavior:

```text
Room phase
  host/client exchange seat claims, roster, settings, referee role.

Preparation phase
  players choose spawn/robot/config. Host mirrors selections. World can still
  apply preparation previews, but should not repeatedly reload resources.

Live phase
  client -> host: compact player_input at fixed high rate with sequence/time.
  host -> client: authoritative_snapshot at fixed rate.
  host only: simulation, damage, projectile authority, buffs, energy state.
  client: local prediction for own view, then smooth correction from host.
```

Live phase must ignore late room/roster/lobby mutations that would rebuild entities. If flashing back happens only after match start, inspect:

```text
logs/lan_uplink.log
logs/lan_downlink.log
logs/lan_validation.log
logs/lan_match_sync.log
logs/frame_pump.log
```

Red flags:

- Client sends `snapshot` in Live.
- Host receives roster/lobby selection after Live and rebuilds world.
- Entity counts differ between host authoritative snapshot and client active seat list.
- Pitch/yaw/roll signs differ between input frame and snapshot application.
- Snapshot correction is hard-snapping every frame instead of smoothing.

## Map And Editor Contract

Map JSON is split by function and loaded as one map preset at runtime. Feature owners:

```text
Map/editor annotations
  FineTerrainAnnotationDocument.cs
  TerrainEditorForm.cs
  Simulator.LoadLargeTerrain

Runtime collision
  Simulator3dHost.ReloadTerrainCollisionAnnotations
  TerrainMotionService.AddEditedCollisionShapes

Buff authoring
  Buff editor should write the same 3D volume structure consumed by FacilityRegion.

Composite/static mesh authoring
  Composite copy and static primitives belong in the editor UI top toolbar,
  then write map component JSON like normal map parts.
```

The active editor requirement is a top toolbar with expandable feature panels, not more permanent right-side duplicated panels. Copy, paste, primitive creation, collision creation, and buff editing should be invoked from that toolbar and then edit the same document model.

## Linux Parity Checklist For The Current Screenshot

Before calling the Linux port usable, verify this exact list:

- OpenGK top HUD appears at full UI resolution.
- Minimap shows only active robots.
- P/O panel can open, close, and click without freezing the background.
- Local O panel shows local event logs instead of LAN uplink/downlink details.
- Preparation/self-check/countdown overlays appear and allow non-movement keys.
- Third-person camera captures mouse unless `Alt` is held.
- Energy pending markers are on the disk plane, centered on 10-ring centers, with correct team color.
- Energy light arms/chevrons follow the map interaction components.
- Base armor authored panels are visible closed, animate open, and keep collision aligned.
- Edited collision and buff volumes match editor height and YPR.
- Movement checks vertical passability before horizontal acceptance.
- LAN client input is responsive after Live starts and no flashback occurs.

## Linux Migration Plan

### Phase 1: Platform-neutral contracts

Add and use platform-neutral types:

- `GameKey`
- `GameMouseButton`
- `GameInputSnapshot`
- `ICursorCaptureService`
- `IClipboardService`
- `IFileDialogService`
- runtime tick/frame timing service

WinForms and OpenTK should both translate into the same game input model.

### Phase 2: Runtime extraction

Move platform-independent logic away from WinForms:

- live input interpretation
- camera mode decisions
- LAN pump and authority sync
- startup phase state machine
- P/O panel action dispatch
- scene interaction update rules

The code can still be called by `Simulator3dForm`, but it must not require WinForms types.

### Phase 3: OpenTK client shell

Create a cross-platform OpenTK app after the contracts are stable:

```text
src/Simulator.OpenTkClient/Simulator.OpenTkClient.csproj
```

Target:

```xml
<TargetFramework>net10.0</TargetFramework>
```

Use OpenTK for:

- window creation
- key/mouse input
- mouse capture/release
- game loop
- OpenGL context

Do not reference `System.Windows.Forms`.

### Phase 4: UI renderer convergence

Keep OpenGK visual style, but move drawing away from WinForms `Graphics` and `TextRenderer`.

Acceptable paths:

- OpenGL immediate/batched primitives for game HUD and panels.
- SkiaSharp rendered to texture if text/2D shape fidelity is needed.
- ImGui for editor/tool panels, not as the final in-match visual style unless explicitly chosen.

### Phase 5: Editor Linux compatibility

`Simulator.LoadLargeTerrain` is already close to OpenTK/ImGui. The first blocker is WinForms file dialog usage.

Replace `FileDialogService` with:

- ImGui path input
- recent path list
- optional native file dialog abstraction later

Then retarget `LoadLargeTerrain.csproj` from `net10.0-windows` to `net10.0`.

## Required Project Changes

### ThreeD

Current:

```xml
<TargetFramework>net10.0-windows</TargetFramework>
<UseWindowsForms>true</UseWindowsForms>
<PackageReference Include="OpenCvSharp4.runtime.win" ... />
```

Keep this for Windows compatibility for now. Do not try to make it Linux-native directly.

### Future OpenTK client

Create a separate project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="OpenTK" Version="4.9.3" />
    <ProjectReference Include="..\Simulator.Core\Simulator.Core.csproj" />
    <ProjectReference Include="..\Simulator.Assets\Simulator.Assets.csproj" />
    <ProjectReference Include="..\Simulator.Editors\Simulator.Editors.csproj" />
    <ProjectReference Include="..\Simulator.Runtime\Simulator.Runtime.csproj" />
  </ItemGroup>
</Project>
```

Do not reference `Simulator.ThreeD` until its reusable runtime parts are split out of WinForms.

### OpenCV

Do not carry `OpenCvSharp4.runtime.win` into Linux.

Use conditional package references or make OpenCV optional:

```xml
<PackageReference Include="OpenCvSharp4.runtime.win" Version="4.10.0.20240616" Condition="'$(RuntimeIdentifier)' == 'win-x64'" />
```

For Linux, select the matching OpenCvSharp runtime package or system OpenCV binding for the target distro.

## Build Commands

Windows compatibility:

```powershell
dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -c Debug --no-restore
```

Pure runtime projects:

```bash
dotnet build src/Simulator.Core/Simulator.Core.csproj -c Debug
dotnet build src/Simulator.Assets/Simulator.Assets.csproj -c Debug
dotnet build src/Simulator.Editors/Simulator.Editors.csproj -c Debug
dotnet build src/Simulator.Runtime/Simulator.Runtime.csproj -c Debug
```

Future Linux client:

```bash
dotnet run --project src/Simulator.OpenTkClient/Simulator.OpenTkClient.csproj -- --start-match
```

## Runtime Interaction Checklist

Use this list when checking feature parity:

- Main menu opens the OpenGK room UI.
- Local room seat add/remove does not freeze.
- LAN room shows host referee on clients.
- LAN live state does not accept seat/roster mutations that rebuild world entities.
- Hidden P/O panels do not receive mouse clicks.
- O panel works in local mode.
- P panel works in LAN referee mode.
- Preparation/counter/self-check still allow P/O panels.
- Third-person view only releases mouse while `Alt` is held.
- First-person barrel is visible and damped.
- Energy mechanism waiting marker anchors to the 10-ring center.
- Activated energy rings remain lit.
- Base armor moves only annotated components and moves collision with visuals.
- Buff/collision volumes match editor dimensions.
- Minimap/top HUD only show active robots.
- Client input remains responsive after match enters Live.

## Logs To Check

Root logs are preferred:

```text
logs/lan_uplink.log
logs/lan_downlink.log
logs/lan_uplink_detail.log
logs/lan_downlink_detail.log
logs/lan_validation.log
logs/match_startup.log
logs/frame_pump.log
logs/render_perf.log
logs/simulation_perf.log
logs/motion_perf.log
```

For LAN jitter:

- There should be no client `tx_snapshot` in Live once host authority is active.
- Client should send `player_input`.
- Host should send `authoritative_snapshot`.
- Live roster/seat/lobby messages should not rebuild world entities.

## Skill For Future AI

Use the repo skill:

```text
docs/skills/artinx-linux-opentk-port/SKILL.md
```

If using Codex locally, copy `docs/skills/artinx-linux-opentk-port` to the Codex skills directory so it is auto-discovered.

## First Recommended Coding Step

Start by using platform-neutral input types from `Simulator.Runtime`, then convert both WinForms and OpenTK input paths into that model. This is the smallest useful convergence step because it removes the strongest coupling between the future OpenTK client and the legacy WinForms shell.
