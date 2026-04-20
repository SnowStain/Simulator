using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Windows.Forms;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm : Form
{
    private const int ToolbarHeight = 54;
    private const int HudHeight = 118;
    private const int SidebarWidth = 270;
    private const int DecisionSidebarWidth = 320;

    private static readonly string[] HudRosterOrder =
    {
        "robot_1",
        "robot_2",
        "robot_3",
        "robot_4",
        "robot_7",
    };

    private static readonly IReadOnlyDictionary<string, string> HudUnitLabelMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["robot_1"] = "1 英雄",
            ["robot_2"] = "2 工程",
            ["robot_3"] = "3 步兵",
            ["robot_4"] = "4 步兵",
            ["robot_7"] = "7 哨兵",
        };

    private enum SimulatorAppState
    {
        MainMenu,
        Lobby,
        InMatch,
    }

    private readonly record struct UiButton(Rectangle Rect, string Action);

    private readonly record struct TerrainFacePatch(
        Vector3[] Vertices,
        Vector3 CenterScene,
        float MinXWorld,
        float MinYWorld,
        float MaxXWorld,
        float MaxYWorld,
        Color FillColor,
        Color EdgeColor);

    private readonly record struct ProjectedFace(
        PointF[] Points,
        float AverageDepth,
        Color FillColor,
        Color EdgeColor);

    private sealed class FloatingCombatMarker
    {
        public FloatingCombatMarker(string targetId, double worldX, double worldY, double heightM, string text, Color color, float lifetimeSec)
        {
            TargetId = targetId;
            WorldX = worldX;
            WorldY = worldY;
            HeightM = heightM;
            Text = text;
            Color = color;
            LifetimeSec = Math.Max(0.12f, lifetimeSec);
        }

        public string TargetId { get; }

        public double WorldX { get; }

        public double WorldY { get; }

        public double HeightM { get; }

        public string Text { get; }

        public Color Color { get; }

        public float LifetimeSec { get; }

        public float AgeSec { get; set; }
    }

    private const int MaxSimulationCatchUpSteps = 8;

    private readonly Simulator3dHost _host;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private readonly Font _tinyHudFont = new("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _smallHudFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _hudMidFont = new("Microsoft YaHei UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _hudBigFont = new("Microsoft YaHei UI", 18f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _titleFont = new("Microsoft YaHei UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _menuTitleFont = new("Microsoft YaHei UI", 22f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _menuSubtitleFont = new("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly List<UiButton> _uiButtons = new();
    private readonly HashSet<Keys> _heldKeys = new();

    private SimulatorAppState _appState;
    private bool _paused;
    private bool _followSelection = true;
    private bool _panDragging;
    private bool _firePressed;
    private bool _autoAimPressed;
    private bool _buyAmmoRequested;
    private bool _pendingJumpRequest;
    private bool _showDebugSidebars;
    private bool _showProjectileTrails;
    private bool _firstPersonView = true;
    private bool _mouseCaptureActive;
    private bool _suppressMouseWarp;
    private bool _spaceKeyWasDown;
    private bool _buyKeyWasDown;
    private Point _lastMouse;
    private float _pendingMouseYawDeltaDeg;
    private float _pendingMousePitchDeltaDeg;

    private float _cameraYawRad = -0.85f;
    private float _cameraPitchRad = 0.62f;
    private float _cameraDistanceM = 24f;
    private Vector3 _cameraTargetM;
    private Vector3 _cameraPositionM;
    private Matrix4x4 _viewMatrix;
    private Matrix4x4 _projectionMatrix;
    private RuntimeGridData? _cachedRuntimeGrid;
    private readonly List<TerrainFacePatch> _terrainFaces = new();
    private readonly List<TerrainFacePatch> _terrainDetailFaces = new();
    private readonly List<TerrainFacePatch> _terrainDrawBuffer = new();
    private readonly List<ProjectedFace> _projectedTerrainFaceBuffer = new();
    private readonly List<SimulationEntity> _entityDrawBuffer = new();
    private readonly Dictionary<string, List<Vector3>> _projectileTrailPoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FloatingCombatMarker> _combatMarkers = new();
    private Bitmap? _terrainColorBitmap;
    private string? _terrainColorBitmapPath;
    private int _terrainDetailCenterCellX = int.MinValue;
    private int _terrainDetailCenterCellY = int.MinValue;
    private float _terrainDetailMinXWorld;
    private float _terrainDetailMinYWorld;
    private float _terrainDetailMaxXWorld;
    private float _terrainDetailMaxYWorld;
    private long _lastTerrainDetailRebuildTicks;
    private long _lastFrameClockTicks;
    private double _simulationAccumulatorSec;

    public Simulator3dForm(Simulator3dOptions options)
    {
        _host = new Simulator3dHost(options);

        Text = "RM26 C# 3D Simulator";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 720);
        ClientSize = new Size(1440, 900);
        BackColor = Color.FromArgb(16, 20, 28);
        DoubleBuffered = true;
        KeyPreview = true;

        _appState = options.StartInMatch ? SimulatorAppState.InMatch : SimulatorAppState.MainMenu;
        _paused = _appState != SimulatorAppState.InMatch;
        _lastFrameClockTicks = _frameClock.ElapsedTicks;

        ResetCameraForMap();

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 16,
        };
        _timer.Tick += (_, _) => OnFrameTick();
        _timer.Start();

        MouseDown += OnMouseDownInternal;
        MouseUp += OnMouseUpInternal;
        MouseMove += OnMouseMoveInternal;
        MouseWheel += OnMouseWheelInternal;
        KeyDown += OnKeyDownInternal;
        KeyUp += OnKeyUpInternal;
        Activated += (_, _) => UpdateMouseCaptureState();
        Deactivate += (_, _) =>
        {
            ReleaseMouseCapture();
            ResetLiveInput();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tinyHudFont.Dispose();
            _smallHudFont.Dispose();
            _hudMidFont.Dispose();
            _hudBigFont.Dispose();
            _titleFont.Dispose();
            _menuTitleFont.Dispose();
            _menuSubtitleFont.Dispose();
            _terrainColorBitmap?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.Clear(BackColor);
        _uiButtons.Clear();

        DrawBackground(graphics);
        switch (_appState)
        {
            case SimulatorAppState.MainMenu:
                DrawMainMenu(graphics);
                break;
            case SimulatorAppState.Lobby:
                DrawLobby(graphics);
                break;
            case SimulatorAppState.InMatch:
                UpdateCameraMatrices();
                DrawFloor(graphics);
                DrawFacilities(graphics);
                DrawEntities(graphics);
                DrawProjectiles(graphics);
                DrawCombatMarkers(graphics);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                DrawCrosshair(graphics);
                DrawMatchToolbar(graphics);
                DrawHud(graphics);
                DrawPlayerStatusPanelV2(graphics);
                DrawOrientationWidget(graphics);
                if (_showDebugSidebars)
                {
                    DrawDecisionDeploymentPanel(graphics);
                }
                break;
        }
    }

    private void OnFrameTick()
    {
        UpdateMouseCaptureState();
        if (_appState == SimulatorAppState.InMatch && !_paused)
        {
            AdvanceSimulationClock();
            UpdateProjectileTrailCache();
        }
        else if (_appState != SimulatorAppState.InMatch)
        {
            _simulationAccumulatorSec = 0.0;
            _projectileTrailPoints.Clear();
            _combatMarkers.Clear();
        }
        else
        {
            _simulationAccumulatorSec = 0.0;
        }

        Invalidate();
    }

    private void AdvanceSimulationClock()
    {
        long currentTicks = _frameClock.ElapsedTicks;
        long elapsedTicks = Math.Max(0, currentTicks - _lastFrameClockTicks);
        _lastFrameClockTicks = currentTicks;

        double elapsedSec = Math.Min(0.100, elapsedTicks / (double)Stopwatch.Frequency);
        double fixedDt = Math.Max(0.008, _host.DeltaTimeSec);
        _simulationAccumulatorSec = Math.Min(_simulationAccumulatorSec + elapsedSec, fixedDt * MaxSimulationCatchUpSteps);

        PlayerControlState firstState = BuildPlayerControlState();
        PlayerControlState repeatedState = firstState with
        {
            TurretYawDeltaDeg = 0.0,
            GimbalPitchDeltaDeg = 0.0,
            JumpRequested = false,
            BuyAmmoRequested = false,
        };

        int simulatedSteps = 0;
        while (_simulationAccumulatorSec + 1e-9 >= fixedDt && simulatedSteps < MaxSimulationCatchUpSteps)
        {
            _host.Step(simulatedSteps == 0 ? firstState : repeatedState);
            CaptureCombatMarkersFromLatestReport();
            _simulationAccumulatorSec -= fixedDt;
            simulatedSteps++;
        }

        AdvanceCombatMarkers((float)elapsedSec);
    }

    private void OnMouseDownInternal(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            string? action = ResolveUiAction(eventArgs.Location);
            if (!string.IsNullOrWhiteSpace(action))
            {
                ExecuteUiAction(action);
                return;
            }
        }

        if (_appState != SimulatorAppState.InMatch)
        {
            return;
        }

        if (!Focused)
        {
            Focus();
        }

        if (eventArgs.Button == MouseButtons.Left)
        {
            _firePressed = true;
            UpdateMouseCaptureState();
            return;
        }

        if (eventArgs.Button == MouseButtons.Right)
        {
            _autoAimPressed = true;
            UpdateMouseCaptureState();
            return;
        }

        if (!_firstPersonView && eventArgs.Button == MouseButtons.Middle)
        {
            _panDragging = true;
            _lastMouse = eventArgs.Location;
        }
    }

    private void OnMouseUpInternal(object? sender, MouseEventArgs eventArgs)
    {
        if (_appState != SimulatorAppState.InMatch)
        {
            return;
        }

        if (eventArgs.Button == MouseButtons.Right)
        {
            _autoAimPressed = false;
        }

        if (!_firstPersonView && eventArgs.Button == MouseButtons.Middle)
        {
            _panDragging = false;
        }

        if (eventArgs.Button == MouseButtons.Left)
        {
            _firePressed = false;
        }
    }

    private void OnMouseMoveInternal(object? sender, MouseEventArgs eventArgs)
    {
        if (_suppressMouseWarp)
        {
            _suppressMouseWarp = false;
            _lastMouse = eventArgs.Location;
            return;
        }

        Point delta = new(eventArgs.X - _lastMouse.X, eventArgs.Y - _lastMouse.Y);
        _lastMouse = eventArgs.Location;

        if (_appState != SimulatorAppState.InMatch)
        {
            return;
        }

        if (_mouseCaptureActive && !_paused)
        {
            Point center = new(ClientSize.Width / 2, ClientSize.Height / 2);
            Point lookDelta = new(eventArgs.X - center.X, eventArgs.Y - center.Y);
            if (lookDelta.X != 0 || lookDelta.Y != 0)
            {
                _pendingMouseYawDeltaDeg += lookDelta.X * 0.18f;
                _pendingMousePitchDeltaDeg -= lookDelta.Y * 0.14f;
                if (!_firstPersonView)
                {
                    _cameraYawRad += lookDelta.X * (0.18f * MathF.PI / 180f);
                    _cameraPitchRad = Math.Clamp(
                        _cameraPitchRad - lookDelta.Y * (0.14f * MathF.PI / 180f),
                        0.12f,
                        1.20f);
                }

                WarpCursorToClientCenter();
            }

            return;
        }

        if (_panDragging)
        {
            Vector3 forward = GetHorizontalForward();
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            float panScale = MathF.Max(0.02f, _cameraDistanceM * 0.0018f);
            _cameraTargetM += (-right * delta.X + forward * delta.Y) * panScale;
            _followSelection = false;
        }
    }

    private void OnMouseWheelInternal(object? sender, MouseEventArgs eventArgs)
    {
        if (_appState != SimulatorAppState.InMatch)
        {
            return;
        }

        float zoomAmount = eventArgs.Delta > 0 ? 0.90f : 1.10f;
        _cameraDistanceM = Math.Clamp(_cameraDistanceM * zoomAmount, 3.0f, 250f);
    }

    private void OnKeyDownInternal(object? sender, KeyEventArgs eventArgs)
    {
        bool isNewPress = _heldKeys.Add(eventArgs.KeyCode);
        if (_appState == SimulatorAppState.InMatch)
        {
            eventArgs.SuppressKeyPress = true;
            eventArgs.Handled = true;
        }

        if (!isNewPress)
        {
            return;
        }

        if (_appState == SimulatorAppState.InMatch && eventArgs.KeyCode == Keys.Space)
        {
            _pendingJumpRequest = true;
        }

        if (_appState == SimulatorAppState.InMatch && eventArgs.KeyCode == Keys.B)
        {
            _buyAmmoRequested = true;
        }

        switch (_appState)
        {
            case SimulatorAppState.MainMenu:
                HandleMainMenuKey(eventArgs);
                break;
            case SimulatorAppState.Lobby:
                HandleLobbyKey(eventArgs);
                break;
            case SimulatorAppState.InMatch:
                HandleInMatchKey(eventArgs);
                break;
        }

        Invalidate();
    }

    private void OnKeyUpInternal(object? sender, KeyEventArgs eventArgs)
    {
        _heldKeys.Remove(eventArgs.KeyCode);
        if (_appState == SimulatorAppState.InMatch)
        {
            eventArgs.SuppressKeyPress = true;
            eventArgs.Handled = true;
        }
    }

    private void HandleMainMenuKey(KeyEventArgs eventArgs)
    {
        switch (eventArgs.KeyCode)
        {
            case Keys.Enter:
                _appState = SimulatorAppState.Lobby;
                break;
            case Keys.Escape:
                Close();
                break;
            case Keys.Left:
            case Keys.PageUp:
                _host.CycleMapPreset(-1);
                ResetCameraForMap();
                break;
            case Keys.Right:
            case Keys.PageDown:
                _host.CycleMapPreset(1);
                ResetCameraForMap();
                break;
            case Keys.D1:
                _host.SetRendererMode("opengl");
                break;
            case Keys.D2:
                _host.SetRendererMode("moderngl");
                break;
            case Keys.D3:
                _host.SetRendererMode("native_cpp");
                break;
            case Keys.F7:
                LaunchDecisionDeploymentProgram();
                break;
        }
    }

    private void HandleLobbyKey(KeyEventArgs eventArgs)
    {
        switch (eventArgs.KeyCode)
        {
            case Keys.Enter:
                StartMatch();
                break;
            case Keys.Escape:
                _appState = SimulatorAppState.MainMenu;
                break;
            case Keys.Tab:
                _host.CycleSelectedEntity(eventArgs.Shift ? -1 : 1);
                break;
            case Keys.T:
                _host.SetSelectedTeam(_host.SelectedTeam.Equals("red", StringComparison.OrdinalIgnoreCase) ? "blue" : "red");
                SelectLobbyRole(ResolveLobbySelectedRoleKey());
                break;
            case Keys.I:
                _host.SetInfantryMode(_host.InfantryMode == "full" ? "balance" : "full");
                SelectLobbyRole("infantry");
                break;
            case Keys.R:
                _host.ToggleRicochet();
                break;
            case Keys.F6:
                _host.ReloadDecisionDeploymentProfile();
                break;
            case Keys.Left:
            case Keys.PageUp:
                _host.CycleMapPreset(-1);
                ResetCameraForMap();
                break;
            case Keys.Right:
            case Keys.PageDown:
                _host.CycleMapPreset(1);
                ResetCameraForMap();
                break;
            case Keys.F7:
                LaunchDecisionDeploymentProgram();
                break;
        }
    }

    private void HandleInMatchKey(KeyEventArgs eventArgs)
    {
        switch (eventArgs.KeyCode)
        {
            case Keys.P:
                _paused = !_paused;
                UpdateMouseCaptureState();
                break;
            case Keys.N:
                if (_paused)
                {
                    _host.Step(BuildPlayerControlState(forceEnable: true));
                }
                break;
            case Keys.R:
                _host.ResetWorld();
                ResetCameraForMap();
                SnapCameraToSelectedEntity();
                break;
            case Keys.Tab:
                _host.CycleSelectedEntity(eventArgs.Shift ? -1 : 1);
                break;
            case Keys.F:
                _followSelection = true;
                SnapCameraToSelectedEntity();
                break;
            case Keys.V:
                ToggleViewMode();
                break;
            case Keys.PageUp:
                _host.CycleMapPreset(1);
                ResetCameraForMap();
                break;
            case Keys.PageDown:
                _host.CycleMapPreset(-1);
                ResetCameraForMap();
                break;
            case Keys.D1:
                _host.SetRendererMode("opengl");
                break;
            case Keys.D2:
                _host.SetRendererMode("moderngl");
                break;
            case Keys.D3:
                _host.SetRendererMode("native_cpp");
                break;
            case Keys.F6:
                _host.ReloadDecisionDeploymentProfile();
                break;
            case Keys.F1:
                _showDebugSidebars = !_showDebugSidebars;
                break;
            case Keys.F4:
                _showProjectileTrails = !_showProjectileTrails;
                break;
            case Keys.F7:
                LaunchDecisionDeploymentProgram();
                break;
            case Keys.Escape:
                _paused = !_paused;
                UpdateMouseCaptureState();
                break;
            case Keys.L:
                ReturnToLobby();
                break;
        }
    }

    private void DrawBackground(Graphics graphics)
    {
        using var upperBrush = new LinearGradientBrush(
            new Point(0, 0),
            new Point(0, ClientSize.Height),
            Color.FromArgb(28, 34, 48),
            Color.FromArgb(14, 18, 28));
        graphics.FillRectangle(upperBrush, ClientRectangle);

        using var haloBrush = new SolidBrush(Color.FromArgb(24, 110, 160, 190));
        graphics.FillEllipse(haloBrush, -200, -120, ClientSize.Width + 400, ClientSize.Height / 2);
    }

    private void DrawMainMenu(Graphics graphics)
    {
        string title = "RM26 3D Simulator";
        string subtitle = "C# runtime entry";
        SizeF titleSize = graphics.MeasureString(title, _menuTitleFont);
        graphics.DrawString(title, _menuTitleFont, Brushes.White, (ClientSize.Width - titleSize.Width) * 0.5f, 68f);
        graphics.DrawString(subtitle, _menuSubtitleFont, Brushes.Gainsboro, (ClientSize.Width - 220) * 0.5f, 118f);

        Rectangle panel = new((ClientSize.Width - 760) / 2, 150, 760, 520);
        DrawPanel(graphics, panel);

        graphics.DrawString("Renderer", _menuSubtitleFont, Brushes.Gainsboro, panel.X + 36, panel.Y + 26);
        Rectangle backendOpenGl = new(panel.X + 36, panel.Y + 56, 140, 40);
        Rectangle backendModernGl = new(backendOpenGl.Right + 14, panel.Y + 56, 140, 40);
        Rectangle backendNative = new(backendModernGl.Right + 14, panel.Y + 56, 140, 40);
        DrawButton(graphics, backendOpenGl, "OpenGL", "menu_backend:opengl", _host.ActiveRendererMode == "opengl");
        DrawButton(graphics, backendModernGl, "ModernGL", "menu_backend:moderngl", _host.ActiveRendererMode == "moderngl");
        DrawButton(graphics, backendNative, "Native C++", "menu_backend:native_cpp", _host.ActiveRendererMode == "native_cpp");

        graphics.DrawString("Map Preset", _menuSubtitleFont, Brushes.Gainsboro, panel.X + 36, panel.Y + 126);
        Rectangle mapPrev = new(panel.X + 36, panel.Y + 156, 44, 40);
        Rectangle mapLabel = new(panel.X + 90, panel.Y + 156, panel.Width - 180, 40);
        Rectangle mapNext = new(panel.Right - 80, panel.Y + 156, 44, 40);
        DrawButton(graphics, mapPrev, "<", "menu_map_prev", false);
        DrawButton(graphics, mapNext, ">", "menu_map_next", false);
        using (var labelBrush = new SolidBrush(Color.FromArgb(44, 52, 66)))
        using (var borderPen = new Pen(Color.FromArgb(116, 132, 150), 1f))
        {
            graphics.FillRectangle(labelBrush, mapLabel);
            graphics.DrawRectangle(borderPen, mapLabel);
        }
        StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(_host.ActiveMapPreset, _menuSubtitleFont, Brushes.WhiteSmoke, mapLabel, centered);

        string[] lines =
        {
            "Pick map and appearance, then enter lobby to choose team and role.",
            "In match: V view, ESC pause, RMB auto aim, Space jump, B resupply.",
            "Keyboard shortcuts: D1/D2/D3 backend, PgUp/PgDn map.",
        };
        float textY = panel.Y + 226;
        foreach (string line in lines)
        {
            graphics.DrawString(line, _menuSubtitleFont, Brushes.LightGray, panel.X + 36, textY);
            textY += 26;
        }

        int editorTop = panel.Y + 328;
        int editorGap = 12;
        int editorWidth = (panel.Width - 72 - editorGap) / 2;
        Rectangle openTerrainEditor = new(panel.X + 36, editorTop, editorWidth, 34);
        Rectangle openAppearanceEditor = new(openTerrainEditor.Right + editorGap, editorTop, editorWidth, 34);
        Rectangle openLobby = new(panel.X + 36, panel.Bottom - 104, panel.Width - 72, 44);
        Rectangle exit = new(panel.X + 36, panel.Bottom - 62, panel.Width - 72, 36);
        DrawButton(graphics, openTerrainEditor, "Open Terrain Editor", "menu_open_terrain_editor", false, Color.FromArgb(86, 110, 166));
        DrawButton(graphics, openAppearanceEditor, "Open Appearance Editor", "menu_open_appearance_editor", false, Color.FromArgb(86, 110, 166));
        DrawButton(graphics, openLobby, "Enter Lobby", "menu_open_lobby", true, Color.FromArgb(52, 132, 226));
        DrawButton(graphics, exit, "Exit", "menu_exit", false);
    }

    private void DrawLobby(Graphics graphics)
    {
        string title = "Pre-match Lobby";
        SizeF titleSize = graphics.MeasureString(title, _menuTitleFont);
        graphics.DrawString(title, _menuTitleFont, Brushes.White, (ClientSize.Width - titleSize.Width) * 0.5f, 56f);

        int panelHeight = Math.Min(620, Math.Max(560, ClientSize.Height - 160));
        int panelY = Math.Max(108, (ClientSize.Height - panelHeight) / 2);
        Rectangle panel = new((ClientSize.Width - 840) / 2, panelY, 840, panelHeight);
        DrawPanel(graphics, panel);

        graphics.DrawString("Team", _menuSubtitleFont, Brushes.Gainsboro, panel.X + 30, panel.Y + 24);
        Rectangle redTeam = new(panel.X + 30, panel.Y + 52, 110, 38);
        Rectangle blueTeam = new(redTeam.Right + 12, panel.Y + 52, 110, 38);
        DrawButton(graphics, redTeam, "Red", "lobby_team:red", _host.SelectedTeam == "red", Color.FromArgb(174, 66, 66));
        DrawButton(graphics, blueTeam, "Blue", "lobby_team:blue", _host.SelectedTeam == "blue", Color.FromArgb(64, 112, 200));

        graphics.DrawString("Map", _menuSubtitleFont, Brushes.Gainsboro, panel.Right - 264, panel.Y + 24);
        Rectangle mapPrev = new(panel.Right - 264, panel.Y + 52, 44, 38);
        Rectangle mapLabel = new(panel.Right - 210, panel.Y + 52, 156, 38);
        Rectangle mapNext = new(panel.Right - 44, panel.Y + 52, 44, 38);
        DrawButton(graphics, mapPrev, "<", "menu_map_prev", false);
        DrawButton(graphics, mapNext, ">", "menu_map_next", false);
        using (var labelBrush = new SolidBrush(Color.FromArgb(44, 52, 66)))
        using (var borderPen = new Pen(Color.FromArgb(116, 132, 150), 1f))
        {
            graphics.FillRectangle(labelBrush, mapLabel);
            graphics.DrawRectangle(borderPen, mapLabel);
        }
        StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(_host.ActiveMapPreset, _menuSubtitleFont, Brushes.WhiteSmoke, mapLabel, centered);

        graphics.DrawString("Controlled Role", _menuSubtitleFont, Brushes.Gainsboro, panel.X + 30, panel.Y + 122);
        string selectedRole = ResolveLobbySelectedRoleKey();
        (string RoleKey, string Label, Color Color)[] roleButtons =
        {
            ("hero", "Hero", Color.FromArgb(112, 126, 232)),
            ("engineer", "Engineer", Color.FromArgb(92, 172, 126)),
            ("infantry", "Infantry", Color.FromArgb(196, 132, 82)),
            ("sentry", "Sentry", Color.FromArgb(132, 110, 198)),
        };
        int roleButtonWidth = 180;
        int roleButtonHeight = 44;
        int roleGap = 12;
        for (int index = 0; index < roleButtons.Length; index++)
        {
            Rectangle button = new(
                panel.X + 30 + index * (roleButtonWidth + roleGap),
                panel.Y + 150,
                roleButtonWidth,
                roleButtonHeight);
            DrawButton(
                graphics,
                button,
                roleButtons[index].Label,
                $"lobby_pick_role:{roleButtons[index].RoleKey}",
                string.Equals(selectedRole, roleButtons[index].RoleKey, StringComparison.OrdinalIgnoreCase),
                roleButtons[index].Color);
        }

        if (string.Equals(selectedRole, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            graphics.DrawString("Infantry Model", _menuSubtitleFont, Brushes.Gainsboro, panel.X + 30, panel.Y + 214);
            Rectangle infantryFull = new(panel.X + 30, panel.Y + 242, 150, 38);
            Rectangle infantryBalance = new(infantryFull.Right + 12, panel.Y + 242, 170, 38);
            DrawButton(graphics, infantryFull, "Full", "lobby_infantry_mode:full", _host.InfantryMode == "full", Color.FromArgb(94, 132, 88));
            DrawButton(graphics, infantryBalance, "Balance", "lobby_infantry_mode:balance", _host.InfantryMode == "balance", Color.FromArgb(86, 146, 112));
        }

        int configY = panel.Y + 214;
        int leftConfigX = panel.X + 380;
        graphics.DrawString("Rule Config", _menuSubtitleFont, Brushes.Gainsboro, leftConfigX, configY);
        DrawLobbyOptionRow(
            graphics,
            leftConfigX,
            configY + 28,
            "Hero",
            new[]
            {
                ("Range", "lobby_hero_mode:ranged_priority", _host.HeroPerformanceMode == "ranged_priority"),
                ("Melee", "lobby_hero_mode:melee_priority", _host.HeroPerformanceMode == "melee_priority"),
            });
        DrawLobbyOptionRow(
            graphics,
            leftConfigX,
            configY + 62,
            "Inf Chassis",
            new[]
            {
                ("HP", "lobby_infantry_durability:hp_priority", _host.InfantryDurabilityMode == "hp_priority"),
                ("Power", "lobby_infantry_durability:power_priority", _host.InfantryDurabilityMode == "power_priority"),
            });
        DrawLobbyOptionRow(
            graphics,
            leftConfigX,
            configY + 96,
            "Inf Weapon",
            new[]
            {
                ("Cool", "lobby_infantry_weapon:cooling_priority", _host.InfantryWeaponMode == "cooling_priority"),
                ("Burst", "lobby_infantry_weapon:burst_priority", _host.InfantryWeaponMode == "burst_priority"),
            });
        DrawLobbyOptionRow(
            graphics,
            leftConfigX,
            configY + 130,
            "Sentry",
            new[]
            {
                ("Full", "lobby_sentry_control:full_auto", _host.SentryControlMode == "full_auto"),
                ("Semi", "lobby_sentry_control:semi_auto", _host.SentryControlMode == "semi_auto"),
            });
        DrawLobbyOptionRow(
            graphics,
            leftConfigX,
            configY + 164,
            "Stance",
            new[]
            {
                ("Atk", "lobby_sentry_stance:attack", _host.SentryStance == "attack"),
                ("Move", "lobby_sentry_stance:move", _host.SentryStance == "move"),
                ("Def", "lobby_sentry_stance:defense", _host.SentryStance == "defense"),
            });

        SimulationEntity? selectedEntity = _host.SelectedEntity;
        Rectangle preview = new(panel.X + 30, panel.Y + 418, panel.Width - 60, 98);
        DrawCard(graphics, preview, true);
        if (selectedEntity is not null)
        {
            string team = selectedEntity.Team.Equals("red", StringComparison.OrdinalIgnoreCase) ? "Red" : "Blue";
            string role = ResolveRoleLabel(selectedEntity);
            string subtype = string.Equals(selectedRole, "infantry", StringComparison.OrdinalIgnoreCase)
                ? (_host.InfantryMode == "balance" ? "Balance model" : "Full model")
                : "Standard model";
            graphics.DrawString(selectedEntity.Id, _menuTitleFont, Brushes.WhiteSmoke, preview.X + 18, preview.Y + 16);
            graphics.DrawString($"{team} | {role} | {subtype}", _hudMidFont, Brushes.Gainsboro, preview.X + 18, preview.Y + 52);
            graphics.DrawString(
                $"HP {selectedEntity.Health:0}/{selectedEntity.MaxHealth:0}   Ammo {(string.Equals(selectedEntity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? selectedEntity.Ammo42Mm : selectedEntity.Ammo17Mm)}   Power {(int)selectedEntity.Power}/{(int)selectedEntity.MaxPower}",
                _smallHudFont,
                Brushes.LightGray,
                preview.X + 18,
                preview.Y + 78);
        }
        else
        {
            graphics.DrawString("No controllable unit selected.", _menuSubtitleFont, Brushes.LightGray, preview.X + 18, preview.Y + 48);
        }

        Rectangle openTerrainEditor = new(panel.X + 30, preview.Bottom + 18, 180, 38);
        Rectangle openAppearanceEditor = new(openTerrainEditor.Right + 12, preview.Bottom + 18, 200, 38);
        Rectangle ricochet = new(openAppearanceEditor.Right + 12, preview.Bottom + 18, 190, 38);
        Rectangle back = new(panel.X + 30, panel.Bottom - 72, 180, 38);
        Rectangle start = new(panel.Right - 230, panel.Bottom - 82, 200, 48);

        DrawButton(graphics, openTerrainEditor, "Open Terrain Editor", "menu_open_terrain_editor", false, Color.FromArgb(86, 110, 166));
        DrawButton(graphics, openAppearanceEditor, "Open Appearance Editor", "menu_open_appearance_editor", false, Color.FromArgb(86, 110, 166));
        DrawButton(
            graphics,
            ricochet,
            _host.RicochetEnabled ? "Ricochet: On" : "Ricochet: Off",
            "lobby_toggle_ricochet",
            _host.RicochetEnabled,
            Color.FromArgb(60, 130, 205));
        DrawButton(graphics, back, "Back", "lobby_back_main", false);
        DrawButton(graphics, start, "Start Match", "lobby_start_match", true, Color.FromArgb(52, 132, 226));

        graphics.DrawString("Keyboard: Enter start, Esc back, T switch team, I infantry model, R toggle ricochet.", _smallHudFont, Brushes.LightGray, panel.X + 30, panel.Bottom - 24);
    }

    private void DrawLobbyOptionRow(
        Graphics graphics,
        int x,
        int y,
        string label,
        IReadOnlyList<(string Text, string Action, bool Selected)> options)
    {
        using var labelBrush = new SolidBrush(Color.FromArgb(214, 222, 230));
        graphics.DrawString(label, _tinyHudFont, labelBrush, x, y + 7);
        int buttonX = x + 92;
        foreach ((string text, string action, bool selected) in options)
        {
            Rectangle rect = new(buttonX, y, 76, 28);
            DrawButton(graphics, rect, text, action, selected, Color.FromArgb(76, 116, 178));
            buttonX += 84;
        }
    }

    private void DrawMatchToolbar(Graphics graphics)
    {
        Rectangle toolbar = new(0, 0, ClientSize.Width, ToolbarHeight);
        using (var toolbarBrush = new SolidBrush(Color.FromArgb(244, 25, 30, 38)))
        using (var borderPen = new Pen(Color.FromArgb(92, 106, 118), 1f))
        {
            graphics.FillRectangle(toolbarBrush, toolbar);
            graphics.DrawLine(borderPen, toolbar.Left, toolbar.Bottom - 1, toolbar.Right, toolbar.Bottom - 1);
        }

        using var titleBrush = new SolidBrush(Color.FromArgb(245, 247, 250));
        using var subtitleBrush = new SolidBrush(Color.FromArgb(200, 212, 224));
        graphics.DrawString("RM26 ARTINX-Asoul模拟器", _hudMidFont, titleBrush, 16f, 12f);

        string modeText = _host.IsSingleUnitTestMode
            ? $"单兵种测试 | 主控 {_host.SingleUnitTestFocusId}"
            : "完整模式";
        graphics.DrawString(modeText, _tinyHudFont, subtitleBrush, 16f, 32f);

        int buttonY = 11;
        int right = ClientSize.Width - 12;

        Rectangle lobby = new(right - 108, buttonY, 108, 32);
        right = lobby.Left - 8;
        Rectangle reset = new(right - 92, buttonY, 92, 32);
        right = reset.Left - 8;
        Rectangle pause = new(right - 102, buttonY, 102, 32);
        right = pause.Left - 8;
        Rectangle panels = new(right - 94, buttonY, 94, 32);
        right = panels.Left - 8;
        Rectangle decision = new(right - 126, buttonY, 126, 32);

        DrawButton(graphics, decision, "F7 Decision", "match_open_decision_deployment", false, Color.FromArgb(74, 100, 156));
        DrawButton(graphics, panels, _showDebugSidebars ? "Hide F1" : "Panel F1", "match_toggle_debug_sidebars", _showDebugSidebars, Color.FromArgb(86, 110, 166));
        DrawButton(graphics, pause, _paused ? "继续" : "暂停", "match_toggle_pause", _paused, Color.FromArgb(62, 130, 206));
        DrawButton(graphics, reset, "重置", "match_reset_world", false, Color.FromArgb(92, 98, 112));
        DrawButton(graphics, lobby, "返回大厅", "match_return_lobby", false, Color.FromArgb(92, 98, 112));
    }

    private string ResolveLobbySelectedRoleKey()
    {
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is null)
        {
            return "hero";
        }

        return selected.RoleKey.ToLowerInvariant() switch
        {
            "hero" => "hero",
            "engineer" => "engineer",
            "sentry" => "sentry",
            _ => "infantry",
        };
    }

    private void SelectLobbyRole(string? roleKey)
    {
        string normalized = (roleKey ?? string.Empty).Trim().ToLowerInvariant();
        string team = _host.SelectedTeam;
        string entityId = normalized switch
        {
            "hero" => $"{team}_robot_1",
            "engineer" => $"{team}_robot_2",
            "sentry" => $"{team}_robot_7",
            _ => $"{team}_robot_3",
        };
        _host.SetSelectedEntity(entityId);
    }

    private void DrawDecisionDeploymentPanel(Graphics graphics)
    {
        if (_appState != SimulatorAppState.InMatch)
        {
            return;
        }

        int panelTop = ToolbarHeight + HudHeight;
        int panelHeight = Math.Max(160, ClientSize.Height - panelTop);
        Rectangle controlPanel = new(ClientSize.Width - SidebarWidth, panelTop, SidebarWidth, panelHeight);
        DrawMatchControlSidebar(graphics, controlPanel);

        if (_host.IsSingleUnitTestMode)
        {
            Rectangle decisionPanel = new(controlPanel.Left - DecisionSidebarWidth, panelTop, DecisionSidebarWidth, panelHeight);
            DrawSingleUnitDecisionSidebar(graphics, decisionPanel);
        }
    }

    private void DrawHud(Graphics graphics)
    {
        Rectangle hudRect = new(0, ToolbarHeight, ClientSize.Width, HudHeight);
        using (var hudBrush = new SolidBrush(Color.FromArgb(238, 32, 37, 45)))
        {
            graphics.FillRectangle(hudBrush, hudRect);
        }

        int centerX = ClientSize.Width / 2;
        Rectangle centerPanel = new(centerX - 102, ToolbarHeight + 8, 204, 92);
        using (var centerBrush = new SolidBrush(Color.FromArgb(245, 65, 76, 84)))
        using (var centerPen = new Pen(Color.FromArgb(185, 110, 122, 136), 1f))
        {
            graphics.FillRectangle(centerBrush, centerPanel);
            graphics.DrawRectangle(centerPen, centerPanel);
        }

        double remaining = Math.Max(0.0, _host.GameDurationSec - _host.World.GameTimeSec);
        int remainingSeconds = (int)remaining;
        int minutes = remainingSeconds / 60;
        int seconds = remainingSeconds % 60;

        string roundText = _paused
            ? "已暂停"
            : (_host.World.GameTimeSec <= 0.02 ? "未开始" : "Round 1/5");
        double worldUnitsPerMeter = 1.0 / Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double autoAimDistanceWorld = _host.AutoAimMaxDistanceM / Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        string scaleText = $"比例尺 1m≈{worldUnitsPerMeter:0.00}单位 | 8m≈{autoAimDistanceWorld:0.0}";

        StringFormat centerFormat = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        using (var roundBrush = new SolidBrush(Color.FromArgb(225, 230, 236)))
        {
            graphics.DrawString(roundText, _tinyHudFont, roundBrush, new RectangleF(centerPanel.X, centerPanel.Y + 1, centerPanel.Width, 24), centerFormat);
        }

        using (var timerBrush = new SolidBrush(Color.White))
        {
            graphics.DrawString($"{minutes}:{seconds:00}", _hudBigFont, timerBrush, new RectangleF(centerPanel.X, centerPanel.Y + 27, centerPanel.Width, 34), centerFormat);
        }

        using (var scaleBrush = new SolidBrush(Color.FromArgb(192, 199, 206)))
        {
            graphics.DrawString(scaleText, _tinyHudFont, scaleBrush, new RectangleF(centerPanel.X, centerPanel.Bottom - 24, centerPanel.Width, 20), centerFormat);
        }

        Rectangle redRect = new(10, ToolbarHeight + 8, centerX - 120, 96);
        Rectangle blueRect = new(centerX + 110, ToolbarHeight + 8, ClientSize.Width - centerX - 120, 96);
        DrawTeamHudSection(graphics, "red", "红方", redRect);
        DrawTeamHudSection(graphics, "blue", "蓝方", blueRect);
    }

    private void DrawCrosshair(Graphics graphics)
    {
        float x = ClientSize.Width * 0.5f;
        float sceneTop = ToolbarHeight + HudHeight;
        float y = sceneTop + (ClientSize.Height - sceneTop) * 0.5f;

        using var shadowPen = new Pen(Color.FromArgb(145, 0, 0, 0), 3f);
        using var crossPen = new Pen(Color.FromArgb(230, 235, 68, 72), 1.5f);
        graphics.DrawLine(shadowPen, x - 12, y, x - 4, y);
        graphics.DrawLine(shadowPen, x + 4, y, x + 12, y);
        graphics.DrawLine(shadowPen, x, y - 12, x, y - 4);
        graphics.DrawLine(shadowPen, x, y + 4, x, y + 12);
        graphics.DrawLine(crossPen, x - 12, y, x - 4, y);
        graphics.DrawLine(crossPen, x + 4, y, x + 12, y);
        graphics.DrawLine(crossPen, x, y - 12, x, y - 4);
        graphics.DrawLine(crossPen, x, y + 4, x, y + 12);
        graphics.FillEllipse(Brushes.WhiteSmoke, x - 1.5f, y - 1.5f, 3f, 3f);
    }

    private void DrawPlayerStatusPanel(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null)
        {
            return;
        }

        Rectangle panel = new(24, ClientSize.Height - 122, 390, 98);
        using GraphicsPath path = CreateRoundedRectangle(panel, 6);
        using var fill = new SolidBrush(Color.FromArgb(224, 13, 19, 26));
        using var border = new Pen(Color.FromArgb(170, 122, 146, 168), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        Color teamColor = ResolveTeamColor(entity.Team);
        using var teamBrush = new SolidBrush(teamColor);
        graphics.FillRectangle(teamBrush, panel.X, panel.Y, 5, panel.Height);

        string roleLabel = ResolveRoleLabel(entity);
        string controlLabel = entity.IsPlayerControlled || string.Equals(entity.Id, _host.SelectedEntity?.Id, StringComparison.OrdinalIgnoreCase)
            ? "玩家接管"
            : "AI 决策";
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        using var textBrush = new SolidBrush(Color.FromArgb(206, 216, 226));
        graphics.DrawString($"{entity.Id}  {roleLabel}  {controlLabel}", _smallHudFont, titleBrush, panel.X + 16, panel.Y + 10);

        float barX = panel.X + 16;
        float barY = panel.Y + 35;
        (float powerRatio, string powerLabel) = ResolvePowerGauge(entity);
        DrawMiniGauge(graphics, new RectangleF(barX, barY, 112, 10), entity.MaxHealth <= 0 ? 0f : (float)(entity.Health / entity.MaxHealth), Color.FromArgb(72, 214, 126), $"HP {(int)entity.Health}/{(int)entity.MaxHealth}");
        DrawMiniGauge(graphics, new RectangleF(barX + 126, barY, 96, 10), powerRatio, Color.FromArgb(75, 146, 232), powerLabel);
        DrawMiniGauge(graphics, new RectangleF(barX + 236, barY, 96, 10), entity.MaxHeat <= 0 ? 0f : (float)(entity.Heat / entity.MaxHeat), Color.FromArgb(228, 130, 58), $"H {(int)entity.Heat}");

        double speedMps = Math.Sqrt(
            entity.VelocityXWorldPerSec * entity.VelocityXWorldPerSec
            + entity.VelocityYWorldPerSec * entity.VelocityYWorldPerSec) * Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        string ammoText = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? $"42mm {entity.Ammo42Mm}"
            : $"17mm {entity.Ammo17Mm}";
        string motionText = $"弹药 {ammoText}   速度 {speedMps:0.0}m/s   云台 {entity.GimbalPitchDeg:0}°";
        graphics.DrawString(motionText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 54);

        string statusText = $"决策 {FormatDecisionLabelShort(entity.AiDecisionSelected, entity.AiDecision)}   Shift 上台阶   Ctrl/LMB 开火";
        graphics.DrawString(statusText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 73);
    }

    private void DrawPlayerStatusPanelV2(Graphics graphics)
    {
        if (_titleFont is not null)
        {
            DrawPlayerStatusPanelModern(graphics);
            return;
        }

        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null)
        {
            return;
        }

        Rectangle panel = new(24, ClientSize.Height - 122, 420, 98);
        using GraphicsPath path = CreateRoundedRectangle(panel, 6);
        using var fill = new SolidBrush(Color.FromArgb(224, 13, 19, 26));
        using var border = new Pen(Color.FromArgb(170, 122, 146, 168), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var teamBrush = new SolidBrush(ResolveTeamColor(entity.Team));
        graphics.FillRectangle(teamBrush, panel.X, panel.Y, 5, panel.Height);

        string roleLabel = ResolveRoleLabel(entity);
        string controlLabel = entity.IsPlayerControlled ? "玩家接管" : "AI 决策";
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        using var textBrush = new SolidBrush(Color.FromArgb(206, 216, 226));
        graphics.DrawString($"{entity.Id}  {roleLabel}  {controlLabel}", _smallHudFont, titleBrush, panel.X + 16, panel.Y + 10);

        float barX = panel.X + 16;
        float barY = panel.Y + 35;
        (float powerRatio, string powerLabel) = ResolvePowerGauge(entity);
        DrawMiniGauge(graphics, new RectangleF(barX, barY, 112, 10), entity.MaxHealth <= 0 ? 0f : (float)(entity.Health / entity.MaxHealth), Color.FromArgb(72, 214, 126), $"HP {(int)entity.Health}/{(int)entity.MaxHealth}");
        DrawMiniGauge(graphics, new RectangleF(barX + 126, barY, 96, 10), powerRatio, Color.FromArgb(75, 146, 232), powerLabel);
        DrawMiniGauge(graphics, new RectangleF(barX + 236, barY, 96, 10), entity.MaxHeat <= 0 ? 0f : (float)(entity.Heat / entity.MaxHeat), Color.FromArgb(228, 130, 58), $"H {(int)entity.Heat}");

        double speedMps = Math.Sqrt(
            entity.VelocityXWorldPerSec * entity.VelocityXWorldPerSec
            + entity.VelocityYWorldPerSec * entity.VelocityYWorldPerSec) * Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        string ammoText = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? $"42mm {entity.Ammo42Mm}"
            : $"17mm {entity.Ammo17Mm}";
        string motionText = $"弹药 {ammoText}   速度 {speedMps:0.0}m/s   云台 {entity.GimbalPitchDeg:0}°";
        graphics.DrawString(motionText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 54);

        bool inFriendlySupply = _host.MapPreset.Facilities.Any(region =>
            (string.Equals(region.Type, "supply", StringComparison.OrdinalIgnoreCase)
                || string.Equals(region.Type, "buff_supply", StringComparison.OrdinalIgnoreCase))
            && string.Equals(region.Team, entity.Team, StringComparison.OrdinalIgnoreCase)
            && region.Contains(entity.X, entity.Y));
        string supplyPrompt = inFriendlySupply ? "  B 补弹" : string.Empty;
        string statusText = $"锁定 {(entity.AutoAimLocked ? "装甲" : "待机")}   右键自瞄  左键射击  Shift 小陀螺{supplyPrompt}";
        graphics.DrawString(statusText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 73);
    }

    private void DrawMiniGauge(Graphics graphics, RectangleF rect, float ratio, Color fillColor, string label)
    {
        float clamped = Math.Clamp(ratio, 0f, 1f);
        using var back = new SolidBrush(Color.FromArgb(130, 42, 48, 58));
        using var fill = new SolidBrush(Color.FromArgb(220, fillColor));
        using var border = new Pen(Color.FromArgb(110, 190, 202, 214), 1f);
        graphics.FillRectangle(back, rect);
        graphics.FillRectangle(fill, rect.X, rect.Y, rect.Width * clamped, rect.Height);
        graphics.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
        graphics.DrawString(label, _tinyHudFont, Brushes.WhiteSmoke, rect.X, rect.Y - 12);
    }

    private static (float Ratio, string Label) ResolvePowerGauge(SimulationEntity entity)
    {
        double limit = Math.Max(1.0, entity.ChassisDrivePowerLimitW);
        double draw = Math.Max(0.0, entity.ChassisPowerDrawW);
        float ratio = (float)Math.Clamp(draw / limit, 0.0, 1.0);
        return (ratio, $"P {draw:0}/{limit:0}W");
    }

    private void DrawTeamHudSection(Graphics graphics, string teamKey, string teamLabel, Rectangle rect)
    {
        Color teamColor = ResolveTeamColor(teamKey);
        using (var panelBrush = new SolidBrush(Color.FromArgb(238, 48, 54, 64)))
        using (var panelPen = new Pen(Color.FromArgb(170, 98, 108, 120), 1f))
        {
            graphics.FillRectangle(panelBrush, rect);
            graphics.DrawRectangle(panelPen, rect);
        }

        Rectangle banner = new(rect.X + 8, rect.Y + 8, rect.Width - 16, 24);
        using (var bannerBrush = new SolidBrush(teamColor))
        {
            graphics.FillRectangle(bannerBrush, banner);
        }

        double gold = _host.World.Teams.TryGetValue(teamKey, out SimulationTeamState? teamState) ? teamState.Gold : 0.0;
        StringFormat centerFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString($"{teamLabel}  金币 {(int)gold}", _hudMidFont, Brushes.White, banner, centerFormat);

        SimulationEntity? baseEntity = FindEntityById($"{teamKey}_base");
        SimulationEntity? outpostEntity = FindEntityById($"{teamKey}_outpost");
        string structureText =
            $"基地 {(int)(baseEntity?.Health ?? 0)}/{(int)(baseEntity?.MaxHealth ?? 0)}   前哨站 {(int)(outpostEntity?.Health ?? 0)}/{(int)(outpostEntity?.MaxHealth ?? 0)}";
        using (var structureBrush = new SolidBrush(Color.FromArgb(232, 236, 242)))
        {
            graphics.DrawString(structureText, _tinyHudFont, structureBrush, rect.X + 12, rect.Y + 40);
        }

        IReadOnlyList<SimulationEntity> units = BuildTeamHudUnits(teamKey);
        int unitAreaY = rect.Y + 56;
        int unitCardWidth = Math.Max(56, (rect.Width - 20) / Math.Max(1, units.Count));

        for (int index = 0; index < units.Count; index++)
        {
            SimulationEntity unit = units[index];
            Rectangle card = new(rect.X + 8 + index * unitCardWidth, unitAreaY, unitCardWidth - 6, 36);
            bool isSelected = string.Equals(_host.SelectedEntity?.Id, unit.Id, StringComparison.OrdinalIgnoreCase);
            Color borderColor = unit.IsAlive ? teamColor : Color.FromArgb(128, 128, 128);
            using (var cardBrush = new SolidBrush(Color.FromArgb(236, 28, 33, 41)))
            using (var borderPen = new Pen(isSelected ? Color.FromArgb(231, 180, 58) : borderColor, isSelected ? 2f : 1f))
            {
                graphics.FillRectangle(cardBrush, card);
                graphics.DrawRectangle(borderPen, card);
            }

            string entityKey = ExtractEntityKey(unit.Id);
            string label = HudUnitLabelMap.TryGetValue(entityKey, out string? mappedLabel) ? mappedLabel : ResolveRoleLabel(unit);
            string hpText = $"{(int)unit.Health}";
            string levelText = $"Lv{Math.Max(1, unit.Level)}";
            string nodeText = FormatDecisionLabelShort(unit.AiDecisionSelected, unit.AiDecision);

            graphics.DrawString(label, _tinyHudFont, Brushes.White, card.X + 6, card.Y + 1);
            using (var hpBrush = new SolidBrush(unit.IsAlive ? Color.FromArgb(218, 182, 81) : Color.FromArgb(128, 128, 128)))
            {
                graphics.DrawString(hpText, _tinyHudFont, hpBrush, card.X + 6, card.Y + 12);
            }

            SizeF lvSize = graphics.MeasureString(levelText, _tinyHudFont);
            graphics.DrawString(levelText, _tinyHudFont, Brushes.White, card.Right - lvSize.Width - 6, card.Y + 12);
            graphics.DrawString(nodeText, _tinyHudFont, Brushes.White, card.X + 6, card.Y + 22);

            if (EntityHasBarrel(unit))
            {
                using var barrelBrush = new SolidBrush(Color.FromArgb(76, 164, 104));
                graphics.FillEllipse(barrelBrush, card.Right - 15, card.Y + 6, 6, 6);
            }

            _uiButtons.Add(new UiButton(card, $"match_select:{unit.Id}"));
        }
    }

    private void DrawMatchControlSidebar(Graphics graphics, Rectangle panel)
    {
        using (var panelBrush = new SolidBrush(Color.FromArgb(244, 247, 248, 250)))
        using (var leftBorderPen = new Pen(Color.FromArgb(207, 212, 219), 1f))
        {
            graphics.FillRectangle(panelBrush, panel);
            graphics.DrawLine(leftBorderPen, panel.Left, panel.Top, panel.Left, panel.Bottom);
        }

        float y = panel.Y + 16;
        using var titleBrush = new SolidBrush(Color.FromArgb(34, 40, 49));
        using var textBrush = new SolidBrush(Color.FromArgb(34, 40, 49));
        graphics.DrawString("对局控制", _titleFont, titleBrush, panel.X + 16, y);
        y += 30;

        graphics.DrawString("完整模式保留标准对局推进。", _tinyHudFont, textBrush, panel.X + 16, y);
        y += 18;
        graphics.DrawString("单兵种测试下仅主控兵种允许运动。", _tinyHudFont, textBrush, panel.X + 16, y);
        y += 24;

        graphics.DrawString("对局模式", _smallHudFont, textBrush, panel.X + 16, y);
        y += 26;
        Rectangle fullMode = new(panel.X + 16, (int)y, 92, 28);
        Rectangle singleMode = new(panel.X + 116, (int)y, 124, 28);
        DrawButton(graphics, fullMode, "完整", "lobby_mode:full", !_host.IsSingleUnitTestMode, Color.FromArgb(64, 108, 176));
        DrawButton(graphics, singleMode, "单兵种测试", "lobby_mode:single_unit_test", _host.IsSingleUnitTestMode, Color.FromArgb(108, 94, 188));
        y += 42;

        if (_host.IsSingleUnitTestMode)
        {
            graphics.DrawString("主控方", _smallHudFont, textBrush, panel.X + 16, y);
            y += 26;
            Rectangle redTeam = new(panel.X + 16, (int)y, 72, 26);
            Rectangle blueTeam = new(panel.X + 96, (int)y, 72, 26);
            DrawButton(graphics, redTeam, "红方", "lobby_team:red", string.Equals(_host.SingleUnitTestTeam, "red", StringComparison.OrdinalIgnoreCase), Color.FromArgb(174, 66, 66));
            DrawButton(graphics, blueTeam, "蓝方", "lobby_team:blue", string.Equals(_host.SingleUnitTestTeam, "blue", StringComparison.OrdinalIgnoreCase), Color.FromArgb(64, 112, 200));
            y += 36;

            graphics.DrawString("主控兵种", _smallHudFont, textBrush, panel.X + 16, y);
            y += 26;
            (string Key, string Label)[] specs =
            {
                ("robot_1", "英雄"),
                ("robot_2", "工程"),
                ("robot_3", "步兵1"),
                ("robot_4", "步兵2"),
                ("robot_7", "哨兵"),
            };

            int buttonWidth = 74;
            for (int index = 0; index < specs.Length; index++)
            {
                int row = index / 3;
                int col = index % 3;
                Rectangle roleRect = new(panel.X + 16 + col * (buttonWidth + 8), (int)y + row * 34, buttonWidth, 26);
                bool active = string.Equals(_host.SingleUnitTestEntityKey, specs[index].Key, StringComparison.OrdinalIgnoreCase);
                DrawButton(graphics, roleRect, specs[index].Label, $"lobby_focus_entity:{specs[index].Key}", active, Color.FromArgb(86, 120, 188));
            }

            y += 74;
            graphics.DrawString($"当前主控: {_host.SingleUnitTestFocusId}", _tinyHudFont, textBrush, panel.X + 16, y);
            y += 18;
            graphics.DrawString("当前决策与待办决策见左侧决策栏。", _tinyHudFont, textBrush, panel.X + 16, y);
            y += 22;
        }
        else
        {
            graphics.DrawString("当前不注入人工待办决策。", _tinyHudFont, textBrush, panel.X + 16, y);
            y += 18;
            graphics.DrawString("若要检查单兵种决策，请切换到单兵种测试。", _tinyHudFont, textBrush, panel.X + 16, y);
            y += 22;
        }

        graphics.DrawString("运行控制", _smallHudFont, textBrush, panel.X + 16, y);
        y += 26;
        Rectangle pauseRect = new(panel.X + 16, (int)y, panel.Width - 32, 28);
        DrawButton(graphics, pauseRect, _paused ? "继续对局" : "暂停对局", "match_toggle_pause", _paused, Color.FromArgb(60, 130, 205));
        y += 34;
        Rectangle resetRect = new(panel.X + 16, (int)y, panel.Width - 32, 28);
        DrawButton(graphics, resetRect, "重置对局", "match_reset_world", false, Color.FromArgb(86, 98, 112));
        y += 34;
        Rectangle lobbyRect = new(panel.X + 16, (int)y, panel.Width - 32, 28);
        DrawButton(graphics, lobbyRect, "返回大厅", "match_return_lobby", false, Color.FromArgb(86, 98, 112));
        y += 34;
        Rectangle reloadRect = new(panel.X + 16, (int)y, panel.Width - 32, 28);
        DrawButton(graphics, reloadRect, "F6 重载部署", "match_reload_deployment", false, Color.FromArgb(74, 100, 156));
        y += 40;

        if (y > panel.Bottom - 118)
        {
            return;
        }

        graphics.DrawString("部署模式", _smallHudFont, textBrush, panel.X + 16, y);
        y += 24;
        IReadOnlyDictionary<string, string> modes = _host.RoleDeploymentModes;
        foreach ((string role, string mode) in new[]
                 {
                     ("hero", modes.TryGetValue("hero", out string? heroMode) ? heroMode : "aggressive"),
                     ("engineer", modes.TryGetValue("engineer", out string? engineerMode) ? engineerMode : "support"),
                     ("infantry", modes.TryGetValue("infantry", out string? infantryMode) ? infantryMode : "aggressive"),
                     ("sentry", modes.TryGetValue("sentry", out string? sentryMode) ? sentryMode : "hold"),
                 })
        {
            if (y > panel.Bottom - 46)
            {
                break;
            }

            graphics.DrawString($"{ResolveRoleLabel(role)}: {ResolveDecisionModeLabel(mode)}", _tinyHudFont, textBrush, panel.X + 16, y);
            y += 18;
        }

        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is not null && y <= panel.Bottom - 24)
        {
            y += 6;
            graphics.DrawString($"当前单位: {selected.Id}", _tinyHudFont, textBrush, panel.X + 16, y);
            y += 16;
            graphics.DrawString($"实时决策: {FormatDecisionLabelShort(selected.AiDecisionSelected, selected.AiDecision)}", _tinyHudFont, textBrush, panel.X + 16, y);
        }
    }

    private void DrawSingleUnitDecisionSidebar(Graphics graphics, Rectangle panel)
    {
        using (var panelBrush = new SolidBrush(Color.FromArgb(244, 247, 248, 250)))
        using (var leftBorderPen = new Pen(Color.FromArgb(207, 212, 219), 1f))
        {
            graphics.FillRectangle(panelBrush, panel);
            graphics.DrawLine(leftBorderPen, panel.Left, panel.Top, panel.Left, panel.Bottom);
        }

        float y = panel.Y + 16;
        using var textBrush = new SolidBrush(Color.FromArgb(34, 40, 49));
        graphics.DrawString("决策可视化", _titleFont, textBrush, panel.X + 16, y);
        y += 30;

        SimulationEntity? focus = _host.SingleUnitTestFocusEntity;
        string currentDecision = focus?.AiDecisionSelected ?? string.Empty;
        string forcedDecision = focus?.TestForcedDecisionId ?? string.Empty;
        string summaryDecision = FormatDecisionLabelShort(focus?.AiDecisionSelected ?? string.Empty, focus?.AiDecision ?? string.Empty);

        graphics.DrawString("当下决策", _smallHudFont, textBrush, panel.X + 16, y);
        y += 24;
        graphics.DrawString($"主控实体: {focus?.Id ?? "未找到"}", _tinyHudFont, textBrush, panel.X + 16, y);
        y += 18;
        graphics.DrawString($"当前分支: {(string.IsNullOrWhiteSpace(currentDecision) ? "无" : currentDecision)}", _tinyHudFont, textBrush, panel.X + 16, y);
        y += 18;
        graphics.DrawString($"待办分支: {(string.IsNullOrWhiteSpace(forcedDecision) ? "未设置" : forcedDecision)}", _tinyHudFont, textBrush, panel.X + 16, y);
        y += 18;
        graphics.DrawString(summaryDecision, _tinyHudFont, textBrush, panel.X + 16, y);
        y += 24;

        graphics.DrawString("后续候选", _smallHudFont, textBrush, panel.X + 16, y);
        y += 24;
        IReadOnlyList<DecisionSpec> nextSpecs = _host.GetSingleUnitTestNextDecisionSpecs();
        if (nextSpecs.Count == 0)
        {
            graphics.DrawString("当前无法推断下一步候选", _tinyHudFont, textBrush, panel.X + 16, y);
            y += 20;
        }
        else
        {
            for (int index = 0; index < Math.Min(3, nextSpecs.Count); index++)
            {
                Rectangle row = new(panel.X + 16, (int)y, panel.Width - 32, 28);
                using (var rowBrush = new SolidBrush(Color.FromArgb(234, 238, 243)))
                {
                    graphics.FillRectangle(rowBrush, row);
                }

                graphics.DrawString(nextSpecs[index].Label, _tinyHudFont, textBrush, row.X + 8, row.Y + 7);
                y += 32;
            }
        }

        y += 8;
        graphics.DrawString("主控待办决策", _smallHudFont, textBrush, panel.X + 16, y);
        Rectangle clearRect = new(panel.Right - 96, (int)y - 2, 80, 24);
        DrawButton(graphics, clearRect, "清除待办", "match_clear_decision", false, Color.FromArgb(96, 104, 118));
        y += 30;

        IReadOnlyList<DecisionSpec> decisionSpecs = _host.GetSingleUnitTestDecisionSpecs();
        int rowHeight = 30;
        int availableHeight = Math.Max(40, panel.Bottom - (int)y - 12);
        int maxVisible = Math.Max(1, availableHeight / rowHeight);
        for (int index = 0; index < Math.Min(maxVisible, decisionSpecs.Count); index++)
        {
            DecisionSpec spec = decisionSpecs[index];
            bool isForced = string.Equals(spec.Id, forcedDecision, StringComparison.OrdinalIgnoreCase);
            bool isRunning = string.Equals(spec.Id, currentDecision, StringComparison.OrdinalIgnoreCase);
            Rectangle row = new(panel.X + 16, (int)y + index * rowHeight, panel.Width - 32, 26);
            using (var rowBrush = new SolidBrush(isForced || isRunning ? Color.FromArgb(217, 232, 247) : Color.FromArgb(234, 238, 243)))
            {
                graphics.FillRectangle(rowBrush, row);
            }

            string suffix = isForced ? " [待办]" : (isRunning ? " [当前]" : string.Empty);
            graphics.DrawString($"{spec.Label}{suffix}", _tinyHudFont, textBrush, row.X + 8, row.Y + 6);
            _uiButtons.Add(new UiButton(row, $"match_set_decision:{spec.Id}"));
        }
    }

    private IReadOnlyList<SimulationEntity> BuildTeamHudUnits(string teamKey)
    {
        var byKey = new Dictionary<string, SimulationEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (!string.Equals(entity.Team, teamKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byKey[ExtractEntityKey(entity.Id)] = entity;
        }

        var ordered = new List<SimulationEntity>(HudRosterOrder.Length);
        foreach (string key in HudRosterOrder)
        {
            if (byKey.TryGetValue(key, out SimulationEntity? entity))
            {
                ordered.Add(entity);
            }
        }

        return ordered;
    }

    private SimulationEntity? FindEntityById(string entityId)
    {
        return _host.World.Entities.FirstOrDefault(entity =>
            string.Equals(entity.Id, entityId, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractEntityKey(string entityId)
    {
        string value = (entityId ?? string.Empty).Trim();
        int separator = value.IndexOf('_');
        if (separator <= 0 || separator >= value.Length - 1)
        {
            return value;
        }

        return value[(separator + 1)..];
    }

    private static bool EntityHasBarrel(SimulationEntity entity)
    {
        if (string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDecisionLabelShort(string selectedDecision, string decisionText)
    {
        string raw = !string.IsNullOrWhiteSpace(selectedDecision)
            ? selectedDecision
            : decisionText;
        raw = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "待机";
        }

        if (raw.StartsWith("_action_", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[8..];
        }

        if (raw.Length > 10)
        {
            raw = raw[..10];
        }

        return raw;
    }

    private void ExecuteUiAction(string action)
    {
        if (action.StartsWith("menu_backend:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetRendererMode(action.Split(':', 2)[1]);
            return;
        }

        if (action.StartsWith("lobby_team:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSelectedTeam(action.Split(':', 2)[1]);
            SelectLobbyRole(ResolveLobbySelectedRoleKey());
            return;
        }

        if (action.StartsWith("lobby_infantry_mode:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetInfantryMode(action.Split(':', 2)[1]);
            SelectLobbyRole("infantry");
            return;
        }

        if (action.StartsWith("lobby_hero_mode:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetHeroPerformanceMode(action.Split(':', 2)[1]);
            SelectLobbyRole("hero");
            return;
        }

        if (action.StartsWith("lobby_infantry_durability:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetInfantryDurabilityMode(action.Split(':', 2)[1]);
            SelectLobbyRole("infantry");
            return;
        }

        if (action.StartsWith("lobby_infantry_weapon:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetInfantryWeaponMode(action.Split(':', 2)[1]);
            SelectLobbyRole("infantry");
            return;
        }

        if (action.StartsWith("lobby_sentry_control:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSentryControlMode(action.Split(':', 2)[1]);
            SelectLobbyRole("sentry");
            return;
        }

        if (action.StartsWith("lobby_sentry_stance:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSentryStance(action.Split(':', 2)[1]);
            SelectLobbyRole("sentry");
            return;
        }

        if (action.StartsWith("lobby_pick_role:", StringComparison.OrdinalIgnoreCase))
        {
            SelectLobbyRole(action.Split(':', 2)[1]);
            return;
        }

        if (action.StartsWith("lobby_focus_entity:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSingleUnitTestFocus(entityKey: action.Split(':', 2)[1]);
            return;
        }

        if (action.StartsWith("lobby_pick:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSelectedEntity(action.Split(':', 2)[1]);
            return;
        }

        if (action.StartsWith("match_select:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSelectedEntity(action.Split(':', 2)[1]);
            return;
        }

        if (action.StartsWith("match_set_decision:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSingleUnitTestDecision(action.Split(':', 2)[1]);
            return;
        }

        if (string.Equals(action, "match_clear_decision", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSingleUnitTestDecision(string.Empty);
            return;
        }

        switch (action)
        {
            case "menu_open_lobby":
                _host.SetMatchMode("full");
                SelectLobbyRole(ResolveLobbySelectedRoleKey());
                _appState = SimulatorAppState.Lobby;
                _paused = true;
                break;
            case "menu_open_appearance_editor":
                LaunchPythonEditor("appearance_editor.py");
                break;
            case "menu_open_terrain_editor":
                LaunchPythonEditor("terrain_editor.py");
                break;
            case "menu_open_rule_editor":
                OpenEditorDialog(new RuleEditorForm());
                break;
            case "menu_open_behavior_editor":
                OpenEditorDialog(new BehaviorEditorForm());
                break;
            case "menu_open_functional_editor":
                OpenEditorDialog(new FunctionalEditorForm());
                break;
            case "menu_open_decision_deployment":
                LaunchDecisionDeploymentProgram();
                break;
            case "menu_exit":
                Close();
                break;
            case "menu_map_prev":
                _host.CycleMapPreset(-1);
                ResetCameraForMap();
                break;
            case "menu_map_next":
                _host.CycleMapPreset(1);
                ResetCameraForMap();
                break;
            case "lobby_back_main":
                _appState = SimulatorAppState.MainMenu;
                _paused = true;
                break;
            case "lobby_start_match":
                StartMatch();
                break;
            case "lobby_toggle_ricochet":
                _host.ToggleRicochet();
                break;
            case "match_reload_deployment":
                _host.ReloadDecisionDeploymentProfile();
                break;
            case "match_open_decision_deployment":
                LaunchDecisionDeploymentProgram();
                break;
            case "match_toggle_debug_sidebars":
                _showDebugSidebars = !_showDebugSidebars;
                break;
            case "match_toggle_pause":
                _paused = !_paused;
                break;
            case "match_reset_world":
                _host.ResetWorld();
                ResetCameraForMap();
                SnapCameraToSelectedEntity();
                break;
            case "match_return_lobby":
                ReturnToLobby();
                break;
        }
    }

    private string? ResolveUiAction(Point point)
    {
        for (int index = _uiButtons.Count - 1; index >= 0; index--)
        {
            UiButton button = _uiButtons[index];
            if (button.Rect.Contains(point))
            {
                return button.Action;
            }
        }

        return null;
    }

    private void StartMatch()
    {
        _host.SetMatchMode("full");
        _host.ResetWorld();
        _paused = false;
        _followSelection = !_firstPersonView;
        _appState = SimulatorAppState.InMatch;
        ResetLiveInput();
        ResetCameraForMap();
        SnapCameraToSelectedEntity();
        UpdateMouseCaptureState();
    }

    private void ReturnToLobby()
    {
        _paused = true;
        _appState = SimulatorAppState.Lobby;
        ReleaseMouseCapture();
        ResetLiveInput();
    }

    private void OpenEditorDialog(Form editor)
    {
        using (editor)
        {
            editor.ShowDialog(this);
        }

        _host.ResetWorld();
        ResetCameraForMap();
        Invalidate();
    }

    private void LaunchPythonEditor(string scriptName)
    {
        string root = _host.ProjectRootPath;
        string scriptPath = Path.Combine(root, scriptName);
        if (!File.Exists(scriptPath))
        {
            MessageBox.Show(this, $"Missing script: {scriptPath}", "RM26 3D Simulator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        List<string> launchers = new();
        string venvPython = Path.Combine(root, ".venv", "Scripts", "python.exe");
        if (File.Exists(venvPython))
        {
            launchers.Add(venvPython);
        }

        launchers.Add("py");
        launchers.Add("python");

        foreach (string launcher in launchers)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = launcher,
                    Arguments = $"\"{scriptPath}\"",
                    WorkingDirectory = root,
                    UseShellExecute = true,
                });
                return;
            }
            catch
            {
                // Try the next launcher.
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                WorkingDirectory = root,
                UseShellExecute = true,
            });
            return;
        }
        catch
        {
            // Fall through to the warning dialog below.
        }

        MessageBox.Show(this, "Python launcher not found.", "RM26 3D Simulator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void LaunchDecisionDeploymentProgram()
    {
        try
        {
            string root = _host.ProjectRootPath;
            string[] executableCandidates =
            {
                Path.Combine(root, "src", "Simulator.Decision", "bin", "Debug", "net9.0-windows", "Simulator.Decision.exe"),
                Path.Combine(root, "src", "Simulator.Decision", "bin", "Release", "net9.0-windows", "Simulator.Decision.exe"),
            };

            foreach (string candidate in executableCandidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    WorkingDirectory = Path.GetDirectoryName(candidate) ?? root,
                    UseShellExecute = true,
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{_host.DecisionProjectPath}\"",
                WorkingDirectory = root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to open decision deployment tool.\n\n{ex.Message}", "RM26 3D Simulator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DrawPanel(Graphics graphics, Rectangle rect, int alpha = 152)
    {
        using var panelBrush = new SolidBrush(Color.FromArgb(alpha, 18, 22, 30));
        using var panelBorderPen = new Pen(Color.FromArgb(Math.Min(255, alpha + 48), 132, 146, 164), 1f);
        graphics.FillRectangle(panelBrush, rect);
        graphics.DrawRectangle(panelBorderPen, rect);
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
    {
        int diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawCard(Graphics graphics, Rectangle rect, bool selected)
    {
        using var fill = new SolidBrush(Color.FromArgb(148, 36, 44, 56));
        using var border = new Pen(selected ? Color.Gold : Color.FromArgb(146, 132, 148, 164), selected ? 2f : 1f);
        graphics.FillRectangle(fill, rect);
        graphics.DrawRectangle(border, rect);
    }

    private void DrawButton(
        Graphics graphics,
        Rectangle rect,
        string label,
        string action,
        bool active,
        Color? activeColor = null,
        bool registerOnly = false)
    {
        if (!registerOnly)
        {
            Color fillColor = active
                ? activeColor ?? Color.FromArgb(58, 124, 214)
                : Color.FromArgb(64, 76, 92);
            using var brush = new SolidBrush(fillColor);
            using var borderPen = new Pen(active ? Color.FromArgb(210, 236, 242, 248) : Color.FromArgb(140, 156, 170, 188), active ? 1.4f : 1f);
            graphics.FillRectangle(brush, rect);
            graphics.DrawRectangle(borderPen, rect);

            if (!string.IsNullOrWhiteSpace(label))
            {
                StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                graphics.DrawString(label, _menuSubtitleFont, Brushes.WhiteSmoke, rect, centered);
            }
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            _uiButtons.Add(new UiButton(rect, action));
        }
    }

    private void ResetCameraForMap()
    {
        _cameraTargetM = ComputeMapCenterMeters();
        _cameraDistanceM = ComputeDefaultCameraDistance();
        RebuildTerrainTileCache();
    }

    private void SnapCameraToSelectedEntity()
    {
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is null)
        {
            return;
        }

        float focusHeight = (float)Math.Max(0.0, selected.GroundHeightM + selected.AirborneHeightM + 0.55);
        _cameraTargetM = ToScenePoint(selected.X, selected.Y, focusHeight);
        _cameraYawRad = ResolveEntityYaw(selected) + MathF.PI;
        _cameraPitchRad = 0.38f;
        _cameraDistanceM = 9.5f;
    }

    private Vector3 ComputeMapCenterMeters()
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        return new Vector3(
            _host.MapPreset.Width * scale * 0.5f,
            0f,
            _host.MapPreset.Height * scale * 0.5f);
    }

    private float ComputeDefaultCameraDistance()
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        float longestEdgeM = Math.Max(_host.MapPreset.Width, _host.MapPreset.Height) * scale;
        return Math.Clamp(longestEdgeM * 1.15f, 18f, 140f);
    }

    private void UpdateCameraMatrices()
    {
        SimulationEntity? selected = _host.SelectedEntity;
        if (_firstPersonView && selected is not null)
        {
            float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
            float turretYaw = (float)(selected.TurretYawDeg * Math.PI / 180.0);
            float pitch = (float)(selected.GimbalPitchDeg * Math.PI / 180.0);
            Vector3 lookDirection = Vector3.Normalize(new Vector3(
                MathF.Cos(pitch) * MathF.Cos(turretYaw),
                MathF.Sin(pitch),
                MathF.Cos(pitch) * MathF.Sin(turretYaw)));
            (double muzzleX, double muzzleY, double muzzleHeightM) = SimulationCombatMath.ComputeMuzzlePoint(_host.World, selected);
            Vector3 muzzle = ToScenePoint(muzzleX, muzzleY, (float)muzzleHeightM);
            Vector3 eye = muzzle - lookDirection * MathF.Max(0.018f, (float)selected.BarrelRadiusM * 0.65f);

            _cameraPositionM = eye;
            _cameraTargetM = eye + lookDirection * 24f;
            _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, Vector3.UnitY);

            float aspectFirstPerson = Math.Max(1f, ClientSize.Width / (float)Math.Max(ClientSize.Height, 1));
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(0.98f, aspectFirstPerson, 0.015f, 1500f);
            return;
        }

        if (_followSelection)
        {
            if (selected is not null)
            {
                float focusHeight = (float)Math.Max(0.0, selected.GroundHeightM + selected.AirborneHeightM + 0.55);
                Vector3 desiredTarget = ToScenePoint(selected.X, selected.Y, focusHeight);
                _cameraTargetM = Vector3.Lerp(_cameraTargetM, desiredTarget, 0.18f);

                float chaseDistance = Math.Clamp(8.5f + (float)Math.Max(selected.GroundHeightM, 0.0) * 0.22f, 6.0f, 14.0f);
                _cameraDistanceM = MathHelperLerp(_cameraDistanceM, chaseDistance, 0.06f);
            }
        }

        float horizontalDistance = MathF.Cos(_cameraPitchRad) * _cameraDistanceM;
        _cameraPositionM = _cameraTargetM + new Vector3(
            MathF.Cos(_cameraYawRad) * horizontalDistance,
            MathF.Sin(_cameraPitchRad) * _cameraDistanceM,
            MathF.Sin(_cameraYawRad) * horizontalDistance);

        _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, Vector3.UnitY);
        float aspect = Math.Max(1f, ClientSize.Width / (float)Math.Max(ClientSize.Height, 1));
        _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, aspect, 0.06f, 1500f);
    }

    private void DrawFloor(Graphics graphics)
    {
        if (!ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid))
        {
            RebuildTerrainTileCache();
        }

        if (_terrainFaces.Count == 0)
        {
            DrawFallbackFloor(graphics);
            DrawStaticStructureBodies(graphics);
            return;
        }

        DrawTerrainTilesBackToFront(graphics);
        DrawStaticStructureBodies(graphics);
    }

    private void DrawFallbackFloor(Graphics graphics)
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        float widthM = _host.MapPreset.Width * scale;
        float heightM = _host.MapPreset.Height * scale;

        Vector3 p1 = new(0f, 0f, 0f);
        Vector3 p2 = new(widthM, 0f, 0f);
        Vector3 p3 = new(widthM, 0f, heightM);
        Vector3 p4 = new(0f, 0f, heightM);

        if (TryProject(p1, out PointF s1, out _)
            && TryProject(p2, out PointF s2, out _)
            && TryProject(p3, out PointF s3, out _)
            && TryProject(p4, out PointF s4, out _))
        {
            using var floorBrush = new SolidBrush(Color.FromArgb(44, 52, 66, 76));
            graphics.FillPolygon(floorBrush, new[] { s1, s2, s3, s4 });
            using var floorPen = new Pen(Color.FromArgb(98, 126, 148, 168), 1.4f);
            graphics.DrawPolygon(floorPen, new[] { s1, s2, s3, s4 });
        }

        float gridStep = Math.Clamp(Math.Max(widthM, heightM) / 22f, 0.8f, 3.0f);
        using var gridPen = new Pen(Color.FromArgb(68, 150, 174, 194), 1f);

        for (float x = 0; x <= widthM + 0.0001f; x += gridStep)
        {
            DrawLine3d(graphics, new Vector3(x, 0.01f, 0f), new Vector3(x, 0.01f, heightM), gridPen);
        }

        for (float z = 0; z <= heightM + 0.0001f; z += gridStep)
        {
            DrawLine3d(graphics, new Vector3(0f, 0.01f, z), new Vector3(widthM, 0.01f, z), gridPen);
        }
    }

    private void RebuildTerrainTileCache()
    {
        _cachedRuntimeGrid = _host.RuntimeGrid;
        _terrainFaces.Clear();
        _terrainDetailFaces.Clear();
        _terrainDetailCenterCellX = int.MinValue;
        _terrainDetailCenterCellY = int.MinValue;
        _lastTerrainDetailRebuildTicks = 0;
        EnsureTerrainColorBitmapLoaded();

        if (_cachedRuntimeGrid is null || !_cachedRuntimeGrid.IsValid)
        {
            return;
        }

        int coarseStep = ResolveTerrainCoarseStep(_cachedRuntimeGrid);
        RebuildTerrainTileCacheMerged(coarseStep, coarseStep, _terrainFaces);
        RebuildVisibleTerrainDetailCache(force: true);
    }

    private static int ResolveTerrainCoarseStep(RuntimeGridData runtimeGrid)
    {
        int longest = Math.Max(runtimeGrid.WidthCells, runtimeGrid.HeightCells);
        if (longest >= 260)
        {
            return 6;
        }

        if (longest >= 150)
        {
            return 4;
        }

        return 3;
    }

    private static Color ResolveTerrainColor(byte terrainCode, float heightM)
    {
        Color baseColor = terrainCode switch
        {
            0 => Color.FromArgb(165, 135, 91),
            1 => Color.FromArgb(144, 116, 78),
            2 => Color.FromArgb(41, 43, 43),
            3 => Color.FromArgb(118, 104, 84),
            4 => Color.FromArgb(178, 148, 95),
            5 => Color.FromArgb(154, 126, 82),
            6 => Color.FromArgb(185, 151, 96),
            7 => Color.FromArgb(130, 106, 76),
            8 => Color.FromArgb(112, 132, 86),
            9 => Color.FromArgb(169, 112, 82),
            10 => Color.FromArgb(92, 102, 128),
            11 => Color.FromArgb(128, 92, 118),
            12 => Color.FromArgb(116, 137, 144),
            13 => Color.FromArgb(122, 101, 78),
            14 => Color.FromArgb(50, 53, 55),
            _ => Color.FromArgb(156, 128, 88),
        };

        float lightness = Math.Clamp(heightM * 0.18f, 0f, 0.28f);
        return BlendColor(baseColor, Color.White, lightness);
    }

    private void EnsureTerrainColorBitmapLoaded()
    {
        string? imagePath = ResolveTerrainColorBitmapPath();
        if (string.Equals(_terrainColorBitmapPath, imagePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _terrainColorBitmap?.Dispose();
        _terrainColorBitmap = null;
        _terrainColorBitmapPath = imagePath;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return;
        }

        try
        {
            _terrainColorBitmap = new Bitmap(imagePath);
        }
        catch
        {
            _terrainColorBitmap = null;
        }
    }

    private string? ResolveTerrainColorBitmapPath()
    {
        string? imagePath = _host.ResolveMapImagePath();
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            return imagePath;
        }

        string mapDirectory = Path.GetDirectoryName(_host.MapPreset.SourcePath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(mapDirectory) || !Directory.Exists(mapDirectory))
        {
            return imagePath;
        }

        string[] preferredNames =
        {
            "场地-俯视图.png",
            "俯视图.png",
            "鍦哄湴-淇鍥?png",
            "淇鍥?png",
        };

        foreach (string name in preferredNames)
        {
            string candidate = Path.Combine(mapDirectory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Directory.EnumerateFiles(mapDirectory, "*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path =>
            {
                string fileName = Path.GetFileName(path);
                return fileName.Contains("俯视", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("blank", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("map", StringComparison.OrdinalIgnoreCase);
            })
            ?? Directory.EnumerateFiles(mapDirectory, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }

    private bool TrySampleTerrainBaseColor(int runtimeCellX, int runtimeCellY, out Color color)
    {
        color = default;
        if (_terrainColorBitmap is null || _cachedRuntimeGrid is null)
        {
            return false;
        }

        int pixelX = (int)Math.Round((runtimeCellX + 0.5) / Math.Max(1, _cachedRuntimeGrid.WidthCells) * (_terrainColorBitmap.Width - 1));
        int pixelY = (int)Math.Round((runtimeCellY + 0.5) / Math.Max(1, _cachedRuntimeGrid.HeightCells) * (_terrainColorBitmap.Height - 1));
        pixelX = Math.Clamp(pixelX, 0, Math.Max(0, _terrainColorBitmap.Width - 1));
        pixelY = Math.Clamp(pixelY, 0, Math.Max(0, _terrainColorBitmap.Height - 1));
        color = _terrainColorBitmap.GetPixel(pixelX, pixelY);
        return true;
    }

    private bool DrawProjectedMapFloorImage(Graphics graphics)
    {
        // The map floor is now rendered through sampled terrain-color tiles instead of a
        // perspective image warp. This avoids GDI+ OOM crashes and keeps the PNG aligned
        // 1:1 with the runtime map coordinates.
        return false;
    }

    private static Color BlendColor(Color left, Color right, float t)
    {
        float blend = Math.Clamp(t, 0f, 1f);
        int r = (int)MathF.Round(left.R + (right.R - left.R) * blend);
        int g = (int)MathF.Round(left.G + (right.G - left.G) * blend);
        int b = (int)MathF.Round(left.B + (right.B - left.B) * blend);
        int a = (int)MathF.Round(left.A + (right.A - left.A) * blend);
        return Color.FromArgb(
            Math.Clamp(a, 0, 255),
            Math.Clamp(r, 0, 255),
            Math.Clamp(g, 0, 255),
            Math.Clamp(b, 0, 255));
    }

    private void DrawFacilities(Graphics graphics)
    {
        if (!_showDebugSidebars)
        {
            return;
        }

        foreach (FacilityRegion region in _host.MapPreset.Facilities.OrderByDescending(FacilitySortDepth))
        {
            if (region.Type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)
                || region.HeightM <= 0.20)
            {
                continue;
            }

            IReadOnlyList<Vector3> footprint = BuildFacilityFootprint(region);
            if (footprint.Count < 3)
            {
                continue;
            }

            float height = (float)Math.Max(region.HeightM, 0.30);
            Color teamColor = ResolveTeamColor(region.Team);
            Color topColor = Color.FromArgb(120, teamColor);
            Color edgeColor = Color.FromArgb(198, BlendColor(teamColor, Color.Black, 0.18f));
            DrawPrismWireframe(graphics, footprint, height, topColor, edgeColor, null);
        }
    }

    private float FacilitySortDepth(FacilityRegion region)
    {
        IReadOnlyList<Vector3> footprint = BuildFacilityFootprint(region);
        if (footprint.Count == 0)
        {
            return 0f;
        }

        Vector3 center = Vector3.Zero;
        foreach (Vector3 point in footprint)
        {
            center += point;
        }

        center /= footprint.Count;
        return Vector3.DistanceSquared(center, _cameraPositionM);
    }

    private void DrawEntities(Graphics graphics)
    {
        _entityDrawBuffer.Clear();
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            _entityDrawBuffer.Add(entity);
        }

        _entityDrawBuffer.Sort((left, right) =>
            Vector3.DistanceSquared(ToScenePoint(right.X, right.Y, 0), _cameraPositionM)
                .CompareTo(Vector3.DistanceSquared(ToScenePoint(left.X, left.Y, 0), _cameraPositionM)));

        foreach (SimulationEntity entity in _entityDrawBuffer)
        {
            if (_firstPersonView
                && entity.IsPlayerControlled
                && string.Equals(entity.Id, _host.SelectedEntity?.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            float height;
            float entityHeightM = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM);
            Vector3 center = ToScenePoint(entity.X, entity.Y, entityHeightM);
            float occlusionProbeHeight = entityHeightM + (float)Math.Max(
                0.30,
                entity.BodyClearanceM + entity.BodyHeightM * 0.60 + entity.GimbalBodyHeightM * 0.35);
            if (IsTerrainOccludingPoint(ToScenePoint(entity.X, entity.Y, occlusionProbeHeight)))
            {
                continue;
            }

            if (entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase))
            {
                RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
                height = DrawOutpostModel(graphics, entity, center, profile, StructureRenderPass.DynamicArmor);
                if (IsAnyKeyHeld(Keys.F3))
                {
                    DrawEntityCollisionBox(graphics, entity, center, profile);
                }
            }
            else if (entity.EntityType.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
                height = DrawBaseModel(graphics, entity, center, profile, StructureRenderPass.DynamicArmor);
                if (IsAnyKeyHeld(Keys.F3))
                {
                    DrawEntityCollisionBox(graphics, entity, center, profile);
                }
            }
            else
            {
                RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
                height = DrawEntityAppearanceModel(graphics, entity, center, profile);
                if (IsAnyKeyHeld(Keys.F3))
                {
                    DrawEntityCollisionBox(graphics, entity, center, profile);
                }
            }

            DrawEntityBar(graphics, entity, center, height);
        }
    }

    private void DrawStaticStructureBodies(Graphics graphics)
    {
        _entityDrawBuffer.Clear();
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase)
                || entity.EntityType.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                _entityDrawBuffer.Add(entity);
            }
        }

        if (_entityDrawBuffer.Count == 0)
        {
            return;
        }

        _entityDrawBuffer.Sort((left, right) =>
            Vector3.DistanceSquared(ToScenePoint(right.X, right.Y, 0), _cameraPositionM)
                .CompareTo(Vector3.DistanceSquared(ToScenePoint(left.X, left.Y, 0), _cameraPositionM)));

        foreach (SimulationEntity entity in _entityDrawBuffer)
        {
            float entityHeightM = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM);
            Vector3 center = ToScenePoint(entity.X, entity.Y, entityHeightM);
            RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
            if (entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase))
            {
                DrawOutpostModel(graphics, entity, center, profile, StructureRenderPass.StaticBody);
            }
            else
            {
                DrawBaseModel(graphics, entity, center, profile, StructureRenderPass.StaticBody);
            }
        }
    }

    private float DrawEntityAppearanceModel(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile)
    {
        if (_host is not null)
        {
            return DrawEntityAppearanceModelModern(graphics, entity, center, profile);
        }

        float yaw = ResolveEntityYaw(entity);
        float turretYaw = (float)(entity.TurretYawDeg * Math.PI / 180.0);
        Color teamColor = ResolveTeamColor(entity.Team);
        Color bodyColor = TintProfileColor(profile.BodyColor, teamColor, entity.IsAlive ? 0.16f : 0.04f, entity.IsAlive);
        Color turretColor = TintProfileColor(profile.TurretColor, teamColor, entity.IsAlive ? 0.22f : 0.05f, entity.IsAlive);
        Color wheelColor = TintProfileColor(profile.WheelColor, teamColor, entity.IsAlive ? 0.07f : 0.03f, entity.IsAlive);

        float bodyLength = Math.Max(0.12f, profile.BodyLengthM);
        float bodyWidth = Math.Max(0.10f, profile.BodyWidthM * profile.BodyRenderWidthScale);
        float bodyHeight = Math.Max(0.08f, profile.BodyHeightM);
        float bodyBase = Math.Max(0f, profile.BodyClearanceM);

        IReadOnlyList<Vector3> bodyFootprint = BuildOrientedRectFootprint(center, bodyLength, bodyWidth, bodyBase, yaw);
        DrawPrismWireframe(
            graphics,
            bodyFootprint,
            bodyHeight,
            Color.FromArgb(entity.IsAlive ? 248 : 232, bodyColor),
            Color.FromArgb(entity.IsAlive ? 255 : 220, bodyColor),
            null);

        float maxHeight = bodyBase + bodyHeight;

        float wheelRadius = Math.Clamp(profile.WheelRadiusM, 0.03f, 0.24f);
        float wheelLength = Math.Max(0.045f, wheelRadius * 0.7f);
        float wheelWidth = Math.Max(0.08f, wheelRadius * 2f);
        float wheelBodyHeight = Math.Max(0.05f, wheelRadius * 1.35f);
        float wheelBase = Math.Max(0f, bodyBase - wheelRadius * 0.40f);

        foreach (Vector2 wheelOffset in profile.WheelOffsetsM)
        {
            Vector3 wheelCenter = OffsetScenePosition(center, wheelOffset.X, wheelOffset.Y, yaw, wheelBase);
            IReadOnlyList<Vector3> wheelFootprint = BuildOrientedEllipseFootprint(
                wheelCenter,
                wheelLength,
                wheelWidth,
                0f,
                yaw + MathF.PI * 0.5f,
                14);

            DrawPrismWireframe(
                graphics,
                wheelFootprint,
                wheelBodyHeight,
                Color.FromArgb(entity.IsAlive ? 246 : 224, wheelColor),
                Color.FromArgb(entity.IsAlive ? 250 : 216, wheelColor),
                null);

            maxHeight = Math.Max(maxHeight, wheelBase + wheelBodyHeight);
        }

        float armorRadiusX = bodyLength * 0.5f + Math.Max(0.012f, profile.ArmorPlateGapM);
        float armorRadiusY = bodyWidth * 0.5f + Math.Max(0.012f, profile.ArmorPlateGapM);
        float armorBase = bodyBase + bodyHeight * 0.5f - profile.ArmorPlateHeightM * 0.5f;
        float armorThickness = Math.Max(0.012f, profile.ArmorPlateGapM * 0.75f);
        IReadOnlyList<float> armorOrbitYaws = profile.ArmorOrbitYawsDeg.Count > 0
            ? profile.ArmorOrbitYawsDeg
            : new[] { 0f, 180f, 90f, 270f };
        IReadOnlyList<float> armorSelfYaws = profile.ArmorSelfYawsDeg.Count > 0
            ? profile.ArmorSelfYawsDeg
            : armorOrbitYaws;
        Color armorColor = TintProfileColor(profile.ArmorColor, teamColor, entity.IsAlive ? 0.18f : 0.04f, entity.IsAlive);
        for (int index = 0; index < armorOrbitYaws.Count; index++)
        {
            float orbitRad = armorOrbitYaws[index] * MathF.PI / 180f;
            float localForward = MathF.Cos(orbitRad) * armorRadiusX;
            float localSide = MathF.Sin(orbitRad) * armorRadiusY;
            float plateYaw = yaw + ((index < armorSelfYaws.Count ? armorSelfYaws[index] : armorOrbitYaws[index]) * MathF.PI / 180f);
            Vector3 armorCenter = OffsetScenePosition(center, localForward, localSide, yaw, armorBase);
            IReadOnlyList<Vector3> armorFootprint = BuildOrientedRectFootprint(
                armorCenter,
                armorThickness,
                profile.ArmorPlateWidthM,
                0f,
                plateYaw);
            DrawPrismWireframe(
                graphics,
                armorFootprint,
                profile.ArmorPlateHeightM,
                Color.FromArgb(entity.IsAlive ? 248 : 226, armorColor),
                Color.FromArgb(entity.IsAlive ? 255 : 216, armorColor),
                null);
            maxHeight = Math.Max(maxHeight, armorBase + profile.ArmorPlateHeightM);
        }

        if (!string.Equals(profile.FrontClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            float frontForward = bodyLength * 0.5f + profile.FrontClimbAssistForwardOffsetM + profile.FrontClimbAssistBottomLengthM * 0.4f;
            float frontSide = Math.Max(bodyWidth * 0.28f, bodyWidth * 0.5f - profile.FrontClimbAssistInnerOffsetM);
            float frontPlateBase = Math.Max(0f, wheelRadius * 0.55f - (entity.TraversalActive ? 0.04f : 0f));
            float frontPlateHeight = profile.FrontClimbAssistPlateHeightM;
            Color climbColor = BlendColor(bodyColor, Color.FromArgb(92, 96, 108), 0.34f);
            foreach (float sideSign in new[] { -1f, 1f })
            {
                Vector3 frontCenter = OffsetScenePosition(center, frontForward, frontSide * sideSign, yaw, frontPlateBase);
                IReadOnlyList<Vector3> frontFootprint = BuildOrientedRectFootprint(
                    frontCenter,
                    Math.Max(profile.FrontClimbAssistTopLengthM, profile.FrontClimbAssistBottomLengthM),
                    profile.FrontClimbAssistPlateWidthM,
                    0f,
                    yaw);
                DrawPrismWireframe(
                    graphics,
                    frontFootprint,
                    frontPlateHeight,
                    Color.FromArgb(entity.IsAlive ? 246 : 224, climbColor),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, climbColor),
                    null);
                maxHeight = Math.Max(maxHeight, frontPlateBase + frontPlateHeight);
            }
        }

        if (!string.Equals(profile.RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            float rearLift = entity.TraversalActive ? MathF.Sin((float)entity.TraversalProgress * MathF.PI) * 0.18f : 0f;
            float rearReach = entity.TraversalActive ? MathF.Sin((float)entity.TraversalProgress * MathF.PI) * 0.16f : 0f;
            float upperBase = Math.Max(bodyBase + bodyHeight * 0.55f + rearLift * 0.35f, profile.RearClimbAssistMountHeightM);
            float lowerBase = Math.Max(0.02f, wheelRadius * 0.18f + rearLift);
            float rearForward = -bodyLength * 0.5f + profile.RearClimbAssistMountOffsetXM;
            float rearSide = Math.Max(bodyWidth * 0.30f, bodyWidth * 0.5f - profile.RearClimbAssistInnerOffsetM);
            Color legColor = BlendColor(bodyColor, Color.FromArgb(86, 92, 106), 0.44f);
            foreach (float sideSign in new[] { -1f, 1f })
            {
                Vector3 upperCenter = OffsetScenePosition(center, rearForward, rearSide * sideSign, yaw, upperBase);
                IReadOnlyList<Vector3> upperFootprint = BuildOrientedRectFootprint(
                    upperCenter,
                    profile.RearClimbAssistUpperLengthM,
                    profile.RearClimbAssistUpperWidthM,
                    0f,
                    yaw);
                DrawPrismWireframe(
                    graphics,
                    upperFootprint,
                    profile.RearClimbAssistUpperHeightM,
                    Color.FromArgb(entity.IsAlive ? 246 : 224, legColor),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, legColor),
                    null);

                Vector3 lowerCenter = OffsetScenePosition(center, rearForward - rearReach - profile.RearClimbAssistLowerLengthM * 0.2f, rearSide * sideSign, yaw, lowerBase);
                IReadOnlyList<Vector3> lowerFootprint = BuildOrientedRectFootprint(
                    lowerCenter,
                    profile.RearClimbAssistLowerLengthM,
                    profile.RearClimbAssistLowerWidthM,
                    0f,
                    yaw - MathF.PI * 0.5f * (entity.TraversalActive ? 0.32f : 0.10f));
                DrawPrismWireframe(
                    graphics,
                    lowerFootprint,
                    profile.RearClimbAssistLowerHeightM,
                    Color.FromArgb(entity.IsAlive ? 248 : 226, legColor),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, legColor),
                    null);
                maxHeight = Math.Max(maxHeight, upperBase + profile.RearClimbAssistUpperHeightM);
            }
        }

        if (profile.GimbalLengthM > 0.04f && profile.GimbalWidthM > 0.04f && profile.GimbalBodyHeightM > 0.02f)
        {
            float turretBase = Math.Max(bodyBase + bodyHeight, profile.GimbalHeightM - profile.GimbalBodyHeightM * 0.5f);
            Vector3 turretCenter = OffsetScenePosition(center, profile.GimbalOffsetXM, profile.GimbalOffsetYM, yaw, turretBase);
            IReadOnlyList<Vector3> turretFootprint = BuildOrientedRectFootprint(
                turretCenter,
                profile.GimbalLengthM,
                profile.GimbalWidthM,
                0f,
                turretYaw);

            DrawPrismWireframe(
                graphics,
                turretFootprint,
                profile.GimbalBodyHeightM,
                Color.FromArgb(entity.IsAlive ? 248 : 226, turretColor),
                Color.FromArgb(entity.IsAlive ? 255 : 220, turretColor),
                null);

            maxHeight = Math.Max(maxHeight, turretBase + profile.GimbalBodyHeightM);

            if (profile.BarrelLengthM > 0.04f && profile.BarrelRadiusM > 0.004f)
            {
                float barrelHeight = Math.Max(0.02f, profile.BarrelRadiusM * 2f);
                float barrelBase = turretBase + profile.GimbalBodyHeightM * 0.46f - profile.BarrelRadiusM;
                float barrelForwardOffset = profile.GimbalLengthM * 0.5f + profile.BarrelLengthM * 0.5f;
                Vector3 barrelCenter = OffsetScenePosition(
                    turretCenter,
                    barrelForwardOffset,
                    0f,
                    turretYaw,
                    barrelBase - turretBase);

                IReadOnlyList<Vector3> barrelFootprint = BuildOrientedRectFootprint(
                    barrelCenter,
                    profile.BarrelLengthM,
                    profile.BarrelRadiusM * 2f,
                    0f,
                    turretYaw);

                DrawPrismWireframe(
                    graphics,
                    barrelFootprint,
                    barrelHeight,
                    Color.FromArgb(entity.IsAlive ? 248 : 226, turretColor),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, turretColor),
                    null);

                maxHeight = Math.Max(maxHeight, barrelBase + barrelHeight);
            }
        }

        if (TryProject(center + new Vector3(0f, maxHeight + 0.03f, 0f), out PointF screenLabel, out _))
        {
            using var textBrush = new SolidBrush(Color.FromArgb(entity.IsAlive ? 232 : 142, 228, 232, 238));
            SizeF size = graphics.MeasureString(entity.Id, _smallHudFont);
            graphics.DrawString(entity.Id, _smallHudFont, textBrush, screenLabel.X - size.Width * 0.5f, screenLabel.Y - 11f);
        }

        return maxHeight;
    }

    private IReadOnlyList<Vector3> BuildOrientedRectFootprint(
        Vector3 center,
        float length,
        float width,
        float baseHeight,
        float yaw)
    {
        Vector2 forward = new(MathF.Cos(yaw), MathF.Sin(yaw));
        Vector2 right = new(-forward.Y, forward.X);
        float halfLength = length * 0.5f;
        float halfWidth = width * 0.5f;

        Vector2 o1 = forward * -halfLength + right * -halfWidth;
        Vector2 o2 = forward * halfLength + right * -halfWidth;
        Vector2 o3 = forward * halfLength + right * halfWidth;
        Vector2 o4 = forward * -halfLength + right * halfWidth;

        return new[]
        {
            new Vector3(center.X + o1.X, center.Y + baseHeight, center.Z + o1.Y),
            new Vector3(center.X + o2.X, center.Y + baseHeight, center.Z + o2.Y),
            new Vector3(center.X + o3.X, center.Y + baseHeight, center.Z + o3.Y),
            new Vector3(center.X + o4.X, center.Y + baseHeight, center.Z + o4.Y),
        };
    }

    private IReadOnlyList<Vector3> BuildOrientedEllipseFootprint(
        Vector3 center,
        float length,
        float width,
        float baseHeight,
        float yaw,
        int segments = 12)
    {
        int pointCount = Math.Max(8, segments);
        Vector2 forward = new(MathF.Cos(yaw), MathF.Sin(yaw));
        Vector2 right = new(-forward.Y, forward.X);
        float halfLength = Math.Max(0.001f, length * 0.5f);
        float halfWidth = Math.Max(0.001f, width * 0.5f);
        Vector3[] points = new Vector3[pointCount];
        for (int index = 0; index < pointCount; index++)
        {
            float angle = MathF.Tau * index / pointCount;
            Vector2 offset = forward * (MathF.Cos(angle) * halfLength)
                + right * (MathF.Sin(angle) * halfWidth);
            points[index] = new Vector3(center.X + offset.X, center.Y + baseHeight, center.Z + offset.Y);
        }

        return points;
    }

    private static Vector3 OffsetScenePosition(Vector3 center, float localForward, float localLateral, float yaw, float height)
    {
        Vector2 forward = new(MathF.Cos(yaw), MathF.Sin(yaw));
        Vector2 right = new(-forward.Y, forward.X);
        Vector2 offset = forward * localForward + right * localLateral;
        return new Vector3(center.X + offset.X, center.Y + height, center.Z + offset.Y);
    }

    private static float ResolveEntityYaw(SimulationEntity entity)
    {
        return (float)(entity.AngleDeg * Math.PI / 180.0);
    }

    private static Color TintProfileColor(Color source, Color teamTint, float tintAmount, bool alive)
    {
        float tint = Math.Clamp(tintAmount, 0f, 1f);
        Color mixed = BlendColor(source, teamTint, tint);
        if (alive)
        {
            return mixed;
        }

        int gray = (int)MathF.Round((mixed.R + mixed.G + mixed.B) / 3f);
        return Color.FromArgb(
            Math.Clamp((int)(gray * 0.86f), 0, 255),
            Math.Clamp((int)(gray * 0.86f), 0, 255),
            Math.Clamp((int)(gray * 0.90f), 0, 255));
    }

    private void DrawEntityBar(Graphics graphics, SimulationEntity entity, Vector3 center, float height)
    {
        Vector3 barAnchor = center + new Vector3(0f, height + 0.12f, 0f);
        if (!TryProject(barAnchor, out PointF barPoint, out _))
        {
            return;
        }

        float width = 66f;
        float healthRatio = entity.MaxHealth <= 0 ? 0f : (float)Math.Clamp(entity.Health / entity.MaxHealth, 0.0, 1.0);
        RectangleF backRect = new(barPoint.X - width * 0.5f, barPoint.Y - 8f, width, 6f);
        RectangleF fillRect = new(backRect.X, backRect.Y, backRect.Width * healthRatio, backRect.Height);

        using var backBrush = new SolidBrush(Color.FromArgb(120, 20, 24, 30));
        using var fillBrush = new SolidBrush(entity.IsAlive ? Color.FromArgb(200, 52, 220, 126) : Color.FromArgb(160, 100, 110, 120));
        using var outlinePen = new Pen(Color.FromArgb(170, 176, 188, 196), 1f);
        graphics.FillRectangle(backBrush, backRect);
        graphics.FillRectangle(fillBrush, fillRect);
        graphics.DrawRectangle(outlinePen, backRect.X, backRect.Y, backRect.Width, backRect.Height);

        string hpText = $"HP {(int)Math.Ceiling(Math.Max(0.0, entity.Health))}/{(int)Math.Ceiling(Math.Max(0.0, entity.MaxHealth))}";
        using var textBrush = new SolidBrush(Color.FromArgb(entity.IsAlive ? 232 : 148, 232, 236, 242));
        SizeF hpSize = graphics.MeasureString(hpText, _tinyHudFont);
        graphics.DrawString(hpText, _tinyHudFont, textBrush, barPoint.X - hpSize.Width * 0.5f, backRect.Y - 13f);
    }

    private void DrawProjectiles(Graphics graphics)
    {
        foreach (SimulationProjectile projectile in _host.World.Projectiles)
        {
            if (_showProjectileTrails
                && _projectileTrailPoints.TryGetValue(projectile.Id, out List<Vector3>? trail)
                && trail.Count > 1)
            {
                DrawProjectileTrail(graphics, projectile, trail);
            }

            Vector3 center = ToScenePoint(projectile.X, projectile.Y, (float)projectile.HeightM);
            bool largeRound = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
            DrawProjectileSphere(graphics, center, largeRound ? 0.021f : 0.0085f, largeRound);
        }
    }

    private void DrawProjectileSphere(Graphics graphics, Vector3 center, float radiusM, bool largeRound)
    {
        if (!TryProject(center, out PointF screenCenter, out _))
        {
            return;
        }

        float screenRadius = 3.0f;
        if (TryProject(center + Vector3.UnitY * Math.Max(0.004f, radiusM), out PointF edge, out _))
        {
            screenRadius = Math.Clamp(MathF.Abs(edge.Y - screenCenter.Y), largeRound ? 3.8f : 2.4f, largeRound ? 11.0f : 6.5f);
        }

        Color core = largeRound
            ? Color.FromArgb(255, 250, 255, 246)
            : Color.FromArgb(255, 88, 255, 106);
        Color glow = largeRound
            ? Color.FromArgb(96, 255, 255, 255)
            : Color.FromArgb(112, 80, 255, 114);
        using var glowBrush = new SolidBrush(glow);
        using var coreBrush = new SolidBrush(core);
        using var edgePen = new Pen(largeRound ? Color.White : Color.FromArgb(230, 190, 255, 178), 1f);
        float glowRadius = screenRadius * (largeRound ? 2.25f : 2.4f);
        graphics.FillEllipse(glowBrush, screenCenter.X - glowRadius, screenCenter.Y - glowRadius, glowRadius * 2f, glowRadius * 2f);
        graphics.FillEllipse(coreBrush, screenCenter.X - screenRadius, screenCenter.Y - screenRadius, screenRadius * 2f, screenRadius * 2f);
        graphics.DrawEllipse(edgePen, screenCenter.X - screenRadius, screenCenter.Y - screenRadius, screenRadius * 2f, screenRadius * 2f);
    }

    private bool IsTerrainOccludingPoint(Vector3 targetPoint)
    {
        if (_cachedRuntimeGrid is null || !_cachedRuntimeGrid.IsValid)
        {
            return false;
        }

        Vector3 ray = targetPoint - _cameraPositionM;
        float distance = ray.Length();
        if (distance <= 0.25f)
        {
            return false;
        }

        float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        int samples = Math.Clamp((int)MathF.Ceiling(distance / 0.10f), 8, 120);
        for (int index = 1; index < samples; index++)
        {
            float t = index / (float)samples;
            if (t >= 0.92f)
            {
                break;
            }

            Vector3 sample = _cameraPositionM + ray * t;
            float sampleWorldX = sample.X / metersPerWorldUnit;
            float sampleWorldY = sample.Z / metersPerWorldUnit;
            if (sampleWorldX < 0f
                || sampleWorldY < 0f
                || sampleWorldX >= _cachedRuntimeGrid.WidthCells * _cachedRuntimeGrid.CellWidthWorld
                || sampleWorldY >= _cachedRuntimeGrid.HeightCells * _cachedRuntimeGrid.CellHeightWorld)
            {
                continue;
            }

            int cellX = Math.Clamp((int)MathF.Floor(sampleWorldX / Math.Max(_cachedRuntimeGrid.CellWidthWorld, 1e-6f)), 0, _cachedRuntimeGrid.WidthCells - 1);
            int cellY = Math.Clamp((int)MathF.Floor(sampleWorldY / Math.Max(_cachedRuntimeGrid.CellHeightWorld, 1e-6f)), 0, _cachedRuntimeGrid.HeightCells - 1);
            float terrainHeight = _cachedRuntimeGrid.HeightMap[_cachedRuntimeGrid.IndexOf(cellX, cellY)];
            if (terrainHeight > 0.03f && sample.Y <= terrainHeight - 0.02f)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateProjectileTrailCache()
    {
        if (!_showProjectileTrails)
        {
            _projectileTrailPoints.Clear();
            return;
        }

        if (_host.World.Projectiles.Count == 0)
        {
            _projectileTrailPoints.Clear();
            return;
        }

        HashSet<string> activeIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (SimulationProjectile projectile in _host.World.Projectiles)
        {
            activeIds.Add(projectile.Id);
            if (!_projectileTrailPoints.TryGetValue(projectile.Id, out List<Vector3>? trail))
            {
                trail = new List<Vector3>(20);
                _projectileTrailPoints[projectile.Id] = trail;
            }

            Vector3 point = ToScenePoint(projectile.X, projectile.Y, (float)projectile.HeightM);
            if (trail.Count == 0 || Vector3.DistanceSquared(trail[^1], point) >= 0.0006f)
            {
                trail.Add(point);
                if (trail.Count > 24)
                {
                    trail.RemoveAt(0);
                }
            }
        }

        foreach (string staleId in _projectileTrailPoints.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _projectileTrailPoints.Remove(staleId);
        }
    }

    private void DrawProjectileTrail(Graphics graphics, SimulationProjectile projectile, IReadOnlyList<Vector3> trail)
    {
        var points = new List<PointF>(trail.Count);
        foreach (Vector3 trailPoint in trail)
        {
            if (TryProject(trailPoint, out PointF screenPoint, out _))
            {
                points.Add(screenPoint);
            }
        }

        if (points.Count < 2)
        {
            return;
        }

        Color tint = string.Equals(projectile.Team, "red", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(112, 255, 138, 116)
            : Color.FromArgb(112, 124, 198, 255);
        using var pen = new Pen(tint, 1.2f);
        graphics.DrawLines(pen, points.ToArray());
    }

    private void DrawEntityCollisionBox(Graphics graphics, SimulationEntity entity, Vector3 center, RobotAppearanceProfile profile)
    {
        float yaw = ResolveEntityYaw(entity);
        float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        float diameterFromCollision = (float)Math.Max(0.12, entity.CollisionRadiusWorld * metersPerWorldUnit * 2.0);
        float length = Math.Max(diameterFromCollision, Math.Max((float)entity.BodyLengthM, profile.BodyLengthM));
        float width = Math.Max(diameterFromCollision, Math.Max((float)entity.BodyWidthM, profile.BodyWidthM * profile.BodyRenderWidthScale));
        float baseHeight = Math.Max(0f, Math.Max((float)entity.BodyClearanceM, profile.BodyClearanceM));
        float height = Math.Max(0.08f, Math.Max((float)entity.BodyHeightM, profile.BodyHeightM));
        IReadOnlyList<Vector3> footprint = BuildOrientedRectFootprint(center, length, width, baseHeight, yaw);
        DrawPrismWireframe(
            graphics,
            footprint,
            height,
            Color.FromArgb(44, 94, 170, 255),
            Color.FromArgb(216, 150, 214, 255),
            null);
    }

    private IReadOnlyList<Vector3> BuildFacilityFootprint(FacilityRegion region)
    {
        if (region.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase) && region.Points.Count >= 3)
        {
            return region.Points.Select(point => ToScenePoint(point.X, point.Y, 0f)).ToArray();
        }

        if (region.Shape.Equals("line", StringComparison.OrdinalIgnoreCase))
        {
            Vector2 start = new((float)region.X1, (float)region.Y1);
            Vector2 end = new((float)region.X2, (float)region.Y2);
            Vector2 direction = end - start;
            if (direction.LengthSquared() <= 1e-4f)
            {
                float radius = (float)Math.Max(region.Thickness * 0.5, 4.0);
                return BuildRectFootprint(start.X - radius, start.Y - radius, start.X + radius, start.Y + radius);
            }

            direction = Vector2.Normalize(direction);
            Vector2 normal = new(-direction.Y, direction.X);
            float half = (float)Math.Max(region.Thickness * 0.5, 2.0);
            Vector2 p1 = start + normal * half;
            Vector2 p2 = start - normal * half;
            Vector2 p3 = end - normal * half;
            Vector2 p4 = end + normal * half;
            return new[]
            {
                ToScenePoint(p1.X, p1.Y, 0f),
                ToScenePoint(p2.X, p2.Y, 0f),
                ToScenePoint(p3.X, p3.Y, 0f),
                ToScenePoint(p4.X, p4.Y, 0f),
            };
        }

        return BuildRectFootprint(region.X1, region.Y1, region.X2, region.Y2);
    }

    private IReadOnlyList<Vector3> BuildRectFootprint(double x1, double y1, double x2, double y2)
    {
        float left = (float)Math.Min(x1, x2);
        float right = (float)Math.Max(x1, x2);
        float top = (float)Math.Min(y1, y2);
        float bottom = (float)Math.Max(y1, y2);

        return new[]
        {
            ToScenePoint(left, top, 0f),
            ToScenePoint(right, top, 0f),
            ToScenePoint(right, bottom, 0f),
            ToScenePoint(left, bottom, 0f),
        };
    }

    private void DrawPrismWireframe(
        Graphics graphics,
        IReadOnlyList<Vector3> baseVertices,
        float height,
        Color topColor,
        Color edgeColor,
        string? label)
    {
        if (baseVertices.Count < 3 || height <= 0f)
        {
            return;
        }

        Vector3[] topVertices = new Vector3[baseVertices.Count];
        Vector3 labelPoint = Vector3.Zero;
        for (int index = 0; index < baseVertices.Count; index++)
        {
            topVertices[index] = baseVertices[index] + new Vector3(0f, height, 0f);
            labelPoint += topVertices[index];
        }

        var faces = new List<ProjectedFace>(baseVertices.Count + 1);
        if (TryBuildProjectedFace(topVertices, ShadeFaceColor(topColor, topVertices, 0.78f), edgeColor, out ProjectedFace topFace))
        {
            faces.Add(topFace);
        }

        for (int index = 0; index < baseVertices.Count; index++)
        {
            int next = (index + 1) % baseVertices.Count;
            Vector3[] sideVertices =
            {
                baseVertices[index],
                baseVertices[next],
                topVertices[next],
                topVertices[index],
            };

            Color sideColor = ShadeFaceColor(topColor, sideVertices, 0.50f);
            if (TryBuildProjectedFace(sideVertices, sideColor, edgeColor, out ProjectedFace sideFace))
            {
                faces.Add(sideFace);
            }
        }

        faces.Sort((left, right) => right.AverageDepth.CompareTo(left.AverageDepth));
        foreach (ProjectedFace face in faces)
        {
            using var faceBrush = new SolidBrush(face.FillColor);
            using var facePen = new Pen(face.EdgeColor, 1.1f);
            graphics.FillPolygon(faceBrush, face.Points);
            graphics.DrawPolygon(facePen, face.Points);
        }

        labelPoint /= baseVertices.Count;
        if (!string.IsNullOrWhiteSpace(label)
            && TryProject(labelPoint + new Vector3(0f, 0.05f, 0f), out PointF screenLabel, out _))
        {
            using var textBrush = new SolidBrush(Color.FromArgb(230, 230, 234, 242));
            SizeF size = graphics.MeasureString(label, _smallHudFont);
            graphics.DrawString(label, _smallHudFont, textBrush, screenLabel.X - size.Width * 0.5f, screenLabel.Y - 11f);
        }
    }

    private bool TryBuildProjectedFace(
        IReadOnlyList<Vector3> vertices,
        Color fillColor,
        Color edgeColor,
        out ProjectedFace face)
    {
        var points = new PointF[vertices.Count];
        float depthSum = 0f;
        for (int index = 0; index < vertices.Count; index++)
        {
            if (!TryProject(vertices[index], out PointF point, out float depth))
            {
                face = default;
                return false;
            }

            points[index] = point;
            depthSum += depth;
        }

        if (Math.Abs(ComputeSignedArea(points)) < 0.0005f)
        {
            face = default;
            return false;
        }

        face = new ProjectedFace(points, depthSum / Math.Max(1, vertices.Count), fillColor, edgeColor);
        return true;
    }

    private static float ComputeSignedArea(IReadOnlyList<PointF> points)
    {
        float area = 0f;
        for (int index = 0; index < points.Count; index++)
        {
            PointF current = points[index];
            PointF next = points[(index + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }

        return area * 0.5f;
    }

    private static Color ShadeFaceColor(Color color, IReadOnlyList<Vector3> vertices, float ambient)
    {
        if (vertices.Count < 3)
        {
            return color;
        }

        Vector3 normal = Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]);
        if (normal.LengthSquared() <= 1e-8f)
        {
            return color;
        }

        normal = Vector3.Normalize(normal);
        Vector3 light = Vector3.Normalize(new Vector3(-0.45f, 1.0f, -0.35f));
        float diffuse = MathF.Abs(Vector3.Dot(normal, light));
        float brightness = Math.Clamp(ambient + diffuse * 0.42f, 0.35f, 1.12f);
        return ScaleColor(color, brightness);
    }

    private static Color ScaleColor(Color color, float scale)
    {
        return Color.FromArgb(
            color.A,
            Math.Clamp((int)MathF.Round(color.R * scale), 0, 255),
            Math.Clamp((int)MathF.Round(color.G * scale), 0, 255),
            Math.Clamp((int)MathF.Round(color.B * scale), 0, 255));
    }

    private bool TryProject(Vector3 point, out PointF screenPoint, out float depth)
    {
        Vector4 view = Vector4.Transform(new Vector4(point, 1f), _viewMatrix);
        depth = -view.Z;
        if (depth <= 0.01f)
        {
            screenPoint = default;
            return false;
        }

        Vector4 clip = Vector4.Transform(view, _projectionMatrix);
        if (Math.Abs(clip.W) <= 1e-5f)
        {
            screenPoint = default;
            return false;
        }

        float inverseW = 1f / clip.W;
        float ndcX = clip.X * inverseW;
        float ndcY = clip.Y * inverseW;
        float x = (ndcX * 0.5f + 0.5f) * ClientSize.Width;
        float y = (1f - (ndcY * 0.5f + 0.5f)) * ClientSize.Height;
        screenPoint = new PointF(x, y);
        return true;
    }

    private void DrawLine3d(Graphics graphics, Vector3 from, Vector3 to, Pen pen)
    {
        if (TryProject(from, out PointF a, out _) && TryProject(to, out PointF b, out _))
        {
            graphics.DrawLine(pen, a, b);
        }
    }

    private Vector3 ToScenePoint(double xWorld, double yWorld, float heightMeters)
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        return new Vector3((float)xWorld * scale, heightMeters, (float)yWorld * scale);
    }

    private static (float Radius, float Height) ResolveEntitySize(SimulationEntity entity)
    {
        if (entity.EntityType.Equals("base", StringComparison.OrdinalIgnoreCase))
        {
            return (1.2f, 1.4f);
        }

        if (entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase))
        {
            return (0.85f, 1.1f);
        }

        if (entity.EntityType.Equals("sentry", StringComparison.OrdinalIgnoreCase)
            || entity.RoleKey.Equals("sentry", StringComparison.OrdinalIgnoreCase))
        {
            return (0.45f, 0.80f);
        }

        if (entity.RoleKey.Equals("hero", StringComparison.OrdinalIgnoreCase))
        {
            return (0.42f, 0.78f);
        }

        if (entity.RoleKey.Equals("engineer", StringComparison.OrdinalIgnoreCase))
        {
            return (0.39f, 0.74f);
        }

        return (0.35f, 0.70f);
    }

    private static Color ResolveTeamColor(string team)
    {
        if (team.Equals("red", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(224, 76, 76);
        }

        if (team.Equals("blue", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(76, 132, 232);
        }

        return Color.FromArgb(188, 178, 124);
    }

    private static string ResolveRoleLabel(SimulationEntity entity)
    {
        if (entity.RoleKey.Equals("hero", StringComparison.OrdinalIgnoreCase))
        {
            return "Hero";
        }

        if (entity.RoleKey.Equals("engineer", StringComparison.OrdinalIgnoreCase))
        {
            return "Engineer";
        }

        if (entity.RoleKey.Equals("sentry", StringComparison.OrdinalIgnoreCase))
        {
            return "Sentry";
        }

        if (entity.RoleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase))
        {
            return "Infantry";
        }

        return entity.RoleKey;
    }

    private static string ResolveRoleLabel(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "hero" => "英雄",
            "engineer" => "工程",
            "infantry" => "步兵",
            "sentry" => "哨兵",
            _ => role,
        };
    }

    private static string ResolveDecisionModeLabel(string mode)
    {
        return (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "hold" => "驻守",
            "support" => "支援",
            "flank" => "侧翼",
            _ => "压制",
        };
    }

    private Vector3 GetHorizontalForward()
    {
        Vector3 horizontalForward = new(
            -MathF.Cos(_cameraYawRad),
            0f,
            -MathF.Sin(_cameraYawRad));

        if (horizontalForward.LengthSquared() <= 1e-6f)
        {
            return Vector3.UnitZ;
        }

        return Vector3.Normalize(horizontalForward);
    }

    private static float MathHelperLerp(float from, float to, float amount)
    {
        return from + (to - from) * Math.Clamp(amount, 0f, 1f);
    }

    private static float SmoothAngleRadians(float current, float target, float amount)
    {
        float delta = target - current;
        while (delta > MathF.PI)
        {
            delta -= MathF.PI * 2f;
        }

        while (delta < -MathF.PI)
        {
            delta += MathF.PI * 2f;
        }

        return current + delta * Math.Clamp(amount, 0f, 1f);
    }

    private PlayerControlState BuildPlayerControlState(bool forceEnable = false)
    {
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is null)
        {
            return new PlayerControlState
            {
                Enabled = false,
            };
        }

        double moveForward = GetMovementAxisContinuous(Keys.W, 0x57, Keys.S, 0x53);
        double moveRight = GetMovementAxisContinuous(Keys.D, 0x44, Keys.A, 0x41);
        double turretYawDelta = _pendingMouseYawDeltaDeg;
        double gimbalPitchDelta = _pendingMousePitchDeltaDeg;
        _pendingMouseYawDeltaDeg = 0f;
        _pendingMousePitchDeltaDeg = 0f;
        bool jumpRequested = _pendingJumpRequest || ConsumePressedInput(Keys.Space, 0x20, ref _spaceKeyWasDown);
        _pendingJumpRequest = false;
        bool buyAmmoRequested = _buyAmmoRequested || ConsumePressedInput(Keys.B, 0x42, ref _buyKeyWasDown, heldCountsAsPressed: true);
        _buyAmmoRequested = false;
        bool fireModifierPressed = IsAnyKeyHeld(Keys.ControlKey, Keys.LControlKey, Keys.RControlKey);
        bool smallGyroActive = IsAnyKeyHeld(Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey);

        bool enabled = forceEnable || (_appState == SimulatorAppState.InMatch && !_paused);
        return new PlayerControlState
        {
            EntityId = selected.Id,
            Enabled = enabled,
            MoveForward = moveForward,
            MoveRight = moveRight,
            TurretYawDeltaDeg = turretYawDelta,
            GimbalPitchDeltaDeg = gimbalPitchDelta,
            FirePressed = _firePressed || fireModifierPressed,
            AutoAimPressed = _autoAimPressed,
            JumpRequested = jumpRequested,
            StepClimbModeActive = false,
            SmallGyroActive = smallGyroActive,
            BuyAmmoRequested = buyAmmoRequested,
        };
    }

    private bool IsAnyKeyHeld(params Keys[] keys)
    {
        foreach (Keys key in keys)
        {
            if (_heldKeys.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private void ResetLiveInput()
    {
        _heldKeys.Clear();
        _firePressed = false;
        _autoAimPressed = false;
        _buyAmmoRequested = false;
        _pendingJumpRequest = false;
        _spaceKeyWasDown = false;
        _buyKeyWasDown = false;
        _pendingMouseYawDeltaDeg = 0f;
        _pendingMousePitchDeltaDeg = 0f;
    }

    private void UpdateMouseCaptureState()
    {
        bool shouldCapture =
            _appState == SimulatorAppState.InMatch
            && !_paused
            && Visible
            && (ContainsFocus || IsWindowActive());

        if (shouldCapture)
        {
            if (!_mouseCaptureActive)
            {
                Cursor.Hide();
                _mouseCaptureActive = true;
                WarpCursorToClientCenter();
            }
            return;
        }

        ReleaseMouseCapture();
    }

    private void ReleaseMouseCapture()
    {
        if (_mouseCaptureActive)
        {
            Cursor.Show();
            _mouseCaptureActive = false;
        }
    }

    private void WarpCursorToClientCenter()
    {
        if (!_mouseCaptureActive || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        Point centerClient = new(ClientSize.Width / 2, ClientSize.Height / 2);
        Point centerScreen = PointToScreen(centerClient);
        _suppressMouseWarp = true;
        Cursor.Position = centerScreen;
        _lastMouse = centerClient;
    }

    private void CaptureCombatMarkersFromLatestReport()
    {
        if (_host.LastReport is null || _host.LastReport.CombatEvents.Count == 0)
        {
            return;
        }

        foreach (SimulationCombatEvent combatEvent in _host.LastReport.CombatEvents)
        {
            if (!combatEvent.Hit || combatEvent.Damage <= 0.0)
            {
                continue;
            }

            SimulationEntity? target = _host.World.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Id, combatEvent.TargetId, StringComparison.OrdinalIgnoreCase));
            Color tint = target is null
                ? Color.FromArgb(244, 255, 214, 84)
                : ResolveTeamColor(target.Team);
            _combatMarkers.Add(new FloatingCombatMarker(
                combatEvent.TargetId,
                target?.X ?? 0.0,
                target?.Y ?? 0.0,
                (target?.GroundHeightM ?? 0.0) + (target?.AirborneHeightM ?? 0.0) + 0.9,
                "hit",
                Color.FromArgb(246, tint),
                0.70f));
        }

        if (_combatMarkers.Count > 24)
        {
            _combatMarkers.RemoveRange(0, _combatMarkers.Count - 24);
        }
    }

    private void AdvanceCombatMarkers(float deltaSec)
    {
        if (_combatMarkers.Count == 0)
        {
            return;
        }

        for (int index = _combatMarkers.Count - 1; index >= 0; index--)
        {
            FloatingCombatMarker marker = _combatMarkers[index];
            marker.AgeSec += Math.Max(0f, deltaSec);
            if (marker.AgeSec >= marker.LifetimeSec)
            {
                _combatMarkers.RemoveAt(index);
            }
        }
    }

    private void DrawCombatMarkers(Graphics graphics)
    {
        if (_combatMarkers.Count == 0)
        {
            return;
        }

        foreach (FloatingCombatMarker marker in _combatMarkers)
        {
            SimulationEntity? target = _host.World.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Id, marker.TargetId, StringComparison.OrdinalIgnoreCase));
            double worldX = target?.X ?? marker.WorldX;
            double worldY = target?.Y ?? marker.WorldY;
            double heightM = target is null
                ? marker.HeightM
                : target.GroundHeightM + target.AirborneHeightM + Math.Max(target.BodyHeightM, 0.30) + 0.55 + marker.AgeSec * 0.20;

            Vector3 anchor = ToScenePoint(worldX, worldY, (float)heightM);
            if (!TryProject(anchor, out PointF screenPoint, out _))
            {
                continue;
            }

            float fadeRatio = 1f - Math.Clamp(marker.AgeSec / marker.LifetimeSec, 0f, 1f);
            Color textColor = Color.FromArgb(
                Math.Clamp((int)MathF.Round(fadeRatio * 255f), 0, 255),
                marker.Color);
            using var textBrush = new SolidBrush(textColor);
            SizeF size = graphics.MeasureString(marker.Text, _smallHudFont);
            graphics.DrawString(marker.Text, _smallHudFont, textBrush, screenPoint.X - size.Width * 0.5f, screenPoint.Y - size.Height * 0.5f);
        }
    }
}
