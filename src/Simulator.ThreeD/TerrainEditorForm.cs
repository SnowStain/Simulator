using System.Diagnostics;
using System.Windows.Forms;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal sealed class TerrainEditorForm : Form
{
    private readonly ProjectLayout _layout;
    private readonly ConfigurationService _configService;
    private readonly TerrainEditorService _service;
    private readonly ListBox _presetList = new();
    private readonly Label _currentLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _hintLabel = new();

    public TerrainEditorForm()
    {
        _layout = ProjectLayout.Discover();
        _configService = new ConfigurationService();
        _service = new TerrainEditorService(_configService, new AssetCatalogService());

        Text = "Terrain Editor";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new System.Drawing.Size(620, 520);
        MinimumSize = new System.Drawing.Size(540, 420);

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

        _currentLabel.Dock = DockStyle.Top;
        _currentLabel.Height = 26;

        _hintLabel.Dock = DockStyle.Top;
        _hintLabel.Height = 58;
        _hintLabel.Text = "For full terrain painting, height editing, facility editing, and blankCanvas synchronization, launch the full terrain editor.";
        _hintLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

        _presetList.Dock = DockStyle.Fill;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
        };

        var reload = new Button { Text = "Reload", Width = 88, Height = 28 };
        reload.Click += (_, _) => ReloadPresets();

        var apply = new Button { Text = "Apply Preset", Width = 110, Height = 28 };
        apply.Click += (_, _) => ApplySelectedPreset();

        var launchFull = new Button { Text = "Launch Full Editor", Width = 138, Height = 28 };
        launchFull.Click += (_, _) => LaunchFullTerrainEditor();

        buttons.Controls.Add(reload);
        buttons.Controls.Add(apply);
        buttons.Controls.Add(launchFull);

        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Height = 24;

        panel.Controls.Add(_presetList);
        panel.Controls.Add(_hintLabel);
        panel.Controls.Add(_currentLabel);
        panel.Controls.Add(_statusLabel);
        panel.Controls.Add(buttons);
        Controls.Add(panel);

        ReloadPresets();
    }

    private void ReloadPresets()
    {
        _presetList.Items.Clear();
        foreach (string preset in _service.ListMapPresets(_layout))
        {
            _presetList.Items.Add(preset);
        }

        string configPath = _configService.ResolvePrimaryConfigPath(_layout);
        string currentPreset = _configService.GetMapPreset(_configService.LoadConfig(configPath));
        _currentLabel.Text = $"Current preset: {currentPreset}";

        int idx = _presetList.Items.IndexOf(currentPreset);
        if (idx >= 0)
        {
            _presetList.SelectedIndex = idx;
        }

        _statusLabel.Text = "Preset list loaded.";
    }

    private void ApplySelectedPreset()
    {
        string? preset = _presetList.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(preset))
        {
            _statusLabel.Text = "Select a preset first.";
            return;
        }

        try
        {
            IReadOnlyList<string> files = _service.SetActiveMapPreset(_layout, preset);
            _currentLabel.Text = $"Current preset: {preset}";
            _statusLabel.Text = $"Applied to {files.Count} config file(s).";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Apply failed: {ex.Message}";
        }
    }

    private void LaunchFullTerrainEditor()
    {
        string scriptPath = Path.Combine(_layout.RootPath, "terrain_editor.py");
        if (!File.Exists(scriptPath))
        {
            _statusLabel.Text = "terrain_editor.py not found.";
            return;
        }

        foreach (string launcher in new[] { "py", "python" })
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = launcher,
                    Arguments = $"\"{scriptPath}\"",
                    WorkingDirectory = _layout.RootPath,
                    UseShellExecute = true,
                });
                _statusLabel.Text = "Full terrain editor launched.";
                return;
            }
            catch
            {
                // Try the next launcher.
            }
        }

        _statusLabel.Text = "Python launcher not found.";
    }
}
