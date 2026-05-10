using System.Drawing;
using System.Windows.Forms;
using Simulator.Platform.Ui;

namespace Simulator.ThreeD;

internal sealed class WinFormsOpenGkTextPainter : IOpenGkUiTextPainter<Graphics>
{
    private readonly Func<OpenGkUiTextStyle, Font> _resolveFont;
    private readonly Font _fallbackFont;

    public WinFormsOpenGkTextPainter(Func<OpenGkUiTextStyle, Font> resolveFont, Font fallbackFont)
    {
        _resolveFont = resolveFont;
        _fallbackFont = fallbackFont;
    }

    public void DrawText(Graphics surface, OpenGkUiTextLayout text)
    {
        if (string.IsNullOrWhiteSpace(text.Text) || text.Rect.Width <= 0 || text.Rect.Height <= 0)
        {
            return;
        }

        Font preferredFont = _resolveFont(text.Style);
        Font font = ResolveFittingFont(surface, text.Text, text.Rect, preferredFont, _fallbackFont);
        TextRenderer.DrawText(
            surface,
            text.Text,
            font,
            text.Rect,
            text.Color,
            ResolveFlags(text.Align));
    }

    private static TextFormatFlags ResolveFlags(OpenGkUiTextAlign align)
    {
        TextFormatFlags horizontal = align switch
        {
            OpenGkUiTextAlign.Left => TextFormatFlags.Left,
            OpenGkUiTextAlign.Right => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter,
        };

        return horizontal
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis
            | TextFormatFlags.SingleLine
            | TextFormatFlags.PreserveGraphicsClipping
            | TextFormatFlags.NoPrefix;
    }

    private static Font ResolveFittingFont(Graphics graphics, string text, Rectangle rect, Font preferredFont, Font fallbackFont)
    {
        if (string.IsNullOrWhiteSpace(text) || rect.Width <= 8 || rect.Height <= 8)
        {
            return fallbackFont;
        }

        Size measured = TextRenderer.MeasureText(
            graphics,
            text,
            preferredFont,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (measured.Width <= rect.Width - 8 && measured.Height <= rect.Height + 4)
        {
            return preferredFont;
        }

        return fallbackFont;
    }
}
