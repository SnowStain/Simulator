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
    public string PresetName { get; set; } = "basicMap";

    public string DisplayName { get; set; } = "basicMap";

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

    [Description("Polygon points as x,y; x,y; x,y")]
    public string PointsText { get; set; } = string.Empty;

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
}

[TypeConverter(typeof(ExpandableObjectConverter))]
public sealed class TerrainFacetEditorModel
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = "slope";

    public string Team { get; set; } = "neutral";

    public string TopColorHex { get; set; } = "#8A9576";

    public string SideColorHex { get; set; } = "#4B4F55";

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
