namespace Simulator.Platform.Runtime;

public interface IFrameTicker : IDisposable
{
    void Start();

    void Stop();
}
