using System.Numerics;
using System.Text.Json;
using Simulator.Core.Map;

namespace Simulator.Core.Gameplay;

internal sealed record AnnotatedOutpostDefinition
{
    public required IReadOnlyDictionary<string, AnnotatedOutpostTeamDefinition> Teams { get; init; }
}

internal sealed record AnnotatedOutpostTeamDefinition
{
    public required string Team { get; init; }

    public required double PivotWorldX { get; init; }

    public required double PivotWorldY { get; init; }

    public required double PivotHeightM { get; init; }

    public required IReadOnlyList<AnnotatedOutpostPlateState> RotatingPlates { get; init; }

    public ArmorPlateTarget? TopPlate { get; init; }
}

internal readonly record struct AnnotatedOutpostPlateState(
    string PlateId,
    double RadiusM,
    double HeightM,
    double BaseAngleRad,
    double SideLengthM,
    double WidthM,
    double HeightSpanM);

internal static class OutpostAnnotationLoader
{
    private const string OutpostKeyword = "\u524d\u54e8\u7ad9";
    private const string RotatingArmorKeyword = "\u65cb\u8f6c\u88c5\u7532\u677f";
    private const string TopArmorKeyword = "\u9876\u90e8\u4ea4\u4e92\u7ec4\u4ef6";
    private const string RedTeamKeyword = "\u7ea2\u65b9";
    private const string BlueTeamKeyword = "\u84dd\u65b9";
    private const string MiddleKeyword = "\u4e2d";
    private const string LowerKeyword = "\u4e0b";
    private const string UpperKeyword = "\u4e0a";
    private const string TopKeyword = "\u9876\u90e8";
    private const string LightStripKeyword = "\u706f\u6761";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Dictionary<string, AnnotatedOutpostDefinition> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();

