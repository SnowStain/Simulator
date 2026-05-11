using System.ComponentModel;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal sealed class BuffEditorForm : Form
{
    private readonly ProjectLayout _layout = ProjectLayout.Discover();
    private readonly TerrainEditorService _service = new(new ConfigurationService(), new AssetCatalogService());
    private readonly ListBox _presetList = new();
    private readonly ListBox _buffList = new();
    private readonly PropertyGrid _buffGrid = new();
    private readonly MapPresetPreviewControl _preview = new();
    private readonly ComboBox _typePicker = new();
    private readonly ComboBox _teamPicker = new();
    private readonly Label _statusLabel = new();
    private MapPresetEditorSettings? _document;

    public BuffEditorForm()
    {
        Text = "Buff 编辑器";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1380, 860);
        MinimumSize = new Size(1080, 700);
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        _preview.MapSelectionChanged += (_, _) => UpdateSelectionStatus();
        _preview.SelectionTargetChanged += (_, _) => SelectPreviewFacility();
        _preview.DocumentEdited += (_, _) => OnDocumentEdited();
        _buffList.SelectedIndexChanged += (_, _) => SelectBuffFromList();
        _buffGrid.PropertyValueChanged += (_, _) => OnDocumentEdited();

        Controls.Add(BuildRoot());
        ReloadPresets(selectActive: true);
    }

    private Control BuildRoot()
    {
        var shell = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 230,
            FixedPanel = FixedPanel.Panel1,
        };
        shell.Panel1.Controls.Add(BuildPresetPanel());

        var editor = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 870,
        };
        editor.Panel1.Controls.Add(_preview);
        editor.Panel2.Controls.Add(BuildBuffPanel());
        shell.Panel2.Controls.Add(editor);
        return shell;
    }

    private Control BuildPresetPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = "地图",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
        };
        _presetList.Dock = DockStyle.Fill;
        _presetList.SelectedIndexChanged += (_, _) => LoadSelectedPreset();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };
        buttons.Controls.Add(CreateButton("刷新", (_, _) => ReloadPresets(selectActive: false), 92));
        buttons.Controls.Add(CreateButton("应用地图", (_, _) => ApplySelectedPreset(), 92));
        buttons.Controls.Add(CreateButton("保存", (_, _) => SaveDocument(), 92));

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.TopLeft;
        _statusLabel.AutoEllipsis = true;

        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(_presetList, 0, 1);
        panel.Controls.Add(buttons, 0, 2);
        panel.Controls.Add(_statusLabel, 0, 3);
        return panel;
    }

    private Control BuildBuffPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 58));

        _typePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _typePicker.Width = 190;
        _typePicker.Items.AddRange(new object[]
        {
            "buff_supply",
            "buff_fort",
            "buff_base",
            "buff_outpost",
            "buff_trapezoid_highland",
            "buff_central_highland",
            "buff_hero_deployment",
            "debuff_zone",
        });
        _typePicker.SelectedIndex = 0;

        _teamPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _teamPicker.Width = 100;
        _teamPicker.Items.AddRange(new object[] { "neutral", "red", "blue" });
        _teamPicker.SelectedIndex = 0;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };
        toolbar.Controls.Add(new Label { Text = "类型", Width = 42, TextAlign = ContentAlignment.MiddleLeft });
        toolbar.Controls.Add(_typePicker);
        toolbar.Controls.Add(new Label { Text = "阵营", Width = 42, TextAlign = ContentAlignment.MiddleLeft });
        toolbar.Controls.Add(_teamPicker);
        toolbar.Controls.Add(CreateButton("绘制 Buff", (_, _) => AddBuffFromSelection(), 96));
        toolbar.Controls.Add(CreateButton("默认 Buff", (_, _) => AddDefaultBuff(), 96));
        toolbar.Controls.Add(CreateButton("删除", (_, _) => DeleteSelectedBuff(), 82));
        toolbar.Controls.Add(CreateButton("重载", (_, _) => LoadSelectedPreset(force: true), 82));

        _buffList.Dock = DockStyle.Fill;
        _buffList.DisplayMember = nameof(FacilityRegionEditorModel.Id);
        _buffGrid.Dock = DockStyle.Fill;

        panel.Controls.Add(toolbar, 0, 0);
        panel.Controls.Add(_buffList, 0, 1);
        panel.Controls.Add(_buffGrid, 0, 2);
        return panel;
    }

    private static Button CreateButton(string text, EventHandler onClick, int width)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 30,
            Margin = new Padding(4),
        };
        button.Click += onClick;
        return button;
    }

    private void ReloadPresets(bool selectActive)
    {
        string? active = null;
        if (selectActive)
        {
            try
            {
                active = _service.GetActiveMapPreset(_layout);
            }
            catch
            {
                active = null;
            }
        }

        _presetList.BeginUpdate();
        _presetList.Items.Clear();
        foreach (string preset in _service.ListMapPresets(_layout))
        {
            _presetList.Items.Add(preset);
            if (active is not null && string.Equals(preset, active, StringComparison.OrdinalIgnoreCase))
            {
                _presetList.SelectedItem = preset;
            }
        }

        if (_presetList.SelectedIndex < 0 && _presetList.Items.Count > 0)
        {
            _presetList.SelectedIndex = 0;
        }

        _presetList.EndUpdate();
    }

    private void LoadSelectedPreset(bool force = false)
    {
        if (_presetList.SelectedItem is not string preset)
        {
            return;
        }

        if (!force && _document is not null && string.Equals(_document.PresetName, preset, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _document = _service.LoadPresetDocument(_layout, preset);
            _preview.Document = _document;
            _preview.SelectedFacilityId = null;
            RebindBuffList();
            _statusLabel.Text = $"已加载 {preset}";
        }
        catch (Exception ex)
        {
            _document = null;
            _preview.Document = null;
            _buffList.DataSource = null;
            _buffGrid.SelectedObject = null;
            _statusLabel.Text = $"加载失败: {ex.Message}";
        }
    }

    private void RebindBuffList(string? selectedId = null)
    {
        if (_document is null)
        {
            _buffList.DataSource = null;
            _buffGrid.SelectedObject = null;
            return;
        }

        List<FacilityRegionEditorModel> buffs = _document.Facilities
            .Where(IsBuffFacility)
            .OrderBy(facility => facility.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _buffList.DataSource = buffs;
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            _buffList.SelectedItem = buffs.FirstOrDefault(facility => string.Equals(facility.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }

        if (_buffList.SelectedIndex < 0 && buffs.Count > 0)
        {
            _buffList.SelectedIndex = 0;
        }

        SelectBuffFromList();
    }

    private void SelectBuffFromList()
    {
        var selected = _buffList.SelectedItem as FacilityRegionEditorModel;
        _buffGrid.SelectedObject = selected;
        _preview.SelectedFacilityId = selected?.Id;
        _preview.MarkSceneDirty();
    }

    private void SelectPreviewFacility()
    {
        if (_document is null || string.IsNullOrWhiteSpace(_preview.SelectedFacilityId))
        {
            return;
        }

        FacilityRegionEditorModel? selected = _document.Facilities.FirstOrDefault(facility =>
            string.Equals(facility.Id, _preview.SelectedFacilityId, StringComparison.OrdinalIgnoreCase)
            && IsBuffFacility(facility));
        if (selected is not null)
        {
            _buffList.SelectedItem = (_buffList.DataSource as IEnumerable<FacilityRegionEditorModel>)?
                .FirstOrDefault(facility => string.Equals(facility.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void AddBuffFromSelection()
    {
        if (_document is null)
        {
            return;
        }

        RectangleF selection = _preview.MapSelection is RectangleF rect && rect.Width >= 2f && rect.Height >= 2f
            ? rect
            : BuildDefaultRect();
        AddBuffRegion(selection);
    }

    private void AddDefaultBuff()
    {
        if (_document is null)
        {
            return;
        }

        AddBuffRegion(BuildDefaultRect());
    }

    private RectangleF BuildDefaultRect()
    {
        if (_document is null)
        {
            return new RectangleF(0, 0, 40, 40);
        }

        float width = Math.Max(20f, _document.Width * 0.08f);
        float height = Math.Max(20f, _document.Height * 0.08f);
        return new RectangleF(
            Math.Max(0f, (_document.Width - width) * 0.5f),
            Math.Max(0f, (_document.Height - height) * 0.5f),
            width,
            height);
    }

    private void AddBuffRegion(RectangleF selection)
    {
        if (_document is null)
        {
            return;
        }

        string type = _typePicker.SelectedItem?.ToString() ?? "buff_supply";
        string team = _teamPicker.SelectedItem?.ToString() ?? "neutral";
        int nextIndex = _document.Facilities.Count(IsBuffFacility) + 1;
        var facility = new FacilityRegionEditorModel
        {
            Id = $"{type.Replace('-', '_')}_{nextIndex}",
            Type = type,
            Team = team,
            Shape = "rect",
            X1 = Math.Clamp(selection.Left, 0f, Math.Max(0f, _document.Width)),
            Y1 = Math.Clamp(selection.Top, 0f, Math.Max(0f, _document.Height)),
            X2 = Math.Clamp(selection.Right, 0f, Math.Max(0f, _document.Width)),
            Y2 = Math.Clamp(selection.Bottom, 0f, Math.Max(0f, _document.Height)),
            HeightM = 0.08,
            Thickness = 12.0,
            VolumeShape = "box",
            BlocksMovement = false,
        };
        facility.CenterX = (facility.X1 + facility.X2) * 0.5;
        facility.CenterY = (facility.Y1 + facility.Y2) * 0.5;
        facility.CenterZM = 0.04;
        facility.SizeX = Math.Max(0.01, Math.Abs(facility.X2 - facility.X1));
        facility.SizeY = Math.Max(0.01, Math.Abs(facility.Y2 - facility.Y1));
        facility.SizeZM = 0.08;
        facility.CollisionHeightM = 0.08;

        _document.Facilities.Add(facility);
        RebindBuffList(facility.Id);
        _preview.SelectedFacilityId = facility.Id;
        _preview.MarkSceneDirty();
        _statusLabel.Text = $"已新增 {facility.Id}";
    }

    private void DeleteSelectedBuff()
    {
        if (_document is null || _buffList.SelectedItem is not FacilityRegionEditorModel selected)
        {
            return;
        }

        _document.Facilities.Remove(selected);
        RebindBuffList();
        _preview.SelectedFacilityId = null;
        _preview.MarkSceneDirty();
        _statusLabel.Text = $"已删除 {selected.Id}";
    }

    private void SaveDocument()
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            _service.SavePresetDocument(_document);
            _statusLabel.Text = $"已保存 {_document.PresetName}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"保存失败: {ex.Message}";
        }
    }

    private void ApplySelectedPreset()
    {
        if (_presetList.SelectedItem is not string preset)
        {
            return;
        }

        try
        {
            IReadOnlyList<string> paths = _service.SetActiveMapPreset(_layout, preset);
            _statusLabel.Text = $"已应用 {preset}: {string.Join(", ", paths)}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"应用失败: {ex.Message}";
        }
    }

    private void UpdateSelectionStatus()
    {
        if (_preview.MapSelection is RectangleF selection)
        {
            _statusLabel.Text = $"选择区域 {selection.Left:0.#},{selection.Top:0.#} - {selection.Right:0.#},{selection.Bottom:0.#}";
        }
    }

    private void OnDocumentEdited()
    {
        string? selectedId = (_buffList.SelectedItem as FacilityRegionEditorModel)?.Id ?? _preview.SelectedFacilityId;
        RebindBuffList(selectedId);
        _preview.MarkSceneDirty();
    }

    private static bool IsBuffFacility(FacilityRegionEditorModel facility)
    {
        string type = facility.Type ?? string.Empty;
        return type.Contains("buff", StringComparison.OrdinalIgnoreCase)
            || type.Contains("debuff", StringComparison.OrdinalIgnoreCase)
            || type.Equals("supply", StringComparison.OrdinalIgnoreCase)
            || type.Equals("fort", StringComparison.OrdinalIgnoreCase);
    }
}
