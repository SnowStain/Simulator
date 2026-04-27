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

        double dtSec = filter.Initialized
            ? Math.Clamp(world.GameTimeSec - filter.LastUpdateTimeSec, 1.0 / 240.0, 0.12)
            : 1.0 / 60.0;
        bool rotatingTarget =
            string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
            || target.SmallGyroActive;
        double measurementNoiseM = rotatingTarget ? 0.010 : 0.016;
        double accelerationNoiseMps2 = string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            ? 14.0
            : string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
                ? 10.0
                : target.SmallGyroActive
                    ? 22.0
                    : 8.0;

        if (!filter.Initialized)
        {
            ResolveInitialVelocity(world, filter, observedXWorld, observedYWorld, observedHeightM, metersPerWorldUnit, out double vx, out double vy, out double vz);
            filter.Initialize(observedXWorld, observedYWorld, observedHeightM, vx, vy, vz, metersPerWorldUnit);
        }
        else
        {
            filter.Update(observedXWorld, observedYWorld, observedHeightM, dtSec, metersPerWorldUnit, measurementNoiseM, accelerationNoiseMps2);
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

        double dtSec = filter.Initialized
            ? Math.Clamp(world.GameTimeSec - filter.LastUpdateTimeSec, 1.0 / 240.0, 0.12)
            : 1.0 / 60.0;
        bool rotatingTarget =
            string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
            || target.SmallGyroActive;
        double measurementNoiseM = rotatingTarget ? 0.010 : 0.016;
        double accelerationNoiseMps2 = string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            ? 14.0
            : string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
                ? 10.0
                : target.SmallGyroActive
                    ? 22.0
                    : 8.0;
        double jerkNoiseMps3 = accelerationNoiseMps2 * (rotatingTarget ? 3.4 : 2.4);

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
                measurementNoiseM,
                jerkNoiseMps3);
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
}
