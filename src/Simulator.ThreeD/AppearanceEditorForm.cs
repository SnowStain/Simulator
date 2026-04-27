using System.ComponentModel;
using System.Windows.Forms;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal sealed class AppearanceEditorForm : Form
{
    private readonly record struct TreeSelection(string RoleKey, string? SubtypeKey);

    private sealed class FilteredProfileView : ICustomTypeDescriptor
    {
        private readonly object _target;
        private readonly HashSet<string> _hiddenProperties;

        public FilteredProfileView(object target, IEnumerable<string> hiddenProperties)
        {
            _target = target;
            _hiddenProperties = new HashSet<string>(hiddenProperties, StringComparer.OrdinalIgnoreCase);
        }

        public AttributeCollection GetAttributes() => TypeDescriptor.GetAttributes(_target, true);
        public string? GetClassName() => TypeDescriptor.GetClassName(_target, true);
        public string? GetComponentName() => TypeDescriptor.GetComponentName(_target, true);
        public TypeConverter? GetConverter() => TypeDescriptor.GetConverter(_target, true);
        public EventDescriptor? GetDefaultEvent() => TypeDescriptor.GetDefaultEvent(_target, true);
        public PropertyDescriptor? GetDefaultProperty() => TypeDescriptor.GetDefaultProperty(_target, true);
        public object? GetEditor(Type editorBaseType) => TypeDescriptor.GetEditor(_target, editorBaseType, true);
        public EventDescriptorCollection GetEvents(Attribute[]? attributes) => TypeDescriptor.GetEvents(_target, attributes, true);
        public EventDescriptorCollection GetEvents() => TypeDescriptor.GetEvents(_target, true);
        public object GetPropertyOwner(PropertyDescriptor? pd) => _target;
        public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();

        public PropertyDescriptorCollection GetProperties()
        {
            PropertyDescriptorCollection source = TypeDescriptor.GetProperties(_target, true);
            PropertyDescriptor[] filtered = source
                .Cast<PropertyDescriptor>()
                .Where(property => !_hiddenProperties.Contains(property.Name))
                .ToArray();
            return new PropertyDescriptorCollection(filtered, readOnly: true);
        }
    }

    private readonly ProjectLayout _layout;
    private readonly AppearanceEditorService _service;
    private readonly TreeView _profileTree = new();
    private readonly PropertyGrid _propertyGrid = new();
    private readonly ListBox _validationList = new();
    private readonly Label _status = new();
    private readonly AppearanceProfilePreviewControl _helperPreview = new();
    private readonly EmbeddedSimulatorPreviewHost _gpuPreview = new();

    private RobotAppearanceRoot _document = new();
    private RobotAppearanceRoot _loadedSnapshot = new();
    private readonly string _previewAppearancePath;
    private bool _planeDrivePreviewEnabled;

    public AppearanceEditorForm()
    {
        _layout = ProjectLayout.Discover();
        _service = new AppearanceEditorService();
        _previewAppearancePath = _layout.ResolvePath("build_verify", "editor_preview", "appearance_preview.json");
        _gpuPreview.EmptyText = "请先选择外观配置，再加载 GPU 预览。";
        _gpuPreview.OptionsFactory = BuildGpuPreviewOptions;
        KeyPreview = true;
        KeyDown += OnEditorKeyDown;

        Text = "外观编辑器";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new System.Drawing.Size(1320, 840);
        MinimumSize = new System.Drawing.Size(1100, 720);

        var shell = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 260,
            FixedPanel = FixedPanel.Panel1,
        };

        _profileTree.Dock = DockStyle.Fill;
        _profileTree.HideSelection = false;
        _profileTree.AfterSelect += (_, _) => BindSelection();

        var treeHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            Text = "外观配置",
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };

        shell.Panel1.Controls.Add(_profileTree);
        shell.Panel1.Controls.Add(treeHeader);

        var rightLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 6, 8, 4),
        };

        Button reloadButton = CreateToolbarButton("重新加载", (_, _) => ReloadDocument(preserveSelection: true));
        Button saveButton = CreateToolbarButton("保存", (_, _) => SaveDocument());
        Button validateButton = CreateToolbarButton("校验", (_, _) => RefreshValidation());
        Button resetButton = CreateToolbarButton("重置当前项", (_, _) => ResetSelected());
        Button defaultSubtypeButton = CreateToolbarButton("设为默认子类型", (_, _) => SetDefaultSubtype());
        Button addSubtypeButton = CreateToolbarButton("新增子类型", (_, _) => AddSubtype());
        Button deleteSubtypeButton = CreateToolbarButton("删除子类型", (_, _) => DeleteSubtype());
        Button openPreviewButton = CreateToolbarButton("打开模拟器预览", (_, _) => OpenSimulatorPreview());
        toolbar.Controls.Add(reloadButton);
        toolbar.Controls.Add(saveButton);
        toolbar.Controls.Add(validateButton);
        toolbar.Controls.Add(resetButton);
        toolbar.Controls.Add(defaultSubtypeButton);
        toolbar.Controls.Add(addSubtypeButton);
        toolbar.Controls.Add(deleteSubtypeButton);
        toolbar.Controls.Add(CreateToolbarButton("F6 Flat Drive Preview", (_, _) => TogglePlaneDrivePreview()));
        toolbar.Controls.Add(openPreviewButton);

        var editorSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 470,
            FixedPanel = FixedPanel.Panel1,
        };

        _propertyGrid.Dock = DockStyle.Fill;
        _propertyGrid.HelpVisible = true;
        _propertyGrid.ToolbarVisible = false;
        _propertyGrid.PropertyValueChanged += (_, _) =>
        {
            _document.EnsureInitialized();
            _helperPreview.Invalidate();
            QueueGpuPreviewReload();
            RefreshValidation();
        };

        var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var previewTabs = new TabControl { Dock = DockStyle.Fill };
        _gpuPreview.Dock = DockStyle.Fill;
        _helperPreview.Dock = DockStyle.Fill;
        previewTabs.TabPages.Add(new TabPage("GPU 预览") { Controls = { _gpuPreview } });
        previewTabs.TabPages.Add(new TabPage("编辑辅助") { Controls = { _helperPreview } });
        previewTabs.SelectedIndex = 0;
        previewHost.Controls.Add(previewTabs);

        editorSplit.Panel1.Controls.Add(_propertyGrid);
        editorSplit.Panel2.Controls.Add(previewHost);

        var validationPanel = new Panel { Dock = DockStyle.Fill };
        var validationHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "校验与工程适配说明",
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };
        _validationList.Dock = DockStyle.Fill;
        validationPanel.Controls.Add(_validationList);
        validationPanel.Controls.Add(validationHeader);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(8, 0, 0, 0);

        rightLayout.Controls.Add(toolbar, 0, 0);
        rightLayout.Controls.Add(editorSplit, 0, 1);
        rightLayout.Controls.Add(validationPanel, 0, 2);
        rightLayout.Controls.Add(_status, 0, 3);

        shell.Panel2.Controls.Add(rightLayout);
        Controls.Add(shell);

        ReloadDocument(preserveSelection: false);
    }

    private Button CreateToolbarButton(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, Width = 132, Height = 28 };
        button.Click += onClick;
        return button;
    }

    private void ReloadDocument(bool preserveSelection)
    {
        TreeSelection? selection = preserveSelection ? GetCurrentSelection() : null;
        _document = _service.LoadLatestAppearanceDocument(_layout);
        _document.EnsureInitialized();
        _loadedSnapshot = RobotAppearanceJsonSerializer.Deserialize(RobotAppearanceJsonSerializer.Serialize(_document));
        RebuildTree(selection);
        RefreshValidation();
        QueueGpuPreviewReload();
        _status.Text = $"已加载 {Path.GetRelativePath(_layout.RootPath, _layout.AppearancePresetPath)}";
    }

    private void RebuildTree(TreeSelection? selection)
    {
        _profileTree.BeginUpdate();
        _profileTree.Nodes.Clear();

        foreach (string roleKey in GetOrderedRoleKeys())
        {
            if (!_document.Profiles.TryGetValue(roleKey, out RobotAppearanceProfileDefinition? profile))
            {
                continue;
            }

            var node = new TreeNode(roleKey) { Tag = new TreeSelection(roleKey, null) };
            if (string.Equals(roleKey, "infantry", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string subtypeKey in profile.SubtypeProfiles.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
                {
                    node.Nodes.Add(new TreeNode($"子类型：{subtypeKey}") { Tag = new TreeSelection(roleKey, subtypeKey) });
                }
            }

            _profileTree.Nodes.Add(node);
        }

        _profileTree.ExpandAll();
        SelectTreeNode(selection);
        _profileTree.EndUpdate();
    }

    private IEnumerable<string> GetOrderedRoleKeys()
    {
        string[] preferred = { "outpost", "base", "energy_mechanism", "hero", "engineer", "infantry", "sentry" };
        foreach (string roleKey in preferred)
        {
            if (_document.Profiles.ContainsKey(roleKey))
            {
                yield return roleKey;
            }
        }

        foreach (string roleKey in _document.Profiles.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            if (!preferred.Contains(roleKey, StringComparer.OrdinalIgnoreCase))
            {
                yield return roleKey;
            }
        }
    }

    private void SelectTreeNode(TreeSelection? selection)
    {
        if (_profileTree.Nodes.Count == 0)
        {
            _propertyGrid.SelectedObject = null;
            _helperPreview.Profile = null;
            _validationList.Items.Clear();
            return;
        }

        TreeNode? found = null;
        if (selection is TreeSelection desired)
        {
            found = FindNode(_profileTree.Nodes, desired);
        }

        _profileTree.SelectedNode = found ?? _profileTree.Nodes[0];
    }

    private static TreeNode? FindNode(TreeNodeCollection nodes, TreeSelection target)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is TreeSelection current
                && string.Equals(current.RoleKey, target.RoleKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.SubtypeKey ?? string.Empty, target.SubtypeKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            TreeNode? child = FindNode(node.Nodes, target);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private TreeSelection? GetCurrentSelection()
        => _profileTree.SelectedNode?.Tag is TreeSelection selection ? selection : null;

    private void BindSelection()
    {
        TreeSelection? selection = GetCurrentSelection();
        if (selection is null)
        {
            _propertyGrid.SelectedObject = null;
            _helperPreview.Profile = null;
            return;
        }

        if (!TryResolveSelection(selection.Value, out RobotAppearanceProfileDefinition? profile))
        {
            _propertyGrid.SelectedObject = null;
            _helperPreview.Profile = null;
            return;
        }

        _propertyGrid.SelectedObject = BuildPropertyGridSelection(selection.Value.RoleKey, profile!);
        _helperPreview.RoleKey = selection.Value.RoleKey;
        _helperPreview.SubtypeKey = selection.Value.SubtypeKey;
        _helperPreview.Profile = profile;
        _helperPreview.Invalidate();
        QueueGpuPreviewReload();
        RefreshValidation();
    }

    private bool TryResolveSelection(TreeSelection selection, out RobotAppearanceProfileDefinition? profile)
    {
        profile = null;
        if (!_document.Profiles.TryGetValue(selection.RoleKey, out RobotAppearanceProfileDefinition? roleProfile))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(selection.SubtypeKey))
        {
            profile = roleProfile;
            return true;
        }

        return roleProfile.SubtypeProfiles.TryGetValue(selection.SubtypeKey, out profile);
    }

    private object BuildPropertyGridSelection(string roleKey, RobotAppearanceProfileDefinition profile)
    {
        if (roleKey is not ("base" or "outpost" or "energy_mechanism"))
        {
            return profile;
        }

        string[] hidden =
        {
            nameof(RobotAppearanceProfileDefinition.BodyClearanceM),
            nameof(RobotAppearanceProfileDefinition.WheelStyle),
            nameof(RobotAppearanceProfileDefinition.WheelRadiusM),
            nameof(RobotAppearanceProfileDefinition.WheelCount),
            nameof(RobotAppearanceProfileDefinition.RearLegWheelRadiusM),
            nameof(RobotAppearanceProfileDefinition.SuspensionStyle),
            nameof(RobotAppearanceProfileDefinition.CustomWheelPositionsText),
            nameof(RobotAppearanceProfileDefinition.WheelOrbitYawsText),
            nameof(RobotAppearanceProfileDefinition.WheelSelfYawsText),
            nameof(RobotAppearanceProfileDefinition.GimbalLengthM),
            nameof(RobotAppearanceProfileDefinition.GimbalWidthM),
            nameof(RobotAppearanceProfileDefinition.GimbalBodyHeightM),
            nameof(RobotAppearanceProfileDefinition.GimbalMountGapM),
            nameof(RobotAppearanceProfileDefinition.GimbalMountLengthM),
            nameof(RobotAppearanceProfileDefinition.GimbalMountWidthM),
            nameof(RobotAppearanceProfileDefinition.GimbalMountHeightM),
            nameof(RobotAppearanceProfileDefinition.BarrelLengthM),
            nameof(RobotAppearanceProfileDefinition.BarrelRadiusM),
            nameof(RobotAppearanceProfileDefinition.GimbalHeightM),
            nameof(RobotAppearanceProfileDefinition.GimbalOffsetXM),
            nameof(RobotAppearanceProfileDefinition.GimbalOffsetYM),
            nameof(RobotAppearanceProfileDefinition.ArmStyle),
            nameof(RobotAppearanceProfileDefinition.FrontClimbAssistStyle),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistStyle),
            nameof(RobotAppearanceProfileDefinition.FrontClimbAssistTopLengthM),
            nameof(RobotAppearanceProfileDefinition.FrontClimbAssistBottomLengthM),
            nameof(RobotAppearanceProfileDefinition.FrontClimbAssistPlateWidthM),
            nameof(RobotAppearanceProfileDefinition.FrontClimbAssistPlateHeightM),
            nameof(RobotAppearanceProfileDefinition.FrontClimbAssistForwardOffsetM),
            nameof(RobotAppearanceProfileDefinition.FrontClimbAssistInnerOffsetM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistUpperLengthM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistLowerLengthM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistUpperWidthM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistUpperHeightM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistLowerWidthM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistLowerHeightM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistMountOffsetXM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistMountHeightM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistInnerOffsetM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistUpperPairGapM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistHingeRadiusM),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistKneeMinDeg),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistKneeMaxDeg),
            nameof(RobotAppearanceProfileDefinition.RearClimbAssistKneeDirection),
            nameof(RobotAppearanceProfileDefinition.ChassisSubtype),
            nameof(RobotAppearanceProfileDefinition.DefaultChassisSubtype),
            nameof(RobotAppearanceProfileDefinition.ChassisSupportsJump),
            nameof(RobotAppearanceProfileDefinition.ChassisSpeedScale),
            nameof(RobotAppearanceProfileDefinition.ChassisDrivePowerLimitW),
            nameof(RobotAppearanceProfileDefinition.ChassisDriveIdleDrawW),
            nameof(RobotAppearanceProfileDefinition.ChassisDriveRpmCoeff),
            nameof(RobotAppearanceProfileDefinition.ChassisDriveAccelCoeff),
        };

        return new FilteredProfileView(profile, hidden);
    }

    private void SaveDocument()
    {
        try
        {
            _document.EnsureInitialized();
            IReadOnlyList<string> errors = RobotAppearanceValidator.ValidateRoot(_document);
            _service.SaveLatestAppearanceDocument(_layout, _document);
            _loadedSnapshot = RobotAppearanceJsonSerializer.Deserialize(RobotAppearanceJsonSerializer.Serialize(_document));
            QueueGpuPreviewReload();
            _status.Text = errors.Count == 0
                ? "已保存外观文档。"
                : $"已保存外观文档，包含 {errors.Count} 条校验警告。";
            RefreshValidation();
        }
        catch (Exception ex)
        {
            _status.Text = $"保存失败：{ex.Message}";
        }
    }

    private void RefreshValidation()
    {
        _validationList.Items.Clear();
        TreeSelection? selection = GetCurrentSelection();
        if (selection is not null
            && TryResolveSelection(selection.Value, out RobotAppearanceProfileDefinition? profile)
            && profile is not null)
        {
            foreach (string issue in RobotAppearanceValidator.ValidateProfile(selection.Value.RoleKey, selection.Value.SubtypeKey, profile))
            {
                _validationList.Items.Add(issue);
            }

            IReadOnlyList<(double X, double Y)> wheelOffsets = profile.GetWheelOffsetsOrDefaults();
            if (selection.Value.RoleKey is "base" or "outpost" or "energy_mechanism")
            {
                _validationList.Items.Add($"预览：结构={selection.Value.RoleKey}，形状={profile.BodyShape}，高度={profile.BodyHeightM:0.###}m");
                _validationList.Items.Add($"结构：抬升={profile.StructureBaseLiftM:0.###}m，顶部装甲高度={profile.StructureTopArmorCenterHeightM:0.###}m");
                if (selection.Value.RoleKey == "energy_mechanism")
                {
                    _validationList.Items.Add($"能量机关：转子半径={profile.StructureRotorRadiusM:0.###}m，悬臂间距={profile.StructureCantileverPairGapM:0.###}m");
                }
            }
            else
            {
                _validationList.Items.Add($"预览：轮组={wheelOffsets.Count}，形状={profile.BodyShape}，轮组样式={profile.WheelStyle}");
                _validationList.Items.Add($"动力：功率上限={profile.ChassisDrivePowerLimitW:0.###}W，加速度系数={profile.ChassisDriveAccelCoeff:0.###}，怠速功耗={profile.ChassisDriveIdleDrawW:0.###}W");
                _validationList.Items.Add($"地形：离地间隙={profile.BodyClearanceM:0.###}m，后腿样式={profile.RearClimbAssistStyle}，支持跳跃={profile.ChassisSupportsJump}");
            }
            if (!string.IsNullOrWhiteSpace(selection.Value.SubtypeKey)
                && _document.Profiles.TryGetValue(selection.Value.RoleKey, out RobotAppearanceProfileDefinition? roleProfile)
                && string.Equals(roleProfile.DefaultChassisSubtype, selection.Value.SubtypeKey, StringComparison.OrdinalIgnoreCase))
            {
                _validationList.Items.Add("当前子类型为运行时默认子类型。");
            }
        }

        if (_validationList.Items.Count == 0)
        {
            _validationList.Items.Add("未发现校验问题。");
        }
    }

    private void ResetSelected()
    {
        TreeSelection? selection = GetCurrentSelection();
        if (selection is null)
        {
            return;
        }

        _document = RobotAppearanceJsonSerializer.Deserialize(RobotAppearanceJsonSerializer.Serialize(_loadedSnapshot));
        RebuildTree(selection);
        BindSelection();
        QueueGpuPreviewReload();
        _status.Text = "已将当前外观重置到最近一次加载或保存的状态。";
    }

    private void SetDefaultSubtype()
    {
        TreeSelection? selection = GetCurrentSelection();
        if (selection is null || string.IsNullOrWhiteSpace(selection.Value.SubtypeKey))
        {
            _status.Text = "请先选择一个步兵子类型。";
            return;
        }

        if (!_document.Profiles.TryGetValue(selection.Value.RoleKey, out RobotAppearanceProfileDefinition? roleProfile))
        {
            return;
        }

        roleProfile.DefaultChassisSubtype = selection.Value.SubtypeKey ?? string.Empty;
        _propertyGrid.Refresh();
        QueueGpuPreviewReload();
        RefreshValidation();
        _status.Text = $"默认子类型已设置为 {selection.Value.SubtypeKey}。";
    }

    private void AddSubtype()
    {
        TreeSelection? selection = GetCurrentSelection();
        string roleKey = selection?.RoleKey ?? "infantry";
        if (!string.Equals(roleKey, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "当前仅步兵支持子类型编辑。";
            return;
        }

        if (!_document.Profiles.TryGetValue("infantry", out RobotAppearanceProfileDefinition? profile))
        {
            return;
        }

        int suffix = profile.SubtypeProfiles.Count + 1;
        string newKey;
        do
        {
            newKey = $"custom_subtype_{suffix++}";
        } while (profile.SubtypeProfiles.ContainsKey(newKey));

        RobotAppearanceProfileDefinition source = selection is TreeSelection current
            && !string.IsNullOrWhiteSpace(current.SubtypeKey)
            && profile.SubtypeProfiles.TryGetValue(current.SubtypeKey!, out RobotAppearanceProfileDefinition? subtypeProfile)
                ? subtypeProfile
                : profile;
        RobotAppearanceProfileDefinition clone = source.DeepClone();
        clone.ChassisSubtype = newKey;
        clone.RoleKey = "infantry";
        profile.SubtypeProfiles[newKey] = clone;
        RebuildTree(new TreeSelection("infantry", newKey));
        QueueGpuPreviewReload();
        _status.Text = $"已新增子类型 {newKey}。";
    }

    private void DeleteSubtype()
    {
        TreeSelection? selection = GetCurrentSelection();
        if (selection is null || string.IsNullOrWhiteSpace(selection.Value.SubtypeKey))
        {
            _status.Text = "请先选择要删除的步兵子类型。";
            return;
        }

        if (!_document.Profiles.TryGetValue(selection.Value.RoleKey, out RobotAppearanceProfileDefinition? profile))
        {
            return;
        }

        if (!profile.SubtypeProfiles.Remove(selection.Value.SubtypeKey!))
        {
            return;
        }

        if (string.Equals(profile.DefaultChassisSubtype, selection.Value.SubtypeKey, StringComparison.OrdinalIgnoreCase))
        {
            profile.DefaultChassisSubtype = profile.SubtypeProfiles.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty;
        }

        RebuildTree(new TreeSelection(selection.Value.RoleKey, profile.DefaultChassisSubtype));
        QueueGpuPreviewReload();
        _status.Text = $"已删除子类型 {selection.Value.SubtypeKey}。";
    }

    private void OpenSimulatorPreview()
    {
        try
        {
            Simulator3dOptions? options = BuildGpuPreviewOptions();
            if (options is null)
            {
                _status.Text = "请先选择一个外观配置，再打开 GPU 预览。";
                return;
            }

            var preview = new Simulator3dForm(options);
            preview.Show(this);
            _status.Text = "已使用当前外观数据打开模拟器预览。";
        }
        catch (Exception ex)
        {
            _status.Text = $"预览失败：{ex.Message}";
        }
    }

    private void QueueGpuPreviewReload()
    {
        try
        {
            WriteGpuPreviewSnapshot();
            _gpuPreview.QueueReload();
        }
        catch (Exception ex)
        {
            _status.Text = $"GPU 预览快照生成失败：{ex.Message}";
        }
    }

    private Simulator3dOptions? BuildGpuPreviewOptions()
    {
        TreeSelection? selection = GetCurrentSelection();
        if (selection is null)
        {
            return null;
        }

        WriteGpuPreviewSnapshot();

        if (selection.Value.RoleKey is "base" or "outpost" or "energy_mechanism")
        {
            if (_planeDrivePreviewEnabled)
            {
                return null;
            }

            return new Simulator3dOptions
            {
                RendererMode = "gpu",
                StartInMatch = true,
                AppearancePath = _previewAppearancePath,
                PreviewOnly = true,
                PreviewStructure = selection.Value.RoleKey,
                PreviewTeam = string.Equals(selection.Value.RoleKey, "energy_mechanism", StringComparison.OrdinalIgnoreCase) ? null : "blue",
                PreviewRoleKey = selection.Value.RoleKey,
                PreviewSubtypeKey = selection.Value.SubtypeKey,
            };
        }

        string? entityKey = ResolveSingleUnitPreviewEntityKey(selection.Value.RoleKey);
        if (string.IsNullOrWhiteSpace(entityKey))
        {
            return null;
        }

        return new Simulator3dOptions
        {
            RendererMode = "gpu",
            MapPreset = _planeDrivePreviewEnabled ? "blankCanvas" : null,
            StartInMatch = true,
            MatchMode = "single_unit_test",
            SelectedTeam = "blue",
            SingleUnitTestTeam = "blue",
            SingleUnitTestEntityKey = entityKey,
            AppearancePath = _previewAppearancePath,
            PreviewRoleKey = selection.Value.RoleKey,
            PreviewSubtypeKey = selection.Value.SubtypeKey,
        };
    }

    private static string? ResolveSingleUnitPreviewEntityKey(string roleKey)
    {
        return roleKey.ToLowerInvariant() switch
        {
            "hero" => "robot_1",
            "engineer" => "robot_2",
            "infantry" => "robot_3",
            "sentry" => "robot_7",
            _ => null,
        };
    }

    private void TogglePlaneDrivePreview()
    {
        TreeSelection? selection = GetCurrentSelection();
        if (selection is null)
        {
            _status.Text = "Please select a robot profile before enabling flat drive preview.";
            return;
        }

        if (selection.Value.RoleKey is "base" or "outpost" or "energy_mechanism")
        {
            _planeDrivePreviewEnabled = false;
            _status.Text = "Flat drive preview is only available for robot units.";
            QueueGpuPreviewReload();
            return;
        }

        _planeDrivePreviewEnabled = !_planeDrivePreviewEnabled;
        QueueGpuPreviewReload();
        _status.Text = _planeDrivePreviewEnabled
            ? "F6 flat drive preview enabled. Click the GPU preview and drive on blankCanvas."
            : "F6 flat drive preview disabled. Returned to the static appearance preview.";
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.F6)
        {
            return;
        }

        TogglePlaneDrivePreview();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void WriteGpuPreviewSnapshot()
    {
        _document.EnsureInitialized();
        Directory.CreateDirectory(Path.GetDirectoryName(_previewAppearancePath) ?? _layout.RootPath);
        RobotAppearanceJsonSerializer.SaveToFile(_previewAppearancePath, _document);
    }
}
