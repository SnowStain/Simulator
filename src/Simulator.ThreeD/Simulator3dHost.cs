using System.Text.Json.Nodes;
using System.Text.Json;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Core.Engine;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed class Simulator3dHost
{
    public sealed record ScenarioDamageSnapshot(
        string EntityId,
        string Label,
        double LastOneSecondDamage,
        double TotalDamage,
        double SecondsSinceLastHit);

    public sealed record UnitTestEnergySnapshot(
        string State,
        bool LargeMode,
        int ActivatedDisks,
        int LastRingScore,
        double AverageRingScore);

    public sealed record DuelRoundStatSnapshot(
        double DamageOutput,
        int Shots,
        int Hits,
        double HitRate,
        double Health,
        double MaxHealth,
        bool Destroyed);

    public sealed record DuelRoundPairSnapshot(
        int RoundIndex,
        DuelRoundStatSnapshot FriendlyStats,
        DuelRoundStatSnapshot EnemyStats);

    public sealed record DuelMatchSnapshot(
        int RoundLimit,
        int RoundsCompleted,
        int RedScore,
        int BlueScore,
        bool Finished,
        string ResultLabel,
        bool WaitingForNextRound,
        double RoundRestartRemainingSec,
        DuelRoundStatSnapshot FriendlyStats,
        DuelRoundStatSnapshot EnemyStats,
        DuelRoundStatSnapshot FriendlyTotalStats,
        DuelRoundStatSnapshot EnemyTotalStats,
        IReadOnlyList<DuelRoundPairSnapshot> RoundStats,
        bool FriendlyDestroyedLastRound,
        bool EnemyDestroyedLastRound);

    internal sealed record PreparedMatchWorldState(SimulationWorldState World);
    internal sealed record PreparedLobbyWorldState(
        SimulationWorldState World,
        MapPresetDefinition MapPreset,
        AppearanceProfileCatalog AppearanceCatalog,
        RuntimeGridData? RuntimeGrid,
        string? RuntimeGridWarning,
        RuleSet Rules,
        DecisionDeploymentConfig DecisionDeploymentConfig);

    private readonly record struct LobbyWorldBuildSnapshot(
        string MatchMode,
        string ActiveMapPreset,
        string InfantryMode,
        string HeroPerformanceMode,
        string InfantryDurabilityMode,
        string InfantryWeaponMode,
        string SentryControlMode,
        string SentryStance,
        double AutoAimAccuracyScale);

    private sealed class ScenarioDamageAccumulator
    {
        public Queue<(double TimeSec, double Damage)> RecentHits { get; } = new();

        public double TotalDamage { get; set; }

        public double LastHitTimeSec { get; set; } = double.NegativeInfinity;
    }

    private static readonly string[] SingleUnitEntityKeys =
    {
        "robot_1",
        "robot_2",
        "robot_3",
        "robot_4",
        "robot_7",
    };

    private static readonly string[] UnitTestTrackedDamageEntityIds =
    {
        "blue_outpost",
        "blue_base",
    };

    private const string DuelEnemyEntityId = "blue_robot_7";
    private const double DuelRoundRestartDelaySec = 10.0;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<DecisionSpec>> DecisionSpecsByRole =
        new Dictionary<string, IReadOnlyList<DecisionSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["hero"] = new[]
            {
                new DecisionSpec("hero_hold", "驻守据点"),
                new DecisionSpec("hero_push", "推进压制"),
                new DecisionSpec("hero_flank", "侧翼突击"),
                new DecisionSpec("hero_withdraw", "战术后撤"),
            },
            ["engineer"] = new[]
            {
                new DecisionSpec("eng_supply", "优先补给"),
                new DecisionSpec("eng_repair", "维修支援"),
                new DecisionSpec("eng_mine", "资源采集"),
                new DecisionSpec("eng_fallback", "回撤保护"),
            },
            ["infantry"] = new[]
            {
                new DecisionSpec("inf_hold", "保持火力线"),
                new DecisionSpec("inf_pressure", "压迫推进"),
                new DecisionSpec("inf_hunt", "追击目标"),
                new DecisionSpec("inf_reposition", "机动转点"),
            },
            ["sentry"] = new[]
            {
                new DecisionSpec("sen_anchor", "阵地锚定"),
                new DecisionSpec("sen_cover", "侧向掩护"),
                new DecisionSpec("sen_pulse", "脉冲清线"),
                new DecisionSpec("sen_fallback", "退守补位"),
            },
        };

    private readonly ProjectLayout _layout;
    private readonly ConfigurationService _configurationService;
    private readonly MapPresetService _mapPresetService;
    private readonly RuleSetLoader _ruleSetLoader;
    private readonly SimulationBootstrapService _bootstrapService;
    private readonly RuntimeGridLoader _runtimeGridLoader;
    private readonly BepuProjectileObstacleBackend _bepuProjectileObstacleBackend = new();
    private readonly string _primaryConfigPath;
    private readonly string? _appearancePathOverride;
    private readonly JsonObject _config;
    private DateTime _lastDecisionConfigProbeUtc;
    private DateTime _decisionConfigLastWriteUtc;
    private long _lastSimulationPerfLogTicks;
    private RuleSet _rules;
    private RuleSimulationService _simulationService;
    private TerrainMotionService _terrainMotionService;
    private AppearanceProfileCatalog _appearanceCatalog;
    private RuntimeGridData? _runtimeGrid;
    private string? _runtimeGridWarning;
    private string? _selectedEntityId;
    private DecisionDeploymentConfig _decisionDeploymentConfig = DecisionDeploymentConfig.CreateDefault();
    private string _matchMode = "full";
    private string _infantryMode = "full";
    private string _heroPerformanceMode = "ranged_priority";
    private string _infantryDurabilityMode = "hp_priority";
    private string _infantryWeaponMode = "cooling_priority";
    private string _sentryControlMode = "full_auto";
    private string _sentryStance = "attack";
    private double _autoAimAccuracyScale = 1.0;
    private bool _aiEnabled = true;
    private bool _solidProjectileRendering = true;
    private string _projectilePhysicsBackend = "native";
    private string _singleUnitTestTeam = "red";
    private string _singleUnitTestEntityKey = "robot_1";
    private readonly string? _previewRoleKey;
    private readonly string? _previewSubtypeKey;
    private bool _requiresDeferredLobbyBootstrap;
    private readonly Dictionary<string, ScenarioDamageAccumulator> _unitTestDamageByEntityId = new(StringComparer.OrdinalIgnoreCase);
    private bool _unitTestEnergyForceLarge;
    private int _duelRoundLimit = 5;
    private int _duelRedScore;
    private int _duelBlueScore;
    private int _duelRoundsCompleted;
    private bool _duelFinished;
    private string _duelResultLabel = string.Empty;
    private double _duelRoundRestartRemainingSec;
    private double _duelCurrentFriendlyDamage;
    private double _duelCurrentEnemyDamage;
    private int _duelCurrentFriendlyShots;
    private int _duelCurrentEnemyShots;
    private int _duelCurrentFriendlyHits;
    private int _duelCurrentEnemyHits;
    private double _duelTotalFriendlyDamage;
    private double _duelTotalEnemyDamage;
    private int _duelTotalFriendlyShots;
    private int _duelTotalEnemyShots;
    private int _duelTotalFriendlyHits;
    private int _duelTotalEnemyHits;
    private DuelRoundStatSnapshot _duelLastFriendlyStats = CreateEmptyDuelRoundStats();
    private DuelRoundStatSnapshot _duelLastEnemyStats = CreateEmptyDuelRoundStats();
    private DuelRoundStatSnapshot _duelTotalFriendlyStats = CreateEmptyDuelRoundStats();
    private DuelRoundStatSnapshot _duelTotalEnemyStats = CreateEmptyDuelRoundStats();
    private readonly List<DuelRoundPairSnapshot> _duelRoundHistory = new();
    private bool _duelLastRoundFriendlyDestroyed;
    private bool _duelLastRoundEnemyDestroyed;

    public Simulator3dHost(Simulator3dOptions options)
    {
        _layout = ProjectLayout.Discover();
        _configurationService = new ConfigurationService();
        _mapPresetService = new MapPresetService();
        _ruleSetLoader = new RuleSetLoader();
        _bootstrapService = new SimulationBootstrapService();
        _runtimeGridLoader = new RuntimeGridLoader();

        _primaryConfigPath = _configurationService.ResolvePrimaryConfigPath(_layout);
        _appearancePathOverride = string.IsNullOrWhiteSpace(options.AppearancePath)
            ? null
            : Path.GetFullPath(options.AppearancePath);
        _config = _configurationService.LoadConfig(_primaryConfigPath);
        _decisionConfigLastWriteUtc = ReadLastWriteTimeUtc(_primaryConfigPath);
        JsonObject simulatorConfig = ConfigurationService.EnsureObject(_config, "simulator");
        _decisionDeploymentConfig = DecisionDeploymentConfig.LoadFromConfig(_config);

        AvailableMapPresets = DiscoverMapPresets(_layout);
        ActiveRendererMode = string.IsNullOrWhiteSpace(options.RendererMode)
            ? ResolveRendererMode(_config, "gpu")
            : Simulator3dOptions.NormalizeRendererMode(options.RendererMode);
        _matchMode = string.IsNullOrWhiteSpace(options.MatchMode)
            ? "full"
            : Simulator3dOptions.NormalizeMatchMode(options.MatchMode);
        _infantryMode = NormalizeInfantryMode(simulatorConfig["sim3d_infantry_mode"]?.ToString());
        _heroPerformanceMode = RuleSet.NormalizeHeroMode(simulatorConfig["sim3d_hero_performance_mode"]?.ToString());
        _infantryDurabilityMode = RuleSet.NormalizeInfantryDurabilityMode(simulatorConfig["sim3d_infantry_durability_mode"]?.ToString());
        _infantryWeaponMode = RuleSet.NormalizeInfantryWeaponMode(simulatorConfig["sim3d_infantry_weapon_mode"]?.ToString());
        _sentryControlMode = RuleSet.NormalizeSentryControlMode(simulatorConfig["sim3d_sentry_control_mode"]?.ToString());
        _sentryStance = RuleSet.NormalizeSentryStance(simulatorConfig["sim3d_sentry_stance"]?.ToString());
        _autoAimAccuracyScale = Math.Clamp(ReadDouble(simulatorConfig["sim3d_autoaim_accuracy_scale"], 1.0), 0.05, 1.0);
        _aiEnabled = ReadBoolean(simulatorConfig["sim3d_ai_enabled"], fallback: true);
        _solidProjectileRendering = ReadBoolean(simulatorConfig["sim3d_projectile_entity_rendering"], fallback: true);
        _projectilePhysicsBackend = NormalizeProjectilePhysicsBackend(simulatorConfig["sim3d_projectile_physics_backend"]?.ToString());
        _unitTestEnergyForceLarge = ReadBoolean(simulatorConfig["sim3d_unit_test_energy_force_large"], fallback: false);
        _duelRoundLimit = Math.Clamp((int)Math.Round(ReadDouble(simulatorConfig["sim3d_duel_round_limit"], 5.0)), 1, 99);
        _singleUnitTestTeam = Simulator3dOptions.NormalizeTeam(options.SingleUnitTestTeam ?? simulatorConfig["single_unit_test_team"]?.ToString());
        _singleUnitTestEntityKey = Simulator3dOptions.NormalizeSingleUnitEntityKey(options.SingleUnitTestEntityKey ?? simulatorConfig["single_unit_test_entity_key"]?.ToString());
        _previewRoleKey = Simulator3dOptions.NormalizePreviewRoleKey(options.PreviewRoleKey);
        _previewSubtypeKey = Simulator3dOptions.NormalizePreviewSubtypeKey(options.PreviewSubtypeKey);
        ActiveMapPreset = ResolvePreset(options.MapPreset);
        DeltaTimeSec = options.DeltaTimeSec;
        SelectedTeam = Simulator3dOptions.NormalizeTeam(options.SelectedTeam ?? simulatorConfig["sim3d_selected_team"]?.ToString());
        _selectedEntityId = string.IsNullOrWhiteSpace(options.SelectedEntityId)
            ? simulatorConfig["sim3d_selected_entity_id"]?.ToString()
            : options.SelectedEntityId;
        RicochetEnabled = options.RicochetEnabled
            ?? ReadBoolean(simulatorConfig["player_projectile_ricochet_enabled"], fallback: true);

        MapPreset = LoadMapPresetForCurrentMode();
        _rules = _ruleSetLoader.LoadFromConfig(_config);
        _simulationService = BuildSimulationService(_rules);
        _terrainMotionService = CreateTerrainMotionService();
        _appearanceCatalog = AppearanceProfileCatalog.Load(ResolveAppearancePath());
        if (ShouldDeferInitialLobbyBootstrap(options))
        {
            _requiresDeferredLobbyBootstrap = true;
            _runtimeGrid = null;
            _runtimeGridWarning = "startup_async_pending";
            World = CreatePlaceholderWorld(MapPreset);
        }
        else
        {
            _runtimeGrid = _runtimeGridLoader.TryLoad(MapPreset, out _runtimeGridWarning);
            World = BuildWorldForCurrentMode(MapPreset, _rules, _appearanceCatalog, CaptureLobbyWorldBuildSnapshot());
        }

        _unitTestDamageByEntityId.Clear();
        EnsureSingleUnitTestFocus();
        ApplyMapComponentTestRuntimeFilters();
        ApplySingleUnitTestRuntimeFilters();
        EnsureSelectedEntity();
        PersistSimulatorSettings();
    }

    public IReadOnlyList<string> AvailableMapPresets { get; }

    public string ProjectRootPath => _layout.RootPath;

    public string DecisionProjectPath => _layout.ResolvePath("src", "Simulator.Decision", "Simulator.Decision.csproj");

    public string ActiveRendererMode { get; private set; }

    public string ActiveMapPreset { get; private set; }

    public double DeltaTimeSec { get; }

    public double GameDurationSec => _rules.GameDurationSec;

    public double AutoAimMaxDistanceM => _rules.Combat.AutoAimMaxDistanceM;

    public double CombatFireRateHz => _rules.Combat.FireRateHz;

    public string MatchMode => _matchMode;

    public string InfantryMode => _infantryMode;

    public string HeroPerformanceMode => _heroPerformanceMode;

    public string InfantryDurabilityMode => _infantryDurabilityMode;

    public string InfantryWeaponMode => _infantryWeaponMode;

    public string SentryControlMode => _sentryControlMode;

    public string SentryStance => _sentryStance;

    public double AutoAimAccuracyScale => _autoAimAccuracyScale;

    public bool AiEnabled => _aiEnabled;

    public bool SolidProjectileRendering => _solidProjectileRendering;

    public string ProjectilePhysicsBackend => _projectilePhysicsBackend;

    public bool IsSingleUnitTestMode => string.Equals(_matchMode, "single_unit_test", StringComparison.OrdinalIgnoreCase);

    public bool IsDuelMode => string.Equals(_matchMode, "duel_1v1", StringComparison.OrdinalIgnoreCase);

    public bool IsUnitTestMode => string.Equals(_matchMode, "unit_test", StringComparison.OrdinalIgnoreCase);

    public bool IsMapComponentTestMode => string.Equals(_matchMode, "map_component_test", StringComparison.OrdinalIgnoreCase);

    public bool IsFocusSandboxMode => IsSingleUnitTestMode || IsDuelMode || IsUnitTestMode;

    public string SingleUnitTestTeam => _singleUnitTestTeam;

    public string SingleUnitTestEntityKey => _singleUnitTestEntityKey;

    public string SingleUnitTestFocusId => $"{_singleUnitTestTeam}_{_singleUnitTestEntityKey}";

    public bool UnitTestEnergyForceLarge => _unitTestEnergyForceLarge;

    public int DuelRoundLimit => _duelRoundLimit;

    public bool IsDuelFinished => _duelFinished;

    public string SelectedTeam { get; private set; }

    public bool RicochetEnabled { get; private set; }

    public MapPresetDefinition MapPreset { get; private set; }

    public SimulationWorldState World { get; private set; }

    public RuntimeGridData? RuntimeGrid => _runtimeGrid;

    public string? RuntimeGridWarning => _runtimeGridWarning;

    internal bool RequiresDeferredLobbyBootstrap => _requiresDeferredLobbyBootstrap;

    private TerrainMotionService CreateTerrainMotionService()
        => new(
            _rules,
            _decisionDeploymentConfig,
            MapPreset.Facilities,
            enableFieldCompositeInteractionTest: !IsFocusSandboxMode);

    public AppearanceProfileCatalog AppearanceCatalog => _appearanceCatalog;

    public string InfantryAppearanceSubtype =>
        string.Equals(_infantryMode, "balance", StringComparison.OrdinalIgnoreCase)
            ? "balance_legged"
            : "omni_wheel";

    public SimulationRunReport? LastReport { get; private set; }

    public string? ResolveMapImagePath()
    {
        return TerrainSurfaceMapSupport.ResolveBaseColorBitmapPath(MapPreset);
    }

    public RobotAppearanceProfile ResolveAppearanceProfile(SimulationEntity entity)
    {
        string? subtype = entity.RoleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase)
            ? entity.ChassisSubtype
            : null;
        return TryResolveFacilityAppearanceProfile(entity, MapPreset, _appearanceCatalog)
            ?? _appearanceCatalog.Resolve(entity.RoleKey, subtype);
    }

    public ResolvedRoleProfile ResolveRuntimeProfile(SimulationEntity entity)
        => _rules.ResolveRuntimeProfile(entity);

    public IReadOnlyDictionary<string, string> RoleDeploymentModes =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hero"] = _decisionDeploymentConfig.ResolveMode("hero"),
            ["engineer"] = _decisionDeploymentConfig.ResolveMode("engineer"),
            ["infantry"] = _decisionDeploymentConfig.ResolveMode("infantry"),
            ["sentry"] = _decisionDeploymentConfig.ResolveMode("sentry"),
        };

    public SimulationEntity? SingleUnitTestFocusEntity =>
        World.Entities.FirstOrDefault(entity => string.Equals(entity.Id, SingleUnitTestFocusId, StringComparison.OrdinalIgnoreCase));

    public SimulationEntity? SelectedEntity =>
        World.Entities.FirstOrDefault(entity => string.Equals(entity.Id, _selectedEntityId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ScenarioDamageSnapshot> GetUnitTestDamageSnapshots()
    {
        if (!IsUnitTestMode)
        {
            return Array.Empty<ScenarioDamageSnapshot>();
        }

        double nowSec = World.GameTimeSec;
        return UnitTestTrackedDamageEntityIds
            .Select(entityId =>
            {
                ScenarioDamageAccumulator accumulator = GetOrCreateUnitTestDamageAccumulator(entityId);
                return new ScenarioDamageSnapshot(
                    entityId,
                    ResolveScenarioDamageLabel(entityId),
                    ResolveRecentDamage(accumulator, nowSec),
                    accumulator.TotalDamage,
                    double.IsNegativeInfinity(accumulator.LastHitTimeSec)
                        ? double.PositiveInfinity
                        : Math.Max(0.0, nowSec - accumulator.LastHitTimeSec));
            })
            .ToArray();
    }

    public UnitTestEnergySnapshot GetUnitTestEnergySnapshot()
    {
        SimulationTeamState teamState = World.GetOrCreateTeamState("red");
        double averageRingScore = teamState.EnergyHitRingCount > 0
            ? teamState.EnergyHitRingSum / Math.Max(1.0, teamState.EnergyHitRingCount)
            : 0.0;
        return new UnitTestEnergySnapshot(
            teamState.EnergyMechanismState,
            teamState.EnergyLargeMechanismActive || _unitTestEnergyForceLarge,
            teamState.EnergyActivatedGroupCount,
            teamState.EnergyLastRingScore,
            averageRingScore);
    }

    public DuelMatchSnapshot GetDuelMatchSnapshot()
    {
        return new DuelMatchSnapshot(
            _duelRoundLimit,
            _duelRoundsCompleted,
            _duelRedScore,
            _duelBlueScore,
            _duelFinished,
            string.IsNullOrWhiteSpace(_duelResultLabel) ? ResolveDuelResultLabel() : _duelResultLabel,
            _duelRoundRestartRemainingSec > 1e-6,
            Math.Max(0.0, _duelRoundRestartRemainingSec),
            _duelLastFriendlyStats,
            _duelLastEnemyStats,
            _duelTotalFriendlyStats,
            _duelTotalEnemyStats,
            _duelRoundHistory.ToArray(),
            _duelLastRoundFriendlyDestroyed,
            _duelLastRoundEnemyDestroyed);
    }

    private ScenarioDamageAccumulator GetOrCreateUnitTestDamageAccumulator(string entityId)
    {
        if (!_unitTestDamageByEntityId.TryGetValue(entityId, out ScenarioDamageAccumulator? accumulator))
        {
            accumulator = new ScenarioDamageAccumulator();
            _unitTestDamageByEntityId[entityId] = accumulator;
        }

        return accumulator;
    }

    private static string ResolveScenarioDamageLabel(string entityId)
    {
        return entityId switch
        {
            "blue_outpost" => "前哨站",
            "blue_base" => "基地",
            _ => entityId,
        };
    }

    private static double ResolveRecentDamage(ScenarioDamageAccumulator accumulator, double nowSec)
    {
        PruneRecentDamageSamples(accumulator, nowSec, windowSec: 1.0);
        return accumulator.RecentHits.Sum(sample => sample.Damage);
    }

    private void ResetDuelMatchState()
    {
        _duelRedScore = 0;
        _duelBlueScore = 0;
        _duelRoundsCompleted = 0;
        _duelFinished = false;
        _duelResultLabel = string.Empty;
        _duelRoundRestartRemainingSec = 0.0;
        ResetDuelCurrentRoundStats();
        _duelLastFriendlyStats = CreateEmptyDuelRoundStats();
        _duelLastEnemyStats = CreateEmptyDuelRoundStats();
        _duelTotalFriendlyDamage = 0.0;
        _duelTotalEnemyDamage = 0.0;
        _duelTotalFriendlyShots = 0;
        _duelTotalEnemyShots = 0;
        _duelTotalFriendlyHits = 0;
        _duelTotalEnemyHits = 0;
        _duelTotalFriendlyStats = CreateEmptyDuelRoundStats();
        _duelTotalEnemyStats = CreateEmptyDuelRoundStats();
        _duelRoundHistory.Clear();
        _duelLastRoundFriendlyDestroyed = false;
        _duelLastRoundEnemyDestroyed = false;
    }

    private static DuelRoundStatSnapshot CreateEmptyDuelRoundStats()
        => new(0.0, 0, 0, 0.0, 0.0, 1.0, false);

    private void ResetDuelCurrentRoundStats()
    {
        _duelCurrentFriendlyDamage = 0.0;
        _duelCurrentEnemyDamage = 0.0;
        _duelCurrentFriendlyShots = 0;
        _duelCurrentEnemyShots = 0;
        _duelCurrentFriendlyHits = 0;
        _duelCurrentEnemyHits = 0;
    }

    private string ResolveDuelResultLabel()
    {
        if (_duelRedScore == _duelBlueScore)
        {
            return $"平局 {_duelRedScore}:{_duelBlueScore}";
        }

        return _duelRedScore > _duelBlueScore
            ? $"红方胜 {_duelRedScore}:{_duelBlueScore}"
            : $"蓝方胜 {_duelRedScore}:{_duelBlueScore}";
    }

    private static void PruneRecentDamageSamples(ScenarioDamageAccumulator accumulator, double nowSec, double windowSec)
    {
        double threshold = nowSec - Math.Max(0.0, windowSec);
        while (accumulator.RecentHits.Count > 0
               && accumulator.RecentHits.Peek().TimeSec < threshold)
        {
            accumulator.RecentHits.Dequeue();
        }
    }

    public IReadOnlyList<DecisionSpec> GetSingleUnitTestDecisionSpecs()
    {
        SimulationEntity? focus = SingleUnitTestFocusEntity;
        if (focus is null)
        {
            return Array.Empty<DecisionSpec>();
        }

        string roleKey = ResolveDecisionRoleKey(focus);
        if (DecisionSpecsByRole.TryGetValue(roleKey, out IReadOnlyList<DecisionSpec>? specs))
        {
            return specs;
        }

        return Array.Empty<DecisionSpec>();
    }

    public IReadOnlyList<DecisionSpec> GetSingleUnitTestNextDecisionSpecs()
    {
        IReadOnlyList<DecisionSpec> specs = GetSingleUnitTestDecisionSpecs();
        if (specs.Count == 0)
        {
            return specs;
        }

        SimulationEntity? focus = SingleUnitTestFocusEntity;
        string anchor = focus?.TestForcedDecisionId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(anchor))
        {
            anchor = focus?.AiDecisionSelected ?? string.Empty;
        }

        int anchorIndex = -1;
        if (!string.IsNullOrWhiteSpace(anchor))
        {
            for (int index = 0; index < specs.Count; index++)
            {
                if (string.Equals(specs[index].Id, anchor, StringComparison.OrdinalIgnoreCase))
                {
                    anchorIndex = index;
                    break;
                }
            }
        }

        if (anchorIndex < 0)
        {
            return specs.Take(3).ToArray();
        }

        var next = new List<DecisionSpec>(capacity: 3);
        for (int offset = 1; offset <= specs.Count && next.Count < 3; offset++)
        {
            int index = (anchorIndex + offset) % specs.Count;
            next.Add(specs[index]);
        }

        return next;
    }

    public bool SetSingleUnitTestDecision(string decisionId)
    {
        SimulationEntity? focus = SingleUnitTestFocusEntity;
        if (focus is null)
        {
            return false;
        }

        string normalized = (decisionId ?? string.Empty).Trim();
        IReadOnlyList<DecisionSpec> specs = GetSingleUnitTestDecisionSpecs();
        if (!string.IsNullOrWhiteSpace(normalized)
            && !specs.Any(spec => string.Equals(spec.Id, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        ClearAllForcedSingleUnitDecisions();
        focus.TestForcedDecisionId = normalized;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        DecisionSpec selected = specs.First(spec => string.Equals(spec.Id, normalized, StringComparison.OrdinalIgnoreCase));
        focus.AiDecisionSelected = selected.Id;
        focus.AiDecision = selected.Label;
        return true;
    }

    public string GetSingleUnitCurrentDecisionLabel()
    {
        SimulationEntity? focus = SingleUnitTestFocusEntity;
        if (focus is null)
        {
            return "未找到";
        }

        return string.IsNullOrWhiteSpace(focus.AiDecision) ? "待机" : focus.AiDecision;
    }

    private static string ResolveDecisionRoleKey(SimulationEntity entity)
    {
        if (string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            return "sentry";
        }

        string role = (entity.RoleKey ?? string.Empty).Trim().ToLowerInvariant();
        return role switch
        {
            "hero" => "hero",
            "engineer" => "engineer",
            "infantry" => "infantry",
            _ => "infantry",
        };
    }

    public void SetRendererMode(string mode)
    {
        string normalized = Simulator3dOptions.NormalizeRendererMode(mode);
        if (string.Equals(normalized, ActiveRendererMode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ActiveRendererMode = normalized;
        PersistSimulatorSettings();
    }

    public void ToggleMatchMode()
    {
        SetMatchMode(IsSingleUnitTestMode ? "full" : "single_unit_test");
    }

    public bool SetMatchMode(string mode)
    {
        string normalized = Simulator3dOptions.NormalizeMatchMode(mode);
        if (string.Equals(_matchMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _matchMode = normalized;
        _unitTestDamageByEntityId.Clear();
        ResetDuelMatchState();
        EnsureSingleUnitTestFocus();
        RebuildWorld();
        PersistSimulatorSettings();
        return true;
    }

    public bool SetMatchModeDeferred(string mode)
    {
        string normalized = Simulator3dOptions.NormalizeMatchMode(mode);
        if (!string.Equals(normalized, "full", StringComparison.OrdinalIgnoreCase))
        {
            return SetMatchMode(normalized);
        }

        if (string.Equals(_matchMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _matchMode = normalized;
        _unitTestDamageByEntityId.Clear();
        ResetDuelMatchState();
        EnsureSingleUnitTestFocus();
        RebuildDeferredLobbyPlaceholderWorld();
        PersistSimulatorSettings();
        return true;
    }

    public bool SetInfantryMode(string mode, bool rebuildWorld = true)
    {
        string normalized = NormalizeInfantryMode(mode);
        if (string.Equals(_infantryMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _infantryMode = normalized;
        if (rebuildWorld)
        {
            RebuildWorld();
        }

        PersistSimulatorSettings();
        return true;
    }

    public bool SetHeroPerformanceMode(string mode, bool rebuildWorld = true)
    {
        string normalized = RuleSet.NormalizeHeroMode(mode);
        if (string.Equals(_heroPerformanceMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _heroPerformanceMode = normalized;
        if (rebuildWorld)
        {
            RebuildWorld();
        }

        PersistSimulatorSettings();
        return true;
    }

    public bool SetInfantryDurabilityMode(string mode, bool rebuildWorld = true)
    {
        string normalized = RuleSet.NormalizeInfantryDurabilityMode(mode);
        if (string.Equals(_infantryDurabilityMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _infantryDurabilityMode = normalized;
        if (rebuildWorld)
        {
            RebuildWorld();
        }

        PersistSimulatorSettings();
        return true;
    }

    public bool SetInfantryWeaponMode(string mode, bool rebuildWorld = true)
    {
        string normalized = RuleSet.NormalizeInfantryWeaponMode(mode);
        if (string.Equals(_infantryWeaponMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _infantryWeaponMode = normalized;
        if (rebuildWorld)
        {
            RebuildWorld();
        }

        PersistSimulatorSettings();
        return true;
    }

    public bool SetSentryControlMode(string mode, bool rebuildWorld = true)
    {
        string normalized = RuleSet.NormalizeSentryControlMode(mode);
        if (string.Equals(_sentryControlMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _sentryControlMode = normalized;
        if (rebuildWorld)
        {
            RebuildWorld();
        }

        PersistSimulatorSettings();
        return true;
    }

    public bool SetSentryStance(string stance, bool rebuildWorld = true)
    {
        string normalized = RuleSet.NormalizeSentryStance(stance);
        if (string.Equals(_sentryStance, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _sentryStance = normalized;
        if (rebuildWorld)
        {
            RebuildWorld();
        }

        PersistSimulatorSettings();
        return true;
    }

    public bool SetAutoAimAccuracyScale(double scale)
    {
        double normalized = Math.Clamp(scale, 0.05, 1.0);
        if (Math.Abs(_autoAimAccuracyScale - normalized) <= 1e-6)
        {
            return false;
        }

        _autoAimAccuracyScale = normalized;
        ApplyConfiguredRoleProfilesToWorld(resetHealth: false);
        PersistSimulatorSettings();
        return true;
    }

    public bool SetAiEnabled(bool enabled)
    {
        if (_aiEnabled == enabled)
        {
            return false;
        }

        _aiEnabled = enabled;
        _simulationService = BuildSimulationService(_rules);
        if (!enabled)
        {
            foreach (SimulationEntity entity in World.Entities.Where(entity => !entity.IsPlayerControlled))
            {
                entity.IsFireCommandActive = false;
                entity.AutoAimRequested = false;
                entity.AutoAimLocked = false;
                entity.AutoAimTargetId = null;
                entity.AutoAimPlateId = null;
            }
        }

        PersistSimulatorSettings();
        return true;
    }

    public bool SetUnitTestEnergyForceLarge(bool enabled)
    {
        if (_unitTestEnergyForceLarge == enabled)
        {
            return false;
        }

        _unitTestEnergyForceLarge = enabled;
        if (IsUnitTestMode)
        {
            SimulationTeamState teamState = World.GetOrCreateTeamState("red");
            ConfigureUnitTestEnergyTeamState(teamState);
            teamState.EnergyTestForceLarge = enabled;
        }

        PersistSimulatorSettings();
        return true;
    }

    public bool SetDuelRoundLimit(int roundLimit)
    {
        int normalized = Math.Clamp(roundLimit, 1, 99);
        if (_duelRoundLimit == normalized)
        {
            return false;
        }

        _duelRoundLimit = normalized;
        PersistSimulatorSettings();
        return true;
    }

    public bool SetSolidProjectileRendering(bool enabled)
    {
        if (_solidProjectileRendering == enabled)
        {
            return false;
        }

        _solidProjectileRendering = enabled;
        PersistSimulatorSettings();
        return true;
    }

    public bool SetProjectilePhysicsBackend(string backend)
    {
        string normalized = NormalizeProjectilePhysicsBackend(backend);
        if (string.Equals(_projectilePhysicsBackend, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _projectilePhysicsBackend = normalized;
        _simulationService = BuildSimulationService(_rules);
        PersistSimulatorSettings();
        return true;
    }

    public bool SetSingleUnitTestFocus(string? team = null, string? entityKey = null)
    {
        bool changed = false;
        if (!string.IsNullOrWhiteSpace(team))
        {
            string normalizedTeam = NormalizeFocusSandboxTeam(team);
            if (!string.Equals(_singleUnitTestTeam, normalizedTeam, StringComparison.OrdinalIgnoreCase))
            {
                _singleUnitTestTeam = normalizedTeam;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(entityKey))
        {
            string normalizedEntity = Simulator3dOptions.NormalizeSingleUnitEntityKey(entityKey);
            if (IsDuelMode && string.Equals(normalizedEntity, "robot_2", StringComparison.OrdinalIgnoreCase))
            {
                normalizedEntity = "robot_3";
            }

            if (!string.Equals(_singleUnitTestEntityKey, normalizedEntity, StringComparison.OrdinalIgnoreCase))
            {
                _singleUnitTestEntityKey = normalizedEntity;
                changed = true;
            }
        }

        if (!changed)
        {
            return false;
        }

        EnsureSingleUnitTestFocus();
        RefreshFocusSandboxScenario(refillRobots: true);
        ApplySingleUnitTestRuntimeFilters();
        EnsureSelectedEntity();
        PersistSimulatorSettings();
        return true;
    }

    public void CycleSingleUnitTestEntityKey(int direction)
    {
        if (SingleUnitEntityKeys.Length == 0)
        {
            return;
        }

        int currentIndex = Array.FindIndex(
            SingleUnitEntityKeys,
            key => string.Equals(key, _singleUnitTestEntityKey, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int nextIndex = (currentIndex + direction) % SingleUnitEntityKeys.Length;
        if (nextIndex < 0)
        {
            nextIndex += SingleUnitEntityKeys.Length;
        }

        SetSingleUnitTestFocus(entityKey: SingleUnitEntityKeys[nextIndex]);
    }

    public void CycleMapPreset(int direction)
    {
        if (AvailableMapPresets.Count == 0)
        {
            return;
        }

        int currentIndex = FindPresetIndex(ActiveMapPreset);
        int nextIndex = (currentIndex + direction) % AvailableMapPresets.Count;
        if (nextIndex < 0)
        {
            nextIndex += AvailableMapPresets.Count;
        }

        SetMapPreset(AvailableMapPresets[nextIndex]);
    }

    public void SetMapPreset(string preset)
    {
        string? canonicalPreset = AvailableMapPresets.FirstOrDefault(item =>
            string.Equals(item, preset, StringComparison.OrdinalIgnoreCase));
        if (canonicalPreset is null)
        {
            return;
        }

        if (string.Equals(ActiveMapPreset, canonicalPreset, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ActiveMapPreset = canonicalPreset;
        RebuildWorld();
        PersistSimulatorSettings();
    }

    public void ResetWorld()
    {
        RebuildWorld();
    }

    public bool StartNextDuelRoundNow()
    {
        if (!IsDuelMode || _duelFinished || _duelRoundRestartRemainingSec <= 1e-6)
        {
            return false;
        }

        _duelRoundRestartRemainingSec = 0.0;
        World.Projectiles.Clear();
        World.SurfaceLastAcceptedHitTimes.Clear();
        LastReport = null;
        ArrangeDuelScenarioEntities(World, _rules, refillRobots: true);
        ApplySingleUnitTestRuntimeFilters();
        EnsureSelectedEntity();
        return true;
    }

    public void PrepareScenarioMatchStart()
    {
        if (!IsDuelMode)
        {
            return;
        }

        ResetDuelMatchState();
    }

    internal Task<PreparedMatchWorldState> PrepareMatchWorldAsync()
        => Task.Run(PrepareMatchWorldState);

    internal Task<PreparedLobbyWorldState> PrepareLobbyWorldAsync()
    {
        LobbyWorldBuildSnapshot snapshot = CaptureLobbyWorldBuildSnapshot();
        return Task.Run(() => PrepareLobbyWorldState(snapshot));
    }

    internal void ApplyPreparedMatchWorld(PreparedMatchWorldState prepared)
    {
        World = prepared.World;
        LastReport = null;
        _unitTestDamageByEntityId.Clear();
        EnsureSingleUnitTestFocus();
        ApplyMapComponentTestRuntimeFilters();
        ApplySingleUnitTestRuntimeFilters();
        EnsureSelectedEntity();
    }

    internal void ApplyPreparedLobbyWorld(PreparedLobbyWorldState prepared)
    {
        string? previousSelection = _selectedEntityId;
        string previousTeam = SelectedTeam;
        _requiresDeferredLobbyBootstrap = false;
        MapPreset = prepared.MapPreset;
        _appearanceCatalog = prepared.AppearanceCatalog;
        _runtimeGrid = prepared.RuntimeGrid;
        _runtimeGridWarning = prepared.RuntimeGridWarning;
        _rules = prepared.Rules;
        _decisionDeploymentConfig = prepared.DecisionDeploymentConfig;
        _simulationService = BuildSimulationService(_rules);
        _terrainMotionService = CreateTerrainMotionService();
        World = prepared.World;
        _selectedEntityId = previousSelection;
        SelectedTeam = previousTeam;
        LastReport = null;
        _unitTestDamageByEntityId.Clear();
        EnsureSingleUnitTestFocus();
        ApplyMapComponentTestRuntimeFilters();
        ApplySingleUnitTestRuntimeFilters();
        EnsureSelectedEntity();
    }

    public void Step(PlayerControlState? playerControlState = null)
    {
        if (IsDuelMode && _duelFinished)
        {
            return;
        }

        bool duelRoundRestartActive = IsDuelMode && _duelRoundRestartRemainingSec > 1e-6;
        if (IsDuelMode && _duelRoundRestartRemainingSec > 1e-6)
        {
            _duelRoundRestartRemainingSec = Math.Max(0.0, _duelRoundRestartRemainingSec - DeltaTimeSec);
            World.Projectiles.Clear();
            World.SurfaceLastAcceptedHitTimes.Clear();
            LastReport = null;
            if (_duelRoundRestartRemainingSec <= 1e-6 && !_duelFinished)
            {
                ArrangeDuelScenarioEntities(World, _rules, refillRobots: true);
                duelRoundRestartActive = false;
            }
        }

        bool effectiveAiEnabled = _aiEnabled || IsDuelMode;
        long stepStartTicks = SimulatorRuntimePerformance.Timestamp();
        long segmentStartTicks = stepStartTicks;
        MaybeReloadDecisionDeploymentProfile();
        long reloadTicks = SimulatorRuntimePerformance.ElapsedTicksSince(segmentStartTicks);
        segmentStartTicks = SimulatorRuntimePerformance.Timestamp();
        EnsureSingleUnitTestFocus();
        ApplyPlayerControlState(playerControlState);
        ApplyMapComponentTestRuntimeFilters();
        ApplySingleUnitTestRuntimeFilters();
        ApplyScenarioRuntimePreMotion();
        long inputTicks = SimulatorRuntimePerformance.ElapsedTicksSince(segmentStartTicks);
        segmentStartTicks = SimulatorRuntimePerformance.Timestamp();
        _terrainMotionService.Step(World, _runtimeGrid, DeltaTimeSec, effectiveAiEnabled);
        long motionTicks = SimulatorRuntimePerformance.ElapsedTicksSince(segmentStartTicks);
        segmentStartTicks = SimulatorRuntimePerformance.Timestamp();
        ApplyMapComponentTestRuntimeFilters();
        ApplySingleUnitTestRuntimeFilters();
        long postMotionFilterTicks = SimulatorRuntimePerformance.ElapsedTicksSince(segmentStartTicks);
        segmentStartTicks = SimulatorRuntimePerformance.Timestamp();
        LastReport = _simulationService.Run(
            World,
            MapPreset.Facilities,
            DeltaTimeSec,
            DeltaTimeSec,
            captureFinalEntities: false,
            enableCombat: !duelRoundRestartActive);
        ApplyScenarioRuntimePostSimulation();
        long rulesTicks = SimulatorRuntimePerformance.ElapsedTicksSince(segmentStartTicks);
        segmentStartTicks = SimulatorRuntimePerformance.Timestamp();
        ApplySingleUnitTestRuntimeFilters();
        EnsureSelectedEntity();
        long selectTicks = SimulatorRuntimePerformance.ElapsedTicksSince(segmentStartTicks);
        long totalTicks = SimulatorRuntimePerformance.ElapsedTicksSince(stepStartTicks);
        LogSimulationPerfIfDue(totalTicks, reloadTicks, inputTicks, motionTicks, postMotionFilterTicks, rulesTicks, selectTicks);
    }

    private void LogSimulationPerfIfDue(
        long totalTicks,
        long reloadTicks,
        long inputTicks,
        long motionTicks,
        long postMotionFilterTicks,
        long rulesTicks,
        long selectTicks)
    {
        if (!SimulatorRuntimePerformance.TryMarkInterval(ref _lastSimulationPerfLogTicks, 2.0))
        {
            return;
        }

        string line =
            $"{SimulatorRuntimePerformance.WallClockLabel()} "
            + $"total={SimulatorRuntimePerformance.FormatMilliseconds(totalTicks)}ms "
            + $"reload={SimulatorRuntimePerformance.FormatMilliseconds(reloadTicks)}ms "
            + $"input={SimulatorRuntimePerformance.FormatMilliseconds(inputTicks)}ms "
            + $"motion={SimulatorRuntimePerformance.FormatMilliseconds(motionTicks)}ms "
            + $"filter={SimulatorRuntimePerformance.FormatMilliseconds(postMotionFilterTicks)}ms "
            + $"rules={SimulatorRuntimePerformance.FormatMilliseconds(rulesTicks)}ms "
            + $"select={SimulatorRuntimePerformance.FormatMilliseconds(selectTicks)}ms "
            + $"entities={World.Entities.Count} projectiles={World.Projectiles.Count}";
        SimulatorRuntimeLog.Append("simulation_perf.log", line);
    }

    public IReadOnlyList<SimulationEntity> GetControlCandidates(string? team = null)
    {
        bool teamFilterEnabled = !string.IsNullOrWhiteSpace(team);
        string normalizedTeam = Simulator3dOptions.NormalizeTeam(team);
        return World.Entities
            .Where(IsControlEntity)
            .Where(entity => !teamFilterEnabled || string.Equals(entity.Team, normalizedTeam, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => entity.Team, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<SimulationEntity> GetTacticalTargets(string friendlyTeam)
    {
        string normalizedTeam = Simulator3dOptions.NormalizeTeam(friendlyTeam);
        return World.Entities
            .Where(entity => entity.IsAlive && !entity.IsSimulationSuppressed)
            .Where(entity => !string.Equals(entity.Team, normalizedTeam, StringComparison.OrdinalIgnoreCase))
            .Where(entity =>
                IsControlEntity(entity)
                || string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => entity.Team, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.EntityType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void SetTeamTacticalCommand(
        string team,
        string command,
        string? targetId,
        double targetX,
        double targetY,
        double patrolRadiusWorld)
    {
        string normalizedTeam = Simulator3dOptions.NormalizeTeam(team);
        string normalizedCommand = (command ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedCommand is not ("attack" or "defend" or "patrol"))
        {
            normalizedCommand = string.Empty;
        }

        foreach (SimulationEntity entity in World.Entities)
        {
            if (!IsControlEntity(entity)
                || !string.Equals(entity.Team, normalizedTeam, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entity.TacticalCommand = normalizedCommand;
            entity.TacticalTargetId = normalizedCommand == "attack" ? targetId : null;
            entity.TacticalTargetX = targetX;
            entity.TacticalTargetY = targetY;
            entity.TacticalPatrolRadiusWorld = Math.Max(4.0, patrolRadiusWorld);
        }
    }

    public bool SetEntityTacticalCommand(
        string entityId,
        string command,
        string? targetId,
        double targetX,
        double targetY,
        double patrolRadiusWorld)
    {
        string normalizedCommand = (command ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedCommand is not ("attack" or "defend" or "patrol"))
        {
            normalizedCommand = string.Empty;
        }

        SimulationEntity? entity = World.Entities.FirstOrDefault(candidate =>
            IsControlEntity(candidate)
            && string.Equals(candidate.Id, entityId, StringComparison.OrdinalIgnoreCase));
        if (entity is null)
        {
            return false;
        }

        entity.TacticalCommand = normalizedCommand;
        entity.TacticalTargetId = normalizedCommand == "attack" ? targetId : null;
        entity.TacticalTargetX = targetX;
        entity.TacticalTargetY = targetY;
        entity.TacticalPatrolRadiusWorld = Math.Max(4.0, patrolRadiusWorld);
        return true;
    }

    public void SetSelectedTeam(string team)
    {
        string normalized = Simulator3dOptions.NormalizeTeam(team);
        if (IsFocusSandboxMode)
        {
            SetSingleUnitTestFocus(team: normalized);
            return;
        }

        if (string.Equals(SelectedTeam, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedTeam = normalized;
        _selectedEntityId = null;
        EnsureSelectedEntity();
        PersistSimulatorSettings();
    }

    public bool SetSelectedEntity(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return false;
        }

        if (IsFocusSandboxMode)
        {
            if (!TryResolveTeamAndEntityKey(entityId, out string team, out string entityKey))
            {
                return false;
            }

            return SetSingleUnitTestFocus(team, entityKey);
        }

        SimulationEntity? matched = GetControlCandidates()
            .FirstOrDefault(entity => string.Equals(entity.Id, entityId, StringComparison.OrdinalIgnoreCase));
        if (matched is null)
        {
            return false;
        }

        _selectedEntityId = matched.Id;
        SelectedTeam = Simulator3dOptions.NormalizeTeam(matched.Team);
        PersistSimulatorSettings();
        return true;
    }

    public void ToggleRicochet()
    {
        SetRicochetEnabled(!RicochetEnabled);
    }

    public void ReloadDecisionDeploymentProfile()
    {
        JsonObject latest = _configurationService.LoadConfig(_primaryConfigPath);
        _decisionDeploymentConfig = DecisionDeploymentConfig.LoadFromConfig(latest);
        _terrainMotionService = CreateTerrainMotionService();
        _decisionConfigLastWriteUtc = ReadLastWriteTimeUtc(_primaryConfigPath);
        _lastDecisionConfigProbeUtc = DateTime.UtcNow;
    }

    public void SetRicochetEnabled(bool enabled)
    {
        if (RicochetEnabled == enabled)
        {
            return;
        }

        RicochetEnabled = enabled;
        _simulationService = BuildSimulationService(_rules);
        PersistSimulatorSettings();
    }

    public void CycleSelectedEntity(int direction)
    {
        if (IsFocusSandboxMode)
        {
            CycleSingleUnitTestEntityKey(direction);
            return;
        }

        IReadOnlyList<SimulationEntity> candidates = GetControlCandidates(SelectedTeam);
        if (candidates.Count == 0)
        {
            candidates = GetControlCandidates();
        }

        if (candidates.Count == 0)
        {
            _selectedEntityId = null;
            return;
        }

        int currentIndex = 0;
        if (!string.IsNullOrWhiteSpace(_selectedEntityId))
        {
            int foundIndex = Array.FindIndex(candidates.ToArray(), item =>
                string.Equals(item.Id, _selectedEntityId, StringComparison.OrdinalIgnoreCase));
            if (foundIndex >= 0)
            {
                currentIndex = foundIndex;
            }
        }

        int nextIndex = (currentIndex + direction) % candidates.Count;
        if (nextIndex < 0)
        {
            nextIndex += candidates.Count;
        }

        _selectedEntityId = candidates[nextIndex].Id;
        SelectedTeam = Simulator3dOptions.NormalizeTeam(candidates[nextIndex].Team);
        PersistSimulatorSettings();
    }

    private void RebuildWorld()
    {
        string? previousSelection = _selectedEntityId;
        string previousTeam = SelectedTeam;

        _requiresDeferredLobbyBootstrap = false;
        MapPreset = LoadMapPresetForCurrentMode();
        _appearanceCatalog = AppearanceProfileCatalog.Load(ResolveAppearancePath());
        _runtimeGrid = _runtimeGridLoader.TryLoad(MapPreset, out _runtimeGridWarning);
        _rules = _ruleSetLoader.LoadFromConfig(_config);
        _decisionDeploymentConfig = DecisionDeploymentConfig.LoadFromConfig(_config);
        _simulationService = BuildSimulationService(_rules);
        _terrainMotionService = CreateTerrainMotionService();
        World = BuildWorldForCurrentMode(MapPreset, _rules, _appearanceCatalog, CaptureLobbyWorldBuildSnapshot());
        _unitTestDamageByEntityId.Clear();
        EnsureSingleUnitTestFocus();
        ApplyMapComponentTestRuntimeFilters();
        ApplySingleUnitTestRuntimeFilters();
        _selectedEntityId = previousSelection;
        SelectedTeam = previousTeam;
        LastReport = null;
        EnsureSelectedEntity();
    }

    private void RebuildDeferredLobbyPlaceholderWorld()
    {
        string? previousSelection = _selectedEntityId;
        string previousTeam = SelectedTeam;

        MapPreset = LoadMapPresetForCurrentMode();
        _appearanceCatalog = AppearanceProfileCatalog.Load(ResolveAppearancePath());
        _rules = _ruleSetLoader.LoadFromConfig(_config);
        _decisionDeploymentConfig = DecisionDeploymentConfig.LoadFromConfig(_config);
        _simulationService = BuildSimulationService(_rules);
        _terrainMotionService = CreateTerrainMotionService();
        _runtimeGrid = null;
        _runtimeGridWarning = "startup_async_pending";
        _requiresDeferredLobbyBootstrap = true;
        World = CreatePlaceholderWorld(MapPreset);
        _unitTestDamageByEntityId.Clear();
        EnsureSingleUnitTestFocus();
        ApplyMapComponentTestRuntimeFilters();
        ApplySingleUnitTestRuntimeFilters();
        _selectedEntityId = previousSelection;
        SelectedTeam = previousTeam;
        LastReport = null;
        EnsureSelectedEntity();
    }

    private PreparedMatchWorldState PrepareMatchWorldState()
    {
        SimulationWorldState world = BuildWorldForCurrentMode(MapPreset, _rules, _appearanceCatalog, CaptureLobbyWorldBuildSnapshot());
        return new PreparedMatchWorldState(world);
    }

    private LobbyWorldBuildSnapshot CaptureLobbyWorldBuildSnapshot()
    {
        return new LobbyWorldBuildSnapshot(
            _matchMode,
            ActiveMapPreset,
            _infantryMode,
            _heroPerformanceMode,
            _infantryDurabilityMode,
            _infantryWeaponMode,
            _sentryControlMode,
            _sentryStance,
            _autoAimAccuracyScale);
    }

    private PreparedLobbyWorldState PrepareLobbyWorldState(LobbyWorldBuildSnapshot snapshot)
    {
        MapPresetDefinition mapPreset = LoadMapPresetForMode(snapshot.MatchMode, snapshot.ActiveMapPreset);
        AppearanceProfileCatalog appearanceCatalog = AppearanceProfileCatalog.Load(ResolveAppearancePath());
        RuntimeGridData? runtimeGrid = _runtimeGridLoader.TryLoad(mapPreset, out string? runtimeGridWarning);
        RuleSet rules = _ruleSetLoader.LoadFromConfig(_config);
        DecisionDeploymentConfig decisionDeploymentConfig = DecisionDeploymentConfig.LoadFromConfig(_config);
        SimulationWorldState world = BuildWorldForCurrentMode(mapPreset, rules, appearanceCatalog, snapshot);
        return new PreparedLobbyWorldState(
            world,
            mapPreset,
            appearanceCatalog,
            runtimeGrid,
            runtimeGridWarning,
            rules,
            decisionDeploymentConfig);
    }

    private bool ShouldDeferInitialLobbyBootstrap(Simulator3dOptions options)
    {
        if (options.PreviewOnly)
        {
            return false;
        }

        if (IsMapComponentTestMode || IsFocusSandboxMode)
        {
            return false;
        }

        return string.Equals(_matchMode, "full", StringComparison.OrdinalIgnoreCase);
    }

    private SimulationWorldState CreatePlaceholderWorld(MapPresetDefinition mapPreset)
    {
        var world = new SimulationWorldState
        {
            GameTimeSec = 0.0,
            MetersPerWorldUnit = ResolveMetersPerWorldUnit(mapPreset),
            WorldWidth = Math.Max(1.0, mapPreset.Width),
            WorldHeight = Math.Max(1.0, mapPreset.Height),
        };

        double initialGold = ResolveInitialGold();
        SimulationTeamState redTeam = world.GetOrCreateTeamState("red", initialGold);
        SimulationTeamState blueTeam = world.GetOrCreateTeamState("blue", initialGold);
        redTeam.EnergyRotorDirectionSign = 1;
        blueTeam.EnergyRotorDirectionSign = -1;
        return world;
    }

    private MapPresetDefinition LoadMapPresetForCurrentMode()
        => LoadMapPresetForMode(_matchMode, ActiveMapPreset);

    private MapPresetDefinition LoadMapPresetForMode(string matchMode, string activeMapPreset)
    {
        if (string.Equals(matchMode, "single_unit_test", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFixedSandboxMapPreset("blankCanvas", activeMapPreset);
        }

        if (string.Equals(matchMode, "duel_1v1", StringComparison.OrdinalIgnoreCase))
        {
            return BuildDuelScenarioMapPreset();
        }

        if (string.Equals(matchMode, "unit_test", StringComparison.OrdinalIgnoreCase))
        {
            return BuildUnitTestScenarioMapPreset();
        }

        return _mapPresetService.LoadPreset(_layout, activeMapPreset);
    }

    private MapPresetDefinition LoadFixedSandboxMapPreset(string preferredPreset, string fallbackPreset)
    {
        string resolvedPreset = AvailableMapPresets.FirstOrDefault(preset =>
                string.Equals(preset, preferredPreset, StringComparison.OrdinalIgnoreCase))
            ?? fallbackPreset;
        return _mapPresetService.LoadPreset(_layout, resolvedPreset);
    }

    private SimulationWorldState BuildWorldForCurrentMode(
        MapPresetDefinition mapPreset,
        RuleSet rules,
        AppearanceProfileCatalog appearanceCatalog,
        LobbyWorldBuildSnapshot snapshot)
    {
        SimulationWorldState world = _bootstrapService.BuildInitialWorld(_config, rules, mapPreset);
        ApplyConfiguredRoleProfilesToWorld(world, resetHealth: true, rules, snapshot);
        ApplyAppearanceProfilesToWorld(world, appearanceCatalog, mapPreset, snapshot.InfantryMode);
        ConfigureScenarioWorld(world, mapPreset, snapshot.MatchMode, rules);
        return world;
    }

    private void ConfigureScenarioWorld(
        SimulationWorldState world,
        MapPresetDefinition mapPreset,
        string matchMode,
        RuleSet rules)
    {
        if (string.Equals(matchMode, "duel_1v1", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureDuelScenarioWorld(world, mapPreset, rules);
            return;
        }

        if (string.Equals(matchMode, "unit_test", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureUnitTestScenarioWorld(world, mapPreset, rules);
        }
    }

    private MapPresetDefinition BuildDuelScenarioMapPreset()
    {
        const int mapWidth = 1000;
        const int mapHeight = 1000;
        const double fieldSizeM = 10.0;
        double centerX = mapWidth * 0.5;
        double centerY = mapHeight * 0.5;
        const double wallLengthWorld = 300.0;
        const double wallThicknessWorld = 22.0;
        const double wallGapWorld = 100.0;
        const double wallAngleDeg = 45.0;
        double wallAngleRad = wallAngleDeg * Math.PI / 180.0;
        double axisOffset = wallGapWorld * 0.5 + wallLengthWorld * 0.5;
        double offsetX = Math.Cos(wallAngleRad) * axisOffset;
        double offsetY = Math.Sin(wallAngleRad) * axisOffset;
        double upperCenterX = centerX + offsetX;
        double upperCenterY = centerY + offsetY;
        double lowerCenterX = centerX - offsetX;
        double lowerCenterY = centerY - offsetY;
        double wallHalfX = Math.Cos(wallAngleRad) * wallLengthWorld * 0.5;
        double wallHalfY = Math.Sin(wallAngleRad) * wallLengthWorld * 0.5;
        var facilities = new List<FacilityRegion>
        {
            CreateRectFacility("duel_boundary", "boundary", "neutral", centerX, centerY, mapWidth - 1, mapHeight - 1, 14.0),
            CreateLineFacility(
                "duel_wall_upper_collision",
                "wall",
                "neutral",
                upperCenterX - wallHalfX,
                upperCenterY - wallHalfY,
                upperCenterX + wallHalfX,
                upperCenterY + wallHalfY,
                wallThicknessWorld,
                1.55),
            CreateLineFacility(
                "duel_wall_lower_collision",
                "wall",
                "neutral",
                lowerCenterX - wallHalfX,
                lowerCenterY - wallHalfY,
                lowerCenterX + wallHalfX,
                lowerCenterY + wallHalfY,
                wallThicknessWorld,
                1.55),
        };
        var facets = new List<TerrainFacetDefinition>
        {
            CreateRectFacet(
                "duel_floor",
                "floor",
                "neutral",
                centerX,
                centerY,
                mapWidth,
                mapHeight,
                0.0,
                "#9EA5AC",
                "#7A828A",
                collisionEnabled: true),
            CreateRotatedRectFacet(
                "duel_wall_upper",
                "wall_block",
                "neutral",
                upperCenterX,
                upperCenterY,
                wallLengthWorld,
                wallThicknessWorld,
                wallAngleDeg,
                1.55,
                "#CCD2D8",
                "#8D949C",
                collisionEnabled: true),
            CreateRotatedRectFacet(
                "duel_wall_lower",
                "wall_block",
                "neutral",
                lowerCenterX,
                lowerCenterY,
                wallLengthWorld,
                wallThicknessWorld,
                wallAngleDeg,
                1.55,
                "#CCD2D8",
                "#8D949C",
                collisionEnabled: true),
        };
        return CreateScenarioMapPreset("duel_1v1", mapWidth, mapHeight, fieldSizeM, fieldSizeM, facilities, facets);
    }

    private MapPresetDefinition BuildUnitTestScenarioMapPreset()
    {
        const int mapWidth = 600;
        const int mapHeight = 600;
        const double fieldSizeM = 6.0;
        double centerX = mapWidth * 0.5;
        double centerY = mapHeight * 0.5;
        double radius = 190.0;
        var facilities = new List<FacilityRegion>
        {
            CreateRectFacility("unit_test_boundary", "boundary", "neutral", centerX, centerY, mapWidth - 1, mapHeight - 1, 14.0),
            CreateRectFacility("unit_test_blue_base", "base", "blue", centerX, centerY - radius, 190.0, 160.0, 12.0, 1.181),
            CreateRectFacility("unit_test_blue_outpost", "outpost", "blue", centerX + radius * 0.866, centerY + radius * 0.5, 70.0, 70.0, 12.0, 1.578),
            CreateRectFacility("unit_test_energy_mechanism", "energy_mechanism", "neutral", centerX - radius * 0.866, centerY + radius * 0.5, 210.0, 210.0, 12.0, 2.30),
        };
        var facets = new List<TerrainFacetDefinition>
        {
            CreateRectFacet(
                "unit_test_floor",
                "floor",
                "neutral",
                centerX,
                centerY,
                mapWidth,
                mapHeight,
                0.0,
                "#39444D",
                "#293238",
                collisionEnabled: true),
        };
        return CreateScenarioMapPreset("unit_test", mapWidth, mapHeight, fieldSizeM, fieldSizeM, facilities, facets);
    }

    private static MapPresetDefinition CreateScenarioMapPreset(
        string name,
        int width,
        int height,
        double fieldLengthM,
        double fieldWidthM,
        IReadOnlyList<FacilityRegion> facilities,
        IReadOnlyList<TerrainFacetDefinition> facets)
    {
        return new MapPresetDefinition
        {
            Name = name,
            Width = width,
            Height = height,
            FieldLengthM = fieldLengthM,
            FieldWidthM = fieldWidthM,
            ImagePath = string.Empty,
            SourcePath = string.Empty,
            AnnotationPath = string.Empty,
            CoordinateSystem = new MapCoordinateSystemDefinition
            {
                CoordinateSpace = "world",
                Unit = "px",
                OriginX = 0.0,
                OriginY = 0.0,
                FieldLengthM = fieldLengthM,
                FieldWidthM = fieldWidthM,
            },
            TerrainSurface = new TerrainSurfaceDefinition
            {
                MapType = "terrain_surface_map",
                RenderProfile = "scenario_flat_facets",
                TopFaceMode = "solid_color",
                SideFaceMode = "solid_color",
                SideColorHex = "#505962",
                ResolutionM = Math.Max(fieldLengthM / Math.Max(1, width), fieldWidthM / Math.Max(1, height)),
                WidthCells = width,
                HeightCells = height,
                Facets = facets,
            },
            RuntimeGrid = null,
            Facilities = facilities,
        };
    }

    private static FacilityRegion CreateRectFacility(
        string id,
        string type,
        string team,
        double centerX,
        double centerY,
        double width,
        double height,
        double thickness = 12.0,
        double heightM = 0.0)
    {
        double halfWidth = width * 0.5;
        double halfHeight = height * 0.5;
        return new FacilityRegion
        {
            Id = id,
            Type = type,
            Team = team,
            Shape = "rect",
            X1 = centerX - halfWidth,
            Y1 = centerY - halfHeight,
            X2 = centerX + halfWidth,
            Y2 = centerY + halfHeight,
            Thickness = thickness,
            HeightM = heightM,
        };
    }

    private static FacilityRegion CreateLineFacility(
        string id,
        string type,
        string team,
        double x1,
        double y1,
        double x2,
        double y2,
        double thickness,
        double heightM = 0.0)
    {
        return new FacilityRegion
        {
            Id = id,
            Type = type,
            Team = team,
            Shape = "line",
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Thickness = thickness,
            HeightM = heightM,
        };
    }

    private static TerrainFacetDefinition CreateRectFacet(
        string id,
        string type,
        string team,
        double centerX,
        double centerY,
        double width,
        double height,
        double topHeightM,
        string topColorHex,
        string sideColorHex,
        bool collisionEnabled)
    {
        double halfWidth = width * 0.5;
        double halfHeight = height * 0.5;
        return new TerrainFacetDefinition
        {
            Id = id,
            Type = type,
            Team = team,
            TopColorHex = topColorHex,
            SideColorHex = sideColorHex,
            CollisionEnabled = collisionEnabled,
            CollisionExpandM = collisionEnabled ? 0.01 : 0.0,
            Points =
            [
                new Point2D(centerX - halfWidth, centerY - halfHeight),
                new Point2D(centerX + halfWidth, centerY - halfHeight),
                new Point2D(centerX + halfWidth, centerY + halfHeight),
                new Point2D(centerX - halfWidth, centerY + halfHeight),
            ],
            HeightsM =
            [
                topHeightM,
                topHeightM,
                topHeightM,
                topHeightM,
            ],
        };
    }

    private static TerrainFacetDefinition CreateRotatedRectFacet(
        string id,
        string type,
        string team,
        double centerX,
        double centerY,
        double width,
        double height,
        double angleDeg,
        double topHeightM,
        string topColorHex,
        string sideColorHex,
        bool collisionEnabled)
    {
        double halfWidth = width * 0.5;
        double halfHeight = height * 0.5;
        double angleRad = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);
        Point2D Rotate(double localX, double localY)
        {
            return new Point2D(
                centerX + localX * cos - localY * sin,
                centerY + localX * sin + localY * cos);
        }

        return new TerrainFacetDefinition
        {
            Id = id,
            Type = type,
            Team = team,
            TopColorHex = topColorHex,
            SideColorHex = sideColorHex,
            CollisionEnabled = collisionEnabled,
            CollisionExpandM = collisionEnabled ? 0.01 : 0.0,
            Points =
            [
                Rotate(-halfWidth, -halfHeight),
                Rotate(halfWidth, -halfHeight),
                Rotate(halfWidth, halfHeight),
                Rotate(-halfWidth, halfHeight),
            ],
            HeightsM =
            [
                topHeightM,
                topHeightM,
                topHeightM,
                topHeightM,
            ],
        };
    }

    private static (double X, double Y) ResolveFacilityCenter(FacilityRegion facility)
    {
        if (facility.Points.Count > 0)
        {
            double minX = facility.Points.Min(point => point.X);
            double maxX = facility.Points.Max(point => point.X);
            double minY = facility.Points.Min(point => point.Y);
            double maxY = facility.Points.Max(point => point.Y);
            return ((minX + maxX) * 0.5, (minY + maxY) * 0.5);
        }

        return ((facility.X1 + facility.X2) * 0.5, (facility.Y1 + facility.Y2) * 0.5);
    }

    private void ConfigureDuelScenarioWorld(
        SimulationWorldState world,
        MapPresetDefinition mapPreset,
        RuleSet rules)
    {
        ArrangeDuelScenarioEntities(world, rules, refillRobots: true);
    }

    private void ConfigureUnitTestScenarioWorld(
        SimulationWorldState world,
        MapPresetDefinition mapPreset,
        RuleSet rules)
    {
        _ = mapPreset;
        SimulationTeamState teamState = world.GetOrCreateTeamState("red");
        ConfigureUnitTestEnergyTeamState(teamState);
        teamState.EnergyTestForceLarge = _unitTestEnergyForceLarge;
        ArrangeUnitTestScenarioEntities(world, rules, refillRobots: true);
    }

    private void AddScenarioWallObstacleEntities(SimulationWorldState world, MapPresetDefinition mapPreset)
    {
        foreach (FacilityRegion facility in mapPreset.Facilities.Where(facility =>
                     string.Equals(facility.Type, "wall", StringComparison.OrdinalIgnoreCase)))
        {
            string entityId = $"{facility.Id}_obstacle";
            if (world.Entities.Any(entity => string.Equals(entity.Id, entityId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            (double centerX, double centerY) = ResolveFacilityCenter(facility);
            double widthWorld = Math.Abs(facility.X2 - facility.X1);
            double heightWorld = Math.Abs(facility.Y2 - facility.Y1);
            double angleDeg = 0.0;
            double lengthWorld = Math.Max(widthWorld, heightWorld);
            double thicknessWorld = Math.Max(2.0, Math.Min(widthWorld, heightWorld));
            if (string.Equals(facility.Shape, "line", StringComparison.OrdinalIgnoreCase))
            {
                double dx = facility.X2 - facility.X1;
                double dy = facility.Y2 - facility.Y1;
                lengthWorld = Math.Sqrt(dx * dx + dy * dy);
                thicknessWorld = Math.Max(2.0, facility.Thickness * 2.0);
                angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            }
            else if (heightWorld >= widthWorld)
            {
                angleDeg = 90.0;
            }

            double lengthM = Math.Max(0.20, lengthWorld * world.MetersPerWorldUnit);
            double widthM = Math.Max(0.08, thicknessWorld * world.MetersPerWorldUnit);
            world.Entities.Add(new SimulationEntity
            {
                Id = entityId,
                Team = "neutral",
                EntityType = "wall",
                RoleKey = "wall",
                X = centerX,
                Y = centerY,
                AngleDeg = angleDeg,
                TurretYawDeg = angleDeg,
                IsAlive = false,
                BodyLengthM = lengthM,
                BodyWidthM = widthM,
                BodyHeightM = 1.55,
                BodyClearanceM = 0.0,
                CollisionRadiusWorld = Math.Sqrt(lengthM * lengthM + widthM * widthM) * 0.5 / Math.Max(world.MetersPerWorldUnit, 1e-6),
                RuntimeModelToSceneMatrix = world.RuntimeModelToSceneMatrix,
                RuntimeModelToWorldMatrix = world.RuntimeModelToWorldMatrix,
            });
        }
    }

    private void ArrangeDuelScenarioEntities(
        SimulationWorldState world,
        RuleSet rules,
        bool refillRobots)
    {
        _singleUnitTestTeam = "red";
        string focusId = SingleUnitTestFocusId;
        string enemyId = DuelEnemyEntityId;
        TrimDuelScenarioMovableEntities(world, focusId, enemyId);
        double centerX = world.WorldWidth * 0.5;
        double centerY = world.WorldHeight * 0.5;
        double spawnOffsetX = world.WorldWidth * 0.37;
        double spawnOffsetY = world.WorldHeight * 0.37;
        bool preserveProgress = HasDuelProgressToPreserve();
        _duelRoundRestartRemainingSec = 0.0;
        if (refillRobots)
        {
            ResetDuelCurrentRoundStats();
        }

        foreach (SimulationEntity entity in world.Entities.Where(IsMovableEntity))
        {
            if (string.Equals(entity.Id, focusId, StringComparison.OrdinalIgnoreCase))
            {
                PlaceScenarioEntity(entity, centerX - spawnOffsetX, centerY - spawnOffsetY, 45.0);
                if (refillRobots)
                {
                    ResetScenarioRobotState(entity, rules, preserveProgress);
                }

                continue;
            }

            if (string.Equals(entity.Id, enemyId, StringComparison.OrdinalIgnoreCase))
            {
                PlaceScenarioEntity(entity, centerX + spawnOffsetX, centerY + spawnOffsetY, 225.0);
                if (refillRobots)
                {
                    ResetScenarioRobotState(entity, rules, preserveProgress);
                }

                entity.TacticalCommand = "attack";
                entity.TacticalTargetId = focusId;
                ConfigureDuelEnemySentry(entity);
            }
        }
    }

    private bool HasDuelProgressToPreserve()
        => IsDuelMode
            && (_duelRoundsCompleted > 0
                || _duelRedScore > 0
                || _duelBlueScore > 0
                || _duelRoundRestartRemainingSec > 1e-6);

    private static void TrimDuelScenarioMovableEntities(
        SimulationWorldState world,
        string focusId,
        string enemyId)
    {
        for (int index = world.Entities.Count - 1; index >= 0; index--)
        {
            SimulationEntity entity = world.Entities[index];
            if (!IsMovableEntity(entity))
            {
                continue;
            }

            if (string.Equals(entity.Id, focusId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.Id, enemyId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            world.Entities.RemoveAt(index);
        }
    }

    private void ArrangeUnitTestScenarioEntities(
        SimulationWorldState world,
        RuleSet rules,
        bool refillRobots)
    {
        _singleUnitTestTeam = "red";
        string focusId = SingleUnitTestFocusId;
        double centerX = world.WorldWidth * 0.5;
        double centerY = world.WorldHeight * 0.5;
        int benchIndex = 0;
        foreach (SimulationEntity entity in world.Entities.Where(IsMovableEntity))
        {
            if (string.Equals(entity.Id, focusId, StringComparison.OrdinalIgnoreCase))
            {
                PlaceScenarioEntity(entity, centerX, centerY, 270.0);
                if (refillRobots)
                {
                    ResetScenarioRobotState(entity, rules, preserveProgress: false);
                }

                continue;
            }

            ParkScenarioEntity(entity, benchIndex++);
        }
    }

    private void PlaceScenarioEntity(SimulationEntity entity, double x, double y, double yawDeg)
    {
        entity.X = x;
        entity.Y = y;
        entity.GroundHeightM = 0.0;
        entity.AirborneHeightM = 0.0;
        entity.VerticalVelocityMps = 0.0;
        entity.JumpCrouchTimerSec = 0.0;
        entity.JumpCrouchDurationSec = 0.0;
        entity.LandingCompressionM = 0.0;
        entity.LandingCompressionVelocityMps = 0.0;
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
        entity.ChassisPitchDeg = 0.0;
        entity.ChassisRollDeg = 0.0;
        entity.MoveInputForward = 0.0;
        entity.MoveInputRight = 0.0;
        entity.TraversalActive = false;
        entity.TraversalProgress = 0.0;
        entity.MotionBlockReason = string.Empty;
        ResetAutoAimLockState(entity);
    }

    private static void ParkScenarioEntity(SimulationEntity entity, int index)
    {
        double x = -400.0 - index * 40.0;
        double y = -400.0 - index * 12.0;
        entity.X = x;
        entity.Y = y;
        entity.GroundHeightM = 0.0;
        entity.AirborneHeightM = 0.0;
        entity.VerticalVelocityMps = 0.0;
        entity.JumpCrouchTimerSec = 0.0;
        entity.JumpCrouchDurationSec = 0.0;
        entity.LandingCompressionM = 0.0;
        entity.LandingCompressionVelocityMps = 0.0;
        entity.VelocityXWorldPerSec = 0.0;
        entity.VelocityYWorldPerSec = 0.0;
        entity.AngularVelocityDegPerSec = 0.0;
        entity.MoveInputForward = 0.0;
        entity.MoveInputRight = 0.0;
        entity.TraversalActive = false;
        entity.TraversalProgress = 0.0;
        entity.TacticalCommand = string.Empty;
        entity.TacticalTargetId = null;
    }

    private void ResetScenarioRobotState(SimulationEntity entity, RuleSet rules, bool preserveProgress)
    {
        ResolvedRoleProfile baselineProfile = rules.ResolveRuntimeProfile(entity);
        int maxLevel = Math.Max(1, baselineProfile.MaxLevel);
        if (preserveProgress)
        {
            entity.Level = Math.Clamp(entity.Level <= 0 ? baselineProfile.InitialLevel : entity.Level, 1, maxLevel);
            entity.Experience = Math.Max(0.0, entity.Experience);
            entity.PendingExperienceDisplay = Math.Max(0.0, entity.PendingExperienceDisplay);
            entity.PendingLevelUpCount = Math.Max(0, entity.PendingLevelUpCount);
        }
        else
        {
            entity.Level = baselineProfile.InitialLevel;
            entity.Experience = 0.0;
            entity.PendingExperienceDisplay = 0.0;
            entity.PendingLevelUpCount = 0;
        }

        ResolvedRoleProfile profile = rules.ResolveRuntimeProfile(entity);
        entity.MaxLevel = profile.MaxLevel;
        entity.MaxHealth = profile.MaxHealth;
        entity.MaxPower = profile.MaxPower;
        entity.MaxHeat = profile.MaxHeat;
        entity.AmmoType = profile.AmmoType;
        entity.RuleDrivePowerLimitW = profile.MaxPower;
        entity.MaxChassisEnergy = profile.MaxChassisEnergy;
        entity.ChassisEcoPowerLimitW = profile.EcoPowerLimitW;
        entity.ChassisBoostThresholdEnergy = profile.BoostThresholdEnergy;
        entity.ChassisBoostMultiplier = profile.BoostPowerMultiplier;
        entity.ChassisBoostPowerCapW = profile.BoostPowerCapW;
        entity.MaxBufferEnergyJ = Math.Max(0.0, entity.MaxBufferEnergyJ <= 1e-6 ? 60.0 : entity.MaxBufferEnergyJ);
        entity.BufferReserveEnergyJ = Math.Clamp(entity.BufferReserveEnergyJ <= 1e-6 ? 10.0 : entity.BufferReserveEnergyJ, 0.0, entity.MaxBufferEnergyJ);
        entity.MaxSuperCapEnergyJ = Math.Max(0.0, entity.MaxSuperCapEnergyJ <= 1e-6 ? 2000.0 : entity.MaxSuperCapEnergyJ);
        entity.Health = entity.MaxHealth;
        entity.Power = entity.MaxPower;
        entity.Heat = 0.0;
        entity.HeatLockInitialHeat = 0.0;
        entity.BufferEnergyJ = entity.MaxBufferEnergyJ;
        entity.SuperCapEnergyJ = entity.MaxSuperCapEnergyJ;
        entity.ChassisEnergy = profile.UsesChassisEnergy ? profile.InitialChassisEnergy : 0.0;
        entity.IsAlive = true;
        entity.PermanentEliminated = false;
        entity.DestroyedTimeSec = double.NegativeInfinity;
        entity.State = "idle";
        entity.RespawnTimerSec = 0.0;
        entity.WeakTimerSec = 0.0;
        entity.RespawnAmmoLockTimerSec = 0.0;
        entity.RespawnInvincibleTimerSec = 0.0;
        entity.HeatLockTimerSec = 0.0;
        entity.PowerCutTimerSec = 0.0;
        entity.FireCooldownSec = 0.0;
        entity.ShotsFired = 0;
        entity.UnlimitedAmmo = false;
        entity.JumpCrouchTimerSec = 0.0;
        entity.JumpCrouchDurationSec = 0.0;
        entity.LandingCompressionM = 0.0;
        entity.LandingCompressionVelocityMps = 0.0;
        entity.TestForcedDecisionId = string.Empty;
        entity.AiDecisionSelected = string.Empty;
        entity.AiDecision = "idle";
        entity.HeroDeploymentRequested = false;
        entity.HeroDeploymentActive = false;
        entity.HeroDeploymentHoldTimerSec = 0.0;
        entity.HeroDeploymentExitHoldTimerSec = 0.0;
        entity.HeroDeploymentYawCorrectionDeg = 0.0;
        entity.HeroDeploymentPitchCorrectionDeg = 0.0;
        entity.HeroDeploymentLastPitchErrorDeg = 0.0;
        entity.HeroDeploymentCorrectionPlateId = null;
        ResetAutoAimLockState(entity);
        ApplyInitialPurchasedAmmo(entity, profile);
    }

    private static void ConfigureUnitTestEnergyTeamState(SimulationTeamState teamState)
    {
        teamState.EnergyTestAlwaysAvailable = true;
        teamState.EnergyTestForceLarge = false;
    }

    private string NormalizeFocusSandboxTeam(string? team)
    {
        if (IsDuelMode || IsUnitTestMode)
        {
            return "red";
        }

        return Simulator3dOptions.NormalizeTeam(team);
    }

    private void RefreshFocusSandboxScenario(bool refillRobots)
    {
        if (IsDuelMode)
        {
            ArrangeDuelScenarioEntities(World, _rules, refillRobots);
            return;
        }

        if (IsUnitTestMode)
        {
            ConfigureUnitTestEnergyTeamState(World.GetOrCreateTeamState("red"));
            World.GetOrCreateTeamState("red").EnergyTestForceLarge = _unitTestEnergyForceLarge;
            ArrangeUnitTestScenarioEntities(World, _rules, refillRobots);
        }
    }

    private void ApplyScenarioRuntimePreMotion()
    {
        if (IsDuelMode)
        {
            MaintainDuelScenarioAiState();
        }

        if (IsUnitTestMode)
        {
            SimulationTeamState teamState = World.GetOrCreateTeamState("red");
            ConfigureUnitTestEnergyTeamState(teamState);
            teamState.EnergyTestForceLarge = _unitTestEnergyForceLarge;
        }
    }

    private void ApplyScenarioRuntimePostSimulation()
    {
        if (IsDuelMode)
        {
            AccumulateDuelRoundStats();
            MaintainDuelScenarioAiState();
            MaintainDuelRoundState();
        }

        if (!IsUnitTestMode)
        {
            return;
        }

        MaintainUnitTestDamageTracking();
        MaintainUnitTestStructureState();
        MaintainUnitTestEnergyState();
    }

    private void MaintainDuelScenarioAiState()
    {
        SimulationEntity? focus = SingleUnitTestFocusEntity;
        SimulationEntity? enemy = World.Entities.FirstOrDefault(entity =>
            string.Equals(entity.Id, DuelEnemyEntityId, StringComparison.OrdinalIgnoreCase));
        if (enemy is null)
        {
            return;
        }

        enemy.IsSimulationSuppressed = false;
        enemy.TacticalCommand = "attack";
        enemy.TacticalTargetId = focus?.Id;
        enemy.TacticalTargetX = focus?.X ?? enemy.X;
        enemy.TacticalTargetY = focus?.Y ?? enemy.Y;
        ConfigureDuelEnemySentry(enemy);
    }

    private static void ConfigureDuelEnemySentry(SimulationEntity enemy)
    {
        enemy.SentryStance = "attack";
        enemy.SentryControlMode = "full_auto";
        enemy.UnlimitedAmmo = true;
        if (string.Equals(enemy.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            enemy.Ammo42Mm = Math.Max(enemy.Ammo42Mm, 9999);
        }
        else if (!string.Equals(enemy.AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            enemy.Ammo17Mm = Math.Max(enemy.Ammo17Mm, 9999);
        }
    }

    private void AccumulateDuelRoundStats()
    {
        if (_duelRoundRestartRemainingSec > 1e-6 || LastReport is null)
        {
            return;
        }

        foreach (SimulationShotEvent shotEvent in LastReport.ShotEvents)
        {
            if (IsDuelFriendlyShooter(shotEvent.ShooterId, shotEvent.Team))
            {
                _duelCurrentFriendlyShots++;
            }
            else if (IsDuelEnemyShooter(shotEvent.ShooterId, shotEvent.Team))
            {
                _duelCurrentEnemyShots++;
            }
        }

        foreach (SimulationCombatEvent combatEvent in LastReport.CombatEvents)
        {
            SimulationEntity? shooter = World.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Id, combatEvent.ShooterId, StringComparison.OrdinalIgnoreCase));
            string shooterTeam = shooter?.Team ?? string.Empty;
            if (IsDuelFriendlyShooter(combatEvent.ShooterId, shooterTeam))
            {
                if (combatEvent.Hit)
                {
                    _duelCurrentFriendlyHits++;
                }

                if (combatEvent.Damage > 1e-6)
                {
                    _duelCurrentFriendlyDamage += combatEvent.Damage;
                }
            }
            else if (IsDuelEnemyShooter(combatEvent.ShooterId, shooterTeam))
            {
                if (combatEvent.Hit)
                {
                    _duelCurrentEnemyHits++;
                }

                if (combatEvent.Damage > 1e-6)
                {
                    _duelCurrentEnemyDamage += combatEvent.Damage;
                }
            }
        }
    }

    private bool IsDuelFriendlyShooter(string shooterId, string? team)
        => string.Equals(shooterId, SingleUnitTestFocusId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(team, "red", StringComparison.OrdinalIgnoreCase);

    private static bool IsDuelEnemyShooter(string shooterId, string? team)
        => string.Equals(shooterId, DuelEnemyEntityId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase);

    private void MaintainDuelRoundState()
    {
        if (_duelRoundRestartRemainingSec > 1e-6)
        {
            return;
        }

        if (LastReport is null || LastReport.LifecycleEvents.Count == 0)
        {
            return;
        }

        bool playerDestroyed = LastReport.LifecycleEvents.Any(lifecycleEvent =>
            string.Equals(lifecycleEvent.EventType, "death", StringComparison.OrdinalIgnoreCase)
            && string.Equals(lifecycleEvent.EntityId, SingleUnitTestFocusId, StringComparison.OrdinalIgnoreCase));
        bool enemyDestroyed = LastReport.LifecycleEvents.Any(lifecycleEvent =>
            string.Equals(lifecycleEvent.EventType, "death", StringComparison.OrdinalIgnoreCase)
            && string.Equals(lifecycleEvent.EntityId, DuelEnemyEntityId, StringComparison.OrdinalIgnoreCase));
        if (!playerDestroyed && !enemyDestroyed)
        {
            return;
        }

        CaptureDuelRoundEndStats(playerDestroyed, enemyDestroyed);
        if (playerDestroyed && !enemyDestroyed)
        {
            _duelBlueScore++;
        }
        else if (enemyDestroyed && !playerDestroyed)
        {
            _duelRedScore++;
        }

        _duelRoundsCompleted++;
        World.Projectiles.Clear();
        World.SurfaceLastAcceptedHitTimes.Clear();
        if (_duelRoundsCompleted >= _duelRoundLimit)
        {
            _duelFinished = true;
            _duelResultLabel = ResolveDuelResultLabel();
            return;
        }

        _duelRoundRestartRemainingSec = DuelRoundRestartDelaySec;
        PrepareDuelRoundRestartWait(World, _rules);
    }

    private void CaptureDuelRoundEndStats(bool playerDestroyed, bool enemyDestroyed)
    {
        SimulationEntity? player = SingleUnitTestFocusEntity;
        SimulationEntity? enemy = World.Entities.FirstOrDefault(entity =>
            string.Equals(entity.Id, DuelEnemyEntityId, StringComparison.OrdinalIgnoreCase));
        _duelLastRoundFriendlyDestroyed = playerDestroyed || player is null || !player.IsAlive;
        _duelLastRoundEnemyDestroyed = enemyDestroyed || enemy is null || !enemy.IsAlive;
        _duelLastFriendlyStats = CreateDuelRoundStats(
            _duelCurrentFriendlyDamage,
            _duelCurrentFriendlyShots,
            _duelCurrentFriendlyHits,
            player,
            _duelLastRoundFriendlyDestroyed);
        _duelLastEnemyStats = CreateDuelRoundStats(
            _duelCurrentEnemyDamage,
            _duelCurrentEnemyShots,
            _duelCurrentEnemyHits,
            enemy,
            _duelLastRoundEnemyDestroyed);

        _duelTotalFriendlyDamage += _duelLastFriendlyStats.DamageOutput;
        _duelTotalEnemyDamage += _duelLastEnemyStats.DamageOutput;
        _duelTotalFriendlyShots += _duelLastFriendlyStats.Shots;
        _duelTotalEnemyShots += _duelLastEnemyStats.Shots;
        _duelTotalFriendlyHits += _duelLastFriendlyStats.Hits;
        _duelTotalEnemyHits += _duelLastEnemyStats.Hits;
        _duelTotalFriendlyStats = CreateDuelRoundStats(
            _duelTotalFriendlyDamage,
            _duelTotalFriendlyShots,
            _duelTotalFriendlyHits,
            player,
            _duelLastRoundFriendlyDestroyed);
        _duelTotalEnemyStats = CreateDuelRoundStats(
            _duelTotalEnemyDamage,
            _duelTotalEnemyShots,
            _duelTotalEnemyHits,
            enemy,
            _duelLastRoundEnemyDestroyed);
        _duelRoundHistory.Add(new DuelRoundPairSnapshot(
            _duelRoundsCompleted + 1,
            _duelLastFriendlyStats,
            _duelLastEnemyStats));
    }

    private static DuelRoundStatSnapshot CreateDuelRoundStats(
        double damage,
        int shots,
        int hits,
        SimulationEntity? entity,
        bool destroyed)
    {
        double maxHealth = Math.Max(1.0, entity?.MaxHealth ?? 1.0);
        double health = destroyed ? 0.0 : Math.Clamp(entity?.Health ?? 0.0, 0.0, maxHealth);
        double hitRate = shots > 0 ? hits / (double)shots : 0.0;
        return new DuelRoundStatSnapshot(
            Math.Max(0.0, damage),
            Math.Max(0, shots),
            Math.Max(0, hits),
            Math.Clamp(hitRate, 0.0, 1.0),
            health,
            maxHealth,
            destroyed);
    }

    private void PrepareDuelRoundRestartWait(SimulationWorldState world, RuleSet rules)
    {
        foreach (SimulationEntity entity in world.Entities.Where(entity =>
                     string.Equals(entity.Id, SingleUnitTestFocusId, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(entity.Id, DuelEnemyEntityId, StringComparison.OrdinalIgnoreCase)))
        {
            entity.MoveInputForward = 0.0;
            entity.MoveInputRight = 0.0;
            entity.VelocityXWorldPerSec = 0.0;
            entity.VelocityYWorldPerSec = 0.0;
            entity.IsFireCommandActive = false;
            entity.AutoAimRequested = false;
            entity.BuyAmmoRequested = false;
            entity.EnergyActivationRequested = false;
        }

        MaintainDuelScenarioAiState();
    }

    private void MaintainUnitTestDamageTracking()
    {
        double nowSec = World.GameTimeSec;
        foreach (string entityId in UnitTestTrackedDamageEntityIds)
        {
            ScenarioDamageAccumulator accumulator = GetOrCreateUnitTestDamageAccumulator(entityId);
            if (!double.IsNegativeInfinity(accumulator.LastHitTimeSec)
                && nowSec - accumulator.LastHitTimeSec >= 3.0)
            {
                accumulator.TotalDamage = 0.0;
                accumulator.LastHitTimeSec = double.NegativeInfinity;
                accumulator.RecentHits.Clear();
                continue;
            }

            PruneRecentDamageSamples(accumulator, nowSec, windowSec: 1.0);
        }

        if (LastReport is null)
        {
            return;
        }

        foreach (SimulationCombatEvent combatEvent in LastReport.CombatEvents)
        {
            if (!combatEvent.Hit
                || combatEvent.Damage <= 1e-6
                || !UnitTestTrackedDamageEntityIds.Contains(combatEvent.TargetId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            ScenarioDamageAccumulator accumulator = GetOrCreateUnitTestDamageAccumulator(combatEvent.TargetId);
            if (!double.IsNegativeInfinity(accumulator.LastHitTimeSec)
                && combatEvent.TimeSec - accumulator.LastHitTimeSec >= 3.0)
            {
                accumulator.TotalDamage = 0.0;
                accumulator.RecentHits.Clear();
            }

            accumulator.LastHitTimeSec = combatEvent.TimeSec;
            accumulator.TotalDamage += combatEvent.Damage;
            accumulator.RecentHits.Enqueue((combatEvent.TimeSec, combatEvent.Damage));
        }

        foreach (string entityId in UnitTestTrackedDamageEntityIds)
        {
            PruneRecentDamageSamples(GetOrCreateUnitTestDamageAccumulator(entityId), nowSec, windowSec: 1.0);
        }
    }

    private void MaintainUnitTestStructureState()
    {
        RestoreUnitTestStructureEntity("blue_outpost");
        RestoreUnitTestStructureEntity("blue_base");
    }

    private void RestoreUnitTestStructureEntity(string entityId)
    {
        SimulationEntity? entity = World.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, entityId, StringComparison.OrdinalIgnoreCase));
        if (entity is null)
        {
            return;
        }

        entity.Health = entity.MaxHealth;
        entity.IsAlive = true;
        entity.PermanentEliminated = false;
        entity.State = "idle";
        entity.DestroyedTimeSec = double.NegativeInfinity;
        entity.RespawnTimerSec = 0.0;
        entity.WeakTimerSec = 0.0;
        entity.RespawnInvincibleTimerSec = 0.0;
    }

    private void MaintainUnitTestEnergyState()
    {
        SimulationTeamState teamState = World.GetOrCreateTeamState("red");
        ConfigureUnitTestEnergyTeamState(teamState);
        teamState.EnergyTestForceLarge = _unitTestEnergyForceLarge;
        if (!string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        teamState.EnergyMechanismState = "inactive";
        teamState.EnergyLargeMechanismActive = false;
        teamState.EnergyBuffTimerSec = 0.0;
        teamState.EnergyBuffDamageDealtMult = 1.0;
        teamState.EnergyBuffDamageTakenMult = 1.0;
        teamState.EnergyBuffCoolingMult = 1.0;
        teamState.EnergyActivationTimerSec = 0.0;
        teamState.EnergyVirtualHits = 0.0;
        teamState.EnergyActivationWindowTimerSec = 0.0;
        teamState.EnergyLitModuleTimerSec = 0.0;
        teamState.EnergyNextModuleDelaySec = 0.0;
        teamState.EnergyCurrentLitMask = 0;
    }

    private double ResolveMetersPerWorldUnit(MapPresetDefinition mapPreset)
    {
        if (string.IsNullOrWhiteSpace(mapPreset.SourcePath)
            && string.IsNullOrWhiteSpace(mapPreset.ImagePath)
            && string.IsNullOrWhiteSpace(mapPreset.AnnotationPath))
        {
            double presetScaleFromLength = mapPreset.FieldLengthM > 0 && mapPreset.Width > 0
                ? mapPreset.FieldLengthM / mapPreset.Width
                : 0.0;
            double presetScaleFromWidth = mapPreset.FieldWidthM > 0 && mapPreset.Height > 0
                ? mapPreset.FieldWidthM / mapPreset.Height
                : 0.0;
            if (presetScaleFromLength > 0 && presetScaleFromWidth > 0)
            {
                return (presetScaleFromLength + presetScaleFromWidth) * 0.5;
            }

            if (presetScaleFromLength > 0)
            {
                return presetScaleFromLength;
            }

            if (presetScaleFromWidth > 0)
            {
                return presetScaleFromWidth;
            }
        }

        JsonObject? map = _config["map"] as JsonObject;
        double fieldLengthM = ReadDouble(map?["field_length_m"], mapPreset.FieldLengthM);
        double fieldWidthM = ReadDouble(map?["field_width_m"], mapPreset.FieldWidthM);
        int width = (int)Math.Round(ReadDouble(map?["width"], mapPreset.Width));
        int height = (int)Math.Round(ReadDouble(map?["height"], mapPreset.Height));
        if (width <= 0)
        {
            width = mapPreset.Width;
        }

        if (height <= 0)
        {
            height = mapPreset.Height;
        }

        double scaleFromLength = fieldLengthM > 0 && width > 0 ? fieldLengthM / width : 0.0;
        double scaleFromWidth = fieldWidthM > 0 && height > 0 ? fieldWidthM / height : 0.0;
        if (scaleFromLength > 0 && scaleFromWidth > 0)
        {
            return (scaleFromLength + scaleFromWidth) * 0.5;
        }

        if (scaleFromLength > 0)
        {
            return scaleFromLength;
        }

        return scaleFromWidth > 0 ? scaleFromWidth : 0.0178;
    }

    private double ResolveInitialGold()
    {
        JsonObject? rules = _config["rules"] as JsonObject;
        if (rules?["economy"] is JsonObject economy)
        {
            return ReadDouble(economy["initial_gold"], 400.0);
        }

        return ReadDouble(rules?["initial_gold"], 400.0);
    }

    private RuleSimulationService BuildSimulationService(RuleSet rules)
    {
        var interactionService = new ArenaInteractionService(rules);
        return new RuleSimulationService(
            rules,
            interactionService,
            seed: 20260419,
            enableAutoMovement: false,
            enableAiCombat: _aiEnabled || IsDuelMode,
            canSeeAutoAimPlate: (world, shooter, target, plate) =>
                AutoAimVisibility.CanSeePlate(world, _runtimeGrid, shooter, target, plate),
            resolveProjectileObstacle: (world, shooter, projectile, startX, startY, startHeightM, endX, endY, endHeightM, obstacleCandidates) =>
                ResolveProjectileObstacle(
                    world,
                    shooter,
                    projectile,
                    startX,
                    startY,
                    startHeightM,
                    endX,
                    endY,
                    endHeightM,
                    obstacleCandidates),
            projectileRicochetEnabled: RicochetEnabled);
    }

    private ProjectileObstacleHit? ResolveProjectileObstacle(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationProjectile projectile,
        double startX,
        double startY,
        double startHeightM,
        double endX,
        double endY,
        double endHeightM,
        IReadOnlyList<SimulationEntity>? obstacleCandidates)
    {
        if (string.Equals(_projectilePhysicsBackend, "bepu", StringComparison.OrdinalIgnoreCase))
        {
            return _bepuProjectileObstacleBackend.ResolveHit(
                world,
                _runtimeGrid,
                shooter,
                projectile,
                startX,
                startY,
                startHeightM,
                endX,
                endY,
                endHeightM,
                obstacleCandidates,
                ResolveAppearanceProfile);
        }

        return ProjectileObstacleResolver.ResolveHit(
            world,
            _runtimeGrid,
            shooter,
            projectile,
            startX,
            startY,
            startHeightM,
            endX,
            endY,
            endHeightM,
            obstacleCandidates,
            ResolveAppearanceProfile);
    }

    private void ApplyPlayerControlState(PlayerControlState? state)
    {
        foreach (SimulationEntity entity in World.Entities)
        {
            if (!IsMovableEntity(entity))
            {
                continue;
            }

            entity.IsPlayerControlled = false;
            entity.MoveInputForward = 0;
            entity.MoveInputRight = 0;
            entity.IsFireCommandActive = false;
            entity.StepClimbModeActive = false;
            entity.JumpRequested = false;
            entity.AutoAimRequested = false;
            entity.AutoAimGuidanceOnly = false;
            entity.SmallGyroActive = false;
            entity.BuyAmmoRequested = false;
            entity.EnergyActivationRequested = false;
        }

        if (state is null || !state.Enabled)
        {
            return;
        }

        string? entityId = IsFocusSandboxMode
            ? SingleUnitTestFocusId
            : (!string.IsNullOrWhiteSpace(state.EntityId) ? state.EntityId : _selectedEntityId);
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return;
        }

        SimulationEntity? entityToControl = World.Entities.FirstOrDefault(entity =>
            string.Equals(entity.Id, entityId, StringComparison.OrdinalIgnoreCase)
            && IsMovableEntity(entity)
            && !entity.IsSimulationSuppressed
            && entity.IsAlive);
        if (entityToControl is null)
        {
            return;
        }

        entityToControl.IsPlayerControlled = true;
        entityToControl.MoveInputForward = Math.Clamp(state.MoveForward, -1.0, 1.0);
        entityToControl.MoveInputRight = Math.Clamp(state.MoveRight, -1.0, 1.0);
        entityToControl.IsFireCommandActive = state.FirePressed;
        entityToControl.StepClimbModeActive = state.StepClimbModeActive;
        entityToControl.JumpRequested = state.JumpRequested;
        entityToControl.AutoAimRequested = state.AutoAimPressed;
        entityToControl.AutoAimGuidanceOnly = state.AutoAimGuidanceOnly;
        entityToControl.SmallGyroActive = state.SmallGyroActive;
        entityToControl.BuyAmmoRequested = state.BuyAmmoRequested;
        entityToControl.EnergyActivationRequested = state.EnergyActivationPressed;
        if (string.Equals(entityToControl.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            bool rangedHero = string.Equals(
                RuleSet.NormalizeHeroMode(entityToControl.HeroPerformanceMode),
                "ranged_priority",
                StringComparison.OrdinalIgnoreCase);
            if (!rangedHero)
            {
                entityToControl.HeroDeploymentRequested = false;
                entityToControl.HeroDeploymentActive = false;
                entityToControl.HeroDeploymentHoldTimerSec = 0.0;
                entityToControl.HeroDeploymentExitHoldTimerSec = 0.0;
                entityToControl.HeroDeploymentLastPitchErrorDeg = 0.0;
            }
            else if (entityToControl.HeroDeploymentActive)
            {
                entityToControl.HeroDeploymentRequested = true;
                if (state.HeroDeployHoldPressed)
                {
                    entityToControl.HeroDeploymentExitHoldTimerSec += DeltaTimeSec;
                    if (entityToControl.HeroDeploymentExitHoldTimerSec >= 2.0)
                    {
                        entityToControl.HeroDeploymentRequested = false;
                        entityToControl.HeroDeploymentActive = false;
                        entityToControl.HeroDeploymentHoldTimerSec = 0.0;
                        entityToControl.HeroDeploymentExitHoldTimerSec = 0.0;
                        entityToControl.HeroDeploymentYawCorrectionDeg = 0.0;
                        entityToControl.HeroDeploymentPitchCorrectionDeg = 0.0;
                        entityToControl.HeroDeploymentLastPitchErrorDeg = 0.0;
                        entityToControl.HeroDeploymentCorrectionPlateId = null;
                    }
                }
                else
                {
                    entityToControl.HeroDeploymentExitHoldTimerSec = 0.0;
                }
            }
            else
            {
                entityToControl.HeroDeploymentExitHoldTimerSec = 0.0;
                entityToControl.HeroDeploymentRequested = state.HeroDeployHoldPressed;
                if (!entityToControl.HeroDeploymentRequested)
                {
                    entityToControl.HeroDeploymentActive = false;
                    entityToControl.HeroDeploymentHoldTimerSec = 0.0;
                }
            }
        }

        entityToControl.SuperCapEnabled = state.SuperCapActive;

        if (state.SentryStanceToggleRequested
            && string.Equals(entityToControl.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            string nextStance = RuleSet.NormalizeSentryStance(entityToControl.SentryStance) switch
            {
                "attack" => "move",
                "move" => "defense",
                _ => "attack",
            };
            entityToControl.SentryStance = nextStance;
            _sentryStance = nextStance;
        }

        entityToControl.TurretYawDeg = NormalizeDegrees(entityToControl.TurretYawDeg + state.TurretYawDeltaDeg);
        entityToControl.GimbalPitchDeg = Math.Clamp(entityToControl.GimbalPitchDeg + state.GimbalPitchDeltaDeg, -35.0, 35.0);
    }

    public string ToggleSelectedAutoAimTargetMode()
    {
        SimulationEntity? entity = SelectedEntity;
        if (entity is null || !IsMovableEntity(entity))
        {
            return "armor";
        }

        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            entity.HeroAutoAimMode = SimulationCombatMath.NormalizeHeroAutoAimMode(entity.HeroAutoAimMode) == "lob"
                ? "normal"
                : "lob";
            entity.AutoAimTargetMode = "armor";
            ResetAutoAimLockState(entity);
            return entity.HeroAutoAimMode;
        }

        entity.AutoAimTargetMode = string.Equals(entity.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            ? "armor"
            : "energy";
        ResetAutoAimLockState(entity);
        return entity.AutoAimTargetMode;
    }

    private static void ResetAutoAimLockState(SimulationEntity entity)
    {
        entity.AutoAimLocked = false;
        entity.AutoAimTargetId = null;
        entity.AutoAimPlateId = null;
        entity.AutoAimTargetKind = string.Equals(entity.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            ? "energy_disk"
            : "vehicle_armor";
        entity.AutoAimPlateDirection = string.Empty;
        entity.AutoAimAccuracy = 0.0;
        entity.AutoAimDistanceCoefficient = 0.0;
        entity.AutoAimMotionCoefficient = 0.0;
        entity.AutoAimLeadTimeSec = 0.0;
        entity.AutoAimLeadDistanceM = 0.0;
        entity.AutoAimAimPointX = 0.0;
        entity.AutoAimAimPointY = 0.0;
        entity.AutoAimAimPointHeightM = 0.0;
        entity.AutoAimObservedVelocityXMps = 0.0;
        entity.AutoAimObservedVelocityYMps = 0.0;
        entity.AutoAimObservedVelocityZMps = 0.0;
        entity.AutoAimObservedAngularVelocityRadPerSec = 0.0;
        entity.AutoAimHasSmoothedAim = false;
        entity.AutoAimLockKey = null;
    }

    private static bool IsMovableEntity(SimulationEntity entity)
    {
        return string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase);
    }

    private static double NormalizeDegrees(double degrees)
    {
        double value = degrees % 360.0;
        if (value < 0)
        {
            value += 360.0;
        }

        return value;
    }

    private void EnsureSelectedEntity()
    {
        if (IsMapComponentTestMode)
        {
            _selectedEntityId = null;
            return;
        }

        if (IsFocusSandboxMode)
        {
            EnsureSingleUnitTestFocus();
            _selectedEntityId = SingleUnitTestFocusEntity?.Id;
            SelectedTeam = _singleUnitTestTeam;
            return;
        }

        SimulationEntity? selected = null;
        if (!string.IsNullOrWhiteSpace(_selectedEntityId))
        {
            selected = World.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Id, _selectedEntityId, StringComparison.OrdinalIgnoreCase)
                && IsControlEntity(entity));
        }

        if (selected is not null)
        {
            SelectedTeam = Simulator3dOptions.NormalizeTeam(selected.Team);
            return;
        }

        SimulationEntity? replacement = GetControlCandidates(SelectedTeam).FirstOrDefault()
            ?? GetControlCandidates().FirstOrDefault();
        _selectedEntityId = replacement?.Id;
        if (replacement is not null)
        {
            SelectedTeam = Simulator3dOptions.NormalizeTeam(replacement.Team);
        }
    }

    private void EnsureSingleUnitTestFocus()
    {
        _singleUnitTestTeam = NormalizeFocusSandboxTeam(_singleUnitTestTeam);
        _singleUnitTestEntityKey = Simulator3dOptions.NormalizeSingleUnitEntityKey(_singleUnitTestEntityKey);
        if (IsDuelMode && string.Equals(_singleUnitTestEntityKey, "robot_2", StringComparison.OrdinalIgnoreCase))
        {
            _singleUnitTestEntityKey = "robot_3";
        }

        if (!IsFocusSandboxMode)
        {
            return;
        }

        SimulationEntity? focus = SingleUnitTestFocusEntity;
        if (focus is not null && IsControlEntity(focus))
        {
            _selectedEntityId = focus.Id;
            SelectedTeam = _singleUnitTestTeam;
            return;
        }

        SimulationEntity? sameTeamCandidate = GetControlCandidates(_singleUnitTestTeam).FirstOrDefault();
        if (sameTeamCandidate is not null
            && TryResolveTeamAndEntityKey(sameTeamCandidate.Id, out string candidateTeam, out string candidateKey))
        {
            _singleUnitTestTeam = candidateTeam;
            _singleUnitTestEntityKey = candidateKey;
            _selectedEntityId = sameTeamCandidate.Id;
            SelectedTeam = candidateTeam;
            return;
        }

        SimulationEntity? anyCandidate = GetControlCandidates().FirstOrDefault();
        if (anyCandidate is not null
            && TryResolveTeamAndEntityKey(anyCandidate.Id, out string fallbackTeam, out string fallbackKey))
        {
            _singleUnitTestTeam = fallbackTeam;
            _singleUnitTestEntityKey = fallbackKey;
            _selectedEntityId = anyCandidate.Id;
            SelectedTeam = fallbackTeam;
        }
    }

    private void ApplySingleUnitTestRuntimeFilters()
    {
        if (!IsFocusSandboxMode)
        {
            foreach (SimulationEntity entity in World.Entities)
            {
                entity.IsSimulationSuppressed = false;
                entity.TestForcedDecisionId = string.Empty;
                if (string.IsNullOrWhiteSpace(entity.AiDecision))
                {
                    entity.AiDecision = "idle";
                }
            }

            return;
        }

        string focusId = SingleUnitTestFocusId;
        foreach (SimulationEntity entity in World.Entities)
        {
            if (!IsMovableEntity(entity))
            {
                entity.IsSimulationSuppressed = false;
                continue;
            }

            bool allowActive = string.Equals(entity.Id, focusId, StringComparison.OrdinalIgnoreCase);
            if (IsDuelMode
                && string.Equals(entity.Id, DuelEnemyEntityId, StringComparison.OrdinalIgnoreCase))
            {
                allowActive = true;
            }

            bool suppressed = !allowActive;
            entity.IsSimulationSuppressed = suppressed;
            if (!suppressed)
            {
                if (string.Equals(entity.Id, focusId, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateSingleUnitFocusDecisionState(entity);
                }
                else if (string.IsNullOrWhiteSpace(entity.AiDecision))
                {
                    entity.AiDecision = "idle";
                }

                continue;
            }

            entity.IsPlayerControlled = false;
            entity.MoveInputForward = 0;
            entity.MoveInputRight = 0;
            entity.IsFireCommandActive = false;
            entity.StepClimbModeActive = false;
            entity.JumpRequested = false;
            entity.VelocityXWorldPerSec = 0;
            entity.VelocityYWorldPerSec = 0;
            entity.AngularVelocityDegPerSec = 0;
            entity.AiDecision = "单兵种测试待机";
            entity.AiDecisionSelected = string.Empty;
            entity.TestForcedDecisionId = string.Empty;
        }
    }

    private void ApplyMapComponentTestRuntimeFilters()
    {
        if (!IsMapComponentTestMode)
        {
            return;
        }

        for (int index = World.Entities.Count - 1; index >= 0; index--)
        {
            if (IsControlEntity(World.Entities[index]))
            {
                World.Entities.RemoveAt(index);
            }
        }
    }

    private void UpdateSingleUnitFocusDecisionState(SimulationEntity focus)
    {
        string forced = (focus.TestForcedDecisionId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(forced))
        {
            DecisionSpec? forcedSpec = GetSingleUnitTestDecisionSpecs()
                .FirstOrDefault(spec => string.Equals(spec.Id, forced, StringComparison.OrdinalIgnoreCase));
            if (forcedSpec is not null)
            {
                focus.AiDecisionSelected = forcedSpec.Id;
                focus.AiDecision = forcedSpec.Label;
                return;
            }

            focus.TestForcedDecisionId = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(focus.AiDecisionSelected)
            && !string.IsNullOrWhiteSpace(focus.AiDecision))
        {
            return;
        }

        IReadOnlyList<DecisionSpec> specs = GetSingleUnitTestDecisionSpecs();
        if (specs.Count == 0)
        {
            focus.AiDecision = "idle";
            focus.AiDecisionSelected = string.Empty;
            return;
        }

        DecisionSpec fallback = specs[0];
        focus.AiDecisionSelected = fallback.Id;
        focus.AiDecision = fallback.Label;
    }

    private void ClearAllForcedSingleUnitDecisions()
    {
        foreach (SimulationEntity entity in World.Entities)
        {
            entity.TestForcedDecisionId = string.Empty;
        }
    }

    private static bool TryResolveTeamAndEntityKey(string entityId, out string team, out string entityKey)
    {
        team = "red";
        entityKey = "robot_1";

        string trimmed = (entityId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        int separator = trimmed.IndexOf('_');
        if (separator <= 0 || separator >= trimmed.Length - 1)
        {
            return false;
        }

        string parsedTeam = Simulator3dOptions.NormalizeTeam(trimmed.Substring(0, separator));
        string parsedEntityKey = trimmed.Substring(separator + 1);
        if (!IsSingleUnitEntityKey(parsedEntityKey))
        {
            return false;
        }

        team = parsedTeam;
        entityKey = Simulator3dOptions.NormalizeSingleUnitEntityKey(parsedEntityKey);
        return true;
    }

    private static bool IsSingleUnitEntityKey(string entityKey)
    {
        return SingleUnitEntityKeys.Any(key => string.Equals(key, entityKey, StringComparison.OrdinalIgnoreCase));
    }

    private int FindPresetIndex(string preset)
    {
        for (int index = 0; index < AvailableMapPresets.Count; index++)
        {
            if (string.Equals(AvailableMapPresets[index], preset, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private string ResolvePreset(string? preferred)
    {
        string configured = _configurationService.GetMapPreset(_config);
        string? simulatorPreset = (_config["simulator"] as JsonObject)?["sim3d_map_preset"]?.GetValue<string>();

        foreach (string candidate in new[] { preferred ?? string.Empty, simulatorPreset ?? string.Empty, configured })
        {
            string trimmed = candidate.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (AvailableMapPresets.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return AvailableMapPresets.Count > 0 ? AvailableMapPresets[0] : configured;
    }

    private void PersistSimulatorSettings()
    {
        _configurationService.SetMapPreset(_config, ActiveMapPreset);
        JsonObject simulator = ConfigurationService.EnsureObject(_config, "simulator");
        simulator["sim3d_renderer_backend"] = ActiveRendererMode;
        simulator["terrain_scene_backend"] = ResolveBackendNameByMode(ActiveRendererMode);
        simulator["match_mode"] = _matchMode;
        simulator["sim3d_infantry_mode"] = _infantryMode;
        simulator["sim3d_hero_performance_mode"] = _heroPerformanceMode;
        simulator["sim3d_infantry_durability_mode"] = _infantryDurabilityMode;
        simulator["sim3d_infantry_weapon_mode"] = _infantryWeaponMode;
        simulator["sim3d_sentry_control_mode"] = _sentryControlMode;
        simulator["sim3d_sentry_stance"] = _sentryStance;
        simulator["sim3d_autoaim_accuracy_scale"] = _autoAimAccuracyScale;
        simulator["sim3d_ai_enabled"] = _aiEnabled;
        simulator["sim3d_projectile_entity_rendering"] = _solidProjectileRendering;
        simulator["sim3d_projectile_physics_backend"] = _projectilePhysicsBackend;
        simulator["sim3d_duel_round_limit"] = _duelRoundLimit;
        simulator["sim3d_unit_test_energy_force_large"] = _unitTestEnergyForceLarge;
        simulator["single_unit_test_team"] = _singleUnitTestTeam;
        simulator["single_unit_test_entity_key"] = _singleUnitTestEntityKey;
        simulator["sim3d_selected_team"] = SelectedTeam;
        simulator["sim3d_selected_entity_id"] = _selectedEntityId;
        simulator["player_projectile_ricochet_enabled"] = RicochetEnabled;

        IReadOnlyList<string> existing = _configurationService.ExistingConfigPaths(_layout);
        List<string> targets = existing.Count > 0
            ? existing.ToList()
            : new List<string> { _primaryConfigPath };

        foreach (string path in targets)
        {
            JsonObject current = string.Equals(path, _primaryConfigPath, StringComparison.OrdinalIgnoreCase)
                ? _config
                : _configurationService.LoadConfig(path);

            _configurationService.SetMapPreset(current, ActiveMapPreset);
            JsonObject currentSimulator = ConfigurationService.EnsureObject(current, "simulator");
            currentSimulator["sim3d_renderer_backend"] = ActiveRendererMode;
            currentSimulator["terrain_scene_backend"] = ResolveBackendNameByMode(ActiveRendererMode);
            currentSimulator["match_mode"] = _matchMode;
            currentSimulator["sim3d_infantry_mode"] = _infantryMode;
            currentSimulator["sim3d_hero_performance_mode"] = _heroPerformanceMode;
            currentSimulator["sim3d_infantry_durability_mode"] = _infantryDurabilityMode;
            currentSimulator["sim3d_infantry_weapon_mode"] = _infantryWeaponMode;
            currentSimulator["sim3d_sentry_control_mode"] = _sentryControlMode;
            currentSimulator["sim3d_sentry_stance"] = _sentryStance;
            currentSimulator["sim3d_autoaim_accuracy_scale"] = _autoAimAccuracyScale;
            currentSimulator["sim3d_ai_enabled"] = _aiEnabled;
            currentSimulator["sim3d_projectile_entity_rendering"] = _solidProjectileRendering;
            currentSimulator["sim3d_projectile_physics_backend"] = _projectilePhysicsBackend;
            currentSimulator["sim3d_duel_round_limit"] = _duelRoundLimit;
            currentSimulator["sim3d_unit_test_energy_force_large"] = _unitTestEnergyForceLarge;
            currentSimulator["single_unit_test_team"] = _singleUnitTestTeam;
            currentSimulator["single_unit_test_entity_key"] = _singleUnitTestEntityKey;
            currentSimulator["sim3d_selected_team"] = SelectedTeam;
            currentSimulator["sim3d_selected_entity_id"] = _selectedEntityId;
            currentSimulator["player_projectile_ricochet_enabled"] = RicochetEnabled;
            _configurationService.SaveConfig(path, current);
        }

        _decisionConfigLastWriteUtc = ReadLastWriteTimeUtc(_primaryConfigPath);
        _lastDecisionConfigProbeUtc = DateTime.UtcNow;
    }

    private void MaybeReloadDecisionDeploymentProfile()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastDecisionConfigProbeUtc).TotalSeconds < 2.0)
        {
            return;
        }

        _lastDecisionConfigProbeUtc = nowUtc;
        DateTime latestWriteUtc = ReadLastWriteTimeUtc(_primaryConfigPath);
        if (latestWriteUtc <= _decisionConfigLastWriteUtc)
        {
            return;
        }

        JsonObject latest = _configurationService.LoadConfig(_primaryConfigPath);
        _decisionDeploymentConfig = DecisionDeploymentConfig.LoadFromConfig(latest);
        _terrainMotionService = CreateTerrainMotionService();
        _decisionConfigLastWriteUtc = latestWriteUtc;
    }

    private static bool IsControlEntity(SimulationEntity entity)
    {
        if (!entity.IsAlive)
        {
            return false;
        }

        if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadBoolean(JsonNode? node, bool fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue(out string? text))
            {
                if (bool.TryParse(text, out bool parsed))
                {
                    return parsed;
                }

                if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "off", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return fallback;
    }

    private static double ReadDouble(JsonNode? node, double fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out double doubleValue))
            {
                return doubleValue;
            }

            if (value.TryGetValue(out int intValue))
            {
                return intValue;
            }

            if (value.TryGetValue(out string? text)
                && double.TryParse(text, out doubleValue))
            {
                return doubleValue;
            }
        }

        return fallback;
    }

    private static string ResolveBackendNameByMode(string mode)
    {
        return Simulator3dOptions.NormalizeRendererMode(mode) switch
        {
            "gpu" => "wgl_opengl",
            "opengl" => "editor_opengl",
            "native_cpp" => "native_cpp",
            _ => "pyglet_moderngl",
        };
    }

    private static string ResolveRendererMode(JsonObject config, string fallback)
    {
        JsonObject? simulator = config["simulator"] as JsonObject;
        string? configured = simulator?["sim3d_renderer_backend"]?.GetValue<string>()
            ?? simulator?["terrain_scene_backend"]?.GetValue<string>();
        return Simulator3dOptions.NormalizeRendererMode(configured ?? fallback);
    }

    private static IReadOnlyList<string> DiscoverMapPresets(ProjectLayout layout)
    {
        return new AssetCatalogService().ListMapPresets(layout);
    }

    private static DateTime ReadLastWriteTimeUtc(string path)
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private void ApplyAppearanceProfilesToWorld()
        => ApplyAppearanceProfilesToWorld(World, _appearanceCatalog, MapPreset, _infantryMode);

    private void ApplyAppearanceProfilesToWorld(
        SimulationWorldState world,
        AppearanceProfileCatalog appearanceCatalog,
        MapPresetDefinition mapPreset,
        string infantryMode)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        foreach (SimulationEntity entity in world.Entities)
        {
            if (!string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? subtype = entity.RoleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase)
                ? ResolveAppearanceSubtypeOverride(entity, infantryMode)
                : null;
            RobotAppearanceProfile profile = TryResolveFacilityAppearanceProfile(entity, mapPreset, appearanceCatalog)
                ?? appearanceCatalog.Resolve(entity.RoleKey, subtype);
            profile.ApplyToEntity(entity, metersPerWorldUnit);
        }
    }

    private string? ResolveAppearanceSubtypeOverride(SimulationEntity entity, string infantryMode)
    {
        if (string.Equals(entity.RoleKey, _previewRoleKey, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_previewSubtypeKey))
        {
            return _previewSubtypeKey;
        }

        return string.Equals(infantryMode, "balance", StringComparison.OrdinalIgnoreCase)
            ? "balance_legged"
            : "omni_wheel";
    }

    private RobotAppearanceProfile? TryResolveFacilityAppearanceProfile(
        SimulationEntity entity,
        MapPresetDefinition mapPreset,
        AppearanceProfileCatalog appearanceCatalog)
    {
        if (!SimulationCombatMath.IsStructure(entity))
        {
            return null;
        }

        FacilityRegion? region = mapPreset.Facilities.FirstOrDefault(candidate =>
        {
            if (!string.Equals(candidate.Type, entity.EntityType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(candidate.Id, entity.Id, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(candidate.Team, entity.Team, StringComparison.OrdinalIgnoreCase);
        });

        return region is null ? null : appearanceCatalog.ResolveFacilityProfile(region);
    }

    private string ResolveAppearancePath()
        => _appearancePathOverride ?? _layout.AppearancePresetPath;

    private void ApplyConfiguredRoleProfilesToWorld(bool resetHealth)
        => ApplyConfiguredRoleProfilesToWorld(World, resetHealth, _rules, CaptureLobbyWorldBuildSnapshot());

    private void ApplyConfiguredRoleProfilesToWorld(
        SimulationWorldState world,
        bool resetHealth,
        RuleSet rules,
        LobbyWorldBuildSnapshot snapshot)
    {
        foreach (SimulationEntity entity in world.Entities)
        {
            if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
            {
                entity.HeroPerformanceMode = snapshot.HeroPerformanceMode;
            }
            else if (string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase))
            {
                entity.InfantryDurabilityMode = snapshot.InfantryDurabilityMode;
                entity.InfantryWeaponMode = snapshot.InfantryWeaponMode;
            }
            else if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
            {
                entity.SentryControlMode = snapshot.SentryControlMode;
                entity.SentryStance = snapshot.SentryStance;
            }
            else
            {
                continue;
            }

            ResolvedRoleProfile profile = rules.ResolveRuntimeProfile(entity);
            entity.MaxLevel = profile.MaxLevel;
            entity.MaxHealth = profile.MaxHealth;
            entity.MaxPower = profile.MaxPower;
            entity.MaxHeat = profile.MaxHeat;
            entity.AutoAimAccuracyScale = snapshot.AutoAimAccuracyScale;
            entity.AmmoType = profile.AmmoType;
            entity.RuleDrivePowerLimitW = profile.MaxPower;
            entity.MaxChassisEnergy = profile.MaxChassisEnergy;
            entity.ChassisEnergy = profile.UsesChassisEnergy
                ? Math.Min(profile.MaxChassisEnergy, resetHealth ? profile.InitialChassisEnergy : entity.ChassisEnergy)
                : 0.0;
            entity.ChassisEcoPowerLimitW = profile.EcoPowerLimitW;
            entity.ChassisBoostThresholdEnergy = profile.BoostThresholdEnergy;
            entity.ChassisBoostMultiplier = profile.BoostPowerMultiplier;
            entity.ChassisBoostPowerCapW = profile.BoostPowerCapW;
            entity.MaxBufferEnergyJ = Math.Max(0.0, entity.MaxBufferEnergyJ <= 1e-6 ? 60.0 : entity.MaxBufferEnergyJ);
            entity.BufferReserveEnergyJ = Math.Clamp(entity.BufferReserveEnergyJ <= 1e-6 ? 10.0 : entity.BufferReserveEnergyJ, 0.0, entity.MaxBufferEnergyJ);
            entity.MaxSuperCapEnergyJ = Math.Max(0.0, entity.MaxSuperCapEnergyJ <= 1e-6 ? 2000.0 : entity.MaxSuperCapEnergyJ);

            if (resetHealth)
            {
                entity.Health = profile.MaxHealth;
                entity.Power = profile.MaxPower;
                entity.Heat = 0.0;
                entity.HeatLockInitialHeat = 0.0;
                entity.BufferEnergyJ = entity.MaxBufferEnergyJ;
                entity.SuperCapEnergyJ = entity.MaxSuperCapEnergyJ;
                ApplyInitialPurchasedAmmo(entity, profile);
            }
            else
            {
                entity.Health = Math.Min(entity.Health, entity.MaxHealth);
                entity.Power = Math.Min(entity.Power, entity.MaxPower);
                entity.Heat = Math.Min(entity.Heat, entity.MaxHeat);
                entity.BufferEnergyJ = Math.Min(entity.BufferEnergyJ, entity.MaxBufferEnergyJ);
                entity.SuperCapEnergyJ = Math.Min(entity.SuperCapEnergyJ, entity.MaxSuperCapEnergyJ);
            }
        }
    }

    private static string NormalizeInfantryMode(string? mode)
    {
        string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "balance" or "balance_legged" => "balance",
            _ => "full",
        };
    }

    private static string NormalizeProjectilePhysicsBackend(string? backend)
    {
        return string.Equals(backend, "bepu", StringComparison.OrdinalIgnoreCase) ? "bepu" : "native";
    }

    private static void ApplyInitialPurchasedAmmo(SimulationEntity entity, ResolvedRoleProfile profile)
    {
        entity.Ammo17Mm = 0;
        entity.Ammo42Mm = 0;
        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            entity.Ammo17Mm = profile.InitialAllowedAmmo17Mm;
            entity.Ammo42Mm = profile.InitialAllowedAmmo42Mm;
            return;
        }

        if (string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            entity.Ammo17Mm = 100;
            return;
        }

        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            entity.Ammo42Mm = 10;
        }
    }
}
