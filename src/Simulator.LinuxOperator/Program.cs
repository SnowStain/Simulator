using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Text.Json.Nodes;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;
using NumericsVector3 = System.Numerics.Vector3;
using TkKeys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;
using TkMouseButton = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;

namespace Simulator.LinuxOperator;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            LinuxOperatorOptions options = LinuxOperatorOptions.Parse(args);
            ProjectLayout layout = ProjectLayout.Discover();
            var configService = new ConfigurationService();
            var mapPresetService = new MapPresetService();
            var ruleLoader = new RuleSetLoader();
            string configPath = configService.ResolvePrimaryConfigPath(layout);
            var config = configService.LoadConfig(configPath);
            string preset = string.IsNullOrWhiteSpace(options.Preset)
                ? mapPresetService.ResolvePresetName(layout, configService)
                : options.Preset;
            MapPresetDefinition mapPreset = mapPresetService.LoadPreset(layout, preset);
            RuleSet rules = ruleLoader.LoadFromConfig(config);
            var bootstrap = new SimulationBootstrapService();
            SimulationWorldState world = bootstrap.BuildInitialWorld(config, rules, mapPreset);
            if (world.Entities.Count == 0)
            {
                throw new InvalidOperationException($"Map preset '{preset}' produced no controllable entities.");
            }

            var windowSettings = new GameWindowSettings
            {
                UpdateFrequency = 120.0,
            };
            var nativeSettings = new NativeWindowSettings
            {
                Title = $"RMUC 2026 Linux Operator - {preset}",
                ClientSize = new Vector2i(options.Width, options.Height),
                StartFocused = options.LockMouseOnStart,
                StartVisible = true,
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
            };

            using var window = new LinuxOperatorWindow(
                windowSettings,
                nativeSettings,
                world,
                config,
                mapPreset,
                rules,
                options);
            window.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Linux operator simulator failed: {ex.Message}");
            return 1;
        }
    }
}

