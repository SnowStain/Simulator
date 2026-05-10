using System.Drawing;

namespace Simulator.Platform.Ui;

public readonly record struct OpenGkRefereeEnergyCard(
    string Title,
    string ActionPrefix,
    int ActiveCount = 0);

public static class OpenGkRefereePanelContent
{
    public static void AddLocalOverview(OpenGkUiDrawList drawList, Rectangle content, string status)
    {
        int gap = 14;
        int colWidth = (content.Width - gap * 2) / 3;
        Rectangle left = new(content.X, content.Y, colWidth, Math.Max(180, content.Height - 178));
        Rectangle mid = new(left.Right + gap, content.Y, colWidth, left.Height);
        Rectangle right = new(mid.Right + gap, content.Y, colWidth, left.Height);
        Rectangle logs = new(content.X, left.Bottom + gap, content.Width, Math.Max(120, content.Bottom - left.Bottom - gap));

        OpenGkRefereePanelLayoutResolver.AddPanelFrame(drawList, left, "Facilities");
        OpenGkRefereePanelLayoutResolver.AddPanelFrame(drawList, mid, "Robots");
        OpenGkRefereePanelLayoutResolver.AddPanelFrame(drawList, right, "Economy / View");
        OpenGkRefereePanelLayoutResolver.AddPanelFrame(drawList, logs, "Local Event Log");
        drawList.Text(
            new Rectangle(left.X + 22, left.Y + 72, left.Width - 44, 24),
            status,
            Color.FromArgb(224, 232, 238),
            OpenGkUiTextStyle.Small,
            OpenGkUiTextAlign.Left);
    }

    public static void AddEnergyGrid(OpenGkUiDrawList drawList, Rectangle content, IReadOnlyList<OpenGkRefereeEnergyCard> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        int gap = 14;
        int columns = cards.Count <= 1 ? 1 : 2;
        int rows = Math.Max(1, (cards.Count + columns - 1) / columns);
        int columnWidth = (content.Width - gap * (columns - 1)) / columns;
        int rowHeight = (content.Height - gap * (rows - 1)) / rows;
        for (int i = 0; i < cards.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;
            Rectangle cardRect = new(
                content.X + column * (columnWidth + gap),
                content.Y + row * (rowHeight + gap),
                columnWidth,
                rowHeight);
            AddEnergyCard(drawList, cardRect, cards[i]);
        }
    }

    private static void AddEnergyCard(OpenGkUiDrawList drawList, Rectangle rect, OpenGkRefereeEnergyCard card)
    {
        OpenGkRefereePanelLayoutResolver.AddPanelFrame(drawList, rect, card.Title);
        int x = rect.X + 18;
        int y = rect.Y + 68;
        int buttonWidth = Math.Max(52, (rect.Width - 36 - 5 * 6) / 6);
        int activeCount = Math.Clamp(card.ActiveCount, 0, 5);
        for (int i = 0; i <= 5; i++)
        {
            OpenGkUiPainter.AddPButton(
                drawList,
                new Rectangle(x + i * (buttonWidth + 6), y, buttonWidth, 28),
                i.ToString(),
                $"{card.ActionPrefix}:{i}",
                active: i == activeCount,
                enabled: true);
        }
    }
}
