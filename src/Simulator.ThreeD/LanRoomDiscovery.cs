using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Simulator.ThreeD;

internal sealed record LanDiscoveredRoom(
    string RoomName,
    string HostName,
    string HostAddress,
    int Port,
    int ConnectedPlayers,
    int MaxPlayers,
    bool Private,
    DateTime LastSeenUtc);

internal sealed class LanRoomDiscoveryService : IDisposable
{
    private const string Protocol = "artinx-lan-room-v1";
    public const int DiscoveryPort = 26012;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, LanDiscoveredRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly UdpClient? _udp;
    private readonly Task? _receiveTask;
    private bool _disposed;

    private sealed record RoomAnnouncement(
        string Protocol,
        string RoomName,
        string HostName,
        string HostAddress,
        int Port,
        int ConnectedPlayers,
        int MaxPlayers,
        bool Private,
        bool Closed);

    public LanRoomDiscoveryService()
    {
        try
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _udp = new UdpClient { Client = socket, EnableBroadcast = true };
            _receiveTask = ReceiveLoopAsync(_cancellation.Token);
        }
        catch (SocketException)
        {
            _udp = null;
        }
    }

    public bool IsAvailable => _udp is not null;

    public IReadOnlyList<LanDiscoveredRoom> GetRooms()
    {
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(4.5);
        foreach ((string key, LanDiscoveredRoom room) in _rooms.ToArray())
        {
            if (room.LastSeenUtc < cutoff)
            {
                _rooms.TryRemove(key, out _);
            }
        }

        return _rooms.Values
            .OrderByDescending(room => room.LastSeenUtc)
            .ThenBy(room => room.RoomName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void AnnounceRoom(
        string roomName,
        string hostName,
        string hostAddress,
        int port,
        int connectedPlayers,
        int maxPlayers,
        bool privateRoom)
    {
        if (_udp is null || _disposed || privateRoom)
        {
            return;
        }

        var announcement = new RoomAnnouncement(
            Protocol,
            string.IsNullOrWhiteSpace(roomName) ? "ARTINX LAN Room" : roomName.Trim(),
            string.IsNullOrWhiteSpace(hostName) ? Environment.MachineName : hostName.Trim(),
            string.IsNullOrWhiteSpace(hostAddress) ? LanMultiplayerSession.ResolvePreferredLocalAddress() : hostAddress.Trim(),
            Math.Clamp(port, 1024, 65535),
            Math.Clamp(connectedPlayers, 0, maxPlayers),
            Math.Clamp(maxPlayers, 1, 10),
            privateRoom,
            Closed: false);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, JsonOptions));
        try
        {
            _ = _udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void WithdrawRoom(
        string roomName,
        string hostName,
        string hostAddress,
        int port)
    {
        string resolvedHostAddress = string.IsNullOrWhiteSpace(hostAddress)
            ? LanMultiplayerSession.ResolvePreferredLocalAddress()
            : hostAddress.Trim();
        string key = $"{resolvedHostAddress}:{Math.Clamp(port, 1024, 65535)}";
        _rooms.TryRemove(key, out _);
        if (_udp is null || _disposed)
        {
            return;
        }

        var announcement = new RoomAnnouncement(
            Protocol,
            string.IsNullOrWhiteSpace(roomName) ? "ARTINX LAN Room" : roomName.Trim(),
            string.IsNullOrWhiteSpace(hostName) ? Environment.MachineName : hostName.Trim(),
            resolvedHostAddress,
            Math.Clamp(port, 1024, 65535),
            0,
            10,
            Private: false,
            Closed: true);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, JsonOptions));
        try
        {
            _ = _udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_udp is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                break;
            }

            try
            {
                RoomAnnouncement? announcement = JsonSerializer.Deserialize<RoomAnnouncement>(
                    Encoding.UTF8.GetString(result.Buffer),
                    JsonOptions);
                if (announcement is null
                    || !string.Equals(announcement.Protocol, Protocol, StringComparison.OrdinalIgnoreCase)
                    || (announcement.Private && !announcement.Closed))
                {
                    continue;
                }

                string address = result.RemoteEndPoint.Address.ToString();
                string key = $"{address}:{announcement.Port}";
                if (announcement.Closed)
                {
                    _rooms.TryRemove(key, out _);
                    continue;
                }

                _rooms[key] = new LanDiscoveredRoom(
                    announcement.RoomName,
                    announcement.HostName,
                    address,
                    Math.Clamp(announcement.Port, 1024, 65535),
                    Math.Clamp(announcement.ConnectedPlayers, 0, announcement.MaxPlayers),
                    Math.Clamp(announcement.MaxPlayers, 1, 10),
                    announcement.Private,
                    DateTime.UtcNow);
            }
            catch (JsonException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _udp?.Dispose();
        _cancellation.Dispose();
    }
}
