using System.Drawing;

namespace Simulator.Platform.Ui;

public readonly record struct OpenGkRuntimeHudState(
    string Status,
    string Phase,
    string Detail,
    double PhaseProgress,
    bool MovementLocked,
    bool PanelOpen,
    bool DeathOverlayVisible,
    double DeathProgress);

public static class OpenGkRuntimeHudPainter
{
    public static void AddRuntimeStatusOverlay(OpenGkUiDrawList drawList, Size clientSize, OpenGkRuntimeHudState state)
    {
        int width = Math.Max(1, clientSize.Width);
        int panelWidth = Math.Min(720, Math.Max(360, width - 48));
        Rectangle panel = new(24, 18, panelWidth, 74);
        drawList.FillRect(panel, Color.FromArgb(142, 7, 11, 16));
        drawList.StrokeRect(panel, Color.FromArgb(122, 116, 184, 204), 1.2f);

        string status = string.IsNullOrWhiteSpace(state.Status) ? "runtime" : state.Status;
        string phase = string.IsNullOrWhiteSpace(state.Phase) ? "phase=unknown" : state.Phase;
        drawList.Text(
            new Rectangle(panel.X + 14, panel.Y + 8, panel.Width - 28, 24),
            $"{status} | {phase}",
            Color.FromArgb(238, 244, 248),
            OpenGkUiTextStyle.HudMid,
            OpenGkUiTextAlign.Left);

        string input = state.MovementLocked ? "movement=locked" : "movement=enabled";
        string panelState = state.PanelOpen ? "panel=open" : "panel=closed";
        string detail = string.IsNullOrWhiteSpace(state.Detail)
            ? $"{input} / {panelState}"
            : $"{state.Detail} / {input} / {panelState}";
        drawList.Text(
            new Rectangle(panel.X + 14, panel.Y + 35, panel.Width - 28, 18),
            detail,
            Color.FromArgb(198, 214, 224),
            OpenGkUiTextStyle.Small,
            OpenGkUiTextAlign.Left);

        Rectangle track = new(panel.X + 14, panel.Bottom - 13, panel.Width - 28, 5);
        drawList.FillRect(track, Color.FromArgb(118, 48, 58, 66));
        int fillWidth = Math.Clamp((int)Math.Round(track.Width * Math.Clamp(state.PhaseProgress, 0.0, 1.0)), 0, track.Width);
        if (fillWidth > 0)
        {
            drawList.FillRect(new Rectangle(track.X, track.Y, fillWidth, track.Height), Color.FromArgb(222, 92, 218, 255));
        }
    }

    public static void AddDeathOverlay(OpenGkUiDrawList drawList, Size clientSize, OpenGkRuntimeHudState state)
    {
        if (!state.DeathOverlayVisible)
        {
            return;
        }

        int width = Math.Max(1, clientSize.Width);
        int height = Math.Max(1, clientSize.Height);
        drawList.FillRect(new Rectangle(0, 0, width, height), Color.FromArgb(132, 0, 0, 0));
        Rectangle panel = new((width - 420) / 2, Math.Max(34, height / 2 - 74), 420, 110);
        drawList.FillRect(panel, Color.FromArgb(206, 8, 10, 12));
        drawList.StrokeRect(panel, Color.FromArgb(190, 220, 226, 232), 1.4f);
        drawList.Text(
            new Rectangle(panel.X + 18, panel.Y + 18, panel.Width - 36, 28),
            "RESPAWNING",
            Color.FromArgb(240, 244, 246),
            OpenGkUiTextStyle.HudBig);
        Rectangle track = new(panel.X + 34, panel.Bottom - 34, panel.Width - 68, 8);
        drawList.FillRect(track, Color.FromArgb(92, 74, 78, 84));
        int fillWidth = Math.Clamp((int)Math.Round(track.Width * Math.Clamp(state.DeathProgress, 0.0, 1.0)), 0, track.Width);
        if (fillWidth > 0)
        {
            drawList.FillRect(new Rectangle(track.X, track.Y, fillWidth, track.Height), Color.FromArgb(224, 190, 226, 236));
        }
    }
}
