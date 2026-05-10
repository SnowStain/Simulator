# Scene Interaction And UI Contract

Use this reference before replacing WinForms paths or moving UI/gameplay into OpenTK. The Linux client is not complete until these behaviors work.

## Match And Room Flow

- Main menu opens the OpenGK room layout, not the old 5v5 vehicle-selection lobby.
- Local room and LAN room use the same room model: seats are the source of truth.
- Room lobby no longer chooses final robot type/position once the match starts; preparation phase handles vehicle/point selection.
- Inactive robots must not exist in `World.Entities`; hiding them is not enough.
- Local mode allows one player-controlled robot. Added AI count equals active AI count.
- LAN mode defaults to no AI unless explicitly configured.
- Referee/spectator seats do not spawn controllable robots.
- Preparation, referee self-check, and countdown allow P/O panels; only movement keys are locked.
- Single-player `Enter` can skip preparation, self-check, and 5-second countdown. LAN cannot skip host authority flow.

## P/O Referee Panels

- LAN referee uses `P`; local mode uses `O`.
- `P/O` toggles open and closed. Hidden panels must not receive mouse input.
- LAN player-side P panel configures performance, not vehicle type, after live.
- Local O panel mirrors referee controls but uses local event logs instead of LAN up/down details.
- Logout returns to room or main menu as specified, never to the old lobby.
- Energy mechanism controls are on a separate page:
  - red/blue
  - small/large
  - waiting/activated/full states
  - choose active arm count and specific arm indexes
- Manual controls include:
  - activate red/blue small/large energy mechanism into waiting state
  - open red/blue base armor plates
  - stop red/blue outpost rotation
  - configure buff/fort/base states when available

## HUD And Cameras

- Top UI shows current base/outpost health numbers above bars without overlap.
- Hidden/inactive robots do not appear in top UI or minimap.
- First-person view sees the vehicle model/barrel from the camera position.
- Third-person view keeps the pivot on the selected vehicle; mouse controls view unless `Alt` releases cursor.
- Death keeps the view pose, applies grayscale/black-white background, and overlays respawn progress above all UI.
- Hit feedback flashes a subtle red gradient around first-person view.
- Barrel shake is heavily damped, but vehicle/camera inertial motion remains.

## Scene Interactions

- Map-editor collision volumes are real runtime collision volumes and debug visuals.
- Collision volume dimensions use editor coordinate mapping and full height.
- Buff regions are 3D volumes, not flat polygons; they must trigger from the same volume drawn in debug.
- Fort capture uses a progress bar and applies different effects for own/enemy fort. Enemy fort capture can open enemy base armor after the rules delay.
- Base armor uses annotated map interactive components only. Do not generate replacement plates in code.
- Base armor can open from any parallel trigger:
  - base health below threshold
  - enemy fort occupation rule
  - P/O manual open
- Base armor visual and collision transforms must move together.

## Energy Mechanism

- Use annotated map components for disks, 10-rings, arms, arm-middle, and arm-outer.
- Waiting-hit marker center must anchor to the 10-ring component center.
- Waiting-hit marker, arrows, arm colors, and R logo use team color:
  - red `255,0,0`
  - blue `0,0,255`
- Waiting-hit markers are visible only during valid waiting-hit windows.
- Activated rings remain lit.
- Small energy: each activated arm stays lit; inactive arms remain dark except the waiting marker/arrow.
- Large energy: side arms and arm-middle fill by progress; arm-outer lights only after full activation.
- Projectile hits count only during waiting-hit windows; after full activation the mechanism ignores hits.
- Auto-aim prediction runs in the energy mechanism vertical plane and must account for non-uniform large-energy motion.

## LAN Runtime Contract

- Host is authoritative.
- Client sends input at high frequency.
- Host sends authoritative snapshots.
- Client performs local prediction and smooth correction using ack.
- After live, roster/seat/lobby messages may update UI but must not rebuild live entities.
- In one-robot rooms, host authoritative snapshot and client entity count must stay one controllable robot plus structures/facilities only.

## Editor Contract

- Map editor must support selecting and copying components/composites.
- Map editor can create collision boxes/cylinders with size, xyz, yaw/pitch/roll, radius/height.
- Buff editing is split into a buff drawing editor that reuses terrain editor interaction style but does not own composite editing.
- Static mesh primitives such as cylinder/prism can be added, colored, and treated like map parts.
- JSON color overrides still apply to model rendering, but editor composite selection should not permanently destroy original model color data.
