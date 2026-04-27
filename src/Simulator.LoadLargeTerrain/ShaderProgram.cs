using OpenTK.Graphics.OpenGL4;

namespace LoadLargeTerrain;

internal sealed class ShaderProgram : IDisposable
{
    public ShaderProgram(string vertexSource, string fragmentSource)
    {
        var vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vertexShader);
        GL.AttachShader(Handle, fragmentShader);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out var linkStatus);
        if (linkStatus == 0)
        {
            var log = GL.GetProgramInfoLog(Handle);
            GL.DeleteProgram(Handle);
            throw new InvalidOperationException($"Shader link failed: {log}");
        }

        GL.DetachShader(Handle, vertexShader);
        GL.DetachShader(Handle, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
    }

    public int Handle { get; }

    public void Use()
    {
        GL.UseProgram(Handle);
    }

    public int GetUniformLocation(string name)
    {
        return GL.GetUniformLocation(Handle, name);
    }

    public void Dispose()
    {
        GL.DeleteProgram(Handle);
    }

    private static int CompileShader(ShaderType shaderType, string source)
    {
        var handle = GL.CreateShader(shaderType);
        GL.ShaderSource(handle, source);
        GL.CompileShader(handle);
        GL.GetShader(handle, ShaderParameter.CompileStatus, out var status);
        if (status == 0)
        {
            var log = GL.GetShaderInfoLog(handle);
            GL.DeleteShader(handle);
            throw new InvalidOperationException($"{shaderType} compilation failed: {log}");
        }

        return handle;
    }
}
