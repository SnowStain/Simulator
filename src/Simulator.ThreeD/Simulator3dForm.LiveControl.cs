using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private bool IsWindowActive()
    {
        return IsHandleCreated && GetForegroundWindow() == Handle;
    }

    private double GetMovementAxis(Keys positive, Keys negative)
    {
        return (IsAnyKeyHeld(positive) ? 1.0 : 0.0) - (IsAnyKeyHeld(negative) ? 1.0 : 0.0);
    }

    private double GetMovementAxisContinuous(Keys positiveKey, int positiveVirtualKey, Keys negativeKey, int negativeVirtualKey)
    {
        _ = positiveVirtualKey;
        _ = negativeVirtualKey;
        bool positiveDown = IsAnyKeyHeld(positiveKey);
        bool negativeDown = IsAnyKeyHeld(negativeKey);
        return (positiveDown ? 1.0 : 0.0) - (negativeDown ? 1.0 : 0.0);
    }

    private bool ConsumePressedKey(Keys key, ref bool wasDown)
    {
        bool isDown = IsAnyKeyHeld(key);
        bool pressed = isDown && !wasDown;
        wasDown = isDown;
        return pressed;
    }

    private bool ConsumePressedInput(Keys key, int virtualKey, ref bool wasDown, bool heldCountsAsPressed = false)
    {
        _ = virtualKey;
        bool isDown = IsAnyKeyHeld(key);
        bool pressed = heldCountsAsPressed ? isDown : isDown && !wasDown;
        wasDown = isDown;
        return pressed;
    }

    private void ToggleViewMode()
    {
        _firstPersonView = !_firstPersonView;
        _followSelection = !_firstPersonView;
        if (!_firstPersonView)
        {
            _cameraDistanceM = Math.Clamp(_cameraDistanceM, 6.5f, 18f);
        }

        UpdateMouseCaptureState();
    }

    private void DrawPlayerStatusPanelModern(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null)
        {
            return;
        }

        Rectangle panel = new(24, ClientSize.Height - 122, 420, 98);
        using GraphicsPath path = CreateRoundedRectangle(panel, 6);
        using var fill = new SolidBrush(Color.FromArgb(224, 13, 19, 26));
        using var border = new Pen(Color.FromArgb(170, 122, 146, 168), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var teamBrush = new SolidBrush(ResolveTeamColor(entity.Team));
        graphics.FillRectangle(teamBrush, panel.X, panel.Y, 5, panel.Height);

        string roleLabel = ResolveRoleLabel(entity);
        string controlLabel = entity.IsPlayerControlled ? "Manual" : "AI";
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        using var textBrush = new SolidBrush(Color.FromArgb(206, 216, 226));
        graphics.DrawString($"{entity.Id}  {roleLabel}  {controlLabel}", _smallHudFont, titleBrush, panel.X + 16, panel.Y + 10);

        float barX = panel.X + 16;
        float barY = panel.Y + 35;
        (float powerRatio, string powerLabel) = ResolvePowerGauge(entity);
        DrawMiniGauge(graphics, new RectangleF(barX, barY, 112, 10), entity.MaxHealth <= 0 ? 0f : (float)(entity.Health / entity.MaxHealth), Color.FromArgb(72, 214, 126), $"HP {(int)entity.Health}/{(int)entity.MaxHealth}");
        DrawMiniGauge(graphics, new RectangleF(barX + 126, barY, 96, 10), powerRatio, Color.FromArgb(75, 146, 232), powerLabel);
        DrawMiniGauge(graphics, new RectangleF(barX + 236, barY, 96, 10), entity.MaxHeat <= 0 ? 0f : (float)(entity.Heat / entity.MaxHeat), Color.FromArgb(228, 130, 58), $"H {(int)entity.Heat}");

        double speedMps = Math.Sqrt(
            entity.VelocityXWorldPerSec * entity.VelocityXWorldPerSec
            + entity.VelocityYWorldPerSec * entity.VelocityYWorldPerSec) * Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        string ammoText = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? $"42mm {entity.Ammo42Mm}"
            : $"17mm {entity.Ammo17Mm}";
        string motionText = $"Ammo {ammoText}   Speed {speedMps:0.0}m/s   P {entity.ChassisPowerDrawW:0}W   Pitch {entity.GimbalPitchDeg:0}deg";
        graphics.DrawString(motionText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 54);

        bool inFriendlySupply = _host.MapPreset.Facilities.Any(region =>
            string.Equals(region.Type, "supply", StringComparison.OrdinalIgnoreCase)
            && string.Equals(region.Team, entity.Team, StringComparison.OrdinalIgnoreCase)
            && region.Contains(entity.X, entity.Y));
        string supplyPrompt = inFriendlySupply ? "   B Resupply" : string.Empty;
        string statusText = $"Lock {(entity.AutoAimLocked ? "Armor" : "Free")}   RMB AutoAim   LMB Fire   Shift Spin   V View{supplyPrompt}";
        graphics.DrawString(statusText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 73);
    }

    private void DrawOrientationWidget(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || _appState != SimulatorAppState.InMatch)
        {
            return;
        }

        Rectangle panel = new(ClientSize.Width - 196, ClientSize.Height - 186, 164, 132);
        using GraphicsPath path = CreateRoundedRectangle(panel, 10);
        using var fill = new SolidBrush(Color.FromArgb(214, 10, 16, 24));
        using var border = new Pen(Color.FromArgb(150, 118, 136, 156), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var textBrush = new SolidBrush(Color.FromArgb(230, 236, 241, 245));
        using var subTextBrush = new SolidBrush(Color.FromArgb(196, 198, 207, 216));
        graphics.DrawString(_firstPersonView ? "First Person" : "Third Person", _smallHudFont, textBrush, panel.X + 12, panel.Y + 10);

        PointF center = new(panel.X + 46, panel.Y + 67);
        float radius = 26f;
        using var ringPen = new Pen(Color.FromArgb(118, 144, 162, 178), 1.1f);
        graphics.DrawEllipse(ringPen, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        graphics.DrawEllipse(ringPen, center.X - radius * 0.58f, center.Y - radius * 0.58f, radius * 1.16f, radius * 1.16f);

        DrawHeadingNeedle(graphics, center, radius * 0.92f, (float)(entity.AngleDeg * Math.PI / 180.0), Color.FromArgb(236, 238, 244));
        DrawHeadingNeedle(graphics, center, radius * 0.74f, (float)(entity.TurretYawDeg * Math.PI / 180.0), ResolveTeamColor(entity.Team));

        float pitchRatio = (float)Math.Clamp((entity.GimbalPitchDeg + 40.0) / 80.0, 0.0, 1.0);
        RectangleF pitchBar = new(panel.X + 92, panel.Y + 56, 52, 8);
        using var pitchBack = new SolidBrush(Color.FromArgb(110, 44, 52, 62));
        using var pitchFill = new SolidBrush(Color.FromArgb(216, 90, 170, 232));
        using var pitchPen = new Pen(Color.FromArgb(124, 182, 198, 214), 1f);
        graphics.FillRectangle(pitchBack, pitchBar);
        graphics.FillRectangle(pitchFill, pitchBar.X, pitchBar.Y, pitchBar.Width * pitchRatio, pitchBar.Height);
        graphics.DrawRectangle(pitchPen, pitchBar.X, pitchBar.Y, pitchBar.Width, pitchBar.Height);
        graphics.DrawString($"Chassis {NormalizeCompassDeg(entity.AngleDeg):000}deg", _tinyHudFont, subTextBrush, panel.X + 92, panel.Y + 20);
        graphics.DrawString($"Turret  {NormalizeCompassDeg(entity.TurretYawDeg):000}deg", _tinyHudFont, subTextBrush, panel.X + 92, panel.Y + 36);
        graphics.DrawString($"Pitch {entity.GimbalPitchDeg:+0;-0;0}deg", _tinyHudFont, subTextBrush, panel.X + 92, panel.Y + 68);
        graphics.DrawString($"Drive {entity.ChassisPowerDrawW:0}W", _tinyHudFont, subTextBrush, panel.X + 92, panel.Y + 84);
        graphics.DrawString("Body / Turret", _tinyHudFont, subTextBrush, panel.X + 14, panel.Bottom - 20);
    }

    private static void DrawHeadingNeedle(Graphics graphics, PointF center, float length, float yawRad, Color color)
    {
        PointF tip = new(
            center.X + MathF.Cos(yawRad) * length,
            center.Y + MathF.Sin(yawRad) * length);
        PointF left = new(
            center.X + MathF.Cos(yawRad + 2.55f) * (length * 0.22f),
            center.Y + MathF.Sin(yawRad + 2.55f) * (length * 0.22f));
        PointF right = new(
            center.X + MathF.Cos(yawRad - 2.55f) * (length * 0.22f),
            center.Y + MathF.Sin(yawRad - 2.55f) * (length * 0.22f));
        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, 2f);
        graphics.DrawLine(pen, center, tip);
        graphics.FillPolygon(brush, new[] { tip, left, right });
    }

    private static double NormalizeCompassDeg(double degrees)
    {
        double normalized = degrees % 360.0;
        return normalized < 0.0 ? normalized + 360.0 : normalized;
    }

    private void DrawTerrainTilesBackToFront(Graphics graphics)
    {
        if (_terrainFaces.Count == 0)
        {
            return;
        }

        CollectVisibleTerrainTiles(_terrainDrawBuffer);
        if (_terrainDrawBuffer.Count == 0)
        {
            return;
        }

        _projectedTerrainFaceBuffer.Clear();
        _projectedTerrainFaceBuffer.EnsureCapacity(_terrainDrawBuffer.Count);
        foreach (TerrainFacePatch face in _terrainDrawBuffer)
        {
            if (TryBuildProjectedFace(face.Vertices, face.FillColor, face.EdgeColor, out ProjectedFace projected))
            {
                _projectedTerrainFaceBuffer.Add(projected);
            }
        }

        _projectedTerrainFaceBuffer.Sort((left, right) => right.AverageDepth.CompareTo(left.AverageDepth));
        foreach (ProjectedFace face in _projectedTerrainFaceBuffer)
        {
            using var faceBrush = new SolidBrush(face.FillColor);
            using var facePen = new Pen(face.EdgeColor, 1f);
            graphics.FillPolygon(faceBrush, face.Points);
            graphics.DrawPolygon(facePen, face.Points);
        }
    }

    private void CollectVisibleTerrainTiles(List<TerrainFacePatch> buffer)
    {
        buffer.Clear();
        RebuildVisibleTerrainDetailCache(force: false);

        bool hasDetail = _terrainDetailFaces.Count > 0;
        foreach (TerrainFacePatch face in _terrainFaces)
        {
            if (hasDetail && IntersectsTerrainDetailRegion(face))
            {
                continue;
            }

            if (IsTerrainFacePotentiallyVisible(face))
            {
                buffer.Add(face);
            }
        }

        foreach (TerrainFacePatch face in _terrainDetailFaces)
        {
            if (IsTerrainFacePotentiallyVisible(face))
            {
                buffer.Add(face);
            }
        }
    }

    private void RebuildVisibleTerrainDetailCache(bool force)
    {
        if (_cachedRuntimeGrid is null || !_cachedRuntimeGrid.IsValid)
        {
            return;
        }

        if (!TryResolveTerrainFocusWorld(out float focusXWorld, out float focusYWorld))
        {
            return;
        }

        int centerCellX = Math.Clamp(
            (int)MathF.Floor(focusXWorld / Math.Max(_cachedRuntimeGrid.CellWidthWorld, 1e-6f)),
            0,
            _cachedRuntimeGrid.WidthCells - 1);
        int centerCellY = Math.Clamp(
            (int)MathF.Floor(focusYWorld / Math.Max(_cachedRuntimeGrid.CellHeightWorld, 1e-6f)),
            0,
            _cachedRuntimeGrid.HeightCells - 1);

        float radiusM = ResolveTerrainDetailRadiusM();
        float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        float radiusWorld = radiusM / metersPerWorldUnit;
        int radiusCellsX = Math.Clamp((int)MathF.Ceiling(radiusWorld / Math.Max(_cachedRuntimeGrid.CellWidthWorld, 1e-6f)), 4, _cachedRuntimeGrid.WidthCells);
        int radiusCellsY = Math.Clamp((int)MathF.Ceiling(radiusWorld / Math.Max(_cachedRuntimeGrid.CellHeightWorld, 1e-6f)), 4, _cachedRuntimeGrid.HeightCells);
        int rebuildThresholdX = Math.Max(3, radiusCellsX / 3);
        int rebuildThresholdY = Math.Max(3, radiusCellsY / 3);

        if (!force
            && _terrainDetailFaces.Count > 0
            && Math.Abs(centerCellX - _terrainDetailCenterCellX) <= rebuildThresholdX
            && Math.Abs(centerCellY - _terrainDetailCenterCellY) <= rebuildThresholdY)
        {
            return;
        }

        if (!force
            && _terrainDetailFaces.Count > 0
            && _lastTerrainDetailRebuildTicks > 0
            && (_frameClock.ElapsedTicks - _lastTerrainDetailRebuildTicks) / (double)Stopwatch.Frequency < 0.18)
        {
            return;
        }

        int startCellX = Math.Max(0, centerCellX - radiusCellsX);
        int endCellX = Math.Min(_cachedRuntimeGrid.WidthCells, centerCellX + radiusCellsX + 1);
        int startCellY = Math.Max(0, centerCellY - radiusCellsY);
        int endCellY = Math.Min(_cachedRuntimeGrid.HeightCells, centerCellY + radiusCellsY + 1);

        _terrainDetailFaces.Clear();
        _terrainDetailCenterCellX = centerCellX;
        _terrainDetailCenterCellY = centerCellY;
        _terrainDetailMinXWorld = startCellX * _cachedRuntimeGrid.CellWidthWorld;
        _terrainDetailMaxXWorld = endCellX * _cachedRuntimeGrid.CellWidthWorld;
        _terrainDetailMinYWorld = startCellY * _cachedRuntimeGrid.CellHeightWorld;
        _terrainDetailMaxYWorld = endCellY * _cachedRuntimeGrid.CellHeightWorld;
        int detailStep = ResolveTerrainDetailStep(_cachedRuntimeGrid);
        RebuildTerrainTileCacheMerged(detailStep, detailStep, _terrainDetailFaces, startCellX, endCellX, startCellY, endCellY);
        _lastTerrainDetailRebuildTicks = _frameClock.ElapsedTicks;
    }

    private static int ResolveTerrainDetailStep(RuntimeGridData runtimeGrid)
    {
        int longest = Math.Max(runtimeGrid.WidthCells, runtimeGrid.HeightCells);
        if (longest >= 360)
        {
            return 3;
        }

        if (longest >= 220)
        {
            return 2;
        }

        return 1;
    }

    private bool TryResolveTerrainFocusWorld(out float focusXWorld, out float focusYWorld)
    {
        float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is not null && (_firstPersonView || _followSelection))
        {
            focusXWorld = (float)selected.X;
            focusYWorld = (float)selected.Y;
            return true;
        }

        focusXWorld = _cameraTargetM.X / metersPerWorldUnit;
        focusYWorld = _cameraTargetM.Z / metersPerWorldUnit;
        return true;
    }

    private float ResolveTerrainDetailRadiusM()
    {
        if (_firstPersonView)
        {
            return 7.0f;
        }

        return Math.Clamp(_cameraDistanceM * 0.95f, 10.0f, 22.0f);
    }

    private bool IntersectsTerrainDetailRegion(TerrainFacePatch face)
    {
        return face.MaxXWorld >= _terrainDetailMinXWorld
            && face.MinXWorld <= _terrainDetailMaxXWorld
            && face.MaxYWorld >= _terrainDetailMinYWorld
            && face.MinYWorld <= _terrainDetailMaxYWorld;
    }

    private bool IsTerrainFacePotentiallyVisible(TerrainFacePatch face)
    {
        const float margin = 0.34f;
        bool hasProjectedPoint = false;
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        foreach (Vector3 vertex in face.Vertices)
        {
            Vector4 view = Vector4.Transform(new Vector4(vertex, 1f), _viewMatrix);
            float depth = -view.Z;
            if (depth <= 0.02f)
            {
                continue;
            }

            Vector4 clip = Vector4.Transform(view, _projectionMatrix);
            if (Math.Abs(clip.W) <= 1e-5f)
            {
                continue;
            }

            float inverseW = 1f / clip.W;
            float ndcX = clip.X * inverseW;
            float ndcY = clip.Y * inverseW;
            minX = Math.Min(minX, ndcX);
            maxX = Math.Max(maxX, ndcX);
            minY = Math.Min(minY, ndcY);
            maxY = Math.Max(maxY, ndcY);
            hasProjectedPoint = true;
        }

        if (!hasProjectedPoint)
        {
            return false;
        }

        return maxX >= -1f - margin
            && minX <= 1f + margin
            && maxY >= -1f - margin
            && minY <= 1f + margin;
    }
}
