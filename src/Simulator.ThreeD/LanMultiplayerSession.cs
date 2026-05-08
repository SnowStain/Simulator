using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Simulator.ThreeD;

internal enum LanPeerRole
{
    Host,
    Guest,
}

internal enum LanMatchPeerRole
{
    Referee,
    Player,
    Spectator,
}

internal sealed record LanSeatClaim(
    long Sequence,
    string PlayerId,
    string PlayerName,
    string SeatId,
    string Team,
    string EntityKey,
    bool Ready,
    string Role = "player",
    int SpawnPointIndex = 0,
    string ChassisMode = "");

internal sealed record LanMatchSeatState(
    string SeatId,
    string Team,
    int RobotNumber,
    string EntityKey,
    string PlayerId,
    string PlayerName,
    bool Connected,
    bool Ready,
    string Role = "player",
    int SpawnPointIndex = 0,
    string ChassisMode = "");

internal sealed record LanRoomRoster(
    long Sequence,
    string MatchId,
    IReadOnlyList<LanMatchSeatState> Seats,
    LanRoomGameSettings? Settings = null);

internal sealed record LanRoomGameSettings(
    string MatchMode = "uc",
    int SmallBulletDamage = 20,
    int LargeBulletDamage = 200,
    int HeroStartLevel = 1,
    int EngineerStartLevel = 1,
    int InfantryStartLevel = 1,
    int RedStartGold = 400,
    int BlueStartGold = 400);

internal sealed record LanInputFrame(
    long Sequence,
    string EntityId,
    bool Enabled,
    double MoveForward,
    double MoveRight,
    double TurretYawDeltaDeg,
    double GimbalPitchDeltaDeg,
    bool FirePressed,
    bool AutoAimPressed,
    bool AutoAimGuidanceOnly,
    bool HeroLobAutoFireReady,
    bool JumpRequested,
    bool StepClimbModeActive,
    bool SmallGyroActive,
    bool BuyAmmoRequested,
    bool EnergyActivationPressed,
    bool HeroDeployHoldPressed,
    bool SuperCapActive,
    bool SentryStanceToggleRequested);

internal sealed record LanPlayerInputFrame(
    long Sequence,
    string PlayerId,
    string SeatId,
    string EntityId,
    long SimulationTick,
    double ClientTimeSec,
    LanInputFrame Input);

internal sealed record LanLobbySelection(
    long Sequence,
    string PlayerName,
    string Team,
    string EntityKey,
    int SlotIndex = 0,
    string MemberRole = "player",
    string PlayerId = "",
    int SpawnPointIndex = 0,
    string ChassisMode = "");

internal sealed record LanStartMatchCommand(
    string MatchMode,
    string MapPreset,
    string HostTeam,
    string GuestTeam,
    string HostEntityKey,
    string GuestEntityKey,
    string HeroPerformanceMode,
    string InfantryMode,
    string InfantryDurabilityMode,
    string InfantryWeaponMode,
    string SentryControlMode,
    string SentryStance,
    double AutoAimAccuracyScale,
    double DisplayLatencyMs,
    int DuelRoundLimit,
    string ProjectilePhysicsBackend,
    long StartSequence);

internal sealed record LanValidationDigest(
    long Sequence,
    double GameTimeSec,
    int EntityCount,
    int ProjectileCount,
    ulong Hash,
    string Summary);

internal sealed record LanWorldSnapshot(
    long Sequence,
    double GameTimeSec,
    string AuthoritativeEntityId,
    string AuthoritativeTeam,
    IReadOnlyList<LanEntitySnapshot> Entities);

internal sealed record LanEntitySnapshot(
    string Id,
    string Team,
    double X,
    double Y,
    double AngleDeg,
    double TurretYawDeg,
    double GimbalPitchDeg,
    double VelocityXWorldPerSec,
    double VelocityYWorldPerSec,
    double AngularVelocityDegPerSec,
    double CurrentHealth,
    double Power,
    double Heat,
    int Ammo17Mm,
    int Ammo42Mm,
    int ShotsFired,
    int FortReserveAmmo,
    int FortReserveAmmoCap,
    bool IsAlive,
    bool IsPlayerControlled);

