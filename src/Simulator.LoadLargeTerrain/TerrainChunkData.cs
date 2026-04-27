namespace LoadLargeTerrain;

internal sealed class TerrainChunkData
{
    public required string Name { get; init; }

    public required BoundingBox Bounds { get; init; }

    public required VertexData[] Vertices { get; init; }

    public required uint[] Indices { get; init; }

    public required ComponentRangeData[] ComponentRanges { get; init; }
}
