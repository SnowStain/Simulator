namespace Simulator.Core.Gameplay;

public sealed class SimulationProjectile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string ShooterId { get; init; } = string.Empty;

    public string Team { get; init; } = "red";

    public string AmmoType { get; init; } = "17mm";

    public string? PreferredTargetId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double HeightM { get; set; }

    public double VelocityXWorldPerSec { get; set; }

    public double VelocityYWorldPerSec { get; set; }

    public double VelocityZMps { get; set; }

    public double RemainingLifeSec { get; set; } = 3.0;
}

public sealed class SimulationEntity
{
    public string Id { get; init; } = string.Empty;

    public string Team { get; init; } = "red";

    public string EntityType { get; init; } = "robot";

    public string RoleKey { get; init; } = "infantry";

    public int Level { get; set; } = 1;

    public int MaxLevel { get; set; } = 5;

    public double Experience { get; set; }

    public string HeroPerformanceMode { get; set; } = "ranged_priority";

    public string InfantryDurabilityMode { get; set; } = "hp_priority";

    public string InfantryWeaponMode { get; set; } = "cooling_priority";

    public string SentryControlMode { get; set; } = "full_auto";

    public string SentryStance { get; set; } = "attack";

    public double X { get; set; }

    public double Y { get; set; }

    public double GroundHeightM { get; set; }

    public double AirborneHeightM { get; set; }

    public double VerticalVelocityMps { get; set; }

    public double AngleDeg { get; set; }

    public double TurretYawDeg { get; set; }

    public double GimbalPitchDeg { get; set; }

    public bool AutoAimRequested { get; set; }

    public bool AutoAimLocked { get; set; }

    public string? AutoAimTargetId { get; set; }

    public string? AutoAimPlateId { get; set; }

    public string AutoAimPlateDirection { get; set; } = string.Empty;

    public double AutoAimAccuracy { get; set; }

    public double AutoAimDistanceCoefficient { get; set; }

    public double AutoAimMotionCoefficient { get; set; }

    public double AutoAimLeadTimeSec { get; set; }

    public double AutoAimLeadDistanceM { get; set; }

    public bool AutoAimHasSmoothedAim { get; set; }

    public string? AutoAimLockKey { get; set; }

    public double AutoAimSmoothedYawDeg { get; set; }

    public double AutoAimSmoothedPitchDeg { get; set; }

    public double VelocityXWorldPerSec { get; set; }

    public double VelocityYWorldPerSec { get; set; }

    public double AngularVelocityDegPerSec { get; set; }

    public double ChassisTargetYawDeg { get; set; }

    public double LastAimSpeedMps { get; set; }

    public double LastAimHeightM { get; set; }

    public double AutoAimInstabilityTimerSec { get; set; }

    public double MoveInputForward { get; set; }

    public double MoveInputRight { get; set; }

    public bool IsPlayerControlled { get; set; }

    public bool IsFireCommandActive { get; set; }

    public bool JumpRequested { get; set; }

    public bool StepClimbModeActive { get; set; }

    public bool SmallGyroActive { get; set; }

    public bool BuyAmmoRequested { get; set; }

    public bool IsSimulationSuppressed { get; set; }

    public double DirectStepHeightM { get; set; } = 0.06;

    public double MaxStepClimbHeightM { get; set; } = 0.35;

    public double StepClimbDurationSec { get; set; } = 1.0;

    public bool TraversalActive { get; set; }

    public double TraversalProgress { get; set; }

    public double TraversalDirectionDeg { get; set; }

    public double TraversalStartX { get; set; }

    public double TraversalStartY { get; set; }

    public double TraversalTargetX { get; set; }

    public double TraversalTargetY { get; set; }

    public double TraversalStartGroundHeightM { get; set; }

    public double TraversalTargetGroundHeightM { get; set; }

    public double CollisionRadiusWorld { get; set; }

    public double MassKg { get; set; } = 20.0;

    public double ChassisSpeedScale { get; set; } = 1.0;

    public bool ChassisSupportsJump { get; set; }

    public double ChassisDrivePowerLimitW { get; set; } = 180.0;

    public double RuleDrivePowerLimitW { get; set; }

    public double ChassisDriveIdleDrawW { get; set; } = 16.0;

    public double ChassisDriveRpmCoeff { get; set; } = 0.00005;

    public double ChassisDriveAccelCoeff { get; set; } = 0.012;

    public double ChassisPowerDrawW { get; set; }

    public double ChassisRpm { get; set; }

    public double ChassisSpeedLimitMps { get; set; }

    public double ChassisPowerRatio { get; set; } = 1.0;

