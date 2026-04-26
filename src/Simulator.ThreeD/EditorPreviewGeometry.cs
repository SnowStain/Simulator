using System.Drawing;
using System.Linq;
using System.Numerics;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal static class EditorPreviewGeometry
{
    private readonly record struct PreviewBalanceLegGeometry(
        Vector2 UpperFront,
        Vector2 UpperRear,
        Vector2 KneeCenter,
        Vector2 KneeFront,
        Vector2 KneeRear,
        Vector2 Foot,
        float SideOffset,
        float HingeRadius);

    public static void AppendAppearancePreview(ICollection<Preview3dFace> faces, RobotAppearanceProfile profile)
    {
        string roleKey = string.IsNullOrWhiteSpace(profile.RoleKey) ? "infantry" : profile.RoleKey;
        if (string.Equals(roleKey, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            AppendEnergyMechanismPreview(faces, profile, Vector3.Zero);
            return;
        }

        if (string.Equals(roleKey, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            AppendOutpostPreview(faces, profile, Vector3.Zero);
            return;
        }

        if (string.Equals(roleKey, "base", StringComparison.OrdinalIgnoreCase))
        {
            AppendBasePreview(faces, profile, Vector3.Zero);
            return;
        }

        AppendMobileRobotPreview(faces, profile, Vector3.Zero);
    }

    public static void AppendFacilityStructurePreview(
        ICollection<Preview3dFace> faces,
        FacilityRegion region,
        RobotAppearanceProfile profile,
        Vector3 center)
    {
        if (string.Equals(region.Type, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            AppendEnergyMechanismPreview(faces, profile, center);
            return;
        }

        if (string.Equals(region.Type, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            AppendOutpostPreview(faces, profile, center);
            return;
        }

        if (string.Equals(region.Type, "base", StringComparison.OrdinalIgnoreCase))
        {
            AppendBasePreview(faces, profile, center);
            return;
        }

        AppendMobileRobotPreview(faces, profile, center);
    }

    public static (Vector3 Min, Vector3 Max) MeasureBounds(IReadOnlyCollection<Preview3dFace> faces)
    {
        if (faces.Count == 0)
        {
            return (new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 1f, 0.5f));
        }

        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (Preview3dFace face in faces)
        {
            foreach (Vector3 vertex in face.Vertices)
            {
                if (!Preview3dPrimitives.IsFinite(vertex))
                {
                    continue;
                }

                min = Vector3.Min(min, vertex);
                max = Vector3.Max(max, vertex);
            }
        }

        if (!Preview3dPrimitives.IsFinite(min) || !Preview3dPrimitives.IsFinite(max))
        {
            return (new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 1f, 0.5f));
        }

        if (max.Y - min.Y < 0.1f)
        {
            max.Y = min.Y + 0.1f;
        }

        return (min, max);
    }

    private static void AppendMobileRobotPreview(ICollection<Preview3dFace> faces, RobotAppearanceProfile profile, Vector3 offset)
    {
        float bodyLength = Math.Max(0.12f, profile.BodyLengthM);
        float bodyWidth = Math.Max(0.10f, profile.BodyWidthM * Math.Max(0.4f, profile.BodyRenderWidthScale));
        float bodyHeight = Math.Max(0.08f, profile.BodyHeightM);
        float bodyBase = Math.Max(0f, profile.BodyClearanceM);
        float bodyCenterY = bodyBase + bodyHeight * 0.5f;
        Color bodyColor = profile.BodyColor;
        Color turretColor = profile.TurretColor;
        Color armorColor = profile.ArmorColor;
        Color wheelColor = profile.WheelColor;

        if (string.Equals(profile.BodyShape, "octagon", StringComparison.OrdinalIgnoreCase))
        {
            float halfLength = bodyLength * 0.5f;
            float halfWidth = bodyWidth * 0.5f;
            float corner = Math.Min(halfLength * 0.45f, halfWidth * 0.45f);
            var bottom = new[]
            {
                offset + new Vector3(-halfLength + corner, bodyBase, -halfWidth),
                offset + new Vector3(halfLength - corner, bodyBase, -halfWidth),
                offset + new Vector3(halfLength, bodyBase, -halfWidth + corner),
                offset + new Vector3(halfLength, bodyBase, halfWidth - corner),
                offset + new Vector3(halfLength - corner, bodyBase, halfWidth),
                offset + new Vector3(-halfLength + corner, bodyBase, halfWidth),
                offset + new Vector3(-halfLength, bodyBase, halfWidth - corner),
                offset + new Vector3(-halfLength, bodyBase, -halfWidth + corner),
            };
            var top = bottom.Select(point => point + Vector3.UnitY * bodyHeight).ToArray();
            Preview3dPrimitives.AddPrism(faces, bottom, top, bodyColor);
        }
        else
        {
            Preview3dPrimitives.AddBox(
                faces,
                offset + new Vector3(0f, bodyCenterY, 0f),
                new Vector3(bodyLength * 0.5f, bodyHeight * 0.5f, bodyWidth * 0.5f),
                bodyColor);
        }

        float capHeight = Math.Max(0.015f, bodyHeight * 0.12f);
        Preview3dPrimitives.AddBox(
            faces,
            offset + new Vector3(0f, bodyBase + bodyHeight * 0.72f + capHeight * 0.5f, 0f),
            new Vector3(bodyLength * 0.40f, capHeight * 0.5f, bodyWidth * 0.40f),
            Preview3dPrimitives.Multiply(bodyColor, 1.08f));

        PreviewBalanceLegGeometry? balanceLeg = string.Equals(profile.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase)
            ? ResolvePreviewBalanceLegGeometry(profile)
            : null;
        HashSet<int> dynamicWheelIndices = ResolveDynamicWheelIndices(profile, balanceLeg is not null);
        for (int wheelIndex = 0; wheelIndex < profile.WheelOffsetsM.Count; wheelIndex++)
        {
            Vector2 wheelOffset = profile.WheelOffsetsM[wheelIndex];
            float radius = Math.Max(0.03f, Math.Max(profile.WheelRadiusM, profile.RearLegWheelRadiusM));
            Vector3 wheelCenter = offset + new Vector3(wheelOffset.X, radius, wheelOffset.Y);
            if (balanceLeg is not null && dynamicWheelIndices.Contains(wheelIndex))
            {
                float sideSign = wheelOffset.Y < 0f ? -1f : 1f;
                if (Math.Abs(wheelOffset.Y) <= 1e-4f)
                {
                    sideSign = wheelIndex % 2 == 0 ? -1f : 1f;
                }

                radius = Math.Clamp(profile.RearLegWheelRadiusM, 0.03f, 0.32f);
                wheelCenter = offset + new Vector3(
                    balanceLeg.Value.Foot.X,
                    balanceLeg.Value.Foot.Y,
                    balanceLeg.Value.SideOffset * sideSign);
            }

            Preview3dPrimitives.AddCylinder(
                faces,
                wheelCenter,
                Vector3.UnitZ,
                Vector3.UnitY,
                radius,
                Math.Max(0.018f, radius * 0.68f),
                wheelColor,
                14);
        }

        if (!string.Equals(profile.FrontClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            float plateCenterX = bodyLength * 0.5f + profile.FrontClimbAssistForwardOffsetM + profile.FrontClimbAssistBottomLengthM * 0.32f;
            float plateCenterY = Math.Max(profile.WheelRadiusM, profile.RearLegWheelRadiusM) + profile.FrontClimbAssistPlateHeightM * 0.5f;
            float sideOffset = Math.Max(bodyWidth * 0.25f, bodyWidth * 0.5f - profile.FrontClimbAssistInnerOffsetM);
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                float sideSign = sideIndex == 0 ? -1f : 1f;
                Preview3dPrimitives.AddBox(
                    faces,
                    offset + new Vector3(plateCenterX, plateCenterY, sideOffset * sideSign),
                    new Vector3(
                        Math.Max(0.015f, profile.FrontClimbAssistBottomLengthM * 0.5f),
                        Math.Max(0.02f, profile.FrontClimbAssistPlateHeightM * 0.5f),
                        Math.Max(0.004f, profile.FrontClimbAssistPlateWidthM * 0.5f)),
                    Color.FromArgb(92, 96, 108));
            }
        }

        if (!string.Equals(profile.RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (balanceLeg is not null)
            {
                AppendBalanceLegPreview(faces, profile, offset, bodyWidth, balanceLeg.Value);
            }
            else
            {
                float sideOffset = Math.Max(bodyWidth * 0.22f, bodyWidth * 0.5f - profile.RearClimbAssistInnerOffsetM);
                float mountX = -bodyLength * 0.5f + profile.RearClimbAssistMountOffsetXM;
                float mountY = Math.Max(bodyBase + bodyHeight * 0.55f, profile.RearClimbAssistMountHeightM);
                float jointX = mountX - Math.Max(0.02f, profile.RearClimbAssistUpperLengthM * 0.55f);
                float jointY = mountY - Math.Max(0.02f, profile.RearClimbAssistUpperLengthM * 0.45f);
                float footX = jointX - Math.Max(0.02f, profile.RearClimbAssistLowerLengthM * 0.65f);
                float footY = Math.Max(profile.RearLegWheelRadiusM, 0.03f);
                for (int sideIndex = 0; sideIndex < 2; sideIndex++)
                {
                    float sideSign = sideIndex == 0 ? -1f : 1f;
                    Vector3 mount = offset + new Vector3(mountX, mountY, sideOffset * sideSign);
                    Vector3 joint = offset + new Vector3(jointX, jointY, sideOffset * sideSign);
                    Vector3 foot = offset + new Vector3(footX, footY, sideOffset * sideSign);
                    AddBeam(
                        faces,
                        mount,
                        joint,
                        Math.Max(0.008f, profile.RearClimbAssistUpperWidthM),
                        Math.Max(0.010f, profile.RearClimbAssistUpperHeightM),
                        Color.FromArgb(110, 118, 132));
                    AddBeam(
                        faces,
                        joint,
                        foot,
                        Math.Max(0.008f, profile.RearClimbAssistLowerWidthM),
                        Math.Max(0.010f, profile.RearClimbAssistLowerHeightM),
                        Color.FromArgb(96, 104, 118));
                }
            }
        }

        if (profile.GimbalMountGapM + profile.GimbalMountHeightM > 0.02f)
        {
            float mountHeight = Math.Max(0.02f, profile.GimbalMountGapM + profile.GimbalMountHeightM + 0.08f);
            Preview3dPrimitives.AddBox(
                faces,
                offset + new Vector3(
                    profile.GimbalOffsetXM,
                    bodyBase + bodyHeight + mountHeight * 0.5f,
                    profile.GimbalOffsetYM),
                new Vector3(
                    Math.Max(0.02f, profile.GimbalMountLengthM * 0.5f),
                    mountHeight * 0.5f,
                    Math.Max(0.02f, profile.GimbalMountWidthM * Math.Max(0.4f, profile.BodyRenderWidthScale) * 0.5f)),
                Preview3dPrimitives.Multiply(bodyColor, 0.86f));
        }

        if (profile.GimbalLengthM > 0.04f && profile.GimbalWidthM > 0.04f && profile.GimbalBodyHeightM > 0.02f)
        {
            float turretBase = Math.Max(bodyBase + bodyHeight + profile.GimbalMountGapM + profile.GimbalMountHeightM, profile.GimbalHeightM - profile.GimbalBodyHeightM * 0.5f);
            Vector3 hingeCenter = offset + new Vector3(profile.GimbalOffsetXM, turretBase, profile.GimbalOffsetYM);
            Preview3dPrimitives.AddCylinder(
                faces,
                hingeCenter,
                Vector3.UnitZ,
                Vector3.UnitY,
                Math.Max(0.014f, profile.GimbalBodyHeightM * 0.18f),
                Math.Max(0.018f, profile.GimbalWidthM * Math.Max(0.4f, profile.BodyRenderWidthScale) * 0.55f),
                Preview3dPrimitives.Multiply(turretColor, 0.92f),
                12);

            Vector3 turretCenter = hingeCenter + new Vector3(
                profile.GimbalLengthM * 0.04f,
                profile.GimbalBodyHeightM * 0.56f + 0.01f,
                0f);
            Preview3dPrimitives.AddBox(
                faces,
                turretCenter,
                new Vector3(
                    profile.GimbalLengthM * 0.5f,
                    profile.GimbalBodyHeightM * 0.5f,
                    profile.GimbalWidthM * Math.Max(0.4f, profile.BodyRenderWidthScale) * 0.5f),
                turretColor);

            if (profile.BarrelLengthM > 0.04f && profile.BarrelRadiusM > 0.004f)
            {
                Vector3 barrelCenter = hingeCenter + new Vector3(
                    profile.GimbalLengthM * 0.5f + profile.BarrelLengthM * 0.5f,
                    profile.GimbalBodyHeightM * 0.18f,
                    0f);
                Preview3dPrimitives.AddCylinder(
                    faces,
                    barrelCenter,
                    Vector3.UnitX,
                    Vector3.UnitY,
                    Math.Max(0.006f, profile.BarrelRadiusM),
                    profile.BarrelLengthM,
                    Color.FromArgb(226, 232, 238),
                    12);
            }
        }

        IReadOnlyList<float> orbitYaws = profile.ArmorOrbitYawsDeg.Count > 0
            ? profile.ArmorOrbitYawsDeg
            : new[] { 0f, 180f, 90f, 270f };
        IReadOnlyList<float> selfYaws = profile.ArmorSelfYawsDeg.Count == orbitYaws.Count
            ? profile.ArmorSelfYawsDeg
            : orbitYaws;
        float armorDistanceX = bodyLength * 0.5f + profile.ArmorPlateGapM + profile.ArmorPlateWidthM * 0.5f;
        float armorDistanceZ = bodyWidth * 0.5f + profile.ArmorPlateGapM + profile.ArmorPlateWidthM * 0.5f;
        for (int index = 0; index < orbitYaws.Count; index++)
        {
            float yaw = orbitYaws[index] * MathF.PI / 180f;
            float selfYaw = selfYaws[Math.Min(index, selfYaws.Count - 1)] * MathF.PI / 180f;
            Vector3 armorCenter = offset + new Vector3(
                MathF.Cos(yaw) * armorDistanceX,
                bodyCenterY,
                MathF.Sin(yaw) * armorDistanceZ);
            Preview3dPrimitives.AddBox(
                faces,
                armorCenter,
                new Vector3(
                    Math.Max(0.004f, profile.ArmorPlateGapM * 0.5f),
                    Math.Max(0.02f, profile.ArmorPlateHeightM * 0.5f),
                    Math.Max(0.02f, profile.ArmorPlateLengthM * 0.5f)),
                Color.FromArgb(42, 46, 54),
                -selfYaw);
            Preview3dPrimitives.AddBox(
                faces,
                armorCenter,
                new Vector3(
                    Math.Max(0.003f, profile.ArmorPlateGapM * 0.35f),
                    Math.Max(0.02f, profile.ArmorPlateHeightM * 0.45f),
                    Math.Max(0.02f, profile.ArmorPlateLengthM * 0.46f)),
                armorColor,
                -selfYaw);
        }
    }

    private static void AppendOutpostPreview(ICollection<Preview3dFace> faces, RobotAppearanceProfile profile, Vector3 offset)
    {
        float baseLift = profile.StructureBaseLiftM > 1e-4f ? profile.StructureBaseLiftM : 0.40f;
        float towerHeight = Math.Clamp(profile.BodyHeightM, 1.00f, 2.40f);
        float bodyTopHeight = Math.Clamp(profile.StructureBodyTopHeightM, 0.12f, towerHeight);
        float lowerShoulderHeight = Math.Clamp(profile.StructureLowerShoulderHeightM, 0.08f, bodyTopHeight);
        float upperShoulderHeight = Math.Clamp(profile.StructureUpperShoulderHeightM, bodyTopHeight, towerHeight);
        float headBaseHeight = Math.Clamp(profile.StructureHeadBaseHeightM, upperShoulderHeight, towerHeight + 0.10f);
        float baseWidth = Math.Clamp(profile.BodyLengthM, 0.40f, 0.95f);
        float topDiameter = Math.Clamp(profile.BodyWidthM, 0.28f, 0.80f);
        float towerRadius = Math.Clamp(profile.StructureTowerRadiusM > 1e-4f ? profile.StructureTowerRadiusM : topDiameter * 0.36f, 0.12f, 0.34f);
        Color bodyColor = profile.BodyColor;
        Color darkBody = Preview3dPrimitives.Multiply(profile.BodyColor, 0.84f);
        Color capColor = Preview3dPrimitives.Multiply(profile.TurretColor, 1.02f);

        Preview3dPrimitives.AddBox(
            faces,
            offset + new Vector3(0f, baseLift + 0.042f, 0f),
            new Vector3(baseWidth * 0.50f, 0.042f, baseWidth * 0.50f),
            Preview3dPrimitives.Multiply(bodyColor, 0.92f),
            MathF.PI * 0.25f);
        AddRegularPolygonPrism(faces, offset, baseWidth * 0.46f, 0.255f, baseLift + 0.085f, baseLift + lowerShoulderHeight, bodyColor, 8, MathF.PI * 0.25f);
        AddRegularPolygonPrism(faces, offset, 0.205f, 0.175f, baseLift + lowerShoulderHeight, baseLift + bodyTopHeight, darkBody, 8, MathF.PI * 0.125f);
        AddRegularPolygonPrism(faces, offset, 0.220f, 0.165f, baseLift + bodyTopHeight, baseLift + upperShoulderHeight, capColor, 8, 0f);
        AddRegularPolygonPrism(faces, offset, 0.165f, 0.120f, baseLift + upperShoulderHeight, baseLift + headBaseHeight, Preview3dPrimitives.Multiply(capColor, 1.06f), 8, MathF.PI * 0.125f);

        Preview3dPrimitives.AddBox(
            faces,
            offset + new Vector3(0f, baseLift + headBaseHeight + 0.05f, 0f),
            new Vector3(0.00f, 0.05f, 0.07f),
            darkBody);
        Preview3dPrimitives.AddBox(
            faces,
            offset + new Vector3(0.03f, baseLift + towerHeight - 0.05f, 0f),
            new Vector3(0.105f, 0.06f, 0.09f),
            capColor);

        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float sideSign = sideIndex == 0 ? -1f : 1f;
            Preview3dPrimitives.AddBox(
                faces,
                offset + new Vector3(0f, baseLift + headBaseHeight + 0.03f, sideSign * 0.25f),
                new Vector3(0.05f, 0.11f, 0.008f),
                darkBody);
        }

        IReadOnlyList<float> orbitYaws = profile.ArmorOrbitYawsDeg.Count > 0
            ? profile.ArmorOrbitYawsDeg
            : new[] { 0f, 90f, 180f, 270f };
        foreach (float orbitYawDeg in orbitYaws)
        {
            float yaw = orbitYawDeg * MathF.PI / 180f;
            Preview3dPrimitives.AddBox(
                faces,
                offset + new Vector3(
                    MathF.Cos(yaw) * (towerRadius + 0.11f),
                    baseLift + headBaseHeight - 0.07f,
                    MathF.Sin(yaw) * (towerRadius + 0.11f)),
                new Vector3(
                    Math.Max(0.01f, profile.ArmorPlateGapM * 0.5f),
                    Math.Max(0.04f, profile.ArmorPlateWidthM * 0.5f),
                    Math.Max(0.04f, profile.ArmorPlateHeightM * 0.5f)),
                profile.ArmorColor,
                -yaw);
        }

        float topHeight = profile.StructureTopArmorCenterHeightM > 1e-4f ? profile.StructureTopArmorCenterHeightM : baseLift + towerHeight + 0.06f;
        float topZ = profile.StructureTopArmorOffsetZM;
        float topYaw = (profile.StructureTopArmorTiltDeg > 1e-4f ? profile.StructureTopArmorTiltDeg : 45f) * MathF.PI / 180f;
        Preview3dPrimitives.AddBox(
            faces,
            offset + new Vector3(profile.StructureTopArmorOffsetXM, topHeight, topZ),
            new Vector3(
                Math.Max(0.04f, profile.ArmorPlateWidthM * 0.5f),
                Math.Max(0.01f, profile.ArmorPlateGapM * 0.35f),
                Math.Max(0.04f, profile.ArmorPlateWidthM * 0.5f)),
            profile.ArmorColor,
            topYaw);
    }

    private static void AppendBasePreview(ICollection<Preview3dFace> faces, RobotAppearanceProfile profile, Vector3 offset)
    {
        float baseLength = Math.Clamp(profile.BodyLengthM > 1e-4f ? profile.BodyLengthM : 1.881f, 1.10f, 2.35f);
        float baseWidth = Math.Clamp((profile.BodyWidthM > 1e-4f ? profile.BodyWidthM : 1.609f) * Math.Max(0.4f, profile.BodyRenderWidthScale), 0.90f, 2.05f);
        float baseHeight = Math.Clamp(profile.BodyHeightM > 1e-4f ? profile.BodyHeightM : 1.181f, 0.70f, 1.60f);
        float roofHeight = Math.Clamp(profile.StructureRoofHeightM > 1e-4f ? profile.StructureRoofHeightM : baseHeight * 0.87f, 0.45f, baseHeight - 0.02f);
        float slabHeight = Math.Min(0.20f, baseHeight * 0.22f);
        float shoulderHeight = Math.Clamp(
            profile.StructureShoulderHeightM > 1e-4f ? profile.StructureShoulderHeightM : baseHeight * 0.728f,
            slabHeight + 0.12f,
            roofHeight - 0.05f);
        Color bodyColor = profile.BodyColor;
        Color darkColor = Preview3dPrimitives.Multiply(bodyColor, 0.84f);
        Color armorColor = profile.ArmorColor;
        float topEdge = Math.Clamp(profile.StructureHexTopEdgeM > 1e-4f ? profile.StructureHexTopEdgeM : baseLength * 0.58f, 0.05f, baseLength * 0.92f);

        AddHexPrism(faces, offset, profile, baseLength, baseWidth, topEdge, 0f, slabHeight, Preview3dPrimitives.Multiply(darkColor, 1.04f));
        AddHexPrism(faces, offset, profile, baseLength * 0.90f, baseWidth * 0.88f, topEdge * 0.90f, slabHeight, shoulderHeight, bodyColor, baseLength * 0.62f, baseWidth * 0.56f, topEdge * 0.62f);
        AddHexPrism(faces, offset, profile, baseLength * 0.62f, baseWidth * 0.56f, topEdge * 0.62f, shoulderHeight, roofHeight, Preview3dPrimitives.Multiply(bodyColor, 1.08f), baseLength * 0.40f, baseWidth * 0.34f, topEdge * 0.40f);

        Preview3dPrimitives.AddBox(
            faces,
            offset + new Vector3(0f, baseHeight * 0.58f, 0f),
            new Vector3(0.055f, Math.Min(baseHeight * 0.33f, Math.Max(0.05f, profile.StructureCoreColumnHeightM)) * 0.5f, 0.06f),
            Preview3dPrimitives.Multiply(bodyColor, 0.86f));

        Preview3dPrimitives.AddBox(
            faces,
            offset + new Vector3(0.02f, profile.StructureDetectorBridgeCenterHeightM > 1e-4f ? profile.StructureDetectorBridgeCenterHeightM : baseHeight * 0.925f, 0f),
            new Vector3(0.04f, 0.022f, Math.Min(Math.Max(0.08f, profile.StructureDetectorWidthM) * 0.5f, baseWidth * 0.30f)),
            darkColor);
        Preview3dPrimitives.AddBox(
            faces,
            offset + new Vector3(0f, profile.StructureDetectorSensorCenterHeightM > 1e-4f ? profile.StructureDetectorSensorCenterHeightM : baseHeight * 0.962f, 0f),
            new Vector3(0.03f, Math.Max(0.030f, Math.Max(0.02f, profile.StructureDetectorHeightM) * 0.5f), 0.03f),
            Color.FromArgb(96, 255, 130));

        Preview3dPrimitives.AddBox(
            faces,
            offset + new Vector3(baseLength * 0.04f + profile.StructureTopArmorOffsetXM, profile.StructureTopArmorCenterHeightM > 1e-4f ? profile.StructureTopArmorCenterHeightM : baseHeight * 0.974f, profile.StructureTopArmorOffsetZM),
            new Vector3(
                Math.Max(0.04f, profile.ArmorPlateWidthM * 0.5f),
                Math.Max(0.01f, profile.ArmorPlateGapM * 0.35f),
                Math.Max(0.04f, profile.ArmorPlateWidthM * 0.5f)),
            armorColor,
            -(profile.StructureTopArmorTiltDeg > 1e-4f ? profile.StructureTopArmorTiltDeg : 27.5f) * MathF.PI / 180f);

        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float sideSign = sideIndex == 0 ? -1f : 1f;
            Preview3dPrimitives.AddBox(
                faces,
                offset + new Vector3(-baseLength * 0.07f, baseHeight * 0.44f, sideSign * baseWidth * 0.43f),
                new Vector3(baseLength * 0.18f, baseHeight * 0.24f, 0.035f),
                Preview3dPrimitives.Multiply(armorColor, 0.96f));
            Preview3dPrimitives.AddBox(
                faces,
                offset + new Vector3(-baseLength * 0.06f, baseHeight * 0.62f, sideSign * baseWidth * 0.31f),
                new Vector3(baseLength * 0.13f, baseHeight * 0.15f, 0.010f),
                Color.FromArgb(255, 40, 40));
        }
    }

    private static void AppendEnergyMechanismPreview(ICollection<Preview3dFace> faces, RobotAppearanceProfile profile, Vector3 offset)
    {
        EnergyRenderMesh mesh = EnergyMechanismGeometry.BuildPreviewPair(profile, 0.0f);
        foreach (EnergyRenderPrism prism in mesh.Prisms)
        {
            Preview3dPrimitives.AddPrism(
                faces,
                prism.Bottom.Select(point => point + offset).ToArray(),
                prism.Top.Select(point => point + offset).ToArray(),
                prism.FillColor);
        }

        foreach (EnergyRenderBox box in mesh.Boxes)
        {
            Preview3dPrimitives.AddOrientedBox(
                faces,
                box.Center + offset,
                box.Forward,
                box.Right,
                box.Up,
                box.Length,
                box.Width,
                box.Height,
                box.FillColor);
        }

        foreach (EnergyRenderCylinder cylinder in mesh.Cylinders)
        {
            Preview3dPrimitives.AddCylinder(
                faces,
                cylinder.Center + offset,
                cylinder.NormalAxis,
                cylinder.UpAxis,
                cylinder.Radius,
                cylinder.Thickness,
                cylinder.FillColor,
                cylinder.Segments);
        }
    }

    private static void AddHexPrism(
        ICollection<Preview3dFace> faces,
        Vector3 center,
        RobotAppearanceProfile profile,
        float bottomLength,
        float bottomWidth,
        float bottomShortEdge,
        float bottomHeight,
        float topHeight,
        Color color,
        float? topLength = null,
        float? topWidth = null,
        float? topShortEdge = null)
    {
        IReadOnlyList<Vector3> bottom = BuildBaseHexagonalFootprint(center, profile, bottomLength, bottomWidth, bottomShortEdge, bottomHeight);
        IReadOnlyList<Vector3> top = BuildBaseHexagonalFootprint(
            center,
            profile,
            topLength ?? bottomLength * 0.96f,
            topWidth ?? bottomWidth * 0.94f,
            topShortEdge ?? bottomShortEdge * 0.96f,
            topHeight);
        Preview3dPrimitives.AddPrism(faces, bottom, top, color);
    }

    private static IReadOnlyList<Vector3> BuildBaseHexagonalFootprint(
        Vector3 center,
        RobotAppearanceProfile profile,
        float length,
        float width,
        float shortEdge,
        float height)
    {
        float halfLength = Math.Max(0.05f, length * 0.5f);
        float halfWidth = Math.Max(0.05f, width * 0.5f);
        float clampedShortEdge = Math.Clamp(shortEdge, 0.05f, length * 0.92f);
        float cornerX = MathF.Min(halfLength * 0.92f, clampedShortEdge * 0.5f);
        Vector2[] local =
        {
            new(-cornerX, -halfWidth),
            new(cornerX, -halfWidth),
            new(halfLength, 0f),
            new(cornerX, halfWidth),
            new(-cornerX, halfWidth),
            new(-halfLength, 0f),
        };

        Vector3[] result = new Vector3[local.Length];
        for (int index = 0; index < local.Length; index++)
        {
            result[index] = center + new Vector3(local[index].X, height, local[index].Y);
        }

        return result;
    }

    private static void AddRegularPolygonPrism(
        ICollection<Preview3dFace> faces,
        Vector3 center,
        float bottomRadius,
        float topRadius,
        float bottomHeight,
        float topHeight,
        Color color,
        int sides,
        float yaw)
    {
        IReadOnlyList<Vector3> bottom = BuildRegularPolygonFootprint(center, Math.Max(0.02f, bottomRadius), bottomHeight, yaw, sides);
        IReadOnlyList<Vector3> top = BuildRegularPolygonFootprint(center, Math.Max(0.02f, topRadius), topHeight, yaw, sides);
        Preview3dPrimitives.AddPrism(faces, bottom, top, color);
    }

    private static IReadOnlyList<Vector3> BuildRegularPolygonFootprint(
        Vector3 center,
        float radius,
        float height,
        float yaw,
        int sides)
    {
        int pointCount = Math.Max(4, sides);
        Vector3[] result = new Vector3[pointCount];
        for (int index = 0; index < pointCount; index++)
        {
            float angle = yaw + index * MathF.Tau / pointCount;
            result[index] = new Vector3(
                center.X + MathF.Cos(angle) * radius,
                center.Y + height,
                center.Z + MathF.Sin(angle) * radius);
        }

        return result;
    }

    private static HashSet<int> ResolveDynamicWheelIndices(RobotAppearanceProfile profile, bool hasBalanceLeg)
    {
        HashSet<int> dynamicIndices = new();
        if (!hasBalanceLeg)
        {
            return dynamicIndices;
        }

        if (string.Equals(profile.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase)
            && profile.WheelOffsetsM.Count <= 2)
        {
            for (int index = 0; index < profile.WheelOffsetsM.Count; index++)
            {
                dynamicIndices.Add(index);
            }

            return dynamicIndices;
        }

        int leftRearIndex = -1;
        int rightRearIndex = -1;
        float leftMostX = float.MaxValue;
        float rightMostX = float.MaxValue;
        for (int index = 0; index < profile.WheelOffsetsM.Count; index++)
        {
            Vector2 offset = profile.WheelOffsetsM[index];
            if (offset.Y < 0f && offset.X < leftMostX)
            {
                leftMostX = offset.X;
                leftRearIndex = index;
            }

            if (offset.Y > 0f && offset.X < rightMostX)
            {
                rightMostX = offset.X;
                rightRearIndex = index;
            }
        }

        if (leftRearIndex >= 0)
        {
            dynamicIndices.Add(leftRearIndex);
        }

        if (rightRearIndex >= 0)
        {
            dynamicIndices.Add(rightRearIndex);
        }

        return dynamicIndices;
    }

    private static void AppendBalanceLegPreview(
        ICollection<Preview3dFace> faces,
        RobotAppearanceProfile profile,
        Vector3 offset,
        float bodyWidth,
        PreviewBalanceLegGeometry leg)
    {
        float bodySideOffset = Math.Max(0.02f, bodyWidth * 0.5f * 0.98f);
        Color mountColor = Color.FromArgb(126, 136, 148);
        Color upperColor = Color.FromArgb(110, 118, 132);
        Color lowerColor = Color.FromArgb(96, 104, 118);
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float sideSign = sideIndex == 0 ? -1f : 1f;
            float sideOffset = leg.SideOffset * sideSign;
            float mountSideOffset = bodySideOffset * sideSign;
            Vector3 mountFront = offset + new Vector3(leg.UpperFront.X, leg.UpperFront.Y, mountSideOffset);
            Vector3 mountRear = offset + new Vector3(leg.UpperRear.X, leg.UpperRear.Y, mountSideOffset);
            Vector3 upperFront = offset + new Vector3(leg.UpperFront.X, leg.UpperFront.Y, sideOffset);
            Vector3 upperRear = offset + new Vector3(leg.UpperRear.X, leg.UpperRear.Y, sideOffset);
            Vector3 kneeFront = offset + new Vector3(leg.KneeFront.X, leg.KneeFront.Y, sideOffset);
            Vector3 kneeRear = offset + new Vector3(leg.KneeRear.X, leg.KneeRear.Y, sideOffset);
            Vector3 kneeCenter = offset + new Vector3(leg.KneeCenter.X, leg.KneeCenter.Y, sideOffset);
            Vector3 foot = offset + new Vector3(leg.Foot.X, leg.Foot.Y, sideOffset);
            Vector3 wheelAnchor = offset + new Vector3(leg.Foot.X, leg.Foot.Y, leg.SideOffset * sideSign);

            float mountBeamWidth = Math.Max(0.010f, profile.RearClimbAssistUpperWidthM * 0.95f);
            float mountBeamHeight = Math.Max(0.010f, profile.RearClimbAssistUpperHeightM * 0.88f);
            AddBeam(faces, mountFront, upperFront, mountBeamWidth, mountBeamHeight, mountColor);
            AddBeam(faces, mountRear, upperRear, mountBeamWidth, mountBeamHeight, mountColor);
            AddBeam(faces, mountFront, mountRear, mountBeamWidth, mountBeamHeight, mountColor);
            AddBeam(
                faces,
                upperFront,
                kneeFront,
                Math.Max(0.010f, profile.RearClimbAssistUpperWidthM),
                Math.Max(0.010f, profile.RearClimbAssistUpperHeightM),
                upperColor);
            AddBeam(
                faces,
                upperRear,
                kneeRear,
                Math.Max(0.010f, profile.RearClimbAssistUpperWidthM),
                Math.Max(0.010f, profile.RearClimbAssistUpperHeightM),
                upperColor);
            AddBeam(
                faces,
                kneeCenter,
                foot,
                Math.Max(0.010f, profile.RearClimbAssistLowerWidthM),
                Math.Max(0.010f, profile.RearClimbAssistLowerHeightM),
                lowerColor);
            AddBeam(
                faces,
                foot,
                wheelAnchor,
                Math.Max(0.010f, leg.HingeRadius * 1.25f),
                Math.Max(0.010f, leg.HingeRadius * 1.10f),
                Preview3dPrimitives.Multiply(lowerColor, 1.08f));
            AddLegHub(faces, mountFront, leg.HingeRadius * 0.86f, mountColor);
            AddLegHub(faces, mountRear, leg.HingeRadius * 0.86f, mountColor);
            AddLegHub(faces, upperFront, leg.HingeRadius, upperColor);
            AddLegHub(faces, upperRear, leg.HingeRadius, upperColor);
            AddLegHub(faces, kneeFront, leg.HingeRadius, upperColor);
            AddLegHub(faces, kneeRear, leg.HingeRadius, upperColor);
            AddLegHub(faces, foot, leg.HingeRadius, lowerColor);
            AddLegHub(faces, wheelAnchor, leg.HingeRadius * 0.72f, Preview3dPrimitives.Multiply(lowerColor, 1.08f));
        }
    }

    private static void AddLegHub(ICollection<Preview3dFace> faces, Vector3 center, float radius, Color color)
    {
        float safeRadius = Math.Max(0.008f, radius);
        Preview3dPrimitives.AddBox(
            faces,
            center,
            new Vector3(Math.Max(0.008f, safeRadius * 1.4f), safeRadius, Math.Max(0.008f, safeRadius * 1.4f)),
            color);
    }

    private static PreviewBalanceLegGeometry ResolvePreviewBalanceLegGeometry(RobotAppearanceProfile profile)
    {
        float bodyHalfX = profile.BodyLengthM * 0.5f;
        float wheelRadius = Math.Max(0.018f, profile.RearLegWheelRadiusM);
        float footX = profile.WheelOffsetsM.Count > 0 ? profile.WheelOffsetsM.Min(offset => offset.X) : -bodyHalfX * 0.78f;
        float footY = wheelRadius;
        float wheelOuter = profile.WheelOffsetsM.Count > 0
            ? profile.WheelOffsetsM.Max(offset => Math.Abs(offset.Y) * profile.BodyRenderWidthScale)
            : profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale + wheelRadius * 0.55f;
        float rawSideOffset = Math.Max(
            profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale + wheelRadius * 0.28f,
            wheelOuter + wheelRadius * 0.08f);
        float bodyHalfSide = profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale;
        float armorThickness = Math.Max(0.012f, Math.Max(0.005f, profile.ArmorPlateGapM) * 0.75f);
        float armorCenterSide = bodyHalfSide + Math.Max(0.005f, profile.ArmorPlateGapM) + armorThickness * 1.35f;
        float hingeInsideLimit = armorCenterSide - Math.Max(0.018f, profile.RearClimbAssistHingeRadiusM * 1.35f);
        float minSideOffset = bodyHalfSide + Math.Max(0.004f, profile.RearClimbAssistHingeRadiusM * 0.30f);
        float maxSideOffset = Math.Max(minSideOffset, hingeInsideLimit);
        float sideOffset = Math.Clamp(rawSideOffset, minSideOffset, maxSideOffset);
        float anchorX = -bodyHalfX + profile.RearClimbAssistMountOffsetXM;
        float anchorY = profile.RearClimbAssistMountHeightM;
        float rearwardClearance = Math.Max(0.02f, profile.RearClimbAssistUpperLengthM * 0.14f);
        footX = Math.Min(footX, anchorX - rearwardClearance);
        Vector2 foot = ClampTwoLinkTargetPoint(
            new Vector2(anchorX, anchorY),
            new Vector2(footX, footY),
            profile.RearClimbAssistUpperLengthM,
            profile.RearClimbAssistLowerLengthM,
            profile.RearClimbAssistKneeMinDeg,
            profile.RearClimbAssistKneeMaxDeg);
        Vector2 knee = SelectBalanceLegJoint(
            new Vector2(anchorX, anchorY),
            foot,
            profile.RearClimbAssistUpperLengthM,
            profile.RearClimbAssistLowerLengthM,
            profile.RearClimbAssistKneeDirection);
        float pairGap = Math.Max(0.02f, profile.RearClimbAssistUpperPairGapM);
        float halfGap = pairGap * 0.5f;
        return new PreviewBalanceLegGeometry(
            new Vector2(anchorX + halfGap, anchorY),
            new Vector2(anchorX - halfGap, anchorY),
            knee,
            new Vector2(knee.X + halfGap, knee.Y),
            new Vector2(knee.X - halfGap, knee.Y),
            foot,
            sideOffset,
            Math.Max(0.008f, profile.RearClimbAssistHingeRadiusM));
    }

    private static Vector2 ClampTwoLinkTargetPoint(
        Vector2 anchor,
        Vector2 target,
        float upperLength,
        float lowerLength,
        float minAngleDeg,
        float maxAngleDeg)
    {
        Vector2 delta = target - anchor;
        float distance = delta.Length();
        if (distance <= 1e-6f)
        {
            return new Vector2(anchor.X, anchor.Y + Math.Max(0.001f, Math.Abs(upperLength - lowerLength)));
        }

        float clampedMinAngle = Math.Clamp(minAngleDeg, 5f, 175f);
        float clampedMaxAngle = Math.Clamp(Math.Max(clampedMinAngle, maxAngleDeg), 5f, 175f);
        float spanMin = SpanForAngle(upperLength, lowerLength, clampedMinAngle);
        float spanMax = SpanForAngle(upperLength, lowerLength, clampedMaxAngle);
        float low = Math.Max(Math.Abs(upperLength - lowerLength) + 1e-4f, MathF.Min(spanMin, spanMax));
        float high = MathF.Min(upperLength + lowerLength - 1e-4f, MathF.Max(spanMin, spanMax));
        float clampedDistance = Math.Clamp(distance, low, high);
        return anchor + delta / distance * clampedDistance;
    }

    private static float SpanForAngle(float upperLength, float lowerLength, float angleDeg)
    {
        float angleRad = angleDeg * MathF.PI / 180f;
        return MathF.Sqrt(Math.Max(upperLength * upperLength + lowerLength * lowerLength - 2f * upperLength * lowerLength * MathF.Cos(angleRad), 1e-6f));
    }

    private static Vector2 SelectBalanceLegJoint(
        Vector2 anchor,
        Vector2 foot,
        float upperLength,
        float lowerLength,
        string kneeDirection)
    {
        (Vector2 candidateA, Vector2 candidateB) = ResolveTwoLinkJointCandidates(anchor, foot, upperLength, lowerLength);
        bool preferFront = string.Equals(kneeDirection, "front", StringComparison.OrdinalIgnoreCase);
        float Score(Vector2 candidate)
        {
            float directionPenalty = preferFront
                ? Math.Max(0f, anchor.X - candidate.X) * 1000f
                : Math.Max(0f, candidate.X - anchor.X) * 1000f;
            float abovePenalty = Math.Max(0f, candidate.Y - anchor.Y) * 100f;
            float xBias = (preferFront ? -candidate.X : candidate.X) * 0.25f;
            return directionPenalty + abovePenalty + xBias;
        }

        return Score(candidateA) <= Score(candidateB) ? candidateA : candidateB;
    }

    private static (Vector2 CandidateA, Vector2 CandidateB) ResolveTwoLinkJointCandidates(
        Vector2 start,
        Vector2 end,
        float upperLength,
        float lowerLength)
    {
        Vector2 delta = end - start;
        float distance = delta.Length();
        if (distance <= 1e-6f)
        {
            Vector2 midpoint = new((start.X + end.X) * 0.5f, MathF.Min(start.Y, end.Y) - Math.Max(upperLength, lowerLength) * 0.35f);
            return (midpoint, midpoint);
        }

        float clampedDistance = Math.Clamp(distance, Math.Abs(upperLength - lowerLength) + 1e-4f, upperLength + lowerLength - 1e-4f);
        Vector2 direction = delta / distance;
        float baseDistance = (upperLength * upperLength - lowerLength * lowerLength + clampedDistance * clampedDistance) / Math.Max(2f * clampedDistance, 1e-6f);
        float height = MathF.Sqrt(Math.Max(upperLength * upperLength - baseDistance * baseDistance, 0f));
        Vector2 basePoint = start + direction * baseDistance;
        Vector2 perp = new(-direction.Y, direction.X);
        return (basePoint + perp * height, basePoint - perp * height);
    }

    private static void AddBeam(
        ICollection<Preview3dFace> faces,
        Vector3 start,
        Vector3 end,
        float width,
        float height,
        Color color)
    {
        Vector3 forward = end - start;
        float length = forward.Length();
        if (length <= 1e-4f)
        {
            return;
        }

        forward /= length;
        Vector3 up = MathF.Abs(Vector3.Dot(forward, Vector3.UnitY)) > 0.94f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
        up = Vector3.Normalize(Vector3.Cross(forward, right));
        Preview3dPrimitives.AddOrientedBox(
            faces,
            (start + end) * 0.5f,
            forward,
            right,
            up,
            length,
            Math.Max(0.006f, width),
            Math.Max(0.006f, height),
            color);
    }
}
