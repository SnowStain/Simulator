using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Simulator.Assets;

namespace Simulator.OpenTk.Rendering;

public sealed class RobotAppearanceOpenTkRenderer : IDisposable
{
    private int _program;
    private int _modelViewProjectionUniform;
    private int _modelUniform;
    private int _colorUniform;
    private PrimitiveMesh? _box;
    private PrimitiveMesh? _cylinder;

    public void Load()
    {
        _program = BuildProgram();
        _modelViewProjectionUniform = GL.GetUniformLocation(_program, "uModelViewProjection");
        _modelUniform = GL.GetUniformLocation(_program, "uModel");
        _colorUniform = GL.GetUniformLocation(_program, "uColor");
        _box = PrimitiveMesh.CreateBox();
        _cylinder = PrimitiveMesh.CreateCylinderX(24);
    }

    public void Render(IReadOnlyList<OpenTkRobotRenderItem> robots, Matrix4 viewProjection)
    {
        if (_program == 0 || _box is null || _cylinder is null || robots.Count == 0)
        {
            return;
        }

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(_program);

        foreach (OpenTkRobotRenderItem robot in robots)
        {
            if (!robot.IsAlive || !string.Equals(robot.EntityType, "robot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DrawRobot(robot, viewProjection);
        }

        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _box?.Dispose();
        _box = null;
        _cylinder?.Dispose();
        _cylinder = null;
        if (_program != 0)
        {
            GL.DeleteProgram(_program);
            _program = 0;
        }
    }

    private void DrawRobot(OpenTkRobotRenderItem robot, Matrix4 viewProjection)
    {
        RobotAppearanceProfileDefinition? profile = robot.AppearanceProfile;
        float unit = MathF.Max(0.05f, robot.ModelUnitsPerMeter);
        float bodyLength = Meters(profile?.BodyLengthM ?? robot.BodyLengthM, unit, 0.48f);
        float bodyWidth = Meters((profile?.BodyWidthM ?? robot.BodyWidthM) * Math.Max(0.45, profile?.BodyRenderWidthScale ?? robot.BodyRenderWidthScale), unit, 0.48f);
        float bodyHeight = Meters(profile?.BodyHeightM ?? robot.BodyHeightM, unit, 0.18f);
        float bodyClearance = Meters(profile?.BodyClearanceM ?? robot.BodyClearanceM, unit, 0.10f);
        float wheelRadius = Meters(profile?.WheelRadiusM ?? robot.WheelRadiusM, unit, 0.08f);
        float wheelThickness = MathF.Max(unit * 0.035f, bodyWidth * 0.13f);
        float mountLength = Meters(profile?.GimbalMountLengthM ?? robot.GimbalMountLengthM, unit, 0.12f);
        float mountWidth = Meters(profile?.GimbalMountWidthM ?? robot.GimbalMountWidthM, unit, 0.10f);
        float mountHeight = Meters(profile?.GimbalMountHeightM ?? robot.GimbalMountHeightM, unit, 0.05f);
        float gimbalLength = Meters(profile?.GimbalLengthM ?? robot.GimbalLengthM, unit, 0.26f);
        float gimbalWidth = Meters(profile?.GimbalWidthM ?? robot.GimbalWidthM, unit, 0.16f);
        float gimbalHeight = Meters(profile?.GimbalBodyHeightM ?? robot.GimbalBodyHeightM, unit, 0.10f);
        float barrelLength = Meters(profile?.BarrelLengthM ?? robot.BarrelLengthM, unit, 0.18f);
        float barrelRadius = Meters(profile?.BarrelRadiusM ?? robot.BarrelRadiusM, unit, 0.016f);

        Matrix4 chassisRoot = BuildRoot(robot.Position, robot.SceneForward, robot.ChassisPitchDeg, robot.ChassisRollDeg);
        Matrix4 turretRoot = BuildRoot(robot.Position, robot.SceneTurretForward, robot.ChassisPitchDeg, robot.ChassisRollDeg);

        Vector4 bodyColor = ResolveColor(profile?.BodyColorRgb, robot.Team, body: true);
        Vector4 turretColor = ResolveColor(profile?.TurretColorRgb, robot.Team, body: false);
        Vector4 wheelColor = ResolveColor(profile?.WheelColorRgb, robot.Team, wheel: true);

        DrawBox(new Vector3(bodyLength, bodyHeight, bodyWidth), new Vector3(0.0f, bodyClearance + bodyHeight * 0.5f, 0.0f), chassisRoot, bodyColor, viewProjection);

        IReadOnlyList<(double X, double Y)> wheels = profile?.GetWheelOffsetsOrDefaults() ?? robot.WheelOffsetsM;
        if (wheels.Count == 0)
        {
            double lx = bodyLength * 0.38f / unit;
            double ly = bodyWidth * 0.42f / unit;
            wheels = new[] { (-lx, -ly), (lx, -ly), (-lx, ly), (lx, ly) };
        }

        foreach ((double x, double y) in wheels)
        {
            var offset = new Vector3((float)x * unit, wheelRadius, (float)y * unit);
            Matrix4 wheelLocal =
                Matrix4.CreateScale(wheelThickness, wheelRadius * 2.0f, wheelRadius * 2.0f)
                * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(90.0f))
                * Matrix4.CreateTranslation(offset);
            DrawMesh(_cylinder!, wheelLocal * chassisRoot, wheelColor, viewProjection);
        }

        float bodyTop = bodyClearance + bodyHeight;
        float mountY = bodyTop + Meters(profile?.GimbalMountGapM ?? robot.GimbalMountGapM, unit, 0.05f) + mountHeight * 0.5f;
        DrawBox(new Vector3(mountLength, mountHeight, mountWidth), new Vector3(0.0f, mountY, 0.0f), turretRoot, turretColor, viewProjection);

        float gimbalY = bodyTop + Meters(profile?.GimbalHeightM ?? robot.GimbalHeightM, unit, 0.34f);
        DrawBox(new Vector3(gimbalLength, gimbalHeight, gimbalWidth), new Vector3(0.0f, gimbalY, 0.0f), turretRoot, turretColor, viewProjection);

        Matrix4 barrelPitch = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians((float)-robot.GimbalPitchDeg));
        var barrelOffset = new Vector3(gimbalLength * 0.5f + barrelLength * 0.5f, gimbalY, 0.0f);
        Matrix4 barrelModel =
            Matrix4.CreateScale(barrelLength, barrelRadius * 2.0f, barrelRadius * 2.0f)
            * barrelPitch
            * Matrix4.CreateTranslation(barrelOffset)
            * turretRoot;
        DrawMesh(_cylinder!, barrelModel, turretColor, viewProjection);

        if (profile?.CustomPrimitives is { Count: > 0 } customPrimitives)
        {
            foreach (RobotAppearanceCustomPrimitiveDefinition primitive in customPrimitives)
            {
                DrawCustomPrimitive(primitive, chassisRoot, turretRoot, unit, viewProjection);
            }
        }
    }

