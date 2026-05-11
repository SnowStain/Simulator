using System.Numerics;
using System.Drawing.Drawing2D;
using System.Globalization;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const double FineTerrainEnergyDoubleFlashDurationSec = 0.80;
    private const double FineTerrainEnergyCompletionFlashDurationSec = 3.20;
    private const double FineTerrainEnergyFlashIntervalSec = 0.20;
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
    private FineTerrainAnnotationDocument? _fineTerrainCollisionAnnotation;
    private string? _fineTerrainCollisionAnnotationKey;
    private Task<FineTerrainAnnotationDocument?>? _fineTerrainCollisionAnnotationLoadTask;
    private string? _fineTerrainCollisionAnnotationLoadingKey;
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
                    unit.RingScore,
                    worldNormal.X,
                    worldNormal.Y,
                    worldNormal.Z));
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
                entity.RuntimeOutpostTargetsGameTimeSec = double.NaN;
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
            entity.RuntimeOutpostTargetsGameTimeSec = _host.World.GameTimeSec;
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

            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                if (unit.IsLightStrip)
                {
                    continue;
                }

                Matrix4x4 unitTransform = ResolveFineTerrainBaseUnitTransform(
                    scene.WorldScale,
                    item,
                    unit,
                    transform,
                    entity);
                Vector3 centerModel = Vector3.Transform(unit.LocalCentroidModel, unitTransform);
                (double worldX, double worldY, double heightM) = ModelPointToWorld(centerModel, scene.WorldScale);
                worldX += sceneAlignmentOffset.X / Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
                worldY += sceneAlignmentOffset.Z / Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
                heightM += sceneAlignmentOffset.Y;
                Vector3 normalTipModel = Vector3.Transform(unit.LocalCentroidModel + unit.LocalNormalModel, unitTransform);
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
                    unit.HeightSpanM,
                    NormalXM: worldNormal.X,
                    NormalYM: worldNormal.Y,
                    NormalZM: worldNormal.Z));
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
                Vector3 itemCenter = ModelToScenePoint(Vector3.Transform(item.PivotModel, compositeTransform), scene.WorldScale) + sceneAlignmentOffset;
                bool drawDynamicEffects = ShouldDrawFineTerrainEnergyDynamicEffects(itemCenter);
                drewFineBody |= item.Triangles.Count > 0;
                DrawFineTerrainEnergyTrianglesGdi(graphics, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
                DrawFineTerrainEnergyBodyStripTriangles(graphics, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
                DrawFineTerrainEnergyUnitTriangles(graphics, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset, drawDynamicEffects);
                if (drawDynamicEffects)
                {
                    DrawFineTerrainEnergyInteractionFeedback(graphics, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
                }
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
            Vector3 itemCenter = ModelToScenePoint(Vector3.Transform(item.PivotModel, compositeTransform), scene.WorldScale) + sceneAlignmentOffset;
            if (!IsFineTerrainItemPotentiallyVisible(
                    itemCenter,
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
            bool drawDynamicEffects = ShouldDrawFineTerrainEnergyDynamicEffects(itemCenter);
            DrawFineTerrainEnergyUnitTriangles(null, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset, drawDynamicEffects);
            if (drawDynamicEffects)
            {
                DrawFineTerrainEnergyInteractionFeedback(null, scene.WorldScale, item, compositeTransform, sceneAlignmentOffset);
            }
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
            if (entity is not null)
            {
                plates = SimulationCombatMath.GetArmorPlateTargets(
                    entity,
                    _host.World.MetersPerWorldUnit,
                    _host.World.GameTimeSec,
                    includeOutpostTopArmor: true);
                transform = ResolveFineTerrainOutpostCompositeTransform(item, entity);
                sceneAlignmentOffset = ResolveFineTerrainOutpostSceneAlignmentOffset(scene.WorldScale, entity, item, plates);
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
                if (!TryDrawGpuFineTerrainOutpostBody(scene.WorldScale, item, transform, sceneAlignmentOffset))
                {
                    DrawFineTerrainColoredTriangles(
                        null,
                        scene.WorldScale,
                        item.Triangles,
                        transform,
                        sceneAlignmentOffset);
                }

                drawn = true;
            }

            foreach (FineTerrainOutpostUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                Color? flashOverride = ResolveStructurePlateFlashOverride(entity, unit.PlateId);
                if (flashOverride is null
                    && TryDrawGpuFineTerrainUnitMesh(
                    _fineTerrainOutpostUnitMeshCache,
                    _fineTerrainOutpostSceneKey ?? string.Empty,
                    $"{item.Team}|{item.Name}|{unit.Name}",
                    scene.WorldScale,
                    item.PivotModel,
                    unit.Triangles,
                    transform,
                    sceneAlignmentOffset,
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
                    Vector3.Zero,
                    flashOverride);
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
                    Vector3.Zero);
                drawn = true;
            }

            foreach (FineTerrainOutpostUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    unit.Triangles,
                    transform,
                    Vector3.Zero);
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
            Matrix4x4 compositeTransform = ResolveFineTerrainBaseCompositeTransform(scene.WorldScale, item, entity, includeSlide: true);
            Vector3 sceneAlignmentOffset = Vector3.Zero;
            bool outerPanelComposite = FineTerrainBaseVisualCache.IsBaseOuterPanelCompositeName(item.Name);
            if (!IsFineTerrainItemPotentiallyVisible(
                    ModelToScenePoint(Vector3.Transform(item.PivotModel, compositeTransform), scene.WorldScale) + sceneAlignmentOffset,
                    outerPanelComposite ? 1.55f : 1.15f,
                    1.0f))
            {
                continue;
            }

            if (item.Triangles.Count > 0)
            {
                if (outerPanelComposite && entity is not null)
                {
                    Matrix4x4 outerPanelTransform = ResolveFineTerrainBaseOuterPanelOpenTransform(
                        scene.WorldScale,
                        item,
                        entity,
                        compositeTransform);
                    if (!TryDrawGpuFineTerrainBaseBody(scene.WorldScale, item, outerPanelTransform, sceneAlignmentOffset))
                    {
                        DrawFineTerrainColoredTriangles(
                            null,
                            scene.WorldScale,
                            item.Triangles,
                            outerPanelTransform,
                            sceneAlignmentOffset);
                    }
                }
                else if (!TryDrawGpuFineTerrainBaseBody(scene.WorldScale, item, compositeTransform, sceneAlignmentOffset))
                {
                    DrawFineTerrainColoredTriangles(
                        null,
                        scene.WorldScale,
                        item.Triangles,
                        compositeTransform,
                        sceneAlignmentOffset);
                }
                drawn = true;
            }

            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                Matrix4x4 unitTransform = entity is null
                    ? compositeTransform
                    : ResolveFineTerrainBaseUnitTransform(
                        scene.WorldScale,
                        item,
                        unit,
                        compositeTransform,
                        entity);
                Color? flashOverride = ResolveStructurePlateFlashOverride(entity, unit.PlateId);
                if (flashOverride is null
                    && TryDrawGpuFineTerrainUnitMesh(
                    _fineTerrainBaseUnitMeshCache,
                    _fineTerrainBaseSceneKey ?? string.Empty,
                    $"{item.Team}|{item.Name}|{unit.Name}",
                    scene.WorldScale,
                    item.PivotModel,
                    unit.Triangles,
                    unitTransform,
                    sceneAlignmentOffset,
                    Vector3.Zero))
                {
                    drawn = true;
                    continue;
                }

                DrawFineTerrainColoredTriangles(
                    null,
                    scene.WorldScale,
                    unit.Triangles,
                    unitTransform,
                    sceneAlignmentOffset,
                    Vector3.Zero,
                    flashOverride);
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
            Matrix4x4 compositeTransform = ResolveFineTerrainBaseCompositeTransform(scene.WorldScale, item, entity, includeSlide: true);
            Vector3 sceneAlignmentOffset = Vector3.Zero;
            bool outerPanelComposite = FineTerrainBaseVisualCache.IsBaseOuterPanelCompositeName(item.Name);
            if (item.Triangles.Count > 0)
            {
                if (outerPanelComposite && entity is not null)
                {
                    Matrix4x4 outerPanelTransform = ResolveFineTerrainBaseOuterPanelOpenTransform(
                        scene.WorldScale,
                        item,
                        entity,
                        compositeTransform);
                    DrawFineTerrainColoredTriangles(
                        graphics,
                        scene.WorldScale,
                        item.Triangles,
                        outerPanelTransform,
                        sceneAlignmentOffset);
                }
                else
                {
                    DrawFineTerrainColoredTriangles(
                        graphics,
                        scene.WorldScale,
                        item.Triangles,
                        compositeTransform,
                        sceneAlignmentOffset);
                }
                drawn = true;
            }

            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                if (unit.Triangles.Count == 0)
                {
                    continue;
                }

                Matrix4x4 unitTransform = entity is null
                    ? compositeTransform
                    : ResolveFineTerrainBaseUnitTransform(
                        scene.WorldScale,
                        item,
                        unit,
                        compositeTransform,
                        entity);
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    unit.Triangles,
                    unitTransform,
                    sceneAlignmentOffset);
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
        Color teamColor = ResolveTeamColor(item.Team);
        bool completionFlashBlack = IsFineTerrainEnergyCompletionFlashBlack(_host.World.GameTimeSec, teamState);
        Color litColor = completionFlashBlack ? Color.FromArgb(255, 8, 9, 11) : Color.FromArgb(255, teamColor);
        Color darkColor = Color.FromArgb(255, 8, 9, 11);

        if (_gpuGeometryPass && UseGpuRenderer)
        {
            if (TryDrawGpuFineTerrainEnergyStripMesh(
                worldScale,
                item,
                compositeTransform,
                sceneAlignmentOffset,
                activatedRatio,
                litColor,
                darkColor))
            {
                return;
            }

            foreach ((FineTerrainColoredTriangle triangle, float progress) in ResolveFineTerrainEnergyStripTriangles(item))
            {
                Color fill = progress <= activatedRatio + 1e-4f
                    ? litColor
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
        foreach ((FineTerrainColoredTriangle triangle, float progress) in ResolveFineTerrainEnergyStripTriangles(item))
        {
            Color fill = progress <= activatedRatio + 1e-4f
                ? litColor
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

        int ratioKey = Math.Clamp((int)MathF.Round(activatedRatio * 64f), 0, 64);
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
        foreach ((FineTerrainColoredTriangle triangle, float progress) in ResolveFineTerrainEnergyStripTriangles(item))
        {
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

    private IReadOnlyList<(FineTerrainColoredTriangle Triangle, float Progress)> ResolveFineTerrainEnergyStripTriangles(
        FineTerrainEnergyMechanismVisualItem item)
    {
        string sceneKey = _fineTerrainEnergyStripMeshSceneKey ?? _fineTerrainEnergySceneKey ?? string.Empty;
        string cacheKey = $"{sceneKey}|{item.CompositeId}|{item.Team}|{item.Triangles.Count}";
        if (_fineTerrainEnergyStripTriangleCache.TryGetValue(cacheKey, out List<(FineTerrainColoredTriangle Triangle, float Progress)>? cached))
        {
            return cached;
        }

        cached = new List<(FineTerrainColoredTriangle Triangle, float Progress)>(4096);
        foreach (FineTerrainColoredTriangle triangle in item.Triangles)
        {
            if (TryResolveFineTerrainEnergyStripProgress(item, triangle, out float progress))
            {
                cached.Add((triangle, progress));
            }
        }

        _fineTerrainEnergyStripTriangleCache[cacheKey] = cached;
        return cached;
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
        if (!ShouldRenderGeneratedEnergyMechanismRingOverlays())
        {
            return;
        }
        // Ring highlight feedback is folded into the ring-unit pass so the lit
        // color replaces the dark authored ring instead of stacking over it.
    }

    private bool TryResolveFineTerrainEnergyRingHighlightColor(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit,
        out Color color)
    {
        color = default;
        if (unit.Kind != FineTerrainEnergyUnitKind.Ring
            || unit.RingScore <= 0
            || !_host.World.Teams.TryGetValue(item.Team, out SimulationTeamState? teamState))
        {
            return false;
        }

        Color teamColor = ResolveMapTeamLineColor(item.Team);
        int persistentRingScore = unit.ArmIndex >= 0 && unit.ArmIndex < teamState.EnergyHitRingsByArm.Length
            ? Math.Clamp(teamState.EnergyHitRingsByArm[unit.ArmIndex], 0, 10)
            : 0;
        bool hitFlashing = teamState.EnergyLastHitArmIndex == unit.ArmIndex
            && teamState.EnergyLastRingScore == unit.RingScore
            && _host.World.GameTimeSec <= teamState.EnergyLastHitFlashEndSec;

        if (persistentRingScore <= 0 || persistentRingScore != unit.RingScore)
        {
            return false;
        }

        color = ResolveEnergyMechanismRingLitColor(teamColor, emphasized: hitFlashing || persistentRingScore >= 7);
        return true;
    }

    private static FineTerrainEnergyMechanismUnitVisualItem? ResolveFineTerrainEnergyRingUnit(
        FineTerrainEnergyMechanismVisualItem item,
        int armIndex,
        int preferredRingScore)
        => item.Units
            .Where(candidate => candidate.Kind == FineTerrainEnergyUnitKind.Ring && candidate.ArmIndex == armIndex)
            .OrderBy(candidate =>
            {
                int score = candidate.RingScore <= 0 ? 1 : candidate.RingScore;
                return Math.Abs(score - Math.Max(1, preferredRingScore));
            })
            .ThenBy(candidate => candidate.RingScore <= 0 ? int.MaxValue : candidate.RingScore)
            .FirstOrDefault();

    private void DrawFineTerrainEnergyRingOverlayTriangles(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem ringUnit,
        Matrix4x4 transform,
        Vector3 sceneAlignmentOffset,
        Color ringColor)
    {
        if (ringUnit.Triangles.Count == 0)
        {
            return;
        }

        Color fill = IsDarkFlashColor(ringColor)
            ? Color.FromArgb(248, 2, 3, 4)
            : Color.FromArgb(255, ringColor.R, ringColor.G, ringColor.B);
        Vector3 extraSceneOffset = ResolveFineTerrainEnergyUnitSceneLift(ringUnit, worldScale, transform)
            + ResolveFineTerrainEnergyUnitNormalOffset(ringUnit, worldScale, transform, 0.0180f);
        ResolveFineTerrainEnergyRingOverlayPose(
            item,
            ringUnit,
            worldScale,
            transform,
            sceneAlignmentOffset,
            extraSceneOffset,
            out Vector3 ringCenter,
            out Vector3 ringNormal,
            out Vector3 ringUp,
            out float innerRadius,
            out float outerRadius);

        if (_gpuGeometryPass && UseGpuRenderer)
        {
            foreach (FineTerrainColoredTriangle triangle in ringUnit.Triangles)
            {
                Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
                Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
                Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
                AppendOrDrawGpuTriangle(a, b, c, fill);
                AppendOrDrawGpuTriangle(a, c, b, fill);
            }

            return;
        }

        if (graphics is null)
        {
            return;
        }

        var faces = new List<ProjectedFace>(Math.Min(ringUnit.Triangles.Count, 384));
        foreach (FineTerrainColoredTriangle triangle in ringUnit.Triangles)
        {
            Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
            Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
            Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
            if (TryBuildProjectedFace(new[] { a, b, c }, fill, fill, out ProjectedFace face))
            {
                faces.Add(face);
            }
        }

        if (faces.Count > 0)
        {
            DrawProjectedFaceBatch(graphics, faces, 0.98f);
        }

    }

    private void ResolveFineTerrainEnergyRingOverlayPose(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem ringUnit,
        FineTerrainWorldScale worldScale,
        Matrix4x4 transform,
        Vector3 sceneAlignmentOffset,
        Vector3 extraSceneOffset,
        out Vector3 center,
        out Vector3 normal,
        out Vector3 upAxis,
        out float innerRadius,
        out float outerRadius)
    {
        center = ModelToScenePoint(Vector3.Transform(ringUnit.LocalCenterModel, transform), worldScale)
            + sceneAlignmentOffset
            + extraSceneOffset;
        Vector3 normalTipScene = ModelToScenePoint(
            Vector3.Transform(ringUnit.LocalCenterModel + ringUnit.LocalNormalModel, transform),
            worldScale) + sceneAlignmentOffset;
        Vector3 sceneNormal = normalTipScene - center;
        normal = sceneNormal.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(sceneNormal);

        Vector3 pivotScene = ModelToScenePoint(Vector3.Transform(item.PivotModel, transform), worldScale) + sceneAlignmentOffset;
        Vector3 radial = center - pivotScene;
        Vector3 tangent = radial - normal * Vector3.Dot(radial, normal);
        if (tangent.LengthSquared() <= 1e-8f)
        {
            tangent = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.96f
                ? Vector3.UnitX
                : Vector3.UnitY;
            tangent -= normal * Vector3.Dot(tangent, normal);
        }

        upAxis = tangent.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(tangent);
        float measuredRadius = Math.Max(0.020f, (float)Math.Max(ringUnit.WidthM, ringUnit.HeightSpanM) * 0.54f);
        float bandWidth = Math.Clamp(measuredRadius * 0.34f, 0.014f, 0.045f);
        outerRadius = measuredRadius + bandWidth * 0.18f;
        innerRadius = Math.Max(0.002f, outerRadius - bandWidth);
    }

    private static bool IsDarkFlashColor(Color color)
        => color.R <= 16 && color.G <= 16 && color.B <= 16;

    private static Color ResolveEnergyMechanismRingLitColor(Color teamColor, bool emphasized)
    {
        Color tinted = BlendColor(teamColor, Color.White, emphasized ? 0.58f : 0.38f);
        return Color.FromArgb(255, tinted);
    }

    private static Color ResolveEnergyMechanismPendingDiskColor(Color teamColor)
    {
        return Color.FromArgb(255, ResolveEnergyMechanismPureLightFromColor(teamColor));
    }

    private static Color ResolveEnergyMechanismPureLightFromColor(Color color)
        => color.R >= color.B
            ? Color.FromArgb(255, 255, 0, 0)
            : Color.FromArgb(255, 0, 0, 255);

    private static Color ResolveEnergyMechanismRingFlashColor(Color teamColor)
    {
        Color tinted = BlendColor(teamColor, Color.Black, 0.24f);
        return Color.FromArgb(242, tinted);
    }

    private static bool IsFineTerrainEnergyHitFlashBlack(double currentTimeSec, double flashEndTimeSec)
    {
        double remainingSec = flashEndTimeSec - currentTimeSec;
        if (remainingSec <= 1e-6)
        {
            return false;
        }

        double clampedRemainingSec = Math.Min(FineTerrainEnergyDoubleFlashDurationSec, remainingSec);
        double elapsedSec = FineTerrainEnergyDoubleFlashDurationSec - clampedRemainingSec;
        int phaseIndex = Math.Clamp((int)(elapsedSec / FineTerrainEnergyFlashIntervalSec), 0, 3);
        return phaseIndex is 0 or 2;
    }

    private static bool IsFineTerrainEnergyCompletionFlashBlack(double currentTimeSec, SimulationTeamState teamState)
    {
        return false;
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

    private void DrawFineTerrainEnergyUnitTriangles(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset,
        bool drawDynamicEffects = true)
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

            if (drawDynamicEffects
                && TryDrawFineTerrainEnergyPendingDiskPattern(
                    graphics,
                    worldScale,
                    item,
                    unit,
                    compositeTransform,
                    sceneAlignmentOffset))
            {
                continue;
            }

            Vector3 extraSceneOffset = Vector3.Zero;
            Color? overrideColor = TryResolveFineTerrainEnergyRingHighlightColor(item, unit, out Color litColor)
                ? litColor
                : ResolveFineTerrainEnergyLightArmColor(item, unit);

            bool pendingStripFlow = ShouldDrawFineTerrainEnergyPendingStripFlow(item, unit);
            if (drawDynamicEffects
                && !pendingStripFlow
                && TryDrawFineTerrainEnergyLightArmProgressUnit(
                    graphics,
                    worldScale,
                    item,
                    unit,
                    compositeTransform,
                    sceneAlignmentOffset))
            {
                TryDrawFineTerrainEnergyPendingStripFlow(
                    graphics,
                    worldScale,
                    item,
                    unit,
                    compositeTransform,
                    sceneAlignmentOffset);
                continue;
            }

            if (graphics is null
                && TryDrawGpuFineTerrainUnitMesh(
                    _fineTerrainEnergyUnitMeshCache,
                    _fineTerrainEnergySceneKey ?? string.Empty,
                    $"{item.CompositeId}|{unit.Name}",
                    worldScale,
                    item.PivotModel,
                    unit.Triangles,
                    compositeTransform,
                    sceneAlignmentOffset,
                    extraSceneOffset,
                    overrideColor))
            {
                if (drawDynamicEffects)
                {
                    TryDrawFineTerrainEnergyPendingStripFlow(
                        graphics,
                        worldScale,
                        item,
                        unit,
                        compositeTransform,
                        sceneAlignmentOffset);
                }
                continue;
            }

            DrawFineTerrainColoredTriangles(
                graphics,
                worldScale,
                unit.Triangles,
                compositeTransform,
                sceneAlignmentOffset,
                extraSceneOffset,
                overrideColor);
            if (drawDynamicEffects)
            {
                TryDrawFineTerrainEnergyPendingStripFlow(
                    graphics,
                    worldScale,
                    item,
                    unit,
                    compositeTransform,
                    sceneAlignmentOffset);
            }
        }
    }

    private bool ShouldDrawFineTerrainEnergyDynamicEffects(Vector3 itemCenterScene)
    {
        float distanceSq = Vector3.DistanceSquared(_cameraPositionM, itemCenterScene);
        if (distanceSq > 13.5f * 13.5f)
        {
            return false;
        }

        if (!TryProject(itemCenterScene, out PointF center, out _)
            || !TryProject(itemCenterScene + Vector3.UnitY * 1.25f, out PointF sample, out _))
        {
            return false;
        }

        float projectedRadiusPx = MathF.Abs(sample.Y - center.Y);
        return projectedRadiusPx >= 96f;
    }

    private bool TryDrawFineTerrainEnergyPendingDiskPattern(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        if (unit.Kind != FineTerrainEnergyUnitKind.Ring
            || unit.ArmIndex < 0
            || !_host.World.Teams.TryGetValue(item.Team, out SimulationTeamState? teamState)
            || !string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            || teamState.EnergyCurrentLitMask == 0
            || (teamState.EnergyCurrentLitMask & (1 << unit.ArmIndex)) == 0)
        {
            return false;
        }

        int persistentRingScore = unit.ArmIndex < teamState.EnergyHitRingsByArm.Length
            ? Math.Clamp(teamState.EnergyHitRingsByArm[unit.ArmIndex], 0, 10)
            : 0;
        if (persistentRingScore > 0)
        {
            return false;
        }

        FineTerrainEnergyMechanismUnitVisualItem? centerUnit = ResolveFineTerrainEnergyRingUnit(item, unit.ArmIndex, preferredRingScore: 10);
        if (!ReferenceEquals(centerUnit, unit))
        {
            return false;
        }

        FineTerrainEnergyMechanismUnitVisualItem radiusUnit =
            ResolveFineTerrainEnergyRingUnit(item, unit.ArmIndex, preferredRingScore: 1) ?? unit;
        Vector3 extraSceneOffset = ResolveFineTerrainEnergyUnitSceneLift(unit, worldScale, compositeTransform)
            + ResolveFineTerrainEnergyUnitNormalOffset(unit, worldScale, compositeTransform, 0.0060f);
        if (!TryResolveFineTerrainEnergyUnitSurfacePose(
                item,
                unit,
                worldScale,
                compositeTransform,
                sceneAlignmentOffset,
                extraSceneOffset,
                out Vector3 center,
                out Vector3 normal,
                out Vector3 upAxis,
                out float measuredRadius))
        {
            ResolveFineTerrainEnergyRingOverlayPose(
                item,
                unit,
                worldScale,
                compositeTransform,
                sceneAlignmentOffset,
                extraSceneOffset,
                out center,
                out normal,
                out upAxis,
                out _,
                out measuredRadius);
        }

        if (!ReferenceEquals(radiusUnit, unit)
            && TryResolveFineTerrainEnergyUnitSurfacePose(
                item,
                radiusUnit,
                worldScale,
                compositeTransform,
                sceneAlignmentOffset,
                extraSceneOffset,
                out _,
                out _,
                out _,
                out float radiusUnitMeasuredRadius))
        {
            measuredRadius = MathF.Max(measuredRadius, radiusUnitMeasuredRadius);
        }

        if (TryResolveFineTerrainEnergyUnitModelCenterPose(
                item,
                unit,
                worldScale,
                compositeTransform,
                sceneAlignmentOffset,
                extraSceneOffset,
                out Vector3 exactCenter,
                out _,
                out _))
        {
            center = exactCenter;
        }

        Vector3 pivotScene = ModelToScenePoint(Vector3.Transform(item.PivotModel, compositeTransform), worldScale)
            + sceneAlignmentOffset
            + extraSceneOffset;
        center = MoveFineTerrainEnergyPendingCenterTowardPivot(center, pivotScene, normal, 0.010f);
        Color teamColor = ResolveEnergyMechanismPureTeamLightColor(item.Team);
        bool readyToHit = teamState.EnergyNextModuleDelaySec <= 1e-6;
        Color pendingColor = readyToHit
            ? Color.FromArgb(255, teamColor)
            : Color.FromArgb(232, teamColor);
        float diskRadius = ResolveEnergyPendingPatternRadius(measuredRadius);
        if (_gpuGeometryPass && UseGpuRenderer)
        {
            DrawGpuEnergyPendingDiskPattern(center, normal, upAxis, diskRadius, pendingColor);
            return true;
        }

        if (graphics is null)
        {
            return false;
        }

        DrawCpuEnergyPendingDiskPattern(graphics, center, normal, upAxis, diskRadius, pendingColor);
        return true;
    }

    private void DrawFineTerrainEnergyPendingDiskPlaneArrows(
        Graphics? graphics,
        Vector3 diskCenter,
        Vector3 pivotScene,
        Vector3 normalAxis,
        Vector3 upAxis,
        float diskRadius,
        Color activeColor,
        bool hasMiddleArmBounds,
        Vector3 armCenter,
        Vector3 armNormal,
        Vector3 armOutward,
        Vector3 armLateral,
        float armMinAlong,
        float armMaxAlong,
        float armHalfWidth)
    {
        if (!hasMiddleArmBounds || armMaxAlong <= armMinAlong + 1e-4f)
        {
            return;
        }

        Vector3 normal = armNormal.LengthSquared() <= 1e-8f
            ? (normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis))
            : Vector3.Normalize(armNormal);
        Vector3 outward = diskCenter - pivotScene;
        outward -= normal * Vector3.Dot(outward, normal);
        if (outward.LengthSquared() <= 1e-8f)
        {
            outward = upAxis;
            outward -= normal * Vector3.Dot(outward, normal);
        }

        if (outward.LengthSquared() <= 1e-8f)
        {
            return;
        }

        outward = Vector3.Normalize(outward);
        Vector3 lateral = Vector3.Cross(normal, outward);
        Vector3 boundedOutward = armOutward - normal * Vector3.Dot(armOutward, normal);
        if (boundedOutward.LengthSquared() > 1e-8f)
        {
            outward = Vector3.Normalize(boundedOutward);
        }

        float minAlong = armMinAlong;
        float maxAlong = armMaxAlong;
        if (Vector3.Dot(diskCenter - armCenter, outward) < 0f)
        {
            outward = -outward;
            minAlong = -armMaxAlong;
            maxAlong = -armMinAlong;
        }

        Vector3 boundedLateral = armLateral - normal * Vector3.Dot(armLateral, normal);
        boundedLateral -= outward * Vector3.Dot(boundedLateral, outward);
        lateral = boundedLateral.LengthSquared() > 1e-8f
            ? Vector3.Normalize(boundedLateral)
            : Vector3.Cross(normal, outward);

        if (lateral.LengthSquared() <= 1e-8f)
        {
            return;
        }

        lateral = Vector3.Normalize(lateral);
        float radius = ResolveEnergyPendingPatternRadius(diskRadius);
        float edgeInset = Math.Clamp(radius * 0.070f, 0.004f, 0.016f);
        float usableMinAlong = minAlong + edgeInset;
        float usableMaxAlong = maxAlong - edgeInset;
        float usableLength = usableMaxAlong - usableMinAlong;
        if (usableLength <= 0.030f)
        {
            return;
        }

        const int chevronCount = 11;
        float chevronHalfWidth = Math.Min(
            Math.Clamp(radius * 0.064f, 0.004f, 0.012f),
            Math.Clamp(armHalfWidth * 0.24f, 0.0035f, 0.012f));
        float baseSpacing = usableLength / chevronCount;
        float chevronDepth = Math.Clamp(baseSpacing * 0.28f, 0.004f, 0.014f);
        float travelMinAlong = usableMinAlong + chevronDepth * 0.62f;
        float travelMaxAlong = usableMaxAlong - chevronDepth * 0.62f;
        if (travelMaxAlong <= travelMinAlong)
        {
            return;
        }

        float travelLength = travelMaxAlong - travelMinAlong;
        float spacing = travelLength / chevronCount;
        float stroke = Math.Clamp(Math.Min(chevronDepth, chevronHalfWidth) * 0.26f, 0.0018f, 0.0042f);
        Color color = ResolveEnergyMechanismPendingDiskColor(activeColor);

        // Match the rule art: eleven compact chevrons roll outward along the middle light arm.
        float phase = (float)((_host.World.GameTimeSec * 3.25) % 1.0);
        for (int index = 0; index < chevronCount; index++)
        {
            float slot = (index + phase) % chevronCount;
            float along = travelMinAlong + spacing * slot;

            Vector3 basePoint = armCenter + outward * along + normal * 0.018f;
            Vector3 tip = basePoint + outward * (chevronDepth * 0.52f);
            Vector3 tail = basePoint - outward * (chevronDepth * 0.52f);
            Vector3 leftTail = tail - lateral * chevronHalfWidth;
            Vector3 rightTail = tail + lateral * chevronHalfWidth;
            if (!IsFineTerrainEnergyPointInsideMiddleArmBounds(tip, armCenter, outward, lateral, minAlong, maxAlong, armHalfWidth)
                || !IsFineTerrainEnergyPointInsideMiddleArmBounds(leftTail, armCenter, outward, lateral, minAlong, maxAlong, armHalfWidth)
                || !IsFineTerrainEnergyPointInsideMiddleArmBounds(rightTail, armCenter, outward, lateral, minAlong, maxAlong, armHalfWidth))
            {
                continue;
            }

            DrawFineTerrainEnergyChevronStroke(graphics, leftTail, tip, normal, stroke, color);
            DrawFineTerrainEnergyChevronStroke(graphics, rightTail, tip, normal, stroke, color);
        }
    }

    private bool TryResolveFineTerrainEnergyMiddleArmFlowPose(
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        int armIndex,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset,
        out Vector3 center,
        out Vector3 normal,
        out Vector3 outward,
        out Vector3 lateral,
        out float minAlong,
        out float maxAlong,
        out float halfWidth)
    {
        FineTerrainEnergyMechanismUnitVisualItem? fallbackWholeArm = null;
        foreach (FineTerrainEnergyMechanismUnitVisualItem candidate in item.Units)
        {
            if (candidate.ArmIndex != armIndex || !IsFineTerrainEnergyLightArmCandidateUnit(candidate))
            {
                continue;
            }

            if (IsFineTerrainEnergyMiddleLightArmUnit(candidate)
                && TryResolveFineTerrainEnergyStripFlowPose(
                    worldScale,
                    item,
                    candidate,
                    compositeTransform,
                    sceneAlignmentOffset,
                    out center,
                    out normal,
                    out outward,
                    out lateral,
                    out minAlong,
                    out maxAlong,
                    out halfWidth))
            {
                return true;
            }

            fallbackWholeArm ??= candidate;
        }

        if (fallbackWholeArm is not null
            && TryResolveFineTerrainEnergyStripFlowPose(
                worldScale,
                item,
                fallbackWholeArm,
                compositeTransform,
                sceneAlignmentOffset,
                out center,
                out normal,
                out outward,
                out lateral,
                out minAlong,
                out maxAlong,
                out halfWidth))
        {
            float length = maxAlong - minAlong;
            if (length > 0.030f)
            {
                // Some role exports merge "\u5149\u81c2-\u4e2d" into one LightArm unit.
                // In that case use the central band of the authored arm mesh as the middle-light-arm range.
                minAlong += length * 0.24f;
                maxAlong -= length * 0.24f;
                halfWidth *= 0.62f;
                return maxAlong > minAlong + 0.030f;
            }
        }

        center = Vector3.Zero;
        normal = Vector3.UnitX;
        outward = Vector3.UnitZ;
        lateral = Vector3.UnitY;
        minAlong = 0f;
        maxAlong = 0f;
        halfWidth = 0.015f;
        return false;
    }

    private static bool IsFineTerrainEnergyPointInsideMiddleArmBounds(
        Vector3 point,
        Vector3 center,
        Vector3 outward,
        Vector3 lateral,
        float minAlong,
        float maxAlong,
        float halfWidth)
    {
        Vector3 delta = point - center;
        float along = Vector3.Dot(delta, outward);
        float side = MathF.Abs(Vector3.Dot(delta, lateral));
        return along >= minAlong + 0.004f
            && along <= maxAlong - 0.004f
            && side <= MathF.Max(0.004f, halfWidth * 0.92f);
    }

    private static Vector3 MoveFineTerrainEnergyPendingCenterOutwardFromPivot(
        Vector3 center,
        Vector3 pivot,
        Vector3 normal,
        float distanceM)
    {
        Vector3 outward = center - pivot;
        outward -= normal * Vector3.Dot(outward, normal);
        float lengthSquared = outward.LengthSquared();
        if (lengthSquared <= 1e-8f)
        {
            return center;
        }

        float length = MathF.Sqrt(lengthSquared);
        return center + outward / length * MathF.Max(0f, distanceM);
    }

    private static Vector3 MoveFineTerrainEnergyPendingCenterTowardPivot(
        Vector3 center,
        Vector3 pivot,
        Vector3 normal,
        float distanceM)
    {
        Vector3 inward = pivot - center;
        inward -= normal * Vector3.Dot(inward, normal);
        float lengthSquared = inward.LengthSquared();
        if (lengthSquared <= 1e-8f)
        {
            return center;
        }

        float length = MathF.Sqrt(lengthSquared);
        return center + inward / length * MathF.Min(MathF.Max(0f, distanceM), length);
    }

    private bool TryResolveFineTerrainEnergyUnitModelCenterPose(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit,
        FineTerrainWorldScale worldScale,
        Matrix4x4 transform,
        Vector3 sceneAlignmentOffset,
        Vector3 extraSceneOffset,
        out Vector3 center,
        out Vector3 normal,
        out Vector3 upAxis)
    {
        upAxis = Vector3.UnitY;
        center = ModelToScenePoint(Vector3.Transform(unit.LocalCenterModel, transform), worldScale)
            + sceneAlignmentOffset
            + extraSceneOffset;
        Vector3 normalTip = ModelToScenePoint(Vector3.Transform(unit.LocalCenterModel + unit.LocalNormalModel, transform), worldScale)
            + sceneAlignmentOffset
            + extraSceneOffset;
        normal = normalTip - center;
        if (normal.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        normal = Vector3.Normalize(normal);
        Vector3 pivot = ModelToScenePoint(Vector3.Transform(item.PivotModel, transform), worldScale)
            + sceneAlignmentOffset
            + extraSceneOffset;
        upAxis = center - pivot;
        upAxis -= normal * Vector3.Dot(upAxis, normal);
        if (upAxis.LengthSquared() <= 1e-8f)
        {
            upAxis = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.96f
                ? Vector3.UnitX
                : Vector3.UnitY;
            upAxis -= normal * Vector3.Dot(upAxis, normal);
        }

        if (upAxis.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        upAxis = Vector3.Normalize(upAxis);
        return true;
    }

    private bool TryDrawFineTerrainEnergyPendingStripFlow(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        if (!ShouldDrawFineTerrainEnergyPendingStripFlow(item, unit))
        {
            return false;
        }

        SimulationTeamState teamState = _host.World.Teams[item.Team];
        int persistentRingScore = unit.ArmIndex < teamState.EnergyHitRingsByArm.Length
            ? Math.Clamp(teamState.EnergyHitRingsByArm[unit.ArmIndex], 0, 10)
            : 0;
        if (persistentRingScore > 0)
        {
            return false;
        }

        if (!TryResolveFineTerrainEnergyStripFlowPose(
                worldScale,
                item,
                unit,
                compositeTransform,
                sceneAlignmentOffset,
                out Vector3 center,
                out Vector3 normal,
                out Vector3 outward,
                out Vector3 lateral,
                out float minAlong,
                out float maxAlong,
                out float halfWidth))
        {
            return false;
        }

        Color teamColor = ResolveEnergyMechanismPureTeamLightColor(item.Team);
        bool readyToHit = teamState.EnergyNextModuleDelaySec <= 1e-6;
        bool explicitMiddleUnit = IsFineTerrainEnergyMiddleLightArmUnit(unit);
        FineTerrainEnergyMechanismUnitVisualItem? pendingRingUnit = ResolveFineTerrainEnergyRingUnit(item, unit.ArmIndex, preferredRingScore: 10);
        if (pendingRingUnit is not null)
        {
            Vector3 ringCenter = ModelToScenePoint(Vector3.Transform(pendingRingUnit.LocalCenterModel, compositeTransform), worldScale)
                + sceneAlignmentOffset;
            float ringAlong = Vector3.Dot(ringCenter - center, outward);
            if (ringAlong > minAlong)
            {
                float ringClearance = Math.Clamp((maxAlong - minAlong) * 0.14f, 0.030f, 0.085f);
                maxAlong = Math.Min(maxAlong, ringAlong - ringClearance);
            }
        }

        if (!explicitMiddleUnit)
        {
            float wholeLength = maxAlong - minAlong;
            if (wholeLength <= 0.04f)
            {
                return false;
            }

            // Current annotations can merge "\u5149\u81c2-\u4e2d" into the whole LightArm.
            // In that case reserve only the middle strip and leave the armor-plate end clear.
            float middleMin = minAlong + wholeLength * 0.08f;
            float middleMax = minAlong + wholeLength * 0.58f;
            if (middleMax <= middleMin + 0.035f)
            {
                return false;
            }

            minAlong = middleMin;
            maxAlong = middleMax;
            halfWidth *= 0.54f;
        }

        float length = MathF.Max(0.04f, maxAlong - minAlong);
        const int chevronCount = 11;
        float baseSpacing = length / Math.Max(1, chevronCount);
        float chevronDepth = Math.Clamp(baseSpacing * 0.56f, 0.012f, 0.038f);
        float edgeInset = Math.Clamp(chevronDepth * 0.20f, length * 0.006f, length * 0.020f);
        float usableMinAlong = minAlong + edgeInset + chevronDepth * 0.52f;
        float usableMaxAlong = maxAlong - edgeInset - chevronDepth * 0.52f;
        float usableLength = MathF.Max(0.04f, usableMaxAlong - usableMinAlong);
        float spacing = usableLength / Math.Max(1, chevronCount - 1);
        double phase = (_host.World.GameTimeSec * 3.2) % 1.0;
        chevronDepth = Math.Clamp(spacing * 0.62f, 0.012f, 0.038f);
        float chevronHalfWidth = Math.Clamp(halfWidth * 0.76f, 0.008f, 0.036f);
        float stroke = Math.Clamp(Math.Min(chevronDepth, chevronHalfWidth) * 0.30f, 0.0029f, 0.0072f);
        Vector3 faceOffset = normal * 0.014f;
        Color color = Color.FromArgb(readyToHit ? 255 : 214, ResolveEnergyMechanismPendingDiskColor(teamColor));
        for (int index = 0; index < chevronCount; index++)
        {
            float along = usableMinAlong + spacing * index + spacing * (float)phase;
            while (along > usableMaxAlong)
            {
                along -= usableLength + spacing;
            }
            while (along < usableMinAlong)
            {
                along += usableLength + spacing;
            }

            Vector3 tip = center + outward * (along + chevronDepth * 0.46f) + faceOffset;
            Vector3 tail = center + outward * (along - chevronDepth * 0.46f) + faceOffset;
            Vector3 leftTail = tail - lateral * chevronHalfWidth;
            Vector3 rightTail = tail + lateral * chevronHalfWidth;
            if (!IsFineTerrainEnergyPointInsideMiddleArmBounds(tip, center, outward, lateral, minAlong, maxAlong, halfWidth)
                || !IsFineTerrainEnergyPointInsideMiddleArmBounds(leftTail, center, outward, lateral, minAlong, maxAlong, halfWidth)
                || !IsFineTerrainEnergyPointInsideMiddleArmBounds(rightTail, center, outward, lateral, minAlong, maxAlong, halfWidth))
            {
                continue;
            }

            DrawFineTerrainEnergyChevronStroke(graphics, leftTail, tip, normal, stroke, color);
            DrawFineTerrainEnergyChevronStroke(graphics, rightTail, tip, normal, stroke, color);
        }

        return true;
    }

    private bool ShouldDrawFineTerrainEnergyPendingStripFlow(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit)
    {
        if (!IsFineTerrainEnergyPendingArm(item, unit)
            || !IsFineTerrainEnergyLightArmCandidateUnit(unit))
        {
            return false;
        }

        return IsFineTerrainEnergyMiddleLightArmUnit(unit);
    }

    private bool IsFineTerrainEnergyPendingArm(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit)
    {
        if (!IsFineTerrainEnergyLightArmVisualKind(unit.Kind)
            || IsFineTerrainEnergyOuterLightArmUnit(unit)
            || unit.ArmIndex < 0
            || unit.Triangles.Count == 0
            || !_host.World.Teams.TryGetValue(item.Team, out SimulationTeamState? teamState)
            || !string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            || teamState.EnergyCurrentLitMask == 0
            || (teamState.EnergyCurrentLitMask & (1 << unit.ArmIndex)) == 0)
        {
            return false;
        }

        int persistentRingScore = unit.ArmIndex < teamState.EnergyHitRingsByArm.Length
            ? Math.Clamp(teamState.EnergyHitRingsByArm[unit.ArmIndex], 0, 10)
            : 0;
        return persistentRingScore <= 0;
    }

    private static bool IsFineTerrainEnergyMiddleLightArmUnit(FineTerrainEnergyMechanismUnitVisualItem unit)
    {
        if (!IsFineTerrainEnergyLightArmCandidateUnit(unit))
        {
            return false;
        }

        string name = unit.Name ?? string.Empty;
        if (name.Contains("\u5916", StringComparison.Ordinal)
            || name.Contains("outer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (unit.Kind == FineTerrainEnergyUnitKind.MiddleLightArm)
        {
            return true;
        }

        return name.Contains("\u4e2d", StringComparison.Ordinal)
            && (name.Contains("\u5149\u81c2", StringComparison.Ordinal)
                || name.Contains("\u706f\u81c2", StringComparison.Ordinal)
                || unit.Kind is FineTerrainEnergyUnitKind.LightArm or FineTerrainEnergyUnitKind.MiddleLightArm);
    }

    private static bool IsFineTerrainEnergyLightArmCandidateUnit(FineTerrainEnergyMechanismUnitVisualItem unit)
    {
        if (!IsFineTerrainEnergyLightArmVisualKind(unit.Kind))
        {
            return false;
        }

        if (IsFineTerrainEnergyOuterLightArmUnit(unit))
        {
            return false;
        }

        string name = unit.Name ?? string.Empty;
        return unit.Kind is FineTerrainEnergyUnitKind.LightArm or FineTerrainEnergyUnitKind.MiddleLightArm
            || name.Contains("\u706f\u81c2", StringComparison.Ordinal)
            || name.Contains("\u5149\u81c2", StringComparison.Ordinal)
            || name.Contains("arm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFineTerrainEnergyOuterLightArmUnit(FineTerrainEnergyMechanismUnitVisualItem unit)
    {
        if (!IsFineTerrainEnergyLightArmVisualKind(unit.Kind))
        {
            return false;
        }

        if (unit.Kind == FineTerrainEnergyUnitKind.OuterLightArm)
        {
            return true;
        }

        string name = unit.Name ?? string.Empty;
        return name.Contains("\u80fd\u91cf\u673a\u5173", StringComparison.OrdinalIgnoreCase)
            && (name.Contains("\u706f\u81c2", StringComparison.OrdinalIgnoreCase)
                || name.Contains("\u5149\u81c2", StringComparison.OrdinalIgnoreCase))
            && name.Contains("\u5916", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFineTerrainEnergyLightArmVisualKind(FineTerrainEnergyUnitKind kind)
        => kind is FineTerrainEnergyUnitKind.LightArm
            or FineTerrainEnergyUnitKind.MiddleLightArm
            or FineTerrainEnergyUnitKind.OuterLightArm
            or FineTerrainEnergyUnitKind.LightStrip;

    private bool TryResolveFineTerrainEnergyStripFlowPose(
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset,
        out Vector3 center,
        out Vector3 normal,
        out Vector3 outward,
        out Vector3 lateral,
        out float minAlong,
        out float maxAlong,
        out float halfWidth)
    {
        center = Vector3.Zero;
        normal = Vector3.UnitX;
        outward = Vector3.UnitZ;
        lateral = Vector3.UnitY;
        minAlong = 0f;
        maxAlong = 0f;
        halfWidth = 0.015f;
        Vector3 weightedCenter = Vector3.Zero;
        Vector3 weightedNormal = Vector3.Zero;
        float totalArea = 0f;
        List<Vector3> vertices = new(unit.Triangles.Count * 3);
        foreach (FineTerrainColoredTriangle triangle in unit.Triangles)
        {
            Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, compositeTransform), worldScale) + sceneAlignmentOffset;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            Vector3 cross = Vector3.Cross(b - a, c - a);
            float area = cross.Length() * 0.5f;
            if (area <= 1e-8f)
            {
                continue;
            }

            weightedCenter += (a + b + c) * (area / 3f);
            weightedNormal += Vector3.Normalize(cross) * area;
            totalArea += area;
        }

        if (vertices.Count == 0 || totalArea <= 1e-8f)
        {
            return false;
        }

        center = weightedCenter / totalArea;
        normal = weightedNormal.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(weightedNormal);
        Vector3 pivotScene = ModelToScenePoint(Vector3.Transform(item.PivotModel, compositeTransform), worldScale) + sceneAlignmentOffset;
        outward = center - pivotScene;
        outward -= normal * Vector3.Dot(outward, normal);
        if (outward.LengthSquared() <= 1e-8f)
        {
            outward = Vector3.UnitZ - normal * Vector3.Dot(Vector3.UnitZ, normal);
        }

        if (outward.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        outward = Vector3.Normalize(outward);
        lateral = Vector3.Cross(normal, outward);
        if (lateral.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        lateral = Vector3.Normalize(lateral);
        minAlong = float.MaxValue;
        maxAlong = float.MinValue;
        float maxAbsLateral = 0f;
        foreach (Vector3 vertex in vertices)
        {
            Vector3 delta = vertex - center;
            float along = Vector3.Dot(delta, outward);
            float side = MathF.Abs(Vector3.Dot(delta, lateral));
            minAlong = MathF.Min(minAlong, along);
            maxAlong = MathF.Max(maxAlong, along);
            maxAbsLateral = MathF.Max(maxAbsLateral, side);
        }

        if (!float.IsFinite(minAlong)
            || !float.IsFinite(maxAlong)
            || maxAlong <= minAlong + 0.02f)
        {
            return false;
        }

        halfWidth = Math.Clamp(maxAbsLateral * 0.86f, 0.012f, 0.055f);
        float maxNormalOffset = 0f;
        foreach (Vector3 vertex in vertices)
        {
            maxNormalOffset = MathF.Max(maxNormalOffset, Vector3.Dot(vertex - center, normal));
        }

        if (maxNormalOffset > 1e-5f)
        {
            center += normal * maxNormalOffset;
        }

        return true;
    }

    private void DrawFineTerrainEnergyChevronStroke(
        Graphics? graphics,
        Vector3 start,
        Vector3 end,
        Vector3 normal,
        float thickness,
        Color color)
    {
        Vector3 direction = end - start;
        if (direction.LengthSquared() <= 1e-8f)
        {
            return;
        }

        direction = Vector3.Normalize(direction);
        Vector3 side = Vector3.Cross(normal, direction);
        if (side.LengthSquared() <= 1e-8f)
        {
            return;
        }

        side = Vector3.Normalize(side) * thickness;
        if (graphics is null)
        {
            AppendOrDrawGpuQuad(start - side, start + side, end + side, end - side, color);
            return;
        }

        DrawCpuMarkerQuad(graphics, start - side, start + side, end + side, end - side, color);
    }

    private void DrawFineTerrainEnergyChevronFill(
        Graphics? graphics,
        Vector3 tail,
        Vector3 tip,
        Vector3 normal,
        Vector3 outwardSide,
        float thickness,
        Color color)
    {
        Vector3 direction = tip - tail;
        if (direction.LengthSquared() <= 1e-8f)
        {
            return;
        }

        direction = Vector3.Normalize(direction);
        Vector3 side = Vector3.Cross(normal, direction);
        if (side.LengthSquared() <= 1e-8f)
        {
            return;
        }

        side = Vector3.Normalize(side) * thickness;
        Vector3 shoulder = Vector3.Lerp(tail, tip, 0.54f);
        Vector3 outer = shoulder + outwardSide * (thickness * 2.3f);
        Vector3 inner = shoulder - outwardSide * (thickness * 0.35f);
        if (graphics is null)
        {
            AppendOrDrawGpuQuad(tail - side, tail + side, outer + side, outer - side, color);
            AppendOrDrawGpuTriangle(outer, tip, inner, color);
            return;
        }

        DrawCpuMarkerQuad(graphics, tail - side, tail + side, outer + side, outer - side, color);
        DrawCpuTriangle(graphics, outer, tip, inner, color);
    }

    private void DrawCpuTriangle(Graphics graphics, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        if (TryBuildProjectedFace(new[] { a, b, c }, color, color, out ProjectedFace face))
        {
            DrawProjectedFaceBatch(graphics, new[] { face }, 0.94f);
        }
    }

    private bool TryResolveFineTerrainEnergyUnitSurfacePose(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit,
        FineTerrainWorldScale worldScale,
        Matrix4x4 transform,
        Vector3 sceneAlignmentOffset,
        Vector3 extraSceneOffset,
        out Vector3 center,
        out Vector3 normal,
        out Vector3 upAxis,
        out float outerRadius)
    {
        center = Vector3.Zero;
        normal = Vector3.UnitY;
        upAxis = Vector3.UnitZ;
        outerRadius = 0f;
        if (unit.Triangles.Count == 0)
        {
            return false;
        }

        Vector3 weightedCenter = Vector3.Zero;
        Vector3 weightedNormal = Vector3.Zero;
        float totalArea = 0f;
        foreach (FineTerrainColoredTriangle triangle in unit.Triangles)
        {
            Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, transform), worldScale) + sceneAlignmentOffset;
            Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, transform), worldScale) + sceneAlignmentOffset;
            Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, transform), worldScale) + sceneAlignmentOffset;
            Vector3 cross = Vector3.Cross(b - a, c - a);
            float area = cross.Length() * 0.5f;
            if (area <= 1e-7f)
            {
                continue;
            }

            weightedCenter += (a + b + c) * (area / 3f);
            weightedNormal += cross;
            totalArea += area;
        }

        if (totalArea <= 1e-7f || weightedNormal.LengthSquared() <= 1e-10f)
        {
            return false;
        }

        Vector3 roughCenter = weightedCenter / totalArea;
        normal = Vector3.Normalize(weightedNormal);
        Vector3 pivotScene = ModelToScenePoint(Vector3.Transform(item.PivotModel, transform), worldScale) + sceneAlignmentOffset;
        upAxis = roughCenter - pivotScene;
        upAxis -= normal * Vector3.Dot(upAxis, normal);
        if (upAxis.LengthSquared() <= 1e-8f)
        {
            upAxis = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.96f
                ? Vector3.UnitX
                : Vector3.UnitY;
            upAxis -= normal * Vector3.Dot(upAxis, normal);
        }

        if (upAxis.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        Vector3 planeUpAxis = Vector3.Normalize(upAxis);
        Vector3 sideAxis = Vector3.Cross(planeUpAxis, normal);
        if (sideAxis.LengthSquared() <= 1e-8f)
        {
            sideAxis = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.96f
                ? Vector3.UnitX
                : Vector3.UnitY;
            sideAxis -= normal * Vector3.Dot(sideAxis, normal);
        }

        if (sideAxis.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        sideAxis = Vector3.Normalize(sideAxis);
        float minSide = float.PositiveInfinity;
        float maxSide = float.NegativeInfinity;
        float minUp = float.PositiveInfinity;
        float maxUp = float.NegativeInfinity;
        foreach (FineTerrainColoredTriangle triangle in unit.Triangles)
        {
            TrackVertex(ModelToScenePoint(Vector3.Transform(triangle.A, transform), worldScale) + sceneAlignmentOffset);
            TrackVertex(ModelToScenePoint(Vector3.Transform(triangle.B, transform), worldScale) + sceneAlignmentOffset);
            TrackVertex(ModelToScenePoint(Vector3.Transform(triangle.C, transform), worldScale) + sceneAlignmentOffset);
        }

        if (!float.IsFinite(minSide) || !float.IsFinite(maxSide) || !float.IsFinite(minUp) || !float.IsFinite(maxUp))
        {
            return false;
        }

        center = roughCenter
            + sideAxis * ((minSide + maxSide) * 0.5f)
            + planeUpAxis * ((minUp + maxUp) * 0.5f)
            + extraSceneOffset;
        upAxis = planeUpAxis;

        foreach (FineTerrainColoredTriangle triangle in unit.Triangles)
        {
            Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
            Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
            Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
            outerRadius = MathF.Max(outerRadius, ResolvePlanarDistanceToAxis(a, center, normal));
            outerRadius = MathF.Max(outerRadius, ResolvePlanarDistanceToAxis(b, center, normal));
            outerRadius = MathF.Max(outerRadius, ResolvePlanarDistanceToAxis(c, center, normal));
        }

        outerRadius = Math.Clamp(outerRadius, 0.030f, 0.155f);
        return true;

        void TrackVertex(Vector3 vertex)
        {
            Vector3 delta = vertex - roughCenter;
            float side = Vector3.Dot(delta, sideAxis);
            float up = Vector3.Dot(delta, planeUpAxis);
            minSide = MathF.Min(minSide, side);
            maxSide = MathF.Max(maxSide, side);
            minUp = MathF.Min(minUp, up);
            maxUp = MathF.Max(maxUp, up);
        }
    }

    private static float ResolvePlanarDistanceToAxis(Vector3 point, Vector3 center, Vector3 normal)
    {
        Vector3 delta = point - center;
        delta -= normal * Vector3.Dot(delta, normal);
        return delta.Length();
    }

    private Color? ResolveFineTerrainEnergyLightArmColor(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit)
    {
        if (!IsFineTerrainEnergyLightArmVisualKind(unit.Kind)
            || !_host.World.Teams.TryGetValue(item.Team, out SimulationTeamState? teamState))
        {
            return null;
        }

        bool fullyActivated = string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase);
        bool armActivated = unit.ArmIndex >= 0
            && ((unit.ArmIndex < teamState.EnergyHitRingsByArm.Length
                    && teamState.EnergyHitRingsByArm[unit.ArmIndex] > 0)
                || IsFineTerrainEnergyArmActivatedByProgress(teamState, unit.ArmIndex));
        bool pendingArm = IsFineTerrainEnergyPendingArm(item, unit);
        if (IsFineTerrainEnergyOuterLightArmUnit(unit))
        {
            bool allDisksActivated = ResolveFineTerrainEnergyActivatedArmCount(teamState) >= 5;
            if (allDisksActivated)
            {
                Color teamColor = ResolveEnergyMechanismPureTeamLightColor(item.Team);
                return Color.FromArgb(255, teamColor);
            }

            return Color.FromArgb(255, 9, 10, 13);
        }

        if (pendingArm)
        {
            return Color.FromArgb(255, 9, 10, 13);
        }

        if (teamState.EnergyLargeMechanismActive && !fullyActivated)
        {
            return Color.FromArgb(255, 9, 10, 13);
        }

        if (fullyActivated || armActivated)
        {
            Color teamColor = ResolveEnergyMechanismPureTeamLightColor(item.Team);
            return Color.FromArgb(255, teamColor);
        }

        return Color.FromArgb(255, 9, 10, 13);
    }

    private bool TryDrawFineTerrainEnergyLightArmProgressUnit(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset)
    {
        if (!IsFineTerrainEnergyLightArmVisualKind(unit.Kind)
            || IsFineTerrainEnergyOuterLightArmUnit(unit)
            || unit.Triangles.Count == 0
            || !_host.World.Teams.TryGetValue(item.Team, out SimulationTeamState? teamState)
            || IsFineTerrainEnergyPendingArm(item, unit)
            || !TryResolveFineTerrainEnergyUnitRadialRange(item, unit, out float minRadius, out float maxRadius))
        {
            return false;
        }

        float litRatio = ResolveFineTerrainEnergyLightArmLitRatio(teamState, unit);
        if (litRatio <= 1e-4f || litRatio >= 0.999f)
        {
            return false;
        }

        Color litColor = ResolveEnergyMechanismPureTeamLightColor(item.Team);
        Color darkColor = Color.FromArgb(255, 9, 10, 13);
        if (_gpuGeometryPass && UseGpuRenderer)
        {
            foreach (FineTerrainColoredTriangle triangle in unit.Triangles)
            {
                Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, compositeTransform), worldScale) + sceneAlignmentOffset;
                Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, compositeTransform), worldScale) + sceneAlignmentOffset;
                Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, compositeTransform), worldScale) + sceneAlignmentOffset;
                Color fill = ResolveFineTerrainEnergyLightArmTriangleColor(item, triangle, minRadius, maxRadius, litRatio, litColor, darkColor);
                AppendOrDrawGpuTriangle(a, b, c, fill);
            }

            return true;
        }

        if (graphics is null)
        {
            return false;
        }

        var faces = new List<ProjectedFace>(Math.Min(unit.Triangles.Count, 2048));
        foreach (FineTerrainColoredTriangle triangle in unit.Triangles)
        {
            Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, compositeTransform), worldScale) + sceneAlignmentOffset;
            Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, compositeTransform), worldScale) + sceneAlignmentOffset;
            Color fill = ResolveFineTerrainEnergyLightArmTriangleColor(item, triangle, minRadius, maxRadius, litRatio, litColor, darkColor);
            Color edge = Color.FromArgb(255, BlendColor(fill, Color.Black, 0.22f));
            if (TryBuildProjectedFace(new[] { a, b, c }, fill, edge, out ProjectedFace face))
            {
                faces.Add(face);
            }
        }

        if (faces.Count == 0)
        {
            return false;
        }

        if (_collectProjectedFacesOnly)
        {
            _projectedEntityFaceBuffer.AddRange(faces);
            return true;
        }

        DrawProjectedFaceBatch(graphics, faces, 0.925f);
        return true;
    }

    private float ResolveFineTerrainEnergyLightArmLitRatio(
        SimulationTeamState teamState,
        FineTerrainEnergyMechanismUnitVisualItem unit)
    {
        if (string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase))
        {
            return 1f;
        }

        if (teamState.EnergyLargeMechanismActive)
        {
            return ResolveFineTerrainEnergyActivationRatio(teamState);
        }

        bool armActivated = unit.ArmIndex >= 0
            && ((unit.ArmIndex < teamState.EnergyHitRingsByArm.Length
                    && teamState.EnergyHitRingsByArm[unit.ArmIndex] > 0)
                || IsFineTerrainEnergyArmActivatedByProgress(teamState, unit.ArmIndex));
        return armActivated ? 1f : 0f;
    }

    private static bool TryResolveFineTerrainEnergyUnitRadialRange(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainEnergyMechanismUnitVisualItem unit,
        out float minRadius,
        out float maxRadius)
    {
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        Vector3 rotorAxis = ResolveFineTerrainRotorAxis(item.CoordinateYprDegrees);
        foreach (FineTerrainColoredTriangle triangle in unit.Triangles)
        {
            Include(triangle.A);
            Include(triangle.B);
            Include(triangle.C);
        }

        if (!float.IsFinite(min)
            || !float.IsFinite(max)
            || max <= min + 1e-4f)
        {
            minRadius = 0f;
            maxRadius = 1f;
            return false;
        }

        minRadius = min;
        maxRadius = max;
        return true;

        void Include(Vector3 point)
        {
            Vector3 radial = point - item.PivotModel;
            radial -= rotorAxis * Vector3.Dot(radial, rotorAxis);
            float radius = radial.Length();
            min = MathF.Min(min, radius);
            max = MathF.Max(max, radius);
        }
    }

    private static Color ResolveFineTerrainEnergyLightArmTriangleColor(
        FineTerrainEnergyMechanismVisualItem item,
        FineTerrainColoredTriangle triangle,
        float minRadius,
        float maxRadius,
        float litRatio,
        Color litColor,
        Color darkColor)
    {
        if (litRatio >= 0.999f)
        {
            return Color.FromArgb(255, litColor);
        }

        if (litRatio <= 1e-4f)
        {
            return darkColor;
        }

        Vector3 rotorAxis = ResolveFineTerrainRotorAxis(item.CoordinateYprDegrees);
        Vector3 centroid = (triangle.A + triangle.B + triangle.C) / 3f;
        Vector3 radial = centroid - item.PivotModel;
        radial -= rotorAxis * Vector3.Dot(radial, rotorAxis);
        float progress = Math.Clamp((radial.Length() - minRadius) / MathF.Max(1e-4f, maxRadius - minRadius), 0f, 1f);
        return progress <= litRatio
            ? Color.FromArgb(255, litColor)
            : darkColor;
    }

    private static bool IsFineTerrainEnergyArmActivatedByProgress(SimulationTeamState teamState, int armIndex)
    {
        int activatedCount = Math.Clamp(teamState.EnergyActivatedGroupCount, 0, 5);
        if (activatedCount <= 0 || armIndex < 0)
        {
            return false;
        }

        int orderLength = Math.Min(activatedCount, teamState.EnergyActivationOrder.Length);
        bool orderLooksInitialized = teamState.EnergyActivationOrder.Any(index => index != 0);
        for (int index = 0; index < orderLength; index++)
        {
            if (Math.Clamp(teamState.EnergyActivationOrder[index], 0, 4) == armIndex)
            {
                return true;
            }
        }

        return !orderLooksInitialized && armIndex < activatedCount;
    }

    private static Color ResolveEnergyMechanismPureTeamLightColor(string team)
        => string.Equals(team, "red", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(255, 255, 0, 0)
            : Color.FromArgb(255, 0, 0, 255);

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

    private bool TryDrawGpuFineTerrainUnitMesh(
        Dictionary<string, FineTerrainStaticMeshCache> cacheMap,
        string sceneKey,
        string meshKey,
        FineTerrainWorldScale worldScale,
        Vector3 pivotModel,
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        Matrix4x4 compositeTransform,
        Vector3 sceneAlignmentOffset,
        Vector3 extraSceneOffset,
        Color? overrideColor = null)
    {
        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null)
        {
            return false;
        }

        string colorKey = overrideColor is Color color ? color.ToArgb().ToString("X8") : "source";
        string cacheKey = $"{sceneKey}|{meshKey}|{colorKey}";
        if (!cacheMap.TryGetValue(cacheKey, out FineTerrainStaticMeshCache? cache))
        {
            cache = BuildFineTerrainMeshCache(worldScale, pivotModel, triangles, overrideColor);
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

    private FineTerrainStaticMeshCache? BuildFineTerrainMeshCache(
        FineTerrainWorldScale worldScale,
        Vector3 pivotModel,
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        Color? overrideColor = null)
    {
        if (triangles.Count == 0)
        {
            return null;
        }

        Vector3 pivotScene = ModelToScenePoint(pivotModel, worldScale);
        var vertices = new List<GpuVertex>(triangles.Count * 3);
        foreach (FineTerrainColoredTriangle triangle in triangles)
        {
            Color fill = overrideColor ?? ResolveFineTerrainTriangleColor(triangle.Color);
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

    private Vector3 CollisionShapeModelToScenePoint(Vector3 modelPoint, FineTerrainWorldScale worldScale)
    {
        double fieldLengthM = _host.MapPreset.FieldLengthM > 1e-6
            ? _host.MapPreset.FieldLengthM
            : Math.Max(1e-6, worldScale.MapLengthXMeters);
        double fieldWidthM = _host.MapPreset.FieldWidthM > 1e-6
            ? _host.MapPreset.FieldWidthM
            : Math.Max(1e-6, worldScale.MapLengthZMeters);
        double centeredXMeters = (modelPoint.X - worldScale.ModelCenter.X) * worldScale.XMetersPerModelUnit;
        double centeredZMeters = (modelPoint.Z - worldScale.ModelCenter.Z) * worldScale.ZMetersPerModelUnit;
        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double worldX = (fieldLengthM * 0.5 + centeredXMeters) / metersPerWorldUnit;
        double worldY = (fieldWidthM * 0.5 + centeredZMeters) / metersPerWorldUnit;
        double heightM = Math.Max(0.0, (modelPoint.Y - worldScale.ModelMinY) * worldScale.YMetersPerModelUnit);
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
            bool outerPanelComposite = FineTerrainBaseVisualCache.IsBaseOuterPanelCompositeName(item.Name);
            bool drawBody = renderPass != StructureRenderPass.DynamicArmor && !outerPanelComposite;
            bool drawUnits = renderPass != StructureRenderPass.StaticBody;
            if (drawBody && item.Triangles.Count > 0)
            {
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    item.Triangles,
                    transform,
                    sceneAlignmentOffset);
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

                Color? flashOverride = ResolveStructurePlateFlashOverride(entity, unit.PlateId);
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    unit.Triangles,
                    transform,
                    sceneAlignmentOffset,
                    Vector3.Zero,
                    flashOverride);
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
            bool outerPanelComposite = FineTerrainBaseVisualCache.IsBaseOuterPanelCompositeName(item.Name);
            bool topArmorComposite = FineTerrainBaseVisualCache.IsBaseTopArmorCompositeName(item.Name);
            bool drawBody = outerPanelComposite || topArmorComposite
                ? renderPass != StructureRenderPass.StaticBody
                : renderPass != StructureRenderPass.DynamicArmor;
            bool drawUnits = renderPass != StructureRenderPass.StaticBody;
            if (drawBody && item.Triangles.Count > 0)
            {
                if (outerPanelComposite)
                {
                    DrawFineTerrainBaseOuterPanelTriangles(
                        graphics,
                        scene.WorldScale,
                        item,
                        entity,
                        item.Triangles,
                        compositeTransform,
                        sceneAlignmentOffset);
                }
                else
                {
                    DrawFineTerrainColoredTriangles(
                        graphics,
                        scene.WorldScale,
                        item.Triangles,
                        compositeTransform,
                        sceneAlignmentOffset);
                }

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

                Matrix4x4 unitTransform = ResolveFineTerrainBaseUnitTransform(
                    scene.WorldScale,
                    item,
                    unit,
                    compositeTransform,
                    entity);
                Color? flashOverride = ResolveStructurePlateFlashOverride(entity, unit.PlateId);
                DrawFineTerrainColoredTriangles(
                    graphics,
                    scene.WorldScale,
                    unit.Triangles,
                    unitTransform,
                    sceneAlignmentOffset,
                    Vector3.Zero,
                    flashOverride);
                drawn = true;
            }
        }

        return drawn;
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
        if (IsFineTerrainEnergyOpaqueBlack(source))
        {
            return Color.FromArgb(255, source);
        }

        return source;
    }

    private static bool IsFineTerrainEnergyOpaqueBlack(Color color)
        => color.R <= 32 && color.G <= 32 && color.B <= 36;

    private float ResolveFineTerrainEnergyActivationRatio(SimulationTeamState teamState)
    {
        if (string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase)
            || ResolveFineTerrainEnergyActivatedArmCount(teamState) >= 5)
        {
            return 1.0f;
        }

        if (string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            && !teamState.EnergyLargeMechanismActive)
        {
            return 0f;
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
            FineTerrainEnergyUnitKind.MiddleLightArm => 0.0045f,
            FineTerrainEnergyUnitKind.OuterLightArm => 0.0045f,
            FineTerrainEnergyUnitKind.LightStrip => 0.0045f,
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

    private Vector3 ResolveFineTerrainEnergyUnitNormalOffset(
        FineTerrainEnergyMechanismUnitVisualItem unit,
        FineTerrainWorldScale worldScale,
        Matrix4x4 compositeTransform,
        float liftMeters)
    {
        if (liftMeters <= 1e-6f)
        {
            return Vector3.Zero;
        }

        Vector3 centerScene = ModelToScenePoint(Vector3.Transform(unit.LocalCenterModel, compositeTransform), worldScale);
        Vector3 normalTipScene = ModelToScenePoint(Vector3.Transform(unit.LocalCenterModel + unit.LocalNormalModel, compositeTransform), worldScale);
        Vector3 sceneNormal = normalTipScene - centerScene;
        return sceneNormal.LengthSquared() <= 1e-8f
            ? Vector3.Zero
            : Vector3.Normalize(sceneNormal) * liftMeters;
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
        Vector3 sceneAlignmentOffset)
        => DrawFineTerrainColoredTriangles(
            graphics,
            worldScale,
            triangles,
            transform,
            sceneAlignmentOffset,
            Vector3.Zero);

    private void DrawFineTerrainColoredTriangles(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        Matrix4x4 transform,
        Vector3 sceneAlignmentOffset,
        Vector3 extraSceneOffset)
        => DrawFineTerrainColoredTriangles(
            graphics,
            worldScale,
            triangles,
            transform,
            sceneAlignmentOffset,
            extraSceneOffset,
            null);

    private void DrawFineTerrainColoredTriangles(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        Matrix4x4 transform,
        Vector3 sceneAlignmentOffset,
        Vector3 extraSceneOffset,
        Color? overrideColor)
    {
        if (_gpuGeometryPass && UseGpuRenderer)
        {
            foreach (FineTerrainColoredTriangle triangle in triangles)
            {
                Vector3 a = ModelToScenePoint(Vector3.Transform(triangle.A, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
                Vector3 b = ModelToScenePoint(Vector3.Transform(triangle.B, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
                Vector3 c = ModelToScenePoint(Vector3.Transform(triangle.C, transform), worldScale) + sceneAlignmentOffset + extraSceneOffset;
                AppendOrDrawGpuTriangle(a, b, c, overrideColor ?? ResolveFineTerrainTriangleColor(triangle.Color));
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
            Color fill = overrideColor ?? ResolveFineTerrainTriangleColor(triangle.Color);
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

    private static Color ResolveFineTerrainTriangleColor(Color source)
    {
        Color fill = source.A <= 0 ? Color.FromArgb(236, 224, 232, 240) : source;
        return fill;
    }

    private Color? ResolveStructurePlateFlashOverride(SimulationEntity? entity, string plateId)
    {
        if (entity is null || string.IsNullOrWhiteSpace(plateId))
        {
            return null;
        }

        return IsStructurePlateFlashActive(entity.Id, plateId, out float intensity) && intensity > 0.5f
            ? Color.FromArgb(246, 2, 3, 4)
            : null;
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

        if (!includeSlide || entity is null)
        {
            return baseTransform;
        }

        if (FineTerrainBaseVisualCache.IsBaseOuterPanelCompositeName(item.Name))
        {
            return baseTransform;
        }

        if (!entity.IsAlive)
        {
            return baseTransform;
        }

        if (!FineTerrainBaseVisualCache.IsBaseTopArmorCompositeName(item.Name))
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

    private Matrix4x4 ResolveFineTerrainBaseUnitTransform(
        FineTerrainWorldScale worldScale,
        FineTerrainBaseVisualItem item,
        FineTerrainBaseUnitVisualItem unit,
        Matrix4x4 compositeTransform,
        SimulationEntity entity)
    {
        if (IsFineTerrainBaseOuterPanelUnit(item, unit))
        {
            float openProgress = ResolveBaseArmorOpenProgress(entity);
            if (openProgress <= 1e-4f)
            {
                return compositeTransform;
            }

            Matrix4x4 openTransform = ResolveFineTerrainBaseOuterPanelOpenModelTransform(
                worldScale,
                item,
                unit,
                unit.LocalCentroidModel,
                openProgress);
            return openTransform * compositeTransform;
        }

        return compositeTransform;
    }

    private static bool IsFineTerrainBaseOuterPanelUnit(
        FineTerrainBaseVisualItem item,
        FineTerrainBaseUnitVisualItem unit)
        => FineTerrainBaseVisualCache.IsBaseOuterPanelCompositeName(item.Name)
            || unit.PlateId.StartsWith("base_outer", StringComparison.OrdinalIgnoreCase)
            || unit.Name.Contains("\u5916\u677f", StringComparison.Ordinal);

    private Matrix4x4 ResolveFineTerrainBaseOuterPanelOpenTransform(
        FineTerrainWorldScale worldScale,
        FineTerrainBaseVisualItem item,
        SimulationEntity entity,
        Matrix4x4 baseTransform)
    {
        float openProgress = ResolveBaseArmorOpenProgress(entity);
        if (openProgress <= 1e-4f)
        {
            return baseTransform;
        }

        Matrix4x4 openTransform = ResolveFineTerrainBaseOuterPanelOpenModelTransform(
            worldScale,
            item,
            unit: null,
            pivotModel: item.PivotModel,
            openProgress);
        return openTransform * baseTransform;
    }

    private void DrawFineTerrainBaseOuterPanelTriangles(
        Graphics? graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainBaseVisualItem item,
        SimulationEntity entity,
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        Matrix4x4 transform,
        Vector3 sceneAlignmentOffset)
    {
        float openProgress = ResolveBaseArmorOpenProgress(entity);
        if (openProgress <= 1e-4f)
        {
            DrawFineTerrainColoredTriangles(
                graphics,
                worldScale,
                triangles,
                transform,
                sceneAlignmentOffset);
            return;
        }

        Matrix4x4 openTransform = ResolveFineTerrainBaseOuterPanelOpenModelTransform(
            worldScale,
            item,
            unit: null,
            pivotModel: item.PivotModel,
            openProgress);
        Matrix4x4 translatedTransform = openTransform * transform;
        DrawFineTerrainColoredTriangles(
            graphics,
            worldScale,
            triangles,
            translatedTransform,
            sceneAlignmentOffset);
    }

    private static bool IsFineTerrainBaseMiddlePanel(string plateId)
        => string.Equals(plateId, "base_middle_front", StringComparison.OrdinalIgnoreCase)
            || string.Equals(plateId, "base_middle_left", StringComparison.OrdinalIgnoreCase)
            || string.Equals(plateId, "base_middle_right", StringComparison.OrdinalIgnoreCase)
            || string.Equals(plateId, "base_middle", StringComparison.OrdinalIgnoreCase);

    private static Vector3 ResolveFineTerrainBaseOuterPanelLocalNormal(
        FineTerrainBaseVisualItem item,
        FineTerrainBaseUnitVisualItem? unit = null)
    {
        string name = $"{item.Name ?? string.Empty} {unit?.Name ?? string.Empty} {unit?.PlateId ?? string.Empty}";
        if (name.Contains("left", StringComparison.OrdinalIgnoreCase)
            || name.Contains("\u5de6", StringComparison.Ordinal))
        {
            return -Vector3.UnitX;
        }

        if (name.Contains("right", StringComparison.OrdinalIgnoreCase)
            || name.Contains("\u53f3", StringComparison.Ordinal))
        {
            return Vector3.UnitX;
        }

        if (name.Contains("front", StringComparison.OrdinalIgnoreCase)
            || name.Contains("\u524d", StringComparison.Ordinal))
        {
            return -Vector3.UnitZ;
        }

        if (unit is not null)
        {
            Vector3 normal = new(unit.LocalNormalModel.X, 0f, unit.LocalNormalModel.Z);
            if (normal.LengthSquared() > 1e-8f)
            {
                return Vector3.Normalize(normal);
            }
        }

        return -Vector3.UnitZ;
    }

    private static Vector3 ResolveFineTerrainBaseOuterPanelLocalHingePivot(
        FineTerrainBaseVisualItem item,
        Vector3 normalModel)
    {
        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        bool initialized = false;
        foreach (FineTerrainColoredTriangle triangle in item.Triangles)
        {
            Include(triangle.A);
            Include(triangle.B);
            Include(triangle.C);
        }

        foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
        {
            foreach (FineTerrainColoredTriangle triangle in unit.Triangles)
            {
                Include(triangle.A);
                Include(triangle.B);
                Include(triangle.C);
            }
        }

        if (!initialized)
        {
            return item.PivotModel;
        }

        Vector3 center = (min + max) * 0.5f;
        center.Y = min.Y;
        if (MathF.Abs(normalModel.X) > MathF.Abs(normalModel.Z))
        {
            center.X = normalModel.X < 0f ? max.X : min.X;
        }
        else
        {
            center.Z = normalModel.Z < 0f ? max.Z : min.Z;
        }

        return center;

        void Include(Vector3 point)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
            initialized = true;
        }
    }

    private static Vector3 ResolveFineTerrainBaseFallbackPanelNormal(string plateId)
    {
        if (plateId.Contains("left", StringComparison.OrdinalIgnoreCase))
        {
            return -Vector3.UnitX;
        }

        if (plateId.Contains("right", StringComparison.OrdinalIgnoreCase))
        {
            return Vector3.UnitX;
        }

        return -Vector3.UnitZ;
    }

    private static Vector3 ResolveFineTerrainBasePanelHingePivotModel(
        FineTerrainBaseUnitVisualItem unit,
        Vector3 normalModel,
        FineTerrainWorldScale worldScale)
    {
        if (unit.Triangles.Count == 0)
        {
            return unit.LocalCentroidModel;
        }

        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (FineTerrainColoredTriangle triangle in unit.Triangles)
        {
            Include(triangle.A);
            Include(triangle.B);
            Include(triangle.C);
        }

        Vector3 center = (min + max) * 0.5f;
        float bottomY = min.Y;
        float inwardModelUnits = ResolveFineTerrainModelDistanceForMeters(worldScale, normalModel, 0.018f);
        center.Y = bottomY;
        center -= normalModel * inwardModelUnits;
        return center;

        void Include(Vector3 point)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }

    private static Vector3 ResolveFineTerrainBaseOuterPanelOpenOffsetModel(FineTerrainWorldScale worldScale)
    {
        float y = MathF.Abs(worldScale.YMetersPerModelUnit) <= 1e-6f
            ? 0f
            : -0.07f / worldScale.YMetersPerModelUnit;
        float z = MathF.Abs(worldScale.ZMetersPerModelUnit) <= 1e-6f
            ? 0f
            : -0.25f / worldScale.ZMetersPerModelUnit;
        return new Vector3(0f, y, z);
    }

    private static Matrix4x4 ResolveFineTerrainBaseOuterPanelOpenModelTransform(
        FineTerrainWorldScale worldScale,
        FineTerrainBaseVisualItem item,
        FineTerrainBaseUnitVisualItem? unit,
        Vector3 pivotModel,
        float openProgress)
    {
        float progress = Math.Clamp(openProgress, 0f, 1f);
        if (progress <= 1e-4f)
        {
            return Matrix4x4.Identity;
        }

        Vector3 xAxisModel = ResolveFineTerrainBaseOuterPanelCoordinateAxisModel(item, FineTerrainEditorAxis.X);
        if (xAxisModel.LengthSquared() <= 1e-8f)
        {
            xAxisModel = Vector3.UnitX;
        }

        Vector3 offsetModel = ResolveFineTerrainBaseOuterPanelOpenOffsetModel(
            worldScale,
            item,
            unit,
            pivotModel) * progress;
        float angleRad = 7f * MathF.PI / 180f * progress;
        return
            Matrix4x4.CreateTranslation(-pivotModel)
            * Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(xAxisModel), angleRad)
            * Matrix4x4.CreateTranslation(pivotModel)
            * Matrix4x4.CreateTranslation(offsetModel);
    }

    private static Vector3 ResolveFineTerrainBaseOuterPanelOpenOffsetModel(
        FineTerrainWorldScale worldScale,
        FineTerrainBaseVisualItem item,
        FineTerrainBaseUnitVisualItem? unit,
        Vector3 localCenterModel)
    {
        Vector3 panelOutModel = -ResolveFineTerrainBaseOuterPanelCoordinateAxisModel(item, FineTerrainEditorAxis.Z);
        Vector3 panelDownModel = -ResolveFineTerrainBaseOuterPanelCoordinateAxisModel(item, FineTerrainEditorAxis.Y);
        float outUnits = ResolveFineTerrainModelDistanceForMeters(worldScale, panelOutModel, 0.25f);
        float downUnits = ResolveFineTerrainModelDistanceForMeters(worldScale, panelDownModel, 0.07f);
        return panelOutModel * outUnits + panelDownModel * downUnits;
    }

    private static Vector3 ResolveFineTerrainBaseOuterPanelCoordinateAxisModel(
        FineTerrainBaseVisualItem item,
        FineTerrainEditorAxis axis)
    {
        Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(
            item.CoordinateYprDegrees.X * MathF.PI / 180f,
            item.CoordinateYprDegrees.Y * MathF.PI / 180f,
            item.CoordinateYprDegrees.Z * MathF.PI / 180f);
        Vector3 xAxis = SafeNormalize(Vector3.TransformNormal(Vector3.UnitX, rotation), Vector3.UnitX);
        Vector3 yCandidate = SafeNormalize(Vector3.TransformNormal(Vector3.UnitY, rotation), Vector3.UnitY);
        Vector3 zAxis = SafeNormalize(Vector3.Cross(xAxis, yCandidate), Vector3.UnitZ);
        Vector3 yAxis = SafeNormalize(Vector3.Cross(zAxis, xAxis), Vector3.UnitY);
        return axis switch
        {
            FineTerrainEditorAxis.X => xAxis,
            FineTerrainEditorAxis.Y => yAxis,
            FineTerrainEditorAxis.Z => zAxis,
            _ => Vector3.Zero,
        };
    }

    private static Vector3 ResolveFineTerrainBaseOuterPanelOpenOffsetModel(
        FineTerrainWorldScale worldScale,
        FineTerrainBaseVisualItem item,
        Vector3 localCenterModel)
        => ResolveFineTerrainBaseOuterPanelOpenOffsetModel(worldScale, item, unit: null, localCenterModel: localCenterModel);

    private static Vector3 ResolveFineTerrainBaseOuterPanelRadialOriginModel(FineTerrainBaseVisualItem item)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
        {
            if (!IsFineTerrainBaseOuterPanelUnit(item, unit))
            {
                continue;
            }

            sum += unit.LocalCentroidModel;
            count++;
        }

        if (count >= 2)
        {
            Vector3 center = sum / count;
            center.Y = item.PivotModel.Y;
            return center;
        }

        return item.PivotModel;
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
        StartFineTerrainCollisionAnnotationLoad();
    }

    private double ResolveFineTerrainVisualSceneLoadProgress()
    {
        MapPresetDefinition preset = ResolveFineTerrainVisualSourcePreset();
        if (string.IsNullOrWhiteSpace(preset.AnnotationPath) || !File.Exists(preset.AnnotationPath))
        {
            return 1.0;
        }

        string cacheKey = BuildFineTerrainSceneCacheKey();
        StartFineTerrainEnergySceneLoad(cacheKey);
        StartFineTerrainOutpostSceneLoad(cacheKey);
        StartFineTerrainBaseSceneLoad(cacheKey);
        StartFineTerrainCollisionAnnotationLoad();
        CompleteFineTerrainEnergySceneLoad(cacheKey);
        CompleteFineTerrainOutpostSceneLoad(cacheKey);
        CompleteFineTerrainBaseSceneLoad(cacheKey);
        CompleteFineTerrainCollisionAnnotationLoad();

        double energy = _fineTerrainEnergySceneLoadTask is null ? 1.0 : 0.35;
        double outpost = _fineTerrainOutpostSceneLoadTask is null ? 1.0 : 0.35;
        double baseTop = _fineTerrainBaseSceneLoadTask is null ? 1.0 : 0.35;
        double collision = _fineTerrainCollisionAnnotationLoadTask is null ? 1.0 : 0.35;
        return (energy + outpost + baseTop + collision) / 4.0;
    }

    private bool AreFineTerrainVisualScenesReady()
    {
        MapPresetDefinition preset = ResolveFineTerrainVisualSourcePreset();
        if (string.IsNullOrWhiteSpace(preset.AnnotationPath) || !File.Exists(preset.AnnotationPath))
        {
            return true;
        }

        string cacheKey = BuildFineTerrainSceneCacheKey();
        StartFineTerrainEnergySceneLoad(cacheKey);
        StartFineTerrainOutpostSceneLoad(cacheKey);
        StartFineTerrainBaseSceneLoad(cacheKey);
        StartFineTerrainCollisionAnnotationLoad();
        CompleteFineTerrainEnergySceneLoad(cacheKey);
        CompleteFineTerrainOutpostSceneLoad(cacheKey);
        CompleteFineTerrainBaseSceneLoad(cacheKey);
        CompleteFineTerrainCollisionAnnotationLoad();
        return _fineTerrainEnergySceneLoadTask is null
            && _fineTerrainOutpostSceneLoadTask is null
            && _fineTerrainBaseSceneLoadTask is null
            && _fineTerrainCollisionAnnotationLoadTask is null;
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
        MapPresetDefinition preset = ResolveFineTerrainVisualSourcePreset();
        string annotationPath = preset.AnnotationPath;
        string terrainKey = preset.RuntimeGrid?.SourcePath ?? string.Empty;
        return $"{annotationPath}|{terrainKey}|{preset.FieldLengthM:0.###}|{preset.FieldWidthM:0.###}|{_host.MapPreset.Name}";
    }

    private MapPresetDefinition ResolveFineTerrainVisualSourcePreset()
    {
        if (!ShouldUseUnitTestNewMapComposites())
        {
            return _host.MapPreset;
        }

        string mapPath = Path.Combine(_host.ProjectRootPath, "maps", "rmuc2026", "map.json");
        string annotationPath = Path.Combine(_host.ProjectRootPath, "maps", "rmuc2026", "RMUC2026_MAP.component_roles.json");
        string terrainCachePath = Path.Combine(_host.ProjectRootPath, "maps", "rmuc2026", "RMUC2026_MAP.terraincache.lz4");
        if (!File.Exists(annotationPath) || !File.Exists(terrainCachePath))
        {
            return _host.MapPreset;
        }

        return new MapPresetDefinition
        {
            Name = $"{_host.MapPreset.Name}_rmuc2026_composites",
            Width = _host.MapPreset.Width,
            Height = _host.MapPreset.Height,
            FieldLengthM = _host.MapPreset.FieldLengthM,
            FieldWidthM = _host.MapPreset.FieldWidthM,
            ImagePath = _host.MapPreset.ImagePath,
            SourcePath = File.Exists(mapPath) ? mapPath : _host.MapPreset.SourcePath,
            AnnotationPath = annotationPath,
            Facilities = _host.MapPreset.Facilities,
            CoordinateSystem = _host.MapPreset.CoordinateSystem,
            TerrainSurface = _host.MapPreset.TerrainSurface,
            RuntimeGrid = new RuntimeGridDefinition
            {
                SourcePath = terrainCachePath,
                ResolutionM = _host.MapPreset.RuntimeGrid?.ResolutionM ?? 0.01,
            },
        };
    }

    private bool ShouldUseUnitTestNewMapComposites()
        => _host.IsUnitTestMode
            || string.Equals(_host.MapPreset.Name, "unit_test", StringComparison.OrdinalIgnoreCase);

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

        MapPresetDefinition preset = ResolveFineTerrainVisualSourcePreset();
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

        MapPresetDefinition preset = ResolveFineTerrainVisualSourcePreset();
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

        MapPresetDefinition preset = ResolveFineTerrainVisualSourcePreset();
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

    private string? BuildFineTerrainCollisionAnnotationCacheKey()
    {
        string annotationPath = _host.MapPreset.AnnotationPath;
        if (string.IsNullOrWhiteSpace(annotationPath) || !File.Exists(annotationPath))
        {
            return null;
        }

        return $"{Path.GetFullPath(annotationPath)}|{File.GetLastWriteTimeUtc(annotationPath).Ticks}";
    }

    private void StartFineTerrainCollisionAnnotationLoad()
    {
        string? cacheKey = BuildFineTerrainCollisionAnnotationCacheKey();
        if (cacheKey is null)
        {
            _fineTerrainCollisionAnnotation = null;
            _fineTerrainCollisionAnnotationKey = null;
            _fineTerrainCollisionAnnotationLoadTask = null;
            _fineTerrainCollisionAnnotationLoadingKey = null;
            return;
        }

        if (string.Equals(_fineTerrainCollisionAnnotationKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_fineTerrainCollisionAnnotationLoadTask is not null
            && string.Equals(_fineTerrainCollisionAnnotationLoadingKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string annotationPath = _host.MapPreset.AnnotationPath;
        _fineTerrainCollisionAnnotationLoadTask = Task.Run(() => FineTerrainAnnotationDocument.TryLoadCollisionOnly(annotationPath));
        _fineTerrainCollisionAnnotationLoadingKey = cacheKey;
    }

    private void CompleteFineTerrainCollisionAnnotationLoad()
    {
        if (_fineTerrainCollisionAnnotationLoadTask is null
            || _fineTerrainCollisionAnnotationLoadingKey is null
            || !_fineTerrainCollisionAnnotationLoadTask.IsCompleted)
        {
            return;
        }

        string cacheKey = _fineTerrainCollisionAnnotationLoadingKey;
        try
        {
            _fineTerrainCollisionAnnotation = _fineTerrainCollisionAnnotationLoadTask.Result;
            _fineTerrainCollisionAnnotationKey = cacheKey;
        }
        catch
        {
            _fineTerrainCollisionAnnotation = null;
            _fineTerrainCollisionAnnotationKey = cacheKey;
        }
        finally
        {
            _fineTerrainCollisionAnnotationLoadTask = null;
            _fineTerrainCollisionAnnotationLoadingKey = null;
        }
    }

    private FineTerrainAnnotationDocument? ResolveFineTerrainCollisionAnnotation()
    {
        string? cacheKey = BuildFineTerrainCollisionAnnotationCacheKey();
        if (cacheKey is null)
        {
            _fineTerrainCollisionAnnotation = null;
            _fineTerrainCollisionAnnotationKey = null;
            _fineTerrainCollisionAnnotationLoadTask = null;
            _fineTerrainCollisionAnnotationLoadingKey = null;
            return null;
        }

        CompleteFineTerrainCollisionAnnotationLoad();
        if (string.Equals(_fineTerrainCollisionAnnotationKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return _fineTerrainCollisionAnnotation;
        }

        StartFineTerrainCollisionAnnotationLoad();
        return null;
    }

    private bool TryDrawFineTerrainCollisionShapes(Graphics graphics)
    {
        FineTerrainAnnotationDocument? annotation = ResolveFineTerrainCollisionAnnotation();
        if (annotation is null || annotation.CollisionShapes.Count == 0)
        {
            return false;
        }

        bool drew = false;
        foreach (FineTerrainCollisionShapeAnnotation shape in annotation.CollisionShapes)
        {
            drew |= TryDrawFineTerrainCollisionShape(graphics, annotation.WorldScale, shape);
        }

        return drew;
    }

    private bool TryDrawGpuFineTerrainCollisionShapes()
    {
        FineTerrainAnnotationDocument? annotation = ResolveFineTerrainCollisionAnnotation();
        if (annotation is null || annotation.CollisionShapes.Count == 0)
        {
            return false;
        }

        bool drew = false;
        foreach (FineTerrainCollisionShapeAnnotation shape in annotation.CollisionShapes)
        {
            drew |= TryDrawGpuFineTerrainCollisionShape(annotation.WorldScale, shape);
        }

        return drew;
    }

    private bool TryDrawFineTerrainCollisionShape(
        Graphics graphics,
        FineTerrainWorldScale worldScale,
        FineTerrainCollisionShapeAnnotation shape)
    {
        Color baseColor = ResolveFineTerrainCollisionShapeDisplayColor(shape, Color.FromArgb(255, 0, 210, 255));
        bool staticMesh = IsFineTerrainStaticMeshShape(shape);
        Color fillColor = Color.FromArgb(staticMesh ? 255 : 104, baseColor);
        Color edgeColor = Color.FromArgb(245, BlendColor(baseColor, Color.White, staticMesh ? 0.08f : 0.16f));
        IReadOnlyList<Vector3> bottom = BuildCollisionShapeBottomFootprint(shape, worldScale, out float height);
        if (bottom.Count < 3 || height <= 1e-4f)
        {
            return false;
        }

        Vector3 offset = Vector3.UnitY * height;
        var top = bottom.Select(point => point + offset).ToArray();
        DrawGeneralPrism(graphics, bottom, top, fillColor, edgeColor, null);
        return true;
    }

    private bool TryDrawGpuFineTerrainCollisionShape(FineTerrainWorldScale worldScale, FineTerrainCollisionShapeAnnotation shape)
    {
        Color baseColor = ResolveFineTerrainCollisionShapeDisplayColor(shape, Color.FromArgb(255, 0, 210, 255));
        bool staticMesh = IsFineTerrainStaticMeshShape(shape);
        Color fillColor = Color.FromArgb(staticMesh ? 255 : 96, baseColor);
        Color edgeColor = Color.FromArgb(245, BlendColor(baseColor, Color.White, staticMesh ? 0.08f : 0.16f));
        IReadOnlyList<Vector3> bottom = BuildCollisionShapeBottomFootprint(shape, worldScale, out float height);
        if (bottom.Count < 3 || height <= 1e-4f)
        {
            return false;
        }

        Vector3 offset = Vector3.UnitY * height;
        var top = bottom.Select(point => point + offset).ToArray();
        DrawGpuGeneralPrism(bottom, top, fillColor);
        for (int index = 0; index < bottom.Count; index++)
        {
            int next = (index + 1) % bottom.Count;
            DrawGpuLine(bottom[index], bottom[next], edgeColor);
            DrawGpuLine(top[index], top[next], edgeColor);
            DrawGpuLine(bottom[index], top[index], edgeColor);
        }

        return true;
    }

    private static bool IsFineTerrainStaticMeshShape(FineTerrainCollisionShapeAnnotation shape)
        => string.Equals(shape.TerrainLabel, "static_mesh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shape.TerrainLabel, "static", StringComparison.OrdinalIgnoreCase);

    private static Color ResolveFineTerrainCollisionShapeDisplayColor(
        FineTerrainCollisionShapeAnnotation shape,
        Color fallback)
    {
        string raw = shape.ColorHex.Trim();
        if (raw.Length == 0)
        {
            return fallback;
        }

        if (raw[0] == '#')
        {
            raw = raw[1..];
        }

        if (raw.Length == 6
            && int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        if (raw.Length == 8
            && uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            return Color.FromArgb(unchecked((int)argb));
        }

        return fallback;
    }

    private IReadOnlyList<Vector3> BuildCollisionShapeBottomFootprint(
        FineTerrainCollisionShapeAnnotation shape,
        FineTerrainWorldScale worldScale,
        out float height)
    {
        IReadOnlyList<Vector3> modelVertices = BuildCollisionShapeModelVertices(shape, worldScale);
        if (modelVertices.Count < 3)
        {
            height = 0f;
            return Array.Empty<Vector3>();
        }

        var sceneVertices = modelVertices
            .Select(vertex => CollisionShapeModelToScenePoint(vertex, worldScale))
            .ToArray();
        float minY = sceneVertices.Min(vertex => vertex.Y);
        float maxY = sceneVertices.Max(vertex => vertex.Y);
        height = Math.Max(0.03f, maxY - minY);
        Vector2[] hull = BuildSceneFootprintHull(sceneVertices.Select(vertex => new Vector2(vertex.X, vertex.Z)).ToArray());
        if (hull.Length < 3)
        {
            return Array.Empty<Vector3>();
        }

        return hull.Select(point => new Vector3(point.X, minY, point.Y)).ToArray();
    }

    private static IReadOnlyList<Vector3> BuildCollisionShapeModelVertices(
        FineTerrainCollisionShapeAnnotation shape,
        FineTerrainWorldScale worldScale)
    {
        string shapeType = shape.ShapeType.Trim();
        if (shapeType.Equals("polyhedron", StringComparison.OrdinalIgnoreCase)
            && shape.VerticesModel.Count >= 3)
        {
            return shape.VerticesModel.Select(vertex => vertex.ToVector3()).ToArray();
        }

        Vector3 modelCenter = ResolveCollisionShapeModelCenter(shape, worldScale);
        Vector3 ypr = shape.YprDegrees.ToVector3();
        Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(
            ypr.X * MathF.PI / 180f,
            ypr.Y * MathF.PI / 180f,
            ypr.Z * MathF.PI / 180f);

        if (shapeType.Equals("cylinder", StringComparison.OrdinalIgnoreCase))
        {
            const int Segments = 20;
            float radius = Math.Max(0.001f, shape.RadiusModel);
            float halfHeight = Math.Max(0.001f, shape.HeightModel) * 0.5f;
            var vertices = new Vector3[Segments * 2];
            for (int index = 0; index < Segments; index++)
            {
                float radians = MathF.Tau * index / Segments;
                Vector3 radial = new(MathF.Cos(radians) * radius, 0f, MathF.Sin(radians) * radius);
                vertices[index] = modelCenter + Vector3.Transform(radial - Vector3.UnitY * halfHeight, rotation);
                vertices[index + Segments] = modelCenter + Vector3.Transform(radial + Vector3.UnitY * halfHeight, rotation);
            }

            return vertices;
        }

        if (shapeType.Equals("quad_prism", StringComparison.OrdinalIgnoreCase)
            || shapeType.Equals("rect_prism", StringComparison.OrdinalIgnoreCase)
            || shapeType.Equals("square_prism", StringComparison.OrdinalIgnoreCase))
        {
            Vector3 prismSize = shape.SizeModel.ToVector3();
            float halfHeight = Math.Max(0.001f, shape.HeightModel > 1e-4f ? shape.HeightModel : prismSize.Y) * 0.5f;
            float halfX = Math.Max(0.001f, prismSize.X) * 0.5f;
            float halfZ = Math.Max(0.001f, Math.Abs(prismSize.Z) > 1e-4f ? prismSize.Z : prismSize.X) * 0.5f;
            Vector3[] bottom =
            {
                new(-halfX, -halfHeight, -halfZ),
                new(halfX, -halfHeight, -halfZ),
                new(halfX, -halfHeight, halfZ),
                new(-halfX, -halfHeight, halfZ),
            };
            return BuildCollisionShapePrismModelVertices(modelCenter, rotation, bottom, halfHeight);
        }

        if (shapeType.Equals("hex_prism", StringComparison.OrdinalIgnoreCase)
            || shapeType.Equals("hexagon_prism", StringComparison.OrdinalIgnoreCase))
        {
            float radius = Math.Max(0.001f, shape.RadiusModel);
            float halfHeight = Math.Max(0.001f, shape.HeightModel) * 0.5f;
            var bottom = new Vector3[6];
            for (int index = 0; index < bottom.Length; index++)
            {
                float radians = MathF.Tau * index / bottom.Length + MathF.PI / 6f;
                bottom[index] = new Vector3(
                    MathF.Cos(radians) * radius,
                    -halfHeight,
                    MathF.Sin(radians) * radius);
            }

            return BuildCollisionShapePrismModelVertices(modelCenter, rotation, bottom, halfHeight);
        }

        Vector3 size = shape.SizeModel.ToVector3();
        Vector3 half = new(
            Math.Max(0.001f, size.X) * 0.5f,
            Math.Max(0.001f, size.Y) * 0.5f,
            Math.Max(0.001f, size.Z) * 0.5f);
        var boxVertices = new Vector3[8];
        int cursor = 0;
        foreach (float localZ in new[] { -half.Z, half.Z })
        {
            foreach (float localY in new[] { -half.Y, half.Y })
            {
                foreach (float localX in new[] { -half.X, half.X })
                {
                    boxVertices[cursor++] = modelCenter + Vector3.Transform(new Vector3(localX, localY, localZ), rotation);
                }
            }
        }

        return boxVertices;
    }

    private static Vector3 ResolveCollisionShapeModelCenter(
        FineTerrainCollisionShapeAnnotation shape,
        FineTerrainWorldScale worldScale)
    {
        Vector3 center = shape.PositionModel.ToVector3();
        float requestedHeightModel = Math.Max(0.001f, ResolveCollisionShapeRequestedHeightModel(shape));
        float minimumCenterY = worldScale.ModelMinY + requestedHeightModel * 0.38f;
        if (center.Y < minimumCenterY && center.Y >= -0.25f && center.Y <= 20f)
        {
            float centerHeightM = Math.Max(0f, center.Y);
            center.Y = worldScale.ModelMinY + centerHeightM / MathF.Max(worldScale.YMetersPerModelUnit, 1e-6f);
        }

        return center;
    }

    private static float ResolveCollisionShapeRequestedHeightModel(FineTerrainCollisionShapeAnnotation shape)
    {
        string shapeType = shape.ShapeType.Trim();
        if (shapeType.Equals("cylinder", StringComparison.OrdinalIgnoreCase)
            || shapeType.Equals("hex_prism", StringComparison.OrdinalIgnoreCase)
            || shapeType.Equals("hexagon_prism", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(0.0f, shape.HeightModel);
        }

        Vector3 size = shape.SizeModel.ToVector3();
        if (shape.HeightModel > 1e-4f)
        {
            return shape.HeightModel;
        }

        return Math.Max(Math.Max(0.0f, size.Y), Math.Max(0.0f, size.Z));
    }

    private static IReadOnlyList<Vector3> BuildCollisionShapePrismModelVertices(
        Vector3 center,
        Matrix4x4 rotation,
        IReadOnlyList<Vector3> bottom,
        float halfHeight)
    {
        var vertices = new Vector3[bottom.Count * 2];
        for (int index = 0; index < bottom.Count; index++)
        {
            Vector3 basePoint = bottom[index];
            vertices[index] = center + Vector3.Transform(basePoint, rotation);
            vertices[index + bottom.Count] = center + Vector3.Transform(
                new Vector3(basePoint.X, halfHeight, basePoint.Z),
                rotation);
        }

        return vertices;
    }

    private static Vector2[] BuildSceneFootprintHull(IReadOnlyList<Vector2> points)
    {
        var sorted = points
            .OrderBy(point => point.X)
            .ThenBy(point => point.Y)
            .DistinctBy(point => ((int)MathF.Round(point.X * 1000f), (int)MathF.Round(point.Y * 1000f)))
            .ToArray();
        if (sorted.Length <= 2)
        {
            return sorted;
        }

        static float Cross(Vector2 origin, Vector2 a, Vector2 b)
            => (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);

        var hull = new List<Vector2>(sorted.Length * 2);
        foreach (Vector2 point in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0f)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(point);
        }

        int lowerCount = hull.Count;
        for (int index = sorted.Length - 2; index >= 0; index--)
        {
            Vector2 point = sorted[index];
            while (hull.Count > lowerCount && Cross(hull[^2], hull[^1], point) <= 0f)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(point);
        }

        if (hull.Count > 1)
        {
            hull.RemoveAt(hull.Count - 1);
        }

        return hull.ToArray();
    }
}
