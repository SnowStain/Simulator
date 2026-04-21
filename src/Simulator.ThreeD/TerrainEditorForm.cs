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
    private readonly ListBox _facilityList = new();
    private readonly ListBox _facetList = new();
    private readonly PropertyGrid _mapGrid = new();
    private readonly PropertyGrid _surfaceGrid = new();
    private readonly PropertyGrid _facilityGrid = new();
    private readonly PropertyGrid _facetGrid = new();
    private readonly Label _statusLabel = new();
    private readonly Label _currentLabel = new();
    private readonly MapPresetPreviewControl _preview = new();
    private readonly TabControl _tabControl = new();

    private MapPresetEditorSettings? _document;

    public TerrainEditorForm()
    {
        _layout = ProjectLayout.Discover();
        _configService = new ConfigurationService();
        _service = new TerrainEditorService(_configService, new AssetCatalogService());

        Text = ".NET Terrain / Map Editor";
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
            Text = "Map Presets",
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

        buttons.Controls.Add(CreateButton("Reload List", (_, _) => ReloadPresets(selectCurrentPreset: false), 104));
        buttons.Controls.Add(CreateButton("Apply Preset", (_, _) => ApplySelectedPreset(), 104));
        buttons.Controls.Add(CreateButton("3D Preview", (_, _) => OpenPreviewSimulator(), 104));

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
        toolbar.Controls.Add(CreateButton("Save Map", (_, _) => SaveCurrentDocument(), 96));
        toolbar.Controls.Add(CreateButton("Apply + Preview", (_, _) =>
        {
            SaveCurrentDocument();
            ApplySelectedPreset();
            OpenPreviewSimulator();
        }, 124));
        toolbar.Controls.Add(CreateButton("Add Rect", (_, _) => AddFacility("rect"), 92));
        toolbar.Controls.Add(CreateButton("Add Polygon", (_, _) => AddFacility("polygon"), 96));
        toolbar.Controls.Add(CreateButton("Add Ramp", (_, _) => AddFacet(), 96));
        toolbar.Controls.Add(CreateButton("Delete Selected", (_, _) => DeleteCurrentSelection(), 120));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 860,
        };

        _preview.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(_preview);
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
        _mapGrid.ToolbarVisible = false;
        _surfaceGrid.ToolbarVisible = false;
        _facilityGrid.ToolbarVisible = false;
        _facetGrid.ToolbarVisible = false;
        _mapGrid.HelpVisible = true;
        _surfaceGrid.HelpVisible = true;
        _facilityGrid.HelpVisible = true;
        _facetGrid.HelpVisible = true;
        _mapGrid.PropertyValueChanged += (_, _) => OnDocumentModified();
        _surfaceGrid.PropertyValueChanged += (_, _) => OnDocumentModified();
        _facilityGrid.PropertyValueChanged += (_, _) => OnDocumentModified();
        _facetGrid.PropertyValueChanged += (_, _) => OnDocumentModified();

        _tabControl.TabPages.Add(new TabPage("Map") { Controls = { _mapGrid } });
        _tabControl.TabPages.Add(new TabPage("Surface") { Controls = { _surfaceGrid } });
        _tabControl.TabPages.Add(new TabPage("Facilities") { Controls = { BuildListAndGrid(_facilityList, _facilityGrid, "Facility Regions") } });
        _tabControl.TabPages.Add(new TabPage("Slope Facets") { Controls = { BuildListAndGrid(_facetList, _facetGrid, "3D Slope / Edge Facets") } });

        _facilityList.Dock = DockStyle.Fill;
        _facilityList.DisplayMember = nameof(FacilityRegionEditorModel.Id);
        _facilityList.SelectedIndexChanged += (_, _) => BindSelectedFacility();

        _facetList.Dock = DockStyle.Fill;
        _facetList.DisplayMember = nameof(TerrainFacetEditorModel.Id);
        _facetList.SelectedIndexChanged += (_, _) => BindSelectedFacet();

        return _tabControl;
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

    private void ReloadPresets(bool selectCurrentPreset)
    {
        string? selected = selectCurrentPreset ? _service.GetActiveMapPreset(_layout) : _presetList.SelectedItem?.ToString();
        _presetList.Items.Clear();
        foreach (string preset in _service.ListMapPresets(_layout))
        {
            _presetList.Items.Add(preset);
        }

        _currentLabel.Text = $"Current preset: {_service.GetActiveMapPreset(_layout)}";
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

        _statusLabel.Text = "Preset list loaded.";
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
            _preview.Document = _document;
            _mapGrid.SelectedObject = _document;
            _surfaceGrid.SelectedObject = _document.TerrainSurface;
            RebindFacilityList();
            RebindFacetList();
            _statusLabel.Text = $"Loaded {preset}. 3D terrain editor ready.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Load failed: {ex.Message}";
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
        _preview.SelectedFacilityId = _facilityList.SelectedItem is FacilityRegionEditorModel facility ? facility.Id : null;
        _preview.Invalidate();
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
        _preview.SelectedFacetId = _facetList.SelectedItem is TerrainFacetEditorModel facet ? facet.Id : null;
        _preview.Invalidate();
    }

    private void BindSelectedFacility()
    {
        if (_facilityList.SelectedItem is not FacilityRegionEditorModel facility)
        {
            return;
        }

        _facilityGrid.SelectedObject = facility;
        _preview.SelectedFacilityId = facility.Id;
        _preview.SelectedFacetId = null;
        _preview.Invalidate();
    }

    private void BindSelectedFacet()
    {
        if (_facetList.SelectedItem is not TerrainFacetEditorModel facet)
        {
            return;
        }

        _facetGrid.SelectedObject = facet;
        _preview.SelectedFacetId = facet.Id;
        _preview.SelectedFacilityId = null;
        _preview.Invalidate();
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
        _statusLabel.Text = $"Added {facility.Id}.";
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
        _statusLabel.Text = $"Added {facet.Id}.";
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
            _statusLabel.Text = $"Deleted {facility.Id}.";
            return;
        }

        if (_tabControl.SelectedIndex == 3 && _facetList.SelectedItem is TerrainFacetEditorModel facet)
        {
            _document.TerrainFacets.Remove(facet);
            RebindFacetList();
            _facetGrid.SelectedObject = null;
            _statusLabel.Text = $"Deleted {facet.Id}.";
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
            _statusLabel.Text = $"Saved {_document.PresetName}.";
            _currentLabel.Text = $"Current preset: {_service.GetActiveMapPreset(_layout)}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Save failed: {ex.Message}";
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
            _currentLabel.Text = $"Current preset: {preset}";
            _statusLabel.Text = $"Applied preset to {files.Count} config file(s).";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Apply failed: {ex.Message}";
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
            _statusLabel.Text = $"Opened simulator preview for {_document.PresetName} (GPU OpenGL 3D).";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Preview failed: {ex.Message}";
        }
    }

    private void OnDocumentModified()
    {
        _preview.Invalidate();
        _facilityList.Refresh();
        _facetList.Refresh();
        if (_facilityGrid.SelectedObject is FacilityRegionEditorModel facility)
        {
            _preview.SelectedFacilityId = facility.Id;
        }

        if (_facetGrid.SelectedObject is TerrainFacetEditorModel facet)
        {
            _preview.SelectedFacetId = facet.Id;
        }
    }
}
