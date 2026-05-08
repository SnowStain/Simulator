using System.Numerics;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal readonly record struct EntityCollisionPoseContext(
    double MetersPerWorldUnit,
    double CenterXWorld,
    double CenterYWorld,
    Func<double, double, double, double> SampleClosestSurfaceHeightM);

internal readonly record struct EntityCollisionPart(
    string Id,
    double LocalX,
    double LocalY,
    double LengthM,
    double WidthM,
    double MinHeightM,
    double HeightM,
    double VisualRadiusM = 0.0,
    double VisualThicknessM = 0.0,
    double LocalYawDeg = 0.0);

internal static class EntityCollisionModel
{
    private const double CollisionInflationM = 0.006;
    private static readonly object PartCacheLock = new();
    private static readonly Dictionary<string, CachedCollisionParts> PartCache = new(StringComparer.Ordinal);
    private const int MaxCachedCollisionPartSets = 96;

    public static IReadOnlyList<EntityCollisionPart> ResolveParts(SimulationEntity entity)
        => ResolveParts(entity, null);

    public static IReadOnlyList<EntityCollisionPart> ResolveParts(
        SimulationEntity entity,
        EntityCollisionPoseContext? poseContext)
    {
        if (poseContext is null)
        {
            string signature = BuildPartCacheSignature(entity);
            lock (PartCacheLock)
            {
                if (PartCache.TryGetValue(entity.Id, out CachedCollisionParts cached)
                    && string.Equals(cached.Signature, signature, StringComparison.Ordinal))
                {
                    return cached.Parts;
                }
            }

            IReadOnlyList<EntityCollisionPart> resolved = ResolvePartsUncached(entity, poseContext);
            lock (PartCacheLock)
            {
                if (PartCache.Count >= MaxCachedCollisionPartSets && !PartCache.ContainsKey(entity.Id))
                {
                    PartCache.Clear();
                }

                PartCache[entity.Id] = new CachedCollisionParts(signature, resolved);
            }

            return resolved;
        }

        return ResolvePartsUncached(entity, poseContext);
    }

    public static IReadOnlyList<EntityCollisionPart> ResolveGroundSupportParts(
        SimulationEntity entity,
        EntityCollisionPoseContext poseContext)
    {
        double planarInset = ResolvePlanarCollisionInsetM(entity);
        double bodyLength = Math.Max(0.12, entity.BodyLengthM);
        double bodyWidth = Math.Max(0.12, entity.BodyWidthM * Math.Max(0.2, entity.BodyRenderWidthScale));
        var parts = new List<EntityCollisionPart>(Math.Max(2, entity.WheelOffsetsM.Count + 2));
        AddWheelAndLegParts(entity, parts, bodyLength, bodyWidth, planarInset, poseContext);
        AddClimbAssistParts(entity, parts, bodyLength, bodyWidth, planarInset);
        return InflateParts(parts);
    }

    private static IReadOnlyList<EntityCollisionPart> ResolvePartsUncached(
        SimulationEntity entity,
        EntityCollisionPoseContext? poseContext)
    {
        if (SimulationCombatMath.IsStructure(entity))
        {
            return InflateParts(ResolveStructureParts(entity));
        }

        var parts = new List<EntityCollisionPart>(Math.Max(3, entity.WheelOffsetsM.Count + 2));
        double planarInset = ResolvePlanarCollisionInsetM(entity);
        double bodyLength = Math.Max(0.12, entity.BodyLengthM);
        double bodyWidth = Math.Max(0.12, entity.BodyWidthM * Math.Max(0.2, entity.BodyRenderWidthScale));
        double bodyMinHeight = Math.Max(0.0, Math.Min(entity.BodyClearanceM, 0.18));
        double bodyHeight = Math.Max(0.12, entity.BodyHeightM + 0.03);
        double bodyMaxHeight = bodyMinHeight + bodyHeight;
        if (string.Equals(entity.ChassisSubtype, "balance_legged", StringComparison.OrdinalIgnoreCase)
            || entity.ChassisSubtype.Contains("balance", StringComparison.OrdinalIgnoreCase))
        {
            bodyMinHeight = Math.Max(bodyMinHeight, Math.Clamp(entity.WheelRadiusM * 0.82, 0.050, 0.135));
            bodyHeight = Math.Max(0.11, bodyMaxHeight - bodyMinHeight);
        }
        if (UsesOmniWheelSupport(entity))
        {
            bodyMinHeight = Math.Max(
                bodyMinHeight,
                Math.Clamp(entity.WheelRadiusM * 0.92, 0.045, 0.115));
            bodyHeight = Math.Max(0.08, bodyMaxHeight - bodyMinHeight);
        }
        AddChassisBodyParts(
            parts,
            entity,
            bodyLength,
            bodyWidth,
            bodyMinHeight,
            bodyHeight,
            planarInset);

        if (entity.GimbalLengthM > 0.04 && entity.GimbalWidthM > 0.04)
        {
            parts.Add(new EntityCollisionPart(
                "gimbal",
                entity.GimbalOffsetXM,
                entity.GimbalOffsetYM,
                ShrinkPlanarDimension(Math.Max(0.08, entity.GimbalLengthM) + 0.012, planarInset * 0.85, 0.06),
                ShrinkPlanarDimension(Math.Max(0.08, entity.GimbalWidthM) + 0.012, planarInset * 0.85, 0.06),
                bodyMinHeight + Math.Max(0.02, entity.BodyHeightM * 0.65),
                Math.Max(0.06, entity.GimbalBodyHeightM + entity.GimbalMountHeightM),
                LocalYawDeg: NormalizeRelativeYawDeg(entity.TurretYawDeg - entity.AngleDeg)));
        }

        AddWheelAndLegParts(entity, parts, bodyLength, bodyWidth, planarInset, poseContext);
        AddClimbAssistParts(entity, parts, bodyLength, bodyWidth, planarInset);
        return InflateParts(parts);
    }

