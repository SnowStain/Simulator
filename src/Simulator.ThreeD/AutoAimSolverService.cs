using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed class AutoAimSolverService
{
    private readonly bool _useThirdOrderEkfPoseChain;
    private readonly Dictionary<string, AutoAimObservationFilterState> _kalmanFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AutoAimThirdOrderEkfPoseFilterState> _ekf3Filters = new(StringComparer.OrdinalIgnoreCase);

    public AutoAimSolverService(bool useThirdOrderEkfPoseChain = true)
    {
        _useThirdOrderEkfPoseChain = useThirdOrderEkfPoseChain;
    }

    public void ClearEntity(string shooterId)
    {
        _kalmanFilters.Remove(shooterId);
        _ekf3Filters.Remove(shooterId);
    }

    public AutoAimObservedState ResolveObservationState(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget aimPlate,
        ArmorPlateTarget observationPlate)
    {
        return _useThirdOrderEkfPoseChain
            ? UpdateThirdOrderEkfObservationState(world, shooter, target, aimPlate, observationPlate)
            : UpdateKalmanObservationState(world, shooter, target, aimPlate, observationPlate);
    }

    public AutoAimSolution ComputeSolution(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double maxDistanceM,
        AutoAimObservedState observationState)
    {
        return _useThirdOrderEkfPoseChain
            ? SimulationCombatMath.ComputeObservationDrivenAutoAimSolutionThirdOrderEkf(
                world,
                shooter,
                target,
                plate,
                maxDistanceM,
                observationState.AimPointXWorld,
                observationState.AimPointYWorld,
                observationState.AimPointHeightM,
                observationState.VelocityXMps,
                observationState.VelocityYMps,
                observationState.VelocityZMps,
                observationState.AccelerationXMps2,
                observationState.AccelerationYMps2,
                observationState.AccelerationZMps2,
                observationState.AngularVelocityRadPerSec)
            : SimulationCombatMath.ComputeObservationDrivenAutoAimSolution(
                world,
                shooter,
                target,
                plate,
                maxDistanceM,
                observationState.AimPointXWorld,
                observationState.AimPointYWorld,
                observationState.AimPointHeightM,
                observationState.VelocityXMps,
                observationState.VelocityYMps,
                observationState.VelocityZMps,
                observationState.AngularVelocityRadPerSec);
    }

    private AutoAimObservedState UpdateKalmanObservationState(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget aimPlate,
        ArmorPlateTarget observationPlate)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        string observationKey = $"{target.Id}:{aimPlate.Id}:{observationPlate.Id}";
        string targetKind = SimulationCombatMath.ResolveAutoAimTargetKind(target, aimPlate);
        double observedXWorld = observationPlate.X;
        double observedYWorld = observationPlate.Y;
        double observedHeightM = observationPlate.HeightM;

        if (!_kalmanFilters.TryGetValue(shooter.Id, out AutoAimObservationFilterState? filter)
            || !string.Equals(filter.ObservationKey, observationKey, StringComparison.OrdinalIgnoreCase)
            || world.GameTimeSec - filter.LastUpdateTimeSec > 0.30)
        {
            filter = AutoAimObservationFilterState.Create(observationKey, target.Id, aimPlate.Id, observationPlate.Id, targetKind);
        }

        AutoAimObservationTuning tuning = ResolveObservationTuning(shooter, target, targetKind);
        double dtSec = filter.Initialized
            ? Math.Clamp(world.GameTimeSec - filter.LastUpdateTimeSec, 1.0 / 240.0, tuning.MaxDtSec)
            : 1.0 / 60.0;
        SanitizeObservationMeasurement(
            world,
            target,
            targetKind,
            filter.HasLastMeasurement,
            filter.LastMeasurementXWorld,
            filter.LastMeasurementYWorld,
            filter.LastMeasurementHeightM,
            filter.LastMeasurementTimeSec,
            metersPerWorldUnit,
            ref observedXWorld,
            ref observedYWorld,
            ref observedHeightM);

        if (!filter.Initialized)
        {
            ResolveInitialVelocity(world, filter, observedXWorld, observedYWorld, observedHeightM, metersPerWorldUnit, out double vx, out double vy, out double vz);
            filter.Initialize(observedXWorld, observedYWorld, observedHeightM, vx, vy, vz, metersPerWorldUnit);
        }
        else
        {
            filter.Update(observedXWorld, observedYWorld, observedHeightM, dtSec, metersPerWorldUnit, tuning.MeasurementNoiseM, tuning.AccelerationNoiseMps2);
        }

        StoreMeasurement(filter, world.GameTimeSec, observedXWorld, observedYWorld, observedHeightM);

        double angularVelocityRadPerSec = TryResolveObservedPlateAngularVelocity(
            world,
            target,
            aimPlate,
            filter.FilteredXWorld,
            filter.FilteredYWorld,
            filter.FilteredVelocityXMps,
            filter.FilteredVelocityYMps,
            out double resolvedOmega)
                ? resolvedOmega
                : 0.0;

        _kalmanFilters[shooter.Id] = filter;
        if (string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(aimPlate.Id, observationPlate.Id, StringComparison.OrdinalIgnoreCase)
            && TryProjectEnergyObservationToAimRing(
                target,
                aimPlate,
                filter.FilteredXWorld,
                filter.FilteredYWorld,
                filter.FilteredHeightM,
                filter.FilteredVelocityXMps,
                filter.FilteredVelocityYMps,
                filter.FilteredVelocityZMps,
                0.0,
                0.0,
                0.0,
                out AutoAimObservedState projectedState))
        {
            return projectedState with { AngularVelocityRadPerSec = angularVelocityRadPerSec };
        }

        return new AutoAimObservedState(
            filter.FilteredXWorld,
            filter.FilteredYWorld,
            filter.FilteredHeightM,
            filter.FilteredVelocityXMps,
            filter.FilteredVelocityYMps,
            filter.FilteredVelocityZMps,
            0.0,
            0.0,
            0.0,
            angularVelocityRadPerSec);
    }

    private AutoAimObservedState UpdateThirdOrderEkfObservationState(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget aimPlate,
        ArmorPlateTarget observationPlate)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        string observationKey = $"{target.Id}:{aimPlate.Id}:{observationPlate.Id}";
        string targetKind = SimulationCombatMath.ResolveAutoAimTargetKind(target, aimPlate);
        double observedXWorld = observationPlate.X;
        double observedYWorld = observationPlate.Y;
        double observedHeightM = observationPlate.HeightM;

        if (!_ekf3Filters.TryGetValue(shooter.Id, out AutoAimThirdOrderEkfPoseFilterState? filter)
            || !string.Equals(filter.ObservationKey, observationKey, StringComparison.OrdinalIgnoreCase)
            || world.GameTimeSec - filter.LastUpdateTimeSec > 0.30)
        {
            filter = AutoAimThirdOrderEkfPoseFilterState.Create(observationKey, target.Id, aimPlate.Id, observationPlate.Id, targetKind);
        }

        AutoAimObservationTuning tuning = ResolveObservationTuning(shooter, target, targetKind);
        double dtSec = filter.Initialized
            ? Math.Clamp(world.GameTimeSec - filter.LastUpdateTimeSec, 1.0 / 240.0, tuning.MaxDtSec)
            : 1.0 / 60.0;
        SanitizeObservationMeasurement(
            world,
            target,
            targetKind,
            filter.HasLastMeasurement,
            filter.LastMeasurementXWorld,
            filter.LastMeasurementYWorld,
            filter.LastMeasurementHeightM,
            filter.LastMeasurementTimeSec,
            metersPerWorldUnit,
            ref observedXWorld,
            ref observedYWorld,
            ref observedHeightM);

        if (!filter.Initialized)
        {
            ResolveInitialVelocity(world, filter, observedXWorld, observedYWorld, observedHeightM, metersPerWorldUnit, out double vx, out double vy, out double vz);
            filter.Initialize(observedXWorld, observedYWorld, observedHeightM, vx, vy, vz, metersPerWorldUnit);
        }
        else
        {
            filter.Update(
                observedXWorld,
                observedYWorld,
                observedHeightM,
                shooter.X,
                shooter.Y,
                dtSec,
                metersPerWorldUnit,
                tuning.MeasurementNoiseM,
                tuning.JerkNoiseMps3);
        }

        StoreMeasurement(filter, world.GameTimeSec, observedXWorld, observedYWorld, observedHeightM);

        double angularVelocityRadPerSec = TryResolveObservedPlateAngularVelocity(
            world,
            target,
            aimPlate,
            filter.FilteredXWorld,
            filter.FilteredYWorld,
            filter.FilteredVelocityXMps,
            filter.FilteredVelocityYMps,
            out double resolvedOmega)
                ? resolvedOmega
                : 0.0;

        _ekf3Filters[shooter.Id] = filter;
        if (string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(aimPlate.Id, observationPlate.Id, StringComparison.OrdinalIgnoreCase)
            && TryProjectEnergyObservationToAimRing(
                target,
                aimPlate,
                filter.FilteredXWorld,
                filter.FilteredYWorld,
                filter.FilteredHeightM,
                filter.FilteredVelocityXMps,
                filter.FilteredVelocityYMps,
                filter.FilteredVelocityZMps,
                filter.FilteredAccelerationXMps2,
                filter.FilteredAccelerationYMps2,
                filter.FilteredAccelerationZMps2,
                out AutoAimObservedState projectedState))
        {
            return projectedState with { AngularVelocityRadPerSec = angularVelocityRadPerSec };
        }

        return new AutoAimObservedState(
            filter.FilteredXWorld,
            filter.FilteredYWorld,
            filter.FilteredHeightM,
            filter.FilteredVelocityXMps,
            filter.FilteredVelocityYMps,
            filter.FilteredVelocityZMps,
            filter.FilteredAccelerationXMps2,
            filter.FilteredAccelerationYMps2,
            filter.FilteredAccelerationZMps2,
            angularVelocityRadPerSec);
    }

    private static bool TryProjectEnergyObservationToAimRing(
        SimulationEntity target,
        ArmorPlateTarget aimPlate,
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double observedVelocityXMps,
        double observedVelocityYMps,
        double observedVelocityZMps,
        double observedAccelerationXMps2,
        double observedAccelerationYMps2,
        double observedAccelerationZMps2,
        out AutoAimObservedState projectedState)
    {
        projectedState = default;
        double observedRelX = observedXWorld - target.X;
        double observedRelY = observedYWorld - target.Y;
        double observedRadius = Math.Sqrt(observedRelX * observedRelX + observedRelY * observedRelY);
        double aimRelX = aimPlate.X - target.X;
        double aimRelY = aimPlate.Y - target.Y;
        double aimRadius = Math.Sqrt(aimRelX * aimRelX + aimRelY * aimRelY);
        if (observedRadius <= 1e-6 || aimRadius <= 1e-6)
        {
            return false;
        }

        double radiusScale = aimRadius / observedRadius;
        projectedState = new AutoAimObservedState(
            target.X + observedRelX * radiusScale,
            target.Y + observedRelY * radiusScale,
            aimPlate.HeightM > 1e-6 ? aimPlate.HeightM : observedHeightM,
            observedVelocityXMps * radiusScale,
            observedVelocityYMps * radiusScale,
            observedVelocityZMps,
            observedAccelerationXMps2 * radiusScale,
            observedAccelerationYMps2 * radiusScale,
            observedAccelerationZMps2,
            0.0);
        return true;
    }

    private static void ResolveInitialVelocity(
        SimulationWorldState world,
        AutoAimObservationFilterState filter,
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double metersPerWorldUnit,
        out double initialVelocityXMps,
        out double initialVelocityYMps,
        out double initialVelocityZMps)
    {
        initialVelocityXMps = 0.0;
        initialVelocityYMps = 0.0;
        initialVelocityZMps = 0.0;
        if (!filter.HasLastMeasurement)
        {
            return;
        }

        double measurementDtSec = Math.Clamp(world.GameTimeSec - filter.LastMeasurementTimeSec, 1.0 / 240.0, 0.20);
        initialVelocityXMps = (observedXWorld - filter.LastMeasurementXWorld) * metersPerWorldUnit / measurementDtSec;
        initialVelocityYMps = (observedYWorld - filter.LastMeasurementYWorld) * metersPerWorldUnit / measurementDtSec;
        initialVelocityZMps = (observedHeightM - filter.LastMeasurementHeightM) / measurementDtSec;
    }

    private static void ResolveInitialVelocity(
        SimulationWorldState world,
        AutoAimThirdOrderEkfPoseFilterState filter,
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double metersPerWorldUnit,
        out double initialVelocityXMps,
        out double initialVelocityYMps,
        out double initialVelocityZMps)
    {
        initialVelocityXMps = 0.0;
        initialVelocityYMps = 0.0;
        initialVelocityZMps = 0.0;
        if (!filter.HasLastMeasurement)
        {
            return;
        }

        double measurementDtSec = Math.Clamp(world.GameTimeSec - filter.LastMeasurementTimeSec, 1.0 / 240.0, 0.20);
        initialVelocityXMps = (observedXWorld - filter.LastMeasurementXWorld) * metersPerWorldUnit / measurementDtSec;
        initialVelocityYMps = (observedYWorld - filter.LastMeasurementYWorld) * metersPerWorldUnit / measurementDtSec;
        initialVelocityZMps = (observedHeightM - filter.LastMeasurementHeightM) / measurementDtSec;
    }

    private static void StoreMeasurement(AutoAimObservationFilterState filter, double gameTimeSec, double observedXWorld, double observedYWorld, double observedHeightM)
    {
        filter.LastMeasurementXWorld = observedXWorld;
        filter.LastMeasurementYWorld = observedYWorld;
        filter.LastMeasurementHeightM = observedHeightM;
        filter.LastMeasurementTimeSec = gameTimeSec;
        filter.HasLastMeasurement = true;
        filter.LastUpdateTimeSec = gameTimeSec;
    }

    private static void StoreMeasurement(AutoAimThirdOrderEkfPoseFilterState filter, double gameTimeSec, double observedXWorld, double observedYWorld, double observedHeightM)
    {
        filter.LastMeasurementXWorld = observedXWorld;
        filter.LastMeasurementYWorld = observedYWorld;
        filter.LastMeasurementHeightM = observedHeightM;
        filter.LastMeasurementTimeSec = gameTimeSec;
        filter.HasLastMeasurement = true;
        filter.LastUpdateTimeSec = gameTimeSec;
    }

    private static void SanitizeObservationMeasurement(
        SimulationWorldState world,
        SimulationEntity target,
        string targetKind,
        bool hasLastMeasurement,
        double lastXWorld,
        double lastYWorld,
        double lastHeightM,
        double lastMeasurementTimeSec,
        double metersPerWorldUnit,
        ref double observedXWorld,
        ref double observedYWorld,
        ref double observedHeightM)
    {
        bool fastRotatingVehicle = target.SmallGyroActive
            && string.Equals(targetKind, "vehicle_armor", StringComparison.OrdinalIgnoreCase);
        if (!fastRotatingVehicle || !hasLastMeasurement)
        {
            return;
        }

        double dtSec = Math.Clamp(world.GameTimeSec - lastMeasurementTimeSec, 1.0 / 240.0, 0.10);
        double dxM = (observedXWorld - lastXWorld) * metersPerWorldUnit;
        double dyM = (observedYWorld - lastYWorld) * metersPerWorldUnit;
        double planarStepM = Math.Sqrt(dxM * dxM + dyM * dyM);
        double radiusM = Math.Sqrt(
            Math.Pow((lastXWorld - target.X) * metersPerWorldUnit, 2)
            + Math.Pow((lastYWorld - target.Y) * metersPerWorldUnit, 2));
        double targetLinearMps = Math.Sqrt(
            target.VelocityXWorldPerSec * target.VelocityXWorldPerSec
            + target.VelocityYWorldPerSec * target.VelocityYWorldPerSec)
            * metersPerWorldUnit;
        double omegaRadPerSec = Math.Abs(target.AngularVelocityDegPerSec) * Math.PI / 180.0;
        double maxPlanarStepM = (targetLinearMps + omegaRadPerSec * Math.Clamp(radiusM, 0.05, 0.65) + 1.25) * dtSec;
        maxPlanarStepM = Math.Clamp(maxPlanarStepM, 0.012, 0.42);
        if (planarStepM > maxPlanarStepM && planarStepM > 1e-6)
        {
            double ratio = maxPlanarStepM / planarStepM;
            observedXWorld = lastXWorld + (observedXWorld - lastXWorld) * ratio;
            observedYWorld = lastYWorld + (observedYWorld - lastYWorld) * ratio;
        }

        double maxHeightStepM = Math.Clamp(1.2 * dtSec, 0.008, 0.08);
        observedHeightM = Math.Clamp(observedHeightM, lastHeightM - maxHeightStepM, lastHeightM + maxHeightStepM);
    }

    private static bool TryResolveObservedPlateAngularVelocity(
        SimulationWorldState world,
        SimulationEntity target,
        ArmorPlateTarget aimPlate,
        double filteredXWorld,
        double filteredYWorld,
        double filteredVelocityXMps,
        double filteredVelocityYMps,
        out double angularVelocityRadPerSec)
    {
        angularVelocityRadPerSec = 0.0;
        double pivotX = target.X;
        double pivotY = target.Y;
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double radialXM = (filteredXWorld - pivotX) * metersPerWorldUnit;
        double radialYM = (filteredYWorld - pivotY) * metersPerWorldUnit;
        double radialLengthSq = radialXM * radialXM + radialYM * radialYM;
        if (radialLengthSq <= 1e-4)
        {
            return false;
        }

        angularVelocityRadPerSec =
            (radialXM * filteredVelocityYMps - radialYM * filteredVelocityXMps)
            / radialLengthSq;
        if (!double.IsFinite(angularVelocityRadPerSec))
        {
            angularVelocityRadPerSec = 0.0;
            return false;
        }

        if (target.SmallGyroActive
            && string.Equals(SimulationCombatMath.ResolveAutoAimTargetKind(target, aimPlate), "vehicle_armor", StringComparison.OrdinalIgnoreCase))
        {
            double modelOmegaRadPerSec = target.AngularVelocityDegPerSec * Math.PI / 180.0;
            if (Math.Abs(modelOmegaRadPerSec) > 0.05 && double.IsFinite(modelOmegaRadPerSec))
            {
                angularVelocityRadPerSec = Math.Clamp(modelOmegaRadPerSec, -12.0, 12.0);
                return true;
            }

            angularVelocityRadPerSec = Math.Clamp(angularVelocityRadPerSec, -8.0, 8.0);
            return Math.Abs(angularVelocityRadPerSec) >= 0.05;
        }

        if (string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(aimPlate.Id, "outpost_top", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Math.Abs(angularVelocityRadPerSec) < 0.02)
        {
            angularVelocityRadPerSec = 0.0;
            return false;
        }

        return true;
    }

    private static AutoAimObservationTuning ResolveObservationTuning(
        SimulationEntity shooter,
        SimulationEntity target,
        string targetKind)
    {
        bool rotatingTarget =
            string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
            || target.SmallGyroActive;
        bool largeRound = string.Equals(shooter.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);

        double measurementNoiseM = rotatingTarget ? 0.010 : 0.016;
        double accelerationNoiseMps2 = string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            ? 14.0
            : string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
                ? 10.0
                : target.SmallGyroActive
                    ? 22.0
                    : 8.0;
        double jerkNoiseMps3 = accelerationNoiseMps2 * (rotatingTarget ? 3.4 : 2.4);
        double maxDtSec = 0.12;

        if (largeRound)
        {
            measurementNoiseM *= rotatingTarget ? 0.94 : 0.84;
            accelerationNoiseMps2 *= rotatingTarget ? 0.90 : 0.78;
            jerkNoiseMps3 *= rotatingTarget ? 0.92 : 0.76;
            maxDtSec = rotatingTarget ? 0.10 : 0.09;
        }

        return new AutoAimObservationTuning(
            Math.Max(0.004, measurementNoiseM),
            Math.Max(2.5, accelerationNoiseMps2),
            Math.Max(8.0, jerkNoiseMps3),
            Math.Clamp(maxDtSec, 1.0 / 120.0, 0.12));
    }

    private readonly record struct AutoAimObservationTuning(
        double MeasurementNoiseM,
        double AccelerationNoiseMps2,
        double JerkNoiseMps3,
        double MaxDtSec);
}