internal sealed record LinuxOperatorOptions(
    string Preset,
    string PlayerEntityId,
    string PlayerTeam,
    string PlayerEntityKey,
    int SpawnPointIndex,
    int Width,
    int Height,
    double DurationSec,
    bool EnergyTest,
    bool EnergyLargeTest,
    bool LockMouseOnStart,
    bool StartInMatch,
    double StartTimeSec)
{
    public static LinuxOperatorOptions Parse(IReadOnlyList<string> args)
    {
        string preset = "rmuc2026";
        string player = string.Empty;
        string playerTeam = "red";
        string playerEntityKey = "robot_3";
        int spawnPointIndex = -1;
        int width = 1440;
        int height = 900;
        double durationSec = 0.0;
        bool energyTest = false;
        bool energyLargeTest = false;
        bool lockMouseOnStart = false;
        bool startInMatch = false;
        double startTimeSec = 0.0;

        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            string NextValue(string name)
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException($"Missing value for {name}.");
                }

                return args[++index];
            }

            switch (arg)
            {
                case "--preset":
                case "--map":
                    preset = NextValue(arg);
                    break;
                case "--player":
                case "--entity":
                    player = NextValue(arg);
                    if (TryParseEntityId(player, out string parsedTeam, out string parsedEntityKey))
                    {
                        playerTeam = parsedTeam;
                        playerEntityKey = parsedEntityKey;
                    }

                    break;
                case "--team":
                case "--operator-team":
                    playerTeam = NormalizeTeam(NextValue(arg));
                    break;
                case "--robot":
                case "--seat":
                case "--operator":
                    playerEntityKey = NormalizeEntityKey(NextValue(arg));
                    break;
                case "--spawn":
                case "--spawn-point":
                    spawnPointIndex = int.Parse(NextValue(arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--width":
                    width = int.Parse(NextValue(arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--height":
                    height = int.Parse(NextValue(arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--duration-sec":
                    durationSec = double.Parse(NextValue(arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--start-time-sec":
                case "--game-time-sec":
                    startTimeSec = double.Parse(NextValue(arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--energy-test":
                    energyTest = true;
                    break;
                case "--large-energy-test":
                case "--energy-large-test":
                    energyTest = true;
                    energyLargeTest = true;
                    startInMatch = true;
                    startTimeSec = Math.Max(startTimeSec, 180.0);
                    break;
                case "--lock-mouse":
                case "--grab-mouse":
                    lockMouseOnStart = true;
                    break;
                case "--start-match":
                    startInMatch = true;
                    break;
                default:
                    if (!arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        preset = arg;
                    }
                    break;
            }
        }

        return new LinuxOperatorOptions(
            preset,
            player,
            playerTeam,
            playerEntityKey,
            spawnPointIndex,
            Math.Clamp(width, 640, 3840),
            Math.Clamp(height, 480, 2160),
            Math.Max(0.0, durationSec),
            energyTest,
            energyLargeTest,
            lockMouseOnStart,
            startInMatch,
            Math.Clamp(startTimeSec, 0.0, 420.0));
    }

    private static string NormalizeTeam(string raw)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value == "blue" ? "blue" : "red";
    }

    private static string NormalizeEntityKey(string raw)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.StartsWith("red_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("blue_", StringComparison.OrdinalIgnoreCase))
        {
            int separator = value.IndexOf('_');
            value = separator >= 0 && separator + 1 < value.Length
                ? value[(separator + 1)..]
                : value;
        }

        return value switch
        {
            "hero" => "robot_1",
            "engineer" => "robot_2",
            "infantry" or "infantry_1" => "robot_3",
            "infantry_2" => "robot_4",
            "sentry" => "robot_7",
            "robot_1" or "robot_2" or "robot_3" or "robot_4" or "robot_7" => value,
            _ => "robot_3",
        };
    }

    private static bool TryParseEntityId(string raw, out string team, out string entityKey)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        team = "red";
        entityKey = "robot_3";
        int separator = value.IndexOf('_');
        if (separator <= 0 || separator + 1 >= value.Length)
        {
            return false;
        }

        string parsedTeam = NormalizeTeam(value[..separator]);
        string parsedEntityKey = NormalizeEntityKey(value[(separator + 1)..]);
        if (!value.StartsWith("red_", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("blue_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        team = parsedTeam;
        entityKey = parsedEntityKey;
        return true;
    }
}

internal sealed class LinuxOperatorWindow : GameWindow
{
    private const double LocalPreparationSec = 60.0;
    private const double LocalSelfCheckSec = 15.0;
    private const double LocalCountdownSec = 5.0;
    private const double OperatorRuntimeLogIntervalSec = 0.50;
    private static readonly OperatorSeatSpec[] OperatorSeatSpecs =
    [
        new("robot_1", "HERO", TkKeys.D1),
        new("robot_2", "ENGINEER", TkKeys.D2),
        new("robot_3", "INFANTRY1", TkKeys.D3),
        new("robot_4", "INFANTRY2", TkKeys.D4),
        new("robot_7", "SENTRY", TkKeys.D7),
    ];

    private readonly SimulationWorldState _world;
    private readonly JsonObject _config;
    private readonly MapPresetDefinition _mapPreset;
    private readonly RuleSimulationService _simulation;
    private readonly ArenaInteractionService _interaction;
    private readonly LinuxOperatorOptions _options;
    private readonly PrimitiveBatch2D _batch = new();
    private readonly List<SimulationCombatEvent> _recentCombatEvents = new();
    private readonly TerrainSceneRenderer3D? _terrainRenderer;
    private readonly TerrainSceneOverlay? _terrainScene;
    private readonly System.Diagnostics.Stopwatch _lifetime = System.Diagnostics.Stopwatch.StartNew();
    private SimulationEntity _player;
    private bool _renderedTerrain3DThisFrame;
    private Matrix4 _lastViewProjection3D = Matrix4.Identity;
    private Vector3 _lastCameraPosition3D = Vector3.Zero;
    private double _cameraYawDeg;
    private double _cameraPitchDeg = -2.0;
    private bool _lastFDown;
    private bool _lastTabDown;
    private bool _lastEnterDown;
    private bool _lastDigit1Down;
    private bool _lastDigit2Down;
    private bool _lastDigit3Down;
    private bool _lastDigit4Down;
    private bool _lastDigit7Down;
    private bool _lastMDown;
    private bool _lastQDown;
    private bool _lastEDown;
    private bool _lastLeftDown;
    private bool _lastRightDown;
    private bool _lastLeftMouseDown;
    private bool _lastODown;
    private bool _lastPDown;
    private bool _mouseLookLocked;
    private bool _localRefereePanelOpen;
    private LinuxMatchPhase _matchPhase = LinuxMatchPhase.Login;
    private bool _operatorFirstPersonConfirmed;
    private string _operatorTeam = "red";
    private string _operatorEntityKey = "robot_3";
    private int _operatorSpawnPointIndex = 2;
    private double _matchPhaseElapsedSec;
    private double _lastEnergyRotationSampleSec = double.NegativeInfinity;
    private EnergyRotationSample? _previousEnergyRotationSample;
    private double _lastRuntimeLogSec = double.NegativeInfinity;
    private double _runTimeSec;

    private bool HasEnteredMatch => _matchPhase != LinuxMatchPhase.Login;

    private bool IsMatchLive => _matchPhase == LinuxMatchPhase.Live;

    private double EnergyVisualGameTimeSec => IsMatchLive
        ? _world.GameTimeSec
        : _world.GameTimeSec + _matchPhaseElapsedSec;

    private bool CanUseMouseLook => IsFocused
        && !_localRefereePanelOpen
        && (IsMatchLive || ShouldRenderOperatorFirstPersonScene());

    private bool ShouldRenderOperatorFirstPersonScene()
        => _operatorFirstPersonConfirmed
            && _matchPhase is LinuxMatchPhase.Preparation or LinuxMatchPhase.SelfCheck or LinuxMatchPhase.Countdown;

    public LinuxOperatorWindow(
        GameWindowSettings gameWindowSettings,
        NativeWindowSettings nativeWindowSettings,
        SimulationWorldState world,
        JsonObject config,
        MapPresetDefinition mapPreset,
        RuleSet rules,
        LinuxOperatorOptions options)
        : base(gameWindowSettings, nativeWindowSettings)
    {
        _world = world;
        _config = config;
        _mapPreset = mapPreset;
        _options = options;
        _interaction = new ArenaInteractionService(rules);
        _simulation = new RuleSimulationService(
            rules,
            _interaction,
            seed: 20260509,
            enableAutoMovement: false,
            enableAiCombat: true);
        _operatorTeam = NormalizeTeam(options.PlayerTeam);
        _operatorEntityKey = NormalizeEntityKey(options.PlayerEntityKey);
        _operatorSpawnPointIndex = options.SpawnPointIndex >= 0
            ? Math.Clamp(options.SpawnPointIndex, 0, OperatorSeatSpecs.Length - 1)
            : ResolveDefaultSpawnPointIndex(_operatorEntityKey);
        bool startLive = options.EnergyTest || options.StartTimeSec > 0.0;
        bool startCountdown = options.StartInMatch && !startLive;
        _matchPhase = startLive
            ? LinuxMatchPhase.Live
            : startCountdown
                ? LinuxMatchPhase.Countdown
                : LinuxMatchPhase.Login;
        _operatorFirstPersonConfirmed = startLive || startCountdown;
        _world.GameTimeSec = startLive ? Math.Max(0.0, options.StartTimeSec) : 0.0;

        _terrainRenderer = TerrainSceneRenderer3D.TryBuild(mapPreset, world);
        _terrainScene = _terrainRenderer is null ? TerrainSceneOverlay.TryLoad(mapPreset, world) : null;
        _mouseLookLocked = options.LockMouseOnStart;
        _player = ResolvePlayer(options.PlayerEntityId);
        PrepareOperatorLoginPreview();
        SelectPlayer(_player);
        if (startLive)
        {
            ApplyOperatorLoginSeat();
            EnterMatchLive("command_line");
        }
        else if (startCountdown)
        {
            ApplyOperatorLoginSeat();
            _player = ResolvePlayer(string.Empty);
            SelectPlayer(_player);
            BeginOperatorCountdown("command_line");
        }
        else
        {
            LogMatchPhase(LinuxMatchPhase.Login, "local_room_ready", 0.0, 0.0);
        }

        if (options.EnergyTest)
        {
            ArenaInteractionService.StartForcedEnergyAttempt(
                _world.GetOrCreateTeamState(_player.Team),
                large: options.EnergyLargeTest || _world.GameTimeSec >= 180.0,
                _world.GameTimeSec);
            EnsureEnergyVisualDemoState(_world.GetOrCreateTeamState(_player.Team), options.EnergyLargeTest || _world.GameTimeSec >= 180.0);
            RefreshEnergyRuntimeTargets();
            LogEnergyRotationSample(force: true);
        }
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        GL.ClearColor(0.50f, 0.60f, 0.68f, 1f);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _terrainRenderer?.Initialize();
        _batch.Initialize();
        UpdateCursorState();
    }

    protected override void OnUnload()
    {
        _batch.Dispose();
        _terrainRenderer?.Dispose();
        base.OnUnload();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        double dt = Math.Clamp(args.Time, 0.001, 0.050);
        _runTimeSec += dt;
        if (ShouldCloseForDuration())
        {
            return;
        }

        if (IsFocused && KeyboardState.IsKeyDown(TkKeys.Escape))
        {
            Close();
            return;
        }

        HandleLocalRefereePanelToggle();
        HandleMouseLockToggle();
        UpdateCursorState();
        if (IsFocused)
        {
            bool enterDown = KeyboardState.IsKeyDown(TkKeys.Enter);
            if (enterDown && !_lastEnterDown)
            {
                StartOperatorMatch();
            }

            _lastEnterDown = enterDown;
            if (!_localRefereePanelOpen && _matchPhase == LinuxMatchPhase.Login)
            {
                HandleOperatorLoginInput();
            }

            if (_mouseLookLocked && CanUseMouseLook)
            {
                _cameraYawDeg = NormalizeDeg(_cameraYawDeg + MouseState.Delta.X * 0.09);
                _cameraPitchDeg = Math.Clamp(_cameraPitchDeg - MouseState.Delta.Y * 0.075, -38.0, 26.0);
            }

            bool tabDown = KeyboardState.IsKeyDown(TkKeys.Tab);
            if (IsMatchLive && tabDown && !_lastTabDown)
            {
                _player.AutoAimRequested = !_player.AutoAimRequested;
            }

            _lastTabDown = tabDown;

            bool fDown = KeyboardState.IsKeyDown(TkKeys.F);
            if (IsMatchLive && fDown && !_lastFDown)
            {
                ArenaInteractionService.StartForcedEnergyAttempt(
                    _world.GetOrCreateTeamState(_player.Team),
                    large: _options.EnergyLargeTest || _world.GameTimeSec >= 180.0,
                    _world.GameTimeSec);
                EnsureEnergyVisualDemoState(_world.GetOrCreateTeamState(_player.Team), _options.EnergyLargeTest || _world.GameTimeSec >= 180.0);
                RefreshEnergyRuntimeTargets();
                LogEnergyRotationSample(force: true);
            }

            _lastFDown = fDown;
        }
        else
        {
            _lastTabDown = false;
            _lastFDown = false;
            _lastEnterDown = false;
            _lastQDown = false;
            _lastEDown = false;
            _lastLeftDown = false;
            _lastRightDown = false;
            _lastLeftMouseDown = false;
            _lastODown = false;
            _lastPDown = false;
            ClearPlayerInput();
        }

        if (IsMatchLive)
        {
            StepOperatorRuntime(dt);
            SimulationRunReport report = _simulation.Run(
                _world,
                _mapPreset.Facilities,
                dt,
                dt,
                captureFinalEntities: false,
                enableCombat: true);
            foreach (SimulationCombatEvent evt in report.CombatEvents)
            {
                _recentCombatEvents.Add(evt);
            }

            if (_recentCombatEvents.Count > 40)
            {
                _recentCombatEvents.RemoveRange(0, _recentCombatEvents.Count - 40);
            }

            RefreshEnergyRuntimeTargets();
            if (_options.EnergyTest)
            {
                EnsureEnergyVisualDemoState(
                    _world.GetOrCreateTeamState(_player.Team),
                    _options.EnergyLargeTest || _world.GameTimeSec >= 180.0);
                RefreshEnergyRuntimeTargets();
            }

            LogOperatorRuntimeSample();
        }
        else if (HasEnteredMatch)
        {
            AdvanceLocalStartup(dt);
            ClearPlayerInput();
        }
        else
        {
            ClearPlayerInput();
        }

        LogEnergyRotationSample(force: false);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        if (ShouldCloseForDuration())
        {
            return;
        }

        Rgba clearColor = HasEnteredMatch
            ? Rgba.FromBytes(4, 6, 10)
            : Rgba.FromBytes(128, 154, 172);
        GL.ClearColor(clearColor.R, clearColor.G, clearColor.B, 1f);
        GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        _renderedTerrain3DThisFrame = DrawTerrainScene3D();

        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        _batch.Begin(ClientSize.X, ClientSize.Y);
        if (_renderedTerrain3DThisFrame)
        {
            DrawProjected3DOverlay();
        }

        if (IsMatchLive || ShouldRenderOperatorFirstPersonScene() || !_renderedTerrain3DThisFrame)
        {
            DrawWorldView();
        }

        if (IsMatchLive)
        {
            DrawHud();
        }
        else
        {
            DrawOperatorStartupOverlay();
        }

        DrawLocalRefereePanelOverlay();
        _batch.Flush();
        SwapBuffers();
    }

    private void DrawProjected3DOverlay()
    {
        DrawEnergyMechanism3DOverlay(_lastViewProjection3D);
    }

    private bool ShouldCloseForDuration()
    {
        if (_options.DurationSec <= 0.0
            || Math.Max(_runTimeSec, _lifetime.Elapsed.TotalSeconds) < _options.DurationSec)
        {
            return false;
        }

        Close();
        return true;
    }

    private void StartOperatorMatch()
    {
        if (IsMatchLive)
        {
            return;
        }

        if (_matchPhase == LinuxMatchPhase.Login)
        {
            ApplyOperatorLoginSeat();
            _player = ResolvePlayer(string.Empty);
            SelectPlayer(_player);
            BeginOperatorCountdown("operator_login_confirmed");
            return;
        }

        if (_matchPhase == LinuxMatchPhase.Preparation && !_operatorFirstPersonConfirmed)
        {
            ConfirmOperatorFirstPersonView("enter_confirm");
            return;
        }

        AdvanceLocalStartup(skip: true);
    }

    private void AdvanceLocalStartup(double dt)
    {
        if (_matchPhase == LinuxMatchPhase.Login || _matchPhase == LinuxMatchPhase.Live)
        {
            return;
        }

        _matchPhaseElapsedSec += Math.Max(0.0, dt);
        double requiredSec = _matchPhase switch
        {
            LinuxMatchPhase.Preparation => LocalPreparationSec,
            LinuxMatchPhase.SelfCheck => LocalSelfCheckSec,
            LinuxMatchPhase.Countdown => LocalCountdownSec,
            _ => 0.0,
        };
        if (_matchPhaseElapsedSec + 1e-6 >= requiredSec)
        {
            AdvanceLocalStartup(skip: false);
        }
    }

    private void AdvanceLocalStartup(bool skip)
    {
        switch (_matchPhase)
        {
            case LinuxMatchPhase.Preparation:
                if (!_operatorFirstPersonConfirmed)
                {
                    ConfirmOperatorFirstPersonView(skip ? "enter_skip_confirm" : "timer_confirm");
                }

                _matchPhase = LinuxMatchPhase.SelfCheck;
                _matchPhaseElapsedSec = 0.0;
                LogMatchPhase(_matchPhase, skip ? "enter_skip" : "timer", 0.0, LocalSelfCheckSec);
                break;
            case LinuxMatchPhase.SelfCheck:
                _matchPhase = LinuxMatchPhase.Countdown;
                _matchPhaseElapsedSec = 0.0;
                LogMatchPhase(_matchPhase, skip ? "enter_skip" : "timer", 0.0, LocalCountdownSec);
                break;
            case LinuxMatchPhase.Countdown:
                EnterMatchLive(skip ? "enter_skip" : "timer");
                break;
        }
    }

    private void BeginOperatorCountdown(string reason)
    {
        ApplyOperatorPreparationSelection();
        _operatorFirstPersonConfirmed = true;
        _matchPhase = LinuxMatchPhase.Countdown;
        _matchPhaseElapsedSec = 0.0;
        _player.State = "countdown";
        _cameraYawDeg = _player.AngleDeg;
        _cameraPitchDeg = -2.0;
        _player.TurretYawDeg = _cameraYawDeg;
        _player.GimbalPitchDeg = _cameraPitchDeg;
        RefreshEnergyRuntimeTargets();
        LogMatchPhase(_matchPhase, reason, 0.0, LocalCountdownSec);
    }

    private void EnterMatchLive(string reason)
    {
        ApplyOperatorPreparationSelection();
        _operatorFirstPersonConfirmed = true;
        _matchPhase = LinuxMatchPhase.Live;
        _matchPhaseElapsedSec = 0.0;
        _world.GameTimeSec = 0.0;
        if (_options.StartTimeSec > 0.0)
        {
            _world.GameTimeSec = Math.Max(0.0, _options.StartTimeSec);
        }

        _player.State = "manual";
        _cameraYawDeg = _player.AngleDeg;
        _player.TurretYawDeg = _cameraYawDeg;
        RefreshEnergyRuntimeTargets();
        LogMatchPhase(LinuxMatchPhase.Live, reason, _world.GameTimeSec, 0.0);
    }

    private void ConfirmOperatorFirstPersonView(string reason)
    {
        ApplyOperatorPreparationSelection();
        _operatorFirstPersonConfirmed = true;
        _cameraYawDeg = _player.AngleDeg;
        _player.TurretYawDeg = _cameraYawDeg;
        _player.GimbalPitchDeg = _cameraPitchDeg;
        LogMatchPhase(_matchPhase, reason, _matchPhaseElapsedSec, LocalPreparationSec);
    }

    private void ApplyOperatorPreparationSelection()
    {
        _player.IsSimulationSuppressed = false;
        _player.IsPlayerControlled = true;
        _player.UnlimitedAmmo = true;
        if (!_player.IsAlive && !_player.PermanentEliminated)
        {
            _player.Health = Math.Max(1.0, _player.MaxHealth);
            _player.RespawnTimerSec = 0.0;
        }
    }

    private static void LogMatchPhase(LinuxMatchPhase phase, string reason, double elapsedSec, double durationSec)
    {
        File.AppendAllText(
            "/tmp/simulator_linux_operator.log",
            $"{DateTime.Now:HH:mm:ss.fff} match_phase phase={phase} reason={reason} elapsed={elapsedSec:0.000} duration={durationSec:0.000}{Environment.NewLine}");
    }

    private void PrepareOperatorLoginPreview()
    {
        string activeEntityId = $"{_operatorTeam}_{_operatorEntityKey}";
        foreach (SimulationEntity entity in _world.Entities.Where(IsMovableEntity))
        {
            entity.IsSimulationSuppressed = !string.Equals(entity.Id, activeEntityId, StringComparison.OrdinalIgnoreCase);
        }

        SimulationEntity? preview = _world.Entities.FirstOrDefault(entity =>
            string.Equals(entity.Id, activeEntityId, StringComparison.OrdinalIgnoreCase));
        if (preview is null)
        {
            return;
        }

        (double x, double y, double yawDeg) = ResolvePreparationSpawn(_operatorTeam, _operatorSpawnPointIndex);
        PlaceOperatorEntity(preview, x, y, yawDeg);
        preview.State = "login_preview";
        preview.IsPlayerControlled = false;
        _player = preview;
    }

    private void ApplyOperatorLoginSeat()
    {
        string team = NormalizeTeam(_operatorTeam);
        string entityKey = NormalizeEntityKey(_operatorEntityKey);
        int spawnPointIndex = Math.Clamp(_operatorSpawnPointIndex, 0, OperatorSeatSpecs.Length - 1);
        string activeEntityId = $"{team}_{entityKey}";
        for (int index = _world.Entities.Count - 1; index >= 0; index--)
        {
            SimulationEntity entity = _world.Entities[index];
            if (!IsMovableEntity(entity))
            {
                continue;
            }

            if (string.Equals(entity.Id, activeEntityId, StringComparison.OrdinalIgnoreCase))
            {
                entity.IsSimulationSuppressed = false;
                continue;
            }

            _world.Entities.RemoveAt(index);
        }

        SimulationEntity? player = _world.Entities.FirstOrDefault(entity =>
            string.Equals(entity.Id, activeEntityId, StringComparison.OrdinalIgnoreCase));
        if (player is null)
        {
            throw new InvalidOperationException($"Operator seat '{activeEntityId}' was not found in map configuration.");
        }

        (double x, double y, double yawDeg) = ResolvePreparationSpawn(team, spawnPointIndex);
        PlaceOperatorEntity(player, x, y, yawDeg);
        player.IsSimulationSuppressed = false;
        player.State = "preparing";
        player.UnlimitedAmmo = true;
        _world.Projectiles.Clear();
        File.AppendAllText(
            "/tmp/simulator_linux_operator.log",
            $"{DateTime.Now:HH:mm:ss.fff} login seat={team}_{entityKey} team={team} entity={entityKey} spawn={spawnPointIndex} world_entity={player.Id} active_robots=1{Environment.NewLine}");
    }

    private static bool IsMovableEntity(SimulationEntity entity)
        => string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase);

    private (double X, double Y, double YawDeg) ResolvePreparationSpawn(string team, int spawnPointIndex)
    {
        string[] spawnEntityKeys =
        [
            "robot_1",
            "robot_2",
            "robot_3",
            "robot_4",
            "robot_7",
        ];
        int clampedIndex = Math.Clamp(spawnPointIndex, 0, spawnEntityKeys.Length - 1);
        if (TryReadConfiguredSpawn(team, spawnEntityKeys[clampedIndex], out (double X, double Y, double YawDeg) configuredSpawn))
        {
            return configuredSpawn;
        }

        string fallbackEntityId = $"{NormalizeTeam(team)}_{spawnEntityKeys[clampedIndex]}";
        SimulationEntity? fallback = _world.Entities.FirstOrDefault(entity =>
            string.Equals(entity.Id, fallbackEntityId, StringComparison.OrdinalIgnoreCase));
        if (fallback is not null)
        {
            return (fallback.X, fallback.Y, fallback.AngleDeg);
        }

        fallback = _world.Entities
            .Where(IsMovableEntity)
            .Where(entity => string.Equals(entity.Team, team, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (fallback is not null)
        {
            return (fallback.X, fallback.Y, fallback.AngleDeg);
        }

        return (_world.WorldWidth * 0.5, _world.WorldHeight * 0.5, team == "blue" ? 180.0 : 0.0);
    }

    private bool TryReadConfiguredSpawn(string team, string entityKey, out (double X, double Y, double YawDeg) spawn)
    {
        spawn = default;
        JsonObject? entities = _config["entities"] as JsonObject;
        JsonObject? initialPositions = entities?["initial_positions"] as JsonObject;
        JsonObject? teamPositions = initialPositions?[team] as JsonObject;
        JsonObject? position = teamPositions?[entityKey] as JsonObject;
        if (position is null)
        {
            return false;
        }

        double fallbackYaw = team == "blue" ? 180.0 : 0.0;
        spawn = (
            ReadDouble(position["x"], _world.WorldWidth * 0.5),
            ReadDouble(position["y"], _world.WorldHeight * 0.5),
            ReadDouble(position["angle"], fallbackYaw));
        return true;
    }

    private static double ReadDouble(JsonNode? node, double fallback)
        => node is null
            ? fallback
            : double.TryParse(
                node.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double value)
                ? value
                : fallback;

    private static void PlaceOperatorEntity(SimulationEntity entity, double x, double y, double yawDeg)
    {
        entity.X = x;
        entity.Y = y;
        entity.GroundHeightM = 0.0;
        entity.AirborneHeightM = 0.0;
        entity.VerticalVelocityMps = 0.0;
        entity.JumpCrouchTimerSec = 0.0;
        entity.JumpCrouchDurationSec = 0.0;
        entity.StepClimbPoseBlend = 0.0;
        entity.StepClimbPoseVelocity = 0.0;
        entity.LandingCompressionM = 0.0;
        entity.LandingCompressionVelocityMps = 0.0;
        entity.ChassisImpactShakeTimerSec = 0.0;
        entity.ChassisImpactShakeDurationSec = 0.0;
        entity.ChassisImpactShakeIntensity = 0.0;
        entity.ChassisImpactShakeDirectionDeg = 0.0;
        entity.VelocityXWorldPerSec = 0.0;
        entity.VelocityYWorldPerSec = 0.0;
        entity.AngularVelocityDegPerSec = 0.0;
        entity.ObservedVelocityXWorldPerSec = 0.0;
        entity.ObservedVelocityYWorldPerSec = 0.0;
        entity.ObservedAngularVelocityDegPerSec = 0.0;
        entity.AngleDeg = yawDeg;
        entity.ChassisTargetYawDeg = yawDeg;
        entity.TurretYawDeg = yawDeg;
        entity.GimbalPitchDeg = 0.0;
        entity.TurretYawCommandVelocityDegPerSec = 0.0;
        entity.GimbalPitchCommandVelocityDegPerSec = 0.0;
        entity.ChassisPitchDeg = 0.0;
        entity.ChassisRollDeg = 0.0;
        entity.ChassisPitchVelocityDegPerSec = 0.0;
        entity.ChassisRollVelocityDegPerSec = 0.0;
        entity.LastChassisVelocityXMps = 0.0;
        entity.LastChassisVelocityYMps = 0.0;
        entity.VisualLegLeftFootXM = double.NaN;
        entity.VisualLegLeftFootYM = double.NaN;
        entity.VisualLegRightFootXM = double.NaN;
        entity.VisualLegRightFootYM = double.NaN;
        entity.MoveInputForward = 0.0;
        entity.MoveInputRight = 0.0;
        entity.TraversalActive = false;
        entity.TraversalProgress = 0.0;
        entity.FortCaptureProgressSec = 0.0;
        entity.FortEnemyOccupationProgressSec = 0.0;
        entity.FortActiveFacilityId = string.Empty;
        entity.FortReserveAmmo = 0;
        entity.FortReserveAmmoCap = 0;
        entity.MotionBlockReason = string.Empty;
    }

    private static string NormalizeTeam(string raw)
        => string.Equals((raw ?? string.Empty).Trim(), "blue", StringComparison.OrdinalIgnoreCase) ? "blue" : "red";

    private static string NormalizeEntityKey(string raw)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.StartsWith("red_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("blue_", StringComparison.OrdinalIgnoreCase))
        {
            int separator = value.IndexOf('_');
            value = separator >= 0 && separator + 1 < value.Length
                ? value[(separator + 1)..]
                : value;
        }

        return value switch
        {
            "hero" => "robot_1",
            "engineer" => "robot_2",
            "infantry" or "infantry_1" => "robot_3",
            "infantry_2" => "robot_4",
            "sentry" => "robot_7",
            "robot_1" or "robot_2" or "robot_3" or "robot_4" or "robot_7" => value,
            _ => "robot_3",
        };
    }

    private static int ResolveDefaultSpawnPointIndex(string entityKey)
        => NormalizeEntityKey(entityKey) switch
        {
            "robot_1" => 0,
            "robot_2" => 1,
            "robot_3" => 2,
            "robot_4" => 3,
            "robot_7" => 4,
            _ => 2,
        };

    private void HandleOperatorLoginInput()
    {
        if (HandleLoginSeatKey(OperatorSeatSpecs[0].Key, ref _lastDigit1Down, OperatorSeatSpecs[0].EntityKey)
            || HandleLoginSeatKey(OperatorSeatSpecs[1].Key, ref _lastDigit2Down, OperatorSeatSpecs[1].EntityKey)
            || HandleLoginSeatKey(OperatorSeatSpecs[2].Key, ref _lastDigit3Down, OperatorSeatSpecs[2].EntityKey)
            || HandleLoginSeatKey(OperatorSeatSpecs[3].Key, ref _lastDigit4Down, OperatorSeatSpecs[3].EntityKey)
            || HandleLoginSeatKey(OperatorSeatSpecs[4].Key, ref _lastDigit7Down, OperatorSeatSpecs[4].EntityKey))
        {
            return;
        }

        bool qDown = KeyboardState.IsKeyDown(TkKeys.Q);
        if (qDown && !_lastQDown)
        {
            SetOperatorLoginTeam("red");
        }

        _lastQDown = qDown;

        bool eDown = KeyboardState.IsKeyDown(TkKeys.E);
        if (eDown && !_lastEDown)
        {
            SetOperatorLoginTeam("blue");
        }

        _lastEDown = eDown;

        bool leftDown = KeyboardState.IsKeyDown(TkKeys.Left);
        if (leftDown && !_lastLeftDown)
        {
            SetOperatorLoginSpawn(_operatorSpawnPointIndex - 1);
        }

        _lastLeftDown = leftDown;

        bool rightDown = KeyboardState.IsKeyDown(TkKeys.Right);
        if (rightDown && !_lastRightDown)
        {
            SetOperatorLoginSpawn(_operatorSpawnPointIndex + 1);
        }

        _lastRightDown = rightDown;

        bool leftMouseDown = MouseState.IsButtonDown(TkMouseButton.Left);
        if (leftMouseDown && !_lastLeftMouseDown)
        {
            HandleLoginMouseClick(MouseState.Position);
        }

        _lastLeftMouseDown = leftMouseDown;
    }

    private bool HandleLoginSeatKey(TkKeys key, ref bool lastDown, string entityKey)
    {
        bool down = KeyboardState.IsKeyDown(key);
        bool pressed = down && !lastDown;
        lastDown = down;
        if (!pressed)
        {
            return false;
        }

        SetOperatorLoginSeat(_operatorTeam, entityKey);
        return true;
    }

    private void SetOperatorLoginTeam(string team)
    {
        _operatorTeam = NormalizeTeam(team);
        SetOperatorLoginSeat(_operatorTeam, _operatorEntityKey);
    }

    private void SetOperatorLoginSeat(string team, string entityKey)
    {
        string normalizedTeam = NormalizeTeam(team);
        string normalizedEntity = NormalizeEntityKey(entityKey);
        string entityId = $"{normalizedTeam}_{normalizedEntity}";
        if (!_world.Entities.Any(entity => string.Equals(entity.Id, entityId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _operatorTeam = normalizedTeam;
        _operatorEntityKey = normalizedEntity;
        _operatorSpawnPointIndex = ResolveDefaultSpawnPointIndex(normalizedEntity);
        PrepareOperatorLoginPreview();
        SelectPlayer(_player);
        LogMatchPhase(LinuxMatchPhase.Login, $"seat_selected:{entityId}", 0.0, 0.0);
    }

    private void SetOperatorLoginSpawn(int spawnPointIndex)
    {
        _operatorSpawnPointIndex = Math.Clamp(spawnPointIndex, 0, OperatorSeatSpecs.Length - 1);
        PrepareOperatorLoginPreview();
        SelectPlayer(_player);
    }

    private void HandleLoginMouseClick(Vector2 position)
    {
        float x = position.X;
        float y = position.Y;
        float w = ClientSize.X;
        float h = ClientSize.Y;
        float panelW = Math.Clamp(w * 0.74f, 760f, 1120f);
        float panelH = Math.Clamp(h * 0.58f, 430f, 590f);
        float panelX = (w - panelW) * 0.5f;
        float panelY = Math.Clamp(h * 0.10f, 54f, 110f);
        if (y < panelY || y > panelY + panelH)
        {
            return;
        }

        float teamButtonY = panelY + 100f;
        float teamButtonW = 126f;
        if (x >= panelX + 44f && x <= panelX + 44f + teamButtonW && y >= teamButtonY && y <= teamButtonY + 38f)
        {
            SetOperatorLoginTeam("red");
            return;
        }

        if (x >= panelX + 44f + teamButtonW + 12f && x <= panelX + 44f + teamButtonW * 2f + 12f && y >= teamButtonY && y <= teamButtonY + 38f)
        {
            SetOperatorLoginTeam("blue");
            return;
        }

        float gridY = panelY + 170f;
        float seatW = (panelW - 88f - 16f * (OperatorSeatSpecs.Length - 1)) / OperatorSeatSpecs.Length;
        for (int index = 0; index < OperatorSeatSpecs.Length; index++)
        {
            float seatX = panelX + 44f + index * (seatW + 16f);
            if (x >= seatX && x <= seatX + seatW && y >= gridY && y <= gridY + 132f)
            {
                SetOperatorLoginSeat(_operatorTeam, OperatorSeatSpecs[index].EntityKey);
                return;
            }
        }

        float spawnY = gridY + 164f;
        for (int index = 0; index < OperatorSeatSpecs.Length; index++)
        {
            float spawnX = panelX + 44f + index * 72f;
            if (x >= spawnX && x <= spawnX + 56f && y >= spawnY && y <= spawnY + 38f)
            {
                SetOperatorLoginSpawn(index);
                return;
            }
        }
    }

    private SimulationEntity ResolvePlayer(string requestedId)
    {
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            SimulationEntity? exact = _world.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Id, requestedId, StringComparison.OrdinalIgnoreCase)
                && IsOperatorControllable(entity));
            if (exact is not null)
            {
                return exact;
            }
        }

        string loginEntityId = $"{NormalizeTeam(_options.PlayerTeam)}_{NormalizeEntityKey(_options.PlayerEntityKey)}";
        return _world.Entities.FirstOrDefault(entity =>
                IsOperatorControllable(entity)
                && string.Equals(entity.Id, loginEntityId, StringComparison.OrdinalIgnoreCase))
            ?? _world.Entities.First(IsOperatorControllable);
    }

    private static bool IsOperatorControllable(SimulationEntity entity)
        => entity.IsAlive
            && (string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase));

    private void SelectPlayer(SimulationEntity entity)
    {
        foreach (SimulationEntity candidate in _world.Entities)
        {
            candidate.IsPlayerControlled = false;
        }

        _player = entity;
        _player.IsPlayerControlled = true;
        _player.UnlimitedAmmo = true;
        _cameraYawDeg = _player.AngleDeg;
        _player.TurretYawDeg = _cameraYawDeg;
        _player.GimbalPitchDeg = _cameraPitchDeg;
    }

    private void HandleSelectionKeys()
    {
        _lastDigit1Down = KeyboardState.IsKeyDown(TkKeys.D1);
        _lastDigit2Down = KeyboardState.IsKeyDown(TkKeys.D2);
        _lastDigit3Down = KeyboardState.IsKeyDown(TkKeys.D3);
        _lastDigit4Down = KeyboardState.IsKeyDown(TkKeys.D4);
        _lastDigit7Down = KeyboardState.IsKeyDown(TkKeys.D7);
    }

    private void TrySelectByKey(TkKeys key, ref bool lastDown, string suffix)
    {
        bool down = KeyboardState.IsKeyDown(key);
        if (down && !lastDown)
        {
            SimulationEntity? candidate = _world.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Id, $"{_player.Team}_{suffix}", StringComparison.OrdinalIgnoreCase)
                && IsOperatorControllable(entity));
            if (candidate is not null)
            {
                SelectPlayer(candidate);
            }
        }

        lastDown = down;
    }

    private void StepOperatorRuntime(double dt)
    {
        LinuxPlayerControlState control = BuildPlayerControlState();
        ApplyPlayerControlState(control, dt);
        StepPlayerMotion(control, dt);
    }

    private LinuxPlayerControlState BuildPlayerControlState()
    {
        if (!IsFocused || !IsMatchLive || _localRefereePanelOpen)
        {
            return LinuxPlayerControlState.Empty;
        }

        double forwardInput = KeyAxis(TkKeys.W, TkKeys.S);
        double rightInput = KeyAxis(TkKeys.D, TkKeys.A);
        double length = Math.Sqrt(forwardInput * forwardInput + rightInput * rightInput);
        if (length > 1.0)
        {
            forwardInput /= length;
            rightInput /= length;
        }

        return new LinuxPlayerControlState(
            forwardInput,
            rightInput,
            _cameraYawDeg,
            _cameraPitchDeg,
            MouseState.IsButtonDown(TkMouseButton.Left),
            _player.AutoAimRequested || MouseState.IsButtonDown(TkMouseButton.Right),
            KeyboardState.IsKeyDown(TkKeys.Space),
            KeyboardState.IsKeyDown(TkKeys.F),
            KeyboardState.IsKeyDown(TkKeys.LeftShift) || KeyboardState.IsKeyDown(TkKeys.RightShift));
    }

    private void ApplyPlayerControlState(LinuxPlayerControlState control, double dt)
    {
        foreach (SimulationEntity entity in _world.Entities.Where(IsMovableEntity))
        {
            entity.IsPlayerControlled = ReferenceEquals(entity, _player);
            entity.MoveInputForward = 0.0;
            entity.MoveInputRight = 0.0;
            entity.IsFireCommandActive = false;
            entity.JumpRequested = false;
            entity.EnergyActivationRequested = false;
        }

        if (!IsMatchLive || !IsFocused || _localRefereePanelOpen)
        {
            return;
        }

        double previousYaw = _player.TurretYawDeg;
        double previousPitch = _player.GimbalPitchDeg;
        _player.IsPlayerControlled = true;
        _player.MoveInputForward = control.MoveForward;
        _player.MoveInputRight = control.MoveRight;
        _player.IsFireCommandActive = control.Fire;
        _player.AutoAimRequested = control.AutoAim;
        _player.JumpRequested = control.Jump;
        _player.EnergyActivationRequested = control.EnergyActivation;
        _player.TurretYawDeg = control.CameraYawDeg;
        _player.GimbalPitchDeg = control.CameraPitchDeg;
        if (dt > 1e-6)
        {
            _player.TurretYawCommandVelocityDegPerSec = NormalizeSignedDeg(control.CameraYawDeg - previousYaw) / dt;
            _player.GimbalPitchCommandVelocityDegPerSec = (control.CameraPitchDeg - previousPitch) / dt;
        }
    }

    private void StepPlayerMotion(LinuxPlayerControlState control, double dt)
    {
        double previousX = _player.X;
        double previousY = _player.Y;
        double previousAngleDeg = _player.AngleDeg;
        if (!IsMatchLive || !IsFocused || _localRefereePanelOpen || !_player.IsAlive || _player.IsSimulationSuppressed)
        {
            _player.VelocityXWorldPerSec = 0.0;
            _player.VelocityYWorldPerSec = 0.0;
            _player.ObservedVelocityXWorldPerSec = 0.0;
            _player.ObservedVelocityYWorldPerSec = 0.0;
            return;
        }

        double yawRad = control.CameraYawDeg * Math.PI / 180.0;
        double forwardX = Math.Cos(yawRad);
        double forwardY = Math.Sin(yawRad);
        double rightX = -Math.Sin(yawRad);
        double rightY = Math.Cos(yawRad);
        double metersPerWorldUnit = Math.Max(_world.MetersPerWorldUnit, 1e-6);
        double speedMps = ResolveMoveSpeedMps(_player);
        if (control.Boost)
        {
            speedMps *= 1.18;
        }

        double deltaWorld = speedMps * dt / metersPerWorldUnit;
        double moveX = (forwardX * control.MoveForward + rightX * control.MoveRight) * deltaWorld;
        double moveY = (forwardY * control.MoveForward + rightY * control.MoveRight) * deltaWorld;
        MoveOperatorWithCollision(moveX, moveY);

        double actualDx = _player.X - previousX;
        double actualDy = _player.Y - previousY;
        _player.VelocityXWorldPerSec = dt > 1e-6 ? actualDx / dt : 0.0;
        _player.VelocityYWorldPerSec = dt > 1e-6 ? actualDy / dt : 0.0;
        _player.ObservedVelocityXWorldPerSec = _player.VelocityXWorldPerSec;
        _player.ObservedVelocityYWorldPerSec = _player.VelocityYWorldPerSec;
        _player.HasObservedKinematics = true;
        _player.LastObservedX = _player.X;
        _player.LastObservedY = _player.Y;
        if (Math.Abs(control.MoveForward) > 1e-4 || Math.Abs(control.MoveRight) > 1e-4)
        {
            _player.AngleDeg = control.CameraYawDeg;
            _player.ChassisTargetYawDeg = control.CameraYawDeg;
            _player.State = "manual";
        }

        _player.AngularVelocityDegPerSec = dt > 1e-6
            ? NormalizeSignedDeg(_player.AngleDeg - previousAngleDeg) / dt
            : 0.0;
        _player.ObservedAngularVelocityDegPerSec = _player.AngularVelocityDegPerSec;
        _player.LastObservedAngleDeg = _player.AngleDeg;
    }

    private void MoveOperatorWithCollision(double moveX, double moveY)
    {
        double radiusWorld = ResolveOperatorCollisionRadiusWorld(_player);
        double targetX = Math.Clamp(_player.X + moveX, radiusWorld, Math.Max(radiusWorld, _world.WorldWidth - radiusWorld));
        double targetY = Math.Clamp(_player.Y + moveY, radiusWorld, Math.Max(radiusWorld, _world.WorldHeight - radiusWorld));
        string blockReason = string.Empty;
        bool movedX = false;
        bool movedY = false;

        if (Math.Abs(targetX - _player.X) > 1e-8)
        {
            if (IsOperatorPositionPassable(targetX, _player.Y, radiusWorld, out blockReason))
            {
                _player.X = targetX;
                movedX = true;
            }
        }

        if (Math.Abs(targetY - _player.Y) > 1e-8)
        {
            if (IsOperatorPositionPassable(_player.X, targetY, radiusWorld, out blockReason))
            {
                _player.Y = targetY;
                movedY = true;
            }
        }

        if (!movedX && !movedY && Math.Abs(moveX) + Math.Abs(moveY) > 1e-6)
        {
            _player.MotionBlockReason = string.IsNullOrWhiteSpace(blockReason) ? "blocked" : blockReason;
        }
        else
        {
            _player.MotionBlockReason = string.Empty;
        }
    }

    private bool IsOperatorPositionPassable(double x, double y, double radiusWorld, out string blockReason)
    {
        blockReason = string.Empty;
        if (x < radiusWorld
            || y < radiusWorld
            || x > _world.WorldWidth - radiusWorld
            || y > _world.WorldHeight - radiusWorld)
        {
            blockReason = "map_bounds";
            return false;
        }

        foreach (FacilityRegion facility in _mapPreset.Facilities)
        {
            if (!IsOperatorBlockingFacility(facility))
            {
                continue;
            }

            if (FacilityTouchesCircle(facility, x, y, radiusWorld))
            {
                blockReason = $"facility:{facility.Id}";
                return false;
            }
        }

        return true;
    }

    private static bool IsOperatorBlockingFacility(FacilityRegion facility)
    {
        string type = (facility.Type ?? string.Empty).Trim().ToLowerInvariant();
        return type is "base" or "outpost" or "energy_mechanism";
    }

    private static bool FacilityTouchesCircle(FacilityRegion facility, double x, double y, double radiusWorld)
    {
        if (facility.Contains(x, y))
        {
            return true;
        }

        for (int index = 0; index < 8; index++)
        {
            double angle = index * Math.PI * 0.25;
            if (facility.Contains(x + Math.Cos(angle) * radiusWorld, y + Math.Sin(angle) * radiusWorld))
            {
                return true;
            }
        }

        return false;
    }

    private static double ResolveOperatorCollisionRadiusWorld(SimulationEntity entity)
    {
        if (entity.CollisionRadiusWorld > 1e-6)
        {
            return Math.Max(0.1, entity.CollisionRadiusWorld);
        }

        double radiusM = Math.Max(entity.BodyLengthM, entity.BodyWidthM) * 0.58;
        return Math.Max(0.1, radiusM / 0.0178);
    }

    private void LogOperatorRuntimeSample()
    {
        if (!IsMatchLive || _world.GameTimeSec - _lastRuntimeLogSec < OperatorRuntimeLogIntervalSec)
        {
            return;
        }

        _lastRuntimeLogSec = _world.GameTimeSec;
        File.AppendAllText(
            "/tmp/simulator_linux_operator.log",
            $"{DateTime.Now:HH:mm:ss.fff} runtime phase={_matchPhase} t={_world.GameTimeSec:0.000} seat={_player.Id} pos=({_player.X:0.00},{_player.Y:0.00}) yaw={_player.AngleDeg:0.0} turret={_player.TurretYawDeg:0.0} input=({_player.MoveInputForward:0.00},{_player.MoveInputRight:0.00}) vel=({_player.VelocityXWorldPerSec:0.00},{_player.VelocityYWorldPerSec:0.00}) block={_player.MotionBlockReason}{Environment.NewLine}");
    }

    private void ClearPlayerInput()
    {
        _player.MoveInputForward = 0.0;
        _player.MoveInputRight = 0.0;
        _player.VelocityXWorldPerSec = 0.0;
        _player.VelocityYWorldPerSec = 0.0;
        _player.IsFireCommandActive = false;
        _player.JumpRequested = false;
        _player.EnergyActivationRequested = false;
    }

    private void HandleMouseLockToggle()
    {
        bool mDown = KeyboardState.IsKeyDown(TkKeys.M);
        if (IsFocused && mDown && !_lastMDown)
        {
            _mouseLookLocked = !_mouseLookLocked;
        }

        _lastMDown = mDown;
        if (!IsFocused
            || !CanUseMouseLook
            || KeyboardState.IsKeyDown(TkKeys.LeftAlt)
            || KeyboardState.IsKeyDown(TkKeys.RightAlt))
        {
            _mouseLookLocked = false;
        }
    }

    private void HandleLocalRefereePanelToggle()
    {
        bool oDown = KeyboardState.IsKeyDown(TkKeys.O);
        bool pDown = KeyboardState.IsKeyDown(TkKeys.P);
        if (IsFocused && ((oDown && !_lastODown) || (pDown && !_lastPDown)))
        {
            _localRefereePanelOpen = !_localRefereePanelOpen;
            if (_localRefereePanelOpen)
            {
                _mouseLookLocked = false;
                ClearPlayerInput();
            }
        }

        _lastODown = oDown;
        _lastPDown = pDown;
    }

    private void UpdateCursorState()
    {
        CursorState = CanUseMouseLook && _mouseLookLocked
            ? CursorState.Grabbed
            : CursorState.Normal;
    }

    private void LogEnergyRotationSample(bool force)
    {
        if (!_options.EnergyTest && !force)
        {
            return;
        }

        if (!force && _world.GameTimeSec - _lastEnergyRotationSampleSec < 0.50)
        {
            return;
        }

        SimulationTeamState teamState = _world.GetOrCreateTeamState(_player.Team);
        if (!force
            && (!string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
                || !teamState.EnergyLargeMechanismActive))
        {
            return;
        }

        if (!TryBuildEnergyRotationSample(teamState, out EnergyRotationSample sample))
        {
            return;
        }

        double measuredOmega = double.NaN;
        double expectedAverageOmega = double.NaN;
        string status = "sample";
        if (_previousEnergyRotationSample is EnergyRotationSample previous
            && previous.ArmIndex == sample.ArmIndex)
        {
            double dt = sample.GameTimeSec - previous.GameTimeSec;
            if (dt > 1e-6)
            {
                measuredOmega = ComputeSignedAngularDelta(previous, sample) / dt;
                expectedAverageOmega = ComputeWrappedAngleDelta(previous.ExpectedYawRad, sample.ExpectedYawRad) / dt;
                double tolerance = Math.Max(0.18, Math.Abs(expectedAverageOmega) * 0.16);
                status = Math.Abs(Math.Abs(measuredOmega) - Math.Abs(expectedAverageOmega)) <= tolerance
                    ? "ok"
                    : "drift";
            }
        }

        _previousEnergyRotationSample = sample;
        _lastEnergyRotationSampleSec = _world.GameTimeSec;
        string expectedAverageText = FormatAngularVelocity(expectedAverageOmega);
        string measuredText = FormatAngularVelocity(measuredOmega);
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} t={sample.GameTimeSec:0.000}s team={_player.Team} mode={(teamState.EnergyLargeMechanismActive ? "large" : "small")} state={teamState.EnergyMechanismState} arm={sample.ArmIndex} lit_mask=0x{teamState.EnergyCurrentLitMask:X2} yaw={sample.ExpectedYawRad:0.000}rad expected_omega={sample.ExpectedOmegaRadPerSec:0.000}rad/s expected_avg_omega={expectedAverageText} measured_omega={measuredText} status={status}";
        File.AppendAllText("/tmp/simulator_energy_rotation.log", line + Environment.NewLine);
    }

    private static string FormatAngularVelocity(double value)
        => double.IsNaN(value)
            ? "n/a"
            : $"{value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}rad/s";

    private void RefreshEnergyRuntimeTargets()
    {
        foreach (SimulationEntity mechanism in _world.Entities.Where(entity =>
                     string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)))
        {
            mechanism.RuntimeEnergyTargetsByTeam = null;
            mechanism.RuntimeEnergyTargetsGameTimeSec = double.NaN;

            var targetsByTeam = new Dictionary<string, IReadOnlyList<ArmorPlateTarget>>(StringComparer.OrdinalIgnoreCase);
            foreach (SimulationTeamState teamState in _world.Teams.Values)
            {
                targetsByTeam[teamState.Team] = SimulationCombatMath.GetEnergyMechanismTargets(
                    mechanism,
                    _world.MetersPerWorldUnit,
                    _world.GameTimeSec,
                    teamState.Team,
                    teamState);
            }

            if (targetsByTeam.Count > 0)
            {
                mechanism.RuntimeEnergyTargetsByTeam = targetsByTeam;
                mechanism.RuntimeEnergyTargetsGameTimeSec = _world.GameTimeSec;
            }
        }
    }

    private static void EnsureEnergyVisualDemoState(SimulationTeamState teamState, bool large)
    {
        teamState.EnergyTestAlwaysAvailable = true;
        teamState.EnergyTestForceLarge = large;
        bool activating = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase);
        bool mustRestart = !activating
            || teamState.EnergyCurrentLitMask == 0
            || teamState.EnergyLargeMechanismActive != large;
        if (mustRestart)
        {
            teamState.EnergyMechanismState = "activating";
            teamState.EnergyLargeMechanismActive = large;
            teamState.EnergyCurrentLitMask = large ? 0b00011 : 0b00001;
            teamState.EnergyActivatedGroupCount = 0;
            teamState.EnergyHitRingCount = 0;
            teamState.EnergyHitRingSum = 0;
            teamState.EnergyLastRingScore = 0;
            teamState.EnergyLastHitArmIndex = -1;
            teamState.EnergyLastHitFlashEndSec = 0.0;
            Array.Clear(teamState.EnergyHitRingsByArm, 0, teamState.EnergyHitRingsByArm.Length);
        }

        if (teamState.EnergyActivationOrder.All(index => index == 0))
        {
            for (int index = 0; index < Math.Min(EnergyMechanismVisualLogic.ArmCount, teamState.EnergyActivationOrder.Length); index++)
            {
                teamState.EnergyActivationOrder[index] = index;
            }
        }

        teamState.EnergyNextModuleDelaySec = 0.0;
        teamState.EnergyActivationWindowTimerSec = 0.0;
        teamState.EnergyLitModuleTimerSec = 0.0;
    }

    private bool TryBuildEnergyRotationSample(SimulationTeamState teamState, out EnergyRotationSample sample)
    {
        sample = default;
        SimulationEntity? mechanism = _world.Entities.FirstOrDefault(entity =>
            string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase));
        if (mechanism is null)
        {
            return false;
        }

        IReadOnlyList<ArmorPlateTarget> targets = SimulationCombatMath.GetEnergyMechanismTargets(
            mechanism,
            _world.MetersPerWorldUnit,
            _world.GameTimeSec,
            _player.Team,
            teamState);
        var byArm = new Dictionary<int, ArmorPlateTarget>();
        foreach (ArmorPlateTarget target in targets)
        {
            if (!SimulationCombatMath.TryParseEnergyArmIndex(target.Id, out string team, out int armIndex)
                || !string.Equals(team, _player.Team, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!byArm.TryGetValue(armIndex, out ArmorPlateTarget current)
                || target.EnergyRingScore > current.EnergyRingScore)
            {
                byArm[armIndex] = target;
            }
        }

        if (byArm.Count < 3)
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(_world.MetersPerWorldUnit, 1e-6);
        var points = byArm
            .OrderBy(pair => pair.Key)
            .Select(pair => ToEnergyPoint(pair.Value, metersPerWorldUnit))
            .ToArray();
        NumericsVector3 center = NumericsVector3.Zero;
        foreach (NumericsVector3 point in points)
        {
            center += point;
        }

        center /= points.Length;
        if (!TryEstimateEnergyPlaneNormal(points, center, out NumericsVector3 normal))
        {
            return false;
        }

        int selectedArm = ResolveTrackedEnergyArm(teamState, byArm.Keys);
        if (!byArm.TryGetValue(selectedArm, out ArmorPlateTarget selected))
        {
            return false;
        }

        double expectedYaw = ResolveEnergyRotorYawRadForDiagnostics(_world.GameTimeSec, teamState);
        double expectedOmega = ResolveEnergyRotorAngularVelocityRadPerSec(_world.GameTimeSec, teamState);
        sample = new EnergyRotationSample(
            _world.GameTimeSec,
            selectedArm,
            ToEnergyPoint(selected, metersPerWorldUnit),
            center,
            normal,
            expectedYaw,
            expectedOmega);
        return true;
    }

    private static int ResolveTrackedEnergyArm(SimulationTeamState teamState, IEnumerable<int> availableArms)
    {
        for (int arm = 0; arm < EnergyMechanismVisualLogic.ArmCount; arm++)
        {
            if ((teamState.EnergyCurrentLitMask & (1 << arm)) != 0)
            {
                return arm;
            }
        }

        return availableArms.Order().First();
    }

    private static NumericsVector3 ToEnergyPoint(ArmorPlateTarget target, double metersPerWorldUnit)
        => new((float)(target.X * metersPerWorldUnit), (float)target.HeightM, (float)(target.Y * metersPerWorldUnit));

    private static bool TryEstimateEnergyPlaneNormal(
        IReadOnlyList<NumericsVector3> points,
        NumericsVector3 center,
        out NumericsVector3 normal)
    {
        normal = NumericsVector3.Zero;
        for (int i = 0; i < points.Count; i++)
        {
            NumericsVector3 a = points[i] - center;
            for (int j = i + 1; j < points.Count; j++)
            {
                NumericsVector3 candidate = NumericsVector3.Cross(a, points[j] - center);
                if (candidate.LengthSquared() > normal.LengthSquared())
                {
                    normal = candidate;
                }
            }
        }

        if (normal.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        normal = NumericsVector3.Normalize(normal);
        return true;
    }

    private static double ComputeSignedAngularDelta(EnergyRotationSample previous, EnergyRotationSample current)
    {
        NumericsVector3 from = previous.Position - previous.Center;
        NumericsVector3 to = current.Position - current.Center;
        if (from.LengthSquared() <= 1e-8f || to.LengthSquared() <= 1e-8f)
        {
            return 0.0;
        }

        from = NumericsVector3.Normalize(from);
        to = NumericsVector3.Normalize(to);
        double dot = Math.Clamp(NumericsVector3.Dot(from, to), -1.0f, 1.0f);
        double unsigned = Math.Acos(dot);
        double sign = NumericsVector3.Dot(current.PlaneNormal, NumericsVector3.Cross(from, to)) >= 0.0f ? 1.0 : -1.0;
        return unsigned * sign;
    }

    private static double ComputeWrappedAngleDelta(double previousYawRad, double currentYawRad)
    {
        double delta = currentYawRad - previousYawRad;
        return Math.Atan2(Math.Sin(delta), Math.Cos(delta));
    }

    private static double ResolveEnergyRotorYawRadForDiagnostics(double gameTimeSec, SimulationTeamState? teamState)
    {
        const double smallSpeedRadPerSec = Math.PI / 3.0;
        const double largeActiveA = 0.9125;
        const double largeActiveOmega = 1.942;
        const double largeActiveB = 2.090 - largeActiveA;
        int direction = teamState?.EnergyRotorDirectionSign != 0 ? teamState?.EnergyRotorDirectionSign ?? 1 : 1;
        if (teamState is null
            || (!string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase)))
        {
            return direction * gameTimeSec * smallSpeedRadPerSec;
        }

        bool largeActive = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            && teamState.EnergyLargeMechanismActive;
        double safeTime = Math.Max(0.0, gameTimeSec - teamState.EnergyStateStartTimeSec);
        double basePhase = Math.Max(0.0, teamState.EnergyStateStartTimeSec) * smallSpeedRadPerSec;
        if (!largeActive)
        {
            return direction * gameTimeSec * smallSpeedRadPerSec;
        }

        double speedIntegral = largeActiveB * safeTime
            + largeActiveA / largeActiveOmega * (1.0 - Math.Cos(largeActiveOmega * safeTime));
        return direction * (basePhase + speedIntegral);
    }

    private static double ResolveEnergyRotorAngularVelocityRadPerSec(double gameTimeSec, SimulationTeamState? teamState)
    {
        const double smallSpeedRadPerSec = Math.PI / 3.0;
        const double largeActiveA = 0.9125;
        const double largeActiveOmega = 1.942;
        const double largeActiveB = 2.090 - largeActiveA;
        int direction = teamState?.EnergyRotorDirectionSign != 0 ? teamState?.EnergyRotorDirectionSign ?? 1 : 1;
        if (teamState is null
            || !string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            || !teamState.EnergyLargeMechanismActive)
        {
            return direction * smallSpeedRadPerSec;
        }

        double safeTime = Math.Max(0.0, gameTimeSec - teamState.EnergyStateStartTimeSec);
        return direction * (largeActiveB + largeActiveA * Math.Sin(largeActiveOmega * safeTime));
    }

    private double KeyAxis(TkKeys positive, TkKeys negative)
    {
        double value = 0.0;
        if (KeyboardState.IsKeyDown(positive))
        {
            value += 1.0;
        }

        if (KeyboardState.IsKeyDown(negative))
        {
            value -= 1.0;
        }

        return value;
    }

    private static double ResolveMoveSpeedMps(SimulationEntity entity)
    {
        double baseSpeedMps = 3.0 * Math.Sqrt(Math.Max(entity.ChassisDrivePowerLimitW, 10.0) / 50.0);
        baseSpeedMps *= Math.Max(0.2, entity.ChassisSpeedScale);
        baseSpeedMps = Math.Min(6.0, baseSpeedMps);
        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(5.8, baseSpeedMps);
        }

        if (string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(4.8, baseSpeedMps);
        }

        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(3.6, baseSpeedMps);
        }

        return Math.Min(5.4, baseSpeedMps);
    }

    private void DrawWorldView()
    {
        int w = ClientSize.X;
        int h = ClientSize.Y;
        if (!_renderedTerrain3DThisFrame)
        {
            float horizon = h * (0.48f + (float)_cameraPitchDeg / 120f);
            _batch.FillRect(0, 0, w, horizon, Rgba.FromBytes(27, 43, 58));
            _batch.FillRect(0, horizon, w, h - horizon, Rgba.FromBytes(27, 33, 32));
            _batch.FillRect(0, horizon - 1, w, 2, Rgba.FromBytes(110, 124, 132, 110));

            if (!DrawTerrainScene())
            {
                DrawPerspectiveGrid(horizon);
            }

            DrawMapBounds();
            DrawFacilities();
        }

        DrawEntities();
        DrawProjectiles();
        if (!_renderedTerrain3DThisFrame)
        {
            DrawEnergyMechanisms();
        }

        DrawCrosshair();
    }

    private bool DrawTerrainScene3D()
    {
        if (_terrainRenderer is null || !_terrainRenderer.IsReady)
        {
            return false;
        }

        if (ShouldRenderOperatorFirstPersonScene())
        {
            DrawOperatorFirstPersonTerrainScene3D();
            return true;
        }

        if (!IsMatchLive)
        {
            DrawStartupTerrainScene3D();
            return true;
        }

        DrawOperatorFirstPersonTerrainScene3D();
        return true;
    }

    private void DrawOperatorFirstPersonTerrainScene3D()
    {
        float metersPerWorldUnit = (float)Math.Max(_world.MetersPerWorldUnit, 1e-6);
        var cameraPosition = new Vector3(
            (float)_player.X * metersPerWorldUnit,
            (float)(1.20 + _player.GroundHeightM + _player.AirborneHeightM),
            (float)_player.Y * metersPerWorldUnit);
        float yawRad = MathHelper.DegreesToRadians((float)_cameraYawDeg);
        float pitchRad = MathHelper.DegreesToRadians((float)_cameraPitchDeg);
        var forward = new Vector3(
            MathF.Cos(yawRad) * MathF.Cos(pitchRad),
            MathF.Sin(pitchRad),
            MathF.Sin(yawRad) * MathF.Cos(pitchRad));
        if (forward.LengthSquared <= 1e-6f)
        {
            forward = Vector3.UnitX;
        }

        float aspect = ClientSize.Y == 0 ? 1.0f : ClientSize.X / (float)ClientSize.Y;
        Matrix4 view = Matrix4.LookAt(cameraPosition, cameraPosition + forward.Normalized(), Vector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(70.0f),
            aspect,
            0.025f,
            1200.0f);
        Matrix4 viewProjection = view * projection;
        _lastViewProjection3D = viewProjection;
        _lastCameraPosition3D = cameraPosition;
        _terrainRenderer?.Draw(viewProjection, cameraPosition);
    }

    private void DrawStartupTerrainScene3D()
    {
        float metersPerWorldUnit = (float)Math.Max(_world.MetersPerWorldUnit, 1e-6);
        var playerScene = new Vector3(
            (float)_player.X * metersPerWorldUnit,
            0.0f,
            (float)_player.Y * metersPerWorldUnit);
        var fieldCenter = new Vector3(
            Math.Max(1.0f, (float)_mapPreset.FieldLengthM) * 0.5f,
            0.0f,
            Math.Max(1.0f, (float)_mapPreset.FieldWidthM) * 0.5f);
        Vector3 toCenter = fieldCenter - playerScene;
        toCenter.Y = 0.0f;
        if (toCenter.LengthSquared <= 1e-6f)
        {
            toCenter = string.Equals(_player.Team, "blue", StringComparison.OrdinalIgnoreCase)
                ? new Vector3(-1.0f, 0.0f, -0.45f)
                : new Vector3(1.0f, 0.0f, 0.45f);
        }

        toCenter = toCenter.Normalized();
        Vector3 focus = Vector3.Lerp(playerScene, fieldCenter, 0.36f) + new Vector3(0.0f, 0.80f, 0.0f);
        Vector3 cameraPosition = focus - toCenter * 12.5f + new Vector3(0.0f, 7.2f, 0.0f);
        float aspect = ClientSize.Y == 0 ? 1.0f : ClientSize.X / (float)ClientSize.Y;
        Matrix4 view = Matrix4.LookAt(cameraPosition, focus, Vector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(58.0f),
            aspect,
            0.04f,
            1200.0f);
        Matrix4 viewProjection = view * projection;
        _lastViewProjection3D = viewProjection;
        _lastCameraPosition3D = cameraPosition;
        _terrainRenderer!.Draw(viewProjection, cameraPosition);
    }

    private bool DrawTerrainScene()
    {
        if (_terrainScene is null || _terrainScene.Triangles.Count == 0)
        {
            return false;
        }

        int drawn = 0;
        foreach (TerrainTriangle triangle in _terrainScene.Triangles)
        {
            if (DrawTerrainTriangle(triangle))
            {
                drawn++;
            }
        }

        if (drawn > 0)
        {
            _batch.FillRect(0, ClientSize.Y - 118, ClientSize.X, 118, Rgba.FromBytes(8, 10, 12, 56));
        }

        return drawn > 0;
    }

    private void DrawEnergyMechanism3DOverlay(Matrix4 viewProjection)
    {
        foreach (SimulationEntity mechanism in _world.Entities.Where(entity =>
                     string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)))
        {
            SimulationTeamState bodyState = _world.GetOrCreateTeamState(_player.Team);
            DrawEnergyMechanismRotorBody3D(mechanism, bodyState, viewProjection);
            foreach (SimulationTeamState teamState in _world.Teams.Values)
            {
                DrawEnergyMechanismTeam3D(mechanism, teamState, viewProjection);
            }
        }
    }

    private (int Arm, ArmorPlateTarget Disk)[] ResolveEnergyDiskTargets(SimulationEntity mechanism, SimulationTeamState teamState)
    {
        IReadOnlyList<ArmorPlateTarget> targets = SimulationCombatMath.GetEnergyMechanismTargets(
            mechanism,
            _world.MetersPerWorldUnit,
            EnergyVisualGameTimeSec,
            teamState.Team,
            teamState);
        return targets
            .Where(target => SimulationCombatMath.TryParseEnergyArmIndex(target.Id, out _, out _))
            .GroupBy(target =>
            {
                SimulationCombatMath.TryParseEnergyArmIndex(target.Id, out _, out int armIndex);
                return armIndex;
            })
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                ArmorPlateTarget disk = group
                    .OrderBy(target => Math.Abs((target.EnergyRingScore <= 0 ? 10 : target.EnergyRingScore) - 10))
                    .First();
                return (Arm: group.Key, Disk: disk);
            })
            .ToArray();
    }

    private void DrawEnergyMechanismRotorBody3D(
        SimulationEntity mechanism,
        SimulationTeamState teamState,
        Matrix4 viewProjection)
    {
        (int Arm, ArmorPlateTarget Disk)[] disks = ResolveEnergyDiskTargets(mechanism, teamState);
        if (disks.Length < 3)
        {
            return;
        }

        float metersPerWorldUnit = (float)Math.Max(_world.MetersPerWorldUnit, 1e-6);
        Vector3 center = ResolveEnergyCenter(disks, metersPerWorldUnit);
        Vector3[] points = disks.Select(item => ToEnergyScenePoint(item.Disk, metersPerWorldUnit)).ToArray();
        if (!TryEstimateRotorPlane(points, center, out Vector3 planeNormal))
        {
            planeNormal = center - _lastCameraPosition3D;
            if (planeNormal.LengthSquared <= 1e-8f)
            {
                planeNormal = Vector3.UnitZ;
            }

            planeNormal = planeNormal.Normalized();
        }

        if (Vector3.Dot(planeNormal, _lastCameraPosition3D - center) < 0f)
        {
            planeNormal = -planeNormal;
        }

        bool active = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            || string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase);
        float rotorRadius = points.Max(point => (point - center).Length);
        float averageDiskRadius = disks
            .Select(item => ResolveEnergyDiskRadiusM(item.Disk))
            .DefaultIfEmpty(0.24f)
            .Average();
        Rgba hubColor = active ? Rgba.FromBytes(22, 27, 34, 252) : Rgba.FromBytes(16, 18, 22, 238);
        DrawWorldOrientedAnnulus(
            center - planeNormal * 0.018f,
            planeNormal,
            Vector3.UnitY,
            0f,
            rotorRadius + averageDiskRadius * 0.72f,
            Rgba.FromBytes(2, 4, 7, 172),
            viewProjection);
        DrawWorldOrientedAnnulus(center + planeNormal * 0.012f, planeNormal, Vector3.UnitY, 0f, 0.34f, Rgba.FromBytes(5, 8, 12, 246), viewProjection);
        DrawWorldOrientedAnnulus(center + planeNormal * 0.018f, planeNormal, Vector3.UnitY, 0.15f, 0.36f, hubColor, viewProjection);

        for (int index = 0; index < disks.Length; index++)
        {
            Vector3 point = points[index];
            Vector3 radial = point - center;
            float length = radial.Length;
            if (length <= 1e-5f)
            {
                continue;
            }

            radial /= length;
            Vector3 tangent = Vector3.Cross(planeNormal, radial);
            if (tangent.LengthSquared <= 1e-8f)
            {
                tangent = Vector3.Cross(center - _lastCameraPosition3D, radial);
            }

            if (tangent.LengthSquared <= 1e-8f)
            {
                continue;
            }

            tangent = tangent.Normalized();
            float diskRadius = ResolveEnergyDiskRadiusM(disks[index].Disk);
            Rgba bladeFill = index % 2 == 0
                ? Rgba.FromBytes(7, 9, 13, 250)
                : Rgba.FromBytes(62, 66, 74, 238);
            Rgba bladeEdge = Rgba.FromBytes(1, 3, 6, 248);
            DrawEnergyBlade3D(center, radial, tangent, planeNormal, length, diskRadius, bladeFill, bladeEdge, viewProjection);
            DrawEnergyMechanismDiskBody3D(
                point,
                ResolveEnergyPlateNormal(disks[index].Disk, center, point),
                tangent,
                diskRadius,
                Rgba.FromBytes(7, 9, 13, 248),
                Rgba.FromBytes(80, 88, 98, 204),
                viewProjection);
        }
    }

    private void DrawEnergyBlade3D(
        Vector3 center,
        Vector3 radial,
        Vector3 tangent,
        Vector3 planeNormal,
        float length,
        float diskRadius,
        Rgba fill,
        Rgba edge,
        Matrix4 viewProjection)
    {
        Vector3 root = center + radial * MathF.Max(0.23f, length * 0.18f);
        Vector3 shoulder = center + radial * (length * 0.52f);
        Vector3 tip = center + radial * MathF.Max(length * 0.72f, length - diskRadius * 0.40f);
        float rootHalf = Math.Clamp(length * 0.050f, 0.055f, 0.105f);
        float shoulderHalf = Math.Clamp(length * 0.135f, 0.15f, 0.30f);
        float tipHalf = Math.Clamp(length * 0.160f, 0.17f, 0.34f);

        DrawWorldQuad(
            root - tangent * rootHalf,
            root + tangent * rootHalf,
            shoulder + tangent * shoulderHalf,
            shoulder - tangent * shoulderHalf,
            edge,
            viewProjection);
        DrawWorldQuad(
            shoulder - tangent * shoulderHalf,
            shoulder + tangent * shoulderHalf,
            tip + tangent * tipHalf,
            tip - tangent * tipHalf,
            fill,
            viewProjection);
        Vector3 inner = center + radial * (length * 0.37f) + planeNormal * 0.030f;
        Vector3 outer = center + radial * MathF.Max(length * 0.66f, length - diskRadius * 1.28f) + planeNormal * 0.030f;
        DrawWorldTubeLine(inner, outer, Math.Clamp(length * 0.018f, 0.020f, 0.040f), Rgba.FromBytes(30, 35, 44, 210), viewProjection);
        float thickness = Math.Clamp(length * 0.018f, 0.022f, 0.055f);
        DrawWorldQuad(
            root - tangent * rootHalf + planeNormal * thickness,
            shoulder - tangent * shoulderHalf + planeNormal * thickness,
            shoulder - tangent * shoulderHalf - planeNormal * thickness,
            root - tangent * rootHalf - planeNormal * thickness,
            edge.WithAlpha(190),
            viewProjection);
        DrawWorldQuad(
            root + tangent * rootHalf - planeNormal * thickness,
            shoulder + tangent * shoulderHalf - planeNormal * thickness,
            shoulder + tangent * shoulderHalf + planeNormal * thickness,
            root + tangent * rootHalf + planeNormal * thickness,
            edge.WithAlpha(190),
            viewProjection);
        DrawWorldTubeLine(center + planeNormal * 0.020f, tip + planeNormal * 0.020f, Math.Clamp(length * 0.010f, 0.014f, 0.032f), edge.WithAlpha(186), viewProjection);
    }

    private void DrawEnergyMechanismDiskBody3D(
        Vector3 center,
        Vector3 normal,
        Vector3 upAxis,
        float radius,
        Rgba body,
        Rgba rim,
        Matrix4 viewProjection)
    {
        Vector3 safeNormal = normal.LengthSquared <= 1e-8f ? Vector3.UnitZ : normal.Normalized();
        Vector3 front = center + safeNormal * 0.024f;
        DrawWorldOrientedAnnulus(front - safeNormal * 0.010f, safeNormal, upAxis, 0f, radius * 1.16f, Rgba.FromBytes(2, 4, 7, 248), viewProjection);
        DrawWorldOrientedAnnulus(front, safeNormal, upAxis, 0f, radius * 0.72f, body, viewProjection);
        DrawWorldOrientedAnnulus(front + safeNormal * 0.004f, safeNormal, upAxis, radius * 0.72f, radius * 1.04f, rim, viewProjection);
        DrawWorldOrientedAnnulus(front + safeNormal * 0.008f, safeNormal, upAxis, radius * 0.18f, radius * 0.30f, Rgba.FromBytes(42, 48, 56, 210), viewProjection);
    }

    private void DrawEnergyMechanismTeam3D(SimulationEntity mechanism, SimulationTeamState teamState, Matrix4 viewProjection)
    {
        float metersPerWorldUnit = (float)Math.Max(_world.MetersPerWorldUnit, 1e-6);
        (int Arm, ArmorPlateTarget Disk)[] diskTargets = ResolveEnergyDiskTargets(mechanism, teamState);
        if (diskTargets.Length < 3)
        {
            return;
        }

        Vector3 center = ResolveEnergyCenter(diskTargets, metersPerWorldUnit);
        Rgba teamColor = ResolvePureTeamLightColor(teamState.Team);
        bool active = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            || string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase);
        bool hasVisibleState = active
            || teamState.EnergyCurrentLitMask != 0
            || teamState.EnergyActivatedGroupCount > 0
            || teamState.EnergyHitRingsByArm.Any(score => score > 0);
        if (!hasVisibleState)
        {
            return;
        }

        Vector3[] points = diskTargets.Select(item => ToEnergyScenePoint(item.Disk, metersPerWorldUnit)).ToArray();
        if (!TryEstimateRotorPlane(points, center, out Vector3 planeNormal))
        {
            planeNormal = _lastCameraPosition3D - center;
        }

        if (planeNormal.LengthSquared <= 1e-8f)
        {
            planeNormal = Vector3.UnitZ;
        }

        planeNormal = planeNormal.Normalized();
        if (Vector3.Dot(planeNormal, _lastCameraPosition3D - center) < 0f)
        {
            planeNormal = -planeNormal;
        }

        DrawWorldOrientedAnnulus(center + planeNormal * 0.045f, planeNormal, Vector3.UnitY, 0f, 0.13f, active ? teamColor.WithAlpha(232) : Rgba.FromBytes(80, 88, 96, 180), viewProjection);

        foreach ((int armIndex, ArmorPlateTarget disk) in diskTargets)
        {
            Vector3 point = ToEnergyScenePoint(disk, metersPerWorldUnit);
            EnergyMechanismArmDisplayState state = EnergyMechanismVisualLogic.ResolveArmState(teamState, armIndex, EnergyVisualGameTimeSec);
            bool pending = state.Kind == EnergyMechanismArmDisplayKind.Pending;
            bool lit = state.Kind is EnergyMechanismArmDisplayKind.Hit
                or EnergyMechanismArmDisplayKind.ActivatedByProgress
                or EnergyMechanismArmDisplayKind.Completed;
            Rgba armColor = lit
                ? teamColor.WithAlpha(state.Flashing ? 255 : 230)
                : Rgba.FromBytes(18, 22, 28, 176);
            float armWidth = lit ? 0.060f : 0.030f;
            float diskRadius = ResolveEnergyDiskRadiusM(disk);
            Vector3 diskNormal = ResolveEnergyPlateNormal(disk, center, point);
            if (lit)
            {
                DrawWorldTubeLine(center + planeNormal * 0.050f, point + planeNormal * 0.050f, armWidth, armColor, viewProjection);
            }
            else if (pending)
            {
                DrawEnergyPendingArmFlow3D(center, point, planeNormal, teamColor, viewProjection);
            }

            DrawWorldOrientedAnnulus(
                point + diskNormal * 0.040f,
                diskNormal,
                Vector3.UnitY,
                pending ? diskRadius * 0.74f : diskRadius * 0.82f,
                pending ? diskRadius * 1.10f : diskRadius * 1.02f,
                (lit || pending ? teamColor : Rgba.FromBytes(86, 92, 102, 190)).WithAlpha(lit || pending ? 255 : 168),
                viewProjection);
            if (pending)
            {
                DrawWorldOrientedAnnulus(
                    point + diskNormal * 0.048f,
                    diskNormal,
                    Vector3.UnitY,
                    diskRadius * 1.13f,
                    diskRadius * 1.19f,
                    teamColor.WithAlpha(232),
                    viewProjection);
            }

            if (pending)
            {
                DrawEnergyPendingPattern3D(point, diskNormal, diskRadius, teamColor, viewProjection);
            }
            else if (lit)
            {
                DrawEnergyScoreRing3D(point, diskNormal, diskRadius, state.RingScore, teamColor.WithAlpha(state.Flashing ? 255 : 235), viewProjection);
            }
        }
    }

    private static float ResolveEnergyDiskRadiusM(ArmorPlateTarget disk)
        => Math.Clamp((float)Math.Max(disk.WidthM, disk.HeightSpanM) * 0.50f, 0.095f, 0.32f);

    private static Vector3 ResolveEnergyPlateNormal(ArmorPlateTarget disk, Vector3 center, Vector3 point)
    {
        Vector3 normal = new((float)disk.NormalXM, (float)disk.NormalYM, (float)disk.NormalZM);
        if (normal.LengthSquared > 1e-8f)
        {
            return normal.Normalized();
        }

        normal = point - center;
        if (normal.LengthSquared <= 1e-8f)
        {
            return Vector3.UnitZ;
        }

        return normal.Normalized();
    }

    private void DrawEnergyPendingPattern3D(Vector3 center, Vector3 normal, float diskRadius, Rgba color, Matrix4 viewProjection)
    {
        Vector3 safeNormal = normal.LengthSquared <= 1e-8f ? Vector3.UnitZ : normal.Normalized();
        Vector3 front = center + safeNormal * 0.056f;
        ResolveEnergyPlaneBasis(safeNormal, Vector3.UnitY, out Vector3 right, out Vector3 up);
        DrawWorldOrientedAnnulus(front, safeNormal, up, diskRadius * 0.82f, diskRadius * 1.00f, color.WithAlpha(252), viewProjection);
        DrawWorldOrientedAnnulus(front + safeNormal * 0.004f, safeNormal, up, diskRadius * 0.46f, diskRadius * 0.58f, color.WithAlpha(236), viewProjection);
        DrawWorldOrientedAnnulus(front + safeNormal * 0.008f, safeNormal, up, diskRadius * 0.16f, diskRadius * 0.28f, color.WithAlpha(228), viewProjection);
        foreach (Vector3 axis in new[] { right, -right, up, -up })
        {
            DrawEnergyPendingSpoke3D(
                front + safeNormal * 0.012f,
                axis,
                axis == right || axis == -right ? up : right,
                diskRadius * 0.58f,
                diskRadius * 0.94f,
                diskRadius * 0.040f,
                diskRadius * 0.095f,
                color.WithAlpha(224),
                viewProjection);
        }
    }

    private void DrawEnergyPendingSpoke3D(
        Vector3 center,
        Vector3 axis,
        Vector3 side,
        float innerRadius,
        float outerRadius,
        float innerHalfWidth,
        float outerHalfWidth,
        Rgba color,
        Matrix4 viewProjection)
    {
        Vector3 safeAxis = axis.LengthSquared <= 1e-8f ? Vector3.UnitX : axis.Normalized();
        Vector3 safeSide = side.LengthSquared <= 1e-8f ? Vector3.UnitY : side.Normalized();
        Vector3 inner = center + safeAxis * innerRadius;
        Vector3 outer = center + safeAxis * outerRadius;
        DrawWorldQuad(
            inner - safeSide * innerHalfWidth,
            inner + safeSide * innerHalfWidth,
            outer + safeSide * outerHalfWidth,
            outer - safeSide * outerHalfWidth,
            color,
            viewProjection);
    }

    private void DrawEnergyPendingArmFlow3D(
        Vector3 center,
        Vector3 point,
        Vector3 planeNormal,
        Rgba color,
        Matrix4 viewProjection)
    {
        Vector3 axis = point - center;
        float length = axis.Length;
        if (length <= 1e-5f)
        {
            return;
        }

        Vector3 direction = axis / length;
        Vector3 side = Vector3.Cross(planeNormal, direction);
        if (side.LengthSquared <= 1e-8f)
        {
            side = Vector3.Cross(Vector3.UnitY, direction);
        }

        if (side.LengthSquared <= 1e-8f)
        {
            return;
        }

        side = side.Normalized();
        const int ChevronCount = 11;
        float phase = (float)(EnergyVisualGameTimeSec * 2.20 % 1.0);
        float arrowLength = Math.Clamp(length * 0.036f, 0.050f, 0.090f);
        float halfWidth = Math.Clamp(length * 0.023f, 0.030f, 0.058f);
        float lineWidth = Math.Clamp(length * 0.010f, 0.009f, 0.020f);
        for (int index = 0; index < ChevronCount; index++)
        {
            float local = ((index / (float)ChevronCount) + phase / ChevronCount) % 1f;
            float t = 0.36f + local * 0.32f;
            int alpha = 190 + (int)MathF.Round(60f * (1f - local));
            Vector3 apex = center + direction * (length * t) + planeNormal * 0.078f;
            Vector3 tail = apex - direction * arrowLength;
            DrawWorldTubeLine(
                tail + side * halfWidth,
                apex,
                lineWidth,
                color.WithAlpha(Math.Clamp(alpha, 108, 238)),
                viewProjection);
            DrawWorldTubeLine(
                tail - side * halfWidth,
                apex,
                lineWidth,
                color.WithAlpha(Math.Clamp(alpha, 108, 238)),
                viewProjection);
        }
    }

    private void DrawEnergyScoreRing3D(Vector3 center, Vector3 normal, float diskRadius, int ringScore, Rgba color, Matrix4 viewProjection)
    {
        int score = Math.Clamp(ringScore <= 0 ? 10 : ringScore, 1, 10);
        float outer = diskRadius * (11 - score) / 10f;
        float inner = score >= 10 ? 0f : diskRadius * (10 - score) / 10f;
        DrawWorldOrientedAnnulus(center + normal * 0.018f, normal, Vector3.UnitY, inner, Math.Max(inner + 0.004f, outer), color, viewProjection);
    }

    private static Vector3 ResolveEnergyCenter(IEnumerable<(int Arm, ArmorPlateTarget Disk)> disks, float metersPerWorldUnit)
    {
        ArmorPlateTarget[] targets = disks.Select(item => item.Disk).ToArray();
        return new Vector3(
            targets.Average(target => (float)(target.X * metersPerWorldUnit)),
            targets.Average(target => (float)target.HeightM),
            targets.Average(target => (float)(target.Y * metersPerWorldUnit)));
    }

    private static Vector3 ToEnergyScenePoint(ArmorPlateTarget target, float metersPerWorldUnit)
        => new(
            (float)(target.X * metersPerWorldUnit),
            (float)target.HeightM,
            (float)(target.Y * metersPerWorldUnit));

    private static bool TryEstimateRotorPlane(IReadOnlyList<Vector3> points, Vector3 center, out Vector3 normal)
    {
        normal = Vector3.Zero;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 a = points[i] - center;
            for (int j = i + 1; j < points.Count; j++)
            {
                Vector3 candidate = Vector3.Cross(a, points[j] - center);
                if (candidate.LengthSquared > normal.LengthSquared)
                {
                    normal = candidate;
                }
            }
        }

        if (normal.LengthSquared <= 1e-8f)
        {
            return false;
        }

        normal = normal.Normalized();
        return true;
    }

    private static void ResolveEnergyPlaneBasis(Vector3 normalAxis, Vector3 upAxis, out Vector3 right, out Vector3 up)
    {
        Vector3 normal = normalAxis.LengthSquared <= 1e-8f ? Vector3.UnitZ : normalAxis.Normalized();
        up = upAxis.LengthSquared <= 1e-8f ? Vector3.UnitY : upAxis.Normalized();
        up -= normal * Vector3.Dot(up, normal);
        if (up.LengthSquared <= 1e-8f)
        {
            up = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.96f
                ? Vector3.UnitX
                : Vector3.UnitY;
            up -= normal * Vector3.Dot(up, normal);
        }

        up = up.LengthSquared <= 1e-8f ? Vector3.UnitZ : up.Normalized();
        right = Vector3.Cross(normal, up);
        right = right.LengthSquared <= 1e-8f ? Vector3.UnitX : right.Normalized();
    }

    private void DrawWorldTriangle(Vector3 a, Vector3 b, Vector3 c, Rgba color, Matrix4 viewProjection)
    {
        if (TryProjectScenePoint3D(a, viewProjection, out ScreenPoint p0)
            && TryProjectScenePoint3D(b, viewProjection, out ScreenPoint p1)
            && TryProjectScenePoint3D(c, viewProjection, out ScreenPoint p2))
        {
            _batch.Triangle(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, color);
        }
    }

    private void DrawWorldQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Rgba color, Matrix4 viewProjection)
    {
        if (TryProjectScenePoint3D(a, viewProjection, out ScreenPoint p0)
            && TryProjectScenePoint3D(b, viewProjection, out ScreenPoint p1)
            && TryProjectScenePoint3D(c, viewProjection, out ScreenPoint p2)
            && TryProjectScenePoint3D(d, viewProjection, out ScreenPoint p3))
        {
            _batch.Triangle(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, color);
            _batch.Triangle(p0.X, p0.Y, p2.X, p2.Y, p3.X, p3.Y, color);
        }
    }

    private void DrawWorldTubeLine(Vector3 start, Vector3 end, float widthM, Rgba color, Matrix4 viewProjection)
    {
        Vector3 direction = end - start;
        if (direction.LengthSquared <= 1e-8f)
        {
            return;
        }

        Vector3 toCamera = _lastCameraPosition3D - (start + end) * 0.5f;
        Vector3 side = Vector3.Cross(direction, toCamera);
        if (side.LengthSquared <= 1e-8f)
        {
            side = Vector3.Cross(direction, Vector3.UnitY);
        }

        if (side.LengthSquared <= 1e-8f)
        {
            return;
        }

        side = side.Normalized() * widthM;
        if (TryProjectScenePoint3D(start - side, viewProjection, out ScreenPoint a)
            && TryProjectScenePoint3D(start + side, viewProjection, out ScreenPoint b)
            && TryProjectScenePoint3D(end + side, viewProjection, out ScreenPoint c)
            && TryProjectScenePoint3D(end - side, viewProjection, out ScreenPoint d))
        {
            _batch.Triangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, color);
            _batch.Triangle(a.X, a.Y, c.X, c.Y, d.X, d.Y, color);
        }
    }

    private void DrawWorldBillboardCircle(Vector3 center, float radiusM, Rgba color, Matrix4 viewProjection, bool filled)
    {
        if (filled)
        {
            DrawWorldBillboardRing(center, 0f, radiusM, color, viewProjection);
            return;
        }

        DrawWorldBillboardRing(center, radiusM * 0.82f, radiusM, color, viewProjection);
    }

    private void DrawWorldBillboardDisk(Vector3 center, Vector3 normalAxis, float radiusM, Rgba color, Matrix4 viewProjection)
        => DrawWorldOrientedAnnulus(center, normalAxis, Vector3.UnitY, 0f, radiusM, color, viewProjection);

    private void DrawWorldBillboardRing(Vector3 center, float innerRadiusM, float outerRadiusM, Rgba color, Matrix4 viewProjection)
    {
        Vector3 forward = center - _lastCameraPosition3D;
        if (forward.LengthSquared <= 1e-8f)
        {
            forward = Vector3.UnitZ;
        }

        forward = forward.Normalized();
        Vector3 right = Vector3.Cross(Vector3.UnitY, forward);
        if (right.LengthSquared <= 1e-8f)
        {
            right = Vector3.UnitX;
        }

        right = right.Normalized();
        Vector3 up = Vector3.Cross(forward, right).Normalized();
        int segments = 48;
        for (int index = 0; index < segments; index++)
        {
            float a0 = index * MathF.Tau / segments;
            float a1 = (index + 1) * MathF.Tau / segments;
            Vector3 outer0 = center + (right * MathF.Cos(a0) + up * MathF.Sin(a0)) * outerRadiusM;
            Vector3 outer1 = center + (right * MathF.Cos(a1) + up * MathF.Sin(a1)) * outerRadiusM;
            Vector3 inner0 = center + (right * MathF.Cos(a0) + up * MathF.Sin(a0)) * innerRadiusM;
            Vector3 inner1 = center + (right * MathF.Cos(a1) + up * MathF.Sin(a1)) * innerRadiusM;
            if (!TryProjectScenePoint3D(outer0, viewProjection, out ScreenPoint p0)
                || !TryProjectScenePoint3D(outer1, viewProjection, out ScreenPoint p1)
                || !TryProjectScenePoint3D(inner0, viewProjection, out ScreenPoint p2)
                || !TryProjectScenePoint3D(inner1, viewProjection, out ScreenPoint p3))
            {
                continue;
            }

            _batch.Triangle(p0.X, p0.Y, p1.X, p1.Y, p3.X, p3.Y, color);
            _batch.Triangle(p0.X, p0.Y, p3.X, p3.Y, p2.X, p2.Y, color);
        }
    }

    private void DrawWorldOrientedAnnulus(
        Vector3 center,
        Vector3 normalAxis,
        Vector3 upAxis,
        float innerRadiusM,
        float outerRadiusM,
        Rgba color,
        Matrix4 viewProjection)
    {
        float inner = Math.Max(0f, innerRadiusM);
        float outer = Math.Max(inner + 0.002f, outerRadiusM);
        Vector3 normal = normalAxis.LengthSquared <= 1e-8f ? Vector3.UnitZ : normalAxis.Normalized();
        Vector3 up = upAxis.LengthSquared <= 1e-8f ? Vector3.UnitY : upAxis.Normalized();
        up -= normal * Vector3.Dot(up, normal);
        if (up.LengthSquared <= 1e-8f)
        {
            up = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.96f
                ? Vector3.UnitX
                : Vector3.UnitY;
            up -= normal * Vector3.Dot(up, normal);
        }

        if (up.LengthSquared <= 1e-8f)
        {
            up = Vector3.UnitZ;
        }
        else
        {
            up = up.Normalized();
        }

        Vector3 right = Vector3.Cross(normal, up);
        if (right.LengthSquared <= 1e-8f)
        {
            right = Vector3.UnitX;
        }
        else
        {
            right = right.Normalized();
        }

        int segments = 48;
        for (int index = 0; index < segments; index++)
        {
            float a0 = index * MathF.Tau / segments;
            float a1 = (index + 1) * MathF.Tau / segments;
            Vector3 outer0 = center + (right * MathF.Cos(a0) + up * MathF.Sin(a0)) * outer;
            Vector3 outer1 = center + (right * MathF.Cos(a1) + up * MathF.Sin(a1)) * outer;
            if (!TryProjectScenePoint3D(outer0, viewProjection, out ScreenPoint p0)
                || !TryProjectScenePoint3D(outer1, viewProjection, out ScreenPoint p1))
            {
                continue;
            }

            if (inner <= 0.001f)
            {
                if (TryProjectScenePoint3D(center, viewProjection, out ScreenPoint pc))
                {
                    _batch.Triangle(pc.X, pc.Y, p0.X, p0.Y, p1.X, p1.Y, color);
                }

                continue;
            }

            Vector3 inner0 = center + (right * MathF.Cos(a0) + up * MathF.Sin(a0)) * inner;
            Vector3 inner1 = center + (right * MathF.Cos(a1) + up * MathF.Sin(a1)) * inner;
            if (!TryProjectScenePoint3D(inner0, viewProjection, out ScreenPoint p2)
                || !TryProjectScenePoint3D(inner1, viewProjection, out ScreenPoint p3))
            {
                continue;
            }

            _batch.Triangle(p0.X, p0.Y, p1.X, p1.Y, p3.X, p3.Y, color);
            _batch.Triangle(p0.X, p0.Y, p3.X, p3.Y, p2.X, p2.Y, color);
        }
    }

    private bool TryProjectScenePoint3D(Vector3 scenePoint, Matrix4 viewProjection, out ScreenPoint point)
    {
        Vector4 clip = new(scenePoint.X, scenePoint.Y, scenePoint.Z, 1.0f);
        clip = Vector4.TransformRow(clip, viewProjection);
        if (MathF.Abs(clip.W) <= 1e-6f)
        {
            point = default;
            return false;
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        float ndcZ = clip.Z / clip.W;
        if (clip.W <= 0f || ndcZ < -1.0f || ndcZ > 1.0f)
        {
            point = default;
            return false;
        }

        point = new ScreenPoint(
            (ndcX * 0.5f + 0.5f) * ClientSize.X,
            (1f - (ndcY * 0.5f + 0.5f)) * ClientSize.Y,
            Vector3.Distance(scenePoint, _lastCameraPosition3D));
        if (!float.IsFinite(point.X)
            || !float.IsFinite(point.Y)
            || point.X < -ClientSize.X
            || point.X > ClientSize.X * 2f
            || point.Y < -ClientSize.Y
            || point.Y > ClientSize.Y * 2f)
        {
            point = default;
            return false;
        }

        return true;
    }

    private bool DrawTerrainTriangle(TerrainTriangle triangle)
    {
        if (!TryProjectScenePoint(triangle.A, out ScreenPoint a)
            || !TryProjectScenePoint(triangle.B, out ScreenPoint b)
            || !TryProjectScenePoint(triangle.C, out ScreenPoint c))
        {
            return false;
        }

        float minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        float maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
        float maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        if (maxX < -32f || minX > ClientSize.X + 32f || maxY < -32f || minY > ClientSize.Y + 32f)
        {
            return false;
        }

        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 0.7f)
        {
            return false;
        }

        _batch.Triangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, triangle.Color);
        return true;
    }

    private void DrawPerspectiveGrid(float horizon)
    {
        int w = ClientSize.X;
        int h = ClientSize.Y;
        Rgba line = Rgba.FromBytes(82, 100, 96, 95);
        for (int i = -8; i <= 8; i++)
        {
            if (TryProjectRelative(i * 2.0, 2.0, 0, out ScreenPoint near)
                && TryProjectRelative(i * 7.0, 34.0, 0, out ScreenPoint far))
            {
                _batch.Line(near.X, near.Y, far.X, far.Y, 1.2f, line);
            }
        }

        for (int z = 4; z <= 42; z += 4)
        {
            if (TryProjectRelative(-18.0, z, 0, out ScreenPoint left)
                && TryProjectRelative(18.0, z, 0, out ScreenPoint right))
            {
                _batch.Line(left.X, left.Y, right.X, right.Y, 1.0f, Rgba.FromBytes(64, 80, 78, 80));
            }
        }

        _batch.FillRect(0, horizon, w, 22, Rgba.FromBytes(18, 20, 22, 36));
        _batch.FillRect(0, h - 118, w, 118, Rgba.FromBytes(8, 10, 12, 82));
    }

    private void DrawMapBounds()
    {
        var corners = new[]
        {
            (X: 0.0, Y: 0.0),
            (X: _world.WorldWidth, Y: 0.0),
            (X: _world.WorldWidth, Y: _world.WorldHeight),
            (X: 0.0, Y: _world.WorldHeight),
        };
        for (int index = 0; index < corners.Length; index++)
        {
            var a = corners[index];
            var b = corners[(index + 1) % corners.Length];
            DrawProjectedSegment(a.X, a.Y, b.X, b.Y, 0.02, 2.2f, Rgba.FromBytes(210, 220, 230, 120));
        }
    }

    private void DrawFacilities()
    {
        foreach (FacilityRegion facility in _mapPreset.Facilities)
        {
            Rgba color = facility.Type.ToLowerInvariant() switch
            {
                "base" => TeamColor(facility.Team, 90),
                "outpost" => TeamColor(facility.Team, 100),
                "energy_mechanism" => Rgba.FromBytes(240, 210, 80, 90),
                "buff_central_highland" => Rgba.FromBytes(80, 210, 150, 80),
                _ => Rgba.FromBytes(110, 132, 150, 52),
            };

            IReadOnlyList<(double X, double Y)> points = FacilityPoints(facility);
            if (points.Count < 2)
            {
                continue;
            }

            for (int i = 0; i < points.Count; i++)
            {
                (double X, double Y) a = points[i];
                (double X, double Y) b = points[(i + 1) % points.Count];
                DrawProjectedSegment(a.X, a.Y, b.X, b.Y, Math.Max(0.02, facility.HeightM), 1.5f, color);
            }
        }
    }

    private void DrawEntities()
    {
        foreach (SimulationEntity entity in _world.Entities
                     .Where(entity => !ReferenceEquals(entity, _player)
                         && (!_renderedTerrain3DThisFrame || IsOperatorControllable(entity)))
                     .OrderByDescending(DistanceMetersToPlayer))
        {
            if (!TryProjectEntity(entity, out ScreenPoint bottom, out ScreenPoint top, out float widthPx))
            {
                continue;
            }

            Rgba color = EntityColor(entity);
            if (_renderedTerrain3DThisFrame)
            {
                DrawEntityMarker(entity, top, widthPx, color);
                continue;
            }

            float left = bottom.X - widthPx * 0.5f;
            float rectHeight = Math.Max(6f, bottom.Y - top.Y);
            _batch.FillRect(left, top.Y, widthPx, rectHeight, color.WithAlpha(180));
            _batch.StrokeRect(left, top.Y, widthPx, rectHeight, 1.2f, color.WithAlpha(240));
            if (string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
            {
                DrawProjectedHeading(entity, top, widthPx, color);
            }
        }
    }

    private void DrawEntityMarker(SimulationEntity entity, ScreenPoint top, float widthPx, Rgba color)
    {
        float radius = Math.Clamp(widthPx * 0.18f, 4f, 11f);
        float y = top.Y - radius - 5f;
        _batch.Ring(top.X, y, radius, radius + 1.5f, color.WithAlpha(220), 24);
        _batch.Line(top.X - radius * 1.35f, y, top.X + radius * 1.35f, y, 1.2f, color.WithAlpha(160));
        if (string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            DrawProjectedHeading(entity, top, Math.Max(widthPx * 0.55f, 12f), color.WithAlpha(185));
        }
    }

    private void DrawProjectedHeading(SimulationEntity entity, ScreenPoint top, float widthPx, Rgba color)
    {
        double yawRad = entity.TurretYawDeg * Math.PI / 180.0;
        double lengthWorld = 1.0 / Math.Max(_world.MetersPerWorldUnit, 1e-6);
        double tx = entity.X + Math.Cos(yawRad) * lengthWorld;
        double ty = entity.Y + Math.Sin(yawRad) * lengthWorld;
        if (TryProjectWorld(tx, ty, entity.GroundHeightM + entity.BodyHeightM * 1.10, out ScreenPoint tip))
        {
            _batch.Line(top.X, top.Y + Math.Max(4f, widthPx * 0.22f), tip.X, tip.Y, 2.0f, color.WithAlpha(230));
        }
    }

    private void DrawProjectiles()
    {
        foreach (SimulationProjectile projectile in _world.Projectiles)
        {
            if (TryProjectWorld(projectile.X, projectile.Y, projectile.HeightM, out ScreenPoint point))
            {
                Rgba color = string.Equals(projectile.Team, "red", StringComparison.OrdinalIgnoreCase)
                    ? Rgba.FromBytes(255, 210, 80, 230)
                    : Rgba.FromBytes(90, 190, 255, 230);
                _batch.FillCircle(point.X, point.Y, 3.2f, color, 12);
            }
        }
    }

    private void DrawEnergyMechanisms()
    {
        foreach (SimulationEntity mechanism in _world.Entities.Where(entity =>
                     string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)))
        {
            if (!_renderedTerrain3DThisFrame
                && TryProjectEntity(mechanism, out ScreenPoint bottom, out ScreenPoint top, out float widthPx))
            {
                _batch.FillRect(bottom.X - widthPx * 0.5f, top.Y, widthPx, bottom.Y - top.Y, Rgba.FromBytes(48, 48, 52, 170));
                _batch.StrokeRect(bottom.X - widthPx * 0.5f, top.Y, widthPx, bottom.Y - top.Y, 1.4f, Rgba.FromBytes(235, 215, 92, 180));
            }

            foreach (SimulationTeamState teamState in _world.Teams.Values)
            {
                DrawEnergyTargetsForTeam(mechanism, teamState);
            }
        }
    }

    private void DrawEnergyTargetsForTeam(SimulationEntity mechanism, SimulationTeamState teamState)
    {
        IReadOnlyList<ArmorPlateTarget> targets = SimulationCombatMath.GetEnergyMechanismTargets(
            mechanism,
            _world.MetersPerWorldUnit,
            EnergyVisualGameTimeSec,
            teamState.Team,
            teamState);
        var armGroups = targets
                     .Where(target => SimulationCombatMath.TryParseEnergyArmIndex(target.Id, out _, out _))
                     .GroupBy(target =>
                     {
                         SimulationCombatMath.TryParseEnergyArmIndex(target.Id, out _, out int armIndex);
                         return armIndex;
                     })
                     .OrderBy(group => group.Key)
                     .ToArray();
        DrawEnergyRotorOverlay(teamState, armGroups);

        foreach (IGrouping<int, ArmorPlateTarget> armGroup in armGroups)
        {
            int armIndex = armGroup.Key;
            EnergyMechanismArmDisplayState state = EnergyMechanismVisualLogic.ResolveArmState(
                teamState,
                armIndex,
                EnergyVisualGameTimeSec);
            if (state.Kind == EnergyMechanismArmDisplayKind.Off)
            {
                continue;
            }

            ArmorPlateTarget disk = armGroup
                .OrderBy(target => Math.Abs((target.EnergyRingScore <= 0 ? 10 : target.EnergyRingScore) - 10))
                .First();
            if (!TryProjectWorld(disk.X, disk.Y, disk.HeightM, out ScreenPoint center))
            {
                continue;
            }

            float radius = Math.Clamp((float)(Math.Max(disk.WidthM, disk.HeightSpanM) * 22.0 / Math.Max(0.8, center.DepthM)), 5f, 42f);
            Rgba color = TeamColor(teamState.Team, state.Flashing ? 250 : 215);
            if (state.Pending)
            {
                DrawEnergyPendingDiskPattern(center, radius, ResolvePureTeamLightColor(teamState.Team));
            }
            else
            {
                DrawEnergyHitRing(center, radius, state.RingScore, color);
            }
        }
    }

    private void DrawEnergyPendingDiskPattern(ScreenPoint center, float radius, Rgba color)
    {
        float outer = Math.Clamp(radius * 1.10f, 7f, 62f);
        foreach (int ringScore in new[] { 4, 7 })
        {
            float outerRatio = (11 - ringScore) / 10f;
            float innerRatio = (10 - ringScore) / 10f;
            _batch.Ring(
                center.X,
                center.Y,
                outer * innerRatio,
                outer * outerRatio,
                color.WithAlpha(232),
                64);
        }
    }

    private void DrawEnergyHitRing(ScreenPoint center, float radius, int ringScore, Rgba color)
    {
        int score = Math.Clamp(ringScore <= 0 ? 10 : ringScore, 1, 10);
        float normalized = score / 10f;
        float outer = Math.Clamp(radius * (0.28f + normalized * 0.72f), 5f, radius * 1.05f);
        float inner = Math.Max(0f, outer - Math.Clamp(radius * 0.12f, 2.4f, 8.5f));
        _batch.Ring(center.X, center.Y, inner, outer, color, 56);
        _batch.Ring(center.X, center.Y, Math.Max(0f, inner - 3.0f), Math.Max(inner - 1.4f, 1.0f), color.WithAlpha(110), 56);
    }

    private void DrawEnergyRotorOverlay(
        SimulationTeamState teamState,
        IReadOnlyList<IGrouping<int, ArmorPlateTarget>> armGroups)
    {
        if (armGroups.Count < 3)
        {
            return;
        }

        var disks = new List<(int Arm, ArmorPlateTarget Disk)>(armGroups.Count);
        foreach (IGrouping<int, ArmorPlateTarget> armGroup in armGroups)
        {
            ArmorPlateTarget disk = armGroup
                .OrderBy(target => Math.Abs((target.EnergyRingScore <= 0 ? 10 : target.EnergyRingScore) - 10))
                .First();
            disks.Add((armGroup.Key, disk));
        }

        double centerX = disks.Average(item => item.Disk.X);
        double centerY = disks.Average(item => item.Disk.Y);
        double centerHeightM = disks.Average(item => item.Disk.HeightM);
        if (!TryProjectWorld(centerX, centerY, centerHeightM, out ScreenPoint center))
        {
            return;
        }

        bool active = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            || string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase);
        float progressRatio = ResolveEnergyActivationRatio(teamState);
        Rgba teamColor = TeamColor(teamState.Team, active ? 185 : 92);
        _batch.FillCircle(center.X, center.Y, active ? 4.8f : 3.4f, teamColor.WithAlpha(active ? 210 : 128), 18);

        foreach ((int arm, ArmorPlateTarget disk) in disks)
        {
            if (!TryProjectWorld(disk.X, disk.Y, disk.HeightM, out ScreenPoint point))
            {
                continue;
            }

            EnergyMechanismArmDisplayState state = EnergyMechanismVisualLogic.ResolveArmState(
                teamState,
                arm,
                EnergyVisualGameTimeSec);
            Rgba darkArm = Rgba.FromBytes(9, 10, 13, active ? 190 : 112);
            Rgba litArm = ResolvePureTeamLightColor(teamState.Team).WithAlpha(state.Flashing ? 255 : 226);
            bool pending = state.Kind == EnergyMechanismArmDisplayKind.Pending;
            bool lit = state.Kind is EnergyMechanismArmDisplayKind.Hit
                or EnergyMechanismArmDisplayKind.ActivatedByProgress
                or EnergyMechanismArmDisplayKind.Completed;
            Rgba baseArmColor = lit ? litArm : darkArm;
            _batch.Line(center.X, center.Y, point.X, point.Y, lit ? 4.2f : 2.2f, baseArmColor);
            if (teamState.EnergyLargeMechanismActive && progressRatio > 1e-4f && !pending && !lit)
            {
                DrawEnergyArmProgress(center, point, progressRatio, litArm);
            }
            else if (pending)
            {
                DrawEnergyPendingEndMark(point, litArm);
            }

            float diskRadius = Math.Clamp(
                (float)(Math.Max(disk.WidthM, disk.HeightSpanM) * 32.0 / Math.Max(0.8, point.DepthM)),
                6f,
                56f);
            _batch.Ring(point.X, point.Y, diskRadius * 0.82f, diskRadius, (lit ? litArm : teamColor).WithAlpha(lit ? 210 : 92), 54);
        }
    }

    private void DrawEnergyPendingEndMark(ScreenPoint point, Rgba color)
    {
        float size = Math.Clamp(26f / (float)Math.Max(0.8, point.DepthM), 7f, 18f);
        _batch.Ring(point.X, point.Y, size * 0.58f, size * 0.92f, color.WithAlpha(226), 32);
        _batch.Ring(point.X, point.Y, size * 0.18f, size * 0.32f, color.WithAlpha(210), 24);
    }

    private void DrawEnergyArmProgress(ScreenPoint center, ScreenPoint end, float ratio, Rgba color)
    {
        float clamped = Math.Clamp(ratio, 0f, 1f);
        float px = center.X + (end.X - center.X) * clamped;
        float py = center.Y + (end.Y - center.Y) * clamped;
        _batch.Line(center.X, center.Y, px, py, 4.0f, color.WithAlpha(218));
    }

    private static Rgba ResolvePureTeamLightColor(string team)
        => string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase)
            ? Rgba.FromBytes(0, 0, 255, 255)
            : Rgba.FromBytes(255, 0, 0, 255);

    private static float ResolveEnergyActivationRatio(SimulationTeamState teamState)
    {
        if (string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase))
        {
            return 1.0f;
        }

        int activatedCount = teamState.EnergyActivatedGroupCount > 0
            ? Math.Clamp(teamState.EnergyActivatedGroupCount, 0, 5)
            : teamState.EnergyHitRingsByArm.Count(score => score > 0);
        return activatedCount switch
        {
            <= 0 => 0.0f,
            1 => 0.2f,
            2 => 0.4f,
            3 => 0.6f,
            4 => 0.8f,
            _ => 1.0f,
        };
    }

    private void DrawCrosshair()
    {
        float x = ClientSize.X * 0.5f;
        float y = ClientSize.Y * 0.5f;
        Rgba color = _player.AutoAimRequested ? Rgba.FromBytes(255, 216, 72, 220) : Rgba.FromBytes(236, 244, 248, 190);
        _batch.Line(x - 18, y, x - 6, y, 1.6f, color);
        _batch.Line(x + 6, y, x + 18, y, 1.6f, color);
        _batch.Line(x, y - 18, x, y - 6, 1.6f, color);
        _batch.Line(x, y + 6, x, y + 18, 1.6f, color);
        _batch.Ring(x, y, 24, 25.6f, color.WithAlpha(80), 64);
    }

    private void DrawHud()
    {
        DrawBars();
        DrawMiniMap();
        DrawEnergyHudDots();
    }

    private void DrawOperatorStartupOverlay()
    {
        if (IsMatchLive)
        {
            return;
        }

        float w = ClientSize.X;
        float h = ClientSize.Y;
        if (_matchPhase == LinuxMatchPhase.Login)
        {
            DrawOperatorLoginRoomOverlay(w, h);
            return;
        }

        if (_operatorFirstPersonConfirmed)
        {
            DrawStartupTopBand(w);
            DrawStartupFirstPersonBanner(w, h);
            return;
        }

        _batch.FillRect(0, 0, w, h, Rgba.FromBytes(2, 5, 8, 104));
        DrawStartupTopBand(w);

        float panelW = Math.Clamp(w * 0.68f, 620f, 980f);
        float panelH = Math.Clamp(h * 0.42f, 300f, 410f);
        float x = (w - panelW) * 0.5f;
        float y = Math.Clamp(h * 0.13f, 58f, 132f);
        Rgba team = TeamColor(_player.Team, 220);
        _batch.FillRect(x, y, panelW, panelH, Rgba.FromBytes(6, 10, 15, 218));
        _batch.StrokeRect(x, y, panelW, panelH, 1.5f, team.WithAlpha(190));
        _batch.FillRect(x + 18, y + 20, 8, panelH - 40, team.WithAlpha(230));

        DrawSevenSegmentText(x + 46, y + 24, "LOCAL ROOM", 1.65f, Rgba.FromBytes(232, 240, 244, 230));
        DrawSevenSegmentText(x + 46, y + 62, "OPERATOR LOGIN", 2.15f, team);
        DrawSevenSegmentText(x + 46, y + 106, _player.Id.ToUpperInvariant(), 1.32f, Rgba.FromBytes(226, 236, 242, 225));

        float cardY = y + 150;
        float cardGap = 14;
        float cardW = (panelW - 64 - cardGap * 2) / 3f;
        DrawStartupInfoCard(x + 46, cardY, cardW, 102, "SEAT", ResolveOperatorSeatLabel(), team, true);
        DrawStartupInfoCard(x + 46 + (cardW + cardGap), cardY, cardW, 102, "TEAM", _player.Team.ToUpperInvariant(), team, true);
        DrawStartupInfoCard(x + 46 + (cardW + cardGap) * 2, cardY, cardW, 102, "SPAWN", $"P{_operatorSpawnPointIndex + 1}", team, true);

        float railY = cardY + 130;
        DrawStartupStepRail(x + 48, railY, panelW - 96, team);

        string action = "ENTER CONFIRM";
        DrawSevenSegmentText(x + 52, y + panelH - 58, action, 1.28f, Rgba.FromBytes(246, 248, 210, 230));
        DrawSevenSegmentText(x + panelW - 186, y + panelH - 56, "M LOOK ALT FREE", 0.88f, Rgba.FromBytes(176, 194, 202, 178));
    }

    private void DrawOperatorLoginRoomOverlay(float w, float h)
    {
        _batch.FillRect(0, 0, w, h, Rgba.FromBytes(2, 5, 8, 112));
        DrawStartupTopBand(w);
        float panelW = Math.Clamp(w * 0.74f, 760f, 1120f);
        float panelH = Math.Clamp(h * 0.58f, 430f, 590f);
        float x = (w - panelW) * 0.5f;
        float y = Math.Clamp(h * 0.10f, 54f, 110f);
        Rgba team = TeamColor(_operatorTeam, 220);
        _batch.FillRect(x, y, panelW, panelH, Rgba.FromBytes(5, 9, 14, 226));
        _batch.StrokeRect(x, y, panelW, panelH, 1.5f, team.WithAlpha(190));
        _batch.FillRect(x + 20, y + 20, 8, panelH - 40, team.WithAlpha(225));

        DrawSevenSegmentText(x + 44, y + 26, "LOCAL ROOM", 1.65f, Rgba.FromBytes(232, 240, 244, 230));
        DrawSevenSegmentText(x + 44, y + 64, "OPERATOR SEAT LOGIN", 1.72f, team);
        DrawSevenSegmentText(x + panelW - 252, y + 34, "ENTER START", 1.10f, Rgba.FromBytes(246, 248, 210, 226));

        DrawLoginTeamButton(x + 44, y + 100, 126, 38, "RED Q", string.Equals(_operatorTeam, "red", StringComparison.OrdinalIgnoreCase));
        DrawLoginTeamButton(x + 182, y + 100, 126, 38, "BLUE E", string.Equals(_operatorTeam, "blue", StringComparison.OrdinalIgnoreCase));
        DrawSevenSegmentText(x + 326, y + 111, "CLICK SEAT OR PRESS 1 2 3 4 7", 0.82f, Rgba.FromBytes(176, 194, 202, 178));

        float gridY = y + 170;
        float seatGap = 16;
        float seatW = (panelW - 88 - seatGap * (OperatorSeatSpecs.Length - 1)) / OperatorSeatSpecs.Length;
        for (int index = 0; index < OperatorSeatSpecs.Length; index++)
        {
            float sx = x + 44 + index * (seatW + seatGap);
            DrawLoginSeatCard(sx, gridY, seatW, 132, OperatorSeatSpecs[index]);
        }

        float spawnY = gridY + 164;
        DrawSevenSegmentText(x + 44, spawnY - 28, "SPAWN POINT", 0.94f, Rgba.FromBytes(190, 206, 216, 190));
        for (int index = 0; index < OperatorSeatSpecs.Length; index++)
        {
            float sx = x + 44 + index * 72;
            bool active = _operatorSpawnPointIndex == index;
            Rgba color = active ? team : Rgba.FromBytes(98, 112, 124, 128);
            _batch.FillRect(sx, spawnY, 56, 38, active ? team.WithAlpha(54) : Rgba.FromBytes(12, 18, 25, 150));
            _batch.StrokeRect(sx, spawnY, 56, 38, 1.2f, color.WithAlpha(active ? 220 : 125));
            DrawSevenSegmentText(sx + 14, spawnY + 12, $"P{index + 1}", 0.78f, active ? team : Rgba.FromBytes(170, 184, 194, 176));
        }

        DrawSevenSegmentText(x + 44, y + panelH - 54, $"{_operatorTeam.ToUpperInvariant()} {ResolveOperatorSeatLabel().ToUpperInvariant()} P{_operatorSpawnPointIndex + 1}", 1.08f, team);
        DrawSevenSegmentText(x + panelW - 264, y + panelH - 54, "ARROWS CHANGE SPAWN", 0.78f, Rgba.FromBytes(176, 194, 202, 176));
    }

    private void DrawLoginTeamButton(float x, float y, float w, float h, string label, bool active)
    {
        Rgba color = label.StartsWith("BLUE", StringComparison.OrdinalIgnoreCase)
            ? TeamColor("blue", 220)
            : TeamColor("red", 220);
        _batch.FillRect(x, y, w, h, active ? color.WithAlpha(58) : Rgba.FromBytes(12, 18, 25, 150));
        _batch.StrokeRect(x, y, w, h, 1.2f, active ? color : Rgba.FromBytes(128, 142, 154, 120));
        DrawSevenSegmentText(x + 14, y + 12, label, 0.80f, active ? color : Rgba.FromBytes(184, 198, 208, 180));
    }

    private void DrawLoginSeatCard(float x, float y, float w, float h, OperatorSeatSpec seat)
    {
        bool active = string.Equals(_operatorEntityKey, seat.EntityKey, StringComparison.OrdinalIgnoreCase);
        Rgba team = TeamColor(_operatorTeam, active ? 232 : 150);
        _batch.FillRect(x, y, w, h, active ? team.WithAlpha(44) : Rgba.FromBytes(12, 18, 25, 162));
        _batch.StrokeRect(x, y, w, h, active ? 1.8f : 1.0f, active ? team : Rgba.FromBytes(120, 136, 148, 102));
        _batch.FillRect(x + 10, y + 10, 5, h - 20, team.WithAlpha(active ? 220 : 120));
        DrawSevenSegmentText(x + 24, y + 18, seat.Label, 0.94f, active ? team : Rgba.FromBytes(220, 228, 234, 190));
        DrawSevenSegmentText(x + 24, y + 52, $"KEY {FormatSeatKey(seat.Key)}", 0.72f, Rgba.FromBytes(164, 182, 194, 170));
        DrawSevenSegmentText(x + 24, y + 84, active ? "PLAYER" : "EMPTY", 0.82f, active ? Rgba.FromBytes(246, 248, 210, 226) : Rgba.FromBytes(142, 154, 164, 146));
    }

    private static string FormatSeatKey(TkKeys key)
        => key switch
        {
            TkKeys.D1 => "1",
            TkKeys.D2 => "2",
            TkKeys.D3 => "3",
            TkKeys.D4 => "4",
            TkKeys.D7 => "7",
            _ => key.ToString(),
        };

    private void DrawStartupFirstPersonBanner(float w, float h)
    {
        Rgba team = TeamColor(_player.Team, 220);
        double duration = _matchPhase switch
        {
            LinuxMatchPhase.Preparation => LocalPreparationSec,
            LinuxMatchPhase.SelfCheck => LocalSelfCheckSec,
            LinuxMatchPhase.Countdown => LocalCountdownSec,
            _ => 0.0,
        };
        double remaining = Math.Max(0.0, duration - _matchPhaseElapsedSec);
        float panelW = Math.Clamp(w * 0.34f, 360f, 560f);
        float x = (w - panelW) * 0.5f;
        float y = 64f;
        _batch.FillRect(x, y, panelW, 72f, Rgba.FromBytes(4, 8, 13, 186));
        _batch.StrokeRect(x, y, panelW, 72f, 1.2f, team.WithAlpha(172));
        DrawSevenSegmentText(x + 18, y + 14, StartupPhaseLabel(), 1.08f, team);
        DrawSevenSegmentText(x + panelW - 142, y + 14, $"{Math.Ceiling(remaining):00}", 1.08f, Rgba.FromBytes(246, 248, 210, 230));
        string actionLabel = _matchPhase == LinuxMatchPhase.Countdown ? "MATCH START" : "ENTER SKIP";
        DrawSevenSegmentText(x + 18, y + 43, actionLabel, 0.86f, Rgba.FromBytes(226, 236, 242, 188));
        DrawSevenSegmentText(x + panelW - 160, y + 43, "M LOOK ALT FREE", 0.74f, Rgba.FromBytes(176, 194, 202, 170));

        if (_matchPhase == LinuxMatchPhase.Countdown)
        {
            DrawSevenSegmentText(w * 0.5f - 34f, h * 0.42f, $"{Math.Ceiling(remaining):0}", 3.6f, team.WithAlpha(235));
        }
    }

    private void DrawStartupTopBand(float w)
    {
        _batch.FillRect(0, 0, w, 48, Rgba.FromBytes(4, 8, 13, 210));
        Rgba team = TeamColor(_player.Team, 210);
        _batch.FillRect(0, 47, w, 1.5f, team.WithAlpha(160));
        DrawSevenSegmentText(26, 14, "RMUC2026", 1.02f, Rgba.FromBytes(232, 240, 244, 210));
        DrawSevenSegmentText(w - 270, 14, StartupPhaseLabel(), 1.02f, team);
    }

    private string StartupPhaseLabel()
        => _matchPhase switch
        {
            LinuxMatchPhase.Preparation => "PREPARATION",
            LinuxMatchPhase.SelfCheck => "SELF CHECK",
            LinuxMatchPhase.Countdown => "COUNTDOWN",
            _ => "LOGIN",
        };

    private string ResolveOperatorSeatLabel()
        => NormalizeEntityKey(_operatorEntityKey) switch
        {
            "robot_1" => "HERO",
            "robot_2" => "ENGINEER",
            "robot_3" => "INFANTRY1",
            "robot_4" => "INFANTRY2",
            "robot_7" => "SENTRY",
            _ => "INFANTRY",
        };

    private void DrawLocalRefereePanelOverlay()
    {
        if (!_localRefereePanelOpen)
        {
            return;
        }

        float w = ClientSize.X;
        float h = ClientSize.Y;
        float panelW = Math.Clamp(w * 0.30f, 360f, 500f);
        float panelH = Math.Clamp(h * 0.44f, 330f, 460f);
        float x = w - panelW - 28f;
        float y = Math.Clamp(h * 0.12f, 62f, 112f);
        Rgba accent = TeamColor(_player.Team, 220);
        _batch.FillRect(x, y, panelW, panelH, Rgba.FromBytes(4, 8, 13, 224));
        _batch.StrokeRect(x, y, panelW, panelH, 1.4f, accent.WithAlpha(180));
        _batch.FillRect(x + 16, y + 18, 6, panelH - 36, accent.WithAlpha(210));
        DrawSevenSegmentText(x + 36, y + 22, "LOCAL O PANEL", 1.02f, Rgba.FromBytes(232, 240, 244, 224));
        DrawSevenSegmentText(x + 36, y + 54, "ENERGY", 1.42f, accent);
        DrawSevenSegmentText(x + panelW - 146, y + 30, "O P CLOSE", 0.70f, Rgba.FromBytes(176, 194, 202, 176));

        float rowY = y + 106f;
        foreach (SimulationTeamState state in _world.Teams.Values.OrderBy(team => team.Team))
        {
            Rgba team = ResolvePureTeamLightColor(state.Team).WithAlpha(230);
            string teamLabel = string.Equals(state.Team, "blue", StringComparison.OrdinalIgnoreCase) ? "BLUE" : "RED";
            string mode = state.EnergyLargeMechanismActive ? "LARGE" : "SMALL";
            string phase = ResolveEnergyPanelStateLabel(state);
            int litMask = state.EnergyCurrentLitMask;
            int hitCount = state.EnergyHitRingsByArm.Count(score => score > 0);
            _batch.FillRect(x + 36, rowY, panelW - 72, 78, Rgba.FromBytes(10, 15, 22, 174));
            _batch.StrokeRect(x + 36, rowY, panelW - 72, 78, 1.0f, team.WithAlpha(150));
            DrawSevenSegmentText(x + 52, rowY + 14, $"{teamLabel} {mode}", 0.86f, team);
            DrawSevenSegmentText(x + 52, rowY + 42, $"{phase} H{hitCount} M{litMask:X2}", 0.74f, Rgba.FromBytes(214, 226, 234, 204));
            for (int arm = 0; arm < EnergyMechanismVisualLogic.ArmCount; arm++)
            {
                EnergyMechanismArmDisplayState armState = EnergyMechanismVisualLogic.ResolveArmState(state, arm, EnergyVisualGameTimeSec);
                float cx = x + panelW - 136 + arm * 20;
                float cy = rowY + 27;
                if (armState.Pending)
                {
                    _batch.Ring(cx, cy, 5.0f, 7.2f, team, 18);
                }
                else if (armState.Kind != EnergyMechanismArmDisplayKind.Off)
                {
                    _batch.FillCircle(cx, cy, 6.2f, team, 18);
                }
                else
                {
                    _batch.Ring(cx, cy, 4.8f, 5.8f, Rgba.FromBytes(92, 100, 108, 120), 16);
                }
            }

            rowY += 96f;
        }

        DrawSevenSegmentText(x + 36, y + panelH - 74, "F START ENERGY TEST", 0.70f, Rgba.FromBytes(246, 248, 210, 210));
        DrawSevenSegmentText(x + 36, y + panelH - 46, "PANEL PAUSES DRIVE INPUT", 0.66f, Rgba.FromBytes(176, 194, 202, 166));
    }

    private static string ResolveEnergyPanelStateLabel(SimulationTeamState state)
    {
        if (string.Equals(state.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase))
        {
            return "DONE";
        }

        if (string.Equals(state.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase))
        {
            return state.EnergyCurrentLitMask != 0 ? "WAIT" : "ACTV";
        }

        return "IDLE";
    }

    private void DrawStartupInfoCard(float x, float y, float w, float h, string label, string value, Rgba accent, bool active)
    {
        _batch.FillRect(x, y, w, h, Rgba.FromBytes(12, 18, 25, active ? 190 : 132));
        _batch.StrokeRect(x, y, w, h, 1.1f, active ? accent.WithAlpha(150) : Rgba.FromBytes(130, 144, 154, 90));
        DrawSevenSegmentText(x + 16, y + 18, label, 0.95f, Rgba.FromBytes(164, 182, 194, 190));
        DrawSevenSegmentText(x + 16, y + 54, value, 1.18f, active ? accent : Rgba.FromBytes(196, 206, 212, 180));
    }

    private void DrawStartupStepRail(float x, float y, float width, Rgba accent)
    {
        string[] labels = ["ROOM", "SEAT", "FIRST VIEW", "MATCH"];
        bool[] ready =
        [
            true,
            true,
            _matchPhase != LinuxMatchPhase.Login,
            IsMatchLive,
        ];
        float gap = 12;
        float itemW = (width - gap * (labels.Length - 1)) / labels.Length;
        for (int index = 0; index < labels.Length; index++)
        {
            float ix = x + index * (itemW + gap);
            Rgba color = ready[index] ? accent : Rgba.FromBytes(86, 98, 108, 150);
            _batch.FillRect(ix, y, itemW, 50, Rgba.FromBytes(12, 18, 24, 142));
            _batch.StrokeRect(ix, y, itemW, 50, 1.0f, color.WithAlpha(140));
            _batch.FillCircle(ix + 22, y + 25, 10, color, 24);
            DrawSevenSegmentText(ix + 44, y + 17, labels[index], 0.95f, ready[index] ? Rgba.FromBytes(228, 236, 242, 220) : Rgba.FromBytes(142, 154, 164, 170));
        }
    }

    private void DrawSevenSegmentText(float x, float y, string text, float scale, Rgba color)
    {
        float cursor = x;
        foreach (char raw in text)
        {
            char ch = char.ToUpperInvariant(raw);
            if (ch == ' ')
            {
                cursor += 8.0f * scale;
                continue;
            }

            if (ch == '_')
            {
                _batch.FillRect(cursor + 1.0f * scale, y + 12.0f * scale, 8.0f * scale, 1.6f * scale, color);
                cursor += 12.0f * scale;
                continue;
            }

            DrawSevenSegmentGlyph(cursor, y, scale, color, ResolveSevenSegmentMask(ch));
            cursor += 12.0f * scale;
        }
    }

    private void DrawSevenSegmentGlyph(float x, float y, float scale, Rgba color, byte mask)
    {
        float t = Math.Max(1.2f, 1.7f * scale);
        float w = 8.0f * scale;
        float h = 14.0f * scale;
        if ((mask & 0b0000001) != 0) _batch.FillRect(x + t, y, w, t, color);
        if ((mask & 0b0000010) != 0) _batch.FillRect(x + w + t, y + t, t, h * 0.5f - t, color);
        if ((mask & 0b0000100) != 0) _batch.FillRect(x + w + t, y + h * 0.5f + t * 0.5f, t, h * 0.5f - t, color);
        if ((mask & 0b0001000) != 0) _batch.FillRect(x + t, y + h, w, t, color);
        if ((mask & 0b0010000) != 0) _batch.FillRect(x, y + h * 0.5f + t * 0.5f, t, h * 0.5f - t, color);
        if ((mask & 0b0100000) != 0) _batch.FillRect(x, y + t, t, h * 0.5f - t, color);
        if ((mask & 0b1000000) != 0) _batch.FillRect(x + t, y + h * 0.5f, w, t, color);
    }

    private static byte ResolveSevenSegmentMask(char ch)
        => ch switch
        {
            '0' or 'O' => 0b0111111,
            '1' or 'I' => 0b0000110,
            '2' or 'Z' => 0b1011011,
            '3' => 0b1001111,
            '4' => 0b1100110,
            '5' or 'S' => 0b1101101,
            '6' or 'G' => 0b1111101,
            '7' => 0b0000111,
            '8' or 'B' => 0b1111111,
            '9' => 0b1101111,
            'A' or 'R' => 0b1110111,
            'C' => 0b0111001,
            'D' => 0b1011110,
            'E' => 0b1111001,
            'F' => 0b1110001,
            'H' => 0b1110110,
            'J' => 0b0011110,
            'L' => 0b0111000,
            'M' or 'N' => 0b0110111,
            'P' => 0b1110011,
            'T' => 0b1111000,
            'U' or 'V' or 'W' => 0b0111110,
            'Y' => 0b1101110,
            '-' => 0b1000000,
            _ => 0b1000000,
        };

    private void DrawBars()
    {
        float x = 28;
        float y = ClientSize.Y - 84;
        float width = 300;
        DrawBar(x, y, width, 14, Ratio(_player.Health, _player.MaxHealth), TeamColor(_player.Team, 230));
        DrawBar(x, y + 24, width, 10, Ratio(_player.Heat, Math.Max(1.0, _player.MaxHeat)), Rgba.FromBytes(255, 154, 72, 230));
        double ammo = string.Equals(_player.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? _player.Ammo42Mm : _player.Ammo17Mm;
        DrawBar(x, y + 42, width, 10, _player.UnlimitedAmmo ? 1.0 : Math.Clamp(ammo / 120.0, 0.0, 1.0), Rgba.FromBytes(230, 214, 92, 230));
        _batch.FillRect(x - 8, y - 12, width + 16, 72, Rgba.FromBytes(8, 10, 12, 74));
    }

    private void DrawBar(float x, float y, float width, float height, double ratio, Rgba color)
    {
        _batch.FillRect(x, y, width, height, Rgba.FromBytes(34, 42, 48, 190));
        _batch.FillRect(x, y, width * (float)Math.Clamp(ratio, 0.0, 1.0), height, color);
        _batch.StrokeRect(x, y, width, height, 1.0f, Rgba.FromBytes(216, 224, 230, 90));
    }

    private void DrawMiniMap()
    {
        float size = Math.Clamp(ClientSize.Y * 0.26f, 160f, 260f);
        float x = ClientSize.X - size - 24;
        float y = ClientSize.Y - size - 24;
        _batch.FillRect(x, y, size, size, Rgba.FromBytes(5, 8, 12, 168));
        _batch.StrokeRect(x, y, size, size, 1.3f, Rgba.FromBytes(200, 220, 230, 100));

        foreach (FacilityRegion facility in _mapPreset.Facilities)
        {
            IReadOnlyList<(double X, double Y)> points = FacilityPoints(facility);
            if (points.Count < 2)
            {
                continue;
            }

            Rgba color = facility.Type.ToLowerInvariant() switch
            {
                "base" => TeamColor(facility.Team, 95),
                "outpost" => TeamColor(facility.Team, 105),
                "energy_mechanism" => Rgba.FromBytes(238, 214, 76, 110),
                _ => Rgba.FromBytes(128, 144, 154, 38),
            };

            List<(float X, float Y)> projected = points
                .Select(point => MiniMapPoint(point.X, point.Y, x, y, size))
                .ToList();
            _batch.Polygon(projected, color);
        }

        foreach (SimulationEntity entity in _world.Entities.Where(entity => IsOperatorControllable(entity)))
        {
            (float mx, float my) = MiniMapPoint(entity.X, entity.Y, x, y, size);
            float radius = ReferenceEquals(entity, _player) ? 5f : 3.5f;
            _batch.FillCircle(mx, my, radius, EntityColor(entity).WithAlpha(230), 16);
        }

        double yawRad = _cameraYawDeg * Math.PI / 180.0;
        (float px, float py) = MiniMapPoint(_player.X, _player.Y, x, y, size);
        _batch.Line(px, py, px + (float)Math.Cos(yawRad) * 18f, py + (float)Math.Sin(yawRad) * 18f, 2.0f, Rgba.FromBytes(255, 255, 255, 210));
    }

    private void DrawEnergyHudDots()
    {
        float x = ClientSize.X * 0.5f - 72;
        float y = 30;
        foreach (SimulationTeamState state in _world.Teams.Values.OrderBy(team => team.Team))
        {
            Rgba color = TeamColor(state.Team, 230);
            for (int arm = 0; arm < EnergyMechanismVisualLogic.ArmCount; arm++)
            {
                EnergyMechanismArmDisplayState armState = EnergyMechanismVisualLogic.ResolveArmState(state, arm, EnergyVisualGameTimeSec);
                float cx = x + arm * 18;
                if (armState.Pending)
                {
                    _batch.Ring(cx, y, 4.5f, 6.4f, color, 18);
                }
                else if (armState.Completed)
                {
                    _batch.FillCircle(cx, y, 5.6f, color, 18);
                }
                else
                {
                    _batch.Ring(cx, y, 4.2f, 5.2f, Rgba.FromBytes(92, 100, 108, 110), 16);
                }
            }

            y += 18;
        }
    }

    private void DrawProjectedSegment(double ax, double ay, double bx, double by, double heightM, float thickness, Rgba color)
    {
        if (TryProjectWorld(ax, ay, heightM, out ScreenPoint a)
            && TryProjectWorld(bx, by, heightM, out ScreenPoint b))
        {
            _batch.Line(a.X, a.Y, b.X, b.Y, thickness, color);
        }
    }

    private bool TryProjectEntity(SimulationEntity entity, out ScreenPoint bottom, out ScreenPoint top, out float widthPx)
    {
        double height = Math.Max(0.25, entity.BodyHeightM + entity.AirborneHeightM);
        bool bottomOk = TryProjectWorld(entity.X, entity.Y, entity.GroundHeightM, out bottom);
        bool topOk = TryProjectWorld(entity.X, entity.Y, entity.GroundHeightM + height, out top);
        if (!bottomOk || !topOk)
        {
            widthPx = 0;
            return false;
        }

        widthPx = Math.Clamp((float)(Math.Max(entity.BodyWidthM, 0.30) * 360.0 / Math.Max(0.6, bottom.DepthM)), 5f, 130f);
        return true;
    }

    private bool TryProjectRelative(double rightM, double forwardM, double heightM, out ScreenPoint point)
    {
        double yawRad = _cameraYawDeg * Math.PI / 180.0;
        double metersPerWorldUnit = Math.Max(_world.MetersPerWorldUnit, 1e-6);
        double wx = _player.X + (Math.Cos(yawRad) * forwardM - Math.Sin(yawRad) * rightM) / metersPerWorldUnit;
        double wy = _player.Y + (Math.Sin(yawRad) * forwardM + Math.Cos(yawRad) * rightM) / metersPerWorldUnit;
        return TryProjectWorld(wx, wy, heightM, out point);
    }

    private bool TryProjectWorld(double worldX, double worldY, double heightM, out ScreenPoint point)
    {
        double metersPerWorldUnit = Math.Max(_world.MetersPerWorldUnit, 1e-6);
        double dxM = (worldX - _player.X) * metersPerWorldUnit;
        double dyM = (worldY - _player.Y) * metersPerWorldUnit;
        return TryProjectMetricOffset(dxM, dyM, heightM, out point);
    }

    private bool TryProjectScenePoint(NumericsVector3 scenePoint, out ScreenPoint point)
    {
        double metersPerWorldUnit = Math.Max(_world.MetersPerWorldUnit, 1e-6);
        double dxM = scenePoint.X - _player.X * metersPerWorldUnit;
        double dyM = scenePoint.Z - _player.Y * metersPerWorldUnit;
        return TryProjectMetricOffset(dxM, dyM, scenePoint.Y, out point);
    }

    private bool TryProjectMetricOffset(double dxM, double dyM, double heightM, out ScreenPoint point)
    {
        double yawRad = _cameraYawDeg * Math.PI / 180.0;
        double forwardX = Math.Cos(yawRad);
        double forwardY = Math.Sin(yawRad);
        double rightX = -Math.Sin(yawRad);
        double rightY = Math.Cos(yawRad);
        double z = dxM * forwardX + dyM * forwardY;
        double x = dxM * rightX + dyM * rightY;
        if (z <= 0.35)
        {
            point = default;
            return false;
        }

        double cameraHeightM = 1.20 + _player.GroundHeightM + _player.AirborneHeightM;
        double y = heightM - cameraHeightM;
        double focal = ClientSize.X * 0.62;
        double pitchOffset = Math.Tan(_cameraPitchDeg * Math.PI / 180.0) * focal;
        float sx = (float)(ClientSize.X * 0.5 + x / z * focal);
        float sy = (float)(ClientSize.Y * 0.5 - y / z * focal + pitchOffset);
        if (sx < -ClientSize.X || sx > ClientSize.X * 2 || sy < -ClientSize.Y || sy > ClientSize.Y * 2)
        {
            point = default;
            return false;
        }

        point = new ScreenPoint(sx, sy, z);
        return true;
    }

    private static IReadOnlyList<(double X, double Y)> FacilityPoints(FacilityRegion facility)
    {
        if (facility.Points.Count > 1)
        {
            return facility.Points.Select(point => (point.X, point.Y)).ToArray();
        }

        return new[]
        {
            (facility.X1, facility.Y1),
            (facility.X2, facility.Y1),
            (facility.X2, facility.Y2),
            (facility.X1, facility.Y2),
        };
    }

    private (float X, float Y) MiniMapPoint(double worldX, double worldY, float x, float y, float size)
    {
        float px = x + (float)(worldX / Math.Max(1.0, _world.WorldWidth)) * size;
        float py = y + (float)(worldY / Math.Max(1.0, _world.WorldHeight)) * size;
        return (px, py);
    }

    private double DistanceMetersToPlayer(SimulationEntity entity)
    {
        double dx = (entity.X - _player.X) * _world.MetersPerWorldUnit;
        double dy = (entity.Y - _player.Y) * _world.MetersPerWorldUnit;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Ratio(double value, double max)
        => max <= 1e-6 ? 0.0 : Math.Clamp(value / max, 0.0, 1.0);

    private static double NormalizeDeg(double value)
    {
        double result = value % 360.0;
        return result < 0 ? result + 360.0 : result;
    }

    private static double NormalizeSignedDeg(double value)
    {
        double result = (value + 180.0) % 360.0;
        if (result < 0.0)
        {
            result += 360.0;
        }

        return result - 180.0;
    }

    private static Rgba EntityColor(SimulationEntity entity)
    {
        if (string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return Rgba.FromBytes(224, 208, 96, 190);
        }

        if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            return TeamColor(entity.Team, 175);
        }

        return TeamColor(entity.Team, 205);
    }

    private static Rgba TeamColor(string team, int alpha)
        => string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase)
            ? Rgba.FromBytes(72, 146, 255, alpha)
            : string.Equals(team, "red", StringComparison.OrdinalIgnoreCase)
                ? Rgba.FromBytes(255, 80, 76, alpha)
                : Rgba.FromBytes(220, 220, 220, alpha);

    private readonly record struct ScreenPoint(float X, float Y, double DepthM);

    private readonly record struct LinuxPlayerControlState(
        double MoveForward,
        double MoveRight,
        double CameraYawDeg,
        double CameraPitchDeg,
        bool Fire,
        bool AutoAim,
        bool Jump,
        bool EnergyActivation,
        bool Boost)
    {
        public static LinuxPlayerControlState Empty => new(0.0, 0.0, 0.0, 0.0, false, false, false, false, false);
    }

    private enum LinuxMatchPhase
    {
        Login,
        Preparation,
        SelfCheck,
        Countdown,
        Live,
    }

    private readonly record struct EnergyRotationSample(
        double GameTimeSec,
        int ArmIndex,
        NumericsVector3 Position,
        NumericsVector3 Center,
        NumericsVector3 PlaneNormal,
        double ExpectedYawRad,
        double ExpectedOmegaRadPerSec);

    private readonly record struct OperatorSeatSpec(string EntityKey, string Label, TkKeys Key);
}

