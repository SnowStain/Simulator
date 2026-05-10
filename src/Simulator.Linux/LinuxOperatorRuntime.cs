using Simulator.Assets;
using Simulator.Core;
using Simulator.Runtime.Input;

namespace Simulator.Linux;

internal sealed class LinuxOperatorRuntime
{
    private readonly LinuxOperatorOptions _options;
    private readonly ConfigurationService _configurationService = new();
    private readonly AssetCatalogService _assetCatalogService = new();
    private readonly MapPresetService _mapPresetService = new();
    private ProjectLayout? _layout;
    private string _mapPreset = "unknown";
    private string _status = "Booting";
    private double _timeSec;
    private long _frame;

    public LinuxOperatorRuntime(LinuxOperatorOptions options)
    {
        _options = options;
    }

    public string MapPreset => _mapPreset;

    public string Status => _status;

    public double TimeSec => _timeSec;

    public long Frame => _frame;

    public bool CaptureMouse { get; private set; } = true;

    public void Load()
    {
        _layout = ProjectLayout.Discover();
        string configPath = _configurationService.ResolvePrimaryConfigPath(_layout);
        _mapPreset = string.IsNullOrWhiteSpace(_options.MapPreset)
            ? _mapPresetService.ResolvePresetName(_layout, _configurationService)
            : _options.MapPreset!;

        AssetCatalog catalog = _assetCatalogService.BuildCatalog(_layout);
        _status = catalog.IsComplete
            ? $"Ready: {_mapPreset}"
            : $"Ready with missing assets: {_mapPreset}";

        SimulatorRuntimeLog.Append(
            "linux_operator.log",
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} load root={_layout.RootPath} config={configPath} map={_mapPreset} catalog_complete={catalog.IsComplete}");
    }

    public void ApplyInput(GameInputSnapshot input)
    {
        if (input.PressedKeys.Contains(GameKey.Escape))
        {
            CaptureMouse = false;
        }
        else if (input.PressedMouseButtons.Contains(GameMouseButton.Left)
            && !input.DownKeys.Contains(GameKey.LeftAlt)
            && !input.DownKeys.Contains(GameKey.RightAlt))
        {
            CaptureMouse = true;
        }

        if (input.DownKeys.Contains(GameKey.LeftAlt) || input.DownKeys.Contains(GameKey.RightAlt))
        {
            CaptureMouse = false;
        }
    }

    public void Tick(double deltaSec)
    {
        _timeSec += Math.Max(0.0, deltaSec);
        _frame++;
    }
}
