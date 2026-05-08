using System.Globalization;
using System.Drawing;
using System.Net.Sockets;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Windows.Forms;
using Simulator.Core;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private enum LocalRoomSeatKind
    {
        Empty,
        Player,
        Ai,
    }

    private const double LanSnapshotIntervalSec = 0.10;
    private const double LanDigestIntervalSec = 0.50;
    private const int LanRemoteInputBufferLimit = 180;
    private const int LanMaxRefereeCount = 3;
    private const int LanMaxSpectatorCount = 5;
    private const int LanSpawnPointCount = 5;
    private static readonly string[] LanUcEntityKeys = LanRobotSeatCatalog.ControllableEntityKeys.ToArray();
    private static readonly string[] LanUcRoomSeatEntityKeys = LanRobotSeatCatalog.RoomSeatEntityKeys.ToArray();

    private LanMultiplayerSession? _lanSession;
    private PlayerControlState? _latestLanRemoteInput;
    private readonly SortedDictionary<long, PlayerControlState> _lanLocalInputFrames = new();
    private readonly SortedDictionary<long, PlayerControlState> _lanRemoteInputFrames = new();
    private long _lanSimulationSequence;
    private long _lanInputSequence;
    private long _lanLobbySequence;
    private long _lanDigestSequence;
    private long _lanSnapshotSequence;
    private long _lanRefereeReportSequence;
    private double _lastLanDigestSentAtSec;
    private double _lastLanSnapshotSentAtSec;
    private double _lastLanPoseDriftLogSec;
    private long _lanInputFramesSent;
    private long _lanInputFramesReceived;
    private long _lanSnapshotsSent;
    private long _lanSnapshotsReceived;
    private long _lanAuthoritativeSnapshotsSent;
    private long _lanAuthoritativeSnapshotsReceived;
    private LanTrafficSnapshot? _lastLanTrafficSnapshot;
    private LanTrafficReport? _lastLanRemoteTrafficReport;
    private long _lanTrafficReportSequence;
    private double _lastLanTrafficReportSentAtSec = double.NegativeInfinity;
    private double _lastLanTrafficReportReceivedAtSec = double.NegativeInfinity;
    private double _lastLanLocalInputSentAtSec = double.NegativeInfinity;
    private double _lastLanRemoteInputReceivedAtSec = double.NegativeInfinity;
    private double _lastLanSnapshotReceivedAtSec = double.NegativeInfinity;
    private double _lastLanCoalescedRealtimeLogSec = double.NegativeInfinity;
    private double _lastLanSnapshotDetailLogSec = double.NegativeInfinity;
    private double _lastLanAuthoritativeSnapshotDetailLogSec = double.NegativeInfinity;
    private double _lastLanSnapshotTxDetailLogSec = double.NegativeInfinity;
    private double _lastLanAuthoritativeSnapshotTxDetailLogSec = double.NegativeInfinity;
    private long _lanCoalescedRealtimeEvents;
    private string _lanStatusLine = string.Empty;
    private string _lanLocalPlayerId = Guid.NewGuid().ToString("N");
    private LanValidationDigest? _lastLanRemoteDigest;
    private readonly Dictionary<string, LanMatchSeatState> _lanRosterBySeatId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LanLocalRefereeReport> _lanRefereeReports = new();
    private string _lanLocalTeam = "red";
    private string _lanRemoteTeam = "blue";
    private string _lanLocalEntityKey = "robot_1";
    private string _lanRemoteEntityKey = "robot_3";
    private string _lanRemotePlayerName = "对端玩家";
    private bool _lanRoomPanelOpen;
    private bool _lanRoomHostMode = true;
    private bool _lanRoomBusy;
    private string _lanRoomNameText = "ARTINX 5v5 房间";
    private string _lanPlayerNameText = Environment.UserName;
    private string _lanHostAddressText = LanMultiplayerSession.ResolvePreferredLocalAddress();
    private string _lanPortText = "26011";
    private string? _lanFocusedField;
    private string _lanRoomStatusText = "裁判端创建主机房间，最多 10 名玩家加入；加入房间需要填写主机局域网 IP。";
    private bool _lanLocalReady;
    private string _lanLocalMemberRole = "player";
    private int _lanLocalSpawnPointIndex = 0;
    private string _lanRoomMatchMode = "uc";
    private bool _openGkStartHubOpen;
    private bool _openGkCreateModePickerOpen;
    private string _openGkModePickerIntent = "create";
    private bool _openGkShowLanRooms = true;
    private int _openGkSelectedDiscoveredRoomIndex = -1;
    private LanRoomGameSettings _lanRoomSettings = new();
    private LanRoomDiscoveryService? _lanRoomDiscovery;
    private IReadOnlyList<LanDiscoveredRoom> _lanDiscoveredRooms = Array.Empty<LanDiscoveredRoom>();
    private double _lastLanRoomAnnouncementAtSec = double.NegativeInfinity;
    private bool? _lanSavedAiEnabled;
    private readonly List<string> _lanUplinkEventLog = new();
    private readonly List<string> _lanDownlinkEventLog = new();
    private MatchStartupPhase _lastAppliedLanStartupPhase = MatchStartupPhase.None;
    private readonly Dictionary<string, LocalRoomSeatKind> _localRoomSeats = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _localRoomSeatsGate = new();
    private bool _localRoomSeatActionBusy;
    private long _localRoomLastSeatActionTicks;
    private bool _localRoomPanelOpen;
    private bool _localRoomMatchActive;
    private bool _localRefereePanelOpen;
    private string _localRoomStatusText = "本地房间默认不填充机器人，点击席位添加本地玩家或 AI。";

    private bool IsLanMultiplayerActive
        => _lanSession is not null;

    private bool IsLocalRoomActive
        => _localRoomPanelOpen || _localRoomMatchActive;

    private bool IsLanMultiplayerMatchActive
        => _lanSession is not null
            && _appState == SimulatorAppState.InMatch;

    private bool IsLanRemoteAuthoritativeClient
        => IsLanMultiplayerMatchActive
            && _lanSession?.IsHost == false;

    private bool IsLanRefereeClient
        => _lanSession is not null
            && !IsLanDuelRoomMode
            && string.Equals(ResolveLanLocalMemberRole(), "referee", StringComparison.OrdinalIgnoreCase);

    private bool IsLanObserverClient
        => _lanSession is not null
            && !string.Equals(ResolveLanLocalMemberRole(), "player", StringComparison.OrdinalIgnoreCase);

    private bool IsLanDuelRoomMode
        => string.Equals(_lanRoomMatchMode, "1v1", StringComparison.OrdinalIgnoreCase);

    private bool CanEditLanRoomSettings()
        => ((_lanSession?.IsHost == true) || (_localRoomPanelOpen && _lanSession is null))
            && !IsLanDuelRoomMode;

    private void OpenLocalRoom(string matchMode)
    {
        CloseLanSession(statusMessage: "已切换到本地房间。");
        _lanRoomMatchMode = string.Equals(matchMode, "1v1", StringComparison.OrdinalIgnoreCase) ? "1v1" : "uc";
        _lanRoomSettings = _lanRoomSettings with { MatchMode = _lanRoomMatchMode };
        _lanRoomNameText = string.Equals(_lanRoomMatchMode, "1v1", StringComparison.OrdinalIgnoreCase)
            ? "ARTINX 本地 1v1"
            : "RoboMaster 2026 本地房间";
        lock (_localRoomSeatsGate)
        {
            _localRoomSeats.Clear();
        }
        _localRoomPanelOpen = true;
        _localRoomMatchActive = false;
        _localRefereePanelOpen = false;
        _lanRoomPanelOpen = false;
        _openGkStartHubOpen = false;
        _openGkCreateModePickerOpen = false;
        _localRoomStatusText = "本地房间已重置：默认无机器人。点击席位添加本地玩家或 AI。";
        _host.SetMatchModePreservingLoadedWorld(
            string.Equals(_lanRoomMatchMode, "1v1", StringComparison.OrdinalIgnoreCase) ? "duel_1v1" : "full");
        SetLocalRoomAiEnabledSafely(false, "open_room");
        InvalidateHudPortraitCache();
        InvalidateGpuOverlayLayer();
        Invalidate();
    }

    private bool TryExecuteLocalRoomAction(string action)
    {
        if (!action.StartsWith("local_room_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (action.StartsWith("local_room_slot:", StringComparison.OrdinalIgnoreCase))
        {
            long nowTicks = Stopwatch.GetTimestamp();
            if (_localRoomSeatActionBusy
                || (_localRoomLastSeatActionTicks != 0
                    && (nowTicks - _localRoomLastSeatActionTicks) * 1000.0 / Stopwatch.Frequency < 120.0))
            {
                return true;
            }

            string[] parts = action.Split(':', 3);
            if (parts.Length == 3 && _localRoomPanelOpen)
            {
                _localRoomSeatActionBusy = true;
                try
                {
                    CycleLocalRoomSeat(parts[1], parts[2]);
                }
                catch (Exception exception)
                {
                    LogLocalRoomException("cycle_seat", exception);
                    _localRoomStatusText = "本地房间席位更新失败，已阻止闪退；详情见 logs/local_room_crash.log。";
                    InvalidateHudPortraitCache();
                    InvalidateGpuOverlayLayer();
                    Invalidate();
                }
                finally
                {
                    _localRoomSeatActionBusy = false;
                    _localRoomLastSeatActionTicks = Stopwatch.GetTimestamp();
                }
            }

            return true;
        }

        switch (action)
        {
            case "local_room_start_match":
                try
                {
                    StartLocalRoomMatchFromRoom();
                }
                catch (Exception exception)
                {
                    LogLocalRoomException("start_match", exception);
                    _localRoomStatusText = "本地对局启动失败，已保留在房间；详情见 logs/local_room_crash.log。";
                    _localRoomPanelOpen = true;
                    _localRoomMatchActive = false;
                    InvalidateHudPortraitCache();
                    InvalidateGpuOverlayLayer();
                    Invalidate();
                }

                return true;
            case "local_room_disconnect":
                CloseLocalRoom();
                return true;
            default:
                return true;
        }
    }

    private void CycleLocalRoomSeat(string team, string entityKey)
    {
        string normalizedTeam = Simulator3dOptions.NormalizeTeam(team);
        string normalizedEntity = NormalizeLanDuelEntityKey(entityKey);
        if (!IsLanControllableRobotSeat(normalizedEntity))
        {
            _localRoomStatusText = $"{ResolveOpenGkLanTeamLabel(normalizedTeam)} {ResolveOpenGkLanEntityLabel(normalizedEntity)} 是占位席，暂不生成机器人。";
            return;
        }

        string key = ResolveLocalRoomSeatKey(normalizedTeam, normalizedEntity);
        LocalRoomSeatKind next;
        bool hasAnyAi;
        lock (_localRoomSeatsGate)
        {
            _localRoomSeats.TryGetValue(key, out LocalRoomSeatKind current);
            next = current switch
            {
                LocalRoomSeatKind.Empty => HasLocalRoomPlayerSeatLocked() ? LocalRoomSeatKind.Ai : LocalRoomSeatKind.Player,
                LocalRoomSeatKind.Player => LocalRoomSeatKind.Ai,
                _ => LocalRoomSeatKind.Empty,
            };

            if (next == LocalRoomSeatKind.Player)
            {
                foreach (string playerKey in _localRoomSeats
                             .Where(pair => pair.Value == LocalRoomSeatKind.Player)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _localRoomSeats[playerKey] = LocalRoomSeatKind.Ai;
                }
            }

            if (next == LocalRoomSeatKind.Empty)
            {
                _localRoomSeats.Remove(key);
            }
            else
            {
                _localRoomSeats[key] = next;
            }

            hasAnyAi = _localRoomSeats.Values.Any(kind => kind == LocalRoomSeatKind.Ai);
        }

        _localRoomStatusText = next switch
        {
            LocalRoomSeatKind.Player => $"已将 {ResolveOpenGkLanTeamLabel(normalizedTeam)} {ResolveOpenGkLanEntityLabel(normalizedEntity)} 设置为本地玩家。",
            LocalRoomSeatKind.Ai => $"已将 {ResolveOpenGkLanTeamLabel(normalizedTeam)} {ResolveOpenGkLanEntityLabel(normalizedEntity)} 设置为 AI。",
            _ => $"已清空 {ResolveOpenGkLanTeamLabel(normalizedTeam)} {ResolveOpenGkLanEntityLabel(normalizedEntity)}。",
        };
        SetLocalRoomAiEnabledSafely(hasAnyAi, "cycle_seat");
        InvalidateHudPortraitCache();
        InvalidateGpuOverlayLayer();
    }

    private bool HasLocalRoomPlayerSeat()
    {
        lock (_localRoomSeatsGate)
        {
            return HasLocalRoomPlayerSeatLocked();
        }
    }

    private bool HasLocalRoomPlayerSeatLocked()
        => _localRoomSeats.Values.Any(kind => kind == LocalRoomSeatKind.Player);

    private bool HasActiveRoomControlSource(string team, string entityKey)
    {
        string normalizedTeam = Simulator3dOptions.NormalizeTeam(team);
        string normalizedEntity = NormalizeLanDuelEntityKey(entityKey);
        if (!IsLanControllableRobotSeat(normalizedEntity))
        {
            return false;
        }

        if (_localRoomPanelOpen || _localRoomMatchActive)
        {
            return BuildLocalRoomSeatStates().Any(seat =>
                seat.Connected
                && string.Equals(seat.Role, "player", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Simulator3dOptions.NormalizeTeam(seat.Team), normalizedTeam, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeLanDuelEntityKey(seat.EntityKey), normalizedEntity, StringComparison.OrdinalIgnoreCase));
        }

        if (_lanSession is not null)
        {
            return _lanRosterBySeatId.Values.Any(seat =>
                seat.Connected
                && string.Equals(seat.Role, "player", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Simulator3dOptions.NormalizeTeam(seat.Team), normalizedTeam, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeLanDuelEntityKey(seat.EntityKey), normalizedEntity, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    private void StartLocalRoomMatchFromRoom()
    {
        if (BuildLocalRoomSeatStates().Length == 0)
        {
            _localRoomStatusText = "请至少点击一个席位添加本地玩家或 AI。";
            return;
        }

        EnsureLocalRoomHasPlayerSeat();
        _localRoomPanelOpen = false;
        _localRoomMatchActive = true;
        _localRefereePanelOpen = false;
        _host.SetMatchModePreservingLoadedWorld(
            string.Equals(_lanRoomMatchMode, "1v1", StringComparison.OrdinalIgnoreCase) ? "duel_1v1" : "full");
        SetLocalRoomAiEnabledSafely(HasLocalRoomAiSeat(), "start_match");
        _host.ApplyRoomGameSettings(_lanRoomSettings);
        _observerMode = false;
        _observerPinned = false;
        _firstPersonView = true;
        _followSelection = true;
        SelectLocalRoomPlayerEntity();
        BeginMatchStartupSequence(resetWorld: true);
    }

    private void CloseLocalRoom()
    {
        _localRoomPanelOpen = false;
        _localRoomMatchActive = false;
        _localRefereePanelOpen = false;
        lock (_localRoomSeatsGate)
        {
            _localRoomSeats.Clear();
        }
        _openGkStartHubOpen = true;
        _localRoomStatusText = "本地房间已关闭。";
        _host.ClearLanPreparationSelectionFilter();
        SetLocalRoomAiEnabledSafely(true, "close_room");
        InvalidateHudPortraitCache();
        InvalidateGpuOverlayLayer();
    }

    private void EnsureLocalRoomHasPlayerSeat()
    {
        lock (_localRoomSeatsGate)
        {
            if (HasLocalRoomPlayerSeatLocked())
            {
                return;
            }

            string firstKey = _localRoomSeats.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).First();
            _localRoomSeats[firstKey] = LocalRoomSeatKind.Player;
        }
    }

    private void ApplyLocalRoomSelectionsToWorld(bool snapCameraToPlayer = true, bool hardTrimInactiveRobots = false)
    {
        if (_lanSession is not null)
        {
            return;
        }

        _host.ApplyRoomGameSettings(_lanRoomSettings);
        LanMatchSeatState[] seats = BuildLocalRoomSeatStates();
        try
        {
            _host.ApplyLanPreparationSelections(seats, hardTrimInactiveRobots);
        }
        catch (Exception exception)
        {
            LogLocalRoomException("apply_selection", exception);
            _localRoomStatusText = "本地房间席位应用失败，已保留在房间；详情见 logs/local_room_crash.log。";
            InvalidateHudPortraitCache();
            InvalidateGpuOverlayLayer();
            return;
        }

        InvalidateOpenGkUcTopHudCache();
        if (!snapCameraToPlayer
            || (!_localRoomMatchActive && _appState != SimulatorAppState.InMatch))
        {
            return;
        }

        if (SelectLocalRoomPlayerEntity()
            && _host.SelectedEntity is { IsSimulationSuppressed: false })
        {
            SnapCameraToSelectedEntity();
        }
    }

    private LanMatchSeatState[] BuildLocalRoomSeatStates()
    {
        KeyValuePair<string, LocalRoomSeatKind>[] snapshot;
        lock (_localRoomSeatsGate)
        {
            snapshot = _localRoomSeats.ToArray();
        }

        var seats = new List<LanMatchSeatState>(snapshot.Length);
        foreach (KeyValuePair<string, LocalRoomSeatKind> pair in snapshot)
        {
            if (pair.Value == LocalRoomSeatKind.Empty)
            {
                continue;
            }

            try
            {
                (string team, string entityKey) = ParseLocalRoomSeatKey(pair.Key);
                if (!IsLanControllableRobotSeat(entityKey))
                {
                    continue;
                }

                bool player = pair.Value == LocalRoomSeatKind.Player;
                seats.Add(new LanMatchSeatState(
                    ResolveLanSeatId(team, entityKey),
                    team,
                    ResolveLanRobotNumber(entityKey),
                    entityKey,
                    player ? "local_player" : $"local_ai_{team}_{entityKey}",
                    player ? "本地玩家" : "AI 人机",
                    Connected: true,
                    Ready: true,
                    Role: "player",
                    SpawnPointIndex: player ? _lanLocalSpawnPointIndex : ResolveLanDefaultSpawnPointIndex(entityKey),
                    ChassisMode: ResolveLocalRoomChassisMode(entityKey)));
            }
            catch (Exception exception)
            {
                LogLocalRoomException("build_seat_state", exception);
            }
        }

        return seats.ToArray();
    }

    private IEnumerable<LanRoomMemberState> EnumerateLocalRoomMembers()
        => BuildLocalRoomSeatStates()
            .Select(seat => new LanRoomMemberState(
                seat.SeatId,
                seat.PlayerId,
                seat.PlayerName,
                seat.Team,
                seat.EntityKey,
                ResolveLanSlotIndex(seat.Team, seat.EntityKey),
                "player",
                IsLocal: string.Equals(seat.PlayerId, "local_player", StringComparison.OrdinalIgnoreCase),
                Ready: true,
                seat.SpawnPointIndex,
                seat.ChassisMode))
            .ToArray();

    private bool SelectLocalRoomPlayerEntity()
    {
        if (!_localRoomMatchActive && !_localRoomPanelOpen)
        {
            return false;
        }

        LanMatchSeatState[] seats = BuildLocalRoomSeatStates();
        LanMatchSeatState? seat = seats
            .Where(candidate => string.Equals(candidate.PlayerId, "local_player", StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => ResolveLanSlotIndex(candidate.Team, candidate.EntityKey))
            .FirstOrDefault()
            ?? seats
            .OrderBy(candidate => ResolveLanSlotIndex(candidate.Team, candidate.EntityKey))
            .FirstOrDefault();
        if (seat is null)
        {
            return false;
        }

        if (!_host.SetSelectedEntity($"{seat.Team}_{seat.EntityKey}"))
        {
            return false;
        }

        _followSelection = true;
        return true;
    }

    private bool SetLocalRoomPlayerPreparationEntity(string entityKey)
    {
        string normalizedEntity = NormalizeLanDuelEntityKey(entityKey);
        if (!IsLanControllableRobotSeat(normalizedEntity))
        {
            return false;
        }

        string team;
        bool hasAnyAi;
        lock (_localRoomSeatsGate)
        {
            string playerKey = _localRoomSeats
                .Where(pair => pair.Value == LocalRoomSeatKind.Player)
                .Select(pair => pair.Key)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? string.Empty;
            team = !string.IsNullOrWhiteSpace(playerKey)
                ? ParseLocalRoomSeatKey(playerKey).Team
                : Simulator3dOptions.NormalizeTeam(_lanLocalTeam);
            if (string.IsNullOrWhiteSpace(team))
            {
                team = Simulator3dOptions.NormalizeTeam(_host.SelectedTeam);
            }

            if (!string.IsNullOrWhiteSpace(playerKey))
            {
                _localRoomSeats.Remove(playerKey);
            }

            string nextKey = ResolveLocalRoomSeatKey(team, normalizedEntity);
            _localRoomSeats[nextKey] = LocalRoomSeatKind.Player;
            hasAnyAi = _localRoomSeats.Values.Any(kind => kind == LocalRoomSeatKind.Ai);
        }

        _lanLocalTeam = team;
        _lanLocalEntityKey = normalizedEntity;
        _lanLocalSpawnPointIndex = Math.Clamp(_lanLocalSpawnPointIndex, 0, LanSpawnPointCount - 1);
        SetLocalRoomAiEnabledSafely(hasAnyAi, "prepare_entity");
        return true;
    }

    private static void LogLocalRoomException(string context, Exception exception)
    {
        try
        {
            SimulatorRuntimeLog.Append(
                "local_room_crash.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {context} {exception}");
        }
        catch
        {
        }
    }

    private bool HasLocalRoomAiSeat()
    {
        lock (_localRoomSeatsGate)
        {
            return _localRoomSeats.Values.Any(kind => kind == LocalRoomSeatKind.Ai);
        }
    }

    private bool SetLocalRoomAiEnabledSafely(bool enabled, string context)
    {
        try
        {
            if (_host.AiEnabled == enabled)
            {
                return true;
            }

            return _host.SetAiEnabled(enabled);
        }
        catch (Exception exception)
        {
            LogLocalRoomException($"set_ai_enabled:{context}", exception);
            _localRoomStatusText = "AI 状态切换失败，已阻止房间闪退；详情见 logs/local_room_crash.log。";
            return false;
        }
    }

    private static string ResolveLocalRoomSeatKey(string team, string entityKey)
        => $"{Simulator3dOptions.NormalizeTeam(team)}:{NormalizeLanDuelEntityKey(entityKey)}";

    private static (string Team, string EntityKey) ParseLocalRoomSeatKey(string key)
    {
        string[] parts = key.Split(':', 2);
        string team = parts.Length > 0 ? Simulator3dOptions.NormalizeTeam(parts[0]) : "red";
        string entityKey = parts.Length > 1 ? NormalizeLanDuelEntityKey(parts[1]) : "robot_1";
        return (team, entityKey);
    }

    private string ResolveLocalRoomChassisMode(string entityKey)
        => string.Equals(NormalizeLanDuelEntityKey(entityKey), "robot_3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeLanDuelEntityKey(entityKey), "robot_4", StringComparison.OrdinalIgnoreCase)
            ? _host.InfantryMode
            : string.Empty;

    private void OpenLanRoomMenu()
    {
        _lanRoomPanelOpen = true;
        _openGkStartHubOpen = true;
        _mainMenuStartExpanded = true;
        _mainMenuSingleExpanded = false;
        _mainMenuMultiplayerExpanded = true;
        _mainMenuEditorExpanded = false;
        _lanRoomStatusText = _lanSession?.StatusText
            ?? (_lanRoomHostMode
                ? "准备创建裁判主机房间。"
                : "准备加入房间，请确认主机 IP 与端口。");
        Invalidate();
    }

    private void EnterLanMultiplayerLobby()
    {
        if (_lanSession is null)
        {
            _lanRoomStatusText = "请先创建或加入房间。";
            return;
        }

        _lanRoomPanelOpen = false;
        _openGkStartHubOpen = false;
        _host.SetMatchModePreservingLoadedWorld(
            string.Equals(_lanRoomMatchMode, "1v1", StringComparison.OrdinalIgnoreCase) ? "duel_1v1" : "full");
        if (_host.IsDuelMode)
        {
            ApplyLanDuelConfigurationToHost();
        }
        _mainMenuStartExpanded = false;
        _mainMenuEditorExpanded = false;
        EnterLobby();
        PublishLanLobbySelection();
    }

    private void StartLanRoomMatchFromRoom()
    {
        if (_lanSession is null)
        {
            _lanRoomStatusText = "请先创建房间。";
            return;
        }

        if (!_lanSession.IsHost)
        {
            _lanRoomStatusText = "玩家端等待裁判主机开始对局。";
            return;
        }

        if (!CanLanHostStartRoomMatch())
        {
            _lanRoomStatusText = "至少需要一名已准备玩家才能开始。";
            return;
        }

        _host.SetMatchModePreservingLoadedWorld(
            string.Equals(_lanRoomMatchMode, "1v1", StringComparison.OrdinalIgnoreCase) ? "duel_1v1" : "full");
        EnterLanMultiplayerLobby();
        StartMatch();
    }

    private bool CanLanHostStartRoomMatch()
    {
        if (_lanSession?.IsHost != true)
        {
            return false;
        }

        LanMatchSeatState[] players = _lanRosterBySeatId.Values
            .Where(seat => seat.Connected && string.Equals(seat.Role, "player", StringComparison.OrdinalIgnoreCase))
            .Where(seat => IsLanControllableRobotSeat(seat.EntityKey))
            .ToArray();
        if (string.Equals(_lanRoomMatchMode, "1v1", StringComparison.OrdinalIgnoreCase))
        {
            return players.Length == 2 && players.All(seat => seat.Ready);
        }

        return players.Length > 0 && players.All(seat => seat.Ready);
    }

    private void ResetLanSession(LanMultiplayerSession session)
    {
        _lanSession?.Dispose();
        _lanSession = session;
        _lanSavedAiEnabled ??= _host.AiEnabled;
        _host.SetAiEnabled(false);
        ResetLanRuntimeSyncState();
        _lanLobbySequence = 0;
        _lanLocalTeam = session.IsHost ? "red" : "blue";
        _lanRemoteTeam = session.IsHost ? "blue" : "red";
        _lanLocalEntityKey = NormalizeLanDuelEntityKey(_host.SingleUnitTestEntityKey);
        _lanRemoteEntityKey = session.IsHost ? "robot_3" : "robot_1";
        _lanRemotePlayerName = session.IsHost ? "Guest" : "Host";
        _lanLocalMemberRole = session.IsHost && !IsLanDuelRoomMode ? "referee" : "player";
        _lanLocalReady = session.IsHost && !IsLanDuelRoomMode;
        _lanStatusLine = _lanSession.StatusText;
        _lanRoomStatusText = _lanSession.StatusText;
        _lanPortText = _lanSession.Port.ToString(CultureInfo.InvariantCulture);
        _lanRoomMembers.Clear();
        _lanRosterBySeatId.Clear();
        RememberLocalLanRoomMember();
        ReinitializeLanRoomWorldForCurrentRoster();
        if (session.IsConnected)
        {
            PublishLanLobbySelection();
        }
    }

    private void ReinitializeLanRoomWorldForCurrentRoster()
    {
        if (_lanSession is null)
        {
            return;
        }

        _host.SetMatchModePreservingLoadedWorld(
            string.Equals(_lanRoomMatchMode, "1v1", StringComparison.OrdinalIgnoreCase) ? "duel_1v1" : "full");
        _host.ResetWorld();
        _host.ApplyRoomGameSettings(_lanRoomSettings);
        _host.ApplyLanPreparationSelections(CreateLanRoomRoster(_lanLobbySequence).Seats, hardTrimInactiveRobots: false);
        InvalidateHudPortraitCache();
        InvalidateGpuOverlayLayer();
    }

    private void ResetLanRuntimeSyncState()
    {
        _latestLanRemoteInput = null;
        _lanLocalInputFrames.Clear();
        _lanRemoteInputFrames.Clear();
        _lanSimulationSequence = 0;
        _lanInputSequence = 0;
        _lanDigestSequence = 0;
        _lanSnapshotSequence = 0;
        _lanRefereeReportSequence = 0;
        _lastLanDigestSentAtSec = double.NegativeInfinity;
        _lastLanSnapshotSentAtSec = double.NegativeInfinity;
        _lastLanPoseDriftLogSec = 0.0;
        _lanInputFramesSent = 0;
        _lanInputFramesReceived = 0;
        _lanSnapshotsSent = 0;
        _lanSnapshotsReceived = 0;
        _lanAuthoritativeSnapshotsSent = 0;
        _lanAuthoritativeSnapshotsReceived = 0;
        _lastLanTrafficSnapshot = null;
        _lastLanRemoteTrafficReport = null;
        _lanTrafficReportSequence = 0;
        _lastLanTrafficReportSentAtSec = double.NegativeInfinity;
        _lastLanTrafficReportReceivedAtSec = double.NegativeInfinity;
        _lastLanLocalInputSentAtSec = double.NegativeInfinity;
        _lastLanRemoteInputReceivedAtSec = double.NegativeInfinity;
        _lastLanSnapshotReceivedAtSec = double.NegativeInfinity;
        _lastLanCoalescedRealtimeLogSec = double.NegativeInfinity;
        _lastLanSnapshotDetailLogSec = double.NegativeInfinity;
        _lastLanAuthoritativeSnapshotDetailLogSec = double.NegativeInfinity;
        _lastLanSnapshotTxDetailLogSec = double.NegativeInfinity;
        _lastLanAuthoritativeSnapshotTxDetailLogSec = double.NegativeInfinity;
        _lanCoalescedRealtimeEvents = 0;
        _lastLanRemoteDigest = null;
        _lanRefereeReports.Clear();
        _lanUplinkEventLog.Clear();
        _lanDownlinkEventLog.Clear();
        _lastAppliedLanStartupPhase = MatchStartupPhase.None;
    }

    private void AppendLanTrafficLog(bool uplink, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        List<string> target = uplink ? _lanUplinkEventLog : _lanDownlinkEventLog;
        target.Add(line);
        if (target.Count > 256)
        {
            target.RemoveRange(0, target.Count - 256);
        }

        SimulatorRuntimeLog.Append(uplink ? "lan_uplink.log" : "lan_downlink.log", line);
    }

    private static void AppendLanTrafficDetailLog(bool uplink, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        SimulatorRuntimeLog.Append(uplink ? "lan_uplink_detail.log" : "lan_downlink_detail.log", line);
    }

    private static bool IsLanRealtimeSnapshotEvent(string type)
        => string.Equals(type, LanProtocolMessageTypes.AuthoritativeSnapshot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, LanProtocolMessageTypes.Snapshot, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldAppendLanCompactEventLog(string type)
        => !IsLanRealtimeSnapshotEvent(type)
            && !string.Equals(type, LanProtocolMessageTypes.Digest, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, LanProtocolMessageTypes.Input, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, LanProtocolMessageTypes.PlayerInput, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, LanProtocolMessageTypes.TrafficReport, StringComparison.OrdinalIgnoreCase);

    private bool TryMarkLanDetailLogInterval(ref double lastLogSec, double intervalSec)
    {
        double nowSec = _frameClock.Elapsed.TotalSeconds;
        if (nowSec - lastLogSec < intervalSec)
        {
            return false;
        }

        lastLogSec = nowSec;
        return true;
    }

    private void PublishLanTrafficReportIfDue()
    {
        if (_lanSession is null || !_lanSession.IsConnected)
        {
            return;
        }

        double nowSec = _frameClock.Elapsed.TotalSeconds;
        if (nowSec - _lastLanTrafficReportSentAtSec < 1.0)
        {
            return;
        }

        _lastLanTrafficReportSentAtSec = nowSec;
        _lanTrafficReportSequence++;
        LanTrafficSnapshot snapshot = _lastLanTrafficSnapshot ?? _lanSession.CaptureTrafficSnapshot();
        LanTrafficReport report = CreateLanTrafficReport(snapshot, _lanTrafficReportSequence, nowSec);
        _ = _lanSession.SendTrafficReportAsync(report);
        AppendLanTrafficLog(true, $"traffic_report seq={report.Sequence} tx={report.SentMegabitsPerSecond:0.000}Mbps rx={report.ReceivedMegabitsPerSecond:0.000}Mbps drop={report.TotalDroppedRealtimeMessages}/{report.TotalDroppedReliableMessages}");
        AppendLanTrafficDetailLog(true, FormatLanTrafficReportLog(report));
    }

    private LanTrafficReport CreateLanTrafficReport(LanTrafficSnapshot snapshot, long sequence, double nowSec)
        => new(
            sequence,
            _lanLocalPlayerId,
            string.IsNullOrWhiteSpace(_lanPlayerNameText) ? Environment.UserName : _lanPlayerNameText.Trim(),
            ResolveLanLocalMemberRole(),
            _lanSession?.IsHost == true,
            nowSec,
            snapshot.SentMegabitsPerSecond,
            snapshot.ReceivedMegabitsPerSecond,
            snapshot.SentKilobytesPerSecond,
            snapshot.ReceivedKilobytesPerSecond,
            snapshot.TotalSentBytes,
            snapshot.TotalReceivedBytes,
            snapshot.TotalSentMessages,
            snapshot.TotalReceivedMessages,
            snapshot.TotalDroppedRealtimeMessages,
            snapshot.TotalDroppedReliableMessages,
            ResolveTopLanTrafficType(snapshot.SentMessagesByType),
            ResolveTopLanTrafficType(snapshot.ReceivedMessagesByType),
            ResolveTopLanTrafficType(snapshot.DroppedMessagesByType),
            FormatLanTrafficTypeBreakdown(snapshot.SentMessagesByType),
            FormatLanTrafficTypeBreakdown(snapshot.ReceivedMessagesByType),
            FormatLanTrafficTypeBreakdown(snapshot.DroppedMessagesByType));

    private static string ResolveTopLanTrafficType(IReadOnlyDictionary<string, long> counts)
    {
        KeyValuePair<string, long> top = counts.FirstOrDefault();
        return string.IsNullOrWhiteSpace(top.Key) ? "-" : $"{top.Key}:{top.Value}";
    }

    private static string FormatLanTrafficTypeBreakdown(IReadOnlyDictionary<string, long> counts)
    {
        if (counts.Count == 0)
        {
            return "-";
        }

        return string.Join(',', counts.Take(6).Select(pair => $"{pair.Key}:{pair.Value}"));
    }

    private static string FormatLanTrafficReportLog(LanTrafficReport report)
        => $"traffic_report remote={report.PlayerName}/{report.Role} host={(report.IsHost ? 1 : 0)} seq={report.Sequence} local_time={report.LocalTimeSec:0.000} tx={report.SentMegabitsPerSecond:0.000}Mbps/{report.SentKilobytesPerSecond:0.0}KBps rx={report.ReceivedMegabitsPerSecond:0.000}Mbps/{report.ReceivedKilobytesPerSecond:0.0}KBps bytes={report.TotalSentBytes}/{report.TotalReceivedBytes} msg={report.TotalSentMessages}/{report.TotalReceivedMessages} drop={report.TotalDroppedRealtimeMessages}/{report.TotalDroppedReliableMessages} top={report.TopSentType}/{report.TopReceivedType}/{report.TopDroppedType} sent=[{report.SentTypeBreakdown}] recv=[{report.ReceivedTypeBreakdown}] dropped=[{report.DroppedTypeBreakdown}]";

    private string FormatLanPlayerInputDetail(string direction, LanPlayerInputFrame frame)
    {
        LanInputFrame input = frame.Input;
        LanTrafficSnapshot? traffic = _lastLanTrafficSnapshot;
        string trafficText = traffic is null
            ? "traffic=-"
            : $"traffic_tx_rx={traffic.SentMegabitsPerSecond:0.000}/{traffic.ReceivedMegabitsPerSecond:0.000}Mbps bytes={traffic.TotalSentBytes}/{traffic.TotalReceivedBytes}";
        return $"{direction}_player_input seq={frame.Sequence} tick={frame.SimulationTick} client_t={frame.ClientTimeSec:0.000} player={frame.PlayerId} seat={frame.SeatId} entity={frame.EntityId} enabled={(input.Enabled ? 1 : 0)} move=({input.MoveForward:0.000},{input.MoveRight:0.000}) look=({input.TurretYawDeltaDeg:0.000},{input.GimbalPitchDeltaDeg:0.000}) fire={(input.FirePressed ? 1 : 0)} auto={(input.AutoAimPressed ? 1 : 0)} jump={(input.JumpRequested ? 1 : 0)} climb={(input.StepClimbModeActive ? 1 : 0)} gyro={(input.SmallGyroActive ? 1 : 0)} buy={(input.BuyAmmoRequested ? 1 : 0)} queue_l_r={_lanLocalInputFrames.Count}/{_lanRemoteInputFrames.Count} sim_seq={_lanSimulationSequence} {trafficText}";
    }

    private void CloseLanSession(string? statusMessage = null)
    {
        /*
        string closeMessage = string.IsNullOrWhiteSpace(statusMessage)
            ? "连接已关闭。"
            : statusMessage.Trim();
        */
        string closeMessage = string.IsNullOrWhiteSpace(statusMessage)
            ? "\u8FDE\u63A5\u5DF2\u5173\u95ED\u3002"
            : statusMessage.Trim();
        if (_lanSession?.IsHost == true)
        {
            _lanRoomDiscovery ??= new LanRoomDiscoveryService();
            _lanRoomDiscovery.WithdrawRoom(
                _lanRoomNameText,
                string.IsNullOrWhiteSpace(_lanPlayerNameText) ? Environment.UserName : _lanPlayerNameText.Trim(),
                LanMultiplayerSession.ResolvePreferredLocalAddress(),
                _lanSession.Port);
            _lanDiscoveredRooms = _lanRoomDiscovery.GetRooms().ToList();
        }

        _lanSession?.Dispose();
        _lanSession = null;
        _latestLanRemoteInput = null;
        _lanLocalInputFrames.Clear();
        _lanRemoteInputFrames.Clear();
        _lanSimulationSequence = 0;
        _lastLanRemoteDigest = null;
        _lanStatusLine = closeMessage;
        _lanRoomMembers.Clear();
        _lanRoomStatusText = closeMessage;
        /*
        _lanRoomStatusText = "连接已关闭。";
        */
        _lanLocalReady = false;
        _openGkStartHubOpen = true;
        _host.ClearLanPreparationSelectionFilter();
        _host.ClearNetworkDuelConfiguration();
        if (_lanSavedAiEnabled.HasValue)
        {
            _host.SetAiEnabled(_lanSavedAiEnabled.Value);
            _lanSavedAiEnabled = null;
        }
    }

    private void ApplyLanDuelConfigurationToHost()
    {
        if (_lanSession is null || !_host.IsDuelMode)
        {
            return;
        }

        ResolveLanDuelEntityKeys(out string redEntityKey, out string blueEntityKey);
        _host.ConfigureNetworkDuel(_lanLocalTeam, redEntityKey, blueEntityKey);
    }

    private void SelectLanRefereeInitialViewTarget()
    {
        if (!IsLanObserverClient)
        {
            return;
        }

        LanMatchSeatState? seat = _lanRosterBySeatId.Values
            .Where(seat => seat.Connected && string.Equals(seat.Role, "player", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(seat => seat.Ready)
            .ThenBy(seat => seat.Team, StringComparer.OrdinalIgnoreCase)
            .ThenBy(seat => ResolveLanSlotIndex(seat.Team, seat.EntityKey))
            .FirstOrDefault();
        string entityId = seat is null
            ? "red_robot_1"
            : $"{Simulator3dOptions.NormalizeTeam(seat.Team)}_{NormalizeLanDuelEntityKey(seat.EntityKey)}";
        _host.SetSelectedEntity(entityId);
        _followSelection = true;
        SnapCameraToSelectedEntity();
    }

    private void SelectLanLocalPlayerEntity()
    {
        if (_lanSession?.IsHost == true || IsLanObserverClient)
        {
            return;
        }

        string entityId = $"{Simulator3dOptions.NormalizeTeam(_lanLocalTeam)}_{NormalizeLanDuelEntityKey(_lanLocalEntityKey)}";
        _host.SetSelectedEntity(entityId);
        _followSelection = true;
        SnapCameraToSelectedEntity();
    }

    private void ResolveLanDuelEntityKeys(out string redEntityKey, out string blueEntityKey)
    {
        redEntityKey = string.Equals(_lanLocalTeam, "red", StringComparison.OrdinalIgnoreCase)
            ? _lanLocalEntityKey
            : _lanRemoteEntityKey;
        blueEntityKey = string.Equals(_lanLocalTeam, "blue", StringComparison.OrdinalIgnoreCase)
            ? _lanLocalEntityKey
            : _lanRemoteEntityKey;
        redEntityKey = NormalizeLanDuelEntityKey(redEntityKey);
        blueEntityKey = NormalizeLanDuelEntityKey(blueEntityKey);
    }

    private void SetLanLocalTeam(string team, bool broadcast)
    {
        _lanLocalTeam = Simulator3dOptions.NormalizeTeam(team);
        _lanRemoteTeam = OppositeLanTeam(_lanLocalTeam);
        _lanLocalReady = _lanSession?.IsHost == true;
        if (IsLanMultiplayerActive && _host.IsDuelMode)
        {
            ApplyLanDuelConfigurationToHost();
            ResetCameraForMap();
        }

        if (broadcast)
        {
            PublishLanLobbySelection();
        }
    }

    private void SetLanLocalEntityKey(string entityKey, bool broadcast, bool resetSpawnPoint = true)
    {
        string normalizedEntityKey = NormalizeLanDuelEntityKey(entityKey);
        if (!IsLanControllableRobotSeat(normalizedEntityKey))
        {
            _lanRoomStatusText = $"{ResolveLanEntityLabel(normalizedEntityKey)} 是占位席，暂不生成机器人。";
            return;
        }

        _lanLocalEntityKey = normalizedEntityKey;
        if (resetSpawnPoint)
        {
            _lanLocalSpawnPointIndex = ResolveLanDefaultSpawnPointIndex(_lanLocalEntityKey);
        }

        _lanLocalReady = _lanSession?.IsHost == true;
        if (!string.IsNullOrWhiteSpace(_lanLocalTeam))
        {
            _host.SetSelectedEntity($"{_lanLocalTeam}_{_lanLocalEntityKey}");
        }

        if (IsLanMultiplayerActive && _host.IsDuelMode)
        {
            ApplyLanDuelConfigurationToHost();
        }

        if (broadcast)
        {
            PublishLanLobbySelection();
        }
    }

    private void HandleLanLobbySelection(LanLobbySelection selection)
    {
        if (_lanSession is null)
        {
            return;
        }

        string memberRole = string.IsNullOrWhiteSpace(selection.MemberRole) ? "player" : selection.MemberRole.Trim().ToLowerInvariant();
        if (!string.Equals(memberRole, "player", StringComparison.OrdinalIgnoreCase))
        {
            RememberLanRoomMember(selection);
            return;
        }

        _lanRemotePlayerName = string.IsNullOrWhiteSpace(selection.PlayerName) ? _lanRemotePlayerName : selection.PlayerName.Trim();
        _lanRemoteEntityKey = NormalizeLanDuelEntityKey(selection.EntityKey);
        if (!IsLanControllableRobotSeat(_lanRemoteEntityKey))
        {
            RememberLanRoomMember(selection with
            {
                EntityKey = _lanRemoteEntityKey,
                SlotIndex = ResolveLanSlotIndex(selection.Team, _lanRemoteEntityKey),
                MemberRole = "placeholder",
            });
            return;
        }

        string remoteTeam = Simulator3dOptions.NormalizeTeam(selection.Team);
        bool shouldEchoSelection = false;
        if (string.Equals(remoteTeam, _lanLocalTeam, StringComparison.OrdinalIgnoreCase))
        {
            if (_lanSession.IsHost)
            {
                remoteTeam = OppositeLanTeam(_lanLocalTeam);
                shouldEchoSelection = true;
            }
            else
            {
                _lanLocalTeam = OppositeLanTeam(remoteTeam);
                shouldEchoSelection = true;
            }
        }

        _lanRemoteTeam = remoteTeam;
        if (_appState == SimulatorAppState.Lobby && _host.IsDuelMode)
        {
            ApplyLanDuelConfigurationToHost();
        }

        _lanStatusLine = $"{_lanRemotePlayerName} 选择 {ResolveLanTeamLabel(_lanRemoteTeam)} / {ResolveLanEntityLabel(_lanRemoteEntityKey)}";
        RememberLanRoomMember(selection);
        if (shouldEchoSelection)
        {
            PublishLanLobbySelection();
        }
    }

    private void PublishLanLobbySelection()
    {
        if (_lanSession is null || !_lanSession.IsConnected)
        {
            return;
        }

        _lanLobbySequence++;
        _ = _lanSession.SendLobbySelectionAsync(new LanLobbySelection(
            _lanLobbySequence,
            _lanPlayerNameText,
            _lanLocalTeam,
            _lanLocalEntityKey,
            ResolveLanSlotIndex(_lanLocalTeam, _lanLocalEntityKey),
            ResolveLanLocalMemberRole(),
            _lanLocalPlayerId,
            _lanLocalSpawnPointIndex,
            ResolveLanPreparationChassisMode()));
        if (_lanSession.IsHost)
        {
            if (string.Equals(ResolveLanLocalMemberRole(), "player", StringComparison.OrdinalIgnoreCase))
            {
                HandleLanSeatClaim(CreateLanSeatClaim(_lanLobbySequence));
            }
        }
        else
        {
            _ = _lanSession.SendSeatClaimAsync(CreateLanSeatClaim(_lanLobbySequence));
        }

        RememberLocalLanRoomMember();
        ApplyLanLocalPreparationPreviewToWorld();
    }

    private void ApplyLanLocalPreparationPreviewToWorld()
    {
        if (!IsLanMultiplayerMatchActive
            || _lanSession is null
            || !string.Equals(ResolveLanLocalMemberRole(), "player", StringComparison.OrdinalIgnoreCase)
            || !IsLanControllableRobotSeat(_lanLocalEntityKey))
        {
            return;
        }

        string seatId = ResolveLanSeatId(_lanLocalTeam, _lanLocalEntityKey);
        var seats = _lanRosterBySeatId.Values
            .Where(seat => !string.Equals(seat.SeatId, seatId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(seat.PlayerId, _lanLocalPlayerId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        seats.Add(new LanMatchSeatState(
            seatId,
            _lanLocalTeam,
            ResolveLanRobotNumber(_lanLocalEntityKey),
            _lanLocalEntityKey,
            _lanLocalPlayerId,
            string.IsNullOrWhiteSpace(_lanPlayerNameText) ? Environment.UserName : _lanPlayerNameText.Trim(),
            Connected: true,
            Ready: _lanLocalReady,
            Role: "player",
            SpawnPointIndex: _lanLocalSpawnPointIndex,
            ChassisMode: ResolveLanPreparationChassisMode()));
        _host.ApplyRoomGameSettings(_lanRoomSettings);
        _host.ApplyLanPreparationSelections(seats, hardTrimInactiveRobots: false);
        SelectLanLocalPlayerEntity();
        SnapCameraToSelectedEntity();
    }

    private LanSeatClaim CreateLanSeatClaim(long sequence)
    {
        string role = ResolveLanLocalMemberRole();
        string seatId = string.Equals(role, "player", StringComparison.OrdinalIgnoreCase)
            ? ResolveLanSeatId(_lanLocalTeam, _lanLocalEntityKey)
            : $"{role}_{_lanLocalPlayerId}";
        return new LanSeatClaim(
            sequence,
            _lanLocalPlayerId,
            string.IsNullOrWhiteSpace(_lanPlayerNameText) ? Environment.UserName : _lanPlayerNameText.Trim(),
            seatId,
            _lanLocalTeam,
            _lanLocalEntityKey,
            Ready: _lanLocalReady,
            role,
            _lanLocalSpawnPointIndex,
            ResolveLanPreparationChassisMode());
    }

    private string ResolveLanLocalMemberRole()
    {
        if (_lanSession?.IsHost == true && !IsLanDuelRoomMode)
        {
            return "referee";
        }

        string role = string.IsNullOrWhiteSpace(_lanLocalMemberRole) ? "player" : _lanLocalMemberRole.Trim().ToLowerInvariant();
        return role is "referee" or "spectator" ? role : "player";
    }

    private string ResolveLanPreparationChassisMode()
    {
        if (!string.Equals(_lanLocalEntityKey, "robot_3", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_lanLocalEntityKey, "robot_4", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return _host.InfantryMode;
    }

    private static int ResolveLanDefaultSpawnPointIndex(string entityKey)
        => Math.Max(0, LanRobotSeatCatalog.ResolveDefaultSpawnPointIndex(entityKey));

    private static string ResolveLanSeatId(string team, string entityKey)
    {
        string normalizedTeam = Simulator3dOptions.NormalizeTeam(team);
        int robotNumber = ResolveLanRobotNumber(entityKey);
        return $"{normalizedTeam}_{robotNumber}";
    }

    private static string ResolveLanEntityKeyFromSeatId(string seatId)
    {
        string normalized = (seatId ?? string.Empty).Trim().ToLowerInvariant();
        int separator = normalized.LastIndexOf('_');
        string robotNumberText = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        return robotNumberText switch
        {
            "1" => "robot_1",
            "2" => "robot_2",
            "4" => "robot_4",
            "6" => "robot_6",
            "7" => "robot_7",
            _ => "robot_3",
        };
    }

    private static int ResolveLanRobotNumber(string entityKey)
        => LanRobotSeatCatalog.ResolveRobotNumber(entityKey);

    private static bool IsLanControllableRobotSeat(string? entityKey)
        => LanRobotSeatCatalog.IsControllableRobot(entityKey);

    private bool TrySelectFirstAvailableLanTeamSeat(string team)
    {
        string normalizedTeam = Simulator3dOptions.NormalizeTeam(team);
        foreach (string entityKey in LanUcEntityKeys)
        {
            string seatId = ResolveLanSeatId(normalizedTeam, entityKey);
            if (!_lanRosterBySeatId.TryGetValue(seatId, out LanMatchSeatState? seat)
                || !seat.Connected
                || string.Equals(seat.PlayerId, _lanLocalPlayerId, StringComparison.OrdinalIgnoreCase))
            {
                SetLanLocalTeam(normalizedTeam, broadcast: false);
                SetLanLocalEntityKey(entityKey, broadcast: false);
                return true;
            }
        }

        _lanRoomStatusText = $"{ResolveLanTeamLabel(normalizedTeam)}暂无空位。";
        return false;
    }

    private bool CanAcceptLanRoleClaim(string playerId, string role, string seatId)
    {
        if (string.Equals(role, "player", StringComparison.OrdinalIgnoreCase))
        {
            string entityKey = ResolveLanEntityKeyFromSeatId(seatId);
            if (!IsLanControllableRobotSeat(entityKey))
            {
                _lanRoomStatusText = $"{ResolveLanEntityLabel(entityKey)} 是占位席，暂不生成机器人。";
                return false;
            }
        }

        if (string.Equals(role, "referee", StringComparison.OrdinalIgnoreCase))
        {
            if (IsLanDuelRoomMode)
            {
                _lanRoomStatusText = "1v1 房间不启用裁判席位。";
                return false;
            }

            int count = _lanRosterBySeatId.Values.Count(seat =>
                seat.Connected
                && string.Equals(seat.Role, "referee", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(seat.PlayerId, playerId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(seat.SeatId, seatId, StringComparison.OrdinalIgnoreCase));
            if (count >= LanMaxRefereeCount)
            {
                _lanRoomStatusText = $"裁判席位已满（{LanMaxRefereeCount}）。";
                return false;
            }
        }

        if (string.Equals(role, "spectator", StringComparison.OrdinalIgnoreCase))
        {
            int count = _lanRosterBySeatId.Values.Count(seat =>
                seat.Connected
                && string.Equals(seat.Role, "spectator", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(seat.PlayerId, playerId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(seat.SeatId, seatId, StringComparison.OrdinalIgnoreCase));
            if (count >= LanMaxSpectatorCount)
            {
                _lanRoomStatusText = $"观众席位已满（{LanMaxSpectatorCount}）。";
                return false;
            }
        }

        return true;
    }

    private void ApplyLanRoomSettingDelta(string key, int delta)
    {
        if (!CanEditLanRoomSettings())
        {
            return;
        }

        _lanRoomSettings = key switch
        {
            "small_damage" => _lanRoomSettings with { SmallBulletDamage = Math.Clamp(_lanRoomSettings.SmallBulletDamage + delta, 1, 999) },
            "large_damage" => _lanRoomSettings with { LargeBulletDamage = Math.Clamp(_lanRoomSettings.LargeBulletDamage + delta, 1, 999) },
            "hero_level" => _lanRoomSettings with { HeroStartLevel = Math.Clamp(_lanRoomSettings.HeroStartLevel + delta, 1, 10) },
            "engineer_level" => _lanRoomSettings with { EngineerStartLevel = Math.Clamp(_lanRoomSettings.EngineerStartLevel + delta, 1, 10) },
            "infantry_level" => _lanRoomSettings with { InfantryStartLevel = Math.Clamp(_lanRoomSettings.InfantryStartLevel + delta, 1, 10) },
            "red_gold" => _lanRoomSettings with { RedStartGold = Math.Clamp(_lanRoomSettings.RedStartGold + delta * 50, 0, 5000) },
            "blue_gold" => _lanRoomSettings with { BlueStartGold = Math.Clamp(_lanRoomSettings.BlueStartGold + delta * 50, 0, 5000) },
            _ => _lanRoomSettings,
        };

        if (_lanSession?.IsHost == true)
        {
            _lanLobbySequence++;
            _ = _lanSession.SendRosterAsync(CreateLanRoomRoster(_lanLobbySequence));
        }
        else if (_localRoomPanelOpen && _lanSession is null)
        {
            ApplyLocalRoomSelectionsToWorld();
        }
    }

    private static int ResolveLanSlotIndex(string team, string entityKey)
    {
        int teamOffset = string.Equals(Simulator3dOptions.NormalizeTeam(team), "blue", StringComparison.OrdinalIgnoreCase)
            ? LanUcRoomSeatEntityKeys.Length
            : 0;
        int roleOffset = LanRobotSeatCatalog.ResolveRoomSlotIndex(entityKey);
        return teamOffset + roleOffset;
    }

    private static string OppositeLanTeam(string team)
        => string.Equals(Simulator3dOptions.NormalizeTeam(team), "blue", StringComparison.OrdinalIgnoreCase) ? "red" : "blue";

    private static string NormalizeLanDuelEntityKey(string? entityKey)
        => LanRobotSeatCatalog.NormalizeEntityKey(entityKey, allowPlaceholder: true);

    private static string ResolveLanDuelRoleKey(string entityKey)
        => NormalizeLanDuelEntityKey(entityKey) switch
        {
            /*
            "robot_6" => "云台手",
            */
            "robot_1" => "hero",
            "robot_2" => "engineer",
            "robot_6" => "gimbal",
            "robot_7" => "sentry",
            _ => "infantry",
        };

    private static string ResolveLanRoleEntityKey(string roleKey)
        => roleKey switch
        {
            "hero" => "robot_1",
            "engineer" => "robot_2",
            "gimbal" or "gunner" or "operator" => "robot_6",
            "sentry" => "robot_7",
            _ => "robot_3",
        };

    private static string ResolveLanTeamLabel(string team)
        => string.Equals(Simulator3dOptions.NormalizeTeam(team), "blue", StringComparison.OrdinalIgnoreCase) ? "蓝方" : "红方";

    private static string ResolveLanEntityLabel(string entityKey)
        => NormalizeLanDuelEntityKey(entityKey) switch
        {
            "robot_6" => "\u4e91\u53f0\u624b",
            "robot_1" => "英雄",
            "robot_2" => "工程",
            "robot_4" => "步兵2",
            "robot_7" => "哨兵",
            _ => "步兵1",
        };

    private static double NormalizeSignedLanDeg(double degrees)
    {
        double value = degrees % 360.0;
        if (value <= -180.0)
        {
            value += 360.0;
        }
        else if (value > 180.0)
        {
            value -= 360.0;
        }

        return value;
    }

    private void DrawLanRoomPanel(Graphics graphics, Rectangle mainPanel, Color accentColor, float reveal)
    {
        if (UseOpenGkMenuChrome())
        {
            DrawOpenGkLanRoomPanel(graphics, mainPanel, accentColor, reveal);
            return;
        }

        if (!_lanRoomPanelOpen && _lanSession is null)
        {
            return;
        }

        reveal = Math.Clamp(reveal <= 0.01f ? 1f : reveal, 0f, 1f);
        int gap = 22;
        int preferredWidth = Math.Clamp(ClientSize.Width / 3, 430, 520);
        int availableLeft = mainPanel.Left - gap - Math.Clamp(ClientSize.Width / 28, 24, 42);
        int panelWidth = Math.Min(preferredWidth, Math.Max(390, availableLeft));
        int panelX = mainPanel.Left - gap - panelWidth;
        if (panelX < 18)
        {
            panelWidth = Math.Min(520, Math.Max(390, ClientSize.Width - 36));
            panelX = Math.Max(18, (ClientSize.Width - panelWidth) / 2);
        }

        int panelHeight = Math.Min(560, Math.Max(492, mainPanel.Height - 12));
        int panelY = Math.Clamp(mainPanel.Top + 6, 18, Math.Max(18, ClientSize.Height - panelHeight - 18));
        Rectangle panel = new(panelX, panelY, panelWidth, panelHeight);

        using GraphicsPath shadowPath = CreateRoundedRectangle(new Rectangle(panel.X + 8, panel.Y + 12, panel.Width, panel.Height), 22);
        using var shadowBrush = new SolidBrush(Color.FromArgb((int)(52 * reveal), 0, 0, 0));
        graphics.FillPath(shadowBrush, shadowPath);

        using GraphicsPath panelPath = CreateRoundedRectangle(panel, 18);
        using var panelFill = new LinearGradientBrush(
            new Point(panel.Left, panel.Top),
            new Point(panel.Right, panel.Bottom),
            ApplyUiAlpha(Color.FromArgb(214, 8, 14, 24), reveal),
            ApplyUiAlpha(Color.FromArgb(198, 18, 28, 42), reveal));
        using var panelBorder = new Pen(ApplyUiAlpha(Color.FromArgb(158, 166, 214, 255), reveal), 1.1f);
        graphics.FillPath(panelFill, panelPath);
        graphics.DrawPath(panelBorder, panelPath);

        using var accentBrush = new SolidBrush(ApplyUiAlpha(accentColor, 0.86f * reveal));
        graphics.FillRectangle(accentBrush, panel.X + 24, panel.Y + 18, 104, 4);

        using var titleBrush = new SolidBrush(ApplyUiAlpha(Color.FromArgb(244, 247, 250), reveal));
        using var textBrush = new SolidBrush(ApplyUiAlpha(Color.FromArgb(206, 220, 232), reveal));
        using var hintBrush = new SolidBrush(ApplyUiAlpha(Color.FromArgb(166, 184, 198, 214), reveal));
        graphics.DrawString("局域网 5v5 房间", _menuTitleFont, titleBrush, panel.X + 24, panel.Y + 34);
        graphics.DrawString("裁判端作为主机广播开局和快照；玩家加入后选择阵营与车体。", _tinyHudFont, hintBrush, panel.X + 26, panel.Y + 70);

        int contentX = panel.X + 26;
        int contentY = panel.Y + 104;
        int contentWidth = panel.Width - 52;
        int rowHeight = 34;
        DrawLanModeButtons(graphics, contentX, contentY, contentWidth);
        contentY += 48;

        DrawLanTextField(graphics, contentX, contentY, contentWidth, "房间名", "room", _lanRoomNameText, "ARTINX 5v5 房间", enabled: _lanSession is null);
        contentY += rowHeight + 10;
        DrawLanTextField(graphics, contentX, contentY, contentWidth, "玩家名", "player", _lanPlayerNameText, Environment.UserName, enabled: _lanSession is null);
        contentY += rowHeight + 10;
        DrawLanTextField(graphics, contentX, contentY, contentWidth, "主机地址", "host", _lanHostAddressText, "192.168.1.10", enabled: !_lanRoomHostMode && _lanSession is null);
        contentY += rowHeight + 10;
        DrawLanTextField(graphics, contentX, contentY, contentWidth, "端口", "port", _lanPortText, "26011", enabled: _lanSession is null);
        contentY += rowHeight + 16;

        Rectangle statusRect = new(contentX, contentY, contentWidth, 82);
        DrawLanStatusBox(graphics, statusRect);
        contentY += statusRect.Height + 16;

        int buttonGap = 10;
        int buttonWidth = (contentWidth - buttonGap * 2) / 3;
        Rectangle createJoin = new(contentX, contentY, buttonWidth, 38);
        Rectangle enter = new(contentX + buttonWidth + buttonGap, contentY, buttonWidth, 38);
        Rectangle cancel = new(contentX + (buttonWidth + buttonGap) * 2, contentY, buttonWidth, 38);
        string createLabel = _lanSession is not null ? "已连接" : (_lanRoomHostMode ? "创建房间" : "加入房间");
        string connectAction = _lanSession is null && !_lanRoomBusy ? "lan_room_connect" : string.Empty;
        DrawButton(graphics, createJoin, _lanRoomBusy ? "连接中" : createLabel, connectAction, _lanSession is not null, Color.FromArgb(64, 132, 206));
        DrawButton(graphics, enter, "进入大厅", _lanSession is null ? string.Empty : "lan_room_enter", _lanSession is not null, Color.FromArgb(72, 146, 226));
        DrawButton(graphics, cancel, "关闭", "lan_room_close", false, Color.FromArgb(86, 98, 112));

        if (_lanSession is not null)
        {
            string roleText = _lanSession.IsHost
                ? $"裁判 Host / 已连接 {_lanSession.ConnectedPeerCount}/{_lanSession.MaxPlayerClients}"
                : "玩家 Guest / 蓝方";
            graphics.DrawString($"当前身份  {roleText}", _tinyHudFont, textBrush, contentX, panel.Bottom - 30);
        }
    }

    private void DrawLanModeButtons(Graphics graphics, int x, int y, int width)
    {
        int gap = 10;
        int buttonWidth = (width - gap) / 2;
        DrawButton(graphics, new Rectangle(x, y, buttonWidth, 34), "裁判主机", "lan_role:host", _lanRoomHostMode, Color.FromArgb(174, 66, 66));
        DrawButton(graphics, new Rectangle(x + buttonWidth + gap, y, buttonWidth, 34), "玩家加入", "lan_role:guest", !_lanRoomHostMode, Color.FromArgb(64, 112, 200));
    }

    private void DrawLanTextField(Graphics graphics, int x, int y, int width, string label, string field, string value, string placeholder, bool enabled)
    {
        using var labelBrush = new SolidBrush(Color.FromArgb(enabled ? 214 : 132, 222, 230));
        graphics.DrawString(label, _tinyHudFont, labelBrush, x, y + 8);

        Rectangle inputRect = new(x + 96, y, width - 96, 32);
        bool focused = string.Equals(_lanFocusedField, field, StringComparison.OrdinalIgnoreCase);
        float hoverMix = ResolveUiHoverMix($"lan_field:{field}");
        Color baseFill = enabled
            ? Color.FromArgb(152, 34, 44, 60)
            : Color.FromArgb(92, 34, 40, 50);
        Color fillColor = focused
            ? Color.FromArgb(216, 48, 78, 116)
            : BlendUiColor(baseFill, Color.FromArgb(178, 52, 72, 100), hoverMix * 0.5f);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(
            focused
                ? Color.FromArgb(224, 126, 194, 255)
                : BlendUiColor(Color.FromArgb(130, 148, 166, 186), Color.FromArgb(202, 196, 224, 248), hoverMix * 0.45f),
            focused ? 1.45f : 1.0f);
        using GraphicsPath path = CreateRoundedRectangle(inputRect, 8);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string display = string.IsNullOrEmpty(value) ? placeholder : value;
        Color textColor = string.IsNullOrEmpty(value)
            ? Color.FromArgb(enabled ? 152 : 110, 190, 202, 218)
            : Color.FromArgb(enabled ? 242 : 154, 244, 248, 252);
        DrawUiButtonText(graphics, Rectangle.Inflate(inputRect, -10, -1), display, _smallHudFont, textColor);
        if (enabled)
        {
            _uiButtons.Add(new UiButton(inputRect, $"lan_field:{field}"));
        }
    }

    private void DrawLanStatusBox(Graphics graphics, Rectangle rect)
    {
        using var fill = new SolidBrush(Color.FromArgb(128, 18, 28, 42));
        using var border = new Pen(Color.FromArgb(126, 134, 156, 184), 1f);
        graphics.FillRectangle(fill, rect);
        graphics.DrawRectangle(border, rect);

        string status = _lanSession?.StatusText ?? _lanRoomStatusText;
        if (!string.IsNullOrWhiteSpace(_lanStatusLine))
        {
            status = _lanStatusLine;
        }

        using var statusBrush = new SolidBrush(Color.FromArgb(224, 198, 220, 238));
        graphics.DrawString(status, _tinyHudFont, statusBrush, new RectangleF(rect.X + 10, rect.Y + 8, rect.Width - 20, rect.Height - 16));
    }

    private bool TryExecuteLanRoomAction(string action)
    {
        if (!action.StartsWith("lan_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (action.StartsWith("lan_field:", StringComparison.OrdinalIgnoreCase))
        {
            _lanFocusedField = action.Split(':', 2)[1];
            return true;
        }

        if (action.StartsWith("lan_role:", StringComparison.OrdinalIgnoreCase))
        {
            if (_lanSession is null)
            {
                _lanRoomHostMode = string.Equals(action.Split(':', 2)[1], "host", StringComparison.OrdinalIgnoreCase);
                _lanFocusedField = null;
            }

            return true;
        }

        if (action.StartsWith("lan_discovered_room:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(action.Split(':', 2)[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                && index >= 0
                && index < _lanDiscoveredRooms.Count)
            {
                LanDiscoveredRoom room = _lanDiscoveredRooms[index];
                _openGkSelectedDiscoveredRoomIndex = index;
                _lanRoomHostMode = false;
                _lanRoomNameText = room.RoomName;
                _lanHostAddressText = room.HostAddress;
                _lanPortText = room.Port.ToString(CultureInfo.InvariantCulture);
                _lanRoomStatusText = $"已选择房间 {room.RoomName}，点击加入房间。";
                _lanFocusedField = null;
            }

            return true;
        }

        if (action.StartsWith("lan_select_seat:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = action.Split(':', 3);
            if (parts.Length == 3 && _lanSession?.IsHost != true)
            {
                SetLanLocalTeam(parts[1], broadcast: false);
                SetLanLocalEntityKey(parts[2], broadcast: false, resetSpawnPoint: false);
                _lanLocalMemberRole = "player";
                _lanLocalReady = false;
                PublishLanLobbySelection();
                _lanRoomStatusText = "已切换席位，请确认后准备。";
            }

            return true;
        }

        if (action.StartsWith("lan_join_team:", StringComparison.OrdinalIgnoreCase))
        {
            string team = Simulator3dOptions.NormalizeTeam(action.Split(':', 2)[1]);
            if (_lanSession?.IsHost != true && TrySelectFirstAvailableLanTeamSeat(team))
            {
                _lanLocalMemberRole = "player";
                _lanLocalReady = false;
                PublishLanLobbySelection();
                _lanRoomStatusText = $"已加入{ResolveLanTeamLabel(team)}，请选择机器人并准备。";
            }

            return true;
        }

        if (action.StartsWith("lan_role_select:", StringComparison.OrdinalIgnoreCase))
        {
            string role = action.Split(':', 2)[1].Trim().ToLowerInvariant();
            if (_lanSession?.IsHost != true && role is "player" or "referee" or "spectator")
            {
                _lanLocalMemberRole = role;
                _lanLocalReady = false;
                PublishLanLobbySelection();
                _lanRoomStatusText = role switch
                {
                    "referee" => "已切换为裁判席位，裁判不接入机器人。",
                    "spectator" => "已切换为观众席位。",
                    _ => "已切换为玩家席位，请选择队伍与机器人。",
                };
            }

            return true;
        }

        if (action.StartsWith("lan_room_setting:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = action.Split(':', 3);
            if (parts.Length == 3
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
            {
                ApplyLanRoomSettingDelta(parts[1], delta);
            }

            return true;
        }

        switch (action)
        {
            case "lan_room_connect":
                BeginLanRoomConnect();
                return true;
            case "lan_discovery_refresh":
                _lanRoomDiscovery ??= new LanRoomDiscoveryService();
                _lanDiscoveredRooms = _lanRoomDiscovery.GetRooms();
                _lanRoomStatusText = _lanRoomDiscovery.IsAvailable
                    ? "正在刷新局域网房间列表。"
                    : "房间发现端口不可用，请手动输入主机 IP。";
                return true;
            case "lan_room_enter":
                EnterLanMultiplayerLobby();
                return true;
            case "lan_room_start_match":
                StartLanRoomMatchFromRoom();
                return true;
            case "lan_toggle_ready":
                if (_lanSession is not null && !_lanSession.IsHost)
                {
                    _lanLocalReady = !_lanLocalReady;
                    PublishLanLobbySelection();
                }

                return true;
            case "lan_room_close":
                _lanRoomPanelOpen = false;
                _lanFocusedField = null;
                return true;
            case "lan_room_disconnect":
                CloseLanSession();
                _lanRoomPanelOpen = false;
                _lanFocusedField = null;
                return true;
            case "lan_toggle_private":
                if (_lanSession is null)
                {
                    _lanRoomPrivate = !_lanRoomPrivate;
                }

                return true;
            default:
                return true;
        }
    }

    private bool HandleLanRoomInputKey(KeyEventArgs eventArgs)
    {
        if (!_lanRoomPanelOpen)
        {
            return false;
        }

        if (eventArgs.KeyCode == Keys.Escape)
        {
            _lanRoomStatusText = "联机模式已禁用 Esc，请使用界面按钮退出房间。";
            return true;
        }

        if (eventArgs.KeyCode == Keys.Enter)
        {
            if (!string.IsNullOrWhiteSpace(_lanFocusedField))
            {
                _lanFocusedField = null;
            }
            else if (_lanSession is null)
            {
                BeginLanRoomConnect();
            }
            else
            {
                EnterLanMultiplayerLobby();
            }

            return true;
        }

        if (eventArgs.KeyCode == Keys.Tab)
        {
            CycleLanFocusedField(eventArgs.Shift ? -1 : 1);
            return true;
        }

        if (string.IsNullOrWhiteSpace(_lanFocusedField))
        {
            return false;
        }

        if (eventArgs.Control && eventArgs.KeyCode == Keys.V)
        {
            ApplyLanFieldText(Clipboard.GetText(TextDataFormat.Text));
            return true;
        }

        if (eventArgs.KeyCode == Keys.Back)
        {
            BackspaceLanField();
            return true;
        }

        if (eventArgs.KeyCode == Keys.Delete)
        {
            SetLanFieldText(string.Empty);
            return true;
        }

        char? ch = ConvertLanKeyToChar(eventArgs);
        if (ch.HasValue)
        {
            ApplyLanFieldText(ch.Value.ToString());
            return true;
        }

        return false;
    }

    private async void BeginLanRoomConnect()
    {
        if (_lanRoomBusy)
        {
            return;
        }

        int port = ResolveLanRoomPort();
        var session = new LanMultiplayerSession(
            _lanRoomHostMode ? LanPeerRole.Host : LanPeerRole.Guest,
            _lanRoomNameText,
            _lanPlayerNameText);
        _lanRoomBusy = true;
        _lanRoomStatusText = _lanRoomHostMode ? "创建房间中..." : "连接主机中...";
        Invalidate();

        try
        {
            if (_lanRoomHostMode)
            {
                await session.StartHostAsync(port);
            }
            else
            {
                await session.JoinAsync(_lanHostAddressText.Trim(), port);
            }

            ResetLanSession(session);
        }
        catch (Exception exception) when (exception is SocketException or IOException or ArgumentException)
        {
            session.Dispose();
            _lanRoomStatusText = (_lanRoomHostMode ? "创建房间失败：" : "加入房间失败：") + exception.Message;
        }
        finally
        {
            _lanRoomBusy = false;
            Invalidate();
        }
    }

    private int ResolveLanRoomPort()
    {
        if (!int.TryParse(_lanPortText.Trim(), out int port))
        {
            port = 26011;
            _lanPortText = port.ToString(CultureInfo.InvariantCulture);
        }

        port = Math.Clamp(port, 1024, 65535);
        _lanPortText = port.ToString(CultureInfo.InvariantCulture);
        return port;
    }

    private void CycleLanFocusedField(int direction)
    {
        string[] fields = _lanRoomHostMode
            ? new[] { "room", "player", "port" }
            : new[] { "room", "player", "host", "port" };
        int current = Array.FindIndex(fields, field => string.Equals(field, _lanFocusedField, StringComparison.OrdinalIgnoreCase));
        int next = current < 0
            ? 0
            : (current + direction + fields.Length) % fields.Length;
        _lanFocusedField = fields[next];
    }

    private void ApplyLanFieldText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string current = GetLanFieldText();
        int maxLength = ResolveLanFieldMaxLength();
        foreach (char ch in text)
        {
            if (!IsLanFieldCharAllowed(ch) || current.Length >= maxLength)
            {
                continue;
            }

            current += ch;
        }

        SetLanFieldText(current);
    }

    private void BackspaceLanField()
    {
        string current = GetLanFieldText();
        if (current.Length > 0)
        {
            SetLanFieldText(current[..^1]);
        }
    }

    private string GetLanFieldText()
        => _lanFocusedField switch
        {
            "room" => _lanRoomNameText,
            "player" => _lanPlayerNameText,
            "host" => _lanHostAddressText,
            "port" => _lanPortText,
            _ => string.Empty,
        };

    private void SetLanFieldText(string value)
    {
        switch (_lanFocusedField)
        {
            case "room":
                _lanRoomNameText = value;
                break;
            case "player":
                _lanPlayerNameText = value;
                break;
            case "host":
                _lanHostAddressText = value;
                break;
            case "port":
                _lanPortText = new string(value.Where(char.IsDigit).Take(5).ToArray());
                break;
        }
    }

    private int ResolveLanFieldMaxLength()
        => _lanFocusedField switch
        {
            "room" => 32,
            "player" => 24,
            "host" => 64,
            "port" => 5,
            _ => 0,
        };

    private bool IsLanFieldCharAllowed(char ch)
    {
        if (_lanFocusedField == "port")
        {
            return char.IsDigit(ch);
        }

        if (_lanFocusedField == "host")
        {
            return char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':';
        }

        return !char.IsControl(ch);
    }

    private static char? ConvertLanKeyToChar(KeyEventArgs eventArgs)
    {
        Keys key = eventArgs.KeyCode;
        if (key is >= Keys.A and <= Keys.Z)
        {
            char ch = (char)('a' + key - Keys.A);
            return eventArgs.Shift ? char.ToUpperInvariant(ch) : ch;
        }

        if (key is >= Keys.D0 and <= Keys.D9)
        {
            return (char)('0' + key - Keys.D0);
        }

        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            return (char)('0' + key - Keys.NumPad0);
        }

        return key switch
        {
            Keys.Space => ' ',
            Keys.OemPeriod or Keys.Decimal => '.',
            Keys.OemMinus or Keys.Subtract => '-',
            Keys.OemQuestion => '/',
            Keys.OemSemicolon => ':',
            _ => null,
        };
    }

    private void PublishLanInput(PlayerControlState state)
    {
        if (!IsLanMultiplayerMatchActive
            || _lanSession is null
            || !_lanSession.IsConnected
            || _lanSession.IsHost
            || !string.Equals(ResolveLanLocalMemberRole(), "player", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsMatchStartupControlLockActive)
        {
            if (Math.Abs(state.TurretYawDeltaDeg) <= 1e-6
                && Math.Abs(state.GimbalPitchDeltaDeg) <= 1e-6)
            {
                return;
            }

            PlayerControlState startupState = SanitizeStartupLockedPlayerControl(state);
            _lanInputSequence++;
            LanInputFrame startupInputFrame = ToLanInputFrame(startupState, _lanInputSequence);
            _lanInputFramesSent++;
            _lastLanLocalInputSentAtSec = _host.World.GameTimeSec;
            AppendLanTrafficLog(true, $"startup_aim seq={_lanInputSequence} seat={ResolveLanSeatId(_lanLocalTeam, _lanLocalEntityKey)} entity={startupState.EntityId ?? startupInputFrame.EntityId}");
            var startupPlayerInput = new LanPlayerInputFrame(
                _lanInputSequence,
                _lanLocalPlayerId,
                ResolveLanSeatId(_lanLocalTeam, _lanLocalEntityKey),
                startupState.EntityId ?? startupInputFrame.EntityId,
                _lanInputSequence,
                _host.World.GameTimeSec,
                startupInputFrame);
            AppendLanTrafficDetailLog(true, FormatLanPlayerInputDetail("tx_startup", startupPlayerInput));
            _ = _lanSession.SendPlayerInputAsync(startupPlayerInput);
            return;
        }

        _lanInputSequence = _lanSimulationSequence + 1;
        if (!_lanLocalInputFrames.TryGetValue(_lanInputSequence, out PlayerControlState? bufferedState)
            || bufferedState is null)
        {
            bufferedState = state;
            _lanLocalInputFrames[_lanInputSequence] = bufferedState;
        }

        LanInputFrame inputFrame = ToLanInputFrame(bufferedState, _lanInputSequence);
        _lanInputFramesSent++;
        _lastLanLocalInputSentAtSec = _host.World.GameTimeSec;
        AppendLanTrafficLog(true, $"player_input seq={_lanInputSequence} seat={ResolveLanSeatId(_lanLocalTeam, _lanLocalEntityKey)} entity={bufferedState.EntityId ?? inputFrame.EntityId}");
        var playerInput = new LanPlayerInputFrame(
            _lanInputSequence,
            _lanLocalPlayerId,
            ResolveLanSeatId(_lanLocalTeam, _lanLocalEntityKey),
            bufferedState.EntityId ?? inputFrame.EntityId,
            _lanInputSequence,
            _host.World.GameTimeSec,
            inputFrame);
        AppendLanTrafficDetailLog(true, FormatLanPlayerInputDetail("tx", playerInput));
        _ = _lanSession.SendPlayerInputAsync(playerInput);
    }

    private bool TryBuildLanStepStates(PlayerControlState localState, out IReadOnlyList<PlayerControlState> states, out bool waitingForRemoteInput)
    {
        states = Array.Empty<PlayerControlState>();
        waitingForRemoteInput = false;
        if (!IsLanMultiplayerMatchActive || _lanSession is null)
        {
            return false;
        }

        if (!_lanSession.IsHost)
        {
            long localSequence = _lanSimulationSequence + 1;
            PlayerControlState stepLocalState = _lanLocalInputFrames.TryGetValue(localSequence, out PlayerControlState? bufferedLocal)
                && bufferedLocal is not null
                ? bufferedLocal
                : localState;
            _lanLocalInputFrames.Remove(localSequence);
            PruneLanLocalInputFrames(localSequence);
            _lanSimulationSequence = localSequence;
            states = new[] { stepLocalState };
            return true;
        }

        long expectedSequence = _lanSimulationSequence + 1;
        if (!_lanRemoteInputFrames.TryGetValue(expectedSequence, out PlayerControlState? remoteState)
            || remoteState is null)
        {
            if (!IsLanDuelRoomMode)
            {
                _lanSimulationSequence = expectedSequence;
                states = _latestLanRemoteInput is not null
                    ? new[] { _latestLanRemoteInput }
                    : Array.Empty<PlayerControlState>();
                return true;
            }

            waitingForRemoteInput = true;
            _lanStatusLine = $"等待远端输入 step {expectedSequence}";
            return false;
        }

        _lanRemoteInputFrames.Remove(expectedSequence);
        PruneLanRemoteInputFrames(expectedSequence);
        _lanSimulationSequence = expectedSequence;
        _latestLanRemoteInput = remoteState;
        _lanLocalInputFrames.Remove(expectedSequence);
        PruneLanLocalInputFrames(expectedSequence);
        states = new[] { remoteState };
        return true;
    }

    private void PruneLanLocalInputFrames(long completedSequence)
    {
        foreach (long sequence in _lanLocalInputFrames.Keys.Where(sequence => sequence <= completedSequence).ToArray())
        {
            _lanLocalInputFrames.Remove(sequence);
        }

        while (_lanLocalInputFrames.Count > LanRemoteInputBufferLimit)
        {
            _lanLocalInputFrames.Remove(_lanLocalInputFrames.Keys.First());
        }
    }

    private void AdvanceLanClientSimulationSequence()
    {
        long completedSequence = _lanSimulationSequence + 1;
        _lanSimulationSequence = completedSequence;
        _lanLocalInputFrames.Remove(completedSequence);
        PruneLanLocalInputFrames(completedSequence);
    }

    private void PruneLanRemoteInputFrames(long completedSequence)
    {
        foreach (long sequence in _lanRemoteInputFrames.Keys.Where(sequence => sequence <= completedSequence).ToArray())
        {
            _lanRemoteInputFrames.Remove(sequence);
        }

        while (_lanRemoteInputFrames.Count > LanRemoteInputBufferLimit)
        {
            _lanRemoteInputFrames.Remove(_lanRemoteInputFrames.Keys.First());
        }
    }

    private void PumpLanMultiplayerMessages()
    {
        PumpLanRoomDiscovery();
        if (_lanSession is null)
        {
            return;
        }

        _lastLanTrafficSnapshot = _lanSession.CaptureTrafficSnapshot();
        PublishLanTrafficReportIfDue();
        bool receivedEvent = false;
        IReadOnlyList<LanSessionEvent> drainedEvents = _lanSession.DrainEvents();
        LanSessionEvent? latestAuthoritativeSnapshot = null;
        LanSessionEvent? latestClientSnapshot = null;
        int skippedAuthoritativeSnapshots = 0;
        int skippedClientSnapshots = 0;
        foreach (LanSessionEvent item in drainedEvents)
        {
            receivedEvent = true;
            if (string.Equals(item.Type, LanProtocolMessageTypes.AuthoritativeSnapshot, StringComparison.OrdinalIgnoreCase))
            {
                if (latestAuthoritativeSnapshot is not null)
                {
                    skippedAuthoritativeSnapshots++;
                }

                latestAuthoritativeSnapshot = item;
                continue;
            }

            if (string.Equals(item.Type, LanProtocolMessageTypes.Snapshot, StringComparison.OrdinalIgnoreCase))
            {
                if (latestClientSnapshot is not null)
                {
                    skippedClientSnapshots++;
                }

                latestClientSnapshot = item;
                continue;
            }

            if (ShouldAppendLanCompactEventLog(item.Type))
            {
                AppendLanTrafficLog(false, $"event type={item.Type}");
            }

            DispatchLanSessionEvent(item);
        }

        if (latestClientSnapshot is not null)
        {
            DispatchLanSessionEvent(latestClientSnapshot);
        }

        if (latestAuthoritativeSnapshot is not null)
        {
            DispatchLanSessionEvent(latestAuthoritativeSnapshot);
        }

        int skippedRealtimeSnapshots = skippedAuthoritativeSnapshots + skippedClientSnapshots;
        if (skippedRealtimeSnapshots > 0)
        {
            _lanCoalescedRealtimeEvents += skippedRealtimeSnapshots;
            double nowSec = _frameClock.Elapsed.TotalSeconds;
            if (nowSec - _lastLanCoalescedRealtimeLogSec >= 1.0)
            {
                _lastLanCoalescedRealtimeLogSec = nowSec;
                AppendLanTrafficLog(
                    false,
                    $"coalesced_realtime skipped={_lanCoalescedRealtimeEvents} auth={skippedAuthoritativeSnapshots} snapshot={skippedClientSnapshots}");
                _lanCoalescedRealtimeEvents = 0;
            }
        }

        if (receivedEvent)
        {
            InvalidateGpuOverlayLayer();
        }
    }

    private void DispatchLanSessionEvent(LanSessionEvent item)
    {
        if (DispatchLanSessionStatusEvent(item))
        {
            return;
        }

        if (DispatchLanSessionControlEvent(item))
        {
            return;
        }

        if (DispatchLanSessionMatchSyncEvent(item))
        {
            return;
        }

        if (DispatchLanSessionRefereeEvent(item))
        {
            return;
        }

        if (DispatchLanSessionTrafficEvent(item))
        {
            return;
        }
    }

    private bool DispatchLanSessionTrafficEvent(LanSessionEvent item)
    {
        if (!string.Equals(item.Type, LanProtocolMessageTypes.TrafficReport, StringComparison.OrdinalIgnoreCase)
            || item.TrafficReport is null)
        {
            return false;
        }

        _lastLanRemoteTrafficReport = item.TrafficReport;
        _lastLanTrafficReportReceivedAtSec = _frameClock.Elapsed.TotalSeconds;
        AppendLanTrafficLog(false, FormatLanTrafficReportLog(item.TrafficReport));
        return true;
    }

    private void PumpLanRoomDiscovery()
    {
        bool shouldRunDiscovery = _appState == SimulatorAppState.MainMenu
            && (_openGkStartHubOpen || _lanRoomPanelOpen || _lanSession is not null);
        if (!shouldRunDiscovery)
        {
            return;
        }

        _lanRoomDiscovery ??= new LanRoomDiscoveryService();
        List<LanDiscoveredRoom> mergedRooms = _lanRoomDiscovery.IsAvailable
            ? _lanRoomDiscovery.GetRooms().ToList()
            : new List<LanDiscoveredRoom>();
        if (_lanSession?.IsHost == true && !_lanRoomPrivate)
        {
            string localAddress = LanMultiplayerSession.ResolvePreferredLocalAddress();
            int hostPort = _lanSession.Port;
            string localKey = $"{localAddress}:{hostPort}";
            int connectedCount = Math.Max(_lanSession.ConnectedPeerCount + 1, EnumerateLanRoomMembers().Count());
            mergedRooms.RemoveAll(room => string.Equals($"{room.HostAddress}:{room.Port}", localKey, StringComparison.OrdinalIgnoreCase));
            mergedRooms.Insert(0, new LanDiscoveredRoom(
                string.IsNullOrWhiteSpace(_lanSession.RoomName) ? "ARTINX LAN Room" : _lanSession.RoomName,
                string.IsNullOrWhiteSpace(_lanPlayerNameText) ? Environment.MachineName : _lanPlayerNameText.Trim(),
                localAddress,
                hostPort,
                connectedCount,
                _lanSession.MaxPlayerClients,
                Private: false,
                DateTime.UtcNow));
        }

        if (_lanRoomDiscovery.IsAvailable)
        {
            _lanDiscoveredRooms = mergedRooms;
        }

        if (_lanSession?.IsHost == true
            && !_lanRoomPrivate
            && _lanRoomDiscovery.IsAvailable)
        {
            double nowSec = _frameClock.Elapsed.TotalSeconds;
            if (nowSec - _lastLanRoomAnnouncementAtSec >= 1.0)
            {
                _lastLanRoomAnnouncementAtSec = nowSec;
                _lanRoomDiscovery.AnnounceRoom(
                    _lanSession.RoomName,
                    string.IsNullOrWhiteSpace(_lanPlayerNameText) ? Environment.MachineName : _lanPlayerNameText,
                    LanMultiplayerSession.ResolvePreferredLocalAddress(),
                    _lanSession.Port,
                    _lanSession.ConnectedPeerCount,
                    _lanSession.MaxPlayerClients,
                    _lanRoomPrivate);
            }
        }
    }

    private bool DispatchLanSessionStatusEvent(LanSessionEvent item)
    {
        switch (item.Type)
        {
            case "host_disconnected":
                CloseLanSession("\u670D\u52A1\u5668\u5DF2\u5173\u95ED\uFF0C\u5DF2\u8FD4\u56DE\u5927\u5385\u3002");
                if (_appState == SimulatorAppState.InMatch)
                {
                    ReturnToLobby();
                }
                else
                {
                    _appState = SimulatorAppState.Lobby;
                    _paused = true;
                    ReleaseMouseCapture();
                    ResetLiveInput();
                }

                _lanStatusLine = "\u670D\u52A1\u5668\u5DF2\u5173\u95ED\uFF0C\u5DF2\u8FD4\u56DE\u5927\u5385\u3002";
                _lanRoomStatusText = _lanStatusLine;
                _lanRoomPanelOpen = true;
                _openGkStartHubOpen = true;
                _lanFocusedField = null;
                UpdateMouseCaptureState();
                Invalidate();
                return true;
                /*
                _lanStatusLine = item.Message;
                _lanRoomStatusText = "裁判主机已退出，房间已关闭。";
                CloseLanSession();
                _lanRoomPanelOpen = false;
                _lanFocusedField = null;
                return true;
                */
            case "peer_disconnected":
                _lanStatusLine = item.Message;
                _lanRoomStatusText = item.Message;
                HandleLanPeerDisconnected(item.PeerPlayerId ?? string.Empty, item.PeerPlayerName ?? string.Empty);
                return true;
            case LanProtocolMessageTypes.Status:
            case LanProtocolMessageTypes.Error:
                _lanStatusLine = item.Message;
                PublishLanLobbySelection();
                return true;
            default:
                return false;
        }
    }

    private bool DispatchLanSessionControlEvent(LanSessionEvent item)
    {
        switch (item.Type)
        {
            case LanProtocolMessageTypes.Input when item.Input is not null:
                BufferLanRemoteInput(item.Input);
                return true;
            case LanProtocolMessageTypes.SeatClaim when item.SeatClaim is not null:
                HandleLanSeatClaim(item.SeatClaim);
                return true;
            case LanProtocolMessageTypes.Roster when item.Roster is not null:
                HandleLanRoster(item.Roster);
                return true;
            case LanProtocolMessageTypes.PlayerInput when item.PlayerInput is not null:
                HandleLanPlayerInput(item.PlayerInput);
                return true;
            case LanProtocolMessageTypes.LobbySelection when item.LobbySelection is not null:
                HandleLanLobbySelection(item.LobbySelection);
                return true;
            case LanProtocolMessageTypes.StartMatch when item.StartMatch is not null:
                HandleLanStartMatch(item.StartMatch);
                return true;
            default:
                return false;
        }
    }

    private bool DispatchLanSessionMatchSyncEvent(LanSessionEvent item)
    {
        switch (item.Type)
        {
            case LanProtocolMessageTypes.Digest when item.Digest is not null:
                HandleLanDigest(item.Digest);
                return true;
            case LanProtocolMessageTypes.Snapshot when item.Snapshot is not null:
                HandleLanSnapshot(item.Snapshot);
                return true;
            case LanProtocolMessageTypes.AuthoritativeSnapshot when item.AuthoritativeSnapshot is not null:
                HandleLanAuthoritativeSnapshot(item.AuthoritativeSnapshot);
                return true;
            default:
                return false;
        }
    }

    private bool DispatchLanSessionRefereeEvent(LanSessionEvent item)
    {
        switch (item.Type)
        {
            case LanProtocolMessageTypes.RefereeReport when item.RefereeReport is not null:
                HandleLanRefereeReport(item.RefereeReport);
                return true;
            case LanProtocolMessageTypes.RefereeDecision when item.RefereeDecision is not null:
                HandleLanRefereeDecision(item.RefereeDecision);
                return true;
            case LanProtocolMessageTypes.MatchEvent when item.MatchEvent is not null:
                HandleLanReliableMatchEvent(item.MatchEvent);
                return true;
            default:
                return false;
        }
    }

    private void HandleLanSeatClaim(LanSeatClaim claim)
    {
        string team = Simulator3dOptions.NormalizeTeam(claim.Team);
        string entityKey = NormalizeLanDuelEntityKey(claim.EntityKey);
        string seatId = string.IsNullOrWhiteSpace(claim.SeatId)
            ? ResolveLanSeatId(team, entityKey)
            : claim.SeatId.Trim();
        string role = string.IsNullOrWhiteSpace(claim.Role) ? "player" : claim.Role.Trim().ToLowerInvariant();
        string playerId = string.IsNullOrWhiteSpace(claim.PlayerId) ? $"guest_{claim.Sequence}" : claim.PlayerId.Trim();
        if (string.Equals(role, "player", StringComparison.OrdinalIgnoreCase)
            && !IsLanControllableRobotSeat(entityKey))
        {
            _lanRoomStatusText = $"{ResolveLanEntityLabel(entityKey)} 是占位席，暂不生成机器人。";
            if (_lanSession?.IsHost == true)
            {
                _ = _lanSession.SendRosterAsync(CreateLanRoomRoster(claim.Sequence));
            }

            return;
        }

        if (!CanAcceptLanRoleClaim(playerId, role, seatId))
        {
            if (_lanSession?.IsHost == true)
            {
                _ = _lanSession.SendRosterAsync(CreateLanRoomRoster(claim.Sequence));
            }
            return;
        }

        var state = new LanMatchSeatState(
            seatId,
            team,
            ResolveLanRobotNumber(entityKey),
            entityKey,
            playerId,
            string.IsNullOrWhiteSpace(claim.PlayerName) ? "Guest" : claim.PlayerName.Trim(),
            Connected: true,
            claim.Ready,
            role,
            Math.Clamp(claim.SpawnPointIndex, 0, LanSpawnPointCount - 1),
            claim.ChassisMode ?? string.Empty);

        RemoveLanRosterClaimsForPlayerIdentity(state.PlayerId, state.PlayerName, seatId);
        _lanRosterBySeatId[seatId] = state;
        RememberLanRoomMember(new LanLobbySelection(
            claim.Sequence,
            state.PlayerName,
            state.Team,
            state.EntityKey,
            ResolveLanSlotIndex(state.Team, state.EntityKey),
            state.Role,
            state.PlayerId,
            state.SpawnPointIndex,
            state.ChassisMode),
            state.Ready);

        if (_lanSession?.IsHost == true && IsLanMultiplayerMatchActive)
        {
            ApplyLanPreparationSelectionsToWorld();
        }

        if (_lanSession?.IsHost == true)
        {
            _ = _lanSession.SendRosterAsync(CreateLanRoomRoster(claim.Sequence));
        }
    }

    private void RemoveLanRosterClaimsForPlayerIdentity(string playerId, string playerName, string exceptSeatId)
    {
        string normalizedPlayerId = playerId.Trim();
        string normalizedPlayerName = NormalizeLanPlayerName(playerName);
        if (string.IsNullOrWhiteSpace(normalizedPlayerId) && string.IsNullOrWhiteSpace(normalizedPlayerName))
        {
            return;
        }

        foreach (string staleSeatId in _lanRosterBySeatId
                     .Where(pair =>
                         !string.Equals(pair.Key, exceptSeatId, StringComparison.OrdinalIgnoreCase)
                         && ((!string.IsNullOrWhiteSpace(normalizedPlayerId)
                                 && string.Equals(pair.Value.PlayerId, normalizedPlayerId, StringComparison.OrdinalIgnoreCase))
                             || (!string.IsNullOrWhiteSpace(normalizedPlayerName)
                                 && string.Equals(NormalizeLanPlayerName(pair.Value.PlayerName), normalizedPlayerName, StringComparison.OrdinalIgnoreCase))))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lanRosterBySeatId.Remove(staleSeatId);
        }
    }

    private static string NormalizeLanPlayerName(string? playerName)
        => string.IsNullOrWhiteSpace(playerName) ? string.Empty : playerName.Trim();

    private void HandleLanRoster(LanRoomRoster roster)
    {
        Dictionary<string, LanMatchSeatState> previousConnectedSeats = _lanRosterBySeatId.Values
            .Where(seat => seat.Connected)
            .GroupBy(BuildLanPresenceIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(BuildLanPresenceIdentity, seat => seat, StringComparer.OrdinalIgnoreCase);

        _lanRosterBySeatId.Clear();
        if (roster.Settings is not null)
        {
            _lanRoomSettings = roster.Settings;
            if (!string.IsNullOrWhiteSpace(roster.Settings.MatchMode))
            {
                _lanRoomMatchMode = roster.Settings.MatchMode;
            }
        }

        foreach (LanMatchSeatState seat in roster.Seats)
        {
            if (string.IsNullOrWhiteSpace(seat.SeatId))
            {
                continue;
            }

            if (seat.Connected)
            {
                RemoveLanRosterClaimsForPlayerIdentity(seat.PlayerId, seat.PlayerName, seat.SeatId);
            }

            _lanRosterBySeatId[seat.SeatId] = seat;
        }

        NormalizeLanRosterOccupancy();

        RemoveNonLocalLanRoomPlayers();
        foreach (LanMatchSeatState seat in _lanRosterBySeatId.Values)
        {
            if (seat.Connected && !string.IsNullOrWhiteSpace(seat.PlayerName))
            {
                RememberLanRoomMember(new LanLobbySelection(
                    roster.Sequence,
                    seat.PlayerName,
                    seat.Team,
                    seat.EntityKey,
                    ResolveLanSlotIndex(seat.Team, seat.EntityKey),
                    seat.Role,
                    seat.PlayerId,
                    seat.SpawnPointIndex,
                    seat.ChassisMode),
                    seat.Ready);
            }
        }

        LanMatchSeatState? localSeat = _lanRosterBySeatId.Values.FirstOrDefault(seat =>
            seat.Connected
            && string.Equals(seat.PlayerId, _lanLocalPlayerId, StringComparison.OrdinalIgnoreCase));
        if (localSeat is not null)
        {
            _lanLocalSpawnPointIndex = Math.Clamp(localSeat.SpawnPointIndex, 0, LanSpawnPointCount - 1);
        }

        if (_lanSession?.IsHost == true && IsLanMultiplayerMatchActive)
        {
            ApplyLanPreparationSelectionsToWorld();
        }

        Dictionary<string, LanMatchSeatState> currentConnectedSeats = _lanRosterBySeatId.Values
            .Where(seat => seat.Connected)
            .GroupBy(BuildLanPresenceIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(BuildLanPresenceIdentity, seat => seat, StringComparer.OrdinalIgnoreCase);
        foreach ((string identity, LanMatchSeatState seat) in previousConnectedSeats)
        {
            if (!currentConnectedSeats.ContainsKey(identity))
            {
                AppendLanPresenceLeaveEvent(seat);
            }
        }
    }

    private LanRoomRoster CreateLanRoomRoster(long sequence)
    {
        NormalizeLanRosterOccupancy();
        var seats = new List<LanMatchSeatState>(18);
        foreach (string team in new[] { "red", "blue" })
        {
            foreach (string entityKey in LanUcRoomSeatEntityKeys)
            {
                string seatId = ResolveLanSeatId(team, entityKey);
                if (_lanRosterBySeatId.TryGetValue(seatId, out LanMatchSeatState? existing))
                {
                    seats.Add(existing);
                    continue;
                }

                seats.Add(new LanMatchSeatState(
                    seatId,
                    team,
                    ResolveLanRobotNumber(entityKey),
                    entityKey,
                    string.Empty,
                    string.Empty,
                    Connected: false,
                    Ready: false,
                    Role: LanRobotSeatCatalog.IsPlaceholderOnly(entityKey) ? "placeholder" : "player",
                    SpawnPointIndex: ResolveLanDefaultSpawnPointIndex(entityKey)));
            }
        }

        foreach (LanMatchSeatState seat in _lanRosterBySeatId.Values
                     .Where(seat => seat.Connected
                         && !string.Equals(seat.Role, "player", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(seat => seat.Role, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(seat => seat.PlayerName, StringComparer.OrdinalIgnoreCase))
        {
            seats.Add(seat);
        }

        return new LanRoomRoster(sequence, _lanSession?.RoomName ?? "ARTINX 5v5", seats, _lanRoomSettings);
    }

    private static async Task SendLanMatchStartSequenceAsync(
        LanMultiplayerSession session,
        LanRoomRoster roster,
        LanStartMatchCommand command)
    {
        await session.SendRosterAsync(roster).ConfigureAwait(false);
        await session.SendStartMatchAsync(command).ConfigureAwait(false);
    }

    private void NormalizeLanRosterOccupancy()
    {
        HashSet<string> claimedIdentities = new(StringComparer.OrdinalIgnoreCase);
        foreach (LanMatchSeatState seat in _lanRosterBySeatId.Values
                     .Where(seat => seat.Connected)
                     .ToArray())
        {
            string identity = BuildLanPresenceIdentity(seat);
            if (!claimedIdentities.Add(identity))
            {
                _lanRosterBySeatId.Remove(seat.SeatId);
            }
        }
    }

    private void RemoveNonLocalLanRoomPlayers()
    {
        foreach (string staleKey in _lanRoomMembers
                     .Where(pair => !pair.Value.IsLocal)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lanRoomMembers.Remove(staleKey);
        }
    }

    private static string BuildLanPresenceIdentity(LanMatchSeatState seat)
    {
        if (!string.IsNullOrWhiteSpace(seat.PlayerId))
        {
            return $"id:{seat.PlayerId.Trim()}";
        }

        string normalizedName = NormalizeLanPlayerName(seat.PlayerName);
        return string.IsNullOrWhiteSpace(normalizedName)
            ? $"seat:{seat.SeatId}"
            : $"name:{normalizedName}";
    }

    private void AppendLanPresenceLeaveEvent(LanMatchSeatState seat)
    {
        if (!IsLanMultiplayerMatchActive)
        {
            return;
        }

        string playerName = string.IsNullOrWhiteSpace(seat.PlayerName) ? "玩家" : seat.PlayerName.Trim();
        Color color = string.IsNullOrWhiteSpace(seat.Team)
            ? Color.FromArgb(182, 190, 204)
            : ResolveTeamColor(seat.Team);
        AppendMatchEvent($"{playerName} 离线", color, 5.0f);
    }

    private void HandleLanPeerDisconnected(string playerId, string playerName)
    {
        bool removedAny = false;
        foreach (string staleSeatId in _lanRosterBySeatId
                     .Where(pair =>
                         (!string.IsNullOrWhiteSpace(playerId)
                             && string.Equals(pair.Value.PlayerId, playerId, StringComparison.OrdinalIgnoreCase))
                         || (!string.IsNullOrWhiteSpace(playerName)
                             && string.Equals(NormalizeLanPlayerName(pair.Value.PlayerName), NormalizeLanPlayerName(playerName), StringComparison.OrdinalIgnoreCase)))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            if (_lanRosterBySeatId.TryGetValue(staleSeatId, out LanMatchSeatState? removedSeat))
            {
                AppendLanPresenceLeaveEvent(removedSeat);
            }

            removedAny = _lanRosterBySeatId.Remove(staleSeatId) || removedAny;
        }

        foreach (string staleKey in _lanRoomMembers
                     .Where(pair => !pair.Value.IsLocal
                         && ((!string.IsNullOrWhiteSpace(playerId)
                                 && string.Equals(pair.Value.PlayerId, playerId, StringComparison.OrdinalIgnoreCase))
                             || (!string.IsNullOrWhiteSpace(playerName)
                                 && string.Equals(pair.Value.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lanRoomMembers.Remove(staleKey);
            removedAny = true;
        }

        if (removedAny && _lanSession?.IsHost == true)
        {
            _ = _lanSession.SendRosterAsync(CreateLanRoomRoster(++_lanLobbySequence));
        }
    }

    private void ApplyLanPreparationSelectionsToWorld()
    {
        if (_lanSession is null)
        {
            return;
        }

        _host.ApplyRoomGameSettings(_lanRoomSettings);
        _host.ApplyLanPreparationSelections(_lanRosterBySeatId.Values.ToArray(), hardTrimInactiveRobots: IsLanMultiplayerMatchActive);
        InvalidateOpenGkUcTopHudCache();
        if (IsLanObserverClient)
        {
            SelectLanRefereeInitialViewTarget();
        }

        if (_host.SelectedEntity is not null)
        {
            SnapCameraToSelectedEntity();
        }
    }

    private void HandleLanPlayerInput(LanPlayerInputFrame input)
    {
        if (_lanSession?.IsHost != true)
        {
            return;
        }

        AppendLanTrafficLog(false, $"player_input seq={input.Sequence} seat={input.SeatId} entity={input.EntityId}");
        AppendLanTrafficDetailLog(false, FormatLanPlayerInputDetail("rx", input));

        if (!_host.World.Entities.Any(entity =>
                !entity.IsSimulationSuppressed
                && string.Equals(entity.Id, input.EntityId, StringComparison.OrdinalIgnoreCase)))
        {
            ApplyLanPreparationSelectionsToWorld();
            if (!_host.World.Entities.Any(entity =>
                    !entity.IsSimulationSuppressed
                    && string.Equals(entity.Id, input.EntityId, StringComparison.OrdinalIgnoreCase)))
            {
                AppendLanTrafficDetailLog(false, $"rx_player_input_missing_entity_after_roster seq={input.Sequence} seat={input.SeatId} entity={input.EntityId}");
            }
        }

        if (!_lanRosterBySeatId.TryGetValue(input.SeatId, out LanMatchSeatState? seat)
            || !string.Equals(seat.PlayerId, input.PlayerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(seat.EntityKey, ResolveLanEntityKeyFromEntityId(input.EntityId), StringComparison.OrdinalIgnoreCase))
        {
            AppendLanTrafficDetailLog(false, $"rx_player_input_reject reason=seat_binding seq={input.Sequence} seat={input.SeatId} entity={input.EntityId} player={input.PlayerId}");
            QueueLanLocalRefereeReport(
                reporterPlayerId: "referee_host",
                reporterSeatId: "referee",
                observedEntityId: input.EntityId,
                ruleId: "lan.input.seat_binding",
                category: "network_integrity",
                severity: "warning",
                evidenceJson: $"{{\"playerId\":\"{EscapeJsonValue(input.PlayerId)}\",\"seatId\":\"{EscapeJsonValue(input.SeatId)}\",\"entityId\":\"{EscapeJsonValue(input.EntityId)}\",\"tick\":{input.SimulationTick}}}",
                suggestedAction: "reject_input");
            return;
        }

        PlayerControlState state = new()
        {
            EntityId = input.EntityId,
            Enabled = input.Input.Enabled,
            MoveForward = input.Input.MoveForward,
            MoveRight = input.Input.MoveRight,
            TurretYawDeltaDeg = input.Input.TurretYawDeltaDeg,
            GimbalPitchDeltaDeg = input.Input.GimbalPitchDeltaDeg,
            FirePressed = input.Input.FirePressed,
            AutoAimPressed = input.Input.AutoAimPressed,
            AutoAimGuidanceOnly = input.Input.AutoAimGuidanceOnly,
            HeroLobAutoFireReady = input.Input.HeroLobAutoFireReady,
            JumpRequested = input.Input.JumpRequested,
            StepClimbModeActive = input.Input.StepClimbModeActive,
            SmallGyroActive = input.Input.SmallGyroActive,
            BuyAmmoRequested = input.Input.BuyAmmoRequested,
            EnergyActivationPressed = input.Input.EnergyActivationPressed,
            HeroDeployHoldPressed = input.Input.HeroDeployHoldPressed,
            SuperCapActive = input.Input.SuperCapActive,
            SentryStanceToggleRequested = input.Input.SentryStanceToggleRequested,
        };

        if (input.Sequence <= _lanSimulationSequence)
        {
            if (!IsLanDuelRoomMode)
            {
                _latestLanRemoteInput = state;
                _lanInputFramesReceived++;
                _lastLanRemoteInputReceivedAtSec = _host.World.GameTimeSec;
                AppendLanTrafficDetailLog(false, $"rx_player_input_late_promoted seq={input.Sequence} sim_seq={_lanSimulationSequence} seat={input.SeatId} entity={input.EntityId}");
            }

            return;
        }

        if (IsMatchStartupControlLockActive)
        {
            state = SanitizeStartupLockedPlayerControl(state);
            _latestLanRemoteInput = state;
            _lanInputFramesReceived++;
            _lastLanRemoteInputReceivedAtSec = _host.World.GameTimeSec;
            _host.ApplyAimOnlyControlState(state);
            return;
        }

        _lanRemoteInputFrames[input.Sequence] = state;
        _latestLanRemoteInput = state;
        _lanInputFramesReceived++;
        _lastLanRemoteInputReceivedAtSec = _host.World.GameTimeSec;
        PruneLanRemoteInputFrames(_lanSimulationSequence);
    }

    private void HandleLanAuthoritativeSnapshot(LanAuthoritativeMatchSnapshot snapshot)
    {
        if (_lanSession?.IsHost == true)
        {
            return;
        }

        ApplyLanAuthoritativeStartupState(snapshot);
        _host.World.GameTimeSec = Math.Max(_host.World.GameTimeSec, snapshot.GameTimeSec);

        foreach (LanEntitySnapshot entitySnapshot in snapshot.Entities)
        {
            _host.ApplyNetworkEntitySnapshot(entitySnapshot, applyRuleState: true, hardPoseCorrection: false);
        }

        ApplyLanAuthoritativeProjectiles(snapshot.Projectiles);
        ApplyLanAuthoritativeTeams(snapshot.Teams);
        if (TryMarkLanDetailLogInterval(ref _lastLanAuthoritativeSnapshotDetailLogSec, 1.0))
        {
            AppendLanTrafficDetailLog(false, $"rx_authoritative_snapshot seq={snapshot.Sequence} tick={snapshot.SimulationTick} game_t={snapshot.GameTimeSec:0.000} phase={snapshot.MatchPhase} entities={snapshot.Entities.Count} projectiles={snapshot.Projectiles.Count} teams={snapshot.Teams.Count} startup={snapshot.StartupPhase}/{snapshot.StartupActive} local_t={_host.World.GameTimeSec:0.000}");
        }

        _lanAuthoritativeSnapshotsReceived++;
        _lastLanSnapshotReceivedAtSec = _host.World.GameTimeSec;
    }

    private void ApplyLanAuthoritativeStartupState(LanAuthoritativeMatchSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.StartupPhase)
            || !TryParseLanStartupPhase(snapshot.StartupPhase, out MatchStartupPhase authoritativePhase))
        {
            return;
        }

        if (_appState != SimulatorAppState.InMatch)
        {
            return;
        }

        long nowTicks = _frameClock.ElapsedTicks;
        bool authoritativeStartupActive = snapshot.StartupActive
            && authoritativePhase is MatchStartupPhase.Loading
                or MatchStartupPhase.Preparation
                or MatchStartupPhase.SelfCheck
                or MatchStartupPhase.Countdown;
        if (authoritativeStartupActive)
        {
            bool phaseChanged = _matchStartupPhase != authoritativePhase;
            _matchStartupPhase = authoritativePhase;
            double durationSec = snapshot.StartupPhaseDurationSec > 1e-6
                ? snapshot.StartupPhaseDurationSec
                : ResolveMatchStartupPhaseDurationSec(authoritativePhase);
            double elapsedSec = Math.Clamp(snapshot.StartupPhaseElapsedSec, 0.0, Math.Max(0.0, durationSec) + 2.0);
            _matchStartupPhaseStartTicks = nowTicks - (long)Math.Round(elapsedSec * Stopwatch.Frequency);
            _host.World.GameTimeSec = snapshot.GameTimeSec;
            _simulationAccumulatorSec = Math.Min(_simulationAccumulatorSec, Math.Max(0.0, _host.DeltaTimeSec));
            _paused = ResolveLanAuthoritativeStartupPaused(authoritativePhase, snapshot.Paused);
            if (authoritativePhase == MatchStartupPhase.SelfCheck)
            {
                _matchSelfCheckPanelOpen = _pSettingsPanelOpen;
            }
            else if (authoritativePhase != MatchStartupPhase.SelfCheck)
            {
                _matchSelfCheckPanelOpen = false;
            }

            if (phaseChanged || _lastAppliedLanStartupPhase != authoritativePhase)
            {
                ApplyLanStartupPhaseViewState(authoritativePhase);
                InvalidateGpuOverlayLayer();
                UpdateMouseCaptureState();
                LogMatchStartupState($"lan_authoritative_phase phase={authoritativePhase} elapsed={elapsedSec:0.000}");
            }

            _lastAppliedLanStartupPhase = authoritativePhase;
            return;
        }

        if (authoritativePhase == MatchStartupPhase.Live
            && (_matchStartupPhase != MatchStartupPhase.Live || IsMatchStartupActive))
        {
            _matchStartupPhase = MatchStartupPhase.Live;
            _matchStartupPhaseStartTicks = nowTicks;
            _matchStartupViewReady = true;
            _matchSelfCheckPanelOpen = false;
            _paused = snapshot.Paused;
            _simulationAccumulatorSec = 0.0;
            _lastFrameClockTicks = nowTicks;
            ResetLiveInput();
            InvalidateGpuOverlayLayer();
            UpdateMouseCaptureState();
            LogMatchStartupState("lan_authoritative_live");
        }

        _host.World.GameTimeSec = snapshot.GameTimeSec;
        _lastAppliedLanStartupPhase = authoritativePhase;
    }

    private bool ResolveLanAuthoritativeStartupPaused(MatchStartupPhase phase, bool authoritativePaused)
        => phase switch
        {
            MatchStartupPhase.Preparation => true,
            MatchStartupPhase.SelfCheck or MatchStartupPhase.Countdown => true,
            MatchStartupPhase.Loading => true,
            _ => authoritativePaused,
        };

    private void ApplyLanStartupPhaseViewState(MatchStartupPhase phase)
    {
        if (phase == MatchStartupPhase.Preparation)
        {
            _observerPinned = false;
            if (IsLanObserverClient)
            {
                SelectLanRefereeInitialViewTarget();
                SetLanRefereeViewMode(LanRefereeViewMode.FreeThirdPerson);
            }
            else if (!_lanPreparationConfirmed)
            {
                _observerMode = false;
                _firstPersonView = false;
                _followSelection = false;
            }
        }

        if (phase is MatchStartupPhase.SelfCheck or MatchStartupPhase.Countdown)
        {
            if (IsLanObserverClient)
            {
                _observerPinned = false;
                if (!_observerMode && !_firstPersonView)
                {
                    SetLanRefereeViewMode(LanRefereeViewMode.FreeThirdPerson);
                }
            }
            else
            {
                _firstPersonView = true;
                _followSelection = true;
                SelectLanLocalPlayerEntity();
            }
        }
    }

    private static bool TryParseLanStartupPhase(string phase, out MatchStartupPhase parsed)
    {
        if (Enum.TryParse(phase, ignoreCase: true, out parsed))
        {
            return true;
        }

        parsed = MatchStartupPhase.None;
        return false;
    }

    private void ApplyLanAuthoritativeProjectiles(IReadOnlyList<LanProjectileSnapshot> projectiles)
    {
        _host.World.Projectiles.Clear();
        foreach (LanProjectileSnapshot projectile in projectiles)
        {
            _host.World.Projectiles.Add(new SimulationProjectile
            {
                Id = projectile.Id,
                ShooterId = projectile.ShooterId,
                Team = projectile.Team,
                AmmoType = projectile.AmmoType,
                X = projectile.X,
                Y = projectile.Y,
                HeightM = projectile.HeightM,
                VelocityXWorldPerSec = projectile.VelocityXWorldPerSec,
                VelocityYWorldPerSec = projectile.VelocityYWorldPerSec,
                VelocityZMps = projectile.VelocityZMps,
            });
        }
    }

    private void ApplyLanAuthoritativeTeams(IReadOnlyList<LanTeamSnapshot> teams)
    {
        foreach (LanTeamSnapshot snapshot in teams)
        {
            SimulationTeamState team = _host.World.GetOrCreateTeamState(snapshot.Team);
            team.Gold = Math.Max(0.0, snapshot.Gold);
            team.EnergyMechanismState = string.IsNullOrWhiteSpace(snapshot.EnergyMechanismState)
                ? "inactive"
                : snapshot.EnergyMechanismState;
            team.BaseArmorForcedOpen = snapshot.BaseArmorForcedOpen;
        }
    }

    private void HandleLanRefereeReport(LanLocalRefereeReport report)
    {
        _lanRefereeReports.Add(report);
        _lanStatusLine = $"referee report {report.RuleId} {report.ObservedEntityId}";
    }

    private void HandleLanRefereeDecision(LanRefereeDecision decision)
    {
        _lanStatusLine = decision.Accepted
            ? $"referee penalty {decision.PenaltyCode} {decision.TargetEntityId}"
            : $"referee ignored {decision.RuleId}";
        if (!string.IsNullOrWhiteSpace(decision.Message))
        {
            AppendMatchEvent(decision.Message, decision.Accepted ? Color.FromArgb(236, 110, 92) : Color.FromArgb(180, 190, 204), 4.0f);
        }
    }

    private void HandleLanReliableMatchEvent(LanReliableMatchEvent matchEvent)
    {
        _lanStatusLine = $"match event {matchEvent.EventType} {matchEvent.TargetId}";
    }

    private void QueueLanLocalRefereeReport(
        string reporterPlayerId,
        string reporterSeatId,
        string observedEntityId,
        string ruleId,
        string category,
        string severity,
        string evidenceJson,
        string suggestedAction)
    {
        if (_lanSession is null)
        {
            return;
        }

        _lanRefereeReportSequence++;
        var report = new LanLocalRefereeReport(
            _lanRefereeReportSequence,
            reporterPlayerId,
            reporterSeatId,
            observedEntityId,
            ruleId,
            category,
            severity,
            _host.World.GameTimeSec,
            _lanSimulationSequence,
            evidenceJson,
            suggestedAction);
        _lanRefereeReports.Add(report);
        _ = _lanSession.SendRefereeReportAsync(report);
    }

    private static string EscapeJsonValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ResolveLanEntityKeyFromEntityId(string entityId)
    {
        foreach (string key in LanRobotSeatCatalog.RoomSeatEntityKeys)
        {
            if (entityId.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return NormalizeLanDuelEntityKey(entityId);
    }

    private void BufferLanRemoteInput(LanInputFrame input)
    {
        AppendLanTrafficDetailLog(false, FormatLanPlayerInputDetail("rx_legacy", new LanPlayerInputFrame(
            input.Sequence,
            "legacy",
            ResolveLanSeatId(_lanRemoteTeam, ResolveLanEntityKeyFromEntityId(input.EntityId)),
            input.EntityId,
            input.Sequence,
            _host.World.GameTimeSec,
            input)));
        PlayerControlState state = FromLanInputFrame(input);
        if (IsMatchStartupControlLockActive)
        {
            state = SanitizeStartupLockedPlayerControl(state);
            _latestLanRemoteInput = state;
            _lanInputFramesReceived++;
            _lastLanRemoteInputReceivedAtSec = _host.World.GameTimeSec;
            _host.ApplyAimOnlyControlState(state);
            return;
        }

        if (input.Sequence <= _lanSimulationSequence)
        {
            return;
        }

        _lanRemoteInputFrames[input.Sequence] = state;
        _latestLanRemoteInput = state;
        _lanInputFramesReceived++;
        _lastLanRemoteInputReceivedAtSec = _host.World.GameTimeSec;
        PruneLanRemoteInputFrames(_lanSimulationSequence);
    }

    private void HandleLanStartMatch(LanStartMatchCommand command)
    {
        if (_lanSession is null || _lanSession.IsHost)
        {
            return;
        }

        AppendLanTrafficLog(false, $"start_match mode={command.MatchMode} host={command.HostTeam}/{command.HostEntityKey} guest={command.GuestTeam}/{command.GuestEntityKey}");

        ApplyLanStartMatchCommand(command);
        _host.SetMatchModePreservingLoadedWorld(command.MatchMode);
        ApplyLanDuelConfigurationToHost();
        ResetLanRuntimeSyncState();
        if (_appState != SimulatorAppState.Lobby)
        {
            EnterLobby();
            DiscardPendingLobbyWorldRebuild();
        }

        _lanStatusLine = "主机已开始多人对局";
        _observerMode = false;
        _observerPinned = false;
        _tacticalMode = false;
        _firstPersonView = !IsLanObserverClient;
        _followSelection = true;
        if (IsLanObserverClient)
        {
            SelectLanRefereeInitialViewTarget();
            SetLanRefereeViewMode(LanRefereeViewMode.FreeThirdPerson);
        }
        else
        {
            SelectLanLocalPlayerEntity();
        }
        BeginMatchStartupSequence(resetWorld: true);
    }

    private void ApplyLanStartMatchCommand(LanStartMatchCommand command)
    {
        if (_lanSession is null)
        {
            return;
        }

        bool duelStart = string.Equals(command.MatchMode, "duel_1v1", StringComparison.OrdinalIgnoreCase)
            || IsLanDuelRoomMode;
        if (!_lanSession.IsHost && !duelStart && TryResolveLanLocalPlayerSeat(out LanMatchSeatState localSeat))
        {
            _lanLocalTeam = Simulator3dOptions.NormalizeTeam(localSeat.Team);
            _lanLocalEntityKey = NormalizeLanDuelEntityKey(localSeat.EntityKey);
            _lanLocalSpawnPointIndex = Math.Clamp(localSeat.SpawnPointIndex, 0, LanSpawnPointCount - 1);
            _lanRemoteTeam = OppositeLanTeam(_lanLocalTeam);
            _lanRemoteEntityKey = NormalizeLanDuelEntityKey(command.HostEntityKey);
        }
        else
        {
            _lanLocalTeam = _lanSession.IsHost
                ? Simulator3dOptions.NormalizeTeam(command.HostTeam)
                : Simulator3dOptions.NormalizeTeam(command.GuestTeam);
            _lanRemoteTeam = _lanSession.IsHost
                ? Simulator3dOptions.NormalizeTeam(command.GuestTeam)
                : Simulator3dOptions.NormalizeTeam(command.HostTeam);
            _lanLocalEntityKey = NormalizeLanDuelEntityKey(_lanSession.IsHost ? command.HostEntityKey : command.GuestEntityKey);
            _lanRemoteEntityKey = NormalizeLanDuelEntityKey(_lanSession.IsHost ? command.GuestEntityKey : command.HostEntityKey);
        }

        _host.SetHeroPerformanceMode(command.HeroPerformanceMode, rebuildWorld: false);
        _host.SetInfantryMode(command.InfantryMode, rebuildWorld: false);
        _host.SetInfantryDurabilityMode(command.InfantryDurabilityMode, rebuildWorld: false);
        _host.SetInfantryWeaponMode(command.InfantryWeaponMode, rebuildWorld: false);
        _host.SetSentryControlMode(command.SentryControlMode, rebuildWorld: false);
        _host.SetSentryStance(command.SentryStance, rebuildWorld: false);
        _host.SetAutoAimAccuracyScale(command.AutoAimAccuracyScale);
        _host.SetDisplayLatencyMs(command.DisplayLatencyMs);
        _host.SetDuelRoundLimit(command.DuelRoundLimit);
        _host.SetProjectilePhysicsBackend(command.ProjectilePhysicsBackend);
        ApplyLanDuelConfigurationToHost();
    }

    private bool TryResolveLanLocalPlayerSeat(out LanMatchSeatState seat)
    {
        LanMatchSeatState? resolvedSeat = _lanRosterBySeatId.Values.FirstOrDefault(candidate =>
            candidate.Connected
            && string.Equals(candidate.PlayerId, _lanLocalPlayerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Role, "player", StringComparison.OrdinalIgnoreCase)
            && IsLanControllableRobotSeat(candidate.EntityKey));
        if (resolvedSeat is null)
        {
            seat = default!;
            return false;
        }

        seat = resolvedSeat;
        return true;
    }

    private void HandleLanDigest(LanValidationDigest digest)
    {
        _lastLanRemoteDigest = digest;
        AppendLanTrafficLog(false, $"digest seq={digest.Sequence} entities={digest.EntityCount} projectiles={digest.ProjectileCount} hash={digest.Hash:x8}");
        AppendLanTrafficDetailLog(false, $"rx_digest seq={digest.Sequence} game_t={digest.GameTimeSec:0.000} entities={digest.EntityCount} projectiles={digest.ProjectileCount} hash={digest.Hash:x16} summary={digest.Summary}");
        if (_lanSession is null)
        {
            return;
        }

        LanValidationDigest local = CreateLanValidationDigest(_lanDigestSequence);
        bool matched = local.Hash == digest.Hash;
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} role={(_lanSession.IsHost ? "host" : "guest")} peerSeq={digest.Sequence} "
            + $"localSeq={local.Sequence} match={matched} "
            + $"local={local.Hash:x16} remote={digest.Hash:x16} "
            + $"remoteSummary={digest.Summary}";
        SimulatorRuntimeLog.Append("lan_validation.log", line);
        if (!matched)
        {
            _lanStatusLine = $"规则校验不一致 local={local.Hash:x8} remote={digest.Hash:x8}";
        }
    }

    private void HandleLanSnapshot(LanWorldSnapshot snapshot)
    {
        if (_lanSession is null || !IsLanMultiplayerMatchActive)
        {
            return;
        }

        if (TryMarkLanDetailLogInterval(ref _lastLanSnapshotDetailLogSec, 1.0))
        {
            AppendLanTrafficLog(false, $"snapshot seq={snapshot.Sequence} entity_count={snapshot.Entities.Count} t={snapshot.GameTimeSec:0.000}");
            AppendLanTrafficDetailLog(false, $"rx_snapshot seq={snapshot.Sequence} game_t={snapshot.GameTimeSec:0.000} entities={snapshot.Entities.Count} authoritative={snapshot.AuthoritativeEntityId} local_t={_host.World.GameTimeSec:0.000} drift={Math.Abs(_host.World.GameTimeSec - snapshot.GameTimeSec):0.000}");
        }

        _lanSnapshotsReceived++;
        _lastLanSnapshotReceivedAtSec = _host.World.GameTimeSec;
        double driftSec = Math.Abs(_host.World.GameTimeSec - snapshot.GameTimeSec);
        if (!_lanSession.IsHost)
        {
            _host.World.GameTimeSec = Math.Max(_host.World.GameTimeSec, snapshot.GameTimeSec);
        }

        if (driftSec > 0.18)
        {
            _lanStatusLine = $"联机时间差 {driftSec:0.000}s，已记录校验";
        }

        ApplyLanSnapshotPoseCorrections(snapshot);
    }

    private void ApplyLanSnapshotPoseCorrections(LanWorldSnapshot snapshot)
    {
        if (_lanSession is null)
        {
            return;
        }

        if (_lanSession.IsHost)
        {
            ValidateLanRemotePoseSnapshot(snapshot);
            return;
        }

        foreach (LanEntitySnapshot entitySnapshot in snapshot.Entities)
        {
            _host.ApplyNetworkEntitySnapshot(entitySnapshot, applyRuleState: true, hardPoseCorrection: true);
        }
    }

    private void ValidateLanRemotePoseSnapshot(LanWorldSnapshot snapshot)
    {
        if (snapshot.Entities.Count == 0)
        {
            return;
        }

        double maxPositionDriftWorld = 0.0;
        double maxYawDriftDeg = 0.0;
        string driftEntityId = string.Empty;
        foreach (LanEntitySnapshot entitySnapshot in snapshot.Entities)
        {
            SimulationEntity? local = _host.World.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Id, entitySnapshot.Id, StringComparison.OrdinalIgnoreCase));
            if (local is null)
            {
                continue;
            }

            double dx = entitySnapshot.X - local.X;
            double dy = entitySnapshot.Y - local.Y;
            double drift = Math.Sqrt(dx * dx + dy * dy);
            double yawDrift = Math.Abs(NormalizeSignedLanDeg(entitySnapshot.AngleDeg - local.AngleDeg));
            if (drift > maxPositionDriftWorld || yawDrift > maxYawDriftDeg)
            {
                maxPositionDriftWorld = Math.Max(maxPositionDriftWorld, drift);
                maxYawDriftDeg = Math.Max(maxYawDriftDeg, yawDrift);
                driftEntityId = entitySnapshot.Id;
            }
        }

        double nowSec = _host.World.GameTimeSec;
        if ((maxPositionDriftWorld < 2.0 && maxYawDriftDeg < 4.0)
            || nowSec - _lastLanPoseDriftLogSec < 0.50)
        {
            return;
        }

        _lastLanPoseDriftLogSec = nowSec;
        _lanStatusLine = $"远端位姿漂移 {driftEntityId} pos={maxPositionDriftWorld:0.00} yaw={maxYawDriftDeg:0.0}°";
        SimulatorRuntimeLog.Append(
            "lan_validation.log",
            $"{DateTime.Now:HH:mm:ss.fff} pose_drift entity={driftEntityId} pos_world={maxPositionDriftWorld:0.000} yaw_deg={maxYawDriftDeg:0.000} remote_seq={snapshot.Sequence}");
    }

    private void PublishLanValidationIfDue()
    {
        if (!IsLanMultiplayerMatchActive || _lanSession is null || !_lanSession.IsConnected)
        {
            return;
        }

        double nowSec = _frameClock.Elapsed.TotalSeconds;
        PublishLanDigestIfDue(nowSec);
        PublishLanHostSnapshotIfDue(nowSec);
    }

    private void PublishLanDigestIfDue(double nowSec)
    {
        if (_lanSession is null || nowSec - _lastLanDigestSentAtSec < LanDigestIntervalSec)
        {
            return;
        }

        _lastLanDigestSentAtSec = nowSec;
        _lanDigestSequence++;
        LanValidationDigest digest = CreateLanValidationDigest(_lanDigestSequence);
        AppendLanTrafficDetailLog(true, $"tx_digest seq={digest.Sequence} game_t={digest.GameTimeSec:0.000} entities={digest.EntityCount} projectiles={digest.ProjectileCount} hash={digest.Hash:x16} summary={digest.Summary}");
        _ = _lanSession.SendDigestAsync(digest);
    }

    private void PublishLanHostSnapshotIfDue(double nowSec)
    {
        if (_lanSession is null || nowSec - _lastLanSnapshotSentAtSec < LanSnapshotIntervalSec)
        {
            return;
        }

        _lastLanSnapshotSentAtSec = nowSec;
        _lanSnapshotSequence++;
        if (_lanSession.IsHost)
        {
            LanAuthoritativeMatchSnapshot authoritative = CreateLanAuthoritativeMatchSnapshot(_lanSnapshotSequence);
            if (TryMarkLanDetailLogInterval(ref _lastLanAuthoritativeSnapshotTxDetailLogSec, 1.0))
            {
                AppendLanTrafficDetailLog(true, $"tx_authoritative_snapshot seq={authoritative.Sequence} tick={authoritative.SimulationTick} game_t={authoritative.GameTimeSec:0.000} phase={authoritative.MatchPhase} entities={authoritative.Entities.Count} projectiles={authoritative.Projectiles.Count} teams={authoritative.Teams.Count} startup={authoritative.StartupPhase}/{authoritative.StartupActive}");
            }

            _ = _lanSession.SendAuthoritativeSnapshotAsync(authoritative);
            _lanAuthoritativeSnapshotsSent++;
            return;
        }

        LanWorldSnapshot snapshot = CreateLanWorldSnapshot(_lanSnapshotSequence);
        if (TryMarkLanDetailLogInterval(ref _lastLanSnapshotTxDetailLogSec, 1.0))
        {
            AppendLanTrafficDetailLog(true, $"tx_snapshot seq={snapshot.Sequence} game_t={snapshot.GameTimeSec:0.000} entities={snapshot.Entities.Count} authoritative={snapshot.AuthoritativeEntityId}");
        }

        _ = _lanSession.SendSnapshotAsync(snapshot);
        _lanSnapshotsSent++;
    }

    private LanValidationDigest CreateLanValidationDigest(long sequence)
    {
        ulong hash = 14695981039346656037UL;
        int entityCount = 0;
        foreach (SimulationEntity entity in _host.World.Entities
                     .Where(entity => !entity.IsSimulationSuppressed)
                     .OrderBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase))
        {
            entityCount++;
            AddHash(ref hash, entity.Id);
            AddHash(ref hash, Math.Round(entity.X, 3));
            AddHash(ref hash, Math.Round(entity.Y, 3));
            AddHash(ref hash, Math.Round(entity.AngleDeg, 2));
            AddHash(ref hash, Math.Round(entity.TurretYawDeg, 2));
            AddHash(ref hash, Math.Round(entity.GimbalPitchDeg, 2));
            AddHash(ref hash, Math.Round(entity.Health, 1));
            AddHash(ref hash, Math.Round(entity.Power, 1));
            AddHash(ref hash, Math.Round(entity.Heat, 1));
            AddHash(ref hash, entity.Ammo17Mm);
            AddHash(ref hash, entity.Ammo42Mm);
            AddHash(ref hash, entity.ShotsFired);
            AddHash(ref hash, entity.IsAlive ? 1 : 0);
        }

        foreach (SimulationProjectile projectile in _host.World.Projectiles
                     .OrderBy(projectile => projectile.ShooterId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(projectile => Math.Round(projectile.X, 3))
                     .ThenBy(projectile => Math.Round(projectile.Y, 3))
                     .ThenBy(projectile => Math.Round(projectile.HeightM, 3)))
        {
            AddHash(ref hash, projectile.ShooterId);
            AddHash(ref hash, projectile.AmmoType);
            AddHash(ref hash, Math.Round(projectile.X, 3));
            AddHash(ref hash, Math.Round(projectile.Y, 3));
            AddHash(ref hash, Math.Round(projectile.HeightM, 3));
            AddHash(ref hash, Math.Round(projectile.VelocityXWorldPerSec, 3));
            AddHash(ref hash, Math.Round(projectile.VelocityYWorldPerSec, 3));
            AddHash(ref hash, Math.Round(projectile.VelocityZMps, 3));
        }

        AddHash(ref hash, _host.World.Projectiles.Count);
        AddHash(ref hash, _host.HeroPerformanceMode);
        AddHash(ref hash, _host.InfantryMode);
        AddHash(ref hash, _host.InfantryDurabilityMode);
        AddHash(ref hash, _host.InfantryWeaponMode);
        AddHash(ref hash, _host.SentryControlMode);
        AddHash(ref hash, _host.SentryStance);
        AddHash(ref hash, Math.Round(_host.AutoAimAccuracyScale, 3));
        AddHash(ref hash, Math.Round(_host.DisplayLatencyMs, 1));
        AddHash(ref hash, _host.DuelRoundLimit);
        AddHash(ref hash, _host.ProjectilePhysicsBackend);
        AddHash(ref hash, Math.Round(_host.World.GameTimeSec, 2));
        string summary = $"t={_host.World.GameTimeSec:0.00} entities={entityCount} projectiles={_host.World.Projectiles.Count} cfg={_host.InfantryMode}/{_host.HeroPerformanceMode}/{_host.ProjectilePhysicsBackend}";
        return new LanValidationDigest(sequence, _host.World.GameTimeSec, entityCount, _host.World.Projectiles.Count, hash, summary);
    }

    private LanWorldSnapshot CreateLanWorldSnapshot(long sequence)
    {
        string authoritativeEntityId = _host.SelectedEntity?.Id ?? string.Empty;
        string authoritativeTeam = _host.SelectedEntity?.Team ?? _lanLocalTeam;
        IEnumerable<SimulationEntity> sourceEntities = _lanSession?.IsHost == true
            ? _host.World.Entities.Where(entity => !entity.IsSimulationSuppressed)
            : _host.World.Entities.Where(entity =>
                !entity.IsSimulationSuppressed
                && string.Equals(entity.Id, authoritativeEntityId, StringComparison.OrdinalIgnoreCase));

        LanEntitySnapshot[] entities = sourceEntities
            .Select(entity => new LanEntitySnapshot(
                entity.Id,
                entity.Team,
                entity.X,
                entity.Y,
                entity.AngleDeg,
                entity.TurretYawDeg,
                entity.GimbalPitchDeg,
                entity.VelocityXWorldPerSec,
                entity.VelocityYWorldPerSec,
                entity.AngularVelocityDegPerSec,
                entity.Health,
                entity.Power,
                entity.Heat,
                entity.Ammo17Mm,
                entity.Ammo42Mm,
                entity.ShotsFired,
                entity.FortReserveAmmo,
                entity.FortReserveAmmoCap,
                entity.IsAlive,
                entity.IsPlayerControlled))
            .ToArray();
        return new LanWorldSnapshot(sequence, _host.World.GameTimeSec, authoritativeEntityId, authoritativeTeam, entities);
    }

    private LanAuthoritativeMatchSnapshot CreateLanAuthoritativeMatchSnapshot(long sequence)
    {
        LanEntitySnapshot[] entities = _host.World.Entities
            .Where(entity => !entity.IsSimulationSuppressed)
            .OrderBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            .Select(CreateLanEntitySnapshot)
            .ToArray();

        LanProjectileSnapshot[] projectiles = _host.World.Projectiles
            .OrderBy(projectile => projectile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(projectile => new LanProjectileSnapshot(
                projectile.Id,
                projectile.ShooterId,
                projectile.Team,
                projectile.AmmoType,
                projectile.X,
                projectile.Y,
                projectile.HeightM,
                projectile.VelocityXWorldPerSec,
                projectile.VelocityYWorldPerSec,
                projectile.VelocityZMps))
            .ToArray();

        LanTeamSnapshot[] teams = _host.World.Teams.Values
            .OrderBy(team => team.Team, StringComparer.OrdinalIgnoreCase)
            .Select(team => new LanTeamSnapshot(
                team.Team,
                team.Gold,
                ResolveLanTeamScore(team.Team),
                ResolveLanStructureHealth(team.Team, "base"),
                ResolveLanStructureHealth(team.Team, "outpost"),
                team.EnergyMechanismState,
                team.BaseArmorForcedOpen))
            .ToArray();

        return new LanAuthoritativeMatchSnapshot(
            sequence,
            _lanSimulationSequence,
            _host.World.GameTimeSec,
            ResolveLanMatchPhase(),
            entities,
            projectiles,
            teams,
            _matchStartupPhase.ToString(),
            ResolveCurrentMatchStartupPhaseElapsedSec(),
            ResolveMatchStartupPhaseDurationSec(_matchStartupPhase),
            IsMatchStartupActive,
            _paused);
    }

    private double ResolveCurrentMatchStartupPhaseElapsedSec()
        => _matchStartupPhaseStartTicks <= 0
            ? 0.0
            : Math.Max(0.0, (_frameClock.ElapsedTicks - _matchStartupPhaseStartTicks) / (double)Stopwatch.Frequency);

    private static double ResolveMatchStartupPhaseDurationSec(MatchStartupPhase phase)
        => phase switch
        {
            MatchStartupPhase.Preparation => MatchStartupPreparationSec,
            MatchStartupPhase.SelfCheck => MatchStartupSelfCheckSec,
            MatchStartupPhase.Countdown => MatchStartupCountdownSec,
            _ => 0.0,
        };

    private static LanEntitySnapshot CreateLanEntitySnapshot(SimulationEntity entity)
        => new(
            entity.Id,
            entity.Team,
            entity.X,
            entity.Y,
            entity.AngleDeg,
            entity.TurretYawDeg,
            entity.GimbalPitchDeg,
            entity.VelocityXWorldPerSec,
            entity.VelocityYWorldPerSec,
            entity.AngularVelocityDegPerSec,
            entity.Health,
            entity.Power,
            entity.Heat,
            entity.Ammo17Mm,
            entity.Ammo42Mm,
            entity.ShotsFired,
            entity.FortReserveAmmo,
            entity.FortReserveAmmoCap,
            entity.IsAlive,
            entity.IsPlayerControlled);

    private int ResolveLanTeamScore(string team)
    {
        if (!_host.IsDuelMode)
        {
            return 0;
        }

        Simulator3dHost.DuelMatchSnapshot snapshot = _host.GetDuelMatchSnapshot();
        return string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase)
            ? snapshot.BlueScore
            : snapshot.RedScore;
    }

    private double ResolveLanStructureHealth(string team, string entityType)
    {
        SimulationEntity? structure = _host.World.Entities.FirstOrDefault(entity =>
            !entity.IsSimulationSuppressed
            && string.Equals(entity.Team, team, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entity.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
        return Math.Max(0.0, structure?.Health ?? 0.0);
    }

    private string ResolveLanMatchPhase()
    {
        if (_host.IsDuelFinished)
        {
            return "finished";
        }

        return _paused ? "paused" : "running";
    }

    private void DrawLanMultiplayerDemoOverlay(Graphics graphics)
    {
        if (!IsLanMultiplayerMatchActive || _lanSession is null)
        {
            return;
        }

        LanBandwidthEstimate budget = LanBandwidthBudget.Estimate(
            playerCount: 10,
            robotCount: Math.Max(2, _host.World.Entities.Count(entity => !entity.IsSimulationSuppressed)),
            activeProjectileCount: _host.World.Projectiles.Count,
            snapshotRateHz: 1.0 / Math.Max(0.001, LanSnapshotIntervalSec));
        LanTrafficSnapshot traffic = _lastLanTrafficSnapshot ?? _lanSession.CaptureTrafficSnapshot();
        LanTrafficReport? remoteTraffic = _lastLanRemoteTrafficReport;
        double hostOutboundBudgetMbps = budget.JsonHostOutboundMegabitsPerSecond;
        double clientBudgetMbps = budget.RecommendedMegabitsPerSecondPerClient * budget.JsonSafetyMultiplier;
        double sentRatio = hostOutboundBudgetMbps <= 1e-6 ? 0.0 : traffic.SentMegabitsPerSecond / hostOutboundBudgetMbps;
        double recvRatio = clientBudgetMbps <= 1e-6 ? 0.0 : traffic.ReceivedMegabitsPerSecond / clientBudgetMbps;
        double remoteAgeSec = double.IsFinite(_lastLanTrafficReportReceivedAtSec)
            ? Math.Max(0.0, _frameClock.Elapsed.TotalSeconds - _lastLanTrafficReportReceivedAtSec)
            : double.NaN;
        string linkCheck = remoteTraffic is null
            ? "peer report --"
            : $"peer tx {remoteTraffic.SentMegabitsPerSecond:0.000} rx {remoteTraffic.ReceivedMegabitsPerSecond:0.000} Mbps age {remoteAgeSec:0.0}s";
        string crossCheck = remoteTraffic is null
            ? $"local tx {traffic.SentMegabitsPerSecond:0.000} rx {traffic.ReceivedMegabitsPerSecond:0.000} Mbps"
            : $"cross localTx/peerRx {FormatLanMbpsDelta(traffic.SentMegabitsPerSecond, remoteTraffic.ReceivedMegabitsPerSecond)}  peerTx/localRx {FormatLanMbpsDelta(remoteTraffic.SentMegabitsPerSecond, traffic.ReceivedMegabitsPerSecond)}";

        string role = _lanSession.IsHost ? "HOST" : "CLIENT";
        string transport = _lanSession.IsConnected ? "linked" : "offline";
        string inputAge = FormatLanAge(_host.World.GameTimeSec, _lastLanRemoteInputReceivedAtSec);
        string snapshotAge = FormatLanAge(_host.World.GameTimeSec, _lastLanSnapshotReceivedAtSec);
        string line1 = $"LAN DEMO  {role}  {transport}  tick {_lanSimulationSequence}";
        string line2 = $"input tx/rx {_lanInputFramesSent}/{_lanInputFramesReceived}  queue L/R {_lanLocalInputFrames.Count}/{_lanRemoteInputFrames.Count}  remote {inputAge}";
        string line3 = $"{linkCheck}  drop local {traffic.TotalDroppedRealtimeMessages}/{traffic.TotalDroppedReliableMessages} remote {remoteTraffic?.TotalDroppedRealtimeMessages ?? 0}/{remoteTraffic?.TotalDroppedReliableMessages ?? 0}";
        string line4 = $"{crossCheck}  budget host {hostOutboundBudgetMbps:0.00} ({sentRatio:0.0}x) client {clientBudgetMbps:0.00} ({recvRatio:0.0}x) snap {snapshotAge}";

        int width = Math.Min(620, Math.Max(430, ClientSize.Width - 48));
        Rectangle panel = new(24, ToolbarHeight + HudHeight + 12, width, 96);
        using GraphicsPath path = CreateRoundedRectangle(panel, 7);
        using var fill = new SolidBrush(Color.FromArgb(174, 8, 14, 22));
        using var border = new Pen(Color.FromArgb(138, 92, 164, 220), 1f);
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 242, 248));
        using var textBrush = new SolidBrush(Color.FromArgb(210, 222, 234));
        using var accentBrush = new SolidBrush(Color.FromArgb(236, 120, 218, 236));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString(line1, _smallHudFont, titleBrush, panel.X + 12, panel.Y + 8);
        graphics.DrawString(line2, _tinyHudFont, textBrush, panel.X + 12, panel.Y + 31);
        graphics.DrawString(line3, _tinyHudFont, textBrush, panel.X + 12, panel.Y + 49);
        graphics.DrawString(line4, _tinyHudFont, accentBrush, panel.X + 12, panel.Y + 67);
    }

    private static string FormatLanAge(double nowSec, double timestampSec)
        => double.IsFinite(timestampSec)
            ? $"{Math.Max(0.0, nowSec - timestampSec):0.00}s"
            : "--";

    private static string FormatLanMbpsDelta(double sourceMbps, double observedMbps)
    {
        double delta = observedMbps - sourceMbps;
        double ratio = sourceMbps <= 1e-6 ? 0.0 : observedMbps / sourceMbps;
        return $"{sourceMbps:0.000}->{observedMbps:0.000}Mbps d={delta:+0.000;-0.000;0.000} r={ratio:0.00}";
    }

    private static LanInputFrame ToLanInputFrame(PlayerControlState state, long sequence)
        => new(
            sequence,
            state.EntityId ?? string.Empty,
            state.Enabled,
            state.MoveForward,
            state.MoveRight,
            state.TurretYawDeltaDeg,
            state.GimbalPitchDeltaDeg,
            state.FirePressed,
            state.AutoAimPressed,
            state.AutoAimGuidanceOnly,
            state.HeroLobAutoFireReady,
            state.JumpRequested,
            state.StepClimbModeActive,
            state.SmallGyroActive,
            state.BuyAmmoRequested,
            state.EnergyActivationPressed,
            state.HeroDeployHoldPressed,
            state.SuperCapActive,
            state.SentryStanceToggleRequested);

    private static PlayerControlState FromLanInputFrame(LanInputFrame input)
        => new()
        {
            EntityId = input.EntityId,
            Enabled = input.Enabled,
            MoveForward = input.MoveForward,
            MoveRight = input.MoveRight,
            TurretYawDeltaDeg = input.TurretYawDeltaDeg,
            GimbalPitchDeltaDeg = input.GimbalPitchDeltaDeg,
            FirePressed = input.FirePressed,
            AutoAimPressed = input.AutoAimPressed,
            AutoAimGuidanceOnly = input.AutoAimGuidanceOnly,
            HeroLobAutoFireReady = input.HeroLobAutoFireReady,
            JumpRequested = input.JumpRequested,
            StepClimbModeActive = input.StepClimbModeActive,
            SmallGyroActive = input.SmallGyroActive,
            BuyAmmoRequested = input.BuyAmmoRequested,
            EnergyActivationPressed = input.EnergyActivationPressed,
            HeroDeployHoldPressed = input.HeroDeployHoldPressed,
            SuperCapActive = input.SuperCapActive,
            SentryStanceToggleRequested = input.SentryStanceToggleRequested,
        };

    private static void AddHash(ref ulong hash, string value)
    {
        foreach (char ch in value)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
    }

    private static void AddHash(ref ulong hash, double value)
        => AddHash(ref hash, value.ToString("R", CultureInfo.InvariantCulture));

    private static void AddHash(ref ulong hash, int value)
        => AddHash(ref hash, value.ToString(CultureInfo.InvariantCulture));
}
