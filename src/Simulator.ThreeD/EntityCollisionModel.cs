using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal readonly record struct EntityCollisionPart(
    string Id,
    double LocalX,
    double LocalY,
    double LengthM,
    double WidthM,
    double MinHeightM,
    double HeightM);

internal static class EntityCollisionModel
{
    private const double CollisionInflationM = 0.01;

    public static IReadOnlyList<EntityCollisionPart> ResolveParts(SimulationEntity entity)
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
        parts.Add(new EntityCollisionPart(
            "body",
            0.0,
            0.0,
            ShrinkPlanarDimension(bodyLength + 0.018, planarInset, 0.10),
            ShrinkPlanarDimension(bodyWidth + 0.018, planarInset, 0.10),
            bodyMinHeight,
            bodyHeight));

        if (entity.GimbalLengthM > 0.04 && entity.GimbalWidthM > 0.04)
        {
            parts.Add(new EntityCollisionPart(
                "gimbal",
                entity.GimbalOffsetXM,
                entity.GimbalOffsetYM,
                ShrinkPlanarDimension(Math.Max(0.08, entity.GimbalLengthM) + 0.012, planarInset * 0.85, 0.06),
                ShrinkPlanarDimension(Math.Max(0.08, entity.GimbalWidthM) + 0.012, planarInset * 0.85, 0.06),
                bodyMinHeight + Math.Max(0.02, entity.BodyHeightM * 0.65),
                Math.Max(0.06, entity.GimbalBodyHeightM + entity.GimbalMountHeightM)));
        }