    private void DrawCustomPrimitive(
        RobotAppearanceCustomPrimitiveDefinition primitive,
        Matrix4 chassisRoot,
        Matrix4 turretRoot,
        float unit,
        Matrix4 viewProjection)
    {
        Vector3 size = ToVector3(primitive.SizeM, new Vector3(0.06f, 0.04f, 0.04f)) * unit;
        Vector3 offset = ToVector3(primitive.OffsetM, Vector3.Zero) * unit;
        Vector3 ypr = ToVector3(primitive.RotationYprDeg, Vector3.Zero);
        Matrix4 root = IsTurretParent(primitive.ParentPart) ? turretRoot : chassisRoot;
        Matrix4 localRotation =
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(-ypr.X))
            * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(-ypr.Y))
            * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(ypr.Z));
        Matrix4 model = Matrix4.CreateScale(size) * localRotation * Matrix4.CreateTranslation(offset) * root;
        Vector4 color = ResolveColor(primitive.ColorRgb, "neutral", body: false);
        if (primitive.PrimitiveType.Contains("cylinder", StringComparison.OrdinalIgnoreCase))
        {
            DrawMesh(_cylinder!, model, color, viewProjection);
        }
        else
        {
            DrawMesh(_box!, model, color, viewProjection);
        }
    }

    private void DrawBox(Vector3 size, Vector3 offset, Matrix4 root, Vector4 color, Matrix4 viewProjection)
    {
        Matrix4 model = Matrix4.CreateScale(size) * Matrix4.CreateTranslation(offset) * root;
        DrawMesh(_box!, model, color, viewProjection);
    }

    private void DrawMesh(PrimitiveMesh mesh, Matrix4 model, Vector4 color, Matrix4 viewProjection)
    {
        Matrix4 mvp = model * viewProjection;
        GL.UniformMatrix4(_modelViewProjectionUniform, false, ref mvp);
        GL.UniformMatrix4(_modelUniform, false, ref model);
        GL.Uniform4(_colorUniform, color);
        mesh.Draw();
    }

    private static Matrix4 BuildRoot(Vector3 position, Vector3 forward, double pitchDeg, double rollDeg)
    {
        Vector3 safeForward = forward.LengthSquared > 1e-5f ? Vector3.Normalize(new Vector3(forward.X, 0.0f, forward.Z)) : Vector3.UnitX;
        float yaw = -MathF.Atan2(safeForward.Z, safeForward.X);
        Matrix4 localAttitude =
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians((float)rollDeg))
            * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians((float)-pitchDeg));
        return localAttitude * Matrix4.CreateRotationY(yaw) * Matrix4.CreateTranslation(position);
    }

    private static bool IsTurretParent(string? parent)
        => !string.IsNullOrWhiteSpace(parent)
            && (parent.Contains("turret", StringComparison.OrdinalIgnoreCase)
                || parent.Contains("gimbal", StringComparison.OrdinalIgnoreCase)
                || parent.Contains("barrel", StringComparison.OrdinalIgnoreCase)
                || parent.Contains("mount", StringComparison.OrdinalIgnoreCase));

    private static float Meters(double value, float modelUnitsPerMeter, float fallbackMeters)
        => MathF.Max(0.001f, (float)(value > 1e-6 ? value : fallbackMeters) * modelUnitsPerMeter);

    private static Vector3 ToVector3(IReadOnlyList<double>? values, Vector3 fallback)
    {
        if (values is { Count: >= 3 })
        {
            return new Vector3((float)values[0], (float)values[1], (float)values[2]);
        }

        return fallback;
    }

    private static Vector4 ResolveColor(IReadOnlyList<int>? rgb, string team, bool body = false, bool wheel = false)
    {
        if (rgb is { Count: >= 3 })
        {
            return new Vector4(
                Math.Clamp(rgb[0], 0, 255) / 255.0f,
                Math.Clamp(rgb[1], 0, 255) / 255.0f,
                Math.Clamp(rgb[2], 0, 255) / 255.0f,
                1.0f);
        }

        if (wheel)
        {
            return new Vector4(0.10f, 0.11f, 0.12f, 1.0f);
        }

        if (string.Equals(team, "red", StringComparison.OrdinalIgnoreCase))
        {
            return body ? new Vector4(0.52f, 0.12f, 0.14f, 1.0f) : new Vector4(1.0f, 0.16f, 0.16f, 1.0f);
        }

        if (string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase))
        {
            return body ? new Vector4(0.13f, 0.22f, 0.46f, 1.0f) : new Vector4(0.10f, 0.30f, 1.0f, 1.0f);
        }

        return new Vector4(0.68f, 0.70f, 0.72f, 1.0f);
    }

    private static int BuildProgram()
    {
        const string vertexSource = """
            #version 330 core
            layout(location = 0) in vec3 aPosition;
            layout(location = 1) in vec3 aNormal;

            uniform mat4 uModelViewProjection;
            uniform mat4 uModel;

            out vec3 vNormal;
            out vec3 vWorldPosition;

            void main()
            {
                vec4 worldPosition = uModel * vec4(aPosition, 1.0);
                gl_Position = uModelViewProjection * vec4(aPosition, 1.0);
                vWorldPosition = worldPosition.xyz;
                vNormal = normalize(mat3(uModel) * aNormal);
            }
            """;
        const string fragmentSource = """
            #version 330 core
            in vec3 vNormal;
            in vec3 vWorldPosition;

            uniform vec4 uColor;

            out vec4 FragColor;

            void main()
            {
                vec3 normal = normalize(vNormal);
                vec3 lightDirection = normalize(vec3(0.34, 0.86, 0.30));
                float diffuse = max(dot(normal, lightDirection), 0.0);
                float rim = max(dot(normal, normalize(vec3(-0.58, 0.22, -0.42))), 0.0) * 0.18;
                float lighting = 0.34 + diffuse * 0.58 + rim;
                FragColor = vec4(clamp(uColor.rgb * lighting, vec3(0.0), vec3(1.0)), uColor.a);
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
            throw new InvalidOperationException($"Failed to link robot shader: {log}");
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

    private sealed class PrimitiveMesh : IDisposable
    {
        private readonly int _vao;
        private readonly int _vbo;
        private readonly int _ebo;
        private readonly int _indexCount;

        private PrimitiveMesh(int vao, int vbo, int ebo, int indexCount)
        {
            _vao = vao;
            _vbo = vbo;
            _ebo = ebo;
            _indexCount = indexCount;
        }

        public static PrimitiveMesh CreateBox()
        {
            var vertices = new[]
            {
                new Vertex(-0.5f, -0.5f,  0.5f, 0, 0, 1), new Vertex(0.5f, -0.5f,  0.5f, 0, 0, 1), new Vertex(0.5f, 0.5f,  0.5f, 0, 0, 1), new Vertex(-0.5f, 0.5f,  0.5f, 0, 0, 1),
                new Vertex(0.5f, -0.5f, -0.5f, 0, 0, -1), new Vertex(-0.5f, -0.5f, -0.5f, 0, 0, -1), new Vertex(-0.5f, 0.5f, -0.5f, 0, 0, -1), new Vertex(0.5f, 0.5f, -0.5f, 0, 0, -1),
                new Vertex(-0.5f, 0.5f,  0.5f, 0, 1, 0), new Vertex(0.5f, 0.5f,  0.5f, 0, 1, 0), new Vertex(0.5f, 0.5f, -0.5f, 0, 1, 0), new Vertex(-0.5f, 0.5f, -0.5f, 0, 1, 0),
                new Vertex(-0.5f, -0.5f, -0.5f, 0, -1, 0), new Vertex(0.5f, -0.5f, -0.5f, 0, -1, 0), new Vertex(0.5f, -0.5f,  0.5f, 0, -1, 0), new Vertex(-0.5f, -0.5f,  0.5f, 0, -1, 0),
                new Vertex(0.5f, -0.5f,  0.5f, 1, 0, 0), new Vertex(0.5f, -0.5f, -0.5f, 1, 0, 0), new Vertex(0.5f, 0.5f, -0.5f, 1, 0, 0), new Vertex(0.5f, 0.5f,  0.5f, 1, 0, 0),
                new Vertex(-0.5f, -0.5f, -0.5f, -1, 0, 0), new Vertex(-0.5f, -0.5f,  0.5f, -1, 0, 0), new Vertex(-0.5f, 0.5f,  0.5f, -1, 0, 0), new Vertex(-0.5f, 0.5f, -0.5f, -1, 0, 0),
            };
            uint[] indices =
            [
                0, 1, 2, 0, 2, 3,
                4, 5, 6, 4, 6, 7,
                8, 9, 10, 8, 10, 11,
                12, 13, 14, 12, 14, 15,
                16, 17, 18, 16, 18, 19,
                20, 21, 22, 20, 22, 23,
            ];
            return Create(vertices, indices);
        }

        public static PrimitiveMesh CreateCylinderX(int segments)
        {
            var vertices = new List<Vertex>();
            var indices = new List<uint>();
            int safeSegments = Math.Max(8, segments);
            for (int i = 0; i < safeSegments; i++)
            {
                float a = i * MathHelper.TwoPi / safeSegments;
                float y = MathF.Cos(a) * 0.5f;
                float z = MathF.Sin(a) * 0.5f;
                vertices.Add(new Vertex(-0.5f, y, z, 0, y * 2.0f, z * 2.0f));
                vertices.Add(new Vertex(0.5f, y, z, 0, y * 2.0f, z * 2.0f));
            }

            int leftCenter = vertices.Count;
            vertices.Add(new Vertex(-0.5f, 0, 0, -1, 0, 0));
            int rightCenter = vertices.Count;
            vertices.Add(new Vertex(0.5f, 0, 0, 1, 0, 0));

            for (int i = 0; i < safeSegments; i++)
            {
                uint a = (uint)(i * 2);
                uint b = (uint)(((i + 1) % safeSegments) * 2);
                uint c = a + 1;
                uint d = b + 1;
                indices.AddRange(new[] { a, c, d, a, d, b });
                indices.AddRange(new[] { (uint)leftCenter, b, a });
                indices.AddRange(new[] { (uint)rightCenter, c, d });
            }

            return Create(vertices.ToArray(), indices.ToArray());
        }

        public void Draw()
        {
            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
        }

        public void Dispose()
        {
            GL.DeleteBuffer(_ebo);
            GL.DeleteBuffer(_vbo);
            GL.DeleteVertexArray(_vao);
        }

        private static PrimitiveMesh Create(Vertex[] vertices, uint[] indices)
        {
            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            int ebo = GL.GenBuffer();
            int stride = Marshal.SizeOf<Vertex>();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * stride, vertices, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 12);
            GL.BindVertexArray(0);
            return new PrimitiveMesh(vao, vbo, ebo, indices.Length);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Vertex
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float NormalX;
        public readonly float NormalY;
        public readonly float NormalZ;

        public Vertex(float x, float y, float z, float normalX, float normalY, float normalZ)
        {
            X = x;
            Y = y;
            Z = z;
            NormalX = normalX;
            NormalY = normalY;
            NormalZ = normalZ;
        }
    }
}

public sealed record OpenTkRobotRenderItem(
    string Id,
    string Team,
    string EntityType,
    string RoleKey,
    Vector3 Position,
    Vector3 SceneForward,
    Vector3 SceneTurretForward,
    double ChassisPitchDeg,
    double ChassisRollDeg,
    double GimbalPitchDeg,
    double BodyLengthM,
    double BodyWidthM,
    double BodyHeightM,
    double BodyClearanceM,
    double BodyRenderWidthScale,
    double WheelRadiusM,
    double GimbalLengthM,
    double GimbalWidthM,
    double GimbalBodyHeightM,
    double GimbalHeightM,
    double GimbalMountGapM,
    double GimbalMountLengthM,
    double GimbalMountWidthM,
    double GimbalMountHeightM,
    double BarrelLengthM,
    double BarrelRadiusM,
    float ModelUnitsPerMeter,
    bool IsAlive,
    bool IsSelected,
    IReadOnlyList<(double X, double Y)> WheelOffsetsM,
    RobotAppearanceProfileDefinition? AppearanceProfile);
