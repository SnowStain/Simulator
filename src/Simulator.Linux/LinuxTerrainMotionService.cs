using System.Numerics;
using Simulator.Assets;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.Linux;

internal sealed class LinuxTerrainMotionService
{
    private const double DefaultMecanumMaxStepM = 0.25;
    private const double BalancePassiveMaxStepM = 0.03;
    private const double CollisionSkinWorld = 1.5;

    private readonly TerrainCacheHeightField? _heightField;

    public LinuxTerrainMotionService(TerrainCacheHeightField? heightField)
    {
        _heightField = heightField;
    }

    public LinuxMotionResolveResult ResolveMove(
        SimulationWorldState world,
        MapPresetDefinition mapPreset,
        SimulationEntity entity,
        double desiredVelocityXWorldPerSec,
        double desiredVelocityYWorldPerSec,
        double dt)
    {
        double targetX = Math.Clamp(entity.X + desiredVelocityXWorldPerSec * dt, 0.0, world.WorldWidth);
        double targetY = Math.Clamp(entity.Y + desiredVelocityYWorldPerSec * dt, 0.0, world.WorldHeight);
        if (TryResolveCandidate(world, mapPreset, entity, targetX, targetY, out LinuxMotionResolveResult full))
        {
            return full with
            {
                VelocityXWorldPerSec = (full.X - entity.X) / Math.Max(1e-6, dt),
                VelocityYWorldPerSec = (full.Y - entity.Y) / Math.Max(1e-6, dt),
            };
        }

        bool xOk = TryResolveCandidate(world, mapPreset, entity, targetX, entity.Y, out LinuxMotionResolveResult xOnly);
        bool yOk = TryResolveCandidate(world, mapPreset, entity, entity.X, targetY, out LinuxMotionResolveResult yOnly);
        if (xOk && (!yOk || Math.Abs(targetX - entity.X) >= Math.Abs(targetY - entity.Y)))
        {
            return xOnly with
            {
                VelocityXWorldPerSec = (xOnly.X - entity.X) / Math.Max(1e-6, dt),
                VelocityYWorldPerSec = 0.0,
                BlockReason = "slide_y_blocked",
            };
        }

        if (yOk)
        {
            return yOnly with
            {
                VelocityXWorldPerSec = 0.0,
                VelocityYWorldPerSec = (yOnly.Y - entity.Y) / Math.Max(1e-6, dt),
                BlockReason = "slide_x_blocked",
            };
        }

        double currentGround = ResolveGroundHeightM(world, entity.X, entity.Y, entity.GroundHeightM);
        return new LinuxMotionResolveResult(
            entity.X,
            entity.Y,
            0.0,
            0.0,
            currentGround,
            entity.ChassisPitchDeg,
            entity.ChassisRollDeg,
            string.IsNullOrWhiteSpace(full.BlockReason) ? "blocked" : full.BlockReason);
    }

    public LinuxGroundPose ResolveGroundPose(
        SimulationWorldState world,
        SimulationEntity entity)
    {
        IReadOnlyList<SupportSample> samples = SampleSupport(world, entity, entity.X, entity.Y);
        if (samples.Count == 0)
        {
            return new LinuxGroundPose(entity.GroundHeightM, entity.ChassisPitchDeg, entity.ChassisRollDeg);
        }

        return BuildGroundPose(entity, samples);
    }

    private bool TryResolveCandidate(
        SimulationWorldState world,
        MapPresetDefinition mapPreset,
        SimulationEntity entity,
        double candidateX,
        double candidateY,
        out LinuxMotionResolveResult result)
    {
        IReadOnlyList<SupportSample> currentSamples = SampleSupport(world, entity, entity.X, entity.Y);
        IReadOnlyList<SupportSample> candidateSamples = SampleSupport(world, entity, candidateX, candidateY);
        LinuxGroundPose currentPose = currentSamples.Count == 0
            ? new LinuxGroundPose(entity.GroundHeightM, entity.ChassisPitchDeg, entity.ChassisRollDeg)
            : BuildGroundPose(entity, currentSamples);
        LinuxGroundPose candidatePose = candidateSamples.Count == 0
            ? currentPose
            : BuildGroundPose(entity, candidateSamples);

        double verticalRise = candidatePose.GroundHeightM - currentPose.GroundHeightM;
        double maxStep = ResolveMaxStepHeightM(entity);
        if (verticalRise > maxStep + 1e-4)
        {
            result = new LinuxMotionResolveResult(
                entity.X,
                entity.Y,
                0.0,
                0.0,
                currentPose.GroundHeightM,
                currentPose.PitchDeg,
                currentPose.RollDeg,
                $"vertical_step {verticalRise:0.000}m>{maxStep:0.000}m");
            return false;
        }

        if (IntersectsBlockingFacility(world, mapPreset, entity, candidateX, candidateY, candidatePose.GroundHeightM, out string facilityReason)
            || IntersectsOtherEntity(world, entity, candidateX, candidateY, out facilityReason))
        {
            result = new LinuxMotionResolveResult(
                entity.X,
                entity.Y,
                0.0,
                0.0,
                currentPose.GroundHeightM,
                currentPose.PitchDeg,
                currentPose.RollDeg,
                facilityReason);
            return false;
        }

        result = new LinuxMotionResolveResult(
            candidateX,
            candidateY,
            0.0,
            0.0,
            SmoothGround(entity.GroundHeightM, candidatePose.GroundHeightM),
            SmoothAngle(entity.ChassisPitchDeg, candidatePose.PitchDeg, 0.22),
            SmoothAngle(entity.ChassisRollDeg, candidatePose.RollDeg, 0.22),
            string.Empty);
        return true;
    }

