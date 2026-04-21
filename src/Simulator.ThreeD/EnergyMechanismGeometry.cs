using System.Drawing;
using System.Numerics;

namespace Simulator.ThreeD;

internal readonly record struct EnergyRenderBox(
    Vector3 Center,
    Vector3 Forward,
    Vector3 Right,
    Vector3 Up,
    float Length,
    float Width,
    float Height,
    Color FillColor,
    Color EdgeColor);

internal readonly record struct EnergyRenderPrism(
    IReadOnlyList<Vector3> Bottom,
    IReadOnlyList<Vector3> Top,
    Color FillColor,
    Color EdgeColor);

internal readonly record struct EnergyRenderCylinder(
    Vector3 Center,
    Vector3 NormalAxis,
    Vector3 UpAxis,
    float Radius,
    float Thickness,
    Color FillColor,
    Color EdgeColor,
    int Segments);

internal sealed class EnergyRenderMesh
{
    public List<EnergyRenderBox> Boxes { get; } = new();

    public List<EnergyRenderPrism> Prisms { get; } = new();

    public List<EnergyRenderCylinder> Cylinders { get; } = new();

    public float MaxHeight { get; set; }

    public void Append(EnergyRenderMesh other)
    {
        Boxes.AddRange(other.Boxes);
        Prisms.AddRange(other.Prisms);
        Cylinders.AddRange(other.Cylinders);
        MaxHeight = Math.Max(MaxHeight, other.MaxHeight);
    }
}

internal static class EnergyMechanismGeometry
{
    public const float MechanismYawRad = -MathF.PI * 0.25f;

