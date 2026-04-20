namespace Simulator.ThreeD;

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
        float cellHeightWorld)
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
}
