using System.Drawing;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Simulator.Platform.Rendering;
using Simulator.Platform.Runtime;
using Simulator.OpenTk.Rendering;
using Simulator.OpenTk.Input;
using Simulator.Platform.Ui;
using Simulator.Runtime.Input;

namespace Simulator.Linux;

internal sealed class LinuxOperatorWindow : GameWindow
{
    private readonly LinuxOperatorRuntime _runtime;
    private readonly GameInputSnapshotAccumulator _inputAccumulator = new();
    private readonly OpenGkUiButtonRegistry _uiButtons = new();
    private readonly GlPrimitiveRenderer _renderer = new();
    private readonly TerrainCacheOpenTkSceneRenderer _sceneRenderer = new();
    private readonly RobotAppearanceOpenTkRenderer _robotRenderer = new();
    private readonly InteractionOpenTkRenderer _interactionRenderer = new();
    private readonly LinuxOperatorOptions _options;

    public LinuxOperatorWindow(
        GameWindowSettings gameWindowSettings,
        NativeWindowSettings nativeWindowSettings,
        LinuxOperatorOptions options)
        : base(gameWindowSettings, nativeWindowSettings)
    {
        _options = options;
        _runtime = new LinuxOperatorRuntime(options);
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        VSync = VSyncMode.Off;
        GL.ClearColor(0.53f, 0.64f, 0.70f, 1.0f);
        GL.Enable(EnableCap.Multisample);
        _renderer.Load();
        _sceneRenderer.Load();
        _robotRenderer.Load();
        _interactionRenderer.Load();
        _runtime.Load();
        TryLoadTerrainScene();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, Math.Max(1, FramebufferSize.X), Math.Max(1, FramebufferSize.Y));
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        GameInputSnapshot input = CaptureInputSnapshot();
        _runtime.ApplyInput(input);
        if (input.PressedMouseButtons.Contains(GameMouseButton.Left)
            && _uiButtons.TryResolve(new Point((int)input.Pointer.X, (int)input.Pointer.Y), canExecute: null, out string? uiAction)
            && uiAction is not null)
        {
            _runtime.ApplyUiAction(uiAction);
        }

        _runtime.Tick(args.Time);
        if (_options.ExitAfterSec is double exitAfterSec && _runtime.TimeSec >= exitAfterSec)
        {
            Close();
            return;
        }

