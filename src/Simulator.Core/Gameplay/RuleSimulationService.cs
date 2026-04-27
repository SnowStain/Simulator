using System.Numerics;
using Simulator.Core;
using Simulator.Core.Engine;

namespace Simulator.Core.Gameplay;

public sealed record SimulationCombatEvent(
    double TimeSec,
    string ShooterId,
    string TargetId,
    string AmmoType,
    double DistanceM,
    double HitProbability,
    bool Hit,
    double Damage,
    string Message,
    bool CriticalHit = false,
    bool DamagePrevented = false,
    string DamagePreventedReason = "",
    string PlateId = "");

public sealed record SimulationShotEvent(
    double TimeSec,
    string ShooterId,
    string Team,
    string AmmoType,
    string TargetId,
    string PlateId,
    bool PlayerControlled,
    bool AutoAim);

public sealed record SimulationLifecycleEvent(
    double TimeSec,
    string EntityId,
    string Team,
    string EventType,
    string Message);

public sealed class SimulationRunReport
{
    public double DurationSec { get; init; }

    public double DeltaTimeSec { get; init; }

    public int TotalShots { get; set; }

    public int HitShots { get; set; }

    public int InteractionEventCount { get; set; }

    public IList<SimulationCombatEvent> CombatEvents { get; } = new List<SimulationCombatEvent>();

    public IList<SimulationShotEvent> ShotEvents { get; } = new List<SimulationShotEvent>();

    public IList<FacilityInteractionEvent> InteractionEvents { get; } = new List<FacilityInteractionEvent>();

    public IList<SimulationLifecycleEvent> LifecycleEvents { get; } = new List<SimulationLifecycleEvent>();

    public IList<SimulationEntity> FinalEntities { get; } = new List<SimulationEntity>();
}

public sealed class RuleSimulationService
{
    private const double ProjectileDeleteSpeedMps = 5.0;
    private const double MinimumEffectiveDamage = 1.0;
    private const double MinArmorDamageSpeed17Mps = 12.0;
    private const double MinArmorDamageSpeed42Mps = 10.0;
    private const double SurfaceAcceptedHitInterval17Sec = 0.05;
    private const double SurfaceAcceptedHitInterval42Sec = 0.20;
    private const double SurfaceAcceptedHitIntervalEnergySec = 0.0;

    private const double RespawnRecoveryZoneLockSec = 3.0;
    private sealed class ProjectileFrameCache
    {
        public required Dictionary<string, SimulationEntity> EntityById { get; init; }

        public required IReadOnlyList<SimulationEntity> RedTeamTargets { get; init; }

        public required IReadOnlyList<SimulationEntity> BlueTeamTargets { get; init; }

        public required IReadOnlyList<SimulationEntity> DamageTargets { get; init; }

        public required IReadOnlyList<SimulationEntity> ObstacleCandidates { get; init; }

        public required IReadOnlyDictionary<string, IReadOnlyList<ArmorPlateTarget>> ArmorTargetsByEntityId { get; init; }

        public List<SimulationEntity> DirectionalDamageCandidates { get; } = new(8);

        public List<SimulationEntity> DirectionalObstacleCandidates { get; } = new(8);

        public IReadOnlyList<SimulationEntity> GetTargetCandidates(string shooterTeam)
            => string.Equals(shooterTeam, "red", StringComparison.OrdinalIgnoreCase)
                ? BlueTeamTargets
                : RedTeamTargets;
    }

    private readonly RuleSet _rules;
    private readonly ArenaInteractionService _interactionService;
    private readonly bool _enableAutoMovement;
    private readonly bool _enableAiCombat;
    private readonly Func<SimulationWorldState, SimulationEntity, SimulationEntity, ArmorPlateTarget, bool>? _canSeeAutoAimPlate;
    private readonly Func<SimulationWorldState, SimulationEntity, SimulationProjectile, double, double, double, double, double, double, IReadOnlyList<SimulationEntity>?, ProjectileObstacleHit?>? _resolveProjectileObstacle;
    private readonly bool _projectileRicochetEnabled;
    private long _lastRulePerfLogTicks;

    private struct RuleFramePerf
    {
        public long RespawnTicks;

        public long InteractionTicks;

        public long CombatTicks;
    }

    public RuleSimulationService(
        RuleSet rules,
        ArenaInteractionService interactionService,
        int? seed = null,
        bool enableAutoMovement = true,
        bool enableAiCombat = true,
        Func<SimulationWorldState, SimulationEntity, SimulationEntity, ArmorPlateTarget, bool>? canSeeAutoAimPlate = null,
        Func<SimulationWorldState, SimulationEntity, SimulationProjectile, double, double, double, double, double, double, IReadOnlyList<SimulationEntity>?, ProjectileObstacleHit?>? resolveProjectileObstacle = null,
        bool projectileRicochetEnabled = false)
    {
        _rules = rules;
        _interactionService = interactionService;
        _enableAutoMovement = enableAutoMovement;
        _enableAiCombat = enableAiCombat;
        _canSeeAutoAimPlate = canSeeAutoAimPlate;
        _resolveProjectileObstacle = resolveProjectileObstacle;
        _projectileRicochetEnabled = projectileRicochetEnabled;
    }

