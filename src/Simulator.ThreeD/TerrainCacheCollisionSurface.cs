using System.Numerics;
using LoadLargeTerrain;
using Simulator.Assets;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal readonly record struct TerrainSurfaceRayHit(
    double WorldX,
    double WorldY,
    double HeightM,
    Vector3 Normal,
    double SegmentT,
    string Kind);

internal readonly record struct TerrainSurfaceSample(float HeightM, Vector3 Normal);
internal readonly record struct TerrainCollisionDebugTriangle(
    Vector3 A,
    Vector3 B,
    Vector3 C,
    Vector3 Normal,
    bool Walkable);

internal sealed class TerrainCacheCollisionSurface
{
    private const float CollisionInflationM = 0.01f;
    private const float MinProjectedArea = 1e-6f;
    private const float MinWalkableSampleNormalY = 0.35f;
    private const float MinSupportSampleNormalY = 0.02f;
    private const float HeightSampleBarycentricTolerance = -0.0025f;
    private const float MaxTraversalWallNormalY = 0.18f;
    private const float LegacyMechanismTraversalPaddingM = 0.12f;
    private readonly List<TerrainTriangle> _triangles;
    private readonly List<int>?[] _cells;
    private readonly List<int>?[] _walkableCells;
    private readonly float[] _wallCellMinHeightM;
    private readonly float[] _wallCellMaxHeightM;
    private readonly float[] _cellMinHeightM;
    private readonly float[] _cellMaxHeightM;
    private readonly float _cellWorld;
    private readonly int _columns;
    private readonly int _rows;
    private int[]? _raycastVisitMarks;
    private int _raycastVisitGeneration;

    private TerrainCacheCollisionSurface(
        List<TerrainTriangle> triangles,
        List<int>?[] cells,
        List<int>?[] walkableCells,
        float[] wallCellMinHeightM,
        float[] wallCellMaxHeightM,
        float[] cellMinHeightM,
        float[] cellMaxHeightM,
        float widthWorld,
        float heightWorld,
        float cellWorld,
        int columns,
        int rows)
    {
        _triangles = triangles;
        _cells = cells;
        _walkableCells = walkableCells;
        _wallCellMinHeightM = wallCellMinHeightM;
        _wallCellMaxHeightM = wallCellMaxHeightM;
        _cellMinHeightM = cellMinHeightM;
        _cellMaxHeightM = cellMaxHeightM;
        WidthWorld = widthWorld;
        HeightWorld = heightWorld;
        _cellWorld = cellWorld;
        _columns = columns;
        _rows = rows;
    }

    public float WidthWorld { get; }

    public float HeightWorld { get; }

    public int TriangleCount => _triangles.Count;

