using System.Text.Json.Nodes;
using System.Windows.Forms;
using Simulator.Core;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal sealed class AppearanceEditorForm : Form
{
    private readonly ProjectLayout _layout;
    private readonly AppearanceEditorService _service;
    private readonly ListBox _keys = new();
    private readonly TextBox _valueEditor = new();
    private readonly Label _status = new();
    private JsonObject _appearance = new();

    public AppearanceEditorForm()
    {
        _layout = ProjectLayout.Discover();
        _service = new AppearanceEditorService();

        Text = "Appearance Editor";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new System.Drawing.Size(980, 640);
        MinimumSize = new System.Drawing.Size(820, 520);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 280,
            FixedPanel = FixedPanel.Panel1,
        };

        _keys.Dock = DockStyle.Fill;
        _keys.SelectedIndexChanged += (_, _) => LoadSelectedValue();

        var keyPanel = new Panel { Dock = DockStyle.Fill };
        var keyHeader = new Label
        {
            Text = "Top-level keys",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        };
        keyPanel.Controls.Add(_keys);
        keyPanel.Controls.Add(keyHeader);

        var editorPanel = new Panel { Dock = DockStyle.Fill };
        var toolRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(6, 6, 6, 4),
        };

        var reloadButton = new Button { Text = "Reload", Width = 90, Height = 28 };
        reloadButton.Click += (_, _) => ReloadAppearance();

        var saveButton = new Button { Text = "Save Key", Width = 90, Height = 28 };
        saveButton.Click += (_, _) => SaveSelectedKey();

        var addButton = new Button { Text = "Add Key", Width = 90, Height = 28 };
        addButton.Click += (_, _) => AddNewKey();

        toolRow.Controls.Add(reloadButton);
        toolRow.Controls.Add(saveButton);
        toolRow.Controls.Add(addButton);

        _valueEditor.Dock = DockStyle.Fill;
        _valueEditor.Multiline = true;
        _valueEditor.ScrollBars = ScrollBars.Both;
        _valueEditor.WordWrap = false;
        _valueEditor.Font = new System.Drawing.Font("Consolas", 10f);

        _status.Dock = DockStyle.Bottom;
        _status.Height = 24;
        _status.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

        editorPanel.Controls.Add(_valueEditor);
        editorPanel.Controls.Add(toolRow);
        editorPanel.Controls.Add(_status);

        split.Panel1.Controls.Add(keyPanel);
        split.Panel2.Controls.Add(editorPanel);
        Controls.Add(split);

        ReloadAppearance();
    }

    private void ReloadAppearance()
    {
        _appearance = _service.LoadLatestAppearance(_layout);
        _keys.Items.Clear();

        foreach (var item in _appearance.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
            {
                _keys.Items.Add(item.Key);
            }
        }

        if (_keys.Items.Count > 0)
        {
            _keys.SelectedIndex = 0;
        }
        else
        {
            _valueEditor.Text = string.Empty;
        }

        _status.Text = "Appearance loaded.";
    }

    private void LoadSelectedValue()
    {
        string? key = _keys.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            _valueEditor.Text = string.Empty;
            return;
        }

        JsonNode? node = _appearance[key];
        _valueEditor.Text = node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }

    private void SaveSelectedKey()
    {
        string? key = _keys.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            _status.Text = "Select a key first.";
            return;
        }

        try
        {
            _appearance = _service.SetTopLevelValue(_layout, key, _valueEditor.Text);
            _status.Text = $"Saved key: {key}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Save failed: {ex.Message}";
        }
    }

    private void AddNewKey()
    {
        string? key = PromptDialog.Show("New top-level key:", "Add Key");
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        key = key.Trim();
        try
        {
            _appearance = _service.SetTopLevelValue(_layout, key, "{}");
            ReloadAppearance();
            int index = _keys.Items.IndexOf(key);
            if (index >= 0)
            {
                _keys.SelectedIndex = index;
            }

            _status.Text = $"Added key: {key}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Add failed: {ex.Message}";
        }
    }
}
