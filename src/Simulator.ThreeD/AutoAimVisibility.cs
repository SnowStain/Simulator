using System.Numerics;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal static class AutoAimVisibility
{
    public static bool CanSeePlate(
        SimulationWorldState world,
        RuntimeGridData? runtimeGrid,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        (double muzzleX, double muzzleY, double muzzleHeightM) = SimulationCombatMath.ComputeMuzzlePoint(world, shooter);
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        Vector3 start = new(
            (float)(muzzleX * metersPerWorldUnit),
            (float)muzzleHeightM,
            (float)(muzzleY * metersPerWorldUnit));

        foreach (Vector3 sample in BuildPlateSamplePoints(plate, metersPerWorldUnit))
        {
            if (!IsSampleInsideFirstPersonFov(shooter, start, sample))
            {
                continue;
            }

            if (!IsTerrainOccluding(runtimeGrid, metersPerWorldUnit, start, sample)
                && !IsTargetBodyOccludingPlate(metersPerWorldUnit, target, plate, start, sample)
                && !IsEntityOccluding(world, metersPerWorldUnit, shooter, target, start, sample))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Vector3> BuildPlateSamplePoints(ArmorPlateTarget plate, double metersPerWorldUnit)
    {
        float centerX = (float)(plate.X * metersPerWorldUnit);
        float centerY = (float)plate.HeightM;
        float centerZ = (float)(plate.Y * metersPerWorldUnit);
        Vector3 center = new(centerX, centerY, centerZ);
        float yawRad = (float)(plate.YawDeg * Math.PI / 180.0);
        Vector3 forward = new(MathF.Cos(yawRad), 0f, MathF.Sin(yawRad));
        Vector3 side = new(-forward.Z, 0f, forward.X);
        Vector3 vertical = plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase)
            ? Vector3.Normalize(forward + Vector3.UnitY)
            : Vector3.UnitY;
        float halfSideM = (float)Math.Clamp(plate.SideLengthM * 0.46, 0.025, 0.30);

        yield return center;
        yield return center + side * halfSideM;
        yield return center - side * halfSideM;
        yield return center + vertical * halfSideM;
        yield return center - vertical * halfSideM;
        yield return center + side * halfSideM + vertical * halfSideM;
        yield return center + side * halfSideM - vertical * halfSideM;
        yield return center - side * halfSideM + vertical * halfSideM;
        yield return center - side * halfSideM - vertical * halfSideM;
    }

    private static bool IsSampleInsideFirstPersonFov(SimulationEntity shooter, Vector3 start, Vector3 sample)
    {
        if (shooter.HeroDeploymentActive
            && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!shooter.IsPlayerControlled)
        {
            return true;
        }

        Vector3 delta = sample - start;
        float horizontal = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        if (horizontal <= 1e-5f)
        {
            return true;
        }

        double yawDeg = Math.Atan2(delta.Z, delta.X) * 180.0 / Math.PI;
        double pitchDeg = Math.Atan2(delta.Y, horizontal) * 180.0 / Math.PI;
        double yawError = Math.Abs(SimulationCombatMath.NormalizeSignedDeg(yawDeg - shooter.TurretYawDeg));
        double pitchError = Math.Abs(pitchDeg - shooter.GimbalPitchDeg);
        return yawError <= 48.0 && pitchError <= 32.0;
    }

    private static bool IsTerrainOccluding(RuntimeGridData? runtimeGrid, double metersPerWorldUnit, Vector3 start, Vector3 end)
    {
        if (runtimeGrid is null || !runtimeGrid.IsValid)
        {
            return false;
        }

        Vector3 segment = end - start;
        float distanceM = segment.Length();
        if (distanceM <= 0.08f)
        {
            return false;
        }

        int samples = Math.Clamp((int)MathF.Ceiling(distanceM / 0.08f), 8, 180);
        double maxWorldX = runtimeGrid.WidthCells * runtimeGrid.CellWidthWorld;
        double maxWorldY = runtimeGrid.HeightCells * runtimeGrid.CellHeightWorld;
        for (int index = 1; index < samples; index++)
        {
            float t = index / (float)samples;
            if (t <= 0.06f || t >= 0.96f)
            {
                continue;
            }

            Vector3 sample = start + segment * t;
            double sampleWorldX = sample.X / metersPerWorldUnit;
            double sampleWorldY = sample.Z / metersPerWorldUnit;
            if (sampleWorldX < 0.0 || sampleWorldY < 0.0 || sampleWorldX >= maxWorldX || sampleWorldY >= maxWorldY)
            {
                continue;
            }

            int cellX = Math.Clamp((int)Math.Floor(sampleWorldX / Math.Max(runtimeGrid.CellWidthWorld, 1e-6)), 0, runtimeGrid.WidthCells - 1);
            int cellY = Math.Clamp((int)Math.Floor(sampleWorldY / Math.Max(runtimeGrid.CellHeightWorld, 1e-6)), 0, runtimeGrid.HeightCells - 1);
            int sampleIndex = runtimeGrid.IndexOf(cellX, cellY);
            float terrainHeight = runtimeGrid.SampleOcclusionHeight(sampleWorldX, sampleWorldY);
            float visionHeight = terrainHeight;
            if ((terrainHeight > 0.025f || runtimeGrid.VisionBlockMap[sampleIndex])
                && sample.Y <= visionHeight + 0.025f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEntityOccluding(
        SimulationWorldState world,
        double metersPerWorldUnit,
        SimulationEntity shooter,
        SimulationEntity target,
        Vector3 start,
        Vector3 end)
    {
        Vector2 start2 = new(start.X, start.Z);
        Vector2 end2 = new(end.X, end.Z);
        Vector2 segment2 = end2 - start2;
        float lengthSq = segment2.LengthSquared();
        if (lengthSq <= 1e-6f)
        {
            return false;
        }

        foreach (SimulationEntity other in world.Entities)
        {
            if (ReferenceEquals(other, shooter)
                || ReferenceEquals(other, target)
                || !other.IsAlive
                || other.IsSimulationSuppressed)
            {
                continue;
            }

            if (!IsOccludingEntity(other))
            {
                continue;
            }

            if (SegmentIntersectsEntityCore(other, metersPerWorldUnit, start, end, 0.94f))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOccludingEntity(SimulationEntity entity)
    {
        return string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase)
            || SimulationCombatMath.IsStructure(entity);
    }

    private static bool IsTargetBodyOccludingPlate(
        double metersPerWorldUnit,
        SimulationEntity target,
        ArmorPlateTarget plate,
        Vector3 start,
        Vector3 end)
    {
        if (!string.Equals(target.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Vector3 plateCenter = new(
            (float)(plate.X * metersPerWorldUnit),
            (float)plate.HeightM,
            (float)(plate.Y * metersPerWorldUnit));
        Vector3 normal = SimulationCombatMath.ResolveArmorPlateNormal(plate);
        Vector3 toShooter = start - plateCenter;
        if (toShooter.LengthSquared() > 1e-8f && Vector3.Dot(Vector3.Normalize(toShooter), normal) >= 0.08f)
        {
            return false;
        }

        // The target itself is not part of the general occluder pass. This extra core test prevents
        // far-side armor from being lockable through the chassis while keeping the near armor visible.
        return SegmentIntersectsEntityCore(target, metersPerWorldUnit, start, end, 0.96f);
    }

    private static bool SegmentIntersectsEntityCore(
        SimulationEntity entity,
        double metersPerWorldUnit,
        Vector3 start,
        Vector3 end,
        float maxT)
    {
        Vector2 center = new((float)(entity.X * metersPerWorldUnit), (float)(entity.Y * metersPerWorldUnit));
        Vector2 start2 = new(start.X, start.Z);
        Vector2 end2 = new(end.X, end.Z);
        Vector2 segment = end2 - start2;
        if (segment.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        float yawRad = (float)(entity.AngleDeg * Math.PI / 180.0);
        Vector2 forward = new(MathF.Cos(yawRad), MathF.Sin(yawRad));
        Vector2 right = new(-forward.Y, forward.X);
        float halfLength = (float)Math.Max(0.06, entity.BodyLengthM * 0.5 + 0.010);
        float halfWidth = (float)Math.Max(0.06, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5 + 0.010);
        if (SimulationCombatMath.IsStructure(entity))
        {
            float radius = (float)Math.Max(0.12, entity.CollisionRadiusWorld * metersPerWorldUnit);
            halfLength = radius * 0.72f;
            halfWidth = radius * 0.72f;
        }

        Vector2 localStart = new(Vector2.Dot(start2 - center, forward), Vector2.Dot(start2 - center, right));
        Vector2 localEnd = new(Vector2.Dot(end2 - center, forward), Vector2.Dot(end2 - center, right));
        Vector2 localDelta = localEnd - localStart;
        if (!TryClipSlab(localStart.X, localDelta.X, -halfLength, halfLength, ref maxT, out float enterX, out float exitX)
            || !TryClipSlab(localStart.Y, localDelta.Y, -halfWidth, halfWidth, ref maxT, out float enterY, out float exitY))
        {
            return false;
        }

        float enter = MathF.Max(0.04f, MathF.Max(enterX, enterY));
        float exit = MathF.Min(maxT, MathF.Min(exitX, exitY));
        if (enter > exit)
        {
            return false;
        }

        float sampleHeight = start.Y + (end.Y - start.Y) * enter;
        float bottom = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM + entity.StructureBaseLiftM - 0.02);
        float top = bottom + (float)Math.Max(
            0.24,
            SimulationCombatMath.IsStructure(entity)
                ? entity.BodyHeightM + 0.16
                : entity.BodyClearanceM + entity.BodyHeightM + entity.GimbalBodyHeightM * 0.55);
        return sampleHeight >= bottom && sampleHeight <= top;
    }

    private static bool TryClipSlab(
        float origin,
        float delta,
        float min,
        float max,
        ref float maxT,
        out float enter,
        out float exit)
    {
        enter = 0f;
        exit = maxT;
        if (MathF.Abs(delta) <= 1e-7f)
        {
            return origin >= min && origin <= max;
        }

        float t0 = (min - origin) / delta;
        float t1 = (max - origin) / delta;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        enter = MathF.Max(0f, t0);
        exit = MathF.Min(maxT, t1);
        return enter <= exit && exit >= 0f;
    }
}
