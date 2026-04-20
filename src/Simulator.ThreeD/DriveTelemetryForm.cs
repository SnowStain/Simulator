using System.Diagnostics;
using System.Drawing.Drawing2D;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed class DriveTelemetryForm : Form
{
    private readonly Simulator3dHost _host;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Queue<PointF> _wheelPowerSamples = new();
    private readonly Queue<PointF> _superCapSamples = new();
    private readonly Font _titleFont = new("Microsoft YaHei UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _textFont = new("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public DriveTelemetryForm(Simulator3dHost host)
    {
        _host = host;
        Text = "RM26 Drive Telemetry";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(760, 420);
        MinimumSize = new Size(620, 320);
        BackColor = Color.FromArgb(18, 22, 28);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _timer = new System.Windows.Forms.Timer { Interval = 100 };
        _timer.Tick += (_, _) =>
        {
            CaptureSample();
            Invalidate();
        };
        _timer.Start();
        CaptureSample();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _titleFont.Dispose();
            _textFont.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        SimulationEntity? entity = _host.SelectedEntity;
        string title = entity is null ? "No controlled entity" : $"{entity.Id} telemetry";
        using var titleBrush = new SolidBrush(Color.FromArgb(236, 242, 248));
        using var textBrush = new SolidBrush(Color.FromArgb(198, 208, 220));
        g.DrawString(title, _titleFont, titleBrush, 18, 14);

        Rectangle powerRect = new(18, 54, ClientSize.Width - 36, (ClientSize.Height - 86) / 2);
        Rectangle capRect = new(18, powerRect.Bottom + 12, ClientSize.Width - 36, ClientSize.Height - powerRect.Bottom - 30);
        DrawChart(g, powerRect, _wheelPowerSamples, "Wheel Motor Power", "W", Color.FromArgb(90, 170, 245));
        DrawChart(g, capRect, _superCapSamples, "SuperCap Energy", "J", Color.FromArgb(255, 210, 76));

        if (entity is not null)
        {
            double wheelCount = string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase)
                ? 4.0
                : Math.Max(2.0, entity.WheelOffsetsM.Count);
            string footer = $"Current wheel {entity.ChassisPowerDrawW / Math.Max(1.0, wheelCount):0.0}W   total {entity.ChassisPowerDrawW:0.0}W   SC {entity.SuperCapEnergyJ:0.0}/{Math.Max(1.0, entity.MaxSuperCapEnergyJ):0.0}J";
            g.DrawString(footer, _textFont, textBrush, 18, ClientSize.Height - 22);
        }
    }

    private void CaptureSample()
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null)
        {
            return;
        }

        float timeSec = (float)_clock.Elapsed.TotalSeconds;
        double wheelCount = string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase)
            ? 4.0
            : Math.Max(2.0, entity.WheelOffsetsM.Count);
        EnqueueSample(_wheelPowerSamples, new PointF(timeSec, (float)(entity.ChassisPowerDrawW / Math.Max(1.0, wheelCount))));
        EnqueueSample(_superCapSamples, new PointF(timeSec, (float)entity.SuperCapEnergyJ));
    }

    private static void EnqueueSample(Queue<PointF> queue, PointF point)
    {
        queue.Enqueue(point);
        while (queue.Count > 180)
        {
            queue.Dequeue();
        }
    }

    private void DrawChart(Graphics graphics, Rectangle rect, Queue<PointF> samples, string label, string unit, Color lineColor)
    {
        using var panelBrush = new SolidBrush(Color.FromArgb(210, 16, 20, 26));
        using var panelPen = new Pen(Color.FromArgb(114, 126, 144, 166), 1f);
        graphics.FillRectangle(panelBrush, rect);
        graphics.DrawRectangle(panelPen, rect);
        using var titleBrush = new SolidBrush(Color.FromArgb(236, 240, 246));
        using var gridPen = new Pen(Color.FromArgb(42, 180, 190, 204), 1f);
        using var linePen = new Pen(lineColor, 2f);
        using var valueBrush = new SolidBrush(lineColor);
        graphics.DrawString(label, _textFont, titleBrush, rect.X + 10, rect.Y + 8);

        Rectangle plot = new(rect.X + 10, rect.Y + 28, rect.Width - 20, rect.Height - 40);
        for (int i = 1; i < 4; i++)
        {
            int y = plot.Y + plot.Height * i / 4;
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
        }

        if (samples.Count < 2)
        {
            graphics.DrawString("--", _textFont, valueBrush, plot.Right - 26, rect.Y + 8);
            return;
        }

        PointF[] array = samples.ToArray();
        float minX = array[0].X;
        float maxX = array[^1].X;
        float maxValue = Math.Max(1f, array.Max(point => point.Y) * 1.08f);
        var points = new List<PointF>(array.Length);
        foreach (PointF point in array)
        {
            float x = plot.Left + (point.X - minX) / Math.Max(0.001f, maxX - minX) * plot.Width;
            float y = plot.Bottom - point.Y / maxValue * plot.Height;
            points.Add(new PointF(x, y));
        }

        graphics.DrawLines(linePen, points.ToArray());
        PointF latest = points[^1];
        graphics.FillEllipse(valueBrush, latest.X - 3f, latest.Y - 3f, 6f, 6f);
        graphics.DrawString($"{array[^1].Y:0.0}{unit}", _textFont, valueBrush, plot.Right - 58, rect.Y + 8);
    }
}
