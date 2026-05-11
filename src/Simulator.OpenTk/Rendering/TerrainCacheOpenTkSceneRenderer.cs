using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Simulator.Assets;

namespace Simulator.OpenTk.Rendering;

public sealed class TerrainCacheOpenTkSceneRenderer : IDisposable
{
    private readonly List<GpuChunk> _chunks = new();
    private int _program;
    private int _viewProjectionUniform;
    private int _cameraUniform;
    private int _fogNearUniform;
    private int _fogFarUniform;
    private TerrainCacheCatalog? _catalog;
    private string _loadedPath = string.Empty;
    private Matrix4 _lastViewProjection = Matrix4.Identity;
    private Vector3 _lastCameraPosition = Vector3.Zero;
    private float _lastSceneRadius = 1.0f;

    public bool IsLoaded => _chunks.Count > 0 && _catalog is not null;

    public string LoadedPath => _loadedPath;

    public int ChunkCount => _chunks.Count;

    public Matrix4 LastViewProjection => _lastViewProjection;

    public Vector3 LastCameraPosition => _lastCameraPosition;

    public float LastSceneRadius => _lastSceneRadius;

    public void Load()
    {
        _program = BuildProgram();
        _viewProjectionUniform = GL.GetUniformLocation(_program, "uViewProjection");
        _cameraUniform = GL.GetUniformLocation(_program, "uCameraPosition");
        _fogNearUniform = GL.GetUniformLocation(_program, "uFogNear");
        _fogFarUniform = GL.GetUniformLocation(_program, "uFogFar");
    }

