using Simulator.Assets;
using Simulator.Core;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Simulator3dOptions options = Simulator3dOptions.Parse(args);
        if (TryRunLoadLargeTerrainEntry(options))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.OpenEditor))
        {
            SimulatorOpenTkApplication.Run(options);
            return;
        }

        switch (options.OpenEditor)
        {
            case "terrain":
            case "map_component_test":
                return;
            default:
                SimulatorOpenTkApplication.Run(options);
                return;
        }
    }

    private static bool TryRunLoadLargeTerrainEntry(Simulator3dOptions options)
    {
        string? openEditor = options.OpenEditor;
        if (!string.Equals(openEditor, "terrain", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(openEditor, "map_component_test", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ProjectLayout layout = ProjectLayout.Discover();
        TerrainEditorService service = new(new ConfigurationService(), new AssetCatalogService());
        string presetName = string.IsNullOrWhiteSpace(options.MapPreset)
            ? service.GetActiveMapPreset(layout)
            : options.MapPreset!;

        if (string.Equals(openEditor, "map_component_test", StringComparison.OrdinalIgnoreCase))
        {
            LoadLargeTerrainInProcessLauncher.RunMapComponentTest(presetName);
        }
        else
        {
            LoadLargeTerrainInProcessLauncher.RunTerrainEditor(presetName);
        }

        return true;
    }
}
