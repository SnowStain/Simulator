using System.Windows.Forms;
using Simulator.Core;

namespace Simulator.ThreeD;

internal sealed class BehaviorEditorForm : Form
{
    private readonly ProjectLayout _layout;
    private readonly ComboBox _filePicker = new();
    private readonly TextBox _editor = new();
    private readonly Label _status = new();

    public BehaviorEditorForm()
    {
        _layout = ProjectLayout.Discover();

        Text = "Behavior Editor";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new System.Drawing.Size(980, 660);
        MinimumSize = new System.Drawing.Size(820, 520);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };

        _filePicker.Width = 520;
        _filePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _filePicker.SelectedIndexChanged += (_, _) => LoadSelectedFile();

        var reload = new Button { Text = "Reload", Width = 90, Height = 28 };
        reload.Click += (_, _) => LoadSelectedFile();

        var save = new Button { Text = "Save", Width = 90, Height = 28 };
        save.Click += (_, _) => SaveSelectedFile();

        top.Controls.Add(_filePicker);
        top.Controls.Add(reload);
        top.Controls.Add(save);

        _editor.Dock = DockStyle.Fill;
        _editor.Multiline = true;
        _editor.ScrollBars = ScrollBars.Both;
        _editor.WordWrap = false;
        _editor.Font = new System.Drawing.Font("Consolas", 10f);

        _status.Dock = DockStyle.Bottom;
        _status.Height = 24;

        root.Controls.Add(_editor);
        root.Controls.Add(top);
        root.Controls.Add(_status);
        Controls.Add(root);

        ReloadFileList();
    }

    private void ReloadFileList()
    {
        _filePicker.Items.Clear();
        string controlRoot = _layout.ResolvePath("control");
        if (Directory.Exists(controlRoot))
        {
            foreach (string path in Directory.EnumerateFiles(controlRoot))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".xml" or ".txt" or ".json")
                {
                    _filePicker.Items.Add(path);
                }
            }
        }

        if (_filePicker.Items.Count > 0)
        {
            int preferred = 0;
            for (int i = 0; i < _filePicker.Items.Count; i++)
            {
                string candidate = _filePicker.Items[i]?.ToString() ?? string.Empty;
                if (candidate.EndsWith("behavior_trees_btcpp.xml", StringComparison.OrdinalIgnoreCase))
                {
                    preferred = i;
                    break;
                }
            }

            _filePicker.SelectedIndex = preferred;
        }
        else
        {
            _editor.Text = string.Empty;
            _status.Text = "No behavior files found under control/.";
        }
    }

    private void LoadSelectedFile()
    {
        string? path = _filePicker.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _editor.Text = string.Empty;
            _status.Text = "Select a valid behavior file.";
            return;
        }

        _editor.Text = File.ReadAllText(path);
        _status.Text = $"Loaded: {Path.GetFileName(path)}";
    }

    private void SaveSelectedFile()
    {
        string? path = _filePicker.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            _status.Text = "Select a file first.";
            return;
        }

        try
        {
            File.WriteAllText(path, _editor.Text ?? string.Empty);
            _status.Text = $"Saved: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Save failed: {ex.Message}";
        }
    }
}