    public void LoadTerrainCache(string cachePath)
    {
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(cachePath), _loadedPath, StringComparison.OrdinalIgnoreCase) && IsLoaded)
        {
            return;
        }

        ClearChunks();

        var reader = new TerrainCacheMeshReader();
        int chunkIndex = 0;
        _catalog = reader.Load(
            cachePath,
            (_, chunk, vertices, indices, _) =>
            {
                if (vertices.Length == 0 || indices.Length == 0)
                {
                    return;
                }

                _chunks.Add(GpuChunk.Create(chunkIndex++, vertices, indices));
            });
        _loadedPath = Path.GetFullPath(cachePath);
    }

    public void Render(int width, int height, double timeSec, OpenTkSceneCamera? camera = null)
    {
        if (!IsLoaded || _catalog is null)
        {
            return;
        }

        int viewportWidth = Math.Max(1, width);
        int viewportHeight = Math.Max(1, height);
        GL.Viewport(0, 0, viewportWidth, viewportHeight);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(_program);

        Matrix4 viewProjection = BuildViewProjection(_catalog, viewportWidth / (float)viewportHeight, timeSec, camera, out Vector3 cameraPosition, out float sceneRadius);
        _lastViewProjection = viewProjection;
        _lastCameraPosition = cameraPosition;
        _lastSceneRadius = sceneRadius;
        GL.UniformMatrix4(_viewProjectionUniform, false, ref viewProjection);
        GL.Uniform3(_cameraUniform, cameraPosition);
        GL.Uniform1(_fogNearUniform, sceneRadius * 0.45f);
        GL.Uniform1(_fogFarUniform, sceneRadius * 1.65f);

        foreach (GpuChunk chunk in _chunks)
        {
            chunk.Draw();
        }

        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        ClearChunks();
        if (_program != 0)
        {
            GL.DeleteProgram(_program);
            _program = 0;
        }
    }

    private void ClearChunks()
    {
        foreach (GpuChunk chunk in _chunks)
        {
            chunk.Dispose();
        }

        _chunks.Clear();
        _catalog = null;
        _loadedPath = string.Empty;
    }

    private static Matrix4 BuildViewProjection(
        TerrainCacheCatalog catalog,
        float aspect,
        double timeSec,
        OpenTkSceneCamera? camera,
        out Vector3 cameraPosition,
        out float sceneRadius)
    {
        Vector3 min = new(catalog.MinX, catalog.MinY, catalog.MinZ);
        Vector3 max = new(catalog.MaxX, catalog.MaxY, catalog.MaxZ);
        Vector3 center = (min + max) * 0.5f;
        Vector3 extents = Vector3.ComponentMax(max - min, new Vector3(1.0f));
        sceneRadius = MathF.Max(1.0f, extents.Length * 0.5f);

        Vector3 target;
        Vector3 up;
        if (camera is not null)
        {
            cameraPosition = camera.Position;
            target = camera.Target;
            up = camera.Up.LengthSquared > 1e-5f ? Vector3.Normalize(camera.Up) : Vector3.UnitY;
        }
        else
        {
            float yaw = MathHelper.DegreesToRadians(38.0f + MathF.Sin((float)timeSec * 0.04f) * 1.4f);
            float distance = sceneRadius * 1.24f;
            float height = sceneRadius * 0.70f;
            cameraPosition = center + new Vector3(MathF.Cos(yaw) * distance, height, MathF.Sin(yaw) * distance);
            target = center + new Vector3(0.0f, -sceneRadius * 0.08f, 0.0f);
            up = Vector3.UnitY;
        }

        Matrix4 view = Matrix4.LookAt(cameraPosition, target, up);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(48.0f),
            Math.Max(0.05f, aspect),
            Math.Max(0.02f, sceneRadius * 0.002f),
            Math.Max(120.0f, sceneRadius * 4.0f));
        return view * projection;
    }

    private static int BuildProgram()
    {
        const string vertexSource = """
            #version 330 core
            layout(location = 0) in vec3 aPosition;
            layout(location = 1) in vec3 aNormal;
            layout(location = 2) in vec4 aColor;

            uniform mat4 uViewProjection;

            out vec3 vWorldPosition;
            out vec3 vNormal;
            out vec3 vColor;

            void main()
            {
                vec4 worldPosition = vec4(aPosition, 1.0);
                gl_Position = uViewProjection * worldPosition;
                vWorldPosition = aPosition;
                vNormal = normalize(aNormal);
                vColor = aColor.rgb;
            }
            """;
        const string fragmentSource = """
            #version 330 core
            in vec3 vWorldPosition;
            in vec3 vNormal;
            in vec3 vColor;

            uniform vec3 uCameraPosition;
            uniform float uFogNear;
            uniform float uFogFar;

            out vec4 FragColor;

            void main()
            {
                vec3 normal = normalize(vNormal);
                vec3 lightDirection = normalize(vec3(0.36, 0.88, 0.28));
                float diffuse = max(dot(normal, lightDirection), 0.0);
                float sideFill = max(dot(normal, normalize(vec3(-0.55, 0.30, -0.32))), 0.0) * 0.14;
                float lighting = 0.30 + diffuse * 0.62 + sideFill;
                vec3 litColor = clamp(vColor * lighting, vec3(0.0), vec3(1.0));
                float distanceToCamera = distance(vWorldPosition, uCameraPosition);
                float fogFactor = smoothstep(uFogNear, uFogFar, distanceToCamera);
                vec3 fogColor = vec3(0.055, 0.068, 0.078);
                FragColor = vec4(mix(litColor, fogColor, fogFactor * 0.82), 1.0);
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
            throw new InvalidOperationException($"Failed to link terrain scene shader: {log}");
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

    private sealed class GpuChunk : IDisposable
    {
        private readonly int _vao;
        private readonly int _vbo;
        private readonly int _ebo;
        private readonly int _indexCount;

        private GpuChunk(int vao, int vbo, int ebo, int indexCount)
        {
            _vao = vao;
            _vbo = vbo;
            _ebo = ebo;
            _indexCount = indexCount;
        }

        public static GpuChunk Create(int chunkIndex, TerrainCacheVertex[] vertices, int[] indices)
        {
            var packedVertices = new SceneVertex[vertices.Length];
            for (int index = 0; index < vertices.Length; index++)
            {
                TerrainCacheVertex vertex = vertices[index];
                packedVertices[index] = new SceneVertex(
                    vertex.X,
                    vertex.Y,
                    vertex.Z,
                    vertex.NormalX,
                    vertex.NormalY,
                    vertex.NormalZ,
                    vertex.R,
                    vertex.G,
                    vertex.B,
                    vertex.A == 0 ? byte.MaxValue : vertex.A);
            }

            uint[] packedIndices = new uint[indices.Length];
            for (int index = 0; index < indices.Length; index++)
            {
                packedIndices[index] = checked((uint)Math.Max(0, indices[index]));
            }

            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            int ebo = GL.GenBuffer();
            int stride = Marshal.SizeOf<SceneVertex>();

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, packedVertices.Length * stride, packedVertices, BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, packedIndices.Length * sizeof(uint), packedIndices, BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 12);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 24);
            GL.BindVertexArray(0);

            return new GpuChunk(vao, vbo, ebo, packedIndices.Length);
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
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct SceneVertex
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float NormalX;
        public readonly float NormalY;
        public readonly float NormalZ;
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte A;

        public SceneVertex(
            float x,
            float y,
            float z,
            float normalX,
            float normalY,
            float normalZ,
            byte r,
            byte g,
            byte b,
            byte a)
        {
            X = x;
            Y = y;
            Z = z;
            NormalX = normalX;
            NormalY = normalY;
            NormalZ = normalZ;
            R = r;
            G = g;
            B = b;
            A = a;
        }
    }
}