internal sealed class TerrainSceneOverlay
{
    private const int MaxGroundTriangles = 26000;
    private const int MaxFeatureTriangles = 52000;
    private const float FieldMarginM = 2.0f;

    private TerrainSceneOverlay(IReadOnlyList<TerrainTriangle> triangles)
    {
        Triangles = triangles;
    }

    public IReadOnlyList<TerrainTriangle> Triangles { get; }

    public static TerrainSceneOverlay? TryLoad(MapPresetDefinition mapPreset, SimulationWorldState world)
    {
        string terrainCachePath = ResolveTerrainCachePath(mapPreset);
        if (string.IsNullOrWhiteSpace(terrainCachePath) || !File.Exists(terrainCachePath))
        {
            return null;
        }

        var groundTriangles = new List<TerrainTriangle>(MaxGroundTriangles);
        var featureTriangles = new List<TerrainTriangle>(MaxFeatureTriangles);
        var groundRandom = new Random(20260510);
        var featureRandom = new Random(20260511);
        int groundSeen = 0;
        int featureSeen = 0;
        int candidateTriangles = 0;
        int suppressedTriangles = 0;
        HashSet<int> suppressedComponentIds = TerrainComponentSuppression.LoadEnergyMechanismComponentIds(mapPreset);

        try
        {
            var reader = new TerrainCacheMeshReader();
            reader.Load(
                terrainCachePath,
                (_, _, vertices, indices, componentRanges) =>
                {
                    IReadOnlyList<TerrainIndexRange> suppressedRanges = TerrainComponentSuppression.BuildSuppressedIndexRanges(
                        componentRanges,
                        suppressedComponentIds);
                    int triangleIndexCount = indices.Length - indices.Length % 3;
                    for (int index = 0; index < triangleIndexCount; index += 3)
                    {
                        if (TerrainComponentSuppression.IsSuppressedTriangleIndex(index, suppressedRanges))
                        {
                            suppressedTriangles++;
                            continue;
                        }

                        int i0 = indices[index];
                        int i1 = indices[index + 1];
                        int i2 = indices[index + 2];
                        if ((uint)i0 >= (uint)vertices.Length
                            || (uint)i1 >= (uint)vertices.Length
                            || (uint)i2 >= (uint)vertices.Length)
                        {
                            continue;
                        }

                        TerrainCacheVertex v0 = vertices[i0];
                        TerrainCacheVertex v1 = vertices[i1];
                        TerrainCacheVertex v2 = vertices[i2];
                        NumericsVector3 a = TransformModelToScene(v0, world);
                        NumericsVector3 b = TransformModelToScene(v1, world);
                        NumericsVector3 c = TransformModelToScene(v2, world);
                        if (!IsCandidateTriangle(a, b, c, mapPreset))
                        {
                            continue;
                        }

                        candidateTriangles++;
                        Rgba color = ResolveTriangleColor(v0, v1, v2, a, b, c);
                        float sortHeight = (a.Y + b.Y + c.Y) / 3f;
                        var triangle = new TerrainTriangle(a, b, c, color, sortHeight);
                        if (IsFeatureTriangle(v0, v1, v2, a, b, c))
                        {
                            featureSeen++;
                            AddReservoirSample(featureTriangles, MaxFeatureTriangles, featureSeen, featureRandom, triangle);
                        }
                        else
                        {
                            groundSeen++;
                            AddReservoirSample(groundTriangles, MaxGroundTriangles, groundSeen, groundRandom, triangle);
                        }
                    }
                });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Linux operator terrain scene skipped: {ex.Message}");
            return null;
        }

        var triangles = new List<TerrainTriangle>(groundTriangles.Count + featureTriangles.Count);
        triangles.AddRange(groundTriangles);
        triangles.AddRange(featureTriangles);
        triangles.Sort((left, right) => left.SortHeight.CompareTo(right.SortHeight));
        Console.WriteLine(
            $"Linux operator terrain scene loaded: {triangles.Count} sampled triangles from {candidateTriangles} terrain triangles, suppressed {suppressedTriangles} energy triangles.");
        return new TerrainSceneOverlay(triangles);
    }

