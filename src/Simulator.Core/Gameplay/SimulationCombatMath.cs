using System.Numerics;

namespace Simulator.Core.Gameplay;

public readonly record struct ArmorPlateTarget(
    string Id,
    double X,
    double Y,
    double HeightM,
    double YawDeg,
    double SideLengthM = 0.13,
    double WidthM = 0.0,
    double HeightSpanM = 0.0);

public readonly record struct AutoAimSolution(
    double YawDeg,
    double PitchDeg,
    double Accuracy,
    double DistanceCoefficient,
    double MotionCoefficient,
    double LeadTimeSec,
    double LeadDistanceM,
    string PlateDirection);

public static class SimulationCombatMath
{
    private const double GravityMps2 = 9.81;
    private const double SmallProjectileAutoAimLatencySec = 0.032;
    private const double LargeProjectileAutoAimLatencySec = 0.055;
    private const double OutpostTowerRadiusM = 0.20;
    private const double OutpostTowerHeightM = 1.578;
    private const double OutpostBaseLiftM = 0.40;
    private const double OutpostTopArmorCenterLiftM = 0.055;
    private const double OutpostRingSpeedRadPerSec = Math.PI * 0.8;
    private const double BaseArmorOpenThresholdHealth = 2000.0;
    private const double BaseTopArmorSlideAmplitudeM = 0.34;
    private const double BaseTopArmorSlideSpeedRadPerSec = Math.PI * 0.7;
    private const double BaseDiagramHeightM = 1.181;
    private const double BaseTopArmorCenterHeightM = 1.150;
    private const double BaseTopArmorTiltDeg = 27.5;
    private const double EnergyMechanismYawRad = -Math.PI * 0.25;

