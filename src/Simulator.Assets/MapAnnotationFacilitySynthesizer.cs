using System.Text.Json;
using Simulator.Core.Map;

namespace Simulator.Assets;

internal static class MapAnnotationFacilitySynthesizer
{
    private const string RedTeamKeyword = "\u7ea2\u65b9";
    private const string BlueTeamKeyword = "\u84dd\u65b9";
    private const string EnergyKeyword = "\u80fd\u91cf\u673a\u5173";
    private const string OutpostRotatingKeyword = "\u524d\u54e8\u7ad9\u65cb\u8f6c\u88c5\u7532\u677f";
    private const string BaseTopKeyword = "\u57fa\u5730\u9876\u90e8\u4ea4\u4e92\u7ec4\u4ef6";

    private const double OutpostBodyWidthM = 0.65;
    private const double BaseBodyLengthM = 1.881;
    private const double BaseBodyWidthM = 1.609;
    private const double BaseTopForwardOffsetM = BaseBodyLengthM * 0.06;
    private const double EnergyRepresentativeRadiusM = 1.45;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<FacilityRegion> CreateMissingFacilities(
        string annotationPath,
        IReadOnlyList<FacilityRegion> existingFacilities,
        int width,
        int height,
        double fieldLengthM,
        double fieldWidthM)
    {
        if (string.IsNullOrWhiteSpace(annotationPath) || !File.Exists(annotationPath))
        {
            return Array.Empty<FacilityRegion>();
        }

        AnnotationFile? file;
        using (FileStream stream = File.OpenRead(annotationPath))
        {
            file = JsonSerializer.Deserialize<AnnotationFile>(stream, JsonOptions);
        }

        if (file?.Composites is null || file.Composites.Length == 0 || width <= 0 || height <= 0)
        {
            return Array.Empty<FacilityRegion>();
        }

        double metersPerWorldUnit = ResolveMetersPerWorldUnit(width, height, fieldLengthM, fieldWidthM);
        if (metersPerWorldUnit <= 1e-9)
        {
            return Array.Empty<FacilityRegion>();
        }

        HashSet<string> existingKeys = BuildExistingKeySet(existingFacilities);
        var synthesized = new List<FacilityRegion>(5);
        var energyCenters = new List<(double X, double Y)>(2);

        foreach (CompositeAnnotation composite in file.Composites)
        {
            string name = composite.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (name.Contains(EnergyKeyword, StringComparison.Ordinal))
            {
                (double worldX, double worldY) = ResolveWorldPoint(composite.PositionMeters, fieldLengthM, fieldWidthM, metersPerWorldUnit);
                energyCenters.Add((worldX, worldY));
                continue;
            }

            if (name.Contains(OutpostRotatingKeyword, StringComparison.Ordinal)
                && TryResolveTeam(name, out string outpostTeam)
                && existingKeys.Add($"outpost|{outpostTeam}"))
            {
                (double pivotWorldX, double pivotWorldY) = ResolveCompositeWorldPoint(
                    composite.PivotModel,
                    file.WorldScale,
                    file.Components,
                    fieldLengthM,
                    fieldWidthM,
                    metersPerWorldUnit);
                synthesized.Add(CreateRectFacility(
                    $"{outpostTeam}_outpost",
                    "outpost",
                    outpostTeam,
                    pivotWorldX,
                    pivotWorldY,
                    OutpostBodyWidthM,
                    OutpostBodyWidthM,
                    metersPerWorldUnit,
                    bodyHeightM: 1.578,
                    bodyWidthM: 0.65,
                    bodyLengthM: 0.65));
                continue;
            }

            if (name.Contains(BaseTopKeyword, StringComparison.Ordinal)
                && TryResolveTeam(name, out string baseTeam)
                && existingKeys.Add($"base|{baseTeam}"))
            {
                (double topWorldX, double topWorldY) = ResolveWorldPoint(composite.PositionMeters, fieldLengthM, fieldWidthM, metersPerWorldUnit);
                double direction = string.Equals(baseTeam, "blue", StringComparison.OrdinalIgnoreCase) ? -1.0 : 1.0;
                double centerWorldX = topWorldX - direction * (BaseTopForwardOffsetM / metersPerWorldUnit);
                synthesized.Add(CreateRectFacility(
                    $"{baseTeam}_base",
                    "base",
                    baseTeam,
                    centerWorldX,
                    topWorldY,
                    BaseBodyLengthM,
                    BaseBodyWidthM,
                    metersPerWorldUnit,
                    bodyHeightM: 1.181,
                    bodyWidthM: 1.609,
                    bodyLengthM: 1.881));
            }
        }

        if (energyCenters.Count > 0 && existingKeys.Add("energy_mechanism|neutral"))
        {
            double centerX = energyCenters.Average(item => item.X);
            double centerY = energyCenters.Average(item => item.Y);
            synthesized.Add(CreateRectFacility(
                "annotated_energy_mechanism",
                "energy_mechanism",
                "neutral",
                centerX,
                centerY,
                EnergyRepresentativeRadiusM * 2.0,
                EnergyRepresentativeRadiusM * 2.0,
                metersPerWorldUnit,
                bodyHeightM: 2.30,
                bodyWidthM: 1.30,
                bodyLengthM: 2.06));
        }

        return synthesized;
    }

