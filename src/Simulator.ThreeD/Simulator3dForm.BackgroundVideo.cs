using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Simulator.Platform.Media;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private readonly object _backgroundVideoSync = new();
    private IBackgroundVideoSource? _backgroundVideoSource;
    private Bitmap? _backgroundVideoFrame;
    private Bitmap? _backgroundVideoCompositedFrame;
    private string? _backgroundVideoPath;
    private bool _backgroundVideoInitialized;
    private long _backgroundVideoBitmapVersion = -1;
    private long _backgroundVideoCompositedVersion = -1;
    private System.Drawing.Size _backgroundVideoCompositedSize = System.Drawing.Size.Empty;

    private void InitializeBackgroundVideo()
    {
        if (_backgroundVideoInitialized)
        {
            return;
        }

        _backgroundVideoInitialized = true;
        string requestedPath = @"E:\Artinx\260111new\Simulator\Dark1.mp4";
        _backgroundVideoPath = File.Exists(requestedPath)
            ? requestedPath
            : Path.Combine(_host.ProjectRootPath, "Dark1.mp4");
        if (!File.Exists(_backgroundVideoPath))
        {
            return;
        }

        _backgroundVideoSource = CreateBackgroundVideoSource();
        _backgroundVideoSource.Start(_backgroundVideoPath, () => _appState == SimulatorAppState.MainMenu);
    }

    private void DisposeBackgroundVideo()
    {
        _backgroundVideoSource?.Dispose();
        _backgroundVideoSource = null;

        lock (_backgroundVideoSync)
        {
            _backgroundVideoFrame?.Dispose();
            _backgroundVideoFrame = null;
            _backgroundVideoCompositedFrame?.Dispose();
            _backgroundVideoCompositedFrame = null;
            _backgroundVideoBitmapVersion = -1;
            _backgroundVideoCompositedVersion = -1;
            _backgroundVideoCompositedSize = System.Drawing.Size.Empty;
        }
    }

    private bool TryDrawBackgroundVideo(Graphics graphics)
    {
        if (_appState != SimulatorAppState.MainMenu)
        {
            return false;
        }

        IBackgroundVideoSource? source = _backgroundVideoSource;
        if (source is null || !source.TryGetLatestFrame(out BackgroundVideoFrame sourceFrame))
        {
            return false;
        }

        lock (_backgroundVideoSync)
        {
            if (ClientSize.Width <= 0
                || ClientSize.Height <= 0)
            {
                return false;
            }

            if (_backgroundVideoFrame is null || _backgroundVideoBitmapVersion != sourceFrame.Version)
            {
                _backgroundVideoFrame?.Dispose();
                _backgroundVideoFrame = ConvertBgraFrameToBitmap(sourceFrame);
                _backgroundVideoBitmapVersion = sourceFrame.Version;
            }

            System.Drawing.Size targetSize = ClientSize;
            if (_backgroundVideoCompositedFrame is null
                || _backgroundVideoCompositedVersion != _backgroundVideoBitmapVersion
                || _backgroundVideoCompositedSize != targetSize)
            {
                _backgroundVideoCompositedFrame?.Dispose();
                _backgroundVideoCompositedFrame = ComposeBackgroundVideoFrame(_backgroundVideoFrame, targetSize);
                _backgroundVideoCompositedVersion = _backgroundVideoBitmapVersion;
                _backgroundVideoCompositedSize = targetSize;
            }

            graphics.DrawImageUnscaled(_backgroundVideoCompositedFrame, System.Drawing.Point.Empty);
            return true;
        }
    }

    private static Bitmap ComposeBackgroundVideoFrame(Bitmap source, System.Drawing.Size targetSize)
    {
        var composed = new Bitmap(Math.Max(1, targetSize.Width), Math.Max(1, targetSize.Height), PixelFormat.Format32bppPArgb);
        using Graphics graphics = Graphics.FromImage(composed);
        Rectangle targetRect = new(0, 0, composed.Width, composed.Height);
        Rectangle sourceRect = ComputeAspectFillSourceRect(source.Size, targetSize);
        graphics.InterpolationMode = InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.DrawImage(source, targetRect, sourceRect, GraphicsUnit.Pixel);
        using var veil = new SolidBrush(Color.FromArgb(128, 0, 0, 0));
        graphics.FillRectangle(veil, targetRect);
        return composed;
    }

    private static Bitmap ConvertBgraFrameToBitmap(BackgroundVideoFrame frame)
    {
        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bitmap.Width * 4;
            for (int y = 0; y < bitmap.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    frame.Bgra32,
                    y * rowBytes,
                    IntPtr.Add(data.Scan0, y * data.Stride),
                    rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static IBackgroundVideoSource CreateBackgroundVideoSource()
    {
        if (OperatingSystem.IsWindows())
        {
            return new OpenCvBackgroundVideoSource();
        }

        return NullBackgroundVideoSource.Instance;
    }

    private static Rectangle ComputeAspectFillSourceRect(System.Drawing.Size source, System.Drawing.Size target)
    {
        if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0)
        {
            return new Rectangle(0, 0, Math.Max(1, source.Width), Math.Max(1, source.Height));
        }

        float sourceAspect = source.Width / (float)source.Height;
        float targetAspect = target.Width / (float)target.Height;
        if (sourceAspect > targetAspect)
        {
            int cropWidth = Math.Max(1, (int)MathF.Round(source.Height * targetAspect));
            int cropX = Math.Max(0, (source.Width - cropWidth) / 2);
            return new Rectangle(cropX, 0, Math.Min(cropWidth, source.Width - cropX), source.Height);
        }

        int cropHeight = Math.Max(1, (int)MathF.Round(source.Width / targetAspect));
        int cropY = Math.Max(0, (source.Height - cropHeight) / 2);
        return new Rectangle(0, cropY, source.Width, Math.Min(cropHeight, source.Height - cropY));
    }

    private double ResolveBackgroundVideoFrameIntervalSec()
    {
        double interval = _backgroundVideoSource?.FrameIntervalSec ?? MainMenuTargetFrameIntervalSec;
        return double.IsFinite(interval) && interval > 1e-6
            ? Math.Clamp(interval, 1.0 / 144.0, 1.0)
            : MainMenuTargetFrameIntervalSec;
    }
}
