using System.Drawing;
using System.Windows.Forms;

namespace Simulator.ThreeD;

internal sealed class RefereeControlWindow : Form
{
    private readonly Simulator3dForm _owner;
    private readonly Label _statusLabel = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    public RefereeControlWindow(Simulator3dForm owner, bool localMode)
    {
        _owner = owner;
        Text = localMode ? "本地裁判控制" : "局域网裁判控制";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(1040, 720);
        MinimumSize = new Size(860, 560);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = true;
        KeyPreview = true;
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(246, 248, 251);
        ForeColor = Color.FromArgb(24, 32, 42);

        Controls.Add(BuildRoot(localMode));

        _refreshTimer.Interval = 400;
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
        RefreshStatus();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Escape or Keys.O or Keys.P)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private Control BuildRoot(bool localMode)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Padding = new Padding(14, 0, 14, 0);
        _statusLabel.Font = new Font(Font, FontStyle.Bold);
        _statusLabel.BackColor = Color.White;
        _statusLabel.BorderStyle = BorderStyle.FixedSingle;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreatePage("总览", BuildOverviewPage()));
        tabs.TabPages.Add(CreatePage("设施血量", BuildFacilityPage()));
        tabs.TabPages.Add(CreatePage("机器人执法", BuildRobotPage()));
        tabs.TabPages.Add(CreatePage("经济视角", BuildEconomyViewPage()));
        tabs.TabPages.Add(CreatePage("能量机关", BuildEnergyPage()));

        root.Controls.Add(_statusLabel, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(BuildSystemBar(localMode), 0, 2);
        return root;
    }

    private static TabPage CreatePage(string title, Control content)
        => new(title) { Controls = { content } };

    private Control BuildOverviewPage()
    {
        var panel = CreateWrapPanel();
        AddButton(panel, "显示总览", "ref_page:main", 130);
        AddButton(panel, "显示能量机关页", "ref_page:energy", 160);
        AddButton(panel, "自由视角", "ref_view:free", 130);
        AddButton(panel, "第一人称", "ref_view:first", 130);
        AddButton(panel, "俯视视角", "ref_view:top", 130);
        AddButton(panel, "高亮机器人", "ref_highlight", 140);
        return WrapPage(panel);
    }

    private Control BuildFacilityPage()
    {
        var panel = CreateWrapPanel();
        AddFacilityButtons(panel, "red_outpost");
        AddFacilityButtons(panel, "red_base");
        AddFacilityButtons(panel, "blue_outpost");
        AddFacilityButtons(panel, "blue_base");
        AddSeparator(panel);
        AddButton(panel, "红基地上装甲板开启", "ref_base_armor_open:red", 190);
        AddButton(panel, "蓝基地上装甲板开启", "ref_base_armor_open:blue", 190);
        AddButton(panel, "红前哨站停止旋转", "ref_outpost_stop:red", 190);
        AddButton(panel, "蓝前哨站停止旋转", "ref_outpost_stop:blue", 190);
        return WrapPage(panel);
    }

    private Control BuildRobotPage()
    {
        var panel = CreateWrapPanel();
        AddButton(panel, "弹药 -50", "ref_ammo:-50");
        AddButton(panel, "弹药 +50", "ref_ammo:50");
        AddButton(panel, "弹药 +200", "ref_ammo:200");
        AddButton(panel, "黄牌警告", "ref_yellow");
        AddButton(panel, "复活选中", "ref_revive");
        AddButton(panel, "罚下选中", "ref_eject");
        return WrapPage(panel);
    }

    private Control BuildEconomyViewPage()
    {
        var panel = CreateWrapPanel();
        foreach (string team in new[] { "red", "blue" })
        {
            string label = team == "red" ? "红方" : "蓝方";
            AddButton(panel, $"{label}金币 -100", $"ref_gold:{team}:-100");
            AddButton(panel, $"{label}金币 +100", $"ref_gold:{team}:100");
            AddButton(panel, $"{label}金币 +500", $"ref_gold:{team}:500");
        }

        AddSeparator(panel);
        AddButton(panel, "自由视角", "ref_view:free");
        AddButton(panel, "第一人称", "ref_view:first");
        AddButton(panel, "俯视视角", "ref_view:top");
        AddButton(panel, "高亮机器人", "ref_highlight");
        return WrapPage(panel);
    }

    private Control BuildEnergyPage()
    {
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(246, 248, 251),
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(8),
        };
        root.Controls.Add(BuildEnergyTeamSection("红方", "red"));
        root.Controls.Add(BuildEnergyTeamSection("蓝方", "blue"));
        scroll.Controls.Add(root);
        return scroll;
    }

    private Control BuildEnergyTeamSection(string label, string team)
    {
        var box = new GroupBox
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = label,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 12),
        };

        var panel = CreateWrapPanel();
        AddEnergyModeButtons(panel, team, "small", "小能量");
        AddSeparator(panel);
        AddEnergyModeButtons(panel, team, "large", "大能量");
        box.Controls.Add(panel);
        return box;
    }

    private void AddEnergyModeButtons(FlowLayoutPanel panel, string team, string mode, string label)
    {
        AddButton(panel, $"{label} 开启", $"ref_energy_activate:{team}:{mode}", 120);
        AddButton(panel, $"{label} 清空", $"ref_energy_clear:{team}:{mode}", 120);
        AddButton(panel, $"{label} 全激活", $"ref_energy_complete:{team}:{mode}", 130);
        for (int arm = 0; arm < 5; arm++)
        {
            AddButton(panel, $"{label} 灯臂{arm}", $"ref_energy_lit:{team}:{mode}:{arm}", 118);
            AddButton(panel, $"{label} 命中{arm}", $"ref_energy_hit:{team}:{mode}:{arm}", 118);
        }

        for (int count = 0; count <= 5; count++)
        {
            AddButton(panel, $"{label} 已激活 {count}", $"ref_energy_count:{team}:{mode}:{count}", 132);
        }
    }

    private Control BuildSystemBar(bool localMode)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
        };
        AddButton(panel, "关闭窗口", "p_close", 130);
        AddButton(panel, localMode ? "返回房间/大厅" : "登出房间", "p_logout", 156);
        return panel;
    }

    private static Control WrapPage(Control content)
        => new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            BackColor = Color.FromArgb(246, 248, 251),
            Controls = { content },
        };

    private static FlowLayoutPanel CreateWrapPanel()
        => new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(4),
        };

    private static void AddSeparator(FlowLayoutPanel panel)
    {
        panel.SetFlowBreak(panel.Controls[^1], true);
    }

    private void AddFacilityButtons(FlowLayoutPanel panel, string entityId)
    {
        string label = ResolveFacilityLabel(entityId);
        AddButton(panel, $"{label} -500", $"ref_hp_delta:{entityId}:-500");
        AddButton(panel, $"{label} 满血", $"ref_hp_full:{entityId}");
        AddButton(panel, $"{label} 摧毁", $"ref_hp_zero:{entityId}");
        panel.SetFlowBreak(panel.Controls[^1], true);
    }

    private void AddButton(Control parent, string text, string action, int width = 150)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 36,
            Margin = new Padding(6),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(24, 35, 48),
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(196, 207, 218);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(229, 240, 252);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(207, 226, 246);
        button.Click += (_, _) =>
        {
            _owner.InvokeRefereeControlWindowAction(action);
            RefreshStatus();
        };
        parent.Controls.Add(button);
    }

    private void RefreshStatus()
    {
        if (_owner.IsDisposed)
        {
            Close();
            return;
        }

        _statusLabel.Text = _owner.BuildRefereeControlWindowStatusText();
    }

    private static string ResolveFacilityLabel(string entityId)
        => entityId switch
        {
            "red_outpost" => "红前哨",
            "red_base" => "红基地",
            "blue_outpost" => "蓝前哨",
            "blue_base" => "蓝基地",
            _ => entityId,
        };
}
