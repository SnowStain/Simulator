using System.Numerics;
using LoadLargeTerrain;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal static class FineTerrainEnergyMechanismVisualCache
{
    private const string EnergyMechanismKeyword = "\u80fd\u91cf\u673a\u5173";
    private const string RedTeamKeyword = "\u7ea2\u65b9";
    private const string BlueTeamKeyword = "\u84dd\u65b9";
    private const string LightArmKeyword = "\u706f\u81c2";
    private const string GlowArmKeyword = "\u5149\u81c2";
    private const string LightStripKeyword = "\u706f\u6761";
    private const string CenterKeyword = "\u4e2d\u592e";
    private const string MarkKeyword = "\u6807";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, FineTerrainEnergyMechanismVisualScene> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedFailures = new(StringComparer.OrdinalIgnoreCase);

    public static FineTerrainEnergyMechanismVisualScene? TryLoad(MapPresetDefinition preset)
    {
        string annotationPath = preset.AnnotationPath;
        if (string.IsNullOrWhiteSpace(annotationPath) || !File.Exists(annotationPath))
        {
            LogFailureOnce(
                $"{preset.SourcePath}|annotation_missing",
                $"annotation missing for preset={Path.GetFileName(preset.SourcePath)} path={annotationPath}");
            return null;
        }

        string terrainCachePath = ResolveTerrainCachePath(preset);
        if (string.IsNullOrWhiteSpace(terrainCachePath) || !File.Exists(terrainCachePath))
        {
            LogFailureOnce(
                $"{Path.GetFullPath(annotationPath)}|terrain_missing",
                $"terrain cache missing for annotation={Path.GetFileName(annotationPath)} path={terrainCachePath}");
            return null;
        }

        string cacheKey = $"{Path.GetFullPath(annotationPath)}|{Path.GetFullPath(terrainCachePath)}|{File.GetLastWriteTimeUtc(annotationPath).Ticks}|{File.GetLastWriteTimeUtc(terrainCachePath).Ticks}";
        lock (Gate)
        {
            if (Cache.TryGetValue(cacheKey, out FineTerrainEnergyMechanismVisualScene? cached))
            {
                return cached;
            }
        }

        RuntimeReferenceScene runtimeScene;
        try
        {
            runtimeScene = RuntimeReferenceLoader.Load(terrainCachePath, annotationPath);
        }
        catch (Exception exception)
        {
            LogFailureOnce(
                $"{Path.GetFullPath(annotationPath)}|runtime_reference_failed",
                $"runtime reference load failed annotation={Path.GetFileName(annotationPath)} error={exception.Message}");
            return null;
        }

        List<FineTerrainEnergyMechanismVisualItem> items = BuildEnergyMechanismItems(runtimeScene);
        if (items.Count == 0)
        {
            LogFailureOnce(
                $"{Path.GetFullPath(annotationPath)}|no_energy_items",
                $"no annotated energy composites found path={annotationPath}");
            return null;
        }

        HashSet<int> allComponentIds = new();
        foreach (FineTerrainEnergyMechanismVisualItem item in items)
        {
            foreach (int componentId in item.ComponentIds)
            {
                allComponentIds.Add(componentId);
            }

            foreach (FineTerrainEnergyMechanismUnitVisualItem unit in item.Units)
            {
                foreach (int componentId in unit.ComponentIds)
                {
                    allComponentIds.Add(componentId);
                }
            }
        }

        Dictionary<int, RuntimeReferenceBounds> boundsByComponent = runtimeScene.Components
            .ToDictionary(component => component.Id, component => component.Bounds);
        Dictionary<int, List<FineTerrainColoredTriangle>> trianglesByComponent = LoadTrianglesByComponent(runtimeScene, allComponentIds);
        foreach (FineTerrainEnergyMechanismVisualItem item in items)
        {
            HashSet<int> unitComponentIds = new();
            foreach (FineTerrainEnergyMechanismUnitVisualItem unit in item.Units)
            {
                foreach (int componentId in unit.ComponentIds)
                {
                    unitComponentIds.Add(componentId);
                }
            }

            var bodyTriangles = new List<FineTerrainColoredTriangle>(4096);
            foreach (int componentId in item.ComponentIds)
            {
                if (unitComponentIds.Contains(componentId))
                {
                    continue;
                }

                if (trianglesByComponent.TryGetValue(componentId, out List<FineTerrainColoredTriangle>? componentTriangles))
                {
                    bodyTriangles.AddRange(componentTriangles);
                }
            }

            item.Triangles = bodyTriangles;
            foreach (FineTerrainEnergyMechanismUnitVisualItem unit in item.Units)
            {
                var unitTriangles = new List<FineTerrainColoredTriangle>(128);
                foreach (int componentId in unit.ComponentIds)
                {
                    if (trianglesByComponent.TryGetValue(componentId, out List<FineTerrainColoredTriangle>? componentTriangles))
                    {
                        unitTriangles.AddRange(componentTriangles);
                    }
                }

                unit.Triangles = unitTriangles;
                ResolveUnitGeometry(
                    item.PivotModel,
                    ResolveRotorAxis(item.CoordinateYprDegrees),
                    unit.ComponentIds,
                    boundsByComponent,
                    runtimeScene.WorldScale.XMetersPerUnit,
                    runtimeScene.WorldScale.YMetersPerUnit,
                    runtimeScene.WorldScale.ZMetersPerUnit,
                    out Vector3 localCenterModel,
                    out Vector3 localNormalModel,
                    out double sideLengthM,
                    out double widthM,
                    out double heightSpanM);
                unit.LocalCenterModel = localCenterModel;
                unit.LocalNormalModel = localNormalModel;
                unit.SideLengthM = sideLengthM;
                unit.WidthM = widthM;
                unit.HeightSpanM = heightSpanM;
            }
        }

        var scene = new FineTerrainEnergyMechanismVisualScene(ToFineTerrainWorldScale(runtimeScene, preset), items);
        lock (Gate)
        {
            Cache[cacheKey] = scene;
        }

        LogLoadSuccess(annotationPath, terrainCachePath, scene);
        return scene;
    }

    private static List<FineTerrainEnergyMechanismVisualItem> BuildEnergyMechanismItems(RuntimeReferenceScene runtimeScene)
    {
        var items = new List<FineTerrainEnergyMechanismVisualItem>(2);
        foreach (RuntimeReferenceComposite composite in runtimeScene.Composites)
        {
            if (!composite.Name.Contains(EnergyMechanismKeyword, StringComparison.Ordinal))
            {
                continue;
            }

            string team = ResolveEnergyMechanismTeam(composite.Name);
            if (string.IsNullOrWhiteSpace(team))
            {
                continue;
            }

            HashSet<int> centerMarkComponentIds = composite.InteractionUnits
                .Where(unit => IsCenterMarkUnitName(unit.Name))
                .SelectMany(unit => unit.ComponentIds)
                .ToHashSet();
            HashSet<int> compositeComponents = composite.ComponentIds.Length == 0
                ? new HashSet<int>()
                : composite.ComponentIds
                    .Where(componentId => !centerMarkComponentIds.Contains(componentId))
                    .ToHashSet();
            var units = new List<FineTerrainEnergyMechanismUnitVisualItem>(composite.InteractionUnits.Length);
            foreach (RuntimeReferenceInteractionUnit unit in composite.InteractionUnits)
            {
                if (IsCenterMarkUnitName(unit.Name))
                {
                    continue;
                }

                if (!TryParseEnergyUnit(unit.Name, out FineTerrainEnergyUnitKind kind, out int armIndex, out int ringScore))
                {
                    continue;
                }

                int[] filteredComponentIds = unit.ComponentIds
                    .Where(componentId => !centerMarkComponentIds.Contains(componentId))
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                if (filteredComponentIds.Length == 0)
                {
                    continue;
                }

                units.Add(new FineTerrainEnergyMechanismUnitVisualItem(
                    unit.Name,
                    kind,
                    armIndex,
                    ringScore,
                    filteredComponentIds,
                    new List<FineTerrainColoredTriangle>()));

                foreach (int componentId in filteredComponentIds)
                {
                    compositeComponents.Add(componentId);
                }
            }

            compositeComponents.ExceptWith(centerMarkComponentIds);
            if (compositeComponents.Count == 0)
            {
                continue;
            }

            items.Add(new FineTerrainEnergyMechanismVisualItem(
                composite.Id,
                team,
                composite.Name,
                composite.PivotModel,
                composite.PositionModel,
                composite.RotationYprDegrees,
                composite.CoordinateYprDegrees,
                compositeComponents.OrderBy(id => id).ToArray(),
                new List<FineTerrainColoredTriangle>(),
                units));
        }

        return items;
    }

    private static bool TryParseEnergyUnit(
        string name,
        out FineTerrainEnergyUnitKind kind,
        out int armIndex,
        out int ringScore)
    {
        kind = FineTerrainEnergyUnitKind.Ring;
        armIndex = -1;
        ringScore = 0;
        string[] parts = (name ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3
            && int.TryParse(parts[1], out int parsedArm)
            && parsedArm >= 1
            && parsedArm <= 5)
        {
            armIndex = parsedArm - 1;
            if (int.TryParse(parts[2], out int parsedRing)
                && parsedRing >= 1
                && parsedRing <= 10)
            {
                kind = FineTerrainEnergyUnitKind.Ring;
                ringScore = parsedRing;
                return true;
            }

            string role = parts[2];
            if (role.Contains(LightArmKeyword, StringComparison.Ordinal)
                || role.Contains(GlowArmKeyword, StringComparison.Ordinal)
                || role.Contains(LightStripKeyword, StringComparison.Ordinal))
            {
                kind = FineTerrainEnergyUnitKind.LightArm;
                return true;
            }
        }

        if ((name ?? string.Empty).Contains(CenterKeyword, StringComparison.Ordinal)
            && ((name ?? string.Empty).Contains("R", StringComparison.OrdinalIgnoreCase)
                || (name ?? string.Empty).Contains(MarkKeyword, StringComparison.Ordinal)))
        {
            return false;
        }

        return false;
    }

    private static bool IsCenterMarkUnitName(string name)
    {
        return (name ?? string.Empty).Contains(CenterKeyword, StringComparison.Ordinal)
            && ((name ?? string.Empty).Contains("R", StringComparison.OrdinalIgnoreCase)
                || (name ?? string.Empty).Contains(MarkKeyword, StringComparison.Ordinal));
    }

    private static Dictionary<int, List<FineTerrainColoredTriangle>> LoadTrianglesByComponent(
        RuntimeReferenceScene runtimeScene,
        IReadOnlySet<int> componentIds)
    {
        var result = new Dictionary<int, List<FineTerrainColoredTriangle>>(componentIds.Count);
        foreach (RuntimeReferenceChunk chunk in runtimeScene.Chunks)
        {
            RuntimeReferenceVertex[] vertices = chunk.Vertices;
            uint[] indices = chunk.Indices;
            RuntimeReferenceComponentRange[] componentRanges = chunk.ComponentRanges;
            if (componentRanges.Length == 0 || vertices.Length == 0 || indices.Length < 3)
            {
                continue;
            }

            foreach (RuntimeReferenceComponentRange range in componentRanges)
            {
                if (!componentIds.Contains(range.ComponentId))
                {
                    continue;
                }

                if (!result.TryGetValue(range.ComponentId, out List<FineTerrainColoredTriangle>? triangles))
                {
                    triangles = new List<FineTerrainColoredTriangle>(64);
                    result.Add(range.ComponentId, triangles);
                }

                int start = Math.Clamp(range.StartIndex, 0, indices.Length);
                int end = Math.Clamp(range.StartIndex + Math.Max(0, range.IndexCount), start, indices.Length);
                end -= (end - start) % 3;
                for (int triangleIndex = start; triangleIndex < end; triangleIndex += 3)
                {
                    int i0 = checked((int)indices[triangleIndex]);
                    int i1 = checked((int)indices[triangleIndex + 1]);
                    int i2 = checked((int)indices[triangleIndex + 2]);
                    if ((uint)i0 >= (uint)vertices.Length
                        || (uint)i1 >= (uint)vertices.Length
                        || (uint)i2 >= (uint)vertices.Length)
                    {
                        continue;
                    }

                    RuntimeReferenceVertex v0 = vertices[i0];
                    RuntimeReferenceVertex v1 = vertices[i1];
                    RuntimeReferenceVertex v2 = vertices[i2];
                    triangles.Add(new FineTerrainColoredTriangle(
                        v0.Position,
                        v1.Position,
                        v2.Position,
                        ResolveTriangleColor(v0.Color, v1.Color, v2.Color)));
                }
            }
        }

        return result;
    }

    private static void ResolveUnitGeometry(
        Vector3 pivotModel,
        Vector3 rotorAxisModel,
        IReadOnlyList<int> componentIds,
        IReadOnlyDictionary<int, RuntimeReferenceBounds> boundsByComponent,
        float xMetersPerUnit,
        float yMetersPerUnit,
        float zMetersPerUnit,
        out Vector3 localCenterModel,
        out Vector3 localNormalModel,
        out double sideLengthM,
        out double widthM,
        out double heightSpanM)
    {
        localCenterModel = Vector3.Zero;
        localNormalModel = Vector3.UnitX;
        sideLengthM = 0.12;
        widthM = 0.12;
        heightSpanM = 0.12;
        if (componentIds.Count == 0)
        {
            return;
        }

        bool initialized = false;
        Vector3 min = Vector3.Zero;
        Vector3 max = Vector3.Zero;
        foreach (int componentId in componentIds)
        {
            if (!boundsByComponent.TryGetValue(componentId, out RuntimeReferenceBounds bounds))
            {
                continue;
            }

            if (!initialized)
            {
                min = bounds.Min;
                max = bounds.Max;
                initialized = true;
            }
            else
            {
                min = Vector3.Min(min, bounds.Min);
                max = Vector3.Max(max, bounds.Max);
            }
        }

        if (!initialized)
        {
            return;
        }

        localCenterModel = (min + max) * 0.5f;
        Vector3 safeAxis = rotorAxisModel.LengthSquared() <= 1e-8f
            ? Vector3.UnitX
            : Vector3.Normalize(rotorAxisModel);
        Vector3 radial = localCenterModel - pivotModel;
        radial -= safeAxis * Vector3.Dot(radial, safeAxis);
        localNormalModel = radial.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(radial);
        widthM = Math.Max(
            0.02,
            Math.Max(
                (max.X - min.X) * xMetersPerUnit,
                (max.Z - min.Z) * zMetersPerUnit));
        heightSpanM = Math.Max(0.02, (max.Y - min.Y) * yMetersPerUnit);
        sideLengthM = Math.Max(widthM, heightSpanM);
    }

    private static Vector3 ResolveRotorAxis(Vector3 coordinateYprDegrees)
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

    private static FineTerrainWorldScale ToFineTerrainWorldScale(RuntimeReferenceScene runtimeScene, MapPresetDefinition preset)
        => new(
            (float)Math.Max(1.0, preset.FieldLengthM),
            (float)Math.Max(1.0, preset.FieldWidthM),
            runtimeScene.WorldScale.XMetersPerUnit,
            runtimeScene.WorldScale.YMetersPerUnit,
            runtimeScene.WorldScale.ZMetersPerUnit,
            runtimeScene.WorldScale.ModelCenter,
            runtimeScene.Bounds.Min.Y);

    private static string ResolveTerrainCachePath(MapPresetDefinition preset)
    {
        if (preset.RuntimeGrid is null || string.IsNullOrWhiteSpace(preset.RuntimeGrid.SourcePath))
        {
            return string.Empty;
        }

        string? mapDirectory = Path.GetDirectoryName(preset.SourcePath);
        if (string.IsNullOrWhiteSpace(mapDirectory))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(preset.RuntimeGrid.SourcePath)
            ? preset.RuntimeGrid.SourcePath
            : Path.GetFullPath(Path.Combine(mapDirectory, preset.RuntimeGrid.SourcePath));
    }

    private static string ResolveEnergyMechanismTeam(string compositeName)
    {
        if (compositeName.Contains(RedTeamKeyword, StringComparison.Ordinal))
        {
            return "red";
        }

        if (compositeName.Contains(BlueTeamKeyword, StringComparison.Ordinal))
        {
            return "blue";
        }

        return string.Empty;
    }

    private static Color ResolveTriangleColor(uint color0, uint color1, uint color2)
    {
        static Color Unpack(uint packed)
            => Color.FromArgb(
                (int)((packed >> 24) & 0xFF),
                (int)(packed & 0xFF),
                (int)((packed >> 8) & 0xFF),
                (int)((packed >> 16) & 0xFF));

        Color a = Unpack(color0);
        Color b = Unpack(color1);
        Color c = Unpack(color2);
        return Color.FromArgb(
            (a.A + b.A + c.A) / 3,
            (a.R + b.R + c.R) / 3,
            (a.G + b.G + c.G) / 3,
            (a.B + b.B + c.B) / 3);
    }

    private static void LogLoadSuccess(
        string annotationPath,
        string terrainCachePath,
        FineTerrainEnergyMechanismVisualScene scene)
    {
        int triangleCount = 0;
        foreach (FineTerrainEnergyMechanismVisualItem item in scene.Items)
        {
            triangleCount += item.Triangles.Count;
        }

        LogMessage(
            $"load_ok annotation={Path.GetFileName(annotationPath)} terrain={Path.GetFileName(terrainCachePath)} items={scene.Items.Count} triangles={triangleCount}");
        foreach (FineTerrainEnergyMechanismVisualItem item in scene.Items)
        {
            LogMessage(
                $"item team={item.Team} name={item.Name} components={item.ComponentIds.Length} triangles={item.Triangles.Count} pivot=({item.PivotModel.X:0.###},{item.PivotModel.Y:0.###},{item.PivotModel.Z:0.###}) coordYpr=({item.CoordinateYprDegrees.X:0.###},{item.CoordinateYprDegrees.Y:0.###},{item.CoordinateYprDegrees.Z:0.###})");
            foreach (FineTerrainEnergyMechanismUnitVisualItem unit in item.Units.OrderBy(candidate => candidate.ArmIndex).ThenBy(candidate => candidate.RingScore))
            {
                LogMessage(
                    $"unit team={item.Team} kind={unit.Kind} name={unit.Name} arm={unit.ArmIndex} ring={unit.RingScore} components={unit.ComponentIds.Length} center=({unit.LocalCenterModel.X:0.###},{unit.LocalCenterModel.Y:0.###},{unit.LocalCenterModel.Z:0.###}) normal=({unit.LocalNormalModel.X:0.###},{unit.LocalNormalModel.Y:0.###},{unit.LocalNormalModel.Z:0.###}) size=({unit.WidthM:0.###},{unit.HeightSpanM:0.###})");
            }
        }
    }

    private static void LogFailureOnce(string key, string message)
    {
        lock (Gate)
        {
            if (!LoggedFailures.Add(key))
            {
                return;
            }
        }

        LogMessage($"load_fail {message}");
    }

    private static void LogMessage(string message)
    {
        try
        {
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            File.AppendAllText(Path.Combine(logDirectory, "fine_terrain_energy.log"), line + Environment.NewLine);
        }
        catch
        {
        }
    }
}

