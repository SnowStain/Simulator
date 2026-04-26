using System.Diagnostics;
using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Simulator.Core;
using Simulator.Core.Gameplay;

namespace Simulator.Decision;

internal sealed class DecisionDeploymentForm : Form
{
    private static readonly (string Key, string Label, string Description)[] RoleSpecs =
    {
        ("hero", "英雄", "高机动压制与吊射单位"),
        ("engineer", "工程", "补给、采矿与支援单位"),
        ("infantry", "步兵", "主力推进与中距离火力"),
        ("sentry", "哨兵", "阵地火力与侧向掩护"),
    };

    private static readonly (string Key, string Label)[] ModeSpecs =
    {
        ("aggressive", "压制推进"),
        ("hold", "阵地驻守"),
        ("support", "协同支援"),
        ("flank", "侧翼机动"),
    };

    private readonly ConfigurationService _configurationService;
    private readonly string _configPath;
    private readonly Dictionary<string, Dictionary<string, RadioButton>> _roleModeButtons =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Label _headerPathLabel = new();
    private readonly Label _pathLabel = new();
    private readonly Label _statusLabel = new();

    private bool _suspendWrites;

    public DecisionDeploymentForm()
    {
        ProjectLayout layout = ProjectLayout.Discover();
        _configurationService = new ConfigurationService();
        _configPath = _configurationService.ResolvePrimaryConfigPath(layout);

        Text = "RM26 Decision Deployment";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(860, 620);
        ClientSize = new Size(980, 700);
        BackColor = Color.FromArgb(20, 24, 31);
        ForeColor = Color.FromArgb(238, 242, 246);
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

        Controls.Add(BuildLayout());
        LoadDeploymentFromDisk("已加载当前决策部署配置。");
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(20),
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildHeaderPanel(), 0, 0);
        root.Controls.Add(BuildToolbarPanel(), 0, 1);
        root.Controls.Add(BuildRoleGrid(), 0, 2);
        root.Controls.Add(BuildHintLabel(), 0, 3);
        root.Controls.Add(BuildStatusPanel(), 0, 4);
        return root;
    }

    private Control BuildHeaderPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 118,
            Padding = new Padding(18, 16, 18, 14),
            BackColor = Color.FromArgb(31, 38, 49),
            Margin = new Padding(0, 0, 0, 12),
        };

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White,
            Location = new Point(0, 0),
            Text = "决策部署控制台",
        });

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(190, 201, 214),
            Location = new Point(0, 38),
            Text = "独立配置英雄、工程、步兵、哨兵的部署模式，ThreeD 端会自动热加载。",
        });

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(224, 231, 239),
            Location = new Point(0, 70),
            Text = "配置文件",
        });

        _headerPathLabel.AutoEllipsis = true;
        _headerPathLabel.ForeColor = Color.FromArgb(165, 179, 193);
        _headerPathLabel.Location = new Point(0, 88);
        _headerPathLabel.Size = new Size(900, 18);
        _headerPathLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        panel.Controls.Add(_headerPathLabel);

        return panel;
    }

    private Control BuildToolbarPanel()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = BackColor,
            Margin = new Padding(0, 0, 0, 12),
        };

        panel.Controls.Add(BuildActionButton("重新读取", (_, _) => LoadDeploymentFromDisk("已从磁盘重新读取部署配置。")));
        panel.Controls.Add(BuildActionButton("默认方案", (_, _) => ApplyPreset("default", "已应用默认部署方案。")));
        panel.Controls.Add(BuildActionButton("激进方案", (_, _) => ApplyPreset("aggressive", "已应用激进部署方案。")));
        panel.Controls.Add(BuildActionButton("防守方案", (_, _) => ApplyPreset("defensive", "已应用防守部署方案。")));
        panel.Controls.Add(BuildActionButton("定位配置文件", (_, _) => OpenConfigInExplorer()));
        return panel;
    }

    private Control BuildRoleGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = BackColor,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

        for (int index = 0; index < RoleSpecs.Length; index++)
        {
            int row = index / 2;
            int column = index % 2;
            grid.Controls.Add(BuildRoleCard(RoleSpecs[index], column == 0, row == 0), column, row);
        }

        return grid;
    }

    private Control BuildRoleCard((string Key, string Label, string Description) spec, bool isLeftColumn, bool isTopRow)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 36, 46),
            Margin = new Padding(isLeftColumn ? 0 : 6, isTopRow ? 0 : 6, isLeftColumn ? 6 : 0, isTopRow ? 6 : 0),
            Padding = new Padding(18, 16, 18, 16),
        };

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White,
            Text = spec.Label,
        });

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(176, 188, 200),
            Location = new Point(0, 32),
            Text = spec.Description,
        });

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 94,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = panel.BackColor,
        };

        var modeButtons = new Dictionary<string, RadioButton>(StringComparer.OrdinalIgnoreCase);
        foreach ((string modeKey, string modeLabel) in ModeSpecs)
        {
            RadioButton button = BuildModeButton(spec.Key, modeKey, modeLabel);
            modeButtons[modeKey] = button;
            flow.Controls.Add(button);
        }

        _roleModeButtons[spec.Key] = modeButtons;
        panel.Controls.Add(flow);
        return panel;
    }

    private RadioButton BuildModeButton(string roleKey, string modeKey, string label)
    {
        var button = new RadioButton
        {
            Appearance = Appearance.Button,
            AutoSize = false,
            FlatStyle = FlatStyle.Flat,
            Width = 138,
            Height = 34,
            Margin = new Padding(0, 10, 10, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = label,
            Tag = new DecisionChoice(roleKey, modeKey),
        };

        button.FlatAppearance.BorderSize = 1;
        button.CheckedChanged += OnModeButtonCheckedChanged;
        ApplyModeButtonStyle(button);
        return button;
    }

    private Control BuildHintLabel()
    {
        return new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 8, 0, 12),
            ForeColor = Color.FromArgb(170, 183, 197),
            Text = "保存策略后，ThreeD 模拟器会在运行时自动同步，无需重新开局。若你在外部手改了 JSON，点“重新读取”即可。",
        };
    }

    private Control BuildStatusPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.FromArgb(27, 33, 43),
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0),
            AutoSize = true,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _pathLabel.AutoEllipsis = true;
        _pathLabel.Dock = DockStyle.Fill;
        _pathLabel.ForeColor = Color.FromArgb(165, 179, 193);
        _pathLabel.TextAlign = ContentAlignment.MiddleLeft;

        _statusLabel.AutoEllipsis = true;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = Color.FromArgb(218, 226, 234);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        panel.Controls.Add(_pathLabel, 0, 0);
        panel.Controls.Add(_statusLabel, 0, 1);
        return panel;
    }

    private Button BuildActionButton(string label, EventHandler onClick)
    {
        var button = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 62, 79),
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 10, 10),
            Padding = new Padding(14, 7, 14, 7),
            Text = label,
        };

        button.FlatAppearance.BorderColor = Color.FromArgb(98, 118, 139);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(63, 82, 104);
        button.Click += onClick;
        return button;
    }

    private void OnModeButtonCheckedChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is not RadioButton button || button.Tag is not DecisionChoice choice)
        {
            return;
        }

        ApplyModeButtonStyle(button);
        foreach (RadioButton sibling in _roleModeButtons[choice.Role].Values)
        {
            if (!ReferenceEquals(sibling, button))
            {
                ApplyModeButtonStyle(sibling);
            }
        }

        if (_suspendWrites || !button.Checked)
        {
            return;
        }

        SaveCurrentSelection($"已写入 {ResolveRoleLabel(choice.Role)} -> {ResolveModeLabel(choice.Mode)}。");
    }

    private void LoadDeploymentFromDisk(string statusMessage)
    {
        try
        {
            _suspendWrites = true;
            JsonObject config = _configurationService.LoadConfig(_configPath);
            DecisionDeploymentConfig deployment = DecisionDeploymentConfig.LoadFromConfig(config);

            foreach ((string roleKey, _, _) in RoleSpecs)
            {
                string mode = deployment.ResolveMode(roleKey);
                foreach ((string modeKey, _) in ModeSpecs)
                {
                    if (_roleModeButtons[roleKey].TryGetValue(modeKey, out RadioButton? button))
                    {
                        button.Checked = string.Equals(modeKey, mode, StringComparison.OrdinalIgnoreCase);
                        ApplyModeButtonStyle(button);
                    }
                }
            }

            _headerPathLabel.Text = _configPath;
            _pathLabel.Text = $"当前配置: {_configPath}";
            _statusLabel.Text = statusMessage;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取决策部署配置失败。\n\n{ex.Message}", "Decision Deployment", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "读取失败，请检查配置文件。";
        }
        finally
        {
            _suspendWrites = false;
        }
    }

    private void ApplyPreset(string preset, string successMessage)
    {
        try
        {
            DecisionDeploymentConfig deployment = DecisionDeploymentConfig.CreateDefault();
            deployment.ApplyPreset(preset);

            _suspendWrites = true;
            foreach ((string roleKey, _, _) in RoleSpecs)
            {
                string mode = deployment.ResolveMode(roleKey);
                if (_roleModeButtons[roleKey].TryGetValue(mode, out RadioButton? button))
                {
                    button.Checked = true;
                    ApplyModeButtonStyle(button);
                }
            }
        }
        finally
        {
            _suspendWrites = false;
        }

        SaveCurrentSelection(successMessage);
    }

    private void SaveCurrentSelection(string successMessage)
    {
        try
        {
            JsonObject config = _configurationService.LoadConfig(_configPath);
            DecisionDeploymentConfig deployment = DecisionDeploymentConfig.LoadFromConfig(config);
            foreach ((string roleKey, _, _) in RoleSpecs)
            {
                deployment.SetRoleMode(roleKey, ResolveSelectedMode(roleKey));
            }

            deployment.WriteToConfig(config);
            _configurationService.SaveConfig(_configPath, config);
            _statusLabel.Text = successMessage;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"写入决策部署配置失败。\n\n{ex.Message}", "Decision Deployment", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "写入失败，请稍后重试。";
        }
    }

    private string ResolveSelectedMode(string roleKey)
    {
        if (_roleModeButtons.TryGetValue(roleKey, out Dictionary<string, RadioButton>? options))
        {
            foreach ((string modeKey, RadioButton button) in options)
            {
                if (button.Checked)
                {
                    return modeKey;
                }
            }
        }

        return "aggressive";
    }

    private void OpenConfigInExplorer()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_configPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开资源管理器。\n\n{ex.Message}", "Decision Deployment", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void ApplyModeButtonStyle(RadioButton button)
    {
        bool active = button.Checked;
        button.BackColor = active ? Color.FromArgb(76, 121, 199) : Color.FromArgb(40, 48, 60);
        button.ForeColor = active ? Color.White : Color.FromArgb(224, 232, 240);
        button.FlatAppearance.BorderColor = active ? Color.FromArgb(204, 223, 245) : Color.FromArgb(92, 108, 126);
    }

    private static string ResolveRoleLabel(string roleKey)
    {
        foreach ((string key, string label, _) in RoleSpecs)
        {
            if (string.Equals(key, roleKey, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }
        }

        return roleKey;
    }

    private static string ResolveModeLabel(string modeKey)
    {
        foreach ((string key, string label) in ModeSpecs)
        {
            if (string.Equals(key, modeKey, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }
        }

        return modeKey;
    }

    private sealed record DecisionChoice(string Role, string Mode);
}
