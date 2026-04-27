using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Windows.Forms;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const float FineTerrainInMatchEditMoveSpeedMps = 0.60f;
    private const float FineTerrainInMatchEditMoveFastScale = 2.5f;
    private const float FineTerrainInMatchEditSelectionMaxDistancePx = 520f;

    private FineTerrainAnnotationDocument? _fineTerrainInMatchAnnotationDocument;
    private string? _fineTerrainInMatchAnnotationPath;
    private int? _fineTerrainInMatchSelectedCompositeId;
    private bool _fineTerrainInMatchEditDirty;
    private string _fineTerrainInMatchStatusText = "F9 开启局内组合体编辑";
    private Color _fineTerrainInMatchStatusColor = Color.FromArgb(255, 132, 214, 152);
    private long _fineTerrainInMatchStatusVisibleUntilTicks;

    private readonly record struct FineTerrainCompositeScreenCandidate(
        FineTerrainCompositeAnnotation Composite,
        PointF ScreenPoint,
        float Depth,
        float ScreenDistancePx,
        Vector3 ScenePoint);

    private enum FineTerrainEditorAxis
    {
        X,
        Y,
        Z,
    }

    private void SetFineTerrainInMatchStatus(
        string text,
        Color color,
        double durationSec = 5.0,
        bool appendEvent = false)
    {
        _fineTerrainInMatchStatusText = text ?? string.Empty;
        _fineTerrainInMatchStatusColor = color;
        long durationTicks = (long)(Math.Max(0.2, durationSec) * Stopwatch.Frequency);
        _fineTerrainInMatchStatusVisibleUntilTicks = _frameClock.ElapsedTicks + durationTicks;
        AppendGameplayLog(
            "fine_terrain_in_match_editor.log",
            $"{DateTime.Now:HH:mm:ss.fff} {text}");
        if (appendEvent)
        {
            AppendMatchEvent(_fineTerrainInMatchStatusText, color, (float)Math.Max(1.5, durationSec));
        }
    }

    private void ToggleFineTerrainInMatchEditor()
    {
        SetFineTerrainInMatchStatus("收到 F9，正在切换局内组合体编辑。", Color.FromArgb(118, 196, 255), 2.0);
        if (_previewOnly || _sharedHostSimulation || _host.IsMapComponentTestMode)
        {
            SetFineTerrainInMatchStatus("F9 局内编辑仅在正式对局窗口可用。", Color.FromArgb(236, 164, 92), 5.5, appendEvent: true);
            return;
        }

        if (!_fineTerrainInMatchEditMode)
        {
            if (!EnsureFineTerrainInMatchEditorLoaded(out string error))
            {
                SetFineTerrainInMatchStatus(error, Color.FromArgb(236, 110, 92), 6.0, appendEvent: true);
                return;
            }

            if (_tacticalMode)
            {
                ToggleTacticalMode();
            }

            _fineTerrainInMatchEditMode = true;
            _fineTerrainInMatchEditDirty = false;
            _firePressed = false;
            _autoAimPressed = false;
            _pendingSingleFireRequest = false;
            if (TrySelectFineTerrainCompositeAtAnchor(cycleDirection: 0))
            {
                FineTerrainCompositeAnnotation? selected = FindFineTerrainInMatchSelectedComposite();
                SetFineTerrainInMatchStatus(
                    selected is null
                        ? "F9 编辑已开启。"
                        : $"F9 编辑已开启，当前组合体：{selected.Name}",
                    Color.FromArgb(118, 196, 255),
                    5.0,
                    appendEvent: true);
            }
            else
            {
                SetFineTerrainInMatchStatus(
                    "F9 编辑已开启，当前视线下没有可选组合体，按 Tab 可重新选取。",
                    Color.FromArgb(236, 208, 92),
                    5.5,
                    appendEvent: true);
            }

            return;
        }

        SaveFineTerrainInMatchEditor(stayInEditMode: false);
    }

    private bool EnsureFineTerrainInMatchEditorLoaded(out string error)
    {
        error = string.Empty;
        string annotationPath = _host.MapPreset.AnnotationPath;
        if (string.IsNullOrWhiteSpace(annotationPath))
        {
            error = "当前地图没有 annotation_path，无法开启 F9 局内编辑。";
            return false;
        }

        annotationPath = Path.GetFullPath(annotationPath);
        if (!File.Exists(annotationPath))
        {
            error = $"局内编辑注释文件不存在：{Path.GetFileName(annotationPath)}";
            return false;
        }

        if (_fineTerrainInMatchAnnotationDocument is not null
            && string.Equals(_fineTerrainInMatchAnnotationPath, annotationPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        FineTerrainAnnotationDocument? document = FineTerrainAnnotationDocument.TryLoad(annotationPath);
        if (document is null)
        {
            error = $"无法读取局内编辑注释文件：{Path.GetFileName(annotationPath)}";
            return false;
        }

        _fineTerrainInMatchAnnotationDocument = document;
        _fineTerrainInMatchAnnotationPath = annotationPath;
        if (_fineTerrainInMatchSelectedCompositeId is not null
            && document.Composites.All(composite => composite.Id != _fineTerrainInMatchSelectedCompositeId.Value))
        {
            _fineTerrainInMatchSelectedCompositeId = null;
        }

        return true;
    }

    private void UpdateFineTerrainInMatchEditor(double dt)
    {
        if (!_fineTerrainInMatchEditMode || dt <= 1e-6)
        {
            return;
        }

        if (!EnsureFineTerrainInMatchEditorLoaded(out _))
        {
            return;
        }

        FineTerrainCompositeAnnotation? composite = FindFineTerrainInMatchSelectedComposite();
        if (composite is null)
        {
            return;
        }

        Vector3 axisX = ResolveFineTerrainCompositeMoveAxis(composite, FineTerrainEditorAxis.X);
        Vector3 axisY = ResolveFineTerrainCompositeMoveAxis(composite, FineTerrainEditorAxis.Y);
        Vector3 axisZ = ResolveFineTerrainCompositeMoveAxis(composite, FineTerrainEditorAxis.Z);
        Vector3 deltaModel = Vector3.Zero;
        if (IsAnyKeyHeld(Keys.I))
        {
            deltaModel += axisZ;
        }

        if (IsAnyKeyHeld(Keys.K))
        {
            deltaModel -= axisZ;
        }

        if (IsAnyKeyHeld(Keys.J))
        {
            deltaModel -= axisX;
        }

        if (IsAnyKeyHeld(Keys.L))
        {
            deltaModel += axisX;
        }

        if (IsAnyKeyHeld(Keys.OemPeriod))
        {
            deltaModel += axisY;
        }

        if (IsAnyKeyHeld(Keys.Oem1))
        {
            deltaModel -= axisY;
        }

        if (deltaModel.LengthSquared() <= 1e-8f)
        {
            return;
        }

        float speed = FineTerrainInMatchEditMoveSpeedMps;
        if (IsAnyKeyHeld(Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey))
        {
            speed *= FineTerrainInMatchEditMoveFastScale;
        }

        FineTerrainWorldScale worldScale = _fineTerrainInMatchAnnotationDocument!.WorldScale;
        float averageMetersPerModelUnit = MathF.Max(
            1e-6f,
            (worldScale.XMetersPerModelUnit + worldScale.YMetersPerModelUnit + worldScale.ZMetersPerModelUnit) / 3f);
        float deltaModelLength = speed * (float)dt / averageMetersPerModelUnit;
        deltaModel = SafeNormalize(deltaModel, Vector3.Zero) * deltaModelLength;
        MoveSelectedFineTerrainCompositeInMatch(composite, deltaModel);
    }

    private void MoveSelectedFineTerrainCompositeInMatch(
        FineTerrainCompositeAnnotation composite,
        Vector3 deltaModel)
    {
        FineTerrainWorldScale worldScale = _fineTerrainInMatchAnnotationDocument!.WorldScale;
        Vector3 nextModel = composite.PositionModel.ToVector3() + deltaModel;
        composite.PositionModel = FineTerrainVector3.From(nextModel);
        composite.PositionMeters = FineTerrainVector3.From(new Vector3(
            (nextModel.X - worldScale.ModelCenter.X) * worldScale.XMetersPerModelUnit,
            (nextModel.Y - worldScale.ModelCenter.Y) * worldScale.YMetersPerModelUnit,
            (nextModel.Z - worldScale.ModelCenter.Z) * worldScale.ZMetersPerModelUnit));
        _fineTerrainInMatchEditDirty = true;
        SetFineTerrainInMatchStatus(
            $"编辑中：{composite.Name}  PosModel=({nextModel.X:0.###}, {nextModel.Y:0.###}, {nextModel.Z:0.###})",
            Color.FromArgb(255, 226, 168, 92),
            1.8);
    }

    private Vector3 ResolveFineTerrainCompositeMoveAxis(
        FineTerrainCompositeAnnotation composite,
        FineTerrainEditorAxis axis)
    {
        if (!string.Equals(composite.CoordinateSystemMode, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return axis switch
            {
                FineTerrainEditorAxis.X => Vector3.UnitX,
                FineTerrainEditorAxis.Y => Vector3.UnitY,
                FineTerrainEditorAxis.Z => Vector3.UnitZ,
                _ => Vector3.Zero,
            };
        }

        Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(
            composite.CoordinateYprDegrees.X * MathF.PI / 180f,
            composite.CoordinateYprDegrees.Y * MathF.PI / 180f,
            composite.CoordinateYprDegrees.Z * MathF.PI / 180f);
        Vector3 xAxis = SafeNormalize(Vector3.TransformNormal(Vector3.UnitX, rotation), Vector3.UnitX);
        Vector3 yCandidate = SafeNormalize(Vector3.TransformNormal(Vector3.UnitY, rotation), Vector3.UnitY);
        Vector3 zAxis = SafeNormalize(Vector3.Cross(xAxis, yCandidate), Vector3.UnitZ);
        Vector3 yAxis = SafeNormalize(Vector3.Cross(zAxis, xAxis), Vector3.UnitY);
        return axis switch
        {
            FineTerrainEditorAxis.X => xAxis,
            FineTerrainEditorAxis.Y => yAxis,
            FineTerrainEditorAxis.Z => zAxis,
            _ => Vector3.Zero,
        };
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
        => value.LengthSquared() <= 1e-8f ? fallback : Vector3.Normalize(value);

    private bool TrySelectFineTerrainCompositeAtAnchor(int cycleDirection)
    {
        PointF anchor = new(ClientSize.Width * 0.5f, ClientSize.Height * 0.5f);
        return TrySelectFineTerrainCompositeAtScreenPoint(anchor, cycleDirection, strictDistanceLimit: true);
    }

    private void CycleFineTerrainInMatchEditorSelection(int direction)
    {
        if (!_fineTerrainInMatchEditMode)
        {
            return;
        }

        if (!TrySelectFineTerrainCompositeAtAnchor(direction == 0 ? 1 : direction))
        {
            SetFineTerrainInMatchStatus("当前视线下没有可循环的组合体。", Color.FromArgb(236, 208, 92), 4.5);
        }
    }

    private bool TrySelectFineTerrainCompositeAtScreenPoint(
        PointF screenPoint,
        int cycleDirection,
        bool strictDistanceLimit)
    {
        if (!EnsureFineTerrainInMatchEditorLoaded(out _))
        {
            return false;
        }

        List<FineTerrainCompositeScreenCandidate> candidates = BuildFineTerrainCompositeScreenCandidates(screenPoint, strictDistanceLimit);
        if (candidates.Count == 0)
        {
            return false;
        }

        FineTerrainCompositeAnnotation selected;
        if (cycleDirection == 0 || _fineTerrainInMatchSelectedCompositeId is null)
        {
            selected = candidates[0].Composite;
        }
        else
        {
            int currentIndex = candidates.FindIndex(candidate => candidate.Composite.Id == _fineTerrainInMatchSelectedCompositeId.Value);
            if (currentIndex < 0)
            {
                selected = candidates[0].Composite;
            }
            else
            {
                int nextIndex = currentIndex + Math.Sign(cycleDirection);
                while (nextIndex < 0)
                {
                    nextIndex += candidates.Count;
                }

                nextIndex %= candidates.Count;
                selected = candidates[nextIndex].Composite;
            }
        }

        _fineTerrainInMatchSelectedCompositeId = selected.Id;
        SetFineTerrainInMatchStatus($"当前组合体：{selected.Name}（ID {selected.Id}）", Color.FromArgb(118, 196, 255), 4.0);
        return true;
    }

    private List<FineTerrainCompositeScreenCandidate> BuildFineTerrainCompositeScreenCandidates(
        PointF anchor,
        bool strictDistanceLimit)
    {
        var candidates = new List<FineTerrainCompositeScreenCandidate>();
        FineTerrainAnnotationDocument? document = _fineTerrainInMatchAnnotationDocument;
        if (document is null)
        {
            return candidates;
        }

        UpdateCameraMatrices();
        foreach (FineTerrainCompositeAnnotation composite in document.Composites)
        {
            Vector3 pivotScene = ResolveFineTerrainCompositePivotScene(composite, document.WorldScale);
            if (!TryProject(pivotScene, out PointF screenPoint, out float depth))
            {
                continue;
            }

            float dx = screenPoint.X - anchor.X;
            float dy = screenPoint.Y - anchor.Y;
            float distancePx = MathF.Sqrt(dx * dx + dy * dy);
            if (strictDistanceLimit && distancePx > FineTerrainInMatchEditSelectionMaxDistancePx)
            {
                continue;
            }

            candidates.Add(new FineTerrainCompositeScreenCandidate(
                composite,
                screenPoint,
                depth,
                distancePx,
                pivotScene));
        }

        candidates.Sort(static (left, right) =>
        {
            int byDistance = left.ScreenDistancePx.CompareTo(right.ScreenDistancePx);
            return byDistance != 0 ? byDistance : left.Depth.CompareTo(right.Depth);
        });
        return candidates;
    }

    private FineTerrainCompositeAnnotation? FindFineTerrainInMatchSelectedComposite()
    {
        if (_fineTerrainInMatchAnnotationDocument is null || _fineTerrainInMatchSelectedCompositeId is null)
        {
            return null;
        }

        return _fineTerrainInMatchAnnotationDocument.Composites.FirstOrDefault(
            composite => composite.Id == _fineTerrainInMatchSelectedCompositeId.Value);
    }

    private Vector3 ResolveFineTerrainCompositePivotScene(
        FineTerrainCompositeAnnotation composite,
        FineTerrainWorldScale worldScale)
    {
        Matrix4x4 baseTransform = ResolveFineTerrainCompositeBaseTransform(
            composite.PivotModel.ToVector3(),
            composite.PositionModel.ToVector3(),
            composite.YprDegrees.ToVector3());
        return ModelToScenePoint(Vector3.Transform(composite.PivotModel.ToVector3(), baseTransform), worldScale);
    }

    private bool TryResolveFineTerrainCompositePoseOverride(
        int compositeId,
        out Vector3 pivotModel,
        out Vector3 positionModel,
        out Vector3 rotationYprDegrees)
    {
        pivotModel = Vector3.Zero;
        positionModel = Vector3.Zero;
        rotationYprDegrees = Vector3.Zero;
        if (!_fineTerrainInMatchEditMode || _fineTerrainInMatchAnnotationDocument is null)
        {
            return false;
        }

        FineTerrainCompositeAnnotation? composite = _fineTerrainInMatchAnnotationDocument.Composites.FirstOrDefault(
            candidate => candidate.Id == compositeId);
        if (composite is null)
        {
            return false;
        }

        pivotModel = composite.PivotModel.ToVector3();
        positionModel = composite.PositionModel.ToVector3();
        rotationYprDegrees = composite.YprDegrees.ToVector3();
        return true;
    }

    private bool IsFineTerrainCompositePoseOverridden(int compositeId)
        => _fineTerrainInMatchEditMode
            && _fineTerrainInMatchAnnotationDocument is not null
            && _fineTerrainInMatchAnnotationDocument.Composites.Any(candidate => candidate.Id == compositeId);

    private void SaveFineTerrainInMatchEditor(bool stayInEditMode)
    {
        if (!EnsureFineTerrainInMatchEditorLoaded(out string error))
        {
            SetFineTerrainInMatchStatus(error, Color.FromArgb(236, 110, 92), 6.0, appendEvent: true);
            _fineTerrainInMatchEditMode = stayInEditMode && _fineTerrainInMatchEditMode;
            return;
        }

        try
        {
            _fineTerrainInMatchAnnotationDocument!.Save();
            _fineTerrainInMatchEditDirty = false;
            InvalidateFineTerrainVisualScenes();
            string fileName = Path.GetFileName(_fineTerrainInMatchAnnotationDocument.SourcePath);
            SetFineTerrainInMatchStatus(
                stayInEditMode
                    ? $"已保存到 {fileName}，继续编辑中。"
                    : $"已保存到 {fileName}，已退出 F9 编辑。",
                Color.FromArgb(120, 214, 154),
                5.5,
                appendEvent: true);
        }
        catch (Exception exception)
        {
            SetFineTerrainInMatchStatus($"保存失败：{exception.Message}", Color.FromArgb(236, 110, 92), 6.0, appendEvent: true);
            return;
        }

        _fineTerrainInMatchEditMode = stayInEditMode;
        if (!_fineTerrainInMatchEditMode)
        {
            _firePressed = false;
            _autoAimPressed = false;
        }
    }

    private void InvalidateFineTerrainVisualScenes()
    {
        _fineTerrainEnergyScene = null;
        _fineTerrainEnergySceneKey = null;
        _fineTerrainEnergySceneLoadTask = null;
        _fineTerrainEnergySceneLoadingKey = null;
        _fineTerrainOutpostScene = null;
        _fineTerrainOutpostSceneKey = null;
        _fineTerrainOutpostSceneLoadTask = null;
        _fineTerrainOutpostSceneLoadingKey = null;
        ResetFineTerrainEnergyBodyMeshCache(null);
        ResetFineTerrainOutpostBodyMeshCache(null);
    }

    private void DrawFineTerrainInMatchEditorOverlay(Graphics graphics)
    {
        bool statusVisible = _frameClock.ElapsedTicks <= _fineTerrainInMatchStatusVisibleUntilTicks
            && !string.IsNullOrWhiteSpace(_fineTerrainInMatchStatusText);
        if (!_fineTerrainInMatchEditMode && !statusVisible)
        {
            return;
        }

        int panelWidth = Math.Min(470, Math.Max(340, ClientSize.Width - 48));
        Rectangle panel = new(16, 72, panelWidth, 112);
        using GraphicsPath path = CreateRoundedRectangle(panel, 8);
        using var fill = new SolidBrush(Color.FromArgb(188, 10, 14, 20));
        using var border = new Pen(Color.FromArgb(196, 118, 214, 255), 1.1f);
        using var titleBrush = new SolidBrush(Color.FromArgb(244, 248, 255));
        using var textBrush = new SolidBrush(Color.FromArgb(222, 210, 224, 236));
        using var accentBrush = new SolidBrush(_fineTerrainInMatchEditDirty
            ? Color.FromArgb(255, 226, 168, 92)
            : _fineTerrainInMatchStatusColor);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString("F9 局内组合体编辑", _smallHudFont, titleBrush, panel.X + 12, panel.Y + 9);
        graphics.DrawString(
            "Tab 重新选取  Enter 选取准星附近  IJKL 平移  ; 下移  . 上移  Ctrl+S 保存  F9 保存并退出",
            _tinyHudFont,
            textBrush,
            panel.X + 12,
            panel.Y + 30);

        FineTerrainCompositeAnnotation? selected = FindFineTerrainInMatchSelectedComposite();
        string selectedText = selected is null
            ? "当前未选中组合体"
            : $"当前：{selected.Name}  ID {selected.Id}";
        graphics.DrawString(selectedText, _tinyHudFont, titleBrush, panel.X + 12, panel.Y + 52);
        graphics.DrawString(_fineTerrainInMatchStatusText, _tinyHudFont, accentBrush, panel.X + 12, panel.Y + 72);
        if (selected is not null)
        {
            Vector3 pos = selected.PositionModel.ToVector3();
            graphics.DrawString(
                $"PosModel ({pos.X:0.###}, {pos.Y:0.###}, {pos.Z:0.###})",
                _tinyHudFont,
                textBrush,
                panel.X + 12,
                panel.Y + 90);
        }

        if (!_fineTerrainInMatchEditMode || selected is null || _fineTerrainInMatchAnnotationDocument is null)
        {
            return;
        }

        Vector3 pivotScene = ResolveFineTerrainCompositePivotScene(selected, _fineTerrainInMatchAnnotationDocument.WorldScale);
        if (!TryProject(pivotScene, out PointF screenPoint, out _))
        {
            return;
        }

        using var shadowPen = new Pen(Color.FromArgb(170, 0, 0, 0), 3.2f);
        using var markerPen = new Pen(Color.FromArgb(244, 118, 214, 255), 1.5f);
        graphics.DrawEllipse(shadowPen, screenPoint.X - 10f, screenPoint.Y - 10f, 20f, 20f);
        graphics.DrawLine(shadowPen, screenPoint.X - 15f, screenPoint.Y, screenPoint.X + 15f, screenPoint.Y);
        graphics.DrawLine(shadowPen, screenPoint.X, screenPoint.Y - 15f, screenPoint.X, screenPoint.Y + 15f);
        graphics.DrawEllipse(markerPen, screenPoint.X - 10f, screenPoint.Y - 10f, 20f, 20f);
        graphics.DrawLine(markerPen, screenPoint.X - 15f, screenPoint.Y, screenPoint.X + 15f, screenPoint.Y);
        graphics.DrawLine(markerPen, screenPoint.X, screenPoint.Y - 15f, screenPoint.X, screenPoint.Y + 15f);
    }
}
