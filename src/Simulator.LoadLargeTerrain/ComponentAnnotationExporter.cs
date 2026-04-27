using System.Text.Json;

namespace LoadLargeTerrain;

internal static class ComponentAnnotationExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public static void Export(
        string path,
        string sourceModel,
        IReadOnlyList<ComponentData> components,
        IReadOnlySet<int> actorComponentIds,
        IReadOnlyList<CompositeObject> composites,
        WorldScale worldScale,
        IReadOnlyDictionary<int, System.Numerics.Vector4>? componentColorOverrides = null,
        IReadOnlyDictionary<int, string>? componentTerrainLabels = null,
        IReadOnlyList<CollisionShapeObject>? collisionShapes = null)
    {
        var payload = new ComponentAnnotationFile
        {
            SourceModel = sourceModel,
            ExportedUtc = DateTimeOffset.UtcNow,
            WorldScale = new WorldScaleAnnotation
            {
                MapLengthXMeters = WorldScale.RealLengthXMeters,
                MapLengthZMeters = WorldScale.RealLengthZMeters,
                XMetersPerModelUnit = worldScale.XMetersPerUnit,
                YMetersPerModelUnit = worldScale.YMetersPerUnit,
                ZMetersPerModelUnit = worldScale.ZMetersPerUnit,
            },
            TotalComponents = components.Count,
            ActorComponentIds = actorComponentIds.Order().ToArray(),
            Composites = composites.Select(composite => new CompositeAnnotation
            {
                Id = composite.Id,
                Name = composite.Name,
                Role = composite.IsActor ? "actor" : "static",
                ComponentIds = composite.ComponentIds.Order().ToArray(),
                InteractionUnits = composite.InteractionUnits
                    .OrderBy(unit => unit.Id)
                    .Select(unit => new InteractionUnitAnnotation
                    {
                        Id = unit.Id,
                        Name = unit.Name,
                        ComponentIds = unit.ComponentIds.Order().ToArray(),
                    })
                    .ToArray(),
                PositionMeters = VectorAnnotation.From(worldScale.ModelToMeters(composite.PositionModel)),
                PositionModel = VectorAnnotation.From(composite.PositionModel),
                PivotModel = VectorAnnotation.From(composite.PivotModel),
                YprDegrees = VectorAnnotation.From(composite.RotationYprDegrees),
                CoordinateYprDegrees = VectorAnnotation.From(composite.CoordinateYprDegrees),
                CoordinateSystemMode = composite.CoordinateSystemMode == CoordinateSystemMode.Custom ? "custom" : "world",
            }).ToArray(),
            Components = components.Select(component => new ComponentAnnotation
            {
                Id = component.Id,
                NodeIndex = component.NodeIndex,
                MeshIndex = component.MeshIndex,
                PrimitiveIndex = component.PrimitiveIndex,
                Name = component.Name,
                Role = actorComponentIds.Contains(component.Id) ? "actor" : "static",
                TerrainLabel = componentTerrainLabels is not null && componentTerrainLabels.TryGetValue(component.Id, out var terrainLabel)
                    ? terrainLabel
                    : string.Empty,
                Bounds = BoundsAnnotation.From(component.Bounds),
            }).ToArray(),
            ComponentColorOverrides = componentColorOverrides?
                .Where(pair => components.Any(component => component.Id == pair.Key))
                .OrderBy(pair => pair.Key)
                .Select(pair => new ComponentColorOverrideAnnotation
                {
                    ComponentId = pair.Key,
                    R = Math.Clamp(pair.Value.X, 0.0f, 1.0f),
                    G = Math.Clamp(pair.Value.Y, 0.0f, 1.0f),
                    B = Math.Clamp(pair.Value.Z, 0.0f, 1.0f),
                    A = Math.Clamp(pair.Value.W, 0.0f, 1.0f),
                })
                .ToArray() ?? [],
            CollisionShapes = collisionShapes?
                .OrderBy(shape => shape.Id)
                .Select(shape => new CollisionShapeAnnotation
                {
                    Id = shape.Id,
                    Name = shape.Name,
                    ShapeType = shape.ShapeType.ToString().ToLowerInvariant(),
                    PositionModel = VectorAnnotation.From(shape.PositionModel),
                    SizeModel = VectorAnnotation.From(shape.SizeModel),
                    RadiusModel = shape.RadiusModel,
                    HeightModel = shape.HeightModel,
                    YprDegrees = VectorAnnotation.From(shape.RotationYprDegrees),
                    TerrainLabel = shape.TerrainLabel,
                    VerticesModel = shape.VerticesModel.Select(VectorAnnotation.From).ToArray(),
                })
                .ToArray() ?? [],
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, payload, JsonOptions);
    }

    private sealed class ComponentAnnotationFile
    {
        public required string SourceModel { get; init; }

        public required DateTimeOffset ExportedUtc { get; init; }

        public required WorldScaleAnnotation WorldScale { get; init; }

        public required int TotalComponents { get; init; }

        public required int[] ActorComponentIds { get; init; }

        public required CompositeAnnotation[] Composites { get; init; }

        public required ComponentAnnotation[] Components { get; init; }

        public required ComponentColorOverrideAnnotation[] ComponentColorOverrides { get; init; }

        public required CollisionShapeAnnotation[] CollisionShapes { get; init; }
    }

    private sealed class WorldScaleAnnotation
    {
        public required float MapLengthXMeters { get; init; }

        public required float MapLengthZMeters { get; init; }

        public required float XMetersPerModelUnit { get; init; }

        public required float YMetersPerModelUnit { get; init; }

        public required float ZMetersPerModelUnit { get; init; }
    }

    private sealed class CompositeAnnotation
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public required string Role { get; init; }

        public required int[] ComponentIds { get; init; }

        public required InteractionUnitAnnotation[] InteractionUnits { get; init; }

        public required VectorAnnotation PositionMeters { get; init; }

        public required VectorAnnotation PositionModel { get; init; }

        public required VectorAnnotation PivotModel { get; init; }

        public required VectorAnnotation YprDegrees { get; init; }

        public required VectorAnnotation CoordinateYprDegrees { get; init; }

        public required string CoordinateSystemMode { get; init; }
    }

    private sealed class InteractionUnitAnnotation
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public required int[] ComponentIds { get; init; }
    }

    private sealed class ComponentAnnotation
    {
        public required int Id { get; init; }

        public required int NodeIndex { get; init; }

        public required int MeshIndex { get; init; }

        public required int PrimitiveIndex { get; init; }

        public required string Name { get; init; }

        public required string Role { get; init; }

        public required string TerrainLabel { get; init; }

        public required BoundsAnnotation Bounds { get; init; }
    }

    private sealed class CollisionShapeAnnotation
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public required string ShapeType { get; init; }

        public required VectorAnnotation PositionModel { get; init; }

        public required VectorAnnotation SizeModel { get; init; }

        public required float RadiusModel { get; init; }

        public required float HeightModel { get; init; }

        public required VectorAnnotation YprDegrees { get; init; }

        public required string TerrainLabel { get; init; }

        public required VectorAnnotation[] VerticesModel { get; init; }
    }

    private sealed class ComponentColorOverrideAnnotation
    {
        public required int ComponentId { get; init; }

        public required float R { get; init; }

        public required float G { get; init; }

        public required float B { get; init; }

        public required float A { get; init; }
    }

    private sealed class BoundsAnnotation
    {
        public required float[] Min { get; init; }

        public required float[] Max { get; init; }

        public static BoundsAnnotation From(BoundingBox bounds)
        {
            return new BoundsAnnotation
            {
                Min = [bounds.Min.X, bounds.Min.Y, bounds.Min.Z],
                Max = [bounds.Max.X, bounds.Max.Y, bounds.Max.Z],
            };
        }
    }

    private sealed class VectorAnnotation
    {
        public required float X { get; init; }

        public required float Y { get; init; }

        public required float Z { get; init; }

        public static VectorAnnotation From(System.Numerics.Vector3 value)
        {
            return new VectorAnnotation
            {
                X = value.X,
                Y = value.Y,
                Z = value.Z,
            };
        }
    }
}
