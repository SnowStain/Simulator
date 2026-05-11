using System.Drawing;
using Simulator.Platform.Runtime;

namespace Simulator.Platform.Ui;

public readonly record struct OpenGkMainMenuUiState(
    string Title,
    string Subtitle,
    string MapName,
    string Status);

public readonly record struct OpenGkRoomSeatUiState(
    string Label,
    string Team,
    string Occupant,
    string Role,
    bool Occupied,
    bool Local);

public readonly record struct OpenGkRoomUiState(
    string Title,
    string RoomAddress,
    string MapName,
    string Status,
    IReadOnlyList<OpenGkRoomSeatUiState> RedSeats,
    IReadOnlyList<OpenGkRoomSeatUiState> BlueSeats,
    IReadOnlyList<OpenGkRoomSeatUiState> RefereeSeats,
    bool CanStart);

public readonly record struct OpenGkPreparationUiState(
    SimulatorRuntimePhase Phase,
    double RemainingSec,
    bool CanSkip,
    string SelectedRobot,
    string SelectedConfiguration);

public static class OpenGkGameScreenPainter
{
    public static void AddMainMenu(OpenGkUiDrawList drawList, Rectangle root, OpenGkMainMenuUiState state)
    {
        drawList.FillRect(root, Color.FromArgb(150, 4, 7, 11));
        int panelWidth = Math.Min(520, Math.Max(340, root.Width - 48));
        Rectangle panel = new(root.X + 32, root.Y + Math.Max(34, root.Height / 2 - 178), panelWidth, 322);
        OpenGkUiPainter.AddPanel(drawList, panel, 214);
        drawList.Text(
            new Rectangle(panel.X + 26, panel.Y + 24, panel.Width - 52, 48),
            string.IsNullOrWhiteSpace(state.Title) ? "ARTINX A-Soul" : state.Title,
            Color.WhiteSmoke,
            OpenGkUiTextStyle.HudBig,
            OpenGkUiTextAlign.Left);
        drawList.Text(
            new Rectangle(panel.X + 28, panel.Y + 78, panel.Width - 56, 24),
            string.IsNullOrWhiteSpace(state.Subtitle) ? "RoboMaster 2026 UC Simulator" : state.Subtitle,
            Color.FromArgb(212, 210, 226, 238),
            OpenGkUiTextStyle.Small,
            OpenGkUiTextAlign.Left);
        drawList.Text(
            new Rectangle(panel.X + 28, panel.Y + 112, panel.Width - 56, 24),
            $"Map: {state.MapName}",
            Color.FromArgb(226, 255, 224, 116),
            OpenGkUiTextStyle.Small,
            OpenGkUiTextAlign.Left);

        Rectangle local = new(panel.X + 28, panel.Y + 160, panel.Width - 56, 42);
        Rectangle training = new(panel.X + 28, local.Bottom + 12, panel.Width - 56, 42);
        Rectangle quit = new(panel.X + 28, training.Bottom + 12, panel.Width - 56, 42);
        OpenGkUiPainter.AddFlatButton(drawList, local, "Local Room", "menu_local", active: true, enabled: true, hoverMix: 0.0f, activeColor: Color.FromArgb(58, 124, 214));
        OpenGkUiPainter.AddFlatButton(drawList, training, "Training Sandbox", "menu_local", active: false, enabled: true, hoverMix: 0.0f, activeColor: Color.FromArgb(70, 138, 154));
        OpenGkUiPainter.AddFlatButton(drawList, quit, "Release Mouse", "linux:release_mouse", active: false, enabled: true, hoverMix: 0.0f, activeColor: Color.FromArgb(84, 96, 112));
        drawList.Text(
            new Rectangle(panel.X + 28, panel.Bottom - 34, panel.Width - 56, 18),
            state.Status,
            Color.FromArgb(170, 204, 214, 224),
            OpenGkUiTextStyle.Tiny,
            OpenGkUiTextAlign.Left);
    }

