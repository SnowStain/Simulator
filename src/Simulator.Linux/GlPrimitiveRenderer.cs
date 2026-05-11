using System.Drawing;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Simulator.Platform.Ui;

namespace Simulator.Linux;

internal sealed class GlPrimitiveRenderer : IDisposable
{
    private readonly List<Vertex> _vertices = new(4096);
    private int _program;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _viewportUniform;
    private int _width = 1;
    private int _height = 1;

    public void Load()
    {
        _program = BuildProgram();
        _viewportUniform = GL.GetUniformLocation(_program, "uViewport");
        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, 4096 * Vertex.SizeInBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, Vertex.SizeInBytes, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, Vertex.SizeInBytes, 2 * sizeof(float));
        GL.BindVertexArray(0);
    }

    public void Begin(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _vertices.Clear();
    }

    public void Rect(float x, float y, float width, float height, Vector4 color)
    {
        float x2 = x + width;
        float y2 = y + height;
        Add(x, y, color);
        Add(x2, y, color);
        Add(x, y2, color);
        Add(x2, y, color);
        Add(x2, y2, color);
        Add(x, y2, color);
    }

    public void Triangle(float ax, float ay, float bx, float by, float cx, float cy, Vector4 color)
    {
        Add(ax, ay, color);
        Add(bx, by, color);
        Add(cx, cy, color);
    }

    public void Line(float x1, float y1, float x2, float y2, float thickness, Vector4 color)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 1e-4f)
        {
            Rect(x1 - thickness * 0.5f, y1 - thickness * 0.5f, thickness, thickness, color);
            return;
        }

        float half = MathF.Max(0.5f, thickness * 0.5f);
        float nx = -dy / length * half;
        float ny = dx / length * half;
        Add(x1 + nx, y1 + ny, color);
        Add(x2 + nx, y2 + ny, color);
        Add(x1 - nx, y1 - ny, color);
        Add(x2 + nx, y2 + ny, color);
        Add(x2 - nx, y2 - ny, color);
        Add(x1 - nx, y1 - ny, color);
    }

    public void Draw(OpenGkUiDrawList drawList)
    {
        foreach (OpenGkUiDrawCommand command in drawList.Commands)
        {
            switch (command.Kind)
            {
                case OpenGkUiDrawCommandKind.FillRect:
                    Rect(command.Rect.X, command.Rect.Y, command.Rect.Width, command.Rect.Height, ToVector(command.Color));
                    break;

                case OpenGkUiDrawCommandKind.StrokeRect:
                    DrawStroke(command.Rect, command.Color, Math.Max(1.0f, command.StrokeWidth));
                    break;

                case OpenGkUiDrawCommandKind.Text:
                    DrawText(command);
                    break;
            }
        }
    }

    public void End()
    {
        if (_vertices.Count == 0)
        {
            return;
        }

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.UseProgram(_program);
        GL.Uniform2(_viewportUniform, _width, _height);
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        int byteCount = _vertices.Count * Vertex.SizeInBytes;
        GL.BufferData(BufferTarget.ArrayBuffer, byteCount, _vertices.ToArray(), BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertices.Count);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        if (_vertexBuffer != 0)
        {
            GL.DeleteBuffer(_vertexBuffer);
        }

        if (_vertexArray != 0)
        {
            GL.DeleteVertexArray(_vertexArray);
        }

        if (_program != 0)
        {
            GL.DeleteProgram(_program);
        }
    }

    private void Add(float x, float y, Vector4 color)
        => _vertices.Add(new Vertex(x, y, color.X, color.Y, color.Z, color.W));

    private void DrawStroke(Rectangle rect, Color color, float width)
    {
        Vector4 vector = ToVector(color);
        Rect(rect.X, rect.Y, rect.Width, width, vector);
        Rect(rect.X, rect.Bottom - width, rect.Width, width, vector);
        Rect(rect.X, rect.Y, width, rect.Height, vector);
        Rect(rect.Right - width, rect.Y, width, rect.Height, vector);
    }

    private static Vector4 ToVector(Color color)
        => new(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);

    private void DrawText(OpenGkUiDrawCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Text) || command.Rect.Width <= 0 || command.Rect.Height <= 0)
        {
            return;
        }

        float scale = OpenGkUiVectorFont.ResolveScale(command.TextStyle);
        scale = MathF.Min(scale, MathF.Max(1.0f, (command.Rect.Height - 4) / 7.0f));
        float measuredWidth = OpenGkUiVectorFont.MeasureText(command.Text, scale);
        if (measuredWidth > command.Rect.Width - 4)
        {
            scale = MathF.Max(1.0f, scale * (command.Rect.Width - 4) / MathF.Max(1.0f, measuredWidth));
            measuredWidth = OpenGkUiVectorFont.MeasureText(command.Text, scale);
        }

        float x = command.TextAlign switch
        {
            OpenGkUiTextAlign.Right => command.Rect.Right - measuredWidth - 2,
            OpenGkUiTextAlign.Center => command.Rect.X + (command.Rect.Width - measuredWidth) * 0.5f,
            _ => command.Rect.X + 2,
        };
        float y = command.Rect.Y + (command.Rect.Height - 7.0f * scale) * 0.5f;
        Vector4 color = ToVector(command.Color);

        foreach (char original in command.Text)
        {
            if (x > command.Rect.Right - scale)
            {
                break;
            }

            OpenGkUiVectorGlyph glyph = OpenGkUiVectorFont.ResolveGlyph(original);
            DrawGlyph(glyph, x, y, scale, color);
            x += (glyph.Width + 1) * scale;
        }
    }

    private void DrawGlyph(OpenGkUiVectorGlyph glyph, float x, float y, float scale, Vector4 color)
    {
        for (int row = 0; row < glyph.Rows.Count; row++)
        {
            string line = glyph.Rows[row];
            for (int column = 0; column < line.Length; column++)
            {
                if (line[column] == '1')
                {
                    Rect(x + column * scale, y + row * scale, scale, scale, color);
                }
            }
        }
    }

    private static int BuildProgram()
    {
        const string vertexSource = """
            #version 330 core
            layout(location = 0) in vec2 aPosition;
            layout(location = 1) in vec4 aColor;
            uniform vec2 uViewport;
            out vec4 vColor;
            void main()
            {
                vec2 ndc = vec2((aPosition.x / uViewport.x) * 2.0 - 1.0, 1.0 - (aPosition.y / uViewport.y) * 2.0);
                gl_Position = vec4(ndc, 0.0, 1.0);
                vColor = aColor;
            }
            """;
        const string fragmentSource = """
            #version 330 core
            in vec4 vColor;
            out vec4 FragColor;
            void main()
            {
                FragColor = vColor;
            }
            """;

        int vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        int fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = GL.GetProgramInfoLog(program);
            throw new InvalidOperationException($"Failed to link Linux operator shader: {log}");
        }

        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"Failed to compile {type}: {log}");
        }

        return shader;
    }

    private readonly record struct Vertex(float X, float Y, float R, float G, float B, float A)
    {
        public const int SizeInBytes = 6 * sizeof(float);
    }
}
