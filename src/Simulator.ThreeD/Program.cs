using System.Windows.Forms;
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

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (string.IsNullOrWhiteSpace(options.OpenEditor))
        {
            SimulatorOpenTkApplication.Run(options);
            return;
        }

        Form form = CreateEntryForm(options);
        Application.Run(form);
    }

    private static Form CreateEntryForm(Simulator3dOptions options)
    {
        return options.OpenEditor switch
        {
            "appearance" => new AppearanceEditorForm(),
            "rules" => new RuleEditorForm(),
            "behavior" => new BehaviorEditorForm(),
            "functional" => new FunctionalEditorForm(),
            _ => new Simulator3dForm(options),
        };
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
