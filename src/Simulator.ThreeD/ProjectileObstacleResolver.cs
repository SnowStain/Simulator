using System.Numerics;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal static class ProjectileObstacleResolver
{
    public static ProjectileObstacleHit? ResolveHit(
        SimulationWorldState world,
        RuntimeGridData? runtimeGrid,
        SimulationEntity shooter,
        SimulationProjectile projectile,
        double startX,
        double startY,
        double startHeightM,
        double endX,
        double endY,
        double endHeightM)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        Vector3 start = new((float)(startX * metersPerWorldUnit), (float)startHeightM, (float)(startY * metersPerWorldUnit));
        Vector3 end = new((float)(endX * metersPerWorldUnit), (float)endHeightM, (float)(endY * metersPerWorldUnit));
        double projectileRadiusM = SimulationCombatMath.ProjectileDiameterM(projectile.AmmoType) * 0.5;
        ProjectileObstacleHit? bestHit = null;

        if (runtimeGrid is not null && runtimeGrid.IsValid
            && TryResolveTerrainHit(runtimeGrid, metersPerWorldUnit, projectileRadiusM, start, end, out ProjectileObstacleHit terrainHit))
        {
            bestHit = terrainHit;
        }

        foreach (SimulationEntity entity in world.Entities)
        {
            if (!ShouldTreatAsObstacle(entity, shooter, projectile))
            {
                continue;
            }

            if (!TryResolveEntityHit(world, metersPerWorldUnit, entity, projectileRadiusM, start, end, out ProjectileObstacleHit entityHit))
            {
                continue;
            }

            if (bestHit is null || entityHit.SegmentT < bestHit.Value.SegmentT)
            {
                bestHit = entityHit;
            }
        }

        return bestHit;
    }

    private static bool ShouldTreatAsObstacle(SimulationEntity entity, SimulationEntity shooter, SimulationProjectile projectile)
    {
        if (string.Equals(entity.Id, shooter.Id, StringComparison.OrdinalIgnoreCase)
            || entity.IsSimulationSuppressed)
        {
            return false;
        }

        if (SimulationCombatMath.IsStructure(entity)
            && string.Equals(projectile.PreferredTargetId, entity.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SimulationCombatMath.IsStructure(entity))
        {
            return true;
        }

        if (!entity.IsAlive)
        {
            return false;
        }

        return string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveTerrainHit(
        RuntimeGridData runtimeGrid,
        double metersPerWorldUnit,
        double projectileRadiusM,
        Vector3 start,
        Vector3 end,
        out ProjectileObstacleHit hit)
    {
        hit = default;
        Vector3 segment = end - start;
        float distanceM = segment.Length();
        if (distanceM <= 1e-5f)
        {
            return false;
        }

        int previousCellX = -1;
        int previousCellY = -1;
        float previousTerrainHeight = 0f;
        Vector3 previousSample = start;
        float sampleStepM = Math.Max(0.03f, (float)(projectileRadiusM * 1.35));
        int samples = Math.Clamp((int)MathF.Ceiling(distanceM / sampleStepM), 6, 144);
        double maxWorldX = runtimeGrid.WidthCells * runtimeGrid.CellWidthWorld;
        double maxWorldY = runtimeGrid.HeightCells * runtimeGrid.CellHeightWorld;

        for (int index = 1; index <= samples; index++)
        {
            float t = index / (float)samples;
            Vector3 sample = start + segment * t;
            double sampleWorldX = sample.X / metersPerWorldUnit;
            double sampleWorldY = sample.Z / metersPerWorldUnit;
            if (sampleWorldX < 0.0 || sampleWorldY < 0.0 || sampleWorldX >= maxWorldX || sampleWorldY >= maxWorldY)
            {
                previousSample = sample;
                continue;
            }

            int cellX = Math.Clamp((int)Math.Floor(sampleWorldX / Math.Max(runtimeGrid.CellWidthWorld, 1e-6)), 0, runtimeGrid.WidthCells - 1);
            int cellY = Math.Clamp((int)Math.Floor(sampleWorldY / Math.Max(runtimeGrid.CellHeightWorld, 1e-6)), 0, runtimeGrid.HeightCells - 1);
            int terrainIndex = runtimeGrid.IndexOf(cellX, cellY);
            float terrainHeight = runtimeGrid.HeightMap[terrainIndex];
            float clearance = (float)Math.Max(0.012, projectileRadiusM * 0.7);
            bool hitsFloor = terrainHeight > 0.01f && sample.Y <= terrainHeight + clearance;

            if (runtimeGrid.VisionBlockMap[terrainIndex])
            {
                float visionTop = Math.Max(terrainHeight, runtimeGrid.VisionBlockHeightMap[terrainIndex]);
                if (sample.Y >= terrainHeight - clearance && sample.Y <= visionTop + clearance)
                {
                    Vector3 normal = ResolveBarrierNormal(runtimeGrid, metersPerWorldUnit, sample, previousSample, cellX, cellY);
                    hit = new ProjectileObstacleHit(
                        sampleWorldX,
                        sampleWorldY,
                        sample.Y,
                        normal.X,
                        normal.Y,
                        normal.Z,
                        t,
                        SupportsRicochet: true,
                        Kind: "vision_block");
                    return true;
                }
            }

            if (hitsFloor)
            {
                Vector3 normal = Vector3.UnitY;
                if (previousCellX >= 0 && previousCellY >= 0)
                {
                    float heightDelta = terrainHeight - previousTerrainHeight;
                    if ((cellX != previousCellX || cellY != previousCellY)
                        && heightDelta > 0.045f
                        && sample.Y > previousTerrainHeight + clearance)
                    {
                        normal = ResolveBarrierNormal(runtimeGrid, metersPerWorldUnit, sample, previousSample, cellX, cellY);
                    }
                    else
                    {
                        normal = EstimateTerrainNormal(runtimeGrid, metersPerWorldUnit, cellX, cellY);
                    }
                }

                hit = new ProjectileObstacleHit(
                    sampleWorldX,
                    sampleWorldY,
                    sample.Y,
                    normal.X,
                    normal.Y,
                    normal.Z,
                    t,
                    SupportsRicochet: true,
                    Kind: "terrain");
                return true;
            }

            previousCellX = cellX;
            previousCellY = cellY;
            previousTerrainHeight = terrainHeight;
            previousSample = sample;
        }

        return false;
    }

    private static bool TryResolveEntityHit(
        SimulationWorldState world,
        double metersPerWorldUnit,
        SimulationEntity entity,
        double projectileRadiusM,
        Vector3 start,
        Vector3 end,
        out ProjectileObstacleHit hit)
    {
        hit = default;
        Vector2 start2 = new(start.X, start.Z);
        Vector2 end2 = new(end.X, end.Z);
        Vector2 direction = end2 - start2;
        if (direction.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        Vector2 center2 = new((float)(entity.X * metersPerWorldUnit), (float)(entity.Y * metersPerWorldUnit));
        float yawRad = (float)(entity.AngleDeg * Math.PI / 180.0);
        Vector2 forward = new(MathF.Cos(yawRad), MathF.Sin(yawRad));
        Vector2 right = new(-forward.Y, forward.X);
        Vector2 localStart = new(Vector2.Dot(start2 - center2, forward), Vector2.Dot(start2 - center2, right));
        Vector2 localEnd = new(Vector2.Dot(end2 - center2, forward), Vector2.Dot(end2 - center2, right));
        Vector2 localDelta = localEnd - localStart;

        float halfLengthM;
        float halfWidthM;
        if (SimulationCombatMath.IsStructure(entity))
        {
            float radiusM = (float)Math.Max(0.12, entity.CollisionRadiusWorld * metersPerWorldUnit);
            halfLengthM = radiusM * 0.72f;
            halfWidthM = radiusM * 0.72f;
        }
        else
        {
            halfLengthM = (float)Math.Max(0.06, entity.BodyLengthM * 0.5);
            halfWidthM = (float)Math.Max(0.06, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5);
        }

        float margin = (float)Math.Max(0.010, projectileRadiusM);
        halfLengthM += margin;
        halfWidthM += margin;
        if (!TryIntersectObbSlabs(localStart, localDelta, halfLengthM, halfWidthM, out float t, out Vector2 localNormal))
        {
            return false;
        }

        float sampleHeight = start.Y + (end.Y - start.Y) * t;
        float bottom = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM + entity.StructureBaseLiftM);
        float top = bottom + (float)ResolveObstacleHeightM(entity);
        float verticalMargin = (float)Math.Max(0.015, projectileRadiusM * 0.8);
        if (sampleHeight < bottom - verticalMargin || sampleHeight > top + verticalMargin)
        {
            return false;
        }

        Vector2 hitPoint2 = start2 + direction * t;
        Vector2 normal2 = forward * localNormal.X + right * localNormal.Y;
        if (normal2.LengthSquared() <= 1e-8f)
        {
            normal2 = -Vector2.Normalize(direction);
        }

        normal2 = Vector2.Normalize(normal2);

        hit = new ProjectileObstacleHit(
            hitPoint2.X / (float)metersPerWorldUnit,
            hitPoint2.Y / (float)metersPerWorldUnit,
            sampleHeight,
            normal2.X,
            0f,
            normal2.Y,
            t,
            SupportsRicochet: true,
            Kind: entity.EntityType);
        return true;
    }

    private static bool TryIntersectObbSlabs(
        Vector2 localStart,
        Vector2 localDelta,
        float halfLength,
        float halfWidth,
        out float tEnter,
        out Vector2 normal)
    {
        tEnter = 0f;
        float tExit = 1f;
        normal = Vector2.Zero;
        if (!ClipAxis(localStart.X, localDelta.X, -halfLength, halfLength, new Vector2(-1f, 0f), new Vector2(1f, 0f), ref tEnter, ref tExit, ref normal)
            || !ClipAxis(localStart.Y, localDelta.Y, -halfWidth, halfWidth, new Vector2(0f, -1f), new Vector2(0f, 1f), ref tEnter, ref tExit, ref normal))
        {
            return false;
        }

        return tEnter >= 0.015f && tEnter <= 0.985f && tEnter <= tExit;
    }

    private static bool ClipAxis(
        float origin,
        float delta,
        float min,
        float max,
        Vector2 minNormal,
        Vector2 maxNormal,
        ref float tEnter,
        ref float tExit,
        ref Vector2 normal)
    {
        if (MathF.Abs(delta) <= 1e-7f)
        {
            return origin >= min && origin <= max;
        }

        float t0 = (min - origin) / delta;
        float t1 = (max - origin) / delta;
        Vector2 enterNormal = minNormal;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
            enterNormal = maxNormal;
        }

        if (t0 > tEnter)
        {
            tEnter = t0;
            normal = enterNormal;
        }

        tExit = MathF.Min(tExit, t1);
        return tEnter <= tExit;
    }

    private static float SelectValidT(float t0, float t1)
    {
        const float MinT = 0.015f;
        const float MaxT = 0.985f;
        bool valid0 = t0 >= MinT && t0 <= MaxT;
        bool valid1 = t1 >= MinT && t1 <= MaxT;
        if (valid0 && valid1)
        {
            return MathF.Min(t0, t1);
        }

        if (valid0)
        {
            return t0;
        }

        if (valid1)
        {
            return t1;
        }

        return -1f;
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

    private static Vector3 EstimateTerrainNormal(RuntimeGridData runtimeGrid, double metersPerWorldUnit, int cellX, int cellY)
    {
        float center = runtimeGrid.HeightMap[runtimeGrid.IndexOf(cellX, cellY)];
        float left = runtimeGrid.HeightMap[runtimeGrid.IndexOf(Math.Max(0, cellX - 1), cellY)];
        float right = runtimeGrid.HeightMap[runtimeGrid.IndexOf(Math.Min(runtimeGrid.WidthCells - 1, cellX + 1), cellY)];
        float up = runtimeGrid.HeightMap[runtimeGrid.IndexOf(cellX, Math.Max(0, cellY - 1))];
        float down = runtimeGrid.HeightMap[runtimeGrid.IndexOf(cellX, Math.Min(runtimeGrid.HeightCells - 1, cellY + 1))];
        float dxM = (float)Math.Max(0.01, runtimeGrid.CellWidthWorld * metersPerWorldUnit);
        float dyM = (float)Math.Max(0.01, runtimeGrid.CellHeightWorld * metersPerWorldUnit);
        float slopeX = (right - left) / (2f * dxM);
        float slopeY = (down - up) / (2f * dyM);
        Vector3 normal = new(-slopeX, 1f, -slopeY);
        if (normal.LengthSquared() <= 1e-8f || center <= 0.005f)
        {
            return Vector3.UnitY;
        }

        return Vector3.Normalize(normal);
    }

    private static Vector3 ResolveBarrierNormal(
        RuntimeGridData runtimeGrid,
        double metersPerWorldUnit,
        Vector3 sample,
        Vector3 previousSample,
        int cellX,
        int cellY)
    {
        float cellMinXM = (float)(cellX * runtimeGrid.CellWidthWorld * metersPerWorldUnit);
        float cellMaxXM = (float)((cellX + 1) * runtimeGrid.CellWidthWorld * metersPerWorldUnit);
        float cellMinYM = (float)(cellY * runtimeGrid.CellHeightWorld * metersPerWorldUnit);
        float cellMaxYM = (float)((cellY + 1) * runtimeGrid.CellHeightWorld * metersPerWorldUnit);
        float distLeft = MathF.Abs(sample.X - cellMinXM);
        float distRight = MathF.Abs(cellMaxXM - sample.X);
        float distTop = MathF.Abs(sample.Z - cellMinYM);
        float distBottom = MathF.Abs(cellMaxYM - sample.Z);

        Vector3 normal = distLeft <= distRight && distLeft <= distTop && distLeft <= distBottom
            ? new Vector3(-1f, 0f, 0f)
            : distRight <= distTop && distRight <= distBottom
                ? new Vector3(1f, 0f, 0f)
                : distTop <= distBottom
                    ? new Vector3(0f, 0f, -1f)
                    : new Vector3(0f, 0f, 1f);

        Vector3 travel = sample - previousSample;
        if (travel.LengthSquared() > 1e-8f && Vector3.Dot(normal, travel) > 0f)
        {
            normal = -normal;
        }

        return normal;
    }
}
