using System.Text.Json.Nodes;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed class Simulator3dHost
{
    private static readonly string[] SingleUnitEntityKeys =
    {
        "robot_1",
        "robot_2",
        "robot_3",
        "robot_4",
        "robot_7",
    };

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
    private readonly string _primaryConfigPath;
    private readonly JsonObject _config;
    private DateTime _lastDecisionConfigProbeUtc;
    private DateTime _decisionConfigLastWriteUtc;
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
    private string _singleUnitTestTeam = "red";
    private string _singleUnitTestEntityKey = "robot_1";

    public Simulator3dHost(Simulator3dOptions options)
    {
        _layout = ProjectLayout.Discover();
        _configurationService = new ConfigurationService();
        _mapPresetService = new MapPresetService();
        _ruleSetLoader = new RuleSetLoader();
        _bootstrapService = new SimulationBootstrapService();
        _runtimeGridLoader = new RuntimeGridLoader();

        _primaryConfigPath = _configurationService.ResolvePrimaryConfigPath(_layout);
        _config = _configurationService.LoadConfig(_primaryConfigPath);
        _decisionConfigLastWriteUtc = ReadLastWriteTimeUtc(_primaryConfigPath);
        JsonObject simulatorConfig = ConfigurationService.EnsureObject(_config, "simulator");
        _decisionDeploymentConfig = DecisionDeploymentConfig.LoadFromConfig(_config);

        AvailableMapPresets = DiscoverMapPresets(_layout);
        ActiveRendererMode = ResolveRendererMode(_config, options.RendererMode ?? "moderngl");
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
        _singleUnitTestTeam = Simulator3dOptions.NormalizeTeam(options.SingleUnitTestTeam ?? simulatorConfig["single_unit_test_team"]?.ToString());
        _singleUnitTestEntityKey = Simulator3dOptions.NormalizeSingleUnitEntityKey(options.SingleUnitTestEntityKey ?? simulatorConfig["single_unit_test_entity_key"]?.ToString());
        ActiveMapPreset = ResolvePreset(options.MapPreset);
        DeltaTimeSec = options.DeltaTimeSec;
        SelectedTeam = Simulator3dOptions.NormalizeTeam(options.SelectedTeam ?? simulatorConfig["sim3d_selected_team"]?.ToString());
        _selectedEntityId = string.IsNullOrWhiteSpace(options.SelectedEntityId)
            ? simulatorConfig["sim3d_selected_entity_id"]?.ToString()
            : options.SelectedEntityId;
        RicochetEnabled = options.RicochetEnabled
            ?? ReadBoolean(simulatorConfig["player_projectile_ricochet_enabled"], fallback: true);

        _rules = _ruleSetLoader.LoadFromConfig(_config);
        _simulationService = BuildSimulationService(_rules);
        _terrainMotionService = new TerrainMotionService(_rules, _decisionDeploymentConfig);
        MapPreset = _mapPresetService.LoadPreset(_layout, ActiveMapPreset);
        _appearanceCatalog = AppearanceProfileCatalog.Load(_layout.AppearancePresetPath);
        _runtimeGrid = _runtimeGridLoader.TryLoad(MapPreset, out _runtimeGridWarning);
        World = _bootstrapService.BuildInitialWorld(_config, _rules, MapPreset);
        ApplyConfiguredRoleProfilesToWorld(resetHealth: true);
        ApplyAppearanceProfilesToWorld();
        EnsureSingleUnitTestFocus();
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

    public string MatchMode => _matchMode;

    public string InfantryMode => _infantryMode;

    public string HeroPerformanceMode => _heroPerformanceMode;

    public string InfantryDurabilityMode => _infantryDurabilityMode;

    public string InfantryWeaponMode => _infantryWeaponMode;

    public string SentryControlMode => _sentryControlMode;

    public string SentryStance => _sentryStance;

    public double AutoAimAccuracyScale => _autoAimAccuracyScale;

    public bool IsSingleUnitTestMode => string.Equals(_matchMode, "single_unit_test", StringComparison.OrdinalIgnoreCase);

    public string SingleUnitTestTeam => _singleUnitTestTeam;

    public string SingleUnitTestEntityKey => _singleUnitTestEntityKey;

    public string SingleUnitTestFocusId => $"{_singleUnitTestTeam}_{_singleUnitTestEntityKey}";

    public string SelectedTeam { get; private set; }

    public bool RicochetEnabled { get; private set; }

    public MapPresetDefinition MapPreset { get; private set; }

    public SimulationWorldState World { get; private set; }

    public RuntimeGridData? RuntimeGrid => _runtimeGrid;

    public string? RuntimeGridWarning => _runtimeGridWarning;

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
        return _appearanceCatalog.Resolve(entity.RoleKey, subtype);
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
        EnsureSingleUnitTestFocus();
        ApplySingleUnitTestRuntimeFilters();
        EnsureSelectedEntity();
        PersistSimulatorSettings();
        return true;
    }

    public bool SetInfantryMode(string mode)
    {
        string normalized = NormalizeInfantryMode(mode);
        if (string.Equals(_infantryMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _infantryMode = normalized;
        RebuildWorld();
        PersistSimulatorSettings();
        return true;
    }

    public bool SetHeroPerformanceMode(string mode)
    {
        string normalized = RuleSet.NormalizeHeroMode(mode);
        if (string.Equals(_heroPerformanceMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _heroPerformanceMode = normalized;
        RebuildWorld();
        PersistSimulatorSettings();
        return true;
    }

    public bool SetInfantryDurabilityMode(string mode)
    {
        string normalized = RuleSet.NormalizeInfantryDurabilityMode(mode);
        if (string.Equals(_infantryDurabilityMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _infantryDurabilityMode = normalized;
        RebuildWorld();
        PersistSimulatorSettings();
        return true;
    }

    public bool SetInfantryWeaponMode(string mode)
    {
        string normalized = RuleSet.NormalizeInfantryWeaponMode(mode);
        if (string.Equals(_infantryWeaponMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _infantryWeaponMode = normalized;
        RebuildWorld();
        PersistSimulatorSettings();
        return true;
    }

    public bool SetSentryControlMode(string mode)
    {
        string normalized = RuleSet.NormalizeSentryControlMode(mode);
        if (string.Equals(_sentryControlMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _sentryControlMode = normalized;
        RebuildWorld();
        PersistSimulatorSettings();
        return true;
    }

    public bool SetSentryStance(string stance)
    {
        string normalized = RuleSet.NormalizeSentryStance(stance);
        if (string.Equals(_sentryStance, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _sentryStance = normalized;
        RebuildWorld();
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

    public bool SetSingleUnitTestFocus(string? team = null, string? entityKey = null)
    {
        bool changed = false;
        if (!string.IsNullOrWhiteSpace(team))
        {
            string normalizedTeam = Simulator3dOptions.NormalizeTeam(team);
            if (!string.Equals(_singleUnitTestTeam, normalizedTeam, StringComparison.OrdinalIgnoreCase))
            {
                _singleUnitTestTeam = normalizedTeam;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(entityKey))
        {
            string normalizedEntity = Simulator3dOptions.NormalizeSingleUnitEntityKey(entityKey);
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

    public void Step(PlayerControlState? playerControlState = null)
    {
        MaybeReloadDecisionDeploymentProfile();
        EnsureSingleUnitTestFocus();
        ApplyPlayerControlState(playerControlState);
        ApplySingleUnitTestRuntimeFilters();
        _terrainMotionService.Step(World, _runtimeGrid, DeltaTimeSec);
        ApplySingleUnitTestRuntimeFilters();
        LastReport = _simulationService.Run(
            World,
            MapPreset.Facilities,
            DeltaTimeSec,
            DeltaTimeSec,
            captureFinalEntities: false);
        ApplySingleUnitTestRuntimeFilters();
        EnsureSelectedEntity();
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

    public void SetSelectedTeam(string team)
    {
        string normalized = Simulator3dOptions.NormalizeTeam(team);
        if (IsSingleUnitTestMode)
        {
            SetSingleUnitTestFocus(team: normalized);
            return;
        }

        if (string.Equals(SelectedTeam, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedTeam = normalized;
        EnsureSelectedEntity();
        PersistSimulatorSettings();
    }

    public bool SetSelectedEntity(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return false;
        }

        if (IsSingleUnitTestMode)
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
        _terrainMotionService = new TerrainMotionService(_rules, _decisionDeploymentConfig);
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
        if (IsSingleUnitTestMode)
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

        MapPreset = _mapPresetService.LoadPreset(_layout, ActiveMapPreset);
        _appearanceCatalog = AppearanceProfileCatalog.Load(_layout.AppearancePresetPath);
        _runtimeGrid = _runtimeGridLoader.TryLoad(MapPreset, out _runtimeGridWarning);
        _rules = _ruleSetLoader.LoadFromConfig(_config);
        _decisionDeploymentConfig = DecisionDeploymentConfig.LoadFromConfig(_config);
        _simulationService = BuildSimulationService(_rules);
        _terrainMotionService = new TerrainMotionService(_rules, _decisionDeploymentConfig);
        World = _bootstrapService.BuildInitialWorld(_config, _rules, MapPreset);
        ApplyConfiguredRoleProfilesToWorld(resetHealth: true);
        ApplyAppearanceProfilesToWorld();
        EnsureSingleUnitTestFocus();
        ApplySingleUnitTestRuntimeFilters();
        _selectedEntityId = previousSelection;
        SelectedTeam = previousTeam;
        LastReport = null;
        EnsureSelectedEntity();
    }

    private RuleSimulationService BuildSimulationService(RuleSet rules)
    {
        var interactionService = new ArenaInteractionService(rules);
        return new RuleSimulationService(
            rules,
            interactionService,
            seed: 20260419,
            enableAutoMovement: false,
            canSeeAutoAimPlate: (world, shooter, target, plate) =>
                AutoAimVisibility.CanSeePlate(world, _runtimeGrid, shooter, target, plate),
            resolveProjectileObstacle: (world, shooter, projectile, startX, startY, startHeightM, endX, endY, endHeightM) =>
                ProjectileObstacleResolver.ResolveHit(
                    world,
                    _runtimeGrid,
                    shooter,
                    projectile,
                    startX,
                    startY,
                    startHeightM,
                    endX,
                    endY,
                    endHeightM),
            projectileRicochetEnabled: RicochetEnabled);
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
        }

        if (state is null || !state.Enabled)
        {
            return;
        }

        string? entityId = IsSingleUnitTestMode
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
        if (state.HeroDeployToggleRequested && string.Equals(entityToControl.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            bool rangedHero = string.Equals(
                RuleSet.NormalizeHeroMode(entityToControl.HeroPerformanceMode),
                "ranged_priority",
                StringComparison.OrdinalIgnoreCase);
            entityToControl.HeroDeploymentRequested = rangedHero && !entityToControl.HeroDeploymentRequested;
            if (!entityToControl.HeroDeploymentRequested)
            {
                entityToControl.HeroDeploymentActive = false;
                entityToControl.HeroDeploymentHoldTimerSec = 0.0;
            }
        }

        if (state.SuperCapToggleRequested)
        {
            entityToControl.SuperCapEnabled = !entityToControl.SuperCapEnabled;
        }

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
        if (IsSingleUnitTestMode)
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
        _singleUnitTestTeam = Simulator3dOptions.NormalizeTeam(_singleUnitTestTeam);
        _singleUnitTestEntityKey = Simulator3dOptions.NormalizeSingleUnitEntityKey(_singleUnitTestEntityKey);

        if (!IsSingleUnitTestMode)
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
        if (!IsSingleUnitTestMode)
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

            bool suppressed = !string.Equals(entity.Id, focusId, StringComparison.OrdinalIgnoreCase);
            entity.IsSimulationSuppressed = suppressed;
            if (!suppressed)
            {
                UpdateSingleUnitFocusDecisionState(entity);
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
        if ((nowUtc - _lastDecisionConfigProbeUtc).TotalMilliseconds < 250)
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
        _terrainMotionService = new TerrainMotionService(_rules, _decisionDeploymentConfig);
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
        var presets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string mapsRoot = layout.ResolvePath("maps");
        if (Directory.Exists(mapsRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(mapsRoot))
            {
                string name = Path.GetFileName(directory);
                string mapFile = Path.Combine(directory, "map.json");
                if (!string.IsNullOrWhiteSpace(name) && File.Exists(mapFile))
                {
                    presets.Add(name);
                }
            }
        }

        string presetRoot = layout.ResolvePath("map_presets");
        if (Directory.Exists(presetRoot))
        {
            foreach (string file in Directory.EnumerateFiles(presetRoot, "*.json"))
            {
                string? name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    presets.Add(name);
                }
            }
        }

        return presets.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static DateTime ReadLastWriteTimeUtc(string path)
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private void ApplyAppearanceProfilesToWorld()
    {
        double metersPerWorldUnit = Math.Max(World.MetersPerWorldUnit, 1e-6);
        foreach (SimulationEntity entity in World.Entities)
        {
            if (!string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? subtype = entity.RoleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase)
                ? InfantryAppearanceSubtype
                : null;
            RobotAppearanceProfile profile = _appearanceCatalog.Resolve(entity.RoleKey, subtype);
            profile.ApplyToEntity(entity, metersPerWorldUnit);
        }
    }

    private void ApplyConfiguredRoleProfilesToWorld(bool resetHealth)
    {
        foreach (SimulationEntity entity in World.Entities)
        {
            if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
            {
                entity.HeroPerformanceMode = _heroPerformanceMode;
            }
            else if (string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase))
            {
                entity.InfantryDurabilityMode = _infantryDurabilityMode;
                entity.InfantryWeaponMode = _infantryWeaponMode;
            }
            else if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
            {
                entity.SentryControlMode = _sentryControlMode;
                entity.SentryStance = _sentryStance;
            }
            else
            {
                continue;
            }

            ResolvedRoleProfile profile = _rules.ResolveRuntimeProfile(entity);
            entity.MaxLevel = profile.MaxLevel;
            entity.MaxHealth = profile.MaxHealth;
            entity.MaxPower = profile.MaxPower;
            entity.MaxHeat = profile.MaxHeat;
            entity.AutoAimAccuracyScale = _autoAimAccuracyScale;
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
            entity.BufferReserveEnergyJ = Math.Clamp(entity.BufferReserveEnergyJ <= 1e-6 ? 30.0 : entity.BufferReserveEnergyJ, 0.0, entity.MaxBufferEnergyJ);
            entity.MaxSuperCapEnergyJ = Math.Max(0.0, entity.MaxSuperCapEnergyJ <= 1e-6 ? 2000.0 : entity.MaxSuperCapEnergyJ);

            if (resetHealth)
            {
                entity.Health = profile.MaxHealth;
                entity.Power = profile.MaxPower;
                entity.Heat = 0.0;
                entity.BufferEnergyJ = entity.MaxBufferEnergyJ;
                entity.SuperCapEnergyJ = entity.MaxSuperCapEnergyJ;
                bool sentry = string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase);
                entity.Ammo17Mm = sentry ? profile.InitialAllowedAmmo17Mm : 0;
                entity.Ammo42Mm = sentry ? profile.InitialAllowedAmmo42Mm : 0;
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
}