    public static AnnotatedOutpostDefinition? TryLoad(
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
            if (Cache.TryGetValue(cacheKey, out AnnotatedOutpostDefinition? cached))
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
        var teams = new Dictionary<string, AnnotatedOutpostTeamDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (CompositeAnnotation composite in file.Composites)
        {
            if (string.IsNullOrWhiteSpace(composite.Name)
                || !composite.Name.Contains(OutpostKeyword, StringComparison.Ordinal)
                || !TryResolveTeam(composite.Name, out string team))
            {
                continue;
            }

            bool rotatingArmor = composite.Name.Contains(RotatingArmorKeyword, StringComparison.Ordinal);
            bool topArmor = composite.Name.Contains(TopArmorKeyword, StringComparison.Ordinal);
            if (!rotatingArmor && !topArmor)
            {
                continue;
            }

            if (!teams.TryGetValue(team, out AnnotatedOutpostTeamDefinition? existing))
            {
                Vector3 pivotModel = ResolveCompositePivotModel(composite);
                Vector3 positionModel = ResolveCompositePositionModel(composite, pivotModel);
                Vector3 rotationYprDegrees = ResolveCompositeRotationYprDegrees(composite);
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
                existing = new AnnotatedOutpostTeamDefinition
                {
                    Team = team,
                    PivotWorldX = pivotWorldX,
                    PivotWorldY = pivotWorldY,
                    PivotHeightM = pivotHeightM,
                    RotatingPlates = Array.Empty<AnnotatedOutpostPlateState>(),
                    TopPlate = null,
                };
            }

            if (rotatingArmor)
            {
                Vector3 pivotModel = ResolveCompositePivotModel(composite);
                Vector3 positionModel = ResolveCompositePositionModel(composite, pivotModel);
                Vector3 rotationYprDegrees = ResolveCompositeRotationYprDegrees(composite);
                var plates = new List<AnnotatedOutpostPlateState>(3);
                foreach (InteractionUnitAnnotation unit in composite.InteractionUnits ?? Array.Empty<InteractionUnitAnnotation>())
                {
                    if (string.IsNullOrWhiteSpace(unit.Name)
                        || unit.ComponentIds is null
                        || unit.Name.Contains(LightStripKeyword, StringComparison.Ordinal)
                        || !TryResolveRotatingPlateId(unit.Name, out string plateId)
                        || !TryResolveMergedBounds(unit.ComponentIds, componentsById, out MergedBounds bounds))
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
                    double dxM = (worldX - existing.PivotWorldX) * Math.Max(metersPerWorldUnit, 1e-6);
                    double dyM = (worldY - existing.PivotWorldY) * Math.Max(metersPerWorldUnit, 1e-6);
                    double widthM = Math.Max(
                        bounds.SizeModelX * xMetersPerModelUnit,
                        bounds.SizeModelZ * zMetersPerModelUnit);
                    double heightSpanM = Math.Max(0.02, bounds.SizeModelY * yMetersPerModelUnit);
                    plates.Add(new AnnotatedOutpostPlateState(
                        plateId,
                        Math.Sqrt(dxM * dxM + dyM * dyM),
                        heightM,
                        Math.Atan2(dyM, dxM),
                        Math.Max(widthM, heightSpanM),
                        widthM,
                        heightSpanM));
                }

                existing = existing with
                {
                    RotatingPlates = plates
                        .OrderBy(plate => plate.PlateId, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                };
            }

            if (topArmor)
            {
                Vector3 pivotModel = ResolveCompositePivotModel(composite);
                Vector3 positionModel = ResolveCompositePositionModel(composite, pivotModel);
                Vector3 rotationYprDegrees = ResolveCompositeRotationYprDegrees(composite);
                foreach (InteractionUnitAnnotation unit in composite.InteractionUnits ?? Array.Empty<InteractionUnitAnnotation>())
                {
                    if (string.IsNullOrWhiteSpace(unit.Name)
                        || unit.ComponentIds is null
                        || unit.Name.Contains(LightStripKeyword, StringComparison.Ordinal)
                        || !unit.Name.Contains(TopKeyword, StringComparison.Ordinal)
                        || !TryResolveMergedBounds(unit.ComponentIds, componentsById, out MergedBounds bounds))
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
                    existing = existing with
                    {
                        TopPlate = new ArmorPlateTarget(
                            "outpost_top",
                            worldX,
                            worldY,
                            heightM,
                            0.0,
                            Math.Max(
                                bounds.SizeModelX * xMetersPerModelUnit,
                                bounds.SizeModelZ * zMetersPerModelUnit))
                    };
                    break;
                }
            }

            teams[team] = existing;
        }

        if (teams.Count == 0 || teams.Values.All(team => team.RotatingPlates.Count == 0 && team.TopPlate is null))
        {
            return null;
        }

        var definition = new AnnotatedOutpostDefinition
        {
            Teams = teams,
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

    private static bool TryResolveRotatingPlateId(string unitName, out string plateId)
    {
        if (unitName.Contains(MiddleKeyword, StringComparison.Ordinal))
        {
            plateId = "outpost_ring_1";
            return true;
        }

        if (unitName.Contains(LowerKeyword, StringComparison.Ordinal))
        {
            plateId = "outpost_ring_2";
            return true;
        }

        if (unitName.Contains(UpperKeyword, StringComparison.Ordinal))
        {
            plateId = "outpost_ring_3";
            return true;
        }

        plateId = string.Empty;
        return false;
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
            MathF.Max(0.0001f, maxX - minX),
            MathF.Max(0.0001f, maxY - minY),
            MathF.Max(0.0001f, maxZ - minZ));
        return true;
    }

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

        public ComponentAnnotation[]? Components { get; init; }

        public CompositeAnnotation[]? Composites { get; init; }
    }

    private sealed class WorldScaleAnnotation
    {
        public float XMetersPerModelUnit { get; init; }

        public float YMetersPerModelUnit { get; init; }

        public float ZMetersPerModelUnit { get; init; }
    }

    private sealed class ComponentAnnotation
    {
        public int Id { get; init; }

        public ComponentBoundsAnnotation? Bounds { get; init; }
    }

    private sealed class ComponentBoundsAnnotation
    {
        public float[]? Min { get; init; }

        public float[]? Max { get; init; }
    }

    private sealed class CompositeAnnotation
    {
        public string Name { get; init; } = string.Empty;

        public VectorAnnotation? PositionModel { get; init; }

        public VectorAnnotation? PivotModel { get; init; }

        public VectorAnnotation? YprDegrees { get; init; }

        public InteractionUnitAnnotation[]? InteractionUnits { get; init; }
    }

    private sealed class VectorAnnotation
    {
        public float X { get; init; }

        public float Y { get; init; }

        public float Z { get; init; }

        public Vector3 ToVector3() => new(X, Y, Z);
    }

    private sealed class InteractionUnitAnnotation
    {
        public string Name { get; init; } = string.Empty;

        public int[]? ComponentIds { get; init; }
    }
}
