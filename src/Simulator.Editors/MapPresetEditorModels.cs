using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Simulator.Core.Map;

namespace Simulator.Editors;

[TypeConverter(typeof(ExpandableObjectConverter))]
public sealed class MapPresetEditorSettings
{
    [Browsable(false)]
    public string PresetName { get; set; } = "blankCanvas";

    public string DisplayName { get; set; } = "blankCanvas";

    [Browsable(false)]
    public string SourcePath { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public double FieldLengthM { get; set; } = 28.0;

    public double FieldWidthM { get; set; } = 15.0;

    public string ImagePath { get; set; } = string.Empty;

    [Browsable(false)]
    public JsonObject? RawRoot { get; set; }

    [Browsable(false)]
    public JsonObject? RawMap { get; set; }

    [Browsable(false)]
    public TerrainSurfaceEditorSettings TerrainSurface { get; set; } = new();

    [Browsable(false)]
    public BindingList<FacilityRegionEditorModel> Facilities { get; set; } = new();

    [Browsable(false)]
    public BindingList<TerrainFacetEditorModel> TerrainFacets { get; set; } = new();

    [Browsable(false)]
    public bool FacilitiesNormalizedToTopDownWorld { get; set; }

    public override string ToString() => $"{DisplayName} ({Width}x{Height})";
}

[TypeConverter(typeof(ExpandableObjectConverter))]
public sealed class TerrainSurfaceEditorSettings
{
    public string MapType { get; set; } = "terrain_surface_map";

    public string DescriptorPath { get; set; } = string.Empty;

    public string StorageKind { get; set; } = "runtime_triangle_grid";

    public string Topology { get; set; } = "triangle_grid";

    public string MergeMode { get; set; } = "merged_exposed_faces";

    public string SplitMode { get; set; } = "diag_forward";

    public string BaseColorImagePath { get; set; } = string.Empty;

    public string RenderProfile { get; set; } = "top_png_orthographic_side_solid";

    public string TopFaceMode { get; set; } = "orthographic_png";

    public string SideFaceMode { get; set; } = "solid_color";

    public string SideColorHex { get; set; } = "#4B4F55";

    public double TopNormalThreshold { get; set; } = 0.9;

    public double SideNormalThreshold { get; set; } = 0.1;

    public double ResolutionM { get; set; } = 0.01;

    public int HeightCells { get; set; }

    public int WidthCells { get; set; }

    public double HeightScaleBakedIn { get; set; } = 1.0;

    [Browsable(false)]
    public Dictionary<string, string> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString() => $"{RenderProfile} / {TopFaceMode} / {SideFaceMode}";
}

[TypeConverter(typeof(ExpandableObjectConverter))]
public sealed class FacilityRegionEditorModel
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = "unknown";

    public string Team { get; set; } = "neutral";

    public string Shape { get; set; } = "rect";

    public double X1 { get; set; }

    public double Y1 { get; set; }

    public double X2 { get; set; }

    public double Y2 { get; set; }

    public double Thickness { get; set; } = 12.0;

    public double HeightM { get; set; }

    [Category("Collision")]
    [DisplayName("Blocks Movement")]
    [Description("Whether this facility blocks robot movement.")]
    public bool BlocksMovement
    {
        get => TryGetAdditionalBoolean("blocks_movement", out bool value)
            ? value
            : HeightM > 0.25;
        set => SetAdditionalNode("blocks_movement", JsonValue.Create(value));
    }

    [Category("Collision")]
    [DisplayName("Expand M")]
    [Description("Horizontal collision expansion in meters; negative values shrink the collision volume.")]
    public double CollisionExpandM
    {
        get => GetAdditionalDouble("collision_expand_m", 0.0);
        set => SetAdditionalDouble("collision_expand_m", value, removeWhenZero: true);
    }

    [Category("Collision")]
    [DisplayName("Bottom M")]
    [Description("Collision bottom height in meters, relative to terrain/world ground.")]
    public double CollisionBottomM
    {
        get => GetAdditionalDouble("collision_bottom_m", 0.0);
        set => SetAdditionalDouble("collision_bottom_m", value, removeWhenZero: true);
    }

