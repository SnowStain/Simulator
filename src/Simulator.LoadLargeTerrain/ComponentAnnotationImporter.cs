using System.Text.Json;

namespace LoadLargeTerrain;

internal static class ComponentAnnotationImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ImportedAnnotationData? TryLoad(string path, WorldScale worldScale)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<ComponentAnnotationFile>(stream, JsonOptions);
        if (file is null)
        {
            return null;
        }

        var composites = new List<ImportedCompositeData>(file.Composites?.Length ?? 0);
        if (file.Composites is not null)
        {
            foreach (var composite in file.Composites)
            {
                if (composite.Id is null || composite.ComponentIds is null)
                {
                    continue;
                }

                var positionModel = composite.PositionModel?.ToVector3() ??
                                    (composite.PositionMeters is not null
                                        ? worldScale.MetersToModel(composite.PositionMeters.ToVector3())
                                        : System.Numerics.Vector3.Zero);

                composites.Add(new ImportedCompositeData
                {
                    Id = composite.Id.Value,
                    Name = string.IsNullOrWhiteSpace(composite.Name) ? $"组合体 {composite.Id.Value}" : composite.Name,
                    IsActor = string.Equals(composite.Role, "actor", StringComparison.OrdinalIgnoreCase),
                    ComponentIds = composite.ComponentIds,
                    InteractionUnits = composite.InteractionUnits?
                        .Where(unit => unit.Id.HasValue && unit.ComponentIds is not null)
                        .Select(unit => new ImportedInteractionUnitData
                        {
                            Id = unit.Id!.Value,
                            Name = string.IsNullOrWhiteSpace(unit.Name) ? $"互动单元 {unit.Id!.Value}" : unit.Name!,
                            ComponentIds = unit.ComponentIds!,
                        })
                        .ToList() ?? [],
                    PositionModel = positionModel,
                    PivotModel = composite.PivotModel?.ToVector3() ?? positionModel,
                    RotationYprDegrees = composite.YprDegrees?.ToVector3() ?? System.Numerics.Vector3.Zero,
                    CoordinateYprDegrees = composite.CoordinateYprDegrees?.ToVector3() ??
                                           composite.YprDegrees?.ToVector3() ??
                                           System.Numerics.Vector3.Zero,
                    CoordinateSystemMode = ParseCoordinateSystemMode(composite.CoordinateSystemMode),
                });
            }
        }

        return new ImportedAnnotationData
        {
            SourcePath = Path.GetFullPath(path),
            ActorComponentIds = file.ActorComponentIds ?? [],
            Composites = composites,
            ComponentTerrainLabels = file.Components?
                .Where(item => item.Id.HasValue && !string.IsNullOrWhiteSpace(item.TerrainLabel))
                .ToDictionary(item => item.Id!.Value, item => item.TerrainLabel!.Trim()) ?? new Dictionary<int, string>(),
            ComponentColorOverrides = file.ComponentColorOverrides?
                .Where(item => item.ComponentId.HasValue)
                .ToDictionary(
                    item => item.ComponentId!.Value,
                    item => new System.Numerics.Vector4(
                        Math.Clamp(item.R, 0.0f, 1.0f),
                        Math.Clamp(item.G, 0.0f, 1.0f),
                        Math.Clamp(item.B, 0.0f, 1.0f),
                        Math.Clamp(item.A <= 0.0f ? 1.0f : item.A, 0.0f, 1.0f))) ?? new Dictionary<int, System.Numerics.Vector4>(),
            CollisionShapes = file.CollisionShapes?
                .Where(item => item.Id.HasValue)
                .Select(item =>
                {
                    var shape = new ImportedCollisionShapeData
                    {
                        Id = item.Id!.Value,
                        Name = string.IsNullOrWhiteSpace(item.Name) ? $"Collision {item.Id!.Value}" : item.Name!,
                        ShapeType = ParseCollisionShapeType(item.ShapeType),
                        PositionModel = item.PositionModel?.ToVector3() ?? System.Numerics.Vector3.Zero,
                        SizeModel = item.SizeModel?.ToVector3() ?? System.Numerics.Vector3.One,
                        RadiusModel = item.RadiusModel <= 0.0f ? 1.0f : item.RadiusModel,
                        HeightModel = item.HeightModel <= 0.0f ? 1.0f : item.HeightModel,
                        RotationYprDegrees = item.YprDegrees?.ToVector3() ?? System.Numerics.Vector3.Zero,
                        TerrainLabel = item.TerrainLabel?.Trim() ?? string.Empty,
                        VerticesModel = item.VerticesModel?.Select(vertex => vertex.ToVector3()).ToList() ?? [],
                    };

                    return shape;
                })
                .ToList() ?? [],
        };
    }

    internal sealed class ImportedAnnotationData
    {
        public required string SourcePath { get; init; }

        public required int[] ActorComponentIds { get; init; }

        public required List<ImportedCompositeData> Composites { get; init; }

        public required Dictionary<int, string> ComponentTerrainLabels { get; init; }

        public required Dictionary<int, System.Numerics.Vector4> ComponentColorOverrides { get; init; }

        public required List<ImportedCollisionShapeData> CollisionShapes { get; init; }
    }

    internal sealed class ImportedCompositeData
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public required bool IsActor { get; init; }

        public required int[] ComponentIds { get; init; }

        public required List<ImportedInteractionUnitData> InteractionUnits { get; init; }

        public required System.Numerics.Vector3 PositionModel { get; init; }

        public required System.Numerics.Vector3 PivotModel { get; init; }

        public required System.Numerics.Vector3 RotationYprDegrees { get; init; }

        public required System.Numerics.Vector3 CoordinateYprDegrees { get; init; }

        public required CoordinateSystemMode CoordinateSystemMode { get; init; }
    }

    internal sealed class ImportedInteractionUnitData
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public required int[] ComponentIds { get; init; }
    }

    internal sealed class ImportedCollisionShapeData
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public required CollisionShapeType ShapeType { get; init; }

        public required System.Numerics.Vector3 PositionModel { get; init; }

        public required System.Numerics.Vector3 SizeModel { get; init; }

        public required float RadiusModel { get; init; }

        public required float HeightModel { get; init; }

        public required System.Numerics.Vector3 RotationYprDegrees { get; init; }

        public required string TerrainLabel { get; init; }

        public required List<System.Numerics.Vector3> VerticesModel { get; init; }
    }

    private sealed class ComponentAnnotationFile
    {
        public int[]? ActorComponentIds { get; init; }

        public CompositeAnnotation[]? Composites { get; init; }

        public ComponentAnnotation[]? Components { get; init; }

        public ComponentColorOverrideAnnotation[]? ComponentColorOverrides { get; init; }

        public CollisionShapeAnnotation[]? CollisionShapes { get; init; }
    }

    private sealed class CompositeAnnotation
    {
        public int? Id { get; init; }

        public string? Name { get; init; }

        public string? Role { get; init; }

        public int[]? ComponentIds { get; init; }

        public InteractionUnitAnnotation[]? InteractionUnits { get; init; }

        public VectorAnnotation? PositionMeters { get; init; }

        public VectorAnnotation? PositionModel { get; init; }

        public VectorAnnotation? PivotModel { get; init; }

        public VectorAnnotation? YprDegrees { get; init; }

        public VectorAnnotation? CoordinateYprDegrees { get; init; }

        public string? CoordinateSystemMode { get; init; }
    }

    private static CoordinateSystemMode ParseCoordinateSystemMode(string? raw)
    {
        return string.Equals(raw, "custom", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "local", StringComparison.OrdinalIgnoreCase)
            ? CoordinateSystemMode.Custom
            : CoordinateSystemMode.World;
    }

    private static CollisionShapeType ParseCollisionShapeType(string? raw)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            "cylinder" => CollisionShapeType.Cylinder,
            "polyhedron" => CollisionShapeType.Polyhedron,
            _ => CollisionShapeType.Box,
        };
    }

    private sealed class InteractionUnitAnnotation
    {
        public int? Id { get; init; }

        public string? Name { get; init; }

        public int[]? ComponentIds { get; init; }
    }

    private sealed class ComponentAnnotation
    {
        public int? Id { get; init; }

        public string? TerrainLabel { get; init; }
    }

    private sealed class ComponentColorOverrideAnnotation
    {
        public int? ComponentId { get; init; }

        public float R { get; init; }

        public float G { get; init; }

        public float B { get; init; }

        public float A { get; init; } = 1.0f;
    }

    private sealed class CollisionShapeAnnotation
    {
        public int? Id { get; init; }

        public string? Name { get; init; }

        public string? ShapeType { get; init; }

        public VectorAnnotation? PositionModel { get; init; }

        public VectorAnnotation? SizeModel { get; init; }

        public float RadiusModel { get; init; }

        public float HeightModel { get; init; }

        public VectorAnnotation? YprDegrees { get; init; }

        public string? TerrainLabel { get; init; }

        public VectorAnnotation[]? VerticesModel { get; init; }
    }

    private sealed class VectorAnnotation
    {
        public float X { get; init; }

        public float Y { get; init; }

        public float Z { get; init; }

        public System.Numerics.Vector3 ToVector3()
        {
            return new System.Numerics.Vector3(X, Y, Z);
        }
    }
}
