namespace Simulator.Platform.Media;

public sealed class NullBackgroundVideoSource : IBackgroundVideoSource
{
    public static NullBackgroundVideoSource Instance { get; } = new();

    public double FrameIntervalSec => 1.0 / 90.0;

    private NullBackgroundVideoSource()
    {
    }

    public void Start(string path, Func<bool> shouldDecode)
    {
    }

    public void Stop()
    {
    }

    public bool TryGetLatestFrame(out BackgroundVideoFrame frame)
    {
        frame = null!;
        return false;
    }

    public void Dispose()
    {
    }
}
