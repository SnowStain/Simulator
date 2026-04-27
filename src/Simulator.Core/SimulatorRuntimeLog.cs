using System.Collections.Concurrent;
using System.Text;

namespace Simulator.Core;

public static class SimulatorRuntimeLog
{
    private const int MaxQueuedLines = 4096;
    private static readonly ConcurrentQueue<(string FileName, string Line)> PendingLines = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static int _pendingCount;
    private static int _workerStarted;

    public static void Append(string fileName, string line)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        EnsureWorker();
        if (Interlocked.Increment(ref _pendingCount) > MaxQueuedLines)
        {
            Interlocked.Decrement(ref _pendingCount);
            return;
        }

        PendingLines.Enqueue((Path.GetFileName(fileName), line));
        Signal.Release();
    }

    private static void EnsureWorker()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAll();
        _ = Task.Run(ProcessQueueAsync);
    }

    private static async Task ProcessQueueAsync()
    {
        while (true)
        {
            try
            {
                await Signal.WaitAsync().ConfigureAwait(false);
                FlushOnce();
            }
            catch
            {
            }
        }
    }

    private static void FlushOnce()
        => FlushBatch(maxLines: 512);

    private static void FlushAll()
    {
        while (!PendingLines.IsEmpty)
        {
            FlushBatch(maxLines: int.MaxValue);
        }
    }

    private static void FlushBatch(int maxLines)
    {
        Dictionary<string, StringBuilder> batches = new(StringComparer.OrdinalIgnoreCase);
        int drained = 0;
        while (drained < maxLines && PendingLines.TryDequeue(out (string FileName, string Line) item))
        {
            Interlocked.Decrement(ref _pendingCount);
            drained++;
            if (!batches.TryGetValue(item.FileName, out StringBuilder? builder))
            {
                builder = new StringBuilder(2048);
                batches[item.FileName] = builder;
            }

            builder.AppendLine(item.Line);
        }

        if (batches.Count == 0)
        {
            return;
        }

        try
        {
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            foreach ((string fileName, StringBuilder builder) in batches)
            {
                File.AppendAllText(Path.Combine(logDirectory, fileName), builder.ToString());
            }
        }
        catch
        {
        }
    }
}
