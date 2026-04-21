using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using Simulator.Core.Map;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal sealed class MapPresetPreviewControl : Control
{
    private enum PreviewMode
    {
        ThreeD,
        Split,
        Top,
    }

    private readonly List<(PreviewMode Mode, Rectangle Rect)> _tabs = new();
    private string? _loadedBitmapPath;
    private Bitmap? _mapBitmap;
    private PreviewMode _mode = PreviewMode.ThreeD;
    private float _yawRad = 0.18f;
    private float _pitchRad = 1.34f;
    private float _zoomScale = 1.0f;
    private bool _dragging;
    private Point _lastMouse;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public MapPresetEditorSettings? Document { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedFacilityId { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedFacetId { get; set; }

    public MapPresetPreviewControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(18, 22, 28);
        ForeColor = Color.White;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mapBitmap?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
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
        _pitchRad = Math.Clamp(_pitchRad - dy * 0.01f, 0.18f, 1.48f);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _zoomScale = Math.Clamp(_zoomScale * (e.Delta > 0 ? 1.12f : 0.90f), 0.45f, 4.0f);
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
        e.Graphics.DrawString("3D Terrain Preview", Font, titleBrush, 12, 10);
        e.Graphics.DrawString("\u53f3\u952e\u62d6\u52a8\u65cb\u8f6c\uff0c\u6eda\u8f6e\u7f29\u653e\uff0c\u9ed8\u8ba4\u4ece\u4e0a\u5f80\u4e0b\u770b\u3002", SystemFonts.DefaultFont, subBrush, 12, 30);

        DrawTabs(e.Graphics, new Rectangle(12, 50, Math.Max(220, bounds.Width - 24), 30));

        if (Document is null || Document.Width <= 0 || Document.Height <= 0)
        {
            return;
        }

        Rectangle panel = new(12, 86, bounds.Width - 24, bounds.Height - 98);
        using var fill = new SolidBrush(Color.FromArgb(24, 30, 38));
        using var border = new Pen(Color.FromArgb(72, 84, 98));
        e.Graphics.FillRectangle(fill, panel);
        e.Graphics.DrawRectangle(border, panel);

        if (_mode == PreviewMode.Top)
        {
            DrawTopPreview(e.Graphics, panel);
        }
        else if (_mode == PreviewMode.Split)
        {
            Rectangle left = new(panel.X + 8, panel.Y + 8, (panel.Width - 24) / 2, panel.Height - 16);
            Rectangle right = new(left.Right + 8, panel.Y + 8, panel.Width - left.Width - 24, panel.Height - 16);
            DrawTopPreview(e.Graphics, left);
            DrawThreeDPreview(e.Graphics, right);
        }
        else
        {
            DrawThreeDPreview(e.Graphics, panel);
        }
    }

    private void DrawTabs(Graphics graphics, Rectangle rect)
    {
        _tabs.Clear();
        string[] labels = { "3D", "Split", "Top" };
        PreviewMode[] modes = { PreviewMode.ThreeD, PreviewMode.Split, PreviewMode.Top };
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

    private void DrawTopPreview(Graphics graphics, Rectangle panel)
    {
        Rectangle mapRect = FitRect(panel, Document!.Width, Document.Height, 10);
        TryEnsureBitmapLoaded();
        if (_mapBitmap is not null)
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(_mapBitmap, mapRect);
        }
        else
        {
            using var fallback = new SolidBrush(Color.FromArgb(42, 54, 64));
            graphics.FillRectangle(fallback, mapRect);
        }

        foreach (TerrainFacetEditorModel facet in Document.TerrainFacets)
        {
            PointF[] points = facet.ParsePoints()
                .Select(point => ToPoint(mapRect, Document.Width, Document.Height, point.X, point.Y))
                .ToArray();
            if (points.Length < 3)
            {
                continue;
            }

            Color color = TryParseColor(facet.TopColorHex, Color.FromArgb(118, 138, 116));
            using var fill = new SolidBrush(Color.FromArgb(string.Equals(facet.Id, SelectedFacetId, StringComparison.OrdinalIgnoreCase) ? 188 : 124, color));
            using var pen = new Pen(Color.FromArgb(220, color), string.Equals(facet.Id, SelectedFacetId, StringComparison.OrdinalIgnoreCase) ? 2f : 1.2f);
            graphics.FillPolygon(fill, points);
            graphics.DrawPolygon(pen, points);
        }

        using var overlayPen = new Pen(Color.FromArgb(210, 242, 245, 250), 1.3f);
        using var selectedPen = new Pen(Color.FromArgb(255, 255, 208, 74), 2.2f);
        using var pointBrush = new SolidBrush(Color.FromArgb(255, 255, 208, 74));
        foreach (FacilityRegionEditorModel facility in Document.Facilities)
        {
            bool selected = string.Equals(facility.Id, SelectedFacilityId, StringComparison.OrdinalIgnoreCase);
            Pen pen = selected ? selectedPen : overlayPen;
            DrawFacility(graphics, mapRect, Document.Width, Document.Height, facility, pen, pointBrush, selected);
        }

        using var footerBrush = new SolidBrush(Color.FromArgb(188, 204, 214));
        string footer = $"{Document.PresetName}   {Document.Width}x{Document.Height}   facilities={Document.Facilities.Count}   facets={Document.TerrainFacets.Count}";
        graphics.DrawString(footer, SystemFonts.DefaultFont, footerBrush, panel.X + 8, panel.Bottom - 20);
    }

    private void DrawThreeDPreview(Graphics graphics, Rectangle panel)
    {
        Rectangle viewport = Rectangle.Inflate(panel, -10, -10);
        using var fill = new SolidBrush(Color.FromArgb(16, 20, 26));
        using var border = new Pen(Color.FromArgb(64, 80, 96));
        graphics.FillRectangle(fill, viewport);
        graphics.DrawRectangle(border, viewport);

        var faces = new List<Preview3dFace>();
        float width = Math.Max(1f, (float)Math.Max(1.0, Document!.FieldLengthM));
        float height = Math.Max(1f, (float)Math.Max(1.0, Document.FieldWidthM));
        float maxDim = Math.Max(width, height);
        float baseThickness = 0.02f;
        AddTerrainTopPlane(faces, width, height);
        int baseFaceCount = faces.Count;

        foreach (FacilityRegionEditorModel facility in Document.Facilities)
        {
            if (!ShouldRenderFacilityInThreeD(facility))
            {
                continue;
            }

            AddFacilityPreview(faces, facility);
        }

        foreach (TerrainFacetEditorModel facet in Document.TerrainFacets)
        {
            AddFacetPreview(faces, facet);
        }

        float maxHeight = Math.Max(
            baseThickness,
            Math.Max(
                (float)Document.Facilities.Select(f => Math.Max(0.15, f.HeightM)).DefaultIfEmpty(0.15).Max(),
                (float)Document.TerrainFacets.SelectMany(f => f.ParseHeights()).DefaultIfEmpty(0.0).Max()));
        float sceneHeight = Math.Max(maxHeight + baseThickness * 1.35f, maxDim * 0.18f);
        float scale = Math.Min(viewport.Width / Math.Max(1f, width), viewport.Height / Math.Max(1f, sceneHeight)) * 0.86f * _zoomScale;
        Vector3 center = new(width * 0.5f, sceneHeight * 0.24f, height * 0.5f);
        DrawPreviewFaces(graphics, viewport, center, faces, 0, baseFaceCount, scale);
        DrawProjectedTopBitmap(graphics, viewport, center, width, height, scale);
        DrawPreviewFaces(graphics, viewport, center, faces, baseFaceCount, faces.Count - baseFaceCount, scale);

        using var labelBrush = new SolidBrush(Color.FromArgb(184, 198, 208));
        graphics.DrawString("3D terrain / facilities / slope facets", SystemFonts.DefaultFont, labelBrush, viewport.X + 8, viewport.Y + 8);
    }

    private static void AddTerrainTopPlane(ICollection<Preview3dFace> faces, float width, float height)
    {
        faces.Add(new Preview3dFace(
            new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(width, 0f, 0f),
                new Vector3(width, 0f, height),
                new Vector3(0f, 0f, height),
            },
            Color.FromArgb(42, 48, 56)));
    }

    private void DrawPreviewFaces(
        Graphics graphics,
        Rectangle viewport,
        Vector3 center,
        IReadOnlyList<Preview3dFace> faces,
        int startIndex,
        int count,
        float scale)
    {
        if (count <= 0)
        {
            return;
        }

        var slice = new Preview3dFace[count];
        for (int index = 0; index < count; index++)
        {
            slice[index] = faces[startIndex + index];
        }

        foreach ((PointF[] points, _, Color color) in Preview3dPrimitives.ProjectFaces(slice, viewport, center, _yawRad, _pitchRad, scale))
        {
            using var faceBrush = new SolidBrush(color);
            using var facePen = new Pen(Color.FromArgb(120, 12, 16, 20), 1f);
            if (points.Length >= 3)
            {
                graphics.FillPolygon(faceBrush, points);
                graphics.DrawPolygon(facePen, points);
            }
        }
    }

    private void DrawProjectedTopBitmap(Graphics graphics, Rectangle viewport, Vector3 center, float width, float height, float scale)
    {
        TryEnsureBitmapLoaded();
        if (_mapBitmap is null)
        {
            return;
        }

        PointF[] projected = Preview3dPrimitives.ProjectPoints(
            new[]
            {
                new Vector3(0f, 0.018f, 0f),
                new Vector3(width, 0.018f, 0f),
                new Vector3(0f, 0.018f, height),
                new Vector3(width, 0.018f, height),
            },
            viewport,
            center,
            _yawRad,
            _pitchRad,
            scale);
        if (projected.Length < 4)
        {
            return;
        }

        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(_mapBitmap, new[] { projected[0], projected[1], projected[2] });
        using var edge = new Pen(Color.FromArgb(120, 186, 210, 194), 1.2f);
        graphics.DrawPolygon(edge, new[] { projected[0], projected[1], projected[3], projected[2] });
    }

    private void AddFacilityPreview(ICollection<Preview3dFace> faces, FacilityRegionEditorModel facility)
    {
        Color color = ResolveFacilityColor(facility);
        float extrude = Math.Max(0.06f, (float)Math.Max(0.15, facility.HeightM));
        if (string.Equals(facility.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            float startX = MapXToPreviewMeters(facility.X1);
            float startY = MapYToPreviewMeters(facility.Y1);
            float endX = MapXToPreviewMeters(facility.X2);
            float endY = MapYToPreviewMeters(facility.Y2);
            float length = Math.Max(0.08f, (float)Math.Sqrt(Math.Pow(endX - startX, 2) + Math.Pow(endY - startY, 2)));
            Preview3dPrimitives.AddBox(
                faces,
                new Vector3((startX + endX) * 0.5f, extrude * 0.5f, (startY + endY) * 0.5f),
                new Vector3(length * 0.5f, extrude * 0.5f, Math.Max(0.03f, MapXToPreviewMeters(facility.Thickness) * 0.5f)),
                color,
                -(float)Math.Atan2(endY - startY, endX - startX));
            return;
        }

        IReadOnlyList<Point2D> points = string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase)
            ? facility.ParsePoints()
            : new[]
            {
                new Point2D(Math.Min(facility.X1, facility.X2), Math.Min(facility.Y1, facility.Y2)),
                new Point2D(Math.Max(facility.X1, facility.X2), Math.Min(facility.Y1, facility.Y2)),
                new Point2D(Math.Max(facility.X1, facility.X2), Math.Max(facility.Y1, facility.Y2)),
                new Point2D(Math.Min(facility.X1, facility.X2), Math.Max(facility.Y1, facility.Y2)),
            };
        if (points.Count < 3)
        {
            return;
        }

        List<Vector3> bottom = points
            .Select(point => new Vector3(MapXToPreviewMeters(point.X), 0f, MapYToPreviewMeters(point.Y)))
            .ToList();
        List<Vector3> topVertices = points
            .Select(point => new Vector3(MapXToPreviewMeters(point.X), extrude, MapYToPreviewMeters(point.Y)))
            .ToList();
        Preview3dPrimitives.AddPrism(faces, bottom, topVertices, color);
    }

    private static bool ShouldRenderFacilityInThreeD(FacilityRegionEditorModel facility)
    {
        if (string.Equals(facility.Type, "boundary", StringComparison.OrdinalIgnoreCase)
            || facility.Type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(facility.Type, "supply", StringComparison.OrdinalIgnoreCase)
            || string.Equals(facility.Type, "mining_area", StringComparison.OrdinalIgnoreCase)
            || string.Equals(facility.Type, "mineral_exchange", StringComparison.OrdinalIgnoreCase)
            || string.Equals(facility.Type, "dog_hole", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (facility.HeightM > 0.02)
        {
            return true;
        }

        return string.Equals(facility.Type, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(facility.Type, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(facility.Type, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
    }

    private void AddFacetPreview(ICollection<Preview3dFace> faces, TerrainFacetEditorModel facet)
    {
        IReadOnlyList<Point2D> points = facet.ParsePoints();
        if (points.Count < 3)
        {
            return;
        }

        IReadOnlyList<double> heights = facet.ParseHeights();
        var bottom = new List<Vector3>();
        var topVertices = new List<Vector3>();
        for (int index = 0; index < points.Count; index++)
        {
            Point2D point = points[index];
            float heightM = (float)(index < heights.Count ? heights[index] : (heights.Count == 0 ? 0.0 : heights[^1]));
            float x = MapXToPreviewMeters(point.X);
            float y = MapYToPreviewMeters(point.Y);
            bottom.Add(new Vector3(x, 0f, y));
            topVertices.Add(new Vector3(x, Math.Max(0.01f, heightM), y));
        }

        Color color = TryParseColor(facet.TopColorHex, Color.FromArgb(136, 156, 112));
        Preview3dPrimitives.AddPrism(faces, bottom, topVertices, color);
    }

    private float MapXToPreviewMeters(double mapX)
    {
        if (Document is null)
        {
            return 0f;
        }

        double widthUnits = Math.Max(1.0, Document.Width);
        double fieldLength = Math.Max(1.0, Document.FieldLengthM);
        return (float)(mapX / widthUnits * fieldLength);
    }

    private float MapYToPreviewMeters(double mapY)
    {
        if (Document is null)
        {
            return 0f;
        }

        double heightUnits = Math.Max(1.0, Document.Height);
        double fieldWidth = Math.Max(1.0, Document.FieldWidthM);
        return (float)(mapY / heightUnits * fieldWidth);
    }

    private static Color ResolveFacilityColor(FacilityRegionEditorModel facility)
    {
        if (string.Equals(facility.Type, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(facility.Type, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(facility.Team, "blue", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(110, 156, 224)
                : Color.FromArgb(212, 112, 106);
        }

        if (string.Equals(facility.Type, "wall", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(160, 164, 166);
        }

        if (facility.Type.Contains("slope", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(142, 154, 116);
        }

        return Color.FromArgb(96, 180, 144);
    }

    private void TryEnsureBitmapLoaded()
    {
        string? bitmapPath = ResolveBitmapPath();
        if (string.Equals(bitmapPath, _loadedBitmapPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _mapBitmap?.Dispose();
        _mapBitmap = null;
        _loadedBitmapPath = bitmapPath;
        if (string.IsNullOrWhiteSpace(bitmapPath) || !File.Exists(bitmapPath))
        {
            return;
        }

        try
        {
            _mapBitmap = new Bitmap(bitmapPath);
        }
        catch
        {
            _mapBitmap = null;
        }
    }

    private string? ResolveBitmapPath()
    {
        if (Document is null || string.IsNullOrWhiteSpace(Document.SourcePath))
        {
            return null;
        }

        string baseDirectory = Path.GetDirectoryName(Document.SourcePath) ?? string.Empty;
        foreach (string? candidate in new[]
                 {
                     Document.TerrainSurface.BaseColorImagePath,
                     Document.ImagePath,
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string fullPath = Path.IsPathRooted(candidate)
                ? candidate
                : Path.GetFullPath(Path.Combine(baseDirectory, candidate));
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private static Rectangle FitRect(Rectangle bounds, int width, int height, int padding)
    {
        Rectangle content = Rectangle.Inflate(bounds, -padding, -padding);
        float scale = Math.Min(content.Width / (float)Math.Max(1, width), content.Height / (float)Math.Max(1, height));
        int drawWidth = Math.Max(1, (int)Math.Round(width * scale));
        int drawHeight = Math.Max(1, (int)Math.Round(height * scale));
        return new Rectangle(
            content.X + (content.Width - drawWidth) / 2,
            content.Y + (content.Height - drawHeight) / 2,
            drawWidth,
            drawHeight);
    }

    private static void DrawFacility(Graphics graphics, Rectangle mapRect, int mapWidth, int mapHeight, FacilityRegionEditorModel facility, Pen pen, Brush pointBrush, bool selected)
    {
        if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
        {
            PointF[] points = facility.ParsePoints()
                .Select(point => ToPoint(mapRect, mapWidth, mapHeight, point.X, point.Y))
                .ToArray();
            if (points.Length >= 3)
            {
                graphics.DrawPolygon(pen, points);
                if (selected)
                {
                    foreach (PointF point in points)
                    {
                        graphics.FillEllipse(pointBrush, point.X - 3, point.Y - 3, 6, 6);
                    }
                }

                return;
            }
        }

        if (string.Equals(facility.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            PointF a = ToPoint(mapRect, mapWidth, mapHeight, facility.X1, facility.Y1);
            PointF b = ToPoint(mapRect, mapWidth, mapHeight, facility.X2, facility.Y2);
            graphics.DrawLine(pen, a, b);
            return;
        }

        PointF topLeft = ToPoint(mapRect, mapWidth, mapHeight, Math.Min(facility.X1, facility.X2), Math.Min(facility.Y1, facility.Y2));
        PointF bottomRight = ToPoint(mapRect, mapWidth, mapHeight, Math.Max(facility.X1, facility.X2), Math.Max(facility.Y1, facility.Y2));
        graphics.DrawRectangle(
            pen,
            topLeft.X,
            topLeft.Y,
            Math.Max(1f, bottomRight.X - topLeft.X),
            Math.Max(1f, bottomRight.Y - topLeft.Y));
    }

    private static PointF ToPoint(Rectangle mapRect, int mapWidth, int mapHeight, double worldX, double worldY)
    {
        return new PointF(
            mapRect.X + (float)(worldX * mapRect.Width / Math.Max(1, mapWidth)),
            mapRect.Y + (float)(worldY * mapRect.Height / Math.Max(1, mapHeight)));
    }

    private static Color TryParseColor(string? colorHex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return fallback;
        }

        try
        {
            return ColorTranslator.FromHtml(colorHex);
        }
        catch
        {
            return fallback;
        }
    }
}
