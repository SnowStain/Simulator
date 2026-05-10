using Simulator.Assets;
using Simulator.Core;
using Simulator.Runtime.Input;

namespace Simulator.Linux;

internal static class LinuxOperatorDiagnostics
{
    public static int Run(LinuxOperatorOptions options)
    {
        try
        {
            ProjectLayout layout = ProjectLayout.Discover();
            var configuration = new ConfigurationService();
            var assets = new AssetCatalogService();
            var mapPresets = new MapPresetService();
            string configPath = configuration.ResolvePrimaryConfigPath(layout);
            string preset = string.IsNullOrWhiteSpace(options.MapPreset)
                ? mapPresets.ResolvePresetName(layout, configuration)
                : options.MapPreset!;
            string mapPath = mapPresets.ResolveMapPresetPath(layout, preset);
            AssetCatalog catalog = assets.BuildCatalog(layout);
            GameInputSnapshot input = BuildInputProbe();
            bool rulesOk = CountFiles(layout.ResolvePath("rules")) > 0
                || CountFiles(layout.ResolvePath("规则")) > 0
                || CountFiles(layout.ResolvePath("瑙勫垯")) > 0;
            bool essentialAssetsOk = Directory.Exists(layout.ResolvePath("maps"))
                && CountFiles(layout.AppearanceProfileDirectoryPath) > 0
                && rulesOk;

            var checks = new List<(string Name, bool Ok, string Detail)>
            {
                ("root", Directory.Exists(layout.RootPath), layout.RootPath),
                ("config", File.Exists(configPath), Path.GetRelativePath(layout.RootPath, configPath)),
                ("map_preset", File.Exists(mapPath), Path.GetRelativePath(layout.RootPath, mapPath)),
                ("appearance", File.Exists(layout.AppearancePresetPath), Path.GetRelativePath(layout.RootPath, layout.AppearancePresetPath)),
                ("appearance_profiles", Directory.Exists(layout.AppearanceProfileDirectoryPath), Path.GetRelativePath(layout.RootPath, layout.AppearanceProfileDirectoryPath)),
                ("essential_assets", essentialAssetsOk, $"maps={Directory.Exists(layout.ResolvePath("maps"))} rules={rulesOk} files={catalog.TotalFileCount}"),
                ("input_snapshot", input.PressedKeys.Contains(GameKey.W) && input.DownMouseButtons.Contains(GameMouseButton.Left), $"frame={input.Frame} pressed={input.PressedKeys.Count} mouse={input.DownMouseButtons.Count}"),
            };

            foreach ((string name, bool ok, string detail) in checks)
            {
                Console.WriteLine($"{(ok ? "OK" : "FAIL")} {name}: {detail}");
            }

            foreach (AssetCategoryStatus category in catalog.Categories)
            {
                Console.WriteLine($"CAT {(category.Exists && category.FileCount > 0 ? "OK" : "WARN")} {category.Name}: files={category.FileCount}");
            }

            bool success = checks.All(check => check.Ok);
            SimulatorRuntimeLog.Append(
                "linux_operator.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} diagnostics map={preset} success={(success ? 1 : 0)} files={catalog.TotalFileCount}");
            return success ? 0 : 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine($"FAIL diagnostics: {exception.Message}");
            SimulatorRuntimeLog.Append("linux_operator.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} diagnostics_failed {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static GameInputSnapshot BuildInputProbe()
    {
        var accumulator = new GameInputSnapshotAccumulator();
        GamePointerState pointer = accumulator.BuildPointer(100, 120, 0, cursorCaptured: true);
        return accumulator.CaptureState(
            0.25,
            new[] { GameKey.W, GameKey.LeftShift },
            new[] { GameMouseButton.Left },
            pointer);
    }

    private static int CountFiles(string path)
        => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count()
            : 0;
}
