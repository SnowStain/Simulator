using System.Drawing;

namespace Simulator.Platform.Ui;

public readonly record struct OpenGkUiTextLayout(
    Rectangle Rect,
    string Text,
    Color Color,
    OpenGkUiTextStyle Style,
    OpenGkUiTextAlign Align);

public interface IOpenGkUiTextPainter<in TSurface>
{
    void DrawText(TSurface surface, OpenGkUiTextLayout text);
}
