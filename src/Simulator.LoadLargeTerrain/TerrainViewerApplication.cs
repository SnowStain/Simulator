using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace LoadLargeTerrain;

public static class TerrainViewerApplication
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        bool buildCacheOnly = args.Any(arg => string.Equals(arg, "--build-cache-only", StringComparison.OrdinalIgnoreCase));
        bool startInTopDown = args.Any(arg =>
            string.Equals(arg, "--top-down", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--topdown", StringComparison.OrdinalIgnoreCase));
        bool startInComponentTestView = args.Any(arg =>
            string.Equals(arg, "--component-test-view", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--component-test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--match-view", StringComparison.OrdinalIgnoreCase));
        MapPresetEditingSession? mapEditingSession = MapPresetEditingSession.TryCreateFromArgs(args);
        string modelPath = mapEditingSession?.ModelPath ?? ModelLocator.ResolveModelPath(args, "RMUC2026_MAP.glb");
        string cachePath = Path.ChangeExtension(modelPath, ".terraincache.lz4");
        string exportPath = Path.ChangeExtension(modelPath, ".component_roles.json");
        string annotationPath = mapEditingSession?.AnnotationPath ?? ResolveAnnotationPath(args, modelPath, exportPath);

        Console.WriteLine($"模型文件：{modelPath}");
        Console.WriteLine($"缓存文件：{cachePath}");

        TerrainSceneData scene = SceneCache.LoadOrBuild(modelPath, cachePath);
        if (buildCacheOnly)
        {
            Console.WriteLine(
                $"仅构建缓存完成。组件 {scene.Components.Count:N0}，分块 {scene.Chunks.Count:N0}，顶点 {scene.Chunks.Sum(chunk => chunk.Vertices.LongLength):N0}，索引 {scene.Chunks.Sum(chunk => chunk.Indices.LongLength):N0}");
            return 0;
        }

        ComponentAnnotationImporter.ImportedAnnotationData? importedAnnotations =
            ComponentAnnotationImporter.TryLoad(annotationPath, new WorldScale(scene.Bounds));
        if (importedAnnotations is not null)
        {
            Console.WriteLine($"已读取标注 JSON：{importedAnnotations.SourcePath}");
        }
        else
        {
            Console.WriteLine("未找到可读取的标注 JSON，将以空编辑状态启动。");
        }

        GameWindowSettings gameWindowSettings = GameWindowSettings.Default;
        NativeWindowSettings nativeWindowSettings = new()
        {
            Title = "LoadLargeTerrain",
            ClientSize = new Vector2i(1600, 900),
            APIVersion = new Version(4, 1),
            Flags = ContextFlags.ForwardCompatible,
        };

        using TerrainViewerWindow window = new(
            gameWindowSettings,
            nativeWindowSettings,
            scene,
            Path.GetFileName(modelPath),
            exportPath,
            importedAnnotations,
            mapEditingSession,
            startInTopDown || (mapEditingSession is not null && !startInComponentTestView),
            startInComponentTestView);
        window.Run();
        return 0;
    }

    public static int RunMapPreset(string mapPreset, bool startInTopDown, bool componentTestMode)
    {
        var args = new List<string>
        {
            "--map-preset",
            string.IsNullOrWhiteSpace(mapPreset) ? "rmuc2026" : mapPreset.Trim(),
        };

        if (componentTestMode)
        {
            args.Add("--component-test-view");
        }
        else if (startInTopDown)
        {
            args.Add("--top-down");
        }

        return Run(args.ToArray());
    }

    private static string ResolveAnnotationPath(string[] args, string modelPath, string defaultExportPath)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--annotations=", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(arg["--annotations=".Length..].Trim('"'));
            }

            if (string.Equals(arg, "--annotations", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return Path.GetFullPath(args[i + 1].Trim('"'));
            }
        }

        string? modelDirectory = Path.GetDirectoryName(modelPath);
        if (!string.IsNullOrWhiteSpace(modelDirectory))
        {
            string tryJsonPath = Path.Combine(modelDirectory, "try.json");
            if (File.Exists(tryJsonPath))
            {
                return tryJsonPath;
            }
        }

        return defaultExportPath;
    }
}
