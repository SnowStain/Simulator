using System.Drawing;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Simulator.OpenTk.Input;
using Simulator.Platform.Ui;
using Simulator.Runtime.Input;

namespace Simulator.Linux;

internal sealed class LinuxOperatorWindow : GameWindow
{
    private static readonly OpenGkRefereeEnergyCard[] LocalEnergyCards =
    [
        new("Red Small Energy", "local_energy:Red Small Energy"),
        new("Red Large Energy", "local_energy:Red Large Energy"),
        new("Blue Small Energy", "local_energy:Blue Small Energy"),
        new("Blue Large Energy", "local_energy:Blue Large Energy"),
    ];

    private readonly LinuxOperatorRuntime _runtime;
    private readonly GameInputSnapshotAccumulator _inputAccumulator = new();
    private readonly OpenGkUiButtonRegistry _uiButtons = new();
    private readonly GlPrimitiveRenderer _renderer = new();
    private readonly LinuxOperatorOptions _options;

    public LinuxOperatorWindow(
        GameWindowSettings gameWindowSettings,
        NativeWindowSettings nativeWindowSettings,
        LinuxOperatorOptions options)
        : base(gameWindowSettings, nativeWindowSettings)
    {
        _options = options;
        _runtime = new LinuxOperatorRuntime(options);
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        VSync = VSyncMode.Off;
        GL.ClearColor(0.53f, 0.64f, 0.70f, 1.0f);
        GL.Enable(EnableCap.Multisample);
        _renderer.Load();
        _runtime.Load();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, Math.Max(1, FramebufferSize.X), Math.Max(1, FramebufferSize.Y));
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        GameInputSnapshot input = CaptureInputSnapshot();
        _runtime.ApplyInput(input);
        if (input.PressedMouseButtons.Contains(GameMouseButton.Left)
            && _uiButtons.TryResolve(new Point((int)input.Pointer.X, (int)input.Pointer.Y), canExecute: null, out string? uiAction)
            && uiAction is not null)
        {
            _runtime.ApplyUiAction(uiAction);
        }

        _runtime.Tick(args.Time);
        if (_options.ExitAfterSec is double exitAfterSec && _runtime.TimeSec >= exitAfterSec)
        {
            Close();
            return;
        }

