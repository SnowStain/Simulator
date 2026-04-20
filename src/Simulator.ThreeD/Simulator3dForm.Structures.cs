using System.Numerics;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const float OutpostBaseWidthM = 0.65f;
    private const float OutpostBaseLiftM = 0.40f;
    private const float OutpostTopDiameterM = 0.55f;
    private const float OutpostTowerRadiusM = 0.40f;
    private const float OutpostBodyTopHeightM = 1.578f;
    private const float OutpostTowerHeightM = 1.878f;
    private const float OutpostLowerShoulderHeightM = 0.571f;
    private const float OutpostUpperShoulderHeightM = 1.446f;
    private const float OutpostRingSpeedRadPerSec = MathF.PI * 0.8f;
    private const float BaseDiagramLengthM = 1.881f;
    private const float BaseDiagramWidthM = 1.609f;
    private const float BaseDiagramHeightM = 1.181f;
    private const float BaseArmorOpenThresholdHealth = 2000f;
    private const float BaseTopArmorSlideAmplitudeM = 0.34f;
    private const float BaseTopArmorSlideSpeedRadPerSec = MathF.PI * 0.7f;
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
        Color bodyColor = entity.IsAlive
            ? TintProfileColor(profile.BodyColor, teamColor, 0.16f, true)
            : Color.FromArgb(84, 94, 108);
        Color edgeColor = Color.FromArgb(entity.IsAlive ? 252 : 216, BlendColor(bodyColor, Color.Black, 0.16f));
        Color darkBody = BlendColor(profile.WheelColor, teamColor, entity.IsAlive ? 0.10f : 0.02f);
        Color capColor = TintProfileColor(profile.TurretColor, teamColor, entity.IsAlive ? 0.18f : 0.02f, entity.IsAlive);
        float towerHeight = Math.Clamp(profile.BodyHeightM, 1.00f, 2.40f);
        float bodyTopHeight = Math.Min(towerHeight, OutpostBodyTopHeightM / OutpostTowerHeightM * towerHeight);
        float lowerShoulderHeight = OutpostLowerShoulderHeightM / OutpostTowerHeightM * towerHeight;
        float upperShoulderHeight = OutpostUpperShoulderHeightM / OutpostTowerHeightM * towerHeight;
        float baseWidth = Math.Clamp(profile.BodyLengthM, 0.40f, 0.95f);
        float topDiameter = Math.Clamp(profile.BodyWidthM, 0.28f, 0.80f);
        float towerRadius = Math.Max(0.22f, topDiameter * 0.72f);

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
                0.245f,
                0.245f,
                lowerShoulderHeight,
                upperShoulderHeight,
                Color.FromArgb(255, darkBody),
                Color.FromArgb(entity.IsAlive ? 248 : 216, BlendColor(darkBody, Color.Black, 0.22f)));

            DrawTaperedOutpostSection(
                graphics,
                center,
                yaw,
                0.285f,
                topDiameter * 0.5f,
                upperShoulderHeight,
                bodyTopHeight,
                Color.FromArgb(255, capColor),
                edgeColor);

            DrawTaperedOutpostSection(
                graphics,
                center,
                yaw + MathF.PI / 8f,
                topDiameter * 0.44f,
                0.155f,
                bodyTopHeight,
                towerHeight,
                Color.FromArgb(255, BlendColor(capColor, Color.White, 0.06f)),
                edgeColor);

            DrawOutpostRibs(graphics, center, yaw, entity, bodyColor, edgeColor);
        }

        if (renderPass != StructureRenderPass.StaticBody)
        {
            float ringYaw = (float)(entity.AngleDeg * Math.PI / 180.0) + (float)_host.World.GameTimeSec * OutpostRingSpeedRadPerSec;
            DrawCylinderSolid(
                graphics,
                center + new Vector3(0f, towerHeight - 0.10f, 0f),
                Vector3.UnitY,
                new Vector3(MathF.Cos(ringYaw), 0f, MathF.Sin(ringYaw)),
                towerRadius + 0.035f,
                0.030f,
                ringYaw,
                Color.FromArgb(255, BlendColor(bodyColor, Color.White, 0.08f)),
                Color.FromArgb(252, BlendColor(teamColor, Color.Black, 0.18f)),
                24);

            DrawCylinderSolid(
                graphics,
                center + new Vector3(0f, towerHeight - 0.10f, 0f),
                Vector3.UnitY,
                new Vector3(MathF.Cos(ringYaw), 0f, MathF.Sin(ringYaw)),
                Math.Max(0.04f, towerRadius - 0.17f),
                0.034f,
                ringYaw,
                Color.FromArgb(255, darkBody),
                Color.FromArgb(248, BlendColor(darkBody, Color.Black, 0.18f)),
                20);

            string? lockedPlateId = ResolveLockedPlateIdFor(entity);
            foreach (ArmorPlateTarget plate in SimulationCombatMath.GetArmorPlateTargets(entity, _host.World.MetersPerWorldUnit, _host.World.GameTimeSec))
            {
                DrawOutpostArmorPlate(graphics, entity, plate, string.Equals(lockedPlateId, plate.Id, StringComparison.OrdinalIgnoreCase));
            }
        }

        return baseLiftM + towerHeight + 0.10f;
    }

    private void DrawOutpostArmorPlate(Graphics graphics, SimulationEntity entity, ArmorPlateTarget plate, bool locked)
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
        Vector3 tiltedAxis = Vector3.Normalize(topForward + Vector3.UnitY);
        Vector3 plateNormal = Vector3.Normalize(Vector3.Cross(topSide, tiltedAxis));
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
        Color bodyColor = entity.IsAlive
            ? TintProfileColor(profile.BodyColor, teamColor, 0.12f, true)
            : Color.FromArgb(84, 94, 108);
        Color armorColor = entity.IsAlive
            ? TintProfileColor(profile.ArmorColor, teamColor, 0.20f, true)
            : Color.FromArgb(92, 98, 108);
        Color darkColor = BlendColor(profile.WheelColor, Color.FromArgb(36, 40, 46), 0.45f);
        Color edgeColor = Color.FromArgb(entity.IsAlive ? 252 : 216, BlendColor(bodyColor, Color.Black, 0.18f));

        float baseLength = Math.Clamp(profile.BodyLengthM > 1e-4f ? profile.BodyLengthM : BaseDiagramLengthM, 1.10f, 2.35f);
        float baseWidth = Math.Clamp((profile.BodyWidthM > 1e-4f ? profile.BodyWidthM : BaseDiagramWidthM) * Math.Max(0.4f, profile.BodyRenderWidthScale), 0.90f, 2.05f);
        float baseHeight = Math.Clamp(profile.BodyHeightM > 1e-4f ? profile.BodyHeightM : BaseDiagramHeightM, 0.70f, 1.60f);
        float slabHeight = Math.Min(0.20f, baseHeight * 0.22f);
        float shoulderHeight = Math.Min(baseHeight * 0.73f, 0.86f);

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
                baseHeight,
                Color.FromArgb(255, BlendColor(bodyColor, Color.White, 0.08f)),
                edgeColor);

            DrawBaseFixedDetails(graphics, center, yaw, entity, profile, bodyColor, armorColor, darkColor, edgeColor, baseLength, baseWidth, baseHeight);

            DrawOrientedBoxSolid(
                graphics,
                center + forward * (baseLength * 0.05f) + Vector3.UnitY * (baseHeight + 0.045f),
                right,
                forward,
                Vector3.UnitY,
                baseWidth * 0.72f,
                0.060f,
                0.050f,
                Color.FromArgb(255, darkColor),
                Color.FromArgb(248, BlendColor(darkColor, Color.Black, 0.20f)),
                null);
        }

        if (renderPass != StructureRenderPass.StaticBody)
        {
            string? lockedPlateId = ResolveLockedPlateIdFor(entity);
            float slideM = ResolveBaseTopArmorSlideM(_host.World.GameTimeSec);
            Vector3 topPlateCenter = center
                + forward * (baseLength * 0.06f)
                + right * slideM
                + Vector3.UnitY * (baseHeight + 0.10f);
            Color topPlateColor = string.Equals(lockedPlateId, "base_top_slide", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(255, 255, 211, 84)
                : Color.FromArgb(255, armorColor);
            float plateSide = ResolveStructureArmorPlateSideM(entity);
            float plateThickness = ResolveStructureArmorPlateThicknessM(entity);
            DrawOrientedBoxSolid(
                graphics,
                topPlateCenter,
                forward,
                right,
                Vector3.UnitY,
                plateSide,
                plateSide,
                plateThickness,
                topPlateColor,
                Color.FromArgb(255, BlendColor(topPlateColor, Color.Black, 0.18f)),
                null);

            if (entity.Health < BaseArmorOpenThresholdHealth)
            {
                DrawBaseExpandedArmor(graphics, center, yaw, entity, profile, baseLength, baseWidth, baseHeight, armorColor, edgeColor, lockedPlateId);
            }
        }

        return baseHeight + 0.20f;
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
