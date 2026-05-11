using Simulator.Assets;
using Simulator.Core;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;
using Simulator.Platform.Rendering;
using Simulator.Platform.Runtime;
using Simulator.Platform.Ui;
using Simulator.Runtime.Input;
using System.Drawing;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Simulator.Linux;

internal sealed class LinuxOperatorRuntime
{
    private const double FixedStepSec = 1.0 / 60.0;
    private const double LocalDriveSpeedWorldPerSec = 72.0;

    private readonly LinuxOperatorOptions _options;
    private readonly ConfigurationService _configurationService = new();
    private readonly AssetCatalogService _assetCatalogService = new();
    private readonly MapPresetService _mapPresetService = new();
    private readonly RuleSetLoader _ruleSetLoader = new();
    private readonly SimulationBootstrapService _bootstrapService = new();
    private readonly SimulatorRuntimeStateMachine _stateMachine = new();
    private ProjectLayout? _layout;
    private JsonObject _config = new();
    private RuleSet _rules = RuleSet.CreateDefault();
    private MapPresetDefinition? _mapPresetDefinition;
    private RobotAppearanceRoot _appearanceRoot = RobotAppearanceJsonSerializer.CreateDefault();
    private readonly Dictionary<string, RobotAppearanceProfileDefinition?> _appearanceProfileCache = new(StringComparer.OrdinalIgnoreCase);
    private SimulationWorldState? _world;
    private RuleSimulationService? _ruleSimulationService;
    private TerrainCacheHeightField? _terrainHeightField;
    private LinuxTerrainMotionService _terrainMotionService = new(null);
    private GameInputSnapshot _lastInput = GameInputSnapshot.Empty;
    private LinuxGameRenderSnapshot _renderSnapshot = LinuxGameRenderSnapshot.Empty;
    private string _localEntityId = string.Empty;
    private double _simulationAccumulatorSec;
    private string _mapPreset = "unknown";
    private string _status = "Booting";
    private string _terrainCachePath = string.Empty;
    private LinuxCameraMode _cameraMode = LinuxCameraMode.ThirdPerson;

    public LinuxOperatorRuntime(LinuxOperatorOptions options)
    {
        _options = options;
    }

    public string MapPreset => _mapPreset;

    public string Status => $"{_status} | {RuntimePhaseLine}";

    public string RuntimePhaseLine
    {
        get
        {
            SimulatorRuntimeSnapshot snapshot = _stateMachine.Snapshot;
            string timer = snapshot.PhaseDurationSec > 1e-6
                ? $" {Math.Ceiling(snapshot.PhaseRemainingSec):0}s"
                : string.Empty;
            string panel = snapshot.PanelOpen ? $" panel={snapshot.PanelKind}/{snapshot.PanelPage}" : " panel=closed";
            return $"phase={snapshot.Phase}{timer}{panel}";
        }
    }

    public double TimeSec => _stateMachine.Snapshot.ElapsedSec;

    public long Frame => _stateMachine.Snapshot.Frame;

    public bool OperatorPanelOpen => _stateMachine.Snapshot.PanelOpen;

    public OpenGkRefereePanelPage LocalPanelPage => _stateMachine.Snapshot.PanelPage;

    public bool CaptureMouse => _stateMachine.Snapshot.CaptureMouse;

    public bool MovementInputEnabled => _stateMachine.Snapshot.MovementInputEnabled;

    public SimulatorRuntimeSnapshot RuntimeSnapshot => _stateMachine.Snapshot;

    public LinuxGameRenderSnapshot RenderSnapshot => _renderSnapshot;

    public string TerrainCachePath => _terrainCachePath;

    public LinuxCameraMode CameraMode => _cameraMode;

    public OpenGkRuntimeHudState CreateHudState()
    {
        SimulatorRuntimeSnapshot snapshot = _stateMachine.Snapshot;
        double progress = snapshot.PhaseDurationSec > 1e-6
            ? 1.0 - Math.Clamp(snapshot.PhaseRemainingSec / snapshot.PhaseDurationSec, 0.0, 1.0)
            : 1.0;
        string phase = snapshot.PhaseDurationSec > 1e-6
            ? $"{snapshot.Phase} {Math.Ceiling(snapshot.PhaseRemainingSec):0}s"
            : snapshot.Phase.ToString();
        return new OpenGkRuntimeHudState(
            _status,
            phase,
            "O panel / Enter skip local countdown / Alt release mouse",
            progress,
            !snapshot.MovementInputEnabled,
            snapshot.PanelOpen,
            snapshot.DeathOverlayVisible,
            snapshot.DeathOverlayProgress);
    }

