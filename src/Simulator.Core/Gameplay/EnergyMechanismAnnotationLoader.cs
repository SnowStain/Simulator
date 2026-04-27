using System.Numerics;
using System.Text.Json;
using Simulator.Core.Map;

namespace Simulator.Core.Gameplay;

internal sealed class AnnotatedEnergyMechanismDefinition
{
    public required IReadOnlyDictionary<string, AnnotatedEnergyTeamDefinition> Teams { get; init; }

    public required IReadOnlyList<ArmorPlateTarget> AllTargets { get; init; }

    public required double CenterWorldX { get; init; }

    public required double CenterWorldY { get; init; }

    public required double MaxRadiusM { get; init; }

    public required double ApproximateHeightM { get; init; }
}

internal sealed class AnnotatedEnergyTeamDefinition
{
    public required string Team { get; init; }

    public required double PivotWorldX { get; init; }

    public required double PivotWorldY { get; init; }

    public required double PivotHeightM { get; init; }

    public required Vector3 RotorAxisWorld { get; init; }

    public required IReadOnlyList<AnnotatedEnergyPlateState> PlateStates { get; init; }

    public required IReadOnlyList<ArmorPlateTarget> Targets { get; init; }
}

internal readonly record struct AnnotatedEnergyPlateState(
    string PlateId,
    int RingScore,
    double BaseOffsetXM,
    double BaseOffsetYM,
    double BaseOffsetZM,
    double BaseNormalXM,
    double BaseNormalYM,
    double BaseNormalZM,
    double SideLengthM,
    double WidthM,
    double HeightSpanM,
    double BaseYawDeg);

internal static class EnergyMechanismAnnotationLoader
{
    private const string EnergyMechanismKeyword = "\u80fd\u91cf\u673a\u5173";
    private const string RedTeamKeyword = "\u7ea2\u65b9";
    private const string BlueTeamKeyword = "\u84dd\u65b9";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Dictionary<string, AnnotatedEnergyMechanismDefinition> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object CacheGate = new();

