using System.Drawing;
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
        => _openGkTextPainter.DrawText(
            graphics,
            new OpenGkUiTextLayout(
                command.Rect,
                command.Text,
                command.Color,
                command.TextStyle,
                command.TextAlign));

    private Font ResolveOpenGkTextStyleFont(OpenGkUiTextStyle style)
        => style switch
        {
            OpenGkUiTextStyle.Tiny => _tinyHudFont,
            OpenGkUiTextStyle.HudMid => _hudMidFont,
            OpenGkUiTextStyle.HudBig => _hudBigFont,
            OpenGkUiTextStyle.MenuSubtitle => _menuSubtitleFont,
            OpenGkUiTextStyle.MenuButton => _menuButtonFont,
            _ => _smallHudFont,
        };
}
