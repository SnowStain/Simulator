using System.Text.Json.Nodes;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Editors;

namespace LoadLargeTerrain;

internal sealed class MapPresetEditingSession
{
    private readonly TerrainEditorService _terrainEditorService;

    private MapPresetEditingSession(
        ProjectLayout layout,
        TerrainEditorService terrainEditorService,
        MapPresetEditorSettings document,
        string presetName,
        string modelPath,
        string annotationPath)
    {
        Layout = layout;
        _terrainEditorService = terrainEditorService;
        Document = document;
        PresetName = presetName;
        ModelPath = modelPath;
        AnnotationPath = annotationPath;
    }

    public ProjectLayout Layout { get; }

    public MapPresetEditorSettings Document { get; }

    public string PresetName { get; }

    public string ModelPath { get; }

    public string AnnotationPath { get; }

    public static MapPresetEditingSession? TryCreateFromArgs(string[] args)
    {
        string? presetName = null;
        string? mapJsonPath = null;
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--map-preset", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                presetName = args[++index];
                continue;
            }

            if (arg.StartsWith("--map-preset=", StringComparison.OrdinalIgnoreCase))
            {
                presetName = arg["--map-preset=".Length..];
                continue;
            }

            if (string.Equals(arg, "--map-json", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                mapJsonPath = args[++index];
                continue;
            }

            if (arg.StartsWith("--map-json=", StringComparison.OrdinalIgnoreCase))
            {
                mapJsonPath = arg["--map-json=".Length..];
            }
        }

        if (string.IsNullOrWhiteSpace(presetName) && string.IsNullOrWhiteSpace(mapJsonPath))
        {
            return null;
        }

        ProjectLayout layout = ProjectLayout.Discover();
        TerrainEditorService terrainEditorService = new(new ConfigurationService(), new AssetCatalogService());
        if (!string.IsNullOrWhiteSpace(mapJsonPath))
        {
            presetName = ResolvePresetNameFromMapJson(layout, mapJsonPath!) ?? Path.GetFileNameWithoutExtension(mapJsonPath);
        }

        if (string.IsNullOrWhiteSpace(presetName))
        {
            return null;
        }

        MapPresetEditorSettings document = terrainEditorService.LoadPresetDocument(layout, presetName);
        string annotationPath = ResolveAnnotationPath(document, layout);
        string modelPath = ResolveModelPath(document, annotationPath);
        return new MapPresetEditingSession(layout, terrainEditorService, document, presetName, modelPath, annotationPath);
    }

    public void SaveMapDocument()
    {
        _terrainEditorService.SavePresetDocument(Document);
    }

    private static string? ResolvePresetNameFromMapJson(ProjectLayout layout, string candidatePath)
    {
        string fullPath = Path.GetFullPath(candidatePath);
        string mapsRoot = layout.ResolvePath("maps");
        if (!fullPath.StartsWith(mapsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relative = Path.GetRelativePath(mapsRoot, fullPath);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Length > 0 ? segments[0] : null;
    }

    private static string ResolveAnnotationPath(MapPresetEditorSettings document, ProjectLayout layout)
    {
        string presetDirectory = Path.GetDirectoryName(document.SourcePath) ?? layout.RootPath;
        if (document.RawMap is JsonObject rawMap)
        {
            string? relative = rawMap["annotation_path"]?.ToString();
            if (!string.IsNullOrWhiteSpace(relative))
            {
                return Path.GetFullPath(Path.Combine(presetDirectory, relative));
            }
        }

        return Path.Combine(presetDirectory, "try.json");
    }

    private static string ResolveModelPath(MapPresetEditorSettings document, string annotationPath)
    {
        string annotationDirectory = Path.GetDirectoryName(annotationPath) ?? Path.GetDirectoryName(document.SourcePath) ?? Directory.GetCurrentDirectory();
        string[] candidates =
        [
            Path.Combine(annotationDirectory, "RMUC2026_MAP.glb"),
            Path.Combine(annotationDirectory, "rmuc2026_map.glb"),
            Path.Combine(annotationDirectory, "scene.glb"),
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        string? firstGlb = Directory.Exists(annotationDirectory)
            ? Directory.EnumerateFiles(annotationDirectory, "*.glb", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
        if (!string.IsNullOrWhiteSpace(firstGlb))
        {
            return Path.GetFullPath(firstGlb);
        }

        return ModelLocator.ResolveModelPath(Array.Empty<string>(), "RMUC2026_MAP.glb");
    }
}