    private static HashSet<string> BuildExistingKeySet(IReadOnlyList<FacilityRegion> facilities)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (FacilityRegion facility in facilities)
        {
            string type = (facility.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (type is not ("base" or "outpost" or "energy_mechanism"))
            {
                continue;
            }

            string team = string.Equals(type, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
                ? "neutral"
                : string.IsNullOrWhiteSpace(facility.Team) ? "neutral" : facility.Team;
            keys.Add($"{type}|{team}");
        }

        return keys;
    }

    private static FacilityRegion CreateRectFacility(
        string id,
        string type,
        string team,
        double centerWorldX,
        double centerWorldY,
        double sizeXM,
        double sizeYM,
        double metersPerWorldUnit,
        double bodyHeightM,
        double bodyWidthM,
        double bodyLengthM)
    {
        double halfWidthWorld = Math.Max(0.01, sizeXM * 0.5 / metersPerWorldUnit);
        double halfHeightWorld = Math.Max(0.01, sizeYM * 0.5 / metersPerWorldUnit);
        return new FacilityRegion
        {
            Id = id,
            Type = type,
            Team = team,
            Shape = "rect",
            X1 = centerWorldX - halfWidthWorld,
            X2 = centerWorldX + halfWidthWorld,
            Y1 = centerWorldY - halfHeightWorld,
            Y2 = centerWorldY + halfHeightWorld,
            HeightM = bodyHeightM,
            AdditionalProperties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["body_height_m"] = JsonDocument.Parse(bodyHeightM.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
                ["body_width_m"] = JsonDocument.Parse(bodyWidthM.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
                ["body_length_m"] = JsonDocument.Parse(bodyLengthM.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
            },
        };
    }

    private static bool TryResolveTeam(string name, out string team)
    {
        if (name.Contains(RedTeamKeyword, StringComparison.Ordinal))
        {
            team = "red";
            return true;
        }

        if (name.Contains(BlueTeamKeyword, StringComparison.Ordinal))
        {
            team = "blue";
            return true;
        }

        team = string.Empty;
        return false;
    }

    private static (double X, double Y) ResolveWorldPoint(
        Vector3Annotation point,
        double fieldLengthM,
        double fieldWidthM,
        double metersPerWorldUnit)
    {
        return (
            (fieldLengthM * 0.5 - point.X) / metersPerWorldUnit,
            (fieldWidthM * 0.5 - point.Z) / metersPerWorldUnit);
    }

    private static (double X, double Y) ResolveCompositeWorldPoint(
        Vector3Annotation modelPoint,
        WorldScaleAnnotation? worldScale,
        ComponentAnnotation[]? components,
        double fieldLengthM,
        double fieldWidthM,
        double metersPerWorldUnit)
    {
        Vector3Annotation modelCenter = ResolveModelCenter(components);
        double xMetersPerModelUnit = worldScale?.XMetersPerModelUnit ?? 1.0;
        double zMetersPerModelUnit = worldScale?.ZMetersPerModelUnit ?? 1.0;
        double centeredX = (modelPoint.X - modelCenter.X) * xMetersPerModelUnit;
        double centeredZ = (modelPoint.Z - modelCenter.Z) * zMetersPerModelUnit;
        return (
            (fieldLengthM * 0.5 - centeredX) / metersPerWorldUnit,
            (fieldWidthM * 0.5 - centeredZ) / metersPerWorldUnit);
    }

    private static Vector3Annotation ResolveModelCenter(ComponentAnnotation[]? components)
    {
        if (components is null || components.Length == 0)
        {
            return new Vector3Annotation();
        }

        bool initialized = false;
        double minX = 0d;
        double minY = 0d;
        double minZ = 0d;
        double maxX = 0d;
        double maxY = 0d;
        double maxZ = 0d;
        foreach (ComponentAnnotation component in components)
        {
            if (component.Bounds.Min.Length < 3 || component.Bounds.Max.Length < 3)
            {
                continue;
            }

            if (!initialized)
            {
                minX = component.Bounds.Min[0];
                minY = component.Bounds.Min[1];
                minZ = component.Bounds.Min[2];
                maxX = component.Bounds.Max[0];
                maxY = component.Bounds.Max[1];
                maxZ = component.Bounds.Max[2];
                initialized = true;
                continue;
            }

            minX = Math.Min(minX, component.Bounds.Min[0]);
            minY = Math.Min(minY, component.Bounds.Min[1]);
            minZ = Math.Min(minZ, component.Bounds.Min[2]);
            maxX = Math.Max(maxX, component.Bounds.Max[0]);
            maxY = Math.Max(maxY, component.Bounds.Max[1]);
            maxZ = Math.Max(maxZ, component.Bounds.Max[2]);
        }

        if (!initialized)
        {
            return new Vector3Annotation();
        }

        return new Vector3Annotation
        {
            X = (minX + maxX) * 0.5,
            Y = (minY + maxY) * 0.5,
            Z = (minZ + maxZ) * 0.5,
        };
    }

    private static double ResolveMetersPerWorldUnit(int width, int height, double fieldLengthM, double fieldWidthM)
    {
        double scaleFromLength = fieldLengthM > 0 && width > 0 ? fieldLengthM / width : 0.0;
        double scaleFromWidth = fieldWidthM > 0 && height > 0 ? fieldWidthM / height : 0.0;
        if (scaleFromLength > 0 && scaleFromWidth > 0)
        {
            return (scaleFromLength + scaleFromWidth) * 0.5;
        }

        return Math.Max(scaleFromLength, scaleFromWidth);
    }

    private sealed class AnnotationFile
    {
        public WorldScaleAnnotation? WorldScale { get; init; }

        public CompositeAnnotation[]? Composites { get; init; }

        public ComponentAnnotation[]? Components { get; init; }
    }

    private sealed class WorldScaleAnnotation
    {
        public double XMetersPerModelUnit { get; init; }

        public double ZMetersPerModelUnit { get; init; }
    }

    private sealed class CompositeAnnotation
    {
        public string? Name { get; init; }

        public Vector3Annotation PositionMeters { get; init; } = new();

        public Vector3Annotation PivotModel { get; init; } = new();
    }

    private sealed class ComponentAnnotation
    {
        public BoundsAnnotation Bounds { get; init; } = new();
    }

    private sealed class BoundsAnnotation
    {
        public double[] Min { get; init; } = Array.Empty<double>();

        public double[] Max { get; init; } = Array.Empty<double>();
    }

    private sealed class Vector3Annotation
    {
        public double X { get; init; }

        public double Y { get; init; }

        public double Z { get; init; }
    }
}
