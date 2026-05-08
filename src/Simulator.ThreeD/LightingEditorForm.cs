using System.ComponentModel;
using System.Windows.Forms;

namespace Simulator.ThreeD;

internal sealed class LightingEditorForm : Form
{
    private readonly Simulator3dHost _host;
    private readonly PropertyGrid _grid = new();
    private readonly Label _status = new();
    private readonly Panel _previewPanel = new();
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 33 };
    private readonly CheckBox _enabledCheck = new();
    private readonly ComboBox _paletteTarget = new();
    private readonly Panel _paletteSwatch = new();
    private readonly TrackBar _redTrack = CreateColorTrackBar();
    private readonly TrackBar _greenTrack = CreateColorTrackBar();
    private readonly TrackBar _blueTrack = CreateColorTrackBar();
    private readonly NumericUpDown _redValue = CreateColorValue();
    private readonly NumericUpDown _greenValue = CreateColorValue();
    private readonly NumericUpDown _blueValue = CreateColorValue();
    private Simulator3dHost? _previewHost;
    private Simulator3dForm? _previewForm;
    private Simulator3dLightingSettings _settings;
    private bool _syncingUi;

    public LightingEditorForm(Simulator3dHost host)
    {
        _host = host;
        _settings = host.GetLightingSettings();

        Text = "局内光照编辑器";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1320, 820);
        MinimumSize = new Size(1080, 680);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        root.Controls.Add(BuildToolbar(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);

        Load += (_, _) => CreateGpuPreview();
        FormClosed += (_, _) =>
        {
            _previewTimer.Stop();
            DisposeGpuPreview();
            _previewTimer.Dispose();
        };
        _previewTimer.Tick += (_, _) => _previewForm?.Invalidate();
        Reload();
    }

    private Control BuildToolbar()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };

        toolbar.Controls.Add(CreateButton("重新读取", (_, _) => Reload(), 88));
        toolbar.Controls.Add(CreateButton("保存应用", (_, _) => Apply(), 88));
        toolbar.Controls.Add(CreateButton("恢复默认", (_, _) => Reset(), 88));
        toolbar.Controls.Add(CreateButton("刷新预览", (_, _) => RecreateGpuPreview(), 96));
        return toolbar;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 860,
        };

        var previewShell = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 10, 0),
        };

        var previewLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = "GPU 实时预览",
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(24, 30, 40),
            ForeColor = Color.FromArgb(220, 232, 240),
            Padding = new Padding(8, 0, 0, 0),
        };

        _previewPanel.Dock = DockStyle.Fill;
        _previewPanel.BackColor = Color.FromArgb(16, 20, 28);
        previewShell.Controls.Add(_previewPanel);
        previewShell.Controls.Add(previewLabel);

        var rightPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 246));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var intro = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(248, 248, 248),
            Text = "使用说明\r\n"
                + "1. 左侧是局内 GPU 实时预览，颜色调色板和属性表都会实时刷新画面。\r\n"
                + "2. 主光决定整体方向感和主要明暗，补光用于抬亮阴影侧。\r\n"
                + "3. 环境光影响暗部基础亮度，漫反射影响受光面亮度，高光影响金属反光。\r\n"
                + "4. RGB 分量范围是 0-2；超过 1 可用于更强光照强度。\r\n"
                + "5. “保存应用”会写入配置；“恢复默认”只重置当前内存，确认后再保存。",
        };

        _grid.Dock = DockStyle.Fill;
        _grid.ToolbarVisible = false;
        _grid.HelpVisible = true;
        _grid.PropertyValueChanged += (_, _) => UpdateLightingFromGrid(persist: false);

        split.Panel1.Controls.Add(previewShell);
        rightPanel.Controls.Add(intro, 0, 0);
        rightPanel.Controls.Add(BuildPalette(), 0, 1);
        rightPanel.Controls.Add(_grid, 0, 2);
        split.Panel2.Controls.Add(rightPanel);
        return split;
    }

    private Control BuildPalette()
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "实时调色板",
            Padding = new Padding(8),
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 6,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _enabledCheck.Text = "启用局内光照";
        _enabledCheck.Dock = DockStyle.Fill;
        _enabledCheck.CheckedChanged += (_, _) =>
        {
            if (_syncingUi)
            {
                return;
            }

            _settings.Enabled = _enabledCheck.Checked;
            ApplyLiveLighting("光照开关已实时更新。");
        };
        table.Controls.Add(_enabledCheck, 0, 0);
        table.SetColumnSpan(_enabledCheck, 4);

        _paletteTarget.DropDownStyle = ComboBoxStyle.DropDownList;
        _paletteTarget.Dock = DockStyle.Fill;
        _paletteTarget.Items.AddRange(new object[]
        {
            "主光环境光",
            "主光漫反射",
            "主光高光",
            "补光环境光",
            "补光漫反射",
            "补光高光",
            "材质高光",
        });
        _paletteTarget.SelectedIndexChanged += (_, _) => SyncPaletteFromSettings();
        table.Controls.Add(new Label { Text = "对象", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        table.Controls.Add(_paletteTarget, 1, 1);

        _paletteSwatch.Dock = DockStyle.Fill;
        _paletteSwatch.BorderStyle = BorderStyle.FixedSingle;
        _paletteSwatch.Cursor = Cursors.Hand;
        _paletteSwatch.Click += (_, _) => PickPaletteColor();
        table.Controls.Add(_paletteSwatch, 2, 1);
        table.SetColumnSpan(_paletteSwatch, 2);

        AddPaletteRow(table, 2, "R", _redTrack, _redValue);
        AddPaletteRow(table, 3, "G", _greenTrack, _greenValue);
        AddPaletteRow(table, 4, "B", _blueTrack, _blueValue);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "拖动滑块会立即写入当前画面；点击色块可打开系统颜色选择器。",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(82, 92, 104),
        };
        table.Controls.Add(hint, 0, 5);
        table.SetColumnSpan(hint, 4);

        _paletteTarget.SelectedIndex = 1;
        group.Controls.Add(table);
        return group;
    }

    private void AddPaletteRow(TableLayoutPanel table, int row, string label, TrackBar track, NumericUpDown value)
    {
        table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        table.Controls.Add(track, 1, row);
        table.Controls.Add(value, 2, row);
        table.SetColumnSpan(value, 2);

        track.Scroll += (_, _) =>
        {
            if (_syncingUi)
            {
                return;
            }

            _syncingUi = true;
            value.Value = track.Value / 100m;
            _syncingUi = false;
            ApplyPaletteToSettings();
        };
        value.ValueChanged += (_, _) =>
        {
            if (_syncingUi)
            {
                return;
            }

            _syncingUi = true;
            track.Value = (int)Math.Round(value.Value * 100m);
            _syncingUi = false;
            ApplyPaletteToSettings();
        };
    }

    private static Button CreateButton(string text, EventHandler onClick, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += onClick;
        return button;
    }

    private static TrackBar CreateColorTrackBar()
        => new()
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 200,
            TickFrequency = 25,
            SmallChange = 1,
            LargeChange = 10,
        };

    private static NumericUpDown CreateColorValue()
        => new()
        {
            Dock = DockStyle.Fill,
            Minimum = 0m,
            Maximum = 2m,
            DecimalPlaces = 2,
            Increment = 0.01m,
        };

    private void CreateGpuPreview()
    {
        if (_previewForm is not null || _previewPanel.IsDisposed)
        {
            return;
        }

        try
        {
            var options = new Simulator3dOptions
            {
                MapPreset = _host.ActiveMapPreset,
                MatchMode = _host.MatchMode,
                RendererMode = "gpu",
                SelectedTeam = _host.SelectedTeam,
                SelectedEntityId = _host.SelectedEntity?.Id,
                StartInMatch = true,
            };
            _previewHost = new Simulator3dHost(options);
            _previewHost.UpdateLightingSettings(_settings, persist: false);
            _previewForm = Simulator3dForm.CreateSharedHostPreview(_previewHost, options);
            _previewForm.TopLevel = false;
            _previewForm.FormBorderStyle = FormBorderStyle.None;
            _previewForm.Dock = DockStyle.Fill;
            _previewForm.ShowInTaskbar = false;
            _previewPanel.Controls.Clear();
            _previewPanel.Controls.Add(_previewForm);
            _previewForm.Show();
            _previewTimer.Start();
            _status.Text = "GPU 实时预览已启动，调色板变化会立即同步到预览画面。";
        }
        catch (Exception ex)
        {
            _status.Text = $"GPU 预览启动失败：{ex.Message}";
        }
    }

    private void RecreateGpuPreview()
    {
        DisposeGpuPreview();
        CreateGpuPreview();
    }

    private void DisposeGpuPreview()
    {
        _previewTimer.Stop();
        if (_previewForm is null)
        {
            _previewPanel.Controls.Clear();
            return;
        }

        try
        {
            _previewPanel.Controls.Remove(_previewForm);
            _previewForm.Close();
            _previewForm.Dispose();
        }
        catch
        {
        }
        finally
        {
            _previewForm = null;
            _previewHost = null;
            _previewPanel.Controls.Clear();
        }
    }

    private void Reload()
    {
        _settings = _host.GetLightingSettings();
        _grid.SelectedObject = _settings;
        SyncPaletteFromSettings();
        _previewHost?.UpdateLightingSettings(_settings, persist: false);
        _previewForm?.Invalidate();
        _status.Text = "已重新读取光照设置。";
    }

    private void Apply()
    {
        UpdateLightingFromGrid(persist: true);
        _status.Text = "光照设置已保存并应用。";
    }

    private void Reset()
    {
        _settings = Simulator3dLightingSettings.CreateDefault();
        _host.UpdateLightingSettings(_settings, persist: false);
        _previewHost?.UpdateLightingSettings(_settings, persist: false);
        _grid.SelectedObject = _settings;
        SyncPaletteFromSettings();
        _previewForm?.Invalidate();
        _status.Text = "已恢复默认光照，当前仅应用到内存；需要写入配置请点击“保存应用”。";
    }

    private void UpdateLightingFromGrid(bool persist)
    {
        if (_grid.SelectedObject is Simulator3dLightingSettings settings)
        {
            _settings = settings.Normalized();
        }

        ApplyLiveLighting(persist
            ? "光照设置已保存，GPU 预览已更新。"
            : "光照已实时更新。",
            persist);
        SyncPaletteFromSettings();
    }

    private void SyncPaletteFromSettings()
    {
        if (_paletteTarget.SelectedIndex < 0)
        {
            return;
        }

        (float r, float g, float b) = GetSelectedColor();
        _syncingUi = true;
        _enabledCheck.Checked = _settings.Enabled;
        SetColorControls(_redTrack, _redValue, r);
        SetColorControls(_greenTrack, _greenValue, g);
        SetColorControls(_blueTrack, _blueValue, b);
        _paletteSwatch.BackColor = ToDisplayColor(r, g, b);
        _syncingUi = false;
    }

    private static void SetColorControls(TrackBar track, NumericUpDown value, float component)
    {
        decimal decimalValue = Math.Clamp((decimal)component, 0m, 2m);
        value.Value = decimalValue;
        track.Value = (int)Math.Round(decimalValue * 100m);
    }

    private void ApplyPaletteToSettings()
    {
        SetSelectedColor(
            (float)_redValue.Value,
            (float)_greenValue.Value,
            (float)_blueValue.Value);
        _paletteSwatch.BackColor = ToDisplayColor((float)_redValue.Value, (float)_greenValue.Value, (float)_blueValue.Value);
        ApplyLiveLighting("调色板已实时更新。");
    }

    private void PickPaletteColor()
    {
        using var dialog = new ColorDialog
        {
            FullOpen = true,
            Color = _paletteSwatch.BackColor,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _syncingUi = true;
        _redValue.Value = Math.Round(dialog.Color.R / 255m, 2);
        _greenValue.Value = Math.Round(dialog.Color.G / 255m, 2);
        _blueValue.Value = Math.Round(dialog.Color.B / 255m, 2);
        _redTrack.Value = (int)Math.Round(_redValue.Value * 100m);
        _greenTrack.Value = (int)Math.Round(_greenValue.Value * 100m);
        _blueTrack.Value = (int)Math.Round(_blueValue.Value * 100m);
        _syncingUi = false;
        ApplyPaletteToSettings();
    }

    private void ApplyLiveLighting(string message, bool persist = false)
    {
        _settings = _settings.Normalized();
        _host.UpdateLightingSettings(_settings, persist);
        _previewHost?.UpdateLightingSettings(_settings, persist: false);
        _grid.SelectedObject = _settings;
        _status.Text = message;
        _previewForm?.Invalidate();
        _previewForm?.Update();
    }

    private (float R, float G, float B) GetSelectedColor()
    {
        return _paletteTarget.SelectedIndex switch
        {
            0 => (_settings.KeyAmbientR, _settings.KeyAmbientG, _settings.KeyAmbientB),
            1 => (_settings.KeyDiffuseR, _settings.KeyDiffuseG, _settings.KeyDiffuseB),
            2 => (_settings.KeySpecularR, _settings.KeySpecularG, _settings.KeySpecularB),
            3 => (_settings.FillAmbientR, _settings.FillAmbientG, _settings.FillAmbientB),
            4 => (_settings.FillDiffuseR, _settings.FillDiffuseG, _settings.FillDiffuseB),
            5 => (_settings.FillSpecularR, _settings.FillSpecularG, _settings.FillSpecularB),
            6 => (_settings.MaterialSpecularR, _settings.MaterialSpecularG, _settings.MaterialSpecularB),
            _ => (_settings.KeyDiffuseR, _settings.KeyDiffuseG, _settings.KeyDiffuseB),
        };
    }

    private void SetSelectedColor(float r, float g, float b)
    {
        switch (_paletteTarget.SelectedIndex)
        {
            case 0:
                _settings.KeyAmbientR = r;
                _settings.KeyAmbientG = g;
                _settings.KeyAmbientB = b;
                break;
            case 1:
                _settings.KeyDiffuseR = r;
                _settings.KeyDiffuseG = g;
                _settings.KeyDiffuseB = b;
                break;
            case 2:
                _settings.KeySpecularR = r;
                _settings.KeySpecularG = g;
                _settings.KeySpecularB = b;
                break;
            case 3:
                _settings.FillAmbientR = r;
                _settings.FillAmbientG = g;
                _settings.FillAmbientB = b;
                break;
            case 4:
                _settings.FillDiffuseR = r;
                _settings.FillDiffuseG = g;
                _settings.FillDiffuseB = b;
                break;
            case 5:
                _settings.FillSpecularR = r;
                _settings.FillSpecularG = g;
                _settings.FillSpecularB = b;
                break;
            case 6:
                _settings.MaterialSpecularR = r;
                _settings.MaterialSpecularG = g;
                _settings.MaterialSpecularB = b;
                break;
        }
    }

    private static Color ToDisplayColor(float r, float g, float b)
        => Color.FromArgb(
            ToByte(r),
            ToByte(g),
            ToByte(b));

    private static int ToByte(float value)
        => Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
}
