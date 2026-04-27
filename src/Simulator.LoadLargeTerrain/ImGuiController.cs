using System.Runtime.InteropServices;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector2 = System.Numerics.Vector2;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace LoadLargeTerrain;

internal sealed class ImGuiController : IDisposable
{
    private int _vertexArray;
    private int _vertexBuffer;
    private int _indexBuffer;
    private int _vertexBufferSize;
    private int _indexBufferSize;
    private int _fontTexture;
    private int _shader;
    private int _attribLocationTex;
    private int _attribLocationProjMtx;
    private int _attribLocationVtxPos;
    private int _attribLocationVtxUv;
    private int _attribLocationVtxColor;
    private int _windowWidth;
    private int _windowHeight;
    private int _framebufferWidth;
    private int _framebufferHeight;

    public ImGuiController(int width, int height, int framebufferWidth, int framebufferHeight)
    {
        _windowWidth = Math.Max(width, 1);
        _windowHeight = Math.Max(height, 1);
        _framebufferWidth = Math.Max(framebufferWidth, 1);
        _framebufferHeight = Math.Max(framebufferHeight, 1);
        _vertexBufferSize = 10000;
        _indexBufferSize = 2000;

        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        AddChineseFont(io);

        CreateDeviceResources();
        SetKeyMappings();
        SetPerFrameData(1.0f / 60.0f);
    }

    public bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;

    public bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    public void WindowResized(int width, int height, int framebufferWidth, int framebufferHeight)
    {
        _windowWidth = Math.Max(width, 1);
        _windowHeight = Math.Max(height, 1);
        _framebufferWidth = Math.Max(framebufferWidth, 1);
        _framebufferHeight = Math.Max(framebufferHeight, 1);
    }

    public void PressChar(char keyChar)
    {
        ImGui.GetIO().AddInputCharacter(keyChar);
    }

    public void Update(GameWindow window, float deltaSeconds)
    {
        if (deltaSeconds <= 0.0f)
        {
            deltaSeconds = 1.0f / 60.0f;
        }

        WindowResized(window.ClientSize.X, window.ClientSize.Y, window.FramebufferSize.X, window.FramebufferSize.Y);
        SetPerFrameData(deltaSeconds);
        UpdateInput(window);
        ImGui.NewFrame();
    }

