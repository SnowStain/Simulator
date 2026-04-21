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

    private float DrawEnergyMechanismModel(Graphics graphics, FacilityRegion region)
    {
        RobotAppearanceProfile profile = _host.AppearanceCatalog.Resolve("energy_mechanism");
        double centerWorldX = (region.X1 + region.X2) * 0.5;
        double centerWorldY = (region.Y1 + region.Y2) * 0.5;
        Vector3 center = ToScenePoint(centerWorldX, centerWorldY, 0f);
        float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        float footprintWidthM = (float)Math.Abs(region.X2 - region.X1) * metersPerWorldUnit;
        float footprintDepthM = (float)Math.Abs(region.Y2 - region.Y1) * metersPerWorldUnit;
        float referenceLength = Math.Max(0.10f, profile.BodyLengthM);
        float referenceWidth = Math.Max(0.10f, profile.BodyWidthM);
        float modelScale = Math.Clamp(Math.Max(footprintWidthM / referenceLength, footprintDepthM / referenceWidth), 0.75f, 1.35f);

        const float mechanismYawRad = -MathF.PI * 0.25f;
        Vector3 groundForward = new(MathF.Cos(mechanismYawRad), 0f, MathF.Sin(mechanismYawRad));
        Vector3 groundRight = new(-groundForward.Z, 0f, groundForward.X);
        Vector3 worldUp = Vector3.UnitY;
        Color baseColor = profile.BodyColor;
        Color frameColor = profile.TurretColor;
        Color lampColor = profile.WheelColor;
        Color edgeColor = Color.FromArgb(255, 18, 22, 26);

        float groundClearance = Math.Max(0f, profile.StructureGroundClearanceM) * modelScale;
        center += worldUp * groundClearance;
        float baseLength = Math.Max(profile.StructureBaseLengthM * modelScale, footprintWidthM * 1.02f);
        float baseDepth = Math.Max(profile.StructureBaseWidthM * modelScale, footprintDepthM * 1.02f);
        float baseHeight = Math.Max(0.05f, profile.StructureBaseHeightM) * modelScale;
        float upperDeckLength = Math.Max(0.20f, profile.StructureBaseTopLengthM) * modelScale;
        float upperDeckDepth = Math.Max(0.20f, profile.StructureBaseTopWidthM) * modelScale;
        float upperDeckHeight = Math.Max(0.02f, profile.StructureBaseTopHeightM) * modelScale;
        float troughLength = upperDeckLength * 0.78f;
        float troughDepth = upperDeckDepth * 0.54f;
        float frameHeight = Math.Max(baseHeight + 0.40f, profile.StructureFrameHeightM * modelScale);
        float postHeight = Math.Max(1.90f * modelScale, frameHeight - baseHeight);
        float postWidth = Math.Max(0.02f, profile.StructureFrameColumnWidthM) * modelScale;
        float postOffsetZ = Math.Max(postWidth, profile.StructureSupportOffsetM * modelScale);
        float topBeamWidth = Math.Max(postOffsetZ * 2f, profile.StructureFrameWidthM * modelScale);
        float topBeamHeight = Math.Max(0.02f, profile.StructureFrameBeamHeightM) * modelScale;
        float rotorRadius = Math.Max(0.10f, profile.StructureRotorRadiusM) * modelScale;
        float rotorCenterHeight = Math.Max(baseHeight + 0.30f, profile.StructureRotorCenterHeightM * modelScale);
        float rotorLayerOffset = Math.Max(profile.StructureFrameDepthM * 0.45f * modelScale, 0.06f * modelScale);
        float cantileverLength = Math.Max(0.04f, profile.StructureCantileverLengthM) * modelScale;
        float cantileverHeight = Math.Max(0.01f, profile.StructureCantileverHeightM) * modelScale;
        float cantileverDepth = Math.Max(0.01f, profile.StructureCantileverDepthM) * modelScale;
        float cantileverCenterHeight = rotorCenterHeight + profile.StructureCantileverOffsetYM * modelScale;
        float cantileverPairGap = Math.Max(topBeamWidth + cantileverLength, profile.StructureCantileverPairGapM * modelScale);
        float hubOuterRadius = Math.Max(0.04f, profile.StructureRotorHubRadiusM) * modelScale;
        float hubInnerRadius = Math.Max(0.02f, profile.StructureRotorHubRadiusM * 0.44f) * modelScale;
        float armInnerOffset = Math.Max(hubOuterRadius * 1.35f, 0.12f * modelScale);
        float armLength = Math.Max(0.10f, profile.StructureRotorArmLengthM) * modelScale;
        float armOuterRadius = Math.Max(armInnerOffset + armLength, rotorRadius - profile.StructureLampLengthM * 0.15f * modelScale);
        float armWidth = Math.Max(0.01f, profile.StructureRotorArmWidthM) * modelScale;
        float armHeight = Math.Max(0.01f, profile.StructureRotorArmHeightM) * modelScale;
        float lampRadius = Math.Max(0.06f, profile.StructureLampLengthM * 0.5f) * modelScale;
        float lampThickness = Math.Max(0.008f, profile.StructureLampHeightM * 0.18f) * modelScale;

        DrawPrismWireframe(
            graphics,
            BuildEnergyPlatformFootprint(center, groundForward, groundRight, 0f, baseLength, baseDepth, 0.22f),
            baseHeight,
            baseColor,
            edgeColor,
            null);
        DrawPrismWireframe(
            graphics,
            BuildEnergyPlatformFootprint(center + worldUp * baseHeight, groundForward, groundRight, 0f, upperDeckLength, upperDeckDepth, 0.18f),
            upperDeckHeight,
            BlendColor(baseColor, Color.White, 0.06f),
            edgeColor,
            null);
        DrawPrismWireframe(
            graphics,
            BuildEnergyPlatformFootprint(center + worldUp * (baseHeight + upperDeckHeight), groundForward, groundRight, 0f, troughLength, troughDepth, 0.12f),
            0.08f * modelScale,
            frameColor,
            edgeColor,
            null);

        foreach ((float along, float side, Color stripColor) in new[]
        {
            (upperDeckLength * 0.38f, -upperDeckDepth * 0.42f, Color.FromArgb(255, 228, 76, 76)),
            (-upperDeckLength * 0.38f, upperDeckDepth * 0.42f, Color.FromArgb(255, 58, 112, 232)),
        })
        {
            Vector3 stripCenter = center
                + worldUp * (baseHeight + 0.16f * modelScale)
                + groundForward * along
                + groundRight * side;
            DrawOrientedBoxSolid(
                graphics,
                stripCenter,
                groundForward,
                groundRight,
                worldUp,
                upperDeckLength * 0.36f,
                0.018f * modelScale,
                0.012f * modelScale,
                stripColor,
                Color.FromArgb(255, BlendColor(stripColor, Color.Black, 0.25f)),
                null);
        }

        foreach (float side in new[] { -1f, 1f })
        {
            DrawOrientedBoxSolid(
                graphics,
                center + groundForward * (postOffsetZ * side) + worldUp * (baseHeight + postHeight * 0.5f),
                groundForward,
                groundRight,
                worldUp,
                postWidth,
                postWidth,
                postHeight,
                frameColor,
                edgeColor,
                null);
        }

        DrawOrientedBoxSolid(
            graphics,
            center + worldUp * (baseHeight + postHeight + topBeamHeight * 0.5f),
            groundForward,
            groundRight,
            worldUp,
            topBeamWidth,
            postWidth,
            topBeamHeight,
            frameColor,
            edgeColor,
            null);

        float rotorOrientationOffset = MathF.PI * 0.5f;
        float rotorYaw = rotorOrientationOffset + (float)_host.World.GameTimeSec * MathF.PI * 0.56f;

        foreach (float side in new[] { -1f, 1f })
        {
            float braceTop = side * postOffsetZ;
            float braceBottom = side * (postOffsetZ * 0.72f);
            DrawEnergyMechanismBrace(
                graphics,
                center + groundForward * braceTop + groundRight * (-rotorLayerOffset) + worldUp * (baseHeight + postHeight - topBeamHeight * 0.35f),
                center + groundForward * braceBottom + worldUp * (baseHeight + 0.20f * modelScale),
                0.040f * modelScale,
                0.040f * modelScale,
                frameColor,
                edgeColor);
            DrawEnergyMechanismBrace(
                graphics,
                center + groundForward * braceTop + groundRight * rotorLayerOffset + worldUp * (baseHeight + postHeight - topBeamHeight * 0.35f),
                center + groundForward * braceBottom + worldUp * (baseHeight + 0.20f * modelScale),
                0.040f * modelScale,
                0.040f * modelScale,
                frameColor,
                edgeColor);
        }

        foreach ((float layerOffset, float sideSign, Color rotorColor) in new[]
        {
            (-rotorLayerOffset, -1f, Color.FromArgb(255, 228, 76, 76)),
            (rotorLayerOffset, 1f, Color.FromArgb(255, 58, 112, 232)),
        })
        {
            Vector3 cantileverCenter = center
                + groundRight * layerOffset
                + groundForward * (sideSign * cantileverPairGap * 0.5f)
                + worldUp * cantileverCenterHeight;
            DrawEnergyMechanismHanger(
                graphics,
                cantileverCenter,
                groundRight,
                groundForward,
                worldUp,
                cantileverLength,
                cantileverHeight,
                cantileverDepth,
                rotorColor,
                edgeColor);
            DrawEnergyMechanismBrace(
                graphics,
                center + groundRight * layerOffset + groundForward * (sideSign * postOffsetZ) + worldUp * cantileverCenterHeight,
                cantileverCenter - groundForward * (sideSign * cantileverLength * 0.5f),
                cantileverHeight * 0.62f,
                cantileverDepth * 0.72f,
                frameColor,
                edgeColor);

            Vector3 rotorCenter = center + groundRight * layerOffset + worldUp * rotorCenterHeight;
            DrawCylinderSolid(graphics, rotorCenter, groundRight, worldUp, hubOuterRadius, 0.016f * modelScale, rotorYaw, Color.FromArgb(255, 54, 60, 68), edgeColor, 18);
            DrawCylinderSolid(graphics, rotorCenter, groundRight, worldUp, hubInnerRadius, 0.012f * modelScale, rotorYaw, rotorColor, edgeColor, 16);

            for (int index = 0; index < 5; index++)
            {
                float angle = rotorYaw + profile.StructureRotorPhaseDeg * MathF.PI / 180f + index * MathF.Tau / 5f;
                Vector3 armAxis = Vector3.Normalize(groundForward * MathF.Cos(angle) + worldUp * MathF.Sin(angle));
                Vector3 armUp = Vector3.Normalize(Vector3.Cross(groundRight, armAxis));
                Color currentLampColor = index == 0 ? BlendColor(rotorColor, Color.White, 0.25f) : lampColor;

                DrawEnergyMechanismArm(graphics, rotorCenter, armAxis, groundRight, armUp, armInnerOffset, armOuterRadius - armInnerOffset, armWidth, armHeight, frameColor, edgeColor);

                Vector3 podCenter = rotorCenter + armAxis * rotorRadius;
                Vector3 podAnchor = rotorCenter + armAxis * Math.Max(armOuterRadius, rotorRadius - lampRadius * 0.58f);
                DrawEnergyMechanismBrace(graphics, podAnchor + armUp * (0.05f * modelScale), podCenter + armUp * (0.03f * modelScale), 0.022f * modelScale, 0.022f * modelScale, frameColor, edgeColor);
                DrawEnergyMechanismBrace(graphics, podAnchor - armUp * (0.05f * modelScale), podCenter - armUp * (0.03f * modelScale), 0.022f * modelScale, 0.022f * modelScale, frameColor, edgeColor);
                DrawCylinderSolid(graphics, podCenter, groundRight, worldUp, lampRadius, 0.010f * modelScale, 0f, Color.FromArgb(255, 68, 72, 78), edgeColor, 24);
                DrawCylinderSolid(graphics, podCenter, groundRight, worldUp, lampRadius * 0.94f, Math.Max(0.004f * modelScale, lampThickness * 0.45f), 0f, currentLampColor, edgeColor, 24);
            }
        }

        return baseHeight + postHeight + rotorRadius + 0.34f * modelScale;
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
