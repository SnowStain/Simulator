using System.Numerics;
using System.Drawing.Drawing2D;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const double FineTerrainEnergyDoubleFlashDurationSec = 0.96;
    private FineTerrainEnergyMechanismVisualScene? _fineTerrainEnergyScene;
    private string? _fineTerrainEnergySceneKey;
    private Task<FineTerrainEnergyMechanismVisualScene?>? _fineTerrainEnergySceneLoadTask;
    private string? _fineTerrainEnergySceneLoadingKey;
    private FineTerrainOutpostVisualScene? _fineTerrainOutpostScene;
    private string? _fineTerrainOutpostSceneKey;
    private Task<FineTerrainOutpostVisualScene?>? _fineTerrainOutpostSceneLoadTask;
    private string? _fineTerrainOutpostSceneLoadingKey;
    private FineTerrainBaseVisualScene? _fineTerrainBaseScene;
    private string? _fineTerrainBaseSceneKey;
    private Task<FineTerrainBaseVisualScene?>? _fineTerrainBaseSceneLoadTask;
    private string? _fineTerrainBaseSceneLoadingKey;
    private SimulationWorldState? _fineTerrainRuntimeTargetSyncWorld;
    private double _fineTerrainRuntimeTargetSyncGameTimeSec = double.NaN;
    private FineTerrainEnergyMechanismVisualScene? _fineTerrainRuntimeTargetSyncEnergyScene;
    private FineTerrainOutpostVisualScene? _fineTerrainRuntimeTargetSyncOutpostScene;
    private FineTerrainBaseVisualScene? _fineTerrainRuntimeTargetSyncBaseScene;

    private void SyncFineTerrainRuntimeTargetsIfNeeded()
    {
        FineTerrainEnergyMechanismVisualScene? energyScene = ResolveFineTerrainEnergyScene();
        FineTerrainOutpostVisualScene? outpostScene = ResolveFineTerrainOutpostScene();
        FineTerrainBaseVisualScene? baseScene = ResolveFineTerrainBaseScene();
        SimulationWorldState world = _host.World;
        double gameTimeSec = world.GameTimeSec;
        if (ReferenceEquals(_fineTerrainRuntimeTargetSyncWorld, world)
            && Math.Abs(_fineTerrainRuntimeTargetSyncGameTimeSec - gameTimeSec) <= 1e-6
            && ReferenceEquals(_fineTerrainRuntimeTargetSyncEnergyScene, energyScene)
            && ReferenceEquals(_fineTerrainRuntimeTargetSyncOutpostScene, outpostScene)
            && ReferenceEquals(_fineTerrainRuntimeTargetSyncBaseScene, baseScene))
        {
            return;
        }

        _fineTerrainRuntimeTargetSyncWorld = world;
        _fineTerrainRuntimeTargetSyncGameTimeSec = gameTimeSec;
        _fineTerrainRuntimeTargetSyncEnergyScene = energyScene;
        _fineTerrainRuntimeTargetSyncOutpostScene = outpostScene;
        _fineTerrainRuntimeTargetSyncBaseScene = baseScene;
        SyncFineTerrainEnergyRuntimeTargets();
        SyncFineTerrainOutpostRuntimeTargets();
        SyncFineTerrainBaseRuntimeTargets();
    }

    private void SyncFineTerrainEnergyRuntimeTargets()
    {
        SimulationEntity? mechanism = ResolveFineTerrainEnergyMechanismEntity();
        if (mechanism is null)
        {
            return;
        }

        FineTerrainEnergyMechanismVisualScene? scene = ResolveFineTerrainEnergyScene();
        if (scene is null || scene.Items.Count == 0)
        {
            mechanism.RuntimeEnergyTargetsByTeam = null;
            mechanism.RuntimeEnergyTargetsGameTimeSec = double.NaN;
            mechanism.RuntimeEnergyCompositeTransformsByTeam = null;
            return;
        }

        var targetsByTeam = new Dictionary<string, IReadOnlyList<ArmorPlateTarget>>(StringComparer.OrdinalIgnoreCase);
        var transformsByTeam = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        foreach (FineTerrainEnergyMechanismVisualItem item in scene.Items)
        {
            Matrix4x4 compositeTransform = ResolveFineTerrainCompositeTransform(item);
            transformsByTeam[item.Team] = compositeTransform;

            var targets = new List<ArmorPlateTarget>(item.Units.Count);
            foreach (FineTerrainEnergyMechanismUnitVisualItem unit in item.Units)
            {
                if (unit.Kind != FineTerrainEnergyUnitKind.Ring)
                {
                    continue;
                }

                Vector3 centerModel = Vector3.Transform(unit.LocalCenterModel, compositeTransform);
                (double worldX, double worldY, double heightM) = ModelPointToWorld(centerModel, scene.WorldScale);
                Vector3 normalTipModel = Vector3.Transform(unit.LocalCenterModel + unit.LocalNormalModel, compositeTransform);
                (double normalWorldX, double normalWorldY, double normalHeightM) = ModelPointToWorld(normalTipModel, scene.WorldScale);
                Vector3 worldNormal = new(
                    (float)((normalWorldX - worldX) * _host.World.MetersPerWorldUnit),
                    (float)(normalHeightM - heightM),
                    (float)((normalWorldY - worldY) * _host.World.MetersPerWorldUnit));
                if (worldNormal.LengthSquared() <= 1e-8f)
                {
                    worldNormal = Vector3.UnitX;
                }
                else
                {
                    worldNormal = Vector3.Normalize(worldNormal);
                }

                Vector3 projectedNormal = new(worldNormal.X, 0f, worldNormal.Z);
                double yawDeg = projectedNormal.LengthSquared() <= 1e-8f
                    ? 0.0
                    : SimulationCombatMath.NormalizeDeg(Math.Atan2(projectedNormal.Z, projectedNormal.X) * 180.0 / Math.PI);
                targets.Add(new ArmorPlateTarget(
                    $"energy_{item.Team}_arm_{unit.ArmIndex}_ring_{unit.RingScore}",
                    worldX,
                    worldY,
                    heightM,
                    yawDeg,
                    unit.SideLengthM,
                    unit.WidthM,
                    unit.HeightSpanM,
                    unit.RingScore));
            }

            targetsByTeam[item.Team] = targets
                .OrderBy(target => SimulationCombatMath.TryParseEnergyArmIndex(target.Id, out _, out int armIndex) ? armIndex : int.MaxValue)
                .ThenByDescending(target => target.EnergyRingScore)
                .ToArray();
        }

        mechanism.RuntimeEnergyTargetsByTeam = targetsByTeam;
        mechanism.RuntimeEnergyTargetsGameTimeSec = _host.World.GameTimeSec;
        mechanism.RuntimeEnergyCompositeTransformsByTeam = transformsByTeam;
    }

    private void SyncFineTerrainOutpostRuntimeTargets()
    {
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
            {
                entity.RuntimeOutpostTargets = null;
            }
        }

        FineTerrainOutpostVisualScene? scene = ResolveFineTerrainOutpostScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return;
        }

        foreach (FineTerrainOutpostVisualItem item in scene.Items)
        {
            SimulationEntity? entity = ResolveFineTerrainOutpostEntity(item.Team);
            if (entity is null)
            {
                continue;
            }

            Matrix4x4 transform = ResolveFineTerrainOutpostCompositeTransform(item, entity);
            Vector3 pivotModel = Vector3.Transform(item.PivotModel, transform);
            (double pivotWorldX, double pivotWorldY, _) = ModelPointToWorld(pivotModel, scene.WorldScale);
            List<ArmorPlateTarget> targets = entity.RuntimeOutpostTargets?.ToList() ?? new List<ArmorPlateTarget>(4);
            targets.RemoveAll(candidate =>
                item.Kind == FineTerrainOutpostComponentKind.TopArmor
                    ? string.Equals(candidate.Id, "outpost_top", StringComparison.OrdinalIgnoreCase)
                    : candidate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase));

            foreach (FineTerrainOutpostUnitVisualItem unit in item.Units)
            {
                if (unit.IsLightStrip)
                {
                    continue;
                }

                Vector3 centerModel = Vector3.Transform(unit.LocalCentroidModel, transform);
                (double worldX, double worldY, double heightM) = ModelPointToWorld(centerModel, scene.WorldScale);
                Vector3 normalTipModel = Vector3.Transform(unit.LocalCentroidModel + unit.LocalNormalModel, transform);
                (double normalWorldX, double normalWorldY, double normalHeightM) = ModelPointToWorld(normalTipModel, scene.WorldScale);
                Vector3 worldNormal = new(
                    (float)((normalWorldX - worldX) * _host.World.MetersPerWorldUnit),
                    (float)(normalHeightM - heightM),
                    (float)((normalWorldY - worldY) * _host.World.MetersPerWorldUnit));
                if (worldNormal.LengthSquared() <= 1e-8f)
                {
                    worldNormal = Vector3.UnitX;
                }
                else
                {
                    worldNormal = Vector3.Normalize(worldNormal);
                }

                Vector3 projectedNormal = new(worldNormal.X, 0f, worldNormal.Z);
                double yawDeg = projectedNormal.LengthSquared() <= 1e-8f
                    ? 0.0
                    : SimulationCombatMath.NormalizeDeg(Math.Atan2(projectedNormal.Z, projectedNormal.X) * 180.0 / Math.PI);
                if (unit.PlateId.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase))
                {
                    double radialX = worldX - pivotWorldX;
                    double radialY = worldY - pivotWorldY;
                    if (radialX * radialX + radialY * radialY > 1e-10)
                    {
                        yawDeg = SimulationCombatMath.NormalizeDeg(Math.Atan2(radialY, radialX) * 180.0 / Math.PI);
                    }
                }

                targets.Add(new ArmorPlateTarget(
                    unit.PlateId,
                    worldX,
                    worldY,
                    heightM,
                    yawDeg,
                    unit.SideLengthM,
                    unit.WidthM,
                    unit.HeightSpanM));
            }

            entity.RuntimeOutpostTargets = targets
                .OrderBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private void SyncFineTerrainBaseRuntimeTargets()
    {
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase))
            {
                entity.RuntimeBaseTargets = null;
            }
        }

        FineTerrainBaseVisualScene? scene = ResolveFineTerrainBaseScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return;
        }

        foreach (FineTerrainBaseVisualItem item in scene.Items)
        {
            SimulationEntity? entity = ResolveFineTerrainBaseEntity(item.Team);
            if (entity is null)
            {
                continue;
            }

            Matrix4x4 transform = ResolveFineTerrainBaseCompositeTransform(scene.WorldScale, item, entity, includeSlide: true);
            Vector3 sceneAlignmentOffset = Vector3.Zero;
            List<ArmorPlateTarget> targets = entity.RuntimeBaseTargets?.ToList() ?? new List<ArmorPlateTarget>(2);
            targets.RemoveAll(candidate => string.Equals(candidate.Id, "base_top_slide", StringComparison.OrdinalIgnoreCase));

            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                if (unit.IsLightStrip)
                {
                    continue;
                }

                Vector3 centerModel = Vector3.Transform(unit.LocalCentroidModel, transform);
                (double worldX, double worldY, double heightM) = ModelPointToWorld(centerModel, scene.WorldScale);
                worldX += sceneAlignmentOffset.X / Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
                worldY += sceneAlignmentOffset.Z / Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
                heightM += sceneAlignmentOffset.Y;
                Vector3 normalTipModel = Vector3.Transform(unit.LocalCentroidModel + unit.LocalNormalModel, transform);
                (double normalWorldX, double normalWorldY, double normalHeightM) = ModelPointToWorld(normalTipModel, scene.WorldScale);
                normalWorldX += sceneAlignmentOffset.X / Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
                normalWorldY += sceneAlignmentOffset.Z / Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
                normalHeightM += sceneAlignmentOffset.Y;
                Vector3 worldNormal = new(
                    (float)((normalWorldX - worldX) * _host.World.MetersPerWorldUnit),
                    (float)(normalHeightM - heightM),
                    (float)((normalWorldY - worldY) * _host.World.MetersPerWorldUnit));
                if (worldNormal.LengthSquared() <= 1e-8f)
                {
                    worldNormal = Vector3.UnitX;
                }
                else
                {
                    worldNormal = Vector3.Normalize(worldNormal);
                }

                Vector3 projectedNormal = new(worldNormal.X, 0f, worldNormal.Z);
                double yawDeg = projectedNormal.LengthSquared() <= 1e-8f
                    ? 0.0
                    : SimulationCombatMath.NormalizeDeg(Math.Atan2(projectedNormal.Z, projectedNormal.X) * 180.0 / Math.PI);
                targets.Add(new ArmorPlateTarget(
                    unit.PlateId,
                    worldX,
                    worldY,
                    heightM,
                    yawDeg,
                    unit.SideLengthM,
                    unit.WidthM,
                    unit.HeightSpanM));
            }

            entity.RuntimeBaseTargets = targets
                .OrderBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private bool TryDrawFineTerrainEnergyMechanism(Graphics graphics, FacilityRegion representative, double centerWorldX, double centerWorldY)
    {
        FineTerrainEnergyMechanismVisualScene? scene = ResolveFineTerrainEnergyScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return false;
        }

        bool drewFineBody = false;
        SmoothingMode previousSmoothing = graphics.SmoothingMode;
        PixelOffsetMode previousPixelOffset = graphics.PixelOffsetMode;
        CompositingQuality previousCompositing = graphics.CompositingQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        try
        {
            foreach (FineTerrainEnergyMechanismVisualItem item in scene.Items)
            {
                Matrix4x4 compositeTransform = ResolveFineTerrainCompositeTransform(item);
                Vector3 sceneAlignmentOffset = ResolveFineTerrainEnergySceneAlignmentOffset(
                    scene.WorldScale,
                    centerWorldX,
                    centerWorldY,
                    item);
                drewFineBody |= item.Triangles.Count > 0;
                DrawFineTerrainEnergyTrianglesGdi(graphics, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
                DrawFineTerrainEnergyBodyStripTriangles(graphics, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
                DrawFineTerrainEnergyUnitTriangles(graphics, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
                DrawFineTerrainEnergyInteractionFeedback(graphics, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
            }

            return drewFineBody;
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
            graphics.PixelOffsetMode = previousPixelOffset;
            graphics.CompositingQuality = previousCompositing;
        }
    }

    private bool TryDrawGpuFineTerrainEnergyMechanism(FacilityRegion representative, Color fallbackColor, double centerWorldX, double centerWorldY)
    {
        FineTerrainEnergyMechanismVisualScene? scene = ResolveFineTerrainEnergyScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return false;
        }

        bool drewFineBody = false;
        foreach (FineTerrainEnergyMechanismVisualItem item in scene.Items)
        {
            Matrix4x4 compositeTransform = ResolveFineTerrainCompositeTransform(item);
            Vector3 sceneAlignmentOffset = ResolveFineTerrainEnergySceneAlignmentOffset(
                scene.WorldScale,
                centerWorldX,
                centerWorldY,
                item);
            if (!IsFineTerrainItemPotentiallyVisible(
                    ModelToScenePoint(Vector3.Transform(item.PivotModel, compositeTransform), scene.WorldScale) + sceneAlignmentOffset,
                    2.9f,
                    2.4f))
            {
                continue;
            }

            if (!TryDrawGpuFineTerrainEnergyBody(scene.WorldScale, item, fallbackColor, compositeTransform, sceneAlignmentOffset))
            {
                DrawFineTerrainEnergyTrianglesGpu(scene.WorldScale, item, fallbackColor, compositeTransform, sceneAlignmentOffset);
            }
            drewFineBody |= item.Triangles.Count > 0;

            DrawFineTerrainEnergyBodyStripTriangles(null, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
            DrawFineTerrainEnergyUnitTriangles(null, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
            DrawFineTerrainEnergyInteractionFeedback(null, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
        }
        return drewFineBody;
    }

    private bool TryDrawGpuFineTerrainOutposts()
    {
        FineTerrainOutpostVisualScene? scene = ResolveFineTerrainOutpostScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return false;
        }

        bool drawn = false;
        foreach (FineTerrainOutpostVisualItem item in scene.Items)
        {
            SimulationEntity? entity = ResolveFineTerrainOutpostEntity(item.Team);
            IReadOnlyList<ArmorPlateTarget> plates = Array.Empty<ArmorPlateTarget>();
            Matrix4x4 transform;
            Vector3 sceneAlignmentOffset = Vector3.Zero;
            string? lockedPlateId = null;
            if (entity is not null)
            {
                plates = SimulationCombatMath.GetArmorPlateTargets(
                    entity,
                    _host.World.MetersPerWorldUnit,
                    _host.World.GameTimeSec,
                    includeOutpostTopArmor: true);
                transform = ResolveFineTerrainOutpostCompositeTransform(item, entity);
                sceneAlignmentOffset = ResolveFineTerrainOutpostSceneAlignmentOffset(scene.WorldScale, entity, item, plates);
                lockedPlateId = ResolveLockedPlateIdFor(entity);
            }
            else
            {
                transform = ResolveFineTerrainOutpostCompositeTransform(item, entity);
            }

            float visibleRadius = item.Kind == FineTerrainOutpostComponentKind.RotatingArmor ? 1.35f : 0.95f;
            if (!IsFineTerrainItemPotentiallyVisible(
                    ModelToScenePoint(Vector3.Transform(item.PivotModel, transform), scene.WorldScale) + sceneAlignmentOffset,
                    visibleRadius,
                    1.4f))
            {
                continue;
            }

            if (item.Triangles.Count > 0)
            {
                if (entity is not null && !entity.IsAlive)
                {
                    if (!TryDrawGpuFineTerrainTintedUnitMesh(
                            _fineTerrainOutpostBodyMeshCache,
                            _fineTerrainOutpostSceneKey ?? string.Empty,
                            $"{item.Team}|{item.Name}|dead_body",
                            scene.WorldScale,
                            item.PivotModel,
                            item.Triangles,
                            transform,
                            sceneAlignmentOffset,
                            Color.FromArgb(248, 8, 9, 11),
                            0.92f,
                            Vector3.Zero))
                    {
                        DrawFineTerrainColoredTriangles(
                            null,
                            scene.WorldScale,
                            item.Triangles,
                            transform,
                            sceneAlignmentOffset,
                            Color.FromArgb(248, 8, 9, 11),
                            0.92f);
                    }
                }
                else if (!TryDrawGpuFineTerrainOutpostBody(scene.WorldScale, item, transform, sceneAlignmentOffset))
                {
                    DrawFineTerrainColoredTriangles(
                        null,
                        scene.WorldScale,
                        item.Triangles,
                        transform,
                        sceneAlignmentOffset,
                        null,
                        0f);
                }

                drawn = true;
            }

            foreach (FineTerrainOutpostUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                bool locked = string.Equals(lockedPlateId, unit.PlateId, StringComparison.OrdinalIgnoreCase) && !unit.IsLightStrip;
                float flashIntensity = 0f;
                bool flashing = entity is not null
                    && IsStructurePlateFlashActive(entity.Id, unit.PlateId, out flashIntensity)
                    && unit.IsLightStrip;
                ResolveFineTerrainOutpostUnitTint(item.Team, unit.IsLightStrip, locked, flashing, flashIntensity, entity?.IsAlive ?? true, out Color? tint, out float tintStrength);

                if (TryDrawGpuFineTerrainTintedUnitMesh(
                    _fineTerrainOutpostUnitMeshCache,
                    _fineTerrainOutpostSceneKey ?? string.Empty,
                    $"{item.Team}|{item.Name}|{unit.Name}",
                    scene.WorldScale,
                    item.PivotModel,
                    unit.Triangles,
                    transform,
                    sceneAlignmentOffset,
                    tint,
                    tintStrength,
                    Vector3.Zero))
                {
                    drawn = true;
                    continue;
                }

                DrawFineTerrainColoredTriangles(
                    null,
                    scene.WorldScale,
                    unit.Triangles,
                    transform,
                    sceneAlignmentOffset,
                    tint,
                    tintStrength);
                drawn = true;
            }
        }

        return drawn;
    }

    private bool TryDrawFineTerrainOutposts(Graphics graphics)
    {
        FineTerrainOutpostVisualScene? scene = ResolveFineTerrainOutpostScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return false;
        }

        bool drawn = false;
        foreach (FineTerrainOutpostVisualItem item in scene.Items)
        {
            SimulationEntity? entity = ResolveFineTerrainOutpostEntity(item.Team);
            if (entity is not null)
            {
                continue;
            }

            Matrix4x4 transform = ResolveFineTerrainOutpostCompositeTransform(item, entity);
            if (item.Triangles.Count > 0)
            {
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    item.Triangles,
                    transform,
                    Vector3.Zero,
                    null,
                    0f);
                drawn = true;
            }

            foreach (FineTerrainOutpostUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                ResolveFineTerrainOutpostUnitTint(item.Team, unit.IsLightStrip, locked: false, flashing: false, flashIntensity: 0f, isAlive: true, out Color? tint, out float tintStrength);

                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    unit.Triangles,
                    transform,
                    Vector3.Zero,
                    tint,
                    tintStrength);
                drawn = true;
            }
        }

        return drawn;
    }

    private bool TryDrawGpuFineTerrainBases()
    {
        FineTerrainBaseVisualScene? scene = ResolveFineTerrainBaseScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return false;
        }

        bool drawn = false;
        foreach (FineTerrainBaseVisualItem item in scene.Items)
        {
            SimulationEntity? entity = ResolveFineTerrainBaseEntity(item.Team);
            string? lockedPlateId = entity is null ? null : ResolveLockedPlateIdFor(entity);
            Matrix4x4 compositeTransform = ResolveFineTerrainBaseCompositeTransform(scene.WorldScale, item, entity, includeSlide: true);
            Vector3 sceneAlignmentOffset = Vector3.Zero;
            if (!IsFineTerrainItemPotentiallyVisible(
                    ModelToScenePoint(Vector3.Transform(item.PivotModel, compositeTransform), scene.WorldScale) + sceneAlignmentOffset,
                    1.15f,
                    1.0f))
            {
                continue;
            }

            if (item.Triangles.Count > 0)
            {
                if (!TryDrawGpuFineTerrainBaseBody(scene.WorldScale, item, compositeTransform, sceneAlignmentOffset))
                {
                    DrawFineTerrainColoredTriangles(
                        null,
                        scene.WorldScale,
                        item.Triangles,
                        compositeTransform,
                        sceneAlignmentOffset,
                        null,
                        0f);
                }
                drawn = true;
            }

            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                bool locked = string.Equals(lockedPlateId, unit.PlateId, StringComparison.OrdinalIgnoreCase) && !unit.IsLightStrip;
                float flashDarkness = 0f;
                bool flashing = entity is not null
                    && IsStructurePlateFlashActive(entity.Id, unit.PlateId, out flashDarkness)
                    && unit.IsLightStrip;
                ResolveFineTerrainBaseUnitTint(item.Team, unit.IsLightStrip, locked, flashing, flashDarkness, out Color? tint, out float tintStrength);

                if (TryDrawGpuFineTerrainTintedUnitMesh(
                    _fineTerrainBaseUnitMeshCache,
                    _fineTerrainBaseSceneKey ?? string.Empty,
                    $"{item.Team}|{item.Name}|{unit.Name}",
                    scene.WorldScale,
                    item.PivotModel,
                    unit.Triangles,
                    compositeTransform,
                    sceneAlignmentOffset,
                    tint,
                    tintStrength,
                    Vector3.Zero))
                {
                    drawn = true;
                    continue;
                }

                DrawFineTerrainColoredTriangles(
                    null,
                    scene.WorldScale,
                    unit.Triangles,
                    compositeTransform,
                    sceneAlignmentOffset,
                    tint,
                    tintStrength);
                drawn = true;
            }
        }

        return drawn;
    }

    private bool TryDrawFineTerrainBases(Graphics graphics)
    {
        FineTerrainBaseVisualScene? scene = ResolveFineTerrainBaseScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return false;
        }

        bool drawn = false;
        foreach (FineTerrainBaseVisualItem item in scene.Items)
        {
            SimulationEntity? entity = ResolveFineTerrainBaseEntity(item.Team);
            string? lockedPlateId = entity is null ? null : ResolveLockedPlateIdFor(entity);
            Matrix4x4 compositeTransform = ResolveFineTerrainBaseCompositeTransform(scene.WorldScale, item, entity, includeSlide: true);
            Vector3 sceneAlignmentOffset = Vector3.Zero;

            if (item.Triangles.Count > 0)
            {
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    item.Triangles,
                    compositeTransform,
                    sceneAlignmentOffset,
                    null,
                    0f);
                drawn = true;
            }

            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                bool locked = string.Equals(lockedPlateId, unit.PlateId, StringComparison.OrdinalIgnoreCase) && !unit.IsLightStrip;
                float flashDarkness = 0f;
                bool flashing = entity is not null
                    && IsStructurePlateFlashActive(entity.Id, unit.PlateId, out flashDarkness)
                    && unit.IsLightStrip;
                ResolveFineTerrainBaseUnitTint(item.Team, unit.IsLightStrip, locked, flashing, flashDarkness, out Color? tint, out float tintStrength);
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    unit.Triangles,
                    compositeTransform,
                    sceneAlignmentOffset,
                    tint,
                    tintStrength);
                drawn = true;
            }
        }

        return drawn;
    }

    private void DrawFineTerrainEnergyTrianglesGdi(
        Graphics graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        var faces = new List<ProjectedFace>(Math.Min(item.Triangles.Count, 4096));
        foreach (FineTerrainColoredTriangle triangle in item.Triangles)
        {
            if (TryResolveFineTerrainEnergyStripProgress(item, triangle, out _))
            {
                continue;
            }

            Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3[] vertices = { a, b, c };
            Color fill = ResolveFineTerrainEnergyStaticBodyTriangleColor(item, triangle);
            Color edge = Color.FromArgb(Math.Min(255, fill.A + 12), BlendColor(fill, Color.Black, 0.24f));
            if (TryBuildProjectedFace(vertices, fill, edge, out ProjectedFace face))
            {
                faces.Add(face);
            }
        }

        if (faces.Count == 0)
        {
            return;
        }

        if (_collectProjectedFacesOnly)
        {
            _projectedStaticStructureFaceBuffer.AddRange(faces);
            return;
        }

        DrawProjectedFaceBatch(graphics, faces, 0.9f);
    }

    private void DrawFineTerrainEnergyBodyStripTriangles(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        if (!_host.World.Teams.TryGetValue(item.Team, out SimulationTeamState? teamState))
        {
            return;
        }

        float activatedRatio = ResolveFineTerrainEnergyActivationRatio(teamState);
        float flashPulse = 0f;
        bool recentFlash = teamState.EnergyLastHitArmIndex >= 0
            && IsFineTerrainEnergyTripleFlashActive(_host.World.GameTimeSec, teamState.EnergyLastHitFlashEndSec, out flashPulse);
        Color teamColor = ResolveTeamColor(item.Team);
        Color litColor = recentFlash
            ? BlendColor(teamColor, Color.White, 0.22f + flashPulse * 0.20f)
            : teamColor;
        Color darkColor = Color.FromArgb(236, 8, 9, 11);

        if (_gpuGeometryPass && UseGpuRenderer)
        {
            if (TryDrawGpuFineTerrainEnergyStripMesh(
                worldScale,
                item,
                compositeTransform,
                sceneAlignmentOffset,
                activatedRatio,
                Color.FromArgb(236, litColor),
                darkColor))
            {
                return;
            }

            foreach (FineTerrainColoredTriangle triangle in item.Triangles)
            {
                if (!TryResolveFineTerrainEnergyStripProgress(item, triangle, out float progress))
                {
                    continue;
                }

                Color fill = progress <= activatedRatio + 1e-4f
                    ? Color.FromArgb(236, litColor)
                    : darkColor;
                Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, compositeTransform), worldScale) + sceneAlignmentOffset;
                Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, compositeTransform), worldScale) + sceneAlignmentOffset;
                Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, compositeTransform), worldScale) + sceneAlignmentOffset;
                AppendOrDrawGpuTriangle(a, b, c, fill);
            }
            return;
        }

        if (graphics is null)
        {
            return;
        }

        var faces = new List<ProjectedFace>(2048);
        foreach (FineTerrainColoredTriangle triangle in item.Triangles)
        {
            if (!TryResolveFineTerrainEnergyStripProgress(item, triangle, out float progress))
            {
                continue;
            }

            Color fill = progress <= activatedRatio + 1e-4f
                ? Color.FromArgb(236, litColor)
                : darkColor;
            Color edge = Color.FromArgb(Math.Min(255, fill.A + 12), BlendColor(fill, Color.Black, 0.24f));
            Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, compositeTransform), worldScale) + sceneAlignmentOffset;
            if (TryBuildProjectedFace(new[] { a, b, c }, fill, edge, out ProjectedFace face))
            {
                faces.Add(face);
            }
        }

        if (faces.Count == 0)
        {
            return;
        }

        if (_collectProjectedFacesOnly)
        {
            _projectedStaticStructureFaceBuffer.AddRange(faces);
            return;
        }

        DrawProjectedFaceBatch(graphics, faces, 0.905f);
    }

    private bool TryDrawGpuFineTerrainEnergyStripMesh(
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset,
        float activatedRatio,
        Color litColor,
        Color darkColor)
    {
        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null)
        {
            return false;
        }

        int ratioKey = Math.Clamp((int)MathF.Round(activatedRatio * 1000f), 0, 1000);
        string sceneKey = _fineTerrainEnergyStripMeshSceneKey ?? string.Empty;
        string cacheKey = $"{sceneKey}|{item.CompositeId}|{item.Team}|{ratioKey}|{litColor.ToArgb():X8}|{darkColor.ToArgb():X8}";
        if (!_fineTerrainEnergyStripMeshCache.TryGetValue(cacheKey, out FineTerrainStaticMeshCache? cache))
        {
            cache = BuildFineTerrainEnergyStripMeshCache(worldScale, item, activatedRatio, litColor, darkColor);
            if (cache is null)
            {
                return false;
            }

            _fineTerrainEnergyStripMeshCache[cacheKey] = cache;
        }

        Matrix4x4 modelMatrix = ResolveFineTerrainSceneModelMatrix(
            worldScale,
            item.PivotModel,
            compositeTransform,
            cache.PivotScene,
            sceneAlignmentOffset);
        DrawGpuVertexBuffer(cache.Buffer, cache.VertexCount, modelMatrix);
        return true;
    }

    private FineTerrainStaticMeshCache? BuildFineTerrainEnergyStripMeshCache(
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        float activatedRatio,
        Color litColor,
        Color darkColor)
    {
        Vector3 pivotScene = ModelToScenePoint(item.PivotModel, worldScale);
        var vertices = new List<GpuVertex>(4096);
        foreach (FineTerrainColoredTriangle triangle in item.Triangles)
        {
            if (!TryResolveFineTerrainEnergyStripProgress(item, triangle, out float progress))
            {
                continue;
            }

            Color fill = progress <= activatedRatio + 1e-4f ? litColor : darkColor;
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.A, worldScale) - pivotScene, fill));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.B, worldScale) - pivotScene, fill));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.C, worldScale) - pivotScene, fill));
        }

        if (vertices.Count == 0)
        {
            return null;
        }

        _glGenBuffers!(1, out int buffer);
        UploadGpuVertexBuffer(buffer, vertices, GlStaticDraw);
        return new FineTerrainStaticMeshCache
        {
            Buffer = buffer,
            VertexCount = vertices.Count,
            PivotScene = pivotScene,
        };
    }

    private void DrawFineTerrainEnergyInteractionFeedback(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        _ = worldScale;
        _ = compositeTransform;
        _ = sceneAlignmentOffset;
        if (!_host.World.Teams.TryGetValue(item.Team, out SimulationTeamState? teamState))
        {
            return;
        }

        SimulationEntity? mechanism = ResolveFineTerrainEnergyMechanismEntity();
        if (mechanism is null)
        {
            return;
        }

        bool showActive = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            && teamState.EnergyNextModuleDelaySec <= 1e-6
            && teamState.EnergyCurrentLitMask != 0;
        bool showLastHit = teamState.EnergyLastHitArmIndex >= 0
            && teamState.EnergyLastRingScore > 0
            && _host.World.GameTimeSec <= teamState.EnergyLastHitFlashEndSec;
        bool hasPersistentRings = false;
        for (int index = 0; index < teamState.EnergyHitRingsByArm.Length; index++)
        {
            if (teamState.EnergyHitRingsByArm[index] > 0)
            {
                hasPersistentRings = true;
                break;
            }
        }

        if (!showActive && !showLastHit && !hasPersistentRings)
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        Color teamColor = ResolveTeamColor(item.Team);
        Color activeColor = Color.FromArgb(
            244,
            Math.Min(255, teamColor.R + 52),
            Math.Min(255, teamColor.G + 52),
            Math.Min(255, teamColor.B + 52));
        Color ringHitColor = Color.FromArgb(
            250,
            Math.Min(255, teamColor.R + 70),
            Math.Min(255, teamColor.G + 70),
            Math.Min(255, teamColor.B + 70));
        Color ringSteadyColor = Color.FromArgb(232, teamColor);

        foreach (ArmorPlateTarget plate in SimulationCombatMath.GetEnergyMechanismTargets(
                     mechanism,
                     metersPerWorldUnit,
                     _host.World.GameTimeSec,
                     item.Team,
                     teamState))
        {
            if (!SimulationCombatMath.TryParseEnergyArmIndex(plate.Id, out _, out int armIndex))
            {
                continue;
            }

            Vector3 diskCenter = ToScenePoint(plate.X, plate.Y, (float)plate.HeightM);
            ResolveEnergyDiskAxes(plate, out Vector3 normal, out Vector3 upAxis);
            float diskRadius = Math.Max(0.06f, (float)Math.Max(plate.WidthM, plate.HeightSpanM) * 0.5f);
            int persistentRingScore = armIndex >= 0 && armIndex < teamState.EnergyHitRingsByArm.Length
                ? Math.Clamp(teamState.EnergyHitRingsByArm[armIndex], 0, 10)
                : 0;
            int overlayRingScore = showActive
                ? 1
                : persistentRingScore > 0
                    ? persistentRingScore
                    : Math.Max(1, plate.EnergyRingScore);
            if (TryResolveFineTerrainEnergyOverlayPose(
                    item,
                    worldScale,
                    compositeTransform,
                    sceneAlignmentOffset,
                    armIndex,
                    overlayRingScore,
                    out Vector3 overlayCenter,
                    out Vector3 overlayNormal,
                    out Vector3 overlayUpAxis))
            {
                diskCenter = overlayCenter;
                normal = overlayNormal;
                upAxis = overlayUpAxis;
            }

            if (persistentRingScore <= 0)
            {
                continue;
            }

            float outer = diskRadius * (11 - persistentRingScore) / 10f;
            float inner = persistentRingScore >= 10 ? 0f : diskRadius * (10 - persistentRingScore) / 10f;
            bool flashing = showLastHit
                && teamState.EnergyLastHitArmIndex == armIndex
                && teamState.EnergyLastRingScore == persistentRingScore;
            Color ringColor = flashing
                ? (((_host.World.GameTimeSec * 6.25) % 1.0) < 0.5
                    ? Color.FromArgb(232, 8, 9, 11)
                    : ringSteadyColor)
                : ringSteadyColor;

            if (graphics is null)
            {
                DrawGpuAnnulusDoubleSided(
                    diskCenter,
                    normal,
                    upAxis,
                    inner,
                    Math.Max(inner + (flashing ? 0.004f : 0.003f), outer),
                    ringColor,
                    24,
                    flashing ? 0.0140f : 0.0130f);
            }
            else
            {
                DrawCpuAnnulusDoubleSided(
                    graphics,
                    diskCenter,
                    normal,
                    upAxis,
                    inner,
                    Math.Max(inner + (flashing ? 0.004f : 0.003f), outer),
                    ringColor,
                    24,
                    flashing ? 0.0140f : 0.0130f);
            }
        }
    }

    private bool TryResolveFineTerrainEnergyOverlayPose(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainWorldScale worldScale,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset,
        int armIndex,
        int preferredRingScore,
        out Vector3 center,
        out Vector3 normal,
        out Vector3 upAxis)
    {
        center = Vector3.Zero;
        normal = Vector3.UnitX;
        upAxis = Vector3.UnitY;

        FineTerrainEnergyMechanismUnitVisualItem? unit = item.Units
            .Where(candidate => candidate.Kind == FineTerrainEnergyUnitKind.Ring && candidate.ArmIndex == armIndex)
            .OrderBy(candidate =>
            {
                int score = candidate.RingScore <= 0 ? 1 : candidate.RingScore;
                return Math.Abs(score - Math.Max(1, preferredRingScore));
            })
            .ThenBy(candidate => candidate.RingScore <= 0 ? int.MaxValue : candidate.RingScore)
            .FirstOrDefault();
        if (unit is null)
        {
            return false;
        }

        center = ModelToScenePoint(Vector3.Transform(unit.LocalCenterModel, compositeTransform), worldScale)
            + sceneAlignmentOffset
            + ResolveFineTerrainEnergyUnitSceneLift(unit, worldScale, compositeTransform);
        Vector3 normalTipScene = ModelToScenePoint(
            Vector3.Transform(unit.LocalCenterModel + unit.LocalNormalModel, compositeTransform),
            worldScale) + sceneAlignmentOffset;
        Vector3 sceneNormal = normalTipScene - center;
        if (sceneNormal.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        normal = Vector3.Normalize(sceneNormal);
        Vector3 pivotScene = ModelToScenePoint(Vector3.Transform(item.PivotModel, compositeTransform), worldScale) + sceneAlignmentOffset;
        Vector3 radial = center - pivotScene;
        Vector3 tangentUp = radial - normal * Vector3.Dot(radial, normal);
        if (tangentUp.LengthSquared() <= 1e-8f)
        {
            Vector3 worldUp = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.98f
                ? Vector3.UnitZ
                : Vector3.UnitY;
            tangentUp = worldUp - normal * Vector3.Dot(worldUp, normal);
        }

        if (tangentUp.LengthSquared() <= 1e-8f)
        {
            tangentUp = Vector3.UnitZ;
        }

        upAxis = Vector3.Normalize(tangentUp);
        return true;
    }

    private static bool IsFineTerrainEnergyTripleFlashActive(double currentTimeSec, double flashEndTimeSec, out float pulse)
    {
        pulse = 0f;
        double remainingSec = flashEndTimeSec - currentTimeSec;
        if (remainingSec <= 1e-6)
        {
            return false;
        }

        double clampedRemainingSec = Math.Min(FineTerrainEnergyDoubleFlashDurationSec, remainingSec);
        double elapsedSec = FineTerrainEnergyDoubleFlashDurationSec - clampedRemainingSec;
        double phaseDurationSec = FineTerrainEnergyDoubleFlashDurationSec / 6.0;
        int phaseIndex = Math.Clamp((int)(elapsedSec / Math.Max(phaseDurationSec, 1e-6)), 0, 5);
        pulse = phaseIndex is 0 or 2 or 4 ? 1.0f : 0.18f;
        return true;
    }

    private SimulationEntity? ResolveFineTerrainEnergyMechanismEntity()
    {
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (entity.IsSimulationSuppressed
                || !string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return entity;
        }

        return null;
    }

    private static void ResolveFineTerrainEnergyUnitBaseTint(
        FineTerrainEnergyMechanismUnitVisualItem unit,
        out Color tint,
        out float tintStrength)
    {
        switch (unit.Kind)
        {
            case FineTerrainEnergyUnitKind.LightArm:
                tint = Color.FromArgb(244, 8, 9, 11);
                tintStrength = 0.98f;
                return;
            case FineTerrainEnergyUnitKind.CenterMark:
                tint = Color.FromArgb(232, 38, 40, 44);
                tintStrength = 0.86f;
                return;
            default:
                tint = Color.FromArgb(232, 54, 57, 63);
                tintStrength = 0.82f;
                return;
        }
    }

    private bool TryResolveFineTerrainEnergyUnitDynamicTint(
        string team,
        FineTerrainEnergyMechanismUnitVisualItem unit,
        out Color tint,
        out float tintStrength)
    {
        tint = Color.Empty;
        tintStrength = 0f;
        if (!_host.World.Teams.TryGetValue(team, out SimulationTeamState? teamState))
        {
            return false;
        }

        Color teamColor = ResolveTeamColor(team);
        bool activatingState = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase);
        bool activatedState = string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase)
            && teamState.EnergyBuffTimerSec > 1e-6;
        bool showActive = activatingState
            && teamState.EnergyNextModuleDelaySec <= 1e-6
            && teamState.EnergyCurrentLitMask != 0;
        bool activeArm = unit.ArmIndex >= 0
            && showActive
            && (teamState.EnergyCurrentLitMask & (1 << unit.ArmIndex)) != 0;
        int persistentRingScore = unit.ArmIndex >= 0 && unit.ArmIndex < teamState.EnergyHitRingsByArm.Length
            ? Math.Clamp(teamState.EnergyHitRingsByArm[unit.ArmIndex], 0, 10)
            : 0;
        float flashPulse = 0f;
        bool recentHitArm = unit.ArmIndex >= 0
            && teamState.EnergyLastHitArmIndex == unit.ArmIndex
            && IsFineTerrainEnergyTripleFlashActive(_host.World.GameTimeSec, teamState.EnergyLastHitFlashEndSec, out flashPulse);

        switch (unit.Kind)
        {
            case FineTerrainEnergyUnitKind.Ring:
            {
                bool hitFlash = unit.RingScore > 0
                    && recentHitArm
                    && teamState.EnergyLastRingScore == unit.RingScore;
                bool persistent = unit.RingScore > 0 && persistentRingScore == unit.RingScore;
                bool pendingActive = activeArm
                    && persistentRingScore <= 0
                    && (unit.RingScore == 4 || unit.RingScore == 7);
                if (!hitFlash && !persistent && !pendingActive)
                {
                    return false;
                }

                tint = hitFlash
                    ? (flashPulse > 0.5f ? Color.FromArgb(238, 8, 9, 11) : Color.FromArgb(240, teamColor))
                    : pendingActive
                        ? Color.FromArgb(236, BlendColor(teamColor, Color.White, 0.10f))
                        : Color.FromArgb(240, BlendColor(teamColor, Color.White, 0.18f));
                tintStrength = hitFlash
                    ? 1.0f
                    : pendingActive
                        ? 0.94f
                        : 0.98f;
                return true;
            }

            case FineTerrainEnergyUnitKind.LightArm:
            {
                bool lit = activatedState || activeArm || persistentRingScore > 0;
                if (!recentHitArm && !lit)
                {
                    return false;
                }

                tint = recentHitArm
                    ? (flashPulse > 0.5f ? Color.FromArgb(238, 8, 9, 11) : Color.FromArgb(236, teamColor))
                    : Color.FromArgb(activeArm ? 248 : activatedState ? 236 : 228, teamColor);
                tintStrength = recentHitArm
                    ? 1.0f
                    : 0.96f;
                return true;
            }

            case FineTerrainEnergyUnitKind.CenterMark:
                return false;
        }

        return false;
    }

    private void DrawFineTerrainEnergyUnitTriangles(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        foreach (FineTerrainEnergyMechanismUnitVisualItem unit in item.Units)
        {
            if (unit.Triangles.Count == 0)
            {
                continue;
            }

            if (unit.Kind == FineTerrainEnergyUnitKind.CenterMark)
            {
                continue;
            }

            ResolveFineTerrainEnergyUnitBaseTint(unit, out Color baseTint, out float tintStrength);
            Color tint = baseTint;
            if (TryResolveFineTerrainEnergyUnitDynamicTint(item.Team, unit, out Color dynamicTint, out float dynamicStrength))
            {
                tint = dynamicTint;
                tintStrength = dynamicStrength;
            }

            Vector3 extraSceneOffset = ResolveFineTerrainEnergyUnitSceneLift(unit, worldScale, compositeTransform);
            if (graphics is null
                && TryDrawGpuFineTerrainTintedUnitMesh(
                    _fineTerrainEnergyUnitMeshCache,
                    _fineTerrainEnergySceneKey ?? string.Empty,
                    $"{item.CompositeId}|{unit.Name}",
                    worldScale,
                    item.PivotModel,
                    unit.Triangles,
                    compositeTransform,
                    sceneAlignmentOffset,
                    tint,
                    tintStrength,
                    extraSceneOffset))
            {
                continue;
            }

            DrawFineTerrainColoredTriangles(
                graphics,
                worldScale,
                unit.Triangles,
                compositeTransform,
                sceneAlignmentOffset,
                tint,
                tintStrength,
                extraSceneOffset);
        }
    }

    private void DrawFineTerrainEnergyTrianglesGpu(
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Color fallbackColor,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        foreach (FineTerrainColoredTriangle triangle in item.Triangles)
        {
            if (TryResolveFineTerrainEnergyStripProgress(item, triangle, out _))
            {
                continue;
            }

            Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, compositeTransform), worldScale) + sceneAlignmentOffset;
            Color fill = ResolveFineTerrainEnergyStaticBodyTriangleColor(item, triangle, fallbackColor);
            AppendOrDrawGpuTriangle(a, b, c, fill);
        }
    }

    private bool TryDrawGpuFineTerrainEnergyBody(
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Color fallbackColor,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null)
        {
            return false;
        }

        string sceneKey = _fineTerrainEnergyBodyMeshSceneKey ?? string.Empty;
        string cacheKey = $"{sceneKey}|{item.CompositeId}|{item.Team}";
        if (!_fineTerrainEnergyBodyMeshCache.TryGetValue(cacheKey, out FineTerrainStaticMeshCache? cache))
        {
            cache = BuildFineTerrainEnergyBodyMeshCache(worldScale, item, fallbackColor);
            if (cache is null)
            {
                return false;
            }

            _fineTerrainEnergyBodyMeshCache[cacheKey] = cache;
        }

        Matrix4x4 modelMatrix = ResolveFineTerrainSceneModelMatrix(
            worldScale,
            item.PivotModel,
            compositeTransform,
            cache.PivotScene,
            sceneAlignmentOffset);
        DrawGpuVertexBuffer(cache.Buffer, cache.VertexCount, modelMatrix);
        return true;
    }

    private FineTerrainStaticMeshCache? BuildFineTerrainEnergyBodyMeshCache(
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Color fallbackColor)
    {
        if (item.Triangles.Count == 0)
        {
            return null;
        }

        Vector3 pivotScene = ModelToScenePoint(item.PivotModel, worldScale);
        var vertices = new List<GpuVertex>(item.Triangles.Count * 3);
        foreach (FineTerrainColoredTriangle triangle in item.Triangles)
        {
            if (TryResolveFineTerrainEnergyStripProgress(item, triangle, out _))
            {
                continue;
            }

            Color fill = ResolveFineTerrainEnergyStaticBodyTriangleColor(item, triangle, fallbackColor);
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.A, worldScale) - pivotScene, fill));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.B, worldScale) - pivotScene, fill));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.C, worldScale) - pivotScene, fill));
        }

        if (vertices.Count == 0)
        {
            return null;
        }

        _glGenBuffers!(1, out int buffer);
        UploadGpuVertexBuffer(buffer, vertices, GlStaticDraw);
        return new FineTerrainStaticMeshCache
        {
            Buffer = buffer,
            VertexCount = vertices.Count,
            PivotScene = pivotScene,
        };
    }

    private bool TryDrawGpuFineTerrainOutpostBody(
        FineTerrainWorldScale worldScale,
        FineTerrainOutpostVisualItem item,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null)
        {
            return false;
        }

        string sceneKey = _fineTerrainOutpostSceneKey ?? string.Empty;
        string cacheKey = $"{sceneKey}|{item.Team}|{item.Name}";
        if (!_fineTerrainOutpostBodyMeshCache.TryGetValue(cacheKey, out FineTerrainStaticMeshCache? cache))
        {
            cache = BuildFineTerrainOutpostBodyMeshCache(worldScale, item);
            if (cache is null)
            {
                return false;
            }

            _fineTerrainOutpostBodyMeshCache[cacheKey] = cache;
        }

        Matrix4x4 modelMatrix = ResolveFineTerrainSceneModelMatrix(
            worldScale,
            item.PivotModel,
            compositeTransform,
            cache.PivotScene,
            sceneAlignmentOffset);
        DrawGpuVertexBuffer(cache.Buffer, cache.VertexCount, modelMatrix);
        return true;
    }

    private bool TryDrawGpuFineTerrainBaseBody(
        FineTerrainWorldScale worldScale,
        FineTerrainBaseVisualItem item,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null)
        {
            return false;
        }

        string sceneKey = _fineTerrainBaseSceneKey ?? string.Empty;
        string cacheKey = $"{sceneKey}|{item.Team}|{item.Name}";
        if (!_fineTerrainBaseBodyMeshCache.TryGetValue(cacheKey, out FineTerrainStaticMeshCache? cache))
        {
            cache = BuildFineTerrainBaseBodyMeshCache(worldScale, item);
            if (cache is null)
            {
                return false;
            }

            _fineTerrainBaseBodyMeshCache[cacheKey] = cache;
        }

        Matrix4x4 modelMatrix = ResolveFineTerrainSceneModelMatrix(
            worldScale,
            item.PivotModel,
            compositeTransform,
            cache.PivotScene,
            sceneAlignmentOffset);
        DrawGpuVertexBuffer(cache.Buffer, cache.VertexCount, modelMatrix);
        return true;
    }

    private FineTerrainStaticMeshCache? BuildFineTerrainOutpostBodyMeshCache(
        FineTerrainWorldScale worldScale,
        FineTerrainOutpostVisualItem item)
    {
        if (item.Triangles.Count == 0)
        {
            return null;
        }

        Vector3 pivotScene = ModelToScenePoint(item.PivotModel, worldScale);
        var vertices = new List<GpuVertex>(item.Triangles.Count * 3);
        foreach (FineTerrainColoredTriangle triangle in item.Triangles)
        {
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.A, worldScale) - pivotScene, triangle.Color));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.B, worldScale) - pivotScene, triangle.Color));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.C, worldScale) - pivotScene, triangle.Color));
        }

        if (vertices.Count == 0)
        {
            return null;
        }

        _glGenBuffers!(1, out int buffer);
        UploadGpuVertexBuffer(buffer, vertices, GlStaticDraw);
        return new FineTerrainStaticMeshCache
        {
            Buffer = buffer,
            VertexCount = vertices.Count,
            PivotScene = pivotScene,
        };
    }

    private FineTerrainStaticMeshCache? BuildFineTerrainBaseBodyMeshCache(
        FineTerrainWorldScale worldScale,
        FineTerrainBaseVisualItem item)
    {
        if (item.Triangles.Count == 0)
        {
            return null;
        }

        Vector3 pivotScene = ModelToScenePoint(item.PivotModel, worldScale);
        var vertices = new List<GpuVertex>(item.Triangles.Count * 3);
        foreach (FineTerrainColoredTriangle triangle in item.Triangles)
        {
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.A, worldScale) - pivotScene, triangle.Color));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.B, worldScale) - pivotScene, triangle.Color));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.C, worldScale) - pivotScene, triangle.Color));
        }

        if (vertices.Count == 0)
        {
            return null;
        }

        _glGenBuffers!(1, out int buffer);
        UploadGpuVertexBuffer(buffer, vertices, GlStaticDraw);
        return new FineTerrainStaticMeshCache
        {
            Buffer = buffer,
            VertexCount = vertices.Count,
            PivotScene = pivotScene,
        };
    }

    private bool TryDrawGpuFineTerrainTintedUnitMesh(
        Dictionary<string, FineTerrainStaticMeshCache> cacheMap,
        string sceneKey,
        string meshKey,
        FineTerrainWorldScale worldScale,
        Vector3 pivotModel,
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset,
        Color? tint,
        float tintStrength,
        Vector3 extraSceneOffset)
    {
        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null)
        {
            return false;
        }

        string tintKey = tint is null
            ? "none"
            : $"{tint.Value.ToArgb():X8}_{Math.Clamp((int)MathF.Round(tintStrength * 1000f), 0, 1000)}";
        string cacheKey = $"{sceneKey}|{meshKey}|{tintKey}";
        if (!cacheMap.TryGetValue(cacheKey, out FineTerrainStaticMeshCache? cache))
        {
            cache = BuildFineTerrainTintedMeshCache(worldScale, pivotModel, triangles, tint, tintStrength);
            if (cache is null)
            {
                return false;
            }

            cacheMap[cacheKey] = cache;
        }

        Matrix4x4 modelMatrix = ResolveFineTerrainSceneModelMatrix(
            worldScale,
            pivotModel,
            compositeTransform,
            cache.PivotScene,
            sceneAlignmentOffset + extraSceneOffset);
        DrawGpuVertexBuffer(cache.Buffer, cache.VertexCount, modelMatrix);
        return true;
    }

    private FineTerrainStaticMeshCache? BuildFineTerrainTintedMeshCache(
        FineTerrainWorldScale worldScale,
        Vector3 pivotModel,
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        Color? tint,
        float tintStrength)
    {
        if (triangles.Count == 0)
        {
            return null;
        }

        Vector3 pivotScene = ModelToScenePoint(pivotModel, worldScale);
        var vertices = new List<GpuVertex>(triangles.Count * 3);
        foreach (FineTerrainColoredTriangle triangle in triangles)
        {
            Color fill = ResolveFineTerrainTriangleColor(triangle.Color, tint, tintStrength);
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.A, worldScale) - pivotScene, fill));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.B, worldScale) - pivotScene, fill));
            vertices.Add(new GpuVertex(ModelToScenePoint(triangle.C, worldScale) - pivotScene, fill));
        }

        if (vertices.Count == 0)
        {
            return null;
        }

        _glGenBuffers!(1, out int buffer);
        UploadGpuVertexBuffer(buffer, vertices, GlStaticDraw);
        return new FineTerrainStaticMeshCache
        {
            Buffer = buffer,
            VertexCount = vertices.Count,
            PivotScene = pivotScene,
        };
    }

    private Matrix4x4 ResolveFineTerrainSceneModelMatrix(
        FineTerrainWorldScale worldScale,
        Vector3 pivotModel,
        Matrix4x4 compositeTransform,
        Vector3 rawPivotScene,
        Vector3 sceneAlignmentOffset)
    {
        Matrix4x4 sceneLinear = ResolveFineTerrainSceneLinearTransform(worldScale, pivotModel, compositeTransform);
        Vector3 actualPivotScene = ModelToScenePoint(Vector3.Transform(pivotModel, compositeTransform), worldScale) + sceneAlignmentOffset;
        return sceneLinear * Matrix4x4.CreateTranslation(actualPivotScene);
    }

    private bool IsFineTerrainItemPotentiallyVisible(Vector3 centerScene, double radiusM, double heightM)
        => IsSceneBoundsPotentiallyVisible(centerScene, radiusM, heightM);

    private Matrix4x4 ResolveFineTerrainEnergyDeltaModelTransform(FineTerrainEnergyMechanismVisualItem item)
    {
        float dynamicAngle = ResolveFineTerrainEnergyRotorAngleRad(item.Team);
        Vector3 pivot = item.PivotModel;
        Vector3 axis = ResolveFineTerrainRotorAxis(item.CoordinateYprDegrees);
        return
            Matrix4x4.CreateTranslation(-pivot)
            * Matrix4x4.CreateFromAxisAngle(axis, dynamicAngle)
            * Matrix4x4.CreateTranslation(pivot);
    }

    private Vector3 ResolveFineTerrainEnergySceneAlignmentOffset(
        FineTerrainWorldScale worldScale,
        double centerWorldX,
        double centerWorldY,
        FineTerrainEnergyMechanismVisualItem item)
    {
        // Fine-terrain annotations already store the authoritative world-space pose.
        // Do not re-align the visual composite to rule targets in-match, otherwise
        // editor placement and in-match placement diverge.
        return Vector3.Zero;
    }

    private Vector3 ResolveFineTerrainEnergyDesiredPivotScene(string team, double fallbackWorldX, double fallbackWorldY)
    {
        if (TryResolveFineTerrainEnergyTeamPivotWorld(team, out double pivotWorldX, out double pivotWorldY, out double pivotHeightM))
        {
            return ToScenePoint(pivotWorldX, pivotWorldY, (float)pivotHeightM);
        }

        return ToScenePoint(fallbackWorldX, fallbackWorldY, 0f);
    }

    private bool TryResolveFineTerrainEnergyTeamPivotWorld(
        string team,
        out double pivotWorldX,
        out double pivotWorldY,
        out double pivotHeightM)
    {
        pivotWorldX = 0.0;
        pivotWorldY = 0.0;
        pivotHeightM = 0.0;

        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (!string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _host.World.Teams.TryGetValue(team, out SimulationTeamState? teamState);
            IReadOnlyList<ArmorPlateTarget> targets = SimulationCombatMath.GetEnergyMechanismTargets(
                entity,
                _host.World.MetersPerWorldUnit,
                _host.World.GameTimeSec,
                team,
                teamState);
            if (targets.Count == 0)
            {
                continue;
            }

            foreach (ArmorPlateTarget target in targets)
            {
                pivotWorldX += target.X;
                pivotWorldY += target.Y;
                pivotHeightM += target.HeightM;
            }

            double divisor = Math.Max(1, targets.Count);
            pivotWorldX /= divisor;
            pivotWorldY /= divisor;
            pivotHeightM /= divisor;
            return true;
        }

        return false;
    }

    private Matrix4x4 ResolveFineTerrainCompositeTransform(FineTerrainEnergyMechanismVisualItem item)
    {
        return ResolveFineTerrainEnergyDeltaModelTransform(item) * ResolveFineTerrainCompositeBaseTransform(item);
    }

    private Matrix4x4 ResolveFineTerrainCompositeBaseTransform(FineTerrainEnergyMechanismVisualItem item)
    {
        if (TryResolveFineTerrainCompositePoseOverride(
            item.CompositeId,
            out Vector3 pivotModel,
            out Vector3 positionModel,
            out Vector3 rotationYprDegrees))
        {
            return ResolveFineTerrainCompositeBaseTransform(pivotModel, positionModel, rotationYprDegrees);
        }

        return ResolveFineTerrainCompositeBaseTransform(item.PivotModel, item.PositionModel, item.RotationYprDegrees);
    }

    private float ResolveFineTerrainEnergyRotorAngleRad(string team)
    {
        if (_host.World.Teams.TryGetValue(team, out SimulationTeamState? teamState))
        {
            return EnergyMechanismGeometry.ResolveRuleRotorYaw((float)_host.World.GameTimeSec, teamState);
        }

        return EnergyMechanismGeometry.ResolveRuleRotorYaw((float)_host.World.GameTimeSec, null);
    }

    private static Vector3 ResolveFineTerrainRotorAxis(Vector3 coordinateYprDegrees)
    {
        Matrix4x4 coordinateRotation = Matrix4x4.CreateFromYawPitchRoll(
            coordinateYprDegrees.X * MathF.PI / 180f,
            coordinateYprDegrees.Y * MathF.PI / 180f,
            coordinateYprDegrees.Z * MathF.PI / 180f);
        Vector3 axis = Vector3.TransformNormal(Vector3.UnitX, coordinateRotation);
        if (axis.LengthSquared() <= 1e-8f)
        {
            axis = Vector3.UnitX;
        }

        return Vector3.Normalize(axis);
    }

    private Vector3 ModelToScenePoint(Vector3 modelPoint, FineTerrainWorldScale worldScale)
    {
        (double worldX, double worldY, double heightM) = ModelPointToWorld(modelPoint, worldScale);
        return ToScenePoint(worldX, worldY, (float)heightM);
    }

    private (double WorldX, double WorldY, double HeightM) ModelPointToWorld(Vector3 modelPoint, FineTerrainWorldScale worldScale)
    {
        (double worldX, double worldY, _) = FineTerrainAnnotationWorldSpace.ModelPointToWorld(
            worldScale.MapLengthXMeters,
            worldScale.MapLengthZMeters,
            Math.Max(_host.World.MetersPerWorldUnit, 1e-6),
            worldScale.ModelCenter.X,
            worldScale.ModelCenter.Y,
            worldScale.ModelCenter.Z,
            worldScale.XMetersPerModelUnit,
            worldScale.YMetersPerModelUnit,
            worldScale.ZMetersPerModelUnit,
            modelPoint.X,
            modelPoint.Y,
            modelPoint.Z);
        double heightM = Math.Max(0.0, (modelPoint.Y - worldScale.ModelMinY) * worldScale.YMetersPerModelUnit);
        return (worldX, worldY, heightM);
    }

    private Matrix4x4 ResolveFineTerrainSceneLinearTransform(
        FineTerrainWorldScale worldScale,
        Vector3 pivotModel,
        Matrix4x4 deltaModelTransform)
    {
        Vector3 pivotScene = ModelToScenePoint(pivotModel, worldScale);
        Matrix4x4 before = BuildFineTerrainSceneBasisMatrix(
            ModelToScenePoint(pivotModel + Vector3.UnitX, worldScale) - pivotScene,
            ModelToScenePoint(pivotModel + Vector3.UnitY, worldScale) - pivotScene,
            ModelToScenePoint(pivotModel + Vector3.UnitZ, worldScale) - pivotScene);
        if (!Matrix4x4.Invert(before, out Matrix4x4 inverseBefore))
        {
            return Matrix4x4.Identity;
        }

        Vector3 transformedPivotScene = ModelToScenePoint(Vector3.Transform(pivotModel, deltaModelTransform), worldScale);
        Matrix4x4 after = BuildFineTerrainSceneBasisMatrix(
            ModelToScenePoint(Vector3.Transform(pivotModel + Vector3.UnitX, deltaModelTransform), worldScale) - transformedPivotScene,
            ModelToScenePoint(Vector3.Transform(pivotModel + Vector3.UnitY, deltaModelTransform), worldScale) - transformedPivotScene,
            ModelToScenePoint(Vector3.Transform(pivotModel + Vector3.UnitZ, deltaModelTransform), worldScale) - transformedPivotScene);
        return inverseBefore * after;
    }

    private static Matrix4x4 BuildFineTerrainSceneBasisMatrix(Vector3 xAxis, Vector3 yAxis, Vector3 zAxis)
    {
        return new Matrix4x4(
            xAxis.X, xAxis.Y, xAxis.Z, 0f,
            yAxis.X, yAxis.Y, yAxis.Z, 0f,
            zAxis.X, zAxis.Y, zAxis.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    private bool TryDrawFineTerrainOutpost(Graphics graphics, SimulationEntity entity, string? lockedPlateId, StructureRenderPass renderPass)
    {
        FineTerrainOutpostVisualScene? scene = ResolveFineTerrainOutpostScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return false;
        }

        IReadOnlyList<ArmorPlateTarget> plates = SimulationCombatMath.GetArmorPlateTargets(
            entity,
            _host.World.MetersPerWorldUnit,
            _host.World.GameTimeSec,
            includeOutpostTopArmor: true);
        bool drawn = false;
        foreach (FineTerrainOutpostVisualItem item in scene.Items)
        {
            if (!string.Equals(item.Team, entity.Team, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Matrix4x4 transform = ResolveFineTerrainOutpostCompositeTransform(item, entity);
            Vector3 sceneAlignmentOffset = ResolveFineTerrainOutpostSceneAlignmentOffset(scene.WorldScale, entity, item, plates);
            bool drawBody = renderPass != StructureRenderPass.DynamicArmor;
            bool drawUnits = renderPass != StructureRenderPass.StaticBody;
            if (drawBody && item.Triangles.Count > 0)
            {
                Color? bodyTint = entity.IsAlive ? null : Color.FromArgb(248, 8, 9, 11);
                float bodyTintStrength = entity.IsAlive ? 0f : 0.92f;
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    item.Triangles,
                    transform,
                    sceneAlignmentOffset,
                    bodyTint,
                    bodyTintStrength);
                drawn = true;
            }

            if (!drawUnits)
            {
                continue;
            }

            foreach (FineTerrainOutpostUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                bool locked = entity.IsAlive && string.Equals(lockedPlateId, unit.PlateId, StringComparison.OrdinalIgnoreCase) && !unit.IsLightStrip;
                bool flashing = IsStructurePlateFlashActive(entity.Id, unit.PlateId, out float flashIntensity) && unit.IsLightStrip;
                ResolveFineTerrainOutpostUnitTint(item.Team, unit.IsLightStrip, locked, flashing, flashIntensity, entity.IsAlive, out Color? tint, out float tintStrength);

                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    unit.Triangles,
                    transform,
                    sceneAlignmentOffset,
                    tint,
                    tintStrength);
                drawn = true;
            }
        }

        return drawn;
    }

    private bool TryDrawFineTerrainBase(Graphics graphics, SimulationEntity entity, string? lockedPlateId, StructureRenderPass renderPass)
    {
        FineTerrainBaseVisualScene? scene = ResolveFineTerrainBaseScene();
        if (scene is null || scene.Items.Count == 0)
        {
            return false;
        }

        bool drawn = false;
        foreach (FineTerrainBaseVisualItem item in scene.Items)
        {
            if (!string.Equals(item.Team, entity.Team, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Matrix4x4 compositeTransform = ResolveFineTerrainBaseCompositeTransform(scene.WorldScale, item, entity, includeSlide: true);
            Vector3 sceneAlignmentOffset = Vector3.Zero;
            bool drawBody = renderPass != StructureRenderPass.DynamicArmor;
            bool drawUnits = renderPass != StructureRenderPass.StaticBody;
            if (drawBody && item.Triangles.Count > 0)
            {
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    item.Triangles,
                    compositeTransform,
                    sceneAlignmentOffset,
                    null,
                    0f);
                drawn = true;
            }

            if (!drawUnits)
            {
                continue;
            }

            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                bool locked = string.Equals(lockedPlateId, unit.PlateId, StringComparison.OrdinalIgnoreCase) && !unit.IsLightStrip;
                float flashDarkness = 0f;
                bool flashing = IsStructurePlateFlashActive(entity.Id, unit.PlateId, out flashDarkness) && unit.IsLightStrip;
                ResolveFineTerrainBaseUnitTint(item.Team, unit.IsLightStrip, locked, flashing, flashDarkness, out Color? tint, out float tintStrength);
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    unit.Triangles,
                    compositeTransform,
                    sceneAlignmentOffset,
                    tint,
                    tintStrength);
                drawn = true;
            }
        }

        return drawn;
    }

    private void ResolveFineTerrainOutpostUnitTint(
        string team,
        bool isLightStrip,
        bool locked,
        bool flashing,
        float flashIntensity,
        bool isAlive,
        out Color? tint,
        out float tintStrength)
    {
        tint = null;
        tintStrength = 0f;
        if (!isAlive)
        {
            tint = Color.FromArgb(244, 8, 9, 11);
            tintStrength = 1.0f;
            return;
        }

        if (locked)
        {
            tint = Color.FromArgb(255, 255, 214, 86);
            tintStrength = 0.70f;
            return;
        }

        if (!isLightStrip)
        {
            return;
        }

        Color teamColor = ResolveTeamColor(team);
        tint = flashing && flashIntensity > 0.5f
            ? Color.FromArgb(238, 8, 9, 11)
            : Color.FromArgb(214, teamColor);
        tintStrength = 1.0f;
    }

    private void ResolveFineTerrainBaseUnitTint(
        string team,
        bool isLightStrip,
        bool locked,
        bool flashing,
        float flashDarkness,
        out Color? tint,
        out float tintStrength)
    {
        tint = null;
        tintStrength = 0f;
        if (locked)
        {
            tint = Color.FromArgb(255, 255, 214, 86);
            tintStrength = 0.70f;
            return;
        }

        if (!isLightStrip)
        {
            return;
        }

        Color teamColor = ResolveTeamColor(team);
        tint = flashing && flashDarkness > 0.5f
            ? Color.FromArgb(238, 8, 9, 11)
            : Color.FromArgb(220, teamColor);
        tintStrength = flashing ? 0.98f : 0.72f;
    }

    private void DrawFineTerrainEnergyArmStripFeedback(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset,
        SimulationTeamState teamState,
        Color teamColor,
        bool activatedState)
    {
        int activatedArmCount = ResolveFineTerrainEnergyActivatedArmCount(teamState);
        float activatedRatio = activatedState ? 1.0f : Math.Clamp(activatedArmCount / 5.0f, 0f, 1f);
        if (activatedRatio <= 1e-4f)
        {
            return;
        }

        Vector3 pivotScene = ModelToScenePoint(Vector3.Transform(item.PivotModel, compositeTransform), worldScale) + sceneAlignmentOffset;
        Vector3 rotorAxisModel = ResolveFineTerrainRotorAxis(item.CoordinateYprDegrees);
        Vector3 rotorAxisScene = ModelToScenePoint(Vector3.Transform(item.PivotModel + rotorAxisModel, compositeTransform), worldScale) + sceneAlignmentOffset - pivotScene;
        if (rotorAxisScene.LengthSquared() <= 1e-8f)
        {
            rotorAxisScene = Vector3.UnitY;
        }
        else
        {
            rotorAxisScene = Vector3.Normalize(rotorAxisScene);
        }

        Color litColor = Color.FromArgb(232, BlendColor(teamColor, Color.White, 0.14f));
        foreach (IGrouping<int, FineTerrainEnergyMechanismUnitVisualItem> armGroup in item.Units.GroupBy(unit => unit.ArmIndex))
        {
            FineTerrainEnergyMechanismUnitVisualItem? representative = armGroup
                .OrderBy(unit => unit.RingScore)
                .FirstOrDefault();
            if (representative is null)
            {
                continue;
            }

            Vector3 armCenter = ModelToScenePoint(Vector3.Transform(representative.LocalCenterModel, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 radial = armCenter - pivotScene;
            radial -= rotorAxisScene * Vector3.Dot(radial, rotorAxisScene);
            float radialLength = radial.Length();
            if (radialLength <= 0.12f)
            {
                continue;
            }

            radial /= radialLength;
            float startInset = MathF.Min(0.16f, radialLength * 0.18f);
            float endInset = MathF.Min(0.10f, radialLength * 0.12f);
            float stripLength = MathF.Max(0.05f, radialLength - startInset - endInset);
            Vector3 stripStart = pivotScene + radial * startInset;
            Vector3 litEnd = stripStart + radial * (stripLength * activatedRatio);
            DrawFineTerrainBeam(graphics, stripStart, litEnd, rotorAxisScene, 0.020f, 0.0075f, litColor);
        }
    }

    private int ResolveFineTerrainEnergyActivatedArmCount(SimulationTeamState teamState)
    {
        if (teamState.EnergyActivatedGroupCount > 0)
        {
            return Math.Clamp(teamState.EnergyActivatedGroupCount, 0, 5);
        }

        int count = 0;
        for (int index = 0; index < teamState.EnergyHitRingsByArm.Length; index++)
        {
            if (teamState.EnergyHitRingsByArm[index] > 0)
            {
                count++;
            }
        }

        return Math.Clamp(count, 0, 5);
    }

    private void DrawFineTerrainBeam(
        Graphics? graphics,
        Vector3 start,
        Vector3 end,
        Vector3 lateralAxis,
        float height,
        float thickness,
        Color fillColor)
    {
        if (graphics is null)
        {
            DrawGpuBeam3dFast(start, end, lateralAxis, height, thickness, fillColor);
            return;
        }

        DrawBeam3d(
            graphics,
            start,
            end,
            lateralAxis,
            height,
            thickness,
            fillColor,
            Color.FromArgb(Math.Min(255, fillColor.A + 10), BlendColor(fillColor, Color.Black, 0.18f)));
    }

    private Color ResolveFineTerrainEnergyStaticBodyTriangleColor(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainColoredTriangle triangle)
        => ResolveFineTerrainEnergyStaticBodyTriangleColor(
            item,
            triangle,
            Color.FromArgb(236, 224, 232, 240));

    private Color ResolveFineTerrainEnergyStaticBodyTriangleColor(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainColoredTriangle triangle,
        Color fallbackColor)
    {
        Color source = triangle.Color.A <= 0 ? fallbackColor : triangle.Color;
        return NormalizeFineTerrainEnergyBodyColor(source);
    }

    private static Color NormalizeFineTerrainEnergyBodyColor(Color source)
    {
        float luminance = (source.R * 0.2126f + source.G * 0.7152f + source.B * 0.0722f) / 255f;
        int baseValue = Math.Clamp((int)MathF.Round(42f + luminance * 44f), 34, 96);
        return Color.FromArgb(
            source.A <= 0 ? 236 : source.A,
            baseValue,
            Math.Clamp(baseValue + 4, 0, 255),
            Math.Clamp(baseValue + 10, 0, 255));
    }

    private float ResolveFineTerrainEnergyActivationRatio(SimulationTeamState teamState)
    {
        if (string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase)
            && teamState.EnergyBuffTimerSec > 1e-6)
        {
            return 1.0f;
        }

        int activatedArmCount = ResolveFineTerrainEnergyActivatedArmCount(teamState);
        return activatedArmCount switch
        {
            <= 0 => 0f,
            1 => 0.2f,
            2 => 0.4f,
            3 => 0.6f,
            4 => 0.8f,
            _ => 1.0f,
        };
    }

    private bool TryResolveFineTerrainEnergyStripProgress(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainColoredTriangle triangle,
        out float progress)
    {
        progress = 0f;
        if (!IsLikelyFineTerrainEnergyStripColor(triangle.Color))
        {
            return false;
        }

        Vector3 rotorAxis = ResolveFineTerrainRotorAxis(item.CoordinateYprDegrees);
        Vector3 centroid = (triangle.A + triangle.B + triangle.C) / 3f;
        Vector3 radial = centroid - item.PivotModel;
        radial -= rotorAxis * Vector3.Dot(radial, rotorAxis);
        float radialDistance = radial.Length();
        if (radialDistance <= 1e-4f)
        {
            return false;
        }

        ResolveFineTerrainEnergyStripBounds(item, rotorAxis, out float innerRadius, out float outerRadius);
        if (radialDistance < innerRadius - 0.015f || radialDistance > outerRadius + 0.015f)
        {
            return false;
        }

        progress = Math.Clamp((radialDistance - innerRadius) / Math.Max(0.02f, outerRadius - innerRadius), 0f, 1f);
        return true;
    }

    private static void ResolveFineTerrainEnergyStripBounds(
        FineTerrainEnergyMechanismVisualItem item,
        Vector3 rotorAxis,
        out float innerRadius,
        out float outerRadius)
    {
        innerRadius = float.MaxValue;
        outerRadius = 0f;
        foreach (FineTerrainEnergyMechanismUnitVisualItem unit in item.Units)
        {
            if (unit.Kind != FineTerrainEnergyUnitKind.Ring)
            {
                continue;
            }

            Vector3 radial = unit.LocalCenterModel - item.PivotModel;
            radial -= rotorAxis * Vector3.Dot(radial, rotorAxis);
            float unitRadius = radial.Length();
            if (unitRadius <= 1e-4f)
            {
                continue;
            }

            innerRadius = MathF.Min(innerRadius, unitRadius);
            outerRadius = MathF.Max(outerRadius, unitRadius + MathF.Max((float)unit.WidthM, (float)unit.HeightSpanM) * 0.65f);
        }

        if (!float.IsFinite(innerRadius) || innerRadius <= 1e-4f)
        {
            innerRadius = 0.12f;
        }

        if (outerRadius <= innerRadius + 0.02f)
        {
            outerRadius = innerRadius + 0.24f;
        }

        innerRadius = MathF.Max(0.05f, innerRadius * 0.42f);
        outerRadius = MathF.Max(innerRadius + 0.06f, outerRadius * 1.05f);
    }

    private static bool IsLikelyFineTerrainEnergyStripColor(Color color)
    {
        int max = Math.Max(color.R, Math.Max(color.G, color.B));
        int min = Math.Min(color.R, Math.Min(color.G, color.B));
        return max - min >= 26 && max >= 70;
    }

    private Vector3 ResolveFineTerrainEnergyUnitSceneLift(
        FineTerrainEnergyMechanismUnitVisualItem unit,
        FineTerrainWorldScale worldScale,
        Matrix4x4 compositeTransform)
    {
        float liftMeters = unit.Kind switch
        {
            FineTerrainEnergyUnitKind.LightArm => 0.0045f,
            FineTerrainEnergyUnitKind.Ring => 0.0022f,
            _ => 0.0012f,
        };
        if (liftMeters <= 1e-6f)
        {
            return Vector3.Zero;
        }

        Vector3 centerScene = ModelToScenePoint(Vector3.Transform(unit.LocalCenterModel, compositeTransform), worldScale);
        Vector3 normalTipScene = ModelToScenePoint(Vector3.Transform(unit.LocalCenterModel + unit.LocalNormalModel, compositeTransform), worldScale);
        Vector3 sceneNormal = normalTipScene - centerScene;
        if (sceneNormal.LengthSquared() <= 1e-8f)
        {
            return Vector3.Zero;
        }

        return Vector3.Normalize(sceneNormal) * liftMeters;
    }

    private bool HasFineTerrainOutpostForTeam(string team)
    {
        FineTerrainOutpostVisualScene? scene = ResolveFineTerrainOutpostScene();
        return scene is not null
            && scene.Items.Any(item => string.Equals(item.Team, team, StringComparison.OrdinalIgnoreCase));
    }

    private bool HasFineTerrainBaseForTeam(string team)
    {
        FineTerrainBaseVisualScene? scene = ResolveFineTerrainBaseScene();
        return scene is not null
            && scene.Items.Any(item => string.Equals(item.Team, team, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawFineTerrainColoredTriangles(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        Matrix4x4 transform,
        Vector3 sceneAlignmentOffset,
        Color? tint,
        float tintStrength)
        => DrawFineTerrainColoredTriangles(
            graphics,
            worldScale,
            triangles,
            transform,
            sceneAlignmentOffset,
            tint,
            tintStrength,
            Vector3.Zero);

    private void DrawFineTerrainColoredTriangles(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        Matrix4x4 transform,
        Vector3 sceneAlignmentOffset,
        Color? tint,
        float tintStrength,
        Vector3 extraSceneOffset)
    {
        if (_gpuGeometryPass && UseGpuRenderer)
        {
            foreach (FineTerrainColoredTriangle triangle in triangles)
            {
                Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
                Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
                Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
                AppendOrDrawGpuTriangle(a, b, c, ResolveFineTerrainTriangleColor(triangle.Color, tint, tintStrength));
            }

            return;
        }

        if (graphics is null)
        {
            return;
        }

        if (graphics is null)
        {
            return;
        }

        var faces = new List<ProjectedFace>(Math.Min(triangles.Count, 2048));
        foreach (FineTerrainColoredTriangle triangle in triangles)
        {
            Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
            Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
            Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
            Color fill = ResolveFineTerrainTriangleColor(triangle.Color, tint, tintStrength);
            Color edge = Color.FromArgb(Math.Min(255, fill.A + 14), BlendColor(fill, Color.Black, 0.24f));
            if (TryBuildProjectedFace(new[] { a, b, c }, fill, edge, out ProjectedFace face))
            {
                faces.Add(face);
            }
        }

        if (faces.Count == 0)
        {
            return;
        }

        if (_collectProjectedFacesOnly)
        {
            _projectedEntityFaceBuffer.AddRange(faces);
            return;
        }

        DrawProjectedFaceBatch(graphics, faces, 0.92f);
    }

    private static Color ResolveFineTerrainTriangleColor(Color source, Color? tint, float tintStrength)
    {
        Color fill = source.A <= 0 ? Color.FromArgb(236, 224, 232, 240) : source;
        if (tint is null || tintStrength <= 1e-4f)
        {
            return fill;
        }

        Color blended = BlendColor(fill, tint.Value, Math.Clamp(tintStrength, 0f, 1f));
        int alpha = Math.Clamp(Math.Max((int)fill.A, (int)tint.Value.A), 0, 255);
        return Color.FromArgb(alpha, blended);
    }

    private Vector3 ResolveFineTerrainOutpostSceneAlignmentOffset(
        FineTerrainWorldScale worldScale,
        SimulationEntity entity,
        FineTerrainOutpostVisualItem item,
        IReadOnlyList<ArmorPlateTarget> plates)
    {
        // Keep outpost visuals in the exact annotation pose. Combat/aim logic should
        // follow the placed composite, not drag the composite to rule-space targets.
        return Vector3.Zero;
    }

    private Matrix4x4 ResolveFineTerrainOutpostCompositeTransform(
        FineTerrainOutpostVisualItem item,
        SimulationEntity? entity)
    {
        Matrix4x4 baseTransform = ResolveFineTerrainCompositeBaseTransform(item);
        if (item.Kind != FineTerrainOutpostComponentKind.RotatingArmor
            || entity is null)
        {
            return baseTransform;
        }

        float deltaYawRad = (float)SimulationCombatMath.ResolveOutpostRingRelativeRotationRad(entity, _host.World.GameTimeSec);

        return
            Matrix4x4.CreateTranslation(-item.PivotModel)
            * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, deltaYawRad)
            * Matrix4x4.CreateTranslation(item.PivotModel)
            * baseTransform;
    }

    private Matrix4x4 ResolveFineTerrainOutpostCompositeTransform(
        FineTerrainWorldScale worldScale,
        FineTerrainOutpostVisualItem item,
        SimulationEntity entity,
        IReadOnlyList<ArmorPlateTarget> plates)
    {
        if (IsFineTerrainCompositePoseOverridden(item.CompositeId))
        {
            return ResolveFineTerrainOutpostCompositeTransform(item, entity);
        }

        Matrix4x4 baseTransform = ResolveFineTerrainCompositeBaseTransform(item);
        if (item.Kind != FineTerrainOutpostComponentKind.RotatingArmor || !entity.IsAlive)
        {
            return baseTransform;
        }

        if (!TryResolveFineTerrainOutpostReferenceUnit(item, out FineTerrainOutpostUnitVisualItem? referenceUnit)
            || referenceUnit is null
            || !TryResolvePlateById(plates, referenceUnit.PlateId, out ArmorPlateTarget desiredPlate))
        {
            return ResolveFineTerrainOutpostCompositeTransform(item, (SimulationEntity?)entity);
        }

        Vector3 actualPivotScene = ModelToScenePoint(Vector3.Transform(item.PivotModel, baseTransform), worldScale);
        Vector3 actualReferenceScene = ModelToScenePoint(Vector3.Transform(referenceUnit.LocalCentroidModel, baseTransform), worldScale);
        float actualYawRad = MathF.Atan2(actualReferenceScene.Z - actualPivotScene.Z, actualReferenceScene.X - actualPivotScene.X);

        Vector3 desiredPlateScene = ToScenePoint(desiredPlate.X, desiredPlate.Y, (float)desiredPlate.HeightM);
        float desiredYawRad = MathF.Atan2(desiredPlateScene.Z - actualPivotScene.Z, desiredPlateScene.X - actualPivotScene.X);
        float deltaYawRad = NormalizeRadians(desiredYawRad - actualYawRad);

        return
            Matrix4x4.CreateTranslation(-item.PivotModel)
            * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, deltaYawRad)
            * Matrix4x4.CreateTranslation(item.PivotModel)
            * baseTransform;
    }

    private SimulationEntity? ResolveFineTerrainOutpostEntity(string team)
    {
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entity.Team, team, StringComparison.OrdinalIgnoreCase))
            {
                return entity;
            }
        }

        return null;
    }

    private SimulationEntity? ResolveFineTerrainBaseEntity(string team)
    {
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entity.Team, team, StringComparison.OrdinalIgnoreCase))
            {
                return entity;
            }
        }

        return null;
    }

    private Vector3 ResolveFineTerrainOutpostDesiredPivotScene(
        SimulationEntity entity,
        FineTerrainOutpostVisualItem item,
        IReadOnlyList<ArmorPlateTarget> plates)
    {
        if (item.Kind == FineTerrainOutpostComponentKind.TopArmor
            && TryResolvePlateById(plates, "outpost_top", out ArmorPlateTarget topPlate))
        {
            return ToScenePoint(topPlate.X, topPlate.Y, (float)topPlate.HeightM);
        }

        float sumHeight = 0f;
        int count = 0;
        foreach (ArmorPlateTarget plate in plates)
        {
            if (!plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sumHeight += (float)plate.HeightM;
            count++;
        }

        float pivotHeight = count > 0
            ? sumHeight / count
            : (float)(entity.GroundHeightM + entity.StructureBaseLiftM + entity.BodyHeightM);
        return ToScenePoint(entity.X, entity.Y, pivotHeight);
    }

    private static bool TryResolveFineTerrainOutpostReferenceUnit(
        FineTerrainOutpostVisualItem item,
        out FineTerrainOutpostUnitVisualItem? unit)
    {
        unit = item.Units.FirstOrDefault(candidate =>
            !candidate.IsLightStrip
            && string.Equals(candidate.PlateId, "outpost_ring_2", StringComparison.OrdinalIgnoreCase));
        unit ??= item.Units.FirstOrDefault(candidate =>
            !candidate.IsLightStrip
            && candidate.PlateId.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase));
        return unit is not null;
    }

    private static bool TryResolveFineTerrainBaseReferenceUnit(
        FineTerrainBaseVisualItem item,
        out FineTerrainBaseUnitVisualItem? unit)
    {
        unit = item.Units.FirstOrDefault(candidate =>
            !candidate.IsLightStrip
            && string.Equals(candidate.PlateId, "base_top_slide", StringComparison.OrdinalIgnoreCase));
        unit ??= item.Units.FirstOrDefault(candidate => !candidate.IsLightStrip);
        return unit is not null;
    }

    private static bool TryResolvePlateById(IReadOnlyList<ArmorPlateTarget> plates, string plateId, out ArmorPlateTarget plate)
    {
        foreach (ArmorPlateTarget candidate in plates)
        {
            if (string.Equals(candidate.Id, plateId, StringComparison.OrdinalIgnoreCase))
            {
                plate = candidate;
                return true;
            }
        }

        plate = default;
        return false;
    }

    private static float NormalizeRadians(float radians)
    {
        while (radians > MathF.PI)
        {
            radians -= MathF.PI * 2f;
        }

        while (radians < -MathF.PI)
        {
            radians += MathF.PI * 2f;
        }

        return radians;
    }

    private Matrix4x4 ResolveFineTerrainBaseCompositeTransform(
        FineTerrainWorldScale worldScale,
        FineTerrainBaseVisualItem item,
        SimulationEntity? entity,
        bool includeSlide)
    {
        Matrix4x4 baseTransform;
        if (TryResolveFineTerrainCompositePoseOverride(
            item.CompositeId,
            out Vector3 pivotModel,
            out Vector3 positionModel,
            out Vector3 rotationYprDegrees))
        {
            baseTransform = ResolveFineTerrainCompositeBaseTransform(pivotModel, positionModel, rotationYprDegrees);
        }
        else
        {
            baseTransform = ResolveFineTerrainCompositeBaseTransform(item.PivotModel, item.PositionModel, item.RotationYprDegrees);
        }

        if (!includeSlide || entity is null || !entity.IsAlive)
        {
            return baseTransform;
        }

        if (!TryResolveFineTerrainBaseReferenceUnit(item, out FineTerrainBaseUnitVisualItem? referenceUnit)
            || referenceUnit is null)
        {
            return baseTransform;
        }

        Vector3 slideAxisModel = ResolveFineTerrainBaseSlideAxisModel(referenceUnit);
        float slideM = ResolveBaseTopArmorSlideM(_host.World.GameTimeSec);
        if (MathF.Abs(slideM) <= 1e-5f)
        {
            return baseTransform;
        }

        float slideModelUnits = ResolveFineTerrainModelDistanceForMeters(worldScale, slideAxisModel, slideM);
        if (MathF.Abs(slideModelUnits) <= 1e-5f)
        {
            return baseTransform;
        }

        return Matrix4x4.CreateTranslation(slideAxisModel * slideModelUnits) * baseTransform;
    }

    private bool TryResolveFineTerrainBaseTopSlideTarget(
        SimulationEntity entity,
        out ArmorPlateTarget plate)
    {
        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double baseYawDeg = SimulationCombatMath.NormalizeDeg(entity.AngleDeg);
        double baseYawRad = baseYawDeg * Math.PI / 180.0;
        double sideYawRad = baseYawRad + Math.PI * 0.5;
        double metersToWorld = 1.0 / metersPerWorldUnit;
        double bodyLength = Math.Clamp(entity.BodyLengthM, 1.10, 2.35);
        double bodyHeight = Math.Clamp(entity.BodyHeightM, 0.70, 1.60);
        double slideM = ResolveBaseTopArmorSlideM(_host.World.GameTimeSec);
        double topForwardM = bodyLength * 0.06;
        double armorSideLengthM = Math.Clamp(Math.Max(entity.ArmorPlateWidthM, entity.ArmorPlateHeightM), 0.04, 0.60);
        double heightM = entity.GroundHeightM
            + Math.Max(
                0.05,
                entity.StructureTopArmorCenterHeightM > 1e-6
                    ? entity.StructureTopArmorCenterHeightM
                    : bodyHeight * (BaseTopArmorCenterHeightM / BaseDiagramHeightM));
        plate = new ArmorPlateTarget(
            "base_top_slide",
            entity.X + (Math.Cos(baseYawRad) * (topForwardM + entity.StructureTopArmorOffsetXM)
                + Math.Cos(sideYawRad) * (slideM + entity.StructureTopArmorOffsetZM)) * metersToWorld,
            entity.Y + (Math.Sin(baseYawRad) * (topForwardM + entity.StructureTopArmorOffsetXM)
                + Math.Sin(sideYawRad) * (slideM + entity.StructureTopArmorOffsetZM)) * metersToWorld,
            heightM,
            baseYawDeg,
            armorSideLengthM);
        return true;
    }

    private static Vector3 ResolveFineTerrainSceneTranslationToModel(
        FineTerrainWorldScale worldScale,
        Vector3 sceneTranslation)
    {
        float dx = MathF.Abs(worldScale.XMetersPerModelUnit) <= 1e-6f
            ? 0f
            : -sceneTranslation.X / worldScale.XMetersPerModelUnit;
        float dy = MathF.Abs(worldScale.YMetersPerModelUnit) <= 1e-6f
            ? 0f
            : sceneTranslation.Y / worldScale.YMetersPerModelUnit;
        float dz = MathF.Abs(worldScale.ZMetersPerModelUnit) <= 1e-6f
            ? 0f
            : -sceneTranslation.Z / worldScale.ZMetersPerModelUnit;
        return new Vector3(dx, dy, dz);
    }

    private static Vector3 ResolveFineTerrainBaseSlideAxisModel(FineTerrainBaseUnitVisualItem referenceUnit)
    {
        Vector3 normalModel = referenceUnit.LocalNormalModel;
        Vector3 projectedNormal = new(normalModel.X, 0f, normalModel.Z);
        if (projectedNormal.LengthSquared() <= 1e-8f)
        {
            return Vector3.UnitZ;
        }

        projectedNormal = Vector3.Normalize(projectedNormal);
        Vector3 slideAxis = Vector3.Cross(Vector3.UnitY, projectedNormal);
        if (slideAxis.LengthSquared() <= 1e-8f)
        {
            slideAxis = Vector3.UnitZ;
        }

        return Vector3.Normalize(slideAxis);
    }

    private static float ResolveFineTerrainModelDistanceForMeters(
        FineTerrainWorldScale worldScale,
        Vector3 axisModel,
        float distanceM)
    {
        float metersPerModelUnit = MathF.Sqrt(
            axisModel.X * axisModel.X * worldScale.XMetersPerModelUnit * worldScale.XMetersPerModelUnit
            + axisModel.Y * axisModel.Y * worldScale.YMetersPerModelUnit * worldScale.YMetersPerModelUnit
            + axisModel.Z * axisModel.Z * worldScale.ZMetersPerModelUnit * worldScale.ZMetersPerModelUnit);
        if (metersPerModelUnit <= 1e-6f)
        {
            return 0f;
        }

        return distanceM / metersPerModelUnit;
    }

    private Matrix4x4 ResolveFineTerrainCompositeBaseTransform(FineTerrainOutpostVisualItem item)
    {
        if (TryResolveFineTerrainCompositePoseOverride(
            item.CompositeId,
            out Vector3 pivotModel,
            out Vector3 positionModel,
            out Vector3 rotationYprDegrees))
        {
            return ResolveFineTerrainCompositeBaseTransform(pivotModel, positionModel, rotationYprDegrees);
        }

        return ResolveFineTerrainCompositeBaseTransform(item.PivotModel, item.PositionModel, item.RotationYprDegrees);
    }

    private static Matrix4x4 ResolveFineTerrainCompositeBaseTransform(
        Vector3 pivotModel,
        Vector3 positionModel,
        Vector3 rotationYprDegrees)
    {
        float yaw = rotationYprDegrees.X * MathF.PI / 180f;
        float pitch = rotationYprDegrees.Y * MathF.PI / 180f;
        float roll = rotationYprDegrees.Z * MathF.PI / 180f;
        return
            Matrix4x4.CreateTranslation(-pivotModel)
            * Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll)
            * Matrix4x4.CreateTranslation(positionModel);
    }

    private void PreloadFineTerrainVisualScenes()
    {
        string cacheKey = BuildFineTerrainSceneCacheKey();
        StartFineTerrainEnergySceneLoad(cacheKey);
        StartFineTerrainOutpostSceneLoad(cacheKey);
        StartFineTerrainBaseSceneLoad(cacheKey);
    }

    private double ResolveFineTerrainVisualSceneLoadProgress()
    {
        if (string.IsNullOrWhiteSpace(_host.MapPreset.AnnotationPath) || !File.Exists(_host.MapPreset.AnnotationPath))
        {
            return 1.0;
        }

        string cacheKey = BuildFineTerrainSceneCacheKey();
        StartFineTerrainEnergySceneLoad(cacheKey);
        StartFineTerrainOutpostSceneLoad(cacheKey);
        StartFineTerrainBaseSceneLoad(cacheKey);
        CompleteFineTerrainEnergySceneLoad(cacheKey);
        CompleteFineTerrainOutpostSceneLoad(cacheKey);
        CompleteFineTerrainBaseSceneLoad(cacheKey);

        double energy = _fineTerrainEnergySceneLoadTask is null ? 1.0 : 0.35;
        double outpost = _fineTerrainOutpostSceneLoadTask is null ? 1.0 : 0.35;
        double baseTop = _fineTerrainBaseSceneLoadTask is null ? 1.0 : 0.35;
        return (energy + outpost + baseTop) / 3.0;
    }

    private bool AreFineTerrainVisualScenesReady()
    {
        if (string.IsNullOrWhiteSpace(_host.MapPreset.AnnotationPath) || !File.Exists(_host.MapPreset.AnnotationPath))
        {
            return true;
        }

        string cacheKey = BuildFineTerrainSceneCacheKey();
        StartFineTerrainEnergySceneLoad(cacheKey);
        StartFineTerrainOutpostSceneLoad(cacheKey);
        StartFineTerrainBaseSceneLoad(cacheKey);
        CompleteFineTerrainEnergySceneLoad(cacheKey);
        CompleteFineTerrainOutpostSceneLoad(cacheKey);
        CompleteFineTerrainBaseSceneLoad(cacheKey);
        return _fineTerrainEnergySceneLoadTask is null
            && _fineTerrainOutpostSceneLoadTask is null
            && _fineTerrainBaseSceneLoadTask is null;
    }

    private FineTerrainEnergyMechanismVisualScene? ResolveFineTerrainEnergyScene()
    {
        string cacheKey = BuildFineTerrainSceneCacheKey();
        ResetFineTerrainEnergyBodyMeshCache(cacheKey);
        ResetFineTerrainEnergyStripMeshCache(cacheKey);
        ResetFineTerrainEnergyUnitMeshCache(cacheKey);
        CompleteFineTerrainEnergySceneLoad(cacheKey);
        if (_fineTerrainEnergyScene is not null
            && string.Equals(_fineTerrainEnergySceneKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return _fineTerrainEnergyScene;
        }

        StartFineTerrainEnergySceneLoad(cacheKey);
        return null;
    }

    private FineTerrainOutpostVisualScene? ResolveFineTerrainOutpostScene()
    {
        string cacheKey = BuildFineTerrainSceneCacheKey();
        ResetFineTerrainOutpostBodyMeshCache(cacheKey);
        ResetFineTerrainOutpostUnitMeshCache(cacheKey);
        CompleteFineTerrainOutpostSceneLoad(cacheKey);
        if (_fineTerrainOutpostScene is not null
            && string.Equals(_fineTerrainOutpostSceneKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return _fineTerrainOutpostScene;
        }

        StartFineTerrainOutpostSceneLoad(cacheKey);
        return null;
    }

    private FineTerrainBaseVisualScene? ResolveFineTerrainBaseScene()
    {
        string cacheKey = BuildFineTerrainSceneCacheKey();
        ResetFineTerrainBaseBodyMeshCache(cacheKey);
        ResetFineTerrainBaseUnitMeshCache(cacheKey);
        CompleteFineTerrainBaseSceneLoad(cacheKey);
        if (_fineTerrainBaseScene is not null
            && string.Equals(_fineTerrainBaseSceneKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return _fineTerrainBaseScene;
        }

        StartFineTerrainBaseSceneLoad(cacheKey);
        return null;
    }

    private string BuildFineTerrainSceneCacheKey()
    {
        string annotationPath = _host.MapPreset.AnnotationPath;
        string terrainKey = _host.MapPreset.RuntimeGrid?.SourcePath ?? string.Empty;
        return $"{annotationPath}|{terrainKey}";
    }

    private void StartFineTerrainEnergySceneLoad(string cacheKey)
    {
        if (_fineTerrainEnergyScene is not null
            && string.Equals(_fineTerrainEnergySceneKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_fineTerrainEnergySceneLoadTask is not null
            && string.Equals(_fineTerrainEnergySceneLoadingKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MapPresetDefinition preset = _host.MapPreset;
        _fineTerrainEnergySceneLoadTask = Task.Run(() => FineTerrainEnergyMechanismVisualCache.TryLoad(preset));
        _fineTerrainEnergySceneLoadingKey = cacheKey;
    }

    private void StartFineTerrainOutpostSceneLoad(string cacheKey)
    {
        if (_fineTerrainOutpostScene is not null
            && string.Equals(_fineTerrainOutpostSceneKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_fineTerrainOutpostSceneLoadTask is not null
            && string.Equals(_fineTerrainOutpostSceneLoadingKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MapPresetDefinition preset = _host.MapPreset;
        _fineTerrainOutpostSceneLoadTask = Task.Run(() => FineTerrainOutpostVisualCache.TryLoad(preset));
        _fineTerrainOutpostSceneLoadingKey = cacheKey;
    }

    private void StartFineTerrainBaseSceneLoad(string cacheKey)
    {
        if (_fineTerrainBaseScene is not null
            && string.Equals(_fineTerrainBaseSceneKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_fineTerrainBaseSceneLoadTask is not null
            && string.Equals(_fineTerrainBaseSceneLoadingKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MapPresetDefinition preset = _host.MapPreset;
        _fineTerrainBaseSceneLoadTask = Task.Run(() => FineTerrainBaseVisualCache.TryLoad(preset));
        _fineTerrainBaseSceneLoadingKey = cacheKey;
    }

    private void CompleteFineTerrainEnergySceneLoad(string cacheKey)
    {
        if (_fineTerrainEnergySceneLoadTask is null
            || !string.Equals(_fineTerrainEnergySceneLoadingKey, cacheKey, StringComparison.OrdinalIgnoreCase)
            || !_fineTerrainEnergySceneLoadTask.IsCompleted)
        {
            return;
        }

        try
        {
            _fineTerrainEnergyScene = _fineTerrainEnergySceneLoadTask.Result;
            _fineTerrainEnergySceneKey = cacheKey;
        }
        catch
        {
            _fineTerrainEnergyScene = null;
            _fineTerrainEnergySceneKey = cacheKey;
        }
        finally
        {
            _fineTerrainEnergySceneLoadTask = null;
            _fineTerrainEnergySceneLoadingKey = null;
        }
    }

    private void CompleteFineTerrainOutpostSceneLoad(string cacheKey)
    {
        if (_fineTerrainOutpostSceneLoadTask is null
            || !string.Equals(_fineTerrainOutpostSceneLoadingKey, cacheKey, StringComparison.OrdinalIgnoreCase)
            || !_fineTerrainOutpostSceneLoadTask.IsCompleted)
        {
            return;
        }

        try
        {
            _fineTerrainOutpostScene = _fineTerrainOutpostSceneLoadTask.Result;
            _fineTerrainOutpostSceneKey = cacheKey;
        }
        catch
        {
            _fineTerrainOutpostScene = null;
            _fineTerrainOutpostSceneKey = cacheKey;
        }
        finally
        {
            _fineTerrainOutpostSceneLoadTask = null;
            _fineTerrainOutpostSceneLoadingKey = null;
        }
    }

    private void CompleteFineTerrainBaseSceneLoad(string cacheKey)
    {
        if (_fineTerrainBaseSceneLoadTask is null
            || !string.Equals(_fineTerrainBaseSceneLoadingKey, cacheKey, StringComparison.OrdinalIgnoreCase)
            || !_fineTerrainBaseSceneLoadTask.IsCompleted)
        {
            return;
        }

        try
        {
            _fineTerrainBaseScene = _fineTerrainBaseSceneLoadTask.Result;
            _fineTerrainBaseSceneKey = cacheKey;
        }
        catch
        {
            _fineTerrainBaseScene = null;
            _fineTerrainBaseSceneKey = cacheKey;
        }
        finally
        {
            _fineTerrainBaseSceneLoadTask = null;
            _fineTerrainBaseSceneLoadingKey = null;
        }
    }
}
