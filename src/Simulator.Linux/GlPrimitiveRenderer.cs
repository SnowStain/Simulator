using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

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
