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

            if (!ProjectileCollisionBroadphase.MayIntersectObstacleBounds(
                    entity,
                    metersPerWorldUnit,
                    projectileRadiusM,
                    start,
                    end))
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
            || entity.IsSimulationSuppressed
            || SimulationCombatMath.IsLegacyMechanismCollisionSuppressed(entity))
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
        if (runtimeGrid.TryRaycastCollisionSurface(start, end, metersPerWorldUnit, projectileRadiusM, out TerrainSurfaceRayHit meshHit))
        {
            hit = new ProjectileObstacleHit(
                meshHit.WorldX,
                meshHit.WorldY,
                meshHit.HeightM,
                meshHit.Normal.X,
                meshHit.Normal.Y,
                meshHit.Normal.Z,
                meshHit.SegmentT,
                SupportsRicochet: true,
                Kind: meshHit.Kind);
            return true;
        }

        if (runtimeGrid.CollisionSurface is not null)
        {
            return false;
        }

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
            float terrainHeight = runtimeGrid.SampleCollisionHeightWithFacets(sampleWorldX, sampleWorldY);
            float clearance = (float)Math.Max(0.012, projectileRadiusM * 0.7);
            bool hitsFloor = sample.Y <= terrainHeight + clearance;

            if (runtimeGrid.VisionBlockMap[terrainIndex] || runtimeGrid.MovementBlockMap[terrainIndex])
            {
                float blockTop = runtimeGrid.VisionBlockMap[terrainIndex]
                    ? Math.Max(terrainHeight, runtimeGrid.VisionBlockHeightMap[terrainIndex])
                    : Math.Max(terrainHeight + 1.20f, runtimeGrid.VisionBlockHeightMap[terrainIndex]);
                if (sample.Y >= terrainHeight - clearance && sample.Y <= blockTop + clearance)
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
                        Kind: runtimeGrid.VisionBlockMap[terrainIndex] ? "vision_block" : "movement_block");
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

        return TryResolveRobotModelHit(metersPerWorldUnit, entity, projectileRadiusM, start, end, profileResolver, out hit);
    }

    private static bool TryResolveRobotModelHit(
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
        ResolveChassisAxes(
            entity.AngleDeg,
            entity.ChassisPitchDeg,
            entity.ChassisRollDeg,
            out Vector3 forward,
            out Vector3 right,
            out Vector3 up);

        Vector3 center = new(
            (float)(entity.X * metersPerWorldUnit),
            (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM),
            (float)(entity.Y * metersPerWorldUnit));
        float bodyLength = Math.Max(0.12f, (float)(profile?.BodyLengthM ?? entity.BodyLengthM));
        float bodyWidth = Math.Max(0.10f, (float)((profile?.BodyWidthM ?? entity.BodyWidthM) * Math.Max(0.4, profile?.BodyRenderWidthScale ?? entity.BodyRenderWidthScale)));
        float bodyHeight = Math.Max(0.08f, (float)(profile?.BodyHeightM ?? entity.BodyHeightM));
        float bodyBase = Math.Max(0f, (float)(profile?.BodyClearanceM ?? entity.BodyClearanceM) + ResolveRuntimeBodyLiftM(entity));
        string bodyShape = profile?.BodyShape ?? entity.BodyShape ?? string.Empty;

        Vector3 bodyCenter = center + up * (bodyBase + bodyHeight * 0.5f);
        if (string.Equals(bodyShape, "octagon", StringComparison.OrdinalIgnoreCase))
        {
            TryStoreBest(TryIntersectCylinderDisc(start, end, bodyCenter, up, forward, Math.Max(bodyLength, bodyWidth) * 0.48f, bodyHeight * 0.50f, projectileRadiusM, entity.EntityType), ref best);
        }
        else
        {
            TryStoreBest(TryIntersectOrientedBox(start, end, bodyCenter, forward, right, up, bodyLength, bodyWidth, bodyHeight, projectileRadiusM, entity.EntityType), ref best);
        }

        float capHeight = Math.Max(0.015f, bodyHeight * 0.12f);
        TryStoreBest(TryIntersectOrientedBox(
            start,
            end,
            center + up * (bodyBase + bodyHeight * 0.72f + capHeight * 0.5f),
            forward,
            right,
            up,
            bodyLength * 0.80f,
            bodyWidth * 0.80f,
            capHeight,
            projectileRadiusM,
            entity.EntityType), ref best);

        IReadOnlyList<(double X, double Y)> wheelOffsets = entity.WheelOffsetsM;
        if (wheelOffsets.Count == 0)
        {
            double halfLength = Math.Max(0.10, bodyLength * 0.44);
            double halfWidth = Math.Max(0.08, bodyWidth * 0.54);
            wheelOffsets = new[] { (halfLength, halfWidth), (halfLength, -halfWidth), (-halfLength, halfWidth), (-halfLength, -halfWidth) };
        }

        float wheelRadius = Math.Max(0.035f, (float)(profile?.WheelRadiusM ?? entity.WheelRadiusM));
        float wheelHalfThickness = Math.Max(0.012f, Math.Min(0.045f, wheelRadius * 0.32f));
        foreach ((double localForward, double localRight) in wheelOffsets)
        {
            Vector3 wheelCenter = center
                + forward * (float)localForward
                + right * (float)localRight
                + up * Math.Max(wheelRadius, (float)(entity.AirborneHeightM > 1e-4 ? wheelRadius : wheelRadius));
            TryStoreBest(TryIntersectCylinderDisc(start, end, wheelCenter, right, forward, wheelRadius, wheelHalfThickness, projectileRadiusM, entity.EntityType), ref best);
        }

        float gimbalLength = Math.Max(0f, (float)(profile?.GimbalLengthM ?? entity.GimbalLengthM));
        float gimbalWidth = Math.Max(0f, (float)((profile?.GimbalWidthM ?? entity.GimbalWidthM) * Math.Max(0.4, profile?.BodyRenderWidthScale ?? entity.BodyRenderWidthScale)));
        float gimbalHeight = Math.Max(0f, (float)(profile?.GimbalBodyHeightM ?? entity.GimbalBodyHeightM));
        if (gimbalLength > 0.04f && gimbalWidth > 0.04f && gimbalHeight > 0.02f)
        {
            float gimbalOffsetX = (float)(profile?.GimbalOffsetXM ?? entity.GimbalOffsetXM);
            float gimbalOffsetY = (float)(profile?.GimbalOffsetYM ?? entity.GimbalOffsetYM);
            float mountGap = (float)(profile?.GimbalMountGapM ?? entity.GimbalMountGapM);
            float mountHeightOnly = (float)(profile?.GimbalMountHeightM ?? entity.GimbalMountHeightM);
            float bodyTop = bodyBase + bodyHeight;
            if (mountGap + mountHeightOnly > 0.02f)
            {
                float mountHeight = Math.Max(0.02f, mountGap + mountHeightOnly + 0.08f);
                Vector3 mountCenter = center
                    + forward * gimbalOffsetX
                    + right * gimbalOffsetY
                    + up * (bodyTop + mountHeight * 0.5f);
                TryStoreBest(TryIntersectOrientedBox(
                    start,
                    end,
                    mountCenter,
                    forward,
                    right,
                    up,
                    Math.Max(0.02f, (float)(profile?.GimbalMountLengthM ?? entity.GimbalMountLengthM)),
                    Math.Max(0.02f, (float)((profile?.GimbalMountWidthM ?? entity.GimbalMountWidthM) * Math.Max(0.4, profile?.BodyRenderWidthScale ?? entity.BodyRenderWidthScale))),
                    mountHeight,
                    projectileRadiusM,
                    entity.EntityType), ref best);
            }

            float turretBase = Math.Max(
                bodyTop + mountGap + mountHeightOnly,
                (float)(profile?.GimbalHeightM ?? entity.GimbalHeightM) - gimbalHeight * 0.5f);
            Vector3 hingeCenter = center + forward * gimbalOffsetX + right * gimbalOffsetY + up * turretBase;
            ResolveMountedTurretAxes(
                forward,
                right,
                up,
                (float)((entity.TurretYawDeg - entity.AngleDeg) * Math.PI / 180.0),
                (float)(entity.GimbalPitchDeg * Math.PI / 180.0),
                out _,
                out Vector3 turretRight,
                out Vector3 pitchedForward,
                out Vector3 pitchedUp);
            Vector3 turretCenter = hingeCenter
                + pitchedUp * (gimbalHeight * 0.50f + 0.006f)
                + pitchedForward * (gimbalLength * 0.04f);
            TryStoreBest(TryIntersectCylinderDisc(start, end, hingeCenter, turretRight, up, Math.Max(0.014f, gimbalHeight * 0.18f), Math.Max(0.018f, gimbalWidth * 0.55f), projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectOrientedBox(start, end, turretCenter, pitchedForward, turretRight, pitchedUp, gimbalLength, gimbalWidth, gimbalHeight, projectileRadiusM, entity.EntityType), ref best);

            float barrelLength = Math.Max(0f, (float)(profile?.BarrelLengthM ?? entity.BarrelLengthM));
            float barrelRadius = Math.Max(0f, (float)(profile?.BarrelRadiusM ?? entity.BarrelRadiusM));
            if (barrelLength > 0.04f && barrelRadius > 0.004f)
            {
                Vector3 pivot = turretCenter
                    + pitchedForward * (gimbalLength * 0.5f + barrelRadius * 0.45f)
                    + pitchedUp * (gimbalHeight * 0.12f - 0.03f);
                Vector3 barrelCenter = pivot + pitchedForward * (barrelLength * 0.5f);
                TryStoreBest(TryIntersectCylinderDisc(start, end, barrelCenter, pitchedForward, turretRight, barrelRadius, barrelLength * 0.5f, projectileRadiusM, entity.EntityType), ref best);
            }
        }

        if (best is not ProjectileObstacleHit robotHit)
        {
            return false;
        }

        hit = robotHit with
        {
            X = robotHit.X / Math.Max(metersPerWorldUnit, 1e-6),
            Y = robotHit.Y / Math.Max(metersPerWorldUnit, 1e-6),
        };
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
        if (string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            foreach (ArmorPlateTarget plate in SimulationCombatMath.GetEnergyMechanismTargets(entity, metersPerWorldUnit, world.GameTimeSec))
            {
                Vector3 centerPoint = new(
                    (float)(plate.X * metersPerWorldUnit),
                    (float)plate.HeightM,
                    (float)(plate.Y * metersPerWorldUnit));
                Vector3 normal = SimulationCombatMath.ResolveArmorPlateNormal(plate);
                Vector3 upAxis = Math.Abs(Vector3.Dot(normal, Vector3.UnitY)) >= 0.94f ? Vector3.UnitX : Vector3.UnitY;
                float diskRadius = (float)Math.Max(0.06, Math.Max(plate.WidthM, plate.HeightSpanM) * 0.5);
                float diskThickness = (float)Math.Max(0.008, diskRadius * 0.08);
                TryStoreBest(
                    TryIntersectCylinderDisc(
                        start,
                        end,
                        centerPoint,
                        normal,
                        upAxis,
                        diskRadius,
                        diskThickness,
                        projectileRadiusM,
                        entity.EntityType),
                    ref best);
            }

            if (best is ProjectileObstacleHit energyHit)
            {
                hit = ConvertMetricHitToWorld(energyHit, metersPerWorldUnit);
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
            RobotAppearanceProfile? profile = profileResolver?.Invoke(entity);
            float baseLiftM = Math.Clamp((float)(entity.StructureBaseLiftM > 1e-6 ? entity.StructureBaseLiftM : profile?.StructureBaseLiftM ?? 0.40f), 0f, 1.20f);
            if (baseLiftM <= 1e-6f)
            {
                baseLiftM = 0.40f;
            }

            center += up * baseLiftM;
            float baseWidth = (float)Math.Clamp(entity.BodyLengthM, 0.40, 0.95);
            float towerHeight = (float)Math.Clamp(entity.BodyHeightM, 1.00, 2.40);
            float bodyTopHeight = Math.Clamp(profile?.StructureBodyTopHeightM ?? 1.216f, 0.12f, towerHeight);
            float lowerShoulderHeight = Math.Clamp(profile?.StructureLowerShoulderHeightM ?? 0.571f, 0.08f, bodyTopHeight);
            float upperShoulderHeight = Math.Clamp(profile?.StructureUpperShoulderHeightM ?? 1.446f, bodyTopHeight, towerHeight);
            float headBaseHeight = Math.Clamp(profile?.StructureHeadBaseHeightM ?? 1.318f, upperShoulderHeight, towerHeight + 0.10f);
            float towerRadius = Math.Clamp(profile?.StructureTowerRadiusM ?? (float)(entity.BodyWidthM * 0.36), 0.12f, 0.34f);
            TryStoreBest(TryIntersectTaperedRectFrustum(start, end, center, forward, right, up, baseWidth, baseWidth, baseWidth, baseWidth, 0f, 0.085f, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectTaperedRectFrustum(start, end, center, forward, right, up, baseWidth * 0.92f, baseWidth * 0.92f, 0.51f, 0.51f, 0.085f, lowerShoulderHeight, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectTaperedRectFrustum(start, end, center, forward, right, up, 0.41f, 0.41f, 0.35f, 0.35f, lowerShoulderHeight, bodyTopHeight, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectTaperedRectFrustum(start, end, center, forward, right, up, 0.44f, 0.44f, 0.33f, 0.33f, bodyTopHeight, upperShoulderHeight, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectTaperedRectFrustum(start, end, center, forward, right, up, 0.33f, 0.33f, 0.24f, 0.24f, upperShoulderHeight, headBaseHeight, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectOrientedBox(start, end, center + forward * 0.03f + up * Math.Min(towerHeight - 0.08f, headBaseHeight + 0.16f), forward, right, up, 0.21f, 0.18f, 0.12f, projectileRadiusM, entity.EntityType), ref best);
            float ringYawRad = (float)(SimulationCombatMath.ResolveOutpostRingYawDeg(entity, world.GameTimeSec) * Math.PI / 180.0);
            TryStoreBest(TryIntersectCylinderDisc(start, end, center + up * (headBaseHeight - 0.07f), up, new Vector3(MathF.Cos(ringYawRad), 0f, MathF.Sin(ringYawRad)), towerRadius + 0.055f, 0.030f, projectileRadiusM, entity.EntityType), ref best);
        }
        else if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            RobotAppearanceProfile? profile = profileResolver?.Invoke(entity);
            float length = (float)Math.Clamp(entity.BodyLengthM, 1.10, 2.35);
            float width = (float)Math.Clamp(entity.BodyWidthM * entity.BodyRenderWidthScale, 0.90, 2.05);
            float height = (float)Math.Clamp(entity.BodyHeightM, 0.70, 1.60);
            float roofHeight = Math.Clamp(profile?.StructureRoofHeightM ?? height * 0.87f, 0.45f, height - 0.02f);
            float slabHeight = Math.Min(0.20f, height * 0.22f);
            float shoulderHeight = Math.Clamp(profile?.StructureShoulderHeightM ?? height * 0.728f, slabHeight + 0.12f, roofHeight - 0.05f);
            TryStoreBest(TryIntersectTaperedRectFrustum(start, end, center, forward, right, up, length, width, length * 0.96f, width * 0.94f, 0f, slabHeight, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectTaperedRectFrustum(start, end, center, forward, right, up, length * 0.90f, width * 0.88f, length * 0.62f, width * 0.56f, slabHeight, shoulderHeight, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectTaperedRectFrustum(start, end, center, forward, right, up, length * 0.62f, width * 0.56f, length * 0.40f, width * 0.34f, shoulderHeight, roofHeight, projectileRadiusM, entity.EntityType), ref best);
            TryStoreBest(TryIntersectOrientedBox(start, end, center + up * (height * 0.58f), forward, right, up, 0.11f, 0.12f, Math.Min(height * 0.66f, profile?.StructureCoreColumnHeightM ?? 0.783f), projectileRadiusM, entity.EntityType), ref best);
        }

        if (best is ProjectileObstacleHit structureHit)
        {
            hit = ConvertMetricHitToWorld(structureHit, metersPerWorldUnit);
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

    private static ProjectileObstacleHit ConvertMetricHitToWorld(ProjectileObstacleHit hit, double metersPerWorldUnit)
    {
        double safeMeters = Math.Max(metersPerWorldUnit, 1e-6);
        return hit with
        {
            X = hit.X / safeMeters,
            Y = hit.Y / safeMeters,
        };
    }

    private static float ResolveRuntimeBodyLiftM(SimulationEntity entity)
    {
        float bodyLift = 0f;
        if (entity.TraversalActive)
        {
            float progress = (float)Math.Clamp(entity.TraversalProgress, 0.0, 1.0);
            float stepHeight = (float)Math.Clamp(
                Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                0.0,
                0.45);
            if (progress < 0.20f)
            {
                bodyLift = (0.02f + stepHeight * 0.10f) * (progress / 0.20f);
            }
            else if (progress < 0.56f)
            {
                bodyLift = Lerp(0.02f + stepHeight * 0.10f, 0.09f + stepHeight * 0.18f, (progress - 0.20f) / 0.36f);
            }
            else if (progress < 0.84f)
            {
                bodyLift = Lerp(0.09f + stepHeight * 0.18f, 0.05f + stepHeight * 0.08f, (progress - 0.56f) / 0.28f);
            }
            else
            {
                bodyLift = (0.05f + stepHeight * 0.08f) * (1.0f - (progress - 0.84f) / 0.16f);
            }
        }

        if (entity.ChassisSupportsJump && entity.AirborneHeightM > 1e-4)
        {
            bodyLift = MathF.Max(bodyLift, 0.16f * (float)Math.Clamp(entity.AirborneHeightM / 0.70, 0.0, 1.0));
        }

        bodyLift -= ResolveSuspensionVisualCompressionM(entity);
        return bodyLift;
    }

    private static float ResolveSuspensionVisualCompressionM(SimulationEntity entity)
    {
        float compression = (float)Math.Clamp(entity.LandingCompressionM, -0.018, 0.040);
        double duration = Math.Max(1e-6, entity.JumpCrouchDurationSec);
        if (entity.JumpCrouchTimerSec > 1e-6 && duration > 1e-6)
        {
            double progress = Math.Clamp(1.0 - entity.JumpCrouchTimerSec / duration, 0.0, 1.0);
            double eased = progress * progress * (3.0 - 2.0 * progress);
            compression += (float)(0.020 * eased);
        }

        return Math.Clamp(compression, -0.018f, 0.040f);
    }

    private static float Lerp(float start, float end, float t)
        => start + (end - start) * Math.Clamp(t, 0f, 1f);

    private static void ResolveChassisAxes(
        double yawDeg,
        double pitchDeg,
        double rollDeg,
        out Vector3 forward,
        out Vector3 right,
        out Vector3 up)
    {
        float yawRad = (float)(yawDeg * Math.PI / 180.0);
        Vector3 flatForward = new(MathF.Cos(yawRad), 0f, MathF.Sin(yawRad));
        Vector3 flatRight = new(-flatForward.Z, 0f, flatForward.X);
        float pitch = (float)(Math.Clamp(pitchDeg, -32.0, 32.0) * Math.PI / 180.0);
        float roll = (float)(Math.Clamp(rollDeg, -28.0, 28.0) * Math.PI / 180.0);
        forward = SafeNormalize(flatForward * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch), flatForward);
        right = SafeNormalize(flatRight * MathF.Cos(roll) + Vector3.UnitY * MathF.Sin(roll), flatRight);
        up = SafeNormalize(Vector3.Cross(right, forward), Vector3.UnitY);
        right = SafeNormalize(Vector3.Cross(forward, up), flatRight);
    }

    private static void ResolveMountedTurretAxes(
        Vector3 chassisForward,
        Vector3 chassisRight,
        Vector3 chassisUp,
        float localTurretYawRad,
        float gimbalPitchRad,
        out Vector3 turretForward,
        out Vector3 turretRight,
        out Vector3 pitchedForward,
        out Vector3 pitchedUp)
    {
        turretForward = SafeNormalize(
            chassisForward * MathF.Cos(localTurretYawRad)
            + chassisRight * MathF.Sin(localTurretYawRad),
            chassisForward);
        turretRight = SafeNormalize(
            -chassisForward * MathF.Sin(localTurretYawRad)
            + chassisRight * MathF.Cos(localTurretYawRad),
            chassisRight);
        pitchedForward = SafeNormalize(
            turretForward * MathF.Cos(gimbalPitchRad)
            + chassisUp * MathF.Sin(gimbalPitchRad),
            turretForward);
        pitchedUp = SafeNormalize(Vector3.Cross(turretRight, pitchedForward), chassisUp);
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

    private static ProjectileObstacleHit? TryIntersectTaperedRectFrustum(
        Vector3 start,
        Vector3 end,
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float bottomLength,
        float bottomWidth,
        float topLength,
        float topWidth,
        float bottomHeight,
        float topHeight,
        double projectileRadiusM,
        string kind)
    {
        if (topHeight <= bottomHeight + 1e-5f)
        {
            return null;
        }

        Vector3 f = SafeNormalize(forward, Vector3.UnitX);
        Vector3 r = SafeNormalize(right, Vector3.UnitZ);
        Vector3 u = SafeNormalize(up, Vector3.UnitY);
        Vector3 bottomCenter = center + u * bottomHeight;
        Vector3 localStart = new(Vector3.Dot(start - bottomCenter, f), Vector3.Dot(start - bottomCenter, r), Vector3.Dot(start - bottomCenter, u));
        Vector3 localEnd = new(Vector3.Dot(end - bottomCenter, f), Vector3.Dot(end - bottomCenter, r), Vector3.Dot(end - bottomCenter, u));
        Vector3 delta = localEnd - localStart;
        float height = topHeight - bottomHeight;
        float margin = (float)Math.Max(0.006, projectileRadiusM);
        float halfLength0 = Math.Max(0.01f, bottomLength * 0.5f) + margin;
        float halfWidth0 = Math.Max(0.01f, bottomWidth * 0.5f) + margin;
        float lengthSlope = (Math.Max(0.01f, topLength * 0.5f) - Math.Max(0.01f, bottomLength * 0.5f)) / height;
        float widthSlope = (Math.Max(0.01f, topWidth * 0.5f) - Math.Max(0.01f, bottomWidth * 0.5f)) / height;

        float tEnter = 0f;
        float tExit = 1f;
        Vector3 enterNormal = Vector3.Zero;
        if (!ClipHalfSpace(localStart, delta, new Vector3(1f, 0f, -lengthSlope), halfLength0, new Vector3(1f, 0f, -lengthSlope), ref tEnter, ref tExit, ref enterNormal)
            || !ClipHalfSpace(localStart, delta, new Vector3(-1f, 0f, -lengthSlope), halfLength0, new Vector3(-1f, 0f, -lengthSlope), ref tEnter, ref tExit, ref enterNormal)
            || !ClipHalfSpace(localStart, delta, new Vector3(0f, 1f, -widthSlope), halfWidth0, new Vector3(0f, 1f, -widthSlope), ref tEnter, ref tExit, ref enterNormal)
            || !ClipHalfSpace(localStart, delta, new Vector3(0f, -1f, -widthSlope), halfWidth0, new Vector3(0f, -1f, -widthSlope), ref tEnter, ref tExit, ref enterNormal)
            || !ClipHalfSpace(localStart, delta, new Vector3(0f, 0f, -1f), margin, new Vector3(0f, 0f, -1f), ref tEnter, ref tExit, ref enterNormal)
            || !ClipHalfSpace(localStart, delta, new Vector3(0f, 0f, 1f), height + margin, new Vector3(0f, 0f, 1f), ref tEnter, ref tExit, ref enterNormal))
        {
            return null;
        }

        if (tEnter < 0.015f || tEnter > 0.985f || tEnter > tExit)
        {
            return null;
        }

        Vector3 localNormal = SafeNormalize(enterNormal, -SafeNormalize(delta, Vector3.UnitX));
        Vector3 normal = SafeNormalize(f * localNormal.X + r * localNormal.Y + u * localNormal.Z, -SafeNormalize(end - start, Vector3.UnitX));
        Vector3 point = start + (end - start) * tEnter;
        return new ProjectileObstacleHit(point.X, point.Z, point.Y, normal.X, normal.Y, normal.Z, tEnter, SupportsRicochet: true, Kind: kind);
    }

    private static bool ClipHalfSpace(
        Vector3 origin,
        Vector3 delta,
        Vector3 planeNormal,
        float planeOffset,
        Vector3 outwardNormal,
        ref float tEnter,
        ref float tExit,
        ref Vector3 enterNormal)
    {
        float startDistance = Vector3.Dot(planeNormal, origin) - planeOffset;
        float deltaDistance = Vector3.Dot(planeNormal, delta);
        if (MathF.Abs(deltaDistance) <= 1e-7f)
        {
            return startDistance <= 0f;
        }

        float t = -startDistance / deltaDistance;
        if (deltaDistance > 0f)
        {
            tExit = MathF.Min(tExit, t);
        }
        else
        {
            if (t > tEnter)
            {
                tEnter = t;
                enterNormal = outwardNormal;
            }
        }

        return tEnter <= tExit;
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
        if (runtimeGrid.TrySampleFacetCollisionSurface(worldX, worldY, out _, out Vector3 facetNormal, out _))
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