    public static void AddRoom(OpenGkUiDrawList drawList, Rectangle root, OpenGkRoomUiState state)
    {
        drawList.FillRect(root, Color.FromArgb(124, 4, 7, 11));
        OpenGkRoomScreenLayout layout = OpenGkRoomLayout.Resolve(Rectangle.Inflate(root, -32, -28));
        drawList.FillRect(layout.TopBar, Color.FromArgb(216, 7, 11, 17));
        drawList.StrokeRect(layout.TopBar, Color.FromArgb(128, 116, 184, 204), 1.2f);
        drawList.Text(new Rectangle(layout.TopBar.X + 20, layout.TopBar.Y + 12, layout.TopBar.Width / 2, 26), state.Title, Color.WhiteSmoke, OpenGkUiTextStyle.HudMid, OpenGkUiTextAlign.Left);
        drawList.Text(new Rectangle(layout.TopBar.X + 20, layout.TopBar.Y + 40, layout.TopBar.Width / 2, 18), state.RoomAddress, Color.FromArgb(190, 206, 218, 226), OpenGkUiTextStyle.Tiny, OpenGkUiTextAlign.Left);
        drawList.Text(new Rectangle(layout.TopBar.Right - 360, layout.TopBar.Y + 22, 340, 22), $"Map {state.MapName}", Color.FromArgb(228, 255, 224, 116), OpenGkUiTextStyle.Small, OpenGkUiTextAlign.Right);

        AddSeatColumn(drawList, layout.RedTeam, "Red Team", Color.FromArgb(222, 218, 38, 52), state.RedSeats);
        AddSeatColumn(drawList, layout.BlueTeam, "Blue Team", Color.FromArgb(222, 40, 94, 218), state.BlueSeats);
        AddSideColumn(drawList, layout.RefereeAndSettings, state);

        OpenGkUiPainter.AddFlatButton(drawList, layout.LeftAction, "Back", "room_back", active: false, enabled: true, hoverMix: 0.0f, activeColor: Color.FromArgb(84, 96, 112));
        OpenGkUiPainter.AddFlatButton(drawList, layout.RightAction, "Start Prep", "room_start", active: state.CanStart, enabled: state.CanStart, hoverMix: 0.0f, activeColor: Color.FromArgb(58, 124, 214));
    }

    public static void AddPreparationOverlay(OpenGkUiDrawList drawList, Size clientSize, OpenGkPreparationUiState state)
    {
        if (state.Phase is SimulatorRuntimePhase.Live or SimulatorRuntimePhase.Room)
        {
            return;
        }

        int width = Math.Max(1, clientSize.Width);
        Rectangle panel = new((width - 460) / 2, 22, 460, 82);
        drawList.FillRect(panel, Color.FromArgb(164, 6, 9, 13));
        drawList.StrokeRect(panel, Color.FromArgb(150, 146, 184, 204), 1.2f);
        string title = state.Phase switch
        {
            SimulatorRuntimePhase.Preparation => "Preparation",
            SimulatorRuntimePhase.RefereeSelfCheck => "Referee Self Check",
            SimulatorRuntimePhase.Countdown => "Countdown",
            _ => state.Phase.ToString(),
        };
        drawList.Text(new Rectangle(panel.X + 18, panel.Y + 10, panel.Width - 36, 28), $"{title}  {Math.Ceiling(Math.Max(0.0, state.RemainingSec)):0}s", Color.WhiteSmoke, OpenGkUiTextStyle.HudMid);
        string skip = state.CanSkip ? "Enter skips local timer" : "Waiting for match flow";
        drawList.Text(new Rectangle(panel.X + 18, panel.Y + 42, panel.Width - 36, 20), $"{state.SelectedRobot} / {state.SelectedConfiguration} / {skip}", Color.FromArgb(210, 216, 226, 234), OpenGkUiTextStyle.Tiny);
    }