    public void Load()
    {
        _layout = ProjectLayout.Discover();
        string configPath = _configurationService.ResolvePrimaryConfigPath(_layout);
        _config = _configurationService.LoadConfig(configPath);
        _mapPreset = string.IsNullOrWhiteSpace(_options.MapPreset)
            ? _mapPresetService.ResolvePresetName(_layout, _configurationService)
            : _options.MapPreset!;
        _mapPresetDefinition = _mapPresetService.LoadPreset(_layout, _mapPreset);
        _terrainCachePath = ResolveTerrainCachePath(_mapPresetDefinition);
        LoadTerrainMotionData(_terrainCachePath);
        LoadAppearanceProfiles(_layout);
        _rules = _ruleSetLoader.LoadFromConfig(_config);
        _world = _bootstrapService.BuildInitialWorld(_config, _rules, _mapPresetDefinition);
        _ruleSimulationService = new RuleSimulationService(
            _rules,
            new ArenaInteractionService(_rules),
            enableAutoMovement: false,
            enableAiCombat: false);
        SelectLocalEntity();
        _renderSnapshot = BuildRenderSnapshot();

        AssetCatalog catalog = _assetCatalogService.BuildCatalog(_layout);
        _status = catalog.IsComplete
            ? $"Ready: {_mapPreset} entities={_world.Entities.Count}"
            : $"Ready with missing assets: {_mapPreset} entities={_world.Entities.Count}";
        _stateMachine.ResetToRoom(SimulatorRuntimeMode.Local);

        SimulatorRuntimeLog.Append(
            "linux_operator.log",
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} load root={_layout.RootPath} config={configPath} map={_mapPreset} terrain_cache={_terrainCachePath} world={_world.WorldWidth:0}x{_world.WorldHeight:0} entities={_world.Entities.Count} facilities={_mapPresetDefinition.Facilities.Count} catalog_complete={catalog.IsComplete} {RuntimePhaseLine}");
    }

    public void ApplyInput(GameInputSnapshot input)
    {
        _lastInput = input;
        if (input.PressedKeys.Contains(GameKey.V))
        {
            _cameraMode = _cameraMode == LinuxCameraMode.FirstPerson
                ? LinuxCameraMode.ThirdPerson
                : LinuxCameraMode.FirstPerson;
        }

        _stateMachine.ApplyInput(input);
    }

