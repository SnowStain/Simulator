using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Simulator.Core;
using WinFormsKeys = System.Windows.Forms.Keys;
using WinFormsMouseButtons = System.Windows.Forms.MouseButtons;
using TkKeys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;
using TkImage = OpenTK.Windowing.Common.Input.Image;
using TkMouseButton = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Simulator.ThreeD;

internal static class SimulatorOpenTkApplication
{
    public static int Run(Simulator3dOptions options)
    {
        GameWindowSettings gameWindowSettings = GameWindowSettings.Default;
        NativeWindowSettings nativeWindowSettings = new()
        {
            Title = "RM ARTINX A-Soul模拟器",
            ClientSize = new Vector2i(1440, 900),
            APIVersion = new Version(4, 1),
            Profile = ContextProfile.Compatability,
            Flags = ContextFlags.Default,
            Icon = TryCreateWindowIcon(),
        };

        using var window = new SimulatorOpenTkWindow(gameWindowSettings, nativeWindowSettings, options);
        window.Run();
        return 0;
    }

    private static WindowIcon? TryCreateWindowIcon()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "DarkLogo.png"),
            @"E:\Artinx\260111new\Simulator\DarkLogo.png",
        };
        try
        {
            candidates.Insert(0, Path.Combine(ProjectLayout.Discover().RootPath, "DarkLogo.png"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            try
            {
                using Bitmap source = new(candidate);
                return new WindowIcon(new[]
                {
                    CreateIconImage(source, 32),
                    CreateIconImage(source, 128),
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
            {
            }
        }

        return null;
    }

    private static TkImage CreateIconImage(Bitmap source, int size)
    {
        using Bitmap scaled = new(size, size, DrawingPixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(scaled))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        byte[] rgba = new byte[size * size * 4];
        int offset = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Color pixel = scaled.GetPixel(x, y);
                rgba[offset++] = pixel.R;
                rgba[offset++] = pixel.G;
                rgba[offset++] = pixel.B;
                rgba[offset++] = pixel.A;
            }
        }

        return new TkImage(size, size, rgba);
    }
}

internal sealed class SimulatorOpenTkWindow : GameWindow
{
    private static readonly TkKeys[] MonitoredKeys =
    {
        TkKeys.Enter,
        TkKeys.Escape,
        TkKeys.Tab,
        TkKeys.Space,
        TkKeys.A,
        TkKeys.B,
        TkKeys.C,
        TkKeys.D,
        TkKeys.F,
        TkKeys.H,
        TkKeys.I,
        TkKeys.J,
        TkKeys.K,
        TkKeys.L,
        TkKeys.N,
        TkKeys.P,
        TkKeys.Q,
        TkKeys.R,
        TkKeys.S,
        TkKeys.T,
        TkKeys.V,
        TkKeys.W,
        TkKeys.X,
        TkKeys.Z,
        TkKeys.PageUp,
        TkKeys.PageDown,
        TkKeys.LeftShift,
        TkKeys.RightShift,
        TkKeys.LeftControl,
        TkKeys.RightControl,
        TkKeys.LeftAlt,
        TkKeys.RightAlt,
        TkKeys.F1,
        TkKeys.F2,
        TkKeys.F3,
        TkKeys.F4,
        TkKeys.F5,
        TkKeys.F6,
        TkKeys.F7,
        TkKeys.F8,
        TkKeys.F9,
        TkKeys.Semicolon,
        TkKeys.Period,
        TkKeys.D1,
        TkKeys.D2,
        TkKeys.D3,
        TkKeys.D4,
    };

    private readonly Simulator3dForm _runtime;
    private readonly HashSet<TkKeys> _pressedKeys = new();
    private readonly HashSet<TkMouseButton> _pressedMouseButtons = new();
    private Bitmap? _frameBitmap;
    private Graphics? _frameGraphics;
    private int _shaderProgram;
    private int _vertexBuffer;
    private int _vertexArray;
    private int _frameTexture;
    private bool _textureInitialized;

    public SimulatorOpenTkWindow(
        GameWindowSettings gameWindowSettings,
        NativeWindowSettings nativeWindowSettings,
        Simulator3dOptions options)
        : base(gameWindowSettings, nativeWindowSettings)
    {
        _runtime = Simulator3dForm.CreateExternalCompatibilityRuntime(options);
        _runtime.ExternalResize(new Size(nativeWindowSettings.ClientSize.X, nativeWindowSettings.ClientSize.Y));
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        VSync = VSyncMode.Off;
        GL.ClearColor(0.07f, 0.09f, 0.12f, 1.0f);
        _shaderProgram = BuildShaderProgram();
        InitializeQuadBuffers();
        EnsureFrameSurface();
        _runtime.AttachExternalBorrowedGpuContext();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, Math.Max(1, FramebufferSize.X), Math.Max(1, FramebufferSize.Y));
        _runtime.ExternalResize(new Size(Math.Max(1, ClientSize.X), Math.Max(1, ClientSize.Y)));
        EnsureFrameSurface();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        if (_runtime.ExternalRuntimeClosed)
        {
            Close();
            return;
        }

        ProcessKeyboard();
        ProcessMouse();
        _runtime.ExternalAdvanceFrame();

        bool captureMouse = IsFocused && _runtime.ShouldCaptureMouseExternally();
        CursorState = captureMouse ? CursorState.Grabbed : CursorState.Normal;
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        GL.UseProgram(0);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        if (_runtime.ExternalRenderToCurrentOpenGlContext())
        {
            SwapBuffers();
            return;
        }

        EnsureFrameSurface();
        if (_frameGraphics is not null)
        {
            _runtime.ExternalRender(_frameGraphics);
            UploadFrameTexture();
        }

        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.UseProgram(_shaderProgram);
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _frameTexture);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        SwapBuffers();
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _frameGraphics?.Dispose();
        _frameBitmap?.Dispose();
        _runtime.Dispose();
        if (_frameTexture != 0)
        {
            GL.DeleteTexture(_frameTexture);
        }

        if (_vertexBuffer != 0)
        {
            GL.DeleteBuffer(_vertexBuffer);
        }

        if (_vertexArray != 0)
        {
            GL.DeleteVertexArray(_vertexArray);
        }

        if (_shaderProgram != 0)
        {
            GL.DeleteProgram(_shaderProgram);
        }
    }

    protected override void OnFocusedChanged(FocusedChangedEventArgs e)
    {
        base.OnFocusedChanged(e);
        if (IsFocused)
        {
            return;
        }

        bool shiftDown = false;
        bool controlDown = false;
        bool altDown = false;
        foreach (TkKeys key in _pressedKeys.ToArray())
        {
            if (TryMapKey(key, out WinFormsKeys mapped))
            {
                _runtime.ExternalKeyUp(mapped, shiftDown, controlDown, altDown);
            }
        }

        _pressedKeys.Clear();
        foreach (TkMouseButton button in _pressedMouseButtons.ToArray())
        {
            _runtime.ExternalMouseUp(MapMouseButton(button), Point.Empty);
        }

        _pressedMouseButtons.Clear();
    }

    private void ProcessKeyboard()
    {
        bool shiftDown = KeyboardState.IsKeyDown(TkKeys.LeftShift) || KeyboardState.IsKeyDown(TkKeys.RightShift);
        bool controlDown = KeyboardState.IsKeyDown(TkKeys.LeftControl) || KeyboardState.IsKeyDown(TkKeys.RightControl);
        bool altDown = KeyboardState.IsKeyDown(TkKeys.LeftAlt) || KeyboardState.IsKeyDown(TkKeys.RightAlt);

        foreach (TkKeys key in MonitoredKeys)
        {
            bool down = KeyboardState.IsKeyDown(key);
            bool pressed = _pressedKeys.Contains(key);
            if (down && !pressed)
            {
                if (TryMapKey(key, out WinFormsKeys mapped))
                {
                    _runtime.ExternalKeyDown(mapped, shiftDown, controlDown, altDown);
                }

                _pressedKeys.Add(key);
            }
            else if (!down && pressed)
            {
                if (TryMapKey(key, out WinFormsKeys mapped))
                {
                    _runtime.ExternalKeyUp(mapped, shiftDown, controlDown, altDown);
                }

                _pressedKeys.Remove(key);
            }
        }
    }

    private void ProcessMouse()
    {
        Point location = new((int)Math.Round(MousePosition.X), (int)Math.Round(MousePosition.Y));
        Point delta = new((int)Math.Round(MouseState.Delta.X), (int)Math.Round(MouseState.Delta.Y));

        HandleMouseButton(TkMouseButton.Left, WinFormsMouseButtons.Left, location);
        HandleMouseButton(TkMouseButton.Right, WinFormsMouseButtons.Right, location);
        HandleMouseButton(TkMouseButton.Middle, WinFormsMouseButtons.Middle, location);

        int wheelDelta = (int)Math.Round(MouseState.ScrollDelta.Y * 120.0f);
        if (wheelDelta != 0)
        {
            _runtime.ExternalMouseWheel(location, wheelDelta);
        }

        bool capturedLook = IsFocused && _runtime.ShouldCaptureMouseExternally();
        _runtime.ExternalMouseMove(location, delta, capturedLook);
    }

    private void HandleMouseButton(TkMouseButton button, WinFormsMouseButtons mapped, Point location)
    {
        bool down = MouseState.IsButtonDown(button);
        bool pressed = _pressedMouseButtons.Contains(button);
        if (down && !pressed)
        {
            _runtime.ExternalMouseDown(mapped, location);
            _pressedMouseButtons.Add(button);
        }
        else if (!down && pressed)
        {
            _runtime.ExternalMouseUp(mapped, location);
            _pressedMouseButtons.Remove(button);
        }
    }

    private static WinFormsMouseButtons MapMouseButton(TkMouseButton button)
    {
        return button switch
        {
            TkMouseButton.Left => WinFormsMouseButtons.Left,
            TkMouseButton.Right => WinFormsMouseButtons.Right,
            TkMouseButton.Middle => WinFormsMouseButtons.Middle,
            _ => WinFormsMouseButtons.None,
        };
    }

    private void EnsureFrameSurface()
    {
        int width = Math.Max(1, ClientSize.X);
        int height = Math.Max(1, ClientSize.Y);
        if (_frameBitmap is not null && _frameBitmap.Width == width && _frameBitmap.Height == height)
        {
            return;
        }

        _frameGraphics?.Dispose();
        _frameBitmap?.Dispose();
        _frameBitmap = new Bitmap(width, height, DrawingPixelFormat.Format32bppPArgb);
        _frameGraphics = Graphics.FromImage(_frameBitmap);
        _textureInitialized = false;
    }

    private void UploadFrameTexture()
    {
        if (_frameBitmap is null)
        {
            return;
        }

        if (_frameTexture == 0)
        {
            _frameTexture = GL.GenTexture();
        }

        GL.BindTexture(TextureTarget.Texture2D, _frameTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        BitmapData data = _frameBitmap.LockBits(
            new Rectangle(0, 0, _frameBitmap.Width, _frameBitmap.Height),
            ImageLockMode.ReadOnly,
            DrawingPixelFormat.Format32bppPArgb);
        try
        {
            if (!_textureInitialized)
            {
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba,
                    _frameBitmap.Width,
                    _frameBitmap.Height,
                    0,
                    OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    data.Scan0);
                _textureInitialized = true;
            }
            else
            {
                GL.TexSubImage2D(
                    TextureTarget.Texture2D,
                    0,
                    0,
                    0,
                    _frameBitmap.Width,
                    _frameBitmap.Height,
                    OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    data.Scan0);
            }
        }
        finally
        {
            _frameBitmap.UnlockBits(data);
        }
    }

    private void InitializeQuadBuffers()
    {
        float[] vertices =
        {
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
            -1f,  1f, 0f, 0f,
             1f,  1f, 1f, 0f,
        };

        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();

        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        GL.BindVertexArray(0);
    }

    private static bool TryMapKey(TkKeys key, out WinFormsKeys mapped)
    {
        mapped = key switch
        {
            TkKeys.Enter => WinFormsKeys.Enter,
            TkKeys.Escape => WinFormsKeys.Escape,
            TkKeys.Tab => WinFormsKeys.Tab,
            TkKeys.Space => WinFormsKeys.Space,
            TkKeys.A => WinFormsKeys.A,
            TkKeys.B => WinFormsKeys.B,
            TkKeys.C => WinFormsKeys.C,
            TkKeys.D => WinFormsKeys.D,
            TkKeys.F => WinFormsKeys.F,
            TkKeys.H => WinFormsKeys.H,
            TkKeys.I => WinFormsKeys.I,
            TkKeys.J => WinFormsKeys.J,
            TkKeys.K => WinFormsKeys.K,
            TkKeys.L => WinFormsKeys.L,
            TkKeys.N => WinFormsKeys.N,
            TkKeys.P => WinFormsKeys.P,
            TkKeys.Q => WinFormsKeys.Q,
            TkKeys.R => WinFormsKeys.R,
            TkKeys.S => WinFormsKeys.S,
            TkKeys.T => WinFormsKeys.T,
            TkKeys.V => WinFormsKeys.V,
            TkKeys.W => WinFormsKeys.W,
            TkKeys.X => WinFormsKeys.X,
            TkKeys.Z => WinFormsKeys.Z,
            TkKeys.PageUp => WinFormsKeys.PageUp,
            TkKeys.PageDown => WinFormsKeys.PageDown,
            TkKeys.LeftShift => WinFormsKeys.LShiftKey,
            TkKeys.RightShift => WinFormsKeys.RShiftKey,
            TkKeys.LeftControl => WinFormsKeys.LControlKey,
            TkKeys.RightControl => WinFormsKeys.RControlKey,
            TkKeys.LeftAlt => WinFormsKeys.LMenu,
            TkKeys.RightAlt => WinFormsKeys.RMenu,
            TkKeys.F1 => WinFormsKeys.F1,
            TkKeys.F2 => WinFormsKeys.F2,
            TkKeys.F3 => WinFormsKeys.F3,
            TkKeys.F4 => WinFormsKeys.F4,
            TkKeys.F5 => WinFormsKeys.F5,
            TkKeys.F6 => WinFormsKeys.F6,
            TkKeys.F7 => WinFormsKeys.F7,
            TkKeys.F8 => WinFormsKeys.F8,
            TkKeys.F9 => WinFormsKeys.F9,
            TkKeys.Semicolon => WinFormsKeys.Oem1,
            TkKeys.Period => WinFormsKeys.OemPeriod,
            TkKeys.D1 => WinFormsKeys.D1,
            TkKeys.D2 => WinFormsKeys.D2,
            TkKeys.D3 => WinFormsKeys.D3,
            TkKeys.D4 => WinFormsKeys.D4,
            _ => WinFormsKeys.None,
        };

        return mapped != WinFormsKeys.None;
    }

    private static int BuildShaderProgram()
    {
        const string vertexShaderSource =
            """
            #version 330 core
            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec2 aTexCoord;
            out vec2 vTexCoord;
            void main()
            {
                gl_Position = vec4(aPosition, 0.0, 1.0);
                vTexCoord = aTexCoord;
            }
            """;
        const string fragmentShaderSource =
            """
            #version 330 core
            in vec2 vTexCoord;
            uniform sampler2D uFrame;
            out vec4 FragColor;
            void main()
            {
                FragColor = texture(uFrame, vTexCoord);
            }
            """;

        int vertexShader = CompileShader(ShaderType.VertexShader, vertexShaderSource);
        int fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentShaderSource);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vertexShader);
        GL.AttachShader(program, fragmentShader);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int status);
        if (status == 0)
        {
            string info = GL.GetProgramInfoLog(program);
            throw new InvalidOperationException($"Failed to link OpenTK simulator shader: {info}");
        }

        GL.DetachShader(program, vertexShader);
        GL.DetachShader(program, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
        GL.UseProgram(program);
        int samplerLocation = GL.GetUniformLocation(program, "uFrame");
        if (samplerLocation >= 0)
        {
            GL.Uniform1(samplerLocation, 0);
        }

        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0)
        {
            string info = GL.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"Failed to compile {type}: {info}");
        }

        return shader;
    }
}
