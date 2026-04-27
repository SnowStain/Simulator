using System.ComponentModel;
using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using Simulator.Core;
using Simulator.Editors;

namespace Simulator.ThreeD;

internal sealed class FineTerrainCompositeEditingSession
{
    private readonly ProjectLayout _layout;
    private readonly MapPresetEditorSettings _document;
    private Dictionary<int, FineTerrainCompositeAnnotation> _originalCompositeById = new();

    public FineTerrainCompositeEditingSession(ProjectLayout layout, MapPresetEditorSettings document)
    {
        _layout = layout;
        _document = document;
    }

    public FineTerrainAnnotationDocument? AnnotationDocument { get; private set; }

    public string ResolveAnnotationPath()
    {
        if (_document.RawMap is not JsonObject rawMap)
        {
            return string.Empty;
        }

        string? relativePath = rawMap["annotation_path"]?.ToString();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        string presetDirectory = Path.GetDirectoryName(_document.SourcePath) ?? _layout.RootPath;
        return Path.GetFullPath(Path.Combine(presetDirectory, relativePath));
    }

    public string ResolveAnnotationDirectory()
    {
        string annotationPath = ResolveAnnotationPath();
        return Path.GetDirectoryName(annotationPath)
            ?? _layout.ResolvePath("maps", "rmuc26map");
    }

    public bool Reload()
    {
        string annotationPath = ResolveAnnotationPath();
        AnnotationDocument = FineTerrainAnnotationDocument.TryLoad(annotationPath);
        _originalCompositeById = AnnotationDocument?.Composites
            .ToDictionary(composite => composite.Id, CloneComposite)
            ?? new Dictionary<int, FineTerrainCompositeAnnotation>();
        return AnnotationDocument is not null;
    }

    public void Save()
    {
        AnnotationDocument?.Save();
    }

    public IReadOnlyList<FineTerrainCompositeAnchorReference> BuildAnchors(FineTerrainCompositeAnnotation? selectedComposite)
    {
        if (_originalCompositeById.Count == 0)
        {
            return Array.Empty<FineTerrainCompositeAnchorReference>();
        }

        var anchors = new List<FineTerrainCompositeAnchorReference>(_originalCompositeById.Count);
        if (selectedComposite is not null
            && _originalCompositeById.TryGetValue(selectedComposite.Id, out FineTerrainCompositeAnnotation? original))
        {
            anchors.Add(new FineTerrainCompositeAnchorReference(
                $"self:{selectedComposite.Id}",
                $"原始锚点: {selectedComposite.Name}",
                original,
                true));
        }

        foreach (FineTerrainCompositeAnnotation composite in _originalCompositeById.Values.OrderBy(item => item.Id))
        {
            if (selectedComposite is not null && composite.Id == selectedComposite.Id)
            {
                continue;
            }

            anchors.Add(new FineTerrainCompositeAnchorReference(
                composite.Id.ToString(),
                $"{composite.Id}: {composite.Name}",
                composite,
                false));
        }

        return anchors;
    }

    public bool TryResetCompositeToOriginal(FineTerrainCompositeAnnotation composite, out string message)
    {
        if (!_originalCompositeById.TryGetValue(composite.Id, out FineTerrainCompositeAnnotation? original))
        {
            message = $"未找到 {composite.Name} 的原始锚点。";
            return false;
        }

        ApplyCompositePose(composite, original);
        message = $"{composite.Name} 已恢复到原始锚点。";
        return true;
    }

    public bool TrySnapCompositeToAnchor(
        FineTerrainCompositeAnnotation composite,
        FineTerrainCompositeAnchorReference anchor,
        out string message)
    {
        ApplyCompositePose(composite, anchor.SourceComposite);
        message = anchor.IsSelfAnchor
            ? $"{composite.Name} 已恢复到自身原始锚点。"
            : $"{composite.Name} 已吸附到锚点 {anchor.DisplayName}。";
        return true;
    }

    public IReadOnlyList<FineTerrainPreviewMarker> BuildPreviewMarkers()
    {
        if (AnnotationDocument is null || AnnotationDocument.Composites.Count == 0)
        {
            return Array.Empty<FineTerrainPreviewMarker>();
        }

        var markers = new List<FineTerrainPreviewMarker>(AnnotationDocument.Composites.Count);
        foreach (FineTerrainCompositeAnnotation composite in AnnotationDocument.Composites.OrderBy(item => item.Id))
        {
            if (!TryModelToMapPoint(composite.PivotModel.ToVector3(), out PointF mapPoint))
            {
                continue;
            }

            markers.Add(new FineTerrainPreviewMarker(
                composite.Id.ToString(),
                composite.Id,
                composite.Name,
                mapPoint,
                ResolveCompositeMarkerColor(composite.Name)));
        }

        return markers;
    }

    private bool TryModelToMapPoint(Vector3 modelPoint, out PointF mapPoint)
    {
        mapPoint = PointF.Empty;
        if (AnnotationDocument is null
            || _document.Width <= 0
            || _document.Height <= 0
            || _document.FieldLengthM <= 1e-6
            || _document.FieldWidthM <= 1e-6)
        {
            return false;
        }

        float xMetersPerMapUnit = (float)(_document.FieldLengthM / Math.Max(1, _document.Width));
        float zMetersPerMapUnit = (float)(_document.FieldWidthM / Math.Max(1, _document.Height));
        if (xMetersPerMapUnit <= 1e-6f || zMetersPerMapUnit <= 1e-6f)
        {
            return false;
        }

        FineTerrainWorldScale worldScale = AnnotationDocument.WorldScale;
        Vector3 centeredModel = modelPoint - worldScale.ModelCenter;
        float mapX = _document.Width * 0.5f
            - centeredModel.X * worldScale.XMetersPerModelUnit / xMetersPerMapUnit;
        float mapY = _document.Height * 0.5f
            - centeredModel.Z * worldScale.ZMetersPerModelUnit / zMetersPerMapUnit;
        mapPoint = new PointF(mapX, mapY);
        return true;
    }

