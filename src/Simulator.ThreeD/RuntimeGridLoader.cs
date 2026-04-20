using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed class RuntimeGridLoader
{
    public RuntimeGridData? TryLoad(MapPresetDefinition preset, out string? warning)
    {
        warning = null;

        RuntimeGridDefinition? runtime = preset.RuntimeGrid;
        if (runtime is null)
        {
            warning = "runtime_grid metadata not found in map preset.";
            return null;
        }

        string? mapDirectory = Path.GetDirectoryName(preset.SourcePath);
        if (string.IsNullOrWhiteSpace(mapDirectory) || !Directory.Exists(mapDirectory))
        {
            warning = "map preset directory is unavailable.";
            return null;
        }

        string? heightPath = ResolveChannelPath(mapDirectory, runtime.Channels, "height_map", preset.Name);
        string? terrainPath = ResolveChannelPath(mapDirectory, runtime.Channels, "terrain_type_map", preset.Name);
        string? movementBlockPath = ResolveChannelPath(mapDirectory, runtime.Channels, "movement_block_map", preset.Name);
        string? visionBlockPath = ResolveChannelPath(mapDirectory, runtime.Channels, "vision_block_map", preset.Name);
        string? visionBlockHeightPath = ResolveChannelPath(mapDirectory, runtime.Channels, "vision_block_height_map", preset.Name);
        string? functionPassPath = ResolveChannelPath(mapDirectory, runtime.Channels, "function_pass_map", preset.Name);
        string? functionHeadingPath = ResolveChannelPath(mapDirectory, runtime.Channels, "function_heading_map", preset.Name);
        if (heightPath is null
            || terrainPath is null
            || movementBlockPath is null
            || visionBlockPath is null
            || visionBlockHeightPath is null
            || functionPassPath is null
            || functionHeadingPath is null)
        {
            warning = "runtime_grid required channel is missing.";
            return null;
        }

        if (!File.Exists(heightPath)
            || !File.Exists(terrainPath)
            || !File.Exists(movementBlockPath)
            || !File.Exists(visionBlockPath)
            || !File.Exists(visionBlockHeightPath)
            || !File.Exists(functionPassPath)
            || !File.Exists(functionHeadingPath))
        {
            warning = "runtime_grid channel file is missing on disk.";
            return null;
        }

        try
        {
            float[] heightValues = NpyArrayReader.ReadFloatArray2D(heightPath, out int heightRows, out int heightCols);
            byte[] terrainValues = NpyArrayReader.ReadByteArray2D(terrainPath, out int terrainRows, out int terrainCols);
            bool[] movementBlockValues = NpyArrayReader.ReadBoolArray2D(movementBlockPath, out int movementRows, out int movementCols);
            bool[] visionBlockValues = NpyArrayReader.ReadBoolArray2D(visionBlockPath, out int visionRows, out int visionCols);
            float[] visionBlockHeightValues = NpyArrayReader.ReadFloatArray2D(visionBlockHeightPath, out int visionHeightRows, out int visionHeightCols);
            byte[] functionPassValues = NpyArrayReader.ReadByteArray2D(functionPassPath, out int functionPassRows, out int functionPassCols);
            float[] functionHeadingValues = NpyArrayReader.ReadFloatArray2D(functionHeadingPath, out int functionHeadingRows, out int functionHeadingCols);

            if (heightRows != terrainRows
                || heightCols != terrainCols
                || heightRows != movementRows
                || heightCols != movementCols
                || heightRows != visionRows
                || heightCols != visionCols
                || heightRows != visionHeightRows
                || heightCols != visionHeightCols
                || heightRows != functionPassRows
                || heightCols != functionPassCols
                || heightRows != functionHeadingRows
                || heightCols != functionHeadingCols)
            {
                warning = "runtime_grid channel shapes are inconsistent.";
                return null;
            }

            if (runtime.HeightCells > 0 && runtime.WidthCells > 0
                && (heightRows != runtime.HeightCells || heightCols != runtime.WidthCells))
            {
                warning = "runtime_grid shape in metadata does not match NPY channel shape.";
                return null;
            }

            float bakedScale = (float)Math.Max(1.0, runtime.HeightScaleBakedIn);
            if (Math.Abs(bakedScale - 1f) > 1e-6f)
            {
                float ratio = 1f / bakedScale;
                for (int index = 0; index < heightValues.Length; index++)
                {
                    heightValues[index] *= ratio;
                }
            }

            float cellWidth = preset.Width / (float)Math.Max(1, heightCols);
            float cellHeight = preset.Height / (float)Math.Max(1, heightRows);
            var data = new RuntimeGridData(
                heightCols,
                heightRows,
                heightValues,
                terrainValues,
                movementBlockValues,
                visionBlockValues,
                visionBlockHeightValues,
                functionPassValues,
                functionHeadingValues,
                cellWidth,
                cellHeight);
            if (!data.IsValid)
            {
                warning = "runtime_grid data is invalid after loading.";
                return null;
            }

            return data;
        }
        catch (Exception exception)
        {
            warning = $"failed to load runtime_grid channels: {exception.Message}";
            return null;
        }
    }

    private static string? ResolveChannelPath(
        string mapDirectory,
        IReadOnlyDictionary<string, string> channels,
        string channelName,
        string presetName)
    {
        if (channels.TryGetValue(channelName, out string? configuredPath)
            && !string.IsNullOrWhiteSpace(configuredPath))
        {
            string explicitPath = ResolveRelativePath(mapDirectory, configuredPath.Trim());
            if (File.Exists(explicitPath))
            {
                return explicitPath;
            }
        }

        string fallback = Path.Combine(mapDirectory, $"{presetName}.{channelName}.npy");
        if (File.Exists(fallback))
        {
            return fallback;
        }

        string? firstMatch = Directory
            .EnumerateFiles(mapDirectory, $"*.{channelName}.npy", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        return firstMatch;
    }

    private static string ResolveRelativePath(string mapDirectory, string value)
    {
        if (Path.IsPathRooted(value))
        {
            return value;
        }

        return Path.GetFullPath(Path.Combine(mapDirectory, value));
    }
}