internal sealed class FineTerrainEnergyMechanismVisualScene
{
    public FineTerrainEnergyMechanismVisualScene(
        FineTerrainWorldScale worldScale,
        IReadOnlyList<FineTerrainEnergyMechanismVisualItem> items)
    {
        WorldScale = worldScale;
        Items = items;
    }

    public FineTerrainWorldScale WorldScale { get; }

    public IReadOnlyList<FineTerrainEnergyMechanismVisualItem> Items { get; }
}

internal sealed class FineTerrainEnergyMechanismVisualItem
{
    public FineTerrainEnergyMechanismVisualItem(
        int compositeId,
        string team,
        string name,
        Vector3 pivotModel,
        Vector3 positionModel,
        Vector3 rotationYprDegrees,
        Vector3 coordinateYprDegrees,
        int[] componentIds,
        List<FineTerrainColoredTriangle> triangles,
        IReadOnlyList<FineTerrainEnergyMechanismUnitVisualItem> units)
    {
        CompositeId = compositeId;
        Team = team;
        Name = name;
        PivotModel = pivotModel;
        PositionModel = positionModel;
        RotationYprDegrees = rotationYprDegrees;
        CoordinateYprDegrees = coordinateYprDegrees;
        ComponentIds = componentIds;
        Triangles = triangles;
        Units = units;
    }

