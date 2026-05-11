using OpenTK.Graphics.OpenGL4;

namespace Simulator.OpenTk.Rendering;

public sealed class OpenTkGpuSceneRenderer
{
    private readonly IOpenTkGpuSceneRuntime _runtime;

    public OpenTkGpuSceneRenderer(IOpenTkGpuSceneRuntime runtime)
    {
        _runtime = runtime;
    }

    public bool TryRenderToCurrentContext()
    {
        GL.UseProgram(0);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return _runtime.ExternalRenderToCurrentOpenGlContext();
    }
}