        CursorState = IsFocused && _runtime.CaptureMouse
            ? CursorState.Grabbed
            : CursorState.Normal;
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _renderer.Begin(ClientSize.X, ClientSize.Y);
        DrawLinuxGameFrame();
        _renderer.End();
        SwapBuffers();
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _renderer.Dispose();
        _interactionRenderer.Dispose();
        _robotRenderer.Dispose();
        _sceneRenderer.Dispose();
    }

    protected override void OnFocusedChanged(FocusedChangedEventArgs e)
    {
        base.OnFocusedChanged(e);
        if (!IsFocused)
        {
            _runtime.ApplyInput(_inputAccumulator.ReleaseAll(
                GLFW.GetTime(),
                new GamePointerState(0, 0, 0, 0, 0, CursorCaptured: false)));
        }
    }

    private GameInputSnapshot CaptureInputSnapshot()
    {
        var keys = new HashSet<GameKey>();
        foreach (Keys key in OpenTkGameInputMapper.MonitoredKeys)
        {
            if (KeyboardState.IsKeyDown(key))
            {
                GameKey mapped = OpenTkGameInputMapper.MapKey(key);
                if (mapped != GameKey.None)
                {
                    keys.Add(mapped);
                }
            }
        }

        var buttons = new HashSet<GameMouseButton>();
        AddMouseButton(MouseButton.Left, buttons);
        AddMouseButton(MouseButton.Right, buttons);
        AddMouseButton(MouseButton.Middle, buttons);

        GamePointerState pointer = _inputAccumulator.BuildPointer(
            Math.Clamp(MousePosition.X, 0, Math.Max(1, ClientSize.X) - 1),
            Math.Clamp(MousePosition.Y, 0, Math.Max(1, ClientSize.Y) - 1),
            MouseState.ScrollDelta.Y * 120.0,
            IsFocused && _runtime.CaptureMouse,
            MouseState.Delta.X,
            MouseState.Delta.Y);

        return _inputAccumulator.CaptureState(
            GLFW.GetTime(),
            keys,
            buttons,
            pointer);
    }

    private void AddMouseButton(MouseButton button, HashSet<GameMouseButton> buttons)
    {
        if (!MouseState.IsButtonDown(button))
        {
            return;
        }

        GameMouseButton mapped = OpenTkGameInputMapper.MapMouseButton(button);
        if (mapped != GameMouseButton.None)
        {
            buttons.Add(mapped);
        }
    }

    private void DrawLinuxGameFrame()
    {
        float width = Math.Max(1, ClientSize.X);
        float height = Math.Max(1, ClientSize.Y);
        LinuxGameRenderSnapshot snapshot = _runtime.RenderSnapshot;

        if (_sceneRenderer.IsLoaded)
        {
            OpenTkSceneCamera? camera = BuildSceneCamera(snapshot);
            _sceneRenderer.Render((int)width, (int)height, _runtime.TimeSec, camera);
            _interactionRenderer.Render(ToOpenTkInteractions(snapshot), _sceneRenderer.LastViewProjection, _runtime.TimeSec);
            _robotRenderer.Render(ToOpenTkRobots(snapshot), _sceneRenderer.LastViewProjection);
            _renderer.Rect(0, 0, width, height * 0.16f, new Vector4(0.02f, 0.026f, 0.034f, 0.82f));
            _renderer.Rect(width - 256, height - 256, 232, 232, new Vector4(0.02f, 0.032f, 0.040f, 0.72f));
            RectangleF minimapBounds = new(width - 236, height - 236, 192, 156);
            DrawFacilities(snapshot, minimapBounds);
            DrawProjectiles(snapshot, minimapBounds);
            DrawEntities(snapshot, minimapBounds);
        }
        else
        {
            _renderer.Rect(0, 0, width, height, new Vector4(0.035f, 0.045f, 0.055f, 1.0f));
            _renderer.Rect(0, 0, width, height * 0.16f, new Vector4(0.02f, 0.026f, 0.034f, 0.92f));
            RectangleF mapBounds = ResolveMapBounds(width, height, snapshot.WorldWidth, snapshot.WorldHeight);
            DrawMapSurface(snapshot, mapBounds);
            DrawFacilities(snapshot, mapBounds);
            DrawInteractions(snapshot, mapBounds);
            DrawProjectiles(snapshot, mapBounds);
            DrawEntities(snapshot, mapBounds);
        }

        var ui = new OpenGkUiDrawList();
        _uiButtons.Clear();
        SimulatorRuntimeSnapshot runtimeSnapshot = _runtime.RuntimeSnapshot;
        Size clientSize = new((int)width, (int)height);
        if (runtimeSnapshot.Phase == SimulatorRuntimePhase.Room)
        {
            if (runtimeSnapshot.RoomScreen == SimulatorRuntimeRoomScreen.MainMenu)
            {
                OpenGkGameScreenPainter.AddMainMenu(
                    ui,
                    new Rectangle(0, 0, (int)width, (int)height),
                    new OpenGkMainMenuUiState(
                        "ARTINX A-Soul",
                        "RoboMaster 2026 UC Simulator",
                        snapshot.MapName,
                        _runtime.Status));
            }
            else
            {
                OpenGkGameScreenPainter.AddRoom(
                    ui,
                    new Rectangle(0, 0, (int)width, (int)height),
                    BuildRoomUiState(snapshot));
            }
        }
        else
        {
            OpenGkUiPainter.AddPanel(ui, new Rectangle((int)(width - 244), (int)(height - 244), 219, 219), 166);
            OpenGkUiPainter.AddFlatButton(
                ui,
                new Rectangle(24, (int)(height - 104), 145, 34),
                "Capture",
                "linux:capture_mouse",
                _runtime.CaptureMouse,
                enabled: true,
                hoverMix: 0.0f,
                activeColor: Color.FromArgb(58, 124, 214));
            OpenGkUiPainter.AddFlatButton(
                ui,
                new Rectangle(179, (int)(height - 104), 145, 34),
                "Release",
                "linux:release_mouse",
                !_runtime.CaptureMouse,
                enabled: true,
                hoverMix: 0.0f,
                activeColor: Color.FromArgb(70, 138, 154));
            OpenGkRuntimeHudState hudState = _runtime.CreateHudState();
            OpenGkRuntimeHudPainter.AddRuntimeStatusOverlay(ui, clientSize, hudState);
            OpenGkGameScreenPainter.AddPreparationOverlay(
                ui,
                clientSize,
                new OpenGkPreparationUiState(
                    runtimeSnapshot.Phase,
                    runtimeSnapshot.PhaseRemainingSec,
                    runtimeSnapshot.Mode == SimulatorRuntimeMode.Local,
                    snapshot.Entities.FirstOrDefault(entity => entity.IsSelected)?.RoleKey ?? "none",
                    snapshot.Entities.FirstOrDefault(entity => entity.IsSelected)?.AppearanceProfile?.WheelStyle ?? "default"));
            OpenGkRuntimeHudPainter.AddDeathOverlay(ui, clientSize, hudState);
            AddSnapshotText(ui, snapshot, width, height);
        }

        _renderer.Draw(ui);
        _uiButtons.AddRange(ui.Buttons);
        if (_runtime.OperatorPanelOpen)
        {
            _uiButtons.Clear();
            DrawLocalControlPanel(width, height);
        }

        float pulse = 0.5f + 0.5f * MathF.Sin((float)_runtime.TimeSec * 2.0f);
        _renderer.Rect(width * 0.5f - 18, height * 0.5f - 1, 36, 2, new Vector4(0.95f, 0.98f, 1.0f, 0.8f));
        _renderer.Rect(width * 0.5f - 1, height * 0.5f - 18, 2, 36, new Vector4(0.95f, 0.98f, 1.0f, 0.8f));
        _renderer.Rect(width * 0.5f - 4, height * 0.5f - 4, 8, 8, new Vector4(0.2f, 0.7f, 1.0f, 0.35f + pulse * 0.35f));
    }

    private OpenGkRoomUiState BuildRoomUiState(LinuxGameRenderSnapshot snapshot)
    {
        LinuxEntityRenderItem? local = snapshot.Entities.FirstOrDefault(entity => entity.IsSelected);
        IReadOnlyList<OpenGkRoomSeatUiState> red = BuildTeamSeats(snapshot, "red", local?.Id);
        IReadOnlyList<OpenGkRoomSeatUiState> blue = BuildTeamSeats(snapshot, "blue", local?.Id);
        IReadOnlyList<OpenGkRoomSeatUiState> referee =
        [
            new("Referee", "neutral", "local operator", "local", true, false),
            new("Spectator", "neutral", "waiting", "view", false, false),
        ];
        return new OpenGkRoomUiState(
            "RoboMaster 2026 UC",
            "Local Room",
            snapshot.MapName,
            _runtime.Status,
            red,
            blue,
            referee,
            snapshot.Entities.Any(entity => entity.IsSelected && entity.IsAlive));
    }

    private static IReadOnlyList<OpenGkRoomSeatUiState> BuildTeamSeats(LinuxGameRenderSnapshot snapshot, string team, string? localEntityId)
    {
        LinuxEntityRenderItem[] robots = snapshot.Entities
            .Where(entity =>
                string.Equals(entity.Team, team, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        var seats = new List<OpenGkRoomSeatUiState>(5);
        for (int index = 0; index < 5; index++)
        {
            LinuxEntityRenderItem? robot = index < robots.Length ? robots[index] : null;
            seats.Add(new OpenGkRoomSeatUiState(
                $"{CultureName(team)} Seat {index + 1}",
                team,
                robot?.Id ?? "waiting",
                robot?.RoleKey ?? "unassigned",
                robot is not null,
                robot?.Id == localEntityId));
        }

        return seats;
    }

    private static string CultureName(string team)
        => string.Equals(team, "red", StringComparison.OrdinalIgnoreCase) ? "Red" : "Blue";

    private void TryLoadTerrainScene()
    {
        try
        {
            string terrainCachePath = _runtime.TerrainCachePath;
            if (!string.IsNullOrWhiteSpace(terrainCachePath))
            {
                _sceneRenderer.LoadTerrainCache(terrainCachePath);
                Simulator.Core.SimulatorRuntimeLog.Append(
                    "linux_operator.log",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} terrain_scene loaded={_sceneRenderer.IsLoaded} chunks={_sceneRenderer.ChunkCount} path={terrainCachePath}");
            }
        }
        catch (Exception ex)
        {
            Simulator.Core.SimulatorRuntimeLog.Append(
                "linux_operator.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} terrain_scene_failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrawMapSurface(LinuxGameRenderSnapshot snapshot, RectangleF mapBounds)
    {
        _renderer.Rect(mapBounds.X, mapBounds.Y, mapBounds.Width, mapBounds.Height, new Vector4(0.06f, 0.095f, 0.115f, 1.0f));
        _renderer.Line(mapBounds.Left, mapBounds.Top, mapBounds.Right, mapBounds.Top, 2.0f, new Vector4(0.26f, 0.52f, 0.72f, 0.78f));
        _renderer.Line(mapBounds.Right, mapBounds.Top, mapBounds.Right, mapBounds.Bottom, 2.0f, new Vector4(0.26f, 0.52f, 0.72f, 0.78f));
        _renderer.Line(mapBounds.Right, mapBounds.Bottom, mapBounds.Left, mapBounds.Bottom, 2.0f, new Vector4(0.26f, 0.52f, 0.72f, 0.78f));
        _renderer.Line(mapBounds.Left, mapBounds.Bottom, mapBounds.Left, mapBounds.Top, 2.0f, new Vector4(0.26f, 0.52f, 0.72f, 0.78f));

        for (int i = 1; i < 8; i++)
        {
            float x = mapBounds.Left + mapBounds.Width * i / 8.0f;
            _renderer.Line(x, mapBounds.Top, x, mapBounds.Bottom, 1.0f, new Vector4(0.11f, 0.17f, 0.20f, 0.38f));
        }

        for (int i = 1; i < 5; i++)
        {
            float y = mapBounds.Top + mapBounds.Height * i / 5.0f;
            _renderer.Line(mapBounds.Left, y, mapBounds.Right, y, 1.0f, new Vector4(0.11f, 0.17f, 0.20f, 0.38f));
        }
    }

    private void DrawFacilities(LinuxGameRenderSnapshot snapshot, RectangleF mapBounds)
    {
        foreach (LinuxFacilityRenderItem facility in snapshot.Facilities)
        {
            Vector4 edge = ResolveTeamVector(facility.Team, alpha: 0.82f);
            Vector4 fill = ResolveFacilityFill(facility.Type, facility.Team);
            if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && facility.Points.Count >= 3)
            {
                PointF first = WorldToScreen(snapshot, mapBounds, facility.Points[0].X, facility.Points[0].Y);
                for (int index = 1; index < facility.Points.Count - 1; index++)
                {
                    PointF p1 = WorldToScreen(snapshot, mapBounds, facility.Points[index].X, facility.Points[index].Y);
                    PointF p2 = WorldToScreen(snapshot, mapBounds, facility.Points[index + 1].X, facility.Points[index + 1].Y);
                    _renderer.Triangle(first.X, first.Y, p1.X, p1.Y, p2.X, p2.Y, fill);
                }

                DrawPolyline(snapshot, mapBounds, facility.Points.Select(point => (point.X, point.Y)).ToArray(), edge, 1.4f, close: true);
                continue;
            }

            PointF a = WorldToScreen(snapshot, mapBounds, Math.Min(facility.X1, facility.X2), Math.Min(facility.Y1, facility.Y2));
            PointF b = WorldToScreen(snapshot, mapBounds, Math.Max(facility.X1, facility.X2), Math.Max(facility.Y1, facility.Y2));
            float x = Math.Min(a.X, b.X);
            float y = Math.Min(a.Y, b.Y);
            float w = Math.Abs(a.X - b.X);
            float h = Math.Abs(a.Y - b.Y);
            if (w < 1.0f || h < 1.0f)
            {
                continue;
            }

            _renderer.Rect(x, y, w, h, fill);
            _renderer.Line(x, y, x + w, y, 1.2f, edge);
            _renderer.Line(x + w, y, x + w, y + h, 1.2f, edge);
            _renderer.Line(x + w, y + h, x, y + h, 1.2f, edge);
            _renderer.Line(x, y + h, x, y, 1.2f, edge);
        }
    }

    private void DrawInteractions(LinuxGameRenderSnapshot snapshot, RectangleF mapBounds)
    {
        foreach (InteractionSceneRenderItem item in snapshot.Interactions)
        {
            Vector4 edge = ToVector(item.EdgeColor);
            Vector4 fill = ToVector(item.FillColor);
            if (item.PrimitiveKind is InteractionScenePrimitiveKind.Polygon or InteractionScenePrimitiveKind.Triangle
                && item.Points.Count >= 3)
            {
                PointF first = WorldToScreen(snapshot, mapBounds, item.Points[0].X, item.Points[0].Y);
                for (int index = 1; index < item.Points.Count - 1; index++)
                {
                    PointF b = WorldToScreen(snapshot, mapBounds, item.Points[index].X, item.Points[index].Y);
                    PointF c = WorldToScreen(snapshot, mapBounds, item.Points[index + 1].X, item.Points[index + 1].Y);
                    _renderer.Triangle(first.X, first.Y, b.X, b.Y, c.X, c.Y, fill);
                }

                DrawPolyline(snapshot, mapBounds, item.Points.Select(point => ((double)point.X, (double)point.Y)).ToArray(), edge, 1.5f, close: true);
            }
        }
    }

    private void DrawEntities(LinuxGameRenderSnapshot snapshot, RectangleF mapBounds)
    {
        foreach (LinuxEntityRenderItem entity in snapshot.Entities)
        {
            PointF center = WorldToScreen(snapshot, mapBounds, entity.X, entity.Y);
            float radius = ResolveEntityRadius(entity);
            Vector4 color = ResolveTeamVector(entity.Team, entity.IsAlive ? 0.96f : 0.30f);
            Vector4 body = new(color.X * 0.65f + 0.12f, color.Y * 0.65f + 0.14f, color.Z * 0.65f + 0.16f, entity.IsAlive ? 0.95f : 0.28f);
            _renderer.Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2, body);
            _renderer.Line(center.X - radius, center.Y - radius, center.X + radius, center.Y - radius, 1.4f, color);
            _renderer.Line(center.X + radius, center.Y - radius, center.X + radius, center.Y + radius, 1.4f, color);
            _renderer.Line(center.X + radius, center.Y + radius, center.X - radius, center.Y + radius, 1.4f, color);
            _renderer.Line(center.X - radius, center.Y + radius, center.X - radius, center.Y - radius, 1.4f, color);

            float yaw = MathHelper.DegreesToRadians((float)entity.AngleDeg);
            _renderer.Line(
                center.X,
                center.Y,
                center.X + MathF.Cos(yaw) * radius * 1.8f,
                center.Y - MathF.Sin(yaw) * radius * 1.8f,
                entity.IsSelected ? 3.0f : 1.8f,
                entity.IsSelected ? new Vector4(1.0f, 0.86f, 0.20f, 1.0f) : color);

            if (entity.MaxHealth > 1.0)
            {
                float healthWidth = radius * 2.2f;
                float healthRatio = (float)Math.Clamp(entity.Health / Math.Max(1.0, entity.MaxHealth), 0.0, 1.0);
                _renderer.Rect(center.X - healthWidth * 0.5f, center.Y - radius - 9, healthWidth, 3, new Vector4(0.08f, 0.09f, 0.10f, 0.9f));
                _renderer.Rect(center.X - healthWidth * 0.5f, center.Y - radius - 9, healthWidth * healthRatio, 3, color);
            }
        }
    }

    private void DrawProjectiles(LinuxGameRenderSnapshot snapshot, RectangleF mapBounds)
    {
        foreach (LinuxProjectileRenderItem projectile in snapshot.Projectiles)
        {
            PointF point = WorldToScreen(snapshot, mapBounds, projectile.X, projectile.Y);
            Vector4 color = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
                ? new Vector4(1.0f, 0.72f, 0.24f, 0.95f)
                : new Vector4(0.80f, 0.94f, 1.0f, 0.90f);
            _renderer.Rect(point.X - 2, point.Y - 2, 4, 4, color);
        }
    }

    private void AddSnapshotText(OpenGkUiDrawList ui, LinuxGameRenderSnapshot snapshot, float width, float height)
    {
        LinuxEntityRenderItem? selected = snapshot.Entities.FirstOrDefault(entity => entity.IsSelected);
        string selectedLine = selected is null
            ? "local entity: none"
            : $"{selected.Id} {selected.RoleKey} hp {selected.Health:0}/{selected.MaxHealth:0}";
        ui.Text(
            new Rectangle(32, 96, 520, 22),
            $"{snapshot.MapName} t={snapshot.GameTimeSec:0.0}s entities={snapshot.Entities.Count} projectiles={snapshot.Projectiles.Count}",
            Color.FromArgb(230, 215, 232, 245),
            OpenGkUiTextStyle.Small);
        ui.Text(
            new Rectangle(32, 120, 520, 22),
            selectedLine,
            Color.FromArgb(238, 255, 224, 120),
            OpenGkUiTextStyle.Small);
        ui.Text(
            new Rectangle(32, 144, 520, 22),
            $"camera={_runtime.CameraMode}  V toggle first/third person",
            Color.FromArgb(210, 178, 215, 255),
            OpenGkUiTextStyle.Small);
    }

    private OpenTkSceneCamera? BuildSceneCamera(LinuxGameRenderSnapshot snapshot)
    {
        LinuxEntityRenderItem? selected = snapshot.Entities.FirstOrDefault(entity => entity.IsSelected && entity.IsAlive);
        if (selected is null || snapshot.MetersPerModelUnit <= 1e-6)
        {
            return null;
        }

        var position = new Vector3((float)selected.SceneX, (float)selected.SceneY, (float)selected.SceneZ);
        if (!IsFinite(position))
        {
            return null;
        }

        float unit = (float)Math.Clamp(1.0 / snapshot.MetersPerModelUnit, 0.05, 80.0);
        Vector3 up = Vector3.UnitY;
        Vector3 chassisForward = NormalizeOrDefault(
            new Vector3((float)selected.SceneForwardX, 0.0f, (float)selected.SceneForwardZ),
            Vector3.UnitX);
        Vector3 turretForward = NormalizeOrDefault(
            new Vector3((float)selected.SceneTurretForwardX, 0.0f, (float)selected.SceneTurretForwardZ),
            chassisForward);

        if (_runtime.CameraMode == LinuxCameraMode.FirstPerson)
        {
            Vector3 right = NormalizeOrDefault(Vector3.Cross(turretForward, up), Vector3.UnitZ);
            double cameraOffsetX = selected.AppearanceProfile?.FirstPersonCameraOffsetXM ?? 0.0;
            double cameraOffsetY = selected.AppearanceProfile?.FirstPersonCameraOffsetYM ?? 0.0;
            double cameraOffsetZ = selected.AppearanceProfile?.FirstPersonCameraOffsetZM ?? 0.0;
            float baseHeight = (float)Math.Max(
                0.20,
                selected.BodyClearanceM + selected.BodyHeightM + Math.Max(0.08, selected.GimbalHeightM));
            Vector3 cameraPosition = position
                + up * baseHeight * unit
                + turretForward * (float)(cameraOffsetX * unit)
                + up * (float)(cameraOffsetY * unit)
                + right * (float)(cameraOffsetZ * unit);
            float pitch = MathHelper.DegreesToRadians((float)(selected.GimbalPitchDeg + (selected.AppearanceProfile?.FirstPersonCameraPitchDeg ?? 0.0)));
            Vector3 viewForward = NormalizeOrDefault(
                turretForward * MathF.Cos(pitch) + up * MathF.Sin(pitch),
                turretForward);
            return new OpenTkSceneCamera(cameraPosition, cameraPosition + viewForward * unit * 5.0f, up);
        }

        float targetHeight = (float)Math.Max(0.25, selected.BodyClearanceM + selected.BodyHeightM * 0.65) * unit;
        Vector3 target = position + up * targetHeight;
        float distance = MathF.Max(unit * 2.8f, (float)selected.BodyLengthM * unit * 4.5f);
        float height = MathF.Max(unit * 1.15f, (float)(selected.BodyHeightM + selected.GimbalHeightM) * unit * 1.6f);
        Vector3 camera = target - chassisForward * distance + up * height;
        return new OpenTkSceneCamera(camera, target + chassisForward * unit * 0.75f, up);
    }

    private static IReadOnlyList<OpenTkRobotRenderItem> ToOpenTkRobots(LinuxGameRenderSnapshot snapshot)
    {
        float unit = (float)Math.Clamp(1.0 / Math.Max(1e-6, snapshot.MetersPerModelUnit), 0.05, 80.0);
        return snapshot.Entities
            .Select(entity => new OpenTkRobotRenderItem(
                entity.Id,
                entity.Team,
                entity.EntityType,
                entity.RoleKey,
                new Vector3((float)entity.SceneX, (float)entity.SceneY, (float)entity.SceneZ),
                new Vector3((float)entity.SceneForwardX, (float)entity.SceneForwardY, (float)entity.SceneForwardZ),
                new Vector3((float)entity.SceneTurretForwardX, (float)entity.SceneTurretForwardY, (float)entity.SceneTurretForwardZ),
                entity.ChassisPitchDeg,
                entity.ChassisRollDeg,
                entity.GimbalPitchDeg,
                entity.BodyLengthM,
                entity.BodyWidthM,
                entity.BodyHeightM,
                entity.BodyClearanceM,
                entity.BodyRenderWidthScale,
                entity.WheelRadiusM,
                entity.GimbalLengthM,
                entity.GimbalWidthM,
                entity.GimbalBodyHeightM,
                entity.GimbalHeightM,
                entity.GimbalMountGapM,
                entity.GimbalMountLengthM,
                entity.GimbalMountWidthM,
                entity.GimbalMountHeightM,
                entity.BarrelLengthM,
                entity.BarrelRadiusM,
                unit,
                entity.IsAlive,
                entity.IsSelected,
                entity.WheelOffsetsM,
                entity.AppearanceProfile))
            .ToArray();
    }

    private static IReadOnlyList<OpenTkInteractionRenderItem> ToOpenTkInteractions(LinuxGameRenderSnapshot snapshot)
        => snapshot.SceneInteractions
            .Select(item => new OpenTkInteractionRenderItem(
                item.Id,
                item.Kind,
                item.Type,
                item.Team,
                new Vector3((float)item.SceneX, (float)item.SceneY, (float)item.SceneZ),
                (float)item.SceneYawDeg,
                (float)item.SizeXModel,
                (float)item.SizeYModel,
                (float)item.SizeZModel,
                (float)item.RadiusModel,
                item.LitMask,
                item.ActivatedMask,
                item.ActivatedCount,
                item.LargeEnergy,
                item.Stopped,
                item.Progress,
                item.ScenePoints
                    .Select(point => new Vector3((float)point.X, (float)point.Y, (float)point.Z))
                    .ToArray()))
            .ToArray();

    private static Vector3 NormalizeOrDefault(Vector3 value, Vector3 fallback)
        => value.LengthSquared > 1e-8f ? Vector3.Normalize(value) : fallback;

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static RectangleF ResolveMapBounds(float width, float height, double worldWidth, double worldHeight)
    {
        float left = 28.0f;
        float top = 148.0f;
        float availableWidth = Math.Max(240.0f, width - 56.0f);
        float availableHeight = Math.Max(180.0f, height - 280.0f);
        float aspect = (float)(Math.Max(1.0, worldWidth) / Math.Max(1.0, worldHeight));
        float mapWidth = availableWidth;
        float mapHeight = mapWidth / Math.Max(0.01f, aspect);
        if (mapHeight > availableHeight)
        {
            mapHeight = availableHeight;
            mapWidth = mapHeight * aspect;
        }

        return new RectangleF(left + (availableWidth - mapWidth) * 0.5f, top, mapWidth, mapHeight);
    }

    private static PointF WorldToScreen(LinuxGameRenderSnapshot snapshot, RectangleF mapBounds, double x, double y)
    {
        float sx = mapBounds.Left + (float)(x / Math.Max(1.0, snapshot.WorldWidth)) * mapBounds.Width;
        float sy = mapBounds.Bottom - (float)(y / Math.Max(1.0, snapshot.WorldHeight)) * mapBounds.Height;
        return new PointF(sx, sy);
    }

    private void DrawPolyline(
        LinuxGameRenderSnapshot snapshot,
        RectangleF mapBounds,
        IReadOnlyList<(double X, double Y)> points,
        Vector4 color,
        float thickness,
        bool close)
    {
        if (points.Count < 2)
        {
            return;
        }

        for (int index = 0; index < points.Count - 1; index++)
        {
            PointF a = WorldToScreen(snapshot, mapBounds, points[index].X, points[index].Y);
            PointF b = WorldToScreen(snapshot, mapBounds, points[index + 1].X, points[index + 1].Y);
            _renderer.Line(a.X, a.Y, b.X, b.Y, thickness, color);
        }

        if (close)
        {
            PointF a = WorldToScreen(snapshot, mapBounds, points[^1].X, points[^1].Y);
            PointF b = WorldToScreen(snapshot, mapBounds, points[0].X, points[0].Y);
            _renderer.Line(a.X, a.Y, b.X, b.Y, thickness, color);
        }
    }

    private static float ResolveEntityRadius(LinuxEntityRenderItem entity)
    {
        if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            return 13.0f;
        }

        if (string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            return 9.0f;
        }

        if (string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return 12.0f;
        }

        return entity.IsSelected ? 8.0f : 6.5f;
    }

    private static Vector4 ResolveFacilityFill(string type, string team)
    {
        if (type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "supply", StringComparison.OrdinalIgnoreCase))
        {
            Vector4 teamColor = ResolveTeamVector(team, 0.18f);
            return new Vector4(teamColor.X, teamColor.Y, teamColor.Z, 0.18f);
        }

        if (string.Equals(type, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            Vector4 teamColor = ResolveTeamVector(team, 0.26f);
            return new Vector4(teamColor.X, teamColor.Y, teamColor.Z, 0.26f);
        }

        return new Vector4(0.55f, 0.61f, 0.48f, 0.20f);
    }

    private static Vector4 ResolveTeamVector(string team, float alpha)
        => string.Equals(team, "red", StringComparison.OrdinalIgnoreCase)
            ? new Vector4(1.0f, 0.20f, 0.22f, alpha)
            : string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase)
                ? new Vector4(0.18f, 0.42f, 1.0f, alpha)
                : new Vector4(0.96f, 0.76f, 0.20f, alpha);

    private static Vector4 ToVector(Color color)
        => new(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);

    private void DrawLocalControlPanel(float width, float height)
    {
        var panel = new OpenGkUiDrawList();
        panel.FillRect(new Rectangle(0, 0, (int)width, (int)height), Color.FromArgb(154, 8, 10, 12));
        OpenGkRefereePanelLayout layout = OpenGkRefereePanelLayoutResolver.Resolve(new Size((int)width, (int)height), showLogout: true);
        OpenGkRefereePanelLayoutResolver.AddChrome(
            panel,
            layout,
            "Local Control",
            "O closes / Alt releases mouse / local game only",
            showLogout: true,
            logoutLabel: "Back",
            page: _runtime.LocalPanelPage,
            closeAction: "local_close",
            logoutAction: "local_return",
            mainTabAction: "local_page:main",
            energyTabAction: "local_page:energy");

        if (_runtime.LocalPanelPage == OpenGkRefereePanelPage.Energy)
        {
            OpenGkRefereePanelContent.AddEnergyGrid(panel, layout.Content, _runtime.CreateEnergyCards());
        }
        else
        {
            OpenGkRefereePanelContent.AddLocalOverview(panel, layout.Content, _runtime.Status, _runtime.CreateQuickActions());
        }

        _renderer.Draw(panel);
        _uiButtons.AddRange(panel.Buttons);
    }
}