internal sealed record LanProjectileSnapshot(
    string Id,
    string ShooterId,
    string Team,
    string AmmoType,
    double X,
    double Y,
    double HeightM,
    double VelocityXWorldPerSec,
    double VelocityYWorldPerSec,
    double VelocityZMps);

internal sealed record LanTeamSnapshot(
    string Team,
    double Gold,
    int Score,
    double BaseHealth,
    double OutpostHealth,
    string EnergyMechanismState,
    bool BaseArmorForcedOpen);

internal sealed record LanAuthoritativeMatchSnapshot(
    long Sequence,
    long SimulationTick,
    double GameTimeSec,
    string MatchPhase,
    IReadOnlyList<LanEntitySnapshot> Entities,
    IReadOnlyList<LanProjectileSnapshot> Projectiles,
    IReadOnlyList<LanTeamSnapshot> Teams,
    string StartupPhase = "",
    double StartupPhaseElapsedSec = 0.0,
    double StartupPhaseDurationSec = 0.0,
    bool StartupActive = false,
    bool Paused = false);

internal sealed record LanLocalRefereeReport(
    long Sequence,
    string ReporterPlayerId,
    string ReporterSeatId,
    string ObservedEntityId,
    string RuleId,
    string Category,
    string Severity,
    double LocalGameTimeSec,
    long SimulationTick,
    string EvidenceJson,
    string SuggestedAction);

internal sealed record LanRefereeDecision(
    long Sequence,
    string ReportId,
    string RuleId,
    string TargetEntityId,
    string TargetTeam,
    bool Accepted,
    string PenaltyCode,
    double HealthDelta,
    double GoldDelta,
    double TimeoutSec,
    string Message);

internal sealed record LanReliableMatchEvent(
    long Sequence,
    long SimulationTick,
    double GameTimeSec,
    string EventType,
    string TargetId,
    string PayloadJson);

internal sealed record LanTrafficReport(
    long Sequence,
    string PlayerId,
    string PlayerName,
    string Role,
    bool IsHost,
    double LocalTimeSec,
    double SentMegabitsPerSecond,
    double ReceivedMegabitsPerSecond,
    double SentKilobytesPerSecond,
    double ReceivedKilobytesPerSecond,
    long TotalSentBytes,
    long TotalReceivedBytes,
    long TotalSentMessages,
    long TotalReceivedMessages,
    long TotalDroppedRealtimeMessages,
    long TotalDroppedReliableMessages,
    string TopSentType,
    string TopReceivedType,
    string TopDroppedType,
    string SentTypeBreakdown = "",
    string ReceivedTypeBreakdown = "",
    string DroppedTypeBreakdown = "");

internal sealed record LanSessionEvent(
    string Type,
    string Message,
    LanInputFrame? Input = null,
    LanSeatClaim? SeatClaim = null,
    LanRoomRoster? Roster = null,
    LanPlayerInputFrame? PlayerInput = null,
    LanLobbySelection? LobbySelection = null,
    LanStartMatchCommand? StartMatch = null,
    LanValidationDigest? Digest = null,
    LanWorldSnapshot? Snapshot = null,
    LanAuthoritativeMatchSnapshot? AuthoritativeSnapshot = null,
    LanLocalRefereeReport? RefereeReport = null,
    LanRefereeDecision? RefereeDecision = null,
    LanReliableMatchEvent? MatchEvent = null,
    LanTrafficReport? TrafficReport = null,
    string? PeerPlayerId = null,
    string? PeerPlayerName = null);

internal sealed class LanMultiplayerSession : IDisposable
{
    private const int MinLanRoomPort = 1024;
    private const int MaxLanRoomPort = 65535;
    private const int HostPortFallbackProbeCount = 48;
    private const int MaxLanPlayerClients = 10;
    private const string ProtocolVersion = "lan-5v5-v1";
    private static readonly TimeSpan ReliableSendLockTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RealtimeSendLockTimeout = TimeSpan.Zero;

    private sealed record WireEnvelope(string Type, JsonElement Payload);

    private sealed record HelloMessage(string PlayerName, string RoomName, string Version, string PlayerId);

    private sealed record WelcomeMessage(
        string RoomName,
        string HostTeam,
        string GuestTeam,
        string Version,
        int MaxPlayers,
        string HostRole,
        string PeerId);

