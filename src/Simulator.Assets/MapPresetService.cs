using System.Text.Json.Nodes;
using Simulator.Core;
using Simulator.Core.Map;

namespace Simulator.Assets;

public sealed class MapPresetService
{
    public string ResolvePresetName(ProjectLayout layout, ConfigurationService configurationService)
    {
        string configPath = configurationService.ResolvePrimaryConfigPath(layout);
        JsonObject config = configurationService.LoadConfig(configPath);
        return configurationService.GetMapPreset(config);
    }

    public string ResolveMapPresetPath(ProjectLayout layout, string presetName)
    {
        string mapsPresetPath = layout.ResolvePath("maps", presetName, "map.json");
        if (File.Exists(mapsPresetPath))
        {
            return mapsPresetPath;
        }

        string mapPresetsPath = layout.ResolvePath("map_presets", $"{presetName}.json");
        if (File.Exists(mapPresetsPath))
        {
            return mapPresetsPath;
        }

        throw new FileNotFoundException(
            $"Could not find map preset '{presetName}' in maps/<preset>/map.json or map_presets/<preset>.json.");
    }

    public MapPresetDefinition LoadPreset(ProjectLayout layout, string presetName)
    {
        string path = ResolveMapPresetPath(layout, presetName);
        JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
        JsonObject root = node as JsonObject
            ?? throw new InvalidDataException($"Map preset file is not a valid JSON object: {path}");

        JsonObject map = root["map"] as JsonObject ?? root;

        int width = ReadInt(map, 0, "width");
        int height = ReadInt(map, 0, "height");
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Map preset has invalid width/height: {path}");
        }

        var facilities = new List<FacilityRegion>();
        if (map["facilities"] is JsonArray facilitiesArray)
        {
            foreach (JsonNode? item in facilitiesArray)
            {
                if (item is not JsonObject regionNode)
                {
                    continue;
                }

                facilities.Add(ParseFacility(regionNode));
            }
        }

        RuntimeGridDefinition? runtimeGrid = ParseRuntimeGrid(map["runtime_grid"] as JsonObject);

        return new MapPresetDefinition
        {
            Name = root["name"]?.ToString() ?? presetName,
            Width = width,
            Height = height,
            FieldLengthM = ReadDouble(map, 28.0, "field_length_m"),
            FieldWidthM = ReadDouble(map, 15.0, "field_width_m"),
            ImagePath = ReadString(map, string.Empty, "image_path"),
            SourcePath = path,
            Facilities = facilities,
            RuntimeGrid = runtimeGrid,
        };
    }

    public IReadOnlyList<FacilityRegion> QueryFacilitiesAt(
        MapPresetDefinition mapPreset,
        double worldX,
        double worldY)
    {
        return mapPreset.Facilities
            .Where(region => region.Contains(worldX, worldY))
            .ToArray();
    }

    private static FacilityRegion ParseFacility(JsonObject node)
    {
        string shape = ReadString(node, "rect", "shape");
        var points = new List<Point2D>();
        if (node["points"] is JsonArray pointsArray)
        {
            foreach (JsonNode? pointNode in pointsArray)
            {
                if (pointNode is not JsonArray pointArray || pointArray.Count < 2)
                {
                    continue;
                }

                if (TryReadDouble(pointArray[0], out double px)
                    && TryReadDouble(pointArray[1], out double py))
                {
                    points.Add(new Point2D(px, py));
                }
            }
        }

        return new FacilityRegion
        {
            Id = ReadString(node, Guid.NewGuid().ToString("N"), "id"),
            Type = ReadString(node, "unknown", "type"),
            Team = ReadString(node, "neutral", "team"),
            Shape = shape,
            X1 = ReadDouble(node, 0, "x1"),
            Y1 = ReadDouble(node, 0, "y1"),
            X2 = ReadDouble(node, 0, "x2"),
            Y2 = ReadDouble(node, 0, "y2"),
            Thickness = ReadDouble(node, 12, "thickness"),
            HeightM = ReadDouble(node, 0, "height_m"),
            Points = points,
        };
    }

    private static RuntimeGridDefinition? ParseRuntimeGrid(JsonObject? node)
    {
        if (node is null)
        {
            return null;
        }

        int heightCells = 0;
        int widthCells = 0;
        if (node["shape"] is JsonArray shape && shape.Count >= 2)
        {
            heightCells = TryReadDouble(shape[0], out double parsedHeight)
                ? Math.Max(0, Convert.ToInt32(parsedHeight))
                : 0;
            widthCells = TryReadDouble(shape[1], out double parsedWidth)
                ? Math.Max(0, Convert.ToInt32(parsedWidth))
                : 0;
        }

        if (heightCells <= 0 || widthCells <= 0)
        {
            return null;
        }

        return new RuntimeGridDefinition
        {
            ResolutionM = Math.Max(0.001, ReadDouble(node, 0.01, "resolution_m")),
            HeightCells = heightCells,
            WidthCells = widthCells,
            HeightScaleBakedIn = Math.Max(1.0, ReadDouble(node, 1.0, "height_scale_baked_in")),
            Channels = ReadStringDictionary(node["channels"] as JsonObject),
        };
    }

    private static IReadOnlyDictionary<string, string> ReadStringDictionary(JsonObject? objectNode)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (objectNode is null)
        {
            return result;
        }

        foreach ((string key, JsonNode? valueNode) in objectNode)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string? value = valueNode?.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result[key] = value.Trim();
        }

        return result;
    }

    private static bool TryReadDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is null)
        {
            return false;
        }

        if (node is JsonValue valueNode)
        {
            if (valueNode.TryGetValue(out double doubleValue))
            {
                value = doubleValue;
                return true;
            }

            if (valueNode.TryGetValue(out int intValue))
            {
                value = intValue;
                return true;
            }

            if (valueNode.TryGetValue(out string? text)
                && double.TryParse(text, out double parsed))
            {
                value = parsed;
                return true;
            }
        }

        return false;
    }

    private static string ReadString(JsonObject node, string fallback, params string[] path)
    {
        JsonNode? target = Walk(node, path);
        return target?.ToString() ?? fallback;
    }

    private static int ReadInt(JsonObject node, int fallback, params string[] path)
    {
        JsonNode? target = Walk(node, path);
        if (target is JsonValue value)
        {
            if (value.TryGetValue(out int intValue))
            {
                return intValue;
            }

            if (value.TryGetValue(out double doubleValue))
            {
                return Convert.ToInt32(doubleValue);
            }

            if (value.TryGetValue(out string? text) && int.TryParse(text, out intValue))
            {
                return intValue;
            }
        }

        return fallback;
    }

    private static double ReadDouble(JsonObject node, double fallback, params string[] path)
    {
        JsonNode? target = Walk(node, path);
        return TryReadDouble(target, out double value) ? value : fallback;
    }

    private static JsonNode? Walk(JsonObject node, params string[] path)
    {
        JsonNode? current = node;
        foreach (string segment in path)
        {
            if (current is not JsonObject obj)
            {
                return null;
            }

            current = obj[segment];
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }
}
