namespace Simulator.Core.Gameplay;

public enum EnergyMechanismArmDisplayKind
{
    Off,
    Pending,
    Hit,
    ActivatedByProgress,
    Completed,
}

public readonly record struct EnergyMechanismArmDisplayState(
    int ArmIndex,
    int RingScore,
    bool Pending,
    bool Completed,
    bool Flashing,
    EnergyMechanismArmDisplayKind Kind);

public static class EnergyMechanismVisualLogic
{
    public const int ArmCount = 5;

    public static EnergyMechanismArmDisplayState ResolveArmState(
        SimulationTeamState teamState,
        int armIndex,
        double gameTimeSec)
    {
        if (armIndex < 0 || armIndex >= ArmCount)
        {
            return new EnergyMechanismArmDisplayState(armIndex, 0, false, false, false, EnergyMechanismArmDisplayKind.Off);
        }

        int ringScore = armIndex < teamState.EnergyHitRingsByArm.Length
            ? Math.Clamp(teamState.EnergyHitRingsByArm[armIndex], 0, 10)
            : 0;
        bool fullyActivated = IsActivated(teamState);
        bool pending = IsActivating(teamState)
            && teamState.EnergyCurrentLitMask != 0
            && (teamState.EnergyCurrentLitMask & (1 << armIndex)) != 0
            && ringScore <= 0;
        bool activatedByProgress = IsArmActivatedByProgress(teamState, armIndex);
        bool completed = fullyActivated || ringScore > 0 || activatedByProgress;
        bool flashing = teamState.EnergyLastHitArmIndex == armIndex
            && teamState.EnergyLastRingScore > 0
            && gameTimeSec <= teamState.EnergyLastHitFlashEndSec;
        EnergyMechanismArmDisplayKind kind = fullyActivated
            ? EnergyMechanismArmDisplayKind.Completed
            : ringScore > 0
                ? EnergyMechanismArmDisplayKind.Hit
                : activatedByProgress
                    ? EnergyMechanismArmDisplayKind.ActivatedByProgress
                    : pending
                        ? EnergyMechanismArmDisplayKind.Pending
                        : EnergyMechanismArmDisplayKind.Off;

        return new EnergyMechanismArmDisplayState(armIndex, ringScore, pending, completed, flashing, kind);
    }

    public static int ResolveHitMask(SimulationTeamState teamState, bool large)
    {
        if (teamState.EnergyLargeMechanismActive != large)
        {
            return 0;
        }

        int mask = 0;
        for (int index = 0; index < Math.Min(ArmCount, teamState.EnergyHitRingsByArm.Length); index++)
        {
            if (teamState.EnergyHitRingsByArm[index] > 0)
            {
                mask |= 1 << index;
            }
        }

        return mask;
    }

    public static int ResolveLitMask(SimulationTeamState teamState, bool large)
        => teamState.EnergyLargeMechanismActive == large && IsActivating(teamState)
            ? teamState.EnergyCurrentLitMask & 0x1F
            : 0;

    public static int CountMaskBits(int mask)
    {
        int count = 0;
        int value = mask & 0x1F;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }

    public static bool IsArmActivatedByProgress(SimulationTeamState teamState, int armIndex)
    {
        int activatedCount = Math.Clamp(teamState.EnergyActivatedGroupCount, 0, ArmCount);
        if (activatedCount <= 0 || armIndex < 0)
        {
            return false;
        }

        int orderLength = Math.Min(activatedCount, teamState.EnergyActivationOrder.Length);
        bool orderLooksInitialized = teamState.EnergyActivationOrder.Any(index => index != 0);
        for (int index = 0; index < orderLength; index++)
        {
            if (Math.Clamp(teamState.EnergyActivationOrder[index], 0, ArmCount - 1) == armIndex)
            {
                return true;
            }
        }

        return !orderLooksInitialized && armIndex < activatedCount;
    }

    private static bool IsActivating(SimulationTeamState teamState)
        => string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase);

    private static bool IsActivated(SimulationTeamState teamState)
        => string.Equals(teamState.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase);
}