    public static TerrainCacheCollisionSurface Load(MapPresetDefinition preset, string sourcePath)
    {
        float widthWorld = Math.Max(1f, preset.Width);
        float heightWorld = Math.Max(1f, preset.Height);
        float fieldLengthM = Math.Max(1f, (float)preset.FieldLengthM);
        float fieldWidthM = Math.Max(1f, (float)preset.FieldWidthM);
        float metersPerWorldUnit = ResolveMetersPerWorldUnit(preset);
        // The refined terrain cache is the source of truth for visible arena
        // geometry. Do not carve out legacy mechanism areas here; otherwise
        // the renderer still shows the mesh while movement can clip through it.
        LegacyMechanismExclusion[] legacyMechanismExclusions = Array.Empty<LegacyMechanismExclusion>();
        float cellWorld = Math.Clamp(0.12f / Math.Max(1e-6f, metersPerWorldUnit), 2.0f, 12.0f);
        int columns = Math.Max(1, (int)MathF.Ceiling(widthWorld / cellWorld));
        int rows = Math.Max(1, (int)MathF.Ceiling(heightWorld / cellWorld));
        var triangles = new List<TerrainTriangle>(11_000_000);
        var cells = new List<int>?[checked(columns * rows)];
        var walkableCells = new List<int>?[checked(columns * rows)];
        var wallCells = new List<int>?[checked(columns * rows)];
        var wallCellMinHeightM = new float[cells.Length];
        var wallCellMaxHeightM = new float[cells.Length];
        var cellMinHeightM = new float[cells.Length];
        var cellMaxHeightM = new float[cells.Length];
        Array.Fill(wallCellMinHeightM, float.PositiveInfinity);
        Array.Fill(wallCellMaxHeightM, float.NegativeInfinity);
        Array.Fill(cellMinHeightM, float.PositiveInfinity);
        Array.Fill(cellMaxHeightM, float.NegativeInfinity);
        var reader = new TerrainCacheMeshReader();
        HashSet<int> excludedComponentIds = TerrainCacheActorComponentFilter.LoadExcludedComponentIds(preset);
        RuntimeReferenceScene? referenceScene = TryLoadRuntimeReferenceScene(sourcePath, preset.AnnotationPath);
        FineTerrainWorldScale? annotationWorldScale = TryLoadAnnotationWorldScale(preset.AnnotationPath);
        reader.Load(sourcePath, (catalog, _, vertices, indices, componentRanges) =>
        {
            if (vertices.Length == 0 || indices.Length < 3)
            {
                return;
            }

            float meterScaleX = fieldLengthM / Math.Max(1e-6f, catalog.MaxX - catalog.MinX);
            float meterScaleZ = fieldWidthM / Math.Max(1e-6f, catalog.MaxZ - catalog.MinZ);
            float verticalScale = 0.5f * (meterScaleX + meterScaleZ);
            float worldScaleX = widthWorld / Math.Max(1e-6f, catalog.MaxX - catalog.MinX);
            float worldScaleZ = heightWorld / Math.Max(1e-6f, catalog.MaxZ - catalog.MinZ);
            float modelCenterX = (catalog.MinX + catalog.MaxX) * 0.5f;
            float modelCenterZ = (catalog.MinZ + catalog.MaxZ) * 0.5f;
            float heightOffset = catalog.MinY;
            float originWorldX = fieldLengthM * 0.5f / Math.Max(1e-6f, metersPerWorldUnit);
            float originWorldZ = fieldWidthM * 0.5f / Math.Max(1e-6f, metersPerWorldUnit);
            if (referenceScene is not null)
            {
                worldScaleX = referenceScene.WorldScale.XMetersPerUnit / Math.Max(1e-6f, metersPerWorldUnit);
                worldScaleZ = referenceScene.WorldScale.ZMetersPerUnit / Math.Max(1e-6f, metersPerWorldUnit);
                verticalScale = referenceScene.WorldScale.YMetersPerUnit;
                modelCenterX = referenceScene.WorldScale.ModelCenter.X;
                modelCenterZ = referenceScene.WorldScale.ModelCenter.Z;
                heightOffset = referenceScene.Bounds.Min.Y;
            }
            else if (annotationWorldScale is FineTerrainWorldScale annotationScale)
            {
                worldScaleX = annotationScale.XMetersPerModelUnit / Math.Max(1e-6f, metersPerWorldUnit);
                worldScaleZ = annotationScale.ZMetersPerModelUnit / Math.Max(1e-6f, metersPerWorldUnit);
                verticalScale = annotationScale.YMetersPerModelUnit;
                modelCenterX = annotationScale.ModelCenter.X;
                modelCenterZ = annotationScale.ModelCenter.Z;
                heightOffset = annotationScale.ModelMinY;
                originWorldX = annotationScale.MapLengthXMeters * 0.5f / Math.Max(1e-6f, metersPerWorldUnit);
                originWorldZ = annotationScale.MapLengthZMeters * 0.5f / Math.Max(1e-6f, metersPerWorldUnit);
            }
            Vector3[] converted = new Vector3[vertices.Length];
            for (int index = 0; index < vertices.Length; index++)
            {
                TerrainCacheVertex vertex = vertices[index];
                converted[index] = new Vector3(
                    originWorldX - (vertex.X - modelCenterX) * worldScaleX,
                    Math.Max(0f, (vertex.Y - heightOffset) * verticalScale),
                    originWorldZ - (vertex.Z - modelCenterZ) * worldScaleZ);
            }

            int triangleIndexCount = indices.Length - indices.Length % 3;
            if (componentRanges.Count > 0 && excludedComponentIds.Count > 0)
            {
                foreach (TerrainCacheComponentRange range in componentRanges)
                {
                    if (excludedComponentIds.Contains(range.ComponentId))
                    {
                        continue;
                    }

                    int rangeStart = Math.Clamp(range.StartIndex, 0, triangleIndexCount);
                    int rangeEnd = Math.Clamp(range.StartIndex + Math.Max(0, range.IndexCount), rangeStart, triangleIndexCount);
                    rangeEnd -= (rangeEnd - rangeStart) % 3;
                    AppendCollisionTriangles(rangeStart, rangeEnd);
                }

                return;
            }

            AppendCollisionTriangles(0, triangleIndexCount);

            void AppendCollisionTriangles(int startIndex, int endIndex)
            {
                for (int triangleIndex = startIndex; triangleIndex < endIndex; triangleIndex += 3)
                {
                    int i0 = indices[triangleIndex];
                    int i1 = indices[triangleIndex + 1];
                    int i2 = indices[triangleIndex + 2];
                    if ((uint)i0 >= (uint)converted.Length
                        || (uint)i1 >= (uint)converted.Length
                        || (uint)i2 >= (uint)converted.Length)
                    {
                        continue;
                    }

                    AddConvertedTriangle(converted[i0], converted[i1], converted[i2]);
                }
            }

            void AddConvertedTriangle(Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() <= 1e-16f)
                {
                    return;
                }

                if (ShouldExcludeLegacyMechanismTriangle(a, b, c, legacyMechanismExclusions))
                {
                    return;
                }

                Vector3 normal = Vector3.Normalize(cross);
                if (normal.Y < 0f)
                {
                    normal = -normal;
                }

                AddShellTriangle(a, b, c, normal);
            }

            void AddShellTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
            {
                int triangleId = triangles.Count;
                triangles.Add(new TerrainTriangle(a, b, c, normal));
                AddTriangleToCells(
                    cells,
                    columns,
                    rows,
                    cellWorld,
                    triangleId,
                    a,
                    b,
                    c,
                    metersPerWorldUnit,
                    cellMinHeightM,
                    cellMaxHeightM);
                if (normal.Y >= MinWalkableSampleNormalY)
                {
                    AddTriangleToCells(walkableCells, columns, rows, cellWorld, triangleId, a, b, c, metersPerWorldUnit);
                }
                else if (normal.Y <= MaxTraversalWallNormalY)
                {
                    AddTriangleToCells(wallCells, columns, rows, cellWorld, triangleId, a, b, c, metersPerWorldUnit, wallCellMinHeightM, wallCellMaxHeightM);
                }
            }
        });