    public static AnnotatedEnergyMechanismDefinition? TryLoad(
        MapPresetDefinition mapPreset,
        double metersPerWorldUnit)
    {
        string annotationPath = mapPreset.AnnotationPath;
        if (string.IsNullOrWhiteSpace(annotationPath) || !File.Exists(annotationPath))
        {
            return null;
        }

        string cacheKey = BuildCacheKey(mapPreset, annotationPath, metersPerWorldUnit);

        lock (CacheGate)
        {
            if (Cache.TryGetValue(cacheKey, out AnnotatedEnergyMechanismDefinition? cached))
            {
                return cached;
            }
        }

        using FileStream stream = File.OpenRead(annotationPath);
        ComponentAnnotationFile? file = JsonSerializer.Deserialize<ComponentAnnotationFile>(stream, JsonOptions);
        if (file?.Components is null || file.Composites is null)
        {
            return null;
        }

        Dictionary<int, ComponentAnnotation> componentsById = file.Components
            .Where(component => component.Bounds is not null)
            .ToDictionary(component => component.Id, component => component);
        bool hasRuntimeSceneScale = FineTerrainRuntimeSceneScaleResolver.TryResolve(mapPreset, out FineTerrainRuntimeSceneScale runtimeSceneScale);
        Vector3 modelCenter = hasRuntimeSceneScale
            ? runtimeSceneScale.ModelCenter
            : ResolveModelCenter(file.Components);
        double xMetersPerModelUnit = hasRuntimeSceneScale
            ? runtimeSceneScale.XMetersPerModelUnit
            : Math.Max(1e-6, file.WorldScale?.XMetersPerModelUnit ?? 1.0f);
        double yMetersPerModelUnit = hasRuntimeSceneScale
            ? runtimeSceneScale.YMetersPerModelUnit
            : Math.Max(1e-6, file.WorldScale?.YMetersPerModelUnit ?? xMetersPerModelUnit);
        double zMetersPerModelUnit = hasRuntimeSceneScale
            ? runtimeSceneScale.ZMetersPerModelUnit
            : Math.Max(1e-6, file.WorldScale?.ZMetersPerModelUnit ?? xMetersPerModelUnit);
        var teams = new Dictionary<string, AnnotatedEnergyTeamDefinition>(StringComparer.OrdinalIgnoreCase);
        var allTargets = new List<ArmorPlateTarget>(128);
        double sumWorldX = 0.0;
        double sumWorldY = 0.0;
        int centerCount = 0;
        double fallbackSumWorldX = 0.0;
        double fallbackSumWorldY = 0.0;
        int fallbackCenterCount = 0;
        double maxRadiusM = 0.0;
        double maxHeightM = 0.0;

        foreach (CompositeAnnotation composite in file.Composites)
        {
            if (string.IsNullOrWhiteSpace(composite.Name)
                || !composite.Name.Contains(EnergyMechanismKeyword, StringComparison.Ordinal)
                || !TryResolveTeam(composite.Name, out string team))
            {
                continue;
            }

            double yawDeg = ResolveEnergyPlateYawDeg(composite);
            Vector3 pivotModel = ResolveCompositePivotModel(composite);
            Vector3 positionModel = ResolveCompositePositionModel(composite, pivotModel);
            Vector3 rotationYprDegrees = ResolveCompositeRotationYprDegrees(composite);
            Vector3 rotorAxisModel = ResolveEnergyRotationAxisModel(composite);
            (double pivotWorldX, double pivotWorldY, double pivotHeightM) = FineTerrainAnnotationWorldSpace.CompositeModelPointToWorld(
                mapPreset.FieldLengthM,
                mapPreset.FieldWidthM,
                metersPerWorldUnit,
                modelCenter.X,
                modelCenter.Y,
                modelCenter.Z,
                xMetersPerModelUnit,
                yMetersPerModelUnit,
                zMetersPerModelUnit,
                pivotModel,
                positionModel,
                rotationYprDegrees,
                pivotModel);
            (double axisWorldX, double axisWorldY, double axisHeightM) = FineTerrainAnnotationWorldSpace.CompositeModelPointToWorld(
                mapPreset.FieldLengthM,
                mapPreset.FieldWidthM,
                metersPerWorldUnit,
                modelCenter.X,
                modelCenter.Y,
                modelCenter.Z,
                xMetersPerModelUnit,
                yMetersPerModelUnit,
                zMetersPerModelUnit,
                pivotModel,
                positionModel,
                rotationYprDegrees,
                pivotModel + rotorAxisModel);
            Vector3 rotorAxisWorld = new(
                (float)((axisWorldX - pivotWorldX) * metersPerWorldUnit),
                (float)(axisHeightM - pivotHeightM),
                (float)((axisWorldY - pivotWorldY) * metersPerWorldUnit));
            if (rotorAxisWorld.LengthSquared() <= 1e-8f)
            {
                rotorAxisWorld = Vector3.UnitY;
            }
            else
            {
                rotorAxisWorld = Vector3.Normalize(rotorAxisWorld);
            }

            var targets = new List<ArmorPlateTarget>(64);
            var plateStates = new List<AnnotatedEnergyPlateState>(64);
            double compositeSumWorldX = 0.0;
            double compositeSumWorldY = 0.0;
            int compositeTargetCount = 0;
            foreach (InteractionUnitAnnotation unit in composite.InteractionUnits ?? Array.Empty<InteractionUnitAnnotation>())
            {
                if (string.IsNullOrWhiteSpace(unit.Name)
                    || unit.ComponentIds is null
                    || !TryParseEnergyUnit(unit.Name, out int armIndex, out int ringScore))
                {
                    continue;
                }

                if (!TryResolveMergedBounds(unit.ComponentIds, componentsById, out MergedBounds bounds))
                {
                    continue;
                }

                (double worldX, double worldY, double heightM) = FineTerrainAnnotationWorldSpace.CompositeModelPointToWorld(
                    mapPreset.FieldLengthM,
                    mapPreset.FieldWidthM,
                    metersPerWorldUnit,
                    modelCenter.X,
                    modelCenter.Y,
                    modelCenter.Z,
                    xMetersPerModelUnit,
                    yMetersPerModelUnit,
                    zMetersPerModelUnit,
                    pivotModel,
                    positionModel,
                    rotationYprDegrees,
                    new Vector3(
                        bounds.CenterModelX,
                        bounds.CenterModelY,
                        bounds.CenterModelZ));
                double widthM = Math.Max(
                    0.02,
                    Math.Max(
                        bounds.SizeModelX * xMetersPerModelUnit,
                        bounds.SizeModelZ * zMetersPerModelUnit));
                double heightSpanM = Math.Max(0.02, bounds.SizeModelY * yMetersPerModelUnit);
                var target = new ArmorPlateTarget(
                    $"energy_{team}_arm_{armIndex}_ring_{ringScore}",
                    worldX,
                    worldY,
                    heightM,
                    yawDeg,
                    Math.Max(widthM, heightSpanM),
                    widthM,
                    heightSpanM,
                    ringScore);
                targets.Add(target);
                double baseYawRad = yawDeg * Math.PI / 180.0;
                plateStates.Add(new AnnotatedEnergyPlateState(
                    target.Id,
                    ringScore,
                    (worldX - pivotWorldX) * metersPerWorldUnit,
                    heightM - pivotHeightM,
                    (worldY - pivotWorldY) * metersPerWorldUnit,
                    Math.Cos(baseYawRad),
                    0.0,
                    Math.Sin(baseYawRad),
                    target.SideLengthM,
                    target.WidthM,
                    target.HeightSpanM,
                    target.YawDeg));
                allTargets.Add(target);
                compositeSumWorldX += worldX;
                compositeSumWorldY += worldY;
                compositeTargetCount++;
                fallbackSumWorldX += worldX;
                fallbackSumWorldY += worldY;
                fallbackCenterCount++;
                maxHeightM = Math.Max(maxHeightM, heightM + heightSpanM * 0.5);
            }

            if (targets.Count == 0)
            {
                continue;
            }

            double compositeCenterWorldX = compositeSumWorldX / Math.Max(1, compositeTargetCount);
            double compositeCenterWorldY = compositeSumWorldY / Math.Max(1, compositeTargetCount);
            foreach (ArmorPlateTarget target in targets)
            {
                double dxM = (target.X - compositeCenterWorldX) * metersPerWorldUnit;
                double dyM = (target.Y - compositeCenterWorldY) * metersPerWorldUnit;
                maxRadiusM = Math.Max(maxRadiusM, Math.Sqrt(dxM * dxM + dyM * dyM) + target.SideLengthM * 0.5);
            }

            sumWorldX += compositeCenterWorldX;
            sumWorldY += compositeCenterWorldY;
            centerCount++;
            teams[team] = new AnnotatedEnergyTeamDefinition
            {
                Team = team,
                PivotWorldX = pivotWorldX,
                PivotWorldY = pivotWorldY,
                PivotHeightM = pivotHeightM,
                RotorAxisWorld = rotorAxisWorld,
                PlateStates = plateStates
                    .OrderBy(target => ResolveArmSortKey(target.PlateId))
                    .ThenByDescending(target => target.RingScore)
                    .ToArray(),
                Targets = targets
                    .OrderBy(target => ResolveArmSortKey(target.Id))
                    .ThenByDescending(target => target.EnergyRingScore)
                    .ToArray(),
            };
        }

        if (teams.Count == 0 || allTargets.Count == 0)
        {
            return null;
        }

        var definition = new AnnotatedEnergyMechanismDefinition
        {
            Teams = teams,
            AllTargets = allTargets
                .OrderBy(target => ResolveArmSortKey(target.Id))
                .ThenByDescending(target => target.EnergyRingScore)
                .ToArray(),
            CenterWorldX = centerCount > 0
                ? sumWorldX / centerCount
                : (fallbackCenterCount > 0 ? fallbackSumWorldX / fallbackCenterCount : 0.0),
            CenterWorldY = centerCount > 0
                ? sumWorldY / centerCount
                : (fallbackCenterCount > 0 ? fallbackSumWorldY / fallbackCenterCount : 0.0),
            MaxRadiusM = Math.Max(0.60, maxRadiusM),
            ApproximateHeightM = Math.Max(1.40, maxHeightM),
        };

        lock (CacheGate)
        {
            Cache[cacheKey] = definition;
        }

        return definition;
    }

