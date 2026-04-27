using System.Numerics;

namespace LoadLargeTerrain;

internal enum CollisionShapeType
{
    Box,
    Cylinder,
    Polyhedron,
}

internal sealed class CollisionShapeObject
{
    public required int Id { get; init; }

    public required string Name { get; set; }

    public CollisionShapeType ShapeType { get; set; }

    public Vector3 PositionModel { get; set; }

    public Vector3 SizeModel { get; set; } = Vector3.One;

    public float RadiusModel { get; set; } = 1.0f;

    public float HeightModel { get; set; } = 1.0f;

    public Vector3 RotationYprDegrees { get; set; }

    public string TerrainLabel { get; set; } = string.Empty;

    public List<Vector3> VerticesModel { get; } = new();

    public CollisionShapeObject Clone()
    {
        var clone = new CollisionShapeObject
        {
            Id = Id,
            Name = Name,
            ShapeType = ShapeType,
            PositionModel = PositionModel,
            SizeModel = SizeModel,
            RadiusModel = RadiusModel,
            HeightModel = HeightModel,
            RotationYprDegrees = RotationYprDegrees,
            TerrainLabel = TerrainLabel,
        };

        clone.VerticesModel.AddRange(VerticesModel);
        return clone;
    }
}
