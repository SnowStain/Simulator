namespace LoadLargeTerrain;

internal sealed class ComponentRangeData
{
    public required int ComponentId { get; init; }

    public required int StartIndex { get; init; }

    public required int IndexCount { get; init; }

    public required BoundingBox Bounds { get; init; }
}
