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
    string Message);

public sealed class SimulationRunReport
{
    public double DurationSec { get; init; }

    public double DeltaTimeSec { get; init; }

    public int TotalShots { get; set; }

    public int HitShots { get; set; }

    public int InteractionEventCount { get; set; }

    public IList<SimulationCombatEvent> CombatEvents { get; } = new List<SimulationCombatEvent>();

    public IList<FacilityInteractionEvent> InteractionEvents { get; } = new List<FacilityInteractionEvent>();

    public IList<SimulationEntity> FinalEntities { get; } = new List<SimulationEntity>();
}

public sealed class RuleSimulationService
{
    private readonly RuleSet _rules;
    private readonly ArenaInteractionService _interactionService;
    private readonly bool _enableAutoMovement;
    private readonly Func<SimulationWorldState, SimulationEntity, SimulationEntity, ArmorPlateTarget, bool>? _canSeeAutoAimPlate;

    public RuleSimulationService(
        RuleSet rules,
        ArenaInteractionService interactionService,
        int? seed = null,
        bool enableAutoMovement = true,
        Func<SimulationWorldState, SimulationEntity, SimulationEntity, ArmorPlateTarget, bool>? canSeeAutoAimPlate = null)
    {
        _rules = rules;
        _interactionService = interactionService;
        _enableAutoMovement = enableAutoMovement;
        _canSeeAutoAimPlate = canSeeAutoAimPlate;
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

        for (int step = 0; step < steps; step++)
        {
            world.GameTimeSec += dt;

            TickRespawnAndRecovery(world, dt);
            if (_enableAutoMovement)
            {
                TickAutoMovement(world, dt);
            }

            IReadOnlyList<FacilityInteractionEvent> interactionEvents =
                _interactionService.UpdateWorld(world, facilities, dt);
            foreach (FacilityInteractionEvent interactionEvent in interactionEvents)
            {
                report.InteractionEvents.Add(interactionEvent);
            }

            report.InteractionEventCount += interactionEvents.Count;
            TickCombat(world, dt, report);
        }

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

    private void TickRespawnAndRecovery(SimulationWorldState world, double dt)
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
                    entity.RespawnTimerSec = Math.Max(0.0, entity.RespawnTimerSec - dt);
                    if (entity.RespawnTimerSec <= 0)
                    {
                        RespawnEntity(entity);
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
            entity.FireCooldownSec = Math.Max(0.0, entity.FireCooldownSec - dt);
            entity.WeakTimerSec = Math.Max(0.0, entity.WeakTimerSec - dt);
            entity.RespawnAmmoLockTimerSec = Math.Max(0.0, entity.RespawnAmmoLockTimerSec - dt);
            entity.RespawnInvincibleTimerSec = Math.Max(0.0, entity.RespawnInvincibleTimerSec - dt);
            entity.HeatLockTimerSec = Math.Max(0.0, entity.HeatLockTimerSec - dt);
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

            SimulationEntity? preferredTarget = null;
            ArmorPlateTarget preferredPlate = default;
            bool shouldAutoAim = !shooter.IsPlayerControlled || shooter.AutoAimRequested;
            bool hasPreferredTarget = shouldAutoAim
                && SimulationCombatMath.TryAcquireAutoAimTarget(
                    world,
                    shooter,
                    _rules.Combat.AutoAimMaxDistanceM,
                    out preferredTarget,
                    out preferredPlate,
                    (candidate, candidatePlate) => _canSeeAutoAimPlate?.Invoke(world, shooter, candidate, candidatePlate) ?? true);

            if (!shooter.IsPlayerControlled && !hasPreferredTarget)
            {
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

            if (WouldNextShotExceedHeatLimit(shooter))
            {
                continue;
            }

            if (!HasAmmoForShot(shooter) || !shooter.ConsumeAmmoForShot())
            {
                continue;
            }

            shooter.FireCooldownSec = 1.0 / Math.Max(ResolveFireRateHz(shooter), 0.5);
            report.TotalShots++;
            ApplyShotHeat(shooter);
            world.Projectiles.Add(BuildProjectile(world, shooter, preferredTarget, hasPreferredTarget ? preferredPlate : null));
        }

        TickProjectiles(world, dt, report);
    }

    private SimulationProjectile BuildProjectile(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity? preferredTarget,
        ArmorPlateTarget? preferredPlate)
    {
        double pitchDeg = shooter.GimbalPitchDeg;
        double yawDeg = shooter.TurretYawDeg;
        if (preferredPlate.HasValue && preferredTarget is not null)
        {
            AutoAimSolution solution = SimulationCombatMath.ComputeAutoAimSolution(
                world,
                shooter,
                preferredTarget,
                preferredPlate.Value,
                _rules.Combat.AutoAimMaxDistanceM);
            yawDeg = solution.YawDeg;
            pitchDeg = solution.PitchDeg;
            shooter.TurretYawDeg = yawDeg;
            shooter.GimbalPitchDeg = pitchDeg;
            StoreAutoAimDiagnostics(shooter, preferredTarget, preferredPlate.Value, solution);
        }

        (double muzzleX, double muzzleY, double muzzleHeightM) = SimulationCombatMath.ComputeMuzzlePoint(world, shooter, pitchDeg);
        double speedMps = SimulationCombatMath.ProjectileSpeedMps(shooter.AmmoType);
        double yawRad = yawDeg * Math.PI / 180.0;
        double pitchRad = pitchDeg * Math.PI / 180.0;
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);

        return new SimulationProjectile
        {
            ShooterId = shooter.Id,
            Team = shooter.Team,
            AmmoType = shooter.AmmoType,
            PreferredTargetId = preferredTarget?.Id,
            X = muzzleX,
            Y = muzzleY,
            HeightM = muzzleHeightM,
            VelocityXWorldPerSec = Math.Cos(pitchRad) * Math.Cos(yawRad) * speedMps / metersPerWorldUnit,
            VelocityYWorldPerSec = Math.Cos(pitchRad) * Math.Sin(yawRad) * speedMps / metersPerWorldUnit,
            VelocityZMps = Math.Sin(pitchRad) * speedMps,
            RemainingLifeSec = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 2.6 : 2.2,
        };
    }

