using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

    private void ToggleAutoAimAssistMode()
    {
        _autoAimAssistMode = _autoAimAssistMode == AutoAimAssistMode.HardLock
            ? AutoAimAssistMode.GuidanceOnly
            : AutoAimAssistMode.HardLock;
    }

    private void ToggleTacticalMode()
    {
        _tacticalMode = !_tacticalMode;
        if (_tacticalMode)
        {
            _firstPersonView = false;
            _followSelection = false;
            _cameraTargetM = ComputeMapCenterMeters();
            _cameraDistanceM = Math.Clamp(ComputeDefaultCameraDistance() * 0.85f, 22f, 150f);
            _cameraYawRad = -MathF.PI * 0.5f;
            _cameraPitchRad = 1.18f;
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

        Rectangle panel = new(20, ClientSize.Height - 154, 592, 130);
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
        (float energyRatio, string energyLabel) = ResolveEnergyGauge(entity);
        (float superCapRatio, string superCapLabel) = ResolveSuperCapGauge(entity);
        DrawMiniGauge(graphics, new RectangleF(barX, barY, 112, 10), entity.MaxHealth <= 0 ? 0f : (float)(entity.Health / entity.MaxHealth), Color.FromArgb(72, 214, 126), $"HP {(int)entity.Health}/{(int)entity.MaxHealth}");
        DrawMiniGauge(graphics, new RectangleF(barX + 126, barY, 96, 10), powerRatio, Color.FromArgb(75, 146, 232), powerLabel);
        DrawMiniGauge(graphics, new RectangleF(barX + 236, barY, 70, 10), entity.MaxHeat <= 0 ? 0f : (float)(entity.Heat / entity.MaxHeat), Color.FromArgb(228, 130, 58), $"H {(int)entity.Heat}");
        DrawMiniGauge(graphics, new RectangleF(barX + 320, barY, 92, 10), energyRatio, Color.FromArgb(88, 220, 208), energyLabel);
        DrawMiniGauge(graphics, new RectangleF(barX + 426, barY, 82, 10), superCapRatio, entity.SuperCapEnabled ? Color.FromArgb(255, 210, 76) : Color.FromArgb(152, 164, 178), superCapLabel);

        double speedMps = Math.Sqrt(
            entity.VelocityXWorldPerSec * entity.VelocityXWorldPerSec
            + entity.VelocityYWorldPerSec * entity.VelocityYWorldPerSec) * Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        string ammoText = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? $"42mm {entity.Ammo42Mm}"
            : $"17mm {entity.Ammo17Mm}";
        string sentryText = string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)
            ? $"   Stance {RuleSet.NormalizeSentryStance(entity.SentryStance)}"
            : string.Empty;
        string motionText = $"Ammo {ammoText}   Speed {speedMps:0.0}m/s   P {entity.ChassisPowerDrawW:0}W   Pitch {entity.GimbalPitchDeg:0}deg{sentryText}";
        graphics.DrawString(motionText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 54);

        string aimMode = _autoAimAssistMode == AutoAimAssistMode.HardLock ? "Lock" : "Guide";
        string autoAimText = entity.AutoAimLocked
            ? $"AutoAim {aimMode} {entity.AutoAimPlateDirection}  Hit {entity.AutoAimAccuracy:P0}  lead {entity.AutoAimLeadTimeSec:0.00}s/{entity.AutoAimLeadDistanceM:0.00}m  D{entity.AutoAimDistanceCoefficient:0.00} M{entity.AutoAimMotionCoefficient:0.00}"
            : $"AutoAim {aimMode} free  Hit --  no visible armor lock";
        graphics.DrawString(autoAimText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 72);

        (string buffText, string debuffText) = ResolveBuffDebuffSummary(entity);
        graphics.DrawString($"Buff {buffText}", _tinyHudFont, textBrush, panel.X + 16, panel.Y + 90);
        graphics.DrawString($"Debuff {debuffText}", _tinyHudFont, textBrush, panel.X + 300, panel.Y + 90);

        bool inFriendlySupply = IsInFriendlyFacility(entity, "supply", "buff_supply", "buff_base");
        bool inDeployZone = IsInFriendlyFacility(entity, "buff_hero_deployment");
        string supplyPrompt = inFriendlySupply ? "   B Resupply" : string.Empty;
        string deployText = entity.HeroDeploymentActive
            ? "   HERO DEPLOY AUTO"
            : entity.HeroDeploymentRequested
                ? "   HERO DEPLOY HOLD"
                : inDeployZone ? "   Z Deploy" : string.Empty;
        string sentryPrompt = string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)
            ? "   X SentryStance"
            : string.Empty;
        string statusText = $"T AimMode   H Tactical   RMB AutoAim   LMB Fire   Shift Spin   V View   C SuperCap{sentryPrompt}   F7 Telemetry{supplyPrompt}{deployText}";
        graphics.DrawString(statusText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 108);
    }

    private bool IsInFriendlyFacility(SimulationEntity entity, params string[] facilityTypes)
    {
        return _host.MapPreset.Facilities.Any(region =>
            facilityTypes.Any(type => string.Equals(region.Type, type, StringComparison.OrdinalIgnoreCase))
            && string.Equals(region.Team, entity.Team, StringComparison.OrdinalIgnoreCase)
            && region.Contains(entity.X, entity.Y));
    }

    private (string Buffs, string Debuffs) ResolveBuffDebuffSummary(SimulationEntity entity)
    {
        var buffs = new List<string>(8);
        var debuffs = new List<string>(6);

        AddTimedEffect(buffs, "Invincible", entity.RespawnInvincibleTimerSec);
        AddTimedEffect(buffs, "Highland DEF", entity.TerrainHighlandDefenseTimerSec);
        AddTimedEffect(buffs, "Fly DEF", entity.TerrainFlySlopeDefenseTimerSec);
        AddTimedEffect(buffs, "Road Cool", entity.TerrainRoadCoolingTimerSec);
        AddTimedEffect(buffs, "Slope DEF", entity.TerrainSlopeDefenseTimerSec);
        AddTimedEffect(buffs, "Slope Cool", entity.TerrainSlopeCoolingTimerSec);

        AddTimedEffect(debuffs, "Weak", entity.WeakTimerSec);
        AddTimedEffect(debuffs, "HeatLock", entity.HeatLockTimerSec);
        AddTimedEffect(debuffs, "AmmoLock", entity.RespawnAmmoLockTimerSec);
        AddTimedEffect(debuffs, "DeadZone", entity.DeadZoneTimerSec);

        if (_host.World.Teams.TryGetValue(entity.Team, out SimulationTeamState? teamState)
            && teamState.EnergyBuffTimerSec > 1e-3)
        {
            buffs.Add($"Energy {teamState.EnergyBuffTimerSec:0}s");
        }

        AddMultiplierEffect(buffs, debuffs, entity.DynamicDamageTakenMult, "Taken", buffWhenBelowOne: true);
        AddMultiplierEffect(buffs, debuffs, entity.DynamicDamageDealtMult, "Damage", buffWhenBelowOne: false);
        AddMultiplierEffect(buffs, debuffs, entity.DynamicCoolingMult, "Cooling", buffWhenBelowOne: false);
        AddMultiplierEffect(buffs, debuffs, entity.DynamicPowerRecoveryMult, "PowerRec", buffWhenBelowOne: false);

        return (JoinHudEffects(buffs), JoinHudEffects(debuffs));
    }

    private static void AddTimedEffect(List<string> effects, string label, double seconds)
    {
        if (seconds > 1e-3)
        {
            effects.Add($"{label} {seconds:0}s");
        }
    }

    private static void AddMultiplierEffect(
        List<string> buffs,
        List<string> debuffs,
        double value,
        string label,
        bool buffWhenBelowOne)
    {
        if (value < 0.995)
        {
            (buffWhenBelowOne ? buffs : debuffs).Add($"{label} x{value:0.00}");
        }
        else if (value > 1.005)
        {
            (buffWhenBelowOne ? debuffs : buffs).Add($"{label} x{value:0.00}");
        }
    }

    private static string JoinHudEffects(IReadOnlyList<string> effects)
    {
        if (effects.Count == 0)
        {
            return "none";
        }

        int shown = Math.Min(effects.Count, 4);
        string text = string.Join(", ", effects.Take(shown));
        return effects.Count > shown ? $"{text} +{effects.Count - shown}" : text;
    }

    private void DrawOrientationWidget(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || _appState != SimulatorAppState.InMatch)
        {
            return;
        }

        Rectangle panel = new(ClientSize.Width - 198, ClientSize.Height - 182, 166, 128);
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
        graphics.DrawString($"E {entity.ChassisEnergy / 1000.0:0.0}/{Math.Max(0.0, entity.MaxChassisEnergy) / 1000.0:0.0}kJ", _tinyHudFont, subTextBrush, panel.X + 92, panel.Y + 100);
        graphics.DrawString($"SC {(entity.SuperCapEnabled ? "ON" : "OFF")} {entity.SuperCapEnergyJ:0}J", _tinyHudFont, subTextBrush, panel.X + 92, panel.Y + 100);
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

        if (_firstPersonView)
        {
            EnsureProjectedTerrainFaceCache();
            foreach (ProjectedFace face in _cachedProjectedTerrainFaces)
            {
                using var faceBrush = new SolidBrush(face.FillColor);
                using var facePen = new Pen(face.EdgeColor, 1f);
                graphics.FillPolygon(faceBrush, face.Points);
                graphics.DrawPolygon(facePen, face.Points);
            }

            return;
        }

        EnsureTerrainLayerBitmapCache();
        if (_cachedTerrainLayerBitmap is null)
        {
            return;
        }

        graphics.DrawImageUnscaled(_cachedTerrainLayerBitmap, 0, 0);
    }

    private void EnsureProjectedTerrainFaceCache()
    {
        if (_terrainFaces.Count == 0)
        {
            _cachedProjectedTerrainFaces.Clear();
            _terrainProjectionBuiltVersion = _terrainProjectionCacheVersion;
            return;
        }

        Vector3 currentViewDirection = ResolveTerrainProjectionViewDirection();
        float positionToleranceSq = _firstPersonView ? 0.0009f : 0.032f;
        float targetToleranceSq = _firstPersonView ? 0.0016f : 0.048f;
        float directionDotTolerance = _firstPersonView ? 0.99985f : 0.9992f;
        float angleTolerance = _firstPersonView ? 0.0012f : 0.010f;
        float distanceTolerance = _firstPersonView ? 0.01f : 0.05f;
        bool cameraStable =
            _terrainProjectionBuiltVersion == _terrainProjectionCacheVersion
            && _terrainProjectionCacheClientSize == ClientSize
            && Vector3.DistanceSquared(_terrainProjectionCacheCameraPosition, _cameraPositionM) <= positionToleranceSq
            && Vector3.DistanceSquared(_terrainProjectionCacheCameraTarget, _cameraTargetM) <= targetToleranceSq
            && Vector3.Dot(_terrainProjectionCacheViewDirection, currentViewDirection) >= directionDotTolerance
            && MathF.Abs(_terrainProjectionCacheYawRad - _cameraYawRad) <= angleTolerance
            && MathF.Abs(_terrainProjectionCachePitchRad - _cameraPitchRad) <= angleTolerance
            && MathF.Abs(_terrainProjectionCacheDistanceM - _cameraDistanceM) <= distanceTolerance;
        if (cameraStable)
        {
            return;
        }

        CollectVisibleTerrainTiles(_terrainDrawBuffer);
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
        _cachedProjectedTerrainFaces.Clear();
        ReduceProjectedTerrainFacesForScreenTiles(_projectedTerrainFaceBuffer, _cachedProjectedTerrainFaces);
        _terrainProjectionCacheCameraPosition = _cameraPositionM;
        _terrainProjectionCacheCameraTarget = _cameraTargetM;
        _terrainProjectionCacheViewDirection = currentViewDirection;
        _terrainProjectionCacheYawRad = _cameraYawRad;
        _terrainProjectionCachePitchRad = _cameraPitchRad;
        _terrainProjectionCacheDistanceM = _cameraDistanceM;
        _terrainProjectionCacheClientSize = ClientSize;
        _terrainProjectionBuiltVersion = _terrainProjectionCacheVersion;
    }

    private void EnsureTerrainLayerBitmapCache()
    {
        EnsureProjectedTerrainFaceCache();
        if (_cachedProjectedTerrainFaces.Count == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _cachedTerrainLayerBitmap?.Dispose();
            _cachedTerrainLayerBitmap = null;
            _terrainLayerBitmapBuiltVersion = _terrainProjectionBuiltVersion;
            return;
        }

        Vector3 currentViewDirection = ResolveTerrainProjectionViewDirection();
        bool bitmapStable =
            _cachedTerrainLayerBitmap is not null
            && _terrainLayerBitmapBuiltVersion == _terrainProjectionBuiltVersion
            && _terrainLayerBitmapClientSize == ClientSize
            && Vector3.DistanceSquared(_terrainLayerBitmapCameraPosition, _cameraPositionM) <= 0.016f
            && Vector3.DistanceSquared(_terrainLayerBitmapCameraTarget, _cameraTargetM) <= 0.024f
            && Vector3.Dot(_terrainLayerBitmapViewDirection, currentViewDirection) >= 0.9995f;
        if (bitmapStable)
        {
            return;
        }

        Bitmap bitmap = new(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        using (Graphics layerGraphics = Graphics.FromImage(bitmap))
        {
            layerGraphics.SmoothingMode = SmoothingMode.None;
            layerGraphics.Clear(Color.Transparent);
            foreach (ProjectedFace face in _cachedProjectedTerrainFaces)
            {
                using var faceBrush = new SolidBrush(face.FillColor);
                using var facePen = new Pen(face.EdgeColor, 1f);
                layerGraphics.FillPolygon(faceBrush, face.Points);
                layerGraphics.DrawPolygon(facePen, face.Points);
            }
        }

        _cachedTerrainLayerBitmap?.Dispose();
        _cachedTerrainLayerBitmap = bitmap;
        _terrainLayerBitmapBuiltVersion = _terrainProjectionBuiltVersion;
        _terrainLayerBitmapCameraPosition = _cameraPositionM;
        _terrainLayerBitmapCameraTarget = _cameraTargetM;
        _terrainLayerBitmapViewDirection = currentViewDirection;
        _terrainLayerBitmapClientSize = ClientSize;
    }

    private Vector3 ResolveTerrainProjectionViewDirection()
    {
        Vector3 delta = _cameraTargetM - _cameraPositionM;
        float lengthSquared = delta.LengthSquared();
        if (lengthSquared <= 1e-6f)
        {
            return Vector3.UnitZ;
        }

        return delta / MathF.Sqrt(lengthSquared);
    }

    private void ReduceProjectedTerrainFacesForScreenTiles(List<ProjectedFace> source, List<ProjectedFace> target)
    {
        target.Clear();
        if (source.Count == 0)
        {
            return;
        }

        if (!_firstPersonView)
        {
            target.AddRange(source);
            return;
        }

        int tileWidth = Math.Max(96, ClientSize.Width / 10);
        int tileHeight = Math.Max(84, ClientSize.Height / 8);
        var tinyFaceCountByTile = new Dictionary<int, int>();
        foreach (ProjectedFace face in source)
        {
            GetProjectedFaceBounds(face, out float minX, out float minY, out float maxX, out float maxY);
            float width = Math.Max(1f, maxX - minX);
            float height = Math.Max(1f, maxY - minY);
            float area = width * height;
            bool ultraFar = face.AverageDepth > 34f;
            bool far = face.AverageDepth > 22f;
            bool mediumFar = face.AverageDepth > 14f;
            bool tinyFace =
                (area < 260f && ultraFar)
                || (area < 180f && far)
                || (area < 90f && mediumFar);
            if (!tinyFace)
            {
                target.Add(ApplyFarTerrainColorLod(face));
                continue;
            }

            float centerX = (minX + maxX) * 0.5f;
            float centerY = (minY + maxY) * 0.5f;
            int tileX = Math.Clamp((int)(centerX / Math.Max(1, tileWidth)), 0, Math.Max(0, ClientSize.Width / Math.Max(1, tileWidth)));
            int tileY = Math.Clamp((int)(centerY / Math.Max(1, tileHeight)), 0, Math.Max(0, ClientSize.Height / Math.Max(1, tileHeight)));
            int tileKey = tileY * 1024 + tileX;
            int count = tinyFaceCountByTile.TryGetValue(tileKey, out int existing) ? existing : 0;
            int tileBudget = ultraFar ? 1 : far ? 2 : 3;
            if (count >= tileBudget)
            {
                continue;
            }

            tinyFaceCountByTile[tileKey] = count + 1;
            target.Add(ApplyFarTerrainColorLod(face));
        }
    }

    private static ProjectedFace ApplyFarTerrainColorLod(ProjectedFace face)
    {
        if (face.AverageDepth <= 10f)
        {
            return face;
        }

        float farRatio = Math.Clamp((face.AverageDepth - 10f) / 30f, 0f, 1f);
        Color neutralFill = Color.FromArgb(face.FillColor.A, 214, 216, 220);
        Color neutralEdge = Color.FromArgb(face.EdgeColor.A, 110, 114, 120);
        Color fill = BlendColor(face.FillColor, neutralFill, 0.22f * farRatio + 0.38f * farRatio * farRatio);
        Color edge = BlendColor(face.EdgeColor, neutralEdge, 0.18f * farRatio + 0.30f * farRatio * farRatio);
        return new ProjectedFace(face.Points, face.AverageDepth, fill, edge);
    }

    private static void GetProjectedFaceBounds(ProjectedFace face, out float minX, out float minY, out float maxX, out float maxY)
    {
        minX = float.MaxValue;
        minY = float.MaxValue;
        maxX = float.MinValue;
        maxY = float.MinValue;
        foreach (PointF point in face.Points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
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

            if (ShouldCullTerrainFaceForPerformance(face))
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

    private bool ShouldCullTerrainFaceForPerformance(TerrainFacePatch face)
    {
        if (!_firstPersonView)
        {
            return false;
        }

        Vector3 toFace = face.CenterScene - _cameraPositionM;
        float distance = toFace.Length();
        if (distance <= 11f)
        {
            return false;
        }

        Vector3 direction = ResolveTerrainProjectionViewDirection();
        if (distance > 1e-4f)
        {
            float forwardDot = Vector3.Dot(toFace / distance, direction);
            if (forwardDot < 0.20f)
            {
                return true;
            }

            if (distance > 15f && forwardDot < 0.34f)
            {
                return true;
            }

            if (distance > 24f && forwardDot < 0.48f)
            {
                return true;
            }

            if (distance > 30f && forwardDot < 0.62f)
            {
                return true;
            }
        }

        return false;
    }

    private void RebuildVisibleTerrainDetailCache(bool force)
    {
        if (_cachedRuntimeGrid is null || !_cachedRuntimeGrid.IsValid)
        {
            return;
        }

        if (!TryResolveTerrainFocusWorld(
                out float focusXWorld,
                out float focusYWorld,
                out float secondaryFocusXWorld,
                out float secondaryFocusYWorld,
                out bool hasSecondaryFocus))
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
        int secondaryCenterCellX = hasSecondaryFocus
            ? Math.Clamp(
                (int)MathF.Floor(secondaryFocusXWorld / Math.Max(_cachedRuntimeGrid.CellWidthWorld, 1e-6f)),
                0,
                _cachedRuntimeGrid.WidthCells - 1)
            : centerCellX;
        int secondaryCenterCellY = hasSecondaryFocus
            ? Math.Clamp(
                (int)MathF.Floor(secondaryFocusYWorld / Math.Max(_cachedRuntimeGrid.CellHeightWorld, 1e-6f)),
                0,
                _cachedRuntimeGrid.HeightCells - 1)
            : centerCellY;

        float radiusM = ResolveTerrainDetailRadiusM();
        float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        float radiusWorld = radiusM / metersPerWorldUnit;
        int radiusCellsX = Math.Clamp((int)MathF.Ceiling(radiusWorld / Math.Max(_cachedRuntimeGrid.CellWidthWorld, 1e-6f)), 4, _cachedRuntimeGrid.WidthCells);
        int radiusCellsY = Math.Clamp((int)MathF.Ceiling(radiusWorld / Math.Max(_cachedRuntimeGrid.CellHeightWorld, 1e-6f)), 4, _cachedRuntimeGrid.HeightCells);
        int rebuildThresholdX;
        int rebuildThresholdY;
        if (_firstPersonView)
        {
            rebuildThresholdX = Math.Max(6, (int)MathF.Ceiling(0.18f / Math.Max(_cachedRuntimeGrid.CellWidthWorld * metersPerWorldUnit, 1e-4f)));
            rebuildThresholdY = Math.Max(6, (int)MathF.Ceiling(0.18f / Math.Max(_cachedRuntimeGrid.CellHeightWorld * metersPerWorldUnit, 1e-4f)));
        }
        else
        {
            rebuildThresholdX = Math.Max(3, radiusCellsX / 3);
            rebuildThresholdY = Math.Max(3, radiusCellsY / 3);
        }

        if (!force
            && _terrainDetailFaces.Count > 0
            && Math.Abs(centerCellX - _terrainDetailCenterCellX) <= rebuildThresholdX
            && Math.Abs(centerCellY - _terrainDetailCenterCellY) <= rebuildThresholdY
            && Math.Abs(secondaryCenterCellX - _terrainDetailCenterCellX) <= radiusCellsX
            && Math.Abs(secondaryCenterCellY - _terrainDetailCenterCellY) <= radiusCellsY)
        {
            return;
        }

        if (!force
            && _firstPersonView
            && _terrainDetailFaces.Count > 0
            && _lastTerrainDetailRebuildTicks > 0
            && (_frameClock.ElapsedTicks - _lastTerrainDetailRebuildTicks) / (double)Stopwatch.Frequency < 0.055)
        {
            return;
        }

        if (!force
            && !_firstPersonView
            && _terrainDetailFaces.Count > 0
            && _lastTerrainDetailRebuildTicks > 0
            && (_frameClock.ElapsedTicks - _lastTerrainDetailRebuildTicks) / (double)Stopwatch.Frequency < 0.18)
        {
            return;
        }

        int minCenterCellX = Math.Min(centerCellX, secondaryCenterCellX);
        int maxCenterCellX = Math.Max(centerCellX, secondaryCenterCellX);
        int minCenterCellY = Math.Min(centerCellY, secondaryCenterCellY);
        int maxCenterCellY = Math.Max(centerCellY, secondaryCenterCellY);
        int startCellX = Math.Max(0, minCenterCellX - radiusCellsX);
        int endCellX = Math.Min(_cachedRuntimeGrid.WidthCells, maxCenterCellX + radiusCellsX + 1);
        int startCellY = Math.Max(0, minCenterCellY - radiusCellsY);
        int endCellY = Math.Min(_cachedRuntimeGrid.HeightCells, maxCenterCellY + radiusCellsY + 1);

        _terrainDetailFaces.Clear();
        _terrainDetailCenterCellX = centerCellX;
        _terrainDetailCenterCellY = centerCellY;
        _terrainDetailMinXWorld = startCellX * _cachedRuntimeGrid.CellWidthWorld;
        _terrainDetailMaxXWorld = endCellX * _cachedRuntimeGrid.CellWidthWorld;
        _terrainDetailMinYWorld = startCellY * _cachedRuntimeGrid.CellHeightWorld;
        _terrainDetailMaxYWorld = endCellY * _cachedRuntimeGrid.CellHeightWorld;
        int detailStep = ResolveTerrainDetailStepForView(_cachedRuntimeGrid);
        RebuildTerrainTileCacheMerged(detailStep, detailStep, _terrainDetailFaces, startCellX, endCellX, startCellY, endCellY);
        _lastTerrainDetailRebuildTicks = _frameClock.ElapsedTicks;
        _terrainProjectionCacheVersion++;
        _terrainProjectionBuiltVersion = -1;
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

    private int ResolveTerrainDetailStepForView(RuntimeGridData runtimeGrid)
    {
        int step = ResolveTerrainDetailStep(runtimeGrid);
        if (_firstPersonView)
        {
            return Math.Min(4, step + 1);
        }

        return step;
    }

    private bool TryResolveTerrainFocusWorld(
        out float focusXWorld,
        out float focusYWorld,
        out float secondaryFocusXWorld,
        out float secondaryFocusYWorld,
        out bool hasSecondaryFocus)
    {
        float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        SimulationEntity? selected = _host.SelectedEntity;
        if (_firstPersonView)
        {
            focusXWorld = _cameraPositionM.X / metersPerWorldUnit;
            focusYWorld = _cameraPositionM.Z / metersPerWorldUnit;
            Vector3 lookDirection = ResolveTerrainProjectionViewDirection();
            float lookAheadMeters = Math.Clamp(ResolveTerrainDetailRadiusM() * 0.9f, 4.5f, 10.5f);
            Vector3 forwardHotspot = _cameraPositionM + lookDirection * lookAheadMeters;
            secondaryFocusXWorld = forwardHotspot.X / metersPerWorldUnit;
            secondaryFocusYWorld = forwardHotspot.Z / metersPerWorldUnit;
            hasSecondaryFocus = true;
            return true;
        }

        if (selected is not null && _followSelection)
        {
            focusXWorld = (float)selected.X;
            focusYWorld = (float)selected.Y;
            Vector3 lookDirection = ResolveTerrainProjectionViewDirection();
            float lookAheadMeters = Math.Clamp(_cameraDistanceM * 0.42f, 4.0f, 12.0f);
            Vector3 forwardHotspot = _cameraTargetM + lookDirection * lookAheadMeters;
            secondaryFocusXWorld = forwardHotspot.X / metersPerWorldUnit;
            secondaryFocusYWorld = forwardHotspot.Z / metersPerWorldUnit;
            hasSecondaryFocus = true;
            return true;
        }

        focusXWorld = _cameraTargetM.X / metersPerWorldUnit;
        focusYWorld = _cameraTargetM.Z / metersPerWorldUnit;
        Vector3 orbitLookDirection = ResolveTerrainProjectionViewDirection();
        float orbitLookAheadMeters = Math.Clamp(_cameraDistanceM * 0.5f, 5.0f, 14.0f);
        Vector3 orbitForwardHotspot = _cameraTargetM + orbitLookDirection * orbitLookAheadMeters;
        secondaryFocusXWorld = orbitForwardHotspot.X / metersPerWorldUnit;
        secondaryFocusYWorld = orbitForwardHotspot.Z / metersPerWorldUnit;
        hasSecondaryFocus = true;
        return true;
    }

    private float ResolveTerrainDetailRadiusM()
    {
        if (_firstPersonView)
        {
            return 4.2f;
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
        if (!IsSceneBoundsPotentiallyVisible(face.CenterScene, 0.5f, 0.35f))
        {
            return false;
        }

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