    private static Vector3 ResolveModelCenter(IReadOnlyList<ComponentAnnotation> components)
    {
        if (components.Count == 0)
        {
            return Vector3.Zero;
        }

        bool initialized = false;
        Vector3 min = Vector3.Zero;
        Vector3 max = Vector3.Zero;
        foreach (ComponentAnnotation component in components)
        {
            if (component.Bounds?.Min is null
                || component.Bounds?.Max is null
                || component.Bounds.Min.Length < 3
                || component.Bounds.Max.Length < 3)
            {
                continue;
            }

            Vector3 componentMin = new(component.Bounds.Min[0], component.Bounds.Min[1], component.Bounds.Min[2]);
            Vector3 componentMax = new(component.Bounds.Max[0], component.Bounds.Max[1], component.Bounds.Max[2]);
            if (!initialized)
            {
                min = componentMin;
                max = componentMax;
                initialized = true;
                continue;
            }

            min = Vector3.Min(min, componentMin);
            max = Vector3.Max(max, componentMax);
        }

        return initialized ? (min + max) * 0.5f : Vector3.Zero;
    }

    private static bool TryResolveTeam(string name, out string team)
    {
        if (name.Contains(RedTeamKeyword, StringComparison.Ordinal))
        {
            team = "red";
            return true;
        }

        if (name.Contains(BlueTeamKeyword, StringComparison.Ordinal))
        {
            team = "blue";
            return true;
        }

        team = string.Empty;
        return false;
    }

