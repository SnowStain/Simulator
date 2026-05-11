using Simulator.Platform.Ui;
using Simulator.Runtime.Input;

namespace Simulator.Platform.Runtime;

public enum SimulatorRuntimeMode
{
    Local,
    LanHost,
    LanClient,
    Referee,
    Spectator,
}

public enum SimulatorRuntimePhase
{
    Room,
    Preparation,
    RefereeSelfCheck,
    Countdown,
    Live,
    Respawning,
    MatchEnded,
}

public enum SimulatorRuntimePanelKind
{
    None,
    LocalOperator,
    LanReferee,
}

public enum SimulatorRuntimeRoomScreen
{
    MainMenu,
    RoomLobby,
}

public sealed record SimulatorRuntimeStateMachineOptions(
    double LocalPreparationSec,
    double LanPreparationSec,
    double RefereeSelfCheckSec,
    double CountdownSec,
    double RespawnSec,
    bool AllowLocalEnterSkip)
{
    public static SimulatorRuntimeStateMachineOptions Default { get; } = new(
        LocalPreparationSec: 60.0,
        LanPreparationSec: 60.0,
        RefereeSelfCheckSec: 15.0,
        CountdownSec: 5.0,
        RespawnSec: 10.0,
        AllowLocalEnterSkip: true);
}

public readonly record struct SimulatorRuntimeSnapshot(
    SimulatorRuntimeMode Mode,
    SimulatorRuntimePhase Phase,
    SimulatorRuntimeRoomScreen RoomScreen,
    SimulatorRuntimePanelKind PanelKind,
    OpenGkRefereePanelPage PanelPage,
    double ElapsedSec,
    long Frame,
    double PhaseElapsedSec,
    double PhaseDurationSec,
    double PhaseRemainingSec,
    bool CaptureMouse,
    bool MovementInputEnabled,
    bool NonMovementInputEnabled,
    bool RefereePanelAllowed,
    bool DeathOverlayVisible,
    double DeathOverlayProgress)
{
    public bool PanelOpen => PanelKind != SimulatorRuntimePanelKind.None;
}

public sealed class SimulatorRuntimeStateMachine
{
    private readonly SimulatorRuntimeStateMachineOptions _options;
    private SimulatorRuntimeMode _mode = SimulatorRuntimeMode.Local;
    private SimulatorRuntimePhase _phase = SimulatorRuntimePhase.Room;
    private SimulatorRuntimeRoomScreen _roomScreen = SimulatorRuntimeRoomScreen.MainMenu;
    private SimulatorRuntimePanelKind _panelKind = SimulatorRuntimePanelKind.None;
    private OpenGkRefereePanelPage _panelPage = OpenGkRefereePanelPage.Main;
    private double _elapsedSec;
    private double _phaseElapsedSec;
    private double _phaseDurationSec;
    private double _deathOverlayDurationSec;
    private long _frame;
    private bool _captureMouse = true;

    public SimulatorRuntimeStateMachine(SimulatorRuntimeStateMachineOptions? options = null)
    {
        _options = options ?? SimulatorRuntimeStateMachineOptions.Default;
    }

    public SimulatorRuntimeSnapshot Snapshot => new(
        _mode,
        _phase,
        _roomScreen,
        _panelKind,
        _panelPage,
        _elapsedSec,
        _frame,
        _phaseElapsedSec,
        _phaseDurationSec,
        Math.Max(0.0, _phaseDurationSec - _phaseElapsedSec),
        ResolveCaptureMouse(),
        ResolveMovementInputEnabled(),
        ResolveNonMovementInputEnabled(),
        ResolveRefereePanelAllowed(),
        _phase == SimulatorRuntimePhase.Respawning,
        ResolveDeathOverlayProgress());

    public void ResetToRoom(SimulatorRuntimeMode mode)
    {
        _mode = mode;
        _roomScreen = SimulatorRuntimeRoomScreen.MainMenu;
        SetPhase(SimulatorRuntimePhase.Room, 0.0);
        ClosePanel();
        _captureMouse = false;
    }