    private IReadOnlyList<SupportSample> SampleSupport(
        SimulationWorldState world,
        SimulationEntity entity,
        double centerX,
        double centerY)
    {
        (double halfLength, double halfWidth) = ResolveChassisHalfExtentsWorld(world, entity);
        double yawRad = entity.AngleDeg * Math.PI / 180.0;
        double fx = Math.Cos(yawRad);
        double fy = Math.Sin(yawRad);
        double rx = -fy;
        double ry = fx;
        var offsets = new (double Forward, double Right)[]
        {
            (0, 0),
            (halfLength, halfWidth),
            (halfLength, -halfWidth),
            (-halfLength, halfWidth),
            (-halfLength, -halfWidth),
            (halfLength, 0),
            (-halfLength, 0),
            (0, halfWidth),
            (0, -halfWidth),
        };
        var samples = new List<SupportSample>(offsets.Length);
        foreach ((double forward, double right) in offsets)
        {
            double x = centerX + fx * forward + rx * right;
            double y = centerY + fy * forward + ry * right;
            double height = ResolveGroundHeightM(world, x, y, entity.GroundHeightM);
            samples.Add(new SupportSample(forward, right, height));
        }

        return samples;
    }

    private LinuxGroundPose BuildGroundPose(SimulationEntity entity, IReadOnlyList<SupportSample> samples)
    {
        double ground = samples.Max(sample => sample.HeightM);
        double front = samples.Where(sample => sample.Forward > 0).DefaultIfEmpty(samples[0]).Average(sample => sample.HeightM);
        double rear = samples.Where(sample => sample.Forward < 0).DefaultIfEmpty(samples[0]).Average(sample => sample.HeightM);
        double left = samples.Where(sample => sample.Right > 0).DefaultIfEmpty(samples[0]).Average(sample => sample.HeightM);
        double right = samples.Where(sample => sample.Right < 0).DefaultIfEmpty(samples[0]).Average(sample => sample.HeightM);
        double length = Math.Max(0.08, entity.BodyLengthM);
        double width = Math.Max(0.08, entity.BodyWidthM * Math.Max(0.45, entity.BodyRenderWidthScale));
        double pitch = Math.Clamp(Math.Atan2(front - rear, length) * 180.0 / Math.PI, -18.0, 18.0);
        double roll = Math.Clamp(Math.Atan2(left - right, width) * 180.0 / Math.PI, -22.0, 22.0);
        return new LinuxGroundPose(ground, pitch, roll);
    }

    private bool IntersectsBlockingFacility(
        SimulationWorldState world,
        MapPresetDefinition mapPreset,
        SimulationEntity entity,
        double centerX,
        double centerY,
        double groundHeightM,
        out string reason)
    {
        reason = string.Empty;
        (double halfLength, double halfWidth) = ResolveChassisHalfExtentsWorld(world, entity);
        double yawRad = entity.AngleDeg * Math.PI / 180.0;
        double fx = Math.Cos(yawRad);
        double fy = Math.Sin(yawRad);
        double rx = -fy;
        double ry = fx;
        var probes = new (double Forward, double Right)[]
        {
            (0, 0),
            (halfLength + CollisionSkinWorld, halfWidth + CollisionSkinWorld),
            (halfLength + CollisionSkinWorld, -halfWidth - CollisionSkinWorld),
            (-halfLength - CollisionSkinWorld, halfWidth + CollisionSkinWorld),
            (-halfLength - CollisionSkinWorld, -halfWidth - CollisionSkinWorld),
            (halfLength + CollisionSkinWorld, 0),
            (-halfLength - CollisionSkinWorld, 0),
            (0, halfWidth + CollisionSkinWorld),
            (0, -halfWidth - CollisionSkinWorld),
        };

        double bodyBottom = groundHeightM + Math.Max(0.0, entity.BodyClearanceM * 0.35);
        double bodyTop = groundHeightM + Math.Max(0.08, entity.BodyClearanceM + entity.BodyHeightM);
        foreach (FacilityRegion region in mapPreset.Facilities)
        {
            if (!region.BlocksMovement || !VerticalIntervalsOverlap(bodyBottom, bodyTop, region.CollisionBottomM, region.CollisionTopM))
            {
                continue;
            }

            foreach ((double forward, double right) in probes)
            {
                double x = centerX + fx * forward + rx * right;
                double y = centerY + fy * forward + ry * right;
                bool hit = region.HasVolumeDefinition()
                    ? region.ContainsCollisionProjection(x, y, world.MetersPerWorldUnit)
                    : region.Contains(x, y);
                if (!hit)
                {
                    continue;
                }

                reason = $"facility:{region.Id}";
                return true;
            }
        }

        return false;
    }

