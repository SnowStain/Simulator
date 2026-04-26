using LoadLargeTerrain;
using System.Diagnostics;

namespace Simulator.ThreeD;

internal static class LoadLargeTerrainInProcessLauncher
{
    public static void RunTerrainEditor(string mapPreset)
        => TerrainViewerApplication.RunMapPreset(mapPreset, startInTopDown: true, componentTestMode: false);

    public static void RunMapComponentTest(string mapPreset)
        => TerrainViewerApplication.RunMapPreset(mapPreset, startInTopDown: false, componentTestMode: true);

    public static void OpenTerrainEditorAsync(string mapPreset)
        => LaunchViewerProcess(mapPreset, componentTestMode: false);

    public static void OpenMapComponentTestAsync(string mapPreset)
        => LaunchViewerProcess(mapPreset, componentTestMode: true);

    private static void LaunchViewerProcess(string mapPreset, bool componentTestMode)
    {
        string preset = string.IsNullOrWhiteSpace(mapPreset) ? "rmuc2026" : mapPreset.Trim();
        string projectRoot = AppContext.BaseDirectory;
        for (int index = 0; index < 6; index++)
        {
            string solutionPath = Path.Combine(projectRoot, "Simulator.sln");
            if (File.Exists(solutionPath))
            {
                break;
            }

            string? parent = Directory.GetParent(projectRoot)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            projectRoot = parent;
        }

        string projectPath = Path.Combine(projectRoot, "src", "Simulator.LoadLargeTerrain", "LoadLargeTerrain.csproj");
        string arguments = componentTestMode
            ? $"run --project \"{projectPath}\" -- --map-preset \"{preset}\" --component-test-view"
            : $"run --project \"{projectPath}\" -- --map-preset \"{preset}\" --top-down";
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = projectRoot,
            UseShellExecute = true,
        };

        Process.Start(startInfo);
    }
}
