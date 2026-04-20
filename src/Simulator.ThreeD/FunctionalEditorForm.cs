using System.Text.Json.Nodes;
using System.Windows.Forms;
using Simulator.Assets;
using Simulator.Core;

namespace Simulator.ThreeD;

internal sealed class FunctionalEditorForm : Form
{
    private readonly ProjectLayout _layout;
    private readonly ConfigurationService _configService;
    private readonly ComboBox _matchMode = new();
    private readonly ComboBox _team = new();
    private readonly ComboBox _focusEntity = new();
    private readonly ComboBox _backend = new();
    private readonly ComboBox _mapPreset = new();
    private readonly CheckBox _ricochet = new();
    private readonly Label _status = new();

    public FunctionalEditorForm()
    {
        _layout = ProjectLayout.Discover();
        _configService = new ConfigurationService();

        Text = "Functional Editor";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new System.Drawing.Size(720, 420);
        MinimumSize = new System.Drawing.Size(620, 360);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 8,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddLabeledCombo(root, 0, "Match Mode", _matchMode, new[] { "full", "single_unit_test" });
        AddLabeledCombo(root, 1, "Single Unit Team", _team, new[] { "red", "blue" });
        AddLabeledCombo(root, 2, "Single Unit Focus", _focusEntity, new[] { "robot_1", "robot_2", "robot_3", "robot_4", "robot_7" });
        AddLabeledCombo(root, 3, "Renderer Backend", _backend, new[] { "opengl", "moderngl", "native_cpp" });

        _mapPreset.DropDownStyle = ComboBoxStyle.DropDownList;
        var mapLabel = new Label { Text = "Map Preset", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        root.Controls.Add(mapLabel, 0, 4);
        root.Controls.Add(_mapPreset, 1, 4);

        _ricochet.Text = "Projectile Ricochet Enabled";
        _ricochet.Dock = DockStyle.Fill;
        root.Controls.Add(new Label { Text = "Combat", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 5);
        root.Controls.Add(_ricochet, 1, 5);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };
        var reload = new Button { Text = "Reload", Width = 90, Height = 28 };
        reload.Click += (_, _) => LoadCurrentValues();
        var apply = new Button { Text = "Apply", Width = 90, Height = 28 };
        apply.Click += (_, _) => ApplyValues();
        buttons.Controls.Add(reload);
        buttons.Controls.Add(apply);

        root.Controls.Add(new Label(), 0, 6);
        root.Controls.Add(buttons, 1, 6);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        root.Controls.Add(new Label(), 0, 7);
        root.Controls.Add(_status, 1, 7);

        Controls.Add(root);

        LoadPresetChoices();
        LoadCurrentValues();
    }

    private static void AddLabeledCombo(TableLayoutPanel root, int row, string label, ComboBox combo, IEnumerable<string> choices)
    {
        var title = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (string choice in choices)
        {
            combo.Items.Add(choice);
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        root.Controls.Add(title, 0, row);
        root.Controls.Add(combo, 1, row);
    }

    private void LoadPresetChoices()
    {
        _mapPreset.Items.Clear();
        var terrainService = new Simulator.Editors.TerrainEditorService(_configService, new AssetCatalogService());
        foreach (string preset in terrainService.ListMapPresets(_layout))
        {
            _mapPreset.Items.Add(preset);
        }

        if (_mapPreset.Items.Count > 0)
        {
            _mapPreset.SelectedIndex = 0;
        }
    }

    private void LoadCurrentValues()
    {
        string path = _configService.ResolvePrimaryConfigPath(_layout);
        JsonObject config = _configService.LoadConfig(path);
        JsonObject simulator = ConfigurationService.EnsureObject(config, "simulator");

        SelectComboValue(_matchMode, simulator["match_mode"]?.ToString() ?? "full");
        SelectComboValue(_team, simulator["single_unit_test_team"]?.ToString() ?? "red");
        SelectComboValue(_focusEntity, simulator["single_unit_test_entity_key"]?.ToString() ?? "robot_1");
        SelectComboValue(_backend, simulator["sim3d_renderer_backend"]?.ToString() ?? "moderngl");
        SelectComboValue(_mapPreset, simulator["sim3d_map_preset"]?.ToString() ?? _configService.GetMapPreset(config));

        bool ricochet = true;
        if (simulator["player_projectile_ricochet_enabled"] is JsonValue ricochetNode
            && ricochetNode.TryGetValue(out bool parsedRicochet))
        {
            ricochet = parsedRicochet;
        }

        _ricochet.Checked = ricochet;
        _status.Text = "Loaded current functional settings.";
    }

    private void ApplyValues()
    {
        IReadOnlyList<string> paths = _configService.ExistingConfigPaths(_layout);
        if (paths.Count == 0)
        {
            paths = new[] { _configService.ResolvePrimaryConfigPath(_layout) };
        }

        foreach (string path in paths)
        {
            JsonObject config = _configService.LoadConfig(path);
            JsonObject simulator = ConfigurationService.EnsureObject(config, "simulator");

            simulator["match_mode"] = (_matchMode.SelectedItem?.ToString() ?? "full").Trim();
            simulator["single_unit_test_team"] = (_team.SelectedItem?.ToString() ?? "red").Trim();
            simulator["single_unit_test_entity_key"] = (_focusEntity.SelectedItem?.ToString() ?? "robot_1").Trim();

            string backendMode = (_backend.SelectedItem?.ToString() ?? "moderngl").Trim();
            simulator["sim3d_renderer_backend"] = backendMode;
            simulator["terrain_scene_backend"] = backendMode switch
            {
                "opengl" => "editor_opengl",
                "native_cpp" => "native_cpp",
                _ => "pyglet_moderngl",
            };

            string preset = (_mapPreset.SelectedItem?.ToString() ?? "basicMap").Trim();
            _configService.SetMapPreset(config, preset);
            simulator["player_projectile_ricochet_enabled"] = _ricochet.Checked;

            _configService.SaveConfig(path, config);
        }

        _status.Text = $"Applied to {paths.Count} config file(s).";
    }

    private static void SelectComboValue(ComboBox combo, string value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0 && combo.SelectedIndex < 0)
        {
            combo.SelectedIndex = 0;
        }
    }
}
