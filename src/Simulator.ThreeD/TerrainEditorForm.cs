using System.Windows.Forms;
using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json.Nodes;
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
    private readonly ListBox _facilityList = new();
    private readonly ListBox _facetList = new();
    private readonly PropertyGrid _mapGrid = new();
    private readonly PropertyGrid _surfaceGrid = new();
    private readonly PropertyGrid _facilityGrid = new();
    private readonly PropertyGrid _facetGrid = new();
    private readonly PropertyGrid _fineTerrainGrid = new();
    private readonly Label _statusLabel = new();
    private readonly Label _currentLabel = new();
    private readonly MapPresetPreviewControl _editPreview = new();
    private readonly EmbeddedSimulatorPreviewHost _gpuPreview = new();
    private readonly TabControl _tabControl = new();

    private MapPresetEditorSettings? _document;

    public TerrainEditorForm()
    {
        _layout = ProjectLayout.Discover();
        _configService = new ConfigurationService();
        _service = new TerrainEditorService(_configService, new AssetCatalogService());
        _editPreview.MapSelectionChanged += (_, _) => UpdateSelectionStatus();
        _editPreview.SelectionTargetChanged += (_, _) => SyncSelectionFromPreview();
        _editPreview.DocumentEdited += (_, _) => OnDocumentModified();
        _gpuPreview.EmptyText = "请先保存当前地图预设，再加载 GPU 预览。";
        _gpuPreview.OptionsFactory = BuildGpuPreviewOptions;

        Text = "地图与增益编辑器";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1540, 920);
        MinimumSize = new Size(1260, 780);

        var shell = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 260,
            FixedPanel = FixedPanel.Panel1,
        };

        shell.Panel1.Controls.Add(BuildPresetPanel());
        shell.Panel2.Controls.Add(BuildEditorPanel());
        Controls.Add(shell);

        ReloadPresets(selectCurrentPreset: true);
    }

    private Control BuildPresetPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "地图选择",
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _currentLabel.Dock = DockStyle.Top;
        _currentLabel.Height = 24;
        _currentLabel.TextAlign = ContentAlignment.MiddleLeft;

        _presetList.Dock = DockStyle.Fill;
        _presetList.SelectedIndexChanged += (_, _) => LoadSelectedPreset();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 108,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 6, 0, 0),
        };

        buttons.Controls.Add(CreateButton("刷新列表", (_, _) => ReloadPresets(selectCurrentPreset: false), 104));
        buttons.Controls.Add(CreateButton("应用地图", (_, _) => ApplySelectedPreset(), 104));
        buttons.Controls.Add(CreateButton("局内预览", (_, _) => OpenPreviewSimulator(), 104));
        buttons.Controls.Add(CreateButton("精细地图编辑", (_, _) => LaunchFineTerrainEditor(), 128));

        panel.Controls.Add(_presetList);
        panel.Controls.Add(_currentLabel);
        panel.Controls.Add(title);
        panel.Controls.Add(buttons);
        return panel;
    }

    private Control BuildEditorPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };
        toolbar.Controls.Add(CreateButton("保存地图", (_, _) => SaveCurrentDocument(), 96));
        toolbar.Controls.Add(CreateButton("应用并预览", (_, _) =>
        {
            SaveCurrentDocument();
            ApplySelectedPreset();
            OpenPreviewSimulator();
        }, 124));
        toolbar.Controls.Add(CreateButton("新增矩形增益", (_, _) => AddFacility("rect"), 116));
        toolbar.Controls.Add(CreateButton("新增多边形", (_, _) => AddFacility("polygon"), 104));
        toolbar.Controls.Add(CreateButton("新增斜面", (_, _) => AddFacet(), 96));
        toolbar.Controls.Add(CreateButton("框选生成斜面", (_, _) => AddFacetFromSelection(), 124));
        toolbar.Controls.Add(CreateButton("删除选中", (_, _) => DeleteCurrentSelection(), 104));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 860,
        };

        var previewTabs = new TabControl
        {
            Dock = DockStyle.Fill,
        };

        _gpuPreview.Dock = DockStyle.Fill;
        _editPreview.Dock = DockStyle.Fill;
        previewTabs.TabPages.Add(new TabPage("GPU 预览") { Controls = { _gpuPreview } });
        previewTabs.TabPages.Add(new TabPage("编辑辅助") { Controls = { _editPreview } });
        previewTabs.SelectedIndex = 0;

        split.Panel1.Controls.Add(previewTabs);
        split.Panel2.Controls.Add(BuildInspectorPanel());

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(_statusLabel, 0, 2);
        return root;
    }

    private Control BuildInspectorPanel()
    {
        _tabControl.Dock = DockStyle.Fill;

        _mapGrid.Dock = DockStyle.Fill;
        _surfaceGrid.Dock = DockStyle.Fill;
        _facilityGrid.Dock = DockStyle.Fill;
        _facetGrid.Dock = DockStyle.Fill;
        _fineTerrainGrid.Dock = DockStyle.Fill;
        _mapGrid.ToolbarVisible = false;
        _surfaceGrid.ToolbarVisible = false;
        _facilityGrid.ToolbarVisible = false;
        _facetGrid.ToolbarVisible = false;
        _fineTerrainGrid.ToolbarVisible = false;
        _mapGrid.HelpVisible = true;
        _surfaceGrid.HelpVisible = true;
        _facilityGrid.HelpVisible = true;
        _facetGrid.HelpVisible = true;
        _fineTerrainGrid.HelpVisible = true;
        _mapGrid.PropertyValueChanged += (_, _) => OnDocumentModified();
        _surfaceGrid.PropertyValueChanged += (_, _) => OnDocumentModified();
        _facilityGrid.PropertyValueChanged += (_, _) => OnDocumentModified();
        _facetGrid.PropertyValueChanged += (_, _) => OnDocumentModified();

        _tabControl.TabPages.Add(new TabPage("地图参数") { Controls = { _mapGrid } });
        _tabControl.TabPages.Add(new TabPage("地形数据") { Controls = { _surfaceGrid } });
        _tabControl.TabPages.Add(new TabPage("增益/互动区域") { Controls = { BuildListAndGrid(_facilityList, _facilityGrid, "增益与场地互动区域") } });
        _tabControl.TabPages.Add(new TabPage("斜面三角面") { Controls = { BuildListAndGrid(_facetList, _facetGrid, "3D 斜面 / 边缘三角面") } });

        _tabControl.TabPages.Add(new TabPage("精细地图") { Controls = { BuildFineTerrainPanel() } });

        _facilityList.Dock = DockStyle.Fill;
        _facilityList.DisplayMember = nameof(FacilityRegionEditorModel.Id);
        _facilityList.SelectedIndexChanged += (_, _) => BindSelectedFacility();

        _facetList.Dock = DockStyle.Fill;
        _facetList.DisplayMember = nameof(TerrainFacetEditorModel.Id);
        _facetList.SelectedIndexChanged += (_, _) => BindSelectedFacet();

        return _tabControl;
    }

    private Control BuildFineTerrainPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
        };
        toolbar.Controls.Add(CreateButton("刷新关联", (_, _) => BindFineTerrainMetadata(), 108));
        toolbar.Controls.Add(CreateButton("打开精细编辑器", (_, _) => LaunchFineTerrainEditor(), 116));
        toolbar.Controls.Add(CreateButton("打开地图目录", (_, _) => OpenFineTerrainFolder(), 116));

        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_fineTerrainGrid, 0, 1);
        return layout;
    }

    private static Control BuildListAndGrid(ListBox listBox, PropertyGrid grid, string headerText)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 64));

        var header = new Label
        {
            Dock = DockStyle.Fill,
            Text = headerText,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(listBox, 0, 1);
        layout.Controls.Add(grid, 0, 2);
        return layout;
    }

    private Button CreateButton(string text, EventHandler onClick, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += onClick;
        return button;
    }

    private void LaunchFineTerrainEditor()
    {
        if (_document is null)
        {
            _statusLabel.Text = "请先加载地图，再打开内嵌精细地形编辑器。";
            return;
        }

        try
        {
            LoadLargeTerrainInProcessLauncher.OpenTerrainEditorAsync(_document.PresetName);
            _statusLabel.Text = "已打开 LoadLargeTerrain 精细地形编辑器。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"打开精细地形编辑器失败：{ex.Message}";
        }
    }

    private void OpenFineTerrainFolder()
    {
        string folder = _layout.ResolvePath("maps", "rmuc26map");
        if (!Directory.Exists(folder))
        {
            _statusLabel.Text = "精细地形目录不存在。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
            _statusLabel.Text = "已打开精细地形目录。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"打开目录失败：{ex.Message}";
        }
    }

    private void BindFineTerrainMetadata()
    {
        if (_document is null)
        {
            _fineTerrainGrid.SelectedObject = null;
            return;
        }

        string presetDirectory = Path.GetDirectoryName(_document.SourcePath) ?? _layout.RootPath;
        string annotationPath = ResolveMapRelativePath("annotation_path", presetDirectory);
        string runtimeCachePath = ResolveRuntimeGridSourcePath(presetDirectory);
        string fineTerrainFolder = !string.IsNullOrWhiteSpace(annotationPath)
            ? Path.GetDirectoryName(annotationPath) ?? _layout.ResolvePath("maps", "rmuc26map")
            : _layout.ResolvePath("maps", "rmuc26map");
        string modelPath = Path.Combine(fineTerrainFolder, "RMUC2026_MAP.glb");
        _fineTerrainGrid.SelectedObject = new FineTerrainIntegrationViewModel
        {
            PresetName = _document.PresetName,
            MapJsonPath = _document.SourcePath,
            FineTerrainFolder = fineTerrainFolder,
            AnnotationPath = annotationPath,
            AnnotationExists = File.Exists(annotationPath),
            RuntimeCachePath = runtimeCachePath,
            RuntimeCacheExists = File.Exists(runtimeCachePath),
            ModelPath = modelPath,
            ModelExists = File.Exists(modelPath),
            ExternalEditorProject = "内嵌 WinForms 精细地形编辑器",
            ExternalEditorAvailable = true,
        };
    }

    private string ResolveMapRelativePath(string propertyName, string presetDirectory)
    {
        if (_document?.RawMap is not JsonObject rawMap)
        {
            return string.Empty;
        }

        string? relativePath = rawMap[propertyName]?.ToString();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        string combined = Path.Combine(presetDirectory, relativePath);
        return Path.GetFullPath(combined);
    }

    private string ResolveRuntimeGridSourcePath(string presetDirectory)
    {
        if (_document?.RawMap is not JsonObject rawMap
            || rawMap["runtime_grid"] is not JsonObject runtimeGrid)
        {
            return string.Empty;
        }

        string? relativePath = runtimeGrid["source_path"]?.ToString();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        return Path.GetFullPath(Path.Combine(presetDirectory, relativePath));
    }

    private void ReloadPresets(bool selectCurrentPreset)
    {
        string? selected = selectCurrentPreset ? _service.GetActiveMapPreset(_layout) : _presetList.SelectedItem?.ToString();
        _presetList.Items.Clear();
        foreach (string preset in _service.ListMapPresets(_layout))
        {
            _presetList.Items.Add(preset);
        }

        _currentLabel.Text = $"当前预设：{_service.GetActiveMapPreset(_layout)}";
        if (!string.IsNullOrWhiteSpace(selected))
        {
            int index = _presetList.Items.IndexOf(selected);
            if (index >= 0)
            {
                _presetList.SelectedIndex = index;
            }
        }

        if (_presetList.SelectedIndex < 0 && _presetList.Items.Count > 0)
        {
            _presetList.SelectedIndex = 0;
        }

        _statusLabel.Text = "地图预设列表已加载。";
    }

    private void LoadSelectedPreset()
    {
        string? preset = _presetList.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        try
        {
            _document = _service.LoadPresetDocument(_layout, preset);
            _editPreview.Document = _document;
            _mapGrid.SelectedObject = _document;
            _surfaceGrid.SelectedObject = _document.TerrainSurface;
            BindFineTerrainMetadata();
            RebindFacilityList();
            RebindFacetList();
            _editPreview.MarkSceneDirty();
            _gpuPreview.QueueReload();
            _statusLabel.Text = $"已加载 {preset}，3D 地形编辑器就绪。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"加载失败：{ex.Message}";
        }
    }

    private void RebindFacilityList()
    {
        _facilityList.DataSource = null;
        if (_document is null)
        {
            return;
        }

        _facilityList.DataSource = _document.Facilities;
        _facilityList.DisplayMember = nameof(FacilityRegionEditorModel.Id);
        _editPreview.SelectedFacilityId = _facilityList.SelectedItem is FacilityRegionEditorModel facility ? facility.Id : null;
        _editPreview.Invalidate();
    }

    private void RebindFacetList()
    {
        _facetList.DataSource = null;
        if (_document is null)
        {
            return;
        }

        _facetList.DataSource = _document.TerrainFacets;
        _facetList.DisplayMember = nameof(TerrainFacetEditorModel.Id);
        _editPreview.SelectedFacetId = _facetList.SelectedItem is TerrainFacetEditorModel facet ? facet.Id : null;
        _editPreview.Invalidate();
    }

    private void BindSelectedFacility()
    {
        if (_facilityList.SelectedItem is not FacilityRegionEditorModel facility)
        {
            return;
        }

        _facilityGrid.SelectedObject = facility;
        _editPreview.SelectedFacilityId = facility.Id;
        _editPreview.SelectedFacetId = null;
        _editPreview.Invalidate();
    }

    private void BindSelectedFacet()
    {
        if (_facetList.SelectedItem is not TerrainFacetEditorModel facet)
        {
            return;
        }

        _facetGrid.SelectedObject = facet;
        _editPreview.SelectedFacetId = facet.Id;
        _editPreview.SelectedFacilityId = null;
        _editPreview.Invalidate();
    }

    private void AddFacility(string shape)
    {
        if (_document is null)
        {
            return;
        }

        int nextIndex = _document.Facilities.Count + 1;
        var facility = new FacilityRegionEditorModel
        {
            Id = $"custom_region_{nextIndex}",
            Type = shape == "polygon" ? "terrain_slope_red" : "wall",
            Team = "neutral",
            Shape = shape,
            X1 = 0,
            Y1 = 0,
            X2 = Math.Min(_document.Width, 120),
            Y2 = Math.Min(_document.Height, 80),
            HeightM = 0.4,
            Thickness = 12.0,
            PointsText = "120,120; 220,120; 220,180; 120,180",
        };
        _document.Facilities.Add(facility);
        RebindFacilityList();
        _facilityList.SelectedItem = facility;
        _tabControl.SelectedIndex = 2;
        _statusLabel.Text = $"已新增 {facility.Id}。";
    }

    private void AddFacet()
    {
        if (_document is null)
        {
            return;
        }

        int nextIndex = _document.TerrainFacets.Count + 1;
        var facet = new TerrainFacetEditorModel
        {
            Id = $"slope_facet_{nextIndex}",
            Type = "slope",
            Team = "neutral",
            TopColorHex = "#8A9576",
            SideColorHex = "#4B4F55",
            PointsText = "200,180; 320,180; 320,260; 200,260",
            HeightsText = "0,0,0.4,0.4",
        };
        _document.TerrainFacets.Add(facet);
        RebindFacetList();
        _facetList.SelectedItem = facet;
        _tabControl.SelectedIndex = 3;
        _statusLabel.Text = $"已新增 {facet.Id}。";
    }

    private void AddFacetFromSelection()
    {
        if (_document is null)
        {
            return;
        }

        if (_editPreview.MapSelection is not RectangleF selection
            || selection.Width < 2f
            || selection.Height < 2f)
        {
            _statusLabel.Text = "请先在顶视图或分屏预览中框选区域，再点击框选斜面。";
            return;
        }

        int nextIndex = _document.TerrainFacets.Count + 1;
        string heightsText = ResolveSelectionSlopeHeightsText(_editPreview.MapSelectionStart, _editPreview.MapSelectionEnd);
        var facet = new TerrainFacetEditorModel
        {
            Id = $"box_slope_facet_{nextIndex}",
            Type = "slope",
            Team = "neutral",
            TopColorHex = "#8A9576",
            SideColorHex = "#4B4F55",
            PointsText = FormatRectPoints(selection),
            HeightsText = heightsText,
        };

        _document.TerrainFacets.Add(facet);
        RebindFacetList();
        _facetList.SelectedItem = facet;
        _tabControl.SelectedIndex = 3;
        _statusLabel.Text = $"已根据框选区域新增 {facet.Id}。保存地图后即可在模拟器中使用。";
    }

    private static string FormatRectPoints(RectangleF rect)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{rect.Left:0.###},{rect.Top:0.###}; {rect.Right:0.###},{rect.Top:0.###}; {rect.Right:0.###},{rect.Bottom:0.###}; {rect.Left:0.###},{rect.Bottom:0.###}");
    }

    private static string ResolveSelectionSlopeHeightsText(PointF? selectionStart, PointF? selectionEnd)
    {
        const string bottomHigh = "0,0,0.4,0.4";
        if (selectionStart is not PointF start || selectionEnd is not PointF end)
        {
            return bottomHigh;
        }

        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            return dx >= 0f
                ? "0,0.4,0.4,0"
                : "0.4,0,0,0.4";
        }

        return dy >= 0f
            ? bottomHigh
            : "0.4,0.4,0,0";
    }

    private void DeleteCurrentSelection()
    {
        if (_document is null)
        {
            return;
        }

        if (_tabControl.SelectedIndex == 2 && _facilityList.SelectedItem is FacilityRegionEditorModel facility)
        {
            _document.Facilities.Remove(facility);
            RebindFacilityList();
            _facilityGrid.SelectedObject = null;
            _statusLabel.Text = $"已删除 {facility.Id}。";
            return;
        }

        if (_tabControl.SelectedIndex == 3 && _facetList.SelectedItem is TerrainFacetEditorModel facet)
        {
            _document.TerrainFacets.Remove(facet);
            RebindFacetList();
            _facetGrid.SelectedObject = null;
            _statusLabel.Text = $"已删除 {facet.Id}。";
        }
    }

    private void SaveCurrentDocument()
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            _service.SavePresetDocument(_document);
            _gpuPreview.QueueReload();
            _statusLabel.Text = $"已保存 {_document.PresetName}。";
            _currentLabel.Text = $"当前预设：{_service.GetActiveMapPreset(_layout)}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"保存失败：{ex.Message}";
        }
    }

    private void ApplySelectedPreset()
    {
        string? preset = _presetList.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        try
        {
            SaveCurrentDocument();
            IReadOnlyList<string> files = _service.SetActiveMapPreset(_layout, preset);
            _gpuPreview.QueueReload();
            _currentLabel.Text = $"当前预设：{preset}";
            _statusLabel.Text = $"已将预设应用到 {files.Count} 个配置文件。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"应用失败：{ex.Message}";
        }
    }

    private void OpenPreviewSimulator()
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            SaveCurrentDocument();
            var preview = new Simulator3dForm(new Simulator3dOptions
            {
                MapPreset = _document.PresetName,
                RendererMode = "gpu",
                StartInMatch = true,
            });
            preview.Show(this);
            _statusLabel.Text = $"已打开 {_document.PresetName} 的局内预览（GPU OpenGL 3D）。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"预览失败：{ex.Message}";
        }
    }

    private void OnDocumentModified()
    {
        _editPreview.MarkSceneDirty();
        _facilityList.Refresh();
        _facetList.Refresh();
        if (_facilityGrid.SelectedObject is FacilityRegionEditorModel facility)
        {
            _editPreview.SelectedFacilityId = facility.Id;
        }

        if (_facetGrid.SelectedObject is TerrainFacetEditorModel facet)
        {
            _editPreview.SelectedFacetId = facet.Id;
        }

        _statusLabel.Text = "编辑辅助视图已更新；GPU 预览会跟随最近一次保存后的运行时结果。";
    }

    private Simulator3dOptions? BuildGpuPreviewOptions()
    {
        if (_document is null || string.IsNullOrWhiteSpace(_document.PresetName))
        {
            return null;
        }

        return new Simulator3dOptions
        {
            MapPreset = _document.PresetName,
            MatchMode = "map_component_test",
            StartInMatch = true,
            RendererMode = "gpu",
            SelectedTeam = "red",
        };
    }

    private void UpdateSelectionStatus()
    {
        if (_editPreview.MapSelection is not RectangleF selection || selection.Width < 2f || selection.Height < 2f)
        {
            return;
        }

        _statusLabel.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"已框选: x={selection.Left:0.#}..{selection.Right:0.#}, y={selection.Top:0.#}..{selection.Bottom:0.#}。点击“框选生成斜面”可创建斜面。");
    }

    private void SyncSelectionFromPreview()
    {
        if (_document is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_editPreview.SelectedFacilityId))
        {
            FacilityRegionEditorModel? facility = _document.Facilities.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, _editPreview.SelectedFacilityId, StringComparison.OrdinalIgnoreCase));
            if (facility is not null)
            {
                _facilityList.SelectedItem = facility;
                _tabControl.SelectedIndex = 2;
                _statusLabel.Text = $"正在俯视图中编辑增益/互动区域 {facility.Id}。";
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(_editPreview.SelectedFacetId))
        {
            TerrainFacetEditorModel? facet = _document.TerrainFacets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, _editPreview.SelectedFacetId, StringComparison.OrdinalIgnoreCase));
            if (facet is not null)
            {
                _facetList.SelectedItem = facet;
                _tabControl.SelectedIndex = 3;
                _statusLabel.Text = $"正在俯视图中编辑斜面 {facet.Id}。";
            }
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    private sealed class FineTerrainIntegrationViewModel
    {
        [DisplayName("预设")]
        public string PresetName { get; init; } = string.Empty;

        [DisplayName("地图 Json")]
        public string MapJsonPath { get; init; } = string.Empty;

        [DisplayName("精细地图目录")]
        public string FineTerrainFolder { get; init; } = string.Empty;

        [DisplayName("注解路径")]
        public string AnnotationPath { get; init; } = string.Empty;

        [DisplayName("注解是否存在")]
        public bool AnnotationExists { get; init; }

        [DisplayName("运行时缓存路径")]
        public string RuntimeCachePath { get; init; } = string.Empty;

        [DisplayName("运行时缓存是否存在")]
        public bool RuntimeCacheExists { get; init; }

        [DisplayName("模型路径")]
        public string ModelPath { get; init; } = string.Empty;

        [DisplayName("模型是否存在")]
        public bool ModelExists { get; init; }

        [DisplayName("外部编辑器")]
        public string ExternalEditorProject { get; init; } = string.Empty;

        [DisplayName("编辑器可用")]
        public bool ExternalEditorAvailable { get; init; }
    }
}