    private static string BuildPartCacheSignature(SimulationEntity entity)
    {
        var hash = new HashCode();
        Add(entity.BodyLengthM);
        Add(entity.BodyWidthM);
        Add(entity.BodyHeightM);
        Add(entity.BodyClearanceM);
        Add(entity.BodyRenderWidthScale);
        Add(entity.WheelRadiusM);
        Add(entity.RearLegWheelRadiusM);
        Add(entity.GimbalLengthM);
        Add(entity.GimbalWidthM);
        Add(entity.GimbalBodyHeightM);
        Add(entity.GimbalMountHeightM);
        Add(entity.GimbalOffsetXM);
        Add(entity.GimbalOffsetYM);
        Add(entity.FrontClimbAssistTopLengthM);
        Add(entity.FrontClimbAssistBottomLengthM);
        Add(entity.FrontClimbAssistPlateWidthM);
        Add(entity.FrontClimbAssistPlateHeightM);
        Add(entity.FrontClimbAssistForwardOffsetM);
        Add(entity.FrontClimbAssistInnerOffsetM);
        Add(entity.RearClimbAssistUpperLengthM);
        Add(entity.RearClimbAssistLowerLengthM);
        Add(entity.RearClimbAssistUpperWidthM);
        Add(entity.RearClimbAssistUpperHeightM);
        Add(entity.RearClimbAssistLowerWidthM);
        Add(entity.RearClimbAssistLowerHeightM);
        Add(entity.RearClimbAssistMountOffsetXM);
        Add(entity.RearClimbAssistMountHeightM);
        Add(entity.RearClimbAssistInnerOffsetM);
        Add(entity.RearClimbAssistUpperPairGapM);
        Add(entity.RearClimbAssistHingeRadiusM);
        Add(entity.RearClimbAssistKneeMinDeg);
        Add(entity.RearClimbAssistKneeMaxDeg);
        Add(entity.StructureBaseLiftM);
        Add(entity.StructureGroundClearanceM);
        Add(entity.StructureBaseHeightM);
        Add(entity.StructureFrameDepthM);
        Add(entity.StructureCantileverPairGapM);
        Add(entity.StructureCantileverLengthM);
        foreach ((double x, double y) in entity.WheelOffsetsM)
        {
            Add(x);
            Add(y);
        }

        return string.Join(
            '|',
            entity.EntityType,
            entity.RoleKey,
            entity.BodyShape,
            entity.WheelStyle,
            entity.FrontClimbAssistStyle,
            entity.RearClimbAssistStyle,
            entity.RearClimbAssistKneeDirection,
            entity.WheelOffsetsM.Count,
            hash.ToHashCode());

        void Add(double value) => hash.Add((long)Math.Round(value * 10000.0));
    }

    private static IReadOnlyList<EntityCollisionPart> InflateParts(IReadOnlyList<EntityCollisionPart> parts)
    {
        var inflated = new EntityCollisionPart[parts.Count];
        for (int index = 0; index < parts.Count; index++)
        {
            EntityCollisionPart part = parts[index];
            inflated[index] = new EntityCollisionPart(
                part.Id,
                part.LocalX,
                part.LocalY,
                part.LengthM + CollisionInflationM * 2.0,
                part.WidthM + CollisionInflationM * 2.0,
                Math.Max(0.0, part.MinHeightM - CollisionInflationM),
                part.HeightM + CollisionInflationM * 2.0,
                part.VisualRadiusM,
                part.VisualThicknessM,
                part.LocalYawDeg);
        }

        return inflated;
    }

    public static (double HalfLengthM, double HalfWidthM) ResolveConservativeHalfExtents(SimulationEntity entity)
    {
        double halfLength = 0.08;
        double halfWidth = 0.08;

        void Include(double localX, double localY, double length, double width)
        {
            halfLength = Math.Max(halfLength, Math.Abs(localX) + Math.Max(0.0, length) * 0.5);
            halfWidth = Math.Max(halfWidth, Math.Abs(localY) + Math.Max(0.0, width) * 0.5);
        }

        foreach (EntityCollisionPart part in ResolveParts(entity))
        {
            Include(part.LocalX, part.LocalY, part.LengthM, part.WidthM);
        }

        return (halfLength + 0.010, halfWidth + 0.010);
    }