    public SimulationRunReport Run(
        SimulationWorldState world,
        IReadOnlyList<Simulator.Core.Map.FacilityRegion> facilities,
        double durationSec,
        double deltaTimeSec,
        bool captureFinalEntities = true,
        bool enableCombat = true)
    {
        double dt = Math.Clamp(deltaTimeSec, 0.01, 0.2);
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(durationSec, dt) / dt));

        var report = new SimulationRunReport
        {
            DurationSec = steps * dt,
            DeltaTimeSec = dt,
        };

        var perf = new RuleFramePerf();
        long totalStartTicks = SimulatorRuntimePerformance.Timestamp();
        for (int step = 0; step < steps; step++)
        {
            StepRuleFrame(world, facilities, dt, enableCombat, report, ref perf);
        }

        LogRulePerfIfDue(
            SimulatorRuntimePerformance.ElapsedTicksSince(totalStartTicks),
            perf.RespawnTicks,
            perf.InteractionTicks,
            perf.CombatTicks,
            world.Entities.Count,
            world.Projectiles.Count);

        if (captureFinalEntities)
        {
            CaptureFinalEntities(world, report);
        }

        return report;
    }

    private void StepRuleFrame(
        SimulationWorldState world,
        IReadOnlyList<Simulator.Core.Map.FacilityRegion> facilities,
        double dt,
        bool enableCombat,
        SimulationRunReport report,
        ref RuleFramePerf perf)
    {
        world.GameTimeSec += dt;

        long segmentStartTicks = SimulatorRuntimePerformance.Timestamp();
        if (enableCombat)
        {
            TickCombat(world, dt, report);
        }

        perf.CombatTicks += SimulatorRuntimePerformance.ElapsedTicksSince(segmentStartTicks);

        segmentStartTicks = SimulatorRuntimePerformance.Timestamp();
        TickRespawnAndRecovery(world, facilities, dt, report);
        perf.RespawnTicks += SimulatorRuntimePerformance.ElapsedTicksSince(segmentStartTicks);

        if (_enableAutoMovement)
        {
            TickAutoMovement(world, dt);
        }

        segmentStartTicks = SimulatorRuntimePerformance.Timestamp();
        IReadOnlyList<FacilityInteractionEvent> interactionEvents =
            _interactionService.UpdateWorld(world, facilities, dt);
        perf.InteractionTicks += SimulatorRuntimePerformance.ElapsedTicksSince(segmentStartTicks);

        foreach (FacilityInteractionEvent interactionEvent in interactionEvents)
        {
            report.InteractionEvents.Add(interactionEvent);
        }

        report.InteractionEventCount += interactionEvents.Count;
    }

    private static void CaptureFinalEntities(SimulationWorldState world, SimulationRunReport report)
    {
        foreach (SimulationEntity entity in world.Entities)
        {
            report.FinalEntities.Add(CloneEntity(entity));
        }
    }

    private void LogRulePerfIfDue(
        long totalTicks,
        long respawnTicks,
        long interactionTicks,
        long combatTicks,
        int entityCount,
        int projectileCount)
    {
        if (!SimulatorRuntimePerformance.TryMarkInterval(ref _lastRulePerfLogTicks, 2.0))
        {
            return;
        }

        string line =
            $"{SimulatorRuntimePerformance.WallClockLabel()} "
            + $"total={SimulatorRuntimePerformance.FormatMilliseconds(totalTicks)}ms "
            + $"respawn={SimulatorRuntimePerformance.FormatMilliseconds(respawnTicks)}ms "
            + $"interaction={SimulatorRuntimePerformance.FormatMilliseconds(interactionTicks)}ms "
            + $"combat={SimulatorRuntimePerformance.FormatMilliseconds(combatTicks)}ms "
            + $"entities={entityCount} projectiles={projectileCount}";
        SimulatorRuntimeLog.Append("rule_perf.log", line);
    }

    private void TickAutoMovement(SimulationWorldState world, double dt)
    {
        foreach (SimulationEntity entity in world.Entities)
        {
            if (!entity.IsAlive
                || entity.IsSimulationSuppressed
                || string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entity.IsPlayerControlled)
            {
                entity.AutoAimTargetMode = "armor";
                entity.AutoAimTargetKind = "vehicle_armor";
            }

            SimulationEntity? enemy = FindNearestEnemy(world, entity);
            if (enemy is null)
            {
                continue;
            }

            double dx = enemy.X - entity.X;
            double dy = enemy.Y - entity.Y;
            double distanceWorld = Math.Sqrt(dx * dx + dy * dy);
            if (distanceWorld <= 1e-6)
            {
                continue;
            }

            double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
            double desiredDistanceM = Math.Min(5.5, _rules.Combat.AutoAimMaxDistanceM * 0.8);
            double distanceM = distanceWorld * metersPerWorldUnit;
            if (distanceM <= desiredDistanceM)
            {
                continue;
            }

            double speedMps = ResolveMoveSpeedMps(entity);
            double stepDistanceWorld = speedMps * dt / metersPerWorldUnit;
            double usableDistanceWorld = Math.Max(0.0, distanceWorld - desiredDistanceM / metersPerWorldUnit);
            double movement = Math.Min(stepDistanceWorld, usableDistanceWorld);
            if (movement <= 1e-6)
            {
                continue;
            }

            entity.X += dx / distanceWorld * movement;
            entity.Y += dy / distanceWorld * movement;
        }
    }

    private void TickRespawnAndRecovery(
        SimulationWorldState world,
        IReadOnlyList<Simulator.Core.Map.FacilityRegion> facilities,
        double dt,
        SimulationRunReport report)
    {
        foreach (SimulationEntity entity in world.Entities)
        {
            if (entity.IsSimulationSuppressed)
            {
                continue;
            }

            if (!entity.IsAlive)
            {
                if (entity.PermanentEliminated)
                {
                    continue;
                }

                if (entity.RespawnTimerSec > 0)
                {
                    double respawnRate = ResolveRespawnProgressRate(world, facilities, entity);
                    entity.RespawnTimerSec = Math.Max(0.0, entity.RespawnTimerSec - dt * respawnRate);
                    if (entity.RespawnTimerSec <= 0)
                    {
                        RespawnEntity(entity);
                        report.LifecycleEvents.Add(new SimulationLifecycleEvent(
                            world.GameTimeSec,
                            entity.Id,
                            entity.Team,
                            "respawn",
                            $"{entity.Id} respawned"));
                    }
                }

                continue;
            }

            if (SimulationCombatMath.IsStructure(entity))
            {
                continue;
            }

            ResolvedRoleProfile profile = _rules.ResolveRuntimeProfile(entity);
            ApplyResolvedRoleProfile(entity, profile, clampHealthToCurrent: true);
            EnsureOpeningPurchasedAmmo(entity);
            entity.FireCooldownSec = Math.Max(0.0, entity.FireCooldownSec - dt);
            entity.RespawnInvincibleTimerSec = Math.Max(0.0, entity.RespawnInvincibleTimerSec - dt);
            entity.HeatLockTimerSec = Math.Max(0.0, entity.HeatLockTimerSec - dt);
            if (ShouldRequireRespawnRecoveryZone(entity))
            {
                bool inFriendlySupply = IsInFriendlyRespawnRecoveryZone(facilities, entity);
                if (inFriendlySupply)
                {
                    entity.WeakTimerSec = Math.Max(0.0, entity.WeakTimerSec - dt);
                    entity.RespawnAmmoLockTimerSec = 0.0;
                }
            }
            else
            {
                entity.WeakTimerSec = Math.Max(0.0, entity.WeakTimerSec - dt);
                entity.RespawnAmmoLockTimerSec = Math.Max(0.0, entity.RespawnAmmoLockTimerSec - dt);
            }

            if (string.Equals(entity.State, "heat_locked", StringComparison.OrdinalIgnoreCase)
                && entity.Heat > 0.01)
            {
                if (entity.HeatLockInitialHeat <= 1e-6)
                {
                    entity.HeatLockInitialHeat = Math.Max(entity.Heat, entity.MaxHeat);
                }

                entity.HeatLockTimerSec = Math.Max(entity.HeatLockTimerSec, 0.10);
            }

            entity.Power = Math.Min(
                entity.MaxPower,
                entity.Power + profile.PowerRecoveryRate * entity.DynamicPowerRecoveryMult * dt);
            entity.Heat = Math.Max(0.0, entity.Heat - profile.HeatDissipationRate * entity.DynamicCoolingMult * dt);

            if ((entity.HeatLockTimerSec <= 0.0 || string.Equals(entity.State, "heat_locked", StringComparison.OrdinalIgnoreCase))
                && entity.Heat <= 0.01
                && string.Equals(entity.State, "heat_locked", StringComparison.OrdinalIgnoreCase))
            {
                entity.HeatLockTimerSec = 0.0;
                entity.HeatLockInitialHeat = 0.0;
                entity.State = entity.WeakTimerSec > 0 ? "weak" : "idle";
            }

            if (entity.WeakTimerSec <= 0.0
                && entity.RespawnAmmoLockTimerSec <= 0.0
                && string.Equals(entity.State, "weak", StringComparison.OrdinalIgnoreCase))
            {
                entity.State = "idle";
            }

            if (!entity.IsPlayerControlled
                && world.GameTimeSec <= 18.0
                && (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase))
                && !string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase)
                && ((string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) && entity.Ammo42Mm <= 0)
                    || (!string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) && entity.Ammo17Mm <= 0)))
            {
                entity.BuyAmmoRequested = true;
            }
        }
    }

    private void TickCombat(SimulationWorldState world, double dt, SimulationRunReport report)
    {
        foreach (SimulationEntity shooter in world.Entities)
        {
            if (!CanShoot(shooter) || shooter.FireCooldownSec > 1e-6)
            {
                continue;
            }

            if (!shooter.IsPlayerControlled && !_enableAiCombat)
            {
                ClearAutoAimState(shooter);
                shooter.IsFireCommandActive = false;
                continue;
            }

            if (WouldNextShotExceedHeatLimit(shooter) || !HasAmmoForShot(shooter))
            {
                continue;
            }

            SimulationEntity? preferredTarget = null;
            ArmorPlateTarget preferredPlate = default;
            AutoAimSolution preferredSolution = default;
            bool hasPreferredSolution = false;
            bool heroLobMode = string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                && SimulationCombatMath.IsHeroLobAutoAimMode(shooter);
            bool heroLobGuidanceManualFire = heroLobMode
                && shooter.IsPlayerControlled
                && shooter.AutoAimGuidanceOnly
                && shooter.IsFireCommandActive;
            bool heroDeploymentAutoFire = shooter.HeroDeploymentActive
                && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase);
            bool shouldAutoAim = heroDeploymentAutoFire || !shooter.IsPlayerControlled || shooter.AutoAimRequested;
            double autoAimMaxDistanceM = heroLobMode
                ? 1000.0
                : _rules.Combat.AutoAimMaxDistanceM;
            bool hasPreferredTarget = false;
            if (shouldAutoAim
                && TryResolveLockedArmorTarget(world, shooter, autoAimMaxDistanceM, out preferredTarget, out preferredPlate))
            {
                hasPreferredTarget = true;
            }
            else if (shouldAutoAim
                && heroLobMode
                && shooter.AutoAimLocked
                && IsHeroLobStructureTargetKind(shooter.AutoAimTargetKind))
            {
                continue;
            }
            else if (shouldAutoAim && !HasRoughAutoAimCandidate(world, shooter, autoAimMaxDistanceM))
            {
                ClearAutoAimState(shooter);
                continue;
            }

            if (!hasPreferredTarget)
            {
                hasPreferredTarget = shouldAutoAim
                    && SimulationCombatMath.TryAcquireAutoAimTarget(
                        world,
                        shooter,
                        autoAimMaxDistanceM,
                        out preferredTarget,
                        out preferredPlate,
                        (candidate, candidatePlate) => _canSeeAutoAimPlate?.Invoke(world, shooter, candidate, candidatePlate) ?? true);
            }

            if (!shooter.IsPlayerControlled && !hasPreferredTarget)
            {
                continue;
            }

            if (heroDeploymentAutoFire && !hasPreferredTarget)
            {
                ClearAutoAimState(shooter);
                continue;
            }

            if (shooter.IsPlayerControlled && !shouldAutoAim)
            {
                ClearAutoAimState(shooter);
            }
            else if (shooter.IsPlayerControlled && shouldAutoAim && !hasPreferredTarget)
            {
                ClearAutoAimState(shooter);
            }

            if (heroLobMode
                && hasPreferredTarget
                && preferredTarget is not null
                && SimulationCombatMath.IsHeroStructureAutoAimTargetPlate(world, shooter, preferredTarget, preferredPlate)
                && !heroLobGuidanceManualFire
                && !IsHeroLobFireWindowReady(world, shooter, preferredTarget, preferredPlate))
            {
                continue;
            }

            if (!shooter.ConsumeAmmoForShot())
            {
                continue;
            }

            shooter.FireCooldownSec = 1.0 / Math.Max(ResolveFireRateHz(shooter), 0.5);
            report.TotalShots++;
            ApplyShotHeat(shooter);
            if (hasPreferredTarget && preferredTarget is not null)
            {
                preferredSolution = TryResolveStoredAutoAimSolution(shooter, preferredTarget, preferredPlate, out AutoAimSolution storedSolution)
                    ? storedSolution
                    : SimulationCombatMath.ComputeAutoAimSolution(
                        world,
                        shooter,
                        preferredTarget,
                        preferredPlate,
                        autoAimMaxDistanceM);
                hasPreferredSolution = true;
            }

            report.ShotEvents.Add(new SimulationShotEvent(
                world.GameTimeSec,
                shooter.Id,
                shooter.Team,
                shooter.AmmoType,
                preferredTarget?.Id ?? string.Empty,
                hasPreferredTarget ? preferredPlate.Id : string.Empty,
                shooter.IsPlayerControlled,
                shouldAutoAim));

            world.Projectiles.Add(BuildProjectile(
                world,
                shooter,
                preferredTarget,
                hasPreferredTarget ? preferredPlate : null,
                autoAimMaxDistanceM,
                hasPreferredSolution ? preferredSolution : null));
        }

        TickProjectiles(world, dt, report);
    }

    private SimulationProjectile BuildProjectile(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity? preferredTarget,
        ArmorPlateTarget? preferredPlate,
        double autoAimMaxDistanceM,
        AutoAimSolution? preferredSolution = null)
    {
        double pitchDeg = shooter.GimbalPitchDeg;
        double yawDeg = shooter.TurretYawDeg;
        double aimHitProbability = 1.0;
        bool heroLobMode = string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && SimulationCombatMath.IsHeroLobAutoAimMode(shooter);
        bool heroLobStructureTarget = heroLobMode
            && preferredTarget is not null
            && preferredPlate.HasValue
            && SimulationCombatMath.ShouldUseHeroLobStructureAxisAim(world, shooter, preferredTarget, preferredPlate.Value);
        if (preferredPlate.HasValue && preferredTarget is not null)
        {
            AutoAimSolution solution = preferredSolution
                ?? (TryResolveStoredAutoAimSolution(shooter, preferredTarget, preferredPlate.Value, out AutoAimSolution storedSolution)
                    ? storedSolution
                    : SimulationCombatMath.ComputeAutoAimSolution(
                        world,
                        shooter,
                        preferredTarget,
                        preferredPlate.Value,
                        autoAimMaxDistanceM));
            if (!shooter.AutoAimGuidanceOnly)
            {
                if (heroLobStructureTarget)
                {
                    pitchDeg = shooter.AutoAimHasSmoothedAim
                        ? shooter.AutoAimSmoothedPitchDeg
                        : solution.PitchDeg;
                    yawDeg = shooter.TurretYawDeg;
                    shooter.GimbalPitchDeg = pitchDeg;
                }
                else
                {
                    pitchDeg = solution.PitchDeg;
                    if (!heroLobMode || !heroLobStructureTarget)
                    {
                        yawDeg = solution.YawDeg;
                        shooter.TurretYawDeg = yawDeg;
                    }

                    shooter.GimbalPitchDeg = pitchDeg;
                }
            }

            aimHitProbability = ResolveHeroDeploymentHitProbability(shooter, preferredTarget, preferredPlate.Value, solution.Accuracy);
            StoreAutoAimDiagnostics(shooter, preferredTarget, preferredPlate.Value, solution);
        }

        (double muzzleX, double muzzleY, double muzzleHeightM) = SimulationCombatMath.ComputeMuzzlePoint(world, shooter, pitchDeg);
        double speedMps = ResolveShotProjectileSpeedMps(world, shooter);
        double yawRad = yawDeg * Math.PI / 180.0;
        double pitchRad = pitchDeg * Math.PI / 180.0;
        ApplySmallProjectileDispersion(world, shooter, ref yawRad, ref pitchRad);
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        bool staticAutoAimCenterLock = preferredTarget is not null
            && preferredPlate.HasValue
            && SimulationCombatMath.IsAutoAimTargetEffectivelyStatic(world, shooter, preferredTarget, preferredPlate.Value);
        double correctedYawRad = heroLobMode
            ? yawRad
            : ApplyHeroDeploymentCorrectionYaw(shooter, preferredPlate, yawRad);
        double correctedPitchRad = ApplyHeroDeploymentCorrectionPitch(shooter, preferredPlate, pitchRad);
        if (!staticAutoAimCenterLock)
        {
            if (!heroLobMode)
            {
                correctedYawRad = ApplyAutoAimCorrectionYaw(shooter, preferredTarget, preferredPlate, correctedYawRad);
            }

            correctedPitchRad = ApplyAutoAimCorrectionPitch(shooter, preferredTarget, preferredPlate, correctedPitchRad);
        }

        double inheritedVelocityXWorldPerSec = shooter.HasObservedKinematics
            ? shooter.ObservedVelocityXWorldPerSec
            : shooter.VelocityXWorldPerSec;
        double inheritedVelocityYWorldPerSec = shooter.HasObservedKinematics
            ? shooter.ObservedVelocityYWorldPerSec
            : shooter.VelocityYWorldPerSec;

        return new SimulationProjectile
        {
            ShooterId = shooter.Id,
            Team = shooter.Team,
            AmmoType = shooter.AmmoType,
            PreferredTargetId = preferredTarget?.Id,
            PreferredPlateId = preferredPlate?.Id,
            AimHitProbability = aimHitProbability,
            X = muzzleX,
            Y = muzzleY,
            HeightM = muzzleHeightM,
            VelocityXWorldPerSec = inheritedVelocityXWorldPerSec + Math.Cos(correctedPitchRad)
                * Math.Cos(correctedYawRad)
                * speedMps / metersPerWorldUnit,
            VelocityYWorldPerSec = inheritedVelocityYWorldPerSec + Math.Cos(correctedPitchRad)
                * Math.Sin(correctedYawRad)
                * speedMps / metersPerWorldUnit,
            VelocityZMps = Math.Sin(correctedPitchRad) * speedMps,
            RemainingLifeSec = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 8.0 : 6.0,
        };
    }

    private static bool TryResolveStoredAutoAimSolution(
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        out AutoAimSolution solution)
    {
        solution = default;
        if (!shooter.AutoAimLocked
            || !string.Equals(shooter.AutoAimTargetId, target.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(shooter.AutoAimPlateId, plate.Id, StringComparison.OrdinalIgnoreCase)
            || !shooter.AutoAimHasSmoothedAim)
        {
            return false;
        }

        solution = new AutoAimSolution(
            shooter.AutoAimSmoothedYawDeg,
            shooter.AutoAimSmoothedPitchDeg,
            shooter.AutoAimAccuracy,
            shooter.AutoAimDistanceCoefficient,
            shooter.AutoAimMotionCoefficient,
            shooter.AutoAimLeadTimeSec,
            shooter.AutoAimLeadDistanceM,
            shooter.AutoAimPlateDirection,
            shooter.AutoAimAimPointX,
            shooter.AutoAimAimPointY,
            shooter.AutoAimAimPointHeightM,
            shooter.AutoAimObservedVelocityXMps,
            shooter.AutoAimObservedVelocityYMps,
            shooter.AutoAimObservedVelocityZMps,
            shooter.AutoAimObservedAngularVelocityRadPerSec);
        return true;
    }

    private static double ApplyAutoAimCorrectionYaw(
        SimulationEntity shooter,
        SimulationEntity? preferredTarget,
        ArmorPlateTarget? preferredPlate,
        double yawRad)
    {
        if (!ShouldApplyAutoAimCorrection(shooter, preferredTarget, preferredPlate, out string lockKey)
            || !string.Equals(shooter.AutoAimCorrectionLockKey, lockKey, StringComparison.OrdinalIgnoreCase))
        {
            return yawRad;
        }

        return yawRad + shooter.AutoAimYawCorrectionDeg * Math.PI / 180.0;
    }

    private static double ApplyAutoAimCorrectionPitch(
        SimulationEntity shooter,
        SimulationEntity? preferredTarget,
        ArmorPlateTarget? preferredPlate,
        double pitchRad)
    {
        if (!ShouldApplyAutoAimCorrection(shooter, preferredTarget, preferredPlate, out string lockKey)
            || !string.Equals(shooter.AutoAimCorrectionLockKey, lockKey, StringComparison.OrdinalIgnoreCase))
        {
            return pitchRad;
        }

        return pitchRad + shooter.AutoAimPitchCorrectionDeg * Math.PI / 180.0;
    }

    private static bool ShouldApplyAutoAimCorrection(
        SimulationEntity shooter,
        SimulationEntity? preferredTarget,
        ArmorPlateTarget? preferredPlate,
        out string lockKey)
    {
        lockKey = string.Empty;
        if (preferredTarget is null
            || !preferredPlate.HasValue
            || !string.Equals(shooter.AmmoType, "17mm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lockKey = $"{preferredTarget.Id}:{preferredPlate.Value.Id}";
        return true;
    }

    private void ApplySmallProjectileDispersion(
        SimulationWorldState world,
        SimulationEntity shooter,
        ref double yawRad,
        ref double pitchRad)
    {
        if (!string.Equals(shooter.AmmoType, "17mm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        double fireRateHz = Math.Max(0.5, ResolveFireRateHz(shooter));
        double dispersionCoeff = fireRateHz <= 15.0
            ? 1.0
            : 1.0 + Math.Clamp((fireRateHz - 15.0) / 10.0, 0.0, 1.0) * 0.30;
        double angularRadiusRad = (0.13 / 8.0) * dispersionCoeff;
        int seed = StableShotSeed(shooter, world.GameTimeSec);
        double theta = StableUnit(seed) * Math.PI * 2.0;
        double radius = Math.Sqrt(StableUnit(seed ^ unchecked((int)0x9E3779B9))) * angularRadiusRad;
        yawRad += Math.Cos(theta) * radius;
        pitchRad += Math.Sin(theta) * radius;
    }

    private static int StableShotSeed(SimulationEntity shooter, double gameTimeSec)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(shooter.Id);
            hash = hash * 31 + (int)Math.Round(gameTimeSec * 1000.0);
            hash = hash * 31 + shooter.ShotsFired;
            hash = hash * 31 + shooter.Ammo17Mm;
            hash = hash * 31 + shooter.Ammo42Mm;
            return hash;
        }
    }

    private static double ResolveShotProjectileSpeedMps(SimulationWorldState world, SimulationEntity shooter)
    {
        double baseSpeedMps = SimulationCombatMath.ProjectileSpeedMps(shooter);
        int seed = StableShotSeed(shooter, world.GameTimeSec) ^ unchecked((int)0x4F1BBCDC);
        double maxDropMps = Math.Min(0.60, baseSpeedMps * 0.0365);
        return Math.Max(0.1, baseSpeedMps - StableUnit(seed) * maxDropMps);
    }

    private static double StableUnit(int seed)
    {
        unchecked
        {
            uint value = (uint)seed;
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            value *= 0x846ca68b;
            value ^= value >> 16;
            return (value & 0x00FFFFFF) / 16777216.0;
        }
    }

    private static double ApplyHeroDeploymentCorrectionYaw(
        SimulationEntity shooter,
        ArmorPlateTarget? preferredPlate,
        double yawRad)
    {
        if (!shooter.HeroDeploymentActive || !preferredPlate.HasValue)
        {
            return yawRad;
        }

        string correctionKey = ResolveHeroDeploymentCorrectionKey(preferredPlate.Value);
        if (!string.Equals(shooter.HeroDeploymentCorrectionPlateId, correctionKey, StringComparison.OrdinalIgnoreCase))
        {
            return yawRad;
        }

        return yawRad + shooter.HeroDeploymentYawCorrectionDeg * Math.PI / 180.0;
    }

    private static double ApplyHeroDeploymentCorrectionPitch(
        SimulationEntity shooter,
        ArmorPlateTarget? preferredPlate,
        double pitchRad)
    {
        if (!shooter.HeroDeploymentActive || !preferredPlate.HasValue)
        {
            return pitchRad;
        }

        string correctionKey = ResolveHeroDeploymentCorrectionKey(preferredPlate.Value);
        if (!string.Equals(shooter.HeroDeploymentCorrectionPlateId, correctionKey, StringComparison.OrdinalIgnoreCase))
        {
            return pitchRad;
        }

        double lastShotAssistDeg = Math.Clamp(shooter.HeroDeploymentLastPitchErrorDeg * 0.32, -3.5, 3.5);
        return pitchRad + (shooter.HeroDeploymentPitchCorrectionDeg + lastShotAssistDeg) * Math.PI / 180.0;
    }

    private static double ResolveHeroDeploymentHitProbability(
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double fallbackAccuracy)
    {
        if (!shooter.HeroDeploymentActive
            || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            return fallbackAccuracy;
        }

        if (IsHeroDeploymentOutpostRingPlate(plate))
        {
            return Math.Max(0.70, fallbackAccuracy);
        }

        if (IsHeroDeploymentBaseTopPlate(plate))
        {
            return Math.Max(0.56, fallbackAccuracy * 0.94);
        }

        return fallbackAccuracy;
    }

    private static void UpdateHeroDeploymentCorrection(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        Vector3 hitPointM)
    {
        if (!shooter.HeroDeploymentActive
            || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || !IsHeroDeploymentCorrectionPlate(plate))
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        Vector3 plateCenter = new(
            (float)(plate.X * metersPerWorldUnit),
            (float)plate.HeightM,
            (float)(plate.Y * metersPerWorldUnit));
        (double centerYawDeg, double centerPitchDeg) = SimulationCombatMath.ComputeAimAnglesToPoint(
            world,
            shooter,
            plate.X,
            plate.Y,
            plate.HeightM,
            preferHighArc: SimulationCombatMath.ShouldUseHeroLobStructureAxisAim(world, shooter, target, plate));
        (double actualYawDeg, double actualPitchDeg) = SimulationCombatMath.ComputeAimAnglesToPoint(
            world,
            shooter,
            hitPointM.X / metersPerWorldUnit,
            hitPointM.Z / metersPerWorldUnit,
            hitPointM.Y,
            preferHighArc: SimulationCombatMath.ShouldUseHeroLobStructureAxisAim(world, shooter, target, plate));
        double yawErrorDeg = SimulationCombatMath.NormalizeSignedDeg(centerYawDeg - actualYawDeg);
        double pitchErrorDeg = centerPitchDeg - actualPitchDeg;
        bool outpostRing = IsHeroDeploymentOutpostRingPlate(plate);
        shooter.HeroDeploymentCorrectionPlateId = ResolveHeroDeploymentCorrectionKey(plate);
        shooter.HeroDeploymentYawCorrectionDeg = Math.Clamp(
            shooter.HeroDeploymentYawCorrectionDeg * (outpostRing ? 0.58 : 0.66) + yawErrorDeg * (outpostRing ? 0.28 : 0.22),
            outpostRing ? -3.6 : -1.8,
            outpostRing ? 3.6 : 1.8);
        shooter.HeroDeploymentLastPitchErrorDeg = pitchErrorDeg;
        shooter.HeroDeploymentPitchCorrectionDeg = Math.Clamp(
            shooter.HeroDeploymentPitchCorrectionDeg * (outpostRing ? 0.34 : 0.42) + pitchErrorDeg * (outpostRing ? 0.96 : 0.88),
            outpostRing ? -14.5 : -11.5,
            outpostRing ? 14.5 : 11.5);
    }

    private static void UpdateHeroDeploymentMissCorrection(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationProjectile projectile,
        Vector3 impactPointM)
    {
        if (!shooter.HeroDeploymentActive
            || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(projectile.PreferredTargetId)
            || string.IsNullOrWhiteSpace(projectile.PreferredPlateId))
        {
            return;
        }

        SimulationEntity? target = world.Entities.FirstOrDefault(entity =>
            string.Equals(entity.Id, projectile.PreferredTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        ArmorPlateTarget plate = SimulationCombatMath.GetArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, projectile.PreferredPlateId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(plate.Id) || !IsHeroDeploymentCorrectionPlate(plate))
        {
            return;
        }

        (double centerYawDeg, double centerPitchDeg) = SimulationCombatMath.ComputeAimAnglesToPoint(
            world,
            shooter,
            plate.X,
            plate.Y,
            plate.HeightM,
            preferHighArc: SimulationCombatMath.ShouldUseHeroLobStructureAxisAim(world, shooter, target, plate));
        (double actualYawDeg, double actualPitchDeg) = SimulationCombatMath.ComputeAimAnglesToPoint(
            world,
            shooter,
            impactPointM.X / metersPerWorldUnit,
            impactPointM.Z / metersPerWorldUnit,
            impactPointM.Y,
            preferHighArc: SimulationCombatMath.ShouldUseHeroLobStructureAxisAim(world, shooter, target, plate));
        double yawErrorDeg = SimulationCombatMath.NormalizeSignedDeg(centerYawDeg - actualYawDeg);
        double pitchErrorDeg = centerPitchDeg - actualPitchDeg;
        bool outpostRing = IsHeroDeploymentOutpostRingPlate(plate);
        shooter.HeroDeploymentCorrectionPlateId = ResolveHeroDeploymentCorrectionKey(plate);
        shooter.HeroDeploymentYawCorrectionDeg = Math.Clamp(
            shooter.HeroDeploymentYawCorrectionDeg * (outpostRing ? 0.68 : 0.76) + yawErrorDeg * (outpostRing ? 0.20 : 0.16),
            outpostRing ? -4.0 : -2.1,
            outpostRing ? 4.0 : 2.1);
        shooter.HeroDeploymentLastPitchErrorDeg = pitchErrorDeg;
        shooter.HeroDeploymentPitchCorrectionDeg = Math.Clamp(
            shooter.HeroDeploymentPitchCorrectionDeg * (outpostRing ? 0.56 : 0.64) + pitchErrorDeg * (outpostRing ? 0.58 : 0.50),
            outpostRing ? -16.0 : -12.5,
            outpostRing ? 16.0 : 12.5);
    }

    private static bool IsHeroDeploymentOutpostRingPlate(ArmorPlateTarget plate)
        => plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeroDeploymentBaseTopPlate(ArmorPlateTarget plate)
        => plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeroDeploymentCorrectionPlate(ArmorPlateTarget plate)
        => IsHeroDeploymentOutpostRingPlate(plate) || IsHeroDeploymentBaseTopPlate(plate);

    private static string ResolveHeroDeploymentCorrectionKey(ArmorPlateTarget plate)
        => IsHeroDeploymentOutpostRingPlate(plate)
            ? "outpost_ring"
            : plate.Id;

    private static void UpdateAutoAimCorrection(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        Vector3 hitPointM)
    {
        if (!string.Equals(shooter.AmmoType, "17mm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string lockKey = $"{target.Id}:{plate.Id}";
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        (double centerYawDeg, double centerPitchDeg) = SimulationCombatMath.ComputeAimAnglesToPoint(world, shooter, plate.X, plate.Y, plate.HeightM);
        (double actualYawDeg, double actualPitchDeg) = SimulationCombatMath.ComputeAimAnglesToPoint(
            world,
            shooter,
            hitPointM.X / metersPerWorldUnit,
            hitPointM.Z / metersPerWorldUnit,
            hitPointM.Y);
        double yawErrorDeg = SimulationCombatMath.NormalizeSignedDeg(centerYawDeg - actualYawDeg);
        double pitchErrorDeg = centerPitchDeg - actualPitchDeg;
        shooter.AutoAimCorrectionLockKey = lockKey;
        shooter.AutoAimYawCorrectionDeg = Math.Clamp(
            shooter.AutoAimYawCorrectionDeg * 0.72 + yawErrorDeg * 0.42,
            -2.5,
            2.5);
        shooter.AutoAimPitchCorrectionDeg = Math.Clamp(
            shooter.AutoAimPitchCorrectionDeg * 0.72 + pitchErrorDeg * 0.42,
            -3.5,
            3.5);
    }

    private void TickProjectiles(SimulationWorldState world, double dt, SimulationRunReport report)
    {
        if (world.Projectiles.Count == 0)
        {
            return;
        }

        if (_rules is not null)
        {
            TickProjectilesSubstepped(world, dt, report);
            return;
        }

        ProjectileFrameCache frameCache = BuildProjectileFrameCache(world);
        for (int index = world.Projectiles.Count - 1; index >= 0; index--)
        {
            SimulationProjectile projectile = world.Projectiles[index];
            SimulationEntity? shooter = frameCache.EntityById.GetValueOrDefault(projectile.ShooterId);
            if (shooter is null || !shooter.IsAlive)
            {
                world.Projectiles.RemoveAt(index);
                continue;
            }

            double startX = projectile.X;
            double startY = projectile.Y;
            double startHeightM = projectile.HeightM;

            ApplyProjectilePhysicsStep(projectile, Math.Max(world.MetersPerWorldUnit, 1e-6), dt);
            if (IsProjectileOutsideWorldBounds(world, projectile))
            {
                world.Projectiles.RemoveAt(index);
                continue;
            }

            IReadOnlyList<SimulationEntity> directionalDamageTargets = FilterProjectileDamageCandidates(
                frameCache,
                shooter,
                startX,
                startY,
                startHeightM,
                projectile.X,
                projectile.Y,
                projectile.HeightM,
                world.MetersPerWorldUnit);
            if (directionalDamageTargets.Count > 0
                && SimulationCombatMath.TryFindProjectileHit(
                world,
                shooter,
                startX,
                startY,
                startHeightM,
                projectile.X,
                projectile.Y,
                projectile.HeightM,
                directionalDamageTargets,
                frameCache.ArmorTargetsByEntityId,
                out SimulationEntity? hitTarget,
                out ArmorPlateTarget hitPlate,
                out double hitX,
                out double hitY,
                out double hitHeightM,
                out double hitSegmentT))
            {
                IReadOnlyList<SimulationEntity> directionalObstacleTargets = FilterProjectileObstacleCandidates(
                    frameCache,
                    shooter,
                    projectile,
                    startX,
                    startY,
                    startHeightM,
                    projectile.X,
                    projectile.Y,
                    projectile.HeightM,
                    world.MetersPerWorldUnit);
                ProjectileObstacleHit? obstacleHit = TryResolveProjectileObstacle(
                    world,
                    shooter,
                    projectile,
                    startX,
                    startY,
                    startHeightM,
                    projectile.X,
                    projectile.Y,
                    projectile.HeightM,
                    directionalObstacleTargets);
                if (ShouldResolveObstacleBeforePlate(obstacleHit, hitSegmentT)
                    && obstacleHit is ProjectileObstacleHit blockingHit)
                {
                    if (TryApplyRicochet(projectile, blockingHit, world.MetersPerWorldUnit))
                    {
                        continue;
                    }

                    world.Projectiles.RemoveAt(index);
                    continue;
                }

                double dx = hitTarget!.X - shooter.X;
                double dy = hitTarget.Y - shooter.Y;
                double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
                double distanceM = Math.Sqrt(dx * dx + dy * dy) * metersPerWorldUnit;
                Vector3 hitPointM = new((float)(hitX * metersPerWorldUnit), (float)hitHeightM, (float)(hitY * metersPerWorldUnit));
                if (TryHandleEnergyMechanismHit(world, shooter, projectile, hitTarget, hitPlate, hitPointM, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit, report, out bool energyProjectileAlive))
                {
                    if (!energyProjectileAlive)
                    {
                        world.Projectiles.RemoveAt(index);
                    }

                    continue;
                }

                if (!CanProjectileDealArmorDamage(projectile, ResolveProjectileSpeedMps(projectile, metersPerWorldUnit)))
                {
                    if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, world.MetersPerWorldUnit))
                    {
                        continue;
                    }

                    world.Projectiles.RemoveAt(index);
                    continue;
                }

                if (!TryRegisterSurfaceAcceptedHit(world, projectile, hitTarget, hitPlate))
                {
                    if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, world.MetersPerWorldUnit))
                    {
                        continue;
                    }

                    world.Projectiles.RemoveAt(index);
                    continue;
                }

                if (IsBaseProtectedByLivingOutpost(world, hitTarget))
                {
                    if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, world.MetersPerWorldUnit))
                    {
                        continue;
                    }

                    world.Projectiles.RemoveAt(index);
                    continue;
                }

                if (projectile.HasAppliedDamage)
                {
                    if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, world.MetersPerWorldUnit))
                    {
                        continue;
                    }

                    world.Projectiles.RemoveAt(index);
                    continue;
                }

                bool criticalHit = SimulationCombatMath.IsCriticalStructureArmorHit(hitPlate, metersPerWorldUnit, hitPointM);
                double damage = ComputeDamage(shooter, hitTarget, hitPlate, criticalHit);
                if (damage < MinimumEffectiveDamage)
                {
                    if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, world.MetersPerWorldUnit))
                    {
                        continue;
                    }

                    world.Projectiles.RemoveAt(index);
                    continue;
                }

                UpdateHeroDeploymentCorrection(world, shooter, hitTarget, hitPlate, hitPointM);
                UpdateAutoAimCorrection(world, shooter, hitTarget, hitPlate, hitPointM);
                double appliedDamage = ApplyDamage(world, shooter, hitTarget, damage, report, out string damageResult);
                projectile.HasAppliedDamage = true;
                report.HitShots++;
                report.CombatEvents.Add(new SimulationCombatEvent(
                    world.GameTimeSec,
                    shooter.Id,
                    hitTarget.Id,
                    shooter.AmmoType,
                    distanceM,
                    projectile.AimHitProbability,
                    true,
                    appliedDamage,
                    damageResult switch
                    {
                        "invincible" => $"命中 {hitPlate.Id} -0HP 无敌",
                        "base_protected" => $"命中 {hitPlate.Id} -0HP 基地无敌",
                        _ => $"命中 {hitPlate.Id} 造成 {appliedDamage:0.##}"
                    },
                    criticalHit,
                    damageResult is "invincible" or "base_protected",
                    damageResult,
                    hitPlate.Id));
                if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, world.MetersPerWorldUnit))
                {
                    continue;
                }

                world.Projectiles.RemoveAt(index);
                continue;
            }

            IReadOnlyList<SimulationEntity> obstacleOnlyTargets = FilterProjectileObstacleCandidates(
                frameCache,
                shooter,
                projectile,
                startX,
                startY,
                startHeightM,
                projectile.X,
                projectile.Y,
                projectile.HeightM,
                world.MetersPerWorldUnit);
            ProjectileObstacleHit? obstacleOnlyHit = TryResolveProjectileObstacle(
                world,
                shooter,
                projectile,
                startX,
                startY,
                startHeightM,
                projectile.X,
                projectile.Y,
                projectile.HeightM,
                obstacleOnlyTargets);
            if (obstacleOnlyHit is ProjectileObstacleHit obstacleImpact)
            {
                if (TryApplyRicochet(projectile, obstacleImpact, world.MetersPerWorldUnit))
                {
                    continue;
                }

                UpdateHeroDeploymentMissCorrection(
                    world,
                    shooter,
                    projectile,
                    new Vector3(
                        (float)(obstacleImpact.X * world.MetersPerWorldUnit),
                        (float)obstacleImpact.HeightM,
                        (float)(obstacleImpact.Y * world.MetersPerWorldUnit)));
                world.Projectiles.RemoveAt(index);
                continue;
            }

            if (ShouldDeleteProjectileBySpeed(projectile, world.MetersPerWorldUnit))
            {
                UpdateHeroDeploymentMissCorrection(
                    world,
                    shooter,
                    projectile,
                    new Vector3(
                        (float)(projectile.X * world.MetersPerWorldUnit),
                        (float)projectile.HeightM,
                        (float)(projectile.Y * world.MetersPerWorldUnit)));
                world.Projectiles.RemoveAt(index);
            }
        }
    }

    private void TickProjectilesSubstepped(SimulationWorldState world, double dt, SimulationRunReport report)
    {
        if (world.Projectiles.Count == 0)
        {
            return;
        }

        ProjectileFrameCache frameCache = BuildProjectileFrameCache(world);
        for (int index = world.Projectiles.Count - 1; index >= 0; index--)
        {
            SimulationProjectile projectile = world.Projectiles[index];
            if (!frameCache.EntityById.TryGetValue(projectile.ShooterId, out SimulationEntity? shooter) || !shooter.IsAlive)
            {
                world.Projectiles.RemoveAt(index);
                continue;
            }

            bool removeProjectile = false;
            double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
            double speedMps = ResolveProjectileSpeedMps(projectile, metersPerWorldUnit);
            int substeps = Math.Clamp((int)Math.Ceiling(speedMps * Math.Max(dt, 0.01) / 1.1), 1, 6);
            double substepDt = dt / substeps;
            for (int substep = 0; substep < substeps; substep++)
            {
                double startX = projectile.X;
                double startY = projectile.Y;
                double startHeightM = projectile.HeightM;
                ApplyProjectilePhysicsStep(projectile, metersPerWorldUnit, substepDt);
                if (IsProjectileOutsideWorldBounds(world, projectile))
                {
                    removeProjectile = true;
                    break;
                }

                IReadOnlyList<SimulationEntity> directionalDamageTargets = FilterProjectileDamageCandidates(
                    frameCache,
                    shooter,
                    startX,
                    startY,
                    startHeightM,
                    projectile.X,
                    projectile.Y,
                    projectile.HeightM,
                    metersPerWorldUnit);
                if (directionalDamageTargets.Count > 0
                    && SimulationCombatMath.TryFindProjectileHit(
                    world,
                    shooter,
                    startX,
                    startY,
                    startHeightM,
                    projectile.X,
                    projectile.Y,
                    projectile.HeightM,
                    directionalDamageTargets,
                    frameCache.ArmorTargetsByEntityId,
                    out SimulationEntity? hitTarget,
                    out ArmorPlateTarget hitPlate,
                    out double hitX,
                    out double hitY,
                    out double hitHeightM,
                    out double hitSegmentT))
                {
                    IReadOnlyList<SimulationEntity> directionalObstacleTargets = FilterProjectileObstacleCandidates(
                        frameCache,
                        shooter,
                        projectile,
                        startX,
                        startY,
                        startHeightM,
                        projectile.X,
                        projectile.Y,
                        projectile.HeightM,
                        metersPerWorldUnit);
                    ProjectileObstacleHit? obstacleHit = TryResolveProjectileObstacle(
                        world,
                        shooter,
                        projectile,
                        startX,
                        startY,
                        startHeightM,
                        projectile.X,
                        projectile.Y,
                        projectile.HeightM,
                        directionalObstacleTargets);
                    if (ShouldResolveObstacleBeforePlate(obstacleHit, hitSegmentT)
                        && obstacleHit is ProjectileObstacleHit blockingHit)
                    {
                        if (TryApplyRicochet(projectile, blockingHit, metersPerWorldUnit))
                        {
                            break;
                        }

                        removeProjectile = true;
                        break;
                    }

                    double dx = hitTarget!.X - shooter.X;
                    double dy = hitTarget.Y - shooter.Y;
                    double distanceM = Math.Sqrt(dx * dx + dy * dy) * metersPerWorldUnit;
                    Vector3 hitPointM = new((float)(hitX * metersPerWorldUnit), (float)hitHeightM, (float)(hitY * metersPerWorldUnit));
                    if (TryHandleEnergyMechanismHit(world, shooter, projectile, hitTarget, hitPlate, hitPointM, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit, report, out bool energyProjectileAlive))
                    {
                        removeProjectile = !energyProjectileAlive;
                        break;
                    }

                    if (!CanProjectileDealArmorDamage(projectile, ResolveProjectileSpeedMps(projectile, metersPerWorldUnit)))
                    {
                        if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit))
                        {
                            break;
                        }

                        removeProjectile = true;
                        break;
                    }

                    if (!TryRegisterSurfaceAcceptedHit(world, projectile, hitTarget, hitPlate))
                    {
                        if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit))
                        {
                            break;
                        }

                        removeProjectile = true;
                        break;
                    }

                    if (IsBaseProtectedByLivingOutpost(world, hitTarget))
                    {
                        if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit))
                        {
                            break;
                        }

                        removeProjectile = true;
                        break;
                    }

                    if (projectile.HasAppliedDamage)
                    {
                        if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit))
                        {
                            break;
                        }

                        removeProjectile = true;
                        break;
                    }

                    bool criticalHit = SimulationCombatMath.IsCriticalStructureArmorHit(hitPlate, metersPerWorldUnit, hitPointM);
                    double damage = ComputeDamage(shooter, hitTarget, hitPlate, criticalHit);
                    if (damage < MinimumEffectiveDamage)
                    {
                        if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit))
                        {
                            break;
                        }

                        removeProjectile = true;
                        break;
                    }

                    UpdateHeroDeploymentCorrection(world, shooter, hitTarget, hitPlate, hitPointM);
                    UpdateAutoAimCorrection(world, shooter, hitTarget, hitPlate, hitPointM);
                    double appliedDamage = ApplyDamage(world, shooter, hitTarget, damage, report, out string damageResult);
                    projectile.HasAppliedDamage = true;
                    report.HitShots++;
                report.CombatEvents.Add(new SimulationCombatEvent(
                    world.GameTimeSec,
                    shooter.Id,
                    hitTarget.Id,
                    shooter.AmmoType,
                    distanceM,
                    projectile.AimHitProbability,
                    true,
                    appliedDamage,
                    damageResult switch
                    {
                        "invincible" => $"Hit {hitPlate.Id} for 0 invincible",
                        "base_protected" => $"Hit {hitPlate.Id} for 0 base protected",
                        _ => $"Hit {hitPlate.Id} for {appliedDamage:0.##}",
                    },
                    criticalHit,
                    damageResult is "invincible" or "base_protected",
                    damageResult,
                    hitPlate.Id));
                    if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit))
                    {
                        break;
                    }

                    removeProjectile = true;
                    break;
                }

                IReadOnlyList<SimulationEntity> obstacleOnlyTargets = FilterProjectileObstacleCandidates(
                    frameCache,
                    shooter,
                    projectile,
                    startX,
                    startY,
                    startHeightM,
                    projectile.X,
                    projectile.Y,
                    projectile.HeightM,
                    metersPerWorldUnit);
                ProjectileObstacleHit? obstacleOnlyHit = TryResolveProjectileObstacle(
                    world,
                    shooter,
                    projectile,
                    startX,
                    startY,
                    startHeightM,
                    projectile.X,
                    projectile.Y,
                    projectile.HeightM,
                    obstacleOnlyTargets);
                if (obstacleOnlyHit is ProjectileObstacleHit obstacleImpact)
                {
                    if (TryApplyRicochet(projectile, obstacleImpact, metersPerWorldUnit))
                    {
                        break;
                    }

                    UpdateHeroDeploymentMissCorrection(
                        world,
                        shooter,
                        projectile,
                        new Vector3(
                            (float)(obstacleImpact.X * metersPerWorldUnit),
                            (float)obstacleImpact.HeightM,
                            (float)(obstacleImpact.Y * metersPerWorldUnit)));
                    removeProjectile = true;
                    break;
                }

                if (ShouldDeleteProjectileBySpeed(projectile, metersPerWorldUnit))
                {
                    UpdateHeroDeploymentMissCorrection(
                        world,
                        shooter,
                        projectile,
                        new Vector3(
                            (float)(projectile.X * metersPerWorldUnit),
                            (float)projectile.HeightM,
                            (float)(projectile.Y * metersPerWorldUnit)));
                    removeProjectile = true;
                    break;
                }
            }

            if (removeProjectile)
            {
                world.Projectiles.RemoveAt(index);
                continue;
            }

            world.Projectiles[index] = projectile;
        }
    }

    private bool TryHandleEnergyMechanismHit(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationProjectile projectile,
        SimulationEntity hitTarget,
        ArmorPlateTarget hitPlate,
        Vector3 hitPointM,
        double hitX,
        double hitY,
        double hitHeightM,
        double hitSegmentT,
        double metersPerWorldUnit,
        SimulationRunReport report,
        out bool projectileAlive)
    {
        projectileAlive = true;
        if (!string.Equals(hitTarget.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            || !hitPlate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!CanProjectileDealArmorDamage(projectile, ResolveProjectileSpeedMps(projectile, metersPerWorldUnit)))
        {
            projectileAlive = false;
            return true;
        }

        _interactionService.EnsureEnergyMechanismTestAttemptActive(world, shooter.Team, world.GameTimeSec, hitPlate);

        if (!world.Teams.TryGetValue(shooter.Team, out SimulationTeamState? shooterTeamState)
            || !string.Equals(shooterTeamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            || shooterTeamState.EnergyNextModuleDelaySec > 1e-6
            || shooterTeamState.EnergyCurrentLitMask == 0)
        {
            projectileAlive = false;
            return true;
        }

        if (!TryRegisterSurfaceAcceptedHit(world, projectile, hitTarget, hitPlate))
        {
            projectileAlive = false;
            return true;
        }

        int ringScore = ResolveEnergyRingScore(hitPlate, hitPointM, metersPerWorldUnit);
        FacilityInteractionEvent? energyEvent = _interactionService.ApplyEnergyMechanismHit(world, shooter, hitPlate, ringScore);
        if (energyEvent is not null)
        {
            report.InteractionEvents.Add(energyEvent);
            report.InteractionEventCount++;
        }

        report.HitShots++;
        report.CombatEvents.Add(new SimulationCombatEvent(
            world.GameTimeSec,
            shooter.Id,
            hitTarget.Id,
            shooter.AmmoType,
            DistanceM(shooter, hitTarget, metersPerWorldUnit),
            projectile.AimHitProbability,
            true,
            0.0,
            $"能量机关 {hitPlate.Id} 命中 {ringScore} 环",
            false,
            false,
            string.Empty,
            hitPlate.Id));

        projectileAlive = false;
        return true;
    }

    private static bool CanProjectileDealArmorDamage(SimulationProjectile projectile, double speedMps)
    {
        if (projectile.RicochetCount > 0)
        {
            return false;
        }

        double requiredSpeedMps = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? MinArmorDamageSpeed42Mps
            : MinArmorDamageSpeed17Mps;
        return speedMps > requiredSpeedMps;
    }

    private static bool TryRegisterSurfaceAcceptedHit(
        SimulationWorldState world,
        SimulationProjectile projectile,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        double minIntervalSec = ResolveSurfaceAcceptedHitIntervalSec(projectile.AmmoType, target, plate);
        string key = $"{target.Id}:{plate.Id}:{NormalizeAmmoHitGroup(projectile.AmmoType)}";
        if (minIntervalSec <= 1e-6)
        {
            projectile.LastAcceptedSurfaceHitKey = key;
            projectile.LastAcceptedSurfaceHitTimeSec = world.GameTimeSec;
            world.SurfaceLastAcceptedHitTimes[key] = world.GameTimeSec;
            if (world.SurfaceLastAcceptedHitTimes.Count > 2048)
            {
                PruneSurfaceHitTimes(world);
            }

            return true;
        }

        if (world.GameTimeSec - projectile.LastAcceptedSurfaceHitTimeSec < minIntervalSec)
        {
            return false;
        }

        if (world.SurfaceLastAcceptedHitTimes.TryGetValue(key, out double lastHitTimeSec)
            && world.GameTimeSec - lastHitTimeSec < minIntervalSec)
        {
            return false;
        }

        projectile.LastAcceptedSurfaceHitKey = key;
        projectile.LastAcceptedSurfaceHitTimeSec = world.GameTimeSec;
        world.SurfaceLastAcceptedHitTimes[key] = world.GameTimeSec;
        if (world.SurfaceLastAcceptedHitTimes.Count > 2048)
        {
            PruneSurfaceHitTimes(world);
        }

        return true;
    }

    private static double ResolveSurfaceAcceptedHitIntervalSec(
        string ammoType,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            && plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase))
        {
            return SurfaceAcceptedHitIntervalEnergySec;
        }

        return string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? SurfaceAcceptedHitInterval42Sec
            : SurfaceAcceptedHitInterval17Sec;
    }

    private static string NormalizeAmmoHitGroup(string ammoType)
        => string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? "42mm"
            : "17mm";

    private static void PruneSurfaceHitTimes(SimulationWorldState world)
    {
        double cutoff = world.GameTimeSec - 1.0;
        var staleKeys = new List<string>(Math.Min(256, world.SurfaceLastAcceptedHitTimes.Count));
        foreach (KeyValuePair<string, double> pair in world.SurfaceLastAcceptedHitTimes)
        {
            if (pair.Value < cutoff)
            {
                staleKeys.Add(pair.Key);
            }
        }

        foreach (string key in staleKeys)
        {
            world.SurfaceLastAcceptedHitTimes.Remove(key);
        }
    }

    private static int ResolveEnergyRingScore(ArmorPlateTarget plate, Vector3 hitPointM, double metersPerWorldUnit)
    {
        bool energyPlate = plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase);
        if (!energyPlate)
        {
            if (plate.EnergyRingScore > 0)
            {
                return Math.Clamp(plate.EnergyRingScore, 1, 10);
            }

            if (SimulationCombatMath.TryParseEnergyRingScore(plate.Id, out int parsedRingScore))
            {
                return parsedRingScore;
            }
        }

        Vector3 center = new((float)(plate.X * metersPerWorldUnit), (float)plate.HeightM, (float)(plate.Y * metersPerWorldUnit));
        Vector3 normal = Vector3.Normalize(SimulationCombatMath.ResolveArmorPlateNormal(plate));
        double yawRad = plate.YawDeg * Math.PI / 180.0;
        Vector3 side = new(-(float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
        Vector3 up = plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase)
            ? Vector3.Normalize(Vector3.Cross(side, normal))
            : Vector3.UnitY;
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.UnitY;
        }

        Vector3 local = hitPointM - center;
        double radiusM = Math.Max(0.01, Math.Max(plate.WidthM, plate.HeightSpanM) * 0.5);
        double radialM = Math.Sqrt(
            Math.Pow(Vector3.Dot(local, side), 2)
            + Math.Pow(Vector3.Dot(local, up), 2));
        double normalized = Math.Clamp(radialM / radiusM, 0.0, 1.0);
        return Math.Clamp((int)Math.Ceiling((1.0 - normalized) * 10.0), 1, 10);
    }

    private static double DistanceM(SimulationEntity a, SimulationEntity b, double metersPerWorldUnit)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy) * Math.Max(metersPerWorldUnit, 1e-6);
    }

    private static void ApplyProjectilePhysicsStep(
        SimulationProjectile projectile,
        double metersPerWorldUnit,
        double dt)
    {
        double vxMps = projectile.VelocityXWorldPerSec * metersPerWorldUnit;
        double vyMps = projectile.VelocityYWorldPerSec * metersPerWorldUnit;
        double vzMps = projectile.VelocityZMps;
        double speedMps = Math.Sqrt(vxMps * vxMps + vyMps * vyMps + vzMps * vzMps);
        if (speedMps > 1e-6)
        {
            double diameterM = SimulationCombatMath.ProjectileDiameterM(projectile.AmmoType);
            double areaM2 = Math.PI * diameterM * diameterM * 0.25;
            double massKg = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
                ? 0.041
                : 0.0032;
            double dragCoefficient = 0.47;
            double airDensityKgM3 = 1.20;
            double dragAccelMps2 = 0.5 * airDensityKgM3 * dragCoefficient * areaM2 * speedMps * speedMps / Math.Max(0.001, massKg);
            dragAccelMps2 = Math.Min(dragAccelMps2, speedMps / Math.Max(dt, 1e-6) * 0.72);
            double dragStep = dragAccelMps2 * dt / speedMps;
            vxMps -= vxMps * dragStep;
            vyMps -= vyMps * dragStep;
            vzMps -= vzMps * dragStep;
        }

        vzMps -= 9.81 * dt;
        projectile.VelocityXWorldPerSec = vxMps / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.VelocityYWorldPerSec = vyMps / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.VelocityZMps = vzMps;
        projectile.X += projectile.VelocityXWorldPerSec * dt;
        projectile.Y += projectile.VelocityYWorldPerSec * dt;
        projectile.HeightM += projectile.VelocityZMps * dt;
        projectile.RemainingLifeSec -= dt;
    }

    private static double ResolveProjectileSpeedMps(SimulationProjectile projectile, double metersPerWorldUnit)
    {
        double vxMps = projectile.VelocityXWorldPerSec * metersPerWorldUnit;
        double vyMps = projectile.VelocityYWorldPerSec * metersPerWorldUnit;
        double vzMps = projectile.VelocityZMps;
        return Math.Sqrt(vxMps * vxMps + vyMps * vyMps + vzMps * vzMps);
    }

    private static bool ShouldDeleteProjectileBySpeed(SimulationProjectile projectile, double metersPerWorldUnit)
        => ResolveProjectileSpeedMps(projectile, Math.Max(metersPerWorldUnit, 1e-6)) < ProjectileDeleteSpeedMps;

    private static bool IsProjectileOutsideWorldBounds(SimulationWorldState world, SimulationProjectile projectile)
    {
        if (world.WorldWidth <= 1e-6 || world.WorldHeight <= 1e-6)
        {
            return false;
        }

        return projectile.X < 0.0
            || projectile.Y < 0.0
            || projectile.X > world.WorldWidth
            || projectile.Y > world.WorldHeight;
    }

    private ProjectileObstacleHit? TryResolveProjectileObstacle(
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
        if (_resolveProjectileObstacle is null)
        {
            return null;
        }

        return _resolveProjectileObstacle(
            world,
            shooter,
            projectile,
            startX,
            startY,
            startHeightM,
            endX,
            endY,
            endHeightM,
            obstacleCandidates);
    }

    private static bool ShouldResolveObstacleBeforePlate(ProjectileObstacleHit? obstacleHit, double hitSegmentT)
    {
        return obstacleHit is ProjectileObstacleHit obstacle
            && obstacle.SegmentT + 0.015 < hitSegmentT;
    }

    private bool TryApplyArmorRicochet(
        SimulationProjectile projectile,
        ArmorPlateTarget plate,
        double hitX,
        double hitY,
        double hitHeightM,
        double hitSegmentT,
        double metersPerWorldUnit)
    {
        Vector3 normal = SimulationCombatMath.ResolveArmorPlateNormal(plate);
        ProjectileObstacleHit armorHit = new(
            hitX,
            hitY,
            hitHeightM,
            normal.X,
            normal.Y,
            normal.Z,
            hitSegmentT,
            SupportsRicochet: true,
            Kind: "armor_plate");
        return TryApplyRicochet(projectile, armorHit, metersPerWorldUnit);
    }

    private bool TryApplyRicochet(
        SimulationProjectile projectile,
        ProjectileObstacleHit obstacleHit,
        double metersPerWorldUnit)
    {
        if (!obstacleHit.SupportsRicochet)
        {
            return false;
        }

        Vector3 velocityMps = new(
            (float)(projectile.VelocityXWorldPerSec * metersPerWorldUnit),
            (float)projectile.VelocityZMps,
            (float)(projectile.VelocityYWorldPerSec * metersPerWorldUnit));
        if (velocityMps.LengthSquared() <= 1e-6f)
        {
            return false;
        }

        Vector3 normal = new((float)obstacleHit.NormalX, (float)obstacleHit.NormalY, (float)obstacleHit.NormalZ);
        if (normal.LengthSquared() <= 1e-6f)
        {
            normal = Vector3.UnitY;
        }

        normal = Vector3.Normalize(normal);
        if (Vector3.Dot(velocityMps, normal) > 0f)
        {
            normal = -normal;
        }

        bool smallProjectile = !string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        Vector3 incomingDirection = Vector3.Normalize(velocityMps);
        float incidentCos = Math.Clamp(-Vector3.Dot(incomingDirection, normal), 0f, 1f);
        float grazingFactor = 1f - incidentCos;
        Vector3 reflected = Vector3.Reflect(velocityMps, normal);
        if (reflected.LengthSquared() <= 1e-6f)
        {
            reflected = velocityMps - normal * 2f * Vector3.Dot(velocityMps, normal);
        }

        if (reflected.LengthSquared() <= 1e-6f)
        {
            return false;
        }

        if (string.Equals(obstacleHit.Kind, "armor_plate", StringComparison.OrdinalIgnoreCase))
        {
            // Bias the post-hit direction slightly away from the armor so robot armor hits
            // always produce a visible ricochet instead of immediately re-colliding in place.
            float reflectedSpeed = reflected.Length();
            float armorLiftBias = smallProjectile ? 0.24f : 0.18f;
            reflected += normal * MathF.Max(smallProjectile ? 1.05f : 0.8f, reflectedSpeed * armorLiftBias);
            if (reflected.LengthSquared() > 1e-6f)
            {
                reflected = Vector3.Normalize(reflected) * reflectedSpeed;
            }
        }

        float preRicochetSpeed = reflected.Length();
        float retention = smallProjectile
            ? Lerp(0.62f, 0.88f, grazingFactor)
            : Lerp(0.52f, 0.78f, grazingFactor);
        if (string.Equals(obstacleHit.Kind, "armor_plate", StringComparison.OrdinalIgnoreCase))
        {
            retention += smallProjectile ? 0.04f : 0.02f;
        }

        retention = Math.Clamp(retention, 0.45f, 0.92f);
        reflected *= retention;
        if (reflected.Length() < ProjectileDeleteSpeedMps
            && preRicochetSpeed >= ProjectileDeleteSpeedMps)
        {
            reflected = Vector3.Normalize(reflected) * (float)(ProjectileDeleteSpeedMps + 0.05);
        }

        if (reflected.Length() < ProjectileDeleteSpeedMps)
        {
            return false;
        }

        float offsetDistance = string.Equals(obstacleHit.Kind, "armor_plate", StringComparison.OrdinalIgnoreCase)
            ? (smallProjectile ? 0.028f : 0.022f)
            : (smallProjectile ? 0.016f : 0.012f);
        Vector3 offset = Vector3.Normalize(reflected) * offsetDistance;
        projectile.X = obstacleHit.X + offset.X / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.Y = obstacleHit.Y + offset.Z / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.HeightM = obstacleHit.HeightM + offset.Y;
        projectile.VelocityXWorldPerSec = reflected.X / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.VelocityYWorldPerSec = reflected.Z / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.VelocityZMps = reflected.Y;
        projectile.DamageScale *= smallProjectile ? 0.62 : 0.5;
        projectile.RicochetCount++;
        projectile.RemainingLifeSec = Math.Max(0.08, projectile.RemainingLifeSec - 0.01);
        return true;
    }

    private static float Lerp(float from, float to, float t)
        => from + (to - from) * Math.Clamp(t, 0f, 1f);

    private static Dictionary<string, SimulationEntity> BuildEntityLookup(SimulationWorldState world)
    {
        var lookup = new Dictionary<string, SimulationEntity>(world.Entities.Count, StringComparer.OrdinalIgnoreCase);
        foreach (SimulationEntity entity in world.Entities)
        {
            lookup[entity.Id] = entity;
        }

        return lookup;
    }

    private static IReadOnlyList<SimulationEntity> FilterProjectileDamageCandidates(
        ProjectileFrameCache frameCache,
        SimulationEntity shooter,
        double startX,
        double startY,
        double startHeightM,
        double endX,
        double endY,
        double endHeightM,
        double metersPerWorldUnit)
    {
        double safeMetersPerWorldUnit = Math.Max(metersPerWorldUnit, 1e-6);
        double projectileRadiusM = SimulationCombatMath.ProjectileDiameterM(shooter.AmmoType) * 0.5;
        Vector3 segmentStart = new(
            (float)(startX * safeMetersPerWorldUnit),
            (float)startHeightM,
            (float)(startY * safeMetersPerWorldUnit));
        Vector3 segmentEnd = new(
            (float)(endX * safeMetersPerWorldUnit),
            (float)endHeightM,
            (float)(endY * safeMetersPerWorldUnit));
        List<SimulationEntity> directionalTargets = frameCache.DirectionalDamageCandidates;
        directionalTargets.Clear();
        foreach (SimulationEntity candidate in frameCache.DamageTargets)
        {
            if (string.Equals(candidate.Id, shooter.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ProjectileCollisionBroadphase.MayIntersectTargetBounds(
                    candidate,
                    safeMetersPerWorldUnit,
                    projectileRadiusM,
                    segmentStart,
                    segmentEnd))
            {
                directionalTargets.Add(candidate);
            }
        }

        return directionalTargets;
    }

    private static IReadOnlyList<SimulationEntity> FilterProjectileObstacleCandidates(
        ProjectileFrameCache frameCache,
        SimulationEntity shooter,
        SimulationProjectile projectile,
        double startX,
        double startY,
        double startHeightM,
        double endX,
        double endY,
        double endHeightM,
        double metersPerWorldUnit)
    {
        double safeMetersPerWorldUnit = Math.Max(metersPerWorldUnit, 1e-6);
        double projectileRadiusM = SimulationCombatMath.ProjectileDiameterM(projectile.AmmoType) * 0.5;
        Vector3 segmentStart = new(
            (float)(startX * safeMetersPerWorldUnit),
            (float)startHeightM,
            (float)(startY * safeMetersPerWorldUnit));
        Vector3 segmentEnd = new(
            (float)(endX * safeMetersPerWorldUnit),
            (float)endHeightM,
            (float)(endY * safeMetersPerWorldUnit));
        List<SimulationEntity> directionalObstacles = frameCache.DirectionalObstacleCandidates;
        directionalObstacles.Clear();
        foreach (SimulationEntity candidate in frameCache.ObstacleCandidates)
        {
            if (string.Equals(candidate.Id, shooter.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SimulationCombatMath.IsLegacyMechanismCollisionSuppressed(candidate))
            {
                continue;
            }

            if (!ProjectileCollisionBroadphase.MayIntersectObstacleBounds(
                    candidate,
                    safeMetersPerWorldUnit,
                    projectileRadiusM,
                    segmentStart,
                    segmentEnd))
            {
                continue;
            }

            directionalObstacles.Add(candidate);
        }

        return directionalObstacles;
    }

    private static ProjectileFrameCache BuildProjectileFrameCache(SimulationWorldState world)
    {
        Dictionary<string, SimulationEntity> entityById = BuildEntityLookup(world);
        var redTargets = new List<SimulationEntity>(world.Entities.Count);
        var blueTargets = new List<SimulationEntity>(world.Entities.Count);
        var damageTargets = new List<SimulationEntity>(world.Entities.Count);
        var obstacles = new List<SimulationEntity>(world.Entities.Count);
        var armorTargetsByEntityId = new Dictionary<string, IReadOnlyList<ArmorPlateTarget>>(world.Entities.Count, StringComparer.OrdinalIgnoreCase);
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        foreach (SimulationEntity entity in world.Entities)
        {
            if (!entity.IsSimulationSuppressed
                && !SimulationCombatMath.IsLegacyMechanismCollisionSuppressed(entity))
            {
                obstacles.Add(entity);
            }

            if (!entity.IsAlive || entity.IsSimulationSuppressed)
            {
                continue;
            }

            damageTargets.Add(entity);

            if (string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                redTargets.Add(entity);
                blueTargets.Add(entity);
            }
            else if (string.Equals(entity.Team, "red", StringComparison.OrdinalIgnoreCase))
            {
                redTargets.Add(entity);
            }
            else if (string.Equals(entity.Team, "blue", StringComparison.OrdinalIgnoreCase))
            {
                blueTargets.Add(entity);
            }

            armorTargetsByEntityId[entity.Id] = string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
                ? BuildEnergyMechanismTargetsForFrame(world, entity, metersPerWorldUnit)
                : SimulationCombatMath.GetAttackableArmorPlateTargets(entity, metersPerWorldUnit, world.GameTimeSec);
        }

        return new ProjectileFrameCache
        {
            EntityById = entityById,
            RedTeamTargets = redTargets,
            BlueTeamTargets = blueTargets,
            DamageTargets = damageTargets,
            ObstacleCandidates = obstacles,
            ArmorTargetsByEntityId = armorTargetsByEntityId,
        };
    }

    private static IReadOnlyList<ArmorPlateTarget> BuildEnergyMechanismTargetsForFrame(
        SimulationWorldState world,
        SimulationEntity entity,
        double metersPerWorldUnit)
    {
        var targets = new List<ArmorPlateTarget>(10);
        if (world.Teams.TryGetValue("red", out SimulationTeamState? redState))
        {
            targets.AddRange(SimulationCombatMath.GetEnergyMechanismTargets(entity, metersPerWorldUnit, world.GameTimeSec, "red", redState));
        }

        if (world.Teams.TryGetValue("blue", out SimulationTeamState? blueState))
        {
            targets.AddRange(SimulationCombatMath.GetEnergyMechanismTargets(entity, metersPerWorldUnit, world.GameTimeSec, "blue", blueState));
        }

        return targets.Count > 0
            ? targets
            : SimulationCombatMath.GetEnergyMechanismTargets(entity, metersPerWorldUnit, world.GameTimeSec);
    }

    private static void StoreAutoAimDiagnostics(
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        AutoAimSolution solution)
    {
        shooter.AutoAimLocked = true;
        shooter.AutoAimTargetId = target.Id;
        shooter.AutoAimPlateId = plate.Id;
        shooter.AutoAimTargetKind = SimulationCombatMath.ResolveAutoAimTargetKind(target, plate);
        shooter.AutoAimPlateDirection = solution.PlateDirection;
        shooter.AutoAimAccuracy = solution.Accuracy;
        shooter.AutoAimDistanceCoefficient = solution.DistanceCoefficient;
        shooter.AutoAimMotionCoefficient = solution.MotionCoefficient;
        shooter.AutoAimLeadTimeSec = solution.LeadTimeSec;
        shooter.AutoAimLeadDistanceM = solution.LeadDistanceM;
        shooter.AutoAimAimPointX = solution.AimPointX;
        shooter.AutoAimAimPointY = solution.AimPointY;
        shooter.AutoAimAimPointHeightM = solution.AimPointHeightM;
        shooter.AutoAimObservedVelocityXMps = solution.ObservedVelocityXMps;
        shooter.AutoAimObservedVelocityYMps = solution.ObservedVelocityYMps;
        shooter.AutoAimObservedVelocityZMps = solution.ObservedVelocityZMps;
        shooter.AutoAimObservedAngularVelocityRadPerSec = solution.ObservedAngularVelocityRadPerSec;
        shooter.AutoAimHasSmoothedAim = true;
        shooter.AutoAimLockKey = $"{target.Id}:{plate.Id}";
        shooter.AutoAimSmoothedYawDeg = solution.YawDeg;
        shooter.AutoAimSmoothedPitchDeg = solution.PitchDeg;
    }

    private static void ClearAutoAimState(SimulationEntity entity)
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
        entity.HeroLobPitchHoldLockKey = null;
        entity.HeroLobPitchHoldDeg = 0.0;
        entity.HeroLobPitchHoldTargetHeightM = 0.0;
    }

    private static bool CanShoot(SimulationEntity entity)
    {
        if (entity.IsSimulationSuppressed
            || !entity.IsAlive
            || entity.HeatLockTimerSec > 0
            || string.Equals(entity.State, "heat_locked", StringComparison.OrdinalIgnoreCase)
            || entity.RespawnAmmoLockTimerSec > 0
            || string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SimulationCombatMath.IsStructure(entity))
        {
            return false;
        }

        if (entity.IsPlayerControlled && !entity.IsFireCommandActive)
        {
            return false;
        }

        return true;
    }

    private bool WouldNextShotExceedHeatLimit(SimulationEntity entity)
    {
        if (entity.MaxHeat <= 1e-6)
        {
            return false;
        }

        double gain = ResolveShotHeatGain(entity);
        return entity.Heat + gain > entity.MaxHeat + 1e-6;
    }

    private static SimulationEntity? FindNearestEnemy(SimulationWorldState world, SimulationEntity shooter)
    {
        SimulationEntity? nearest = null;
        double nearestDistanceSquared = double.MaxValue;

        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double dx = candidate.X - shooter.X;
            double dy = candidate.Y - shooter.Y;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                nearest = candidate;
            }
        }

        return nearest;
    }

    private static bool HasRoughAutoAimCandidate(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM)
    {
        double effectiveMaxDistanceM = SimulationCombatMath.IsHeroNormalAutoAimMode(shooter)
            ? Math.Min(maxDistanceM, SimulationCombatMath.HeroNormalAutoAimMaxDistanceM)
            : maxDistanceM;
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double maxDistanceWorld = Math.Max(0.0, effectiveMaxDistanceM) / metersPerWorldUnit;
        double maxDistanceSquared = (maxDistanceWorld + 1.5) * (maxDistanceWorld + 1.5);
        bool energyMode = string.Equals(shooter.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase);
        bool heroLobMode = SimulationCombatMath.IsHeroLobAutoAimMode(shooter);
        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || string.Equals(candidate.Id, shooter.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool energyCandidate = string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
            if (energyMode)
            {
                if (!energyCandidate)
                {
                    continue;
                }
            }
            else
            {
                if (energyCandidate || string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (heroLobMode && !SimulationCombatMath.IsHeroStructureAutoAimTargetEntity(world, shooter, candidate))
                {
                    continue;
                }

                if (SimulationCombatMath.IsHeroNormalAutoAimMode(shooter) && SimulationCombatMath.IsStructure(candidate))
                {
                    continue;
                }
            }

            double dx = candidate.X - shooter.X;
            double dy = candidate.Y - shooter.Y;
            double radius = candidate.CollisionRadiusWorld > 1e-6
                ? candidate.CollisionRadiusWorld
                : Math.Max(0.15, Math.Max(candidate.BodyLengthM, candidate.BodyWidthM) * 0.5);
            double allowed = maxDistanceWorld + radius + 0.4;
            if (dx * dx + dy * dy <= Math.Min(maxDistanceSquared, allowed * allowed))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveLockedArmorTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate)
    {
        target = null;
        plate = default;
        if (!shooter.AutoAimLocked
            || !SimulationCombatMath.IsArmorAutoAimTargetKind(shooter.AutoAimTargetKind)
            || string.Equals(shooter.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return false;
        }

        target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && !string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        plate = SimulationCombatMath.GetAttackableArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, shooter.AutoAimPlateId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(plate.Id))
        {
            target = null;
            plate = default;
            return false;
        }

        double dxWorld = plate.X - shooter.X;
        double dyWorld = plate.Y - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        if (!SimulationCombatMath.IsAutoAimArmorTargetEligible(world, shooter, target, plate, distanceM)
            || distanceM > maxDistanceM + 0.4
            || !IsArmorPlateFacingShooter(world, shooter, target, plate))
        {
            target = null;
            plate = default;
            return false;
        }

        if (_canSeeAutoAimPlate is not null
            && !_canSeeAutoAimPlate(world, shooter, target, plate))
        {
            target = null;
            plate = default;
            return false;
        }

        return true;
    }

    private static bool IsArmorPlateFacingShooter(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        if (SimulationCombatMath.ShouldUseHeroLobStructureAxisAim(world, shooter, target, plate))
        {
            return true;
        }

        if (plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        double toShooterYawDeg = SimulationCombatMath.NormalizeDeg(Math.Atan2(shooter.Y - plate.Y, shooter.X - plate.X) * 180.0 / Math.PI);
        double facingError = Math.Abs(SimulationCombatMath.NormalizeSignedDeg(toShooterYawDeg - plate.YawDeg));
        return facingError <= 94.0;
    }

    private static bool IsHeroLobStructureTargetKind(string? targetKind)
        => string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetKind, "base_armor", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeroLobFireWindowReady(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget lockedPlate)
    {
        AutoAimSolution solution = TryResolveStoredAutoAimSolution(shooter, target, lockedPlate, out AutoAimSolution storedSolution)
            ? storedSolution
            : SimulationCombatMath.ComputeAutoAimSolution(world, shooter, target, lockedPlate, 1000.0);
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        AutoAimCompensationProfile compensation = SimulationCombatMath.ResolveAutoAimCompensationProfile(world, shooter, target, lockedPlate);
        double compensatedLeadTimeSec = Math.Clamp(solution.LeadTimeSec + compensation.TimeBiasSec, 0.0, 2.35);
        double predictedTimeSec = world.GameTimeSec + compensatedLeadTimeSec;
        ArmorPlateTarget plate = SimulationCombatMath.GetAttackableArmorPlateTargets(target, metersPerWorldUnit, predictedTimeSec)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, lockedPlate.Id, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(plate.Id))
        {
            plate = lockedPlate;
        }

        if (SimulationCombatMath.TryPredictOutpostRingPlatePose(world, target, lockedPlate, compensatedLeadTimeSec, out ArmorPlateTarget predictedOutpostRing))
        {
            plate = predictedOutpostRing;
        }

        plate = ResolveHeroLobPredictedAimPlate(plate, solution);
        double yawDeg = shooter.TurretYawDeg;
        double pitchDeg = shooter.GimbalPitchDeg;
        return IsHeroLobFireWindowReadyForPose(world, shooter, plate, yawDeg, pitchDeg);
    }

    private static bool IsHeroLobFireWindowReadyForPose(
        SimulationWorldState world,
        SimulationEntity shooter,
        ArmorPlateTarget plate,
        double yawDeg,
        double pitchDeg)
    {
        if (!TryProjectCrosshairToPredictedArmorPlate(
                world,
                shooter,
                plate,
                yawDeg,
                pitchDeg,
                out double horizontalOffsetM,
                out double verticalOffsetM,
                out bool frontFacing))
        {
            return false;
        }

        if (!frontFacing)
        {
            return false;
        }

        double widthM = Math.Clamp(plate.WidthM > 1e-6 ? plate.WidthM : plate.SideLengthM, 0.03, 0.60);
        double heightM = Math.Clamp(plate.HeightSpanM > 1e-6 ? plate.HeightSpanM : plate.SideLengthM, 0.03, 0.60);
        double projectileMarginM = Math.Clamp(
            SimulationCombatMath.ProjectileDiameterM(shooter.AmmoType) * 1.05 + shooter.AutoAimLeadDistanceM * 0.0024,
            0.018,
            0.085);
        double centerPlaneHeightErrorM = SimulationCombatMath.EstimateProjectileHeightErrorAtPoint(
            world,
            shooter,
            plate.X,
            plate.Y,
            plate.HeightM,
            pitchDeg);
        double centerPlaneToleranceM = Math.Clamp(Math.Max(0.18, heightM * 0.88 + projectileMarginM), 0.18, 0.34);
        return Math.Abs(horizontalOffsetM) <= widthM * 0.58 + projectileMarginM
            && Math.Abs(verticalOffsetM) <= heightM * 0.58 + projectileMarginM
            && Math.Abs(centerPlaneHeightErrorM) <= centerPlaneToleranceM;
    }

    private static ArmorPlateTarget ResolveHeroLobPredictedAimPlate(
        ArmorPlateTarget posePlate,
        AutoAimSolution solution)
    {
        if (!double.IsFinite(solution.AimPointX)
            || !double.IsFinite(solution.AimPointY)
            || Math.Abs(solution.AimPointX) + Math.Abs(solution.AimPointY) <= 1e-8)
        {
            return posePlate;
        }

        double aimHeightM = double.IsFinite(solution.AimPointHeightM) && solution.AimPointHeightM > 1e-6
            ? solution.AimPointHeightM
            : posePlate.HeightM;
        return posePlate with
        {
            X = solution.AimPointX,
            Y = solution.AimPointY,
            HeightM = aimHeightM,
        };
    }

    private static bool TryProjectCrosshairToPredictedArmorPlate(
        SimulationWorldState world,
        SimulationEntity shooter,
        ArmorPlateTarget plate,
        double yawDeg,
        double pitchDeg,
        out double horizontalOffsetM,
        out double verticalOffsetM,
        out bool frontFacing)
    {
        horizontalOffsetM = 0.0;
        verticalOffsetM = 0.0;
        frontFacing = true;

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        (double muzzleX, double muzzleY, double muzzleHeightM) = SimulationCombatMath.ComputeMuzzlePoint(world, shooter, pitchDeg);
        Vector3 start = new(
            (float)(muzzleX * metersPerWorldUnit),
            (float)muzzleHeightM,
            (float)(muzzleY * metersPerWorldUnit));
        Vector3 center = new(
            (float)(plate.X * metersPerWorldUnit),
            (float)plate.HeightM,
            (float)(plate.Y * metersPerWorldUnit));
        Vector3 normal = SimulationCombatMath.ResolveArmorPlateNormal(plate);
        if (normal.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        normal = Vector3.Normalize(normal);
        double yawRad = yawDeg * Math.PI / 180.0;
        double pitchRad = Math.Clamp(pitchDeg, -40.0, 40.0) * Math.PI / 180.0;
        Vector3 direction = new(
            (float)(Math.Cos(pitchRad) * Math.Cos(yawRad)),
            (float)Math.Sin(pitchRad),
            (float)(Math.Cos(pitchRad) * Math.Sin(yawRad)));
        if (direction.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        direction = Vector3.Normalize(direction);
        double denom = Vector3.Dot(direction, normal);
        if (Math.Abs(denom) <= 1e-6)
        {
            return false;
        }

        double t = Vector3.Dot(center - start, normal) / denom;
        if (t <= 0.02 || t > 200.0)
        {
            return false;
        }

        frontFacing = plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase) || denom <= -0.03;
        Vector3 crosshairPoint = start + direction * (float)t;
        double plateYawRad = plate.YawDeg * Math.PI / 180.0;
        Vector3 side = new((float)-Math.Sin(plateYawRad), 0f, (float)Math.Cos(plateYawRad));
        if (side.LengthSquared() <= 1e-8f)
        {
            side = Vector3.UnitX;
        }
        else
        {
            side = Vector3.Normalize(side);
        }

        Vector3 up = plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase)
            ? Vector3.Cross(side, normal)
            : Vector3.UnitY;
        up = up.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(up);
        Vector3 delta = crosshairPoint - center;
        horizontalOffsetM = Vector3.Dot(delta, side);
        verticalOffsetM = Vector3.Dot(delta, up);
        return true;
    }

    private static bool TryIntersectBallisticPlane(
        Vector3 start,
        Vector3 velocity,
        Vector3 planeCenter,
        Vector3 planeNormal,
        out double tSec)
    {
        tSec = 0.0;
        double a = -0.5 * 9.81 * planeNormal.Y;
        double b = Vector3.Dot(velocity, planeNormal);
        double c = Vector3.Dot(start - planeCenter, planeNormal);
        if (Math.Abs(a) <= 1e-8)
        {
            if (Math.Abs(b) <= 1e-8)
            {
                return false;
            }

            double linearT = -c / b;
            if (linearT > 0.02 && linearT <= 6.0)
            {
                tSec = linearT;
                return true;
            }

            return false;
        }

        double discriminant = b * b - 4.0 * a * c;
        if (discriminant < 0.0)
        {
            return false;
        }

        double sqrt = Math.Sqrt(discriminant);
        double t0 = (-b - sqrt) / (2.0 * a);
        double t1 = (-b + sqrt) / (2.0 * a);
        double best = double.PositiveInfinity;
        if (t0 > 0.02 && t0 <= 6.0)
        {
            best = t0;
        }

        if (t1 > 0.02 && t1 <= 6.0)
        {
            best = Math.Min(best, t1);
        }

        if (!double.IsFinite(best))
        {
            return false;
        }

        tSec = best;
        return true;
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

    private void ApplyShotHeat(SimulationEntity shooter)
    {
        shooter.Heat += ResolveShotHeatGain(shooter);

        if (shooter.Heat > shooter.MaxHeat && shooter.MaxHeat > 0)
        {
            if (!string.Equals(shooter.State, "heat_locked", StringComparison.OrdinalIgnoreCase)
                || shooter.HeatLockInitialHeat < shooter.Heat)
            {
                shooter.HeatLockInitialHeat = Math.Max(shooter.Heat, shooter.MaxHeat);
            }

            shooter.HeatLockTimerSec = Math.Max(shooter.HeatLockTimerSec, 0.10);
            shooter.State = "heat_locked";
        }
    }

    private double ResolveShotHeatGain(SimulationEntity shooter)
        => string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? _rules.Heat.HeatGain42
            : _rules.Heat.HeatGain17;

    private double ComputeDamage(SimulationEntity shooter, SimulationEntity target, ArmorPlateTarget hitPlate, bool criticalHit = false)
    {
        double baseDamage = ResolveBaseDamage(shooter, target, hitPlate);
        ResolvedRoleProfile shooterProfile = _rules.ResolveRuntimeProfile(shooter);
        ResolvedRoleProfile targetProfile = _rules.ResolveRuntimeProfile(target);

        double dealtMultiplier = ResolveDominantMultiplier(preferHigherOnTie: true,
            shooter.DynamicDamageDealtMult,
            shooterProfile.DamageDealtMultiplier);
        if (ShouldApplyHeroDeploymentBaseDamageBoost(shooter, target))
        {
            dealtMultiplier = ResolveDominantMultiplier(preferHigherOnTie: true, dealtMultiplier, 1.50);
        }

        if (shooter.WeakTimerSec > 0)
        {
            dealtMultiplier = ResolveDominantMultiplier(preferHigherOnTie: true, dealtMultiplier, _rules.Respawn.WeakenDamageDealtMult);
        }

        double takenMultiplier = ResolveDominantMultiplier(preferHigherOnTie: false,
            target.DynamicDamageTakenMult,
            targetProfile.DamageTakenMultiplier);
        if (target.WeakTimerSec > 0)
        {
            takenMultiplier = ResolveDominantMultiplier(preferHigherOnTie: false, takenMultiplier, _rules.Respawn.WeakenDamageTakenMult);
        }

        double criticalMultiplier = criticalHit ? 1.50 : 1.0;
        return Math.Max(0.0, Math.Round(baseDamage * dealtMultiplier * takenMultiplier * criticalMultiplier, 0, MidpointRounding.AwayFromZero));
    }

    private static double ResolveDominantMultiplier(bool preferHigherOnTie, params double[] multipliers)
    {
        double best = 1.0;
        double bestDelta = 0.0;
        foreach (double multiplier in multipliers)
        {
            if (!double.IsFinite(multiplier) || multiplier < 0.0)
            {
                continue;
            }

            double delta = Math.Abs(multiplier - 1.0);
            if (delta > bestDelta + 1e-9
                || (Math.Abs(delta - bestDelta) <= 1e-9
                    && ((preferHigherOnTie && multiplier > best) || (!preferHigherOnTie && multiplier < best))))
            {
                best = multiplier;
                bestDelta = delta;
            }
        }

        return best;
    }

    private static bool ShouldApplyHeroDeploymentBaseDamageBoost(SimulationEntity shooter, SimulationEntity target)
    {
        return shooter.HeroDeploymentActive
            && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase);
    }

    private double ResolveBaseDamage(SimulationEntity shooter, SimulationEntity target, ArmorPlateTarget hitPlate)
    {
        bool ammo42 = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        if (ammo42)
        {
            if (string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
            {
                return _rules.Combat.Damage42ToOutpostArmor;
            }

            return SimulationCombatMath.IsStructure(target)
                ? _rules.Combat.Damage42ToStructure
                : _rules.Combat.Damage42ToRobot;
        }

        if (string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            return IsBaseFrontUpperArmor(hitPlate)
                ? _rules.Combat.Damage17ToBaseFrontUpperArmor
                : _rules.Combat.Damage17ToBaseOtherArmor;
        }

        if (string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            return _rules.Combat.Damage17ToOutpostArmor;
        }

        return SimulationCombatMath.IsStructure(target)
            ? _rules.Combat.Damage17ToStructure
            : _rules.Combat.Damage17ToRobot;
    }

    private static bool IsBaseFrontUpperArmor(ArmorPlateTarget hitPlate)
        => hitPlate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase)
            || hitPlate.Id.Equals("base_front_upper", StringComparison.OrdinalIgnoreCase)
            || hitPlate.Id.Contains("front_upper", StringComparison.OrdinalIgnoreCase);

    private static bool IsBaseProtectedByLivingOutpost(SimulationWorldState world, SimulationEntity target)
    {
        if (!string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (SimulationEntity candidate in world.Entities)
        {
            if (candidate.IsAlive
                && string.Equals(candidate.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Team, target.Team, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private double ApplyDamage(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        double damage,
        SimulationRunReport report,
        out string damageResult)
    {
        if (IsBaseProtectedByLivingOutpost(world, target))
        {
            damageResult = "base_protected";
            return 0.0;
        }

        if (target.RespawnInvincibleTimerSec > 1e-6)
        {
            damageResult = "invincible";
            return 0.0;
        }

        target.Health -= damage;
        ArenaInteractionService.GrantDamageExperience(shooter, target, damage);
        if (target.Health > 0)
        {
            damageResult = "applied";
            return damage;
        }

        target.Health = 0;
        target.IsAlive = false;
        target.DestroyedTimeSec = world.GameTimeSec;
        target.TraversalActive = false;
        ClearAutoAimState(target);
        target.Heat = 0.0;
        target.HeatLockInitialHeat = 0.0;
        target.BufferEnergyJ = target.MaxBufferEnergyJ;
        target.FireCooldownSec = 0.0;
        target.HeatLockTimerSec = 0.0;
        target.RespawnAmmoLockTimerSec = 0.0;
        target.RespawnInvincibleTimerSec = 0.0;
        target.TerrainSequenceKey = string.Empty;
        target.TerrainSequenceTimerSec = 0.0;
        target.TerrainRoadLockoutTimerSec = 0.0;
        target.TerrainHighlandDefenseTimerSec = 0.0;
        target.TerrainFlySlopeDefenseTimerSec = 0.0;
        target.TerrainRoadCoolingTimerSec = 0.0;
        target.TerrainSlopeDefenseTimerSec = 0.0;
        target.TerrainSlopeCoolingTimerSec = 0.0;
        target.HeroDeploymentHoldTimerSec = 0.0;
        target.HeroDeploymentExitHoldTimerSec = 0.0;
        target.HeroDeploymentRequested = false;
        target.HeroDeploymentActive = false;
        target.SuperCapEnabled = false;
        target.Power = target.MaxPower;
        if (target.MaxChassisEnergy > 1e-6)
        {
            target.ChassisEnergy = target.MaxChassisEnergy;
        }

        if (string.Equals(target.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            ArenaInteractionService.GrantKillExperience(shooter, target);
            target.State = "respawning";
            target.RespawnTimerSec = ComputeRespawnDelaySec(world, target);
            report.LifecycleEvents.Add(new SimulationLifecycleEvent(
                world.GameTimeSec,
                target.Id,
                target.Team,
                "death",
                $"{shooter.Id} eliminated {target.Id}"));
            damageResult = "destroyed";
            return damage;
        }

        target.State = "destroyed";
        target.PermanentEliminated = true;
        report.LifecycleEvents.Add(new SimulationLifecycleEvent(
            world.GameTimeSec,
            target.Id,
            target.Team,
            "destroyed",
            $"{shooter.Id} destroyed {target.Id}"));

        if (string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            string winner = string.Equals(target.Team, "red", StringComparison.OrdinalIgnoreCase) ? "blue" : "red";
            world.GetOrCreateTeamState(winner).Gold += 500.0;
        }

        damageResult = "destroyed";
        return damage;
    }

    private void RespawnEntity(SimulationEntity entity)
    {
        entity.IsAlive = true;
        entity.DestroyedTimeSec = double.NegativeInfinity;
        entity.State = "weak";
        ResolvedRoleProfile profile = _rules.ResolveRuntimeProfile(entity);
        ApplyResolvedRoleProfile(entity, profile, clampHealthToCurrent: true);
        entity.Health = Math.Max(1.0, entity.MaxHealth * 0.10);
        double recoveryLockSec = Math.Max(RespawnRecoveryZoneLockSec, _rules.Respawn.InvalidDurationSec);
        entity.WeakTimerSec = recoveryLockSec;
        entity.RespawnAmmoLockTimerSec = recoveryLockSec;
        entity.RespawnInvincibleTimerSec = Math.Max(30.0, _rules.Respawn.InvincibleDurationSec);
        entity.Heat = 0;
        entity.HeatLockInitialHeat = 0;
        entity.BufferEnergyJ = entity.MaxBufferEnergyJ;
        entity.HeatLockTimerSec = 0;
        entity.FireCooldownSec = 0;
        entity.TraversalActive = false;
        entity.AirborneHeightM = 0.0;
        entity.VerticalVelocityMps = 0.0;
        entity.JumpCrouchTimerSec = 0.0;
        entity.JumpCrouchDurationSec = 0.0;
        entity.LandingCompressionM = 0.0;
        entity.LandingCompressionVelocityMps = 0.0;
        entity.TerrainSequenceKey = string.Empty;
        entity.TerrainSequenceTimerSec = 0.0;
        entity.TerrainRoadLockoutTimerSec = 0.0;
        entity.TerrainHighlandDefenseTimerSec = 0.0;
        entity.TerrainFlySlopeDefenseTimerSec = 0.0;
        entity.TerrainRoadCoolingTimerSec = 0.0;
        entity.TerrainSlopeDefenseTimerSec = 0.0;
        entity.TerrainSlopeCoolingTimerSec = 0.0;
        entity.HeroDeploymentHoldTimerSec = 0.0;
        entity.HeroDeploymentExitHoldTimerSec = 0.0;
        ClearAutoAimState(entity);

        entity.Power = profile.MaxPower;
        entity.AmmoType = string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
            ? "none"
            : profile.AmmoType;
        if (entity.MaxChassisEnergy > 1e-6)
        {
            entity.ChassisEnergy = entity.MaxChassisEnergy;
        }

        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)
            && string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            entity.Ammo42Mm = Math.Max(entity.Ammo42Mm, profile.InitialAllowedAmmo42Mm);
        }
        else if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            entity.Ammo17Mm = Math.Max(entity.Ammo17Mm, profile.InitialAllowedAmmo17Mm / 2);
        }
    }

    private double ComputeRespawnDelaySec(SimulationWorldState world, SimulationEntity entity)
    {
        double durationSec = Math.Max(1.0, _rules.GameDurationSec);
        double remainingSec = Math.Max(0.0, durationSec - world.GameTimeSec);
        double elapsedFromRoundedRemainingSec = Math.Max(0.0, durationSec - Math.Round(remainingSec));
        double ruleDelaySec = 10.0 + elapsedFromRoundedRemainingSec / 10.0 + 20.0 * Math.Max(0, entity.InstantReviveCount);
        return Math.Max(_rules.Respawn.RobotDelaySec, ruleDelaySec);
    }

    private static double ResolveRespawnProgressRate(
        SimulationWorldState world,
        IReadOnlyList<Simulator.Core.Map.FacilityRegion> facilities,
        SimulationEntity entity)
    {
        if (IsInFriendlySupplyZone(facilities, entity))
        {
            return 4.0;
        }

        SimulationEntity? baseEntity = world.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Team, entity.Team, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.EntityType, "base", StringComparison.OrdinalIgnoreCase));
        if (baseEntity is not null && baseEntity.Health < 2000.0)
        {
            return 4.0;
        }

        return 1.0;
    }

    private static bool IsInFriendlySupplyZone(
        IReadOnlyList<Simulator.Core.Map.FacilityRegion> facilities,
        SimulationEntity entity)
    {
        return facilities.Any(region =>
            (string.Equals(region.Type, "supply", StringComparison.OrdinalIgnoreCase)
                || string.Equals(region.Type, "buff_supply", StringComparison.OrdinalIgnoreCase)
                || string.Equals(region.Type, "buff_base", StringComparison.OrdinalIgnoreCase))
            && string.Equals(region.Team, entity.Team, StringComparison.OrdinalIgnoreCase)
            && region.Contains(entity.X, entity.Y));
    }

    private static bool IsInFriendlyRespawnRecoveryZone(
        IReadOnlyList<Simulator.Core.Map.FacilityRegion> facilities,
        SimulationEntity entity)
    {
        return facilities.Any(region =>
            (string.Equals(region.Type, "supply", StringComparison.OrdinalIgnoreCase)
                || string.Equals(region.Type, "buff_supply", StringComparison.OrdinalIgnoreCase))
            && string.Equals(region.Team, entity.Team, StringComparison.OrdinalIgnoreCase)
            && region.Contains(entity.X, entity.Y));
    }

    private static bool ShouldRequireRespawnRecoveryZone(SimulationEntity entity)
    {
        return entity.IsAlive
            && string.Equals(entity.State, "weak", StringComparison.OrdinalIgnoreCase)
            && (entity.WeakTimerSec > 1e-6 || entity.RespawnAmmoLockTimerSec > 1e-6);
    }

    private static bool HasAmmoForShot(SimulationEntity entity)
    {
        if (entity.UnlimitedAmmo
            && !string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            return entity.Ammo42Mm > 0;
        }

        return !string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase)
            && entity.Ammo17Mm > 0;
    }

    private static void EnsureOpeningPurchasedAmmo(SimulationEntity entity)
    {
        if (entity.ShotsFired > 0)
        {
            return;
        }

        if (string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            && string.Equals(entity.AmmoType, "17mm", StringComparison.OrdinalIgnoreCase))
        {
            entity.Ammo17Mm = Math.Max(entity.Ammo17Mm, 100);
            return;
        }

        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            entity.Ammo42Mm = Math.Max(entity.Ammo42Mm, 10);
        }
    }

    private double ResolveFireRateHz(SimulationEntity entity)
    {
        double fireRate = _rules.Combat.FireRateHz;
        if (string.Equals(entity.AmmoType, "17mm", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            fireRate = Math.Max(fireRate, 20.0);
        }

        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)
            && string.Equals(RuleSet.NormalizeSentryControlMode(entity.SentryControlMode), "semi_auto", StringComparison.OrdinalIgnoreCase))
        {
            fireRate *= 0.55;
        }

        return fireRate;
    }

    private static void ApplyResolvedRoleProfile(SimulationEntity entity, ResolvedRoleProfile profile, bool clampHealthToCurrent)
    {
        double previousMaxHealth = Math.Max(entity.MaxHealth, 0.0);
        double previousHealthRatio = previousMaxHealth > 1e-6
            ? Math.Clamp(entity.Health / previousMaxHealth, 0.0, 1.0)
            : (entity.Health > 1e-6 ? 1.0 : 0.0);
        entity.MaxLevel = profile.MaxLevel;
        entity.MaxHealth = profile.MaxHealth;
        entity.MaxPower = profile.MaxPower;
        entity.MaxHeat = profile.MaxHeat;
        entity.AmmoType = profile.AmmoType;
        if (string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            entity.AmmoType = "none";
            entity.Ammo17Mm = 0;
            entity.Ammo42Mm = 0;
        }

        entity.RuleDrivePowerLimitW = profile.MaxPower;
        entity.MaxChassisEnergy = profile.MaxChassisEnergy;
        entity.ChassisEcoPowerLimitW = profile.EcoPowerLimitW;
        entity.ChassisBoostThresholdEnergy = profile.BoostThresholdEnergy;
        entity.ChassisBoostMultiplier = profile.BoostPowerMultiplier;
        entity.ChassisBoostPowerCapW = profile.BoostPowerCapW;
        entity.MaxBufferEnergyJ = Math.Max(0.0, entity.MaxBufferEnergyJ <= 1e-6 ? 60.0 : entity.MaxBufferEnergyJ);
        entity.BufferReserveEnergyJ = Math.Clamp(entity.BufferReserveEnergyJ <= 1e-6 ? 10.0 : entity.BufferReserveEnergyJ, 0.0, entity.MaxBufferEnergyJ);
        entity.MaxSuperCapEnergyJ = Math.Max(0.0, entity.MaxSuperCapEnergyJ <= 1e-6 ? 2000.0 : entity.MaxSuperCapEnergyJ);

        if (clampHealthToCurrent)
        {
            entity.Health = entity.Health <= 1e-6
                ? 0.0
                : Math.Clamp(entity.MaxHealth * previousHealthRatio, 0.0, entity.MaxHealth);
            entity.Power = Math.Min(entity.Power, entity.MaxPower);
            entity.Heat = Math.Min(entity.Heat, entity.MaxHeat);
            entity.ChassisEnergy = Math.Min(entity.ChassisEnergy, entity.MaxChassisEnergy);
            entity.BufferEnergyJ = Math.Min(entity.BufferEnergyJ, entity.MaxBufferEnergyJ);
            entity.SuperCapEnergyJ = Math.Min(entity.SuperCapEnergyJ, entity.MaxSuperCapEnergyJ);
            return;
        }

        entity.Health = entity.MaxHealth;
        entity.Power = entity.MaxPower;
        entity.Heat = Math.Min(entity.Heat, entity.MaxHeat);
        entity.ChassisEnergy = profile.UsesChassisEnergy ? profile.InitialChassisEnergy : 0.0;
        entity.BufferEnergyJ = entity.MaxBufferEnergyJ;
        entity.SuperCapEnergyJ = entity.MaxSuperCapEnergyJ;
    }

    private static SimulationEntity CloneEntity(SimulationEntity source)
    {
        return new SimulationEntity
        {
            Id = source.Id,
            Team = source.Team,
            EntityType = source.EntityType,
            RoleKey = source.RoleKey,
            Level = source.Level,
            MaxLevel = source.MaxLevel,
            Experience = source.Experience,
            PendingExperienceDisplay = source.PendingExperienceDisplay,
            PendingLevelUpCount = source.PendingLevelUpCount,
            RuntimeModelToSceneMatrix = source.RuntimeModelToSceneMatrix,
            RuntimeModelToWorldMatrix = source.RuntimeModelToWorldMatrix,
            HeroPerformanceMode = source.HeroPerformanceMode,
            InfantryDurabilityMode = source.InfantryDurabilityMode,
            InfantryWeaponMode = source.InfantryWeaponMode,
            SentryControlMode = source.SentryControlMode,
            SentryStance = source.SentryStance,
            X = source.X,
            Y = source.Y,
            GroundHeightM = source.GroundHeightM,
            AirborneHeightM = source.AirborneHeightM,
            VerticalVelocityMps = source.VerticalVelocityMps,
            JumpCrouchTimerSec = source.JumpCrouchTimerSec,
            JumpCrouchDurationSec = source.JumpCrouchDurationSec,
            LandingCompressionM = source.LandingCompressionM,
            LandingCompressionVelocityMps = source.LandingCompressionVelocityMps,
            LedgeLaunchTimerSec = source.LedgeLaunchTimerSec,
            AngleDeg = source.AngleDeg,
            ChassisPitchDeg = source.ChassisPitchDeg,
            ChassisRollDeg = source.ChassisRollDeg,
            TurretYawDeg = source.TurretYawDeg,
            GimbalPitchDeg = source.GimbalPitchDeg,
            AutoAimRequested = source.AutoAimRequested,
            AutoAimGuidanceOnly = source.AutoAimGuidanceOnly,
            HeroAutoAimMode = source.HeroAutoAimMode,
            AutoAimLocked = source.AutoAimLocked,
            AutoAimTargetId = source.AutoAimTargetId,
            AutoAimPlateId = source.AutoAimPlateId,
            AutoAimTargetKind = source.AutoAimTargetKind,
            AutoAimPlateDirection = source.AutoAimPlateDirection,
            AutoAimAccuracy = source.AutoAimAccuracy,
            AutoAimAccuracyScale = source.AutoAimAccuracyScale,
            AutoAimDistanceCoefficient = source.AutoAimDistanceCoefficient,
            AutoAimMotionCoefficient = source.AutoAimMotionCoefficient,
            AutoAimLeadTimeSec = source.AutoAimLeadTimeSec,
            AutoAimLeadDistanceM = source.AutoAimLeadDistanceM,
            AutoAimAimPointX = source.AutoAimAimPointX,
            AutoAimAimPointY = source.AutoAimAimPointY,
            AutoAimAimPointHeightM = source.AutoAimAimPointHeightM,
            AutoAimObservedVelocityXMps = source.AutoAimObservedVelocityXMps,
            AutoAimObservedVelocityYMps = source.AutoAimObservedVelocityYMps,
            AutoAimObservedVelocityZMps = source.AutoAimObservedVelocityZMps,
            AutoAimObservedAngularVelocityRadPerSec = source.AutoAimObservedAngularVelocityRadPerSec,
            AutoAimHasSmoothedAim = source.AutoAimHasSmoothedAim,
            AutoAimLockKey = source.AutoAimLockKey,
            AutoAimSmoothedYawDeg = source.AutoAimSmoothedYawDeg,
            AutoAimSmoothedPitchDeg = source.AutoAimSmoothedPitchDeg,
            HeroLobPitchHoldLockKey = source.HeroLobPitchHoldLockKey,
            HeroLobPitchHoldDeg = source.HeroLobPitchHoldDeg,
            HeroLobPitchHoldTargetHeightM = source.HeroLobPitchHoldTargetHeightM,
            HeroDeploymentYawCorrectionDeg = source.HeroDeploymentYawCorrectionDeg,
            HeroDeploymentPitchCorrectionDeg = source.HeroDeploymentPitchCorrectionDeg,
            HeroDeploymentLastPitchErrorDeg = source.HeroDeploymentLastPitchErrorDeg,
            HeroDeploymentCorrectionPlateId = source.HeroDeploymentCorrectionPlateId,
            AutoAimYawCorrectionDeg = source.AutoAimYawCorrectionDeg,
            AutoAimPitchCorrectionDeg = source.AutoAimPitchCorrectionDeg,
            AutoAimCorrectionLockKey = source.AutoAimCorrectionLockKey,
            VelocityXWorldPerSec = source.VelocityXWorldPerSec,
            VelocityYWorldPerSec = source.VelocityYWorldPerSec,
            ObservedVelocityXWorldPerSec = source.ObservedVelocityXWorldPerSec,
            ObservedVelocityYWorldPerSec = source.ObservedVelocityYWorldPerSec,
            AngularVelocityDegPerSec = source.AngularVelocityDegPerSec,
            ObservedAngularVelocityDegPerSec = source.ObservedAngularVelocityDegPerSec,
            LastObservedX = source.LastObservedX,
            LastObservedY = source.LastObservedY,
            LastObservedAngleDeg = source.LastObservedAngleDeg,
            HasObservedKinematics = source.HasObservedKinematics,
            ChassisTargetYawDeg = source.ChassisTargetYawDeg,
            LastAimSpeedMps = source.LastAimSpeedMps,
            LastAimHeightM = source.LastAimHeightM,
            AutoAimInstabilityTimerSec = source.AutoAimInstabilityTimerSec,
            MoveInputForward = source.MoveInputForward,
            MoveInputRight = source.MoveInputRight,
            IsPlayerControlled = source.IsPlayerControlled,
            IsFireCommandActive = source.IsFireCommandActive,
            JumpRequested = source.JumpRequested,
            StepClimbModeActive = source.StepClimbModeActive,
            SmallGyroActive = source.SmallGyroActive,
            BuyAmmoRequested = source.BuyAmmoRequested,
            HeroDeploymentRequested = source.HeroDeploymentRequested,
            HeroDeploymentActive = source.HeroDeploymentActive,
            SuperCapEnabled = source.SuperCapEnabled,
            IsSimulationSuppressed = source.IsSimulationSuppressed,
            DirectStepHeightM = source.DirectStepHeightM,
            MaxStepClimbHeightM = source.MaxStepClimbHeightM,
            StepClimbDurationSec = source.StepClimbDurationSec,
            TraversalActive = source.TraversalActive,
            TraversalProgress = source.TraversalProgress,
            TraversalDirectionDeg = source.TraversalDirectionDeg,
            TraversalStartX = source.TraversalStartX,
            TraversalStartY = source.TraversalStartY,
            TraversalTargetX = source.TraversalTargetX,
            TraversalTargetY = source.TraversalTargetY,
            TraversalStartGroundHeightM = source.TraversalStartGroundHeightM,
            TraversalTargetGroundHeightM = source.TraversalTargetGroundHeightM,
            CollisionRadiusWorld = source.CollisionRadiusWorld,
            MotionBlockReason = source.MotionBlockReason,
            TacticalCommand = source.TacticalCommand,
            TacticalTargetId = source.TacticalTargetId,
            TacticalTargetX = source.TacticalTargetX,
            TacticalTargetY = source.TacticalTargetY,
            TacticalPatrolRadiusWorld = source.TacticalPatrolRadiusWorld,
            MassKg = source.MassKg,
            ChassisSpeedScale = source.ChassisSpeedScale,
            ChassisSupportsJump = source.ChassisSupportsJump,
            ChassisDrivePowerLimitW = source.ChassisDrivePowerLimitW,
            RuleDrivePowerLimitW = source.RuleDrivePowerLimitW,
            ChassisDriveIdleDrawW = source.ChassisDriveIdleDrawW,
            ChassisDriveRpmCoeff = source.ChassisDriveRpmCoeff,
            ChassisDriveAccelCoeff = source.ChassisDriveAccelCoeff,
            ChassisPowerDrawW = source.ChassisPowerDrawW,
            ChassisRpm = source.ChassisRpm,
            ChassisSpeedLimitMps = source.ChassisSpeedLimitMps,
            ChassisPowerRatio = source.ChassisPowerRatio,
            ChassisEnergy = source.ChassisEnergy,
            MaxChassisEnergy = source.MaxChassisEnergy,
            ChassisEcoPowerLimitW = source.ChassisEcoPowerLimitW,
            ChassisBoostThresholdEnergy = source.ChassisBoostThresholdEnergy,
            ChassisBoostMultiplier = source.ChassisBoostMultiplier,
            ChassisBoostPowerCapW = source.ChassisBoostPowerCapW,
            BufferEnergyJ = source.BufferEnergyJ,
            MaxBufferEnergyJ = source.MaxBufferEnergyJ,
            BufferReserveEnergyJ = source.BufferReserveEnergyJ,
            ChassisSubtype = source.ChassisSubtype,
            BodyShape = source.BodyShape,
            WheelStyle = source.WheelStyle,
            FrontClimbAssistStyle = source.FrontClimbAssistStyle,
            RearClimbAssistStyle = source.RearClimbAssistStyle,
            RearClimbAssistKneeDirection = source.RearClimbAssistKneeDirection,
            BodyLengthM = source.BodyLengthM,
            BodyWidthM = source.BodyWidthM,
            BodyHeightM = source.BodyHeightM,
            BodyClearanceM = source.BodyClearanceM,
            BodyRenderWidthScale = source.BodyRenderWidthScale,
            StructureBaseLiftM = source.StructureBaseLiftM,
            AnnotatedEnergyMechanism = source.AnnotatedEnergyMechanism,
            RuntimeEnergyTargetsGameTimeSec = source.RuntimeEnergyTargetsGameTimeSec,
            AnnotatedOutpost = source.AnnotatedOutpost,
            WheelRadiusM = source.WheelRadiusM,
            WheelOffsetsM = source.WheelOffsetsM.ToArray(),
            ArmorOrbitYawsDeg = source.ArmorOrbitYawsDeg.ToArray(),
            ArmorSelfYawsDeg = source.ArmorSelfYawsDeg.ToArray(),
            GimbalLengthM = source.GimbalLengthM,
            GimbalWidthM = source.GimbalWidthM,
            GimbalBodyHeightM = source.GimbalBodyHeightM,
            GimbalHeightM = source.GimbalHeightM,
            GimbalOffsetXM = source.GimbalOffsetXM,
            GimbalOffsetYM = source.GimbalOffsetYM,
            GimbalMountGapM = source.GimbalMountGapM,
            GimbalMountLengthM = source.GimbalMountLengthM,
            GimbalMountWidthM = source.GimbalMountWidthM,
            GimbalMountHeightM = source.GimbalMountHeightM,
            BarrelLengthM = source.BarrelLengthM,
            BarrelRadiusM = source.BarrelRadiusM,
            ArmorPlateWidthM = source.ArmorPlateWidthM,
            ArmorPlateLengthM = source.ArmorPlateLengthM,
            ArmorPlateHeightM = source.ArmorPlateHeightM,
            ArmorPlateGapM = source.ArmorPlateGapM,
            ArmorLightLengthM = source.ArmorLightLengthM,
            ArmorLightWidthM = source.ArmorLightWidthM,
            ArmorLightHeightM = source.ArmorLightHeightM,
            BarrelLightLengthM = source.BarrelLightLengthM,
            BarrelLightWidthM = source.BarrelLightWidthM,
            BarrelLightHeightM = source.BarrelLightHeightM,
            FrontClimbAssistTopLengthM = source.FrontClimbAssistTopLengthM,
            FrontClimbAssistBottomLengthM = source.FrontClimbAssistBottomLengthM,
            FrontClimbAssistPlateWidthM = source.FrontClimbAssistPlateWidthM,
            FrontClimbAssistPlateHeightM = source.FrontClimbAssistPlateHeightM,
            FrontClimbAssistForwardOffsetM = source.FrontClimbAssistForwardOffsetM,
            FrontClimbAssistInnerOffsetM = source.FrontClimbAssistInnerOffsetM,
            RearClimbAssistUpperLengthM = source.RearClimbAssistUpperLengthM,
            RearClimbAssistLowerLengthM = source.RearClimbAssistLowerLengthM,
            RearClimbAssistUpperWidthM = source.RearClimbAssistUpperWidthM,
            RearClimbAssistUpperHeightM = source.RearClimbAssistUpperHeightM,
            RearClimbAssistLowerWidthM = source.RearClimbAssistLowerWidthM,
            RearClimbAssistLowerHeightM = source.RearClimbAssistLowerHeightM,
            RearClimbAssistMountOffsetXM = source.RearClimbAssistMountOffsetXM,
            RearClimbAssistMountHeightM = source.RearClimbAssistMountHeightM,
            RearClimbAssistInnerOffsetM = source.RearClimbAssistInnerOffsetM,
            RearClimbAssistUpperPairGapM = source.RearClimbAssistUpperPairGapM,
            RearClimbAssistHingeRadiusM = source.RearClimbAssistHingeRadiusM,
            RearClimbAssistKneeMinDeg = source.RearClimbAssistKneeMinDeg,
            RearClimbAssistKneeMaxDeg = source.RearClimbAssistKneeMaxDeg,
            IsAlive = source.IsAlive,
            PermanentEliminated = source.PermanentEliminated,
            State = source.State,
            AiDecision = source.AiDecision,
            AiDecisionSelected = source.AiDecisionSelected,
            TestForcedDecisionId = source.TestForcedDecisionId,
            MaxHealth = source.MaxHealth,
            Health = source.Health,
            DestroyedTimeSec = source.DestroyedTimeSec,
            MaxPower = source.MaxPower,
            Power = source.Power,
            MaxHeat = source.MaxHeat,
            Heat = source.Heat,
            HeatLockInitialHeat = source.HeatLockInitialHeat,
            RespawnTimerSec = source.RespawnTimerSec,
            WeakTimerSec = source.WeakTimerSec,
            RespawnAmmoLockTimerSec = source.RespawnAmmoLockTimerSec,
            RespawnInvincibleTimerSec = source.RespawnInvincibleTimerSec,
            InstantReviveCount = source.InstantReviveCount,
            HeatLockTimerSec = source.HeatLockTimerSec,
            PowerCutTimerSec = source.PowerCutTimerSec,
            FireCooldownSec = source.FireCooldownSec,
            Ammo17Mm = source.Ammo17Mm,
            Ammo42Mm = source.Ammo42Mm,
            UnlimitedAmmo = source.UnlimitedAmmo,
            AmmoType = source.AmmoType,
            ShotsFired = source.ShotsFired,
            CarriedMinerals = source.CarriedMinerals,
            SupplyAccumulatorSec = source.SupplyAccumulatorSec,
            SupplyBuyCooldownSec = source.SupplyBuyCooldownSec,
            MiningProgressSec = source.MiningProgressSec,
            ExchangeProgressSec = source.ExchangeProgressSec,
            DeadZoneTimerSec = source.DeadZoneTimerSec,
            TerrainSequenceKey = source.TerrainSequenceKey,
            TerrainSequenceTimerSec = source.TerrainSequenceTimerSec,
            TerrainRoadLockoutTimerSec = source.TerrainRoadLockoutTimerSec,
            TerrainHighlandDefenseTimerSec = source.TerrainHighlandDefenseTimerSec,
            TerrainFlySlopeDefenseTimerSec = source.TerrainFlySlopeDefenseTimerSec,
            TerrainRoadCoolingTimerSec = source.TerrainRoadCoolingTimerSec,
            TerrainSlopeDefenseTimerSec = source.TerrainSlopeDefenseTimerSec,
            TerrainSlopeCoolingTimerSec = source.TerrainSlopeCoolingTimerSec,
            HeroDeploymentHoldTimerSec = source.HeroDeploymentHoldTimerSec,
            HeroDeploymentExitHoldTimerSec = source.HeroDeploymentExitHoldTimerSec,
            SuperCapEnergyJ = source.SuperCapEnergyJ,
            MaxSuperCapEnergyJ = source.MaxSuperCapEnergyJ,
            DynamicDamageTakenMult = source.DynamicDamageTakenMult,
            DynamicDamageDealtMult = source.DynamicDamageDealtMult,
            DynamicCoolingMult = source.DynamicCoolingMult,
            DynamicPowerRecoveryMult = source.DynamicPowerRecoveryMult,
        };
    }
}
