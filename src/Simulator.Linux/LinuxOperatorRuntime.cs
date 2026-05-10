using Simulator.Assets;
using Simulator.Core;
using Simulator.Platform.Ui;
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
    private bool _captureMouse = true;

    public LinuxOperatorRuntime(LinuxOperatorOptions options)
    {
        _options = options;
    }

    public string MapPreset => _mapPreset;

    public string Status => _status;

    public double TimeSec => _timeSec;

    public long Frame => _frame;

    public bool OperatorPanelOpen { get; private set; }

    public OpenGkRefereePanelPage LocalPanelPage { get; private set; } = OpenGkRefereePanelPage.Main;

    public bool CaptureMouse => _captureMouse && !OperatorPanelOpen;

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
        if (input.PressedKeys.Contains(GameKey.O))
        {
            OperatorPanelOpen = !OperatorPanelOpen;
            if (OperatorPanelOpen)
            {
                _captureMouse = false;
            }
        }

        if (input.PressedKeys.Contains(GameKey.Escape))
        {
            _captureMouse = false;
        }
        else if (input.PressedMouseButtons.Contains(GameMouseButton.Left)
            && !OperatorPanelOpen
            && !input.DownKeys.Contains(GameKey.LeftAlt)
            && !input.DownKeys.Contains(GameKey.RightAlt))
        {
            _captureMouse = true;
        }

        if (input.DownKeys.Contains(GameKey.LeftAlt) || input.DownKeys.Contains(GameKey.RightAlt))
        {
            _captureMouse = false;
        }
    }

    public void ApplyUiAction(string action)
    {
        if (string.Equals(action, "linux:release_mouse", StringComparison.OrdinalIgnoreCase))
        {
            _captureMouse = false;
        }
        else if (string.Equals(action, "linux:capture_mouse", StringComparison.OrdinalIgnoreCase))
        {
            _captureMouse = true;
            OperatorPanelOpen = false;
        }
        else if (string.Equals(action, "local_close", StringComparison.OrdinalIgnoreCase))
        {
            OperatorPanelOpen = false;
        }
        else if (string.Equals(action, "local_return", StringComparison.OrdinalIgnoreCase))
        {
            OperatorPanelOpen = false;
            _captureMouse = false;
        }
        else if (action.StartsWith("local_page:", StringComparison.OrdinalIgnoreCase))
        {
            LocalPanelPage = action.EndsWith(":energy", StringComparison.OrdinalIgnoreCase)
                ? OpenGkRefereePanelPage.Energy
                : OpenGkRefereePanelPage.Main;
        }

        SimulatorRuntimeLog.Append(
            "linux_operator.log",
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ui action={action} frame={_frame}");
    }

    public void Tick(double deltaSec)
    {
        _timeSec += Math.Max(0.0, deltaSec);
        _frame++;
    }
}
