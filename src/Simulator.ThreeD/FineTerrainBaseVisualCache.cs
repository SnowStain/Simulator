using System.Numerics;
using LoadLargeTerrain;
using Simulator.Core;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal static class FineTerrainBaseVisualCache
{
    private const string BaseKeyword = "\u57fa\u5730";
    private const string TopArmorKeyword = "\u9876\u90e8\u4ea4\u4e92\u7ec4\u4ef6";
    private const string ArmorPlateKeyword = "\u88c5\u7532\u677f";
    private const string LightStripKeyword = "\u706f\u6761";
    private const string RedTeamKeyword = "\u7ea2\u65b9";
    private const string BlueTeamKeyword = "\u84dd\u65b9";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, FineTerrainBaseVisualScene> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedFailures = new(StringComparer.OrdinalIgnoreCase);

    public static FineTerrainBaseVisualScene? TryLoad(MapPresetDefinition preset)
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

        string cacheKey = $"{Path.GetFullPath(annotationPath)}|{Path.GetFullPath(terrainCachePath)}|{File.GetLastWriteTimeUtc(annotationPath).Ticks}|{File.GetLastWriteTimeUtc(terrainCachePath).Ticks}|{preset.FieldLengthM:R}|{preset.FieldWidthM:R}";
        lock (Gate)
        {
            if (Cache.TryGetValue(cacheKey, out FineTerrainBaseVisualScene? cached))
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

        List<FineTerrainBaseVisualItem> items = BuildBaseItems(runtimeScene);
        if (items.Count == 0)
        {
            LogFailureOnce(
                $"{Path.GetFullPath(annotationPath)}|no_base_items",
                $"no annotated base composites found path={annotationPath}");
            return null;
        }

        HashSet<int> allComponentIds = new();
        foreach (FineTerrainBaseVisualItem item in items)
        {
            foreach (int componentId in item.ComponentIds)
            {
                allComponentIds.Add(componentId);
            }

            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
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
        bool hasLightStripHalfSplit = TryResolveLightStripHalfSplit(items, out float lightStripHalfSplitModelX, out bool blueSideLowerModelX);
        foreach (FineTerrainBaseVisualItem item in items)
        {
            HashSet<int> unitComponentIds = new();
            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                foreach (int componentId in unit.ComponentIds)
                {
                    unitComponentIds.Add(componentId);
                }
            }

            var bodyCandidates = new List<BaseBodyCandidate>(512);
            foreach (int componentId in item.ComponentIds)
            {
                if (unitComponentIds.Contains(componentId))
                {
                    continue;
                }

                if (!boundsByComponent.TryGetValue(componentId, out RuntimeReferenceBounds bounds))
                {
                    continue;
                }

                if (trianglesByComponent.TryGetValue(componentId, out List<FineTerrainColoredTriangle>? componentTriangles))
                {
                    bodyCandidates.Add(new BaseBodyCandidate(componentId, bounds, componentTriangles));
                }
            }

            var bodyTriangles = new List<FineTerrainColoredTriangle>(2048);
            foreach (BaseBodyCandidate candidate in bodyCandidates)
            {
                bodyTriangles.AddRange(candidate.Triangles);
            }
            string itemDisplayTeam = hasLightStripHalfSplit
                ? ResolveLightStripDisplayTeam(item.PositionModel.X, lightStripHalfSplitModelX, blueSideLowerModelX)
                : item.Team;
            item.Triangles = NormalizeBaseTriangles(bodyTriangles, itemDisplayTeam, isLightStrip: false);
            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                var triangles = new List<FineTerrainColoredTriangle>(128);
                foreach (int componentId in unit.ComponentIds)
                {
                    if (trianglesByComponent.TryGetValue(componentId, out List<FineTerrainColoredTriangle>? componentTriangles))
                    {
                        triangles.AddRange(componentTriangles);
                    }
                }

                string unitDisplayTeam = hasLightStripHalfSplit
                    ? ResolveLightStripDisplayTeam(item.PositionModel.X, lightStripHalfSplitModelX, blueSideLowerModelX)
                    : item.Team;
                unit.Triangles = NormalizeBaseTriangles(triangles, unitDisplayTeam, unit.IsLightStrip);
                ResolveUnitGeometry(
                    item.PivotModel,
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
                unit.LocalCentroidModel = localCenterModel;
                unit.LocalNormalModel = localNormalModel;
                unit.SideLengthM = sideLengthM;
                unit.WidthM = widthM;
                unit.HeightSpanM = heightSpanM;
            }
        }

        var scene = new FineTerrainBaseVisualScene(ToFineTerrainWorldScale(runtimeScene, preset), items);
        lock (Gate)
        {
            Cache[cacheKey] = scene;
        }

        LogLoadSuccess(annotationPath, terrainCachePath, scene);
        return scene;
    }

    private static List<FineTerrainBaseVisualItem> BuildBaseItems(RuntimeReferenceScene runtimeScene)
    {
        var items = new List<FineTerrainBaseVisualItem>(2);
        foreach (RuntimeReferenceComposite composite in runtimeScene.Composites)
        {
            if (!composite.Name.Contains(BaseKeyword, StringComparison.Ordinal)
                || !composite.Name.Contains(TopArmorKeyword, StringComparison.Ordinal))
            {
                continue;
            }

            string team = ResolveTeam(composite.Name);
            if (string.IsNullOrWhiteSpace(team))
            {
                continue;
            }

            HashSet<int> compositeComponents = composite.ComponentIds.Length == 0
                ? new HashSet<int>()
                : composite.ComponentIds.ToHashSet();
            var units = new List<FineTerrainBaseUnitVisualItem>(composite.InteractionUnits.Length);
            foreach (RuntimeReferenceInteractionUnit unit in composite.InteractionUnits)
            {
                string? plateId = ResolvePlateId(unit.Name);
                if (string.IsNullOrWhiteSpace(plateId) || unit.ComponentIds.Length == 0)
                {
                    continue;
                }

                int[] componentIds = unit.ComponentIds.OrderBy(id => id).ToArray();
                units.Add(new FineTerrainBaseUnitVisualItem(
                    unit.Name,
                    plateId,
                    IsLightStrip(unit.Name),
                    componentIds,
                    Vector3.Zero,
                    new List<FineTerrainColoredTriangle>()));
                foreach (int componentId in componentIds)
                {
                    compositeComponents.Add(componentId);
                }
            }

            if (units.Count == 0)
            {
                continue;
            }

            items.Add(new FineTerrainBaseVisualItem(
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

    private static bool TryResolveLightStripHalfSplit(
        IReadOnlyList<FineTerrainBaseVisualItem> items,
        out float splitModelX,
        out bool blueSideLowerModelX)
    {
        splitModelX = 0f;
        blueSideLowerModelX = false;
        float blueSum = 0f;
        int blueCount = 0;
        float redSum = 0f;
        int redCount = 0;
        foreach (FineTerrainBaseVisualItem item in items)
        {
            if (string.Equals(item.Team, "blue", StringComparison.OrdinalIgnoreCase))
            {
                blueSum += item.PositionModel.X;
                blueCount++;
            }
            else if (string.Equals(item.Team, "red", StringComparison.OrdinalIgnoreCase))
            {
                redSum += item.PositionModel.X;
                redCount++;
            }
        }

        if (blueCount == 0 || redCount == 0)
        {
            return false;
        }

        float blueAvg = blueSum / blueCount;
        float redAvg = redSum / redCount;
        splitModelX = (blueAvg + redAvg) * 0.5f;
        blueSideLowerModelX = blueAvg < redAvg;
        return true;
    }

    private static string ResolveLightStripDisplayTeam(
        float positionModelX,
        float splitModelX,
        bool blueSideLowerModelX)
    {
        bool onBlueSide = blueSideLowerModelX
            ? positionModelX <= splitModelX
            : positionModelX >= splitModelX;
        return onBlueSide ? "blue" : "red";
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

    private static List<FineTerrainColoredTriangle> SelectBaseBodyTriangles(
        Vector3 pivotModel,
        RuntimeReferenceBounds unitBounds,
        IReadOnlyList<BaseBodyCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return new List<FineTerrainColoredTriangle>();
        }

        Vector3 exclusionPadding = new(0.18f, 0.12f, 0.18f);
        RuntimeReferenceBounds exclusionBounds = ExpandBounds(unitBounds, exclusionPadding);

        static float ScoreCandidate(Vector3 pivot, BaseBodyCandidate candidate)
        {
            Vector3 center = candidate.Bounds.Center;
            Vector2 horizontal = new(center.X - pivot.X, center.Z - pivot.Z);
            float horizontalDistance = horizontal.Length();
            float verticalDistance = MathF.Abs(center.Y - pivot.Y);
            Vector3 size = candidate.Bounds.Size;
            float maxExtent = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            return horizontalDistance + verticalDistance * 0.85f + maxExtent * 0.30f;
        }

        static bool IsPrimaryCandidate(Vector3 pivot, BaseBodyCandidate candidate)
        {
            Vector3 center = candidate.Bounds.Center;
            Vector2 horizontal = new(center.X - pivot.X, center.Z - pivot.Z);
            float horizontalDistance = horizontal.Length();
            float verticalDistance = MathF.Abs(center.Y - pivot.Y);
            Vector3 size = candidate.Bounds.Size;
            float maxExtent = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            return horizontalDistance <= 0.95f
                && verticalDistance <= 0.55f
                && maxExtent <= 0.95f;
        }

        List<BaseBodyCandidate> orderedPrimary = candidates
            .Where(candidate => !BoundsIntersect(exclusionBounds, candidate.Bounds))
            .Where(candidate => IsPrimaryCandidate(pivotModel, candidate))
            .OrderBy(candidate => ScoreCandidate(pivotModel, candidate))
            .ToList();
        List<BaseBodyCandidate> orderedFallback = candidates
            .Where(candidate => !BoundsIntersect(exclusionBounds, candidate.Bounds))
            .OrderBy(candidate => ScoreCandidate(pivotModel, candidate))
            .ToList();
        if (orderedPrimary.Count == 0 && orderedFallback.Count == 0)
        {
            orderedPrimary = candidates
                .Where(candidate => IsPrimaryCandidate(pivotModel, candidate))
                .OrderBy(candidate => ScoreCandidate(pivotModel, candidate))
                .ToList();
            orderedFallback = candidates
                .OrderBy(candidate => ScoreCandidate(pivotModel, candidate))
                .ToList();
        }

        const int softTriangleBudget = 18000;
        const int hardTriangleBudget = 26000;

        static List<FineTerrainColoredTriangle> MaterializeCandidates(
            IReadOnlyList<BaseBodyCandidate> source,
            int softBudget,
            int hardBudget)
        {
            var result = new List<FineTerrainColoredTriangle>(Math.Min(hardBudget, 4096));
            int triangleCount = 0;
            foreach (BaseBodyCandidate candidate in source)
            {
                int nextCount = triangleCount + candidate.Triangles.Count;
                if (triangleCount >= softBudget && nextCount > hardBudget)
                {
                    break;
                }

                if (triangleCount > 0 && nextCount > hardBudget && triangleCount >= softBudget / 2)
                {
                    continue;
                }

                result.AddRange(candidate.Triangles);
                triangleCount = nextCount;
                if (triangleCount >= softBudget)
                {
                    break;
                }
            }

            return result;
        }

        List<FineTerrainColoredTriangle> primarySelection = MaterializeCandidates(orderedPrimary, softTriangleBudget, hardTriangleBudget);
        if (primarySelection.Count > 0)
        {
            return primarySelection;
        }

        return MaterializeCandidates(orderedFallback, softTriangleBudget, hardTriangleBudget);
    }

    private static RuntimeReferenceBounds ResolveAggregateBounds(
        IEnumerable<int> componentIds,
        IReadOnlyDictionary<int, RuntimeReferenceBounds> boundsByComponent)
    {
        bool hasBounds = false;
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (int componentId in componentIds)
        {
            if (!boundsByComponent.TryGetValue(componentId, out RuntimeReferenceBounds bounds))
            {
                continue;
            }

            Vector3 boundsMin = bounds.Min;
            Vector3 boundsMax = bounds.Max;
            min = Vector3.Min(min, boundsMin);
            max = Vector3.Max(max, boundsMax);
            hasBounds = true;
        }

        return hasBounds
            ? new RuntimeReferenceBounds(min, max)
            : new RuntimeReferenceBounds(Vector3.Zero, Vector3.Zero);
    }

    private static RuntimeReferenceBounds ExpandBounds(RuntimeReferenceBounds bounds, Vector3 padding)
        => new(bounds.Min - padding, bounds.Max + padding);

    private static bool BoundsIntersect(RuntimeReferenceBounds a, RuntimeReferenceBounds b)
        => a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
            && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
            && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

    private static void ResolveUnitGeometry(
        Vector3 pivotModel,
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
        localCenterModel = pivotModel;
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
        Vector3 radial = localCenterModel - pivotModel;
        if (radial.LengthSquared() <= 1e-8f)
        {
            radial = Vector3.UnitX;
        }

        localNormalModel = Vector3.Normalize(radial);
        widthM = Math.Max(
            0.02,
            Math.Max(
                (max.X - min.X) * xMetersPerUnit,
                (max.Z - min.Z) * zMetersPerUnit));
        heightSpanM = Math.Max(0.02, (max.Y - min.Y) * yMetersPerUnit);
        sideLengthM = Math.Max(widthM, heightSpanM);
    }

    private static string ResolveTeam(string compositeName)
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

    private static string? ResolvePlateId(string unitName)
        => unitName.Contains(ArmorPlateKeyword, StringComparison.Ordinal) || unitName.Contains(LightStripKeyword, StringComparison.Ordinal)
            ? "base_top_slide"
            : null;

    private static bool IsLightStrip(string unitName)
        => unitName.Contains(LightStripKeyword, StringComparison.Ordinal);

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

    private static List<FineTerrainColoredTriangle> NormalizeBaseTriangles(
        IReadOnlyList<FineTerrainColoredTriangle> triangles,
        string team,
        bool isLightStrip)
    {
        var normalized = new List<FineTerrainColoredTriangle>(triangles.Count);
        foreach (FineTerrainColoredTriangle triangle in triangles)
        {
            normalized.Add(new FineTerrainColoredTriangle(
                triangle.A,
                triangle.B,
                triangle.C,
                ResolveNormalizedBaseTriangleColor(triangle.Color, team, isLightStrip)));
        }

        return normalized;
    }

    private static Color ResolveNormalizedBaseTriangleColor(Color source, string team, bool isLightStrip)
    {
        Color safeSource = source.A <= 0 ? Color.FromArgb(236, 224, 232, 240) : source;
        if (isLightStrip
            || ShouldApplyTeamAccentTint(safeSource)
            || (string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase) && ShouldForceBlueSideRecolor(safeSource)))
        {
            Color teamColor = string.Equals(team, "red", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(208, 66, 44)
                : string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(34, 82, 170)
                    : Color.FromArgb(112, 120, 128);
            return Color.FromArgb(safeSource.A, teamColor);
        }

        float luminance = (safeSource.R * 0.2126f + safeSource.G * 0.7152f + safeSource.B * 0.0722f) / 255f;
        int baseValue = Math.Clamp((int)MathF.Round(50f + luminance * 46f), 36, 104);
        return Color.FromArgb(
            safeSource.A,
            baseValue,
            Math.Clamp(baseValue + 4, 0, 255),
            Math.Clamp(baseValue + 10, 0, 255));
    }

    private static bool ShouldApplyTeamAccentTint(Color source)
    {
        int dominant = Math.Max(source.R, Math.Max(source.G, source.B));
        int minimum = Math.Min(source.R, Math.Min(source.G, source.B));
        if (dominant < 112 || dominant - minimum < 54)
        {
            return false;
        }

        bool redAccent = source.R > source.G + 18 && source.R > source.B + 18;
        bool blueAccent = source.B > source.G + 18 && source.B > source.R + 18;
        return redAccent || blueAccent;
    }

    private static bool ShouldForceBlueSideRecolor(Color source)
    {
        return source.R >= 84
            && source.R > source.G + 10
            && source.R > source.B + 18;
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
        FineTerrainBaseVisualScene scene)
    {
        int triangleCount = 0;
        int bodyTriangleCount = 0;
        int unitCount = 0;
        foreach (FineTerrainBaseVisualItem item in scene.Items)
        {
            LogMessage(
                $"item team={item.Team} name={item.Name} components={item.ComponentIds.Length} body_triangles={item.Triangles.Count} pivot=({item.PivotModel.X:0.###},{item.PivotModel.Y:0.###},{item.PivotModel.Z:0.###})");
            bodyTriangleCount += item.Triangles.Count;
            foreach (FineTerrainBaseUnitVisualItem unit in item.Units)
            {
                triangleCount += unit.Triangles.Count;
                unitCount++;
                LogMessage(
                    $"unit team={item.Team} plate={unit.PlateId} light={unit.IsLightStrip} components={unit.ComponentIds.Length} center=({unit.LocalCentroidModel.X:0.###},{unit.LocalCentroidModel.Y:0.###},{unit.LocalCentroidModel.Z:0.###}) normal=({unit.LocalNormalModel.X:0.###},{unit.LocalNormalModel.Y:0.###},{unit.LocalNormalModel.Z:0.###}) size=({unit.WidthM:0.###},{unit.HeightSpanM:0.###})");
            }
        }

        LogMessage(
            $"load_ok annotation={Path.GetFileName(annotationPath)} terrain={Path.GetFileName(terrainCachePath)} items={scene.Items.Count} units={unitCount} body_triangles={bodyTriangleCount} unit_triangles={triangleCount}");
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
        SimulatorRuntimeLog.Append("fine_terrain_base.log", $"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    private readonly record struct BaseBodyCandidate(
        int ComponentId,
        RuntimeReferenceBounds Bounds,
        List<FineTerrainColoredTriangle> Triangles);
}

internal sealed class FineTerrainBaseVisualScene
{
    public FineTerrainBaseVisualScene(
        FineTerrainWorldScale worldScale,
        IReadOnlyList<FineTerrainBaseVisualItem> items)
    {
        WorldScale = worldScale;
        Items = items;
    }

    public FineTerrainWorldScale WorldScale { get; }

    public IReadOnlyList<FineTerrainBaseVisualItem> Items { get; }
}

internal sealed class FineTerrainBaseVisualItem
{
    public FineTerrainBaseVisualItem(
        int compositeId,
        string team,
        string name,
        Vector3 pivotModel,
        Vector3 positionModel,
        Vector3 rotationYprDegrees,
        Vector3 coordinateYprDegrees,
        int[] componentIds,
        List<FineTerrainColoredTriangle> triangles,
        IReadOnlyList<FineTerrainBaseUnitVisualItem> units)
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

    public IReadOnlyList<FineTerrainBaseUnitVisualItem> Units { get; }
}

internal sealed class FineTerrainBaseUnitVisualItem
{
    public FineTerrainBaseUnitVisualItem(
        string name,
        string plateId,
        bool isLightStrip,
        int[] componentIds,
        Vector3 localCentroidModel,
        List<FineTerrainColoredTriangle> triangles)
    {
        Name = name;
        PlateId = plateId;
        IsLightStrip = isLightStrip;
        ComponentIds = componentIds;
        LocalCentroidModel = localCentroidModel;
        Triangles = triangles;
    }

    public string Name { get; }

    public string PlateId { get; }

    public bool IsLightStrip { get; }

    public int[] ComponentIds { get; }

    public Vector3 LocalCentroidModel { get; set; }

    public Vector3 LocalNormalModel { get; set; } = Vector3.UnitX;

    public double SideLengthM { get; set; } = 0.12;

    public double WidthM { get; set; } = 0.12;

    public double HeightSpanM { get; set; } = 0.12;

    public List<FineTerrainColoredTriangle> Triangles { get; set; }
}
