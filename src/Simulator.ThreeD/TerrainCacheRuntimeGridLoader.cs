using Simulator.Assets;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed class TerrainCacheRuntimeGridLoader
{
    private const float WallNormalYThreshold = 0.35f;
    private const float WallHeightThresholdM = 0.08f;

    public RuntimeGridData Load(MapPresetDefinition preset, RuntimeGridDefinition runtime)
    {
        string? mapDirectory = Path.GetDirectoryName(preset.SourcePath);
        if (string.IsNullOrWhiteSpace(mapDirectory) || !Directory.Exists(mapDirectory))
        {
            throw new InvalidOperationException("Map preset directory is unavailable.");
        }

        string sourcePath = ResolveRelativePath(mapDirectory, runtime.SourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Terrain cache source file was not found: {sourcePath}", sourcePath);
        }

        float mapWidthWorld = (float)Math.Max(1.0, preset.Width);
        float mapHeightWorld = (float)Math.Max(1.0, preset.Height);
        float fieldLengthM = (float)Math.Max(1.0, preset.FieldLengthM);
        float fieldWidthM = (float)Math.Max(1.0, preset.FieldWidthM);
        float resolutionM = (float)Math.Max(0.001, runtime.ResolutionM);
        int widthCells = runtime.WidthCells > 0 ? runtime.WidthCells : Math.Max(1, (int)Math.Ceiling(fieldLengthM / resolutionM));
        int heightCells = runtime.HeightCells > 0 ? runtime.HeightCells : Math.Max(1, (int)Math.Ceiling(fieldWidthM / resolutionM));
        int total = checked(widthCells * heightCells);

        float cellWidth = mapWidthWorld / Math.Max(1, widthCells);
        float cellHeight = mapHeightWorld / Math.Max(1, heightCells);

        TerrainCacheCollisionSurface collisionSurface = TerrainCacheCollisionSurface.Load(preset, sourcePath);
        float[] heightMap = new float[total];
        byte[] terrainTypeMap = new byte[total];
        bool[] movementBlockMap = new bool[total];
        bool[] visionBlockMap = new bool[total];
        float[] visionBlockHeightMap = new float[total];
        byte[] functionPassMap = new byte[total];
        float[] functionHeadingMap = new float[total];
        Array.Fill(functionHeadingMap, float.NaN);

        return new RuntimeGridData(
            widthCells,
            heightCells,
            heightMap,
            terrainTypeMap,
            movementBlockMap,
            visionBlockMap,
            visionBlockHeightMap,
            functionPassMap,
            functionHeadingMap,
            cellWidth,
            cellHeight,
            collisionSurface: collisionSurface);
    }

    private static void RasterizeChunk(
        TerrainCacheMeshChunkHeader chunk,
        TerrainCacheVertex[] vertices,
        int[] indices,
        TerrainCacheCatalog catalog,
        float scaleX,
        float scaleZ,
        float verticalScale,
        float heightOffset,
        int widthCells,
        int heightCells,
        float cellWidth,
        float cellHeight,
        float[] heightMap,
        byte[] terrainTypeMap,
        bool[] movementBlockMap,
        bool[] visionBlockMap,
        float[] visionBlockHeightMap,
        byte[] functionPassMap)
    {
        if (vertices.Length == 0 || indices.Length < 3)
        {
            return;
        }

        float[] worldX = new float[vertices.Length];
        float[] worldY = new float[vertices.Length];
        float[] height = new float[vertices.Length];
        float modelCenterX = (catalog.MinX + catalog.MaxX) * 0.5f;
        float modelCenterZ = (catalog.MinZ + catalog.MaxZ) * 0.5f;
        float widthWorld = widthCells * cellWidth;
        float heightWorld = heightCells * cellHeight;
        for (int index = 0; index < vertices.Length; index++)
        {
            TerrainCacheVertex vertex = vertices[index];
            worldX[index] = widthWorld * 0.5f - (vertex.X - modelCenterX) * scaleX;
            worldY[index] = heightWorld * 0.5f - (vertex.Z - modelCenterZ) * scaleZ;
            height[index] = Math.Max(0f, (vertex.Y - heightOffset) * verticalScale);
        }

        int triangleIndexCount = indices.Length - indices.Length % 3;
        for (int index = 0; index < triangleIndexCount; index += 3)
        {
            int i0 = indices[index];
            int i1 = indices[index + 1];
            int i2 = indices[index + 2];
            if ((uint)i0 >= (uint)vertices.Length
                || (uint)i1 >= (uint)vertices.Length
                || (uint)i2 >= (uint)vertices.Length)
            {
                continue;
            }

            float x0 = worldX[i0];
            float y0 = worldY[i0];
            float h0 = height[i0];
            float x1 = worldX[i1];
            float y1 = worldY[i1];
            float h1 = height[i1];
            float x2 = worldX[i2];
            float y2 = worldY[i2];
            float h2 = height[i2];
            float maxHeight = Math.Max(h0, Math.Max(h1, h2));
            float minHeight = Math.Min(h0, Math.Min(h1, h2));
            float normalY = Math.Abs((vertices[i0].NormalY + vertices[i1].NormalY + vertices[i2].NormalY) / 3f);
            bool wallLike = normalY < WallNormalYThreshold && maxHeight - minHeight > WallHeightThresholdM;

            float area = Edge(x0, y0, x1, y1, x2, y2);
            if (Math.Abs(area) <= 1e-8f)
            {
                if (wallLike)
                {
                    MarkFootprint(
                        x0,
                        y0,
                        x1,
                        y1,
                        x2,
                        y2,
                        maxHeight,
                        widthCells,
                        heightCells,
                        cellWidth,
                        cellHeight,
                        heightMap,
                        terrainTypeMap,
                        movementBlockMap,
                        visionBlockMap,
                        visionBlockHeightMap,
                        functionPassMap);
                }

                continue;
            }

            RasterizeTriangle(
                x0,
                y0,
                h0,
                x1,
                y1,
                h1,
                x2,
                y2,
                h2,
                area,
                wallLike,
                maxHeight,
                widthCells,
                heightCells,
                cellWidth,
                cellHeight,
                heightMap,
                terrainTypeMap,
                movementBlockMap,
                visionBlockMap,
                visionBlockHeightMap,
                functionPassMap);
        }
    }

    private static void RasterizeTriangle(
        float x0,
        float y0,
        float h0,
        float x1,
        float y1,
        float h1,
        float x2,
        float y2,
        float h2,
        float area,
        bool wallLike,
        float maxHeight,
        int widthCells,
        int heightCells,
        float cellWidth,
        float cellHeight,
        float[] heightMap,
        byte[] terrainTypeMap,
        bool[] movementBlockMap,
        bool[] visionBlockMap,
        float[] visionBlockHeightMap,
        byte[] functionPassMap)
    {
        int minCellX = WorldToCellX(Math.Min(x0, Math.Min(x1, x2)), cellWidth, widthCells);
        int maxCellX = WorldToCellX(Math.Max(x0, Math.Max(x1, x2)), cellWidth, widthCells);
        int minCellY = WorldToCellY(Math.Min(y0, Math.Min(y1, y2)), cellHeight, heightCells);
        int maxCellY = WorldToCellY(Math.Max(y0, Math.Max(y1, y2)), cellHeight, heightCells);
        float invArea = 1f / area;

        for (int cellYIndex = minCellY; cellYIndex <= maxCellY; cellYIndex++)
        {
            float py = (cellYIndex + 0.5f) * cellHeight;
            int rowOffset = cellYIndex * widthCells;
            for (int cellXIndex = minCellX; cellXIndex <= maxCellX; cellXIndex++)
            {
                float px = (cellXIndex + 0.5f) * cellWidth;
                float w0 = Edge(x1, y1, x2, y2, px, py) * invArea;
                float w1 = Edge(x2, y2, x0, y0, px, py) * invArea;
                float w2 = 1f - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f)
                {
                    continue;
                }

                int cellIndex = rowOffset + cellXIndex;
                float sampleHeight = Math.Max(0f, h0 * w0 + h1 * w1 + h2 * w2);
                if (sampleHeight > heightMap[cellIndex])
                {
                    heightMap[cellIndex] = sampleHeight;
                    terrainTypeMap[cellIndex] = wallLike ? (byte)2 : (byte)12;
                }

                if (!wallLike)
                {
                    continue;
                }

                MarkBlockedCell(
                    cellIndex,
                    maxHeight,
                    heightMap,
                    terrainTypeMap,
                    movementBlockMap,
                    visionBlockMap,
                    visionBlockHeightMap,
                    functionPassMap);
            }
        }
    }

    private static void MarkFootprint(
        float x0,
        float y0,
        float x1,
        float y1,
        float x2,
        float y2,
        float maxHeight,
        int widthCells,
        int heightCells,
        float cellWidth,
        float cellHeight,
        float[] heightMap,
        byte[] terrainTypeMap,
        bool[] movementBlockMap,
        bool[] visionBlockMap,
        float[] visionBlockHeightMap,
        byte[] functionPassMap)
    {
        int minCellX = Math.Max(0, WorldToCellX(Math.Min(x0, Math.Min(x1, x2)), cellWidth, widthCells) - 1);
        int maxCellX = Math.Min(widthCells - 1, WorldToCellX(Math.Max(x0, Math.Max(x1, x2)), cellWidth, widthCells) + 1);
        int minCellY = Math.Max(0, WorldToCellY(Math.Min(y0, Math.Min(y1, y2)), cellHeight, heightCells) - 1);
        int maxCellY = Math.Min(heightCells - 1, WorldToCellY(Math.Max(y0, Math.Max(y1, y2)), cellHeight, heightCells) + 1);
        for (int cellYIndex = minCellY; cellYIndex <= maxCellY; cellYIndex++)
        {
            int rowOffset = cellYIndex * widthCells;
            for (int cellXIndex = minCellX; cellXIndex <= maxCellX; cellXIndex++)
            {
                int cellIndex = rowOffset + cellXIndex;
                MarkBlockedCell(
                    cellIndex,
                    maxHeight,
                    heightMap,
                    terrainTypeMap,
                    movementBlockMap,
                    visionBlockMap,
                    visionBlockHeightMap,
                    functionPassMap);
            }
        }
    }

    private static void MarkBlockedCell(
        int cellIndex,
        float maxHeight,
        float[] heightMap,
        byte[] terrainTypeMap,
        bool[] movementBlockMap,
        bool[] visionBlockMap,
        float[] visionBlockHeightMap,
        byte[] functionPassMap)
    {
        terrainTypeMap[cellIndex] = 2;
        movementBlockMap[cellIndex] = true;
        visionBlockMap[cellIndex] = true;
        if (maxHeight > heightMap[cellIndex])
        {
            heightMap[cellIndex] = maxHeight;
        }

        if (maxHeight > visionBlockHeightMap[cellIndex])
        {
            visionBlockHeightMap[cellIndex] = maxHeight;
        }

        functionPassMap[cellIndex] = 2;
    }

    private static float Edge(float ax, float ay, float bx, float by, float px, float py)
        => (px - ax) * (by - ay) - (py - ay) * (bx - ax);

    private static int WorldToCellX(float worldX, float cellWidth, int widthCells)
        => Math.Clamp((int)Math.Floor(worldX / Math.Max(1e-6f, cellWidth)), 0, widthCells - 1);

    private static int WorldToCellY(float worldY, float cellHeight, int heightCells)
        => Math.Clamp((int)Math.Floor(worldY / Math.Max(1e-6f, cellHeight)), 0, heightCells - 1);

    private static string ResolveRelativePath(string mapDirectory, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Terrain cache map requires runtime_grid.source_path.");
        }

        if (Path.IsPathRooted(value))
        {
            return value;
        }

        return Path.GetFullPath(Path.Combine(mapDirectory, value));
    }
}
