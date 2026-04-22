using System.Numerics;

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
    string DamagePreventedReason = "");

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

    private const double RespawnRecoveryZoneLockSec = 3.0;
    private sealed class ProjectileFrameCache
    {
        public required Dictionary<string, SimulationEntity> EntityById { get; init; }

        public required IReadOnlyList<SimulationEntity> RedTeamTargets { get; init; }

        public required IReadOnlyList<SimulationEntity> BlueTeamTargets { get; init; }

        public required IReadOnlyList<SimulationEntity> DamageTargets { get; init; }

        public required IReadOnlyList<SimulationEntity> ObstacleCandidates { get; init; }

        public required IReadOnlyDictionary<string, IReadOnlyList<ArmorPlateTarget>> ArmorTargetsByEntityId { get; init; }

        public IReadOnlyList<SimulationEntity> GetTargetCandidates(string shooterTeam)
            => string.Equals(shooterTeam, "red", StringComparison.OrdinalIgnoreCase)
                ? BlueTeamTargets
                : RedTeamTargets;
    }

    private readonly RuleSet _rules;
    private readonly ArenaInteractionService _interactionService;
    private readonly bool _enableAutoMovement;
    private readonly Func<SimulationWorldState, SimulationEntity, SimulationEntity, ArmorPlateTarget, bool>? _canSeeAutoAimPlate;
    private readonly Func<SimulationWorldState, SimulationEntity, SimulationProjectile, double, double, double, double, double, double, IReadOnlyList<SimulationEntity>?, ProjectileObstacleHit?>? _resolveProjectileObstacle;
    private readonly bool _projectileRicochetEnabled;
    private long _lastRulePerfLogTicks;

    public RuleSimulationService(
        RuleSet rules,
        ArenaInteractionService interactionService,
        int? seed = null,
        bool enableAutoMovement = true,
        Func<SimulationWorldState, SimulationEntity, SimulationEntity, ArmorPlateTarget, bool>? canSeeAutoAimPlate = null,
        Func<SimulationWorldState, SimulationEntity, SimulationProjectile, double, double, double, double, double, double, IReadOnlyList<SimulationEntity>?, ProjectileObstacleHit?>? resolveProjectileObstacle = null,
        bool projectileRicochetEnabled = false)
    {
        _rules = rules;
        _interactionService = interactionService;
        _enableAutoMovement = enableAutoMovement;
        _canSeeAutoAimPlate = canSeeAutoAimPlate;
        _resolveProjectileObstacle = resolveProjectileObstacle;
        _projectileRicochetEnabled = projectileRicochetEnabled;
    }

    public SimulationRunReport Run(
        SimulationWorldState world,
        IReadOnlyList<Simulator.Core.Map.FacilityRegion> facilities,
        double durationSec,
        double deltaTimeSec,
        bool captureFinalEntities = true)
    {
        double dt = Math.Clamp(deltaTimeSec, 0.01, 0.2);
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(durationSec, dt) / dt));

        var report = new SimulationRunReport
        {
            DurationSec = steps * dt,
            DeltaTimeSec = dt,
        };

        long respawnTicks = 0;
        long interactionTicks = 0;
        long combatTicks = 0;
        long totalStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int step = 0; step < steps; step++)
        {
            world.GameTimeSec += dt;

            long segmentStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            TickCombat(world, dt, report);
            combatTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segmentStartTicks;
            segmentStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            TickRespawnAndRecovery(world, facilities, dt, report);
            respawnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segmentStartTicks;
            if (_enableAutoMovement)
            {
                TickAutoMovement(world, dt);
            }

            segmentStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            IReadOnlyList<FacilityInteractionEvent> interactionEvents =
                _interactionService.UpdateWorld(world, facilities, dt);
            interactionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segmentStartTicks;
            foreach (FacilityInteractionEvent interactionEvent in interactionEvents)
            {
                report.InteractionEvents.Add(interactionEvent);
            }

            report.InteractionEventCount += interactionEvents.Count;
        }

        LogRulePerfIfDue(
            System.Diagnostics.Stopwatch.GetTimestamp() - totalStartTicks,
            respawnTicks,
            interactionTicks,
            combatTicks,
            world.Entities.Count,
            world.Projectiles.Count);

        if (!captureFinalEntities)
        {
            return report;
        }

        foreach (SimulationEntity entity in world.Entities)
        {
            report.FinalEntities.Add(CloneEntity(entity));
        }

        return report;
    }

    private void LogRulePerfIfDue(
        long totalTicks,
        long respawnTicks,
        long interactionTicks,
        long combatTicks,
        int entityCount,
        int projectileCount)
    {
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastRulePerfLogTicks > 0
            && (nowTicks - _lastRulePerfLogTicks) / (double)System.Diagnostics.Stopwatch.Frequency < 2.0)
        {
            return;
        }

        _lastRulePerfLogTicks = nowTicks;
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} "
            + $"total={TicksToMs(totalTicks):0.00}ms "
            + $"respawn={TicksToMs(respawnTicks):0.00}ms "
            + $"interaction={TicksToMs(interactionTicks):0.00}ms "
            + $"combat={TicksToMs(combatTicks):0.00}ms "
            + $"entities={entityCount} projectiles={projectileCount}";
        try
        {
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "rule_perf.log"), line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static double TicksToMs(long ticks)
        => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

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
                entity.AutoAimTargetKind = "armor";
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
                    entity.RespawnAmmoLockTimerSec = Math.Max(0.0, entity.RespawnAmmoLockTimerSec - dt);
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

            if (WouldNextShotExceedHeatLimit(shooter) || !HasAmmoForShot(shooter))
            {
                continue;
            }

            SimulationEntity? preferredTarget = null;
            ArmorPlateTarget preferredPlate = default;
            AutoAimSolution preferredSolution = default;
            bool hasPreferredSolution = false;
            bool heroDeploymentAutoFire = shooter.HeroDeploymentActive
                && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase);
            bool shouldAutoAim = heroDeploymentAutoFire || !shooter.IsPlayerControlled || shooter.AutoAimRequested;
            double autoAimMaxDistanceM = heroDeploymentAutoFire
                ? 1000.0
                : _rules.Combat.AutoAimMaxDistanceM;
            bool hasPreferredTarget = false;
            if (shouldAutoAim
                && TryResolveLockedArmorTarget(world, shooter, autoAimMaxDistanceM, out preferredTarget, out preferredPlate))
            {
                hasPreferredTarget = true;
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

            if (!shooter.ConsumeAmmoForShot())
            {
                continue;
            }

            shooter.FireCooldownSec = 1.0 / Math.Max(ResolveFireRateHz(shooter), 0.5);
            report.TotalShots++;
            ApplyShotHeat(shooter);
            if (hasPreferredTarget && preferredTarget is not null)
            {
                preferredSolution = SimulationCombatMath.ComputeAutoAimSolution(
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
        if (preferredPlate.HasValue && preferredTarget is not null)
        {
            AutoAimSolution solution = preferredSolution ?? SimulationCombatMath.ComputeAutoAimSolution(
                world,
                shooter,
                preferredTarget,
                preferredPlate.Value,
                autoAimMaxDistanceM);
            if (!shooter.AutoAimGuidanceOnly)
            {
                yawDeg = solution.YawDeg;
                pitchDeg = solution.PitchDeg;
                shooter.TurretYawDeg = yawDeg;
                shooter.GimbalPitchDeg = pitchDeg;
            }

            aimHitProbability = ResolveHeroDeploymentHitProbability(shooter, preferredTarget, preferredPlate.Value, solution.Accuracy);
            StoreAutoAimDiagnostics(shooter, preferredTarget, preferredPlate.Value, solution);
        }

        (double muzzleX, double muzzleY, double muzzleHeightM) = SimulationCombatMath.ComputeMuzzlePoint(world, shooter, pitchDeg);
        double speedMps = SimulationCombatMath.ProjectileSpeedMps(shooter);
        double yawRad = yawDeg * Math.PI / 180.0;
        double pitchRad = pitchDeg * Math.PI / 180.0;
        ApplySmallProjectileDispersion(world, shooter, ref yawRad, ref pitchRad);
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        bool staticAutoAimCenterLock = preferredTarget is not null
            && preferredPlate.HasValue
            && SimulationCombatMath.IsAutoAimTargetEffectivelyStatic(world, shooter, preferredTarget, preferredPlate.Value);
        double correctedYawRad = ApplyHeroDeploymentCorrectionYaw(shooter, preferredPlate, yawRad);
        double correctedPitchRad = ApplyHeroDeploymentCorrectionPitch(shooter, preferredPlate, pitchRad);
        if (!staticAutoAimCenterLock)
        {
            correctedYawRad = ApplyAutoAimCorrectionYaw(shooter, preferredTarget, preferredPlate, correctedYawRad);
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
            hash = hash * 31 + shooter.Ammo17Mm;
            hash = hash * 31 + shooter.Ammo42Mm;
            return hash;
        }
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

        if (!string.Equals(shooter.HeroDeploymentCorrectionPlateId, preferredPlate.Value.Id, StringComparison.OrdinalIgnoreCase))
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

        if (!string.Equals(shooter.HeroDeploymentCorrectionPlateId, preferredPlate.Value.Id, StringComparison.OrdinalIgnoreCase))
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

        if (plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase))
        {
            return 0.80;
        }

        if (plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase))
        {
            return 0.50;
        }

        return fallbackAccuracy;
    }

    private static void UpdateHeroDeploymentCorrection(
        SimulationWorldState world,
        SimulationEntity shooter,
        ArmorPlateTarget plate,
        Vector3 hitPointM)
    {
        if (!shooter.HeroDeploymentActive
            || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || (!plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase)
                && !plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase)))
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
            preferHighArc: true);
        (double actualYawDeg, double actualPitchDeg) = SimulationCombatMath.ComputeAimAnglesToPoint(
            world,
            shooter,
            hitPointM.X / metersPerWorldUnit,
            hitPointM.Z / metersPerWorldUnit,
            hitPointM.Y,
            preferHighArc: true);
        double yawErrorDeg = SimulationCombatMath.NormalizeSignedDeg(centerYawDeg - actualYawDeg);
        double pitchErrorDeg = centerPitchDeg - actualPitchDeg;
        shooter.HeroDeploymentCorrectionPlateId = plate.Id;
        shooter.HeroDeploymentYawCorrectionDeg = Math.Clamp(
            shooter.HeroDeploymentYawCorrectionDeg * 0.72 + yawErrorDeg * 0.18,
            -1.2,
            1.2);
        shooter.HeroDeploymentLastPitchErrorDeg = pitchErrorDeg;
        shooter.HeroDeploymentPitchCorrectionDeg = Math.Clamp(
            shooter.HeroDeploymentPitchCorrectionDeg * 0.48 + pitchErrorDeg * 0.82,
            -10.0,
            10.0);
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
        if (string.IsNullOrWhiteSpace(plate.Id)
            || (!plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase)
                && !plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        (double centerYawDeg, double centerPitchDeg) = SimulationCombatMath.ComputeAimAnglesToPoint(
            world,
            shooter,
            plate.X,
            plate.Y,
            plate.HeightM,
            preferHighArc: true);
        (double actualYawDeg, double actualPitchDeg) = SimulationCombatMath.ComputeAimAnglesToPoint(
            world,
            shooter,
            impactPointM.X / metersPerWorldUnit,
            impactPointM.Z / metersPerWorldUnit,
            impactPointM.Y,
            preferHighArc: true);
        double yawErrorDeg = SimulationCombatMath.NormalizeSignedDeg(centerYawDeg - actualYawDeg);
        double pitchErrorDeg = centerPitchDeg - actualPitchDeg;
        shooter.HeroDeploymentCorrectionPlateId = plate.Id;
        shooter.HeroDeploymentYawCorrectionDeg = Math.Clamp(
            shooter.HeroDeploymentYawCorrectionDeg * 0.82 + yawErrorDeg * 0.12,
            -1.4,
            1.4);
        shooter.HeroDeploymentLastPitchErrorDeg = pitchErrorDeg;
        shooter.HeroDeploymentPitchCorrectionDeg = Math.Clamp(
            shooter.HeroDeploymentPitchCorrectionDeg * 0.74 + pitchErrorDeg * 0.42,
            -11.0,
            11.0);
    }

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

            if (SimulationCombatMath.TryFindProjectileHit(
                world,
                shooter,
                startX,
                startY,
                startHeightM,
                projectile.X,
                projectile.Y,
                projectile.HeightM,
                frameCache.DamageTargets,
                frameCache.ArmorTargetsByEntityId,
                out SimulationEntity? hitTarget,
                out ArmorPlateTarget hitPlate,
                out double hitX,
                out double hitY,
                out double hitHeightM,
                out double hitSegmentT))
            {
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
                    frameCache.ObstacleCandidates);
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

                UpdateHeroDeploymentCorrection(world, shooter, hitPlate, hitPointM);
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
                    damageResult));
                if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, world.MetersPerWorldUnit))
                {
                    continue;
                }

                world.Projectiles.RemoveAt(index);
                continue;
            }

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
                frameCache.ObstacleCandidates);
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

                if (SimulationCombatMath.TryFindProjectileHit(
                    world,
                    shooter,
                    startX,
                    startY,
                    startHeightM,
                    projectile.X,
                    projectile.Y,
                    projectile.HeightM,
                    frameCache.DamageTargets,
                    frameCache.ArmorTargetsByEntityId,
                    out SimulationEntity? hitTarget,
                    out ArmorPlateTarget hitPlate,
                    out double hitX,
                    out double hitY,
                    out double hitHeightM,
                    out double hitSegmentT))
                {
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
                        frameCache.ObstacleCandidates);
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

                    UpdateHeroDeploymentCorrection(world, shooter, hitPlate, hitPointM);
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
                    damageResult));
                    if (TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit))
                    {
                        break;
                    }

                    removeProjectile = true;
                    break;
                }

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
                    frameCache.ObstacleCandidates);
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
            projectileAlive = TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit);
            return true;
        }

        if (!world.Teams.TryGetValue(shooter.Team, out SimulationTeamState? shooterTeamState)
            || !string.Equals(shooterTeamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            || shooterTeamState.EnergyNextModuleDelaySec > 1e-6
            || shooterTeamState.EnergyCurrentLitMask == 0)
        {
            projectileAlive = TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit);
            return true;
        }

        if (!TryRegisterSurfaceAcceptedHit(world, projectile, hitTarget, hitPlate))
        {
            projectileAlive = TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit);
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
            $"Energy mechanism ring {ringScore} hit {hitPlate.Id}",
            false));

        projectileAlive = TryApplyArmorRicochet(projectile, hitPlate, hitX, hitY, hitHeightM, hitSegmentT, metersPerWorldUnit);
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
        double minIntervalSec = ResolveSurfaceAcceptedHitIntervalSec(projectile.AmmoType);
        string key = $"{target.Id}:{plate.Id}:{NormalizeAmmoHitGroup(projectile.AmmoType)}";
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

    private static double ResolveSurfaceAcceptedHitIntervalSec(string ammoType)
        => string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? SurfaceAcceptedHitInterval42Sec
            : SurfaceAcceptedHitInterval17Sec;

    private static string NormalizeAmmoHitGroup(string ammoType)
        => string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? "42mm"
            : "17mm";

    private static void PruneSurfaceHitTimes(SimulationWorldState world)
    {
        double cutoff = world.GameTimeSec - 1.0;
        foreach (string key in world.SurfaceLastAcceptedHitTimes
                     .Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            world.SurfaceLastAcceptedHitTimes.Remove(key);
        }
    }

    private static int ResolveEnergyRingScore(ArmorPlateTarget plate, Vector3 hitPointM, double metersPerWorldUnit)
    {
        _ = metersPerWorldUnit;
        Vector3 center = new((float)(plate.X * metersPerWorldUnit), (float)plate.HeightM, (float)(plate.Y * metersPerWorldUnit));
        double yawRad = plate.YawDeg * Math.PI / 180.0;
        Vector3 side = new(-(float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
        Vector3 up = Vector3.UnitY;
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
            reflected += normal * MathF.Max(0.8f, reflectedSpeed * 0.18f);
            if (reflected.LengthSquared() > 1e-6f)
            {
                reflected = Vector3.Normalize(reflected) * reflectedSpeed;
            }
        }

        float preRicochetSpeed = reflected.Length();
        reflected *= 0.70710677f;
        if (reflected.Length() < ProjectileDeleteSpeedMps
            && preRicochetSpeed >= ProjectileDeleteSpeedMps)
        {
            reflected = Vector3.Normalize(reflected) * (float)(ProjectileDeleteSpeedMps + 0.05);
        }

        if (reflected.Length() < ProjectileDeleteSpeedMps)
        {
            return false;
        }

        float offsetDistance = string.Equals(obstacleHit.Kind, "armor_plate", StringComparison.OrdinalIgnoreCase) ? 0.020f : 0.012f;
        Vector3 offset = Vector3.Normalize(reflected) * offsetDistance;
        projectile.X = obstacleHit.X + offset.X / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.Y = obstacleHit.Y + offset.Z / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.HeightM = obstacleHit.HeightM + offset.Y;
        projectile.VelocityXWorldPerSec = reflected.X / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.VelocityYWorldPerSec = reflected.Z / Math.Max(metersPerWorldUnit, 1e-6);
        projectile.VelocityZMps = reflected.Y;
        projectile.DamageScale *= 0.5;
        projectile.RicochetCount++;
        projectile.RemainingLifeSec = Math.Max(0.08, projectile.RemainingLifeSec - 0.01);
        return true;
    }

    private static Dictionary<string, SimulationEntity> BuildEntityLookup(SimulationWorldState world)
    {
        var lookup = new Dictionary<string, SimulationEntity>(world.Entities.Count, StringComparer.OrdinalIgnoreCase);
        foreach (SimulationEntity entity in world.Entities)
        {
            lookup[entity.Id] = entity;
        }

        return lookup;
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
            if (!entity.IsSimulationSuppressed)
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
                : SimulationCombatMath.GetArmorPlateTargets(entity, metersPerWorldUnit, world.GameTimeSec);
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
        shooter.AutoAimTargetKind = plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase)
            ? "energy_disk"
            : "armor";
        shooter.AutoAimPlateDirection = solution.PlateDirection;
        shooter.AutoAimAccuracy = solution.Accuracy;
        shooter.AutoAimDistanceCoefficient = solution.DistanceCoefficient;
        shooter.AutoAimMotionCoefficient = solution.MotionCoefficient;
        shooter.AutoAimLeadTimeSec = solution.LeadTimeSec;
        shooter.AutoAimLeadDistanceM = solution.LeadDistanceM;
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
            : "armor";
        entity.AutoAimPlateDirection = string.Empty;
        entity.AutoAimAccuracy = 0.0;
        entity.AutoAimDistanceCoefficient = 0.0;
        entity.AutoAimMotionCoefficient = 0.0;
        entity.AutoAimLeadTimeSec = 0.0;
        entity.AutoAimLeadDistanceM = 0.0;
        entity.AutoAimHasSmoothedAim = false;
        entity.AutoAimLockKey = null;
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
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double maxDistanceWorld = Math.Max(0.0, maxDistanceM) / metersPerWorldUnit;
        double maxDistanceSquared = (maxDistanceWorld + 1.5) * (maxDistanceWorld + 1.5);
        bool energyMode = string.Equals(shooter.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase);
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

    private static bool TryResolveLockedArmorTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate)
    {
        target = null;
        plate = default;
        if (!shooter.AutoAimLocked
            || !string.Equals(shooter.AutoAimTargetKind, "armor", StringComparison.OrdinalIgnoreCase)
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
        plate = SimulationCombatMath.GetArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec)
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
        if (distanceM > maxDistanceM + 0.4 || !IsArmorPlateFacingShooter(shooter, plate))
        {
            target = null;
            plate = default;
            return false;
        }

        return true;
    }

    private static bool IsArmorPlateFacingShooter(SimulationEntity shooter, ArmorPlateTarget plate)
    {
        if (plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        double toShooterYawDeg = SimulationCombatMath.NormalizeDeg(Math.Atan2(shooter.Y - plate.Y, shooter.X - plate.X) * 180.0 / Math.PI);
        double facingError = Math.Abs(SimulationCombatMath.NormalizeSignedDeg(toShooterYawDeg - plate.YawDeg));
        return facingError <= 86.0;
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
        if (target.Health > 0)
        {
            damageResult = "applied";
            return damage;
        }

        target.Health = 0;
        target.IsAlive = false;
        target.TraversalActive = false;
        ClearAutoAimState(target);
        target.Heat = 0.0;
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
        entity.State = "weak";
        ResolvedRoleProfile profile = _rules.ResolveRuntimeProfile(entity);
        ApplyResolvedRoleProfile(entity, profile, clampHealthToCurrent: true);
        entity.Health = Math.Max(1.0, entity.MaxHealth * 0.10);
        double recoveryLockSec = Math.Max(RespawnRecoveryZoneLockSec, _rules.Respawn.InvalidDurationSec);
        entity.WeakTimerSec = recoveryLockSec;
        entity.RespawnAmmoLockTimerSec = recoveryLockSec;
        entity.RespawnInvincibleTimerSec = Math.Max(30.0, _rules.Respawn.InvincibleDurationSec);
        entity.Heat = 0;
        entity.HeatLockTimerSec = 0;
        entity.FireCooldownSec = 0;
        entity.TraversalActive = false;
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
            entity.Health = Math.Min(entity.Health, entity.MaxHealth);
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
            LedgeLaunchTimerSec = source.LedgeLaunchTimerSec,
            AngleDeg = source.AngleDeg,
            ChassisPitchDeg = source.ChassisPitchDeg,
            ChassisRollDeg = source.ChassisRollDeg,
            TurretYawDeg = source.TurretYawDeg,
            GimbalPitchDeg = source.GimbalPitchDeg,
            AutoAimRequested = source.AutoAimRequested,
            AutoAimGuidanceOnly = source.AutoAimGuidanceOnly,
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
            AutoAimHasSmoothedAim = source.AutoAimHasSmoothedAim,
            AutoAimLockKey = source.AutoAimLockKey,
            AutoAimSmoothedYawDeg = source.AutoAimSmoothedYawDeg,
            AutoAimSmoothedPitchDeg = source.AutoAimSmoothedPitchDeg,
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
            MaxPower = source.MaxPower,
            Power = source.Power,
            MaxHeat = source.MaxHeat,
            Heat = source.Heat,
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
