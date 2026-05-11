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

    public bool CollisionEnabled { get; init; } = true;

    public double CollisionExpandM { get; init; }

    public double CollisionHeightOffsetM { get; init; }

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
        if (HasVolumeDefinition())
        {
            return ContainsVolumeProjection(x, y);
        }

        string normalizedShape = (Shape ?? "rect").Trim().ToLowerInvariant();
        return normalizedShape switch
        {
            "polygon" => ContainsPolygon(x, y),
            "line" => ContainsLine(x, y),
            _ => ContainsRect(x, y),
        };
    }

    public bool Contains(double x, double y, double heightM)
    {
        if (!HasVolumeDefinition())
        {
            return Contains(x, y);
        }

        if (!ContainsVolumeProjection(x, y))
        {
            return false;
        }

        double centerZ = ReadAdditionalDouble("center_z_m", ReadAdditionalDouble("collision_center_z_m", ReadAdditionalDouble("z_m", ResolveDefaultVolumeHeightM() * 0.5)));
        double height = ResolveVolumeHeightM();
        double bottom = ReadAdditionalDouble("bottom_m", ReadAdditionalDouble("collision_bottom_m", centerZ - height * 0.5));
        double top = ReadAdditionalDouble("top_m", ReadAdditionalDouble("collision_top_m", bottom + height));
        if (top < bottom)
        {
            (bottom, top) = (top, bottom);
        }

        return heightM >= bottom - 1e-6 && heightM <= top + 1e-6;
    }

    public bool BlocksMovement
    {
        get
        {
            if (TryReadAdditionalBoolean("blocks_movement", out bool explicitValue))
            {
                return explicitValue;
            }

            string type = Type ?? string.Empty;
            if (type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "supply", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "fort", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return HeightM > 0.25
                || type.Contains("collision", StringComparison.OrdinalIgnoreCase)
                || type.Contains("wall", StringComparison.OrdinalIgnoreCase)
                || type.Contains("barrier", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "base", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "outpost", StringComparison.OrdinalIgnoreCase);
        }
    }

    public double CollisionBottomM
        => ReadAdditionalDouble("collision_bottom_m", ReadAdditionalDouble("bottom_m", 0.0));

    public double CollisionHeightM
        => Math.Max(0.02, ReadAdditionalDouble("collision_height_m", ResolveVolumeHeightM()));

    public double CollisionTopM
        => ReadAdditionalDouble("collision_top_m", ReadAdditionalDouble("top_m", CollisionBottomM + CollisionHeightM));

    public double CollisionExpandM
        => ReadAdditionalDouble("collision_expand_m", 0.0);

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

        if (IsNearPolygonBoundary(x, y, Math.Max(1.0, Thickness * 0.08)))
        {
            return true;
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

    private bool IsNearPolygonBoundary(double x, double y, double tolerance)
    {
        double toleranceSquared = tolerance * tolerance;
        Point2D previous = Points[^1];
        foreach (Point2D current in Points)
        {
            double dx = current.X - previous.X;
            double dy = current.Y - previous.Y;
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared <= 1e-9
                ? 0.0
                : Math.Clamp(((x - previous.X) * dx + (y - previous.Y) * dy) / lengthSquared, 0.0, 1.0);
            double closestX = previous.X + dx * t;
            double closestY = previous.Y + dy * t;
            double distanceSquared = (x - closestX) * (x - closestX) + (y - closestY) * (y - closestY);
            if (distanceSquared <= toleranceSquared)
            {
                return true;
            }

            previous = current;
        }

        return false;
    }

    public bool HasVolumeDefinition()
        => AdditionalProperties is not null
            && (AdditionalProperties.ContainsKey("volume_shape")
                || AdditionalProperties.ContainsKey("center_x")
                || AdditionalProperties.ContainsKey("center_y")
                || AdditionalProperties.ContainsKey("size_x")
                || AdditionalProperties.ContainsKey("size_y")
                || AdditionalProperties.ContainsKey("radius"));

    public bool ContainsVolumeProjection(double x, double y)
        => ContainsVolumeProjectionCore(x, y, expandWorld: 0.0);

    public bool ContainsCollisionProjection(double x, double y, double metersPerWorldUnit)
        => HasVolumeDefinition()
            ? ContainsVolumeProjectionCore(x, y, Math.Max(0.0, CollisionExpandM) / Math.Max(1e-6, metersPerWorldUnit))
            : Contains(x, y);

    private bool ContainsVolumeProjectionCore(double x, double y, double expandWorld)
    {
        string volumeShape = ReadAdditionalString("volume_shape", Shape);
        double centerX = ReadAdditionalDouble("center_x", (X1 + X2) * 0.5);
        double centerY = ReadAdditionalDouble("center_y", (Y1 + Y2) * 0.5);
        double yawDeg = ReadAdditionalDouble("yaw_deg", ReadAdditionalDouble("yaw", 0.0));
        double dx = x - centerX;
        double dy = y - centerY;
        double yawRad = -yawDeg * Math.PI / 180.0;
        double localX = dx * Math.Cos(yawRad) - dy * Math.Sin(yawRad);
        double localY = dx * Math.Sin(yawRad) + dy * Math.Cos(yawRad);

        if (volumeShape.Contains("cylinder", StringComparison.OrdinalIgnoreCase)
            || volumeShape.Contains("circle", StringComparison.OrdinalIgnoreCase))
        {
            double radius = ReadAdditionalDouble(
                "radius",
                Math.Max(Math.Abs(X2 - X1), Math.Abs(Y2 - Y1)) * 0.5);
            double expandedRadius = Math.Max(0.01, radius + expandWorld);
            return localX * localX + localY * localY <= expandedRadius * expandedRadius;
        }

        double sizeX = ReadAdditionalDouble("size_x", Math.Abs(X2 - X1)) + expandWorld * 2.0;
        double sizeY = ReadAdditionalDouble("size_y", Math.Abs(Y2 - Y1)) + expandWorld * 2.0;
        return Math.Abs(localX) <= Math.Max(0.01, sizeX) * 0.5
            && Math.Abs(localY) <= Math.Max(0.01, sizeY) * 0.5;
    }

    private double ResolveDefaultVolumeHeightM()
        => Math.Max(0.05, HeightM);

    private double ResolveVolumeHeightM()
        => Math.Max(0.02, ReadAdditionalDouble("size_z_m", ReadAdditionalDouble("collision_height_m", ReadAdditionalDouble("height_m", ResolveDefaultVolumeHeightM()))));

    private string ReadAdditionalString(string key, string fallback)
    {
        if (AdditionalProperties is null
            || !AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return fallback;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : element.ToString();
    }

    private double ReadAdditionalDouble(string key, double fallback)
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

    private bool TryReadAdditionalBoolean(string key, out bool value)
    {
        value = false;
        if (AdditionalProperties is null
            || !AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && bool.TryParse(element.GetString(), out bool parsed))
        {
            value = parsed;
            return true;
        }

        return false;
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

    public string AnnotationPath { get; init; } = string.Empty;

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

    public string SourcePath { get; init; } = string.Empty;

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
