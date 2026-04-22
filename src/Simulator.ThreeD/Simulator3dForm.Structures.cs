using System.Numerics;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const float OutpostBaseWidthM = 0.65f;
    private const float OutpostBaseLiftM = 0.40f;
    private const float OutpostTopDiameterM = 0.55f;
    private const float OutpostTowerRadiusM = 0.20f;
    private const float OutpostBodyTopHeightM = 1.216f;
    private const float OutpostHeadBaseHeightM = 1.318f;
    private const float OutpostTowerHeightM = 1.578f;
    private const float OutpostLowerShoulderHeightM = 0.571f;
    private const float OutpostUpperShoulderHeightM = 1.446f;
    private const float OutpostRingSpeedRadPerSec = MathF.PI * 0.8f;
    private const float BaseDiagramLengthM = 1.881f;
    private const float BaseDiagramWidthM = 1.609f;
    private const float BaseDiagramHeightM = 1.181f;
    private const float BaseDetectorWidthM = 0.980f;
    private const float BaseDetectorHeightM = 0.095f;
    private const float BaseCoreColumnHeightM = 0.783f;
    private const float BaseShoulderHeightM = 0.860f;
    private const float BaseDetectorBridgeCenterHeightM = 1.093f;
    private const float BaseDetectorSensorCenterHeightM = 1.136f;
    private const float BaseTopArmorCenterHeightM = 1.150f;
    private const float BaseArmorOpenThresholdHealth = 2000f;
    private const float BaseTopArmorSlideAmplitudeM = 0.34f;
    private const float BaseTopArmorSlideSpeedRadPerSec = MathF.PI * 0.7f;
    private const float BaseTopArmorTiltDeg = 27.5f;
    private const float StructureArmorPlateSideM = 0.13f;
    private const float StructureArmorPlateThicknessM = 0.025f;

    private enum StructureRenderPass
    {
        Full,
        StaticBody,
        DynamicArmor,
    }

    private float DrawOutpostModel(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile,
        StructureRenderPass renderPass = StructureRenderPass.Full)
    {
        float baseLiftM = Math.Clamp((float)(entity.StructureBaseLiftM > 1e-6 ? entity.StructureBaseLiftM : profile.StructureBaseLiftM), 0f, 1.20f);
        if (baseLiftM <= 1e-6f)
        {
            baseLiftM = OutpostBaseLiftM;
        }

        center += Vector3.UnitY * baseLiftM;
        float yaw = (float)(entity.AngleDeg * Math.PI / 180.0);
        Color teamColor = ResolveTeamColor(entity.Team);
        Color bodyColor = ResolveDeepGrayMaterial(profile.BodyColor, entity.IsAlive, 0.00f);
        Color edgeColor = Color.FromArgb(entity.IsAlive ? 252 : 216, BlendColor(bodyColor, Color.Black, 0.16f));
        Color darkBody = ResolveDeepGrayMaterial(profile.BodyColor, entity.IsAlive, -0.06f);
        Color capColor = ResolveDeepGrayMaterial(profile.TurretColor, entity.IsAlive, 0.04f);
        float towerHeight = Math.Clamp(profile.BodyHeightM, 1.00f, 2.40f);
        float bodyTopHeight = Math.Clamp(profile.StructureBodyTopHeightM, 0.12f, towerHeight);
        float lowerShoulderHeight = Math.Clamp(profile.StructureLowerShoulderHeightM, 0.08f, bodyTopHeight);
        float upperShoulderHeight = Math.Clamp(profile.StructureUpperShoulderHeightM, bodyTopHeight, towerHeight);
        float baseWidth = Math.Clamp(profile.BodyLengthM, 0.40f, 0.95f);
        float topDiameter = Math.Clamp(profile.BodyWidthM, 0.28f, 0.80f);
        float towerRadius = Math.Clamp(profile.StructureTowerRadiusM > 1e-4f ? profile.StructureTowerRadiusM : topDiameter * 0.36f, 0.12f, 0.34f);
        float headBaseHeight = Math.Clamp(profile.StructureHeadBaseHeightM, upperShoulderHeight, towerHeight + 0.10f);

        if (renderPass != StructureRenderPass.DynamicArmor)
        {
            IReadOnlyList<Vector3> baseFootprint = BuildOrientedRectFootprint(
                center,
                baseWidth,
                baseWidth,
                0f,
                yaw + MathF.PI * 0.25f);
            DrawPrismWireframe(
                graphics,
                baseFootprint,
                0.085f,
                Color.FromArgb(255, BlendColor(bodyColor, Color.Black, 0.08f)),
                edgeColor,
                null);

            DrawTaperedOutpostSection(
                graphics,
                center,
                yaw,
                baseWidth * 0.46f,
                0.255f,
                0.085f,
                lowerShoulderHeight,
                Color.FromArgb(255, bodyColor),
                edgeColor);

            DrawTaperedOutpostSection(
                graphics,
                center,
                yaw + MathF.PI / 8f,
                0.205f,
                0.175f,
                lowerShoulderHeight,
                bodyTopHeight,
                Color.FromArgb(255, darkBody),
                Color.FromArgb(entity.IsAlive ? 248 : 216, BlendColor(darkBody, Color.Black, 0.22f)));

            DrawTaperedOutpostSection(
                graphics,
                center,
                yaw,
                0.220f,
                0.165f,
                bodyTopHeight,
                upperShoulderHeight,
                Color.FromArgb(255, capColor),
                edgeColor);

            DrawTaperedOutpostSection(
                graphics,
                center,
                yaw + MathF.PI / 8f,
                0.165f,
                0.120f,
                upperShoulderHeight,
                headBaseHeight,
                Color.FromArgb(255, BlendColor(capColor, Color.White, 0.06f)),
                edgeColor);

            DrawOutpostHead(graphics, center, yaw, towerHeight, headBaseHeight, entity, profile, capColor, edgeColor);
            Vector3 forward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
            Vector3 side = new(-forward.Z, 0f, forward.X);
            DrawOutpostRibs(graphics, center, yaw, bodyColor, edgeColor);
            DrawStructureHealthLightBar(
                graphics,
                entity,
                OffsetScenePosition(center, 0.285f, 0f, yaw, 0.62f),
                Vector3.UnitY,
                forward,
                side,
                0.68f,
                0.030f,
                0.110f);
        }

        if (renderPass != StructureRenderPass.StaticBody)
        {
            float ringYaw = (float)(entity.AngleDeg * Math.PI / 180.0)
                + (entity.IsAlive ? (float)_host.World.GameTimeSec * OutpostRingSpeedRadPerSec : 0f);
            DrawCylinderSolid(
                graphics,
                center + new Vector3(0f, headBaseHeight - 0.07f, 0f),
                Vector3.UnitY,
                new Vector3(MathF.Cos(ringYaw), 0f, MathF.Sin(ringYaw)),
                towerRadius + 0.055f,
                0.030f,
                ringYaw,
                Color.FromArgb(255, BlendColor(bodyColor, Color.White, 0.08f)),
                Color.FromArgb(252, BlendColor(teamColor, Color.Black, 0.18f)),
                24);

            DrawCylinderSolid(
                graphics,
                center + new Vector3(0f, headBaseHeight - 0.07f, 0f),
                Vector3.UnitY,
                new Vector3(MathF.Cos(ringYaw), 0f, MathF.Sin(ringYaw)),
                Math.Max(0.04f, towerRadius - 0.02f),
                0.034f,
                ringYaw,
                Color.FromArgb(255, darkBody),
                Color.FromArgb(248, BlendColor(darkBody, Color.Black, 0.18f)),
                20);

            string? lockedPlateId = ResolveLockedPlateIdFor(entity);
            double armorTimeSec = entity.IsAlive ? _host.World.GameTimeSec : 0.0;
            foreach (ArmorPlateTarget plate in SimulationCombatMath.GetArmorPlateTargets(entity, _host.World.MetersPerWorldUnit, armorTimeSec))
            {
                DrawOutpostArmorPlate(graphics, entity, profile, plate, string.Equals(lockedPlateId, plate.Id, StringComparison.OrdinalIgnoreCase));
            }
        }

        return baseLiftM + towerHeight + 0.12f;
    }

    private void DrawOutpostArmorPlate(Graphics graphics, SimulationEntity entity, RobotAppearanceProfile profile, ArmorPlateTarget plate, bool locked)
    {
        bool topPlate = string.Equals(plate.Id, "outpost_top", StringComparison.OrdinalIgnoreCase);
        Color teamColor = ResolveTeamColor(entity.Team);
        Color plateColor = locked
            ? Color.FromArgb(255, 255, 210, 76)
            : Color.FromArgb(255, BlendColor(teamColor, Color.White, 0.24f));
        Color edgeColor = locked
            ? Color.FromArgb(255, 255, 242, 142)
            : Color.FromArgb(255, BlendColor(teamColor, Color.Black, 0.12f));
        Color backingColor = Color.FromArgb(255, 42, 46, 54);
        Color backingEdge = Color.FromArgb(255, 18, 20, 24);
        float plateSide = ResolveStructureArmorPlateSideM(entity);
        float plateThickness = ResolveStructureArmorPlateThicknessM(entity);

        float yaw = (float)(plate.YawDeg * Math.PI / 180.0);
        Vector3 plateCenter = ToScenePoint(plate.X, plate.Y, (float)plate.HeightM);
        if (IsTerrainOccludingPoint(plateCenter))
        {
            return;
        }

        if (!topPlate)
        {
            Vector3 forward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
            Vector3 side = new(-forward.Z, 0f, forward.X);
            DrawOrientedBoxSolid(
                graphics,
                plateCenter,
                forward,
                side,
                Vector3.UnitY,
                plateThickness * 1.16f,
                plateSide * 1.08f,
                plateSide * 1.08f,
                backingColor,
                backingEdge,
                null);
            DrawOrientedBoxSolid(
                graphics,
                plateCenter,
                forward,
                side,
                Vector3.UnitY,
                plateThickness,
                plateSide,
                plateSide,
                plateColor,
                edgeColor,
                null);
            return;
        }

        Vector3 topForward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 topSide = new(-topForward.Z, 0f, topForward.X);
        float tiltDeg = topPlate ? (profile.StructureTopArmorTiltDeg > 1e-4f ? profile.StructureTopArmorTiltDeg : 45f) : 0f;
        Vector3 plateNormal = Vector3.Normalize(topForward * MathF.Sin(tiltDeg * MathF.PI / 180f) + Vector3.UnitY * MathF.Cos(tiltDeg * MathF.PI / 180f));
        Vector3 tiltedAxis = Vector3.Normalize(Vector3.Cross(topSide, plateNormal));
        DrawOrientedBoxSolid(
            graphics,
            plateCenter,
            tiltedAxis,
            topSide,
            plateNormal,
            plateSide * 1.08f,
            plateSide * 1.08f,
            plateThickness * 1.16f,
            backingColor,
            backingEdge,
            null);
        DrawOrientedBoxSolid(
            graphics,
            plateCenter,
            tiltedAxis,
            topSide,
            plateNormal,
            plateSide,
            plateSide,
            plateThickness,
            plateColor,
            edgeColor,
            null);
    }

    private float DrawBaseModel(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile,
        StructureRenderPass renderPass = StructureRenderPass.Full)
    {
        float yaw = (float)(entity.AngleDeg * Math.PI / 180.0);
        Vector3 forward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 right = new(-forward.Z, 0f, forward.X);
        Color teamColor = ResolveTeamColor(entity.Team);
        Color bodyColor = ResolveDeepGrayMaterial(profile.BodyColor, entity.IsAlive, 0.00f);
        Color armorColor = ResolveDeepGrayMaterial(profile.ArmorColor, entity.IsAlive, 0.02f);
        Color darkColor = ResolveDeepGrayMaterial(profile.BodyColor, entity.IsAlive, -0.09f);
        Color edgeColor = Color.FromArgb(entity.IsAlive ? 252 : 216, BlendColor(bodyColor, Color.Black, 0.18f));

        float baseLength = Math.Clamp(profile.BodyLengthM > 1e-4f ? profile.BodyLengthM : BaseDiagramLengthM, 1.10f, 2.35f);
        float baseWidth = Math.Clamp((profile.BodyWidthM > 1e-4f ? profile.BodyWidthM : BaseDiagramWidthM) * Math.Max(0.4f, profile.BodyRenderWidthScale), 0.90f, 2.05f);
        float baseHeight = Math.Clamp(profile.BodyHeightM > 1e-4f ? profile.BodyHeightM : BaseDiagramHeightM, 0.70f, 1.60f);
        float roofHeight = Math.Clamp(profile.StructureRoofHeightM > 1e-4f ? profile.StructureRoofHeightM : baseHeight * 0.87f, 0.45f, baseHeight - 0.02f);
        float slabHeight = Math.Min(0.20f, baseHeight * 0.22f);
        float shoulderHeight = Math.Clamp(
            profile.StructureShoulderHeightM > 1e-4f ? profile.StructureShoulderHeightM : baseHeight * (BaseShoulderHeightM / BaseDiagramHeightM),
            slabHeight + 0.12f,
            roofHeight - 0.05f);

        if (renderPass != StructureRenderPass.DynamicArmor)
        {
            DrawTaperedBaseSection(
                graphics,
                center,
                profile,
                yaw,
                baseLength,
                baseWidth,
                baseLength * 0.96f,
                baseWidth * 0.94f,
                0f,
                slabHeight,
                Color.FromArgb(255, BlendColor(darkColor, bodyColor, 0.24f)),
                edgeColor);

            DrawTaperedBaseSection(
                graphics,
                center,
                profile,
                yaw,
                baseLength * 0.90f,
                baseWidth * 0.88f,
                baseLength * 0.62f,
                baseWidth * 0.56f,
                slabHeight,
                shoulderHeight,
                Color.FromArgb(255, bodyColor),
                edgeColor);

            DrawTaperedBaseSection(
                graphics,
                center,
                profile,
                yaw,
                baseLength * 0.62f,
                baseWidth * 0.56f,
                baseLength * 0.40f,
                baseWidth * 0.34f,
                shoulderHeight,
                roofHeight,
                Color.FromArgb(255, BlendColor(bodyColor, Color.White, 0.08f)),
                edgeColor);

            DrawBaseFixedDetails(graphics, center, yaw, entity, profile, bodyColor, armorColor, darkColor, edgeColor, baseLength, baseWidth, baseHeight);

            DrawOrientedBoxSolid(
                graphics,
                center + Vector3.UnitY * (baseHeight * 0.58f),
                forward,
                right,
                Vector3.UnitY,
                0.11f,
                0.12f,
                Math.Min(baseHeight * 0.66f, profile.StructureCoreColumnHeightM > 1e-4f ? profile.StructureCoreColumnHeightM : BaseCoreColumnHeightM),
                Color.FromArgb(255, ResolveDeepGrayMaterial(profile.BodyColor, entity.IsAlive, -0.02f)),
                Color.FromArgb(248, BlendColor(edgeColor, Color.Black, 0.18f)),
                null);

            DrawBaseDetectorModule(graphics, center, yaw, entity, profile, darkColor, edgeColor, baseWidth, baseHeight);
        }

        if (renderPass != StructureRenderPass.StaticBody)
        {
            string? lockedPlateId = ResolveLockedPlateIdFor(entity);
            float slideM = 0f;
            Vector3 topPlateCenter = center
                + forward * (baseLength * 0.04f + profile.StructureTopArmorOffsetXM)
                + right * (slideM + profile.StructureTopArmorOffsetZM)
                + Vector3.UnitY * (profile.StructureTopArmorCenterHeightM > 1e-4f ? profile.StructureTopArmorCenterHeightM : baseHeight * (BaseTopArmorCenterHeightM / BaseDiagramHeightM));
            if (!IsTerrainOccludingPoint(topPlateCenter))
            {
                Color topPlateColor = string.Equals(lockedPlateId, "base_top_slide", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(255, 255, 211, 84)
                    : Color.FromArgb(255, armorColor);
                float plateSide = ResolveStructureArmorPlateSideM(entity);
                float plateThickness = ResolveStructureArmorPlateThicknessM(entity);
                float tiltRad = (profile.StructureTopArmorTiltDeg > 1e-4f ? profile.StructureTopArmorTiltDeg : BaseTopArmorTiltDeg) * MathF.PI / 180f;
                Vector3 plateForward = Vector3.Normalize(forward * MathF.Cos(tiltRad) - Vector3.UnitY * MathF.Sin(tiltRad));
                Vector3 plateNormal = Vector3.Normalize(forward * MathF.Sin(tiltRad) + Vector3.UnitY * MathF.Cos(tiltRad));
                DrawOrientedBoxSolid(
                    graphics,
                    topPlateCenter,
                    plateForward,
                    right,
                    plateNormal,
                    plateSide * 1.08f,
                    plateSide * 1.08f,
                    plateThickness * 1.16f,
                    Color.FromArgb(255, 42, 46, 54),
                    Color.FromArgb(255, 18, 20, 24),
                    null);
                DrawOrientedBoxSolid(
                    graphics,
                    topPlateCenter,
                    plateForward,
                    right,
                    plateNormal,
                    plateSide,
                    plateSide,
                    plateThickness,
                    topPlateColor,
                    Color.FromArgb(255, BlendColor(topPlateColor, Color.Black, 0.18f)),
                    null);
            }

            if (entity.Health < BaseArmorOpenThresholdHealth)
            {
                DrawBaseExpandedArmor(graphics, center, yaw, entity, profile, baseLength, baseWidth, baseHeight, armorColor, edgeColor, lockedPlateId);
            }
        }

        return baseHeight + 0.20f;
    }

    private Color ResolveStructureGroundBlendColor(SimulationEntity entity)
    {
        return TrySampleTerrainBaseColor(
            Math.Clamp((int)Math.Round(entity.X / Math.Max(_cachedRuntimeGrid?.CellWidthWorld ?? 1.0f, 1e-6f)), 0, Math.Max(0, (_cachedRuntimeGrid?.WidthCells ?? 1) - 1)),
            Math.Clamp((int)Math.Round(entity.Y / Math.Max(_cachedRuntimeGrid?.CellHeightWorld ?? 1.0f, 1e-6f)), 0, Math.Max(0, (_cachedRuntimeGrid?.HeightCells ?? 1) - 1)),
            out Color sampled)
            ? sampled
            : ResolveTerrainColor(0, 0f);
    }

    private void DrawBaseFixedDetails(
        Graphics graphics,
        Vector3 center,
        float yaw,
        SimulationEntity entity,
        RobotAppearanceProfile profile,
        Color bodyColor,
        Color armorColor,
        Color darkColor,
        Color edgeColor,
        float baseLength,
        float baseWidth,
        float baseHeight)
    {
        Vector3 forward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 right = new(-forward.Z, 0f, forward.X);
        Color teamColor = ResolveTeamColor(entity.Team);

        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float sideSign = sideIndex == 0 ? -1f : 1f;
            Vector3 sideCenter = center
                + right * (sideSign * baseWidth * 0.43f)
                + forward * (-baseLength * 0.07f)
                + Vector3.UnitY * (baseHeight * 0.44f);
            DrawOrientedBoxSolid(
                graphics,
                sideCenter,
                forward,
                right,
                Vector3.UnitY,
                baseLength * 0.36f,
                0.070f,
                baseHeight * 0.48f,
                Color.FromArgb(255, 42, 46, 54),
                Color.FromArgb(255, 18, 20, 24),
                null);
            DrawOrientedBoxSolid(
                graphics,
                sideCenter + right * (sideSign * 0.004f),
                forward,
                right,
                Vector3.UnitY,
                baseLength * 0.33f,
                0.044f,
                baseHeight * 0.42f,
                Color.FromArgb(255, BlendColor(armorColor, Color.White, 0.08f)),
                edgeColor,
                null);

            Vector3 stripCenter = sideCenter + right * (sideSign * 0.041f) + Vector3.UnitY * (baseHeight * 0.08f);
            DrawStructureHealthLightBar(
                graphics,
                entity,
                stripCenter,
                forward,
                right,
                Vector3.UnitY,
                baseLength * 0.23f,
                0.018f,
                baseHeight * 0.32f);
        }

        Vector3 frontPanel = center + forward * (baseLength * 0.42f) + Vector3.UnitY * (baseHeight * 0.28f);
        DrawOrientedBoxSolid(
            graphics,
            frontPanel,
            forward,
            right,
            Vector3.UnitY,
            0.045f,
            baseWidth * 0.26f,
            baseHeight * 0.22f,
            Color.FromArgb(255, darkColor),
            Color.FromArgb(248, BlendColor(darkColor, Color.Black, 0.18f)),
            null);

        Vector3 rearPlateCenter = center + forward * (-baseLength * 0.28f) + Vector3.UnitY * (baseHeight * 0.18f);
        DrawOrientedBoxSolid(
            graphics,
            rearPlateCenter,
            forward,
            right,
            Vector3.UnitY,
            baseLength * 0.28f,
            baseWidth * 0.30f,
            0.060f,
            Color.FromArgb(255, BlendColor(darkColor, bodyColor, 0.24f)),
            edgeColor,
            null);

        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float sideSign = sideIndex == 0 ? -1f : 1f;
            Vector3 sideLightCenter = center
                + right * (sideSign * baseWidth * 0.31f)
                + forward * (-baseLength * 0.06f)
                + Vector3.UnitY * (baseHeight * 0.62f);
            Vector3 panelUp = Vector3.Normalize(Vector3.UnitY * 0.92f + right * (sideSign * 0.40f));
            Vector3 panelRight = Vector3.Normalize(Vector3.Cross(panelUp, forward));
            DrawStructureHealthLightBar(
                graphics,
                entity,
                sideLightCenter,
                forward,
                panelRight,
                panelUp,
                baseLength * 0.26f,
                0.020f,
                baseHeight * 0.30f);
        }
    }

    private void DrawStructureHealthLightBar(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        Vector3 lengthAxis,
        Vector3 depthAxis,
        Vector3 upAxis,
        float length,
        float depth,
        float height)
    {
        Color teamColor = ResolveTeamColor(entity.Team);
        float healthRatio = entity.MaxHealth <= 1e-6
            ? 0f
            : (float)Math.Clamp(entity.Health / entity.MaxHealth, 0.0, 1.0);
        Vector3 safeLengthAxis = lengthAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(lengthAxis);
        Vector3 safeDepthAxis = depthAxis.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(depthAxis);
        Vector3 safeUpAxis = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        float safeLength = Math.Max(0.04f, length);
        float safeDepth = Math.Max(0.008f, depth);
        float safeHeight = Math.Max(0.010f, height);

        DrawOrientedBoxSolid(
            graphics,
            center,
            safeLengthAxis,
            safeDepthAxis,
            safeUpAxis,
            safeLength,
            safeDepth,
            safeHeight,
            Color.FromArgb(entity.IsAlive ? 255 : 226, 30, 34, 40),
            Color.FromArgb(255, 12, 14, 18),
            null);

        float fillLength = safeLength * healthRatio;
        if (fillLength <= 0.002f)
        {
            return;
        }

        DrawOrientedBoxSolid(
            graphics,
            center - safeLengthAxis * ((safeLength - fillLength) * 0.5f),
            safeLengthAxis,
            safeDepthAxis,
            safeUpAxis,
            fillLength,
            safeDepth * 1.08f,
            safeHeight * 1.08f,
            Color.FromArgb(entity.IsAlive ? 255 : 180, teamColor),
            Color.FromArgb(255, BlendColor(teamColor, Color.Black, 0.18f)),
            null);
    }

    private void DrawBaseExpandedArmor(
        Graphics graphics,
        Vector3 center,
        float yaw,
        SimulationEntity entity,
        RobotAppearanceProfile profile,
        float baseLength,
        float baseWidth,
        float baseHeight,
        Color armorColor,
        Color edgeColor,
        string? lockedPlateId)
    {
        Vector3 forward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 right = new(-forward.Z, 0f, forward.X);
        float openAngle = (profile.StructureSideArmorOpenAngleDeg > 1e-4f ? profile.StructureSideArmorOpenAngleDeg : 27.5f) * MathF.PI / 180f;
        float outwardOffset = profile.StructureSideArmorOutwardOffsetM > 1e-4f ? profile.StructureSideArmorOutwardOffsetM : 0.12f;
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float sideSign = sideIndex == 0 ? -1f : 1f;
            Vector3 sideDirection = right * sideSign;
            Vector3 panelUp = Vector3.Normalize(Vector3.UnitY * MathF.Cos(openAngle) + sideDirection * MathF.Sin(openAngle));
            Vector3 panelRight = Vector3.Cross(panelUp, forward);
            if (Vector3.Dot(panelRight, sideDirection) < 0f)
            {
                panelRight = -panelRight;
            }

            Vector3 panelCenter = center
                + sideDirection * (baseWidth * 0.33f + outwardOffset)
                + forward * (-baseLength * 0.05f)
                + Vector3.UnitY * (baseHeight * 0.58f);
            DrawOrientedBoxSolid(
                graphics,
                panelCenter,
                forward,
                panelRight,
                panelUp,
                baseLength * 0.34f,
                0.075f,
                baseHeight * 0.58f,
                Color.FromArgb(255, 42, 46, 54),
                Color.FromArgb(255, 18, 20, 24),
                null);
            DrawOrientedBoxSolid(
                graphics,
                panelCenter + sideDirection * 0.006f,
                forward,
                panelRight,
                panelUp,
                baseLength * 0.31f,
                0.048f,
                baseHeight * 0.50f,
                Color.FromArgb(255, BlendColor(armorColor, Color.White, 0.12f)),
                edgeColor,
                null);
        }

        Color teamColor = ResolveTeamColor(entity.Team);
        Color coreColor = string.Equals(lockedPlateId, "base_core", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(255, 255, 211, 84)
            : Color.FromArgb(255, BlendColor(teamColor, Color.FromArgb(180, 42, 36), 0.48f));
        Vector3 coreCenter = center + forward * (baseLength * 0.15f) + Vector3.UnitY * (baseHeight * 0.70f);
        if (!IsTerrainOccludingPoint(coreCenter))
        {
        float plateSide = ResolveStructureArmorPlateSideM(entity);
        float plateThickness = ResolveStructureArmorPlateThicknessM(entity);
        DrawOrientedBoxSolid(
            graphics,
            coreCenter,
            forward,
            right,
            Vector3.UnitY,
            plateThickness * 1.16f,
            plateSide * 1.08f,
            plateSide * 1.08f,
            Color.FromArgb(255, 42, 46, 54),
            Color.FromArgb(255, 18, 20, 24),
            null);
        DrawOrientedBoxSolid(
            graphics,
            coreCenter,
            forward,
            right,
            Vector3.UnitY,
            plateThickness,
            plateSide,
            plateSide,
            coreColor,
            Color.FromArgb(255, BlendColor(coreColor, Color.Black, 0.18f)),
            null);
        }

        DrawOrientedBoxSolid(
            graphics,
            center + Vector3.UnitY * (baseHeight * 0.58f),
            forward,
            right,
            Vector3.UnitY,
            0.12f,
            0.12f,
            Math.Min(baseHeight * 0.66f, BaseCoreColumnHeightM),
            Color.FromArgb(255, BlendColor(coreColor, Color.FromArgb(255, 64, 48), 0.38f)),
            Color.FromArgb(248, BlendColor(coreColor, Color.White, 0.08f)),
            null);
    }

    private void DrawOutpostHead(
        Graphics graphics,
        Vector3 center,
        float yaw,
        float towerHeight,
        float headBaseHeight,
        SimulationEntity entity,
        RobotAppearanceProfile profile,
        Color capColor,
        Color edgeColor)
    {
        Vector3 forward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 right = new(-forward.Z, 0f, forward.X);
        Color darkColor = BlendColor(capColor, Color.Black, 0.38f);
        Color teamColor = ResolveTeamColor(entity.Team);

        Vector3 neckCenter = center + Vector3.UnitY * (headBaseHeight + 0.05f);
        DrawOrientedBoxSolid(
            graphics,
            neckCenter,
            forward,
            right,
            Vector3.UnitY,
            0.16f,
            0.14f,
            0.10f,
            Color.FromArgb(255, darkColor),
            edgeColor,
            null);

        Vector3 topHeadCenter = center + forward * 0.03f + Vector3.UnitY * (Math.Min(towerHeight - 0.08f, headBaseHeight + 0.16f));
        DrawOrientedBoxSolid(
            graphics,
            topHeadCenter,
            forward,
            right,
            Vector3.UnitY,
            0.21f,
            0.18f,
            0.12f,
            Color.FromArgb(255, BlendColor(capColor, Color.White, 0.06f)),
            edgeColor,
            null);

        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float sideSign = sideIndex == 0 ? -1f : 1f;
            Vector3 armCenter = center + right * (sideSign * 0.25f) + Vector3.UnitY * (headBaseHeight + 0.03f);
            Vector3 armUp = Vector3.Normalize(Vector3.UnitY * 0.84f + right * (sideSign * 0.54f));
            Vector3 armSide = Vector3.Normalize(Vector3.Cross(forward, armUp));
            DrawOrientedBoxSolid(
                graphics,
                armCenter,
                forward,
                armSide,
                armUp,
                0.10f,
                0.016f,
                0.22f,
                Color.FromArgb(255, darkColor),
                edgeColor,
                null);
        }

        Vector3 lampCenter = center + forward * 0.02f + Vector3.UnitY * (towerHeight - 0.025f);
        DrawOrientedBoxSolid(
            graphics,
            lampCenter,
            forward,
            right,
            Vector3.UnitY,
            0.055f,
            0.055f,
            0.040f,
            Color.FromArgb(entity.IsAlive ? 255 : 220, BlendColor(teamColor, Color.FromArgb(96, 255, 130), 0.45f)),
            Color.FromArgb(255, BlendColor(teamColor, Color.White, 0.18f)),
            null);
    }

    private void DrawBaseDetectorModule(
        Graphics graphics,
        Vector3 center,
        float yaw,
        SimulationEntity entity,
        RobotAppearanceProfile profile,
        Color darkColor,
        Color edgeColor,
        float baseWidth,
        float baseHeight)
    {
        Vector3 forward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 right = new(-forward.Z, 0f, forward.X);
        Color teamColor = ResolveTeamColor(entity.Team);
        float detectorWidth = Math.Min((profile.StructureDetectorWidthM > 1e-4f ? profile.StructureDetectorWidthM : BaseDetectorWidthM) / BaseDiagramWidthM * baseWidth, baseWidth * 0.88f);
        float bridgeCenterHeight = profile.StructureDetectorBridgeCenterHeightM > 1e-4f ? profile.StructureDetectorBridgeCenterHeightM : baseHeight * (BaseDetectorBridgeCenterHeightM / BaseDiagramHeightM);
        float sensorCenterHeight = profile.StructureDetectorSensorCenterHeightM > 1e-4f ? profile.StructureDetectorSensorCenterHeightM : baseHeight * (BaseDetectorSensorCenterHeightM / BaseDiagramHeightM);
        Vector3 bridgeCenter = center + forward * 0.02f + Vector3.UnitY * bridgeCenterHeight;
        DrawOrientedBoxSolid(
            graphics,
            bridgeCenter,
            right,
            forward,
            Vector3.UnitY,
            detectorWidth,
            0.08f,
            0.045f,
            Color.FromArgb(255, darkColor),
            edgeColor,
            null);

        Vector3 sensorCenter = center + Vector3.UnitY * sensorCenterHeight;
        DrawCylinderSolid(
            graphics,
            sensorCenter,
            Vector3.UnitY,
            right,
            0.050f,
            Math.Max(0.030f, (profile.StructureDetectorHeightM > 1e-4f ? profile.StructureDetectorHeightM : BaseDetectorHeightM) * 0.50f),
            yaw,
            Color.FromArgb(entity.IsAlive ? 255 : 220, BlendColor(teamColor, Color.FromArgb(96, 255, 130), 0.45f)),
            Color.FromArgb(255, BlendColor(teamColor, Color.White, 0.18f)),
            16);
    }

    private void DrawTaperedBaseSection(
        Graphics graphics,
        Vector3 center,
        RobotAppearanceProfile profile,
        float yaw,
        float bottomLength,
        float bottomWidth,
        float topLength,
        float topWidth,
        float bottomHeight,
        float topHeight,
        Color fillColor,
        Color edgeColor)
    {
        IReadOnlyList<Vector3> bottom = BuildBaseHexagonalFootprint(center, profile, bottomLength, bottomWidth, bottomHeight, yaw);
        IReadOnlyList<Vector3> top = BuildBaseHexagonalFootprint(center, profile, topLength, topWidth, topHeight, yaw);
        DrawGeneralPrism(graphics, bottom, top, fillColor, edgeColor, null);
    }

    private static float ResolveBaseTopArmorSlideM(double gameTimeSec)
        => MathF.Sin((float)gameTimeSec * BaseTopArmorSlideSpeedRadPerSec) * BaseTopArmorSlideAmplitudeM;

    private static float ResolveStructureArmorPlateSideM(SimulationEntity entity)
        => Math.Clamp((float)Math.Max(entity.ArmorPlateWidthM, entity.ArmorPlateHeightM), 0.04f, 0.60f);

    private static float ResolveStructureArmorPlateThicknessM(SimulationEntity entity)
        => Math.Clamp((float)Math.Max(entity.ArmorPlateGapM, StructureArmorPlateThicknessM), 0.012f, 0.10f);

    private static IReadOnlyList<Vector3> BuildBaseHexagonalFootprint(
        Vector3 center,
        RobotAppearanceProfile profile,
        float length,
        float width,
        float height,
        float yaw)
    {
        float halfLength = Math.Max(0.05f, length * 0.5f);
        float halfWidth = Math.Max(0.05f, width * 0.5f);
        float shortEdge = Math.Clamp(
            profile.StructureHexTopEdgeM > 1e-4f ? profile.StructureHexTopEdgeM : length * 0.58f,
            0.05f,
            length * 0.92f);
        float cornerX = MathF.Min(halfLength * 0.92f, shortEdge * 0.5f);
        Vector2[] local =
        {
            new(-cornerX, -halfWidth),
            new(cornerX, -halfWidth),
            new(halfLength, 0f),
            new(cornerX, halfWidth),
            new(-cornerX, halfWidth),
            new(-halfLength, 0f),
        };

        Vector2 forward = new(MathF.Cos(yaw), MathF.Sin(yaw));
        Vector2 right = new(-forward.Y, forward.X);
        Vector3[] result = new Vector3[local.Length];
        for (int index = 0; index < local.Length; index++)
        {
            Vector2 offset = forward * local[index].X + right * local[index].Y;
            result[index] = new Vector3(center.X + offset.X, center.Y + height, center.Z + offset.Y);
        }

        return result;
    }

    private void DrawTaperedOutpostSection(
        Graphics graphics,
        Vector3 center,
        float yaw,
        float bottomRadius,
        float topRadius,
        float bottomHeight,
        float topHeight,
        Color fillColor,
        Color edgeColor)
    {
        IReadOnlyList<Vector3> bottom = BuildRegularPolygonFootprint(center, Math.Max(0.02f, bottomRadius), bottomHeight, yaw, 8);
        IReadOnlyList<Vector3> top = BuildRegularPolygonFootprint(center, Math.Max(0.02f, topRadius), topHeight, yaw, 8);
        DrawGeneralPrism(graphics, bottom, top, fillColor, edgeColor, null);
    }

    private void DrawOutpostRibs(Graphics graphics, Vector3 center, float yaw, Color bodyColor, Color edgeColor)
    {
        Color ribColor = Color.FromArgb(255, BlendColor(bodyColor, Color.Black, 0.24f));
        float[] yaws = { 0f, MathF.PI * 0.5f, MathF.PI, MathF.PI * 1.5f };
        foreach (float localYaw in yaws)
        {
            float ribYaw = yaw + localYaw;
            Vector3 ribCenter = OffsetScenePosition(
                center,
                MathF.Cos(localYaw) * 0.300f,
                MathF.Sin(localYaw) * 0.300f,
                yaw,
                0.60f);
            IReadOnlyList<Vector3> ribFootprint = BuildOrientedRectFootprint(
                ribCenter,
                0.070f,
                0.050f,
                0f,
                ribYaw);
            DrawPrismWireframe(
                graphics,
                ribFootprint,
                0.84f,
                ribColor,
                edgeColor,
                null);
        }
    }

    private float DrawEnergyMechanismModel(Graphics graphics, FacilityRegion region, double? overrideCenterWorldX = null, double? overrideCenterWorldY = null)
    {
        RobotAppearanceProfile profile = _host.AppearanceCatalog.ResolveFacilityProfile(region);
        (double centerWorldX, double centerWorldY) = overrideCenterWorldX.HasValue && overrideCenterWorldY.HasValue
            ? (overrideCenterWorldX.Value, overrideCenterWorldY.Value)
            : ResolveFacilityRegionCenter(region);
        Vector3 center = ToScenePoint(centerWorldX, centerWorldY, 0f);
        EnergyRenderMesh mesh = EnergyMechanismGeometry.BuildSingle(
            profile,
            center,
            EnergyMechanismGeometry.ResolveAccentColor(region.Team),
            (float)_host.World.GameTimeSec,
            ResolveEnergyRotorYawForRender);

        foreach (EnergyRenderPrism prism in mesh.Prisms)
        {
            DrawGeneralPrism(graphics, prism.Bottom, prism.Top, prism.FillColor, prism.EdgeColor, null);
        }

        foreach (EnergyRenderBox box in mesh.Boxes)
        {
            DrawOrientedBoxSolid(
                graphics,
                box.Center,
                box.Forward,
                box.Right,
                box.Up,
                box.Length,
                box.Width,
                box.Height,
                box.FillColor,
                box.EdgeColor,
                null);
        }

        foreach (EnergyRenderCylinder cylinder in mesh.Cylinders)
        {
            DrawCylinderSolid(
                graphics,
                cylinder.Center,
                cylinder.NormalAxis,
                cylinder.UpAxis,
                cylinder.Radius,
                cylinder.Thickness,
                0f,
                cylinder.FillColor,
                cylinder.EdgeColor,
                cylinder.Segments);
        }

        return mesh.MaxHeight;
    }

    private void DrawEnergyMechanismHanger(
        Graphics graphics,
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float width,
        float height,
        float depth,
        Color frameColor,
        Color edgeColor)
    {
        float frameHalfLength = width * 0.5f;
        float frameHalfHeight = height * 0.5f;
        float bar = Math.Max(0.020f, Math.Min(width, height) * 0.12f);
        foreach (float side in new[] { -1f, 1f })
        {
            DrawOrientedBoxSolid(
                graphics,
                center + forward * (frameHalfLength * side),
                up,
                right,
                forward,
                frameHalfHeight * 2f,
                depth,
                bar,
                frameColor,
                edgeColor,
                null);

            DrawOrientedBoxSolid(
                graphics,
                center + up * (frameHalfHeight * side),
                forward,
                right,
                up,
                frameHalfLength * 2f + bar,
                depth,
                bar,
                frameColor,
                edgeColor,
                null);
        }
    }

    private IReadOnlyList<Vector3> BuildEnergyPlatformFootprint(
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        float baseHeight,
        float length,
        float width,
        float cornerScale)
    {
        float halfLength = Math.Max(0.12f, length * 0.5f);
        float halfWidth = Math.Max(0.12f, width * 0.5f);
        float cutLength = Math.Max(0.05f, halfLength * cornerScale);
        float cutWidth = Math.Max(0.05f, halfWidth * cornerScale);
        (float X, float Z)[] shape =
        [
            (-halfLength + cutLength, -halfWidth),
            (halfLength - cutLength, -halfWidth),
            (halfLength, -halfWidth + cutWidth),
            (halfLength, halfWidth - cutWidth),
            (halfLength - cutLength, halfWidth),
            (-halfLength + cutLength, halfWidth),
            (-halfLength, halfWidth - cutWidth),
            (-halfLength, -halfWidth + cutWidth),
        ];
        Vector3[] result = new Vector3[shape.Length];
        for (int index = 0; index < shape.Length; index++)
        {
            result[index] = center + forward * shape[index].X + right * shape[index].Z + Vector3.UnitY * baseHeight;
        }

        return result;
    }

    private void DrawEnergyMechanismBrace(
        Graphics graphics,
        Vector3 start,
        Vector3 end,
        float width,
        float depth,
        Color fillColor,
        Color edgeColor)
    {
        Vector3 axis = end - start;
        float length = axis.Length();
        if (length <= 1e-4f)
        {
            return;
        }

        Vector3 forward = Vector3.Normalize(axis);
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        if (right.LengthSquared() <= 1e-6f)
        {
            right = Vector3.UnitZ;
        }

        Vector3 up = Vector3.Normalize(Vector3.Cross(forward, right));
        DrawOrientedBoxSolid(
            graphics,
            (start + end) * 0.5f,
            forward,
            right,
            up,
            length,
            depth,
            width,
            fillColor,
            edgeColor,
            null);
    }

    private void DrawEnergyMechanismArm(
        Graphics graphics,
        Vector3 center,
        Vector3 axis,
        Vector3 right,
        Vector3 up,
        float innerRadius,
        float outerRadius,
        float railGap,
        float railThickness,
        Color fillColor,
        Color edgeColor)
    {
        Vector3 innerCenter = center + axis * innerRadius;
        Vector3 outerCenter = center + axis * outerRadius;
        Vector3 railOffset = up * railGap;
        DrawEnergyMechanismBrace(graphics, innerCenter + railOffset, outerCenter + railOffset * 0.72f, railThickness, railThickness, fillColor, edgeColor);
        DrawEnergyMechanismBrace(graphics, innerCenter - railOffset, outerCenter - railOffset * 0.72f, railThickness, railThickness, fillColor, edgeColor);
        DrawEnergyMechanismBrace(graphics, innerCenter + railOffset, innerCenter - railOffset, railThickness * 0.8f, railThickness, fillColor, edgeColor);
        DrawEnergyMechanismBrace(graphics, outerCenter + railOffset * 0.72f, outerCenter - railOffset * 0.72f, railThickness, railThickness, fillColor, edgeColor);
    }

    private void DrawEnergyMechanismPod(
        Graphics graphics,
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float length,
        float width,
        float height,
        Color fillColor,
        Color edgeColor)
    {
        DrawOrientedBoxSolid(
            graphics,
            center,
            forward,
            right,
            up,
            length * 0.72f,
            width,
            height,
            Color.FromArgb(255, 68, 72, 78),
            edgeColor,
            null);
        DrawOrientedBoxSolid(
            graphics,
            center + forward * (length * 0.18f),
            forward,
            right,
            up,
            length * 0.22f,
            width * 0.82f,
            height * 0.78f,
            fillColor,
            Color.FromArgb(255, BlendColor(fillColor, Color.Black, 0.22f)),
            null);
        DrawOrientedBoxSolid(
            graphics,
            center - forward * (length * 0.22f),
            forward,
            right,
            up,
            length * 0.16f,
            width * 0.72f,
            height * 0.66f,
            Color.FromArgb(255, 54, 58, 64),
            edgeColor,
            null);
        DrawOrientedBoxSolid(
            graphics,
            center - forward * (length * 0.06f) + right * (width * 0.18f),
            forward,
            right,
            up,
            length * 0.14f,
            width * 0.22f,
            height * 0.18f,
            Color.FromArgb(255, 60, 64, 70),
            edgeColor,
            null);
        DrawOrientedBoxSolid(
            graphics,
            center - forward * (length * 0.06f) - right * (width * 0.18f),
            forward,
            right,
            up,
            length * 0.14f,
            width * 0.22f,
            height * 0.18f,
            Color.FromArgb(255, 60, 64, 70),
            edgeColor,
            null);
    }

    private static IReadOnlyList<Vector3> BuildRegularPolygonFootprint(
        Vector3 center,
        float radius,
        float height,
        float yaw,
        int sides)
    {
        int segmentCount = Math.Max(4, sides);
        Vector3[] result = new Vector3[segmentCount];
        for (int index = 0; index < segmentCount; index++)
        {
            float angle = yaw + index * MathF.Tau / segmentCount;
            result[index] = new Vector3(
                center.X + MathF.Cos(angle) * radius,
                center.Y + height,
                center.Z + MathF.Sin(angle) * radius);
        }

        return result;
    }

    private string? ResolveLockedPlateIdFor(SimulationEntity entity)
    {
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is null
            || !selected.AutoAimLocked
            || string.IsNullOrWhiteSpace(selected.AutoAimTargetId)
            || !string.Equals(selected.AutoAimTargetId, entity.Id, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return selected.AutoAimPlateId;
    }
}