    private static void ApplyCompositePose(FineTerrainCompositeAnnotation target, FineTerrainCompositeAnnotation source)
    {
        target.PositionModel = CloneVector(source.PositionModel);
        target.PivotModel = CloneVector(source.PivotModel);
        target.YprDegrees = CloneVector(source.YprDegrees);
        target.CoordinateYprDegrees = CloneVector(source.CoordinateYprDegrees);
    }

    private static FineTerrainCompositeAnnotation CloneComposite(FineTerrainCompositeAnnotation source)
    {
        return new FineTerrainCompositeAnnotation
        {
            Id = source.Id,
            Name = source.Name,
            Role = source.Role,
            ComponentIds = source.ComponentIds.ToArray(),
            InteractionUnits = source.InteractionUnits
                .Select(unit => new FineTerrainInteractionUnitAnnotation
                {
                    Id = unit.Id,
                    Name = unit.Name,
                    ComponentIds = unit.ComponentIds.ToArray(),
                })
                .ToArray(),
            PositionMeters = CloneVector(source.PositionMeters),
            PositionModel = CloneVector(source.PositionModel),
            PivotModel = CloneVector(source.PivotModel),
            YprDegrees = CloneVector(source.YprDegrees),
            CoordinateYprDegrees = CloneVector(source.CoordinateYprDegrees),
        };
    }

    private static FineTerrainVector3 CloneVector(FineTerrainVector3 source)
    {
        return new FineTerrainVector3
        {
            X = source.X,
            Y = source.Y,
            Z = source.Z,
        };
    }

    private static Color ResolveCompositeMarkerColor(string name)
    {
        if (name.Contains("红方", StringComparison.Ordinal))
        {
            return Color.FromArgb(232, 86, 74);
        }

        if (name.Contains("蓝方", StringComparison.Ordinal))
        {
            return Color.FromArgb(78, 150, 236);
        }

        if (name.Contains("能量机关", StringComparison.Ordinal))
        {
            return Color.FromArgb(126, 214, 144);
        }

        return Color.FromArgb(246, 208, 96);
    }
}

internal sealed class FineTerrainCompositeAnchorReference
{
    public FineTerrainCompositeAnchorReference(
        string id,
        string displayName,
        FineTerrainCompositeAnnotation sourceComposite,
        bool isSelfAnchor)
    {
        Id = id;
        DisplayName = displayName;
        SourceComposite = sourceComposite;
        IsSelfAnchor = isSelfAnchor;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public FineTerrainCompositeAnnotation SourceComposite { get; }

    public bool IsSelfAnchor { get; }
}

internal readonly record struct FineTerrainPreviewMarker(
    string Id,
    int CompositeId,
    string Label,
    PointF MapPoint,
    Color Color);

internal sealed class FineTerrainPreviewMarkerEventArgs : EventArgs
{
    public FineTerrainPreviewMarkerEventArgs(FineTerrainPreviewMarker marker)
    {
        Marker = marker;
    }

    public FineTerrainPreviewMarker Marker { get; }
}

internal sealed class FineTerrainCompositeAnchorPropertyView
{
    private readonly FineTerrainCompositeAnchorReference _anchor;

    public FineTerrainCompositeAnchorPropertyView(FineTerrainCompositeAnchorReference anchor)
    {
        _anchor = anchor;
    }

    [ReadOnly(true)]
    [DisplayName("Anchor")]
    public string DisplayName => _anchor.DisplayName;

    [ReadOnly(true)]
    [DisplayName("Composite ID")]
    public int CompositeId => _anchor.SourceComposite.Id;

    [ReadOnly(true)]
    [DisplayName("Self Anchor")]
    public bool IsSelfAnchor => _anchor.IsSelfAnchor;

    [Category("Position Model")]
    [ReadOnly(true)]
    public float PositionModelX => _anchor.SourceComposite.PositionModel.X;

    [Category("Position Model")]
    [ReadOnly(true)]
    public float PositionModelY => _anchor.SourceComposite.PositionModel.Y;

    [Category("Position Model")]
    [ReadOnly(true)]
    public float PositionModelZ => _anchor.SourceComposite.PositionModel.Z;

    [Category("Pivot Model")]
    [ReadOnly(true)]
    public float PivotModelX => _anchor.SourceComposite.PivotModel.X;

    [Category("Pivot Model")]
    [ReadOnly(true)]
    public float PivotModelY => _anchor.SourceComposite.PivotModel.Y;

    [Category("Pivot Model")]
    [ReadOnly(true)]
    public float PivotModelZ => _anchor.SourceComposite.PivotModel.Z;

    [Category("Rotation YPR")]
    [ReadOnly(true)]
    public float RotationYawDeg => _anchor.SourceComposite.YprDegrees.X;

    [Category("Rotation YPR")]
    [ReadOnly(true)]
    public float RotationPitchDeg => _anchor.SourceComposite.YprDegrees.Y;

    [Category("Rotation YPR")]
    [ReadOnly(true)]
    public float RotationRollDeg => _anchor.SourceComposite.YprDegrees.Z;
}
