using System.Numerics;

namespace LoadLargeTerrain;

public readonly record struct RuntimeReferenceBounds(
    Vector3 Min,
    Vector3 Max)
{
    public Vector3 Center => (Min + Max) * 0.5f;

    public Vector3 Size => Max - Min;
}

public readonly record struct RuntimeReferenceWorldScale(
    float XMetersPerUnit,
    float YMetersPerUnit,
    float ZMetersPerUnit,
    Vector3 ModelCenter);

public readonly record struct RuntimeReferenceVertex(
    Vector3 Position,
    Vector3 Normal,
    uint Color);

public sealed record RuntimeReferenceComponent(
    int Id,
    int NodeIndex,
    int MeshIndex,
    int PrimitiveIndex,
    string Name,
    RuntimeReferenceBounds Bounds);

public sealed record RuntimeReferenceComponentRange(
    int ComponentId,
    int StartIndex,
    int IndexCount,
    RuntimeReferenceBounds Bounds);

public sealed record RuntimeReferenceChunk(
    string Name,
    RuntimeReferenceBounds Bounds,
    RuntimeReferenceVertex[] Vertices,
    uint[] Indices,
    RuntimeReferenceComponentRange[] ComponentRanges);

public sealed record RuntimeReferenceInteractionUnit(
    int Id,
    string Name,
    int[] ComponentIds);

public sealed record RuntimeReferenceComposite(
    int Id,
    string Name,
    bool IsActor,
    int[] ComponentIds,
    RuntimeReferenceInteractionUnit[] InteractionUnits,
    Vector3 PositionModel,
    Vector3 PivotModel,
    Vector3 RotationYprDegrees,
    Vector3 CoordinateYprDegrees,
    string CoordinateSystemMode);

public sealed class RuntimeReferenceScene
{
    public required string TerrainCachePath { get; init; }

    public required string ModelPath { get; init; }

    public string? AnnotationPath { get; init; }

    public required RuntimeReferenceBounds Bounds { get; init; }

    public required RuntimeReferenceWorldScale WorldScale { get; init; }

    public required IReadOnlyList<RuntimeReferenceChunk> Chunks { get; init; }

    public required IReadOnlyList<RuntimeReferenceComponent> Components { get; init; }

    public required IReadOnlyList<RuntimeReferenceComposite> Composites { get; init; }
}

