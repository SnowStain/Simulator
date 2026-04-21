namespace Simulator.ThreeD;

internal sealed record PlayerControlState
{
    public string? EntityId { get; init; }

    public bool Enabled { get; init; }

    public double MoveForward { get; init; }

    public double MoveRight { get; init; }

    public double TurretYawDeltaDeg { get; init; }

    public double GimbalPitchDeltaDeg { get; init; }

    public bool FirePressed { get; init; }

    public bool AutoAimPressed { get; init; }

    public bool AutoAimGuidanceOnly { get; init; }

    public bool JumpRequested { get; init; }

    public bool StepClimbModeActive { get; init; }

    public bool SmallGyroActive { get; init; }

    public bool BuyAmmoRequested { get; init; }

    public bool EnergyActivationPressed { get; init; }

    public bool HeroDeployToggleRequested { get; init; }

    public bool SuperCapActive { get; init; }

    public bool SentryStanceToggleRequested { get; init; }
}
