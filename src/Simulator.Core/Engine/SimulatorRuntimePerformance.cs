using System.Diagnostics;
using System.Globalization;

namespace Simulator.Core.Engine;

public static class SimulatorRuntimePerformance
{
    public static long Timestamp()
        => Stopwatch.GetTimestamp();

    public static long ElapsedTicksSince(long startTicks)
        => Stopwatch.GetTimestamp() - startTicks;

    public static double TicksToMilliseconds(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;

    public static string FormatMilliseconds(long ticks)
        => TicksToMilliseconds(ticks).ToString("0.00", CultureInfo.InvariantCulture);

    public static string WallClockLabel()
        => DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static bool TryMarkInterval(ref long lastLogTicks, double minIntervalSec)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        if (lastLogTicks > 0
            && (nowTicks - lastLogTicks) / (double)Stopwatch.Frequency < minIntervalSec)
        {
            return false;
        }

        lastLogTicks = nowTicks;
        return true;
    }
}
