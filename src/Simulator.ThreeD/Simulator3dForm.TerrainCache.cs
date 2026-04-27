using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Simulator.Assets;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const int MaxTerrainTopMergeSpan = 36;
    private const int MaxTerrainWallMergeSpan = 256;
    private const float TerrainSlopeSmoothThresholdM = 0.048f;

    private enum TerrainWallEdge
    {
        North,
        South,
        West,
        East,
    }

    private readonly record struct TerrainTileSeed(
        int StartCellX,
        int EndCellX,
        int StartCellY,
        int EndCellY,
        float HeightM,
        int MaterialKey,
        Color FillColor,
        Color EdgeColor);

    private bool TryRebuildTerrainCacheTriangleMesh(List<TerrainFacePatch> target)
    {
        if (!TryResolveTerrainCacheGpuRenderSource(out string sourcePath))
        {
            return false;
        }

        target.Clear();
        SetTerrainCacheGpuRenderSource(sourcePath);
        AppendGameplayLog(
            "terrain_cache_render.log",
            $"{DateTime.Now:HH:mm:ss.fff} source={Path.GetFileName(sourcePath)} original_triangle_gpu_stream=enabled");
        return true;
    }

    private bool TryResolveTerrainCacheGpuRenderSource(out string sourcePath)
    {
        sourcePath = string.Empty;
        RuntimeGridDefinition? runtime = _host.MapPreset.RuntimeGrid;
        if (runtime is null
            || !string.Equals(runtime.StorageKind, "terrain_cache_lz4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? mapDirectory = Path.GetDirectoryName(_host.MapPreset.SourcePath);
        if (string.IsNullOrWhiteSpace(mapDirectory))
        {
            return false;
        }

        sourcePath = Path.IsPathRooted(runtime.SourcePath)
            ? runtime.SourcePath
            : Path.GetFullPath(Path.Combine(mapDirectory, runtime.SourcePath));
        if (EnsureTerrainCacheLz4Available(sourcePath))
        {
            return true;
        }

        foreach (string fallback in EnumerateTerrainCacheFallbackPaths(mapDirectory, sourcePath))
        {
            if (EnsureTerrainCacheLz4Available(fallback))
            {
                sourcePath = fallback;
                return true;
            }
        }

        return false;
    }

    private bool EnsureTerrainCacheLz4Available(string cachePath)
    {
        if (File.Exists(cachePath))
        {
            return true;
        }

        string? directory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string glbPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(cachePath)) + ".glb");
        if (!File.Exists(glbPath))
        {
            return false;
        }

        return TryBuildTerrainCacheFromGlb(glbPath, cachePath);
    }

    private IEnumerable<string> EnumerateTerrainCacheFallbackPaths(string mapDirectory, string requestedPath)
    {
        string requestedName = Path.GetFileName(requestedPath);
        string mapsRoot = Directory.GetParent(mapDirectory)?.FullName ?? mapDirectory;
        yield return Path.GetFullPath(Path.Combine(mapsRoot, "rmuc26map", requestedName));
        yield return Path.GetFullPath(Path.Combine(mapsRoot, "rmuc26map", "RMUC2026_MAP.terraincache.lz4"));
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RMUC2026_MAP.terraincache.lz4"));
    }

    private bool TryBuildTerrainCacheFromGlb(string glbPath, string expectedCachePath)
    {
        string referenceProject = Path.Combine(
            _host.ProjectRootPath,
            "src",
            "Simulator.LoadLargeTerrain",
            "LoadLargeTerrain.csproj");
        if (!File.Exists(referenceProject))
        {
            AppendGameplayLog(
                "terrain_cache_render.log",
                $"{DateTime.Now:HH:mm:ss.fff} terrain_cache_missing source={Path.GetFileName(expectedCachePath)} glb={Path.GetFileName(glbPath)} reference_builder=missing");
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedCachePath)!);
            AppendGameplayLog(
                "terrain_cache_render.log",
                $"{DateTime.Now:HH:mm:ss.fff} terrain_cache_build_from_glb started glb={glbPath}");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{referenceProject}\" -- \"{glbPath}\" --build-cache-only",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            bool ok = process.ExitCode == 0 && File.Exists(expectedCachePath);
            AppendGameplayLog(
                "terrain_cache_render.log",
                $"{DateTime.Now:HH:mm:ss.fff} terrain_cache_build_from_glb exit={process.ExitCode} ok={ok} cache={expectedCachePath}");
            return ok;
        }
        catch (Exception exception)
        {
            AppendGameplayLog(
                "terrain_cache_render.log",
                $"{DateTime.Now:HH:mm:ss.fff} terrain_cache_build_from_glb failed glb={glbPath} error={exception.Message}");
            return false;
        }
    }

    private static Color ResolveTerrainCacheTriangleColor(TerrainCacheVertex v0, TerrainCacheVertex v1, TerrainCacheVertex v2, float averageHeight)
    {
        int r = (v0.R + v1.R + v2.R) / 3;
        int g = (v0.G + v1.G + v2.G) / 3;
        int b = (v0.B + v1.B + v2.B) / 3;
        int a = Math.Clamp((v0.A + v1.A + v2.A) / 3, 32, 255);
        if (r + g + b <= 8)
        {
            Color fallback = ResolveTerrainColor(averageHeight <= 0.04f ? (byte)0 : (byte)12, averageHeight);
            r = fallback.R;
            g = fallback.G;
            b = fallback.B;
            a = fallback.A;
        }

        Color baseColor = Color.FromArgb(a, r, g, b);
        Color heightColor = averageHeight <= 0.03f
            ? baseColor
            : BlendColor(baseColor, Color.White, Math.Clamp(averageHeight * 0.045f, 0f, 0.10f));
        return ApplyTerrainCacheBakedLighting(heightColor, v0, v1, v2, averageHeight);
    }

    private static Color ApplyTerrainCacheBakedLighting(
        Color baseColor,
        TerrainCacheVertex v0,
        TerrainCacheVertex v1,
        TerrainCacheVertex v2,
        float averageHeight)
    {
        Vector3 normal = new(
            v0.NormalX + v1.NormalX + v2.NormalX,
            v0.NormalY + v1.NormalY + v2.NormalY,
            v0.NormalZ + v1.NormalZ + v2.NormalZ);
        if (normal.LengthSquared() <= 1e-8f)
        {
            return baseColor;
        }

        normal = Vector3.Normalize(normal);
        Vector3 keyLight = Vector3.Normalize(new Vector3(-0.34f, 0.88f, -0.31f));
        Vector3 fillLight = Vector3.Normalize(new Vector3(0.58f, 0.48f, 0.46f));
        float key = MathF.Max(0f, Vector3.Dot(normal, keyLight));
        float fill = MathF.Max(0f, Vector3.Dot(normal, fillLight));
        float sky = Math.Clamp(normal.Y * 0.5f + 0.5f, 0f, 1f);
        float slopeShadow = 1.0f - Math.Clamp((1.0f - MathF.Max(0f, normal.Y)) * 0.22f, 0f, 0.22f);
        float heightLift = Math.Clamp(averageHeight * 0.035f, 0f, 0.08f);
        float brightness = (0.60f + key * 0.34f + fill * 0.11f + sky * 0.09f + heightLift) * slopeShadow;
        brightness = Math.Clamp(brightness, 0.48f, 1.18f);

        float coolKey = Math.Clamp(0.020f + key * 0.034f + (1f - key) * (1f - sky) * 0.046f, 0f, 0.082f);
        int r = Math.Clamp((int)MathF.Round(baseColor.R * brightness + 178f * coolKey), 0, 255);
        int g = Math.Clamp((int)MathF.Round(baseColor.G * brightness + 214f * coolKey), 0, 255);
        int b = Math.Clamp((int)MathF.Round(baseColor.B * brightness + 255f * coolKey), 0, 255);
        return ApplyAmbientSceneLight(Color.FromArgb(baseColor.A, r, g, b), 0.018f);
    }

    private void RebuildTerrainTileCacheMerged(
        int stepX,
        int stepY,
        List<TerrainFacePatch> target,
        int startCellX = 0,
        int endCellX = int.MaxValue,
        int startCellY = 0,
        int endCellY = int.MaxValue)
    {
        if (_cachedRuntimeGrid is null)
        {
            return;
        }

        int minCellX = Math.Clamp(startCellX, 0, _cachedRuntimeGrid.WidthCells);
        int maxCellX = Math.Clamp(endCellX, minCellX, _cachedRuntimeGrid.WidthCells);
        int minCellY = Math.Clamp(startCellY, 0, _cachedRuntimeGrid.HeightCells);
        int maxCellY = Math.Clamp(endCellY, minCellY, _cachedRuntimeGrid.HeightCells);
        if (minCellX >= maxCellX || minCellY >= maxCellY)
        {
            return;
        }

        List<int> xStarts = new();
        List<int> xEnds = new();
        for (int x = minCellX; x < maxCellX; x += stepX)
        {
            xStarts.Add(x);
            xEnds.Add(Math.Min(maxCellX, x + stepX));
        }

        List<int> yStarts = new();
        List<int> yEnds = new();
        for (int y = minCellY; y < maxCellY; y += stepY)
        {
            yStarts.Add(y);
            yEnds.Add(Math.Min(maxCellY, y + stepY));
        }

        int columns = xStarts.Count;
        int rows = yStarts.Count;
        if (columns == 0 || rows == 0)
        {
            return;
        }

        TerrainTileSeed[,] seeds = new TerrainTileSeed[rows, columns];
        for (int row = 0; row < rows; row++)
        {
            int sampleY = Math.Min(_cachedRuntimeGrid.HeightCells - 1, yStarts[row] + (yEnds[row] - yStarts[row]) / 2);
            for (int column = 0; column < columns; column++)
            {
                int sampleX = Math.Min(_cachedRuntimeGrid.WidthCells - 1, xStarts[column] + (xEnds[column] - xStarts[column]) / 2);
                int sampleIndex = _cachedRuntimeGrid.IndexOf(sampleX, sampleY);
                float height = NormalizeTerrainRenderHeight(Math.Max(0f, _cachedRuntimeGrid.HeightMap[sampleIndex]));
                byte terrainCode = _cachedRuntimeGrid.TerrainTypeMap[sampleIndex];
                Color baseFill = ResolveTerrainTopFaceColor(sampleX, sampleY, terrainCode, height);
                Color mergeFill = QuantizeTerrainMaterialColor(baseFill);
                Color fill = baseFill;
                Color edge = height <= 0.03f
                    ? Color.FromArgb(0, fill)
                    : BlendColor(fill, Color.Black, 0.20f);
                seeds[row, column] = new TerrainTileSeed(
                    xStarts[column],
                    xEnds[column],
                    yStarts[row],
                    yEnds[row],
                    height,
                    BuildTerrainMaterialKey(terrainCode, mergeFill),
                    fill,
                    edge);
            }
        }

        BuildMergedTopTerrainFaces(seeds, rows, columns, target);
        BuildMergedWallTerrainFaces(seeds, rows, columns, TerrainWallEdge.North, target);
        BuildMergedWallTerrainFaces(seeds, rows, columns, TerrainWallEdge.South, target);
        BuildMergedWallTerrainFaces(seeds, rows, columns, TerrainWallEdge.West, target);
        BuildMergedWallTerrainFaces(seeds, rows, columns, TerrainWallEdge.East, target);
    }

    private void BuildMergedTopTerrainFaces(TerrainTileSeed[,] seeds, int rows, int columns, List<TerrainFacePatch> target)
    {
        bool[,] consumed = new bool[rows, columns];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                if (consumed[row, column])
                {
                    continue;
                }

                TerrainTileSeed seed = seeds[row, column];
                int width = 1;
                while (column + width < columns
                    && width < MaxTerrainTopMergeSpan
                    && !consumed[row, column + width]
                    && CanMergeTerrainSeed(seed, seeds[row, column + width]))
                {
                    width++;
                }

                int heightSpan = 1;
                bool expandable = true;
                while (row + heightSpan < rows && heightSpan < MaxTerrainTopMergeSpan && expandable)
                {
                    for (int testColumn = column; testColumn < column + width; testColumn++)
                    {
                        if (consumed[row + heightSpan, testColumn]
                            || !CanMergeTerrainSeed(seed, seeds[row + heightSpan, testColumn]))
                        {
                            expandable = false;
                            break;
                        }
                    }

                    if (expandable)
                    {
                        heightSpan++;
                    }
                }

                for (int y = row; y < row + heightSpan; y++)
                {
                    for (int x = column; x < column + width; x++)
                    {
                        consumed[y, x] = true;
                    }
                }

                TerrainTileSeed bottomRight = seeds[row + heightSpan - 1, column + width - 1];
                AddTerrainTopFace(seed, bottomRight, target);
            }
        }
    }

    private void BuildMergedWallTerrainFaces(
        TerrainTileSeed[,] seeds,
        int rows,
        int columns,
        TerrainWallEdge edge,
        List<TerrainFacePatch> target)
    {
        if (edge is TerrainWallEdge.North or TerrainWallEdge.South)
        {
            for (int row = 0; row < rows; row++)
            {
                int column = 0;
                while (column < columns)
                {
                    if (!TryResolveWallSegment(seeds, row, column, edge, out float topHeight, out float bottomHeight, out Color fill, out Color edgeColor))
                    {
                        column++;
                        continue;
                    }

                    int runStart = column;
                    int runEnd = column + 1;
                    while (runEnd < columns
                        && runEnd - runStart < MaxTerrainWallMergeSpan
                        && TryResolveWallSegment(seeds, row, runEnd, edge, out float nextTop, out float nextBottom, out _, out _)
                        && CanMergeTerrainWallSeed(seeds[row, runStart], seeds[row, runEnd], topHeight, bottomHeight, nextTop, nextBottom))
                    {
                        runEnd++;
                    }

                    AddTerrainWallFace(seeds[row, runStart], seeds[row, runEnd - 1], edge, topHeight, bottomHeight, fill, edgeColor, target);
                    column = runEnd;
                }
            }

            return;
        }

        for (int column = 0; column < columns; column++)
        {
            int row = 0;
            while (row < rows)
            {
                if (!TryResolveWallSegment(seeds, row, column, edge, out float topHeight, out float bottomHeight, out Color fill, out Color edgeColor))
                {
                    row++;
                    continue;
                }

                int runStart = row;
                int runEnd = row + 1;
                while (runEnd < rows
                    && runEnd - runStart < MaxTerrainWallMergeSpan
                    && TryResolveWallSegment(seeds, runEnd, column, edge, out float nextTop, out float nextBottom, out _, out _)
                    && CanMergeTerrainWallSeed(seeds[runStart, column], seeds[runEnd, column], topHeight, bottomHeight, nextTop, nextBottom))
                {
                    runEnd++;
                }

                AddTerrainWallFace(seeds[runStart, column], seeds[runEnd - 1, column], edge, topHeight, bottomHeight, fill, edgeColor, target);
                row = runEnd;
            }
        }
    }

    private void AddTerrainTopFace(TerrainTileSeed topLeft, TerrainTileSeed bottomRight, List<TerrainFacePatch> target)
    {
        float x1World = topLeft.StartCellX * _cachedRuntimeGrid!.CellWidthWorld;
        float x2World = bottomRight.EndCellX * _cachedRuntimeGrid.CellWidthWorld;
        float y1World = topLeft.StartCellY * _cachedRuntimeGrid.CellHeightWorld;
        float y2World = bottomRight.EndCellY * _cachedRuntimeGrid.CellHeightWorld;
        float baseHeight = topLeft.HeightM <= 0.02f ? 0.002f : topLeft.HeightM;
        float h00 = ResolveTerrainCornerHeight(topLeft.StartCellX, topLeft.StartCellY, baseHeight);
        float h10 = ResolveTerrainCornerHeight(bottomRight.EndCellX, topLeft.StartCellY, baseHeight);
        float h11 = ResolveTerrainCornerHeight(bottomRight.EndCellX, bottomRight.EndCellY, baseHeight);
        float h01 = ResolveTerrainCornerHeight(topLeft.StartCellX, bottomRight.EndCellY, baseHeight);
        if (ShouldSkipTerrainTopGridFace(topLeft, bottomRight, h00, h10, h11, h01))
        {
            return;
        }

        AddTerrainQuadAsTriangles(
            ToScenePoint(x1World, y1World, h00),
            ToScenePoint(x2World, y1World, h10),
            ToScenePoint(x2World, y2World, h11),
            ToScenePoint(x1World, y2World, h01),
            x1World,
            y1World,
            x2World,
            y2World,
            topLeft.FillColor,
            topLeft.EdgeColor,
            target);
    }

    private void AddTerrainWallFace(
        TerrainTileSeed startSeed,
        TerrainTileSeed endSeed,
        TerrainWallEdge edge,
        float topHeight,
        float bottomHeight,
        Color fillColor,
        Color edgeColor,
        List<TerrainFacePatch> target)
    {
        float cellWidth = _cachedRuntimeGrid!.CellWidthWorld;
        float cellHeight = _cachedRuntimeGrid.CellHeightWorld;
        switch (edge)
        {
            case TerrainWallEdge.North:
            {
                float x1World = startSeed.StartCellX * cellWidth;
                float x2World = endSeed.EndCellX * cellWidth;
                float yWorld = startSeed.StartCellY * cellHeight;
                AddTerrainQuadAsTriangles(
                    ToScenePoint(x1World, yWorld, bottomHeight),
                    ToScenePoint(x2World, yWorld, bottomHeight),
                    ToScenePoint(x2World, yWorld, topHeight),
                    ToScenePoint(x1World, yWorld, topHeight),
                    x1World,
                    yWorld,
                    x2World,
                    yWorld,
                    fillColor,
                    edgeColor,
                    target);
                break;
            }

            case TerrainWallEdge.South:
            {
                float x1World = startSeed.StartCellX * cellWidth;
                float x2World = endSeed.EndCellX * cellWidth;
                float yWorld = startSeed.EndCellY * cellHeight;
                AddTerrainQuadAsTriangles(
                    ToScenePoint(x2World, yWorld, bottomHeight),
                    ToScenePoint(x1World, yWorld, bottomHeight),
                    ToScenePoint(x1World, yWorld, topHeight),
                    ToScenePoint(x2World, yWorld, topHeight),
                    x1World,
                    yWorld,
                    x2World,
                    yWorld,
                    fillColor,
                    edgeColor,
                    target);
                break;
            }

            case TerrainWallEdge.West:
            {
                float xWorld = startSeed.StartCellX * cellWidth;
                float y1World = startSeed.StartCellY * cellHeight;
                float y2World = endSeed.EndCellY * cellHeight;
                AddTerrainQuadAsTriangles(
                    ToScenePoint(xWorld, y2World, bottomHeight),
                    ToScenePoint(xWorld, y1World, bottomHeight),
                    ToScenePoint(xWorld, y1World, topHeight),
                    ToScenePoint(xWorld, y2World, topHeight),
                    xWorld,
                    y1World,
                    xWorld,
                    y2World,
                    fillColor,
                    edgeColor,
                    target);
                break;
            }

            default:
            {
                float xWorld = startSeed.EndCellX * cellWidth;
                float y1World = startSeed.StartCellY * cellHeight;
                float y2World = endSeed.EndCellY * cellHeight;
                AddTerrainQuadAsTriangles(
                    ToScenePoint(xWorld, y1World, bottomHeight),
                    ToScenePoint(xWorld, y2World, bottomHeight),
                    ToScenePoint(xWorld, y2World, topHeight),
                    ToScenePoint(xWorld, y1World, topHeight),
                    xWorld,
                    y1World,
                    xWorld,
                    y2World,
                    fillColor,
                    edgeColor,
                    target);
                break;
            }
        }
    }

    private void AddTerrainQuadAsTriangles(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        float minXWorld,
        float minYWorld,
        float maxXWorld,
        float maxYWorld,
        Color fillColor,
        Color edgeColor,
        List<TerrainFacePatch> target)
    {
        AddTerrainFacePatch(new[] { a, b, c, d }, minXWorld, minYWorld, maxXWorld, maxYWorld, fillColor, edgeColor, target);
    }

    private void AddTerrainFacePatch(
        IReadOnlyList<Vector3> vertices,
        float minXWorld,
        float minYWorld,
        float maxXWorld,
        float maxYWorld,
        Color fillColor,
        Color edgeColor,
        List<TerrainFacePatch> target)
    {
        Vector3[] vertexArray = vertices.ToArray();
        Vector3 center = Vector3.Zero;
        foreach (Vector3 vertex in vertexArray)
        {
            center += vertex;
        }

        center /= Math.Max(1, vertexArray.Length);
        float normalY = 1f;
        if (vertexArray.Length >= 3)
        {
            Vector3 normal = Vector3.Cross(vertexArray[1] - vertexArray[0], vertexArray[2] - vertexArray[0]);
            normalY = normal.LengthSquared() <= 1e-8f ? 1f : MathF.Abs(Vector3.Normalize(normal).Y);
        }

        Color litFillColor = ShadeFaceColor(fillColor, vertexArray, normalY >= 0.55f ? 0.78f : 0.50f);
        target.Add(new TerrainFacePatch(
            vertexArray,
            center,
            Math.Min(minXWorld, maxXWorld),
            Math.Min(minYWorld, maxYWorld),
            Math.Max(minXWorld, maxXWorld),
            Math.Max(minYWorld, maxYWorld),
            litFillColor,
            edgeColor));
    }

    private bool TryResolveWallSegment(
        TerrainTileSeed[,] seeds,
        int row,
        int column,
        TerrainWallEdge edge,
        out float topHeight,
        out float bottomHeight,
        out Color fillColor,
        out Color edgeColor)
    {
        TerrainTileSeed seed = seeds[row, column];
        if (IsTerrainSeedCoveredByFacet(seed))
        {
            topHeight = 0f;
            bottomHeight = 0f;
            fillColor = Color.Empty;
            edgeColor = Color.Empty;
            return false;
        }

        float neighborHeight = ResolveNeighborHeight(seeds, row, column, edge);
        topHeight = seed.HeightM;
        bottomHeight = Math.Max(0f, neighborHeight);
        if (topHeight - bottomHeight <= TerrainSlopeSmoothThresholdM)
        {
            fillColor = Color.Empty;
            edgeColor = Color.Empty;
            return false;
        }

        fillColor = ResolveTerrainWallColor(seed.FillColor, topHeight, bottomHeight);
        edgeColor = BlendColor(fillColor, Color.Black, 0.30f);
        return true;
    }

    private bool ShouldSkipTerrainTopGridFace(
        TerrainTileSeed topLeft,
        TerrainTileSeed bottomRight,
        float h00,
        float h10,
        float h11,
        float h01)
    {
        float minHeight = MathF.Min(MathF.Min(h00, h10), MathF.Min(h11, h01));
        float maxHeight = MathF.Max(MathF.Max(h00, h10), MathF.Max(h11, h01));
        if (maxHeight <= TerrainSlopeSmoothThresholdM
            && maxHeight - minHeight <= TerrainSlopeSmoothThresholdM)
        {
            return true;
        }

        return IsTerrainSeedCoveredByFacet(topLeft, bottomRight);
    }

    private bool IsTerrainSeedCoveredByFacet(TerrainTileSeed seed)
        => IsTerrainSeedCoveredByFacet(seed, seed);

    private bool IsTerrainSeedCoveredByFacet(TerrainTileSeed topLeft, TerrainTileSeed bottomRight)
    {
        if (_cachedRuntimeGrid is null || _cachedRuntimeGrid.Facets.Count == 0)
        {
            return false;
        }

        float centerX = (topLeft.StartCellX + bottomRight.EndCellX) * _cachedRuntimeGrid.CellWidthWorld * 0.5f;
        float centerY = (topLeft.StartCellY + bottomRight.EndCellY) * _cachedRuntimeGrid.CellHeightWorld * 0.5f;
        foreach (TerrainFacetRuntime facet in _cachedRuntimeGrid.Facets)
        {
            if (centerX < facet.MinX
                || centerX > facet.MaxX
                || centerY < facet.MinY
                || centerY > facet.MaxY)
            {
                continue;
            }

            if (IsPointInsideTerrainFacet(centerX, centerY, facet.PointsWorld))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPointInsideTerrainFacet(float x, float y, IReadOnlyList<Vector2> polygon)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector2 pi = polygon[i];
            Vector2 pj = polygon[j];
            float denominator = pj.Y - pi.Y;
            if (MathF.Abs(denominator) <= 1e-6f)
            {
                continue;
            }

            bool intersects = ((pi.Y > y) != (pj.Y > y))
                && x < (pj.X - pi.X) * (y - pi.Y) / denominator + pi.X;
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private Color ResolveTerrainTopFaceColor(int runtimeCellX, int runtimeCellY, byte terrainCode, float heightM)
    {
        if (UsesOrthographicPngTopSurface()
            && TrySampleTerrainBaseColor(runtimeCellX, runtimeCellY, out Color directSample))
        {
            return directSample;
        }

        Color baseFill = TrySampleTerrainBaseColorSmoothed(runtimeCellX, runtimeCellY, out Color sampled)
            ? BlendColor(sampled, Color.White, heightM <= 0.03f ? 0f : Math.Clamp(heightM * 0.08f, 0f, 0.16f))
            : ResolveTerrainColor(terrainCode, heightM);
        return baseFill;
    }

    private Color ResolveTerrainWallColor(Color topSurfaceColor, float topHeight, float bottomHeight)
    {
        float verticalRange = Math.Clamp((topHeight - bottomHeight) / 0.60f, 0f, 1f);
        Color fallback = UsesSolidTerrainWalls()
            ? ResolveTerrainWallSolidColor()
            : Color.FromArgb(58, 62, 68);
        Color sampled = topSurfaceColor.A == 0 ? fallback : topSurfaceColor;
        Color shaded = BlendColor(sampled, Color.Black, 0.34f + 0.18f * verticalRange);
        return BlendColor(shaded, fallback, 0.12f);
    }

    private bool UsesOrthographicPngTopSurface()
    {
        return TerrainSurfaceMapSupport.UsesOrthographicPngTopSurface(_host.MapPreset.TerrainSurface);
    }

    private bool UsesSolidTerrainWalls()
    {
        return TerrainSurfaceMapSupport.UsesSolidTerrainWalls(_host.MapPreset.TerrainSurface);
    }

    private Color ResolveTerrainWallSolidColor()
    {
        return TerrainSurfaceMapSupport.ResolveTerrainWallSolidColor(
            _host.MapPreset.TerrainSurface,
            Color.FromArgb(58, 62, 68));
    }

    private float ResolveNeighborHeight(TerrainTileSeed[,] seeds, int row, int column, TerrainWallEdge edge)
    {
        TerrainTileSeed seed = seeds[row, column];
        return edge switch
        {
            TerrainWallEdge.North => row > 0 ? seeds[row - 1, column].HeightM : SampleExternalNeighborHeight(seed, TerrainWallEdge.North),
            TerrainWallEdge.South => row + 1 < seeds.GetLength(0) ? seeds[row + 1, column].HeightM : SampleExternalNeighborHeight(seed, TerrainWallEdge.South),
            TerrainWallEdge.West => column > 0 ? seeds[row, column - 1].HeightM : SampleExternalNeighborHeight(seed, TerrainWallEdge.West),
            _ => column + 1 < seeds.GetLength(1) ? seeds[row, column + 1].HeightM : SampleExternalNeighborHeight(seed, TerrainWallEdge.East),
        };
    }

    private float SampleExternalNeighborHeight(TerrainTileSeed seed, TerrainWallEdge edge)
    {
        if (_cachedRuntimeGrid is null)
        {
            return 0f;
        }

        int sampleX = Math.Clamp(seed.StartCellX + Math.Max(0, seed.EndCellX - seed.StartCellX - 1) / 2, 0, _cachedRuntimeGrid.WidthCells - 1);
        int sampleY = Math.Clamp(seed.StartCellY + Math.Max(0, seed.EndCellY - seed.StartCellY - 1) / 2, 0, _cachedRuntimeGrid.HeightCells - 1);
        switch (edge)
        {
            case TerrainWallEdge.North:
                if (seed.StartCellY <= 0)
                {
                    return 0f;
                }

                sampleY = seed.StartCellY - 1;
                break;
            case TerrainWallEdge.South:
                if (seed.EndCellY >= _cachedRuntimeGrid.HeightCells)
                {
                    return 0f;
                }

                sampleY = seed.EndCellY;
                break;
            case TerrainWallEdge.West:
                if (seed.StartCellX <= 0)
                {
                    return 0f;
                }

                sampleX = seed.StartCellX - 1;
                break;
            default:
                if (seed.EndCellX >= _cachedRuntimeGrid.WidthCells)
                {
                    return 0f;
                }

                sampleX = seed.EndCellX;
                break;
        }

        return NormalizeTerrainRenderHeight(Math.Max(0f, _cachedRuntimeGrid.HeightMap[_cachedRuntimeGrid.IndexOf(sampleX, sampleY)]));
    }

    private static bool CanMergeTerrainWallSeed(
        TerrainTileSeed left,
        TerrainTileSeed right,
        float leftTopHeight,
        float leftBottomHeight,
        float rightTopHeight,
        float rightBottomHeight)
    {
        return Math.Abs(left.HeightM - right.HeightM) <= TerrainSlopeSmoothThresholdM
            && Math.Abs(leftTopHeight - rightTopHeight) <= TerrainSlopeSmoothThresholdM
            && Math.Abs(leftBottomHeight - rightBottomHeight) <= TerrainSlopeSmoothThresholdM;
    }

    private static bool CanMergeTerrainSeed(TerrainTileSeed left, TerrainTileSeed right)
    {
        float heightTolerance = left.HeightM <= 0.02f && right.HeightM <= 0.02f
            ? 0.006f
            : TerrainSlopeSmoothThresholdM;
        return Math.Abs(left.HeightM - right.HeightM) <= heightTolerance
            && left.MaterialKey == right.MaterialKey;
    }

    private static float NormalizeTerrainRenderHeight(float rawHeight)
    {
        if (rawHeight <= 0.02f)
        {
            return 0f;
        }

        return MathF.Round(rawHeight / 0.01f) * 0.01f;
    }

    private bool TrySampleTerrainBaseColorSmoothed(int runtimeCellX, int runtimeCellY, out Color color)
    {
        color = Color.Empty;
        if (_terrainColorBitmap is null || _cachedRuntimeGrid is null)
        {
            return false;
        }

        int sampleCount = 0;
        int r = 0;
        int g = 0;
        int b = 0;
        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            int cellY = Math.Clamp(runtimeCellY + offsetY, 0, _cachedRuntimeGrid.HeightCells - 1);
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                int cellX = Math.Clamp(runtimeCellX + offsetX, 0, _cachedRuntimeGrid.WidthCells - 1);
                if (!TrySampleTerrainBaseColor(cellX, cellY, out Color sample))
                {
                    continue;
                }

                r += sample.R;
                g += sample.G;
                b += sample.B;
                sampleCount++;
            }
        }

        if (sampleCount == 0)
        {
            return false;
        }

        color = Color.FromArgb(r / sampleCount, g / sampleCount, b / sampleCount);
        return true;
    }

    private static Color QuantizeTerrainMaterialColor(Color color)
    {
        static int Quantize(int value)
        {
            const int bucket = 12;
            return Math.Clamp((int)MathF.Round(value / (float)bucket) * bucket, 0, 255);
        }

        return Color.FromArgb(color.A, Quantize(color.R), Quantize(color.G), Quantize(color.B));
    }

    private static int BuildTerrainMaterialKey(byte terrainCode, Color fill)
    {
        return terrainCode << 24 | fill.R << 16 | fill.G << 8 | fill.B;
    }

    private float ResolveTerrainCornerHeight(int cornerCellX, int cornerCellY, float baseHeight)
    {
        if (_cachedRuntimeGrid is null)
        {
            return baseHeight;
        }

        var samples = new List<float>(4);
        TryAddCornerHeightSample(cornerCellX - 1, cornerCellY - 1, baseHeight, samples);
        TryAddCornerHeightSample(cornerCellX, cornerCellY - 1, baseHeight, samples);
        TryAddCornerHeightSample(cornerCellX - 1, cornerCellY, baseHeight, samples);
        TryAddCornerHeightSample(cornerCellX, cornerCellY, baseHeight, samples);
        if (samples.Count == 0)
        {
            return baseHeight;
        }

        float min = samples[0];
        float max = samples[0];
        float sum = 0f;
        for (int index = 0; index < samples.Count; index++)
        {
            float sample = samples[index];
            min = MathF.Min(min, sample);
            max = MathF.Max(max, sample);
            sum += sample;
        }

        if (max - min > TerrainSlopeSmoothThresholdM)
        {
            return baseHeight;
        }

        float resolved = sum / samples.Count;
        if (TryResolveExtendedCornerHeight(cornerCellX, cornerCellY, resolved, out float extended))
        {
            resolved = resolved * 0.42f + extended * 0.58f;
            if (TryResolveExtendedCornerHeight(cornerCellX, cornerCellY, resolved, out float secondExtended))
            {
                resolved = resolved * 0.54f + secondExtended * 0.46f;
            }
        }

        return resolved <= 0.02f ? 0.002f : resolved;
    }

    private void TryAddCornerHeightSample(int cellX, int cellY, float baseHeight, List<float> samples)
    {
        if (_cachedRuntimeGrid is null
            || cellX < 0
            || cellY < 0
            || cellX >= _cachedRuntimeGrid.WidthCells
            || cellY >= _cachedRuntimeGrid.HeightCells)
        {
            return;
        }

        float sample = NormalizeTerrainRenderHeight(Math.Max(0f, _cachedRuntimeGrid.HeightMap[_cachedRuntimeGrid.IndexOf(cellX, cellY)]));
        if (MathF.Abs(sample - baseHeight) <= TerrainSlopeSmoothThresholdM)
        {
            samples.Add(sample);
        }
    }

    private bool TryResolveExtendedCornerHeight(int cornerCellX, int cornerCellY, float baseHeight, out float smoothedHeight)
    {
        smoothedHeight = baseHeight;
        if (_cachedRuntimeGrid is null)
        {
            return false;
        }

        var samples = new List<float>(9);
        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                TryAddCornerHeightSample(cornerCellX + offsetX, cornerCellY + offsetY, baseHeight, samples);
            }
        }

        if (samples.Count < 4)
        {
            return false;
        }

        float min = samples[0];
        float max = samples[0];
        float sum = 0f;
        for (int index = 0; index < samples.Count; index++)
        {
            float sample = samples[index];
            min = MathF.Min(min, sample);
            max = MathF.Max(max, sample);
            sum += sample;
        }

        if (max - min > TerrainSlopeSmoothThresholdM)
        {
            return false;
        }

        smoothedHeight = sum / samples.Count;
        return true;
    }

    private void AppendTerrainFacetFaces(
        List<TerrainFacePatch> target,
        float minXWorld = float.NegativeInfinity,
        float maxXWorld = float.PositiveInfinity,
        float minYWorld = float.NegativeInfinity,
        float maxYWorld = float.PositiveInfinity)
    {
        if (_cachedRuntimeGrid is null || _cachedRuntimeGrid.Facets.Count == 0)
        {
            return;
        }

        foreach (TerrainFacetRuntime facet in _cachedRuntimeGrid.Facets)
        {
            if (facet.MaxX < minXWorld || facet.MinX > maxXWorld || facet.MaxY < minYWorld || facet.MinY > maxYWorld)
            {
                continue;
            }

            AddTerrainFacetPatch(facet, target);
        }
    }

    private void AddTerrainFacetPatch(TerrainFacetRuntime facet, List<TerrainFacePatch> target)
    {
        if (facet.PointsWorld.Count < 3 || facet.HeightsM.Count < 3)
        {
            return;
        }

        Color topColor = ResolveTerrainFacetTopColor(facet);
        Color sideColor = ResolveTerrainFacetSideColor(facet);
        float minX = facet.MinX;
        float maxX = facet.MaxX;
        float minY = facet.MinY;
        float maxY = facet.MaxY;

        Vector2 anchor2 = facet.PointsWorld[0];
        float anchorHeight = facet.HeightsM[0];
        Vector3 anchor3 = ToScenePoint(anchor2.X, anchor2.Y, anchorHeight);
        for (int index = 1; index < facet.PointsWorld.Count - 1; index++)
        {
            Vector2 b2 = facet.PointsWorld[index];
            Vector2 c2 = facet.PointsWorld[index + 1];
            float hb = index < facet.HeightsM.Count ? facet.HeightsM[index] : facet.HeightsM[^1];
            float hc = index + 1 < facet.HeightsM.Count ? facet.HeightsM[index + 1] : facet.HeightsM[^1];
            AddTerrainFacePatch(
                new[]
                {
                    anchor3,
                    ToScenePoint(b2.X, b2.Y, hb),
                    ToScenePoint(c2.X, c2.Y, hc),
                },
                minX,
                minY,
                maxX,
                maxY,
                topColor,
                BlendColor(topColor, Color.Black, 0.24f),
                target);
        }

        for (int index = 0; index < facet.PointsWorld.Count; index++)
        {
            int next = (index + 1) % facet.PointsWorld.Count;
            Vector2 a2 = facet.PointsWorld[index];
            Vector2 b2 = facet.PointsWorld[next];
            float ha = index < facet.HeightsM.Count ? facet.HeightsM[index] : facet.HeightsM[^1];
            float hb = next < facet.HeightsM.Count ? facet.HeightsM[next] : facet.HeightsM[^1];
            if (Math.Max(ha, hb) <= 0.02f)
            {
                continue;
            }

            AddTerrainQuadAsTriangles(
                ToScenePoint(a2.X, a2.Y, 0f),
                ToScenePoint(b2.X, b2.Y, 0f),
                ToScenePoint(b2.X, b2.Y, hb),
                ToScenePoint(a2.X, a2.Y, ha),
                minX,
                minY,
                maxX,
                maxY,
                sideColor,
                BlendColor(sideColor, Color.Black, 0.32f),
                target);
        }
    }

    private Color ResolveTerrainFacetTopColor(TerrainFacetRuntime facet)
    {
        if (_cachedRuntimeGrid is null || facet.PointsWorld.Count == 0)
        {
            return facet.TopColor;
        }

        var samples = new List<Color>(facet.PointsWorld.Count + 1);
        for (int index = 0; index < facet.PointsWorld.Count; index++)
        {
            TryAddTerrainFacetColorSample(facet.PointsWorld[index].X, facet.PointsWorld[index].Y, samples);
        }

        float centerX = 0f;
        float centerY = 0f;
        for (int index = 0; index < facet.PointsWorld.Count; index++)
        {
            centerX += facet.PointsWorld[index].X;
            centerY += facet.PointsWorld[index].Y;
        }

        centerX /= facet.PointsWorld.Count;
        centerY /= facet.PointsWorld.Count;
        TryAddTerrainFacetColorSample(centerX, centerY, samples);
        if (samples.Count == 0)
        {
            return facet.TopColor;
        }

        int sumA = 0;
        int sumR = 0;
        int sumG = 0;
        int sumB = 0;
        for (int index = 0; index < samples.Count; index++)
        {
            Color sample = samples[index];
            sumA += sample.A;
            sumR += sample.R;
            sumG += sample.G;
            sumB += sample.B;
        }

        Color averaged = Color.FromArgb(
            sumA / samples.Count,
            sumR / samples.Count,
            sumG / samples.Count,
            sumB / samples.Count);
        float averageHeight = facet.HeightsM.Count == 0 ? 0f : facet.HeightsM.Average();
        return BlendColor(averaged, Color.White, averageHeight <= 0.03f ? 0f : Math.Clamp(averageHeight * 0.08f, 0f, 0.14f));
    }

    private void TryAddTerrainFacetColorSample(float worldX, float worldY, List<Color> samples)
    {
        if (_cachedRuntimeGrid is null)
        {
            return;
        }

        int cellX = Math.Clamp((int)MathF.Floor(worldX / Math.Max(1e-6f, _cachedRuntimeGrid.CellWidthWorld)), 0, _cachedRuntimeGrid.WidthCells - 1);
        int cellY = Math.Clamp((int)MathF.Floor(worldY / Math.Max(1e-6f, _cachedRuntimeGrid.CellHeightWorld)), 0, _cachedRuntimeGrid.HeightCells - 1);
        if (TrySampleTerrainBaseColorSmoothed(cellX, cellY, out Color terrainSample)
            || TrySampleTerrainBaseColor(cellX, cellY, out terrainSample))
        {
            samples.Add(terrainSample);
        }
    }

    private Color ResolveTerrainFacetSideColor(TerrainFacetRuntime facet)
    {
        Color sampled = ResolveTerrainFacetTopColor(facet);
        if (_cachedRuntimeGrid is not null
            && facet.PointsWorld.Count > 0)
        {
            float centerX = 0f;
            float centerY = 0f;
            for (int index = 0; index < facet.PointsWorld.Count; index++)
            {
                centerX += facet.PointsWorld[index].X;
                centerY += facet.PointsWorld[index].Y;
            }

            centerX /= facet.PointsWorld.Count;
            centerY /= facet.PointsWorld.Count;
            int cellX = Math.Clamp((int)MathF.Floor(centerX / Math.Max(1e-6f, _cachedRuntimeGrid.CellWidthWorld)), 0, _cachedRuntimeGrid.WidthCells - 1);
            int cellY = Math.Clamp((int)MathF.Floor(centerY / Math.Max(1e-6f, _cachedRuntimeGrid.CellHeightWorld)), 0, _cachedRuntimeGrid.HeightCells - 1);
            if (TrySampleTerrainBaseColorSmoothed(cellX, cellY, out Color terrainSample))
            {
                sampled = BlendColor(sampled, terrainSample, 0.72f);
            }
        }

        Color fallback = facet.SideColor.A == 0 ? Color.FromArgb(58, 62, 68) : facet.SideColor;
        return BlendColor(BlendColor(sampled, Color.Black, 0.38f), fallback, 0.10f);
    }
}
