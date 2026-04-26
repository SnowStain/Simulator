using System.Collections.Concurrent;

namespace Simulator.Core.Architecture;

public enum SimulationLayer
{
    Engine,
    Rules,
    User,
}

public readonly record struct BusEnvelope<TMessage>(
    SimulationLayer From,
    SimulationLayer To,
    TMessage Message,
    double GameTimeSec = 0.0);

public interface IMessageBus
{
    IDisposable Subscribe<TMessage>(Action<BusEnvelope<TMessage>> handler);

    void Publish<TMessage>(BusEnvelope<TMessage> message);
}

public sealed class MessageBus : IMessageBus
{
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Guid, Delegate>> _subscriptions = new();

    public IDisposable Subscribe<TMessage>(Action<BusEnvelope<TMessage>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Guid token = Guid.NewGuid();
        ConcurrentDictionary<Guid, Delegate> bucket = _subscriptions.GetOrAdd(
            typeof(TMessage),
            static _ => new ConcurrentDictionary<Guid, Delegate>());
        bucket[token] = handler;
        return new Subscription(() =>
        {
            if (_subscriptions.TryGetValue(typeof(TMessage), out ConcurrentDictionary<Guid, Delegate>? handlers))
            {
                handlers.TryRemove(token, out _);
            }
        });
    }

    public void Publish<TMessage>(BusEnvelope<TMessage> message)
    {
        if (!_subscriptions.TryGetValue(typeof(TMessage), out ConcurrentDictionary<Guid, Delegate>? handlers))
        {
            return;
        }

        foreach (Delegate handler in handlers.Values)
        {
            if (handler is Action<BusEnvelope<TMessage>> typedHandler)
            {
                typedHandler(message);
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}

public readonly record struct RenderFrameRequested(string ViewId, string RenderMode);

public readonly record struct RuleTickCompleted(double DeltaTimeSec, int EntityCount);

public readonly record struct UserCommandIssued(string UserId, string Command, string? Payload = null);