    private static IReadOnlyList<EntityCollisionPart> ResolveStructureParts(SimulationEntity entity)
    {
        double baseLength = Math.Max(0.18, entity.BodyLengthM);
        double baseWidth = Math.Max(0.18, entity.BodyWidthM * Math.Max(0.2, entity.BodyRenderWidthScale));

        var parts = new List<EntityCollisionPart>(3)
        {
            new(
                "structure_base",
                0.0,
                0.0,
                baseLength + 0.020,
                baseWidth + 0.020,
                Math.Max(0.0, entity.StructureGroundClearanceM),
                Math.Max(0.24, entity.StructureBaseHeightM + entity.BodyHeightM * 0.45)),
        };

        if (string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            double armGap = Math.Max(baseWidth * 0.5, entity.StructureCantileverPairGapM * 0.5);
            double armLength = Math.Max(0.12, entity.StructureCantileverLengthM);
            double armWidth = Math.Max(0.04, entity.StructureFrameDepthM);
            parts.Add(new EntityCollisionPart("outpost_left_arm", 0.0, -armGap, armLength, armWidth, 0.45, 1.10));
            parts.Add(new EntityCollisionPart("outpost_right_arm", 0.0, armGap, armLength, armWidth, 0.45, 1.10));
        }

        return parts;
    }

    private static void AddWheelAndLegParts(
        SimulationEntity entity,
        List<EntityCollisionPart> parts,
        double bodyLength,
        double bodyWidth,
        double planarInset,
        EntityCollisionPoseContext? poseContext)
    {
        double wheelRadius = Math.Clamp(entity.WheelRadiusM, 0.03, 0.24);
        double rearLegWheelRadius = ResolveRearLegWheelRadiusM(entity, wheelRadius);
        double wheelWidth = ResolveWheelCollisionWidthM(wheelRadius);
        double rearLegWheelWidth = ResolveWheelCollisionWidthM(rearLegWheelRadius);
        IReadOnlyList<(double X, double Y)> wheelOffsets = entity.WheelOffsetsM;
        bool dynamicRearLegCollision =
            string.Equals(entity.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase);
        BalanceLegCollisionPose? leftRearPose = dynamicRearLegCollision
            && HasRearLegSide(entity, wheelOffsets, -1.0)
                ? ResolveBalanceLegCollisionPose(entity, wheelOffsets, bodyLength * 0.5, bodyWidth * 0.5, -1.0, rearLegWheelRadius, poseContext)
                : null;
        BalanceLegCollisionPose? rightRearPose = dynamicRearLegCollision
            && HasRearLegSide(entity, wheelOffsets, 1.0)
                ? ResolveBalanceLegCollisionPose(entity, wheelOffsets, bodyLength * 0.5, bodyWidth * 0.5, 1.0, rearLegWheelRadius, poseContext)
                : null;

        for (int index = 0; index < wheelOffsets.Count; index++)
        {
            (double x, double y) = wheelOffsets[index];
            double wheelSide = y * Math.Max(0.2, entity.BodyRenderWidthScale);
            if (dynamicRearLegCollision && IsRearLegWheelIndex(entity, wheelOffsets, index))
            {
                BalanceLegCollisionPose? rearPose = ResolveWheelSideSign(wheelOffsets, index, wheelSide) < 0.0
                    ? leftRearPose
                    : rightRearPose;
                if (rearPose is BalanceLegCollisionPose pose)
                {
                    AddWheelPart(parts, planarInset, pose.Foot.X, pose.SideOffset, pose.WheelRadius, rearLegWheelWidth, pose.Foot.Y);
                    continue;
                }
            }

            AddWheelPart(parts, planarInset, x, wheelSide, wheelRadius, wheelWidth, wheelRadius);
        }

        if (!dynamicRearLegCollision)
        {
            return;
        }

        if (wheelOffsets.Count == 0)
        {
            if (leftRearPose is BalanceLegCollisionPose leftFallbackPose)
            {
                AddWheelPart(parts, planarInset, leftFallbackPose.Foot.X, leftFallbackPose.SideOffset, leftFallbackPose.WheelRadius, rearLegWheelWidth, leftFallbackPose.Foot.Y);
            }

            if (rightRearPose is BalanceLegCollisionPose rightFallbackPose)
            {
                AddWheelPart(parts, planarInset, rightFallbackPose.Foot.X, rightFallbackPose.SideOffset, rightFallbackPose.WheelRadius, rearLegWheelWidth, rightFallbackPose.Foot.Y);
            }
        }

        if (leftRearPose is BalanceLegCollisionPose leftPose)
        {
            AddBalanceLegLinkParts(entity, parts, planarInset, leftPose, "left");
        }

        if (rightRearPose is BalanceLegCollisionPose rightPose)
        {
            AddBalanceLegLinkParts(entity, parts, planarInset, rightPose, "right");
        }
    }