        AddWheelAndLegParts(entity, parts, bodyLength, bodyWidth, planarInset);
        AddClimbAssistParts(entity, parts, bodyLength, bodyWidth, planarInset);
        return InflateParts(parts);
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
                part.HeightM + CollisionInflationM * 2.0);
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

        if (SimulationCombatMath.IsStructure(entity))
        {
            Include(
                0.0,
                0.0,
                Math.Max(0.18, entity.BodyLengthM) + 0.020,
                Math.Max(0.18, entity.BodyWidthM * Math.Max(0.2, entity.BodyRenderWidthScale)) + 0.020);
            if (string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
            {
                double armGap = Math.Max(entity.BodyWidthM * Math.Max(0.2, entity.BodyRenderWidthScale) * 0.25, entity.StructureCantileverPairGapM * 0.5);
                double armLength = Math.Max(0.12, entity.StructureCantileverLengthM);
                double armWidth = Math.Max(0.04, entity.StructureFrameDepthM);
                Include(0.0, -armGap, armLength, armWidth);
                Include(0.0, armGap, armLength, armWidth);
            }

            return (halfLength + 0.010, halfWidth + 0.010);
        }

        double bodyLength = Math.Max(0.12, entity.BodyLengthM);
        double bodyWidth = Math.Max(0.12, entity.BodyWidthM * Math.Max(0.2, entity.BodyRenderWidthScale));
        double planarInset = ResolvePlanarCollisionInsetM(entity);
        Include(
            0.0,
            0.0,
            ShrinkPlanarDimension(bodyLength + 0.018, planarInset, 0.10),
            ShrinkPlanarDimension(bodyWidth + 0.018, planarInset, 0.10));
        if (entity.GimbalLengthM > 0.04 && entity.GimbalWidthM > 0.04)
        {
            Include(
                entity.GimbalOffsetXM,
                entity.GimbalOffsetYM,
                ShrinkPlanarDimension(Math.Max(0.08, entity.GimbalLengthM) + 0.012, planarInset * 0.85, 0.06),
                ShrinkPlanarDimension(Math.Max(0.08, entity.GimbalWidthM) + 0.012, planarInset * 0.85, 0.06));
        }

        double wheelRadius = Math.Clamp(entity.WheelRadiusM, 0.03, 0.24);
        foreach ((double x, double y) in entity.WheelOffsetsM)
        {
            Include(
                x,
                y * Math.Max(0.2, entity.BodyRenderWidthScale),
                ShrinkPlanarDimension(wheelRadius * 2.0 + 0.012, planarInset * 0.75, 0.05),
                ShrinkPlanarDimension(0.055, planarInset * 0.90, 0.02));
        }

        if (entity.WheelOffsetsM.Count > 0
            && (string.Equals(entity.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase)))
        {
            double rearX = entity.WheelOffsetsM.Min(offset => offset.X);
            double sideExtent = entity.WheelOffsetsM.Max(offset => Math.Abs(offset.Y * Math.Max(0.2, entity.BodyRenderWidthScale)));
            double rearWheelRadius = Math.Clamp(wheelRadius * 1.24, 0.03, 0.32);
            double legLength = Math.Max(0.08, entity.RearClimbAssistLowerLengthM + entity.RearClimbAssistUpperLengthM * 0.36);
            double legWidth = Math.Max(0.028, entity.RearClimbAssistLowerWidthM + rearWheelRadius * 0.30);
            double legX = Math.Min(-bodyLength * 0.48, rearX - legLength * 0.20);
            double legSide = Math.Max(bodyWidth * 0.48, sideExtent);
            Include(
                legX,
                -legSide,
                ShrinkPlanarDimension(legLength, planarInset * 0.75, 0.05),
                ShrinkPlanarDimension(legWidth, planarInset, 0.02));
            Include(
                legX,
                legSide,
                ShrinkPlanarDimension(legLength, planarInset * 0.75, 0.05),
                ShrinkPlanarDimension(legWidth, planarInset, 0.02));
        }

        if (!string.Equals(entity.FrontClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            double plateLength = Math.Max(entity.FrontClimbAssistTopLengthM, entity.FrontClimbAssistBottomLengthM);
            double forward = bodyLength * 0.5 + entity.FrontClimbAssistForwardOffsetM + plateLength * 0.5;
            double frontSide = Math.Max(bodyWidth * 0.30, bodyWidth * 0.5 - entity.FrontClimbAssistInnerOffsetM);
            Include(
                forward,
                -frontSide,
                ShrinkPlanarDimension(plateLength, planarInset * 0.85, 0.04),
                ShrinkPlanarDimension(Math.Max(0.018, entity.FrontClimbAssistPlateWidthM), planarInset, 0.012));
            Include(
                forward,
                frontSide,
                ShrinkPlanarDimension(plateLength, planarInset * 0.85, 0.04),
                ShrinkPlanarDimension(Math.Max(0.018, entity.FrontClimbAssistPlateWidthM), planarInset, 0.012));
        }

        if (!string.Equals(entity.RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase))
        {
            double lowerLength = Math.Max(0.04, entity.RearClimbAssistLowerLengthM);
            double rearForward = -bodyLength * 0.5 + entity.RearClimbAssistMountOffsetXM - lowerLength * 0.20;
            double rearSide = Math.Max(bodyWidth * 0.30, bodyWidth * 0.5 - entity.RearClimbAssistInnerOffsetM);
            Include(
                rearForward,
                -rearSide,
                ShrinkPlanarDimension(lowerLength, planarInset * 0.85, 0.03),
                ShrinkPlanarDimension(Math.Max(0.018, entity.RearClimbAssistLowerWidthM), planarInset, 0.012));
            Include(
                rearForward,
                rearSide,
                ShrinkPlanarDimension(lowerLength, planarInset * 0.85, 0.03),
                ShrinkPlanarDimension(Math.Max(0.018, entity.RearClimbAssistLowerWidthM), planarInset, 0.012));
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
        double planarInset)
    {
        double wheelRadius = Math.Clamp(entity.WheelRadiusM, 0.03, 0.24);
        double wheelWidth = 0.055;
        if (entity.WheelOffsetsM.Count == 0)
        {
            return;
        }

        foreach ((double x, double y) in entity.WheelOffsetsM)
        {
            double wheelSide = y * Math.Max(0.2, entity.BodyRenderWidthScale);
            parts.Add(new EntityCollisionPart(
                "wheel",
                x,
                wheelSide,
                ShrinkPlanarDimension(wheelRadius * 2.0 + 0.012, planarInset * 0.75, 0.05),
                ShrinkPlanarDimension(wheelWidth, planarInset * 0.90, 0.02),
                0.0,
                wheelRadius * 2.0));
        }

        if (!string.Equals(entity.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        double rearX = entity.WheelOffsetsM.Min(offset => offset.X);
        double sideExtent = entity.WheelOffsetsM.Max(offset => Math.Abs(offset.Y * Math.Max(0.2, entity.BodyRenderWidthScale)));
        double rearWheelRadius = Math.Clamp(wheelRadius * 1.24, 0.03, 0.32);
        double legLength = Math.Max(0.08, entity.RearClimbAssistLowerLengthM + entity.RearClimbAssistUpperLengthM * 0.36);
        double legWidth = Math.Max(0.028, entity.RearClimbAssistLowerWidthM + rearWheelRadius * 0.30);
        double legX = Math.Min(-bodyLength * 0.48, rearX - legLength * 0.20);
        double legSide = Math.Max(bodyWidth * 0.48, sideExtent);
        parts.Add(new EntityCollisionPart(
            "left_rear_leg",
            legX,
            -legSide,
            ShrinkPlanarDimension(legLength, planarInset * 0.75, 0.05),
            ShrinkPlanarDimension(legWidth, planarInset, 0.02),
            0.0,
            Math.Max(0.16, entity.RearClimbAssistMountHeightM)));
        parts.Add(new EntityCollisionPart(
            "right_rear_leg",
            legX,
            legSide,
            ShrinkPlanarDimension(legLength, planarInset * 0.75, 0.05),
            ShrinkPlanarDimension(legWidth, planarInset, 0.02),
            0.0,
            Math.Max(0.16, entity.RearClimbAssistMountHeightM)));
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
        if (SimulationCombatMath.IsStructure(entity))
        {
            return 0.0;
        }

        double width = Math.Max(0.12, entity.BodyWidthM * Math.Max(0.2, entity.BodyRenderWidthScale));
        double length = Math.Max(0.12, entity.BodyLengthM);
        double minPlanarDimension = Math.Min(length, width);
        double roleBias = entity.RoleKey.ToLowerInvariant() switch
        {
            "hero" => 0.024,
            "engineer" => 0.023,
            "sentry" => 0.024,
            "infantry" => 0.018,
            _ => 0.020,
        };
        if (string.Equals(entity.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase))
        {
            roleBias += 0.003;
        }

        return Math.Clamp(Math.Min(minPlanarDimension * 0.10, roleBias), 0.010, 0.028);
    }

    private static double ShrinkPlanarDimension(double sizeM, double insetM, double minSizeM)
    {
        if (insetM <= 1e-6)
        {
            return Math.Max(minSizeM, sizeM);
        }

        return Math.Max(minSizeM, sizeM - insetM * 2.0);
    }
}
