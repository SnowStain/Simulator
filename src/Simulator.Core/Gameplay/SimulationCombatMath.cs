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
    private const double OutpostTowerRadiusM = 0.40;
    private const double OutpostTowerHeightM = 1.878;
    private const double OutpostBaseLiftM = 0.40;
    private const double OutpostRingSpeedRadPerSec = Math.PI * 0.8;
    private const double BaseArmorOpenThresholdHealth = 2000.0;
    private const double BaseTopArmorSlideAmplitudeM = 0.34;
    private const double BaseTopArmorSlideSpeedRadPerSec = Math.PI * 0.7;

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
                if (shooter.IsPlayerControlled && (yawError > 62.0 || pitchError > 38.0))
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

                double score = yawError * yawError + pitchError * pitchError + distanceM * 0.4;
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

        double turretCenterHeight = shooter.GroundHeightM
            + shooter.AirborneHeightM
            + Math.Max(
                shooter.BodyClearanceM + shooter.BodyHeightM,
                shooter.GimbalHeightM > 1e-4
                    ? shooter.GimbalHeightM
                    : shooter.BodyClearanceM + shooter.BodyHeightM + shooter.GimbalBodyHeightM * 0.5);

        Vector2 bodyForward = new((float)Math.Cos(bodyYawRad), (float)Math.Sin(bodyYawRad));
        Vector2 bodyRight = new((float)-Math.Sin(bodyYawRad), (float)Math.Cos(bodyYawRad));
        Vector2 turretBaseWorld = new(
            (float)shooter.X + bodyForward.X * (float)(shooter.GimbalOffsetXM / metersPerWorldUnit) + bodyRight.X * (float)(shooter.GimbalOffsetYM / metersPerWorldUnit),
            (float)shooter.Y + bodyForward.Y * (float)(shooter.GimbalOffsetXM / metersPerWorldUnit) + bodyRight.Y * (float)(shooter.GimbalOffsetYM / metersPerWorldUnit));

        double forwardM = Math.Max(0.03, shooter.GimbalLengthM * 0.5 + shooter.BarrelLengthM);
        double horizontalM = Math.Cos(pitchRad) * forwardM;
        double verticalM = Math.Sin(pitchRad) * forwardM;
        double forwardWorld = horizontalM / metersPerWorldUnit;
        return (
            turretBaseWorld.X + Math.Cos(turretYawRad) * forwardWorld,
            turretBaseWorld.Y + Math.Sin(turretYawRad) * forwardWorld,
            turretCenterHeight + verticalM);
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
            double speedMps = ProjectileSpeedMps(shooter.AmmoType);
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

        double accuracy = Math.Clamp(distanceCoefficient * motionCoefficient, 0.05, 1.0);
        if (accuracy >= 0.999)
        {
            (double perfectYaw, double perfectPitch) = ComputeAimAnglesToPoint(world, shooter, predictedX, predictedY, predictedHeightM);
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
            predictedHeightM + heightErrorM);
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
        out double hitHeightM)
    {
        hitTarget = null;
        hitPlate = default;
        hitX = 0;
        hitY = 0;
        hitHeightM = 0;

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double projectileRadiusM = ProjectileDiameterM(shooter.AmmoType) * 0.5;
        double bestDistanceSq = double.MaxValue;
        Vector3 segmentStart = new((float)(startX * metersPerWorldUnit), (float)startHeightM, (float)(startY * metersPerWorldUnit));
        Vector3 segmentEnd = new((float)(endX * metersPerWorldUnit), (float)endHeightM, (float)(endY * metersPerWorldUnit));

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
                Vector3 platePoint = new(
                    (float)(plate.X * metersPerWorldUnit),
                    (float)plate.HeightM,
                    (float)(plate.Y * metersPerWorldUnit));
                (double distanceSq, Vector3 hitPoint) = PointToSegmentDistanceSquared(platePoint, segmentStart, segmentEnd);
                double plateHitRadiusM = projectileRadiusM + Math.Max(0.065, HitPlateHalfDiagonalM(plate));
                if (distanceSq > plateHitRadiusM * plateHitRadiusM || distanceSq >= bestDistanceSq)
                {
                    continue;
                }

                bestDistanceSq = distanceSq;
                hitTarget = candidate;
                hitPlate = plate;
                hitX = hitPoint.X / metersPerWorldUnit;
                hitY = hitPoint.Z / metersPerWorldUnit;
                hitHeightM = hitPoint.Y;
            }
        }

        return hitTarget is not null;
    }

    private static double HitPlateHalfDiagonalM(ArmorPlateTarget plate)
    {
        double sideLength = Math.Clamp(plate.SideLengthM, 0.03, 0.60);
        return sideLength * Math.Sqrt(0.5);
    }

    public static double ProjectileSpeedMps(string ammoType)
    {
        return string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 15.0 : 20.0;
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
        double projectileSpeedMps = Math.Max(1.0, ProjectileSpeedMps(shooter.AmmoType));
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
        double baseLiftM = Math.Clamp(target.StructureBaseLiftM <= 1e-6 ? OutpostBaseLiftM : target.StructureBaseLiftM, 0.0, 1.20);
        double centerHeightM = target.GroundHeightM + baseLiftM + OutpostTowerHeightM;
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateWidthM, target.ArmorPlateHeightM), 0.04, 0.60);
        var plates = new List<ArmorPlateTarget>(4)
        {
            new(
                "outpost_top",
                target.X + Math.Cos(baseYawRad) * radiusWorld,
                target.Y + Math.Sin(baseYawRad) * radiusWorld,
                centerHeightM - 0.055,
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
        double slideM = Math.Sin(Math.Max(0.0, gameTimeSec) * BaseTopArmorSlideSpeedRadPerSec) * BaseTopArmorSlideAmplitudeM;
        double topForwardM = bodyLength * 0.06;
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateWidthM, target.ArmorPlateHeightM), 0.04, 0.60);

        var plates = new List<ArmorPlateTarget>(2)
        {
            new(
                "base_top_slide",
                target.X + (Math.Cos(baseYawRad) * topForwardM + Math.Cos(sideYawRad) * slideM) * metersToWorld,
                target.Y + (Math.Sin(baseYawRad) * topForwardM + Math.Sin(sideYawRad) * slideM) * metersToWorld,
                target.GroundHeightM + bodyHeight + 0.10,
                NormalizeDeg(baseYawDeg + 90.0),
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
