using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Simulator.Core;

namespace Simulator.Linux;

internal static class LinuxOperatorApplication
{
    public static int Run(string[] args)
    {
        try
        {
            LinuxOperatorOptions options = LinuxOperatorOptions.Parse(args);
            var gameSettings = new GameWindowSettings
            {
                UpdateFrequency = 240.0,
            };
            var nativeSettings = new NativeWindowSettings
            {
                Title = $"RMUC 2026 Linux Operator - {options.MapPreset ?? "active"}",
                ClientSize = new Vector2i(options.Width, options.Height),
                APIVersion = new Version(4, 1),
                Profile = ContextProfile.Compatability,
                Flags = ContextFlags.Default,
                NumberOfSamples = 4,
            };

            using var window = new LinuxOperatorWindow(gameSettings, nativeSettings, options);
            window.Run();
            return 0;
        }
        catch (Exception exception) when (exception is GLFWException or InvalidOperationException)
        {
            SimulatorRuntimeLog.Append("linux_operator.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} startup_failed {exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine($"Linux operator failed: {exception.Message}");
            return 1;
        }
    }
}
