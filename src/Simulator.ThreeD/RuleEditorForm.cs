using System.Text.Json;
using System.Windows.Forms;
using Simulator.Core;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal sealed class RuleEditorForm : Form
{
    private readonly ProjectLayout _layout;
    private readonly RuleEditorService _service;
    private readonly TextBox _pathBox = new();
    private readonly TextBox _valueBox = new();
    private readonly TextBox _rulesSnapshot = new();
    private readonly Label _status = new();

    public RuleEditorForm()
    {
        _layout = ProjectLayout.Discover();
        _service = new RuleEditorService(new ConfigurationService());

        Text = "Rule Editor";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new System.Drawing.Size(980, 660);
        MinimumSize = new System.Drawing.Size(820, 520);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 76,
            ColumnCount = 4,
            RowCount = 2,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));

        var pathLabel = new Label { Text = "Path", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        _pathBox.Dock = DockStyle.Fill;
        _pathBox.Text = "rules.combat.auto_aim_max_distance_m";

        var valueLabel = new Label { Text = "Value", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        _valueBox.Dock = DockStyle.Fill;
        _valueBox.Text = "9.0";

        top.Controls.Add(pathLabel, 0, 0);
        top.Controls.Add(_pathBox, 1, 0);
        top.Controls.Add(valueLabel, 2, 0);
        top.Controls.Add(_valueBox, 3, 0);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };

        var apply = new Button { Text = "Apply", Width = 88, Height = 28 };
        apply.Click += (_, _) => ApplyRuleValue();

        var reload = new Button { Text = "Reload", Width = 88, Height = 28 };
        reload.Click += (_, _) => ReloadSnapshot();

        buttonRow.Controls.Add(apply);
        buttonRow.Controls.Add(reload);

        top.Controls.Add(buttonRow, 1, 1);
        top.SetColumnSpan(buttonRow, 3);

        _rulesSnapshot.Dock = DockStyle.Fill;
        _rulesSnapshot.Multiline = true;
        _rulesSnapshot.ScrollBars = ScrollBars.Both;
        _rulesSnapshot.WordWrap = false;
        _rulesSnapshot.Font = new System.Drawing.Font("Consolas", 10f);

        _status.Dock = DockStyle.Bottom;
        _status.Height = 24;

        root.Controls.Add(_rulesSnapshot);
        root.Controls.Add(top);
        root.Controls.Add(_status);
        Controls.Add(root);

        ReloadSnapshot();
    }

    private void ReloadSnapshot()
    {
        try
        {
            var rules = _service.LoadRules(_layout);
            _rulesSnapshot.Text = rules.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            _status.Text = "Rules reloaded.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Reload failed: {ex.Message}";
        }
    }

    private void ApplyRuleValue()
    {
        string path = (_pathBox.Text ?? string.Empty).Trim();
        string value = (_valueBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _status.Text = "Path is required.";
            return;
        }

        try
        {
            IReadOnlyList<string> files = _service.SetRuleValue(_layout, path, value);
            ReloadSnapshot();
            _status.Text = $"Applied to {files.Count} config file(s).";
        }
        catch (Exception ex)
        {
            _status.Text = $"Apply failed: {ex.Message}";
        }
    }
}
