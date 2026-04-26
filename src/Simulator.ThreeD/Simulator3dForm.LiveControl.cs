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

    private readonly record struct HeroLobCalibrationPreview(
        string TargetId,
        string PlateId,
        Vector3 PlateCenterScene,
        Vector3 ImpactScene,
        bool HasImpactPoint,
        bool HitsPlate,
        bool CrossedPlatePlane,
        float HorizontalOffsetM,
        float VerticalOffsetM,
        float DepthOffsetM,
        float TotalOffsetM,
        float NormalAngleDeg,
        bool FireWindowReady,
        float PlateWidthM,
        float PlateHeightM,
        bool HasSuggestedFireWindow,
        float SecondsToFireWindow);

    private HeroLobCalibrationPreview _heroLobCalibrationPreviewCache;
    private bool _heroLobCalibrationPreviewCacheValid;
    private bool _heroLobCalibrationPreviewCacheIncludesFireWindowSuggestion;
    private string? _heroLobCalibrationPreviewCacheShooterId;
    private string? _heroLobCalibrationPreviewCacheTargetId;
    private string? _heroLobCalibrationPreviewCachePlateId;
    private double _heroLobCalibrationPreviewCacheWorldTimeSec;
    private double _heroLobCalibrationPreviewCacheLeadTimeSec;
    private double _heroLobCalibrationPreviewCacheShooterX;
    private double _heroLobCalibrationPreviewCacheShooterY;
    private double _heroLobCalibrationPreviewCacheTurretYawDeg;
    private double _heroLobCalibrationPreviewCacheGimbalPitchDeg;

    private bool TryGetHeroLobCalibrationPreviewCached(
        SimulationEntity shooter,
        out HeroLobCalibrationPreview preview,
        bool includeFireWindowSuggestion)
    {
        if (TryReuseHeroLobCalibrationPreview(shooter, includeFireWindowSuggestion, out preview))
        {
            return true;
        }

        if (!TryBuildHeroLobCalibrationPreview(shooter, out preview, includeFireWindowSuggestion))
        {
            _heroLobCalibrationPreviewCacheValid = false;
            return false;
        }

        _heroLobCalibrationPreviewCache = preview;
        _heroLobCalibrationPreviewCacheValid = true;
        _heroLobCalibrationPreviewCacheIncludesFireWindowSuggestion = includeFireWindowSuggestion;
        _heroLobCalibrationPreviewCacheShooterId = shooter.Id;
        _heroLobCalibrationPreviewCacheTargetId = shooter.AutoAimTargetId;
        _heroLobCalibrationPreviewCachePlateId = shooter.AutoAimPlateId;
        _heroLobCalibrationPreviewCacheWorldTimeSec = _host.World.GameTimeSec;
        _heroLobCalibrationPreviewCacheLeadTimeSec = shooter.AutoAimLeadTimeSec;
        _heroLobCalibrationPreviewCacheShooterX = shooter.X;
        _heroLobCalibrationPreviewCacheShooterY = shooter.Y;
        _heroLobCalibrationPreviewCacheTurretYawDeg = shooter.TurretYawDeg;
        _heroLobCalibrationPreviewCacheGimbalPitchDeg = shooter.GimbalPitchDeg;
        return true;
    }

    private bool TryReuseHeroLobCalibrationPreview(
        SimulationEntity shooter,
        bool includeFireWindowSuggestion,
        out HeroLobCalibrationPreview preview)
    {
        preview = default;
        if (!_heroLobCalibrationPreviewCacheValid)
        {
            return false;
        }

        if (!string.Equals(_heroLobCalibrationPreviewCacheShooterId, shooter.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_heroLobCalibrationPreviewCacheTargetId, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_heroLobCalibrationPreviewCachePlateId, shooter.AutoAimPlateId, StringComparison.OrdinalIgnoreCase)
            || (includeFireWindowSuggestion && !_heroLobCalibrationPreviewCacheIncludesFireWindowSuggestion))
        {
            return false;
        }

        if (Math.Abs(_host.World.GameTimeSec - _heroLobCalibrationPreviewCacheWorldTimeSec) > 0.050
            || Math.Abs(shooter.AutoAimLeadTimeSec - _heroLobCalibrationPreviewCacheLeadTimeSec) > 0.010
            || Math.Abs(shooter.X - _heroLobCalibrationPreviewCacheShooterX) > 0.020
            || Math.Abs(shooter.Y - _heroLobCalibrationPreviewCacheShooterY) > 0.020
            || Math.Abs(shooter.TurretYawDeg - _heroLobCalibrationPreviewCacheTurretYawDeg) > 0.25
            || Math.Abs(shooter.GimbalPitchDeg - _heroLobCalibrationPreviewCacheGimbalPitchDeg) > 0.20)
        {
            return false;
        }

        preview = _heroLobCalibrationPreviewCache;
        return true;
    }

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
            SnapCameraToSelectedEntity();
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
            ReleaseMouseCapture();
            ResetLiveInput();
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
        string controlLabel = entity.IsPlayerControlled ? "\u624b\u52a8" : "\u81ea\u52a8";
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        using var textBrush = new SolidBrush(Color.FromArgb(206, 216, 226));
        graphics.DrawString($"{entity.Id}  {roleLabel}  {controlLabel}", _smallHudFont, titleBrush, panel.X + 16, panel.Y + 10);

        float barX = panel.X + 16;
        float barY = panel.Y + 35;
        (float powerRatio, string powerLabel) = ResolvePowerGauge(entity);
        (float energyRatio, string energyLabel) = ResolveEnergyGauge(entity);
        (float superCapRatio, string superCapLabel) = ResolveSuperCapGauge(entity);
        DrawMiniGauge(graphics, new RectangleF(barX, barY, 112, 10), entity.MaxHealth <= 0 ? 0f : (float)(entity.Health / entity.MaxHealth), Color.FromArgb(72, 214, 126), $"\u8840\u91cf {(int)entity.Health}/{(int)entity.MaxHealth}");
        DrawPowerGauge(graphics, new RectangleF(barX + 126, barY, 96, 10), entity, powerRatio, powerLabel);
        DrawMiniGauge(graphics, new RectangleF(barX + 236, barY, 70, 10), entity.MaxHeat <= 0 ? 0f : (float)(entity.Heat / entity.MaxHeat), Color.FromArgb(228, 130, 58), $"\u70ed\u91cf {(int)entity.Heat}");
        DrawMiniGauge(graphics, new RectangleF(barX + 320, barY, 92, 10), energyRatio, Color.FromArgb(88, 220, 208), energyLabel);
        DrawMiniGauge(graphics, new RectangleF(barX + 426, barY, 82, 10), superCapRatio, entity.SuperCapEnabled ? Color.FromArgb(255, 210, 76) : Color.FromArgb(152, 164, 178), superCapLabel);

        (double speedMps, _, _) = ResolveDisplayWorldVelocity(entity);
        string ammoText = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? $"42mm {entity.Ammo42Mm}"
            : $"17mm {entity.Ammo17Mm}";
        double fireRateHz = ResolveDisplayedFireRateHz(entity);
        string sentryText = string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)
            ? $"   \u54e8\u5175\u5f62\u6001 {ResolveSentryStanceLabel(entity.SentryStance)}"
            : string.Empty;
        string gyroText = entity.SmallGyroActive
            ? $"   小陀螺ω {entity.AngularVelocityDegPerSec:+0;-0;0}\u00b0/s"
            : string.Empty;
        string motionText = $"\u5f39\u836f {ammoText}   \u5c04\u9891 {fireRateHz:0.0}Hz   \u5927\u5730\u901f\u5ea6 {speedMps:0.0}m/s{gyroText}{sentryText}";
        graphics.DrawString(motionText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 54);

        string aimMode = _autoAimAssistMode == AutoAimAssistMode.HardLock ? "\u786c\u9501" : "\u5f15\u5bfc";
        string targetTypeText = ResolveAutoAimTargetTypeText(entity);
        string autoAimText = entity.AutoAimLocked
            ? $"\u81ea\u7784 {aimMode} {targetTypeText} {entity.AutoAimPlateDirection}   \u547d\u4e2d {entity.AutoAimAccuracy:P0}   \u63d0\u524d {entity.AutoAimLeadTimeSec:0.00}s/{entity.AutoAimLeadDistanceM:0.00}m"
            : $"\u81ea\u7784 {aimMode}   \u672a\u9501\u5b9a{targetTypeText}";
        graphics.DrawString(autoAimText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 72);

        bool inFriendlySupply = IsInFriendlyFacility(entity, "supply", "buff_supply");
        bool inDeployZone = IsInFriendlyFacility(entity, "buff_hero_deployment");
        string supplyPrompt = inFriendlySupply ? "   B \u8865\u7ed9" : string.Empty;
        string deployText = entity.HeroDeploymentActive
            ? "   \u957f\u6309Z\u9000\u51fa\u90e8\u7f72"
            : entity.HeroDeploymentRequested
                ? "   \u82f1\u96c4\u90e8\u7f72\u8bfb\u6761"
                : inDeployZone ? "   \u957f\u6309Z\u90e8\u7f72" : string.Empty;
        string sentryPrompt = string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)
            ? "   X \u54e8\u5175\u5f62\u6001"
            : string.Empty;
        string statusText = $"\u76ee\u6807 {ResolveAutoAimModeLabel(entity)}   \u5de6\u952e\u5f00\u706b   \u53f3\u952e\u81ea\u7784   F5\u5e2e\u52a9   F1\u6307\u6325{sentryPrompt}{supplyPrompt}{deployText}";
        graphics.DrawString(statusText, _tinyHudFont, textBrush, panel.X + 16, panel.Y + 96);
    }

    private void DrawHeroLobCalibrationOverlay(Graphics graphics)
    {
        DrawHeroLobCalibrationOverlayLocalized(graphics);
        return;

#if false
        SimulationEntity? shooter = _host.SelectedEntity;
        if (shooter is null
            || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || !SimulationCombatMath.IsHeroLobAutoAimMode(shooter))
        {
            return;
        }

        Rectangle panel = new(20, ClientSize.Height - 344, 592, 178);
        Rectangle previewRect = GetHeroLobSubviewRect();
        using GraphicsPath path = CreateRoundedRectangle(panel, 6);
        using var fill = new SolidBrush(Color.FromArgb(220, 10, 16, 24));
        using var border = new Pen(Color.FromArgb(160, 112, 134, 156), 1f);
        using (var fillRegion = new Region(path))
        {
            // Leave the sub-viewport transparent so the dedicated 3D secondary
            // camera can stay visible under the HUD panel.
            fillRegion.Exclude(previewRect);
            graphics.FillRegion(fill, fillRegion);
        }
        graphics.DrawPath(border, path);

        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        using var textBrush = new SolidBrush(Color.FromArgb(206, 216, 226));
        graphics.DrawString("吊射弹道校准", _smallHudFont, titleBrush, panel.X + 14, panel.Y + 10);
        string modeLine = _autoAimAssistMode == AutoAimAssistMode.GuidanceOnly
            ? "引导模式：只显示预测点和弹道窗口，不接管云台/相机/扳机。"
            : "强锁/部署：只在预测板面、中心偏差、法线夹角同时满足时自动扳机。";

        bool trackingSubviewTarget = IsHeroLobSubviewTrackingTarget(shooter);
        if (!TryGetHeroLobCalibrationPreviewCached(shooter, out HeroLobCalibrationPreview preview, includeFireWindowSuggestion: true))
        {
            DrawHeroLobSubviewFrame(graphics, previewRect, hasPreview: false, locked: trackingSubviewTarget);
            graphics.DrawString(
                "右键锁定前哨站/基地装甲板后，这里会显示当前弹道的命中点或偏移量。",
                _tinyHudFont,
                textBrush,
                panel.X + 14,
                panel.Y + 38);
            graphics.DrawString(
                "普通模式仅处理 8m 内车体装甲板；吊射模式全距离处理前哨站/基地装甲板。",
                _tinyHudFont,
                textBrush,
                panel.X + 14,
                panel.Y + 58);
            graphics.DrawString(
                modeLine,
                _tinyHudFont,
                textBrush,
                panel.X + 14,
                panel.Y + 78);
            return;
        }

        DrawHeroLobSubviewFrame(graphics, previewRect, hasPreview: true, locked: trackingSubviewTarget);

        Color accentColor = preview.HitsPlate
            ? Color.FromArgb(92, 224, 144)
            : Color.FromArgb(255, 198, 96);
        using var accentBrush = new SolidBrush(accentColor);
        string summary = preview.HitsPlate
            ? $"命中 {preview.TargetId}/{preview.PlateId}"
            : $"未命中 {preview.TargetId}/{preview.PlateId}";
        graphics.DrawString(summary, _tinyHudFont, accentBrush, panel.X + 204, panel.Y + 40);
        string fireWindowText = preview.FireWindowReady
            ? "窗口：可发射"
            : "窗口：等待装甲板正面/中心窗口";
        graphics.DrawString(
            $"{fireWindowText}   法线夹角 {preview.NormalAngleDeg:0.0}° / 45°",
            _tinyHudFont,
            preview.FireWindowReady ? accentBrush : textBrush,
            panel.X + 204,
            panel.Y + 118);
        string fireAdvice = preview.FireWindowReady
            ? "\u5efa\u8bae\uff1a\u73b0\u5728\u53d1\u5c04"
            : preview.HasSuggestedFireWindow
                ? $"\u5efa\u8bae\uff1a\u7b49\u5f85 {preview.SecondsToFireWindow:0.00}s \u540e\u518d\u53d1\u5c04"
                : "\u5efa\u8bae\uff1a\u5f53\u524d\u59ff\u6001 2.4s \u5185\u65e0\u5408\u9002\u7a97\u53e3";
        graphics.DrawString(
            fireAdvice,
            _tinyHudFont,
            preview.FireWindowReady ? accentBrush : textBrush,
            panel.X + 204,
            panel.Y + 138);
        graphics.DrawString(
            modeLine,
            _tinyHudFont,
            textBrush,
            panel.X + 204,
            panel.Y + 158);
        if (preview.HitsPlate)
        {
            graphics.DrawString(
                $"命中点 右偏 {preview.HorizontalOffsetM:+0.000;-0.000;0.000}m   高偏 {preview.VerticalOffsetM:+0.000;-0.000;0.000}m",
                _tinyHudFont,
                textBrush,
                panel.X + 204,
                panel.Y + 62);
            graphics.DrawString(
                $"plate {preview.PlateWidthM:0.000}m x {preview.PlateHeightM:0.000}m",
                _tinyHudFont,
                textBrush,
                panel.X + 204,
                panel.Y + 82);
        }
        else
        {
            string missMode = preview.CrossedPlatePlane ? "穿过板平面但未落在板面内" : "未穿过板平面";
            graphics.DrawString(
                $"{missMode}   横偏 {preview.HorizontalOffsetM:+0.000;-0.000;0.000}m   高偏 {preview.VerticalOffsetM:+0.000;-0.000;0.000}m   深度 {preview.DepthOffsetM:+0.000;-0.000;0.000}m   总偏差 {preview.TotalOffsetM:0.000}m",
                _tinyHudFont,
                textBrush,
                new RectangleF(panel.X + 204, panel.Y + 58, panel.Width - 218, 42));
        }

        DrawHeroLobCalibrationWorldMarker(graphics, preview);
#endif
    }

    private void DrawHeroLobCalibrationOverlayLocalized(Graphics graphics)
    {
        {
            SimulationEntity? shooter = _host.SelectedEntity;
            if (shooter is null
                || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                || !SimulationCombatMath.IsHeroLobAutoAimMode(shooter)
                || !ShouldShowHeroLobSubview(shooter))
            {
                return;
            }

            Rectangle panel = new(20, ClientSize.Height - 344, 592, 178);
            Rectangle previewRect = GetHeroLobSubviewRect();
            using GraphicsPath path = CreateRoundedRectangle(panel, 6);
            using var fill = new SolidBrush(Color.FromArgb(220, 10, 16, 24));
            using var border = new Pen(Color.FromArgb(160, 112, 134, 156), 1f);
            using (var fillRegion = new Region(path))
            {
                fillRegion.Exclude(previewRect);
                graphics.FillRegion(fill, fillRegion);
            }

            graphics.DrawPath(border, path);

            using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
            using var textBrush = new SolidBrush(Color.FromArgb(206, 216, 226));
            graphics.DrawString("吊射弹道校准", _smallHudFont, titleBrush, panel.X + 14, panel.Y + 10);
            string modeLine = _autoAimAssistMode == AutoAimAssistMode.GuidanceOnly
                ? "引导模式：只显示预测命中点和发射窗口，不接管云台或火控。"
                : "硬锁/部署：只有命中窗口、中心误差与法线夹角同时满足时才自动开火。";

            bool trackingSubviewTarget = IsHeroLobSubviewTrackingTarget(shooter);
            if (!TryGetHeroLobCalibrationPreviewCached(shooter, out HeroLobCalibrationPreview preview, includeFireWindowSuggestion: true))
            {
                DrawHeroLobSubviewFrame(graphics, previewRect, hasPreview: false, locked: trackingSubviewTarget);
                graphics.DrawString("右键锁定前哨站或基地装甲板后，这里会显示当前弹道的命中点或偏移量。", _tinyHudFont, textBrush, panel.X + 14, panel.Y + 38);
                graphics.DrawString("普通模式仅处理 8m 内车体装甲板；吊射模式全距离处理前哨站/基地装甲板。", _tinyHudFont, textBrush, panel.X + 14, panel.Y + 58);
                graphics.DrawString(modeLine, _tinyHudFont, textBrush, panel.X + 14, panel.Y + 78);
                return;
            }

            DrawHeroLobSubviewFrame(graphics, previewRect, hasPreview: true, locked: trackingSubviewTarget);

            Color accentColor = preview.HitsPlate
                ? Color.FromArgb(92, 224, 144)
                : Color.FromArgb(255, 198, 96);
            using var accentBrush = new SolidBrush(accentColor);
            string summary = preview.HitsPlate
                ? $"命中 {preview.TargetId}/{preview.PlateId}"
                : $"未命中 {preview.TargetId}/{preview.PlateId}";
            graphics.DrawString(summary, _tinyHudFont, accentBrush, panel.X + 204, panel.Y + 40);
            string fireWindowText = preview.FireWindowReady
                ? "窗口：允许发射"
                : "窗口：等待装甲板正面/中心窗口";
            graphics.DrawString($"{fireWindowText}   法线夹角 {preview.NormalAngleDeg:0.0}° / 45°", _tinyHudFont, preview.FireWindowReady ? accentBrush : textBrush, panel.X + 204, panel.Y + 118);
            string fireAdvice = preview.FireWindowReady
                ? "建议：现在发射"
                : preview.HasSuggestedFireWindow
                    ? $"建议：等待 {preview.SecondsToFireWindow:0.00}s 后再发射"
                    : "建议：当前姿态 2.4s 内无合适窗口";
            graphics.DrawString(fireAdvice, _tinyHudFont, preview.FireWindowReady ? accentBrush : textBrush, panel.X + 204, panel.Y + 138);
            graphics.DrawString(modeLine, _tinyHudFont, textBrush, panel.X + 204, panel.Y + 158);
            if (preview.HitsPlate)
            {
                graphics.DrawString($"命中点：右偏 {preview.HorizontalOffsetM:+0.000;-0.000;0.000}m   高偏 {preview.VerticalOffsetM:+0.000;-0.000;0.000}m", _tinyHudFont, textBrush, panel.X + 204, panel.Y + 62);
                graphics.DrawString($"装甲板 {preview.PlateWidthM:0.000}m x {preview.PlateHeightM:0.000}m", _tinyHudFont, textBrush, panel.X + 204, panel.Y + 82);
            }
            else
            {
                string missMode = preview.CrossedPlatePlane ? "穿过板平面但未落在板面内" : "未穿过板平面";
                graphics.DrawString($"{missMode}   横偏 {preview.HorizontalOffsetM:+0.000;-0.000;0.000}m   高偏 {preview.VerticalOffsetM:+0.000;-0.000;0.000}m   深度 {preview.DepthOffsetM:+0.000;-0.000;0.000}m   总偏差 {preview.TotalOffsetM:0.000}m", _tinyHudFont, textBrush, new RectangleF(panel.X + 204, panel.Y + 58, panel.Width - 218, 42));
            }

            DrawHeroLobCalibrationWorldMarker(graphics, preview);
            return;
        }

        SimulationEntity? shooter = _host.SelectedEntity;
        if (shooter is null
            || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || !SimulationCombatMath.IsHeroLobAutoAimMode(shooter))
        {
            return;
        }

        Rectangle panel = new(20, ClientSize.Height - 344, 592, 178);
        Rectangle previewRect = GetHeroLobSubviewRect();
        using GraphicsPath path = CreateRoundedRectangle(panel, 6);
        using var fill = new SolidBrush(Color.FromArgb(220, 10, 16, 24));
        using var border = new Pen(Color.FromArgb(160, 112, 134, 156), 1f);
        using (var fillRegion = new Region(path))
        {
            fillRegion.Exclude(previewRect);
            graphics.FillRegion(fill, fillRegion);
        }

        graphics.DrawPath(border, path);

        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        using var textBrush = new SolidBrush(Color.FromArgb(206, 216, 226));
        graphics.DrawString("吊射弹道校准", _smallHudFont, titleBrush, panel.X + 14, panel.Y + 10);
        string modeLine = _autoAimAssistMode == AutoAimAssistMode.GuidanceOnly
            ? "引导模式：只显示预测点和开火窗口，不接管云台、镜头或扳机。"
            : "强锁/部署：只有命中板面、中心误差与法线夹角同时满足时才自动开火。";

        bool trackingSubviewTarget = IsHeroLobSubviewTrackingTarget(shooter);
        if (!TryGetHeroLobCalibrationPreviewCached(shooter, out HeroLobCalibrationPreview preview, includeFireWindowSuggestion: true))
        {
            DrawHeroLobSubviewFrame(graphics, previewRect, hasPreview: false, locked: trackingSubviewTarget);
            graphics.DrawString(
                "右键锁定前哨站或基地装甲板后，这里会显示当前弹道的命中点或偏移量。",
                _tinyHudFont,
                textBrush,
                panel.X + 14,
                panel.Y + 38);
            graphics.DrawString(
                "普通模式仅处理 8m 内车体装甲板；吊射模式全距离处理前哨站/基地装甲板。",
                _tinyHudFont,
                textBrush,
                panel.X + 14,
                panel.Y + 58);
            graphics.DrawString(
                modeLine,
                _tinyHudFont,
                textBrush,
                panel.X + 14,
                panel.Y + 78);
            return;
        }

        DrawHeroLobSubviewFrame(graphics, previewRect, hasPreview: true, locked: trackingSubviewTarget);

        Color accentColor = preview.HitsPlate
            ? Color.FromArgb(92, 224, 144)
            : Color.FromArgb(255, 198, 96);
        using var accentBrush = new SolidBrush(accentColor);
        string summary = preview.HitsPlate
            ? $"命中 {preview.TargetId}/{preview.PlateId}"
            : $"未命中 {preview.TargetId}/{preview.PlateId}";
        graphics.DrawString(summary, _tinyHudFont, accentBrush, panel.X + 204, panel.Y + 40);
        string fireWindowText = preview.FireWindowReady
            ? "窗口：可发射"
            : "窗口：等待装甲板正面/中心窗口";
        graphics.DrawString(
            $"{fireWindowText}   法线夹角 {preview.NormalAngleDeg:0.0}° / 45°",
            _tinyHudFont,
            preview.FireWindowReady ? accentBrush : textBrush,
            panel.X + 204,
            panel.Y + 118);
        string fireAdvice = preview.FireWindowReady
            ? "建议：现在发射"
            : preview.HasSuggestedFireWindow
                ? $"建议：等待 {preview.SecondsToFireWindow:0.00}s 后再发射"
                : "建议：当前姿态 2.4s 内无合适窗口";
        graphics.DrawString(
            fireAdvice,
            _tinyHudFont,
            preview.FireWindowReady ? accentBrush : textBrush,
            panel.X + 204,
            panel.Y + 138);
        graphics.DrawString(
            modeLine,
            _tinyHudFont,
            textBrush,
            panel.X + 204,
            panel.Y + 158);
        if (preview.HitsPlate)
        {
            graphics.DrawString(
                $"命中点：右偏 {preview.HorizontalOffsetM:+0.000;-0.000;0.000}m   高偏 {preview.VerticalOffsetM:+0.000;-0.000;0.000}m",
                _tinyHudFont,
                textBrush,
                panel.X + 204,
                panel.Y + 62);
            graphics.DrawString(
                $"plate {preview.PlateWidthM:0.000}m x {preview.PlateHeightM:0.000}m",
                _tinyHudFont,
                textBrush,
                panel.X + 204,
                panel.Y + 82);
        }
        else
        {
            string missMode = preview.CrossedPlatePlane ? "穿过板平面但未落在板面内" : "未穿过板平面";
            graphics.DrawString(
                $"{missMode}   横偏 {preview.HorizontalOffsetM:+0.000;-0.000;0.000}m   高偏 {preview.VerticalOffsetM:+0.000;-0.000;0.000}m   深度 {preview.DepthOffsetM:+0.000;-0.000;0.000}m   总偏差 {preview.TotalOffsetM:0.000}m",
                _tinyHudFont,
                textBrush,
                new RectangleF(panel.X + 204, panel.Y + 58, panel.Width - 218, 42));
        }

        DrawHeroLobCalibrationWorldMarker(graphics, preview);
    }

    private void DrawHeroLobSubviewOverlay(Graphics graphics)
    {
        SimulationEntity? shooter = _host.SelectedEntity;
        if (shooter is null
            || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || !SimulationCombatMath.IsHeroLobAutoAimMode(shooter))
        {
            return;
        }

        Rectangle previewRect = GetHeroLobSubviewRect();
        bool trackingSubviewTarget = IsHeroLobSubviewTrackingTarget(shooter);
        DrawHeroLobSubviewFrame(graphics, previewRect, hasPreview: true, locked: trackingSubviewTarget);

        if (TryGetHeroLobCalibrationPreviewCached(shooter, out HeroLobCalibrationPreview preview, includeFireWindowSuggestion: true))
        {
            DrawHeroLobCalibrationWorldMarker(graphics, preview);
        }
    }

    private Rectangle GetHeroLobOverlayPanelRect()
    {
        Rectangle statusPanel = GetPlayerStatusPanelRect();
        int width = Math.Max(420, statusPanel.Width);
        int height = 252;
        int x = Math.Max(18, statusPanel.X);
        int y = Math.Max(18, statusPanel.Y - height - 10);
        return new Rectangle(x, y, width, height);
    }

    private Rectangle GetHeroLobSubviewRect()
    {
        Rectangle panel = GetHeroLobOverlayPanelRect();
        return new Rectangle(panel.X, panel.Y, panel.Width, panel.Height);
    }

    private void DrawHeroLobSubviewFrame(Graphics graphics, Rectangle rect, bool hasPreview, bool locked)
    {
        using var border = new Pen(
            hasPreview
                ? Color.FromArgb(locked ? 210 : 168, locked ? 96 : 118, locked ? 214 : 140, locked ? 255 : 164)
                : Color.FromArgb(150, 118, 140, 164),
            1.1f);
        graphics.DrawRectangle(border, rect);
    }

    private void DrawHeroLobSecondaryViewport(Graphics graphics)
    {
        if (UseGpuRenderer && !UseFastFlatRenderer)
        {
            return;
        }

        if (!TryResolveHeroLobSecondaryCamera(out Rectangle viewport, out Vector3 cameraPosition, out Vector3 cameraTarget, out Vector3 cameraUp, out float verticalFovRad))
        {
            return;
        }

        GraphicsState state = graphics.Save();
        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        bool previousSuppressLabels = _suppressEntityLabels;
        bool previousSuppressSelectedEntityModel = _suppressSelectedEntityModel;
        try
        {
            graphics.SetClip(viewport);
            _projectionViewportRect = viewport;
            _cameraPositionM = cameraPosition;
            _cameraTargetM = cameraTarget;
            _viewMatrix = Matrix4x4.CreateLookAt(cameraPosition, cameraTarget, cameraUp);
            float aspect = Math.Max(0.6f, viewport.Width / (float)Math.Max(1, viewport.Height));
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(verticalFovRad, aspect, 0.015f, 1500f);
            _suppressEntityLabels = true;
            _suppressSelectedEntityModel = true;
            DrawInMatchWorld(graphics);
        }
        finally
        {
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _suppressEntityLabels = previousSuppressLabels;
            _suppressSelectedEntityModel = previousSuppressSelectedEntityModel;
            graphics.Restore(state);
        }
    }

    private static bool IsHeroLobSubviewTrackingTarget(SimulationEntity shooter)
        => shooter.AutoAimLocked
            && IsHeroLobStructureTargetKind(shooter.AutoAimTargetKind)
            && !string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            && !string.IsNullOrWhiteSpace(shooter.AutoAimPlateId);

    private bool TryResolveHeroLobSecondaryCamera(
        out Rectangle viewport,
        out Vector3 cameraPosition,
        out Vector3 cameraTarget,
        out Vector3 cameraUp,
        out float verticalFovRad)
    {
        viewport = GetHeroLobSubviewRect();
        cameraPosition = Vector3.Zero;
        cameraTarget = Vector3.Zero;
        cameraUp = Vector3.UnitY;
        float defaultVerticalFovRad = MathF.Max(0.09f, FirstPersonVerticalFovRad / 3.0f);
        verticalFovRad = defaultVerticalFovRad;

        SimulationEntity? shooter = _host.SelectedEntity;
        if (shooter is null
            || !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || !SimulationCombatMath.IsHeroLobAutoAimMode(shooter))
        {
            return false;
        }

        RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(shooter);
        float yaw = ResolveEntityYaw(shooter);
        float turretYaw = (float)(shooter.TurretYawDeg * Math.PI / 180.0);
        float gimbalPitch = (float)(shooter.GimbalPitchDeg * Math.PI / 180.0);
        RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(shooter);
        ResolveChassisAxes(yaw, shooter.ChassisPitchDeg, shooter.ChassisRollDeg, out Vector3 chassisForward, out Vector3 chassisRight, out Vector3 chassisUp);
        ResolveMountedTurretAxes(
            chassisForward,
            chassisRight,
            chassisUp,
            turretYaw - yaw,
            gimbalPitch,
            out _,
            out Vector3 turretRight,
            out Vector3 pitchedForward,
            out Vector3 pitchedUp);

        float groundHeight = (float)(shooter.GroundHeightM + shooter.AirborneHeightM);
        float bodyTop = MathF.Max(0f, profile.BodyClearanceM + motion.BodyLiftM + profile.BodyHeightM);
        float turretBase = MathF.Max(
            bodyTop + profile.GimbalMountGapM + profile.GimbalMountHeightM,
            profile.GimbalHeightM - profile.GimbalBodyHeightM * 0.5f);
        float hingeBase = MathF.Max(
            bodyTop + profile.GimbalMountGapM + profile.GimbalMountHeightM,
            turretBase);
        Vector3 chassisOrigin = ToScenePoint(shooter.X, shooter.Y, groundHeight);
        Vector3 hingeCenter = OffsetScenePosition(
            chassisOrigin,
            profile.GimbalOffsetXM,
            profile.GimbalOffsetYM,
            hingeBase,
            chassisForward,
            chassisRight,
            chassisUp);
        Vector3 turretCenter = hingeCenter
            + pitchedUp * (profile.GimbalBodyHeightM * 0.50f + 0.006f)
            + pitchedForward * (profile.GimbalLengthM * 0.04f);
        ResolveHeroSubviewCameraMount(
            turretCenter,
            pitchedForward,
            turretRight,
            pitchedUp,
            profile.GimbalWidthM * profile.BodyRenderWidthScale,
            profile.GimbalBodyHeightM,
            out Vector3 subCameraCenter,
            out Vector3 subCameraForward,
            out _,
            out cameraUp,
            out _);

        cameraPosition = subCameraCenter + subCameraForward * 0.032f;
        bool lockTarget = IsHeroLobSubviewTrackingTarget(shooter);
        if (lockTarget
            && TryGetHeroLobCalibrationPreviewCached(shooter, out HeroLobCalibrationPreview preview, includeFireWindowSuggestion: false))
        {
            cameraTarget = preview.PlateCenterScene;
        }
        else if (lockTarget)
        {
            SimulationEntity? target = _host.World.Entities.FirstOrDefault(candidate =>
                candidate.IsAlive
                && string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
            double predictedPlateTimeSec = _host.World.GameTimeSec + Math.Max(0.0, shooter.AutoAimLeadTimeSec);
            if (target is not null
                && TryResolveVisualArmorPlatePose(target, shooter.AutoAimPlateId!, predictedPlateTimeSec, out VisualArmorPlatePose visualPlate))
            {
                cameraTarget = visualPlate.Center;
            }
        }
        else
        {
            Vector3 mainForward = _cameraTargetM - _cameraPositionM;
            if (mainForward.LengthSquared() <= 1e-6f)
            {
                mainForward = subCameraForward;
            }
            else
            {
                mainForward = Vector3.Normalize(mainForward);
            }

            cameraTarget = cameraPosition + mainForward * 40f;
        }

        if ((cameraTarget - cameraPosition).LengthSquared() <= 1e-6f)
        {
            cameraTarget = cameraPosition + subCameraForward * 40f;
        }

        if (_autoAimPressed
            && lockTarget
            && TryGetHeroLobCalibrationPreviewCached(shooter, out HeroLobCalibrationPreview activePreview, includeFireWindowSuggestion: false))
        {
            float adaptiveFov = ResolveHeroLobSubviewAdaptiveVerticalFov(
                activePreview.PlateWidthM,
                activePreview.PlateHeightM,
                viewport.Width,
                viewport.Height,
                cameraPosition,
                cameraTarget,
                defaultVerticalFovRad);
            verticalFovRad = MathF.Min(defaultVerticalFovRad, adaptiveFov);
        }

        return viewport.Width > 8 && viewport.Height > 8;
    }

    private static float ResolveHeroLobSubviewAdaptiveVerticalFov(
        float plateWidthM,
        float plateHeightM,
        int viewportWidth,
        int viewportHeight,
        Vector3 cameraPosition,
        Vector3 cameraTarget,
        float defaultVerticalFovRad)
    {
        float safeWidth = Math.Max(0.04f, plateWidthM);
        float safeHeight = Math.Max(0.04f, plateHeightM);
        float aspect = Math.Max(0.6f, viewportWidth / (float)Math.Max(1, viewportHeight));
        float plateAspect = safeWidth / Math.Max(0.02f, safeHeight);
        float targetAreaFraction = 0.15f;
        float heightFraction = MathF.Sqrt(targetAreaFraction * aspect / Math.Max(0.1f, plateAspect));
        heightFraction = Math.Clamp(heightFraction, 0.20f, 0.78f);

        float distance = Math.Max(0.10f, Vector3.Distance(cameraPosition, cameraTarget));
        float desiredVerticalFovRad = 2f * MathF.Atan(safeHeight / Math.Max(0.02f, 2f * distance * heightFraction));
        return Math.Clamp(desiredVerticalFovRad, 0.035f, defaultVerticalFovRad);
    }

    private void DrawHeroLobCalibrationWorldMarker(Graphics graphics, HeroLobCalibrationPreview preview)
    {
        if (!preview.HasImpactPoint
            || !TryProject(preview.ImpactScene, out PointF impactPoint, out _))
        {
            return;
        }

        using var shadowPen = new Pen(Color.FromArgb(160, 0, 0, 0), 3f);
        using var hitPen = new Pen(Color.FromArgb(238, 92, 224, 144), 1.8f);
        using var missPen = new Pen(Color.FromArgb(240, 255, 198, 96), 1.8f);
        using var labelBrush = new SolidBrush(preview.HitsPlate
            ? Color.FromArgb(235, 120, 236, 160)
            : Color.FromArgb(245, 255, 206, 112));
        float radius = preview.HitsPlate ? 9f : 8f;
        Pen activePen = preview.HitsPlate ? hitPen : missPen;
        graphics.DrawEllipse(shadowPen, impactPoint.X - radius, impactPoint.Y - radius, radius * 2f, radius * 2f);
        graphics.DrawEllipse(activePen, impactPoint.X - radius, impactPoint.Y - radius, radius * 2f, radius * 2f);
        graphics.DrawLine(activePen, impactPoint.X - 12f, impactPoint.Y, impactPoint.X + 12f, impactPoint.Y);
        graphics.DrawLine(activePen, impactPoint.X, impactPoint.Y - 12f, impactPoint.X, impactPoint.Y + 12f);

        if (preview.HitsPlate)
        {
            graphics.DrawString("命中点", _tinyHudFont, labelBrush, impactPoint.X + 12f, impactPoint.Y - 18f);
            return;
        }

        if (TryProject(preview.PlateCenterScene, out PointF plateCenterPoint, out _))
        {
            graphics.DrawLine(shadowPen, plateCenterPoint, impactPoint);
            graphics.DrawLine(missPen, plateCenterPoint, impactPoint);
        }

        graphics.DrawString($"偏移 {preview.TotalOffsetM:0.00}m", _tinyHudFont, labelBrush, impactPoint.X + 12f, impactPoint.Y - 18f);
    }

    private bool TryBuildHeroLobCalibrationPreview(
        SimulationEntity shooter,
        out HeroLobCalibrationPreview preview,
        bool includeFireWindowSuggestion = true)
    {
        preview = default;
        if (!shooter.IsAlive
            || !shooter.AutoAimLocked
            || string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId)
            || (!string.Equals(shooter.AutoAimTargetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(shooter.AutoAimTargetKind, "base_armor", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        SimulationEntity? target = _host.World.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        double predictedPlateTimeSec = _host.World.GameTimeSec + Math.Max(0.0, shooter.AutoAimLeadTimeSec);
        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        ArmorPlateTarget plate = SimulationCombatMath.GetAttackableArmorPlateTargets(target, metersPerWorldUnit, predictedPlateTimeSec)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, shooter.AutoAimPlateId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(plate.Id))
        {
            return false;
        }

        if (!TryResolveVisualArmorPlatePose(target, shooter.AutoAimPlateId, predictedPlateTimeSec, out VisualArmorPlatePose visualPlate))
        {
            return false;
        }

        Vector3 plateCenterScene = visualPlate.Center;
        Vector3 plateNormalScene = Vector3.Normalize(visualPlate.Forward);
        Vector3 plateRightScene = Vector3.Normalize(visualPlate.Right);
        if (plateRightScene.LengthSquared() <= 1e-8f)
        {
            plateRightScene = Vector3.UnitX;
        }

        float plateWidthM = (float)(plate.WidthM > 1e-6 ? plate.WidthM : plate.SideLengthM);
        float plateHeightM = (float)(plate.HeightSpanM > 1e-6 ? plate.HeightSpanM : plate.SideLengthM);
        if (!TrySimulateCurrentProjectileImpactPoint(shooter, plateCenterScene, plateNormalScene, out Vector3 impactScene, out bool crossedPlatePlane, out Vector3 impactDirectionScene))
        {
            return false;
        }

        Vector3 delta = impactScene - plateCenterScene;
        float horizontalOffsetM = Vector3.Dot(delta, plateRightScene);
        float verticalOffsetM = delta.Y;
        float depthOffsetM = Vector3.Dot(delta, plateNormalScene);
        float normalAngleDeg = 180f;
        if (impactDirectionScene.LengthSquared() > 1e-8f)
        {
            float frontDot = Vector3.Dot(-Vector3.Normalize(impactDirectionScene), plateNormalScene);
            normalAngleDeg = MathF.Acos(Math.Clamp(frontDot, -1f, 1f)) * 180f / MathF.PI;
        }

        bool hitsPlate = crossedPlatePlane
            && MathF.Abs(horizontalOffsetM) <= plateWidthM * 0.5f
            && MathF.Abs(verticalOffsetM) <= plateHeightM * 0.5f;
        bool fireWindowReady = hitsPlate
            && normalAngleDeg <= 45f
            && MathF.Abs(horizontalOffsetM) <= plateWidthM * 0.46f
            && MathF.Abs(verticalOffsetM) <= plateHeightM * 0.48f;
        bool hasSuggestedFireWindow = fireWindowReady;
        float secondsToFireWindow = 0f;
        if (!fireWindowReady && includeFireWindowSuggestion)
        {
            hasSuggestedFireWindow = TryEstimateHeroLobFireWindowDelay(
                shooter,
                target,
                shooter.AutoAimPlateId,
                plateWidthM,
                plateHeightM,
                out secondsToFireWindow);
        }

        preview = new HeroLobCalibrationPreview(
            target.Id,
            plate.Id,
            plateCenterScene,
            impactScene,
            HasImpactPoint: true,
            hitsPlate,
            crossedPlatePlane,
            horizontalOffsetM,
            verticalOffsetM,
            depthOffsetM,
            delta.Length(),
            normalAngleDeg,
            fireWindowReady,
            plateWidthM,
            plateHeightM,
            hasSuggestedFireWindow,
            secondsToFireWindow);
        return true;
    }

    private bool TryEstimateHeroLobFireWindowDelay(
        SimulationEntity shooter,
        SimulationEntity target,
        string plateId,
        float plateWidthM,
        float plateHeightM,
        out float secondsToFireWindow)
    {
        secondsToFireWindow = 0f;
        double leadTimeSec = Math.Clamp(shooter.AutoAimLeadTimeSec, 0.0, 2.35);
        const double horizonSec = 2.40;
        const double stepSec = 0.08;
        for (double waitSec = stepSec; waitSec <= horizonSec + 1e-6; waitSec += stepSec)
        {
            double plateTimeSec = _host.World.GameTimeSec + waitSec + leadTimeSec;
            if (!TryResolveVisualArmorPlatePose(target, plateId, plateTimeSec, out VisualArmorPlatePose visualPlate))
            {
                continue;
            }

            Vector3 normalScene = visualPlate.Forward.LengthSquared() <= 1e-8f
                ? Vector3.UnitX
                : Vector3.Normalize(visualPlate.Forward);
            if (!TrySimulateCurrentProjectileImpactPoint(
                    shooter,
                    visualPlate.Center,
                    normalScene,
                    out Vector3 impactScene,
                    out bool crossedPlatePlane,
                    out Vector3 impactDirectionScene))
            {
                continue;
            }

            Vector3 rightScene = visualPlate.Right.LengthSquared() <= 1e-8f
                ? Vector3.UnitX
                : Vector3.Normalize(visualPlate.Right);
            Vector3 delta = impactScene - visualPlate.Center;
            float horizontalOffsetM = Vector3.Dot(delta, rightScene);
            float verticalOffsetM = delta.Y;
            float normalAngleDeg = 180f;
            if (impactDirectionScene.LengthSquared() > 1e-8f)
            {
                float frontDot = Vector3.Dot(-Vector3.Normalize(impactDirectionScene), normalScene);
                normalAngleDeg = MathF.Acos(Math.Clamp(frontDot, -1f, 1f)) * 180f / MathF.PI;
            }

            if (crossedPlatePlane
                && normalAngleDeg <= 45f
                && MathF.Abs(horizontalOffsetM) <= plateWidthM * 0.46f
                && MathF.Abs(verticalOffsetM) <= plateHeightM * 0.48f)
            {
                secondsToFireWindow = (float)waitSec;
                return true;
            }
        }

        return false;
    }

    private bool TrySimulateCurrentProjectileImpactPoint(
        SimulationEntity shooter,
        Vector3 plateCenterScene,
        Vector3 plateNormalScene,
        out Vector3 impactScene,
        out bool crossedPlatePlane,
        out Vector3 impactDirectionScene)
    {
        crossedPlatePlane = false;
        impactDirectionScene = Vector3.UnitX;
        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double yawRad = shooter.TurretYawDeg * Math.PI / 180.0;
        double pitchRad = shooter.GimbalPitchDeg * Math.PI / 180.0;
        double speedMps = SimulationCombatMath.ProjectileSpeedMps(shooter);
        (double x, double y, double heightM) = SimulationCombatMath.ComputeMuzzlePoint(_host.World, shooter, shooter.GimbalPitchDeg);
        double inheritedVxWorldPerSec = shooter.HasObservedKinematics ? shooter.ObservedVelocityXWorldPerSec : shooter.VelocityXWorldPerSec;
        double inheritedVyWorldPerSec = shooter.HasObservedKinematics ? shooter.ObservedVelocityYWorldPerSec : shooter.VelocityYWorldPerSec;
        double vxMps = inheritedVxWorldPerSec * metersPerWorldUnit + Math.Cos(pitchRad) * Math.Cos(yawRad) * speedMps;
        double vyMps = inheritedVyWorldPerSec * metersPerWorldUnit + Math.Cos(pitchRad) * Math.Sin(yawRad) * speedMps;
        double vzMps = Math.Sin(pitchRad) * speedMps;
        RuntimeGridData? runtimeGrid = _host.RuntimeGrid;
        double dt = 0.010;
        double maxLifeSec = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 4.8 : 3.2;
        Vector3 previousScene = ToScenePoint(x, y, (float)heightM);

        for (double t = 0.0; t <= maxLifeSec; t += dt)
        {
            double prevX = x;
            double prevY = y;
            double prevHeightM = heightM;
            ApplyPredictedProjectileStep(shooter.AmmoType, metersPerWorldUnit, dt, ref x, ref y, ref heightM, ref vxMps, ref vyMps, ref vzMps);
            Vector3 currentScene = ToScenePoint(x, y, (float)heightM);

            if (TryIntersectSegmentWithPlane(previousScene, currentScene, plateCenterScene, plateNormalScene, out Vector3 planeImpactScene))
            {
                impactScene = planeImpactScene;
                crossedPlatePlane = true;
                Vector3 segment = currentScene - previousScene;
                impactDirectionScene = segment.LengthSquared() <= 1e-8f ? impactDirectionScene : Vector3.Normalize(segment);
                return true;
            }

            if (runtimeGrid is not null
                && runtimeGrid.IsValid
                && t > 0.05)
            {
                float terrainHeightM = runtimeGrid.SampleOcclusionHeight((float)x, (float)y);
                if (heightM <= terrainHeightM + 0.015)
                {
                    impactScene = ToScenePoint(x, y, terrainHeightM);
                    Vector3 segment = currentScene - previousScene;
                    impactDirectionScene = segment.LengthSquared() <= 1e-8f ? impactDirectionScene : Vector3.Normalize(segment);
                    return true;
                }
            }

            if (heightM < -0.05 || IsPredictedProjectileOutsideWorld(runtimeGrid, x, y))
            {
                impactScene = ToScenePoint(prevX, prevY, (float)Math.Max(prevHeightM, 0.0));
                Vector3 segment = currentScene - previousScene;
                impactDirectionScene = segment.LengthSquared() <= 1e-8f ? impactDirectionScene : Vector3.Normalize(segment);
                return true;
            }

            previousScene = currentScene;
        }

        impactScene = previousScene;
        return true;
    }

    private static bool TryIntersectSegmentWithPlane(
        Vector3 start,
        Vector3 end,
        Vector3 planePoint,
        Vector3 planeNormal,
        out Vector3 intersection)
    {
        intersection = default;
        float startDistance = Vector3.Dot(start - planePoint, planeNormal);
        float endDistance = Vector3.Dot(end - planePoint, planeNormal);
        float delta = startDistance - endDistance;
        if (MathF.Abs(delta) <= 1e-6f)
        {
            if (MathF.Abs(startDistance) > 1e-4f)
            {
                return false;
            }

            intersection = start;
            return true;
        }

        float t = startDistance / delta;
        if (t < 0f || t > 1f)
        {
            return false;
        }

        intersection = Vector3.Lerp(start, end, t);
        return true;
    }

    private static string ResolveSentryStanceLabel(string stance)
    {
        string normalized = RuleSet.NormalizeSentryStance(stance);
        if (string.Equals(normalized, "defense", StringComparison.OrdinalIgnoreCase))
        {
            return "\u9632\u5b88";
        }

        if (string.Equals(normalized, "patrol", StringComparison.OrdinalIgnoreCase))
        {
            return "\u5de1\u903b";
        }

        if (string.Equals(normalized, "attack", StringComparison.OrdinalIgnoreCase))
        {
            return "\u8fdb\u653b";
        }

        return normalized;
    }

    private static string ResolveAutoAimTargetTypeText(SimulationEntity entity)
    {
        if (string.Equals(entity.AutoAimTargetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase))
        {
            return "\u80fd\u91cf\u673a\u5173\u5706\u76d8";
        }

        if (string.Equals(entity.AutoAimTargetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase))
        {
            return "\u524d\u54e8\u7ad9\u88c5\u7532\u677f";
        }

        if (string.Equals(entity.AutoAimTargetKind, "base_armor", StringComparison.OrdinalIgnoreCase))
        {
            return "\u57fa\u5730\u88c5\u7532\u677f";
        }

        if (string.Equals(entity.AutoAimTargetKind, "vehicle_armor", StringComparison.OrdinalIgnoreCase))
        {
            return "\u8f66\u4f53\u88c5\u7532\u677f";
        }

        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && SimulationCombatMath.IsHeroLobAutoAimMode(entity))
        {
            return "\u524d\u54e8/\u57fa\u5730\u88c5\u7532";
        }

        return "\u88c5\u7532\u677f";
    }

    private static string ResolveAutoAimModeLabel(SimulationEntity entity)
    {
        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            if (entity.HeroDeploymentActive)
            {
                return "\u540a\u5c04(\u90e8\u7f72)";
            }

            return SimulationCombatMath.IsHeroLobAutoAimMode(entity)
                ? "\u540a\u5c04"
                : "\u6b63\u5e38";
        }

        return string.Equals(entity.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            ? "\u80fd\u91cf\u673a\u5173"
            : "\u88c5\u7532\u677f";
    }

    private (double SpeedMps, double XComponentMps, double YComponentMps) ResolveDisplayWorldVelocity(SimulationEntity entity)
    {
        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double vxMps = entity.VelocityXWorldPerSec * metersPerWorldUnit;
        double vyMps = entity.VelocityYWorldPerSec * metersPerWorldUnit;
        double speedMps = Math.Sqrt(vxMps * vxMps + vyMps * vyMps);
        double referenceYawRad = ResolveRedBaseForwardYawDeg() * Math.PI / 180.0;
        double worldYForwardX = Math.Cos(referenceYawRad);
        double worldYForwardY = Math.Sin(referenceYawRad);
        double worldXForwardX = -worldYForwardY;
        double worldXForwardY = worldYForwardX;
        double displayX = vxMps * worldXForwardX + vyMps * worldXForwardY;
        double displayY = vxMps * worldYForwardX + vyMps * worldYForwardY;
        return (speedMps, displayX, displayY);
    }

    private double ResolveDisplayWorldYawDeg(double yawDeg)
        => NormalizeCompassDeg(yawDeg - ResolveRedBaseForwardYawDeg());

    private double ResolveRedBaseForwardYawDeg()
    {
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entity.Team, "red", StringComparison.OrdinalIgnoreCase))
            {
                return entity.AngleDeg;
            }
        }

        return 0.0;
    }

    private double ResolveDisplayedFireRateHz(SimulationEntity entity)
    {
        if (!entity.IsAlive
            || entity.HeatLockTimerSec > 1e-6
            || entity.RespawnAmmoLockTimerSec > 1e-6
            || string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return 0.0;
        }

        double fireRate = Math.Max(0.5, _host.CombatFireRateHz);
        if (string.Equals(entity.AmmoType, "17mm", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            fireRate = Math.Max(fireRate, 20.0);
        }

        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)
            && string.Equals(RuleSet.NormalizeSentryControlMode(entity.SentryControlMode), "semi_auto", StringComparison.OrdinalIgnoreCase))
        {
            fireRate *= 0.55;
        }

        return fireRate;
    }

    private void DrawKeyGuideOverlay(Graphics graphics)
    {
        if (!_showKeyGuide || _appState != SimulatorAppState.InMatch)
        {
            return;
        }

        string[] lines =
        {
            "鼠标：移动视角 / 左键开火 / 右键自瞄；T 切换硬锁与引导点",
            "W A S D：底盘前左后右；Shift 小陀螺；Space 跳跃/越障",
            "Q：切换自瞄目标或英雄普通/吊射；Z 英雄部署；B 补弹；C 超级电容；X 哨兵形态",
            "F1 指挥模式；F2 观察者；F3 碰撞箱；F4 弹道轨迹；F5 关闭本说明",
            "F6 重载部署；F7 遥测；F8 自瞄解算/上次与下次弹道；F9 局内组合体编辑",
            "F9 编辑：Tab 选中；I/J/K/L 按组合体坐标系前左后右移动；. 上移；, 下移；Ctrl+S 保存",
            "PageUp/PageDown 切换地图；Esc/P 暂停；R 重新开始只在暂停菜单中生效",
        };

        int lineHeight = 20;
        int panelWidth = Math.Min(Math.Max(560, ClientSize.Width - 80), 760);
        int panelHeight = 52 + lineHeight * lines.Length;
        Rectangle panel = new(20, 78, panelWidth, panelHeight);
        using GraphicsPath path = CreateRoundedRectangle(panel, 10);
        using var fill = new SolidBrush(Color.FromArgb(172, 12, 18, 24));
        using var border = new Pen(Color.FromArgb(150, 120, 140, 160), 1f);
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248));
        using var textBrush = new SolidBrush(Color.FromArgb(208, 218, 228));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString("键位说明", _hudMidFont, titleBrush, panel.X + 14, panel.Y + 12);

        float y = panel.Y + 44;
        foreach (string line in lines)
        {
            graphics.DrawString(line, _smallHudFont, textBrush, panel.X + 16, y);
            y += lineHeight;
        }

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

    private List<BuffProgressEntry> CollectBuffProgressEntries(SimulationEntity entity)
    {
        var entries = new List<BuffProgressEntry>(8);
        AddTimedBuff(entries, "invincible", "invincible", "\u590d\u6d3b\u65e0\u654c", "\u514d\u75ab\u4f24\u5bb3", entity.RespawnInvincibleTimerSec, 30.0, Color.FromArgb(255, 222, 92), 999.0);
        AddTimedBuff(entries, "terrain_highland_def", "defense", "\u9ad8\u5730\u9632\u5fa1", "\u53d7\u5230\u4f24\u5bb3 x0.75", entity.TerrainHighlandDefenseTimerSec, 30.0, Color.FromArgb(104, 190, 255), 0.25);
        AddTimedBuff(entries, "terrain_fly_def", "defense", "\u98de\u5761\u9632\u5fa1", "\u53d7\u5230\u4f24\u5bb3 x0.75", entity.TerrainFlySlopeDefenseTimerSec, 30.0, Color.FromArgb(118, 210, 246), 0.25);
        AddTimedBuff(entries, "terrain_road_cool", "cooling", "\u516c\u8def\u51b7\u5374", "\u5c04\u51fb\u70ed\u91cf\u51b7\u5374 x2.00", entity.TerrainRoadCoolingTimerSec, 5.0, Color.FromArgb(105, 224, 160), 1.00);
        AddTimedBuff(entries, "terrain_slope_def", "defense", "\u96a7\u9053\u9632\u5fa1", "\u53d7\u5230\u4f24\u5bb3 x0.90", entity.TerrainSlopeDefenseTimerSec, 10.0, Color.FromArgb(150, 206, 255), 0.10);
        AddTimedBuff(entries, "terrain_slope_cool", "cooling", "\u96a7\u9053\u51b7\u5374", "\u5c04\u51fb\u70ed\u91cf\u51b7\u5374 x1.20", entity.TerrainSlopeCoolingTimerSec, 120.0, Color.FromArgb(94, 230, 176), 0.20);

        if (_host.World.Teams.TryGetValue(entity.Team, out SimulationTeamState? teamState)
            && teamState.EnergyBuffTimerSec > 1e-3)
        {
            double durationSec = teamState.EnergyLargeMechanismActive
                ? Math.Max(30.0, teamState.EnergyBuffTimerSec)
                : 20.0;
            if (teamState.EnergyBuffDamageTakenMult < 0.995)
            {
                AddTimedBuff(
                    entries,
                    teamState.EnergyLargeMechanismActive ? "energy_team_large_defense" : "energy_team_small_defense",
                    "defense",
                    teamState.EnergyLargeMechanismActive ? "\u5927\u80fd\u91cf\u9632\u5fa1" : "\u5c0f\u80fd\u91cf\u9632\u5fa1",
                    $"\u53d7\u5230\u4f24\u5bb3 x{teamState.EnergyBuffDamageTakenMult:0.00}",
                    teamState.EnergyBuffTimerSec,
                    durationSec,
                    Color.FromArgb(255, 174, 82),
                    Math.Max(0.0, 1.0 - teamState.EnergyBuffDamageTakenMult));
            }

            if (teamState.EnergyBuffDamageDealtMult > 1.005)
            {
                AddTimedBuff(
                    entries,
                    "energy_team_large_damage",
                    "damage",
                    "\u5927\u80fd\u91cf\u653b\u51fb",
                    $"\u9020\u6210\u4f24\u5bb3 x{teamState.EnergyBuffDamageDealtMult:0.00}",
                    teamState.EnergyBuffTimerSec,
                    durationSec,
                    Color.FromArgb(255, 126, 96),
                    Math.Max(0.0, teamState.EnergyBuffDamageDealtMult - 1.0));
            }

            if (teamState.EnergyBuffCoolingMult > 1.005)
            {
                AddTimedBuff(
                    entries,
                    "energy_team_large_cooling",
                    "cooling",
                    "\u5927\u80fd\u91cf\u51b7\u5374",
                    $"\u70ed\u91cf\u51b7\u5374 x{teamState.EnergyBuffCoolingMult:0.00}",
                    teamState.EnergyBuffTimerSec,
                    durationSec,
                    Color.FromArgb(98, 224, 174),
                    Math.Max(0.0, teamState.EnergyBuffCoolingMult - 1.0));
            }
        }

        if (entity.DynamicDamageTakenMult < 0.995)
        {
            AddOrMergeBuff(entries, new BuffProgressEntry(
                $"defense_{entity.DynamicDamageTakenMult:0.00}",
                "defense",
                "\u9632\u5fa1\u589e\u76ca",
                $"\u53d7\u5230\u4f24\u5bb3 x{entity.DynamicDamageTakenMult:0.00}",
                0.0,
                0.0,
                Color.FromArgb(115, 196, 255),
                false,
                Math.Max(0.0, 1.0 - entity.DynamicDamageTakenMult)));
        }

        if (entity.DynamicDamageDealtMult > 1.005)
        {
            AddOrMergeBuff(entries, new BuffProgressEntry(
                $"damage_{entity.DynamicDamageDealtMult:0.00}",
                "damage",
                "\u653b\u51fb\u589e\u76ca",
                $"\u9020\u6210\u4f24\u5bb3 x{entity.DynamicDamageDealtMult:0.00}",
                0.0,
                0.0,
                Color.FromArgb(255, 112, 96),
                false,
                Math.Max(0.0, entity.DynamicDamageDealtMult - 1.0)));
        }

        if (entity.DynamicCoolingMult > 1.005)
        {
            AddOrMergeBuff(entries, new BuffProgressEntry(
                $"cooling_{entity.DynamicCoolingMult:0.00}",
                "cooling",
                "\u51b7\u5374\u589e\u76ca",
                $"\u70ed\u91cf\u51b7\u5374 x{entity.DynamicCoolingMult:0.00}",
                0.0,
                0.0,
                Color.FromArgb(98, 224, 174),
                false,
                Math.Max(0.0, entity.DynamicCoolingMult - 1.0)));
        }

        if (entity.DynamicPowerRecoveryMult > 1.005)
        {
            AddOrMergeBuff(entries, new BuffProgressEntry(
                $"power_recovery_{entity.DynamicPowerRecoveryMult:0.00}",
                "power",
                "\u56de\u80fd\u589e\u76ca",
                $"\u5e95\u76d8\u56de\u80fd x{entity.DynamicPowerRecoveryMult:0.00}",
                0.0,
                0.0,
                Color.FromArgb(244, 214, 96),
                false,
                Math.Max(0.0, entity.DynamicPowerRecoveryMult - 1.0)));
        }

        return entries;
    }

    private static void AddTimedBuff(
        List<BuffProgressEntry> entries,
        string key,
        string category,
        string name,
        string effect,
        double remainingSec,
        double durationSec,
        Color color,
        double magnitude)
    {
        if (remainingSec <= 1e-3)
        {
            return;
        }

        AddOrMergeBuff(entries, new BuffProgressEntry(
            key,
            category,
            name,
            effect,
            Math.Max(0.0, remainingSec),
            Math.Max(remainingSec, durationSec),
            color,
            true,
            magnitude));
    }

    private static void AddOrMergeBuff(List<BuffProgressEntry> entries, BuffProgressEntry candidate)
    {
        for (int index = 0; index < entries.Count; index++)
        {
            BuffProgressEntry current = entries[index];
            if (!string.Equals(current.Category, candidate.Category, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool replace =
                candidate.Magnitude > current.Magnitude + 1e-6
                || (Math.Abs(candidate.Magnitude - current.Magnitude) <= 1e-6
                    && candidate.RemainingSec > current.RemainingSec + 1e-6);
            if (replace)
            {
                entries[index] = candidate;
            }

            return;
        }

        entries.Add(candidate);
    }

    private void UpdateSelectedBuffNotifications()
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || _appState != SimulatorAppState.InMatch)
        {
            _selectedBuffSnapshot.Clear();
            _selectedBuffSnapshotEntityId = null;
            return;
        }

        if (!string.Equals(_selectedBuffSnapshotEntityId, entity.Id, StringComparison.OrdinalIgnoreCase))
        {
            _selectedBuffSnapshot.Clear();
            _selectedBuffSnapshotEntityId = entity.Id;
        }

        List<BuffProgressEntry> entries = CollectBuffProgressEntries(entity);
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BuffProgressEntry entry in entries)
        {
            currentKeys.Add(entry.Key);
            if (_selectedBuffSnapshot.ContainsKey(entry.Key))
            {
                continue;
            }

            if (!entry.Timed)
            {
                continue;
            }

            if (TryBuildFacilityBuffToast(entity, entry, out CenterBuffToast? facilityToast))
            {
                _centerBuffToasts.Add(facilityToast!);
            }
            else
            {
                string durationText = entry.Timed
                    ? $"\u6301\u7eed {entry.RemainingSec:0}\u79d2"
                    : "\u533a\u57df\u5185\u6301\u7eed";
                _centerBuffToasts.Add(new CenterBuffToast(
                    "\u83b7\u5f97\u589e\u76ca",
                    $"{entry.Name}  {durationText}",
                    entry.Color));
            }
        }

        foreach (string oldKey in _selectedBuffSnapshot.Keys.ToArray())
        {
            if (!currentKeys.Contains(oldKey))
            {
                _selectedBuffSnapshot.Remove(oldKey);
            }
        }

        foreach (BuffProgressEntry entry in entries)
        {
            _selectedBuffSnapshot[entry.Key] = entry.RemainingSec;
        }

        if (_centerBuffToasts.Count > 5)
        {
            _centerBuffToasts.RemoveRange(0, _centerBuffToasts.Count - 5);
        }
    }

    private bool TryBuildFacilityBuffToast(SimulationEntity entity, BuffProgressEntry entry, out CenterBuffToast? toast)
    {
        toast = null;
        Simulator.Core.Map.FacilityRegion? region = ResolveFacilityToastRegion(entity, entry.Key);
        if (region is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(region.Team)
            && !string.Equals(region.Team, "neutral", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(region.Team, _host.SelectedTeam, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string facilityText = ResolveFacilityToastLabel(region.Type);
        toast = new CenterBuffToast(
            "\u83b7\u5f97\u573a\u5730\u589e\u76ca",
            $"{facilityText}  {entry.Name}  {entry.RemainingSec:0}\u79d2",
            entry.Color);
        return true;
    }

    private Simulator.Core.Map.FacilityRegion? ResolveFacilityToastRegion(SimulationEntity entity, string entryKey)
    {
        foreach (Simulator.Core.Map.FacilityRegion region in _host.MapPreset.Facilities)
        {
            if (!region.Contains(entity.X, entity.Y))
            {
                continue;
            }

            string type = region.Type ?? string.Empty;
            if (entryKey.Equals("terrain_highland_def", StringComparison.OrdinalIgnoreCase)
                && (type.Equals("buff_trapezoid_highland", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("buff_central_highland", StringComparison.OrdinalIgnoreCase)))
            {
                return region;
            }

            if (entryKey.Equals("terrain_fly_def", StringComparison.OrdinalIgnoreCase)
                && type.Equals("buff_terrain_fly_slope", StringComparison.OrdinalIgnoreCase))
            {
                return region;
            }

            if (entryKey.Equals("terrain_road_cool", StringComparison.OrdinalIgnoreCase)
                && type.Equals("buff_terrain_road", StringComparison.OrdinalIgnoreCase))
            {
                return region;
            }

            if ((entryKey.Equals("terrain_slope_def", StringComparison.OrdinalIgnoreCase)
                    || entryKey.Equals("terrain_slope_cool", StringComparison.OrdinalIgnoreCase))
                && type.Equals("buff_terrain_slope", StringComparison.OrdinalIgnoreCase))
            {
                return region;
            }

            if (entryKey.StartsWith("defense_", StringComparison.OrdinalIgnoreCase)
                && (type.Equals("buff_base", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("buff_outpost", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("buff_fort", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("buff_hero_deployment", StringComparison.OrdinalIgnoreCase)))
            {
                return region;
            }

            if (entryKey.StartsWith("damage_", StringComparison.OrdinalIgnoreCase)
                && type.Equals("buff_hero_deployment", StringComparison.OrdinalIgnoreCase))
            {
                return region;
            }

            if (entryKey.StartsWith("cooling_", StringComparison.OrdinalIgnoreCase)
                && type.Equals("buff_fort", StringComparison.OrdinalIgnoreCase))
            {
                return region;
            }
        }

        return null;
    }

    private static string ResolveFacilityOwnerText(string facilityTeam, string entityTeam)
    {
        if (string.Equals(facilityTeam, "neutral", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(facilityTeam))
        {
            return "\u4e2d\u7acb";
        }

        return string.Equals(facilityTeam, entityTeam, StringComparison.OrdinalIgnoreCase)
            ? "\u5df1\u65b9"
            : "\u654c\u65b9";
    }

    private static string ResolveFacilityToastLabel(string facilityType)
    {
        string type = facilityType ?? string.Empty;
        return type switch
        {
            "buff_trapezoid_highland" => "\u68af\u5f62\u9ad8\u5730",
            "buff_central_highland" => "\u4e2d\u592e\u9ad8\u5730",
            "buff_terrain_fly_slope" => "\u98de\u5761",
            "buff_terrain_road" => "\u516c\u8def",
            "buff_terrain_slope" => "\u659c\u9762",
            "buff_base" => "\u57fa\u5730\u589e\u76ca\u533a",
            "buff_outpost" => "\u524d\u54e8\u7ad9\u589e\u76ca\u533a",
            "buff_fort" => "\u57ce\u5821\u589e\u76ca\u533a",
            "buff_hero_deployment" => "\u82f1\u96c4\u90e8\u7f72\u533a",
            _ => "\u573a\u5730\u589e\u76ca",
        };
    }

    private void AdvanceBuffToasts(float deltaSec)
    {
        for (int index = _centerBuffToasts.Count - 1; index >= 0; index--)
        {
            CenterBuffToast toast = _centerBuffToasts[index];
            toast.AgeSec += Math.Max(0f, deltaSec);
            if (toast.AgeSec >= toast.LifetimeSec)
            {
                _centerBuffToasts.RemoveAt(index);
            }
        }
    }

    private void DrawCenterBuffToasts(Graphics graphics)
    {
        if (_centerBuffToasts.Count == 0)
        {
            return;
        }

        int shown = Math.Min(1, _centerBuffToasts.Count);
        float y = ClientSize.Height * 0.30f;
        for (int i = 0; i < shown; i++)
        {
            CenterBuffToast toast = _centerBuffToasts[_centerBuffToasts.Count - 1 - i];
            float fadeIn = Math.Clamp(toast.AgeSec / 0.22f, 0f, 1f);
            float fadeOut = Math.Clamp((toast.LifetimeSec - toast.AgeSec) / 0.55f, 0f, 1f);
            int alpha = (int)(235f * Math.Min(fadeIn, fadeOut));
            if (alpha <= 0)
            {
                continue;
            }

            SizeF titleSize = graphics.MeasureString(toast.Title, _hudMidFont);
            SizeF detailSize = graphics.MeasureString(toast.Detail, _smallHudFont);
            float width = Math.Clamp(Math.Max(titleSize.Width, detailSize.Width) + 42f, 260f, 420f);
            RectangleF rect = new(ClientSize.Width - width - 28f, y + i * 58f, width, 46f);
            using GraphicsPath path = CreateRoundedRectangle(Rectangle.Round(rect), 8);
            using var fill = new SolidBrush(Color.FromArgb((int)(alpha * 0.82f), 10, 16, 24));
            using var border = new Pen(Color.FromArgb(alpha, toast.Color), 1.2f);
            using var titleBrush = new SolidBrush(Color.FromArgb(alpha, toast.Color));
            using var detailBrush = new SolidBrush(Color.FromArgb((int)(alpha * 0.88f), 230, 238, 245));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            graphics.DrawString(toast.Title, _hudMidFont, titleBrush, rect.X + 18f, rect.Y + 5f);
            graphics.DrawString(toast.Detail, _smallHudFont, detailBrush, rect.X + 18f, rect.Y + 25f);
        }
    }

    private void DrawBuffProgressOverlay(Graphics graphics)
    {
        SimulationEntity? entity = _host.SelectedEntity;
        if (entity is null || _appState != SimulatorAppState.InMatch)
        {
            return;
        }

        List<BuffProgressEntry> entries = CollectBuffProgressEntries(entity)
            .Where(entry => entry.Timed && entry.DurationSec > 1e-3)
            .Take(5)
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        int width = 282;
        int rowHeight = 20;
        int height = 22 + entries.Count * rowHeight;
        Rectangle panel = new(ClientSize.Width - width - 30, ClientSize.Height - 196 - height, width, height);
        using GraphicsPath path = CreateRoundedRectangle(panel, 8);
        using var fill = new SolidBrush(Color.FromArgb(205, 10, 16, 24));
        using var border = new Pen(Color.FromArgb(120, 140, 160, 180), 1f);
        using var textBrush = new SolidBrush(Color.FromArgb(226, 236, 242, 248));
        using var subBrush = new SolidBrush(Color.FromArgb(178, 202, 212, 224));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        graphics.DrawString("BUFF", _smallHudFont, textBrush, panel.X + 10, panel.Y + 5);

        for (int index = 0; index < entries.Count; index++)
        {
            BuffProgressEntry entry = entries[index];
            float y = panel.Y + 24 + index * rowHeight;
            float ratio = (float)Math.Clamp(entry.RemainingSec / Math.Max(entry.DurationSec, 1e-6), 0.0, 1.0);
            RectangleF bar = new(panel.X + 84, y + 5, panel.Width - 102, 8);
            using var back = new SolidBrush(Color.FromArgb(116, 42, 50, 62));
            using var front = new SolidBrush(Color.FromArgb(230, entry.Color));
            using var barBorder = new Pen(Color.FromArgb(126, 170, 186, 204), 1f);
            graphics.DrawString(entry.Name, _tinyHudFont, subBrush, panel.X + 10, y - 1);
            graphics.FillRectangle(back, bar);
            graphics.FillRectangle(front, bar.X, bar.Y, bar.Width * ratio, bar.Height);
            graphics.DrawRectangle(barBorder, bar.X, bar.Y, bar.Width, bar.Height);
            graphics.DrawString($"{entry.RemainingSec:0}s", _tinyHudFont, textBrush, bar.Right - 26, y - 1);
        }
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

        Rectangle panel = new(ClientSize.Width - 190, ClientSize.Height - 142, 158, 88);
        using GraphicsPath path = CreateRoundedRectangle(panel, 10);
        using var fill = new SolidBrush(Color.FromArgb(214, 10, 16, 24));
        using var border = new Pen(Color.FromArgb(150, 118, 136, 156), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var textBrush = new SolidBrush(Color.FromArgb(230, 236, 241, 245));
        using var subTextBrush = new SolidBrush(Color.FromArgb(196, 198, 207, 216));
        graphics.DrawString(_firstPersonView ? "First Person" : "Third Person", _smallHudFont, textBrush, panel.X + 12, panel.Y + 8);
        double bodyWorldYawDeg = ResolveDisplayWorldYawDeg(entity.AngleDeg);
        double turretWorldYawDeg = ResolveDisplayWorldYawDeg(entity.TurretYawDeg);

        PointF center = new(panel.X + 58, panel.Y + 52);
        using var axisPen = new Pen(Color.FromArgb(96, 154, 170, 188), 1f);
        graphics.DrawLine(axisPen, center.X - 42f, center.Y, center.X + 42f, center.Y);
        graphics.DrawLine(axisPen, center.X, center.Y - 24f, center.X, center.Y + 24f);

        DrawRotatedHudRectangle(
            graphics,
            center,
            58f,
            24f,
            (float)(bodyWorldYawDeg * Math.PI / 180.0),
            Color.FromArgb(190, 68, 82, 98),
            Color.FromArgb(235, 222, 230, 238));
        DrawHeadingNeedle(
            graphics,
            center,
            34f,
            (float)(bodyWorldYawDeg * Math.PI / 180.0),
            Color.FromArgb(205, 198, 207, 216));
        DrawRotatedHudRectangle(
            graphics,
            center,
            46f,
            13f,
            (float)(turretWorldYawDeg * Math.PI / 180.0),
            Color.FromArgb(220, ResolveTeamColor(entity.Team)),
            Color.FromArgb(250, 245, 232, 132));
        DrawHeadingNeedle(
            graphics,
            center,
            31f,
            (float)(turretWorldYawDeg * Math.PI / 180.0),
            Color.FromArgb(255, 255, 230, 92));

        graphics.DrawString($"Yaw {bodyWorldYawDeg:0}\u00b0", _tinyHudFont, subTextBrush, panel.X + 104, panel.Y + 30);
        graphics.DrawString($"Tur {turretWorldYawDeg:0}\u00b0", _tinyHudFont, textBrush, panel.X + 104, panel.Y + 46);
        graphics.DrawString($"P {entity.GimbalPitchDeg:+0;-0;0}\u00b0", _tinyHudFont, subTextBrush, panel.X + 112, panel.Y + 64);
    }

    private static void DrawRotatedHudRectangle(
        Graphics graphics,
        PointF center,
        float width,
        float height,
        float yawRad,
        Color fillColor,
        Color edgeColor)
    {
        float c = MathF.Cos(yawRad);
        float s = MathF.Sin(yawRad);
        PointF Transform(float x, float y) => new(
            center.X + x * c - y * s,
            center.Y + x * s + y * c);
        PointF[] points =
        {
            Transform(-width * 0.5f, -height * 0.5f),
            Transform(width * 0.5f, -height * 0.5f),
            Transform(width * 0.5f, height * 0.5f),
            Transform(-width * 0.5f, height * 0.5f),
        };
        using var fill = new SolidBrush(fillColor);
        using var edge = new Pen(edgeColor, 1.2f);
        graphics.FillPolygon(fill, points);
        graphics.DrawPolygon(edge, points);
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

        EnsureTerrainLayerBitmapCache();
        if (_cachedTerrainLayerBitmap is not null)
        {
            graphics.DrawImageUnscaled(_cachedTerrainLayerBitmap, 0, 0);
            return;
        }

        EnsureProjectedTerrainFaceCache();
        DrawProjectedFaceBatch(graphics, _cachedProjectedTerrainFaces, 1f);
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
        float positionToleranceSq = _firstPersonView ? 0.0009f : 0.150f;
        float targetToleranceSq = _firstPersonView ? 0.0016f : 0.180f;
        float directionDotTolerance = _firstPersonView ? 0.99985f : 0.9975f;
        float angleTolerance = _firstPersonView ? 0.0012f : 0.010f;
        float distanceTolerance = _firstPersonView ? 0.01f : 0.16f;
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
        float positionToleranceSq = _firstPersonView ? 0.040f : 0.260f;
        float targetToleranceSq = _firstPersonView ? 0.060f : 0.320f;
        float directionDotTolerance = _firstPersonView ? 0.9988f : 0.9968f;
        bool bitmapStable =
            _cachedTerrainLayerBitmap is not null
            && _terrainLayerBitmapBuiltVersion == _terrainProjectionBuiltVersion
            && _terrainLayerBitmapClientSize == ClientSize
            && Vector3.DistanceSquared(_terrainLayerBitmapCameraPosition, _cameraPositionM) <= positionToleranceSq
            && Vector3.DistanceSquared(_terrainLayerBitmapCameraTarget, _cameraTargetM) <= targetToleranceSq
            && Vector3.Dot(_terrainLayerBitmapViewDirection, currentViewDirection) >= directionDotTolerance;
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
            int faceBudget = UseGpuRenderer ? 5200 : 3400;
            foreach (ProjectedFace face in source)
            {
                target.Add(ApplyFarTerrainColorLod(face));
                if (target.Count >= faceBudget)
                {
                    break;
                }
            }

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
        Vector3 toFace = face.CenterScene - _cameraPositionM;
        float distance = toFace.Length();
        if (!_firstPersonView)
        {
            if (distance <= Math.Max(18f, _cameraDistanceM * 1.25f))
            {
                return false;
            }

            Vector3 thirdPersonDirection = ResolveTerrainProjectionViewDirection();
            if (distance > 1e-4f)
            {
                float forwardDot = Vector3.Dot(toFace / distance, thirdPersonDirection);
                if (distance > 28f && forwardDot < -0.18f)
                {
                    return true;
                }

                if (distance > 42f && forwardDot < 0.05f)
                {
                    return true;
                }
            }

            return false;
        }

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
            float rebuildMeters = UseGpuRenderer ? 0.60f : 0.18f;
            rebuildThresholdX = Math.Max(6, (int)MathF.Ceiling(rebuildMeters / Math.Max(_cachedRuntimeGrid.CellWidthWorld * metersPerWorldUnit, 1e-4f)));
            rebuildThresholdY = Math.Max(6, (int)MathF.Ceiling(rebuildMeters / Math.Max(_cachedRuntimeGrid.CellHeightWorld * metersPerWorldUnit, 1e-4f)));
        }
        else
        {
            bool relaxedGpuThreshold = UseGpuRenderer && !_firstPersonView;
            rebuildThresholdX = relaxedGpuThreshold ? Math.Max(6, radiusCellsX / 2) : Math.Max(3, radiusCellsX / 3);
            rebuildThresholdY = relaxedGpuThreshold ? Math.Max(6, radiusCellsY / 2) : Math.Max(3, radiusCellsY / 3);
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
            && (_frameClock.ElapsedTicks - _lastTerrainDetailRebuildTicks) / (double)Stopwatch.Frequency < (UseGpuRenderer ? 0.120 : 0.055))
        {
            return;
        }

        if (!force
            && !_firstPersonView
            && _terrainDetailFaces.Count > 0
            && _lastTerrainDetailRebuildTicks > 0
            && (_frameClock.ElapsedTicks - _lastTerrainDetailRebuildTicks) / (double)Stopwatch.Frequency < (UseGpuRenderer ? 0.32 : 0.18))
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
        AppendTerrainFacetFaces(_terrainDetailFaces, _terrainDetailMinXWorld, _terrainDetailMaxXWorld, _terrainDetailMinYWorld, _terrainDetailMaxYWorld);
        _lastTerrainDetailRebuildTicks = _frameClock.ElapsedTicks;
        if (!UseGpuRenderer || UseFastFlatRenderer)
        {
            _terrainProjectionCacheVersion++;
            _terrainProjectionBuiltVersion = -1;
        }
    }

    private static int ResolveTerrainDetailStep(RuntimeGridData runtimeGr