    private sealed class PeerConnection : IDisposable
    {
        public PeerConnection(TcpClient client)
        {
            Client = client;
            NetworkStream stream = client.GetStream();
            Reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            Writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            EndPoint = client.Client.RemoteEndPoint?.ToString() ?? string.Empty;
        }

        public TcpClient Client { get; }

        public StreamReader Reader { get; }

        public StreamWriter Writer { get; }

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public string EndPoint { get; }

        public string PeerId { get; } = Guid.NewGuid().ToString("N");

        public string PlayerId { get; set; } = string.Empty;

        public string PlayerName { get; set; } = string.Empty;

        public void Dispose()
        {
            Reader.Dispose();
            Writer.Dispose();
            Client.Close();
            SendLock.Dispose();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<LanSessionEvent> _events = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<PeerConnection> _hostPeers = new();
    private readonly object _hostPeersLock = new();
    private readonly LanTrafficTelemetry _trafficTelemetry = new();
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _acceptTask;
    private Task? _readTask;
    private volatile bool _disposed;

    public LanMultiplayerSession(LanPeerRole role, string roomName, string playerName)
    {
        Role = role;
        RoomName = string.IsNullOrWhiteSpace(roomName) ? "ARTINX 5v5 Room" : roomName.Trim();
        PlayerName = string.IsNullOrWhiteSpace(playerName) ? Environment.UserName : playerName.Trim();
        LocalTeam = role == LanPeerRole.Host ? "red" : "blue";
        RemoteTeam = role == LanPeerRole.Host ? "blue" : "red";
    }

    public LanPeerRole Role { get; }

    public string RoomName { get; }

    public string PlayerName { get; }

    public string LocalPlayerId { get; } = Guid.NewGuid().ToString("N");

    public string LocalTeam { get; }

    public string RemoteTeam { get; }

    public bool IsHost => Role == LanPeerRole.Host;

    public bool IsConnected => IsHost ? ConnectedPeerCount > 0 : _client?.Connected == true;

    public int ConnectedPeerCount
    {
        get
        {
            if (!IsHost)
            {
                return _client?.Connected == true ? 1 : 0;
            }

            lock (_hostPeersLock)
            {
                return _hostPeers.Count(peer => peer.Client.Connected);
            }
        }
    }

    public int MaxPlayerClients => MaxLanPlayerClients;

    public int Port { get; private set; }

    public string? RemoteEndPoint { get; private set; }

    public string StatusText { get; private set; } = "未连接";

    public static string ResolvePreferredLocalAddress()
    {
        try
        {
            foreach (IPAddress address in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address))
                {
                    return address.ToString();
                }
            }
        }
        catch (SocketException)
        {
        }

        return "127.0.0.1";
    }

    public Task StartHostAsync(int port)
    {
        ThrowIfDisposed();
        int requestedPort = Math.Clamp(port, MinLanRoomPort, MaxLanRoomPort);
        _cancellation = new CancellationTokenSource();
        _listener = StartHostListenerWithFallback(requestedPort);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        StatusText = $"5v5 裁判主机已创建，监听 {ResolvePreferredLocalAddress()}:{Port}";
        EnqueueStatus(StatusText);
        _acceptTask = AcceptGuestsAsync(_cancellation.Token);
        return Task.CompletedTask;
    }

