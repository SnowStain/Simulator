using System.Drawing;
using System.Linq;
using System.Numerics;

namespace Simulator.ThreeD;

internal sealed class TerrainFacetRuntime
{
    public TerrainFacetRuntime(
        string id,
        string type,
        string team,
        Color topColor,
        Color sideColor,
        IReadOnlyList<Vector2> pointsWorld,
        IReadOnlyList<float> heightsM)
    {
        Id = id;
        Type = type;
        Team = team;
        TopColor = topColor;
        SideColor = sideColor;
        PointsWorld = pointsWorld;
        HeightsM = heightsM;
        MinX = pointsWorld.Min(point => point.X);
        MaxX = pointsWorld.Max(point => point.X);
        MinY = pointsWorld.Min(point => point.Y);
        MaxY = pointsWorld.Max(point => point.Y);
        MaxHeightM = heightsM.Count == 0 ? 0f : heightsM.Max();
    }

    public string Id { get; }

    public string Type { get; }

    public string Team { get; }

    public Color TopColor { get; }

    public Color SideColor { get; }

    public IReadOnlyList<Vector2> PointsWorld { get; }

    public IReadOnlyList<float> HeightsM { get; }

    public float MinX { get; }

    public float MaxX { get; }

    public float MinY { get; }

    public float MaxY { get; }

    public float MaxHeightM { get; }
}

internal sealed class RuntimeGridData
{
    public RuntimeGridData(
        int widthCells,
        int heightCells,
        float[] heightMap,
        byte[] terrainTypeMap,
        bool[] movementBlockMap,
        bool[] visionBlockMap,
        float[] visionBlockHeightMap,
        byte[] functionPassMap,
        float[] functionHeadingMap,
        float cellWidthWorld,
        float cellHeightWorld,
        IReadOnlyList<TerrainFacetRuntime>? facets = null)
    {
        WidthCells = widthCells;
        HeightCells = heightCells;
        HeightMap = heightMap;
        TerrainTypeMap = terrainTypeMap;
        MovementBlockMap = movementBlockMap;
        VisionBlockMap = visionBlockMap;
        VisionBlockHeightMap = visionBlockHeightMap;
        FunctionPassMap = functionPassMap;
        FunctionHeadingMap = functionHeadingMap;
        CellWidthWorld = cellWidthWorld;
        CellHeightWorld = cellHeightWorld;
        Facets = facets ?? Array.Empty<TerrainFacetRuntime>();
    }

    public int WidthCells { get; }

    public int HeightCells { get; }

    public float[] HeightMap { get; }

    public byte[] TerrainTypeMap { get; }

    public bool[] MovementBlockMap { get; }

    public bool[] VisionBlockMap { get; }

    public float[] VisionBlockHeightMap { get; }

    public byte[] FunctionPassMap { get; }

    public float[] FunctionHeadingMap { get; }

    public float CellWidthWorld { get; }

    public float CellHeightWorld { get; }

    public IReadOnlyList<TerrainFacetRuntime> Facets { get; }

    public bool IsValid =>
        WidthCells > 0
        && HeightCells > 0
        && HeightMap.Length == WidthCells * HeightCells
        && TerrainTypeMap.Length == WidthCells * HeightCells
        && MovementBlockMap.Length == WidthCells * HeightCells
        && VisionBlockMap.Length == WidthCells * HeightCells
        && VisionBlockHeightMap.Length == WidthCells * HeightCells
        && FunctionPassMap.Length == WidthCells * HeightCells
        && FunctionHeadingMap.Length == WidthCells * HeightCells
        && CellWidthWorld > 0f
        && CellHeightWorld > 0f;

    public int IndexOf(int cellX, int cellY)
    {
        return checked(cellY * WidthCells + cellX);
    }

    public bool TrySampleFacetSurface(double worldX, double worldY, out float heightM, out Vector3 normal, out TerrainFacetRuntime? facet)
    {
        facet = null;
        heightM = 0f;
        normal = Vector3.UnitY;
        if (Facets.Count == 0)
        {
            return false;
        }

        Vector2 point = new((float)worldX, (float)worldY);
        float bestHeight = float.MinValue;
        Vector3 bestNormal = Vector3.UnitY;
        TerrainFacetRuntime? bestFacet = null;
        foreach (TerrainFacetRuntime candidate in Facets)
        {
            if (point.X < candidate.MinX || point.X > candidate.MaxX || point.Y < candidate.MinY || point.Y > candidate.MaxY)
            {
                continue;
            }

            if (!TrySampleFacetHeight(candidate, point, out float candidateHeight, out Vector3 candidateNormal))
            {
                continue;
            }

            if (candidateHeight > bestHeight)
            {
                bestHeight = candidateHeight;
                bestNormal = candidateNormal;
                bestFacet = candidate;
            }
        }

        if (bestFacet is null)
        {
            return false;
        }

        facet = bestFacet;
        heightM = bestHeight;
        normal = bestNormal;
        return true;
    }

    public float SampleHeightWithFacets(double worldX, double worldY)
    {
        float baseHeight = SampleSmoothedHeight(worldX, worldY);
        return TrySampleFacetSurface(worldX, worldY, out float facetHeight, out _, out _)
            ? Math.Max(baseHeight, facetHeight)
            : baseHeight;
    }