    public static IReadOnlyList<ArmorPlateTarget> GetArmorPlateTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec = 0.0)
    {
        if (string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            return GetOutpostArmorPlateTargets(target, metersPerWorldUnit, gameTimeSec);
        }

        if (string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            return GetBaseArmorPlateTargets(target, metersPerWorldUnit, gameTimeSec);
        }

        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return GetEnergyMechanismTargets(target, metersPerWorldUnit, gameTimeSec);
        }

        if (IsStructure(target))
        {
            return new[]
            {
                new ArmorPlateTarget(
                    "core",
                    target.X,
                    target.Y,
                    Math.Max(0.25, target.GroundHeightM + target.BodyClearanceM + target.BodyHeightM * 0.5),
                    target.AngleDeg),
            };
        }

        double bodyHalfLengthM = Math.Max(0.01, target.BodyLengthM * 0.5);
        double bodyHalfWidthM = Math.Max(0.01, target.BodyWidthM * target.BodyRenderWidthScale * 0.5);
        double gapM = Math.Max(0.005, target.ArmorPlateGapM);
        double plateThicknessM = ResolveArmorPlateThicknessM(target);
        double localCenterHeightM = Math.Max(0.08, target.BodyClearanceM + target.BodyHeightM * 0.5)
            + ResolveVisualBodyLiftM(target);

        IReadOnlyList<double> orbitYaws = target.ArmorOrbitYawsDeg.Count > 0
            ? target.ArmorOrbitYawsDeg
            : new[] { 0d, 180d, 90d, 270d };
        IReadOnlyList<double> selfYaws = target.ArmorSelfYawsDeg.Count > 0
            ? target.ArmorSelfYawsDeg
            : orbitYaws;

        var plates = new List<ArmorPlateTarget>(orbitYaws.Count);
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateLengthM, target.ArmorPlateHeightM), 0.04, 0.60);
        double bodyYawRad = DegreesToRadians(target.AngleDeg);
        for (int index = 0; index < orbitYaws.Count; index++)
        {
            double orbitYawDeg = orbitYaws[index];
            double orbitYawRad = DegreesToRadians(orbitYawDeg);
            double outwardX = Math.Cos(orbitYawRad);
            double outwardY = Math.Sin(orbitYawRad);
            bool frontBackFace = Math.Abs(outwardX) >= Math.Abs(outwardY);
            double localForwardM;
            double localLateralM;
            double faceYawDeg;
            if (frontBackFace)
            {
                double sign = outwardX >= 0.0 ? 1.0 : -1.0;
                localForwardM = sign * (bodyHalfLengthM + gapM + plateThicknessM * 0.5);
                localLateralM = 0.0;
                faceYawDeg = sign > 0.0 ? 0.0 : 180.0;
            }
            else
            {
                double sign = outwardY >= 0.0 ? 1.0 : -1.0;
                localForwardM = 0.0;
                localLateralM = sign * (bodyHalfWidthM + gapM + plateThicknessM * 0.5);
                faceYawDeg = sign > 0.0 ? 90.0 : -90.0;
            }

            ResolveChassisAxes(
                target.AngleDeg,
                target.ChassisPitchDeg,
                target.ChassisRollDeg,
                out Vector3 chassisForward,
                out Vector3 chassisRight,
                out Vector3 chassisUp);
            Vector3 visualOffset =
                chassisForward * (float)localForwardM
                + chassisRight * (float)localLateralM
                + chassisUp * (float)localCenterHeightM;
            double selfYawDeg = target.ArmorSelfYawsDeg.Count > 0
                ? (index < selfYaws.Count ? selfYaws[index] : orbitYawDeg)
                : faceYawDeg;
            plates.Add(new ArmorPlateTarget(
                $"armor_{index + 1}",
                target.X + visualOffset.X / Math.Max(metersPerWorldUnit, 1e-6),
                target.Y + visualOffset.Z / Math.Max(metersPerWorldUnit, 1e-6),
                target.GroundHeightM + target.AirborneHeightM + visualOffset.Y,
                NormalizeDeg(target.AngleDeg + selfYawDeg),
                armorSideLengthM,
                Math.Clamp(target.ArmorPlateLengthM, 0.04, 0.60),
                Math.Clamp(target.ArmorPlateHeightM, 0.04, 0.60)));
        }

        return plates;
    }

    public static IReadOnlyList<ArmorPlateTarget> GetEnergyMechanismTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec,
        string? targetTeam = null,
        SimulationTeamState? teamState = null)
    {
        double safeMeters = Math.Max(metersPerWorldUnit, 1e-6);
        double anchorXM = target.X * safeMeters;
        double anchorZM = target.Y * safeMeters;
        double forwardX = Math.Cos(EnergyMechanismYawRad);
        double forwardZ = Math.Sin(EnergyMechanismYawRad);
        double rightX = -forwardZ;
        double rightZ = forwardX;
        double normalYawDeg = NormalizeDeg(RadiansToDegrees(Math.Atan2(rightZ, rightX)));
        EnergyTargetLayout layout = ResolveEnergyTargetLayout(target);
        double rotorYaw = ResolveEnergyRotorYawRad(gameTimeSec, teamState);
        string[] sides = string.IsNullOrWhiteSpace(targetTeam)
            ? new[] { "red", "blue" }
            : new[] { targetTeam };
        var plates = new List<ArmorPlateTarget>(sides.Length * 5);
        foreach (string side in sides)
        {
            bool redSide = string.Equals(side, "red", StringComparison.OrdinalIgnoreCase);
            double localZ = redSide ? -layout.RotorAxisGapM * 0.5 : layout.RotorAxisGapM * 0.5;
            for (int index = 0; index < 5; index++)
            {
                double yaw = rotorYaw + layout.RotorPhaseRad + Math.Tau * index / 5.0;
                double localX = Math.Cos(yaw) * layout.RotorRadiusM;
                double localY = layout.RotorCenterHeightM + Math.Sin(yaw) * layout.RotorRadiusM;
                double xM = anchorXM + forwardX * localX + rightX * localZ;
                double zM = anchorZM + forwardZ * localX + rightZ * localZ;
                plates.Add(new ArmorPlateTarget(
                    $"energy_{(redSide ? "red" : "blue")}_arm_{index}",
                    xM / safeMeters,
                    zM / safeMeters,
                    target.GroundHeightM + localY,
                    normalYawDeg,
                    layout.DiskDiameterM,
                    layout.DiskDiameterM,
                    layout.DiskDiameterM));
            }
        }

        return plates;
    }

    private static EnergyTargetLayout ResolveEnergyTargetLayout(SimulationEntity target)
    {
        double baseHeight = Math.Max(0.0, target.StructureBaseHeightM);
        double groundClearance = Math.Max(0.0, target.StructureGroundClearanceM);
        double frameWidth = Math.Max(0.80, target.StructureFrameWidthM > 1e-6 ? target.StructureFrameWidthM : target.BodyLengthM);
        double frameDepth = Math.Max(0.06, target.StructureFrameDepthM > 1e-6 ? target.StructureFrameDepthM : target.BodyWidthM * 0.18);
        double rotorCenterHeight = Math.Max(baseHeight + groundClearance + 0.40, target.StructureRotorCenterHeightM);
        double rotorPhaseRad = DegreesToRadians(target.StructureRotorPhaseDeg);
        double rotorRadius = Math.Max(0.18, target.StructureRotorRadiusM);
        double hubRadius = Math.Max(0.05, target.StructureRotorHubRadiusM);
        double lampLength = Math.Max(0.06, target.StructureLampLengthM);
        double lampWidth = Math.Max(0.06, target.StructureLampWidthM);
        double cantileverLength = Math.Max(0.0, target.StructureCantileverLengthM);
        double cantileverPairGap = Math.Max(
            frameWidth + cantileverLength,
            target.StructureCantileverPairGapM > 1e-6 ? target.StructureCantileverPairGapM : frameWidth + cantileverLength);
        double rotorAxisGap = Math.Max(
            Math.Max(frameDepth * 1.8, hubRadius * 2.6),
            Math.Min(cantileverPairGap, frameWidth) * 0.42 + cantileverLength * 0.30);
        double diskDiameterM = Math.Max(0.18, Math.Max(lampLength, lampWidth));
        return new EnergyTargetLayout(
            rotorCenterHeight + target.StructureCantileverOffsetYM,
            rotorPhaseRad,
            rotorRadius,
            rotorAxisGap,
            diskDiameterM);
    }

    private static double ResolveEnergyRotorYawRad(double gameTimeSec, SimulationTeamState? teamState)
    {
        const double smallSpeedRadPerSec = Math.PI / 3.0;
        const double largeActiveA = 0.9125;
        const double largeActiveOmega = 1.942;
        const double largeActiveB = 2.090 - largeActiveA;
        if (teamState is null
            || (!string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase)))
        {
            return 0.0;
        }

        int direction = teamState.EnergyRotorDirectionSign != 0 ? teamState.EnergyRotorDirectionSign : 1;
        bool largeActive = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            && teamState.EnergyLargeMechanismActive;
        double safeTime = Math.Max(0.0, gameTimeSec - teamState.EnergyStateStartTimeSec);
        if (!largeActive)
        {
            return direction * safeTime * smallSpeedRadPerSec;
        }

        double speedIntegral = largeActiveB * safeTime
            + largeActiveA / largeActiveOmega * (1.0 - Math.Cos(largeActiveOmega * safeTime));
        return direction * speedIntegral;
    }

    public static bool TryAcquireEnergyMechanismTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        Func<SimulationEntity, ArmorPlateTarget, bool>? canSeePlate = null)
    {
        target = null;
        plate = default;
        if (!world.Teams.TryGetValue(shooter.Team, out SimulationTeamState? teamState)
            || !string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            || teamState.EnergyNextModuleDelaySec > 1e-6)
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double bestScore = double.MaxValue;
        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || !string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ArmorPlateTarget candidatePlate in GetEnergyMechanismTargets(candidate, metersPerWorldUnit, world.GameTimeSec, shooter.Team, teamState))
            {
                if (!IsEnergyPlateLitForTeam(teamState, candidatePlate.Id))
                {
                    continue;
                }

                double dxWorld = candidatePlate.X - shooter.X;
                double dyWorld = candidatePlate.Y - shooter.Y;
                double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
                if (distanceM > maxDistanceM)
                {
                    continue;
                }

                (double predictedX, double predictedY, double predictedHeightM) = PredictArmorPlatePoint(
                    world,
                    shooter,
                    candidate,
                    candidatePlate,
                    distanceM,
                    out _,
                    out _);
                (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
                    world,
                    shooter,
                    predictedX,
                    predictedY,
                    predictedHeightM,
                    preferHighArc: false);

                double yawError = Math.Abs(NormalizeSignedDeg(yawDeg - shooter.TurretYawDeg));
                double pitchError = Math.Abs(pitchDeg - shooter.GimbalPitchDeg);
                if (shooter.IsPlayerControlled && (yawError > 80.0 || pitchError > 48.0))
                {
                    continue;
                }

                if (canSeePlate is not null && !canSeePlate(candidate, candidatePlate))
                {
                    continue;
                }

                double score = yawError * yawError * 1.45 + pitchError * pitchError * 1.85 + distanceM * 0.04;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                target = candidate;
                plate = candidatePlate;
            }
        }

        return target is not null;
    }

    public static bool IsEnergyPlateLitForTeam(SimulationTeamState teamState, string plateId)
    {
        if (!TryParseEnergyArmIndex(plateId, out string team, out int armIndex)
            || !string.Equals(team, teamState.Team, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (teamState.EnergyCurrentLitMask & (1 << armIndex)) != 0;
    }

    public static bool TryParseEnergyArmIndex(string plateId, out string team, out int armIndex)
    {
        team = string.Empty;
        armIndex = -1;
        string[] parts = (plateId ?? string.Empty).Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !parts[0].Equals("energy", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("arm", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[3], out armIndex)
            || armIndex < 0
            || armIndex > 4)
        {
            return false;
        }

        team = parts[1];
        return team.Equals("red", StringComparison.OrdinalIgnoreCase)
            || team.Equals("blue", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryAcquireAutoAimTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        Func<SimulationEntity, ArmorPlateTarget, bool>? canSeePlate = null)
    {
        if (string.Equals(shooter.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            return TryAcquireEnergyMechanismTarget(world, shooter, maxDistanceM, out target, out plate, canSeePlate);
        }

        target = null;
        plate = default;

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double bestScore = double.MaxValue;
        bool heroDeployment = shooter.HeroDeploymentActive
            && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase);
        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id, shooter.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ArmorPlateTarget candidatePlate in GetArmorPlateTargets(candidate, metersPerWorldUnit, world.GameTimeSec))
            {
                if (heroDeployment && !IsHeroDeploymentTargetPlate(candidate, candidatePlate))
                {
                    continue;
                }

                (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(world, shooter, candidatePlate.X, candidatePlate.Y, candidatePlate.HeightM);
                double dxWorld = candidatePlate.X - shooter.X;
                double dyWorld = candidatePlate.Y - shooter.Y;
                double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
                if (distanceM > maxDistanceM)
                {
                    continue;
                }

                double yawError = Math.Abs(NormalizeSignedDeg(yawDeg - shooter.TurretYawDeg));
                double pitchError = Math.Abs(pitchDeg - shooter.GimbalPitchDeg);
                if (shooter.IsPlayerControlled && !heroDeployment && (yawError > 62.0 || pitchError > 38.0))
                {
                    continue;
                }

                if (!IsPlateFacingShooter(shooter, candidatePlate))
                {
                    continue;
                }

                if (canSeePlate is not null && !canSeePlate(candidate, candidatePlate))
                {
                    continue;
                }

                double score = yawError * yawError * 1.85 + pitchError * pitchError * 2.30 + distanceM * 0.08;
                if (heroDeployment)
                {
                    score -= candidatePlate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase) ? 260.0 : 220.0;
                }

                double lifetimeScore = ComputePlateLifetimeScore(world, shooter, candidate, candidatePlate, metersPerWorldUnit);
                bool sameLockedPlate = string.Equals(shooter.AutoAimTargetId, candidate.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(shooter.AutoAimPlateId, candidatePlate.Id, StringComparison.OrdinalIgnoreCase);
                if (sameLockedPlate)
                {
                    double futureMargin = Math.Max(-1.0, Math.Min(1.0, lifetimeScore / 90.0));
                    score -= futureMargin > 0.18 ? 44.0 : 18.0;
                }

                score -= lifetimeScore;
                if (candidatePlate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase))
                {
                    score += 220.0;
                }

                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                target = candidate;
                plate = candidatePlate;
            }
        }

        return target is not null;
    }

    private static bool IsHeroDeploymentTargetPlate(SimulationEntity target, ArmorPlateTarget plate)
    {
        if (!IsStructure(target))
        {
            return false;
        }

        return plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase)
            || plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase);
    }

    private static double ResolveVisualBodyLiftM(SimulationEntity entity)
    {
        double bodyLift = 0.0;
        if (entity.TraversalActive)
        {
            double progress = Math.Clamp(entity.TraversalProgress, 0.0, 1.0);
            static double Blend(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0.0, 1.0);
            bool heavyChassis =
                string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase);
            if (heavyChassis)
            {
                double stepHeight = Math.Clamp(
                    Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0.0,
                    0.45);
                if (progress < 0.20)
                {
                    bodyLift = (0.02 + stepHeight * 0.10) * (progress / 0.20);
                }
                else if (progress < 0.56)
                {
                    bodyLift = Blend(0.02 + stepHeight * 0.10, 0.09 + stepHeight * 0.18, (progress - 0.20) / 0.36);
                }
                else if (progress < 0.84)
                {
                    bodyLift = Blend(0.09 + stepHeight * 0.18, 0.05 + stepHeight * 0.08, (progress - 0.56) / 0.28);
                }
                else
                {
                    bodyLift = (0.05 + stepHeight * 0.08) * (1.0 - (progress - 0.84) / 0.16);
                }
            }
            else if (progress < 0.4)
            {
                double stepHeight = Math.Clamp(
                    Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0.0,
                    0.32);
                bodyLift = (0.06 + stepHeight * 0.10) * (progress / 0.4);
            }
            else if (progress < 0.7)
            {
                double stepHeight = Math.Clamp(
                    Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0.0,
                    0.32);
                bodyLift = 0.09 + stepHeight * 0.12;
            }
            else
            {
                double stepHeight = Math.Clamp(
                    Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0.0,
                    0.32);
                bodyLift = (0.09 + stepHeight * 0.12) * (1.0 - (progress - 0.7) / 0.3);
            }
        }

        if (entity.ChassisSupportsJump && entity.AirborneHeightM > 1e-4)
        {
            double jumpWave = Math.Clamp(entity.AirborneHeightM / 0.70, 0.0, 1.0);
            bodyLift = Math.Max(bodyLift, 0.16 * jumpWave);
        }

        return bodyLift;
    }

    private static double ComputePlateLifetimeScore(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double metersPerWorldUnit)
    {
        bool switchingSameTarget = string.Equals(shooter.AutoAimTargetId, target.Id, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(shooter.AutoAimPlateId, plate.Id, StringComparison.OrdinalIgnoreCase);
        double horizonSec = Math.Abs(target.AngularVelocityDegPerSec) > 12.0 || target.SmallGyroActive
            ? 0.36
            : 0.22;
        ArmorPlateTarget futurePlate = GetArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec + horizonSec)
            .FirstOrDefault(candidate => candidate.Id.Equals(plate.Id, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(futurePlate.Id))
        {
            return switchingSameTarget ? -16.0 : -4.0;
        }

        bool futureFacing = IsPlateFacingShooter(shooter, futurePlate);
        if (!futureFacing)
        {
            return switchingSameTarget ? -32.0 : -10.0;
        }

        double currentFace = ComputeFacingMargin(shooter, plate);
        double futureFace = ComputeFacingMargin(shooter, futurePlate);
        double enteringBonus = Math.Clamp(futureFace - currentFace, -0.20, 0.46) * 150.0;
        if (switchingSameTarget && currentFace < 0.40 && futureFace > currentFace + 0.18)
        {
            enteringBonus += 34.0;
        }

        return (switchingSameTarget ? 44.0 : 14.0) + enteringBonus;
    }

    public static (double X, double Y, double HeightM) ComputeMuzzlePoint(
        SimulationWorldState world,
        SimulationEntity shooter,
        double? pitchDegOverride = null)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double bodyYawRad = DegreesToRadians(shooter.AngleDeg);
        double turretYawRad = DegreesToRadians(shooter.TurretYawDeg);
        double pitchDeg = pitchDegOverride ?? shooter.GimbalPitchDeg;
        double pitchRad = DegreesToRadians(pitchDeg);

        double groundHeight = shooter.GroundHeightM + shooter.AirborneHeightM;
        double bodyTopM = shooter.BodyClearanceM + shooter.BodyHeightM;
        double hingeHeightM = Math.Max(
            bodyTopM + shooter.GimbalMountGapM + shooter.GimbalMountHeightM,
            shooter.GimbalHeightM > 1e-4
                ? shooter.GimbalHeightM - shooter.GimbalBodyHeightM * 0.5
                : bodyTopM + shooter.GimbalBodyHeightM * 0.5);

        Vector2 bodyForward = new((float)Math.Cos(bodyYawRad), (float)Math.Sin(bodyYawRad));
        Vector2 bodyRight = new((float)-Math.Sin(bodyYawRad), (float)Math.Cos(bodyYawRad));
        Vector2 hingeWorld = new(
            (float)shooter.X + bodyForward.X * (float)(shooter.GimbalOffsetXM / metersPerWorldUnit) + bodyRight.X * (float)(shooter.GimbalOffsetYM / metersPerWorldUnit),
            (float)shooter.Y + bodyForward.Y * (float)(shooter.GimbalOffsetXM / metersPerWorldUnit) + bodyRight.Y * (float)(shooter.GimbalOffsetYM / metersPerWorldUnit));

        Vector3 turretForward = new((float)Math.Cos(turretYawRad), 0f, (float)Math.Sin(turretYawRad));
        Vector3 turretRight = new(-turretForward.Z, 0f, turretForward.X);
        Vector3 pitchedForward = Vector3.Normalize(turretForward * (float)Math.Cos(pitchRad) + Vector3.UnitY * (float)Math.Sin(pitchRad));
        Vector3 pitchedUp = Vector3.Cross(turretRight, pitchedForward);
        if (pitchedUp.LengthSquared() <= 1e-8f)
        {
            pitchedUp = Vector3.UnitY;
        }

        pitchedUp = Vector3.Normalize(pitchedUp);
        Vector3 hingeM = new(
            (float)(hingeWorld.X * metersPerWorldUnit),
            (float)(groundHeight + hingeHeightM),
            (float)(hingeWorld.Y * metersPerWorldUnit));
        Vector3 turretCenterM = hingeM
            + pitchedUp * (float)(shooter.GimbalBodyHeightM * 0.50 + 0.006)
            + pitchedForward * (float)(shooter.GimbalLengthM * 0.04);
        Vector3 muzzleM = turretCenterM
            + pitchedForward * (float)(shooter.GimbalLengthM * 0.5 + shooter.BarrelRadiusM * 0.45 + shooter.BarrelLengthM)
            + pitchedUp * (float)(shooter.GimbalBodyHeightM * 0.12);
        return (
            muzzleM.X / metersPerWorldUnit,
            muzzleM.Z / metersPerWorldUnit,
            muzzleM.Y);
    }

    public static (double YawDeg, double PitchDeg) ComputeAimAnglesToPoint(
        SimulationWorldState world,
        SimulationEntity shooter,
        double targetX,
        double targetY,
        double targetHeightM,
        bool preferHighArc = false)
    {
        double pitch = shooter.GimbalPitchDeg;
        double yaw = shooter.TurretYawDeg;
        double speedMps = Math.Max(1.0, ProjectileSpeedMps(shooter));
        bool useDragSolver = preferHighArc;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            (double muzzleX, double muzzleY, double muzzleHeightM) = ComputeMuzzlePoint(world, shooter, pitch);
            double dxWorld = targetX - muzzleX;
            double dyWorld = targetY - muzzleY;
            double horizontalM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * Math.Max(world.MetersPerWorldUnit, 1e-6);
            yaw = NormalizeDeg(RadiansToDegrees(Math.Atan2(dyWorld, dxWorld)));

            double dzM = targetHeightM - muzzleHeightM;
            if (useDragSolver
                && TrySolveBallisticPitchDegWithDrag(horizontalM, dzM, speedMps, shooter.AmmoType, preferHighArc, out double dragPitchDeg))
            {
                pitch = Math.Clamp(dragPitchDeg, -40.0, 40.0);
                continue;
            }

            if (TrySolveVacuumBallisticPitchDeg(horizontalM, dzM, speedMps, preferHighArc, out double vacuumPitchDeg))
            {
                pitch = Math.Clamp(vacuumPitchDeg, -40.0, 40.0);
                continue;
            }

            pitch = Math.Clamp(
                RadiansToDegrees(Math.Atan2(dzM, Math.Max(horizontalM, 1e-4))),
                -40.0,
                40.0);
        }

        return (yaw, pitch);
    }

    private static bool TrySolveVacuumBallisticPitchDeg(
        double horizontalM,
        double dzM,
        double speedMps,
        bool preferHighArc,
        out double pitchDeg)
    {
        pitchDeg = 0.0;
        if (horizontalM <= 1e-5)
        {
            pitchDeg = dzM >= 0.0 ? 40.0 : -40.0;
            return true;
        }

        double speedSq = speedMps * speedMps;
        double discriminant = speedSq * speedSq - GravityMps2 * (GravityMps2 * horizontalM * horizontalM + 2.0 * dzM * speedSq);
        if (discriminant < 0.0)
        {
            return false;
        }

        double sqrt = Math.Sqrt(discriminant);
        double denom = GravityMps2 * horizontalM;
        if (Math.Abs(denom) <= 1e-6)
        {
            return false;
        }

        double lowTan = (speedSq - sqrt) / denom;
        double highTan = (speedSq + sqrt) / denom;
        pitchDeg = RadiansToDegrees(Math.Atan(preferHighArc ? highTan : lowTan));
        return !double.IsNaN(pitchDeg) && !double.IsInfinity(pitchDeg);
    }

    private static bool TrySolveBallisticPitchDegWithDrag(
        double horizontalM,
        double dzM,
        double speedMps,
        string ammoType,
        bool preferHighArc,
        out double pitchDeg)
    {
        pitchDeg = 0.0;
        if (horizontalM <= 1e-5)
        {
            pitchDeg = dzM >= 0.0 ? 40.0 : -40.0;
            return true;
        }

        const int sampleCount = 49;
        Span<double> samplePitches = stackalloc double[sampleCount];
        Span<double> sampleErrors = stackalloc double[sampleCount];
        Span<double> bracketStarts = stackalloc double[sampleCount];
        Span<double> bracketEnds = stackalloc double[sampleCount];
        int bracketCount = 0;
        double minPitch = dzM < -0.08 ? -35.0 : -12.0;
        double maxPitch = 40.0;
        double bestPitch = 0.0;
        double bestAbsError = double.MaxValue;

        for (int index = 0; index < sampleCount; index++)
        {
            double t = sampleCount <= 1 ? 0.0 : index / (double)(sampleCount - 1);
            double candidatePitch = minPitch + (maxPitch - minPitch) * t;
            double error = EvaluateBallisticHeightError(horizontalM, dzM, speedMps, ammoType, DegreesToRadians(candidatePitch));
            samplePitches[index] = candidatePitch;
            sampleErrors[index] = error;
            double absError = Math.Abs(error);
            if (absError < bestAbsError)
            {
                bestAbsError = absError;
                bestPitch = candidatePitch;
            }

            if (index == 0)
            {
                continue;
            }

            double previousError = sampleErrors[index - 1];
            if ((previousError <= 0.0 && error >= 0.0) || (previousError >= 0.0 && error <= 0.0))
            {
                bracketStarts[bracketCount] = samplePitches[index - 1];
                bracketEnds[bracketCount] = candidatePitch;
                bracketCount++;
            }
        }

        if (bracketCount > 0)
        {
            int bracketIndex = preferHighArc ? bracketCount - 1 : 0;
            double lowPitch = bracketStarts[bracketIndex];
            double highPitch = bracketEnds[bracketIndex];
            double lowError = EvaluateBallisticHeightError(horizontalM, dzM, speedMps, ammoType, DegreesToRadians(lowPitch));
            for (int iteration = 0; iteration < 16; iteration++)
            {
                double midPitch = (lowPitch + highPitch) * 0.5;
                double midError = EvaluateBallisticHeightError(horizontalM, dzM, speedMps, ammoType, DegreesToRadians(midPitch));
                if ((lowError <= 0.0 && midError >= 0.0) || (lowError >= 0.0 && midError <= 0.0))
                {
                    highPitch = midPitch;
                }
                else
                {
                    lowPitch = midPitch;
                    lowError = midError;
                }
            }

            pitchDeg = (lowPitch + highPitch) * 0.5;
            return true;
        }

        pitchDeg = bestPitch;
        return bestAbsError < Math.Max(0.30, horizontalM * 0.12);
    }

    private static double EvaluateBallisticHeightError(
        double horizontalM,
        double dzM,
        double speedMps,
        string ammoType,
        double pitchRad)
    {
        double vxMps = speedMps * Math.Cos(pitchRad);
        double vzMps = speedMps * Math.Sin(pitchRad);
        if (vxMps <= 1e-5)
        {
            return -dzM;
        }

        double xM = 0.0;
        double zM = 0.0;
        double previousXM = 0.0;
        double previousZM = 0.0;
        double diameterM = ProjectileDiameterM(ammoType);
        double areaM2 = Math.PI * diameterM * diameterM * 0.25;
        double massKg = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 0.041 : 0.0032;
        double dt = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 1.0 / 240.0 : 1.0 / 200.0;
        int maxSteps = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 840 : 520;
        for (int step = 0; step < maxSteps; step++)
        {
            previousXM = xM;
            previousZM = zM;

            double speed = Math.Sqrt(vxMps * vxMps + vzMps * vzMps);
            if (speed > 1e-6)
            {
                double dragCoefficient = 0.47;
                double airDensityKgM3 = 1.20;
                double dragAccelMps2 = 0.5 * airDensityKgM3 * dragCoefficient * areaM2 * speed * speed / Math.Max(0.001, massKg);
                dragAccelMps2 = Math.Min(dragAccelMps2, speed / Math.Max(dt, 1e-6) * 0.72);
                double dragStep = dragAccelMps2 * dt / speed;
                vxMps -= vxMps * dragStep;
                vzMps -= vzMps * dragStep;
            }

            vzMps -= GravityMps2 * dt;
            xM += vxMps * dt;
            zM += vzMps * dt;

            if (xM >= horizontalM)
            {
                double segment = Math.Max(xM - previousXM, 1e-6);
                double lerp = Math.Clamp((horizontalM - previousXM) / segment, 0.0, 1.0);
                double sampledZM = previousZM + (zM - previousZM) * lerp;
                return sampledZM - dzM;
            }

            if (vxMps <= 1e-4 || (zM < dzM - 6.0 && vzMps < 0.0))
            {
                break;
            }
        }

        return zM - dzM;
    }

    public static (double YawDeg, double PitchDeg, double Accuracy) ComputeAutoAimAnglesWithError(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double maxDistanceM)
    {
        AutoAimSolution solution = ComputeAutoAimSolution(world, shooter, target, plate, maxDistanceM);
        return (solution.YawDeg, solution.PitchDeg, solution.Accuracy);
    }

    public static AutoAimSolution ComputeAutoAimSolution(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double maxDistanceM)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double dxWorld = plate.X - shooter.X;
        double dyWorld = plate.Y - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        (double predictedX, double predictedY, double predictedHeightM) = PredictArmorPlatePoint(
            world,
            shooter,
            target,
            plate,
            distanceM,
            out double leadTimeSec,
            out double leadDistanceM);
        double distanceCoefficient = ComputeAutoAimDistanceCoefficient(distanceM, maxDistanceM);
        double motionCoefficient = ComputeAutoAimMotionCoefficient(world, target);
        if (target.AutoAimInstabilityTimerSec > 1e-6)
        {
            motionCoefficient *= 0.50;
        }

        double accuracyScale = Math.Clamp(shooter.AutoAimAccuracyScale <= 1e-6 ? 1.0 : shooter.AutoAimAccuracyScale, 0.05, 1.0);
        double accuracy = Math.Clamp(distanceCoefficient * motionCoefficient * accuracyScale, 0.05, 1.0);
        bool heroDeploymentTopTarget = shooter.HeroDeploymentActive
            && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && (plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase)
                || plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase));
        if (accuracy >= 0.999)
        {
            (double perfectYaw, double perfectPitch) = ComputeAimAnglesToPoint(world, shooter, predictedX, predictedY, predictedHeightM, heroDeploymentTopTarget);

            return new AutoAimSolution(
                perfectYaw,
                perfectPitch,
                1.0,
                distanceCoefficient,
                motionCoefficient,
                leadTimeSec,
                leadDistanceM,
                DescribeArmorPlateDirection(target, plate));
        }

        double errorRatio = 1.0 - accuracy;
        int seed = StableHash(shooter.Id, target.Id, plate.Id);
        double sideNoise = StableSignedUnit(seed);
        double heightNoise = StableSignedUnit(seed ^ 0x5A17);
        double lateralErrorM = errorRatio * Math.Clamp(distanceM * 0.075, 0.025, 0.42) * sideNoise;
        double heightErrorM = errorRatio * Math.Clamp(0.08 + distanceM * 0.04, 0.08, 0.36) * heightNoise;
        double plateYawRad = Math.Atan2(predictedY - shooter.Y, predictedX - shooter.X);
        double sideXWorld = -Math.Sin(plateYawRad) * lateralErrorM / metersPerWorldUnit;
        double sideYWorld = Math.Cos(plateYawRad) * lateralErrorM / metersPerWorldUnit;

        // The ballistic solver still handles gravity; the error is applied to a nearby virtual point,
        // so auto aim can miss high/low instead of simply pinning the crosshair to the armor center.
        (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
            world,
            shooter,
            predictedX + sideXWorld,
            predictedY + sideYWorld,
            predictedHeightM + heightErrorM,
            heroDeploymentTopTarget);

        return new AutoAimSolution(
            yawDeg,
            pitchDeg,
            accuracy,
            distanceCoefficient,
            motionCoefficient,
            leadTimeSec,
            leadDistanceM,
            DescribeArmorPlateDirection(target, plate));
    }

    public static bool TryFindProjectileHit(
        SimulationWorldState world,
        SimulationEntity shooter,
        double startX,
        double startY,
        double startHeightM,
        double endX,
        double endY,
        double endHeightM,
        IReadOnlyList<SimulationEntity>? candidateTargets,
        out SimulationEntity? hitTarget,
        out ArmorPlateTarget hitPlate,
        out double hitX,
        out double hitY,
        out double hitHeightM,
        out double hitSegmentT)
        => TryFindProjectileHit(
            world,
            shooter,
            startX,
            startY,
            startHeightM,
            endX,
            endY,
            endHeightM,
            candidateTargets,
            null,
            out hitTarget,
            out hitPlate,
            out hitX,
            out hitY,
            out hitHeightM,
            out hitSegmentT);

    public static bool TryFindProjectileHit(
        SimulationWorldState world,
        SimulationEntity shooter,
        double startX,
        double startY,
        double startHeightM,
        double endX,
        double endY,
        double endHeightM,
        IReadOnlyList<SimulationEntity>? candidateTargets,
        IReadOnlyDictionary<string, IReadOnlyList<ArmorPlateTarget>>? cachedArmorTargets,
        out SimulationEntity? hitTarget,
        out ArmorPlateTarget hitPlate,
        out double hitX,
        out double hitY,
        out double hitHeightM,
        out double hitSegmentT)
    {
        hitTarget = null;
        hitPlate = default;
        hitX = 0;
        hitY = 0;
        hitHeightM = 0;
        hitSegmentT = 1.0;

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double projectileRadiusM = ProjectileDiameterM(shooter.AmmoType) * 0.5;
        double bestSegmentT = double.MaxValue;
        Vector3 segmentStart = new((float)(startX * metersPerWorldUnit), (float)startHeightM, (float)(startY * metersPerWorldUnit));
        Vector3 segmentEnd = new((float)(endX * metersPerWorldUnit), (float)endHeightM, (float)(endY * metersPerWorldUnit));
        IReadOnlyList<SimulationEntity> candidates = candidateTargets ?? (IReadOnlyList<SimulationEntity>)world.Entities;

        foreach (SimulationEntity candidate in candidates)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || string.Equals(candidate.Id, shooter.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ProjectileCollisionBroadphase.MayIntersectTargetBounds(
                    candidate,
                    metersPerWorldUnit,
                    projectileRadiusM,
                    segmentStart,
                    segmentEnd))
            {
                continue;
            }

            IReadOnlyList<ArmorPlateTarget> armorTargets = cachedArmorTargets is not null
                && cachedArmorTargets.TryGetValue(candidate.Id, out IReadOnlyList<ArmorPlateTarget>? cachedTargets)
                    ? cachedTargets
                    : GetArmorPlateTargets(candidate, metersPerWorldUnit, world.GameTimeSec);
            bool energyMechanismTarget = string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
            world.Teams.TryGetValue(shooter.Team, out SimulationTeamState? shooterTeamState);
            bool restrictToCurrentEnergyTarget = energyMechanismTarget
                && shooterTeamState is not null
                && string.Equals(shooterTeamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
                && shooterTeamState.EnergyNextModuleDelaySec <= 1e-6
                && shooterTeamState.EnergyCurrentLitMask != 0;
            foreach (ArmorPlateTarget plate in armorTargets)
            {
                if (restrictToCurrentEnergyTarget
                    && (!TryParseEnergyArmIndex(plate.Id, out string plateTeam, out int armIndex)
                        || !string.Equals(plateTeam, shooter.Team, StringComparison.OrdinalIgnoreCase)
                        || (shooterTeamState!.EnergyCurrentLitMask & (1 << armIndex)) == 0))
                {
                    continue;
                }

                if (!TryIntersectProjectileWithArmorPlate(
                    plate,
                    metersPerWorldUnit,
                    projectileRadiusM,
                    segmentStart,
                    segmentEnd,
                    out double plateSegmentT,
                    out Vector3 hitPoint))
                {
                    continue;
                }

                if (plateSegmentT >= bestSegmentT)
                {
                    continue;
                }

                bestSegmentT = plateSegmentT;
                hitTarget = candidate;
                hitPlate = plate;
                hitX = hitPoint.X / metersPerWorldUnit;
                hitY = hitPoint.Z / metersPerWorldUnit;
                hitHeightM = hitPoint.Y;
                hitSegmentT = Math.Clamp(plateSegmentT, 0.0, 1.0);
            }
        }

        return hitTarget is not null;
    }

    public static Vector3 ResolveArmorPlateNormal(ArmorPlateTarget plate)
    {
        double yawRad = DegreesToRadians(plate.YawDeg);
        Vector3 forward = new((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));
        if (plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase))
        {
            double tiltRad = DegreesToRadians(BaseTopArmorTiltDeg);
            return Vector3.Normalize(forward * (float)Math.Sin(tiltRad) + Vector3.UnitY * (float)Math.Cos(tiltRad));
        }

        if (plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return Vector3.Normalize(forward + Vector3.UnitY);
        }

        return forward;
    }

    public static bool IsCriticalStructureArmorHit(ArmorPlateTarget plate, double metersPerWorldUnit, Vector3 hitPoint)
    {
        bool structurePlate = plate.Id.StartsWith("outpost_", StringComparison.OrdinalIgnoreCase)
            || plate.Id.StartsWith("base_", StringComparison.OrdinalIgnoreCase);
        if (!structurePlate)
        {
            return false;
        }

        Vector3 center = new(
            (float)(plate.X * metersPerWorldUnit),
            (float)plate.HeightM,
            (float)(plate.Y * metersPerWorldUnit));
        double yawRad = DegreesToRadians(plate.YawDeg);
        Vector3 normal = ResolveArmorPlateNormal(plate);
        Vector3 side = new(-(float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
        Vector3 up = plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase)
            ? Vector3.Normalize(Vector3.Cross(side, normal))
            : Vector3.UnitY;
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.UnitY;
        }

        Vector3 local = hitPoint - center;
        const float halfCriticalSideM = 0.005f;
        return MathF.Abs(Vector3.Dot(local, side)) <= halfCriticalSideM
            && MathF.Abs(Vector3.Dot(local, up)) <= halfCriticalSideM;
    }

    private static bool TryIntersectProjectileWithArmorPlate(
        ArmorPlateTarget plate,
        double metersPerWorldUnit,
        double projectileRadiusM,
        Vector3 segmentStart,
        Vector3 segmentEnd,
        out double segmentT,
        out Vector3 hitPoint)
    {
        segmentT = 1.0;
        hitPoint = default;
        Vector3 center = new(
            (float)(plate.X * metersPerWorldUnit),
            (float)plate.HeightM,
            (float)(plate.Y * metersPerWorldUnit));
        Vector3 segment = segmentEnd - segmentStart;
        float segmentLengthSq = segment.LengthSquared();
        if (segmentLengthSq <= 1e-9f)
        {
            return false;
        }

        Vector3 normal = ResolveArmorPlateNormal(plate);
        double yawRad = DegreesToRadians(plate.YawDeg);
        Vector3 side = new(-(float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
        Vector3 up = plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase)
            ? Vector3.Normalize(Vector3.Cross(side, normal))
            : Vector3.UnitY;
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.UnitY;
        }

        float denom = Vector3.Dot(segment, normal);
        if (Math.Abs(denom) > 1e-7f)
        {
            float t = Vector3.Dot(center - segmentStart, normal) / denom;
            if (t >= -0.025f && t <= 1.025f)
            {
                Vector3 candidate = segmentStart + segment * Math.Clamp(t, 0f, 1f);
                Vector3 local = candidate - center;
                float halfWidth = (float)Math.Clamp(ResolveArmorPlateHitWidthM(plate) * 0.5, 0.015, 0.30);
                float halfHeight = (float)Math.Clamp(ResolveArmorPlateHitHeightM(plate) * 0.5, 0.015, 0.30);
                float tolerance = (float)Math.Max(0.010, projectileRadiusM + 0.006);
                if (Math.Abs(Vector3.Dot(local, side)) <= halfWidth + tolerance
                    && Math.Abs(Vector3.Dot(local, up)) <= halfHeight + tolerance)
                {
                    segmentT = Math.Clamp(t, 0.0f, 1.0f);
                    hitPoint = candidate;
                    return true;
                }
            }
        }

        (double distanceSq, Vector3 closest) = PointToSegmentDistanceSquared(center, segmentStart, segmentEnd);
        double plateHitRadiusM = projectileRadiusM + Math.Max(0.065, HitPlateHalfDiagonalM(plate));
        if (distanceSq > plateHitRadiusM * plateHitRadiusM)
        {
            return false;
        }

        hitPoint = closest;
        segmentT = Math.Clamp(Vector3.Dot(closest - segmentStart, segment) / segmentLengthSq, 0.0, 1.0);
        return true;
    }

    private static double HitPlateHalfDiagonalM(ArmorPlateTarget plate)
    {
        double width = ResolveArmorPlateHitWidthM(plate);
        double height = ResolveArmorPlateHitHeightM(plate);
        return Math.Sqrt(width * width + height * height) * 0.5;
    }

    private static double ResolveArmorPlateHitWidthM(ArmorPlateTarget plate)
    {
        double width = plate.WidthM > 1e-6 ? plate.WidthM : plate.SideLengthM;
        return Math.Clamp(width, 0.03, 0.60);
    }

    private static double ResolveArmorPlateHitHeightM(ArmorPlateTarget plate)
    {
        double height = plate.HeightSpanM > 1e-6 ? plate.HeightSpanM : plate.SideLengthM;
        return Math.Clamp(height, 0.03, 0.60);
    }

    public static double ProjectileSpeedMps(string ammoType)
    {
        return string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 12.0 : 25.0;
    }

    public static double ProjectileSpeedMps(SimulationEntity shooter)
    {
        if (string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            bool rangedHero = string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                && string.Equals(RuleSet.NormalizeHeroMode(shooter.HeroPerformanceMode), "ranged_priority", StringComparison.OrdinalIgnoreCase);
            return rangedHero ? 16.5 : 12.0;
        }

        return 25.0;
    }

    public static double ProjectileDiameterM(string ammoType)
    {
        return string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 0.042 : 0.017;
    }

    public static bool IsStructure(SimulationEntity entity)
    {
        return string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
    }

    public static string DescribeArmorPlateDirection(SimulationEntity target, ArmorPlateTarget plate)
    {
        if (plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase))
        {
            return "energy disk";
        }

        if (plate.Id.StartsWith("armor_", StringComparison.OrdinalIgnoreCase))
        {
            double relativeYaw = NormalizeSignedDeg(plate.YawDeg - target.AngleDeg);
            double abs = Math.Abs(relativeYaw);
            if (abs <= 45.0)
            {
                return "front";
            }

            if (abs >= 135.0)
            {
                return "rear";
            }

            return relativeYaw > 0.0 ? "left" : "right";
        }

        if (plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase))
        {
            return "mid tilted";
        }

        if (plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase))
        {
            return "rotating ring";
        }

        if (plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase))
        {
            return "top sliding";
        }

        if (plate.Id.Equals("base_core", StringComparison.OrdinalIgnoreCase))
        {
            return "core";
        }

        return plate.Id;
    }

    public static double NormalizeDeg(double degrees)
    {
        double value = degrees % 360.0;
        if (value < 0.0)
        {
            value += 360.0;
        }

        return value;
    }

    public static double NormalizeSignedDeg(double degrees)
    {
        double value = NormalizeDeg(degrees);
        return value > 180.0 ? value - 360.0 : value;
    }

    private static bool IsPlateFacingShooter(SimulationEntity shooter, ArmorPlateTarget plate)
    {
        if (plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        double toShooterYawDeg = NormalizeDeg(RadiansToDegrees(Math.Atan2(shooter.Y - plate.Y, shooter.X - plate.X)));
        double facingError = Math.Abs(NormalizeSignedDeg(toShooterYawDeg - plate.YawDeg));
        return facingError <= 86.0;
    }

    private static double ComputeFacingMargin(SimulationEntity shooter, ArmorPlateTarget plate)
    {
        if (plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        double toShooterYawDeg = NormalizeDeg(RadiansToDegrees(Math.Atan2(shooter.Y - plate.Y, shooter.X - plate.X)));
        double facingError = Math.Abs(NormalizeSignedDeg(toShooterYawDeg - plate.YawDeg));
        return Math.Cos(Math.Min(180.0, facingError) * Math.PI / 180.0);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static double ResolveArmorPlateThicknessM(SimulationEntity entity)
    {
        return Math.Max(
            0.012,
            Math.Max(
                entity.ArmorPlateGapM * 0.75,
                entity.ArmorPlateWidthM * 0.24));
    }

    private readonly record struct EnergyTargetLayout(
        double RotorCenterHeightM,
        double RotorPhaseRad,
        double RotorRadiusM,
        double RotorAxisGapM,
        double DiskDiameterM);

    private static void ResolveChassisAxes(
        double yawDeg,
        double pitchDeg,
        double rollDeg,
        out Vector3 forward,
        out Vector3 right,
        out Vector3 up)
    {
        double yawRad = DegreesToRadians(yawDeg);
        Vector3 flatForward = new((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));
        Vector3 flatRight = new(-flatForward.Z, 0f, flatForward.X);
        float pitch = (float)DegreesToRadians(Math.Clamp(pitchDeg, -32.0, 32.0));
        float roll = (float)DegreesToRadians(Math.Clamp(rollDeg, -28.0, 28.0));
        forward = Vector3.Normalize(flatForward * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch));
        right = Vector3.Normalize(flatRight * MathF.Cos(roll) + Vector3.UnitY * MathF.Sin(roll));
        up = Vector3.Normalize(Vector3.Cross(right, forward));
        right = Vector3.Normalize(Vector3.Cross(forward, up));
    }

    private static (double X, double Y, double HeightM) PredictArmorPlatePoint(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double distanceM,
        out double leadTimeSec,
        out double leadDistanceM)
    {
        double projectileSpeedMps = Math.Max(1.0, ProjectileSpeedMps(shooter));
        double dragTimeScale = 1.0 + Math.Clamp(distanceM * 0.012, 0.0, 0.18);
        double travelLeadTimeSec = Math.Clamp(distanceM / projectileSpeedMps * dragTimeScale, 0.0, 0.75);
        double fireLatencySec = ResolveAutoAimFiringLatencySec(shooter);
        leadTimeSec = Math.Clamp(travelLeadTimeSec + fireLatencySec, 0.0, 0.85);
        bool preferHighArc = shooter.HeroDeploymentActive
            && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && IsHeroDeploymentTargetPlate(target, plate);

        (double predictedX, double predictedY, double predictedHeightM) = (plate.X, plate.Y, plate.HeightM);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            (predictedX, predictedY, predictedHeightM, leadDistanceM) = PredictArmorPlatePointAtLeadTime(
                world,
                shooter,
                target,
                plate,
                leadTimeSec);
            (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM,
                preferHighArc);
            double refinedTravelTimeSec = EstimateProjectileTravelTimeSec(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM,
                pitchDeg);
            double refinedLeadTimeSec = Math.Clamp(refinedTravelTimeSec + fireLatencySec, 0.0, 1.10);
            if (Math.Abs(refinedLeadTimeSec - leadTimeSec) <= 0.004)
            {
                leadTimeSec = refinedLeadTimeSec;
                break;
            }

            leadTimeSec = leadTimeSec * 0.38 + refinedLeadTimeSec * 0.62;
        }

        (predictedX, predictedY, predictedHeightM, leadDistanceM) = PredictArmorPlatePointAtLeadTime(
            world,
            shooter,
            target,
            plate,
            leadTimeSec);
        return (predictedX, predictedY, predictedHeightM);
    }

    private static (double X, double Y, double HeightM, double LeadDistanceM) PredictArmorPlatePointAtLeadTime(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double leadTimeSec)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        if (leadTimeSec <= 1e-4)
        {
            return (plate.X, plate.Y, plate.HeightM, 0.0);
        }

        bool suppressLateralLead = ShouldSuppressStructureTopLateralLead(shooter, plate);
        (double translationLeadScale, double angularLeadScale) = ResolveAutoAimLeadScales(shooter, target);
        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            string? plateTeam = null;
            SimulationTeamState? teamState = null;
            if (TryParseEnergyArmIndex(plate.Id, out string parsedTeam, out _))
            {
                plateTeam = parsedTeam;
                world.Teams.TryGetValue(parsedTeam, out teamState);
            }
            else if (world.Teams.TryGetValue(shooter.Team, out SimulationTeamState? shooterTeamState))
            {
                plateTeam = shooter.Team;
                teamState = shooterTeamState;
            }

            ArmorPlateTarget predictedPlate = GetEnergyMechanismTargets(
                    target,
                    metersPerWorldUnit,
                    world.GameTimeSec + leadTimeSec,
                    plateTeam,
                    teamState)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, plate.Id, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(predictedPlate.Id))
            {
                return (predictedPlate.X, predictedPlate.Y, predictedPlate.HeightM, DistanceBetweenPlatePointsM(metersPerWorldUnit, plate, predictedPlate));
            }
        }

        if (IsStructure(target))
        {
            ArmorPlateTarget predictedPlate = GetArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec + leadTimeSec)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, plate.Id, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(predictedPlate.Id))
            {
                if (suppressLateralLead)
                {
                    return (plate.X, plate.Y, predictedPlate.HeightM, Math.Abs(predictedPlate.HeightM - plate.HeightM));
                }

                return (predictedPlate.X, predictedPlate.Y, predictedPlate.HeightM, DistanceBetweenPlatePointsM(metersPerWorldUnit, plate, predictedPlate));
            }
        }

        double offsetXWorld = plate.X - target.X;
        double offsetYWorld = plate.Y - target.Y;
        double angularLeadRad = DegreesToRadians(target.AngularVelocityDegPerSec * leadTimeSec * angularLeadScale);
        double rotatedOffsetXWorld = offsetXWorld * Math.Cos(angularLeadRad) - offsetYWorld * Math.Sin(angularLeadRad);
        double rotatedOffsetYWorld = offsetXWorld * Math.Sin(angularLeadRad) + offsetYWorld * Math.Cos(angularLeadRad);

        double predictedX = target.X + target.VelocityXWorldPerSec * leadTimeSec * translationLeadScale + rotatedOffsetXWorld;
        double predictedY = target.Y + target.VelocityYWorldPerSec * leadTimeSec * translationLeadScale + rotatedOffsetYWorld;
        double predictedHeightM = Math.Max(0.0, plate.HeightM + target.VerticalVelocityMps * leadTimeSec);
        if (suppressLateralLead)
        {
            predictedX = plate.X;
            predictedY = plate.Y;
        }

        double leadDxM = (predictedX - plate.X) * metersPerWorldUnit;
        double leadDyM = (predictedY - plate.Y) * metersPerWorldUnit;
        double leadDzM = predictedHeightM - plate.HeightM;
        double leadDistanceM = Math.Sqrt(leadDxM * leadDxM + leadDyM * leadDyM + leadDzM * leadDzM);
        return (predictedX, predictedY, predictedHeightM, leadDistanceM);
    }

    private static double EstimateProjectileTravelTimeSec(
        SimulationWorldState world,
        SimulationEntity shooter,
        double targetX,
        double targetY,
        double targetHeightM,
        double pitchDeg)
    {
        (double muzzleX, double muzzleY, double muzzleHeightM) = ComputeMuzzlePoint(world, shooter, pitchDeg);
        double horizontalM = Math.Sqrt(
            Math.Pow(targetX - muzzleX, 2)
            + Math.Pow(targetY - muzzleY, 2)) * Math.Max(world.MetersPerWorldUnit, 1e-6);
        double dzM = targetHeightM - muzzleHeightM;
        double speedMps = Math.Max(1.0, ProjectileSpeedMps(shooter));
        if (horizontalM <= 1e-4)
        {
            return 0.0;
        }

        if (TryEstimateBallisticTravelTimeWithDrag(horizontalM, dzM, speedMps, shooter.AmmoType, DegreesToRadians(pitchDeg), out double dragTimeSec))
        {
            return dragTimeSec;
        }

        double horizontalSpeedMps = Math.Max(0.1, speedMps * Math.Cos(DegreesToRadians(pitchDeg)));
        return horizontalM / horizontalSpeedMps;
    }

    private static bool TryEstimateBallisticTravelTimeWithDrag(
        double horizontalM,
        double dzM,
        double speedMps,
        string ammoType,
        double pitchRad,
        out double travelTimeSec)
    {
        travelTimeSec = 0.0;
        double vxMps = speedMps * Math.Cos(pitchRad);
        double vzMps = speedMps * Math.Sin(pitchRad);
        if (vxMps <= 1e-5)
        {
            return false;
        }

        double xM = 0.0;
        double zM = 0.0;
        double previousXM = 0.0;
        double previousZM = 0.0;
        double previousTimeSec = 0.0;
        double diameterM = ProjectileDiameterM(ammoType);
        double areaM2 = Math.PI * diameterM * diameterM * 0.25;
        double massKg = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 0.041 : 0.0032;
        double dt = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 1.0 / 240.0 : 1.0 / 220.0;
        int maxSteps = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 900 : 620;
        for (int step = 0; step < maxSteps; step++)
        {
            previousXM = xM;
            previousZM = zM;
            previousTimeSec = travelTimeSec;

            double speed = Math.Sqrt(vxMps * vxMps + vzMps * vzMps);
            if (speed > 1e-6)
            {
                double dragCoefficient = 0.47;
                double airDensityKgM3 = 1.20;
                double dragAccelMps2 = 0.5 * airDensityKgM3 * dragCoefficient * areaM2 * speed * speed / Math.Max(0.001, massKg);
                dragAccelMps2 = Math.Min(dragAccelMps2, speed / Math.Max(dt, 1e-6) * 0.72);
                double dragStep = dragAccelMps2 * dt / speed;
                vxMps -= vxMps * dragStep;
                vzMps -= vzMps * dragStep;
            }

            vzMps -= GravityMps2 * dt;
            xM += vxMps * dt;
            zM += vzMps * dt;
            travelTimeSec += dt;

            if (xM >= horizontalM)
            {
                double segment = Math.Max(xM - previousXM, 1e-6);
                double lerp = Math.Clamp((horizontalM - previousXM) / segment, 0.0, 1.0);
                double sampledZM = previousZM + (zM - previousZM) * lerp;
                if (Math.Abs(sampledZM - dzM) <= Math.Max(0.45, horizontalM * 0.10))
                {
                    travelTimeSec = previousTimeSec + (travelTimeSec - previousTimeSec) * lerp;
                    return true;
                }

                travelTimeSec = previousTimeSec + (travelTimeSec - previousTimeSec) * lerp;
                return true;
            }

            if (vxMps <= 1e-4 || (zM < dzM - 6.0 && vzMps < 0.0))
            {
                break;
            }
        }

        return false;
    }

    private static double ResolveAutoAimFiringLatencySec(SimulationEntity shooter)
    {
        return string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? LargeProjectileAutoAimLatencySec
            : SmallProjectileAutoAimLatencySec;
    }

    private static bool ShouldSuppressStructureTopLateralLead(SimulationEntity shooter, ArmorPlateTarget plate)
    {
        return string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            && (plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase)
                || plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase));
    }

    private static (double TranslationLeadScale, double AngularLeadScale) ResolveAutoAimLeadScales(
        SimulationEntity shooter,
        SimulationEntity target)
    {
        if (target.AutoAimInstabilityTimerSec <= 1e-6)
        {
            return (1.0, 1.0);
        }

        double instabilityRatio = Math.Clamp(target.AutoAimInstabilityTimerSec / 0.42, 0.0, 1.0);
        bool smallProjectile = !string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        double minTranslation = smallProjectile ? 0.22 : 0.38;
        double minAngular = smallProjectile ? 0.12 : 0.24;
        double translationLeadScale = 1.0 - (1.0 - minTranslation) * instabilityRatio;
        double angularLeadScale = 1.0 - (1.0 - minAngular) * instabilityRatio;
        return (translationLeadScale, angularLeadScale);
    }

    private static double DistanceBetweenPlatePointsM(double metersPerWorldUnit, ArmorPlateTarget current, ArmorPlateTarget predicted)
    {
        double dxM = (predicted.X - current.X) * metersPerWorldUnit;
        double dyM = (predicted.Y - current.Y) * metersPerWorldUnit;
        double dzM = predicted.HeightM - current.HeightM;
        return Math.Sqrt(dxM * dxM + dyM * dyM + dzM * dzM);
    }

    private static IReadOnlyList<ArmorPlateTarget> GetOutpostArmorPlateTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec)
    {
        double baseYawDeg = NormalizeDeg(target.AngleDeg);
        double baseYawRad = DegreesToRadians(baseYawDeg);
        double radiusWorld = (OutpostTowerRadiusM + 0.055) / Math.Max(metersPerWorldUnit, 1e-6);
        double topRadiusWorld = Math.Max(0.0, OutpostTowerRadiusM + 0.035) / Math.Max(metersPerWorldUnit, 1e-6);
        double baseLiftM = Math.Clamp(target.StructureBaseLiftM <= 1e-6 ? OutpostBaseLiftM : target.StructureBaseLiftM, 0.0, 1.20);
        double centerHeightM = target.GroundHeightM + baseLiftM + OutpostTowerHeightM;
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateWidthM, target.ArmorPlateHeightM), 0.04, 0.60);
        var plates = new List<ArmorPlateTarget>(4)
        {
            new(
                "outpost_top",
                target.X
                    + (Math.Cos(baseYawRad) * target.StructureTopArmorOffsetXM - Math.Sin(baseYawRad) * target.StructureTopArmorOffsetZM)
                        / Math.Max(metersPerWorldUnit, 1e-6)
                    + Math.Cos(baseYawRad) * topRadiusWorld,
                target.Y
                    + (Math.Sin(baseYawRad) * target.StructureTopArmorOffsetXM + Math.Cos(baseYawRad) * target.StructureTopArmorOffsetZM)
                        / Math.Max(metersPerWorldUnit, 1e-6)
                    + Math.Sin(baseYawRad) * topRadiusWorld,
                target.GroundHeightM
                    + baseLiftM
                    + Math.Max(0.05, target.StructureTopArmorCenterHeightM > 1e-6 ? target.StructureTopArmorCenterHeightM : centerHeightM + OutpostTopArmorCenterLiftM - target.GroundHeightM - baseLiftM),
                baseYawDeg,
                armorSideLengthM),
        };

        double ringBaseYawDeg = NormalizeDeg(baseYawDeg + RadiansToDegrees(OutpostRingSpeedRadPerSec * Math.Max(0.0, gameTimeSec)));
        double[] heightOffsets = { 0.05, 0.0, -0.05 };
        for (int index = 0; index < 3; index++)
        {
            double yawDeg = NormalizeDeg(ringBaseYawDeg + index * 120.0);
            double yawRad = DegreesToRadians(yawDeg);
            plates.Add(new ArmorPlateTarget(
                $"outpost_ring_{index + 1}",
                target.X + Math.Cos(yawRad) * radiusWorld,
                target.Y + Math.Sin(yawRad) * radiusWorld,
                target.GroundHeightM + baseLiftM + OutpostTowerHeightM - 0.10 + heightOffsets[index],
                yawDeg,
                armorSideLengthM));
        }

        return plates;
    }

    private static IReadOnlyList<ArmorPlateTarget> GetBaseArmorPlateTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec)
    {
        double baseYawDeg = NormalizeDeg(target.AngleDeg);
        double baseYawRad = DegreesToRadians(baseYawDeg);
        double sideYawRad = baseYawRad + Math.PI * 0.5;
        double metersToWorld = 1.0 / Math.Max(metersPerWorldUnit, 1e-6);
        double bodyLength = Math.Clamp(target.BodyLengthM, 1.10, 2.35);
        double bodyHeight = Math.Clamp(target.BodyHeightM, 0.70, 1.60);
        double slideM = 0.0;
        double topForwardM = bodyLength * 0.06;
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateWidthM, target.ArmorPlateHeightM), 0.04, 0.60);

        var plates = new List<ArmorPlateTarget>(2)
        {
            new(
                "base_top_slide",
                target.X + (Math.Cos(baseYawRad) * (topForwardM + target.StructureTopArmorOffsetXM) + Math.Cos(sideYawRad) * (slideM + target.StructureTopArmorOffsetZM)) * metersToWorld,
                target.Y + (Math.Sin(baseYawRad) * (topForwardM + target.StructureTopArmorOffsetXM) + Math.Sin(sideYawRad) * (slideM + target.StructureTopArmorOffsetZM)) * metersToWorld,
                target.GroundHeightM + Math.Max(0.05, target.StructureTopArmorCenterHeightM > 1e-6 ? target.StructureTopArmorCenterHeightM : bodyHeight * (BaseTopArmorCenterHeightM / BaseDiagramHeightM)),
                baseYawDeg,
                armorSideLengthM),
        };

        if (target.Health < BaseArmorOpenThresholdHealth)
        {
            double coreForwardM = bodyLength * 0.15;
            plates.Add(new ArmorPlateTarget(
                "base_core",
                target.X + Math.Cos(baseYawRad) * coreForwardM * metersToWorld,
                target.Y + Math.Sin(baseYawRad) * coreForwardM * metersToWorld,
                target.GroundHeightM + bodyHeight * 0.70,
                baseYawDeg,
                armorSideLengthM));
        }

        return plates;
    }

    private static (double DistanceSquared, Vector3 Point) PointToSegmentDistanceSquared(
        Vector3 point,
        Vector3 segmentStart,
        Vector3 segmentEnd)
    {
        Vector3 segment = segmentEnd - segmentStart;
        float lengthSq = segment.LengthSquared();
        if (lengthSq <= 1e-8f)
        {
            Vector3 delta = point - segmentStart;
            return (delta.LengthSquared(), segmentStart);
        }

        float t = Vector3.Dot(point - segmentStart, segment) / lengthSq;
        t = Math.Clamp(t, 0f, 1f);
        Vector3 projection = segmentStart + segment * t;
        Vector3 diff = point - projection;
        return (diff.LengthSquared(), projection);
    }

    private static double ComputeAutoAimDistanceCoefficient(double distanceM, double maxDistanceM)
    {
        double effectiveMax = Math.Max(1.0, maxDistanceM);
        if (distanceM <= 1.0)
        {
            return 0.50;
        }

        double t = Math.Clamp((distanceM - 1.0) / Math.Max(1e-6, effectiveMax - 1.0), 0.0, 1.0);
        return 0.50 + t * 0.50;
    }

    private static double ComputeAutoAimMotionCoefficient(SimulationWorldState world, SimulationEntity target)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double translationSpeedMps = Math.Sqrt(
            target.VelocityXWorldPerSec * target.VelocityXWorldPerSec
            + target.VelocityYWorldPerSec * target.VelocityYWorldPerSec) * metersPerWorldUnit;
        bool translating = translationSpeedMps > 0.08;
        bool rotating = Math.Abs(target.AngularVelocityDegPerSec) > 12.0 || target.SmallGyroActive;

        if (translating && rotating)
        {
            return 0.60;
        }

        if (rotating)
        {
            return 0.70;
        }

        if (translating)
        {
            return 0.80;
        }

        return 1.0;
    }

    private static int StableHash(params string?[] values)
    {
        unchecked
        {
            int hash = 17;
            foreach (string? value in values)
            {
                if (string.IsNullOrEmpty(value))
                {
                    hash *= 31;
                    continue;
                }

                foreach (char ch in value)
                {
                    hash = hash * 31 + ch;
                }
            }

            return hash;
        }
    }

    private static double StableSignedUnit(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return x / (double)uint.MaxValue * 2.0 - 1.0;
        }
    }

    private static double SmoothSignedNoise(double phase, int seed)
    {
        double a = Math.Sin(phase + (seed & 255) * 0.071);
        double b = Math.Sin(phase * 0.53 + ((seed >> 8) & 255) * 0.047);
        return Math.Clamp(a * 0.68 + b * 0.32, -1.0, 1.0);
    }
}