    private static TcpListener StartHostListenerWithFallback(int requestedPort)
    {
        SocketException? lastSocketException = null;
        foreach (int candidatePort in EnumerateHostPortCandidates(requestedPort))
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, candidatePort);
                listener.Start(MaxLanPlayerClients);
                return listener;
            }
            catch (SocketException exception)
            {
                lastSocketException = exception;
                try
                {
                    listener?.Stop();
                }
                catch (SocketException)
                {
                }
            }
        }

        throw lastSocketException ?? new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private static IEnumerable<int> EnumerateHostPortCandidates(int requestedPort)
    {
        int endPort = Math.Min(MaxLanRoomPort, requestedPort + HostPortFallbackProbeCount - 1);
        for (int candidatePort = requestedPort; candidatePort <= endPort; candidatePort++)
        {
            yield return candidatePort;
        }

        yield return 0;
    }

    public async Task JoinAsync(string hostAddress, int port)
    {
        ThrowIfDisposed();
        Port = Math.Clamp(port, 1024, 65535);
        _cancellation = new CancellationTokenSource();
        _client = new TcpClient
        {
            NoDelay = true,
        };
        StatusText = $"连接 {hostAddress}:{Port} 中...";
        EnqueueStatus(StatusText);
        await _client.ConnectAsync(hostAddress, Port, _cancellation.Token).ConfigureAwait(false);
        ConfigureConnection(_client);
        await SendAsync(LanProtocolMessageTypes.Hello, new HelloMessage(PlayerName, RoomName, ProtocolVersion, LocalPlayerId)).ConfigureAwait(false);
        StatusText = $"已连接裁判主机 {RemoteEndPoint}";
        EnqueueStatus(StatusText);
        _readTask = ReadLoopAsync(_cancellation.Token);
    }

    public IReadOnlyList<LanSessionEvent> DrainEvents()
    {
        var events = new List<LanSessionEvent>();
        while (_events.TryDequeue(out LanSessionEvent? item))
        {
            events.Add(item);
        }

        return events;
    }

    public LanTrafficSnapshot CaptureTrafficSnapshot()
        => _trafficTelemetry.Capture();

    public Task SendInputAsync(LanInputFrame input)
        => SendAsync(LanProtocolMessageTypes.Input, input);

    public Task SendSeatClaimAsync(LanSeatClaim claim)
        => SendAsync(LanProtocolMessageTypes.SeatClaim, claim);

    public Task SendRosterAsync(LanRoomRoster roster)
        => SendAsync(LanProtocolMessageTypes.Roster, roster);

    public Task SendPlayerInputAsync(LanPlayerInputFrame input)
        => SendAsync(LanProtocolMessageTypes.PlayerInput, input);

    public Task SendLobbySelectionAsync(LanLobbySelection selection)
        => SendAsync(LanProtocolMessageTypes.LobbySelection, selection);

    public Task SendStartMatchAsync(LanStartMatchCommand command)
        => SendAsync(LanProtocolMessageTypes.StartMatch, command);

    public Task SendDigestAsync(LanValidationDigest digest)
        => SendAsync(LanProtocolMessageTypes.Digest, digest);

    public Task SendSnapshotAsync(LanWorldSnapshot snapshot)
        => SendAsync(LanProtocolMessageTypes.Snapshot, snapshot);

    public Task SendAuthoritativeSnapshotAsync(LanAuthoritativeMatchSnapshot snapshot)
        => SendAsync(LanProtocolMessageTypes.AuthoritativeSnapshot, snapshot);

    public Task SendRefereeReportAsync(LanLocalRefereeReport report)
        => SendAsync(LanProtocolMessageTypes.RefereeReport, report);

    public Task SendRefereeDecisionAsync(LanRefereeDecision decision)
        => SendAsync(LanProtocolMessageTypes.RefereeDecision, decision);

    public Task SendMatchEventAsync(LanReliableMatchEvent matchEvent)
        => SendAsync(LanProtocolMessageTypes.MatchEvent, matchEvent);

    public Task SendTrafficReportAsync(LanTrafficReport report)
        => SendAsync(LanProtocolMessageTypes.TrafficReport, report);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Close();
        _listener?.Stop();
        lock (_hostPeersLock)
        {
            foreach (PeerConnection peer in _hostPeers)
            {
                peer.Dispose();
            }

            _hostPeers.Clear();
        }

        _sendLock.Dispose();
        _cancellation?.Dispose();
    }

    private async Task AcceptGuestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                PeerConnection peer = new(client);
                bool accepted;
                int peerCount;
                lock (_hostPeersLock)
                {
                    accepted = _hostPeers.Count < MaxLanPlayerClients;
                    if (accepted)
                    {
                        _hostPeers.Add(peer);
                        _client ??= client;
                        RemoteEndPoint = peer.EndPoint;
                    }

                    peerCount = _hostPeers.Count;
                }

                if (!accepted)
                {
                    peer.Dispose();
                    EnqueueStatus("5v5 房间已满，拒绝新的玩家连接");
                    continue;
                }

                StatusText = $"玩家已加入 {peer.EndPoint}  ({peerCount}/{MaxLanPlayerClients})";
                EnqueueStatus(StatusText);
                await SendToPeerAsync(
                    peer,
                    LanProtocolMessageTypes.Welcome,
                    new WelcomeMessage(RoomName, "red", "blue", ProtocolVersion, MaxLanPlayerClients, "referee", peer.PeerId)).ConfigureAwait(false);
                _ = ReadLoopAsync(peer, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
        {
            EnqueueError($"房间监听中断：{exception.Message}");
        }
    }

    private void ConfigureConnection(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader is not null)
            {
                string? line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                HandleWireLine(null, line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or JsonException or ObjectDisposedException)
        {
            EnqueueError($"连接读取中断：{exception.Message}");
        }

        if (!_disposed)
        {
            StatusText = "连接已断开";
            EnqueueStatus(StatusText);
            _events.Enqueue(new LanSessionEvent("host_disconnected", StatusText));
        }
    }

    private async Task ReadLoopAsync(PeerConnection peer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await peer.Reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                HandleWireLine(peer, line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or JsonException or ObjectDisposedException)
        {
            EnqueueError($"玩家连接读取中断：{exception.Message}");
        }
        finally
        {
            int peerCount;
            lock (_hostPeersLock)
            {
                _hostPeers.Remove(peer);
                peerCount = _hostPeers.Count;
            }

            string endpoint = peer.EndPoint;
            string disconnectedPlayerName = string.IsNullOrWhiteSpace(peer.PlayerName) ? endpoint : peer.PlayerName.Trim();
            peer.Dispose();
            if (!_disposed)
            {
                StatusText = $"玩家离线：{disconnectedPlayerName}  ({peerCount}/{MaxLanPlayerClients})";
                EnqueueStatus(StatusText);
                _events.Enqueue(new LanSessionEvent(
                    "peer_disconnected",
                    StatusText,
                    PeerPlayerId: peer.PlayerId,
                    PeerPlayerName: peer.PlayerName));
            }
        }
    }

    private void HandleWireLine(PeerConnection? peer, string line)
    {
        WireEnvelope? envelope = JsonSerializer.Deserialize<WireEnvelope>(line, JsonOptions);
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Type))
        {
            return;
        }

        _trafficTelemetry.RecordReceived(envelope.Type, LanTrafficTelemetry.EstimateWireBytes(line));

        switch (envelope.Type)
        {
            case LanProtocolMessageTypes.Hello:
            {
                HelloMessage? hello = envelope.Payload.Deserialize<HelloMessage>(JsonOptions);
                if (peer is not null && hello is not null)
                {
                    peer.PlayerName = hello.PlayerName ?? string.Empty;
                    peer.PlayerId = hello.PlayerId ?? string.Empty;
                }
                EnqueueStatus($"玩家加入：{hello?.PlayerName ?? "Guest"}");
                break;
            }
            case LanProtocolMessageTypes.Welcome:
            {
                WelcomeMessage? welcome = envelope.Payload.Deserialize<WelcomeMessage>(JsonOptions);
                EnqueueStatus($"加入房间：{welcome?.RoomName ?? RoomName}");
                break;
            }
            case LanProtocolMessageTypes.Input:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.Input,
                    "收到远端输入",
                    Input: envelope.Payload.Deserialize<LanInputFrame>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.SeatClaim:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.SeatClaim,
                    "remote seat claim",
                    SeatClaim: envelope.Payload.Deserialize<LanSeatClaim>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.Roster:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.Roster,
                    "room roster",
                    Roster: envelope.Payload.Deserialize<LanRoomRoster>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.PlayerInput:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.PlayerInput,
                    "player input",
                    PlayerInput: envelope.Payload.Deserialize<LanPlayerInputFrame>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.LobbySelection:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.LobbySelection,
                    "收到远端房间选择",
                    LobbySelection: envelope.Payload.Deserialize<LanLobbySelection>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.StartMatch:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.StartMatch,
                    "主机开始对局",
                    StartMatch: envelope.Payload.Deserialize<LanStartMatchCommand>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.Digest:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.Digest,
                    "收到规则摘要",
                    Digest: envelope.Payload.Deserialize<LanValidationDigest>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.Snapshot:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.Snapshot,
                    "收到主机快照",
                    Snapshot: envelope.Payload.Deserialize<LanWorldSnapshot>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.AuthoritativeSnapshot:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.AuthoritativeSnapshot,
                    "authoritative snapshot",
                    AuthoritativeSnapshot: envelope.Payload.Deserialize<LanAuthoritativeMatchSnapshot>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.RefereeReport:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.RefereeReport,
                    "local referee report",
                    RefereeReport: envelope.Payload.Deserialize<LanLocalRefereeReport>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.RefereeDecision:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.RefereeDecision,
                    "referee decision",
                    RefereeDecision: envelope.Payload.Deserialize<LanRefereeDecision>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.MatchEvent:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.MatchEvent,
                    "reliable match event",
                    MatchEvent: envelope.Payload.Deserialize<LanReliableMatchEvent>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.TrafficReport:
                _events.Enqueue(new LanSessionEvent(
                    LanProtocolMessageTypes.TrafficReport,
                    "traffic report",
                    TrafficReport: envelope.Payload.Deserialize<LanTrafficReport>(JsonOptions)));
                break;
            case LanProtocolMessageTypes.Status:
                EnqueueStatus(envelope.Payload.GetString() ?? "远端状态更新");
                break;
        }
    }

    private async Task SendAsync<T>(string type, T payload)
    {
        if (_disposed)
        {
            return;
        }

        if (IsHost)
        {
            await BroadcastAsync(type, payload).ConfigureAwait(false);
            return;
        }

        if (_writer is null)
        {
            return;
        }

        bool dropIfBusy = ShouldDropLanMessageWhenSendBusy(type);
        bool acquired = dropIfBusy
            ? await _sendLock.WaitAsync(RealtimeSendLockTimeout).ConfigureAwait(false)
            : await _sendLock.WaitAsync(ReliableSendLockTimeout).ConfigureAwait(false);
        if (!acquired)
        {
            _trafficTelemetry.RecordDropped(type, realtime: dropIfBusy);
            if (!dropIfBusy)
            {
                EnqueueError($"send busy, dropped reliable message {type}");
            }

            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(new { type, payload }, JsonOptions);
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
            _trafficTelemetry.RecordSent(type, LanTrafficTelemetry.EstimateWireBytes(json));
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            EnqueueError($"发送失败：{exception.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task BroadcastAsync<T>(string type, T payload)
    {
        PeerConnection[] peers;
        lock (_hostPeersLock)
        {
            peers = _hostPeers.ToArray();
        }

        await Task.WhenAll(peers.Select(peer => SendToPeerAsync(peer, type, payload))).ConfigureAwait(false);
    }

    private async Task SendToPeerAsync<T>(PeerConnection peer, string type, T payload)
    {
        bool dropIfBusy = ShouldDropLanMessageWhenSendBusy(type);
        bool acquired = dropIfBusy
            ? await peer.SendLock.WaitAsync(RealtimeSendLockTimeout).ConfigureAwait(false)
            : await peer.SendLock.WaitAsync(ReliableSendLockTimeout).ConfigureAwait(false);
        if (!acquired)
        {
            _trafficTelemetry.RecordDropped(type, realtime: dropIfBusy);
            if (!dropIfBusy)
            {
                EnqueueError($"send busy, dropped reliable message {type}");
            }

            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(new { type, payload }, JsonOptions);
            await peer.Writer.WriteLineAsync(json).ConfigureAwait(false);
            _trafficTelemetry.RecordSent(type, LanTrafficTelemetry.EstimateWireBytes(json));
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            EnqueueError($"发送失败：{exception.Message}");
        }
        finally
        {
            peer.SendLock.Release();
        }
    }

    private static bool ShouldDropLanMessageWhenSendBusy(string type)
        => LanProtocolMetadata.Describe(type).Delivery == LanProtocolDelivery.Realtime;

    private void EnqueueStatus(string message)
        => _events.Enqueue(new LanSessionEvent(LanProtocolMessageTypes.Status, message));

    private void EnqueueError(string message)
    {
        StatusText = message;
        _events.Enqueue(new LanSessionEvent(LanProtocolMessageTypes.Error, message));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LanMultiplayerSession));
        }
    }
}
