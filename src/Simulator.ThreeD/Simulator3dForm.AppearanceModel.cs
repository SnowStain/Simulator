using System.Numerics;
using System.Runtime.CompilerServices;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private const float RenderWheelHalfWidthM = 0.02f;
    private const float HeroSubviewCameraBodyLengthM = 0.07f;
    private const float HeroSubviewCameraBodyWidthM = 0.03f;
    private const float HeroSubviewCameraBodyHeightM = 0.03f;
    private const float HeroSubviewCameraConnectorLengthM = 0.08f;
    private const float HeroSubviewCameraConnectorRadiusM = 0.015f;
    private const float HeroSubviewCameraLensRadiusM = 0.010f;
    private const float HeroSubviewCameraLensHalfLengthM = 0.010f;
    private bool _useProfileColorsForVehiclePreview;
    private static readonly ConditionalWeakTable<RobotAppearanceProfile, AppearanceDerivedGeometryCache> _appearanceGeometryCache = new();

    private readonly record struct SolidFace(Vector3[] Vertices, float Ambient);

    private readonly record struct RenderWheelComponent(
        float LocalX,
        float LocalY,
        float CenterHeightM,
        float RadiusM,
        float HalfThicknessM,
        float SpinRad,
        bool FixedToLeg);

    private readonly record struct ArmorRenderComponent(Vector3 LocalCenter, float YawRad, float PitchRad, float RollRad);

    private readonly record struct ArmorLightRenderComponent(Vector3 LocalCenterA, Vector3 LocalCenterB, float YawRad, float PitchRad, float RollRad);

    private readonly record struct FrictionWheelPose(Vector3 Center, Vector3 Axis, Vector3 RadialHint, float SpinSign, int Index);

    private readonly record struct AppearancePartPose(
        string PartKey,
        int ComponentIndex,
        Vector3 Center,
        Vector3 Forward,
        Vector3 Right,
        Vector3 Up,
        float LengthM,
        float WidthM,
        float HeightM);

    private readonly record struct VisualArmorPlatePose(
        Vector3 Center,
        Vector3 Forward,
        Vector3 Right,
        Vector3 Up,
        float HalfWidth,
        float HalfHeight,
        string Label);

    private readonly record struct RuntimeChassisMotion(
        float BodyLiftM,
        float FrontDropM,
        float FrontRaiseM,
        float RearFootRaiseM,
        float RearFootReachM,
        float LongitudinalOffsetM,
        float LateralOffsetM);

    private readonly record struct BalanceLegGeometry(
        Vector2 UpperFront,
        Vector2 UpperRear,
        Vector2 KneeCenter,
        Vector2 KneeFront,
        Vector2 KneeRear,
        Vector2 Foot,
        float SideOffset,
        float HingeRadius);

    private sealed class AppearanceDerivedGeometryCache
    {
        public IReadOnlyList<ArmorRenderComponent> ArmorComponents { get; init; } = Array.Empty<ArmorRenderComponent>();

        public IReadOnlyList<ArmorLightRenderComponent> ArmorLightComponents { get; init; } = Array.Empty<ArmorLightRenderComponent>();
    }

    private float DrawEntityAppearanceModelModern(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile)
    {
        CanonicalizeEntityTeamForVisuals(entity);
        float yaw = ResolveAppearanceYaw(entity);
        float turretYaw = ResolveAppearanceTurretYaw(entity);
        float gimbalPitch = -(float)(entity.GimbalPitchDeg * Math.PI / 180.0);
        Color bodyColor = ResolveAppearanceMaterialColor(profile.BodyColor, entity.IsAlive, 0.00f);
        Color turretColor = ResolveAppearanceMaterialColor(profile.TurretColor, entity.IsAlive, 0.05f);
        Color wheelColor = ResolveAppearanceMaterialColor(profile.WheelColor, entity.IsAlive, -0.04f);
        Color armorColor = ResolveAppearanceMaterialColor(profile.ArmorColor, entity.IsAlive, 0.02f);
        RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(entity);
        ResolveChassisAxes(yaw, entity.ChassisPitchDeg, entity.ChassisRollDeg, out Vector3 chassisForward3, out Vector3 chassisRight3, out Vector3 chassisUp3);
        Vector3 chassisMotionOffset = ResolveRuntimeChassisSceneOffset(entity, motion);
        center += chassisMotionOffset;

        float bodyLength = Math.Max(0.12f, profile.BodyLengthM);
        float bodyWidth = Math.Max(0.10f, profile.BodyWidthM * profile.BodyRenderWidthScale);
        float bodyHeight = Math.Max(0.08f, profile.BodyHeightM);
        float bodyBase = Math.Max(0f, profile.BodyClearanceM + motion.BodyLiftM);
        bool octagonBody = string.Equals(profile.BodyShape, "octagon", StringComparison.OrdinalIgnoreCase);
        float maxHeight = bodyBase + bodyHeight;
        if (!octagonBody)
        {
            Color bodyFill = Color.FromArgb(entity.IsAlive ? 248 : 232, bodyColor);
            Color bodyEdge = Color.FromArgb(entity.IsAlive ? 255 : 220, bodyColor);
            if (HasBodyFaceTilt(profile))
            {
                DrawTiltedBodySolid(
                    graphics,
                    center,
                    chassisForward3,
                    chassisRight3,
                    chassisUp3,
                    bodyLength,
                    bodyWidth,
                    bodyHeight,
                    bodyBase,
                    profile,
                    bodyFill,
                    bodyEdge);
            }
            else
            {
                DrawOrientedBoxSolid(
                    graphics,
                    center + chassisUp3 * (bodyBase + bodyHeight * 0.5f),
                    chassisForward3,
                    chassisRight3,
                    chassisUp3,
                    bodyLength,
                    bodyWidth,
                    bodyHeight,
                    bodyFill,
                    bodyEdge,
                    null);
            }

            float capHeight = Math.Max(0.015f, bodyHeight * 0.12f);
            DrawOrientedBoxSolid(
                graphics,
                center + chassisUp3 * (bodyBase + bodyHeight * 0.72f + capHeight * 0.5f),
                chassisForward3,
                chassisRight3,
                chassisUp3,
                bodyLength * 0.80f,
                bodyWidth * 0.80f,
                capHeight,
                Color.FromArgb(entity.IsAlive ? 246 : 226, BlendColor(bodyColor, Color.White, 0.14f)),
                Color.FromArgb(entity.IsAlive ? 250 : 216, BlendColor(bodyColor, Color.Black, 0.08f)),
                null);
            maxHeight = Math.Max(maxHeight, bodyBase + bodyHeight * 0.72f + capHeight);
        }
        else
        {
            IReadOnlyList<Vector3> bodyFootprint = BuildBodyOutlineFootprint(center, bodyLength, bodyWidth, bodyBase, yaw, octagonBody);
            DrawPrismWireframe(
                graphics,
                bodyFootprint,
                bodyHeight,
                Color.FromArgb(entity.IsAlive ? 248 : 232, bodyColor),
                Color.FromArgb(entity.IsAlive ? 255 : 220, bodyColor),
                null);

            float capBase = bodyBase + bodyHeight * 0.72f;
            float capHeight = Math.Max(0.015f, bodyHeight * 0.12f);
            IReadOnlyList<Vector3> bodyCap = ScaleFootprint(bodyFootprint, center, capBase, 0.78f);
            DrawPrismWireframe(
                graphics,
                bodyCap,
                capHeight,
                Color.FromArgb(entity.IsAlive ? 246 : 226, BlendColor(bodyColor, Color.White, 0.14f)),
                Color.FromArgb(entity.IsAlive ? 250 : 216, BlendColor(bodyColor, Color.Black, 0.08f)),
                null);
            maxHeight = Math.Max(maxHeight, capBase + capHeight);
        }

        if (!SimulationCombatMath.IsStructure(entity))
        {
            DrawRearHealthLightBar(
                graphics,
                entity,
                center,
                chassisForward3,
                chassisRight3,
                chassisUp3,
                bodyLength,
                bodyWidth,
                bodyBase + bodyHeight,
                ResolveFixedRobotLightColor(ResolveCanonicalEntityTeam(entity)));
            maxHeight = Math.Max(maxHeight, bodyBase + bodyHeight + 0.026f);
        }

        Vector2 forward2 = new(MathF.Cos(yaw), MathF.Sin(yaw));
        Vector2 right2 = new(-forward2.Y, forward2.X);
        Vector3 lateralAxis = chassisRight3;

        foreach (RenderWheelComponent wheel in ResolveWheelComponents(entity, profile, motion))
        {
            Vector3 wheelForward = chassisForward3;
            Vector3 wheelAxle = lateralAxis;
            if (string.Equals(profile.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase))
            {
                Vector2 inwardLocal = new(-wheel.LocalX, -wheel.LocalY);
                if (inwardLocal.LengthSquared() > 1e-6f)
                {
                    inwardLocal = Vector2.Normalize(inwardLocal);
                    wheelAxle = Vector3.Normalize(
                        chassisForward3 * inwardLocal.X
                        + chassisRight3 * inwardLocal.Y);
                    wheelForward = chassisUp3;
                }
            }

            float resolvedCenterHeight = ResolveTerrainAwareWheelCenterHeight(
                entity,
                center,
                forward2,
                right2,
                chassisForward3,
                chassisRight3,
                wheelAxle,
                chassisUp3,
                wheel.LocalX,
                wheel.LocalY,
                wheel.RadiusM,
                wheel.CenterHeightM,
                wheel.FixedToLeg);
            Vector3 wheelCenter = center
                + chassisForward3 * wheel.LocalX
                + chassisRight3 * wheel.LocalY
                + chassisUp3 * resolvedCenterHeight;

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

            maxHeight = Math.Max(maxHeight, resolvedCenterHeight + wheel.RadiusM);
        }

        string? lockedPlateId = ResolveLockedPlateIdFor(entity);
        IReadOnlyList<ArmorRenderComponent> armorComponents = ResolveArmorComponents(profile);
        float plateHeight = Math.Max(0.04f, profile.ArmorPlateHeightM);
        float plateSpan = Math.Max(0.04f, profile.ArmorPlateLengthM);
        float plateThickness = ResolveArmorPlateThickness(profile);
        for (int armorIndex = 0; armorIndex < armorComponents.Count; armorIndex++)
        {
            ArmorRenderComponent component = armorComponents[armorIndex];
            Vector3 armorCenter = OffsetScenePosition(
                center,
                component.LocalCenter.X,
                component.LocalCenter.Z,
                component.LocalCenter.Y + motion.BodyLiftM,
                chassisForward3,
                chassisRight3,
                chassisUp3);
            bool locked = string.Equals(lockedPlateId, $"armor_{armorIndex + 1}", StringComparison.OrdinalIgnoreCase);
            Color backingFill = Color.FromArgb(entity.IsAlive ? 252 : 226, 42, 46, 54);
            Color backingEdge = Color.FromArgb(entity.IsAlive ? 255 : 220, 18, 20, 24);
            Color plateFill = locked
                ? Color.FromArgb(255, 255, 211, 84)
                : Color.FromArgb(entity.IsAlive ? 248 : 226, armorColor);
            Color plateEdge = locked
                ? Color.FromArgb(255, 255, 244, 150)
                : Color.FromArgb(entity.IsAlive ? 255 : 216, armorColor);
            ResolveLocalPlaneAxes(chassisForward3, chassisRight3, component.YawRad, out Vector3 plateForward3, out Vector3 plateRight3);
            ResolveRotatedAxes(
                plateForward3,
                plateRight3,
                chassisUp3,
                new Vector3(0f, component.PitchRad * 180f / MathF.PI, component.RollRad * 180f / MathF.PI),
                out plateForward3,
                out plateRight3,
                out Vector3 plateUp3);
            DrawOrientedBoxSolid(
                graphics,
                armorCenter,
                plateForward3,
                plateRight3,
                plateUp3,
                plateThickness * 1.08f,
                plateSpan * 1.08f,
                plateHeight * 1.08f,
                backingFill,
                backingEdge,
                null);
            DrawOrientedBoxSolid(
                graphics,
                armorCenter,
                plateForward3,
                plateRight3,
                plateUp3,
                plateThickness,
                plateSpan,
                plateHeight,
                plateFill,
                plateEdge,
                null);
            maxHeight = Math.Max(maxHeight, component.LocalCenter.Y + plateHeight * 0.5f);
        }

        foreach (ArmorLightRenderComponent component in ResolveArmorLightComponents(profile))
        {
            Vector3 lightCenterA = component.LocalCenterA + new Vector3(0f, motion.BodyLiftM, 0f);
            Vector3 lightCenterB = component.LocalCenterB + new Vector3(0f, motion.BodyLiftM, 0f);
            maxHeight = Math.Max(maxHeight, DrawArmorLight(graphics, center, chassisForward3, chassisRight3, chassisUp3, lightCenterA, component, entity, profile));
            maxHeight = Math.Max(maxHeight, DrawArmorLight(graphics, center, chassisForward3, chassisRight3, chassisUp3, lightCenterB, component, entity, profile));
        }

        if (!string.Equals(profile.FrontClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            float frontForward = bodyLength * 0.5f + profile.FrontClimbAssistForwardOffsetM + profile.FrontClimbAssistBottomLengthM * 0.5f;
            float frontSide = Math.Max(bodyWidth * 0.45f, bodyWidth * 0.5f - profile.FrontClimbAssistInnerOffsetM);
            float frontContactLift = motion.BodyLiftM;
            float plateCenterHeight = profile.WheelRadiusM + profile.FrontClimbAssistPlateHeightM * 0.5f + frontContactLift;
            Color climbColor = BlendColor(bodyColor, Color.FromArgb(92, 96, 108), 0.34f);
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                float sideSign = sideIndex == 0 ? -1f : 1f;
                Vector3 plateCenter = OffsetScenePosition(center, frontForward, frontSide * sideSign, plateCenterHeight, chassisForward3, chassisRight3, chassisUp3);
                DrawTaperedPlate(
                    graphics,
                    plateCenter,
                    chassisForward3,
                    chassisRight3,
                    chassisUp3,
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
                maxHeight = Math.Max(maxHeight, DrawBalanceLegAssembly(graphics, center, yaw, lateralAxis, chassisForward3, chassisRight3, chassisUp3, profile, entity, legColor, motion));
            }
            else
            {
                float upperBase = Math.Max(bodyBase + bodyHeight * 0.55f, profile.RearClimbAssistMountHeightM + motion.BodyLiftM);
                float lowerBase = Math.Max(0.02f, profile.WheelRadiusM * 0.18f + motion.BodyLiftM);
                float rearForward = -bodyLength * 0.5f + profile.RearClimbAssistMountOffsetXM;
                float rearSide = Math.Max(bodyWidth * 0.30f, bodyWidth * 0.5f - profile.RearClimbAssistInnerOffsetM);
                for (int sideIndex = 0; sideIndex < 2; sideIndex++)
                {
                    float sideSign = sideIndex == 0 ? -1f : 1f;
                    Vector3 upperCenter = OffsetScenePosition(center, rearForward, rearSide * sideSign, upperBase, chassisForward3, chassisRight3, chassisUp3);
                    DrawOrientedBoxSolid(
                        graphics,
                        upperCenter,
                        chassisForward3,
                        chassisRight3,
                        chassisUp3,
                        profile.RearClimbAssistUpperLengthM,
                        profile.RearClimbAssistUpperWidthM,
                        profile.RearClimbAssistUpperHeightM,
                        Color.FromArgb(entity.IsAlive ? 246 : 224, legColor),
                        Color.FromArgb(entity.IsAlive ? 250 : 216, legColor),
                        null);

                    Vector3 lowerCenter = OffsetScenePosition(center, rearForward - profile.RearClimbAssistLowerLengthM * 0.2f, rearSide * sideSign, lowerBase, chassisForward3, chassisRight3, chassisUp3);
                    DrawOrientedBoxSolid(
                        graphics,
                        lowerCenter,
                        chassisForward3,
                        chassisRight3,
                        chassisUp3,
                        profile.RearClimbAssistLowerLengthM,
                        profile.RearClimbAssistLowerWidthM,
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
            ResolveWorldTurretAxes(turretYaw, gimbalPitch, out Vector3 turretForward, out Vector3 turretRight, out Vector3 pitchedForward, out Vector3 pitchedUp);
            if (profile.GimbalMountGapM + profile.GimbalMountHeightM > 0.02f)
            {
                float mountHeight = Math.Max(0.02f, profile.GimbalMountGapM + profile.GimbalMountHeightM + 0.08f);
                float mountCenterHeight = bodyTop + mountHeight * 0.5f;
                Vector3 mountCenter = OffsetScenePosition(center, profile.GimbalOffsetXM, profile.GimbalOffsetYM, mountCenterHeight, chassisForward3, chassisRight3, chassisUp3);
                DrawOrientedBoxSolid(
                    graphics,
                    mountCenter,
                    turretForward,
                    turretRight,
                    chassisUp3,
                    Math.Max(0.02f, profile.GimbalMountLengthM),
                    Math.Max(0.02f, profile.GimbalMountWidthM * profile.BodyRenderWidthScale),
                    mountHeight,
                    Color.FromArgb(entity.IsAlive ? 246 : 224, BlendColor(bodyColor, Color.FromArgb(96, 100, 112), 0.45f)),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, BlendColor(bodyColor, Color.Black, 0.12f)),
                    null);
                maxHeight = Math.Max(maxHeight, bodyTop + mountHeight);
            }

            float turretBase = Math.Max(bodyTop + profile.GimbalMountGapM + profile.GimbalMountHeightM, profile.GimbalHeightM - profile.GimbalBodyHeightM * 0.5f);
            float hingeBase = Math.Max(
                bodyTop + profile.GimbalMountGapM + profile.GimbalMountHeightM,
                turretBase);
            Vector3 hingeCenter = OffsetScenePosition(
                center,
                profile.GimbalOffsetXM + profile.GimbalRelativeOffsetXM,
                profile.GimbalOffsetYM + profile.GimbalRelativeOffsetZM,
                hingeBase + profile.GimbalRelativeOffsetYM,
                chassisForward3,
                chassisRight3,
                chassisUp3);
            Vector3 turretCenter = hingeCenter
                + pitchedUp * (profile.GimbalBodyHeightM * 0.50f);
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
                    + pitchedForward * (profile.GimbalLengthM * 0.5f + profile.BarrelOffsetXM)
                    + pitchedUp * profile.BarrelOffsetYM
                    + turretRight * profile.BarrelOffsetZM;
                Vector3 barrelCenter = pivot + barrelAxis * (profile.BarrelLengthM * 0.5f);

                DrawHollowOctagonalBarrel(
                    graphics,
                    barrelCenter,
                    barrelAxis,
                    turretRight,
                    pitchedUp,
                    Math.Max(0.006f, profile.BarrelRadiusM),
                    profile.BarrelLengthM * 0.5f,
                    profile.BarrelOctagonLongEdgeM,
                    profile.BarrelOctagonShortEdgeM,
                    Color.FromArgb(entity.IsAlive ? 248 : 226, turretColor),
                    Color.FromArgb(entity.IsAlive ? 250 : 216, turretColor));
                maxHeight = Math.Max(maxHeight, Math.Max(pivot.Y, barrelCenter.Y) + profile.BarrelRadiusM * 1.6f);

                maxHeight = Math.Max(maxHeight, DrawBarrelFrictionWheels(graphics, pivot, barrelAxis, turretRight, pitchedUp, profile, entity));
                maxHeight = Math.Max(maxHeight, DrawBarrelLight(graphics, pivot, barrelAxis, turretRight, pitchedUp, profile, entity, 1f));
                maxHeight = Math.Max(maxHeight, DrawBarrelLight(graphics, pivot, barrelAxis, turretRight, pitchedUp, profile, entity, -1f));
            }

            if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
            {
                ResolveHeroSubviewCameraMount(
                    turretCenter,
                    pitchedForward,
                    turretRight,
                    pitchedUp,
                    profile.GimbalWidthM * profile.BodyRenderWidthScale,
                    profile.GimbalBodyHeightM,
                    out Vector3 cameraCenter,
                    out Vector3 cameraForward,
                    out Vector3 cameraRight,
                    out Vector3 cameraUp,
                    out Vector3 connectorBase);
                Color cameraColor = BlendColor(turretColor, Color.FromArgb(92, 98, 108), 0.22f);
                Vector3 connectorAxis = Vector3.Normalize(cameraForward + cameraUp);
                Vector3 connectorAnchor = connectorBase
                    + connectorAxis * HeroSubviewCameraConnectorLengthM
                    - cameraRight * 0.006f;
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 offset = cameraRight * (0.010f * side);
                    Vector3 start = connectorBase + offset;
                    Vector3 end = connectorAnchor + offset;
                    Vector3 axis = end - start;
                    float length = axis.Length();
                    if (length > 1e-4f)
                    {
                        DrawCylinderSolid(
                            graphics,
                            (start + end) * 0.5f,
                            axis,
                            cameraUp,
                            HeroSubviewCameraConnectorRadiusM,
                            length * 0.5f,
                            0f,
                            Color.FromArgb(entity.IsAlive ? 242 : 220, BlendColor(cameraColor, Color.Black, 0.10f)),
                            Color.FromArgb(entity.IsAlive ? 248 : 214, BlendColor(cameraColor, Color.Black, 0.18f)),
                            10);
                    }
                }

                DrawOrientedBoxSolid(
                    graphics,
                    cameraCenter,
                    cameraForward,
                    cameraRight,
                    cameraUp,
                    HeroSubviewCameraBodyLengthM,
                    HeroSubviewCameraBodyWidthM,
                    HeroSubviewCameraBodyHeightM,
                    Color.FromArgb(entity.IsAlive ? 246 : 220, cameraColor),
                    Color.FromArgb(entity.IsAlive ? 252 : 214, BlendColor(cameraColor, Color.Black, 0.14f)),
                    null);
                DrawCylinderSolid(
                    graphics,
                    cameraCenter + cameraForward * (HeroSubviewCameraBodyLengthM * 0.54f),
                    cameraForward,
                    cameraUp,
                    HeroSubviewCameraLensRadiusM,
                    HeroSubviewCameraLensHalfLengthM,
                    0f,
                    Color.FromArgb(entity.IsAlive ? 252 : 222, 28, 34, 42),
                    Color.FromArgb(entity.IsAlive ? 255 : 226, 18, 22, 28),
                    12);
                maxHeight = Math.Max(maxHeight, cameraCenter.Y + HeroSubviewCameraBodyHeightM);
            }
        }

        maxHeight = Math.Max(
            maxHeight,
            DrawCustomAppearanceAttachments(
                graphics,
                entity,
                center,
                profile,
                motion,
                yaw,
                turretYaw,
                gimbalPitch,
                bodyLength,
                bodyWidth,
                bodyHeight,
                bodyBase,
                chassisForward3,
                chassisRight3,
                chassisUp3));

        if (!_suppressEntityLabels
            && TryProject(center + new Vector3(0f, maxHeight + 0.03f, 0f), out PointF screenLabel, out _))
        {
            using var textBrush = new SolidBrush(Color.FromArgb(entity.IsAlive ? 232 : 142, 228, 232, 238));
            SizeF size = graphics.MeasureString(entity.Id, _smallHudFont);
            graphics.DrawString(entity.Id, _smallHudFont, textBrush, screenLabel.X - size.Width * 0.5f, screenLabel.Y - 11f);
        }

        return maxHeight;
    }

    private static void ResolveHeroSubviewCameraMount(
        Vector3 turretCenter,
        Vector3 pitchedForward,
        Vector3 turretRight,
        Vector3 pitchedUp,
        float turretWidthM,
        float turretHeightM,
        out Vector3 cameraCenter,
        out Vector3 cameraForward,
        out Vector3 cameraRight,
        out Vector3 cameraUp,
        out Vector3 connectorBase)
    {
        cameraRight = turretRight.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(turretRight);
        cameraUp = Vector3.UnitY;
        cameraForward = Vector3.Cross(cameraRight, cameraUp);
        if (cameraForward.LengthSquared() <= 1e-8f)
        {
            cameraForward = pitchedForward.LengthSquared() <= 1e-8f
                ? Vector3.UnitX
                : Vector3.Normalize(new Vector3(pitchedForward.X, 0f, pitchedForward.Z));
        }

        if (cameraForward.LengthSquared() <= 1e-8f)
        {
            cameraForward = Vector3.UnitX;
        }

        cameraForward = Vector3.Normalize(cameraForward);
        cameraRight = Vector3.Normalize(Vector3.Cross(cameraUp, cameraForward));

        connectorBase = turretCenter
            - cameraRight * MathF.Max(0.018f, turretWidthM * 0.46f)
            + cameraUp * MathF.Max(0.010f, turretHeightM * 0.18f)
            - cameraForward * 0.006f;
        Vector3 connectorAxis = Vector3.Normalize(cameraForward + cameraUp);
        Vector3 connectorAnchor = connectorBase
            + connectorAxis * HeroSubviewCameraConnectorLengthM
            - cameraRight * 0.006f;
        cameraCenter = connectorAnchor
            + cameraForward * (HeroSubviewCameraBodyLengthM * 0.5f - 0.004f)
            + cameraUp * (HeroSubviewCameraBodyHeightM * 0.5f - 0.002f);
    }

    private float DrawCustomAppearanceAttachments(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile,
        RuntimeChassisMotion motion,
        float yaw,
        float turretYaw,
        float gimbalPitch,
        float bodyLength,
        float bodyWidth,
        float bodyHeight,
        float bodyBase,
        Vector3 chassisForward3,
        Vector3 chassisRight3,
        Vector3 chassisUp3)
    {
        if (profile.CustomPrimitives.Count == 0 && profile.CustomAnchors.Count == 0 && profile.CustomLinks.Count == 0)
        {
            return 0f;
        }

        List<AppearancePartPose> partPoses = ResolveAttachmentPartPoses(
            entity,
            center,
            profile,
            motion,
            yaw,
            turretYaw,
            gimbalPitch,
            bodyLength,
            bodyWidth,
            bodyHeight,
            bodyBase,
            chassisForward3,
            chassisRight3,
            chassisUp3);
        float maxHeight = 0f;

        foreach (RobotAppearanceCustomPrimitive primitive in profile.CustomPrimitives)
        {
            foreach (AppearancePartPose parentPose in ResolveMatchingPartPoses(partPoses, primitive.ParentPart, primitive.ComponentScope, primitive.ComponentIndex))
            {
                Vector3 centerWorld = ResolveLocalPoint(parentPose, primitive.OffsetM);
                ResolveRotatedAxes(parentPose.Forward, parentPose.Right, parentPose.Up, primitive.RotationYprDeg, out Vector3 forward, out Vector3 right, out Vector3 up);
                float length = Math.Max(0.002f, primitive.SizeM.X);
                float height = Math.Max(0.002f, primitive.SizeM.Y);
                float width = Math.Max(0.002f, primitive.SizeM.Z);
                if (string.Equals(primitive.PrimitiveType, "cylinder", StringComparison.OrdinalIgnoreCase))
                {
                    float radius = Math.Max(0.001f, MathF.Max(height, width) * 0.5f);
                    DrawCylinderSolid(
                        graphics,
                        centerWorld,
                        forward,
                        up,
                        radius,
                        length * 0.5f,
                        0f,
                        Color.FromArgb(entity.IsAlive ? 242 : 214, primitive.Color),
                        Color.FromArgb(entity.IsAlive ? 248 : 206, BlendColor(primitive.Color, Color.White, 0.16f)),
                        12);
                }
                else
                {
                    DrawOrientedBoxSolid(
                        graphics,
                        centerWorld,
                        forward,
                        right,
                        up,
                        length,
                        width,
                        height,
                        Color.FromArgb(entity.IsAlive ? 242 : 214, primitive.Color),
                        Color.FromArgb(entity.IsAlive ? 248 : 206, BlendColor(primitive.Color, Color.White, 0.16f)),
                        null);
                }

                maxHeight = Math.Max(maxHeight, centerWorld.Y + height * 0.5f);
            }
        }

        var anchorMap = new Dictionary<string, List<AppearancePartPose>>(StringComparer.OrdinalIgnoreCase);
        foreach (RobotAppearanceAnchor anchor in profile.CustomAnchors)
        {
            if (IsActiveAttachmentAnchor(anchor))
            {
                continue;
            }

            foreach (AppearancePartPose parentPose in ResolveMatchingPartPoses(partPoses, anchor.ParentPart, anchor.ComponentScope, anchor.ComponentIndex))
            {
                Vector3 anchorCenter = ResolveLocalPoint(parentPose, anchor.OffsetM);
                ResolveRotatedAxes(parentPose.Forward, parentPose.Right, parentPose.Up, anchor.RotationYprDeg, out Vector3 forward, out Vector3 right, out Vector3 up);
                AddAnchorPose(anchorMap, anchor.Id, new AppearancePartPose(anchor.Id, parentPose.ComponentIndex, anchorCenter, forward, right, up, 0.01f, 0.01f, 0.01f));
            }
        }

        var linkPoseMap = new Dictionary<string, List<(AppearancePartPose Start, AppearancePartPose End)>>(StringComparer.OrdinalIgnoreCase);
        bool progress = true;
        for (int pass = 0; pass < 5 && progress; pass++)
        {
            progress = false;
            foreach (RobotAppearanceLink link in profile.CustomLinks)
            {
                if (linkPoseMap.ContainsKey(link.Id))
                {
                    continue;
                }

                List<(AppearancePartPose Start, AppearancePartPose End)> pairs = ResolveAnchorPosePairs(anchorMap, link.StartAnchorId, link.EndAnchorId);
                if (pairs.Count > 0)
                {
                    linkPoseMap[link.Id] = pairs;
                    progress = true;
                }
            }

            foreach (RobotAppearanceAnchor anchor in profile.CustomAnchors)
            {
                if (!IsActiveAttachmentAnchor(anchor) || anchorMap.ContainsKey(anchor.Id))
                {
                    continue;
                }

                if (!linkPoseMap.TryGetValue(anchor.ParentLinkId, out var linkPoses))
                {
                    continue;
                }

                foreach ((AppearancePartPose Start, AppearancePartPose End) linkPose in linkPoses)
                {
                    Vector3 start = linkPose.Start.Center;
                    Vector3 end = linkPose.End.Center;
                    Vector3 axis = end - start;
                    float distance = axis.Length();
                    if (distance <= 1e-5f)
                    {
                        continue;
                    }

                    Vector3 forward = axis / distance;
                    Vector3 up = linkPose.Start.Up - forward * Vector3.Dot(linkPose.Start.Up, forward);
                    if (up.LengthSquared() <= 1e-8f)
                    {
                        up = Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) >= 0.92f ? Vector3.UnitX : Vector3.UnitY;
                    }

                    up = Vector3.Normalize(up);
                    Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
                    Vector3 linkCenter = Vector3.Lerp(start, end, Math.Clamp(anchor.LinkPositionRatio, 0f, 1f));
                    ResolveRotatedAxes(forward, right, up, anchor.RotationYprDeg, out forward, out right, out up);
                    Vector3 anchorCenter = ResolveLocalPoint(new AppearancePartPose(anchor.Id, linkPose.Start.ComponentIndex, linkCenter, forward, right, up, 0.01f, 0.01f, 0.01f), anchor.OffsetM);
                    AddAnchorPose(anchorMap, anchor.Id, new AppearancePartPose(anchor.Id, linkPose.Start.ComponentIndex, anchorCenter, forward, right, up, 0.01f, 0.01f, 0.01f));
                    progress = true;
                }
            }
        }

        foreach (RobotAppearanceLink link in profile.CustomLinks)
        {
            foreach ((AppearancePartPose start, AppearancePartPose end) in ResolveAnchorPosePairs(anchorMap, link.StartAnchorId, link.EndAnchorId))
            {
                Vector3 axis = end.Center - start.Center;
                float anchorDistance = axis.Length();
                if (anchorDistance <= 1e-4f)
                {
                    continue;
                }

                float length = link.LengthM > 0.001f ? link.LengthM : anchorDistance;
                Vector3 forward = axis / anchorDistance;
                Vector3 up = start.Up - forward * Vector3.Dot(start.Up, forward);
                if (up.LengthSquared() <= 1e-8f)
                {
                    up = Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) >= 0.92f ? Vector3.UnitX : Vector3.UnitY;
                }

                up = Vector3.Normalize(up);
                Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
                Vector3 linkEnd = start.Center + forward * length;
                DrawOrientedBoxSolid(
                    graphics,
                    (start.Center + linkEnd) * 0.5f,
                    forward,
                    right,
                    up,
                    length,
                    Math.Max(0.001f, link.WidthM),
                    Math.Max(0.001f, link.ThicknessM),
                    Color.FromArgb(entity.IsAlive ? 238 : 208, link.Color),
                    Color.FromArgb(entity.IsAlive ? 246 : 198, BlendColor(link.Color, Color.White, 0.12f)),
                    null);
                maxHeight = Math.Max(maxHeight, Math.Max(start.Center.Y, linkEnd.Y) + Math.Max(link.WidthM, link.ThicknessM) * 0.5f);
            }
        }

        return maxHeight;
    }

    private static void AddAnchorPose(Dictionary<string, List<AppearancePartPose>> anchorMap, string id, AppearancePartPose pose)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (!anchorMap.TryGetValue(id, out List<AppearancePartPose>? poses))
        {
            poses = new List<AppearancePartPose>();
            anchorMap[id] = poses;
        }

        poses.Add(pose);
    }

    private static List<(AppearancePartPose Start, AppearancePartPose End)> ResolveAnchorPosePairs(
        Dictionary<string, List<AppearancePartPose>> anchorMap,
        string startAnchorId,
        string endAnchorId)
    {
        var result = new List<(AppearancePartPose Start, AppearancePartPose End)>();
        if (!anchorMap.TryGetValue(startAnchorId, out List<AppearancePartPose>? starts)
            || !anchorMap.TryGetValue(endAnchorId, out List<AppearancePartPose>? ends)
            || starts.Count == 0
            || ends.Count == 0)
        {
            return result;
        }

        int count = Math.Max(starts.Count, ends.Count);
        for (int index = 0; index < count; index++)
        {
            AppearancePartPose start = starts[Math.Min(index, starts.Count - 1)];
            AppearancePartPose end = ends[Math.Min(index, ends.Count - 1)];
            result.Add((start, end));
        }

        return result;
    }

    private static bool IsActiveAttachmentAnchor(RobotAppearanceAnchor anchor)
        => string.Equals(anchor.AnchorMode, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(anchor.AnchorMode, "link", StringComparison.OrdinalIgnoreCase);

    private List<AppearancePartPose> ResolveAttachmentPartPoses(
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile,
        RuntimeChassisMotion motion,
        float yaw,
        float turretYaw,
        float gimbalPitch,
        float bodyLength,
        float bodyWidth,
        float bodyHeight,
        float bodyBase,
        Vector3 chassisForward3,
        Vector3 chassisRight3,
        Vector3 chassisUp3)
    {
        var poses = new List<AppearancePartPose>(32);
        poses.Add(new AppearancePartPose("body", 0, center + chassisUp3 * (bodyBase + bodyHeight * 0.5f), chassisForward3, chassisRight3, chassisUp3, bodyLength, bodyWidth, bodyHeight));

        IReadOnlyList<ArmorRenderComponent> armorComponents = ResolveArmorComponents(profile);
        float plateHeight = Math.Max(0.04f, profile.ArmorPlateHeightM);
        float plateSpan = Math.Max(0.04f, profile.ArmorPlateLengthM);
        float plateThickness = ResolveArmorPlateThickness(profile);
        for (int armorIndex = 0; armorIndex < armorComponents.Count; armorIndex++)
        {
            ArmorRenderComponent component = armorComponents[armorIndex];
            Vector3 armorCenter = OffsetScenePosition(
                center,
                component.LocalCenter.X,
                component.LocalCenter.Z,
                component.LocalCenter.Y + motion.BodyLiftM,
                chassisForward3,
                chassisRight3,
                chassisUp3);
            ResolveLocalPlaneAxes(chassisForward3, chassisRight3, component.YawRad, out Vector3 plateForward3, out Vector3 plateRight3);
            ResolveRotatedAxes(
                plateForward3,
                plateRight3,
                chassisUp3,
                new Vector3(0f, component.PitchRad * 180f / MathF.PI, component.RollRad * 180f / MathF.PI),
                out plateForward3,
                out plateRight3,
                out Vector3 plateUp3);
            poses.Add(new AppearancePartPose("armor", armorIndex, armorCenter, plateForward3, plateRight3, plateUp3, plateThickness, plateSpan, plateHeight));
        }

        IReadOnlyList<ArmorLightRenderComponent> armorLightComponents = ResolveArmorLightComponents(profile);
        for (int lightIndex = 0; lightIndex < armorLightComponents.Count; lightIndex++)
        {
            ArmorLightRenderComponent component = armorLightComponents[lightIndex];
            ResolveLocalPlaneAxes(chassisForward3, chassisRight3, component.YawRad, out Vector3 lightForward3, out Vector3 lightRight3);
            ResolveRotatedAxes(
                lightForward3,
                lightRight3,
                chassisUp3,
                new Vector3(0f, component.PitchRad * 180f / MathF.PI, component.RollRad * 180f / MathF.PI),
                out lightForward3,
                out lightRight3,
                out Vector3 lightUp3);
            Vector3 centerA = OffsetScenePosition(center, component.LocalCenterA.X, component.LocalCenterA.Z, component.LocalCenterA.Y + motion.BodyLiftM, chassisForward3, chassisRight3, chassisUp3);
            Vector3 centerB = OffsetScenePosition(center, component.LocalCenterB.X, component.LocalCenterB.Z, component.LocalCenterB.Y + motion.BodyLiftM, chassisForward3, chassisRight3, chassisUp3);
            poses.Add(new AppearancePartPose("armor_light", lightIndex * 2, centerA, lightForward3, lightRight3, lightUp3, Math.Max(0.006f, profile.ArmorLightWidthM), Math.Max(0.010f, profile.ArmorLightLengthM), Math.Max(0.005f, profile.ArmorLightHeightM)));
            poses.Add(new AppearancePartPose("armor_light", lightIndex * 2 + 1, centerB, lightForward3, lightRight3, lightUp3, Math.Max(0.006f, profile.ArmorLightWidthM), Math.Max(0.010f, profile.ArmorLightLengthM), Math.Max(0.005f, profile.ArmorLightHeightM)));
        }

        float rearHealthWidth = profile.RearHealthLightLengthM > 0.001f
            ? Math.Max(0.012f, profile.RearHealthLightLengthM)
            : Math.Max(0.08f, bodyWidth * 0.74f);
        float rearHealthDepth = profile.RearHealthLightWidthM > 0.001f
            ? Math.Max(0.004f, profile.RearHealthLightWidthM)
            : Math.Clamp(bodyLength * 0.045f, 0.018f, 0.038f);
        float rearHealthHeight = profile.RearHealthLightHeightM > 0.001f
            ? Math.Max(0.004f, profile.RearHealthLightHeightM)
            : Math.Clamp(bodyWidth * 0.035f, 0.010f, 0.018f);
        Vector3 rearHealthCenter = center
            - chassisForward3 * (bodyLength * 0.5f + rearHealthDepth * 0.24f)
            + chassisForward3 * profile.RearHealthLightOffsetXM
            + chassisUp3 * (bodyBase + bodyHeight + motion.BodyLiftM + rearHealthHeight * 0.58f + profile.RearHealthLightOffsetYM)
            + chassisRight3 * profile.RearHealthLightOffsetZM;
        poses.Add(new AppearancePartPose("rear_health_light", 0, rearHealthCenter, chassisForward3, chassisRight3, chassisUp3, rearHealthDepth, rearHealthWidth, rearHealthHeight));

        Vector2 forward2 = new(MathF.Cos(yaw), MathF.Sin(yaw));
        Vector2 right2 = new(-forward2.Y, forward2.X);
        int wheelIndex = 0;
        foreach (RenderWheelComponent wheel in ResolveWheelComponents(entity, profile, motion))
        {
            float resolvedCenterHeight = ResolveTerrainAwareWheelCenterHeight(
                entity,
                center,
                forward2,
                right2,
                chassisForward3,
                chassisRight3,
                chassisRight3,
                chassisUp3,
                wheel.LocalX,
                wheel.LocalY,
                wheel.RadiusM,
                wheel.CenterHeightM,
                wheel.FixedToLeg);
            Vector3 wheelCenter = center
                + chassisForward3 * wheel.LocalX
                + chassisRight3 * wheel.LocalY
                + chassisUp3 * resolvedCenterHeight;
            poses.Add(new AppearancePartPose("wheel", wheelIndex++, wheelCenter, chassisForward3, chassisRight3, chassisUp3, wheel.RadiusM * 2f, wheel.HalfThicknessM * 2f, wheel.RadiusM * 2f));
        }

        if (!string.Equals(profile.FrontClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            float frontForward = bodyLength * 0.5f + profile.FrontClimbAssistForwardOffsetM + profile.FrontClimbAssistBottomLengthM * 0.5f;
            float frontSide = Math.Max(bodyWidth * 0.45f, bodyWidth * 0.5f - profile.FrontClimbAssistInnerOffsetM);
            float plateCenterHeight = profile.WheelRadiusM + profile.FrontClimbAssistPlateHeightM * 0.5f + motion.BodyLiftM;
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                float sideSign = sideIndex == 0 ? -1f : 1f;
                Vector3 plateCenter = OffsetScenePosition(center, frontForward, frontSide * sideSign, plateCenterHeight, chassisForward3, chassisRight3, chassisUp3);
                poses.Add(new AppearancePartPose("front_climb", sideIndex, plateCenter, chassisForward3, chassisRight3, chassisUp3, profile.FrontClimbAssistBottomLengthM, profile.FrontClimbAssistPlateWidthM, profile.FrontClimbAssistPlateHeightM));
            }
        }

        if (!string.Equals(profile.RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(profile.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase))
            {
                for (int sideIndex = 0; sideIndex < 2; sideIndex++)
                {
                    float sideSign = sideIndex == 0 ? -1f : 1f;
                    BalanceLegGeometry leg = ResolveBalanceLegGeometry(entity, profile, motion, sideSign);
                    float sideOffset = leg.SideOffset * sideSign;
                    float mountSideOffset = Math.Max(0.02f, profile.BodyWidthM * profile.BodyRenderWidthScale * 0.5f * 0.98f) * sideSign;
                    Vector3 mountFront = OffsetScenePosition(center, leg.UpperFront.X, mountSideOffset, leg.UpperFront.Y, chassisForward3, chassisRight3, chassisUp3);
                    Vector3 mountRear = OffsetScenePosition(center, leg.UpperRear.X, mountSideOffset, leg.UpperRear.Y, chassisForward3, chassisRight3, chassisUp3);
                    Vector3 upperFront = OffsetScenePosition(center, leg.UpperFront.X, sideOffset, leg.UpperFront.Y, chassisForward3, chassisRight3, chassisUp3);
                    Vector3 upperRear = OffsetScenePosition(center, leg.UpperRear.X, sideOffset, leg.UpperRear.Y, chassisForward3, chassisRight3, chassisUp3);
                    Vector3 kneeFront = OffsetScenePosition(center, leg.KneeFront.X, sideOffset, leg.KneeFront.Y, chassisForward3, chassisRight3, chassisUp3);
                    Vector3 kneeRear = OffsetScenePosition(center, leg.KneeRear.X, sideOffset, leg.KneeRear.Y, chassisForward3, chassisRight3, chassisUp3);
                    Vector3 kneeCenter = OffsetScenePosition(center, leg.KneeCenter.X, sideOffset, leg.KneeCenter.Y, chassisForward3, chassisRight3, chassisUp3);
                    Vector3 foot = OffsetScenePosition(center, leg.Foot.X, sideOffset, leg.Foot.Y, chassisForward3, chassisRight3, chassisUp3);
                    Vector3 avgCenter = (upperFront + upperRear + foot) / 3f;
                    poses.Add(new AppearancePartPose("rear_climb", sideIndex, avgCenter, chassisForward3, chassisRight3, chassisUp3, profile.RearClimbAssistUpperLengthM, profile.RearClimbAssistUpperWidthM, profile.RearClimbAssistMountHeightM));
                    AddBalanceLegAttachmentPartPoses(
                        poses,
                        sideIndex,
                        mountFront,
                        mountRear,
                        upperFront,
                        upperRear,
                        kneeFront,
                        kneeRear,
                        kneeCenter,
                        foot,
                        chassisForward3,
                        chassisRight3,
                        chassisUp3,
                        profile);
                }
            }
            else
            {
                float upperBase = Math.Max(bodyBase + bodyHeight * 0.55f, profile.RearClimbAssistMountHeightM + motion.BodyLiftM);
                float rearForward = -bodyLength * 0.5f + profile.RearClimbAssistMountOffsetXM;
                float rearSide = Math.Max(bodyWidth * 0.30f, bodyWidth * 0.5f - profile.RearClimbAssistInnerOffsetM);
                for (int sideIndex = 0; sideIndex < 2; sideIndex++)
                {
                    float sideSign = sideIndex == 0 ? -1f : 1f;
                    Vector3 upperCenter = OffsetScenePosition(center, rearForward, rearSide * sideSign, upperBase, chassisForward3, chassisRight3, chassisUp3);
                    poses.Add(new AppearancePartPose("rear_climb", sideIndex, upperCenter, chassisForward3, chassisRight3, chassisUp3, profile.RearClimbAssistUpperLengthM, profile.RearClimbAssistUpperWidthM, profile.RearClimbAssistUpperHeightM));
                }
            }
        }

        if (profile.GimbalLengthM > 0.04f && profile.GimbalWidthM > 0.04f && profile.GimbalBodyHeightM > 0.02f)
        {
            float bodyTop = bodyBase + bodyHeight;
            ResolveWorldTurretAxes(turretYaw, gimbalPitch, out Vector3 turretForward, out Vector3 turretRight, out Vector3 pitchedForward, out Vector3 pitchedUp);
            if (profile.GimbalMountGapM + profile.GimbalMountHeightM > 0.02f)
            {
                float mountHeight = Math.Max(0.02f, profile.GimbalMountGapM + profile.GimbalMountHeightM + 0.08f);
                float mountCenterHeight = bodyTop + mountHeight * 0.5f;
                Vector3 mountCenter = OffsetScenePosition(center, profile.GimbalOffsetXM, profile.GimbalOffsetYM, mountCenterHeight, chassisForward3, chassisRight3, chassisUp3);
                poses.Add(new AppearancePartPose("mount", 0, mountCenter, turretForward, turretRight, chassisUp3, Math.Max(0.02f, profile.GimbalMountLengthM), Math.Max(0.02f, profile.GimbalMountWidthM * profile.BodyRenderWidthScale), mountHeight));
            }

            float turretBase = Math.Max(bodyTop + profile.GimbalMountGapM + profile.GimbalMountHeightM, profile.GimbalHeightM - profile.GimbalBodyHeightM * 0.5f);
            float hingeBase = Math.Max(bodyTop + profile.GimbalMountGapM + profile.GimbalMountHeightM, turretBase);
            Vector3 hingeCenter = OffsetScenePosition(
                center,
                profile.GimbalOffsetXM + profile.GimbalRelativeOffsetXM,
                profile.GimbalOffsetYM + profile.GimbalRelativeOffsetZM,
                hingeBase + profile.GimbalRelativeOffsetYM,
                chassisForward3,
                chassisRight3,
                chassisUp3);
            Vector3 turretCenter = hingeCenter
                + pitchedUp * (profile.GimbalBodyHeightM * 0.50f);
            poses.Add(new AppearancePartPose("turret", 0, turretCenter, pitchedForward, turretRight, pitchedUp, profile.GimbalLengthM, profile.GimbalWidthM * profile.BodyRenderWidthScale, profile.GimbalBodyHeightM));

            if (profile.BarrelLengthM > 0.04f && profile.BarrelRadiusM > 0.004f)
            {
                Vector3 barrelAxis = pitchedForward;
                Vector3 pivot = turretCenter
                    + pitchedForward * (profile.GimbalLengthM * 0.5f + profile.BarrelOffsetXM)
                    + pitchedUp * profile.BarrelOffsetYM
                    + turretRight * profile.BarrelOffsetZM;
                Vector3 barrelCenter = pivot + barrelAxis * (profile.BarrelLengthM * 0.5f);
                poses.Add(new AppearancePartPose("barrel", 0, barrelCenter, barrelAxis, turretRight, pitchedUp, profile.BarrelLengthM, profile.BarrelRadiusM * 2f, profile.BarrelRadiusM * 2f));

                float frictionHeight = profile.BarrelFrictionWheelHeightM > 0f ? profile.BarrelFrictionWheelHeightM : profile.BarrelFrictionWheelWidthM;
                if (profile.BarrelFrictionWheelRadiusM > 0.002f && frictionHeight > 0.002f)
                {
                    foreach (FrictionWheelPose wheel in ResolveBarrelFrictionWheelPoses(pivot, barrelAxis, turretRight, pitchedUp, profile))
                    {
                        Vector3 wheelUp = Vector3.Cross(wheel.Axis, wheel.RadialHint);
                        wheelUp = wheelUp.LengthSquared() <= 1e-8f ? pitchedUp : Vector3.Normalize(wheelUp);
                        poses.Add(new AppearancePartPose("barrel_friction_wheel", wheel.Index, wheel.Center, wheel.Axis, wheel.RadialHint, wheelUp, frictionHeight, profile.BarrelFrictionWheelRadiusM * 2f, profile.BarrelFrictionWheelRadiusM * 2f));
                    }
                }

                float firstPersonForwardOffset = MathF.Abs(profile.FirstPersonCameraOffsetXM) > 1e-6f
                    ? profile.FirstPersonCameraOffsetXM
                    : 0.04f;
                float firstPersonHeightOffset = MathF.Abs(profile.FirstPersonCameraOffsetYM) > 1e-6f
                    ? profile.FirstPersonCameraOffsetYM
                    : 0.06f;
                Vector3 firstPersonCameraCenter = pivot
                    + barrelAxis * firstPersonForwardOffset
                    + pitchedUp * firstPersonHeightOffset
                    + turretRight * profile.FirstPersonCameraOffsetZM;
                ResolveRotatedAxes(barrelAxis, turretRight, pitchedUp, Vector3.Zero, out Vector3 cameraForward, out Vector3 cameraRight, out Vector3 cameraUp);
                poses.Add(new AppearancePartPose("first_person_camera", 0, firstPersonCameraCenter, cameraForward, cameraRight, cameraUp, 0.024f, 0.024f, 0.024f));

                float sideOffset = Math.Max(0.01f, profile.BarrelLightWidthM * 1.5f);
                Vector3 lightBase = pivot
                    + barrelAxis * (profile.BarrelLengthM * 0.56f + profile.BarrelLightOffsetXM)
                    + pitchedUp * (profile.BarrelLightOffsetYM - profile.BarrelLightHeightM * 0.10f)
                    + turretRight * profile.BarrelLightOffsetZM;
                Vector3 lightA = lightBase + turretRight * sideOffset;
                Vector3 lightB = lightBase - turretRight * sideOffset;
                poses.Add(new AppearancePartPose("barrel_light", 0, lightA, barrelAxis, turretRight, pitchedUp, Math.Max(0.006f, profile.BarrelLightLengthM), Math.Max(0.004f, profile.BarrelLightWidthM), Math.Max(0.004f, profile.BarrelLightHeightM)));
                poses.Add(new AppearancePartPose("barrel_light", 1, lightB, barrelAxis, turretRight, pitchedUp, Math.Max(0.006f, profile.BarrelLightLengthM), Math.Max(0.004f, profile.BarrelLightWidthM), Math.Max(0.004f, profile.BarrelLightHeightM)));
            }

            if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
            {
                ResolveHeroSubviewCameraMount(
                    turretCenter,
                    pitchedForward,
                    turretRight,
                    pitchedUp,
                    profile.GimbalWidthM * profile.BodyRenderWidthScale,
                    profile.GimbalBodyHeightM,
                    out Vector3 cameraCenter,
                    out Vector3 cameraForward,
                    out Vector3 cameraRight,
                    out Vector3 cameraUp,
                    out _);
                poses.Add(new AppearancePartPose("hero_subview_camera", 0, cameraCenter, cameraForward, cameraRight, cameraUp, HeroSubviewCameraBodyLengthM, HeroSubviewCameraBodyWidthM, HeroSubviewCameraBodyHeightM));
            }
        }

        return poses;
    }

    private static void AddBalanceLegAttachmentPartPoses(
        List<AppearancePartPose> poses,
        int sideIndex,
        Vector3 mountFront,
        Vector3 mountRear,
        Vector3 upperFront,
        Vector3 upperRear,
        Vector3 kneeFront,
        Vector3 kneeRear,
        Vector3 kneeCenter,
        Vector3 foot,
        Vector3 chassisForward3,
        Vector3 chassisRight3,
        Vector3 chassisUp3,
        RobotAppearanceProfile profile)
    {
        float hingeSize = Math.Max(0.008f, profile.RearClimbAssistHingeRadiusM * 2f);
        Vector3 aggregateCenter = (upperFront + upperRear + kneeCenter + foot) * 0.25f;
        AddAttachmentPartPoseWithAliases(
            poses,
            "balance_leg",
            sideIndex,
            aggregateCenter,
            chassisForward3,
            chassisRight3,
            chassisUp3,
            Math.Max(profile.RearClimbAssistUpperLengthM, profile.RearClimbAssistLowerLengthM),
            Math.Max(profile.RearClimbAssistUpperWidthM, profile.RearClimbAssistLowerWidthM),
            Math.Max(profile.RearClimbAssistUpperHeightM, profile.RearClimbAssistLowerHeightM));

        AddBalanceLegBeamAttachmentPose(poses, "balance_leg_mount_front", sideIndex, mountFront, upperFront, chassisRight3, chassisUp3, profile.RearClimbAssistUpperWidthM, profile.RearClimbAssistUpperHeightM);
        AddBalanceLegBeamAttachmentPose(poses, "balance_leg_mount_rear", sideIndex, mountRear, upperRear, chassisRight3, chassisUp3, profile.RearClimbAssistUpperWidthM, profile.RearClimbAssistUpperHeightM);
        AddBalanceLegBeamAttachmentPose(poses, "balance_leg_mount_cross", sideIndex, mountFront, mountRear, chassisRight3, chassisUp3, profile.RearClimbAssistUpperWidthM, profile.RearClimbAssistUpperHeightM);
        AddBalanceLegBeamAttachmentPose(poses, "balance_leg_upper_front", sideIndex, upperFront, kneeFront, chassisRight3, chassisUp3, profile.RearClimbAssistUpperWidthM, profile.RearClimbAssistUpperHeightM);
        AddBalanceLegBeamAttachmentPose(poses, "balance_leg_upper_rear", sideIndex, upperRear, kneeRear, chassisRight3, chassisUp3, profile.RearClimbAssistUpperWidthM, profile.RearClimbAssistUpperHeightM);
        AddBalanceLegBeamAttachmentPose(poses, "balance_leg_lower", sideIndex, kneeCenter, foot, chassisRight3, chassisUp3, profile.RearClimbAssistLowerWidthM, profile.RearClimbAssistLowerHeightM);

        AddBalanceLegPointAttachmentPose(poses, "balance_leg_upper_front_hinge", sideIndex, upperFront, chassisForward3, chassisRight3, chassisUp3, hingeSize);
        AddBalanceLegPointAttachmentPose(poses, "balance_leg_upper_rear_hinge", sideIndex, upperRear, chassisForward3, chassisRight3, chassisUp3, hingeSize);
        AddBalanceLegPointAttachmentPose(poses, "balance_leg_knee_front_hinge", sideIndex, kneeFront, chassisForward3, chassisRight3, chassisUp3, hingeSize);
        AddBalanceLegPointAttachmentPose(poses, "balance_leg_knee_rear_hinge", sideIndex, kneeRear, chassisForward3, chassisRight3, chassisUp3, hingeSize);
        AddBalanceLegPointAttachmentPose(poses, "balance_leg_knee", sideIndex, kneeCenter, chassisForward3, chassisRight3, chassisUp3, hingeSize);
        AddBalanceLegPointAttachmentPose(poses, "balance_leg_foot", sideIndex, foot, chassisForward3, chassisRight3, chassisUp3, hingeSize);
    }

    private static void AddBalanceLegBeamAttachmentPose(
        List<AppearancePartPose> poses,
        string partKey,
        int sideIndex,
        Vector3 start,
        Vector3 end,
        Vector3 lateralAxis,
        Vector3 fallbackUp,
        float widthM,
        float heightM)
    {
        Vector3 axis = end - start;
        float length = axis.Length();
        if (length <= 1e-5f)
        {
            return;
        }

        Vector3 forward = axis / length;
        Vector3 right = NormalizeOrFallback(lateralAxis, Vector3.UnitZ);
        Vector3 up = Vector3.Cross(right, forward);
        if (up.LengthSquared() <= 1e-8f)
        {
            up = fallbackUp - forward * Vector3.Dot(fallbackUp, forward);
        }

        up = NormalizeOrFallback(up, fallbackUp);
        right = NormalizeOrFallback(Vector3.Cross(forward, up), right);
        AddAttachmentPartPoseWithAliases(
            poses,
            partKey,
            sideIndex,
            (start + end) * 0.5f,
            forward,
            right,
            up,
            length,
            Math.Max(0.006f, widthM),
            Math.Max(0.006f, heightM));
    }

    private static void AddBalanceLegPointAttachmentPose(
        List<AppearancePartPose> poses,
        string partKey,
        int sideIndex,
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float sizeM)
    {
        AddAttachmentPartPoseWithAliases(
            poses,
            partKey,
            sideIndex,
            center,
            forward,
            right,
            up,
            sizeM,
            sizeM,
            sizeM);
    }

    private static void AddAttachmentPartPoseWithAliases(
        List<AppearancePartPose> poses,
        string partKey,
        int sideIndex,
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float lengthM,
        float widthM,
        float heightM)
    {
        AddAttachmentPartPose(poses, partKey, sideIndex, center, forward, right, up, lengthM, widthM, heightM);
        string sideName = sideIndex == 0 ? "left" : "right";
        AddAttachmentPartPose(poses, $"{partKey}_{sideName}", 0, center, forward, right, up, lengthM, widthM, heightM);
        const string balanceLegPrefix = "balance_leg";
        if (partKey.StartsWith(balanceLegPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string rearLegKey = "rear_leg" + partKey.Substring(balanceLegPrefix.Length);
            AddAttachmentPartPose(poses, rearLegKey, sideIndex, center, forward, right, up, lengthM, widthM, heightM);
            AddAttachmentPartPose(poses, $"{rearLegKey}_{sideName}", 0, center, forward, right, up, lengthM, widthM, heightM);
        }
    }

    private static void AddAttachmentPartPose(
        List<AppearancePartPose> poses,
        string partKey,
        int componentIndex,
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float lengthM,
        float widthM,
        float heightM)
    {
        poses.Add(new AppearancePartPose(
            partKey,
            componentIndex,
            center,
            NormalizeOrFallback(forward, Vector3.UnitX),
            NormalizeOrFallback(right, Vector3.UnitZ),
            NormalizeOrFallback(up, Vector3.UnitY),
            Math.Max(0.002f, lengthM),
            Math.Max(0.002f, widthM),
            Math.Max(0.002f, heightM)));
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-8f)
        {
            return Vector3.Normalize(value);
        }

        return fallback.LengthSquared() > 1e-8f ? Vector3.Normalize(fallback) : Vector3.UnitY;
    }

    private static IEnumerable<AppearancePartPose> ResolveMatchingPartPoses(
        IReadOnlyList<AppearancePartPose> poses,
        string parentPart,
        string componentScope,
        int componentIndex)
    {
        bool all = string.Equals(componentScope, "all", StringComparison.OrdinalIgnoreCase);
        foreach (AppearancePartPose pose in poses)
        {
            if (!string.Equals(pose.PartKey, parentPart, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (all || pose.ComponentIndex == componentIndex)
            {
                yield return pose;
            }
        }
    }

    private static AppearancePartPose? ResolveFirstMatchingPartPose(
        IReadOnlyList<AppearancePartPose> poses,
        string parentPart,
        string componentScope,
        int componentIndex)
    {
        foreach (AppearancePartPose pose in ResolveMatchingPartPoses(poses, parentPart, componentScope, componentIndex))
        {
            return pose;
        }

        return null;
    }

    private static Vector3 ResolveLocalPoint(AppearancePartPose parentPose, Vector3 localOffset)
        => parentPose.Center
            + parentPose.Forward * localOffset.X
            + parentPose.Up * localOffset.Y
            + parentPose.Right * localOffset.Z;

    private static void ResolveRotatedAxes(
        Vector3 baseForward,
        Vector3 baseRight,
        Vector3 baseUp,
        Vector3 rotationYprDeg,
        out Vector3 forward,
        out Vector3 right,
        out Vector3 up)
    {
        forward = baseForward;
        right = baseRight;
        up = baseUp;

        RotateBasisAroundAxis(ref forward, ref right, ref up, up, rotationYprDeg.X * MathF.PI / 180f);
        RotateBasisAroundAxis(ref forward, ref right, ref up, right, rotationYprDeg.Y * MathF.PI / 180f);
        RotateBasisAroundAxis(ref forward, ref right, ref up, forward, rotationYprDeg.Z * MathF.PI / 180f);
    }

    private static void RotateBasisAroundAxis(ref Vector3 forward, ref Vector3 right, ref Vector3 up, Vector3 axis, float angleRad)
    {
        if (MathF.Abs(angleRad) <= 1e-6f || axis.LengthSquared() <= 1e-8f)
        {
            return;
        }

        Vector3 normalizedAxis = Vector3.Normalize(axis);
        forward = Vector3.Normalize(Vector3.Transform(forward, Quaternion.CreateFromAxisAngle(normalizedAxis, angleRad)));
        right = Vector3.Normalize(Vector3.Transform(right, Quaternion.CreateFromAxisAngle(normalizedAxis, angleRad)));
        up = Vector3.Normalize(Vector3.Transform(up, Quaternion.CreateFromAxisAngle(normalizedAxis, angleRad)));
    }

    private float DrawEntityAppearanceModelProxy(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        RobotAppearanceProfile profile,
        float distanceM)
    {
        CanonicalizeEntityTeamForVisuals(entity);
        float yaw = ResolveAppearanceYaw(entity);
        float turretYaw = ResolveAppearanceTurretYaw(entity);
        float gimbalPitch = -(float)(entity.GimbalPitchDeg * Math.PI / 180.0);
        Color bodyColor = ResolveAppearanceMaterialColor(profile.BodyColor, entity.IsAlive, 0.00f);
        Color turretColor = ResolveAppearanceMaterialColor(profile.TurretColor, entity.IsAlive, 0.05f);
        Color armorColor = ResolveAppearanceMaterialColor(profile.ArmorColor, entity.IsAlive, 0.02f);

        float bodyLength = Math.Max(0.12f, profile.BodyLengthM);
        float bodyWidth = Math.Max(0.10f, profile.BodyWidthM * profile.BodyRenderWidthScale);
        float bodyHeight = Math.Max(0.08f, profile.BodyHeightM);
        RuntimeChassisMotion motion = ResolveRuntimeChassisMotion(entity);
        float bodyBase = Math.Max(0f, profile.BodyClearanceM + (float)Math.Max(0.0, entity.AirborneHeightM) + motion.BodyLiftM);
        bool ultraSimple = distanceM >= 18f;

        IReadOnlyList<Vector3> bodyFootprint = BuildOrientedRectFootprint(center, bodyLength, bodyWidth, bodyBase, yaw);
        DrawPrismWireframe(
            graphics,
            bodyFootprint,
            bodyHeight,
            Color.FromArgb(entity.IsAlive ? 244 : 228, bodyColor),
            Color.FromArgb(entity.IsAlive ? 250 : 212, BlendColor(bodyColor, Color.Black, 0.10f)),
            null);

        float maxHeight = bodyBase + bodyHeight;
        ResolveChassisAxes(yaw, entity.ChassisPitchDeg, entity.ChassisRollDeg, out Vector3 chassisForward3, out Vector3 chassisRight3, out Vector3 chassisUp3);
        ResolveWorldTurretAxes(turretYaw, gimbalPitch, out _, out Vector3 turretRight, out Vector3 pitchedForward, out Vector3 pitchedUp);
        float turretBase = Math.Max(bodyBase + bodyHeight + profile.GimbalMountGapM, profile.GimbalHeightM - profile.GimbalBodyHeightM * 0.5f);
        Vector3 turretCenter = OffsetScenePosition(
            center,
            profile.GimbalOffsetXM + profile.GimbalRelativeOffsetXM,
            profile.GimbalOffsetYM + profile.GimbalRelativeOffsetZM,
            turretBase + profile.GimbalBodyHeightM * 0.50f + profile.GimbalRelativeOffsetYM,
            chassisForward3,
            chassisRight3,
            chassisUp3);
        DrawOrientedBoxSolid(
            graphics,
            turretCenter,
            pitchedForward,
            turretRight,
            pitchedUp,
            Math.Max(0.12f, profile.GimbalLengthM * (ultraSimple ? 0.75f : 0.92f)),
            Math.Max(0.08f, profile.GimbalWidthM * profile.BodyRenderWidthScale),
            Math.Max(0.05f, profile.GimbalBodyHeightM),
            Color.FromArgb(entity.IsAlive ? 244 : 226, turretColor),
            Color.FromArgb(entity.IsAlive ? 250 : 212, BlendColor(turretColor, Color.Black, 0.12f)),
            null);
        maxHeight = Math.Max(maxHeight, turretCenter.Y + profile.GimbalBodyHeightM * 0.6f);

        if (profile.BarrelLengthM > 0.03f)
        {
            Vector3 barrelCenter = turretCenter
                + pitchedForward * (profile.GimbalLengthM * 0.5f + profile.BarrelOffsetXM + Math.Max(0.16f, profile.BarrelLengthM * (ultraSimple ? 0.42f : 0.50f)))
                + pitchedUp * profile.BarrelOffsetYM
                + turretRight * profile.BarrelOffsetZM;
            DrawCylinderSolid(
                graphics,
                barrelCenter,
                pitchedForward,
                turretRight,
                Math.Max(0.006f, profile.BarrelRadiusM * (ultraSimple ? 0.9f : 1.0f)),
                Math.Max(0.07f, profile.BarrelLengthM * (ultraSimple ? 0.26f : 0.34f)),
                0f,
                Color.FromArgb(entity.IsAlive ? 244 : 226, turretColor),
                Color.FromArgb(entity.IsAlive ? 250 : 212, BlendColor(turretColor, Color.Black, 0.14f)),
                ultraSimple ? 8 : 10);
            maxHeight = Math.Max(maxHeight, barrelCenter.Y + profile.BarrelRadiusM * 1.6f);
        }

        if (!ultraSimple)
        {
            Vector2 forward2 = new(MathF.Cos(yaw), MathF.Sin(yaw));
            Vector2 right2 = new(-forward2.Y, forward2.X);
            Vector3 wheelAxis = new(-MathF.Sin(yaw), 0f, MathF.Cos(yaw));
            foreach (RenderWheelComponent wheel in ResolveWheelComponents(entity, profile, motion))
            {
                float resolvedCenterHeight = ResolveTerrainAwareWheelCenterHeight(
                    entity,
                    center,
                    forward2,
                    right2,
                    chassisForward3,
                    chassisRight3,
                    wheelAxis,
                    chassisUp3,
                    wheel.LocalX,
                    wheel.LocalY,
                    wheel.RadiusM,
                    wheel.FixedToLeg ? wheel.CenterHeightM : Math.Max(wheel.RadiusM, wheel.CenterHeightM),
                    wheel.FixedToLeg);
                Vector3 wheelCenter = center
                    + chassisForward3 * wheel.LocalX
                    + chassisRight3 * wheel.LocalY
                    + chassisUp3 * resolvedCenterHeight;

                DrawCylinderSolid(
                    graphics,
                    wheelCenter,
                    wheelAxis,
                    chassisUp3,
                    Math.Max(0.016f, wheel.RadiusM),
                    Math.Max(0.010f, wheel.HalfThicknessM),
                    wheel.SpinRad,
                    Color.FromArgb(entity.IsAlive ? 240 : 220, ResolveAppearanceMaterialColor(profile.WheelColor, entity.IsAlive, -0.04f)),
                    Color.FromArgb(entity.IsAlive ? 246 : 210, BlendColor(profile.WheelColor, Color.Black, 0.10f)),
                    10,
                    matteMaterial: true);
            }

            string? lockedPlateId = ResolveLockedPlateIdFor(entity);
            IReadOnlyList<ArmorRenderComponent> armorComponents = ResolveArmorComponents(profile);
            if (armorComponents.Count > 0)
            {
                ArmorRenderComponent frontPlate = armorComponents[0];
                float plateHeight = Math.Max(0.04f, profile.ArmorPlateHeightM);
                float plateSpan = Math.Max(0.04f, profile.ArmorPlateLengthM);
                float plateThickness = ResolveArmorPlateThickness(profile);
                Vector3 armorCenter = OffsetScenePosition(
                    center,
                    frontPlate.LocalCenter.X,
                    frontPlate.LocalCenter.Z,
                    frontPlate.LocalCenter.Y,
                    chassisForward3,
                    chassisRight3,
                    chassisUp3);
                ResolveLocalPlaneAxes(chassisForward3, chassisRight3, frontPlate.YawRad, out Vector3 plateForward3, out Vector3 plateRight3);
                bool locked = string.Equals(lockedPlateId, "armor_1", StringComparison.OrdinalIgnoreCase);
                DrawOrientedBoxSolid(
                    graphics,
                    armorCenter,
                    plateForward3,
                    plateRight3,
                    chassisUp3,
                    plateThickness * 1.08f,
                    Math.Max(0.09f, plateSpan * 1.08f),
                    Math.Max(0.06f, plateHeight * 1.08f),
                    Color.FromArgb(244, 42, 46, 54),
                    Color.FromArgb(250, 18, 20, 24),
                    null);
                DrawOrientedBoxSolid(
                    graphics,
                    armorCenter,
                    plateForward3,
                    plateRight3,
                    chassisUp3,
                    plateThickness,
                    Math.Max(0.08f, plateSpan),
                    Math.Max(0.05f, plateHeight),
                    locked ? Color.FromArgb(250, 255, 211, 84) : Color.FromArgb(236, armorColor),
                    locked ? Color.FromArgb(252, 255, 244, 150) : Color.FromArgb(246, armorColor),
                    null);
            }
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

    private static bool HasBodyFaceTilt(RobotAppearanceProfile profile)
    {
        return MathF.Abs(profile.BodyFrontTiltDeg) > 0.01f
            || MathF.Abs(profile.BodyRearTiltDeg) > 0.01f
            || MathF.Abs(profile.BodyLeftTiltDeg) > 0.01f
            || MathF.Abs(profile.BodyRightTiltDeg) > 0.01f;
    }

    private void DrawTiltedBodySolid(
        Graphics graphics,
        Vector3 center,
        Vector3 forwardAxis,
        Vector3 rightAxis,
        Vector3 upAxis,
        float length,
        float width,
        float height,
        float baseHeight,
        RobotAppearanceProfile profile,
        Color fillColor,
        Color edgeColor,
        bool matteMaterial = true)
    {
        Vector3 forward = forwardAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(forwardAxis);
        Vector3 right = rightAxis.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(rightAxis);
        Vector3 up = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        float halfLength = Math.Max(0.001f, length * 0.5f);
        float halfWidth = Math.Max(0.001f, width * 0.5f);
        float bodyHeight = Math.Max(0.001f, height);

        static float Inset(float tiltDeg, float bodyHeight)
        {
            float clamped = Math.Clamp(tiltDeg, 0f, 65f);
            return MathF.Tan(clamped * MathF.PI / 180f) * bodyHeight;
        }

        float rearX = -halfLength + Inset(profile.BodyRearTiltDeg, bodyHeight);
        float frontX = halfLength - Inset(profile.BodyFrontTiltDeg, bodyHeight);
        float leftZ = -halfWidth + Inset(profile.BodyLeftTiltDeg, bodyHeight);
        float rightZ = halfWidth - Inset(profile.BodyRightTiltDeg, bodyHeight);
        float minLength = Math.Min(halfLength * 1.6f, Math.Max(0.02f, halfLength * 0.20f));
        float minWidth = Math.Min(halfWidth * 1.6f, Math.Max(0.02f, halfWidth * 0.20f));
        if (frontX - rearX < minLength)
        {
            float mid = (frontX + rearX) * 0.5f;
            rearX = mid - minLength * 0.5f;
            frontX = mid + minLength * 0.5f;
        }

        if (rightZ - leftZ < minWidth)
        {
            float mid = (rightZ + leftZ) * 0.5f;
            leftZ = mid - minWidth * 0.5f;
            rightZ = mid + minWidth * 0.5f;
        }

        Vector3 LocalPoint(float localX, float localZ, float localY)
            => center + forward * localX + right * localZ + up * localY;

        Vector3[] bottom =
        {
            LocalPoint(-halfLength, -halfWidth, baseHeight),
            LocalPoint(halfLength, -halfWidth, baseHeight),
            LocalPoint(halfLength, halfWidth, baseHeight),
            LocalPoint(-halfLength, halfWidth, baseHeight),
        };
        Vector3[] top =
        {
            LocalPoint(rearX, leftZ, baseHeight + bodyHeight),
            LocalPoint(frontX, leftZ, baseHeight + bodyHeight),
            LocalPoint(frontX, rightZ, baseHeight + bodyHeight),
            LocalPoint(rearX, rightZ, baseHeight + bodyHeight),
        };

        DrawGeneralPrism(graphics, bottom, top, fillColor, edgeColor, null);
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

        bool hasBalanceLeg = string.Equals(profile.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase);
        bool allWheelIndicesDynamic = false;
        int leftRearIndex = -1;
        int rightRearIndex = -1;
        if (hasBalanceLeg)
        {
            if (string.Equals(profile.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase)
                && profile.WheelOffsetsM.Count <= 2)
            {
                allWheelIndicesDynamic = true;
            }
            else
            {
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

            }
        }

        float spinBase = (float)(_host.World.GameTimeSec * entity.ChassisRpm * Math.PI / 30.0);
        RenderWheelComponent[] result = new RenderWheelComponent[profile.WheelOffsetsM.Count];
        for (int index = 0; index < profile.WheelOffsetsM.Count; index++)
        {
            Vector2 rawOffset = profile.WheelOffsetsM[index];
            float localX = rawOffset.X;
            float localY = rawOffset.Y * profile.BodyRenderWidthScale;
            float wheelRadius = Math.Clamp(profile.WheelRadiusM, 0.03f, 0.24f);
            float centerHeight = wheelRadius + motion.BodyLiftM;
            bool fixedToLeg = false;
            if (hasBalanceLeg && (allWheelIndicesDynamic || index == leftRearIndex || index == rightRearIndex))
            {
                wheelRadius = ResolveRearLegWheelRadiusM(profile);
                float sideSign = rawOffset.Y < 0f ? -1f : 1f;
                if (Math.Abs(rawOffset.Y) <= 1e-4f)
                {
                    sideSign = index % 2 == 0 ? -1f : 1f;
                }

                BalanceLegGeometry sideLeg = ResolveBalanceLegGeometry(entity, profile, motion, sideSign);
                localX = sideLeg.Foot.X;
                localY = ResolveBalanceLegWheelSideOffset(profile, sideLeg, wheelRadius) * sideSign;
                centerHeight = sideLeg.Foot.Y;
                fixedToLeg = true;
            }

            float halfThickness = RenderWheelHalfWidthM;
            result[index] = new RenderWheelComponent(
                localX,
                localY,
                centerHeight,
                wheelRadius,
                halfThickness,
                spinBase + index * 0.85f,
                fixedToLeg);
        }

        return result;
    }

    private static float ResolveBalanceLegWheelSideOffset(RobotAppearanceProfile profile, BalanceLegGeometry leg, float wheelRadius)
    {
        float legHalfWidth = Math.Max(profile.RearClimbAssistUpperWidthM, profile.RearClimbAssistLowerWidthM) * 0.5f;
        float clearance = Math.Max(0.006f, wheelRadius * 0.04f);
        return leg.SideOffset + legHalfWidth + RenderWheelHalfWidthM + clearance;
    }

    private static float ResolveRearLegWheelRadiusM(RobotAppearanceProfile profile)
        => Math.Clamp(profile.RearLegWheelRadiusM, 0.03f, 0.32f);

    private static void ResolveChassisAxes(
        float yaw,
        double pitchDeg,
        double rollDeg,
        out Vector3 forward,
        out Vector3 right,
        out Vector3 up)
    {
        Vector3 flatForward = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 flatRight = new(-flatForward.Z, 0f, flatForward.X);
        float pitch = (float)(Math.Clamp(pitchDeg, -32.0, 32.0) * Math.PI / 180.0);
        float roll = (float)(Math.Clamp(rollDeg, -28.0, 28.0) * Math.PI / 180.0);
        forward = Vector3.Normalize(flatForward * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch));
        right = Vector3.Normalize(flatRight * MathF.Cos(roll) + Vector3.UnitY * MathF.Sin(roll));
        up = Vector3.Normalize(Vector3.Cross(right, forward));
        right = Vector3.Normalize(Vector3.Cross(forward, up));
    }

    private static void ResolveMountedTurretAxes(
        Vector3 chassisForward,
        Vector3 chassisRight,
        Vector3 chassisUp,
        float localTurretYaw,
        float gimbalPitch,
        out Vector3 turretForward,
        out Vector3 turretRight,
        out Vector3 pitchedForward,
        out Vector3 pitchedUp)
    {
        Vector3 baseForward = new(chassisForward.X, 0f, chassisForward.Z);
        if (baseForward.LengthSquared() <= 1e-8f)
        {
            baseForward = new(-chassisRight.Z, 0f, chassisRight.X);
        }

        if (baseForward.LengthSquared() <= 1e-8f)
        {
            baseForward = Vector3.UnitX;
        }

        baseForward = Vector3.Normalize(baseForward);
        Vector3 baseRight = Vector3.Normalize(new Vector3(-baseForward.Z, 0f, baseForward.X));
        turretForward = Vector3.Normalize(baseForward * MathF.Cos(localTurretYaw) + baseRight * MathF.Sin(localTurretYaw));
        turretRight = Vector3.Normalize(-baseForward * MathF.Sin(localTurretYaw) + baseRight * MathF.Cos(localTurretYaw));
        pitchedForward = Vector3.Normalize(turretForward * MathF.Cos(gimbalPitch) + Vector3.UnitY * MathF.Sin(gimbalPitch));
        pitchedUp = Vector3.Normalize(Vector3.Cross(turretRight, pitchedForward));
    }

    private static void ResolveWorldTurretAxes(
        float worldTurretYaw,
        float gimbalPitch,
        out Vector3 turretForward,
        out Vector3 turretRight,
        out Vector3 pitchedForward,
        out Vector3 pitchedUp)
    {
        turretForward = Vector3.Normalize(new Vector3(MathF.Cos(worldTurretYaw), 0f, MathF.Sin(worldTurretYaw)));
        turretRight = Vector3.Normalize(new Vector3(-turretForward.Z, 0f, turretForward.X));
        pitchedForward = Vector3.Normalize(turretForward * MathF.Cos(gimbalPitch) + Vector3.UnitY * MathF.Sin(gimbalPitch));
        pitchedUp = Vector3.Normalize(Vector3.Cross(turretRight, pitchedForward));
    }

    private bool TryResolveVisualArmorPlatePose(
        SimulationEntity entity,
        string plateId,
        out VisualArmorPlatePose pose)
        => TryResolveVisualArmorPlatePose(entity, plateId, _host.World.GameTimeSec, out pose);

    private bool TryResolveVisualArmorPlatePose(
        SimulationEntity entity,
        string plateId,
        double gameTimeSec,
        out VisualArmorPlatePose pose)
    {
        pose = default;
        RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
        if (Math.Abs(gameTimeSec - _host.World.GameTimeSec) <= 1e-4
            && plateId.StartsWith("armor_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(plateId.AsSpan("armor_".Length), out int oneBasedIndex))
        {
            IReadOnlyList<ArmorRenderComponent> armor = ResolveArmorComponents(profile);
            int index = oneBasedIndex - 1;
            if (index < 0 || index >= armor.Count)
            {
                return false;
            }

            float yaw = ResolveEntityYaw(entity);
            Vector3 entityCenter = ToScenePoint(entity.X, entity.Y, (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM));
            entityCenter += ResolveRuntimeChassisSceneOffset(entity, ResolveRuntimeChassisMotion(entity));
            ResolveChassisAxes(yaw, entity.ChassisPitchDeg, entity.ChassisRollDeg, out Vector3 chassisForward, out Vector3 chassisRight, out Vector3 chassisUp);
            ArmorRenderComponent component = armor[index];
            Vector3 plateCenter = OffsetScenePosition(
                entityCenter,
                component.LocalCenter.X,
                component.LocalCenter.Z,
                component.LocalCenter.Y + ResolveRuntimeChassisMotion(entity).BodyLiftM,
                chassisForward,
                chassisRight,
                chassisUp);
            ResolveLocalPlaneAxes(chassisForward, chassisRight, component.YawRad, out Vector3 plateForward, out Vector3 plateRight);
            ResolveRotatedAxes(
                plateForward,
                plateRight,
                chassisUp,
                new Vector3(0f, component.PitchRad * 180f / MathF.PI, component.RollRad * 180f / MathF.PI),
                out plateForward,
                out plateRight,
                out Vector3 plateUp);
            float halfWidth = Math.Max(0.06f, profile.ArmorPlateLengthM * 0.52f);
            float halfHeight = Math.Max(0.06f, profile.ArmorPlateHeightM * 0.52f);
            pose = new VisualArmorPlatePose(plateCenter, plateForward, plateRight, plateUp, halfWidth, halfHeight, plateId);
            return true;
        }

        ArmorPlateTarget plate = SimulationCombatMath.GetArmorPlateTargets(entity, _host.World.MetersPerWorldUnit, gameTimeSec)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, plateId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(plate.Id))
        {
            return false;
        }

        Vector3 center = ToScenePoint(plate.X, plate.Y, (float)plate.HeightM);
        Vector3 normal = Vector3.Normalize(SimulationCombatMath.ResolveArmorPlateNormal(plate));
        Vector3 right = Vector3.Cross(Vector3.UnitY, normal);
        if (right.LengthSquared() <= 1e-6f)
        {
            right = Vector3.Cross(Vector3.UnitX, normal);
        }

        right = Vector3.Normalize(right);
        Vector3 up = Vector3.Normalize(Vector3.Cross(normal, right));
        float plateHalfWidth = Math.Max(0.06f, (float)Math.Max(plate.SideLengthM, plate.WidthM) * 0.52f);
        float plateHalfHeight = Math.Max(0.045f, (float)Math.Max(plate.HeightSpanM, plate.SideLengthM) * 0.52f);
        pose = new VisualArmorPlatePose(center, normal, right, up, plateHalfWidth, plateHalfHeight, plate.Id);
        return true;
    }

    private static Color ResolveDeepGrayMaterial(Color source, bool alive, float bias)
        => source;

    private Color ResolveAppearanceMaterialColor(Color source, bool alive, float bias)
        => source;

    private static AppearanceDerivedGeometryCache BuildAppearanceGeometryCache(RobotAppearanceProfile profile)
    {
        float gap = Math.Max(0.005f, profile.ArmorPlateGapM);
        float thickness = ResolveArmorPlateThickness(profile);
        float centerY = profile.BodyClearanceM + profile.BodyHeightM * 0.5f;
        IReadOnlyList<float> orbitYaws = profile.ArmorOrbitYawsDeg.Count > 0
            ? profile.ArmorOrbitYawsDeg
            : new[] { 0f, 180f, 90f, 270f };
        IReadOnlyList<float> selfYaws = profile.ArmorSelfYawsDeg.Count > 0
            ? profile.ArmorSelfYawsDeg
            : orbitYaws;
        ArmorRenderComponent[] armorComponents = new ArmorRenderComponent[orbitYaws.Count];
        for (int index = 0; index < orbitYaws.Count; index++)
        {
            float orbitRad = orbitYaws[index] * MathF.PI / 180f;
            Vector2 outward = new(MathF.Cos(orbitRad), MathF.Sin(orbitRad));
            float supportDistance = ResolveBodyOutlineSupportDistance(profile, outward);
            float plateDistance = supportDistance + gap + thickness * 0.5f;
            float localX = outward.X * plateDistance;
            float localZ = outward.Y * plateDistance;
            float defaultYaw = orbitRad;

            float selfYaw = (index < selfYaws.Count ? selfYaws[index] : orbitYaws[index]) * MathF.PI / 180f;
            if (profile.ArmorSelfYawsDeg.Count == 0)
            {
                selfYaw = defaultYaw;
            }

            Vector3 plateOffset = index < profile.ArmorPlateOffsetsM.Count
                ? profile.ArmorPlateOffsetsM[index]
                : Vector3.Zero;
            Vector3 plateRotation = index < profile.ArmorPlateRotationsYprDeg.Count
                ? profile.ArmorPlateRotationsYprDeg[index]
                : Vector3.Zero;
            armorComponents[index] = new ArmorRenderComponent(
                new Vector3(localX + plateOffset.X, centerY + plateOffset.Y, localZ + plateOffset.Z),
                selfYaw + plateRotation.X * MathF.PI / 180f,
                plateRotation.Y * MathF.PI / 180f,
                plateRotation.Z * MathF.PI / 180f);
        }

        float armorHalfWidth = profile.ArmorPlateLengthM * 0.5f;
        float lightHalfWidth = Math.Max(0.005f, profile.ArmorLightWidthM * 0.5f);
        ArmorLightRenderComponent[] lightComponents = new ArmorLightRenderComponent[armorComponents.Length];
        for (int index = 0; index < armorComponents.Length; index++)
        {
            ArmorRenderComponent component = armorComponents[index];
            int lightIndexA = index * 2;
            int lightIndexB = lightIndexA + 1;
            float defaultPlateDistance = Math.Max(0.004f, profile.ArmorPlateGapM * 0.15f);
            float lightOffsetA = armorHalfWidth
                + lightHalfWidth
                + (lightIndexA < profile.ArmorLightPlateDistancesM.Count ? Math.Max(0f, profile.ArmorLightPlateDistancesM[lightIndexA]) : defaultPlateDistance);
            float lightOffsetB = armorHalfWidth
                + lightHalfWidth
                + (lightIndexB < profile.ArmorLightPlateDistancesM.Count ? Math.Max(0f, profile.ArmorLightPlateDistancesM[lightIndexB]) : defaultPlateDistance);
            Vector3 offsetA = lightIndexA < profile.ArmorLightOffsetsM.Count ? profile.ArmorLightOffsetsM[lightIndexA] : Vector3.Zero;
            Vector3 offsetB = lightIndexB < profile.ArmorLightOffsetsM.Count ? profile.ArmorLightOffsetsM[lightIndexB] : Vector3.Zero;
            Vector3 localForward = new(MathF.Cos(component.YawRad), 0f, MathF.Sin(component.YawRad));
            Vector3 localRight = new(-MathF.Sin(component.YawRad), 0f, MathF.Cos(component.YawRad));
            ResolveRotatedAxes(
                localForward,
                localRight,
                Vector3.UnitY,
                new Vector3(0f, component.PitchRad * 180f / MathF.PI, component.RollRad * 180f / MathF.PI),
                out _,
                out localRight,
                out _);
            localRight = localRight.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(localRight);
            lightComponents[index] = new ArmorLightRenderComponent(
                component.LocalCenter + localForward * offsetA.X + Vector3.UnitY * offsetA.Y + localRight * (lightOffsetA + offsetA.Z),
                component.LocalCenter + localForward * offsetB.X + Vector3.UnitY * offsetB.Y - localRight * (lightOffsetB - offsetB.Z),
                component.YawRad,
                component.PitchRad,
                component.RollRad);
        }

        return new AppearanceDerivedGeometryCache
        {
            ArmorComponents = armorComponents,
            ArmorLightComponents = lightComponents,
        };
    }

    private static IReadOnlyList<ArmorRenderComponent> ResolveArmorComponents(RobotAppearanceProfile profile)
        => _appearanceGeometryCache.GetValue(profile, static key => BuildAppearanceGeometryCache(key)).ArmorComponents;

    private static IReadOnlyList<ArmorLightRenderComponent> ResolveArmorLightComponents(RobotAppearanceProfile profile)
        => _appearanceGeometryCache.GetValue(profile, static key => BuildAppearanceGeometryCache(key)).ArmorLightComponents;

    private static float ResolveBodyOutlineSupportDistance(RobotAppearanceProfile profile, Vector2 outward)
    {
        if (outward.LengthSquared() <= 1e-8f)
        {
            return 0f;
        }

        outward = Vector2.Normalize(outward);
        float halfLength = profile.BodyLengthM * 0.5f;
        float halfWidth = profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale;
        Span<Vector2> local = stackalloc Vector2[8];
        int count;
        if (string.Equals(profile.BodyShape, "octagon", StringComparison.OrdinalIgnoreCase))
        {
            float chamfer = MathF.Min(halfLength, halfWidth) * 0.34f;
            local[0] = new(-halfLength + chamfer, -halfWidth);
            local[1] = new(halfLength - chamfer, -halfWidth);
            local[2] = new(halfLength, -halfWidth + chamfer);
            local[3] = new(halfLength, halfWidth - chamfer);
            local[4] = new(halfLength - chamfer, halfWidth);
            local[5] = new(-halfLength + chamfer, halfWidth);
            local[6] = new(-halfLength, halfWidth - chamfer);
            local[7] = new(-halfLength, -halfWidth + chamfer);
            count = 8;
        }
        else
        {
            local[0] = new(-halfLength, -halfWidth);
            local[1] = new(halfLength, -halfWidth);
            local[2] = new(halfLength, halfWidth);
            local[3] = new(-halfLength, halfWidth);
            count = 4;
        }

        float support = float.NegativeInfinity;
        for (int index = 0; index < count; index++)
        {
            support = MathF.Max(support, Vector2.Dot(local[index], outward));
        }

        return float.IsFinite(support) ? support : 0f;
    }


    private static float ResolveArmorPlateThickness(RobotAppearanceProfile profile)
    {
        if (profile.ArmorPlateThicknessM >= 0f)
        {
            return Math.Max(0.001f, profile.ArmorPlateThicknessM);
        }

        return Math.Max(
            0.004f,
            Math.Max(
                profile.ArmorPlateGapM * 0.75f,
                profile.ArmorPlateWidthM * 0.08f));
    }

    private void DrawRearHealthLightBar(
        Graphics graphics,
        SimulationEntity entity,
        Vector3 center,
        Vector3 chassisForward3,
        Vector3 chassisRight3,
        Vector3 chassisUp3,
        float bodyLength,
        float bodyWidth,
        float bodyTop,
        Color barColor)
    {
        float ratio = entity.MaxHealth <= 1e-6
            ? 0f
            : (float)Math.Clamp(entity.Health / entity.MaxHealth, 0.0, 1.0);
        float configuredLength = (float)entity.RearHealthLightLengthM;
        float configuredWidth = (float)entity.RearHealthLightWidthM;
        float configuredHeight = (float)entity.RearHealthLightHeightM;
        float fullWidth = configuredLength > 0.001f
            ? Math.Max(0.012f, configuredLength)
            : Math.Max(0.08f, bodyWidth * 0.74f);
        float filledWidth = Math.Max(0.001f, fullWidth * ratio);
        float barLength = configuredWidth > 0.001f
            ? Math.Max(0.004f, configuredWidth)
            : Math.Clamp(bodyLength * 0.045f, 0.018f, 0.038f);
        float barHeight = configuredHeight > 0.001f
            ? Math.Max(0.004f, configuredHeight)
            : Math.Clamp(bodyWidth * 0.035f, 0.010f, 0.018f);
        Vector3 rearEdgeCenter = center
            - chassisForward3 * (bodyLength * 0.5f + barLength * 0.24f)
            + chassisForward3 * (float)entity.RearHealthLightOffsetXM
            + chassisUp3 * (bodyTop + barHeight * 0.58f + (float)entity.RearHealthLightOffsetYM)
            + chassisRight3 * (float)entity.RearHealthLightOffsetZM;
        DrawOrientedBoxSolid(
            graphics,
            rearEdgeCenter,
            chassisForward3,
            chassisRight3,
            chassisUp3,
            barLength,
            fullWidth,
            barHeight,
            Color.FromArgb(entity.IsAlive ? 232 : 180, 18, 22, 28),
            Color.FromArgb(entity.IsAlive ? 244 : 190, 8, 10, 14),
            null);
        if (ratio <= 0.001f)
        {
            return;
        }

        Vector3 fillCenter = rearEdgeCenter - chassisRight3 * ((fullWidth - filledWidth) * 0.5f);
        Color fillBase = barColor;
        if (IsRobotRearHealthFlashActive(entity.Id, out float rearFlashIntensity))
        {
            fillBase = BlendColor(fillBase, Color.White, 0.94f * rearFlashIntensity);
        }

        Color fill = Color.FromArgb(entity.IsAlive ? 255 : 176, BlendColor(fillBase, Color.White, entity.IsAlive ? 0.12f : 0.0f));
        DrawOrientedBoxSolid(
            graphics,
            fillCenter,
            chassisForward3,
            chassisRight3,
            chassisUp3,
            barLength * 1.08f,
            filledWidth,
            barHeight * 1.28f,
            fill,
            Color.FromArgb(entity.IsAlive ? 255 : 184, BlendColor(fillBase, Color.White, 0.72f)),
            null);
    }

    private float DrawArmorLight(
        Graphics graphics,
        Vector3 center,
        Vector3 chassisForward3,
        Vector3 chassisRight3,
        Vector3 chassisUp3,
        Vector3 localCenter,
        ArmorLightRenderComponent component,
        SimulationEntity entity,
        RobotAppearanceProfile profile)
    {
        Color lightColor = ResolveFixedRobotLightColor(ResolveCanonicalEntityTeam(entity));
        if (IsRobotArmorFlashActive(entity.Id, out float armorFlashIntensity))
        {
            lightColor = BlendColor(lightColor, Color.FromArgb(255, 6, 8, 10), 0.92f * armorFlashIntensity);
        }

        Vector3 worldCenter = OffsetScenePosition(center, localCenter.X, localCenter.Z, localCenter.Y, chassisForward3, chassisRight3, chassisUp3);
        ResolveLocalPlaneAxes(chassisForward3, chassisRight3, component.YawRad, out Vector3 lightForward3, out Vector3 lightRight3);
        ResolveRotatedAxes(
            lightForward3,
            lightRight3,
            chassisUp3,
            new Vector3(0f, component.PitchRad * 180f / MathF.PI, component.RollRad * 180f / MathF.PI),
            out lightForward3,
            out lightRight3,
            out Vector3 lightUp3);
        DrawOrientedBoxSolid(
            graphics,
            worldCenter,
            lightForward3,
            lightRight3,
            lightUp3,
            Math.Max(0.006f, profile.ArmorLightWidthM),
            Math.Max(0.010f, profile.ArmorLightLengthM),
            Math.Max(0.005f, profile.ArmorLightHeightM),
            lightColor,
            Color.FromArgb(255, BlendColor(lightColor, Color.White, entity.IsAlive ? 0.68f : 0.18f)),
            null,
            matteMaterial: true);
        return localCenter.Y + profile.ArmorLightHeightM * 0.5f;
    }

    private static void ResolveLocalPlaneAxes(
        Vector3 baseForward,
        Vector3 baseRight,
        float localYaw,
        out Vector3 forward,
        out Vector3 right)
    {
        forward = Vector3.Normalize(baseForward * MathF.Cos(localYaw) + baseRight * MathF.Sin(localYaw));
        right = Vector3.Normalize(-baseForward * MathF.Sin(localYaw) + baseRight * MathF.Cos(localYaw));
    }

    private float DrawBarrelLight(
        Graphics graphics,
        Vector3 barrelPivot,
        Vector3 barrelAxis,
        Vector3 barrelRight,
        Vector3 barrelUp,
        RobotAppearanceProfile profile,
        SimulationEntity entity,
        float sideSign)
    {
        Vector3 axis = barrelAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(barrelAxis);
        Vector3 right = barrelRight.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(barrelRight);
        Vector3 up = barrelUp.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(barrelUp);
        float sideOffset = Math.Max(0.01f, profile.BarrelLightWidthM * 1.5f) * sideSign;
        Vector3 lightCenter = barrelPivot
            + axis * (profile.BarrelLengthM * 0.56f + profile.BarrelLightOffsetXM)
            + right * (sideOffset + profile.BarrelLightOffsetZM)
            + up * (profile.BarrelLightOffsetYM - profile.BarrelLightHeightM * 0.10f);
        Color lightColor = ResolveFixedRobotLightColor(ResolveCanonicalEntityTeam(entity));

        DrawCylinderSolid(
            graphics,
            lightCenter,
            axis,
            right,
            Math.Max(0.004f, profile.BarrelLightHeightM * 0.28f),
            Math.Max(0.006f, profile.BarrelLightLengthM * 0.5f),
            0f,
            lightColor,
            Color.FromArgb(255, BlendColor(lightColor, Color.White, entity.IsAlive ? 0.68f : 0.18f)),
            8,
            matteMaterial: true);
        return lightCenter.Y + profile.BarrelLightHeightM * 0.5f;
    }

    private static FrictionWheelPose[] ResolveBarrelFrictionWheelPoses(
        Vector3 barrelPivot,
        Vector3 barrelAxis,
        Vector3 barrelRight,
        Vector3 barrelUp,
        RobotAppearanceProfile profile)
    {
        FrictionWheelPose[] result = new FrictionWheelPose[string.Equals(profile.RoleKey, "hero", StringComparison.OrdinalIgnoreCase) ? 6 : 2];
        float radius = Math.Max(0f, profile.BarrelFrictionWheelRadiusM);
        float height = profile.BarrelFrictionWheelHeightM > 0f ? profile.BarrelFrictionWheelHeightM : profile.BarrelFrictionWheelWidthM;
        if (radius <= 0.002f || height <= 0.002f)
        {
            return result;
        }

        Vector3 axis = barrelAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(barrelAxis);
        Vector3 right = barrelRight.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(barrelRight);
        Vector3 up = barrelUp.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(barrelUp);
        float sideOffset = Math.Max(profile.BarrelFrictionWheelOffsetZM, profile.BarrelRadiusM + height * 0.85f);
        Vector3 baseCenter = barrelPivot
            + axis * profile.BarrelFrictionWheelOffsetXM
            + up * profile.BarrelFrictionWheelOffsetYM;
        Vector3 yprDeg = new(profile.BarrelFrictionWheelYawDeg, profile.BarrelFrictionWheelPitchDeg, profile.BarrelFrictionWheelRollDeg);
        Vector3 WheelOffset(int index)
        {
            Vector3 offset = index >= 0 && index < profile.BarrelFrictionWheelOffsetsM.Count
                ? profile.BarrelFrictionWheelOffsetsM[index]
                : Vector3.Zero;
            return axis * offset.X + up * offset.Y + right * offset.Z;
        }

        if (string.Equals(profile.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            float ringRadius = Math.Max(sideOffset, profile.BarrelRadiusM + radius * 1.05f);
            float groupGap = Math.Max(height * 1.35f, radius * 0.55f);
            int index = 0;
            for (int groupIndex = 0; groupIndex < 2; groupIndex++)
            {
                float groupShift = groupIndex == 0 ? -groupGap * 0.5f : groupGap * 0.5f;
                Vector3 groupCenter = baseCenter + axis * groupShift;
                for (int angleIndex = 0; angleIndex < 3; angleIndex++)
                {
                    float angleDeg = angleIndex == 0 ? 90f : angleIndex == 1 ? 210f : 330f;
                    float angle = angleDeg * MathF.PI / 180f;
                    Vector3 radial = Vector3.Normalize(up * MathF.Sin(angle) + right * MathF.Cos(angle));
                    Vector3 tangent = Vector3.Normalize(up * MathF.Cos(angle) - right * MathF.Sin(angle));
                    ResolveRotatedAxes(tangent, axis, radial, yprDeg, out Vector3 wheelAxis, out Vector3 wheelRadial, out _);
                    result[index] = new FrictionWheelPose(groupCenter + radial * ringRadius + WheelOffset(index), wheelAxis, wheelRadial, -1f, index);
                    index++;
                }
            }

            return result;
        }

        ResolveRotatedAxes(right, axis, up, yprDeg, out Vector3 pairAxis, out Vector3 pairRadial, out _);
        result[0] = new FrictionWheelPose(baseCenter - right * sideOffset + WheelOffset(0), pairAxis, pairRadial, -1f, 0);
        result[1] = new FrictionWheelPose(baseCenter + right * sideOffset + WheelOffset(1), pairAxis, pairRadial, 1f, 1);
        return result;
    }

    private float DrawBarrelFrictionWheels(
        Graphics graphics,
        Vector3 barrelPivot,
        Vector3 barrelAxis,
        Vector3 barrelRight,
        Vector3 barrelUp,
        RobotAppearanceProfile profile,
        SimulationEntity entity)
    {
        float radius = Math.Max(0f, profile.BarrelFrictionWheelRadiusM);
        float height = profile.BarrelFrictionWheelHeightM > 0f ? profile.BarrelFrictionWheelHeightM : profile.BarrelFrictionWheelWidthM;
        float halfWidth = Math.Max(0f, height * 0.5f);
        if (radius <= 0.002f || halfWidth <= 0.001f)
        {
            return 0f;
        }

        FrictionWheelPose[] wheels = ResolveBarrelFrictionWheelPoses(barrelPivot, barrelAxis, barrelRight, barrelUp, profile);
        if (wheels.Length == 0)
        {
            return 0f;
        }

        float spin = (float)((_host.World?.GameTimeSec ?? 0.0) * (Math.PI * 18.0));
        Color fill = Color.FromArgb(entity.IsAlive ? 242 : 208, BlendColor(profile.TurretColor, Color.FromArgb(30, 34, 40), 0.45f));
        Color edge = Color.FromArgb(entity.IsAlive ? 248 : 202, BlendColor(profile.TurretColor, Color.Black, 0.20f));
        float maxHeight = 0f;
        foreach (FrictionWheelPose wheel in wheels)
        {
            DrawCylinderSolid(graphics, wheel.Center, wheel.Axis, wheel.RadialHint, radius, halfWidth, spin * wheel.SpinSign, fill, edge, 22, matteMaterial: true);
            maxHeight = Math.Max(maxHeight, wheel.Center.Y + radius);
        }

        return maxHeight;
    }

    private bool IsArmorPlateVisibleFromCamera(SimulationEntity entity, Vector3 plateCenter, float plateYaw)
    {
        bool targetHasCoreOccluder = string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase);
        Vector3 normal = new(MathF.Cos(plateYaw), 0f, MathF.Sin(plateYaw));
        Vector3 side = new(-normal.Z, 0f, normal.X);
        const float sampleHalfSideM = 0.052f;

        Span<Vector3> samples = stackalloc Vector3[5];
        samples[0] = plateCenter;
        samples[1] = plateCenter + side * sampleHalfSideM;
        samples[2] = plateCenter - side * sampleHalfSideM;
        samples[3] = plateCenter + Vector3.UnitY * sampleHalfSideM;
        samples[4] = plateCenter - Vector3.UnitY * sampleHalfSideM;

        for (int index = 0; index < samples.Length; index++)
        {
            Vector3 sample = samples[index];
            if (IsTerrainOccludingPoint(sample))
            {
                continue;
            }

            if (targetHasCoreOccluder && IsEntityCoreOccludingPoint(entity, sample))
            {
                continue;
            }

            Vector3 toCamera = _cameraPositionM - sample;
            toCamera.Y = 0f;
            if (toCamera.LengthSquared() <= 1e-6f)
            {
                return true;
            }

            // Back-face rejection is intentionally soft. The chassis occlusion test is the
            // authoritative guard for far-side armor, while this avoids drawing inverted plates.
            if (Vector3.Dot(normal, Vector3.Normalize(toCamera)) >= -0.32f)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsEntityCoreOccludingPoint(SimulationEntity entity, Vector3 targetPoint)
    {
        Vector3 start = _cameraPositionM;
        Vector2 center = new((float)(entity.X * _host.World.MetersPerWorldUnit), (float)(entity.Y * _host.World.MetersPerWorldUnit));
        Vector2 start2 = new(start.X, start.Z);
        Vector2 end2 = new(targetPoint.X, targetPoint.Z);
        Vector2 segment = end2 - start2;
        if (segment.LengthSquared() <= 1e-8f)
        {
            return false;
        }

        float yawRad = (float)(entity.AngleDeg * Math.PI / 180.0);
        Vector2 forward = new(MathF.Cos(yawRad), MathF.Sin(yawRad));
        Vector2 right = new(-forward.Y, forward.X);
        float halfLength = (float)Math.Max(0.06, entity.BodyLengthM * 0.5 + 0.010);
        float halfWidth = (float)Math.Max(0.06, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5 + 0.010);
        Vector2 localStart = new(Vector2.Dot(start2 - center, forward), Vector2.Dot(start2 - center, right));
        Vector2 localEnd = new(Vector2.Dot(end2 - center, forward), Vector2.Dot(end2 - center, right));
        Vector2 localDelta = localEnd - localStart;
        float maxT = 0.96f;
        if (!TryClipOcclusionSlab(localStart.X, localDelta.X, -halfLength, halfLength, ref maxT, out float enterX, out float exitX)
            || !TryClipOcclusionSlab(localStart.Y, localDelta.Y, -halfWidth, halfWidth, ref maxT, out float enterY, out float exitY))
        {
            return false;
        }

        float enter = MathF.Max(0.04f, MathF.Max(enterX, enterY));
        float exit = MathF.Min(maxT, MathF.Min(exitX, exitY));
        if (enter > exit)
        {
            return false;
        }

        float sampleHeight = start.Y + (targetPoint.Y - start.Y) * enter;
        float bottom = (float)Math.Max(0.0, entity.GroundHeightM + entity.AirborneHeightM - 0.02);
        float top = bottom + (float)Math.Max(0.24, entity.BodyClearanceM + entity.BodyHeightM + entity.GimbalBodyHeightM * 0.55);
        return sampleHeight >= bottom && sampleHeight <= top;
    }

    private static bool TryClipOcclusionSlab(
        float origin,
        float delta,
        float min,
        float max,
        ref float maxT,
        out float enter,
        out float exit)
    {
        enter = 0f;
        exit = maxT;
        if (MathF.Abs(delta) <= 1e-7f)
        {
            return origin >= min && origin <= max;
        }

        float t0 = (min - origin) / delta;
        float t1 = (max - origin) / delta;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        enter = MathF.Max(enter, t0);
        exit = MathF.Min(exit, t1);
        maxT = exit;
        return enter <= exit;
    }

    private bool IsLocalSideVisibleFromCamera(Vector3 entityCenter, float chassisYaw, float localY, float bodyTopM)
    {
        if (Math.Abs(localY) <= 1e-4f)
        {
            return true;
        }

        Vector3 fullOffset = _cameraPositionM - entityCenter;
        if (fullOffset.LengthSquared() <= MathF.Max(6.5f, bodyTopM * bodyTopM * 6.0f))
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
        return MathF.Sign(localY) * cameraSide >= -0.24f;
    }

    private void DrawTaperedPlate(
        Graphics graphics,
        Vector3 center,
        Vector3 forwardAxis,
        Vector3 rightAxis,
        Vector3 upAxis,
        float topLength,
        float bottomLength,
        float width,
        float height,
        Color fillColor,
        Color edgeColor,
        bool matteMaterial = true)
    {
        fillColor = OpaqueSolidColor(fillColor);
        edgeColor = OpaqueSolidColor(edgeColor);
        if (ShouldUseGpuDynamicPrimitiveFastPath())
        {
            Vector3 forward = forwardAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(forwardAxis);
            Vector3 right = rightAxis.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(rightAxis);
            Vector3 up = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
            float fastHalfWidth = Math.Max(0.001f, width * 0.5f);
            float fastHalfHeight = Math.Max(0.001f, height * 0.5f);
            float fastRearX = -Math.Max(0.001f, bottomLength) * 0.5f;
            float fastFrontBottomX = fastRearX + Math.Max(0.001f, bottomLength);
            float fastFrontTopX = fastRearX + Math.Max(0.001f, topLength);
            Span<Vector3> fastBottom = stackalloc Vector3[4];
            Span<Vector3> fastTop = stackalloc Vector3[4];
            fastBottom[0] = center + forward * fastRearX - right * fastHalfWidth - up * fastHalfHeight;
            fastBottom[1] = center + forward * fastFrontBottomX - right * fastHalfWidth - up * fastHalfHeight;
            fastBottom[2] = center + forward * fastFrontBottomX + right * fastHalfWidth - up * fastHalfHeight;
            fastBottom[3] = center + forward * fastRearX + right * fastHalfWidth - up * fastHalfHeight;
            fastTop[0] = center + forward * fastRearX - right * fastHalfWidth + up * fastHalfHeight;
            fastTop[1] = center + forward * fastFrontTopX - right * fastHalfWidth + up * fastHalfHeight;
            fastTop[2] = center + forward * fastFrontTopX + right * fastHalfWidth + up * fastHalfHeight;
            fastTop[3] = center + forward * fastRearX + right * fastHalfWidth + up * fastHalfHeight;
            DrawGpuGeneralPrismFast(fastBottom, fastTop, fillColor, 0.78f, 0.52f, matteMaterial);
            return;
        }

        float halfWidth = Math.Max(0.001f, width * 0.5f);
        float rearX = -Math.Max(0.001f, bottomLength) * 0.5f;
        float frontBottomX = rearX + Math.Max(0.001f, bottomLength);
        float frontTopX = rearX + Math.Max(0.001f, topLength);
        IReadOnlyList<Vector3> bottom = BuildPlateFace(center, forwardAxis, rightAxis, upAxis, rearX, frontBottomX, halfWidth, -height * 0.5f);
        IReadOnlyList<Vector3> top = BuildPlateFace(center, forwardAxis, rightAxis, upAxis, rearX, frontTopX, halfWidth, height * 0.5f);
        DrawGeneralPrism(graphics, bottom, top, fillColor, edgeColor, null, matteMaterial);
    }

    private static IReadOnlyList<Vector3> BuildPlateFace(
        Vector3 center,
        Vector3 forwardAxis,
        Vector3 rightAxis,
        Vector3 upAxis,
        float rearX,
        float frontX,
        float halfWidth,
        float localY)
    {
        Vector2[] local =
        {
            new(rearX, -halfWidth),
            new(frontX, -halfWidth),
            new(frontX, halfWidth),
            new(rearX, halfWidth),
        };
        Vector3 forward = forwardAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(forwardAxis);
        Vector3 right = rightAxis.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(rightAxis);
        Vector3 up = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        Vector3[] result = new Vector3[local.Length];
        for (int index = 0; index < local.Length; index++)
        {
            result[index] = center + forward * local[index].X + right * local[index].Y + up * localY;
        }

        return result;
    }

    private void DrawGeneralPrism(
        Graphics graphics,
        IReadOnlyList<Vector3> bottomVertices,
        IReadOnlyList<Vector3> topVertices,
        Color fillColor,
        Color edgeColor,
        string? label,
        bool matteMaterial = true)
    {
        if (bottomVertices.Count < 3 || topVertices.Count != bottomVertices.Count)
        {
            return;
        }

        fillColor = OpaqueSolidColor(fillColor);
        edgeColor = OpaqueSolidColor(edgeColor);
        if (ShouldUseGpuDynamicPrimitiveFastPath())
        {
            DrawGpuGeneralPrismFast(bottomVertices, topVertices, fillColor, 0.78f, 0.52f, matteMaterial);
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

        DrawSolidFaces(graphics, faces, fillColor, edgeColor, label, matteMaterial);
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
            24,
            matteMaterial: true);

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
            18,
            matteMaterial: true);
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
        int segmentCount,
        bool matteMaterial = true)
    {
        if (radius <= 1e-4f || halfLength <= 1e-4f)
        {
            return;
        }

        fillColor = OpaqueSolidColor(fillColor);
        edgeColor = OpaqueSolidColor(edgeColor);
        if (ShouldUseGpuDynamicPrimitiveFastPath())
        {
            DrawGpuCylinderSolidFast(center, axisDirection, radialHint, radius, halfLength, spinRad, fillColor, segmentCount, matteMaterial);
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

        DrawSolidFaces(graphics, faces, fillColor, edgeColor, null, matteMaterial);
    }

    private void DrawHollowOctagonalBarrel(
        Graphics graphics,
        Vector3 center,
        Vector3 axisDirection,
        Vector3 rightHint,
        Vector3 upHint,
        float radius,
        float halfLength,
        float longEdgeM,
        float shortEdgeM,
        Color fillColor,
        Color edgeColor,
        bool matteMaterial = true)
    {
        if (radius <= 1e-4f || halfLength <= 1e-4f)
        {
            return;
        }

        fillColor = OpaqueSolidColor(fillColor);
        edgeColor = OpaqueSolidColor(edgeColor);
        if (ShouldUseGpuDynamicPrimitiveFastPath())
        {
            DrawGpuHollowOctagonalBarrelFast(center, axisDirection, rightHint, upHint, radius, halfLength, longEdgeM, shortEdgeM, fillColor, matteMaterial);
            return;
        }

        Vector3 axis = axisDirection.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(axisDirection);
        Vector3 right = rightHint - axis * Vector3.Dot(rightHint, axis);
        if (right.LengthSquared() <= 1e-8f)
        {
            right = Vector3.Cross(Math.Abs(Vector3.Dot(axis, Vector3.UnitY)) > 0.92f ? Vector3.UnitZ : Vector3.UnitY, axis);
        }

        right = Vector3.Normalize(right);
        Vector3 up = upHint - axis * Vector3.Dot(upHint, axis) - right * Vector3.Dot(upHint, right);
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.Cross(axis, right);
        }

        up = Vector3.Normalize(up);
        ResolveBarrelOctagonEdges(radius, longEdgeM, shortEdgeM, out float longEdge, out float shortEdge);
        float diagonal = shortEdge / MathF.Sqrt(2f);
        float halfLong = longEdge * 0.5f;
        float halfExtent = halfLong + diagonal * 0.5f;
        Vector2[] section =
        {
            new(-halfLong, halfExtent),
            new(halfLong, halfExtent),
            new(halfLong + diagonal, halfExtent - diagonal),
            new(halfLong + diagonal, -halfExtent + diagonal),
            new(halfLong, -halfExtent),
            new(-halfLong, -halfExtent),
            new(-halfLong - diagonal, -halfExtent + diagonal),
            new(-halfLong - diagonal, halfExtent - diagonal),
        };

        Vector3 rear = center - axis * halfLength;
        Vector3 muzzle = center + axis * halfLength;
        Vector3[] rearRing = new Vector3[section.Length];
        Vector3[] muzzleRing = new Vector3[section.Length];
        for (int index = 0; index < section.Length; index++)
        {
            Vector3 offset = right * section[index].X + up * section[index].Y;
            rearRing[index] = rear + offset;
            muzzleRing[index] = muzzle + offset;
        }

        var faces = new List<SolidFace>(section.Length + 2)
        {
            new(muzzleRing.ToArray(), 0.80f),
            new(rearRing.Reverse().ToArray(), 0.58f),
        };
        for (int index = 0; index < section.Length; index++)
        {
            int next = (index + 1) % section.Length;
            faces.Add(new SolidFace(
                new[]
                {
                    rearRing[index],
                    rearRing[next],
                    muzzleRing[next],
                    muzzleRing[index],
                },
                index is 0 or 4 ? 0.72f : index is 2 or 6 ? 0.54f : 0.62f));
        }

        DrawSolidFaces(graphics, faces, fillColor, edgeColor, null, matteMaterial);
        DrawCylinderSolid(
            graphics,
            muzzle + axis * 0.004f,
            axis,
            up,
            Math.Max(0.004f, radius * 0.58f),
            0.004f,
            0f,
            Color.FromArgb(248, 8, 10, 14),
            Color.FromArgb(255, 2, 3, 5),
            18,
            matteMaterial: true);
    }

    private static void ResolveBarrelOctagonEdges(float radius, float configuredLongEdgeM, float configuredShortEdgeM, out float longEdgeM, out float shortEdgeM)
    {
        float safeRadius = Math.Max(0.004f, radius);
        longEdgeM = configuredLongEdgeM > 1e-5f ? configuredLongEdgeM : safeRadius * 1.80f;
        shortEdgeM = configuredShortEdgeM > 1e-5f ? configuredShortEdgeM : safeRadius * 0.72f;
        longEdgeM = Math.Max(0.004f, longEdgeM);
        shortEdgeM = Math.Max(0.002f, shortEdgeM);
    }

    private void DrawBeam3d(
        Graphics graphics,
        Vector3 start,
        Vector3 end,
        Vector3 lateralAxis,
        float height,
        float thickness,
        Color fillColor,
        Color edgeColor,
        bool matteMaterial = true)
    {
        Vector3 axis = end - start;
        if (axis.LengthSquared() <= 1e-8f)
        {
            return;
        }

        fillColor = OpaqueSolidColor(fillColor);
        edgeColor = OpaqueSolidColor(edgeColor);
        if (ShouldUseGpuDynamicPrimitiveFastPath())
        {
            DrawGpuBeam3dFast(start, end, lateralAxis, height, thickness, fillColor, matteMaterial);
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
        DrawSolidFaces(graphics, faces, fillColor, edgeColor, null, matteMaterial);
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
        string? label,
        bool matteMaterial = true)
    {
        if (length <= 1e-4f || width <= 1e-4f || height <= 1e-4f)
        {
            return;
        }

        fillColor = OpaqueSolidColor(fillColor);
        edgeColor = OpaqueSolidColor(edgeColor);
        if (ShouldUseGpuDynamicPrimitiveFastPath())
        {
            DrawGpuOrientedBoxFast(center, forwardDirection, rightDirection, upDirection, length, width, height, fillColor, matteMaterial);
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
        DrawSolidFaces(graphics, faces, fillColor, edgeColor, label, matteMaterial);
    }

    private void DrawSolidFaces(
        Graphics graphics,
        IReadOnlyList<SolidFace> faces,
        Color fillColor,
        Color edgeColor,
        string? label,
        bool matteMaterial = true)
    {
        fillColor = OpaqueSolidColor(fillColor);
        edgeColor = OpaqueSolidColor(edgeColor);
        if (_gpuGeometryPass && UseGpuRenderer)
        {
            DrawGpuSolidFaces(faces, fillColor, edgeColor, matteMaterial);

            if (!string.IsNullOrWhiteSpace(label))
            {
                Vector3 gpuLabelPoint = Vector3.Zero;
                int gpuLabelVertices = 0;
                foreach (SolidFace solidFace in faces)
                {
                    foreach (Vector3 vertex in solidFace.Vertices)
                    {
                        gpuLabelPoint += vertex;
                        gpuLabelVertices++;
                    }
                }

                if (gpuLabelVertices > 0)
                {
                    gpuLabelPoint /= gpuLabelVertices;
                    if (TryProject(gpuLabelPoint + new Vector3(0f, 0.05f, 0f), out PointF screenLabel, out _))
                    {
                        using var textBrush = new SolidBrush(Color.FromArgb(230, 230, 234, 242));
                        SizeF size = graphics.MeasureString(label, _smallHudFont);
                        graphics.DrawString(label, _smallHudFont, textBrush, screenLabel.X - size.Width * 0.5f, screenLabel.Y - 11f);
                    }
                }
            }

            return;
        }

        _projectedFaceScratchBuffer.Clear();
        List<ProjectedFace> projectedFaces = _projectedFaceScratchBuffer;
        projectedFaces.EnsureCapacity(faces.Count);
        Vector3 labelPoint = Vector3.Zero;
        int labelVertices = 0;
        foreach (SolidFace solidFace in faces)
        {
            foreach (Vector3 vertex in solidFace.Vertices)
            {
                labelPoint += vertex;
                labelVertices++;
            }

            if (ShouldCullSolidFace(solidFace.Vertices))
            {
                continue;
            }

            if (TryBuildProjectedFace(
                solidFace.Vertices,
                ShadeFaceColor(fillColor, solidFace.Vertices, solidFace.Ambient, matteMaterial),
                edgeColor,
                out ProjectedFace projected))
            {
                projectedFaces.Add(projected);
            }
        }

        projectedFaces.Sort((left, right) => right.AverageDepth.CompareTo(left.AverageDepth));
        ReduceProjectedSolidFacesForPerformance(projectedFaces);
        if (_collectProjectedFacesOnly)
        {
            _projectedEntityFaceBuffer.AddRange(projectedFaces);
        }
        else
        {
            DrawProjectedFaceBatch(graphics, projectedFaces, 1f);
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

    private static Color OpaqueSolidColor(Color color)
        => color.A == 255 ? color : Color.FromArgb(255, color);

    private void ReduceProjectedSolidFacesForPerformance(List<ProjectedFace> projectedFaces)
    {
        if (!_firstPersonView || projectedFaces.Count <= 6)
        {
            return;
        }

        var reduced = new List<ProjectedFace>(projectedFaces.Count);
        int smallFaceBudget = projectedFaces.Count >= 24 ? 10 : 6;
        foreach (ProjectedFace face in projectedFaces)
        {
            GetProjectedFaceBounds(face, out float minX, out float minY, out float maxX, out float maxY);
            float area = Math.Max(1f, maxX - minX) * Math.Max(1f, maxY - minY);
            bool deepFar = face.AverageDepth > 18f;
            bool far = face.AverageDepth > 12f;
            bool tiny = area < 65f;
            bool small = area < 140f;
            if ((deepFar && small) || (far && tiny))
            {
                if (smallFaceBudget <= 0)
                {
                    continue;
                }

                smallFaceBudget--;
                Color fill = deepFar
                    ? BlendColor(face.FillColor, Color.FromArgb(face.FillColor.A, 206, 208, 212), 0.24f)
                    : face.FillColor;
                reduced.Add(new ProjectedFace(face.Points, face.AverageDepth, fill, face.EdgeColor));
                continue;
            }

            reduced.Add(face);
        }

        projectedFaces.Clear();
        projectedFaces.AddRange(reduced);
    }

    private float DrawBalanceLegAssembly(
        Graphics graphics,
        Vector3 center,
        float yaw,
        Vector3 lateralAxis,
        Vector3 chassisForward3,
        Vector3 chassisRight3,
        Vector3 chassisUp3,
        RobotAppearanceProfile profile,
        SimulationEntity entity,
        Color legColor,
        RuntimeChassisMotion motion)
    {
        float maxHeight = 0f;
        Vector3 forwardAxis = chassisForward3;
        float bodySideOffset = Math.Max(0.02f, profile.BodyWidthM * profile.BodyRenderWidthScale * 0.5f * 0.98f);
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float sideSign = sideIndex == 0 ? -1f : 1f;
            BalanceLegGeometry leg = ResolveBalanceLegGeometry(entity, profile, motion, sideSign);
            maxHeight = Math.Max(maxHeight, Math.Max(leg.UpperFront.Y, leg.KneeCenter.Y));
            float sideOffset = leg.SideOffset * sideSign;
            float mountSideOffset = bodySideOffset * sideSign;

            Vector3 mountFront = OffsetScenePosition(center, leg.UpperFront.X, mountSideOffset, leg.UpperFront.Y, chassisForward3, chassisRight3, chassisUp3);
            Vector3 mountRear = OffsetScenePosition(center, leg.UpperRear.X, mountSideOffset, leg.UpperRear.Y, chassisForward3, chassisRight3, chassisUp3);
            Vector3 upperFront = OffsetScenePosition(center, leg.UpperFront.X, sideOffset, leg.UpperFront.Y, chassisForward3, chassisRight3, chassisUp3);
            Vector3 upperRear = OffsetScenePosition(center, leg.UpperRear.X, sideOffset, leg.UpperRear.Y, chassisForward3, chassisRight3, chassisUp3);
            Vector3 kneeFront = OffsetScenePosition(center, leg.KneeFront.X, sideOffset, leg.KneeFront.Y, chassisForward3, chassisRight3, chassisUp3);
            Vector3 kneeRear = OffsetScenePosition(center, leg.KneeRear.X, sideOffset, leg.KneeRear.Y, chassisForward3, chassisRight3, chassisUp3);
            Vector3 kneeCenter = OffsetScenePosition(center, leg.KneeCenter.X, sideOffset, leg.KneeCenter.Y, chassisForward3, chassisRight3, chassisUp3);
            float legFootSideOffset = leg.SideOffset * sideSign;
            float wheelRadius = ResolveRearLegWheelRadiusM(profile);
            float wheelSideOffset = ResolveBalanceLegWheelSideOffset(profile, leg, wheelRadius) * sideSign;
            Vector3 wheelAnchor = OffsetScenePosition(center, leg.Foot.X, wheelSideOffset, leg.Foot.Y, chassisForward3, chassisRight3, chassisUp3);
            Vector3 foot = OffsetScenePosition(center, leg.Foot.X, legFootSideOffset, leg.Foot.Y, chassisForward3, chassisRight3, chassisUp3);

            float mountBeamHeight = Math.Max(0.010f, profile.RearClimbAssistUpperHeightM * 0.88f);
            float mountBeamWidth = Math.Max(0.010f, profile.RearClimbAssistUpperWidthM * 0.95f);
            Color mountFill = Color.FromArgb(entity.IsAlive ? 248 : 224, BlendColor(legColor, Color.White, 0.10f));
            Color mountEdge = Color.FromArgb(entity.IsAlive ? 252 : 216, BlendColor(legColor, Color.Black, 0.08f));
            DrawBeam3d(graphics, mountFront, upperFront, forwardAxis, mountBeamHeight, mountBeamWidth, mountFill, mountEdge);
            DrawBeam3d(graphics, mountRear, upperRear, forwardAxis, mountBeamHeight, mountBeamWidth, mountFill, mountEdge);
            DrawBeam3d(graphics, mountFront, mountRear, lateralAxis, mountBeamHeight, mountBeamWidth, mountFill, mountEdge);

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
            DrawBeam3d(
                graphics,
                foot,
                wheelAnchor,
                forwardAxis,
                Math.Max(0.008f, profile.RearClimbAssistLowerHeightM * 0.72f),
                Math.Max(0.008f, profile.RearClimbAssistLowerWidthM * 0.72f),
                Color.FromArgb(entity.IsAlive ? 238 : 214, BlendColor(legColor, Color.White, 0.08f)),
                Color.FromArgb(entity.IsAlive ? 246 : 210, BlendColor(legColor, Color.Black, 0.08f)));
            DrawLegHub(graphics, center, yaw, chassisForward3, chassisRight3, chassisUp3, mountSideOffset, leg.UpperFront, leg.HingeRadius * 0.86f, entity, legColor);
            DrawLegHub(graphics, center, yaw, chassisForward3, chassisRight3, chassisUp3, mountSideOffset, leg.UpperRear, leg.HingeRadius * 0.86f, entity, legColor);
            DrawLegHub(graphics, center, yaw, chassisForward3, chassisRight3, chassisUp3, sideOffset, leg.UpperFront, leg.HingeRadius, entity, legColor);
            DrawLegHub(graphics, center, yaw, chassisForward3, chassisRight3, chassisUp3, sideOffset, leg.UpperRear, leg.HingeRadius, entity, legColor);
            DrawLegHub(graphics, center, yaw, chassisForward3, chassisRight3, chassisUp3, sideOffset, leg.KneeFront, leg.HingeRadius, entity, legColor);
            DrawLegHub(graphics, center, yaw, chassisForward3, chassisRight3, chassisUp3, sideOffset, leg.KneeRear, leg.HingeRadius, entity, legColor);
            DrawLegHub(graphics, center, yaw, chassisForward3, chassisRight3, chassisUp3, legFootSideOffset, leg.Foot, leg.HingeRadius, entity, legColor);
            DrawLegHub(graphics, center, yaw, chassisForward3, chassisRight3, chassisUp3, wheelSideOffset, leg.Foot, leg.HingeRadius * 0.72f, entity, legColor);

            maxHeight = Math.Max(maxHeight, Math.Max(leg.Foot.Y, leg.KneeCenter.Y) + leg.HingeRadius);
        }

        return maxHeight;
    }

    private void DrawLegHub(
        Graphics graphics,
        Vector3 center,
        float yaw,
        Vector3 chassisForward3,
        Vector3 chassisRight3,
        Vector3 chassisUp3,
        float sideOffset,
        Vector2 localPoint,
        float radius,
        SimulationEntity entity,
        Color legColor)
    {
        float baseHeight = localPoint.Y - radius;
        Vector3 hubCenter = OffsetScenePosition(center, localPoint.X, sideOffset, baseHeight, chassisForward3, chassisRight3, chassisUp3);
        DrawOrientedBoxSolid(
            graphics,
            hubCenter + chassisUp3 * radius,
            chassisForward3,
            chassisRight3,
            chassisUp3,
            Math.Max(0.008f, radius * 1.4f),
            Math.Max(0.008f, radius * 1.4f),
            Math.Max(0.010f, radius * 2f),
            Color.FromArgb(entity.IsAlive ? 248 : 226, BlendColor(legColor, Color.White, 0.12f)),
            Color.FromArgb(entity.IsAlive ? 250 : 216, BlendColor(legColor, Color.Black, 0.10f)),
            null);
    }

    private BalanceLegGeometry ResolveBalanceLegGeometry(SimulationEntity entity, RobotAppearanceProfile profile, RuntimeChassisMotion motion, float sideSign)
    {
        float bodyHalfX = profile.BodyLengthM * 0.5f;
        float wheelRadius = ResolveRearLegWheelRadiusM(profile);
        float footX = -bodyHalfX * 0.78f;
        if (profile.WheelOffsetsM.Count > 0)
        {
            float sideBias = sideSign < 0f ? -1f : 1f;
            float sideFootX = float.PositiveInfinity;
            float allFootX = float.PositiveInfinity;
            foreach (Vector2 offset in profile.WheelOffsetsM)
            {
                allFootX = MathF.Min(allFootX, offset.X);
                if (Math.Abs(offset.Y) > 1e-4f && Math.Sign(offset.Y) == Math.Sign(sideBias))
                {
                    sideFootX = MathF.Min(sideFootX, offset.X);
                }
            }

            footX = float.IsFinite(sideFootX) ? sideFootX : allFootX;
        }

        footX += motion.RearFootReachM;
        float footY = wheelRadius + motion.RearFootRaiseM;
        float wheelOuter = profile.WheelOffsetsM.Count > 0
            ? profile.WheelOffsetsM.Max(offset => Math.Abs(offset.Y) * profile.BodyRenderWidthScale)
            : profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale + wheelRadius * 0.55f;
        float rawSideOffset = Math.Max(
            profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale + wheelRadius * 0.28f,
            wheelOuter + wheelRadius * 0.08f);
        float bodyHalfSide = profile.BodyWidthM * 0.5f * profile.BodyRenderWidthScale;
        float armorThickness = ResolveArmorPlateThickness(profile);
        float armorCenterSide = bodyHalfSide + Math.Max(0.005f, profile.ArmorPlateGapM) + armorThickness * 1.35f;
        float hingeInsideLimit = armorCenterSide - Math.Max(0.018f, profile.RearClimbAssistHingeRadiusM * 1.35f);
        float minSideOffset = bodyHalfSide + Math.Max(0.004f, profile.RearClimbAssistHingeRadiusM * 0.30f);
        float maxSideOffset = Math.Max(minSideOffset, hingeInsideLimit);
        float sideOffset = Math.Clamp(rawSideOffset, minSideOffset, maxSideOffset);
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
        if (entity.AirborneHeightM <= 1e-4)
        {
            float yaw = ResolveAppearanceYaw(entity);
            Vector2 forward = new(MathF.Cos(yaw), MathF.Sin(yaw));
            Vector2 right = new(-forward.Y, forward.X);
            float clampedSideSign = sideSign < 0f ? -1f : 1f;
            float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
            float baseHeight = (float)(entity.GroundHeightM + entity.AirborneHeightM);
            float nominalFootHeight = wheelRadius + motion.RearFootRaiseM;
            float referenceBottomHeight = baseHeight + nominalFootHeight - wheelRadius;
            float terrain = SampleWheelVisualContactHeight(
                (float)(entity.X + (forward.X * foot.X + right.X * sideOffset * clampedSideSign) / metersPerWorldUnit),
                (float)(entity.Y + (forward.Y * foot.X + right.Y * sideOffset * clampedSideSign) / metersPerWorldUnit),
                referenceBottomHeight);
            float oppositeTerrain = SampleWheelVisualContactHeight(
                (float)(entity.X + (forward.X * foot.X - right.X * sideOffset * clampedSideSign) / metersPerWorldUnit),
                (float)(entity.Y + (forward.Y * foot.X - right.Y * sideOffset * clampedSideSign) / metersPerWorldUnit),
                referenceBottomHeight);
            float centerTerrain = SampleWheelVisualContactHeight((float)entity.X, (float)entity.Y, referenceBottomHeight);
            float targetFootHeight = terrain + wheelRadius - baseHeight;
            float terrainSpread = MathF.Max(
                MathF.Abs(terrain - oppositeTerrain),
                MathF.Max(MathF.Abs(terrain - centerTerrain), MathF.Abs(oppositeTerrain - centerTerrain)));
            bool stableFlatFooting =
                terrainSpread <= 0.045f
                && Math.Abs(entity.ChassisPitchDeg) <= 4.0
                && Math.Abs(entity.ChassisRollDeg) <= 4.0
                && MathF.Abs(motion.RearFootRaiseM) <= 0.018f
                && MathF.Abs(motion.RearFootReachM) <= 0.035f;
            if (stableFlatFooting || MathF.Abs(targetFootHeight - nominalFootHeight) <= 0.018f)
            {
                targetFootHeight = nominalFootHeight;
            }

            float terrainDelta = targetFootHeight - foot.Y;
            float terrainReachBias = stableFlatFooting
                ? 0f
                : Math.Clamp(-terrainDelta * 0.08f, -0.045f, 0.035f);
            foot = ClampTwoLinkTargetPoint(
                new Vector2(anchorX, anchorY),
                new Vector2(foot.X + terrainReachBias, targetFootHeight),
                profile.RearClimbAssistUpperLengthM,
                profile.RearClimbAssistLowerLengthM,
                profile.RearClimbAssistKneeMinDeg,
                profile.RearClimbAssistKneeMaxDeg);
        }

        foot = SmoothBalanceLegFootTarget(entity, sideSign, foot);

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

    private Vector2 SmoothBalanceLegFootTarget(SimulationEntity entity, float sideSign, Vector2 targetFoot)
    {
        bool leftSide = sideSign < 0f;
        double storedX = leftSide ? entity.VisualLegLeftFootXM : entity.VisualLegRightFootXM;
        double storedY = leftSide ? entity.VisualLegLeftFootYM : entity.VisualLegRightFootYM;
        if (!double.IsFinite(storedX) || !double.IsFinite(storedY))
        {
            StoreBalanceLegFootTarget(entity, leftSide, targetFoot);
            return targetFoot;
        }

        Vector2 currentFoot = new((float)storedX, (float)storedY);
        float distance = Vector2.Distance(currentFoot, targetFoot);
        double dt = Math.Clamp(_host.DeltaTimeSec, 1.0 / 240.0, 1.0 / 30.0);
        double responseHz = entity.TraversalActive ? 8.5 : 6.5;
        if (distance > 0.10f)
        {
            responseHz *= 0.58;
        }

        float alpha = (float)Math.Clamp(1.0 - Math.Exp(-responseHz * dt), 0.035, 0.22);
        Vector2 smoothedFoot = Vector2.Lerp(currentFoot, targetFoot, alpha);
        float maxStep = Math.Max(0.004f, (float)((entity.TraversalActive ? 1.10 : 0.78) * dt));
        Vector2 step = smoothedFoot - currentFoot;
        if (step.LengthSquared() > maxStep * maxStep)
        {
            smoothedFoot = currentFoot + Vector2.Normalize(step) * maxStep;
        }

        StoreBalanceLegFootTarget(entity, leftSide, smoothedFoot);
        return smoothedFoot;
    }

    private static void StoreBalanceLegFootTarget(SimulationEntity entity, bool leftSide, Vector2 foot)
    {
        if (leftSide)
        {
            entity.VisualLegLeftFootXM = foot.X;
            entity.VisualLegLeftFootYM = foot.Y;
            return;
        }

        entity.VisualLegRightFootXM = foot.X;
        entity.VisualLegRightFootYM = foot.Y;
    }

    private float ResolveTerrainAwareWheelCenterHeight(
        SimulationEntity entity,
        Vector3 center,
        Vector2 forward2,
        Vector2 right2,
        Vector3 chassisForward3,
        Vector3 chassisRight3,
        Vector3 wheelAxis,
        Vector3 chassisUp3,
        float localX,
        float localY,
        float wheelRadius,
        float fallbackCenterHeight,
        bool fixedToLeg)
    {
        if (fixedToLeg
            || (entity.TraversalActive
                && string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase)
                && localX > 1e-4f))
        {
            return fallbackCenterHeight;
        }

        float metersPerWorldUnit = (float)Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        float sampleWorldX = (float)(entity.X + (forward2.X * localX + right2.X * localY) / metersPerWorldUnit);
        float sampleWorldY = (float)(entity.Y + (forward2.Y * localX + right2.Y * localY) / metersPerWorldUnit);
        float contactOffsetY = ResolveVisualWheelSurfaceContactVerticalOffset(wheelRadius, wheelAxis);
        float fallbackBottomHeight =
            center.Y
            + chassisForward3.Y * localX
            + chassisRight3.Y * localY
            + chassisUp3.Y * fallbackCenterHeight
            + contactOffsetY;
        float terrainHeight = SampleWheelVisualContactHeight(sampleWorldX, sampleWorldY, fallbackBottomHeight);
        float numerator =
            terrainHeight
            - center.Y
            - chassisForward3.Y * localX
            - chassisRight3.Y * localY
            - contactOffsetY;
        float upY = MathF.Abs(chassisUp3.Y) <= 1e-4f ? 1f : chassisUp3.Y;
        float resolvedCenterHeight = numerator / upY;
        if (!float.IsFinite(resolvedCenterHeight))
        {
            return fallbackCenterHeight;
        }

        float downReach = fixedToLeg ? 0.24f : 0.075f;
        float upReach = fixedToLeg ? 0.30f : 0.24f;
        if (string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase))
        {
            downReach = Math.Min(downReach, 0.060f);
            upReach = Math.Max(upReach, 0.22f);
        }

        return Math.Clamp(resolvedCenterHeight, fallbackCenterHeight - downReach, fallbackCenterHeight + upReach);
    }

    private float SampleWheelVisualContactHeight(float worldX, float worldY, float referenceBottomHeightM)
    {
        if (_cachedRuntimeGrid is null || !_cachedRuntimeGrid.IsValid)
        {
            return SampleTerrainHeightMeters(worldX, worldY);
        }

        float maxWorldX = _cachedRuntimeGrid.WidthCells * _cachedRuntimeGrid.CellWidthWorld;
        float maxWorldY = _cachedRuntimeGrid.HeightCells * _cachedRuntimeGrid.CellHeightWorld;
        float sampleX = Math.Clamp(worldX, 0f, Math.Max(0f, maxWorldX - 1e-4f));
        float sampleY = Math.Clamp(worldY, 0f, Math.Max(0f, maxWorldY - 1e-4f));
        if (_cachedRuntimeGrid.TrySampleClosestCollisionSurface(
                sampleX,
                sampleY,
                referenceBottomHeightM,
                out TerrainSurfaceSample closestSample,
                maxCellRadius: 1))
        {
            return closestSample.HeightM <= 0.02f ? 0f : (float)closestSample.HeightM;
        }

        return SampleTerrainHeightMeters(sampleX, sampleY);
    }

    private static float ResolveVisualWheelSurfaceContactVerticalOffset(float radiusM, Vector3 wheelAxis)
    {
        Vector3 axis = wheelAxis.LengthSquared() <= 1e-8f ? Vector3.UnitZ : Vector3.Normalize(wheelAxis);
        Vector3 radial = Vector3.UnitY - axis * axis.Y;
        if (radial.LengthSquared() <= 1e-8f)
        {
            return -radiusM;
        }

        radial = Vector3.Normalize(radial);
        return -radiusM * MathF.Max(0f, radial.Y);
    }

    private RuntimeChassisMotion ResolveRuntimeChassisMotion(SimulationEntity entity)
    {
        float bodyLift = 0f;
        float frontDrop = 0f;
        float frontRaise = 0f;
        float rearFootRaise = 0f;
        float rearFootReach = 0f;
        bool mecanum =
            string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase);
        bool balanceInfantry =
            string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            && entity.ChassisSubtype.Contains("balance", StringComparison.OrdinalIgnoreCase);

        if (entity.TraversalActive)
        {
            float progress = Math.Clamp((float)entity.TraversalProgress, 0f, 1f);
            static float Blend(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
            bool heavyChassis =
                string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase);
            if (balanceInfantry)
            {
                float stepHeight = Math.Clamp(
                    (float)Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0f,
                    0.22f);
                if (progress < 0.30f)
                {
                    float ratio = progress / 0.30f;
                    bodyLift = Blend(0.15f, 0.22f + stepHeight * 0.18f, ratio);
                    frontDrop = 0.04f * ratio;
                    frontRaise = 0.03f * ratio;
                    rearFootRaise = -(0.04f + stepHeight * 0.30f) * ratio;
                    rearFootReach = -(0.03f + stepHeight * 0.16f) * ratio;
                }
                else if (progress < 0.72f)
                {
                    float ratio = (progress - 0.30f) / 0.42f;
                    bodyLift = Blend(0.22f + stepHeight * 0.18f, 0.18f + stepHeight * 0.12f, ratio);
                    frontDrop = Blend(0.04f, 0.07f, ratio);
                    frontRaise = Blend(0.03f, 0.06f, ratio);
                    rearFootRaise = -Blend(0.04f + stepHeight * 0.30f, 0.12f + stepHeight * 0.38f, ratio);
                    rearFootReach = -Blend(0.03f + stepHeight * 0.16f, 0.08f + stepHeight * 0.20f, ratio);
                }
                else
                {
                    float ratio = (progress - 0.72f) / 0.28f;
                    bodyLift = (0.18f + stepHeight * 0.12f) * (1f - ratio);
                    frontDrop = 0.07f * (1f - ratio);
                    frontRaise = 0.06f * (1f - ratio);
                    rearFootRaise = -(0.12f + stepHeight * 0.38f) * (1f - ratio);
                    rearFootReach = -(0.08f + stepHeight * 0.20f) * (1f - ratio);
                }
            }
            else if (heavyChassis)
            {
                float stepHeight = Math.Clamp(
                    (float)Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0f,
                    0.45f);
                float liftPeak = 0.11f + stepHeight * 0.42f;
                float reachPeak = 0.06f + stepHeight * 0.16f;
                if (progress < 0.20f)
                {
                    float ratio = progress / 0.20f;
                    rearFootRaise = -liftPeak * 0.30f * ratio;
                    rearFootReach = -reachPeak * 0.30f * ratio;
                    bodyLift = (0.02f + stepHeight * 0.10f) * ratio;
                    frontDrop = 0.02f * ratio;
                    frontRaise = 0.015f * ratio;
                }
                else if (progress < 0.56f)
                {
                    float ratio = (progress - 0.20f) / 0.36f;
                    rearFootRaise = -liftPeak * Blend(0.30f, 1.08f, ratio);
                    rearFootReach = -reachPeak * Blend(0.30f, 1.00f, ratio);
                    bodyLift = Blend(0.02f + stepHeight * 0.10f, 0.09f + stepHeight * 0.18f, ratio);
                    frontDrop = Blend(0.02f, 0.08f, ratio);
                    frontRaise = Blend(0.015f, 0.05f, ratio);
                }
                else if (progress < 0.84f)
                {
                    float ratio = (progress - 0.56f) / 0.28f;
                    rearFootRaise = -liftPeak * Blend(1.08f, 0.62f, ratio);
                    rearFootReach = -reachPeak * Blend(1.00f, 0.34f, ratio);
                    bodyLift = Blend(0.09f + stepHeight * 0.18f, 0.05f + stepHeight * 0.08f, ratio);
                    frontDrop = Blend(0.08f, 0.03f, ratio);
                    frontRaise = Blend(0.05f, 0.02f, ratio);
                }
                else
                {
                    float ratio = (progress - 0.84f) / 0.16f;
                    rearFootRaise = -liftPeak * 0.62f * (1f - ratio);
                    rearFootReach = -reachPeak * 0.34f * (1f - ratio);
                    bodyLift = (0.05f + stepHeight * 0.08f) * (1f - ratio);
                    frontDrop = 0.03f * (1f - ratio);
                    frontRaise = 0.02f * (1f - ratio);
                }
            }
            else if (progress < 0.4f)
            {
                float ratio = progress / 0.4f;
                float stepHeight = Math.Clamp(
                    (float)Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0f,
                    0.32f);
                frontDrop = 0.10f + 0.18f * ratio;
                frontRaise = 0.05f + 0.06f * ratio;
                rearFootRaise = -(0.10f + stepHeight * 0.24f) * ratio;
                rearFootReach = -(0.07f + stepHeight * 0.12f) * ratio;
                bodyLift = (0.06f + stepHeight * 0.10f) * ratio;
            }
            else if (progress < 0.7f)
            {
                float stepHeight = Math.Clamp(
                    (float)Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0f,
                    0.32f);
                frontDrop = 0.18f;
                frontRaise = 0.08f;
                rearFootRaise = -(0.13f + stepHeight * 0.28f);
                rearFootReach = -(0.10f + stepHeight * 0.12f);
                bodyLift = 0.09f + stepHeight * 0.12f;
            }
            else
            {
                float ratio = (progress - 0.7f) / 0.3f;
                float stepHeight = Math.Clamp(
                    (float)Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0f,
                    0.32f);
                frontDrop = 0.18f - 0.10f * ratio;
                frontRaise = 0.08f - 0.04f * ratio;
                rearFootRaise = -(0.13f + stepHeight * 0.28f) * (1f - ratio);
                rearFootReach = -(0.10f + stepHeight * 0.12f) * (1f - ratio);
                bodyLift = (0.09f + stepHeight * 0.12f) * (1f - ratio);
            }
        }
        else if (balanceInfantry && entity.JumpCrouchTimerSec > 1e-6)
        {
            float duration = Math.Max(1e-6f, (float)entity.JumpCrouchDurationSec);
            float progress = Math.Clamp(1f - (float)entity.JumpCrouchTimerSec / duration, 0f, 1f);
            float eased = progress * progress * (3f - 2f * progress);
            bodyLift = Math.Max(bodyLift, 0.015f + 0.020f * eased);
            frontDrop = Math.Max(frontDrop, 0.02f + 0.04f * eased);
            frontRaise = Math.Max(frontRaise, 0.02f + 0.03f * eased);
            rearFootRaise = Math.Min(rearFootRaise, -(0.05f + 0.05f * eased));
            rearFootReach = Math.Min(rearFootReach, -(0.03f + 0.04f * eased));
        }
        else if (balanceInfantry && entity.StepClimbPoseBlend > 1e-4)
        {
            float stepBlend = Math.Clamp((float)entity.StepClimbPoseBlend, 0f, 1f);
            float stepVelocity = Math.Clamp((float)entity.StepClimbPoseVelocity, -2.8f, 2.8f);
            float eased = stepBlend * stepBlend * (3f - 2f * stepBlend);
            float overshoot = MathF.Max(0f, stepVelocity) * 0.010f;
            float rebound = MathF.Max(0f, -stepVelocity) * 0.008f;
            bodyLift = Math.Max(bodyLift, 0.030f + 0.090f * eased + overshoot);
            frontDrop = Math.Max(frontDrop, 0.008f + 0.024f * eased + rebound * 0.4f);
            frontRaise = Math.Max(frontRaise, 0.012f + 0.040f * eased + overshoot * 0.8f);
            rearFootRaise = Math.Min(rearFootRaise, -(0.035f + 0.090f * eased + overshoot * 0.7f));
            rearFootReach = Math.Min(rearFootReach, -(0.015f + 0.040f * eased + overshoot * 0.35f));
        }

        if (entity.ChassisSupportsJump && entity.AirborneHeightM > 1e-4)
        {
            float jumpWave = Math.Clamp((float)(entity.AirborneHeightM / 0.70), 0f, 1f);
            bodyLift = Math.Max(bodyLift, 0.16f * jumpWave);
            frontDrop = Math.Max(frontDrop, 0.08f * jumpWave);
            frontRaise = Math.Max(frontRaise, 0.06f * jumpWave);
            if (balanceInfantry)
            {
                bool descending = entity.VerticalVelocityMps < -0.12;
                float tuckPhase = descending
                    ? Math.Clamp((float)((-entity.VerticalVelocityMps - 0.12) / 2.4), 0f, 1f)
                    : jumpWave;
                rearFootRaise = Math.Max(rearFootRaise, descending ? 0.08f + 0.05f * tuckPhase : 0.14f + 0.08f * tuckPhase);
                rearFootReach = Math.Max(rearFootReach, descending ? 0.04f + 0.03f * tuckPhase : 0.06f + 0.05f * tuckPhase);
            }
            else
            {
                rearFootRaise = Math.Min(rearFootRaise, -0.18f * jumpWave);
                rearFootReach = Math.Min(rearFootReach, -0.10f * jumpWave);
            }
        }

        float mecanumTranslationOscillation = ResolveMecanumTranslationVerticalOscillationM(entity);
        if (MathF.Abs(mecanumTranslationOscillation) > 1e-5f)
        {
            float visualSuppression = mecanum && !balanceInfantry ? 0.76f : 1f;
            float oscillation = mecanumTranslationOscillation * visualSuppression;
            bodyLift += oscillation;
            float oscillationAbs = MathF.Abs(oscillation);
            frontDrop = MathF.Max(frontDrop, oscillationAbs * 0.18f);
            frontRaise = MathF.Max(frontRaise, oscillationAbs * 0.13f);
            rearFootRaise = MathF.Min(rearFootRaise, -oscillationAbs * 0.14f);
        }

        bool bindFirstPersonMountedView = ShouldBindSelectedFirstPersonViewToGimbal(entity);
        float compression = ResolveSuspensionVisualCompressionM(entity);
        if (MathF.Abs(compression) > 1e-5f)
        {
            float visualCompression = mecanum && !balanceInfantry ? compression * 0.82f : compression;
            bodyLift -= visualCompression;
            if (visualCompression > 0f)
            {
                frontDrop = Math.Max(frontDrop, visualCompression * 1.55f);
                frontRaise = Math.Max(frontRaise, visualCompression * 0.72f);
                rearFootRaise = Math.Min(rearFootRaise, -visualCompression * 1.38f);
            }
            else
            {
                float rebound = -visualCompression;
                frontRaise = Math.Max(frontRaise, rebound * 0.66f);
                rearFootRaise = MathF.Min(rearFootRaise, -rebound * 0.56f);
            }
        }

        float impactJolt = ResolveChassisImpactVisualJoltM(entity);
        if (MathF.Abs(impactJolt) > 1e-5f)
        {
            float visualImpactJolt = mecanum && !balanceInfantry ? impactJolt * 0.78f : impactJolt;
            if (bindFirstPersonMountedView)
            {
                visualImpactJolt *= 0.62f;
            }

            float impactAbs = MathF.Abs(visualImpactJolt);
            bodyLift += visualImpactJolt * 0.34f;
            frontDrop = MathF.Max(frontDrop, impactAbs * 0.68f);
            frontRaise = MathF.Max(frontRaise, impactAbs * 0.42f);
            rearFootRaise = MathF.Min(rearFootRaise, -impactAbs * 0.56f);
        }

        float longitudinalOffset = 0f;
        float lateralOffset = 0f;
        double motionPhase = _host.World.GameTimeSec * Math.PI * 2.0;
        if (mecanum && !balanceInfantry && entity.AirborneHeightM <= 1e-4 && !entity.TraversalActive)
        {
            double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
            double observedMagnitude = Math.Abs(entity.ObservedVelocityXWorldPerSec) + Math.Abs(entity.ObservedVelocityYWorldPerSec);
            double velocityX = observedMagnitude > 1e-4 ? entity.ObservedVelocityXWorldPerSec : entity.VelocityXWorldPerSec;
            double velocityY = observedMagnitude > 1e-4 ? entity.ObservedVelocityYWorldPerSec : entity.VelocityYWorldPerSec;
            double speedMps = Math.Sqrt(velocityX * velocityX + velocityY * velocityY) * metersPerWorldUnit;
            double inputMagnitude = Math.Min(1.0, Math.Sqrt(entity.MoveInputForward * entity.MoveInputForward + entity.MoveInputRight * entity.MoveInputRight));
            float translationMotion = (float)Math.Clamp(Math.Max(speedMps / 4.6, inputMagnitude), 0.0, 1.0);
            if (translationMotion > 0.03f)
            {
                float phase = (float)(motionPhase * (7.2 + translationMotion * 4.6) + ResolveCameraVibrationPhase(entity.Id) * 0.83f);
                longitudinalOffset = (
                    MathF.Sin(phase * 0.92f + 0.35f) * 0.0011f
                    + MathF.Sin(phase * 1.61f + 1.10f) * 0.00034f) * translationMotion;
                lateralOffset = (
                    MathF.Sin(phase + 0.78f) * 0.0014f
                    + MathF.Sin(phase * 1.47f + 2.05f) * 0.00042f) * translationMotion;
            }
        }
        else if (balanceInfantry && entity.AirborneHeightM <= 1e-4)
        {
            float accelerationResponse = Math.Clamp(
                (float)(Math.Abs(entity.ChassisPitchVelocityDegPerSec) + Math.Abs(entity.ChassisRollVelocityDegPerSec)) / 42f,
                0f,
                1f);
            float stepPoseResponse = Math.Clamp(
                (float)(Math.Abs(entity.StepClimbPoseVelocity) * 0.42 + entity.StepClimbPoseBlend * 0.58),
                0f,
                1f);
            accelerationResponse = Math.Max(accelerationResponse, stepPoseResponse);
            if (accelerationResponse > 0.04f)
            {
                float phase = (float)(motionPhase * (5.8 + accelerationResponse * 5.6) + ResolveCameraVibrationPhase(entity.Id) * 0.58f);
                longitudinalOffset = MathF.Sin(phase * 0.86f + 0.40f) * 0.0010f * accelerationResponse;
                lateralOffset = MathF.Sin(phase * 1.19f + 1.45f) * 0.0013f * accelerationResponse;
            }
        }

        if (bindFirstPersonMountedView && entity.AirborneHeightM <= 1e-4 && !entity.TraversalActive)
        {
            float inertialResponse = Math.Clamp(
                (float)(Math.Abs(entity.ChassisPitchVelocityDegPerSec) + Math.Abs(entity.ChassisRollVelocityDegPerSec)) / 96f,
                0f,
                1f);
            if (inertialResponse > 0.025f)
            {
                float phase = (float)(_host.World.GameTimeSec * Math.PI * (7.0 + inertialResponse * 5.0)
                    + ResolveCameraVibrationPhase(entity.Id) * 0.91f);
                longitudinalOffset += MathF.Sin(phase + 0.35f) * 0.0022f * inertialResponse;
                lateralOffset += MathF.Sin(phase * 1.27f + 1.05f) * 0.0015f * inertialResponse;
                bodyLift += MathF.Sin(phase * 1.58f + 0.62f) * 0.0016f * inertialResponse;
            }
        }

        return new RuntimeChassisMotion(bodyLift, frontDrop, frontRaise, rearFootRaise, rearFootReach, longitudinalOffset, lateralOffset);
    }

    private Vector3 ResolveRuntimeChassisSceneOffset(SimulationEntity entity, RuntimeChassisMotion motion)
    {
        if (MathF.Abs(motion.LongitudinalOffsetM) <= 1e-6f && MathF.Abs(motion.LateralOffsetM) <= 1e-6f)
        {
            return Vector3.Zero;
        }

            float yaw = ResolveAppearanceYaw(entity);
        ResolveChassisAxes(yaw, entity.ChassisPitchDeg, entity.ChassisRollDeg, out Vector3 chassisForward, out Vector3 chassisRight, out _);
        return chassisForward * motion.LongitudinalOffsetM + chassisRight * motion.LateralOffsetM;
    }

    private float ResolveMecanumTranslationVerticalOscillationM(SimulationEntity entity)
    {
        bool balanceInfantry = IsBalanceInfantryLabel(entity);
        bool mecanum = string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase);
        if (!mecanum
            || balanceInfantry
            || entity.AirborneHeightM > 1e-4
            || entity.TraversalActive)
        {
            return 0f;
        }

        double metersPerWorldUnit = Math.Max(_host.World.MetersPerWorldUnit, 1e-6);
        double velocityX = Math.Abs(entity.ObservedVelocityXWorldPerSec) + Math.Abs(entity.ObservedVelocityYWorldPerSec) > 1e-4
            ? entity.ObservedVelocityXWorldPerSec
            : entity.VelocityXWorldPerSec;
        double velocityY = Math.Abs(entity.ObservedVelocityXWorldPerSec) + Math.Abs(entity.ObservedVelocityYWorldPerSec) > 1e-4
            ? entity.ObservedVelocityYWorldPerSec
            : entity.VelocityYWorldPerSec;
        double speedMps = Math.Sqrt(velocityX * velocityX + velocityY * velocityY) * metersPerWorldUnit;
        double inputMagnitude = Math.Min(
            1.0,
            Math.Sqrt(entity.MoveInputForward * entity.MoveInputForward + entity.MoveInputRight * entity.MoveInputRight));
        double motion = Math.Clamp(Math.Max(speedMps / 4.2, inputMagnitude), 0.0, 1.0);
        if (motion <= 0.025)
        {
            return 0f;
        }

        double strafeRatio = Math.Abs(entity.MoveInputRight)
            / Math.Max(0.12, Math.Abs(entity.MoveInputForward) + Math.Abs(entity.MoveInputRight));
        double amplitude = (0.00072 + 0.00188 * motion) * (1.0 + strafeRatio * 0.10);
        double frequencyHz = 8.0 + Math.Clamp(speedMps, 0.0, 5.0) * 1.18 + strafeRatio * 0.8;
        double phaseOffset = (Math.Abs(entity.Id.GetHashCode()) % 997) * 0.0063;
        double phase = _host.World.GameTimeSec * frequencyHz * Math.PI * 2.0 + phaseOffset;
        double value = Math.Sin(phase) * amplitude + Math.Sin(phase * 1.71 + 0.7) * amplitude * 0.18;
        return (float)Math.Clamp(value, -0.0030f, 0.0030f);
    }

    private bool ShouldBindSelectedFirstPersonViewToGimbal(SimulationEntity entity)
        => _firstPersonView
            && _host.SelectedEntity is { } selected
            && string.Equals(selected.Id, entity.Id, StringComparison.OrdinalIgnoreCase);

    private static float ResolveSuspensionVisualCompressionM(SimulationEntity entity)
    {
        float compression = (float)Math.Clamp(entity.LandingCompressionM, -0.018, 0.040);
        double duration = Math.Max(1e-6, entity.JumpCrouchDurationSec);
        if (entity.JumpCrouchTimerSec > 1e-6 && duration > 1e-6)
        {
            double progress = Math.Clamp(1.0 - entity.JumpCrouchTimerSec / duration, 0.0, 1.0);
            double eased = progress * progress * (3.0 - 2.0 * progress);
            compression += (float)(0.020 * eased);
        }

        return Math.Clamp(compression, -0.018f, 0.040f);
    }

    private static float ResolveChassisImpactVisualJoltM(SimulationEntity entity)
    {
        if (entity.ChassisImpactShakeTimerSec <= 1e-5
            || entity.ChassisImpactShakeDurationSec <= 1e-5
            || entity.ChassisImpactShakeIntensity <= 1e-5)
        {
            return 0f;
        }

        double remaining = Math.Clamp(entity.ChassisImpactShakeTimerSec / entity.ChassisImpactShakeDurationSec, 0.0, 1.0);
        double age = 1.0 - remaining;
        double envelope = Math.Pow(remaining, 0.72);
        double wave = Math.Sin(age * Math.PI * 7.0);
        return Math.Clamp((float)(wave * envelope * entity.ChassisImpactShakeIntensity * 0.026), -0.024f, 0.024f);
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

    private static float ResolveAppearanceYaw(SimulationEntity entity)
        => -(float)(entity.AngleDeg * Math.PI / 180.0);

    private static float ResolveAppearanceTurretYaw(SimulationEntity entity)
        => (float)(entity.TurretYawDeg * Math.PI / 180.0);
}
