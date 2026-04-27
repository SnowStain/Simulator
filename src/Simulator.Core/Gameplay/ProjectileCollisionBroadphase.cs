using System.Numerics;

namespace Simulator.Core.Gameplay;

public static class ProjectileCollisionBroadphase
{
    public static bool MayIntersectTargetBounds(
        SimulationEntity entity,
        double metersPerWorldUnit,
        double projectileRadiusM,
        Vector3 segmentStartM,
        Vector3 segmentEndM)
    {
        GetApproximateTargetBounds(entity, metersPerWorldUnit, projectileRadiusM, out Vector3 min, out Vector3 max);
        return SegmentIntersectsAabb(segmentStartM, segmentEndM, min, max);
    }

    public static bool MayIntersectObstacleBounds(
        SimulationEntity entity,
        double metersPerWorldUnit,
        double projectileRadiusM,
        Vector3 segmentStartM,
        Vector3 segmentEndM)
    {
        GetApproximateObstacleBounds(entity, metersPerWorldUnit, projectileRadiusM, out Vector3 min, out Vector3 max);
        return SegmentIntersectsAabb(segmentStartM, segmentEndM, min, max);
    }

    public static void GetApproximateObstacleBounds(
        SimulationEntity entity,
        double metersPerWorldUnit,
        double projectileRadiusM,
        out Vector3 min,
        out Vector3 max)
    {
        float centerX = (float)(entity.X * metersPerWorldUnit);
        float centerZ = (float)(entity.Y * metersPerWorldUnit);
        float bottom = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM + entity.StructureBaseLiftM);
        float top = bottom + (float)ResolveObstacleHeightM(entity);

        float halfLengthM;
        float halfWidthM;
        if (SimulationCombatMath.IsStructure(entity))
        {
            float radiusM = (float)Math.Max(0.12, entity.CollisionRadiusWorld * metersPerWorldUnit);
            halfLengthM = radiusM * 0.84f;
            halfWidthM = radiusM * 0.84f;
        }
        else
        {
            halfLengthM = (float)Math.Max(0.08, entity.BodyLengthM * 0.5);
            halfWidthM = (float)Math.Max(0.08, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5);
        }

        float horizontalRadiusM = MathF.Sqrt(halfLengthM * halfLengthM + halfWidthM * halfWidthM)
            + (float)Math.Max(0.02, projectileRadiusM * 1.35);
        float verticalMarginM = (float)Math.Max(0.03, projectileRadiusM);
        min = new Vector3(centerX - horizontalRadiusM, bottom - verticalMarginM, centerZ - horizontalRadiusM);
        max = new Vector3(centerX + horizontalRadiusM, top + verticalMarginM, centerZ + horizontalRadiusM);
    }

    private static void GetApproximateTargetBounds(
        SimulationEntity entity,
        double metersPerWorldUnit,
        double projectileRadiusM,
        out Vector3 min,
        out Vector3 max)
    {
        GetApproximateObstacleBounds(entity, metersPerWorldUnit, projectileRadiusM, out min, out max);
        if (string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            float centerX = (float)(entity.X * metersPerWorldUnit);
            float centerZ = (float)(entity.Y * metersPerWorldUnit);
            float radius = (float)Math.Max(1.45, entity.CollisionRadiusWorld * metersPerWorldUnit + 1.10);
            float bottom = MathF.Min(min.Y, (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM) - 0.20f);
            float top = MathF.Max(max.Y, bottom + 2.65f);
            min = new Vector3(centerX - radius, bottom, centerZ - radius);
            max = new Vector3(centerX + radius, top, centerZ + radius);
            return;
        }

        float extraTop = SimulationCombatMath.IsStructure(entity) ? 0.08f : 0.04f;
        max.Y += extraTop;
    }

    public static bool SegmentIntersectsAabb(
        Vector3 segmentStartM,
        Vector3 segmentEndM,
        Vector3 min,
        Vector3 max)
    {
        Vector3 delta = segmentEndM - segmentStartM;
        float tMin = 0f;
        float tMax = 1f;

        if (!ClipAxis(segmentStartM.X, delta.X, min.X, max.X, ref tMin, ref tMax)
            || !ClipAxis(segmentStartM.Y, delta.Y, min.Y, max.Y, ref tMin, ref tMax)
            || !ClipAxis(segmentStartM.Z, delta.Z, min.Z, max.Z, ref tMin, ref tMax))
        {
            return false;
        }

        return tMax >= 0f && tMin <= 1f && tMin <= tMax;
    }

    private static bool ClipAxis(
        float origin,
        float delta,
        float min,
        float max,
        ref float tMin,
        ref float tMax)
    {
        if (MathF.Abs(delta) <= 1e-7f)
        {
            return origin >= min && origin <= max;
        }

        float inv = 1f / delta;
        float t0 = (min - origin) * inv;
        float t1 = (max - origin) * inv;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        tMin = MathF.Max(tMin, t0);
        tMax = MathF.Min(tMax, t1);
        return tMin <= tMax;
    }

    private static double ResolveObstacleHeightM(SimulationEntity entity)
    {
        if (SimulationCombatMath.IsStructure(entity))
        {
            return Math.Max(0.45, entity.BodyHeightM + entity.GimbalHeightM + 0.18);
        }

        double body = entity.BodyClearanceM + entity.BodyHeightM;
        double turret = entity.GimbalBodyHeightM + Math.Max(0.12, entity.GimbalHeightM * 0.65);
        return Math.Max(0.35, body + turret);
    }
}