    private void TickProjectiles(SimulationWorldState world, double dt, SimulationRunReport report)
    {
        if (_rules is not null)
        {
            TickProjectilesSubstepped(world, dt, report);
            return;
        }

        for (int index = world.Projectiles.Count - 1; index >= 0; index--)
        {
            SimulationProjectile projectile = world.Projectiles[index];
            SimulationEntity? shooter = world.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Id, projectile.ShooterId, StringComparison.OrdinalIgnoreCase));
            if (shooter is null || !shooter.IsAlive)
            {
                world.Projectiles.RemoveAt(index);
                continue;
            }

            double startX = projectile.X;
            double startY = projectile.Y;
            double startHeightM = projectile.HeightM;

            ApplyProjectilePhysicsStep(projectile, Math.Max(world.MetersPerWorldUnit, 1e-6), dt);

            if (SimulationCombatMath.TryFindProjectileHit(
                world,
                shooter,
                startX,
                startY,
                startHeightM,
                projectile.X,
                projectile.Y,
                projectile.HeightM,
                out SimulationEntity? hitTarget,
                out ArmorPlateTarget hitPlate,
                out _,
                out _,
                out _))
            {
                double dx = hitTarget!.X - shooter.X;
                double dy = hitTarget.Y - shooter.Y;
                double distanceM = Math.Sqrt(dx * dx + dy * dy) * Math.Max(world.MetersPerWorldUnit, 1e-6);
                double damage = ComputeDamage(shooter, hitTarget);
                ApplyDamage(world, hitTarget, damage);
                report.HitShots++;
                report.CombatEvents.Add(new SimulationCombatEvent(
                    world.GameTimeSec,
                    shooter.Id,
                    hitTarget.Id,
                    shooter.AmmoType,
                    distanceM,
                    1.0,
                    true,
                    damage,
                    $"命中 {hitPlate.Id} 造成 {damage:0.##}"));
                world.Projectiles.RemoveAt(index);
                continue;
            }

