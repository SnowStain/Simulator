using System.Numerics;

namespace Simulator.Core.Gameplay;

public readonly record struct ArmorPlateTarget(
    string Id,
    double X,
    double Y,
    double HeightM,
    double YawDeg,
    double SideLengthM = 0.13,
    double WidthM = 0.0,
    double HeightSpanM = 0.0,
    int EnergyRingScore = 0);

public readonly record struct AutoAimSolution(
    double YawDeg,
    double PitchDeg,
    double Accuracy,
    double DistanceCoefficient,
    double MotionCoefficient,
    double LeadTimeSec,
    double LeadDistanceM,
    string PlateDirection,
    double AimPointX = 0.0,
    double AimPointY = 0.0,
    double AimPointHeightM = 0.0,
    double ObservedVelocityXMps = 0.0,
    double ObservedVelocityYMps = 0.0,
    double ObservedVelocityZMps = 0.0,
    double ObservedAngularVelocityRadPerSec = 0.0);

public readonly record struct AutoAimCompensationProfile(
    string Name,
    double TranslationLeadScale,
    double AngularLeadScale,
    double TimeBiasSec);

public static class SimulationCombatMath
{
    private const double GravityMps2 = 9.81;
    private const double SmallProjectileAutoAimLatencySec = 0.032;
    private const double LargeProjectileAutoAimLatencySec = 0.055;
    private const double ShooterInheritedVelocityLeadScale = 1.48;
    private const double AutoAimSearchConeHalfAngleDeg = 25.0;
    private const double RuntimeEnergyTargetsTimeToleranceSec = 0.25;
    public const double HeroNormalAutoAimMaxDistanceM = 8.0;
    private const double OutpostTowerRadiusM = 0.20;
    private const double OutpostTowerHeightM = 1.578;
    private const double OutpostBaseLiftM = 0.40;
    private const double OutpostTopArmorCenterLiftM = 0.055;
    private const double OutpostRingSpeedRadPerSec = Math.PI * 0.8;
    private const double OutpostRingDampingRatePerSec = 2.2;
    private const double OutpostRingSettleTimeSec = 2.4;
    private const double BaseArmorOpenThresholdHealth = 2000.0;
    private const double BaseTopArmorSlideAmplitudeM = 0.34;
    private const double BaseTopArmorSlideSpeedRadPerSec = Math.PI * 0.7;
    private const double BaseDiagramHeightM = 1.181;
    private const double BaseTopArmorCenterHeightM = 1.150;
    private const double BaseTopArmorTiltDeg = 27.5;
    private const double EnergyMechanismYawRad = -Math.PI * 0.25;
    private static readonly double OutpostRingSettledResidualRotationRad =
        OutpostRingSpeedRadPerSec / OutpostRingDampingRatePerSec
        * (1.0 - Math.Exp(-OutpostRingDampingRatePerSec * OutpostRingSettleTimeSec));

    public static IReadOnlyList<ArmorPlateTarget> GetArmorPlateTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec = 0.0,
        bool includeOutpostTopArmor = true)
    {
        if (string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            return GetOutpostArmorPlateTargets(target, metersPerWorldUnit, gameTimeSec, includeOutpostTopArmor);
        }

        if (string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            return GetBaseArmorPlateTargets(target, metersPerWorldUnit, gameTimeSec);
        }

        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return GetEnergyMechanismTargets(target, metersPerWorldUnit, gameTimeSec);
        }

        if (IsStructure(target))
        {
            return new[]
            {
                new ArmorPlateTarget(
                    "core",
                    target.X,
                    target.Y,
                    Math.Max(0.25, target.GroundHeightM + target.BodyClearanceM + target.BodyHeightM * 0.5),
                    target.AngleDeg),
            };
        }

        double bodyHalfLengthM = Math.Max(0.01, target.BodyLengthM * 0.5);
        double bodyHalfWidthM = Math.Max(0.01, target.BodyWidthM * target.BodyRenderWidthScale * 0.5);
        double gapM = Math.Max(0.005, target.ArmorPlateGapM);
        double plateThicknessM = ResolveArmorPlateThicknessM(target);
        double localCenterHeightM = Math.Max(0.08, target.BodyClearanceM + target.BodyHeightM * 0.5)
            + ResolveVisualBodyLiftM(target);

        IReadOnlyList<double> orbitYaws = target.ArmorOrbitYawsDeg.Count > 0
            ? target.ArmorOrbitYawsDeg
            : new[] { 0d, 180d, 90d, 270d };
        IReadOnlyList<double> selfYaws = target.ArmorSelfYawsDeg.Count > 0
            ? target.ArmorSelfYawsDeg
            : orbitYaws;

        var plates = new List<ArmorPlateTarget>(orbitYaws.Count);
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateLengthM, target.ArmorPlateHeightM), 0.04, 0.60);
        double bodyYawRad = DegreesToRadians(target.AngleDeg);
        for (int index = 0; index < orbitYaws.Count; index++)
        {
            double orbitYawDeg = orbitYaws[index];
            double orbitYawRad = DegreesToRadians(orbitYawDeg);
            double outwardX = Math.Cos(orbitYawRad);
            double outwardY = Math.Sin(orbitYawRad);
            bool frontBackFace = Math.Abs(outwardX) >= Math.Abs(outwardY);
            double localForwardM;
            double localLateralM;
            double faceYawDeg;
            if (frontBackFace)
            {
                double sign = outwardX >= 0.0 ? 1.0 : -1.0;
                localForwardM = sign * (bodyHalfLengthM + gapM + plateThicknessM * 0.5);
                localLateralM = 0.0;
                faceYawDeg = sign > 0.0 ? 0.0 : 180.0;
            }
            else
            {
                double sign = outwardY >= 0.0 ? 1.0 : -1.0;
                localForwardM = 0.0;
                localLateralM = sign * (bodyHalfWidthM + gapM + plateThicknessM * 0.5);
                faceYawDeg = sign > 0.0 ? 90.0 : -90.0;
            }

            ResolveChassisAxes(
                target.AngleDeg,
                target.ChassisPitchDeg,
                target.ChassisRollDeg,
                out Vector3 chassisForward,
                out Vector3 chassisRight,
                out Vector3 chassisUp);
            Vector3 visualOffset =
                chassisForward * (float)localForwardM
                + chassisRight * (float)localLateralM
                + chassisUp * (float)localCenterHeightM;
            double selfYawDeg = target.ArmorSelfYawsDeg.Count > 0
                ? (index < selfYaws.Count ? selfYaws[index] : orbitYawDeg)
                : faceYawDeg;
            plates.Add(new ArmorPlateTarget(
                $"armor_{index + 1}",
                target.X + visualOffset.X / Math.Max(metersPerWorldUnit, 1e-6),
                target.Y + visualOffset.Z / Math.Max(metersPerWorldUnit, 1e-6),
                target.GroundHeightM + target.AirborneHeightM + visualOffset.Y,
                NormalizeDeg(target.AngleDeg + selfYawDeg),
                armorSideLengthM,
                Math.Clamp(target.ArmorPlateLengthM, 0.04, 0.60),
                Math.Clamp(target.ArmorPlateHeightM, 0.04, 0.60)));
        }

        return plates;
    }

    public static double ResolveOutpostRingRelativeRotationRad(SimulationEntity target, double gameTimeSec)
    {
        double clampedTimeSec = Math.Max(0.0, gameTimeSec);
        double activeSpinTimeSec = Math.Min(clampedTimeSec, 180.0);
        if (target.IsAlive)
        {
            return OutpostRingSpeedRadPerSec * activeSpinTimeSec;
        }

        double destroyedTimeSec = target.DestroyedTimeSec;
        if (double.IsNaN(destroyedTimeSec) || double.IsInfinity(destroyedTimeSec) || destroyedTimeSec < 0.0)
        {
            destroyedTimeSec = clampedTimeSec;
        }

        double spinTimeSec = Math.Min(180.0, Math.Max(0.0, destroyedTimeSec));
        double decayElapsedSec = Math.Max(0.0, clampedTimeSec - destroyedTimeSec);
        if (decayElapsedSec >= OutpostRingSettleTimeSec)
        {
            return OutpostRingSpeedRadPerSec * spinTimeSec + OutpostRingSettledResidualRotationRad;
        }

        double residualRotationRad = decayElapsedSec <= 1e-6
            ? 0.0
            : OutpostRingSpeedRadPerSec / OutpostRingDampingRatePerSec
                * (1.0 - Math.Exp(-OutpostRingDampingRatePerSec * decayElapsedSec));
        return OutpostRingSpeedRadPerSec * spinTimeSec + residualRotationRad;
    }

    public static double ResolveOutpostRingYawDeg(SimulationEntity target, double gameTimeSec)
        => NormalizeDeg(target.AngleDeg + RadiansToDegrees(ResolveOutpostRingRelativeRotationRad(target, gameTimeSec)));

    public static IReadOnlyList<ArmorPlateTarget> GetAttackableArmorPlateTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec = 0.0)
        => GetArmorPlateTargets(target, metersPerWorldUnit, gameTimeSec, includeOutpostTopArmor: false);

    public static IReadOnlyList<ArmorPlateTarget> GetProjectedRobotArmorPlateTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double leadTimeSec)
    {
        if (leadTimeSec <= 1e-6 || IsStructure(target))
        {
            return GetAttackableArmorPlateTargets(target, metersPerWorldUnit, 0.0);
        }

        double projectedYawDeg = NormalizeDeg(
            target.AngleDeg + RadiansToDegrees(ResolveAutoAimAngularVelocityRadPerSec(target)) * leadTimeSec);
        double projectedX = target.X + ResolveObservedVelocityXWorldPerSec(target) * leadTimeSec;
        double projectedY = target.Y + ResolveObservedVelocityYWorldPerSec(target) * leadTimeSec;
        double bodyHalfLengthM = Math.Max(0.01, target.BodyLengthM * 0.5);
        double bodyHalfWidthM = Math.Max(0.01, target.BodyWidthM * target.BodyRenderWidthScale * 0.5);
        double gapM = Math.Max(0.005, target.ArmorPlateGapM);
        double plateThicknessM = ResolveArmorPlateThicknessM(target);
        double localCenterHeightM = Math.Max(0.08, target.BodyClearanceM + target.BodyHeightM * 0.5)
            + ResolveVisualBodyLiftM(target);

        IReadOnlyList<double> orbitYaws = target.ArmorOrbitYawsDeg.Count > 0
            ? target.ArmorOrbitYawsDeg
            : new[] { 0d, 180d, 90d, 270d };
        IReadOnlyList<double> selfYaws = target.ArmorSelfYawsDeg.Count > 0
            ? target.ArmorSelfYawsDeg
            : orbitYaws;

        var plates = new List<ArmorPlateTarget>(orbitYaws.Count);
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateLengthM, target.ArmorPlateHeightM), 0.04, 0.60);
        for (int index = 0; index < orbitYaws.Count; index++)
        {
            double orbitYawDeg = orbitYaws[index];
            double orbitYawRad = DegreesToRadians(orbitYawDeg);
            double outwardX = Math.Cos(orbitYawRad);
            double outwardY = Math.Sin(orbitYawRad);
            bool frontBackFace = Math.Abs(outwardX) >= Math.Abs(outwardY);
            double localForwardM;
            double localLateralM;
            double faceYawDeg;
            if (frontBackFace)
            {
                double sign = outwardX >= 0.0 ? 1.0 : -1.0;
                localForwardM = sign * (bodyHalfLengthM + gapM + plateThicknessM * 0.5);
                localLateralM = 0.0;
                faceYawDeg = sign > 0.0 ? 0.0 : 180.0;
            }
            else
            {
                double sign = outwardY >= 0.0 ? 1.0 : -1.0;
                localForwardM = 0.0;
                localLateralM = sign * (bodyHalfWidthM + gapM + plateThicknessM * 0.5);
                faceYawDeg = sign > 0.0 ? 90.0 : -90.0;
            }

            ResolveChassisAxes(
                projectedYawDeg,
                target.ChassisPitchDeg,
                target.ChassisRollDeg,
                out Vector3 chassisForward,
                out Vector3 chassisRight,
                out Vector3 chassisUp);
            Vector3 visualOffset =
                chassisForward * (float)localForwardM
                + chassisRight * (float)localLateralM
                + chassisUp * (float)localCenterHeightM;
            double selfYawDeg = target.ArmorSelfYawsDeg.Count > 0
                ? (index < selfYaws.Count ? selfYaws[index] : orbitYawDeg)
                : faceYawDeg;
            plates.Add(new ArmorPlateTarget(
                $"armor_{index + 1}",
                projectedX + visualOffset.X / Math.Max(metersPerWorldUnit, 1e-6),
                projectedY + visualOffset.Z / Math.Max(metersPerWorldUnit, 1e-6),
                target.GroundHeightM + target.AirborneHeightM + visualOffset.Y,
                NormalizeDeg(projectedYawDeg + selfYawDeg),
                armorSideLengthM,
                Math.Clamp(target.ArmorPlateLengthM, 0.04, 0.60),
                Math.Clamp(target.ArmorPlateHeightM, 0.04, 0.60)));
        }

        return plates;
    }

    public static IReadOnlyList<ArmorPlateTarget> GetEnergyMechanismTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec,
        string? targetTeam = null,
        SimulationTeamState? teamState = null)
    {
        if (target.AnnotatedEnergyMechanism is { } annotated)
        {
            return GetAnnotatedEnergyMechanismTargets(
                target,
                annotated,
                metersPerWorldUnit,
                gameTimeSec,
                targetTeam,
                teamState);
        }

        return GetLegacyEnergyMechanismTargets(target, metersPerWorldUnit, gameTimeSec, targetTeam, teamState);
    }

    private static IReadOnlyList<ArmorPlateTarget> GetAnnotatedEnergyMechanismTargets(
        SimulationEntity target,
        AnnotatedEnergyMechanismDefinition annotated,
        double metersPerWorldUnit,
        double gameTimeSec,
        string? targetTeam,
        SimulationTeamState? teamState)
    {
        bool hasRuntimeTargets =
            target.RuntimeEnergyTargetsByTeam is { Count: > 0 }
            && !double.IsNaN(target.RuntimeEnergyTargetsGameTimeSec);
        if (hasRuntimeTargets
            && target.RuntimeEnergyTargetsByTeam is { Count: > 0 } runtimeTargetsByTeam)
        {
            if (!string.IsNullOrWhiteSpace(targetTeam)
                && runtimeTargetsByTeam.TryGetValue(targetTeam, out IReadOnlyList<ArmorPlateTarget>? runtimeTargets)
                && TryBuildProjectedRuntimeEnergyTargets(
                    annotated,
                    runtimeTargets,
                    targetTeam,
                    target.RuntimeEnergyTargetsGameTimeSec,
                    gameTimeSec,
                    metersPerWorldUnit,
                    teamState,
                    out IReadOnlyList<ArmorPlateTarget>? projectedRuntimeTargets))
            {
                return projectedRuntimeTargets;
            }

            if (Math.Abs(target.RuntimeEnergyTargetsGameTimeSec - gameTimeSec) <= RuntimeEnergyTargetsTimeToleranceSec)
            {
                var runtimeMergedTargets = new List<ArmorPlateTarget>(128);
                foreach (KeyValuePair<string, IReadOnlyList<ArmorPlateTarget>> entry in runtimeTargetsByTeam
                             .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    runtimeMergedTargets.AddRange(entry.Value);
                }

                if (runtimeMergedTargets.Count > 0)
                {
                    return runtimeMergedTargets;
                }
            }
        }

        var mergedTargets = new List<ArmorPlateTarget>(128);
        if (!string.IsNullOrWhiteSpace(targetTeam))
        {
            AppendAnnotatedEnergyTeamTargets(
                mergedTargets,
                annotated,
                target,
                metersPerWorldUnit,
                gameTimeSec,
                targetTeam,
                teamState);
            return mergedTargets;
        }

        AppendAnnotatedEnergyTeamTargets(
            mergedTargets,
            annotated,
            target,
            metersPerWorldUnit,
            gameTimeSec,
            "red",
            teamState);
        AppendAnnotatedEnergyTeamTargets(
            mergedTargets,
            annotated,
            target,
            metersPerWorldUnit,
            gameTimeSec,
            "blue",
            teamState);
        return mergedTargets;
    }

    private static bool TryBuildProjectedRuntimeEnergyTargets(
        AnnotatedEnergyMechanismDefinition annotated,
        IReadOnlyList<ArmorPlateTarget> runtimeTargets,
        string team,
        double sourceTimeSec,
        double targetTimeSec,
        double metersPerWorldUnit,
        SimulationTeamState? teamState,
        out IReadOnlyList<ArmorPlateTarget> projectedTargets)
    {
        projectedTargets = Array.Empty<ArmorPlateTarget>();
        if (runtimeTargets.Count == 0)
        {
            return false;
        }

        if (!annotated.Teams.TryGetValue(team, out AnnotatedEnergyTeamDefinition? teamDefinition))
        {
            projectedTargets = runtimeTargets;
            return true;
        }

        double deltaYawRad = ResolveEnergyRotorYawRad(targetTimeSec, teamState) - ResolveEnergyRotorYawRad(sourceTimeSec, teamState);
        if (Math.Abs(deltaYawRad) <= 1e-6)
        {
            projectedTargets = runtimeTargets;
            return true;
        }

        double safeMeters = Math.Max(metersPerWorldUnit, 1e-6);
        Vector3 axis = teamDefinition.RotorAxisWorld.LengthSquared() <= 1e-8f
            ? Vector3.UnitY
            : Vector3.Normalize(teamDefinition.RotorAxisWorld);
        var rotatedTargets = new List<ArmorPlateTarget>(runtimeTargets.Count);
        foreach (ArmorPlateTarget runtimeTarget in runtimeTargets)
        {
            Vector3 localOffset = new(
                (float)((runtimeTarget.X - teamDefinition.PivotWorldX) * safeMeters),
                (float)(runtimeTarget.HeightM - teamDefinition.PivotHeightM),
                (float)((runtimeTarget.Y - teamDefinition.PivotWorldY) * safeMeters));
            Vector3 rotatedOffset = RotateAroundAxis(localOffset, axis, (float)deltaYawRad);

            Vector3 yawNormal = new(
                (float)Math.Cos(DegreesToRadians(runtimeTarget.YawDeg)),
                0f,
                (float)Math.Sin(DegreesToRadians(runtimeTarget.YawDeg)));
            Vector3 rotatedNormal = RotateAroundAxis(yawNormal, axis, (float)deltaYawRad);
            Vector3 projectedNormal = new(rotatedNormal.X, 0f, rotatedNormal.Z);
            double yawDeg = projectedNormal.LengthSquared() <= 1e-8f
                ? runtimeTarget.YawDeg
                : NormalizeDeg(RadiansToDegrees(Math.Atan2(projectedNormal.Z, projectedNormal.X)));

            rotatedTargets.Add(runtimeTarget with
            {
                X = teamDefinition.PivotWorldX + rotatedOffset.X / safeMeters,
                Y = teamDefinition.PivotWorldY + rotatedOffset.Z / safeMeters,
                HeightM = teamDefinition.PivotHeightM + rotatedOffset.Y,
                YawDeg = yawDeg,
            });
        }

        projectedTargets = rotatedTargets
            .OrderBy(candidate => ResolveEnergyArmSortKey(candidate.Id))
            .ThenByDescending(candidate => candidate.EnergyRingScore)
            .ToArray();
        return rotatedTargets.Count > 0;
    }

    private static void AppendAnnotatedEnergyTeamTargets(
        ICollection<ArmorPlateTarget> output,
        AnnotatedEnergyMechanismDefinition annotated,
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec,
        string team,
        SimulationTeamState? teamState)
    {
        var mergedById = new Dictionary<string, ArmorPlateTarget>(StringComparer.OrdinalIgnoreCase);
        if (annotated.Teams.TryGetValue(team, out AnnotatedEnergyTeamDefinition? teamDefinition))
        {
            double rotorYawRad = ResolveEnergyRotorYawRad(gameTimeSec, teamState);
            foreach (AnnotatedEnergyPlateState plateState in teamDefinition.PlateStates)
            {
                ArmorPlateTarget annotatedTarget = BuildAnnotatedEnergyPlateTarget(teamDefinition, plateState, rotorYawRad, metersPerWorldUnit);
                mergedById[annotatedTarget.Id] = annotatedTarget;
            }
        }

        if (!HasCompleteAnnotatedEnergyRingSet(mergedById.Values, team))
        {
            IReadOnlyList<ArmorPlateTarget> legacyTargets = GetLegacyEnergyMechanismTargets(
                target,
                metersPerWorldUnit,
                gameTimeSec,
                team,
                teamState);
            foreach (ArmorPlateTarget fallbackTarget in ExpandLegacyEnergyTargetsToRings(legacyTargets))
            {
                mergedById.TryAdd(fallbackTarget.Id, fallbackTarget);
            }
        }

        foreach (ArmorPlateTarget targetPlate in mergedById.Values
                     .OrderBy(candidate => ResolveEnergyArmSortKey(candidate.Id))
                     .ThenByDescending(candidate => candidate.EnergyRingScore))
        {
            output.Add(targetPlate);
        }
    }

    private static ArmorPlateTarget BuildAnnotatedEnergyPlateTarget(
        AnnotatedEnergyTeamDefinition teamDefinition,
        AnnotatedEnergyPlateState plateState,
        double rotorYawRad,
        double metersPerWorldUnit)
    {
        Vector3 axis = teamDefinition.RotorAxisWorld.LengthSquared() <= 1e-8f
            ? Vector3.UnitY
            : Vector3.Normalize(teamDefinition.RotorAxisWorld);
        Vector3 baseOffset = new(
            (float)plateState.BaseOffsetXM,
            (float)plateState.BaseOffsetYM,
            (float)plateState.BaseOffsetZM);
        Vector3 baseNormal = new(
            (float)plateState.BaseNormalXM,
            (float)plateState.BaseNormalYM,
            (float)plateState.BaseNormalZM);
        Vector3 rotatedOffset = RotateAroundAxis(baseOffset, axis, (float)rotorYawRad);
        Vector3 rotatedNormal = RotateAroundAxis(baseNormal, axis, (float)rotorYawRad);
        if (rotatedNormal.LengthSquared() <= 1e-8f)
        {
            rotatedNormal = baseNormal.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(baseNormal);
        }
        else
        {
            rotatedNormal = Vector3.Normalize(rotatedNormal);
        }

        Vector3 projectedNormal = new(rotatedNormal.X, 0f, rotatedNormal.Z);
        double yawDeg = plateState.BaseYawDeg;
        if (projectedNormal.LengthSquared() > 1e-8f)
        {
            projectedNormal = Vector3.Normalize(projectedNormal);
            yawDeg = NormalizeDeg(RadiansToDegrees(Math.Atan2(projectedNormal.Z, projectedNormal.X)));
        }

        double safeMeters = Math.Max(metersPerWorldUnit, 1e-6);
        return new ArmorPlateTarget(
            plateState.PlateId,
            teamDefinition.PivotWorldX + rotatedOffset.X / safeMeters,
            teamDefinition.PivotWorldY + rotatedOffset.Z / safeMeters,
            teamDefinition.PivotHeightM + rotatedOffset.Y,
            yawDeg,
            plateState.SideLengthM,
            plateState.WidthM,
            plateState.HeightSpanM,
            plateState.RingScore);
    }

    private static Vector3 RotateAroundAxis(Vector3 vector, Vector3 axis, float angleRad)
    {
        if (vector.LengthSquared() <= 1e-12f || Math.Abs(angleRad) <= 1e-8f)
        {
            return vector;
        }

        Vector3 normalizedAxis = axis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(axis);
        float cos = MathF.Cos(angleRad);
        float sin = MathF.Sin(angleRad);
        return vector * cos
            + Vector3.Cross(normalizedAxis, vector) * sin
            + normalizedAxis * Vector3.Dot(normalizedAxis, vector) * (1f - cos);
    }

    private static bool HasCompleteAnnotatedEnergyRingSet(
        IEnumerable<ArmorPlateTarget> targets,
        string team)
    {
        int maskByArm0 = 0;
        int maskByArm1 = 0;
        int maskByArm2 = 0;
        int maskByArm3 = 0;
        int maskByArm4 = 0;
        foreach (ArmorPlateTarget target in targets)
        {
            if (!TryParseEnergyArmIndex(target.Id, out string parsedTeam, out int armIndex)
                || !string.Equals(parsedTeam, team, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int ringScore = target.EnergyRingScore;
            if (ringScore <= 0 && !TryParseEnergyRingScore(target.Id, out ringScore))
            {
                continue;
            }

            int bit = 1 << Math.Clamp(ringScore - 1, 0, 9);
            switch (armIndex)
            {
                case 0:
                    maskByArm0 |= bit;
                    break;
                case 1:
                    maskByArm1 |= bit;
                    break;
                case 2:
                    maskByArm2 |= bit;
                    break;
                case 3:
                    maskByArm3 |= bit;
                    break;
                case 4:
                    maskByArm4 |= bit;
                    break;
            }
        }

        const int completeRingMask = (1 << 10) - 1;
        return maskByArm0 == completeRingMask
            && maskByArm1 == completeRingMask
            && maskByArm2 == completeRingMask
            && maskByArm3 == completeRingMask
            && maskByArm4 == completeRingMask;
    }

    private static IEnumerable<ArmorPlateTarget> ExpandLegacyEnergyTargetsToRings(
        IReadOnlyList<ArmorPlateTarget> legacyTargets)
    {
        foreach (ArmorPlateTarget legacyTarget in legacyTargets)
        {
            if (!TryParseEnergyArmIndex(legacyTarget.Id, out string team, out int armIndex))
            {
                continue;
            }

            double baseWidthM = legacyTarget.WidthM > 1e-6 ? legacyTarget.WidthM : legacyTarget.SideLengthM;
            double baseHeightM = legacyTarget.HeightSpanM > 1e-6 ? legacyTarget.HeightSpanM : legacyTarget.SideLengthM;
            for (int ringScore = 1; ringScore <= 10; ringScore++)
            {
                double ratio = Math.Max(0.18, 1.0 - (ringScore - 1) * 0.08);
                double widthM = Math.Max(0.02, baseWidthM * ratio);
                double heightM = Math.Max(0.02, baseHeightM * ratio);
                yield return new ArmorPlateTarget(
                    $"energy_{team}_arm_{armIndex}_ring_{ringScore}",
                    legacyTarget.X,
                    legacyTarget.Y,
                    legacyTarget.HeightM,
                    legacyTarget.YawDeg,
                    Math.Max(widthM, heightM),
                    widthM,
                    heightM,
                    ringScore);
            }
        }
    }

    private static int ResolveEnergyArmSortKey(string plateId)
        => TryParseEnergyArmIndex(plateId, out _, out int armIndex) ? armIndex : int.MaxValue;

    private static IReadOnlyList<ArmorPlateTarget> GetLegacyEnergyMechanismTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec,
        string? targetTeam = null,
        SimulationTeamState? teamState = null)
    {
        double safeMeters = Math.Max(metersPerWorldUnit, 1e-6);
        double anchorXM = target.X * safeMeters;
        double anchorZM = target.Y * safeMeters;
        double forwardX = Math.Cos(EnergyMechanismYawRad);
        double forwardZ = Math.Sin(EnergyMechanismYawRad);
        double rightX = -forwardZ;
        double rightZ = forwardX;
        double normalYawDeg = NormalizeDeg(RadiansToDegrees(Math.Atan2(rightZ, rightX)));
        EnergyTargetLayout layout = ResolveEnergyTargetLayout(target);
        double rotorYaw = ResolveEnergyRotorYawRad(gameTimeSec, teamState);
        string[] sides = string.IsNullOrWhiteSpace(targetTeam)
            ? new[] { "red", "blue" }
            : new[] { targetTeam };
        var plates = new List<ArmorPlateTarget>(sides.Length * 5);
        foreach (string side in sides)
        {
            bool redSide = string.Equals(side, "red", StringComparison.OrdinalIgnoreCase);
            double localZ = redSide ? -layout.RotorAxisGapM * 0.5 : layout.RotorAxisGapM * 0.5;
            for (int index = 0; index < 5; index++)
            {
                double yaw = rotorYaw + layout.RotorPhaseRad + Math.Tau * index / 5.0;
                double localX = Math.Cos(yaw) * layout.RotorRadiusM;
                double localY = layout.RotorCenterHeightM + Math.Sin(yaw) * layout.RotorRadiusM;
                double xM = anchorXM + forwardX * localX + rightX * localZ;
                double zM = anchorZM + forwardZ * localX + rightZ * localZ;
                plates.Add(new ArmorPlateTarget(
                    $"energy_{(redSide ? "red" : "blue")}_arm_{index}",
                    xM / safeMeters,
                    zM / safeMeters,
                    target.GroundHeightM + localY,
                    normalYawDeg,
                    layout.DiskDiameterM,
                    layout.DiskDiameterM,
                    layout.DiskDiameterM));
            }
        }

        return plates;
    }

    private static EnergyTargetLayout ResolveEnergyTargetLayout(SimulationEntity target)
    {
        double baseHeight = Math.Max(0.0, target.StructureBaseHeightM);
        double groundClearance = Math.Max(0.0, target.StructureGroundClearanceM);
        double frameWidth = Math.Max(0.80, target.StructureFrameWidthM > 1e-6 ? target.StructureFrameWidthM : target.BodyLengthM);
        double frameDepth = Math.Max(0.06, target.StructureFrameDepthM > 1e-6 ? target.StructureFrameDepthM : target.BodyWidthM * 0.18);
        double rotorCenterHeight = Math.Max(baseHeight + groundClearance + 0.40, target.StructureRotorCenterHeightM);
        double rotorPhaseRad = DegreesToRadians(target.StructureRotorPhaseDeg);
        double rotorRadius = Math.Max(0.18, target.StructureRotorRadiusM);
        double hubRadius = Math.Max(0.05, target.StructureRotorHubRadiusM);
        double lampLength = Math.Max(0.06, target.StructureLampLengthM);
        double lampWidth = Math.Max(0.06, target.StructureLampWidthM);
        double armWidth = Math.Max(0.04, Math.Min(lampLength, lampWidth) * 0.20);
        double cantileverLength = Math.Max(0.0, target.StructureCantileverLengthM);
        double cantileverPairGap = Math.Max(
            frameWidth + cantileverLength,
            target.StructureCantileverPairGapM > 1e-6 ? target.StructureCantileverPairGapM : frameWidth + cantileverLength);
        double rotorAxisGap = Math.Max(
            Math.Max(frameDepth * 1.8, hubRadius * 2.6),
            Math.Min(cantileverPairGap, frameWidth) * 0.42 + cantileverLength * 0.30);
        double diskDiameterM = Math.Max(0.18, Math.Max(lampLength, lampWidth));
        rotorRadius += Math.Max(diskDiameterM * 0.21, armWidth * 1.10);
        return new EnergyTargetLayout(
            rotorCenterHeight + target.StructureCantileverOffsetYM,
            rotorPhaseRad,
            rotorRadius,
            rotorAxisGap,
            diskDiameterM);
    }

    private static double ResolveEnergyRotorYawRad(double gameTimeSec, SimulationTeamState? teamState)
    {
        const double smallSpeedRadPerSec = Math.PI / 3.0;
        const double largeActiveA = 0.9125;
        const double largeActiveOmega = 1.942;
        const double largeActiveB = 2.090 - largeActiveA;
        int direction = teamState?.EnergyRotorDirectionSign != 0 ? teamState?.EnergyRotorDirectionSign ?? 1 : 1;
        if (teamState is null
            || (!string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase)))
        {
            return direction * gameTimeSec * smallSpeedRadPerSec;
        }

        bool largeActive = string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
            && teamState.EnergyLargeMechanismActive;
        double safeTime = Math.Max(0.0, gameTimeSec - teamState.EnergyStateStartTimeSec);
        double basePhase = Math.Max(0.0, teamState.EnergyStateStartTimeSec) * smallSpeedRadPerSec;
        if (!largeActive)
        {
            return direction * gameTimeSec * smallSpeedRadPerSec;
        }

        double speedIntegral = largeActiveB * safeTime
            + largeActiveA / largeActiveOmega * (1.0 - Math.Cos(largeActiveOmega * safeTime));
        return direction * (basePhase + speedIntegral);
    }

public static bool TryAcquireEnergyMechanismTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        Func<SimulationEntity, ArmorPlateTarget, bool>? canSeePlate = null)
    {
        target = null;
        plate = default;
        if (!world.Teams.TryGetValue(shooter.Team, out SimulationTeamState? teamState))
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double bestScore = double.MaxValue;
        List<RankedAutoAimCandidate>? visibleCandidateQueue = null;
        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || !string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IReadOnlyList<ArmorPlateTarget> mechanismTargets = GetEnergyMechanismTargets(
                candidate,
                metersPerWorldUnit,
                world.GameTimeSec,
                shooter.Team,
                teamState);
            foreach (ArmorPlateTarget visualPlate in mechanismTargets)
            {
                if (!IsEnergyVisualObservationRing(visualPlate)
                    || (teamState.EnergyCurrentLitMask != 0 && !IsEnergyPlateLitForTeam(teamState, visualPlate.Id)))
                {
                    continue;
                }

                ArmorPlateTarget candidatePlate = ResolveEnergyTenRingAimPlate(mechanismTargets, visualPlate);
                double dxWorld = visualPlate.X - shooter.X;
                double dyWorld = visualPlate.Y - shooter.Y;
                double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
                if (distanceM > maxDistanceM)
                {
                    continue;
                }

                (double observedX, double observedY, double observedHeightM) = PredictArmorPlatePoint(
                    world,
                    shooter,
                    candidate,
                    visualPlate,
                    distanceM,
                    0.0,
                    out _,
                    out _);
                (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
                    world,
                    shooter,
                    observedX,
                    observedY,
                    observedHeightM,
                    preferHighArc: false);

                double yawError = Math.Abs(NormalizeSignedDeg(yawDeg - shooter.TurretYawDeg));
                double pitchError = Math.Abs(pitchDeg - shooter.GimbalPitchDeg);
                if (!IsWithinAutoAimSearchCone(yawError, pitchError, lobMode: false))
                {
                    continue;
                }

                if (canSeePlate is not null && !canSeePlate(candidate, visualPlate))
                {
                    continue;
                }

                double score = yawError * yawError * 1.45 + pitchError * pitchError * 1.85 + distanceM * 0.04;
                score -= ResolveRotatingArmorFreshAppearanceBonus(
                    world,
                    shooter,
                    candidate,
                    visualPlate,
                    metersPerWorldUnit);
                if (visibleCandidateQueue is not null)
                {
                    AddRankedAutoAimCandidate(visibleCandidateQueue, candidate, candidatePlate, score, 5);
                    continue;
                }

                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                target = candidate;
                plate = candidatePlate;
            }
        }

        if (visibleCandidateQueue is not null)
        {
            return TrySelectFirstVisibleCandidate(visibleCandidateQueue, canSeePlate!, out target, out plate);
        }

        return target is not null;
    }

    private static bool IsEnergyVisualObservationRing(ArmorPlateTarget plate)
    {
        if (!plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int ringScore = plate.EnergyRingScore;
        if (ringScore <= 0 && !TryParseEnergyRingScore(plate.Id, out ringScore))
        {
            return false;
        }

        return ringScore == 1;
    }

    private static ArmorPlateTarget ResolveEnergyTenRingAimPlate(
        IReadOnlyList<ArmorPlateTarget> mechanismTargets,
        ArmorPlateTarget visualPlate)
    {
        if (!TryParseEnergyArmIndex(visualPlate.Id, out string team, out int armIndex))
        {
            return visualPlate;
        }

        ArmorPlateTarget tenRing = mechanismTargets.FirstOrDefault(candidate =>
            TryParseEnergyArmIndex(candidate.Id, out string candidateTeam, out int candidateArm)
            && string.Equals(candidateTeam, team, StringComparison.OrdinalIgnoreCase)
            && candidateArm == armIndex
            && ResolveEnergyRingScore(candidate) == 10);
        return string.IsNullOrWhiteSpace(tenRing.Id) ? visualPlate : tenRing;
    }

    private static ArmorPlateTarget ResolveEnergyObservationRingPlate(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget aimPlate,
        double gameTimeSec)
    {
        if (!TryParseEnergyArmIndex(aimPlate.Id, out string team, out int armIndex))
        {
            return aimPlate;
        }

        SimulationTeamState? teamState = null;
        if (!world.Teams.TryGetValue(team, out teamState))
        {
            world.Teams.TryGetValue(shooter.Team, out teamState);
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        ArmorPlateTarget oneRing = GetEnergyMechanismTargets(target, metersPerWorldUnit, gameTimeSec, team, teamState)
            .FirstOrDefault(candidate =>
                TryParseEnergyArmIndex(candidate.Id, out string candidateTeam, out int candidateArm)
                && string.Equals(candidateTeam, team, StringComparison.OrdinalIgnoreCase)
                && candidateArm == armIndex
                && ResolveEnergyRingScore(candidate) == 1);
        return string.IsNullOrWhiteSpace(oneRing.Id) ? aimPlate : oneRing;
    }

    private static int ResolveEnergyRingScore(ArmorPlateTarget plate)
    {
        if (plate.EnergyRingScore > 0)
        {
            return plate.EnergyRingScore;
        }

        return TryParseEnergyRingScore(plate.Id, out int score) ? score : 0;
    }

    public static bool IsEnergyPlateLitForTeam(SimulationTeamState teamState, string plateId)
    {
        if (!TryParseEnergyArmIndex(plateId, out string team, out int armIndex)
            || !string.Equals(team, teamState.Team, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (teamState.EnergyCurrentLitMask & (1 << armIndex)) != 0;
    }

    public static bool TryParseEnergyArmIndex(string plateId, out string team, out int armIndex)
    {
        team = string.Empty;
        armIndex = -1;
        string[] parts = (plateId ?? string.Empty).Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4
            || !parts[0].Equals("energy", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("arm", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[3], out armIndex)
            || armIndex < 0
            || armIndex > 4)
        {
            return false;
        }

        team = parts[1];
        return team.Equals("red", StringComparison.OrdinalIgnoreCase)
            || team.Equals("blue", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseEnergyRingScore(string plateId, out int ringScore)
    {
        ringScore = 0;
        string[] parts = (plateId ?? string.Empty).Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6
            || !parts[0].Equals("energy", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("arm", StringComparison.OrdinalIgnoreCase)
            || !parts[4].Equals("ring", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[5], out int parsed))
        {
            return false;
        }

        ringScore = Math.Clamp(parsed, 1, 10);
        return true;
    }

    public static bool TryAcquireAutoAimTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        Func<SimulationEntity, ArmorPlateTarget, bool>? canSeePlate = null)
    {
        if (string.Equals(shooter.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            if (TryAcquireLockedEnergyMechanismTarget(world, shooter, maxDistanceM, out target, out plate, canSeePlate))
            {
                return true;
            }

            return TryAcquireEnergyMechanismTarget(world, shooter, maxDistanceM, out target, out plate, canSeePlate);
        }

        target = null;
        plate = default;

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        if (TryAcquireLockedAutoAimTarget(
                world,
                shooter,
                maxDistanceM,
                metersPerWorldUnit,
                out target,
                out plate,
                canSeePlate))
        {
            return true;
        }

        double bestScore = double.MaxValue;
        bool heroLobMode = IsHeroLobAutoAimMode(shooter);
        List<RankedAutoAimCandidate>? visibleCandidateQueue = canSeePlate is null
            ? null
            : new List<RankedAutoAimCandidate>(32);
        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id, shooter.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ArmorPlateTarget candidatePlate in GetAttackableArmorPlateTargets(candidate, metersPerWorldUnit, world.GameTimeSec))
            {
                double dxWorld = candidatePlate.X - shooter.X;
                double dyWorld = candidatePlate.Y - shooter.Y;
                double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
                if (distanceM > maxDistanceM)
                {
                    continue;
                }

                if (!IsAutoAimArmorTargetEligible(world, shooter, candidate, candidatePlate, distanceM))
                {
                    continue;
                }

                (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
                    world,
                    shooter,
                    candidatePlate.X,
                    candidatePlate.Y,
                    candidatePlate.HeightM,
                    preferHighArc: heroLobMode && IsHeroStructureAutoAimTargetPlate(world, shooter, candidate, candidatePlate));
                double yawError = Math.Abs(NormalizeSignedDeg(yawDeg - shooter.TurretYawDeg));
                double pitchError = Math.Abs(pitchDeg - shooter.GimbalPitchDeg);
                if (!IsWithinAutoAimSearchCone(yawError, pitchError, heroLobMode))
                {
                    continue;
                }

                double score = yawError * yawError * 1.85 + pitchError * pitchError * 2.30 + distanceM * 0.08;
                if (heroLobMode)
                {
                    score -= 220.0;
                }

                bool plateFacingOrEmerging = IsPlateFacingOrEmergingSoon(
                    world,
                    shooter,
                    candidate,
                    candidatePlate,
                    metersPerWorldUnit,
                    out double lifetimeScore);
                if (!plateFacingOrEmerging)
                {
                    continue;
                }

                double plateAreaScore = ResolveArmorPlateAreaScore(candidatePlate);
                double freshAppearanceBonus = ResolveRotatingArmorFreshAppearanceBonus(
                    world,
                    shooter,
                    candidate,
                    candidatePlate,
                    metersPerWorldUnit);
                double exitPenalty = ResolveRotatingArmorExitPenalty(
                    world,
                    shooter,
                    candidate,
                    candidatePlate,
                    metersPerWorldUnit);
                bool sameLockedTarget = string.Equals(shooter.AutoAimTargetId, candidate.Id, StringComparison.OrdinalIgnoreCase);
                bool sameLockedPlate = string.Equals(shooter.AutoAimTargetId, candidate.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(shooter.AutoAimPlateId, candidatePlate.Id, StringComparison.OrdinalIgnoreCase);
                if (sameLockedPlate)
                {
                    double futureMargin = Math.Max(-1.0, Math.Min(1.0, lifetimeScore / 90.0));
                    double lockRetention = futureMargin > 0.18 ? 118.0 : 76.0;
                    if (exitPenalty > 48.0)
                    {
                        lockRetention *= Math.Clamp(1.0 - exitPenalty / 190.0, 0.08, 0.55);
                    }

                    score -= lockRetention;
                }
                else if (sameLockedTarget)
                {
                    // Keep the current target stable first, then consider plate switching.
                    double rotatingSwitchRelief = IsRotatingArmorPlate(candidate, candidatePlate)
                        ? Math.Clamp((freshAppearanceBonus - 48.0) * 1.25, 0.0, 98.0)
                        : 0.0;
                    score += 126.0 - rotatingSwitchRelief;
                }
                else if (shooter.AutoAimLocked && IsArmorAutoAimTargetKind(shooter.AutoAimTargetKind))
                {
                    // Different-target switching needs to be much more expensive than same-target re-selection.
                    score += 268.0;
                }

                score -= lifetimeScore;
                score -= freshAppearanceBonus;
                score += exitPenalty;
                if (IsRotatingArmorPlate(candidate, candidatePlate))
                {
                    // Prefer the plate that has just entered the visible arc over a plate
                    // that is already centered but about to rotate away, but keep the camera stable.
                    score -= Math.Clamp(freshAppearanceBonus - 52.0, 0.0, 120.0) * 0.42;
                }

                score -= Math.Clamp(plateAreaScore, 0.0, 0.20) * 160.0;
                if (visibleCandidateQueue is not null)
                {
                    AddRankedAutoAimCandidate(visibleCandidateQueue, candidate, candidatePlate, score, 32);
                    continue;
                }

                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                target = candidate;
                plate = candidatePlate;
            }
        }

        if (visibleCandidateQueue is not null)
        {
            return TrySelectFirstVisibleCandidate(visibleCandidateQueue, canSeePlate!, out target, out plate);
        }

        return target is not null;
    }

    private static void AddRankedAutoAimCandidate(
        List<RankedAutoAimCandidate> candidates,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double score,
        int maxCount)
    {
        int insertIndex = candidates.Count;
        while (insertIndex > 0 && score < candidates[insertIndex - 1].Score)
        {
            insertIndex--;
        }

        if (insertIndex >= maxCount && candidates.Count >= maxCount)
        {
            return;
        }

        candidates.Insert(insertIndex, new RankedAutoAimCandidate(target, plate, score));
        if (candidates.Count > maxCount)
        {
            candidates.RemoveAt(candidates.Count - 1);
        }
    }

    private static bool TrySelectFirstVisibleCandidate(
        IReadOnlyList<RankedAutoAimCandidate> candidates,
        Func<SimulationEntity, ArmorPlateTarget, bool> canSeePlate,
        out SimulationEntity? target,
        out ArmorPlateTarget plate)
    {
        foreach (RankedAutoAimCandidate candidate in candidates)
        {
            if (!canSeePlate(candidate.Target, candidate.Plate))
            {
                continue;
            }

            target = candidate.Target;
            plate = candidate.Plate;
            return true;
        }

        target = null;
        plate = default;
        return false;
    }

    private readonly record struct RankedAutoAimCandidate(
        SimulationEntity Target,
        ArmorPlateTarget Plate,
        double Score);

    private static bool IsWithinAutoAimSearchCone(double yawErrorDeg, double pitchErrorDeg, bool lobMode)
    {
        double pitchWeight = lobMode ? 0.42 : 0.70;
        double angularError = Math.Sqrt(yawErrorDeg * yawErrorDeg + pitchErrorDeg * pitchErrorDeg * pitchWeight * pitchWeight);
        return angularError <= AutoAimSearchConeHalfAngleDeg;
    }

    private static bool TryAcquireLockedAutoAimTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        double metersPerWorldUnit,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        Func<SimulationEntity, ArmorPlateTarget, bool>? canSeePlate)
    {
        target = null;
        plate = default;

        if (string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return false;
        }

        bool heroLobMode = IsHeroLobAutoAimMode(shooter);
        target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && !string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        plate = GetAttackableArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, shooter.AutoAimPlateId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(plate.Id))
        {
            target = null;
            plate = default;
            return false;
        }

        double dxWorld = plate.X - shooter.X;
        double dyWorld = plate.Y - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        if (distanceM > maxDistanceM)
        {
            target = null;
            plate = default;
            return false;
        }

        if (!IsAutoAimArmorTargetEligible(world, shooter, target, plate, distanceM))
        {
            target = null;
            plate = default;
            return false;
        }

        (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
            world,
            shooter,
            plate.X,
            plate.Y,
            plate.HeightM,
            preferHighArc: heroLobMode && IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate));
        double yawError = Math.Abs(NormalizeSignedDeg(yawDeg - shooter.TurretYawDeg));
        double pitchError = Math.Abs(pitchDeg - shooter.GimbalPitchDeg);
        if (!IsWithinAutoAimSearchCone(yawError, pitchError, heroLobMode))
        {
            target = null;
            plate = default;
            return false;
        }

        if (!IsPlateFacingOrEmergingSoon(world, shooter, target, plate, metersPerWorldUnit, out _))
        {
            target = null;
            plate = default;
            return false;
        }

        if (canSeePlate is not null && !canSeePlate(target, plate))
        {
            target = null;
            plate = default;
            return false;
        }

        if (TrySelectEmergingSameTargetRotatingPlate(
                world,
                shooter,
                target,
                plate,
                maxDistanceM,
                metersPerWorldUnit,
                heroLobMode,
                canSeePlate,
                out ArmorPlateTarget preferredPlate))
        {
            plate = preferredPlate;
        }

        return true;
    }

    private static bool TryAcquireLockedEnergyMechanismTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        Func<SimulationEntity, ArmorPlateTarget, bool>? canSeePlate)
    {
        target = null;
        plate = default;

        if (string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return false;
        }

        target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        string targetTeam = shooter.Team;
        SimulationTeamState? teamState = null;
        if (TryParseEnergyArmIndex(shooter.AutoAimPlateId, out string parsedTeam, out _))
        {
            targetTeam = parsedTeam;
            world.Teams.TryGetValue(parsedTeam, out teamState);
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        IReadOnlyList<ArmorPlateTarget> mechanismTargets = GetEnergyMechanismTargets(
            target,
            metersPerWorldUnit,
            world.GameTimeSec,
            targetTeam,
            teamState);
        plate = mechanismTargets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, shooter.AutoAimPlateId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(plate.Id))
        {
            target = null;
            plate = default;
            return false;
        }

        if (teamState is not null
            && teamState.EnergyCurrentLitMask != 0
            && !IsEnergyPlateLitForTeam(teamState, plate.Id))
        {
            target = null;
            plate = default;
            return false;
        }

        ArmorPlateTarget observationPlate = ResolveEnergyObservationRingPlate(world, shooter, target, plate, world.GameTimeSec);
        double dxWorld = observationPlate.X - shooter.X;
        double dyWorld = observationPlate.Y - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        if (distanceM > maxDistanceM)
        {
            target = null;
            plate = default;
            return false;
        }

        (double observedX, double observedY, double observedHeightM) = PredictArmorPlatePoint(
            world,
            shooter,
            target,
            observationPlate,
            distanceM,
            0.0,
            out _,
            out _);
        (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
            world,
            shooter,
            observedX,
            observedY,
            observedHeightM,
            preferHighArc: false);
        double yawError = Math.Abs(NormalizeSignedDeg(yawDeg - shooter.TurretYawDeg));
        double pitchError = Math.Abs(pitchDeg - shooter.GimbalPitchDeg);
        if (!IsWithinAutoAimSearchCone(yawError, pitchError, lobMode: false))
        {
            target = null;
            plate = default;
            return false;
        }

        if (canSeePlate is not null && !canSeePlate(target, observationPlate))
        {
            target = null;
            plate = default;
            return false;
        }

        return true;
    }

    private static bool TrySelectEmergingSameTargetRotatingPlate(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget currentPlate,
        double maxDistanceM,
        double metersPerWorldUnit,
        bool heroLobMode,
        Func<SimulationEntity, ArmorPlateTarget, bool>? canSeePlate,
        out ArmorPlateTarget preferredPlate)
    {
        preferredPlate = default;
        if (!IsRotatingArmorPlate(target, currentPlate)
            || world.GameTimeSec - shooter.AutoAimLastLockChangeTimeSec < 0.18)
        {
            return false;
        }

        double currentLifetime = ComputePlateLifetimeScore(world, shooter, target, currentPlate, metersPerWorldUnit);
        double currentFresh = ResolveRotatingArmorFreshAppearanceBonus(world, shooter, target, currentPlate, metersPerWorldUnit);
        double currentExitPenalty = ResolveRotatingArmorExitPenalty(world, shooter, target, currentPlate, metersPerWorldUnit);
        double currentScore = currentFresh + currentLifetime - currentExitPenalty;
        double bestScore = currentScore + (currentExitPenalty > 60.0 ? 16.0 : 38.0);
        foreach (ArmorPlateTarget candidatePlate in GetAttackableArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec))
        {
            if (candidatePlate.Id.Equals(currentPlate.Id, StringComparison.OrdinalIgnoreCase)
                || !IsRotatingArmorPlate(target, candidatePlate))
            {
                continue;
            }

            double dxWorld = candidatePlate.X - shooter.X;
            double dyWorld = candidatePlate.Y - shooter.Y;
            double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
            if (distanceM > maxDistanceM
                || !IsAutoAimArmorTargetEligible(world, shooter, target, candidatePlate, distanceM)
                || !IsPlateFacingOrEmergingSoon(world, shooter, target, candidatePlate, metersPerWorldUnit, out double lifetimeScore))
            {
                continue;
            }

            double fresh = ResolveRotatingArmorFreshAppearanceBonus(world, shooter, target, candidatePlate, metersPerWorldUnit);
            if (fresh < 62.0 && lifetimeScore < currentLifetime + 28.0)
            {
                continue;
            }

            (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
                world,
                shooter,
                candidatePlate.X,
                candidatePlate.Y,
                candidatePlate.HeightM,
                preferHighArc: heroLobMode && IsHeroStructureAutoAimTargetPlate(world, shooter, target, candidatePlate));
            double yawError = Math.Abs(NormalizeSignedDeg(yawDeg - shooter.TurretYawDeg));
            double pitchError = Math.Abs(pitchDeg - shooter.GimbalPitchDeg);
            double yawDeltaFromCurrent = Math.Abs(NormalizeSignedDeg(yawDeg - shooter.AutoAimSmoothedYawDeg));
            if (!IsWithinAutoAimSearchCone(yawError, pitchError, heroLobMode))
            {
                continue;
            }

            if (yawDeltaFromCurrent > 42.0 && fresh < 120.0)
            {
                continue;
            }

            if (canSeePlate is not null && !canSeePlate(target, candidatePlate))
            {
                continue;
            }

            double exitPenalty = ResolveRotatingArmorExitPenalty(world, shooter, target, candidatePlate, metersPerWorldUnit);
            double score = fresh + lifetimeScore - exitPenalty - yawError * 0.22 - pitchError * 0.36 - yawDeltaFromCurrent * 0.18;
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            preferredPlate = candidatePlate;
        }

        return !string.IsNullOrWhiteSpace(preferredPlate.Id);
    }

    private static bool IsHeroDeploymentTargetPlate(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        if (!IsHeroLobAutoAimMode(shooter))
        {
            return false;
        }

        return IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate);
    }

    private static bool HasLivingEnemyOutpost(SimulationWorldState world, SimulationEntity shooter)
    {
        string enemyTeam = string.Equals(shooter.Team, "red", StringComparison.OrdinalIgnoreCase)
            ? "blue"
            : "red";
        return world.Entities.Any(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && string.Equals(candidate.Team, enemyTeam, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.EntityType, "outpost", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHeroDeploymentOutpostRingPlate(ArmorPlateTarget plate)
        => plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeroDeploymentBaseTopPlate(ArmorPlateTarget plate)
        => plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeroDeploymentHighArcPlate(SimulationEntity target, ArmorPlateTarget plate)
        => (string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
                && IsHeroDeploymentOutpostRingPlate(plate))
            || (string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase)
                && IsHeroDeploymentBaseTopPlate(plate));

    private static double ResolveVisualBodyLiftM(SimulationEntity entity)
    {
        double bodyLift = 0.0;
        if (entity.TraversalActive)
        {
            double progress = Math.Clamp(entity.TraversalProgress, 0.0, 1.0);
            static double Blend(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0.0, 1.0);
            bool heavyChassis =
                string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase);
            if (heavyChassis)
            {
                double stepHeight = Math.Clamp(
                    Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0.0,
                    0.45);
                if (progress < 0.20)
                {
                    bodyLift = (0.02 + stepHeight * 0.10) * (progress / 0.20);
                }
                else if (progress < 0.56)
                {
                    bodyLift = Blend(0.02 + stepHeight * 0.10, 0.09 + stepHeight * 0.18, (progress - 0.20) / 0.36);
                }
                else if (progress < 0.84)
                {
                    bodyLift = Blend(0.09 + stepHeight * 0.18, 0.05 + stepHeight * 0.08, (progress - 0.56) / 0.28);
                }
                else
                {
                    bodyLift = (0.05 + stepHeight * 0.08) * (1.0 - (progress - 0.84) / 0.16);
                }
            }
            else if (progress < 0.4)
            {
                double stepHeight = Math.Clamp(
                    Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0.0,
                    0.32);
                bodyLift = (0.06 + stepHeight * 0.10) * (progress / 0.4);
            }
            else if (progress < 0.7)
            {
                double stepHeight = Math.Clamp(
                    Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0.0,
                    0.32);
                bodyLift = 0.09 + stepHeight * 0.12;
            }
            else
            {
                double stepHeight = Math.Clamp(
                    Math.Max(0.0, entity.TraversalTargetGroundHeightM - entity.TraversalStartGroundHeightM),
                    0.0,
                    0.32);
                bodyLift = (0.09 + stepHeight * 0.12) * (1.0 - (progress - 0.7) / 0.3);
            }
        }

        if (entity.ChassisSupportsJump && entity.AirborneHeightM > 1e-4)
        {
            double jumpWave = Math.Clamp(entity.AirborneHeightM / 0.70, 0.0, 1.0);
            bodyLift = Math.Max(bodyLift, 0.16 * jumpWave);
        }

        return bodyLift;
    }

    private static double ComputePlateLifetimeScore(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double metersPerWorldUnit)
    {
        bool switchingSameTarget = string.Equals(shooter.AutoAimTargetId, target.Id, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(shooter.AutoAimPlateId, plate.Id, StringComparison.OrdinalIgnoreCase);
        double horizonSec = Math.Abs(target.AngularVelocityDegPerSec) > 12.0 || target.SmallGyroActive
            ? 0.36
            : 0.22;
        IReadOnlyList<ArmorPlateTarget> futurePlates = IsStructure(target)
            ? GetAttackableArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec + horizonSec)
            : GetProjectedRobotArmorPlateTargets(target, metersPerWorldUnit, horizonSec);
        ArmorPlateTarget futurePlate = futurePlates
            .FirstOrDefault(candidate => candidate.Id.Equals(plate.Id, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(futurePlate.Id))
        {
            return switchingSameTarget ? -16.0 : -4.0;
        }

        bool futureFacing = IsPlateFacingShooter(shooter, futurePlate);
        if (!futureFacing)
        {
            return switchingSameTarget ? -32.0 : -10.0;
        }

        double currentFace = ComputeFacingMargin(shooter, plate);
        double futureFace = ComputeFacingMargin(shooter, futurePlate);
        double enteringBonus = Math.Clamp(futureFace - currentFace, -0.20, 0.46) * 150.0;
        if (switchingSameTarget && currentFace < 0.40 && futureFace > currentFace + 0.18)
        {
            enteringBonus += 12.0;
        }

        return (switchingSameTarget ? 18.0 : 14.0) + enteringBonus;
    }

    private static bool IsPlateFacingOrEmergingSoon(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double metersPerWorldUnit,
        out double lifetimeScore)
    {
        lifetimeScore = ComputePlateLifetimeScore(world, shooter, target, plate, metersPerWorldUnit);
        if (IsPlateFacingShooter(shooter, plate))
        {
            return true;
        }

        bool rotating = target.SmallGyroActive || Math.Abs(target.AngularVelocityDegPerSec) > 24.0;
        return rotating && lifetimeScore >= 18.0;
    }

    public static double ResolveRotatingArmorFreshAppearanceBonus(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double metersPerWorldUnit)
    {
        if (!IsRotatingArmorPlate(target, plate))
        {
            return 0.0;
        }

        double currentMargin = ComputeFacingMargin(shooter, plate);
        double horizonSec = plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase) ? 0.12 : 0.18;
        if (!TryResolvePlateAtGameTime(
                world,
                target,
                plate,
                metersPerWorldUnit,
                world.GameTimeSec + horizonSec,
                out ArmorPlateTarget futurePlate))
        {
            return currentMargin >= 0.50 ? 36.0 : 0.0;
        }

        double futureMargin = ComputeFacingMargin(shooter, futurePlate);
        double marginTrend = Math.Clamp(futureMargin - currentMargin, -0.40, 0.55);
        bool currentlyFacing = currentMargin >= 0.50;
        if (!currentlyFacing && futureMargin >= 0.50)
        {
            return 155.0 + Math.Max(0.0, marginTrend) * 190.0;
        }

        if (!currentlyFacing)
        {
            return 0.0;
        }

        double edgeFreshness = Math.Clamp((0.84 - currentMargin) / 0.34, 0.0, 1.0);
        double enteringTrend = Math.Clamp(marginTrend / 0.24, 0.0, 1.0);
        double leavingPenalty = marginTrend < -0.08 ? 0.55 : 1.0;
        return edgeFreshness * (88.0 + enteringTrend * 68.0) * leavingPenalty;
    }

    private static double ResolveRotatingArmorExitPenalty(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double metersPerWorldUnit)
    {
        if (!IsRotatingArmorPlate(target, plate))
        {
            return 0.0;
        }

        double currentMargin = ComputeFacingMargin(shooter, plate);
        double horizonSec = plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase) ? 0.12 : 0.18;
        if (!TryResolvePlateAtGameTime(
                world,
                target,
                plate,
                metersPerWorldUnit,
                world.GameTimeSec + horizonSec,
                out ArmorPlateTarget futurePlate))
        {
            return currentMargin >= 0.50 ? 92.0 : 0.0;
        }

        double futureMargin = ComputeFacingMargin(shooter, futurePlate);
        double leavingTrend = Math.Clamp(currentMargin - futureMargin, 0.0, 1.35);
        if (leavingTrend <= 0.035)
        {
            return 0.0;
        }

        double futureBackPenalty = futureMargin < 0.50
            ? Math.Clamp((0.50 - futureMargin) / 0.80, 0.0, 1.0) * 145.0
            : 0.0;
        double currentlyCenteredLeavingPenalty = currentMargin > 0.74
            ? Math.Clamp((currentMargin - 0.74) / 0.26, 0.0, 1.0) * Math.Clamp(leavingTrend / 0.30, 0.0, 1.0) * 56.0
            : 0.0;
        return Math.Clamp(leavingTrend * 150.0 + futureBackPenalty + currentlyCenteredLeavingPenalty, 0.0, 230.0);
    }

    public static bool IsRotatingArmorPlate(SimulationEntity target, ArmorPlateTarget plate)
    {
        if (plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase)
            || plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return plate.Id.StartsWith("armor_", StringComparison.OrdinalIgnoreCase)
            && (target.SmallGyroActive || Math.Abs(target.AngularVelocityDegPerSec) > 24.0);
    }

    public static string ResolveAutoAimTargetKind(SimulationEntity target, ArmorPlateTarget plate)
    {
        if (plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return "energy_disk";
        }

        if (string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
        {
            return "outpost_armor";
        }

        if (string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase))
        {
            return "base_armor";
        }

        return "vehicle_armor";
    }

    public static bool IsArmorAutoAimTargetKind(string? kind)
        => string.Equals(kind, "armor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "vehicle_armor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "base_armor", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolvePlateAtGameTime(
        SimulationWorldState world,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double metersPerWorldUnit,
        double gameTimeSec,
        out ArmorPlateTarget resolvedPlate)
    {
        IReadOnlyList<ArmorPlateTarget> candidates;
        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            string? plateTeam = null;
            SimulationTeamState? teamState = null;
            if (TryParseEnergyArmIndex(plate.Id, out string parsedTeam, out _))
            {
                plateTeam = parsedTeam;
                world.Teams.TryGetValue(parsedTeam, out teamState);
            }

            candidates = GetEnergyMechanismTargets(target, metersPerWorldUnit, gameTimeSec, plateTeam, teamState);
        }
        else if (IsStructure(target))
        {
            candidates = GetAttackableArmorPlateTargets(target, metersPerWorldUnit, gameTimeSec);
        }
        else
        {
            candidates = GetProjectedRobotArmorPlateTargets(
                target,
                metersPerWorldUnit,
                Math.Max(0.0, gameTimeSec - world.GameTimeSec));
        }

        resolvedPlate = candidates.FirstOrDefault(candidate => string.Equals(candidate.Id, plate.Id, StringComparison.OrdinalIgnoreCase));
        return !string.IsNullOrWhiteSpace(resolvedPlate.Id);
    }

    private static double ResolveArmorPlateAreaScore(ArmorPlateTarget plate)
    {
        double width = plate.WidthM > 1e-6 ? plate.WidthM : plate.SideLengthM;
        double height = plate.HeightSpanM > 1e-6 ? plate.HeightSpanM : plate.SideLengthM;
        return Math.Max(0.0016, width * height);
    }

    public static (double X, double Y, double HeightM) ComputeMuzzlePoint(
        SimulationWorldState world,
        SimulationEntity shooter,
        double? pitchDegOverride = null)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double pitchDeg = pitchDegOverride ?? shooter.GimbalPitchDeg;

        double groundHeight = shooter.GroundHeightM + shooter.AirborneHeightM;
        double bodyTopM = shooter.BodyClearanceM + ResolveVisualBodyLiftM(shooter) + shooter.BodyHeightM;
        double hingeHeightM = Math.Max(
            bodyTopM + shooter.GimbalMountGapM + shooter.GimbalMountHeightM,
            shooter.GimbalHeightM > 1e-4
                ? shooter.GimbalHeightM - shooter.GimbalBodyHeightM * 0.5
                : bodyTopM + shooter.GimbalBodyHeightM * 0.5);

        ResolveChassisAxes(shooter.AngleDeg, shooter.ChassisPitchDeg, shooter.ChassisRollDeg, out Vector3 chassisForward, out Vector3 chassisRight, out Vector3 chassisUp);
        ResolveMountedTurretAxes(
            chassisForward,
            chassisRight,
            chassisUp,
            DegreesToRadians(shooter.TurretYawDeg - shooter.AngleDeg),
            DegreesToRadians(pitchDeg),
            out _,
            out _,
            out Vector3 pitchedForward,
            out Vector3 pitchedUp);
        Vector3 chassisOriginM = new(
            (float)(shooter.X * metersPerWorldUnit),
            (float)groundHeight,
            (float)(shooter.Y * metersPerWorldUnit));
        Vector3 hingeM = chassisOriginM
            + chassisForward * (float)shooter.GimbalOffsetXM
            + chassisRight * (float)shooter.GimbalOffsetYM
            + chassisUp * (float)hingeHeightM;
        Vector3 turretCenterM = hingeM
            + pitchedUp * (float)(shooter.GimbalBodyHeightM * 0.50 + 0.006)
            + pitchedForward * (float)(shooter.GimbalLengthM * 0.04);
        Vector3 muzzleM = turretCenterM
            + pitchedForward * (float)(shooter.GimbalLengthM * 0.5 + shooter.BarrelRadiusM * 0.45 + shooter.BarrelLengthM)
            + pitchedUp * (float)(shooter.GimbalBodyHeightM * 0.12 - 0.03);
        return (
            muzzleM.X / metersPerWorldUnit,
            muzzleM.Z / metersPerWorldUnit,
            muzzleM.Y);
    }

    public static (double YawDeg, double PitchDeg) ComputeAimAnglesToPoint(
        SimulationWorldState world,
        SimulationEntity shooter,
        double targetX,
        double targetY,
        double targetHeightM,
        bool preferHighArc = false)
    {
        double pitch = shooter.GimbalPitchDeg;
        double yaw = shooter.TurretYawDeg;
        double speedMps = Math.Max(1.0, ProjectileSpeedMps(shooter));
        bool useDragSolver = preferHighArc
            || string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        for (int iteration = 0; iteration < 3; iteration++)
        {
            (double muzzleX, double muzzleY, double muzzleHeightM) = ComputeMuzzlePoint(world, shooter, pitch);
            double dxWorld = targetX - muzzleX;
            double dyWorld = targetY - muzzleY;
            double horizontalM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * Math.Max(world.MetersPerWorldUnit, 1e-6);
            yaw = NormalizeDeg(RadiansToDegrees(Math.Atan2(dyWorld, dxWorld)));

            double dzM = targetHeightM - muzzleHeightM;
            if (useDragSolver
                && TrySolveBallisticPitchDegWithDrag(horizontalM, dzM, speedMps, shooter.AmmoType, preferHighArc, out double dragPitchDeg))
            {
                pitch = Math.Clamp(dragPitchDeg, -40.0, 40.0);
                continue;
            }

            if (TrySolveVacuumBallisticPitchDeg(horizontalM, dzM, speedMps, preferHighArc, out double vacuumPitchDeg))
            {
                pitch = Math.Clamp(vacuumPitchDeg, -40.0, 40.0);
                continue;
            }

            pitch = Math.Clamp(
                RadiansToDegrees(Math.Atan2(dzM, Math.Max(horizontalM, 1e-4))),
                -40.0,
                40.0);
        }

        return (yaw, pitch);
    }

    private static bool TrySolveVacuumBallisticPitchDeg(
        double horizontalM,
        double dzM,
        double speedMps,
        bool preferHighArc,
        out double pitchDeg)
    {
        pitchDeg = 0.0;
        if (horizontalM <= 1e-5)
        {
            pitchDeg = dzM >= 0.0 ? 40.0 : -40.0;
            return true;
        }

        double speedSq = speedMps * speedMps;
        double discriminant = speedSq * speedSq - GravityMps2 * (GravityMps2 * horizontalM * horizontalM + 2.0 * dzM * speedSq);
        if (discriminant < 0.0)
        {
            return false;
        }

        double sqrt = Math.Sqrt(discriminant);
        double denom = GravityMps2 * horizontalM;
        if (Math.Abs(denom) <= 1e-6)
        {
            return false;
        }

        double lowTan = (speedSq - sqrt) / denom;
        double highTan = (speedSq + sqrt) / denom;
        pitchDeg = RadiansToDegrees(Math.Atan(preferHighArc ? highTan : lowTan));
        return !double.IsNaN(pitchDeg) && !double.IsInfinity(pitchDeg);
    }

    private static bool TrySolveBallisticPitchDegWithDrag(
        double horizontalM,
        double dzM,
        double speedMps,
        string ammoType,
        bool preferHighArc,
        out double pitchDeg)
    {
        pitchDeg = 0.0;
        if (horizontalM <= 1e-5)
        {
            pitchDeg = dzM >= 0.0 ? 40.0 : -40.0;
            return true;
        }

        const int sampleCount = 49;
        Span<double> samplePitches = stackalloc double[sampleCount];
        Span<double> sampleErrors = stackalloc double[sampleCount];
        Span<double> bracketStarts = stackalloc double[sampleCount];
        Span<double> bracketEnds = stackalloc double[sampleCount];
        int bracketCount = 0;
        double minPitch = dzM < -0.08 ? -35.0 : -12.0;
        double maxPitch = 40.0;
        double bestPitch = 0.0;
        double bestAbsError = double.MaxValue;

        for (int index = 0; index < sampleCount; index++)
        {
            double t = sampleCount <= 1 ? 0.0 : index / (double)(sampleCount - 1);
            double candidatePitch = minPitch + (maxPitch - minPitch) * t;
            double error = EvaluateBallisticHeightError(horizontalM, dzM, speedMps, ammoType, DegreesToRadians(candidatePitch));
            samplePitches[index] = candidatePitch;
            sampleErrors[index] = error;
            double absError = Math.Abs(error);
            if (absError < bestAbsError)
            {
                bestAbsError = absError;
                bestPitch = candidatePitch;
            }

            if (index == 0)
            {
                continue;
            }

            double previousError = sampleErrors[index - 1];
            if ((previousError <= 0.0 && error >= 0.0) || (previousError >= 0.0 && error <= 0.0))
            {
                bracketStarts[bracketCount] = samplePitches[index - 1];
                bracketEnds[bracketCount] = candidatePitch;
                bracketCount++;
            }
        }

        if (bracketCount > 0)
        {
            int bracketIndex = preferHighArc ? bracketCount - 1 : 0;
            double lowPitch = bracketStarts[bracketIndex];
            double highPitch = bracketEnds[bracketIndex];
            double lowError = EvaluateBallisticHeightError(horizontalM, dzM, speedMps, ammoType, DegreesToRadians(lowPitch));
            for (int iteration = 0; iteration < 16; iteration++)
            {
                double midPitch = (lowPitch + highPitch) * 0.5;
                double midError = EvaluateBallisticHeightError(horizontalM, dzM, speedMps, ammoType, DegreesToRadians(midPitch));
                if ((lowError <= 0.0 && midError >= 0.0) || (lowError >= 0.0 && midError <= 0.0))
                {
                    highPitch = midPitch;
                }
                else
                {
                    lowPitch = midPitch;
                    lowError = midError;
                }
            }

            pitchDeg = (lowPitch + highPitch) * 0.5;
            return true;
        }

        pitchDeg = bestPitch;
        return bestAbsError < Math.Max(0.30, horizontalM * 0.12);
    }

    private static double EvaluateBallisticHeightError(
        double horizontalM,
        double dzM,
        double speedMps,
        string ammoType,
        double pitchRad)
    {
        double vxMps = speedMps * Math.Cos(pitchRad);
        double vzMps = speedMps * Math.Sin(pitchRad);
        if (vxMps <= 1e-5)
        {
            return -dzM;
        }

        double xM = 0.0;
        double zM = 0.0;
        double previousXM = 0.0;
        double previousZM = 0.0;
        double diameterM = ProjectileDiameterM(ammoType);
        double areaM2 = Math.PI * diameterM * diameterM * 0.25;
        double massKg = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 0.041 : 0.0032;
        double dt = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 1.0 / 240.0 : 1.0 / 200.0;
        int maxSteps = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 840 : 520;
        for (int step = 0; step < maxSteps; step++)
        {
            previousXM = xM;
            previousZM = zM;

            double speed = Math.Sqrt(vxMps * vxMps + vzMps * vzMps);
            if (speed > 1e-6)
            {
                double dragCoefficient = 0.47;
                double airDensityKgM3 = 1.20;
                double dragAccelMps2 = 0.5 * airDensityKgM3 * dragCoefficient * areaM2 * speed * speed / Math.Max(0.001, massKg);
                dragAccelMps2 = Math.Min(dragAccelMps2, speed / Math.Max(dt, 1e-6) * 0.72);
                double dragStep = dragAccelMps2 * dt / speed;
                vxMps -= vxMps * dragStep;
                vzMps -= vzMps * dragStep;
            }

            vzMps -= GravityMps2 * dt;
            xM += vxMps * dt;
            zM += vzMps * dt;

            if (xM >= horizontalM)
            {
                double segment = Math.Max(xM - previousXM, 1e-6);
                double lerp = Math.Clamp((horizontalM - previousXM) / segment, 0.0, 1.0);
                double sampledZM = previousZM + (zM - previousZM) * lerp;
                return sampledZM - dzM;
            }

            if (vxMps <= 1e-4 || (zM < dzM - 6.0 && vzMps < 0.0))
            {
                break;
            }
        }

        return zM - dzM;
    }

    public static (double YawDeg, double PitchDeg, double Accuracy) ComputeAutoAimAnglesWithError(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double maxDistanceM)
    {
        AutoAimSolution solution = ComputeAutoAimSolution(world, shooter, target, plate, maxDistanceM);
        return (solution.YawDeg, solution.PitchDeg, solution.Accuracy);
    }

    public static AutoAimSolution ComputeAutoAimSolution(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double maxDistanceM)
    {
        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return ComputeEnergyMechanismAutoAimSolution(world, shooter, target, plate, maxDistanceM);
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double dxWorld = plate.X - shooter.X;
        double dyWorld = plate.Y - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        (double observedVxWorld, double observedVyWorld, double observedVzMps) =
            ResolveArmorPlatePointObservedVelocityWorld(world, target, plate);
        double observedOmegaRadPerSec = ResolveAutoAimAngularVelocityRadPerSec(target);
        double heightCompensationM = ResolveLargeProjectileAutoAimHeightCompensation(shooter, distanceM);
        (double predictedX, double predictedY, double predictedHeightM) = PredictArmorPlatePoint(
            world,
            shooter,
            target,
            plate,
            distanceM,
            heightCompensationM,
            out double leadTimeSec,
            out double leadDistanceM);
        double distanceScaleMaxDistanceM = IsHeroNormalAutoAimMode(shooter)
            ? Math.Min(maxDistanceM, HeroNormalAutoAimMaxDistanceM)
            : maxDistanceM;
        double distanceCoefficient = ComputeAutoAimDistanceCoefficient(distanceM, distanceScaleMaxDistanceM);
        double motionCoefficient = ComputeAutoAimMotionCoefficient(world, shooter, target);
        if (target.AutoAimInstabilityTimerSec > 1e-6)
        {
            motionCoefficient *= 0.50;
        }

        double accuracyScale = Math.Clamp(shooter.AutoAimAccuracyScale <= 1e-6 ? 1.0 : shooter.AutoAimAccuracyScale, 0.05, 1.0);
        double accuracy = Math.Clamp(distanceCoefficient * motionCoefficient * accuracyScale, 0.05, 1.0);
        bool heroStructureHighArcTarget = IsHeroLobAutoAimMode(shooter)
            && IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate);
        bool criticalStructureTarget = ShouldAimStructureCriticalZone(target, plate);
        if (IsAutoAimTargetEffectivelyStatic(world, shooter, target, plate))
        {
            (double centerYaw, double centerPitch) = ComputeAimAnglesToPoint(
                world,
                shooter,
                plate.X,
                plate.Y,
                plate.HeightM + heightCompensationM,
                heroStructureHighArcTarget);
            return EnrichAutoAimSolution(
                world,
                new AutoAimSolution(
                centerYaw,
                centerPitch,
                1.0,
                distanceCoefficient,
                motionCoefficient,
                0.0,
                0.0,
                DescribeArmorPlateDirection(target, plate)),
                plate.X,
                plate.Y,
                plate.HeightM + heightCompensationM,
                observedVxWorld,
                observedVyWorld,
                observedVzMps,
                observedOmegaRadPerSec);
        }

        if (criticalStructureTarget)
        {
            (double criticalYaw, double criticalPitch) = ComputeAimAnglesToPoint(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                heroStructureHighArcTarget);

            return EnrichAutoAimSolution(
                world,
                new AutoAimSolution(
                criticalYaw,
                criticalPitch,
                accuracy,
                distanceCoefficient,
                motionCoefficient,
                leadTimeSec,
                leadDistanceM,
                DescribeArmorPlateDirection(target, plate)),
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                observedVxWorld,
                observedVyWorld,
                observedVzMps,
                observedOmegaRadPerSec);
        }

        if (accuracy >= 0.999)
        {
            (double perfectYaw, double perfectPitch) = ComputeAimAnglesToPoint(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                heroStructureHighArcTarget);

            return EnrichAutoAimSolution(
                world,
                new AutoAimSolution(
                perfectYaw,
                perfectPitch,
                1.0,
                distanceCoefficient,
                motionCoefficient,
                leadTimeSec,
                leadDistanceM,
                DescribeArmorPlateDirection(target, plate)),
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                observedVxWorld,
                observedVyWorld,
                observedVzMps,
                observedOmegaRadPerSec);
        }

        double errorRatio = 1.0 - accuracy;
        int seed = StableHash(shooter.Id, target.Id, plate.Id);
        double sideNoise = ResolveAutoAimLateralErrorSign(
            world,
            shooter,
            target,
            plate,
            predictedX,
            predictedY,
            StableSignedUnit(seed));
        double heightNoise = StableSignedUnit(seed ^ 0x5A17);
        double lateralErrorM = errorRatio * Math.Clamp(distanceM * 0.075, 0.025, 0.42) * sideNoise;
        double heightErrorM = errorRatio * Math.Clamp(0.08 + distanceM * 0.04, 0.08, 0.36) * heightNoise;
        double plateYawRad = Math.Atan2(predictedY - shooter.Y, predictedX - shooter.X);
        double sideXWorld = -Math.Sin(plateYawRad) * lateralErrorM / metersPerWorldUnit;
        double sideYWorld = Math.Cos(plateYawRad) * lateralErrorM / metersPerWorldUnit;

        // 弹道求解仍负责重力；误差只施加到附近虚拟点，避免自瞄总是钉死装甲板中心。
        (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
            world,
            shooter,
            predictedX + sideXWorld,
            predictedY + sideYWorld,
            predictedHeightM + heightCompensationM + heightErrorM,
            heroStructureHighArcTarget);

        return EnrichAutoAimSolution(
            world,
            new AutoAimSolution(
            yawDeg,
            pitchDeg,
            accuracy,
            distanceCoefficient,
            motionCoefficient,
            leadTimeSec,
            leadDistanceM,
            DescribeArmorPlateDirection(target, plate)),
            predictedX + sideXWorld,
            predictedY + sideYWorld,
            predictedHeightM + heightCompensationM + heightErrorM,
            observedVxWorld,
            observedVyWorld,
            observedVzMps,
            observedOmegaRadPerSec);
    }

    public static AutoAimSolution ComputeObservationDrivenAutoAimSolution(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double maxDistanceM,
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double observedVelocityXMps,
        double observedVelocityYMps,
        double observedVelocityZMps,
        double observedAngularVelocityRadPerSec)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double dxWorld = observedXWorld - shooter.X;
        double dyWorld = observedYWorld - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        double heightCompensationM = ResolveLargeProjectileAutoAimHeightCompensation(shooter, distanceM);
        (double predictedX, double predictedY, double predictedHeightM) = PredictObservationDrivenArmorPlatePoint(
            world,
            shooter,
            target,
            plate,
            observedXWorld,
            observedYWorld,
            observedHeightM,
            observedVelocityXMps,
            observedVelocityYMps,
            observedVelocityZMps,
            distanceM,
            heightCompensationM,
            out double leadTimeSec,
            out double leadDistanceM);
        double distanceScaleMaxDistanceM = IsHeroNormalAutoAimMode(shooter)
            ? Math.Min(maxDistanceM, HeroNormalAutoAimMaxDistanceM)
            : maxDistanceM;
        double distanceCoefficient = ComputeAutoAimDistanceCoefficient(distanceM, distanceScaleMaxDistanceM);
        double motionCoefficient = ComputeAutoAimObservationMotionCoefficient(
            world,
            shooter,
            target,
            observedVelocityXMps,
            observedVelocityYMps,
            observedVelocityZMps,
            observedAngularVelocityRadPerSec);
        if (target.AutoAimInstabilityTimerSec > 1e-6)
        {
            motionCoefficient *= 0.50;
        }

        double accuracyScale = Math.Clamp(shooter.AutoAimAccuracyScale <= 1e-6 ? 1.0 : shooter.AutoAimAccuracyScale, 0.05, 1.0);
        double accuracy = Math.Clamp(distanceCoefficient * motionCoefficient * accuracyScale, 0.08, 1.0);
        bool heroStructureHighArcTarget = IsHeroLobAutoAimMode(shooter)
            && IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate);
        bool criticalStructureTarget = ShouldAimStructureCriticalZone(target, plate);
        if (IsObservationDrivenTargetEffectivelyStatic(
            world,
            shooter,
            target,
            plate,
            observedVelocityXMps,
            observedVelocityYMps,
            observedVelocityZMps,
            observedAngularVelocityRadPerSec))
        {
            (double centerYaw, double centerPitch) = ComputeAimAnglesToPoint(
                world,
                shooter,
                observedXWorld,
                observedYWorld,
                observedHeightM + heightCompensationM,
                heroStructureHighArcTarget);
            return EnrichAutoAimSolution(
                world,
                new AutoAimSolution(
                    centerYaw,
                    centerPitch,
                    1.0,
                    distanceCoefficient,
                    motionCoefficient,
                    0.0,
                    0.0,
                    DescribeArmorPlateDirection(target, plate)),
                observedXWorld,
                observedYWorld,
                observedHeightM + heightCompensationM,
                observedVelocityXMps / metersPerWorldUnit,
                observedVelocityYMps / metersPerWorldUnit,
                observedVelocityZMps,
                observedAngularVelocityRadPerSec);
        }

        if (criticalStructureTarget || accuracy >= 0.999)
        {
            (double perfectYaw, double perfectPitch) = ComputeAimAnglesToPoint(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                heroStructureHighArcTarget);

            return EnrichAutoAimSolution(
                world,
                new AutoAimSolution(
                    perfectYaw,
                    perfectPitch,
                    criticalStructureTarget ? Math.Max(accuracy, 0.999) : accuracy,
                    distanceCoefficient,
                    motionCoefficient,
                    leadTimeSec,
                    leadDistanceM,
                    DescribeArmorPlateDirection(target, plate)),
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                observedVelocityXMps / metersPerWorldUnit,
                observedVelocityYMps / metersPerWorldUnit,
                observedVelocityZMps,
                observedAngularVelocityRadPerSec);
        }

        double errorRatio = 1.0 - accuracy;
        int seed = StableHash(shooter.Id, target.Id, plate.Id);
        double sideNoise = ResolveAutoAimLateralErrorSign(
            world,
            shooter,
            target,
            plate,
            predictedX,
            predictedY,
            StableSignedUnit(seed));
        double heightNoise = StableSignedUnit(seed ^ 0x5A17);
        double lateralErrorM = errorRatio * Math.Clamp(distanceM * 0.055, 0.015, 0.26) * sideNoise;
        double heightErrorM = errorRatio * Math.Clamp(0.05 + distanceM * 0.025, 0.05, 0.18) * heightNoise;
        double plateYawRad = Math.Atan2(predictedY - shooter.Y, predictedX - shooter.X);
        double sideXWorld = -Math.Sin(plateYawRad) * lateralErrorM / metersPerWorldUnit;
        double sideYWorld = Math.Cos(plateYawRad) * lateralErrorM / metersPerWorldUnit;
        (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
            world,
            shooter,
            predictedX + sideXWorld,
            predictedY + sideYWorld,
            predictedHeightM + heightCompensationM + heightErrorM,
            heroStructureHighArcTarget);

        return EnrichAutoAimSolution(
            world,
            new AutoAimSolution(
                yawDeg,
                pitchDeg,
                accuracy,
                distanceCoefficient,
                motionCoefficient,
                leadTimeSec,
                leadDistanceM,
                DescribeArmorPlateDirection(target, plate)),
            predictedX + sideXWorld,
            predictedY + sideYWorld,
            predictedHeightM + heightCompensationM + heightErrorM,
            observedVelocityXMps / metersPerWorldUnit,
            observedVelocityYMps / metersPerWorldUnit,
            observedVelocityZMps,
            observedAngularVelocityRadPerSec);
    }

    public static AutoAimSolution ComputeObservationDrivenAutoAimSolutionThirdOrderEkf(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double maxDistanceM,
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double observedVelocityXMps,
        double observedVelocityYMps,
        double observedVelocityZMps,
        double observedAccelerationXMps2,
        double observedAccelerationYMps2,
        double observedAccelerationZMps2,
        double observedAngularVelocityRadPerSec)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double dxWorld = observedXWorld - shooter.X;
        double dyWorld = observedYWorld - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        double heightCompensationM = ResolveLargeProjectileAutoAimHeightCompensation(shooter, distanceM);
        (double predictedX, double predictedY, double predictedHeightM) = PredictObservationDrivenArmorPlatePointThirdOrderEkf(
            world,
            shooter,
            target,
            plate,
            observedXWorld,
            observedYWorld,
            observedHeightM,
            observedVelocityXMps,
            observedVelocityYMps,
            observedVelocityZMps,
            observedAccelerationXMps2,
            observedAccelerationYMps2,
            observedAccelerationZMps2,
            distanceM,
            heightCompensationM,
            out double leadTimeSec,
            out double leadDistanceM);
        double distanceScaleMaxDistanceM = IsHeroNormalAutoAimMode(shooter)
            ? Math.Min(maxDistanceM, HeroNormalAutoAimMaxDistanceM)
            : maxDistanceM;
        double distanceCoefficient = ComputeAutoAimDistanceCoefficient(distanceM, distanceScaleMaxDistanceM);
        double motionCoefficient = ComputeAutoAimObservationMotionCoefficient(
            world,
            shooter,
            target,
            observedVelocityXMps,
            observedVelocityYMps,
            observedVelocityZMps,
            observedAngularVelocityRadPerSec);
        double accelerationMagnitudeMps2 = Math.Sqrt(
            observedAccelerationXMps2 * observedAccelerationXMps2
            + observedAccelerationYMps2 * observedAccelerationYMps2
            + observedAccelerationZMps2 * observedAccelerationZMps2);
        motionCoefficient -= Math.Clamp(accelerationMagnitudeMps2 * 0.006, 0.0, 0.08);
        motionCoefficient = Math.Clamp(motionCoefficient, 0.72, 1.0);
        if (target.AutoAimInstabilityTimerSec > 1e-6)
        {
            motionCoefficient *= 0.50;
        }

        double accuracyScale = Math.Clamp(shooter.AutoAimAccuracyScale <= 1e-6 ? 1.0 : shooter.AutoAimAccuracyScale, 0.05, 1.0);
        double accuracy = Math.Clamp(distanceCoefficient * motionCoefficient * accuracyScale, 0.08, 1.0);
        bool heroStructureHighArcTarget = IsHeroLobAutoAimMode(shooter)
            && IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate);
        bool criticalStructureTarget = ShouldAimStructureCriticalZone(target, plate);
        bool effectivelyStatic = IsObservationDrivenTargetEffectivelyStatic(
                world,
                shooter,
                target,
                plate,
                observedVelocityXMps,
                observedVelocityYMps,
                observedVelocityZMps,
                observedAngularVelocityRadPerSec)
            && accelerationMagnitudeMps2 <= 0.85;
        if (effectivelyStatic)
        {
            (double centerYaw, double centerPitch) = ComputeAimAnglesToPoint(
                world,
                shooter,
                observedXWorld,
                observedYWorld,
                observedHeightM + heightCompensationM,
                heroStructureHighArcTarget);
            return EnrichAutoAimSolution(
                world,
                new AutoAimSolution(
                    centerYaw,
                    centerPitch,
                    1.0,
                    distanceCoefficient,
                    motionCoefficient,
                    0.0,
                    0.0,
                    DescribeArmorPlateDirection(target, plate)),
                observedXWorld,
                observedYWorld,
                observedHeightM + heightCompensationM,
                observedVelocityXMps / metersPerWorldUnit,
                observedVelocityYMps / metersPerWorldUnit,
                observedVelocityZMps,
                observedAngularVelocityRadPerSec);
        }

        if (criticalStructureTarget || accuracy >= 0.999)
        {
            (double perfectYaw, double perfectPitch) = ComputeAimAnglesToPoint(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                heroStructureHighArcTarget);

            return EnrichAutoAimSolution(
                world,
                new AutoAimSolution(
                    perfectYaw,
                    perfectPitch,
                    criticalStructureTarget ? Math.Max(accuracy, 0.999) : accuracy,
                    distanceCoefficient,
                    motionCoefficient,
                    leadTimeSec,
                    leadDistanceM,
                    DescribeArmorPlateDirection(target, plate)),
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                observedVelocityXMps / metersPerWorldUnit,
                observedVelocityYMps / metersPerWorldUnit,
                observedVelocityZMps,
                observedAngularVelocityRadPerSec);
        }

        double errorRatio = 1.0 - accuracy;
        int seed = StableHash(shooter.Id, target.Id, plate.Id, "ekf3");
        double sideNoise = ResolveAutoAimLateralErrorSign(
            world,
            shooter,
            target,
            plate,
            predictedX,
            predictedY,
            StableSignedUnit(seed));
        double heightNoise = StableSignedUnit(seed ^ 0x5A17);
        double lateralErrorM = errorRatio * Math.Clamp(distanceM * 0.055, 0.015, 0.24) * sideNoise;
        double heightErrorM = errorRatio * Math.Clamp(0.05 + distanceM * 0.022, 0.05, 0.16) * heightNoise;
        double plateYawRad = Math.Atan2(predictedY - shooter.Y, predictedX - shooter.X);
        double sideXWorld = -Math.Sin(plateYawRad) * lateralErrorM / metersPerWorldUnit;
        double sideYWorld = Math.Cos(plateYawRad) * lateralErrorM / metersPerWorldUnit;
        (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
            world,
            shooter,
            predictedX + sideXWorld,
            predictedY + sideYWorld,
            predictedHeightM + heightCompensationM + heightErrorM,
            heroStructureHighArcTarget);

        return EnrichAutoAimSolution(
            world,
            new AutoAimSolution(
                yawDeg,
                pitchDeg,
                accuracy,
                distanceCoefficient,
                motionCoefficient,
                leadTimeSec,
                leadDistanceM,
                DescribeArmorPlateDirection(target, plate)),
            predictedX + sideXWorld,
            predictedY + sideYWorld,
            predictedHeightM + heightCompensationM + heightErrorM,
            observedVelocityXMps / metersPerWorldUnit,
            observedVelocityYMps / metersPerWorldUnit,
            observedVelocityZMps,
            observedAngularVelocityRadPerSec);
    }
    private static AutoAimSolution ComputeEnergyMechanismAutoAimSolution(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double maxDistanceM)
    {
        ArmorPlateTarget visualObservationPlate = ResolveEnergyObservationRingPlate(world, shooter, target, plate, world.GameTimeSec);
        (double observedVxWorld, double observedVyWorld, double observedVzMps) =
            ResolveArmorPlatePointObservedVelocityWorld(world, target, visualObservationPlate);
        double observedOmegaRadPerSec = ResolveAutoAimAngularVelocityRadPerSec(target);
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        return ComputeObservationDrivenAutoAimSolutionThirdOrderEkf(
            world,
            shooter,
            target,
            plate,
            maxDistanceM,
            visualObservationPlate.X,
            visualObservationPlate.Y,
            visualObservationPlate.HeightM,
            observedVxWorld * metersPerWorldUnit,
            observedVyWorld * metersPerWorldUnit,
            observedVzMps,
            0.0,
            0.0,
            0.0,
            observedOmegaRadPerSec);
    }
    private static AutoAimSolution EnrichAutoAimSolution(
        SimulationWorldState world,
        AutoAimSolution solution,
        double aimPointXWorld,
        double aimPointYWorld,
        double aimPointHeightM,
        double observedVelocityXWorldPerSec,
        double observedVelocityYWorldPerSec,
        double observedVelocityZMps,
        double observedAngularVelocityRadPerSec)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        return solution with
        {
            AimPointX = aimPointXWorld,
            AimPointY = aimPointYWorld,
            AimPointHeightM = aimPointHeightM,
            ObservedVelocityXMps = observedVelocityXWorldPerSec * metersPerWorldUnit,
            ObservedVelocityYMps = observedVelocityYWorldPerSec * metersPerWorldUnit,
            ObservedVelocityZMps = observedVelocityZMps,
            ObservedAngularVelocityRadPerSec = observedAngularVelocityRadPerSec,
        };
    }

    private static double ResolveLargeProjectileAutoAimHeightCompensation(SimulationEntity shooter, double distanceM)
    {
        if (!string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            return 0.0;
        }

        double projectileRadiusM = ProjectileDiameterM(shooter.AmmoType) * 0.5;
        double cameraBarrelAxisOffsetM = 0.04;
        double dragSafetyM = Math.Clamp(distanceM * 0.006, 0.0, 0.07);
        return Math.Clamp(projectileRadiusM + cameraBarrelAxisOffsetM + dragSafetyM, 0.045, 0.12);
    }

    public static bool TryFindProjectileHit(
        SimulationWorldState world,
        SimulationEntity shooter,
        double startX,
        double startY,
        double startHeightM,
        double endX,
        double endY,
        double endHeightM,
        IReadOnlyList<SimulationEntity>? candidateTargets,
        out SimulationEntity? hitTarget,
        out ArmorPlateTarget hitPlate,
        out double hitX,
        out double hitY,
        out double hitHeightM,
        out double hitSegmentT)
        => TryFindProjectileHit(
            world,
            shooter,
            startX,
            startY,
            startHeightM,
            endX,
            endY,
            endHeightM,
            candidateTargets,
            null,
            out hitTarget,
            out hitPlate,
            out hitX,
            out hitY,
            out hitHeightM,
            out hitSegmentT);

    public static bool TryFindProjectileHit(
        SimulationWorldState world,
        SimulationEntity shooter,
        double startX,
        double startY,
        double startHeightM,
        double endX,
        double endY,
        double endHeightM,
        IReadOnlyList<SimulationEntity>? candidateTargets,
        IReadOnlyDictionary<string, IReadOnlyList<ArmorPlateTarget>>? cachedArmorTargets,
        out SimulationEntity? hitTarget,
        out ArmorPlateTarget hitPlate,
        out double hitX,
        out double hitY,
        out double hitHeightM,
        out double hitSegmentT)
    {
        hitTarget = null;
        hitPlate = default;
        hitX = 0;
        hitY = 0;
        hitHeightM = 0;
        hitSegmentT = 1.0;

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double projectileRadiusM = ProjectileDiameterM(shooter.AmmoType) * 0.5;
        double bestSegmentT = double.MaxValue;
        Vector3 segmentStart = new((float)(startX * metersPerWorldUnit), (float)startHeightM, (float)(startY * metersPerWorldUnit));
        Vector3 segmentEnd = new((float)(endX * metersPerWorldUnit), (float)endHeightM, (float)(endY * metersPerWorldUnit));
        IReadOnlyList<SimulationEntity> candidates = candidateTargets ?? (IReadOnlyList<SimulationEntity>)world.Entities;

        foreach (SimulationEntity candidate in candidates)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || string.Equals(candidate.Id, shooter.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ProjectileCollisionBroadphase.MayIntersectTargetBounds(
                    candidate,
                    metersPerWorldUnit,
                    projectileRadiusM,
                    segmentStart,
                    segmentEnd))
            {
                continue;
            }

            IReadOnlyList<ArmorPlateTarget> armorTargets = cachedArmorTargets is not null
                && cachedArmorTargets.TryGetValue(candidate.Id, out IReadOnlyList<ArmorPlateTarget>? cachedTargets)
                    ? cachedTargets
                    : GetAttackableArmorPlateTargets(candidate, metersPerWorldUnit, world.GameTimeSec);
            bool energyMechanismTarget = string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
            world.Teams.TryGetValue(shooter.Team, out SimulationTeamState? shooterTeamState);
            bool restrictToCurrentEnergyTarget = energyMechanismTarget
                && shooterTeamState is not null
                && string.Equals(shooterTeamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
                && shooterTeamState.EnergyNextModuleDelaySec <= 1e-6
                && shooterTeamState.EnergyCurrentLitMask != 0;
            IReadOnlyList<ArmorPlateTarget> collisionTargets = energyMechanismTarget
                ? GetEnergyMechanismCollisionPlates(armorTargets, shooter.Team, shooterTeamState, restrictToCurrentEnergyTarget)
                : armorTargets;
            foreach (ArmorPlateTarget plate in collisionTargets)
            {
                double plateSegmentT;
                Vector3 hitPoint;
                bool hit = energyMechanismTarget
                    ? TryIntersectProjectileWithEnergyDisk(
                        plate,
                        metersPerWorldUnit,
                        projectileRadiusM,
                        segmentStart,
                        segmentEnd,
                        out plateSegmentT,
                        out hitPoint)
                    : TryIntersectProjectileWithArmorPlate(
                        plate,
                        metersPerWorldUnit,
                        projectileRadiusM,
                        segmentStart,
                        segmentEnd,
                        out plateSegmentT,
                        out hitPoint);
                if (!hit)
                {
                    continue;
                }

                if (plateSegmentT >= bestSegmentT)
                {
                    continue;
                }

                bestSegmentT = plateSegmentT;
                hitTarget = candidate;
                hitPlate = plate;
                hitX = hitPoint.X / metersPerWorldUnit;
                hitY = hitPoint.Z / metersPerWorldUnit;
                hitHeightM = hitPoint.Y;
                hitSegmentT = Math.Clamp(plateSegmentT, 0.0, 1.0);
            }
        }

        return hitTarget is not null;
    }

    private static IReadOnlyList<ArmorPlateTarget> GetEnergyMechanismCollisionPlates(
        IReadOnlyList<ArmorPlateTarget> armorTargets,
        string shooterTeam,
        SimulationTeamState? shooterTeamState,
        bool restrictToCurrentEnergyTarget)
    {
        var selectedByArm = new Dictionary<string, ArmorPlateTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (ArmorPlateTarget plate in armorTargets)
        {
            if (!TryParseEnergyArmIndex(plate.Id, out string plateTeam, out int armIndex))
            {
                continue;
            }

            if (restrictToCurrentEnergyTarget
                && (!string.Equals(plateTeam, shooterTeam, StringComparison.OrdinalIgnoreCase)
                    || shooterTeamState is null
                    || (shooterTeamState.EnergyCurrentLitMask & (1 << armIndex)) == 0))
            {
                continue;
            }

            string key = $"{plateTeam}:{armIndex}";
            if (!selectedByArm.TryGetValue(key, out ArmorPlateTarget existing)
                || IsPreferredEnergyCollisionPlate(plate, existing))
            {
                selectedByArm[key] = plate;
            }
        }

        return selectedByArm.Count == 0
            ? Array.Empty<ArmorPlateTarget>()
            : selectedByArm.Values
                .OrderBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static bool IsPreferredEnergyCollisionPlate(ArmorPlateTarget candidate, ArmorPlateTarget current)
    {
        int candidateRing = ResolveEnergyRingScore(candidate);
        int currentRing = ResolveEnergyRingScore(current);
        if (candidateRing == 1 && currentRing != 1)
        {
            return true;
        }

        if (candidateRing != 1 && currentRing == 1)
        {
            return false;
        }

        double candidateDiameterM = Math.Max(ResolveArmorPlateHitWidthM(candidate), ResolveArmorPlateHitHeightM(candidate));
        double currentDiameterM = Math.Max(ResolveArmorPlateHitWidthM(current), ResolveArmorPlateHitHeightM(current));
        if (Math.Abs(candidateDiameterM - currentDiameterM) > 1e-6)
        {
            return candidateDiameterM > currentDiameterM;
        }

        return candidateRing < currentRing;
    }

    public static Vector3 ResolveArmorPlateNormal(ArmorPlateTarget plate)
    {
        double yawRad = DegreesToRadians(plate.YawDeg);
        Vector3 forward = new((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));
        if (plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase))
        {
            double tiltRad = DegreesToRadians(BaseTopArmorTiltDeg);
            return Vector3.Normalize(forward * (float)Math.Sin(tiltRad) + Vector3.UnitY * (float)Math.Cos(tiltRad));
        }

        if (plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return Vector3.Normalize(forward + Vector3.UnitY);
        }

        return forward;
    }

    public static bool IsCriticalStructureArmorHit(ArmorPlateTarget plate, double metersPerWorldUnit, Vector3 hitPoint)
    {
        if (!IsCriticalStructurePlate(plate))
        {
            return false;
        }

        Vector3 center = new(
            (float)(plate.X * metersPerWorldUnit),
            (float)plate.HeightM,
            (float)(plate.Y * metersPerWorldUnit));
        double yawRad = DegreesToRadians(plate.YawDeg);
        Vector3 normal = ResolveArmorPlateNormal(plate);
        Vector3 side = new(-(float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
        Vector3 up = plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase)
            ? Vector3.Normalize(Vector3.Cross(side, normal))
            : Vector3.UnitY;
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.UnitY;
        }

        Vector3 local = hitPoint - center;
        const float halfCriticalSideM = 0.005f;
        return MathF.Abs(Vector3.Dot(local, side)) <= halfCriticalSideM
            && MathF.Abs(Vector3.Dot(local, up)) <= halfCriticalSideM;
    }

    private static bool TryIntersectProjectileWithArmorPlate(
        ArmorPlateTarget plate,
        double metersPerWorldUnit,
        double projectileRadiusM,
        Vector3 segmentStart,
        Vector3 segmentEnd,
        out double segmentT,
        out Vector3 hitPoint)
    {
        segmentT = 1.0;
        hitPoint = default;
        Vector3 center = new(
            (float)(plate.X * metersPerWorldUnit),
            (float)plate.HeightM,
            (float)(plate.Y * metersPerWorldUnit));
        Vector3 segment = segmentEnd - segmentStart;
        float segmentLengthSq = segment.LengthSquared();
        if (segmentLengthSq <= 1e-9f)
        {
            return false;
        }

        Vector3 normal = ResolveArmorPlateNormal(plate);
        if (!IsProjectileApproachingArmorFrontFace(normal, segment))
        {
            return false;
        }

        double yawRad = DegreesToRadians(plate.YawDeg);
        Vector3 side = new(-(float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
        Vector3 up = plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase)
            ? Vector3.Normalize(Vector3.Cross(side, normal))
            : Vector3.UnitY;
        if (up.LengthSquared() <= 1e-8f)
        {
            up = Vector3.UnitY;
        }

        float denom = Vector3.Dot(segment, normal);
        if (Math.Abs(denom) > 1e-7f)
        {
            float t = Vector3.Dot(center - segmentStart, normal) / denom;
            if (t >= -0.025f && t <= 1.025f)
            {
                Vector3 candidate = segmentStart + segment * Math.Clamp(t, 0f, 1f);
                Vector3 local = candidate - center;
                float halfWidth = (float)Math.Clamp(ResolveArmorPlateHitWidthM(plate) * 0.5, 0.015, 0.30);
                float halfHeight = (float)Math.Clamp(ResolveArmorPlateHitHeightM(plate) * 0.5, 0.015, 0.30);
                float tolerance = (float)ResolveProjectilePlateToleranceM(plate, projectileRadiusM);
                if (Math.Abs(Vector3.Dot(local, side)) <= halfWidth + tolerance
                    && Math.Abs(Vector3.Dot(local, up)) <= halfHeight + tolerance)
                {
                    segmentT = Math.Clamp(t, 0.0f, 1.0f);
                    hitPoint = candidate;
                    return true;
                }
            }
        }

        (double distanceSq, Vector3 closest) = PointToSegmentDistanceSquared(center, segmentStart, segmentEnd);
        double plateHitRadiusM = ResolveProjectilePlateFallbackRadiusM(plate, projectileRadiusM);
        if (distanceSq > plateHitRadiusM * plateHitRadiusM)
        {
            return false;
        }

        hitPoint = closest;
        segmentT = Math.Clamp(Vector3.Dot(closest - segmentStart, segment) / segmentLengthSq, 0.0, 1.0);
        return true;
    }

    private static bool IsProjectileApproachingArmorFrontFace(Vector3 normal, Vector3 segment)
    {
        if (normal.LengthSquared() <= 1e-8f || segment.LengthSquared() <= 1e-9f)
        {
            return true;
        }

        float approach = Vector3.Dot(Vector3.Normalize(segment), Vector3.Normalize(normal));
        return approach <= -0.05f;
    }

    private static bool TryIntersectProjectileWithEnergyDisk(
        ArmorPlateTarget plate,
        double metersPerWorldUnit,
        double projectileRadiusM,
        Vector3 segmentStart,
        Vector3 segmentEnd,
        out double segmentT,
        out Vector3 hitPoint)
    {
        segmentT = 1.0;
        hitPoint = default;
        Vector3 center = new(
            (float)(plate.X * metersPerWorldUnit),
            (float)plate.HeightM,
            (float)(plate.Y * metersPerWorldUnit));
        Vector3 segment = segmentEnd - segmentStart;
        float segmentLengthSq = segment.LengthSquared();
        if (segmentLengthSq <= 1e-9f)
        {
            return false;
        }

        Vector3 normal = Vector3.Normalize(ResolveArmorPlateNormal(plate));
        double yawRad = DegreesToRadians(plate.YawDeg);
        Vector3 side = new(-(float)Math.Sin(yawRad), 0f, (float)Math.Cos(yawRad));
        Vector3 up = Vector3.UnitY;
        if (side.LengthSquared() <= 1e-8f)
        {
            side = Vector3.Normalize(Vector3.Cross(up, normal));
        }

        if (side.LengthSquared() <= 1e-8f)
        {
            side = Vector3.UnitZ;
        }

        side = Vector3.Normalize(side);
        float diskRadiusM = (float)Math.Max(0.035, Math.Max(ResolveArmorPlateHitWidthM(plate), ResolveArmorPlateHitHeightM(plate)) * 0.5);
        bool smallProjectile = projectileRadiusM <= 0.005;
        float radialToleranceM = (float)(smallProjectile
            ? Math.Max(0.034, projectileRadiusM + 0.022)
            : Math.Max(0.026, projectileRadiusM + 0.016));
        float planeToleranceM = (float)(smallProjectile
            ? Math.Max(0.026, projectileRadiusM + 0.018)
            : Math.Max(0.020, projectileRadiusM + 0.014));

        float denom = Vector3.Dot(segment, normal);
        if (Math.Abs(denom) > 1e-7f)
        {
            float t = Vector3.Dot(center - segmentStart, normal) / denom;
            if (t >= -0.05f && t <= 1.05f)
            {
                Vector3 candidate = segmentStart + segment * Math.Clamp(t, 0f, 1f);
                Vector3 local = candidate - center;
                float planeOffsetM = MathF.Abs(Vector3.Dot(local, normal));
                float radialSq = MathF.Pow(Vector3.Dot(local, side), 2) + MathF.Pow(Vector3.Dot(local, up), 2);
                float allowedRadiusM = diskRadiusM + radialToleranceM;
                if (planeOffsetM <= planeToleranceM && radialSq <= allowedRadiusM * allowedRadiusM)
                {
                    segmentT = Math.Clamp(t, 0.0f, 1.0f);
                    hitPoint = candidate;
                    return true;
                }
            }
        }

        (double distanceSq, Vector3 closest) = PointToSegmentDistanceSquared(center, segmentStart, segmentEnd);
        double fallbackRadiusM = diskRadiusM + Math.Max(radialToleranceM, 0.028f);
        if (distanceSq > fallbackRadiusM * fallbackRadiusM)
        {
            return false;
        }

        Vector3 fallbackLocal = closest - center;
        double fallbackPlaneOffsetM = Math.Abs(Vector3.Dot(fallbackLocal, normal));
        double fallbackRadialSq = Math.Pow(Vector3.Dot(fallbackLocal, side), 2) + Math.Pow(Vector3.Dot(fallbackLocal, up), 2);
        if (fallbackPlaneOffsetM > planeToleranceM + (smallProjectile ? 0.034 : 0.024)
            || fallbackRadialSq > fallbackRadiusM * fallbackRadiusM)
        {
            return false;
        }

        hitPoint = closest;
        segmentT = Math.Clamp(Vector3.Dot(closest - segmentStart, segment) / segmentLengthSq, 0.0, 1.0);
        return true;
    }

    private static double HitPlateHalfDiagonalM(ArmorPlateTarget plate)
    {
        double width = ResolveArmorPlateHitWidthM(plate);
        double height = ResolveArmorPlateHitHeightM(plate);
        return Math.Sqrt(width * width + height * height) * 0.5;
    }

    private static double ResolveProjectilePlateToleranceM(ArmorPlateTarget plate, double projectileRadiusM)
    {
        bool smallProjectile = projectileRadiusM <= 0.005;
        double toleranceM = smallProjectile
            ? Math.Max(0.016, projectileRadiusM + 0.010)
            : Math.Max(0.010, projectileRadiusM + 0.006);
        if (IsFineInteractiveArmorPlate(plate))
        {
            toleranceM += smallProjectile ? 0.004 : 0.002;
            if (plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase))
            {
                toleranceM += smallProjectile ? 0.008 : 0.004;
            }
        }

        if (plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase))
        {
            toleranceM += smallProjectile ? 0.010 : 0.005;
        }

        return toleranceM;
    }

    private static double ResolveProjectilePlateFallbackRadiusM(ArmorPlateTarget plate, double projectileRadiusM)
    {
        bool smallProjectile = projectileRadiusM <= 0.005;
        double halfDiagonalM = HitPlateHalfDiagonalM(plate);
        double minimumRadiusM = smallProjectile ? 0.085 : 0.065;
        if (IsFineInteractiveArmorPlate(plate))
        {
            minimumRadiusM += smallProjectile ? 0.012 : 0.006;
            halfDiagonalM += smallProjectile ? 0.010 : 0.004;
            if (plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase))
            {
                minimumRadiusM += smallProjectile ? 0.020 : 0.010;
                halfDiagonalM += smallProjectile ? 0.018 : 0.009;
            }
        }

        if (plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase))
        {
            minimumRadiusM += smallProjectile ? 0.018 : 0.010;
            halfDiagonalM += smallProjectile ? 0.014 : 0.008;
        }

        return projectileRadiusM + Math.Max(minimumRadiusM, halfDiagonalM);
    }

    private static bool IsFineInteractiveArmorPlate(ArmorPlateTarget plate)
    {
        return plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase)
            || plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase)
            || plate.Id.StartsWith("base_top", StringComparison.OrdinalIgnoreCase);
    }

    private static double ResolveArmorPlateHitWidthM(ArmorPlateTarget plate)
    {
        double width = plate.WidthM > 1e-6 ? plate.WidthM : plate.SideLengthM;
        return Math.Clamp(width, 0.03, 0.60);
    }

    private static double ResolveArmorPlateHitHeightM(ArmorPlateTarget plate)
    {
        double height = plate.HeightSpanM > 1e-6 ? plate.HeightSpanM : plate.SideLengthM;
        return Math.Clamp(height, 0.03, 0.60);
    }

    public static double ProjectileSpeedMps(string ammoType)
    {
        return string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 12.0 : 25.0;
    }

    public static double ProjectileSpeedMps(SimulationEntity shooter)
    {
        if (string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            bool rangedHero = string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                && string.Equals(RuleSet.NormalizeHeroMode(shooter.HeroPerformanceMode), "ranged_priority", StringComparison.OrdinalIgnoreCase);
            return rangedHero ? 16.5 : 12.0;
        }

        return 25.0;
    }

    public static double ProjectileDiameterM(string ammoType)
    {
        return string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 0.021 : 0.0085;
    }

    public static bool IsStructure(SimulationEntity entity)
    {
        return string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLegacyMechanismCollisionSuppressed(SimulationEntity entity)
    {
        return string.Equals(entity.EntityType, "base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeHeroAutoAimMode(string? mode)
        => string.Equals(mode, "lob", StringComparison.OrdinalIgnoreCase) ? "lob" : "normal";

    public static bool IsHeroLobAutoAimMode(SimulationEntity shooter)
    {
        return string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && (shooter.HeroDeploymentActive
                || string.Equals(NormalizeHeroAutoAimMode(shooter.HeroAutoAimMode), "lob", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsHeroNormalAutoAimMode(SimulationEntity shooter)
    {
        return string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && !IsHeroLobAutoAimMode(shooter);
    }

    public static bool IsHeroStructureAutoAimTargetEntity(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target)
    {
        if (!string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || !IsStructure(target))
        {
            return false;
        }

        return string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.EntityType, "base", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHeroStructureAutoAimTargetPlate(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        if (!IsHeroStructureAutoAimTargetEntity(world, shooter, target))
        {
            return false;
        }

        return string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            ? IsHeroDeploymentOutpostRingPlate(plate)
            : IsHeroDeploymentBaseTopPlate(plate);
    }

    public static bool IsAutoAimArmorTargetEligible(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double distanceM)
    {
        if (!string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsHeroLobAutoAimMode(shooter))
        {
            return IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate);
        }

        if (IsStructure(target))
        {
            return false;
        }

        return distanceM <= HeroNormalAutoAimMaxDistanceM;
    }

    public static string DescribeArmorPlateDirection(SimulationEntity target, ArmorPlateTarget plate)
    {
        if (plate.Id.StartsWith("energy_", StringComparison.OrdinalIgnoreCase))
        {
            return "\u80fd\u91cf\u5706\u76d8";
        }

        if (plate.Id.StartsWith("armor_", StringComparison.OrdinalIgnoreCase))
        {
            double relativeYaw = NormalizeSignedDeg(plate.YawDeg - target.AngleDeg);
            double abs = Math.Abs(relativeYaw);
            if (abs <= 45.0)
            {
                return "\u524d\u88c5\u7532";
            }

            if (abs >= 135.0)
            {
                return "\u540e\u88c5\u7532";
            }

            return relativeYaw > 0.0 ? "\u5de6\u88c5\u7532" : "\u53f3\u88c5\u7532";
        }

        if (plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase))
        {
            return "\u4e2d\u90e8\u659c\u88c5\u7532";
        }

        if (plate.Id.Equals("outpost_ring_1", StringComparison.OrdinalIgnoreCase))
        {
            return "\u4e2d\u65cb\u8f6c\u88c5\u7532\u677f";
        }

        if (plate.Id.Equals("outpost_ring_2", StringComparison.OrdinalIgnoreCase))
        {
            return "\u4e0b\u65cb\u8f6c\u88c5\u7532\u677f";
        }

        if (plate.Id.Equals("outpost_ring_3", StringComparison.OrdinalIgnoreCase))
        {
            return "\u4e0a\u65cb\u8f6c\u88c5\u7532\u677f";
        }

        if (plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase))
        {
            return "\u65cb\u8f6c\u88c5\u7532";
        }

        if (plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase))
        {
            return "\u9876\u90e8\u6ed1\u79fb\u88c5\u7532";
        }

        if (plate.Id.Equals("base_core", StringComparison.OrdinalIgnoreCase))
        {
            return "\u6838\u5fc3\u88c5\u7532";
        }

        return plate.Id;
    }

    public static double NormalizeDeg(double degrees)
    {
        double value = degrees % 360.0;
        if (value < 0.0)
        {
            value += 360.0;
        }

        return value;
    }

    public static double NormalizeSignedDeg(double degrees)
    {
        double value = NormalizeDeg(degrees);
        return value > 180.0 ? value - 360.0 : value;
    }

    private static bool IsPlateFacingShooter(SimulationEntity shooter, ArmorPlateTarget plate)
    {
        if (IsHeroLobAutoAimMode(shooter)
            && (plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase)
                || plate.Id.StartsWith("base_top", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        double toShooterYawDeg = NormalizeDeg(RadiansToDegrees(Math.Atan2(shooter.Y - plate.Y, shooter.X - plate.X)));
        double facingError = Math.Abs(NormalizeSignedDeg(toShooterYawDeg - plate.YawDeg));
        return facingError <= 60.0;
    }

    private static double ComputeFacingMargin(SimulationEntity shooter, ArmorPlateTarget plate)
    {
        if (IsHeroLobAutoAimMode(shooter)
            && (plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase)
                || plate.Id.StartsWith("base_top", StringComparison.OrdinalIgnoreCase)))
        {
            return 1.0;
        }

        if (plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        double toShooterYawDeg = NormalizeDeg(RadiansToDegrees(Math.Atan2(shooter.Y - plate.Y, shooter.X - plate.X)));
        double facingError = Math.Abs(NormalizeSignedDeg(toShooterYawDeg - plate.YawDeg));
        return Math.Cos(Math.Min(180.0, facingError) * Math.PI / 180.0);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static double ResolveArmorPlateThicknessM(SimulationEntity entity)
    {
        return Math.Max(
            0.012,
            Math.Max(
                entity.ArmorPlateGapM * 0.75,
                entity.ArmorPlateWidthM * 0.24));
    }

    private readonly record struct EnergyTargetLayout(
        double RotorCenterHeightM,
        double RotorPhaseRad,
        double RotorRadiusM,
        double RotorAxisGapM,
        double DiskDiameterM);

    private static void ResolveChassisAxes(
        double yawDeg,
        double pitchDeg,
        double rollDeg,
        out Vector3 forward,
        out Vector3 right,
        out Vector3 up)
    {
        double yawRad = DegreesToRadians(yawDeg);
        Vector3 flatForward = new((float)Math.Cos(yawRad), 0f, (float)Math.Sin(yawRad));
        Vector3 flatRight = new(-flatForward.Z, 0f, flatForward.X);
        float pitch = (float)DegreesToRadians(Math.Clamp(pitchDeg, -32.0, 32.0));
        float roll = (float)DegreesToRadians(Math.Clamp(rollDeg, -28.0, 28.0));
        forward = Vector3.Normalize(flatForward * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch));
        right = Vector3.Normalize(flatRight * MathF.Cos(roll) + Vector3.UnitY * MathF.Sin(roll));
        up = Vector3.Normalize(Vector3.Cross(right, forward));
        right = Vector3.Normalize(Vector3.Cross(forward, up));
    }

    private static void ResolveMountedTurretAxes(
        Vector3 chassisForward,
        Vector3 chassisRight,
        Vector3 chassisUp,
        double localTurretYawRad,
        double gimbalPitchRad,
        out Vector3 turretForward,
        out Vector3 turretRight,
        out Vector3 pitchedForward,
        out Vector3 pitchedUp)
    {
        turretForward = Vector3.Normalize(
            chassisForward * (float)Math.Cos(localTurretYawRad)
            + chassisRight * (float)Math.Sin(localTurretYawRad));
        turretRight = Vector3.Normalize(
            -chassisForward * (float)Math.Sin(localTurretYawRad)
            + chassisRight * (float)Math.Cos(localTurretYawRad));
        pitchedForward = Vector3.Normalize(
            turretForward * (float)Math.Cos(gimbalPitchRad)
            + chassisUp * (float)Math.Sin(gimbalPitchRad));
        pitchedUp = Vector3.Normalize(Vector3.Cross(turretRight, pitchedForward));
    }

    private static (double X, double Y, double HeightM) PredictArmorPlatePoint(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double distanceM,
        double heightCompensationM,
        out double leadTimeSec,
        out double leadDistanceM)
    {
        bool largeProjectile = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        double projectileSpeedMps = Math.Max(1.0, ProjectileSpeedMps(shooter));
        double dragTimeScale = 1.0 + Math.Clamp(distanceM * (largeProjectile ? 0.018 : 0.012), 0.0, largeProjectile ? 0.26 : 0.18);
        double travelLeadTimeSec = Math.Clamp(distanceM / projectileSpeedMps * dragTimeScale, 0.0, largeProjectile ? 1.05 : 0.75);
        double fireLatencySec = ResolveAutoAimFiringLatencySec(shooter);
        leadTimeSec = Math.Clamp(travelLeadTimeSec + fireLatencySec, 0.0, largeProjectile ? 1.20 : 0.85);
        bool preferHighArc = IsHeroLobAutoAimMode(shooter)
            && IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate);
        int iterations = largeProjectile ? 6 : 4;
        double convergenceThresholdSec = largeProjectile ? 0.0025 : 0.0040;

        (double predictedX, double predictedY, double predictedHeightM) = (plate.X, plate.Y, plate.HeightM);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            (predictedX, predictedY, predictedHeightM, leadDistanceM) = PredictArmorPlatePointAtLeadTime(
                world,
                shooter,
                target,
                plate,
                leadTimeSec);
            (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                preferHighArc);
            double refinedTravelTimeSec = EstimateProjectileTravelTimeSec(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                pitchDeg);
            double refinedLeadTimeSec = Math.Clamp(refinedTravelTimeSec + fireLatencySec, 0.0, largeProjectile ? 1.35 : 1.10);
            if (Math.Abs(refinedLeadTimeSec - leadTimeSec) <= convergenceThresholdSec)
            {
                leadTimeSec = refinedLeadTimeSec;
                break;
            }

            leadTimeSec = largeProjectile
                ? leadTimeSec * 0.24 + refinedLeadTimeSec * 0.76
                : leadTimeSec * 0.38 + refinedLeadTimeSec * 0.62;
        }

        (predictedX, predictedY, predictedHeightM, leadDistanceM) = PredictArmorPlatePointAtLeadTime(
            world,
            shooter,
            target,
            plate,
            leadTimeSec);
        return (predictedX, predictedY, predictedHeightM);
    }

    private static (double X, double Y, double HeightM) PredictObservationDrivenArmorPlatePoint(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double observedVelocityXMps,
        double observedVelocityYMps,
        double observedVelocityZMps,
        double distanceM,
        double heightCompensationM,
        out double leadTimeSec,
        out double leadDistanceM)
    {
        bool largeProjectile = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        double projectileSpeedMps = Math.Max(1.0, ProjectileSpeedMps(shooter));
        double dragTimeScale = 1.0 + Math.Clamp(distanceM * (largeProjectile ? 0.018 : 0.012), 0.0, largeProjectile ? 0.26 : 0.18);
        double travelLeadTimeSec = Math.Clamp(distanceM / projectileSpeedMps * dragTimeScale, 0.0, largeProjectile ? 1.05 : 0.75);
        double fireLatencySec = ResolveAutoAimFiringLatencySec(shooter);
        leadTimeSec = Math.Clamp(travelLeadTimeSec + fireLatencySec, 0.0, largeProjectile ? 1.20 : 0.85);
        bool preferHighArc = IsHeroLobAutoAimMode(shooter)
            && IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate);
        int iterations = largeProjectile ? 6 : 4;
        double convergenceThresholdSec = largeProjectile ? 0.0025 : 0.0040;

        (double predictedX, double predictedY, double predictedHeightM) = (observedXWorld, observedYWorld, observedHeightM);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            (predictedX, predictedY, predictedHeightM, leadDistanceM) = PredictObservationDrivenArmorPlatePointAtLeadTime(
                world,
                shooter,
                target,
                plate,
                observedXWorld,
                observedYWorld,
                observedHeightM,
                observedVelocityXMps,
                observedVelocityYMps,
                observedVelocityZMps,
                leadTimeSec);
            (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                preferHighArc);
            double refinedTravelTimeSec = EstimateProjectileTravelTimeSec(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                pitchDeg);
            double refinedLeadTimeSec = Math.Clamp(refinedTravelTimeSec + fireLatencySec, 0.0, largeProjectile ? 1.35 : 1.10);
            if (Math.Abs(refinedLeadTimeSec - leadTimeSec) <= convergenceThresholdSec)
            {
                leadTimeSec = refinedLeadTimeSec;
                break;
            }

            leadTimeSec = largeProjectile
                ? leadTimeSec * 0.24 + refinedLeadTimeSec * 0.76
                : leadTimeSec * 0.38 + refinedLeadTimeSec * 0.62;
        }

        (predictedX, predictedY, predictedHeightM, leadDistanceM) = PredictObservationDrivenArmorPlatePointAtLeadTime(
            world,
            shooter,
            target,
            plate,
            observedXWorld,
            observedYWorld,
            observedHeightM,
            observedVelocityXMps,
            observedVelocityYMps,
            observedVelocityZMps,
            leadTimeSec);
        return (predictedX, predictedY, predictedHeightM);
    }

    private static (double X, double Y, double HeightM) PredictObservationDrivenArmorPlatePointThirdOrderEkf(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double observedVelocityXMps,
        double observedVelocityYMps,
        double observedVelocityZMps,
        double observedAccelerationXMps2,
        double observedAccelerationYMps2,
        double observedAccelerationZMps2,
        double distanceM,
        double heightCompensationM,
        out double leadTimeSec,
        out double leadDistanceM)
    {
        bool largeProjectile = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        double projectileSpeedMps = Math.Max(1.0, ProjectileSpeedMps(shooter));
        double dragTimeScale = 1.0 + Math.Clamp(distanceM * (largeProjectile ? 0.018 : 0.012), 0.0, largeProjectile ? 0.26 : 0.18);
        double travelLeadTimeSec = Math.Clamp(distanceM / projectileSpeedMps * dragTimeScale, 0.0, largeProjectile ? 1.05 : 0.75);
        double fireLatencySec = ResolveAutoAimFiringLatencySec(shooter);
        leadTimeSec = Math.Clamp(travelLeadTimeSec + fireLatencySec, 0.0, largeProjectile ? 1.20 : 0.85);
        bool preferHighArc = IsHeroLobAutoAimMode(shooter)
            && IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate);
        int iterations = largeProjectile ? 6 : 4;
        double convergenceThresholdSec = largeProjectile ? 0.0025 : 0.0040;

        (double predictedX, double predictedY, double predictedHeightM) = (observedXWorld, observedYWorld, observedHeightM);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            (predictedX, predictedY, predictedHeightM, leadDistanceM) = PredictObservationDrivenArmorPlatePointAtLeadTimeThirdOrderEkf(
                world,
                shooter,
                target,
                plate,
                observedXWorld,
                observedYWorld,
                observedHeightM,
                observedVelocityXMps,
                observedVelocityYMps,
                observedVelocityZMps,
                observedAccelerationXMps2,
                observedAccelerationYMps2,
                observedAccelerationZMps2,
                leadTimeSec);
            (double yawDeg, double pitchDeg) = ComputeAimAnglesToPoint(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                preferHighArc);
            double refinedTravelTimeSec = EstimateProjectileTravelTimeSec(
                world,
                shooter,
                predictedX,
                predictedY,
                predictedHeightM + heightCompensationM,
                pitchDeg);
            double refinedLeadTimeSec = Math.Clamp(refinedTravelTimeSec + fireLatencySec, 0.0, largeProjectile ? 1.35 : 1.10);
            if (Math.Abs(refinedLeadTimeSec - leadTimeSec) <= convergenceThresholdSec)
            {
                leadTimeSec = refinedLeadTimeSec;
                break;
            }

            leadTimeSec = largeProjectile
                ? leadTimeSec * 0.24 + refinedLeadTimeSec * 0.76
                : leadTimeSec * 0.38 + refinedLeadTimeSec * 0.62;
        }

        (predictedX, predictedY, predictedHeightM, leadDistanceM) = PredictObservationDrivenArmorPlatePointAtLeadTimeThirdOrderEkf(
            world,
            shooter,
            target,
            plate,
            observedXWorld,
            observedYWorld,
            observedHeightM,
            observedVelocityXMps,
            observedVelocityYMps,
            observedVelocityZMps,
            observedAccelerationXMps2,
            observedAccelerationYMps2,
            observedAccelerationZMps2,
            leadTimeSec);
        return (predictedX, predictedY, predictedHeightM);
    }

    private static (double X, double Y, double HeightM, double LeadDistanceM) PredictObservationDrivenArmorPlatePointAtLeadTime(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double observedVelocityXMps,
        double observedVelocityYMps,
        double observedVelocityZMps,
        double leadTimeSec)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        if (leadTimeSec <= 1e-4)
        {
            return (observedXWorld, observedYWorld, observedHeightM, 0.0);
        }

        if (IsObservationDrivenTargetEffectivelyStatic(
            world,
            shooter,
            target,
            plate,
            observedVelocityXMps,
            observedVelocityYMps,
            observedVelocityZMps,
            0.0))
        {
            return (observedXWorld, observedYWorld, observedHeightM, 0.0);
        }

        AutoAimCompensationProfile compensationProfile = ResolveAutoAimCompensationProfile(world, shooter, target, plate);
        double compensatedLeadTimeSec = Math.Max(0.0, leadTimeSec + compensationProfile.TimeBiasSec);
        bool suppressLateralLead = ShouldSuppressStructureTopLateralLead(shooter, plate)
            || (IsHeroLobAutoAimMode(shooter)
                && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                && IsHeroDeploymentBaseTopPlate(plate));
        double observedVelocityXWorldPerSec = observedVelocityXMps / metersPerWorldUnit;
        double observedVelocityYWorldPerSec = observedVelocityYMps / metersPerWorldUnit;
        double shooterFutureOffsetX = ResolveObservedVelocityXWorldPerSec(shooter) * compensatedLeadTimeSec * ShooterInheritedVelocityLeadScale;
        double shooterFutureOffsetY = ResolveObservedVelocityYWorldPerSec(shooter) * compensatedLeadTimeSec * ShooterInheritedVelocityLeadScale;
        double predictedX = observedXWorld + observedVelocityXWorldPerSec * compensatedLeadTimeSec - shooterFutureOffsetX;
        double predictedY = observedYWorld + observedVelocityYWorldPerSec * compensatedLeadTimeSec - shooterFutureOffsetY;
        double predictedHeightM = Math.Max(0.0, observedHeightM + observedVelocityZMps * compensatedLeadTimeSec);
        if (suppressLateralLead)
        {
            predictedX = observedXWorld;
            predictedY = observedYWorld;
        }

        double leadDxM = (predictedX - observedXWorld) * metersPerWorldUnit;
        double leadDyM = (predictedY - observedYWorld) * metersPerWorldUnit;
        double leadDzM = predictedHeightM - observedHeightM;
        double leadDistanceM = Math.Sqrt(leadDxM * leadDxM + leadDyM * leadDyM + leadDzM * leadDzM);
        return (predictedX, predictedY, predictedHeightM, leadDistanceM);
    }
    private static (double X, double Y, double HeightM, double LeadDistanceM) PredictObservationDrivenArmorPlatePointAtLeadTimeThirdOrderEkf(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double observedVelocityXMps,
        double observedVelocityYMps,
        double observedVelocityZMps,
        double observedAccelerationXMps2,
        double observedAccelerationYMps2,
        double observedAccelerationZMps2,
        double leadTimeSec)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        if (leadTimeSec <= 1e-4)
        {
            return (observedXWorld, observedYWorld, observedHeightM, 0.0);
        }

        double accelerationMagnitudeMps2 = Math.Sqrt(
            observedAccelerationXMps2 * observedAccelerationXMps2
            + observedAccelerationYMps2 * observedAccelerationYMps2
            + observedAccelerationZMps2 * observedAccelerationZMps2);
        if (IsObservationDrivenTargetEffectivelyStatic(
                world,
                shooter,
                target,
                plate,
                observedVelocityXMps,
                observedVelocityYMps,
                observedVelocityZMps,
                0.0)
            && accelerationMagnitudeMps2 <= 0.85)
        {
            return (observedXWorld, observedYWorld, observedHeightM, 0.0);
        }

        AutoAimCompensationProfile compensationProfile = ResolveAutoAimCompensationProfile(world, shooter, target, plate);
        double compensatedLeadTimeSec = Math.Max(0.0, leadTimeSec + compensationProfile.TimeBiasSec);
        bool suppressLateralLead = ShouldSuppressStructureTopLateralLead(shooter, plate)
            || (IsHeroLobAutoAimMode(shooter)
                && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                && IsHeroDeploymentBaseTopPlate(plate));
        double observedVelocityXWorldPerSec = observedVelocityXMps / metersPerWorldUnit;
        double observedVelocityYWorldPerSec = observedVelocityYMps / metersPerWorldUnit;
        double observedAccelerationXWorldPerSec2 = observedAccelerationXMps2 / metersPerWorldUnit;
        double observedAccelerationYWorldPerSec2 = observedAccelerationYMps2 / metersPerWorldUnit;
        double shooterFutureOffsetX = ResolveObservedVelocityXWorldPerSec(shooter) * compensatedLeadTimeSec * ShooterInheritedVelocityLeadScale;
        double shooterFutureOffsetY = ResolveObservedVelocityYWorldPerSec(shooter) * compensatedLeadTimeSec * ShooterInheritedVelocityLeadScale;
        double predictedX = observedXWorld
            + observedVelocityXWorldPerSec * compensatedLeadTimeSec
            + 0.5 * observedAccelerationXWorldPerSec2 * compensatedLeadTimeSec * compensatedLeadTimeSec
            - shooterFutureOffsetX;
        double predictedY = observedYWorld
            + observedVelocityYWorldPerSec * compensatedLeadTimeSec
            + 0.5 * observedAccelerationYWorldPerSec2 * compensatedLeadTimeSec * compensatedLeadTimeSec
            - shooterFutureOffsetY;
        double predictedHeightM = Math.Max(0.0, observedHeightM + observedVelocityZMps * compensatedLeadTimeSec + 0.5 * observedAccelerationZMps2 * compensatedLeadTimeSec * compensatedLeadTimeSec);
        if (suppressLateralLead)
        {
            predictedX = observedXWorld;
            predictedY = observedYWorld;
        }

        double leadDxM = (predictedX - observedXWorld) * metersPerWorldUnit;
        double leadDyM = (predictedY - observedYWorld) * metersPerWorldUnit;
        double leadDzM = predictedHeightM - observedHeightM;
        double leadDistanceM = Math.Sqrt(leadDxM * leadDxM + leadDyM * leadDyM + leadDzM * leadDzM);
        return (predictedX, predictedY, predictedHeightM, leadDistanceM);
    }
    private static (double X, double Y, double HeightM, double LeadDistanceM) PredictArmorPlatePointAtLeadTime(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double leadTimeSec)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        if (leadTimeSec <= 1e-4)
        {
            return (plate.X, plate.Y, plate.HeightM, 0.0);
        }

        if (IsAutoAimTargetEffectivelyStatic(world, shooter, target, plate))
        {
            return (plate.X, plate.Y, plate.HeightM, 0.0);
        }

        bool suppressLateralLead = ShouldSuppressStructureTopLateralLead(shooter, plate)
            || (IsHeroLobAutoAimMode(shooter)
                && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                && IsHeroDeploymentBaseTopPlate(plate));
        (double relativeVelocityXWorldPerSec, double relativeVelocityYWorldPerSec, double plateVelocityZMps) =
            ResolveArmorPlatePointRelativeObservedVelocityWorld(world, shooter, target, plate);
        double plateSpeedMps = Math.Sqrt(
            relativeVelocityXWorldPerSec * relativeVelocityXWorldPerSec
            + relativeVelocityYWorldPerSec * relativeVelocityYWorldPerSec) * metersPerWorldUnit;
        if (plateSpeedMps <= 0.10
            && Math.Abs(plateVelocityZMps) <= 0.06
            && !target.SmallGyroActive)
        {
            return (plate.X, plate.Y, plate.HeightM, 0.0);
        }

        AutoAimCompensationProfile compensationProfile = ResolveAutoAimCompensationProfile(world, shooter, target, plate);
        double translationLeadScale = compensationProfile.TranslationLeadScale;
        double angularLeadScale = compensationProfile.AngularLeadScale;
        double compensatedLeadTimeSec = Math.Max(0.0, leadTimeSec + compensationProfile.TimeBiasSec);
        double offsetXWorld = plate.X - target.X;
        double offsetYWorld = plate.Y - target.Y;
        double angularLeadRad = ResolveAutoAimAngularVelocityRadPerSec(target) * compensatedLeadTimeSec * angularLeadScale;
        double rotatedOffsetXWorld = offsetXWorld * Math.Cos(angularLeadRad) - offsetYWorld * Math.Sin(angularLeadRad);
        double rotatedOffsetYWorld = offsetXWorld * Math.Sin(angularLeadRad) + offsetYWorld * Math.Cos(angularLeadRad);

        double predictedX = target.X
            + ResolveObservedVelocityXWorldPerSec(target) * compensatedLeadTimeSec * translationLeadScale
            - ResolveObservedVelocityXWorldPerSec(shooter) * compensatedLeadTimeSec * translationLeadScale * ShooterInheritedVelocityLeadScale
            + rotatedOffsetXWorld;
        double predictedY = target.Y
            + ResolveObservedVelocityYWorldPerSec(target) * compensatedLeadTimeSec * translationLeadScale
            - ResolveObservedVelocityYWorldPerSec(shooter) * compensatedLeadTimeSec * translationLeadScale * ShooterInheritedVelocityLeadScale
            + rotatedOffsetYWorld;
        double predictedHeightM = Math.Max(0.0, plate.HeightM + target.VerticalVelocityMps * compensatedLeadTimeSec);
        if (suppressLateralLead)
        {
            predictedX = plate.X;
            predictedY = plate.Y;
        }

        double leadDxM = (predictedX - plate.X) * metersPerWorldUnit;
        double leadDyM = (predictedY - plate.Y) * metersPerWorldUnit;
        double leadDzM = predictedHeightM - plate.HeightM;
        double leadDistanceM = Math.Sqrt(leadDxM * leadDxM + leadDyM * leadDyM + leadDzM * leadDzM);
        return (predictedX, predictedY, predictedHeightM, leadDistanceM);
    }

    private static double EstimateProjectileTravelTimeSec(
        SimulationWorldState world,
        SimulationEntity shooter,
        double targetX,
        double targetY,
        double targetHeightM,
        double pitchDeg)
    {
        (double muzzleX, double muzzleY, double muzzleHeightM) = ComputeMuzzlePoint(world, shooter, pitchDeg);
        double horizontalM = Math.Sqrt(
            Math.Pow(targetX - muzzleX, 2)
            + Math.Pow(targetY - muzzleY, 2)) * Math.Max(world.MetersPerWorldUnit, 1e-6);
        double dzM = targetHeightM - muzzleHeightM;
        double speedMps = Math.Max(1.0, ProjectileSpeedMps(shooter));
        if (horizontalM <= 1e-4)
        {
            return 0.0;
        }

        double pitchRad = DegreesToRadians(pitchDeg);
        if (TryEstimateBallisticTravelTimeWithDrag(horizontalM, dzM, speedMps, shooter.AmmoType, pitchRad, out double dragTimeSec))
        {
            return dragTimeSec;
        }

        double horizontalSpeedMps = Math.Max(0.1, speedMps * Math.Cos(pitchRad));
        return horizontalM / horizontalSpeedMps;
    }

    private static bool TryEstimateBallisticTravelTimeWithDrag(
        double horizontalM,
        double dzM,
        double speedMps,
        string ammoType,
        double pitchRad,
        out double travelTimeSec)
    {
        travelTimeSec = 0.0;
        double vxMps = speedMps * Math.Cos(pitchRad);
        double vzMps = speedMps * Math.Sin(pitchRad);
        if (vxMps <= 1e-5)
        {
            return false;
        }

        double xM = 0.0;
        double zM = 0.0;
        double previousXM = 0.0;
        double previousTimeSec = 0.0;
        double diameterM = ProjectileDiameterM(ammoType);
        double areaM2 = Math.PI * diameterM * diameterM * 0.25;
        double massKg = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 0.041 : 0.0032;
        double dt = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 1.0 / 240.0 : 1.0 / 220.0;
        int maxSteps = string.Equals(ammoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 900 : 620;
        for (int step = 0; step < maxSteps; step++)
        {
            previousXM = xM;
            previousTimeSec = travelTimeSec;

            double speed = Math.Sqrt(vxMps * vxMps + vzMps * vzMps);
            if (speed > 1e-6)
            {
                double dragCoefficient = 0.47;
                double airDensityKgM3 = 1.20;
                double dragAccelMps2 = 0.5 * airDensityKgM3 * dragCoefficient * areaM2 * speed * speed / Math.Max(0.001, massKg);
                dragAccelMps2 = Math.Min(dragAccelMps2, speed / Math.Max(dt, 1e-6) * 0.72);
                double dragStep = dragAccelMps2 * dt / speed;
                vxMps -= vxMps * dragStep;
                vzMps -= vzMps * dragStep;
            }

            vzMps -= GravityMps2 * dt;
            xM += vxMps * dt;
            zM += vzMps * dt;
            travelTimeSec += dt;
            if (xM >= horizontalM)
            {
                double t = (horizontalM - previousXM) / Math.Max(1e-6, xM - previousXM);
                travelTimeSec = previousTimeSec + (travelTimeSec - previousTimeSec) * Math.Clamp(t, 0.0, 1.0);
                return double.IsFinite(dzM);
            }
        }

        return false;
    }

    private static double ResolveAutoAimFiringLatencySec(SimulationEntity shooter)
        => string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? LargeProjectileAutoAimLatencySec
            : SmallProjectileAutoAimLatencySec;

    private static bool IsObservationDrivenTargetEffectivelyStatic(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double observedVelocityXMps,
        double observedVelocityYMps,
        double observedVelocityZMps,
        double observedAngularVelocityRadPerSec)
    {
        if (target.SmallGyroActive)
        {
            return false;
        }

        if (IsStructure(target) || string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Abs(observedVelocityXMps) <= 0.045
                && Math.Abs(observedVelocityYMps) <= 0.045
                && Math.Abs(observedVelocityZMps) <= 0.040
                && Math.Abs(observedAngularVelocityRadPerSec) <= 0.08;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double shooterSpeedMps = Math.Sqrt(
            Math.Pow(ResolveObservedVelocityXWorldPerSec(shooter) * metersPerWorldUnit, 2)
            + Math.Pow(ResolveObservedVelocityYWorldPerSec(shooter) * metersPerWorldUnit, 2));
        double relativeSpeedMps = Math.Sqrt(
            observedVelocityXMps * observedVelocityXMps
            + observedVelocityYMps * observedVelocityYMps
            + observedVelocityZMps * observedVelocityZMps);
        return relativeSpeedMps <= (shooterSpeedMps > 0.8 ? 0.16 : 0.10)
            && Math.Abs(observedAngularVelocityRadPerSec) <= 0.14
            && !ShouldSuppressStructureTopLateralLead(shooter, plate);
    }

    private static double ComputeAutoAimObservationMotionCoefficient(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        double observedVelocityXMps,
        double observedVelocityYMps,
        double observedVelocityZMps,
        double observedAngularVelocityRadPerSec)
    {
        if (IsStructure(target) || string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double shooterVelocityXMps = ResolveObservedVelocityXWorldPerSec(shooter) * metersPerWorldUnit;
        double shooterVelocityYMps = ResolveObservedVelocityYWorldPerSec(shooter) * metersPerWorldUnit;
        double relativeVelocityXMps = observedVelocityXMps - shooterVelocityXMps;
        double relativeVelocityYMps = observedVelocityYMps - shooterVelocityYMps;
        double relativeSpeedMps = Math.Sqrt(
            relativeVelocityXMps * relativeVelocityXMps
            + relativeVelocityYMps * relativeVelocityYMps
            + observedVelocityZMps * observedVelocityZMps);
        double speedPenalty = Math.Clamp(relativeSpeedMps * 0.045, 0.0, 0.18);
        double angularPenalty = Math.Clamp(Math.Abs(observedAngularVelocityRadPerSec) * 0.030, 0.0, 0.12);
        double coefficient = 1.0 - speedPenalty - angularPenalty;
        if (target.SmallGyroActive)
        {
            coefficient -= 0.08;
        }

        return Math.Clamp(coefficient, 0.72, 1.0);
    }

    public static bool IsAutoAimTargetEffectivelyStatic(SimulationWorldState world, SimulationEntity target, ArmorPlateTarget plate)
        => IsAutoAimTargetEffectivelyStatic(world, shooter: null, target, plate);

    public static bool IsAutoAimTargetEffectivelyStatic(
        SimulationWorldState world,
        SimulationEntity? shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            && !plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        (double plateVelocityXWorldPerSec, double plateVelocityYWorldPerSec, double plateVelocityZMps) = shooter is null
            ? ResolveArmorPlatePointObservedVelocityWorld(world, target, plate)
            : ResolveArmorPlatePointRelativeObservedVelocityWorld(world, shooter, target, plate);
        double plateTranslationSpeedMps = Math.Sqrt(
            plateVelocityXWorldPerSec * plateVelocityXWorldPerSec
            + plateVelocityYWorldPerSec * plateVelocityYWorldPerSec) * metersPerWorldUnit;
        return plateTranslationSpeedMps <= 0.06
            && Math.Abs(plateVelocityZMps) <= 0.05
            && !target.SmallGyroActive
            && target.AutoAimInstabilityTimerSec <= 1e-6;
    }

    private static bool ShouldSuppressStructureTopLateralLead(SimulationEntity shooter, ArmorPlateTarget plate)
    {
        return string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            && (plate.Id.Equals("outpost_top", StringComparison.OrdinalIgnoreCase)
                || plate.Id.Equals("base_top_slide", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldAimStructureCriticalZone(SimulationEntity target, ArmorPlateTarget plate)
    {
        return IsStructure(target) && IsCriticalStructurePlate(plate);
    }

    private static bool IsCriticalStructurePlate(ArmorPlateTarget plate)
    {
        return plate.Id.StartsWith("outpost_ring_", StringComparison.OrdinalIgnoreCase)
            || plate.Id.StartsWith("base_", StringComparison.OrdinalIgnoreCase);
    }

    public static AutoAimCompensationProfile ResolveAutoAimCompensationProfile(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        (double translationLeadScale, double angularLeadScale) = ResolveAutoAimLeadScales(world, shooter, target, plate);
        double timeBiasSec = ResolveAutoAimLeadTimeBiasSec(world, shooter, target, plate);
        return new AutoAimCompensationProfile(
            ResolveAutoAimCompensationProfileName(shooter, target, plate),
            translationLeadScale,
            angularLeadScale,
            timeBiasSec);
    }

    private static string ResolveAutoAimCompensationProfileName(
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        bool largeProjectile = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        bool energyTarget = string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
        bool structureTarget = IsStructure(target);
        bool rotatingPlate = IsRotatingArmorPlate(target, plate);
        if (largeProjectile && structureTarget && rotatingPlate)
        {
            return "42mm_structure_rotating";
        }

        if (largeProjectile && structureTarget)
        {
            return "42mm_structure";
        }

        if (largeProjectile && energyTarget)
        {
            return "42mm_energy";
        }

        if (largeProjectile && rotatingPlate)
        {
            return "42mm_rotating";
        }

        if (!largeProjectile && energyTarget)
        {
            return "17mm_energy";
        }

        if (!largeProjectile && rotatingPlate)
        {
            return "17mm_rotating";
        }

        return largeProjectile ? "42mm_default" : "17mm_default";
    }

    private static double ResolveAutoAimLeadTimeBiasSec(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        bool largeProjectile = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        bool energyTarget = string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
        bool structureTarget = IsStructure(target);
        bool rotatingPlate = IsRotatingArmorPlate(target, plate);
        bool heroLobStructureTarget = largeProjectile
            && IsHeroLobAutoAimMode(shooter)
            && string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && structureTarget;
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double distanceM = Math.Sqrt(
            Math.Pow(plate.X - shooter.X, 2)
            + Math.Pow(plate.Y - shooter.Y, 2)) * metersPerWorldUnit;
        double distanceRatio = Math.Clamp(distanceM / (largeProjectile ? 18.0 : 12.0), 0.0, 1.0);

        double biasSec;
        if (largeProjectile)
        {
            if (heroLobStructureTarget && rotatingPlate)
            {
                // Hero lob uses future structure pose reconstruction directly.
                // Keep a positive release bias so long-range shells are fired for the
                // plate we expect to occupy the line, not the plate that is rotating away.
                biasSec = 0.024 + 0.024 * distanceRatio;
            }
            else if (heroLobStructureTarget)
            {
                biasSec = 0.014 + 0.016 * distanceRatio;
            }
            else if (structureTarget && rotatingPlate)
            {
                biasSec = -0.022 - 0.010 * distanceRatio;
            }
            else if (structureTarget)
            {
                biasSec = -0.018 - 0.008 * distanceRatio;
            }
            else if (energyTarget)
            {
                biasSec = -0.012 - 0.006 * distanceRatio;
            }
            else if (rotatingPlate)
            {
                biasSec = -0.014 - 0.006 * distanceRatio;
            }
            else
            {
                biasSec = -0.008 - 0.004 * distanceRatio;
            }
        }
        else
        {
            if (energyTarget)
            {
                // Energy disks already use analytic future-pose reconstruction.
                // Keep 17mm prediction slightly earlier so rapid follow-up shots
                // do not drift to the outgoing side of the rotating disk.
                biasSec = -0.010 - 0.004 * distanceRatio;
            }
            else if (rotatingPlate)
            {
                biasSec = -0.005 - 0.003 * distanceRatio;
            }
            else
            {
                biasSec = -0.002 - 0.002 * distanceRatio;
            }
        }

        if (target.AutoAimInstabilityTimerSec > 1e-6)
        {
            biasSec *= heroLobStructureTarget ? 0.82 : 0.65;
        }

        return biasSec;
    }

    private static (double TranslationLeadScale, double AngularLeadScale) ResolveAutoAimLeadScales(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        bool largeProjectile = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        double baseTranslation = largeProjectile ? 1.28 : 0.98;
        double baseAngular = largeProjectile ? 1.14 : 0.96;
        if (largeProjectile
            && IsHeroLobAutoAimMode(shooter)
            && IsStructure(target))
        {
            // Structure targets already have an exact future-pose solve, so keep the fallback
            // motion model close to neutral instead of aggressively damping it.
            baseTranslation = IsRotatingArmorPlate(target, plate) ? 1.14 : 1.08;
            baseAngular = IsRotatingArmorPlate(target, plate) ? 1.04 : 0.99;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double distanceM = Math.Sqrt(
            Math.Pow(plate.X - shooter.X, 2)
            + Math.Pow(plate.Y - shooter.Y, 2)) * metersPerWorldUnit;
        bool rotatingPlate = IsRotatingArmorPlate(target, plate);
        bool structureTarget = IsStructure(target);
        bool energyTarget = string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
        double distanceRatio = Math.Clamp(distanceM / (largeProjectile ? 16.0 : 11.0), 0.0, 1.0);
        double empiricalTranslationDamping = 1.0 - (largeProjectile ? 0.16 : 0.08) * distanceRatio;
        double empiricalAngularDamping = 1.0 - (largeProjectile ? 0.24 : 0.12) * distanceRatio;
        if (rotatingPlate)
        {
            empiricalTranslationDamping *= largeProjectile ? 0.90 : 0.96;
            empiricalAngularDamping *= largeProjectile ? 0.72 : 0.86;
        }

        if (energyTarget)
        {
            empiricalTranslationDamping *= largeProjectile ? 0.92 : 0.955;
            empiricalAngularDamping *= largeProjectile ? 0.84 : 0.90;
        }
        else if (structureTarget)
        {
            empiricalTranslationDamping *= largeProjectile ? 0.88 : 0.95;
            empiricalAngularDamping *= largeProjectile ? 0.78 : 0.92;
        }

        double translationLeadScale = baseTranslation * empiricalTranslationDamping;
        double angularLeadScale = baseAngular * empiricalAngularDamping;
        if (target.AutoAimInstabilityTimerSec <= 1e-6)
        {
            return (translationLeadScale, angularLeadScale);
        }

        // Empirical disturbance damping stays in the same prediction chain to mimic visual jitter without forking F8/control solve logic.
        double instabilityRatio = Math.Clamp(target.AutoAimInstabilityTimerSec / 0.42, 0.0, 1.0);
        double minTranslation = largeProjectile ? 0.60 : 0.22;
        double minAngular = largeProjectile ? 0.42 : 0.12;
        translationLeadScale -= (translationLeadScale - minTranslation) * instabilityRatio;
        angularLeadScale -= (angularLeadScale - minAngular) * instabilityRatio;
        return (translationLeadScale, angularLeadScale);
    }

    private static double DistanceBetweenPlatePointsM(double metersPerWorldUnit, ArmorPlateTarget current, ArmorPlateTarget predicted)
    {
        double dxM = (predicted.X - current.X) * metersPerWorldUnit;
        double dyM = (predicted.Y - current.Y) * metersPerWorldUnit;
        double dzM = predicted.HeightM - current.HeightM;
        return Math.Sqrt(dxM * dxM + dyM * dyM + dzM * dzM);
    }

    private static IReadOnlyList<ArmorPlateTarget> GetOutpostArmorPlateTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec,
        bool includeTopArmor)
    {
        if (target.AnnotatedOutpost is { } annotatedOutpost)
        {
            return GetAnnotatedOutpostArmorPlateTargets(target, annotatedOutpost, metersPerWorldUnit, gameTimeSec, includeTopArmor);
        }

        double baseYawDeg = NormalizeDeg(target.AngleDeg);
        double baseYawRad = DegreesToRadians(baseYawDeg);
        double radiusWorld = (OutpostTowerRadiusM + 0.055) / Math.Max(metersPerWorldUnit, 1e-6);
        double topRadiusWorld = Math.Max(0.0, OutpostTowerRadiusM + 0.035) / Math.Max(metersPerWorldUnit, 1e-6);
        double baseLiftM = Math.Clamp(target.StructureBaseLiftM <= 1e-6 ? OutpostBaseLiftM : target.StructureBaseLiftM, 0.0, 1.20);
        double centerHeightM = target.GroundHeightM + baseLiftM + OutpostTowerHeightM;
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateWidthM, target.ArmorPlateHeightM), 0.04, 0.60);
        var plates = new List<ArmorPlateTarget>(includeTopArmor ? 4 : 3);
        if (includeTopArmor)
        {
            plates.Add(new ArmorPlateTarget(
                "outpost_top",
                target.X
                    + (Math.Cos(baseYawRad) * target.StructureTopArmorOffsetXM - Math.Sin(baseYawRad) * target.StructureTopArmorOffsetZM)
                        / Math.Max(metersPerWorldUnit, 1e-6)
                    + Math.Cos(baseYawRad) * topRadiusWorld,
                target.Y
                    + (Math.Sin(baseYawRad) * target.StructureTopArmorOffsetXM + Math.Cos(baseYawRad) * target.StructureTopArmorOffsetZM)
                        / Math.Max(metersPerWorldUnit, 1e-6)
                    + Math.Sin(baseYawRad) * topRadiusWorld,
                target.GroundHeightM
                    + baseLiftM
                    + Math.Max(0.05, target.StructureTopArmorCenterHeightM > 1e-6 ? target.StructureTopArmorCenterHeightM : centerHeightM + OutpostTopArmorCenterLiftM - target.GroundHeightM - baseLiftM),
                baseYawDeg,
                armorSideLengthM));
        }

        double ringBaseYawDeg = ResolveOutpostRingYawDeg(target, gameTimeSec);
        double[] heightOffsets = { 0.05, 0.0, -0.05 };
        for (int index = 0; index < 3; index++)
        {
            double yawDeg = NormalizeDeg(ringBaseYawDeg + index * 120.0);
            double yawRad = DegreesToRadians(yawDeg);
            plates.Add(new ArmorPlateTarget(
                $"outpost_ring_{index + 1}",
                target.X + Math.Cos(yawRad) * radiusWorld,
                target.Y + Math.Sin(yawRad) * radiusWorld,
                target.GroundHeightM + baseLiftM + OutpostTowerHeightM - 0.10 + heightOffsets[index],
                yawDeg,
                armorSideLengthM));
        }

        return plates;
    }

    private static IReadOnlyList<ArmorPlateTarget> GetAnnotatedOutpostArmorPlateTargets(
        SimulationEntity target,
        AnnotatedOutpostTeamDefinition annotated,
        double metersPerWorldUnit,
        double gameTimeSec,
        bool includeTopArmor)
    {
        if (target.RuntimeOutpostTargets is { Count: > 0 } runtimeTargets)
        {
            if (includeTopArmor)
            {
                return runtimeTargets;
            }

            ArmorPlateTarget[] filteredRuntimeTargets = runtimeTargets
                .Where(candidate => !string.Equals(candidate.Id, "outpost_top", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (filteredRuntimeTargets.Length > 0)
            {
                return filteredRuntimeTargets;
            }
        }

        double safeMeters = Math.Max(metersPerWorldUnit, 1e-6);
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateWidthM, target.ArmorPlateHeightM), 0.04, 0.60);
        var plates = new List<ArmorPlateTarget>(annotated.RotatingPlates.Count + (includeTopArmor && annotated.TopPlate.HasValue ? 1 : 0));

        if (includeTopArmor && annotated.TopPlate is ArmorPlateTarget topPlate)
        {
            plates.Add(topPlate with
            {
                YawDeg = NormalizeDeg(target.AngleDeg),
                SideLengthM = topPlate.SideLengthM > 1e-6 ? topPlate.SideLengthM : armorSideLengthM,
                WidthM = topPlate.WidthM > 1e-6
                    ? topPlate.WidthM
                    : (topPlate.SideLengthM > 1e-6 ? topPlate.SideLengthM : armorSideLengthM),
                HeightSpanM = topPlate.HeightSpanM > 1e-6
                    ? topPlate.HeightSpanM
                    : (topPlate.SideLengthM > 1e-6 ? topPlate.SideLengthM : armorSideLengthM),
            });
        }

        double rotationRad = ResolveOutpostRingRelativeRotationRad(target, gameTimeSec);
        foreach (AnnotatedOutpostPlateState plate in annotated.RotatingPlates)
        {
            double angleRad = plate.BaseAngleRad + rotationRad;
            double yawDeg = NormalizeDeg(RadiansToDegrees(angleRad));
            plates.Add(new ArmorPlateTarget(
                plate.PlateId,
                annotated.PivotWorldX + Math.Cos(angleRad) * plate.RadiusM / safeMeters,
                annotated.PivotWorldY + Math.Sin(angleRad) * plate.RadiusM / safeMeters,
                plate.HeightM,
                yawDeg,
                plate.SideLengthM > 1e-6 ? plate.SideLengthM : armorSideLengthM,
                plate.WidthM,
                plate.HeightSpanM));
        }

        return plates;
    }

    private static IReadOnlyList<ArmorPlateTarget> GetBaseArmorPlateTargets(
        SimulationEntity target,
        double metersPerWorldUnit,
        double gameTimeSec)
    {
        if (target.RuntimeBaseTargets is { Count: > 0 } runtimeTargets)
        {
            if (target.Health >= BaseArmorOpenThresholdHealth)
            {
                return runtimeTargets;
            }

            var runtimeWithCore = runtimeTargets.ToList();
            if (!runtimeWithCore.Any(candidate => string.Equals(candidate.Id, "base_core", StringComparison.OrdinalIgnoreCase)))
            {
                double runtimeBaseYawDeg = NormalizeDeg(target.AngleDeg);
                double runtimeBaseYawRad = DegreesToRadians(runtimeBaseYawDeg);
                double runtimeMetersToWorld = 1.0 / Math.Max(metersPerWorldUnit, 1e-6);
                double runtimeBodyLength = Math.Clamp(target.BodyLengthM, 1.10, 2.35);
                double runtimeBodyHeight = Math.Clamp(target.BodyHeightM, 0.70, 1.60);
                double runtimeArmorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateWidthM, target.ArmorPlateHeightM), 0.04, 0.60);
                double coreForwardM = runtimeBodyLength * 0.15;
                runtimeWithCore.Add(new ArmorPlateTarget(
                    "base_core",
                    target.X + Math.Cos(runtimeBaseYawRad) * coreForwardM * runtimeMetersToWorld,
                    target.Y + Math.Sin(runtimeBaseYawRad) * coreForwardM * runtimeMetersToWorld,
                    target.GroundHeightM + runtimeBodyHeight * 0.70,
                    runtimeBaseYawDeg,
                    runtimeArmorSideLengthM));
            }

            return runtimeWithCore;
        }

        double baseYawDeg = NormalizeDeg(target.AngleDeg);
        double baseYawRad = DegreesToRadians(baseYawDeg);
        double sideYawRad = baseYawRad + Math.PI * 0.5;
        double metersToWorld = 1.0 / Math.Max(metersPerWorldUnit, 1e-6);
        double bodyLength = Math.Clamp(target.BodyLengthM, 1.10, 2.35);
        double bodyHeight = Math.Clamp(target.BodyHeightM, 0.70, 1.60);
        double slideM = Math.Sin(Math.Max(0.0, gameTimeSec) * BaseTopArmorSlideSpeedRadPerSec) * BaseTopArmorSlideAmplitudeM;
        double topForwardM = bodyLength * 0.06;
        double armorSideLengthM = Math.Clamp(Math.Max(target.ArmorPlateWidthM, target.ArmorPlateHeightM), 0.04, 0.60);

        var plates = new List<ArmorPlateTarget>(2)
        {
            new(
                "base_top_slide",
                target.X + (Math.Cos(baseYawRad) * (topForwardM + target.StructureTopArmorOffsetXM) + Math.Cos(sideYawRad) * (slideM + target.StructureTopArmorOffsetZM)) * metersToWorld,
                target.Y + (Math.Sin(baseYawRad) * (topForwardM + target.StructureTopArmorOffsetXM) + Math.Sin(sideYawRad) * (slideM + target.StructureTopArmorOffsetZM)) * metersToWorld,
                target.GroundHeightM + Math.Max(0.05, target.StructureTopArmorCenterHeightM > 1e-6 ? target.StructureTopArmorCenterHeightM : bodyHeight * (BaseTopArmorCenterHeightM / BaseDiagramHeightM)),
                baseYawDeg,
                armorSideLengthM),
        };

        if (target.Health < BaseArmorOpenThresholdHealth)
        {
            double coreForwardM = bodyLength * 0.15;
            plates.Add(new ArmorPlateTarget(
                "base_core",
                target.X + Math.Cos(baseYawRad) * coreForwardM * metersToWorld,
                target.Y + Math.Sin(baseYawRad) * coreForwardM * metersToWorld,
                target.GroundHeightM + bodyHeight * 0.70,
                baseYawDeg,
                armorSideLengthM));
        }

        return plates;
    }

    private static (double DistanceSquared, Vector3 Point) PointToSegmentDistanceSquared(
        Vector3 point,
        Vector3 segmentStart,
        Vector3 segmentEnd)
    {
        Vector3 segment = segmentEnd - segmentStart;
        float lengthSq = segment.LengthSquared();
        if (lengthSq <= 1e-8f)
        {
            Vector3 delta = point - segmentStart;
            return (delta.LengthSquared(), segmentStart);
        }

        float t = Vector3.Dot(point - segmentStart, segment) / lengthSq;
        t = Math.Clamp(t, 0f, 1f);
        Vector3 projection = segmentStart + segment * t;
        Vector3 diff = point - projection;
        return (diff.LengthSquared(), projection);
    }

    private static double ComputeAutoAimDistanceCoefficient(double distanceM, double maxDistanceM)
    {
        double effectiveMax = Math.Max(1.0, maxDistanceM);
        if (distanceM <= 1.0)
        {
            return 0.50;
        }

        double t = Math.Clamp((distanceM - 1.0) / Math.Max(1e-6, effectiveMax - 1.0), 0.0, 1.0);
        return 0.50 + t * 0.50;
    }

    private static double ComputeAutoAimMotionCoefficient(SimulationWorldState world, SimulationEntity shooter, SimulationEntity target)
    {
        ArmorPlateTarget representativePlate = GetAttackableArmorPlateTargets(target, Math.Max(world.MetersPerWorldUnit, 1e-6), world.GameTimeSec)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(representativePlate.Id))
        {
            return 1.0;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        (double plateVelocityXWorldPerSec, double plateVelocityYWorldPerSec, _) =
            ResolveArmorPlatePointRelativeObservedVelocityWorld(world, shooter, target, representativePlate);
        double translationSpeedMps = Math.Sqrt(
            plateVelocityXWorldPerSec * plateVelocityXWorldPerSec
            + plateVelocityYWorldPerSec * plateVelocityYWorldPerSec) * metersPerWorldUnit;
        bool translating = translationSpeedMps > 0.12;
        bool rotating = Math.Abs(target.AngularVelocityDegPerSec) > 18.0 || target.SmallGyroActive;

        if (translating && rotating)
        {
            return 0.60;
        }

        if (rotating)
        {
            return 0.70;
        }

        if (translating)
        {
            return 0.80;
        }

        return 1.0;
    }

    private static (double VelocityXWorldPerSec, double VelocityYWorldPerSec, double VelocityZMps) ResolveArmorPlatePointVelocityWorld(
        SimulationWorldState world,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        double offsetXWorld = plate.X - target.X;
        double offsetYWorld = plate.Y - target.Y;
        double omegaRadPerSec = ResolveAutoAimAngularVelocityRadPerSec(target);
        double angularVelocityXWorld = -omegaRadPerSec * offsetYWorld;
        double angularVelocityYWorld = omegaRadPerSec * offsetXWorld;
        return (
            target.VelocityXWorldPerSec + angularVelocityXWorld,
            target.VelocityYWorldPerSec + angularVelocityYWorld,
            target.VerticalVelocityMps);
    }
    private static (double VelocityXWorldPerSec, double VelocityYWorldPerSec, double VelocityZMps) ResolveArmorPlatePointObservedVelocityWorld(
        SimulationWorldState world,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        if (TryResolveDynamicStructurePlateVelocityWorld(world, target, plate, out (double VelocityXWorldPerSec, double VelocityYWorldPerSec, double VelocityZMps) structureVelocity))
        {
            return structureVelocity;
        }

        double offsetXWorld = plate.X - target.X;
        double offsetYWorld = plate.Y - target.Y;
        double omegaRadPerSec = ResolveAutoAimAngularVelocityRadPerSec(target);
        double angularVelocityXWorld = -omegaRadPerSec * offsetYWorld;
        double angularVelocityYWorld = omegaRadPerSec * offsetXWorld;
        return (
            ResolveObservedVelocityXWorldPerSec(target) + angularVelocityXWorld,
            ResolveObservedVelocityYWorldPerSec(target) + angularVelocityYWorld,
            target.VerticalVelocityMps);
    }
    private static bool TryResolveDynamicStructurePlateVelocityWorld(
        SimulationWorldState world,
        SimulationEntity target,
        ArmorPlateTarget plate,
        out (double VelocityXWorldPerSec, double VelocityYWorldPerSec, double VelocityZMps) velocity)
    {
        velocity = default;
        if (!IsStructure(target))
        {
            return false;
        }

        double offsetXWorld = plate.X - target.X;
        double offsetYWorld = plate.Y - target.Y;
        double omegaRadPerSec = ResolveAutoAimAngularVelocityRadPerSec(target);
        double angularVelocityXWorld = -omegaRadPerSec * offsetYWorld;
        double angularVelocityYWorld = omegaRadPerSec * offsetXWorld;
        velocity = (
            ResolveObservedVelocityXWorldPerSec(target) + angularVelocityXWorld,
            ResolveObservedVelocityYWorldPerSec(target) + angularVelocityYWorld,
            target.VerticalVelocityMps);
        return true;
    }
    private static (double VelocityXWorldPerSec, double VelocityYWorldPerSec, double VelocityZMps) ResolveArmorPlatePointRelativeVelocityWorld(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        (double plateVelocityXWorldPerSec, double plateVelocityYWorldPerSec, double plateVelocityZMps) =
            ResolveArmorPlatePointVelocityWorld(world, target, plate);
        return (
            plateVelocityXWorldPerSec - shooter.VelocityXWorldPerSec,
            plateVelocityYWorldPerSec - shooter.VelocityYWorldPerSec,
            plateVelocityZMps - shooter.VerticalVelocityMps);
    }

    private static (double VelocityXWorldPerSec, double VelocityYWorldPerSec, double VelocityZMps) ResolveArmorPlatePointRelativeObservedVelocityWorld(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate)
    {
        (double plateVelocityXWorldPerSec, double plateVelocityYWorldPerSec, double plateVelocityZMps) =
            ResolveArmorPlatePointObservedVelocityWorld(world, target, plate);
        return (
            plateVelocityXWorldPerSec - ResolveObservedVelocityXWorldPerSec(shooter),
            plateVelocityYWorldPerSec - ResolveObservedVelocityYWorldPerSec(shooter),
            plateVelocityZMps - shooter.VerticalVelocityMps);
    }

    private static double ResolveAutoAimAngularVelocityRadPerSec(SimulationEntity target)
        // 渲染与玩法 yaw 正方向和自瞄横向约定相反，因此预测时需要反转观测到的底盘角速度。
        => DegreesToRadians(ResolveObservedAngularVelocityDegPerSec(target));

    private static double ResolveObservedVelocityXWorldPerSec(SimulationEntity entity)
        => entity.HasObservedKinematics ? entity.ObservedVelocityXWorldPerSec : entity.VelocityXWorldPerSec;

    private static double ResolveObservedVelocityYWorldPerSec(SimulationEntity entity)
        => entity.HasObservedKinematics ? entity.ObservedVelocityYWorldPerSec : entity.VelocityYWorldPerSec;

    private static double ResolveObservedAngularVelocityDegPerSec(SimulationEntity entity)
        => entity.HasObservedKinematics ? entity.ObservedAngularVelocityDegPerSec : entity.AngularVelocityDegPerSec;

    private static double ResolveAutoAimLateralErrorSign(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double predictedX,
        double predictedY,
        double fallback)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double aimYawRad = Math.Atan2(predictedY - shooter.Y, predictedX - shooter.X);
        double sideX = -Math.Sin(aimYawRad);
        double sideY = Math.Cos(aimYawRad);

        (double plateVelocityXWorldPerSec, double plateVelocityYWorldPerSec, _) =
            ResolveArmorPlatePointRelativeObservedVelocityWorld(world, shooter, target, plate);
        double lateralVelocityMps =
            (plateVelocityXWorldPerSec * sideX
                + plateVelocityYWorldPerSec * sideY)
            * metersPerWorldUnit;

        if (Math.Abs(lateralVelocityMps) <= 0.035)
        {
            return fallback;
        }

        double motionBias = Math.Clamp(lateralVelocityMps / 1.15, -1.0, 1.0);
        double blended = motionBias * 0.86 + fallback * 0.18;
        if (Math.Abs(blended) < 0.18)
        {
            return Math.Sign(lateralVelocityMps);
        }

        return Math.Clamp(blended, -1.0, 1.0);
    }

    private static int StableHash(params string?[] values)
    {
        unchecked
        {
            int hash = 17;
            foreach (string? value in values)
            {
                if (string.IsNullOrEmpty(value))
                {
                    hash *= 31;
                    continue;
                }

                foreach (char ch in value)
                {
                    hash = hash * 31 + ch;
                }
            }

            return hash;
        }
    }

    private static double StableSignedUnit(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return x / (double)uint.MaxValue * 2.0 - 1.0;
        }
    }

    private static double SmoothSignedNoise(double phase, int seed)
    {
        double a = Math.Sin(phase + (seed & 255) * 0.071);
        double b = Math.Sin(phase * 0.53 + ((seed >> 8) & 255) * 0.047);
        return Math.Clamp(a * 0.68 + b * 0.32, -1.0, 1.0);
    }
}
