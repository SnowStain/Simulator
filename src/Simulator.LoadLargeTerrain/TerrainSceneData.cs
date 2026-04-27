using System.Numerics;

namespace LoadLargeTerrain;

internal sealed class TerrainSceneData
{
    public required BoundingBox Bounds { get; init; }

    public required IReadOnlyList<TerrainChunkData> Chunks { get; init; }

    public required IReadOnlyList<ComponentData> Components { get; init; }

    public required long SourceWriteTimeUtcTicks { get; init; }

    public Vector3 RecommendedSpawn
    {
        get
        {
            var center = Bounds.Center;
            var size = Bounds.Size;
            var altitude = MathF.Max(size.Y * 0.25f, MathF.Max(size.X, size.Z) * 0.04f);
            return new Vector3(center.X, Bounds.Max.Y + altitude, center.Z);
        }
    }

    public float RecommendedMoveSpeed
    {
        get
        {
            var size = Bounds.Size;
            return MathF.Max(15.0f, MathF.Max(size.X, size.Z) * 0.08f);
        }
    }

    public float RecommendedFarPlane
    {
        get
        {
            var size = Bounds.Size;
            return MathF.Max(2000.0f, MathF.Max(size.X, size.Z) * 1.5f);
        }
    }
}
