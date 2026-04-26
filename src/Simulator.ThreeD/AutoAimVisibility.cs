using System.Numerics;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal static class AutoAimVisibility
{
    private const double PlateVisibilityCacheBucketSec = 0.075;
    private const double ApproximateVisibilityCacheBucketSec = 0.10;
    private const int MaxVisibilityCacheEntries = 4096;
    private static readonly Dictionary<VisibilityCacheKey, bool> VisibilityCache = new();
    private static readonly List<VisibilityCacheKey> VisibilityCachePruneScratch = new(512);

    public static bool CanSeePlate(
        SimulationWorldState world,
        RuntimeGridData? runtimeGrid,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        VisibilityCacheKey cacheKey = VisibilityCacheKey.ForPlate(
            world.GameTimeSec,
            PlateVisibilityCacheBucketSec,
            metersPerWorldUnit,
            shooter,
            target,
            plate);
        if (TryGetCachedVisibility(cacheKey, out bool cachedVisible))
        {
            return cachedVisible;
        }

        bool visible = ComputeCanSeePlate(world, runtimeGrid, shooter, target, plate, metersPerWorldUnit);
        StoreCachedVisibility(cacheKey, visible);
        return visible;
    }

    private static bool ComputeCanSeePlate(
        SimulationWorldState world,
        RuntimeGridData? runtimeGrid,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double metersPerWorldUnit)
    {
        (double muzzleX, double muzzleY, double muzzleHeightM) = SimulationCombatMath.ComputeMuzzlePoint(world, shooter);
        Vector3 start = new(
            (float)(muzzleX * metersPerWorldUnit),
            (float)muzzleHeightM,
            (float)(muzzleY * metersPerWorldUnit));

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
        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            float ringHalfWidthM = (float)Math.Max(
                halfSideM,
                Math.Max(plate.WidthM, plate.HeightSpanM) * 0.48);
            return CanSeeSample(center)
                || CanSeeSample(center + side * ringHalfWidthM)
                || CanSeeSample(center - side * ringHalfWidthM)
                || CanSeeSample(center + vertical * ringHalfWidthM)
                || CanSeeSample(center - vertical * ringHalfWidthM);
        }

        if (!IsPlateFaceVisibleToShooter(target, plate, start, center))
        {
            return false;
        }

        if (CanSeeSample(center)
            || CanSeeSample(center + side * halfSideM)
            || CanSeeSample(center - side * halfSideM)
            || CanSeeSample(center + vertical * halfSideM)
            || CanSeeSample(center - vertical * halfSideM))
        {
            return true;
        }

        if (!SimulationCombatMath.IsStructure(target)
            && !string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CanSeeSample(center + side * halfSideM + vertical * halfSideM)
            || CanSeeSample(center + side * halfSideM - vertical * halfSideM)
            || CanSeeSample(center - side * halfSideM + vertical * halfSideM)
            || CanSeeSample(center - side * halfSideM - vertical * halfSideM);

        bool CanSeeSample(Vector3 sample)
        {
            return IsSampleInsideFirstPersonFov(shooter, start, sample)
                && !IsTerrainOccluding(runtimeGrid, metersPerWorldUnit, start, sample)
                && !IsTargetBodyOccludingPlate(metersPerWorldUnit, target, plate, start, sample)
                && !IsEntityOccluding(world, metersPerWorldUnit, shooter, target, start, sample);
        }
    }

    public static bool CanSeeApproximatePoint(
        SimulationWorldState world,
        RuntimeGridData? runtimeGrid,
        SimulationEntity shooter,
        SimulationEntity target,
        double worldX,
        double worldY,
        double heightM)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        VisibilityCacheKey cacheKey = VisibilityCacheKey.ForApproximatePoint(
            world.GameTimeSec,
            ApproximateVisibilityCacheBucketSec,
            metersPerWorldUnit,
            shooter,
            target,
            worldX,
            worldY,
            heightM);
        if (TryGetCachedVisibility(cacheKey, out bool cachedVisible))
        {
            return cachedVisible;
        }

        (double muzzleX, double muzzleY, double muzzleHeightM) = SimulationCombatMath.ComputeMuzzlePoint(world, shooter);
        Vector3 start = new(
            (float)(muzzleX * metersPerWorldUnit),
            (float)muzzleHeightM,
            (float)(muzzleY * metersPerWorldUnit));
        Vector3 sample = new(
            (float)(worldX * metersPerWorldUnit),
            (float)heightM,
            (float)(worldY * metersPerWorldUnit));
        bool visible = IsSampleInsideFirstPersonFov(shooter, start, sample)
            && !IsTerrainOccluding(runtimeGrid, metersPerWorldUnit, start, sample)
            && !IsEntityOccluding(world, metersPerWorldUnit, shooter, target, start, sample);
        StoreCachedVisibility(cacheKey, visible);
        return visible;
    }

    private static bool IsSampleInsideFirstPersonFov(SimulationEntity shooter, Vector3 start, Vector3 sample)
    {
        if (SimulationCombatMath.IsHeroLobAutoAimMode(shooter)
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
        return yawError <= 66.0 && pitchError <= 50.0;
    }

    private static bool IsTerrainOccluding(RuntimeGridData? runtimeGrid, double metersPerWorldUnit, Vector3 start, Vector3 end)
    {
        if (runtimeGrid is null || !runtimeGrid.IsValid)
        {
            return false;
        }

        if (runtimeGrid.CollisionSurface is not null)
        {
            return IsCollisionSurfaceOccluding(runtimeGrid, metersPerWorldUnit, start, end);
        }

        Vector3 segment = end - start;
        float distanceM = segment.Length();
        if (distanceM <= 0.08f)
        {
            return false;
        }

        int samples = Math.Clamp((int)MathF.Ceiling(distanceM / 0.14f), 6, 96);
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

    private static bool IsCollisionSurfaceOccluding(RuntimeGridData runtimeGrid, double metersPerWorldUnit, Vector3 start, Vector3 end)
    {
        Vector3 segment = end - start;
        float distanceM = segment.Length();
        if (distanceM <= 0.08f)
        {
            return false;
        }

        int samples = Math.Clamp((int)MathF.Ceiling(distanceM / 0.32f), 5, 48);
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
            if (!runtimeGrid.TrySampleCollisionSurface(sampleWorldX, sampleWorldY, out TerrainSurfaceSample terrain))
            {
                continue;
            }

            if (terrain.HeightM > 0.025f && sample.Y <= terrain.HeightM + 0.025f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPlateFaceVisibleToShooter(SimulationEntity target, ArmorPlateTarget plate, Vector3 start, Vector3 plateCenter)
    {
        if ((!string.Equals(target.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
            || plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Vector3 toShooter = start - plateCenter;
        if (toShooter.LengthSquared() <= 1e-8f)
        {
            return true;
        }

        Vector3 normal = SimulationCombatMath.ResolveArmorPlateNormal(plate);
        if (normal.LengthSquared() <= 1e-8f)
        {
            return true;
        }

        float facing = Vector3.Dot(Vector3.Normalize(toShooter), Vector3.Normalize(normal));
        return facing >= 0.20f;
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
        if (SimulationCombatMath.IsLegacyMechanismCollisionSuppressed(entity))
        {
            return false;
        }

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

        // 目标自身不会进入通用遮挡列表，这里额外检查车体核心，避免透过底盘锁到背面装甲。
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
            float radius = ResolveStructureVisionBlockRadiusM(entity, metersPerWorldUnit);
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
                ? ResolveStructureVisionBlockHeightM(entity)
                : entity.BodyClearanceM + entity.BodyHeightM + entity.GimbalBodyHeightM * 0.55);
        return sampleHeight >= bottom && sampleHeight <= top;
    }

    private static float ResolveStructureVisionBlockRadiusM(SimulationEntity structure, double metersPerWorldUnit)
    {
        float collisionRadius = (float)Math.Max(0.0, structure.CollisionRadiusWorld * metersPerWorldUnit);
        return structure.EntityType switch
        {
            "base" => Math.Max(0.72f, collisionRadius),
            "outpost" => Math.Max(0.52f, collisionRadius),
            "energy_mechanism" => Math.Max(1.45f, collisionRadius),
            _ => Math.Max(0.34f, collisionRadius),
        };
    }

    private static float ResolveStructureVisionBlockHeightM(SimulationEntity structure)
        => structure.EntityType switch
        {
            "base" => Math.Max(1.10f, (float)structure.BodyHeightM + 0.55f),
            "outpost" => Math.Max(1.65f, (float)structure.BodyHeightM + 1.00f),
            "energy_mechanism" => Math.Max(2.35f, (float)structure.BodyHeightM + 1.80f),
            _ => Math.Max(0.80f, (float)structure.BodyHeightM),
        };

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

    private static bool TryGetCachedVisibility(VisibilityCacheKey key, out bool visible)
        => VisibilityCache.TryGetValue(key, out visible);

    private static void StoreCachedVisibility(VisibilityCacheKey key, bool visible)
    {
        if (VisibilityCache.Count > MaxVisibilityCacheEntries)
        {
            int minLiveBucket = key.TimeBucket - 2;
            VisibilityCachePruneScratch.Clear();
            foreach (VisibilityCacheKey candidate in VisibilityCache.Keys)
            {
                if (candidate.TimeBucket < minLiveBucket)
                {
                    VisibilityCachePruneScratch.Add(candidate);
                }
            }

            foreach (VisibilityCacheKey oldKey in VisibilityCachePruneScratch)
            {
                VisibilityCache.Remove(oldKey);
            }

            if (VisibilityCache.Count > MaxVisibilityCacheEntries)
            {
                VisibilityCache.Clear();
            }
        }

        VisibilityCache[key] = visible;
    }

    private readonly record struct VisibilityCacheKey(
        byte Kind,
        int TimeBucket,
        string ShooterId,
        string TargetId,
        string PlateOrPointId,
        int ShooterXM,
        int ShooterYM,
        int ShooterYawHalfDeg,
        int ShooterPitchHalfDeg,
        int TargetXM,
        int TargetYM,
        int TargetHeightCm)
    {
        public static VisibilityCacheKey ForPlate(
            double gameTimeSec,
            double bucketSec,
            double metersPerWorldUnit,
            SimulationEntity shooter,
            SimulationEntity target,
            ArmorPlateTarget plate)
        {
            return new VisibilityCacheKey(
                0,
                QuantizeTime(gameTimeSec, bucketSec),
                shooter.Id,
                target.Id,
                plate.Id,
                QuantizeMeters(shooter.X * metersPerWorldUnit, 10.0),
                QuantizeMeters(shooter.Y * metersPerWorldUnit, 10.0),
                QuantizeDegrees(shooter.TurretYawDeg, 2.0),
                QuantizeDegrees(shooter.GimbalPitchDeg, 2.0),
                QuantizeMeters(plate.X * metersPerWorldUnit, 20.0),
                QuantizeMeters(plate.Y * metersPerWorldUnit, 20.0),
                QuantizeMeters(plate.HeightM, 100.0));
        }

        public static VisibilityCacheKey ForApproximatePoint(
            double gameTimeSec,
            double bucketSec,
            double metersPerWorldUnit,
            SimulationEntity shooter,
            SimulationEntity target,
            double worldX,
            double worldY,
            double heightM)
        {
            return new VisibilityCacheKey(
                1,
                QuantizeTime(gameTimeSec, bucketSec),
                shooter.Id,
                target.Id,
                "point",
                QuantizeMeters(shooter.X * metersPerWorldUnit, 10.0),
                QuantizeMeters(shooter.Y * metersPerWorldUnit, 10.0),
                QuantizeDegrees(shooter.TurretYawDeg, 2.0),
                QuantizeDegrees(shooter.GimbalPitchDeg, 2.0),
                QuantizeMeters(worldX * metersPerWorldUnit, 20.0),
                QuantizeMeters(worldY * metersPerWorldUnit, 20.0),
                QuantizeMeters(heightM, 100.0));
        }

        private static int QuantizeTime(double value, double bucketSec)
            => (int)Math.Floor(value / Math.Max(1e-6, bucketSec));

        private static int QuantizeMeters(double value, double scale)
            => (int)Math.Round(value * scale);

        private static int QuantizeDegrees(double value, double scale)
            => (int)Math.Round(SimulationCombatMath.NormalizeDeg(value) * scale);
    }
}