public static class RuntimeReferenceLoader
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, RuntimeReferenceScene> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static RuntimeReferenceScene Load(
        string terrainCachePath,
        string? annotationPath = null,
        string? modelPath = null)
    {
        if (string.IsNullOrWhiteSpace(terrainCachePath))
        {
            throw new ArgumentException("Terrain cache path cannot be empty.", nameof(terrainCachePath));
        }

        string fullTerrainCachePath = Path.GetFullPath(terrainCachePath);
        string resolvedModelPath = ResolveModelPath(fullTerrainCachePath, modelPath);
        string? fullAnnotationPath = string.IsNullOrWhiteSpace(annotationPath)
            ? null
            : Path.GetFullPath(annotationPath);
        string cacheKey = BuildCacheKey(fullTerrainCachePath, resolvedModelPath, fullAnnotationPath);

        lock (Gate)
        {
            if (Cache.TryGetValue(cacheKey, out RuntimeReferenceScene? cached))
            {
                return cached;
            }
        }

        TerrainSceneData scene = SceneCache.LoadOrBuild(resolvedModelPath, fullTerrainCachePath);
        var worldScale = new WorldScale(scene.Bounds);
        ComponentAnnotationImporter.ImportedAnnotationData? importedAnnotations =
            string.IsNullOrWhiteSpace(fullAnnotationPath) || !File.Exists(fullAnnotationPath)
                ? null
                : ComponentAnnotationImporter.TryLoad(fullAnnotationPath, worldScale);

        RuntimeReferenceScene loaded = new()
        {
            TerrainCachePath = fullTerrainCachePath,
            ModelPath = resolvedModelPath,
            AnnotationPath = fullAnnotationPath,
            Bounds = ToRuntimeBounds(scene.Bounds),
            WorldScale = new RuntimeReferenceWorldScale(
                worldScale.XMetersPerUnit,
                worldScale.YMetersPerUnit,
                worldScale.ZMetersPerUnit,
                scene.Bounds.Center),
            Chunks = scene.Chunks.Select(ToRuntimeChunk).ToArray(),
            Components = scene.Components.Select(ToRuntimeComponent).ToArray(),
            Composites = importedAnnotations?.Composites.Select(ToRuntimeComposite).ToArray()
                ?? Array.Empty<RuntimeReferenceComposite>(),
        };

        lock (Gate)
        {
            Cache[cacheKey] = loaded;
        }

        return loaded;
    }

    private static string BuildCacheKey(
        string terrainCachePath,
        string modelPath,
        string? annotationPath)
    {
        long terrainTicks = File.Exists(terrainCachePath) ? File.GetLastWriteTimeUtc(terrainCachePath).Ticks : 0L;
        long modelTicks = File.Exists(modelPath) ? File.GetLastWriteTimeUtc(modelPath).Ticks : 0L;
        long annotationTicks = !string.IsNullOrWhiteSpace(annotationPath) && File.Exists(annotationPath)
            ? File.GetLastWriteTimeUtc(annotationPath).Ticks
            : 0L;
        return $"{terrainCachePath}|{terrainTicks}|{modelPath}|{modelTicks}|{annotationPath}|{annotationTicks}";
    }

    private static string ResolveModelPath(string terrainCachePath, string? modelPath)
    {
        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            string fullPath = Path.GetFullPath(modelPath);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        string? directory = Path.GetDirectoryName(terrainCachePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new FileNotFoundException("Cannot resolve model path for terrain cache.", terrainCachePath);
        }

        string fileName = Path.GetFileName(terrainCachePath);
        var candidates = new List<string>(6);
        if (fileName.EndsWith(".terraincache.lz4", StringComparison.OrdinalIgnoreCase))
        {
            string stem = fileName[..^".terraincache.lz4".Length];
            candidates.Add(Path.Combine(directory, stem + ".glb"));
        }

        if (fileName.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + ".glb"));
        }

        candidates.Add(Path.Combine(directory, "RMUC2026_MAP.glb"));
        candidates.Add(Path.Combine(directory, "rmuc2026_map.glb"));
        candidates.Add(Path.Combine(directory, "scene.glb"));
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("Cannot find a matching .glb model for terrain cache.", terrainCachePath);
    }

    private static RuntimeReferenceBounds ToRuntimeBounds(BoundingBox bounds)
        => new(bounds.Min, bounds.Max);

    private static RuntimeReferenceComponent ToRuntimeComponent(ComponentData component)
        => new(
            component.Id,
            component.NodeIndex,
            component.MeshIndex,
            component.PrimitiveIndex,
            component.Name,
            ToRuntimeBounds(component.Bounds));

    private static RuntimeReferenceChunk ToRuntimeChunk(TerrainChunkData chunk)
        => new(
            chunk.Name,
            ToRuntimeBounds(chunk.Bounds),
            chunk.Vertices.Select(vertex => new RuntimeReferenceVertex(vertex.Position, vertex.Normal, vertex.Color)).ToArray(),
            chunk.Indices.ToArray(),
            chunk.ComponentRanges
                .Select(range => new RuntimeReferenceComponentRange(
                    range.ComponentId,
                    range.StartIndex,
                    range.IndexCount,
                    ToRuntimeBounds(range.Bounds)))
                .ToArray());

    private static RuntimeReferenceComposite ToRuntimeComposite(ComponentAnnotationImporter.ImportedCompositeData composite)
        => new(
            composite.Id,
            composite.Name,
            composite.IsActor,
            composite.ComponentIds.ToArray(),
            composite.InteractionUnits
                .Select(unit => new RuntimeReferenceInteractionUnit(
                    unit.Id,
                    unit.Name,
                    unit.ComponentIds.ToArray()))
                .ToArray(),
            composite.PositionModel,
            composite.PivotModel,
            composite.RotationYprDegrees,
            composite.CoordinateYprDegrees,
            composite.CoordinateSystemMode == CoordinateSystemMode.Custom ? "custom" : "world");
}