    public void BeginPreparation(SimulatorRuntimeMode mode)
    {
        _mode = mode;
        _roomScreen = SimulatorRuntimeRoomScreen.RoomLobby;
        double duration = mode == SimulatorRuntimeMode.Local
            ? _options.LocalPreparationSec
            : _options.LanPreparationSec;
        SetPhase(SimulatorRuntimePhase.Preparation, duration);
        ClosePanel();
        _captureMouse = true;
    }

    public void BeginLive(SimulatorRuntimeMode mode)
    {
        _mode = mode;
        SetPhase(SimulatorRuntimePhase.Live, 0.0);
        ClosePanel();
        _captureMouse = true;
    }

    public void BeginRespawn(double? durationSec = null)
    {
        _deathOverlayDurationSec = Math.Max(0.01, durationSec ?? _options.RespawnSec);
        SetPhase(SimulatorRuntimePhase.Respawning, _deathOverlayDurationSec);
        ClosePanel();
        _captureMouse = true;
    }

    public void EndMatch()
    {
        SetPhase(SimulatorRuntimePhase.MatchEnded, 0.0);
        ClosePanel();
        _captureMouse = false;
    }

    public void Tick(double deltaSec)
    {
        double dt = Math.Max(0.0, deltaSec);
        _elapsedSec += dt;
        _phaseElapsedSec += dt;
        _frame++;
        if (_phaseDurationSec > 1e-6 && _phaseElapsedSec + 1e-6 >= _phaseDurationSec)
        {
            AdvanceTimedPhase();
        }
    }

    public void ApplyInput(GameInputSnapshot input)
    {
        if (input.PressedKeys.Contains(GameKey.O) && ResolveRefereePanelAllowed())
        {
            TogglePanel(SimulatorRuntimePanelKind.LocalOperator);
        }

        if (input.PressedKeys.Contains(GameKey.P) && ResolveRefereePanelAllowed())
        {
            SimulatorRuntimePanelKind kind = _mode == SimulatorRuntimeMode.Local
                ? SimulatorRuntimePanelKind.LocalOperator
                : SimulatorRuntimePanelKind.LanReferee;
            TogglePanel(kind);
        }

        if (input.PressedKeys.Contains(GameKey.Enter) && CanSkipCurrentPhase())
        {
            AdvanceTimedPhase();
        }
        else if (input.PressedKeys.Contains(GameKey.Enter) && _phase == SimulatorRuntimePhase.Room)
        {
            if (_roomScreen == SimulatorRuntimeRoomScreen.MainMenu)
            {
                OpenLocalRoom();
            }
            else
            {
                BeginPreparation(_mode);
            }
        }

        if (input.PressedKeys.Contains(GameKey.Escape))
        {
            _captureMouse = false;
            return;
        }

        if (input.DownKeys.Contains(GameKey.LeftAlt) || input.DownKeys.Contains(GameKey.RightAlt))
        {
            _captureMouse = false;
            return;
        }

        if (input.PressedMouseButtons.Contains(GameMouseButton.Left) && _panelKind == SimulatorRuntimePanelKind.None)
        {
            _captureMouse = true;
        }
    }

    public bool ApplyUiAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        if (string.Equals(action, "linux:release_mouse", StringComparison.OrdinalIgnoreCase))
        {
            _captureMouse = false;
            return true;
        }

        if (string.Equals(action, "linux:capture_mouse", StringComparison.OrdinalIgnoreCase))
        {
            _captureMouse = true;
            ClosePanel();
            return true;
        }

        if (string.Equals(action, "menu_local", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "room_open_local", StringComparison.OrdinalIgnoreCase))
        {
            OpenLocalRoom();
            return true;
        }

        if (string.Equals(action, "room_back", StringComparison.OrdinalIgnoreCase))
        {
            ResetToRoom(_mode);
            return true;
        }

        if (string.Equals(action, "room_start", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "room_ready", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "local_start", StringComparison.OrdinalIgnoreCase))
        {
            BeginPreparation(_mode);
            return true;
        }

