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
        Vector3 end = new(
            (float)(plate.X * metersPerWorldUnit),
            (float)plate.HeightM,
            (float)(plate.Y * metersPerWorldUnit));

        return !IsTerrainOccluding(runtimeGrid, metersPerWorldUnit, start, end)
            && !IsEntityOccluding(world, metersPerWorldUnit, shooter, target, start, end);
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
            float terrainHeight = runtimeGrid.HeightMap[runtimeGrid.IndexOf(cellX, cellY)];
            if (terrainHeight > 0.025f && sample.Y <= terrainHeight + 0.025f)
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

            Vector2 center2 = new((float)(other.X * metersPerWorldUnit), (float)(other.Y * metersPerWorldUnit));
            float t = Math.Clamp(Vector2.Dot(center2 - start2, segment2) / lengthSq, 0f, 1f);
            if (t <= 0.05f || t >= 0.95f)
            {
                continue;
            }

            Vector2 closest = start2 + segment2 * t;
            float radiusM = (float)Math.Max(0.10, other.CollisionRadiusWorld * metersPerWorldUnit);
            if (Vector2.DistanceSquared(center2, closest) > radiusM * radiusM)
            {
                continue;
            }

            float sampleHeight = start.Y + (end.Y - start.Y) * t;
            float bottom = (float)Math.Max(0.0, other.GroundHeightM + other.AirborneHeightM - 0.04);
            float top = (float)(other.GroundHeightM
                + other.AirborneHeightM
                + Math.Max(0.35, other.BodyClearanceM + other.BodyHeightM + other.GimbalBodyHeightM + 0.10));
            if (sampleHeight >= bottom && sampleHeight <= top)
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
}