    private static void AddSeatColumn(OpenGkUiDrawList drawList, Rectangle rect, string title, Color accent, IReadOnlyList<OpenGkRoomSeatUiState> seats)
    {
        drawList.FillRect(rect, Color.FromArgb(196, 9, 13, 20));
        drawList.StrokeRect(rect, Color.FromArgb(150, accent.R, accent.G, accent.B), 1.5f);
        drawList.FillRect(new Rectangle(rect.X, rect.Y, rect.Width, 34), accent);
        drawList.Text(new Rectangle(rect.X + 14, rect.Y + 6, rect.Width - 28, 22), $"{title} ({seats.Count(seat => seat.Occupied)}/{Math.Max(1, seats.Count)})", Color.WhiteSmoke, OpenGkUiTextStyle.Small, OpenGkUiTextAlign.Left);

        int y = rect.Y + 48;
        int rowHeight = Math.Clamp((rect.Height - 60) / Math.Max(1, seats.Count), 44, 58);
        foreach (OpenGkRoomSeatUiState seat in seats)
        {
            Rectangle row = new(rect.X + 12, y, rect.Width - 24, rowHeight - 8);
            Color fill = seat.Local
                ? Color.FromArgb(82, 54, 86, 116)
                : seat.Occupied
                    ? Color.FromArgb(62, 28, 36, 46)
                    : Color.FromArgb(38, 22, 26, 34);
            drawList.FillRect(row, fill);
            drawList.StrokeRect(row, seat.Local ? Color.FromArgb(206, 255, 224, 116) : Color.FromArgb(86, 104, 118, 132), 1.0f);
            drawList.Text(new Rectangle(row.X + 12, row.Y + 6, row.Width - 24, 18), seat.Label, Color.FromArgb(226, 232, 238), OpenGkUiTextStyle.Small, OpenGkUiTextAlign.Left);
            drawList.Text(new Rectangle(row.X + 12, row.Y + 24, row.Width - 24, 16), seat.Occupied ? $"{seat.Occupant} / {seat.Role}" : "waiting", Color.FromArgb(178, 196, 206, 216), OpenGkUiTextStyle.Tiny, OpenGkUiTextAlign.Left);
            y += rowHeight;
        }
    }

    private static void AddSideColumn(OpenGkUiDrawList drawList, Rectangle rect, OpenGkRoomUiState state)
    {
        drawList.FillRect(rect, Color.FromArgb(196, 9, 13, 20));
        drawList.StrokeRect(rect, Color.FromArgb(132, 126, 174, 190), 1.2f);
        drawList.Text(new Rectangle(rect.X + 16, rect.Y + 18, rect.Width - 32, 28), "Referee / Settings", Color.WhiteSmoke, OpenGkUiTextStyle.HudMid, OpenGkUiTextAlign.Left);
        int y = rect.Y + 66;
        foreach (OpenGkRoomSeatUiState seat in state.RefereeSeats)
        {
            Rectangle row = new(rect.X + 14, y, rect.Width - 28, 34);
            drawList.FillRect(row, seat.Occupied ? Color.FromArgb(58, 34, 42, 50) : Color.FromArgb(38, 22, 26, 34));
            drawList.StrokeRect(row, Color.FromArgb(96, 126, 174, 190), 1.0f);
            drawList.Text(row, seat.Occupied ? $"{seat.Label}  {seat.Occupant}" : $"{seat.Label}  waiting", Color.FromArgb(222, 232, 238), OpenGkUiTextStyle.Small, OpenGkUiTextAlign.Left);
            y += 42;
        }

        Rectangle info = new(rect.X + 14, y + 12, rect.Width - 28, Math.Max(118, rect.Bottom - y - 26));
        OpenGkRefereePanelLayoutResolver.AddPanelFrame(drawList, info, "Game Settings");
        drawList.Text(new Rectangle(info.X + 18, info.Y + 62, info.Width - 36, 20), $"Map: {state.MapName}", Color.FromArgb(214, 226, 232), OpenGkUiTextStyle.Small, OpenGkUiTextAlign.Left);
        drawList.Text(new Rectangle(info.X + 18, info.Y + 88, info.Width - 36, 20), state.Status, Color.FromArgb(184, 198, 210), OpenGkUiTextStyle.Tiny, OpenGkUiTextAlign.Left);
    }
}