    public static EnergyRenderMesh BuildSingle(
        RobotAppearanceProfile profile,
        Vector3 anchor,
        Color accentColor,
        float animationTimeSec)
    {
        _ = accentColor;
        var mesh = new EnergyRenderMesh();
        Vector3 groundForward = new(MathF.Cos(MechanismYawRad), 0f, MathF.Sin(MechanismYawRad));
        Vector3 groundRight = new(-groundForward.Z, 0f, groundForward.X);

        Color bodyColor = Color.FromArgb(255, 58, 62, 68);
        Color frameColor = Color.FromArgb(255, 72, 76, 84);
        Color assemblyColor = Color.FromArgb(255, 64, 68, 76);
        Color edgeColor = Color.FromArgb(255, 18, 22, 26);
        Color ringGrayOuter = Color.FromArgb(255, 86, 90, 96);
        Color ringGrayInner = Color.FromArgb(255, 108, 112, 118);
        Color[] rotorColors =
        [
            Color.FromArgb(255, 228, 76, 76),
            Color.FromArgb(255, 58, 112, 232),
        ];

        float rotorYaw = animationTimeSec * MathF.Tau * 0.32f;
        float baseHeight = Math.Max(0.00f, profile.StructureBaseHeightM);
        float groundClearance = Math.Max(0.0f, profile.StructureGroundClearanceM);
        float frameWidth = Math.Max(0.80f, profile.StructureFrameWidthM);
        float frameDepth = Math.Max(0.06f, profile.StructureFrameDepthM);
        float frameHeight = Math.Max(baseHeight + 0.60f, profile.StructureFrameHeightM);
        float supportOffset = Math.Max(0.10f, profile.StructureSupportOffsetM > 0f ? profile.StructureSupportOffsetM : frameWidth * 0.5f);
        float columnWidth = Math.Max(0.04f, profile.StructureFrameColumnWidthM);
        float beamHeight = Math.Max(0.04f, profile.StructureFrameBeamHeightM);
        float rotorCenterHeight = Math.Max(baseHeight + groundClearance + 0.40f, profile.StructureRotorCenterHeightM);
        float rotorPhaseRad = profile.StructureRotorPhaseDeg * MathF.PI / 180f;
        float rotorRadius = Math.Max(0.18f, profile.StructureRotorRadiusM);
        float hubRadius = Math.Max(0.05f, profile.StructureRotorHubRadiusM);
        float armWidth = Math.Max(0.04f, profile.StructureRotorArmWidthM);
        float armHeight = Math.Max(0.03f, profile.StructureRotorArmHeightM);
        float lampLength = Math.Max(0.06f, profile.StructureLampLengthM);
        float lampWidth = Math.Max(0.05f, profile.StructureLampWidthM);
        float lampHeight = Math.Max(0.03f, profile.StructureLampHeightM);
        float hangerWidth = Math.Max(0.20f, profile.StructureHangerWidthM);
        float hangerHeight = Math.Max(0.12f, profile.StructureHangerHeightM);
        float hangerDepth = Math.Max(0.04f, profile.StructureHangerDepthM);
        float hangerCenterHeight = Math.Max(baseHeight + 0.20f, profile.StructureHangerCenterHeightM);
        float lowerModuleWidth = Math.Max(0.04f, profile.StructureLowerModuleWidthM);
        float lowerModuleHeight = Math.Max(0.04f, profile.StructureLowerModuleHeightM);
        float lowerModuleDepth = Math.Max(0.04f, profile.StructureLowerModuleDepthM);
        float lowerModuleOffset = Math.Max(0.05f, profile.StructureLowerModuleOffsetXM);
        float lowerModuleCenterHeight = Math.Max(baseHeight + lowerModuleHeight * 0.5f, profile.StructureLowerModuleCenterHeightM);
        float cantileverLength = Math.Max(0.00f, profile.StructureCantileverLengthM);
        float cantileverPairGap = Math.Max(frameWidth + cantileverLength, profile.StructureCantileverPairGapM > 0f ? profile.StructureCantileverPairGapM : frameWidth + cantileverLength);
        float cantileverOffsetY = profile.StructureCantileverOffsetYM;
        float cantileverHeight = Math.Max(0.04f, profile.StructureCantileverHeightM > 0f ? profile.StructureCantileverHeightM : 0.04f);
        float cantileverDepth = Math.Max(0.04f, profile.StructureCantileverDepthM > 0f ? profile.StructureCantileverDepthM : 0.04f);
        float railGap = Math.Max(0.026f, armWidth * 0.68f);
        float armInnerRadius = Math.Max(hubRadius * 1.35f, 0.12f);
        float armLength = Math.Max(0.10f, profile.StructureRotorArmLengthM);
        float armOuterRadius = Math.Max(armInnerRadius + 0.04f, Math.Min(armInnerRadius + armLength, rotorRadius - lampLength * 0.15f));
        float podDepth = Math.Max(lampWidth * 0.18f, frameDepth * 0.55f);
        float topBeamY = groundClearance + frameHeight - beamHeight * 0.5f;
        float baseLength = Math.Max(0.40f, profile.StructureBaseLengthM > 0f ? profile.StructureBaseLengthM : Math.Max(frameWidth * 1.65f, profile.BodyLengthM * 1.72f));
        float baseWidth = Math.Max(0.40f, profile.StructureBaseWidthM > 0f ? profile.StructureBaseWidthM : Math.Max(frameDepth * 6.0f, profile.BodyWidthM * 2.45f));
        float baseTopLength = Math.Max(0.20f, profile.StructureBaseTopLengthM > 0f ? profile.StructureBaseTopLengthM : baseLength * 0.34f);
        float baseTopWidth = Math.Max(0.16f, profile.StructureBaseTopWidthM > 0f ? profile.StructureBaseTopWidthM : baseWidth * 0.24f);
        float postHeight = Math.Max(1.90f, frameHeight - baseHeight);
        float topBeamWidth = Math.Max(supportOffset * 2.0f, frameWidth);
        float connectorCenterY = hangerCenterHeight;
        float rotorCenterY = rotorCenterHeight + cantileverOffsetY;
        float rotorAxisGap = Math.Max(
            Math.Max(
                frameDepth * 1.8f,
                hubRadius * 2.6f),
            Math.Min(cantileverPairGap, frameWidth) * 0.42f + cantileverLength * 0.30f);
        float stemHeight = Math.Max(0.12f, topBeamY - connectorCenterY - beamHeight * 0.5f);
        float hangerBlockWidth = Math.Max(0.08f, hangerWidth * 0.32f);
        float hangerBlockHeight = Math.Max(0.08f, hangerHeight * 0.22f);
        float hangerBlockDepth = Math.Max(0.06f, hangerDepth);
        float assemblyRodSpan = Math.Max(0.05f, lowerModuleOffset);
        float moduleSideOffset = Math.Max(lowerModuleWidth * 0.72f, lowerModuleOffset);

        foreach (float side in new[] { -1f, 1f })
        {
            AddLocalBox(mesh, anchor, groundForward, groundRight, side * supportOffset, groundClearance + baseHeight * 0.5f, 0.0f, baseTopLength * 0.5f, baseHeight * 0.5f, baseTopWidth * 0.5f, bodyColor, edgeColor);
            AddLocalBox(mesh, anchor, groundForward, groundRight, side * supportOffset, groundClearance + baseHeight + postHeight * 0.5f, 0.0f, columnWidth * 0.5f, postHeight * 0.5f, frameDepth * 0.5f, frameColor, edgeColor);
        }

        AddLocalBox(mesh, anchor, groundForward, groundRight, 0.0f, topBeamY, 0.0f, topBeamWidth * 0.5f, beamHeight * 0.5f, columnWidth * 0.5f, frameColor, edgeColor);
        AddLocalBox(mesh, anchor, groundForward, groundRight, 0.0f, connectorCenterY + stemHeight * 0.5f, 0.0f, columnWidth * 0.30f, stemHeight * 0.5f, hangerBlockDepth * 0.35f, frameColor, edgeColor);
        AddLocalBox(mesh, anchor, groundForward, groundRight, 0.0f, connectorCenterY, 0.0f, hangerBlockWidth * 0.5f, hangerBlockHeight * 0.5f, hangerBlockDepth * 0.5f, frameColor, edgeColor);

        for (int rotorIndex = 0; rotorIndex < 2; rotorIndex++)
        {
            float cx = 0.0f;
            float cy = rotorCenterY;
            float cz = rotorIndex == 0 ? -rotorAxisGap * 0.5f : rotorAxisGap * 0.5f;
            Color rotorColor = rotorColors[rotorIndex];

            AddBrace(
                mesh,
                LocalPoint(anchor, groundForward, groundRight, 0.0f, connectorCenterY, 0.0f),
                LocalPoint(anchor, groundForward, groundRight, cx, cy, cz),
                cantileverHeight * 0.72f,
                cantileverDepth,
                frameColor,
                edgeColor);

            Vector3 hubCenter = LocalPoint(anchor, groundForward, groundRight, cx, cy, cz);
            AddLocalEnergyPod(
                mesh,
                anchor,
                groundForward,
                groundRight,
                cx,
                cy,
                cz,
                MathF.PI * 0.25f,
                hubRadius * 3.1f,
                hubRadius * 2.8f,
                hubRadius * 2.8f,
                Math.Max(frameDepth * 0.58f, 0.045f),
                Color.FromArgb(255, 74, 78, 84),
                edgeColor);
            AddRingedDisk(
                mesh,
                hubCenter,
                groundRight,
                Vector3.UnitY,
                hubRadius * 0.98f,
                Math.Max(frameDepth * 0.12f, 0.012f),
                rotorColor,
                ringGrayOuter,
                ringGrayInner,
                6,
                edgeColor);

            for (int index = 0; index < 5; index++)
            {
                float yaw = rotorYaw + rotorPhaseRad + MathF.Tau * index / 5f;
                AddEnergyArm(mesh, anchor, groundForward, groundRight, cx, cy, cz, yaw, armInnerRadius, armOuterRadius, railGap, armWidth, armHeight, frameColor, edgeColor);
                float lampX = cx + MathF.Cos(yaw) * rotorRadius;
                float lampY = cy + MathF.Sin(yaw) * rotorRadius;

                Vector3 lampCenter = LocalPoint(anchor, groundForward, groundRight, lampX, lampY, cz);
                AddRingedDisk(
                    mesh,
                    lampCenter,
                    groundRight,
                    Vector3.UnitY,
                    lampLength * 0.43f,
                    Math.Max(0.005f, podDepth * 0.18f),
                    rotorColor,
                    ringGrayOuter,
                    ringGrayInner,
                    10,
                    edgeColor);
            }
        }

        float rodTopY = connectorCenterY - hangerBlockHeight * 0.65f;
        float rodBottomY = lowerModuleCenterHeight + lowerModuleHeight * 0.42f;
        foreach ((float Side, Color Accent) sideData in new[] { (-1f, rotorColors[1]), (1f, rotorColors[0]) })
        {
            float side = sideData.Side;
            AddBrace(
                mesh,
                LocalPoint(anchor, groundForward, groundRight, side * assemblyRodSpan, rodTopY, 0.0f),
                LocalPoint(anchor, groundForward, groundRight, side * moduleSideOffset, rodBottomY, 0.0f),
                0.022f,
                0.020f,
                frameColor,
                edgeColor);
            AddEnergyPod(
                mesh,
                LocalPoint(anchor, groundForward, groundRight, side * moduleSideOffset, lowerModuleCenterHeight, 0.0f),
                MechanismYawRad + (side < 0f ? 0f : MathF.PI),
                lowerModuleWidth,
                lowerModuleHeight,
                lowerModuleHeight,
                lowerModuleDepth,
                assemblyColor,
                edgeColor);
            AddLocalBox(
                mesh,
                anchor,
                groundForward,
                groundRight,
                side * moduleSideOffset,
                lowerModuleCenterHeight,
                0.0f,
                lowerModuleWidth * 0.10f,
                lowerModuleHeight * 0.28f,
                lowerModuleDepth * 0.10f,
                sideData.Accent,
                edgeColor);
        }

        mesh.MaxHeight = groundClearance + Math.Max(frameHeight, rotorCenterY + rotorRadius + lampHeight + 0.10f);
        return mesh;
    }

