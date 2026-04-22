using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
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
    private const int GlLineLoop = 0x0002;
    private const int GlModelView = 0x1700;
    private const int GlProjection = 0x1701;
    private const int GlDepthTest = 0x0B71;
    private const int GlTexture2D = 0x0DE1;
    private const int GlBlend = 0x0BE2;
    private const int GlSrcAlpha = 0x0302;
    private const int GlOneMinusSrcAlpha = 0x0303;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlLinear = 0x2601;
    private const int GlRgba = 0x1908;
    private const int GlBgra = 0x80E1;
    private const int GlUnsignedByte = 0x1401;
    private const int GlFloat = 0x1406;
    private const int GlVertexArray = 0x8074;
    private const int GlColorArray = 0x8076;
    private const int GlArrayBuffer = 0x8892;
    private const int GlStaticDraw = 0x88E4;
    private const int GlDynamicDraw = 0x88E8;
    private const float GpuTerrainChunkSizeM = 128.0f;
    private const float GpuTerrainSmoothFlatSkipHeightM = 0.048f;
    private const double GpuOverlayUploadIntervalSec = 1.0 / 30.0;

    private enum GpuDynamicBatchKind
    {
        Entity,
        Facility,
        Projectile,
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
    private int _gpuTerrainTexture;
    private string? _gpuTerrainTexturePath;
    private Size _gpuTerrainTextureSize = Size.Empty;
    private Bitmap? _gpuOverlayBitmap;
    private Graphics? _gpuOverlayGraphics;
    private int _gpuOverlayTexture;
    private Size _gpuOverlayTextureSize = Size.Empty;
    private long _lastGpuOverlayUploadTicks;
    private bool _lastGpuOverlayPausedState;
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
    private readonly List<GpuVertex> _gpuTerrainVertexBuildBuffer = new(65536);
    private readonly List<GpuVertex> _gpuDynamicVertexBuildBuffer = new(16384);
    private readonly List<GpuVertex> _gpuEnergyMechanismVertexBuildBuffer = new(8192);
    private readonly List<GpuVertex> _gpuProjectileVertexBuildBuffer = new(8192);
    private readonly List<GpuTerrainChunk> _gpuTerrainChunks = new(512);
    private GlGenBuffersDelegate? _glGenBuffers;
    private GlBindBufferDelegate? _glBindBuffer;
    private GlBufferDataDelegate? _glBufferData;
    private GlBufferSubDataDelegate? _glBufferSubData;
    private GlDeleteBuffersDelegate? _glDeleteBuffers;
    private GlGenVertexArraysDelegate? _glGenVertexArrays;
    private GlBindVertexArrayDelegate? _glBindVertexArray;
    private GlDeleteVertexArraysDelegate? _glDeleteVertexArrays;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct GpuVertex
    {
        public GpuVertex(Vector3 position, Color color)
        {
            X = position.X;
            Y = position.Y;
            Z = position.Z;
            R = color.R;
            G = color.G;
            B = color.B;
            A = color.A;
        }

        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte A;
    }

    private sealed class GpuTerrainChunk
    {
        public int Buffer;

        public int VertexCount;

        public int Version = -1;

        public readonly List<GpuVertex> BuildBuffer = new(768);
    }

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
    private static extern void glDrawArrays(int mode, int first, int count);

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

    private void DrawGpuMatch(Graphics graphics)
    {
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

        wglMakeCurrent(_gpuDeviceContext, _gpuRenderContext);
        glViewport(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        glClearColor(0.035f, 0.047f, 0.065f, 1f);
        glClear(GlColorBufferBit | GlDepthBufferBit);
        glEnable(GlDepthTest);
        glEnable(GlBlend);
        glBlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);

        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(_projectionMatrix));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(_viewMatrix));

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

        int facilityVertices = _gpuEnergyMechanismVertexBuildBuffer.Count;
        int entityVertices = _gpuDynamicVertexBuildBuffer.Count;
        int projectileVertices = _gpuProjectileVertexBuildBuffer.Count;
        stepStartTicks = Stopwatch.GetTimestamp();
        FlushGpuEnergyMechanismVertices();
        FlushGpuDynamicVertices();
        FlushGpuProjectileVertices();
        flushTicks = Stopwatch.GetTimestamp() - stepStartTicks;
        DrawGpuEntityHealthBars();
        if (!_previewOnly)
        {
            DrawGpuProjectileTrailLines();
            DrawGpuPredictedProjectileTrajectory();
            DrawGpuDebugReference();
        }

        _hasPresentedGpuFrame = true;
        stepStartTicks = Stopwatch.GetTimestamp();
        DrawGpuOverlayLayer();
        overlayTicks = Stopwatch.GetTimestamp() - stepStartTicks;
        stepStartTicks = Stopwatch.GetTimestamp();
        SwapBuffers(_gpuDeviceContext);
        swapTicks = Stopwatch.GetTimestamp() - stepStartTicks;
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

    private void DrawGpuOverlayLayer()
    {
        EnsureGpuOverlaySurface();
        if (_gpuOverlayGraphics is null)
        {
            return;
        }

        long nowTicks = _frameClock.ElapsedTicks;
        bool mustUpload = _gpuOverlayTexture == 0
            || _gpuOverlayTextureSize != ClientSize
            || _lastGpuOverlayPausedState != _paused
            || _lastGpuOverlayUploadTicks <= 0
            || (nowTicks - _lastGpuOverlayUploadTicks) / (double)Stopwatch.Frequency >= GpuOverlayUploadIntervalSec;
        if (mustUpload)
        {
            _gpuOverlayGraphics.Clear(Color.Transparent);
            _gpuOverlayGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            DrawInMatchOverlay(_gpuOverlayGraphics);
            UploadGpuOverlayBitmap();
            _lastGpuOverlayUploadTicks = nowTicks;
            _lastGpuOverlayPausedState = _paused;
        }

        PresentGpuOverlayTexture();
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
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} "
            + $"frame={ElapsedMs(frameStartTicks, nowTicks):0.00}ms "
            + $"map={TicksToMs(terrainTicks):0.00}ms/{_gpuTerrainVertexCount}v/1draw "
            + $"facility={TicksToMs(facilityTicks):0.00}ms/{facilityVertices}v/{(facilityVertices > 0 ? 1 : 0)}draw "
            + $"unit={TicksToMs(entityTicks):0.00}ms/{entityVertices}v/{(entityVertices > 0 ? 1 : 0)}draw "
            + $"projectile={TicksToMs(projectileTicks):0.00}ms/{projectileVertices}v/{(projectileVertices > 0 ? 1 : 0)}draw "
            + $"flush={TicksToMs(flushTicks):0.00}ms overlay={TicksToMs(overlayTicks):0.00}ms swap={TicksToMs(swapTicks):0.00}ms "
            + $"entities={_host.World.Entities.Count} projectiles={_host.World.Projectiles.Count}";

        try
        {
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "render_perf.log"), line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static double ElapsedMs(long startTicks, long endTicks)
        => (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;

    private static double TicksToMs(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;

    private bool EnsureGpuContext()
    {
        if (_gpuContextReady)
        {
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
            glTexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
            glTexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
            glTexImage2D(GlTexture2D, 0, GlRgba, uploadBitmap.Width, uploadBitmap.Height, 0, GlBgra, GlUnsignedByte, data.Scan0);
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
            DrawGpuVertexBuffer(_gpuTerrainVertexBuffer, _gpuTerrainVertexCount);
        }
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
            AppendGpuTriangle(target, face.Vertices[0], face.Vertices[1], face.Vertices[2], face.FillColor);
        }
        else if (face.Vertices.Length == 4)
        {
            AppendGpuQuad(target, face.Vertices[0], face.Vertices[1], face.Vertices[2], face.Vertices[3], face.FillColor);
        }
        else if (face.Vertices.Length > 4)
        {
            for (int index = 1; index < face.Vertices.Length - 1; index++)
            {
                AppendGpuTriangle(target, face.Vertices[0], face.Vertices[index], face.Vertices[index + 1], face.FillColor);
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
                AppendGpuTriangle(target, face.Vertices[0], face.Vertices[1], face.Vertices[2], face.FillColor);
            }
            else if (face.Vertices.Length == 4)
            {
                AppendGpuQuad(target, face.Vertices[0], face.Vertices[1], face.Vertices[2], face.Vertices[3], face.FillColor);
            }
            else if (face.Vertices.Length > 4)
            {
                for (int index = 1; index < face.Vertices.Length - 1; index++)
                {
                    AppendGpuTriangle(target, face.Vertices[0], face.Vertices[index], face.Vertices[index + 1], face.FillColor);
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

            Vector2 anchor = facet.PointsWorld[0];
            float anchorHeight = facet.HeightsM[0];
            Vector3 a = ToScenePoint(anchor.X, anchor.Y, anchorHeight);
            for (int index = 1; index < facet.PointsWorld.Count - 1; index++)
            {
                Vector2 b = facet.PointsWorld[index];
                Vector2 c = facet.PointsWorld[index + 1];
                float hb = index < facet.HeightsM.Count ? facet.HeightsM[index] : facet.HeightsM[^1];
                float hc = index + 1 < facet.HeightsM.Count ? facet.HeightsM[index + 1] : facet.HeightsM[^1];
                AppendGpuTriangle(target, a, ToScenePoint(b.X, b.Y, hb), ToScenePoint(c.X, c.Y, hc), facet.TopColor);
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
                AppendGpuTriangle(ResolveGpuTerrainChunk(center).BuildBuffer, a, vb, vc, facet.TopColor);
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

            Vector2 anchor = facet.PointsWorld[0];
            float anchorHeight = facet.HeightsM[0];
            for (int index = 1; index < facet.PointsWorld.Count - 1; index++)
            {
                Vector2 b = facet.PointsWorld[index];
                Vector2 c = facet.PointsWorld[index + 1];
                float hb = index < facet.HeightsM.Count ? facet.HeightsM[index] : facet.HeightsM[^1];
                float hc = index + 1 < facet.HeightsM.Count ? facet.HeightsM[index + 1] : facet.HeightsM[^1];
                DrawGpuTriangle(ToScenePoint(anchor.X, anchor.Y, anchorHeight), ToScenePoint(b.X, b.Y, hb), ToScenePoint(c.X, c.Y, hc), facet.TopColor);
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
            if (!_showDebugSidebars && !energyMechanism && !dogHole)
            {
                continue;
            }

            Color color = region.Type switch
            {
                "base" or "outpost" => Color.FromArgb(160, ResolveTeamColor(region.Team)),
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
                    DrawGpuEnergyMechanismModel(representative, color, energyCenterX, energyCenterY);
                }
                else
                {
                    DrawGpuEnergyMechanismModel(region, color);
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

        float height = Math.Max(0.012f, (float)region.HeightM);
        if (string.Equals(region.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && region.Points.Count >= 3)
        {
            Vector3 anchor = ToScenePoint(region.Points[0].X, region.Points[0].Y, height);
            for (int index = 1; index < region.Points.Count - 1; index++)
            {
                AppendOrDrawGpuTriangle(anchor, ToScenePoint(region.Points[index].X, region.Points[index].Y, height), ToScenePoint(region.Points[index + 1].X, region.Points[index + 1].Y, height), color);
            }

            return;
        }

        double minX = Math.Min(region.X1, region.X2);
        double maxX = Math.Max(region.X1, region.X2);
        double minY = Math.Min(region.Y1, region.Y2);
        double maxY = Math.Max(region.Y1, region.Y2);
        AppendOrDrawGpuQuad(
            ToScenePoint(minX, minY, height),
            ToScenePoint(maxX, minY, height),
            ToScenePoint(maxX, maxY, height),
            ToScenePoint(minX, maxY, height),
            color);
    }

    private void DrawGpuEntities()
    {
        _entityOverlayBuffer.Clear();
        foreach (SimulationEntity entity in _host.World.Entities)
        {
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

            Color teamColor = ResolveTeamColor(entity.Team);
            float baseHeight = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM);
            Vector3 center = ToScenePoint(entity.X, entity.Y, baseHeight + (float)Math.Max(0.06, entity.BodyHeightM * 0.5));
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
            DrawGpuBox(center, radius, Math.Max(0.09f, radius * 0.7f), height, Color.FromArgb(230, teamColor));
            _entityOverlayBuffer.Add(new EntityRenderOverlay(
                entity,
                center,
                height,
                _host.ResolveAppearanceProfile(entity)));

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
        int largeSlices = projectileCount >= 96 ? 5 : projectileCount >= 48 ? 6 : 7;
        int largeStacks = projectileCount >= 96 ? 3 : 4;
        int smallSlices = projectileCount >= 96 ? 4 : projectileCount >= 48 ? 5 : 6;
        int smallStacks = projectileCount >= 96 ? 2 : 3;
        bool flatProjectileRendering = !_host.SolidProjectileRendering;
        List<GpuVertex> projectileBuffer = CurrentGpuDynamicBuildBuffer();
        Vector3 cameraRight = Vector3.Zero;
        Vector3 cameraUp = Vector3.Zero;
        if (flatProjectileRendering)
        {
            Vector3 viewForward = _cameraTargetM - _cameraPositionM;
            if (viewForward.LengthSquared() <= 1e-6f)
            {
                viewForward = Vector3.UnitZ;
            }
            else
            {
                viewForward = Vector3.Normalize(viewForward);
            }

            cameraRight = Vector3.Cross(viewForward, Vector3.UnitY);
            if (cameraRight.LengthSquared() <= 1e-6f)
            {
                cameraRight = Vector3.UnitX;
            }
            else
            {
                cameraRight = Vector3.Normalize(cameraRight);
            }

            cameraUp = Vector3.Cross(cameraRight, viewForward);
            if (cameraUp.LengthSquared() <= 1e-6f)
            {
                cameraUp = Vector3.UnitY;
            }
            else
            {
                cameraUp = Vector3.Normalize(cameraUp);
            }
        }

        foreach (SimulationProjectile projectile in _host.World.Projectiles)
        {
            bool largeRound = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
            Color color = largeRound
                ? Color.FromArgb(248, 250, 252, 255)
                : Color.FromArgb(248, 112, 255, 128);
            Vector3 center = ToScenePoint(projectile.X, projectile.Y, (float)Math.Max(0.05, projectile.HeightM));
            float radius = (float)(SimulationCombatMath.ProjectileDiameterM(projectile.AmmoType) * 0.5);
            float visibleRadius = Math.Max(radius * (largeRound ? 3.2f : 3.8f), largeRound ? 0.030f : 0.020f);
            if (!IsSceneBoundsPotentiallyVisible(center, visibleRadius * 1.3f, visibleRadius * 1.3f))
            {
                continue;
            }

            if (!flatProjectileRendering)
            {
                AppendGpuSphere(
                    projectileBuffer,
                    center,
                    visibleRadius,
                    color,
                    largeRound ? largeSlices : smallSlices,
                    largeRound ? largeStacks : smallStacks);
            }
            else
            {
                Color rimColor = largeRound
                    ? Color.FromArgb(color.A, 98, 196, 255)
                    : Color.FromArgb(color.A, 42, 255, 92);
                Color coreColor = largeRound
                    ? Color.FromArgb(255, 255, 255, 255)
                    : Color.FromArgb(255, 188, 255, 200);
                AppendGpuProjectileBillboard(projectileBuffer, center, visibleRadius, rimColor, coreColor, largeRound ? 10 : 8, cameraRight, cameraUp);
            }
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
        DrawGpuQuad(
            center - halfRight - halfUp,
            center + halfRight - halfUp,
            center + halfRight + halfUp,
            center - halfRight + halfUp,
            Color.FromArgb(210, 5, 8, 12));

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
            bool showActive = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
                && teamState.EnergyNextModuleDelaySec <= 1e-6
                && teamState.EnergyCurrentLitMask != 0;
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

            if (!showActive && !showActivated && !showLastHit && !hasPersistentRings)
            {
                continue;
            }

            Color teamColor = ResolveTeamColor(teamState.Team);
            Color activeColor = Color.FromArgb(220, Math.Min(255, teamColor.R + 45), Math.Min(255, teamColor.G + 45), Math.Min(255, teamColor.B + 45));
            Color activatedColor = Color.FromArgb(210, teamColor.R, teamColor.G, teamColor.B);
            Color ringHitColor = Color.FromArgb(245, Math.Min(255, teamColor.R + 58), Math.Min(255, teamColor.G + 58), Math.Min(255, teamColor.B + 58));
            Color ringSteadyColor = Color.FromArgb(204, teamColor.R, teamColor.G, teamColor.B);
            foreach (ArmorPlateTarget plate in SimulationCombatMath.GetEnergyMechanismTargets(mechanism, metersPerWorldUnit, _host.World.GameTimeSec, teamState.Team, teamState))
            {
                if (!SimulationCombatMath.TryParseEnergyArmIndex(plate.Id, out _, out int armIndex))
                {
                    continue;
                }

                Vector3 center = ToScenePoint(plate.X, plate.Y, (float)plate.HeightM);
                Vector3 normal = ResolveGpuPlateNormal(plate);
                float diskRadius = Math.Max(0.06f, (float)Math.Max(plate.WidthM, plate.HeightSpanM) * 0.5f);
                if (showActive && (teamState.EnergyCurrentLitMask & (1 << armIndex)) != 0)
                {
                    double blinkPhase = (_host.World.GameTimeSec * 3.0) % 1.0;
                    Color blinkColor = blinkPhase < 0.5 ? activeColor : Color.FromArgb(185, activeColor.R, activeColor.G, activeColor.B);
                    DrawGpuPendingEnergyMarkerDoubleSided(center, normal, Vector3.UnitY, diskRadius * 1.02f, blinkColor);
                }
                else if (showActivated)
                {
                    DrawGpuActivationMarkerDoubleSided(center, normal, Vector3.UnitY, diskRadius * 1.01f, activatedColor, false);
                }

                int persistentRingScore = armIndex >= 0 && armIndex < teamState.EnergyHitRingsByArm.Length
                    ? Math.Clamp(teamState.EnergyHitRingsByArm[armIndex], 0, 10)
                    : 0;
                if (persistentRingScore <= 0)
                {
                    continue;
                }

                float outer = diskRadius * (11 - persistentRingScore) / 10f;
                float inner = persistentRingScore >= 10 ? 0f : diskRadius * (10 - persistentRingScore) / 10f;
                bool flashing = showLastHit
                    && teamState.EnergyLastHitArmIndex == armIndex
                    && _host.World.GameTimeSec <= teamState.EnergyLastHitFlashEndSec;
                if (flashing)
                {
                    double blinkPhase = (_host.World.GameTimeSec * 8.0) % 1.0;
                    Color flashColor = blinkPhase < 0.5 ? ringHitColor : Color.FromArgb(210, teamColor.R, teamColor.G, teamColor.B);
                    DrawGpuAnnulusDoubleSided(center, normal, Vector3.UnitY, inner, Math.Max(inner + 0.004f, outer), flashColor, 24, 0.0140f);
                }
                else
                {
                    DrawGpuAnnulusDoubleSided(center, normal, Vector3.UnitY, inner, Math.Max(inner + 0.003f, outer), ringSteadyColor, 24, 0.0130f);
                }
            }
        }
    }

    private void DrawGpuActivationMarkerDoubleSided(Vector3 center, Vector3 normalAxis, Vector3 upAxis, float diskRadius, Color color, bool drawCross)
    {
        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        DrawGpuActivationMarker(center + normal * 0.0180f, normal, upAxis, diskRadius, color, drawCross);
        DrawGpuActivationMarker(center - normal * 0.0180f, -normal, upAxis, diskRadius, color, drawCross);
    }

    private void DrawGpuPendingEnergyMarkerDoubleSided(Vector3 center, Vector3 normalAxis, Vector3 upAxis, float diskRadius, Color color)
    {
        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        DrawGpuAnnulusDoubleSided(center, normal, upAxis, diskRadius * 0.18f, diskRadius * 0.25f, color, 24, 0.0260f);
        DrawGpuAnnulusDoubleSided(center, normal, upAxis, diskRadius * 0.61f, diskRadius * 0.71f, color, 24, 0.0270f);
        DrawGpuPendingEnergyCross(center + normal * 0.0280f, normal, upAxis, diskRadius, color);
        DrawGpuPendingEnergyCross(center - normal * 0.0280f, -normal, upAxis, diskRadius, color);
    }

    private void DrawGpuPendingEnergyCross(Vector3 center, Vector3 normalAxis, Vector3 upAxis, float diskRadius, Color color)
    {
        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        Vector3 up = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        if (MathF.Abs(Vector3.Dot(normal, up)) > 0.98f)
        {
            up = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
        }

        Vector3 tangent = Vector3.Normalize(up - normal * Vector3.Dot(up, normal));
        Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
        float armHalfLength = diskRadius * 0.86f;
        float armHalfWidth = Math.Max(0.008f, diskRadius * 0.075f);
        AppendOrDrawGpuQuad(
            center - tangent * armHalfLength - bitangent * armHalfWidth,
            center + tangent * armHalfLength - bitangent * armHalfWidth,
            center + tangent * armHalfLength + bitangent * armHalfWidth,
            center - tangent * armHalfLength + bitangent * armHalfWidth,
            color);
        AppendOrDrawGpuQuad(
            center - bitangent * armHalfLength - tangent * armHalfWidth,
            center + bitangent * armHalfLength - tangent * armHalfWidth,
            center + bitangent * armHalfLength + tangent * armHalfWidth,
            center - bitangent * armHalfLength + tangent * armHalfWidth,
            color);
    }

    private void DrawGpuActivationMarker(Vector3 center, Vector3 normalAxis, Vector3 upAxis, float diskRadius, Color color, bool drawCross)
    {
        DrawGpuAnnulus(center, normalAxis, upAxis, diskRadius * 0.18f, diskRadius * 0.24f, color, 24);
        DrawGpuAnnulus(center, normalAxis, upAxis, diskRadius * 0.62f, diskRadius * 0.70f, color, 24);
        if (!drawCross)
        {
            DrawGpuAnnulus(center, normalAxis, upAxis, diskRadius * 0.92f, diskRadius * 1.02f, color, 24);
            return;
        }

        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        Vector3 up = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        if (MathF.Abs(Vector3.Dot(normal, up)) > 0.98f)
        {
            up = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
        }

        Vector3 tangent = Vector3.Normalize(up - normal * Vector3.Dot(up, normal));
        Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
        float armHalfLength = diskRadius * 0.82f;
        float armHalfWidth = Math.Max(0.007f, diskRadius * 0.07f);
        AppendOrDrawGpuQuad(
            center - tangent * armHalfLength - bitangent * armHalfWidth,
            center + tangent * armHalfLength - bitangent * armHalfWidth,
            center + tangent * armHalfLength + bitangent * armHalfWidth,
            center - tangent * armHalfLength + bitangent * armHalfWidth,
            color);
        AppendOrDrawGpuQuad(
            center - bitangent * armHalfLength - tangent * armHalfWidth,
            center + bitangent * armHalfLength - tangent * armHalfWidth,
            center + bitangent * armHalfLength + tangent * armHalfWidth,
            center - bitangent * armHalfLength + tangent * armHalfWidth,
            color);
    }

    private void DrawGpuIdleEnergyMarker(Vector3 center, Vector3 normalAxis, Vector3 upAxis, float diskRadius, Color color)
    {
        DrawGpuAnnulus(center, normalAxis, upAxis, diskRadius * 0.78f, diskRadius * 0.86f, color, 24);
    }

    private void DrawGpuAnnulusDoubleSided(Vector3 center, Vector3 normalAxis, Vector3 upAxis, float innerRadius, float outerRadius, Color color, int segmentCount, float offset)
    {
        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        DrawGpuAnnulus(center + normal * offset, normal, upAxis, innerRadius, outerRadius, color, segmentCount);
        DrawGpuAnnulus(center - normal * offset, -normal, upAxis, innerRadius, outerRadius, color, segmentCount);
    }

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
            ref _gpuDynamicVertexCapacity);
    }

    private void FlushGpuEnergyMechanismVertices()
    {
        FlushGpuVertexList(
            _gpuEnergyMechanismVertexBuildBuffer,
            ref _gpuEnergyMechanismVertexBuffer,
            ref _gpuEnergyMechanismVertexCapacity);
    }

    private void FlushGpuProjectileVertices()
    {
        FlushGpuVertexList(
            _gpuProjectileVertexBuildBuffer,
            ref _gpuProjectileVertexBuffer,
            ref _gpuProjectileVertexCapacity);
    }

    private void FlushGpuVertexList(List<GpuVertex> vertices, ref int buffer, ref int capacity)
    {
        if (vertices.Count == 0)
        {
            return;
        }

        if (!_gpuBufferApiReady || _glGenBuffers is null || _glBindBuffer is null || _glBufferData is null || _glBufferSubData is null)
        {
            DrawGpuVerticesImmediate(vertices);
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
        DrawGpuVertexBuffer(buffer, vertices.Count);
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

    private void DrawGpuVertexBuffer(int buffer, int vertexCount)
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
        glEnableClientState(GlVertexArray);
        glEnableClientState(GlColorArray);
        glVertexPointer(3, GlFloat, stride, IntPtr.Zero);
        glColorPointer(4, GlUnsignedByte, stride, new IntPtr(12));
        glDrawArrays(GlTriangles, 0, vertexCount);
        glDisableClientState(GlColorArray);
        glDisableClientState(GlVertexArray);
        _glBindBuffer(GlArrayBuffer, 0);
        _glBindVertexArray?.Invoke(0);
    }

    private static void DrawGpuVerticesImmediate(IReadOnlyList<GpuVertex> vertices)
    {
        glBegin(GlTriangles);
        foreach (GpuVertex vertex in vertices)
        {
            glColor4ub(vertex.R, vertex.G, vertex.B, vertex.A);
            glVertex3f(vertex.X, vertex.Y, vertex.Z);
        }

        glEnd();
    }

    private static void AppendGpuTriangle(List<GpuVertex> target, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        target.Add(new GpuVertex(a, color));
        target.Add(new GpuVertex(b, color));
        target.Add(new GpuVertex(c, color));
    }

    private static void AppendGpuQuad(List<GpuVertex> target, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        AppendGpuTriangle(target, a, b, c, color);
        AppendGpuTriangle(target, a, c, d, color);
    }

    private static void AppendGpuPolygon(List<GpuVertex> target, IReadOnlyList<Vector3> vertices, Color color)
    {
        if (vertices.Count < 3)
        {
            return;
        }

        for (int index = 1; index < vertices.Count - 1; index++)
        {
            AppendGpuTriangle(target, vertices[0], vertices[index], vertices[index + 1], color);
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
        Vector3 light = Vector3.Normalize(new Vector3(-0.45f, 1.0f, -0.35f));
        float diffuse = MathF.Abs(Vector3.Dot(normal, light));
        return ScaleGpuColor(color, Math.Clamp(ambient + diffuse * 0.42f, 0.35f, 1.12f));
    }

    private static void SetGpuColor(Color color)
    {
        glColor4ub(color.R, color.G, color.B, color.A);
    }

    private void AppendOrDrawGpuTriangle(Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        if (_gpuBatchingDynamicGeometry)
        {
            AppendGpuTriangle(CurrentGpuDynamicBuildBuffer(), a, b, c, color);
            return;
        }

        DrawGpuTriangle(a, b, c, color);
    }

    private void AppendOrDrawGpuQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        if (_gpuBatchingDynamicGeometry)
        {
            AppendGpuQuad(CurrentGpuDynamicBuildBuffer(), a, b, c, d, color);
            return;
        }

        DrawGpuQuad(a, b, c, d, color);
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

    private void DrawGpuSolidFaces(IReadOnlyList<SolidFace> faces, Color fillColor, Color edgeColor)
    {
        if (_gpuBatchingDynamicGeometry)
        {
            List<GpuVertex> target = CurrentGpuDynamicBuildBuffer();
            foreach (SolidFace face in faces)
            {
                Color shaded = ShadeFaceColor(fillColor, face.Vertices, face.Ambient);
                AppendGpuPolygon(target, face.Vertices, shaded);
            }

            return;
        }

        foreach (SolidFace face in faces)
        {
            Color shaded = ShadeFaceColor(fillColor, face.Vertices, face.Ambient);
            DrawGpuPolygon(face.Vertices, shaded);
            DrawGpuPolygonOutline(face.Vertices, edgeColor);
        }
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
        if (_gpuOverlayBitmap is not null
            && _gpuOverlayGraphics is not null
            && _gpuOverlayBitmap.Width == ClientSize.Width
            && _gpuOverlayBitmap.Height == ClientSize.Height)
        {
            return;
        }

        _gpuOverlayGraphics?.Dispose();
        _gpuOverlayGraphics = null;
        _gpuOverlayBitmap?.Dispose();
        _gpuOverlayBitmap = null;

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        _gpuOverlayBitmap = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb);
        _gpuOverlayGraphics = Graphics.FromImage(_gpuOverlayBitmap);
        _gpuOverlayGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        _gpuOverlayTextureSize = Size.Empty;
        _lastGpuOverlayUploadTicks = 0;
    }

    private void UploadGpuOverlayBitmap()
    {
        if (_gpuOverlayBitmap is null)
        {
            return;
        }

        Rectangle rect = new(0, 0, _gpuOverlayBitmap.Width, _gpuOverlayBitmap.Height);
        BitmapData data = _gpuOverlayBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (_gpuOverlayTexture == 0)
            {
                glGenTextures(1, out _gpuOverlayTexture);
            }

            glBindTexture(GlTexture2D, _gpuOverlayTexture);
            glTexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
            glTexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
            if (_gpuOverlayTextureSize != _gpuOverlayBitmap.Size)
            {
                glTexImage2D(GlTexture2D, 0, GlRgba, _gpuOverlayBitmap.Width, _gpuOverlayBitmap.Height, 0, GlBgra, GlUnsignedByte, data.Scan0);
                _gpuOverlayTextureSize = _gpuOverlayBitmap.Size;
            }
            else
            {
                glTexSubImage2D(GlTexture2D, 0, 0, 0, _gpuOverlayBitmap.Width, _gpuOverlayBitmap.Height, GlBgra, GlUnsignedByte, data.Scan0);
            }
        }
        finally
        {
            _gpuOverlayBitmap.UnlockBits(data);
        }
    }

    private void PresentGpuOverlayTexture()
    {
        if (_gpuOverlayTexture == 0)
        {
            return;
        }

        glDisable(GlDepthTest);
        glMatrixMode(GlProjection);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glMatrixMode(GlModelView);
        glLoadMatrixf(ToOpenGlMatrix(Matrix4x4.Identity));
        glEnable(GlTexture2D);
        glBindTexture(GlTexture2D, _gpuOverlayTexture);
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

        DeleteGpuBuffer(ref _gpuDynamicVertexBuffer);
        DeleteGpuBuffer(ref _gpuEnergyMechanismVertexBuffer);
        DeleteGpuBuffer(ref _gpuProjectileVertexBuffer);
        DeleteGpuVertexArray(ref _gpuSharedVertexArray);
        _gpuTerrainVertexCount = 0;
        _gpuDynamicVertexCapacity = 0;
        _gpuEnergyMechanismVertexCapacity = 0;
        _gpuProjectileVertexCapacity = 0;
        _gpuTerrainBufferVersion = -1;

        if (_gpuOverlayTexture != 0 && _gpuContextReady)
        {
            wglMakeCurrent(_gpuDeviceContext, _gpuRenderContext);
            int overlayTexture = _gpuOverlayTexture;
            glDeleteTextures(1, ref overlayTexture);
            _gpuOverlayTexture = 0;
        }

        if (_gpuTerrainTexture != 0 && _gpuContextReady)
        {
            wglMakeCurrent(_gpuDeviceContext, _gpuRenderContext);
            int texture = _gpuTerrainTexture;
            glDeleteTextures(1, ref texture);
            _gpuTerrainTexture = 0;
        }

        if (_gpuRenderContext != IntPtr.Zero)
        {
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(_gpuRenderContext);
            _gpuRenderContext = IntPtr.Zero;
        }

        if (_gpuDeviceContext != IntPtr.Zero && IsHandleCreated)
        {
            ReleaseDC(Handle, _gpuDeviceContext);
            _gpuDeviceContext = IntPtr.Zero;
        }

        _gpuContextReady = false;
    }

    private void InvalidateGpuTerrainBuffers()
    {
        _gpuTerrainBufferVersion = -1;
        _gpuTerrainVertexCount = 0;
        _gpuTerrainVertexBuildBuffer.Clear();
        foreach (GpuTerrainChunk chunk in _gpuTerrainChunks)
        {
            chunk.VertexCount = 0;
            chunk.Version = -1;
        }
    }

    private void DeleteGpuBuffer(ref int buffer)
    {
        if (buffer == 0 || !_gpuContextReady || _glDeleteBuffers is null)
        {
            buffer = 0;
            return;
        }

        wglMakeCurrent(_gpuDeviceContext, _gpuRenderContext);
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

        wglMakeCurrent(_gpuDeviceContext, _gpuRenderContext);
        int handle = vertexArray;
        _glDeleteVertexArrays(1, ref handle);
        vertexArray = 0;
    }
}
