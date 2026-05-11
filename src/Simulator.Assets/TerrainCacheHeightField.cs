namespace Simulator.Assets;

public sealed class TerrainCacheHeightField
{
    private const int DefaultGridColumns = 192;
    private const int DefaultGridRows = 128;

    private readonly TerrainTriangle[] _triangles;
    private readonly int[][] _cells;
    private readonly TerrainCacheCatalog _catalog;
    private readonly int _columns;
    private readonly int _rows;
    private readonly float _cellWidth;
    private readonly float _cellDepth;

    private TerrainCacheHeightField(
        TerrainCacheCatalog catalog,
        TerrainTriangle[] triangles,
        int[][] cells,
        int columns,
        int rows)
    {
        _catalog = catalog;
        _triangles = triangles;
        _cells = cells;
        _columns = columns;
        _rows = rows;
        _cellWidth = MathF.Max(1e-5f, (catalog.MaxX - catalog.MinX) / Math.Max(1, columns));
        _cellDepth = MathF.Max(1e-5f, (catalog.MaxZ - catalog.MinZ) / Math.Max(1, rows));
    }

    public TerrainCacheCatalog Catalog => _catalog;

    public static TerrainCacheHeightField Load(string cachePath)
    {
        var triangles = new List<TerrainTriangle>(capacity: 64_000);
        TerrainCacheCatalog? catalog = null;
        var reader = new TerrainCacheMeshReader();
        catalog = reader.Load(
            cachePath,
            (loadedCatalog, _, vertices, indices, _) =>
            {
                catalog ??= loadedCatalog;
                AppendTriangles(vertices, indices, triangles);
            });

        if (catalog is null)
        {
            throw new InvalidDataException($"Terrain cache contains no catalog: {cachePath}");
        }

        TerrainTriangle[] packedTriangles = triangles.ToArray();
        int columns = DefaultGridColumns;
        int rows = DefaultGridRows;
        var cellBuckets = new List<int>[columns * rows];
        for (int index = 0; index < packedTriangles.Length; index++)
        {
            TerrainTriangle triangle = packedTriangles[index];
            int minColumn = ClampColumn(catalog, columns, triangle.MinX);
            int maxColumn = ClampColumn(catalog, columns, triangle.MaxX);
            int minRow = ClampRow(catalog, rows, triangle.MinZ);
            int maxRow = ClampRow(catalog, rows, triangle.MaxZ);
            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int cellIndex = row * columns + column;
                    (cellBuckets[cellIndex] ??= new List<int>(4)).Add(index);
                }
            }
        }

        int[][] cells = new int[cellBuckets.Length][];
        for (int index = 0; index < cellBuckets.Length; index++)
        {
            cells[index] = cellBuckets[index]?.ToArray() ?? Array.Empty<int>();
        }

        return new TerrainCacheHeightField(catalog, packedTriangles, cells, columns, rows);
    }

    public bool TrySample(float modelX, float modelZ, out TerrainCacheHeightSample sample)
    {
        sample = default;
        if (_triangles.Length == 0
            || modelX < _catalog.MinX || modelX > _catalog.MaxX
            || modelZ < _catalog.MinZ || modelZ > _catalog.MaxZ)
        {
            return false;
        }

        int column = ClampColumn(_catalog, _columns, modelX);
        int row = ClampRow(_catalog, _rows, modelZ);
        int[] candidates = _cells[row * _columns + column];
        bool found = false;
        float bestY = float.NegativeInfinity;
        TerrainTriangle bestTriangle = default;
        foreach (int triangleIndex in candidates)
        {
            TerrainTriangle triangle = _triangles[triangleIndex];
            if (!triangle.ContainsXZ(modelX, modelZ, out float y))
            {
                continue;
            }

            if (!found || y > bestY)
            {
                found = true;
                bestY = y;
                bestTriangle = triangle;
            }
        }

        if (!found)
        {
            return false;
        }

        sample = new TerrainCacheHeightSample(bestY, bestTriangle.NormalX, bestTriangle.NormalY, bestTriangle.NormalZ);
        return true;
    }

    private static void AppendTriangles(
        TerrainCacheVertex[] vertices,
        int[] indices,
        List<TerrainTriangle> triangles)
    {
        for (int index = 0; index + 2 < indices.Length; index += 3)
        {
            TerrainCacheVertex a = vertices[Math.Clamp(indices[index], 0, vertices.Length - 1)];
            TerrainCacheVertex b = vertices[Math.Clamp(indices[index + 1], 0, vertices.Length - 1)];
            TerrainCacheVertex c = vertices[Math.Clamp(indices[index + 2], 0, vertices.Length - 1)];
            if (TerrainTriangle.TryCreate(a, b, c, out TerrainTriangle triangle))
            {
                triangles.Add(triangle);
            }
        }
    }

    private static int ClampColumn(TerrainCacheCatalog catalog, int columns, float modelX)
    {
        float span = MathF.Max(1e-5f, catalog.MaxX - catalog.MinX);
        int column = (int)((modelX - catalog.MinX) / span * columns);
        return Math.Clamp(column, 0, columns - 1);
    }

    private static int ClampRow(TerrainCacheCatalog catalog, int rows, float modelZ)
    {
        float span = MathF.Max(1e-5f, catalog.MaxZ - catalog.MinZ);
        int row = (int)((modelZ - catalog.MinZ) / span * rows);
        return Math.Clamp(row, 0, rows - 1);
    }

    private readonly record struct TerrainTriangle(
        float Ax,
        float Ay,
        float Az,
        float Bx,
        float By,
        float Bz,
        float Cx,
        float Cy,
        float Cz,
        float NormalX,
        float NormalY,
        float NormalZ,
        float MinX,
        float MaxX,
        float MinZ,
        float MaxZ)
    {
        public static bool TryCreate(TerrainCacheVertex a, TerrainCacheVertex b, TerrainCacheVertex c, out TerrainTriangle triangle)
        {
            triangle = default;
            float ux = b.X - a.X;
            float uy = b.Y - a.Y;
            float uz = b.Z - a.Z;
            float vx = c.X - a.X;
            float vy = c.Y - a.Y;
            float vz = c.Z - a.Z;
            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;
            float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length <= 1e-7f || MathF.Abs(ny) <= 0.08f)
            {
                return false;
            }

            nx /= length;
            ny /= length;
            nz /= length;
            triangle = new TerrainTriangle(
                a.X,
                a.Y,
                a.Z,
                b.X,
                b.Y,
                b.Z,
                c.X,
                c.Y,
                c.Z,
                nx,
                ny,
                nz,
                MathF.Min(a.X, MathF.Min(b.X, c.X)),
                MathF.Max(a.X, MathF.Max(b.X, c.X)),
                MathF.Min(a.Z, MathF.Min(b.Z, c.Z)),
                MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
            return true;
        }

        public bool ContainsXZ(float x, float z, out float y)
        {
            y = 0.0f;
            if (x < MinX - 1e-5f || x > MaxX + 1e-5f || z < MinZ - 1e-5f || z > MaxZ + 1e-5f)
            {
                return false;
            }

            float v0x = Bx - Ax;
            float v0z = Bz - Az;
            float v1x = Cx - Ax;
            float v1z = Cz - Az;
            float v2x = x - Ax;
            float v2z = z - Az;
            float denominator = v0x * v1z - v1x * v0z;
            if (MathF.Abs(denominator) <= 1e-7f)
            {
                return false;
            }

            float inv = 1.0f / denominator;
            float u = (v2x * v1z - v1x * v2z) * inv;
            float v = (v0x * v2z - v2x * v0z) * inv;
            float w = 1.0f - u - v;
            const float tolerance = -1e-4f;
            if (u < tolerance || v < tolerance || w < tolerance)
            {
                return false;
            }

            y = Ay * w + By * u + Cy * v;
            return true;
        }
    }
}

public readonly record struct TerrainCacheHeightSample(
    float ModelY,
    float NormalX,
    float NormalY,
    float NormalZ);
