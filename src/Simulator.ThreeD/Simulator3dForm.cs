using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm : Form
{
    private const int ToolbarHeight = 0;
    private const int HudHeight = 118;
    private const int SidebarWidth = 270;
    private const int DecisionSidebarWidth = 320;
    private const double MainMenuTargetFrameIntervalSec = 1.0 / 30.0;
    private const float FirstPersonVerticalFovRad = MathF.PI * 0.5f; // 90搴﹁鍦鸿銆?    private const float FirstPersonBarrelScreenDropM = 0.030f;
    private const float FirstPersonSightConvergenceM = 24.0f;
    private const float ObserverYawSensitivityRadPerPixel = 0.00165f;
    private const float ObserverPitchSensitivityRadPerPixel = 0.00125f;
    // Keep the legacy arena mechanism proxies visible until the fine-component
    // runtime render path fully replaces them in-match. Otherwise the user sees
    // "missing" interactive structures even though rules are still active.
    private const bool HideTemporaryArenaMechanismModels = false;

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

    private enum AutoAimAssistMode
    {
        HardLock,
        GuidanceOnly,
    }

    private enum TacticalCommandMode
    {
        Attack,
        Defend,
        Patrol,
    }

    private enum MatchStartupPhase
    {
        None,
        Loading,
        Countdown,
        Live,
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

    private readonly record struct EntityRenderOverlay(
        SimulationEntity Entity,
        Vector3 Center,
        float Height,
        RobotAppearanceProfile Profile);

    private readonly record struct ProjectileRenderCommand(
        bool Visible,
        bool Solid,
        PointF Center,
        float ScreenRadius,
        RectangleF FlatBody,
        Color CoreColor,
        Color MidColor,
        Color RimColor,
        Color GlowColor,
        Color TrailColor,
        PointF[]? TrailPoints);

    private sealed class FloatingCombatMarker
    {
        public FloatingCombatMarker(
            string targetId,
            double worldX,
            double worldY,
            double heightM,
            string text,
            Color color,
            float lifetimeSec,
            float screenOffsetX = 0f,
            float screenOffsetY = 0f,
            float riseSpeed = 0.20f)
        {
            TargetId = targetId;
            WorldX = worldX;
            WorldY = worldY;
            HeightM = heightM;
            Text = text;
            Color = color;
            LifetimeSec = Math.Max(0.12f, lifetimeSec);
            ScreenOffsetX = screenOffsetX;
            ScreenOffsetY = screenOffsetY;
            RiseSpeed = Math.Max(0.05f, riseSpeed);
        }

        public string TargetId { get; }

        public double WorldX { get; }

        public double WorldY { get; }

        public double HeightM { get; }

        public string Text { get; }

        public Color Color { get; }

        public float LifetimeSec { get; }

        public float AgeSec { get; set; }

        public float ScreenOffsetX { get; }

        public float ScreenOffsetY { get; }

        public float RiseSpeed { get; }
    }

    private sealed class MatchEventFeedItem
    {
        public MatchEventFeedItem(string text, Color color, float lifetimeSec)
        {
            Text = text;
            Color = color;
            LifetimeSec = Math.Max(1.0f, lifetimeSec);
        }

        public string Text { get; }

        public Color Color { get; }

        public float LifetimeSec { get; }

        public float AgeSec { get; set; }
    }

    private sealed class CenterBuffToast
    {
        public CenterBuffToast(string title, string detail, Color color, float lifetimeSec = 3.0f)
        {
            Title = title;
            Detail = detail;
            Color = color;
            LifetimeSec = Math.Max(0.5f, lifetimeSec);
        }

        public string Title { get; }

        public string Detail { get; }

        public Color Color { get; }

        public float LifetimeSec { get; }

        public float AgeSec { get; set; }
    }

    private readonly record struct BuffProgressEntry(
        string Key,
        string Category,
        string Name,
        string Effect,
        double RemainingSec,
        double DurationSec,
        Color Color,
        bool Timed,
        double Magnitude);

    private const int MaxSimulationCatchUpSteps = 2;
    private const double MatchStartupCountdownSec = 3.0;

    private readonly Simulator3dHost _host;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private readonly Font _tinyHudFont = new("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _smallHudFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _hudMidFont = new("Microsoft YaHei UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _hudBigFont = new("Microsoft YaHei UI", 18f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _titleFont = new("Microsoft YaHei UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _menuTitleFont = new("Microsoft YaHei UI", 24f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _menuSubtitleFont = new("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _menuEyebrowFont = new("Microsoft YaHei UI", 8.8f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _menuButtonFont = new("Microsoft YaHei UI", 13f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _menuFootnoteFont = new("Microsoft YaHei UI", 9.2f, FontStyle.Regular, GraphicsUnit.Point);
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
    private bool _tacticalMode;
    private bool _observerMode;
    private bool _observerPinned;
    private bool _mouseCaptureActive;
    private bool _suppressMouseWarp;
    private readonly bool _externallyDrivenCompatibilityMode;
    private bool _spaceKeyWasDown;
    private bool _buyKeyWasDown;
    private bool _sentryStanceKeyWasDown;
    private bool _showKeyGuide;
    private bool _showVisionPoseSolve;
    private bool _showCollisionDebug;
    private bool _fineTerrainInMatchEditMode;
    private bool _pendingSingleFireRequest;
    private bool _draggingLobbyAutoAimSlider;
    private Point _lastMouse;
    private float _pendingMouseYawDeltaDeg;
    private float _pendingMousePitchDeltaDeg;
    private AutoAimAssistMode _autoAimAssistMode = AutoAimAssistMode.HardLock;
    private TacticalCommandMode _tacticalCommandMode = TacticalCommandMode.Attack;
    private double _simulationTimeScale = 1.0;
    private string? _tacticalAttackTargetId;
    private double _tacticalGroundTargetX;
    private double _tacticalGroundTargetY;
    private double _tacticalPatrolRadiusWorld = 45.0;

    private float _cameraYawRad = -0.85f;
    private float _cameraPitchRad = 0.62f;
    private float _cameraDistanceM = 24f;
    private float _thirdPersonFollowDistanceScale = 1f;
    private Vector3 _cameraTargetM;
    private Vector3 _cameraPositionM;
    private Vector3 _observerPositionM;
    private float _observerYawRad = -0.85f;
    private float _observerPitchRad = 0.18f;
    private float _observerMoveSpeedMps = 4.5f;
    private Matrix4x4 _viewMatrix;
    private Matrix4x4 _projectionMatrix;
    private RuntimeGridData? _cachedRuntimeGrid;
    private string _cachedTerrainAssetSignature = string.Empty;
    private readonly List<TerrainFacePatch> _terrainFaces = new();
    private readonly List<TerrainFacePatch> _terrainDetailFaces = new();
    private readonly List<TerrainFacePatch> _terrainDrawBuffer = new();
    private readonly List<ProjectedFace> _projectedTerrainFaceBuffer = new();
    private readonly List<ProjectedFace> _cachedProjectedTerrainFaces = new();
    private readonly List<ProjectedFace> _projectedStaticStructureFaceBuffer = new();
    private readonly List<ProjectedFace> _cachedProjectedStaticStructureFaces = new();
    private readonly List<ProjectedFace> _projectedEntityFaceBuffer = new();
    private readonly List<ProjectedFace> _projectedFaceScratchBuffer = new();
    private readonly List<SimulationEntity> _entityDrawBuffer = new();
    private readonly List<EntityRenderOverlay> _entityOverlayBuffer = new();
    private readonly List<TerrainCollisionDebugTriangle> _terrainCollisionDebugTriangleBuffer = new(2048);
    private int _lastGpuFullDetailEntityRenderCount;
    private int _lastGpuProxyEntityRenderCount;
    private int _lastGpuStructureEntityRenderCount;
    private int _lastGpuEnergyEntityRenderCount;
    private long _lastGpuFullDetailEntityRenderTicks;
    private long _lastGpuProxyEntityRenderTicks;
    private long _lastGpuStructureEntityRenderTicks;
    private long _lastGpuEnergyEntityRenderTicks;
    private readonly Dictionary<string, List<Vector3>> _projectileTrailPoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _projectileTrailActiveIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _projectileTrailStaleIds = new(64);
    private readonly Dictionary<int, SolidBrush> _projectedFaceBrushCache = new();
    private readonly Dictionary<int, Pen> _projectedFacePenCache = new();
    private readonly List<FloatingCombatMarker> _combatMarkers = new();
    private readonly List<MatchEventFeedItem> _matchEventFeed = new();
    private readonly List<CenterBuffToast> _centerBuffToasts = new();
    private readonly Dictionary<string, double> _selectedBuffSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _powerCutSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _outpostPlateFlashEndTimes = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedBuffSnapshotEntityId;
    private DriveTelemetryForm? _driveTelemetryForm;
    private Icon? _windowIcon;
    private Bitmap? _cachedTerrainLayerBitmap;
    private Bitmap? _cachedStaticStructureLayerBitmap;
    private Bitmap? _cpuProjectileLayerBitmap;
    private Graphics? _cpuProjectileLayerGraphics;
    private Size _cpuProjectileLayerClientSize = Size.Empty;
    private ProjectileRenderCommand[] _projectileRenderCommandBuffer = Array.Empty<ProjectileRenderCommand>();
    private Bitmap? _fastEntityLayerBitmap;
    private Graphics? _fastEntityLayerGraphics;
    private Bitmap? _fastProjectileLayerBitmap;
    private Graphics? _fastProjectileLayerGraphics;
    private Size _fastLayerBitmapClientSize = Size.Empty;
    private Bitmap? _hudPortraitCacheBitmap;
    private string _hudPortraitCacheKey = string.Empty;
    private Size _hudPortraitCacheSize = Size.Empty;
    private Bitmap? _terrainColorBitmap;
    private string? _terrainColorBitmapPath;
    private Rectangle? _projectionViewportRect;
    private Rectangle _lobbyAutoAimSliderRect;
    private Rectangle _lobbyDuelRoundInputRect;
    private bool _lobbyDuelRoundInputFocused;
    private string _lobbyDuelRoundInputText = "5";
    private bool _suppressEntityLabels;
    private bool _suppressSelectedEntityModel;
    private bool _matchNoGcRegionActive;
    private bool _matchNoGcRegionAttempted;
    private GCLatencyMode _previousGcLatencyMode = GCLatencyMode.Interactive;
    private int _terrainDetailCenterCellX = int.MinValue;
    private int _terrainDetailCenterCellY = int.MinValue;
    private float _terrainDetailMinXWorld;
    private float _terrainDetailMinYWorld;
    private float _terrainDetailMaxXWorld;
    private float _terrainDetailMaxYWorld;
    private long _lastTerrainDetailRebuildTicks;
    private int _terrainProjectionCacheVersion;
    private int _terrainProjectionBuiltVersion = -1;
    private Vector3 _terrainProjectionCacheCameraPosition;
    private Vector3 _terrainProjectionCacheCameraTarget;
    private Vector3 _terrainProjectionCacheViewDirection;
    private float _terrainProjectionCacheYawRad = float.NaN;
    private float _terrainProjectionCachePitchRad = float.NaN;
    private float _terrainProjectionCacheDistanceM = float.NaN;
    private Size _terrainProjectionCacheClientSize = Size.Empty;
    private int _terrainLayerBitmapBuiltVersion = -1;
    private Vector3 _terrainLayerBitmapCameraPosition;
    private Vector3 _terrainLayerBitmapCameraTarget;
    private Vector3 _terrainLayerBitmapViewDirection;
    private Size _terrainLayerBitmapClientSize = Size.Empty;
    private int _staticStructureLayerCacheVersion;
    private int _staticStructureLayerBitmapBuiltVersion = -1;
    private Vector3 _staticStructureLayerBitmapCameraPosition;
    private Vector3 _staticStructureLayerBitmapCameraTarget;
    private Vector3 _staticStructureLayerBitmapViewDirection;
    private Size _staticStructureLayerBitmapClientSize = Size.Empty;
    private float _staticStructureProjectionCacheYawRad = float.NaN;
    private float _staticStructureProjectionCachePitchRad = float.NaN;
    private float _staticStructureProjectionCacheDistanceM = float.NaN;
    private long _lastFrameClockTicks;
    private long _lastPresentedFrameTicks;
    private long _lastFramePumpLogTicks;
    private double _framePumpAccumulatedGapMs;
    private double _framePumpMaxGapMs;
    private double _framePumpAccumulatedSimulationMs;
    private double _framePumpMaxSimulationMs;
    private int _framePumpPresentedFrames;
    private int _framePumpSimulationSteps;
    private double _simulationAccumulatorSec;
    private double _targetFrameIntervalSec;
    private double _smoothedFrameRate;
    private int _displayRefreshRateHz;
    private bool _collectProjectedFacesOnly;
    private bool? _gpuControlStylesActive;
    private bool _gpuGeometryPass;
    private bool _hasPresentedGpuFrame;
    private bool _fieldCompositeInteractionTestLogged;
    private MatchStartupPhase _matchStartupPhase = MatchStartupPhase.None;
    private long _matchStartupPhaseStartTicks;
    private long _lastMatchStartupLogTicks;
    private bool _matchStartupViewReady;
    private Task<Simulator3dHost.PreparedMatchWorldState>? _matchStartupPrepareTask;
    private Task<Simulator3dHost.PreparedLobbyWorldState>? _lobbyWorldRebuildTask;
    private string _lobbyWorldRebuildLabel = string.Empty;
    private bool _startMatchAfterLobbyWorldRebuild;
    private readonly bool _previewOnly;
    private readonly bool _sharedHostSimulation;
    private readonly string? _previewStructure;
    private readonly string? _previewTeam;
    private string? _previewFocusEntityId;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Handle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    public Simulator3dForm(Simulator3dOptions options)
        : this(new Simulator3dHost(options), options, sharedHostSimulation: false, externallyDrivenCompatibilityMode: false)
    {
    }

    internal static Simulator3dForm CreateExternalCompatibilityRuntime(Simulator3dOptions options)
    {
        var host = new Simulator3dHost(options);
        host.SetRendererMode("gpu");
        return new(host, options, sharedHostSimulation: false, externallyDrivenCompatibilityMode: true);
    }

    private Simulator3dForm(
        Simulator3dHost host,
        Simulator3dOptions options,
        bool sharedHostSimulation,
        bool externallyDrivenCompatibilityMode)
    {
        _host = host;
        _sharedHostSimulation = sharedHostSimulation;
        _externallyDrivenCompatibilityMode = externallyDrivenCompatibilityMode;
        _previewOnly = options.PreviewOnly;
        _previewStructure = Simulator3dOptions.NormalizePreviewStructure(options.PreviewStructure);
        _previewTeam = Simulator3dOptions.NormalizePreviewTeam(options.PreviewTeam);

        Text = "RM ARTINX A-Soul\u6a21\u62df\u5668";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 720);
        ClientSize = new Size(1440, 900);
        BackColor = Color.FromArgb(16, 20, 28);
        KeyPreview = true;
        TryApplyWindowIcon();

        _appState = options.StartInMatch ? SimulatorAppState.InMatch : SimulatorAppState.MainMenu;
        _paused = _appState != SimulatorAppState.InMatch
            || (options.StartInMatch && !_previewOnly && !_sharedHostSimulation);
        _lastFrameClockTicks = _frameClock.ElapsedTicks;
        _lastPresentedFrameTicks = _lastFrameClockTicks;
        _displayRefreshRateHz = ResolveDisplayRefreshRateHz();
        _targetFrameIntervalSec = 1.0 / Math.Max(120, _displayRefreshRateHz);
        _tacticalGroundTargetX = _host.World.Entities.FirstOrDefault(entity => string.Equals(entity.Team, _host.SelectedTeam, StringComparison.OrdinalIgnoreCase))?.X
            ?? 0.0;
        _tacticalGroundTargetY = _host.World.Entities.FirstOrDefault(entity => string.Equals(entity.Team, _host.SelectedTeam, StringComparison.OrdinalIgnoreCase))?.Y
            ?? 0.0;
        ApplyRendererControlStyles();
        InitializeMainMenuChrome();
        SyncLobbyDuelRoundInputFromHost();

        ResetCameraForMap();
        _observerPositionM = _cameraTargetM + new Vector3(0f, MathF.Max(8f, _cameraDistanceM * 0.55f), _cameraDistanceM * 0.55f);
        if (_previewOnly)
        {
            ConfigurePreviewMode();
        }
        else if (_host.IsMapComponentTestMode)
        {
            ConfigureMapComponentTestMode();
        }
        if (_appState != SimulatorAppState.MainMenu)
        {
            PreloadActiveMapTerrainAssets();
            if (_host.RequiresDeferredLobbyBootstrap)
            {
                QueueLobbyWorldRebuild("加载地图中");
            }
        }

        if (options.StartInMatch && !_previewOnly && !_sharedHostSimulation && !_host.IsMapComponentTestMode)
        {
            BeginMatchStartupSequence(resetWorld: !_host.RequiresDeferredLobbyBootstrap);
        }

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 1,
        };
        if (!_externallyDrivenCompatibilityMode)
        {
            _timer.Tick += (_, _) => OnFrameTick();
            _timer.Start();
            Application.Idle += OnApplicationIdle;

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
    }

    private void ConfigureMapComponentTestMode()
    {
        _observerMode = true;
        _observerPinned = false;
        _followSelection = false;
        _firstPersonView = false;
        _paused = false;
        _cameraDistanceM = Math.Max(_cameraDistanceM, 21f);
        _observerYawRad = -0.88f;
        _observerPitchRad = 0.20f;
        _observerPositionM = _cameraTargetM + new Vector3(0f, 8.6f, MathF.Max(11f, _cameraDistanceM * 0.46f));
        ReleaseMouseCapture();
    }

    private void TryApplyWindowIcon()
    {
        string[] candidates =
        {
            Path.Combine(_host.ProjectRootPath, "DarkLogo.png"),
            Path.Combine(AppContext.BaseDirectory, "DarkLogo.png"),
            @"E:\Artinx\260111new\Simulator\DarkLogo.png",
        };

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            IntPtr iconHandle = IntPtr.Zero;
            try
            {
                using Bitmap bitmap = new(candidate);
                iconHandle = bitmap.GetHicon();
                using Icon transientIcon = Icon.FromHandle(iconHandle);
                _windowIcon?.Dispose();
                _windowIcon = (Icon)transientIcon.Clone();
                Icon = _windowIcon;
                ApplyWindowIconToNativeHandle();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
            {
            }
            finally
            {
                if (iconHandle != IntPtr.Zero)
                {
                    DestroyIcon(iconHandle);
                }
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryApplyWindowIcon();
    }

    private void ApplyWindowIconToNativeHandle()
    {
        if (!IsHandleCreated || _windowIcon is null)
        {
            return;
        }

        SendMessage(Handle, WmSetIcon, (IntPtr)IconSmall, _windowIcon.Handle);
        SendMessage(Handle, WmSetIcon, (IntPtr)IconBig, _windowIcon.Handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ExitMatchGcControl();
            Application.Idle -= OnApplicationIdle;
            _timer.Dispose();
            DisposeGpuRenderer();
            _tinyHudFont.Dispose();
            _smallHudFont.Dispose();
            _hudMidFont.Dispose();
            _hudBigFont.Dispose();
            _titleFont.Dispose();
            _menuTitleFont.Dispose();
            _menuSubtitleFont.Dispose();
            _menuEyebrowFont.Dispose();
            _menuButtonFont.Dispose();
            _menuFootnoteFont.Dispose();
            _windowIcon?.Dispose();
            DisposeBackgroundVideo();
            _driveTelemetryForm?.Dispose();
            _cachedTerrainLayerBitmap?.Dispose();
            _cachedStaticStructureLayerBitmap?.Dispose();
            _cpuProjectileLayerGraphics?.Dispose();
            _cpuProjectileLayerBitmap?.Dispose();
            _fastEntityLayerGraphics?.Dispose();
            _fastProjectileLayerGraphics?.Dispose();
            _fastEntityLayerBitmap?.Dispose();
            _fastProjectileLayerBitmap?.Dispose();
            InvalidateHudPortraitCache();
            foreach (SolidBrush brush in _projectedFaceBrushCache.Values)
            {
                brush.Dispose();
            }

            foreach (Pen pen in _projectedFacePenCache.Values)
            {
                pen.Dispose();
            }

            _terrainColorBitmap?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ApplyRendererControlStyles();
        base.OnPaint(e);

        RenderFrameToGraphics(e.Graphics, allowGpuScene: true);
    }

    private void RenderFrameToGraphics(Graphics graphics, bool allowGpuScene)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        _uiButtons.Clear();

        bool gpuSceneAvailable = _appState == SimulatorAppState.InMatch
            && UseGpuRenderer
            && !UseFastFlatRenderer
            && allowGpuScene;
        if (!gpuSceneAvailable)
        {
            graphics.Clear(BackColor);
            DrawBackground(graphics);
        }
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
                if (gpuSceneAvailable)
                {
                    DrawGpuMatch(graphics);
                }
                else
                {
                    DrawInMatchWorld(graphics);
                    DrawInMatchOverlay(graphics);
                }

                MarkMatchStartupViewReady();
                break;
        }

        if (_appState == SimulatorAppState.Lobby && _lobbyWorldRebuildTask is not null)
        {
            DrawLobbyWorldLoadingOverlay(graphics);
        }
        else if (_appState == SimulatorAppState.MainMenu && _lobbyWorldRebuildTask is not null)
        {
            DrawMainMenuLoadingBadge(graphics);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_appState == SimulatorAppState.InMatch && UseGpuRenderer && !UseFastFlatRenderer)
        {
            return;
        }

        base.OnPaintBackground(e);
    }

    internal void ExternalResize(Size clientSize)
    {
        if (clientSize.Width <= 0 || clientSize.Height <= 0)
        {
            return;
        }

        ClientSize = clientSize;
    }

    internal void ExternalAdvanceFrame()
    {
        OnFrameTick();
    }

    internal void ExternalRender(Graphics graphics)
    {
        RenderFrameToGraphics(graphics, allowGpuScene: false);
    }

    internal void ExternalKeyDown(Keys keyCode, bool shiftDown, bool controlDown, bool altDown)
    {
        Keys data = keyCode;
        if (shiftDown)
        {
            data |= Keys.Shift;
        }

        if (controlDown)
        {
            data |= Keys.Control;
        }

        if (altDown)
        {
            data |= Keys.Alt;
        }

        OnKeyDownInternal(this, new KeyEventArgs(data));
    }

    internal void ExternalKeyUp(Keys keyCode, bool shiftDown, bool controlDown, bool altDown)
    {
        Keys data = keyCode;
        if (shiftDown)
        {
            data |= Keys.Shift;
        }

        if (controlDown)
        {
            data |= Keys.Control;
        }

        if (altDown)
        {
            data |= Keys.Alt;
        }

        OnKeyUpInternal(this, new KeyEventArgs(data));
    }

    internal void ExternalMouseDown(MouseButtons button, Point location, int wheelDelta = 0)
    {
        OnMouseDownInternal(this, new MouseEventArgs(button, 1, location.X, location.Y, wheelDelta));
    }

    internal void ExternalMouseUp(MouseButtons button, Point location, int wheelDelta = 0)
    {
        OnMouseUpInternal(this, new MouseEventArgs(button, 1, location.X, location.Y, wheelDelta));
    }

    internal void ExternalMouseWheel(Point location, int wheelDelta)
    {
        OnMouseWheelInternal(this, new MouseEventArgs(MouseButtons.None, 0, location.X, location.Y, wheelDelta));
    }

    internal void ExternalMouseMove(Point location, Point delta, bool capturedLook)
    {
        if (_appState != SimulatorAppState.InMatch)
        {
            _lastMouse = location;
            if (_draggingLobbyAutoAimSlider && _appState == SimulatorAppState.Lobby)
            {
                UpdateLobbyAutoAimSlider(location);
            }

            return;
        }

        _lastMouse = location;
        if (capturedLook && !_paused)
        {
            if (_observerMode || _sharedHostSimulation)
            {
                _observerYawRad = WrapAngleRadians(_observerYawRad + delta.X * ObserverYawSensitivityRadPerPixel);
                _observerPitchRad = Math.Clamp(_observerPitchRad - delta.Y * ObserverPitchSensitivityRadPerPixel, -1.12f, 1.12f);
            }
            else
            {
                _pendingMouseYawDeltaDeg += delta.X * 0.18f;
                _pendingMousePitchDeltaDeg -= delta.Y * 0.14f;
                if (!_firstPersonView)
                {
                    _cameraPitchRad = Math.Clamp(
                        _cameraPitchRad - delta.Y * (0.14f * MathF.PI / 180f),
                        0.12f,
                        1.20f);
                }
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

    internal bool ShouldCaptureMouseExternally()
    {
        return _appState == SimulatorAppState.InMatch
            && !_paused
            && !_tacticalMode
            && !_observerPinned;
    }

    internal bool ExternalRuntimeClosed => IsDisposed || Disposing;

    private void DrawInMatchWorld(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        if (UseFastFlatRenderer)
        {
            DrawFastFlatMatch(graphics);
            return;
        }

        DrawFloor(graphics);
        DrawFacilities(graphics);
        DrawTeamTopNeonLights(graphics);
        DrawEntities(graphics);
        if (!_previewOnly && !_tacticalMode)
        {
            DrawProjectiles(graphics);
            DrawCombatMarkers(graphics);
        }
    }

    private void DrawInMatchOverlay(Graphics graphics)
    {
        if (_previewOnly)
        {
            long previewPhaseStart = Stopwatch.GetTimestamp();
            DrawPreviewOnlyOverlay(graphics);
            TrackGpuOverlayPhase("preview", previewPhaseStart);
            return;
        }

        bool firstPersonHud = IsFirstPersonHudVisible();

        if (firstPersonHud)
        {
            long weaponPhaseStart = Stopwatch.GetTimestamp();
            DrawWeaponLockOverlay(graphics);
            TrackGpuOverlayPhase("weapon", weaponPhaseStart);
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        long viewPhaseStart = Stopwatch.GetTimestamp();
        DrawHeroLobSecondaryViewport(graphics);
        DrawDeadSelectedEntityScreenTint(graphics);
        if (firstPersonHud)
        {
            DrawCrosshair(graphics);
            DrawDeploymentPrompt(graphics);
            DrawEnergyActivationPrompt(graphics);
            DrawHeroDeploymentFeedOverlay(graphics);
        }

        TrackGpuOverlayPhase("view", viewPhaseStart);

        long combatPhaseStart = Stopwatch.GetTimestamp();
        if (UseGpuRenderer && !UseFastFlatRenderer && _hasPresentedGpuFrame)
        {
            if (!_previewOnly)
            {
                DrawCombatMarkers(graphics);
            }

            if (_showCollisionDebug)
            {
                DrawEntityOverlayBars(graphics);
            }
        }
        TrackGpuOverlayPhase("combat", combatPhaseStart);

        long hudPhaseStart = Stopwatch.GetTimestamp();
        DrawHud(graphics);
        DrawFpsBadge(graphics);
        if (firstPersonHud)
        {
            DrawCentralQuarterGauges(graphics);
            DrawCenterBuffToasts(graphics);
        }
        TrackGpuOverlayPhase("hud", hudPhaseStart);

        long statusPhaseStart = Stopwatch.GetTimestamp();
        DrawPlayerStatusPanelV2(graphics);
        DrawUnitTestScenarioOverlay(graphics);
        DrawRespawnInvincibilityBadge(graphics);
        DrawHeroLobSubviewOverlay(graphics);
        DrawKeyGuideOverlay(graphics);
        DrawObserverOverlay(graphics);
        DrawOrientationWidget(graphics);
        TrackGpuOverlayPhase("status", statusPhaseStart);

        long debugPhaseStart = Stopwatch.GetTimestamp();
        DrawF3DebugPoseOverlay(graphics);
        DrawVisionPoseSolveOverlay(graphics);
        DrawFineTerrainInMatchEditorOverlay(graphics);
        DrawTacticalOverlay(graphics);
        TrackGpuOverlayPhase("debug", debugPhaseStart);

        long eventPhaseStart = Stopwatch.GetTimestamp();
        DrawMatchEventFeed(graphics);
        DrawDuelRoundRestartHint(graphics);
        if (_showDebugSidebars)
        {
            DrawDecisionDeploymentPanel(graphics);
        }
        TrackGpuOverlayPhase("events", eventPhaseStart);

        if (IsMatchStartupActive)
        {
            long modalPhaseStart = Stopwatch.GetTimestamp();
            DrawMatchStartupOverlay(graphics);
            TrackGpuOverlayPhase("modal", modalPhaseStart);
        }
        else if (_paused)
        {
            long modalPhaseStart = Stopwatch.GetTimestamp();
            if (_host.IsDuelMode && _host.IsDuelFinished)
            {
                DrawDuelFinishedOverlay(graphics);
            }
            else
            {
                DrawPauseOverlay(graphics);
            }
            TrackGpuOverlayPhase("modal", modalPhaseStart);
        }
    }

    private bool IsFirstPersonHudVisible()
        => _firstPersonView && !_observerMode && !_sharedHostSimulation;

    private bool IsMatchStartupActive
        => _appState == SimulatorAppState.InMatch
            && (_matchStartupPhase == MatchStartupPhase.Loading
                || _matchStartupPhase == MatchStartupPhase.Countdown);

    private void OnFrameTick()
    {
        if (_appState == SimulatorAppState.MainMenu)
        {
            UpdateMainMenuChrome();
        }

        CompleteLobbyWorldRebuildIfReady();
        UpdateMatchGcControl();
        UpdateMouseCaptureState();
        if (_appState == SimulatorAppState.Lobby || IsMatchStartupActive)
        {
            WarmTerrainCacheGpuAssets();
        }

        if (_appState == SimulatorAppState.InMatch
            && _paused
            && !IsMatchStartupActive
            && !_previewOnly
            && !_observerMode
            && !_fineTerrainInMatchEditMode
            && !_sharedHostSimulation)
        {
            _simulationAccumulatorSec = 0.0;
            _lastFrameClockTicks = _frameClock.ElapsedTicks;
            System.Threading.Thread.Sleep(16);
            return;
        }

        long presentNowTicks = _frameClock.ElapsedTicks;
        double secondsSincePresent = Math.Max(0.0, (presentNowTicks - _lastPresentedFrameTicks) / (double)Stopwatch.Frequency);
        double targetFrameIntervalSec = ResolveCurrentTargetFrameIntervalSec();
        if (secondsSincePresent + 0.00035 < targetFrameIntervalSec)
        {
            PaceUntilNextFrame(targetFrameIntervalSec, secondsSincePresent);
            return;
        }

        if (_appState == SimulatorAppState.InMatch)
        {
            UpdateObserverMotion(secondsSincePresent);
            UpdateFineTerrainInMatchEditor(secondsSincePresent);
            UpdateMatchStartupState(presentNowTicks);
        }

        long simulationStartTicks = Stopwatch.GetTimestamp();
        int simulatedSteps = 0;
        if (_appState == SimulatorAppState.InMatch && IsMatchStartupActive)
        {
            _simulationAccumulatorSec = 0.0;
            _lastFrameClockTicks = _frameClock.ElapsedTicks;
            ResetLiveInput();
        }
        else if (_appState == SimulatorAppState.InMatch && !_paused && !_sharedHostSimulation)
        {
            simulatedSteps = AdvanceSimulationClock();
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
            if (_previewOnly)
            {
                _host.World.GameTimeSec += secondsSincePresent;
            }
        }

        if (_appState == SimulatorAppState.InMatch)
        {
            SyncFineTerrainRuntimeTargetsIfNeeded();
        }

        double simulationMs = (Stopwatch.GetTimestamp() - simulationStartTicks) * 1000.0 / Stopwatch.Frequency;
        TrackFramePumpPerf(presentNowTicks, secondsSincePresent, simulationMs, simulatedSteps);
        _lastPresentedFrameTicks = presentNowTicks;
        if (secondsSincePresent > 1e-4)
        {
            double instantFps = 1.0 / secondsSincePresent;
            _smoothedFrameRate = _smoothedFrameRate <= 1e-3
                ? instantFps
                : _smoothedFrameRate * 0.88 + instantFps * 0.12;
        }

        PresentFrameRequest();
    }

    private void PresentFrameRequest()
    {
        Invalidate();
        if (_appState == SimulatorAppState.InMatch
            && !_paused
            && !_previewOnly
            && !_sharedHostSimulation
            && UseGpuRenderer
            && !UseFastFlatRenderer
            && IsHandleCreated
            && Visible)
        {
            Update();
        }
    }

    private double ResolveCurrentTargetFrameIntervalSec()
        => _appState == SimulatorAppState.MainMenu
            ? Math.Max(_targetFrameIntervalSec, ResolveBackgroundVideoFrameIntervalSec())
            : _targetFrameIntervalSec;

    private void PaceUntilNextFrame(double targetFrameIntervalSec, double secondsSincePresent)
    {
        double remainingMs = (targetFrameIntervalSec - secondsSincePresent) * 1000.0;
        if (_appState == SimulatorAppState.InMatch && !_paused && remainingMs <= 8.0)
        {
            if (remainingMs > 1.2)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(64);
            }

            return;
        }

        if (remainingMs <= 0.20)
        {
            Thread.Yield();
            return;
        }

        Thread.Sleep(Math.Clamp((int)Math.Floor(remainingMs), 1, 8));
    }

    private void TrackFramePumpPerf(long nowTicks, double secondsSincePresent, double simulationMs, int simulatedSteps)
    {
        if (_appState != SimulatorAppState.InMatch || _previewOnly)
        {
            return;
        }

        double gapMs = secondsSincePresent * 1000.0;
        _framePumpPresentedFrames++;
        _framePumpSimulationSteps += simulatedSteps;
        _framePumpAccumulatedGapMs += gapMs;
        _framePumpMaxGapMs = Math.Max(_framePumpMaxGapMs, gapMs);
        _framePumpAccumulatedSimulationMs += simulationMs;
        _framePumpMaxSimulationMs = Math.Max(_framePumpMaxSimulationMs, simulationMs);

        if (_lastFramePumpLogTicks > 0
            && (nowTicks - _lastFramePumpLogTicks) / (double)Stopwatch.Frequency < 2.0)
        {
            return;
        }

        int frames = Math.Max(1, _framePumpPresentedFrames);
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} "
            + $"frames={_framePumpPresentedFrames} "
            + $"gapAvg={_framePumpAccumulatedGapMs / frames:0.00}ms "
            + $"gapMax={_framePumpMaxGapMs:0.00}ms "
            + $"simAvg={_framePumpAccumulatedSimulationMs / frames:0.00}ms "
            + $"simMax={_framePumpMaxSimulationMs:0.00}ms "
            + $"simSteps={_framePumpSimulationSteps} "
            + $"targetHz={1.0 / Math.Max(1e-6, ResolveCurrentTargetFrameIntervalSec()):0.0} "
            + $"fps={_smoothedFrameRate:0.0}";

        SimulatorRuntimeLog.Append("frame_pump.log", line);

        _lastFramePumpLogTicks = nowTicks;
        _framePumpPresentedFrames = 0;
        _framePumpSimulationSteps = 0;
        _framePumpAccumulatedGapMs = 0.0;
        _framePumpMaxGapMs = 0.0;
        _framePumpAccumulatedSimulationMs = 0.0;
        _framePumpMaxSimulationMs = 0.0;
    }

    private void ApplyRendererControlStyles()
    {
        bool gpu = string.Equals(_host.ActiveRendererMode, "gpu", StringComparison.OrdinalIgnoreCase)
            && _appState == SimulatorAppState.InMatch
            && !UseFastFlatRenderer;
        if (_gpuControlStylesActive == gpu)
        {
            return;
        }

        DoubleBuffered = !gpu;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, !gpu);
        UpdateStyles();
        _gpuControlStylesActive = gpu;
    }

    private void OnApplicationIdle(object? sender, EventArgs e)
    {
        while (IsHandleCreated && Visible && AppStillIdle())
        {
            OnFrameTick();
        }
    }

    private static bool AppStillIdle()
    {
        return !PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
    }

    private int AdvanceSimulationClock()
    {
        long currentTicks = _frameClock.ElapsedTicks;
        long elapsedTicks = Math.Max(0, currentTicks - _lastFrameClockTicks);
        _lastFrameClockTicks = currentTicks;

        double elapsedSec = Math.Min(0.050, elapsedTicks / (double)Stopwatch.Frequency);
        double scaledElapsedSec = elapsedSec * Math.Clamp(_simulationTimeScale, 0.05, 3.0);
        double fixedDt = Math.Max(_tacticalMode ? 0.024 : 0.016, _host.DeltaTimeSec);
        _simulationAccumulatorSec = Math.Min(_simulationAccumulatorSec + scaledElapsedSec, fixedDt * MaxSimulationCatchUpSteps);

        PlayerControlState firstState = BuildPlayerControlState();
        PlayerControlState repeatedState = firstState with
        {
            TurretYawDeltaDeg = 0.0,
            GimbalPitchDeltaDeg = 0.0,
            JumpRequested = false,
            BuyAmmoRequested = false,
            EnergyActivationPressed = false,
            HeroDeployToggleRequested = false,
            SuperCapActive = false,
            SentryStanceToggleRequested = false,
        };

        int simulatedSteps = 0;
        while (_simulationAccumulatorSec + 1e-9 >= fixedDt && simulatedSteps < MaxSimulationCatchUpSteps)
        {
            _host.Step(simulatedSteps == 0 ? firstState : repeatedState);
            CaptureCombatMarkersFromLatestReport();
            if (_host.IsDuelMode && _host.IsDuelFinished)
            {
                SetPaused(true);
                _simulationAccumulatorSec = 0.0;
                simulatedSteps++;
                break;
            }

            _simulationAccumulatorSec -= fixedDt;
            simulatedSteps++;
        }

        if (simulatedSteps > 0)
        {
            UpdateSelectedBuffNotifications();
            CaptureExperienceGainNotifications();
            CapturePowerCutNotifications();
        }

        AdvanceCombatMarkers((float)elapsedSec);
        AdvanceMatchEventFeed((float)elapsedSec);
        AdvanceBuffToasts((float)elapsedSec);
        return simulatedSteps;
    }

    private void OnMouseDownInternal(object? sender, MouseEventArgs eventArgs)
    {
        if (_appState == SimulatorAppState.Lobby
            && eventArgs.Button == MouseButtons.Left
            && _lobbyDuelRoundInputFocused
            && !_lobbyDuelRoundInputRect.Contains(eventArgs.Location))
        {
            CommitLobbyDuelRoundInput();
            _lobbyDuelRoundInputFocused = false;
        }

        if (eventArgs.Button == MouseButtons.Left)
        {
            string? action = ResolveUiAction(eventArgs.Location);
            if (!string.IsNullOrWhiteSpace(action))
            {
                if (!CanExecuteUiActionForCurrentState(action))
                {
                    return;
                }

                ExecuteUiAction(action);
                return;
            }
        }

        if (_appState == SimulatorAppState.Lobby
            && eventArgs.Button == MouseButtons.Left
            && _lobbyAutoAimSliderRect.Contains(eventArgs.Location))
        {
            _draggingLobbyAutoAimSlider = true;
            UpdateLobbyAutoAimSlider(eventArgs.Location);
            return;
        }

        if (_appState != SimulatorAppState.InMatch)
        {
            return;
        }

        if (!_externallyDrivenCompatibilityMode && !Focused)
        {
            Focus();
        }

        if (_paused)
        {
            return;
        }

        if (_fineTerrainInMatchEditMode)
        {
            return;
        }

        if (_observerMode || _sharedHostSimulation)
        {
            return;
        }

        if (_fineTerrainInMatchEditMode)
        {
            return;
        }

        if (_tacticalMode && eventArgs.Button == MouseButtons.Left)
        {
            HandleTacticalCanvasClick(eventArgs.Location);
            return;
        }

        if (eventArgs.Button == MouseButtons.Left)
        {
            _firePressed = true;
            _pendingSingleFireRequest = true;
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
            _draggingLobbyAutoAimSlider = false;
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
            if (_draggingLobbyAutoAimSlider && _appState == SimulatorAppState.Lobby)
            {
                UpdateLobbyAutoAimSlider(eventArgs.Location);
            }

            return;
        }

        if (_mouseCaptureActive && !_paused)
        {
            Point center = new(ClientSize.Width / 2, ClientSize.Height / 2);
            Point lookDelta = new(eventArgs.X - center.X, eventArgs.Y - center.Y);
            if (lookDelta.X != 0 || lookDelta.Y != 0)
            {
                if (_observerMode || _sharedHostSimulation)
                {
                    _observerYawRad = WrapAngleRadians(_observerYawRad + lookDelta.X * ObserverYawSensitivityRadPerPixel);
                    _observerPitchRad = Math.Clamp(_observerPitchRad - lookDelta.Y * ObserverPitchSensitivityRadPerPixel, -1.12f, 1.12f);
                }
                else
                {
                    _pendingMouseYawDeltaDeg += lookDelta.X * 0.18f;
                    _pendingMousePitchDeltaDeg -= lookDelta.Y * 0.14f;
                    if (!_firstPersonView)
                    {
                        _cameraPitchRad = Math.Clamp(
                            _cameraPitchRad - lookDelta.Y * (0.14f * MathF.PI / 180f),
                            0.12f,
                            1.20f);
                    }
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

        if (_observerMode)
        {
            _observerMoveSpeedMps = Math.Clamp(_observerMoveSpeedMps * (eventArgs.Delta > 0 ? 1.12f : 0.89f), 0.8f, 28f);
            return;
        }

        float zoomAmount = eventArgs.Delta > 0 ? 0.90f : 1.10f;
        if (!_firstPersonView)
        {
            _thirdPersonFollowDistanceScale = Math.Clamp(_thirdPersonFollowDistanceScale * zoomAmount, 0.55f, 3.0f);
            _cameraDistanceM = Math.Clamp(_cameraDistanceM * zoomAmount, 3.0f, 250f);
            return;
        }

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
                ToggleMainMenuStartSection();
                break;
            case Keys.Escape:
                Close();
                break;
            case Keys.D1:
                _host.SetRendererMode("gpu");
                ApplyRendererControlStyles();
                break;
            case Keys.D2:
                _host.SetRendererMode("opengl");
                ApplyRendererControlStyles();
                break;
            case Keys.D3:
                _host.SetRendererMode("moderngl");
                ApplyRendererControlStyles();
                break;
            case Keys.D4:
                _host.SetRendererMode("native_cpp");
                ApplyRendererControlStyles();
                break;
            case Keys.F7:
                ToggleDriveTelemetryWindow();
                break;
        }
    }

    private void EnterLobby()
    {
        SelectLobbyRole(ResolveLobbySelectedRoleKey());
        SyncLobbyDuelRoundInputFromHost();
        _appState = SimulatorAppState.Lobby;
        _paused = true;
        _matchStartupPhase = MatchStartupPhase.None;
        _matchStartupViewReady = false;
        ResetLiveInput();
        ReleaseMouseCapture();
        if (_host.RequiresDeferredLobbyBootstrap)
        {
            QueueLobbyWorldRebuild("加载地图与世界中");
        }
        else
        {
            PreloadActiveMapTerrainAssets();
        }

        Invalidate();
    }

    private void SyncLobbyDuelRoundInputFromHost()
    {
        _lobbyDuelRoundInputText = Math.Clamp(_host.DuelRoundLimit, 1, 99).ToString();
        _lobbyDuelRoundInputFocused = false;
    }

    private void CommitLobbyDuelRoundInput(bool restoreOnEmpty = true)
    {
        string trimmed = _lobbyDuelRoundInputText.Trim();
        if (int.TryParse(trimmed, out int roundLimit))
        {
            int normalized = Math.Clamp(roundLimit, 1, 99);
            _host.SetDuelRoundLimit(normalized);
            _lobbyDuelRoundInputText = normalized.ToString();
            return;
        }

        if (restoreOnEmpty)
        {
            SyncLobbyDuelRoundInputFromHost();
        }
    }

    private void AdjustLobbyDuelRoundInput(int delta)
    {
        int current = _host.DuelRoundLimit;
        if (int.TryParse(_lobbyDuelRoundInputText.Trim(), out int parsed))
        {
            current = parsed;
        }

        int next = Math.Clamp(current + delta, 1, 99);
        _lobbyDuelRoundInputText = next.ToString();
        _host.SetDuelRoundLimit(next);
    }

    private bool HandleLobbyDuelRoundInputKey(KeyEventArgs eventArgs)
    {
        if (!_host.IsDuelMode || !_lobbyDuelRoundInputFocused)
        {
            return false;
        }

        switch (eventArgs.KeyCode)
        {
            case Keys.Enter:
                CommitLobbyDuelRoundInput();
                _lobbyDuelRoundInputFocused = false;
                return true;
            case Keys.Escape:
                SyncLobbyDuelRoundInputFromHost();
                return true;
            case Keys.Back:
                if (_lobbyDuelRoundInputText.Length > 0)
                {
                    _lobbyDuelRoundInputText = _lobbyDuelRoundInputText[..^1];
                }

                return true;
            case Keys.Delete:
                _lobbyDuelRoundInputText = string.Empty;
                return true;
            case Keys.Up:
            case Keys.Right:
                AdjustLobbyDuelRoundInput(1);
                return true;
            case Keys.Down:
            case Keys.Left:
                AdjustLobbyDuelRoundInput(-1);
                return true;
        }

        char? digit = eventArgs.KeyCode switch
        {
            >= Keys.D0 and <= Keys.D9 => (char)('0' + (eventArgs.KeyCode - Keys.D0)),
            >= Keys.NumPad0 and <= Keys.NumPad9 => (char)('0' + (eventArgs.KeyCode - Keys.NumPad0)),
            _ => null,
        };
        if (!digit.HasValue)
        {
            return false;
        }

        if (_lobbyDuelRoundInputText.Length >= 2)
        {
            _lobbyDuelRoundInputText = digit.Value.ToString();
        }
        else
        {
            _lobbyDuelRoundInputText += digit.Value;
        }

        if (int.TryParse(_lobbyDuelRoundInputText, out int roundLimit))
        {
            roundLimit = Math.Clamp(roundLimit, 1, 99);
            _host.SetDuelRoundLimit(roundLimit);
            _lobbyDuelRoundInputText = roundLimit.ToString();
        }

        return true;
    }

    private void QueueLobbyWorldRebuild(string label)
    {
        _lobbyWorldRebuildLabel = label;
        _lobbyWorldRebuildTask = _host.PrepareLobbyWorldAsync();
        Invalidate();
    }

    private void DiscardPendingLobbyWorldRebuild()
    {
        _lobbyWorldRebuildTask = null;
        _lobbyWorldRebuildLabel = string.Empty;
        _startMatchAfterLobbyWorldRebuild = false;
    }

    private void CompleteLobbyWorldRebuildIfReady()
    {
        if (_lobbyWorldRebuildTask is not { IsCompleted: true } rebuildTask)
        {
            return;
        }

        _lobbyWorldRebuildTask = null;
        try
        {
            Simulator3dHost.PreparedLobbyWorldState prepared = rebuildTask.GetAwaiter().GetResult();
            _host.ApplyPreparedLobbyWorld(prepared);
            PreloadActiveMapTerrainAssets();
            if (_appState == SimulatorAppState.Lobby)
            {
                ResetCameraForMap();
                SelectLobbyRole(ResolveLobbySelectedRoleKey());
            }
            else if (_appState == SimulatorAppState.InMatch)
            {
                ResetCameraForMap();
                SnapCameraToSelectedEntity();
            }
        }
        catch
        {
            _startMatchAfterLobbyWorldRebuild = false;
            throw;
        }

        if (_startMatchAfterLobbyWorldRebuild)
        {
            _startMatchAfterLobbyWorldRebuild = false;
            BeginMatchStartupSequence(resetWorld: true);
            return;
        }

        Invalidate();
    }

    private void DrawLobbyWorldLoadingOverlay(Graphics graphics)
    {
        using var overlay = new SolidBrush(Color.FromArgb(196, 7, 11, 16));
        graphics.FillRectangle(overlay, ClientRectangle);

        int panelWidth = Math.Min(560, Math.Max(420, ClientSize.Width - 120));
        Rectangle panel = new((ClientSize.Width - panelWidth) / 2, Math.Max(84, ClientSize.Height / 2 - 92), panelWidth, 184);
        using GraphicsPath path = CreateRoundedRectangle(panel, 12);
        using var fill = new SolidBrush(Color.FromArgb(234, 15, 21, 28));
        using var border = new Pen(Color.FromArgb(168, 112, 196, 255), 1.1f);
        using var titleBrush = new SolidBrush(Color.FromArgb(244, 242, 248, 255));
        using var textBrush = new SolidBrush(Color.FromArgb(226, 206, 220, 232));
        using var mutedBrush = new SolidBrush(Color.FromArgb(190, 164, 178, 192));
        using var accentBrush = new SolidBrush(Color.FromArgb(236, 88, 154, 244));
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string title = "LOADING";
        string detail = string.IsNullOrWhiteSpace(_lobbyWorldRebuildLabel)
            ? "Preparing map preset, runtime grid and startup world"
            : _lobbyWorldRebuildLabel;
        graphics.DrawString(title, _smallHudFont, mutedBrush, new RectangleF(panel.X, panel.Y + 22, panel.Width, 22), centered);
        graphics.DrawString(detail, _hudMidFont, titleBrush, new RectangleF(panel.X + 22, panel.Y + 48, panel.Width - 44, 28), centered);

        Rectangle bar = new(panel.X + 58, panel.Y + 98, panel.Width - 116, 14);
        using GraphicsPath barPath = CreateRoundedRectangle(bar, 7);
        using var barBack = new SolidBrush(Color.FromArgb(108, 42, 52, 66));
        graphics.FillPath(barBack, barPath);
        float elapsedSec = (float)(_frameClock.ElapsedTicks / (double)Stopwatch.Frequency);
        float pulseWidth = Math.Clamp(bar.Width * 0.28f, 82f, 146f);
        float pulseX = bar.X + ((MathF.Sin(elapsedSec * 2.3f) * 0.5f + 0.5f) * Math.Max(0f, bar.Width - pulseWidth));
        Rectangle pulseRect = new((int)MathF.Round(pulseX), bar.Y, Math.Max(1, (int)MathF.Round(pulseWidth)), bar.Height);
        using GraphicsPath pulsePath = CreateRoundedRectangle(pulseRect, 7);
        graphics.FillPath(accentBrush, pulsePath);

        graphics.DrawString($"场地：{ResolveLobbyMapLabel()}", _tinyHudFont, textBrush, new RectangleF(panel.X + 24, panel.Y + 124, panel.Width - 48, 22), centered);
        graphics.DrawString("界面可继续操作，大厅世界仍在后台准备。", _tinyHudFont, mutedBrush, new RectangleF(panel.X + 24, panel.Y + 146, panel.Width - 48, 22), centered);
    }

    private void DrawMainMenuLoadingBadge(Graphics graphics)
    {
        Rectangle badge = new(20, 20, 280, 54);
        DrawPanel(graphics, badge, alpha: 156);
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        using var detailBrush = new SolidBrush(Color.FromArgb(194, 208, 220, 232));
        graphics.DrawString("后台正在准备大厅世界", _smallHudFont, titleBrush, badge.X + 12, badge.Y + 9);
        graphics.DrawString(string.IsNullOrWhiteSpace(_lobbyWorldRebuildLabel) ? "加载中" : _lobbyWorldRebuildLabel, _tinyHudFont, detailBrush, badge.X + 12, badge.Y + 29);
    }

    private void HandleLobbyKey(KeyEventArgs eventArgs)
    {
        if (HandleLobbyDuelRoundInputKey(eventArgs))
        {
            return;
        }

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
                if (_host.SetInfantryMode(_host.InfantryMode == "full" ? "balance" : "full", rebuildWorld: false))
                {
                    SelectLobbyRole("infantry");
                    QueueLobbyWorldRebuild("步兵形态更新中");
                }
                break;
            case Keys.R:
                _host.ToggleRicochet();
                break;
            case Keys.F6:
                _host.ReloadDecisionDeploymentProfile();
                break;
            case Keys.F7:
                ToggleDriveTelemetryWindow();
                break;
        }
    }

    private void HandleInMatchKey(KeyEventArgs eventArgs)
    {
        if (IsMatchStartupActive)
        {
            if (eventArgs.KeyCode == Keys.Escape || eventArgs.KeyCode == Keys.L)
            {
                ReturnToLobby();
            }

            return;
        }

        switch (eventArgs.KeyCode)
        {
            case Keys.P:
                SetPaused(!_paused);
                break;
            case Keys.N:
                if (_paused)
                {
                    _host.Step(BuildPlayerControlState(forceEnable: true));
                }
                break;
            case Keys.R:
                if (_paused)
                {
                    BeginMatchStartupSequence(resetWorld: true);
                }

                break;
            case Keys.Tab:
                if (_fineTerrainInMatchEditMode)
                {
                    CycleFineTerrainInMatchEditorSelection(eventArgs.Shift ? -1 : 1);
                    break;
                }

                _host.CycleSelectedEntity(eventArgs.Shift ? -1 : 1);
                break;
            case Keys.Enter:
                if (_host.IsDuelMode && _host.GetDuelMatchSnapshot().WaitingForNextRound)
                {
                    if (_host.StartNextDuelRoundNow())
                    {
                        _simulationAccumulatorSec = 0.0;
                        ResetLiveInput();
                        SnapCameraToSelectedEntity();
                        InvalidateGpuOverlayLayer();
                    }

                    break;
                }

                if (_fineTerrainInMatchEditMode)
                {
                    TrySelectFineTerrainCompositeAtAnchor(cycleDirection: 0);
                }

                break;
            case Keys.F:
                if (_observerMode || _sharedHostSimulation)
                {
                    break;
                }

                if (!IsEnergyActivatorSelected())
                {
                    _followSelection = true;
                    SnapCameraToSelectedEntity();
                }
                break;
            case Keys.Q:
                if (!_observerMode && !_sharedHostSimulation)
                {
                    _host.ToggleSelectedAutoAimTargetMode();
                }
                break;
            case Keys.V:
                if (!_observerMode)
                {
                    ToggleViewMode();
                }
                break;
            case Keys.T:
                ToggleAutoAimAssistMode();
                break;
            case Keys.C:
                if (eventArgs.Control && _observerMode && !_observerPinned)
                {
                    SpawnPinnedSpectatorWindow();
                }

                break;
            case Keys.X:
                break;
            case Keys.H:
                ToggleTacticalMode();
                break;
            case Keys.PageUp:
                if (!_host.IsSingleUnitTestMode && !_host.IsUnitTestMode)
                {
                    _host.CycleMapPreset(1);
                    ResetCameraForMap();
                }
                break;
            case Keys.PageDown:
                if (!_host.IsSingleUnitTestMode && !_host.IsUnitTestMode)
                {
                    _host.CycleMapPreset(-1);
                    ResetCameraForMap();
                }
                break;
            case Keys.D1:
                if (TryHandleFocusSandboxRoleHotkey("robot_1"))
                {
                    break;
                }

                _host.SetRendererMode("gpu");
                ApplyRendererControlStyles();
                break;
            case Keys.D2:
                if (TryHandleFocusSandboxRoleHotkey("robot_3"))
                {
                    break;
                }

                _host.SetRendererMode("opengl");
                ApplyRendererControlStyles();
                break;
            case Keys.D3:
                if (TryHandleFocusSandboxRoleHotkey("robot_7"))
                {
                    break;
                }

                _host.SetRendererMode("moderngl");
                ApplyRendererControlStyles();
                break;
            case Keys.D4:
                _host.SetRendererMode("native_cpp");
                ApplyRendererControlStyles();
                break;
            case Keys.F6:
                _host.ReloadDecisionDeploymentProfile();
                break;
            case Keys.F4:
                _showProjectileTrails = !_showProjectileTrails;
                break;
            case Keys.F5:
                _showKeyGuide = !_showKeyGuide;
                break;
            case Keys.F3:
                _showCollisionDebug = !_showCollisionDebug;
                break;
            case Keys.F7:
                ToggleDriveTelemetryWindow();
                break;
            case Keys.F8:
                _showVisionPoseSolve = !_showVisionPoseSolve;
                break;
            case Keys.F9:
                ToggleFineTerrainInMatchEditor();
                break;
            case Keys.F2:
                ToggleObserverMode();
                break;
            case Keys.S:
                if (_fineTerrainInMatchEditMode && eventArgs.Control)
                {
                    SaveFineTerrainInMatchEditor(stayInEditMode: true);
                }

                break;
            case Keys.Escape:
                SetPaused(!_paused);
                break;
            case Keys.L:
                if (_paused)
                {
                    ReturnToLobby();
                }

                break;
        }
    }

    private bool IsEnergyActivatorSelected()
    {
        SimulationEntity? entity = _host.SelectedEntity;
        return entity is not null
            && (string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase));
    }

    private bool TryHandleFocusSandboxRoleHotkey(string entityKey)
    {
        if (!_host.IsFocusSandboxMode)
        {
            return false;
        }

        _host.SetSingleUnitTestFocus(entityKey: entityKey);
        _followSelection = true;
        SnapCameraToSelectedEntity();
        return true;
    }

    private void DrawBackground(Graphics graphics)
    {
        if (TryDrawBackgroundVideo(graphics))
        {
            return;
        }

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
        _ = UseModernMainMenuChrome();

        float startExpand = (float)_mainMenuStartExpandedVisual;
        float editorExpand = (float)_mainMenuEditorExpandedVisual;
        float pulse = 0.5f + 0.5f * MathF.Sin((float)_mainMenuPulseTimeSec * 1.55f);
        Color primaryAccent = Color.FromArgb(72, 136, 206);
        Color secondaryAccent = Color.FromArgb(64, 112, 168);
        Color quietAccent = Color.FromArgb(78, 108, 140);
        int horizontalMargin = Math.Clamp(ClientSize.Width / 28, 24, 42);
        int verticalMargin = Math.Clamp(ClientSize.Height / 18, 18, 54);
        int panelWidth = Math.Min(Math.Clamp(ClientSize.Width / 3, 356, 436), Math.Max(300, ClientSize.Width - horizontalMargin * 2));
        int rowWidth = panelWidth - 56;
        int buttonHeight = ClientSize.Height < 650 ? 50 : 58;
        int subButtonHeight = ClientSize.Height < 650 ? 36 : 42;
        int subButtonGap = ClientSize.Height < 650 ? 8 : 10;
        int sectionGap = ClientSize.Height < 650 ? 12 : 16;
        int headerHeight = ClientSize.Height < 650 ? 104 : 116;
        int submenuBlockHeight = subButtonHeight * 3 + subButtonGap * 2;
        int startReserve = startExpand > 0.05f ? submenuBlockHeight + 12 : 0;
        int editorReserve = editorExpand > 0.05f ? submenuBlockHeight + 12 : 0;
        int panelHeight = headerHeight + buttonHeight * 3 + sectionGap * 2 + startReserve + editorReserve + 34;
        int maxPanelHeight = Math.Max(260, ClientSize.Height - verticalMargin * 2);
        if (panelHeight > maxPanelHeight)
        {
            subButtonHeight = 34;
            subButtonGap = 6;
            sectionGap = 10;
            submenuBlockHeight = subButtonHeight * 3 + subButtonGap * 2;
            startReserve = startExpand > 0.05f ? submenuBlockHeight + 10 : 0;
            editorReserve = editorExpand > 0.05f ? submenuBlockHeight + 10 : 0;
            panelHeight = Math.Min(maxPanelHeight, headerHeight + buttonHeight * 3 + sectionGap * 2 + startReserve + editorReserve + 30);
        }

        int x = Math.Max(horizontalMargin, ClientSize.Width - panelWidth - horizontalMargin);
        int yMax = Math.Max(verticalMargin, ClientSize.Height - verticalMargin - panelHeight);
        int y = Math.Clamp(ClientSize.Height / 2 - panelHeight / 2, verticalMargin, yMax);
        Rectangle panel = new(
            x,
            y,
            panelWidth,
            panelHeight);

        using var titleBrush = new SolidBrush(Color.FromArgb(244, 247, 250));
        using var textBrush = new SolidBrush(Color.FromArgb(210, 220, 232));
        using var hintBrush = new SolidBrush(Color.FromArgb(168, 184, 198, 214));
        using var eyebrowBrush = new SolidBrush(Color.FromArgb(196, 196, 210, 226));
        using var footerBrush = new SolidBrush(Color.FromArgb(180, 196, 208, 220));

        Rectangle shadowRect = Rectangle.Inflate(panel, 12, 14);
        shadowRect.Offset(0, 10);
        using (GraphicsPath shadowPath = CreateRoundedRectangle(shadowRect, 26))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(46, 0, 0, 0)))
        {
            graphics.FillPath(shadowBrush, shadowPath);
        }

        using (GraphicsPath panelPath = CreateRoundedRectangle(panel, 18))
        using (var panelFill = new LinearGradientBrush(
                   new Point(panel.Left, panel.Top),
                   new Point(panel.Right, panel.Bottom),
                   Color.FromArgb(206, 7, 11, 20),
                   Color.FromArgb(192, 16, 24, 39)))
        using (var panelBorder = new Pen(Color.FromArgb(156, 166, 214, 255), 1.15f))
        using (var accentBrush = new SolidBrush(Color.FromArgb((int)MathF.Round(108f + pulse * 46f), 90, 164, 255)))
        {
            graphics.FillPath(panelFill, panelPath);
            graphics.DrawPath(panelBorder, panelPath);
            graphics.FillRectangle(accentBrush, panel.X + 28, panel.Y + 20, 112, 4);
        }

        graphics.DrawString("战术模拟平台", _menuEyebrowFont, eyebrowBrush, panel.X + 28, panel.Y + 28);
        graphics.DrawString("ARTINX A-Soul", _menuTitleFont, titleBrush, panel.X + 28, panel.Y + 48);
        int cursorY = panel.Y + headerHeight;
        Rectangle startButton = new(panel.X + 28, cursorY, rowWidth, buttonHeight);
        DrawMainMenuActionButton(graphics, startButton, "开始游戏", "main_menu_toggle_start", primaryAccent, _mainMenuStartExpanded, 1f, emphasized: true);
        cursorY += buttonHeight + 12;
        if (startExpand > 0.05f)
        {
            int subOffsetY = (int)MathF.Round((1f - startExpand) * 12f);
            Rectangle fullMode = new(panel.X + 42, cursorY + subOffsetY, rowWidth - 14, subButtonHeight);
            Rectangle duelMode = new(panel.X + 42, cursorY + subButtonHeight + subButtonGap + subOffsetY, rowWidth - 14, subButtonHeight);
            Rectangle unitTestMode = new(panel.X + 42, cursorY + (subButtonHeight + subButtonGap) * 2 + subOffsetY, rowWidth - 14, subButtonHeight);
            DrawMainMenuActionButton(graphics, fullMode, "UC对战", "menu_open_lobby_full", secondaryAccent, false, startExpand);
            DrawMainMenuActionButton(graphics, duelMode, "1v1对战", "menu_open_lobby_duel", secondaryAccent, false, startExpand);
            DrawMainMenuActionButton(graphics, unitTestMode, "单位测试模式", "menu_open_lobby_unit_test", secondaryAccent, false, startExpand);
        }
        cursorY += startReserve;

        cursorY += sectionGap;
        Rectangle editorButton = new(panel.X + 28, cursorY, rowWidth, buttonHeight);
        DrawMainMenuActionButton(graphics, editorButton, "编辑器", "main_menu_toggle_editor", quietAccent, _mainMenuEditorExpanded, 1f);
        cursorY += buttonHeight + 12;
        if (editorExpand > 0.05f)
        {
            int subOffsetY = (int)MathF.Round((1f - editorExpand) * 12f);
            Rectangle terrainEditor = new(panel.X + 42, cursorY + subOffsetY, rowWidth - 14, subButtonHeight);
            Rectangle appearanceEditor = new(panel.X + 42, cursorY + subButtonHeight + subButtonGap + subOffsetY, rowWidth - 14, subButtonHeight);
            Rectangle ruleEditor = new(panel.X + 42, cursorY + (subButtonHeight + subButtonGap) * 2 + subOffsetY, rowWidth - 14, subButtonHeight);
            DrawMainMenuActionButton(graphics, terrainEditor, "地图编辑器", "menu_open_terrain_editor", quietAccent, false, editorExpand);
            DrawMainMenuActionButton(graphics, appearanceEditor, "外观编辑器", "menu_open_appearance_editor", quietAccent, false, editorExpand);
            DrawMainMenuActionButton(graphics, ruleEditor, "规则编辑器", "menu_open_rule_editor", quietAccent, false, editorExpand);
        }
        cursorY += editorReserve;

        cursorY += sectionGap;
        Rectangle exitButton = new(panel.X + 28, cursorY, rowWidth, buttonHeight);
        DrawMainMenuActionButton(graphics, exitButton, "退出", "menu_exit", quietAccent, false, 1f);
    }

    private void DrawMainMenuActionButton(
        Graphics graphics,
        Rectangle rect,
        string label,
        string action,
        Color accentColor,
        bool active,
        float reveal,
        bool emphasized = false)
    {
        reveal = Math.Clamp(reveal, 0f, 1f);
        if (reveal <= 0.01f)
        {
            return;
        }

        float hoverMix = ResolveUiHoverMix(action);
        Rectangle drawRect = hoverMix > 0.01f ? Rectangle.Inflate(rect, 1, 1) : rect;
        GraphicsPath path = CreateRoundedRectangle(drawRect, drawRect.Height >= 50 ? 14 : 12);
        try
        {
            float accentMix = Math.Clamp((active ? 0.52f : 0.18f) + hoverMix * 0.24f + (emphasized ? 0.10f : 0.0f), 0f, 0.92f);
            Color topColor = BlendUiColor(Color.FromArgb(224, 48, 60, 78), accentColor, accentMix);
            Color bottomColor = BlendUiColor(Color.FromArgb(214, 18, 26, 38), accentColor, accentMix + 0.12f);
            topColor = ApplyUiAlpha(topColor, reveal);
            bottomColor = ApplyUiAlpha(bottomColor, reveal);
            Color borderColor = ApplyUiAlpha(
                active
                    ? BlendUiColor(Color.FromArgb(220, 226, 236, 246), Color.White, hoverMix * 0.35f)
                    : BlendUiColor(Color.FromArgb(138, 156, 172, 188), Color.FromArgb(205, 226, 238, 248), hoverMix * 0.55f),
                0.72f + reveal * 0.28f);
            using var fill = new LinearGradientBrush(
                new Point(drawRect.Left, drawRect.Top),
                new Point(drawRect.Left, drawRect.Bottom),
                topColor,
                bottomColor);
            using var border = new Pen(borderColor, active ? 1.45f : 1.0f + hoverMix * 0.35f);
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);

            using var highlight = new SolidBrush(ApplyUiAlpha(Color.FromArgb(active ? 60 : 34, 255, 255, 255), reveal * (0.7f + hoverMix * 0.3f)));
            graphics.FillRectangle(highlight, drawRect.X + 1, drawRect.Y + 1, Math.Max(1, drawRect.Width - 2), Math.Max(2, drawRect.Height / 4));

            using var accentBrush = new SolidBrush(ApplyUiAlpha(accentColor, 0.40f + reveal * 0.35f));
            graphics.FillRectangle(accentBrush, drawRect.X + 14, drawRect.Y + 12, 3, Math.Max(10, drawRect.Height - 24));

            DrawUiButtonText(
                graphics,
                Rectangle.Inflate(drawRect, -18, -2),
                label,
                drawRect.Height >= 50 ? _menuButtonFont : _menuSubtitleFont,
                ApplyUiAlpha(Color.WhiteSmoke, 0.82f + reveal * 0.18f));
        }
        finally
        {
            path.Dispose();
        }

        if (reveal >= 0.35f && !string.IsNullOrWhiteSpace(action))
        {
            _uiButtons.Add(new UiButton(drawRect, action));
        }
    }

    private void DrawMainMenuMapChoices(Graphics graphics, int x, int y, int width, int bottom)
    {
        IReadOnlyList<string> presets = _host.AvailableMapPresets;
        if (presets.Count == 0)
        {
            using var warnBrush = new SolidBrush(Color.FromArgb(240, 238, 176, 96));
            graphics.DrawString("未找到可用地图预设。", _smallHudFont, warnBrush, x, y + 8);
            return;
        }

        int chipHeight = 32;
        int gap = 8;
        int cursorX = x;
        int cursorY = y;
        int bottomLimit = Math.Max(y + chipHeight, bottom);
        int hiddenCount = 0;

        foreach (string preset in presets)
        {
            string label = FormatMapPresetLabel(preset);
            int chipWidth = Math.Clamp((int)MathF.Ceiling(graphics.MeasureString(label, _smallHudFont).Width) + 34, 118, 210);
            if (cursorX > x && cursorX + chipWidth > x + width)
            {
                cursorX = x;
                cursorY += chipHeight + gap;
            }

            if (cursorY + chipHeight > bottomLimit)
            {
                hiddenCount++;
                continue;
            }

            bool selected = string.Equals(preset, _host.ActiveMapPreset, StringComparison.OrdinalIgnoreCase);
            Rectangle chip = new(cursorX, cursorY, chipWidth, chipHeight);
            DrawButton(
                graphics,
                chip,
                label,
                $"menu_map_select:{preset}",
                selected,
                selected ? Color.FromArgb(72, 146, 226) : Color.FromArgb(68, 84, 102));
            cursorX += chipWidth + gap;
        }

        if (hiddenCount > 0)
        {
            using var hintBrush = new SolidBrush(Color.FromArgb(198, 210, 220));
            graphics.DrawString($"还有 {hiddenCount} 张地图因窗口高度不足未显示。", _tinyHudFont, hintBrush, x, bottomLimit + 4);
        }
    }

    private static string FormatMapPresetLabel(string preset)
    {
        if (string.Equals(preset, "rmuc2026TerrainCache", StringComparison.OrdinalIgnoreCase))
        {
            return "RMUC2026 缓存";
        }

        if (string.Equals(preset, "rmuc2026", StringComparison.OrdinalIgnoreCase))
        {
            return "RMUC2026";
        }

        return preset;
    }

    private string ResolveLobbyMapLabel()
    {
        if (_host.IsSingleUnitTestMode)
        {
            return "单兵种固定场地";
        }

        if (_host.IsDuelMode)
        {
            return "1v1 独立平面场地";
        }

        if (_host.IsUnitTestMode)
        {
            return "单位测试独立场地";
        }

        return FormatMapPresetLabel(_host.ActiveMapPreset);
    }

    private string ResolveMatchModeLabel()
    {
        if (_host.IsUnitTestMode)
        {
            return "单位测试";
        }

        if (_host.IsDuelMode)
        {
            return "1v1 对抗";
        }

        if (_host.IsSingleUnitTestMode)
        {
            return "单兵种测试";
        }

        return "完整模式";
    }

    private void DrawLobby(Graphics graphics)
    {
        string title = "车辆选择";
        SizeF titleSize = graphics.MeasureString(title, _menuTitleFont);
        graphics.DrawString(title, _menuTitleFont, Brushes.White, (ClientSize.Width - titleSize.Width) * 0.5f, 56f);

        int panelHeight = Math.Min(650, Math.Max(590, ClientSize.Height - 140));
        int panelY = Math.Max(96, (ClientSize.Height - panelHeight) / 2);
        Rectangle panel = new((ClientSize.Width - 980) / 2, panelY, 980, panelHeight);
        DrawPanel(graphics, panel);

        using var metaBrush = new SolidBrush(Color.FromArgb(214, 222, 230));
        string lobbyTeamLabel = string.Equals(_host.SelectedTeam, "red", StringComparison.OrdinalIgnoreCase) ? "红方" : "蓝方";
        graphics.DrawString($"地图  {ResolveLobbyMapLabel()}", _menuSubtitleFont, metaBrush, panel.X + 30, panel.Y + 24);
        string teamLine = _host.IsDuelMode || _host.IsUnitTestMode
            ? $"阵营  {lobbyTeamLabel}  （该模式固定红方主控）"
            : $"阵营  {lobbyTeamLabel}";
        graphics.DrawString(teamLine, _tinyHudFont, metaBrush, panel.X + 30, panel.Y + 52);
        bool redTeamActive = string.Equals(_host.SelectedTeam, "red", StringComparison.OrdinalIgnoreCase);
        bool blueTeamActive = string.Equals(_host.SelectedTeam, "blue", StringComparison.OrdinalIgnoreCase);
        Rectangle lobbyRedTeam = new(panel.X + 308, panel.Y + 47, 68, 30);
        Rectangle lobbyBlueTeam = new(panel.X + 384, panel.Y + 47, 68, 30);
        DrawButton(graphics, lobbyRedTeam, "红方", "lobby_team:red", redTeamActive, Color.FromArgb(174, 66, 66));
        DrawButton(graphics, lobbyBlueTeam, "蓝方", "lobby_team:blue", blueTeamActive, Color.FromArgb(64, 112, 200));
        _lobbyAutoAimSliderRect = Rectangle.Empty;

        graphics.DrawString("单位类型", _menuSubtitleFont, Brushes.Gainsboro, panel.X + 30, panel.Y + 96);
        string selectedRole = ResolveLobbySelectedRoleKey();
        (string RoleKey, string Label, Color Color)[] roleButtons = _host.IsDuelMode
            ? new[]
            {
                ("hero", "英雄", Color.FromArgb(112, 126, 232)),
                ("infantry", "步兵", Color.FromArgb(196, 132, 82)),
                ("sentry", "哨兵", Color.FromArgb(132, 110, 198)),
            }
            : new[]
            {
                ("hero", "英雄", Color.FromArgb(112, 126, 232)),
                ("engineer", "工程", Color.FromArgb(92, 172, 126)),
                ("infantry", "步兵", Color.FromArgb(196, 132, 82)),
                ("sentry", "哨兵", Color.FromArgb(132, 110, 198)),
            };
        int roleButtonWidth = 200;
        int roleButtonHeight = 44;
        int roleGap = 12;
        for (int index = 0; index < roleButtons.Length; index++)
        {
            int row = index / 2;
            int col = index % 2;
            Rectangle button = new(
                panel.X + 30 + col * (roleButtonWidth + roleGap),
                panel.Y + 124 + row * (roleButtonHeight + 12),
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

        int configX = panel.X + 30;
        int configY = panel.Y + 250;
        graphics.DrawString("单位配置", _menuSubtitleFont, Brushes.Gainsboro, configX, configY);
        int nextConfigY = configY + 28;
        if (string.Equals(selectedRole, "hero", StringComparison.OrdinalIgnoreCase))
        {
            DrawLobbyOptionRow(
                graphics,
                configX,
                nextConfigY,
                "英雄风格",
                new[]
                {
                    ("远程", "lobby_hero_mode:ranged_priority", _host.HeroPerformanceMode == "ranged_priority"),
                    ("近战", "lobby_hero_mode:melee_priority", _host.HeroPerformanceMode == "melee_priority"),
                });
            nextConfigY += 42;
        }
        else if (string.Equals(selectedRole, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            DrawLobbyOptionRow(
                graphics,
                configX,
                nextConfigY,
                "模型",
                new[]
                {
                    ("完整", "lobby_infantry_mode:full", _host.InfantryMode == "full"),
                    ("平衡", "lobby_infantry_mode:balance", _host.InfantryMode == "balance"),
                });
            nextConfigY += 42;
            DrawLobbyOptionRow(
                graphics,
                configX,
                nextConfigY,
                "生存",
                new[]
                {
                    ("血量", "lobby_infantry_durability:hp_priority", _host.InfantryDurabilityMode == "hp_priority"),
                    ("功率", "lobby_infantry_durability:power_priority", _host.InfantryDurabilityMode == "power_priority"),
                });
            nextConfigY += 42;
            DrawLobbyOptionRow(
                graphics,
                configX,
                nextConfigY,
                "武器",
                new[]
                {
                    ("冷却", "lobby_infantry_weapon:cooling_priority", _host.InfantryWeaponMode == "cooling_priority"),
                    ("爆发", "lobby_infantry_weapon:burst_priority", _host.InfantryWeaponMode == "burst_priority"),
                });
            nextConfigY += 42;
        }
        else if (string.Equals(selectedRole, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            DrawLobbyOptionRow(
                graphics,
                configX,
                nextConfigY,
                "火控",
                new[]
                {
                    ("全自动", "lobby_sentry_control:full_auto", _host.SentryControlMode == "full_auto"),
                    ("半自动", "lobby_sentry_control:semi_auto", _host.SentryControlMode == "semi_auto"),
                });
            nextConfigY += 42;
            DrawLobbyOptionRow(
                graphics,
                configX,
                nextConfigY,
                "姿态",
                new[]
                {
                    ("进攻", "lobby_sentry_stance:attack", _host.SentryStance == "attack"),
                    ("机动", "lobby_sentry_stance:move", _host.SentryStance == "move"),
                    ("防守", "lobby_sentry_stance:defense", _host.SentryStance == "defense"),
                });
            nextConfigY += 42;
        }
        else
        {
            graphics.DrawString("工程暂无额外赛前配置项。", _menuSubtitleFont, Brushes.LightGray, configX, nextConfigY + 6);
            nextConfigY += 34;
        }

        if (_host.IsUnitTestMode)
        {
            DrawLobbyOptionRow(
                graphics,
                configX,
                nextConfigY,
                "能量机关",
                new[]
                {
                    ("小能量", "lobby_unit_test_energy:small", !_host.UnitTestEnergyForceLarge),
                    ("大能量", "lobby_unit_test_energy:large", _host.UnitTestEnergyForceLarge),
                });
            nextConfigY += 42;
        }
        else if (_host.IsDuelMode)
        {
            DrawLobbyDuelRoundInputRow(graphics, configX, nextConfigY);
            nextConfigY += 54;
        }

        DrawLobbySliderRow(
            graphics,
            configX,
            nextConfigY + 6,
            "自瞄命中率常数",
            _host.AutoAimAccuracyScale,
            "lobby_autoaim_accuracy");
        nextConfigY += 52;

        if (!_host.IsDuelMode)
        {
            DrawLobbyOptionRow(
                graphics,
                configX,
                nextConfigY,
                "智能体",
                new[]
                {
                    ("开启", "lobby_ai:on", _host.AiEnabled),
                    ("关闭", "lobby_ai:off", !_host.AiEnabled),
                });
            nextConfigY += 42;
        }

        DrawLobbyOptionRow(
            graphics,
            configX,
            nextConfigY,
            "弹丸渲染",
            new[]
            {
                ("实体", "lobby_projectile_render:solid", _host.SolidProjectileRendering),
                ("2D", "lobby_projectile_render:flat", !_host.SolidProjectileRendering),
            });
        nextConfigY += 42;

        DrawLobbyOptionRow(
            graphics,
            configX,
            nextConfigY,
            "物理后端",
            new[]
            {
                ("原生", "lobby_projectile_physics:native", _host.ProjectilePhysicsBackend == "native"),
                ("BEPU", "lobby_projectile_physics:bepu", _host.ProjectilePhysicsBackend == "bepu"),
            });
        nextConfigY += 42;

        int previewX = panel.X + 470;
        bool fixedScenarioMap = _host.IsSingleUnitTestMode || _host.IsDuelMode || _host.IsUnitTestMode;
        graphics.DrawString(fixedScenarioMap ? "场地" : "地图选择", _menuSubtitleFont, Brushes.Gainsboro, previewX, panel.Y + 24);
        Rectangle mapCurrent = new(previewX + 50, panel.Y + 50, 248, 32);
        if (fixedScenarioMap)
        {
            DrawPanel(graphics, mapCurrent, alpha: 126);
            DrawUiButtonText(graphics, mapCurrent, ResolveLobbyMapLabel(), _smallHudFont, Color.FromArgb(232, 240, 246));
        }
        else
        {
            Rectangle mapPrev = new(previewX, panel.Y + 50, 42, 32);
            Rectangle mapNext = new(previewX + 306, panel.Y + 50, 42, 32);
            DrawButton(graphics, mapPrev, "<", "lobby_map_prev", true, Color.FromArgb(72, 110, 164));
            DrawPanel(graphics, mapCurrent, alpha: 126);
            DrawUiButtonText(graphics, mapCurrent, FormatMapPresetLabel(_host.ActiveMapPreset), _smallHudFont, Color.FromArgb(232, 240, 246));

            DrawButton(graphics, mapNext, ">", "lobby_map_next", true, Color.FromArgb(72, 110, 164));
        }

        Rectangle preview = new(panel.X + 470, panel.Y + 92, 480, panel.Height - 188);
        SimulationEntity? selectedEntity = _host.SelectedEntity;
        DrawLobbyVehiclePreviewCard(graphics, preview, selectedEntity);

        Rectangle back = new(panel.X + 30, panel.Bottom - 64, 180, 38);
        Rectangle start = new(panel.Right - 230, panel.Bottom - 74, 200, 48);
        DrawButton(graphics, back, "返回主界面", "lobby_back_main", false);
        DrawButton(
            graphics,
            start,
            _lobbyWorldRebuildTask is null ? "开始对局" : "更新中...",
            "lobby_start_match",
            _lobbyWorldRebuildTask is null,
            Color.FromArgb(52, 132, 226));

        if (_lobbyWorldRebuildTask is not null)
        {
            using var statusBrush = new SolidBrush(Color.FromArgb(224, 182, 222, 255));
            graphics.DrawString(
                string.IsNullOrWhiteSpace(_lobbyWorldRebuildLabel) ? "配置更新中..." : _lobbyWorldRebuildLabel,
                _smallHudFont,
                statusBrush,
                panel.Right - 268,
                panel.Bottom - 112);
        }

        graphics.DrawString("键盘：Enter 开始，Esc 返回，T 切换阵营，I 切换步兵模型，拖动滑条调整自瞄常数。", _smallHudFont, Brushes.LightGray, panel.X + 30, panel.Bottom - 24);
    }

    private void DrawLobbySliderRow(Graphics graphics, int x, int y, string label, double value, string key)
    {
        using var labelBrush = new SolidBrush(Color.FromArgb(214, 222, 230));
        using var valueBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        graphics.DrawString(label, _tinyHudFont, labelBrush, x, y + 6);

        Rectangle track = new(x + 108, y + 4, 216, 16);
        Rectangle fill = new(track.X, track.Y, (int)Math.Round(track.Width * Math.Clamp(value, 0.05, 1.0)), track.Height);
        Rectangle knob = new(
            track.X + (int)Math.Round(track.Width * Math.Clamp(value, 0.05, 1.0)) - 6,
            track.Y - 4,
            12,
            track.Height + 8);
        using var backBrush = new SolidBrush(Color.FromArgb(132, 44, 52, 62));
        using var fillBrush = new SolidBrush(Color.FromArgb(220, 76, 146, 232));
        using var borderPen = new Pen(Color.FromArgb(130, 188, 198, 214), 1f);
        using var knobBrush = new SolidBrush(Color.FromArgb(244, 246, 250));
        graphics.FillRectangle(backBrush, track);
        graphics.FillRectangle(fillBrush, fill);
        graphics.DrawRectangle(borderPen, track);
        graphics.FillEllipse(knobBrush, knob);
        graphics.DrawEllipse(borderPen, knob);
        graphics.DrawString($"{value * 100.0:0}%", _tinyHudFont, valueBrush, track.Right + 12, y + 2);

        if (string.Equals(key, "lobby_autoaim_accuracy", StringComparison.OrdinalIgnoreCase))
        {
            _lobbyAutoAimSliderRect = new Rectangle(track.X - 4, track.Y - 6, track.Width + 8, track.Height + 12);
        }
    }

    private void DrawLobbyDuelRoundInputRow(Graphics graphics, int x, int y)
    {
        using var labelBrush = new SolidBrush(Color.FromArgb(214, 222, 230));
        using var valueBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        using var hintBrush = new SolidBrush(Color.FromArgb(170, 186, 198, 214));
        graphics.DrawString("对局次数", _tinyHudFont, labelBrush, x, y + 6);

        Rectangle inputRect = new(x + 108, y + 2, 88, 28);
        _lobbyDuelRoundInputRect = inputRect;
        _uiButtons.Add(new UiButton(Rectangle.Inflate(inputRect, 2, 2), "lobby_duel_rounds_focus"));

        float hoverMix = ResolveUiHoverMix("lobby_duel_rounds_focus");
        Color fillColor = _lobbyDuelRoundInputFocused
            ? Color.FromArgb(218, 52, 80, 122)
            : BlendUiColor(Color.FromArgb(148, 36, 44, 58), Color.FromArgb(176, 52, 72, 98), hoverMix * 0.55f);
        using var fillBrush = new SolidBrush(fillColor);
        using var borderPen = new Pen(_lobbyDuelRoundInputFocused
            ? Color.FromArgb(224, 126, 194, 255)
            : BlendUiColor(Color.FromArgb(136, 158, 178, 198), Color.FromArgb(212, 196, 224, 248), hoverMix * 0.45f), _lobbyDuelRoundInputFocused ? 1.45f : 1.0f);
        using GraphicsPath path = CreateRoundedRectangle(inputRect, 8);
        graphics.FillPath(fillBrush, path);
        graphics.DrawPath(borderPen, path);

        string text = string.IsNullOrWhiteSpace(_lobbyDuelRoundInputText) ? "输入" : _lobbyDuelRoundInputText;
        Color textColor = string.IsNullOrWhiteSpace(_lobbyDuelRoundInputText)
            ? Color.FromArgb(166, 196, 206, 218)
            : Color.FromArgb(242, 244, 248, 252);
        DrawUiButtonText(graphics, inputRect, text, _smallHudFont, textColor);

        graphics.DrawString("局", _smallHudFont, valueBrush, inputRect.Right + 10, y + 5);
        graphics.DrawString("直接输入 1-99", _tinyHudFont, hintBrush, inputRect.Right + 34, y + 8);
        if (_lobbyDuelRoundInputFocused)
        {
            graphics.DrawString("Enter 确认  Esc 还原", _tinyHudFont, hintBrush, x + 108, y + 32);
        }
    }

    private void UpdateLobbyAutoAimSlider(Point location)
    {
        if (_lobbyAutoAimSliderRect.Width <= 0)
        {
            return;
        }

        double t = Math.Clamp((location.X - _lobbyAutoAimSliderRect.Left) / (double)Math.Max(1, _lobbyAutoAimSliderRect.Width), 0.0, 1.0);
        double value = 0.05 + t * 0.95;
        _host.SetAutoAimAccuracyScale(value);
        Invalidate();
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
        int buttonX = x + 108;
        foreach ((string text, string action, bool selected) in options)
        {
            Rectangle rect = new(buttonX, y, 92, 28);
            DrawButton(graphics, rect, text, action, selected, Color.FromArgb(76, 116, 178));
            buttonX += 100;
        }
    }

    private void DrawLobbyVehiclePreviewCard(Graphics graphics, Rectangle rect, SimulationEntity? entity)
    {
        DrawCard(graphics, rect, entity is not null);
        Rectangle viewport = new(rect.X + 16, rect.Y + 18, rect.Width - 32, Math.Max(180, rect.Height - 138));
        using (var viewportBrush = new SolidBrush(Color.FromArgb(164, 24, 30, 40)))
        using (var viewportPen = new Pen(Color.FromArgb(116, 124, 140, 156), 1f))
        {
            graphics.FillRectangle(viewportBrush, viewport);
            graphics.DrawRectangle(viewportPen, viewport);
        }

        graphics.DrawString("载具预览", _menuSubtitleFont, Brushes.WhiteSmoke, rect.X + 18, rect.Y + 6);
        if (entity is null)
        {
            graphics.DrawString("当前未选择可控制单位。", _menuSubtitleFont, Brushes.LightGray, rect.X + 18, viewport.Bottom + 18);
            return;
        }

        RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
        GraphicsState state = graphics.Save();
        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        bool previousSuppressLabels = _suppressEntityLabels;
        double previousAngle = entity.AngleDeg;
        double previousTurretYaw = entity.TurretYawDeg;
        double previousPitch = entity.GimbalPitchDeg;

        try
        {
            graphics.SetClip(viewport);
            using var groundBrush = new SolidBrush(Color.FromArgb(74, 96, 102, 110));
            graphics.FillEllipse(
                groundBrush,
                viewport.X + viewport.Width * 0.22f,
                viewport.Bottom - 42,
                viewport.Width * 0.56f,
                20f);

            _projectionViewportRect = viewport;
            _suppressEntityLabels = true;
            entity.AngleDeg = 34.0;
            entity.TurretYawDeg = 16.0;
            entity.GimbalPitchDeg = -6.0;

            float previewExtent = Math.Max(
                0.45f,
                Math.Max(
                    profile.BodyLengthM + profile.BarrelLengthM * 0.8f,
                    Math.Max(profile.BodyWidthM, profile.GimbalHeightM + profile.BodyClearanceM)));
            _cameraTargetM = new Vector3(0f, Math.Max(0.22f, profile.BodyClearanceM + profile.BodyHeightM * 0.55f), 0f);
            float distance = Math.Clamp(previewExtent * 2.9f, 1.8f, 5.2f);
            _cameraPositionM = _cameraTargetM + new Vector3(distance * 0.86f, distance * 0.52f, distance * 1.08f);
            _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, Vector3.UnitY);
            float aspect = Math.Max(0.6f, viewport.Width / (float)Math.Max(1, viewport.Height));
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(0.86f, aspect, 0.02f, 40f);

            DrawEntityAppearanceModelModern(graphics, entity, Vector3.Zero, profile);
        }
        finally
        {
            entity.AngleDeg = previousAngle;
            entity.TurretYawDeg = previousTurretYaw;
            entity.GimbalPitchDeg = previousPitch;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _suppressEntityLabels = previousSuppressLabels;
            graphics.Restore(state);
        }

        string team = string.Equals(entity.Team, "red", StringComparison.OrdinalIgnoreCase) ? "\u7ea2\u65b9" : "\u84dd\u65b9";
        string role = ResolveRoleLabel(entity);
        string subtype = string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            ? (_host.InfantryMode == "balance" ? "\u5e73\u8861" : "\u5168\u5411")
            : "\u6807\u51c6";
        int ammo = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? entity.Ammo42Mm : entity.Ammo17Mm;
        string ammoLabel = EntityHasBarrel(entity) ? $"{entity.AmmoType} {ammo}" : "\u65e0\u5f39\u836f";

        int textY = viewport.Bottom + 16;
        graphics.DrawString($"{team}  |  {role}  |  {subtype}", _hudMidFont, Brushes.WhiteSmoke, rect.X + 18, textY);
        textY += 28;
        graphics.DrawString($"\u8840\u91cf {entity.Health:0}/{entity.MaxHealth:0}   \u529f\u7387 {(int)entity.Power}/{(int)entity.MaxPower}   {ammoLabel}", _smallHudFont, Brushes.Gainsboro, rect.X + 18, textY);
        textY += 22;
        graphics.DrawString($"\u7f16\u53f7 {entity.Id}", _smallHudFont, Brushes.LightGray, rect.X + 18, textY);
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

        string modeText = _host.IsFocusSandboxMode
            ? $"{ResolveMatchModeLabel()} | 主控 {_host.SingleUnitTestFocusId}"
            : ResolveMatchModeLabel();
        graphics.DrawString(modeText, _tinyHudFont, subtitleBrush, 16f, 32f);
        string perfText = $"\u6e32\u67d3 {_host.ActiveRendererMode} | \u76ee\u6807 {Math.Max(120, _displayRefreshRateHz)}Hz | \u5e27\u7387 {_smoothedFrameRate:0}";
        SizeF perfSize = graphics.MeasureString(perfText, _tinyHudFont);
        graphics.DrawString(perfText, _tinyHudFont, subtitleBrush, Math.Max(16f, ClientSize.Width - perfSize.Width - 430f), 32f);

    }

    private void DrawPauseOverlay(Graphics graphics)
    {
        if (_observerMode)
        {
            DrawObserverPauseControls(graphics);
            return;
        }

        using var dim = new SolidBrush(Color.FromArgb(172, 62, 68, 74));
        graphics.FillRectangle(dim, ClientRectangle);

        int panelWidth = Math.Min(560, Math.Max(320, ClientSize.Width - 80));
        int panelHeight = 188;
        Rectangle panel = new(
            (ClientSize.Width - panelWidth) / 2,
            (ClientSize.Height - panelHeight) / 2,
            panelWidth,
            panelHeight);
        using GraphicsPath path = CreateRoundedRectangle(panel, 14);
        using var fill = new SolidBrush(Color.FromArgb(230, 14, 20, 28));
        using var border = new Pen(Color.FromArgb(170, 190, 202, 214), 1.2f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var titleBrush = new SolidBrush(Color.FromArgb(245, 248, 250));
        using var textBrush = new SolidBrush(Color.FromArgb(204, 218, 228));
        string title = "已暂停";
        SizeF titleSize = graphics.MeasureString(title, _hudMidFont);
        graphics.DrawString(title, _hudMidFont, titleBrush, panel.X + (panel.Width - titleSize.Width) * 0.5f, panel.Y + 22);
        string hint = "鼠标已释放，点击继续或按 Esc 返回对局";
        SizeF hintSize = graphics.MeasureString(hint, _tinyHudFont);
        graphics.DrawString(hint, _tinyHudFont, textBrush, panel.X + (panel.Width - hintSize.Width) * 0.5f, panel.Y + 62);

        int buttonWidth = 112;
        int buttonHeight = 34;
        int gap = 10;
        int totalWidth = buttonWidth * 3 + gap * 2;
        int x = panel.X + (panel.Width - totalWidth) / 2;
        int y = panel.Bottom - 62;
        DrawButton(graphics, new Rectangle(x, y, buttonWidth, buttonHeight), "继续", "match_toggle_pause", true, Color.FromArgb(62, 130, 206));
        x += buttonWidth + gap;
        DrawButton(graphics, new Rectangle(x, y, buttonWidth, buttonHeight), "重新开始", "match_reset_world", false, Color.FromArgb(92, 98, 112));
        x += buttonWidth + gap;
        DrawButton(graphics, new Rectangle(x, y, buttonWidth, buttonHeight), "返回大厅", "match_return_lobby", false, Color.FromArgb(92, 98, 112));
    }

    private void DrawDuelFinishedOverlay(Graphics graphics)
    {
        Simulator3dHost.DuelMatchSnapshot snapshot = _host.GetDuelMatchSnapshot();
        using var dim = new SolidBrush(Color.FromArgb(176, 34, 38, 44));
        graphics.FillRectangle(dim, ClientRectangle);

        int visibleRoundRows = Math.Min(snapshot.RoundStats.Count, 8);
        int historyHeight = visibleRoundRows > 0 ? 42 + visibleRoundRows * 23 : 54;
        int panelWidth = Math.Min(860, Math.Max(520, ClientSize.Width - 64));
        int panelHeight = Math.Min(ClientSize.Height - 48, 356 + historyHeight);
        Rectangle panel = new(
            (ClientSize.Width - panelWidth) / 2,
            (ClientSize.Height - panelHeight) / 2,
            panelWidth,
            panelHeight);
        using GraphicsPath path = CreateRoundedRectangle(panel, 16);
        using var fill = new SolidBrush(Color.FromArgb(232, 12, 18, 26));
        using var border = new Pen(Color.FromArgb(186, 210, 220, 232), 1.2f);
        using var titleBrush = new SolidBrush(Color.FromArgb(244, 248, 252));
        using var textBrush = new SolidBrush(Color.FromArgb(214, 226, 236));
        using var accentBrush = new SolidBrush(Color.FromArgb(255, 246, 210, 84));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string title = "1v1 对局结束";
        SizeF titleSize = graphics.MeasureString(title, _hudMidFont);
        graphics.DrawString(title, _hudMidFont, titleBrush, panel.X + (panel.Width - titleSize.Width) * 0.5f, panel.Y + 22);

        string scoreLine = $"红方 {snapshot.RedScore} : {snapshot.BlueScore} 蓝方";
        SizeF scoreSize = graphics.MeasureString(scoreLine, _hudBigFont);
        graphics.DrawString(scoreLine, _hudBigFont, accentBrush, panel.X + (panel.Width - scoreSize.Width) * 0.5f, panel.Y + 64);

        string summary = $"已完成 {snapshot.RoundsCompleted}/{snapshot.RoundLimit} 局  ·  {snapshot.ResultLabel}";
        SizeF summarySize = graphics.MeasureString(summary, _smallHudFont);
        graphics.DrawString(summary, _smallHudFont, textBrush, panel.X + (panel.Width - summarySize.Width) * 0.5f, panel.Y + 114);

        Rectangle historyRect = new(panel.X + 28, panel.Y + 146, panel.Width - 56, historyHeight);
        DrawDuelRoundHistoryTable(graphics, historyRect, snapshot.RoundStats, visibleRoundRows);

        int columnGap = 18;
        int columnWidth = (panel.Width - 64 - columnGap) / 2;
        Rectangle friendlyRect = new(panel.X + 32, historyRect.Bottom + 14, columnWidth, 88);
        Rectangle enemyRect = new(friendlyRect.Right + columnGap, friendlyRect.Y, columnWidth, friendlyRect.Height);
        DrawDuelRoundStatColumn(graphics, friendlyRect, "我方总计", snapshot.FriendlyTotalStats, ResolveTeamColor("red"));
        DrawDuelRoundStatColumn(graphics, enemyRect, "敌方总计", snapshot.EnemyTotalStats, ResolveTeamColor("blue"));

        int buttonWidth = 124;
        int buttonHeight = 36;
        int gap = 14;
        int totalWidth = buttonWidth * 2 + gap;
        int x = panel.X + (panel.Width - totalWidth) / 2;
        int y = panel.Bottom - 60;
        DrawButton(graphics, new Rectangle(x, y, buttonWidth, buttonHeight), "重新开始", "match_reset_world", false, Color.FromArgb(62, 130, 206));
        DrawButton(graphics, new Rectangle(x + buttonWidth + gap, y, buttonWidth, buttonHeight), "返回大厅", "match_return_lobby", false, Color.FromArgb(92, 98, 112));
    }

    private void DrawDuelRoundHistoryTable(
        Graphics graphics,
        Rectangle rect,
        IReadOnlyList<Simulator3dHost.DuelRoundPairSnapshot> roundStats,
        int visibleRoundRows)
    {
        using GraphicsPath path = CreateRoundedRectangle(rect, 7);
        using var fill = new SolidBrush(Color.FromArgb(86, 245, 248, 252));
        using var border = new Pen(Color.FromArgb(118, 210, 220, 232), 1.0f);
        using var titleBrush = new SolidBrush(Color.FromArgb(236, 250, 252, 255));
        using var textBrush = new SolidBrush(Color.FromArgb(210, 230, 236, 244));
        using var mutedBrush = new SolidBrush(Color.FromArgb(160, 206, 216, 226));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        int totalRows = roundStats.Count;
        int rowsToShow = Math.Min(Math.Max(0, visibleRoundRows), totalRows);
        int startIndex = Math.Max(0, totalRows - rowsToShow);
        string title = startIndex > 0
            ? $"每局数据  最近 {rowsToShow}/{totalRows} 局"
            : "每局数据";
        graphics.DrawString(title, _smallHudFont, titleBrush, rect.X + 12, rect.Y + 8);
        graphics.DrawString("局", _tinyHudFont, mutedBrush, rect.X + 14, rect.Y + 31);
        graphics.DrawString("我方", _tinyHudFont, mutedBrush, rect.X + 58, rect.Y + 31);
        graphics.DrawString("敌方 AI", _tinyHudFont, mutedBrush, rect.X + rect.Width / 2 + 18, rect.Y + 31);

        if (rowsToShow <= 0)
        {
            graphics.DrawString("暂无回合数据", _tinyHudFont, mutedBrush, rect.X + 14, rect.Y + 54);
            return;
        }

        for (int rowIndex = 0; rowIndex < rowsToShow; rowIndex++)
        {
            Simulator3dHost.DuelRoundPairSnapshot row = roundStats[startIndex + rowIndex];
            float y = rect.Y + 53 + rowIndex * 23;
            graphics.DrawString(row.RoundIndex.ToString(), _tinyHudFont, textBrush, rect.X + 14, y);
            graphics.DrawString(FormatDuelCompactStats(row.FriendlyStats), _tinyHudFont, textBrush, rect.X + 58, y);
            graphics.DrawString(FormatDuelCompactStats(row.EnemyStats), _tinyHudFont, textBrush, rect.X + rect.Width / 2 + 18, y);
        }
    }

    private static string FormatDuelCompactStats(Simulator3dHost.DuelRoundStatSnapshot stats)
        => $"输出 {stats.DamageOutput:0}  命中 {stats.HitRate * 100.0:0}% ({stats.Hits}/{stats.Shots})  血 {Math.Max(0.0, stats.Health):0}/{Math.Max(1.0, stats.MaxHealth):0}";

    private void DrawDuelRoundRestartHint(Graphics graphics)
    {
        if (!_host.IsDuelMode)
        {
            return;
        }

        Simulator3dHost.DuelMatchSnapshot snapshot = _host.GetDuelMatchSnapshot();
        if (!snapshot.WaitingForNextRound || snapshot.Finished)
        {
            return;
        }

        if (snapshot.FriendlyDestroyedLastRound)
        {
            using var grayWash = new SolidBrush(Color.FromArgb(150, 214, 218, 222));
            graphics.FillRectangle(grayWash, ClientRectangle);
        }

        int panelWidth = Math.Min(680, Math.Max(360, ClientSize.Width - 48));
        int panelHeight = 188;
        Rectangle panel = new(
            (ClientSize.Width - panelWidth) / 2,
            (ClientSize.Height - panelHeight) / 2,
            panelWidth,
            panelHeight);

        using GraphicsPath path = CreateRoundedRectangle(panel, 8);
        using var fill = new SolidBrush(Color.FromArgb(102, 12, 18, 26));
        using var border = new Pen(Color.FromArgb(118, 220, 230, 238), 1.0f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using StringFormat format = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        using var titleBrush = new SolidBrush(Color.FromArgb(226, 250, 252, 255));
        using var hintBrush = new SolidBrush(Color.FromArgb(190, 224, 232, 240));
        string title = snapshot.FriendlyDestroyedLastRound
            ? "已被击杀"
            : "回合结束";
        string hint = $"下一局准备中  {snapshot.RoundRestartRemainingSec:0.0}s  ·  Enter 立即开始";
        graphics.DrawString(title, _hudMidFont, titleBrush, new RectangleF(panel.X, panel.Y + 12, panel.Width, 28), format);
        graphics.DrawString(hint, _smallHudFont, hintBrush, new RectangleF(panel.X, panel.Y + 40, panel.Width, 22), format);

        int columnGap = 18;
        int columnWidth = (panel.Width - 64 - columnGap) / 2;
        Rectangle friendlyRect = new(panel.X + 32, panel.Y + 76, columnWidth, 88);
        Rectangle enemyRect = new(friendlyRect.Right + columnGap, friendlyRect.Y, columnWidth, friendlyRect.Height);
        DrawDuelRoundStatColumn(graphics, friendlyRect, "我方", snapshot.FriendlyStats, ResolveTeamColor("red"));
        DrawDuelRoundStatColumn(graphics, enemyRect, "敌方 AI", snapshot.EnemyStats, ResolveTeamColor("blue"));
    }

    private void DrawDuelRoundStatColumn(
        Graphics graphics,
        Rectangle rect,
        string title,
        Simulator3dHost.DuelRoundStatSnapshot stats,
        Color accent)
    {
        using GraphicsPath path = CreateRoundedRectangle(rect, 7);
        using var fill = new SolidBrush(Color.FromArgb(92, 245, 248, 252));
        using var border = new Pen(Color.FromArgb(136, accent), 1.0f);
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 250, 252, 255));
        using var textBrush = new SolidBrush(Color.FromArgb(216, 232, 238, 246));
        using var mutedBrush = new SolidBrush(Color.FromArgb(170, 208, 218, 228));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string state = stats.Destroyed ? "击毁" : "存活";
        graphics.DrawString($"{title}  {state}", _smallHudFont, titleBrush, rect.X + 12, rect.Y + 9);
        graphics.DrawString($"输出  {stats.DamageOutput:0}", _tinyHudFont, textBrush, rect.X + 12, rect.Y + 34);
        graphics.DrawString($"命中率  {stats.HitRate * 100.0:0}%  ({stats.Hits}/{stats.Shots})", _tinyHudFont, textBrush, rect.X + 12, rect.Y + 53);
        graphics.DrawString($"血量  {Math.Max(0.0, stats.Health):0}/{Math.Max(1.0, stats.MaxHealth):0}", _tinyHudFont, mutedBrush, rect.X + 12, rect.Y + 72);
    }

    private void DrawMatchStartupOverlay(Graphics graphics)
    {
        using var dim = new SolidBrush(Color.FromArgb(168, 88, 92, 96));
        graphics.FillRectangle(dim, ClientRectangle);

        int panelWidth = Math.Min(560, Math.Max(320, ClientSize.Width - 96));
        int panelHeight = _matchStartupPhase == MatchStartupPhase.Countdown ? 220 : 184;
        Rectangle panel = new(
            (ClientSize.Width - panelWidth) / 2,
            (ClientSize.Height - panelHeight) / 2,
            panelWidth,
            panelHeight);
        using GraphicsPath path = CreateRoundedRectangle(panel, 18);
        using var fill = new SolidBrush(Color.FromArgb(232, 10, 16, 25));
        using var border = new Pen(Color.FromArgb(190, 96, 188, 255), 1.35f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var titleBrush = new SolidBrush(Color.FromArgb(248, 248, 252));
        using var textBrush = new SolidBrush(Color.FromArgb(218, 224, 236));
        using var mutedBrush = new SolidBrush(Color.FromArgb(168, 184, 200));
        using var accentBrush = new SolidBrush(Color.FromArgb(126, 214, 255));
        using StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        if (_matchStartupPhase == MatchStartupPhase.Countdown)
        {
            double elapsedSec = (_frameClock.ElapsedTicks - _matchStartupPhaseStartTicks) / (double)Stopwatch.Frequency;
            int countdown = Math.Clamp((int)Math.Ceiling(MatchStartupCountdownSec - elapsedSec), 1, 3);
            graphics.DrawString("MATCH STARTING", _smallHudFont, mutedBrush, new RectangleF(panel.X, panel.Y + 22, panel.Width, 24), centered);
            graphics.DrawString(countdown.ToString(), _menuTitleFont, accentBrush, new RectangleF(panel.X, panel.Y + 54, panel.Width, 86), centered);
            graphics.DrawString("Control will unlock after the countdown.", _smallHudFont, textBrush, new RectangleF(panel.X + 24, panel.Y + 142, panel.Width - 48, 28), centered);
            graphics.DrawString("World time is still held at 00:00.", _tinyHudFont, mutedBrush, new RectangleF(panel.X + 24, panel.Y + 168, panel.Width - 48, 22), centered);
            return;
        }

        double progress = ResolveMatchStartupLoadProgress();
        graphics.DrawString("LOADING MATCH", _smallHudFont, mutedBrush, new RectangleF(panel.X, panel.Y + 22, panel.Width, 24), centered);
        graphics.DrawString("Preparing terrain and player camera", _hudMidFont, titleBrush, new RectangleF(panel.X + 22, panel.Y + 50, panel.Width - 44, 30), centered);

        Rectangle bar = new(panel.X + 58, panel.Y + 98, panel.Width - 116, 14);
        using GraphicsPath barPath = CreateRoundedRectangle(bar, 7);
        using var barBack = new SolidBrush(Color.FromArgb(112, 42, 52, 66));
        graphics.FillPath(barBack, barPath);
        int fillWidth = Math.Clamp((int)Math.Round(bar.Width * progress), 0, bar.Width);
        if (fillWidth > 0)
        {
            Rectangle fillRect = new(bar.X, bar.Y, fillWidth, bar.Height);
            using GraphicsPath fillPath = CreateRoundedRectangle(fillRect, 7);
            graphics.FillPath(accentBrush, fillPath);
        }

        string detail = _matchStartupViewReady
            ? "Robot view ready. Waiting for full terrain residency..."
            : "Loading controlled robot camera...";
        if (progress >= 0.999 && _matchStartupViewReady)
        {
            detail = "Ready. Countdown will begin now.";
        }

        graphics.DrawString($"{progress * 100.0:0}%  {detail}", _tinyHudFont, textBrush, new RectangleF(panel.X + 24, panel.Y + 124, panel.Width - 48, 24), centered);
        graphics.DrawString("Press Esc to return to lobby.", _tinyHudFont, mutedBrush, new RectangleF(panel.X + 24, panel.Y + 148, panel.Width - 48, 22), centered);
    }

    private double ResolveMatchStartupLoadProgress()
    {
        double terrainProgress = ResolveActiveMapTerrainLoadProgress();
        double worldProgress = _matchStartupPrepareTask is null
            ? 1.0
            : (_matchStartupPrepareTask.IsCompleted ? 1.0 : 0.12);
        return Math.Clamp(worldProgress * 0.58 + terrainProgress * 0.42, 0.0, 1.0);
    }

    private void DrawObserverPauseControls(Graphics graphics)
    {
        int buttonWidth = 112;
        int buttonHeight = 32;
        int gap = 10;
        int x = 16;
        int y = ClientSize.Height - buttonHeight - 18;
        DrawButton(graphics, new Rectangle(x, y, buttonWidth, buttonHeight), "继续", "match_toggle_pause", true, Color.FromArgb(62, 130, 206));
        x += buttonWidth + gap;
        DrawButton(graphics, new Rectangle(x, y, buttonWidth, buttonHeight), "重新开始", "match_reset_world", false, Color.FromArgb(92, 98, 112));
        x += buttonWidth + gap;
        DrawButton(graphics, new Rectangle(x, y, buttonWidth + 22, buttonHeight), "返回大厅", "match_return_lobby", false, Color.FromArgb(92, 98, 112));

        Rectangle info = new(16, y - 34, 452, 26);
        using GraphicsPath path = CreateRoundedRectangle(info, 7);
        using var fill = new SolidBrush(Color.FromArgb(156, 10, 14, 20));
        using var border = new Pen(Color.FromArgb(182, 112, 210, 255), 1f);
        using var text = new SolidBrush(Color.FromArgb(230, 220, 232, 244));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString("观察者暂停中：F上浮  C下降  鼠标转向  Ctrl+C固定机位新窗口", _tinyHudFont, text, info.X + 10, info.Y + 6);
    }

    private void DrawObserverOverlay(Graphics graphics)
    {
        if (!_observerMode && !_sharedHostSimulation)
        {
            return;
        }

        string title = _observerPinned || _sharedHostSimulation
            ? "Observer Spectator"
            : "Observer Mode";
        string detail = _observerPinned || _sharedHostSimulation
            ? "Pinned camera mirror window"
            : $"WASD move  F rise  C descend  mouse look  wheel sensitivity {Math.Round(_observerMoveSpeedMps, 1)}m/s  Ctrl+C pin window";
        Rectangle panel = new(16, 16, Math.Min(620, ClientSize.Width - 32), 48);
        using GraphicsPath path = CreateRoundedRectangle(panel, 8);
        using var fill = new SolidBrush(Color.FromArgb(184, 8, 12, 18));
        using var border = new Pen(Color.FromArgb(196, 122, 210, 255), 1.1f);
        using var titleBrush = new SolidBrush(Color.FromArgb(242, 236, 246, 255));
        using var textBrush = new SolidBrush(Color.FromArgb(220, 194, 214, 232));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString(title, _smallHudFont, titleBrush, panel.X + 12, panel.Y + 8);
        graphics.DrawString(detail, _tinyHudFont, textBrush, panel.X + 12, panel.Y + 26);
    }

    private void DrawTacticalOverlay(Graphics graphics)
    {
        if (UseFastFlatRenderer)
        {
            DrawFastTacticalRoutes(graphics);
        }
        else
        {
            DrawTacticalRoutes(graphics);
        }

        if (!_tacticalMode)
        {
            return;
        }

        Rectangle panel = new(18, ToolbarHeight + HudHeight + 14, 380, 250);
        using GraphicsPath path = CreateRoundedRectangle(panel, 10);
        using var fill = new SolidBrush(Color.FromArgb(218, 10, 18, 26));
        using var border = new Pen(Color.FromArgb(142, 126, 168, 156), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var titleBrush = new SolidBrush(Color.FromArgb(238, 246, 242));
        using var textBrush = new SolidBrush(Color.FromArgb(204, 218, 224));
        graphics.DrawString("\u6307\u6325\u6a21\u5f0f", _hudMidFont, titleBrush, panel.X + 14, panel.Y + 10);
        graphics.DrawString("\u70b9\u51fb\u5df1\u65b9\u5355\u4f4d\u9009\u4e2d\uff0c\u518d\u70b9\u51fb\u654c\u65b9\u6216\u5730\u9762\u4e0b\u8fbe\u6307\u4ee4\u3002", _tinyHudFont, textBrush, panel.X + 14, panel.Y + 36);

        int y = panel.Y + 58;
        DrawButton(graphics, new Rectangle(panel.X + 14, y, 88, 28), "\u8fdb\u653b", "tactical_mode:attack", _tacticalCommandMode == TacticalCommandMode.Attack, Color.FromArgb(190, 82, 76));
        DrawButton(graphics, new Rectangle(panel.X + 112, y, 88, 28), "\u9632\u5b88", "tactical_mode:defend", _tacticalCommandMode == TacticalCommandMode.Defend, Color.FromArgb(64, 132, 210));
        DrawButton(graphics, new Rectangle(panel.X + 210, y, 88, 28), "\u5de1\u903b", "tactical_mode:patrol", _tacticalCommandMode == TacticalCommandMode.Patrol, Color.FromArgb(74, 154, 112));

        y += 38;
        double[] scales = { 0.3, 0.6, 1.0, 1.5 };
        for (int index = 0; index < scales.Length; index++)
        {
            double scale = scales[index];
            DrawButton(
                graphics,
                new Rectangle(panel.X + 14 + index * 74, y, 64, 26),
                $"{scale:0.0}x",
                $"tactical_timescale:{scale:0.0}",
                Math.Abs(_simulationTimeScale - scale) < 0.01,
                Color.FromArgb(116, 118, 196));
        }

        y += 38;
        string targetText = _tacticalCommandMode == TacticalCommandMode.Attack
            ? $"\u76ee\u6807\uff1a{(_tacticalAttackTargetId ?? ResolveDefaultTacticalTargetId() ?? "\u65e0")}"
            : $"\u5730\u70b9\uff1a{_tacticalGroundTargetX:0},{_tacticalGroundTargetY:0}";
        graphics.DrawString(targetText, _tinyHudFont, textBrush, panel.X + 14, y);
        graphics.DrawString($"\u961f\u4f0d {ResolveTeamName(_host.SelectedTeam)} | H \u9000\u51fa | T \u81ea\u7784 {ResolveAutoAimAssistLabel(_autoAimAssistMode)}", _tinyHudFont, textBrush, panel.X + 14, y + 18);
        y += 40;
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is not null)
        {
            int ammo = string.Equals(selected.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
                ? selected.Ammo42Mm
                : selected.Ammo17Mm;
            graphics.DrawString($"\u5df2\u9009\uff1a{selected.Id} / {ResolveRoleLabel(selected)}", _tinyHudFont, titleBrush, panel.X + 14, y);
            graphics.DrawString(
                $"\u8840\u91cf {selected.Health:0}/{selected.MaxHealth:0}   \u5f39\u836f {ammo}   \u70ed\u91cf {selected.Heat:0}/{Math.Max(1.0, selected.MaxHeat):0}   \u529f\u7387 {selected.ChassisPowerDrawW:0}/{Math.Max(1.0, selected.EffectiveDrivePowerLimitW):0}W",
                _tinyHudFont,
                textBrush,
                panel.X + 14,
                y + 18);
        }

        DrawButton(graphics, new Rectangle(panel.Right - 96, panel.Bottom - 36, 78, 26), "\u5e94\u7528", "tactical_apply", false, Color.FromArgb(86, 126, 156));
    }

    private void DrawTacticalRoutes(Graphics graphics)
    {
        IReadOnlyList<SimulationEntity> units = _host.GetControlCandidates(_host.SelectedTeam);
        if (units.Count == 0)
        {
            return;
        }

        using var attackPen = new Pen(Color.FromArgb(174, 255, 96, 76), 1.6f);
        using var defendPen = new Pen(Color.FromArgb(174, 92, 172, 255), 1.4f);
        using var patrolPen = new Pen(Color.FromArgb(168, 76, 220, 144), 1.3f);
        foreach (SimulationEntity unit in units)
        {
            if (string.IsNullOrWhiteSpace(unit.TacticalCommand))
            {
                continue;
            }

            Vector3 from = ToScenePoint(unit.X, unit.Y, (float)(unit.GroundHeightM + unit.AirborneHeightM + 0.06));
            if (string.Equals(unit.TacticalCommand, "attack", StringComparison.OrdinalIgnoreCase))
            {
                SimulationEntity? target = _host.World.Entities.FirstOrDefault(entity =>
                    string.Equals(entity.Id, unit.TacticalTargetId, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    continue;
                }

                Vector3 to = ToScenePoint(target.X, target.Y, (float)(target.GroundHeightM + target.AirborneHeightM + 0.12));
                DrawProjectedLine(graphics, from, to, attackPen);
            }
            else
            {
                Vector3 to = ToScenePoint(unit.TacticalTargetX, unit.TacticalTargetY, 0.08f);
                DrawProjectedLine(graphics, from, to, string.Equals(unit.TacticalCommand, "patrol", StringComparison.OrdinalIgnoreCase) ? patrolPen : defendPen);
                if (string.Equals(unit.TacticalCommand, "patrol", StringComparison.OrdinalIgnoreCase))
                {
                    DrawTacticalPatrolCircle(graphics, unit.TacticalTargetX, unit.TacticalTargetY, Math.Max(4.0, unit.TacticalPatrolRadiusWorld), patrolPen);
                }
            }
        }
    }

    private void DrawTacticalPatrolCircle(Graphics graphics, double worldX, double worldY, double radiusWorld, Pen pen)
    {
        PointF? previous = null;
        const int Segments = 32;
        for (int index = 0; index <= Segments; index++)
        {
            double angle = index * Math.PI * 2.0 / Segments;
            Vector3 point = ToScenePoint(worldX + Math.Cos(angle) * radiusWorld, worldY + Math.Sin(angle) * radiusWorld, 0.08f);
            if (TryProject(point, out PointF screen, out _))
            {
                if (previous is PointF prev)
                {
                    graphics.DrawLine(pen, prev, screen);
                }

                previous = screen;
            }
            else
            {
                previous = null;
            }
        }
    }

    private void DrawProjectedLine(Graphics graphics, Vector3 from, Vector3 to, Pen pen)
    {
        if (TryProject(from, out PointF a, out _) && TryProject(to, out PointF b, out _))
        {
            graphics.DrawLine(pen, a, b);
        }
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
        if (_host.IsDuelMode && string.Equals(normalized, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "infantry";
        }

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

    private void DrawHudLegacy(Graphics graphics)
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
            ? "已暂停" : (_host.World.GameTimeSec <= 0.02 ? "未开始" : "Round 1/5");
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
        DrawTeamHudSection(graphics, "red", "绾㈡柟", redRect);
        DrawTeamHudSection(graphics, "blue", "钃濇柟", blueRect);
    }

    private void DrawHud(Graphics graphics)
    {
        if (_host.IsUnitTestMode)
        {
            return;
        }

        if (_host.IsDuelMode)
        {
            DrawDuelHud(graphics);
            return;
        }

        int centerX = ClientSize.Width / 2;
        Rectangle centerPanel = new(centerX - 105, 2, 210, 56);

        double remaining = Math.Max(0.0, _host.GameDurationSec - _host.World.GameTimeSec);
        int remainingSeconds = (int)remaining;
        int minutes = remainingSeconds / 60;
        int seconds = remainingSeconds % 60;
        StringFormat centerFormat = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        using (var titleBrush = new SolidBrush(Color.FromArgb(224, 226, 234, 242)))
        {
            graphics.DrawString(_paused ? "\u5df2\u6682\u505c" : "\u5bf9\u5c40\u65f6\u95f4", _tinyHudFont, titleBrush, new RectangleF(centerPanel.X, centerPanel.Y + 4, centerPanel.Width, 18), centerFormat);
        }

        using (var timerBrush = new SolidBrush(Color.White))
        {
            graphics.DrawString($"{minutes}:{seconds:00}", _hudBigFont, timerBrush, new RectangleF(centerPanel.X, centerPanel.Y + 21, centerPanel.Width, 34), centerFormat);
        }

        DrawTeamGoldBadge(graphics, "red", new Rectangle(centerPanel.Left - 126, 24, 116, 24));
        DrawTeamGoldBadge(graphics, "blue", new Rectangle(centerPanel.Right + 10, 24, 116, 24));

        int sideGap = 138;
        int sideMargin = 12;
        int sideWidth = Math.Max(
            220,
            Math.Min(
                centerPanel.Left - sideGap - sideMargin,
                ClientSize.Width - centerPanel.Right - sideGap - sideMargin));
        Rectangle redRect = new(centerPanel.Left - sideGap - sideWidth, 8, sideWidth, HudHeight - 16);
        Rectangle blueRect = new(centerPanel.Right + sideGap, 8, sideWidth, HudHeight - 16);
        DrawTeamHudSection(graphics, "red", "\u7ea2\u65b9", redRect);
        DrawTeamHudSection(graphics, "blue", "\u84dd\u65b9", blueRect);
    }

    private void DrawDuelHud(Graphics graphics)
    {
        Simulator3dHost.DuelMatchSnapshot snapshot = _host.GetDuelMatchSnapshot();
        int centerX = ClientSize.Width / 2;
        Rectangle centerPanel = new(centerX - 132, 4, 264, 68);

        double remaining = Math.Max(0.0, _host.GameDurationSec - _host.World.GameTimeSec);
        int remainingSeconds = (int)remaining;
        int minutes = remainingSeconds / 60;
        int seconds = remainingSeconds % 60;
        using StringFormat centerFormat = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        using (var shadowBrush = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
        using (var titleBrush = new SolidBrush(Color.FromArgb(224, 226, 234, 242)))
        {
            string title = snapshot.Finished
                ? "1v1 结算"
                : (snapshot.WaitingForNextRound ? "回合结算中" : (_paused ? "已暂停" : "1v1 对抗"));
            graphics.DrawString(title, _tinyHudFont, shadowBrush, new RectangleF(centerPanel.X + 1, centerPanel.Y + 3, centerPanel.Width, 16), centerFormat);
            graphics.DrawString(title, _tinyHudFont, titleBrush, new RectangleF(centerPanel.X, centerPanel.Y + 4, centerPanel.Width, 16), centerFormat);
        }

        using (var shadowBrush = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
        using (var timerBrush = new SolidBrush(Color.White))
        {
            graphics.DrawString($"{minutes}:{seconds:00}", _hudBigFont, shadowBrush, new RectangleF(centerPanel.X + 1, centerPanel.Y + 16, centerPanel.Width, 28), centerFormat);
            graphics.DrawString($"{minutes}:{seconds:00}", _hudBigFont, timerBrush, new RectangleF(centerPanel.X, centerPanel.Y + 17, centerPanel.Width, 28), centerFormat);
        }

        using (var scoreBrush = new SolidBrush(Color.FromArgb(236, 246, 210, 84)))
        using (var metaBrush = new SolidBrush(Color.FromArgb(208, 220, 230, 238)))
        {
            graphics.DrawString($"比分 {snapshot.RedScore}:{snapshot.BlueScore}", _smallHudFont, scoreBrush, new RectangleF(centerPanel.X, centerPanel.Y + 44, centerPanel.Width, 18), centerFormat);
            string roundLine = snapshot.Finished
                ? snapshot.ResultLabel
                : (snapshot.WaitingForNextRound
                    ? $"{snapshot.RoundRestartRemainingSec:0.0}s 后开始下一局"
                    : $"第 {Math.Min(snapshot.RoundLimit, snapshot.RoundsCompleted + 1)}/{snapshot.RoundLimit} 局");
            graphics.DrawString(roundLine, _tinyHudFont, metaBrush, new RectangleF(centerPanel.X, centerPanel.Y + 58, centerPanel.Width, 14), centerFormat);
        }

        SimulationEntity? player = _host.SelectedEntity;
        SimulationEntity? enemy = FindEntityById("blue_robot_7");
        Rectangle leftBar = new(20, 20, Math.Max(220, centerPanel.Left - 44), 26);
        Rectangle rightBar = new(centerPanel.Right + 24, 20, Math.Max(220, ClientSize.Width - centerPanel.Right - 44), 26);
        DrawDuelHudBar(graphics, leftBar, player, ResolveTeamColor("red"), $"我方  {snapshot.RedScore}");
        DrawDuelHudBar(graphics, rightBar, enemy, ResolveTeamColor("blue"), $"敌方 AI  {snapshot.BlueScore}");
    }

    private void DrawDuelHudBar(Graphics graphics, Rectangle rect, SimulationEntity? entity, Color color, string sideLabel)
    {
        float ratio = ResolveHealthRatio(entity);
        string roleTitle = entity is null ? "--" : ResolveHudRoleTitle(entity);
        string label = entity is null
            ? $"{sideLabel}  {roleTitle}"
            : $"{sideLabel}  {roleTitle}  {(int)Math.Max(0.0, entity.Health)}/{(int)Math.Max(1.0, entity.MaxHealth)}";
        DrawTopHudBar(graphics, rect, ratio, color, label, entity is null || !entity.IsAlive);
    }

    private void DrawFpsBadge(Graphics graphics)
    {
        Rectangle badge = new(Math.Max(8, ClientSize.Width - 88), Math.Max(HudHeight + 2, ClientSize.Height - 30), 82, 22);
        using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var text = new SolidBrush(Color.FromArgb(236, 236, 246, 252));
        graphics.DrawString($"\u5e27\u7387 {_smoothedFrameRate:0}", _tinyHudFont, shadow, badge.X + 9, badge.Y + 5);
        graphics.DrawString($"\u5e27\u7387 {_smoothedFrameRate:0}", _tinyHudFont, text, badge.X + 8, badge.Y + 4);
    }

    private void DrawTeamGoldBadge(Graphics graphics, string teamKey, Rectangle rect)
    {
        double gold = _host.World.Teams.TryGetValue(teamKey, out SimulationTeamState? teamState) ? teamState.Gold : 0.0;
        using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var text = new SolidBrush(Color.FromArgb(246, 255, 224, 96));
        StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString($"\u91d1\u5e01 {(int)gold}", _smallHudFont, shadow, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width, rect.Height), center);
        graphics.DrawString($"\u91d1\u5e01 {(int)gold}", _smallHudFont, text, rect, center);
    }

    private void DrawCrosshair(Graphics graphics)
    {
        float x = ClientSize.Width * 0.5f;
        float y = ClientSize.Height * 0.5f;

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
        DrawHeroDeploymentChargeRing(graphics, x, y);
        DrawCrosshairStatusProgressRings(graphics, x, y);
        DrawTrackedArmorPlateHighlight(graphics);
        DrawAutoAimGuidanceMarker(graphics);
    }

    private void DrawDeadSelectedEntityScreenTint(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || entity.IsAlive)
        {
            return;
        }

        using var tint = new SolidBrush(Color.FromArgb(142, 188, 190, 194));
        using var shade = new SolidBrush(Color.FromArgb(96, 0, 0, 0));
        graphics.FillRectangle(tint, ClientRectangle);
        graphics.FillRectangle(shade, ClientRectangle);
    }

    private void DrawCrosshairStatusProgressRings(Graphics graphics, float centerX, float centerY)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null)
        {
            return;
        }

        string label;
        Color color;
        double remainingSec;
        double totalSec;
        if (!entity.IsAlive)
        {
            remainingSec = Math.Max(0.0, entity.RespawnTimerSec);
            totalSec = 15.0;
            label = "\u8bfb\u6761\u590d\u6d3b";
            color = Color.FromArgb(92, 224, 144);
        }
        else if (entity.PowerCutTimerSec > 1e-6)
        {
            remainingSec = Math.Max(0.0, entity.PowerCutTimerSec);
            totalSec = 5.0;
            label = "\u5e95\u76d8\u65ad\u7535";
            color = Color.FromArgb(255, 88, 88);
        }
        else if (entity.HeatLockTimerSec > 1e-6 || string.Equals(entity.State, "heat_locked", StringComparison.OrdinalIgnoreCase))
        {
            ResolvedRoleProfile profile = _host.ResolveRuntimeProfile(entity);
            double coolingRate = Math.Max(0.1, profile.HeatDissipationRate * Math.Max(0.1, entity.DynamicCoolingMult));
            remainingSec = Math.Max(entity.HeatLockTimerSec, entity.Heat / coolingRate);
            totalSec = Math.Max(0.5, ResolveHeatLockInitialHeatForProgress(entity) / coolingRate);
            label = "\u70ed\u91cf\u8d85\u9650";
            color = Color.FromArgb(255, 88, 88);
        }
        else
        {
            return;
        }

        float progress = (float)Math.Clamp(1.0 - remainingSec / Math.Max(0.1, totalSec), 0.0, 1.0);
        RectangleF ring = new(centerX - 48f, centerY - 48f, 96f, 96f);
        using var backPen = new Pen(Color.FromArgb(136, 12, 14, 18), 5.4f);
        using var progressPen = new Pen(Color.FromArgb(238, color), 5.0f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawEllipse(backPen, ring);
        graphics.DrawArc(progressPen, ring, -90f, progress * 360f);

        using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var text = new SolidBrush(Color.FromArgb(245, color));
        using StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        string line = $"{label} {remainingSec:0.0}s";
        RectangleF textRect = new(centerX - 120f, ring.Bottom + 6f, 240f, 20f);
        graphics.DrawString(line, _tinyHudFont, shadow, new RectangleF(textRect.X + 1f, textRect.Y + 1f, textRect.Width, textRect.Height), centered);
        graphics.DrawString(line, _tinyHudFont, text, textRect, centered);
    }

    private static double ResolveHeatLockInitialHeatForProgress(SimulationEntity entity)
    {
        double initialHeat = entity.HeatLockInitialHeat;
        if (initialHeat <= 1e-6)
        {
            initialHeat = Math.Max(entity.Heat, entity.MaxHeat + Math.Max(1.0, entity.MaxHeat * 0.10));
        }

        return Math.Max(initialHeat, Math.Max(entity.Heat, 1.0));
    }

    private void DrawRespawnInvincibilityBadge(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || entity.RespawnInvincibleTimerSec <= 1e-6)
        {
            return;
        }

        float radius = 23f;
        PointF center = new(ClientSize.Width - 58f, ClientSize.Height - 66f);
        RectangleF circle = new(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        using var back = new SolidBrush(Color.FromArgb(190, 34, 25, 8));
        using var gold = new Pen(Color.FromArgb(250, 255, 206, 72), 2.1f);
        using var fill = new SolidBrush(Color.FromArgb(232, 255, 206, 72));
        graphics.FillEllipse(back, circle);
        graphics.DrawEllipse(gold, circle);
        PointF[] shield = BuildShieldIconNeo(center, 12f);
        graphics.FillPolygon(fill, shield);
        using var textBrush = new SolidBrush(Color.FromArgb(250, 255, 245, 210));
        using var shadowBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        string text = "\u65e0\u654c";
        graphics.DrawString(text, _tinyHudFont, shadowBrush, center.X + 8f, center.Y + 9f);
        graphics.DrawString(text, _tinyHudFont, textBrush, center.X + 7f, center.Y + 8f);
    }

    private void DrawCentralQuarterGauges(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || _previewOnly || ClientSize.Width <= 8 || ClientSize.Height <= 8)
        {
            return;
        }

        float safeClientMin = Math.Clamp(Math.Min(ClientSize.Width, ClientSize.Height), 1f, 4096f);
        float diameter = Math.Clamp(safeClientMin * 0.45f, 184f, 1720f);
        float centerX = ClientSize.Width * 0.5f;
        float centerY = ClientSize.Height * 0.5f;
        RectangleF ringRect = new(centerX - diameter * 0.5f, centerY - diameter * 0.5f, diameter, diameter);
        float stroke = Math.Clamp(diameter * 0.026f, 7.0f, 14.0f);
        float outerStroke = Math.Max(3.0f, stroke * 0.62f);

        float hpRatio = SafeGaugeRatio(entity.Health, entity.MaxHealth);
        float powerRatio = entity.EffectiveDrivePowerLimitW <= 1e-6
            ? 0f
            : SafeGaugeRatio(entity.ChassisPowerDrawW, entity.EffectiveDrivePowerLimitW);
        float superCapRatio = SafeGaugeRatio(entity.SuperCapEnergyJ, entity.MaxSuperCapEnergyJ);
        float bufferRatio = SafeGaugeRatio(entity.BufferEnergyJ, entity.MaxBufferEnergyJ);
        float heatRatio = SafeGaugeRatio(entity.Heat, entity.MaxHeat);

        DrawQuarterGaugeArc(graphics, ringRect, 180f, hpRatio, Color.FromArgb(186, 72, 214, 126), stroke);
        DrawQuarterGaugeArc(graphics, ringRect, 270f, powerRatio, Color.FromArgb(194, 255, 214, 48), stroke);
        DrawQuarterGaugeArc(graphics, ringRect, 0f, superCapRatio, Color.FromArgb(194, 255, 96, 196), stroke);
        RectangleF bufferRect = RectangleF.Inflate(ringRect, stroke * 0.82f, stroke * 0.82f);
        DrawPartialGaugeArc(graphics, bufferRect, 18f, 45f, bufferRatio, Color.FromArgb(162, 168, 174, 184), outerStroke);
        DrawQuarterGaugeArc(graphics, ringRect, 90f, heatRatio, Color.FromArgb(186, 228, 130, 58), stroke);

        double projectileSpeed = SimulationCombatMath.ProjectileSpeedMps(entity);
        int allowedShots = ResolveAllowedShotCountNeo(entity);
        string projectileSpeedText = $"{projectileSpeed:0.0}m/s";
        string allowedShotsText = $"允许发弹量 {allowedShots}";
        string autoAimModeText = $"自瞄 {ResolveAutoAimTargetModeHudLabel(entity)}";
        string assistModeText = ResolveAutoAimAssistLabel(_autoAimAssistMode);
        float textX = ringRect.Right + stroke * 0.45f;
        float textY = centerY - stroke * 1.15f;
        using var hudTextBrush = new SolidBrush(Color.FromArgb(168, 232, 236, 242));
        graphics.DrawString(projectileSpeedText, _tinyHudFont, hudTextBrush, textX, textY);
        graphics.DrawString(allowedShotsText, _tinyHudFont, hudTextBrush, textX, textY + 16f);

        SizeF autoAimModeSize = graphics.MeasureString(autoAimModeText, _tinyHudFont);
        SizeF assistModeSize = graphics.MeasureString(assistModeText, _tinyHudFont);
        float leftTextX = ringRect.Left - stroke * 0.45f - Math.Max(autoAimModeSize.Width, assistModeSize.Width);
        graphics.DrawString(autoAimModeText, _tinyHudFont, hudTextBrush, leftTextX, textY);
        graphics.DrawString(assistModeText, _tinyHudFont, hudTextBrush, leftTextX, textY + 16f);
    }

    private static void DrawQuarterGaugeArc(Graphics graphics, RectangleF rect, float startAngle, float ratio, Color color, float width)
        => DrawPartialGaugeArc(graphics, rect, startAngle, 90f, ratio, color, width);

    private static float SafeGaugeRatio(double value, double maximum)
    {
        if (!double.IsFinite(value) || !double.IsFinite(maximum) || maximum <= 1e-6)
        {
            return 0f;
        }

        return (float)Math.Clamp(value / maximum, 0.0, 1.0);
    }

    private static void DrawPartialGaugeArc(Graphics graphics, RectangleF rect, float startAngle, float sweepAngle, float ratio, Color color, float width)
    {
        if (!float.IsFinite(rect.X)
            || !float.IsFinite(rect.Y)
            || !float.IsFinite(rect.Width)
            || !float.IsFinite(rect.Height)
            || !float.IsFinite(startAngle)
            || !float.IsFinite(sweepAngle)
            || !float.IsFinite(ratio)
            || !float.IsFinite(width)
            || rect.Width < 2f
            || rect.Height < 2f
            || rect.Width > 8192f
            || rect.Height > 8192f
            || MathF.Abs(sweepAngle) <= 1e-4f
            || width <= 0.1f)
        {
            return;
        }

        float safeWidth = Math.Clamp(width, 1f, Math.Min(rect.Width, rect.Height) * 0.25f);
        float safeRatio = Math.Clamp(ratio, 0f, 1f);
        using var backPen = new Pen(Color.FromArgb(42, 220, 226, 236), safeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        using var fillPen = new Pen(color, safeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        try
        {
            graphics.DrawArc(backPen, rect, startAngle, sweepAngle);
            if (safeRatio > 1e-4f)
            {
                graphics.DrawArc(fillPen, rect, startAngle, safeRatio * sweepAngle);
            }
        }
        catch (Exception exception) when (exception is OutOfMemoryException or ArgumentException)
        {
            // GDI+ reports invalid arc geometry as OutOfMemory; skip this non-critical HUD element.
        }
    }

    private void DrawF3DebugPoseOverlay(Graphics graphics)
    {
        if (!_showCollisionDebug)
        {
            return;
        }

        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null)
        {
            return;
        }

        (int terrainTriangleCount, int terrainWallCount, int regionCount) = DrawTerrainCollisionDebugOverlay(graphics, entity);
        List<string> lines = new()
        {
            $"坐标 X:{entity.X:0.0} Y:{entity.Y:0.0} Z:{entity.GroundHeightM + entity.AirborneHeightM:0.00}m",
            $"姿态 yaw:{ResolveDisplayWorldYawDeg(entity.AngleDeg):+0.0;-0.0;0.0} pitch:{entity.ChassisPitchDeg:+0.0;-0.0;0.0} roll:{entity.ChassisRollDeg:+0.0;-0.0;0.0}",
        };

        (double speedMps, double displayVx, double displayVy) = ResolveDisplayWorldVelocity(entity);
        lines.Add($"实时 XY {entity.X:0.00}, {entity.Y:0.00}  总速 {speedMps:0.00}m/s  角速度 {entity.AngularVelocityDegPerSec:+0.0;-0.0;0.0}deg/s");
        lines.Add($"速度分量 vx:{displayVx:+0.00;-0.00;0.00} vy:{displayVy:+0.00;-0.00;0.00}");
        lines.Add($"地形碰撞 triangles:{terrainTriangleCount} walls:{terrainWallCount}");
        lines.Add($"Buff/Debuff regions:{regionCount}");
        int wheelIndex = 0;
        foreach (double clearanceM in ResolveWheelGroundClearances(entity))
        {
            lines.Add($"轮{wheelIndex + 1} 离地 {clearanceM * 100.0:+0.0;-0.0;0.0}cm");
            wheelIndex++;
        }

        Rectangle panel = new(14, HudHeight + 8, 300, 24 + lines.Count * 17);
        using GraphicsPath path = CreateRoundedRectangle(panel, 7);
        using var fill = new SolidBrush(Color.FromArgb(178, 4, 8, 12));
        using var border = new Pen(Color.FromArgb(170, 255, 220, 92), 1f);
        using var text = new SolidBrush(Color.FromArgb(238, 246, 242, 210));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        float y = panel.Y + 10;
        foreach (string line in lines)
        {
            graphics.DrawString(line, _tinyHudFont, text, panel.X + 12, y);
            y += 17f;
        }
    }

    private int CollectTerrainCollisionDebugTriangles(SimulationEntity entity, out int wallCount)
    {
        wallCount = 0;
        if (_cachedRuntimeGrid is null || _cachedRuntimeGrid.CollisionSurface is null)
        {
            _terrainCollisionDebugTriangleBuffer.Clear();
            return 0;
        }

        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double debugRadiusM = Math.Max(2.4, Math.Max(entity.BodyLengthM, entity.BodyWidthM) * 2.4);
        double debugRadiusWorld = debugRadiusM / metersPerWorldUnit;
        int triangleCount = _cachedRuntimeGrid.CollectCollisionDebugTriangles(
            entity.X,
            entity.Y,
            debugRadiusWorld,
            _terrainCollisionDebugTriangleBuffer,
            maxTriangles: 1536);
        if (triangleCount <= 0)
        {
            return 0;
        }

        foreach (TerrainCollisionDebugTriangle triangle in _terrainCollisionDebugTriangleBuffer)
        {
            if (!triangle.Walkable)
            {
                wallCount++;
            }
        }

        return triangleCount;
    }

    private (int TriangleCount, int WallCount, int RegionCount) DrawTerrainCollisionDebugOverlay(Graphics graphics, SimulationEntity entity)
    {
        int triangleCount = CollectTerrainCollisionDebugTriangles(entity, out int wallCount);
        int regionCount = CountBuffDebuffDebugRegions();
        if (triangleCount <= 0)
        {
            if (!(UseGpuRenderer && _hasPresentedGpuFrame))
            {
                DrawGdiBuffDebuffDebugRegions(graphics);
            }

            return (0, 0, regionCount);
        }

        if (UseGpuRenderer && _hasPresentedGpuFrame)
        {
            return (triangleCount, wallCount, regionCount);
        }

        var faces = new List<ProjectedFace>(triangleCount);
        foreach (TerrainCollisionDebugTriangle triangle in _terrainCollisionDebugTriangleBuffer)
        {
            bool wall = !triangle.Walkable;
            Vector3 a = ToScenePoint(triangle.A.X, triangle.A.Z, triangle.A.Y);
            Vector3 b = ToScenePoint(triangle.B.X, triangle.B.Z, triangle.B.Y);
            Vector3 c = ToScenePoint(triangle.C.X, triangle.C.Z, triangle.C.Y);
            Color fill = wall
                ? Color.FromArgb(62, 255, 108, 96)
                : Color.FromArgb(46, 84, 178, 255);
            Color edge = wall
                ? Color.FromArgb(196, 255, 172, 120)
                : Color.FromArgb(176, 142, 210, 255);
            if (TryBuildProjectedFace(new[] { a, b, c }, fill, edge, out ProjectedFace face))
            {
                faces.Add(face);
            }
        }

        if (faces.Count > 0)
        {
            faces.Sort((left, right) => right.AverageDepth.CompareTo(left.AverageDepth));
            DrawProjectedFaceBatch(graphics, faces, 0.8f);
        }

        using var wallPen = new Pen(Color.FromArgb(214, 255, 188, 132), 1.2f);
        using var walkablePen = new Pen(Color.FromArgb(168, 132, 208, 255), 0.9f);
        foreach (TerrainCollisionDebugTriangle triangle in _terrainCollisionDebugTriangleBuffer)
        {
            Vector3 a = ToScenePoint(triangle.A.X, triangle.A.Z, triangle.A.Y);
            Vector3 b = ToScenePoint(triangle.B.X, triangle.B.Z, triangle.B.Y);
            Vector3 c = ToScenePoint(triangle.C.X, triangle.C.Z, triangle.C.Y);
            Pen pen = triangle.Walkable ? walkablePen : wallPen;
            DrawLine3d(graphics, a, b, pen);
            DrawLine3d(graphics, b, c, pen);
            DrawLine3d(graphics, c, a, pen);
        }

        DrawGdiBuffDebuffDebugRegions(graphics);
        return (triangleCount, wallCount, regionCount);
    }

    private void DrawGpuTerrainCollisionDebugGeometry(SimulationEntity entity)
    {
        if (!(_gpuGeometryPass && UseGpuRenderer) || !_showCollisionDebug)
        {
            return;
        }

        int triangleCount = CollectTerrainCollisionDebugTriangles(entity, out _);
        GpuDynamicBatchKind previousBatch = _gpuCurrentDynamicBatch;
        _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Facility;
        try
        {
            // F3 下把局部地形碰撞面直接压进 GPU 调试几何，避免同一批碰撞信息再走一遍 GDI。
            foreach (TerrainCollisionDebugTriangle triangle in _terrainCollisionDebugTriangleBuffer)
            {
                Vector3 a = ToScenePoint(triangle.A.X, triangle.A.Z, triangle.A.Y);
                Vector3 b = ToScenePoint(triangle.B.X, triangle.B.Z, triangle.B.Y);
                Vector3 c = ToScenePoint(triangle.C.X, triangle.C.Z, triangle.C.Y);
                Color fill = triangle.Walkable
                    ? Color.FromArgb(72, 84, 178, 255)
                    : Color.FromArgb(92, 255, 108, 96);
                AppendGpuShadedPolygon(new[] { a, b, c }, fill, triangle.Walkable ? 0.72f : 0.62f);
            }

            DrawGpuBuffDebuffDebugRegions();
        }
        finally
        {
            _gpuCurrentDynamicBatch = previousBatch;
        }
    }

    private void DrawGpuBuffDebuffDebugRegions()
    {
        foreach (FacilityRegion region in _host.MapPreset.Facilities)
        {
            if (!IsBuffDebuffDebugRegion(region))
            {
                continue;
            }

            DrawGpuBuffDebuffDebugRegion(region, ResolveBuffDebuffDebugColor(region));
        }
    }

    private void DrawGpuBuffDebuffDebugRegion(FacilityRegion region, Color color)
    {
        float height = Math.Max(0.035f, (float)region.HeightM + 0.045f);
        Color fill = Color.FromArgb(Math.Min((int)color.A, 56), color);
        Color edge = Color.FromArgb(Math.Max(165, Math.Min(224, color.A + 88)), color);

        if (string.Equals(region.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && region.Points.Count >= 3)
        {
            Vector3[] points = region.Points
                .Select(point => ToScenePoint(point.X, point.Y, height))
                .ToArray();
            for (int index = 1; index < points.Length - 1; index++)
            {
                AppendOrDrawGpuTriangle(points[0], points[index], points[index + 1], fill);
            }

            DrawGpuDebugRegionOutline(points, edge);
            return;
        }

        double minX = Math.Min(region.X1, region.X2);
        double maxX = Math.Max(region.X1, region.X2);
        double minY = Math.Min(region.Y1, region.Y2);
        double maxY = Math.Max(region.Y1, region.Y2);
        Vector3[] corners =
        {
            ToScenePoint(minX, minY, height),
            ToScenePoint(maxX, minY, height),
            ToScenePoint(maxX, maxY, height),
            ToScenePoint(minX, maxY, height),
        };
        AppendOrDrawGpuQuad(corners[0], corners[1], corners[2], corners[3], fill);
        DrawGpuDebugRegionOutline(corners, edge);
    }

    private int CountBuffDebuffDebugRegions()
        => _host.MapPreset.Facilities.Count(IsBuffDebuffDebugRegion);

    private void DrawGdiBuffDebuffDebugRegions(Graphics graphics)
    {
        var faces = new List<ProjectedFace>();
        foreach (FacilityRegion region in _host.MapPreset.Facilities)
        {
            if (!IsBuffDebuffDebugRegion(region))
            {
                continue;
            }

            Color baseColor = ResolveBuffDebuffDebugColor(region);
            Color fill = Color.FromArgb(Math.Min(72, (int)baseColor.A), baseColor);
            Color edge = Color.FromArgb(Math.Max(176, Math.Min(230, baseColor.A + 72)), baseColor);
            if (TryBuildProjectedRegionFace(region, fill, edge, out ProjectedFace face))
            {
                faces.Add(face);
            }

            DrawGdiBuffDebuffRegionOutline(graphics, region, edge);
        }

        if (faces.Count <= 0)
        {
            return;
        }

        faces.Sort((left, right) => right.AverageDepth.CompareTo(left.AverageDepth));
        DrawProjectedFaceBatch(graphics, faces, 0.95f);
    }

    private bool TryBuildProjectedRegionFace(FacilityRegion region, Color fill, Color edge, out ProjectedFace face)
    {
        float height = Math.Max(0.035f, (float)region.HeightM + 0.055f);
        if (string.Equals(region.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && region.Points.Count >= 3)
        {
            Vector3[] points = region.Points
                .Select(point => ToScenePoint(point.X, point.Y, height))
                .ToArray();
            return TryBuildProjectedFace(points, fill, edge, out face);
        }

        double minX = Math.Min(region.X1, region.X2);
        double maxX = Math.Max(region.X1, region.X2);
        double minY = Math.Min(region.Y1, region.Y2);
        double maxY = Math.Max(region.Y1, region.Y2);
        Vector3[] corners =
        {
            ToScenePoint(minX, minY, height),
            ToScenePoint(maxX, minY, height),
            ToScenePoint(maxX, maxY, height),
            ToScenePoint(minX, maxY, height),
        };
        return TryBuildProjectedFace(corners, fill, edge, out face);
    }

    private void DrawGdiBuffDebuffRegionOutline(Graphics graphics, FacilityRegion region, Color color)
    {
        using var pen = new Pen(color, 1.35f);
        float height = Math.Max(0.035f, (float)region.HeightM + 0.055f);
        if (string.Equals(region.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && region.Points.Count >= 2)
        {
            for (int index = 0; index < region.Points.Count; index++)
            {
                Point2D a = region.Points[index];
                Point2D b = region.Points[(index + 1) % region.Points.Count];
                DrawLine3d(
                    graphics,
                    ToScenePoint(a.X, a.Y, height),
                    ToScenePoint(b.X, b.Y, height),
                    pen);
            }

            return;
        }

        Vector3[] corners =
        {
            ToScenePoint(Math.Min(region.X1, region.X2), Math.Min(region.Y1, region.Y2), height),
            ToScenePoint(Math.Max(region.X1, region.X2), Math.Min(region.Y1, region.Y2), height),
            ToScenePoint(Math.Max(region.X1, region.X2), Math.Max(region.Y1, region.Y2), height),
            ToScenePoint(Math.Min(region.X1, region.X2), Math.Max(region.Y1, region.Y2), height),
        };
        for (int index = 0; index < corners.Length; index++)
        {
            DrawLine3d(graphics, corners[index], corners[(index + 1) % corners.Length], pen);
        }
    }

    private static void DrawGpuDebugRegionOutline(IReadOnlyList<Vector3> points, Color color)
    {
        if (points.Count < 2)
        {
            return;
        }

        for (int index = 0; index < points.Count; index++)
        {
            DrawGpuLine(points[index], points[(index + 1) % points.Count], color);
        }
    }

    private static bool IsBuffDebuffDebugRegion(FacilityRegion region)
    {
        string type = region.Type ?? string.Empty;
        return type.Contains("buff", StringComparison.OrdinalIgnoreCase)
            || type.Contains("debuff", StringComparison.OrdinalIgnoreCase)
            || type.Contains("weak", StringComparison.OrdinalIgnoreCase)
            || type.Contains("slow", StringComparison.OrdinalIgnoreCase)
            || type.Contains("damage", StringComparison.OrdinalIgnoreCase);
    }

    private static Color ResolveBuffDebuffDebugColor(FacilityRegion region)
    {
        string type = region.Type ?? string.Empty;
        if (type.Contains("debuff", StringComparison.OrdinalIgnoreCase)
            || type.Contains("weak", StringComparison.OrdinalIgnoreCase)
            || type.Contains("slow", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(108, 255, 92, 92);
        }

        if (type.Contains("cool", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(102, 94, 224, 176);
        }

        if (type.Contains("power", StringComparison.OrdinalIgnoreCase)
            || type.Contains("supply", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(102, 255, 220, 84);
        }

        return Color.FromArgb(96, 118, 220, 255);
    }

    private IEnumerable<double> ResolveWheelGroundClearances(SimulationEntity entity)
    {
        IReadOnlyList<(double X, double Y)> wheelOffsets = entity.WheelOffsetsM;
        if (wheelOffsets.Count == 0)
        {
            double halfLength = Math.Max(0.10, entity.BodyLengthM * 0.5);
            double halfWidth = Math.Max(0.09, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5);
            wheelOffsets = new[]
            {
                (halfLength, halfWidth),
                (halfLength, -halfWidth),
                (-halfLength, halfWidth),
                (-halfLength, -halfWidth),
            };
        }

        double yawRad = entity.AngleDeg * Math.PI / 180.0;
        double forwardX = Math.Cos(yawRad);
        double forwardY = Math.Sin(yawRad);
        double rightX = -forwardY;
        double rightY = forwardX;
        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double chassisPlaneM = entity.GroundHeightM + entity.AirborneHeightM;
        foreach ((double localForwardM, double localRightM) in wheelOffsets)
        {
            double worldX = entity.X + (forwardX * localForwardM + rightX * localRightM) / metersPerWorldUnit;
            double worldY = entity.Y + (forwardY * localForwardM + rightY * localRightM) / metersPerWorldUnit;
            double terrainHeightM = SampleTerrainHeightMeters(worldX, worldY);
            yield return chassisPlaneM - terrainHeightM;
        }
    }

    private void DrawPredictedProjectileTrajectory(Graphics graphics)
    {
        if (!_showProjectileTrails || _paused)
        {
            return;
        }

        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || !entity.IsAlive)
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double yawRad = entity.TurretYawDeg * Math.PI / 180.0;
        double pitchRad = entity.GimbalPitchDeg * Math.PI / 180.0;
        double speedMps = SimulationCombatMath.ProjectileSpeedMps(entity);
        (double x, double y, double heightM) = SimulationCombatMath.ComputeMuzzlePoint(_host.World, entity, entity.GimbalPitchDeg);
        double inheritedVxWorldPerSec = entity.HasObservedKinematics ? entity.ObservedVelocityXWorldPerSec : entity.VelocityXWorldPerSec;
        double inheritedVyWorldPerSec = entity.HasObservedKinematics ? entity.ObservedVelocityYWorldPerSec : entity.VelocityYWorldPerSec;
        double vxMps = inheritedVxWorldPerSec * metersPerWorldUnit + Math.Cos(pitchRad) * Math.Cos(yawRad) * speedMps;
        double vyMps = inheritedVyWorldPerSec * metersPerWorldUnit + Math.Cos(pitchRad) * Math.Sin(yawRad) * speedMps;
        double vzMps = Math.Sin(pitchRad) * speedMps;

        List<PointF> projected = new(80);
        RuntimeGridData? runtimeGrid = _host.RuntimeGrid;
        double dt = 0.035;
        double maxLifeSec = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 4.2 : 2.8;
        bool hasImpactSurface = false;
        for (double t = 0.0; t <= maxLifeSec; t += dt)
        {
            Vector3 scenePoint = ToScenePoint(x, y, (float)heightM);
            if (TryProject(scenePoint, out PointF point, out _))
            {
                projected.Add(point);
            }

            if (heightM < -0.05 || IsPredictedProjectileOutsideWorld(runtimeGrid, x, y))
            {
                break;
            }

            if (runtimeGrid is not null && runtimeGrid.IsValid)
            {
                float terrainHeight = runtimeGrid.SampleOcclusionHeight((float)x, (float)y);
                if (heightM <= terrainHeight + 0.015 && t > 0.05)
                {
                    hasImpactSurface = true;
                    break;
                }
            }

            ApplyPredictedProjectileStep(entity.AmmoType, metersPerWorldUnit, dt, ref x, ref y, ref heightM, ref vxMps, ref vyMps, ref vzMps);
        }

        if (projected.Count < 2)
        {
            return;
        }

        using GraphicsPath path = new();
        path.AddLines(projected.ToArray());
        using var glowPen = new Pen(Color.FromArgb(96, 255, 190, 56), 6.0f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var pathPen = new Pen(Color.FromArgb(238, 255, 214, 76), 2.0f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        graphics.DrawPath(glowPen, path);
        graphics.DrawPath(pathPen, path);

        PointF end = projected[^1];
        using var dotBrush = new SolidBrush(Color.FromArgb(245, 255, 222, 96));
        graphics.FillEllipse(dotBrush, end.X - 3.0f, end.Y - 3.0f, 6.0f, 6.0f);
        if (hasImpactSurface)
        {
            float radius = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 11f : 7f;
            using var impactPen = new Pen(Color.FromArgb(245, 255, 224, 72), 2.0f);
            graphics.DrawEllipse(impactPen, end.X - radius, end.Y - radius, radius * 2f, radius * 2f);
        }
    }

    private static bool IsPredictedProjectileOutsideWorld(RuntimeGridData? runtimeGrid, double x, double y)
    {
        if (runtimeGrid is null || !runtimeGrid.IsValid)
        {
            return false;
        }

        return x < 0.0
            || y < 0.0
            || x >= runtimeGrid.WidthCells * runtimeGrid.CellWidthWorld
            || y >= runtimeGrid.HeightCells * runtimeGrid.CellHeightWorld;
    }

    private static void ApplyPredictedProjectileStep(
        string ammoType,
        double metersPerWorldUnit,
        double dt,
        ref double x,
        ref double y,
        ref double heightM,
        ref double vxMps,
        ref double vyMps,
        ref double vzMps)
    {
        double speedMps = Math.Sqrt(vxMps * vxMps + vyMps * vyMps + vzMps * vzMps);
        if (speedMps > 1e-6)
        {
            double diameterM = SimulationCombatMath.ProjectileDiameterM(ammoType);
            double areaM2 = Math.PI * diameterM * diameterM * 0.25;
            double massKg = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 0.041 : 0.0032;
            double dragAccelMps2 = 0.5 * 1.20 * 0.47 * areaM2 * speedMps * speedMps / Math.Max(0.001, massKg);
            dragAccelMps2 = Math.Min(dragAccelMps2, speedMps / Math.Max(dt, 1e-6) * 0.72);
            double dragStep = dragAccelMps2 * dt / speedMps;
            vxMps -= vxMps * dragStep;
            vyMps -= vyMps * dragStep;
            vzMps -= vzMps * dragStep;
        }

        vzMps -= 9.81 * dt;
        x += vxMps / Math.Max(metersPerWorldUnit, 1e-6) * dt;
        y += vyMps / Math.Max(metersPerWorldUnit, 1e-6) * dt;
        heightM += vzMps * dt;
    }

    private void DrawHeroDeploymentChargeRing(Graphics graphics, float centerX, float centerY)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null
            || !string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool exiting = entity.HeroDeploymentActive;
        double timerSec = exiting
            ? entity.HeroDeploymentExitHoldTimerSec
            : entity.HeroDeploymentHoldTimerSec;
        if (timerSec <= 1e-4)
        {
            return;
        }

        float progress = (float)Math.Clamp(timerSec / 2.0, 0.0, 1.0);
        RectangleF ring = new(centerX - 30f, centerY - 30f, 60f, 60f);
        using var backPen = new Pen(Color.FromArgb(128, 24, 28, 34), 4f);
        using var progressPen = new Pen(exiting ? Color.FromArgb(235, 255, 132, 92) : Color.FromArgb(235, 255, 216, 92), 4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawEllipse(backPen, ring);
        graphics.DrawArc(progressPen, ring, -90f, progress * 360f);
    }

    private Rectangle CreateRightSideHudCard(int width, int y, int height)
        => new(ClientSize.Width - width - 28, y, width, height);

    private void DrawRightSideHudCard(
        Graphics graphics,
        Rectangle bounds,
        Color fillColor,
        Color borderColor,
        string title,
        string detail,
        Color titleColor,
        Color detailColor,
        float titleTop = 10f,
        float detailTop = 34f)
    {
        using GraphicsPath path = CreateRoundedRectangle(bounds, 10);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(borderColor, 1.2f);
        using var titleBrush = new SolidBrush(titleColor);
        using var detailBrush = new SolidBrush(detailColor);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString(title, _smallHudFont, titleBrush, bounds.X + 14, bounds.Y + titleTop);
        graphics.DrawString(detail, _tinyHudFont, detailBrush, new RectangleF(bounds.X + 14, bounds.Y + detailTop, bounds.Width - 28, bounds.Height - detailTop - 8));
    }

    private void DrawRightSideProgressBar(Graphics graphics, RectangleF bar, float progress, Color color)
    {
        float clamped = Math.Clamp(progress, 0f, 1f);
        using var back = new SolidBrush(Color.FromArgb(128, 38, 44, 52));
        using var fill = new SolidBrush(Color.FromArgb(236, color));
        using var border = new Pen(Color.FromArgb(156, 196, 206, 216), 1f);
        graphics.FillRectangle(back, bar);
        graphics.FillRectangle(fill, bar.X, bar.Y, bar.Width * clamped, bar.Height);
        graphics.DrawRectangle(border, bar.X, bar.Y, bar.Width, bar.Height);
    }

    private void DrawCenteredHoldProgress(
        Graphics graphics,
        string title,
        string detail,
        float progress,
        Color color)
    {
        float clamped = Math.Clamp(progress, 0f, 1f);
        Rectangle box = new(ClientSize.Width / 2 - 190, ClientSize.Height / 2 + 72, 380, 72);
        using GraphicsPath path = CreateRoundedRectangle(box, 8);
        using var fill = new SolidBrush(Color.FromArgb(168, 14, 20, 28));
        using var border = new Pen(Color.FromArgb(218, color), 1.2f);
        using var titleBrush = new SolidBrush(Color.FromArgb(248, color));
        using var detailBrush = new SolidBrush(Color.FromArgb(232, 232, 240, 248));
        using StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString(title, _smallHudFont, titleBrush, new RectangleF(box.X + 12, box.Y + 8, box.Width - 24, 20), center);
        graphics.DrawString(detail, _tinyHudFont, detailBrush, new RectangleF(box.X + 12, box.Y + 30, box.Width - 24, 16), center);
        DrawRightSideProgressBar(graphics, new RectangleF(box.X + 18, box.Bottom - 18, box.Width - 36, 8), clamped, color);
    }

#pragma warning disable CS0162
    private void DrawWeaponLockOverlay(Graphics graphics)
    {
        if (_paused)
        {
            return;
        }

        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null)
        {
            return;
        }

        if (TryResolveCriticalStateOverlay(entity, out string title, out string detail, out string centerLabel, out float progress))
        {
            _ = title;
            _ = detail;
            _ = centerLabel;
            _ = progress;
            return;
        }

        if (!_firstPersonView)
        {
            return;
        }

        string? lockText = ResolveWeaponLockOverlayText(entity);
        if (string.IsNullOrWhiteSpace(lockText))
        {
            return;
        }

        Rectangle rightBox = CreateRightSideHudCard(344, HudHeight + 76, 84);
        DrawRightSideHudCard(
            graphics,
            rightBox,
            Color.FromArgb(204, 14, 20, 28),
            Color.FromArgb(220, 132, 154, 180),
            "武器锁定",
            lockText,
            Color.FromArgb(255, 232, 238, 246),
            Color.FromArgb(238, 218, 228, 240),
            titleTop: 9f,
            detailTop: 34f);
        return;

        Rectangle box = new(ClientSize.Width / 2 - 250, ClientSize.Height / 2 - 42, 500, 84);
        using GraphicsPath path = CreateRoundedRectangle(box, 12);
        using var fill = new SolidBrush(Color.FromArgb(196, 14, 20, 28));
        using var border = new Pen(Color.FromArgb(220, 132, 154, 180), 1.4f);
        using var titleBrush = new SolidBrush(Color.FromArgb(255, 232, 238, 246));
        using var textBrush = new SolidBrush(Color.FromArgb(238, 218, 228, 240));
        StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString("枪管锁定", _hudMidFont, titleBrush, new RectangleF(box.X, box.Y + 14, box.Width, 22), center);
        graphics.DrawString(lockText, _smallHudFont, textBrush, new RectangleF(box.X + 18, box.Y + 38, box.Width - 36, 24), center);
    }

    private bool TryResolveCriticalStateOverlay(
        SimulationEntity entity,
        out string title,
        out string detail,
        out string centerLabel,
        out float progress)
    {
        title = string.Empty;
        detail = string.Empty;
        centerLabel = string.Empty;
        progress = 0f;

        if (entity.PowerCutTimerSec > 1e-6)
        {
            double remainingSec = Math.Max(0.0, entity.PowerCutTimerSec);
            title = "超功率";
            detail = $"底盘断电中，还剩 {remainingSec:0.0}s";
            centerLabel = $"{remainingSec:0.0}s";
            progress = (float)Math.Clamp(1.0 - remainingSec / 5.0, 0.0, 1.0);
            return true;
        }

        if (entity.HeatLockTimerSec > 1e-6 || string.Equals(entity.State, "heat_locked", StringComparison.OrdinalIgnoreCase))
        {
            ResolvedRoleProfile profile = _host.ResolveRuntimeProfile(entity);
            double coolingRate = Math.Max(0.1, profile.HeatDissipationRate * Math.Max(0.1, entity.DynamicCoolingMult));
            double unlockSec = Math.Max(entity.HeatLockTimerSec, entity.Heat / coolingRate);
            double initialUnlockSec = Math.Max(0.5, ResolveHeatLockInitialHeatForProgress(entity) / coolingRate);
            title = "超热量";
            detail = $"枪管锁定，预计 {unlockSec:0.0}s 后恢复";
            centerLabel = $"{unlockSec:0.0}s";
            progress = (float)Math.Clamp(1.0 - unlockSec / initialUnlockSec, 0.0, 1.0);
            return true;
        }

        return false;
    }

    private void DrawCriticalStateOverlay(
        Graphics graphics,
        string title,
        string detail,
        string centerLabel,
        float progress,
        bool lowerOnScreen = false)
    {
        float alphaScale = _firstPersonView ? 1.0f : 0.88f;
        Rectangle rightBox = CreateRightSideHudCard(352, lowerOnScreen ? HudHeight + 172 : HudHeight + 76, 108);
        using (GraphicsPath rightPath = CreateRoundedRectangle(rightBox, 10))
        using (var fill = new SolidBrush(Color.FromArgb((int)(208 * alphaScale), 36, 8, 8)))
        using (var border = new Pen(Color.FromArgb((int)(232 * alphaScale), 255, 92, 92), 1.3f))
        using (var rightTitleBrush = new SolidBrush(Color.FromArgb((int)(248 * alphaScale), 255, 122, 122)))
        using (var rightDetailBrush = new SolidBrush(Color.FromArgb((int)(232 * alphaScale), 255, 228, 228)))
        using (var rightCenterBrush = new SolidBrush(Color.FromArgb((int)(250 * alphaScale), 255, 242, 242)))
        {
            graphics.FillPath(fill, rightPath);
            graphics.DrawPath(border, rightPath);
            graphics.DrawString(title, _smallHudFont, rightTitleBrush, rightBox.X + 14, rightBox.Y + 10);
            graphics.DrawString(centerLabel, _hudMidFont, rightCenterBrush, rightBox.X + 14, rightBox.Y + 34);
            graphics.DrawString(detail, _tinyHudFont, rightDetailBrush, new RectangleF(rightBox.X + 14, rightBox.Y + 60, rightBox.Width - 28, 22));
            DrawRightSideProgressBar(graphics, new RectangleF(rightBox.X + 14, rightBox.Bottom - 18, rightBox.Width - 28, 8), progress, Color.FromArgb(255, 88, 88));
        }
        return;

        float centerX = ClientSize.Width * 0.5f;
        float centerY = lowerOnScreen
            ? ClientSize.Height * 0.68f
            : ClientSize.Height * 0.5f - 18f;
        RectangleF ringRect = new(centerX - 56f, centerY - 56f, 112f, 112f);
        using var shadowPen = new Pen(Color.FromArgb((int)(110 * alphaScale), 0, 0, 0), 11f);
        using var backPen = new Pen(Color.FromArgb((int)(132 * alphaScale), 68, 14, 14), 8f);
        using var progressPen = new Pen(Color.FromArgb((int)(240 * alphaScale), 255, 88, 88), 8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        using var ringFill = new SolidBrush(Color.FromArgb((int)(176 * alphaScale), 36, 8, 8));
        using var titleBrush = new SolidBrush(Color.FromArgb((int)(248 * alphaScale), 255, 122, 122));
        using var detailBrush = new SolidBrush(Color.FromArgb((int)(232 * alphaScale), 255, 228, 228));
        using var centerBrush = new SolidBrush(Color.FromArgb((int)(250 * alphaScale), 255, 242, 242));

        graphics.FillEllipse(ringFill, ringRect);
        graphics.DrawEllipse(shadowPen, ringRect);
        graphics.DrawEllipse(backPen, ringRect);
        graphics.DrawArc(progressPen, ringRect, -90f, Math.Clamp(progress, 0f, 1f) * 360f);

        StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(centerLabel, _hudMidFont, centerBrush, new RectangleF(ringRect.X, ringRect.Y + 14f, ringRect.Width, 28f), centered);
        graphics.DrawString(title, _hudMidFont, titleBrush, new RectangleF(centerX - 180f, ringRect.Bottom + 10f, 360f, 24f), centered);
        graphics.DrawString(detail, _smallHudFont, detailBrush, new RectangleF(centerX - 230f, ringRect.Bottom + 36f, 460f, 24f), centered);
    }

    private string? ResolveWeaponLockOverlayText(SimulationEntity entity)
    {
        if (entity.HeatLockTimerSec > 1e-6 || string.Equals(entity.State, "heat_locked", StringComparison.OrdinalIgnoreCase))
        {
            ResolvedRoleProfile profile = _host.ResolveRuntimeProfile(entity);
            double coolingRate = Math.Max(0.1, profile.HeatDissipationRate * Math.Max(0.1, entity.DynamicCoolingMult));
            double unlockSec = Math.Max(entity.HeatLockTimerSec, entity.Heat / coolingRate);
            return $"热量超限，预计 {unlockSec:0.0}s 后解锁";
        }

        if (entity.RespawnAmmoLockTimerSec > 1e-6)
        {
            return "复活锁枪，返回自家补给区接触补给增益后立即解锁";
        }

        return null;
    }

    private void DrawAutoAimGuidanceMarker(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null
            || _autoAimAssistMode != AutoAimAssistMode.GuidanceOnly
            || !_autoAimPressed
            || !entity.AutoAimLocked)
        {
            return;
        }

        if (string.Equals(entity.AutoAimTargetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entity.AutoAimTargetId))
        {
            SimulationEntity? energyTarget = _host.World.Entities.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, entity.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
            if (energyTarget is not null
                && TryResolveTrackedEnergyDiskPose(entity, energyTarget, Math.Clamp(entity.AutoAimLeadTimeSec, 0.0, 1.10), out _, out Vector3 energyCenter, out float energyRadius, out _, out _, out _)
                && TryProject(energyCenter, out PointF energyPoint, out _))
            {
                using var energyGuidePen = new Pen(Color.FromArgb(238, 255, 214, 70), 1.5f);
                float energyGuideRadius = Math.Max(13f, energyRadius * 16f);
                using var energyOuterPen = new Pen(Color.FromArgb(160, 0, 0, 0), 3f);
                graphics.DrawEllipse(energyOuterPen, energyPoint.X - energyGuideRadius, energyPoint.Y - energyGuideRadius, energyGuideRadius * 2f, energyGuideRadius * 2f);
                graphics.DrawEllipse(energyGuidePen, energyPoint.X - energyGuideRadius, energyPoint.Y - energyGuideRadius, energyGuideRadius * 2f, energyGuideRadius * 2f);
                using var energyTextBrush = new SolidBrush(Color.FromArgb(240, 255, 228, 90));
                graphics.DrawString($"预测 {entity.AutoAimAccuracy:P0}", _tinyHudFont, energyTextBrush, energyPoint.X + 16f, energyPoint.Y - 10f);
                return;
            }
        }

        Vector3 marker = ToScenePoint(entity.AutoAimAimPointX, entity.AutoAimAimPointY, (float)entity.AutoAimAimPointHeightM);
        if (!TryProject(marker, out PointF point, out _))
        {
            return;
        }

        using var guidePen = new Pen(Color.FromArgb(238, 255, 214, 70), 1.5f);
        using var shadowPen = new Pen(Color.FromArgb(160, 0, 0, 0), 3f);
        float radius = 13f;
        graphics.DrawEllipse(shadowPen, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
        graphics.DrawLine(shadowPen, point.X - 18f, point.Y, point.X - 7f, point.Y);
        graphics.DrawLine(shadowPen, point.X + 7f, point.Y, point.X + 18f, point.Y);
        graphics.DrawLine(shadowPen, point.X, point.Y - 18f, point.X, point.Y - 7f);
        graphics.DrawLine(shadowPen, point.X, point.Y + 7f, point.X, point.Y + 18f);
        graphics.DrawEllipse(guidePen, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
        graphics.DrawLine(guidePen, point.X - 18f, point.Y, point.X - 7f, point.Y);
        graphics.DrawLine(guidePen, point.X + 7f, point.Y, point.X + 18f, point.Y);
        graphics.DrawLine(guidePen, point.X, point.Y - 18f, point.X, point.Y - 7f);
        graphics.DrawLine(guidePen, point.X, point.Y + 7f, point.X, point.Y + 18f);
        using var brush = new SolidBrush(Color.FromArgb(240, 255, 228, 90));
        graphics.DrawString($"预测 {entity.AutoAimAccuracy:P0}", _tinyHudFont, brush, point.X + 16f, point.Y - 10f);
    }

    private void DrawTrackedArmorPlateHighlight(Graphics graphics)
    {
        if (!_firstPersonView || _paused)
        {
            return;
        }

        SimulationEntity? shooter = _host.SelectedEntity;
        if (shooter is null
            || !shooter.AutoAimLocked
            || string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return;
        }

        SimulationEntity? target = _host.World.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        if (string.Equals(shooter.AutoAimTargetKind, "energy_disk", StringComparison.OrdinalIgnoreCase))
        {
            DrawTrackedEnergyDiskHighlight(graphics, shooter, target);
            return;
        }

        if (!TryResolveVisualArmorPlatePose(target, shooter.AutoAimPlateId, out VisualArmorPlatePose visualPlate))
        {
            return;
        }

        Vector3 p1 = visualPlate.Center + visualPlate.Right * visualPlate.HalfSide + visualPlate.Up * visualPlate.HalfSide;
        Vector3 p2 = visualPlate.Center - visualPlate.Right * visualPlate.HalfSide + visualPlate.Up * visualPlate.HalfSide;
        Vector3 p3 = visualPlate.Center - visualPlate.Right * visualPlate.HalfSide - visualPlate.Up * visualPlate.HalfSide;
        Vector3 p4 = visualPlate.Center + visualPlate.Right * visualPlate.HalfSide - visualPlate.Up * visualPlate.HalfSide;
        if (!TryProject(p1, out PointF s1, out _)
            || !TryProject(p2, out PointF s2, out _)
            || !TryProject(p3, out PointF s3, out _)
            || !TryProject(p4, out PointF s4, out _))
        {
            return;
        }

        PointF[] polygon = { s1, s2, s3, s4 };
        using var glowBrush = new SolidBrush(Color.FromArgb(40, 255, 224, 96));
        using var glowPen = new Pen(Color.FromArgb(210, 255, 216, 92), 3.2f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        using var outlinePen = new Pen(Color.FromArgb(255, 255, 245, 196), 1.3f) { DashStyle = DashStyle.Dash, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        graphics.FillPolygon(glowBrush, polygon);
        graphics.DrawPolygon(glowPen, polygon);
        graphics.DrawPolygon(outlinePen, polygon);

        PointF label = new(
            (s1.X + s2.X + s3.X + s4.X) * 0.25f,
            Math.Min(Math.Min(s1.Y, s2.Y), Math.Min(s3.Y, s4.Y)) - 16f);
        using var shadowBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        using var textBrush = new SolidBrush(Color.FromArgb(255, 255, 236, 150));
        string direction = string.IsNullOrWhiteSpace(shooter.AutoAimPlateDirection)
            ? visualPlate.Label
            : shooter.AutoAimPlateDirection;
        graphics.DrawString($"\u9501\u5b9a {direction}", _tinyHudFont, shadowBrush, label.X + 1f, label.Y + 1f);
        graphics.DrawString($"\u9501\u5b9a {direction}", _tinyHudFont, textBrush, label);
    }

    private void DrawVisionPoseSolveOverlay(Graphics graphics)
    {
        if (!_showVisionPoseSolve || _paused)
        {
            return;
        }

        SimulationEntity? shooter = _host.SelectedEntity;
        if (shooter is null
            || !shooter.AutoAimLocked
            || string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return;
        }

        SimulationEntity? target = _host.World.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        TryResolveAutoAimPlate(shooter, target, shooter.AutoAimPlateId!, out ArmorPlateTarget currentPlate);
        AutoAimCompensationProfile? compensation = string.IsNullOrWhiteSpace(currentPlate.Id)
            ? null
            : SimulationCombatMath.ResolveAutoAimCompensationProfile(_host.World, shooter, target, currentPlate);
        (double muzzleWorldX, double muzzleWorldY, double muzzleHeightM) = SimulationCombatMath.ComputeMuzzlePoint(_host.World, shooter, shooter.GimbalPitchDeg);
        Vector3 muzzleM = new((float)(muzzleWorldX * metersPerWorldUnit), (float)muzzleHeightM, (float)(muzzleWorldY * metersPerWorldUnit));
        Vector3 aimPointM = ToScenePoint(shooter.AutoAimAimPointX, shooter.AutoAimAimPointY, (float)shooter.AutoAimAimPointHeightM);
        double displayAimX = shooter.AutoAimAimPointX;
        double displayAimY = shooter.AutoAimAimPointY;
        double displayAimHeightM = shooter.AutoAimAimPointHeightM;
        if (string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && SimulationCombatMath.IsArmorAutoAimTargetKind(shooter.AutoAimTargetKind)
            && !string.IsNullOrWhiteSpace(currentPlate.Id))
        {
            if (TryResolveVisualArmorPlatePose(target, currentPlate.Id, _host.World.GameTimeSec, out VisualArmorPlatePose visualPlate))
            {
                aimPointM = visualPlate.Center;
            }
            else
            {
                aimPointM = ToScenePoint(currentPlate.X, currentPlate.Y, (float)currentPlate.HeightM);
            }

            displayAimX = aimPointM.X / metersPerWorldUnit;
            displayAimY = aimPointM.Z / metersPerWorldUnit;
            displayAimHeightM = aimPointM.Y;
        }
        Vector3 toAim = aimPointM - muzzleM;
        double rangeM = Math.Max(0.001, toAim.Length());
        double observedYawDeg = SimulationCombatMath.NormalizeDeg(Math.Atan2(toAim.Z, toAim.X) * 180.0 / Math.PI);
        double observedPitchDeg = Math.Atan2(toAim.Y, Math.Sqrt(toAim.X * toAim.X + toAim.Z * toAim.Z)) * 180.0 / Math.PI;
        double yawCorrectionDeg = shooter.AutoAimSmoothedYawDeg - observedYawDeg;
        double pitchCorrectionDeg = shooter.AutoAimSmoothedPitchDeg - observedPitchDeg;
        string targetTypeLabel = ResolveVisionTargetKindLabel(shooter.AutoAimTargetKind);
        string sourceLabel = ResolveVisionObservationSourceLabel(shooter.AutoAimTargetKind);
        string plateSummary = $"板号={shooter.AutoAimPlateId}";
        double effectiveLeadTimeSec = Math.Max(0.0, shooter.AutoAimLeadTimeSec + (compensation?.TimeBiasSec ?? 0.0));
        double observedSpeedMps = Math.Sqrt(
            shooter.AutoAimObservedVelocityXMps * shooter.AutoAimObservedVelocityXMps
            + shooter.AutoAimObservedVelocityYMps * shooter.AutoAimObservedVelocityYMps
            + shooter.AutoAimObservedVelocityZMps * shooter.AutoAimObservedVelocityZMps);
        Rectangle panel = new(18, HudHeight + 64, 432, 154);
        using GraphicsPath path = CreateRoundedRectangle(panel, 8);
        using var fill = new SolidBrush(Color.FromArgb(148, 6, 12, 18));
        using var border = new Pen(Color.FromArgb(210, 255, 220, 92), 1f);
        using var titleBrush = new SolidBrush(Color.FromArgb(255, 255, 236, 150));
        using var textBrush = new SolidBrush(Color.FromArgb(232, 224, 234, 238));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        float y = panel.Y + 10;
        graphics.DrawString("F8 视觉解算", _smallHudFont, titleBrush, panel.X + 12, y);
        y += 22f;
        graphics.DrawString($"目标={target.Id} {plateSummary} 类型={targetTypeLabel}", _tinyHudFont, textBrush, panel.X + 12, y);
        y += 18f;
        graphics.DrawString($"观测来源={sourceLabel}", _tinyHudFont, textBrush, panel.X + 12, y);
        y += 18f;
        graphics.DrawString($"距离={rangeM:0.00}m 偏航={observedYawDeg:0.0}° 俯仰={observedPitchDeg:0.0}°", _tinyHudFont, textBrush, panel.X + 12, y);
        y += 18f;
        graphics.DrawString(
            $"速度=({shooter.AutoAimObservedVelocityXMps:0.00},{shooter.AutoAimObservedVelocityYMps:0.00},{shooter.AutoAimObservedVelocityZMps:0.00})m/s 合速={observedSpeedMps:0.00}m/s",
            _tinyHudFont,
            textBrush,
            panel.X + 12,
            y);
        y += 18f;
        graphics.DrawString(
            $"角速度={shooter.AutoAimObservedAngularVelocityRadPerSec:0.000}rad/s 提前量={shooter.AutoAimLeadTimeSec:0.000}s 有效={effectiveLeadTimeSec:0.000}s 提前距离={shooter.AutoAimLeadDistanceM:0.00}m",
            _tinyHudFont,
            textBrush,
            panel.X + 12,
            y);
        y += 18f;
        graphics.DrawString(
            $"瞄准点=({displayAimX:0.00},{displayAimY:0.00},{displayAimHeightM:0.00}) 修正=({yawCorrectionDeg:+0.00;-0.00;0.00},{pitchCorrectionDeg:+0.00;-0.00;0.00})°",
            _tinyHudFont,
            textBrush,
            panel.X + 12,
            y);
        y += 18f;
        string compensationLine = compensation is null
            ? "经验修正=无"
            : $"经验修正={compensation.Value.Name} 平移={compensation.Value.TranslationLeadScale:0.000} 旋转={compensation.Value.AngularLeadScale:0.000} 时间偏置={compensation.Value.TimeBiasSec:+0.000;-0.000;0.000}s";
        graphics.DrawString(compensationLine, _tinyHudFont, textBrush, panel.X + 12, y);
        y += 18f;
        graphics.DrawString(
            $"解算=({shooter.AutoAimSmoothedYawDeg:0.00},{shooter.AutoAimSmoothedPitchDeg:0.00})° 命中率={shooter.AutoAimAccuracy:P0} 距离系数={shooter.AutoAimDistanceCoefficient:0.00} 运动系数={shooter.AutoAimMotionCoefficient:0.00}",
            _tinyHudFont,
            textBrush,
            panel.X + 12,
            y);

        if (TryProject(aimPointM, out PointF leadPoint, out _))
        {
            using var leadPen = new Pen(Color.FromArgb(238, 98, 232, 255), 1.7f);
            graphics.DrawEllipse(leadPen, leadPoint.X - 7f, leadPoint.Y - 7f, 14f, 14f);
            graphics.DrawLine(leadPen, leadPoint.X - 12f, leadPoint.Y, leadPoint.X + 12f, leadPoint.Y);
            graphics.DrawLine(leadPen, leadPoint.X, leadPoint.Y - 12f, leadPoint.X, leadPoint.Y + 12f);
        }

        if (string.Equals(shooter.AutoAimTargetKind, "energy_disk", StringComparison.OrdinalIgnoreCase))
        {
            int diskCount = DrawVisionEnergyDiskModelSet(graphics, shooter, target, out int trackedIndex, out string stateLine);
            using var debugBrush = new SolidBrush(Color.FromArgb(226, 172, 236, 255));
            graphics.DrawString($"能量机关建模：中心+圆盘={diskCount} 当前跟踪={trackedIndex} {stateLine}", _tinyHudFont, debugBrush, panel.X + 12, panel.Bottom + 8);
        }
    }

    private bool TryResolveAutoAimPlate(
        SimulationEntity shooter,
        SimulationEntity target,
        string plateId,
        out ArmorPlateTarget plate)
    {
        plate = default;
        if (string.IsNullOrWhiteSpace(plateId))
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            string targetTeam = shooter.Team;
            SimulationTeamState? teamState = null;
            if (SimulationCombatMath.TryParseEnergyArmIndex(plateId, out string parsedTeam, out _))
            {
                targetTeam = parsedTeam;
                _host.World.Teams.TryGetValue(parsedTeam, out teamState);
            }

            plate = SimulationCombatMath.GetEnergyMechanismTargets(
                    target,
                    metersPerWorldUnit,
                    _host.World.GameTimeSec,
                    targetTeam,
                    teamState)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, plateId, StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrWhiteSpace(plate.Id);
        }

        plate = SimulationCombatMath.GetAttackableArmorPlateTargets(target, metersPerWorldUnit, _host.World.GameTimeSec)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, plateId, StringComparison.OrdinalIgnoreCase));
        return !string.IsNullOrWhiteSpace(plate.Id);
    }

    private void DrawInferredVisionArmorModel(
        Graphics graphics,
        SimulationEntity target,
        VisualArmorPlatePose observedPlate,
        string targetKind)
    {
        if (string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase))
        {
            DrawInferredOutpostArmorModel(graphics, target, observedPlate);
            return;
        }

        if (string.Equals(targetKind, "base_armor", StringComparison.OrdinalIgnoreCase))
        {
            DrawInferredBaseArmorModel(graphics, observedPlate);
            return;
        }

        if (!TryResolveVisionBodyPrior(target, targetKind, out float halfLengthM, out float halfWidthM))
        {
            return;
        }

        float observedYaw = MathF.Atan2(observedPlate.Forward.Z, observedPlate.Forward.X);
        float localPlateYaw = ResolveVisionPlateLocalYawRad(observedPlate.Label);
        float bodyYaw = observedYaw - localPlateYaw;
        Vector3 bodyForward = new(MathF.Cos(bodyYaw), 0f, MathF.Sin(bodyYaw));
        Vector3 bodyRight = new(-bodyForward.Z, 0f, bodyForward.X);
        Vector3 observedLocalNormal = new(MathF.Cos(localPlateYaw), 0f, MathF.Sin(localPlateYaw));
        float centerOffsetX = observedLocalNormal.X * halfLengthM;
        float centerOffsetZ = observedLocalNormal.Z * halfWidthM;
        Vector3 estimatedCenter = observedPlate.Center - bodyForward * centerOffsetX - bodyRight * centerOffsetZ;
        using var inferredPen = new Pen(Color.FromArgb(190, 98, 232, 255), 1.4f) { DashStyle = DashStyle.Dash };
        using var observedPen = new Pen(Color.FromArgb(235, 255, 220, 92), 2.0f);
        DrawVisionPlateQuad(graphics, observedPlate.Center, observedPlate.Right, observedPlate.Up, observedPlate.HalfSide, observedPen);

        ReadOnlySpan<float> localYaws = stackalloc float[] { 0f, MathF.PI, MathF.PI * 0.5f, -MathF.PI * 0.5f };
        for (int index = 0; index < localYaws.Length; index++)
        {
            float localYaw = localYaws[index];
            float offsetX = MathF.Cos(localYaw) * halfLengthM;
            float offsetZ = MathF.Sin(localYaw) * halfWidthM;
            Vector3 center = estimatedCenter + bodyForward * offsetX + bodyRight * offsetZ;
            Vector3 normal = Vector3.Normalize(bodyForward * MathF.Cos(localYaw) + bodyRight * MathF.Sin(localYaw));
            Vector3 plateRight = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
            if (plateRight.LengthSquared() <= 1e-6f)
            {
                plateRight = bodyRight;
            }

            float side = Math.Max(0.055f, observedPlate.HalfSide * 0.92f);
            DrawVisionPlateQuad(graphics, center, plateRight, Vector3.UnitY, side, inferredPen);
        }
    }

    private void DrawInferredOutpostArmorModel(Graphics graphics, SimulationEntity target, VisualArmorPlatePose observedPlate)
    {
        using var inferredPen = new Pen(Color.FromArgb(190, 98, 232, 255), 1.4f) { DashStyle = DashStyle.Dash };
        using var observedPen = new Pen(Color.FromArgb(235, 255, 220, 92), 2.0f);
        DrawVisionPlateQuad(graphics, observedPlate.Center, observedPlate.Right, observedPlate.Up, observedPlate.HalfSide, observedPen);

        IReadOnlyList<ArmorPlateTarget> runtimePlates = SimulationCombatMath.GetAttackableArmorPlateTargets(
                target,
                Math.Max(_host.World.MetersPerWorldUnit, 1e-6),
                _host.World.GameTimeSec)
            .Where(candidate => candidate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (runtimePlates.Count == 0)
        {
            return;
        }

        foreach (ArmorPlateTarget plate in runtimePlates)
        {
            Vector3 center = ToScenePoint(plate.X, plate.Y, (float)plate.HeightM);
            Vector3 normal = Vector3.Normalize(SimulationCombatMath.ResolveArmorPlateNormal(plate));
            Vector3 right = Vector3.Cross(Vector3.UnitY, normal);
            if (right.LengthSquared() <= 1e-6f)
            {
                right = Vector3.UnitZ;
            }

            right = Vector3.Normalize(right);
            Vector3 up = Vector3.Normalize(Vector3.Cross(normal, right));
            float halfWidth = Math.Max(0.055f, (float)Math.Max(plate.WidthM, plate.SideLengthM) * 0.5f);
            float halfHeight = Math.Max(0.045f, (float)Math.Max(plate.HeightSpanM, plate.SideLengthM) * 0.5f);
            DrawVisionPlateQuad(graphics, center, right, up, halfWidth, halfHeight, inferredPen);
        }
    }

    private void DrawInferredBaseArmorModel(Graphics graphics, VisualArmorPlatePose observedPlate)
    {
        using var inferredPen = new Pen(Color.FromArgb(190, 98, 232, 255), 1.4f) { DashStyle = DashStyle.Dash };
        using var observedPen = new Pen(Color.FromArgb(235, 255, 220, 92), 2.0f);
        DrawVisionPlateQuad(graphics, observedPlate.Center, observedPlate.Right, observedPlate.Up, observedPlate.HalfSide, observedPen);

        Vector3 forward = Vector3.Normalize(new Vector3(observedPlate.Forward.X, 0f, observedPlate.Forward.Z));
        if (forward.LengthSquared() <= 1e-6f)
        {
            forward = Vector3.UnitX;
        }

        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        if (right.LengthSquared() <= 1e-6f)
        {
            right = Vector3.UnitZ;
        }

        bool observedTop = string.Equals(observedPlate.Label, "base_top_slide", StringComparison.OrdinalIgnoreCase);
        Vector3 inferredCenter = observedTop
            ? observedPlate.Center - forward * 0.18f - Vector3.UnitY * 0.28f
            : observedPlate.Center + forward * 0.18f + Vector3.UnitY * 0.28f;
        DrawVisionPlateQuad(graphics, inferredCenter, right, Vector3.UnitY, Math.Max(0.055f, observedPlate.HalfSide), inferredPen);
    }

    private static bool TryResolveVisionBodyPrior(SimulationEntity target, string targetKind, out float halfLengthM, out float halfWidthM)
    {
        halfLengthM = 0.26f;
        halfWidthM = 0.20f;
        if (string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase))
        {
            halfLengthM = 0.22f;
            halfWidthM = 0.22f;
            return true;
        }

        if (string.Equals(targetKind, "base_armor", StringComparison.OrdinalIgnoreCase))
        {
            halfLengthM = 0.48f;
            halfWidthM = 0.36f;
            return true;
        }

        if (SimulationCombatMath.IsStructure(target))
        {
            halfLengthM = string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase) ? 0.48f : 0.22f;
            halfWidthM = string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase) ? 0.36f : 0.22f;
            return true;
        }

        string role = target.RoleKey ?? string.Empty;
        if (string.Equals(role, "hero", StringComparison.OrdinalIgnoreCase))
        {
            halfLengthM = 0.34f;
            halfWidthM = 0.26f;
        }
        else if (string.Equals(role, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            halfLengthM = 0.32f;
            halfWidthM = 0.23f;
        }
        else if (string.Equals(role, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            halfLengthM = 0.30f;
            halfWidthM = 0.24f;
        }

        return true;
    }

    private static string ResolveVisionTargetKindLabel(string? targetKind)
    {
        if (string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase))
        {
            return "能量机关圆盘";
        }

        if (string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase))
        {
            return "前哨站装甲板";
        }

        if (string.Equals(targetKind, "base_armor", StringComparison.OrdinalIgnoreCase))
        {
            return "基地顶部装甲板";
        }

        return "车体装甲板";
    }

    private static string ResolveVisionObservationSourceLabel(string? targetKind)
    {
        if (string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase))
        {
            return "视觉圆盘位姿 + 旋转轨迹反解";
        }

        if (string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase))
        {
            return "视觉装甲板位姿 + 前哨站旋转先验";
        }

        if (string.Equals(targetKind, "base_armor", StringComparison.OrdinalIgnoreCase))
        {
            return "视觉装甲板位姿 + 基地几何先验";
        }

        return "视觉装甲板位姿 + 车体几何先验";
    }

    private bool TryResolveEnergyObservationDisk(
        SimulationEntity shooter,
        SimulationEntity target,
        double gameTimeSec,
        out ArmorPlateTarget disk,
        out int trackedArmIndex,
        out string targetTeam)
    {
        disk = default;
        trackedArmIndex = -1;
        targetTeam = shooter.Team;
        if (string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return false;
        }

        if (SimulationCombatMath.TryParseEnergyArmIndex(shooter.AutoAimPlateId, out string parsedTeam, out int armIndex))
        {
            targetTeam = parsedTeam;
            trackedArmIndex = armIndex;
        }

        _host.World.Teams.TryGetValue(targetTeam, out SimulationTeamState? teamState);
        IReadOnlyList<ArmorPlateTarget> disks = SimulationCombatMath.GetEnergyMechanismTargets(
            target,
            Math.Max(_host.World.MetersPerWorldUnit, 1e-6),
            gameTimeSec,
            targetTeam,
            teamState);
        foreach (ArmorPlateTarget candidate in disks)
        {
            if (!SimulationCombatMath.TryParseEnergyArmIndex(candidate.Id, out string candidateTeam, out int candidateArm)
                || !string.Equals(candidateTeam, targetTeam, StringComparison.OrdinalIgnoreCase)
                || candidateArm != trackedArmIndex
                || !IsEnergyObservationRing(candidate))
            {
                continue;
            }

            disk = candidate;
            return true;
        }

        return false;
    }

    private static bool IsEnergyObservationRing(ArmorPlateTarget disk)
    {
        int ringScore = disk.EnergyRingScore;
        if (ringScore <= 0 && !SimulationCombatMath.TryParseEnergyRingScore(disk.Id, out ringScore))
        {
            return false;
        }

        return ringScore == 1;
    }

    private int DrawVisionEnergyDiskModelSet(Graphics graphics, SimulationEntity shooter, SimulationEntity target, out int trackedIndex, out string stateLine)
    {
        trackedIndex = -1;
        stateLine = string.Empty;
        string targetTeam = shooter.Team;
        if (SimulationCombatMath.TryParseEnergyArmIndex(shooter.AutoAimPlateId ?? string.Empty, out string parsedTeam, out _))
        {
            targetTeam = parsedTeam;
        }

        _host.World.Teams.TryGetValue(targetTeam, out SimulationTeamState? teamState);
        IReadOnlyList<ArmorPlateTarget> disks = SimulationCombatMath.GetEnergyMechanismTargets(
            target,
            Math.Max(_host.World.MetersPerWorldUnit, 1e-6),
            _host.World.GameTimeSec,
            targetTeam,
            teamState);
        if (teamState is not null)
        {
            stateLine = $"team={targetTeam} state={teamState.EnergyMechanismState} lit={teamState.EnergyActivatedGroupCount}";
        }
        else
        {
            stateLine = $"team={targetTeam}";
        }

        int trackedArmIndex = -1;
        if (SimulationCombatMath.TryParseEnergyArmIndex(shooter.AutoAimPlateId ?? string.Empty, out _, out int parsedArmIndex))
        {
            trackedArmIndex = parsedArmIndex;
        }

        int drawn = 0;
        Vector3 centerSum = Vector3.Zero;
        foreach (ArmorPlateTarget disk in disks
                     .Where(IsEnergyObservationRing)
                     .OrderBy(candidate =>
                         SimulationCombatMath.TryParseEnergyArmIndex(candidate.Id, out _, out int armIndex) ? armIndex : int.MaxValue))
        {
            if (!SimulationCombatMath.TryParseEnergyArmIndex(disk.Id, out _, out int armIndex))
            {
                continue;
            }

            Vector3 center = ToScenePoint(disk.X, disk.Y, (float)disk.HeightM);
            float radiusM = (float)Math.Max(0.05, Math.Max(disk.WidthM, disk.HeightSpanM) * 0.5);
            float yawRad = (float)(disk.YawDeg * Math.PI / 180.0);
            Vector3 normal = Vector3.Normalize(new Vector3(MathF.Cos(yawRad), 0f, MathF.Sin(yawRad)));
            Vector3 tangent = Vector3.Cross(Vector3.UnitY, normal);
            tangent = tangent.LengthSquared() <= 1e-6f ? Vector3.UnitZ : Vector3.Normalize(tangent);
            bool tracked = armIndex == trackedArmIndex;
            if (tracked)
            {
                trackedIndex = armIndex;
            }

            DrawVisionDiskModel(graphics, center, radiusM, tangent, tracked, $"A{armIndex}");
            centerSum += center;
            drawn++;
        }

        if (drawn > 0)
        {
            Vector3 rotorCenter = centerSum / drawn;
            if (TryProject(rotorCenter, out PointF centerPoint, out _))
            {
                using var centerPen = new Pen(Color.FromArgb(235, 255, 236, 96), 1.8f);
                using var centerBrush = new SolidBrush(Color.FromArgb(235, 255, 236, 96));
                graphics.DrawEllipse(centerPen, centerPoint.X - 6f, centerPoint.Y - 6f, 12f, 12f);
                graphics.DrawLine(centerPen, centerPoint.X - 10f, centerPoint.Y, centerPoint.X + 10f, centerPoint.Y);
                graphics.DrawLine(centerPen, centerPoint.X, centerPoint.Y - 10f, centerPoint.X, centerPoint.Y + 10f);
                graphics.DrawString("center", _tinyHudFont, centerBrush, centerPoint.X + 8f, centerPoint.Y - 12f);
            }
        }

        return drawn;
    }

    private void DrawVisionDiskModel(Graphics graphics, Vector3 center, float radiusM, Vector3 tangent, bool tracked = true, string? label = null)
    {
        if (!TryProject(center, out PointF screenPoint, out _))
        {
            return;
        }

        float radius = 12f;
        if (TryProject(center + Vector3.UnitY * radiusM, out PointF samplePoint, out _))
        {
            radius = Math.Max(radius, Distance(screenPoint, samplePoint));
        }

        if (TryProject(center + tangent * radiusM, out samplePoint, out _))
        {
            radius = Math.Max(radius, Distance(screenPoint, samplePoint));
        }

        Color diskColor = tracked
            ? Color.FromArgb(230, 98, 232, 255)
            : Color.FromArgb(184, 96, 184, 216);
        using var diskPen = new Pen(diskColor, tracked ? 1.9f : 1.2f) { DashStyle = tracked ? DashStyle.Dash : DashStyle.Dot };
        graphics.DrawEllipse(diskPen, screenPoint.X - radius, screenPoint.Y - radius, radius * 2f, radius * 2f);
        if (!string.IsNullOrWhiteSpace(label))
        {
            using var shadowBrush = new SolidBrush(Color.FromArgb(172, 0, 0, 0));
            using var textBrush = new SolidBrush(tracked ? Color.FromArgb(245, 216, 244, 255) : Color.FromArgb(218, 208, 224, 236));
            PointF labelPoint = new(screenPoint.X + radius + 4f, screenPoint.Y - radius - 2f);
            graphics.DrawString(label, _tinyHudFont, shadowBrush, labelPoint.X + 1f, labelPoint.Y + 1f);
            graphics.DrawString(label, _tinyHudFont, textBrush, labelPoint);
        }
    }

    private static float ResolveVisionPlateLocalYawRad(string plateLabel)
    {
        if (plateLabel.EndsWith("_2", StringComparison.OrdinalIgnoreCase))
        {
            return MathF.PI;
        }

        if (plateLabel.EndsWith("_3", StringComparison.OrdinalIgnoreCase))
        {
            return MathF.PI * 0.5f;
        }

        if (plateLabel.EndsWith("_4", StringComparison.OrdinalIgnoreCase))
        {
            return -MathF.PI * 0.5f;
        }

        return 0f;
    }

    private void DrawVisionPlateQuad(Graphics graphics, Vector3 center, Vector3 right, Vector3 up, float halfSide, Pen pen)
        => DrawVisionPlateQuad(graphics, center, right, up, halfSide, halfSide, pen);

    private void DrawVisionPlateQuad(Graphics graphics, Vector3 center, Vector3 right, Vector3 up, float halfWidth, float halfHeight, Pen pen)
    {
        Vector3 p1 = center + right * halfWidth + up * halfHeight;
        Vector3 p2 = center - right * halfWidth + up * halfHeight;
        Vector3 p3 = center - right * halfWidth - up * halfHeight;
        Vector3 p4 = center + right * halfWidth - up * halfHeight;
        if (TryProject(p1, out PointF s1, out _)
            && TryProject(p2, out PointF s2, out _)
            && TryProject(p3, out PointF s3, out _)
            && TryProject(p4, out PointF s4, out _))
        {
            graphics.DrawPolygon(pen, new[] { s1, s2, s3, s4 });
        }
    }

    private void DrawTrackedEnergyDiskHighlight(Graphics graphics, SimulationEntity shooter, SimulationEntity target)
    {
        if (!TryResolveTrackedEnergyDiskPose(shooter, target, 0.0, out ArmorPlateTarget disk, out Vector3 center, out float diskRadiusM, out _, out _, out Vector3 tangent))
        {
            return;
        }

        if (!TryProject(center, out PointF screenPoint, out _))
        {
            return;
        }

        Vector3 upAxis = Vector3.UnitY;
        float radius = 12f;
        if (TryProject(center + upAxis * diskRadiusM, out PointF samplePoint, out _))
        {
            radius = Math.Max(radius, Distance(screenPoint, samplePoint));
        }

        if (TryProject(center - upAxis * diskRadiusM, out samplePoint, out _))
        {
            radius = Math.Max(radius, Distance(screenPoint, samplePoint));
        }

        if (TryProject(center + tangent * diskRadiusM, out samplePoint, out _))
        {
            radius = Math.Max(radius, Distance(screenPoint, samplePoint));
        }

        if (TryProject(center - tangent * diskRadiusM, out samplePoint, out _))
        {
            radius = Math.Max(radius, Distance(screenPoint, samplePoint));
        }

        using var glowPen = new Pen(Color.FromArgb(220, 255, 216, 92), 3.0f);
        using var outlinePen = new Pen(Color.FromArgb(255, 255, 245, 196), 1.2f) { DashStyle = DashStyle.Dash };
        using var shadowBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        using var textBrush = new SolidBrush(Color.FromArgb(255, 255, 236, 150));
        graphics.DrawEllipse(glowPen, screenPoint.X - radius, screenPoint.Y - radius, radius * 2f, radius * 2f);
        graphics.DrawEllipse(outlinePen, screenPoint.X - radius * 1.14f, screenPoint.Y - radius * 1.14f, radius * 2.28f, radius * 2.28f);

        PointF label = new(screenPoint.X - radius * 0.9f, screenPoint.Y - radius - 18f);
        const string text = "\u9501\u5b9a \u80fd\u91cf\u5706\u76d8";
        graphics.DrawString(text, _tinyHudFont, shadowBrush, label.X + 1f, label.Y + 1f);
        graphics.DrawString(text, _tinyHudFont, textBrush, label);
    }

    private bool TryResolveTrackedEnergyDiskPose(
        SimulationEntity shooter,
        SimulationEntity target,
        double leadTimeSec,
        out ArmorPlateTarget disk,
        out Vector3 center,
        out float diskRadiusM,
        out Vector3 normal,
        out Vector3 upAxis,
        out Vector3 tangent)
    {
        disk = default;
        center = default;
        diskRadiusM = 0f;
        normal = Vector3.UnitX;
        upAxis = Vector3.UnitY;
        tangent = Vector3.UnitZ;
        if (string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return false;
        }

        string targetTeam = shooter.Team;
        if (SimulationCombatMath.TryParseEnergyArmIndex(shooter.AutoAimPlateId, out string parsedTeam, out _))
        {
            targetTeam = parsedTeam;
        }

        _host.World.Teams.TryGetValue(targetTeam, out SimulationTeamState? teamState);
        leadTimeSec = Math.Clamp(leadTimeSec, 0.0, 1.10);
        IReadOnlyList<ArmorPlateTarget> disks = SimulationCombatMath.GetEnergyMechanismTargets(
            target,
            Math.Max(_host.World.MetersPerWorldUnit, 1e-6),
            _host.World.GameTimeSec + leadTimeSec,
            targetTeam,
            teamState);
        for (int index = 0; index < disks.Count; index++)
        {
            if (!string.Equals(disks[index].Id, shooter.AutoAimPlateId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            disk = disks[index];
            center = ToScenePoint(disk.X, disk.Y, (float)disk.HeightM);
            diskRadiusM = (float)Math.Max(0.05, Math.Max(disk.WidthM, disk.HeightSpanM) * 0.5);
            float yawRad = (float)(disk.YawDeg * Math.PI / 180.0);
            normal = Vector3.Normalize(new Vector3(MathF.Cos(yawRad), 0f, MathF.Sin(yawRad)));
            upAxis = Vector3.UnitY;
            tangent = Vector3.Cross(upAxis, normal);
            tangent = tangent.LengthSquared() <= 1e-6f ? Vector3.UnitZ : Vector3.Normalize(tangent);
            return true;
        }

        return false;
    }

    private static float Distance(PointF a, PointF b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void DrawPlayerStatusPanel(Graphics graphics)
    {
        DrawPlayerStatusPanelV2(graphics);
    }

    private Rectangle GetPlayerStatusPanelRect()
    {
        int panelWidth = Math.Min(470, Math.Max(380, ClientSize.Width / 4));
        int panelHeight = 126;
        return new Rectangle(22, ClientSize.Height - panelHeight - 22, panelWidth, panelHeight);
    }

    private void DrawPlayerStatusPanelV2(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null)
        {
            return;
        }

        Rectangle panel = GetPlayerStatusPanelRect();
        Color teamColor = ResolveTeamColor(entity.Team);
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));

        PointF portraitCenter = new(panel.X + 76, panel.Y + 60);
        DrawExperienceArcNeo(graphics, entity, portraitCenter, 58f);
        DrawRobotPortraitNeo(graphics, entity, portraitCenter, 50f, teamColor);

        float rightX = panel.X + 146f;
        float rightWidth = panel.Right - rightX - 18f;
        graphics.DrawString(ResolveHudRoleTitle(entity), _smallHudFont, titleBrush, rightX, panel.Y + 10f);
        DrawTrapezoidHealthBarNeo(graphics, new RectangleF(rightX, panel.Y + 42f, rightWidth, 24f), entity, teamColor);
        DrawBufferEnergyBarNeo(graphics, new RectangleF(rightX, panel.Y + 76f, rightWidth * 0.75f, 16f), entity);
    }

    private void DrawUnitTestScenarioOverlay(Graphics graphics)
    {
        if (!_host.IsUnitTestMode)
        {
            return;
        }

        IReadOnlyList<Simulator3dHost.ScenarioDamageSnapshot> damageSnapshots = _host.GetUnitTestDamageSnapshots();
        Simulator3dHost.UnitTestEnergySnapshot energySnapshot = _host.GetUnitTestEnergySnapshot();
        int panelWidth = 260;
        int panelHeight = 146 + damageSnapshots.Count * 20;
        Rectangle panel = new(18, 78, panelWidth, panelHeight);
        DrawPanel(graphics, panel, alpha: 148);

        using var titleBrush = new SolidBrush(Color.FromArgb(238, 246, 252));
        using var textBrush = new SolidBrush(Color.FromArgb(218, 228, 238));
        using var mutedBrush = new SolidBrush(Color.FromArgb(176, 194, 208));
        graphics.DrawString("单位测试", _smallHudFont, titleBrush, panel.X + 12, panel.Y + 10);
        graphics.DrawString("设施伤害统计", _tinyHudFont, mutedBrush, panel.X + 12, panel.Y + 30);

        float y = panel.Y + 50;
        foreach (Simulator3dHost.ScenarioDamageSnapshot snapshot in damageSnapshots)
        {
            string idleText = double.IsPositiveInfinity(snapshot.SecondsSinceLastHit)
                ? "待命"
                : snapshot.SecondsSinceLastHit >= 3.0
                    ? "已重置"
                    : $"{snapshot.SecondsSinceLastHit:0.0}s";
            graphics.DrawString(
                $"{snapshot.Label}  1秒 {snapshot.LastOneSecondDamage:0}  总计 {snapshot.TotalDamage:0}",
                _tinyHudFont,
                textBrush,
                panel.X + 12,
                y);
            graphics.DrawString(idleText, _tinyHudFont, mutedBrush, panel.Right - 54, y);
            y += 20f;
        }

        y += 4f;
        string energyMode = energySnapshot.LargeMode ? "大能量机关" : "小能量机关";
        graphics.DrawString($"{energyMode}  状态 {energySnapshot.State}", _tinyHudFont, titleBrush, panel.X + 12, y);
        y += 20f;
        graphics.DrawString(
            $"已激活圆盘 {energySnapshot.ActivatedDisks}/5  最近环数 {energySnapshot.LastRingScore}",
            _tinyHudFont,
            textBrush,
            panel.X + 12,
            y);
        y += 20f;
        graphics.DrawString($"平均环数 {energySnapshot.AverageRingScore:0.0}  按 F 可重复激活", _tinyHudFont, mutedBrush, panel.X + 12, y);
        y += 20f;
        graphics.DrawString("1 英雄  2 步兵  3 哨兵", _tinyHudFont, mutedBrush, panel.X + 12, y);
    }

    private void DrawOuterStatusArcsNeo(Graphics graphics, SimulationEntity entity, PointF center, float radius)
    {
        RectangleF rect = new(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        using var leftArc = new Pen(Color.FromArgb(80, 204, 210, 218), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var rightArc = new Pen(Color.FromArgb(80, 204, 210, 218), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(leftArc, rect, 126f, 72f);
        graphics.DrawArc(rightArc, rect, -18f, 72f);

        double projectileSpeed = SimulationCombatMath.ProjectileSpeedMps(entity);
        int allowedShots = ResolveAllowedShotCountNeo(entity);
        using var textBrush = new SolidBrush(Color.FromArgb(220, 232, 236, 242));
        using var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
        string line1 = $"{projectileSpeed:0}m/s";
        string line2 = $"允许发弹量 {allowedShots}";
        PointF textPoint = new(center.X + radius * 0.72f, center.Y + radius * 0.10f);
        graphics.DrawString(line1, _tinyHudFont, shadow, textPoint.X + 1f, textPoint.Y + 1f);
        graphics.DrawString(line1, _tinyHudFont, textBrush, textPoint);
        graphics.DrawString(line2, _tinyHudFont, shadow, textPoint.X + 1f, textPoint.Y + 18f);
        graphics.DrawString(line2, _tinyHudFont, textBrush, textPoint.X, textPoint.Y + 17f);
    }

    private static int ResolveAllowedShotCountNeo(SimulationEntity entity)
    {
        int ammo = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? entity.Ammo42Mm
            : entity.Ammo17Mm;
        return Math.Max(0, ammo);
    }

    private void DrawRobotPortraitNeo(Graphics graphics, SimulationEntity entity, PointF center, float radius, Color teamColor)
    {
        if (_appState == SimulatorAppState.InMatch
            && TryDrawCachedRobotPortraitNeo(graphics, entity, center, radius, teamColor))
        {
            return;
        }

        RectangleF circle = new(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        using var rim = new Pen(Color.FromArgb(224, teamColor), 2.4f);
        graphics.DrawEllipse(rim, circle);

        GraphicsState state = graphics.Save();
        graphics.SetClip(circle);
        DrawFixedRobotSidePortraitNeo(graphics, entity, center, radius, teamColor);
        graphics.Restore(state);

    }

    private bool TryDrawCachedRobotPortraitNeo(Graphics graphics, SimulationEntity entity, PointF center, float radius, Color teamColor)
    {
        int diameter = Math.Max(2, (int)MathF.Ceiling(radius * 2f));
        Size size = new(diameter, diameter);
        string cacheKey = BuildHudPortraitCacheKey(entity, size, teamColor);
        if (_hudPortraitCacheBitmap is null
            || _hudPortraitCacheSize != size
            || !string.Equals(_hudPortraitCacheKey, cacheKey, StringComparison.Ordinal))
        {
            InvalidateHudPortraitCache();
            _hudPortraitCacheBitmap = BuildHudPortraitCacheBitmap(entity, size, teamColor);
            _hudPortraitCacheSize = size;
            _hudPortraitCacheKey = cacheKey;
        }

        if (_hudPortraitCacheBitmap is null)
        {
            return false;
        }

        int x = (int)MathF.Round(center.X - radius);
        int y = (int)MathF.Round(center.Y - radius);
        graphics.DrawImageUnscaled(_hudPortraitCacheBitmap, x, y);
        return true;
    }

    private string BuildHudPortraitCacheKey(SimulationEntity entity, Size size, Color teamColor)
    {
        RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
        return $"{entity.Id}|{entity.Team}|{entity.RoleKey}|{profile.RoleKey}|{profile.ChassisSubtype}|"
            + $"{profile.BodyLengthM:0.000}|{profile.BodyWidthM:0.000}|{profile.BodyHeightM:0.000}|"
            + $"{profile.BodyClearanceM:0.000}|{profile.WheelStyle}|{profile.SuspensionStyle}|"
            + $"{profile.ArmStyle}|{profile.FrontClimbAssistStyle}|{profile.RearClimbAssistStyle}|"
            + $"{teamColor.ToArgb()}|{size.Width}x{size.Height}";
    }

    private Bitmap? BuildHudPortraitCacheBitmap(SimulationEntity entity, Size size, Color teamColor)
    {
        if (size.Width <= 1 || size.Height <= 1)
        {
            return null;
        }

        Bitmap bitmap = new(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using Graphics cacheGraphics = Graphics.FromImage(bitmap);
        cacheGraphics.Clear(Color.Transparent);
        cacheGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        cacheGraphics.CompositingQuality = CompositingQuality.HighQuality;
        cacheGraphics.InterpolationMode = InterpolationMode.Bilinear;
        cacheGraphics.PixelOffsetMode = PixelOffsetMode.Half;
        cacheGraphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        float radius = Math.Min(size.Width, size.Height) * 0.5f;
        PointF center = new(size.Width * 0.5f, size.Height * 0.5f);
        RectangleF circle = new(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        using var rim = new Pen(Color.FromArgb(224, teamColor), 2.4f);
        cacheGraphics.DrawEllipse(rim, RectangleF.Inflate(circle, -1.2f, -1.2f));

        GraphicsState state = cacheGraphics.Save();
        using GraphicsPath circlePath = new();
        circlePath.AddEllipse(circle);
        cacheGraphics.SetClip(circlePath, CombineMode.Replace);
        DrawFixedRobotSidePortraitNeo(cacheGraphics, entity, center, radius, teamColor);
        cacheGraphics.Restore(state);
        cacheGraphics.DrawEllipse(rim, RectangleF.Inflate(circle, -1.2f, -1.2f));
        return bitmap;
    }

    private void InvalidateHudPortraitCache()
    {
        _hudPortraitCacheBitmap?.Dispose();
        _hudPortraitCacheBitmap = null;
        _hudPortraitCacheKey = string.Empty;
        _hudPortraitCacheSize = Size.Empty;
    }

    private void DrawFixedRobotSidePortraitNeo(Graphics graphics, SimulationEntity entity, PointF center, float radius, Color teamColor)
    {
        RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
        GraphicsState state = graphics.Save();
        Rectangle viewport = Rectangle.Ceiling(new RectangleF(center.X - radius, center.Y - radius, radius * 2f, radius * 2f));
        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        bool previousSuppressLabels = _suppressEntityLabels;
        double previousAngle = entity.AngleDeg;
        double previousTurretYaw = entity.TurretYawDeg;
        double previousPitch = entity.GimbalPitchDeg;

        try
        {
            graphics.SetClip(viewport, CombineMode.Intersect);
            using var groundBrush = new SolidBrush(Color.FromArgb(74, 96, 102, 110));
            graphics.FillEllipse(
                groundBrush,
                viewport.X + viewport.Width * 0.18f,
                viewport.Bottom - Math.Max(10f, radius * 0.30f),
                viewport.Width * 0.64f,
                Math.Max(10f, radius * 0.24f));

            _projectionViewportRect = viewport;
            _suppressEntityLabels = true;
            entity.AngleDeg = 34.0;
            entity.TurretYawDeg = 16.0;
            entity.GimbalPitchDeg = -6.0;

            float previewExtent = Math.Max(
                0.45f,
                Math.Max(
                    profile.BodyLengthM + profile.BarrelLengthM * 0.8f,
                    Math.Max(profile.BodyWidthM, profile.GimbalHeightM + profile.BodyClearanceM)));
            _cameraTargetM = new Vector3(0f, Math.Max(0.22f, profile.BodyClearanceM + profile.BodyHeightM * 0.55f), 0f);
            float distance = Math.Clamp(previewExtent * 2.9f, 1.8f, 5.2f);
            _cameraPositionM = _cameraTargetM + new Vector3(distance * 0.86f, distance * 0.52f, distance * 1.08f);
            _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, Vector3.UnitY);
            float aspect = Math.Max(0.6f, viewport.Width / (float)Math.Max(1, viewport.Height));
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(0.86f, aspect, 0.02f, 40f);

            DrawEntityAppearanceModelModern(graphics, entity, Vector3.Zero, profile);
        }
        finally
        {
            entity.AngleDeg = previousAngle;
            entity.TurretYawDeg = previousTurretYaw;
            entity.GimbalPitchDeg = previousPitch;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _suppressEntityLabels = previousSuppressLabels;
            graphics.Restore(state);
        }
    }

    private void DrawExperienceArcNeo(Graphics graphics, SimulationEntity entity, PointF center, float radius)
    {
        double current = ResolveExperienceThresholdNeo(entity.Level);
        double next = ResolveExperienceThresholdNeo(Math.Min(10, Math.Max(2, entity.Level + 1)));
        float ratio = next <= current
            ? 1f
            : (float)Math.Clamp((entity.Experience - current) / (next - current), 0.0, 1.0);
        RectangleF rect = new(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        using var back = new Pen(Color.FromArgb(100, 238, 236, 222), 5.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var front = new Pen(Color.FromArgb(240, 255, 206, 72), 5.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(back, rect, 150f, 240f);
        graphics.DrawArc(front, rect, 150f, 240f * ratio);

        double startRad = 150.0 * Math.PI / 180.0;
        PointF levelAnchor = new(
            center.X + (float)Math.Cos(startRad) * (radius + 12f),
            center.Y + (float)Math.Sin(startRad) * (radius + 12f));
        string levelText = $"等级{Math.Max(1, entity.Level)}";
        using var levelBrush = new SolidBrush(Color.FromArgb(238, 255, 214, 86));
        graphics.DrawString(levelText, _tinyHudFont, levelBrush, levelAnchor.X - 16f, levelAnchor.Y - 9f);
    }

    private static double ResolveExperienceThresholdNeo(int level)
        => Math.Clamp(level, 1, 10) switch
        {
            1 => 0.0,
            2 => 550.0,
            3 => 1100.0,
            4 => 1650.0,
            5 => 2200.0,
            6 => 2750.0,
            7 => 3300.0,
            8 => 3850.0,
            9 => 4400.0,
            _ => 5000.0,
        };

    private void DrawTrapezoidHealthBarNeo(Graphics graphics, RectangleF rect, SimulationEntity entity, Color teamColor)
    {
        float ratio = entity.MaxHealth <= 1e-6 ? 0f : (float)Math.Clamp(entity.Health / entity.MaxHealth, 0.0, 1.0);
        using GraphicsPath outline = CreateRightTaperTrapezoidPathNeo(rect, 10f);
        using var back = new SolidBrush(Color.FromArgb(140, 52, 56, 62));
        graphics.FillPath(back, outline);
        GraphicsState state = graphics.Save();
        graphics.SetClip(outline);
        using var fill = new SolidBrush(Color.FromArgb(230, teamColor));
        graphics.FillRectangle(fill, rect.X, rect.Y, rect.Width * ratio, rect.Height);
        graphics.Restore(state);
        using var border = new Pen(Color.FromArgb(130, 235, 240, 246), 1f);
        graphics.DrawPath(border, outline);
        using StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString($"{entity.Health:0}/{Math.Max(1.0, entity.MaxHealth):0}", _smallHudFont, Brushes.WhiteSmoke, rect, center);
    }

    private static GraphicsPath CreateRightTaperTrapezoidPathNeo(RectangleF rect, float taper)
    {
        GraphicsPath path = new();
        path.AddPolygon(new[]
        {
            new PointF(rect.Left, rect.Top),
            new PointF(rect.Right, rect.Top),
            new PointF(rect.Right - taper, rect.Bottom),
            new PointF(rect.Left, rect.Bottom),
        });
        return path;
    }

    private void DrawBufferEnergyBarNeo(Graphics graphics, RectangleF rect, SimulationEntity entity)
    {
        float ratio = entity.MaxBufferEnergyJ <= 1e-6
            ? 0f
            : (float)Math.Clamp(entity.BufferEnergyJ / entity.MaxBufferEnergyJ, 0.0, 1.0);
        using GraphicsPath outline = CreateRightTaperTrapezoidPathNeo(rect, 8f);
        using var back = new SolidBrush(Color.FromArgb(132, 70, 74, 80));
        using var fill = new SolidBrush(Color.FromArgb(218, 192, 202, 206));
        using var edge = new Pen(Color.FromArgb(130, 220, 226, 230), 1f);
        graphics.FillPath(back, outline);
        GraphicsState state = graphics.Save();
        graphics.SetClip(outline);
        graphics.FillRectangle(fill, rect.X, rect.Y, rect.Width * ratio, rect.Height);
        graphics.Restore(state);
        graphics.DrawPath(edge, outline);
        using StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var text = new SolidBrush(Color.FromArgb(232, 240, 244, 248));
        graphics.DrawString($"{ResolveDisplayedDrivePowerLimitW(entity):0} W", _smallHudFont, text, rect, center);
    }

    private static int ResolveHudRobotIndex(SimulationEntity entity)
    {
        string[] parts = (entity.Id ?? string.Empty).Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[^1], out int index))
        {
            return index;
        }

        return 0;
    }

    private static string ResolveHudRoleTitle(SimulationEntity entity)
        => ResolveHudRoleTitle(entity.RoleKey);

    private static string ResolveHudRoleTitle(string role)
    {
        return (role ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "hero" => "Hero",
            "engineer" => "Engineer",
            "infantry" => "Infantry",
            "sentry" => "Sentry",
            _ => string.IsNullOrWhiteSpace(role) ? "Unknown" : role.Trim(),
        };
    }

    private void DrawHudBuffIconRowNeo(Graphics graphics, SimulationEntity entity, PointF origin, float width)
    {
        List<BuffProgressEntry> entries = CollectBuffProgressEntries(entity).Take(Math.Max(1, (int)(width / 45f))).ToList();
        for (int index = 0; index < entries.Count; index++)
        {
            DrawHudBuffIconNeo(graphics, entries[index], new PointF(origin.X + 18f + index * 45f, origin.Y + 15f));
        }
    }

    private void DrawHudBuffIconNeo(Graphics graphics, BuffProgressEntry entry, PointF center)
    {
        const float radius = 14f;
        RectangleF circle = new(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        using var back = new SolidBrush(Color.FromArgb(205, 38, 32, 16));
        using var ring = new Pen(Color.FromArgb(235, 242, 190, 64), 1.3f);
        graphics.FillEllipse(back, circle);
        graphics.DrawEllipse(ring, circle);
        if (entry.Timed && entry.DurationSec > 1e-3)
        {
            float ratio = (float)Math.Clamp(entry.RemainingSec / entry.DurationSec, 0.0, 1.0);
            using var progress = new Pen(Color.FromArgb(255, 255, 226, 96), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(progress, circle, -90f, ratio * 360f);
        }

        using var pen = new Pen(Color.FromArgb(248, 255, 234, 142), 1.8f);
        using var brush = new SolidBrush(Color.FromArgb(238, 255, 234, 142));
        string key = entry.Key;
        if (key.Contains("damage", StringComparison.OrdinalIgnoreCase))
        {
            graphics.DrawLine(pen, center.X - 6f, center.Y + 6f, center.X + 7f, center.Y - 7f);
            graphics.DrawLine(pen, center.X + 2f, center.Y - 8f, center.X + 8f, center.Y - 8f);
            graphics.DrawLine(pen, center.X + 8f, center.Y - 8f, center.X + 8f, center.Y - 2f);
        }
        else if (key.Contains("cool", StringComparison.OrdinalIgnoreCase))
        {
            graphics.DrawEllipse(pen, center.X - 8f, center.Y - 8f, 16f, 16f);
            graphics.DrawLine(pen, center.X, center.Y, center.X, center.Y - 6f);
            graphics.DrawLine(pen, center.X, center.Y, center.X + 5f, center.Y + 3f);
        }
        else if (key.Contains("power", StringComparison.OrdinalIgnoreCase))
        {
            graphics.DrawRectangle(pen, center.X - 8f, center.Y - 5f, 14f, 10f);
            graphics.FillRectangle(brush, center.X + 7f, center.Y - 2f, 3f, 4f);
            graphics.FillRectangle(brush, center.X - 5f, center.Y - 2f, 8f, 4f);
        }
        else if (key.Contains("weak", StringComparison.OrdinalIgnoreCase))
        {
            PointF[] shield = BuildShieldIconNeo(center, 8f);
            graphics.DrawPolygon(pen, shield);
            graphics.DrawLine(pen, center.X - 5f, center.Y - 6f, center.X + 5f, center.Y + 7f);
        }
        else if (key.Contains("heal", StringComparison.OrdinalIgnoreCase) || key.Contains("recover", StringComparison.OrdinalIgnoreCase))
        {
            graphics.FillRectangle(brush, center.X - 3f, center.Y - 8f, 6f, 16f);
            graphics.FillRectangle(brush, center.X - 8f, center.Y - 3f, 16f, 6f);
        }
        else
        {
            graphics.DrawPolygon(pen, BuildShieldIconNeo(center, 8f));
        }

        string multiplier = ResolveBuffMultiplierLabelNeo(entry);
        if (!string.IsNullOrWhiteSpace(multiplier))
        {
            using var multiplierBack = new SolidBrush(Color.FromArgb(168, 8, 12, 18));
            using var multiplierText = new SolidBrush(Color.FromArgb(248, 255, 245, 210));
            SizeF size = graphics.MeasureString(multiplier, _tinyHudFont);
            RectangleF textRect = new(center.X - size.Width * 0.5f, center.Y + 10f, size.Width + 2f, size.Height);
            graphics.FillRectangle(multiplierBack, textRect);
            graphics.DrawString(multiplier, _tinyHudFont, multiplierText, textRect.X + 1f, textRect.Y);
        }
    }

    private static PointF[] BuildShieldIconNeo(PointF center, float size)
        =>
        [
            new(center.X, center.Y - size),
            new(center.X + size, center.Y - size * 0.45f),
            new(center.X + size * 0.55f, center.Y + size * 0.65f),
            new(center.X, center.Y + size),
            new(center.X - size * 0.55f, center.Y + size * 0.65f),
            new(center.X - size, center.Y - size * 0.45f),
        ];

    private static string ResolveBuffMultiplierLabelNeo(BuffProgressEntry entry)
    {
        int index = entry.Effect.IndexOf('x');
        if (index < 0 || index >= entry.Effect.Length - 1)
        {
            return string.Empty;
        }

        string value = new(entry.Effect.Skip(index + 1).TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        return string.IsNullOrWhiteSpace(value) ? string.Empty : $"x{value}";
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

    private void DrawPowerGauge(Graphics graphics, RectangleF rect, SimulationEntity entity, float ratio, string label)
    {
        DrawMiniGauge(graphics, rect, ratio, Color.FromArgb(75, 146, 232), label);
        RectangleF bufferRect = new(rect.X, rect.Bottom + 2f, rect.Width, 3f);
        float bufferRatio = entity.MaxBufferEnergyJ <= 1e-6
            ? 0f
            : (float)Math.Clamp(entity.BufferEnergyJ / entity.MaxBufferEnergyJ, 0.0, 1.0);
        using (var bufferBack = new SolidBrush(Color.FromArgb(122, 42, 46, 52)))
        using (var bufferFill = new SolidBrush(Color.FromArgb(210, 154, 162, 170)))
        using (var bufferPen = new Pen(Color.FromArgb(160, 186, 192, 198), 1f))
        {
            graphics.FillRectangle(bufferBack, bufferRect);
            graphics.FillRectangle(bufferFill, bufferRect.X, bufferRect.Y, bufferRect.Width * bufferRatio, bufferRect.Height);
            graphics.DrawRectangle(bufferPen, bufferRect.X, bufferRect.Y, bufferRect.Width, bufferRect.Height);
        }

        double displayLimit = Math.Max(1.0, ResolveDisplayedDrivePowerLimitW(entity));
        double overPowerW = Math.Max(0.0, entity.ChassisPowerDrawW - displayLimit);
        if (overPowerW <= 1e-3)
        {
            return;
        }

        float overRatio = (float)Math.Clamp(overPowerW / 300.0, 0.08, 1.0);
        RectangleF overRect = new(rect.Right - rect.Width * overRatio, rect.Y, rect.Width * overRatio, rect.Height);
        using var overFill = new SolidBrush(Color.FromArgb(235, 255, 210, 76));
        using var overPen = new Pen(Color.FromArgb(255, 255, 232, 118), 1f);
        graphics.FillRectangle(overFill, overRect);
        graphics.DrawRectangle(overPen, overRect.X, overRect.Y, overRect.Width, overRect.Height);
    }

    private void DrawHeroDeploymentFeedOverlay(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null
            || !entity.HeroDeploymentActive
            || !string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var blackout = new SolidBrush(Color.FromArgb(0, 2, 5, 8));
        graphics.FillRectangle(blackout, ClientRectangle);

        bool exiting = entity.HeroDeploymentExitHoldTimerSec > 1e-4;
        Rectangle sideBox = CreateRightSideHudCard(366, HudHeight + 172, exiting ? 118 : 98);
        string sideDetail = exiting
            ? "当前为自动吊射。长按 Z 可退出部署。"
            : "部署会强制启用吊射自瞄与自动扳机；非部署时仍可手动用 Q 切换模式。";
        DrawRightSideHudCard(
            graphics,
            sideBox,
            Color.FromArgb(206, 18, 26, 34),
            Color.FromArgb(220, 255, 210, 84),
            "英雄部署",
            sideDetail,
            Color.FromArgb(255, 255, 224, 116),
            Color.FromArgb(232, 232, 240, 248),
            titleTop: 10f,
            detailTop: 36f);
        if (exiting)
        {
            float exitProgress = (float)Math.Clamp(entity.HeroDeploymentExitHoldTimerSec / 2.0, 0.0, 1.0);
            DrawRightSideProgressBar(graphics, new RectangleF(sideBox.X + 14, sideBox.Bottom - 18, sideBox.Width - 28, 8), exitProgress, Color.FromArgb(255, 132, 92));
            DrawCenteredHoldProgress(
                graphics,
                "退出部署",
                "正在切回普通模式",
                exitProgress,
                Color.FromArgb(255, 132, 92));
        }
        return;

        Rectangle box = new(ClientSize.Width / 2 - 230, ClientSize.Height / 2 - (exiting ? 82 : 62), 460, exiting ? 164 : 124);
        using GraphicsPath path = CreateRoundedRectangle(box, 10);
        using var fill = new SolidBrush(Color.FromArgb(218, 18, 26, 34));
        using var border = new Pen(Color.FromArgb(220, 255, 210, 84), 1.4f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var titleBrush = new SolidBrush(Color.FromArgb(255, 255, 224, 116));
        using var textBrush = new SolidBrush(Color.FromArgb(232, 232, 240, 248));
        StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("\u82f1\u96c4\u90e8\u7f72\u6a21\u5f0f", _hudMidFont, titleBrush, new RectangleF(box.X, box.Y + 18, box.Width, 26), center);
        graphics.DrawString("\u7b2c\u4e00\u4eba\u79f0\u753b\u9762\u4e2d\u65ad\uff0c\u81ea\u7784\u4e0e\u81ea\u52a8\u5f00\u706b\u5df2\u542f\u7528\u3002", _smallHudFont, textBrush, new RectangleF(box.X + 18, box.Y + 50, box.Width - 36, 24), center);
        graphics.DrawString("\u76ee\u6807\u4f18\u5148\u7ea7\uff1a\u524d\u54e8\u7ad9\u9876\u90e8 80%\uff0c\u57fa\u5730\u9876\u90e8 50%\u3002\u957f\u6309 Z 2\u79d2\u9000\u51fa\u90e8\u7f72\u3002", _tinyHudFont, textBrush, new RectangleF(box.X + 18, box.Y + 82, box.Width - 36, 22), center);
        if (exiting)
        {
            float progress = (float)Math.Clamp(entity.HeroDeploymentExitHoldTimerSec / 2.0, 0.0, 1.0);
            RectangleF ring = new(box.X + box.Width * 0.5f - 18f, box.Y + 116f, 36f, 36f);
            using var ringBack = new Pen(Color.FromArgb(130, 44, 50, 58), 4f);
            using var ringProgress = new Pen(Color.FromArgb(245, 255, 132, 92), 4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawEllipse(ringBack, ring);
            graphics.DrawArc(ringProgress, ring, -90f, progress * 360f);
            graphics.DrawString("\u9000\u51fa\u90e8\u7f72\u8bfb\u6761", _tinyHudFont, textBrush, new RectangleF(box.X, ring.Bottom + 4f, box.Width, 18f), center);
        }
    }

    private void DrawDeploymentPrompt(Graphics graphics)
    {
        if (_appState != SimulatorAppState.InMatch || _paused)
        {
            return;
        }

        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null
            || !string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || entity.HeroDeploymentActive)
        {
            return;
        }

        bool inDeployZone = _host.MapPreset.Facilities.Any(region =>
            string.Equals(region.Type, "buff_hero_deployment", StringComparison.OrdinalIgnoreCase)
            && string.Equals(region.Team, entity.Team, StringComparison.OrdinalIgnoreCase)
            && region.Contains(entity.X, entity.Y));
        if (!inDeployZone)
        {
            return;
        }

        Rectangle rightBox = CreateRightSideHudCard(336, HudHeight + 284, 74);
        string deployDetail = entity.HeroDeploymentRequested
            ? "保持停留，等 2 秒部署读条完成。"
            : "长按 Z 2 秒进入部署。部署后自动扳机开启，Q 仍可切换普通/吊射。";
        DrawRightSideHudCard(
            graphics,
            rightBox,
            Color.FromArgb(212, 24, 30, 40),
            Color.FromArgb(240, 255, 210, 84),
            "部署区域",
            deployDetail,
            Color.FromArgb(255, 255, 226, 128),
            Color.FromArgb(238, 232, 238, 244),
            titleTop: 8f,
            detailTop: 34f);
        return;

        Rectangle box = new(ClientSize.Width / 2 - 200, ClientSize.Height - 180, 400, 54);
        using GraphicsPath path = CreateRoundedRectangle(box, 8);
        using var fill = new SolidBrush(Color.FromArgb(212, 24, 30, 40));
        using var border = new Pen(Color.FromArgb(240, 255, 210, 84), 1.2f);
        using var titleBrush = new SolidBrush(Color.FromArgb(255, 255, 226, 128));
        using var textBrush = new SolidBrush(Color.FromArgb(238, 232, 238, 244));
        StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString("\u90e8\u7f72\u533a", _smallHudFont, titleBrush, new RectangleF(box.X, box.Y + 6, box.Width, 18), center);
        graphics.DrawString(entity.HeroDeploymentRequested ? "\u6b63\u5728\u8bfb\u6761\uff0c\u8bf7\u5728\u90e8\u7f72\u533a\u5185\u4fdd\u63012\u79d2" : "\u957f\u6309 Z 2\u79d2\u8fdb\u5165\u90e8\u7f72\uff0c\u81ea\u52a8\u653b\u51fb\u9876\u90e8\u88c5\u7532", _tinyHudFont, textBrush, new RectangleF(box.X + 10, box.Y + 26, box.Width - 20, 18), center);
    }

    private void DrawEnergyActivationPrompt(Graphics graphics)
    {
        if (_appState != SimulatorAppState.InMatch || _paused)
        {
            return;
        }

        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null
            || (!string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
            || !_host.World.Teams.TryGetValue(entity.Team, out SimulationTeamState? teamState))
        {
            return;
        }

        bool activatingState = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase);
        bool activatedState = string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase)
            && teamState.EnergyBuffTimerSec > 1e-6;
        bool large = _host.World.GameTimeSec >= 180.0;
        int slot = ResolveEnergyLargeAttemptSlot(_host.World.GameTimeSec);
        bool canActivate = activatingState
            || (!large && !teamState.EnergySmallChanceUsed)
            || (large && slot > 0 && teamState.EnergyLastLargeAttemptSlot != slot);
        if (!canActivate && !activatedState)
        {
            return;
        }

        string sideTitle;
        string sideDetail;
        if (activatedState)
        {
            sideTitle = large ? "Large Energy Active" : "Small Energy Active";
            sideDetail = $"Buff remaining {teamState.EnergyBuffTimerSec:0.0}s.";
        }
        else if (activatingState)
        {
            sideTitle = large ? "Large Energy Activating" : "Small Energy Activating";
            sideDetail = $"Solved {teamState.EnergyActivatedGroupCount}/5 disks, current lit window {Math.Max(0.0, 2.5 - teamState.EnergyLitModuleTimerSec):0.0}s.";
        }
        else
        {
            sideTitle = large ? "Large Energy Ready" : "Small Energy Ready";
            sideDetail = "Press F to start and use Q to switch auto-aim onto the energy disks.";
        }

        Rectangle rightBox = CreateRightSideHudCard(356, HudHeight + 372, 78);
        DrawRightSideHudCard(
            graphics,
            rightBox,
            Color.FromArgb(218, 18, 26, 34),
            Color.FromArgb(230, 255, 184, 86),
            sideTitle,
            sideDetail,
            Color.FromArgb(255, 255, 220, 118),
            Color.FromArgb(238, 232, 238, 244),
            titleTop: 8f,
            detailTop: 34f);
        return;

        Rectangle box = new(ClientSize.Width / 2 - 220, ClientSize.Height - 248, 440, 58);
        using GraphicsPath path = CreateRoundedRectangle(box, 8);
        using var fill = new SolidBrush(Color.FromArgb(218, 18, 26, 34));
        using var border = new Pen(Color.FromArgb(230, 255, 184, 86), 1.2f);
        using var titleBrush = new SolidBrush(Color.FromArgb(255, 255, 220, 118));
        using var textBrush = new SolidBrush(Color.FromArgb(238, 232, 238, 244));
        StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string titleText;
        string detailText;
        if (activatedState)
        {
            titleText = large
                ? "\u5927\u80fd\u91cf\u673a\u5173\u5df2\u6fc0\u6d3b"
                : "\u5c0f\u80fd\u91cf\u673a\u5173\u5df2\u6fc0\u6d3b";
            detailText = $"\u589e\u76ca\u5269\u4f59 {teamState.EnergyBuffTimerSec:0.0}s";
        }
        else if (activatingState)
        {
            titleText = large
                ? "\u5927\u80fd\u91cf\u673a\u5173\u6b63\u5728\u6fc0\u6d3b"
                : "\u5c0f\u80fd\u91cf\u673a\u5173\u6b63\u5728\u6fc0\u6d3b";
            detailText = $"\u6b63\u5728\u6fc0\u6d3b\uff1a{teamState.EnergyActivatedGroupCount}/5  \u5f53\u524d\u76ee\u6807\u5269\u4f59 {Math.Max(0.0, 2.5 - teamState.EnergyLitModuleTimerSec):0.0}s";
        }
        else
        {
            titleText = large
                ? "\u5927\u80fd\u91cf\u673a\u5173\u53ef\u6fc0\u6d3b"
                : "\u5c0f\u80fd\u91cf\u673a\u5173\u53ef\u6fc0\u6d3b";
            detailText = "\u6bd4\u8d5b\u65f6\u95f4\u5141\u8bb8\u65f6\uff0c\u53ef\u76f4\u63a5\u6309 F \u5f00\u542f\uff0cQ \u5207\u6362\u5230\u80fd\u91cf\u5706\u76d8\u81ea\u7784";
        }

        bool activating = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase);
        string title = large ? "大能量机关可激活" : "小能量机关可激活";
        string detail = activating
            ? $"正在激活：{teamState.EnergyActivatedGroupCount}/5  亮灯剩余 {Math.Max(0.0, 2.5 - teamState.EnergyLitModuleTimerSec):0.0}s  Q切能量自瞄"
            : "按住 F 开启，Q 切换到能量机关自瞄";
        graphics.DrawString(titleText, _smallHudFont, titleBrush, new RectangleF(box.X, box.Y + 7, box.Width, 20), center);
        graphics.DrawString(detailText, _tinyHudFont, textBrush, new RectangleF(box.X + 12, box.Y + 31, box.Width - 24, 18), center);
    }
#pragma warning restore CS0162

    private static int ResolveEnergyLargeAttemptSlot(double gameTimeSec)
    {
        if (gameTimeSec >= 330.0)
        {
            return 3;
        }

        if (gameTimeSec >= 245.0)
        {
            return 2;
        }

        return gameTimeSec >= 180.0 ? 1 : 0;
    }

    private static (float Ratio, string Label) ResolvePowerGauge(SimulationEntity entity)
    {
        double displayLimit = ResolveDisplayedDrivePowerLimitW(entity);
        double limit = Math.Max(
            1.0,
            displayLimit);
        double draw = Math.Max(0.0, entity.ChassisPowerDrawW);
        float ratio = (float)Math.Clamp(draw / limit, 0.0, 1.0);
        double overPowerW = Math.Max(0.0, draw - limit);
        string overText = overPowerW > 1e-3 ? $" +{overPowerW:0}" : string.Empty;
        if (entity.PowerCutTimerSec > 1e-6)
        {
            return (0f, $"P CUT {entity.PowerCutTimerSec:0.0}s");
        }

        return (ratio, $"P {draw:0}/{limit:0}{overText}W");
    }

    private static double ResolveDisplayedDrivePowerLimitW(SimulationEntity entity)
    {
        double mechanicalLimitW = Math.Max(1.0, entity.ChassisDrivePowerLimitW);
        double ruleLimitW = entity.RuleDrivePowerLimitW > 1e-6 ? entity.RuleDrivePowerLimitW : mechanicalLimitW;
        double baseLimitW = Math.Min(mechanicalLimitW, Math.Max(1.0, ruleLimitW));
        if (entity.MaxChassisEnergy <= 1e-6)
        {
            return baseLimitW;
        }

        if (entity.ChassisEnergy <= 1e-6)
        {
            return Math.Min(baseLimitW, Math.Max(1.0, entity.ChassisEcoPowerLimitW));
        }

        if (entity.ChassisEnergy >= entity.ChassisBoostThresholdEnergy)
        {
            return Math.Min(200.0, baseLimitW * Math.Max(1.0, entity.ChassisBoostMultiplier));
        }

        return baseLimitW;
    }

    private static (float Ratio, string Label) ResolveEnergyGauge(SimulationEntity entity)
    {
        if (entity.MaxChassisEnergy <= 1e-6)
        {
            return (0f, "\u80fd\u91cf --");
        }

        float ratio = (float)Math.Clamp(entity.ChassisEnergy / entity.MaxChassisEnergy, 0.0, 1.0);
        return (ratio, $"\u80fd\u91cf {entity.ChassisEnergy / 1000.0:0.0}k");
    }

    private static (float Ratio, string Label) ResolveSuperCapGauge(SimulationEntity entity)
    {
        if (entity.MaxSuperCapEnergyJ <= 1e-6)
        {
            return (0f, "\u8d85\u7535 --");
        }

        float ratio = (float)Math.Clamp(entity.SuperCapEnergyJ / entity.MaxSuperCapEnergyJ, 0.0, 1.0);
        if (entity.SuperCapEnabled && entity.SuperCapEnergyJ <= 300.0)
        {
            return (ratio, $"\u8d85\u7535\u4f4e {entity.SuperCapEnergyJ:0}");
        }

        return (ratio, $"\u8d85\u7535 {entity.SuperCapEnergyJ:0}");
    }

    private void DrawTeamHudSection(Graphics graphics, string teamKey, string teamLabel, Rectangle rect)
    {
        Color teamColor = ResolveTeamColor(teamKey);

        bool redSide = string.Equals(teamKey, "red", StringComparison.OrdinalIgnoreCase);
        SimulationEntity? baseEntity = FindEntityById($"{teamKey}_base");
        SimulationEntity? outpostEntity = FindEntityById($"{teamKey}_outpost");
        int outerBarWidth = Math.Min(118, Math.Max(74, rect.Width / 5));
        Rectangle outpostBar = redSide
            ? new Rectangle(rect.X + 4, rect.Y + 9, outerBarWidth, 12)
            : new Rectangle(rect.Right - outerBarWidth - 4, rect.Y + 9, outerBarWidth, 12);
        Rectangle baseBar = redSide
            ? new Rectangle(outpostBar.Right + 7, rect.Y + 7, Math.Max(80, rect.Right - outpostBar.Right - 11), 16)
            : new Rectangle(rect.X + 4, rect.Y + 7, Math.Max(80, outpostBar.Left - rect.X - 11), 16);

        float baseRatio = ResolveHealthRatio(baseEntity);
        float outpostRatio = ResolveHealthRatio(outpostEntity);
        DrawTopHudBar(graphics, baseBar, baseRatio, teamColor, $"{teamLabel} \u57fa\u5730 {(int)Math.Max(0.0, baseEntity?.Health ?? 0)}/{(int)Math.Max(0.0, baseEntity?.MaxHealth ?? 0)}", false);
        DrawTopHudBar(graphics, outpostBar, outpostRatio, teamColor, "\u524d\u54e8\u7ad9", (outpostEntity?.Health ?? 0.0) <= 0.0);

        IReadOnlyList<SimulationEntity> units = BuildTeamHudUnits(teamKey);
        if (units.Count == 0)
        {
            return;
        }

        int unitAreaY = rect.Y + 30;
        int availableWidth = rect.Width - 8;
        int gap = 4;
        int cardWidth = Math.Max(58, (availableWidth - gap * (units.Count - 1)) / Math.Max(1, units.Count));
        for (int index = 0; index < units.Count; index++)
        {
            int logicalIndex = redSide ? index : units.Count - 1 - index;
            SimulationEntity unit = units[logicalIndex];
            Rectangle card = new(rect.X + 4 + index * (cardWidth + gap), unitAreaY, Math.Min(cardWidth, rect.Right - 4 - (rect.X + 4 + index * (cardWidth + gap))), 58);
            DrawTopHudUnitCard(graphics, unit, card, teamColor);
        }
    }

    private void DrawTopHudBar(Graphics graphics, Rectangle rect, float ratio, Color color, string label, bool forceGrey)
    {
        float clamped = Math.Clamp(ratio, 0f, 1f);
        Color fillColor = forceGrey ? Color.FromArgb(132, 112, 118, 126) : color;
        using var back = new SolidBrush(Color.FromArgb(164, 4, 8, 12));
        using var fill = new SolidBrush(Color.FromArgb(forceGrey ? 150 : 224, fillColor));
        using var border = new Pen(Color.FromArgb(170, 184, 194, 206), 1f);
        graphics.FillRectangle(back, rect);
        graphics.FillRectangle(fill, rect.X, rect.Y, rect.Width * clamped, rect.Height);
        graphics.DrawRectangle(border, rect);
        using var text = new SolidBrush(Color.FromArgb(forceGrey ? 178 : 238, 246, 248, 252));
        StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(label, _tinyHudFont, text, rect, center);
    }

    private void DrawTopHudUnitCard(Graphics graphics, SimulationEntity unit, Rectangle card, Color teamColor)
    {
        bool isSelected = string.Equals(_host.SelectedEntity?.Id, unit.Id, StringComparison.OrdinalIgnoreCase);
        using GraphicsPath path = CreateRoundedRectangle(card, 5);
        using var fill = new SolidBrush(Color.FromArgb(unit.IsAlive ? 218 : 172, 10, 15, 22));
        using var border = new Pen(isSelected ? Color.FromArgb(245, 255, 218, 84) : Color.FromArgb(unit.IsAlive ? 168 : 110, teamColor), isSelected ? 1.8f : 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string entityKey = ExtractEntityKey(unit.Id);
        string label = HudUnitLabelMap.TryGetValue(entityKey, out string? mappedLabel) ? mappedLabel : ResolveRoleLabel(unit);
        int ammo = ResolveDisplayedAmmo(unit);
        using var title = new SolidBrush(Color.FromArgb(unit.IsAlive ? 242 : 150, 244, 248, 252));
        using var ammoBrush = new SolidBrush(Color.FromArgb(unit.IsAlive ? 230 : 140, 255, 224, 96));
        string ammoText = ammo.ToString();
        float ammoColumnWidth = Math.Clamp(graphics.MeasureString(ammoText, _tinyHudFont).Width + 8f, 24f, Math.Max(24f, card.Width * 0.36f));
        RectangleF titleRect = new(card.X + 5, card.Y + 3, Math.Max(8f, card.Width - ammoColumnWidth - 11f), 16f);
        RectangleF ammoRect = new(card.Right - ammoColumnWidth - 5f, card.Y + 3, ammoColumnWidth, 16f);
        using StringFormat noWrapLeft = new()
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
        };
        using StringFormat noWrapRight = new()
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Near,
        };
        graphics.DrawString(label, _tinyHudFont, title, titleRect, noWrapLeft);
        graphics.DrawString(ammoText, _tinyHudFont, ammoBrush, ammoRect, noWrapRight);

        Rectangle hpBar = new(card.X + 5, card.Y + 24, Math.Max(8, card.Width - 10), 9);
        float hpRatio = ResolveHealthRatio(unit);
        DrawTopHudBar(graphics, hpBar, hpRatio, unit.IsAlive ? Color.FromArgb(86, 224, 126) : Color.FromArgb(112, 118, 126), string.Empty, !unit.IsAlive);

        string hpText = unit.IsAlive
            ? $"\u8840\u91cf {(int)Math.Ceiling(Math.Max(0.0, unit.Health))}/{(int)Math.Ceiling(Math.Max(0.0, unit.MaxHealth))}"
            : $"\u590d\u6d3b {unit.RespawnTimerSec:0}";
        using var hpBrush = new SolidBrush(Color.FromArgb(unit.IsAlive ? 232 : 158, 222, 232, 242));
        string ammoLabel = string.Equals(unit.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? "42mm" : "17mm";
        using var small = new SolidBrush(Color.FromArgb(188, 204, 214, 226));
        RectangleF caliberRect = new(card.Right - 34f, card.Y + 36, 30f, 16f);
        RectangleF hpTextRect = new(card.X + 5, card.Y + 36, Math.Max(8f, card.Width - 44f), 16f);
        graphics.DrawString(hpText, _tinyHudFont, hpBrush, hpTextRect, noWrapLeft);
        if (card.Width >= 78)
        {
            graphics.DrawString(ammoLabel, _tinyHudFont, small, caliberRect, noWrapRight);
        }
        _uiButtons.Add(new UiButton(card, $"match_select:{unit.Id}"));
    }

    private static float ResolveHealthRatio(SimulationEntity? entity)
    {
        if (entity is null || entity.MaxHealth <= 1e-6)
        {
            return 0f;
        }

        return (float)Math.Clamp(entity.Health / entity.MaxHealth, 0.0, 1.0);
    }

    private static int ResolveDisplayedAmmo(SimulationEntity entity)
        => string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? entity.Ammo42Mm
            : entity.Ammo17Mm;

    private void DrawTeamHudSectionLegacy(Graphics graphics, string teamKey, string teamLabel, Rectangle rect)
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
            string hpText = unit.IsAlive ? $"{(int)unit.Health}" : $"R {unit.RespawnTimerSec:0}";
            string levelText = $"Lv{Math.Max(1, unit.Level)}";
            string nodeText = FormatDecisionLabelShort(unit.AiDecisionSelected, unit.AiDecision);

            graphics.DrawString(label, _tinyHudFont, Brushes.White, card.X + 6, card.Y + 1);
            using (var hpBrush = new SolidBrush(unit.IsAlive ? Color.FromArgb(218, 182, 81) : Color.FromArgb(128, 128, 128)))
            {
                graphics.DrawString(hpText, _tinyHudFont, hpBrush, card.X + 6, card.Y + 12);
            }

            SizeF lvSize = graphics.MeasureString(levelText, _tinyHudFont);
            graphics.DrawString(levelText, _tinyHudFont, Brushes.White, card.Right - lvSize.Width - 6, card.Y + 12);
            if (!unit.IsAlive)
            {
                graphics.DrawString($"Resp {unit.RespawnTimerSec:0.0}s", _tinyHudFont, Brushes.Gainsboro, card.X + 6, card.Y + 22);
            }
            else
            {
                graphics.DrawString(nodeText, _tinyHudFont, Brushes.White, card.X + 6, card.Y + 22);
            }

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
        graphics.DrawString("测试模式会替换世界布置、设施目标与可控单位。", _tinyHudFont, textBrush, panel.X + 16, y);
        y += 24;

        graphics.DrawString("对局模式", _smallHudFont, textBrush, panel.X + 16, y);
        y += 26;
        Rectangle fullMode = new(panel.X + 16, (int)y, 72, 28);
        Rectangle singleMode = new(panel.X + 94, (int)y, 72, 28);
        Rectangle duelMode = new(panel.X + 172, (int)y, 72, 28);
        Rectangle unitTestMode = new(panel.X + 16, (int)y + 34, 96, 28);
        DrawButton(graphics, fullMode, "完整", "lobby_mode:full", !_host.IsFocusSandboxMode, Color.FromArgb(64, 108, 176));
        DrawButton(graphics, singleMode, "单兵", "lobby_mode:single_unit_test", _host.IsSingleUnitTestMode, Color.FromArgb(108, 94, 188));
        DrawButton(graphics, duelMode, "1v1", "lobby_mode:duel_1v1", _host.IsDuelMode, Color.FromArgb(164, 92, 88));
        DrawButton(graphics, unitTestMode, "单位测试", "lobby_mode:unit_test", _host.IsUnitTestMode, Color.FromArgb(92, 156, 118));
        y += 68;

        if (_host.IsFocusSandboxMode)
        {
            if (_host.IsSingleUnitTestMode)
            {
                graphics.DrawString("主控队伍", _smallHudFont, textBrush, panel.X + 16, y);
                y += 26;
                Rectangle redTeam = new(panel.X + 16, (int)y, 72, 26);
                Rectangle blueTeam = new(panel.X + 96, (int)y, 72, 26);
                DrawButton(graphics, redTeam, "红方", "lobby_team:red", string.Equals(_host.SingleUnitTestTeam, "red", StringComparison.OrdinalIgnoreCase), Color.FromArgb(174, 66, 66));
                DrawButton(graphics, blueTeam, "蓝方", "lobby_team:blue", string.Equals(_host.SingleUnitTestTeam, "blue", StringComparison.OrdinalIgnoreCase), Color.FromArgb(64, 112, 200));
                y += 36;
            }
            else if (_host.IsDuelMode)
            {
                graphics.DrawString("1v1 模式固定红方主控，蓝方哨兵自动攻击。", _tinyHudFont, textBrush, panel.X + 16, y);
                y += 28;
            }
            else if (_host.IsUnitTestMode)
            {
                graphics.DrawString("单位测试固定红方主控，目标设施按 120° 平面布置。", _tinyHudFont, textBrush, panel.X + 16, y);
                y += 28;
            }

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
            string focusHint = _host.IsUnitTestMode
                ? "局内显示设施伤害统计与能量机关状态。"
                : _host.IsDuelMode
                    ? "局内仅保留主控与蓝方哨兵，哨兵会持续追击主控。"
                    : "当前决策与待办决策见左侧决策栏。";
            graphics.DrawString(focusHint, _tinyHudFont, textBrush, panel.X + 16, y);
            y += 22;
        }
        else
        {
            graphics.DrawString("当前不注入人工待办决策。", _tinyHudFont, textBrush, panel.X + 16, y);
            y += 18;
            graphics.DrawString("若要查看单兵种决策，请切换到单兵种测试。", _tinyHudFont, textBrush, panel.X + 16, y);
            y += 22;
        }

        graphics.DrawString("运行控制", _smallHudFont, textBrush, panel.X + 16, y);
        y += 26;
        Rectangle pauseRect = new(panel.X + 16, (int)y, panel.Width - 32, 28);
        DrawButton(graphics, pauseRect, _paused ? "继续对局" : "暂停对局", "match_toggle_pause", _paused, Color.FromArgb(60, 130, 205));
        y += 34;
        Rectangle resetRect = new(panel.X + 16, (int)y, panel.Width - 32, 28);
        DrawButton(graphics, resetRect, "重新开始", "match_reset_world", false, Color.FromArgb(86, 98, 112));
        y += 34;
        Rectangle lobbyRect = new(panel.X + 16, (int)y, panel.Width - 32, 28);
        DrawButton(graphics, lobbyRect, "返回大厅", "match_return_lobby", false, Color.FromArgb(86, 98, 112));
        y += 34;
        Rectangle reloadRect = new(panel.X + 16, (int)y, panel.Width - 32, 28);
        DrawButton(graphics, reloadRect, "F6 重新加载部署", "match_reload_deployment", false, Color.FromArgb(74, 100, 156));
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
        graphics.DrawString("决策面板", _titleFont, textBrush, panel.X + 16, y);
        y += 30;

        SimulationEntity? focus = _host.SingleUnitTestFocusEntity;
        string currentDecision = focus?.AiDecisionSelected ?? string.Empty;
        string forcedDecision = focus?.TestForcedDecisionId ?? string.Empty;
        string summaryDecision = FormatDecisionLabelShort(focus?.AiDecisionSelected ?? string.Empty, focus?.AiDecision ?? string.Empty);

        graphics.DrawString("当前决策", _smallHudFont, textBrush, panel.X + 16, y);
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
            graphics.DrawString("当前无法推断下一步候选。", _tinyHudFont, textBrush, panel.X + 16, y);
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
            return "待命";
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

    private bool CanExecuteUiActionForCurrentState(string action)
    {
        if (_appState != SimulatorAppState.InMatch || IsMatchStartupActive)
        {
            return true;
        }

        if (string.Equals(action, "match_reset_world", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "match_return_lobby", StringComparison.OrdinalIgnoreCase))
        {
            return _paused;
        }

        return true;
    }

    private void ExecuteUiAction(string action)
    {
        if (!CanExecuteUiActionForCurrentState(action))
        {
            return;
        }

        if (_lobbyDuelRoundInputFocused
            && !string.Equals(action, "lobby_duel_rounds_focus", StringComparison.OrdinalIgnoreCase))
        {
            CommitLobbyDuelRoundInput();
            _lobbyDuelRoundInputFocused = false;
        }

        if (action.StartsWith("menu_backend:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetRendererMode(action.Split(':', 2)[1]);
            ApplyRendererControlStyles();
            return;
        }

        if (action.StartsWith("menu_map_select:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetMapPreset(action.Split(':', 2)[1]);
            ResetCameraForMap();
            PreloadActiveMapTerrainAssets();
            return;
        }

        if (action.StartsWith("lobby_mode:", StringComparison.OrdinalIgnoreCase))
        {
            string role = ResolveLobbySelectedRoleKey();
            if (_host.SetMatchMode(action.Split(':', 2)[1]))
            {
                ResetCameraForMap();
                PreloadActiveMapTerrainAssets();
                SelectLobbyRole(role);
                SyncLobbyDuelRoundInputFromHost();
            }

            return;
        }

        if (action.StartsWith("lobby_team:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSelectedTeam(action.Split(':', 2)[1]);
            SelectLobbyRole(ResolveLobbySelectedRoleKey());
            return;
        }

        if (string.Equals(action, "lobby_map_prev", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.IsSingleUnitTestMode || _host.IsUnitTestMode)
            {
                return;
            }

            _host.CycleMapPreset(-1);
            ResetCameraForMap();
            PreloadActiveMapTerrainAssets();
            SelectLobbyRole(ResolveLobbySelectedRoleKey());
            return;
        }

        if (string.Equals(action, "lobby_map_next", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.IsSingleUnitTestMode || _host.IsUnitTestMode)
            {
                return;
            }

            _host.CycleMapPreset(1);
            ResetCameraForMap();
            PreloadActiveMapTerrainAssets();
            SelectLobbyRole(ResolveLobbySelectedRoleKey());
            return;
        }

        if (action.StartsWith("lobby_infantry_mode:", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.SetInfantryMode(action.Split(':', 2)[1], rebuildWorld: false))
            {
                SelectLobbyRole("infantry");
                QueueLobbyWorldRebuild("步兵形态更新中");
            }
            return;
        }

        if (action.StartsWith("lobby_hero_mode:", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.SetHeroPerformanceMode(action.Split(':', 2)[1], rebuildWorld: false))
            {
                SelectLobbyRole("hero");
                QueueLobbyWorldRebuild("英雄配置更新中");
            }
            return;
        }

        if (action.StartsWith("lobby_infantry_durability:", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.SetInfantryDurabilityMode(action.Split(':', 2)[1], rebuildWorld: false))
            {
                SelectLobbyRole("infantry");
                QueueLobbyWorldRebuild("步兵配置更新中");
            }
            return;
        }

        if (action.StartsWith("lobby_infantry_weapon:", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.SetInfantryWeaponMode(action.Split(':', 2)[1], rebuildWorld: false))
            {
                SelectLobbyRole("infantry");
                QueueLobbyWorldRebuild("步兵配置更新中");
            }
            return;
        }

        if (action.StartsWith("lobby_sentry_control:", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.SetSentryControlMode(action.Split(':', 2)[1], rebuildWorld: false))
            {
                SelectLobbyRole("sentry");
                QueueLobbyWorldRebuild("哨兵配置更新中");
            }
            return;
        }

        if (action.StartsWith("lobby_sentry_stance:", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.SetSentryStance(action.Split(':', 2)[1], rebuildWorld: false))
            {
                SelectLobbyRole("sentry");
                QueueLobbyWorldRebuild("哨兵姿态更新中");
            }
            return;
        }

        if (action.StartsWith("lobby_projectile_render:", StringComparison.OrdinalIgnoreCase))
        {
            string mode = action.Split(':', 2)[1];
            _host.SetSolidProjectileRendering(!string.Equals(mode, "flat", StringComparison.OrdinalIgnoreCase));
            return;
        }

        if (action.StartsWith("lobby_ai:", StringComparison.OrdinalIgnoreCase))
        {
            string mode = action.Split(':', 2)[1];
            _host.SetAiEnabled(string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase));
            return;
        }

        if (action.StartsWith("lobby_projectile_physics:", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetProjectilePhysicsBackend(action.Split(':', 2)[1]);
            return;
        }

        if (action.StartsWith("lobby_unit_test_energy:", StringComparison.OrdinalIgnoreCase))
        {
            string mode = action.Split(':', 2)[1];
            _host.SetUnitTestEnergyForceLarge(string.Equals(mode, "large", StringComparison.OrdinalIgnoreCase));
            return;
        }

        if (action.StartsWith("lobby_duel_rounds:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(action.Split(':', 2)[1], out int roundLimit))
            {
                _host.SetDuelRoundLimit(roundLimit);
            }

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

        if (action.StartsWith("tactical_mode:", StringComparison.OrdinalIgnoreCase))
        {
            _tacticalCommandMode = action.Split(':', 2)[1].ToLowerInvariant() switch
            {
                "defend" => TacticalCommandMode.Defend,
                "patrol" => TacticalCommandMode.Patrol,
                _ => TacticalCommandMode.Attack,
            };
            return;
        }

        if (action.StartsWith("tactical_timescale:", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(action.Split(':', 2)[1], out double timeScale))
        {
            _simulationTimeScale = Math.Clamp(timeScale, 0.1, 2.0);
            return;
        }

        if (string.Equals(action, "match_clear_decision", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetSingleUnitTestDecision(string.Empty);
            return;
        }

        switch (action)
        {
            case "lobby_duel_rounds_focus":
                _lobbyDuelRoundInputFocused = true;
                if (string.IsNullOrWhiteSpace(_lobbyDuelRoundInputText))
                {
                    _lobbyDuelRoundInputText = _host.DuelRoundLimit.ToString();
                }
                break;
            case "main_menu_toggle_start":
                ToggleMainMenuStartSection();
                break;
            case "main_menu_toggle_editor":
                ToggleMainMenuEditorSection();
                break;
            case "menu_open_lobby":
                _host.SetMatchMode("full");
                EnterLobby();
                break;
            case "menu_open_lobby_full":
                EnterLobbyFromMainMenu("full");
                break;
            case "menu_open_lobby_duel":
                EnterLobbyFromMainMenu("duel_1v1");
                break;
            case "menu_open_lobby_unit_test":
                EnterLobbyFromMainMenu("unit_test");
                break;
            case "menu_open_map_component_test":
                OpenMapComponentTestWindow();
                break;
            case "menu_open_appearance_editor":
                OpenEditorDialog(new AppearanceEditorForm());
                break;
            case "menu_open_terrain_editor":
                LoadLargeTerrainInProcessLauncher.OpenTerrainEditorAsync(_host.ActiveMapPreset);
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
            case "match_open_drive_telemetry":
                ToggleDriveTelemetryWindow();
                break;
            case "match_toggle_debug_sidebars":
                break;
            case "match_toggle_tactical":
                ToggleTacticalMode();
                break;
            case "tactical_apply":
                ApplyCurrentTacticalCommand();
                break;
            case "match_toggle_pause":
                SetPaused(!_paused);
                break;
            case "match_reset_world":
                BeginMatchStartupSequence(resetWorld: true);
                break;
            case "match_return_lobby":
                ReturnToLobby();
                break;
        }
    }

    private void OpenMapComponentTestWindow()
    {
        LoadLargeTerrainInProcessLauncher.OpenMapComponentTestAsync(_host.ActiveMapPreset);
    }

    private void OpenLegacyMapComponentRuntimeWindow()
    {
        var form = new Simulator3dForm(new Simulator3dOptions
        {
            MapPreset = _host.ActiveMapPreset,
            MatchMode = "map_component_test",
            StartInMatch = true,
            SelectedTeam = _host.SelectedTeam,
            RendererMode = _host.ActiveRendererMode,
        });
        form.Show(this);
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

    private void HandleTacticalCanvasClick(Point point)
    {
        if (TryPickTacticalFriendlyUnit(point, out SimulationEntity? selectedUnit))
        {
            _host.SetSelectedEntity(selectedUnit!.Id);
            _tacticalAttackTargetId = selectedUnit.TacticalTargetId;
            return;
        }

        if (_tacticalCommandMode == TacticalCommandMode.Attack)
        {
            if (TryPickTacticalTarget(point, out SimulationEntity? target))
            {
                _tacticalAttackTargetId = target!.Id;
                ApplyCurrentTacticalCommand();
            }
        }
        else if (TryPickGroundWorld(point, out double worldX, out double worldY))
        {
            _tacticalGroundTargetX = worldX;
            _tacticalGroundTargetY = worldY;
            ApplyCurrentTacticalCommand();
        }
    }

    private void ApplyCurrentTacticalCommand()
    {
        string command = _tacticalCommandMode switch
        {
            TacticalCommandMode.Defend => "defend",
            TacticalCommandMode.Patrol => "patrol",
            _ => "attack",
        };
        string? targetId = _tacticalCommandMode == TacticalCommandMode.Attack
            ? _tacticalAttackTargetId ?? ResolveDefaultTacticalTargetId()
            : null;
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is null
            || !string.Equals(selected.Team, _host.SelectedTeam, StringComparison.OrdinalIgnoreCase)
            || !_host.SetEntityTacticalCommand(
                selected.Id,
                command,
                targetId,
                _tacticalGroundTargetX,
                _tacticalGroundTargetY,
                _tacticalPatrolRadiusWorld))
        {
            _host.SetTeamTacticalCommand(
                _host.SelectedTeam,
                command,
                targetId,
                _tacticalGroundTargetX,
                _tacticalGroundTargetY,
                _tacticalPatrolRadiusWorld);
        }
    }

    private bool TryPickTacticalFriendlyUnit(Point screenPoint, out SimulationEntity? unit)
    {
        unit = null;
        float bestDistanceSq = 24f * 24f;
        foreach (SimulationEntity candidate in _host.GetControlCandidates(_host.SelectedTeam))
        {
            if (!candidate.IsAlive || candidate.IsSimulationSuppressed)
            {
                continue;
            }

            PointF projected;
            if (UseFastFlatRenderer)
            {
                if (!TryProjectFlatWorld(candidate.X, candidate.Y, out projected))
                {
                    continue;
                }
            }
            else
            {
                Vector3 center = ToScenePoint(
                    candidate.X,
                    candidate.Y,
                    (float)(candidate.GroundHeightM + candidate.AirborneHeightM + Math.Max(0.35, candidate.BodyHeightM * 0.6)));
                if (!TryProject(center, out projected, out _))
                {
                    continue;
                }
            }

            float dx = projected.X - screenPoint.X;
            float dy = projected.Y - screenPoint.Y;
            float distanceSq = dx * dx + dy * dy;
            if (distanceSq >= bestDistanceSq)
            {
                continue;
            }

            bestDistanceSq = distanceSq;
            unit = candidate;
        }

        return unit is not null;
    }

    private void ApplyCurrentTacticalCommandToTeam()
    {
        string command = _tacticalCommandMode switch
        {
            TacticalCommandMode.Defend => "defend",
            TacticalCommandMode.Patrol => "patrol",
            _ => "attack",
        };
        string? targetId = _tacticalCommandMode == TacticalCommandMode.Attack
            ? _tacticalAttackTargetId ?? ResolveDefaultTacticalTargetId()
            : null;
        _host.SetTeamTacticalCommand(
            _host.SelectedTeam,
            command,
            targetId,
            _tacticalGroundTargetX,
            _tacticalGroundTargetY,
            _tacticalPatrolRadiusWorld);
    }

    private string? ResolveDefaultTacticalTargetId()
        => _host.GetTacticalTargets(_host.SelectedTeam).FirstOrDefault()?.Id;

    private bool TryPickTacticalTarget(Point screenPoint, out SimulationEntity? target)
    {
        target = null;
        float bestDistanceSq = 26f * 26f;
        foreach (SimulationEntity candidate in _host.GetTacticalTargets(_host.SelectedTeam))
        {
            PointF projected;
            if (UseFastFlatRenderer)
            {
                if (!TryProjectFlatWorld(candidate.X, candidate.Y, out projected))
                {
                    continue;
                }
            }
            else
            {
                Vector3 center = ToScenePoint(
                    candidate.X,
                    candidate.Y,
                    (float)(candidate.GroundHeightM + candidate.AirborneHeightM + Math.Max(0.35, candidate.BodyHeightM * 0.6)));
                if (!TryProject(center, out projected, out _))
                {
                    continue;
                }
            }

            float dx = projected.X - screenPoint.X;
            float dy = projected.Y - screenPoint.Y;
            float distanceSq = dx * dx + dy * dy;
            if (distanceSq >= bestDistanceSq)
            {
                continue;
            }

            bestDistanceSq = distanceSq;
            target = candidate;
        }

        return target is not null;
    }

    private bool TryPickGroundWorld(Point screenPoint, out double worldX, out double worldY)
    {
        worldX = 0.0;
        worldY = 0.0;
        if (UseFastFlatRenderer && _fastRendererMapRect.Width > 0 && _fastRendererMapRect.Height > 0)
        {
            if (!_fastRendererMapRect.Contains(screenPoint))
            {
                return false;
            }

            double normalizedX = (screenPoint.X - _fastRendererMapRect.X) / (double)Math.Max(1, _fastRendererMapRect.Width);
            double normalizedY = (screenPoint.Y - _fastRendererMapRect.Y) / (double)Math.Max(1, _fastRendererMapRect.Height);
            worldX = Math.Clamp(normalizedX * Math.Max(0, _host.MapPreset.Width), 0.0, Math.Max(0.0, _host.MapPreset.Width));
            worldY = Math.Clamp(normalizedY * Math.Max(0, _host.MapPreset.Height), 0.0, Math.Max(0.0, _host.MapPreset.Height));
            return true;
        }

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return false;
        }

        Matrix4x4 viewProjection = Matrix4x4.Multiply(_viewMatrix, _projectionMatrix);
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
        {
            return false;
        }

        float ndcX = screenPoint.X / (float)Math.Max(1, ClientSize.Width) * 2f - 1f;
        float ndcY = 1f - screenPoint.Y / (float)Math.Max(1, ClientSize.Height) * 2f;
        Vector4 nearClip = new(ndcX, ndcY, 0f, 1f);
        Vector4 farClip = new(ndcX, ndcY, 1f, 1f);
        Vector4 nearWorld4 = Vector4.Transform(nearClip, inverse);
        Vector4 farWorld4 = Vector4.Transform(farClip, inverse);
        if (Math.Abs(nearWorld4.W) <= 1e-6f || Math.Abs(farWorld4.W) <= 1e-6f)
        {
            return false;
        }

        Vector3 nearWorld = new(nearWorld4.X / nearWorld4.W, nearWorld4.Y / nearWorld4.W, nearWorld4.Z / nearWorld4.W);
        Vector3 farWorld = new(farWorld4.X / farWorld4.W, farWorld4.Y / farWorld4.W, farWorld4.Z / farWorld4.W);
        Vector3 ray = farWorld - nearWorld;
        if (Math.Abs(ray.Y) <= 1e-6f)
        {
            return false;
        }

        float t = -nearWorld.Y / ray.Y;
        if (t < 0f)
        {
            return false;
        }

        Vector3 hit = nearWorld + ray * t;
        double scale = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        worldX = Math.Clamp(hit.X / scale, 0.0, Math.Max(0.0, _host.MapPreset.Width));
        worldY = Math.Clamp(hit.Z / scale, 0.0, Math.Max(0.0, _host.MapPreset.Height));
        return true;
    }

    private void StartMatch()
    {
        if (_lobbyWorldRebuildTask is not null)
        {
            _startMatchAfterLobbyWorldRebuild = true;
            return;
        }

        BeginMatchStartupSequence(resetWorld: true);
    }

    private void BeginMatchStartupSequence(bool resetWorld)
    {
        _paused = true;
        _followSelection = !_firstPersonView;
        _appState = SimulatorAppState.InMatch;
        _matchStartupPhase = MatchStartupPhase.Loading;
        _matchStartupPhaseStartTicks = _frameClock.ElapsedTicks;
        _lastMatchStartupLogTicks = 0;
        _matchStartupViewReady = false;
        _simulationAccumulatorSec = 0.0;
        _lastFrameClockTicks = _frameClock.ElapsedTicks;
        _hasPresentedGpuFrame = false;
        _matchNoGcRegionAttempted = false;
        InvalidateHudPortraitCache();
        ResetLiveInput();
        ResetCameraForMap();
        if (resetWorld)
        {
            _host.PrepareScenarioMatchStart();
        }

        _matchStartupPrepareTask = resetWorld ? _host.PrepareMatchWorldAsync() : null;
        if (!resetWorld)
        {
            _host.World.GameTimeSec = 0.0;
            PreloadActiveMapTerrainAssets();
            SnapCameraToSelectedEntity();
        }

        ReleaseMouseCapture();
        InvalidateGpuOverlayLayer();
        LogMatchStartupState(resetWorld ? "loading_started_async_reset" : "loading_started");
        UpdateMouseCaptureState();
    }

    private void RestartMatchImmediately()
    {
        long nowTicks = _frameClock.ElapsedTicks;
        _host.SetMatchMode("full");
        _host.ResetWorld();
        _host.World.GameTimeSec = 0.0;
        _simulationAccumulatorSec = 0.0;
        _lastFrameClockTicks = nowTicks;
        _matchStartupPhase = MatchStartupPhase.Live;
        _matchStartupPhaseStartTicks = nowTicks;
        _matchStartupViewReady = true;
        _paused = false;
        _appState = SimulatorAppState.InMatch;
        InvalidateHudPortraitCache();
        ResetLiveInput();
        ResetCameraForMap();
        PreloadActiveMapTerrainAssets();
        SnapCameraToSelectedEntity();
        InvalidateGpuOverlayLayer();
        UpdateMouseCaptureState();
        LogMatchStartupState("match_restarted_live");
    }

    private void MarkMatchStartupViewReady()
    {
        if (_matchStartupPhase != MatchStartupPhase.Loading)
        {
            return;
        }

        if (_host.SelectedEntity is null)
        {
            return;
        }

        if (UseGpuRenderer && !UseFastFlatRenderer && !_hasPresentedGpuFrame && !_gpuContextFailed)
        {
            return;
        }

        if (!_matchStartupViewReady)
        {
            _matchStartupViewReady = true;
            LogMatchStartupState("view_ready");
        }
    }

    private void UpdateMatchStartupState(long nowTicks)
    {
        if (!TryFinalizePendingMatchStartupPreparation(nowTicks))
        {
            return;
        }

        if (_matchStartupPhase == MatchStartupPhase.Loading)
        {
            bool terrainReady = IsActiveMapTerrainFullyLoaded();
            LogMatchStartupProgressIfDue(nowTicks, terrainReady);
            if (_matchStartupViewReady && terrainReady)
            {
                _matchStartupPhase = MatchStartupPhase.Countdown;
                _matchStartupPhaseStartTicks = nowTicks;
                _host.World.GameTimeSec = 0.0;
                _simulationAccumulatorSec = 0.0;
                _lastFrameClockTicks = nowTicks;
                InvalidateGpuOverlayLayer();
                LogMatchStartupState("countdown_started");
            }

            return;
        }

        if (_matchStartupPhase != MatchStartupPhase.Countdown)
        {
            return;
        }

        double elapsedSec = (nowTicks - _matchStartupPhaseStartTicks) / (double)Stopwatch.Frequency;
        if (elapsedSec < MatchStartupCountdownSec)
        {
            return;
        }

        _matchStartupPhase = MatchStartupPhase.Live;
        _matchStartupPhaseStartTicks = nowTicks;
        _host.World.GameTimeSec = 0.0;
        _simulationAccumulatorSec = 0.0;
        _lastFrameClockTicks = nowTicks;
        _paused = false;
        ResetLiveInput();
        InvalidateGpuOverlayLayer();
        UpdateMouseCaptureState();
        LogMatchStartupState("match_live");
    }

    private bool TryFinalizePendingMatchStartupPreparation(long nowTicks)
    {
        if (_matchStartupPrepareTask is null)
        {
            return true;
        }

        if (!_matchStartupPrepareTask.IsCompleted)
        {
            return false;
        }

        try
        {
            _host.ApplyPreparedMatchWorld(_matchStartupPrepareTask.Result);
        }
        catch (Exception exception)
        {
            _matchStartupPrepareTask = null;
            LogMatchStartupState($"async_reset_failed {exception.GetType().Name}:{exception.Message}");
            _host.ResetWorld();
        }

        _matchStartupPrepareTask = null;
        _host.World.GameTimeSec = 0.0;
        _simulationAccumulatorSec = 0.0;
        _lastFrameClockTicks = nowTicks;
        PreloadActiveMapTerrainAssets();
        SnapCameraToSelectedEntity();
        InvalidateHudPortraitCache();
        InvalidateGpuOverlayLayer();
        LogMatchStartupState("async_reset_ready");
        return true;
    }

    private void LogMatchStartupProgressIfDue(long nowTicks, bool terrainReady)
    {
        if (_lastMatchStartupLogTicks > 0
            && (nowTicks - _lastMatchStartupLogTicks) / (double)Stopwatch.Frequency < 1.0)
        {
            return;
        }

        _lastMatchStartupLogTicks = nowTicks;
        LogMatchStartupState(
            $"loading_progress terrain_ready={terrainReady} view_ready={_matchStartupViewReady} world_ready={_matchStartupPrepareTask is null} progress={ResolveMatchStartupLoadProgress():0.000}");
    }

    private void LogMatchStartupState(string message)
    {
        SimulatorRuntimeLog.Append(
            "match_startup.log",
            $"{DateTime.Now:HH:mm:ss.fff} phase={_matchStartupPhase} paused={_paused} {message}");
    }

    private void UpdateMatchGcControl()
    {
        bool shouldUseNoGcRegion =
            _appState == SimulatorAppState.InMatch
            && !_paused
            && !IsMatchStartupActive
            && !_previewOnly;
        if (shouldUseNoGcRegion)
        {
            EnterMatchGcControl();
        }
        else
        {
            ExitMatchGcControl();
        }
    }

    private void EnterMatchGcControl()
    {
        if (_matchNoGcRegionActive || _matchNoGcRegionAttempted)
        {
            return;
        }

        _matchNoGcRegionAttempted = true;
        _previousGcLatencyMode = GCSettings.LatencyMode;
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            _matchNoGcRegionActive = GC.TryStartNoGCRegion(
                256L * 1024L * 1024L,
                disallowFullBlockingGC: true);
            AppendGameplayLog(
                "gc_control.log",
                $"{DateTime.Now:HH:mm:ss.fff} enter sustained_low_latency no_gc_region={_matchNoGcRegionActive}");
        }
        catch (Exception exception)
        {
            _matchNoGcRegionActive = false;
            AppendGameplayLog(
                "gc_control.log",
                $"{DateTime.Now:HH:mm:ss.fff} enter_failed {exception.GetType().Name}:{exception.Message}");
        }
    }

    private void ExitMatchGcControl()
    {
        if (!_matchNoGcRegionActive
            && !_matchNoGcRegionAttempted
            && GCSettings.LatencyMode == _previousGcLatencyMode)
        {
            return;
        }

        bool noGcRegionStillActive = false;
        try
        {
            if (_matchNoGcRegionActive)
            {
                GC.EndNoGCRegion();
            }
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("must be set", StringComparison.OrdinalIgnoreCase))
        {
            AppendGameplayLog(
                "gc_control.log",
                $"{DateTime.Now:HH:mm:ss.fff} end_no_gc_skipped {exception.GetType().Name}:{exception.Message}");
        }
        catch (Exception exception)
        {
            noGcRegionStillActive =
                exception is InvalidOperationException invalidOperationException
                && invalidOperationException.Message.Contains("in progress", StringComparison.OrdinalIgnoreCase);
            AppendGameplayLog(
                "gc_control.log",
                $"{DateTime.Now:HH:mm:ss.fff} end_no_gc_failed {exception.GetType().Name}:{exception.Message}");
        }

        if (noGcRegionStillActive)
        {
            _matchNoGcRegionActive = true;
            return;
        }

        try
        {
            if (GCSettings.LatencyMode != _previousGcLatencyMode)
            {
                GCSettings.LatencyMode = _previousGcLatencyMode;
            }
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("NoGCRegion mode is in progress", StringComparison.OrdinalIgnoreCase))
        {
            _matchNoGcRegionActive = true;
            AppendGameplayLog(
                "gc_control.log",
                $"{DateTime.Now:HH:mm:ss.fff} restore_latency_deferred {exception.GetType().Name}:{exception.Message}");
            return;
        }
        catch (Exception exception)
        {
            AppendGameplayLog(
                "gc_control.log",
                $"{DateTime.Now:HH:mm:ss.fff} restore_latency_failed {exception.GetType().Name}:{exception.Message}");
            return;
        }

        _matchNoGcRegionActive = false;
        _matchNoGcRegionAttempted = false;
    }

    private void SetPaused(bool paused)
    {
        if (IsMatchStartupActive)
        {
            return;
        }

        _paused = paused;
        if (paused)
        {
            ReleaseMouseCapture();
            ResetLiveInput();
        }
        else
        {
            _uiButtons.Clear();
        }

        UpdateMouseCaptureState();
        Invalidate();
    }

    private void ReturnToLobby()
    {
        _paused = true;
        _matchStartupPhase = MatchStartupPhase.None;
        _matchStartupViewReady = false;
        _matchStartupPrepareTask = null;
        _appState = SimulatorAppState.Lobby;
        ExitMatchGcControl();
        ReleaseMouseCapture();
        ResetLiveInput();
    }

    private void OpenEditorDialog(Form editor)
    {
        bool wasPaused = _paused;
        SetPaused(true);
        ExitMatchGcControl();

        using (editor)
        {
            editor.ShowDialog(this);
        }

        _paused = wasPaused;
        UpdateMatchGcControl();
        if (_appState != SimulatorAppState.MainMenu)
        {
            _host.ResetWorld();
            ResetCameraForMap();
        }

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
                // Continue trying the next launcher.
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
                Path.Combine(root, "src", "Simulator.Decision", "bin", "Debug", "net10.0-windows", "Simulator.Decision.exe"),
                Path.Combine(root, "src", "Simulator.Decision", "bin", "Release", "net10.0-windows", "Simulator.Decision.exe"),
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

    private void ToggleDriveTelemetryWindow()
    {
        if (_driveTelemetryForm is not null && !_driveTelemetryForm.IsDisposed)
        {
            if (_driveTelemetryForm.Visible)
            {
                _driveTelemetryForm.Close();
            }
            else
            {
                _driveTelemetryForm.Show(this);
            }

            return;
        }

        _driveTelemetryForm = new DriveTelemetryForm(_host);
        _driveTelemetryForm.FormClosed += (_, _) => _driveTelemetryForm = null;
        _driveTelemetryForm.Show(this);
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
            float hoverMix = ResolveUiHoverMix(action);
            Color idleColor = Color.FromArgb(64, 76, 92);
            Color accentColor = activeColor ?? Color.FromArgb(58, 124, 214);
            Color fillColor = BlendUiColor(idleColor, accentColor, active ? 0.72f : hoverMix * 0.48f);
            fillColor = Color.FromArgb(
                Math.Clamp(fillColor.A + (int)MathF.Round(hoverMix * 10f), 0, 255),
                fillColor.R,
                fillColor.G,
                fillColor.B);
            Rectangle drawRect = hoverMix > 0.01f ? Rectangle.Inflate(rect, 1, 1) : rect;
            using var brush = new SolidBrush(fillColor);
            using var borderPen = new Pen(
                active
                    ? BlendUiColor(Color.FromArgb(210, 236, 242, 248), Color.White, hoverMix * 0.35f)
                    : BlendUiColor(Color.FromArgb(140, 156, 170, 188), Color.FromArgb(208, 228, 238, 248), hoverMix * 0.55f),
                active ? 1.5f : 1.0f + hoverMix * 0.4f);
            graphics.FillRectangle(brush, drawRect);
            graphics.DrawRectangle(borderPen, drawRect);
            if (hoverMix > 0.01f || active)
            {
                using var highlight = new SolidBrush(Color.FromArgb(Math.Clamp((int)MathF.Round((active ? 46f : 28f) + hoverMix * 36f), 0, 255), 255, 255, 255));
                graphics.FillRectangle(highlight, drawRect.X + 1, drawRect.Y + 1, Math.Max(1, drawRect.Width - 2), Math.Max(2, drawRect.Height / 4));
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                Font buttonFont = drawRect.Height <= 32 ? _smallHudFont : _menuSubtitleFont;
                DrawUiButtonText(graphics, drawRect, label, buttonFont, Color.WhiteSmoke);
            }
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            _uiButtons.Add(new UiButton(rect, action));
        }
    }

    private static Color BlendUiColor(Color from, Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)MathF.Round(from.A + (to.A - from.A) * t),
            (int)MathF.Round(from.R + (to.R - from.R) * t),
            (int)MathF.Round(from.G + (to.G - from.G) * t),
            (int)MathF.Round(from.B + (to.B - from.B) * t));
    }

    private static Color ApplyUiAlpha(Color color, float alpha)
    {
        alpha = Math.Clamp(alpha, 0f, 1f);
        return Color.FromArgb(
            Math.Clamp((int)MathF.Round(color.A * alpha), 0, 255),
            color.R,
            color.G,
            color.B);
    }

    private static void DrawUiButtonText(Graphics graphics, Rectangle rect, string text, Font font, Color color)
    {
        Rectangle textRect = Rectangle.Inflate(rect, -8, -1);
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            textRect,
            color,
            TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis
            | TextFormatFlags.SingleLine
            | TextFormatFlags.PreserveGraphicsClipping
            | TextFormatFlags.NoPrefix);
    }

    private void ResetCameraForMap()
    {
        _cameraTargetM = ComputeMapCenterMeters();
        _cameraDistanceM = ComputeDefaultCameraDistance();
        _thirdPersonFollowDistanceScale = 1f;
        RebuildTerrainTileCache();
    }

    private void SnapCameraToSelectedEntity()
    {
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is null)
        {
            return;
        }

        RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(selected);
        float focusHeight = (float)Math.Max(0.0, selected.GroundHeightM + selected.AirborneHeightM + motion.BodyLiftM + 0.55);
        _cameraTargetM = ToScenePoint(selected.X, selected.Y, focusHeight);
        _cameraYawRad = ResolveThirdPersonCameraYaw(selected);
        _cameraPitchRad = 0.38f;
        _cameraDistanceM = 9.5f * _thirdPersonFollowDistanceScale;
    }

    private static float ResolveThirdPersonCameraYaw(SimulationEntity selected)
        => (float)(selected.TurretYawDeg * Math.PI / 180.0) + MathF.PI;

    private void ToggleObserverMode()
    {
        if (_observerMode)
        {
            _observerMode = false;
            _observerPinned = false;
            _followSelection = true;
            SnapCameraToSelectedEntity();
            return;
        }

        Vector3 forward = Vector3.Normalize(_cameraTargetM - _cameraPositionM);
        if (forward.LengthSquared() <= 1e-6f)
        {
            forward = Vector3.Normalize(_cameraTargetM - ComputeMapCenterMeters());
        }

        _observerMode = true;
        _observerPinned = false;
        _firstPersonView = false;
        _followSelection = false;
        _observerPositionM = _cameraPositionM;
        _observerYawRad = MathF.Atan2(forward.Z, forward.X);
        _observerPitchRad = Math.Clamp(MathF.Asin(Math.Clamp(forward.Y, -0.98f, 0.98f)), -1.12f, 1.12f);
        _observerMoveSpeedMps = Math.Clamp(_cameraDistanceM * 0.36f, 2f, 12f);
    }

    private void ConfigurePinnedSpectatorCamera(Vector3 position, float yawRad, float pitchRad)
    {
        _observerMode = true;
        _observerPinned = true;
        _followSelection = false;
        _firstPersonView = false;
        _observerPositionM = position;
        _observerYawRad = yawRad;
        _observerPitchRad = Math.Clamp(pitchRad, -1.12f, 1.12f);
    }

    private void SpawnPinnedSpectatorWindow()
    {
        Simulator3dOptions options = new()
        {
            MapPreset = _host.ActiveMapPreset,
            RendererMode = _host.ActiveRendererMode,
            MatchMode = _host.MatchMode,
            DeltaTimeSec = _host.DeltaTimeSec,
            SelectedTeam = _host.SelectedTeam,
            SelectedEntityId = _host.SelectedEntity?.Id,
            StartInMatch = true,
        };
        var spectator = new Simulator3dForm(_host, options, sharedHostSimulation: true, externallyDrivenCompatibilityMode: false)
        {
            Text = "RM ARTINX A-Soul模拟器 - Spectator",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(Location.X + 48, Location.Y + 48),
        };
        spectator.ConfigurePinnedSpectatorCamera(_observerPositionM, _observerYawRad, _observerPitchRad);
        spectator.Show(this);
    }

    private static float WrapAngleRadians(float angle)
    {
        while (angle > MathF.PI)
        {
            angle -= MathF.Tau;
        }

        while (angle < -MathF.PI)
        {
            angle += MathF.Tau;
        }

        return angle;
    }

    private Vector3 GetObserverForwardVector()
    {
        float cosPitch = MathF.Cos(_observerPitchRad);
        Vector3 forward = new(
            MathF.Cos(_observerYawRad) * cosPitch,
            MathF.Sin(_observerPitchRad),
            MathF.Sin(_observerYawRad) * cosPitch);
        return forward.LengthSquared() <= 1e-6f ? Vector3.UnitX : Vector3.Normalize(forward);
    }

    private void UpdateObserverMotion(double dt)
    {
        if (!_observerMode || _observerPinned || dt <= 1e-6)
        {
            return;
        }

        Vector3 forward = GetObserverForwardVector();
        Vector3 flatForward = Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z));
        if (flatForward.LengthSquared() <= 1e-6f)
        {
            flatForward = Vector3.UnitX;
        }

        Vector3 right = Vector3.Normalize(Vector3.Cross(flatForward, Vector3.UnitY));
        double moveForward = GetMovementAxisContinuous(Keys.W, 0x57, Keys.S, 0x53);
        double moveRight = GetMovementAxisContinuous(Keys.D, 0x44, Keys.A, 0x41);
        float vertical = 0f;
        if (IsAnyKeyHeld(Keys.F))
        {
            vertical += 1f;
        }

        if (IsAnyKeyHeld(Keys.C))
        {
            vertical -= 1f;
        }

        float speed = _observerMoveSpeedMps;
        if (IsAnyKeyHeld(Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey))
        {
            speed *= 1.85f;
        }

        Vector3 delta = flatForward * (float)moveForward + right * (float)moveRight + Vector3.UnitY * vertical;
        if (delta.LengthSquared() <= 1e-6f)
        {
            return;
        }

        _observerPositionM += Vector3.Normalize(delta) * speed * (float)dt;
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
        if (_observerMode || _sharedHostSimulation)
        {
            Vector3 forward = GetObserverForwardVector();
            Vector3 up = MathF.Abs(Vector3.Dot(forward, Vector3.UnitY)) >= 0.985f
                ? Vector3.UnitZ
                : Vector3.UnitY;
            _cameraPositionM = _observerPositionM;
            _cameraTargetM = _observerPositionM + forward * 24f;
            _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, up);
            float aspectObserver = Math.Max(1f, ClientSize.Width / (float)Math.Max(ClientSize.Height, 1));
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, aspectObserver, 0.03f, 1500f);
            return;
        }

        if (_firstPersonView && selected is not null)
        {
            float yaw = ResolveEntityYaw(selected);
            float turretYaw = (float)(selected.TurretYawDeg * Math.PI / 180.0);
            float gimbalPitch = (float)(selected.GimbalPitchDeg * Math.PI / 180.0);
            RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(selected);
            ResolveChassisAxes(yaw, selected.ChassisPitchDeg, selected.ChassisRollDeg, out Vector3 chassisForward, out Vector3 chassisRight, out Vector3 chassisUp);
            ResolveMountedTurretAxes(
                chassisForward,
                chassisRight,
                chassisUp,
                turretYaw - yaw,
                gimbalPitch,
                out _,
                out Vector3 turretRight,
                out Vector3 pitchedForward,
                out Vector3 pitchedUp);
            float groundHeight = (float)(selected.GroundHeightM + selected.AirborneHeightM);
            float bodyTop = (float)Math.Max(0.0, selected.BodyClearanceM + motion.BodyLiftM + selected.BodyHeightM);
            float turretBase = MathF.Max(
                bodyTop + (float)selected.GimbalMountGapM + (float)selected.GimbalMountHeightM,
                selected.GimbalHeightM > 1e-4
                    ? (float)(selected.GimbalHeightM - selected.GimbalBodyHeightM * 0.5)
                    : bodyTop);
            float hingeBase = MathF.Max(
                bodyTop + (float)selected.GimbalMountGapM + (float)selected.GimbalMountHeightM,
                turretBase);
            Vector3 chassisOrigin = ToScenePoint(selected.X, selected.Y, groundHeight);
            Vector3 hingeCenter = OffsetScenePosition(
                chassisOrigin,
                (float)selected.GimbalOffsetXM,
                (float)selected.GimbalOffsetYM,
                hingeBase,
                chassisForward,
                chassisRight,
                chassisUp);
            Vector3 turretCenter = hingeCenter
                + pitchedUp * ((float)selected.GimbalBodyHeightM * 0.50f + 0.006f)
                + pitchedForward * ((float)selected.GimbalLengthM * 0.04f);
            Vector3 barrelAxisAnchor = turretCenter
                + pitchedForward * ((float)selected.GimbalLengthM * 0.5f + MathF.Max(0.006f, (float)selected.BarrelRadiusM * 0.45f))
                + pitchedUp * (MathF.Max(0.0f, (float)selected.GimbalBodyHeightM * 0.12f) - 0.03f);
            bool heroFirstPerson = string.Equals(selected.RoleKey, "hero", StringComparison.OrdinalIgnoreCase);
            float heroForwardCameraBiasM = heroFirstPerson ? 0.04f : 0.0f;
            float cameraHeightOffsetM = heroFirstPerson ? 0.06f : 0.05f;
            Vector3 eye = barrelAxisAnchor
                + pitchedForward * heroForwardCameraBiasM
                + pitchedUp * cameraHeightOffsetM;
            Vector3 sightConvergence = eye + pitchedForward * FirstPersonSightConvergenceM;

            _cameraPositionM = eye;
            _cameraTargetM = sightConvergence;
            _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, pitchedUp.LengthSquared() > 1e-8f ? pitchedUp : Vector3.UnitY);

            float aspectFirstPerson = Math.Max(1f, ClientSize.Width / (float)Math.Max(ClientSize.Height, 1));
            float firstPersonFov = FirstPersonVerticalFovRad;
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(firstPersonFov, aspectFirstPerson, 0.015f, 1500f);
            return;
        }

        if (_followSelection)
        {
            if (selected is not null)
            {
                RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(selected);
                float focusHeight = (float)Math.Max(0.0, selected.GroundHeightM + selected.AirborneHeightM + motion.BodyLiftM + 0.55);
                Vector3 desiredTarget = ToScenePoint(selected.X, selected.Y, focusHeight);
                float followResponse = UseGpuRenderer ? 0.14f : 0.11f;
                _cameraTargetM = Vector3.Lerp(_cameraTargetM, desiredTarget, followResponse);

                float baseChaseDistance = Math.Clamp(8.5f + (float)Math.Max(selected.GroundHeightM, 0.0) * 0.22f, 6.0f, 14.0f);
                float chaseDistance = Math.Clamp(baseChaseDistance * _thirdPersonFollowDistanceScale, 3.2f, 38.0f);
                _cameraDistanceM = MathHelperLerp(_cameraDistanceM, chaseDistance, 0.045f);
                float yawResponse = _mouseCaptureActive ? 0.16f : 0.095f;
                _cameraYawRad = SmoothAngleRadians(_cameraYawRad, ResolveThirdPersonCameraYaw(selected), yawResponse);
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
        if (TerrainAssetCacheNeedsRebuild())
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
        DrawStaticStructureBodiesCached(graphics);
    }

    private void PreloadActiveMapTerrainAssets()
    {
        if (TerrainAssetCacheNeedsRebuild())
        {
            RebuildTerrainTileCache();
        }

        if (!string.IsNullOrWhiteSpace(_terrainCacheGpuSourcePath))
        {
            EnsureTerrainCacheGpuChunksBuilt();
        }

        PreloadFineTerrainVisualScenes();
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
        bool preserveTerrainCacheGpuChunks =
            TryResolveTerrainCacheGpuRenderSource(out string nextTerrainCacheSourcePath)
            && string.Equals(_terrainCacheGpuSourcePath, Path.GetFullPath(nextTerrainCacheSourcePath), StringComparison.OrdinalIgnoreCase)
            && (_terrainCacheGpuChunks.Count > 0 || _terrainCacheGpuBuildTask is not null);
        InvalidateGpuTerrainBuffers(preserveTerrainCacheGpuChunks);
        _cachedRuntimeGrid = _host.RuntimeGrid;
        _cachedTerrainAssetSignature = ResolveActiveTerrainAssetSignature();
        _terrainFaces.Clear();
        _terrainDetailFaces.Clear();
        _cachedProjectedTerrainFaces.Clear();
        _terrainDetailCenterCellX = int.MinValue;
        _terrainDetailCenterCellY = int.MinValue;
        _lastTerrainDetailRebuildTicks = 0;
        _cachedTerrainLayerBitmap?.Dispose();
        _cachedTerrainLayerBitmap = null;
        _cachedStaticStructureLayerBitmap?.Dispose();
        _cachedStaticStructureLayerBitmap = null;
        _terrainProjectionCacheCameraPosition = default;
        _terrainProjectionCacheCameraTarget = default;
        _terrainProjectionCacheViewDirection = default;
        _terrainProjectionCacheYawRad = float.NaN;
        _terrainProjectionCachePitchRad = float.NaN;
        _terrainProjectionCacheDistanceM = float.NaN;
        _terrainProjectionCacheClientSize = Size.Empty;
        _terrainLayerBitmapBuiltVersion = -1;
        _terrainLayerBitmapCameraPosition = default;
        _terrainLayerBitmapCameraTarget = default;
        _terrainLayerBitmapViewDirection = default;
        _terrainLayerBitmapClientSize = Size.Empty;
        _staticStructureLayerCacheVersion++;
        _staticStructureLayerBitmapBuiltVersion = -1;
        _staticStructureLayerBitmapCameraPosition = default;
        _staticStructureLayerBitmapCameraTarget = default;
        _staticStructureLayerBitmapViewDirection = default;
        _staticStructureLayerBitmapClientSize = Size.Empty;
        _staticStructureProjectionCacheYawRad = float.NaN;
        _staticStructureProjectionCachePitchRad = float.NaN;
        _staticStructureProjectionCacheDistanceM = float.NaN;
        _cachedProjectedStaticStructureFaces.Clear();
        _terrainProjectionCacheVersion++;
        if (preserveTerrainCacheGpuChunks)
        {
            foreach (TerrainCacheGpuChunk chunk in _terrainCacheGpuChunks)
            {
                if (chunk.Buffer != 0)
                {
                    chunk.Version = _terrainProjectionCacheVersion;
                }
            }
        }

        _terrainProjectionBuiltVersion = -1;
        EnsureTerrainColorBitmapLoaded();

        if (_cachedRuntimeGrid is null || !_cachedRuntimeGrid.IsValid)
        {
            return;
        }

        if (TryRebuildTerrainCacheTriangleMesh(_terrainFaces))
        {
            return;
        }

        int coarseStep = ResolveTerrainCoarseStep(_cachedRuntimeGrid);
        RebuildTerrainTileCacheMerged(coarseStep, coarseStep, _terrainFaces);
        AppendTerrainFacetFaces(_terrainFaces);
        RebuildVisibleTerrainDetailCache(force: true);
    }

    private bool TerrainAssetCacheNeedsRebuild()
    {
        if (!ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid))
        {
            return true;
        }

        return !string.Equals(_cachedTerrainAssetSignature, ResolveActiveTerrainAssetSignature(), StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveActiveTerrainAssetSignature()
    {
        MapPresetDefinition preset = _host.MapPreset;
        return string.Join(
            "|",
            _host.MatchMode,
            preset.Name ?? string.Empty,
            preset.SourcePath ?? string.Empty,
            preset.ImagePath ?? string.Empty,
            preset.AnnotationPath ?? string.Empty,
            preset.Width.ToString(),
            preset.Height.ToString(),
            _host.World.MetersPerWorldUnit.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
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
        return TerrainSurfaceMapSupport.ResolveBaseColorBitmapPath(_host.MapPreset);
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
        // 地图底面改为按地形颜色采样绘制，避免透视贴图导致 GDI+ 内存溢出。
        // 同时保证 PNG 与局内地图坐标一一对齐。
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
        bool energyMechanismDrawn = false;
        foreach (FacilityRegion region in _host.MapPreset.Facilities.OrderByDescending(FacilitySortDepth))
        {
            if (!ShouldRenderFacility(region))
            {
                continue;
            }

            bool energyMechanism = string.Equals(region.Type, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
            bool dogHole = string.Equals(region.Type, "dog_hole", StringComparison.OrdinalIgnoreCase);
            if (ShouldHideTemporaryArenaMechanismModels() && IsTemporaryArenaMechanismFacility(region))
            {
                continue;
            }

            if (ShouldSuppressLegacyBaseOutpostFacility(region))
            {
                continue;
            }

            if (!_showDebugSidebars && !energyMechanism && !dogHole)
            {
                continue;
            }

            if (region.Type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)
                || (!energyMechanism && !dogHole && region.HeightM <= 0.20))
            {
                continue;
            }

            if (energyMechanism)
            {
                if (energyMechanismDrawn)
                {
                    continue;
                }

                energyMechanismDrawn = true;
                if (TryResolveEnergyMechanismRenderCenter(out FacilityRegion representative, out double energyCenterX, out double energyCenterY))
                {
                    if (!TryDrawFineTerrainEnergyMechanism(graphics, representative, energyCenterX, energyCenterY))
                    {
                        if (!ShouldSuppressCoarseEnergyMechanismFallback())
                        {
                            DrawEnergyMechanismModel(graphics, representative, energyCenterX, energyCenterY);
                        }
                    }
                }
                else
                {
                    (double fallbackEnergyCenterX, double fallbackEnergyCenterY) = ResolveFacilityRegionCenter(region);
                    if (!TryDrawFineTerrainEnergyMechanism(graphics, region, fallbackEnergyCenterX, fallbackEnergyCenterY))
                    {
                        if (!ShouldSuppressCoarseEnergyMechanismFallback())
                        {
                            DrawEnergyMechanismModel(graphics, region);
                        }
                    }
                }
                continue;
            }

            if (dogHole)
            {
                DrawDogHoleModel(graphics, region);
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

        TryDrawFineTerrainOutposts(graphics);
        TryDrawFineTerrainBases(graphics);
    }

    private void DrawTeamTopNeonLights(Graphics graphics)
    {
        // Map-authored top light strips now keep their original material colors.
    }

    private void DrawSkyLightRays(Graphics graphics)
    {
        if (_host.World is null
            || _host.MapPreset.Width <= 0
            || _host.MapPreset.Height <= 0)
        {
            return;
        }

        SmoothingMode previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        DrawSkyLightRayCross(graphics, 0.33f, 0.46f, 0.22f, 0.62f, Color.FromArgb(44, 170, 226, 255));
        DrawSkyLightRayCross(graphics, 0.67f, 0.54f, 0.18f, 0.55f, Color.FromArgb(36, 154, 212, 255));
        DrawSkyLightRayCross(graphics, 0.50f, 0.50f, 0.12f, 0.42f, Color.FromArgb(22, 198, 236, 255));
        graphics.SmoothingMode = previousSmoothing;
    }

    private void DrawSkyLightRayCross(Graphics graphics, float normalizedX, float normalizedY, float topWidthM, float baseWidthM, Color color)
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        double mapWidth = Math.Max(0.1, _host.MapPreset.Width);
        double mapHeight = Math.Max(0.1, _host.MapPreset.Height);
        double centerX = mapWidth * normalizedX;
        double centerY = mapHeight * normalizedY;
        double topHalfWorld = Math.Max(0.02, topWidthM * 0.5 / scale);
        double baseHalfWorld = Math.Max(topHalfWorld * 2.5, baseWidthM * 0.5 / scale);
        float topHeightM = 5.2f;
        float bottomHeightM = 0.05f;

        DrawProjectedNeonQuad(
            graphics,
            new[]
            {
                ToScenePoint(centerX - topHalfWorld, centerY, topHeightM),
                ToScenePoint(centerX + topHalfWorld, centerY, topHeightM),
                ToScenePoint(centerX + baseHalfWorld, centerY, bottomHeightM),
                ToScenePoint(centerX - baseHalfWorld, centerY, bottomHeightM),
            },
            color);
        DrawProjectedNeonQuad(
            graphics,
            new[]
            {
                ToScenePoint(centerX, centerY - topHalfWorld, topHeightM),
                ToScenePoint(centerX, centerY + topHalfWorld, topHeightM),
                ToScenePoint(centerX, centerY + baseHalfWorld, bottomHeightM),
                ToScenePoint(centerX, centerY - baseHalfWorld, bottomHeightM),
            },
            Color.FromArgb(Math.Max(12, color.A - 8), color));
    }

    private void DrawTeamTopNeonBand(Graphics graphics, string team, bool farSide)
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        double mapWidth = Math.Max(0.1, _host.MapPreset.Width);
        double mapHeight = Math.Max(0.1, _host.MapPreset.Height);
        double marginWorld = Math.Min(mapWidth * 0.08, 0.55 / scale);
        double x0 = Math.Clamp(marginWorld, 0.0, mapWidth);
        double x1 = Math.Clamp(mapWidth - marginWorld, x0, mapWidth);
        Color teamColor = ResolveTeamColor(team);
        float[] depthsM = { 0.66f, 0.34f, 0.105f };
        int[] alphas = { 34, 72, 218 };
        for (int index = 0; index < depthsM.Length; index++)
        {
            double depthWorld = depthsM[index] / scale;
            double insetWorld = 0.045 / scale;
            double y0;
            double y1;
            if (farSide)
            {
                y1 = Math.Clamp(mapHeight - insetWorld, 0.0, mapHeight);
                y0 = Math.Clamp(y1 - depthWorld, 0.0, y1);
            }
            else
            {
                y0 = Math.Clamp(insetWorld, 0.0, mapHeight);
                y1 = Math.Clamp(y0 + depthWorld, y0, mapHeight);
            }

            Color glow = Color.FromArgb(alphas[index], teamColor);
            float heightM = 0.045f + index * 0.007f;
            DrawProjectedNeonQuad(
                graphics,
                new[]
                {
                    ToScenePoint(x0, y0, heightM),
                    ToScenePoint(x1, y0, heightM),
                    ToScenePoint(x1, y1, heightM),
                    ToScenePoint(x0, y1, heightM),
                },
                glow);
        }
    }

    private void DrawProjectedNeonQuad(Graphics graphics, IReadOnlyList<Vector3> vertices, Color fill)
    {
        if (!TryBuildProjectedFace(vertices, fill, Color.FromArgb(Math.Min(180, fill.A + 24), fill), out ProjectedFace face))
        {
            return;
        }

        using var brush = new SolidBrush(face.FillColor);
        using var pen = new Pen(face.EdgeColor, fill.A >= 180 ? 1.8f : 0.8f);
        graphics.FillPolygon(brush, face.Points);
        graphics.DrawPolygon(pen, face.Points);
    }

    private void DrawDogHoleModel(Graphics graphics, FacilityRegion region)
    {
        ResolveDogHoleFrameGeometry(
            region,
            out Vector3 center,
            out Vector3 forward,
            out Vector3 right,
            out Vector3 up,
            out float openingWidth,
            out float openingHeight,
            out float depth,
            out float frameThickness,
            out float topBeamThickness);

        float pillarHeight = openingHeight + topBeamThickness;
        float halfSpan = openingWidth * 0.5f + frameThickness * 0.5f;
        Color fillColor = Color.FromArgb(228, 74, 79, 86);
        Color edgeColor = Color.FromArgb(238, 40, 44, 49);

        DrawOrientedBoxSolid(
            graphics,
            center - right * halfSpan + up * (pillarHeight * 0.5f),
            forward,
            right,
            up,
            depth,
            frameThickness,
            pillarHeight,
            fillColor,
            edgeColor,
            null);
        DrawOrientedBoxSolid(
            graphics,
            center + right * halfSpan + up * (pillarHeight * 0.5f),
            forward,
            right,
            up,
            depth,
            frameThickness,
            pillarHeight,
            fillColor,
            edgeColor,
            null);
        DrawOrientedBoxSolid(
            graphics,
            center + up * (openingHeight + topBeamThickness * 0.5f),
            forward,
            right,
            up,
            depth,
            openingWidth + frameThickness * 2f,
            topBeamThickness,
            fillColor,
            edgeColor,
            null);
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
        DrawEntityGeometry(graphics);
        DrawEntityOverlayBars(graphics);
    }

    private void DrawEntityGeometry(Graphics graphics)
    {
        _entityDrawBuffer.Clear();
        _entityOverlayBuffer.Clear();
        _projectedEntityFaceBuffer.Clear();
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (!ShouldRenderEntity(entity))
            {
                continue;
            }

            if (ShouldHideTemporaryArenaMechanismModels() && IsTemporaryArenaMechanismEntity(entity))
            {
                continue;
            }

            if (ShouldSuppressLegacyBaseEntity(entity))
            {
                continue;
            }

            _entityDrawBuffer.Add(entity);
        }

        bool gpuGeometryOnly = _gpuGeometryPass && UseGpuRenderer;
        if (!gpuGeometryOnly)
        {
            _entityDrawBuffer.Sort((left, right) =>
                Vector3.DistanceSquared(ToScenePoint(right.X, right.Y, 0), _cameraPositionM)
                    .CompareTo(Vector3.DistanceSquared(ToScenePoint(left.X, left.Y, 0), _cameraPositionM)));
        }

        _collectProjectedFacesOnly = !gpuGeometryOnly;
        int fullDetailCount = 0;
        int proxyCount = 0;
        int structureCount = 0;
        int energyCount = 0;
        long fullDetailTicks = 0;
        long proxyTicks = 0;
        long structureTicks = 0;
        long energyTicks = 0;
        bool drawCollisionOnGpu = gpuGeometryOnly && _showCollisionDebug;
        foreach (SimulationEntity entity in _entityDrawBuffer)
        {
            if (_suppressSelectedEntityModel
                && string.Equals(entity.Id, _host.SelectedEntity?.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            float height;
            float entityHeightM = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM);
            Vector3 center = ToScenePoint(entity.X, entity.Y, entityHeightM);
            float distanceM = Vector3.Distance(center, _cameraPositionM);
            RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
            if (!gpuGeometryOnly && IsEntityFullyTerrainOccluded(entity, center, profile))
            {
                continue;
            }

            if (gpuGeometryOnly)
            {
                _gpuCurrentDynamicBatch = entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase)
                    || entity.EntityType.Equals("base", StringComparison.OrdinalIgnoreCase)
                        ? GpuDynamicBatchKind.Facility
                        : GpuDynamicBatchKind.Entity;
            }

            if (entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase))
            {
                structureCount++;
                long branchStartTicks = gpuGeometryOnly ? Stopwatch.GetTimestamp() : 0;
                height = DrawOutpostModel(graphics, entity, center, profile, StructureRenderPass.DynamicArmor);
                if (gpuGeometryOnly)
                {
                    structureTicks += Stopwatch.GetTimestamp() - branchStartTicks;
                }
                if (drawCollisionOnGpu)
                {
                    DrawEntityCollisionBox(graphics, entity, center, profile);
                }
            }
            else if (entity.EntityType.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                structureCount++;
                long branchStartTicks = gpuGeometryOnly ? Stopwatch.GetTimestamp() : 0;
                height = DrawBaseModel(graphics, entity, center, profile, StructureRenderPass.DynamicArmor);
                if (gpuGeometryOnly)
                {
                    structureTicks += Stopwatch.GetTimestamp() - branchStartTicks;
                }
                if (drawCollisionOnGpu)
                {
                    DrawEntityCollisionBox(graphics, entity, center, profile);
                }
            }
            else if (entity.EntityType.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                energyCount++;
                long branchStartTicks = gpuGeometryOnly ? Stopwatch.GetTimestamp() : 0;
                height = Math.Max(
                    1.0f,
                    profile.StructureGroundClearanceM
                    + profile.StructureBaseHeightM
                    + profile.StructureFrameHeightM
                    + profile.StructureRotorRadiusM);
                if (gpuGeometryOnly)
                {
                    energyTicks += Stopwatch.GetTimestamp() - branchStartTicks;
                }
                if (_showCollisionDebug)
                {
                    DrawEntityCollisionBox(graphics, entity, center, profile);
                }
            }
            else
            {
                bool useProxy = ShouldUseSimplifiedEntityRender(entity, center, profile, distanceM);
                long branchStartTicks = gpuGeometryOnly ? Stopwatch.GetTimestamp() : 0;
                if (useProxy)
                {
                    proxyCount++;
                    height = DrawEntityAppearanceModelProxy(graphics, entity, center, profile, distanceM);
                    if (gpuGeometryOnly)
                    {
                        proxyTicks += Stopwatch.GetTimestamp() - branchStartTicks;
                    }
                }
                else
                {
                    fullDetailCount++;
                    height = DrawEntityAppearanceModel(graphics, entity, center, profile);
                    if (gpuGeometryOnly)
                    {
                        fullDetailTicks += Stopwatch.GetTimestamp() - branchStartTicks;
                    }
                }

                if (drawCollisionOnGpu)
                {
                    DrawEntityCollisionBox(graphics, entity, center, profile);
                }
            }

            _entityOverlayBuffer.Add(new EntityRenderOverlay(entity, center, height, profile));
        }

        if (gpuGeometryOnly)
        {
            _lastGpuFullDetailEntityRenderCount = fullDetailCount;
            _lastGpuProxyEntityRenderCount = proxyCount;
            _lastGpuStructureEntityRenderCount = structureCount;
            _lastGpuEnergyEntityRenderCount = energyCount;
            _lastGpuFullDetailEntityRenderTicks = fullDetailTicks;
            _lastGpuProxyEntityRenderTicks = proxyTicks;
            _lastGpuStructureEntityRenderTicks = structureTicks;
            _lastGpuEnergyEntityRenderTicks = energyTicks;
        }

        _collectProjectedFacesOnly = false;
        if (!gpuGeometryOnly)
        {
            _projectedEntityFaceBuffer.Sort((left, right) => right.AverageDepth.CompareTo(left.AverageDepth));
            DrawProjectedFaceBatch(graphics, _projectedEntityFaceBuffer, 1f);
        }
    }

    private void DrawEntityOverlayBars(Graphics graphics)
    {
        if (_previewOnly)
        {
            return;
        }

        bool debugCollisionOnly = UseGpuRenderer && _showCollisionDebug;
        foreach (EntityRenderOverlay overlay in _entityOverlayBuffer)
        {
            if (string.Equals(overlay.Entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_showCollisionDebug)
            {
                if (debugCollisionOnly)
                {
                    continue;
                }

                DrawEntityCollisionBox(graphics, overlay.Entity, overlay.Center, overlay.Profile);
            }

            if (UseGpuRenderer
                && !_firstPersonView
                && !string.Equals(overlay.Entity.Id, _host.SelectedEntity?.Id, StringComparison.OrdinalIgnoreCase)
                && Vector3.DistanceSquared(overlay.Center, _cameraPositionM) > 24.0f * 24.0f)
            {
                continue;
            }

            if (_firstPersonView
                && !string.Equals(overlay.Entity.Id, _host.SelectedEntity?.Id, StringComparison.OrdinalIgnoreCase)
                && Vector3.DistanceSquared(overlay.Center, _cameraPositionM) > 14.0f * 14.0f)
            {
                continue;
            }

            DrawEntityBar(graphics, overlay.Entity, overlay.Center, overlay.Height);
        }
    }

    private bool ShouldUseSimplifiedEntityRender(
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile,
        float distanceM)
    {
        if (_tacticalMode)
        {
            return true;
        }

        if (_previewOnly || !UseGpuRenderer || SimulationCombatMath.IsStructure(entity))
        {
            return false;
        }

        if (!string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            double robotRadiusM = Math.Max(
                0.18,
                Math.Max(profile.BodyLengthM, profile.BodyWidthM * profile.BodyRenderWidthScale) * 0.65
                + Math.Max(profile.WheelRadiusM, profile.RearLegWheelRadiusM) * 0.75);
            double robotHeightM = Math.Max(
                0.35,
                profile.BodyClearanceM
                + profile.BodyHeightM
                + Math.Max(profile.RearClimbAssistMountHeightM + profile.RearLegWheelRadiusM, profile.WheelRadiusM));
            if (IsSceneBoundsPotentiallyVisible(center, robotRadiusM, robotHeightM))
            {
                return false;
            }
        }

        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is not null)
        {
            if (string.Equals(entity.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(selected.AutoAimTargetId)
                && string.Equals(entity.Id, selected.AutoAimTargetId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        float proxyDistanceM = _firstPersonView
            ? 3.25f
            : _observerMode
                ? 6.50f
                : 4.75f;
        if (!entity.IsAlive)
        {
            proxyDistanceM = Math.Min(proxyDistanceM, 2.40f);
        }

        return distanceM >= proxyDistanceM;
    }

    private void ConfigurePreviewMode()
    {
        _appState = SimulatorAppState.InMatch;
        _paused = true;
        _showDebugSidebars = true;
        _showProjectileTrails = false;
        _followSelection = false;
        _firstPersonView = false;
        _tacticalMode = false;
        _previewFocusEntityId = ResolvePreviewFocusEntityId();
        SnapCameraToPreviewFocus();
    }

    private string? ResolvePreviewFocusEntityId()
    {
        if (string.IsNullOrWhiteSpace(_previewStructure))
        {
            return null;
        }

        IEnumerable<SimulationEntity> candidates = _host.World.Entities.Where(entity =>
            string.Equals(entity.EntityType, _previewStructure, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(_previewTeam))
        {
            candidates = candidates.Where(entity => string.Equals(entity.Team, _previewTeam, StringComparison.OrdinalIgnoreCase));
        }

        return candidates
            .OrderBy(entity => entity.Team, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            .Select(entity => entity.Id)
            .FirstOrDefault();
    }

    private bool ShouldRenderFacility(FacilityRegion region)
    {
        if (!_previewOnly)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_previewStructure))
        {
            return false;
        }

        return string.Equals(region.Type, _previewStructure, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldHideTemporaryArenaMechanismModels()
        => HideTemporaryArenaMechanismModels && !_previewOnly;

    private bool HasFineTerrainAnnotationMap()
    {
        if (_previewOnly)
        {
            return false;
        }

        MapPresetDefinition preset = ResolveFineTerrainVisualSourcePreset();
        return !string.IsNullOrWhiteSpace(preset.AnnotationPath)
            && File.Exists(preset.AnnotationPath);
    }

    private bool ShouldSuppressCoarseEnergyMechanismFallback()
        => HasFineTerrainAnnotationMap();

    private bool ShouldSuppressLegacyBaseOutpostFacility(FacilityRegion region)
        => HasFineTerrainAnnotationMap()
            && (string.Equals(region.Type, "base", StringComparison.OrdinalIgnoreCase)
                || string.Equals(region.Type, "outpost", StringComparison.OrdinalIgnoreCase));

    private bool ShouldSuppressLegacyBaseEntity(SimulationEntity entity)
        => HasFineTerrainAnnotationMap()
            && (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase));

    private bool ShouldSuppressLegacyBaseOrOutpostProxyEntity(SimulationEntity entity)
        => HasFineTerrainAnnotationMap()
            && (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase));

    private static bool IsTemporaryArenaMechanismFacility(FacilityRegion region)
        => string.Equals(region.Type, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(region.Type, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(region.Type, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            || string.Equals(region.Type, "dog_hole", StringComparison.OrdinalIgnoreCase);

    private static bool IsTemporaryArenaMechanismEntity(SimulationEntity entity)
        => string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);

    private bool ShouldRenderEntity(SimulationEntity entity)
    {
        if (!_previewOnly)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_previewStructure))
        {
            return false;
        }

        if (!string.Equals(entity.EntityType, _previewStructure, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_previewFocusEntityId))
        {
            return string.Equals(entity.Id, _previewFocusEntityId, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(_previewTeam))
        {
            return string.Equals(entity.Team, _previewTeam, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private void SnapCameraToPreviewFocus()
    {
        Vector3 target = ComputeMapCenterMeters();
        float extent = 8.0f;

        if (string.Equals(_previewStructure, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            && TryResolveEnergyMechanismRenderCenter(out FacilityRegion energyRegion, out double energyCenterX, out double energyCenterY))
        {
            RobotAppearanceProfile profile = _host.AppearanceCatalog.ResolveFacilityProfile(energyRegion);
            float focusHeight = (float)Math.Max(
                profile.StructureGroundClearanceM + profile.StructureBaseHeightM + profile.StructureFrameHeightM * 0.45f,
                1.4);
            target = ToScenePoint(energyCenterX, energyCenterY, focusHeight);
            extent = Math.Max(
                5.5f,
                Math.Max(profile.StructureBaseLengthM, profile.StructureBaseWidthM) * 1.55f);
        }
        else
        {
            SimulationEntity? focus = !string.IsNullOrWhiteSpace(_previewFocusEntityId)
                ? _host.World.Entities.FirstOrDefault(entity => string.Equals(entity.Id, _previewFocusEntityId, StringComparison.OrdinalIgnoreCase))
                : null;
            if (focus is not null)
            {
                RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(focus);
                float focusHeight = (float)Math.Max(
                    profile.BodyHeightM + profile.StructureTopArmorCenterHeightM + profile.StructureFrameHeightM,
                    1.4);
                target = ToScenePoint(focus.X, focus.Y, focusHeight * 0.32f);
                extent = Math.Max(
                    4.0f,
                    (float)Math.Max(
                        profile.StructureBaseLengthM,
                        Math.Max(profile.StructureBaseWidthM, Math.Max(profile.BodyLengthM, profile.BodyWidthM))) * 2.1f);
            }
        }

        _cameraTargetM = target;
        _cameraYawRad = -MathF.PI * 0.52f;
        _cameraPitchRad = 1.04f;
        _cameraDistanceM = Math.Clamp(extent, 5.5f, 28f);
    }

    private bool TryResolveEnergyMechanismRenderCenter(out FacilityRegion representative, out double centerWorldX, out double centerWorldY)
    {
        representative = null!;
        centerWorldX = 0.0;
        centerWorldY = 0.0;
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (!string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            representative = _host.MapPreset.Facilities.FirstOrDefault(region =>
                string.Equals(region.Type, "energy_mechanism", StringComparison.OrdinalIgnoreCase)) ?? null!;
            centerWorldX = entity.X;
            centerWorldY = entity.Y;
            return representative is not null;
        }

        int count = 0;
        double sumX = 0.0;
        double sumY = 0.0;
        foreach (FacilityRegion region in _host.MapPreset.Facilities)
        {
            if (!string.Equals(region.Type, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            representative ??= region;
            (double regionCenterX, double regionCenterY) = ResolveFacilityRegionCenter(region);
            sumX += regionCenterX;
            sumY += regionCenterY;
            count++;
        }

        if (count == 0)
        {
            return false;
        }

        centerWorldX = sumX / count;
        centerWorldY = sumY / count;
        return true;
    }

    private static (double X, double Y) ResolveFacilityRegionCenter(FacilityRegion region)
    {
        if (region.Points.Count > 0)
        {
            double sumX = 0.0;
            double sumY = 0.0;
            foreach (Point2D point in region.Points)
            {
                sumX += point.X;
                sumY += point.Y;
            }

            return (sumX / region.Points.Count, sumY / region.Points.Count);
        }

        return ((region.X1 + region.X2) * 0.5, (region.Y1 + region.Y2) * 0.5);
    }

    private void ResolveDogHoleFrameGeometry(
        FacilityRegion region,
        out Vector3 center,
        out Vector3 forward,
        out Vector3 right,
        out Vector3 up,
        out float openingWidth,
        out float openingHeight,
        out float depth,
        out float frameThickness,
        out float topBeamThickness)
    {
        (double centerWorldX, double centerWorldY) = ResolveFacilityRegionCenter(region);
        bool isFlySlopeDogHole = region.Id.StartsWith("red_dog_hole", StringComparison.OrdinalIgnoreCase)
            || region.Id.StartsWith("blue_dog_hole", StringComparison.OrdinalIgnoreCase)
            || region.Id.Contains("fly_slope", StringComparison.OrdinalIgnoreCase);
        double defaultYawDeg = isFlySlopeDogHole ? 0.0 : 90.0;
        double defaultBottomOffset = 0.0;
        double defaultTopBeamThickness = isFlySlopeDogHole ? 0.10 : 0.05;
        float yawDeg = (float)ResolveFacilityOverride(region, "model_yaw_deg", defaultYawDeg, -360.0);
        float yaw = yawDeg * (MathF.PI / 180f);
        float bottomOffset = (float)ResolveFacilityOverride(region, "model_bottom_offset_m", defaultBottomOffset, -2.0);
        openingWidth = (float)ResolveFacilityOverride(region, "model_clear_width_m", 0.8, 0.05);
        openingHeight = (float)ResolveFacilityOverride(region, "model_clear_height_m", 0.25, 0.05);
        depth = (float)ResolveFacilityOverride(region, "model_depth_m", 0.25, 0.03);
        frameThickness = (float)ResolveFacilityOverride(region, "model_frame_thickness_m", 0.065, 0.01);
        topBeamThickness = (float)ResolveFacilityOverride(region, "model_top_beam_thickness_m", defaultTopBeamThickness, 0.01);
        float groundHeight = SampleTerrainHeightMeters(centerWorldX, centerWorldY);

        center = ToScenePoint(centerWorldX, centerWorldY, groundHeight + bottomOffset);
        forward = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        right = new Vector3(-forward.Z, 0f, forward.X);
        up = Vector3.UnitY;
    }

    private float SampleTerrainHeightMeters(double worldX, double worldY)
    {
        if (_cachedRuntimeGrid is null || !_cachedRuntimeGrid.IsValid)
        {
            return 0f;
        }

        float sampleX = (float)worldX;
        float sampleY = (float)worldY;
        float maxWorldX = _cachedRuntimeGrid.WidthCells * _cachedRuntimeGrid.CellWidthWorld;
        float maxWorldY = _cachedRuntimeGrid.HeightCells * _cachedRuntimeGrid.CellHeightWorld;
        sampleX = Math.Clamp(sampleX, 0f, Math.Max(0f, maxWorldX - 1e-4f));
        sampleY = Math.Clamp(sampleY, 0f, Math.Max(0f, maxWorldY - 1e-4f));
        if (_cachedRuntimeGrid.TrySampleCollisionSurface(sampleX, sampleY, out TerrainSurfaceSample surfaceSample, allowNeighborExpansion: false))
        {
            return (float)surfaceSample.HeightM;
        }

        if (_cachedRuntimeGrid.TrySampleCollisionSurface(sampleX, sampleY, out surfaceSample, allowNeighborExpansion: true))
        {
            return (float)surfaceSample.HeightM;
        }

        return _cachedRuntimeGrid.SampleOcclusionHeight(sampleX, sampleY);
    }

    private static double ResolveFacilityOverride(
        FacilityRegion region,
        string key,
        double fallback,
        double minValue)
    {
        if (region.AdditionalProperties is null
            || !region.AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return fallback;
        }

        double value;
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out value) => Math.Max(minValue, value),
            JsonValueKind.String when double.TryParse(element.GetString(), out value) => Math.Max(minValue, value),
            _ => fallback,
        };
    }

    private void DrawPreviewOnlyOverlay(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        string title = _previewStructure switch
        {
            "base" => "局内预览：基地",
            "outpost" => "局内预览：前哨站",
            "energy_mechanism" => "局内预览：能量机关",
            _ => "局内预览",
        };
        string teamLabel = _previewTeam switch
        {
            "red" => "红方",
            "blue" => "蓝方",
            "neutral" => "中立",
            _ => "默认队伍",
        };

        Rectangle panel = new(18, 18, 320, 72);
        using SolidBrush background = new(Color.FromArgb(168, 12, 18, 24));
        using Pen border = new(Color.FromArgb(220, 84, 100, 118));
        using SolidBrush textBrush = new(Color.WhiteSmoke);
        using SolidBrush subBrush = new(Color.FromArgb(220, 182, 192, 204));
        graphics.FillRectangle(background, panel);
        graphics.DrawRectangle(border, panel);
        graphics.DrawString(title, _hudMidFont, textBrush, panel.X + 14, panel.Y + 12);
        graphics.DrawString($"真实 C# 对局模型预览 | {teamLabel} | 鼠标滚轮缩放，右键拖动旋转", _tinyHudFont, subBrush, panel.X + 14, panel.Y + 40);
    }

    private void DrawStaticStructureBodies(Graphics graphics)
    {
        if (ShouldHideTemporaryArenaMechanismModels())
        {
            return;
        }

        _entityDrawBuffer.Clear();
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase)
                || entity.EntityType.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                if (!ShouldRenderEntity(entity))
                {
                    continue;
                }

                if (ShouldSuppressLegacyBaseEntity(entity))
                {
                    continue;
                }

                _entityDrawBuffer.Add(entity);
            }
        }

        if (_entityDrawBuffer.Count == 0)
        {
            return;
        }

        if (!(_gpuGeometryPass && UseGpuRenderer))
        {
            _entityDrawBuffer.Sort((left, right) =>
                Vector3.DistanceSquared(ToScenePoint(right.X, right.Y, 0), _cameraPositionM)
                    .CompareTo(Vector3.DistanceSquared(ToScenePoint(left.X, left.Y, 0), _cameraPositionM)));
        }

        foreach (SimulationEntity entity in _entityDrawBuffer)
        {
            float entityHeightM = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM);
            Vector3 center = ToScenePoint(entity.X, entity.Y, entityHeightM);
            RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
            double structureRadiusM = entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0.45, profile.StructureTowerRadiusM + 0.35)
                : Math.Max(0.80, profile.BodyLengthM * 0.9);
            double structureHeightM = entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(1.4, profile.StructureRoofHeightM + profile.StructureTopArmorCenterHeightM + 0.25)
                : Math.Max(1.5, profile.BodyHeightM + profile.StructureTopArmorCenterHeightM + 0.35);
            if (!IsSceneBoundsPotentiallyVisible(center, structureRadiusM, structureHeightM))
            {
                continue;
            }

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

    private void DrawStaticStructureBodiesCached(Graphics graphics)
    {
        if (ShouldHideTemporaryArenaMechanismModels())
        {
            _cachedProjectedStaticStructureFaces.Clear();
            return;
        }

        EnsureProjectedStaticStructureFaceCache();
        if (_cachedProjectedStaticStructureFaces.Count > 0)
        {
            DrawProjectedFaceBatch(graphics, _cachedProjectedStaticStructureFaces, 1.1f);
            return;
        }

        DrawStaticStructureBodies(graphics);
    }

    private void EnsureProjectedStaticStructureFaceCache()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _cachedStaticStructureLayerBitmap?.Dispose();
            _cachedStaticStructureLayerBitmap = null;
            _cachedProjectedStaticStructureFaces.Clear();
            return;
        }

        Vector3 currentViewDirection = _cameraTargetM == _cameraPositionM
            ? Vector3.UnitZ
            : Vector3.Normalize(_cameraTargetM - _cameraPositionM);
        float positionToleranceSq = _firstPersonView ? 0.0009f : 0.180f;
        float targetToleranceSq = _firstPersonView ? 0.0016f : 0.220f;
        float directionDotTolerance = _firstPersonView ? 0.99985f : 0.9970f;
        float angleTolerance = _firstPersonView ? 0.0012f : 0.010f;
        float distanceTolerance = _firstPersonView ? 0.01f : 0.18f;
        bool projectionStable =
            _cachedProjectedStaticStructureFaces.Count > 0
            && _staticStructureLayerBitmapBuiltVersion == _staticStructureLayerCacheVersion
            && _staticStructureLayerBitmapClientSize == ClientSize
            && Vector3.DistanceSquared(_staticStructureLayerBitmapCameraPosition, _cameraPositionM) <= positionToleranceSq
            && Vector3.DistanceSquared(_staticStructureLayerBitmapCameraTarget, _cameraTargetM) <= targetToleranceSq
            && Vector3.Dot(_staticStructureLayerBitmapViewDirection, currentViewDirection) >= directionDotTolerance
            && MathF.Abs(_staticStructureProjectionCacheYawRad - _cameraYawRad) <= angleTolerance
            && MathF.Abs(_staticStructureProjectionCachePitchRad - _cameraPitchRad) <= angleTolerance
            && MathF.Abs(_staticStructureProjectionCacheDistanceM - _cameraDistanceM) <= distanceTolerance;
        if (projectionStable)
        {
            return;
        }

        _projectedStaticStructureFaceBuffer.Clear();
        bool previousCollectMode = _collectProjectedFacesOnly;
        int previousEntityFaceCount = _projectedEntityFaceBuffer.Count;
        _collectProjectedFacesOnly = true;
        try
        {
            using Bitmap scratchBitmap = new(1, 1, PixelFormat.Format32bppPArgb);
            using Graphics scratchGraphics = Graphics.FromImage(scratchBitmap);
            DrawStaticStructureBodies(scratchGraphics);
            for (int index = previousEntityFaceCount; index < _projectedEntityFaceBuffer.Count; index++)
            {
                _projectedStaticStructureFaceBuffer.Add(_projectedEntityFaceBuffer[index]);
            }

            if (_projectedEntityFaceBuffer.Count > previousEntityFaceCount)
            {
                _projectedEntityFaceBuffer.RemoveRange(previousEntityFaceCount, _projectedEntityFaceBuffer.Count - previousEntityFaceCount);
            }
        }
        finally
        {
            _collectProjectedFacesOnly = previousCollectMode;
        }
        _projectedStaticStructureFaceBuffer.Sort((left, right) => right.AverageDepth.CompareTo(left.AverageDepth));
        _cachedProjectedStaticStructureFaces.Clear();
        _cachedProjectedStaticStructureFaces.AddRange(_projectedStaticStructureFaceBuffer);
        _staticStructureLayerBitmapBuiltVersion = _staticStructureLayerCacheVersion;
        _staticStructureLayerBitmapCameraPosition = _cameraPositionM;
        _staticStructureLayerBitmapCameraTarget = _cameraTargetM;
        _staticStructureLayerBitmapViewDirection = currentViewDirection;
        _staticStructureLayerBitmapClientSize = ClientSize;
        _staticStructureProjectionCacheYawRad = _cameraYawRad;
        _staticStructureProjectionCachePitchRad = _cameraPitchRad;
        _staticStructureProjectionCacheDistanceM = _cameraDistanceM;
    }

    private bool IsSceneBoundsPotentiallyVisible(Vector3 center, double radiusM, double heightM)
    {
        float radius = (float)Math.Max(0.08, radiusM);
        float height = (float)Math.Max(0.10, heightM);
        Vector4 viewCenter = Vector4.Transform(new Vector4(center, 1f), _viewMatrix);
        float depth = -viewCenter.Z;
        float bound = MathF.Sqrt(radius * radius + (height * 0.5f) * (height * 0.5f));
        if (depth < -bound)
        {
            return false;
        }

        float depthForCull = Math.Max(0.06f, depth + bound);
        float xLimit = depthForCull / Math.Max(0.05f, _projectionMatrix.M11) + bound;
        float yLimit = depthForCull / Math.Max(0.05f, _projectionMatrix.M22) + bound;
        return MathF.Abs(viewCenter.X) <= xLimit && MathF.Abs(viewCenter.Y) <= yLimit;
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
                float barrelBase = turretBase + profile.GimbalBodyHeightM * 0.46f - profile.BarrelRadiusM - 0.03f;
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

    private static IReadOnlyList<Vector3> BuildOrientedRectFootprint(
        Vector3 center,
        float length,
        float width,
        Vector3 forward,
        Vector3 right)
    {
        Vector3 safeForward = forward.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(forward);
        Vector3 safeRight = right.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(right);
        float halfLength = length * 0.5f;
        float halfWidth = width * 0.5f;
        return new[]
        {
            center - safeForward * halfLength - safeRight * halfWidth,
            center + safeForward * halfLength - safeRight * halfWidth,
            center + safeForward * halfLength + safeRight * halfWidth,
            center - safeForward * halfLength + safeRight * halfWidth,
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

    private static Vector3 OffsetScenePosition(
        Vector3 center,
        float localForward,
        float localLateral,
        float localUp,
        Vector3 forward,
        Vector3 right,
        Vector3 up)
    {
        return center + forward * localForward + right * localLateral + up * localUp;
    }

    private static float ResolveEntityYaw(SimulationEntity entity)
    {
        return (float)(entity.AngleDeg * Math.PI / 180.0);
    }

    private static Color TintProfileColor(Color source, Color teamTint, float tintAmount, bool alive)
    {
        _ = teamTint;
        return ResolveDeepGrayMaterial(source, alive, Math.Clamp(tintAmount, 0f, 1f) * 0.08f - 0.02f);
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

        string hpText = $"\u8840\u91cf {(int)Math.Ceiling(Math.Max(0.0, entity.Health))}/{(int)Math.Ceiling(Math.Max(0.0, entity.MaxHealth))}";
        using var textBrush = new SolidBrush(Color.FromArgb(entity.IsAlive ? 232 : 148, 232, 236, 242));
        SizeF hpSize = graphics.MeasureString(hpText, _tinyHudFont);
        graphics.DrawString(hpText, _tinyHudFont, textBrush, barPoint.X - hpSize.Width * 0.5f, backRect.Y - 13f);
    }

    private void DrawProjectiles(Graphics graphics)
    {
        EnsureCpuProjectileLayerSurface();
        if (_cpuProjectileLayerGraphics is null || _cpuProjectileLayerBitmap is null)
        {
            return;
        }

        Graphics layerGraphics = _cpuProjectileLayerGraphics;
        layerGraphics.Clear(Color.Transparent);
        int commandCount = BuildProjectileRenderCommands(out ProjectileRenderCommand[] commands);
        for (int index = 0; index < commandCount; index++)
        {
            ProjectileRenderCommand command = commands[index];
            if (!command.Visible)
            {
                continue;
            }

            if (command.TrailPoints is { Length: > 1 })
            {
                using var trailPen = new Pen(command.TrailColor, 1.2f);
                layerGraphics.DrawLines(trailPen, command.TrailPoints);
            }

            if (command.Solid)
            {
                DrawProjectileSphere(layerGraphics, command);
            }
            else
            {
                DrawProjectileFlatSprite(layerGraphics, command);
            }
        }

        graphics.DrawImageUnscaled(_cpuProjectileLayerBitmap, 0, 0);
    }

    private int BuildProjectileRenderCommands(out ProjectileRenderCommand[] commands)
    {
        IList<SimulationProjectile> projectiles = _host.World.Projectiles;
        if (projectiles.Count == 0)
        {
            commands = Array.Empty<ProjectileRenderCommand>();
            return 0;
        }

        if (_projectileRenderCommandBuffer.Length < projectiles.Count)
        {
            _projectileRenderCommandBuffer = new ProjectileRenderCommand[projectiles.Count];
        }

        for (int index = 0; index < projectiles.Count; index++)
        {
            _projectileRenderCommandBuffer[index] = BuildProjectileRenderCommand(projectiles[index]);
        }

        commands = _projectileRenderCommandBuffer;
        return projectiles.Count;
    }

    private ProjectileRenderCommand BuildProjectileRenderCommand(SimulationProjectile projectile)
    {
        Vector3 center = ToScenePoint(projectile.X, projectile.Y, (float)projectile.HeightM);
        if (!TryProject(center, out PointF screenCenter, out _))
        {
            return default;
        }

        bool largeRound = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        float radiusM = (float)(SimulationCombatMath.ProjectileDiameterM(projectile.AmmoType) * 0.5);
        float screenRadius = largeRound ? 1.9f : 1.1f;
        if (TryProject(center + Vector3.UnitY * Math.Max(0.0025f, radiusM), out PointF verticalEdge, out _))
        {
            screenRadius = Math.Max(screenRadius, MathF.Abs(verticalEdge.Y - screenCenter.Y));
        }

        if (TryProject(center + Vector3.UnitX * Math.Max(0.0025f, radiusM), out PointF horizontalEdge, out _))
        {
            screenRadius = Math.Max(screenRadius, MathF.Abs(horizontalEdge.X - screenCenter.X));
        }

        bool solid = _host.SolidProjectileRendering;
        screenRadius *= solid
            ? (largeRound ? 0.56f : 0.59f)
            : (largeRound ? 1.10f : 1.00f);
        screenRadius = solid
            ? Math.Clamp(screenRadius, largeRound ? 1.2f : 0.7f, largeRound ? 7.8f : 4.3f)
            : Math.Clamp(screenRadius, largeRound ? 1.4f : 0.9f, largeRound ? 6.3f : 3.7f);

        Color core = solid
            ? (largeRound ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 164, 255, 172))
            : BlendColor(
                string.Equals(projectile.Team, "red", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(236, 255, 124, 102)
                    : Color.FromArgb(236, 110, 178, 255),
                Color.White,
                0.26f);
        Color mid = solid
            ? (largeRound ? Color.FromArgb(255, 146, 214, 255) : Color.FromArgb(255, 52, 255, 84))
            : (string.Equals(projectile.Team, "red", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(236, 255, 124, 102)
                : Color.FromArgb(236, 110, 178, 255));
        Color rim = solid
            ? (largeRound ? Color.FromArgb(255, 72, 176, 255) : Color.FromArgb(255, 12, 184, 44))
            : Color.FromArgb(220, BlendColor(mid, Color.Black, 0.18f));
        Color glow = solid
            ? (largeRound ? Color.FromArgb(82, 96, 198, 255) : Color.FromArgb(88, 52, 255, 96))
            : Color.FromArgb(74, mid);
        Color trailColor = largeRound
            ? Color.FromArgb(138, 86, 188, 255)
            : Color.FromArgb(132, 72, 255, 108);

        RectangleF flatBody = new(
            screenCenter.X - screenRadius,
            screenCenter.Y - screenRadius * (largeRound ? 0.52f : 0.58f),
            screenRadius * 2f,
            screenRadius * (largeRound ? 1.04f : 1.16f));

        PointF[]? trailPoints = null;
        if (_showProjectileTrails
            && _projectileTrailPoints.TryGetValue(projectile.Id, out List<Vector3>? trail)
            && trail.Count > 1)
        {
            List<PointF> screenTrail = new(trail.Count);
            foreach (Vector3 trailPoint in trail)
            {
                if (TryProject(trailPoint, out PointF screenPoint, out _))
                {
                    screenTrail.Add(screenPoint);
                }
            }

            if (screenTrail.Count > 1)
            {
                trailPoints = screenTrail.ToArray();
            }
        }

        return new ProjectileRenderCommand(
            Visible: true,
            Solid: solid,
            Center: screenCenter,
            ScreenRadius: screenRadius,
            FlatBody: flatBody,
            CoreColor: core,
            MidColor: mid,
            RimColor: rim,
            GlowColor: glow,
            TrailColor: trailColor,
            TrailPoints: trailPoints);
    }

    private void DrawProjectileSphere(Graphics graphics, Vector3 center, float radiusM, bool largeRound)
    {
        if (!TryProject(center, out PointF screenCenter, out _))
        {
            return;
        }

        float screenRadius = largeRound ? 1.9f : 1.1f;
        if (TryProject(center + Vector3.UnitY * Math.Max(0.0025f, radiusM), out PointF verticalEdge, out _))
        {
            screenRadius = Math.Max(screenRadius, MathF.Abs(verticalEdge.Y - screenCenter.Y));
        }

        if (TryProject(center + Vector3.UnitX * Math.Max(0.0025f, radiusM), out PointF horizontalEdge, out _))
        {
            screenRadius = Math.Max(screenRadius, MathF.Abs(horizontalEdge.X - screenCenter.X));
        }

        screenRadius *= largeRound ? 1.12f : 1.18f;
        screenRadius = Math.Clamp(screenRadius, largeRound ? 2.4f : 1.3f, largeRound ? 15.5f : 8.6f);

        Color core = largeRound ? Color.FromArgb(255, 246, 251, 255) : Color.FromArgb(255, 112, 255, 128);
        Color mid = largeRound ? Color.FromArgb(255, 226, 234, 244) : Color.FromArgb(255, 40, 236, 82);
        Color rim = largeRound ? Color.FromArgb(255, 188, 196, 212) : Color.FromArgb(255, 10, 164, 36);
        Color glow = largeRound ? Color.FromArgb(72, 255, 255, 255) : Color.FromArgb(84, 72, 255, 116);
        float glowRadius = screenRadius * (largeRound ? 1.8f : 2.0f);
        using var glowBrush = new SolidBrush(glow);
        using var rimBrush = new SolidBrush(rim);
        using var midBrush = new SolidBrush(Color.FromArgb(118, mid));
        using var coreBrush = new SolidBrush(core);
        using var highlightBrush = new SolidBrush(Color.FromArgb(largeRound ? 108 : 84, Color.White));
        using var edgePen = new Pen(Color.FromArgb(220, rim), 1f);

        graphics.FillEllipse(glowBrush, screenCenter.X - glowRadius, screenCenter.Y - glowRadius, glowRadius * 2f, glowRadius * 2f);
        graphics.FillEllipse(rimBrush, screenCenter.X - screenRadius, screenCenter.Y - screenRadius, screenRadius * 2f, screenRadius * 2f);
        graphics.FillEllipse(midBrush, screenCenter.X - screenRadius * 0.82f, screenCenter.Y - screenRadius * 0.82f, screenRadius * 1.64f, screenRadius * 1.64f);
        graphics.FillEllipse(coreBrush, screenCenter.X - screenRadius * 0.50f, screenCenter.Y - screenRadius * 0.50f, screenRadius, screenRadius);
        graphics.FillEllipse(highlightBrush, screenCenter.X - screenRadius * 0.62f, screenCenter.Y - screenRadius * 0.72f, screenRadius * 0.72f, screenRadius * 0.58f);
        graphics.DrawEllipse(edgePen, screenCenter.X - screenRadius, screenCenter.Y - screenRadius, screenRadius * 2f, screenRadius * 2f);
    }

    private void DrawProjectileSphere(Graphics graphics, ProjectileRenderCommand command)
    {
        PointF screenCenter = command.Center;
        float screenRadius = command.ScreenRadius;
        float glowRadius = screenRadius * 1.9f;
        using var glowBrush = new SolidBrush(command.GlowColor);
        using var rimBrush = new SolidBrush(command.RimColor);
        using var midBrush = new SolidBrush(Color.FromArgb(118, command.MidColor));
        using var coreBrush = new SolidBrush(command.CoreColor);
        using var highlightBrush = new SolidBrush(Color.FromArgb(92, Color.White));
        using var edgePen = new Pen(Color.FromArgb(220, command.RimColor), 1f);

        graphics.FillEllipse(glowBrush, screenCenter.X - glowRadius, screenCenter.Y - glowRadius, glowRadius * 2f, glowRadius * 2f);
        graphics.FillEllipse(rimBrush, screenCenter.X - screenRadius, screenCenter.Y - screenRadius, screenRadius * 2f, screenRadius * 2f);
        graphics.FillEllipse(midBrush, screenCenter.X - screenRadius * 0.82f, screenCenter.Y - screenRadius * 0.82f, screenRadius * 1.64f, screenRadius * 1.64f);
        graphics.FillEllipse(coreBrush, screenCenter.X - screenRadius * 0.50f, screenCenter.Y - screenRadius * 0.50f, screenRadius, screenRadius);
        graphics.FillEllipse(highlightBrush, screenCenter.X - screenRadius * 0.62f, screenCenter.Y - screenRadius * 0.72f, screenRadius * 0.72f, screenRadius * 0.58f);
        graphics.DrawEllipse(edgePen, screenCenter.X - screenRadius, screenCenter.Y - screenRadius, screenRadius * 2f, screenRadius * 2f);
    }

    private void DrawProjectileFlatSprite(Graphics graphics, Vector3 center, float radiusM, bool largeRound, string team)
    {
        if (!TryProject(center, out PointF screenCenter, out _))
        {
            return;
        }

        float halfWidth = largeRound ? 4.2f : 2.6f;
        float halfHeight = largeRound ? 2.2f : 1.5f;
        if (TryProject(center + Vector3.UnitX * Math.Max(0.0025f, radiusM), out PointF horizontalEdge, out _))
        {
            halfWidth = Math.Max(halfWidth, MathF.Abs(horizontalEdge.X - screenCenter.X) * 2.2f);
            halfHeight = Math.Max(halfHeight, MathF.Abs(horizontalEdge.X - screenCenter.X) * 1.1f);
        }

        Color tint = string.Equals(team, "red", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(236, 255, 124, 102)
            : Color.FromArgb(236, 110, 178, 255);
        using var glowBrush = new SolidBrush(Color.FromArgb(74, tint));
        using var fillBrush = new SolidBrush(tint);
        using var coreBrush = new SolidBrush(BlendColor(tint, Color.White, 0.26f));
        using var edgePen = new Pen(Color.FromArgb(220, BlendColor(tint, Color.Black, 0.18f)), 1f);

        graphics.FillEllipse(glowBrush, screenCenter.X - halfWidth * 1.5f, screenCenter.Y - halfWidth * 1.5f, halfWidth * 3.0f, halfWidth * 3.0f);
        RectangleF body = new(screenCenter.X - halfWidth, screenCenter.Y - halfHeight, halfWidth * 2f, halfHeight * 2f);
        graphics.FillRectangle(fillBrush, body.X, body.Y, body.Width, body.Height);
        graphics.FillRectangle(coreBrush, body.X + body.Width * 0.18f, body.Y + body.Height * 0.18f, body.Width * 0.64f, body.Height * 0.64f);
        graphics.DrawRectangle(edgePen, body.X, body.Y, body.Width, body.Height);
    }

    private void DrawProjectileFlatSprite(Graphics graphics, ProjectileRenderCommand command)
    {
        RectangleF body = command.FlatBody;
        float glowRadius = Math.Max(body.Width, body.Height) * 1.5f;
        using var glowBrush = new SolidBrush(command.GlowColor);
        using var fillBrush = new SolidBrush(command.MidColor);
        using var coreBrush = new SolidBrush(command.CoreColor);
        using var edgePen = new Pen(command.RimColor, 1f);

        graphics.FillEllipse(glowBrush, command.Center.X - glowRadius, command.Center.Y - glowRadius, glowRadius * 2f, glowRadius * 2f);
        graphics.FillRectangle(fillBrush, body.X, body.Y, body.Width, body.Height);
        graphics.FillRectangle(coreBrush, body.X + body.Width * 0.18f, body.Y + body.Height * 0.18f, body.Width * 0.64f, body.Height * 0.64f);
        graphics.DrawRectangle(edgePen, body.X, body.Y, body.Width, body.Height);
    }

    private void EnsureCpuProjectileLayerSurface()
    {
        if (_cpuProjectileLayerBitmap is not null
            && _cpuProjectileLayerGraphics is not null
            && _cpuProjectileLayerClientSize == ClientSize)
        {
            return;
        }

        _cpuProjectileLayerGraphics?.Dispose();
        _cpuProjectileLayerGraphics = null;
        _cpuProjectileLayerBitmap?.Dispose();
        _cpuProjectileLayerBitmap = null;
        _cpuProjectileLayerClientSize = ClientSize;

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        _cpuProjectileLayerBitmap = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        _cpuProjectileLayerGraphics = Graphics.FromImage(_cpuProjectileLayerBitmap);
        _cpuProjectileLayerGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _cpuProjectileLayerGraphics.CompositingQuality = CompositingQuality.HighSpeed;
    }

    private bool IsEntityFullyTerrainOccluded(SimulationEntity entity, Vector3 center, RobotAppearanceProfile profile)
    {
        if (_cachedRuntimeGrid is null || !_cachedRuntimeGrid.IsValid)
        {
            return false;
        }

        float yaw = ResolveEntityYaw(entity);
        float bodyLength = Math.Max(0.12f, profile.BodyLengthM);
        float bodyWidth = Math.Max(0.10f, profile.BodyWidthM * profile.BodyRenderWidthScale);
        float bodyBase = Math.Max(0f, profile.BodyClearanceM);
        float bodyHeight = Math.Max(0.08f, profile.BodyHeightM + profile.GimbalBodyHeightM * 0.55f);

        if (entity.EntityType.Equals("outpost", StringComparison.OrdinalIgnoreCase))
        {
            bodyLength = OutpostBaseWidthM;
            bodyWidth = OutpostBaseWidthM;
            bodyBase = OutpostBaseLiftM;
            bodyHeight = OutpostTowerHeightM + 0.12f;
        }
        else if (entity.EntityType.Equals("base", StringComparison.OrdinalIgnoreCase))
        {
            bodyLength = BaseDiagramLengthM;
            bodyWidth = BaseDiagramWidthM;
            bodyBase = 0f;
            bodyHeight = BaseDiagramHeightM + 0.24f;
        }

        float halfLength = bodyLength * 0.42f;
        float halfWidth = bodyWidth * 0.42f;
        float lowHeight = bodyBase + MathF.Min(bodyHeight * 0.25f, 0.22f);
        float midHeight = bodyBase + bodyHeight * 0.55f;
        float topHeight = bodyBase + bodyHeight;

        Span<Vector3> probes = stackalloc Vector3[8];
        int count = 0;
        probes[count++] = OffsetScenePosition(center, 0f, 0f, yaw, lowHeight);
        probes[count++] = OffsetScenePosition(center, 0f, 0f, yaw, midHeight);
        probes[count++] = OffsetScenePosition(center, 0f, 0f, yaw, topHeight);
        probes[count++] = OffsetScenePosition(center, halfLength, 0f, yaw, midHeight);
        probes[count++] = OffsetScenePosition(center, -halfLength, 0f, yaw, midHeight);
        probes[count++] = OffsetScenePosition(center, 0f, halfWidth, yaw, midHeight);
        probes[count++] = OffsetScenePosition(center, 0f, -halfWidth, yaw, midHeight);
        probes[count++] = OffsetScenePosition(center, halfLength * 0.55f, 0f, yaw, topHeight);

        for (int index = 0; index < count; index++)
        {
            if (!IsTerrainOccludingPoint(probes[index]))
            {
                return false;
            }
        }

        return true;
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
            int sampleIndex = _cachedRuntimeGrid.IndexOf(cellX, cellY);
            float terrainHeight = _cachedRuntimeGrid.SampleOcclusionHeight(sampleWorldX, sampleWorldY);
            float visionHeight = terrainHeight;
            if ((terrainHeight > 0.03f || _cachedRuntimeGrid.VisionBlockMap[sampleIndex])
                && sample.Y <= visionHeight + 0.02f)
            {
                return true;
            }

            if (IsStructureOccludingPoint(sample, targetPoint))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsStructureOccludingPoint(Vector3 samplePoint, Vector3 targetPoint)
    {
        float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        foreach (SimulationEntity structure in _host.World.Entities)
        {
            if (!structure.IsAlive
                || structure.IsSimulationSuppressed
                || !SimulationCombatMath.IsStructure(structure))
            {
                continue;
            }

            Vector3 center = ToScenePoint(structure.X, structure.Y, (float)Math.Max(0.0, structure.GroundHeightM + structure.AirborneHeightM));
            float bottom = center.Y;
            float height = ResolveStructureVisionBlockHeightM(structure);
            if (samplePoint.Y < bottom - 0.02f || samplePoint.Y > bottom + height + 0.05f)
            {
                continue;
            }

            float radius = ResolveStructureVisionBlockRadiusM(structure, metersPerWorldUnit);
            float dx = samplePoint.X - center.X;
            float dz = samplePoint.Z - center.Z;
            if (dx * dx + dz * dz > radius * radius)
            {
                continue;
            }

            float targetDx = targetPoint.X - center.X;
            float targetDz = targetPoint.Z - center.Z;
            float targetMargin = Math.Max(0.10f, radius * 0.25f);
            if (targetDx * targetDx + targetDz * targetDz <= (radius + targetMargin) * (radius + targetMargin))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static float ResolveStructureVisionBlockRadiusM(SimulationEntity structure, float metersPerWorldUnit)
    {
        float collisionRadius = (float)Math.Max(0.0, structure.CollisionRadiusWorld * metersPerWorldUnit);
        return structure.EntityType switch
        {
            "base" => Math.Max(0.72f, collisionRadius),
            "outpost" => Math.Max(0.52f, collisionRadius),
            "energy_mechanism" => Math.Max(1.45f, collisionRadius),
            _ => Math.Max(0.34f, collisionRadius),
        };
    }

    private static float ResolveStructureVisionBlockHeightM(SimulationEntity structure)
        => structure.EntityType switch
        {
            "base" => Math.Max(1.10f, (float)structure.BodyHeightM + 0.55f),
            "outpost" => Math.Max(1.65f, (float)structure.BodyHeightM + 1.00f),
            "energy_mechanism" => Math.Max(2.35f, (float)structure.BodyHeightM + 1.80f),
            _ => Math.Max(0.80f, (float)structure.BodyHeightM),
        };

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

        _projectileTrailActiveIds.Clear();
        foreach (SimulationProjectile projectile in _host.World.Projectiles)
        {
            _projectileTrailActiveIds.Add(projectile.Id);
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

        _projectileTrailStaleIds.Clear();
        foreach (string id in _projectileTrailPoints.Keys)
        {
            if (!_projectileTrailActiveIds.Contains(id))
            {
                _projectileTrailStaleIds.Add(id);
            }
        }

        foreach (string staleId in _projectileTrailStaleIds)
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

        bool largeRound = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        Color tint = largeRound
            ? Color.FromArgb(138, 86, 188, 255)
            : Color.FromArgb(132, 72, 255, 108);
        using var pen = new Pen(tint, 1.2f);
        graphics.DrawLines(pen, points.ToArray());
    }

    private void DrawEntityCollisionBox(Graphics graphics, SimulationEntity entity, Vector3 center, RobotAppearanceProfile profile)
    {
        if (SimulationCombatMath.IsLegacyMechanismCollisionSuppressed(entity))
        {
            return;
        }

        float yaw = ResolveEntityYaw(entity);
        _ = profile;
        foreach (EntityCollisionPart part in EntityCollisionModel.ResolveParts(entity))
        {
            Vector3 partCenter = OffsetScenePosition(
                center,
                (float)part.LocalX,
                (float)part.LocalY,
                yaw,
                0f);
            IReadOnlyList<Vector3> footprint = BuildOrientedRectFootprint(
                partCenter,
                (float)Math.Max(0.02, part.LengthM),
                (float)Math.Max(0.02, part.WidthM),
                (float)Math.Max(0.0, part.MinHeightM),
                yaw);
            DrawPrismWireframe(
                graphics,
                footprint,
                (float)Math.Max(0.04, part.HeightM),
                Color.FromArgb(34, 94, 170, 255),
                Color.FromArgb(210, 150, 214, 255),
                null);
        }
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

        if (ShouldUseGpuDynamicPrimitiveFastPath())
        {
            Vector3 lift = new(0f, height, 0f);
            Vector3 fastLabelPoint = Vector3.Zero;
            for (int index = 0; index < baseVertices.Count; index++)
            {
                fastLabelPoint += baseVertices[index] + lift;
            }

            Span<Vector3> topVerticesFast = baseVertices.Count <= 16 ? stackalloc Vector3[baseVertices.Count] : new Vector3[baseVertices.Count];
            for (int index = 0; index < baseVertices.Count; index++)
            {
                topVerticesFast[index] = baseVertices[index] + lift;
            }

            DrawGpuGeneralPrismFast(baseVertices, topVerticesFast, topColor, 0.78f, 0.50f);
            fastLabelPoint /= baseVertices.Count;
            if (!string.IsNullOrWhiteSpace(label)
                && TryProject(fastLabelPoint + new Vector3(0f, 0.05f, 0f), out PointF gpuScreenLabelFast, out _))
            {
                using var textBrush = new SolidBrush(Color.FromArgb(230, 230, 234, 242));
                SizeF size = graphics.MeasureString(label, _smallHudFont);
                graphics.DrawString(label, _smallHudFont, textBrush, gpuScreenLabelFast.X - size.Width * 0.5f, gpuScreenLabelFast.Y - 11f);
            }

            return;
        }

        Vector3[] topVertices = new Vector3[baseVertices.Count];
        Vector3 labelPoint = Vector3.Zero;
        for (int index = 0; index < baseVertices.Count; index++)
        {
            topVertices[index] = baseVertices[index] + new Vector3(0f, height, 0f);
            labelPoint += topVertices[index];
        }

        if (_gpuGeometryPass && UseGpuRenderer)
        {
            var solidFaces = new List<SolidFace>(baseVertices.Count + 1)
            {
                new(topVertices, 0.78f),
            };
            for (int index = 0; index < baseVertices.Count; index++)
            {
                int next = (index + 1) % baseVertices.Count;
                solidFaces.Add(new SolidFace(
                    new[]
                    {
                        baseVertices[index],
                        baseVertices[next],
                        topVertices[next],
                        topVertices[index],
                    },
                    0.50f));
            }

            DrawGpuSolidFaces(solidFaces, topColor, edgeColor);
            labelPoint /= baseVertices.Count;
            if (!string.IsNullOrWhiteSpace(label)
                && TryProject(labelPoint + new Vector3(0f, 0.05f, 0f), out PointF gpuScreenLabel, out _))
            {
                using var textBrush = new SolidBrush(Color.FromArgb(230, 230, 234, 242));
                SizeF size = graphics.MeasureString(label, _smallHudFont);
                graphics.DrawString(label, _smallHudFont, textBrush, gpuScreenLabel.X - size.Width * 0.5f, gpuScreenLabel.Y - 11f);
            }

            return;
        }

        _projectedFaceScratchBuffer.Clear();
        List<ProjectedFace> faces = _projectedFaceScratchBuffer;
        faces.EnsureCapacity(baseVertices.Count + 1);
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
        if (_collectProjectedFacesOnly)
        {
            _projectedEntityFaceBuffer.AddRange(faces);
        }
        else
        {
            DrawProjectedFaceBatch(graphics, faces, 1.1f);
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
        Vector3 keyLight = Vector3.Normalize(new Vector3(-0.45f, 1.0f, -0.35f));
        Vector3 rimLight = Vector3.Normalize(new Vector3(0.55f, 0.72f, 0.48f));
        float keyDiffuse = MathF.Max(0f, Vector3.Dot(normal, keyLight));
        float rimDiffuse = MathF.Max(0f, Vector3.Dot(normal, rimLight));
        float diffuseFill = MathF.Abs(normal.Y) * 0.05f;
        float ambientFloor = MathF.Max(ambient, 0.48f);
        float brightness = Math.Clamp(
            ambientFloor + keyDiffuse * 0.32f + rimDiffuse * 0.13f + diffuseFill,
            0.44f,
            1.15f);
        Color lit = ScaleColor(color, brightness);
        float coolTint = Math.Clamp(keyDiffuse * 0.018f + rimDiffuse * 0.026f, 0f, 0.040f);
        if (coolTint <= 1e-5f)
        {
            return ApplyAmbientSceneLight(lit, 0.026f);
        }

        int r = Math.Clamp((int)MathF.Round(lit.R + (196 - lit.R) * coolTint * 0.72f), 0, 255);
        int g = Math.Clamp((int)MathF.Round(lit.G + (224 - lit.G) * coolTint), 0, 255);
        int b = Math.Clamp((int)MathF.Round(lit.B + (255 - lit.B) * coolTint), 0, 255);
        return ApplyAmbientSceneLight(Color.FromArgb(lit.A, r, g, b), 0.026f);
    }

    private static Color ApplyAmbientSceneLight(Color color, float strength)
    {
        float amount = Math.Clamp(strength, 0f, 1f);
        int r = Math.Clamp((int)MathF.Round(color.R + (204 - color.R) * amount * 0.72f), 0, 255);
        int g = Math.Clamp((int)MathF.Round(color.G + (226 - color.G) * amount), 0, 255);
        int b = Math.Clamp((int)MathF.Round(color.B + (255 - color.B) * amount), 0, 255);
        return Color.FromArgb(color.A, r, g, b);
    }

    private static Color ScaleColor(Color color, float scale)
    {
        return Color.FromArgb(
            color.A,
            Math.Clamp((int)MathF.Round(color.R * scale), 0, 255),
            Math.Clamp((int)MathF.Round(color.G * scale), 0, 255),
            Math.Clamp((int)MathF.Round(color.B * scale), 0, 255));
    }

    private bool ShouldUseGpuDynamicPrimitiveFastPath()
        => _gpuGeometryPass && UseGpuRenderer && _gpuBatchingDynamicGeometry;

    private void DrawGpuGeneralPrismFast(
        IReadOnlyList<Vector3> bottomVertices,
        IReadOnlyList<Vector3> topVertices,
        Color fillColor,
        float topAmbient,
        float sideAmbient)
    {
        if (bottomVertices.Count < 3 || topVertices.Count != bottomVertices.Count)
        {
            return;
        }

        AppendGpuShadedPolygon(topVertices, fillColor, topAmbient);
        for (int index = 0; index < bottomVertices.Count; index++)
        {
            int next = (index + 1) % bottomVertices.Count;
            AppendGpuShadedQuad(
                bottomVertices[index],
                bottomVertices[next],
                topVertices[next],
                topVertices[index],
                fillColor,
                sideAmbient);
        }
    }

    private void DrawGpuGeneralPrismFast(
        IReadOnlyList<Vector3> bottomVertices,
        ReadOnlySpan<Vector3> topVertices,
        Color fillColor,
        float topAmbient,
        float sideAmbient)
    {
        if (bottomVertices.Count < 3 || topVertices.Length != bottomVertices.Count)
        {
            return;
        }

        AppendGpuShadedPolygon(topVertices, fillColor, topAmbient);
        for (int index = 0; index < bottomVertices.Count; index++)
        {
            int next = (index + 1) % bottomVertices.Count;
            AppendGpuShadedQuad(
                bottomVertices[index],
                bottomVertices[next],
                topVertices[next],
                topVertices[index],
                fillColor,
                sideAmbient);
        }
    }

    private void DrawGpuGeneralPrismFast(
        ReadOnlySpan<Vector3> bottomVertices,
        ReadOnlySpan<Vector3> topVertices,
        Color fillColor,
        float topAmbient,
        float sideAmbient)
    {
        if (bottomVertices.Length < 3 || topVertices.Length != bottomVertices.Length)
        {
            return;
        }

        AppendGpuShadedPolygon(topVertices, fillColor, topAmbient);
        for (int index = 0; index < bottomVertices.Length; index++)
        {
            int next = (index + 1) % bottomVertices.Length;
            AppendGpuShadedQuad(
                bottomVertices[index],
                bottomVertices[next],
                topVertices[next],
                topVertices[index],
                fillColor,
                sideAmbient);
        }
    }

    private void DrawGpuOrientedBoxFast(
        Vector3 center,
        Vector3 forwardDirection,
        Vector3 rightDirection,
        Vector3 upDirection,
        float length,
        float width,
        float height,
        Color fillColor)
    {
        Vector3 forward = forwardDirection.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(forwardDirection);
        Vector3 right = rightDirection - forward * Vector3.Dot(rightDirection, forward);
        if (right.LengthSquared() <= 1e-8f)
        {
            Vector3 fallback = Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) >= 0.92f ? Vector3.UnitZ : Vector3.UnitY;
            right = Vector3.Cross(fallback, forward);
        }

        right = Vector3.Normalize(right);
        Vector3 up = upDirection - forward * Vector3.Dot(upDirection, forward) - right * Vector3.Dot(upDirection, right);
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.Cross(right, forward);
        }

        up = Vector3.Normalize(up);
        Vector3 halfForward = forward * (length * 0.5f);
        Vector3 halfRight = right * (width * 0.5f);
        Vector3 halfUp = up * (height * 0.5f);

        Vector3 p000 = center - halfForward - halfRight - halfUp;
        Vector3 p100 = center + halfForward - halfRight - halfUp;
        Vector3 p110 = center + halfForward + halfRight - halfUp;
        Vector3 p010 = center - halfForward + halfRight - halfUp;
        Vector3 p001 = center - halfForward - halfRight + halfUp;
        Vector3 p101 = center + halfForward - halfRight + halfUp;
        Vector3 p111 = center + halfForward + halfRight + halfUp;
        Vector3 p011 = center - halfForward + halfRight + halfUp;

        AppendGpuShadedQuad(p001, p101, p111, p011, fillColor, 0.78f);
        AppendGpuShadedQuad(p000, p010, p110, p100, fillColor, 0.48f);
        AppendGpuShadedQuad(p100, p110, p111, p101, fillColor, 0.64f);
        AppendGpuShadedQuad(p000, p001, p011, p010, fillColor, 0.54f);
        AppendGpuShadedQuad(p010, p011, p111, p110, fillColor, 0.58f);
        AppendGpuShadedQuad(p000, p100, p101, p001, fillColor, 0.56f);
    }

    private void DrawGpuCylinderSolidFast(
        Vector3 center,
        Vector3 axisDirection,
        Vector3 radialHint,
        float radius,
        float halfLength,
        float spinRad,
        Color fillColor,
        int segmentCount)
    {
        Vector3 axis = axisDirection.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(axisDirection);
        Vector3 radialA = radialHint - axis * Vector3.Dot(radialHint, axis);
        if (radialA.LengthSquared() <= 1e-8f)
        {
            Vector3 fallback = Math.Abs(Vector3.Dot(axis, Vector3.UnitY)) >= 0.92f ? Vector3.UnitX : Vector3.UnitY;
            radialA = fallback - axis * Vector3.Dot(fallback, axis);
        }

        radialA = Vector3.Normalize(radialA);
        Vector3 radialB = Vector3.Normalize(Vector3.Cross(axis, radialA));
        Vector3 spunA = radialA * MathF.Cos(spinRad) + radialB * MathF.Sin(spinRad);
        Vector3 spunB = Vector3.Normalize(Vector3.Cross(axis, spunA));
        Vector3 capA = center - axis * halfLength;
        Vector3 capB = center + axis * halfLength;

        int segments = Math.Max(8, segmentCount);
        Span<Vector3> ringA = segments <= 32 ? stackalloc Vector3[segments] : new Vector3[segments];
        Span<Vector3> ringB = segments <= 32 ? stackalloc Vector3[segments] : new Vector3[segments];
        for (int index = 0; index < segments; index++)
        {
            float angle = index * MathF.Tau / segments;
            Vector3 radial = spunA * MathF.Cos(angle) + spunB * MathF.Sin(angle);
            ringA[index] = capA + radial * radius;
            ringB[index] = capB + radial * radius;
        }

        AppendGpuShadedPolygon(ringB, fillColor, 0.82f);
        AppendGpuShadedPolygonReversed(ringA, fillColor, 0.72f);
        for (int index = 0; index < segments; index++)
        {
            int next = (index + 1) % segments;
            AppendGpuShadedQuad(ringA[index], ringA[next], ringB[next], ringB[index], fillColor, 0.56f);
        }
    }

    private void DrawGpuHollowOctagonalBarrelFast(
        Vector3 center,
        Vector3 axisDirection,
        Vector3 rightHint,
        Vector3 upHint,
        float radius,
        float halfLength,
        Color fillColor)
    {
        Vector3 axis = axisDirection.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(axisDirection);
        Vector3 right = rightHint - axis * Vector3.Dot(rightHint, axis);
        if (right.LengthSquared() <= 1e-8f)
        {
            right = Vector3.Cross(Math.Abs(Vector3.Dot(axis, Vector3.UnitY)) > 0.92f ? Vector3.UnitZ : Vector3.UnitY, axis);
        }

        right = Vector3.Normalize(right);
        Vector3 up = upHint - axis * Vector3.Dot(upHint, axis) - right * Vector3.Dot(upHint, right);
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.Cross(axis, right);
        }

        up = Vector3.Normalize(up);
        float halfWidth = Math.Max(radius * 1.38f, radius + 0.004f);
        float halfHeight = Math.Max(radius * 1.10f, radius + 0.002f);
        float chamfer = Math.Min(halfWidth, halfHeight) * 0.34f;
        Span<Vector2> section = stackalloc Vector2[8]
        {
            new(-halfWidth + chamfer, -halfHeight),
            new(halfWidth - chamfer, -halfHeight),
            new(halfWidth, -halfHeight + chamfer),
            new(halfWidth, halfHeight - chamfer),
            new(halfWidth - chamfer, halfHeight),
            new(-halfWidth + chamfer, halfHeight),
            new(-halfWidth, halfHeight - chamfer),
            new(-halfWidth, -halfHeight + chamfer),
        };

        Vector3 rear = center - axis * halfLength;
        Vector3 muzzle = center + axis * halfLength;
        Span<Vector3> rearRing = stackalloc Vector3[8];
        Span<Vector3> muzzleRing = stackalloc Vector3[8];
        for (int index = 0; index < section.Length; index++)
        {
            Vector3 offset = right * section[index].X + up * section[index].Y;
            rearRing[index] = rear + offset;
            muzzleRing[index] = muzzle + offset;
        }

        AppendGpuShadedPolygon(muzzleRing, fillColor, 0.80f);
        AppendGpuShadedPolygonReversed(rearRing, fillColor, 0.58f);
        for (int index = 0; index < section.Length; index++)
        {
            int next = (index + 1) % section.Length;
            float ambient = index is 0 or 4 ? 0.72f : index is 2 or 6 ? 0.54f : 0.62f;
            AppendGpuShadedQuad(rearRing[index], rearRing[next], muzzleRing[next], muzzleRing[index], fillColor, ambient);
        }

        DrawGpuCylinderSolidFast(
            muzzle + axis * 0.004f,
            axis,
            up,
            Math.Max(0.004f, radius * 0.58f),
            0.004f,
            0f,
            Color.FromArgb(248, 8, 10, 14),
            18);
    }

    private void DrawGpuBeam3dFast(
        Vector3 start,
        Vector3 end,
        Vector3 lateralAxis,
        float height,
        float thickness,
        Color fillColor)
    {
        Vector3 axis = Vector3.Normalize(end - start);
        Vector3 side = lateralAxis.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(lateralAxis);
        Vector3 up = Vector3.Cross(side, axis);
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.Cross(axis, Vector3.UnitY);
        }

        if (up.LengthSquared() <= 1e-8f)
        {
            return;
        }

        up = Vector3.Normalize(up);
        Vector3 halfUp = up * Math.Max(0.001f, height * 0.5f);
        Vector3 halfSide = side * Math.Max(0.001f, thickness * 0.5f);

        Vector3 a = start + halfUp + halfSide;
        Vector3 b = end + halfUp + halfSide;
        Vector3 c = end - halfUp + halfSide;
        Vector3 d = start - halfUp + halfSide;
        Vector3 e = start + halfUp - halfSide;
        Vector3 f = end + halfUp - halfSide;
        Vector3 g = end - halfUp - halfSide;
        Vector3 h = start - halfUp - halfSide;

        AppendGpuShadedQuad(a, b, c, d, fillColor, 0.76f);
        AppendGpuShadedQuad(e, f, g, h, fillColor, 0.70f);
        AppendGpuShadedQuad(a, b, f, e, fillColor, 0.62f);
        AppendGpuShadedQuad(d, c, g, h, fillColor, 0.44f);
        AppendGpuShadedQuad(b, c, g, f, fillColor, 0.58f);
        AppendGpuShadedQuad(a, d, h, e, fillColor, 0.56f);
    }

    private void AppendGpuShadedPolygon(IReadOnlyList<Vector3> vertices, Color fillColor, float ambient)
    {
        if (vertices.Count < 3)
        {
            return;
        }

        Color shaded = ShadeFaceColorFast(fillColor, vertices[0], vertices[1], vertices[2], ambient);
        for (int index = 1; index < vertices.Count - 1; index++)
        {
            AppendOrDrawGpuTriangle(vertices[0], vertices[index], vertices[index + 1], shaded);
        }
    }

    private void AppendGpuShadedPolygon(ReadOnlySpan<Vector3> vertices, Color fillColor, float ambient)
    {
        if (vertices.Length < 3)
        {
            return;
        }

        Color shaded = ShadeFaceColorFast(fillColor, vertices[0], vertices[1], vertices[2], ambient);
        for (int index = 1; index < vertices.Length - 1; index++)
        {
            AppendOrDrawGpuTriangle(vertices[0], vertices[index], vertices[index + 1], shaded);
        }
    }

    private void AppendGpuShadedPolygonReversed(ReadOnlySpan<Vector3> vertices, Color fillColor, float ambient)
    {
        if (vertices.Length < 3)
        {
            return;
        }

        Color shaded = ShadeFaceColorFast(fillColor, vertices[0], vertices[vertices.Length - 1], vertices[vertices.Length - 2], ambient);
        for (int index = 1; index < vertices.Length - 1; index++)
        {
            AppendOrDrawGpuTriangle(vertices[0], vertices[index + 1], vertices[index], shaded);
        }
    }

    private void AppendGpuShadedQuad(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        Color fillColor,
        float ambient)
    {
        Color shaded = ShadeFaceColorFast(fillColor, a, b, c, ambient);
        AppendOrDrawGpuQuad(a, b, c, d, shaded);
    }

    private static Color ShadeFaceColorFast(Color color, Vector3 a, Vector3 bVertex, Vector3 c, float ambient)
    {
        Vector3 normal = Vector3.Cross(bVertex - a, c - a);
        if (normal.LengthSquared() <= 1e-8f)
        {
            return color;
        }

        normal = Vector3.Normalize(normal);
        Vector3 keyLight = Vector3.Normalize(new Vector3(-0.45f, 1.0f, -0.35f));
        Vector3 rimLight = Vector3.Normalize(new Vector3(0.55f, 0.72f, 0.48f));
        float keyDiffuse = MathF.Max(0f, Vector3.Dot(normal, keyLight));
        float rimDiffuse = MathF.Max(0f, Vector3.Dot(normal, rimLight));
        float diffuseFill = MathF.Abs(normal.Y) * 0.05f;
        float ambientFloor = MathF.Max(ambient, 0.48f);
        float brightness = Math.Clamp(
            ambientFloor + keyDiffuse * 0.32f + rimDiffuse * 0.13f + diffuseFill,
            0.44f,
            1.15f);
        Color lit = ScaleColor(color, brightness);
        float coolTint = Math.Clamp(keyDiffuse * 0.018f + rimDiffuse * 0.026f, 0f, 0.040f);
        if (coolTint <= 1e-5f)
        {
            return ApplyAmbientSceneLight(lit, 0.026f);
        }

        int r = Math.Clamp((int)MathF.Round(lit.R + (196 - lit.R) * coolTint * 0.72f), 0, 255);
        int g = Math.Clamp((int)MathF.Round(lit.G + (224 - lit.G) * coolTint), 0, 255);
        int bComponent = Math.Clamp((int)MathF.Round(lit.B + (255 - lit.B) * coolTint), 0, 255);
        return ApplyAmbientSceneLight(Color.FromArgb(lit.A, r, g, bComponent), 0.026f);
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
        Rectangle viewport = _projectionViewportRect ?? ClientRectangle;
        float x = viewport.X + (ndcX * 0.5f + 0.5f) * viewport.Width;
        float y = viewport.Y + (1f - (ndcY * 0.5f + 0.5f)) * viewport.Height;
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

    private void DrawProjectedFaceBatch(Graphics graphics, IReadOnlyList<ProjectedFace> faces, float edgeWidth)
    {
        if (faces.Count == 0)
        {
            return;
        }

        int edgeWidthBucket = Math.Clamp((int)MathF.Round(edgeWidth * 100f), 1, 1000);
        foreach (ProjectedFace face in faces)
        {
            int fillKey = face.FillColor.ToArgb();
            if (!_projectedFaceBrushCache.TryGetValue(fillKey, out SolidBrush? brush))
            {
                brush = new SolidBrush(face.FillColor);
                _projectedFaceBrushCache.Add(fillKey, brush);
            }

            int edgeKey = HashCode.Combine(face.EdgeColor.ToArgb(), edgeWidthBucket);
            if (!_projectedFacePenCache.TryGetValue(edgeKey, out Pen? pen))
            {
                pen = new Pen(face.EdgeColor, edgeWidth);
                _projectedFacePenCache.Add(edgeKey, pen);
            }

            graphics.FillPolygon(brush, face.Points);
            graphics.DrawPolygon(pen, face.Points);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    private static int ResolveDisplayRefreshRateHz()
    {
        try
        {
            const int CurrentSettings = -1;
            DevMode mode = new()
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (short)Marshal.SizeOf<DevMode>(),
            };
            if (EnumDisplaySettings(null, CurrentSettings, ref mode) && mode.dmDisplayFrequency >= 30)
            {
                return mode.dmDisplayFrequency;
            }
        }
        catch
        {
        }

        return 60;
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
        => ResolveRoleLabel(entity.RoleKey);

    private static string ResolveTeamName(string team)
    {
        if (team.Equals("red", StringComparison.OrdinalIgnoreCase))
        {
            return "\u7ea2\u65b9";
        }

        if (team.Equals("blue", StringComparison.OrdinalIgnoreCase))
        {
            return "\u84dd\u65b9";
        }

        if (team.Equals("neutral", StringComparison.OrdinalIgnoreCase))
        {
            return "中立";
        }

        return team;
    }

    private static string ResolveAutoAimAssistLabel(AutoAimAssistMode mode)
        => mode == AutoAimAssistMode.HardLock ? "\u786c\u9501" : "\u5f15\u5bfc";

    private static string ResolveAutoAimTargetModeHudLabel(SimulationEntity entity)
    {
        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && SimulationCombatMath.IsHeroLobAutoAimMode(entity))
        {
            return "吊射";
        }

        if (string.Equals(entity.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.AutoAimTargetKind, "energy_disk", StringComparison.OrdinalIgnoreCase))
        {
            return "能量机关";
        }

        if (string.Equals(entity.AutoAimTargetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase))
        {
            return "前哨站";
        }

        if (string.Equals(entity.AutoAimTargetKind, "base_armor", StringComparison.OrdinalIgnoreCase))
        {
            return "基地";
        }

        return "装甲板";
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
            "flank" => "侧袭",
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

    private bool IsHeroLobModeActive(SimulationEntity entity)
    {
        return string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && SimulationCombatMath.IsHeroLobAutoAimMode(entity);
    }

    private static bool IsHeroLobStructureTargetKind(string? targetKind)
    {
        return string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetKind, "base_armor", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetAutoAimProjectedPoint(SimulationEntity entity, out PointF projectedAim)
    {
        projectedAim = default;
        if (!entity.AutoAimLocked || entity.AutoAimAimPointHeightM <= 1e-6)
        {
            return false;
        }

        Vector3 aimPoint = ToScenePoint(entity.AutoAimAimPointX, entity.AutoAimAimPointY, (float)entity.AutoAimAimPointHeightM);
        return TryProject(aimPoint, out projectedAim, out _);
    }

    private bool IsHeroLobReticleAlignedForAutoFire(SimulationEntity entity)
    {
        if (!IsHeroLobModeActive(entity)
            || !entity.AutoAimLocked
            || string.IsNullOrWhiteSpace(entity.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(entity.AutoAimPlateId)
            || !IsHeroLobStructureTargetKind(entity.AutoAimTargetKind))
        {
            return false;
        }

        PointF screenCenter = new(ClientSize.Width * 0.5f, ClientSize.Height * 0.5f);
        if (TryGetHeroLobCalibrationPreviewCached(entity, out HeroLobCalibrationPreview preview, includeFireWindowSuggestion: false))
        {
            if (!preview.FireWindowReady)
            {
                return false;
            }

            float centerHorizontalToleranceM = Math.Max(0.040f, preview.PlateWidthM * 0.28f);
            float centerVerticalToleranceM = Math.Max(0.040f, preview.PlateHeightM * 0.30f);
            float depthToleranceM = 0.14f + Math.Clamp((float)entity.AutoAimLeadDistanceM * 0.020f, 0f, 0.16f);
            bool impactNearCenter = preview.CrossedPlatePlane
                && MathF.Abs(preview.HorizontalOffsetM) <= centerHorizontalToleranceM
                && MathF.Abs(preview.VerticalOffsetM) <= centerVerticalToleranceM
                && MathF.Abs(preview.DepthOffsetM) <= depthToleranceM;
            if (!impactNearCenter)
            {
                return false;
            }

            if (TryGetProjectedHeroLobPlatePolygon(entity, out PointF[] projectedPlateForWindow))
            {
                float centerMarginPx = Math.Clamp(
                    24f + (float)entity.AutoAimLeadDistanceM * 1.7f,
                    24f,
                    _firstPersonView ? 54f : 64f);
                return IsPointInsideOrNearConvexPolygon(screenCenter, projectedPlateForWindow, centerMarginPx);
            }

            if (TryGetAutoAimProjectedPoint(entity, out PointF projectedAimForWindow))
            {
                float dx = projectedAimForWindow.X - screenCenter.X;
                float dy = projectedAimForWindow.Y - screenCenter.Y;
                float previewFallbackThresholdPx = _firstPersonView ? 42f : 54f;
                return dx * dx + dy * dy <= previewFallbackThresholdPx * previewFallbackThresholdPx;
            }

            return true;
        }

        if (TryGetProjectedHeroLobPlatePolygon(entity, out PointF[] projectedPlate))
        {
            float longestEdge = 0f;
            for (int index = 0; index < projectedPlate.Length; index++)
            {
                PointF a = projectedPlate[index];
                PointF b = projectedPlate[(index + 1) % projectedPlate.Length];
                float dx = b.X - a.X;
                float dy = b.Y - a.Y;
                longestEdge = Math.Max(longestEdge, MathF.Sqrt(dx * dx + dy * dy));
            }

            float overlapMarginPx = Math.Clamp(
                longestEdge * 0.28f
                + (_firstPersonView ? 22f : 30f)
                + Math.Clamp((float)entity.AutoAimLeadDistanceM * 1.3f, 0f, 18f),
                _firstPersonView ? 28f : 38f,
                _firstPersonView ? 58f : 72f);
            return IsPointInsideOrNearConvexPolygon(screenCenter, projectedPlate, overlapMarginPx);
        }

        if (!TryGetAutoAimProjectedPoint(entity, out PointF projectedAim))
        {
            return false;
        }

        float fallbackDx = projectedAim.X - screenCenter.X;
        float fallbackDy = projectedAim.Y - screenCenter.Y;
        float fallbackThresholdPx = (_firstPersonView ? 42f : 54f)
            + (entity.HeroDeploymentActive ? 10f : 0f)
            + Math.Clamp((float)entity.AutoAimLeadDistanceM * 1.2f, 0f, 16f);
        return fallbackDx * fallbackDx + fallbackDy * fallbackDy <= fallbackThresholdPx * fallbackThresholdPx;
    }

    private bool TryGetProjectedHeroLobPlatePolygon(SimulationEntity shooter, out PointF[] polygon)
    {
        polygon = Array.Empty<PointF>();
        if (string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return false;
        }

        SimulationEntity? target = _host.World.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        double predictedPlateTimeSec = _host.World.GameTimeSec + Math.Max(0.0, shooter.AutoAimLeadTimeSec);
        if (target is null
            || !TryResolveVisualArmorPlatePose(target, shooter.AutoAimPlateId, predictedPlateTimeSec, out VisualArmorPlatePose visualPlate))
        {
            return false;
        }

        Vector3 p1 = visualPlate.Center + visualPlate.Right * visualPlate.HalfSide + visualPlate.Up * visualPlate.HalfSide;
        Vector3 p2 = visualPlate.Center - visualPlate.Right * visualPlate.HalfSide + visualPlate.Up * visualPlate.HalfSide;
        Vector3 p3 = visualPlate.Center - visualPlate.Right * visualPlate.HalfSide - visualPlate.Up * visualPlate.HalfSide;
        Vector3 p4 = visualPlate.Center + visualPlate.Right * visualPlate.HalfSide - visualPlate.Up * visualPlate.HalfSide;
        if (!TryProject(p1, out PointF s1, out _)
            || !TryProject(p2, out PointF s2, out _)
            || !TryProject(p3, out PointF s3, out _)
            || !TryProject(p4, out PointF s4, out _))
        {
            return false;
        }

        polygon = new[] { s1, s2, s3, s4 };
        return true;
    }

    private static bool IsPointInsideOrNearConvexPolygon(PointF point, PointF[] polygon, float marginPx)
    {
        if (polygon.Length < 3)
        {
            return false;
        }

        if (IsPointInsideConvexPolygon(point, polygon))
        {
            return true;
        }

        float marginSq = marginPx * marginPx;
        for (int index = 0; index < polygon.Length; index++)
        {
            PointF a = polygon[index];
            PointF b = polygon[(index + 1) % polygon.Length];
            if (DistancePointToSegmentSquared(point, a, b) <= marginSq)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPointInsideConvexPolygon(PointF point, PointF[] polygon)
    {
        float sign = 0f;
        for (int index = 0; index < polygon.Length; index++)
        {
            PointF a = polygon[index];
            PointF b = polygon[(index + 1) % polygon.Length];
            float cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
            if (MathF.Abs(cross) <= 1e-3f)
            {
                continue;
            }

            float currentSign = MathF.Sign(cross);
            if (MathF.Abs(sign) <= 1e-3f)
            {
                sign = currentSign;
                continue;
            }

            if (currentSign != sign)
            {
                return false;
            }
        }

        return true;
    }

    private static float DistancePointToSegmentSquared(PointF point, PointF start, PointF end)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float lengthSq = dx * dx + dy * dy;
        if (lengthSq <= 1e-6f)
        {
            float px = point.X - start.X;
            float py = point.Y - start.Y;
            return px * px + py * py;
        }

        float t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSq;
        t = Math.Clamp(t, 0f, 1f);
        float closestX = start.X + dx * t;
        float closestY = start.Y + dy * t;
        float diffX = point.X - closestX;
        float diffY = point.Y - closestY;
        return diffX * diffX + diffY * diffY;
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
        bool energyActivationPressed = IsAnyKeyHeld(Keys.F);
        bool heroDeployHoldPressed = IsAnyKeyHeld(Keys.Z);
        bool superCapActive = IsAnyKeyHeld(Keys.C);
        bool sentryStanceToggleRequested = ConsumePressedInput(Keys.X, 0x58, ref _sentryStanceKeyWasDown);
        bool smallGyroActive = IsAnyKeyHeld(Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey);
        bool heroDeployActive = selected.HeroDeploymentActive;
        bool heroLobMode = IsHeroLobModeActive(selected);
        bool heroLobGuidanceActive = heroLobMode
            && _autoAimPressed
            && _autoAimAssistMode == AutoAimAssistMode.GuidanceOnly
            && !heroDeployActive;
        bool heroLobAutoControlActive = heroLobMode
            && (heroDeployActive || (_autoAimPressed && _autoAimAssistMode == AutoAimAssistMode.HardLock));
        bool heroLobAutoFireReady = heroLobAutoControlActive && IsHeroLobReticleAlignedForAutoFire(selected);
        bool energyAutoAimSingleShot =
            string.Equals(selected.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(selected.RoleKey, "hero", StringComparison.OrdinalIgnoreCase);
        bool manualFirePressed = energyAutoAimSingleShot
            ? _pendingSingleFireRequest
            : _firePressed;
        bool heroLobManualFireAllowed = heroLobMode && (!heroDeployActive && (!_autoAimPressed || heroLobGuidanceActive));
        bool firePressed = heroLobMode
            ? (heroLobAutoFireReady || (heroLobManualFireAllowed && manualFirePressed))
            : (heroDeployActive || manualFirePressed);
        _pendingSingleFireRequest = false;
        if (heroDeployActive)
        {
            moveForward = 0.0;
            moveRight = 0.0;
            smallGyroActive = false;
        }

        bool freezeHeroLobYaw = heroLobAutoControlActive
            && selected.AutoAimLocked
            && IsHeroLobStructureTargetKind(selected.AutoAimTargetKind);
        bool freezeHeroLobPitch = heroLobAutoControlActive
            && !heroLobGuidanceActive
            && selected.AutoAimLocked
            && IsHeroLobStructureTargetKind(selected.AutoAimTargetKind);
        if (freezeHeroLobYaw)
        {
            turretYawDelta = 0.0;
        }

        if (freezeHeroLobPitch)
        {
            gimbalPitchDelta = 0.0;
        }

        bool enabled = !_observerMode
            && !_sharedHostSimulation
            && !_fineTerrainInMatchEditMode
            && selected.IsAlive
            && (forceEnable || (_appState == SimulatorAppState.InMatch && !_paused && !_tacticalMode));
        if (!selected.IsAlive)
        {
            moveForward = 0.0;
            moveRight = 0.0;
            turretYawDelta = 0.0;
            gimbalPitchDelta = 0.0;
            firePressed = false;
            smallGyroActive = false;
            superCapActive = false;
        }

        return new PlayerControlState
        {
            EntityId = selected.Id,
            Enabled = enabled,
            MoveForward = moveForward,
            MoveRight = moveRight,
            TurretYawDeltaDeg = turretYawDelta,
            GimbalPitchDeltaDeg = gimbalPitchDelta,
            FirePressed = firePressed,
            AutoAimPressed = selected.IsAlive && (heroDeployActive || _autoAimPressed),
            AutoAimGuidanceOnly = _autoAimAssistMode == AutoAimAssistMode.GuidanceOnly,
            JumpRequested = jumpRequested,
            StepClimbModeActive = false,
            SmallGyroActive = smallGyroActive,
            BuyAmmoRequested = buyAmmoRequested,
            EnergyActivationPressed = energyActivationPressed,
            HeroDeployToggleRequested = false,
            HeroDeployHoldPressed = heroDeployHoldPressed,
            SuperCapActive = superCapActive,
            SentryStanceToggleRequested = sentryStanceToggleRequested,
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
        _pendingSingleFireRequest = false;
        _spaceKeyWasDown = false;
        _buyKeyWasDown = false;
        _sentryStanceKeyWasDown = false;
        _pendingMouseYawDeltaDeg = 0f;
        _pendingMousePitchDeltaDeg = 0f;
    }

    private void UpdateMouseCaptureState()
    {
        if (_externallyDrivenCompatibilityMode)
        {
            _mouseCaptureActive = false;
            return;
        }

        bool shouldCapture =
            _appState == SimulatorAppState.InMatch
            && !_paused
            && !_tacticalMode
            && !_observerPinned
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
        if (_externallyDrivenCompatibilityMode)
        {
            _mouseCaptureActive = false;
            return;
        }

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
        if (_host.LastReport is null)
        {
            return;
        }

        var entityById = new Dictionary<string, SimulationEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            entityById[entity.Id] = entity;
        }

        foreach (SimulationCombatEvent combatEvent in _host.LastReport.CombatEvents)
        {
            if (!combatEvent.Hit)
            {
                continue;
            }

            entityById.TryGetValue(combatEvent.TargetId, out SimulationEntity? target);
            bool energyMechanismHit =
                string.Equals(target?.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
                || combatEvent.PlateId.StartsWith("energy_", StringComparison.OrdinalIgnoreCase);
            if (energyMechanismHit)
            {
                continue;
            }

            Color tint = combatEvent.CriticalHit
                ? Color.FromArgb(255, 236, 188, 62)
                : combatEvent.DamagePrevented
                    ? Color.FromArgb(224, 196, 196, 196)
                : target is null
                    ? Color.FromArgb(244, 255, 214, 84)
                    : ResolveTeamColor(target.Team);
            int markerSlot = _combatMarkers.Count % 7;
            float markerOffsetX = (markerSlot - 3) * 18f;
            float markerOffsetY = -Math.Abs(markerSlot - 3) * 2.5f;
            float markerRiseSpeed = 0.18f + (markerSlot % 3) * 0.035f;
            _combatMarkers.Add(new FloatingCombatMarker(
                combatEvent.TargetId,
                target?.X ?? 0.0,
                target?.Y ?? 0.0,
                (target?.GroundHeightM ?? 0.0) + (target?.AirborneHeightM ?? 0.0) + 0.9,
                combatEvent.Damage <= 1e-6 ? "-0HP" : $"-{combatEvent.Damage:0.##}HP",
                Color.FromArgb(246, tint),
                0.70f,
                markerOffsetX,
                markerOffsetY,
                markerRiseSpeed));

            entityById.TryGetValue(combatEvent.ShooterId, out SimulationEntity? shooter);
            Color eventColor = combatEvent.CriticalHit
                ? Color.FromArgb(255, 236, 188, 62)
                : shooter?.IsPlayerControlled == true
                    ? Color.FromArgb(255, 246, 216, 72)
                    : ResolveTeamColor(shooter?.Team ?? target?.Team ?? "neutral");
            string targetLabel = target is null ? combatEvent.TargetId : FormatEventEntityName(target);
            string shooterLabel = shooter is null ? combatEvent.ShooterId : FormatEventEntityName(shooter);
            string combatText = combatEvent.DamagePrevented
                ? $"{shooterLabel} 命中 {targetLabel}  生命 -0  {ResolveDamagePreventedLabel(combatEvent.DamagePreventedReason)}"
                : combatEvent.CriticalHit
                    ? $"{shooterLabel} 暴击 {targetLabel}  生命 -{combatEvent.Damage:0.##}  (150%)"
                    : $"{shooterLabel} 命中 {targetLabel}  生命 -{combatEvent.Damage:0.##}";
            AppendMatchEvent(combatText, eventColor);
            TrackStructurePlateFlash(combatEvent);
        }

        foreach (SimulationShotEvent shotEvent in _host.LastReport.ShotEvents)
        {
            string fireLine =
                $"{DateTime.Now:HH:mm:ss.fff} {shotEvent.ShooterId} team={shotEvent.Team} ammo={shotEvent.AmmoType} auto={(shotEvent.AutoAim ? 1 : 0)} player={(shotEvent.PlayerControlled ? 1 : 0)} target={shotEvent.TargetId} plate={shotEvent.PlateId}";
            AppendGameplayLog("fire_events.log", fireLine);
            AppendAutoAimCompensationLog(entityById, shotEvent);
        }

        if (_combatMarkers.Count > 24)
        {
            _combatMarkers.RemoveRange(0, _combatMarkers.Count - 24);
        }

        foreach (SimulationLifecycleEvent lifecycleEvent in _host.LastReport.LifecycleEvents)
        {
            entityById.TryGetValue(lifecycleEvent.EntityId, out SimulationEntity? entity);
            Color eventColor = ResolveTeamColor(entity?.Team ?? lifecycleEvent.Team);
            string prefix = lifecycleEvent.EventType switch
            {
                "respawn" => "复活",
                "destroyed" => "摧毁",
                "death" => "击倒",
                _ => "事件",
            };
            AppendMatchEvent($"{prefix}  {FormatLifecycleEventText(lifecycleEvent, entityById)}", eventColor, 8.5f);
        }

        foreach (FacilityInteractionEvent interactionEvent in _host.LastReport.InteractionEvents)
        {
            bool suppressEnergyProgressFeed =
                string.Equals(interactionEvent.FacilityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
                && (interactionEvent.Message.StartsWith("\u547d\u4e2d\u80fd\u91cf\u673a\u5173", StringComparison.OrdinalIgnoreCase)
                    || interactionEvent.Message.Contains("\u8be5\u5706\u76d8\u5df2\u8ba1\u5165\u8fdb\u5ea6", StringComparison.OrdinalIgnoreCase));
            if (suppressEnergyProgressFeed)
            {
                continue;
            }

            Color eventColor = ResolveTeamColor(interactionEvent.Team);
            string eventText = interactionEvent.Message;
            if (string.Equals(interactionEvent.FacilityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                eventText = $"能量机关  {interactionEvent.Message}";
            }

            AppendMatchEvent(eventText, eventColor, 7.0f);
            bool sameTeamEnergyEvent =
                string.Equals(interactionEvent.Team, _host.SelectedTeam, StringComparison.OrdinalIgnoreCase);
            if (sameTeamEnergyEvent
                && string.Equals(interactionEvent.FacilityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                if (interactionEvent.Message.Contains("\u5c0f\u80fd\u91cf\u673a\u5173\u5f00\u59cb\u6fc0\u6d3b", StringComparison.OrdinalIgnoreCase))
                {
                    _centerBuffToasts.Add(new CenterBuffToast(
                        "\u5c0f\u80fd\u91cf\u673a\u5173\u5df2\u5f00\u542f",
                        "\u4ec5\u53ef\u9501\u5b9a\u5df1\u65b9\u4eae\u706f\u76ee\u6807\uff0c\u5de6\u952e\u73b0\u5728\u4e3a\u5355\u53d1",
                        eventColor));
                }
                else if (interactionEvent.Message.Contains("\u5927\u80fd\u91cf\u673a\u5173\u5f00\u59cb\u6fc0\u6d3b", StringComparison.OrdinalIgnoreCase))
                {
                    _centerBuffToasts.Add(new CenterBuffToast(
                        "\u5927\u80fd\u91cf\u673a\u5173\u5df2\u5f00\u542f",
                        "\u8bf7\u4f9d\u6b21\u547d\u4e2d\u5df1\u65b9\u4eae\u706f\u5706\u73af",
                        eventColor));
                }
                else if (interactionEvent.Message.Contains("\u5df2\u6fc0\u6d3b\u6210\u529f", StringComparison.OrdinalIgnoreCase))
                {
                    _centerBuffToasts.Add(new CenterBuffToast(
                        interactionEvent.Message.Contains("\u5927\u80fd\u91cf", StringComparison.OrdinalIgnoreCase)
                            ? "\u5927\u80fd\u91cf\u673a\u5173\u5df2\u6fc0\u6d3b"
                            : "\u5c0f\u80fd\u91cf\u673a\u5173\u5df2\u6fc0\u6d3b",
                        interactionEvent.Message,
                        eventColor));
                }
                else if (interactionEvent.Message.Contains("\u6fc0\u6d3b\u5931\u8d25", StringComparison.OrdinalIgnoreCase))
                {
                    _centerBuffToasts.Add(new CenterBuffToast(
                        "\u80fd\u91cf\u673a\u5173\u6fc0\u6d3b\u5931\u8d25",
                        interactionEvent.Message,
                        Color.FromArgb(255, 192, 112, 96)));
                }
            }
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

    private void CapturePowerCutNotifications()
    {
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            activeIds.Add(entity.Id);
            _powerCutSnapshot.TryGetValue(entity.Id, out double previousTimerSec);
            double currentTimerSec = Math.Max(0.0, entity.PowerCutTimerSec);
            _powerCutSnapshot[entity.Id] = currentTimerSec;
            if (currentTimerSec <= 1e-3 || previousTimerSec > 1e-3)
            {
                continue;
            }

            Color teamColor = ResolveTeamColor(entity.Team);
            AppendMatchEvent($"{FormatEventEntityName(entity)} 底盘超功率违规，断电 5s", teamColor, 8.0f);
        }

        List<string> staleIds = _powerCutSnapshot.Keys.Where(id => !activeIds.Contains(id)).ToList();
        foreach (string staleId in staleIds)
        {
            _powerCutSnapshot.Remove(staleId);
        }
    }

    private void CaptureExperienceGainNotifications()
    {
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            double gainedExperience = Math.Max(0.0, entity.PendingExperienceDisplay);
            int levelUps = Math.Max(0, entity.PendingLevelUpCount);
            if (gainedExperience <= 1e-3 && levelUps <= 0)
            {
                continue;
            }

            Color teamColor = ResolveTeamColor(entity.Team);
            if (gainedExperience > 1e-3)
            {
                _combatMarkers.Add(new FloatingCombatMarker(
                    entity.Id,
                    entity.X,
                    entity.Y,
                    entity.GroundHeightM + entity.AirborneHeightM + 1.15,
                    $"经验 +{gainedExperience:0}",
                    Color.FromArgb(248, 255, 214, 92),
                    0.95f,
                    screenOffsetX: 12f,
                    screenOffsetY: -18f,
                    riseSpeed: 0.24f));

                if (_host.SelectedEntity is not null
                    && string.Equals(_host.SelectedEntity.Id, entity.Id, StringComparison.OrdinalIgnoreCase))
                {
                    AppendMatchEvent($"{FormatEventEntityName(entity)} 获得经验 +{gainedExperience:0}", teamColor, 3.5f);
                }
            }

            if (levelUps > 0)
            {
                _centerBuffToasts.Add(new CenterBuffToast(
                    "等级提升",
                    $"{FormatEventEntityName(entity)}  等级 {Math.Max(1, entity.Level)}",
                    teamColor,
                    2.8f));
            }

            entity.PendingExperienceDisplay = 0.0;
            entity.PendingLevelUpCount = 0;
        }
    }

    private static string FormatLifecycleEventText(
        SimulationLifecycleEvent lifecycleEvent,
        IReadOnlyDictionary<string, SimulationEntity> entityById)
    {
        string targetLabel = entityById.TryGetValue(lifecycleEvent.EntityId, out SimulationEntity? target)
            ? FormatEventEntityName(target)
            : lifecycleEvent.EntityId;
        string message = lifecycleEvent.Message ?? string.Empty;
        if (message.Contains(" eliminated ", StringComparison.OrdinalIgnoreCase)
            || message.Contains(" destroyed ", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                string shooterLabel = entityById.TryGetValue(parts[0], out SimulationEntity? shooter)
                    ? FormatEventEntityName(shooter)
                    : parts[0];
                return $"{shooterLabel} 击毁 {targetLabel}";
            }
        }

        if (message.EndsWith(" respawned", StringComparison.OrdinalIgnoreCase))
        {
            return $"{targetLabel} 已复活";
        }

        return targetLabel;
    }

    private void AppendMatchEvent(string text, Color color, float lifetimeSec = 7.5f)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _matchEventFeed.Add(new MatchEventFeedItem(text.Trim(), color, lifetimeSec));
        AppendGameplayLog("match_event_feed.log", $"{DateTime.Now:HH:mm:ss.fff} {text.Trim()}");
        if (_matchEventFeed.Count > 9)
        {
            _matchEventFeed.RemoveRange(0, _matchEventFeed.Count - 9);
        }
    }

    private void TrackStructurePlateFlash(SimulationCombatEvent combatEvent)
    {
        if (!combatEvent.Hit
            || string.IsNullOrWhiteSpace(combatEvent.TargetId)
            || string.IsNullOrWhiteSpace(combatEvent.PlateId)
            || (!combatEvent.PlateId.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(combatEvent.PlateId, "outpost_top", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(combatEvent.PlateId, "base_top_slide", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string key = $"{combatEvent.TargetId}:{combatEvent.PlateId}";
        double flashEndSec = _host.World.GameTimeSec + 0.96;
        if (_outpostPlateFlashEndTimes.TryGetValue(key, out double existing))
        {
            _outpostPlateFlashEndTimes[key] = Math.Max(existing, flashEndSec);
            return;
        }

        _outpostPlateFlashEndTimes.Add(key, flashEndSec);
    }

    private bool IsStructurePlateFlashActive(string targetId, string plateId, out float intensity)
    {
        intensity = 0f;
        string key = $"{targetId}:{plateId}";
        if (!_outpostPlateFlashEndTimes.TryGetValue(key, out double flashEndSec))
        {
            return false;
        }

        double remainingSec = flashEndSec - _host.World.GameTimeSec;
        if (remainingSec <= 1e-6)
        {
            _outpostPlateFlashEndTimes.Remove(key);
            return false;
        }

        const float totalDurationSec = 0.96f;
        float elapsedSec = totalDurationSec - Math.Clamp((float)remainingSec, 0f, totalDurationSec);
        float phaseDurationSec = totalDurationSec / 6f;
        int phaseIndex = Math.Clamp((int)(elapsedSec / Math.Max(phaseDurationSec, 1e-6f)), 0, 5);
        intensity = phaseIndex is 0 or 2 or 4 ? 1f : 0f;
        return true;
    }

    private static string ResolveDamagePreventedLabel(string reason)
        => reason switch
        {
            "invincible" => "无敌",
            "base_protected" => "基地无敌",
            _ => "免伤",
        };

    private static void AppendGameplayLog(string fileName, string line)
    {
        SimulatorRuntimeLog.Append(fileName, line);
    }

    private void AppendAutoAimCompensationLog(
        IReadOnlyDictionary<string, SimulationEntity> entityById,
        SimulationShotEvent shotEvent)
    {
        if (!shotEvent.AutoAim
            || string.IsNullOrWhiteSpace(shotEvent.TargetId)
            || string.IsNullOrWhiteSpace(shotEvent.PlateId)
            || !entityById.TryGetValue(shotEvent.ShooterId, out SimulationEntity? shooter)
            || !entityById.TryGetValue(shotEvent.TargetId, out SimulationEntity? target)
            || !TryResolveAutoAimPlate(shooter, target, shotEvent.PlateId, out ArmorPlateTarget plate))
        {
            return;
        }

        AutoAimCompensationProfile compensation = SimulationCombatMath.ResolveAutoAimCompensationProfile(_host.World, shooter, target, plate);
        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double distanceM = Math.Sqrt(
            Math.Pow(plate.X - shooter.X, 2)
            + Math.Pow(plate.Y - shooter.Y, 2)) * metersPerWorldUnit;
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} shooter={shotEvent.ShooterId} ammo={shotEvent.AmmoType} target={shotEvent.TargetId} plate={shotEvent.PlateId} kind={SimulationCombatMath.ResolveAutoAimTargetKind(target, plate)} distance_m={distanceM:0.000} lead_s={shooter.AutoAimLeadTimeSec:0.000} lead_m={shooter.AutoAimLeadDistanceM:0.000} obs_v=({shooter.AutoAimObservedVelocityXMps:0.000},{shooter.AutoAimObservedVelocityYMps:0.000},{shooter.AutoAimObservedVelocityZMps:0.000}) obs_omega={shooter.AutoAimObservedAngularVelocityRadPerSec:0.000} profile={compensation.Name} trans_scale={compensation.TranslationLeadScale:0.000} ang_scale={compensation.AngularLeadScale:0.000} time_bias_s={compensation.TimeBiasSec:+0.000;-0.000;0.000}";
        AppendGameplayLog("autoaim_compensation.log", line);
    }

    private void AdvanceMatchEventFeed(float deltaSec)
    {
        if (_matchEventFeed.Count == 0)
        {
            return;
        }

        for (int index = _matchEventFeed.Count - 1; index >= 0; index--)
        {
            MatchEventFeedItem item = _matchEventFeed[index];
            item.AgeSec += Math.Max(0f, deltaSec);
            if (item.AgeSec >= item.LifetimeSec)
            {
                _matchEventFeed.RemoveAt(index);
            }
        }
    }

    private void DrawMatchEventFeed(Graphics graphics)
    {
        if (_matchEventFeed.Count == 0)
        {
            return;
        }

        int width = Math.Min(460, Math.Max(320, ClientSize.Width / 3));
        int visibleCount = Math.Min(5, _matchEventFeed.Count);
        int start = Math.Max(0, _matchEventFeed.Count - visibleCount);
        var wrappedItems = new List<(MatchEventFeedItem Item, IReadOnlyList<string> Lines)>(visibleCount);
        int contentHeight = 30;
        for (int index = start; index < _matchEventFeed.Count; index++)
        {
            MatchEventFeedItem item = _matchEventFeed[index];
            IReadOnlyList<string> lines = WrapEventText(graphics, item.Text, _tinyHudFont, width - 24);
            wrappedItems.Add((item, lines));
            contentHeight += lines.Count * 16 + 6;
        }

        Rectangle panel = new(
            ClientSize.Width - width - 18,
            ToolbarHeight + HudHeight + 12,
            width,
            contentHeight);
        using GraphicsPath path = CreateRoundedRectangle(panel, 7);
        using var fill = new SolidBrush(Color.FromArgb(184, 12, 18, 26));
        using var border = new Pen(Color.FromArgb(130, 126, 146, 168), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var titleBrush = new SolidBrush(Color.FromArgb(232, 238, 244));
        graphics.DrawString("\u4e8b\u4ef6", _smallHudFont, titleBrush, panel.X + 12, panel.Y + 7);

        int y = panel.Y + 28;
        foreach ((MatchEventFeedItem item, IReadOnlyList<string> lines) in wrappedItems)
        {
            float fade = Math.Clamp(1.0f - item.AgeSec / Math.Max(0.1f, item.LifetimeSec), 0.18f, 1.0f);
            Color color = Color.FromArgb(
                Math.Clamp((int)(fade * 245f), 70, 245),
                item.Color.R,
                item.Color.G,
                item.Color.B);
            using var brush = new SolidBrush(color);
            foreach (string line in lines)
            {
                graphics.DrawString(line, _tinyHudFont, brush, panel.X + 12, y);
                y += 16;
            }

            y += 6;
        }
    }

    private static IReadOnlyList<string> WrapEventText(Graphics graphics, string text, Font font, int maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new[] { string.Empty };
        }

        maxWidth = Math.Max(80, maxWidth);
        var lines = new List<string>();
        string current = string.Empty;
        foreach (char character in text)
        {
            string candidate = current + character;
            if (!string.IsNullOrEmpty(current)
                && graphics.MeasureString(candidate, font).Width > maxWidth)
            {
                lines.Add(current.TrimEnd());
                current = character.ToString();
            }
            else
            {
                current = candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            lines.Add(current.TrimEnd());
        }

        return lines.Count == 0 ? new[] { text } : lines;
    }

    private static string TrimEventText(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
        {
            return text;
        }

        return text[..Math.Max(1, maxChars - 1)] + "...";
    }

    private static string FormatEventEntityName(SimulationEntity entity)
    {
        if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            return $"{ResolveTeamName(entity.Team)}基地";
        }

        if (string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            return $"{ResolveTeamName(entity.Team)}前哨站";
        }

        if (string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return "能量机关";
        }

        if (string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            string role = ResolveRoleLabel(entity);
            string number = ResolveRobotNumberSuffix(entity.Id);
            return string.IsNullOrWhiteSpace(number)
                ? $"{ResolveTeamName(entity.Team)}{role}"
                : $"{ResolveTeamName(entity.Team)}{number}号{role}";
        }

        return entity.Id;
    }

    private static string ResolveRobotNumberSuffix(string entityId)
    {
        int underscore = entityId.LastIndexOf('_');
        if (underscore >= 0
            && underscore < entityId.Length - 1
            && int.TryParse(entityId[(underscore + 1)..], out int number))
        {
            return number.ToString();
        }

        return string.Empty;
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
                : target.GroundHeightM + target.AirborneHeightM + Math.Max(target.BodyHeightM, 0.30) + 0.55 + marker.AgeSec * marker.RiseSpeed;

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
            graphics.DrawString(
                marker.Text,
                _smallHudFont,
                textBrush,
                screenPoint.X - size.Width * 0.5f + marker.ScreenOffsetX,
                screenPoint.Y - size.Height * 0.5f + marker.ScreenOffsetY);
        }
    }
}