    public void Render()
    {
        ImGui.Render();
        RenderImDrawData(ImGui.GetDrawData());
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vertexArray);
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteTexture(_fontTexture);
        GL.DeleteProgram(_shader);
        ImGui.DestroyContext();
    }

    private void CreateDeviceResources()
    {
        _vertexBuffer = GL.GenBuffer();
        _indexBuffer = GL.GenBuffer();
        _vertexArray = GL.GenVertexArray();

        RecreateFontDeviceTexture();

        const string vertexSource = """
            #version 330 core
            uniform mat4 projection_matrix;
            layout(location = 0) in vec2 in_position;
            layout(location = 1) in vec2 in_texCoord;
            layout(location = 2) in vec4 in_color;
            out vec2 frag_UV;
            out vec4 frag_Color;
            void main()
            {
                gl_Position = projection_matrix * vec4(in_position, 0, 1);
                frag_UV = in_texCoord;
                frag_Color = in_color;
            }
            """;

        const string fragmentSource = """
            #version 330 core
            uniform sampler2D in_fontTexture;
            in vec2 frag_UV;
            in vec4 frag_Color;
            out vec4 output_color;
            void main()
            {
                output_color = frag_Color * texture(in_fontTexture, frag_UV.st);
            }
            """;

        var vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
        _shader = GL.CreateProgram();
        GL.AttachShader(_shader, vertex);
        GL.AttachShader(_shader, fragment);
        GL.LinkProgram(_shader);
        GL.GetProgram(_shader, GetProgramParameterName.LinkStatus, out var status);
        if (status == 0)
        {
            throw new InvalidOperationException(GL.GetProgramInfoLog(_shader));
        }

        GL.DetachShader(_shader, vertex);
        GL.DetachShader(_shader, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);

        _attribLocationTex = GL.GetUniformLocation(_shader, "in_fontTexture");
        _attribLocationProjMtx = GL.GetUniformLocation(_shader, "projection_matrix");
        _attribLocationVtxPos = 0;
        _attribLocationVtxUv = 1;
        _attribLocationVtxColor = 2;

        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        var stride = Marshal.SizeOf<ImDrawVert>();
        GL.EnableVertexAttribArray(_attribLocationVtxPos);
        GL.VertexAttribPointer(_attribLocationVtxPos, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(_attribLocationVtxUv);
        GL.VertexAttribPointer(_attribLocationVtxUv, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.EnableVertexAttribArray(_attribLocationVtxColor);
        GL.VertexAttribPointer(_attribLocationVtxColor, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
    }

    private static unsafe void AddChineseFont(ImGuiIOPtr io)
    {
        var fontCandidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simhei.ttf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simsun.ttc"),
        };

        var fontPath = fontCandidates.FirstOrDefault(File.Exists);
        if (fontPath is null)
        {
            io.Fonts.AddFontDefault();
            Console.WriteLine("未找到中文字体，界面中文可能无法正常显示。");
            return;
        }

        io.Fonts.AddFontFromFileTTF(fontPath, 18.0f, null, io.Fonts.GetGlyphRangesChineseFull());
        Console.WriteLine($"已加载中文界面字体：{fontPath}");
    }

    private unsafe void RecreateFontDeviceTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out var width, out var height, out var bytesPerPixel);

        var previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        _fontTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        io.Fonts.SetTexID((IntPtr)_fontTexture);
        io.Fonts.ClearTexData();
        GL.BindTexture(TextureTarget.Texture2D, previousTexture);

        _ = bytesPerPixel;
    }

    private void SetPerFrameData(float deltaSeconds)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_windowWidth, _windowHeight);
        io.DisplayFramebufferScale = new Vector2(
            _framebufferWidth / (float)Math.Max(_windowWidth, 1),
            _framebufferHeight / (float)Math.Max(_windowHeight, 1));
        io.DeltaTime = deltaSeconds;
    }

    private void UpdateInput(GameWindow window)
    {
        var io = ImGui.GetIO();
        var mouse = window.MouseState;
        var mousePosition = window.MousePosition;
        var keyboard = window.KeyboardState;

        io.MousePos = new Vector2(mousePosition.X, mousePosition.Y);
        io.MouseDown[0] = mouse.IsButtonDown(MouseButton.Left);
        io.MouseDown[1] = mouse.IsButtonDown(MouseButton.Right);
        io.MouseDown[2] = mouse.IsButtonDown(MouseButton.Middle);
        io.MouseWheel = mouse.ScrollDelta.Y;
        io.MouseWheelH = mouse.ScrollDelta.X;

        foreach (var key in MappedKeys)
        {
            io.AddKeyEvent(key.ImGuiKey, keyboard.IsKeyDown(key.OpenTkKey));
        }

        io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt));
        io.AddKeyEvent(ImGuiKey.ModSuper, keyboard.IsKeyDown(Keys.LeftSuper) || keyboard.IsKeyDown(Keys.RightSuper));
    }

    private static void SetKeyMappings()
    {
    }

    private unsafe void RenderImDrawData(ImDrawDataPtr drawData)
    {
        var framebufferWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        var framebufferHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (framebufferWidth <= 0 || framebufferHeight <= 0)
        {
            return;
        }

        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ScissorTest);
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        GL.Viewport(0, 0, framebufferWidth, framebufferHeight);

        var left = drawData.DisplayPos.X;
        var right = drawData.DisplayPos.X + drawData.DisplaySize.X;
        var top = drawData.DisplayPos.Y;
        var bottom = drawData.DisplayPos.Y + drawData.DisplaySize.Y;
        var orthoProjection = new Matrix4(
            2.0f / (right - left), 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / (top - bottom), 0.0f, 0.0f,
            0.0f, 0.0f, -1.0f, 0.0f,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0.0f, 1.0f);

        GL.UseProgram(_shader);
        GL.Uniform1(_attribLocationTex, 0);
        GL.UniformMatrix4(_attribLocationProjMtx, false, ref orthoProjection);
        GL.BindVertexArray(_vertexArray);

        drawData.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

        for (var n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            var vertexSize = cmdList.VtxBuffer.Size * Marshal.SizeOf<ImDrawVert>();
            if (vertexSize > _vertexBufferSize)
            {
                var newSize = (int)Math.Max(_vertexBufferSize * 1.5f, vertexSize);
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
                GL.BufferData(BufferTarget.ArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                _vertexBufferSize = newSize;
            }

            var indexSize = cmdList.IdxBuffer.Size * sizeof(ushort);
            if (indexSize > _indexBufferSize)
            {
                var newSize = (int)Math.Max(_indexBufferSize * 1.5f, indexSize);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
                GL.BufferData(BufferTarget.ElementArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                _indexBufferSize = newSize;
            }

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertexSize, cmdList.VtxBuffer.Data);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, indexSize, cmdList.IdxBuffer.Data);

            for (var cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex++)
            {
                var drawCommand = cmdList.CmdBuffer[cmdIndex];
                if (drawCommand.UserCallback != IntPtr.Zero)
                {
                    throw new NotImplementedException("ImGui user callbacks are not supported.");
                }

                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, (int)drawCommand.TextureId);

                var clip = drawCommand.ClipRect;
                GL.Scissor(
                    (int)clip.X,
                    (int)(framebufferHeight - clip.W),
                    (int)(clip.Z - clip.X),
                    (int)(clip.W - clip.Y));

                GL.DrawElementsBaseVertex(
                    PrimitiveType.Triangles,
                    (int)drawCommand.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (IntPtr)(drawCommand.IdxOffset * sizeof(ushort)),
                    (int)drawCommand.VtxOffset);
            }
        }

        GL.Disable(EnableCap.ScissorTest);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private static int CompileShader(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var status);
        if (status == 0)
        {
            throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
        }

        return shader;
    }

    private static readonly (Keys OpenTkKey, ImGuiKey ImGuiKey)[] MappedKeys =
    [
        (Keys.Tab, ImGuiKey.Tab),
        (Keys.Left, ImGuiKey.LeftArrow),
        (Keys.Right, ImGuiKey.RightArrow),
        (Keys.Up, ImGuiKey.UpArrow),
        (Keys.Down, ImGuiKey.DownArrow),
        (Keys.PageUp, ImGuiKey.PageUp),
        (Keys.PageDown, ImGuiKey.PageDown),
        (Keys.Home, ImGuiKey.Home),
        (Keys.End, ImGuiKey.End),
        (Keys.Insert, ImGuiKey.Insert),
        (Keys.Delete, ImGuiKey.Delete),
        (Keys.Backspace, ImGuiKey.Backspace),
        (Keys.Space, ImGuiKey.Space),
        (Keys.Enter, ImGuiKey.Enter),
        (Keys.Escape, ImGuiKey.Escape),
        (Keys.A, ImGuiKey.A),
        (Keys.C, ImGuiKey.C),
        (Keys.V, ImGuiKey.V),
        (Keys.X, ImGuiKey.X),
        (Keys.Y, ImGuiKey.Y),
        (Keys.Z, ImGuiKey.Z),
    ];
}
