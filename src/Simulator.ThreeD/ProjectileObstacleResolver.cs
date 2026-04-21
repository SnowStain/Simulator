using System.Numerics;
using System.Linq;
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
        double endHeightM,
        IReadOnlyList<SimulationEntity>? obstacleCandidates = null,
        Func<SimulationEntity, RobotAppearanceProfile>? profileResolver = null)
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

        IReadOnlyList<SimulationEntity> candidates = obstacleCandidates ?? (IReadOnlyList<SimulationEntity>)world.Entities;
        foreach (SimulationEntity entity in candidates)
        {
            if (!ShouldTreatAsObstacle(entity, shooter, projectile))
            {
                continue;
            }

            if (!TryResolveEntityHit(world, metersPerWorldUnit, entity, projectileRadiusM, start, end, profileResolver, out ProjectileObstacleHit entityHit))
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

        return true;
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
            float terrainHeight = runtimeGrid.SampleHeightWithFacets(sampleWorldX, sampleWorldY);
            float clearance = (float)Math.Max(0.012, projectileRadiusM * 0.7);
            bool hitsFloor = sample.Y <= terrainHeight + clearance;

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
                        && heightDelta > 0.049f
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
        Func<SimulationEntity, RobotAppearanceProfile>? profileResolver,
        out ProjectileObstacleHit hit)
    {
        hit = default;
        if (SimulationCombatMath.IsStructure(entity))
        {
            return TryResolveStructureModelHit(world, metersPerWorldUnit, entity, projectileRadiusM, start, end, profileResolver, out hit);
        }

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

        float halfLengthM = (float)Math.Max(0.06, entity.BodyLengthM * 0.5);
        float halfWidthM = (float)Math.Max(0.06, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5);

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

    private static bool TryResolveStructureModelHit(
        SimulationWorldState world,
        double metersPerWorldUnit,
        SimulationEntity entity,
        double projectileRadiusM,
        Vector3 start,
        Vector3 end,
        Func<SimulationEntity, RobotAppearanceProfile>? profileResolver,
        out ProjectileObstacleHit hit)
    {
        hit = default;
        ProjectileObstacleHit? best = null;
        RobotAppearanceProfile? profile = profileResolver?.Invoke(entity);
        if (string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            && profile is not null)
        {
            Vector3 anchor = new((float)(entity.X * metersPerWorldUnit), 0f, (float)(entity.Y * metersPerWorldUnit));
            EnergyRenderMesh mesh = EnergyMechanismGeometry.BuildSingle(
                profile,
                anchor,
                EnergyMechanismGeometry.ResolveAccentColor(entity.Team),
                (float)world.GameTimeSec);
            foreach (EnergyRenderBox box in mesh.Boxes)
            {
                TryStoreBest(TryIntersectOrientedBox(start, end, box.Center, box.Forward, box.Right, box.Up, box.Length, box.Width, box.Height, projectileRadiusM, entity.EntityType), ref best);
            }

            foreach (EnergyRenderCylinder cylinder in mesh.Cylinders)
            {
                TryStoreBest(TryIntersectCylinderDisc(start, end, cylinder.Center, cylinder.NormalAxis, cylinder.UpAxis, cylinder.Radius, cylinder.Thickness, projectileRadiusM, entity.EntityType), ref best);
            }

            foreach (EnergyRenderPrism prism in mesh.Prisms)
            {
                TryStoreBest(TryIntersectPrismAabb(start, end, prism.Bottom, prism.Top, projectileRadiusM, entity.EntityType), ref best);
            }

            if (best is ProjectileObstacleHit energyHit)
            {
                hit = energyHit;
                return true;
            }

            return false;
        }

        Vector3 center = new(
            (float)(entity.X * metersPerWorldUnit),
            (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM + entity.StructureBaseLiftM),
            (float)(entity.Y * metersPerWorldUnit));
        float yaw = (float)(entity.AngleDeg * Math.PI / 180.0);
        Vector3 forward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 right = new(-forward.Z, 0f, forward.X);
        Vector3 up = Vector3.UnitY;
        if (string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            float baseWidth = (float)Math.Clamp(entity.BodyLengthM, 0.40, 0.95);
            float towerHeight = (float)Math.Clamp(entity.BodyHeightM, 1.00, 2.40);
            float towerRadius = (float)Math.Clamp(entity.BodyWidthM * 0.36, 0.12, 0.34);
            TryStoreBest(TryIntersectOrientedBox(start, end, center + up * 0.045f, forward, right, up, baseWidth, baseWidth, 0.09f, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectVerticalCylinder(start, end, center + up * (towerHeight * 0.52f), towerRadius, towerHeight, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectVerticalCylinder(start, end, center + up * (towerHeight * 0.86f), towerRadius + 0.065f, 0.11f, projectileRadiusM, entity.EntityType), ref best);
        }
        else if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            float length = (float)Math.Clamp(entity.BodyLengthM, 1.10, 2.35);
            float width = (float)Math.Clamp(entity.BodyWidthM * entity.BodyRenderWidthScale, 0.90, 2.05);
            float height = (float)Math.Clamp(entity.BodyHeightM, 0.70, 1.60);
            TryStoreBest(TryIntersectOrientedBox(start, end, center + up * (height * 0.42f), forward, right, up, length * 0.92f, width * 0.92f, height * 0.84f, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectOrientedBox(start, end, center + up * (height + 0.06f), forward, right, up, length * 0.42f, width * 0.42f, 0.12f, projectileRadiusM, entity.EntityType), ref best);
        }

        if (best is ProjectileObstacleHit structureHit)
        {
            hit = structureHit;
            return true;
        }

        return false;
    }

    private static void TryStoreBest(ProjectileObstacleHit? candidate, ref ProjectileObstacleHit? best)
    {
        if (candidate is not ProjectileObstacleHit hit)
        {
            return;
        }

        if (best is null || hit.SegmentT < best.Value.SegmentT)
        {
            best = hit;
        }
    }

    private static ProjectileObstacleHit? TryIntersectOrientedBox(
        Vector3 start,
        Vector3 end,
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float length,
        float width,
        float height,
        double projectileRadiusM,
        string kind)
    {
        Vector3 f = SafeNormalize(forward, Vector3.UnitX);
        Vector3 r = SafeNormalize(right, Vector3.UnitZ);
        Vector3 u = SafeNormalize(up, Vector3.UnitY);
        Vector3 localStart = new(Vector3.Dot(start - center, f), Vector3.Dot(start - center, r), Vector3.Dot(start - center, u));
        Vector3 localEnd = new(Vector3.Dot(end - center, f), Vector3.Dot(end - center, r), Vector3.Dot(end - center, u));
        Vector3 delta = localEnd - localStart;
        Vector3 half = new(
            Math.Max(0.006f, length * 0.5f + (float)projectileRadiusM),
            Math.Max(0.006f, width * 0.5f + (float)projectileRadiusM),
            Math.Max(0.006f, height * 0.5f + (float)projectileRadiusM));
        if (!TryIntersectAabbSlabs(localStart, delta, -half, half, out float t, out Vector3 localNormal))
        {
            return null;
        }

        Vector3 normal = SafeNormalize(f * localNormal.X + r * localNormal.Y + u * localNormal.Z, -SafeNormalize(end - start, Vector3.UnitX));
        Vector3 point = start + (end - start) * t;
        return new ProjectileObstacleHit(point.X, point.Z, point.Y, normal.X, normal.Y, normal.Z, t, SupportsRicochet: true, Kind: kind);
    }

    private static ProjectileObstacleHit? TryIntersectVerticalCylinder(
        Vector3 start,
        Vector3 end,
        Vector3 center,
        float radius,
        float height,
        double projectileRadiusM,
        string kind)
    {
        return TryIntersectCylinderDisc(start, end, center, Vector3.UnitY, Vector3.UnitX, radius, height * 0.5f, projectileRadiusM, kind);
    }

    private static ProjectileObstacleHit? TryIntersectCylinderDisc(
        Vector3 start,
        Vector3 end,
        Vector3 center,
        Vector3 axisDirection,
        Vector3 radialHint,
        float radius,
        float halfLength,
        double projectileRadiusM,
        string kind)
    {
        Vector3 axis = SafeNormalize(axisDirection, Vector3.UnitY);
        Vector3 radialA = radialHint - axis * Vector3.Dot(radialHint, axis);
        radialA = SafeNormalize(radialA, Math.Abs(Vector3.Dot(axis, Vector3.UnitY)) > 0.9f ? Vector3.UnitX : Vector3.UnitY);
        Vector3 radialB = SafeNormalize(Vector3.Cross(axis, radialA), Vector3.UnitZ);
        Vector3 localStart = new(Vector3.Dot(start - center, radialA), Vector3.Dot(start - center, radialB), Vector3.Dot(start - center, axis));
        Vector3 localEnd = new(Vector3.Dot(end - center, radialA), Vector3.Dot(end - center, radialB), Vector3.Dot(end - center, axis));
        Vector3 delta = localEnd - localStart;
        float expandedRadius = Math.Max(0.004f, radius + (float)projectileRadiusM);
        float expandedHalf = Math.Max(0.004f, halfLength + (float)projectileRadiusM);
        float bestT = float.PositiveInfinity;
        Vector3 bestNormal = Vector3.Zero;

        float a = delta.X * delta.X + delta.Y * delta.Y;
        float b = 2f * (localStart.X * delta.X + localStart.Y * delta.Y);
        float c = localStart.X * localStart.X + localStart.Y * localStart.Y - expandedRadius * expandedRadius;
        float discriminant = b * b - 4f * a * c;
        if (a > 1e-8f && discriminant >= 0f)
        {
            float sqrt = MathF.Sqrt(discriminant);
            StoreCylinderT((-b - sqrt) / (2f * a), sideNormal: true);
            StoreCylinderT((-b + sqrt) / (2f * a), sideNormal: true);
        }

        if (MathF.Abs(delta.Z) > 1e-8f)
        {
            StoreCylinderT((-expandedHalf - localStart.Z) / delta.Z, sideNormal: false);
            StoreCylinderT((expandedHalf - localStart.Z) / delta.Z, sideNormal: false);
        }

        if (!float.IsFinite(bestT))
        {
            return null;
        }

        Vector3 normal = SafeNormalize(radialA * bestNormal.X + radialB * bestNormal.Y + axis * bestNormal.Z, -SafeNormalize(end - start, Vector3.UnitX));
        Vector3 point = start + (end - start) * bestT;
        return new ProjectileObstacleHit(point.X, point.Z, point.Y, normal.X, normal.Y, normal.Z, bestT, SupportsRicochet: true, Kind: kind);

        void StoreCylinderT(float t, bool sideNormal)
        {
            if (t < 0.015f || t > 0.985f || t >= bestT)
            {
                return;
            }

            Vector3 sample = localStart + delta * t;
            if (MathF.Abs(sample.Z) > expandedHalf + 1e-5f
                || sample.X * sample.X + sample.Y * sample.Y > expandedRadius * expandedRadius + 1e-5f)
            {
                return;
            }

            bestT = t;
            bestNormal = sideNormal
                ? SafeNormalize(new Vector3(sample.X, sample.Y, 0f), Vector3.UnitX)
                : new Vector3(0f, 0f, sample.Z >= 0f ? 1f : -1f);
        }
    }

    private static ProjectileObstacleHit? TryIntersectPrismAabb(
        Vector3 start,
        Vector3 end,
        IReadOnlyList<Vector3> bottom,
        IReadOnlyList<Vector3> top,
        double projectileRadiusM,
        string kind)
    {
        if (bottom.Count == 0 || top.Count == 0)
        {
            return null;
        }

        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (Vector3 point in bottom.Concat(top))
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        Vector3 margin = Vector3.One * (float)Math.Max(0.006, projectileRadiusM);
        if (!TryIntersectAabbSlabs(start, end - start, min - margin, max + margin, out float t, out Vector3 normal))
        {
            return null;
        }

        Vector3 pointHit = start + (end - start) * t;
        normal = SafeNormalize(normal, -SafeNormalize(end - start, Vector3.UnitX));
        return new ProjectileObstacleHit(pointHit.X, pointHit.Z, pointHit.Y, normal.X, normal.Y, normal.Z, t, SupportsRicochet: true, Kind: kind);
    }

    private static bool TryIntersectAabbSlabs(
        Vector3 origin,
        Vector3 delta,
        Vector3 min,
        Vector3 max,
        out float tEnter,
        out Vector3 normal)
    {
        tEnter = 0f;
        float tExit = 1f;
        normal = Vector3.Zero;
        return ClipAxis3(origin.X, delta.X, min.X, max.X, new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), ref tEnter, ref tExit, ref normal)
            && ClipAxis3(origin.Y, delta.Y, min.Y, max.Y, new Vector3(0f, -1f, 0f), new Vector3(0f, 1f, 0f), ref tEnter, ref tExit, ref normal)
            && ClipAxis3(origin.Z, delta.Z, min.Z, max.Z, new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 1f), ref tEnter, ref tExit, ref normal)
            && tEnter >= 0.015f
            && tEnter <= 0.985f
            && tEnter <= tExit;
    }

    private static bool ClipAxis3(
        float origin,
        float delta,
        float min,
        float max,
        Vector3 minNormal,
        Vector3 maxNormal,
        ref float tEnter,
        ref float tExit,
        ref Vector3 normal)
    {
        if (MathF.Abs(delta) <= 1e-7f)
        {
            return origin >= min && origin <= max;
        }

        float t0 = (min - origin) / delta;
        float t1 = (max - origin) / delta;
        Vector3 enterNormal = minNormal;
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

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        return value.LengthSquared() <= 1e-8f ? fallback : Vector3.Normalize(value);
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
        double worldX = (cellX + 0.5) * runtimeGrid.CellWidthWorld;
        double worldY = (cellY + 0.5) * runtimeGrid.CellHeightWorld;
        if (runtimeGrid.TrySampleFacetSurface(worldX, worldY, out _, out Vector3 facetNormal, out _))
        {
            return facetNormal;
        }

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
