using System.Runtime.InteropServices;
using OpenCvSharp;
using Simulator.Platform.Media;

namespace Simulator.ThreeD;

internal sealed class OpenCvBackgroundVideoSource : IBackgroundVideoSource
{
    private const int MaxDecodeWidth = 960;
    private const int MaxDecodeHeight = 540;

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private BackgroundVideoFrame? _latestFrame;
    private double _frameIntervalSec = 1.0 / 90.0;
    private long _version;

    public double FrameIntervalSec
    {
        get
        {
            double interval = _frameIntervalSec;
            return double.IsFinite(interval) && interval > 1e-6
                ? Math.Clamp(interval, 1.0 / 144.0, 1.0)
                : 1.0 / 90.0;
        }
    }

    public void Start(string path, Func<bool> shouldDecode)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunLoop(path, shouldDecode, _cts.Token));
    }

    public void Stop()
    {
        CancellationTokenSource? cts = _cts;
        Task? task = _task;
        _cts = null;
        _task = null;
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
    }

    public bool TryGetLatestFrame(out BackgroundVideoFrame frame)
    {
        lock (_sync)
        {
            if (_latestFrame is null)
            {
                frame = null!;
                return false;
            }

            frame = _latestFrame;
            return true;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            _latestFrame = null;
        }
    }

    private async Task RunLoop(string path, Func<bool> shouldDecode, CancellationToken cancellationToken)
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

            fps = Math.Clamp(fps, 1.0, 30.0);
            _frameIntervalSec = 1.0 / fps;
            int delayMs = Math.Clamp((int)Math.Round(1000.0 / fps), 7, 1000);
            using var frame = new Mat();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!shouldDecode())
                {
                    await Task.Delay(120, cancellationToken);
                    continue;
                }

                if (!capture.Read(frame) || frame.Empty())
                {
                    capture.Set(VideoCaptureProperties.PosFrames, 0);
                    continue;
                }

                BackgroundVideoFrame converted = ConvertFrame(frame);
                lock (_sync)
                {
                    _latestFrame = converted;
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

    private BackgroundVideoFrame ConvertFrame(Mat frame)
    {
        Mat source = frame;
        Mat? resized = null;
        try
        {
            if (frame.Width > MaxDecodeWidth || frame.Height > MaxDecodeHeight)
            {
                double scale = Math.Min(
                    MaxDecodeWidth / (double)Math.Max(1, frame.Width),
                    MaxDecodeHeight / (double)Math.Max(1, frame.Height));
                int width = Math.Max(1, (int)Math.Round(frame.Width * scale));
                int height = Math.Max(1, (int)Math.Round(frame.Height * scale));
                resized = new Mat();
                Cv2.Resize(frame, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Area);
                source = resized;
            }

            return ConvertFrameCore(source);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    private BackgroundVideoFrame ConvertFrameCore(Mat frame)
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

        int rowBytes = bgra.Width * 4;
        byte[] buffer = new byte[rowBytes * bgra.Height];
        byte[] row = new byte[rowBytes];
        for (int y = 0; y < bgra.Height; y++)
        {
            Marshal.Copy(IntPtr.Add(bgra.Data, y * (int)bgra.Step()), row, 0, rowBytes);
            Buffer.BlockCopy(row, 0, buffer, y * rowBytes, rowBytes);
        }

        long version = Interlocked.Increment(ref _version);
        return new BackgroundVideoFrame(bgra.Width, bgra.Height, version, buffer);
    }
}
