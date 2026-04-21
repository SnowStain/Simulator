using System.Drawing;
using System.Numerics;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const int MaxTerrainTopMergeSpan = 36;
    private const int MaxTerrainWallMergeSpan = 56;
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
        AddTerrainFacePatch(new[] { a, b, c }, minXWorld, minYWorld, maxXWorld, maxYWorld, fillColor, edgeColor, target);
        AddTerrainFacePatch(new[] { a, c, d }, minXWorld, minYWorld, maxXWorld, maxYWorld, fillColor, edgeColor, target);
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
        target.Add(new TerrainFacePatch(
            vertexArray,
            center,
            Math.Min(minXWorld, maxXWorld),
            Math.Min(minYWorld, maxYWorld),
            Math.Max(minXWorld, maxXWorld),
            Math.Max(minYWorld, maxYWorld),
            fillColor,
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

        Color topColor = facet.TopColor;
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

    private Color ResolveTerrainFacetSideColor(TerrainFacetRuntime facet)
    {
        Color sampled = facet.TopColor;
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
                sampled = BlendColor(sampled, terrainSample, 0.55f);
            }
        }

        Color fallback = facet.SideColor.A == 0 ? Color.FromArgb(58, 62, 68) : facet.SideColor;
        return BlendColor(BlendColor(sampled, Color.Black, 0.38f), fallback, 0.10f);
    }
}
