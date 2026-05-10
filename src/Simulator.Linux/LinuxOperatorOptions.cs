namespace Simulator.Linux;

internal sealed record LinuxOperatorOptions(
    string? MapPreset,
    int Width,
    int Height,
    bool HeadlessDiagnostics,
    double? ExitAfterSec)
{
    public static LinuxOperatorOptions Parse(IReadOnlyList<string> args)
    {
        string? mapPreset = null;
        int width = 1440;
        int height = 900;
        bool headlessDiagnostics = false;
        double? exitAfterSec = null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--map", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                mapPreset = args[++i];
            }
            else if (string.Equals(arg, "--size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                string[] parts = args[++i].Split('x', 'X');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int parsedWidth)
                    && int.TryParse(parts[1], out int parsedHeight))
                {
                    width = Math.Clamp(parsedWidth, 640, 7680);
                    height = Math.Clamp(parsedHeight, 480, 4320);
                }
            }
            else if (string.Equals(arg, "--diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                headlessDiagnostics = true;
            }
            else if (string.Equals(arg, "--exit-after", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                if (double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                {
                    exitAfterSec = Math.Clamp(parsed, 0.1, 120.0);
                }
            }
        }

        return new LinuxOperatorOptions(mapPreset, width, height, headlessDiagnostics, exitAfterSec);
    }
}