    public int CompositeId { get; }

    public string Team { get; }

    public string Name { get; }

    public Vector3 PivotModel { get; }

    public Vector3 PositionModel { get; }

    public Vector3 RotationYprDegrees { get; }

    public Vector3 CoordinateYprDegrees { get; }

    public int[] ComponentIds { get; }

    public List<FineTerrainColoredTriangle> Triangles { get; set; }

    public IReadOnlyList<FineTerrainEnergyMechanismUnitVisualItem> Units { get; }
}

internal sealed class FineTerrainEnergyMechanismUnitVisualItem
{
    public FineTerrainEnergyMechanismUnitVisualItem(
        string name,
        FineTerrainEnergyUnitKind kind,
        int armIndex,
        int ringScore,
        int[] componentIds,
        List<FineTerrainColoredTriangle> triangles)
    {
        Name = name;
        Kind = kind;
        ArmIndex = armIndex;
        RingScore = ringScore;
        ComponentIds = componentIds;
        Triangles = triangles;
    }

    public string Name { get; }

    public FineTerrainEnergyUnitKind Kind { get; }

    public int ArmIndex { get; }

    public int RingScore { get; }

    public int[] ComponentIds { get; }

    public List<FineTerrainColoredTriangle> Triangles { get; set; }

    public Vector3 LocalCenterModel { get; set; }

    public Vector3 LocalNormalModel { get; set; } = Vector3.UnitX;

    public double SideLengthM { get; set; } = 0.12;

    public double WidthM { get; set; } = 0.12;

    public double HeightSpanM { get; set; } = 0.12;
}

internal enum FineTerrainEnergyUnitKind
{
    Ring,
    LightArm,
    CenterMark,
}

internal readonly record struct FineTerrainColoredTriangle(
    Vector3 A,
    Vector3 B,
    Vector3 C,
    Color Color);
