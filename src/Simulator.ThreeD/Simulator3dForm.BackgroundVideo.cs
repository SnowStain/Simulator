using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const int BackgroundVideoMaxDecodeWidth = 1280;
    private const int BackgroundVideoMaxDecodeHeight = 720;
    private readonly object _backgroundVideoSync = new();
    private CancellationTokenSource? _backgroundVideoCts;
    private Task? _backgroundVideoTask;
    private Bitmap? _backgroundVideoFrame;
    private string? _backgroundVideoPath;
    private bool _backgroundVideoInitialized;
    private double _backgroundVideoFrameIntervalSec = MainMenuTargetFrameIntervalSec;
    private long _backgroundVideoFrameVersion;

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

        _backgroundVideoCts = new CancellationTokenSource();
        _backgroundVideoTask = Task.Run(() => RunBackgroundVideoLoop(_backgroundVideoPath, _backgroundVideoCts.Token));
    }

    private void DisposeBackgroundVideo()
    {
        CancellationTokenSource? cts = _backgroundVideoCts;
        Task? task = _backgroundVideoTask;
        _backgroundVideoCts = null;
        _backgroundVideoTask = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }

        if (task is not null)
        {
            try
            {
                task.Wait(500);
            }
            catch
            {
            }
        }

        lock (_backgroundVideoSync)
        {
            _backgroundVideoFrame?.Dispose();
            _backgroundVideoFrame = null;
        }
    }

    private bool TryDrawBackgroundVideo(Graphics graphics)
    {
        if (_appState != SimulatorAppState.MainMenu)
        {
            return false;
        }

        lock (_backgroundVideoSync)
        {
            if (_backgroundVideoFrame is null
                || ClientSize.Width <= 0
                || ClientSize.Height <= 0)
            {
                return false;
            }

            Rectangle sourceRect = ComputeAspectFillSourceRect(_backgroundVideoFrame.Size, ClientSize);
            GraphicsState state = graphics.Save();
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.DrawImage(_backgroundVideoFrame, ClientRectangle, sourceRect, GraphicsUnit.Pixel);
            graphics.Restore(state);
            using var veil = new SolidBrush(Color.FromArgb(128, 0, 0, 0));
            graphics.FillRectangle(veil, ClientRectangle);
            return true;
        }
    }

    private async Task RunBackgroundVideoLoop(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var capture = new VideoCapture(path);
            if (!capture.IsOpened())
            {
                return;
            }

            double fps = capture.Fps;
            if (!double.IsFinite(fps) || fps < 1.0)
            {
                fps = 30.0;
            }

            fps = Math.Clamp(fps, 1.0, 144.0);
            _backgroundVideoFrameIntervalSec = 1.0 / fps;
            int delayMs = Math.Clamp((int)Math.Round(1000.0 / fps), 7, 1000);
            using var frame = new Mat();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_appState != SimulatorAppState.MainMenu)
                {
                    await Task.Delay(120, cancellationToken);
                    continue;
                }

                if (!capture.Read(frame) || frame.Empty())
                {
                    capture.Set(VideoCaptureProperties.PosFrames, 0);
                    continue;
                }

                Bitmap bitmap = ConvertFrameToBitmap(frame);
                lock (_backgroundVideoSync)
                {
                    _backgroundVideoFrame?.Dispose();
                    _backgroundVideoFrame = bitmap;
                    _backgroundVideoFrameVersion++;
                }

                await Task.Delay(delayMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static Bitmap ConvertFrameToBitmap(Mat frame)
    {
        Mat source = frame;
        Mat? resized = null;
        try
        {
            if (frame.Width > BackgroundVideoMaxDecodeWidth || frame.Height > BackgroundVideoMaxDecodeHeight)
            {
                double scale = Math.Min(
                    BackgroundVideoMaxDecodeWidth / (double)Math.Max(1, frame.Width),
                    BackgroundVideoMaxDecodeHeight / (double)Math.Max(1, frame.Height));
                int width = Math.Max(1, (int)Math.Round(frame.Width * scale));
                int height = Math.Max(1, (int)Math.Round(frame.Height * scale));
                resized = new Mat();
                Cv2.Resize(frame, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Area);
                source = resized;
            }

            return ConvertFrameToBitmapCore(source);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    private static Bitmap ConvertFrameToBitmapCore(Mat frame)
    {
        using var bgra = new Mat();
        switch (frame.Channels())
        {
            case 4:
                frame.CopyTo(bgra);
                break;
            case 3:
                Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);
                break;
            default:
                Cv2.CvtColor(frame, bgra, ColorConversionCodes.GRAY2BGRA);
                break;
        }

        var bitmap = new Bitmap(bgra.Width, bgra.Height, PixelFormat.Format32bppArgb);
        Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bitmap.Width * 4;
            byte[] row = new byte[rowBytes];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(bgra.Data, y * (int)bgra.Step()), row, 0, rowBytes);
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
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
        double interval = _backgroundVideoFrameIntervalSec;
        return double.IsFinite(interval) && interval > 1e-6
            ? Math.Clamp(interval, 1.0 / 144.0, 1.0)
            : MainMenuTargetFrameIntervalSec;
    }
}
