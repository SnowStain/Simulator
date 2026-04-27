using System.Drawing;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal static class TerrainSurfaceMapSupport
{
    private static readonly string[] PreferredTopImageNames =
    {
        "场地-俯视图.png",
        "俯视图.png",
        "top_view.png",
        "map_top.png",
    };

    public static bool UsesOrthographicPngTopSurface(TerrainSurfaceDefinition? terrainSurface)
    {
        return terrainSurface is not null
            && string.Equals(terrainSurface.MapType, "terrain_surface_map", StringComparison.OrdinalIgnoreCase)
            && string.Equals(terrainSurface.RenderProfile, "top_png_orthographic_side_solid", StringComparison.OrdinalIgnoreCase)
            && string.Equals(terrainSurface.TopFaceMode, "orthographic_png", StringComparison.OrdinalIgnoreCase);
    }

    public static bool UsesSolidTerrainWalls(TerrainSurfaceDefinition? terrainSurface)
    {
        return terrainSurface is not null
            && string.Equals(terrainSurface.MapType, "terrain_surface_map", StringComparison.OrdinalIgnoreCase)
            && string.Equals(terrainSurface.SideFaceMode, "solid_color", StringComparison.OrdinalIgnoreCase);
    }

    public static Color ResolveTerrainWallSolidColor(TerrainSurfaceDefinition? terrainSurface, Color fallback)
    {
        string? colorText = terrainSurface?.SideColorHex;
        if (!string.IsNullOrWhiteSpace(colorText))
        {
            try
            {
                return ColorTranslator.FromHtml(colorText);
            }
            catch
            {
            }
        }

        return fallback;
    }

    public static string? ResolveBaseColorBitmapPath(MapPresetDefinition preset)
    {
        string mapDirectory = Path.GetDirectoryName(preset.SourcePath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(mapDirectory) || !Directory.Exists(mapDirectory))
        {
            return null;
        }

        foreach (string? candidate in EnumerateImageCandidates(preset))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string fullPath = Path.IsPathRooted(candidate)
                ? candidate
                : Path.GetFullPath(Path.Combine(mapDirectory, candidate));
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return Directory.EnumerateFiles(mapDirectory, "*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path =>
            {
                string fileName = Path.GetFileName(path);
                return fileName.Contains("俯视", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("top", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("blank", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("map", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static IEnumerable<string?> EnumerateImageCandidates(MapPresetDefinition preset)
    {
        yield return preset.TerrainSurface?.BaseColorImagePath;
        yield return preset.ImagePath;

        foreach (string name in PreferredTopImageNames)
        {
            yield return name;
        }
    }
}