    private static NumericsVector3 TransformModelToScene(TerrainCacheVertex vertex, SimulationWorldState world)
        => NumericsVector3.Transform(
            new NumericsVector3(vertex.X, vertex.Y, vertex.Z),
            world.RuntimeModelToSceneMatrix);

    private static void AddReservoirSample(
        List<TerrainTriangle> target,
        int capacity,
        int seen,
        Random random,
        TerrainTriangle triangle)
    {
        if (target.Count < capacity)
        {
            target.Add(triangle);
            return;
        }

        int slot = random.Next(seen);
        if (slot < capacity)
        {
            target[slot] = triangle;
        }
    }

    private static bool IsCandidateTriangle(
        NumericsVector3 a,
        NumericsVector3 b,
        NumericsVector3 c,
        MapPresetDefinition mapPreset)
    {
        if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
        {
            return false;
        }

        float minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        float maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
        float maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        float minZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
        float maxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
        if (maxX < -FieldMarginM
            || minX > mapPreset.FieldLengthM + FieldMarginM
            || maxZ < -FieldMarginM
            || minZ > mapPreset.FieldWidthM + FieldMarginM
            || maxY < -3.0f
            || minY > 8.0f)
        {
            return false;
        }

        NumericsVector3 normal = NumericsVector3.Cross(b - a, c - a);
        return normal.LengthSquared() > 1e-8f;
    }

