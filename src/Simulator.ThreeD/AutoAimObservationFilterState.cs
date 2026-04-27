namespace Simulator.ThreeD;

internal sealed class AutoAimObservationFilterState
{
    private const double InitialPositionVariance = 0.20 * 0.20;
    private const double InitialVelocityVariance = 4.5 * 4.5;

    private KalmanAxisState _x;
    private KalmanAxisState _y;
    private KalmanAxisState _z;

    private AutoAimObservationFilterState(
        string observationKey,
        string targetId,
        string aimPlateId,
        string observationPlateId,
        string targetKind)
    {
        ObservationKey = observationKey;
        TargetId = targetId;
        AimPlateId = aimPlateId;
        ObservationPlateId = observationPlateId;
        TargetKind = targetKind;
    }

    public string ObservationKey { get; }

    public string TargetId { get; }

    public string AimPlateId { get; }

    public string ObservationPlateId { get; }

    public string TargetKind { get; }

    public bool Initialized { get; private set; }

    public bool HasLastMeasurement { get; set; }

    public double LastMeasurementXWorld { get; set; }

    public double LastMeasurementYWorld { get; set; }

    public double LastMeasurementHeightM { get; set; }

    public double LastMeasurementTimeSec { get; set; } = -999.0;

    public double LastUpdateTimeSec { get; set; } = -999.0;

    public double LastMetersPerWorldUnit { get; private set; } = 1.0;

    public double FilteredXWorld => _x.Position;

    public double FilteredYWorld => _y.Position;

    public double FilteredHeightM => _z.Position;

    public double FilteredVelocityXMps => _x.VelocityPerSec * LastMetersPerWorldUnit;

    public double FilteredVelocityYMps => _y.VelocityPerSec * LastMetersPerWorldUnit;

    public double FilteredVelocityZMps => _z.VelocityPerSec;

    public static AutoAimObservationFilterState Create(
        string observationKey,
        string targetId,
        string aimPlateId,
        string observationPlateId,
        string targetKind)
        => new(observationKey, targetId, aimPlateId, observationPlateId, targetKind);

    public void Initialize(
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double velocityXMps,
        double velocityYMps,
        double velocityZMps,
        double metersPerWorldUnit)
    {
        LastMetersPerWorldUnit = Math.Max(metersPerWorldUnit, 1e-6);
        _x = KalmanAxisState.CreateWorldAxis(observedXWorld, velocityXMps / LastMetersPerWorldUnit);
        _y = KalmanAxisState.CreateWorldAxis(observedYWorld, velocityYMps / LastMetersPerWorldUnit);
        _z = KalmanAxisState.CreateMetricAxis(observedHeightM, velocityZMps);
        Initialized = true;
    }

    public void Update(
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double dtSec,
        double metersPerWorldUnit,
        double measurementNoiseM,
        double accelerationNoiseMps2)
    {
        LastMetersPerWorldUnit = Math.Max(metersPerWorldUnit, 1e-6);
        double worldMeasurementVariance = Math.Pow(measurementNoiseM / Math.Max(metersPerWorldUnit, 1e-6), 2);
        double metricMeasurementVariance = measurementNoiseM * measurementNoiseM;
        double worldAccelerationVariance = Math.Pow(accelerationNoiseMps2 / Math.Max(metersPerWorldUnit, 1e-6), 2);
        double metricAccelerationVariance = accelerationNoiseMps2 * accelerationNoiseMps2;
        _x.Update(observedXWorld, dtSec, worldMeasurementVariance, worldAccelerationVariance);
        _y.Update(observedYWorld, dtSec, worldMeasurementVariance, worldAccelerationVariance);
        _z.Update(observedHeightM, dtSec, metricMeasurementVariance, metricAccelerationVariance);
    }

    private struct KalmanAxisState
    {
        public double Position;
        public double VelocityPerSec;
        public double P00;
        public double P01;
        public double P10;
        public double P11;

        public static KalmanAxisState CreateWorldAxis(double positionWorld, double velocityPerSec)
            => new()
            {
                Position = positionWorld,
                VelocityPerSec = velocityPerSec,
                P00 = InitialPositionVariance,
                P11 = InitialVelocityVariance,
            };

        public static KalmanAxisState CreateMetricAxis(double positionM, double velocityPerSec)
            => new()
            {
                Position = positionM,
                VelocityPerSec = velocityPerSec,
                P00 = InitialPositionVariance,
                P11 = InitialVelocityVariance,
            };

        public void Update(
            double measurement,
            double dtSec,
            double measurementVariance,
            double accelerationVariance)
        {
            double dt = Math.Clamp(dtSec, 1.0 / 240.0, 0.12);
            double predictedPosition = Position + VelocityPerSec * dt;
            double predictedVelocity = VelocityPerSec;
            double dt2 = dt * dt;
            double dt3 = dt2 * dt;
            double dt4 = dt2 * dt2;
            double predictedP00 = P00 + dt * (P10 + P01) + dt2 * P11 + 0.25 * dt4 * accelerationVariance;
            double predictedP01 = P01 + dt * P11 + 0.5 * dt3 * accelerationVariance;
            double predictedP10 = P10 + dt * P11 + 0.5 * dt3 * accelerationVariance;
            double predictedP11 = P11 + dt2 * accelerationVariance;
            double innovation = measurement - predictedPosition;
            double innovationVariance = predictedP00 + Math.Max(1e-8, measurementVariance);
            double gainPosition = predictedP00 / innovationVariance;
            double gainVelocity = predictedP10 / innovationVariance;
            Position = predictedPosition + gainPosition * innovation;
            VelocityPerSec = predictedVelocity + gainVelocity * innovation;
            P00 = Math.Max(1e-10, (1.0 - gainPosition) * predictedP00);
            P01 = (1.0 - gainPosition) * predictedP01;
            P10 = predictedP10 - gainVelocity * predictedP00;
            P11 = Math.Max(1e-10, predictedP11 - gainVelocity * predictedP01);
        }
    }
}