    public double ChassisEnergy { get; set; }

    public double MaxChassisEnergy { get; set; }

    public double ChassisEcoPowerLimitW { get; set; } = 35.0;

    public double ChassisBoostThresholdEnergy { get; set; } = 25000.0;

    public double ChassisBoostMultiplier { get; set; } = 1.25;

    public double ChassisBoostPowerCapW { get; set; } = 200.0;

    public string ChassisSubtype { get; set; } = string.Empty;

    public string BodyShape { get; set; } = "box";

    public string WheelStyle { get; set; } = "standard";

    public string FrontClimbAssistStyle { get; set; } = "none";

    public string RearClimbAssistStyle { get; set; } = "none";

    public string RearClimbAssistKneeDirection { get; set; } = "rear";

    public double BodyLengthM { get; set; } = 0.48;

    public double BodyWidthM { get; set; } = 0.48;

    public double BodyHeightM { get; set; } = 0.18;

    public double BodyClearanceM { get; set; } = 0.10;

    public double BodyRenderWidthScale { get; set; } = 1.0;

    public double StructureBaseLiftM { get; set; }

    public double WheelRadiusM { get; set; } = 0.08;

    public IReadOnlyList<(double X, double Y)> WheelOffsetsM { get; set; } =
        Array.Empty<(double X, double Y)>();

    public IReadOnlyList<double> ArmorOrbitYawsDeg { get; set; } =
        Array.Empty<double>();

    public IReadOnlyList<double> ArmorSelfYawsDeg { get; set; } =
        Array.Empty<double>();

    public double GimbalLengthM { get; set; } = 0.26;

    public double GimbalWidthM { get; set; } = 0.16;

    public double GimbalBodyHeightM { get; set; } = 0.10;

    public double GimbalHeightM { get; set; } = 0.34;

    public double GimbalOffsetXM { get; set; }

    public double GimbalOffsetYM { get; set; }

    public double GimbalMountGapM { get; set; } = 0.10;

    public double GimbalMountLengthM { get; set; } = 0.10;

    public double GimbalMountWidthM { get; set; } = 0.10;

    public double GimbalMountHeightM { get; set; } = 0.04;

    public double BarrelLengthM { get; set; } = 0.12;

    public double BarrelRadiusM { get; set; } = 0.016;

    public double ArmorPlateWidthM { get; set; } = 0.16;

    public double ArmorPlateLengthM { get; set; } = 0.16;

    public double ArmorPlateHeightM { get; set; } = 0.16;

    public double ArmorPlateGapM { get; set; } = 0.02;

    public double ArmorLightLengthM { get; set; } = 0.04;

    public double ArmorLightWidthM { get; set; } = 0.005;

    public double ArmorLightHeightM { get; set; } = 0.08;

    public double BarrelLightLengthM { get; set; } = 0.10;

    public double BarrelLightWidthM { get; set; } = 0.01;

    public double BarrelLightHeightM { get; set; } = 0.03;

    public double FrontClimbAssistTopLengthM { get; set; } = 0.05;

    public double FrontClimbAssistBottomLengthM { get; set; } = 0.03;

    public double FrontClimbAssistPlateWidthM { get; set; } = 0.018;

    public double FrontClimbAssistPlateHeightM { get; set; } = 0.18;

    public double FrontClimbAssistForwardOffsetM { get; set; } = 0.04;

    public double FrontClimbAssistInnerOffsetM { get; set; } = 0.06;

    public double RearClimbAssistUpperLengthM { get; set; } = 0.09;

    public double RearClimbAssistLowerLengthM { get; set; } = 0.08;

    public double RearClimbAssistUpperWidthM { get; set; } = 0.016;

    public double RearClimbAssistUpperHeightM { get; set; } = 0.016;

    public double RearClimbAssistLowerWidthM { get; set; } = 0.016;

    public double RearClimbAssistLowerHeightM { get; set; } = 0.016;

    public double RearClimbAssistMountOffsetXM { get; set; } = 0.03;

    public double RearClimbAssistMountHeightM { get; set; } = 0.22;

    public double RearClimbAssistInnerOffsetM { get; set; } = 0.03;

    public double RearClimbAssistUpperPairGapM { get; set; } = 0.06;

    public double RearClimbAssistHingeRadiusM { get; set; } = 0.016;

    public double RearClimbAssistKneeMinDeg { get; set; } = 42.0;

    public double RearClimbAssistKneeMaxDeg { get; set; } = 132.0;

    public bool IsAlive { get; set; } = true;

    public bool PermanentEliminated { get; set; }

