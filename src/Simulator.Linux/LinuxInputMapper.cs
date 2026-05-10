using OpenTK.Windowing.GraphicsLibraryFramework;
using Simulator.Runtime.Input;

namespace Simulator.Linux;

internal static class LinuxInputMapper
{
    public static GameKey MapKey(Keys key)
        => key switch
        {
            Keys.Enter => GameKey.Enter,
            Keys.Escape => GameKey.Escape,
            Keys.Tab => GameKey.Tab,
            Keys.Space => GameKey.Space,
            Keys.Backspace => GameKey.Backspace,
            Keys.Delete => GameKey.Delete,
            Keys.PageUp => GameKey.PageUp,
            Keys.PageDown => GameKey.PageDown,
            Keys.LeftShift => GameKey.LeftShift,
            Keys.RightShift => GameKey.RightShift,
            Keys.LeftControl => GameKey.LeftControl,
            Keys.RightControl => GameKey.RightControl,
            Keys.LeftAlt => GameKey.LeftAlt,
            Keys.RightAlt => GameKey.RightAlt,
            Keys.A => GameKey.A,
            Keys.B => GameKey.B,
            Keys.C => GameKey.C,
            Keys.D => GameKey.D,
            Keys.E => GameKey.E,
            Keys.F => GameKey.F,
            Keys.H => GameKey.H,
            Keys.I => GameKey.I,
            Keys.J => GameKey.J,
            Keys.K => GameKey.K,
            Keys.L => GameKey.L,
            Keys.N => GameKey.N,
            Keys.O => GameKey.O,
            Keys.P => GameKey.P,
            Keys.Q => GameKey.Q,
            Keys.R => GameKey.R,
            Keys.S => GameKey.S,
            Keys.T => GameKey.T,
            Keys.V => GameKey.V,
            Keys.W => GameKey.W,
            Keys.X => GameKey.X,
            Keys.Z => GameKey.Z,
            Keys.D0 => GameKey.D0,
            Keys.D1 => GameKey.D1,
            Keys.D2 => GameKey.D2,
            Keys.D3 => GameKey.D3,
            Keys.D4 => GameKey.D4,
            Keys.D5 => GameKey.D5,
            Keys.D6 => GameKey.D6,
            Keys.D7 => GameKey.D7,
            Keys.D8 => GameKey.D8,
            Keys.D9 => GameKey.D9,
            Keys.F1 => GameKey.F1,
            Keys.F2 => GameKey.F2,
            Keys.F3 => GameKey.F3,
            Keys.F4 => GameKey.F4,
            Keys.F5 => GameKey.F5,
            Keys.F6 => GameKey.F6,
            Keys.F7 => GameKey.F7,
            Keys.F8 => GameKey.F8,
            Keys.F9 => GameKey.F9,
            _ => GameKey.None,
        };

    public static GameMouseButton MapMouseButton(MouseButton button)
        => button switch
        {
            MouseButton.Left => GameMouseButton.Left,
            MouseButton.Right => GameMouseButton.Right,
            MouseButton.Middle => GameMouseButton.Middle,
            _ => GameMouseButton.None,
        };
}
