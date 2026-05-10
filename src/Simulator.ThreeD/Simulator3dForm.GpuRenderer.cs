using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using LoadLargeTerrain;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const uint PfdDrawToWindow = 0x00000004;
    private const uint PfdSupportOpenGl = 0x00000020;
    private const uint PfdDoubleBuffer = 0x00000001;
    private const byte PfdTypeRgba = 0;
    private const int GlColorBufferBit = 0x00004000;
    private const int GlDepthBufferBit = 0x00000100;
    private const int GlTriangles = 0x0004;
    private const int GlQuads = 0x0007;
    private const int GlLines = 0x0001;
    private const int GlLineStrip = 0x0003;
    private const int GlLineLoop = 0x0002;
    private const int GlModelView = 0x1700;
    private const int GlProjection = 0x1701;
    private const int GlDepthTest = 0x0B71;
    private const int GlScissorTest = 0x0C11;
    private const int GlTexture2D = 0x0DE1;
    private const int GlFramebuffer = 0x8D40;
    private const int GlRenderbuffer = 0x8D41;
    private const int GlColorAttachment0 = 0x8CE0;
    private const int GlDepthAttachment = 0x8D00;
    private const int GlDepthComponent24 = 0x81A6;
    private const int GlFramebufferComplete = 0x8CD5;
    private const int GlBlend = 0x0BE2;
    private const int GlSrcAlpha = 0x0302;
    private const int GlOneMinusSrcAlpha = 0x0303;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlLinear = 0x2601;
    private const int GlLinearMipmapLinear = 0x2703;
    private const int GlNearest = 0x2600;
    private const int GlRgba = 0x1908;
    private const int GlLuminance = 0x1909;
    private const int GlBgra = 0x80E1;
    private const int GlUnsignedByte = 0x1401;
    private const int GlFloat = 0x1406;
    private const int GlPackAlignment = 0x0D05;
    private const int GlVertexArray = 0x8074;
    private const int GlNormalArray = 0x8075;
    private const int GlColorArray = 0x8076;
    private const int GlLighting = 0x0B50;
    private const int GlLight0 = 0x4000;
    private const int GlLight1 = 0x4001;
    private const int GlColorMaterial = 0x0B57;
    private const int GlNormalize = 0x0BA1;
    private const int GlFrontAndBack = 0x0408;
    private const int GlAmbientAndDiffuse = 0x1602;
    private const int GlAmbient = 0x1200;
    private const int GlDiffuse = 0x1201;
    private const int GlSpecular = 0x1202;
    private const int GlPosition = 0x1203;
    private const int GlShininess = 0x1601;
    private const int GlSmooth = 0x1D01;
    private const int GlArrayBuffer = 0x8892;
    private const int GlElementArrayBuffer = 0x8893;
    private const int GlStaticDraw = 0x88E4;
    private const int GlDynamicDraw = 0x88E8;
    private const int GlUnsignedInt = 0x1405;
    private const float GpuTerrainChunkSizeM = 128.0f;
    private const float TerrainCacheGpuChunkSizeM = 2.0f;
    private const int TerrainCacheGpuMaxUploadsPerFrame = 3;
    private const int TerrainCacheGpuMaxUploadVerticesPerFrame = 1_250_000;
    private const int TerrainCacheGpuMaxResidentVertices = 36_000_000;
    private const float GpuTerrainSmoothFlatSkipHeightM = 0.048f;
    private const double GpuOverlayUploadIntervalSec = 1.0 / 5.0;
    private const double GpuEditorOverlayUploadIntervalSec = 1.0 / 18.0;
    private const double GpuThirdPersonOverlayUploadIntervalSec = 1.0 / 2.5;
    private const double GpuOverlaySlowPhaseThresholdMs = 0.75;

    private enum GpuDynamicBatchKind
    {
        Entity,
        Facility,
        Projectile,
    }

    private enum GpuOverlayLayerKind
    {
        Scene,
        Ui,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort Size;
        public ushort Version;
        public uint Flags;
        public byte PixelType;
        public byte ColorBits;
        public byte RedBits;
        public byte RedShift;
        public byte GreenBits;
        public byte GreenShift;
        public byte BlueBits;
        public byte BlueShift;
        public byte AlphaBits;
        public byte AlphaShift;
        public byte AccumBits;
        public byte AccumRedBits;
        public byte AccumGreenBits;
        public byte AccumBlueBits;
        public byte AccumAlphaBits;
        public byte DepthBits;
        public byte StencilBits;
        public byte AuxBuffers;
        public byte LayerType;
        public byte Reserved;
        public uint LayerMask;
        public uint VisibleMask;
        public uint DamageMask;
    }

    private IntPtr _gpuDeviceContext;
    private IntPtr _gpuRenderContext;
    private bool _gpuContextReady;
    private bool _gpuContextFailed;
    private bool _gpuContextBorrowedExternally;
    private int _gpuTerrainTexture;
    private string? _gpuTerrainTexturePath;
    private Size _gpuTerrainTextureSize = Size.Empty;
    private Bitmap? _gpuOverlayBitmap;
    private Graphics? _gpuOverlayGraphics;
    private float _gpuOverlaySurfaceScale = 1f;
    private Size _gpuOverlayLogicalSize = Size.Empty;
    private Bitmap? _gpuOverlaySceneBitmap;
    private Graphics? _gpuOverlaySceneGraphics;
    private int _gpuOverlaySceneTexture;
    private Size _gpuOverlaySceneTextureSize = Size.Empty;
    private Bitmap? _gpuOverlayUiBitmap;
    private Graphics? _gpuOverlayUiGraphics;
    private int _gpuOverlayUiTexture;
    private Size _gpuOverlayUiTextureSize = Size.Empty;
    private Bitmap? _gpuExternalScratchBitmap;
    private Graphics? _gpuExternalScratchGraphics;
    private int _gpuOverlayTexture;
    private Size _gpuOverlayTextureSize = Size.Empty;
    private int _gpuSceneFramebuffer;
    private int _gpuSceneColorTexture;
    private int _gpuSceneDepthRenderbuffer;
    private Size _gpuSceneRenderTargetSize = Size.Empty;
    private bool _gpuFramebufferApiReady;
    private bool _gpuUseExtendedSceneTextureThisFrame;
    private int _gpuHeroLobSubviewTexture;
    private Size _gpuHeroLobSubviewTextureSize = Size.Empty;
    private bool _gpuHeroLobSubviewTextureUsesGrayscale;
    private long _lastGpuOverlayUploadTicks;
    private long _lastGpuOverlaySceneUploadTicks;
    private long _lastGpuOverlayUiUploadTicks;
    private long _lastGpuOverlayDrawCostTicks;
    private long _lastGpuOverlayUploadCostTicks;
    private long _lastGpuOverlayPresentCostTicks;
    private bool _lastGpuOverlayUploaded;
    private bool _lastGpuOverlayPausedState;
    private bool _gpuOverlaySceneDirty = true;
    private bool _gpuOverlayUiDirty = true;
    private bool _measureGpuOverlayPhases;
    private string _currentGpuOverlayPhaseSummary = string.Empty;
    private string _lastGpuOverlayPhaseSummary = "-";
    private int _gpuTerrainVertexBuffer;
    private int _gpuTerrainVertexCount;
    private int _gpuTerrainBufferVersion = -1;
    private int _gpuDynamicVertexBuffer;
    private int _gpuDynamicVertexCapacity;
    private int _gpuEnergyMechanismVertexBuffer;
    private int _gpuEnergyMechanismVertexCapacity;
    private int _gpuProjectileVertexBuffer;
    private int _gpuProjectileVertexCapacity;
    private int _gpuSharedVertexArray;
    private bool _gpuBufferApiReady;
    private bool _gpuBatchingDynamicGeometry;
    private bool _gpuEnergyMechanismBatchActive;
    private GpuDynamicBatchKind _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Entity;
    private long _lastGpuRenderPerfLogTicks;
    private int _gpuTerrainChunkColumns = 1;
    private int _gpuTerrainChunkRows = 1;
    private string? _terrainCacheGpuSourcePath;
    private string? _terrainCacheGpuLoadedSourcePath;
    private bool _terrainCacheGpuLoadedLightingEnabled;
    private string? _terrainCacheGpuAnnotationPath;
    private long _terrainCacheGpuAnnotationTicks;
    private bool _terrainCacheGpuBuildFailed;
    private int _terrainCacheGpuColumns;
    private int _terrainCacheGpuRows;
    private int _terrainCacheGpuTotalTriangles;
    private int _terrainCacheGpuResidentVertices;
    private int _terrainCacheGpuVisibleVertices;
    private int _terrainCacheGpuDrawCalls;
    private int _terrainCacheGpuPendingUploads;
    private long _terrainCacheGpuFrameIndex;
    private long _lastTerrainCacheGpuLogTicks;
    private Task<TerrainCacheGpuBuildResult>? _terrainCacheGpuBuildTask;
    private string? _terrainCacheGpuBuildingSourcePath;
    private readonly List<GpuVertex> _gpuTerrainVertexBuildBuffer = new(65536);
    private readonly List<GpuVertex> _gpuDynamicVertexBuildBuffer = new(16384);
    private readonly List<GpuVertex> _gpuEnergyMechanismVertexBuildBuffer = new(8192);
    private readonly List<GpuVertex> _gpuProjectileVertexBuildBuffer = new(8192);
    private readonly List<GpuTerrainChunk> _gpuTerrainChunks = new(512);
    private readonly List<TerrainCacheGpuChunk> _terrainCacheGpuChunks = new(128);
    private readonly List<TerrainCacheGpuChunk> _terrainCacheGpuVisibleChunkScratch = new(128);
    private TerrainCacheGpuChunkQuadTree? _terrainCacheGpuChunkTree;
    private readonly Dictionary<string, FineTerrainStaticMeshCache> _fineTerrainEnergyBodyMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FineTerrainStaticMeshCache> _fineTerrainEnergyStripMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FineTerrainStaticMeshCache> _fineTerrainOutpostBodyMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FineTerrainStaticMeshCache> _fineTerrainBaseBodyMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FineTerrainStaticMeshCache> _fineTerrainEnergyUnitMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FineTerrainStaticMeshCache> _fineTerrainOutpostUnitMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FineTerrainStaticMeshCache> _fineTerrainBaseUnitMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(FineTerrainColoredTriangle Triangle, float Progress)>> _fineTerrainEnergyStripTriangleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GpuEntityAppearanceMeshCache> _gpuEntityAppearanceMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly float[] _gpuKeyLightPosition = { -0.42f, 0.96f, -0.26f, 0.0f };
    private readonly float[] _gpuKeyLightAmbient = { 0.30f, 0.36f, 0.48f, 1.0f };
    private readonly float[] _gpuKeyLightDiffuse = { 0.86f, 1.02f, 1.20f, 1.0f };
    private readonly float[] _gpuKeyLightSpecular = { 0.24f, 0.34f, 0.52f, 1.0f };
    private readonly float[] _gpuFillLightPosition = { 0.62f, 0.34f, 0.64f, 0.0f };
    private readonly float[] _gpuFillLightAmbient = { 0.018f, 0.030f, 0.052f, 1.0f };
    private readonly float[] _gpuFillLightDiffuse = { 0.12f, 0.22f, 0.38f, 1.0f };
    private readonly float[] _gpuFillLightSpecular = { 0.05f, 0.09f, 0.18f, 1.0f };
    private readonly float[] _gpuRobotMaterialSpecular = { 0.18f, 0.26f, 0.42f, 1.0f };
    private static readonly object _gpuCylinderUnitCircleCacheLock = new();
    private static readonly Dictionary<int, Vector2[]> _gpuCylinderUnitCircleCache = new();
    private string? _fineTerrainEnergyBodyMeshSceneKey;
    private string? _fineTerrainEnergyStripMeshSceneKey;
    private string? _fineTerrainOutpostBodyMeshSceneKey;
    private string? _fineTerrainBaseBodyMeshSceneKey;
    private string? _fineTerrainEnergyUnitMeshSceneKey;
    private string? _fineTerrainOutpostUnitMeshSceneKey;
    private string? _fineTerrainBaseUnitMeshSceneKey;
    private Simulator3dLightingSettings _lightingSettings = Simulator3dLightingSettings.CreateDefault();
    private GlGenBuffersDelegate? _glGenBuffers;
    private GlBindBufferDelegate? _glBindBuffer;
    private GlBufferDataDelegate? _glBufferData;
    private GlBufferSubDataDelegate? _glBufferSubData;
    private GlDeleteBuffersDelegate? _glDeleteBuffers;
    private GlGenVertexArraysDelegate? _glGenVertexArrays;
    private GlBindVertexArrayDelegate? _glBindVertexArray;
    private GlDeleteVertexArraysDelegate? _glDeleteVertexArrays;
    private GlGenFramebuffersDelegate? _glGenFramebuffers;
    private GlBindFramebufferDelegate? _glBindFramebuffer;
    private GlFramebufferTexture2DDelegate? _glFramebufferTexture2D;
    private GlDeleteFramebuffersDelegate? _glDeleteFramebuffers;
    private GlCheckFramebufferStatusDelegate? _glCheckFramebufferStatus;
    private GlGenRenderbuffersDelegate? _glGenRenderbuffers;
    private GlBindRenderbufferDelegate? _glBindRenderbuffer;
    private GlRenderbufferStorageDelegate? _glRenderbufferStorage;
    private GlFramebufferRenderbufferDelegate? _glFramebufferRenderbuffer;
    private GlDeleteRenderbuffersDelegate? _glDeleteRenderbuffers;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct GpuVertex
    {
        public GpuVertex(Vector3 position, Color color)
            : this(position, color, Vector3.UnitY)
        {
        }

        public GpuVertex(Vector3 position, Color color, Vector3 normal)
        {
            X = position.X;
            Y = position.Y;
            Z = position.Z;
            R = color.R;
            G = color.G;
            B = color.B;
            A = color.A;
            Vector3 safeNormal = normal.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(normal);
            Nx = safeNormal.X;
            Ny = safeNormal.Y;
            Nz = safeNormal.Z;
        }

        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte A;
        public readonly float Nx;
        public readonly float Ny;
        public readonly float Nz;
    }

    private sealed class GpuTerrainChunk
    {
        public int Buffer;

        public int VertexCount;

        public int Version = -1;

        public readonly List<GpuVertex> BuildBuffer = new(768);
    }

    private sealed class FineTerrainStaticMeshCache
    {
        public int Buffer;

        public int VertexCount;

        public Vector3 PivotScene;
    }

    private sealed class GpuEntityAppearanceMeshCache
    {
        public int Buffer;

        public int VertexCount;

        public float HeightM;

        public long LastUsedFrame;
    }

    private sealed class TerrainCacheGpuChunk
    {
        public readonly List<GpuVertex> Vertices = new(4096);

        public readonly List<int> Indices = new(4096);

        public int Buffer;

        public int IndexBuffer;

        public int Version = -1;

        public int VertexCount;

        public int IndexCount;

        public Vector3 Center;

        public float RadiusM;

        public float HeightM;

        public long LastUsedFrame;

        public float MinX = float.PositiveInfinity;

        public float MinY = float.PositiveInfinity;

        public float MinZ = float.PositiveInfinity;

        public float MaxX = float.NegativeInfinity;

        public float MaxY = float.NegativeInfinity;

        public float MaxZ = float.NegativeInfinity;

        public void AppendTriangle(GpuVertex a, GpuVertex b, GpuVertex c)
        {
            int baseIndex = Vertices.Count;
            Vertices.Add(a);
            Vertices.Add(b);
            Vertices.Add(c);
            Indices.Add(baseIndex);
            Indices.Add(baseIndex + 1);
            Indices.Add(baseIndex + 2);
            Include(a);
            Include(b);
            Include(c);
        }

        public void FinalizeBounds()
        {
            VertexCount = Vertices.Count;
            IndexCount = Indices.Count;
            if (VertexCount <= 0)
            {
                Center = Vector3.Zero;
                RadiusM = 0f;
                HeightM = 0f;
                IndexCount = 0;
                return;
            }

            Center = new Vector3(
                (MinX + MaxX) * 0.5f,
                (MinY + MaxY) * 0.5f,
                (MinZ + MaxZ) * 0.5f);
            float dx = Math.Max(0f, MaxX - MinX);
            float dz = Math.Max(0f, MaxZ - MinZ);
            RadiusM = MathF.Sqrt(dx * dx + dz * dz) * 0.5f;
            HeightM = Math.Max(0.05f, MaxY - MinY);
        }

        private void Include(GpuVertex vertex)
        {
            MinX = Math.Min(MinX, vertex.X);
            MinY = Math.Min(MinY, vertex.Y);
            MinZ = Math.Min(MinZ, vertex.Z);
            MaxX = Math.Max(MaxX, vertex.X);
            MaxY = Math.Max(MaxY, vertex.Y);
            MaxZ = Math.Max(MaxZ, vertex.Z);
        }
    }

    private sealed class TerrainCacheGpuChunkQuadTree
    {
        private const int MaxDepth = 8;
        private const int MaxLeafChunks = 10;
        private readonly IReadOnlyList<TerrainCacheGpuChunk> _chunks;
        private readonly Node _root;

        private TerrainCacheGpuChunkQuadTree(IReadOnlyList<TerrainCacheGpuChunk> chunks, Node root)
        {
            _chunks = chunks;
            _root = root;
        }

        public static TerrainCacheGpuChunkQuadTree? Build(IReadOnlyList<TerrainCacheGpuChunk> chunks)
        {
            var indices = new List<int>(chunks.Count);
            for (int index = 0; index < chunks.Count; index++)
            {
                if (chunks[index].VertexCount > 0)
                {
                    indices.Add(index);
                }
            }

            return indices.Count == 0
                ? null
                : new TerrainCacheGpuChunkQuadTree(chunks, Node.Build(chunks, indices, 0));
        }

        public void QueryVisible(Simulator3dForm owner, List<TerrainCacheGpuChunk> target)
        {
            target.Clear();
            _root.Query(owner, _chunks, target);
        }

        private sealed class Node
        {
            private readonly int[]? _chunkIndices;
            private readonly Node[]? _children;
            private readonly float _minX;
            private readonly float _minY;
            private readonly float _minZ;
            private readonly float _maxX;
            private readonly float _maxY;
            private readonly float _maxZ;

            private Node(
                float minX,
                float minY,
                float minZ,
                float maxX,
                float maxY,
                float maxZ,
                int[]? chunkIndices,
                Node[]? children)
            {
                _minX = minX;
                _minY = minY;
                _minZ = minZ;
                _maxX = maxX;
                _maxY = maxY;
                _maxZ = maxZ;
                _chunkIndices = chunkIndices;
                _children = children;
            }

            public static Node Build(IReadOnlyList<TerrainCacheGpuChunk> chunks, List<int> indices, int depth)
            {
                ResolveBounds(chunks, indices, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
                if (indices.Count <= MaxLeafChunks || depth >= MaxDepth)
                {
                    return new Node(minX, minY, minZ, maxX, maxY, maxZ, indices.ToArray(), null);
                }

                float midX = (minX + maxX) * 0.5f;
                float midZ = (minZ + maxZ) * 0.5f;
                var buckets = new List<int>[] { new(), new(), new(), new() };
                foreach (int index in indices)
                {
                    TerrainCacheGpuChunk chunk = chunks[index];
                    int bucket = (chunk.Center.X >= midX ? 1 : 0) | (chunk.Center.Z >= midZ ? 2 : 0);
                    buckets[bucket].Add(index);
                }

                if (buckets.Any(bucket => bucket.Count == indices.Count))
                {
                    return new Node(minX, minY, minZ, maxX, maxY, maxZ, indices.ToArray(), null);
                }

                var children = new List<Node>(4);
                foreach (List<int> bucket in buckets)
                {
                    if (bucket.Count > 0)
                    {
                        children.Add(Build(chunks, bucket, depth + 1));
                    }
                }

                return new Node(minX, minY, minZ, maxX, maxY, maxZ, null, children.ToArray());
            }

            public void Query(Simulator3dForm owner, IReadOnlyList<TerrainCacheGpuChunk> chunks, List<TerrainCacheGpuChunk> target)
            {
                if (!owner.IsTerrainCacheGpuBoundsVisible(_minX, _minY, _minZ, _maxX, _maxY, _maxZ))
                {
                    return;
                }

                if (_children is not null)
                {
                    foreach (Node child in _children)
                    {
                        child.Query(owner, chunks, target);
                    }

                    return;
                }

                if (_chunkIndices is null)
                {
                    return;
                }

                foreach (int index in _chunkIndices)
                {
                    TerrainCacheGpuChunk chunk = chunks[index];
                    if (owner.IsTerrainCacheGpuChunkVisible(chunk))
                    {
                        target.Add(chunk);
                    }
                }
            }

            private static void ResolveBounds(
                IReadOnlyList<TerrainCacheGpuChunk> chunks,
                IReadOnlyList<int> indices,
                out float minX,
                out float minY,
                out float minZ,
                out float maxX,
                out float maxY,
                out float maxZ)
            {
                minX = float.PositiveInfinity;
                minY = float.PositiveInfinity;
                minZ = float.PositiveInfinity;
                maxX = float.NegativeInfinity;
                maxY = float.NegativeInfinity;
                maxZ = float.NegativeInfinity;
                foreach (int index in indices)
                {
                    TerrainCacheGpuChunk chunk = chunks[index];
                    minX = Math.Min(minX, chunk.MinX);
                    minY = Math.Min(minY, chunk.MinY);
                    minZ = Math.Min(minZ, chunk.MinZ);
                    maxX = Math.Max(maxX, chunk.MaxX);
                    maxY = Math.Max(maxY, chunk.MaxY);
                    maxZ = Math.Max(maxZ, chunk.MaxZ);
                }
            }
        }
    }

    private readonly record struct TerrainCacheGpuBuildParameters(
        float MapWidthWorld,
        float MapHeightWorld,
        float FieldLengthM,
        float FieldWidthM,
        float SceneScale,
        string? AnnotationPath,
        IReadOnlySet<int> ExcludedComponentIds,
        bool UseBakedLighting);

    private sealed record TerrainCacheGpuBuildResult(
        string SourcePath,
        bool UseBakedLighting,
        List<TerrainCacheGpuChunk> Chunks,
        int Columns,
        int Rows,
        int EmittedTriangles,
        int TotalTriangles,
        int VertexCount,
        int UsedChunks);

    private readonly record struct GpuTerrainChunkWindow(int CenterColumn, int CenterRow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int ChoosePixelFormat(IntPtr hdc, ref PixelFormatDescriptor ppfd);

    [DllImport("gdi32.dll")]
    private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PixelFormatDescriptor ppfd);

    [DllImport("gdi32.dll")]
    private static extern bool SwapBuffers(IntPtr hdc);

    [DllImport("opengl32.dll")]
    private static extern IntPtr wglCreateContext(IntPtr hdc);

    [DllImport("opengl32.dll")]
    private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);

    [DllImport("opengl32.dll")]
    private static extern bool wglDeleteContext(IntPtr hglrc);

    [DllImport("opengl32.dll")]
    private static extern IntPtr wglGetProcAddress(string name);

    [DllImport("opengl32.dll")]
    private static extern void glViewport(int x, int y, int width, int height);

    [DllImport("opengl32.dll")]
    private static extern void glScissor(int x, int y, int width, int height);

    [DllImport("opengl32.dll")]
    private static extern void glClearColor(float red, float green, float blue, float alpha);

    [DllImport("opengl32.dll")]
    private static extern void glClear(int mask);

    [DllImport("opengl32.dll")]
    private static extern void glEnable(int cap);

    [DllImport("opengl32.dll")]
    private static extern void glDisable(int cap);

    [DllImport("opengl32.dll")]
    private static extern void glBlendFunc(int sfactor, int dfactor);

    [DllImport("opengl32.dll")]
    private static extern void glMatrixMode(int mode);

    [DllImport("opengl32.dll")]
    private static extern void glLoadMatrixf(float[] matrix);

    [DllImport("opengl32.dll")]
    private static extern void glBegin(int mode);

    [DllImport("opengl32.dll")]
    private static extern void glEnd();

    [DllImport("opengl32.dll")]
    private static extern void glColor4ub(byte red, byte green, byte blue, byte alpha);

    [DllImport("opengl32.dll")]
    private static extern void glVertex3f(float x, float y, float z);

    [DllImport("opengl32.dll")]
    private static extern void glLineWidth(float width);

    [DllImport("opengl32.dll")]
    private static extern void glTexCoord2f(float s, float t);

    [DllImport("opengl32.dll")]
    private static extern void glGenTextures(int n, out int textures);

    [DllImport("opengl32.dll")]
    private static extern void glBindTexture(int target, int texture);

    [DllImport("opengl32.dll")]
    private static extern void glTexParameteri(int target, int pname, int param);

    [DllImport("opengl32.dll")]
    private static extern void glTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr pixels);

    [DllImport("opengl32.dll")]
    private static extern void glTexSubImage2D(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, IntPtr pixels);

    [DllImport("opengl32.dll")]
    private static extern void glGenerateMipmap(int target);

    [DllImport("opengl32.dll")]
    private static extern void glCopyTexImage2D(int target, int level, int internalformat, int x, int y, int width, int height, int border);

    [DllImport("opengl32.dll")]
    private static extern void glCopyTexSubImage2D(int target, int level, int xoffset, int yoffset, int x, int y, int width, int height);

    [DllImport("opengl32.dll")]
    private static extern void glDeleteTextures(int n, ref int textures);

    [DllImport("opengl32.dll")]
    private static extern void glEnableClientState(int array);

    [DllImport("opengl32.dll")]
    private static extern void glDisableClientState(int array);

    [DllImport("opengl32.dll")]
    private static extern void glVertexPointer(int size, int type, int stride, IntPtr pointer);

    [DllImport("opengl32.dll")]
    private static extern void glColorPointer(int size, int type, int stride, IntPtr pointer);

    [DllImport("opengl32.dll")]
    private static extern void glNormalPointer(int type, int stride, IntPtr pointer);

    [DllImport("opengl32.dll")]
    private static extern void glNormal3f(float nx, float ny, float nz);

    [DllImport("opengl32.dll")]
    private static extern void glLightfv(int light, int pname, float[] parameters);

    [DllImport("opengl32.dll")]
    private static extern void glMaterialfv(int face, int pname, float[] parameters);

    [DllImport("opengl32.dll")]
    private static extern void glMaterialf(int face, int pname, float parameter);

    [DllImport("opengl32.dll")]
    private static extern void glColorMaterial(int face, int mode);

    [DllImport("opengl32.dll")]
    private static extern void glShadeModel(int mode);

    [DllImport("opengl32.dll")]
    private static extern void glDrawArrays(int mode, int first, int count);

    [DllImport("opengl32.dll")]
    private static extern void glDrawElements(int mode, int count, int type, IntPtr indices);

    [DllImport("opengl32.dll")]
    private static extern void glReadPixels(int x, int y, int width, int height, int format, int type, IntPtr pixels);

    [DllImport("opengl32.dll")]
    private static extern void glPixelStorei(int pname, int param);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WglSwapIntervalExt(int interval);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGenBuffersDelegate(int n, out int buffers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlBindBufferDelegate(int target, int buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlBufferDataDelegate(int target, IntPtr size, IntPtr data, int usage);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlBufferSubDataDelegate(int target, IntPtr offset, IntPtr size, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlDeleteBuffersDelegate(int n, ref int buffers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGenVertexArraysDelegate(int n, out int arrays);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlBindVertexArrayDelegate(int array);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlDeleteVertexArraysDelegate(int n, ref int arrays);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGenFramebuffersDelegate(int n, out int framebuffers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlBindFramebufferDelegate(int target, int framebuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlFramebufferTexture2DDelegate(int target, int attachment, int textarget, int texture, int level);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlDeleteFramebuffersDelegate(int n, ref int framebuffers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GlCheckFramebufferStatusDelegate(int target);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGenRenderbuffersDelegate(int n, out int renderbuffers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlBindRenderbufferDelegate(int target, int renderbuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlRenderbufferStorageDelegate(int target, int internalformat, int width, int height);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlFramebufferRenderbufferDelegate(int target, int attachment, int renderbuffertarget, int renderbuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlDeleteRenderbuffersDelegate(int n, ref int renderbuffers);

    private void DrawGpuMatch(Graphics graphics)
    {
        SyncGpuLightingSettings();
        long frameStartTicks = Stopwatch.GetTimestamp();
        long terrainTicks = 0;
        long facilityTicks = 0;
        long entityTicks = 0;
        long projectileTicks = 0;
        long flushTicks = 0;
        long overlayTicks = 0;
        long swapTicks = 0;
        if (!EnsureGpuContext())
        {
            DrawInMatchWorld(graphics);
            DrawInMatchOverlay(graphics);
            return;
        }

        if (!ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid))
        {
            RebuildTerrainTileCache();
        }

        if (!MakeGpuContextCurrent())
        {
            DrawInMatchWorld(graphics);
            DrawInMatchOverlay(graphics);
            return;
        }

        int clientWidth = Math.Max(1, ClientSize.Width);
        int clientHeight = Math.Max(1, ClientSize.Height);
        Rectangle mainViewport = _projectionViewportRect ?? new Rectangle(0, 0, clientWidth, clientHeight);
        bool deploymentSubviewOnly = IsHeroDeploymentSubviewOnlyMode();
        Rectangle heroSubviewViewport = Rectangle.Empty;
        Rectangle heroSubviewSourceRect = Rectangle.Empty;
        Size sceneRenderSize = mainViewport.Size;
        Matrix4x4 sceneProjectionMatrix = _projectionMatrix;
        bool useSceneTexture = TryPrepareHeroLobExtendedScene(
            mainViewport,
            out heroSubviewViewport,
            out heroSubviewSourceRect,
            out sceneRenderSize,
            out sceneProjectionMatrix);
        if (useSceneTexture)
        {
            TryInitializeGpuFramebufferApi();
            useSceneTexture = EnsureGpuSceneRenderTarget(sceneRenderSize);
        }

        _gpuUseExtendedSceneTextureThisFrame = useSceneTexture;
        if (useSceneTexture && _glBindFramebuffer is not null)
        {
            _glBindFramebuffer(GlFramebuffer, _gpuSceneFramebuffer);
            glViewport(0, 0, sceneRenderSize.Width, sceneRenderSize.Height);
        }
        else
        {
            _gpuUseExtendedSceneTextureThisFrame = false;
            glViewport(0, 0, clientWidth, clientHeight);
        }

        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousProjection = _projectionMatrix;
        try
        {
            _projectionViewportRect = new Rectangle(0, 0, useSceneTexture ? sceneRenderSize.Width : clientWidth, useSceneTexture ? sceneRenderSize.Height : clientHeight);
            _projectionMatrix = useSceneTexture ? sceneProjectionMatrix : previousProjection;
            glClearColor(0.030f, 0.037f, 0.050f, 1f);
            glClear(GlColorBufferBit | GlDepthBufferBit);
            glEnable(GlDepthTest);
            glEnable(GlBlend);
            glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);

            glMatrixMode(GlProjection);
            glLoadMatrixf(ToOpenGlMatrix(_projectionMatrix));
            glMatrixMode(GlModelView);
            glLoadMatrixf(ToOpenGlMatrix(_viewMatrix));

            RenderGpuWorldScene(
                graphics,
                out terrainTicks,
                out facilityTicks,
                out entityTicks,
                out projectileTicks,
                out flushTicks,
                out int facilityVertices,
                out int entityVertices,
                out int projectileVertices);

            if (useSceneTexture && _glBindFramebuffer is not null)
            {
                bool copiedDeploymentSubview = false;
                if (deploymentSubviewOnly && !heroSubviewViewport.IsEmpty && !heroSubviewSourceRect.IsEmpty)
                {
                    copiedDeploymentSubview = CopyGpuHeroLobSubviewTexture(heroSubviewSourceRect, sceneRenderSize, grayscale: true);
                }

                _glBindFramebuffer(GlFramebuffer, 0);
                glViewport(0, 0, clientWidth, clientHeight);

                if (deploymentSubviewOnly)
                {
                    glClearColor(0f, 0f, 0f, 1f);
                    glClear(GlColorBufferBit | GlDepthBufferBit);
                    if (copiedDeploymentSubview)
                    {
                        PresentGpuHeroLobSubviewCached(heroSubviewViewport);
                    }
                }
                else
                {
                    PresentGpuTextureRegion(
                        _gpuSceneColorTexture,
                        sceneRenderSize,
                        new Rectangle(0, 0, clientWidth, clientHeight),
                        new Rectangle(0, 0, clientWidth, clientHeight));
                    if (!heroSubviewViewport.IsEmpty && !heroSubviewSourceRect.IsEmpty)
                    {
                        PresentGpuTextureRegion(
                            _gpuSceneColorTexture,
                            sceneRenderSize,
                            heroSubviewSourceRect,
                            heroSubviewViewport,
                            useNearestFilter: true);
                    }
                }
            }
            else
            {
                DrawGpuHeroLobSecondaryViewport(graphics);
            }

            _hasPresentedGpuFrame = true;
            long stepStartTicks = Stopwatch.GetTimestamp();
            _projectionViewportRect = previousViewport;
            _projectionMatrix = previousProjection;
            DrawGpuOverlayLayer();
            overlayTicks = Stopwatch.GetTimestamp() - stepStartTicks;
            if (!_gpuContextBorrowedExternally)
            {
                stepStartTicks = Stopwatch.GetTimestamp();
                SwapBuffers(_gpuDeviceContext);
                swapTicks = Stopwatch.GetTimestamp() - stepStartTicks;
            }

            LogGpuRenderPerfIfDue(
                frameStartTicks,
                terrainTicks,
                facilityTicks,
                entityTicks,
                projectileTicks,
                flushTicks,
                overlayTicks,
                swapTicks,
                facilityVertices,
                entityVertices,
                projectileVertices);
        }
        finally
        {
            if (useSceneTexture && _glBindFramebuffer is not null)
            {
                _glBindFramebuffer(GlFramebuffer, 0);
            }

            _projectionViewportRect = previousViewport;
            _projectionMatrix = previousProjection;
        }
    }

    private void RenderGpuWorldScene(
        Graphics graphics,
        out long terrainTicks,
        out long facilityTicks,
        out long entityTicks,
        out long projectileTicks,
        out long flushTicks,
        out int facilityVertices,
        out int entityVertices,
        out int projectileVertices)
    {
        terrainTicks = 0;
        facilityTicks = 0;
        entityTicks = 0;
        projectileTicks = 0;
        flushTicks = 0;
        facilityVertices = 0;
        entityVertices = 0;
        projectileVertices = 0;
        bool deploymentSubviewOnly = IsHeroDeploymentSubviewOnlyMode();
        _gpuDynamicVertexBuildBuffer.Clear();
        _gpuEnergyMechanismVertexBuildBuffer.Clear();
        _gpuProjectileVertexBuildBuffer.Clear();
        long stepStartTicks = Stopwatch.GetTimestamp();
        DrawGpuTerrainBase();
        DrawGpuTerrainGeometry();
        terrainTicks = Stopwatch.GetTimestamp() - stepStartTicks;
        _gpuBatchingDynamicGeometry = true;
        _gpuGeometryPass = true;
        GpuDynamicBatchKind previousBatch = _gpuCurrentDynamicBatch;
        try
        {
            stepStartTicks = Stopwatch.GetTimestamp();
            _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Facility;
            DrawGpuFacilities();
            DrawGpuTeamTopNeonLights();
            if (!deploymentSubviewOnly && _host.SelectedEntity is SimulationEntity debugEntity)
            {
                DrawGpuTerrainCollisionDebugGeometry(debugEntity);
            }

            bool previousSuppressLabels = _suppressEntityLabels;
            _suppressEntityLabels = true;
            try
            {
                DrawStaticStructureBodies(graphics);
                facilityTicks = Stopwatch.GetTimestamp() - stepStartTicks;

                stepStartTicks = Stopwatch.GetTimestamp();
                _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Entity;
                DrawEntityGeometry(graphics);
                entityTicks = Stopwatch.GetTimestamp() - stepStartTicks;

                if (!_previewOnly)
                {
                    stepStartTicks = Stopwatch.GetTimestamp();
                    _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Projectile;
                    DrawGpuProjectiles();
                    projectileTicks = Stopwatch.GetTimestamp() - stepStartTicks;
                }
            }
            finally
            {
                _suppressEntityLabels = previousSuppressLabels;
            }
        }
        finally
        {
            _gpuCurrentDynamicBatch = previousBatch;
            _gpuGeometryPass = false;
            _gpuBatchingDynamicGeometry = false;
        }

        facilityVertices = _gpuEnergyMechanismVertexBuildBuffer.Count;
        entityVertices = _gpuDynamicVertexBuildBuffer.Count;
        projectileVertices = _gpuProjectileVertexBuildBuffer.Count;
        stepStartTicks = Stopwatch.GetTimestamp();
        FlushGpuEnergyMechanismVertices();
        FlushGpuDynamicVertices();
        FlushGpuProjectileVertices();
        flushTicks = Stopwatch.GetTimestamp() - stepStartTicks;
        if (!deploymentSubviewOnly)
        {
            DrawGpuEntityHealthBars();
        }

        if (!_previewOnly && !deploymentSubviewOnly)
        {
            DrawGpuProjectileTrailLines();
            DrawGpuDebugReference();
        }
    }

    private void DrawGpuOverlayLayer()
    {
        EnsureGpuOverlayMatchSurfaces();
        if (_gpuOverlaySceneGraphics is null || _gpuOverlayUiGraphics is null)
        {
            return;
        }

        long nowTicks = _frameClock.ElapsedTicks;
        SimulatorRenderPassPlan passPlan = ResolveRenderPassPlan();
        double sceneUploadIntervalSec = passPlan.SceneOverlayUploadIntervalSec;
        double uiUploadIntervalSec = passPlan.UiOverlayUploadIntervalSec;
        bool mustUploadScene = _gpuOverlaySceneTexture == 0
            || _gpuOverlaySceneBitmap is null
            || _gpuOverlaySceneTextureSize != _gpuOverlaySceneBitmap.Size
            || _lastGpuOverlayPausedState != _paused
            || _lastGpuOverlaySceneUploadTicks <= 0
            || _gpuOverlaySceneDirty
            || (nowTicks - _lastGpuOverlaySceneUploadTicks) / (double)Stopwatch.Frequency >= sceneUploadIntervalSec;
        bool mustUploadUi = _gpuOverlayUiTexture == 0
            || _gpuOverlayUiBitmap is null
            || _gpuOverlayUiTextureSize != _gpuOverlayUiBitmap.Size
            || _lastGpuOverlayPausedState != _paused
            || _lastGpuOverlayUiUploadTicks <= 0
            || _gpuOverlayUiDirty
            || (nowTicks - _lastGpuOverlayUiUploadTicks) / (double)Stopwatch.Frequency >= uiUploadIntervalSec;
        if (mustUploadScene
            && mustUploadUi
            && _gpuOverlaySceneTexture != 0
            && _gpuOverlayUiTexture != 0
            && !_gpuOverlaySceneDirty
            && !_gpuOverlayUiDirty)
        {
            bool sceneOlder = _lastGpuOverlaySceneUploadTicks <= _lastGpuOverlayUiUploadTicks;
            mustUploadScene = sceneOlder;
            mustUploadUi = !sceneOlder;
        }

        if (mustUploadScene || mustUploadUi)
        {
            long drawStartTicks = Stopwatch.GetTimestamp();
            _measureGpuOverlayPhases = true;
            _currentGpuOverlayPhaseSummary = string.Empty;
            try
            {
                if (mustUploadScene)
                {
                    _gpuOverlaySceneGraphics.Clear(Color.Transparent);
                    _gpuOverlaySceneGraphics.ResetTransform();
                    _gpuOverlaySceneGraphics.ScaleTransform(_gpuOverlaySurfaceScale, _gpuOverlaySurfaceScale);
                    ConfigureGpuOverlayGraphics(_gpuOverlaySceneGraphics);
                    DrawInMatchOverlaySceneLayer(_gpuOverlaySceneGraphics);
                    _gpuOverlaySceneGraphics.ResetTransform();
                }

                if (mustUploadUi)
                {
                    _gpuOverlayUiGraphics.Clear(Color.Transparent);
                    _gpuOverlayUiGraphics.ResetTransform();
                    _gpuOverlayUiGraphics.ScaleTransform(_gpuOverlaySurfaceScale, _gpuOverlaySurfaceScale);
                    ConfigureGpuOverlayGraphics(_gpuOverlayUiGraphics);
                    DrawInMatchOverlayUiLayer(_gpuOverlayUiGraphics);
                    _gpuOverlayUiGraphics.ResetTransform();
                }
            }
            finally
            {
                _measureGpuOverlayPhases = false;
                _lastGpuOverlayPhaseSummary = string.IsNullOrWhiteSpace(_currentGpuOverlayPhaseSummary)
                    ? "-"
                    : _currentGpuOverlayPhaseSummary.TrimEnd(';');
                _currentGpuOverlayPhaseSummary = string.Empty;
            }

            _lastGpuOverlayDrawCostTicks = Stopwatch.GetTimestamp() - drawStartTicks;

            long uploadStartTicks = Stopwatch.GetTimestamp();
            if (mustUploadScene)
            {
                UploadGpuOverlayBitmap(_gpuOverlaySceneBitmap, ref _gpuOverlaySceneTexture, ref _gpuOverlaySceneTextureSize);
                _lastGpuOverlaySceneUploadTicks = nowTicks;
                _gpuOverlaySceneDirty = false;
            }

            if (mustUploadUi)
            {
                UploadGpuOverlayBitmap(_gpuOverlayUiBitmap, ref _gpuOverlayUiTexture, ref _gpuOverlayUiTextureSize);
                _lastGpuOverlayUiUploadTicks = nowTicks;
                _gpuOverlayUiDirty = false;
            }

            _lastGpuOverlayUploadCostTicks = Stopwatch.GetTimestamp() - uploadStartTicks;
            _lastGpuOverlayUploadTicks = nowTicks;
            _lastGpuOverlayPausedState = _paused;
            _lastGpuOverlayUploaded = true;
        }
        else
        {
            _lastGpuOverlayDrawCostTicks = 0;
            _lastGpuOverlayUploadCostTicks = 0;
            _lastGpuOverlayUploaded = false;
        }

        long presentStartTicks = Stopwatch.GetTimestamp();
        PresentGpuOverlayTexture(_gpuOverlaySceneTexture);
        if (passPlan.RenderGpuHudPrimitives)
        {
            DrawGpuHudPrimitives();
        }
        PresentGpuOverlayTexture(_gpuOverlayUiTexture);
        _lastGpuOverlayPresentCostTicks = Stopwatch.GetTimestamp() - presentStartTicks;
    }

    private void DrawGpuHudPrimitives()
    {
        if (_previewOnly)
        {
            return;
        }

        DrawGpuOpenGkDynamicHudPrimitives();
        if (!IsFirstPersonHudVisible())
        {
            return;
        }

        DrawGpuCrosshairPrimitive();
        if (_customHudVisible)
        {
            DrawGpuCrosshairStatusProgressRingsPrimitive();
        }
        DrawGpuHeroDeploymentChargeRingPrimitive();
        DrawGpuCrosshairStatusProgressPrimitive();
        DrawGpuAutoAimGuidancePrimitive();
    }

    private void DrawGpuOpenGkDynamicHudPrimitives()
    {
        if (!UseOpenGkMatchHud()
            || _appState != SimulatorAppState.InMatch
            || !ShouldDrawOpenGkDynamicHudShapesOnGpu()
            || _host.IsDuelMode
            || _host.IsUnitTestMode
            || ClientSize.Width <= 0
            || ClientSize.Height <= 0)
        {
            return;
        }

        PrepareGpuScreenPrimitivePass();
        ResolveOpenGkUcHudLayoutV2(out Rectangle red, out _, out Rectangle blue);
        DrawGpuOpenGkTeamHudDynamicPrimitives(red, "red", mirrored: false);
        DrawGpuOpenGkTeamHudDynamicPrimitives(blue, "blue", mirrored: true);
        glEnable(GlDepthTest);
        glLineWidth(1f);
    }

    private void DrawGpuOpenGkTeamHudDynamicPrimitives(Rectangle rect, string teamKey, bool mirrored)
    {
        Color teamColor = ResolveTeamColor(teamKey);
        SimulationEntity? baseEntity = FindEntityById($"{teamKey}_base");
        SimulationEntity? outpostEntity = FindEntityById($"{teamKey}_outpost");
        int outpostWidth = Math.Clamp(rect.Width / 7, 72, 100);
        Rectangle outpostBar = mirrored
            ? new Rectangle(rect.Right - outpostWidth - 10, rect.Y + 25, outpostWidth, 26)
            : new Rectangle(rect.X + 10, rect.Y + 25, outpostWidth, 26);
        Rectangle baseBanner = mirrored
            ? new Rectangle(rect.X + 10, rect.Y + 30, rect.Width - outpostBar.Width - 28, 14)
            : new Rectangle(outpostBar.Right + 8, rect.Y + 30, rect.Width - outpostBar.Width - 28, 14);
        Rectangle baseBar = new(baseBanner.X + 34, baseBanner.Y + 4, Math.Max(42, baseBanner.Width - 40), 8);
        Rectangle outpostGauge = new(outpostBar.X + 7, outpostBar.Bottom - 9, outpostBar.Width - 14, 5);

        DrawGpuOpenGkStructureBar(baseBar, ResolveHealthRatio(baseEntity), teamColor, fillFromRight: mirrored, pathMirrored: !mirrored);
        DrawGpuOpenGkStructureBar(outpostGauge, ResolveHealthRatio(outpostEntity), teamColor, fillFromRight: mirrored, pathMirrored: !mirrored);

        IReadOnlyList<OpenGkHudUnitSlot> slots = BuildOpenGkHudUnitSlotsV2(teamKey);
        Rectangle[] cards = BuildOpenGkUcUnitCardRectsV2(rect, slots.Count, mirrored);
        for (int i = 0; i < slots.Count && i < cards.Length; i++)
        {
            OpenGkHudUnitSlot slot = slots[ResolveOpenGkUcSlotIndex(i, slots.Count, mirrored)];
            if (slot.Entity is null)
            {
                continue;
            }

            Rectangle card = cards[i];
            int infoStripHeight = 22;
            Rectangle infoStrip = new(card.X + 4, card.Bottom - infoStripHeight - 3, card.Width - 8, infoStripHeight);
            Rectangle hpRect = new(infoStrip.X + 3, infoStrip.Y + 3, Math.Max(10, infoStrip.Width - 6), 6);
            DrawGpuOpenGkUnitBar(hpRect, ResolveHealthRatio(slot.Entity), teamColor, fillFromRight: mirrored, pathMirrored: !mirrored);
            DrawGpuOpenGkAmmoGlyphs(new Rectangle(infoStrip.X + 3, infoStrip.Y + 10, Math.Max(1, infoStrip.Width - 6), 12), slot.Entity);
        }
    }

    private void DrawGpuOpenGkStructureBar(Rectangle rect, float ratio, Color color, bool fillFromRight, bool pathMirrored)
    {
        DrawGpuScreenParallelogramFill(rect, ratio, Color.FromArgb(238, color), fillFromRight, pathMirrored, Math.Min(8f, Math.Max(4f, rect.Height)));
        int glowWidth = Math.Max(0, (int)Math.Round(rect.Width * Math.Clamp(ratio, 0f, 1f)) - 4);
        if (glowWidth > 1)
        {
            Rectangle glowRect = fillFromRight
                ? new Rectangle(rect.Right - glowWidth, rect.Y, glowWidth, Math.Max(2, rect.Height / 3))
                : new Rectangle(rect.X, rect.Y, glowWidth, Math.Max(2, rect.Height / 3));
            DrawGpuScreenParallelogramFill(glowRect, 1f, Color.FromArgb(72, 255, 255, 255), fillFromRight, pathMirrored, Math.Min(8f, Math.Max(4f, rect.Height)));
        }
    }

    private void DrawGpuOpenGkUnitBar(Rectangle rect, float ratio, Color color, bool fillFromRight, bool pathMirrored)
    {
        DrawGpuScreenParallelogramFill(rect, ratio, Color.FromArgb(236, color), fillFromRight, pathMirrored, Math.Min(8f, rect.Width * 0.18f));
    }

    private void DrawGpuOpenGkAmmoGlyphs(Rectangle rect, SimulationEntity entity)
    {
        if (!entity.IsAlive
            || string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool largeAmmo = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        int iconWidth = largeAmmo ? 11 : 24;
        int iconX = rect.X + Math.Max(0, (rect.Width - iconWidth - 22) / 2);
        Rectangle icon = new(iconX, rect.Y + Math.Max(0, (rect.Height - 8) / 2), iconWidth, 8);
        Color fill = Color.FromArgb(235, 248, 250, 252);
        if (largeAmmo)
        {
            DrawGpuScreenCircle(new Rectangle(icon.X + 1, icon.Y, 8, 8), fill, segments: 14);
            DrawGpuScreenCircle(new Rectangle(icon.X + 3, icon.Y + 2, 1, 1), Color.FromArgb(82, 120, 132, 146), segments: 6);
            DrawGpuScreenCircle(new Rectangle(icon.X + 6, icon.Y + 2, 1, 1), Color.FromArgb(82, 120, 132, 146), segments: 6);
            DrawGpuScreenCircle(new Rectangle(icon.X + 5, icon.Y + 5, 1, 1), Color.FromArgb(82, 120, 132, 146), segments: 6);
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            int x = icon.X + i * 8;
            Span<PointF> bullet =
            [
                new PointF(x, icon.Y + 2),
                new PointF(x + 5, icon.Y + 2),
                new PointF(x + 7, icon.Y + 4),
                new PointF(x + 5, icon.Y + 6),
                new PointF(x, icon.Y + 6),
            ];
            DrawGpuScreenPolygon(bullet, fill);
        }
    }

    private void PrepareGpuScreenPrimitivePass()
    {
        glDisable(GlLighting);
        glDisable(GlTexture2D);
        glEnable(GlBlend);
        glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);
        glDisable(GlDepthTest);
        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
    }

    private void DrawGpuScreenParallelogramFill(Rectangle rect, float ratio, Color color, bool fillFromRight, bool pathMirrored, float skew)
    {
        float clamped = Math.Clamp(ratio, 0f, 1f);
        if (rect.Width <= 0 || rect.Height <= 0 || clamped <= 0.001f)
        {
            return;
        }

        int fillWidth = Math.Max(1, (int)Math.Round(rect.Width * clamped));
        Rectangle fillRect = fillFromRight
            ? new Rectangle(rect.Right - fillWidth, rect.Y, fillWidth, rect.Height)
            : new Rectangle(rect.X, rect.Y, fillWidth, rect.Height);
        float safeSkew = Math.Min(Math.Max(0f, skew), fillRect.Width * 0.45f);
        Span<PointF> points = stackalloc PointF[4];
        if (pathMirrored)
        {
            points[0] = new PointF(fillRect.Left, fillRect.Top);
            points[1] = new PointF(fillRect.Right - safeSkew, fillRect.Top);
            points[2] = new PointF(fillRect.Right, fillRect.Bottom);
            points[3] = new PointF(fillRect.Left + safeSkew, fillRect.Bottom);
        }
        else
        {
            points[0] = new PointF(fillRect.Left + safeSkew, fillRect.Top);
            points[1] = new PointF(fillRect.Right, fillRect.Top);
            points[2] = new PointF(fillRect.Right - safeSkew, fillRect.Bottom);
            points[3] = new PointF(fillRect.Left, fillRect.Bottom);
        }

        DrawGpuScreenPolygon(points, color);
    }

    private void DrawGpuScreenPolygon(ReadOnlySpan<PointF> points, Color color)
    {
        if (points.Length < 3)
        {
            return;
        }

        SetGpuColor(color);
        glBegin(GlTriangles);
        for (int i = 1; i < points.Length - 1; i++)
        {
            EmitGpuScreenVertex(points[0]);
            EmitGpuScreenVertex(points[i]);
            EmitGpuScreenVertex(points[i + 1]);
        }

        glEnd();
    }

    private void DrawGpuScreenCircle(Rectangle rect, Color color, int segments)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int count = Math.Clamp(segments, 6, 32);
        float cx = rect.X + rect.Width * 0.5f;
        float cy = rect.Y + rect.Height * 0.5f;
        float rx = rect.Width * 0.5f;
        float ry = rect.Height * 0.5f;
        SetGpuColor(color);
        glBegin(GlTriangles);
        for (int i = 0; i < count; i++)
        {
            float a0 = MathF.Tau * i / count;
            float a1 = MathF.Tau * (i + 1) / count;
            EmitGpuScreenVertex(new PointF(cx, cy));
            EmitGpuScreenVertex(new PointF(cx + MathF.Cos(a0) * rx, cy + MathF.Sin(a0) * ry));
            EmitGpuScreenVertex(new PointF(cx + MathF.Cos(a1) * rx, cy + MathF.Sin(a1) * ry));
        }

        glEnd();
    }

    private void EmitGpuScreenVertex(PointF point)
        => glVertex3f(ScreenXToNdc(point.X), ScreenYToNdc(point.Y), 0f);

    private void DrawGpuCrosshairPrimitive()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        glDisable(GlLighting);
        glDisable(GlTexture2D);
        glEnable(GlBlend);
        glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);
        glDisable(GlDepthTest);
        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));

        float centerX = ScreenXToNdc(ClientSize.Width * 0.5f);
        float centerY = ScreenYToNdc(ClientSize.Height * 0.5f);
        float gapX = 4f / Math.Max(1f, ClientSize.Width) * 2f;
        float armX = 12f / Math.Max(1f, ClientSize.Width) * 2f;
        float gapY = 4f / Math.Max(1f, ClientSize.Height) * 2f;
        float armY = 12f / Math.Max(1f, ClientSize.Height) * 2f;
        float dotX = 1.5f / Math.Max(1f, ClientSize.Width) * 2f;
        float dotY = 1.5f / Math.Max(1f, ClientSize.Height) * 2f;

        glLineWidth(3f);
        glColor4ub(0, 0, 0, 145);
        glBegin(GlLines);
        glVertex3f(centerX - armX, centerY, 0f);
        glVertex3f(centerX - gapX, centerY, 0f);
        glVertex3f(centerX + gapX, centerY, 0f);
        glVertex3f(centerX + armX, centerY, 0f);
        glVertex3f(centerX, centerY + armY, 0f);
        glVertex3f(centerX, centerY + gapY, 0f);
        glVertex3f(centerX, centerY - gapY, 0f);
        glVertex3f(centerX, centerY - armY, 0f);
        glEnd();

        glLineWidth(1.5f);
        glColor4ub(235, 68, 72, 230);
        glBegin(GlLines);
        glVertex3f(centerX - armX, centerY, 0f);
        glVertex3f(centerX - gapX, centerY, 0f);
        glVertex3f(centerX + gapX, centerY, 0f);
        glVertex3f(centerX + armX, centerY, 0f);
        glVertex3f(centerX, centerY + armY, 0f);
        glVertex3f(centerX, centerY + gapY, 0f);
        glVertex3f(centerX, centerY - gapY, 0f);
        glVertex3f(centerX, centerY - armY, 0f);
        glEnd();

        glColor4ub(245, 245, 245, 255);
        glBegin(GlQuads);
        glVertex3f(centerX - dotX, centerY + dotY, 0f);
        glVertex3f(centerX + dotX, centerY + dotY, 0f);
        glVertex3f(centerX + dotX, centerY - dotY, 0f);
        glVertex3f(centerX - dotX, centerY - dotY, 0f);
        glEnd();

        glLineWidth(1f);
        glEnable(GlDepthTest);
    }

    private void DrawGpuHeroDeploymentChargeRingPrimitive()
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null
            || !string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool exiting = entity.HeroDeploymentActive;
        double timerSec = exiting
            ? entity.HeroDeploymentExitHoldTimerSec
            : entity.HeroDeploymentHoldTimerSec;
        if (timerSec <= 1e-4 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        float progress = (float)Math.Clamp(timerSec / 2.0, 0.0, 1.0);
        DrawGpuScreenRing(ClientSize.Width * 0.5f, ClientSize.Height * 0.5f, 30f, 4f, Color.FromArgb(128, 24, 28, 34));
        DrawGpuScreenArc(
            ClientSize.Width * 0.5f,
            ClientSize.Height * 0.5f,
            30f,
            4f,
            -90f,
            progress * 360f,
            Color.FromArgb(exiting ? 235 : 235, exiting ? 255 : 255, exiting ? 132 : 216, 92),
            4f);
    }

    private void DrawGpuCrosshairStatusProgressPrimitive()
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        if (!TryResolveCrosshairStatusProgress(entity, out float progress, out Color color, out double remainingSec, out string label))
        {
            return;
        }

        float centerX = ClientSize.Width * 0.5f;
        float centerY = ClientSize.Height * 0.5f;
        DrawGpuScreenRing(centerX, centerY, 48f, 5.4f, Color.FromArgb(136, 12, 14, 18));
        DrawGpuScreenArc(centerX, centerY, 48f, 5.0f, -90f, progress * 360f, Color.FromArgb(238, color), 5.0f);

        _ = remainingSec;
        _ = label;
    }

    private bool TryResolveCrosshairStatusProgress(
        SimulationEntity entity,
        out float progress,
        out Color color,
        out double remainingSec,
        out string label)
    {
        progress = 0f;
        color = Color.White;
        remainingSec = 0.0;
        label = string.Empty;

        if (!entity.IsAlive)
        {
            remainingSec = Math.Max(0.0, entity.RespawnTimerSec);
            double totalSec = 15.0;
            progress = (float)Math.Clamp(1.0 - remainingSec / totalSec, 0.0, 1.0);
            label = "\u8bfb\u6761\u590d\u6d3b";
            color = Color.FromArgb(92, 224, 144);
            return true;
        }

        if (entity.PowerCutTimerSec > 1e-6)
        {
            remainingSec = Math.Max(0.0, entity.PowerCutTimerSec);
            double totalSec = 5.0;
            progress = (float)Math.Clamp(1.0 - remainingSec / totalSec, 0.0, 1.0);
            label = "\u5e95\u76d8\u65ad\u7535";
            color = Color.FromArgb(255, 88, 88);
            return true;
        }

        if (entity.HeatLockTimerSec > 1e-6 || string.Equals(entity.State, "heat_locked", StringComparison.OrdinalIgnoreCase))
        {
            ResolvedRoleProfile profile = _host.ResolveRuntimeProfile(entity);
            double coolingRate = Math.Max(0.1, profile.HeatDissipationRate * Math.Max(0.1, entity.DynamicCoolingMult));
            remainingSec = Math.Max(entity.HeatLockTimerSec, entity.Heat / coolingRate);
            double totalSec = Math.Max(0.5, ResolveHeatLockInitialHeatForProgress(entity) / coolingRate);
            progress = (float)Math.Clamp(1.0 - remainingSec / totalSec, 0.0, 1.0);
            label = "\u70ed\u91cf\u8d85\u9650";
            color = Color.FromArgb(255, 88, 88);
            return true;
        }

        return false;
    }

    private void DrawGpuScreenRing(float centerX, float centerY, float radius, float width, Color color)
        => DrawGpuScreenArc(centerX, centerY, radius, width, 0f, 360f, color, width);

    private void DrawGpuScreenArc(
        float centerX,
        float centerY,
        float radius,
        float width,
        float startAngleDeg,
        float sweepAngleDeg,
        Color color,
        float lineWidth)
    {
        if (ClientSize.Width <= 0
            || ClientSize.Height <= 0
            || radius <= 0.5f
            || lineWidth <= 0.1f
            || MathF.Abs(sweepAngleDeg) <= 1e-4f)
        {
            return;
        }

        glDisable(GlDepthTest);
        glDisable(GlLighting);
        glDisable(GlTexture2D);
        glEnable(GlBlend);
        glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);
        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));

        int segments = Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweepAngleDeg) / 8f), 16, 96);
        bool fullCircle = MathF.Abs(MathF.Abs(sweepAngleDeg) - 360f) <= 1e-3f;
        float startRad = MathF.PI / 180f * startAngleDeg;
        float sweepRad = MathF.PI / 180f * sweepAngleDeg;
        float cx = ScreenXToNdc(centerX);
        float cy = ScreenYToNdc(centerY);
        float scaleX = 2f / Math.Max(1f, ClientSize.Width);
        float scaleY = 2f / Math.Max(1f, ClientSize.Height);

        glLineWidth(lineWidth);
        glColor4ub(color.R, color.G, color.B, color.A);
        glBegin(fullCircle ? GlLineLoop : GlLineStrip);
        int pointCount = fullCircle ? segments : segments + 1;
        for (int i = 0; i < pointCount; i++)
        {
            float t = fullCircle
                ? i / (float)segments
                : i / (float)segments;
            float angle = startRad + sweepRad * t;
            float x = cx + MathF.Cos(angle) * radius * scaleX;
            float y = cy + MathF.Sin(angle) * radius * scaleY;
            glVertex3f(x, y, 0f);
        }
        glEnd();

        glLineWidth(1f);
        glEnable(GlDepthTest);
    }

    private void DrawGpuCrosshairStatusProgressRingsPrimitive()
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || _previewOnly || ClientSize.Width <= 8 || ClientSize.Height <= 8)
        {
            return;
        }

        float safeClientMin = Math.Clamp(Math.Min(ClientSize.Width, ClientSize.Height), 1f, 4096f);
        float diameter = Math.Clamp(safeClientMin * 0.57f, 330f, 840f);
        float centerX = ClientSize.Width * 0.5f;
        float centerY = ClientSize.Height * 0.5f;
        float arcWidth = Math.Clamp(diameter * 0.026f, 7.0f, 13.0f);
        float outerArcWidth = Math.Max(3.0f, arcWidth * 0.58f);

        float hpRatio = SafeGaugeRatio(entity.Health, entity.MaxHealth);
        float heatRatio = SafeGaugeRatio(entity.Heat, entity.MaxHeat);
        (float powerRatio, _) = ResolvePowerGauge(entity);
        (float superCapRatio, _) = ResolveSuperCapGauge(entity);
        float bufferRatio = SafeGaugeRatio(entity.BufferEnergyJ, entity.MaxBufferEnergyJ);

        DrawGpuRatioScreenArc(centerX, centerY, diameter, 180f, 90f, hpRatio, Color.FromArgb(128, 72, 214, 126), arcWidth);
        DrawGpuRatioScreenArc(centerX, centerY, diameter, 270f, 90f, powerRatio, Color.FromArgb(136, 255, 214, 48), arcWidth);
        DrawGpuRatioScreenArc(centerX, centerY, diameter, 0f, 90f, superCapRatio, Color.FromArgb(138, 255, 96, 196), arcWidth);
        DrawGpuRatioScreenArc(centerX, centerY, diameter, 90f, 90f, heatRatio, Color.FromArgb(128, 228, 130, 58), arcWidth);
        DrawGpuRatioScreenArc(centerX, centerY, diameter * 1.09f, 18f, 27f, bufferRatio, Color.FromArgb(96, 168, 174, 184), outerArcWidth);
    }

    private void DrawGpuAutoAimGuidancePrimitive()
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null
            || _autoAimAssistMode != AutoAimAssistMode.GuidanceOnly
            || !_autoAimPressed
            || !entity.AutoAimLocked
            || ShouldSuppressHeroDeploymentAimDecorations(entity))
        {
            return;
        }

        if (string.Equals(entity.AutoAimTargetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entity.AutoAimTargetId))
        {
            SimulationEntity? energyTarget = _host.World.Entities.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, entity.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
            if (energyTarget is not null
                && TryResolveTrackedEnergyDiskPose(entity, energyTarget, Math.Clamp(entity.AutoAimLeadTimeSec, 0.0, 1.10), out _, out Vector3 energyCenter, out float energyRadius, out _, out _, out _)
                && TryProject(energyCenter, out PointF energyPoint, out _))
            {
                float radius = Math.Max(13f, energyRadius * 16f);
                DrawGpuScreenCircle(energyPoint.X, energyPoint.Y, radius, Color.FromArgb(160, 0, 0, 0), 3f);
                DrawGpuScreenCircle(energyPoint.X, energyPoint.Y, radius, Color.FromArgb(238, 255, 214, 70), 1.5f);
                return;
            }
        }

        Vector3 marker = ToScenePoint(entity.AutoAimAimPointX, entity.AutoAimAimPointY, (float)entity.AutoAimAimPointHeightM);
        if (TryProject(marker, out PointF point, out _))
        {
            DrawGpuScreenCircle(point.X, point.Y, 13f, Color.FromArgb(160, 0, 0, 0), 3f);
            DrawGpuScreenCircle(point.X, point.Y, 13f, Color.FromArgb(238, 255, 214, 70), 1.5f);
        }
    }

    private void DrawGpuRatioScreenArc(float centerX, float centerY, float diameter, float startAngleDeg, float sweepAngleDeg, float ratio, Color color, float lineWidth)
    {
        float clamped = Math.Clamp(ratio, 0f, 1f);
        if (clamped <= 1e-4f || diameter <= 1f || lineWidth <= 0.1f)
        {
            return;
        }

        glDisable(GlDepthTest);
        glDisable(GlLighting);
        glDisable(GlTexture2D);
        glEnable(GlBlend);
        glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);
        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));

        int segments = Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweepAngleDeg) / 8f), 16, 96);
        float cx = ScreenXToNdc(centerX);
        float cy = ScreenYToNdc(centerY);
        float scaleX = 2f / Math.Max(1f, ClientSize.Width);
        float scaleY = 2f / Math.Max(1f, ClientSize.Height);
        float startRad = MathF.PI / 180f * startAngleDeg;
        float sweepRad = MathF.PI / 180f * sweepAngleDeg;
        float radius = diameter * 0.5f;
        float shadowWidth = Math.Max(1f, lineWidth * 0.5f);

        DrawGpuScreenArcCore(cx, cy, radius, scaleX, scaleY, startRad, sweepRad, clamped, Color.FromArgb(110, 0, 0, 0), lineWidth + shadowWidth);
        DrawGpuScreenArcCore(cx, cy, radius, scaleX, scaleY, startRad, sweepRad, clamped, color, lineWidth);
    }

    private void DrawGpuScreenCircle(float centerX, float centerY, float radius, Color color, float lineWidth)
        => DrawGpuRatioScreenArc(centerX, centerY, radius * 2f, 0f, 360f, 1f, color, lineWidth);

    private void DrawGpuScreenArcCore(float cx, float cy, float radius, float scaleX, float scaleY, float startRad, float sweepRad, float ratio, Color color, float lineWidth)
    {
        glLineWidth(lineWidth);
        glColor4ub(color.R, color.G, color.B, color.A);
        glBegin(GlLineStrip);
        int segments = Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweepRad) / (MathF.PI / 22.5f)), 16, 96);
        int pointCount = segments + 1;
        for (int i = 0; i < pointCount; i++)
        {
            float t = i / (float)segments * ratio;
            float angle = startRad + sweepRad * t;
            float x = cx + MathF.Cos(angle) * radius * scaleX;
            float y = cy + MathF.Sin(angle) * radius * scaleY;
            glVertex3f(x, y, 0f);
        }

        glEnd();
        glLineWidth(1f);
        glEnable(GlDepthTest);
    }

    private void DrawGpuScreenCross(Vector3 centerScene, float radiusPixels, Color color)
    {
        if (!TryProject(centerScene, out PointF point, out _))
        {
            return;
        }

        float cx = ScreenXToNdc(point.X);
        float cy = ScreenYToNdc(point.Y);
        float dx = radiusPixels / Math.Max(1f, ClientSize.Width) * 2f;
        float dy = radiusPixels / Math.Max(1f, ClientSize.Height) * 2f;
        glDisable(GlDepthTest);
        glDisable(GlLighting);
        glDisable(GlTexture2D);
        glEnable(GlBlend);
        glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);
        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glLineWidth(1.5f);
        glColor4ub(color.R, color.G, color.B, color.A);
        glBegin(GlLines);
        glVertex3f(cx - dx, cy, 0f);
        glVertex3f(cx - dx * 0.35f, cy, 0f);
        glVertex3f(cx + dx * 0.35f, cy, 0f);
        glVertex3f(cx + dx, cy, 0f);
        glVertex3f(cx, cy - dy, 0f);
        glVertex3f(cx, cy - dy * 0.35f, 0f);
        glVertex3f(cx, cy + dy * 0.35f, 0f);
        glVertex3f(cx, cy + dy, 0f);
        glEnd();
        glLineWidth(1f);
        glEnable(GlDepthTest);
    }

    private float ScreenXToNdc(float x)
        => x / Math.Max(1f, ClientSize.Width) * 2f - 1f;

    private float ScreenYToNdc(float y)
        => 1f - y / Math.Max(1f, ClientSize.Height) * 2f;

    private void ConfigureGpuOverlayGraphics(Graphics graphics)
    {
        bool activeReducedOverlay = _gpuOverlaySurfaceScale < 0.99f && !_paused;
        graphics.SmoothingMode = activeReducedOverlay
            ? System.Drawing.Drawing2D.SmoothingMode.HighSpeed
            : System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
        graphics.InterpolationMode = activeReducedOverlay
            ? System.Drawing.Drawing2D.InterpolationMode.Low
            : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.TextRenderingHint = activeReducedOverlay
            ? TextRenderingHint.SingleBitPerPixelGridFit
            : TextRenderingHint.ClearTypeGridFit;
    }

    private void TrackGpuOverlayPhase(string name, long startTicks)
    {
        if (!_measureGpuOverlayPhases)
        {
            return;
        }

        double elapsedMs = TicksToMs(Stopwatch.GetTimestamp() - startTicks);
        if (elapsedMs < GpuOverlaySlowPhaseThresholdMs)
        {
            return;
        }

        if (_currentGpuOverlayPhaseSummary.Length < 220)
        {
            _currentGpuOverlayPhaseSummary += $"{name}:{elapsedMs:0.0};";
        }
    }

    private void DrawGpuHeroLobSecondaryViewport(Graphics graphics)
    {
        _ = graphics;
        if (_gpuUseExtendedSceneTextureThisFrame)
        {
            return;
        }

        Rectangle mainViewport = _projectionViewportRect ?? new Rectangle(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        if (!TryResolveHeroLobSubviewCrop(mainViewport, out Rectangle viewport, out Rectangle sourceRect))
        {
            return;
        }

        bool deploymentSubviewOnly = IsHeroDeploymentSubviewOnlyMode();
        bool grayscale = _host.SelectedEntity?.HeroDeploymentActive == true;
        if (deploymentSubviewOnly)
        {
            if (CopyGpuHeroLobSubviewTexture(sourceRect, ClientSize, grayscale))
            {
                glClearColor(0f, 0f, 0f, 1f);
                glClear(GlColorBufferBit | GlDepthBufferBit);
                PresentGpuHeroLobSubviewCached(viewport);
            }

            return;
        }

        PresentGpuHeroLobSubviewTexture(viewport, sourceRect, grayscale);
    }

    private bool TryPrepareHeroLobExtendedScene(
        Rectangle mainViewport,
        out Rectangle viewport,
        out Rectangle sourceRect,
        out Size renderTargetSize,
        out Matrix4x4 projectionMatrix)
    {
        viewport = Rectangle.Empty;
        sourceRect = Rectangle.Empty;
        renderTargetSize = mainViewport.Size;
        projectionMatrix = _projectionMatrix;

        if (!TryBuildHeroLobSubviewSourceRequest(mainViewport, out viewport, out Rectangle desiredSourceRect))
        {
            return false;
        }

        int verticalSafetyPad = Math.Max(56, desiredSourceRect.Height);
        int extraBottom = Math.Max(0, desiredSourceRect.Bottom + verticalSafetyPad - mainViewport.Height);
        int maxUsefulExtra = Math.Max(desiredSourceRect.Height * 5, mainViewport.Height);
        int paddedExtra = Math.Min(extraBottom + 64, maxUsefulExtra);
        int renderHeight = AlignTo(Math.Max(mainViewport.Height, mainViewport.Height + paddedExtra), 16);
        renderTargetSize = new Size(mainViewport.Width, renderHeight);
        sourceRect = ClampSubviewSourceRect(desiredSourceRect, new Rectangle(0, 0, renderTargetSize.Width, renderTargetSize.Height));
        if (sourceRect.Width <= 8 || sourceRect.Height <= 8)
        {
            return false;
        }

        if (renderTargetSize.Height > mainViewport.Height
            && !TryCreateBottomExtendedProjection(_projectionMatrix, mainViewport.Height, renderTargetSize.Height, out projectionMatrix))
        {
            renderTargetSize = mainViewport.Size;
            sourceRect = ClampSubviewSourceRect(desiredSourceRect, new Rectangle(0, 0, renderTargetSize.Width, renderTargetSize.Height));
            projectionMatrix = _projectionMatrix;
        }

        return true;
    }

    private bool TryResolveHeroLobSubviewCrop(Rectangle sourceBounds, out Rectangle viewport, out Rectangle sourceRect)
    {
        if (!TryBuildHeroLobSubviewSourceRequest(sourceBounds, out viewport, out Rectangle desiredSourceRect))
        {
            sourceRect = Rectangle.Empty;
            return false;
        }

        sourceRect = ClampSubviewSourceRect(desiredSourceRect, sourceBounds);
        return sourceRect.Width > 8 && sourceRect.Height > 8;
    }

    private bool TryBuildHeroLobSubviewSourceRequest(Rectangle mainViewport, out Rectangle viewport, out Rectangle desiredSourceRect)
    {
        viewport = GetHeroLobSubviewPresentationRect();
        desiredSourceRect = Rectangle.Empty;

        SimulationEntity? shooter = _host.SelectedEntity;
        if (shooter is null
            || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || !SimulationCombatMath.IsHeroLobAutoAimMode(shooter))
        {
            return false;
        }

        if (viewport.Width <= 8 || viewport.Height <= 8 || !ShouldShowHeroLobSubview(shooter))
        {
            return false;
        }

        Size sourceViewportSize = viewport.Size;
        float zoomScale = 2.2f;
        PointF cropCenter = new(mainViewport.X + mainViewport.Width * 0.5f, mainViewport.Y + mainViewport.Height * 0.5f);
        if (TryResolveHeroLobSubviewCropCenterAndZoom(shooter, sourceViewportSize, mainViewport, out PointF resolvedCenter, out float resolvedZoomScale))
        {
            cropCenter = resolvedCenter;
            zoomScale = resolvedZoomScale;
        }

        int sourceWidth = Math.Max(32, (int)MathF.Round(sourceViewportSize.Width / Math.Max(1.0f, zoomScale)));
        int sourceHeight = Math.Max(32, (int)MathF.Round(sourceViewportSize.Height / Math.Max(1.0f, zoomScale)));
        int sourceX = (int)MathF.Round(cropCenter.X - sourceWidth * 0.5f);
        int sourceY = (int)MathF.Round(cropCenter.Y - sourceHeight * 0.5f);
        desiredSourceRect = new Rectangle(sourceX, sourceY, sourceWidth, sourceHeight);
        return true;
    }

    private bool TryResolveHeroLobSubviewCropCenterAndZoom(
        SimulationEntity shooter,
        Size viewportSize,
        Rectangle mainViewport,
        out PointF center,
        out float zoomScale)
    {
        center = new PointF(mainViewport.X + mainViewport.Width * 0.5f, mainViewport.Y + mainViewport.Height * 0.5f);
        zoomScale = 2.2f;

        if ((_autoAimPressed || shooter.HeroDeploymentActive)
            && IsHeroLobSubviewTrackingTarget(shooter)
            && TryGetProjectedHeroLobPlatePolygon(shooter, out PointF[] polygon))
        {
            RectangleF bounds = GetBounds(polygon);
            if (bounds.Width > 2f && bounds.Height > 2f)
            {
                float targetFraction = 0.34f;
                float aspect = Math.Max(0.6f, viewportSize.Width / (float)Math.Max(1, viewportSize.Height));
                float plateArea = Math.Max(16f, bounds.Width * bounds.Height);
                float desiredSourceArea = Math.Max(plateArea / targetFraction, 1024f);
                float sourceWidth = MathF.Sqrt(desiredSourceArea * aspect);
                float sourceHeight = sourceWidth / aspect;
                sourceWidth = Math.Max(sourceWidth, bounds.Width + Math.Max(28f, bounds.Width * 0.52f) * 2f);
                sourceHeight = Math.Max(sourceHeight, bounds.Height + Math.Max(34f, bounds.Height * 0.72f) * 2f);
                sourceWidth = Math.Min(sourceWidth, viewportSize.Width / 1.02f);
                sourceHeight = Math.Min(sourceHeight, viewportSize.Height / 1.02f);
                float zoomX = viewportSize.Width / Math.Max(8f, sourceWidth);
                float zoomY = viewportSize.Height / Math.Max(8f, sourceHeight);
                zoomScale = Math.Clamp(MathF.Min(zoomX, zoomY), 1.0f, 12f);
                float bottomBias = Math.Min(sourceHeight * 0.06f, Math.Max(8f, bounds.Height * 0.34f));
                center = new PointF(
                    bounds.Left + bounds.Width * 0.5f,
                    bounds.Top + bounds.Height * 0.5f + bottomBias);
                return true;
            }
        }

        if (TryGetAutoAimProjectedPoint(shooter, out PointF projectedAim))
        {
            center = projectedAim;
            return true;
        }

        return false;
    }

    private static RectangleF GetBounds(IReadOnlyList<PointF> points)
    {
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        for (int index = 0; index < points.Count; index++)
        {
            PointF point = points[index];
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY))
        {
            return RectangleF.Empty;
        }

        return RectangleF.FromLTRB(minX, minY, maxX, maxY);
    }

    private static Rectangle ClampSubviewSourceRect(Rectangle sourceRect, Rectangle bounds)
    {
        int width = Math.Min(sourceRect.Width, bounds.Width);
        int height = Math.Min(sourceRect.Height, bounds.Height);
        int x = Math.Clamp(sourceRect.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - width));
        int y = Math.Clamp(sourceRect.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    private static bool TryCreateBottomExtendedProjection(Matrix4x4 sourceProjection, int mainHeight, int renderHeight, out Matrix4x4 projectionMatrix)
    {
        projectionMatrix = sourceProjection;
        if (renderHeight <= mainHeight || mainHeight <= 0)
        {
            return true;
        }

        float nearPlane = sourceProjection.M43 / sourceProjection.M33;
        float farPlane = sourceProjection.M43 / (sourceProjection.M33 + 1f);
        if (!float.IsFinite(nearPlane) || !float.IsFinite(farPlane) || nearPlane <= 1e-4f || farPlane <= nearPlane)
        {
            return false;
        }

        float top = nearPlane / Math.Max(1e-4f, sourceProjection.M22);
        float right = nearPlane / Math.Max(1e-4f, sourceProjection.M11);
        float heightScale = renderHeight / (float)mainHeight;
        float bottom = -top * Math.Max(1f, heightScale * 2f - 1f);
        projectionMatrix = Matrix4x4.CreatePerspectiveOffCenter(-right, right, bottom, top, nearPlane, farPlane);
        return true;
    }

    private static int AlignTo(int value, int alignment)
    {
        if (alignment <= 1)
        {
            return Math.Max(1, value);
        }

        return Math.Max(alignment, ((Math.Max(1, value) + alignment - 1) / alignment) * alignment);
    }

    private void PresentGpuHeroLobSubviewTexture(Rectangle destinationRect, Rectangle sourceRect, bool grayscale)
    {
        if (CopyGpuHeroLobSubviewTexture(sourceRect, ClientSize, grayscale))
        {
            PresentGpuHeroLobSubviewCached(destinationRect);
        }
    }

    private bool CopyGpuHeroLobSubviewTexture(Rectangle sourceRect, Size sourceFramebufferSize, bool grayscale)
    {
        if (sourceRect.Width <= 0
            || sourceRect.Height <= 0
            || sourceFramebufferSize.Width <= 0
            || sourceFramebufferSize.Height <= 0)
        {
            return false;
        }

        if (_gpuHeroLobSubviewTexture == 0)
        {
            glGenTextures(1, out _gpuHeroLobSubviewTexture);
        }

        glBindTexture(GlTexture2D, _gpuHeroLobSubviewTexture);
        glTexParameteri(GlTexture2D, GlTextureMinFilter, GlNearest);
        glTexParameteri(GlTexture2D, GlTextureMagFilter, GlNearest);

        int copyY = Math.Max(0, sourceFramebufferSize.Height - sourceRect.Bottom);
        if (_gpuHeroLobSubviewTextureSize != sourceRect.Size
            || _gpuHeroLobSubviewTextureUsesGrayscale != grayscale)
        {
            glCopyTexImage2D(GlTexture2D, 0, grayscale ? GlLuminance : GlRgba, sourceRect.X, copyY, sourceRect.Width, sourceRect.Height, 0);
            _gpuHeroLobSubviewTextureSize = sourceRect.Size;
            _gpuHeroLobSubviewTextureUsesGrayscale = grayscale;
        }
        else
        {
            glCopyTexSubImage2D(GlTexture2D, 0, 0, 0, sourceRect.X, copyY, sourceRect.Width, sourceRect.Height);
        }

        return true;
    }

    private void PresentGpuHeroLobSubviewCached(Rectangle destinationRect)
    {
        if (_gpuHeroLobSubviewTexture == 0
            || _gpuHeroLobSubviewTextureSize.Width <= 0
            || _gpuHeroLobSubviewTextureSize.Height <= 0
            || destinationRect.Width <= 0
            || destinationRect.Height <= 0)
        {
            return;
        }

        glDisable(GlDepthTest);
        glEnable(GlTexture2D);
        glBindTexture(GlTexture2D, _gpuHeroLobSubviewTexture);
        glTexParameteri(GlTexture2D, GlTextureMinFilter, GlNearest);
        glTexParameteri(GlTexture2D, GlTextureMagFilter, GlNearest);

        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glColor4ub(255, 255, 255, 255);

        float left = destinationRect.Left / (float)Math.Max(1, ClientSize.Width) * 2f - 1f;
        float right = destinationRect.Right / (float)Math.Max(1, ClientSize.Width) * 2f - 1f;
        float top = 1f - destinationRect.Top / (float)Math.Max(1, ClientSize.Height) * 2f;
        float bottom = 1f - destinationRect.Bottom / (float)Math.Max(1, ClientSize.Height) * 2f;

        glBegin(GlQuads);
        glTexCoord2f(0f, 1f);
        glVertex3f(left, top, 0f);
        glTexCoord2f(1f, 1f);
        glVertex3f(right, top, 0f);
        glTexCoord2f(1f, 0f);
        glVertex3f(right, bottom, 0f);
        glTexCoord2f(0f, 0f);
        glVertex3f(left, bottom, 0f);
        glEnd();
        glDisable(GlTexture2D);
    }

    private void PresentGpuTextureRegion(int textureId, Size textureSize, Rectangle sourceRect, Rectangle destinationRect, bool useNearestFilter = false)
    {
        if (textureId == 0
            || textureSize.Width <= 0
            || textureSize.Height <= 0
            || sourceRect.Width <= 0
            || sourceRect.Height <= 0
            || destinationRect.Width <= 0
            || destinationRect.Height <= 0)
        {
            return;
        }

        glDisable(GlDepthTest);
        glEnable(GlTexture2D);
        glBindTexture(GlTexture2D, textureId);
        int textureFilter = useNearestFilter ? GlNearest : GlLinear;
        glTexParameteri(GlTexture2D, GlTextureMinFilter, textureFilter);
        glTexParameteri(GlTexture2D, GlTextureMagFilter, textureFilter);
        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glColor4ub(255, 255, 255, 255);

        float left = destinationRect.Left / (float)Math.Max(1, ClientSize.Width) * 2f - 1f;
        float right = destinationRect.Right / (float)Math.Max(1, ClientSize.Width) * 2f - 1f;
        float top = 1f - destinationRect.Top / (float)Math.Max(1, ClientSize.Height) * 2f;
        float bottom = 1f - destinationRect.Bottom / (float)Math.Max(1, ClientSize.Height) * 2f;

        float texLeft = sourceRect.Left / (float)textureSize.Width;
        float texRight = sourceRect.Right / (float)textureSize.Width;
        float texTop = 1f - sourceRect.Top / (float)textureSize.Height;
        float texBottom = 1f - sourceRect.Bottom / (float)textureSize.Height;

        glBegin(GlQuads);
        glTexCoord2f(texLeft, texTop);
        glVertex3f(left, top, 0f);
        glTexCoord2f(texRight, texTop);
        glVertex3f(right, top, 0f);
        glTexCoord2f(texRight, texBottom);
        glVertex3f(right, bottom, 0f);
        glTexCoord2f(texLeft, texBottom);
        glVertex3f(left, bottom, 0f);
        glEnd();
        glDisable(GlTexture2D);
    }

    private void LogGpuRenderPerfIfDue(
        long frameStartTicks,
        long terrainTicks,
        long facilityTicks,
        long entityTicks,
        long projectileTicks,
        long flushTicks,
        long overlayTicks,
        long swapTicks,
        int facilityVertices,
        int entityVertices,
        int projectileVertices)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        if (_lastGpuRenderPerfLogTicks > 0
            && (nowTicks - _lastGpuRenderPerfLogTicks) / (double)Stopwatch.Frequency < 2.0)
        {
            return;
        }

        _lastGpuRenderPerfLogTicks = nowTicks;
        string mapStats = string.IsNullOrWhiteSpace(_terrainCacheGpuSourcePath)
            ? $"{_gpuTerrainVertexCount}v/1draw"
            : $"{_terrainCacheGpuVisibleVertices}v/{_terrainCacheGpuDrawCalls}draw/{_terrainCacheGpuResidentVertices}resident/{_terrainCacheGpuPendingUploads}pending";
        SimulatorRenderPassPlan passPlan = ResolveRenderPassPlan();
        SimulatorFramePacingPlan pacingPlan = ResolveFramePacingPlan();
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} "
            + $"mode={(_host.SelectedEntity?.HeroDeploymentActive == true ? "deployment_subview" : "normal")} "
            + $"pipeline={passPlan.Label} targetHz={pacingPlan.TargetHz:0.0} "
            + $"frame={ElapsedMs(frameStartTicks, nowTicks):0.00}ms "
            + $"map={TicksToMs(terrainTicks):0.00}ms/{mapStats} "
            + $"facility={TicksToMs(facilityTicks):0.00}ms/{facilityVertices}v/{(facilityVertices > 0 ? 1 : 0)}draw "
            + $"unit={TicksToMs(entityTicks):0.00}ms/{entityVertices}v/{(entityVertices > 0 ? 1 : 0)}draw "
            + $"projectile={TicksToMs(projectileTicks):0.00}ms/{projectileVertices}v/{(projectileVertices > 0 ? 1 : 0)}draw "
            + $"flush={TicksToMs(flushTicks):0.00}ms overlay={TicksToMs(overlayTicks):0.00}ms swap={TicksToMs(swapTicks):0.00}ms "
            + $"overlay_detail(uploaded={(_lastGpuOverlayUploaded ? 1 : 0)} scale={_gpuOverlaySurfaceScale:0.00} "
            + $"draw={TicksToMs(_lastGpuOverlayDrawCostTicks):0.00}ms tex={TicksToMs(_lastGpuOverlayUploadCostTicks):0.00}ms present={TicksToMs(_lastGpuOverlayPresentCostTicks):0.00}ms "
            + $"phases={_lastGpuOverlayPhaseSummary}) "
            + $"entities={_host.World.Entities.Count} projectiles={_host.World.Projectiles.Count} "
            + $"detail(full={_lastGpuFullDetailEntityRenderCount}/{TicksToMs(_lastGpuFullDetailEntityRenderTicks):0.00}ms,"
            + $"proxy={_lastGpuProxyEntityRenderCount}/{TicksToMs(_lastGpuProxyEntityRenderTicks):0.00}ms,"
            + $"structure={_lastGpuStructureEntityRenderCount}/{TicksToMs(_lastGpuStructureEntityRenderTicks):0.00}ms,"
            + $"energy={_lastGpuEnergyEntityRenderCount}/{TicksToMs(_lastGpuEnergyEntityRenderTicks):0.00}ms)";

        SimulatorRuntimeLog.Append("render_perf.log", line);
    }

    private static double ElapsedMs(long startTicks, long endTicks)
        => (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;

    private static double TicksToMs(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;

    internal void AttachExternalBorrowedGpuContext()
    {
        _gpuContextBorrowedExternally = true;
        _gpuContextFailed = false;
        _gpuContextReady = true;
        TryInitializeGpuBufferApi();
    }

    internal bool ExternalRenderToCurrentOpenGlContext()
    {
        if (!UseGpuRenderer || UseFastFlatRenderer)
        {
            return false;
        }

        _gpuContextBorrowedExternally = true;
        _gpuContextFailed = false;
        _gpuContextReady = true;
        TryInitializeGpuBufferApi();
        EnsureExternalGpuScratchSurface();
        SimulatorRenderPassPlan passPlan = ResolveRenderPassPlan();

        if (_appState == SimulatorAppState.MainMenu)
        {
            if (!passPlan.RenderWorld)
            {
                DrawGpuOpenGkFrozenBackdropOverlay(graphics => DrawOpenGkMainMenuForeground(graphics));
            }
            else
            {
                DrawGpuOpenGkMenuScene(_gpuExternalScratchGraphics!);
                DrawGpuOpenGkMenuOverlay();
            }
            return true;
        }

        if (_appState == SimulatorAppState.Lobby)
        {
            if (!passPlan.RenderWorld)
            {
                DrawGpuOpenGkFrozenBackdropOverlay(graphics => DrawOpenGkLanRoomScreen(graphics));
            }
            else
            {
                DrawGpuOpenGkLobbyScene(_gpuExternalScratchGraphics!);
                DrawGpuOpenGkLobbyOverlay();
            }
            return true;
        }

        if (_appState != SimulatorAppState.InMatch)
        {
            return false;
        }

        UpdateCameraMatrices();
        DrawGpuMatch(_gpuExternalScratchGraphics!);
        MarkMatchStartupViewReady();
        return true;
    }

    private void DrawGpuOpenGkMenuScene(Graphics graphics)
    {
        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        double previousGameTimeSec = _host.World.GameTimeSec;
        bool previousGeometryPass = _gpuGeometryPass;
        bool previousBatching = _gpuBatchingDynamicGeometry;
        GpuDynamicBatchKind previousBatch = _gpuCurrentDynamicBatch;
        bool previousSuppressLabels = _suppressEntityLabels;
        bool previousUseProfileColors = _useProfileColorsForVehiclePreview;

        if (!EnsureGpuContext())
        {
            return;
        }

        if (!ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid))
        {
            RebuildTerrainTileCache();
        }

        if (!MakeGpuContextCurrent())
        {
            return;
        }

        int width = Math.Max(1, ClientSize.Width);
        int height = Math.Max(1, ClientSize.Height);

        try
        {
            glViewport(0, 0, width, height);
            glClearColor(0.030f, 0.037f, 0.050f, 1f);
            glClear(GlColorBufferBit | GlDepthBufferBit);
            glEnable(GlDepthTest);
            glEnable(GlBlend);
            glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);

            UpdateOpenGkBackdropCamera(new Size(width, height));
            _projectionViewportRect = new Rectangle(0, 0, width, height);
            _host.World.GameTimeSec = Math.Max(previousGameTimeSec, _frameClock.Elapsed.TotalSeconds);
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(0.60f, width / (float)Math.Max(1, height), 0.03f, 900f);
            glMatrixMode(GlProjection);
            glLoadMatrixf(ToOpenGlMatrix(_projectionMatrix));
            glMatrixMode(GlModelView);
            glLoadMatrixf(ToOpenGlMatrix(_viewMatrix));

            _gpuDynamicVertexBuildBuffer.Clear();
            _gpuEnergyMechanismVertexBuildBuffer.Clear();
            _gpuProjectileVertexBuildBuffer.Clear();
            _gpuBatchingDynamicGeometry = true;
            _gpuGeometryPass = true;

            DrawGpuTerrainBase();
            DrawGpuTerrainGeometry();
            _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Facility;
            DrawGpuFacilities();
            DrawGpuTeamTopNeonLights();
            _suppressEntityLabels = true;
            DrawStaticStructureBodies(graphics);
            _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Entity;
            DrawEntityGeometry(graphics);
            FlushGpuEnergyMechanismVertices();
            FlushGpuDynamicVertices();
        }
        finally
        {
            _host.World.GameTimeSec = previousGameTimeSec;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _gpuGeometryPass = previousGeometryPass;
            _gpuBatchingDynamicGeometry = previousBatching;
            _gpuCurrentDynamicBatch = previousBatch;
            _suppressEntityLabels = previousSuppressLabels;
            _useProfileColorsForVehiclePreview = previousUseProfileColors;
        }
    }

    private void DrawGpuOpenGkMenuOverlay()
    {
        EnsureGpuOverlaySurface();
        if (_gpuOverlayGraphics is null)
        {
            return;
        }

        _uiButtons.Clear();
        _gpuOverlayGraphics.Clear(Color.Transparent);
        _gpuOverlayGraphics.ResetTransform();
        _gpuOverlayGraphics.ScaleTransform(_gpuOverlaySurfaceScale, _gpuOverlaySurfaceScale);
        ConfigureGpuOverlayGraphics(_gpuOverlayGraphics);
        DrawOpenGkMainMenuForeground(_gpuOverlayGraphics);
        _gpuOverlayGraphics.ResetTransform();
        UploadGpuOverlayBitmap();
        PresentGpuOverlayTexture();
    }

    private void DrawGpuOpenGkFrozenBackdropOverlay(Action<Graphics> drawOverlay)
    {
        EnsureGpuOverlaySurface();
        if (_gpuOverlayGraphics is null)
        {
            return;
        }

        long nowTicks = _frameClock.ElapsedTicks;
        SimulatorRenderPassPlan passPlan = ResolveRenderPassPlan();
        bool throttleLanRoomUi =
            passPlan.Mode == SimulatorRenderModeKind.LanRoom
            && _gpuOverlayTexture != 0
            && _gpuOverlayBitmap is not null
            && _gpuOverlayTextureSize == _gpuOverlayBitmap.Size
            && !_gpuOverlayUiDirty;
        if (throttleLanRoomUi
            && _lastGpuOverlayUiUploadTicks > 0
            && (nowTicks - _lastGpuOverlayUiUploadTicks) / (double)Stopwatch.Frequency < passPlan.UiOverlayUploadIntervalSec)
        {
            PresentGpuOverlayTexture();
            return;
        }

        EnsureOpenGkBackdropBitmap(allowSuppressedRefresh: true);
        _uiButtons.Clear();
        _gpuOverlayGraphics.Clear(Color.FromArgb(6, 8, 12));
        _gpuOverlayGraphics.ResetTransform();
        _gpuOverlayGraphics.ScaleTransform(_gpuOverlaySurfaceScale, _gpuOverlaySurfaceScale);
        ConfigureGpuOverlayGraphics(_gpuOverlayGraphics);
        if (_openGkBackdropBitmap is not null)
        {
            _gpuOverlayGraphics.DrawImage(_openGkBackdropBitmap, ClientRectangle);
        }

        drawOverlay(_gpuOverlayGraphics);
        _gpuOverlayGraphics.ResetTransform();
        UploadGpuOverlayBitmap();
        _lastGpuOverlayUiUploadTicks = nowTicks;
        _gpuOverlayUiDirty = false;
        PresentGpuOverlayTexture();
    }

    private void DrawGpuOpenGkLobbyScene(Graphics graphics)
    {
        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        double previousGameTimeSec = _host.World.GameTimeSec;
        bool previousGeometryPass = _gpuGeometryPass;
        bool previousBatching = _gpuBatchingDynamicGeometry;
        GpuDynamicBatchKind previousBatch = _gpuCurrentDynamicBatch;
        bool previousSuppressLabels = _suppressEntityLabels;
        bool previousUseProfileColors = _useProfileColorsForVehiclePreview;

        if (!EnsureGpuContext())
        {
            return;
        }

        if (!ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid))
        {
            RebuildTerrainTileCache();
        }

        if (!MakeGpuContextCurrent())
        {
            return;
        }

        int width = Math.Max(1, ClientSize.Width);
        int height = Math.Max(1, ClientSize.Height);

        try
        {
            glViewport(0, 0, width, height);
            glClearColor(0.030f, 0.037f, 0.050f, 1f);
            glClear(GlColorBufferBit | GlDepthBufferBit);
            glEnable(GlDepthTest);
            glEnable(GlBlend);
            glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);

            UpdateOpenGkBackdropCamera(new Size(width, height));
            _projectionViewportRect = new Rectangle(0, 0, width, height);
            _host.World.GameTimeSec = Math.Max(previousGameTimeSec, _frameClock.Elapsed.TotalSeconds);
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(0.60f, width / (float)Math.Max(1, height), 0.03f, 900f);
            glMatrixMode(GlProjection);
            glLoadMatrixf(ToOpenGlMatrix(_projectionMatrix));
            glMatrixMode(GlModelView);
            glLoadMatrixf(ToOpenGlMatrix(_viewMatrix));

            _gpuDynamicVertexBuildBuffer.Clear();
            _gpuEnergyMechanismVertexBuildBuffer.Clear();
            _gpuProjectileVertexBuildBuffer.Clear();
            _gpuBatchingDynamicGeometry = true;
            _gpuGeometryPass = true;

            DrawGpuTerrainBase();
            DrawGpuTerrainGeometry();
            _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Facility;
            DrawGpuFacilities();
            DrawGpuTeamTopNeonLights();
            _suppressEntityLabels = true;
            DrawStaticStructureBodies(graphics);
            _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Entity;
            DrawEntityGeometry(graphics);
            FlushGpuEnergyMechanismVertices();
            FlushGpuDynamicVertices();
        }
        finally
        {
            _host.World.GameTimeSec = previousGameTimeSec;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _gpuGeometryPass = previousGeometryPass;
            _gpuBatchingDynamicGeometry = previousBatching;
            _gpuCurrentDynamicBatch = previousBatch;
            _suppressEntityLabels = previousSuppressLabels;
            _useProfileColorsForVehiclePreview = previousUseProfileColors;
        }
    }

    private void DrawGpuOpenGkLobbyOverlay()
    {
        EnsureGpuOverlaySurface();
        if (_gpuOverlayGraphics is null)
        {
            return;
        }

        _uiButtons.Clear();
        _gpuOverlayGraphics.Clear(Color.Transparent);
        _gpuOverlayGraphics.ResetTransform();
        _gpuOverlayGraphics.ScaleTransform(_gpuOverlaySurfaceScale, _gpuOverlaySurfaceScale);
        ConfigureGpuOverlayGraphics(_gpuOverlayGraphics);

        if (IsLanMultiplayerActive)
        {
            using var shade = new SolidBrush(Color.FromArgb(112, 0, 0, 0));
            _gpuOverlayGraphics.FillRectangle(shade, ClientRectangle);
            DrawOpenGkLanRoomScreen(_gpuOverlayGraphics);
        }
        else
        {
            DrawOpenGkMainHeader(_gpuOverlayGraphics);
            DrawOpenGkLobbyHud(_gpuOverlayGraphics);
        }

        _gpuOverlayGraphics.ResetTransform();
        UploadGpuOverlayBitmap();
        PresentGpuOverlayTexture();
    }

    private Bitmap? RenderLobbyVehiclePreviewGpu(
        SimulationEntity entity,
        Size size,
        double fixedAngleDeg = 34.0,
        double fixedTurretYawDeg = 16.0,
        double fixedGimbalPitchDeg = -6.0,
        float distanceScaleOverride = 1.45f)
    {
        int width = Math.Clamp(size.Width, 16, 2048);
        int height = Math.Clamp(size.Height, 16, 2048);
        if (!EnsureGpuContext() || !MakeGpuContextCurrent())
        {
            return null;
        }

        TryInitializeGpuBufferApi();
        TryInitializeGpuFramebufferApi();
        if (!EnsureGpuSceneRenderTarget(new Size(width, height)) || _glBindFramebuffer is null)
        {
            return null;
        }

        RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        bool previousGeometryPass = _gpuGeometryPass;
        bool previousBatching = _gpuBatchingDynamicGeometry;
        GpuDynamicBatchKind previousBatch = _gpuCurrentDynamicBatch;
        bool previousSuppressLabels = _suppressEntityLabels;
        bool previousUseProfileColors = _useProfileColorsForVehiclePreview;
        double previousAngle = entity.AngleDeg;
        double previousTurretYaw = entity.TurretYawDeg;
        double previousPitch = entity.GimbalPitchDeg;

        try
        {
            _glBindFramebuffer(GlFramebuffer, _gpuSceneFramebuffer);
            glViewport(0, 0, width, height);
            glClearColor(0.080f, 0.095f, 0.118f, 1f);
            glClear(GlColorBufferBit | GlDepthBufferBit);
            glEnable(GlDepthTest);
            glEnable(GlBlend);
            glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);

            _projectionViewportRect = new Rectangle(0, 0, width, height);
            _suppressEntityLabels = true;
            _gpuGeometryPass = true;
            _gpuBatchingDynamicGeometry = true;
            _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Entity;
            _useProfileColorsForVehiclePreview = true;
            _gpuDynamicVertexBuildBuffer.Clear();
            _gpuEnergyMechanismVertexBuildBuffer.Clear();
            _gpuProjectileVertexBuildBuffer.Clear();

            entity.AngleDeg = fixedAngleDeg;
            entity.TurretYawDeg = fixedTurretYawDeg;
            entity.GimbalPitchDeg = fixedGimbalPitchDeg;

            float previewExtent = Math.Max(
                0.45f,
                Math.Max(
                    profile.BodyLengthM + profile.BarrelLengthM * 0.8f,
                    Math.Max(profile.BodyWidthM, profile.GimbalHeightM + profile.BodyClearanceM)));
            _cameraTargetM = new Vector3(0f, Math.Max(0.22f, profile.BodyClearanceM + profile.BodyHeightM * 0.55f), 0f);
            float distance = Math.Clamp(previewExtent * Math.Clamp(distanceScaleOverride, 0.86f, 1.75f), 0.62f, 3.2f);
            _cameraPositionM = _cameraTargetM + new Vector3(distance * 0.86f, distance * 0.52f, distance * 1.08f);
            _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, Vector3.UnitY);
            float aspect = Math.Max(0.6f, width / (float)Math.Max(1, height));
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(0.86f, aspect, 0.02f, 40f);

            glMatrixMode(GlProjection);
            glLoadMatrixf(ToOpenGlMatrix(_projectionMatrix));
            glMatrixMode(GlModelView);
            glLoadMatrixf(ToOpenGlMatrix(_viewMatrix));

            using Bitmap scratchBitmap = new(1, 1, PixelFormat.Format32bppPArgb);
            using Graphics scratchGraphics = Graphics.FromImage(scratchBitmap);
            DrawEntityAppearanceModelModern(scratchGraphics, entity, Vector3.Zero, profile);
            FlushGpuDynamicVertices();

            Bitmap bitmap = new(width, height, PixelFormat.Format32bppPArgb);
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                glPixelStorei(GlPackAlignment, 4);
                glReadPixels(0, 0, width, height, GlBgra, GlUnsignedByte, data.Scan0);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            entity.AngleDeg = previousAngle;
            entity.TurretYawDeg = previousTurretYaw;
            entity.GimbalPitchDeg = previousPitch;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _gpuGeometryPass = previousGeometryPass;
            _gpuBatchingDynamicGeometry = previousBatching;
            _gpuCurrentDynamicBatch = previousBatch;
            _suppressEntityLabels = previousSuppressLabels;
            _useProfileColorsForVehiclePreview = previousUseProfileColors;
            _glBindFramebuffer?.Invoke(GlFramebuffer, 0);
            if (ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                glViewport(0, 0, ClientSize.Width, ClientSize.Height);
            }
        }
    }

    private Bitmap? RenderOpenGkBackdropGpu(Size size)
    {
        int width = Math.Clamp(size.Width, 16, 2048);
        int height = Math.Clamp(size.Height, 16, 2048);
        if (!EnsureGpuContext() || !MakeGpuContextCurrent())
        {
            return null;
        }

        TryInitializeGpuBufferApi();
        TryInitializeGpuFramebufferApi();
        if (!EnsureGpuSceneRenderTarget(new Size(width, height)) || _glBindFramebuffer is null)
        {
            return null;
        }

        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        bool previousGeometryPass = _gpuGeometryPass;
        bool previousBatching = _gpuBatchingDynamicGeometry;
        GpuDynamicBatchKind previousBatch = _gpuCurrentDynamicBatch;
        bool previousSuppressLabels = _suppressEntityLabels;
        bool previousUseProfileColors = _useProfileColorsForVehiclePreview;
        double previousGameTimeSec = _host.World.GameTimeSec;

        try
        {
            _glBindFramebuffer(GlFramebuffer, _gpuSceneFramebuffer);
            glViewport(0, 0, width, height);
            glClearColor(0.035f, 0.042f, 0.055f, 1f);
            glClear(GlColorBufferBit | GlDepthBufferBit);
            glEnable(GlDepthTest);
            glEnable(GlBlend);
            glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);

            _projectionViewportRect = new Rectangle(0, 0, width, height);
            _gpuGeometryPass = true;
            _gpuBatchingDynamicGeometry = true;
            _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Entity;
            _suppressEntityLabels = true;
            _useProfileColorsForVehiclePreview = true;
            _gpuDynamicVertexBuildBuffer.Clear();
            _gpuEnergyMechanismVertexBuildBuffer.Clear();
            _gpuProjectileVertexBuildBuffer.Clear();

            UpdateOpenGkBackdropCamera(new Size(width, height));
            _host.World.GameTimeSec = Math.Max(previousGameTimeSec, _frameClock.Elapsed.TotalSeconds);

            glMatrixMode(GlProjection);
            glLoadMatrixf(ToOpenGlMatrix(_projectionMatrix));
            glMatrixMode(GlModelView);
            glLoadMatrixf(ToOpenGlMatrix(_viewMatrix));

            using Bitmap scratchBitmap = new(1, 1, PixelFormat.Format32bppPArgb);
            using Graphics scratchGraphics = Graphics.FromImage(scratchBitmap);
            DrawGpuTerrainBase();
            DrawGpuTerrainGeometry();
            DrawGpuFacilities();
            DrawGpuTeamTopNeonLights();
            DrawStaticStructureBodies(scratchGraphics);
            DrawEntityGeometry(scratchGraphics);
            FlushGpuEnergyMechanismVertices();
            FlushGpuDynamicVertices();
            FlushGpuProjectileVertices();

            Bitmap bitmap = new(width, height, PixelFormat.Format32bppPArgb);
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                glPixelStorei(GlPackAlignment, 4);
                glReadPixels(0, 0, width, height, GlBgra, GlUnsignedByte, data.Scan0);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            _host.World.GameTimeSec = previousGameTimeSec;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _gpuGeometryPass = previousGeometryPass;
            _gpuBatchingDynamicGeometry = previousBatching;
            _gpuCurrentDynamicBatch = previousBatch;
            _suppressEntityLabels = previousSuppressLabels;
            _useProfileColorsForVehiclePreview = previousUseProfileColors;
            _glBindFramebuffer?.Invoke(GlFramebuffer, 0);
            if (ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                glViewport(0, 0, ClientSize.Width, ClientSize.Height);
            }
        }
    }

    private bool EnsureGpuContext()
    {
        if (_gpuContextReady)
        {
            if (_gpuContextBorrowedExternally)
            {
                TryInitializeGpuBufferApi();
            }

            return true;
        }

        if (_gpuContextBorrowedExternally)
        {
            _gpuContextReady = true;
            TryInitializeGpuBufferApi();
            return true;
        }

        if (_gpuContextFailed || !IsHandleCreated)
        {
            return false;
        }

        _gpuDeviceContext = GetDC(Handle);
        if (_gpuDeviceContext == IntPtr.Zero)
        {
            _gpuContextFailed = true;
            return false;
        }

        PixelFormatDescriptor descriptor = new()
        {
            Size = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(),
            Version = 1,
            Flags = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer,
            PixelType = PfdTypeRgba,
            ColorBits = 32,
            DepthBits = 24,
            StencilBits = 8,
            LayerType = 0,
        };
        int pixelFormat = ChoosePixelFormat(_gpuDeviceContext, ref descriptor);
        if (pixelFormat <= 0 || !SetPixelFormat(_gpuDeviceContext, pixelFormat, ref descriptor))
        {
            _gpuContextFailed = true;
            return false;
        }

        _gpuRenderContext = wglCreateContext(_gpuDeviceContext);
        if (_gpuRenderContext == IntPtr.Zero || !wglMakeCurrent(_gpuDeviceContext, _gpuRenderContext))
        {
            _gpuContextFailed = true;
            return false;
        }

        TryDisableGpuVSync();
        TryInitializeGpuBufferApi();
        _gpuContextReady = true;
        return true;
    }

    private void TryDisableGpuVSync()
    {
        IntPtr proc = wglGetProcAddress("wglSwapIntervalEXT");
        if (proc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Marshal.GetDelegateForFunctionPointer<WglSwapIntervalExt>(proc)(0);
        }
        catch
        {
        }
    }

    private void DrawGpuTerrainBase()
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        float widthM = _host.MapPreset.Width * scale;
        float heightM = _host.MapPreset.Height * scale;
        if (EnsureGpuTerrainTexture())
        {
            glEnable(GlTexture2D);
            glBindTexture(GlTexture2D, _gpuTerrainTexture);
            glColor4ub(255, 255, 255, 255);
            glBegin(GlQuads);
            glTexCoord2f(0f, 0f);
            glVertex3f(0f, 0f, 0f);
            glTexCoord2f(1f, 0f);
            glVertex3f(widthM, 0f, 0f);
            glTexCoord2f(1f, 1f);
            glVertex3f(widthM, 0f, heightM);
            glTexCoord2f(0f, 1f);
            glVertex3f(0f, 0f, heightM);
            glEnd();
            glDisable(GlTexture2D);
            return;
        }

        DrawGpuQuad(
            new Vector3(0f, 0f, 0f),
            new Vector3(widthM, 0f, 0f),
            new Vector3(widthM, 0f, heightM),
            new Vector3(0f, 0f, heightM),
            Color.FromArgb(72, 62, 78, 72));
    }

    private bool EnsureGpuTerrainTexture()
    {
        EnsureTerrainColorBitmapLoaded();
        if (_terrainColorBitmap is null)
        {
            return false;
        }

        if (_gpuTerrainTexture != 0
            && string.Equals(_gpuTerrainTexturePath, _terrainColorBitmapPath, StringComparison.OrdinalIgnoreCase)
            && _gpuTerrainTextureSize == _terrainColorBitmap.Size)
        {
            return true;
        }

        if (_gpuTerrainTexture != 0)
        {
            int oldTexture = _gpuTerrainTexture;
            glDeleteTextures(1, ref oldTexture);
            _gpuTerrainTexture = 0;
        }

        using Bitmap uploadBitmap = new(_terrainColorBitmap.Width, _terrainColorBitmap.Height, PixelFormat.Format32bppArgb);
        using (Graphics uploadGraphics = Graphics.FromImage(uploadBitmap))
        {
            uploadGraphics.DrawImageUnscaled(_terrainColorBitmap, 0, 0);
        }

        BitmapData data = uploadBitmap.LockBits(
            new Rectangle(0, 0, uploadBitmap.Width, uploadBitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            glGenTextures(1, out _gpuTerrainTexture);
            glBindTexture(GlTexture2D, _gpuTerrainTexture);
            glTexParameteri(GlTexture2D, GlTextureMinFilter, GlLinearMipmapLinear);
            glTexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
            glTexImage2D(GlTexture2D, 0, GlRgba, uploadBitmap.Width, uploadBitmap.Height, 0, GlBgra, GlUnsignedByte, data.Scan0);
            OpenTK.Graphics.OpenGL4.GL.GenerateMipmap(OpenTK.Graphics.OpenGL4.GenerateMipmapTarget.Texture2D);
            _gpuTerrainTexturePath = _terrainColorBitmapPath;
            _gpuTerrainTextureSize = uploadBitmap.Size;
            return true;
        }
        finally
        {
            uploadBitmap.UnlockBits(data);
        }
    }

    private void DrawGpuTerrainGeometry()
    {
        if (TryDrawTerrainCacheGpuChunks())
        {
            return;
        }

        if (!EnsureGpuTerrainVertexBuffer())
        {
            DrawGpuTerrainMeshImmediate();
            DrawGpuTerrainFacetsImmediate();
            return;
        }

        if (_glBindBuffer is null)
        {
            return;
        }

        if (_gpuTerrainVertexCount > 0)
        {
            DrawGpuVertexBuffer(_gpuTerrainVertexBuffer, _gpuTerrainVertexCount, useNativeLighting: false);
        }
    }

    private bool TryDrawTerrainCacheGpuChunks()
    {
        if (string.IsNullOrWhiteSpace(_terrainCacheGpuSourcePath))
        {
            return false;
        }

        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null)
        {
            return false;
        }

        if (!EnsureTerrainCacheGpuChunksBuilt())
        {
            return false;
        }

        _terrainCacheGpuFrameIndex++;
        _terrainCacheGpuVisibleVertices = 0;
        _terrainCacheGpuDrawCalls = 0;
        _terrainCacheGpuPendingUploads = 0;
        int uploadsThisFrame = 0;
        int uploadVerticesThisFrame = 0;
        IReadOnlyList<TerrainCacheGpuChunk> visibleChunks = ResolveTerrainCacheGpuVisibleChunks();
        foreach (TerrainCacheGpuChunk chunk in visibleChunks)
        {
            if (chunk.VertexCount <= 0)
            {
                continue;
            }

            chunk.LastUsedFrame = _terrainCacheGpuFrameIndex;
            _terrainCacheGpuVisibleVertices += chunk.VertexCount;
            if (!EnsureTerrainCacheGpuChunkUploaded(
                    chunk,
                    ref uploadsThisFrame,
                    ref uploadVerticesThisFrame))
            {
                _terrainCacheGpuPendingUploads++;
                continue;
            }

            DrawGpuIndexedVertexBuffer(chunk.Buffer, chunk.IndexBuffer, chunk.IndexCount);
            _terrainCacheGpuDrawCalls++;
        }

        TrimTerrainCacheGpuResidentBuffers();
        LogTerrainCacheGpuRenderIfDue();
        return true;
    }

    private bool IsTerrainCacheGpuChunkVisible(TerrainCacheGpuChunk chunk)
    {
        if (chunk.VertexCount <= 0)
        {
            return false;
        }

        return IsTerrainCacheGpuBoundsVisible(chunk.MinX, chunk.MinY, chunk.MinZ, chunk.MaxX, chunk.MaxY, chunk.MaxZ);
    }

    private IReadOnlyList<TerrainCacheGpuChunk> ResolveTerrainCacheGpuVisibleChunks()
    {
        if (_terrainCacheGpuChunkTree is not null)
        {
            _terrainCacheGpuChunkTree.QueryVisible(this, _terrainCacheGpuVisibleChunkScratch);
            return _terrainCacheGpuVisibleChunkScratch;
        }

        _terrainCacheGpuVisibleChunkScratch.Clear();
        foreach (TerrainCacheGpuChunk chunk in _terrainCacheGpuChunks)
        {
            if (IsTerrainCacheGpuChunkVisible(chunk))
            {
                _terrainCacheGpuVisibleChunkScratch.Add(chunk);
            }
        }

        return _terrainCacheGpuVisibleChunkScratch;
    }

    private bool IsTerrainCacheGpuBoundsVisible(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        Vector3 boundsCenter = new(
            (minX + maxX) * 0.5f,
            (minY + maxY) * 0.5f,
            (minZ + maxZ) * 0.5f);
        float horizontalRadius = MathF.Max(0.25f, MathF.Sqrt(
            MathF.Pow((maxX - minX) * 0.5f, 2f)
            + MathF.Pow((maxZ - minZ) * 0.5f, 2f)));
        float verticalSpan = MathF.Max(0.20f, maxY - minY);
        if (!IsSceneBoundsPotentiallyVisible(boundsCenter, horizontalRadius, verticalSpan))
        {
            return false;
        }

        Span<Vector3> corners =
        [
            new(minX, minY, minZ),
            new(maxX, minY, minZ),
            new(minX, maxY, minZ),
            new(maxX, maxY, minZ),
            new(minX, minY, maxZ),
            new(maxX, minY, maxZ),
            new(minX, maxY, maxZ),
            new(maxX, maxY, maxZ),
        ];

        bool allLeft = true;
        bool allRight = true;
        bool allBelow = true;
        bool allAbove = true;
        bool allNear = true;
        bool allFar = true;
        foreach (Vector3 corner in corners)
        {
            Vector4 view = Vector4.Transform(new Vector4(corner, 1f), _viewMatrix);
            Vector4 clip = Vector4.Transform(view, _projectionMatrix);
            float w = clip.W;
            allLeft &= clip.X < -w;
            allRight &= clip.X > w;
            allBelow &= clip.Y < -w;
            allAbove &= clip.Y > w;
            allNear &= clip.Z < -w;
            allFar &= clip.Z > w;
        }

        if (_firstPersonView)
        {
            // First-person camera often runs very low and close to the ground.
            // Nearby terrain chunks can straddle the near plane and get rejected
            // as "allNear" even though they should still contribute visible ground.
            return !(allLeft || allRight || allBelow || allAbove || allFar);
        }

        return !(allLeft || allRight || allBelow || allAbove || allNear || allFar);
    }

    private bool EnsureTerrainCacheGpuChunksBuilt()
    {
        if (string.IsNullOrWhiteSpace(_terrainCacheGpuSourcePath) || _terrainCacheGpuBuildFailed)
        {
            return false;
        }

        bool lightingEnabled = _lightingSettings.Enabled;
        if (string.Equals(_terrainCacheGpuLoadedSourcePath, _terrainCacheGpuSourcePath, StringComparison.OrdinalIgnoreCase)
            && _terrainCacheGpuLoadedLightingEnabled == lightingEnabled
            && _terrainCacheGpuChunks.Count > 0)
        {
            return true;
        }

        if (_terrainCacheGpuLoadedLightingEnabled != lightingEnabled)
        {
            ReleaseTerrainCacheGpuChunks(deleteBuffers: true, clearSource: false);
            _terrainCacheGpuLoadedSourcePath = null;
            _terrainCacheGpuLoadedLightingEnabled = lightingEnabled;
        }

        if (_terrainCacheGpuBuildTask is not null
            && !string.Equals(_terrainCacheGpuBuildingSourcePath, _terrainCacheGpuSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            _terrainCacheGpuBuildTask = null;
            _terrainCacheGpuBuildingSourcePath = null;
        }

        if (_terrainCacheGpuBuildTask is null)
        {
            StartTerrainCacheGpuChunkBuild(_terrainCacheGpuSourcePath);
            return false;
        }

        if (!_terrainCacheGpuBuildTask.IsCompleted)
        {
            return false;
        }

        try
        {
            TerrainCacheGpuBuildResult result = _terrainCacheGpuBuildTask.GetAwaiter().GetResult();
            _terrainCacheGpuBuildTask = null;
            _terrainCacheGpuBuildingSourcePath = null;
            if (!string.Equals(result.SourcePath, _terrainCacheGpuSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (result.UseBakedLighting != _lightingSettings.Enabled)
            {
                _terrainCacheGpuLoadedSourcePath = null;
                _terrainCacheGpuLoadedLightingEnabled = _lightingSettings.Enabled;
                return false;
            }

            ReleaseTerrainCacheGpuChunks(deleteBuffers: true, clearSource: false);
            _terrainCacheGpuChunks.AddRange(result.Chunks);
            _terrainCacheGpuChunkTree = TerrainCacheGpuChunkQuadTree.Build(_terrainCacheGpuChunks);
            _terrainCacheGpuColumns = result.Columns;
            _terrainCacheGpuRows = result.Rows;
            _terrainCacheGpuTotalTriangles = result.EmittedTriangles;
            _gpuTerrainVertexCount = result.VertexCount;
            _terrainCacheGpuLoadedSourcePath = result.SourcePath;
            _terrainCacheGpuLoadedLightingEnabled = result.UseBakedLighting;
            _terrainCacheGpuBuildFailed = _terrainCacheGpuChunks.Count == 0;
            AppendGameplayLog(
                "terrain_cache_render.log",
                $"{DateTime.Now:HH:mm:ss.fff} source={Path.GetFileName(result.SourcePath)} lighting={result.UseBakedLighting} original_triangles={result.EmittedTriangles}/{result.TotalTriangles} chunks={result.UsedChunks}/{result.Chunks.Count} chunk_size_m={TerrainCacheGpuChunkSizeM:0.##} vertices={result.VertexCount}");
            return !_terrainCacheGpuBuildFailed;
        }
        catch (Exception exception)
        {
            _terrainCacheGpuBuildTask = null;
            _terrainCacheGpuBuildingSourcePath = null;
            _terrainCacheGpuBuildFailed = true;
            AppendGameplayLog(
                "terrain_cache_render.log",
                $"{DateTime.Now:HH:mm:ss.fff} source={Path.GetFileName(_terrainCacheGpuSourcePath)} gpu_chunk_build_failed={exception.Message}");
            return false;
        }
    }

    private void StartTerrainCacheGpuChunkBuild(string sourcePath)
    {
        var parameters = new TerrainCacheGpuBuildParameters(
            Math.Max(1f, _host.MapPreset.Width),
            Math.Max(1f, _host.MapPreset.Height),
            Math.Max(1f, (float)_host.MapPreset.FieldLengthM),
            Math.Max(1f, (float)_host.MapPreset.FieldWidthM),
            (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit),
            _terrainCacheGpuAnnotationPath,
            TerrainCacheActorComponentFilter.LoadExcludedComponentIds(_host.MapPreset),
            _lightingSettings.Enabled);
        string fullPath = Path.GetFullPath(sourcePath);
        _terrainCacheGpuBuildingSourcePath = fullPath;
        _terrainCacheGpuBuildTask = Task.Run(() =>
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            }
            catch
            {
            }

            return BuildTerrainCacheGpuChunks(fullPath, parameters);
        });
        AppendGameplayLog(
            "terrain_cache_render.log",
            $"{DateTime.Now:HH:mm:ss.fff} source={Path.GetFileName(fullPath)} gpu_chunk_build=background_started");
    }

    private static TerrainCacheGpuBuildResult BuildTerrainCacheGpuChunks(
        string sourcePath,
        TerrainCacheGpuBuildParameters parameters)
    {
        RuntimeReferenceScene runtimeScene = RuntimeReferenceLoader.Load(sourcePath, parameters.AnnotationPath);
        bool initialized = false;
        float sceneWidthM = Math.Max(1f, parameters.FieldLengthM);
        float sceneDepthM = Math.Max(1f, parameters.FieldWidthM);
        int columns = Math.Max(1, (int)MathF.Ceiling(sceneWidthM / Math.Max(0.25f, TerrainCacheGpuChunkSizeM)));
        int rows = Math.Max(1, (int)MathF.Ceiling(sceneDepthM / Math.Max(0.25f, TerrainCacheGpuChunkSizeM)));
        int chunkCount = checked(columns * rows);
        var chunks = new List<TerrainCacheGpuChunk>(chunkCount);
        for (int index = 0; index < chunkCount; index++)
        {
            chunks.Add(new TerrainCacheGpuChunk());
        }

        int totalTriangles = 0;
        int emittedTriangles = 0;
        foreach (RuntimeReferenceChunk sourceChunk in runtimeScene.Chunks)
        {
            initialized = true;
            RuntimeReferenceVertex[] vertices = sourceChunk.Vertices;
            uint[] indices = sourceChunk.Indices;
            RuntimeReferenceComponentRange[] componentRanges = sourceChunk.ComponentRanges;
            if (vertices.Length == 0 || indices.Length < 3)
            {
                continue;
            }

            Vector3[] sceneVertices = new Vector3[vertices.Length];
            for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                sceneVertices[vertexIndex] = ConvertRuntimeReferenceModelToScenePoint(
                    runtimeScene,
                    parameters,
                    vertices[vertexIndex].Position);
            }

            int triangleIndexCount = indices.Length - indices.Length % 3;
            if (componentRanges.Length > 0
                && (parameters.ExcludedComponentIds.Count > 0 || runtimeScene.ComponentColorOverrides.Count > 0))
            {
                foreach (RuntimeReferenceComponentRange range in componentRanges)
                {
                    int rangeStart = Math.Clamp(range.StartIndex, 0, triangleIndexCount);
                    int rangeEnd = Math.Clamp(range.StartIndex + Math.Max(0, range.IndexCount), rangeStart, triangleIndexCount);
                    rangeEnd -= (rangeEnd - rangeStart) % 3;
                    totalTriangles += Math.Max(0, rangeEnd - rangeStart) / 3;
                    if (parameters.ExcludedComponentIds.Contains(range.ComponentId))
                    {
                        continue;
                    }

                    Color? componentOverrideColor = TryResolveRuntimeReferenceComponentColorOverride(runtimeScene, range.ComponentId, out Color overrideColor)
                        ? overrideColor
                        : null;
                    AppendTriangles(rangeStart, rangeEnd, componentOverrideColor, parameters.UseBakedLighting);
                }

                continue;
            }

            totalTriangles += triangleIndexCount / 3;
            AppendTriangles(0, triangleIndexCount, null, parameters.UseBakedLighting);

            void AppendTriangles(int startIndex, int endIndex, Color? componentOverrideColor, bool useBakedLighting)
            {
                for (int triangle = startIndex; triangle < endIndex; triangle += 3)
                {
                    int i0 = checked((int)indices[triangle]);
                    int i1 = checked((int)indices[triangle + 1]);
                    int i2 = checked((int)indices[triangle + 2]);
                    if ((uint)i0 >= (uint)vertices.Length
                        || (uint)i1 >= (uint)vertices.Length
                        || (uint)i2 >= (uint)vertices.Length)
                    {
                        continue;
                    }

                    Vector3 p0 = sceneVertices[i0];
                    Vector3 p1 = sceneVertices[i1];
                    Vector3 p2 = sceneVertices[i2];
                    TerrainCacheGpuChunk targetChunk = ResolveTerrainCacheGpuChunk(chunks, columns, rows, (p0 + p1 + p2) / 3f);
                    Color fill = componentOverrideColor ?? ResolveRuntimeReferenceTriangleColor(
                        vertices[i0].Color,
                        vertices[i1].Color,
                        vertices[i2].Color);
                    fill = ApplyBakedArenaPointLight(fill, p0, p1, p2, sceneWidthM, sceneDepthM, useBakedLighting);
                    targetChunk.AppendTriangle(
                        new GpuVertex(p0, fill),
                        new GpuVertex(p1, fill),
                        new GpuVertex(p2, fill));
                    emittedTriangles++;
                }
            }
        }

        int usedChunks = 0;
        int vertexCount = 0;
        foreach (TerrainCacheGpuChunk chunk in chunks)
        {
            chunk.FinalizeBounds();
            if (chunk.VertexCount <= 0)
            {
                continue;
            }

            usedChunks++;
            vertexCount += chunk.VertexCount;
        }

        if (!initialized || usedChunks == 0)
        {
            throw new InvalidDataException("terrain cache did not contain renderable triangle chunks.");
        }

        return new TerrainCacheGpuBuildResult(
            sourcePath,
            parameters.UseBakedLighting,
            chunks,
            columns,
            rows,
            emittedTriangles,
            totalTriangles,
            vertexCount,
            usedChunks);
    }

    private static Vector3 ConvertRuntimeReferenceModelToScenePoint(
        RuntimeReferenceScene runtimeScene,
        TerrainCacheGpuBuildParameters parameters,
        Vector3 modelPoint)
    {
        double safeSceneScale = Math.Max(parameters.SceneScale, 1e-6f);
        double centeredXMeters = (modelPoint.X - runtimeScene.WorldScale.ModelCenter.X) * runtimeScene.WorldScale.XMetersPerUnit;
        double centeredZMeters = (modelPoint.Z - runtimeScene.WorldScale.ModelCenter.Z) * runtimeScene.WorldScale.ZMetersPerUnit;
        double worldX = (parameters.FieldLengthM * 0.5 - centeredXMeters) / safeSceneScale;
        double worldY = (parameters.FieldWidthM * 0.5 - centeredZMeters) / safeSceneScale;
        double heightM = Math.Max(0.0, (modelPoint.Y - runtimeScene.Bounds.Min.Y) * runtimeScene.WorldScale.YMetersPerUnit);
        return new Vector3(
            (float)(worldX * parameters.SceneScale),
            (float)heightM,
            (float)(worldY * parameters.SceneScale));
    }

    private static Color ResolveRuntimeReferenceTriangleColor(uint color0, uint color1, uint color2)
    {
        static Color Unpack(uint packed)
            => Color.FromArgb(
                (int)((packed >> 24) & 0xFF),
                (int)(packed & 0xFF),
                (int)((packed >> 8) & 0xFF),
                (int)((packed >> 16) & 0xFF));

        Color a = Unpack(color0);
        Color b = Unpack(color1);
        Color c = Unpack(color2);
        return Color.FromArgb(
            (a.A + b.A + c.A) / 3,
            (a.R + b.R + c.R) / 3,
            (a.G + b.G + c.G) / 3,
            (a.B + b.B + c.B) / 3);
    }

    private static bool TryResolveRuntimeReferenceComponentColorOverride(
        RuntimeReferenceScene runtimeScene,
        int componentId,
        out Color color)
    {
        color = default;
        if (!runtimeScene.ComponentColorOverrides.TryGetValue(componentId, out Vector4 value))
        {
            return false;
        }

        color = Color.FromArgb(
            Math.Clamp((int)MathF.Round((value.W <= 0f ? 1f : value.W) * 255f), 0, 255),
            Math.Clamp((int)MathF.Round(value.X * 255f), 0, 255),
            Math.Clamp((int)MathF.Round(value.Y * 255f), 0, 255),
            Math.Clamp((int)MathF.Round(value.Z * 255f), 0, 255));
        return color.A > 0;
    }

    private static TerrainCacheGpuChunk ResolveTerrainCacheGpuChunk(
        List<TerrainCacheGpuChunk> chunks,
        int columns,
        int rows,
        Vector3 center)
    {
        int column = Math.Clamp((int)MathF.Floor(center.X / TerrainCacheGpuChunkSizeM), 0, columns - 1);
        int row = Math.Clamp((int)MathF.Floor(center.Z / TerrainCacheGpuChunkSizeM), 0, rows - 1);
        return chunks[row * columns + column];
    }

    private bool EnsureTerrainCacheGpuChunkUploaded(
        TerrainCacheGpuChunk chunk,
        ref int uploadsThisFrame,
        ref int uploadVerticesThisFrame)
    {
        if (chunk.VertexCount <= 0 || chunk.IndexCount <= 0)
        {
            return false;
        }

        if (chunk.Buffer != 0 && chunk.IndexBuffer != 0 && chunk.Version == _terrainProjectionCacheVersion)
        {
            return true;
        }

        if (uploadsThisFrame >= TerrainCacheGpuMaxUploadsPerFrame
            || (uploadsThisFrame > 0
                && uploadVerticesThisFrame + chunk.VertexCount > TerrainCacheGpuMaxUploadVerticesPerFrame))
        {
            return false;
        }

        if (_glGenBuffers is null)
        {
            return false;
        }

        if (chunk.Buffer == 0)
        {
            _glGenBuffers(1, out chunk.Buffer);
        }
        else
        {
            _terrainCacheGpuResidentVertices = Math.Max(0, _terrainCacheGpuResidentVertices - chunk.VertexCount);
        }

        if (chunk.IndexBuffer == 0)
        {
            _glGenBuffers(1, out chunk.IndexBuffer);
        }

        UploadGpuVertexBuffer(chunk.Buffer, chunk.Vertices, GlStaticDraw);
        UploadGpuIndexBuffer(chunk.IndexBuffer, chunk.Indices, GlStaticDraw);
        chunk.Version = _terrainProjectionCacheVersion;
        _terrainCacheGpuResidentVertices += chunk.VertexCount;
        uploadsThisFrame++;
        uploadVerticesThisFrame += chunk.VertexCount;
        return true;
    }

    private void WarmTerrainCacheGpuAssets()
    {
        if (!UseGpuRenderer
            || UseFastFlatRenderer
            || string.IsNullOrWhiteSpace(_terrainCacheGpuSourcePath))
        {
            return;
        }

        if (!EnsureGpuContext()
            || !_gpuBufferApiReady
            || _glGenBuffers is null
            || _glBindBuffer is null
            || _glBufferData is null)
        {
            return;
        }

        if (!MakeGpuContextCurrent())
        {
            return;
        }

        if (!EnsureTerrainCacheGpuChunksBuilt())
        {
            return;
        }

        int uploadsThisFrame = 0;
        int uploadVerticesThisFrame = 0;
        IReadOnlyList<TerrainCacheGpuChunk> warmChunks = ResolveTerrainCacheGpuVisibleChunks();
        foreach (TerrainCacheGpuChunk chunk in warmChunks)
        {
            if (chunk.VertexCount <= 0
                || (chunk.Buffer != 0 && chunk.IndexBuffer != 0 && chunk.Version == _terrainProjectionCacheVersion))
            {
                continue;
            }

            if (!EnsureTerrainCacheGpuChunkUploaded(
                    chunk,
                    ref uploadsThisFrame,
                    ref uploadVerticesThisFrame))
            {
                break;
            }

            chunk.LastUsedFrame = _terrainCacheGpuFrameIndex;
        }

        if (uploadsThisFrame > 0)
        {
            TrimTerrainCacheGpuResidentBuffers();
            int pendingUploads = 0;
            foreach (TerrainCacheGpuChunk chunk in _terrainCacheGpuChunks)
            {
                if (chunk.VertexCount > 0
                    && (chunk.Buffer == 0 || chunk.IndexBuffer == 0 || chunk.Version != _terrainProjectionCacheVersion))
                {
                    pendingUploads++;
                }
            }
            AppendGameplayLog(
                "terrain_cache_render.log",
                $"{DateTime.Now:HH:mm:ss.fff} gpu_prewarm uploaded_chunks={uploadsThisFrame} resident_vertices={_terrainCacheGpuResidentVertices} pending_upload_chunks={pendingUploads} total_triangles={_terrainCacheGpuTotalTriangles}");
        }
    }

    private bool IsActiveMapTerrainFullyLoaded()
    {
        bool fineTerrainReady = AreFineTerrainVisualScenesReady();
        if (string.IsNullOrWhiteSpace(_terrainCacheGpuSourcePath))
        {
            return fineTerrainReady;
        }

        if (!UseGpuRenderer || UseFastFlatRenderer)
        {
            return (_cachedRuntimeGrid is not null && ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid))
                && fineTerrainReady;
        }

        if (!EnsureTerrainCacheGpuChunksBuilt() || _terrainCacheGpuChunks.Count == 0)
        {
            return false;
        }

        IReadOnlyList<TerrainCacheGpuChunk> visibleChunks = ResolveTerrainCacheGpuVisibleChunks();
        foreach (TerrainCacheGpuChunk chunk in visibleChunks)
        {
            if (chunk.VertexCount > 0
                && (chunk.Buffer == 0 || chunk.IndexBuffer == 0 || chunk.Version != _terrainProjectionCacheVersion))
            {
                return false;
            }
        }

        return fineTerrainReady;
    }

    private double ResolveActiveMapTerrainLoadProgress()
    {
        double fineTerrainProgress = ResolveFineTerrainVisualSceneLoadProgress();
        if (string.IsNullOrWhiteSpace(_terrainCacheGpuSourcePath))
        {
            return fineTerrainProgress;
        }

        if (!UseGpuRenderer || UseFastFlatRenderer)
        {
            double terrainProgress = _cachedRuntimeGrid is not null && ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid)
                ? 1.0
                : 0.65;
            return Math.Clamp(terrainProgress * 0.85 + fineTerrainProgress * 0.15, 0.0, 1.0);
        }

        if (!EnsureTerrainCacheGpuChunksBuilt() || _terrainCacheGpuChunks.Count == 0)
        {
            double terrainProgress = _terrainCacheGpuBuildTask is not null
                ? (_terrainCacheGpuBuildTask.IsCompleted ? 0.45 : 0.18)
                : 0.08;
            return Math.Clamp(terrainProgress * 0.85 + fineTerrainProgress * 0.15, 0.0, 1.0);
        }

        IReadOnlyList<TerrainCacheGpuChunk> visibleChunks = ResolveTerrainCacheGpuVisibleChunks();
        long totalVertices = 0;
        long residentVertices = 0;
        foreach (TerrainCacheGpuChunk chunk in visibleChunks)
        {
            if (chunk.VertexCount <= 0)
            {
                continue;
            }

            totalVertices += chunk.VertexCount;
            if (chunk.Buffer != 0 && chunk.IndexBuffer != 0 && chunk.Version == _terrainProjectionCacheVersion)
            {
                residentVertices += chunk.VertexCount;
            }
        }

        if (totalVertices <= 0)
        {
            return fineTerrainProgress;
        }

        double terrainResidentProgress = Math.Clamp(residentVertices / (double)totalVertices, 0.0, 1.0);
        return Math.Clamp(terrainResidentProgress * 0.85 + fineTerrainProgress * 0.15, 0.0, 1.0);
    }

    private void InvalidateGpuOverlayLayer()
    {
        _lastGpuOverlayUploadTicks = 0;
        _lastGpuOverlaySceneUploadTicks = 0;
        _lastGpuOverlayUiUploadTicks = 0;
        _gpuOverlaySceneDirty = true;
        _gpuOverlayUiDirty = true;
    }

    private void TrimTerrainCacheGpuResidentBuffers()
    {
        if (_terrainCacheGpuResidentVertices <= TerrainCacheGpuMaxResidentVertices)
        {
            return;
        }

        foreach (TerrainCacheGpuChunk chunk in _terrainCacheGpuChunks)
        {
            if (_terrainCacheGpuResidentVertices <= TerrainCacheGpuMaxResidentVertices)
            {
                return;
            }

            if (chunk.Buffer == 0 || chunk.LastUsedFrame == _terrainCacheGpuFrameIndex)
            {
                continue;
            }

            DeleteGpuBuffer(ref chunk.Buffer);
            DeleteGpuBuffer(ref chunk.IndexBuffer);
            chunk.Version = -1;
            _terrainCacheGpuResidentVertices = Math.Max(0, _terrainCacheGpuResidentVertices - chunk.VertexCount);
        }
    }

    private void LogTerrainCacheGpuRenderIfDue()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        if (_lastTerrainCacheGpuLogTicks > 0
            && (nowTicks - _lastTerrainCacheGpuLogTicks) / (double)Stopwatch.Frequency < 2.0)
        {
            return;
        }

        _lastTerrainCacheGpuLogTicks = nowTicks;
        AppendGameplayLog(
            "terrain_cache_render.log",
            $"{DateTime.Now:HH:mm:ss.fff} gpu_stream visible_vertices={_terrainCacheGpuVisibleVertices} resident_vertices={_terrainCacheGpuResidentVertices} draw_calls={_terrainCacheGpuDrawCalls} pending_upload_chunks={_terrainCacheGpuPendingUploads} total_triangles={_terrainCacheGpuTotalTriangles}");
    }

    private bool EnsureGpuTerrainVertexBuffer()
    {
        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null)
        {
            return false;
        }

        if (!ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid))
        {
            RebuildTerrainTileCache();
        }

        if (_gpuTerrainVertexBuffer != 0 && _gpuTerrainBufferVersion == _terrainProjectionCacheVersion)
        {
            return true;
        }

        _gpuTerrainVertexBuildBuffer.Clear();
        AppendGpuTerrainFaces(_terrainFaces, _gpuTerrainVertexBuildBuffer);
        AppendGpuTerrainFacets(_gpuTerrainVertexBuildBuffer);
        _gpuTerrainVertexCount = _gpuTerrainVertexBuildBuffer.Count;
        if (_gpuTerrainVertexBuffer == 0)
        {
            _glGenBuffers(1, out _gpuTerrainVertexBuffer);
        }

        if (_gpuTerrainVertexCount > 0)
        {
            UploadGpuVertexBuffer(_gpuTerrainVertexBuffer, _gpuTerrainVertexBuildBuffer, GlStaticDraw);
        }

        _gpuTerrainBufferVersion = _terrainProjectionCacheVersion;
        return true;
    }

    private bool EnsureGpuTerrainChunkBuildBuffers()
    {
        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null)
        {
            return false;
        }

        if (!ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid))
        {
            RebuildTerrainTileCache();
        }

        EnsureGpuTerrainChunkList();
        if (_gpuTerrainChunks.Count == _gpuTerrainChunkColumns * _gpuTerrainChunkRows
            && _gpuTerrainBufferVersion == _terrainProjectionCacheVersion)
        {
            return true;
        }

        foreach (GpuTerrainChunk chunk in _gpuTerrainChunks)
        {
            chunk.BuildBuffer.Clear();
            chunk.VertexCount = 0;
            chunk.Version = -1;
        }

        AppendGpuTerrainFacesToChunks(_terrainFaces);
        AppendGpuTerrainFacetsToChunks();

        _gpuTerrainVertexCount = 0;
        foreach (GpuTerrainChunk chunk in _gpuTerrainChunks)
        {
            chunk.VertexCount = chunk.BuildBuffer.Count;
            _gpuTerrainVertexCount += chunk.VertexCount;
            if (chunk.VertexCount <= 0)
            {
                continue;
            }
        }

        _gpuTerrainBufferVersion = _terrainProjectionCacheVersion;
        return true;
    }

    private bool EnsureGpuTerrainChunkUploaded(GpuTerrainChunk chunk)
    {
        if (chunk.VertexCount <= 0)
        {
            return false;
        }

        if (chunk.Buffer != 0 && chunk.Version == _terrainProjectionCacheVersion)
        {
            return true;
        }

        if (_glGenBuffers is null)
        {
            return false;
        }

        if (chunk.Buffer == 0)
        {
            _glGenBuffers(1, out chunk.Buffer);
        }

        UploadGpuVertexBuffer(chunk.Buffer, chunk.BuildBuffer, GlStaticDraw);
        chunk.Version = _terrainProjectionCacheVersion;
        return true;
    }

    private GpuTerrainChunkWindow ResolveGpuTerrainChunkWindow()
    {
        Vector3 focus = _cameraTargetM;
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is not null)
        {
            focus = ToScenePoint(selected.X, selected.Y, (float)Math.Max(0.0, selected.GroundHeightM + selected.AirborneHeightM));
        }
        else if (_firstPersonView)
        {
            focus = _cameraPositionM;
        }

        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        int column = Math.Clamp((int)MathF.Floor(focus.X / Math.Max(1e-4f, GpuTerrainChunkSizeM)), 0, _gpuTerrainChunkColumns - 1);
        int row = Math.Clamp((int)MathF.Floor(focus.Z / Math.Max(1e-4f, GpuTerrainChunkSizeM)), 0, _gpuTerrainChunkRows - 1);
        return new GpuTerrainChunkWindow(column, row);
    }

    private void EnsureGpuTerrainChunkList()
    {
        int columns = ResolveGpuTerrainChunkColumnCount();
        int rows = ResolveGpuTerrainChunkRowCount();
        int targetCount = columns * rows;
        if (_gpuTerrainChunkColumns != columns || _gpuTerrainChunkRows != rows)
        {
            foreach (GpuTerrainChunk chunk in _gpuTerrainChunks)
            {
                DeleteGpuBuffer(ref chunk.Buffer);
                chunk.VertexCount = 0;
                chunk.Version = -1;
                chunk.BuildBuffer.Clear();
            }

            _gpuTerrainChunks.Clear();
            _gpuTerrainChunkColumns = columns;
            _gpuTerrainChunkRows = rows;
            _gpuTerrainBufferVersion = -1;
        }

        while (_gpuTerrainChunks.Count < targetCount)
        {
            _gpuTerrainChunks.Add(new GpuTerrainChunk());
        }
    }

    private int ResolveGpuTerrainChunkColumnCount()
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        float widthM = Math.Max(scale, _host.MapPreset.Width * scale);
        return Math.Max(1, (int)MathF.Ceiling(widthM / GpuTerrainChunkSizeM));
    }

    private int ResolveGpuTerrainChunkRowCount()
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        float heightM = Math.Max(scale, _host.MapPreset.Height * scale);
        return Math.Max(1, (int)MathF.Ceiling(heightM / GpuTerrainChunkSizeM));
    }

    private void AppendGpuTerrainFacesToChunks(IReadOnlyList<TerrainFacePatch> faces)
    {
        foreach (TerrainFacePatch face in faces)
        {
            AppendGpuTerrainFaceToTarget(face, ResolveGpuTerrainChunk(face.CenterScene).BuildBuffer);
        }
    }

    private void AppendGpuTerrainFaceToTarget(TerrainFacePatch face, List<GpuVertex> target)
    {
        if (IsGpuSmoothFlatTerrainFace(face))
        {
            return;
        }

        if (face.Vertices.Length == 3)
        {
            AppendGpuTerrainTriangle(target, face.Vertices[0], face.Vertices[1], face.Vertices[2], face.FillColor, _lightingSettings.Enabled);
        }
        else if (face.Vertices.Length == 4)
        {
            AppendGpuTerrainQuad(target, face.Vertices[0], face.Vertices[1], face.Vertices[2], face.Vertices[3], face.FillColor, _lightingSettings.Enabled);
        }
        else if (face.Vertices.Length > 4)
        {
            for (int index = 1; index < face.Vertices.Length - 1; index++)
            {
                AppendGpuTerrainTriangle(target, face.Vertices[0], face.Vertices[index], face.Vertices[index + 1], face.FillColor, _lightingSettings.Enabled);
            }
        }
    }

    private static bool IsGpuSmoothFlatTerrainFace(TerrainFacePatch face)
    {
        if (face.Vertices.Length < 3)
        {
            return true;
        }

        float minY = float.MaxValue;
        float maxY = float.MinValue;
        foreach (Vector3 vertex in face.Vertices)
        {
            minY = Math.Min(minY, vertex.Y);
            maxY = Math.Max(maxY, vertex.Y);
        }

        return maxY <= GpuTerrainSmoothFlatSkipHeightM
            && maxY - minY <= GpuTerrainSmoothFlatSkipHeightM;
    }

    private GpuTerrainChunk ResolveGpuTerrainChunk(Vector3 centerScene)
    {
        EnsureGpuTerrainChunkList();
        int column = Math.Clamp((int)MathF.Floor(centerScene.X / Math.Max(1e-4f, GpuTerrainChunkSizeM)), 0, _gpuTerrainChunkColumns - 1);
        int row = Math.Clamp((int)MathF.Floor(centerScene.Z / Math.Max(1e-4f, GpuTerrainChunkSizeM)), 0, _gpuTerrainChunkRows - 1);
        return _gpuTerrainChunks[row * _gpuTerrainChunkColumns + column];
    }

    private void AppendGpuTerrainFaces(IReadOnlyList<TerrainFacePatch> faces, List<GpuVertex> target)
    {
        foreach (TerrainFacePatch face in faces)
        {
            if (face.Vertices.Length == 3)
            {
                AppendGpuTerrainTriangle(target, face.Vertices[0], face.Vertices[1], face.Vertices[2], face.FillColor, _lightingSettings.Enabled);
            }
            else if (face.Vertices.Length == 4)
            {
                AppendGpuTerrainQuad(target, face.Vertices[0], face.Vertices[1], face.Vertices[2], face.Vertices[3], face.FillColor, _lightingSettings.Enabled);
            }
            else if (face.Vertices.Length > 4)
            {
                for (int index = 1; index < face.Vertices.Length - 1; index++)
                {
                    AppendGpuTerrainTriangle(target, face.Vertices[0], face.Vertices[index], face.Vertices[index + 1], face.FillColor, _lightingSettings.Enabled);
                }
            }
        }
    }

    private void AppendGpuTerrainFacets(List<GpuVertex> target)
    {
        RuntimeGridData? runtimeGrid = _host.RuntimeGrid;
        if (runtimeGrid is null)
        {
            return;
        }

        foreach (TerrainFacetRuntime facet in runtimeGrid.Facets)
        {
            if (facet.PointsWorld.Count < 3 || facet.HeightsM.Count < 3)
            {
                continue;
            }

            Color topColor = ResolveTerrainFacetTopColor(facet);
            Vector2 anchor = facet.PointsWorld[0];
            float anchorHeight = facet.HeightsM[0];
            Vector3 a = ToScenePoint(anchor.X, anchor.Y, anchorHeight);
            for (int index = 1; index < facet.PointsWorld.Count - 1; index++)
            {
                Vector2 b = facet.PointsWorld[index];
                Vector2 c = facet.PointsWorld[index + 1];
                float hb = index < facet.HeightsM.Count ? facet.HeightsM[index] : facet.HeightsM[^1];
                float hc = index + 1 < facet.HeightsM.Count ? facet.HeightsM[index + 1] : facet.HeightsM[^1];
                AppendGpuTerrainTriangle(target, a, ToScenePoint(b.X, b.Y, hb), ToScenePoint(c.X, c.Y, hc), topColor, _lightingSettings.Enabled);
            }
        }
    }

    private void AppendGpuTerrainFacetsToChunks()
    {
        RuntimeGridData? runtimeGrid = _host.RuntimeGrid;
        if (runtimeGrid is null)
        {
            return;
        }

        foreach (TerrainFacetRuntime facet in runtimeGrid.Facets)
        {
            if (facet.PointsWorld.Count < 3 || facet.HeightsM.Count < 3)
            {
                continue;
            }

            Color topColor = ResolveTerrainFacetTopColor(facet);
            Vector2 anchor = facet.PointsWorld[0];
            float anchorHeight = facet.HeightsM[0];
            Vector3 a = ToScenePoint(anchor.X, anchor.Y, anchorHeight);
            for (int index = 1; index < facet.PointsWorld.Count - 1; index++)
            {
                Vector2 b = facet.PointsWorld[index];
                Vector2 c = facet.PointsWorld[index + 1];
                float hb = index < facet.HeightsM.Count ? facet.HeightsM[index] : facet.HeightsM[^1];
                float hc = index + 1 < facet.HeightsM.Count ? facet.HeightsM[index + 1] : facet.HeightsM[^1];
                Vector3 vb = ToScenePoint(b.X, b.Y, hb);
                Vector3 vc = ToScenePoint(c.X, c.Y, hc);
                Vector3 center = (a + vb + vc) / 3f;
                AppendGpuTerrainTriangle(ResolveGpuTerrainChunk(center).BuildBuffer, a, vb, vc, topColor, _lightingSettings.Enabled);
            }
        }
    }

    private void DrawGpuTerrainFacetsImmediate()
    {
        RuntimeGridData? runtimeGrid = _host.RuntimeGrid;
        if (runtimeGrid is null)
        {
            return;
        }

        foreach (TerrainFacetRuntime facet in runtimeGrid.Facets)
        {
            if (facet.PointsWorld.Count < 3 || facet.HeightsM.Count < 3)
            {
                continue;
            }

            Color topColor = ResolveTerrainFacetTopColor(facet);
            Vector2 anchor = facet.PointsWorld[0];
            float anchorHeight = facet.HeightsM[0];
            for (int index = 1; index < facet.PointsWorld.Count - 1; index++)
            {
                Vector2 b = facet.PointsWorld[index];
                Vector2 c = facet.PointsWorld[index + 1];
                float hb = index < facet.HeightsM.Count ? facet.HeightsM[index] : facet.HeightsM[^1];
                float hc = index + 1 < facet.HeightsM.Count ? facet.HeightsM[index + 1] : facet.HeightsM[^1];
                DrawGpuTriangle(ToScenePoint(anchor.X, anchor.Y, anchorHeight), ToScenePoint(b.X, b.Y, hb), ToScenePoint(c.X, c.Y, hc), topColor);
            }
        }
    }

    private void DrawGpuTerrainMeshImmediate()
    {
        if (!ReferenceEquals(_cachedRuntimeGrid, _host.RuntimeGrid))
        {
            RebuildTerrainTileCache();
        }

        if (_terrainFaces.Count == 0)
        {
            return;
        }

        foreach (TerrainFacePatch face in _terrainFaces)
        {
            if (!IsSceneBoundsPotentiallyVisible(face.CenterScene, Math.Max(face.MaxXWorld - face.MinXWorld, face.MaxYWorld - face.MinYWorld) * 0.5, 1.2))
            {
                continue;
            }

            if (face.Vertices.Length == 3)
            {
                DrawGpuTriangle(face.Vertices[0], face.Vertices[1], face.Vertices[2], face.FillColor);
            }
            else if (face.Vertices.Length == 4)
            {
                DrawGpuQuad(face.Vertices[0], face.Vertices[1], face.Vertices[2], face.Vertices[3], face.FillColor);
            }
            else if (face.Vertices.Length > 4)
            {
                for (int index = 1; index < face.Vertices.Length - 1; index++)
                {
                    DrawGpuTriangle(face.Vertices[0], face.Vertices[index], face.Vertices[index + 1], face.FillColor);
                }
            }
        }
    }

    private void DrawGpuFacilities()
    {
        bool energyMechanismDrawn = false;
        foreach (FacilityRegion region in _host.MapPreset.Facilities)
        {
            if (!ShouldRenderFacility(region))
            {
                continue;
            }

            bool energyMechanism = string.Equals(region.Type, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
            bool dogHole = string.Equals(region.Type, "dog_hole", StringComparison.OrdinalIgnoreCase);
            if (ShouldHideTemporaryArenaMechanismModels() && IsTemporaryArenaMechanismFacility(region))
            {
                continue;
            }

            if (ShouldSuppressLegacyBaseOutpostFacility(region))
            {
                continue;
            }

            if (!_showDebugSidebars && !energyMechanism && !dogHole)
            {
                continue;
            }

            Color color = region.Type switch
            {
                "base" or "outpost" => Color.FromArgb(150, 86, 94, 102),
                "energy_mechanism" => Color.FromArgb(180, ResolveTeamColor(region.Team)),
                "supply" or "buff_supply" => Color.FromArgb(150, 88, 204, 142),
                "wall" => Color.FromArgb(190, 104, 110, 118),
                _ => Color.FromArgb(110, 120, 170, 150),
            };

            if (energyMechanism)
            {
                if (energyMechanismDrawn)
                {
                    continue;
                }

                energyMechanismDrawn = true;
                if (TryResolveEnergyMechanismRenderCenter(out FacilityRegion representative, out double energyCenterX, out double energyCenterY))
                {
                    if (!TryDrawGpuFineTerrainEnergyMechanism(representative, color, energyCenterX, energyCenterY))
                    {
                        if (!ShouldSuppressCoarseEnergyMechanismFallback())
                        {
                            DrawGpuEnergyMechanismModel(representative, color, energyCenterX, energyCenterY);
                        }
                    }
                }
                else
                {
                    (double fallbackEnergyCenterX, double fallbackEnergyCenterY) = ResolveFacilityRegionCenter(region);
                    if (!TryDrawGpuFineTerrainEnergyMechanism(region, color, fallbackEnergyCenterX, fallbackEnergyCenterY))
                    {
                        if (!ShouldSuppressCoarseEnergyMechanismFallback())
                        {
                            DrawGpuEnergyMechanismModel(region, color);
                        }
                    }
                }

                continue;
            }

            if (dogHole)
            {
                DrawGpuDogHoleModel(region);
                continue;
            }

            DrawGpuFacility(region, color);
        }

        TryDrawGpuFineTerrainOutposts();
        TryDrawGpuFineTerrainBases();
        TryDrawGpuFineTerrainCollisionShapes();
    }

    private void DrawGpuTeamTopNeonLights()
    {
        // Map-authored top light strips now keep their original material colors.
    }

    private void DrawGpuSkyLightRays()
    {
        if (_host.World is null
            || _host.MapPreset.Width <= 0
            || _host.MapPreset.Height <= 0)
        {
            return;
        }

        DrawGpuSkyLightRayCross(0.33f, 0.46f, 0.22f, 0.62f, Color.FromArgb(44, 170, 226, 255));
        DrawGpuSkyLightRayCross(0.67f, 0.54f, 0.18f, 0.55f, Color.FromArgb(36, 154, 212, 255));
        DrawGpuSkyLightRayCross(0.50f, 0.50f, 0.12f, 0.42f, Color.FromArgb(22, 198, 236, 255));
    }

    private void DrawGpuSkyLightRayCross(float normalizedX, float normalizedY, float topWidthM, float baseWidthM, Color color)
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        double mapWidth = Math.Max(0.1, _host.MapPreset.Width);
        double mapHeight = Math.Max(0.1, _host.MapPreset.Height);
        double centerX = mapWidth * normalizedX;
        double centerY = mapHeight * normalizedY;
        double topHalfWorld = Math.Max(0.02, topWidthM * 0.5 / scale);
        double baseHalfWorld = Math.Max(topHalfWorld * 2.5, baseWidthM * 0.5 / scale);
        float topHeightM = 5.2f;
        float bottomHeightM = 0.05f;

        AppendOrDrawGpuQuad(
            ToScenePoint(centerX - topHalfWorld, centerY, topHeightM),
            ToScenePoint(centerX + topHalfWorld, centerY, topHeightM),
            ToScenePoint(centerX + baseHalfWorld, centerY, bottomHeightM),
            ToScenePoint(centerX - baseHalfWorld, centerY, bottomHeightM),
            color);
        AppendOrDrawGpuQuad(
            ToScenePoint(centerX, centerY - topHalfWorld, topHeightM),
            ToScenePoint(centerX, centerY + topHalfWorld, topHeightM),
            ToScenePoint(centerX, centerY + baseHalfWorld, bottomHeightM),
            ToScenePoint(centerX, centerY - baseHalfWorld, bottomHeightM),
            Color.FromArgb(Math.Max(12, color.A - 8), color));
    }

    private void DrawGpuTeamTopNeonBand(string team, bool farSide)
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        double mapWidth = Math.Max(0.1, _host.MapPreset.Width);
        double mapHeight = Math.Max(0.1, _host.MapPreset.Height);
        double marginWorld = Math.Min(mapWidth * 0.08, 0.55 / scale);
        double x0 = Math.Clamp(marginWorld, 0.0, mapWidth);
        double x1 = Math.Clamp(mapWidth - marginWorld, x0, mapWidth);
        Color teamColor = ResolveTeamColor(team);
        float[] depthsM = { 0.66f, 0.34f, 0.105f };
        int[] alphas = { 34, 72, 218 };
        for (int index = 0; index < depthsM.Length; index++)
        {
            double depthWorld = depthsM[index] / scale;
            double insetWorld = 0.045 / scale;
            double y0;
            double y1;
            if (farSide)
            {
                y1 = Math.Clamp(mapHeight - insetWorld, 0.0, mapHeight);
                y0 = Math.Clamp(y1 - depthWorld, 0.0, y1);
            }
            else
            {
                y0 = Math.Clamp(insetWorld, 0.0, mapHeight);
                y1 = Math.Clamp(y0 + depthWorld, y0, mapHeight);
            }

            Color glow = Color.FromArgb(alphas[index], teamColor);
            float heightM = 0.045f + index * 0.007f;
            AppendOrDrawGpuQuad(
                ToScenePoint(x0, y0, heightM),
                ToScenePoint(x1, y0, heightM),
                ToScenePoint(x1, y1, heightM),
                ToScenePoint(x0, y1, heightM),
                glow);
        }
    }

    private void DrawGpuDogHoleModel(FacilityRegion region)
    {
        ResolveDogHoleFrameGeometry(
            region,
            out Vector3 center,
            out Vector3 forward,
            out Vector3 right,
            out Vector3 up,
            out float openingWidth,
            out float openingHeight,
            out float depth,
            out float frameThickness,
            out float topBeamThickness);

        float pillarHeight = openingHeight + topBeamThickness;
        float halfSpan = openingWidth * 0.5f + frameThickness * 0.5f;
        Color fillColor = Color.FromArgb(242, 74, 79, 86);
        Color edgeColor = Color.FromArgb(246, 40, 44, 49);

        DrawGpuOrientedBox(
            center - right * halfSpan + up * (pillarHeight * 0.5f),
            forward,
            right,
            up,
            depth,
            frameThickness,
            pillarHeight,
            fillColor,
            edgeColor);
        DrawGpuOrientedBox(
            center + right * halfSpan + up * (pillarHeight * 0.5f),
            forward,
            right,
            up,
            depth,
            frameThickness,
            pillarHeight,
            fillColor,
            edgeColor);
        DrawGpuOrientedBox(
            center + up * (openingHeight + topBeamThickness * 0.5f),
            forward,
            right,
            up,
            depth,
            openingWidth + frameThickness * 2f,
            topBeamThickness,
            fillColor,
            edgeColor);
    }

    private void DrawGpuFacility(FacilityRegion region, Color color)
    {
        if (string.Equals(region.Type, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            DrawGpuEnergyMechanismModel(region, color);
            return;
        }

        ResolveFacilityVisualVerticalRange(region, out float bottomM, out float topM);
        IReadOnlyList<Point2D> footprint = BuildGpuFacilityVolumeFootprint(region);
        if (footprint.Count < 3)
        {
            return;
        }

        var bottom = new Vector3[footprint.Count];
        var top = new Vector3[footprint.Count];
        for (int index = 0; index < footprint.Count; index++)
        {
            bottom[index] = ToScenePoint(footprint[index].X, footprint[index].Y, bottomM);
            top[index] = ToScenePoint(footprint[index].X, footprint[index].Y, topM);
        }

        DrawGpuGeneralPrism(bottom, top, color);
        Color edge = Color.FromArgb(Math.Min(255, color.A + 96), BlendColor(color, Color.White, 0.35f));
        for (int index = 0; index < footprint.Count; index++)
        {
            int next = (index + 1) % footprint.Count;
            DrawGpuLine(bottom[index], bottom[next], edge);
            DrawGpuLine(top[index], top[next], edge);
            DrawGpuLine(bottom[index], top[index], edge);
        }
    }

    private static void ResolveFacilityVisualVerticalRange(FacilityRegion region, out float bottomM, out float topM)
    {
        double height = Math.Max(
            0.02,
            ReadFacilityAdditionalDouble(region, "size_z_m", ReadFacilityAdditionalDouble(region, "height_m", Math.Max(0.05, region.HeightM))));
        double centerZ = ReadFacilityAdditionalDouble(region, "center_z_m", ReadFacilityAdditionalDouble(region, "z_m", height * 0.5));
        double bottom = ReadFacilityAdditionalDouble(region, "bottom_m", centerZ - height * 0.5);
        double top = ReadFacilityAdditionalDouble(region, "top_m", centerZ + height * 0.5);
        if (top < bottom)
        {
            (bottom, top) = (top, bottom);
        }

        bottomM = (float)Math.Clamp(bottom, -4.0, 20.0);
        topM = (float)Math.Clamp(top, bottom + 0.02, 20.0);
    }

    private static IReadOnlyList<Point2D> BuildGpuFacilityVolumeFootprint(FacilityRegion region)
    {
        string volumeShape = ReadFacilityAdditionalString(region, "volume_shape", region.Shape);
        bool hasVolumeFootprint =
            region.AdditionalProperties is not null
            && (region.AdditionalProperties.ContainsKey("center_x")
                || region.AdditionalProperties.ContainsKey("center_y")
                || region.AdditionalProperties.ContainsKey("size_x")
                || region.AdditionalProperties.ContainsKey("size_y")
                || region.AdditionalProperties.ContainsKey("radius"));

        if (hasVolumeFootprint)
        {
            double centerX = ReadFacilityAdditionalDouble(region, "center_x", (region.X1 + region.X2) * 0.5);
            double centerY = ReadFacilityAdditionalDouble(region, "center_y", (region.Y1 + region.Y2) * 0.5);
            double yawDeg = ReadFacilityAdditionalDouble(region, "yaw_deg", ReadFacilityAdditionalDouble(region, "yaw", 0.0));
            double yawRad = yawDeg * Math.PI / 180.0;
            double cos = Math.Cos(yawRad);
            double sin = Math.Sin(yawRad);

            if (volumeShape.Contains("cylinder", StringComparison.OrdinalIgnoreCase)
                || volumeShape.Contains("circle", StringComparison.OrdinalIgnoreCase))
            {
                double radius = Math.Max(
                    0.01,
                    ReadFacilityAdditionalDouble(region, "radius", Math.Max(Math.Abs(region.X2 - region.X1), Math.Abs(region.Y2 - region.Y1)) * 0.5));
                var points = new Point2D[28];
                for (int index = 0; index < points.Length; index++)
                {
                    double angle = Math.Tau * index / points.Length;
                    points[index] = new Point2D(centerX + Math.Cos(angle) * radius, centerY + Math.Sin(angle) * radius);
                }

                return points;
            }

            double halfX = Math.Max(0.01, ReadFacilityAdditionalDouble(region, "size_x", Math.Abs(region.X2 - region.X1))) * 0.5;
            double halfY = Math.Max(0.01, ReadFacilityAdditionalDouble(region, "size_y", Math.Abs(region.Y2 - region.Y1))) * 0.5;
            Point2D[] local =
            {
                new(-halfX, -halfY),
                new(halfX, -halfY),
                new(halfX, halfY),
                new(-halfX, halfY),
            };
            return local
                .Select(point => new Point2D(
                    centerX + point.X * cos - point.Y * sin,
                    centerY + point.X * sin + point.Y * cos))
                .ToArray();
        }

        if (string.Equals(region.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && region.Points.Count >= 3)
        {
            return region.Points;
        }

        if (string.Equals(region.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            Vector2 start = new((float)region.X1, (float)region.Y1);
            Vector2 end = new((float)region.X2, (float)region.Y2);
            Vector2 direction = end - start;
            if (direction.LengthSquared() <= 1e-4f)
            {
                double radius = Math.Max(region.Thickness * 0.5, 4.0);
                return new[]
                {
                    new Point2D(region.X1 - radius, region.Y1 - radius),
                    new Point2D(region.X1 + radius, region.Y1 - radius),
                    new Point2D(region.X1 + radius, region.Y1 + radius),
                    new Point2D(region.X1 - radius, region.Y1 + radius),
                };
            }

            direction = Vector2.Normalize(direction);
            Vector2 normal = new(-direction.Y, direction.X);
            float half = (float)Math.Max(region.Thickness * 0.5, 2.0);
            return new[]
            {
                new Point2D(start.X + normal.X * half, start.Y + normal.Y * half),
                new Point2D(start.X - normal.X * half, start.Y - normal.Y * half),
                new Point2D(end.X - normal.X * half, end.Y - normal.Y * half),
                new Point2D(end.X + normal.X * half, end.Y + normal.Y * half),
            };
        }

        double minX = Math.Min(region.X1, region.X2);
        double maxX = Math.Max(region.X1, region.X2);
        double minY = Math.Min(region.Y1, region.Y2);
        double maxY = Math.Max(region.Y1, region.Y2);
        return new[]
        {
            new Point2D(minX, minY),
            new Point2D(maxX, minY),
            new Point2D(maxX, maxY),
            new Point2D(minX, maxY),
        };
    }

    private static string ReadFacilityAdditionalString(FacilityRegion region, string key, string fallback)
    {
        if (region.AdditionalProperties is null
            || !region.AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return fallback;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : element.ToString();
    }

    private static double ReadFacilityAdditionalDouble(FacilityRegion region, string key, double fallback)
    {
        if (region.AdditionalProperties is null
            || !region.AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double numeric))
        {
            return numeric;
        }

        return element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : fallback;
    }

    private void DrawGpuFieldCompositeInteractionTests()
    {
        if (_previewOnly || _host.World is null)
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double centerWorldX = _host.MapPreset.Width * 0.5 + 1.10 / metersPerWorldUnit;
        double centerWorldY = _host.MapPreset.Height * 0.5 - 1.45 / metersPerWorldUnit;
        Vector3 positionScene = ToScenePoint(centerWorldX, centerWorldY, 0.26f);
        float yawRad = (float)(_host.World.GameTimeSec * 0.85);
        float pitchRad = MathF.Sin((float)_host.World.GameTimeSec * 0.55f) * 0.055f;

        Matrix4x4 modelMatrix =
            Matrix4x4.CreateTranslation(-Vector3.Zero)
            * Matrix4x4.CreateFromYawPitchRoll(yawRad, pitchRad, 0f)
            * Matrix4x4.CreateTranslation(positionScene);

        Color frameColor = Color.FromArgb(218, 72, 86, 94);
        Color activeColor = Color.FromArgb(238, 255, 184, 72);
        Color unitColor = Color.FromArgb(210, 66, 182, 255);
        Color edgeColor = Color.FromArgb(238, 22, 28, 34);

        DrawGpuCompositeLocalBox(modelMatrix, new Vector3(0f, 0f, 0f), 0.78f, 0.050f, 0.050f, frameColor, edgeColor);
        DrawGpuCompositeLocalBox(modelMatrix, new Vector3(0f, 0f, 0.13f), 0.68f, 0.035f, 0.035f, frameColor, edgeColor);
        DrawGpuCompositeLocalBox(modelMatrix, new Vector3(0f, 0f, -0.13f), 0.68f, 0.035f, 0.035f, frameColor, edgeColor);
        DrawGpuCompositeLocalBox(modelMatrix, new Vector3(0.40f, 0f, 0f), 0.045f, 0.36f, 0.065f, activeColor, edgeColor);
        DrawGpuCompositeLocalBox(modelMatrix, new Vector3(-0.40f, 0f, 0f), 0.045f, 0.36f, 0.065f, unitColor, edgeColor);
        DrawGpuCompositeLocalBox(modelMatrix, new Vector3(0f, -0.19f, 0f), 0.080f, 0.080f, 0.38f, Color.FromArgb(228, 86, 92, 100), edgeColor);

        Vector3 pivot = Vector3.Transform(Vector3.Zero, modelMatrix);
        DrawGpuImpactCircle(pivot + Vector3.UnitY * 0.008f, Vector3.UnitY, 0.16f, Color.FromArgb(160, 255, 202, 82));

        if (!_fieldCompositeInteractionTestLogged)
        {
            _fieldCompositeInteractionTestLogged = true;
            AppendGameplayLog(
                "field_interaction_test.log",
                $"{DateTime.Now:HH:mm:ss.fff} composite_test=enabled source=LoadLargeTerrain_matrix_semantics components=6 interaction_units=2 participates_in_rules=false participates_in_collision=false");
        }
    }

    private void DrawGpuCompositeLocalBox(
        Matrix4x4 modelMatrix,
        Vector3 localCenter,
        float length,
        float width,
        float height,
        Color fillColor,
        Color edgeColor)
    {
        Vector3 center = Vector3.Transform(localCenter, modelMatrix);
        Vector3 forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, modelMatrix));
        Vector3 right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, modelMatrix));
        Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, modelMatrix));
        DrawGpuOrientedBox(center, forward, right, up, length, width, height, fillColor, edgeColor);
    }

    private void DrawGpuEntities()
    {
        _entityOverlayBuffer.Clear();
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (!ShouldRenderEntity(entity))
            {
                continue;
            }

            if (ShouldHideTemporaryArenaMechanismModels() && IsTemporaryArenaMechanismEntity(entity))
            {
                continue;
            }

            if (ShouldSuppressLegacyBaseOrOutpostProxyEntity(entity))
            {
                continue;
            }

            if (string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                float baseHeightOnly = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM);
                Vector3 overlayCenter = ToScenePoint(entity.X, entity.Y, baseHeightOnly);
                RobotAppearanceProfile overlayProfile = _host.ResolveAppearanceProfile(entity);
                float overlayHeight = Math.Max(
                    1.0f,
                    overlayProfile.StructureGroundClearanceM
                    + overlayProfile.StructureBaseHeightM
                    + overlayProfile.StructureFrameHeightM
                    + overlayProfile.StructureRotorRadiusM);
                _entityOverlayBuffer.Add(new EntityRenderOverlay(
                    entity,
                    overlayCenter,
                    overlayHeight,
                    overlayProfile));
                continue;
            }

            RobotAppearanceProfile entityProfile = _host.ResolveAppearanceProfile(entity);
            Color bodyColor = entityProfile.BodyColor.A <= 0 ? Color.FromArgb(166, 174, 186) : entityProfile.BodyColor;
            RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(entity);
            float baseHeight = (float)Math.Max(
                0.0,
                entity.GroundHeightM + entity.AirborneHeightM + motion.BodyLiftM);
            Vector3 center = ToScenePoint(entity.X, entity.Y, baseHeight + (float)Math.Max(0.06, entity.BodyHeightM * 0.5))
                + ResolveRuntimeChassisSceneOffset(entity, motion);
            float radius = entity.EntityType switch
            {
                "base" => 0.62f,
                "outpost" => 0.42f,
                "sentry" => 0.25f,
                _ => Math.Max(0.16f, (float)Math.Max(entity.BodyLengthM, entity.BodyWidthM) * 0.45f),
            };
            float height = entity.EntityType is "base" or "outpost"
                ? Math.Max(0.55f, (float)entity.BodyHeightM)
                : Math.Max(0.14f, (float)entity.BodyHeightM);
            DrawGpuBox(center, radius, Math.Max(0.09f, radius * 0.7f), height, Color.FromArgb(230, bodyColor));
            _entityOverlayBuffer.Add(new EntityRenderOverlay(
                entity,
                center,
                height,
                entityProfile));

            float yaw = (float)(entity.AngleDeg * Math.PI / 180.0);
            Vector3 nose = center + new Vector3(MathF.Cos(yaw) * radius * 1.35f, 0.02f, MathF.Sin(yaw) * radius * 1.35f);
            DrawGpuLine(center + new Vector3(0f, height * 0.48f, 0f), nose + new Vector3(0f, height * 0.48f, 0f), Color.FromArgb(250, 242, 246, 250));
        }
    }

    private void DrawGpuProjectiles()
    {
        if (_host.World.Projectiles.Count == 0)
        {
            return;
        }

        int projectileCount = _host.World.Projectiles.Count;
        const int LargeProjectileSegments = 6;
        const int SmallProjectileSegments = 5;
        List<GpuVertex> projectileBuffer = CurrentGpuDynamicBuildBuffer();
        int requiredVertices = projectileCount * LargeProjectileSegments * 3;
        if (projectileBuffer.Capacity < requiredVertices)
        {
            projectileBuffer.Capacity = requiredVertices;
        }

        Vector3 viewForward = _cameraTargetM - _cameraPositionM;
        if (viewForward.LengthSquared() <= 1e-6f)
        {
            viewForward = Vector3.UnitZ;
        }
        else
        {
            viewForward = Vector3.Normalize(viewForward);
        }

        Vector3 cameraRight = Vector3.Cross(viewForward, Vector3.UnitY);
        if (cameraRight.LengthSquared() <= 1e-6f)
        {
            cameraRight = Vector3.UnitX;
        }
        else
        {
            cameraRight = Vector3.Normalize(cameraRight);
        }

        Vector3 cameraUp = Vector3.Cross(cameraRight, viewForward);
        if (cameraUp.LengthSquared() <= 1e-6f)
        {
            cameraUp = Vector3.UnitY;
        }
        else
        {
            cameraUp = Vector3.Normalize(cameraUp);
        }

        foreach (SimulationProjectile projectile in _host.World.Projectiles)
        {
            bool largeRound = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
            Color color = largeRound
                ? Color.FromArgb(248, 250, 252, 255)
                : Color.FromArgb(248, 112, 255, 128);
            Vector3 center = ToScenePoint(projectile.X, projectile.Y, (float)Math.Max(0.05, projectile.HeightM));
            float radius = (float)(SimulationCombatMath.ProjectileDiameterM(projectile.AmmoType) * 0.5);
            float visibleRadius = Math.Max(radius * (largeRound ? 2.2f : 2.6f), largeRound ? 0.015f : 0.010f);
            if (!IsSceneBoundsPotentiallyVisible(center, visibleRadius * 1.3f, visibleRadius * 1.3f))
            {
                continue;
            }

            Color rimColor = largeRound
                ? Color.FromArgb(color.A, 98, 196, 255)
                : Color.FromArgb(color.A, 42, 255, 92);
            Color coreColor = largeRound
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(255, 188, 255, 200);
            AppendGpuProjectileBillboard(
                projectileBuffer,
                center,
                visibleRadius,
                rimColor,
                coreColor,
                largeRound ? LargeProjectileSegments : SmallProjectileSegments,
                cameraRight,
                cameraUp);
        }
    }

    private void DrawGpuProjectileTrailLines()
    {
        if (!_showProjectileTrails || _projectileTrailPoints.Count == 0)
        {
            return;
        }

        glDisable(GlDepthTest);
        glLineWidth(2.0f);
        foreach (SimulationProjectile projectile in _host.World.Projectiles)
        {
            if (!_projectileTrailPoints.TryGetValue(projectile.Id, out List<Vector3>? trail) || trail.Count < 2)
            {
                continue;
            }

            bool largeRound = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
            Color color = largeRound
                ? Color.FromArgb(170, 86, 188, 255)
                : Color.FromArgb(176, 72, 255, 108);
            DrawGpuPolyline(trail, color);
        }

        glLineWidth(1.0f);
        glEnable(GlDepthTest);
    }

    private void DrawGpuPredictedProjectileTrajectory()
    {
        if (!_showProjectileTrails || _paused)
        {
            return;
        }

        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || !entity.IsAlive)
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double yawRad = entity.TurretYawDeg * Math.PI / 180.0;
        double pitchRad = entity.GimbalPitchDeg * Math.PI / 180.0;
        double speedMps = SimulationCombatMath.ProjectileSpeedMps(entity);
        (double x, double y, double heightM) = SimulationCombatMath.ComputeMuzzlePoint(_host.World, entity, entity.GimbalPitchDeg);
        double inheritedVxWorldPerSec = entity.HasObservedKinematics ? entity.ObservedVelocityXWorldPerSec : entity.VelocityXWorldPerSec;
        double inheritedVyWorldPerSec = entity.HasObservedKinematics ? entity.ObservedVelocityYWorldPerSec : entity.VelocityYWorldPerSec;
        double vxMps = inheritedVxWorldPerSec * metersPerWorldUnit + Math.Cos(pitchRad) * Math.Cos(yawRad) * speedMps;
        double vyMps = inheritedVyWorldPerSec * metersPerWorldUnit + Math.Cos(pitchRad) * Math.Sin(yawRad) * speedMps;
        double vzMps = Math.Sin(pitchRad) * speedMps;

        Span<Vector3> trajectory = stackalloc Vector3[128];
        int count = 0;
        bool hasImpactSurface = false;
        RuntimeGridData? runtimeGrid = _host.RuntimeGrid;
        double dt = 0.035;
        double maxLifeSec = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 4.2 : 2.8;
        for (double t = 0.0; t <= maxLifeSec && count < trajectory.Length; t += dt)
        {
            trajectory[count++] = ToScenePoint(x, y, (float)heightM);
            if (heightM < -0.05 || IsPredictedProjectileOutsideWorld(runtimeGrid, x, y))
            {
                break;
            }

            if (runtimeGrid is not null && runtimeGrid.IsValid)
            {
                float terrainHeight = runtimeGrid.SampleOcclusionHeight((float)x, (float)y);
                if (heightM <= terrainHeight + 0.015 && t > 0.05)
                {
                    hasImpactSurface = true;
                    break;
                }
            }

            ApplyPredictedProjectileStep(entity.AmmoType, metersPerWorldUnit, dt, ref x, ref y, ref heightM, ref vxMps, ref vyMps, ref vzMps);
        }

        if (count < 2)
        {
            return;
        }

        glDisable(GlDepthTest);
        glLineWidth(4.0f);
        DrawGpuPolyline(trajectory[..count], Color.FromArgb(92, 255, 190, 56));
        glLineWidth(2.0f);
        DrawGpuPolyline(trajectory[..count], Color.FromArgb(242, 255, 214, 76));
        if (hasImpactSurface)
        {
            bool largeRound = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
            float radius = largeRound ? 0.18f : 0.11f;
            DrawGpuImpactCircle(trajectory[count - 1] + Vector3.UnitY * 0.012f, Vector3.UnitY, radius, Color.FromArgb(248, 255, 224, 72));
        }

        glLineWidth(1.0f);
        glEnable(GlDepthTest);
    }

    private void DrawGpuEntityHealthBars()
    {
        if (_previewOnly || _entityOverlayBuffer.Count == 0)
        {
            return;
        }

        string? selectedTeam = _host.SelectedEntity?.Team;
        if (string.IsNullOrWhiteSpace(selectedTeam))
        {
            return;
        }

        Vector3 viewForward = _cameraTargetM - _cameraPositionM;
        if (viewForward.LengthSquared() <= 1e-6f)
        {
            viewForward = Vector3.UnitZ;
        }
        else
        {
            viewForward = Vector3.Normalize(viewForward);
        }

        Vector3 cameraRight = Vector3.Cross(viewForward, Vector3.UnitY);
        if (cameraRight.LengthSquared() <= 1e-6f)
        {
            cameraRight = Vector3.UnitX;
        }
        else
        {
            cameraRight = Vector3.Normalize(cameraRight);
        }

        Vector3 cameraUp = Vector3.Cross(cameraRight, viewForward);
        if (cameraUp.LengthSquared() <= 1e-6f)
        {
            cameraUp = Vector3.UnitY;
        }
        else
        {
            cameraUp = Vector3.Normalize(cameraUp);
        }

        glEnable(GlDepthTest);
        foreach (EntityRenderOverlay overlay in _entityOverlayBuffer)
        {
            SimulationEntity entity = overlay.Entity;
            if (!entity.IsAlive
                || entity.IsSimulationSuppressed
                || !string.Equals(entity.Team, selectedTeam, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
                || (!string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (_firstPersonView && string.Equals(entity.Id, _host.SelectedEntity?.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            float healthRatio = entity.MaxHealth <= 1e-6
                ? 0f
                : (float)Math.Clamp(entity.Health / entity.MaxHealth, 0.0, 1.0);
            Vector3 anchor = overlay.Center + Vector3.UnitY * (overlay.Height + 0.18f);
            float distanceM = MathF.Sqrt(Math.Max(0.01f, Vector3.DistanceSquared(anchor, _cameraPositionM)));
            float width = Math.Clamp(distanceM * 0.025f, 0.42f, 0.90f);
            float height = Math.Clamp(width * 0.085f, 0.035f, 0.065f);
            DrawGpuBillboardHealthBar(
                anchor,
                cameraRight,
                cameraUp,
                width,
                height,
                healthRatio,
                ResolveTeamColor(entity.Team));
        }
    }

    private static void DrawGpuBillboardHealthBar(
        Vector3 center,
        Vector3 right,
        Vector3 up,
        float width,
        float height,
        float ratio,
        Color teamColor)
    {
        Vector3 halfRight = right * (width * 0.5f);
        Vector3 halfUp = up * (height * 0.5f);

        float clamped = Math.Clamp(ratio, 0f, 1f);
        if (clamped <= 0.001f)
        {
            return;
        }

        float inset = Math.Min(height * 0.22f, width * 0.04f);
        Vector3 left = center - halfRight + right * inset;
        Vector3 bottom = -halfUp + up * inset;
        Vector3 top = halfUp - up * inset;
        Vector3 fillRight = right * Math.Max(0.0f, (width - inset * 2f) * clamped);
        Color fillColor = Color.FromArgb(232, BlendColor(teamColor, Color.White, 0.18f));
        DrawGpuQuad(
            left + bottom,
            left + fillRight + bottom,
            left + fillRight + top,
            left + top,
            fillColor);
    }

    private static void DrawGpuImpactCircle(Vector3 center, Vector3 normal, float radius, Color color)
    {
        if (normal.LengthSquared() <= 1e-8f || radius <= 1e-4f)
        {
            return;
        }

        normal = Vector3.Normalize(normal);
        Vector3 tangent = Vector3.Cross(normal, Vector3.UnitZ);
        if (tangent.LengthSquared() <= 1e-6f)
        {
            tangent = Vector3.Cross(normal, Vector3.UnitX);
        }

        tangent = Vector3.Normalize(tangent);
        Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
        SetGpuColor(color);
        glBegin(GlLineLoop);
        const int segments = 36;
        for (int index = 0; index < segments; index++)
        {
            float angle = MathF.Tau * index / segments;
            Vector3 point = center + tangent * (MathF.Cos(angle) * radius) + bitangent * (MathF.Sin(angle) * radius);
            glVertex3f(point.X, point.Y, point.Z);
        }

        glEnd();
    }

    private void DrawGpuDebugReference()
    {
        float scale = (float)Math.Max(1e-6, _host.World.MetersPerWorldUnit);
        float widthM = _host.MapPreset.Width * scale;
        float heightM = _host.MapPreset.Height * scale;
        float centerX = widthM * 0.5f;
        float centerZ = heightM * 0.5f;
        DrawGpuLine(new Vector3(centerX - 1.8f, 0.08f, centerZ), new Vector3(centerX + 1.8f, 0.08f, centerZ), Color.FromArgb(255, 255, 80, 80));
        DrawGpuLine(new Vector3(centerX, 0.08f, centerZ - 1.8f), new Vector3(centerX, 0.08f, centerZ + 1.8f), Color.FromArgb(255, 80, 180, 255));
    }

    private void DrawGpuBox(Vector3 center, float halfX, float halfZ, float height, Color color)
    {
        float y0 = center.Y - height * 0.5f;
        float y1 = center.Y + height * 0.5f;
        Vector3 a = new(center.X - halfX, y0, center.Z - halfZ);
        Vector3 b = new(center.X + halfX, y0, center.Z - halfZ);
        Vector3 c = new(center.X + halfX, y0, center.Z + halfZ);
        Vector3 d = new(center.X - halfX, y0, center.Z + halfZ);
        Vector3 e = new(center.X - halfX, y1, center.Z - halfZ);
        Vector3 f = new(center.X + halfX, y1, center.Z - halfZ);
        Vector3 g = new(center.X + halfX, y1, center.Z + halfZ);
        Vector3 h = new(center.X - halfX, y1, center.Z + halfZ);
        DrawGpuQuad(e, f, g, h, color);
        DrawGpuQuad(a, b, f, e, ScaleGpuColor(color, 0.75f));
        DrawGpuQuad(b, c, g, f, ScaleGpuColor(color, 0.70f));
        DrawGpuQuad(c, d, h, g, ScaleGpuColor(color, 0.62f));
        DrawGpuQuad(d, a, e, h, ScaleGpuColor(color, 0.68f));
    }

    private void DrawGpuEnergyMechanismModel(FacilityRegion region, Color fallbackColor, double? overrideCenterWorldX = null, double? overrideCenterWorldY = null)
    {
        bool previousEnergyBatch = _gpuEnergyMechanismBatchActive;
        bool previousDynamicBatch = _gpuBatchingDynamicGeometry;
        _gpuEnergyMechanismBatchActive = true;
        _gpuBatchingDynamicGeometry = true;
        RobotAppearanceProfile profile = _host.AppearanceCatalog.ResolveFacilityProfile(region);
        (double centerWorldX, double centerWorldY) = overrideCenterWorldX.HasValue && overrideCenterWorldY.HasValue
            ? (overrideCenterWorldX.Value, overrideCenterWorldY.Value)
            : ResolveFacilityRegionCenter(region);
        try
        {
            Vector3 center = ToScenePoint(centerWorldX, centerWorldY, 0f);
            EnergyRenderMesh mesh = EnergyMechanismGeometry.BuildSingle(
                profile,
                center,
                EnergyMechanismGeometry.ResolveAccentColor(region.Team),
                (float)_host.World.GameTimeSec,
                ResolveEnergyRotorYawForRender);

            foreach (EnergyRenderPrism prism in mesh.Prisms)
            {
                DrawGpuGeneralPrism(prism.Bottom, prism.Top, prism.FillColor);
            }

            foreach (EnergyRenderBox box in mesh.Boxes)
            {
                DrawGpuOrientedBox(box.Center, box.Forward, box.Right, box.Up, box.Length, box.Width, box.Height, box.FillColor, box.EdgeColor);
            }

            foreach (EnergyRenderCylinder cylinder in mesh.Cylinders)
            {
                DrawGpuDiskTarget(cylinder.Center, cylinder.NormalAxis, cylinder.UpAxis, cylinder.Radius, cylinder.Thickness, cylinder.FillColor, cylinder.Segments);
            }

            foreach (EnergyRenderAnnulus annulus in mesh.Annuli)
            {
                DrawGpuAnnulus(annulus.Center, annulus.NormalAxis, annulus.UpAxis, annulus.InnerRadius, annulus.OuterRadius, annulus.FillColor, annulus.Segments);
            }

            DrawGpuEnergyMechanismRuleHighlights(region, centerWorldX, centerWorldY);
        }
        finally
        {
            _gpuEnergyMechanismBatchActive = previousEnergyBatch;
            _gpuBatchingDynamicGeometry = previousDynamicBatch;
        }
    }

    private float ResolveEnergyRotorYawForRender(int rotorIndex)
    {
        string team = rotorIndex == 0 ? "red" : "blue";
        _host.World.Teams.TryGetValue(team, out SimulationTeamState? teamState);
        return EnergyMechanismGeometry.ResolveRuleRotorYaw((float)_host.World.GameTimeSec, teamState);
    }

    private void DrawGpuEnergyMechanismRuleHighlights(FacilityRegion region, double centerWorldX, double centerWorldY)
    {
        // Only render the authored energy-mechanism model rings. The score/active
        // overlay annuli added extra rings standing off the disk plane.
        if (!ShouldRenderGeneratedEnergyMechanismRingOverlays())
        {
            return;
        }

        SimulationEntity? mechanism = null;
        double bestMechanismScore = double.MaxValue;
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (!string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double dx = entity.X - centerWorldX;
            double dy = entity.Y - centerWorldY;
            double score = dx * dx + dy * dy;
            if (string.Equals(entity.Id, region.Id, StringComparison.OrdinalIgnoreCase))
            {
                score -= 100000.0;
            }

            if (score >= bestMechanismScore)
            {
                continue;
            }

            bestMechanismScore = score;
            mechanism = entity;
        }

        if (mechanism is null)
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        foreach (SimulationTeamState teamState in _host.World.Teams.Values)
        {
            bool showPending = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
                && teamState.EnergyCurrentLitMask != 0;
            bool showActive = showPending && teamState.EnergyNextModuleDelaySec <= 1e-6;
            bool showActivated = string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase)
                && teamState.EnergyBuffTimerSec > 1e-6;
            bool showLastHit = teamState.EnergyLastHitArmIndex >= 0 && teamState.EnergyLastRingScore > 0;
            bool hasPersistentRings = false;
            for (int index = 0; index < teamState.EnergyHitRingsByArm.Length; index++)
            {
                if (teamState.EnergyHitRingsByArm[index] > 0)
                {
                    hasPersistentRings = true;
                    break;
                }
            }

            if (!showPending && !showActivated && !showLastHit && !hasPersistentRings)
            {
                continue;
            }

            Color teamColor = ResolveMapTeamLineColor(teamState.Team);
            Color activeColor = ResolveEnergyMechanismRingLitColor(teamColor, emphasized: showActive);
            Color ringSteadyColor = ResolveEnergyMechanismRingLitColor(teamColor, emphasized: false);
            bool completionFlashBlack = IsFineTerrainEnergyCompletionFlashBlack(_host.World.GameTimeSec, teamState);
            foreach (ArmorPlateTarget plate in SelectEnergyMechanismOverlayDisks(
                         SimulationCombatMath.GetEnergyMechanismTargets(mechanism, metersPerWorldUnit, _host.World.GameTimeSec, teamState.Team, teamState)))
            {
                if (!SimulationCombatMath.TryParseEnergyArmIndex(plate.Id, out _, out int armIndex))
                {
                    continue;
                }

                Vector3 center = ToScenePoint(plate.X, plate.Y, (float)plate.HeightM);
                Vector3 normal = ResolveGpuPlateNormal(plate);
                float diskRadius = ResolveEnergyPendingPatternRadius((float)Math.Max(plate.WidthM, plate.HeightSpanM) * 0.5f);
                int persistentRingScore = armIndex >= 0 && armIndex < teamState.EnergyHitRingsByArm.Length
                    ? Math.Clamp(teamState.EnergyHitRingsByArm[armIndex], 0, 10)
                    : 0;
                bool activeArm = showPending && (teamState.EnergyCurrentLitMask & (1 << armIndex)) != 0;
                if (persistentRingScore <= 0 && !activeArm)
                {
                    continue;
                }

                void DrawRingScore(int ringScore, Color ringColor, bool emphasized)
                {
                    float outer = diskRadius * (11 - ringScore) / 10f;
                    float inner = ringScore >= 10 ? 0f : diskRadius * (10 - ringScore) / 10f;
                    DrawGpuAnnulusDoubleSided(
                        center,
                        normal,
                        Vector3.UnitY,
                        inner,
                        Math.Max(inner + (emphasized ? 0.004f : 0.003f), outer),
                        ringColor,
                        24,
                        emphasized ? 0.0140f : 0.0130f);
                }

                if (activeArm && persistentRingScore <= 0)
                {
                    DrawGpuEnergyPendingDiskPattern(center, normal, Vector3.UnitY, diskRadius, activeColor);
                    continue;
                }

                bool hitFlashing = showLastHit
                    && teamState.EnergyLastHitArmIndex == armIndex
                    && _host.World.GameTimeSec <= teamState.EnergyLastHitFlashEndSec;
                bool hitFlashBlack = hitFlashing
                    && IsFineTerrainEnergyHitFlashBlack(_host.World.GameTimeSec, teamState.EnergyLastHitFlashEndSec);
                Color ringColor = completionFlashBlack || hitFlashBlack
                    ? ResolveEnergyMechanismRingFlashColor(teamColor)
                    : ringSteadyColor;
                DrawRingScore(persistentRingScore, ringColor, hitFlashing || completionFlashBlack);
            }
        }
    }

    private void DrawGpuEnergyPendingDiskPattern(
        Vector3 center,
        Vector3 normalAxis,
        Vector3 upAxis,
        float diskRadius,
        Color activeColor)
    {
        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        Vector3 up = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        up -= normal * Vector3.Dot(up, normal);
        if (up.LengthSquared() <= 1e-8f)
        {
            up = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.96f
                ? Vector3.UnitX
                : Vector3.UnitY;
            up -= normal * Vector3.Dot(up, normal);
        }

        up = up.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(up);
        Vector3 side = Vector3.Cross(up, normal);
        if (side.LengthSquared() <= 1e-8f)
        {
            side = Vector3.UnitX;
        }
        else
        {
            side = Vector3.Normalize(side);
        }

        Color hot = ResolveEnergyMechanismPendingDiskColor(activeColor);
        float scale = ResolveEnergyPendingPatternRadius(diskRadius) / 0.300f;
        float ringWidth = 0.040f * scale;
        foreach (float outerRadius in new[] { 0.070f * scale, 0.150f * scale, 0.270f * scale })
        {
            DrawGpuAnnulus(
                center + normal * 0.006f,
                normal,
                up,
                MathF.Max(0.0f, outerRadius - ringWidth),
                outerRadius,
                hot,
                64);
        }

        float spokeOuterRadius = 0.300f * scale;
        float spokeInnerRadius = MathF.Max(0.0f, spokeOuterRadius - 0.200f * scale);
        float spokeOuterHalfWidth = 0.0350f * scale;
        float spokeInnerHalfWidth = 0.0100f * scale;
        Vector3 faceCenter = center + normal * 0.007f;
        for (int index = 0; index < 4; index++)
        {
            float angle = index * MathF.Tau / 4f;
            Vector3 radial = Vector3.Normalize(up * MathF.Cos(angle) + side * MathF.Sin(angle));
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(normal, radial));
            Vector3 inner = faceCenter + radial * spokeInnerRadius;
            Vector3 outer = faceCenter + radial * spokeOuterRadius;
            AppendOrDrawGpuQuad(
                inner - tangent * spokeInnerHalfWidth,
                inner + tangent * spokeInnerHalfWidth,
                outer + tangent * spokeOuterHalfWidth,
                outer - tangent * spokeOuterHalfWidth,
                hot);
        }
    }

    private void DrawGpuAnnulusDoubleSided(Vector3 center, Vector3 normalAxis, Vector3 upAxis, float innerRadius, float outerRadius, Color color, int segmentCount, float offset)
    {
        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        DrawGpuAnnulus(center + normal * offset, normal, upAxis, innerRadius, outerRadius, color, segmentCount);
        DrawGpuAnnulus(center - normal * offset, -normal, upAxis, innerRadius, outerRadius, color, segmentCount);
    }

    private static bool ShouldRenderGeneratedEnergyMechanismRingOverlays() => true;

    private void DrawGpuGeneralPrism(IReadOnlyList<Vector3> bottom, IReadOnlyList<Vector3> top, Color color)
    {
        if (bottom.Count < 3 || top.Count < 3 || bottom.Count != top.Count)
        {
            return;
        }

        for (int index = 1; index < top.Count - 1; index++)
        {
            AppendOrDrawGpuTriangle(top[0], top[index], top[index + 1], color);
            AppendOrDrawGpuTriangle(bottom[0], bottom[index + 1], bottom[index], ScaleGpuColor(color, 0.84f));
        }

        for (int index = 0; index < bottom.Count; index++)
        {
            int next = (index + 1) % bottom.Count;
            AppendOrDrawGpuQuad(bottom[index], bottom[next], top[next], top[index], ScaleGpuColor(color, 0.74f - 0.05f * (index % 3)));
        }
    }

    private void DrawGpuEnergyMechanismHanger(
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float width,
        float height,
        float depth,
        Color frameColor,
        Color edgeColor)
    {
        float frameHalfLength = width * 0.5f;
        float frameHalfHeight = height * 0.5f;
        float bar = Math.Max(0.020f, Math.Min(width, height) * 0.12f);
        foreach (float side in new[] { -1f, 1f })
        {
            DrawGpuOrientedBox(
                center + forward * (frameHalfLength * side),
                up,
                right,
                forward,
                frameHalfHeight * 2f,
                depth,
                bar,
                frameColor,
                edgeColor);

            DrawGpuOrientedBox(
                center + up * (frameHalfHeight * side),
                forward,
                right,
                up,
                frameHalfLength * 2f + bar,
                depth,
                bar,
                frameColor,
                edgeColor);
        }
    }

    private static IReadOnlyList<Vector3> BuildGpuEnergyPlatformFootprint(
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        float baseHeight,
        float length,
        float width,
        float cornerScale)
    {
        float halfLength = Math.Max(0.12f, length * 0.5f);
        float halfWidth = Math.Max(0.12f, width * 0.5f);
        float cutLength = Math.Max(0.05f, halfLength * cornerScale);
        float cutWidth = Math.Max(0.05f, halfWidth * cornerScale);
        (float X, float Z)[] shape =
        [
            (-halfLength + cutLength, -halfWidth),
            (halfLength - cutLength, -halfWidth),
            (halfLength, -halfWidth + cutWidth),
            (halfLength, halfWidth - cutWidth),
            (halfLength - cutLength, halfWidth),
            (-halfLength + cutLength, halfWidth),
            (-halfLength, halfWidth - cutWidth),
            (-halfLength, -halfWidth + cutWidth),
        ];
        Vector3[] result = new Vector3[shape.Length];
        for (int index = 0; index < shape.Length; index++)
        {
            result[index] = center + forward * shape[index].X + right * shape[index].Z + Vector3.UnitY * baseHeight;
        }

        return result;
    }

    private void DrawGpuPrism(IReadOnlyList<Vector3> footprint, float height, Color color)
    {
        if (footprint.Count < 3 || height <= 1e-4f)
        {
            return;
        }

        Vector3 offset = Vector3.UnitY * height;
        for (int index = 1; index < footprint.Count - 1; index++)
        {
            DrawGpuTriangle(footprint[0] + offset, footprint[index] + offset, footprint[index + 1] + offset, color);
        }

        for (int index = 0; index < footprint.Count; index++)
        {
            Vector3 a = footprint[index];
            Vector3 b = footprint[(index + 1) % footprint.Count];
            DrawGpuQuad(a, b, b + offset, a + offset, ScaleGpuColor(color, 0.72f - 0.06f * (index % 3)));
        }
    }

    private void DrawGpuEnergyMechanismBrace(
        Vector3 start,
        Vector3 end,
        float width,
        float depth,
        Color fillColor,
        Color edgeColor)
    {
        Vector3 axis = end - start;
        float length = axis.Length();
        if (length <= 1e-4f)
        {
            return;
        }

        Vector3 forward = Vector3.Normalize(axis);
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        if (right.LengthSquared() <= 1e-6f)
        {
            right = Vector3.UnitZ;
        }

        Vector3 up = Vector3.Normalize(Vector3.Cross(forward, right));
        DrawGpuOrientedBox((start + end) * 0.5f, forward, right, up, length, depth, width, fillColor, edgeColor);
    }

    private void DrawGpuEnergyMechanismArm(
        Vector3 center,
        Vector3 axis,
        Vector3 right,
        Vector3 up,
        float innerRadius,
        float outerRadius,
        float railGap,
        float railThickness,
        Color fillColor,
        Color edgeColor)
    {
        Vector3 innerCenter = center + axis * innerRadius;
        Vector3 outerCenter = center + axis * outerRadius;
        Vector3 railOffset = up * railGap;
        DrawGpuEnergyMechanismBrace(innerCenter + railOffset, outerCenter + railOffset * 0.72f, railThickness, railThickness, fillColor, edgeColor);
        DrawGpuEnergyMechanismBrace(innerCenter - railOffset, outerCenter - railOffset * 0.72f, railThickness, railThickness, fillColor, edgeColor);
        DrawGpuEnergyMechanismBrace(innerCenter + railOffset, innerCenter - railOffset, railThickness * 0.8f, railThickness, fillColor, edgeColor);
        DrawGpuEnergyMechanismBrace(outerCenter + railOffset * 0.72f, outerCenter - railOffset * 0.72f, railThickness, railThickness, fillColor, edgeColor);
    }

    private void DrawGpuEnergyMechanismPod(
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float length,
        float width,
        float height,
        Color fillColor,
        Color edgeColor)
    {
        DrawGpuOrientedBox(center, forward, right, up, length * 0.72f, width, height, Color.FromArgb(255, 68, 72, 78), edgeColor);
        DrawGpuOrientedBox(center + forward * (length * 0.18f), forward, right, up, length * 0.22f, width * 0.82f, height * 0.78f, fillColor, edgeColor);
        DrawGpuOrientedBox(center - forward * (length * 0.22f), forward, right, up, length * 0.16f, width * 0.72f, height * 0.66f, Color.FromArgb(255, 54, 58, 64), edgeColor);
        DrawGpuOrientedBox(center - forward * (length * 0.06f) + right * (width * 0.18f), forward, right, up, length * 0.14f, width * 0.22f, height * 0.18f, Color.FromArgb(255, 60, 64, 70), edgeColor);
        DrawGpuOrientedBox(center - forward * (length * 0.06f) - right * (width * 0.18f), forward, right, up, length * 0.14f, width * 0.22f, height * 0.18f, Color.FromArgb(255, 60, 64, 70), edgeColor);
    }

    private void DrawGpuDiskTarget(Vector3 center, Vector3 normalAxis, Vector3 upAxis, float radius, float thickness, Color ringColor, int segmentCount = 20)
    {
        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        Vector3 up = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        if (MathF.Abs(Vector3.Dot(normal, up)) > 0.98f)
        {
            up = Vector3.UnitY;
            if (MathF.Abs(Vector3.Dot(normal, up)) > 0.98f)
            {
                up = Vector3.UnitZ;
            }
        }

        Vector3 tangent = Vector3.Normalize(up - normal * Vector3.Dot(up, normal));
        Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
        float halfThickness = Math.Max(0.001f, thickness * 0.5f);
        int segments = Math.Clamp(segmentCount, 12, 48);
        Color shellColor = Color.FromArgb(255, 68, 72, 78);
        Vector3 frontCenter = center - normal * halfThickness;
        Vector3 backCenter = center + normal * halfThickness;
        for (int index = 0; index < segments; index++)
        {
            float a0 = index * MathF.Tau / segments;
            float a1 = (index + 1) * MathF.Tau / segments;
            Vector3 radial0 = tangent * (MathF.Cos(a0) * radius) + bitangent * (MathF.Sin(a0) * radius);
            Vector3 radial1 = tangent * (MathF.Cos(a1) * radius) + bitangent * (MathF.Sin(a1) * radius);
            Vector3 front0 = frontCenter + radial0;
            Vector3 front1 = frontCenter + radial1;
            Vector3 back0 = backCenter + radial0;
            Vector3 back1 = backCenter + radial1;
            AppendOrDrawGpuQuad(front0, front1, back1, back0, shellColor);
            AppendOrDrawGpuTriangle(frontCenter, front1, front0, ScaleGpuColor(ringColor, 1.0f));
            AppendOrDrawGpuTriangle(backCenter, back0, back1, ScaleGpuColor(ringColor, 1.0f));
        }
    }

    private void DrawGpuAnnulus(Vector3 center, Vector3 normalAxis, Vector3 upAxis, float innerRadius, float outerRadius, Color color, int segmentCount = 28)
    {
        outerRadius = Math.Max(0.002f, outerRadius);
        innerRadius = Math.Clamp(innerRadius, 0f, outerRadius - 0.001f);
        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        Vector3 up = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        if (MathF.Abs(Vector3.Dot(normal, up)) > 0.98f)
        {
            up = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
        }

        Vector3 tangent = Vector3.Normalize(up - normal * Vector3.Dot(up, normal));
        Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
        int segments = Math.Clamp(segmentCount, 12, 64);
        Vector3 faceCenter = center - normal * 0.0015f;
        for (int index = 0; index < segments; index++)
        {
            float a0 = index * MathF.Tau / segments;
            float a1 = (index + 1) * MathF.Tau / segments;
            Vector3 outer0 = faceCenter + tangent * (MathF.Cos(a0) * outerRadius) + bitangent * (MathF.Sin(a0) * outerRadius);
            Vector3 outer1 = faceCenter + tangent * (MathF.Cos(a1) * outerRadius) + bitangent * (MathF.Sin(a1) * outerRadius);
            if (innerRadius <= 0.002f)
            {
                AppendOrDrawGpuTriangle(faceCenter, outer1, outer0, color);
                continue;
            }

            Vector3 inner0 = faceCenter + tangent * (MathF.Cos(a0) * innerRadius) + bitangent * (MathF.Sin(a0) * innerRadius);
            Vector3 inner1 = faceCenter + tangent * (MathF.Cos(a1) * innerRadius) + bitangent * (MathF.Sin(a1) * innerRadius);
            AppendOrDrawGpuQuad(inner0, inner1, outer1, outer0, color);
        }
    }

    private static Vector3 ResolveGpuPlateNormal(ArmorPlateTarget plate)
    {
        double yawRad = plate.YawDeg * Math.PI / 180.0;
        return Vector3.Normalize(new Vector3((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad)));
    }

    private void DrawGpuOrientedBox(
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float length,
        float width,
        float height,
        Color fillColor,
        Color edgeColor)
    {
        if (length <= 1e-4f || width <= 1e-4f || height <= 1e-4f)
        {
            return;
        }

        Vector3 f = forward.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(forward);
        Vector3 r = right.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(right);
        Vector3 u = up.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(up);
        Vector3 hf = f * (length * 0.5f);
        Vector3 hr = r * (width * 0.5f);
        Vector3 hu = u * (height * 0.5f);

        Vector3 v000 = center - hf - hr - hu;
        Vector3 v001 = center - hf + hr - hu;
        Vector3 v010 = center + hf + hr - hu;
        Vector3 v011 = center + hf - hr - hu;
        Vector3 v100 = center - hf - hr + hu;
        Vector3 v101 = center - hf + hr + hu;
        Vector3 v110 = center + hf + hr + hu;
        Vector3 v111 = center + hf - hr + hu;

        if (_gpuBatchingDynamicGeometry)
        {
            List<GpuVertex> target = CurrentGpuDynamicBuildBuffer();
            AppendGpuQuad(target, v100, v101, v110, v111, ScaleGpuColor(fillColor, 0.82f));
            AppendGpuQuad(target, v001, v000, v011, v010, ScaleGpuColor(fillColor, 0.46f));
            AppendGpuQuad(target, v000, v100, v111, v011, ScaleGpuColor(fillColor, 0.58f));
            AppendGpuQuad(target, v101, v001, v010, v110, ScaleGpuColor(fillColor, 0.54f));
            AppendGpuQuad(target, v011, v111, v110, v010, ScaleGpuColor(fillColor, 0.62f));
            AppendGpuQuad(target, v000, v001, v101, v100, ScaleGpuColor(fillColor, 0.50f));
            return;
        }

        var faces = new List<SolidFace>(6)
        {
            new(new[] { v100, v101, v110, v111 }, 0.82f),
            new(new[] { v001, v000, v011, v010 }, 0.46f),
            new(new[] { v000, v100, v111, v011 }, 0.58f),
            new(new[] { v101, v001, v010, v110 }, 0.54f),
            new(new[] { v011, v111, v110, v010 }, 0.62f),
            new(new[] { v000, v001, v101, v100 }, 0.50f),
        };
        DrawGpuSolidFaces(faces, fillColor, edgeColor);
    }

    private void DrawGpuSphere(Vector3 center, float radius, Color color, int slices, int stacks)
    {
        radius = Math.Max(0.001f, radius);
        int safeSlices = Math.Max(6, slices);
        int safeStacks = Math.Max(4, stacks);
        for (int stack = 0; stack < safeStacks; stack++)
        {
            float v0 = stack / (float)safeStacks;
            float v1 = (stack + 1) / (float)safeStacks;
            float phi0 = (v0 - 0.5f) * MathF.PI;
            float phi1 = (v1 - 0.5f) * MathF.PI;
            for (int slice = 0; slice < safeSlices; slice++)
            {
                float u0 = slice / (float)safeSlices;
                float u1 = (slice + 1) / (float)safeSlices;
                float theta0 = u0 * MathF.Tau;
                float theta1 = u1 * MathF.Tau;
                Vector3 a = center + ResolveSpherePoint(radius, theta0, phi0);
                Vector3 b = center + ResolveSpherePoint(radius, theta1, phi0);
                Vector3 c = center + ResolveSpherePoint(radius, theta1, phi1);
                Vector3 d = center + ResolveSpherePoint(radius, theta0, phi1);
                Color shaded = ShadeGpuFaceColor(color, a, b, c, 0.72f);
                DrawGpuQuad(a, b, c, d, shaded);
            }
        }
    }

    private static Vector3 ResolveSpherePoint(float radius, float theta, float phi)
    {
        float cosPhi = MathF.Cos(phi);
        return new Vector3(
            MathF.Cos(theta) * cosPhi * radius,
            MathF.Sin(phi) * radius,
            MathF.Sin(theta) * cosPhi * radius);
    }

    private void FlushGpuDynamicVertices()
    {
        FlushGpuVertexList(
            _gpuDynamicVertexBuildBuffer,
            ref _gpuDynamicVertexBuffer,
            ref _gpuDynamicVertexCapacity,
            useNativeLighting: true);
    }

    private void FlushGpuEnergyMechanismVertices()
    {
        FlushGpuVertexList(
            _gpuEnergyMechanismVertexBuildBuffer,
            ref _gpuEnergyMechanismVertexBuffer,
            ref _gpuEnergyMechanismVertexCapacity,
            useNativeLighting: true);
    }

    private void FlushGpuProjectileVertices()
    {
        FlushGpuVertexList(
            _gpuProjectileVertexBuildBuffer,
            ref _gpuProjectileVertexBuffer,
            ref _gpuProjectileVertexCapacity,
            useNativeLighting: false);
    }

    private void ResetFineTerrainEnergyBodyMeshCache(string? sceneKey)
    {
        if (string.Equals(_fineTerrainEnergyBodyMeshSceneKey, sceneKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_glDeleteBuffers is not null)
        {
            foreach (FineTerrainStaticMeshCache cache in _fineTerrainEnergyBodyMeshCache.Values)
            {
                if (cache.Buffer == 0)
                {
                    continue;
                }

                int buffer = cache.Buffer;
                _glDeleteBuffers(1, ref buffer);
            }
        }

        _fineTerrainEnergyBodyMeshCache.Clear();
        _fineTerrainEnergyBodyMeshSceneKey = sceneKey;
    }

    private void ResetFineTerrainEnergyStripMeshCache(string? sceneKey)
    {
        if (string.Equals(_fineTerrainEnergyStripMeshSceneKey, sceneKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_glDeleteBuffers is not null)
        {
            foreach (FineTerrainStaticMeshCache cache in _fineTerrainEnergyStripMeshCache.Values)
            {
                if (cache.Buffer == 0)
                {
                    continue;
                }

                int buffer = cache.Buffer;
                _glDeleteBuffers(1, ref buffer);
            }
        }

        _fineTerrainEnergyStripMeshCache.Clear();
        _fineTerrainEnergyStripTriangleCache.Clear();
        _fineTerrainEnergyStripMeshSceneKey = sceneKey;
    }

    private void ResetFineTerrainOutpostBodyMeshCache(string? sceneKey)
    {
        if (string.Equals(_fineTerrainOutpostBodyMeshSceneKey, sceneKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_glDeleteBuffers is not null)
        {
            foreach (FineTerrainStaticMeshCache cache in _fineTerrainOutpostBodyMeshCache.Values)
            {
                if (cache.Buffer == 0)
                {
                    continue;
                }

                int buffer = cache.Buffer;
                _glDeleteBuffers(1, ref buffer);
            }
        }

        _fineTerrainOutpostBodyMeshCache.Clear();
        _fineTerrainOutpostBodyMeshSceneKey = sceneKey;
    }

    private void ResetFineTerrainBaseBodyMeshCache(string? sceneKey)
    {
        if (string.Equals(_fineTerrainBaseBodyMeshSceneKey, sceneKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_glDeleteBuffers is not null)
        {
            foreach (FineTerrainStaticMeshCache cache in _fineTerrainBaseBodyMeshCache.Values)
            {
                if (cache.Buffer == 0)
                {
                    continue;
                }

                int buffer = cache.Buffer;
                _glDeleteBuffers(1, ref buffer);
            }
        }

        _fineTerrainBaseBodyMeshCache.Clear();
        _fineTerrainBaseBodyMeshSceneKey = sceneKey;
    }

    private void ResetFineTerrainEnergyUnitMeshCache(string? sceneKey)
    {
        if (string.Equals(_fineTerrainEnergyUnitMeshSceneKey, sceneKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_glDeleteBuffers is not null)
        {
            foreach (FineTerrainStaticMeshCache cache in _fineTerrainEnergyUnitMeshCache.Values)
            {
                if (cache.Buffer == 0)
                {
                    continue;
                }

                int buffer = cache.Buffer;
                _glDeleteBuffers(1, ref buffer);
            }
        }

        _fineTerrainEnergyUnitMeshCache.Clear();
        _fineTerrainEnergyUnitMeshSceneKey = sceneKey;
    }

    private void ResetFineTerrainOutpostUnitMeshCache(string? sceneKey)
    {
        if (string.Equals(_fineTerrainOutpostUnitMeshSceneKey, sceneKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_glDeleteBuffers is not null)
        {
            foreach (FineTerrainStaticMeshCache cache in _fineTerrainOutpostUnitMeshCache.Values)
            {
                if (cache.Buffer == 0)
                {
                    continue;
                }

                int buffer = cache.Buffer;
                _glDeleteBuffers(1, ref buffer);
            }
        }

        _fineTerrainOutpostUnitMeshCache.Clear();
        _fineTerrainOutpostUnitMeshSceneKey = sceneKey;
    }

    private void ResetFineTerrainBaseUnitMeshCache(string? sceneKey)
    {
        if (string.Equals(_fineTerrainBaseUnitMeshSceneKey, sceneKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_glDeleteBuffers is not null)
        {
            foreach (FineTerrainStaticMeshCache cache in _fineTerrainBaseUnitMeshCache.Values)
            {
                if (cache.Buffer == 0)
                {
                    continue;
                }

                int buffer = cache.Buffer;
                _glDeleteBuffers(1, ref buffer);
            }
        }

        _fineTerrainBaseUnitMeshCache.Clear();
        _fineTerrainBaseUnitMeshSceneKey = sceneKey;
    }

    private void FlushGpuVertexList(List<GpuVertex> vertices, ref int buffer, ref int capacity, bool useNativeLighting)
    {
        if (vertices.Count == 0)
        {
            return;
        }

        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null || _glBufferSubData is null)
        {
            DrawGpuVerticesImmediate(vertices, useNativeLighting);
            vertices.Clear();
            return;
        }

        if (buffer == 0)
        {
            _glGenBuffers(1, out buffer);
        }

        int bytes = vertices.Count * Marshal.SizeOf<GpuVertex>();
        _glBindBuffer(GlArrayBuffer, buffer);
        if (capacity < vertices.Count)
        {
            int nextCapacity = Math.Max(vertices.Count, Math.Max(4096, capacity * 2));
            _glBufferData(GlArrayBuffer, new IntPtr(nextCapacity * Marshal.SizeOf<GpuVertex>()), IntPtr.Zero, GlDynamicDraw);
            capacity = nextCapacity;
        }

        UploadGpuVertexSubData(vertices, bytes);
        DrawGpuVertexBuffer(buffer, vertices.Count, useNativeLighting);
        _glBindBuffer(GlArrayBuffer, 0);
        vertices.Clear();
    }

    private void TryInitializeGpuBufferApi()
    {
        if (_gpuBufferApiReady)
        {
            return;
        }

        try
        {
            _glGenBuffers = LoadOpenGlProc<GlGenBuffersDelegate>("glGenBuffers", "glGenBuffersARB");
            _glBindBuffer = LoadOpenGlProc<GlBindBufferDelegate>("glBindBuffer", "glBindBufferARB");
            _glBufferData = LoadOpenGlProc<GlBufferDataDelegate>("glBufferData", "glBufferDataARB");
            _glBufferSubData = LoadOpenGlProc<GlBufferSubDataDelegate>("glBufferSubData", "glBufferSubDataARB");
            _glDeleteBuffers = LoadOpenGlProc<GlDeleteBuffersDelegate>("glDeleteBuffers", "glDeleteBuffersARB");
            _glGenVertexArrays = LoadOpenGlProc<GlGenVertexArraysDelegate>("glGenVertexArrays", "glGenVertexArraysAPPLE");
            _glBindVertexArray = LoadOpenGlProc<GlBindVertexArrayDelegate>("glBindVertexArray", "glBindVertexArrayAPPLE");
            _glDeleteVertexArrays = LoadOpenGlProc<GlDeleteVertexArraysDelegate>("glDeleteVertexArrays", "glDeleteVertexArraysAPPLE");
            _gpuBufferApiReady = _glGenBuffers is not null
                && _glBindBuffer is not null
                && _glBufferData is not null
                && _glBufferSubData is not null
                && _glDeleteBuffers is not null;
        }
        catch
        {
            _gpuBufferApiReady = false;
        }
    }

    private void TryInitializeGpuFramebufferApi()
    {
        if (_gpuFramebufferApiReady)
        {
            return;
        }

        try
        {
            _glGenFramebuffers = LoadOpenGlProc<GlGenFramebuffersDelegate>("glGenFramebuffers", "glGenFramebuffersEXT");
            _glBindFramebuffer = LoadOpenGlProc<GlBindFramebufferDelegate>("glBindFramebuffer", "glBindFramebufferEXT");
            _glFramebufferTexture2D = LoadOpenGlProc<GlFramebufferTexture2DDelegate>("glFramebufferTexture2D", "glFramebufferTexture2DEXT");
            _glDeleteFramebuffers = LoadOpenGlProc<GlDeleteFramebuffersDelegate>("glDeleteFramebuffers", "glDeleteFramebuffersEXT");
            _glCheckFramebufferStatus = LoadOpenGlProc<GlCheckFramebufferStatusDelegate>("glCheckFramebufferStatus", "glCheckFramebufferStatusEXT");
            _glGenRenderbuffers = LoadOpenGlProc<GlGenRenderbuffersDelegate>("glGenRenderbuffers", "glGenRenderbuffersEXT");
            _glBindRenderbuffer = LoadOpenGlProc<GlBindRenderbufferDelegate>("glBindRenderbuffer", "glBindRenderbufferEXT");
            _glRenderbufferStorage = LoadOpenGlProc<GlRenderbufferStorageDelegate>("glRenderbufferStorage", "glRenderbufferStorageEXT");
            _glFramebufferRenderbuffer = LoadOpenGlProc<GlFramebufferRenderbufferDelegate>("glFramebufferRenderbuffer", "glFramebufferRenderbufferEXT");
            _glDeleteRenderbuffers = LoadOpenGlProc<GlDeleteRenderbuffersDelegate>("glDeleteRenderbuffers", "glDeleteRenderbuffersEXT");
            _gpuFramebufferApiReady = _glGenFramebuffers is not null
                && _glBindFramebuffer is not null
                && _glFramebufferTexture2D is not null
                && _glDeleteFramebuffers is not null
                && _glCheckFramebufferStatus is not null
                && _glGenRenderbuffers is not null
                && _glBindRenderbuffer is not null
                && _glRenderbufferStorage is not null
                && _glFramebufferRenderbuffer is not null
                && _glDeleteRenderbuffers is not null;
        }
        catch
        {
            _gpuFramebufferApiReady = false;
        }
    }

    private bool EnsureGpuSceneRenderTarget(Size size)
    {
        if (!_gpuFramebufferApiReady
            || _glGenFramebuffers is null
            || _glBindFramebuffer is null
            || _glFramebufferTexture2D is null
            || _glCheckFramebufferStatus is null
            || _glGenRenderbuffers is null
            || _glBindRenderbuffer is null
            || _glRenderbufferStorage is null
            || _glFramebufferRenderbuffer is null)
        {
            return false;
        }

        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        if (_gpuSceneFramebuffer != 0
            && _gpuSceneColorTexture != 0
            && _gpuSceneDepthRenderbuffer != 0
            && _gpuSceneRenderTargetSize.Width == width
            && _gpuSceneRenderTargetSize.Height == height)
        {
            return true;
        }

        DeleteGpuSceneRenderTarget();

        _glGenFramebuffers(1, out _gpuSceneFramebuffer);
        _glGenRenderbuffers(1, out _gpuSceneDepthRenderbuffer);
        glGenTextures(1, out _gpuSceneColorTexture);
        if (_gpuSceneFramebuffer == 0 || _gpuSceneDepthRenderbuffer == 0 || _gpuSceneColorTexture == 0)
        {
            DeleteGpuSceneRenderTarget();
            return false;
        }

        glBindTexture(GlTexture2D, _gpuSceneColorTexture);
        glTexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
        glTexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
        glTexImage2D(GlTexture2D, 0, GlRgba, width, height, 0, GlRgba, GlUnsignedByte, IntPtr.Zero);

        _glBindFramebuffer(GlFramebuffer, _gpuSceneFramebuffer);
        _glFramebufferTexture2D(GlFramebuffer, GlColorAttachment0, GlTexture2D, _gpuSceneColorTexture, 0);

        _glBindRenderbuffer(GlRenderbuffer, _gpuSceneDepthRenderbuffer);
        _glRenderbufferStorage(GlRenderbuffer, GlDepthComponent24, width, height);
        _glFramebufferRenderbuffer(GlFramebuffer, GlDepthAttachment, GlRenderbuffer, _gpuSceneDepthRenderbuffer);

        bool ready = _glCheckFramebufferStatus(GlFramebuffer) == GlFramebufferComplete;
        _glBindRenderbuffer(GlRenderbuffer, 0);
        _glBindFramebuffer(GlFramebuffer, 0);
        _gpuSceneRenderTargetSize = ready ? new Size(width, height) : Size.Empty;
        if (!ready)
        {
            DeleteGpuSceneRenderTarget();
        }

        return ready;
    }

    private void DeleteGpuSceneRenderTarget()
    {
        DeleteGpuFramebuffer(ref _gpuSceneFramebuffer);
        DeleteGpuRenderbuffer(ref _gpuSceneDepthRenderbuffer);
        DeleteGpuTexture(ref _gpuSceneColorTexture);
        _gpuSceneRenderTargetSize = Size.Empty;
    }

    private static T? LoadOpenGlProc<T>(params string[] names)
        where T : Delegate
    {
        foreach (string name in names)
        {
            IntPtr proc = wglGetProcAddress(name);
            long value = proc.ToInt64();
            if (value > 3 && value != -1)
            {
                return Marshal.GetDelegateForFunctionPointer<T>(proc);
            }
        }

        return null;
    }

    private void DeleteGpuFramebuffer(ref int framebuffer)
    {
        if (framebuffer == 0 || _glDeleteFramebuffers is null)
        {
            framebuffer = 0;
            return;
        }

        int value = framebuffer;
        _glDeleteFramebuffers(1, ref value);
        framebuffer = 0;
    }

    private void DeleteGpuRenderbuffer(ref int renderbuffer)
    {
        if (renderbuffer == 0 || _glDeleteRenderbuffers is null)
        {
            renderbuffer = 0;
            return;
        }

        int value = renderbuffer;
        _glDeleteRenderbuffers(1, ref value);
        renderbuffer = 0;
    }

    private void DeleteGpuTexture(ref int texture)
    {
        if (texture == 0)
        {
            return;
        }

        int value = texture;
        glDeleteTextures(1, ref value);
        texture = 0;
    }

    private unsafe void UploadGpuVertexBuffer(int buffer, List<GpuVertex> vertices, int usage)
    {
        if (_glBindBuffer is null || _glBufferData is null)
        {
            return;
        }

        ReadOnlySpan<GpuVertex> span = CollectionsMarshal.AsSpan(vertices);
        fixed (GpuVertex* ptr = span)
        {
            _glBindBuffer(GlArrayBuffer, buffer);
            _glBufferData(GlArrayBuffer, new IntPtr(span.Length * Marshal.SizeOf<GpuVertex>()), new IntPtr(ptr), usage);
            _glBindBuffer(GlArrayBuffer, 0);
        }
    }

    private unsafe void UploadGpuIndexBuffer(int buffer, List<int> indices, int usage)
    {
        if (_glBindBuffer is null || _glBufferData is null)
        {
            return;
        }

        ReadOnlySpan<int> span = CollectionsMarshal.AsSpan(indices);
        fixed (int* ptr = span)
        {
            _glBindBuffer(GlElementArrayBuffer, buffer);
            _glBufferData(GlElementArrayBuffer, new IntPtr(span.Length * sizeof(int)), new IntPtr(ptr), usage);
            _glBindBuffer(GlElementArrayBuffer, 0);
        }
    }

    private unsafe void UploadGpuVertexSubData(List<GpuVertex> vertices, int bytes)
    {
        if (_glBufferSubData is null)
        {
            return;
        }

        ReadOnlySpan<GpuVertex> span = CollectionsMarshal.AsSpan(vertices);
        fixed (GpuVertex* ptr = span)
        {
            _glBufferSubData(GlArrayBuffer, IntPtr.Zero, new IntPtr(bytes), new IntPtr(ptr));
        }
    }

    private void DrawGpuVertexBuffer(int buffer, int vertexCount, bool useNativeLighting = false)
    {
        if (vertexCount <= 0 || _glBindBuffer is null)
        {
            return;
        }

        int stride = Marshal.SizeOf<GpuVertex>();
        if (_gpuSharedVertexArray == 0 && _glGenVertexArrays is not null)
        {
            _glGenVertexArrays(1, out _gpuSharedVertexArray);
        }

        _glBindVertexArray?.Invoke(_gpuSharedVertexArray);
        _glBindBuffer(GlArrayBuffer, buffer);
        bool enableLighting = useNativeLighting && _lightingSettings.Enabled;
        ConfigureGpuNativeLighting(enableLighting, _lightingSettings);
        glEnableClientState(GlVertexArray);
        glEnableClientState(GlColorArray);
        glVertexPointer(3, GlFloat, stride, IntPtr.Zero);
        glColorPointer(4, GlUnsignedByte, stride, new IntPtr(12));
        if (enableLighting)
        {
            glEnableClientState(GlNormalArray);
            glNormalPointer(GlFloat, stride, new IntPtr(16));
        }

        glDrawArrays(GlTriangles, 0, vertexCount);
        if (enableLighting)
        {
            glDisableClientState(GlNormalArray);
        }

        glDisableClientState(GlColorArray);
        glDisableClientState(GlVertexArray);
        ConfigureGpuNativeLighting(false, _lightingSettings);
        _glBindBuffer(GlArrayBuffer, 0);
        _glBindVertexArray?.Invoke(0);
    }

    private void DrawGpuVertexBuffer(int buffer, int vertexCount, Matrix4x4 modelMatrix)
    {
        if (vertexCount <= 0)
        {
            return;
        }

        Matrix4x4 modelView = modelMatrix * _viewMatrix;
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(modelView));
        try
        {
            DrawGpuVertexBuffer(buffer, vertexCount);
        }
        finally
        {
            glLoadMatrixf(ToOpenGlMatrix(_viewMatrix));
        }
    }

    private bool TryDrawCachedGpuEntityAppearance(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile)
    {
        if (!_gpuBufferApiReady || _glGenBuffers is null)
        {
            return false;
        }

        string cacheKey = BuildGpuEntityAppearanceMeshCacheKey(entity, profile);
        if (_gpuEntityAppearanceMeshCache.TryGetValue(cacheKey, out GpuEntityAppearanceMeshCache? cache))
        {
            cache.LastUsedFrame = _terrainCacheGpuFrameIndex;
            DrawGpuVertexBuffer(cache.Buffer, cache.VertexCount, ResolveGpuEntityAppearanceModelMatrix(entity, center));
            return true;
        }

        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        bool previousGeometryPass = _gpuGeometryPass;
        bool previousBatching = _gpuBatchingDynamicGeometry;
        GpuDynamicBatchKind previousBatch = _gpuCurrentDynamicBatch;
        bool previousUseProfileColors = _useProfileColorsForVehiclePreview;
        double previousAngle = entity.AngleDeg;
        double previousTurretYaw = entity.TurretYawDeg;
        double previousPitch = entity.GimbalPitchDeg;
        List<GpuVertex> preservedDynamicVertices = new(_gpuDynamicVertexBuildBuffer);
        List<GpuVertex>? builtVertices = null;

        try
        {
            _gpuDynamicVertexBuildBuffer.Clear();
            _gpuBatchingDynamicGeometry = true;
            _gpuGeometryPass = true;
            _gpuCurrentDynamicBatch = GpuDynamicBatchKind.Entity;
            _useProfileColorsForVehiclePreview = false;
            entity.AngleDeg = 0.0;
            entity.TurretYawDeg = SimulationCombatMath.NormalizeDeg(previousTurretYaw - previousAngle);

            using Bitmap scratchBitmap = new(1, 1, PixelFormat.Format32bppPArgb);
            using Graphics scratchGraphics = Graphics.FromImage(scratchBitmap);
            DrawEntityAppearanceModelModern(scratchGraphics, entity, Vector3.Zero, profile);
            if (_gpuDynamicVertexBuildBuffer.Count > 0)
            {
                builtVertices = new List<GpuVertex>(_gpuDynamicVertexBuildBuffer);
            }
        }
        finally
        {
            if (_gpuDynamicVertexBuildBuffer.Count != preservedDynamicVertices.Count
                || preservedDynamicVertices.Count > 0)
            {
                _gpuDynamicVertexBuildBuffer.Clear();
                _gpuDynamicVertexBuildBuffer.AddRange(preservedDynamicVertices);
            }

            entity.AngleDeg = previousAngle;
            entity.TurretYawDeg = previousTurretYaw;
            entity.GimbalPitchDeg = previousPitch;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _gpuGeometryPass = previousGeometryPass;
            _gpuBatchingDynamicGeometry = previousBatching;
            _gpuCurrentDynamicBatch = previousBatch;
            _useProfileColorsForVehiclePreview = previousUseProfileColors;
        }

        if (builtVertices is null || builtVertices.Count <= 0)
        {
            return false;
        }

        _glGenBuffers!(1, out int buffer);
        UploadGpuVertexBuffer(buffer, builtVertices, GlStaticDraw);
        cache = new GpuEntityAppearanceMeshCache
        {
            Buffer = buffer,
            VertexCount = builtVertices.Count,
            HeightM = EstimateGpuEntityAppearanceHeight(entity, profile),
            LastUsedFrame = _terrainCacheGpuFrameIndex,
        };
        _gpuEntityAppearanceMeshCache[cacheKey] = cache;
        TrimGpuEntityAppearanceMeshCache();
        DrawGpuVertexBuffer(cache.Buffer, cache.VertexCount, ResolveGpuEntityAppearanceModelMatrix(entity, center));
        return true;
    }

    private static bool RequiresWorldSpaceGimbalRendering(SimulationEntity entity)
        => false;

    private static Matrix4x4 ResolveGpuEntityAppearanceModelMatrix(SimulationEntity entity, Vector3 center)
    {
        float yaw = -(float)(entity.AngleDeg * Math.PI / 180.0);
        return Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, yaw) * Matrix4x4.CreateTranslation(center);
    }

    private string BuildGpuEntityAppearanceMeshCacheKey(SimulationEntity entity, RobotAppearanceProfile profile)
    {
        RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(entity);
        IReadOnlyList<RenderWheelComponent> wheels = ResolveWheelComponents(entity, profile, motion);
        double bakedTurretYaw = SimulationCombatMath.NormalizeSignedDeg(entity.TurretYawDeg - entity.AngleDeg);
        var builder = new System.Text.StringBuilder(384);
        builder.Append(entity.RoleKey).Append('|')
            .Append(profile.RoleKey).Append('|')
            .Append(profile.ChassisSubtype).Append('|')
            .Append(profile.BodyShape).Append('|')
            .Append(profile.WheelStyle).Append('|')
            .Append(profile.SuspensionStyle).Append('|')
            .Append(profile.ArmStyle).Append('|')
            .Append(profile.FrontClimbAssistStyle).Append('|')
            .Append(profile.RearClimbAssistStyle).Append('|')
            .Append(Math.Round(profile.BodyLengthM, 3)).Append('|')
            .Append(Math.Round(profile.BodyWidthM, 3)).Append('|')
            .Append(Math.Round(profile.BodyHeightM, 3)).Append('|')
            .Append(Math.Round(profile.GimbalLengthM, 3)).Append('|')
            .Append(Math.Round(profile.GimbalWidthM, 3)).Append('|')
            .Append(Math.Round(profile.GimbalBodyHeightM, 3)).Append('|')
            .Append(Math.Round(profile.BarrelLengthM, 3)).Append('|')
            .Append(Math.Round(profile.BarrelRadiusM, 3)).Append('|')
            .Append(Math.Round(entity.ChassisPitchDeg, 1)).Append('|')
            .Append(Math.Round(entity.ChassisRollDeg, 1)).Append('|')
            .Append(Math.Round(bakedTurretYaw, 1)).Append('|')
            .Append(Math.Round(entity.GimbalPitchDeg, 1)).Append('|')
            .Append(Math.Round(motion.BodyLiftM, 3)).Append('|')
            .Append(Math.Round(motion.FrontDropM, 3)).Append('|')
            .Append(Math.Round(motion.FrontRaiseM, 3)).Append('|')
            .Append(Math.Round(motion.RearFootRaiseM, 3)).Append('|')
            .Append(Math.Round(motion.RearFootReachM, 3)).Append('|')
            .Append(entity.TraversalActive ? '1' : '0').Append('|')
            .Append(Math.Round(entity.TraversalProgress, 2)).Append('|')
            .Append(Math.Round(entity.JumpCrouchTimerSec, 2)).Append('|')
            .Append(Math.Round(entity.AirborneHeightM, 2)).Append('|')
            .Append(Math.Round(entity.VerticalVelocityMps, 2)).Append('|')
            .Append(entity.SmallGyroActive ? '1' : '0');
        for (int index = 0; index < wheels.Count; index++)
        {
            RenderWheelComponent wheel = wheels[index];
            builder.Append('|').Append(index).Append(':').Append(Math.Round(wheel.SpinRad, 2));
        }

        return builder.ToString();
    }

    private float EstimateGpuEntityAppearanceHeight(SimulationEntity entity, RobotAppearanceProfile profile)
    {
        RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(entity);
        float bodyBase = Math.Max(0f, profile.BodyClearanceM + motion.BodyLiftM);
        float bodyHeight = Math.Max(0.08f, profile.BodyHeightM);
        float maxHeight = bodyBase + bodyHeight;
        maxHeight = Math.Max(maxHeight, bodyBase + bodyHeight * 0.72f + Math.Max(0.015f, bodyHeight * 0.12f));
        maxHeight = Math.Max(maxHeight, bodyBase + bodyHeight + 0.03f);
        maxHeight = Math.Max(maxHeight, bodyBase + profile.GimbalMountGapM + profile.GimbalMountHeightM + profile.GimbalBodyHeightM + profile.BarrelLengthM * 0.6f);
        return Math.Max(0.18f, maxHeight);
    }

    private float ResolveCachedGpuEntityAppearanceHeight(SimulationEntity entity, RobotAppearanceProfile profile)
    {
        string cacheKey = BuildGpuEntityAppearanceMeshCacheKey(entity, profile);
        return _gpuEntityAppearanceMeshCache.TryGetValue(cacheKey, out GpuEntityAppearanceMeshCache? cache)
            ? Math.Max(0.18f, cache.HeightM)
            : EstimateGpuEntityAppearanceHeight(entity, profile);
    }

    private void TrimGpuEntityAppearanceMeshCache()
    {
        const int maxCachedMeshes = 192;
        if (_gpuEntityAppearanceMeshCache.Count <= maxCachedMeshes)
        {
            return;
        }

        foreach (KeyValuePair<string, GpuEntityAppearanceMeshCache> entry in _gpuEntityAppearanceMeshCache
            .OrderBy(pair => pair.Value.LastUsedFrame)
            .Take(_gpuEntityAppearanceMeshCache.Count - maxCachedMeshes)
            .ToArray())
        {
            GpuEntityAppearanceMeshCache cache = entry.Value;
            if (cache.Buffer != 0)
            {
                DeleteGpuBuffer(ref cache.Buffer);
            }

            _gpuEntityAppearanceMeshCache.Remove(entry.Key);
        }
    }

    private void ClearGpuEntityAppearanceMeshCache()
    {
        foreach (GpuEntityAppearanceMeshCache cache in _gpuEntityAppearanceMeshCache.Values)
        {
            DeleteGpuBuffer(ref cache.Buffer);
        }

        _gpuEntityAppearanceMeshCache.Clear();
    }

    private void DrawGpuIndexedVertexBuffer(int vertexBuffer, int indexBuffer, int indexCount)
    {
        if (indexCount <= 0 || vertexBuffer == 0 || indexBuffer == 0 || _glBindBuffer is null)
        {
            return;
        }

        int stride = Marshal.SizeOf<GpuVertex>();
        if (_gpuSharedVertexArray == 0 && _glGenVertexArrays is not null)
        {
            _glGenVertexArrays(1, out _gpuSharedVertexArray);
        }

        _glBindVertexArray?.Invoke(_gpuSharedVertexArray);
        _glBindBuffer(GlArrayBuffer, vertexBuffer);
        _glBindBuffer(GlElementArrayBuffer, indexBuffer);
        ConfigureGpuNativeLighting(false, _lightingSettings);
        glEnableClientState(GlVertexArray);
        glEnableClientState(GlColorArray);
        glVertexPointer(3, GlFloat, stride, IntPtr.Zero);
        glColorPointer(4, GlUnsignedByte, stride, new IntPtr(12));
        glDrawElements(GlTriangles, indexCount, GlUnsignedInt, IntPtr.Zero);
        glDisableClientState(GlColorArray);
        glDisableClientState(GlVertexArray);
        _glBindBuffer(GlElementArrayBuffer, 0);
        _glBindBuffer(GlArrayBuffer, 0);
        _glBindVertexArray?.Invoke(0);
    }

    private void SyncGpuLightingSettings()
    {
        Simulator3dLightingSettings latest = _host.GetLightingSettings();
        _lightingSettings = latest;
        _gpuKeyLightPosition[0] = latest.KeyDirectionX;
        _gpuKeyLightPosition[1] = latest.KeyDirectionY;
        _gpuKeyLightPosition[2] = latest.KeyDirectionZ;
        _gpuKeyLightAmbient[0] = latest.KeyAmbientR;
        _gpuKeyLightAmbient[1] = latest.KeyAmbientG;
        _gpuKeyLightAmbient[2] = latest.KeyAmbientB;
        _gpuKeyLightDiffuse[0] = latest.KeyDiffuseR;
        _gpuKeyLightDiffuse[1] = latest.KeyDiffuseG;
        _gpuKeyLightDiffuse[2] = latest.KeyDiffuseB;
        _gpuKeyLightSpecular[0] = latest.KeySpecularR;
        _gpuKeyLightSpecular[1] = latest.KeySpecularG;
        _gpuKeyLightSpecular[2] = latest.KeySpecularB;
        _gpuFillLightPosition[0] = latest.FillDirectionX;
        _gpuFillLightPosition[1] = latest.FillDirectionY;
        _gpuFillLightPosition[2] = latest.FillDirectionZ;
        _gpuFillLightAmbient[0] = latest.FillAmbientR;
        _gpuFillLightAmbient[1] = latest.FillAmbientG;
        _gpuFillLightAmbient[2] = latest.FillAmbientB;
        _gpuFillLightDiffuse[0] = latest.FillDiffuseR;
        _gpuFillLightDiffuse[1] = latest.FillDiffuseG;
        _gpuFillLightDiffuse[2] = latest.FillDiffuseB;
        _gpuFillLightSpecular[0] = latest.FillSpecularR;
        _gpuFillLightSpecular[1] = latest.FillSpecularG;
        _gpuFillLightSpecular[2] = latest.FillSpecularB;
        _gpuRobotMaterialSpecular[0] = latest.MaterialSpecularR;
        _gpuRobotMaterialSpecular[1] = latest.MaterialSpecularG;
        _gpuRobotMaterialSpecular[2] = latest.MaterialSpecularB;
    }

    private void ConfigureGpuNativeLighting(bool enabled, Simulator3dLightingSettings settings)
    {
        if (!enabled)
        {
            glDisable(GlColorMaterial);
            glDisable(GlNormalize);
            glDisable(GlLight1);
            glDisable(GlLight0);
            glDisable(GlLighting);
            return;
        }

        glShadeModel(GlSmooth);
        glEnable(GlLighting);
        glEnable(GlLight0);
        glEnable(GlLight1);
        glEnable(GlColorMaterial);
        glColorMaterial(GlFrontAndBack, GlAmbientAndDiffuse);
        glLightfv(GlLight0, GlPosition, _gpuKeyLightPosition);
        glLightfv(GlLight0, GlAmbient, _gpuKeyLightAmbient);
        glLightfv(GlLight0, GlDiffuse, _gpuKeyLightDiffuse);
        glLightfv(GlLight0, GlSpecular, _gpuKeyLightSpecular);
        glLightfv(GlLight1, GlPosition, _gpuFillLightPosition);
        glLightfv(GlLight1, GlAmbient, _gpuFillLightAmbient);
        glLightfv(GlLight1, GlDiffuse, _gpuFillLightDiffuse);
        glLightfv(GlLight1, GlSpecular, _gpuFillLightSpecular);
        glMaterialfv(GlFrontAndBack, GlSpecular, _gpuRobotMaterialSpecular);
        glMaterialf(GlFrontAndBack, GlShininess, settings.MaterialShininess);
    }

    private void DrawGpuVerticesImmediate(IReadOnlyList<GpuVertex> vertices, bool useNativeLighting = false)
    {
        bool enableLighting = useNativeLighting && _lightingSettings.Enabled;
        ConfigureGpuNativeLighting(enableLighting, _lightingSettings);
        glBegin(GlTriangles);
        foreach (GpuVertex vertex in vertices)
        {
            glColor4ub(vertex.R, vertex.G, vertex.B, vertex.A);
            glNormal3f(vertex.Nx, vertex.Ny, vertex.Nz);
            glVertex3f(vertex.X, vertex.Y, vertex.Z);
        }

        glEnd();
        ConfigureGpuNativeLighting(false, _lightingSettings);
    }

    private static void AppendGpuTriangle(List<GpuVertex> target, Vector3 a, Vector3 b, Vector3 c, Color color, bool matteMaterial = false)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() > 1e-8f)
        {
            normal = Vector3.Normalize(normal);
        }
        else
        {
            normal = Vector3.UnitY;
        }

        target.Add(new GpuVertex(a, matteMaterial ? ApplyMatteSurfaceColor(color, a, normal) : color, normal));
        target.Add(new GpuVertex(b, matteMaterial ? ApplyMatteSurfaceColor(color, b, normal) : color, normal));
        target.Add(new GpuVertex(c, matteMaterial ? ApplyMatteSurfaceColor(color, c, normal) : color, normal));
    }

    private static void AppendGpuQuad(List<GpuVertex> target, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, bool matteMaterial = false)
    {
        AppendGpuTriangle(target, a, b, c, color, matteMaterial);
        AppendGpuTriangle(target, a, c, d, color, matteMaterial);
    }

    private static void AppendGpuTerrainTriangle(List<GpuVertex> target, Vector3 a, Vector3 b, Vector3 c, Color color, bool useBakedLighting)
    {
        Color shaded = ShadeGpuFaceColor(color, a, b, c, 0.56f);
        AppendGpuTriangle(target, a, b, c, ApplyBakedArenaPointLight(shaded, a, b, c, 28f, 15f, useBakedLighting));
    }

    private static void AppendGpuTerrainQuad(List<GpuVertex> target, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, bool useBakedLighting)
    {
        AppendGpuTerrainTriangle(target, a, b, c, color, useBakedLighting);
        AppendGpuTerrainTriangle(target, a, c, d, color, useBakedLighting);
    }

    private static void AppendGpuPolygon(List<GpuVertex> target, IReadOnlyList<Vector3> vertices, Color color, bool matteMaterial = false)
    {
        if (vertices.Count < 3)
        {
            return;
        }

        for (int index = 1; index < vertices.Count - 1; index++)
        {
            AppendGpuTriangle(target, vertices[0], vertices[index], vertices[index + 1], color, matteMaterial);
        }
    }

    private static void AppendGpuSphere(
        List<GpuVertex> target,
        Vector3 center,
        float radius,
        Color color,
        int slices,
        int stacks)
    {
        int safeSlices = Math.Max(5, slices);
        int safeStacks = Math.Max(3, stacks);
        for (int stack = 0; stack < safeStacks; stack++)
        {
            float v0 = stack / (float)safeStacks;
            float v1 = (stack + 1) / (float)safeStacks;
            float phi0 = (v0 - 0.5f) * MathF.PI;
            float phi1 = (v1 - 0.5f) * MathF.PI;
            for (int slice = 0; slice < safeSlices; slice++)
            {
                float u0 = slice / (float)safeSlices;
                float u1 = (slice + 1) / (float)safeSlices;
                float theta0 = u0 * MathF.Tau;
                float theta1 = u1 * MathF.Tau;
                Vector3 a = center + ResolveSpherePoint(radius, theta0, phi0);
                Vector3 b = center + ResolveSpherePoint(radius, theta1, phi0);
                Vector3 c = center + ResolveSpherePoint(radius, theta1, phi1);
                Vector3 d = center + ResolveSpherePoint(radius, theta0, phi1);
                Color shaded = ShadeGpuFaceColor(color, a, b, c, 0.72f);
                AppendGpuQuad(target, a, b, c, d, shaded);
            }
        }
    }

    private static void AppendGpuProjectileBillboard(
        List<GpuVertex> target,
        Vector3 center,
        float radius,
        Color rimColor,
        Color coreColor,
        int segments,
        Vector3 cameraRight,
        Vector3 cameraUp)
    {
        int safeSegments = Math.Max(4, segments);
        for (int index = 0; index < safeSegments; index++)
        {
            float angle0 = index / (float)safeSegments * MathF.Tau;
            float angle1 = (index + 1) / (float)safeSegments * MathF.Tau;
            Vector3 edge0 = center + cameraRight * (MathF.Cos(angle0) * radius) + cameraUp * (MathF.Sin(angle0) * radius);
            Vector3 edge1 = center + cameraRight * (MathF.Cos(angle1) * radius) + cameraUp * (MathF.Sin(angle1) * radius);

            target.Add(new GpuVertex(center, coreColor));
            target.Add(new GpuVertex(edge0, rimColor));
            target.Add(new GpuVertex(edge1, rimColor));
        }
    }

    private static Color ScaleGpuColor(Color color, float scale)
    {
        return Color.FromArgb(
            color.A,
            Math.Clamp((int)MathF.Round(color.R * scale), 0, 255),
            Math.Clamp((int)MathF.Round(color.G * scale), 0, 255),
            Math.Clamp((int)MathF.Round(color.B * scale), 0, 255));
    }

    private static Color ShadeGpuFaceColor(Color color, Vector3 a, Vector3 b, Vector3 c, float ambient)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() <= 1e-8f)
        {
            return color;
        }

        normal = Vector3.Normalize(normal);
        Vector3 keyLight = Vector3.Normalize(new Vector3(-0.42f, 1.12f, -0.30f));
        Vector3 rimLight = Vector3.Normalize(new Vector3(0.55f, 0.76f, 0.48f));
        float keyDiffuse = MathF.Max(0f, Vector3.Dot(normal, keyLight));
        float rimDiffuse = MathF.Max(0f, Vector3.Dot(normal, rimLight));
        float diffuseFill = MathF.Abs(normal.Y) * 0.05f;
        float ambientFloor = MathF.Max(ambient, 0.43f);
        float brightness = ambientFloor + keyDiffuse * 0.40f + rimDiffuse * 0.16f + diffuseFill;
        Color lit = ScaleGpuColor(ApplyCoolSceneColor(color, 0.20f), Math.Clamp(brightness, 0.40f, 1.22f));
        lit = ApplyMetallicSheen(lit, normal, keyDiffuse, rimDiffuse);
        return ApplyCoolSceneColor(ApplyGpuTopPointLight(ApplyAmbientSceneLight(lit, 0.032f), a, b, c, 28f, 15f, 0.30f), 0.24f);
    }

    private static Color ApplyBakedArenaPointLight(
        Color color,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        float sceneWidthM,
        float sceneDepthM,
        bool enabled)
    {
        if (!enabled)
        {
            return color;
        }

        Vector3 normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() <= 1e-8f)
        {
            return color;
        }

        normal = Vector3.Normalize(normal);
        Vector3 center = (a + b + c) / 3f;
        Vector3 keyLightPosition = new(
            Math.Max(1f, sceneWidthM) * 0.5f,
            8.8f,
            Math.Max(1f, sceneDepthM) * 0.5f);
        Vector3 toLight = keyLightPosition - center;
        float distanceSq = Math.Max(1e-4f, toLight.LengthSquared());
        Vector3 lightDir = toLight / MathF.Sqrt(distanceSq);
        float direct = MathF.Max(0f, Vector3.Dot(normal, lightDir));
        float attenuation = 1f / (1f + distanceSq / 145f);
        float sky = Math.Clamp(normal.Y * 0.5f + 0.5f, 0f, 1f);
        float bounce = Math.Clamp(1f - MathF.Abs(normal.Y), 0f, 1f) * 0.055f;
        float bakedBrightness = Math.Clamp(0.83f + sky * 0.12f + bounce + direct * attenuation * 0.74f, 0.68f, 1.42f);
        Color lit = ScaleGpuColor(color, bakedBrightness);
        float coolLift = Math.Clamp(direct * attenuation * 0.18f + sky * 0.040f, 0f, 0.18f);
        int r = Math.Clamp((int)MathF.Round(lit.R + (178 - lit.R) * coolLift * 0.48f), 0, 255);
        int g = Math.Clamp((int)MathF.Round(lit.G + (218 - lit.G) * coolLift * 0.88f), 0, 255);
        int bComponent = Math.Clamp((int)MathF.Round(lit.B + (255 - lit.B) * coolLift), 0, 255);
        return ApplyCoolSceneColor(Color.FromArgb(lit.A, r, g, bComponent), enabled ? 0.42f : 0.20f);
    }

    private static Color ApplyGpuTopPointLight(
        Color color,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        float sceneWidthM,
        float sceneDepthM,
        float intensity)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() <= 1e-8f)
        {
            return color;
        }

        normal = Vector3.Normalize(normal);
        Vector3 center = (a + b + c) / 3f;
        Vector3 lightPosition = new(sceneWidthM * 0.5f, 8.5f, sceneDepthM * 0.5f);
        Vector3 toLight = lightPosition - center;
        float distanceSquared = Math.Max(1e-4f, toLight.LengthSquared());
        Vector3 lightDirection = toLight / MathF.Sqrt(distanceSquared);
        float diffuse = MathF.Max(0f, Vector3.Dot(normal, lightDirection));
        float attenuation = 1f / (1f + distanceSquared / 320f);
        float lift = Math.Clamp((diffuse * 0.92f + 0.18f) * attenuation * intensity, 0f, 0.34f);
        if (lift <= 1e-5f)
        {
            return color;
        }

        int r = Math.Clamp((int)MathF.Round(color.R + (196 - color.R) * lift * 0.88f), 0, 255);
        int g = Math.Clamp((int)MathF.Round(color.G + (224 - color.G) * lift), 0, 255);
        int bOut = Math.Clamp((int)MathF.Round(color.B + (255 - color.B) * lift), 0, 255);
        return Color.FromArgb(color.A, r, g, bOut);
    }

    private static void SetGpuColor(Color color)
    {
        glColor4ub(color.R, color.G, color.B, color.A);
    }

    private void AppendOrDrawGpuTriangle(Vector3 a, Vector3 b, Vector3 c, Color color, bool matteMaterial = false)
    {
        if (_gpuBatchingDynamicGeometry)
        {
            AppendGpuTriangle(CurrentGpuDynamicBuildBuffer(), a, b, c, color, matteMaterial);
            return;
        }

        DrawGpuTriangle(a, b, c, matteMaterial ? ApplyMatteSurfaceColor(color, (a + b + c) / 3f, Vector3.Cross(b - a, c - a)) : color);
    }

    private void AppendOrDrawGpuQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, bool matteMaterial = false)
    {
        if (_gpuBatchingDynamicGeometry)
        {
            AppendGpuQuad(CurrentGpuDynamicBuildBuffer(), a, b, c, d, color, matteMaterial);
            return;
        }

        DrawGpuQuad(a, b, c, d, matteMaterial ? ApplyMatteSurfaceColor(color, (a + b + c + d) * 0.25f, Vector3.Cross(b - a, c - a)) : color);
    }

    private List<GpuVertex> CurrentGpuDynamicBuildBuffer()
        => _gpuEnergyMechanismBatchActive || _gpuCurrentDynamicBatch == GpuDynamicBatchKind.Facility
            ? _gpuEnergyMechanismVertexBuildBuffer
            : _gpuCurrentDynamicBatch == GpuDynamicBatchKind.Projectile
                ? _gpuProjectileVertexBuildBuffer
                : _gpuDynamicVertexBuildBuffer;

    private static void DrawGpuTriangle(Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        SetGpuColor(color);
        glBegin(GlTriangles);
        glVertex3f(a.X, a.Y, a.Z);
        glVertex3f(b.X, b.Y, b.Z);
        glVertex3f(c.X, c.Y, c.Z);
        glEnd();
    }

    private static void DrawGpuQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        SetGpuColor(color);
        glBegin(GlQuads);
        glVertex3f(a.X, a.Y, a.Z);
        glVertex3f(b.X, b.Y, b.Z);
        glVertex3f(c.X, c.Y, c.Z);
        glVertex3f(d.X, d.Y, d.Z);
        glEnd();
    }

    private static void DrawGpuLine(Vector3 a, Vector3 b, Color color)
    {
        SetGpuColor(color);
        glBegin(GlLines);
        glVertex3f(a.X, a.Y, a.Z);
        glVertex3f(b.X, b.Y, b.Z);
        glEnd();
    }

    private static void DrawGpuPolyline(IReadOnlyList<Vector3> points, Color color)
    {
        if (points.Count < 2)
        {
            return;
        }

        SetGpuColor(color);
        glBegin(GlLines);
        for (int index = 1; index < points.Count; index++)
        {
            Vector3 a = points[index - 1];
            Vector3 b = points[index];
            glVertex3f(a.X, a.Y, a.Z);
            glVertex3f(b.X, b.Y, b.Z);
        }

        glEnd();
    }

    private static void DrawGpuPolyline(ReadOnlySpan<Vector3> points, Color color)
    {
        if (points.Length < 2)
        {
            return;
        }

        SetGpuColor(color);
        glBegin(GlLines);
        for (int index = 1; index < points.Length; index++)
        {
            Vector3 a = points[index - 1];
            Vector3 b = points[index];
            glVertex3f(a.X, a.Y, a.Z);
            glVertex3f(b.X, b.Y, b.Z);
        }

        glEnd();
    }

    private static float[] ToOpenGlMatrix(Matrix4x4 matrix)
    {
        return
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ];
    }

    private void DrawGpuSolidFaces(IReadOnlyList<SolidFace> faces, Color fillColor, Color edgeColor, bool matteMaterial = false)
    {
        if (_gpuBatchingDynamicGeometry)
        {
            List<GpuVertex> target = CurrentGpuDynamicBuildBuffer();
            foreach (SolidFace face in faces)
            {
                if (ShouldCullSolidFace(face.Vertices))
                {
                    continue;
                }

                Color shaded = ShadeFaceColor(fillColor, face.Vertices, face.Ambient, matteMaterial);
                AppendGpuPolygon(target, face.Vertices, shaded, matteMaterial);
            }

            return;
        }

        foreach (SolidFace face in faces)
        {
            if (ShouldCullSolidFace(face.Vertices))
            {
                continue;
            }

            Color shaded = ShadeFaceColor(fillColor, face.Vertices, face.Ambient, matteMaterial);
            DrawGpuPolygon(face.Vertices, shaded);
            DrawGpuPolygonOutline(face.Vertices, edgeColor);
        }
    }

    private bool ShouldCullSolidFace(IReadOnlyList<Vector3> vertices)
    {
        if (vertices.Count < 3)
        {
            return true;
        }

        Vector3 faceNormal = Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]);
        if (faceNormal.LengthSquared() <= 1e-8f)
        {
            return true;
        }

        Vector3 faceCenter = ResolveFaceCenter(vertices);
        Vector3 toCamera = _cameraPositionM - faceCenter;
        if (toCamera.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        return Vector3.Dot(faceNormal, toCamera) <= 1e-6f;
    }

    private static void DrawGpuPolygon(IReadOnlyList<Vector3> vertices, Color color)
    {
        if (vertices.Count < 3)
        {
            return;
        }

        SetGpuColor(color);
        glBegin(GlTriangles);
        for (int index = 1; index < vertices.Count - 1; index++)
        {
            Vector3 a = vertices[0];
            Vector3 b = vertices[index];
            Vector3 c = vertices[index + 1];
            glVertex3f(a.X, a.Y, a.Z);
            glVertex3f(b.X, b.Y, b.Z);
            glVertex3f(c.X, c.Y, c.Z);
        }

        glEnd();
    }

    private static void DrawGpuPolygonOutline(IReadOnlyList<Vector3> vertices, Color color)
    {
        if (vertices.Count < 2)
        {
            return;
        }

        SetGpuColor(color);
        glBegin(GlLineLoop);
        foreach (Vector3 vertex in vertices)
        {
            glVertex3f(vertex.X, vertex.Y, vertex.Z);
        }

        glEnd();
    }

    private void EnsureGpuOverlaySurface()
    {
        float overlayScale = ResolveGpuOverlaySurfaceScale();
        Size logicalSize = ClientSize;
        int physicalWidth = Math.Max(1, (int)MathF.Ceiling(logicalSize.Width * overlayScale));
        int physicalHeight = Math.Max(1, (int)MathF.Ceiling(logicalSize.Height * overlayScale));
        if (_gpuOverlayBitmap is not null
            && _gpuOverlayGraphics is not null
            && _gpuOverlayBitmap.Width == physicalWidth
            && _gpuOverlayBitmap.Height == physicalHeight
            && _gpuOverlayLogicalSize == logicalSize
            && Math.Abs(_gpuOverlaySurfaceScale - overlayScale) <= 1e-3f)
        {
            return;
        }

        _gpuOverlayGraphics?.Dispose();
        _gpuOverlayGraphics = null;
        _gpuOverlayBitmap?.Dispose();
        _gpuOverlayBitmap = null;
        _gpuOverlaySceneGraphics?.Dispose();
        _gpuOverlaySceneGraphics = null;
        _gpuOverlaySceneBitmap?.Dispose();
        _gpuOverlaySceneBitmap = null;
        _gpuOverlayUiGraphics?.Dispose();
        _gpuOverlayUiGraphics = null;
        _gpuOverlayUiBitmap?.Dispose();
        _gpuOverlayUiBitmap = null;

        if (logicalSize.Width <= 0 || logicalSize.Height <= 0)
        {
            return;
        }

        _gpuOverlaySurfaceScale = overlayScale;
        _gpuOverlayLogicalSize = logicalSize;
        _gpuOverlayBitmap = new Bitmap(physicalWidth, physicalHeight, PixelFormat.Format32bppArgb);
        _gpuOverlayGraphics = Graphics.FromImage(_gpuOverlayBitmap);
        ConfigureGpuOverlayGraphics(_gpuOverlayGraphics);
        _gpuOverlayTextureSize = Size.Empty;
        _lastGpuOverlayUploadTicks = 0;
    }

    private void EnsureGpuOverlayMatchSurfaces()
    {
        float overlayScale = ResolveGpuOverlaySurfaceScale();
        Size logicalSize = ClientSize;
        int physicalWidth = Math.Max(1, (int)MathF.Ceiling(logicalSize.Width * overlayScale));
        int physicalHeight = Math.Max(1, (int)MathF.Ceiling(logicalSize.Height * overlayScale));
        bool sceneReady = _gpuOverlaySceneBitmap is not null
            && _gpuOverlaySceneGraphics is not null
            && _gpuOverlaySceneBitmap.Width == physicalWidth
            && _gpuOverlaySceneBitmap.Height == physicalHeight;
        bool uiReady = _gpuOverlayUiBitmap is not null
            && _gpuOverlayUiGraphics is not null
            && _gpuOverlayUiBitmap.Width == physicalWidth
            && _gpuOverlayUiBitmap.Height == physicalHeight;
        if (sceneReady
            && uiReady
            && _gpuOverlayLogicalSize == logicalSize
            && Math.Abs(_gpuOverlaySurfaceScale - overlayScale) <= 1e-3f)
        {
            return;
        }

        _gpuOverlaySceneGraphics?.Dispose();
        _gpuOverlaySceneGraphics = null;
        _gpuOverlaySceneBitmap?.Dispose();
        _gpuOverlaySceneBitmap = null;
        _gpuOverlayUiGraphics?.Dispose();
        _gpuOverlayUiGraphics = null;
        _gpuOverlayUiBitmap?.Dispose();
        _gpuOverlayUiBitmap = null;
        if (logicalSize.Width <= 0 || logicalSize.Height <= 0)
        {
            return;
        }

        _gpuOverlaySurfaceScale = overlayScale;
        _gpuOverlayLogicalSize = logicalSize;
        _gpuOverlaySceneBitmap = new Bitmap(physicalWidth, physicalHeight, PixelFormat.Format32bppArgb);
        _gpuOverlaySceneGraphics = Graphics.FromImage(_gpuOverlaySceneBitmap);
        ConfigureGpuOverlayGraphics(_gpuOverlaySceneGraphics);
        _gpuOverlayUiBitmap = new Bitmap(physicalWidth, physicalHeight, PixelFormat.Format32bppArgb);
        _gpuOverlayUiGraphics = Graphics.FromImage(_gpuOverlayUiBitmap);
        ConfigureGpuOverlayGraphics(_gpuOverlayUiGraphics);
        _gpuOverlaySceneTextureSize = Size.Empty;
        _gpuOverlayUiTextureSize = Size.Empty;
        _lastGpuOverlaySceneUploadTicks = 0;
        _lastGpuOverlayUiUploadTicks = 0;
        _gpuOverlaySceneDirty = true;
        _gpuOverlayUiDirty = true;
    }

    private float ResolveGpuOverlaySurfaceScale()
    {
        return 1f;
    }

    private void UploadGpuOverlayBitmap()
    {
        UploadGpuOverlayBitmap(_gpuOverlayBitmap, ref _gpuOverlayTexture, ref _gpuOverlayTextureSize);
    }

    private void UploadGpuOverlayBitmap(Bitmap? bitmap, ref int texture, ref Size textureSize)
    {
        if (bitmap is null)
        {
            return;
        }

        Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (texture == 0)
            {
                glGenTextures(1, out texture);
            }

            glBindTexture(GlTexture2D, texture);
            glTexParameteri(GlTexture2D, GlTextureMinFilter, GlNearest);
            glTexParameteri(GlTexture2D, GlTextureMagFilter, GlNearest);
            if (textureSize != bitmap.Size)
            {
                glTexImage2D(GlTexture2D, 0, GlRgba, bitmap.Width, bitmap.Height, 0, GlBgra, GlUnsignedByte, data.Scan0);
                textureSize = bitmap.Size;
            }
            else
            {
                glTexSubImage2D(GlTexture2D, 0, 0, 0, bitmap.Width, bitmap.Height, GlBgra, GlUnsignedByte, data.Scan0);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void PresentGpuOverlayTexture()
    {
        PresentGpuOverlayTexture(_gpuOverlayTexture);
    }

    private void PresentGpuOverlayTexture(int texture)
    {
        if (texture == 0)
        {
            return;
        }

        glDisable(GlDepthTest);
        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glEnable(GlBlend);
        glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);
        glEnable(GlTexture2D);
        glBindTexture(GlTexture2D, texture);
        glColor4ub(255, 255, 255, 255);
        glBegin(GlQuads);
        glTexCoord2f(0f, 0f);
        glVertex3f(-1f, 1f, 0f);
        glTexCoord2f(1f, 0f);
        glVertex3f(1f, 1f, 0f);
        glTexCoord2f(1f, 1f);
        glVertex3f(1f, -1f, 0f);
        glTexCoord2f(0f, 1f);
        glVertex3f(-1f, -1f, 0f);
        glEnd();
        glDisable(GlTexture2D);
    }

    private void DisposeGpuRenderer()
    {
        _gpuExternalScratchGraphics?.Dispose();
        _gpuExternalScratchGraphics = null;
        _gpuExternalScratchBitmap?.Dispose();
        _gpuExternalScratchBitmap = null;
        _gpuOverlayGraphics?.Dispose();
        _gpuOverlayGraphics = null;
        _gpuOverlayBitmap?.Dispose();
        _gpuOverlayBitmap = null;

        DeleteGpuBuffer(ref _gpuTerrainVertexBuffer);
        foreach (GpuTerrainChunk chunk in _gpuTerrainChunks)
        {
            DeleteGpuBuffer(ref chunk.Buffer);
            chunk.VertexCount = 0;
            chunk.Version = -1;
            chunk.BuildBuffer.Clear();
        }

        ReleaseTerrainCacheGpuChunks(deleteBuffers: true, clearSource: true);
        DeleteGpuBuffer(ref _gpuDynamicVertexBuffer);
        DeleteGpuBuffer(ref _gpuEnergyMechanismVertexBuffer);
        DeleteGpuBuffer(ref _gpuProjectileVertexBuffer);
        ClearGpuEntityAppearanceMeshCache();
        DeleteGpuVertexArray(ref _gpuSharedVertexArray);
        _gpuTerrainVertexCount = 0;
        _gpuDynamicVertexCapacity = 0;
        _gpuEnergyMechanismVertexCapacity = 0;
        _gpuProjectileVertexCapacity = 0;
        _gpuTerrainBufferVersion = -1;

        if (_gpuOverlayTexture != 0 && _gpuContextReady && MakeGpuContextCurrent())
        {
            int overlayTexture = _gpuOverlayTexture;
            glDeleteTextures(1, ref overlayTexture);
            _gpuOverlayTexture = 0;
        }
        if (_gpuOverlaySceneTexture != 0 && _gpuContextReady && MakeGpuContextCurrent())
        {
            int sceneTexture = _gpuOverlaySceneTexture;
            glDeleteTextures(1, ref sceneTexture);
            _gpuOverlaySceneTexture = 0;
        }
        if (_gpuOverlayUiTexture != 0 && _gpuContextReady && MakeGpuContextCurrent())
        {
            int uiTexture = _gpuOverlayUiTexture;
            glDeleteTextures(1, ref uiTexture);
            _gpuOverlayUiTexture = 0;
        }
        _gpuOverlayTextureSize = Size.Empty;
        _gpuOverlaySceneTextureSize = Size.Empty;
        _gpuOverlayUiTextureSize = Size.Empty;
        _gpuOverlayLogicalSize = Size.Empty;
        _gpuOverlaySurfaceScale = 1f;

        if (_gpuContextReady && MakeGpuContextCurrent())
        {
            DeleteGpuSceneRenderTarget();
        }
        else
        {
            _gpuSceneFramebuffer = 0;
            _gpuSceneDepthRenderbuffer = 0;
            _gpuSceneColorTexture = 0;
            _gpuSceneRenderTargetSize = Size.Empty;
        }

        if (_gpuHeroLobSubviewTexture != 0 && _gpuContextReady && MakeGpuContextCurrent())
        {
            int subviewTexture = _gpuHeroLobSubviewTexture;
            glDeleteTextures(1, ref subviewTexture);
            _gpuHeroLobSubviewTexture = 0;
        }

        _gpuHeroLobSubviewTextureSize = Size.Empty;
        _gpuHeroLobSubviewTextureUsesGrayscale = false;

        if (_gpuTerrainTexture != 0 && _gpuContextReady && MakeGpuContextCurrent())
        {
            int texture = _gpuTerrainTexture;
            glDeleteTextures(1, ref texture);
            _gpuTerrainTexture = 0;
        }

        if (!_gpuContextBorrowedExternally && _gpuRenderContext != IntPtr.Zero)
        {
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(_gpuRenderContext);
            _gpuRenderContext = IntPtr.Zero;
        }

        if (!_gpuContextBorrowedExternally && _gpuDeviceContext != IntPtr.Zero && IsHandleCreated)
        {
            ReleaseDC(Handle, _gpuDeviceContext);
            _gpuDeviceContext = IntPtr.Zero;
        }

        _gpuContextReady = false;
        _gpuContextBorrowedExternally = false;
    }

    private void InvalidateGpuTerrainBuffers(bool preserveTerrainCacheGpuChunks = false)
    {
        _gpuTerrainBufferVersion = -1;
        _gpuTerrainVertexCount = 0;
        _gpuTerrainVertexBuildBuffer.Clear();
        if (!preserveTerrainCacheGpuChunks)
        {
            ReleaseTerrainCacheGpuChunks(deleteBuffers: true, clearSource: true);
        }

        foreach (GpuTerrainChunk chunk in _gpuTerrainChunks)
        {
            chunk.VertexCount = 0;
            chunk.Version = -1;
        }
    }

    private void SetTerrainCacheGpuRenderSource(string sourcePath)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        string? annotationPath = ResolveTerrainCacheGpuAnnotationPath();
        long annotationTicks = annotationPath is not null && File.Exists(annotationPath)
            ? File.GetLastWriteTimeUtc(annotationPath).Ticks
            : 0L;
        if (string.Equals(_terrainCacheGpuSourcePath, fullPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_terrainCacheGpuAnnotationPath, annotationPath, StringComparison.OrdinalIgnoreCase)
            && _terrainCacheGpuAnnotationTicks == annotationTicks)
        {
            return;
        }

        ReleaseTerrainCacheGpuChunks(deleteBuffers: true, clearSource: true);
        _terrainCacheGpuSourcePath = fullPath;
        _terrainCacheGpuAnnotationPath = annotationPath;
        _terrainCacheGpuAnnotationTicks = annotationTicks;
        _terrainCacheGpuBuildFailed = false;
        _terrainCacheGpuLoadedSourcePath = null;
        _gpuTerrainBufferVersion = -1;
    }

    private string? ResolveTerrainCacheGpuAnnotationPath()
    {
        string annotationPath = _host.MapPreset.AnnotationPath;
        if (string.IsNullOrWhiteSpace(annotationPath))
        {
            return null;
        }

        if (Path.IsPathRooted(annotationPath))
        {
            return Path.GetFullPath(annotationPath);
        }

        string? mapDirectory = Path.GetDirectoryName(_host.MapPreset.SourcePath);
        return string.IsNullOrWhiteSpace(mapDirectory)
            ? Path.GetFullPath(annotationPath)
            : Path.GetFullPath(Path.Combine(mapDirectory, annotationPath));
    }

    private void ReleaseTerrainCacheGpuChunks(bool deleteBuffers, bool clearSource)
    {
        if (deleteBuffers)
        {
            foreach (TerrainCacheGpuChunk chunk in _terrainCacheGpuChunks)
            {
                DeleteGpuBuffer(ref chunk.Buffer);
                DeleteGpuBuffer(ref chunk.IndexBuffer);
            }
        }

        _terrainCacheGpuChunks.Clear();
        _terrainCacheGpuVisibleChunkScratch.Clear();
        _terrainCacheGpuChunkTree = null;
        _terrainCacheGpuColumns = 0;
        _terrainCacheGpuRows = 0;
        _terrainCacheGpuTotalTriangles = 0;
        _terrainCacheGpuResidentVertices = 0;
        _terrainCacheGpuVisibleVertices = 0;
        _terrainCacheGpuDrawCalls = 0;
        _terrainCacheGpuPendingUploads = 0;
        _terrainCacheGpuFrameIndex = 0;
        _lastTerrainCacheGpuLogTicks = 0;
        _terrainCacheGpuLoadedSourcePath = null;
        _terrainCacheGpuBuildFailed = false;
        if (clearSource)
        {
            _terrainCacheGpuSourcePath = null;
            _terrainCacheGpuAnnotationPath = null;
            _terrainCacheGpuAnnotationTicks = 0L;
            _terrainCacheGpuBuildTask = null;
            _terrainCacheGpuBuildingSourcePath = null;
        }
    }

    private void DeleteGpuBuffer(ref int buffer)
    {
        if (buffer == 0 || !_gpuContextReady || _glDeleteBuffers is null)
        {
            buffer = 0;
            return;
        }

        if (!MakeGpuContextCurrent())
        {
            buffer = 0;
            return;
        }

        int handle = buffer;
        _glDeleteBuffers(1, ref handle);
        buffer = 0;
    }

    private void DeleteGpuVertexArray(ref int vertexArray)
    {
        if (vertexArray == 0 || !_gpuContextReady || _glDeleteVertexArrays is null)
        {
            vertexArray = 0;
            return;
        }

        if (!MakeGpuContextCurrent())
        {
            vertexArray = 0;
            return;
        }

        int handle = vertexArray;
        _glDeleteVertexArrays(1, ref handle);
        vertexArray = 0;
    }

    private bool MakeGpuContextCurrent()
    {
        if (_gpuContextBorrowedExternally)
        {
            return true;
        }

        if (_gpuDeviceContext == IntPtr.Zero || _gpuRenderContext == IntPtr.Zero)
        {
            return false;
        }

        return wglMakeCurrent(_gpuDeviceContext, _gpuRenderContext);
    }

    private void EnsureExternalGpuScratchSurface()
    {
        if (_gpuExternalScratchBitmap is not null && _gpuExternalScratchGraphics is not null)
        {
            return;
        }

        _gpuExternalScratchBitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        _gpuExternalScratchGraphics = Graphics.FromImage(_gpuExternalScratchBitmap);
    }
}