    public string State { get; set; } = "idle";

    public string AiDecision { get; set; } = "idle";

    public string AiDecisionSelected { get; set; } = string.Empty;

    public string TestForcedDecisionId { get; set; } = string.Empty;

    public double MaxHealth { get; set; } = 100.0;

    public double Health { get; set; } = 100.0;

    public double MaxPower { get; set; } = 60.0;

    public double Power { get; set; } = 60.0;

    public double MaxHeat { get; set; } = 60.0;

    public double Heat { get; set; }

    public double RespawnTimerSec { get; set; }

    public double WeakTimerSec { get; set; }

    public double RespawnAmmoLockTimerSec { get; set; }

    public double RespawnInvincibleTimerSec { get; set; }

    public int InstantReviveCount { get; set; }

    public double HeatLockTimerSec { get; set; }

    public double FireCooldownSec { get; set; }

    public int Ammo17Mm { get; set; }

    public int Ammo42Mm { get; set; }

    public string AmmoType { get; set; } = "17mm";

    public int CarriedMinerals { get; set; }

    public double SupplyAccumulatorSec { get; set; }

    public double SupplyBuyCooldownSec { get; set; }

    public double MiningProgressSec { get; set; }

    public double ExchangeProgressSec { get; set; }

    public double DeadZoneTimerSec { get; set; }

    public string TerrainSequenceKey { get; set; } = string.Empty;

    public double TerrainSequenceTimerSec { get; set; }

    public double TerrainRoadLockoutTimerSec { get; set; }

    public double TerrainHighlandDefenseTimerSec { get; set; }

    public double TerrainFlySlopeDefenseTimerSec { get; set; }

    public double TerrainRoadCoolingTimerSec { get; set; }

    public double TerrainSlopeDefenseTimerSec { get; set; }

    public double TerrainSlopeCoolingTimerSec { get; set; }

    public double HeroDeploymentHoldTimerSec { get; set; }

    public double DynamicDamageTakenMult { get; set; } = 1.0;

    public double DynamicDamageDealtMult { get; set; } = 1.0;

    public double DynamicCoolingMult { get; set; } = 1.0;

    public double DynamicPowerRecoveryMult { get; set; } = 1.0;

    public void ResetDynamicEffects()
    {
        DynamicDamageTakenMult = 1.0;
        DynamicDamageDealtMult = 1.0;
        DynamicCoolingMult = 1.0;
        DynamicPowerRecoveryMult = 1.0;
    }

    public bool ConsumeAmmoForShot()
    {
        if (string.Equals(AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            if (Ammo42Mm <= 0)
            {
                return false;
            }

            Ammo42Mm -= 1;
            return true;
        }

        if (string.Equals(AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Ammo17Mm <= 0)
        {
            return false;
        }

        Ammo17Mm -= 1;
        return true;
    }

    public void AddAllowedAmmo(int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        if (string.Equals(AmmoType, "42mm", StringComparison.OrdinalIgnoreCase))
        {
            Ammo42Mm += amount;
            return;
        }

        if (!string.Equals(AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            Ammo17Mm += amount;
        }
    }
}

public sealed class SimulationTeamState
{
    public SimulationTeamState(string team, double initialGold)
    {
        Team = team;
        Gold = initialGold;
    }

    public string Team { get; }

    public double Gold { get; set; }

    public double EnergyActivationTimerSec { get; set; }

    public double EnergyVirtualHits { get; set; }

    public double EnergyBuffTimerSec { get; set; }
}

public sealed class SimulationWorldState
{
    public double GameTimeSec { get; set; }

    public double MetersPerWorldUnit { get; set; } = 0.0178;

    public IList<SimulationEntity> Entities { get; } = new List<SimulationEntity>();

    public IList<SimulationProjectile> Projectiles { get; } = new List<SimulationProjectile>();

    public IDictionary<string, SimulationTeamState> Teams { get; } =
        new Dictionary<string, SimulationTeamState>(StringComparer.OrdinalIgnoreCase);

    public SimulationTeamState GetOrCreateTeamState(string team, double initialGold = 400.0)
    {
        if (!Teams.TryGetValue(team, out SimulationTeamState? state))
        {
            state = new SimulationTeamState(team, initialGold);
            Teams[team] = state;
        }

        return state;
    }
}

public sealed record FacilityInteractionEvent(
    double TimeSec,
    string Team,
    string EntityId,
    string FacilityId,
    string FacilityType,
    string Message);

public sealed record FacilityInteractionDescriptor(
    string FacilityType,
    string Summary,
    IReadOnlyList<string> RecommendedRoles);
