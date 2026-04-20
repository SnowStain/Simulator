using System.Numerics;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private readonly record struct SolidFace(Vector3[] Vertices, float Ambient);

    private readonly record struct RenderWheelComponent(
        float LocalX,
        float LocalY,
        float CenterHeightM,
        float RadiusM,
        float HalfThicknessM,
        float SpinRad);

    private readonly record struct ArmorRenderComponent(Vector3 LocalCenter, float YawRad);

    private readonly record struct ArmorLightRenderComponent(Vector3 LocalCenterA, Vector3 LocalCenterB, float YawRad);

    private readonly record struct RuntimeChassisMotion(
        float BodyLiftM,
        float FrontDropM,
        float FrontRaiseM,
        float RearFootRaiseM,
        float RearFootReachM);

    private readonly record struct BalanceLegGeometry(
        Vector2 UpperFront,
        Vector2 UpperRear,
        Vector2 KneeCenter,
        Vector2 KneeFront,
        Vector2 KneeRear,
        Vector2 Foot,
        float SideOffset,
        float HingeRadius);

    private float DrawEntityAppearanceModelModern(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile)
    {
        float yaw = ResolveEntityYaw(entity);
        float turretYaw = (float)(entity.TurretYawDeg * Math.PI / 180.0);
        float gimbalPitch = (float)(entity.GimbalPitchDeg * Math.PI / 180.0);
        Color teamColor = ResolveTeamColor(entity.Team);
        Color bodyColor = TintProfileColor(profile.BodyColor, teamColor, entity.IsAlive ? 0.16f : 0.04f, entity.IsAlive);
        Color turretColor = TintProfileColor(profile.TurretColor, teamColor, entity.IsAlive ? 0.22f : 0.05f, entity.IsAlive);
        Color wheelColor = TintProfileColor(profile.WheelColor, teamColor, entity.IsAlive ? 0.07f : 0.03f, entity.IsAlive);
        Color armorColor = TintProfileColor(profile.ArmorColor, teamColor, entity.IsAlive ? 0.18f : 0.04f, entity.IsAlive);
        RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(entity);

        float bodyLength = Math.Max(0.12f, profile.BodyLengthM);
        float bodyWidth = Math.Max(0.10f, profile.BodyWidthM * profile.BodyRenderWidthScale);
        float bodyHeight = Math.Max(0.08f, profile.BodyHeightM);
        float bodyBase = Math.Max(0f, profile.BodyClearanceM + motion.BodyLiftM);
        bool octagonBody = string.Equals(profile.BodyShape, "octagon", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<Vector3> bodyFootprint = BuildBodyOutlineFootprint(center, bodyLength, bodyWidth, bodyBase, yaw, octagonBody);
        DrawPrismWireframe(
            graphics,
            bodyFootprint,
            bodyHeight,
            Color.FromArgb(entity.IsAlive ? 248 : 232, bodyColor),
            Color.FromArgb(entity.IsAlive ? 255 : 220, bodyColor),
            null);

        float maxHeight = bodyBase + bodyHeight;
        float capBase = bodyBase + bodyHeight * 0.72f;
        float capHeight = Math.Max(0.015f, bodyHeight * 0.12f);
        IReadOnlyList<Vector3> bodyCap = ScaleFootprint(bodyFootprint, center, capBase, octagonBody ? 0.78f : 0.80f);
        DrawPrismWireframe(
            graphics,
            bodyCap,
            capHeight,
            Color.FromArgb(entity.IsAlive ? 246 : 226, BlendColor(bodyColor, Color.White, 0.14f)),
            Color.FromArgb(entity.IsAlive ? 250 : 216, BlendColor(bodyColor, Color.Black, 0.08f)),
            null);
        maxHeight = Math.Max(maxHeight, capBase + capHeight);

        Vector2 forward2 = new(MathF.Cos(yaw), MathF.Sin(yaw));
        Vector2 right2 = new(-forward2.Y, forward2.X);
        Vector3 lateralAxis = new(right2.X, 0f, right2.Y);

        foreach (RenderWheelComponent wheel in ResolveWheelComponents(entity, profile, motion))
        {
            if (!IsLocalSideVisibleFromCamera(center, yaw, wheel.LocalY, bodyBase + bodyHeight))
            {
                continue;
            }

            Vector3 wheelCenter = OffsetScenePosition(
                center,
                wheel.LocalX,
                wheel.LocalY,
                yaw,
                Math.Max(wheel.RadiusM, wheel.CenterHeightM));
            Vector3 wheelForward = new(forward2.X, 0f, forward2.Y);
            Vector3 wheelAxle = lateralAxis;
            if (string.Equals(profile.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase))
            {
                Vector2 inwardLocal = new(-wheel.LocalX, -wheel.LocalY);
                if (inwardLocal.LengthSquared() > 1e-6f)
                {
                    inwardLocal = Vector2.Normalize(inwardLocal);
                    wheelAxle = Vector3.Normalize(
                        new Vector3(forward2.X, 0f, forward2.Y) * inwardLocal.X
                        + new Vector3(right2.X, 0f, right2.Y) * inwardLocal.Y);
                    wheelForward = Vector3.UnitY;
                }
            }

            DrawWheelSolid(
                graphics,
                wheelCenter,
                wheelAxle,
                wheelForward,
                wheel.RadiusM,
                wheel.HalfThicknessM,
                wheel.SpinRad,
                Color.FromArgb(entity.IsAlive ? 248 : 226, wheelColor),
                Color.FromArgb(entity.IsAlive ? 252 : 216, BlendColor(wheelColor, Color.Black, 0.12f)));

            maxHeight = Math.Max(maxHeight, wheel.CenterHeightM + wheel.RadiusM);
        }

        string? lockedPlateId = ResolveLockedPlateIdFor(entity);
        IReadOnlyList<ArmorRenderComponent> armorComponents = ResolveArmorComponents(profile);
        for (int armorIndex = 0; armorIndex < armorComponents.Count; armorIndex++)
        {
            ArmorRenderComponent component = armorComponents[armorIndex];
            Vector3 armorCenter = OffsetScenePosition(center, component.LocalCenter.X, component.LocalCenter.Z, yaw, component.LocalCenter.Y - profile.ArmorPlateHeightM * 0.5f);
            if (!IsArmorPlateVisibleFromCamera(armorCenter, yaw + component.YawRad))
            {
                continue;
            }

            bool locked = string.Equals(lockedPlateId, $"armor_{armorIndex + 1}", StringComparison.OrdinalIgnoreCase);
            Color plateFill = locked
                ? Color.FromArgb(255, 255, 211, 84)
                : Color.FromArgb(entity.IsAlive ? 248 : 226, armorColor);
            Color plateEdge = locked
                ? Color.FromArgb(255, 255, 244, 150)
                : Color.FromArgb(entity.IsAlive ? 255 : 216, armorColor);
            IReadOnlyList<Vector3> armorFootprint = BuildOrientedRectFootprint(
                armorCenter,
                Math.Max(0.012f, profile.ArmorPlateGapM * 0.75f),
                profile.ArmorPlateWidthM,
                0f,
                yaw + component.YawRad);
            DrawPrismWireframe(
                graphics,
                armorFootprint,
                profile.ArmorPlateHeightM,
                plateFill,
                plateEdge,
                null);
            maxHeight = Math.Max(maxHeight, component.LocalCenter.Y + profile.ArmorPlateHeightM * 0.5f);
        }

        foreach (ArmorLightRenderComponent component in ResolveArmorLightComponents(profile))
        {
            maxHeight = Math.Max(maxHeight, DrawArmorLight(graphics, center, yaw, component.LocalCenterA, component.YawRad, entity, profile));
            maxHeight = Math.Max(maxHeight, DrawArmorLight(graphics, center, yaw, component.LocalCenterB, component.YawRad, entity, profile));
        }

        if (!string.Equals(profile.FrontClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            float frontForward = bodyLength * 0.5f + profile.FrontClimbAssistForwardOffsetM + profile.FrontClimbAssistBottomLengthM * 0.5f;
            float frontSide = Math.Max(bodyWidth * 0.45f, bodyWidth * 0.5f - profile.FrontClimbAssistInnerOffsetM);
            float plateCenterHeight = Math.Max(
                profile.WheelRadiusM + profile.FrontClimbAssistPlateHeightM * 0.5f - motion.FrontDropM * 0.5f + motion.FrontRaiseM * 0.2f,
                profile.WheelRadiusM + Math.Max(0f, motion.FrontRaiseM) * 0.75f);
            Color climbColor = BlendColor(bodyColor, Color.FromArgb(92, 96, 108), 0.34f);
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                float sideSign = sideIndex == 0 ? -1f : 1f;
                Vector3 plateCenter = OffsetScenePosition(center, frontForward, frontSide * sideSign, yaw, plateCenterHeight);
                DrawTaperedPlate(
                    graphics,
                    plateCenter,
                    yaw,
                    profile.FrontClimbAssistTopLengthM,
                    profile.FrontClimbAssistBottomLengthM,
                    profile.FrontClimbAssistPlateWidthM,
                    profile.FrontClimbAssistPlateHeightM,
                    Color.FromArgb(entity.IsAlive ? 246 : 224, climbColor),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, climbColor));
                maxHeight = Math.Max(maxHeight, plateCenterHeight + profile.FrontClimbAssistPlateHeightM * 0.5f);
            }
        }

        if (!string.Equals(profile.RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            Color legColor = BlendColor(bodyColor, Color.FromArgb(86, 92, 106), 0.44f);
            if (string.Equals(profile.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase))
            {
                maxHeight = Math.Max(maxHeight, DrawBalanceLegAssembly(graphics, center, yaw, lateralAxis, profile, entity, legColor, motion));
            }
            else
            {
                float rearLift = Math.Max(0f, -motion.RearFootRaiseM);
                float rearReach = Math.Max(0f, -motion.RearFootReachM);
                float upperBase = Math.Max(bodyBase + bodyHeight * 0.55f + rearLift * 0.35f, profile.RearClimbAssistMountHeightM + motion.BodyLiftM);
                float lowerBase = Math.Max(0.02f, profile.WheelRadiusM * 0.18f + rearLift);
                float rearForward = -bodyLength * 0.5f + profile.RearClimbAssistMountOffsetXM;
                float rearSide = Math.Max(bodyWidth * 0.30f, bodyWidth * 0.5f - profile.RearClimbAssistInnerOffsetM);
                for (int sideIndex = 0; sideIndex < 2; sideIndex++)
                {
                    float sideSign = sideIndex == 0 ? -1f : 1f;
                    Vector3 upperCenter = OffsetScenePosition(center, rearForward, rearSide * sideSign, yaw, upperBase);
                    IReadOnlyList<Vector3> upperFootprint = BuildOrientedRectFootprint(
                        upperCenter,
                        profile.RearClimbAssistUpperLengthM,
                        profile.RearClimbAssistUpperWidthM,
                        0f,
                        yaw);
                    DrawPrismWireframe(
                        graphics,
                        upperFootprint,
                        profile.RearClimbAssistUpperHeightM,
                        Color.FromArgb(entity.IsAlive ? 246 : 224, legColor),
                        Color.FromArgb(entity.IsAlive ? 250 : 216, legColor),
                        null);

                    Vector3 lowerCenter = OffsetScenePosition(center, rearForward - rearReach - profile.RearClimbAssistLowerLengthM * 0.2f, rearSide * sideSign, yaw, lowerBase);
                    IReadOnlyList<Vector3> lowerFootprint = BuildOrientedRectFootprint(
                        lowerCenter,
                        profile.RearClimbAssistLowerLengthM,
                        profile.RearClimbAssistLowerWidthM,
                        0f,
                        yaw - MathF.PI * 0.5f * (entity.TraversalActive ? 0.32f : 0.10f));
                    DrawPrismWireframe(
                        graphics,
                        lowerFootprint,
                        profile.RearClimbAssistLowerHeightM,
                        Color.FromArgb(entity.IsAlive ? 248 : 226, legColor),
                        Color.FromArgb(entity.IsAlive ? 250 : 216, legColor),
                        null);
                }

                maxHeight = Math.Max(maxHeight, upperBase + profile.RearClimbAssistUpperHeightM);
            }
        }

        if (profile.GimbalLengthM > 0.04f && profile.GimbalWidthM > 0.04f && profile.GimbalBodyHeightM > 0.02f)
        {
            float bodyTop = bodyBase + bodyHeight;
            if (profile.GimbalMountGapM + profile.GimbalMountHeightM > 0.02f)
            {
                float mountHeight = Math.Max(0.02f, profile.GimbalMountGapM + profile.GimbalMountHeightM);
                float mountBase = bodyTop;
                Vector3 mountCenter = OffsetScenePosition(center, profile.GimbalOffsetXM, profile.GimbalOffsetYM, yaw, mountBase);
                IReadOnlyList<Vector3> mountFootprint = BuildOrientedRectFootprint(
                    mountCenter,
                    Math.Max(0.02f, profile.GimbalMountLengthM),
                    Math.Max(0.02f, profile.GimbalMountWidthM * profile.BodyRenderWidthScale),
                    0f,
                    yaw);
                DrawPrismWireframe(
                    graphics,
                    mountFootprint,
                    mountHeight,
                    Color.FromArgb(entity.IsAlive ? 246 : 224, BlendColor(bodyColor, Color.FromArgb(96, 100, 112), 0.45f)),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, BlendColor(bodyColor, Color.Black, 0.12f)),
                    null);
                maxHeight = Math.Max(maxHeight, mountBase + mountHeight);
            }

            float turretBase = Math.Max(bodyTop + profile.GimbalMountGapM + profile.GimbalMountHeightM, profile.GimbalHeightM - profile.GimbalBodyHeightM * 0.5f);
            float hingeBase = Math.Max(
                bodyTop + profile.GimbalMountGapM + profile.GimbalMountHeightM,
                turretBase);
            Vector3 hingeCenter = OffsetScenePosition(center, profile.GimbalOffsetXM, profile.GimbalOffsetYM, yaw, hingeBase);
            Vector3 turretForward = new(MathF.Cos(turretYaw), 0f, MathF.Sin(turretYaw));
            Vector3 turretRight = new(-turretForward.Z, 0f, turretForward.X);
            Vector3 pitchedForward = Vector3.Normalize(turretForward * MathF.Cos(gimbalPitch) + Vector3.UnitY * MathF.Sin(gimbalPitch));
            Vector3 pitchedUp = Vector3.Normalize(Vector3.Cross(turretRight, pitchedForward));
            Vector3 turretCenter = hingeCenter
                + pitchedUp * (profile.GimbalBodyHeightM * 0.50f + 0.006f)
                + pitchedForward * (profile.GimbalLengthM * 0.04f);
            DrawCylinderSolid(
                graphics,
                hingeCenter,
                turretRight,
                Vector3.UnitY,
                Math.Max(0.014f, profile.GimbalBodyHeightM * 0.18f),
                Math.Max(0.018f, profile.GimbalWidthM * profile.BodyRenderWidthScale * 0.55f),
                0f,
                Color.FromArgb(entity.IsAlive ? 248 : 226, BlendColor(turretColor, Color.Black, 0.12f)),
                Color.FromArgb(entity.IsAlive ? 252 : 216, BlendColor(turretColor, Color.Black, 0.20f)),
                14);
            DrawOrientedBoxSolid(
                graphics,
                turretCenter,
                pitchedForward,
                turretRight,
                pitchedUp,
                profile.GimbalLengthM,
                profile.GimbalWidthM * profile.BodyRenderWidthScale,
                profile.GimbalBodyHeightM,
                Color.FromArgb(entity.IsAlive ? 248 : 226, turretColor),
                Color.FromArgb(entity.IsAlive ? 255 : 220, turretColor),
                null);
            maxHeight = Math.Max(maxHeight, Math.Max(hingeCenter.Y, turretCenter.Y + profile.GimbalBodyHeightM * 0.55f));

            if (profile.BarrelLengthM > 0.04f && profile.BarrelRadiusM > 0.004f)
            {
                Vector3 barrelAxis = pitchedForward;
                Vector3 pivot = turretCenter
                    + pitchedForward * (profile.GimbalLengthM * 0.5f + profile.BarrelRadiusM * 0.45f)
                    + pitchedUp * (profile.GimbalBodyHeightM * 0.12f);
                Vector3 barrelCenter = pivot + barrelAxis * (profile.BarrelLengthM * 0.5f);

                DrawCylinderSolid(
                    graphics,
                    pivot,
                    turretRight,
                    Vector3.UnitY,
                    Math.Max(0.012f, profile.BarrelRadiusM * 1.35f),
                    Math.Max(0.012f, profile.GimbalWidthM * 0.30f),
                    0f,
                    Color.FromArgb(entity.IsAlive ? 248 : 226, BlendColor(turretColor, Color.Black, 0.08f)),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, BlendColor(turretColor, Color.Black, 0.14f)),
                    12);
                DrawCylinderSolid(
                    graphics,
                    barrelCenter,
                    barrelAxis,
                    turretRight,
                    Math.Max(0.006f, profile.BarrelRadiusM),
                    profile.BarrelLengthM * 0.5f,
                    0f,
                    Color.FromArgb(entity.IsAlive ? 248 : 226, turretColor),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, turretColor),
                    12);
                maxHeight = Math.Max(maxHeight, Math.Max(pivot.Y, barrelCenter.Y) + profile.BarrelRadiusM * 1.6f);

                maxHeight = Math.Max(maxHeight, DrawBarrelLight(graphics, pivot, barrelAxis, turretRight, profile, entity, 1f));
                maxHeight = Math.Max(maxHeight, DrawBarrelLight(graphics, pivot, barrelAxis, turretRight, profile, entity, -1f));
            }
        }

        if (TryProject(center + new Vector3(0f, maxHeight + 0.03f, 0f), out PointF screenLabel, out _))
        {
            using var textBrush = new SolidBrush(Color.FromArgb(entity.IsAlive ? 232 : 142, 228, 232, 238));
            SizeF size = graphics.MeasureString(entity.Id, _smallHudFont);
            graphics.DrawString(entity.Id, _smallHudFont, textBrush, screenLabel.X - size.Width * 0.5f, screenLabel.Y - 11f);
        }

        return maxHeight;
    }

    private IReadOnlyList<Vector3> BuildBodyOutlineFootprint(Vector3 center, float length, float width, float baseHeight, float yaw, bool octagonBody)
    {
        if (!octagonBody)
        {
            return BuildOrientedRectFootprint(center, length, width, baseHeight, yaw);
        }

        float halfLength = length * 0.5f;
        float halfWidth = width * 0.5f;
        float chamfer = MathF.Min(halfLength, halfWidth) * 0.34f;
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
            result[index] = new Vector3(center.X + offset.X, center.Y + baseHeight, center.Z + offset.Y);
        }

        return result;
    }

    private static IReadOnlyList<Vector3> ScaleFootprint(IReadOnlyList<Vector3> footprint, Vector3 center, float baseHeight, float scale)
    {
        Vector3[] result = new Vector3[footprint.Count];
        for (int index = 0; index < footprint.Count; index++)
        {
            Vector3 point = footprint[index];
            result[index] = new Vector3(
                center.X + (point.X - center.X) * scale,
                center.Y + baseHeight,
                center.Z + (point.Z - center.Z) * scale);
        }

        return result;
    }

    private IReadOnlyList<RenderWheelComponent> ResolveWheelComponents(SimulationEntity entity, RobotAppearanceProfile profile, RuntimeChassisMotion motion)
    {
        if (profile.WheelOffsetsM.Count == 0)
        {
            return Array.Empty<RenderWheelComponent>();
        }

        BalanceLegGeometry? leg = string.Equals(profile.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase)
            ? ResolveBalanceLegGeometry(entity, profile, motion)
            : null;
        HashSet<int> dynamicIndices = new();
        if (leg is not null)
        {
            if (string.Equals(profile.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase))
            {
                for (int index = 0; index < profile.WheelOffsetsM.Count; index++)
                {
                    dynamicIndices.Add(index);
                }
            }
            else
            {
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
            }
        }

        float spinBase = (float)(_host.World.GameTimeSec * entity.ChassisRpm * Math.PI / 30.0);
        var result = new List<RenderWheelComponent>(profile.WheelOffsetsM.Count);
        for (int index = 0; index < profile.WheelOffsetsM.Count; index++)
        {
            Vector2 rawOffset = profile.WheelOffsetsM[index];
            float localX = rawOffset.X;
            float localY = rawOffset.Y * profile.BodyRenderWidthScale;
            float centerHeight = Math.Clamp(profile.WheelRadiusM, 0.03f, 0.24f);
            if (leg is not null && dynamicIndices.Contains(index))
            {
                float sideSign = rawOffset.Y < 0f ? -1f : 1f;
                if (Math.Abs(rawOffset.Y) <= 1e-4f)
                {
                    sideSign = index % 2 == 0 ? -1f : 1f;
                }

                localX = leg.Value.Foot.X;
                localY = leg.Value.SideOffset * sideSign;
                centerHeight = leg.Value.Foot.Y;
            }

            float halfThickness = string.Equals(profile.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0.02f, centerHeight * 0.22f)
                : Math.Max(0.03f, centerHeight * 0.32f);
            result.Add(new RenderWheelComponent(
                localX,
                localY,
                centerHeight,
                centerHeight,
                halfThickness,
                spinBase + index * 0.85f));
        }

        return result;
    }

    private static IReadOnlyList<ArmorRenderComponent> ResolveArmorComponents(RobotAppearanceProfile profile)
    {
        float bodyHalfX = profile.BodyLengthM * 0.5f;
        float bodyHalfZ = profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale;
        float gap = Math.Max(0.005f, profile.ArmorPlateGapM);
        float thickness = Math.Max(0.012f, gap * 0.75f);
        float centerY = profile.BodyClearanceM + profile.BodyHeightM * 0.55f;
        float radiusX = bodyHalfX + gap + thickness * 1.35f;
        float radiusZ = bodyHalfZ + gap + thickness * 1.35f;
        IReadOnlyList<float> orbitYaws = profile.ArmorOrbitYawsDeg.Count > 0
            ? profile.ArmorOrbitYawsDeg
            : new[] { 0f, 180f, 90f, 270f };
        IReadOnlyList<float> selfYaws = profile.ArmorSelfYawsDeg.Count > 0
            ? profile.ArmorSelfYawsDeg
            : orbitYaws;
        var result = new List<ArmorRenderComponent>(orbitYaws.Count);
        for (int index = 0; index < orbitYaws.Count; index++)
        {
            float orbitRad = orbitYaws[index] * MathF.PI / 180f;
            float selfYaw = (index < selfYaws.Count ? selfYaws[index] : orbitYaws[index]) * MathF.PI / 180f;
            result.Add(new ArmorRenderComponent(
                new Vector3(MathF.Cos(orbitRad) * radiusX, centerY, MathF.Sin(orbitRad) * radiusZ),
                selfYaw));
        }

        return result;
    }

    private static IReadOnlyList<ArmorLightRenderComponent> ResolveArmorLightComponents(RobotAppearanceProfile profile)
    {
        IReadOnlyList<ArmorRenderComponent> armor = ResolveArmorComponents(profile);
        float armorHalfWidth = profile.ArmorPlateWidthM * 0.5f;
        float lightHalfWidth = Math.Max(0.005f, profile.ArmorLightWidthM * 0.5f);
        float lightOffset = armorHalfWidth + lightHalfWidth + Math.Max(0.004f, profile.ArmorPlateGapM * 0.15f);
        var result = new List<ArmorLightRenderComponent>(armor.Count);
        foreach (ArmorRenderComponent component in armor)
        {
            Vector2 offset = RotateLocalPoint(0f, lightOffset, component.YawRad);
            result.Add(new ArmorLightRenderComponent(
                component.LocalCenter + new Vector3(offset.X, 0f, offset.Y),
                component.LocalCenter - new Vector3(offset.X, 0f, offset.Y),
                component.YawRad));
        }

        return result;
    }

    private float DrawArmorLight(
        Graphics graphics,
        Vector3 center,
        float chassisYaw,
        Vector3 localCenter,
        float localYaw,
        SimulationEntity entity,
        RobotAppearanceProfile profile)
    {
        Color lightColor = entity.IsAlive
            ? Color.FromArgb(226, ResolveTeamColor(entity.Team))
            : Color.FromArgb(140, 110, 120, 138);
        float baseHeight = localCenter.Y - profile.ArmorLightHeightM * 0.5f;
        Vector3 worldCenter = OffsetScenePosition(center, localCenter.X, localCenter.Z, chassisYaw, baseHeight);
        if (!IsArmorPlateVisibleFromCamera(worldCenter, chassisYaw + localYaw))
        {
            return localCenter.Y + profile.ArmorLightHeightM * 0.5f;
        }

        IReadOnlyList<Vector3> footprint = BuildOrientedRectFootprint(
            worldCenter,
            Math.Max(0.006f, profile.ArmorLightWidthM),
            Math.Max(0.010f, profile.ArmorLightLengthM),
            0f,
            chassisYaw + localYaw);
        DrawPrismWireframe(
            graphics,
            footprint,
            Math.Max(0.005f, profile.ArmorLightHeightM),
            lightColor,
            Color.FromArgb(entity.IsAlive ? 238 : 144, BlendColor(lightColor, Color.White, 0.18f)),
            null);
        return localCenter.Y + profile.ArmorLightHeightM * 0.5f;
    }

    private float DrawBarrelLight(
        Graphics graphics,
        Vector3 barrelPivot,
        Vector3 barrelAxis,
        Vector3 barrelRight,
        RobotAppearanceProfile profile,
        SimulationEntity entity,
        float sideSign)
    {
        Vector3 axis = barrelAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(barrelAxis);
        Vector3 right = barrelRight.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(barrelRight);
        float sideOffset = Math.Max(0.01f, profile.BarrelLightWidthM * 1.5f) * sideSign;
        Vector3 lightCenter = barrelPivot
            + axis * (profile.BarrelLengthM * 0.56f)
            + right * sideOffset
            - Vector3.UnitY * (profile.BarrelLightHeightM * 0.10f);
        DrawCylinderSolid(
            graphics,
            lightCenter,
            axis,
            right,
            Math.Max(0.004f, profile.BarrelLightHeightM * 0.28f),
            Math.Max(0.006f, profile.BarrelLightLengthM * 0.5f),
            0f,
            Color.FromArgb(220, ResolveTeamColor(entity.Team)),
            Color.FromArgb(236, BlendColor(ResolveTeamColor(entity.Team), Color.White, 0.20f)),
            8);
        return lightCenter.Y + profile.BarrelLightHeightM * 0.5f;
    }

    private bool IsArmorPlateVisibleFromCamera(Vector3 plateCenter, float plateYaw)
    {
        Vector3 normal = new(MathF.Cos(plateYaw), 0f, MathF.Sin(plateYaw));
        Vector3 toCamera = _cameraPositionM - plateCenter;
        toCamera.Y = 0f;
        if (toCamera.LengthSquared() <= 1e-6f)
        {
            return true;
        }

        toCamera = Vector3.Normalize(toCamera);
        return Vector3.Dot(normal, toCamera) >= -0.08f;
    }

    private bool IsLocalSideVisibleFromCamera(Vector3 entityCenter, float chassisYaw, float localY, float bodyTopM)
    {
        if (Math.Abs(localY) <= 1e-4f)
        {
            return true;
        }

        if (_cameraPositionM.Y >= entityCenter.Y + bodyTopM + 0.35f)
        {
            return true;
        }

        Vector3 right = new(-MathF.Sin(chassisYaw), 0f, MathF.Cos(chassisYaw));
        Vector3 toCamera = _cameraPositionM - entityCenter;
        toCamera.Y = 0f;
        if (toCamera.LengthSquared() <= 1e-6f)
        {
            return true;
        }

        float cameraSide = Vector3.Dot(Vector3.Normalize(toCamera), right);
        return MathF.Sign(localY) * cameraSide >= -0.10f;
    }

    private void DrawTaperedPlate(
        Graphics graphics,
        Vector3 center,
        float yaw,
        float topLength,
        float bottomLength,
        float width,
        float height,
        Color fillColor,
        Color edgeColor)
    {
        float halfWidth = Math.Max(0.001f, width * 0.5f);
        float rearX = -Math.Max(0.001f, bottomLength) * 0.5f;
        float frontBottomX = rearX + Math.Max(0.001f, bottomLength);
        float frontTopX = rearX + Math.Max(0.001f, topLength);
        IReadOnlyList<Vector3> bottom = BuildPlateFace(center, yaw, rearX, frontBottomX, halfWidth, center.Y - height * 0.5f);
        IReadOnlyList<Vector3> top = BuildPlateFace(center, yaw, rearX, frontTopX, halfWidth, center.Y + height * 0.5f);
        DrawGeneralPrism(graphics, bottom, top, fillColor, edgeColor, null);
    }

    private static IReadOnlyList<Vector3> BuildPlateFace(Vector3 center, float yaw, float rearX, float frontX, float halfWidth, float y)
    {
        Vector2[] local =
        {
            new(rearX, -halfWidth),
            new(frontX, -halfWidth),
            new(frontX, halfWidth),
            new(rearX, halfWidth),
        };
        Vector2 forward = new(MathF.Cos(yaw), MathF.Sin(yaw));
        Vector2 right = new(-forward.Y, forward.X);
        Vector3[] result = new Vector3[local.Length];
        for (int index = 0; index < local.Length; index++)
        {
            Vector2 offset = forward * local[index].X + right * local[index].Y;
            result[index] = new Vector3(center.X + offset.X, y, center.Z + offset.Y);
        }

        return result;
    }

    private void DrawGeneralPrism(
        Graphics graphics,
        IReadOnlyList<Vector3> bottomVertices,
        IReadOnlyList<Vector3> topVertices,
        Color fillColor,
        Color edgeColor,
        string? label)
    {
        if (bottomVertices.Count < 3 || topVertices.Count != bottomVertices.Count)
        {
            return;
        }

        var faces = new List<SolidFace>(bottomVertices.Count + 1)
        {
            new(topVertices.ToArray(), 0.78f),
        };
        for (int index = 0; index < bottomVertices.Count; index++)
        {
            int next = (index + 1) % bottomVertices.Count;
            faces.Add(new SolidFace(
                new[]
                {
                    bottomVertices[index],
                    bottomVertices[next],
                    topVertices[next],
                    topVertices[index],
                },
                0.52f));
        }

        DrawSolidFaces(graphics, faces, fillColor, edgeColor, label);
    }

    private void DrawWheelSolid(
        Graphics graphics,
        Vector3 center,
        Vector3 axleDirection,
        Vector3 forwardHint,
        float radius,
        float halfThickness,
        float spinRad,
        Color fillColor,
        Color edgeColor)
    {
        DrawCylinderSolid(
            graphics,
            center,
            axleDirection,
            forwardHint,
            Math.Max(0.016f, radius),
            Math.Max(0.010f, halfThickness),
            spinRad,
            fillColor,
            edgeColor,
            14);

        Vector3 axle = axleDirection.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(axleDirection);
        DrawCylinderSolid(
            graphics,
            center,
            axle,
            Vector3.UnitY,
            Math.Max(0.007f, radius * 0.24f),
            Math.Max(0.008f, halfThickness * 1.10f),
            0f,
            Color.FromArgb(Math.Min(255, fillColor.A + 12), BlendColor(fillColor, Color.White, 0.16f)),
            Color.FromArgb(Math.Min(255, edgeColor.A + 8), BlendColor(edgeColor, Color.Black, 0.08f)),
            10);
    }

    private void DrawCylinderSolid(
        Graphics graphics,
        Vector3 center,
        Vector3 axisDirection,
        Vector3 radialHint,
        float radius,
        float halfLength,
        float spinRad,
        Color fillColor,
        Color edgeColor,
        int segmentCount)
    {
        if (radius <= 1e-4f || halfLength <= 1e-4f)
        {
            return;
        }

        Vector3 axis = axisDirection.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(axisDirection);
        Vector3 radialA = radialHint - axis * Vector3.Dot(radialHint, axis);
        if (radialA.LengthSquared() <= 1e-8f)
        {
            Vector3 fallback = Math.Abs(Vector3.Dot(axis, Vector3.UnitY)) >= 0.92f ? Vector3.UnitX : Vector3.UnitY;
            radialA = fallback - axis * Vector3.Dot(fallback, axis);
        }

        radialA = Vector3.Normalize(radialA);
        Vector3 radialB = Vector3.Normalize(Vector3.Cross(axis, radialA));
        Vector3 spunA = radialA * MathF.Cos(spinRad) + radialB * MathF.Sin(spinRad);
        Vector3 spunB = Vector3.Normalize(Vector3.Cross(axis, spunA));
        Vector3 capA = center - axis * halfLength;
        Vector3 capB = center + axis * halfLength;

        int segments = Math.Max(8, segmentCount);
        Vector3[] ringA = new Vector3[segments];
        Vector3[] ringB = new Vector3[segments];
        for (int index = 0; index < segments; index++)
        {
            float angle = index * MathF.Tau / segments;
            Vector3 radial = spunA * MathF.Cos(angle) + spunB * MathF.Sin(angle);
            ringA[index] = capA + radial * radius;
            ringB[index] = capB + radial * radius;
        }

        var faces = new List<SolidFace>(segments + 2)
        {
            new(ringB.ToArray(), 0.82f),
            new(ringA.Reverse().ToArray(), 0.72f),
        };
        for (int index = 0; index < segments; index++)
        {
            int next = (index + 1) % segments;
            faces.Add(new SolidFace(
                new[]
                {
                    ringA[index],
                    ringA[next],
                    ringB[next],
                    ringB[index],
                },
                0.56f));
        }

        DrawSolidFaces(graphics, faces, fillColor, edgeColor, null);
    }

    private void DrawBeam3d(
        Graphics graphics,
        Vector3 start,
        Vector3 end,
        Vector3 lateralAxis,
        float height,
        float thickness,
        Color fillColor,
        Color edgeColor)
    {
        Vector3 axis = end - start;
        if (axis.LengthSquared() <= 1e-8f)
        {
            return;
        }

        axis = Vector3.Normalize(axis);
        Vector3 side = lateralAxis.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(lateralAxis);
        Vector3 up = Vector3.Cross(side, axis);
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.Cross(axis, Vector3.UnitY);
        }

        if (up.LengthSquared() <= 1e-8f)
        {
            return;
        }

        up = Vector3.Normalize(up);
        Vector3 halfUp = up * Math.Max(0.001f, height * 0.5f);
        Vector3 halfSide = side * Math.Max(0.001f, thickness * 0.5f);

        Vector3 a = start + halfUp + halfSide;
        Vector3 b = end + halfUp + halfSide;
        Vector3 c = end - halfUp + halfSide;
        Vector3 d = start - halfUp + halfSide;
        Vector3 e = start + halfUp - halfSide;
        Vector3 f = end + halfUp - halfSide;
        Vector3 g = end - halfUp - halfSide;
        Vector3 h = start - halfUp - halfSide;

        var faces = new List<SolidFace>(6)
        {
            new(new[] { a, b, c, d }, 0.76f),
            new(new[] { e, f, g, h }, 0.70f),
            new(new[] { a, b, f, e }, 0.62f),
            new(new[] { d, c, g, h }, 0.44f),
            new(new[] { b, c, g, f }, 0.58f),
            new(new[] { a, d, h, e }, 0.56f),
        };
        DrawSolidFaces(graphics, faces, fillColor, edgeColor, null);
    }

    private void DrawOrientedBoxSolid(
        Graphics graphics,
        Vector3 center,
        Vector3 forwardDirection,
        Vector3 rightDirection,
        Vector3 upDirection,
        float length,
        float width,
        float height,
        Color fillColor,
        Color edgeColor,
        string? label)
    {
        if (length <= 1e-4f || width <= 1e-4f || height <= 1e-4f)
        {
            return;
        }

        Vector3 forward = forwardDirection.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(forwardDirection);
        Vector3 right = rightDirection - forward * Vector3.Dot(rightDirection, forward);
        if (right.LengthSquared() <= 1e-8f)
        {
            Vector3 fallback = Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) >= 0.92f ? Vector3.UnitZ : Vector3.UnitY;
            right = Vector3.Cross(fallback, forward);
        }

        right = Vector3.Normalize(right);
        Vector3 up = upDirection - forward * Vector3.Dot(upDirection, forward) - right * Vector3.Dot(upDirection, right);
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.Cross(right, forward);
        }

        up = Vector3.Normalize(up);
        Vector3 halfForward = forward * (length * 0.5f);
        Vector3 halfRight = right * (width * 0.5f);
        Vector3 halfUp = up * (height * 0.5f);

        Vector3 p000 = center - halfForward - halfRight - halfUp;
        Vector3 p100 = center + halfForward - halfRight - halfUp;
        Vector3 p110 = center + halfForward + halfRight - halfUp;
        Vector3 p010 = center - halfForward + halfRight - halfUp;
        Vector3 p001 = center - halfForward - halfRight + halfUp;
        Vector3 p101 = center + halfForward - halfRight + halfUp;
        Vector3 p111 = center + halfForward + halfRight + halfUp;
        Vector3 p011 = center - halfForward + halfRight + halfUp;

        var faces = new List<SolidFace>(6)
        {
            new(new[] { p001, p101, p111, p011 }, 0.78f),
            new(new[] { p000, p010, p110, p100 }, 0.48f),
            new(new[] { p100, p110, p111, p101 }, 0.64f),
            new(new[] { p000, p001, p011, p010 }, 0.54f),
            new(new[] { p010, p011, p111, p110 }, 0.58f),
            new(new[] { p000, p100, p101, p001 }, 0.56f),
        };
        DrawSolidFaces(graphics, faces, fillColor, edgeColor, label);
    }

    private void DrawSolidFaces(
        Graphics graphics,
        IReadOnlyList<SolidFace> faces,
        Color fillColor,
        Color edgeColor,
        string? label)
    {
        var projectedFaces = new List<ProjectedFace>(faces.Count);
        Vector3 labelPoint = Vector3.Zero;
        int labelVertices = 0;
        foreach (SolidFace solidFace in faces)
        {
            foreach (Vector3 vertex in solidFace.Vertices)
            {
                labelPoint += vertex;
                labelVertices++;
            }

            if (TryBuildProjectedFace(
                solidFace.Vertices,
                ShadeFaceColor(fillColor, solidFace.Vertices, solidFace.Ambient),
                edgeColor,
                out ProjectedFace projected))
            {
                projectedFaces.Add(projected);
            }
        }

        projectedFaces.Sort((left, right) => right.AverageDepth.CompareTo(left.AverageDepth));
        foreach (ProjectedFace face in projectedFaces)
        {
            using var faceBrush = new SolidBrush(face.FillColor);
            using var facePen = new Pen(face.EdgeColor, 1f);
            graphics.FillPolygon(faceBrush, face.Points);
            graphics.DrawPolygon(facePen, face.Points);
        }

        if (!string.IsNullOrWhiteSpace(label) && labelVertices > 0)
        {
            labelPoint /= labelVertices;
            if (TryProject(labelPoint + new Vector3(0f, 0.05f, 0f), out PointF screenLabel, out _))
            {
                using var textBrush = new SolidBrush(Color.FromArgb(230, 230, 234, 242));
                SizeF size = graphics.MeasureString(label, _smallHudFont);
                graphics.DrawString(label, _smallHudFont, textBrush, screenLabel.X - size.Width * 0.5f, screenLabel.Y - 11f);
            }
        }
    }

    private float DrawBalanceLegAssembly(
        Graphics graphics,
        Vector3 center,
        float yaw,
        Vector3 lateralAxis,
        RobotAppearanceProfile profile,
        SimulationEntity entity,
        Color legColor,
        RuntimeChassisMotion motion)
    {
        BalanceLegGeometry leg = ResolveBalanceLegGeometry(entity, profile, motion);
        float maxHeight = Math.Max(leg.UpperFront.Y, leg.KneeCenter.Y);
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float sideSign = sideIndex == 0 ? -1f : 1f;
            float sideOffset = leg.SideOffset * sideSign;

            Vector3 upperFront = OffsetScenePosition(center, leg.UpperFront.X, sideOffset, yaw, leg.UpperFront.Y);
            Vector3 upperRear = OffsetScenePosition(center, leg.UpperRear.X, sideOffset, yaw, leg.UpperRear.Y);
            Vector3 kneeFront = OffsetScenePosition(center, leg.KneeFront.X, sideOffset, yaw, leg.KneeFront.Y);
            Vector3 kneeRear = OffsetScenePosition(center, leg.KneeRear.X, sideOffset, yaw, leg.KneeRear.Y);
            Vector3 kneeCenter = OffsetScenePosition(center, leg.KneeCenter.X, sideOffset, yaw, leg.KneeCenter.Y);
            Vector3 foot = OffsetScenePosition(center, leg.Foot.X, sideOffset, yaw, leg.Foot.Y);

            DrawBeam3d(
                graphics,
                upperFront,
                kneeFront,
                lateralAxis,
                Math.Max(0.010f, profile.RearClimbAssistUpperHeightM),
                Math.Max(0.010f, profile.RearClimbAssistUpperWidthM),
                Color.FromArgb(entity.IsAlive ? 246 : 224, legColor),
                Color.FromArgb(entity.IsAlive ? 250 : 216, legColor));
            DrawBeam3d(
                graphics,
                upperRear,
                kneeRear,
                lateralAxis,
                Math.Max(0.010f, profile.RearClimbAssistUpperHeightM),
                Math.Max(0.010f, profile.RearClimbAssistUpperWidthM),
                Color.FromArgb(entity.IsAlive ? 244 : 222, legColor),
                Color.FromArgb(entity.IsAlive ? 250 : 216, legColor));
            DrawBeam3d(
                graphics,
                kneeCenter,
                foot,
                lateralAxis,
                Math.Max(0.010f, profile.RearClimbAssistLowerHeightM),
                Math.Max(0.010f, profile.RearClimbAssistLowerWidthM),
                Color.FromArgb(entity.IsAlive ? 248 : 226, legColor),
                Color.FromArgb(entity.IsAlive ? 250 : 216, legColor));

            DrawLegHub(graphics, center, yaw, sideOffset, leg.UpperFront, leg.HingeRadius, entity, legColor);
            DrawLegHub(graphics, center, yaw, sideOffset, leg.UpperRear, leg.HingeRadius, entity, legColor);
            DrawLegHub(graphics, center, yaw, sideOffset, leg.KneeFront, leg.HingeRadius, entity, legColor);
            DrawLegHub(graphics, center, yaw, sideOffset, leg.KneeRear, leg.HingeRadius, entity, legColor);
            DrawLegHub(graphics, center, yaw, sideOffset, leg.Foot, leg.HingeRadius, entity, legColor);

            maxHeight = Math.Max(maxHeight, Math.Max(leg.Foot.Y, leg.KneeCenter.Y) + leg.HingeRadius);
        }

        return maxHeight;
    }

    private void DrawLegHub(
        Graphics graphics,
        Vector3 center,
        float yaw,
        float sideOffset,
        Vector2 localPoint,
        float radius,
        SimulationEntity entity,
        Color legColor)
    {
        float baseHeight = localPoint.Y - radius;
        Vector3 hubCenter = OffsetScenePosition(center, localPoint.X, sideOffset, yaw, baseHeight);
        IReadOnlyList<Vector3> footprint = BuildOrientedRectFootprint(
            hubCenter,
            Math.Max(0.008f, radius * 1.4f),
            Math.Max(0.008f, radius * 1.4f),
            0f,
            yaw);
        DrawPrismWireframe(
            graphics,
            footprint,
            Math.Max(0.010f, radius * 2f),
            Color.FromArgb(entity.IsAlive ? 248 : 226, BlendColor(legColor, Color.White, 0.12f)),
            Color.FromArgb(entity.IsAlive ? 250 : 216, BlendColor(legColor, Color.Black, 0.10f)),
            null);
    }

    private static BalanceLegGeometry ResolveBalanceLegGeometry(SimulationEntity entity, RobotAppearanceProfile profile, RuntimeChassisMotion motion)
    {
        float bodyHalfX = profile.BodyLengthM * 0.5f;
        float wheelRadius = Math.Max(0.018f, profile.WheelRadiusM);
        float footX = profile.WheelOffsetsM.Count > 0 ? profile.WheelOffsetsM.Min(offset => offset.X) : -bodyHalfX * 0.78f;
        footX += motion.RearFootReachM;
        float footY = wheelRadius + motion.RearFootRaiseM;
        float wheelOuter = profile.WheelOffsetsM.Count > 0
            ? profile.WheelOffsetsM.Max(offset => Math.Abs(offset.Y) * profile.BodyRenderWidthScale)
            : profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale + wheelRadius * 0.55f;
        float sideOffset = Math.Max(
            profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale * 0.45f,
            wheelOuter - profile.RearClimbAssistInnerOffsetM * profile.BodyRenderWidthScale);
        float anchorX = -bodyHalfX + profile.RearClimbAssistMountOffsetXM;
        float anchorY = profile.RearClimbAssistMountHeightM + motion.BodyLiftM;
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
        return new BalanceLegGeometry(
            new Vector2(anchorX + halfGap, anchorY),
            new Vector2(anchorX - halfGap, anchorY),
            knee,
            new Vector2(knee.X + halfGap, knee.Y),
            new Vector2(knee.X - halfGap, knee.Y),
            foot,
            sideOffset,
            Math.Max(0.008f, profile.RearClimbAssistHingeRadiusM));
    }

    private static RuntimeChassisMotion ResolveRuntimeChassisMotion(SimulationEntity entity)
    {
        float bodyLift = 0f;
        float frontDrop = 0f;
        float frontRaise = 0f;
        float rearFootRaise = 0f;
        float rearFootReach = 0f;

        if (entity.TraversalActive)
        {
            float progress = Math.Clamp((float)entity.TraversalProgress, 0f, 1f);
            bool heavyChassis =
                string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase);
            if (heavyChassis)
            {
                rearFootRaise = -0.40f * progress;
                bodyLift = 0.05f * MathF.Sin(progress * MathF.PI);
            }
            else if (progress < 0.4f)
            {
                float ratio = progress / 0.4f;
                frontDrop = 0.10f + 0.18f * ratio;
                frontRaise = 0.05f + 0.06f * ratio;
                rearFootRaise = -0.06f * ratio;
                rearFootReach = -0.05f * ratio;
                bodyLift = 0.05f * ratio;
            }
            else if (progress < 0.7f)
            {
                frontDrop = 0.18f;
                frontRaise = 0.08f;
                rearFootRaise = -0.08f;
                rearFootReach = -0.08f;
                bodyLift = 0.08f;
            }
            else
            {
                float ratio = (progress - 0.7f) / 0.3f;
                frontDrop = 0.18f - 0.10f * ratio;
                frontRaise = 0.08f - 0.04f * ratio;
                rearFootRaise = -0.08f * (1f - ratio);
                rearFootReach = -0.08f * (1f - ratio);
                bodyLift = 0.08f * (1f - ratio);
            }
        }

        if (entity.ChassisSupportsJump && entity.AirborneHeightM > 1e-4)
        {
            float jumpWave = Math.Clamp((float)(entity.AirborneHeightM / 0.70), 0f, 1f);
            bodyLift = Math.Max(bodyLift, 0.16f * jumpWave);
            frontDrop = Math.Max(frontDrop, 0.08f * jumpWave);
            frontRaise = Math.Max(frontRaise, 0.06f * jumpWave);
            rearFootRaise = Math.Min(rearFootRaise, -0.18f * jumpWave);
            rearFootReach = Math.Min(rearFootReach, -0.10f * jumpWave);
        }

        return new RuntimeChassisMotion(bodyLift, frontDrop, frontRaise, rearFootRaise, rearFootReach);
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

    private static Vector2 RotateLocalPoint(float x, float y, float yawRad)
    {
        float cos = MathF.Cos(yawRad);
        float sin = MathF.Sin(yawRad);
        return new Vector2(x * cos - y * sin, x * sin + y * cos);
    }
}