    private bool IntersectsOtherEntity(
        SimulationWorldState world,
        SimulationEntity entity,
        double candidateX,
        double candidateY,
        out string reason)
    {
        reason = string.Empty;
        double selfRadius = ResolveEntityCollisionRadiusWorld(world, entity);
        foreach (SimulationEntity other in world.Entities)
        {
            if (ReferenceEquals(other, entity)
                || !other.IsAlive
                || !string.Equals(other.EntityType, "robot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double radius = selfRadius + ResolveEntityCollisionRadiusWorld(world, other);
            double dx = candidateX - other.X;
            double dy = candidateY - other.Y;
            if (dx * dx + dy * dy <= radius * radius)
            {
                reason = $"entity:{other.Id}";
                return true;
            }
        }

        return false;
    }

    private double ResolveGroundHeightM(
        SimulationWorldState world,
        double worldX,
        double worldY,
        double fallback)
    {
        if (_heightField is null || !Matrix4x4.Invert(world.RuntimeModelToWorldMatrix, out Matrix4x4 worldToModel))
        {
            return fallback;
        }

        Vector3 modelPoint = Vector3.Transform(new Vector3((float)worldX, 0.0f, (float)worldY), worldToModel);
        if (!_heightField.TrySample(modelPoint.X, modelPoint.Z, out TerrainCacheHeightSample sample))
        {
            return fallback;
        }

        Vector3 scenePoint = Vector3.Transform(new Vector3(modelPoint.X, sample.ModelY, modelPoint.Z), world.RuntimeModelToSceneMatrix);
        return double.IsFinite(scenePoint.Y) ? scenePoint.Y : fallback;
    }

    private static (double HalfLength, double HalfWidth) ResolveChassisHalfExtentsWorld(
        SimulationWorldState? world,
        SimulationEntity entity)
    {
        double metersPerWorldUnit = Math.Max(1e-6, world?.MetersPerWorldUnit ?? 0.0178);
        double halfLength = Math.Max(0.08, entity.BodyLengthM * 0.5) / metersPerWorldUnit;
        double halfWidth = Math.Max(0.08, entity.BodyWidthM * Math.Max(0.45, entity.BodyRenderWidthScale) * 0.5) / metersPerWorldUnit;
        return (halfLength, halfWidth);
    }

    private static double ResolveEntityCollisionRadiusWorld(SimulationWorldState world, SimulationEntity entity)
    {
        (double halfLength, double halfWidth) = ResolveChassisHalfExtentsWorld(world, entity);
        return Math.Sqrt(halfLength * halfLength + halfWidth * halfWidth) + CollisionSkinWorld;
    }

    private static double ResolveMaxStepHeightM(SimulationEntity entity)
    {
        bool isBalance = entity.ChassisSubtype.Contains("balance", StringComparison.OrdinalIgnoreCase)
            || entity.WheelStyle.Contains("balance", StringComparison.OrdinalIgnoreCase);
        if (isBalance && !entity.SmallGyroActive)
        {
            return BalancePassiveMaxStepM;
        }

        double configured = Math.Max(entity.DirectStepHeightM, entity.MaxStepClimbHeightM);
        return configured > 1e-4
            ? Math.Min(DefaultMecanumMaxStepM, configured)
            : DefaultMecanumMaxStepM;
    }

    private static bool VerticalIntervalsOverlap(double aBottom, double aTop, double bBottom, double bTop)
    {
        if (aTop < aBottom)
        {
            (aBottom, aTop) = (aTop, aBottom);
        }

        if (bTop < bBottom)
        {
            (bBottom, bTop) = (bTop, bBottom);
        }

        return aTop >= bBottom - 1e-4 && bTop >= aBottom - 1e-4;
    }

    private static double SmoothGround(double current, double target)
    {
        if (!double.IsFinite(current))
        {
            return target;
        }

        double alpha = target < current ? 0.18 : 0.46;
        return current + (target - current) * alpha;
    }

    private static double SmoothAngle(double current, double target, double alpha)
        => current + (target - current) * Math.Clamp(alpha, 0.0, 1.0);

    private readonly record struct SupportSample(double Forward, double Right, double HeightM);
}

internal readonly record struct LinuxGroundPose(
    double GroundHeightM,
    double PitchDeg,
    double RollDeg);

internal readonly record struct LinuxMotionResolveResult(
    double X,
    double Y,
    double VelocityXWorldPerSec,
    double VelocityYWorldPerSec,
    double GroundHeightM,
    double PitchDeg,
    double RollDeg,
    string BlockReason);