    private static bool IsFeatureTriangle(
        TerrainCacheVertex v0,
        TerrainCacheVertex v1,
        TerrainCacheVertex v2,
        NumericsVector3 a,
        NumericsVector3 b,
        NumericsVector3 c)
    {
        NumericsVector3 normal = NumericsVector3.Cross(b - a, c - a);
        float normalLength = normal.Length();
        float up = normalLength <= 1e-6f ? 1f : MathF.Abs(normal.Y / normalLength);
        float height = (a.Y + b.Y + c.Y) / 3f;
        int r = (v0.R + v1.R + v2.R) / 3;
        int g = (v0.G + v1.G + v2.G) / 3;
        int bColor = (v0.B + v1.B + v2.B) / 3;
        int chroma = Math.Max(r, Math.Max(g, bColor)) - Math.Min(r, Math.Min(g, bColor));
        return height > 0.10f || up < 0.72f || chroma > 48;
    }

    private static bool IsFinite(NumericsVector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Rgba ResolveTriangleColor(
        TerrainCacheVertex v0,
        TerrainCacheVertex v1,
        TerrainCacheVertex v2,
        NumericsVector3 a,
        NumericsVector3 b,
        NumericsVector3 c)
    {
        int r = (v0.R + v1.R + v2.R) / 3;
        int g = (v0.G + v1.G + v2.G) / 3;
        int bColor = (v0.B + v1.B + v2.B) / 3;
        int alpha = (v0.A + v1.A + v2.A) / 3;
        if (alpha <= 0)
        {
            alpha = 255;
        }

        NumericsVector3 normal = NumericsVector3.Cross(b - a, c - a);
        float normalLength = normal.Length();
        float up = normalLength <= 1e-6f ? 1f : MathF.Abs(normal.Y / normalLength);
        float shade = 0.58f + up * 0.30f + Math.Clamp((a.Y + b.Y + c.Y) / 9f, 0f, 0.12f);
        r = Math.Clamp((int)MathF.Round(r * shade), 0, 255);
        g = Math.Clamp((int)MathF.Round(g * shade), 0, 255);
        bColor = Math.Clamp((int)MathF.Round(bColor * shade), 0, 255);
        int luma = (r * 30 + g * 59 + bColor * 11) / 100;
        if (luma < 24)
        {
            int lift = 24 - luma;
            r = Math.Clamp(r + lift, 0, 255);
            g = Math.Clamp(g + lift, 0, 255);
            bColor = Math.Clamp(bColor + lift, 0, 255);
        }

        return Rgba.FromBytes(r, g, bColor, Math.Clamp(alpha, 178, 238));
    }

    private static string ResolveTerrainCachePath(MapPresetDefinition mapPreset)
    {
        if (mapPreset.RuntimeGrid is null || string.IsNullOrWhiteSpace(mapPreset.RuntimeGrid.SourcePath))
        {
            return string.Empty;
        }

        string? mapDirectory = Path.GetDirectoryName(mapPreset.SourcePath);
        if (string.IsNullOrWhiteSpace(mapDirectory))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(mapPreset.RuntimeGrid.SourcePath)
            ? mapPreset.RuntimeGrid.SourcePath
            : Path.GetFullPath(Path.Combine(mapDirectory, mapPreset.RuntimeGrid.SourcePath));
    }
}

internal static class TerrainComponentSuppression
{
    private const string EnergyMechanismKeyword = "\u80fd\u91cf\u673a\u5173";
    private static readonly Dictionary<string, HashSet<int>> EnergyComponentCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static HashSet<int> LoadEnergyMechanismComponentIds(MapPresetDefinition mapPreset)
    {
        string annotationPath = mapPreset.AnnotationPath;
        if (string.IsNullOrWhiteSpace(annotationPath) || !File.Exists(annotationPath))
        {
            return new HashSet<int>();
        }

        string cacheKey = $"{Path.GetFullPath(annotationPath)}|{File.GetLastWriteTimeUtc(annotationPath).Ticks}";
        lock (Gate)
        {
            if (EnergyComponentCache.TryGetValue(cacheKey, out HashSet<int>? cached))
            {
                return cached;
            }
        }

        var componentIds = new HashSet<int>();
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(annotationPath));
            CollectEnergyCompositeComponentIds(root?["Composites"] as JsonArray, componentIds);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Linux operator energy component suppression skipped: {ex.Message}");
        }

