using System.Collections;
using System.Drawing;

namespace Simulator.Platform.Ui;

public readonly record struct OpenGkUiButton(Rectangle Rect, string Action)
{
    public bool Contains(Point point) => Rect.Contains(point);

    public bool HasActionPrefix(string prefix)
        => Action.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}

public sealed class OpenGkUiButtonRegistry : IEnumerable<OpenGkUiButton>
{
    private readonly List<OpenGkUiButton> _buttons = new();

    public int Count => _buttons.Count;

    public OpenGkUiButton this[int index] => _buttons[index];

    public void Clear() => _buttons.Clear();

    public void Add(OpenGkUiButton button)
    {
        if (string.IsNullOrWhiteSpace(button.Action))
        {
            return;
        }

        _buttons.Add(button);
    }

    public void Add(Rectangle rect, string action)
        => Add(new OpenGkUiButton(rect, action));

    public void AddRange(IEnumerable<OpenGkUiButton> buttons)
    {
        foreach (OpenGkUiButton button in buttons)
        {
            Add(button);
        }
    }

    public int RemoveAll(Predicate<OpenGkUiButton> match)
        => _buttons.RemoveAll(match);

    public void RemoveRange(int index, int count)
        => _buttons.RemoveRange(index, count);

    public bool TryResolve(Point point, Func<string, bool>? canExecute, out string? action)
    {
        for (int index = _buttons.Count - 1; index >= 0; index--)
        {
            OpenGkUiButton button = _buttons[index];
            if (!button.Contains(point))
            {
                continue;
            }

            if (canExecute is not null && !canExecute(button.Action))
            {
                continue;
            }

            action = button.Action;
            return true;
        }

        action = null;
        return false;
    }

    public OpenGkUiButton[] ToArray()
        => _buttons.ToArray();

    public IEnumerator<OpenGkUiButton> GetEnumerator()
        => _buttons.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();
}
