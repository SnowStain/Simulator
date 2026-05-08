using System.Collections.Concurrent;
using System.Text;

namespace Simulator.ThreeD;

internal sealed record LanTrafficSnapshot(
    DateTime CapturedAtUtc,
    long TotalSentBytes,
    long TotalReceivedBytes,
    long TotalSentMessages,
    long TotalReceivedMessages,
    long TotalDroppedRealtimeMessages,
    long TotalDroppedReliableMessages,
    double SentKilobytesPerSecond,
    double ReceivedKilobytesPerSecond,
    double SentMegabitsPerSecond,
    double ReceivedMegabitsPerSecond,
    IReadOnlyDictionary<string, long> SentMessagesByType,
    IReadOnlyDictionary<string, long> ReceivedMessagesByType,
    IReadOnlyDictionary<string, long> DroppedMessagesByType);

internal sealed class LanTrafficTelemetry
{
    private const int MaxSamples = 512;
    private readonly object _gate = new();
    private readonly Queue<TrafficSample> _samples = new();
    private readonly ConcurrentDictionary<string, long> _sentByType = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _receivedByType = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _droppedByType = new(StringComparer.OrdinalIgnoreCase);
    private long _totalSentBytes;
    private long _totalReceivedBytes;
    private long _totalSentMessages;
    private long _totalReceivedMessages;
    private long _totalDroppedRealtimeMessages;
    private long _totalDroppedReliableMessages;

    public void RecordSent(string type, int byteCount)
    {
        int safeBytes = Math.Max(0, byteCount);
        Interlocked.Add(ref _totalSentBytes, safeBytes);
        Interlocked.Increment(ref _totalSentMessages);
        _sentByType.AddOrUpdate(NormalizeType(type), 1, (_, current) => current + 1);
        AddSample(sentBytes: safeBytes, receivedBytes: 0);
    }

    public void RecordReceived(string type, int byteCount)
    {
        int safeBytes = Math.Max(0, byteCount);
        Interlocked.Add(ref _totalReceivedBytes, safeBytes);
        Interlocked.Increment(ref _totalReceivedMessages);
        _receivedByType.AddOrUpdate(NormalizeType(type), 1, (_, current) => current + 1);
        AddSample(sentBytes: 0, receivedBytes: safeBytes);
    }

    public void RecordDropped(string type, bool realtime)
    {
        if (realtime)
        {
            Interlocked.Increment(ref _totalDroppedRealtimeMessages);
        }
        else
        {
            Interlocked.Increment(ref _totalDroppedReliableMessages);
        }

        _droppedByType.AddOrUpdate(NormalizeType(type), 1, (_, current) => current + 1);
    }

    public LanTrafficSnapshot Capture(double windowSec = 1.0)
    {
        DateTime now = DateTime.UtcNow;
        double safeWindowSec = Math.Clamp(windowSec, 0.25, 10.0);
        long sentBytes = 0;
        long receivedBytes = 0;
        lock (_gate)
        {
            TrimSamples(now, Math.Max(10.0, safeWindowSec));
            foreach (TrafficSample sample in _samples)
            {
                if ((now - sample.TimestampUtc).TotalSeconds <= safeWindowSec)
                {
                    sentBytes += sample.SentBytes;
                    receivedBytes += sample.ReceivedBytes;
                }
            }
        }

        double sentKbps = sentBytes / 1000.0 / safeWindowSec;
        double receivedKbps = receivedBytes / 1000.0 / safeWindowSec;
        return new LanTrafficSnapshot(
            now,
            Interlocked.Read(ref _totalSentBytes),
            Interlocked.Read(ref _totalReceivedBytes),
            Interlocked.Read(ref _totalSentMessages),
            Interlocked.Read(ref _totalReceivedMessages),
            Interlocked.Read(ref _totalDroppedRealtimeMessages),
            Interlocked.Read(ref _totalDroppedReliableMessages),
            sentKbps,
            receivedKbps,
            sentKbps * 8.0 / 1000.0,
            receivedKbps * 8.0 / 1000.0,
            SnapshotDictionary(_sentByType),
            SnapshotDictionary(_receivedByType),
            SnapshotDictionary(_droppedByType));
    }

    public static int EstimateWireBytes(string json)
        => Encoding.UTF8.GetByteCount(json) + 1;

    private void AddSample(int sentBytes, int receivedBytes)
    {
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            _samples.Enqueue(new TrafficSample(now, sentBytes, receivedBytes));
            TrimSamples(now, 10.0);
            while (_samples.Count > MaxSamples)
            {
                _samples.Dequeue();
            }
        }
    }

    private void TrimSamples(DateTime now, double keepSec)
    {
        while (_samples.Count > 0 && (now - _samples.Peek().TimestampUtc).TotalSeconds > keepSec)
        {
            _samples.Dequeue();
        }
    }

    private static string NormalizeType(string type)
        => string.IsNullOrWhiteSpace(type) ? "unknown" : type.Trim();

    private static IReadOnlyDictionary<string, long> SnapshotDictionary(ConcurrentDictionary<string, long> dictionary)
        => dictionary
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private readonly record struct TrafficSample(DateTime TimestampUtc, int SentBytes, int ReceivedBytes);
}