            if (projectile.RemainingLifeSec <= 1e-6 || projectile.HeightM < -0.10)
            {
                world.Projectiles.RemoveAt(index);
            }
        }
    }

    private void TickProjectilesSubstepped(SimulationWorldState world, double dt, SimulationRunReport report)
    {
        Dictionary<string, SimulationEntity> entityById = BuildEntityLookup(world);
        for (int index = world.Projectiles.Count - 1; index >= 0; index--)
        {
            SimulationProjectile projectile = world.Projectiles[index];
            if (!entityById.TryGetValue(projectile.ShooterId, out SimulationEntity? shooter) || !shooter.IsAlive)
            {
                world.Projectiles.RemoveAt(index);
                continue;
            }

            bool removeProjectile = false;
            double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
            double speedMps = Math.Sqrt(
                projectile.VelocityXWorldPerSec * projectile.VelocityXWorldPerSec
                + projectile.VelocityYWorldPerSec * projectile.VelocityYWorldPerSec) * metersPerWorldUnit;
            int substeps = Math.Clamp((int)Math.Ceiling(speedMps * Math.Max(dt, 0.01) / 1.1), 1, 6);
            double substepDt = dt / substeps;
            for (int substep = 0; substep < substeps; substep++)
            {
                double startX = projectile.X;
                double startY = projectile.Y;
                double startHeightM = projectile.HeightM;
                ApplyProjectilePhysicsStep(projectile, metersPerWorldUnit, substepDt);

                if (SimulationCombatMath.TryFindProjectileHit(
                    world,
                    shooter,
                    startX,
                    startY,
                    startHeightM,
                    projectile.X,
                    projectile.Y,
                    projectile.HeightM,
                    out SimulationEntity? hitTarget,
                    out ArmorPlateTarget hitPlate,
                    out _,
                    out _,
                    out _))
                {
                    double dx = hitTarget!.X - shooter.X;
                    double dy = hitTarget.Y - shooter.Y;
                    double distanceM = Math.Sqrt(dx * dx + dy * dy) * metersPerWorldUnit;
                    double damage = ComputeDamage(shooter, hitTarget);
                    ApplyDamage(world, hitTarget, damage);
                    report.HitShots++;
                    report.CombatEvents.Add(new SimulationCombatEvent(
                        world.GameTimeSec,
                        shooter.Id,
                        hitTarget.Id,
                        shooter.AmmoType,
                        distanceM,
                        1.0,
                        true,
                        damage,
                        $"鍛戒腑 {hitPlate.Id} 閫犳垚 {damage:0.##}"));
                    removeProjectile = true;
                    break;
                }

                if (projectile.RemainingLifeSec <= 1e-6 || projectile.HeightM < -0.10)
                {
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

    private static Dictionary<string, SimulationEntity> BuildEntityLookup(SimulationWorldState world)
    {
        var lookup = new Dictionary<string, SimulationEntity>(world.Entities.Count, StringComparer.OrdinalIgnoreCase);
        foreach (SimulationEntity entity in world.Entities)
        {
            lookup[entity.Id] = entity;
        }

        return lookup;
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

    private static double ResolveMoveSpeedMps(SimulationEntity entity)
    {
        double baseSpeedMps = 3.0 * Math.Sqrt(Math.Max(entity.ChassisDrivePowerLimitW, 10.0) / 50.0);
        baseSpeedMps *= Math.Max(0.2, entity.ChassisSpeedScale);

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

    private double ComputeDamage(SimulationEntity shooter, SimulationEntity target)
    {
        bool structureTarget = SimulationCombatMath.IsStructure(target);
        bool ammo42 = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        double baseDamage = ammo42
            ? (structureTarget ? _rules.Combat.Damage42ToStructure : _rules.Combat.Damage42ToRobot)
            : (structureTarget ? _rules.Combat.Damage17ToStructure : _rules.Combat.Damage17ToRobot);
        ResolvedRoleProfile shooterProfile = _rules.ResolveRuntimeProfile(shooter);
        ResolvedRoleProfile targetProfile = _rules.ResolveRuntimeProfile(target);

        double dealtMultiplier = shooter.DynamicDamageDealtMult * shooterProfile.DamageDealtMultiplier;
        if (shooter.WeakTimerSec > 0)
        {
            dealtMultiplier *= _rules.Respawn.WeakenDamageDealtMult;
        }

        double takenMultiplier = target.DynamicDamageTakenMult * targetProfile.DamageTakenMultiplier;
        if (target.WeakTimerSec > 0)
        {
            takenMultiplier *= _rules.Respawn.WeakenDamageTakenMult;
        }

        return Math.Max(0.0, Math.Round(baseDamage * dealtMultiplier * takenMultiplier, 2));
    }

    private void ApplyDamage(SimulationWorldState world, SimulationEntity target, double damage)
    {
        if (target.RespawnInvincibleTimerSec > 1e-6)
        {
            return;
        }

        target.Health -= damage;
        if (target.Health > 0)
        {
            return;
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
            return;
        }

        target.State = "destroyed";
        target.PermanentEliminated = true;

        if (string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            string winner = string.Equals(target.Team, "red", StringComparison.OrdinalIgnoreCase) ? "blue" : "red";
            world.GetOrCreateTeamState(winner).Gold += 500.0;
        }
    }

    private void RespawnEntity(SimulationEntity entity)
    {
        entity.IsAlive = true;
        entity.State = "weak";
        entity.Health = Math.Max(1.0, entity.MaxHealth);
        entity.WeakTimerSec = _rules.Respawn.InvalidDurationSec;
        entity.RespawnAmmoLockTimerSec = _rules.Respawn.InvalidDurationSec;
        entity.RespawnInvincibleTimerSec = Math.Max(0.0, _rules.Respawn.InvincibleDurationSec);
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
        ClearAutoAimState(entity);

        ResolvedRoleProfile profile = _rules.ResolveRuntimeProfile(entity);
        ApplyResolvedRoleProfile(entity, profile, clampHealthToCurrent: false);
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

    private static bool HasAmmoForShot(SimulationEntity entity)
    {
        if (string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            return entity.Ammo42Mm > 0;
        }

        return !string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase)
            && entity.Ammo17Mm > 0;
    }

    private double ResolveFireRateHz(SimulationEntity entity)
    {
        double fireRate = _rules.Combat.FireRateHz;
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

        if (clampHealthToCurrent)
        {
            entity.Health = Math.Min(entity.Health, entity.MaxHealth);
            entity.Power = Math.Min(entity.Power, entity.MaxPower);
            entity.Heat = Math.Min(entity.Heat, entity.MaxHeat);
            entity.ChassisEnergy = Math.Min(entity.ChassisEnergy, entity.MaxChassisEnergy);
            return;
        }

        entity.Health = entity.MaxHealth;
        entity.Power = entity.MaxPower;
        entity.Heat = Math.Min(entity.Heat, entity.MaxHeat);
        entity.ChassisEnergy = profile.UsesChassisEnergy ? profile.InitialChassisEnergy : 0.0;
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
            AngleDeg = source.AngleDeg,
            TurretYawDeg = source.TurretYawDeg,
            GimbalPitchDeg = source.GimbalPitchDeg,
            AutoAimRequested = source.AutoAimRequested,
            AutoAimLocked = source.AutoAimLocked,
            AutoAimTargetId = source.AutoAimTargetId,
            AutoAimPlateId = source.AutoAimPlateId,
            AutoAimPlateDirection = source.AutoAimPlateDirection,
            AutoAimAccuracy = source.AutoAimAccuracy,
            AutoAimDistanceCoefficient = source.AutoAimDistanceCoefficient,
            AutoAimMotionCoefficient = source.AutoAimMotionCoefficient,
            AutoAimLeadTimeSec = source.AutoAimLeadTimeSec,
            AutoAimLeadDistanceM = source.AutoAimLeadDistanceM,
            AutoAimHasSmoothedAim = source.AutoAimHasSmoothedAim,
            AutoAimLockKey = source.AutoAimLockKey,
            AutoAimSmoothedYawDeg = source.AutoAimSmoothedYawDeg,
            AutoAimSmoothedPitchDeg = source.AutoAimSmoothedPitchDeg,
            VelocityXWorldPerSec = source.VelocityXWorldPerSec,
            VelocityYWorldPerSec = source.VelocityYWorldPerSec,
            AngularVelocityDegPerSec = source.AngularVelocityDegPerSec,
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
            FireCooldownSec = source.FireCooldownSec,
            Ammo17Mm = source.Ammo17Mm,
            Ammo42Mm = source.Ammo42Mm,
            AmmoType = source.AmmoType,
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
            DynamicDamageTakenMult = source.DynamicDamageTakenMult,
            DynamicDamageDealtMult = source.DynamicDamageDealtMult,
            DynamicCoolingMult = source.DynamicCoolingMult,
            DynamicPowerRecoveryMult = source.DynamicPowerRecoveryMult,
        };
    }
}
