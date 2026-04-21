using System.Drawing.Drawing2D;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private Rectangle _fastRendererMapRect = Rectangle.Empty;

    private bool UseFastFlatRenderer
        => string.Equals(_host.ActiveRendererMode, "native_cpp", StringComparison.OrdinalIgnoreCase);

    private bool UseGpuRenderer
        => string.Equals(_host.ActiveRendererMode, "gpu", StringComparison.OrdinalIgnoreCase);

    private void DrawFastFlatMatch(Graphics graphics)
    {
        Rectangle sceneBounds = new(12, ToolbarHeight + HudHeight + 12, Math.Max(120, ClientSize.Width - 24), Math.Max(120, ClientSize.Height - ToolbarHeight - HudHeight - 24));
        _fastRendererMapRect = FitMapRect(sceneBounds, _host.MapPreset.Width, _host.MapPreset.Height);

        DrawFastMapBase(graphics, _fastRendererMapRect);
        DrawFastFacilities(graphics, _fastRendererMapRect);
        EnsureFastDynamicLayerBitmaps();
        DrawFastProjectileLayer();
        DrawFastEntityLayer();
        if (_fastProjectileLayerBitmap is not null)
        {
            graphics.DrawImageUnscaled(_fastProjectileLayerBitmap, 0, 0);
        }

        if (_fastEntityLayerBitmap is not null)
        {
            graphics.DrawImageUnscaled(_fastEntityLayerBitmap, 0, 0);
        }

        DrawFastCombatMarkers(graphics, _fastRendererMapRect);

        using var border = new Pen(Color.FromArgb(110, 136, 150, 168), 1.2f);
        graphics.DrawRectangle(border, _fastRendererMapRect);

        using var badgeFill = new SolidBrush(Color.FromArgb(210, 18, 24, 32));
        using var badgeText = new SolidBrush(Color.FromArgb(236, 244, 248));
        Rectangle badge = new(_fastRendererMapRect.X + 10, _fastRendererMapRect.Y + 10, 178, 26);
        graphics.FillRectangle(badgeFill, badge);
        string badgeLabel = _tacticalMode ? "Tactical Fast Renderer" : "Fast Renderer / native_cpp";
        graphics.DrawString(badgeLabel, _tinyHudFont, badgeText, badge.X + 8, badge.Y + 6);
    }

    private void DrawFastMapBase(Graphics graphics, Rectangle mapRect)
    {
        EnsureTerrainColorBitmapLoaded();
        if (_terrainColorBitmap is not null)
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(_terrainColorBitmap, mapRect);
            graphics.InterpolationMode = InterpolationMode.Default;
            graphics.PixelOffsetMode = PixelOffsetMode.Default;
            return;
        }

        using var fill = new SolidBrush(Color.FromArgb(42, 52, 60));
        graphics.FillRectangle(fill, mapRect);
    }

    private void DrawFastFacilities(Graphics graphics, Rectangle mapRect)
    {
        using var neutralPen = new Pen(Color.FromArgb(180, 216, 222, 228), 1.2f);
        using var selectedPen = new Pen(Color.FromArgb(232, 255, 210, 74), 1.8f);
        foreach (FacilityRegion region in _host.MapPreset.Facilities)
        {
            Pen pen = region.Type switch
            {
                "base" => new Pen(Color.FromArgb(180, ResolveTeamColor(region.Team)), 1.6f),
                "outpost" => new Pen(Color.FromArgb(180, ResolveTeamColor(region.Team)), 1.6f),
                "supply" or "buff_supply" => new Pen(Color.FromArgb(180, 88, 204, 142), 1.4f),
                "wall" => new Pen(Color.FromArgb(188, 216, 214, 210), 1.5f),
                _ => neutralPen,
            };

            try
            {
                DrawFacilityOverlay(graphics, mapRect, region, pen);
            }
            finally
            {
                if (!ReferenceEquals(pen, neutralPen) && !ReferenceEquals(pen, selectedPen))
                {
                    pen.Dispose();
                }
            }
        }
    }

    private void DrawFastEntityLayer()
    {
        if (_fastEntityLayerGraphics is null)
        {
            return;
        }

        Graphics graphics = _fastEntityLayerGraphics;
        graphics.Clear(Color.Transparent);
        using var tacticalTextBrush = new SolidBrush(Color.FromArgb(232, 238, 244, 246));
        using var tacticalShadowBrush = new SolidBrush(Color.FromArgb(210, 6, 10, 14));
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (!entity.IsAlive
                || (!string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!TryProjectFlatWorld(entity.X, entity.Y, out PointF center))
            {
                continue;
            }

            Color teamColor = ResolveTeamColor(entity.Team);
            float radius = entity.EntityType switch
            {
                "base" => 14f,
                "outpost" => 11f,
                "sentry" => 9f,
                _ => 7f,
            };

            using var fill = new SolidBrush(Color.FromArgb(220, teamColor));
            using var edge = new Pen(Color.FromArgb(236, BlendColor(teamColor, Color.Black, 0.28f)), 1.2f);
            if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase))
            {
                graphics.FillRectangle(fill, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
                graphics.DrawRectangle(edge, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
            }
            else if (string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
            {
                PointF[] diamond =
                {
                    new(center.X, center.Y - radius),
                    new(center.X + radius, center.Y),
                    new(center.X, center.Y + radius),
                    new(center.X - radius, center.Y),
                };
                graphics.FillPolygon(fill, diamond);
                graphics.DrawPolygon(edge, diamond);
            }
            else
            {
                graphics.FillEllipse(fill, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
                graphics.DrawEllipse(edge, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
                float headingRad = (float)(entity.AngleDeg * Math.PI / 180.0);
                PointF nose = new(center.X + MathF.Cos(headingRad) * (radius + 7f), center.Y + MathF.Sin(headingRad) * (radius + 7f));
                graphics.DrawLine(edge, center, nose);
            }

            if (string.Equals(entity.Id, _host.SelectedEntity?.Id, StringComparison.OrdinalIgnoreCase))
            {
                using var selectPen = new Pen(Color.FromArgb(255, 250, 246, 210), 2f);
                graphics.DrawEllipse(selectPen, center.X - radius - 3f, center.Y - radius - 3f, radius * 2f + 6f, radius * 2f + 6f);
            }

            DrawFastHealthBar(graphics, entity, center, radius);
            if (_tacticalMode
                && (string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
                && string.Equals(entity.Team, _host.SelectedTeam, StringComparison.OrdinalIgnoreCase))
            {
                DrawFastTacticalUnitMetrics(graphics, entity, center, radius, tacticalTextBrush, tacticalShadowBrush);
            }
        }
    }

    private void DrawFastTacticalUnitMetrics(
        Graphics graphics,
        SimulationEntity entity,
        PointF center,
        float radius,
        Brush textBrush,
        Brush shadowBrush)
    {
        int ammo = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? entity.Ammo42Mm
            : entity.Ammo17Mm;
        string role = ResolveRoleLabel(entity);
        string stats = $"{role} HP {entity.Health:0}/{entity.MaxHealth:0}  A {ammo}  H {entity.Heat:0}/{Math.Max(1.0, entity.MaxHeat):0}  P {entity.ChassisPowerDrawW:0}/{Math.Max(1.0, entity.EffectiveDrivePowerLimitW):0}W";
        float x = center.X + radius + 6f;
        float y = center.Y - 12f;
        graphics.FillRectangle(shadowBrush, x - 3f, y - 2f, Math.Min(330f, stats.Length * 6.2f), 28f);
        graphics.DrawString(entity.Id, _tinyHudFont, textBrush, x, y);
        graphics.DrawString(stats, _tinyHudFont, textBrush, x, y + 12f);
    }

    private void DrawFastHealthBar(Graphics graphics, SimulationEntity entity, PointF center, float radius)
    {
        RectangleF bar = new(center.X - 14f, center.Y - radius - 10f, 28f, 4f);
        using var bg = new SolidBrush(Color.FromArgb(180, 20, 24, 28));
        using var hp = new SolidBrush(Color.FromArgb(220, 88, 214, 124));
        graphics.FillRectangle(bg, bar);
        float fillWidth = (float)(bar.Width * Math.Clamp(entity.MaxHealth <= 1e-6 ? 0.0 : entity.Health / entity.MaxHealth, 0.0, 1.0));
        if (fillWidth > 0.4f)
        {
            graphics.FillRectangle(hp, bar.X, bar.Y, fillWidth, bar.Height);
        }
    }

    private void DrawFastProjectileLayer()
    {
        if (_fastProjectileLayerGraphics is null)
        {
            return;
        }

        Graphics graphics = _fastProjectileLayerGraphics;
        graphics.Clear(Color.Transparent);
        foreach (SimulationProjectile projectile in _host.World.Projectiles)
        {
            if (!TryProjectFlatWorld(projectile.X, projectile.Y, out PointF point))
            {
                continue;
            }

            Color tint = string.Equals(projectile.Team, "red", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(240, 255, 120, 120)
                : Color.FromArgb(240, 120, 172, 255);
            using var brush = new SolidBrush(tint);
            float size = string.Equals(projectile.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 5f : 3f;
            graphics.FillEllipse(brush, point.X - size * 0.5f, point.Y - size * 0.5f, size, size);
        }
    }

    private void DrawFastCombatMarkers(Graphics graphics, Rectangle mapRect)
    {
        foreach (FloatingCombatMarker marker in _combatMarkers)
        {
            if (!TryProjectFlatWorld(marker.WorldX, marker.WorldY, out PointF point))
            {
                continue;
            }

            using var brush = new SolidBrush(marker.Color);
            graphics.DrawString(marker.Text, _tinyHudFont, brush, point.X + 4f + marker.ScreenOffsetX, point.Y - 8f + marker.ScreenOffsetY);
        }
    }

    private void DrawFastTacticalRoutes(Graphics graphics)
    {
        IReadOnlyList<SimulationEntity> units = _host.GetControlCandidates(_host.SelectedTeam);
        if (units.Count == 0)
        {
            return;
        }

        using var attackPen = new Pen(Color.FromArgb(174, 255, 96, 76), 1.6f);
        using var defendPen = new Pen(Color.FromArgb(174, 92, 172, 255), 1.4f);
        using var patrolPen = new Pen(Color.FromArgb(168, 76, 220, 144), 1.3f);
        foreach (SimulationEntity unit in units)
        {
            if (string.IsNullOrWhiteSpace(unit.TacticalCommand) || !TryProjectFlatWorld(unit.X, unit.Y, out PointF from))
            {
                continue;
            }

            if (string.Equals(unit.TacticalCommand, "attack", StringComparison.OrdinalIgnoreCase))
            {
                SimulationEntity? target = _host.World.Entities.FirstOrDefault(entity =>
                    string.Equals(entity.Id, unit.TacticalTargetId, StringComparison.OrdinalIgnoreCase));
                if (target is not null && TryProjectFlatWorld(target.X, target.Y, out PointF to))
                {
                    graphics.DrawLine(attackPen, from, to);
                }
            }
            else if (TryProjectFlatWorld(unit.TacticalTargetX, unit.TacticalTargetY, out PointF to))
            {
                Pen pen = string.Equals(unit.TacticalCommand, "patrol", StringComparison.OrdinalIgnoreCase) ? patrolPen : defendPen;
                graphics.DrawLine(pen, from, to);
                if (string.Equals(unit.TacticalCommand, "patrol", StringComparison.OrdinalIgnoreCase))
                {
                    DrawFastPatrolCircle(graphics, unit.TacticalTargetX, unit.TacticalTargetY, Math.Max(4.0, unit.TacticalPatrolRadiusWorld), patrolPen);
                }
            }
        }
    }

    private void DrawFastPatrolCircle(Graphics graphics, double worldX, double worldY, double radiusWorld, Pen pen)
    {
        const int segments = 32;
        PointF? previous = null;
        for (int index = 0; index <= segments; index++)
        {
            double angle = index * Math.PI * 2.0 / segments;
            if (!TryProjectFlatWorld(worldX + Math.Cos(angle) * radiusWorld, worldY + Math.Sin(angle) * radiusWorld, out PointF point))
            {
                previous = null;
                continue;
            }

            if (previous is PointF last)
            {
                graphics.DrawLine(pen, last, point);
            }

            previous = point;
        }
    }

    private bool TryProjectFlatWorld(double worldX, double worldY, out PointF point)
    {
        point = default;
        if (_fastRendererMapRect.Width <= 0 || _fastRendererMapRect.Height <= 0)
        {
            return false;
        }

        point = new PointF(
            _fastRendererMapRect.X + (float)(worldX / Math.Max(1, _host.MapPreset.Width) * _fastRendererMapRect.Width),
            _fastRendererMapRect.Y + (float)(worldY / Math.Max(1, _host.MapPreset.Height) * _fastRendererMapRect.Height));
        return true;
    }

    private static Rectangle FitMapRect(Rectangle bounds, int width, int height)
    {
        float scale = Math.Min(bounds.Width / (float)Math.Max(1, width), bounds.Height / (float)Math.Max(1, height));
        int drawWidth = Math.Max(1, (int)Math.Round(width * scale));
        int drawHeight = Math.Max(1, (int)Math.Round(height * scale));
        return new Rectangle(
            bounds.X + (bounds.Width - drawWidth) / 2,
            bounds.Y + (bounds.Height - drawHeight) / 2,
            drawWidth,
            drawHeight);
    }

    private void DrawFacilityOverlay(Graphics graphics, Rectangle mapRect, FacilityRegion region, Pen pen)
    {
        if (string.Equals(region.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && region.Points.Count >= 3)
        {
            PointF[] points = region.Points
                .Select(point => new PointF(
                    mapRect.X + (float)(point.X / Math.Max(1, _host.MapPreset.Width) * mapRect.Width),
                    mapRect.Y + (float)(point.Y / Math.Max(1, _host.MapPreset.Height) * mapRect.Height)))
                .ToArray();
            graphics.DrawPolygon(pen, points);
            return;
        }

        if (string.Equals(region.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            PointF a = new(
                mapRect.X + (float)(region.X1 / Math.Max(1, _host.MapPreset.Width) * mapRect.Width),
                mapRect.Y + (float)(region.Y1 / Math.Max(1, _host.MapPreset.Height) * mapRect.Height));
            PointF b = new(
                mapRect.X + (float)(region.X2 / Math.Max(1, _host.MapPreset.Width) * mapRect.Width),
                mapRect.Y + (float)(region.Y2 / Math.Max(1, _host.MapPreset.Height) * mapRect.Height));
            graphics.DrawLine(pen, a, b);
            return;
        }

        float left = mapRect.X + (float)(Math.Min(region.X1, region.X2) / Math.Max(1, _host.MapPreset.Width) * mapRect.Width);
        float top = mapRect.Y + (float)(Math.Min(region.Y1, region.Y2) / Math.Max(1, _host.MapPreset.Height) * mapRect.Height);
        float right = mapRect.X + (float)(Math.Max(region.X1, region.X2) / Math.Max(1, _host.MapPreset.Width) * mapRect.Width);
        float bottom = mapRect.Y + (float)(Math.Max(region.Y1, region.Y2) / Math.Max(1, _host.MapPreset.Height) * mapRect.Height);
        graphics.DrawRectangle(pen, left, top, Math.Max(1f, right - left), Math.Max(1f, bottom - top));
    }

    private void EnsureFastDynamicLayerBitmaps()
    {
        if (_fastLayerBitmapClientSize == ClientSize
            && _fastEntityLayerBitmap is not null
            && _fastProjectileLayerBitmap is not null
            && _fastEntityLayerGraphics is not null
            && _fastProjectileLayerGraphics is not null)
        {
            return;
        }

        _fastEntityLayerGraphics?.Dispose();
        _fastProjectileLayerGraphics?.Dispose();
        _fastEntityLayerBitmap?.Dispose();
        _fastProjectileLayerBitmap?.Dispose();

        _fastLayerBitmapClientSize = ClientSize;
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _fastEntityLayerBitmap = null;
            _fastProjectileLayerBitmap = null;
            _fastEntityLayerGraphics = null;
            _fastProjectileLayerGraphics = null;
            return;
        }

        _fastEntityLayerBitmap = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _fastProjectileLayerBitmap = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _fastEntityLayerGraphics = Graphics.FromImage(_fastEntityLayerBitmap);
        _fastProjectileLayerGraphics = Graphics.FromImage(_fastProjectileLayerBitmap);
        _fastEntityLayerGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _fastProjectileLayerGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _fastEntityLayerGraphics.CompositingQuality = CompositingQuality.HighSpeed;
        _fastProjectileLayerGraphics.CompositingQuality = CompositingQuality.HighSpeed;
    }
}