    private static void AddChassisBodyParts(
        List<EntityCollisionPart> parts,
        SimulationEntity entity,
        double bodyLength,
        double bodyWidth,
        double bodyMinHeight,
        double bodyHeight,
        double planarInset)
    {
        double inflatedLength = bodyLength + 0.018;
        double inflatedWidth = bodyWidth + 0.018;
        bool balance = entity.ChassisSubtype.Contains("balance", StringComparison.OrdinalIgnoreCase);
        bool omni = string.Equals(entity.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase);
        bool mecanum = string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase);
        if (balance || omni)
        {
            double bodyShrink = balance ? planarInset * 0.80 : planarInset * 0.92;
            parts.Add(new EntityCollisionPart(
                "body",
                0.0,
                0.0,
                ShrinkPlanarDimension(bodyLength + 0.014, bodyShrink, 0.10),
                ShrinkPlanarDimension(bodyWidth + 0.014, bodyShrink, 0.10),
                bodyMinHeight,
                bodyHeight));
            return;
        }

        double widthBias = balance ? 0.76 : omni ? 0.86 : mecanum ? 0.82 : 0.92;
        double frontWidthScale = balance ? 0.58 : omni ? 0.70 : mecanum ? 0.66 : 0.76;
        double rearWidthScale = balance ? 0.62 : mecanum ? 0.70 : 0.78;
        double centerLength = inflatedLength * (balance ? 0.34 : mecanum ? 0.38 : 0.42);
        double endLength = inflatedLength * (balance ? 0.18 : mecanum ? 0.19 : 0.21);
        double centerHeight = Math.Max(0.10, bodyHeight);
        double upperHeightBias = balance ? 0.018 : 0.012;
        double frontMinHeight = bodyMinHeight + (balance ? 0.028 : 0.018);
        double rearMinHeight = bodyMinHeight + (balance ? 0.016 : 0.010);

        parts.Add(new EntityCollisionPart(
            "body_center",
            0.0,
            0.0,
            Math.Max(0.10, ShrinkPlanarDimension(centerLength, planarInset * 0.35, 0.10)),
            Math.Max(0.08, ShrinkPlanarDimension(inflatedWidth * widthBias, planarInset * 0.25, 0.08)),
            bodyMinHeight,
            centerHeight));

        parts.Add(new EntityCollisionPart(
            "body_front",
            inflatedLength * 0.24,
            0.0,
            Math.Max(0.08, ShrinkPlanarDimension(endLength, planarInset * 0.30, 0.08)),
            Math.Max(0.06, ShrinkPlanarDimension(inflatedWidth * frontWidthScale, planarInset * 0.25, 0.06)),
            Math.Max(0.0, frontMinHeight),
            Math.Max(0.08, centerHeight - upperHeightBias)));

        parts.Add(new EntityCollisionPart(
            "body_rear",
            -inflatedLength * 0.24,
            0.0,
            Math.Max(0.08, ShrinkPlanarDimension(endLength, planarInset * 0.30, 0.08)),
            Math.Max(0.06, ShrinkPlanarDimension(inflatedWidth * rearWidthScale, planarInset * 0.25, 0.06)),
            Math.Max(0.0, rearMinHeight),
            Math.Max(0.08, centerHeight - upperHeightBias * 1.2)));