    private static bool TryParseEnergyUnit(string name, out int armIndex, out int ringScore)
    {
        armIndex = -1;
        ringScore = 0;
        string[] parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3
            || !int.TryParse(parts[1], out int parsedArm)
            || parsedArm < 1
            || parsedArm > 5
            || !int.TryParse(parts[2], out int parsedRing)
            || parsedRing < 1
            || parsedRing > 10)
        {
            return false;
        }

        armIndex = parsedArm - 1;
        ringScore = parsedRing;
        return true;
    }

    private static bool TryResolveMergedBounds(
        IReadOnlyList<int> componentIds,
        IReadOnlyDictionary<int, ComponentAnnotation> componentsById,
        out MergedBounds bounds)
    {
        bounds = default;
        bool initialized = false;
        float minX = 0f;
        float minY = 0f;
        float minZ = 0f;
        float maxX = 0f;
        float maxY = 0f;
        float maxZ = 0f;
        foreach (int componentId in componentIds)
        {
            if (!componentsById.TryGetValue(componentId, out ComponentAnnotation? component)
                || component.Bounds?.Min is null
                || component.Bounds?.Max is null
                || component.Bounds.Min.Length < 3
                || component.Bounds.Max.Length < 3)
            {
                continue;
            }

            if (!initialized)
            {
                minX = component.Bounds.Min[0];
                minY = component.Bounds.Min[1];
                minZ = component.Bounds.Min[2];
                maxX = component.Bounds.Max[0];
                maxY = component.Bounds.Max[1];
                maxZ = component.Bounds.Max[2];
                initialized = true;
                continue;
            }

            minX = MathF.Min(minX, component.Bounds.Min[0]);
            minY = MathF.Min(minY, component.Bounds.Min[1]);
            minZ = MathF.Min(minZ, component.Bounds.Min[2]);
            maxX = MathF.Max(maxX, component.Bounds.Max[0]);
            maxY = MathF.Max(maxY, component.Bounds.Max[1]);
            maxZ = MathF.Max(maxZ, component.Bounds.Max[2]);
        }

        if (!initialized)
        {
            return false;
        }

        bounds = new MergedBounds(
            (minX + maxX) * 0.5f,
            (minY + maxY) * 0.5f,
            (minZ + maxZ) * 0.5f,
            MathF.Max(0f, maxX - minX),
            MathF.Max(0f, maxY - minY),
            MathF.Max(0f, maxZ - minZ));
        return true;
    }

    private static double ResolveEnergyPlateYawDeg(CompositeAnnotation composite)
    {
        if (composite.CoordinateYprDegrees is not null)
        {
            return NormalizeDeg(composite.CoordinateYprDegrees.X + 90.0);
        }

        if (composite.YprDegrees is not null)
        {
            return NormalizeDeg(composite.YprDegrees.X + 90.0);
        }

        return 45.0;
    }

    private static Vector3 ResolveEnergyRotationAxisModel(CompositeAnnotation composite)
    {
        if (composite.CoordinateYprDegrees is null)
        {
            return Vector3.UnitX;
        }

        Vector3 coordinateYprDegrees = composite.CoordinateYprDegrees.ToVector3();
        Matrix4x4 coordinateRotation = Matrix4x4.CreateFromYawPitchRoll(
            coordinateYprDegrees.X * MathF.PI / 180f,
            coordinateYprDegrees.Y * MathF.PI / 180f,
            coordinateYprDegrees.Z * MathF.PI / 180f);
        Vector3 axis = Vector3.TransformNormal(Vector3.UnitX, coordinateRotation);
        return axis.LengthSquared() <= 1e-8f
            ? Vector3.UnitX
            : Vector3.Normalize(axis);
    }

    private static int ResolveArmSortKey(string plateId)
        => SimulationCombatMath.TryParseEnergyArmIndex(plateId, out _, out int armIndex) ? armIndex : int.MaxValue;

    private static string BuildCacheKey(MapPresetDefinition mapPreset, string annotationPath, double metersPerWorldUnit)
    {
        string terrainCachePath = ResolveTerrainCachePath(mapPreset);
        long terrainTicks = !string.IsNullOrWhiteSpace(terrainCachePath) && File.Exists(terrainCachePath)
            ? File.GetLastWriteTimeUtc(terrainCachePath).Ticks
            : 0L;
        return $"{Path.GetFullPath(annotationPath)}|{File.GetLastWriteTimeUtc(annotationPath).Ticks}|{terrainCachePath}|{terrainTicks}|{metersPerWorldUnit:0.########}";
    }

    private static string ResolveTerrainCachePath(MapPresetDefinition mapPreset)
    {
        if (mapPreset.RuntimeGrid is null || string.IsNullOrWhiteSpace(mapPreset.RuntimeGrid.SourcePath))
        {
            return string.Empty;
        }

        string? mapDirectory = Path.GetDirectoryName(mapPreset.SourcePath);
        if (string.IsNullOrWhiteSpace(mapDirectory))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(mapPreset.RuntimeGrid.SourcePath)
            ? mapPreset.RuntimeGrid.SourcePath
            : Path.GetFullPath(Path.Combine(mapDirectory, mapPreset.RuntimeGrid.SourcePath));
    }

    private static Vector3 ResolveCompositePivotModel(CompositeAnnotation composite)
        => composite.PivotModel?.ToVector3()
            ?? composite.PositionModel?.ToVector3()
            ?? Vector3.Zero;

    private static Vector3 ResolveCompositePositionModel(CompositeAnnotation composite, Vector3 pivotModel)
        => composite.PositionModel?.ToVector3()
            ?? pivotModel;

    private static Vector3 ResolveCompositeRotationYprDegrees(CompositeAnnotation composite)
        => composite.YprDegrees?.ToVector3()
            ?? Vector3.Zero;

    private static double NormalizeDeg(double angleDeg)
    {
        double normalized = angleDeg % 360.0;
        if (normalized < 0.0)
        {
            normalized += 360.0;
        }

        return normalized;
    }

    private readonly record struct MergedBounds(
        float CenterModelX,
        float CenterModelY,
        float CenterModelZ,
        float SizeModelX,
        float SizeModelY,
        float SizeModelZ);

    private sealed class ComponentAnnotationFile
    {
        public WorldScaleAnnotation? WorldScale { get; init; }

        public CompositeAnnotation[]? Composites { get; init; }

        public ComponentAnnotation[]? Components { get; init; }
    }

    private sealed class WorldScaleAnnotation
    {
        public float XMetersPerModelUnit { get; init; }

        public float YMetersPerModelUnit { get; init; }

        public float ZMetersPerModelUnit { get; init; }
    }

    private sealed class CompositeAnnotation
    {
        public string Name { get; init; } = string.Empty;

        public VectorAnnotation PositionMeters { get; init; } = new();

        public VectorAnnotation? PositionModel { get; init; }

        public VectorAnnotation? PivotModel { get; init; }

        public VectorAnnotation? YprDegrees { get; init; }

        public VectorAnnotation? CoordinateYprDegrees { get; init; }

        public InteractionUnitAnnotation[]? InteractionUnits { get; init; }
    }

    private sealed class InteractionUnitAnnotation
    {
        public string Name { get; init; } = string.Empty;

        public int[]? ComponentIds { get; init; }
    }

    private sealed class ComponentAnnotation
    {
        public int Id { get; init; }

        public BoundsAnnotation? Bounds { get; init; }
    }

    private sealed class BoundsAnnotation
    {
        public float[] Min { get; init; } = Array.Empty<float>();

        public float[] Max { get; init; } = Array.Empty<float>();
    }

    private sealed class VectorAnnotation
    {
        public float X { get; init; }

        public float Y { get; init; }

        public float Z { get; init; }

        public Vector3 ToVector3() => new(X, Y, Z);
    }
}