    [Category("Collision")]
    [DisplayName("Height M")]
    [Description("Collision volume height in meters. When unset, the runtime keeps the legacy full-height volume.")]
    public double CollisionHeightM
    {
        get => GetAdditionalDouble("collision_height_m", Math.Max(0.05, HeightM));
        set => SetAdditionalDouble("collision_height_m", Math.Max(0.02, value), removeWhenZero: false);
    }

    [Description("Polygon points as x,y; x,y; x,y")]
    public string PointsText { get; set; } = string.Empty;

    [Category("设施分组")]
    [DisplayName("整体设施")]
    [Description("启用后，当前区域会被当作一个整体设施来维护，可关联底层组合体与组件。")]
    public bool GroupAsFacility
    {
        get => TryGetAdditionalBoolean("group_as_facility", out bool value) && value;
        set
        {
            if (value)
            {
                SetAdditionalNode("group_as_facility", JsonValue.Create(true));
            }
            else
            {
                RemoveAdditionalKey("group_as_facility");
            }
        }
    }

    [Category("设施分组")]
    [DisplayName("组合体 ID")]
    [Description("使用逗号分隔的组合体 ID 列表。")]
    public string GroupCompositeIdsText
    {
        get => GetAdditionalIntegerListText("group_composite_ids");
        set => SetAdditionalIntegerListText("group_composite_ids", value);
    }

    [Category("设施分组")]
    [DisplayName("组件 ID")]
    [Description("使用逗号分隔的组件 ID 列表。")]
    public string GroupComponentIdsText
    {
        get => GetAdditionalIntegerListText("group_component_ids");
        set => SetAdditionalIntegerListText("group_component_ids", value);
    }

    [Category("设施分组")]
    [DisplayName("来源")]
    [Description("记录该整体设施的生成来源，例如 selection_box。")]
    public string GroupSource
    {
        get => GetAdditionalString("group_source");
        set => SetAdditionalString("group_source", value);
    }

    [Browsable(false)]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public override string ToString() => $"{Id} [{Type}/{Shape}]";

    public static FacilityRegionEditorModel FromFacility(FacilityRegion facility)
    {
        return new FacilityRegionEditorModel
        {
            Id = facility.Id,
            Type = facility.Type,
            Team = facility.Team,
            Shape = facility.Shape,
            X1 = facility.X1,
            Y1 = facility.Y1,
            X2 = facility.X2,
            Y2 = facility.Y2,
            Thickness = facility.Thickness,
            HeightM = facility.HeightM,
            PointsText = string.Join("; ", facility.Points.Select(point =>
                string.Create(CultureInfo.InvariantCulture, $"{point.X:0.###},{point.Y:0.###}"))),
            AdditionalProperties = facility.AdditionalProperties is null
                ? null
                : new Dictionary<string, JsonElement>(facility.AdditionalProperties, StringComparer.OrdinalIgnoreCase),
        };
    }

    public IReadOnlyList<Point2D> ParsePoints()
    {
        var points = new List<Point2D>();
        string raw = PointsText ?? string.Empty;
        foreach (string segment in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = segment.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (pair.Length < 2)
            {
                continue;
            }

            if (double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                points.Add(new Point2D(x, y));
            }
        }

        return points;
    }

    private bool TryGetAdditionalBoolean(string key, out bool value)
    {
        value = false;
        if (AdditionalProperties is null
            || !AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Number when element.TryGetInt32(out int numeric):
                value = numeric != 0;
                return true;
            case JsonValueKind.String:
                return bool.TryParse(element.GetString(), out value);
            default:
                return false;
        }
    }

    private string GetAdditionalString(string key)
    {
        if (AdditionalProperties is null
            || !AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return string.Empty;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.ToString();
    }

    private double GetAdditionalDouble(string key, double fallback)
    {
        if (AdditionalProperties is null
            || !AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double numeric))
        {
            return numeric;
        }

        return element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : fallback;
    }

