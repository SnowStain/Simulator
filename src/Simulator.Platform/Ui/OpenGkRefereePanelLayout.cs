using System.Drawing;

namespace Simulator.Platform.Ui;

public enum OpenGkRefereePanelPage
{
    Main,
    Energy,
}

public readonly record struct OpenGkRefereePanelLayout(
    Rectangle Panel,
    Rectangle Title,
    Rectangle Help,
    Rectangle LogoutButton,
    Rectangle CloseButton,
    Rectangle MainTab,
    Rectangle EnergyTab,
    Rectangle Content);

public static class OpenGkRefereePanelLayoutResolver
{
    public static OpenGkRefereePanelLayout Resolve(Size clientSize, bool showLogout)
    {
        int width = Math.Max(1, clientSize.Width);
        int height = Math.Max(1, clientSize.Height);
        int panelWidth = Math.Min(Math.Max(920, (int)(width * 0.82)), width - 24);
        int panelHeight = Math.Min(Math.Max(610, (int)(height * 0.76)), height - 24);
        panelWidth = Math.Max(320, panelWidth);
        panelHeight = Math.Max(360, panelHeight);

        Rectangle panel = new(
            (width - panelWidth) / 2,
            (height - panelHeight) / 2,
            panelWidth,
            panelHeight);

        int closeX = panel.Right - 116;
        int helpRight = closeX - 14;
        int logoutWidth = showLogout ? 98 : 0;
        Rectangle logout = showLogout
            ? new Rectangle(closeX - logoutWidth - 8, panel.Y + 20, logoutWidth, 30)
            : Rectangle.Empty;
        if (showLogout)
        {
            helpRight -= logoutWidth + 8;
        }

        Rectangle title = new(panel.X + 24, panel.Y + 16, 260, 38);
        Rectangle help = new(panel.X + 290, panel.Y + 24, Math.Max(80, helpRight - (panel.X + 290)), 24);
        Rectangle close = new(closeX, panel.Y + 20, 84, 30);
        Rectangle mainTab = new(panel.X + 24, panel.Y + 54, 88, 28);
        Rectangle energyTab = new(mainTab.Right + 10, mainTab.Y, 120, 28);
        Rectangle content = new(panel.X + 24, panel.Y + 94, panel.Width - 48, panel.Height - 118);
        return new OpenGkRefereePanelLayout(panel, title, help, logout, close, mainTab, energyTab, content);
    }

    public static void AddChrome(
        OpenGkUiDrawList drawList,
        OpenGkRefereePanelLayout layout,
        string title,
        string help,
        bool showLogout,
        string logoutLabel,
        OpenGkRefereePanelPage page,
        string closeAction = "p_close",
        string logoutAction = "p_logout",
        string mainTabAction = "ref_page:main",
        string energyTabAction = "ref_page:energy")
    {
        drawList.FillRect(layout.Panel, Color.FromArgb(232, 10, 14, 18));
        drawList.StrokeRect(layout.Panel, Color.FromArgb(172, 126, 174, 190), 1.4f);
        drawList.Text(layout.Title, title, Color.WhiteSmoke, OpenGkUiTextStyle.HudBig, OpenGkUiTextAlign.Left);
        drawList.Text(layout.Help, help, Color.FromArgb(190, 206, 218, 226), OpenGkUiTextStyle.Tiny, OpenGkUiTextAlign.Left);
        if (showLogout && layout.LogoutButton != Rectangle.Empty)
        {
            OpenGkUiPainter.AddPButton(drawList, layout.LogoutButton, logoutLabel, logoutAction, active: true, enabled: true);
        }

        OpenGkUiPainter.AddPButton(drawList, layout.CloseButton, "Close", closeAction, active: false, enabled: true);
        OpenGkUiPainter.AddPButton(drawList, layout.MainTab, "Overview", mainTabAction, active: page == OpenGkRefereePanelPage.Main, enabled: true);
        OpenGkUiPainter.AddPButton(drawList, layout.EnergyTab, "Energy", energyTabAction, active: page == OpenGkRefereePanelPage.Energy, enabled: true);
    }

    public static void AddPanelFrame(OpenGkUiDrawList drawList, Rectangle rect, string title)
    {
        drawList.FillRect(rect, Color.FromArgb(116, 23, 25, 27));
        drawList.StrokeRect(rect, Color.FromArgb(96, 98, 104, 108), 2f);
        drawList.Text(new Rectangle(rect.X + 22, rect.Y + 24, rect.Width - 44, 32), title, Color.FromArgb(236, 238, 240), OpenGkUiTextStyle.HudMid, OpenGkUiTextAlign.Left);
    }
}
