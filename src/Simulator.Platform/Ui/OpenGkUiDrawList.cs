using System.Drawing;

namespace Simulator.Platform.Ui;

public enum OpenGkUiDrawCommandKind
{
    FillRect,
    StrokeRect,
    Text,
}

public enum OpenGkUiTextAlign
{
    Left,
    Center,
    Right,
}

public enum OpenGkUiTextStyle
{
    Tiny,
    Small,
    HudMid,
    HudBig,
    MenuSubtitle,
    MenuButton,
}

public readonly record struct OpenGkUiDrawCommand(
    OpenGkUiDrawCommandKind Kind,
    Rectangle Rect,
    Color Color,
    float StrokeWidth = 1.0f,
    string Text = "",
    OpenGkUiTextStyle TextStyle = OpenGkUiTextStyle.Small,
    OpenGkUiTextAlign TextAlign = OpenGkUiTextAlign.Center);

public sealed class OpenGkUiDrawList
{
    private readonly List<OpenGkUiDrawCommand> _commands = new();

    public IReadOnlyList<OpenGkUiDrawCommand> Commands => _commands;

    public OpenGkUiButtonRegistry Buttons { get; } = new();

    public void Clear()
    {
        _commands.Clear();
        Buttons.Clear();
    }

    public void FillRect(Rectangle rect, Color color)
        => _commands.Add(new OpenGkUiDrawCommand(OpenGkUiDrawCommandKind.FillRect, rect, color));

    public void StrokeRect(Rectangle rect, Color color, float width = 1.0f)
        => _commands.Add(new OpenGkUiDrawCommand(OpenGkUiDrawCommandKind.StrokeRect, rect, color, width));

    public void Text(Rectangle rect, string text, Color color, OpenGkUiTextStyle style = OpenGkUiTextStyle.Small, OpenGkUiTextAlign align = OpenGkUiTextAlign.Center)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _commands.Add(new OpenGkUiDrawCommand(OpenGkUiDrawCommandKind.Text, rect, color, Text: text, TextStyle: style, TextAlign: align));
    }
}

public static class OpenGkUiPainter
{
    public static void AddPanel(OpenGkUiDrawList drawList, Rectangle rect, int alpha = 152)
    {
        drawList.FillRect(rect, Color.FromArgb(alpha, 18, 22, 30));
        drawList.StrokeRect(rect, Color.FromArgb(Math.Min(255, alpha + 48), 132, 146, 164));
    }

    public static void AddFlatButton(
        OpenGkUiDrawList drawList,
        Rectangle rect,
        string label,
        string action,
        bool active,
        bool enabled,
        float hoverMix,
        Color? activeColor = null,
        bool registerOnly = false)
    {
        Rectangle drawRect = hoverMix > 0.01f ? Rectangle.Inflate(rect, 1, 1) : rect;
        if (!registerOnly)
        {
            Color idleColor = Color.FromArgb(64, 76, 92);
            Color accentColor = activeColor ?? Color.FromArgb(58, 124, 214);
            Color fillColor = Blend(idleColor, accentColor, active ? 0.72f : hoverMix * 0.48f);
            fillColor = Color.FromArgb(
                Math.Clamp(fillColor.A + (int)MathF.Round(hoverMix * 10f), 0, 255),
                fillColor.R,
                fillColor.G,
                fillColor.B);
            Color borderColor = active
                ? Blend(Color.FromArgb(210, 236, 242, 248), Color.White, hoverMix * 0.35f)
                : Blend(Color.FromArgb(140, 156, 170, 188), Color.FromArgb(208, 228, 238, 248), hoverMix * 0.55f);

            drawList.FillRect(drawRect, fillColor);
            drawList.StrokeRect(drawRect, borderColor, active ? 1.5f : 1.0f + hoverMix * 0.4f);
            if (hoverMix > 0.01f || active)
            {
                int highlightAlpha = Math.Clamp((int)MathF.Round((active ? 46f : 28f) + hoverMix * 36f), 0, 255);
                drawList.FillRect(
                    new Rectangle(drawRect.X + 1, drawRect.Y + 1, Math.Max(1, drawRect.Width - 2), Math.Max(2, drawRect.Height / 4)),
                    Color.FromArgb(highlightAlpha, 255, 255, 255));
            }

            drawList.Text(
                drawRect,
                label,
                Color.WhiteSmoke,
                drawRect.Height <= 32 ? OpenGkUiTextStyle.Small : OpenGkUiTextStyle.MenuSubtitle);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            drawList.Buttons.Add(Rectangle.Inflate(rect, 6, 4), action);
        }
    }

    public static void AddPButton(OpenGkUiDrawList drawList, Rectangle rect, string label, string action, bool active, bool enabled)
    {
        drawList.FillRect(rect, active ? Color.FromArgb(80, 36, 46, 48) : Color.FromArgb(54, 34, 38, 40));
        drawList.StrokeRect(rect, enabled ? Color.FromArgb(178, 146, 184, 184) : Color.FromArgb(90, 110, 120, 122), 1.2f);
        drawList.Text(rect, label, enabled ? Color.FromArgb(232, 236, 238) : Color.FromArgb(126, 134, 138), OpenGkUiTextStyle.Small);
        if (enabled && !string.IsNullOrWhiteSpace(action))
        {
            drawList.Buttons.Add(Rectangle.Inflate(rect, 5, 4), action);
        }
    }

    public static Color Blend(Color from, Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)MathF.Round(from.A + (to.A - from.A) * t),
            (int)MathF.Round(from.R + (to.R - from.R) * t),
            (int)MathF.Round(from.G + (to.G - from.G) * t),
            (int)MathF.Round(from.B + (to.B - from.B) * t));
    }
}
