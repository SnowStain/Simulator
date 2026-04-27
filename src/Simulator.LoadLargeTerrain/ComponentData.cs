namespace LoadLargeTerrain;

internal sealed class ComponentData
{
    public required int Id { get; init; }

    public required int NodeIndex { get; init; }

    public required int MeshIndex { get; init; }

    public required int PrimitiveIndex { get; init; }

    public required string Name { get; init; }

    public required BoundingBox Bounds { get; init; }
}
