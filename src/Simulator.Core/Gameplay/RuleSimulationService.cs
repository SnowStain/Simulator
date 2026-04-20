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
            entity.HeatLockTimerSec = Math.Max(0.0, entity.HeatLockTimerSec - dt);

            entity.Power = Math.Min(
                entity.MaxPower,
                entity.Power + profile.PowerRecoveryRate * entity.DynamicPowerRecoveryMult * dt);
            entity.Heat = Math.Max(0.0, entity.Heat - profile.HeatDissipationRate * entity.DynamicCoolingMult * dt);

            if (entity.HeatLockTimerSec <= 0.0
                && entity.Heat <= 0.01
                && string.Equals(entity.State, "heat_locked", StringComparison.OrdinalIgnoreCase))
            {
                entity.State = entity.WeakTimerSec > 0 ? "weak" : "idle";
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
                shooter.AutoAimLocked = false;
                shooter.AutoAimTargetId = null;
                shooter.AutoAimPlateId = null;
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
            (yawDeg, pitchDeg, _) = SimulationCombatMath.ComputeAutoAimAnglesWithError(
                world,
                shooter,
                preferredTarget,
                preferredPlate.Value,
                _rules.Combat.AutoAimMaxDistanceM);
            shooter.TurretYawDeg = yawDeg;
            shooter.GimbalPitchDeg = pitchDeg;
            shooter.AutoAimLocked = true;
            shooter.AutoAimTargetId = preferredTarget?.Id;
            shooter.AutoAimPlateId = preferredPlate.Value.Id;
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

            double drag = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 0.22 : 0.14;
            projectile.VelocityXWorldPerSec *= Math.Exp(-drag * dt);
            projectile.VelocityYWorldPerSec *= Math.Exp(-drag * dt);
            projectile.VelocityZMps = projectile.VelocityZMps * Math.Exp(-drag * dt) - 9.81 * dt;

            projectile.X += projectile.VelocityXWorldPerSec * dt;
            projectile.Y += projectile.VelocityYWorldPerSec * dt;
            projectile.HeightM += projectile.VelocityZMps * dt;
            projectile.RemainingLifeSec -= dt;

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
                double speedScale = Math.Clamp(speedMps / 28.0, 0.0, 1.25);
                double baseDrag = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 0.22 : 0.14;
                double dragCoeff = baseDrag * (0.88 + speedScale * 0.26);
                projectile.VelocityXWorldPerSec *= Math.Exp(-dragCoeff * substepDt);
                projectile.VelocityYWorldPerSec *= Math.Exp(-dragCoeff * substepDt);
                projectile.VelocityZMps = projectile.VelocityZMps * Math.Exp(-dragCoeff * substepDt) - 9.81 * substepDt;

                projectile.X += projectile.VelocityXWorldPerSec * substepDt;
                projectile.Y += projectile.VelocityYWorldPerSec * substepDt;
                projectile.HeightM += projectile.VelocityZMps * substepDt;
                projectile.RemainingLifeSec -= substepDt;

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

    private static Dictionary<string, SimulationEntity> BuildEntityLookup(SimulationWorldState world)
    {
        var lookup = new Dictionary<string, SimulationEntity>(world.Entities.Count, StringComparer.OrdinalIgnoreCase);
        foreach (SimulationEntity entity in world.Entities)
        {
            lookup[entity.Id] = entity;
        }

        return lookup;
    }

    private static bool CanShoot(SimulationEntity entity)
    {
        if (entity.IsSimulationSuppressed
            || !entity.IsAlive
            || entity.HeatLockTimerSec > 0
            || string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase))
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
        shooter.Heat += string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? _rules.Heat.HeatGain42
            : _rules.Heat.HeatGain17;

        if (shooter.Heat > shooter.MaxHeat && shooter.MaxHeat > 0)
        {
            shooter.HeatLockTimerSec = Math.Max(shooter.HeatLockTimerSec, _rules.Heat.OverheatLockDurationSec);
            shooter.State = "heat_locked";
        }
    }

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
        target.Health -= damage;
        if (target.Health > 0)
        {
            return;
        }

        target.Health = 0;
        target.IsAlive = false;
        target.TraversalActive = false;
        target.AutoAimLocked = false;

        if (string.Equals(target.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            target.State = "respawning";
            target.RespawnTimerSec = _rules.Respawn.RobotDelaySec;
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
        entity.Health = Math.Max(1.0, entity.MaxHealth * 0.10);
        entity.WeakTimerSec = _rules.Respawn.InvalidDurationSec;
        entity.Heat = 0;
        entity.HeatLockTimerSec = 0;
        entity.TraversalActive = false;
        entity.AutoAimLocked = false;

        ResolvedRoleProfile profile = _rules.ResolveRuntimeProfile(entity);
        ApplyResolvedRoleProfile(entity, profile, clampHealthToCurrent: false);
        entity.Power = profile.MaxPower;
        entity.AmmoType = profile.AmmoType;

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
            DynamicDamageTakenMult = source.DynamicDamageTakenMult,
            DynamicDamageDealtMult = source.DynamicDamageDealtMult,
            DynamicCoolingMult = source.DynamicCoolingMult,
            DynamicPowerRecoveryMult = source.DynamicPowerRecoveryMult,
        };
    }
}
