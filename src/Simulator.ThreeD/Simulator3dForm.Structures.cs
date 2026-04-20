using System.Numerics;
using Simulator.Core.Gameplay;

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
        Color mapGroundColor = ResolveStructureGroundBlendColor(entity);
        Color bodyColor = entity.IsAlive
            ? TintProfileColor(profile.BodyColor, teamColor, 0.16f, true)
            : BlendColor(mapGroundColor, Color.FromArgb(84, 94, 108), 0.25f);
        Color edgeColor = Color.FromArgb(entity.IsAlive ? 252 : 216, BlendColor(bodyColor, Color.Black, 0.16f));
        Color darkBody = BlendColor(mapGroundColor, teamColor, entity.IsAlive ? 0.10f : 0.02f);
        Color capColor = TintProfileColor(profile.TurretColor, teamColor, entity.IsAlive ? 0.18f : 0.02f, entity.IsAlive);
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
            DrawOutpostRibs(graphics, center, yaw, entity, bodyColor, edgeColor);
        }

        if (renderPass != StructureRenderPass.StaticBody)
        {
            float ringYaw = (float)(entity.AngleDeg * Math.PI / 180.0) + (float)_host.World.GameTimeSec * OutpostRingSpeedRadPerSec;
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
            foreach (ArmorPlateTarget plate in SimulationCombatMath.GetArmorPlateTargets(entity, _host.World.MetersPerWorldUnit, _host.World.GameTimeSec))
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
        Color mapGroundColor = ResolveStructureGroundBlendColor(entity);
        Color bodyColor = entity.IsAlive
            ? BlendColor(TintProfileColor(profile.BodyColor, teamColor, 0.12f, true), mapGroundColor, 0.28f)
            : BlendColor(mapGroundColor, Color.FromArgb(84, 94, 108), 0.25f);
        Color armorColor = entity.IsAlive
            ? TintProfileColor(profile.ArmorColor, teamColor, 0.20f, true)
            : Color.FromArgb(92, 98, 108);
        Color darkColor = BlendColor(mapGroundColor, Color.FromArgb(36, 40, 46), 0.28f);
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
                Color.FromArgb(255, BlendColor(ResolveTeamColor(entity.Team), Color.FromArgb(198, 32, 26), 0.58f)),
                Color.FromArgb(248, BlendColor(edgeColor, Color.Black, 0.18f)),
                null);

            DrawBaseDetectorModule(graphics, center, yaw, entity, profile, darkColor, edgeColor, baseWidth, baseHeight);
        }

        if (renderPass != StructureRenderPass.StaticBody)
        {
            string? lockedPlateId = ResolveLockedPlateIdFor(entity);
            float slideM = 0f;
            Vector3 topPlateCenter = center
                + forward * (baseLength * 0.04f)
                + right * slideM
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
                Color.FromArgb(255, BlendColor(armorColor, Color.White, 0.08f)),
                edgeColor,
                null);

            Vector3 stripCenter = sideCenter + right * (sideSign * 0.041f) + Vector3.UnitY * (baseHeight * 0.08f);
            DrawOrientedBoxSolid(
                graphics,
                stripCenter,
                forward,
                right,
                Vector3.UnitY,
                baseLength * 0.23f,
                0.018f,
                baseHeight * 0.32f,
                Color.FromArgb(entity.IsAlive ? 255 : 226, teamColor),
                Color.FromArgb(255, BlendColor(teamColor, Color.Black, 0.18f)),
                null);
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
            DrawOrientedBoxSolid(
                graphics,
                sideLightCenter,
                forward,
                panelRight,
                panelUp,
                baseLength * 0.26f,
                0.020f,
                baseHeight * 0.30f,
                Color.FromArgb(entity.IsAlive ? 255 : 218, teamColor),
                Color.FromArgb(248, BlendColor(teamColor, Color.White, 0.18f)),
                null);
        }
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
        float openAngle = 27.5f * MathF.PI / 180f;
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
                + sideDirection * (baseWidth * 0.33f + 0.12f)
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
        IReadOnlyList<Vector3> bottom = BuildBaseOctagonalFootprint(center, bottomLength, bottomWidth, bottomHeight, yaw);
        IReadOnlyList<Vector3> top = BuildBaseOctagonalFootprint(center, topLength, topWidth, topHeight, yaw);
        DrawGeneralPrism(graphics, bottom, top, fillColor, edgeColor, null);
    }

    private static float ResolveBaseTopArmorSlideM(double gameTimeSec)
        => MathF.Sin((float)gameTimeSec * BaseTopArmorSlideSpeedRadPerSec) * BaseTopArmorSlideAmplitudeM;

    private static float ResolveStructureArmorPlateSideM(SimulationEntity entity)
        => Math.Clamp((float)Math.Max(entity.ArmorPlateWidthM, entity.ArmorPlateHeightM), 0.04f, 0.60f);

    private static float ResolveStructureArmorPlateThicknessM(SimulationEntity entity)
        => Math.Clamp((float)Math.Max(entity.ArmorPlateGapM, StructureArmorPlateThicknessM), 0.012f, 0.10f);

    private static IReadOnlyList<Vector3> BuildBaseOctagonalFootprint(
        Vector3 center,
        float length,
        float width,
        float height,
        float yaw)
    {
        float halfLength = Math.Max(0.05f, length * 0.5f);
        float halfWidth = Math.Max(0.05f, width * 0.5f);
        float chamfer = MathF.Min(halfLength, halfWidth) * 0.26f;
        Vector2[] local =
        {
            new(-halfLength + chamfer, -halfWidth),
            new(halfLength - chamfer, -halfWidth),
            new(halfLength, -halfWidth + chamfer),
            new(halfLength, halfWidth - chamfer),
            new(halfLength - chamfer, halfWidth),
            new(-halfLength + chamfer, halfWidth),
            new(-halfLength, halfWidth - chamfer),
            new(-halfLength, -halfWidth + chamfer),
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

    private void DrawOutpostRibs(Graphics graphics, Vector3 center, float yaw, SimulationEntity entity, Color bodyColor, Color edgeColor)
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

        Color lightColor = Color.FromArgb(entity.IsAlive ? 255 : 226, ResolveTeamColor(entity.Team));
        Vector3 lightCenter = OffsetScenePosition(center, 0.285f, 0f, yaw, 0.62f);
        IReadOnlyList<Vector3> lightFootprint = BuildOrientedRectFootprint(
            lightCenter,
            0.030f,
            0.110f,
            0f,
            yaw);
        DrawPrismWireframe(
            graphics,
            lightFootprint,
            0.68f,
            lightColor,
            Color.FromArgb(255, BlendColor(lightColor, Color.White, 0.16f)),
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
