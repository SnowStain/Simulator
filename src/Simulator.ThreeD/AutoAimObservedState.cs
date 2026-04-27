namespace Simulator.ThreeD;

internal readonly record struct AutoAimObservedState(
    double AimPointXWorld,
    double AimPointYWorld,
    double AimPointHeightM,
    double VelocityXMps,
    double VelocityYMps,
    double VelocityZMps,
    double AccelerationXMps2,
    double AccelerationYMps2,
    double AccelerationZMps2,
    double AngularVelocityRadPerSec);
