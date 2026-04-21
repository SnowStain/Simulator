using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Simulator.Core.Map;

public readonly record struct Point2D(double X, double Y);

public sealed class TerrainFacetDefinition
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = "slope";

    public string Team { get; init; } = "neutral";

    public string TopColorHex { get; init; } = "#8A9576";

    public string SideColorHex { get; init; } = "#4B4F55";

    public IReadOnlyList<Point2D> Points { get; init; } = Array.Empty<Point2D>();

    public IReadOnlyList<double> HeightsM { get; init; } = Array.Empty<double>();
}

public sealed class FacilityRegion
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = "unknown";

    public string Team { get; init; } = "neutral";

    public string Shape { get; init; } = "rect";

    public double X1 { get; init; }

    public double Y1 { get; init; }

    public double X2 { get; init; }

    public double Y2 { get; init; }

    public double Thickness { get; init; } = 12.0;

    public double HeightM { get; init; }

    public IReadOnlyList<Point2D> Points { get; init; } = Array.Empty<Point2D>();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    public bool Contains(double x, double y)
    {
        string normalizedShape = (Shape ?? "rect").Trim().ToLowerInvariant();
        return normalizedShape switch
        {
            "polygon" => ContainsPolygon(x, y),
            "line" => ContainsLine(x, y),
            _ => ContainsRect(x, y),
        };
    }

    public override string ToString()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Id} ({Type}, team={Team}, shape={Shape})");
    }

    private bool ContainsRect(double x, double y)
    {
        double minX = Math.Min(X1, X2);
        double maxX = Math.Max(X1, X2);
        double minY = Math.Min(Y1, Y2);
        double maxY = Math.Max(Y1, Y2);
        return x >= minX && x <= maxX && y >= minY && y <= maxY;
    }

    private bool ContainsLine(double x, double y)
    {
        double dx = X2 - X1;
        double dy = Y2 - Y1;
        double lineLengthSquared = dx * dx + dy * dy;
        if (lineLengthSquared <= 1e-6)
        {
            return Math.Sqrt((x - X1) * (x - X1) + (y - Y1) * (y - Y1)) <= Math.Max(Thickness, 1.0);
        }

        double t = ((x - X1) * dx + (y - Y1) * dy) / lineLengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);
        double closestX = X1 + t * dx;
        double closestY = Y1 + t * dy;
        return Math.Sqrt((x - closestX) * (x - closestX) + (y - closestY) * (y - closestY)) <= Math.Max(Thickness, 1.0);
    }

    private bool ContainsPolygon(double x, double y)
    {
        if (Points.Count < 3)
        {
            return ContainsRect(x, y);
        }

        bool inside = false;
        Point2D previous = Points[^1];
        foreach (Point2D current in Points)
        {
            bool intersects =
                ((current.Y > y) != (previous.Y > y))
                && (x < (previous.X - current.X) * (y - current.Y) / Math.Max(previous.Y - current.Y, 1e-9) + current.X);
            if (intersects)
            {
                inside = !inside;
            }

            previous = current;
        }

        return inside;
    }
}

public sealed class MapPresetDefinition
{
    public string Name { get; init; } = "unknown";

    public int Width { get; init; }

    public int Height { get; init; }

    public double FieldLengthM { get; init; } = 28.0;

    public double FieldWidthM { get; init; } = 15.0;

    public string ImagePath { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public IReadOnlyList<FacilityRegion> Facilities { get; init; } = Array.Empty<FacilityRegion>();

    public MapCoordinateSystemDefinition CoordinateSystem { get; init; } = new();

    public TerrainSurfaceDefinition? TerrainSurface { get; init; }

    public RuntimeGridDefinition? RuntimeGrid { get; init; }
}

public sealed class MapCoordinateSystemDefinition
{
    public string CoordinateSpace { get; init; } = "world";

    public string Unit { get; init; } = "px";

    public double OriginX { get; init; }

    public double OriginY { get; init; }

    public double FieldLengthM { get; init; } = 28.0;

    public double FieldWidthM { get; init; } = 15.0;
}

public sealed class TerrainSurfaceDefinition
{
    public string MapType { get; init; } = "terrain_surface_map";

    public string DescriptorPath { get; init; } = string.Empty;

    public string StorageKind { get; init; } = "runtime_triangle_grid";

    public string Topology { get; init; } = "triangle_grid";

    public string MergeMode { get; init; } = "merged_exposed_faces";

    public string SplitMode { get; init; } = "diag_forward";

    public string BaseColorImagePath { get; init; } = string.Empty;

    public string RenderProfile { get; init; } = "top_png_orthographic_side_solid";

    public string TopFaceMode { get; init; } = "orthographic_png";

    public string SideFaceMode { get; init; } = "solid_color";

    public string SideColorHex { get; init; } = "#4B4F55";

    public double TopNormalThreshold { get; init; } = 0.9;

    public double SideNormalThreshold { get; init; } = 0.1;

    public double ResolutionM { get; init; } = 0.01;

    public int HeightCells { get; init; }

    public int WidthCells { get; init; }

    public double HeightScaleBakedIn { get; init; } = 1.0;

    public IReadOnlyDictionary<string, string> Channels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TerrainFacetDefinition> Facets { get; init; } = Array.Empty<TerrainFacetDefinition>();
}

public sealed class RuntimeGridDefinition
{
    public double ResolutionM { get; init; } = 0.01;

    public int HeightCells { get; init; }

    public int WidthCells { get; init; }

    public double HeightScaleBakedIn { get; init; } = 1.0;

    public string DescriptorPath { get; init; } = string.Empty;

    public string StorageKind { get; init; } = "runtime_triangle_grid";

    public string SurfaceTopology { get; init; } = "triangle_grid";

    public string SurfaceMergeMode { get; init; } = "merged_exposed_faces";

    public string SurfaceSplitMode { get; init; } = "diag_forward";

    public string RenderProfile { get; init; } = "top_png_orthographic_side_solid";

    public string TopFaceMode { get; init; } = "orthographic_png";

    public string SideFaceMode { get; init; } = "solid_color";

    public string SideColorHex { get; init; } = "#4B4F55";

    public IReadOnlyDictionary<string, string> Channels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
