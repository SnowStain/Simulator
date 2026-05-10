namespace Simulator.Runtime.Input;

public sealed class GameInputSnapshotAccumulator
{
    private readonly HashSet<GameKey> _downKeys = new();
    private readonly HashSet<GameMouseButton> _downMouseButtons = new();
    private long _frame;
    private double _lastPointerX;
    private double _lastPointerY;
    private bool _hasPointer;

    public GameInputSnapshot CaptureState(
        double timeSec,
        IEnumerable<GameKey> downKeys,
        IEnumerable<GameMouseButton> downMouseButtons,
        GamePointerState pointer)
    {
        var nextKeys = new HashSet<GameKey>(downKeys.Where(key => key != GameKey.None));
        var nextButtons = new HashSet<GameMouseButton>(downMouseButtons.Where(button => button != GameMouseButton.None));
        var pressedKeys = new HashSet<GameKey>(nextKeys);
        pressedKeys.ExceptWith(_downKeys);
        var releasedKeys = new HashSet<GameKey>(_downKeys);
        releasedKeys.ExceptWith(nextKeys);
        var pressedButtons = new HashSet<GameMouseButton>(nextButtons);
        pressedButtons.ExceptWith(_downMouseButtons);
        var releasedButtons = new HashSet<GameMouseButton>(_downMouseButtons);
        releasedButtons.ExceptWith(nextButtons);

        _downKeys.Clear();
        _downKeys.UnionWith(nextKeys);
        _downMouseButtons.Clear();
        _downMouseButtons.UnionWith(nextButtons);
        RememberPointer(pointer);

        return CreateSnapshot(timeSec, pressedKeys, releasedKeys, pressedButtons, releasedButtons, pointer);
    }

    public GameInputSnapshot CaptureKey(
        double timeSec,
        GameKey key,
        bool down,
        GamePointerState pointer)
    {
        var pressed = new HashSet<GameKey>();
        var released = new HashSet<GameKey>();
        if (key != GameKey.None)
        {
            if (down)
            {
                if (_downKeys.Add(key))
                {
                    pressed.Add(key);
                }
            }
            else if (_downKeys.Remove(key))
            {
                released.Add(key);
            }
        }

        RememberPointer(pointer);
        return CreateSnapshot(timeSec, pressed, released, Array.Empty<GameMouseButton>(), Array.Empty<GameMouseButton>(), pointer);
    }

    public GameInputSnapshot CaptureMouseButton(
        double timeSec,
        GameMouseButton button,
        bool down,
        GamePointerState pointer)
    {
        var pressed = new HashSet<GameMouseButton>();
        var released = new HashSet<GameMouseButton>();
        if (button != GameMouseButton.None)
        {
            if (down)
            {
                if (_downMouseButtons.Add(button))
                {
                    pressed.Add(button);
                }
            }
            else if (_downMouseButtons.Remove(button))
            {
                released.Add(button);
            }
        }

        RememberPointer(pointer);
        return CreateSnapshot(timeSec, Array.Empty<GameKey>(), Array.Empty<GameKey>(), pressed, released, pointer);
    }

    public GameInputSnapshot CapturePointer(double timeSec, GamePointerState pointer)
    {
        RememberPointer(pointer);
        return CreateSnapshot(
            timeSec,
            Array.Empty<GameKey>(),
            Array.Empty<GameKey>(),
            Array.Empty<GameMouseButton>(),
            Array.Empty<GameMouseButton>(),
            pointer);
    }

    public GameInputSnapshot CaptureWheel(double timeSec, GamePointerState pointer)
    {
        RememberPointer(pointer);
        return CreateSnapshot(
            timeSec,
            Array.Empty<GameKey>(),
            Array.Empty<GameKey>(),
            Array.Empty<GameMouseButton>(),
            Array.Empty<GameMouseButton>(),
            pointer);
    }

    public GameInputSnapshot ReleaseAll(double timeSec, GamePointerState pointer)
    {
        var releasedKeys = new HashSet<GameKey>(_downKeys);
        var releasedButtons = new HashSet<GameMouseButton>(_downMouseButtons);
        _downKeys.Clear();
        _downMouseButtons.Clear();
        RememberPointer(pointer);
        return CreateSnapshot(
            timeSec,
            Array.Empty<GameKey>(),
            releasedKeys,
            Array.Empty<GameMouseButton>(),
            releasedButtons,
            pointer);
    }

    public GamePointerState BuildPointer(
        double x,
        double y,
        double wheelDelta,
        bool cursorCaptured,
        double? deltaX = null,
        double? deltaY = null)
    {
        double resolvedDeltaX = deltaX ?? (_hasPointer ? x - _lastPointerX : 0.0);
        double resolvedDeltaY = deltaY ?? (_hasPointer ? y - _lastPointerY : 0.0);
        return new GamePointerState(x, y, resolvedDeltaX, resolvedDeltaY, wheelDelta, cursorCaptured);
    }

    private GameInputSnapshot CreateSnapshot(
        double timeSec,
        IEnumerable<GameKey> pressedKeys,
        IEnumerable<GameKey> releasedKeys,
        IEnumerable<GameMouseButton> pressedMouseButtons,
        IEnumerable<GameMouseButton> releasedMouseButtons,
        GamePointerState pointer)
    {
        return new GameInputSnapshot(
            ++_frame,
            timeSec,
            new HashSet<GameKey>(_downKeys),
            new HashSet<GameKey>(pressedKeys.Where(key => key != GameKey.None)),
            new HashSet<GameKey>(releasedKeys.Where(key => key != GameKey.None)),
            new HashSet<GameMouseButton>(_downMouseButtons),
            new HashSet<GameMouseButton>(pressedMouseButtons.Where(button => button != GameMouseButton.None)),
            new HashSet<GameMouseButton>(releasedMouseButtons.Where(button => button != GameMouseButton.None)),
            pointer);
    }

    private void RememberPointer(GamePointerState pointer)
    {
        _lastPointerX = pointer.X;
        _lastPointerY = pointer.Y;
        _hasPointer = true;
    }
}
