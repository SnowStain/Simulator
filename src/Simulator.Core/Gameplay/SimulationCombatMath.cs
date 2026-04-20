using System.Numerics;

namespace Simulator.Core.Gameplay;

public readonly record struct ArmorPlateTarget(
    string Id,
    double X,
    double Y,
    double HeightM,
    double YawDeg,
    double SideLengthM = 0.13);

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

        double radiusXWorld = Math.Max(0.01, target.BodyLengthM * 0.5 + target.ArmorPlateGapM) / Math.Max(metersPerWorldUnit, 1e-6);
        double radiusYWorld = Math.Max(0.01, target.BodyWidthM * 0.5 * target.BodyRenderWidthScale + target.ArmorPlateGapM) / Math.Max(metersPerWorldUnit, 1e-6);
        double centerHeightM = target.GroundHeightM
            + target.AirborneHeightM
            + Math.Max(0.08, target.BodyClearanceM + target.BodyHeightM * 0.55);

        IReadOnlyList<double> orbitYaws = target.ArmorOrbitYawsDeg.Count > 0
            ? target.ArmorOrbitYawsDeg
            : new[] { 0d, 180d, 90d, 270d };
        IReadOnlyList<double> selfYaws = target.ArmorSelfYawsDeg.Count > 0
            ? target.ArmorSelfYawsDeg
            : orbitYaws;

        var plates = new List<ArmorPlateTarget>(orbitYaws.Count);
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateWidthM, target.ArmorPlateHeightM), 0.04, 0.60);
        for (int index = 0; index < orbitYaws.Count; index++)
        {
            double orbitYawDeg = orbitYaws[index];
            double selfYawDeg = index < selfYaws.Count ? selfYaws[index] : orbitYawDeg;
            double orbitYawRad = DegreesToRadians(target.AngleDeg + orbitYawDeg);
            plates.Add(new ArmorPlateTarget(
                $"armor_{index + 1}",
                target.X + Math.Cos(orbitYawRad) * radiusXWorld,
                target.Y + Math.Sin(orbitYawRad) * radiusYWorld,
                centerHeightM,
                NormalizeDeg(target.AngleDeg + selfYawDeg),
                armorSideLengthM));
        }

        return plates;
    }

    public static bool TryAcquireAutoAimTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        Func<SimulationEntity, ArmorPlateTarget, bool>? canSeePlate = null)
    {
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
        double targetHeightM)
    {
        double pitch = shooter.GimbalPitchDeg;
        double yaw = shooter.TurretYawDeg;
        for (int iteration = 0; iteration < 2; iteration++)
        {
            (double muzzleX, double muzzleY, double muzzleHeightM) = ComputeMuzzlePoint(world, shooter, pitch);
            double dxWorld = targetX - muzzleX;
            double dyWorld = targetY - muzzleY;
            double horizontalM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * Math.Max(world.MetersPerWorldUnit, 1e-6);
            yaw = NormalizeDeg(RadiansToDegrees(Math.Atan2(dyWorld, dxWorld)));

            double dzM = targetHeightM - muzzleHeightM;
            double speedMps = ProjectileSpeedMps(shooter);
            double effectiveSpeedMps = speedMps * (1.0 - Math.Clamp(horizontalM * 0.012, 0.0, 0.16));
            double ballisticLift = horizontalM <= 1e-4
                ? 0.0
                : GravityMps2 * horizontalM * horizontalM / Math.Max(2.0 * effectiveSpeedMps * effectiveSpeedMps, 1e-6);
            pitch = Math.Clamp(
                RadiansToDegrees(Math.Atan2(dzM + ballisticLift, Math.Max(horizontalM, 1e-4))),
                -40.0,
                40.0);
        }

        return (yaw, pitch);
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
        double loftBiasM = heroDeploymentTopTarget
            ? Math.Clamp(0.08 + distanceM * 0.028, 0.10, 0.28)
            : 0.0;
        if (accuracy >= 0.999)
        {
            (double perfectYaw, double perfectPitch) = ComputeAimAnglesToPoint(world, shooter, predictedX, predictedY, predictedHeightM + loftBiasM);
            if (heroDeploymentTopTarget)
            {
                perfectPitch = Math.Min(40.0, perfectPitch + 1.5);
            }

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
            predictedHeightM + heightErrorM + loftBiasM);
        if (heroDeploymentTopTarget)
        {
            pitchDeg = Math.Min(40.0, pitchDeg + 1.5);
        }

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
        Vector3 segment = segmentEnd - segmentStart;
        double segmentLengthSq = Math.Max(1e-9, segment.LengthSquared());

        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id, shooter.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ArmorPlateTarget plate in GetArmorPlateTargets(candidate, metersPerWorldUnit, world.GameTimeSec))
            {
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
                float halfSide = (float)Math.Clamp(plate.SideLengthM * 0.5, 0.015, 0.30);
                float tolerance = (float)Math.Max(0.010, projectileRadiusM + 0.006);
                if (Math.Abs(Vector3.Dot(local, side)) <= halfSide + tolerance
                    && Math.Abs(Vector3.Dot(local, up)) <= halfSide + tolerance)
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
        double sideLength = Math.Clamp(plate.SideLengthM, 0.03, 0.60);
        return sideLength * Math.Sqrt(0.5);
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
            || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase);
    }

    public static string DescribeArmorPlateDirection(SimulationEntity target, ArmorPlateTarget plate)
    {
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
        leadTimeSec = Math.Clamp(distanceM / projectileSpeedMps * dragTimeScale, 0.0, 0.75);
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);

        if (leadTimeSec <= 1e-4)
        {
            leadDistanceM = 0.0;
            return (plate.X, plate.Y, plate.HeightM);
        }

        if (IsStructure(target))
        {
            ArmorPlateTarget predictedPlate = GetArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec + leadTimeSec)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, plate.Id, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(predictedPlate.Id))
            {
                leadDistanceM = DistanceBetweenPlatePointsM(metersPerWorldUnit, plate, predictedPlate);
                return (predictedPlate.X, predictedPlate.Y, predictedPlate.HeightM);
            }
        }

        double offsetXWorld = plate.X - target.X;
        double offsetYWorld = plate.Y - target.Y;
        double angularLeadRad = DegreesToRadians(target.AngularVelocityDegPerSec * leadTimeSec);
        double rotatedOffsetXWorld = offsetXWorld * Math.Cos(angularLeadRad) - offsetYWorld * Math.Sin(angularLeadRad);
        double rotatedOffsetYWorld = offsetXWorld * Math.Sin(angularLeadRad) + offsetYWorld * Math.Cos(angularLeadRad);

        double predictedX = target.X + target.VelocityXWorldPerSec * leadTimeSec + rotatedOffsetXWorld;
        double predictedY = target.Y + target.VelocityYWorldPerSec * leadTimeSec + rotatedOffsetYWorld;
        double predictedHeightM = Math.Max(0.0, plate.HeightM + target.VerticalVelocityMps * leadTimeSec);

        double leadDxM = (predictedX - plate.X) * metersPerWorldUnit;
        double leadDyM = (predictedY - plate.Y) * metersPerWorldUnit;
        double leadDzM = predictedHeightM - plate.HeightM;
        leadDistanceM = Math.Sqrt(leadDxM * leadDxM + leadDyM * leadDyM + leadDzM * leadDzM);
        return (predictedX, predictedY, predictedHeightM);
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
                target.X + Math.Cos(baseYawRad) * topRadiusWorld,
                target.Y + Math.Sin(baseYawRad) * topRadiusWorld,
                centerHeightM + OutpostTopArmorCenterLiftM,
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
                target.X + (Math.Cos(baseYawRad) * topForwardM + Math.Cos(sideYawRad) * slideM) * metersToWorld,
                target.Y + (Math.Sin(baseYawRad) * topForwardM + Math.Sin(sideYawRad) * slideM) * metersToWorld,
                target.GroundHeightM + bodyHeight * (BaseTopArmorCenterHeightM / BaseDiagramHeightM),
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