    public float SampleOcclusionHeight(double worldX, double worldY)
    {
        int cellX = Math.Clamp((int)Math.Floor(worldX / Math.Max(1e-6f, CellWidthWorld)), 0, WidthCells - 1);
        int cellY = Math.Clamp((int)Math.Floor(worldY / Math.Max(1e-6f, CellHeightWorld)), 0, HeightCells - 1);
        int index = IndexOf(cellX, cellY);
        float baseHeight = VisionBlockMap[index]
            ? Math.Max(HeightMap[index], VisionBlockHeightMap[index])
            : HeightMap[index];
        return TrySampleFacetSurface(worldX, worldY, out float facetHeight, out _, out _)
            ? Math.Max(baseHeight, facetHeight)
            : baseHeight;
    }

    public float SampleNearestHeight(double worldX, double worldY)
    {
        int cellX = Math.Clamp((int)Math.Floor(worldX / Math.Max(1e-6f, CellWidthWorld)), 0, WidthCells - 1);
        int cellY = Math.Clamp((int)Math.Floor(worldY / Math.Max(1e-6f, CellHeightWorld)), 0, HeightCells - 1);
        return HeightMap[IndexOf(cellX, cellY)];
    }

    private float SampleSmoothedHeight(double worldX, double worldY)
    {
        double cellWidth = Math.Max(1e-6f, CellWidthWorld);
        double cellHeight = Math.Max(1e-6f, CellHeightWorld);
        double normalizedX = Math.Clamp(worldX / cellWidth, 0.0, Math.Max(0.0, WidthCells - 1e-6));
        double normalizedY = Math.Clamp(worldY / cellHeight, 0.0, Math.Max(0.0, HeightCells - 1e-6));
        int cellX0 = Math.Clamp((int)Math.Floor(normalizedX), 0, WidthCells - 1);
        int cellY0 = Math.Clamp((int)Math.Floor(normalizedY), 0, HeightCells - 1);
        int cellX1 = Math.Min(cellX0 + 1, WidthCells - 1);
        int cellY1 = Math.Min(cellY0 + 1, HeightCells - 1);

        float h00 = HeightMap[IndexOf(cellX0, cellY0)];
        float h10 = HeightMap[IndexOf(cellX1, cellY0)];
        float h01 = HeightMap[IndexOf(cellX0, cellY1)];
        float h11 = HeightMap[IndexOf(cellX1, cellY1)];
        float min = Math.Min(Math.Min(h00, h10), Math.Min(h01, h11));
        float max = Math.Max(Math.Max(h00, h10), Math.Max(h01, h11));
        if (max - min > 0.049f)
        {
            return h00;
        }

        float tx = (float)Math.Clamp(normalizedX - cellX0, 0.0, 1.0);
        float ty = (float)Math.Clamp(normalizedY - cellY0, 0.0, 1.0);
        float top = h00 + (h10 - h00) * tx;
        float bottom = h01 + (h11 - h01) * tx;
        return top + (bottom - top) * ty;
    }

    private static bool TrySampleFacetHeight(TerrainFacetRuntime facet, Vector2 point, out float heightM, out Vector3 normal)
    {
        heightM = 0f;
        normal = Vector3.UnitY;
        if (facet.PointsWorld.Count < 3 || facet.HeightsM.Count < 3)
        {
            return false;
        }

        Vector2 anchor = facet.PointsWorld[0];
        float anchorHeight = facet.HeightsM[0];
        for (int index = 1; index < facet.PointsWorld.Count - 1; index++)
        {
            Vector2 b = facet.PointsWorld[index];
            Vector2 c = facet.PointsWorld[index + 1];
            float hb = index < facet.HeightsM.Count ? facet.HeightsM[index] : facet.HeightsM[^1];
            float hc = index + 1 < facet.HeightsM.Count ? facet.HeightsM[index + 1] : facet.HeightsM[^1];
            if (!TryComputeBarycentric(point, anchor, b, c, out Vector3 barycentric))
            {
                continue;
            }

            if (barycentric.X < -1e-4f || barycentric.Y < -1e-4f || barycentric.Z < -1e-4f)
            {
                continue;
            }

            heightM = anchorHeight * barycentric.X + hb * barycentric.Y + hc * barycentric.Z;
            Vector3 p0 = new(anchor.X, anchorHeight, anchor.Y);
            Vector3 p1 = new(b.X, hb, b.Y);
            Vector3 p2 = new(c.X, hc, c.Y);
            Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
            normal = cross.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(cross);
            if (normal.Y < 0f)
            {
                normal = -normal;
            }

            return true;
        }

        return false;
    }

    private static bool TryComputeBarycentric(Vector2 point, Vector2 a, Vector2 b, Vector2 c, out Vector3 barycentric)
    {
        barycentric = Vector3.Zero;
        float det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(det) <= 1e-7f)
        {
            return false;
        }

        float w1 = ((b.Y - c.Y) * (point.X - c.X) + (c.X - b.X) * (point.Y - c.Y)) / det;
        float w2 = ((c.Y - a.Y) * (point.X - c.X) + (a.X - c.X) * (point.Y - c.Y)) / det;
        float w3 = 1f - w1 - w2;
        barycentric = new Vector3(w1, w2, w3);
        return true;
    }
}
