namespace Simulator.Platform.Media;

public sealed record class BackgroundVideoFrame(int Width, int Height, long Version, byte[] Bgra32);

public interface IBackgroundVideoSource : IDisposable
{
    double FrameIntervalSec { get; }

    void Start(string path, Func<bool> shouldDecode);

    void Stop();

    bool TryGetLatestFrame(out BackgroundVideoFrame frame);
}