        return new TerrainCacheCollisionSurface(
            triangles,
            cells,
            walkableCells,
            wallCellMinHeightM,
            wallCellMaxHeightM,
            cellMinHeightM,
            cellMaxHeightM,
            widthWorld,
            heightWorld,
            cellWorld,
            columns,
            rows);
    }

    public bool Contains(double worldX, double worldY)
    {
        return worldX >= 0.0
            && worldY >= 0.0
            && worldX < WidthWorld
            && worldY < HeightWorld;
    }

    internal int CollectDebugTriangles(
        double centerWorldX,
        double centerWorldY,
        double radiusWorld,
        List<TerrainCollisionDebugTriangle> destination,
        int maxTriangles = 2048)
    {
        destination.Clear();
        if (_triangles.Count == 0 || maxTriangles <= 0 || radiusWorld <= 0.0)
        {
            return 0;
        }

        float minX = (float)Math.Max(0.0, centerWorldX - radiusWorld);
        float maxX = (float)Math.Min(WidthWorld, centerWorldX + radiusWorld);
        float minY = (float)Math.Max(0.0, centerWorldY - radiusWorld);
        float maxY = (float)Math.Min(HeightWorld, centerWorldY + radiusWorld);
        int minColumn = WorldToColumn(minX);
        int maxColumn = WorldToColumn(MathF.Max(minX, maxX - 1e-4f));
        int minRow = WorldToRow(minY);
        int maxRow = WorldToRow(MathF.Max(minY, maxY - 1e-4f));
        HashSet<int> visited = new(Math.Min(maxTriangles * 2, 4096));

        for (int row = minRow; row <= maxRow; row++)
        {
            for (int column = minColumn; column <= maxColumn; column++)
            {
                List<int>? list = _cells[row * _columns + column];
                if (list is null)
                {
                    continue;
                }

                foreach (int triangleId in list)
                {
                    if (!visited.Add(triangleId))
                    {
                        continue;
                    }

                    TerrainTriangle triangle = _triangles[triangleId];
                    float triangleMinX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
                    float triangleMaxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
                    float triangleMinY = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z));
                    float triangleMaxY = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z));
                    if (triangleMaxX < minX
                        || triangleMinX > maxX
                        || triangleMaxY < minY
                        || triangleMinY > maxY)
                    {
                        continue;
                    }

                    destination.Add(new TerrainCollisionDebugTriangle(
                        triangle.A,
                        triangle.B,
                        triangle.C,
                        triangle.Normal,
                        triangle.Normal.Y >= MinWalkableSampleNormalY));
                    if (destination.Count >= maxTriangles)
                    {
                        return destination.Count;
                    }
                }
            }
        }

        return destination.Count;
    }

    public bool TrySampleHeight(double worldX, double worldY, out TerrainSurfaceSample sample, int maxCellRadius = 1)
    {
        sample = default;
        if (!Contains(worldX, worldY))
        {
            return false;
        }

        float x = (float)worldX;
        float y = (float)worldY;
        int centerColumn = WorldToColumn(x);
        int centerRow = WorldToRow(y);
        int safeMaxRadius = Math.Clamp(maxCellRadius, 0, 2);
        if (TrySampleBestHeight(worldX, worldY, safeMaxRadius, walkableOnly: true, out sample))
        {
            return true;
        }

        // Some refined-map top faces are slightly steeper than the traversal
        // threshold or fall exactly on triangle seams. For support height we
        // still want the highest upward-facing surface under the chassis.
        return TrySampleBestHeight(worldX, worldY, safeMaxRadius, walkableOnly: false, out sample);
    }

    public bool TrySampleHeightInBand(
        double worldX,
        double worldY,
        double minHeightM,
        double maxHeightM,
        out TerrainSurfaceSample sample,
        int maxCellRadius = 1)
    {
        sample = default;
        if (!Contains(worldX, worldY))
        {
            return false;
        }

        float minHeight = (float)Math.Min(minHeightM, maxHeightM);
        float maxHeight = (float)Math.Max(minHeightM, maxHeightM);
        int safeMaxRadius = Math.Clamp(maxCellRadius, 0, 2);
        if (TrySampleBestHeightInBand(worldX, worldY, safeMaxRadius, walkableOnly: true, minHeight, maxHeight, out sample))
        {
            return true;
        }

        return TrySampleBestHeightInBand(worldX, worldY, safeMaxRadius, walkableOnly: false, minHeight, maxHeight, out sample);
    }

    private bool TrySampleBestHeight(double worldX, double worldY, int maxCellRadius, bool walkableOnly, out TerrainSurfaceSample sample)
    {
        sample = default;
        float x = (float)worldX;
        float y = (float)worldY;
        int centerColumn = WorldToColumn(x);
        int centerRow = WorldToRow(y);
        float bestHeight = float.NegativeInfinity;
        Vector3 bestNormal = Vector3.UnitY;
        bool found = false;
        for (int radius = 0; radius <= maxCellRadius && !found; radius++)
        {
            int minColumn = Math.Max(0, centerColumn - radius);
            int maxColumn = Math.Min(_columns - 1, centerColumn + radius);
            int minRow = Math.Max(0, centerRow - radius);
            int maxRow = Math.Min(_rows - 1, centerRow + radius);
            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    List<int>? list = walkableOnly
                        ? _walkableCells[row * _columns + column]
                        : _cells[row * _columns + column];
                    if (list is null)
                    {
                        continue;
                    }

                    foreach (int triangleId in list)
                    {
                        TerrainTriangle triangle = _triangles[triangleId];
                        if ((!walkableOnly && triangle.Normal.Y < MinSupportSampleNormalY)
                            || !triangle.TrySampleHeight(x, y, out float height))
                        {
                            continue;
                        }

                        if (height > bestHeight)
                        {
                            bestHeight = height;
                            bestNormal = triangle.Normal;
                            found = true;
                        }
                    }
                }
            }
        }

        if (!found)
        {
            return false;
        }

        sample = new TerrainSurfaceSample(bestHeight, bestNormal);
        return true;
    }

    private bool TrySampleBestHeightInBand(
        double worldX,
        double worldY,
        int maxCellRadius,
        bool walkableOnly,
        float minHeightM,
        float maxHeightM,
        out TerrainSurfaceSample sample)
    {
        sample = default;
        float x = (float)worldX;
        float y = (float)worldY;
        int centerColumn = WorldToColumn(x);
        int centerRow = WorldToRow(y);
        float bestHeight = float.NegativeInfinity;
        Vector3 bestNormal = Vector3.UnitY;
        bool found = false;
        for (int radius = 0; radius <= maxCellRadius && !found; radius++)
        {
            int minColumn = Math.Max(0, centerColumn - radius);
            int maxColumn = Math.Min(_columns - 1, centerColumn + radius);
            int minRow = Math.Max(0, centerRow - radius);
            int maxRow = Math.Min(_rows - 1, centerRow + radius);
            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    List<int>? list = walkableOnly
                        ? _walkableCells[row * _columns + column]
                        : _cells[row * _columns + column];
                    if (list is null)
                    {
                        continue;
                    }

                    foreach (int triangleId in list)
                    {
                        TerrainTriangle triangle = _triangles[triangleId];
                        if ((!walkableOnly && triangle.Normal.Y < MinSupportSampleNormalY)
                            || !triangle.TrySampleHeight(x, y, out float height))
                        {
                            continue;
                        }

                        if (height < minHeightM - 0.04f || height > maxHeightM + 0.04f)
                        {
                            continue;
                        }

                        if (height > bestHeight)
                        {
                            bestHeight = height;
                            bestNormal = triangle.Normal;
                            found = true;
                        }
                    }
                }
            }
        }

        if (!found)
        {
            return false;
        }

        sample = new TerrainSurfaceSample(bestHeight, bestNormal);
        return true;
    }

    public bool IsMovementBlocked(double worldX, double worldY, double referenceHeightM, double allowedRiseM)
    {
        if (!Contains(worldX, worldY))
        {
            return true;
        }

        if (TrySampleHeight(worldX, worldY, out TerrainSurfaceSample heightSample, maxCellRadius: 0)
            && heightSample.HeightM - referenceHeightM > allowedRiseM + 1e-6)
        {
            return true;
        }

        // Vertical risers in the fine mesh are visual/collision faces for
        // bullets, but robot traversal should be decided by reachable top
        // surface height. Treating every riser edge as a wall makes stairs
        // impossible to climb.
        return false;
    }

    public bool HasWallContact(double worldX, double worldY, double minHeightM, double maxHeightM, int maxCellRadius = 0)
    {
        if (!Contains(worldX, worldY))
        {
            return true;
        }

        int safeRadius = Math.Clamp(maxCellRadius, 0, 2);
        int supportRadius = Math.Max(1, safeRadius);
        if (TrySampleHeightInBand(
                worldX,
                worldY,
                minHeightM - 0.14,
                maxHeightM + 0.16,
                out _,
                maxCellRadius: supportRadius))
        {
            return false;
        }

        if (TrySampleHeight(worldX, worldY, out TerrainSurfaceSample walkableSample, maxCellRadius: supportRadius)
            && walkableSample.HeightM >= minHeightM - 0.14
            && walkableSample.HeightM <= maxHeightM + 0.16)
        {
            return false;
        }

        int centerColumn = WorldToColumn((float)worldX);
        int centerRow = WorldToRow((float)worldY);
        float minHeight = (float)Math.Min(minHeightM, maxHeightM);
        float maxHeight = (float)Math.Max(minHeightM, maxHeightM);
        for (int radius = 0; radius <= safeRadius; radius++)
        {
            int minColumn = Math.Max(0, centerColumn - radius);
            int maxColumn = Math.Min(_columns - 1, centerColumn + radius);
            int minRow = Math.Max(0, centerRow - radius);
            int maxRow = Math.Min(_rows - 1, centerRow + radius);
            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int cellIndex = row * _columns + column;
                    float cellMin = _wallCellMinHeightM[cellIndex];
                    if (!float.IsFinite(cellMin))
                    {
                        continue;
                    }

                    float cellMax = _wallCellMaxHeightM[cellIndex];
                    if (cellMax < minHeight - 0.05f || cellMin > maxHeight + 0.08f)
                    {
                        continue;
                    }

                    return true;
                }
            }
        }

        return false;
    }

    public bool TryRaycast(
        Vector3 startM,
        Vector3 endM,
        double metersPerWorldUnit,
        double projectileRadiusM,
        out TerrainSurfaceRayHit hit)
    {
        hit = default;
        double safeScale = Math.Max(1e-6, metersPerWorldUnit);
        Vector3 segment = endM - startM;
        float distanceM = segment.Length();
        if (distanceM <= 1e-5f)
        {
            return false;
        }

        if (TryRaycastWalkableHeight(startM, segment, distanceM, safeScale, projectileRadiusM, out hit))
        {
            return true;
        }

        // 17 mm streams dominate projectile count. They use the walkable
        // height surface above, while large projectiles keep exact triangle
        // mesh checks for risers and sharp edges.
        if (projectileRadiusM < 0.015)
        {
            return false;
        }

        int samples = Math.Clamp((int)MathF.Ceiling(distanceM / Math.Max(0.04f, _cellWorld * (float)safeScale * 0.55f)), 4, 768);
        int[] visitMarks = EnsureRaycastVisitMarks();
        int visitGeneration = NextRaycastVisitGeneration(visitMarks);
        float bestT = float.PositiveInfinity;
        Vector3 bestNormal = Vector3.UnitY;
        float clearanceM = Math.Max(0.012f, (float)projectileRadiusM);
        for (int index = 0; index <= samples; index++)
        {
            float t = index / (float)samples;
            Vector3 sampleM = startM + segment * t;
            float sampleWorldX = (float)(sampleM.X / safeScale);
            float sampleWorldY = (float)(sampleM.Z / safeScale);
            if (!Contains(sampleWorldX, sampleWorldY))
            {
                continue;
            }

            int column = WorldToColumn(sampleWorldX);
            int row = WorldToRow(sampleWorldY);
            int radius = Math.Max(0, (int)MathF.Ceiling((float)projectileRadiusM / Math.Max(1e-6f, _cellWorld * (float)safeScale)));
            for (int cy = Math.Max(0, row - radius); cy <= Math.Min(_rows - 1, row + radius); cy++)
            {
                for (int cx = Math.Max(0, column - radius); cx <= Math.Min(_columns - 1, column + radius); cx++)
                {
                    int cellIndex = cy * _columns + cx;
                    if (!CellHeightMayIntersect(cellIndex, sampleM.Y, clearanceM))
                    {
                        continue;
                    }

                    List<int>? list = _cells[cellIndex];
                    if (list is null)
                    {
                        continue;
                    }

                    foreach (int triangleId in list)
                    {
                        if (visitMarks[triangleId] == visitGeneration)
                        {
                            continue;
                        }

                        visitMarks[triangleId] = visitGeneration;
                        TerrainTriangle triangle = _triangles[triangleId];
                        if (!triangle.TryIntersectMetric(startM, segment, (float)safeScale, out float hitT, out Vector3 normal)
                            || hitT <= 0.0015f
                            || hitT >= 0.9985f
                            || hitT >= bestT)
                        {
                            continue;
                        }

                        bestT = hitT;
                        bestNormal = normal;
                    }
                }
            }
        }

        if (!float.IsFinite(bestT))
        {
            return false;
        }

        Vector3 point = startM + segment * bestT;
        hit = new TerrainSurfaceRayHit(
            point.X / safeScale,
            point.Z / safeScale,
            point.Y,
            bestNormal,
            bestT,
            "terrain_mesh");
        return true;
    }

    private bool TryRaycastWalkableHeight(
        Vector3 startM,
        Vector3 segmentM,
        float distanceM,
        double metersPerWorldUnit,
        double projectileRadiusM,
        out TerrainSurfaceRayHit hit)
    {
        hit = default;
        double safeScale = Math.Max(1e-6, metersPerWorldUnit);
        float clearanceM = Math.Max(0.010f, (float)projectileRadiusM * 0.85f);
        int samples = Math.Clamp((int)MathF.Ceiling(distanceM / 0.055f), 4, 96);
        float previousT = 0f;
        float previousGap = float.PositiveInfinity;
        TerrainSurfaceSample previousSurface = default;
        bool hasPrevious = false;
        for (int index = 1; index <= samples; index++)
        {
            float t = index / (float)samples;
            Vector3 point = startM + segmentM * t;
            double worldX = point.X / safeScale;
            double worldY = point.Z / safeScale;
            if (!TrySampleHeight(worldX, worldY, out TerrainSurfaceSample surface))
            {
                hasPrevious = false;
                continue;
            }

            float gap = point.Y - surface.HeightM - clearanceM;
            if (gap <= 0f)
            {
                float hitT = t;
                if (hasPrevious && previousGap > 0f)
                {
                    float denom = previousGap - gap;
                    float lerp = Math.Abs(denom) <= 1e-6f ? 0f : previousGap / denom;
                    hitT = previousT + (t - previousT) * Math.Clamp(lerp, 0f, 1f);
                }

                Vector3 hitPoint = startM + segmentM * hitT;
                Vector3 normal = surface.Normal.LengthSquared() > 1e-8f
                    ? Vector3.Normalize(surface.Normal)
                    : Vector3.UnitY;
                hit = new TerrainSurfaceRayHit(
                    hitPoint.X / safeScale,
                    hitPoint.Z / safeScale,
                    surface.HeightM,
                    normal,
                    hitT,
                    "terrain_height");
                return hitT > 0.0015f && hitT < 0.9985f;
            }

            previousT = t;
            previousGap = gap;
            previousSurface = surface;
            hasPrevious = true;
        }

        _ = previousSurface;
        return false;
    }

    private bool CellHeightMayIntersect(int cellIndex, float heightM, float clearanceM)
    {
        float minHeight = _cellMinHeightM[cellIndex];
        if (!float.IsFinite(minHeight))
        {
            return false;
        }

        return heightM >= minHeight - clearanceM
            && heightM <= _cellMaxHeightM[cellIndex] + clearanceM;
    }

    private int[] EnsureRaycastVisitMarks()
    {
        if (_raycastVisitMarks is null || _raycastVisitMarks.Length != _triangles.Count)
        {
            _raycastVisitMarks = new int[_triangles.Count];
            _raycastVisitGeneration = 0;
        }

        return _raycastVisitMarks;
    }

    private int NextRaycastVisitGeneration(int[] visitMarks)
    {
        _raycastVisitGeneration++;
        if (_raycastVisitGeneration != int.MaxValue)
        {
            return _raycastVisitGeneration;
        }

        Array.Clear(visitMarks);
        _raycastVisitGeneration = 1;
        return _raycastVisitGeneration;
    }

    private static void AddTriangleToCells(
        List<int>?[] cells,
        int columns,
        int rows,
        float cellWorld,
        int triangleId,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        float metersPerWorldUnit,
        float[]? cellMinHeightM = null,
        float[]? cellMaxHeightM = null)
    {
        float expansion = Math.Clamp(0.035f / Math.Max(1e-6f, metersPerWorldUnit), 0.75f, cellWorld * 0.65f);
        float minX = MathF.Min(a.X, MathF.Min(b.X, c.X)) - expansion;
        float maxX = MathF.Max(a.X, MathF.Max(b.X, c.X)) + expansion;
        float minY = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - expansion;
        float maxY = MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) + expansion;
        int minColumn = Math.Clamp((int)MathF.Floor(minX / cellWorld), 0, columns - 1);
        int maxColumn = Math.Clamp((int)MathF.Floor(maxX / cellWorld), 0, columns - 1);
        int minRow = Math.Clamp((int)MathF.Floor(minY / cellWorld), 0, rows - 1);
        int maxRow = Math.Clamp((int)MathF.Floor(maxY / cellWorld), 0, rows - 1);
        float triangleMinHeight = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
        float triangleMaxHeight = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        for (int row = minRow; row <= maxRow; row++)
        {
            for (int column = minColumn; column <= maxColumn; column++)
            {
                int cellIndex = row * columns + column;
                cells[cellIndex] ??= new List<int>(8);
                cells[cellIndex]!.Add(triangleId);
                if (cellMinHeightM is not null && cellMaxHeightM is not null)
                {
                    cellMinHeightM[cellIndex] = MathF.Min(cellMinHeightM[cellIndex], triangleMinHeight);
                    cellMaxHeightM[cellIndex] = MathF.Max(cellMaxHeightM[cellIndex], triangleMaxHeight);
                }
            }
        }
    }

    private static float ResolveMetersPerWorldUnit(MapPresetDefinition preset)
    {
        float scaleX = preset.FieldLengthM > 0 && preset.Width > 0
            ? (float)(preset.FieldLengthM / preset.Width)
            : 0f;
        float scaleY = preset.FieldWidthM > 0 && preset.Height > 0
            ? (float)(preset.FieldWidthM / preset.Height)
            : 0f;
        if (scaleX > 0f && scaleY > 0f)
        {
            return (scaleX + scaleY) * 0.5f;
        }

        return scaleX > 0f ? scaleX : scaleY > 0f ? scaleY : 0.0178f;
    }

    private static LegacyMechanismExclusion[] BuildLegacyMechanismExclusions(MapPresetDefinition preset, float metersPerWorldUnit)
    {
        if (preset.Facilities.Count == 0)
        {
            return Array.Empty<LegacyMechanismExclusion>();
        }

        float paddingWorld = LegacyMechanismTraversalPaddingM / Math.Max(1e-6f, metersPerWorldUnit);
        var exclusions = new List<LegacyMechanismExclusion>(8);
        foreach (FacilityRegion facility in preset.Facilities)
        {
            if (!IsLegacyMechanismFacility(facility))
            {
                continue;
            }

            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            if (facility.Points.Count > 0)
            {
                foreach (Point2D point in facility.Points)
                {
                    minX = MathF.Min(minX, (float)point.X);
                    maxX = MathF.Max(maxX, (float)point.X);
                    minY = MathF.Min(minY, (float)point.Y);
                    maxY = MathF.Max(maxY, (float)point.Y);
                }
            }
            else
            {
                minX = (float)Math.Min(facility.X1, facility.X2);
                maxX = (float)Math.Max(facility.X1, facility.X2);
                minY = (float)Math.Min(facility.Y1, facility.Y2);
                maxY = (float)Math.Max(facility.Y1, facility.Y2);
            }

            if (!float.IsFinite(minX) || !float.IsFinite(minY))
            {
                continue;
            }

            exclusions.Add(new LegacyMechanismExclusion(
                minX - paddingWorld,
                maxX + paddingWorld,
                minY - paddingWorld,
                maxY + paddingWorld));
        }

        return exclusions.ToArray();
    }

    private static bool ShouldExcludeLegacyMechanismTriangle(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        LegacyMechanismExclusion[] exclusions)
    {
        if (exclusions.Length == 0)
        {
            return false;
        }

        float minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        float maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        float minY = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
        float maxY = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
        float centroidX = (a.X + b.X + c.X) / 3f;
        float centroidY = (a.Z + b.Z + c.Z) / 3f;
        foreach (LegacyMechanismExclusion exclusion in exclusions)
        {
            if (maxX < exclusion.MinX || minX > exclusion.MaxX || maxY < exclusion.MinY || minY > exclusion.MaxY)
            {
                continue;
            }

            if (exclusion.Contains(a.X, a.Z)
                || exclusion.Contains(b.X, b.Z)
                || exclusion.Contains(c.X, c.Z)
                || exclusion.Contains(centroidX, centroidY))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLegacyMechanismFacility(FacilityRegion facility)
    {
        return facility.Type.Equals("base", StringComparison.OrdinalIgnoreCase)
            || facility.Type.Equals("outpost", StringComparison.OrdinalIgnoreCase)
            || facility.Type.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeReferenceScene? TryLoadRuntimeReferenceScene(string sourcePath, string? annotationPath)
    {
        try
        {
            return RuntimeReferenceLoader.Load(sourcePath, annotationPath);
        }
        catch
        {
            return null;
        }
    }

    private static FineTerrainWorldScale? TryLoadAnnotationWorldScale(string? annotationPath)
    {
        try
        {
            return string.IsNullOrWhiteSpace(annotationPath)
                ? null
                : FineTerrainAnnotationDocument.TryLoad(annotationPath)?.WorldScale;
        }
        catch
        {
            return null;
        }
    }

    private int WorldToColumn(float worldX)
        => Math.Clamp((int)MathF.Floor(worldX / _cellWorld), 0, _columns - 1);

    private int WorldToRow(float worldY)
        => Math.Clamp((int)MathF.Floor(worldY / _cellWorld), 0, _rows - 1);

    private readonly struct TerrainTriangle
    {
        public TerrainTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        {
            A = a;
            B = b;
            C = c;
            Normal = normal;
        }

        public readonly Vector3 A;
        public readonly Vector3 B;
        public readonly Vector3 C;
        public readonly Vector3 Normal;

        public bool TrySampleHeight(float x, float y, out float height)
        {
            height = 0f;
            float det = (B.Z - C.Z) * (A.X - C.X) + (C.X - B.X) * (A.Z - C.Z);
            if (Math.Abs(det) <= MinProjectedArea)
            {
                return false;
            }

            float w1 = ((B.Z - C.Z) * (x - C.X) + (C.X - B.X) * (y - C.Z)) / det;
            float w2 = ((C.Z - A.Z) * (x - C.X) + (A.X - C.X) * (y - C.Z)) / det;
            float w3 = 1f - w1 - w2;
            if (w1 < HeightSampleBarycentricTolerance
                || w2 < HeightSampleBarycentricTolerance
                || w3 < HeightSampleBarycentricTolerance)
            {
                return false;
            }

            height = A.Y * w1 + B.Y * w2 + C.Y * w3;
            return true;
        }

        public bool TryIntersectMetric(Vector3 startM, Vector3 directionM, float metersPerWorldUnit, out float t, out Vector3 normal)
        {
            t = 0f;
            Vector3 a = new(A.X * metersPerWorldUnit, A.Y, A.Z * metersPerWorldUnit);
            Vector3 b = new(B.X * metersPerWorldUnit, B.Y, B.Z * metersPerWorldUnit);
            Vector3 c = new(C.X * metersPerWorldUnit, C.Y, C.Z * metersPerWorldUnit);
            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            Vector3 p = Vector3.Cross(directionM, edge2);
            float det = Vector3.Dot(edge1, p);
            if (Math.Abs(det) <= 1e-7f)
            {
                normal = Vector3.UnitY;
                return false;
            }

            float invDet = 1f / det;
            Vector3 s = startM - a;
            float u = Vector3.Dot(s, p) * invDet;
            if (u < -1e-4f || u > 1.0001f)
            {
                normal = Vector3.UnitY;
                return false;
            }

            Vector3 q = Vector3.Cross(s, edge1);
            float v = Vector3.Dot(directionM, q) * invDet;
            if (v < -1e-4f || u + v > 1.0001f)
            {
                normal = Vector3.UnitY;
                return false;
            }

            t = Vector3.Dot(edge2, q) * invDet;
            if (t < 0f || t > 1f)
            {
                normal = Vector3.UnitY;
                return false;
            }

            normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
            if (Vector3.Dot(normal, directionM) > 0f)
            {
                normal = -normal;
            }

            return true;
        }

    }

    private readonly record struct LegacyMechanismExclusion(float MinX, float MaxX, float MinY, float MaxY)
    {
        public bool Contains(float x, float y)
            => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
    }
}
