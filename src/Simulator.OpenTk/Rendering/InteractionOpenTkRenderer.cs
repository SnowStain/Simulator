using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Simulator.OpenTk.Rendering;

public sealed class InteractionOpenTkRenderer : IDisposable
{
    private const int MaxLineVertices = 24000;

    private readonly List<Vertex> _vertices = new(8192);
    private int _program;
    private int _viewProjectionUniform;
    private int _vertexArray;
    private int _vertexBuffer;

    public void Load()
    {
        _program = BuildProgram();
        _viewProjectionUniform = GL.GetUniformLocation(_program, "uViewProjection");
        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, MaxLineVertices * Vertex.SizeInBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vertex.SizeInBytes, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, Vertex.SizeInBytes, 3 * sizeof(float));
        GL.BindVertexArray(0);
    }

    public void Render(IReadOnlyList<OpenTkInteractionRenderItem> items, Matrix4 viewProjection, double timeSec)
    {
        if (_program == 0 || items.Count == 0)
        {
            return;
        }

        _vertices.Clear();
        foreach (OpenTkInteractionRenderItem item in items)
        {
            switch (item.Kind)
            {
                case "energy":
                    AddEnergyMechanism(item, timeSec);
                    break;
                case "base_armor":
                    AddBaseArmor(item);
                    break;
                case "outpost":
                    AddOutpost(item, timeSec);
                    break;
                case "collision":
                    AddVolume(item, new Vector4(0.0f, 0.85f, 1.0f, 0.72f));
                    break;
                case "buff":
                    AddVolume(item, ResolveTeamColor(item.Team, 0.64f, neutralYellow: true));
                    break;
            }
        }

        if (_vertices.Count == 0)
        {
            return;
        }

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(false);
        GL.LineWidth(2.0f);
        GL.UseProgram(_program);
        GL.UniformMatrix4(_viewProjectionUniform, false, ref viewProjection);
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        int vertexCount = Math.Min(_vertices.Count, MaxLineVertices);
        GL.BufferData(BufferTarget.ArrayBuffer, vertexCount * Vertex.SizeInBytes, _vertices.Take(vertexCount).ToArray(), BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, vertexCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.LineWidth(1.0f);
        GL.DepthMask(true);
        GL.Disable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        if (_vertexBuffer != 0)
        {
            GL.DeleteBuffer(_vertexBuffer);
            _vertexBuffer = 0;
        }

        if (_vertexArray != 0)
        {
            GL.DeleteVertexArray(_vertexArray);
            _vertexArray = 0;
        }

        if (_program != 0)
        {
            GL.DeleteProgram(_program);
            _program = 0;
        }
    }

    private void AddVolume(OpenTkInteractionRenderItem item, Vector4 color)
    {
        float halfHeight = MathF.Max(0.01f, item.SizeZModel * 0.5f);
        if (item.ScenePoints.Count >= 3)
        {
            var bottom = item.ScenePoints.Select(point => new Vector3(point.X, point.Y - halfHeight, point.Z)).ToArray();
            var top = item.ScenePoints.Select(point => new Vector3(point.X, point.Y + halfHeight, point.Z)).ToArray();
            AddLoop(bottom, color);
            AddLoop(top, color);
            for (int index = 0; index < bottom.Length; index++)
            {
                AddLine(bottom[index], top[index], color);
            }

            return;
        }

        Vector3 center = item.Center;
        Vector3 right;
        Vector3 forward;
        ResolveYawAxes(item.SceneYawDeg, out forward, out right);
        float hx = MathF.Max(0.01f, item.SizeXModel * 0.5f);
        float hz = MathF.Max(0.01f, item.SizeYModel * 0.5f);
        var bottomCorners = new[]
        {
            center - right * hx - forward * hz - Vector3.UnitY * halfHeight,
            center + right * hx - forward * hz - Vector3.UnitY * halfHeight,
            center + right * hx + forward * hz - Vector3.UnitY * halfHeight,
            center - right * hx + forward * hz - Vector3.UnitY * halfHeight,
        };
        var topCorners = bottomCorners.Select(point => point + Vector3.UnitY * halfHeight * 2.0f).ToArray();
        AddLoop(bottomCorners, color);
        AddLoop(topCorners, color);
        for (int index = 0; index < 4; index++)
        {
            AddLine(bottomCorners[index], topCorners[index], color);
        }
    }

    private void AddEnergyMechanism(OpenTkInteractionRenderItem item, double timeSec)
    {
        Vector4 team = ResolveTeamColor(item.Team, 0.96f, neutralYellow: false);
        Vector4 dim = new(team.X * 0.22f, team.Y * 0.22f, team.Z * 0.22f, 0.72f);
        Vector4 ready = new(team.X, team.Y, team.Z, 0.98f);
        Vector3 center = item.Center;
        float radius = MathF.Max(0.10f, item.RadiusModel > 0 ? item.RadiusModel : MathF.Max(item.SizeXModel, item.SizeYModel) * 0.5f);
        float armRadius = radius * 0.44f;
        float ringRadius = radius * 0.08f;
        Vector3 planeRight = Vector3.UnitX;
        Vector3 planeUp = Vector3.UnitY;

        AddCircle(center, radius * 0.14f, planeRight, planeUp, ready, 24);
        for (int index = 0; index < 5; index++)
        {
            float angle = MathHelper.DegreesToRadians(-90.0f + index * 72.0f + item.SceneYawDeg);
            Vector3 dir = planeRight * MathF.Cos(angle) + planeUp * MathF.Sin(angle);
            Vector3 armCenter = center + dir * radius * 0.72f;
            bool active = (item.ActivatedMask & (1 << index)) != 0;
            bool lit = (item.LitMask & (1 << index)) != 0;
            Vector4 color = active || lit ? ready : dim;
            if (item.LargeEnergy && !active && !lit && item.Progress > 0.0)
            {
                color = index < Math.Ceiling(item.Progress * 5.0) ? new Vector4(team.X, team.Y, team.Z, 0.76f) : dim;
            }

            AddLine(center + dir * radius * 0.24f, armCenter - dir * armRadius * 0.72f, color);
            AddCircle(armCenter, armRadius, planeRight, planeUp, color, 28);
            if (lit && !active)
            {
                AddReadyMarker(armCenter, armRadius * 0.82f, team);
            }
        }

        if (item.ActivatedCount >= 5 || item.Progress >= 0.999)
        {
            AddCircle(center, radius * 1.02f, planeRight, planeUp, ready, 64);
        }
        else
        {
            float phase = (float)((timeSec * 3.0) % 1.0);
            AddCircle(center, radius * (0.96f + phase * 0.06f), planeRight, planeUp, new Vector4(team.X, team.Y, team.Z, 0.28f), 64);
        }
    }

    private void AddReadyMarker(Vector3 center, float radius, Vector4 color)
    {
        Vector3 right = Vector3.UnitX;
        Vector3 up = Vector3.UnitY;
        AddCircle(center, radius * 0.30f, right, up, color, 24);
        AddCircle(center, radius * 0.58f, right, up, color, 32);
        AddCircle(center, radius, right, up, color, 40);
        for (int index = 0; index < 4; index++)
        {
            float angle = MathHelper.DegreesToRadians(index * 90.0f);
            Vector3 dir = right * MathF.Cos(angle) + up * MathF.Sin(angle);
            Vector3 tangent = new(-dir.Y, dir.X, 0.0f);
            AddLine(center + dir * radius * 0.30f - tangent * radius * 0.06f, center + dir * radius - tangent * radius * 0.13f, color);
            AddLine(center + dir * radius * 0.30f + tangent * radius * 0.06f, center + dir * radius + tangent * radius * 0.13f, color);
        }
    }

    private void AddBaseArmor(OpenTkInteractionRenderItem item)
    {
        Vector4 color = ResolveTeamColor(item.Team, item.Progress > 0.0 ? 0.96f : 0.42f, neutralYellow: false);
        Vector3 center = item.Center;
        float radius = MathF.Max(0.2f, item.RadiusModel);
        float panelWidth = MathF.Max(0.08f, item.SizeYModel * 0.42f);
        float panelHeight = MathF.Max(0.08f, item.SizeZModel);
        for (int index = 0; index < 3; index++)
        {
            float angle = MathHelper.DegreesToRadians(item.SceneYawDeg + index * 120.0f);
            Vector3 outward = new(MathF.Cos(angle), 0.0f, MathF.Sin(angle));
            Vector3 tangent = new(-outward.Z, 0.0f, outward.X);
            Vector3 openOffset = outward * (radius * 0.22f * (float)item.Progress) - Vector3.UnitY * (panelHeight * 0.30f * (float)item.Progress);
            Vector3 panelCenter = center + outward * radius + openOffset;
            Vector3 a = panelCenter - tangent * panelWidth - Vector3.UnitY * panelHeight;
            Vector3 b = panelCenter + tangent * panelWidth - Vector3.UnitY * panelHeight;
            Vector3 c = panelCenter + tangent * panelWidth + Vector3.UnitY * panelHeight;
            Vector3 d = panelCenter - tangent * panelWidth + Vector3.UnitY * panelHeight;
            AddLine(a, b, color);
            AddLine(b, c, color);
            AddLine(c, d, color);
            AddLine(d, a, color);
            AddLine(a, c, new Vector4(color.X, color.Y, color.Z, color.W * 0.45f));
        }
    }

    private void AddOutpost(OpenTkInteractionRenderItem item, double timeSec)
    {
        Vector4 color = item.Stopped
            ? new Vector4(0.86f, 0.88f, 0.92f, 0.54f)
            : ResolveTeamColor(item.Team, 0.92f, neutralYellow: false);
        Vector3 center = item.Center;
        float radius = MathF.Max(0.16f, item.RadiusModel);
        AddCircle(center, radius, Vector3.UnitX, Vector3.UnitZ, color, 48);
        if (!item.Stopped)
        {
            float angle = MathHelper.DegreesToRadians((float)(item.SceneYawDeg + timeSec * 130.0));
            Vector3 dir = new(MathF.Cos(angle), 0.0f, MathF.Sin(angle));
            AddLine(center - dir * radius, center + dir * radius, color);
        }
    }

    private void AddCircle(Vector3 center, float radius, Vector3 axisA, Vector3 axisB, Vector4 color, int segments)
    {
        Vector3 previous = center + axisA * radius;
        for (int index = 1; index <= segments; index++)
        {
            float angle = MathF.Tau * index / segments;
            Vector3 next = center + axisA * (MathF.Cos(angle) * radius) + axisB * (MathF.Sin(angle) * radius);
            AddLine(previous, next, color);
            previous = next;
        }
    }

    private void AddLoop(IReadOnlyList<Vector3> points, Vector4 color)
    {
        if (points.Count < 2)
        {
            return;
        }

        for (int index = 0; index < points.Count; index++)
        {
            AddLine(points[index], points[(index + 1) % points.Count], color);
        }
    }

    private void AddLine(Vector3 a, Vector3 b, Vector4 color)
    {
        if (_vertices.Count + 2 > MaxLineVertices)
        {
            return;
        }

        _vertices.Add(new Vertex(a.X, a.Y, a.Z, color.X, color.Y, color.Z, color.W));
        _vertices.Add(new Vertex(b.X, b.Y, b.Z, color.X, color.Y, color.Z, color.W));
    }

    private static void ResolveYawAxes(double yawDeg, out Vector3 forward, out Vector3 right)
    {
        float yaw = MathHelper.DegreesToRadians((float)-yawDeg);
        forward = new Vector3(MathF.Cos(yaw), 0.0f, MathF.Sin(yaw));
        right = new Vector3(-forward.Z, 0.0f, forward.X);
    }

    private static Vector4 ResolveTeamColor(string team, float alpha, bool neutralYellow)
        => string.Equals(team, "red", StringComparison.OrdinalIgnoreCase)
            ? new Vector4(1.0f, 0.0f, 0.0f, alpha)
            : string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase)
                ? new Vector4(0.0f, 0.12f, 1.0f, alpha)
                : neutralYellow
                    ? new Vector4(1.0f, 0.78f, 0.18f, alpha)
                    : new Vector4(0.38f, 0.92f, 1.0f, alpha);

    private static int BuildProgram()
    {
        const string vertexSource = """
            #version 330 core
            layout(location = 0) in vec3 aPosition;
            layout(location = 1) in vec4 aColor;

            uniform mat4 uViewProjection;

            out vec4 vColor;

            void main()
            {
                gl_Position = uViewProjection * vec4(aPosition, 1.0);
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
            throw new InvalidOperationException($"Failed to link interaction shader: {log}");
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Vertex(
        float X,
        float Y,
        float Z,
        float R,
        float G,
        float B,
        float A)
    {
        public const int SizeInBytes = 7 * sizeof(float);
    }
}

public sealed record OpenTkInteractionRenderItem(
    string Id,
    string Kind,
    string Type,
    string Team,
    Vector3 Center,
    float SceneYawDeg,
    float SizeXModel,
    float SizeYModel,
    float SizeZModel,
    float RadiusModel,
    int LitMask,
    int ActivatedMask,
    int ActivatedCount,
    bool LargeEnergy,
    bool Stopped,
    double Progress,
    IReadOnlyList<Vector3> ScenePoints);
