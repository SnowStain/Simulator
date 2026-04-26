using System.Text.Json;
using System.Text.Json.Nodes;
using Simulator.Core;
using Simulator.Core.Map;

namespace Simulator.Assets;

public sealed class MapPresetService
{
    private static readonly HashSet<string> VisibleMapPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        "blankCanvas",
        "rmuc2026",
    };

    private readonly MapDocumentStore _documentStore = new();

    public string ResolvePresetName(ProjectLayout layout, ConfigurationService configurationService)
    {
        string configPath = configurationService.ResolvePrimaryConfigPath(layout);
        JsonObject config = configurationService.LoadConfig(configPath);
        return configurationService.GetMapPreset(config);
    }

    public IReadOnlyList<string> ListPresetNames(ProjectLayout layout)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string mapPresetsPath = layout.ResolvePath("map_presets");
        if (Directory.Exists(mapPresetsPath))
        {
            foreach (string extension in MapDocumentStore.SupportedExtensions)
            {
                foreach (string file in Directory.EnumerateFiles(mapPresetsPath, $"*{extension}", SearchOption.TopDirectoryOnly))
                {
                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }

        string mapsPath = layout.ResolvePath("maps");
        if (Directory.Exists(mapsPath))
        {
            foreach (string directory in Directory.EnumerateDirectories(mapsPath))
            {
                string name = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (TryResolvePresetSource(layout, name, out _))
                {
                    names.Add(name);
                }
            }
        }

        return names
            .Where(VisibleMapPresets.Contains)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TryResolvePresetSource(ProjectLayout layout, string presetName, out ResolvedMapDocumentSource source)
    {
        foreach (string candidate in EnumerateMapPresetCandidates(layout, presetName))
        {
            if (_documentStore.TryResolve(candidate, out source))
            {
                return true;
            }
        }

        source = default!;
        return false;
    }

    public ResolvedMapDocumentSource ResolveMapPresetSource(ProjectLayout layout, string presetName)
    {
        if (TryResolvePresetSource(layout, presetName, out ResolvedMapDocumentSource source))
        {
            return source;
        }

        throw new FileNotFoundException(
            $"Could not find map preset '{presetName}' in maps/<preset>/map.(json|lz4) or map_presets/<preset>.(json|lz4).");
    }

    public string ResolveMapPresetPath(ProjectLayout layout, string presetName)
    {
        return ResolveMapPresetSource(layout, presetName).FullPath;
    }

    public MapPresetDocument LoadPresetDocument(ProjectLayout layout, string presetName)
    {
        return LoadPresetDocument(layout, presetName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private MapPresetDocument LoadPresetDocument(
        ProjectLayout layout,
        string presetName,
        HashSet<string> ancestry)
    {
        if (!ancestry.Add(presetName))
        {
            throw new InvalidDataException($"Map preset inheritance cycle detected at '{presetName}'.");
        }

        ResolvedMapDocumentSource source = ResolveMapPresetSource(layout, presetName);
        JsonObject root = _documentStore.Load(source);
        string? extendsPreset = root["extends"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(extendsPreset))
        {
            MapPresetDocument baseDocument = LoadPresetDocument(layout, extendsPreset.Trim(), ancestry);
            JsonObject mergedRoot = MergeObjects(
                baseDocument.Root.DeepClone() as JsonObject ?? new JsonObject(),
                root);
            JsonObject mergedMap = mergedRoot["map"] as JsonObject ?? mergedRoot;
            return new MapPresetDocument(mergedRoot, mergedMap, source);
        }

        JsonObject map = root["map"] as JsonObject ?? root;
        return new MapPresetDocument(root, map, source);
    }

    public JsonObject LoadDocument(string path, string? logicalName = null) => _documentStore.Load(path, logicalName);

    public void SaveDocument(string path, JsonObject document) => _documentStore.Save(path, document);

    public void SaveDocument(ResolvedMapDocumentSource source, JsonObject document) => _documentStore.Save(source, document);

    public ResolvedMapDocumentSource ResolveDocument(string path, string? logicalName = null) => _documentStore.Resolve(path, logicalName);

    public JsonObject? TryLoadRelativeDocument(string baseDirectory, string relativeOrAbsolutePath)
        => _documentStore.TryLoadRelative(baseDirectory, relativeOrAbsolutePath);

    public bool TryResolveRelativeDocument(string baseDirectory, string relativeOrAbsolutePath, out ResolvedMapDocumentSource source)
        => _documentStore.TryResolveRelative(baseDirectory, relativeOrAbsolutePath, out source);

    private static IEnumerable<string> EnumerateMapPresetCandidates(ProjectLayout layout, string presetName)
    {
        foreach (string extension in MapDocumentStore.SupportedExtensions)
        {
            yield return layout.ResolvePath("maps", presetName, $"map{extension}");
        }

        foreach (string extension in MapDocumentStore.SupportedExtensions)
        {
            yield return layout.ResolvePath("map_presets", $"{presetName}{extension}");
        }
    }

    public MapPresetDefinition LoadPreset(ProjectLayout layout, string presetName)
    {
        MapPresetDocument document = LoadPresetDocument(layout, presetName);
        string path = document.Source.FullPath;
        JsonObject root = document.Root;
        JsonObject map = document.Map;

        int width = ReadInt(map, 0, "width");
        int height = ReadInt(map, 0, "height");
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Map preset has invalid width/height: {path}");
        }

        MapCoordinateSystemDefinition coordinateSystem = ParseCoordinateSystem(map);
        var facilities = new List<FacilityRegion>();
        if (map["facilities"] is JsonArray facilitiesArray)
        {
            foreach (JsonNode? item in facilitiesArray)
            {
                if (item is not JsonObject regionNode)
                {
                    continue;
                }

                FacilityRegion facility = ParseFacility(regionNode);
                if (string.Equals(facility.Type, "dog_hole", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                facilities.Add(facility);
            }
        }

        facilities.AddRange(MapAnnotationFacilitySynthesizer.CreateMissingFacilities(
            ResolveRelativePath(path, ReadString(map, string.Empty, "annotation_path")),
            facilities,
            width,
            height,
            coordinateSystem.FieldLengthM,
            coordinateSystem.FieldWidthM));

        TerrainSurfaceDefinition? terrainSurface = ParseTerrainSurface(
            map["terrain_surface"] as JsonObject,
            path,
            ReadString(map, string.Empty, "image_path"));
        RuntimeGridDefinition? runtimeGrid = ParseRuntimeGrid(map["runtime_grid"] as JsonObject, terrainSurface);

        return new MapPresetDefinition
        {
            Name = root["name"]?.ToString() ?? presetName,
            Width = width,
            Height = height,
            FieldLengthM = coordinateSystem.FieldLengthM,
            FieldWidthM = coordinateSystem.FieldWidthM,
            ImagePath = ReadString(map, string.Empty, "image_path"),
            SourcePath = path,
            AnnotationPath = ResolveRelativePath(path, ReadString(map, string.Empty, "annotation_path")),
            Facilities = facilities,
            CoordinateSystem = coordinateSystem,
            TerrainSurface = terrainSurface,
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

        HashSet<string> knownKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "id",
            "type",
            "team",
            "shape",
            "x1",
            "y1",
            "x2",
            "y2",
            "thickness",
            "height_m",
            "points",
        };
        Dictionary<string, JsonElement> additionalProperties = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, JsonNode?> property in node)
        {
            if (knownKeys.Contains(property.Key) || property.Value is null)
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(property.Value.ToJsonString());
            additionalProperties[property.Key] = document.RootElement.Clone();
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
            AdditionalProperties = additionalProperties.Count == 0 ? null : additionalProperties,
        };
    }

    private readonly record struct LockedDogHoleFacility(
        string Id,
        string Team,
        double X1,
        double Y1,
        double X2,
        double Y2,
        double YawDeg,
        double BottomOffsetM,
        double TopBeamThicknessM);

    private static readonly LockedDogHoleFacility[] LockedDogHoleFacilities =
    [
        new("red_dog_hole", "red", 725, 92, 761, 142, 0.0, 0.0, 0.10),
        new("blue_dog_hole", "blue", 824, 734, 863, 789, 0.0, 0.0, 0.10),
        new("red_road_side_dog_hole", "red", 513, 710, 567, 727, 90.0, 0.0, 0.05),
        new("blue_road_side_dog_hole", "blue", 1020, 151, 1069, 166, 90.0, 0.0, 0.05),
    ];

    private static void EnsureLockedDogHoleFacilities(List<FacilityRegion> facilities)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int index = facilities.Count - 1; index >= 0; index--)
        {
            FacilityRegion region = facilities[index];
            if (!TryResolveLockedDogHoleFacility(region.Id, out LockedDogHoleFacility locked))
            {
                continue;
            }

            if (!seen.Add(locked.Id))
            {
                facilities.RemoveAt(index);
                continue;
            }

            facilities[index] = NormalizeLockedDogHoleFacility(region);
        }

        foreach (LockedDogHoleFacility locked in LockedDogHoleFacilities)
        {
            if (seen.Contains(locked.Id))
            {
                continue;
            }

            facilities.Add(CreateLockedDogHoleFacility(locked));
        }
    }

    private static FacilityRegion CreateLockedDogHoleFacility(LockedDogHoleFacility locked)
        => NormalizeLockedDogHoleFacility(new FacilityRegion
        {
            Id = locked.Id,
            Type = "dog_hole",
            Team = locked.Team,
        });

    private static FacilityRegion NormalizeLockedDogHoleFacility(FacilityRegion region)
    {
        if (!TryResolveLockedDogHoleFacility(region.Id, out LockedDogHoleFacility locked))
        {
            return region;
        }

        Dictionary<string, JsonElement> properties = region.AdditionalProperties is null
            ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(region.AdditionalProperties, StringComparer.OrdinalIgnoreCase);
        SetJson(properties, "model_type", "frame_dog_hole");
        SetJson(properties, "model_yaw_deg", locked.YawDeg);
        SetJson(properties, "model_bottom_offset_m", locked.BottomOffsetM);
        SetJson(properties, "model_clear_width_m", 0.8);
        SetJson(properties, "model_clear_height_m", 0.25);
        SetJson(properties, "model_depth_m", 0.25);
        SetJson(properties, "model_frame_thickness_m", 0.065);
        SetJson(properties, "model_top_beam_thickness_m", locked.TopBeamThicknessM);
        SetJson(properties, "blocks_movement", false);

        return new FacilityRegion
        {
            Id = locked.Id,
            Type = "dog_hole",
            Team = locked.Team,
            Shape = "rect",
            X1 = locked.X1,
            Y1 = locked.Y1,
            X2 = locked.X2,
            Y2 = locked.Y2,
            Thickness = region.Thickness,
            HeightM = 0.0,
            Points = Array.Empty<Point2D>(),
            AdditionalProperties = properties,
        };
    }

    private static bool TryResolveLockedDogHoleFacility(string id, out LockedDogHoleFacility facility)
    {
        foreach (LockedDogHoleFacility locked in LockedDogHoleFacilities)
        {
            if (string.Equals(id, locked.Id, StringComparison.OrdinalIgnoreCase))
            {
                facility = locked;
                return true;
            }
        }

        facility = default;
        return false;
    }

    private static void SetJson<T>(Dictionary<string, JsonElement> properties, string key, T value)
        => properties[key] = JsonSerializer.SerializeToElement(value);

    private static MapCoordinateSystemDefinition ParseCoordinateSystem(JsonObject map)
    {
        JsonObject? node = map["coordinate_system"] as JsonObject;
        return new MapCoordinateSystemDefinition
        {
            CoordinateSpace = ReadString(node ?? map, ReadString(map, "world", "coordinate_space"), "coordinate_space"),
            Unit = ReadString(node ?? map, ReadString(map, "px", "unit"), "unit"),
            OriginX = ReadDouble(node ?? map, ReadDouble(map, 0.0, "origin_x"), "origin_x"),
            OriginY = ReadDouble(node ?? map, ReadDouble(map, 0.0, "origin_y"), "origin_y"),
            FieldLengthM = ReadDouble(node ?? map, ReadDouble(map, 28.0, "field_length_m"), "field_length_m"),
            FieldWidthM = ReadDouble(node ?? map, ReadDouble(map, 15.0, "field_width_m"), "field_width_m"),
        };
    }

    private static TerrainSurfaceDefinition? ParseTerrainSurface(JsonObject? node, string presetPath, string fallbackImagePath)
    {
        if (node is null)
        {
            return null;
        }

        string presetDirectory = Path.GetDirectoryName(presetPath) ?? string.Empty;
        string descriptorPath = ReadString(node, string.Empty, "descriptor_path");
        JsonObject? descriptorNode = LoadDescriptorNode(presetDirectory, descriptorPath);

        (int heightCells, int widthCells) = ReadShape(node, descriptorNode);
        IReadOnlyDictionary<string, string> channels = ReadStringDictionary(node["channels"] as JsonObject);
        if (channels.Count == 0 && descriptorNode is not null)
        {
            channels = ReadStringDictionary(descriptorNode["channels"] as JsonObject);
        }

        IReadOnlyList<TerrainFacetDefinition> facets = ParseTerrainFacets(node["facets"] as JsonArray);
        if (facets.Count == 0 && descriptorNode is not null)
        {
            facets = ParseTerrainFacets(descriptorNode["facets"] as JsonArray);
        }

        return new TerrainSurfaceDefinition
        {
            MapType = ReadString(node, ReadString(descriptorNode, "terrain_surface_map", "map_type"), "map_type"),
            DescriptorPath = descriptorPath,
            StorageKind = ReadString(node, ReadString(descriptorNode, "runtime_triangle_grid", "storage_kind"), "storage_kind"),
            Topology = ReadString(node, ReadString(descriptorNode, "triangle_grid", "topology"), "topology"),
            MergeMode = ReadString(node, ReadString(descriptorNode, "merged_exposed_faces", "merge_mode"), "merge_mode"),
            SplitMode = ReadString(node, ReadString(descriptorNode, "diag_forward", "split_mode"), "split_mode"),
            BaseColorImagePath = ReadString(node, ReadString(descriptorNode, fallbackImagePath, "base_color_image_path"), "base_color_image_path"),
            RenderProfile = ReadString(node, ReadString(descriptorNode, "top_png_orthographic_side_solid", "render_profile"), "render_profile"),
            TopFaceMode = ReadString(node, ReadString(descriptorNode, "orthographic_png", "top_face_mode"), "top_face_mode"),
            SideFaceMode = ReadString(node, ReadString(descriptorNode, "solid_color", "side_face_mode"), "side_face_mode"),
            SideColorHex = ReadString(node, ReadString(descriptorNode, "#4B4F55", "side_color"), "side_color"),
            TopNormalThreshold = Math.Clamp(
                ReadDouble(node, ReadDouble(descriptorNode, 0.9, "top_normal_threshold"), "top_normal_threshold"),
                0.0,
                1.0),
            SideNormalThreshold = Math.Clamp(
                ReadDouble(node, ReadDouble(descriptorNode, 0.1, "side_normal_threshold"), "side_normal_threshold"),
                0.0,
                1.0),
            ResolutionM = Math.Max(
                0.001,
                ReadDouble(node, ReadDouble(descriptorNode, 0.01, "resolution_m"), "resolution_m")),
            HeightCells = heightCells,
            WidthCells = widthCells,
            HeightScaleBakedIn = Math.Max(
                1.0,
                ReadDouble(node, ReadDouble(descriptorNode, 1.0, "height_scale_baked_in"), "height_scale_baked_in")),
            Channels = channels,
            Facets = facets,
        };
    }

    private static IReadOnlyList<TerrainFacetDefinition> ParseTerrainFacets(JsonArray? node)
    {
        if (node is null || node.Count == 0)
        {
            return Array.Empty<TerrainFacetDefinition>();
        }

        var facets = new List<TerrainFacetDefinition>();
        foreach (JsonNode? item in node)
        {
            if (item is not JsonObject facetNode)
            {
                continue;
            }

            var points = new List<Point2D>();
            if (facetNode["points"] is JsonArray pointsArray)
            {
                foreach (JsonNode? pointNode in pointsArray)
                {
                    if (pointNode is not JsonArray pointArray || pointArray.Count < 2)
                    {
                        continue;
                    }

                    if (TryReadDouble(pointArray[0], out double x)
                        && TryReadDouble(pointArray[1], out double y))
                    {
                        points.Add(new Point2D(x, y));
                    }
                }
            }

            var heights = new List<double>();
            if (facetNode["heights_m"] is JsonArray heightsArray)
            {
                foreach (JsonNode? heightNode in heightsArray)
                {
                    if (TryReadDouble(heightNode, out double height))
                    {
                        heights.Add(height);
                    }
                }
            }

            if (points.Count < 3)
            {
                continue;
            }

            while (heights.Count < points.Count)
            {
                heights.Add(heights.Count == 0 ? 0.0 : heights[^1]);
            }

            facets.Add(new TerrainFacetDefinition
            {
                Id = ReadString(facetNode, Guid.NewGuid().ToString("N"), "id"),
                Type = ReadString(facetNode, "slope", "type"),
                Team = ReadString(facetNode, "neutral", "team"),
                TopColorHex = ReadString(facetNode, "#8A9576", "top_color"),
                SideColorHex = ReadString(facetNode, "#4B4F55", "side_color"),
                Points = points,
                HeightsM = heights,
            });
        }

        return facets;
    }

    private static RuntimeGridDefinition? ParseRuntimeGrid(JsonObject? node, TerrainSurfaceDefinition? terrainSurface)
    {
        if (node is null && terrainSurface is null)
        {
            return null;
        }

        (int heightCells, int widthCells) = ReadShape(node, null);
        if ((heightCells <= 0 || widthCells <= 0) && terrainSurface is not null)
        {
            heightCells = terrainSurface.HeightCells;
            widthCells = terrainSurface.WidthCells;
        }
        if (heightCells <= 0 || widthCells <= 0)
        {
            return null;
        }

        IReadOnlyDictionary<string, string> channels = ReadStringDictionary(node?["channels"] as JsonObject);
        if (channels.Count == 0 && terrainSurface is not null)
        {
            channels = terrainSurface.Channels;
        }

        return new RuntimeGridDefinition
        {
            ResolutionM = Math.Max(0.001, ReadDouble(node, terrainSurface?.ResolutionM ?? 0.01, "resolution_m")),
            HeightCells = heightCells,
            WidthCells = widthCells,
            HeightScaleBakedIn = Math.Max(1.0, ReadDouble(node, terrainSurface?.HeightScaleBakedIn ?? 1.0, "height_scale_baked_in")),
            DescriptorPath = ReadString(node, terrainSurface?.DescriptorPath ?? string.Empty, "descriptor_path"),
            StorageKind = ReadString(node, terrainSurface?.StorageKind ?? "runtime_triangle_grid", "storage_kind"),
            SourcePath = ReadString(node, string.Empty, "source_path"),
            SurfaceTopology = ReadString(node, terrainSurface?.Topology ?? "triangle_grid", "surface_topology"),
            SurfaceMergeMode = ReadString(node, terrainSurface?.MergeMode ?? "merged_exposed_faces", "surface_merge_mode"),
            SurfaceSplitMode = ReadString(node, terrainSurface?.SplitMode ?? "diag_forward", "surface_split_mode"),
            RenderProfile = ReadString(node, terrainSurface?.RenderProfile ?? "top_png_orthographic_side_solid", "render_profile"),
            TopFaceMode = ReadString(node, terrainSurface?.TopFaceMode ?? "orthographic_png", "top_face_mode"),
            SideFaceMode = ReadString(node, terrainSurface?.SideFaceMode ?? "solid_color", "side_face_mode"),
            SideColorHex = ReadString(node, terrainSurface?.SideColorHex ?? "#4B4F55", "side_color"),
            Channels = channels,
        };
    }

    private static (int HeightCells, int WidthCells) ReadShape(JsonObject? primary, JsonObject? secondary)
    {
        static (int Height, int Width) Read(JsonObject? node)
        {
            if (node?["shape"] is JsonArray shape && shape.Count >= 2)
            {
                int heightCells = TryReadDouble(shape[0], out double parsedHeight)
                    ? Math.Max(0, Convert.ToInt32(parsedHeight))
                    : 0;
                int widthCells = TryReadDouble(shape[1], out double parsedWidth)
                    ? Math.Max(0, Convert.ToInt32(parsedWidth))
                    : 0;
                return (heightCells, widthCells);
            }

            return (0, 0);
        }

        (int height, int width) = Read(primary);
        if (height > 0 && width > 0)
        {
            return (height, width);
        }

        return Read(secondary);
    }

    private static JsonObject? LoadDescriptorNode(string presetDirectory, string descriptorPath)
    {
        if (string.IsNullOrWhiteSpace(descriptorPath))
        {
            return null;
        }

        var store = new MapDocumentStore();
        return store.TryLoadRelative(presetDirectory, descriptorPath);
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

    private static string ReadString(JsonObject? node, string fallback, params string[] path)
    {
        if (node is null)
        {
            return fallback;
        }

        JsonNode? target = Walk(node, path);
        return target?.ToString() ?? fallback;
    }

    private static int ReadInt(JsonObject? node, int fallback, params string[] path)
    {
        if (node is null)
        {
            return fallback;
        }

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

    private static double ReadDouble(JsonObject? node, double fallback, params string[] path)
    {
        if (node is null)
        {
            return fallback;
        }

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

    private static string ResolveRelativePath(string sourcePath, string relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(relativeOrAbsolutePath))
        {
            return Path.GetFullPath(relativeOrAbsolutePath);
        }

        string baseDirectory = Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, relativeOrAbsolutePath));
    }

    private static JsonObject MergeObjects(JsonObject target, JsonObject overlay)
    {
        foreach ((string key, JsonNode? overlayValue) in overlay)
        {
            if (overlayValue is JsonObject overlayObject)
            {
                if (target[key] is JsonObject targetObject)
                {
                    target[key] = MergeObjects(targetObject, overlayObject);
                }
                else
                {
                    target[key] = overlayObject.DeepClone();
                }

                continue;
            }

            target[key] = overlayValue?.DeepClone();
        }

        return target;
    }
}
