using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using Simulator.Assets;

namespace Simulator.ThreeD;

internal sealed class AppearanceProfilePreviewControl : Control
{
    private enum PreviewMode
    {
        ThreeD,
        Split,
        Top,
        Side,
    }

    private readonly List<(PreviewMode Mode, Rectangle Rect)> _tabs = new();
    private PreviewMode _mode = PreviewMode.ThreeD;
    private float _yawRad = 0.82f;
    private float _pitchRad = 0.46f;
    private bool _dragging;
    private Point _lastMouse;

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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        foreach ((PreviewMode mode, Rectangle rect) in _tabs)
        {
            if (rect.Contains(e.Location))
            {
                _mode = mode;
                Invalidate();
                return;
            }
        }

        if (e.Button == MouseButtons.Right || (_mode == PreviewMode.ThreeD && e.Button == MouseButtons.Left))
        {
            _dragging = true;
            _lastMouse = e.Location;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        int dx = e.X - _lastMouse.X;
        int dy = e.Y - _lastMouse.Y;
        _lastMouse = e.Location;
        _yawRad += dx * 0.012f;
        _pitchRad = Math.Clamp(_pitchRad - dy * 0.01f, 0.12f, 1.18f);
        Invalidate();
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
        using var subBrush = new SolidBrush(Color.FromArgb(164, 176, 188));
        string title = string.IsNullOrWhiteSpace(SubtypeKey) ? RoleKey : $"{RoleKey} / {SubtypeKey}";
        e.Graphics.DrawString(title, Font, titleBrush, 12, 10);
        e.Graphics.DrawString("3D 为主视图，右键拖动旋转。", SystemFonts.DefaultFont, subBrush, 12, 30);
        DrawTabs(e.Graphics, new Rectangle(12, 50, bounds.Width - 24, 30));

        if (Profile is null)
        {
            return;
        }

        Rectangle panel = new(12, 86, bounds.Width - 24, bounds.Height - 116);
        using var fill = new SolidBrush(Color.FromArgb(24, 30, 38));
        using var border = new Pen(Color.FromArgb(72, 84, 98));
        e.Graphics.FillRectangle(fill, panel);
        e.Graphics.DrawRectangle(border, panel);

        if (_mode == PreviewMode.Top)
        {
            DrawTopView(e.Graphics, panel, Profile);
        }
        else if (_mode == PreviewMode.Side)
        {
            DrawSideView(e.Graphics, panel, Profile);
        }
        else if (_mode == PreviewMode.Split)
        {
            Rectangle left = new(panel.X + 8, panel.Y + 8, (panel.Width - 24) / 2, panel.Height - 16);
            Rectangle right = new(left.Right + 8, panel.Y + 8, panel.Width - left.Width - 24, panel.Height - 16);
            DrawTopView(e.Graphics, left, Profile);
            DrawSideView(e.Graphics, right, Profile);
        }
        else
        {
            DrawThreeDView(e.Graphics, panel, Profile);
        }

        DrawMetrics(e.Graphics, new Rectangle(12, bounds.Bottom - 24, bounds.Width - 24, 20), Profile);
    }

    private void DrawTabs(Graphics graphics, Rectangle rect)
    {
        _tabs.Clear();
        string[] labels = { "3D", "Split", "Top", "Side" };
        PreviewMode[] modes = { PreviewMode.ThreeD, PreviewMode.Split, PreviewMode.Top, PreviewMode.Side };
        int x = rect.X;
        for (int index = 0; index < labels.Length; index++)
        {
            Rectangle tab = new(x, rect.Y, 72, 28);
            bool active = _mode == modes[index];
            using var fill = new SolidBrush(active ? Color.FromArgb(72, 126, 214) : Color.FromArgb(36, 42, 50));
            using var border = new Pen(Color.FromArgb(84, 94, 108));
            using var text = new SolidBrush(Color.FromArgb(236, 242, 248));
            graphics.FillRectangle(fill, tab);
            graphics.DrawRectangle(border, tab);
            var size = graphics.MeasureString(labels[index], Font);
            graphics.DrawString(labels[index], Font, text, tab.X + (tab.Width - size.Width) * 0.5f, tab.Y + 5f);
            _tabs.Add((modes[index], tab));
            x += tab.Width + 8;
        }
    }