        lock (Gate)
        {
            EnergyComponentCache[cacheKey] = componentIds;
        }

        if (componentIds.Count > 0)
        {
            Console.WriteLine($"Linux operator suppressing {componentIds.Count} annotated energy mechanism components from static terrain.");
        }

        return componentIds;
    }

    public static IReadOnlyList<TerrainIndexRange> BuildSuppressedIndexRanges(
        IReadOnlyList<TerrainCacheComponentRange> componentRanges,
        IReadOnlySet<int> suppressedComponentIds)
    {
        if (componentRanges.Count == 0 || suppressedComponentIds.Count == 0)
        {
            return Array.Empty<TerrainIndexRange>();
        }

        var ranges = new List<TerrainIndexRange>();
        foreach (TerrainCacheComponentRange range in componentRanges)
        {
            if (!suppressedComponentIds.Contains(range.ComponentId))
            {
                continue;
            }

            int start = Math.Max(0, range.StartIndex);
            int end = Math.Max(start, start + Math.Max(0, range.IndexCount));
            if (end > start)
            {
                ranges.Add(new TerrainIndexRange(start, end));
            }
        }

        if (ranges.Count <= 1)
        {
            return ranges;
        }

        ranges.Sort((left, right) => left.StartIndex.CompareTo(right.StartIndex));
        var merged = new List<TerrainIndexRange>(ranges.Count);
        foreach (TerrainIndexRange range in ranges)
        {
            if (merged.Count == 0 || range.StartIndex > merged[^1].EndIndex)
            {
                merged.Add(range);
                continue;
            }

            TerrainIndexRange previous = merged[^1];
            merged[^1] = new TerrainIndexRange(previous.StartIndex, Math.Max(previous.EndIndex, range.EndIndex));
        }

        return merged;
    }

    public static bool IsSuppressedTriangleIndex(int triangleStartIndex, IReadOnlyList<TerrainIndexRange> suppressedRanges)
    {
        if (suppressedRanges.Count == 0)
        {
            return false;
        }

        int triangleEndIndex = triangleStartIndex + 3;
        foreach (TerrainIndexRange range in suppressedRanges)
        {
            if (range.StartIndex >= triangleEndIndex)
            {
                return false;
            }

            if (triangleStartIndex < range.EndIndex && triangleEndIndex > range.StartIndex)
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectEnergyCompositeComponentIds(JsonArray? composites, HashSet<int> componentIds)
    {
        if (composites is null)
        {
            return;
        }

        foreach (JsonNode? node in composites)
        {
            JsonObject? composite = node as JsonObject;
            string name = composite?["Name"]?.GetValue<string>() ?? string.Empty;
            if (!name.Contains(EnergyMechanismKeyword, StringComparison.Ordinal))
            {
                continue;
            }

            AddComponentIds(composite?["ComponentIds"], componentIds);
            if (composite?["InteractionUnits"] is JsonArray units)
            {
                foreach (JsonNode? unit in units)
                {
                    AddComponentIds(unit?["ComponentIds"], componentIds);
                }
            }
        }
    }

    private static void AddComponentIds(JsonNode? node, HashSet<int> componentIds)
    {
        if (node is not JsonArray array)
        {
            return;
        }

        foreach (JsonNode? value in array)
        {
            if (value is null)
            {
                continue;
            }

            if (int.TryParse(
                    value.ToString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int id)
                && id >= 0)
            {
                componentIds.Add(id);
            }
        }
    }
}

internal readonly record struct TerrainIndexRange(int StartIndex, int EndIndex);

internal sealed class TerrainSceneRenderer3D : IDisposable
{
    private readonly List<TerrainVertex3D> _vertices;
    private int _program;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _viewProjectionUniform;
    private int _cameraUniform;
    private int _fogNearUniform;
    private int _fogFarUniform;

    private TerrainSceneRenderer3D(List<TerrainVertex3D> vertices)
    {
        _vertices = vertices;
    }

    public bool IsReady => _vertexArray != 0 && _vertices.Count > 0;

    public static TerrainSceneRenderer3D? TryBuild(MapPresetDefinition mapPreset, SimulationWorldState world)
    {
        string terrainCachePath = ResolveTerrainCachePath(mapPreset);
        if (string.IsNullOrWhiteSpace(terrainCachePath) || !File.Exists(terrainCachePath))
        {
            return null;
        }

        var vertices = new List<TerrainVertex3D>(700_000);
        int candidateTriangles = 0;
        int suppressedTriangles = 0;
        HashSet<int> suppressedComponentIds = TerrainComponentSuppression.LoadEnergyMechanismComponentIds(mapPreset);
        try
        {
            var reader = new TerrainCacheMeshReader();
            reader.Load(
                terrainCachePath,
                (_, _, sourceVertices, indices, componentRanges) =>
                {
                    IReadOnlyList<TerrainIndexRange> suppressedRanges = TerrainComponentSuppression.BuildSuppressedIndexRanges(
                        componentRanges,
                        suppressedComponentIds);
                    var sceneVertices = new NumericsVector3[sourceVertices.Length];
                    for (int vertexIndex = 0; vertexIndex < sourceVertices.Length; vertexIndex++)
                    {
                        sceneVertices[vertexIndex] = NumericsVector3.Transform(
                            new NumericsVector3(
                                sourceVertices[vertexIndex].X,
                                sourceVertices[vertexIndex].Y,
                                sourceVertices[vertexIndex].Z),
                            world.RuntimeModelToSceneMatrix);
                    }

                    int triangleIndexCount = indices.Length - indices.Length % 3;
                    for (int index = 0; index < triangleIndexCount; index += 3)
                    {
                        if (TerrainComponentSuppression.IsSuppressedTriangleIndex(index, suppressedRanges))
                        {
                            suppressedTriangles++;
                            continue;
                        }

                        int i0 = indices[index];
                        int i1 = indices[index + 1];
                        int i2 = indices[index + 2];
                        if ((uint)i0 >= (uint)sourceVertices.Length
                            || (uint)i1 >= (uint)sourceVertices.Length
                            || (uint)i2 >= (uint)sourceVertices.Length)
                        {
                            continue;
                        }

                        NumericsVector3 a = sceneVertices[i0];
                        NumericsVector3 b = sceneVertices[i1];
                        NumericsVector3 c = sceneVertices[i2];
                        if (!IsCandidateTriangle(a, b, c, mapPreset))
                        {
                            continue;
                        }

                        candidateTriangles++;
                        NumericsVector3 normal = NumericsVector3.Cross(b - a, c - a);
                        if (normal.LengthSquared() <= 1e-8f)
                        {
                            continue;
                        }

                        normal = NumericsVector3.Normalize(normal);
                        Rgba color = ResolveTriangleColor(sourceVertices[i0], sourceVertices[i1], sourceVertices[i2], a, b, c);
                        AppendVertex(vertices, a, normal, color);
                        AppendVertex(vertices, b, normal, color);
                        AppendVertex(vertices, c, normal, color);
                    }
                });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Linux operator 3D terrain skipped: {ex.Message}");
            return null;
        }

        if (vertices.Count == 0)
        {
            return null;
        }

        Console.WriteLine(
            $"Linux operator 3D terrain loaded: {candidateTriangles} triangles, {vertices.Count} vertices, suppressed {suppressedTriangles} energy triangles.");
        return new TerrainSceneRenderer3D(vertices);
    }

    public void Initialize()
    {
        if (_vertices.Count == 0 || _program != 0)
        {
            return;
        }

        _program = CreateProgram(VertexShaderSource, FragmentShaderSource);
        _viewProjectionUniform = GL.GetUniformLocation(_program, "uViewProjection");
        _cameraUniform = GL.GetUniformLocation(_program, "uCameraPosition");
        _fogNearUniform = GL.GetUniformLocation(_program, "uFogNear");
        _fogFarUniform = GL.GetUniformLocation(_program, "uFogFar");
        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            _vertices.Count * TerrainVertex3D.StrideBytes,
            _vertices.ToArray(),
            BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, TerrainVertex3D.StrideBytes, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, TerrainVertex3D.StrideBytes, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, TerrainVertex3D.StrideBytes, 6 * sizeof(float));
        GL.BindVertexArray(0);
    }

    public void Draw(Matrix4 viewProjection, Vector3 cameraPosition)
    {
        if (!IsReady)
        {
            return;
        }

        GL.UseProgram(_program);
        GL.UniformMatrix4(_viewProjectionUniform, false, ref viewProjection);
        GL.Uniform3(_cameraUniform, cameraPosition);
        GL.Uniform1(_fogNearUniform, 34f);
        GL.Uniform1(_fogFarUniform, 1200f);
        GL.BindVertexArray(_vertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertices.Count);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_vertexBuffer != 0)
        {
            GL.DeleteBuffer(_vertexBuffer);
        }

        if (_vertexArray != 0)
        {
            GL.DeleteVertexArray(_vertexArray);
        }

        if (_program != 0)
        {
            GL.DeleteProgram(_program);
        }
    }

    private static void AppendVertex(
        List<TerrainVertex3D> vertices,
        NumericsVector3 position,
        NumericsVector3 normal,
        Rgba color)
        => vertices.Add(new TerrainVertex3D(
            position.X,
            position.Y,
            position.Z,
            normal.X,
            normal.Y,
            normal.Z,
            color.R,
            color.G,
            color.B,
            color.A));

    private static bool IsCandidateTriangle(
        NumericsVector3 a,
        NumericsVector3 b,
        NumericsVector3 c,
        MapPresetDefinition mapPreset)
    {
        if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
        {
            return false;
        }

        float minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        float maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
        float maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        float minZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
        float maxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
        if (maxX < -2.0f
            || minX > mapPreset.FieldLengthM + 2.0f
            || maxZ < -2.0f
            || minZ > mapPreset.FieldWidthM + 2.0f
            || maxY < -3.0f
            || minY > 8.0f)
        {
            return false;
        }

        return NumericsVector3.Cross(b - a, c - a).LengthSquared() > 1e-8f;
    }

    private static bool IsFinite(NumericsVector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Rgba ResolveTriangleColor(
        TerrainCacheVertex v0,
        TerrainCacheVertex v1,
        TerrainCacheVertex v2,
        NumericsVector3 a,
        NumericsVector3 b,
        NumericsVector3 c)
    {
        int r = (v0.R + v1.R + v2.R) / 3;
        int g = (v0.G + v1.G + v2.G) / 3;
        int bColor = (v0.B + v1.B + v2.B) / 3;
        int alpha = (v0.A + v1.A + v2.A) / 3;
        if (alpha <= 0)
        {
            alpha = 255;
        }

        NumericsVector3 normal = NumericsVector3.Cross(b - a, c - a);
        float normalLength = normal.Length();
        float up = normalLength <= 1e-6f ? 1f : MathF.Abs(normal.Y / normalLength);
        float shade = 0.74f + up * 0.18f + Math.Clamp((a.Y + b.Y + c.Y) / 18f, 0f, 0.08f);
        r = Math.Clamp((int)MathF.Round(r * shade), 0, 255);
        g = Math.Clamp((int)MathF.Round(g * shade), 0, 255);
        bColor = Math.Clamp((int)MathF.Round(bColor * shade), 0, 255);
        int luma = (r * 30 + g * 59 + bColor * 11) / 100;
        if (luma < 22)
        {
            int lift = 22 - luma;
            r = Math.Clamp(r + lift, 0, 255);
            g = Math.Clamp(g + lift, 0, 255);
            bColor = Math.Clamp(bColor + lift, 0, 255);
        }

        return Rgba.FromBytes(r, g, bColor, Math.Clamp(alpha, 200, 255));
    }

    private static string ResolveTerrainCachePath(MapPresetDefinition mapPreset)
    {
        if (mapPreset.RuntimeGrid is null || string.IsNullOrWhiteSpace(mapPreset.RuntimeGrid.SourcePath))
        {
            return string.Empty;
        }

        string? mapDirectory = Path.GetDirectoryName(mapPreset.SourcePath);
        if (string.IsNullOrWhiteSpace(mapDirectory))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(mapPreset.RuntimeGrid.SourcePath)
            ? mapPreset.RuntimeGrid.SourcePath
            : Path.GetFullPath(Path.Combine(mapDirectory, mapPreset.RuntimeGrid.SourcePath));
    }

    private static int CreateProgram(string vertexSource, string fragmentSource)
    {
        int vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        int fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = GL.GetProgramInfoLog(program);
            throw new InvalidOperationException($"OpenGL terrain program link failed: {log}");
        }

        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"{type} terrain compile failed: {log}");
        }

        return shader;
    }

    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aColor;
        uniform mat4 uViewProjection;
        out vec3 vWorldPosition;
        out vec3 vNormal;
        out vec4 vColor;
        void main()
        {
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
            vWorldPosition = aPosition;
            vNormal = normalize(aNormal);
            vColor = aColor;
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec3 vWorldPosition;
        in vec3 vNormal;
        in vec4 vColor;
        uniform vec3 uCameraPosition;
        uniform float uFogNear;
        uniform float uFogFar;
        out vec4 FragColor;
        void main()
        {
            vec3 normal = normalize(vNormal);
            vec3 lightDirection = normalize(vec3(0.35, 0.92, 0.25));
            float diffuse = max(dot(normal, lightDirection), 0.0);
            float lighting = 0.42 + diffuse * 0.58;
            vec3 litColor = vColor.rgb * lighting;
            float distanceToCamera = distance(vWorldPosition, uCameraPosition);
            float fogFactor = smoothstep(uFogNear, uFogFar, distanceToCamera);
            vec3 fogColor = vec3(0.50, 0.60, 0.68);
            FragColor = vec4(mix(litColor, fogColor, fogFactor), vColor.a);
        }
        """;
}

internal readonly record struct TerrainVertex3D(
    float X,
    float Y,
    float Z,
    float NormalX,
    float NormalY,
    float NormalZ,
    float R,
    float G,
    float B,
    float A)
{
    public const int StrideBytes = 10 * sizeof(float);
}

internal readonly record struct TerrainTriangle(
    NumericsVector3 A,
    NumericsVector3 B,
    NumericsVector3 C,
    Rgba Color,
    float SortHeight);

internal readonly record struct Rgba(float R, float G, float B, float A)
{
    public static Rgba FromBytes(int r, int g, int b, int a = 255)
        => new(r / 255f, g / 255f, b / 255f, a / 255f);

    public Rgba WithAlpha(int alpha)
        => this with { A = Math.Clamp(alpha, 0, 255) / 255f };
}

internal sealed class PrimitiveBatch2D : IDisposable
{
    private readonly List<float> _vertices = new(65536);
    private int _program;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _viewportUniform;
    private int _width;
    private int _height;

    public void Initialize()
    {
        _program = CreateProgram(VertexShaderSource, FragmentShaderSource);
        _viewportUniform = GL.GetUniformLocation(_program, "uViewport");
        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, 1024, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 6 * sizeof(float), 2 * sizeof(float));
        GL.BindVertexArray(0);
    }

    public void Begin(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _vertices.Clear();
    }

    public void Flush()
    {
        if (_vertices.Count == 0)
        {
            return;
        }

        GL.UseProgram(_program);
        GL.Uniform2(_viewportUniform, (float)_width, (float)_height);
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Count * sizeof(float), _vertices.ToArray(), BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertices.Count / 6);
        GL.BindVertexArray(0);
    }

    public void FillRect(float x, float y, float width, float height, Rgba color)
    {
        AddQuad(x, y, x + width, y, x + width, y + height, x, y + height, color);
    }

    public void StrokeRect(float x, float y, float width, float height, float thickness, Rgba color)
    {
        FillRect(x, y, width, thickness, color);
        FillRect(x, y + height - thickness, width, thickness, color);
        FillRect(x, y, thickness, height, color);
        FillRect(x + width - thickness, y, thickness, height, color);
    }

    public void Line(float x1, float y1, float x2, float y2, float thickness, Rgba color)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 1e-4f)
        {
            return;
        }

        float nx = -dy / length * thickness * 0.5f;
        float ny = dx / length * thickness * 0.5f;
        AddQuad(x1 - nx, y1 - ny, x2 - nx, y2 - ny, x2 + nx, y2 + ny, x1 + nx, y1 + ny, color);
    }

    public void FillCircle(float cx, float cy, float radius, Rgba color, int segments)
    {
        int safeSegments = Math.Clamp(segments, 8, 96);
        for (int index = 0; index < safeSegments; index++)
        {
            float a0 = MathF.Tau * index / safeSegments;
            float a1 = MathF.Tau * (index + 1) / safeSegments;
            AddTriangle(
                cx,
                cy,
                cx + MathF.Cos(a0) * radius,
                cy + MathF.Sin(a0) * radius,
                cx + MathF.Cos(a1) * radius,
                cy + MathF.Sin(a1) * radius,
                color);
        }
    }

    public void Ring(float cx, float cy, float innerRadius, float outerRadius, Rgba color, int segments)
    {
        int safeSegments = Math.Clamp(segments, 12, 128);
        float inner = MathF.Max(0f, innerRadius);
        float outer = MathF.Max(inner + 0.5f, outerRadius);
        for (int index = 0; index < safeSegments; index++)
        {
            float a0 = MathF.Tau * index / safeSegments;
            float a1 = MathF.Tau * (index + 1) / safeSegments;
            float ix0 = cx + MathF.Cos(a0) * inner;
            float iy0 = cy + MathF.Sin(a0) * inner;
            float ox0 = cx + MathF.Cos(a0) * outer;
            float oy0 = cy + MathF.Sin(a0) * outer;
            float ix1 = cx + MathF.Cos(a1) * inner;
            float iy1 = cy + MathF.Sin(a1) * inner;
            float ox1 = cx + MathF.Cos(a1) * outer;
            float oy1 = cy + MathF.Sin(a1) * outer;
            AddTriangle(ix0, iy0, ox0, oy0, ox1, oy1, color);
            AddTriangle(ix0, iy0, ox1, oy1, ix1, iy1, color);
        }
    }

    public void Polygon(IReadOnlyList<(float X, float Y)> points, Rgba color)
    {
        if (points.Count < 3)
        {
            return;
        }

        for (int index = 1; index < points.Count - 1; index++)
        {
            AddTriangle(points[0].X, points[0].Y, points[index].X, points[index].Y, points[index + 1].X, points[index + 1].Y, color);
        }
    }

    public void Triangle(float x1, float y1, float x2, float y2, float x3, float y3, Rgba color)
    {
        AddTriangle(x1, y1, x2, y2, x3, y3, color);
    }

    private void AddQuad(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4, Rgba color)
    {
        AddTriangle(x1, y1, x2, y2, x3, y3, color);
        AddTriangle(x1, y1, x3, y3, x4, y4, color);
    }

    private void AddTriangle(float x1, float y1, float x2, float y2, float x3, float y3, Rgba color)
    {
        AddVertex(x1, y1, color);
        AddVertex(x2, y2, color);
        AddVertex(x3, y3, color);
    }

    private void AddVertex(float x, float y, Rgba color)
    {
        _vertices.Add(x);
        _vertices.Add(y);
        _vertices.Add(color.R);
        _vertices.Add(color.G);
        _vertices.Add(color.B);
        _vertices.Add(color.A);
    }

    private static int CreateProgram(string vertexSource, string fragmentSource)
    {
        int vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        int fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = GL.GetProgramInfoLog(program);
            throw new InvalidOperationException($"OpenGL program link failed: {log}");
        }

        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }

        return shader;
    }

    public void Dispose()
    {
        if (_vertexBuffer != 0)
        {
            GL.DeleteBuffer(_vertexBuffer);
        }

        if (_vertexArray != 0)
        {
            GL.DeleteVertexArray(_vertexArray);
        }

        if (_program != 0)
        {
            GL.DeleteProgram(_program);
        }
    }

    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec4 aColor;
        uniform vec2 uViewport;
        out vec4 vColor;
        void main()
        {
            vec2 ndc = vec2((aPosition.x / uViewport.x) * 2.0 - 1.0, 1.0 - (aPosition.y / uViewport.y) * 2.0);
            gl_Position = vec4(ndc, 0.0, 1.0);
            vColor = aColor;
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec4 vColor;
        out vec4 FragColor;
        void main()
        {
            FragColor = vColor;
        }
        """;
}
