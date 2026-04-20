using System.Drawing;
using System.Numerics;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const int MaxTerrainTopMergeSpan = 36;
    private const int MaxTerrainWallMergeSpan = 56;

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
                Color fill = TrySampleTerrainBaseColor(sampleX, sampleY, out Color sampled)
                    ? BlendColor(sampled, Color.White, height <= 0.03f ? 0f : Math.Clamp(height * 0.08f, 0f, 0.16f))
                    : ResolveTerrainColor(terrainCode, height);
                Color edge = height <= 0.03f
                    ? Color.FromArgb(0, fill)
                    : BlendColor(fill, Color.Black, 0.20f);
                seeds[row, column] = new TerrainTileSeed(
                    xStarts[column],
                    xEnds[column],
                    yStarts[row],
                    yEnds[row],
                    height,
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
                        && TryResolveWallSegment(seeds, row, runEnd, edge, out float nextTop, out float nextBottom, out Color nextFill, out _)
                        && CanMergeTerrainWallSeed(seeds[row, runStart], seeds[row, runEnd], topHeight, bottomHeight, nextTop, nextBottom, fill, nextFill))
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
                    && TryResolveWallSegment(seeds, runEnd, column, edge, out float nextTop, out float nextBottom, out Color nextFill, out _)
                    && CanMergeTerrainWallSeed(seeds[runStart, column], seeds[runEnd, column], topHeight, bottomHeight, nextTop, nextBottom, fill, nextFill))
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
        float topHeight = topLeft.HeightM <= 0.02f ? 0.002f : topLeft.HeightM;

        AddTerrainFacePatch(
            new[]
            {
                ToScenePoint(x1World, y1World, topHeight),
                ToScenePoint(x2World, y1World, topHeight),
                ToScenePoint(x2World, y2World, topHeight),
                ToScenePoint(x1World, y2World, topHeight),
            },
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
                AddTerrainFacePatch(
                    new[]
                    {
                        ToScenePoint(x1World, yWorld, bottomHeight),
                        ToScenePoint(x2World, yWorld, bottomHeight),
                        ToScenePoint(x2World, yWorld, topHeight),
                        ToScenePoint(x1World, yWorld, topHeight),
                    },
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
                AddTerrainFacePatch(
                    new[]
                    {
                        ToScenePoint(x2World, yWorld, bottomHeight),
                        ToScenePoint(x1World, yWorld, bottomHeight),
                        ToScenePoint(x1World, yWorld, topHeight),
                        ToScenePoint(x2World, yWorld, topHeight),
                    },
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
                AddTerrainFacePatch(
                    new[]
                    {
                        ToScenePoint(xWorld, y2World, bottomHeight),
                        ToScenePoint(xWorld, y1World, bottomHeight),
                        ToScenePoint(xWorld, y1World, topHeight),
                        ToScenePoint(xWorld, y2World, topHeight),
                    },
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
                AddTerrainFacePatch(
                    new[]
                    {
                        ToScenePoint(xWorld, y1World, bottomHeight),
                        ToScenePoint(xWorld, y2World, bottomHeight),
                        ToScenePoint(xWorld, y2World, topHeight),
                        ToScenePoint(xWorld, y1World, topHeight),
                    },
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
        if (topHeight - bottomHeight <= 0.02f)
        {
            fillColor = Color.Empty;
            edgeColor = Color.Empty;
            return false;
        }

        fillColor = BlendColor(seed.FillColor, Color.Black, 0.12f);
        edgeColor = BlendColor(fillColor, Color.Black, 0.22f);
        return true;
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
        float rightBottomHeight,
        Color leftFill,
        Color rightFill)
    {
        return Math.Abs(left.HeightM - right.HeightM) <= 0.035f
            && Math.Abs(leftTopHeight - rightTopHeight) <= 0.035f
            && Math.Abs(leftBottomHeight - rightBottomHeight) <= 0.035f
            && Math.Abs(left.FillColor.R - right.FillColor.R) <= 26
            && Math.Abs(left.FillColor.G - right.FillColor.G) <= 26
            && Math.Abs(left.FillColor.B - right.FillColor.B) <= 26
            && Math.Abs(leftFill.R - rightFill.R) <= 26
            && Math.Abs(leftFill.G - rightFill.G) <= 26
            && Math.Abs(leftFill.B - rightFill.B) <= 26;
    }

    private static bool CanMergeTerrainSeed(TerrainTileSeed left, TerrainTileSeed right)
    {
        float heightTolerance = left.HeightM <= 0.02f && right.HeightM <= 0.02f ? 0.006f : 0.008f;
        int colorTolerance = left.HeightM <= 0.02f && right.HeightM <= 0.02f ? 32 : 18;
        return Math.Abs(left.HeightM - right.HeightM) <= heightTolerance
            && Math.Abs(left.FillColor.R - right.FillColor.R) <= colorTolerance
            && Math.Abs(left.FillColor.G - right.FillColor.G) <= colorTolerance
            && Math.Abs(left.FillColor.B - right.FillColor.B) <= colorTolerance;
    }

    private static float NormalizeTerrainRenderHeight(float rawHeight)
    {
        if (rawHeight <= 0.02f)
        {
            return 0f;
        }

        return MathF.Round(rawHeight / 0.01f) * 0.01f;
    }
}