    private void DrawThreeDView(Graphics graphics, Rectangle rect, RobotAppearanceProfileDefinition profile)
    {
        Rectangle viewport = Rectangle.Inflate(rect, -10, -10);
        using var fill = new SolidBrush(Color.FromArgb(16, 20, 26));
        using var border = new Pen(Color.FromArgb(64, 80, 96));
        graphics.FillRectangle(fill, viewport);
        graphics.DrawRectangle(border, viewport);

        var faces = new List<Preview3dFace>();
        BuildRobotPreview(faces, profile);
        bool energyMechanism = string.Equals(profile.RoleKey, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
        float maxSpan = energyMechanism
            ? (float)Math.Max(
                3.0f,
                Math.Max(profile.StructureBaseLengthM, profile.StructureCantileverPairGapM + profile.StructureBaseLengthM) + 1.0f)
            : (float)Math.Max(
                0.5,
                Math.Max(profile.BodyLengthM + profile.BarrelLengthM + 0.5, profile.BodyWidthM + 0.5));
        float maxHeight = energyMechanism
            ? (float)Math.Max(
                1.8f,
                profile.StructureGroundClearanceM + profile.StructureBaseHeightM + profile.StructureFrameHeightM + profile.StructureRotorRadiusM + profile.StructureLampHeightM + 0.6f)
            : (float)Math.Max(
                0.5,
                profile.BodyClearanceM + profile.BodyHeightM + Math.Max(profile.GimbalHeightM + profile.GimbalBodyHeightM, profile.StructureTopArmorCenterHeightM) + 0.4);
        float scale = Math.Min(viewport.Width / Math.Max(0.4f, maxSpan), viewport.Height / Math.Max(0.4f, maxHeight)) * 0.72f;
        Vector3 center = new(0f, maxHeight * 0.36f, 0f);

        foreach ((PointF[] points, _, Color color) in Preview3dPrimitives.ProjectFaces(faces, viewport, center, _yawRad, _pitchRad, scale))
        {
            using var faceBrush = new SolidBrush(color);
            using var facePen = new Pen(Color.FromArgb(120, 12, 16, 20), 1f);
            if (points.Length >= 3)
            {
                graphics.FillPolygon(faceBrush, points);
                graphics.DrawPolygon(facePen, points);
            }
        }

        using var text = new SolidBrush(Color.FromArgb(176, 188, 200));
        graphics.DrawString("3D preview uses the same appearance parameters as runtime data.", SystemFonts.DefaultFont, text, viewport.X + 8, viewport.Y + 8);
    }

    private static void BuildRobotPreview(ICollection<Preview3dFace> faces, RobotAppearanceProfileDefinition profile)
    {
        if (string.Equals(profile.RoleKey, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            RobotAppearanceProfile runtimeProfile = AppearanceProfileCatalog.ResolvePreviewProfile(profile);
            EnergyRenderMesh mesh = EnergyMechanismGeometry.BuildPreviewPair(runtimeProfile, 0.0f);
            foreach (EnergyRenderPrism prism in mesh.Prisms)
            {
                Preview3dPrimitives.AddPrism(faces, prism.Bottom, prism.Top, prism.FillColor);
            }

            foreach (EnergyRenderBox box in mesh.Boxes)
            {
                Preview3dPrimitives.AddOrientedBox(faces, box.Center, box.Forward, box.Right, box.Up, box.Length, box.Width, box.Height, box.FillColor);
            }

            foreach (EnergyRenderCylinder cylinder in mesh.Cylinders)
            {
                Preview3dPrimitives.AddCylinder(faces, cylinder.Center, cylinder.NormalAxis, cylinder.UpAxis, cylinder.Radius, cylinder.Thickness, cylinder.FillColor, cylinder.Segments);
            }

            return;
        }

        float bodyHalfX = (float)Math.Max(0.05, profile.BodyLengthM * 0.5);
        float bodyHalfY = (float)Math.Max(0.05, profile.BodyHeightM * 0.5);
        float bodyHalfZ = (float)Math.Max(0.05, profile.BodyWidthM * Math.Max(0.4, profile.BodyRenderWidthScale) * 0.5);
        float bodyCenterY = (float)(profile.BodyClearanceM + profile.BodyHeightM * 0.5);
        Preview3dPrimitives.AddBox(faces, new Vector3(0f, bodyCenterY, 0f), new Vector3(bodyHalfX, bodyHalfY, bodyHalfZ), profile.BodyColor);

        foreach ((double x, double y) in profile.GetWheelOffsetsOrDefaults())
        {
            float radius = (float)Math.Max(0.03, Math.Max(profile.WheelRadiusM, profile.RearLegWheelRadiusM));
            Preview3dPrimitives.AddBox(
                faces,
                new Vector3((float)x, radius, (float)y),
                new Vector3(radius * 0.85f, radius, radius * 0.38f),
                profile.WheelColor);
        }

        if (profile.GimbalLengthM > 0.01 && profile.GimbalBodyHeightM > 0.01)
        {
            Preview3dPrimitives.AddBox(
                faces,
                new Vector3(
                    (float)profile.GimbalOffsetXM,
                    (float)(profile.GimbalHeightM + profile.GimbalBodyHeightM * 0.5),
                    (float)profile.GimbalOffsetYM),
                new Vector3(
                    (float)Math.Max(0.03, profile.GimbalLengthM * 0.5),
                    (float)Math.Max(0.03, profile.GimbalBodyHeightM * 0.5),
                    (float)Math.Max(0.03, profile.GimbalWidthM * 0.5)),
                profile.TurretColor);

            Preview3dPrimitives.AddBox(
                faces,
                new Vector3(
                    (float)(profile.GimbalOffsetXM + profile.GimbalLengthM * 0.5 + profile.BarrelLengthM * 0.5),
                    (float)(profile.GimbalHeightM + profile.GimbalBodyHeightM * 0.55),
                    (float)profile.GimbalOffsetYM),
                new Vector3(
                    (float)Math.Max(0.03, profile.BarrelLengthM * 0.5),
                    (float)Math.Max(0.01, profile.BarrelRadiusM),
                    (float)Math.Max(0.01, profile.BarrelRadiusM)),
                Color.FromArgb(226, 232, 238));
        }

        IReadOnlyList<double> orbitYaws = profile.ArmorOrbitYawsDeg.Count > 0
            ? profile.ArmorOrbitYawsDeg
            : new[] { 0d, 180d, 90d, 270d };
        foreach (double yawDeg in orbitYaws)
        {
            float yaw = (float)(yawDeg * Math.PI / 180.0);
            Vector3 offset = new(
                MathF.Cos(yaw) * (bodyHalfX + (float)profile.ArmorPlateGapM + (float)profile.ArmorPlateLengthM * 0.35f),
                bodyCenterY,
                MathF.Sin(yaw) * (bodyHalfZ + (float)profile.ArmorPlateGapM + (float)profile.ArmorPlateLengthM * 0.35f));
            Preview3dPrimitives.AddBox(
                faces,
                offset,
                new Vector3(
                    (float)Math.Max(0.02, profile.ArmorPlateLengthM * 0.5),
                    (float)Math.Max(0.02, profile.ArmorPlateHeightM * 0.5),
                    (float)Math.Max(0.01, profile.ArmorPlateWidthM * 0.12)),
                profile.ArmorColor,
                -yaw);
        }

        if (string.Equals(profile.RoleKey, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            Preview3dPrimitives.AddBox(
                faces,
                new Vector3(0f, (float)profile.StructureBodyTopHeightM * 0.5f, 0f),
                new Vector3(
                    (float)Math.Max(0.08, profile.BodyLengthM * 0.36),
                    (float)Math.Max(0.08, profile.StructureBodyTopHeightM * 0.5),
                    (float)Math.Max(0.08, profile.BodyWidthM * 0.36)),
                profile.BodyColor);
        }
        else if (string.Equals(profile.RoleKey, "base", StringComparison.OrdinalIgnoreCase))
        {
            Preview3dPrimitives.AddBox(
                faces,
                new Vector3(0f, (float)profile.BodyHeightM * 0.5f, 0f),
                new Vector3(
                    (float)Math.Max(0.08, profile.BodyLengthM * 0.46),
                    (float)Math.Max(0.08, profile.BodyHeightM * 0.5),
                    (float)Math.Max(0.08, profile.BodyWidthM * 0.46)),
                profile.BodyColor);
        }
    }

    private static void DrawTopView(Graphics graphics, Rectangle rect, RobotAppearanceProfileDefinition profile)
    {
        DrawPanelFrame(graphics, rect, "Top");
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
        DrawPanelFrame(graphics, rect, "Side");
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