    public static EnergyRenderMesh BuildPreviewPair(RobotAppearanceProfile profile, float animationTimeSec)
        => BuildSingle(profile, Vector3.Zero, ResolveAccentColor(null), animationTimeSec);

    public static Color ResolveAccentColor(string? team)
        => string.Equals(team, "red", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(255, 228, 76, 76)
            : string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(255, 58, 112, 232)
                : Color.FromArgb(255, 255, 195, 64);

    private static void AddHanger(
        EnergyRenderMesh mesh,
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
        float halfWidth = width * 0.5f;
        float halfHeight = height * 0.5f;
        float bar = Math.Max(0.018f, Math.Min(width, height) * 0.12f);
        mesh.Boxes.Add(new EnergyRenderBox(center + forward * halfWidth, up, right, forward, height, depth, bar, frameColor, edgeColor));
        mesh.Boxes.Add(new EnergyRenderBox(center - forward * halfWidth, up, right, forward, height, depth, bar, frameColor, edgeColor));
        mesh.Boxes.Add(new EnergyRenderBox(center + up * halfHeight, forward, right, up, width + bar, depth, bar, frameColor, edgeColor));
        mesh.Boxes.Add(new EnergyRenderBox(center - up * halfHeight, forward, right, up, width + bar, depth, bar, frameColor, edgeColor));
    }

    private static void AddLocalBox(
        EnergyRenderMesh mesh,
        Vector3 anchor,
        Vector3 localForward,
        Vector3 localRight,
        float localX,
        float localY,
        float localZ,
        float halfLength,
        float halfHeight,
        float halfWidth,
        Color fillColor,
        Color edgeColor)
    {
        mesh.Boxes.Add(new EnergyRenderBox(
            LocalPoint(anchor, localForward, localRight, localX, localY, localZ),
            localForward,
            localRight,
            Vector3.UnitY,
            halfLength * 2f,
            halfWidth * 2f,
            halfHeight * 2f,
            fillColor,
            edgeColor));
    }

    private static Vector3 LocalPoint(
        Vector3 anchor,
        Vector3 localForward,
        Vector3 localRight,
        float localX,
        float localY,
        float localZ)
        => anchor + localForward * localX + Vector3.UnitY * localY + localRight * localZ;

    private static void AddEnergyArm(
        EnergyRenderMesh mesh,
        Vector3 anchor,
        Vector3 localForward,
        Vector3 localRight,
        float hubX,
        float hubY,
        float hubZ,
        float yawRad,
        float innerRadius,
        float outerRadius,
        float railGap,
        float railWidth,
        float railDepth,
        Color fillColor,
        Color edgeColor)
    {
        float dirX = MathF.Cos(yawRad);
        float dirY = MathF.Sin(yawRad);
        float sideX = -dirY;
        float sideY = dirX;

        Vector3 rootA = LocalPoint(anchor, localForward, localRight, hubX + dirX * innerRadius + sideX * railGap, hubY + dirY * innerRadius + sideY * railGap, hubZ);
        Vector3 rootB = LocalPoint(anchor, localForward, localRight, hubX + dirX * innerRadius - sideX * railGap, hubY + dirY * innerRadius - sideY * railGap, hubZ);
        Vector3 endA = LocalPoint(anchor, localForward, localRight, hubX + dirX * outerRadius + sideX * railGap * 0.72f, hubY + dirY * outerRadius + sideY * railGap * 0.72f, hubZ);
        Vector3 endB = LocalPoint(anchor, localForward, localRight, hubX + dirX * outerRadius - sideX * railGap * 0.72f, hubY + dirY * outerRadius - sideY * railGap * 0.72f, hubZ);
        AddBrace(mesh, rootA, endA, railWidth, railDepth, fillColor, edgeColor);
        AddBrace(mesh, rootB, endB, railWidth, railDepth, fillColor, edgeColor);
        AddBrace(mesh, rootA, rootB, railWidth * 0.85f, railDepth, fillColor, edgeColor);
        AddBrace(mesh, endA, endB, railWidth * 1.10f, railDepth, fillColor, edgeColor);
    }

    private static void AddEnergyPod(
        EnergyRenderMesh mesh,
        Vector3 center,
        float yawRad,
        float length,
        float width,
        float height,
        float depth,
        Color fillColor,
        Color edgeColor)
    {
        float halfLength = Math.Max(0.02f, length * 0.5f);
        float halfWidth = Math.Max(0.02f, width * 0.5f);
        float halfHeight = Math.Max(0.02f, height * 0.5f);
        float halfDepth = Math.Max(0.01f, depth * 0.5f);
        float noseX = halfLength;
        float shoulderX = halfLength * 0.40f;
        float tailX = -halfLength;
        float tailInnerX = -halfLength * 0.60f;
        float topCut = halfHeight * 0.34f;
        float bottomCut = halfHeight * 0.28f;

        Vector3 podForward = new(MathF.Cos(yawRad), 0f, MathF.Sin(yawRad));
        Vector3 podRight = new(-podForward.Z, 0f, podForward.X);

        IReadOnlyList<Vector3> Section(float zValue)
            => new[]
            {
                center + podForward * tailInnerX + Vector3.UnitY * (-halfHeight) + podRight * zValue,
                center + podForward * shoulderX + Vector3.UnitY * (-halfHeight) + podRight * zValue,
                center + podForward * noseX + Vector3.UnitY * (-bottomCut) + podRight * zValue,
                center + podForward * noseX + Vector3.UnitY * bottomCut + podRight * zValue,
                center + podForward * shoulderX + Vector3.UnitY * halfHeight + podRight * zValue,
                center + podForward * tailInnerX + Vector3.UnitY * halfHeight + podRight * zValue,
                center + podForward * tailX + Vector3.UnitY * topCut + podRight * zValue,
                center + podForward * tailX + Vector3.UnitY * (-topCut) + podRight * zValue,
            };

        mesh.Prisms.Add(new EnergyRenderPrism(
            Section(-halfDepth),
            Section(halfDepth),
            fillColor,
            edgeColor));
    }

    private static void AddLocalEnergyPod(
        EnergyRenderMesh mesh,
        Vector3 anchor,
        Vector3 localForward,
        Vector3 localRight,
        float localX,
        float localY,
        float localZ,
        float yawRad,
        float length,
        float width,
        float height,
        float depth,
        Color fillColor,
        Color edgeColor)
    {
        float halfLength = Math.Max(0.02f, length * 0.5f);
        float halfWidth = Math.Max(0.02f, width * 0.5f);
        float halfHeight = Math.Max(0.02f, height * 0.5f);
        float halfDepth = Math.Max(0.01f, depth * 0.5f);
        float noseX = halfLength;
        float shoulderX = halfLength * 0.40f;
        float tailX = -halfLength;
        float tailInnerX = -halfLength * 0.60f;
        float topCut = halfHeight * 0.34f;
        float bottomCut = halfHeight * 0.28f;

        IReadOnlyList<Vector3> Section(float zValue)
            => new[]
            {
                LocalPoint(anchor, localForward, localRight, localX + MathF.Cos(yawRad) * tailInnerX - MathF.Sin(yawRad) * (-halfHeight), localY + MathF.Sin(yawRad) * tailInnerX + MathF.Cos(yawRad) * (-halfHeight), localZ + zValue),
                LocalPoint(anchor, localForward, localRight, localX + MathF.Cos(yawRad) * shoulderX - MathF.Sin(yawRad) * (-halfHeight), localY + MathF.Sin(yawRad) * shoulderX + MathF.Cos(yawRad) * (-halfHeight), localZ + zValue),
                LocalPoint(anchor, localForward, localRight, localX + MathF.Cos(yawRad) * noseX - MathF.Sin(yawRad) * (-bottomCut), localY + MathF.Sin(yawRad) * noseX + MathF.Cos(yawRad) * (-bottomCut), localZ + zValue),
                LocalPoint(anchor, localForward, localRight, localX + MathF.Cos(yawRad) * noseX - MathF.Sin(yawRad) * bottomCut, localY + MathF.Sin(yawRad) * noseX + MathF.Cos(yawRad) * bottomCut, localZ + zValue),
                LocalPoint(anchor, localForward, localRight, localX + MathF.Cos(yawRad) * shoulderX - MathF.Sin(yawRad) * halfHeight, localY + MathF.Sin(yawRad) * shoulderX + MathF.Cos(yawRad) * halfHeight, localZ + zValue),
                LocalPoint(anchor, localForward, localRight, localX + MathF.Cos(yawRad) * tailInnerX - MathF.Sin(yawRad) * halfHeight, localY + MathF.Sin(yawRad) * tailInnerX + MathF.Cos(yawRad) * halfHeight, localZ + zValue),
                LocalPoint(anchor, localForward, localRight, localX + MathF.Cos(yawRad) * tailX - MathF.Sin(yawRad) * topCut, localY + MathF.Sin(yawRad) * tailX + MathF.Cos(yawRad) * topCut, localZ + zValue),
                LocalPoint(anchor, localForward, localRight, localX + MathF.Cos(yawRad) * tailX - MathF.Sin(yawRad) * (-topCut), localY + MathF.Sin(yawRad) * tailX + MathF.Cos(yawRad) * (-topCut), localZ + zValue),
            };

        mesh.Prisms.Add(new EnergyRenderPrism(
            Section(-halfDepth),
            Section(halfDepth),
            fillColor,
            edgeColor));
    }

    private static void AddRingedDisk(
        EnergyRenderMesh mesh,
        Vector3 center,
        Vector3 normalAxis,
        Vector3 upAxis,
        float radius,
        float thickness,
        Color accentColor,
        Color outerGray,
        Color innerGray,
        int ringCount,
        Color edgeColor)
    {
        float safeRadius = Math.Max(0.02f, radius);
        float safeThickness = Math.Max(0.004f, thickness);
        Vector3 safeNormal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(normalAxis);
        Vector3 safeUp = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        if (Math.Abs(Vector3.Dot(safeNormal, safeUp)) >= 0.96f)
        {
            safeUp = Math.Abs(Vector3.Dot(safeNormal, Vector3.UnitY)) >= 0.96f ? Vector3.UnitX : Vector3.UnitY;
        }

        const int diskSegments = 14;
        mesh.Cylinders.Add(new EnergyRenderCylinder(center, safeNormal, safeUp, safeRadius * 1.05f, safeThickness * 0.55f, accentColor, edgeColor, diskSegments));
        for (int ringIndex = 0; ringIndex < Math.Max(2, ringCount); ringIndex++)
        {
            float t = ringIndex / (float)Math.Max(1, ringCount - 1);
            float ringRadius = safeRadius * (1.0f - t * 0.88f);
            float ringThickness = safeThickness * (0.90f - t * 0.42f);
            Color color = ringIndex == 0 ? accentColor : (ringIndex % 2 == 0 ? outerGray : innerGray);
            mesh.Cylinders.Add(new EnergyRenderCylinder(
                center,
                safeNormal,
                safeUp,
                Math.Max(safeRadius * 0.08f, ringRadius),
                Math.Max(0.0025f, ringThickness),
                color,
                edgeColor,
                diskSegments));
        }

        mesh.Cylinders.Add(new EnergyRenderCylinder(
            center,
            safeNormal,
            safeUp,
            Math.Max(safeRadius * 0.08f, safeRadius * 0.11f),
            safeThickness * 0.95f,
            Color.FromArgb(255, 58, 62, 68),
            edgeColor,
            diskSegments));
    }

    private static void AddArm(
        EnergyRenderMesh mesh,
        Vector3 center,
        Vector3 axis,
        Vector3 sideNormal,
        Vector3 armUp,
        float innerRadius,
        float outerRadius,
        float railGap,
        float railThickness,
        Color fillColor,
        Color edgeColor)
    {
        Vector3 innerCenter = center + axis * innerRadius;
        Vector3 outerCenter = center + axis * outerRadius;
        Vector3 railOffset = armUp * railGap;
        AddBrace(mesh, innerCenter + railOffset, outerCenter + railOffset * 0.72f, railThickness, railThickness, fillColor, edgeColor);
        AddBrace(mesh, innerCenter - railOffset, outerCenter - railOffset * 0.72f, railThickness, railThickness, fillColor, edgeColor);
        AddBrace(mesh, innerCenter + railOffset, innerCenter - railOffset, railThickness * 0.88f, railThickness, fillColor, edgeColor);
        AddBrace(mesh, outerCenter + railOffset * 0.72f, outerCenter - railOffset * 0.72f, railThickness * 0.92f, railThickness, fillColor, edgeColor);
    }

    private static void AddPodShell(
        EnergyRenderMesh mesh,
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
        float backHalfWidth = width * 0.52f;
        float backHalfHeight = height * 0.52f;
        float frontHalfWidth = width * 0.42f;
        float frontHalfHeight = height * 0.42f;
        float bevel = Math.Max(0.010f, Math.Min(width, height) * 0.18f);
        Vector3 backCenter = center - forward * (length * 0.18f);
        Vector3 frontCenter = center + forward * (length * 0.12f);

        mesh.Prisms.Add(new EnergyRenderPrism(
            BuildChamferSection(backCenter, right, up, backHalfWidth, backHalfHeight, bevel),
            BuildChamferSection(frontCenter, right, up, frontHalfWidth, frontHalfHeight, bevel * 0.92f),
            fillColor,
            edgeColor));

        mesh.Boxes.Add(new EnergyRenderBox(
            center - forward * (length * 0.08f),
            forward,
            right,
            up,
            length * 0.30f,
            width * 0.34f,
            height * 0.18f,
            Color.FromArgb(255, 60, 64, 70),
            edgeColor));
    }

    private static IReadOnlyList<Vector3> BuildChamferSection(
        Vector3 center,
        Vector3 right,
        Vector3 up,
        float halfWidth,
        float halfHeight,
        float bevel)
    {
        bevel = Math.Min(bevel, Math.Min(halfWidth, halfHeight) * 0.72f);
        return new[]
        {
            center + up * halfHeight + right * (halfWidth - bevel),
            center + up * (halfHeight - bevel) + right * halfWidth,
            center - up * (halfHeight - bevel) + right * halfWidth,
            center - up * halfHeight + right * (halfWidth - bevel),
            center - up * halfHeight - right * (halfWidth - bevel),
            center - up * (halfHeight - bevel) - right * halfWidth,
            center + up * (halfHeight - bevel) - right * halfWidth,
            center + up * halfHeight - right * (halfWidth - bevel),
        };
    }

    private static void AddBrace(
        EnergyRenderMesh mesh,
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
        mesh.Boxes.Add(new EnergyRenderBox(
            (start + end) * 0.5f,
            forward,
            right,
            up,
            length,
            depth,
            width,
            fillColor,
            edgeColor));
    }

    private static IReadOnlyList<Vector3> BuildPlatformOutline(
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

    private static Color Blend(Color left, Color right, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        float inverse = 1f - amount;
        return Color.FromArgb(
            Math.Clamp((int)MathF.Round(left.R * inverse + right.R * amount), 0, 255),
            Math.Clamp((int)MathF.Round(left.G * inverse + right.G * amount), 0, 255),
            Math.Clamp((int)MathF.Round(left.B * inverse + right.B * amount), 0, 255));
    }
}
