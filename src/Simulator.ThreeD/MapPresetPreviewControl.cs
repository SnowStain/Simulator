using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Text.Json;
using Simulator.Core;
using Simulator.Core.Map;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal sealed class MapPresetPreviewControl : Control
{
    private enum TopEditOperation
    {
        None,
        MoveFacility,
        MoveFacet,
        EditFacilityVertex,
        EditFacetVertex,
    }

    private readonly record struct EditableFacilitySnapshot(
        string Shape,
        double X1,
        double Y1,
        double X2,
        double Y2,
        double Thickness,
        Point2D[] Points);

    private readonly record struct EditableFacetSnapshot(Point2D[] Points);

    private enum PreviewMode
    {
        ThreeD,
        Split,
        Top,
    }

    private readonly List<(PreviewMode Mode, Rectangle Rect)> _tabs = new();
    private readonly RuntimeGridLoader _runtimeGridLoader = new();
    private readonly ProjectLayout _layout = ProjectLayout.Discover();
    private IReadOnlyList<FineTerrainPreviewMarker> _overlayMarkers = Array.Empty<FineTerrainPreviewMarker>();
    private string? _selectedOverlayMarkerId;
    private string? _loadedBitmapPath;
    private Bitmap? _mapBitmap;
    private MapPresetEditorSettings? _document;
    private List<Preview3dFace>? _cachedThreeDFaces;
    private (Vector3 Min, Vector3 Max) _cachedSceneBounds;
    private bool _sceneDirty = true;
    private PreviewMode _mode = PreviewMode.Top;
    private float _yawRad = 0.0f;
    private float _pitchRad = 1.52f;
    private float _zoomScale = 1.0f;
    private bool _dragging;
    private bool _topEditing;
    private bool _selecting;
    private Point _lastMouse;
    private Point _selectionStartClient;
    private Point _selectionCurrentClient;
    private TopEditOperation _topEditOperation;
    private PointF _topEditStartMapPoint;
    private EditableFacilitySnapshot? _facilitySnapshot;
    private EditableFacetSnapshot? _facetSnapshot;
    private int _editVertexIndex = -1;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public MapPresetEditorSettings? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value))
            {
                return;
            }

            _document = value;
            ClearEditState();
            MarkSceneDirty();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedFacilityId { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedFacetId { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<FineTerrainPreviewMarker>? OverlayMarkers
    {
        get => _overlayMarkers;
        set
        {
            _overlayMarkers = value ?? Array.Empty<FineTerrainPreviewMarker>();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedOverlayMarkerId
    {
        get => _selectedOverlayMarkerId;
        set
        {
            if (string.Equals(_selectedOverlayMarkerId, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedOverlayMarkerId = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public RectangleF? MapSelection { get; private set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PointF? MapSelectionStart { get; private set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PointF? MapSelectionEnd { get; private set; }

    public event EventHandler? MapSelectionChanged;
    public event EventHandler? SelectionTargetChanged;
    public event EventHandler? DocumentEdited;
    public event EventHandler<FineTerrainPreviewMarkerEventArgs>? OverlayMarkerActivated;

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

        if (e.Button == MouseButtons.Left && TryGetTopMapRect(e.Location, out Rectangle mapRect))
        {
            PointF mapPoint = ToMapPoint(mapRect, e.Location);
            if (TryHitOverlayMarker(mapPoint, out FineTerrainPreviewMarker marker))
            {
                SelectedOverlayMarkerId = marker.Id;
                OverlayMarkerActivated?.Invoke(this, new FineTerrainPreviewMarkerEventArgs(marker));
                Invalidate();
                return;
            }

            if (TryBeginTopEdit(mapPoint))
            {
                Invalidate();
                return;
            }

            _selecting = true;
            _selectionStartClient = e.Location;
            _selectionCurrentClient = e.Location;
            UpdateMapSelection(e.Location);
            Invalidate();
            return;
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
        if (_topEditing)
        {
            _topEditing = false;
            _topEditOperation = TopEditOperation.None;
            _facilitySnapshot = null;
            _facetSnapshot = null;
            _editVertexIndex = -1;
            MarkSceneDirty();
            return;
        }

        if (_selecting)
        {
            UpdateMapSelection(e.Location);
            _selecting = false;
            Invalidate();
            return;
        }

        _dragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_topEditing && TryGetTopMapRect(e.Location, out Rectangle mapRect))
        {
            UpdateTopEdit(ToMapPoint(mapRect, e.Location));
            return;
        }

        if (_selecting)
        {
            UpdateMapSelection(e.Location);
            Invalidate();
            return;
        }

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

    public void MarkSceneDirty()
    {
        _sceneDirty = true;
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
        e.Graphics.DrawString("Runtime Parity Map Preview", Font, titleBrush, 12, 10);
        e.Graphics.DrawString("Right drag rotates 3D. In Top/Split, drag empty space to box-select, drag objects or vertices to edit.", SystemFonts.DefaultFont, subBrush, 12, 30);

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
        string[] labels = { "3D", "分屏", "俯视" };
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
            DrawTopDownScenePreview(graphics, mapRect);
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
            if (string.Equals(facet.Id, SelectedFacetId, StringComparison.OrdinalIgnoreCase))
            {
                using var vertexBrush = new SolidBrush(Color.FromArgb(255, 255, 208, 74));
                foreach (PointF point in points)
                {
                    graphics.FillEllipse(vertexBrush, point.X - 3, point.Y - 3, 6, 6);
                }
            }
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

        DrawOverlayMarkers(graphics, mapRect);
        DrawMapSelection(graphics, mapRect);

        using var footerBrush = new SolidBrush(Color.FromArgb(188, 204, 214));
        string footer = $"{Document.PresetName}   {Document.Width}x{Document.Height}   增益/互动={Document.Facilities.Count}   斜面={Document.TerrainFacets.Count}";
        graphics.DrawString(footer, SystemFonts.DefaultFont, footerBrush, panel.X + 8, panel.Bottom - 20);
    }

    private void DrawTopDownScenePreview(Graphics graphics, Rectangle mapRect)
    {
        EnsureThreeDSceneCache();
        if (Document is null || _cachedThreeDFaces is null || _cachedThreeDFaces.Count == 0)
        {
            return;
        }

        foreach (Preview3dFace face in _cachedThreeDFaces.OrderBy(candidate => candidate.Vertices.Average(vertex => vertex.Y)))
        {
            PointF[] points = face.Vertices
                .Select(vertex => PreviewPointToTopPoint(mapRect, vertex))
                .ToArray();
            if (points.Length < 3)
            {
                continue;
            }

            using var brush = new SolidBrush(Color.FromArgb(214, face.BaseColor));
            using var pen = new Pen(Color.FromArgb(56, 10, 14, 18), 1f);
            graphics.FillPolygon(brush, points);
            graphics.DrawPolygon(pen, points);
        }
    }

    private PointF PreviewPointToTopPoint(Rectangle mapRect, Vector3 point)
    {
        double fieldLength = Math.Max(1.0, Document?.FieldLengthM ?? 28.0);
        double fieldWidth = Math.Max(1.0, Document?.FieldWidthM ?? 15.0);
        double mapWidth = Math.Max(1.0, Document?.Width ?? 1.0);
        double mapHeight = Math.Max(1.0, Document?.Height ?? 1.0);
        double mapX = point.X / fieldLength * mapWidth;
        double mapY = point.Z / fieldWidth * mapHeight;
        return ToPoint(mapRect, (int)mapWidth, (int)mapHeight, mapX, mapY);
    }

    private void DrawMapSelection(Graphics graphics, Rectangle mapRect)
    {
        if (Document is null || MapSelection is not RectangleF selection)
        {
            return;
        }

        PointF topLeft = ToPoint(mapRect, Document.Width, Document.Height, selection.Left, selection.Top);
        PointF bottomRight = ToPoint(mapRect, Document.Width, Document.Height, selection.Right, selection.Bottom);
        var rect = RectangleF.FromLTRB(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));
        if (rect.Width < 1f || rect.Height < 1f)
        {
            return;
        }

        using var fill = new SolidBrush(Color.FromArgb(54, 255, 208, 74));
        using var pen = new Pen(Color.FromArgb(245, 255, 208, 74), 1.8f) { DashStyle = DashStyle.Dash };
        graphics.FillRectangle(fill, rect);
        graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private void DrawOverlayMarkers(Graphics graphics, Rectangle mapRect)
    {
        if (Document is null || _overlayMarkers.Count == 0)
        {
            return;
        }

        using var labelFont = new Font(Font.FontFamily, Math.Max(8f, Font.Size - 0.5f), FontStyle.Bold, GraphicsUnit.Point);
        foreach (FineTerrainPreviewMarker marker in _overlayMarkers)
        {
            PointF center = ToPoint(mapRect, Document.Width, Document.Height, marker.MapPoint.X, marker.MapPoint.Y);
            bool selected = string.Equals(marker.Id, SelectedOverlayMarkerId, StringComparison.OrdinalIgnoreCase);
            float radius = selected ? 7.5f : 5.5f;
            using var fill = new SolidBrush(Color.FromArgb(selected ? 244 : 214, marker.Color));
            using var edge = new Pen(selected ? Color.FromArgb(255, 255, 235, 132) : Color.FromArgb(220, 14, 18, 22), selected ? 2.2f : 1.4f);
            graphics.FillEllipse(fill, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
            graphics.DrawEllipse(edge, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
            graphics.DrawLine(edge, center.X - radius - 3f, center.Y, center.X + radius + 3f, center.Y);
            graphics.DrawLine(edge, center.X, center.Y - radius - 3f, center.X, center.Y + radius + 3f);

            string label = selected ? $"{marker.CompositeId}: {marker.Label}" : marker.CompositeId.ToString();
            SizeF labelSize = graphics.MeasureString(label, labelFont);
            RectangleF labelRect = new(
                center.X + radius + 6f,
                center.Y - labelSize.Height * 0.55f,
                labelSize.Width + 8f,
                labelSize.Height + 2f);
            using var labelBack = new SolidBrush(Color.FromArgb(selected ? 188 : 148, 18, 22, 28));
            using var labelBrush = new SolidBrush(Color.FromArgb(236, 242, 248));
            graphics.FillRectangle(labelBack, labelRect);
            graphics.DrawString(label, labelFont, labelBrush, labelRect.X + 4f, labelRect.Y + 1f);
        }
    }

    private bool TryHitOverlayMarker(PointF mapPoint, out FineTerrainPreviewMarker marker)
    {
        marker = default;
        if (_overlayMarkers.Count == 0)
        {
            return false;
        }

        float tolerance = 16f;
        float bestDistanceSq = float.MaxValue;
        bool found = false;
        foreach (FineTerrainPreviewMarker candidate in _overlayMarkers)
        {
            float dx = candidate.MapPoint.X - mapPoint.X;
            float dy = candidate.MapPoint.Y - mapPoint.Y;
            float distanceSq = dx * dx + dy * dy;
            if (distanceSq > tolerance * tolerance || distanceSq >= bestDistanceSq)
            {
                continue;
            }

            marker = candidate;
            bestDistanceSq = distanceSq;
            found = true;
        }

        return found;
    }

    private void DrawThreeDPreview(Graphics graphics, Rectangle panel)
    {
        Rectangle viewport = Rectangle.Inflate(panel, -10, -10);
        using var background = new LinearGradientBrush(
            viewport,
            Color.FromArgb(38, 52, 68),
            Color.FromArgb(14, 18, 24),
            LinearGradientMode.Vertical);
        using var border = new Pen(Color.FromArgb(64, 80, 96));
        graphics.FillRectangle(background, viewport);
        graphics.DrawRectangle(border, viewport);
        DrawThreeDBackdrop(graphics, viewport);

        EnsureThreeDSceneCache();
        IReadOnlyList<Preview3dFace> faces = _cachedThreeDFaces is null
            ? Array.Empty<Preview3dFace>()
            : _cachedThreeDFaces;
        if (faces.Count == 0)
        {
            return;
        }

        Vector3 min = _cachedSceneBounds.Min;
        Vector3 max = _cachedSceneBounds.Max;
        float spanX = Math.Max(0.4f, max.X - min.X);
        float spanZ = Math.Max(0.4f, max.Z - min.Z);
        float span = Math.Max(spanX, spanZ);
        float height = Math.Max(0.4f, max.Y - min.Y);
        float scale = Math.Min(viewport.Width / span, viewport.Height / Math.Max(0.4f, height + span * 0.22f)) * 0.84f * _zoomScale;
        Vector3 center = new(
            (min.X + max.X) * 0.5f,
            min.Y + height * 0.34f,
            (min.Z + max.Z) * 0.5f);
        DrawPreviewFaces(graphics, viewport, center, faces, scale);

        using var labelBrush = new SolidBrush(Color.FromArgb(184, 198, 208));
        graphics.DrawString("局内地形网格 + 场地单位预览 + 可编辑增益/斜面覆盖层", SystemFonts.DefaultFont, labelBrush, viewport.X + 8, viewport.Y + 8);
    }

    private void DrawPreviewFaces(
        Graphics graphics,
        Rectangle viewport,
        Vector3 center,
        IReadOnlyList<Preview3dFace> faces,
        float scale)
    {
        if (faces.Count == 0)
        {
            return;
        }

        var projectedFaces = Preview3dPrimitives.ProjectFaces(faces, viewport, center, _yawRad, _pitchRad, scale).ToArray();
        if (projectedFaces.Length == 0)
        {
            using var emptyBrush = new SolidBrush(Color.FromArgb(212, 228, 236, 244));
            graphics.DrawString("3D 预览暂未生成有效面片，已跳过异常几何。", SystemFonts.DefaultFont, emptyBrush, viewport.X + 12, viewport.Y + 34);
            return;
        }

        foreach ((PointF[] points, _, Color color) in projectedFaces)
        {
            using var faceBrush = new SolidBrush(color);
            using var facePen = new Pen(Color.FromArgb(136, 12, 18, 24), 1f);
            if (points.Length >= 3)
            {
                graphics.FillPolygon(faceBrush, points);
                graphics.DrawPolygon(facePen, points);
            }
        }
    }

    private static void DrawThreeDBackdrop(Graphics graphics, Rectangle viewport)
    {
        Rectangle upper = new(viewport.X, viewport.Y, viewport.Width, (int)(viewport.Height * 0.56f));
        Rectangle lower = new(viewport.X, upper.Bottom, viewport.Width, viewport.Bottom - upper.Bottom);
        using var sky = new LinearGradientBrush(
            upper,
            Color.FromArgb(78, 116, 150),
            Color.FromArgb(36, 54, 72),
            LinearGradientMode.Vertical);
        using var ground = new LinearGradientBrush(
            lower,
            Color.FromArgb(28, 38, 32),
            Color.FromArgb(18, 24, 22),
            LinearGradientMode.Vertical);
        graphics.FillRectangle(sky, upper);
        graphics.FillRectangle(ground, lower);

        using var haze = new SolidBrush(Color.FromArgb(36, 220, 236, 248));
        graphics.FillRectangle(haze, viewport.X, upper.Bottom - 8, viewport.Width, 18);
    }

    private void EnsureThreeDSceneCache()
    {
        if (!_sceneDirty && _cachedThreeDFaces is not null)
        {
            return;
        }

        _sceneDirty = false;
        _cachedThreeDFaces = new List<Preview3dFace>();
        if (Document is null)
        {
            _cachedSceneBounds = EditorPreviewGeometry.MeasureBounds(_cachedThreeDFaces);
            return;
        }

        MapPresetDefinition preset = BuildPreviewPreset();
        RuntimeGridData? runtimeGrid = _runtimeGridLoader.TryLoad(preset, out _);
        AppearanceProfileCatalog appearanceCatalog = AppearanceProfileCatalog.Load(_layout.AppearancePresetPath);
        TryEnsureBitmapLoaded();

        AddTerrainPreview(_cachedThreeDFaces, runtimeGrid);
        foreach (FacilityRegionEditorModel facility in Document.Facilities)
        {
            if (!ShouldRenderFacilityInThreeD(facility))
            {
                continue;
            }

            AddFacilityPreview(_cachedThreeDFaces, facility, runtimeGrid, appearanceCatalog);
        }

        if (runtimeGrid is null || !runtimeGrid.IsValid)
        {
            foreach (TerrainFacetEditorModel facet in Document.TerrainFacets)
            {
                AddFacetPreview(_cachedThreeDFaces, facet, runtimeGrid);
            }
        }

        _cachedSceneBounds = EditorPreviewGeometry.MeasureBounds(_cachedThreeDFaces);
    }

    private MapPresetDefinition BuildPreviewPreset()
    {
        TerrainSurfaceDefinition terrainSurface = new()
        {
            MapType = Document!.TerrainSurface.MapType,
            DescriptorPath = Document.TerrainSurface.DescriptorPath,
            StorageKind = Document.TerrainSurface.StorageKind,
            Topology = Document.TerrainSurface.Topology,
            MergeMode = Document.TerrainSurface.MergeMode,
            SplitMode = Document.TerrainSurface.SplitMode,
            BaseColorImagePath = Document.TerrainSurface.BaseColorImagePath,
            RenderProfile = Document.TerrainSurface.RenderProfile,
            TopFaceMode = Document.TerrainSurface.TopFaceMode,
            SideFaceMode = Document.TerrainSurface.SideFaceMode,
            SideColorHex = Document.TerrainSurface.SideColorHex,
            TopNormalThreshold = Document.TerrainSurface.TopNormalThreshold,
            SideNormalThreshold = Document.TerrainSurface.SideNormalThreshold,
            ResolutionM = Document.TerrainSurface.ResolutionM,
            HeightCells = Document.TerrainSurface.HeightCells,
            WidthCells = Document.TerrainSurface.WidthCells,
            HeightScaleBakedIn = Document.TerrainSurface.HeightScaleBakedIn,
            Channels = new Dictionary<string, string>(Document.TerrainSurface.Channels, StringComparer.OrdinalIgnoreCase),
            Facets = Document.TerrainFacets.Select(facet => new TerrainFacetDefinition
            {
                Id = facet.Id,
                Type = facet.Type,
                Team = facet.Team,
                TopColorHex = facet.TopColorHex,
                SideColorHex = facet.SideColorHex,
                CollisionEnabled = facet.CollisionEnabled,
                CollisionExpandM = facet.CollisionExpandM,
                CollisionHeightOffsetM = facet.CollisionHeightOffsetM,
                Points = facet.ParsePoints(),
                HeightsM = facet.ParseHeights(),
            }).ToArray(),
        };

        RuntimeGridDefinition runtimeGrid = new()
        {
            ResolutionM = Document.TerrainSurface.ResolutionM,
            HeightCells = Document.TerrainSurface.HeightCells,
            WidthCells = Document.TerrainSurface.WidthCells,
            HeightScaleBakedIn = Document.TerrainSurface.HeightScaleBakedIn,
            DescriptorPath = Document.TerrainSurface.DescriptorPath,
            StorageKind = Document.TerrainSurface.StorageKind,
            SurfaceTopology = Document.TerrainSurface.Topology,
            SurfaceMergeMode = Document.TerrainSurface.MergeMode,
            SurfaceSplitMode = Document.TerrainSurface.SplitMode,
            RenderProfile = Document.TerrainSurface.RenderProfile,
            TopFaceMode = Document.TerrainSurface.TopFaceMode,
            SideFaceMode = Document.TerrainSurface.SideFaceMode,
            SideColorHex = Document.TerrainSurface.SideColorHex,
            Channels = new Dictionary<string, string>(Document.TerrainSurface.Channels, StringComparer.OrdinalIgnoreCase),
        };

        return new MapPresetDefinition
        {
            Name = Document.PresetName,
            Width = Document.Width,
            Height = Document.Height,
            FieldLengthM = Document.FieldLengthM,
            FieldWidthM = Document.FieldWidthM,
            ImagePath = Document.ImagePath,
            SourcePath = Document.SourcePath,
            Facilities = Document.Facilities.Select(ToFacilityRegion).ToArray(),
            TerrainSurface = terrainSurface,
            RuntimeGrid = runtimeGrid,
        };
    }

    private void AddTerrainPreview(ICollection<Preview3dFace> faces, RuntimeGridData? runtimeGrid)
    {
        if (Document is null)
        {
            return;
        }

        Color sideColor = TryParseColor(Document.TerrainSurface.SideColorHex, Color.FromArgb(75, 79, 85));
        float fieldLength = (float)Math.Max(1.0, Document.FieldLengthM);
        float fieldWidth = (float)Math.Max(1.0, Document.FieldWidthM);
        const float terrainBaseDepthM = 0.05f;
        if (runtimeGrid is null || !runtimeGrid.IsValid)
        {
            Color topColor = _mapBitmap is null
                ? Color.FromArgb(58, 66, 74)
                : SampleBitmapColor(Document.Width * 0.5, Document.Height * 0.5, Color.FromArgb(58, 66, 74));
            AddTerrainBaseSlab(faces, fieldLength, fieldWidth, 0f, terrainBaseDepthM, topColor, sideColor);
            return;
        }

        float minTerrainHeight = float.MaxValue;
        float maxTerrainHeight = float.MinValue;

        int stepX = Math.Max(1, runtimeGrid.WidthCells / 96);
        int stepY = Math.Max(1, runtimeGrid.HeightCells / 96);
        for (int cellY = 0; cellY < runtimeGrid.HeightCells; cellY += stepY)
        {
            int nextCellY = Math.Min(runtimeGrid.HeightCells, cellY + stepY);
            float worldY1 = cellY * runtimeGrid.CellHeightWorld;
            float worldY2 = nextCellY * runtimeGrid.CellHeightWorld;
            for (int cellX = 0; cellX < runtimeGrid.WidthCells; cellX += stepX)
            {
                int nextCellX = Math.Min(runtimeGrid.WidthCells, cellX + stepX);
                float worldX1 = cellX * runtimeGrid.CellWidthWorld;
                float worldX2 = nextCellX * runtimeGrid.CellWidthWorld;
                float h00 = runtimeGrid.SampleHeightWithFacets(worldX1, worldY1);
                float h10 = runtimeGrid.SampleHeightWithFacets(worldX2, worldY1);
                float h11 = runtimeGrid.SampleHeightWithFacets(worldX2, worldY2);
                float h01 = runtimeGrid.SampleHeightWithFacets(worldX1, worldY2);
                minTerrainHeight = Math.Min(minTerrainHeight, Math.Min(Math.Min(h00, h10), Math.Min(h11, h01)));
                maxTerrainHeight = Math.Max(maxTerrainHeight, Math.Max(Math.Max(h00, h10), Math.Max(h11, h01)));
                double sampleX = (worldX1 + worldX2) * 0.5;
                double sampleY = (worldY1 + worldY2) * 0.5;
                Color topColor = SampleBitmapColor(sampleX, sampleY, ResolveTerrainFallbackColor(cellX, cellY, runtimeGrid));
                faces.Add(new Preview3dFace(
                    new[]
                    {
                        ToPreviewPoint(worldX1, worldY1, h00),
                        ToPreviewPoint(worldX2, worldY1, h10),
                        ToPreviewPoint(worldX2, worldY2, h11),
                        ToPreviewPoint(worldX1, worldY2, h01),
                    },
                    topColor));

                AddTerrainWallFace(faces, worldX1, worldY1, worldY2, (h00 + h01) * 0.5f, cellX > 0 ? ResolveChunkNeighborHeight(runtimeGrid, cellX - stepX, cellY, stepX, stepY) : 0f, sideColor, verticalEdge: true);
                AddTerrainWallFace(faces, worldX1, worldY1, worldX2, (h00 + h10) * 0.5f, cellY > 0 ? ResolveChunkNeighborHeight(runtimeGrid, cellX, cellY - stepY, stepX, stepY) : 0f, sideColor, verticalEdge: false);
                if (nextCellX >= runtimeGrid.WidthCells)
                {
                    AddTerrainWallFace(faces, worldX2, worldY1, worldY2, (h10 + h11) * 0.5f, 0f, sideColor, verticalEdge: true);
                }

                if (nextCellY >= runtimeGrid.HeightCells)
                {
                    AddTerrainWallFace(faces, worldY2, worldX1, worldX2, (h01 + h11) * 0.5f, 0f, sideColor, verticalEdge: false, horizontalSwapped: true);
                }
            }
        }

        if (minTerrainHeight == float.MaxValue)
        {
            minTerrainHeight = 0f;
            maxTerrainHeight = 0f;
        }

        float baseTop = minTerrainHeight - 0.006f;
        float baseDepth = terrainBaseDepthM + Math.Max(0f, maxTerrainHeight - minTerrainHeight) * 0.18f;
        AddTerrainBaseSlab(
            faces,
            fieldLength,
            fieldWidth,
            baseTop,
            Math.Max(0.03f, baseDepth),
            SampleBitmapColor(Document.Width * 0.5, Document.Height * 0.5, Color.FromArgb(72, 82, 76)),
            sideColor);
    }

    private static void AddTerrainBaseSlab(
        ICollection<Preview3dFace> faces,
        float fieldLength,
        float fieldWidth,
        float topHeight,
        float depth,
        Color topColor,
        Color sideColor)
    {
        float bottomHeight = topHeight - Math.Max(0.02f, depth);
        Vector3 p1 = new(0f, topHeight, 0f);
        Vector3 p2 = new(fieldLength, topHeight, 0f);
        Vector3 p3 = new(fieldLength, topHeight, fieldWidth);
        Vector3 p4 = new(0f, topHeight, fieldWidth);
        Vector3 b1 = new(0f, bottomHeight, 0f);
        Vector3 b2 = new(fieldLength, bottomHeight, 0f);
        Vector3 b3 = new(fieldLength, bottomHeight, fieldWidth);
        Vector3 b4 = new(0f, bottomHeight, fieldWidth);
        faces.Add(new Preview3dFace(new[] { p1, p2, p3, p4 }, topColor));
        faces.Add(new Preview3dFace(new[] { b1, b4, b3, b2 }, Preview3dPrimitives.Multiply(sideColor, 0.82f)));
        faces.Add(new Preview3dFace(new[] { b1, b2, p2, p1 }, sideColor));
        faces.Add(new Preview3dFace(new[] { b2, b3, p3, p2 }, Preview3dPrimitives.Multiply(sideColor, 0.94f)));
        faces.Add(new Preview3dFace(new[] { b3, b4, p4, p3 }, Preview3dPrimitives.Multiply(sideColor, 0.90f)));
        faces.Add(new Preview3dFace(new[] { b4, b1, p1, p4 }, Preview3dPrimitives.Multiply(sideColor, 0.86f)));
    }

    private void AddTerrainWallFace(
        ICollection<Preview3dFace> faces,
        float fixedAxis,
        float axisStart,
        float axisEnd,
        float topHeight,
        float bottomHeight,
        Color color,
        bool verticalEdge,
        bool horizontalSwapped = false)
    {
        if (topHeight - bottomHeight <= 0.03f)
        {
            return;
        }

        Vector3 a;
        Vector3 b;
        Vector3 c;
        Vector3 d;
        if (verticalEdge)
        {
            a = ToPreviewPoint(fixedAxis, axisStart, bottomHeight);
            b = ToPreviewPoint(fixedAxis, axisEnd, bottomHeight);
            c = ToPreviewPoint(fixedAxis, axisEnd, topHeight);
            d = ToPreviewPoint(fixedAxis, axisStart, topHeight);
        }
        else if (horizontalSwapped)
        {
            a = ToPreviewPoint(axisEnd, fixedAxis, bottomHeight);
            b = ToPreviewPoint(axisStart, fixedAxis, bottomHeight);
            c = ToPreviewPoint(axisStart, fixedAxis, topHeight);
            d = ToPreviewPoint(axisEnd, fixedAxis, topHeight);
        }
        else
        {
            a = ToPreviewPoint(axisStart, fixedAxis, bottomHeight);
            b = ToPreviewPoint(axisEnd, fixedAxis, bottomHeight);
            c = ToPreviewPoint(axisEnd, fixedAxis, topHeight);
            d = ToPreviewPoint(axisStart, fixedAxis, topHeight);
        }

        faces.Add(new Preview3dFace(new[] { a, b, c, d }, color));
    }

    private float ResolveChunkNeighborHeight(RuntimeGridData runtimeGrid, int cellX, int cellY, int stepX, int stepY)
    {
        int sampleX = Math.Clamp(cellX + stepX / 2, 0, runtimeGrid.WidthCells - 1);
        int sampleY = Math.Clamp(cellY + stepY / 2, 0, runtimeGrid.HeightCells - 1);
        float worldX = sampleX * runtimeGrid.CellWidthWorld;
        float worldY = sampleY * runtimeGrid.CellHeightWorld;
        return runtimeGrid.SampleHeightWithFacets(worldX, worldY);
    }

    private Color ResolveTerrainFallbackColor(int cellX, int cellY, RuntimeGridData runtimeGrid)
    {
        int index = runtimeGrid.IndexOf(Math.Clamp(cellX, 0, runtimeGrid.WidthCells - 1), Math.Clamp(cellY, 0, runtimeGrid.HeightCells - 1));
        byte terrainCode = runtimeGrid.TerrainTypeMap[index];
        return terrainCode switch
        {
            1 => Color.FromArgb(120, 136, 118),
            2 => Color.FromArgb(128, 118, 104),
            3 => Color.FromArgb(108, 126, 138),
            _ => Color.FromArgb(112, 124, 112),
        };
    }

    private void AddFacilityPreview(
        ICollection<Preview3dFace> faces,
        FacilityRegionEditorModel facility,
        RuntimeGridData? runtimeGrid,
        AppearanceProfileCatalog appearanceCatalog)
    {
        FacilityRegion region = ToFacilityRegion(facility);
        if (string.Equals(region.Type, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(region.Type, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(region.Type, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            (double centerWorldX, double centerWorldY) = ResolveFacilityCenter(region);
            float groundHeight = runtimeGrid?.SampleHeightWithFacets(centerWorldX, centerWorldY) ?? 0f;
            RobotAppearanceProfile profile = appearanceCatalog.ResolveFacilityProfile(region);
            EditorPreviewGeometry.AppendFacilityStructurePreview(
                faces,
                region,
                profile,
                ToPreviewPoint(centerWorldX, centerWorldY, groundHeight));
            return;
        }

        Color color = ResolveFacilityColor(facility);
        float extrude = Math.Max(0.06f, (float)Math.Max(0.15, facility.HeightM));
        if (string.Equals(facility.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            Vector2 start = new((float)facility.X1, (float)facility.Y1);
            Vector2 end = new((float)facility.X2, (float)facility.Y2);
            Vector2 direction = end - start;
            if (direction.LengthSquared() <= 1e-4f)
            {
                return;
            }

            direction = Vector2.Normalize(direction);
            Vector2 normal = new(-direction.Y, direction.X);
            float half = (float)Math.Max(facility.Thickness * 0.5, 2.0);
            Point2D[] linePoints =
            {
                new(start.X + normal.X * half, start.Y + normal.Y * half),
                new(start.X - normal.X * half, start.Y - normal.Y * half),
                new(end.X - normal.X * half, end.Y - normal.Y * half),
                new(end.X + normal.X * half, end.Y + normal.Y * half),
            };
            AddExtrudedFootprint(faces, linePoints, runtimeGrid, extrude, color);
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

        AddExtrudedFootprint(faces, points, runtimeGrid, extrude, color);
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

    private void AddFacetPreview(ICollection<Preview3dFace> faces, TerrainFacetEditorModel facet, RuntimeGridData? runtimeGrid)
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
            float groundHeight = runtimeGrid?.SampleNearestHeight(point.X, point.Y) ?? 0f;
            Vector3 basePoint = ToPreviewPoint(point.X, point.Y, groundHeight);
            bottom.Add(basePoint);
            topVertices.Add(basePoint + Vector3.UnitY * Math.Max(0.01f, heightM));
        }

        Color color = TryParseColor(facet.TopColorHex, Color.FromArgb(136, 156, 112));
        Preview3dPrimitives.AddPrism(faces, bottom, topVertices, color);
    }

    private void AddExtrudedFootprint(
        ICollection<Preview3dFace> faces,
        IReadOnlyList<Point2D> points,
        RuntimeGridData? runtimeGrid,
        float extrude,
        Color color)
    {
        List<Vector3> bottom = points
            .Select(point => ToPreviewPoint(point.X, point.Y, runtimeGrid?.SampleHeightWithFacets(point.X, point.Y) ?? 0f))
            .ToList();
        List<Vector3> top = bottom
            .Select(point => point + Vector3.UnitY * extrude)
            .ToList();
        Preview3dPrimitives.AddPrism(faces, bottom, top, color);
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

    private Vector3 ToPreviewPoint(double worldX, double worldY, float heightM)
    {
        return new Vector3(MapXToPreviewMeters(worldX), heightM, MapYToPreviewMeters(worldY));
    }

    private Color SampleBitmapColor(double mapX, double mapY, Color fallback)
    {
        if (_mapBitmap is null || Document is null || Document.Width <= 0 || Document.Height <= 0)
        {
            return fallback;
        }

        int bitmapX = Math.Clamp((int)Math.Round(mapX / Math.Max(1.0, Document.Width) * (_mapBitmap.Width - 1)), 0, _mapBitmap.Width - 1);
        int bitmapY = Math.Clamp((int)Math.Round(mapY / Math.Max(1.0, Document.Height) * (_mapBitmap.Height - 1)), 0, _mapBitmap.Height - 1);
        try
        {
            Color sampled = _mapBitmap.GetPixel(bitmapX, bitmapY);
            return sampled.A == 0 ? fallback : sampled;
        }
        catch
        {
            return fallback;
        }
    }

    private static FacilityRegion ToFacilityRegion(FacilityRegionEditorModel facility)
    {
        return new FacilityRegion
        {
            Id = facility.Id,
            Type = facility.Type,
            Team = facility.Team,
            Shape = facility.Shape,
            X1 = facility.X1,
            Y1 = facility.Y1,
            X2 = facility.X2,
            Y2 = facility.Y2,
            Thickness = facility.Thickness,
            HeightM = facility.HeightM,
            Points = facility.ParsePoints(),
            AdditionalProperties = facility.AdditionalProperties,
        };
    }

    private static (double X, double Y) ResolveFacilityCenter(FacilityRegion region)
    {
        if (string.Equals(region.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && region.Points.Count >= 3)
        {
            double avgX = region.Points.Average(point => point.X);
            double avgY = region.Points.Average(point => point.Y);
            return (avgX, avgY);
        }

        return ((region.X1 + region.X2) * 0.5, (region.Y1 + region.Y2) * 0.5);
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

    private void ClearEditState()
    {
        _topEditing = false;
        _selecting = false;
        _topEditOperation = TopEditOperation.None;
        _facilitySnapshot = null;
        _facetSnapshot = null;
        _editVertexIndex = -1;
        MapSelection = null;
        MapSelectionStart = null;
        MapSelectionEnd = null;
    }

    private bool TryBeginTopEdit(PointF mapPoint)
    {
        if (Document is null)
        {
            return false;
        }

        if (TryBeginFacilityEdit(mapPoint, preferSelected: true) || TryBeginFacetEdit(mapPoint, preferSelected: true))
        {
            return true;
        }

        if (TryBeginFacilityEdit(mapPoint, preferSelected: false) || TryBeginFacetEdit(mapPoint, preferSelected: false))
        {
            return true;
        }

        return false;
    }

    private bool TryBeginFacilityEdit(PointF mapPoint, bool preferSelected)
    {
        if (Document is null)
        {
            return false;
        }

        IEnumerable<FacilityRegionEditorModel> facilities = preferSelected && !string.IsNullOrWhiteSpace(SelectedFacilityId)
            ? Document.Facilities.Where(facility => string.Equals(facility.Id, SelectedFacilityId, StringComparison.OrdinalIgnoreCase))
            : Document.Facilities.Reverse();
        foreach (FacilityRegionEditorModel facility in facilities)
        {
            int vertexIndex = TryHitFacilityVertex(facility, mapPoint);
            if (vertexIndex >= 0)
            {
                SelectedFacilityId = facility.Id;
                SelectedFacetId = null;
                _topEditing = true;
                _topEditOperation = TopEditOperation.EditFacilityVertex;
                _topEditStartMapPoint = mapPoint;
                _editVertexIndex = vertexIndex;
                _facilitySnapshot = CaptureFacilitySnapshot(facility);
                SelectionTargetChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (ContainsFacility(facility, mapPoint))
            {
                SelectedFacilityId = facility.Id;
                SelectedFacetId = null;
                _topEditing = true;
                _topEditOperation = TopEditOperation.MoveFacility;
                _topEditStartMapPoint = mapPoint;
                _editVertexIndex = -1;
                _facilitySnapshot = CaptureFacilitySnapshot(facility);
                SelectionTargetChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }

        return false;
    }

    private bool TryBeginFacetEdit(PointF mapPoint, bool preferSelected)
    {
        if (Document is null)
        {
            return false;
        }

        IEnumerable<TerrainFacetEditorModel> facets = preferSelected && !string.IsNullOrWhiteSpace(SelectedFacetId)
            ? Document.TerrainFacets.Where(facet => string.Equals(facet.Id, SelectedFacetId, StringComparison.OrdinalIgnoreCase))
            : Document.TerrainFacets.Reverse();
        foreach (TerrainFacetEditorModel facet in facets)
        {
            int vertexIndex = TryHitFacetVertex(facet, mapPoint);
            if (vertexIndex >= 0)
            {
                SelectedFacilityId = null;
                SelectedFacetId = facet.Id;
                _topEditing = true;
                _topEditOperation = TopEditOperation.EditFacetVertex;
                _topEditStartMapPoint = mapPoint;
                _editVertexIndex = vertexIndex;
                _facetSnapshot = new EditableFacetSnapshot(facet.ParsePoints().ToArray());
                SelectionTargetChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (ContainsFacet(facet, mapPoint))
            {
                SelectedFacilityId = null;
                SelectedFacetId = facet.Id;
                _topEditing = true;
                _topEditOperation = TopEditOperation.MoveFacet;
                _topEditStartMapPoint = mapPoint;
                _editVertexIndex = -1;
                _facetSnapshot = new EditableFacetSnapshot(facet.ParsePoints().ToArray());
                SelectionTargetChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }

        return false;
    }

    private void UpdateTopEdit(PointF mapPoint)
    {
        if (Document is null)
        {
            return;
        }

        float dx = mapPoint.X - _topEditStartMapPoint.X;
        float dy = mapPoint.Y - _topEditStartMapPoint.Y;
        if (_topEditOperation is TopEditOperation.MoveFacility or TopEditOperation.EditFacilityVertex)
        {
            FacilityRegionEditorModel? facility = Document.Facilities.FirstOrDefault(candidate => string.Equals(candidate.Id, SelectedFacilityId, StringComparison.OrdinalIgnoreCase));
            if (facility is null || _facilitySnapshot is not EditableFacilitySnapshot snapshot)
            {
                return;
            }

            if (_topEditOperation == TopEditOperation.MoveFacility)
            {
                facility.X1 = snapshot.X1 + dx;
                facility.Y1 = snapshot.Y1 + dy;
                facility.X2 = snapshot.X2 + dx;
                facility.Y2 = snapshot.Y2 + dy;
                if (snapshot.Points.Length > 0)
                {
                    facility.PointsText = FormatPoints(snapshot.Points.Select(point => new Point2D(point.X + dx, point.Y + dy)).ToArray());
                }
            }
            else
            {
                ApplyFacilityVertexEdit(facility, snapshot, _editVertexIndex, mapPoint);
            }

            NotifyDocumentEdited();
            return;
        }

        if (_topEditOperation is TopEditOperation.MoveFacet or TopEditOperation.EditFacetVertex)
        {
            TerrainFacetEditorModel? facet = Document.TerrainFacets.FirstOrDefault(candidate => string.Equals(candidate.Id, SelectedFacetId, StringComparison.OrdinalIgnoreCase));
            if (facet is null || _facetSnapshot is not EditableFacetSnapshot facetSnapshot)
            {
                return;
            }

            Point2D[] points = facetSnapshot.Points.ToArray();
            if (_topEditOperation == TopEditOperation.MoveFacet)
            {
                for (int index = 0; index < points.Length; index++)
                {
                    points[index] = new Point2D(points[index].X + dx, points[index].Y + dy);
                }
            }
            else if (_editVertexIndex >= 0 && _editVertexIndex < points.Length)
            {
                points[_editVertexIndex] = new Point2D(mapPoint.X, mapPoint.Y);
            }

            facet.PointsText = FormatPoints(points);
            NotifyDocumentEdited();
        }
    }

    private void NotifyDocumentEdited()
    {
        MarkSceneDirty();
        DocumentEdited?.Invoke(this, EventArgs.Empty);
    }

    private static EditableFacilitySnapshot CaptureFacilitySnapshot(FacilityRegionEditorModel facility)
        => new(
            facility.Shape,
            facility.X1,
            facility.Y1,
            facility.X2,
            facility.Y2,
            facility.Thickness,
            facility.ParsePoints().ToArray());

    private static void ApplyFacilityVertexEdit(FacilityRegionEditorModel facility, EditableFacilitySnapshot snapshot, int vertexIndex, PointF mapPoint)
    {
        if (string.Equals(snapshot.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && snapshot.Points.Length > 0)
        {
            Point2D[] points = snapshot.Points.ToArray();
            if (vertexIndex >= 0 && vertexIndex < points.Length)
            {
                points[vertexIndex] = new Point2D(mapPoint.X, mapPoint.Y);
                facility.PointsText = FormatPoints(points);
            }

            return;
        }

        if (string.Equals(snapshot.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            if (vertexIndex == 0)
            {
                facility.X1 = mapPoint.X;
                facility.Y1 = mapPoint.Y;
            }
            else
            {
                facility.X2 = mapPoint.X;
                facility.Y2 = mapPoint.Y;
            }

            return;
        }

        switch (vertexIndex)
        {
            case 0:
                facility.X1 = mapPoint.X;
                facility.Y1 = mapPoint.Y;
                break;
            case 1:
                facility.X2 = mapPoint.X;
                facility.Y1 = mapPoint.Y;
                break;
            case 2:
                facility.X2 = mapPoint.X;
                facility.Y2 = mapPoint.Y;
                break;
            case 3:
                facility.X1 = mapPoint.X;
                facility.Y2 = mapPoint.Y;
                break;
        }
    }

    private static string FormatPoints(IReadOnlyList<Point2D> points)
        => string.Join("; ", points.Select(point => $"{point.X:0.###},{point.Y:0.###}"));

    private static int TryHitFacilityVertex(FacilityRegionEditorModel facility, PointF point)
    {
        IReadOnlyList<Point2D> vertices = ResolveFacilityVertices(facility);
        for (int index = 0; index < vertices.Count; index++)
        {
            if (DistanceSquared(vertices[index], point) <= 64f)
            {
                return index;
            }
        }

        return -1;
    }

    private static int TryHitFacetVertex(TerrainFacetEditorModel facet, PointF point)
    {
        IReadOnlyList<Point2D> vertices = facet.ParsePoints();
        for (int index = 0; index < vertices.Count; index++)
        {
            if (DistanceSquared(vertices[index], point) <= 64f)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ContainsFacility(FacilityRegionEditorModel facility, PointF point)
    {
        if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
        {
            return IsPointInsidePolygon(facility.ParsePoints(), point);
        }

        if (string.Equals(facility.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            return DistancePointToSegment(point, new PointF((float)facility.X1, (float)facility.Y1), new PointF((float)facility.X2, (float)facility.Y2))
                <= Math.Max(6f, (float)facility.Thickness * 0.5f);
        }

        float left = (float)Math.Min(facility.X1, facility.X2);
        float right = (float)Math.Max(facility.X1, facility.X2);
        float top = (float)Math.Min(facility.Y1, facility.Y2);
        float bottom = (float)Math.Max(facility.Y1, facility.Y2);
        return point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
    }

    private static bool ContainsFacet(TerrainFacetEditorModel facet, PointF point)
        => IsPointInsidePolygon(facet.ParsePoints(), point);

    private static IReadOnlyList<Point2D> ResolveFacilityVertices(FacilityRegionEditorModel facility)
    {
        if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
        {
            return facility.ParsePoints();
        }

        if (string.Equals(facility.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                new Point2D(facility.X1, facility.Y1),
                new Point2D(facility.X2, facility.Y2),
            };
        }

        return new[]
        {
            new Point2D(facility.X1, facility.Y1),
            new Point2D(facility.X2, facility.Y1),
            new Point2D(facility.X2, facility.Y2),
            new Point2D(facility.X1, facility.Y2),
        };
    }

    private static float DistanceSquared(Point2D point, PointF candidate)
    {
        float dx = (float)point.X - candidate.X;
        float dy = (float)point.Y - candidate.Y;
        return dx * dx + dy * dy;
    }

    private static bool IsPointInsidePolygon(IReadOnlyList<Point2D> polygon, PointF point)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        bool inside = false;
        Point2D previous = polygon[^1];
        foreach (Point2D current in polygon)
        {
            bool intersects = ((current.Y > point.Y) != (previous.Y > point.Y))
                && (point.X < (previous.X - current.X) * (point.Y - current.Y) / Math.Max(previous.Y - current.Y, 1e-6) + current.X);
            if (intersects)
            {
                inside = !inside;
            }

            previous = current;
        }

        return inside;
    }

    private static float DistancePointToSegment(PointF point, PointF start, PointF end)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        if (Math.Abs(dx) <= 1e-4f && Math.Abs(dy) <= 1e-4f)
        {
            return MathF.Sqrt((point.X - start.X) * (point.X - start.X) + (point.Y - start.Y) * (point.Y - start.Y));
        }

        float t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0f, 1f);
        float closestX = start.X + dx * t;
        float closestY = start.Y + dy * t;
        float diffX = point.X - closestX;
        float diffY = point.Y - closestY;
        return MathF.Sqrt(diffX * diffX + diffY * diffY);
    }

    private bool TryGetTopMapRect(Point clientPoint, out Rectangle mapRect)
    {
        mapRect = Rectangle.Empty;
        if (Document is null || Document.Width <= 0 || Document.Height <= 0)
        {
            return false;
        }

        Rectangle bounds = ClientRectangle;
        Rectangle panel = new(12, 86, bounds.Width - 24, bounds.Height - 98);
        if (_mode == PreviewMode.Top)
        {
            mapRect = FitRect(panel, Document.Width, Document.Height, 10);
            return mapRect.Contains(clientPoint);
        }

        if (_mode == PreviewMode.Split)
        {
            Rectangle left = new(panel.X + 8, panel.Y + 8, (panel.Width - 24) / 2, panel.Height - 16);
            mapRect = FitRect(left, Document.Width, Document.Height, 10);
            return mapRect.Contains(clientPoint);
        }

        return false;
    }

    private void UpdateMapSelection(Point currentClient)
    {
        _selectionCurrentClient = currentClient;
        if (Document is null || !TryGetTopMapRect(_selectionStartClient, out Rectangle mapRect))
        {
            return;
        }

        PointF start = ToMapPoint(mapRect, _selectionStartClient);
        PointF end = ToMapPoint(mapRect, _selectionCurrentClient);
        float left = Math.Min(start.X, end.X);
        float right = Math.Max(start.X, end.X);
        float top = Math.Min(start.Y, end.Y);
        float bottom = Math.Max(start.Y, end.Y);
        MapSelectionStart = start;
        MapSelectionEnd = end;
        MapSelection = RectangleF.FromLTRB(left, top, right, bottom);
        MapSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private PointF ToMapPoint(Rectangle mapRect, Point clientPoint)
    {
        if (Document is null)
        {
            return PointF.Empty;
        }

        float x = (clientPoint.X - mapRect.X) * Document.Width / (float)Math.Max(1, mapRect.Width);
        float y = (clientPoint.Y - mapRect.Y) * Document.Height / (float)Math.Max(1, mapRect.Height);
        return new PointF(
            Math.Clamp(x, 0f, Math.Max(0f, Document.Width)),
            Math.Clamp(y, 0f, Math.Max(0f, Document.Height)));
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
