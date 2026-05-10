using System.Drawing;
using System.Windows.Forms;
using Simulator.Platform.Ui;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private void RenderOpenGkDrawList(Graphics graphics, OpenGkUiDrawList drawList)
    {
        foreach (OpenGkUiDrawCommand command in drawList.Commands)
        {
            switch (command.Kind)
            {
                case OpenGkUiDrawCommandKind.FillRect:
                    using (var brush = new SolidBrush(command.Color))
                    {
                        graphics.FillRectangle(brush, command.Rect);
                    }
                    break;

                case OpenGkUiDrawCommandKind.StrokeRect:
                    using (var pen = new Pen(command.Color, command.StrokeWidth))
                    {
                        graphics.DrawRectangle(pen, command.Rect);
                    }
                    break;

                case OpenGkUiDrawCommandKind.Text:
                    RenderOpenGkTextCommand(graphics, command);
                    break;
            }
        }

        _uiButtons.AddRange(drawList.Buttons);
    }

    private void RenderOpenGkTextCommand(Graphics graphics, OpenGkUiDrawCommand command)
    {
        Font preferred = command.TextStyle switch
        {
            OpenGkUiTextStyle.Tiny => _tinyHudFont,
            OpenGkUiTextStyle.MenuSubtitle => _menuSubtitleFont,
            OpenGkUiTextStyle.MenuButton => _menuButtonFont,
            _ => _smallHudFont,
        };
        Font fallback = command.TextStyle == OpenGkUiTextStyle.Tiny ? _tinyHudFont : _tinyHudFont;
        Font font = ResolveUiButtonFont(graphics, command.Text, command.Rect, preferred, fallback);
        TextFormatFlags align = command.TextAlign switch
        {
            OpenGkUiTextAlign.Left => TextFormatFlags.Left,
            OpenGkUiTextAlign.Right => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter,
        };

        TextRenderer.DrawText(
            graphics,
            command.Text,
            font,
            command.Rect,
            command.Color,
            align
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis
            | TextFormatFlags.SingleLine
            | TextFormatFlags.PreserveGraphicsClipping
            | TextFormatFlags.NoPrefix);
    }
}