    public void ApplyUiAction(string action)
    {
        bool handled = ApplyLocalControlAction(action) || _stateMachine.ApplyUiAction(action);

        SimulatorRuntimeLog.Append(
            "linux_operator.log",
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ui action={action} handled={handled} frame={Frame} {RuntimePhaseLine}");
    }

    public IReadOnlyList<OpenGkRefereeEnergyCard> CreateEnergyCards()
    {
        if (_world is null)
        {
            return Array.Empty<OpenGkRefereeEnergyCard>();
        }

        return
        [
            new("Red Small Energy", "local_energy:Red Small Energy", ResolveEnergyCardActiveCount("red", large: false)),
            new("Red Large Energy", "local_energy:Red Large Energy", ResolveEnergyCardActiveCount("red", large: true)),
            new("Blue Small Energy", "local_energy:Blue Small Energy", ResolveEnergyCardActiveCount("blue", large: false)),
            new("Blue Large Energy", "local_energy:Blue Large Energy", ResolveEnergyCardActiveCount("blue", large: true)),
        ];
    }

    public IReadOnlyList<OpenGkRefereeQuickAction> CreateQuickActions()
    {
        bool redBaseOpen = _world?.GetOrCreateTeamState("red").BaseArmorForcedOpen ?? false;
        bool blueBaseOpen = _world?.GetOrCreateTeamState("blue").BaseArmorForcedOpen ?? false;
        bool redOutpostStopped = ResolveOutpostStopped("red");
        bool blueOutpostStopped = ResolveOutpostStopped("blue");
        return
        [
            new(redBaseOpen ? "Red Armor Close" : "Red Armor Open", "local_base_armor:red:toggle", redBaseOpen),
            new(blueBaseOpen ? "Blue Armor Close" : "Blue Armor Open", "local_base_armor:blue:toggle", blueBaseOpen),
            new(redOutpostStopped ? "Red Outpost Run" : "Red Outpost Stop", "local_outpost:red:toggle", redOutpostStopped),
            new(blueOutpostStopped ? "Blue Outpost Run" : "Blue Outpost Stop", "local_outpost:blue:toggle", blueOutpostStopped),
            new("Start Prep", "room_start", RuntimeSnapshot.Phase == SimulatorRuntimePhase.Preparation),
            new("Enter Live", "local_start", RuntimeSnapshot.Phase == SimulatorRuntimePhase.Live),
        ];
    }

    public void Tick(double deltaSec)
    {
        _stateMachine.Tick(deltaSec);
        TickWorld(deltaSec);
        _renderSnapshot = BuildRenderSnapshot();
    }

    private void TickWorld(double deltaSec)
    {
        if (_world is null || _mapPresetDefinition is null || _ruleSimulationService is null)
        {
            return;
        }

        double clamped = Math.Clamp(deltaSec, 0.0, 0.12);
        _simulationAccumulatorSec += clamped;
        while (_simulationAccumulatorSec >= FixedStepSec)
        {
            ApplyLocalPlayerInput(FixedStepSec);
            _ruleSimulationService.Run(
                _world,
                _mapPresetDefinition.Facilities,
                FixedStepSec,
                FixedStepSec,
                captureFinalEntities: false,
                enableCombat: true);
            _simulationAccumulatorSec -= FixedStepSec;
        }
    }

    private bool ApplyLocalControlAction(string action)
    {
        if (_world is null || string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        if (action.StartsWith("local_energy:", StringComparison.OrdinalIgnoreCase))
        {
            ApplyEnergyAction(action);
            return true;
        }

        if (action.StartsWith("local_base_armor:", StringComparison.OrdinalIgnoreCase))
        {
            string team = action.Split(':', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(team))
            {
                SimulationTeamState state = _world.GetOrCreateTeamState(team);
                state.BaseArmorForcedOpen = !state.BaseArmorForcedOpen;
                return true;
            }
        }

        if (action.StartsWith("local_outpost:", StringComparison.OrdinalIgnoreCase))
        {
            string team = action.Split(':', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? string.Empty;
            SimulationEntity? outpost = _world.Entities.FirstOrDefault(entity =>
                string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entity.Team, team, StringComparison.OrdinalIgnoreCase));
            if (outpost is not null)
            {
                outpost.OutpostRotationStopped = !outpost.OutpostRotationStopped;
                outpost.OutpostStoppedRelativeRotationRad = SimulationCombatMath.ResolveOutpostRingRelativeRotationRad(outpost, _world.GameTimeSec);
                return true;
            }
        }

        return false;
    }

    private void ApplyEnergyAction(string action)
    {
        string[] parts = action.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return;
        }

        string card = parts[1];
        int count = int.TryParse(parts[2], out int parsed) ? Math.Clamp(parsed, 0, 5) : 0;
        string team = card.Contains("Red", StringComparison.OrdinalIgnoreCase) ? "red" : "blue";
        bool large = card.Contains("Large", StringComparison.OrdinalIgnoreCase);
        SimulationTeamState state = _world!.GetOrCreateTeamState(team);
        state.EnergyLargeMechanismActive = large;
        state.EnergyActivatedGroupCount = count;
        state.EnergyMechanismState = count >= 5 ? "activated" : count > 0 ? "activating" : "inactive";
        for (int index = 0; index < state.EnergyActivationOrder.Length; index++)
        {
            state.EnergyActivationOrder[index] = index;
            state.EnergyHitRingsByArm[index] = index < count ? 10 : 0;
        }

        state.EnergyCurrentLitMask = count is > 0 and < 5 ? 1 << count : 0;
        state.EnergyStateStartTimeSec = _world.GameTimeSec;
    }

    private int ResolveEnergyCardActiveCount(string team, bool large)
    {
        if (_world is null)
        {
            return 0;
        }

        SimulationTeamState state = _world.GetOrCreateTeamState(team);
        return state.EnergyLargeMechanismActive == large
            ? Math.Clamp(state.EnergyActivatedGroupCount, 0, 5)
            : 0;
    }

    private bool ResolveOutpostStopped(string team)
        => _world?.Entities.Any(entity =>
            string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            && string.Equals(entity.Team, team, StringComparison.OrdinalIgnoreCase)
            && entity.OutpostRotationStopped) == true;

    private void ApplyLocalPlayerInput(double dt)
    {
        if (_world is null)
        {
            return;
        }

        SimulationEntity? entity = _world.Entities.FirstOrDefault(candidate => candidate.Id == _localEntityId);
        if (entity is null || !entity.IsAlive)
        {
            return;
        }

        if (!MovementInputEnabled)
        {
            entity.MoveInputForward = 0.0;
            entity.MoveInputRight = 0.0;
            entity.IsFireCommandActive = false;
            entity.MotionBlockReason = "input_disabled";
            return;
        }

        double forward = 0.0;
        double right = 0.0;
        if (_lastInput.DownKeys.Contains(GameKey.W))
        {
            forward += 1.0;
        }

        if (_lastInput.DownKeys.Contains(GameKey.S))
        {
            forward -= 1.0;
        }

        if (_lastInput.DownKeys.Contains(GameKey.D))
        {
            right += 1.0;
        }

        if (_lastInput.DownKeys.Contains(GameKey.A))
        {
            right -= 1.0;
        }

        double length = Math.Sqrt(forward * forward + right * right);
        if (length > 1.0)
        {
            forward /= length;
            right /= length;
        }

        entity.MoveInputForward = forward;
        entity.MoveInputRight = right;
        entity.IsFireCommandActive = _lastInput.DownMouseButtons.Contains(GameMouseButton.Left);
        entity.AutoAimRequested = _lastInput.DownMouseButtons.Contains(GameMouseButton.Right);
        entity.SmallGyroActive = _lastInput.DownKeys.Contains(GameKey.X);

        if (Math.Abs(_lastInput.Pointer.DeltaX) > 1e-3)
        {
            entity.TurretYawDeg = NormalizeDegrees(entity.TurretYawDeg + _lastInput.Pointer.DeltaX * 0.08);
            entity.ChassisTargetYawDeg = entity.TurretYawDeg;
        }

        if (Math.Abs(_lastInput.Pointer.DeltaY) > 1e-3)
        {
            entity.GimbalPitchDeg = Math.Clamp(entity.GimbalPitchDeg - _lastInput.Pointer.DeltaY * 0.05, -25.0, 35.0);
        }

        if (length <= 1e-6)
        {
            entity.VelocityXWorldPerSec = 0.0;
            entity.VelocityYWorldPerSec = 0.0;
            LinuxGroundPose pose = _terrainMotionService.ResolveGroundPose(_world, entity);
            entity.GroundHeightM = pose.GroundHeightM;
            entity.ChassisPitchDeg = pose.PitchDeg;
            entity.ChassisRollDeg = pose.RollDeg;
            entity.MotionBlockReason = string.Empty;
            return;
        }

        double yawRad = entity.TurretYawDeg * Math.PI / 180.0;
        double worldForwardX = Math.Cos(yawRad);
        double worldForwardY = Math.Sin(yawRad);
        double worldRightX = -worldForwardY;
        double worldRightY = worldForwardX;
        double speed = LocalDriveSpeedWorldPerSec * entity.ChassisSpeedScale;
        double desiredVelocityX = (worldForwardX * forward + worldRightX * right) * speed;
        double desiredVelocityY = (worldForwardY * forward + worldRightY * right) * speed;
        LinuxMotionResolveResult motion = _terrainMotionService.ResolveMove(
            _world,
            _mapPresetDefinition!,
            entity,
            desiredVelocityX,
            desiredVelocityY,
            dt);
        entity.X = motion.X;
        entity.Y = motion.Y;
        entity.VelocityXWorldPerSec = motion.VelocityXWorldPerSec;
        entity.VelocityYWorldPerSec = motion.VelocityYWorldPerSec;
        entity.GroundHeightM = motion.GroundHeightM;
        entity.ChassisPitchDeg = motion.PitchDeg;
        entity.ChassisRollDeg = motion.RollDeg;
        entity.MotionBlockReason = motion.BlockReason;
        entity.AngleDeg = entity.TurretYawDeg;
    }

    private void SelectLocalEntity()
    {
        if (_world is null)
        {
            return;
        }

        SimulationEntity? entity = _world.Entities.FirstOrDefault(candidate =>
                string.Equals(candidate.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Team, "blue", StringComparison.OrdinalIgnoreCase))
            ?? _world.Entities.FirstOrDefault(candidate =>
                string.Equals(candidate.EntityType, "robot", StringComparison.OrdinalIgnoreCase));
        if (entity is null)
        {
            return;
        }

        foreach (SimulationEntity candidate in _world.Entities)
        {
            candidate.IsPlayerControlled = false;
        }

        entity.IsPlayerControlled = true;
        _localEntityId = entity.Id;
    }

    private LinuxGameRenderSnapshot BuildRenderSnapshot()
    {
        if (_world is null || _mapPresetDefinition is null)
        {
            return LinuxGameRenderSnapshot.Empty;
        }

        var interactions = BuildInteractionRenderList(_world, _mapPresetDefinition);
        double metersPerModelUnit = ResolveMetersPerModelUnit(_world);
        var sceneInteractions = BuildSceneInteractionRenderList(_world, _mapPresetDefinition, metersPerModelUnit);
        return new LinuxGameRenderSnapshot(
            _mapPreset,
            _world.WorldWidth,
            _world.WorldHeight,
            metersPerModelUnit,
            _world.GameTimeSec,
            _localEntityId,
            _mapPresetDefinition.Facilities
                .Select(region => new LinuxFacilityRenderItem(
                    region.Id,
                    region.Type,
                    region.Team,
                    region.Shape,
                    region.X1,
                    region.Y1,
                    region.X2,
                    region.Y2,
                    region.HeightM,
                    region.Points))
                .ToArray(),
            _world.Entities
                .Select(entity =>
                {
                    EntityScenePose pose = ResolveEntityScenePose(_world, entity);
                    return new LinuxEntityRenderItem(
                        entity.Id,
                        entity.Team,
                        entity.EntityType,
                        entity.RoleKey,
                        entity.X,
                        entity.Y,
                        entity.AngleDeg,
                        entity.TurretYawDeg,
                        entity.GimbalPitchDeg,
                        entity.ChassisPitchDeg,
                        entity.ChassisRollDeg,
                        pose.Position.X,
                        pose.Position.Y,
                        pose.Position.Z,
                        pose.ChassisForward.X,
                        pose.ChassisForward.Y,
                        pose.ChassisForward.Z,
                        pose.TurretForward.X,
                        pose.TurretForward.Y,
                        pose.TurretForward.Z,
                        entity.Health,
                        entity.MaxHealth,
                        entity.BodyWidthM,
                        entity.BodyLengthM,
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
                        entity.WheelOffsetsM,
                        ResolveAppearanceProfile(entity),
                        entity.IsAlive,
                        entity.IsPlayerControlled,
                        entity.Id == _localEntityId);
                })
                .ToArray(),
            _world.Projectiles
                .Select(projectile => new LinuxProjectileRenderItem(
                    projectile.Id,
                    projectile.Team,
                    projectile.AmmoType,
                    projectile.X,
                    projectile.Y,
                    projectile.HeightM))
                .ToArray(),
            interactions.Items.ToArray(),
            sceneInteractions);
    }

    private void LoadAppearanceProfiles(ProjectLayout layout)
    {
        try
        {
            _appearanceRoot = RobotAppearanceJsonSerializer.LoadFromFile(layout.AppearancePresetPath);
            _appearanceProfileCache.Clear();
        }
        catch (Exception ex)
        {
            _appearanceRoot = RobotAppearanceJsonSerializer.CreateDefault();
            _appearanceProfileCache.Clear();
            SimulatorRuntimeLog.Append(
                "linux_operator.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} appearance_load_failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LoadTerrainMotionData(string terrainCachePath)
    {
        _terrainHeightField = null;
        _terrainMotionService = new LinuxTerrainMotionService(null);
        if (string.IsNullOrWhiteSpace(terrainCachePath) || !File.Exists(terrainCachePath))
        {
            return;
        }

        try
        {
            _terrainHeightField = TerrainCacheHeightField.Load(terrainCachePath);
            _terrainMotionService = new LinuxTerrainMotionService(_terrainHeightField);
            SimulatorRuntimeLog.Append(
                "linux_operator.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} terrain_motion loaded=True path={terrainCachePath}");
        }
        catch (Exception ex)
        {
            SimulatorRuntimeLog.Append(
                "linux_operator.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} terrain_motion_failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private RobotAppearanceProfileDefinition? ResolveAppearanceProfile(SimulationEntity entity)
    {
        string key = $"{entity.RoleKey}|{entity.ChassisSubtype}";
        if (_appearanceProfileCache.TryGetValue(key, out RobotAppearanceProfileDefinition? profile))
        {
            return profile;
        }

        profile = RobotAppearanceProjectAdapter.ResolveProfile(_appearanceRoot, entity.RoleKey, entity.ChassisSubtype);
        _appearanceProfileCache[key] = profile;
        return profile;
    }

    private static EntityScenePose ResolveEntityScenePose(SimulationWorldState world, SimulationEntity entity)
    {
        if (!Matrix4x4.Invert(world.RuntimeModelToWorldMatrix, out Matrix4x4 worldToModel))
        {
            Vector3 fallback = new((float)entity.X, 0.0f, (float)entity.Y);
            return new EntityScenePose(fallback, Vector3.UnitX, Vector3.UnitX);
        }

        Vector3 position = Vector3.Transform(new Vector3((float)entity.X, 0.0f, (float)entity.Y), worldToModel);
        Vector3 chassisForward = ResolveForward(worldToModel, entity.X, entity.Y, entity.AngleDeg, position);
        Vector3 turretForward = ResolveForward(worldToModel, entity.X, entity.Y, entity.TurretYawDeg, position);
        return new EntityScenePose(position, chassisForward, turretForward);
    }

    private static Vector3 ResolveForward(Matrix4x4 worldToModel, double x, double y, double yawDeg, Vector3 originModel)
    {
        double yawRad = yawDeg * Math.PI / 180.0;
        Vector3 ahead = Vector3.Transform(
            new Vector3((float)(x + Math.Cos(yawRad) * 8.0), 0.0f, (float)(y + Math.Sin(yawRad) * 8.0)),
            worldToModel);
        Vector3 forward = ahead - originModel;
        forward.Y = 0.0f;
        return forward.LengthSquared() > 1e-8f ? Vector3.Normalize(forward) : Vector3.UnitX;
    }

    private static double ResolveMetersPerModelUnit(SimulationWorldState world)
    {
        Matrix4x4 transform = world.RuntimeModelToSceneMatrix;
        double x = Math.Abs(transform.M11);
        double z = Math.Abs(transform.M33);
        double value = (x > 1e-6 && z > 1e-6) ? (x + z) * 0.5 : 1.0;
        return Math.Clamp(value, 0.01, 100.0);
    }

    private static InteractionSceneRenderList BuildInteractionRenderList(
        SimulationWorldState world,
        MapPresetDefinition mapPreset)
    {
        var list = new InteractionSceneRenderList();
        foreach (FacilityRegion region in mapPreset.Facilities)
        {
            string type = region.Type ?? string.Empty;
            if (type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "supply", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "fort", StringComparison.OrdinalIgnoreCase))
            {
                Color edge = ResolveTeamColor(region.Team, alpha: 216);
                Color fill = Color.FromArgb(40, edge.R, edge.G, edge.B);
                list.AddPolygon(
                    InteractionSceneRenderKind.BuffVolume,
                    string.IsNullOrWhiteSpace(region.Id) ? $"{type}:{region.Team}" : region.Id,
                    FormatFacilityLabel(type),
                    ResolveRegionPolygon(region, Math.Max(0.05f, (float)region.HeightM + 0.04f)),
                    fill,
                    edge,
                    region.Team);
            }

            if (string.Equals(type, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                (double cx, double cy) = ResolveRegionCenter(region);
                SimulationTeamState teamState = world.GetOrCreateTeamState(region.Team);
                double progress = string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase)
                    ? 1.0
                    : string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
                        ? 0.5
                        : 0.0;
                list.AddLabel(
                    InteractionSceneRenderKind.EnergyMechanism,
                    $"{region.Id}:energy",
                    $"energy {region.Team} {teamState.EnergyMechanismState}",
                    new Vector3((float)cx, (float)cy, 0.8f),
                    Color.FromArgb(235, 255, 214, 86),
                    region.Team,
                    progress);
            }
        }

        foreach (SimulationEntity entity in world.Entities)
        {
            if (!string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SimulationTeamState team = world.GetOrCreateTeamState(entity.Team);
            double progress = entity.Health <= 2000.0 || team.BaseArmorForcedOpen ? 1.0 : 0.0;
            list.AddLabel(
                InteractionSceneRenderKind.BaseArmorPanel,
                $"{entity.Id}:base_armor",
                $"base armor {entity.Team} {progress:0.0}",
                new Vector3((float)entity.X, (float)entity.Y, 1.2f),
                Color.FromArgb(235, 174, 218, 255),
                entity.Team,
                progress);
        }

        return list;
    }

    private static IReadOnlyList<LinuxSceneInteractionRenderItem> BuildSceneInteractionRenderList(
        SimulationWorldState world,
        MapPresetDefinition mapPreset,
        double metersPerModelUnit)
    {
        var items = new List<LinuxSceneInteractionRenderItem>();
        double modelUnitsPerMeter = 1.0 / Math.Max(1e-6, metersPerModelUnit);
        double worldUnitsToModelUnits = world.MetersPerWorldUnit * modelUnitsPerMeter;
        foreach (FacilityRegion region in mapPreset.Facilities)
        {
            string type = region.Type ?? string.Empty;
            bool isBuff = type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "supply", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "fort", StringComparison.OrdinalIgnoreCase);
            bool isEnergy = string.Equals(type, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
            bool isCollision = region.BlocksMovement
                || type.Contains("collision", StringComparison.OrdinalIgnoreCase)
                || type.Contains("wall", StringComparison.OrdinalIgnoreCase);
            if (!isBuff && !isEnergy && !isCollision)
            {
                continue;
            }

            (double centerX, double centerY) = ResolveVolumeCenter(region);
            double heightM = ResolveRegionHeightM(region);
            Vector3 sceneCenter = WorldToModelPoint(world, centerX, centerY, ResolveRegionCenterHeightM(region, heightM));
            double sizeXWorld = ResolveRegionSizeX(region);
            double sizeYWorld = ResolveRegionSizeY(region);
            double radiusWorld = ResolveRegionRadius(region, sizeXWorld, sizeYWorld);
            string kind = isEnergy
                ? "energy"
                : isCollision
                    ? "collision"
                    : "buff";
            SimulationTeamState teamState = world.GetOrCreateTeamState(region.Team);
            double progress = isEnergy
                ? Math.Clamp(teamState.EnergyActivatedGroupCount / 5.0, 0.0, 1.0)
                : 0.0;
            items.Add(new LinuxSceneInteractionRenderItem(
                string.IsNullOrWhiteSpace(region.Id) ? $"{kind}:{type}:{region.Team}" : region.Id,
                kind,
                type,
                region.Team,
                sceneCenter.X,
                sceneCenter.Y,
                sceneCenter.Z,
                ResolveAdditionalDouble(region, "yaw_deg", ResolveAdditionalDouble(region, "yaw", 0.0)),
                Math.Max(0.01, sizeXWorld * worldUnitsToModelUnits),
                Math.Max(0.01, sizeYWorld * worldUnitsToModelUnits),
                Math.Max(0.02, heightM * modelUnitsPerMeter),
                Math.Max(0.01, radiusWorld * worldUnitsToModelUnits),
                isEnergy ? teamState.EnergyCurrentLitMask : 0,
                isEnergy ? ResolveEnergyActivatedMask(teamState) : 0,
                isEnergy ? teamState.EnergyActivatedGroupCount : 0,
                isEnergy && teamState.EnergyLargeMechanismActive,
                false,
                progress,
                ResolveScenePoints(world, region, ResolveRegionCenterHeightM(region, heightM))));
        }

        foreach (SimulationEntity entity in world.Entities)
        {
            if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase))
            {
                SimulationTeamState team = world.GetOrCreateTeamState(entity.Team);
                bool open = entity.Health <= 2000.0 || team.BaseArmorForcedOpen;
                Vector3 scene = WorldToModelPoint(world, entity.X, entity.Y, entity.GroundHeightM + Math.Max(0.3, entity.BodyHeightM * 0.75));
                items.Add(new LinuxSceneInteractionRenderItem(
                    $"{entity.Id}:base_armor",
                    "base_armor",
                    "base_armor",
                    entity.Team,
                    scene.X,
                    scene.Y,
                    scene.Z,
                    entity.AngleDeg,
                    Math.Max(0.8, entity.BodyLengthM * modelUnitsPerMeter),
                    Math.Max(0.8, entity.BodyWidthM * modelUnitsPerMeter),
                    Math.Max(0.15, entity.BodyHeightM * 0.35 * modelUnitsPerMeter),
                    Math.Max(0.35, entity.BodyWidthM * 0.55 * modelUnitsPerMeter),
                    0,
                    open ? 0b111 : 0,
                    open ? 3 : 0,
                    false,
                    false,
                    open ? 1.0 : 0.0,
                    Array.Empty<(double X, double Y, double Z)>()));
            }
            else if (string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
            {
                Vector3 scene = WorldToModelPoint(world, entity.X, entity.Y, entity.GroundHeightM + Math.Max(0.5, entity.BodyHeightM * 0.85));
                ArmorPlateTarget outpostRing = SimulationCombatMath.GetAttackableArmorPlateTargets(
                        entity,
                        Math.Max(world.MetersPerWorldUnit, 1e-6),
                        world.GameTimeSec)
                    .FirstOrDefault(plate => plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase));
                bool rotating = !string.IsNullOrWhiteSpace(outpostRing.Id)
                    && SimulationCombatMath.IsOutpostRingEffectivelyRotating(entity, outpostRing, world.GameTimeSec);
                items.Add(new LinuxSceneInteractionRenderItem(
                    $"{entity.Id}:outpost_ring",
                    "outpost",
                    "outpost",
                    entity.Team,
                    scene.X,
                    scene.Y,
                    scene.Z,
                    SimulationCombatMath.ResolveOutpostRingYawDeg(entity, world.GameTimeSec),
                    Math.Max(0.35, entity.BodyWidthM * modelUnitsPerMeter),
                    Math.Max(0.35, entity.BodyWidthM * modelUnitsPerMeter),
                    Math.Max(0.04, entity.ArmorPlateHeightM * modelUnitsPerMeter),
                    Math.Max(0.22, entity.BodyWidthM * 0.42 * modelUnitsPerMeter),
                    rotating ? 1 : 0,
                    rotating ? 1 : 0,
                    rotating ? 1 : 0,
                    false,
                    entity.OutpostRotationStopped || !rotating,
                    rotating ? 1.0 : 0.0,
                    Array.Empty<(double X, double Y, double Z)>()));
            }
        }

        return items;
    }

    private static IReadOnlyList<Vector3> ResolveRegionPolygon(FacilityRegion region, float height)
    {
        if (string.Equals(region.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && region.Points.Count >= 3)
        {
            return region.Points.Select(point => new Vector3((float)point.X, (float)point.Y, height)).ToArray();
        }

        double minX = Math.Min(region.X1, region.X2);
        double maxX = Math.Max(region.X1, region.X2);
        double minY = Math.Min(region.Y1, region.Y2);
        double maxY = Math.Max(region.Y1, region.Y2);
        return
        [
            new Vector3((float)minX, (float)minY, height),
            new Vector3((float)maxX, (float)minY, height),
            new Vector3((float)maxX, (float)maxY, height),
            new Vector3((float)minX, (float)maxY, height),
        ];
    }

    private static Vector3 WorldToModelPoint(SimulationWorldState world, double worldX, double worldY, double heightM)
    {
        if (!Matrix4x4.Invert(world.RuntimeModelToWorldMatrix, out Matrix4x4 worldToModel))
        {
            return new Vector3((float)worldX, (float)heightM, (float)worldY);
        }

        return Vector3.Transform(new Vector3((float)worldX, (float)heightM, (float)worldY), worldToModel);
    }

    private static IReadOnlyList<(double X, double Y, double Z)> ResolveScenePoints(
        SimulationWorldState world,
        FacilityRegion region,
        double heightM)
    {
        if (region.Points.Count >= 3)
        {
            return region.Points
                .Select(point =>
                {
                    Vector3 scene = WorldToModelPoint(world, point.X, point.Y, heightM);
                    return ((double)scene.X, (double)scene.Y, (double)scene.Z);
                })
                .ToArray();
        }

        double minX = Math.Min(region.X1, region.X2);
        double maxX = Math.Max(region.X1, region.X2);
        double minY = Math.Min(region.Y1, region.Y2);
        double maxY = Math.Max(region.Y1, region.Y2);
        return new[]
        {
            ToTuple(WorldToModelPoint(world, minX, minY, heightM)),
            ToTuple(WorldToModelPoint(world, maxX, minY, heightM)),
            ToTuple(WorldToModelPoint(world, maxX, maxY, heightM)),
            ToTuple(WorldToModelPoint(world, minX, maxY, heightM)),
        };
    }

    private static (double X, double Y, double Z) ToTuple(Vector3 value)
        => (value.X, value.Y, value.Z);

    private static (double X, double Y) ResolveVolumeCenter(FacilityRegion region)
        => (
            ResolveAdditionalDouble(region, "center_x", (region.X1 + region.X2) * 0.5),
            ResolveAdditionalDouble(region, "center_y", (region.Y1 + region.Y2) * 0.5));

    private static double ResolveRegionSizeX(FacilityRegion region)
        => ResolveAdditionalDouble(region, "size_x", Math.Max(0.01, Math.Abs(region.X2 - region.X1)));

    private static double ResolveRegionSizeY(FacilityRegion region)
        => ResolveAdditionalDouble(region, "size_y", Math.Max(0.01, Math.Abs(region.Y2 - region.Y1)));

    private static double ResolveRegionRadius(FacilityRegion region, double sizeX, double sizeY)
        => ResolveAdditionalDouble(region, "radius", Math.Max(sizeX, sizeY) * 0.5);

    private static double ResolveRegionHeightM(FacilityRegion region)
        => Math.Max(0.02, ResolveAdditionalDouble(region, "size_z_m", ResolveAdditionalDouble(region, "collision_height_m", Math.Max(0.05, region.HeightM))));

    private static double ResolveRegionCenterHeightM(FacilityRegion region, double heightM)
        => ResolveAdditionalDouble(region, "center_z_m", ResolveAdditionalDouble(region, "collision_center_z_m", ResolveAdditionalDouble(region, "z_m", heightM * 0.5)));

    private static double ResolveAdditionalDouble(FacilityRegion region, string key, double fallback)
    {
        if (region.AdditionalProperties is null
            || !region.AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double number))
        {
            return number;
        }

        return element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : fallback;
    }

    private static int ResolveEnergyActivatedMask(SimulationTeamState state)
    {
        int mask = 0;
        for (int index = 0; index < Math.Clamp(state.EnergyActivatedGroupCount, 0, 5); index++)
        {
            int arm = state.EnergyActivationOrder[index];
            if (arm is >= 0 and < 5)
            {
                mask |= 1 << arm;
            }
        }

        return mask;
    }

    private static (double X, double Y) ResolveRegionCenter(FacilityRegion region)
    {
        if (region.Points.Count > 0)
        {
            return (region.Points.Average(point => point.X), region.Points.Average(point => point.Y));
        }

        return ((region.X1 + region.X2) * 0.5, (region.Y1 + region.Y2) * 0.5);
    }

    private static string FormatFacilityLabel(string type)
        => string.IsNullOrWhiteSpace(type)
            ? "facility"
            : type.Replace('_', ' ');

    private static Color ResolveTeamColor(string team, int alpha)
        => string.Equals(team, "red", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(alpha, 255, 74, 74)
            : string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(alpha, 82, 132, 255)
                : Color.FromArgb(alpha, 255, 214, 86);

    private static double NormalizeDegrees(double degrees)
    {
        double value = degrees % 360.0;
        return value < 0.0 ? value + 360.0 : value;
    }

    private static string ResolveTerrainCachePath(MapPresetDefinition mapPreset)
    {
        if (mapPreset.RuntimeGrid is null || string.IsNullOrWhiteSpace(mapPreset.RuntimeGrid.SourcePath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(mapPreset.RuntimeGrid.SourcePath))
        {
            return Path.GetFullPath(mapPreset.RuntimeGrid.SourcePath);
        }

        string baseDirectory = Path.GetDirectoryName(mapPreset.SourcePath) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, mapPreset.RuntimeGrid.SourcePath));
    }

    private readonly record struct EntityScenePose(
        Vector3 Position,
        Vector3 ChassisForward,
        Vector3 TurretForward);
}

internal enum LinuxCameraMode
{
    ThirdPerson,
    FirstPerson,
}