    private string GetAdditionalIntegerListText(string key)
    {
        if (AdditionalProperties is null
            || !AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            List<string> values = new();
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int numeric))
                {
                    values.Add(numeric.ToString(CultureInfo.InvariantCulture));
                }
                else if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    values.Add(item.GetString()!);
                }
            }

            return string.Join(", ", values);
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.ToString();
    }

    private void SetAdditionalString(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            RemoveAdditionalKey(key);
            return;
        }

        SetAdditionalNode(key, JsonValue.Create(value.Trim()));
    }

    private void SetAdditionalDouble(string key, double value, bool removeWhenZero)
    {
        if (!double.IsFinite(value)
            || (removeWhenZero && Math.Abs(value) <= 1e-6))
        {
            RemoveAdditionalKey(key);
            return;
        }

        SetAdditionalNode(key, JsonValue.Create(Math.Round(value, 4)));
    }

    private void SetAdditionalIntegerListText(string key, string? raw)
    {
        string[] segments = (raw ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<int> values = new();
        foreach (string segment in segments)
        {
            if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                values.Add(parsed);
            }
        }

        if (values.Count == 0)
        {
            RemoveAdditionalKey(key);
            return;
        }

        JsonArray array = new();
        foreach (int value in values.Distinct())
        {
            array.Add(value);
        }

        SetAdditionalNode(key, array);
    }

    private void SetAdditionalNode(string key, JsonNode? node)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (node is null)
        {
            RemoveAdditionalKey(key);
            return;
        }

        AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        AdditionalProperties[key] = document.RootElement.Clone();
    }

    private void RemoveAdditionalKey(string key)
    {
        if (AdditionalProperties is null)
        {
            return;
        }

        AdditionalProperties.Remove(key);
        if (AdditionalProperties.Count == 0)
        {
            AdditionalProperties = null;
        }
    }
}

[TypeConverter(typeof(ExpandableObjectConverter))]
public sealed class TerrainFacetEditorModel
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = "slope";

    public string Team { get; set; } = "neutral";

    public string TopColorHex { get; set; } = "#8A9576";

    public string SideColorHex { get; set; } = "#4B4F55";

    [Description("Whether this facet participates in terrain collision sampling.")]
    public bool CollisionEnabled { get; set; } = true;

    [Description("Horizontal collision expansion in meters, applied around the facet centroid.")]
    public double CollisionExpandM { get; set; }

    [Description("Additional collision height offset in meters, without changing the visual mesh.")]
    public double CollisionHeightOffsetM { get; set; }

    [Description("Facet points as x,y; x,y; x,y")]
    public string PointsText { get; set; } = "0,0; 100,0; 100,60; 0,60";

    [Description("Per-vertex heights in meters, e.g. 0,0,0.4,0.4")]
    public string HeightsText { get; set; } = "0,0,0.4,0.4";

    public override string ToString() => $"{Id} [{Type}]";

    public static TerrainFacetEditorModel FromFacet(TerrainFacetDefinition facet)
    {
        return new TerrainFacetEditorModel
        {
            Id = facet.Id,
            Type = facet.Type,
            Team = facet.Team,
            TopColorHex = facet.TopColorHex,
            SideColorHex = facet.SideColorHex,
            CollisionEnabled = facet.CollisionEnabled,
            CollisionExpandM = facet.CollisionExpandM,
            CollisionHeightOffsetM = facet.CollisionHeightOffsetM,
            PointsText = string.Join("; ", facet.Points.Select(point =>
                string.Create(CultureInfo.InvariantCulture, $"{point.X:0.###},{point.Y:0.###}"))),
            HeightsText = string.Join(",", facet.HeightsM.Select(height =>
                string.Create(CultureInfo.InvariantCulture, $"{height:0.###}"))),
        };
    }

    public IReadOnlyList<Point2D> ParsePoints()
    {
        var points = new List<Point2D>();
        foreach (string segment in (PointsText ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = segment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pair.Length < 2)
            {
                continue;
            }

            if (double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                points.Add(new Point2D(x, y));
            }
        }

        return points;
    }

    public IReadOnlyList<double> ParseHeights()
    {
        var heights = new List<double>();
        foreach (string segment in (HeightsText ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(segment, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                heights.Add(value);
            }
        }

        return heights;
    }
}
