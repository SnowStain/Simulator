using System.Drawing;

namespace Simulator.Platform.Ui;

public readonly record struct OpenGkRoomScreenLayout(
    Rectangle Root,
    Rectangle TopBar,
    Rectangle RedTeam,
    Rectangle BlueTeam,
    Rectangle RefereeAndSettings,
    Rectangle LeftAction,
    Rectangle RightAction);

public static class OpenGkRoomLayout
{
    public static OpenGkRoomScreenLayout Resolve(Rectangle root)
    {
        Rectangle topBar = new(root.X, root.Y, root.Width, 72);
        int gap = Math.Clamp(root.Width / 90, 10, 16);
        int minTeamWidth = Math.Clamp(root.Width / 4, 232, 280);
        int minSideWidth = Math.Clamp(root.Width / 5, 230, 320);
        int sideWidth = Math.Clamp(root.Width / 4, minSideWidth, 410);
        int columnWidth = Math.Max(minTeamWidth, (root.Width - sideWidth - gap * 2) / 2);
        if (columnWidth * 2 + sideWidth + gap * 2 > root.Width)
        {
            columnWidth = Math.Max(180, (root.Width - minSideWidth - gap * 2) / 2);
            sideWidth = Math.Max(180, root.Width - columnWidth * 2 - gap * 2);
        }

        int top = root.Y + 92;
        int bottomButtons = 58;
        int contentHeight = root.Bottom - top - bottomButtons;
        Rectangle red = new(root.X, top, columnWidth, contentHeight);
        Rectangle blue = new(red.Right + gap, top, columnWidth, contentHeight);
        Rectangle side = new(blue.Right + gap, top, root.Right - blue.Right - gap, contentHeight);
        int buttonY = root.Bottom - 42;
        return new OpenGkRoomScreenLayout(
            root,
            topBar,
            red,
            blue,
            side,
            new Rectangle(root.X, buttonY, 132, 36),
            new Rectangle(root.Right - 178, buttonY, 178, 36));
    }
}