        CursorState = IsFocused && _runtime.CaptureMouse
            ? CursorState.Grabbed
            : CursorState.Normal;
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _renderer.Begin(ClientSize.X, ClientSize.Y);
        DrawLocalGamePlaceholder();
        _renderer.End();
        SwapBuffers();
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _renderer.Dispose();
    }

    protected override void OnFocusedChanged(FocusedChangedEventArgs e)
    {
        base.OnFocusedChanged(e);
        if (!IsFocused)
        {
            _runtime.ApplyInput(_inputAccumulator.ReleaseAll(
                GLFW.GetTime(),
                new GamePointerState(0, 0, 0, 0, 0, CursorCaptured: false)));
        }
    }

    private GameInputSnapshot CaptureInputSnapshot()
    {
        var keys = new HashSet<GameKey>();
        foreach (Keys key in OpenTkGameInputMapper.MonitoredKeys)
        {
            if (KeyboardState.IsKeyDown(key))
            {
                GameKey mapped = OpenTkGameInputMapper.MapKey(key);
                if (mapped != GameKey.None)
                {
                    keys.Add(mapped);
                }
            }
        }

        var buttons = new HashSet<GameMouseButton>();
        AddMouseButton(MouseButton.Left, buttons);
        AddMouseButton(MouseButton.Right, buttons);
        AddMouseButton(MouseButton.Middle, buttons);

        GamePointerState pointer = _inputAccumulator.BuildPointer(
            Math.Clamp(MousePosition.X, 0, Math.Max(1, ClientSize.X) - 1),
            Math.Clamp(MousePosition.Y, 0, Math.Max(1, ClientSize.Y) - 1),
            MouseState.ScrollDelta.Y * 120.0,
            IsFocused && _runtime.CaptureMouse,
            MouseState.Delta.X,
            MouseState.Delta.Y);

        return _inputAccumulator.CaptureState(
            GLFW.GetTime(),
            keys,
            buttons,
            pointer);
    }

    private void AddMouseButton(MouseButton button, HashSet<GameMouseButton> buttons)
    {
        if (!MouseState.IsButtonDown(button))
        {
            return;
        }

        GameMouseButton mapped = OpenTkGameInputMapper.MapMouseButton(button);
        if (mapped != GameMouseButton.None)
        {
            buttons.Add(mapped);
        }
    }

    private void DrawLocalGamePlaceholder()
    {
        float width = Math.Max(1, ClientSize.X);
        float height = Math.Max(1, ClientSize.Y);

        _renderer.Rect(0, 0, width, height, new Vector4(0.50f, 0.62f, 0.68f, 1.0f));
        _renderer.Rect(0, height * 0.74f, width, height * 0.26f, new Vector4(0.02f, 0.03f, 0.04f, 1.0f));
        _renderer.Rect(width * 0.08f, height * 0.54f, width * 0.84f, 5, new Vector4(0.90f, 0.95f, 0.95f, 1.0f));
        _renderer.Rect(width * 0.12f, height * 0.58f, width * 0.18f, 4, new Vector4(0.85f, 0.12f, 0.16f, 1.0f));
        _renderer.Rect(width * 0.70f, height * 0.58f, width * 0.18f, 4, new Vector4(0.12f, 0.36f, 0.95f, 1.0f));
        _renderer.Rect(24, height - 90, 300, 10, new Vector4(0.82f, 0.18f, 0.18f, 1.0f));
        _renderer.Rect(24, height - 66, 300, 10, new Vector4(0.22f, 0.24f, 0.28f, 1.0f));
        _renderer.Rect(24, height - 42, 300, 10, new Vector4(0.78f, 0.68f, 0.18f, 1.0f));

        var ui = new OpenGkUiDrawList();
        _uiButtons.Clear();
        OpenGkUiPainter.AddPanel(ui, new Rectangle((int)(width - 230), (int)(height - 230), 205, 205), 166);
        OpenGkUiPainter.AddFlatButton(
            ui,
            new Rectangle(24, (int)(height - 104), 145, 34),
            "Capture",
            "linux:capture_mouse",
            _runtime.CaptureMouse,
            enabled: true,
            hoverMix: 0.0f,
            activeColor: Color.FromArgb(58, 124, 214));
        OpenGkUiPainter.AddFlatButton(
            ui,
            new Rectangle(179, (int)(height - 104), 145, 34),
            "Release",
            "linux:release_mouse",
            !_runtime.CaptureMouse,
            enabled: true,
            hoverMix: 0.0f,
            activeColor: Color.FromArgb(70, 138, 154));
        _renderer.Draw(ui);
        _uiButtons.AddRange(ui.Buttons);
        if (_runtime.OperatorPanelOpen)
        {
            _uiButtons.Clear();
            DrawLocalControlPanel(width, height);
        }

        float pulse = 0.5f + 0.5f * MathF.Sin((float)_runtime.TimeSec * 2.0f);
        _renderer.Rect(width * 0.5f - 18, height * 0.5f - 1, 36, 2, new Vector4(0.95f, 0.98f, 1.0f, 0.8f));
        _renderer.Rect(width * 0.5f - 1, height * 0.5f - 18, 2, 36, new Vector4(0.95f, 0.98f, 1.0f, 0.8f));
        _renderer.Rect(width * 0.5f - 4, height * 0.5f - 4, 8, 8, new Vector4(0.2f, 0.7f, 1.0f, 0.35f + pulse * 0.35f));
    }

    private void DrawLocalControlPanel(float width, float height)
    {
        var panel = new OpenGkUiDrawList();
        panel.FillRect(new Rectangle(0, 0, (int)width, (int)height), Color.FromArgb(154, 8, 10, 12));
        OpenGkRefereePanelLayout layout = OpenGkRefereePanelLayoutResolver.Resolve(new Size((int)width, (int)height), showLogout: true);
        OpenGkRefereePanelLayoutResolver.AddChrome(
            panel,
            layout,
            "Local Control",
            "O closes / Alt releases mouse / local game only",
            showLogout: true,
            logoutLabel: "Back",
            page: _runtime.LocalPanelPage,
            closeAction: "local_close",
            logoutAction: "local_return",
            mainTabAction: "local_page:main",
            energyTabAction: "local_page:energy");

        if (_runtime.LocalPanelPage == OpenGkRefereePanelPage.Energy)
        {
            OpenGkRefereePanelContent.AddEnergyGrid(panel, layout.Content, LocalEnergyCards);
        }
        else
        {
            OpenGkRefereePanelContent.AddLocalOverview(panel, layout.Content, _runtime.Status);
        }

        _renderer.Draw(panel);
        _uiButtons.AddRange(panel.Buttons);
    }
}
