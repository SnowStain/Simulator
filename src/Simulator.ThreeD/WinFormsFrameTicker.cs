using Simulator.Platform.Runtime;

namespace Simulator.ThreeD;

internal sealed class WinFormsFrameTicker : IFrameTicker
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly EventHandler _tickHandler;

    public WinFormsFrameTicker(int intervalMs, Action tick)
    {
        _tickHandler = (_, _) => tick();
        _timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1, intervalMs),
        };
        _timer.Tick += _tickHandler;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Tick -= _tickHandler;
        _timer.Dispose();
    }
}
