using System.Drawing.Drawing2D;
using System.ComponentModel;
using Simulator.Assets;

namespace Simulator.ThreeD;

internal sealed class AppearanceProfilePreviewControl : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string RoleKey { get; set; } = string.Empty;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SubtypeKey { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public RobotAppearanceProfileDefinition? Profile { get; set; }

    public AppearanceProfilePreviewControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(20, 23, 28);
        ForeColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);

        Rectangle bounds = ClientRectangle;
        if (bounds.Width <= 4 || bounds.Height <= 4)
        {
            return;
        }

        using var titleBrush = new SolidBrush(Color.FromArgb(226, 232, 240));
        string title = string.IsNullOrWhiteSpace(SubtypeKey) ? RoleKey : $"{RoleKey} / {SubtypeKey}";
        e.Graphics.DrawString(title, Font, titleBrush, 12, 10);

        if (Profile is null)
        {
            return;
        }

        Rectangle topView = new(12, 40, bounds.Width - 24, (bounds.Height - 88) / 2);
        Rectangle sideView = new(12, topView.Bottom + 10, bounds.Width - 24, bounds.Height - topView.Height - 68);
        DrawPanelFrame(e.Graphics, topView, "Top View");
        DrawPanelFrame(e.Graphics, sideView, "Side View");
        DrawTopView(e.Graphics, topView, Profile);
        DrawSideView(e.Graphics, sideView, Profile);
        DrawMetrics(e.Graphics, new Rectangle(12, bounds.Bottom - 28, bounds.Width - 24, 20), Profile);
    }

    private static void DrawPanelFrame(Graphics graphics, Rectangle rect, string title)
    {
        using var fill = new SolidBrush(Color.FromArgb(24, 30, 38));
        using var border = new Pen(Color.FromArgb(72, 84, 98));
        using var text = new SolidBrush(Color.FromArgb(164, 174, 188));
        graphics.FillRectangle(fill, rect);
        graphics.DrawRectangle(border, rect);
        graphics.DrawString(title, SystemFonts.DefaultFont, text, rect.X + 8, rect.Y + 6);
    }

    private static void DrawTopView(Graphics graphics, Rectangle rect, RobotAppearanceProfileDefinition profile)
    {
        Rectangle content = Rectangle.Inflate(rect, -10, -28);
        double maxSpan = Math.Max(0.25, Math.Max(profile.BodyLengthM, profile.BodyWidthM) + 0.45);
        float scale = (float)(Math.Min(content.Width, content.Height) / maxSpan);
        PointF center = new(content.Left + content.Width * 0.5f, content.Top + content.Height * 0.56f);

        using var bodyBrush = new SolidBrush(profile.BodyColor);
        using var turretBrush = new SolidBrush(profile.TurretColor);
        using var wheelBrush = new SolidBrush(profile.WheelColor);
        using var armorBrush = new SolidBrush(profile.ArmorColor);
        using var barrelPen = new Pen(Color.FromArgb(234, 238, 242), 2f);

        float bodyLength = Math.Max(12f, (float)profile.BodyLengthM * scale);
        float bodyWidth = Math.Max(12f, (float)(profile.BodyWidthM * Math.Max(0.4, profile.BodyRenderWidthScale)) * scale);
        if (string.Equals(profile.BodyShape, "octagon", StringComparison.OrdinalIgnoreCase))
        {
            PointF[] octagon = BuildOctagon(center, bodyLength, bodyWidth);
            graphics.FillPolygon(bodyBrush, octagon);
            graphics.DrawPolygon(Pens.Black, octagon);
        }
        else
        {
            var bodyRect = RectangleF.FromLTRB(center.X - bodyLength * 0.5f, center.Y - bodyWidth * 0.5f, center.X + bodyLength * 0.5f, center.Y + bodyWidth * 0.5f);
            graphics.FillRectangle(bodyBrush, bodyRect);
            graphics.DrawRectangle(Pens.Black, Rectangle.Round(bodyRect));
        }

        foreach ((double x, double y) in profile.GetWheelOffsetsOrDefaults())
        {
            float wheelX = center.X + (float)x * scale;
            float wheelY = center.Y + (float)y * scale;
            float radius = Math.Max(4f, (float)profile.WheelRadiusM * scale);
            graphics.FillEllipse(wheelBrush, wheelX - radius, wheelY - radius, radius * 2f, radius * 2f);
            graphics.DrawEllipse(Pens.Black, wheelX - radius, wheelY - radius, radius * 2f, radius * 2f);
        }

        DrawArmorRing(graphics, center, scale, profile, armorBrush);

        float turretLength = Math.Max(8f, (float)profile.GimbalLengthM * scale);
        float turretWidth = Math.Max(6f, (float)profile.GimbalWidthM * scale);
        var turretRect = RectangleF.FromLTRB(
            center.X - turretLength * 0.5f + (float)profile.GimbalOffsetXM * scale,
            center.Y - turretWidth * 0.5f + (float)profile.GimbalOffsetYM * scale,
            center.X + turretLength * 0.5f + (float)profile.GimbalOffsetXM * scale,
            center.Y + turretWidth * 0.5f + (float)profile.GimbalOffsetYM * scale);
        if (turretLength > 8f && turretWidth > 6f)
        {
            graphics.FillRectangle(turretBrush, turretRect);
            graphics.DrawRectangle(Pens.Black, Rectangle.Round(turretRect));
        }

        float barrelLength = Math.Max(6f, (float)profile.BarrelLengthM * scale);
        graphics.DrawLine(
            barrelPen,
            turretRect.Right,
            center.Y + (float)profile.GimbalOffsetYM * scale,
            turretRect.Right + barrelLength,
            center.Y + (float)profile.GimbalOffsetYM * scale);
    }

    private static void DrawArmorRing(Graphics graphics, PointF center, float scale, RobotAppearanceProfileDefinition profile, Brush armorBrush)
    {
        IReadOnlyList<double> orbitYaws = profile.ArmorOrbitYawsDeg.Count > 0
            ? profile.ArmorOrbitYawsDeg
            : new[] { 0d, 180d, 90d, 270d };
        float radiusX = Math.Max(10f, (float)(profile.BodyLengthM * 0.5 + profile.ArmorPlateGapM + profile.ArmorPlateLengthM * 0.28) * scale);
        float radiusY = Math.Max(10f, (float)(profile.BodyWidthM * Math.Max(0.4, profile.BodyRenderWidthScale) * 0.5 + profile.ArmorPlateGapM + profile.ArmorPlateLengthM * 0.28) * scale);
        float plateW = Math.Max(6f, (float)profile.ArmorPlateLengthM * scale);
        float plateH = Math.Max(5f, (float)profile.ArmorPlateWidthM * scale * 0.44f);
        foreach (double yawDeg in orbitYaws)
        {
            double rad = yawDeg * Math.PI / 180.0;
            float x = center.X + (float)Math.Cos(rad) * radiusX;
            float y = center.Y + (float)Math.Sin(rad) * radiusY;
            var plateRect = RectangleF.FromLTRB(x - plateW * 0.5f, y - plateH * 0.5f, x + plateW * 0.5f, y + plateH * 0.5f);
            graphics.FillRectangle(armorBrush, plateRect);
            graphics.DrawRectangle(Pens.Black, Rectangle.Round(plateRect));
        }
    }

    private static void DrawSideView(Graphics graphics, Rectangle rect, RobotAppearanceProfileDefinition profile)
    {
        Rectangle content = Rectangle.Inflate(rect, -10, -28);
        float groundY = content.Bottom - 14;
        using var groundPen = new Pen(Color.FromArgb(86, 98, 112), 1f);
        graphics.DrawLine(groundPen, content.Left, groundY, content.Right, groundY);

        double maxHeightM = Math.Max(
            Math.Max(profile.BodyClearanceM + profile.BodyHeightM + profile.GimbalHeightM + profile.GimbalBodyHeightM + profile.BarrelRadiusM, 0.3),
            Math.Max(profile.StructureTopArmorCenterHeightM + profile.ArmorPlateHeightM, profile.StructureRoofHeightM + profile.StructureDetectorHeightM) + 0.15);
        float scaleY = (float)((content.Height - 24) / Math.Max(0.2, maxHeightM));
        float scaleX = (float)((content.Width - 30) / Math.Max(0.25, profile.BodyLengthM + profile.BarrelLengthM + 0.5));
        float scale = Math.Min(scaleX, scaleY);
        float centerX = content.Left + content.Width * 0.42f;

        using var bodyBrush = new SolidBrush(profile.BodyColor);
        using var turretBrush = new SolidBrush(profile.TurretColor);
        using var wheelBrush = new SolidBrush(profile.WheelColor);
        using var armorBrush = new SolidBrush(profile.ArmorColor);
        using var barrelPen = new Pen(Color.FromArgb(234, 238, 242), 2f);

        float bodyWidth = Math.Max(10f, (float)profile.BodyLengthM * scale);
        float bodyHeight = Math.Max(10f, (float)profile.BodyHeightM * scale);
        float bodyBase = groundY - Math.Max(0f, (float)profile.BodyClearanceM * scale) - bodyHeight;
        var bodyRect = RectangleF.FromLTRB(centerX - bodyWidth * 0.5f, bodyBase, centerX + bodyWidth * 0.5f, bodyBase + bodyHeight);
        graphics.FillRectangle(bodyBrush, bodyRect);
        graphics.DrawRectangle(Pens.Black, Rectangle.Round(bodyRect));

        foreach ((double x, _) in profile.GetWheelOffsetsOrDefaults())
        {
            float wheelCenterX = centerX + (float)x * scale;
            float radius = Math.Max(4f, (float)Math.Max(profile.WheelRadiusM, profile.RearLegWheelRadiusM) * scale);
            graphics.FillEllipse(wheelBrush, wheelCenterX - radius, groundY - radius * 2f, radius * 2f, radius * 2f);
            graphics.DrawEllipse(Pens.Black, wheelCenterX - radius, groundY - radius * 2f, radius * 2f, radius * 2f);
        }

        float armorPlateW = Math.Max(8f, (float)profile.ArmorPlateLengthM * scale);
        float armorPlateH = Math.Max(6f, (float)profile.ArmorPlateHeightM * scale);
        graphics.FillRectangle(armorBrush, centerX + bodyWidth * 0.5f + 4f, bodyBase + bodyHeight * 0.30f, armorPlateW, armorPlateH);
        graphics.FillRectangle(armorBrush, centerX - bodyWidth * 0.5f - armorPlateW - 4f, bodyBase + bodyHeight * 0.30f, armorPlateW, armorPlateH);

        float turretBaseY = groundY - (float)profile.GimbalHeightM * scale - Math.Max(8f, (float)profile.GimbalBodyHeightM * scale);
        float turretWidth = Math.Max(10f, (float)profile.GimbalLengthM * scale);
        float turretHeight = Math.Max(8f, (float)profile.GimbalBodyHeightM * scale);
        if (turretWidth > 10f && turretHeight > 8f)
        {
            var turretRect = RectangleF.FromLTRB(
                centerX - turretWidth * 0.5f + (float)profile.GimbalOffsetXM * scale,
                turretBaseY,
                centerX + turretWidth * 0.5f + (float)profile.GimbalOffsetXM * scale,
                turretBaseY + turretHeight);
            graphics.FillRectangle(turretBrush, turretRect);
            graphics.DrawRectangle(Pens.Black, Rectangle.Round(turretRect));
            graphics.DrawLine(barrelPen, turretRect.Right, turretRect.Top + turretHeight * 0.45f, turretRect.Right + Math.Max(8f, (float)profile.BarrelLengthM * scale), turretRect.Top + turretHeight * 0.45f);
        }

        if (string.Equals(profile.RoleKey, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            float towerRadius = Math.Max(10f, (float)profile.StructureTowerRadiusM * scale);
            graphics.FillEllipse(bodyBrush, centerX - towerRadius, groundY - Math.Max(0f, (float)profile.StructureBodyTopHeightM * scale), towerRadius * 2f, Math.Max(20f, (float)profile.StructureBodyTopHeightM * scale));
        }
        else if (string.Equals(profile.RoleKey, "base", StringComparison.OrdinalIgnoreCase))
        {
            float detectorW = Math.Max(10f, (float)profile.StructureDetectorWidthM * scale);
            float detectorH = Math.Max(6f, (float)profile.StructureDetectorHeightM * scale);
            float detectorY = groundY - (float)profile.StructureDetectorBridgeCenterHeightM * scale;
            graphics.FillRectangle(turretBrush, centerX - detectorW * 0.5f, detectorY, detectorW, detectorH);
            graphics.DrawRectangle(Pens.Black, Rectangle.Round(new RectangleF(centerX - detectorW * 0.5f, detectorY, detectorW, detectorH)));
        }

        if (profile.StructureTopArmorCenterHeightM > 0.01)
        {
            float plateCenterY = groundY - (float)profile.StructureTopArmorCenterHeightM * scale;
            float plateLen = Math.Max(8f, (float)profile.ArmorPlateLengthM * scale);
            float plateHeight = Math.Max(6f, (float)profile.ArmorPlateHeightM * scale);
            float tiltRad = (float)(profile.StructureTopArmorTiltDeg * Math.PI / 180.0);
            PointF a = new(centerX - plateLen * 0.5f, plateCenterY + MathF.Sin(tiltRad) * plateHeight * 0.5f);
            PointF b = new(centerX + plateLen * 0.5f, plateCenterY - MathF.Sin(tiltRad) * plateHeight * 0.5f);
            using var structurePen = new Pen(profile.ArmorColor, 3f);
            graphics.DrawLine(structurePen, a, b);
        }
    }

    private static void DrawMetrics(Graphics graphics, Rectangle rect, RobotAppearanceProfileDefinition profile)
    {
        string text = $"Body {profile.BodyLengthM:0.###} x {profile.BodyWidthM:0.###} x {profile.BodyHeightM:0.###} m   "
            + $"Clearance {profile.BodyClearanceM:0.###} m   "
            + $"Wheel {profile.WheelRadiusM:0.###} m   "
            + $"Power {profile.ChassisDrivePowerLimitW:0.###} W";
        using var brush = new SolidBrush(Color.FromArgb(172, 182, 194));
        graphics.DrawString(text, SystemFonts.DefaultFont, brush, rect.Location);
    }

    private static PointF[] BuildOctagon(PointF center, float width, float height)
    {
        float cutX = width * 0.22f;
        float cutY = height * 0.22f;
        return new[]
        {
            new PointF(center.X - width * 0.5f + cutX, center.Y - height * 0.5f),
            new PointF(center.X + width * 0.5f - cutX, center.Y - height * 0.5f),
            new PointF(center.X + width * 0.5f, center.Y - height * 0.5f + cutY),
            new PointF(center.X + width * 0.5f, center.Y + height * 0.5f - cutY),
            new PointF(center.X + width * 0.5f - cutX, center.Y + height * 0.5f),
            new PointF(center.X - width * 0.5f + cutX, center.Y + height * 0.5f),
            new PointF(center.X - width * 0.5f, center.Y + height * 0.5f - cutY),
            new PointF(center.X - width * 0.5f, center.Y - height * 0.5f + cutY),
        };
    }
}