        if (string.Equals(action, "local_close", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "p_close", StringComparison.OrdinalIgnoreCase))
        {
            ClosePanel();
            return true;
        }

        if (string.Equals(action, "local_return", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "p_logout", StringComparison.OrdinalIgnoreCase))
        {
            ResetToRoom(_mode);
            return true;
        }

        if (action.StartsWith("local_page:", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("ref_page:", StringComparison.OrdinalIgnoreCase))
        {
            _panelPage = action.EndsWith(":energy", StringComparison.OrdinalIgnoreCase)
                ? OpenGkRefereePanelPage.Energy
                : OpenGkRefereePanelPage.Main;
            return true;
        }

        return false;
    }

    public static bool IsMovementKey(GameKey key)
        => key is GameKey.W
            or GameKey.A
            or GameKey.S
            or GameKey.D
            or GameKey.LeftShift
            or GameKey.RightShift
            or GameKey.X;

    private void TogglePanel(SimulatorRuntimePanelKind kind)
    {
        if (_panelKind == kind)
        {
            ClosePanel();
            _captureMouse = true;
            return;
        }

        _panelKind = kind;
        _captureMouse = false;
    }

    private void OpenLocalRoom()
    {
        _mode = SimulatorRuntimeMode.Local;
        _phase = SimulatorRuntimePhase.Room;
        _roomScreen = SimulatorRuntimeRoomScreen.RoomLobby;
        _phaseElapsedSec = 0.0;
        _phaseDurationSec = 0.0;
        ClosePanel();
        _captureMouse = false;
    }

    private void ClosePanel()
    {
        _panelKind = SimulatorRuntimePanelKind.None;
        _panelPage = OpenGkRefereePanelPage.Main;
    }

    private void AdvanceTimedPhase()
    {
        switch (_phase)
        {
            case SimulatorRuntimePhase.Preparation:
                SetPhase(SimulatorRuntimePhase.RefereeSelfCheck, _options.RefereeSelfCheckSec);
                break;
            case SimulatorRuntimePhase.RefereeSelfCheck:
                SetPhase(SimulatorRuntimePhase.Countdown, _options.CountdownSec);
                break;
            case SimulatorRuntimePhase.Countdown:
            case SimulatorRuntimePhase.Respawning:
                SetPhase(SimulatorRuntimePhase.Live, 0.0);
                ClosePanel();
                _captureMouse = true;
                break;
        }
    }

    private void SetPhase(SimulatorRuntimePhase phase, double durationSec)
    {
        _phase = phase;
        _phaseElapsedSec = 0.0;
        _phaseDurationSec = Math.Max(0.0, durationSec);
    }

    private bool CanSkipCurrentPhase()
        => _mode == SimulatorRuntimeMode.Local
            && _options.AllowLocalEnterSkip
            && _phase is SimulatorRuntimePhase.Preparation
                or SimulatorRuntimePhase.RefereeSelfCheck
                or SimulatorRuntimePhase.Countdown;

    private bool ResolveCaptureMouse()
        => _captureMouse && _panelKind == SimulatorRuntimePanelKind.None;

    private bool ResolveMovementInputEnabled()
        => _phase == SimulatorRuntimePhase.Live && _panelKind == SimulatorRuntimePanelKind.None;

    private static bool ResolveNonMovementInputEnabled()
        => true;

    private bool ResolveRefereePanelAllowed()
        => _phase is SimulatorRuntimePhase.Preparation
            or SimulatorRuntimePhase.RefereeSelfCheck
            or SimulatorRuntimePhase.Countdown
            or SimulatorRuntimePhase.Live
            or SimulatorRuntimePhase.Respawning;

    private double ResolveDeathOverlayProgress()
    {
        if (_phase != SimulatorRuntimePhase.Respawning || _deathOverlayDurationSec <= 1e-6)
        {
            return 0.0;
        }

        return Math.Clamp(_phaseElapsedSec / _deathOverlayDurationSec, 0.0, 1.0);
    }
}