        double cheekLength = inflatedLength * (balance ? 0.13 : mecanum ? 0.14 : 0.16);
        double cheekWidth = inflatedWidth * (balance ? 0.11 : mecanum ? 0.13 : 0.16);
        double cheekOffsetX = inflatedLength * (balance ? 0.16 : 0.20);
        double cheekOffsetY = inflatedWidth * (balance ? 0.29 : mecanum ? 0.32 : 0.36);
        double cheekMinHeight = bodyMinHeight + Math.Max(0.015, upperHeightBias * 0.75);
        double cheekHeight = Math.Max(0.08, centerHeight - upperHeightBias * 1.3);
        parts.Add(new EntityCollisionPart(
            "body_left_cheek",
            cheekOffsetX,
            -cheekOffsetY,
            Math.Max(0.06, ShrinkPlanarDimension(cheekLength, planarInset * 0.30, 0.06)),
            Math.Max(0.04, ShrinkPlanarDimension(cheekWidth, planarInset * 0.20, 0.04)),
            Math.Max(0.0, cheekMinHeight),
            cheekHeight));
        parts.Add(new EntityCollisionPart(
            "body_right_cheek",
            cheekOffsetX,
            cheekOffsetY,
            Math.Max(0.06, ShrinkPlanarDimension(cheekLength, planarInset * 0.30, 0.06)),
            Math.Max(0.04, ShrinkPlanarDimension(cheekWidth, planarInset * 0.20, 0.04)),
            Math.Max(0.0, cheekMinHeight),
            cheekHeight));
    }

    private static void AddClimbAssistParts(
        SimulationEntity entity,
        List<EntityCollisionPart> parts,
        double bodyLength,
        double bodyWidth,
        double planarInset)
    {
        if (!string.Equals(entity.FrontClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            double plateLength = Math.Max(entity.FrontClimbAssistTopLengthM, entity.FrontClimbAssistBottomLengthM);
            double plateWidth = Math.Max(0.018, entity.FrontClimbAssistPlateWidthM);
            double forward = bodyLength * 0.5 + entity.FrontClimbAssistForwardOffsetM + plateLength * 0.5;
            double side = Math.Max(bodyWidth * 0.30, bodyWidth * 0.5 - entity.FrontClimbAssistInnerOffsetM);
            double frontHeight = Math.Max(0.08, entity.FrontClimbAssistPlateHeightM);
            parts.Add(new EntityCollisionPart(
                "front_left_climb",
                forward,
                -side,
                ShrinkPlanarDimension(plateLength, planarInset * 0.85, 0.04),
                ShrinkPlanarDimension(plateWidth, planarInset, 0.012),
                0.0,
                frontHeight));
            parts.Add(new EntityCollisionPart(
                "front_right_climb",
                forward,
                side,
                ShrinkPlanarDimension(plateLength, planarInset * 0.85, 0.04),
                ShrinkPlanarDimension(plateWidth, planarInset, 0.012),
                0.0,
                frontHeight));
        }

        if (string.Equals(entity.RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        double lowerLength = Math.Max(0.04, entity.RearClimbAssistLowerLengthM);
        double lowerWidth = Math.Max(0.018, entity.RearClimbAssistLowerWidthM);
        double rearForward = -bodyLength * 0.5 + entity.RearClimbAssistMountOffsetXM - lowerLength * 0.20;
        double rearSide = Math.Max(bodyWidth * 0.30, bodyWidth * 0.5 - entity.RearClimbAssistInnerOffsetM);
        double rearHeight = Math.Max(0.05, entity.RearClimbAssistLowerHeightM + entity.RearClimbAssistMountHeightM * 0.35);
        parts.Add(new EntityCollisionPart(
            "rear_left_climb",
            rearForward,
            -rearSide,
            ShrinkPlanarDimension(lowerLength, planarInset * 0.85, 0.03),
            ShrinkPlanarDimension(lowerWidth, planarInset, 0.012),
            0.0,
            rearHeight));
        parts.Add(new EntityCollisionPart(
            "rear_right_climb",
            rearForward,
            rearSide,
            ShrinkPlanarDimension(lowerLength, planarInset * 0.85, 0.03),
            ShrinkPlanarDimension(lowerWidth, planarInset, 0.012),
            0.0,
            rearHeight));
    }

    private static double ResolvePlanarCollisionInsetM(SimulationEntity entity)
    {
        _ = entity;
        return 0.0;
    }

    private static double ShrinkPlanarDimension(double sizeM, double insetM, double minSizeM)
    {
        if (insetM <= 1e-6)
        {
            return Math.Max(minSizeM, sizeM);
        }

        return Math.Max(minSizeM, sizeM - insetM * 2.0);
    }

    private static double Lerp(double a, double b, double t)
        => a + (b - a) * Math.Clamp(t, 0.0, 1.0);

    private static double NormalizeRelativeYawDeg(double value)
    {
        double normalized = value % 360.0;
        if (normalized > 180.0)
        {
            normalized -= 360.0;
        }
        else if (normalized < -180.0)
        {
            normalized += 360.0;
        }

        return normalized;
    }

    private static bool UsesOmniWheelSupport(SimulationEntity entity)
        => string.Equals(entity.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase);

    private static double ResolveRearLegWheelRadiusM(SimulationEntity entity, double fallbackRadiusM)
    {
        double configured = entity.RearLegWheelRadiusM > 1e-6
            ? entity.RearLegWheelRadiusM
            : fallbackRadiusM;
        return Math.Clamp(configured, 0.03, 0.32);
    }

    private static double ResolveWheelCollisionWidthM(double wheelRadiusM)
    {
        _ = wheelRadiusM;
        return 0.040;
    }

    private static void AddWheelPart(
        List<EntityCollisionPart> parts,
        double planarInset,
        double localX,
        double localY,
        double wheelRadius,
        double wheelWidth,
        double centerHeightM)
    {
        parts.Add(new EntityCollisionPart(
            "wheel",
            localX,
            localY,
            ShrinkPlanarDimension(wheelRadius * 2.0, planarInset * 0.75, 0.045),
            ShrinkPlanarDimension(wheelWidth, planarInset * 0.90, 0.02),
            Math.Max(0.0, centerHeightM - wheelRadius),
            wheelRadius * 2.0,
            wheelRadius,
            wheelWidth));
    }

    private static void AddBalanceLegLinkParts(
        SimulationEntity entity,
        List<EntityCollisionPart> parts,
        double planarInset,
        BalanceLegCollisionPose pose,
        string sideName)
    {
        double halfGap = Math.Max(0.02, entity.RearClimbAssistUpperPairGapM) * 0.5;
        Vector2 upperFront = new(pose.Anchor.X + (float)halfGap, pose.Anchor.Y);
        Vector2 upperRear = new(pose.Anchor.X - (float)halfGap, pose.Anchor.Y);
        Vector2 kneeFront = new(pose.Knee.X + (float)halfGap, pose.Knee.Y);
        Vector2 kneeRear = new(pose.Knee.X - (float)halfGap, pose.Knee.Y);
        AddBeamCollisionPart(
            parts,
            $"{sideName}_rear_upper_link_front",
            upperFront,
            kneeFront,
            pose.SideOffset,
            Math.Max(0.010, entity.RearClimbAssistUpperWidthM),
            Math.Max(0.010, entity.RearClimbAssistUpperHeightM),
            planarInset);
        AddBeamCollisionPart(
            parts,
            $"{sideName}_rear_upper_link_rear",
            upperRear,
            kneeRear,
            pose.SideOffset,
            Math.Max(0.010, entity.RearClimbAssistUpperWidthM),
            Math.Max(0.010, entity.RearClimbAssistUpperHeightM),
            planarInset);
        AddBeamCollisionPart(
            parts,
            $"{sideName}_rear_lower_link",
            pose.Knee,
            pose.Foot,
            pose.SideOffset,
            Math.Max(0.010, entity.RearClimbAssistLowerWidthM),
            Math.Max(0.010, entity.RearClimbAssistLowerHeightM),
            planarInset);
    }

    private static void AddBeamCollisionPart(
        List<EntityCollisionPart> parts,
        string id,
        Vector2 start,
        Vector2 end,
        double localY,
        double beamWidthM,
        double beamHeightM,
        double planarInset)
    {
        Vector2 delta = end - start;
        double linkLengthM = Math.Max(beamWidthM, delta.Length());
        double nodeDiameterM = Math.Max(0.012, Math.Max(beamWidthM, beamHeightM) * 1.18);
        int nodeCount = Math.Clamp((int)Math.Ceiling(linkLengthM / Math.Max(0.028, nodeDiameterM * 1.35)), 2, 9);
        for (int index = 0; index < nodeCount; index++)
        {
            double t = nodeCount == 1 ? 0.5 : index / Math.Max(1.0, nodeCount - 1.0);
            double localX = Lerp(start.X, end.X, t);
            double localHeight = Lerp(start.Y, end.Y, t);
            parts.Add(new EntityCollisionPart(
                $"{id}_node_{index}",
                localX,
                localY,
                ShrinkPlanarDimension(nodeDiameterM, planarInset * 0.45, 0.012),
                ShrinkPlanarDimension(nodeDiameterM, planarInset * 0.45, 0.010),
                Math.Max(0.0, localHeight - beamHeightM * 0.5),
                Math.Max(beamHeightM, nodeDiameterM),
                nodeDiameterM * 0.5,
                nodeDiameterM));
        }
    }

    private static BalanceLegCollisionPose ResolveBalanceLegCollisionPose(
        SimulationEntity entity,
        IReadOnlyList<(double X, double Y)> wheelOffsets,
        double halfLength,
        double halfWidth,
        double sideSign,
        double wheelRadius,
        EntityCollisionPoseContext? poseContext)
    {
        Vector2 anchor = ResolveRearLegAnchorPoint(entity);
        double sideOffset = ResolveRearLegSideOffset(entity, wheelOffsets, halfWidth, wheelRadius) * sideSign;
        Vector2 foot = ClampTwoLinkTargetPointM(
            anchor,
            new Vector2(
                (float)ResolveNominalRearLegFootX(entity, wheelOffsets, halfLength, sideSign),
                (float)wheelRadius),
            (float)Math.Max(0.03, entity.RearClimbAssistUpperLengthM),
            (float)Math.Max(0.03, entity.RearClimbAssistLowerLengthM),
            (float)entity.RearClimbAssistKneeMinDeg,
            (float)entity.RearClimbAssistKneeMaxDeg);
        if (poseContext is EntityCollisionPoseContext context
            && entity.AirborneHeightM <= 1e-4)
        {
            double yawRad = entity.AngleDeg * Math.PI / 180.0;
            double forwardX = Math.Cos(yawRad);
            double forwardY = Math.Sin(yawRad);
            double rightX = Math.Cos(yawRad + Math.PI * 0.5);
            double rightY = Math.Sin(yawRad + Math.PI * 0.5);
            double sampleX = context.CenterXWorld + (forwardX * foot.X + rightX * sideOffset) / Math.Max(context.MetersPerWorldUnit, 1e-6);
            double sampleY = context.CenterYWorld + (forwardY * foot.X + rightY * sideOffset) / Math.Max(context.MetersPerWorldUnit, 1e-6);
            double oppositeSampleX = context.CenterXWorld + (forwardX * foot.X - rightX * sideOffset) / Math.Max(context.MetersPerWorldUnit, 1e-6);
            double oppositeSampleY = context.CenterYWorld + (forwardY * foot.X - rightY * sideOffset) / Math.Max(context.MetersPerWorldUnit, 1e-6);
            double baseHeightM = entity.GroundHeightM + Math.Max(0.0, entity.AirborneHeightM);
            double terrainHeightM = context.SampleClosestSurfaceHeightM(sampleX, sampleY, baseHeightM + foot.Y - wheelRadius);
            double oppositeTerrainHeightM = context.SampleClosestSurfaceHeightM(oppositeSampleX, oppositeSampleY, baseHeightM + foot.Y - wheelRadius);
            double centerTerrainHeightM = context.SampleClosestSurfaceHeightM(context.CenterXWorld, context.CenterYWorld, baseHeightM);
            float targetFootHeightM = (float)(terrainHeightM + wheelRadius - baseHeightM);
            double terrainSpreadM = Math.Max(
                Math.Abs(terrainHeightM - oppositeTerrainHeightM),
                Math.Max(Math.Abs(terrainHeightM - centerTerrainHeightM), Math.Abs(oppositeTerrainHeightM - centerTerrainHeightM)));
            bool stableFlatFooting =
                terrainSpreadM <= 0.045
                && Math.Abs(entity.ChassisPitchDeg) <= 4.0
                && Math.Abs(entity.ChassisRollDeg) <= 4.0;
            if (stableFlatFooting || Math.Abs(targetFootHeightM - wheelRadius) <= 0.018)
            {
                targetFootHeightM = (float)wheelRadius;
            }

            float terrainDeltaM = targetFootHeightM - foot.Y;
            float terrainReachBiasM = stableFlatFooting
                ? 0f
                : Math.Clamp(-terrainDeltaM * 0.08f, -0.045f, 0.035f);
            foot = ClampTwoLinkTargetPointM(
                anchor,
                new Vector2(foot.X + terrainReachBiasM, targetFootHeightM),
                (float)Math.Max(0.03, entity.RearClimbAssistUpperLengthM),
                (float)Math.Max(0.03, entity.RearClimbAssistLowerLengthM),
                (float)entity.RearClimbAssistKneeMinDeg,
                (float)entity.RearClimbAssistKneeMaxDeg);
        }

        Vector2 knee = SelectBalanceLegJoint(
            anchor,
            foot,
            (float)Math.Max(0.03, entity.RearClimbAssistUpperLengthM),
            (float)Math.Max(0.03, entity.RearClimbAssistLowerLengthM),
            entity.RearClimbAssistKneeDirection);
        return new BalanceLegCollisionPose(anchor, knee, foot, sideOffset, wheelRadius);
    }

    private static Vector2 ResolveRearLegAnchorPoint(SimulationEntity entity)
    {
        double anchorX = -Math.Max(0.10, entity.BodyLengthM * 0.5) + entity.RearClimbAssistMountOffsetXM;
        double anchorY = Math.Max(0.01, entity.RearClimbAssistMountHeightM);
        return new Vector2((float)anchorX, (float)anchorY);
    }

    private static bool HasRearLegSide(
        SimulationEntity entity,
        IReadOnlyList<(double X, double Y)> wheelOffsets,
        double sideSign)
    {
        if (!HasRearLegMechanism(entity))
        {
            return false;
        }

        if (wheelOffsets.Count == 0 || wheelOffsets.Count <= 2)
        {
            return true;
        }

        foreach ((double x, double y) in wheelOffsets)
        {
            _ = x;
            if (Math.Abs(y) > 1e-4 && Math.Sign(y) == Math.Sign(sideSign))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRearLegWheelIndex(
        SimulationEntity entity,
        IReadOnlyList<(double X, double Y)> wheelOffsets,
        int index)
    {
        if (!HasRearLegMechanism(entity))
        {
            return false;
        }

        if (wheelOffsets.Count == 0)
        {
            return index >= 2;
        }

        if (string.Equals(entity.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase)
            && wheelOffsets.Count <= 2)
        {
            return true;
        }

        int leftRearIndex = -1;
        int rightRearIndex = -1;
        double leftMostX = double.PositiveInfinity;
        double rightMostX = double.PositiveInfinity;
        for (int i = 0; i < wheelOffsets.Count; i++)
        {
            (double x, double y) = wheelOffsets[i];
            if (y < 0.0 && x < leftMostX)
            {
                leftMostX = x;
                leftRearIndex = i;
            }

            if (y > 0.0 && x < rightMostX)
            {
                rightMostX = x;
                rightRearIndex = i;
            }
        }

        if (leftRearIndex >= 0 || rightRearIndex >= 0)
        {
            return index == leftRearIndex || index == rightRearIndex;
        }

        double minX = wheelOffsets.Min(offset => offset.X);
        return Math.Abs(wheelOffsets[index].X - minX) <= 1e-5;
    }

    private static bool HasRearLegMechanism(SimulationEntity entity)
        => !string.Equals(entity.RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase);

    private static double ResolveWheelSideSign(
        IReadOnlyList<(double X, double Y)> wheelOffsets,
        int index,
        double fallbackLocalY)
    {
        if (wheelOffsets.Count > 0)
        {
            double y = wheelOffsets[index].Y;
            if (Math.Abs(y) > 1e-4)
            {
                return y < 0.0 ? -1.0 : 1.0;
            }
        }

        if (Math.Abs(fallbackLocalY) > 1e-4)
        {
            return fallbackLocalY < 0.0 ? -1.0 : 1.0;
        }

        return index % 2 == 0 ? -1.0 : 1.0;
    }

    private static double ResolveRearLegSideOffset(
        SimulationEntity entity,
        IReadOnlyList<(double X, double Y)> wheelOffsets,
        double halfWidth,
        double wheelRadius)
    {
        double renderWidthScale = Math.Max(0.35, entity.BodyRenderWidthScale);
        double bodyHalfSide = Math.Max(0.05, entity.BodyWidthM * renderWidthScale * 0.5);
        double wheelOuter = wheelOffsets.Count > 0
            ? wheelOffsets.Max(offset => Math.Abs(offset.Y) * renderWidthScale)
            : Math.Max(0.05, halfWidth);
        double rawSideOffset = Math.Max(
            bodyHalfSide + wheelRadius * 0.28,
            wheelOuter + wheelRadius * 0.08);
        double armorThickness = Math.Max(0.012, Math.Max(0.005, entity.ArmorPlateGapM) * 0.75);
        double armorCenterSide = bodyHalfSide + Math.Max(0.005, entity.ArmorPlateGapM) + armorThickness * 1.35;
        double hingeInsideLimit = armorCenterSide - Math.Max(0.018, entity.RearClimbAssistHingeRadiusM * 1.35);
        double minSideOffset = bodyHalfSide + Math.Max(0.004, entity.RearClimbAssistHingeRadiusM * 0.30);
        double maxSideOffset = Math.Max(minSideOffset, hingeInsideLimit);
        return Math.Clamp(rawSideOffset, minSideOffset, maxSideOffset);
    }

    private static double ResolveNominalRearLegFootX(
        SimulationEntity entity,
        IReadOnlyList<(double X, double Y)> wheelOffsets,
        double halfLength,
        double sideSign)
    {
        double footX = -Math.Max(0.08, halfLength * 0.78);
        if (wheelOffsets.Count > 0)
        {
            double sideBias = sideSign < 0.0 ? -1.0 : 1.0;
            double sideFootX = double.PositiveInfinity;
            double allFootX = double.PositiveInfinity;
            foreach ((double x, double y) in wheelOffsets)
            {
                allFootX = Math.Min(allFootX, x);
                if (Math.Abs(y) > 1e-4 && Math.Sign(y) == Math.Sign(sideBias))
                {
                    sideFootX = Math.Min(sideFootX, x);
                }
            }

            footX = double.IsFinite(sideFootX) ? sideFootX : allFootX;
        }

        double anchorX = -Math.Max(0.10, entity.BodyLengthM * 0.5) + entity.RearClimbAssistMountOffsetXM;
        double rearwardClearance = Math.Max(0.02, entity.RearClimbAssistUpperLengthM * 0.14);
        return Math.Min(footX, anchorX - rearwardClearance);
    }

    private static Vector2 ClampTwoLinkTargetPointM(
        Vector2 anchor,
        Vector2 target,
        float upperLength,
        float lowerLength,
        float minAngleDeg,
        float maxAngleDeg)
    {
        upperLength = Math.Max(0.03f, upperLength);
        lowerLength = Math.Max(0.03f, lowerLength);
        Vector2 delta = target - anchor;
        float distance = delta.Length();
        if (distance <= 1e-6f)
        {
            return new Vector2(anchor.X, anchor.Y + Math.Max(0.001f, Math.Abs(upperLength - lowerLength)));
        }

        float clampedMinAngle = Math.Clamp(minAngleDeg, 5f, 175f);
        float clampedMaxAngle = Math.Clamp(Math.Max(clampedMinAngle, maxAngleDeg), 5f, 175f);
        float spanMin = SpanForAngleM(upperLength, lowerLength, clampedMinAngle);
        float spanMax = SpanForAngleM(upperLength, lowerLength, clampedMaxAngle);
        float low = Math.Max(Math.Abs(upperLength - lowerLength) + 1e-4f, MathF.Min(spanMin, spanMax));
        float high = MathF.Min(upperLength + lowerLength - 1e-4f, MathF.Max(spanMin, spanMax));
        float clampedDistance = Math.Clamp(distance, low, high);
        return anchor + delta / distance * clampedDistance;
    }

    private static float SpanForAngleM(float upperLength, float lowerLength, float angleDeg)
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

    private readonly record struct BalanceLegCollisionPose(
        Vector2 Anchor,
        Vector2 Knee,
        Vector2 Foot,
        double SideOffset,
        double WheelRadius);

    private readonly record struct CachedCollisionParts(
        string Signature,
        IReadOnlyList<EntityCollisionPart> Parts);
}
