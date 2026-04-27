using System.Numerics;
using System.Threading.Tasks;
using Simulator.Core;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal sealed class TerrainMotionService
{
    private const double GravityMps2 = 9.81;
    private const double TerrainSmoothHeightThresholdM = 0.048;
    private const double TerrainRenderAnchorThresholdM = 0.02;
    private const double BalanceInfantryMaxJumpHeightM = 0.50;
    private const double BalanceInfantryJumpCrouchDurationSec = 0.08;
    private const double LandingCompressionMaxM = 0.040;
    private const double LandingReboundMaxM = 0.018;
    private const double FacilityCollisionInsetM = 0.035;
    private const double WheelAccelerationRampLimitMps2 = 2.0;
    private const double WheelMaxLinearSpeedMps = 6.0;
    private const double MotorInstantPowerLimitW = 80.0;
    private const double BufferSuperCapSwitchEnergyJ = 10.0;
    private const double SuperCapForcedDischargeW = 300.0;
    private const double StandardGyroYawRateAt50WDegPerSec = 230.0;
    private const double StandardGyroYawRateAt120WDegPerSec = 660.0;
    private const double StandardGyroYawRateCapDegPerSec = 720.0;
    private const double OverPowerCutDurationSec = 5.0;
    private const double NavigationFailedRetryCooldownSec = 2.00;
    private const double NavigationReplanIntervalMovingSec = 1.35;
    private const double NavigationReplanIntervalStaticSec = 1.50;
    private const double NavigationDirectProbeIntervalSec = 2.50;
    private const double NavigationLookAheadProbeIntervalSec = 1.50;
    private const double NavigationFallbackDirectReuseSec = 0.95;
    private const double AutoDecisionIntervalSec = 1.00;
    private const double AutoAimIntervalSec = 0.30;
    private const double AutoAimLockedTrackingIntervalSec = 0.065;
    private const double AutoAimLostSearchDelaySec = 3.0;
    private const double AutoAimSearchPitchBaseDeg = 1.6;
    private const double AutoAimSearchPitchAmplitudeDeg = 7.0;
    private const double AutoAimSearchPitchFrequencyHz = 0.32;
    private const double AiRetainedArmorTrackingReuseSec = 0.22;
    private const double LargeProjectileAutoAimReuseSec = 0.10;
    private const double HeroLobStructureAutoAimReuseSec = 0.16;
    private const double TraversalAutoAimReuseSec = 0.12;
    private const bool UseThirdOrderEkfAutoAimPoseChain = true;
    private const double AutoAimResidualMemorySec = 3.0;
    private const double AutoAimResidualPredictionClampSec = 1.20;
    private const double EnemyProbeIntervalSec = 0.75;
    private const double VisibleEnemyProbeIntervalSec = 0.24;
    private const double NavigationPendingPlanTimeoutSec = 1.00;
    private const double NavigationPlanQueueIntervalSec = 1.85;
    private const double NavigationBlockedReplanIntervalSec = 0.45;
    private const double NavigationWaypointReachM = 0.55;
    private const double LostTargetYawCruiseInput = 1.15;
    private const double AiStuckIntentThreshold = 0.18;
    private const double AiStuckSpeedThresholdMps = 0.10;
    private const double AiStuckTriggerSec = 0.68;
    private const double AiUnstuckDurationSec = 0.82;
    private const int NavigationMaxExpandedCells = 220;
    private const int NavigationMaxLookAheadWaypoints = 2;
    private const int NavigationMaxPendingPlanTasks = 2;
    private readonly RuleSet _rules;
    private readonly DecisionDeploymentConfig _decisionDeployment;
    private readonly IReadOnlyList<FacilityRegion> _facilities;
    private readonly bool _enableFieldCompositeInteractionTest;
    private readonly Dictionary<string, NavigationPathState> _navigationStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AutoAimSolveCache> _autoAimSolveCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AutoAimObservationFilterState> _autoAimObservationFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AutoAimThirdOrderEkfPoseFilterState> _autoAimObservationEkf3Filters = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SimulationEntity> _visibleEnemyCandidateBuffer = new(8);
    private List<FacilityCollisionShape>? _facilityCollisionShapes;
    private double _facilityCollisionCellWidthWorld = -1.0;
    private double _facilityCollisionCellHeightWorld = -1.0;
    private double _metersPerWorldUnit = 0.0178;
    private long _lastMotionPerfLogTicks;
    private long _lastSlowControlLogTicks;
    private long _lastTerrainMovementBlockLogTicks;

    private readonly record struct NavigationUnitSnapshot(
        string Id,
        double X,
        double Y,
        double DirectStepHeightM,
        double MaxStepClimbHeightM,
        double AirborneHeightM,
        double CollisionRadiusWorld,
        bool ChassisSupportsJump,
        bool RestrictStepHeightToThirtyCm);

    private readonly record struct NavigationObstacleSnapshot(
        string Id,
        double X,
        double Y,
        double RadiusWorld);

    private sealed record NavigationPlanResult(
        bool Success,
        string NavigationKey,
        int GoalCellX,
        int GoalCellY,
        List<(double X, double Y)> Waypoints);

    private sealed record NavigationGoalResult(
        bool Success,
        string NavigationKey,
        int GoalCellX,
        int GoalCellY,
        double GoalWorldX,
        double GoalWorldY,
        double TargetX,
        double TargetY,
        double DesiredDistanceM);

    private readonly record struct WheelContactSample(double LocalX, double LocalY, double HeightM);

    public TerrainMotionService(
        RuleSet rules,
        DecisionDeploymentConfig? decisionDeployment = null,
        IReadOnlyList<FacilityRegion>? facilities = null,
        bool enableFieldCompositeInteractionTest = true)
    {
        _rules = rules;
        _decisionDeployment = decisionDeployment ?? DecisionDeploymentConfig.CreateDefault();
        _facilities = facilities ?? Array.Empty<FacilityRegion>();
        _enableFieldCompositeInteractionTest = enableFieldCompositeInteractionTest;
    }

    public void Step(SimulationWorldState world, RuntimeGridData? runtimeGrid, double deltaTimeSec, bool aiEnabled = true)
    {
        if (runtimeGrid is null || !runtimeGrid.IsValid)
        {
            StepFallback(world, deltaTimeSec, aiEnabled);
            return;
        }

        double dt = Math.Clamp(deltaTimeSec, 0.01, 0.2);
        _metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        long totalStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long controlTicks = 0;
        long rotationTicks = 0;
        long verticalTicks = 0;
        long translationTicks = 0;
        int movableCount = 0;
        foreach (SimulationEntity entity in world.Entities)
        {
            if (!IsMovableEntity(entity))
            {
                continue;
            }

            movableCount++;
            if (!entity.IsAlive || entity.IsSimulationSuppressed)
            {
                ClearMotion(entity);
                _navigationStates.Remove(entity.Id);
                ClearAutoAimTracking(entity.Id);
                continue;
            }

            ResetFramePowerTelemetry(entity);
            AdvancePowerCutState(entity, dt);
            if (!entity.IsPlayerControlled && !aiEnabled)
            {
                ClearMotion(entity);
                entity.IsFireCommandActive = false;
                entity.AutoAimRequested = false;
                _navigationStates.Remove(entity.Id);
                continue;
            }

            SimulationEntity? enemy = entity.IsPlayerControlled ? null : ResolveCachedNearestEnemy(world, entity);
            double previousAimSpeedMps = entity.LastAimSpeedMps;
            double previousAimHeightM = entity.LastAimHeightM;
            double previousX = entity.X;
            double previousY = entity.Y;
            double previousAngleDeg = entity.AngleDeg;
            long segmentStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (entity.IsPlayerControlled)
            {
                ApplyPlayerControl(world, runtimeGrid, entity, dt);
            }
            else
            {
                ApplyAutoControl(world, runtimeGrid, entity, enemy, dt);
            }
            controlTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segmentStartTicks;

            double chassisTargetYaw = entity.TraversalActive
                ? entity.TraversalDirectionDeg
                : entity.SmallGyroActive
                    ? entity.AngleDeg + ResolveSmallGyroYawRateDegPerSec(entity) * dt
                    : entity.ChassisTargetYawDeg;

            segmentStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyVerticalMotion(entity, dt);
            verticalTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segmentStartTicks;
            segmentStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyTranslationWithTerrain(world, runtimeGrid, entity, dt);
            translationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segmentStartTicks;
            if (!entity.IsPlayerControlled)
            {
                UpdateAiStuckState(world, entity, GetOrCreateNavigationState(entity.Id, world.GameTimeSec), previousX, previousY, dt);
            }

            segmentStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyRotation(entity, chassisTargetYaw, dt);
            rotationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segmentStartTicks;
            ApplyForcedSuperCapDischarge(entity, dt);
            UpdateObservedKinematics(entity, previousX, previousY, previousAngleDeg, dt);
            UpdateAutoAimInstability(world, entity, dt, previousAimSpeedMps, previousAimHeightM);

            entity.JumpRequested = false;
        }

        LogMotionPerfIfDue(
            System.Diagnostics.Stopwatch.GetTimestamp() - totalStartTicks,
            controlTicks,
            rotationTicks,
            verticalTicks,
            translationTicks,
            movableCount);
    }

    private void LogMotionPerfIfDue(
        long totalTicks,
        long controlTicks,
        long rotationTicks,
        long verticalTicks,
        long translationTicks,
        int movableCount)
    {
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastMotionPerfLogTicks > 0
            && (nowTicks - _lastMotionPerfLogTicks) / (double)System.Diagnostics.Stopwatch.Frequency < 2.0)
        {
            return;
        }

        _lastMotionPerfLogTicks = nowTicks;
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} "
            + $"total={TicksToMs(totalTicks):0.00}ms "
            + $"control={TicksToMs(controlTicks):0.00}ms "
            + $"rotation={TicksToMs(rotationTicks):0.00}ms "
            + $"vertical={TicksToMs(verticalTicks):0.00}ms "
            + $"translation={TicksToMs(translationTicks):0.00}ms "
            + $"movable={movableCount}";
        SimulatorRuntimeLog.Append("motion_perf.log", line);
    }

    private static void StepFallback(SimulationWorldState world, double deltaTimeSec, bool aiEnabled)
    {
        double dt = Math.Clamp(deltaTimeSec, 0.01, 0.2);
        foreach (SimulationEntity entity in world.Entities)
        {
            ResetFramePowerTelemetry(entity);
            AdvancePowerCutState(entity, dt);
            double previousAimSpeedMps = entity.LastAimSpeedMps;
            double previousAimHeightM = entity.LastAimHeightM;
            double previousX = entity.X;
            double previousY = entity.Y;
            double previousAngleDeg = entity.AngleDeg;
            if (!entity.IsAlive)
            {
                ClearMotion(entity);
                continue;
            }

            if (!entity.IsPlayerControlled && !aiEnabled)
            {
                ClearMotion(entity);
                entity.IsFireCommandActive = false;
                entity.AutoAimRequested = false;
                continue;
            }

            entity.EffectiveDrivePowerLimitW = ResolveEffectiveDrivePowerLimitW(entity);
            entity.MotionBlockReason = string.Empty;
            entity.X += entity.VelocityXWorldPerSec * dt;
            entity.Y += entity.VelocityYWorldPerSec * dt;
            ApplyForcedSuperCapDischarge(entity, dt);
            UpdateObservedKinematics(entity, previousX, previousY, previousAngleDeg, dt);
            UpdateAutoAimInstability(world, entity, dt, previousAimSpeedMps, previousAimHeightM);
        }
    }

    private static bool IsMovableEntity(SimulationEntity entity)
    {
        return string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearMotion(SimulationEntity entity)
    {
        entity.VelocityXWorldPerSec = 0;
        entity.VelocityYWorldPerSec = 0;
        entity.AngularVelocityDegPerSec = 0;
        entity.VerticalVelocityMps = 0;
        entity.AirborneHeightM = 0;
        entity.JumpCrouchTimerSec = 0;
        entity.JumpCrouchDurationSec = 0;
        entity.LandingCompressionM = 0;
        entity.LandingCompressionVelocityMps = 0;
        entity.ChassisPitchDeg = 0;
        entity.ChassisRollDeg = 0;
        entity.ChassisPowerDrawW = 0;
        ResetFramePowerTelemetry(entity);
        entity.EffectiveDrivePowerLimitW = ResolveEffectiveDrivePowerLimitW(entity);
        entity.ChassisTargetYawDeg = entity.AngleDeg;
        entity.MotionBlockReason = string.Empty;
        entity.ObservedVelocityXWorldPerSec = 0.0;
        entity.ObservedVelocityYWorldPerSec = 0.0;
        entity.ObservedAngularVelocityDegPerSec = 0.0;
        entity.LastObservedX = entity.X;
        entity.LastObservedY = entity.Y;
        entity.LastObservedAngleDeg = entity.AngleDeg;
        entity.HasObservedKinematics = false;
        ClearAutoAimState(entity);
        entity.TraversalActive = false;
        entity.TraversalProgress = 0;
    }

    private static void ResetFramePowerTelemetry(SimulationEntity entity)
    {
        entity.CurrentFrameSuperCapDrawW = 0.0;
        entity.CurrentFrameBufferDrawW = 0.0;
    }

    private static void AdvancePowerCutState(SimulationEntity entity, double dt)
    {
        if (entity.PowerCutTimerSec <= 1e-6)
        {
            if (string.Equals(entity.State, "power_cut", StringComparison.OrdinalIgnoreCase))
            {
                entity.State = entity.HeatLockTimerSec > 1e-6
                    ? "heat_locked"
                    : entity.WeakTimerSec > 1e-6 ? "weak" : "idle";
            }

            entity.PowerCutTimerSec = 0.0;
            return;
        }

        entity.PowerCutTimerSec = Math.Max(0.0, entity.PowerCutTimerSec - dt);
        entity.State = "power_cut";
    }

    private static void TriggerOverPowerCut(SimulationEntity entity)
    {
        entity.PowerCutTimerSec = Math.Max(entity.PowerCutTimerSec, OverPowerCutDurationSec);
        entity.State = "power_cut";
        entity.ChassisPowerDrawW = 0.0;
        entity.CurrentFrameBufferDrawW = 0.0;
        entity.CurrentFrameSuperCapDrawW = 0.0;
        entity.ChassisPowerRatio = 0.0;
        entity.MotionBlockReason = "power_cut";
    }

    private static void UpdateObservedKinematics(
        SimulationEntity entity,
        double previousX,
        double previousY,
        double previousAngleDeg,
        double dt)
    {
        if (dt <= 1e-6)
        {
            return;
        }

        entity.ObservedVelocityXWorldPerSec = (entity.X - previousX) / dt;
        entity.ObservedVelocityYWorldPerSec = (entity.Y - previousY) / dt;
        entity.ObservedAngularVelocityDegPerSec =
            SimulationCombatMath.NormalizeSignedDeg(entity.AngleDeg - previousAngleDeg) / dt;
        entity.LastObservedX = entity.X;
        entity.LastObservedY = entity.Y;
        entity.LastObservedAngleDeg = entity.AngleDeg;
        entity.HasObservedKinematics = true;
    }

    private void ApplyPlayerControl(SimulationWorldState world, RuntimeGridData runtimeGrid, SimulationEntity entity, double dt)
    {
        UpdateHeroDeploymentState(entity);
        _navigationStates.Remove(entity.Id);
        double moveForward = Math.Clamp(entity.MoveInputForward, -1.0, 1.0);
        double moveRight = Math.Clamp(entity.MoveInputRight, -1.0, 1.0);
        double moveNorm = Math.Sqrt(moveForward * moveForward + moveRight * moveRight);
        if (moveNorm > 1.0)
        {
            moveForward /= moveNorm;
            moveRight /= moveNorm;
        }

        bool heroDeploymentActive = entity.HeroDeploymentActive;
        if (heroDeploymentActive)
        {
            moveForward = 0.0;
            moveRight = 0.0;
            moveNorm = 0.0;
            entity.AutoAimRequested = true;
            entity.IsFireCommandActive = true;
        }

        UpdateTurretAim(world, runtimeGrid, entity, dt, playerControlled: !heroDeploymentActive);

        double controlYawDeg = entity.TurretYawDeg;
        entity.ChassisTargetYawDeg = ResolvePlayerChassisTargetYaw(
            entity,
            controlYawDeg,
            moveForward,
            moveRight,
            moveNorm);
        if (moveNorm > 1e-4)
        {
            double targetYawDeg = SimulationCombatMath.NormalizeDeg(
                controlYawDeg + RadiansToDegrees(Math.Atan2(moveRight, moveForward)));
            entity.TraversalDirectionDeg = targetYawDeg;
        }
        else
        {
            entity.TraversalDirectionDeg = SimulationCombatMath.NormalizeDeg(controlYawDeg);
        }

        ApplyDriveControl(world, entity, moveForward, moveRight, dt, controlYawDeg);

        if (entity.JumpRequested
            && entity.ChassisSupportsJump
            && entity.AirborneHeightM <= 1e-3
            && entity.JumpCrouchTimerSec <= 1e-6)
        {
            StartJumpOrCrouch(entity);
        }

        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            entity.SentryStance = RuleSet.NormalizeSentryStance(entity.SentryStance);
        }

        entity.AiDecisionSelected = "manual_control";
        entity.AiDecision = "玩家控制";
    }

    private void ApplyAutoControl(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        SimulationEntity? enemy,
        double dt)
    {
        long controlStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long navigationTicks = 0;
        long autoAimTicks = 0;
        string controlBranch = "idle";
        entity.EnergyActivationRequested = false;
        entity.AutoAimTargetMode = "armor";
        NavigationPathState autoState = GetOrCreateNavigationState(entity.Id, world.GameTimeSec);
        SimulationEntity? visibleEnemy = ResolveCachedVisibleEnemy(world, runtimeGrid, entity, enemy);
        if (visibleEnemy is not null)
        {
            RememberVisibleTargetResidual(world, entity, autoState, visibleEnemy);
        }
        else
        {
            PruneExpiredTargetResidual(world, autoState);
        }

        if (TryApplyAiUnstuckControl(world, runtimeGrid, entity, autoState, dt))
        {
            controlBranch = "unstuck";
            long aimStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            MaybeUpdateAutoTurretAim(world, runtimeGrid, entity, dt, autoState, force: true);
            autoAimTicks += System.Diagnostics.Stopwatch.GetTimestamp() - aimStartTicks;
            LogSlowAutoControlIfNeeded(entity, controlBranch, controlStartTicks, navigationTicks, autoAimTicks);
            return;
        }

        entity.SmallGyroActive = visibleEnemy is not null;
        bool canReuseAutoDrive =
            autoState.HasCachedAutoDrive
            && visibleEnemy is not null
            && !entity.TraversalActive
            && !IsNavigationBlockReason(entity.MotionBlockReason)
            && world.GameTimeSec - autoState.LastAutoDecisionSec < AutoDecisionIntervalSec;
        if (canReuseAutoDrive)
        {
            controlBranch = "reuse_drive";
            entity.TraversalDirectionDeg = autoState.CachedDriveYawDeg;
            entity.ChassisTargetYawDeg = autoState.CachedDriveYawDeg;
            ApplyDriveControl(world, entity, autoState.CachedMoveForward, autoState.CachedMoveRight, dt, autoState.CachedDriveYawDeg);
            long aimStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            MaybeUpdateAutoTurretAim(world, runtimeGrid, entity, dt, autoState);
            autoAimTicks += System.Diagnostics.Stopwatch.GetTimestamp() - aimStartTicks;
            LogSlowAutoControlIfNeeded(entity, controlBranch, controlStartTicks, navigationTicks, autoAimTicks);
            return;
        }

        autoState.LastAutoDecisionSec = world.GameTimeSec;
        long navigationStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (TryApplyRespawnRecoveryNavigation(world, runtimeGrid, entity, dt))
        {
            navigationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - navigationStartTicks;
            controlBranch = "recover";
            long aimStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            MaybeUpdateAutoTurretAim(world, runtimeGrid, entity, dt, autoState, force: true);
            autoAimTicks += System.Diagnostics.Stopwatch.GetTimestamp() - aimStartTicks;
            LogSlowAutoControlIfNeeded(entity, controlBranch, controlStartTicks, navigationTicks, autoAimTicks);
            return;
        }
        navigationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - navigationStartTicks;

        bool hasNonAttackTacticalCommand = string.Equals(entity.TacticalCommand, "defend", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.TacticalCommand, "patrol", StringComparison.OrdinalIgnoreCase);
        if (enemy is null && !hasNonAttackTacticalCommand)
        {
            controlBranch = "lost_target_cruise_no_enemy";
            ApplyLostTargetYawCruise(world, entity, autoState, dt, clearAim: true);
            LogSlowAutoControlIfNeeded(entity, controlBranch, controlStartTicks, navigationTicks, autoAimTicks);
            return;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        navigationStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        SimulationEntity? tacticalTarget = ResolveTacticalAttackTarget(world, entity)
            ?? ResolveStrategicAttackTarget(world, entity)
            ?? enemy;
        navigationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - navigationStartTicks;
        navigationStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (TryApplyTacticalNavigation(world, runtimeGrid, entity, autoState, tacticalTarget, dt, metersPerWorldUnit))
        {
            navigationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - navigationStartTicks;
            controlBranch = "tactical_nav";
            LogSlowAutoControlIfNeeded(entity, controlBranch, controlStartTicks, navigationTicks, autoAimTicks);
            return;
        }
        navigationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - navigationStartTicks;

        if (tacticalTarget is null)
        {
            controlBranch = "lost_target_cruise_no_tactical";
            ApplyLostTargetYawCruise(world, entity, autoState, dt, clearAim: true);
            LogSlowAutoControlIfNeeded(entity, controlBranch, controlStartTicks, navigationTicks, autoAimTicks);
            return;
        }

        double targetX = tacticalTarget.X;
        double targetY = tacticalTarget.Y;
        double dx = targetX - entity.X;
        double dy = targetY - entity.Y;
        double distanceM = Math.Sqrt(dx * dx + dy * dy) * metersPerWorldUnit;
        string mode = _decisionDeployment.ResolveMode(entity.RoleKey);
        entity.AiDecisionSelected = mode;

        double desiredDistanceM;
        double aggressionScale;
        switch (mode)
        {
            case "hold":
                desiredDistanceM = 6.8;
                aggressionScale = 0.45;
                entity.AiDecision = "阵地驻守";
                break;
            case "support":
                desiredDistanceM = 5.8;
                aggressionScale = 0.58;
                entity.AiDecision = "协同支援";
                break;
            case "flank":
                desiredDistanceM = 4.8;
                aggressionScale = 0.95;
                entity.AiDecision = "侧向包抄";
                break;
            default:
                desiredDistanceM = 4.0;
                aggressionScale = 0.88;
                entity.AiDecision = "火力压制";
                break;
        }

        SimulationEntity? navigationTargetEntity = tacticalTarget;
        string navigationTargetKey = tacticalTarget.Id;
        if (TryResolveStrategicMoveTarget(
            world,
            entity,
            tacticalTarget,
            metersPerWorldUnit,
            out double strategicMoveX,
            out double strategicMoveY,
            out double strategicDesiredDistanceM,
            out double strategicAggressionScale,
            out string strategicNavigationKey,
            out string strategicDecisionSelected,
            out string strategicDecisionText))
        {
            targetX = strategicMoveX;
            targetY = strategicMoveY;
            dx = targetX - entity.X;
            dy = targetY - entity.Y;
            distanceM = Math.Sqrt(dx * dx + dy * dy) * metersPerWorldUnit;
            desiredDistanceM = strategicDesiredDistanceM;
            aggressionScale = strategicAggressionScale;
            navigationTargetEntity = null;
            navigationTargetKey = strategicNavigationKey;
            entity.AiDecisionSelected = strategicDecisionSelected;
            entity.AiDecision = strategicDecisionText;
        }

        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            entity.SentryStance = mode switch
            {
                "hold" => "defense",
                "support" => "move",
                _ => "attack",
            };
        }

        bool duelStrafeScenario = IsDuelStrafeScenario(world, entity, tacticalTarget);
        bool navigationTargetLineOfSightBlocked = navigationTargetEntity is not null
            && !HasCombatLineOfSight(world, runtimeGrid, entity, navigationTargetEntity, visibleEnemy);
        double moveForward = 0.0;
        double moveRight = 0.0;
        bool plannedNavigationApplied = false;
        bool needsLineOfSightReposition = navigationTargetLineOfSightBlocked;
        if (distanceM > desiredDistanceM + 0.25 || needsLineOfSightReposition)
        {
            controlBranch = needsLineOfSightReposition ? "los_reposition" : "advance";
            double heading = RadiansToDegrees(Math.Atan2(dy, dx));
            if (string.Equals(mode, "flank", StringComparison.OrdinalIgnoreCase))
            {
                heading += string.Equals(entity.Team, "red", StringComparison.OrdinalIgnoreCase) ? 34.0 : -34.0;
            }

            bool shouldUsePlannedNavigation =
                needsLineOfSightReposition
                || HasActiveNavigationState(entity.Id)
                || IsNavigationBlockReason(entity.MotionBlockReason);
            if (duelStrafeScenario
                && visibleEnemy is not null
                && distanceM <= desiredDistanceM + 1.5)
            {
                shouldUsePlannedNavigation = false;
            }

            if (!shouldUsePlannedNavigation
                && ShouldRouteAroundObstacle(world, runtimeGrid, entity, targetX, targetY))
            {
                shouldUsePlannedNavigation = true;
            }

            navigationStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (shouldUsePlannedNavigation
                && TryApplyPlannedNavigation(
                    world,
                    runtimeGrid,
                    entity,
                    targetX,
                    targetY,
                    desiredDistanceM,
                    aggressionScale,
                    dt,
                    metersPerWorldUnit,
                    needsLineOfSightReposition ? $"auto:los:{mode}:{navigationTargetKey}" : $"auto:{mode}:{navigationTargetKey}",
                    navigationTargetEntity,
                    needsLineOfSightReposition))
            {
                plannedNavigationApplied = true;
                controlBranch = "planned_nav";
            }
            else
            {
                entity.TraversalDirectionDeg = SimulationCombatMath.NormalizeDeg(heading);
                controlBranch = shouldUsePlannedNavigation ? "fallback_direct" : "direct";
                if (shouldUsePlannedNavigation)
                {
                    autoState.LastFallbackDirectSec = world.GameTimeSec;
                    moveForward = 0.0;
                    moveRight = 0.0;
                    entity.TraversalDirectionDeg = entity.AngleDeg;
                    entity.ChassisTargetYawDeg = entity.AngleDeg;
                    if (needsLineOfSightReposition)
                    {
                        entity.AiDecisionSelected = "los_reposition";
                        entity.AiDecision = "\u7ed5\u5899\u5bfb\u8def";
                    }
                }
                else
                {
                    moveForward = aggressionScale;
                    if (duelStrafeScenario)
                    {
                        moveRight = ResolveDuelStrafeInput(entity.Id, world.GameTimeSec) * 0.38;
                    }
                }
            }
            navigationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - navigationStartTicks;
        }
        else
        {
            controlBranch = duelStrafeScenario ? "duel_strafe" : "hold_range";
            _navigationStates.Remove(entity.Id);
            entity.TraversalDirectionDeg = entity.AngleDeg;
            if (duelStrafeScenario)
            {
                moveRight = ResolveDuelStrafeInput(entity.Id, world.GameTimeSec);
                moveForward = distanceM < desiredDistanceM - 0.45 ? -0.18 : 0.16;
            }
            else
            {
                ApplyTargetTrackingHoldMovement(
                    world,
                    entity,
                    tacticalTarget,
                    dx,
                    dy,
                    distanceM,
                    desiredDistanceM,
                    metersPerWorldUnit,
                    ref moveForward,
                    ref moveRight);
            }
        }

        if (plannedNavigationApplied)
        {
            long aimStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            MaybeUpdateAutoTurretAim(world, runtimeGrid, entity, dt, autoState);
            autoAimTicks += System.Diagnostics.Stopwatch.GetTimestamp() - aimStartTicks;
            LogSlowAutoControlIfNeeded(entity, controlBranch, controlStartTicks, navigationTicks, autoAimTicks);
            return;
        }

        entity.ChassisTargetYawDeg = entity.TraversalDirectionDeg;
        CacheAutoDrive(autoState, moveForward, moveRight, entity.TraversalDirectionDeg);
        ApplyDriveControl(world, entity, moveForward, moveRight, dt, entity.TraversalDirectionDeg);
        long finalAimStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        MaybeUpdateAutoTurretAim(world, runtimeGrid, entity, dt, autoState);
        autoAimTicks += System.Diagnostics.Stopwatch.GetTimestamp() - finalAimStartTicks;
        LogSlowAutoControlIfNeeded(entity, controlBranch, controlStartTicks, navigationTicks, autoAimTicks);
    }

    private static void ApplyTargetTrackingHoldMovement(
        SimulationWorldState world,
        SimulationEntity entity,
        SimulationEntity target,
        double dx,
        double dy,
        double distanceM,
        double desiredDistanceM,
        double metersPerWorldUnit,
        ref double moveForward,
        ref double moveRight)
    {
        double distanceWorld = Math.Sqrt(dx * dx + dy * dy);
        if (distanceWorld <= 1e-6 || distanceM <= 1e-3)
        {
            return;
        }

        double targetVelocityXWorld = Math.Abs(target.ObservedVelocityXWorldPerSec) + Math.Abs(target.ObservedVelocityYWorldPerSec) > 1e-4
            ? target.ObservedVelocityXWorldPerSec
            : target.VelocityXWorldPerSec;
        double targetVelocityYWorld = Math.Abs(target.ObservedVelocityXWorldPerSec) + Math.Abs(target.ObservedVelocityYWorldPerSec) > 1e-4
            ? target.ObservedVelocityYWorldPerSec
            : target.VelocityYWorldPerSec;
        double targetVelocityXMps = targetVelocityXWorld * metersPerWorldUnit;
        double targetVelocityYMps = targetVelocityYWorld * metersPerWorldUnit;
        double selfVelocityXMps = entity.VelocityXWorldPerSec * metersPerWorldUnit;
        double selfVelocityYMps = entity.VelocityYWorldPerSec * metersPerWorldUnit;

        double losX = dx / distanceWorld;
        double losY = dy / distanceWorld;
        double tangentX = -losY;
        double tangentY = losX;
        double targetLateralMps = targetVelocityXMps * tangentX + targetVelocityYMps * tangentY;
        double selfLateralMps = selfVelocityXMps * tangentX + selfVelocityYMps * tangentY;
        double selfRadialMps = selfVelocityXMps * losX + selfVelocityYMps * losY;
        double speedLimitMps = Math.Max(
            1.2,
            ClampWheelLinearSpeedMps(ResolveMoveSpeedMps(entity, Math.Max(1.0, entity.EffectiveDrivePowerLimitW))
                * Math.Max(0.10, entity.ChassisSpeedScale)));

        double lateralFollowMps = Math.Clamp(
            (targetLateralMps - selfLateralMps) * 0.92,
            -speedLimitMps * 0.62,
            speedLimitMps * 0.62);
        double radialCorrectionMps = Math.Clamp(
            (distanceM - desiredDistanceM) * 0.82 - selfRadialMps * 0.36,
            -speedLimitMps * 0.32,
            speedLimitMps * 0.32);

        double desiredVelocityXMps = losX * radialCorrectionMps + tangentX * lateralFollowMps;
        double desiredVelocityYMps = losY * radialCorrectionMps + tangentY * lateralFollowMps;
        double yawRad = DegreesToRadians(entity.AngleDeg);
        double forwardX = Math.Cos(yawRad);
        double forwardY = Math.Sin(yawRad);
        double rightX = -Math.Sin(yawRad);
        double rightY = Math.Cos(yawRad);
        moveForward = Math.Clamp((desiredVelocityXMps * forwardX + desiredVelocityYMps * forwardY) / speedLimitMps, -0.46, 0.46);
        moveRight = Math.Clamp((desiredVelocityXMps * rightX + desiredVelocityYMps * rightY) / speedLimitMps, -0.66, 0.66);

        if (Math.Abs(moveForward) < 0.035)
        {
            moveForward = 0.0;
        }

        if (Math.Abs(moveRight) < 0.040)
        {
            moveRight = 0.0;
        }
    }

    private void ApplyLostTargetYawCruise(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState autoState,
        double dt,
        bool clearAim)
    {
        ResetNavigationPath(autoState);
        bool residualGuided = TryResolveTargetResidualYaw(world, entity, autoState, out double residualYawDeg, out _);
        double yawDeg = residualGuided
            ? residualYawDeg
            : SimulationCombatMath.NormalizeDeg(entity.AngleDeg);
        entity.TraversalDirectionDeg = yawDeg;
        entity.ChassisTargetYawDeg = yawDeg;
        entity.SmallGyroActive = residualGuided;
        CacheAutoDrive(autoState, LostTargetYawCruiseInput, 0.0, yawDeg);
        ApplyDriveControl(world, entity, LostTargetYawCruiseInput, 0.0, dt, yawDeg);
        entity.AiDecisionSelected = residualGuided ? "residual_cruise" : "lost_target_cruise";
        entity.AiDecision = residualGuided ? "\u6b8b\u7559\u5f15\u5bfc\u5de1\u822a" : "\u5931\u76ee\u6807\u5de1\u822a";
        if (clearAim)
        {
            ClearAutoAimState(entity);
        }
    }

    private bool TryApplyAiUnstuckControl(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        NavigationPathState autoState,
        double dt)
    {
        _ = runtimeGrid;
        if (world.GameTimeSec > autoState.AiUnstuckUntilSec)
        {
            return false;
        }

        ResetNavigationPath(autoState);
        entity.TraversalActive = false;
        entity.TraversalProgress = 0.0;
        entity.SmallGyroActive = false;

        double escapeYawDeg = SimulationCombatMath.NormalizeDeg(autoState.AiUnstuckYawDeg);
        double remainingSec = Math.Max(0.0, autoState.AiUnstuckUntilSec - world.GameTimeSec);
        double strafePulse = Math.Sin(world.GameTimeSec * 8.0 + StableUnitPhase(entity.Id) * Math.PI * 2.0) * 0.08;
        double moveForward = remainingSec > AiUnstuckDurationSec * 0.32 ? 0.88 : 0.44;
        double moveRight = Math.Clamp(autoState.AiUnstuckStrafeSign * 0.38 + strafePulse, -0.56, 0.56);

        entity.TraversalDirectionDeg = escapeYawDeg;
        entity.ChassisTargetYawDeg = escapeYawDeg;
        entity.AiDecisionSelected = "unstuck";
        entity.AiDecision = "\u8131\u79bb\u5361\u6b7b";
        if (entity.ChassisSupportsJump
            && entity.AirborneHeightM <= 1e-3
            && remainingSec > AiUnstuckDurationSec * 0.48)
        {
            entity.JumpRequested = true;
        }

        CacheAutoDrive(autoState, moveForward, moveRight, escapeYawDeg);
        ApplyDriveControl(world, entity, moveForward, moveRight, dt, escapeYawDeg);
        return true;
    }

    private void UpdateAiStuckState(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState autoState,
        double previousX,
        double previousY,
        double dt)
    {
        if (!entity.IsAlive
            || entity.IsSimulationSuppressed
            || entity.PowerCutTimerSec > 1e-6)
        {
            autoState.AiStuckTimerSec = 0.0;
            autoState.AiUnstuckUntilSec = -999.0;
            return;
        }

        if (world.GameTimeSec <= autoState.AiUnstuckUntilSec)
        {
            return;
        }

        double moveForward = Math.Clamp(entity.MoveInputForward, -1.25, 1.25);
        double moveRight = Math.Clamp(entity.MoveInputRight, -1.25, 1.25);
        double intent = Math.Sqrt(moveForward * moveForward + moveRight * moveRight);
        if (intent <= AiStuckIntentThreshold)
        {
            autoState.AiStuckTimerSec = Math.Max(0.0, autoState.AiStuckTimerSec - dt * 2.0);
            return;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double movedM = Math.Sqrt((entity.X - previousX) * (entity.X - previousX) + (entity.Y - previousY) * (entity.Y - previousY)) * metersPerWorldUnit;
        double speedMps = movedM / Math.Max(dt, 1e-6);
        bool blocked = IsNavigationBlockReason(entity.MotionBlockReason);
        bool tooSlow = speedMps < AiStuckSpeedThresholdMps
            && entity.AirborneHeightM <= 0.03
            && !entity.TraversalActive;
        if (blocked || tooSlow)
        {
            autoState.AiStuckTimerSec += dt * (blocked ? 1.35 : 1.0);
        }
        else
        {
            autoState.AiStuckTimerSec = Math.Max(0.0, autoState.AiStuckTimerSec - dt * 2.35);
        }

        if (autoState.AiStuckTimerSec < AiStuckTriggerSec
            || world.GameTimeSec - autoState.AiLastStuckTriggerSec < 0.35)
        {
            return;
        }

        TriggerAiUnstuck(world, entity, autoState);
    }

    private static void TriggerAiUnstuck(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState autoState)
    {
        double baseYawDeg = autoState.HasCachedAutoDrive
            ? autoState.CachedDriveYawDeg
            : entity.TraversalDirectionDeg;
        if (!double.IsFinite(baseYawDeg))
        {
            baseYawDeg = entity.AngleDeg;
        }

        double escapeYawDeg = SimulationCombatMath.NormalizeDeg(
            baseYawDeg + (StableUnitPhase(entity.Id) >= 0.5 ? 150.0 : -150.0));
        if (TryResolveBlockingEntityId(entity.MotionBlockReason, out string? blockingEntityId))
        {
            SimulationEntity? blockingEntity = world.Entities.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, blockingEntityId, StringComparison.OrdinalIgnoreCase));
            if (blockingEntity is not null)
            {
                double awayX = entity.X - blockingEntity.X;
                double awayY = entity.Y - blockingEntity.Y;
                if (awayX * awayX + awayY * awayY > 1e-6)
                {
                    escapeYawDeg = SimulationCombatMath.NormalizeDeg(Math.Atan2(awayY, awayX) * 180.0 / Math.PI);
                }
            }
        }

        autoState.AiStuckTimerSec = 0.0;
        autoState.AiLastStuckTriggerSec = world.GameTimeSec;
        autoState.AiUnstuckUntilSec = world.GameTimeSec + AiUnstuckDurationSec;
        autoState.AiUnstuckYawDeg = escapeYawDeg;
        autoState.AiUnstuckStrafeSign = autoState.AiUnstuckStrafeSign >= 0.0 ? -1.0 : 1.0;
        autoState.HasCachedAutoDrive = false;
        ResetNavigationPath(autoState);
    }

    private static bool TryResolveBlockingEntityId(string? motionBlockReason, out string? entityId)
    {
        entityId = null;
        if (string.IsNullOrWhiteSpace(motionBlockReason))
        {
            return false;
        }

        int separatorIndex = motionBlockReason.IndexOf(':');
        if (separatorIndex < 0 || separatorIndex >= motionBlockReason.Length - 1)
        {
            return false;
        }

        string prefix = motionBlockReason[..separatorIndex];
        if (!prefix.Contains("entity", StringComparison.OrdinalIgnoreCase)
            && !prefix.Contains("traversal_collision", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        entityId = motionBlockReason[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(entityId);
    }

    private void MaybeUpdateAutoTurretAim(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double dt,
        NavigationPathState autoState,
        bool force = false)
    {
        double aimIntervalSec = entity.AutoAimLocked || autoState.HasTargetResidual
            ? AutoAimLockedTrackingIntervalSec
            : AutoAimIntervalSec;
        if (!force && world.GameTimeSec - autoState.LastAutoAimSec < aimIntervalSec)
        {
            if (entity.AutoAimLocked)
            {
                RememberAutoAimResidual(world, entity, autoState);
                autoState.AutoAimLostSinceSec = -999.0;
            }

            if (!entity.AutoAimLocked)
            {
                AdvanceAutoTurretLostPattern(world, entity, autoState, dt);
            }

            return;
        }

        UpdateTurretAim(world, runtimeGrid, entity, dt, playerControlled: false);
        if (entity.AutoAimLocked)
        {
            RememberAutoAimResidual(world, entity, autoState);
            autoState.AutoAimLostSinceSec = -999.0;
        }
        else
        {
            PruneExpiredTargetResidual(world, autoState);
        }

        if (!entity.AutoAimLocked)
        {
            AdvanceAutoTurretLostPattern(world, entity, autoState, dt);
        }

        autoState.LastAutoAimSec = world.GameTimeSec;
    }

    private void AdvanceAutoTurretLostPattern(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState autoState,
        double dt)
    {
        if (autoState.AutoAimLostSinceSec < -100.0)
        {
            autoState.AutoAimLostSinceSec = world.GameTimeSec;
        }

        double lostAgeSec = Math.Max(0.0, world.GameTimeSec - autoState.AutoAimLostSinceSec);
        if (lostAgeSec < AutoAimLostSearchDelaySec
            && TryHoldAutoTurretOnResidual(world, entity, autoState, dt))
        {
            return;
        }

        if (lostAgeSec < AutoAimLostSearchDelaySec)
        {
            entity.GimbalPitchDeg = Lerp(entity.GimbalPitchDeg, AutoAimSearchPitchBaseDeg, Math.Clamp(dt * 4.0, 0.0, 1.0));
            return;
        }

        AdvanceAutoTurretSearchPattern(world, entity, autoState, dt);
    }

    private bool TryHoldAutoTurretOnResidual(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState autoState,
        double dt)
    {
        if (!TryResolveTargetResidualPoint(world, entity, autoState, targetId: null, out double xWorld, out double yWorld, out double ageSec))
        {
            return false;
        }

        if (ageSec > AutoAimLostSearchDelaySec + 0.10)
        {
            return false;
        }

        double dx = xWorld - entity.X;
        double dy = yWorld - entity.Y;
        double distanceWorld = Math.Sqrt(dx * dx + dy * dy);
        if (distanceWorld <= 1e-5)
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double targetYawDeg = SimulationCombatMath.NormalizeDeg(RadiansToDegrees(Math.Atan2(dy, dx)));
        double shooterHeightM = entity.GroundHeightM + entity.AirborneHeightM + Math.Max(0.28, entity.BodyHeightM * 0.72);
        double distanceM = Math.Max(0.10, distanceWorld * metersPerWorldUnit);
        double targetPitchDeg = RadiansToDegrees(Math.Atan2(autoState.TargetResidualHeightM - shooterHeightM, distanceM));
        double smooth = Math.Clamp(dt * 8.5, 0.0, 1.0);
        entity.TurretYawDeg = SmoothYawDeg(entity.TurretYawDeg, targetYawDeg, smooth);
        entity.GimbalPitchDeg = Lerp(entity.GimbalPitchDeg, Math.Clamp(targetPitchDeg, -18.0, 28.0), smooth);
        return true;
    }

    private static void AdvanceAutoTurretSearchPattern(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState autoState,
        double dt)
    {
        double scanYawRateDegPerSec = string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)
            ? 84.0
            : 60.0;
        entity.TurretYawDeg = SimulationCombatMath.NormalizeDeg(entity.TurretYawDeg + scanYawRateDegPerSec * Math.Clamp(dt, 0.005, 0.05));
        autoState.AutoAimSearchPhaseSec += Math.Clamp(dt, 0.005, 0.05);
        double phase = autoState.AutoAimSearchPhaseSec + StableUnitPhase(entity.Id) * 0.37 + world.GameTimeSec * 0.15;
        double targetPitchDeg = AutoAimSearchPitchBaseDeg
            + Math.Sin(phase * Math.PI * 2.0 * AutoAimSearchPitchFrequencyHz) * AutoAimSearchPitchAmplitudeDeg;
        entity.GimbalPitchDeg = Lerp(entity.GimbalPitchDeg, targetPitchDeg, Math.Clamp(dt * 7.5, 0.0, 1.0));
    }

    private void RememberVisibleTargetResidual(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState state,
        SimulationEntity target)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double velocityXWorldPerSec = Math.Abs(target.ObservedVelocityXWorldPerSec) + Math.Abs(target.ObservedVelocityYWorldPerSec) > 1e-4
            ? target.ObservedVelocityXWorldPerSec
            : target.VelocityXWorldPerSec;
        double velocityYWorldPerSec = Math.Abs(target.ObservedVelocityXWorldPerSec) + Math.Abs(target.ObservedVelocityYWorldPerSec) > 1e-4
            ? target.ObservedVelocityYWorldPerSec
            : target.VelocityYWorldPerSec;
        double targetHeightM =
            target.GroundHeightM
            + target.AirborneHeightM
            + Math.Max(0.35, target.BodyHeightM * 0.62);

        RememberTargetResidual(
            entity,
            state,
            target.Id,
            stateSec: world.GameTimeSec,
            lastObservedSec: world.GameTimeSec,
            xWorld: target.X,
            yWorld: target.Y,
            heightM: targetHeightM,
            velocityXMps: velocityXWorldPerSec * metersPerWorldUnit,
            velocityYMps: velocityYWorldPerSec * metersPerWorldUnit);
    }

    private bool RememberAutoAimResidual(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState state)
    {
        if (!_autoAimSolveCache.TryGetValue(entity.Id, out AutoAimSolveCache? cache)
            || !SimulationCombatMath.IsArmorAutoAimTargetKind(cache.TargetKind))
        {
            PruneExpiredTargetResidual(world, state);
            return false;
        }

        if (world.GameTimeSec - cache.LastObservedGameTimeSec > AutoAimResidualMemorySec)
        {
            PruneExpiredTargetResidual(world, state);
            return false;
        }

        SimulationEntity? target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && string.Equals(candidate.Id, cache.TargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            ClearTargetResidual(state);
            return false;
        }

        double dtSec = Math.Clamp(world.GameTimeSec - cache.GameTimeSec, 0.0, AutoAimResidualPredictionClampSec);
        AutoAimObservedState observationState = dtSec > 1e-6
            ? AdvanceRetainedObservationState(world, cache, dtSec)
            : cache.ObservationState;
        RememberTargetResidual(
            entity,
            state,
            cache.TargetId,
            stateSec: world.GameTimeSec,
            lastObservedSec: cache.LastObservedGameTimeSec,
            xWorld: observationState.AimPointXWorld,
            yWorld: observationState.AimPointYWorld,
            heightM: observationState.AimPointHeightM,
            velocityXMps: observationState.VelocityXMps,
            velocityYMps: observationState.VelocityYMps);
        return true;
    }

    private void RememberTargetResidual(
        SimulationEntity entity,
        NavigationPathState state,
        string targetId,
        double stateSec,
        double lastObservedSec,
        double xWorld,
        double yWorld,
        double heightM,
        double velocityXMps,
        double velocityYMps)
    {
        if (!double.IsFinite(xWorld)
            || !double.IsFinite(yWorld)
            || !double.IsFinite(heightM)
            || !double.IsFinite(velocityXMps)
            || !double.IsFinite(velocityYMps))
        {
            return;
        }

        state.HasTargetResidual = true;
        state.TargetResidualTargetId = targetId;
        state.TargetResidualStateSec = stateSec;
        state.TargetResidualLastObservedSec = lastObservedSec;
        state.TargetResidualXWorld = xWorld;
        state.TargetResidualYWorld = yWorld;
        state.TargetResidualHeightM = heightM;
        state.TargetResidualVelocityXMps = velocityXMps;
        state.TargetResidualVelocityYMps = velocityYMps;
        _navigationStates[entity.Id] = state;
    }

    private bool TryResolveTargetResidualYaw(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState state,
        out double yawDeg,
        out double ageSec)
    {
        yawDeg = SimulationCombatMath.NormalizeDeg(entity.AngleDeg);
        if (!TryResolveTargetResidualPoint(world, entity, state, targetId: null, out double xWorld, out double yWorld, out ageSec))
        {
            return false;
        }

        double dx = xWorld - entity.X;
        double dy = yWorld - entity.Y;
        if (dx * dx + dy * dy <= 1e-4)
        {
            return false;
        }

        yawDeg = SimulationCombatMath.NormalizeDeg(RadiansToDegrees(Math.Atan2(dy, dx)));
        return true;
    }

    private bool TryResolveTargetResidualPoint(
        SimulationWorldState world,
        SimulationEntity entity,
        NavigationPathState state,
        string? targetId,
        out double xWorld,
        out double yWorld,
        out double ageSec)
    {
        xWorld = entity.X;
        yWorld = entity.Y;
        ageSec = 0.0;
        if (!state.HasTargetResidual || string.IsNullOrWhiteSpace(state.TargetResidualTargetId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(targetId)
            && !string.Equals(state.TargetResidualTargetId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        SimulationEntity? target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && string.Equals(candidate.Id, state.TargetResidualTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            ClearTargetResidual(state);
            return false;
        }

        ageSec = world.GameTimeSec - state.TargetResidualLastObservedSec;
        if (ageSec < -0.05 || ageSec > AutoAimResidualMemorySec)
        {
            ClearTargetResidual(state);
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double predictionSec = Math.Clamp(world.GameTimeSec - state.TargetResidualStateSec, 0.0, AutoAimResidualPredictionClampSec);
        xWorld = state.TargetResidualXWorld + state.TargetResidualVelocityXMps * predictionSec / metersPerWorldUnit;
        yWorld = state.TargetResidualYWorld + state.TargetResidualVelocityYMps * predictionSec / metersPerWorldUnit;
        if (!double.IsFinite(xWorld) || !double.IsFinite(yWorld))
        {
            ClearTargetResidual(state);
            return false;
        }

        xWorld = Math.Clamp(xWorld, 0.0, Math.Max(0.0, world.WorldWidth));
        yWorld = Math.Clamp(yWorld, 0.0, Math.Max(0.0, world.WorldHeight));
        return true;
    }

    private static void PruneExpiredTargetResidual(SimulationWorldState world, NavigationPathState state)
    {
        if (state.HasTargetResidual
            && world.GameTimeSec - state.TargetResidualLastObservedSec > AutoAimResidualMemorySec)
        {
            ClearTargetResidual(state);
        }
    }

    private static void ClearTargetResidual(NavigationPathState state)
    {
        state.HasTargetResidual = false;
        state.TargetResidualTargetId = null;
        state.TargetResidualStateSec = -999.0;
        state.TargetResidualLastObservedSec = -999.0;
        state.TargetResidualXWorld = 0.0;
        state.TargetResidualYWorld = 0.0;
        state.TargetResidualHeightM = 0.0;
        state.TargetResidualVelocityXMps = 0.0;
        state.TargetResidualVelocityYMps = 0.0;
    }

    private bool TryApplyTacticalNavigation(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        NavigationPathState autoState,
        SimulationEntity? tacticalTarget,
        double dt,
        double metersPerWorldUnit)
    {
        string command = (entity.TacticalCommand ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        double targetX = entity.TacticalTargetX;
        double targetY = entity.TacticalTargetY;
        double desiredDistanceM = 0.35;
        double drive = 0.82;
        bool requireGoalLineOfSight = false;
        switch (command)
        {
            case "attack":
                if (tacticalTarget is null)
                {
                    return false;
                }

                targetX = tacticalTarget.X;
                targetY = tacticalTarget.Y;
                desiredDistanceM = 3.6;
                entity.AiDecisionSelected = "tactical_attack";
                entity.AiDecision = $"战术进攻 {tacticalTarget.Id}";
                bool hasCombatLineOfSight = HasCombatLineOfSight(world, runtimeGrid, entity, tacticalTarget, null);
                if (!hasCombatLineOfSight
                    && TryResolveTargetResidualPoint(
                        world,
                        entity,
                        autoState,
                        tacticalTarget.Id,
                        out double residualTargetX,
                        out double residualTargetY,
                        out _))
                {
                    targetX = residualTargetX;
                    targetY = residualTargetY;
                    entity.AiDecisionSelected = "residual_attack";
                    entity.AiDecision = "\u6b8b\u7559\u5f15\u5bfc";
                }

                requireGoalLineOfSight = !hasCombatLineOfSight;
                break;
            case "defend":
                desiredDistanceM = 0.45;
                drive = 0.72;
                entity.AiDecisionSelected = "tactical_defend";
                entity.AiDecision = "战术回防";
                break;
            case "patrol":
                double radius = Math.Max(4.0, entity.TacticalPatrolRadiusWorld);
                double phase = world.GameTimeSec * 0.28 + StableUnitPhase(entity.Id);
                targetX += Math.Cos(phase) * radius;
                targetY += Math.Sin(phase) * radius;
                desiredDistanceM = 0.65;
                drive = 0.62;
                entity.AiDecisionSelected = "tactical_patrol";
                entity.AiDecision = "战术巡逻";
                break;
            default:
                return false;
        }

        double dx = targetX - entity.X;
        double dy = targetY - entity.Y;
        double distanceM = Math.Sqrt(dx * dx + dy * dy) * metersPerWorldUnit;
        if (distanceM > desiredDistanceM || requireGoalLineOfSight)
        {
            if (!TryApplyPlannedNavigation(
                world,
                runtimeGrid,
                entity,
                targetX,
                targetY,
                desiredDistanceM,
                drive,
                dt,
                metersPerWorldUnit,
                requireGoalLineOfSight
                    ? $"tactical:los:{command}:{tacticalTarget?.Id ?? "point"}"
                    : $"tactical:{command}:{tacticalTarget?.Id ?? "point"}",
                tacticalTarget,
                requireGoalLineOfSight))
            {
                if (requireGoalLineOfSight)
                {
                    entity.TraversalDirectionDeg = entity.AngleDeg;
                    entity.ChassisTargetYawDeg = entity.AngleDeg;
                    CacheAutoDrive(GetOrCreateNavigationState(entity.Id, world.GameTimeSec), 0.0, 0.0, entity.AngleDeg);
                    ApplyDriveControl(world, entity, 0.0, 0.0, dt, entity.AngleDeg);
                    entity.AiDecisionSelected = "los_reposition";
                    entity.AiDecision = "\u7ed5\u5899\u5bfb\u8def";
                }
                else
                {
                    entity.TraversalDirectionDeg = SimulationCombatMath.NormalizeDeg(RadiansToDegrees(Math.Atan2(dy, dx)));
                    entity.ChassisTargetYawDeg = entity.TraversalDirectionDeg;
                    CacheAutoDrive(GetOrCreateNavigationState(entity.Id, world.GameTimeSec), drive, 0.0, entity.TraversalDirectionDeg);
                    ApplyDriveControl(world, entity, drive, 0.0, dt, entity.TraversalDirectionDeg);
                }
            }
        }
        else
        {
            _navigationStates.Remove(entity.Id);
            entity.ChassisTargetYawDeg = entity.AngleDeg;
            CacheAutoDrive(GetOrCreateNavigationState(entity.Id, world.GameTimeSec), 0.0, 0.0, entity.AngleDeg);
            ApplyDriveControl(world, entity, 0.0, 0.0, dt, entity.AngleDeg);
        }

        MaybeUpdateAutoTurretAim(world, runtimeGrid, entity, dt, autoState);
        return true;
    }

    private bool TryApplyPlannedNavigation(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double targetX,
        double targetY,
        double desiredDistanceM,
        double drive,
        double dt,
        double metersPerWorldUnit,
        string navigationKey,
        SimulationEntity? targetEntity,
        bool requireGoalLineOfSight = false)
    {
        if (_navigationStates.TryGetValue(entity.Id, out NavigationPathState? existingState)
            && existingState.Waypoints.Count == 0
            && string.Equals(existingState.NavigationKey, navigationKey, StringComparison.Ordinal)
            && world.GameTimeSec - existingState.PlannedAtSec < NavigationPlanQueueIntervalSec)
        {
            return false;
        }

        if (existingState is not null
            && existingState.PendingPlanTask is { IsCompleted: false }
            && string.Equals(existingState.PendingNavigationKey, navigationKey, StringComparison.Ordinal)
            && world.GameTimeSec - existingState.LastPlanQueuedSec < NavigationPlanQueueIntervalSec)
        {
            return false;
        }

        if (existingState is not null
            && existingState.PendingGoalTask is { IsCompleted: false }
            && string.Equals(existingState.PendingGoalNavigationKey, navigationKey, StringComparison.Ordinal)
            && world.GameTimeSec - existingState.LastGoalResolveQueuedSec < NavigationPlanQueueIntervalSec * 0.45)
        {
            return false;
        }

        if (existingState is not null
            && string.Equals(existingState.LastFailedNavigationKey, navigationKey, StringComparison.Ordinal)
            && world.GameTimeSec - existingState.LastFailedPlanSec < NavigationFailedRetryCooldownSec)
        {
            return false;
        }

        double quickReuseInterval = targetEntity is null ? NavigationReplanIntervalStaticSec : NavigationReplanIntervalMovingSec;
        bool existingPathBlocked = IsNavigationBlockReason(entity.MotionBlockReason);
        if (existingState is not null
            && existingState.Waypoints.Count > 0
            && string.Equals(existingState.NavigationKey, navigationKey, StringComparison.Ordinal)
            && !existingPathBlocked
            && world.GameTimeSec - existingState.PlannedAtSec < quickReuseInterval)
        {
            return TryDriveNavigationState(
                world,
                runtimeGrid,
                entity,
                existingState,
                drive,
                dt,
                metersPerWorldUnit,
                targetEntity);
        }

        NavigationPathState state = GetOrCreateNavigationState(entity.Id, world.GameTimeSec);
        double resolvedTargetX = targetX;
        double resolvedTargetY = targetY;
        double resolvedDesiredDistanceM = desiredDistanceM;
        bool reusedResolvedGoal = TryReuseResolvedNavigationGoal(
            world,
            runtimeGrid,
            state,
            navigationKey,
            targetX,
            targetY,
            desiredDistanceM,
            targetEntity,
            out int goalCellX,
            out int goalCellY,
            out double goalWorldX,
            out double goalWorldY);
        if (!reusedResolvedGoal
            && !TryConsumeCompletedNavigationGoal(
                state,
                navigationKey,
                out bool goalTaskCompleted,
                out goalCellX,
                out goalCellY,
                out goalWorldX,
                out goalWorldY,
                out resolvedTargetX,
                out resolvedTargetY,
                out resolvedDesiredDistanceM))
        {
            if (goalTaskCompleted)
            {
                RememberFailedNavigationAttempt(state, navigationKey, -1, -1, world.GameTimeSec);
                return false;
            }

            QueueNavigationGoalResolve(
                world,
                runtimeGrid,
                entity,
                state,
                navigationKey,
                targetX,
                targetY,
                desiredDistanceM,
                metersPerWorldUnit,
                requireGoalLineOfSight,
                ResolveNavigationLineOfSightTargetHeightM(runtimeGrid, targetEntity, targetX, targetY));
            if (state.Waypoints.Count > 0 && state.NextWaypointIndex < state.Waypoints.Count)
            {
                return TryDriveNavigationState(
                    world,
                    runtimeGrid,
                    entity,
                    state,
                    drive,
                    dt,
                    metersPerWorldUnit,
                    targetEntity);
            }

            return false;
        }

        if (!reusedResolvedGoal)
        {
            CacheResolvedNavigationGoal(
                state,
                navigationKey,
                goalCellX,
                goalCellY,
                goalWorldX,
                goalWorldY,
                resolvedTargetX,
                resolvedTargetY,
                resolvedDesiredDistanceM,
                world.GameTimeSec);
        }

        double finalDistanceM = Math.Sqrt(
            (goalWorldX - entity.X) * (goalWorldX - entity.X)
            + (goalWorldY - entity.Y) * (goalWorldY - entity.Y)) * metersPerWorldUnit;
        if (finalDistanceM <= Math.Max(0.16, desiredDistanceM * 0.40))
        {
            _navigationStates.Remove(entity.Id);
            entity.TraversalDirectionDeg = entity.AngleDeg;
            entity.ChassisTargetYawDeg = entity.AngleDeg;
            ApplyDriveControl(world, entity, 0.0, 0.0, dt, entity.AngleDeg);
            return true;
        }

        double replanInterval = targetEntity is null ? NavigationReplanIntervalStaticSec : NavigationReplanIntervalMovingSec;
        string motionBlockReason = entity.MotionBlockReason ?? string.Empty;
        double elapsedSincePlan = world.GameTimeSec - state.PlannedAtSec;
        bool sameNavigationKey = string.Equals(state.NavigationKey, navigationKey, StringComparison.Ordinal);
        bool samePlan = sameNavigationKey && state.GoalCellX == goalCellX && state.GoalCellY == goalCellY;
        int goalCellDelta = Math.Abs(state.GoalCellX - goalCellX) + Math.Abs(state.GoalCellY - goalCellY);
        bool emptyPlanExpired = state.Waypoints.Count == 0
            && (string.IsNullOrWhiteSpace(state.NavigationKey)
                || !sameNavigationKey
                || samePlan && elapsedSincePlan >= replanInterval
                || goalCellDelta >= 5 && elapsedSincePlan >= replanInterval * 0.70);
        bool goalMovedEnough = !samePlan && goalCellDelta >= 5 && elapsedSincePlan >= replanInterval * 0.70;
        bool blockedReplan =
            !string.IsNullOrWhiteSpace(motionBlockReason)
            && IsNavigationBlockReason(motionBlockReason)
            && elapsedSincePlan >= NavigationBlockedReplanIntervalSec;
        bool needsReplan =
            emptyPlanExpired
            || goalMovedEnough
            || elapsedSincePlan >= replanInterval
            || blockedReplan
            || !sameNavigationKey;

        if (needsReplan)
        {
            if (blockedReplan)
            {
                state.HasCachedAutoDrive = false;
            }

            if (TryConsumeCompletedNavigationPlan(state, navigationKey, goalCellX, goalCellY, out List<(double X, double Y)> waypoints))
            {
                ApplyNavigationWaypoints(state, navigationKey, goalCellX, goalCellY, waypoints, world.GameTimeSec);
            }
            else
            {
                if (state.PendingPlanTask is { IsCompleted: true })
                {
                    state.PendingPlanTask = null;
                    RememberFailedNavigationAttempt(state, navigationKey, goalCellX, goalCellY, world.GameTimeSec);
                    return false;
                }

                QueueNavigationPlan(world, runtimeGrid, entity, state, navigationKey, goalCellX, goalCellY);
                if (state.Waypoints.Count == 0
                    || state.NextWaypointIndex >= state.Waypoints.Count
                    || world.GameTimeSec - state.PlannedAtSec > NavigationPendingPlanTimeoutSec)
                {
                    if (blockedReplan)
                    {
                        entity.TraversalDirectionDeg = entity.AngleDeg;
                        entity.ChassisTargetYawDeg = entity.AngleDeg;
                        ApplyDriveControl(world, entity, 0.0, 0.0, dt, entity.AngleDeg);
                        return true;
                    }

                    return false;
                }
            }
        }

        return TryDriveNavigationState(
            world,
            runtimeGrid,
            entity,
            state,
            drive,
            dt,
            metersPerWorldUnit,
            targetEntity);
    }

    private static void ResetNavigationPath(NavigationPathState state)
    {
        state.Waypoints.Clear();
        state.NextWaypointIndex = 0;
        state.NavigationKey = string.Empty;
        state.GoalCellX = -1;
        state.GoalCellY = -1;
        state.HasResolvedGoalCell = false;
        state.PendingNavigationKey = string.Empty;
        state.PendingGoalNavigationKey = string.Empty;
    }

    private static void RememberFailedNavigationAttempt(
        NavigationPathState state,
        string navigationKey,
        int goalCellX,
        int goalCellY,
        double gameTimeSec)
    {
        state.PendingPlanTask = null;
        state.PendingGoalTask = null;
        state.PendingNavigationKey = string.Empty;
        state.PendingGoalNavigationKey = string.Empty;
        state.PendingGoalCellX = -1;
        state.PendingGoalCellY = -1;
        state.Waypoints.Clear();
        state.NextWaypointIndex = 0;
        state.NavigationKey = navigationKey;
        state.GoalCellX = goalCellX;
        state.GoalCellY = goalCellY;
        state.PlannedAtSec = gameTimeSec;
        state.LastFailedNavigationKey = navigationKey;
        state.LastFailedPlanSec = gameTimeSec;
        state.HasResolvedGoalCell = false;
    }

    private static void ApplyNavigationWaypoints(
        NavigationPathState state,
        string navigationKey,
        int goalCellX,
        int goalCellY,
        List<(double X, double Y)> waypoints,
        double gameTimeSec)
    {
        state.Waypoints.Clear();
        foreach ((double X, double Y) waypoint in waypoints)
        {
            state.Waypoints.Add(waypoint);
        }

        state.NextWaypointIndex = 0;
        state.NavigationKey = navigationKey;
        state.GoalCellX = goalCellX;
        state.GoalCellY = goalCellY;
        state.PlannedAtSec = gameTimeSec;
        state.LastFailedNavigationKey = string.Empty;
        state.LastFailedPlanSec = -999.0;
    }

    private static void CacheResolvedNavigationGoal(
        NavigationPathState state,
        string navigationKey,
        int goalCellX,
        int goalCellY,
        double goalWorldX,
        double goalWorldY,
        double targetX,
        double targetY,
        double desiredDistanceM,
        double gameTimeSec)
    {
        state.HasResolvedGoalCell = true;
        state.ResolvedGoalNavigationKey = navigationKey;
        state.ResolvedGoalCellX = goalCellX;
        state.ResolvedGoalCellY = goalCellY;
        state.ResolvedGoalWorldX = goalWorldX;
        state.ResolvedGoalWorldY = goalWorldY;
        state.ResolvedTargetX = targetX;
        state.ResolvedTargetY = targetY;
        state.ResolvedDesiredDistanceM = desiredDistanceM;
        state.LastGoalResolveSec = gameTimeSec;
    }

    private static bool TryReuseResolvedNavigationGoal(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        NavigationPathState state,
        string navigationKey,
        double targetX,
        double targetY,
        double desiredDistanceM,
        SimulationEntity? targetEntity,
        out int goalCellX,
        out int goalCellY,
        out double goalWorldX,
        out double goalWorldY)
    {
        goalCellX = -1;
        goalCellY = -1;
        goalWorldX = 0.0;
        goalWorldY = 0.0;
        if (!state.HasResolvedGoalCell
            || !string.Equals(state.ResolvedGoalNavigationKey, navigationKey, StringComparison.Ordinal))
        {
            return false;
        }

        double maxReuseSec = targetEntity is null ? 3.0 : 1.8;
        if (world.GameTimeSec - state.LastGoalResolveSec > maxReuseSec)
        {
            return false;
        }

        double cellSizeWorld = Math.Max(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld);
        double targetShiftWorld = Math.Sqrt(
            (targetX - state.ResolvedTargetX) * (targetX - state.ResolvedTargetX)
            + (targetY - state.ResolvedTargetY) * (targetY - state.ResolvedTargetY));
        double targetToleranceWorld = targetEntity is null ? cellSizeWorld * 3.5 : cellSizeWorld * 5.5;
        if (targetShiftWorld > targetToleranceWorld
            || Math.Abs(desiredDistanceM - state.ResolvedDesiredDistanceM) > 0.50)
        {
            return false;
        }

        goalCellX = state.ResolvedGoalCellX;
        goalCellY = state.ResolvedGoalCellY;
        goalWorldX = state.ResolvedGoalWorldX;
        goalWorldY = state.ResolvedGoalWorldY;
        return true;
    }

    private static bool TryConsumeCompletedNavigationPlan(
        NavigationPathState state,
        string navigationKey,
        int goalCellX,
        int goalCellY,
        out List<(double X, double Y)> waypoints)
    {
        waypoints = new List<(double X, double Y)>();
        Task<NavigationPlanResult>? pending = state.PendingPlanTask;
        if (pending is null || !pending.IsCompleted)
        {
            return false;
        }

        state.PendingPlanTask = null;
        if (!pending.IsCompletedSuccessfully)
        {
            return false;
        }

        NavigationPlanResult result = pending.Result;
        if (!result.Success
            || !string.Equals(result.NavigationKey, navigationKey, StringComparison.Ordinal)
            || result.GoalCellX != goalCellX
            || result.GoalCellY != goalCellY
            || result.Waypoints.Count == 0)
        {
            return false;
        }

        waypoints = result.Waypoints;
        return true;
    }

    private static bool TryConsumeCompletedNavigationGoal(
        NavigationPathState state,
        string navigationKey,
        out bool completed,
        out int goalCellX,
        out int goalCellY,
        out double goalWorldX,
        out double goalWorldY,
        out double targetX,
        out double targetY,
        out double desiredDistanceM)
    {
        completed = false;
        goalCellX = -1;
        goalCellY = -1;
        goalWorldX = 0.0;
        goalWorldY = 0.0;
        targetX = 0.0;
        targetY = 0.0;
        desiredDistanceM = 0.0;
        Task<NavigationGoalResult>? pending = state.PendingGoalTask;
        if (pending is null || !pending.IsCompleted)
        {
            return false;
        }

        completed = true;
        state.PendingGoalTask = null;
        if (!pending.IsCompletedSuccessfully)
        {
            return false;
        }

        NavigationGoalResult result = pending.Result;
        if (!result.Success || !string.Equals(result.NavigationKey, navigationKey, StringComparison.Ordinal))
        {
            return false;
        }

        goalCellX = result.GoalCellX;
        goalCellY = result.GoalCellY;
        goalWorldX = result.GoalWorldX;
        goalWorldY = result.GoalWorldY;
        targetX = result.TargetX;
        targetY = result.TargetY;
        desiredDistanceM = result.DesiredDistanceM;
        return true;
    }

    private void QueueNavigationGoalResolve(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        NavigationPathState state,
        string navigationKey,
        double targetX,
        double targetY,
        double desiredDistanceM,
        double metersPerWorldUnit,
        bool requireGoalLineOfSight,
        double targetVisionHeightM)
    {
        if (state.PendingGoalTask is { IsCompleted: false })
        {
            return;
        }

        if (world.GameTimeSec - state.LastGoalResolveQueuedSec < NavigationPlanQueueIntervalSec * 0.45)
        {
            return;
        }

        if (CountPendingNavigationPlans() >= NavigationMaxPendingPlanTasks)
        {
            return;
        }

        NavigationUnitSnapshot unit = CaptureNavigationUnitSnapshot(entity);
        List<NavigationObstacleSnapshot> obstacles = CaptureNavigationObstacleSnapshot(world, entity);
        state.PendingGoalNavigationKey = navigationKey;
        state.LastGoalResolveQueuedSec = world.GameTimeSec;
        state.PendingGoalTask = Task.Run(() =>
            BuildNavigationGoalFromSnapshot(
                runtimeGrid,
                unit,
                obstacles,
                navigationKey,
                targetX,
                targetY,
                desiredDistanceM,
                metersPerWorldUnit,
                requireGoalLineOfSight,
                targetVisionHeightM));
    }

    private void QueueNavigationPlan(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        NavigationPathState state,
        string navigationKey,
        int goalCellX,
        int goalCellY)
    {
        if (state.PendingPlanTask is { IsCompleted: false })
        {
            return;
        }

        if (world.GameTimeSec - state.LastPlanQueuedSec < NavigationPlanQueueIntervalSec)
        {
            return;
        }

        if (CountPendingNavigationPlans() >= NavigationMaxPendingPlanTasks)
        {
            return;
        }

        NavigationUnitSnapshot unit = CaptureNavigationUnitSnapshot(entity);
        List<NavigationObstacleSnapshot> obstacles = CaptureNavigationObstacleSnapshot(world, entity);
        state.PendingNavigationKey = navigationKey;
        state.PendingGoalCellX = goalCellX;
        state.PendingGoalCellY = goalCellY;
        state.LastPlanQueuedSec = world.GameTimeSec;
        state.PendingPlanTask = Task.Run(() =>
            BuildNavigationPathFromSnapshot(runtimeGrid, unit, obstacles, navigationKey, goalCellX, goalCellY));
    }

    private int CountPendingNavigationPlans()
    {
        int count = 0;
        foreach (NavigationPathState state in _navigationStates.Values)
        {
            if (state.PendingPlanTask is { IsCompleted: false })
            {
                count++;
            }

            if (state.PendingGoalTask is { IsCompleted: false })
            {
                count++;
            }
        }

        return count;
    }

    private static NavigationUnitSnapshot CaptureNavigationUnitSnapshot(SimulationEntity entity)
    {
        double radius = entity.CollisionRadiusWorld > 1e-6
            ? entity.CollisionRadiusWorld
            : Math.Max(0.10, Math.Max(entity.BodyLengthM, entity.BodyWidthM) * 0.5);
        return new NavigationUnitSnapshot(
            entity.Id,
            entity.X,
            entity.Y,
            entity.DirectStepHeightM,
            entity.MaxStepClimbHeightM,
            entity.AirborneHeightM,
            radius,
            entity.ChassisSupportsJump,
            UsesThirtyCentimeterTraversalCap(entity));
    }

    private static List<NavigationObstacleSnapshot> CaptureNavigationObstacleSnapshot(SimulationWorldState world, SimulationEntity self)
    {
        var obstacles = new List<NavigationObstacleSnapshot>(16);
        foreach (SimulationEntity other in world.Entities)
        {
            if (ReferenceEquals(self, other)
                || !other.IsAlive
                || !IsNavigationObstacleEntity(other))
            {
                continue;
            }

            double radius = other.CollisionRadiusWorld > 1e-6
                ? other.CollisionRadiusWorld
                : Math.Max(0.18, Math.Max(other.BodyLengthM, other.BodyWidthM) * 0.5);
            obstacles.Add(new NavigationObstacleSnapshot(other.Id, other.X, other.Y, radius));
        }

        return obstacles;
    }

    private static bool IsNavigationObstacleEntity(SimulationEntity entity)
        => IsCollidableEntity(entity) && !entity.IsSimulationSuppressed;

    private bool TryDriveNavigationState(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        NavigationPathState state,
        double drive,
        double dt,
        double metersPerWorldUnit,
        SimulationEntity? targetEntity)
    {
        AdvanceNavigationWaypoints(world, runtimeGrid, entity, state, metersPerWorldUnit, targetEntity);
        if (state.Waypoints.Count == 0)
        {
            return false;
        }

        if (state.NextWaypointIndex >= state.Waypoints.Count)
        {
            _navigationStates.Remove(entity.Id);
            return false;
        }

        (double waypointX, double waypointY) = state.Waypoints[state.NextWaypointIndex];
        double dx = waypointX - entity.X;
        double dy = waypointY - entity.Y;
        double distanceToWaypointM = Math.Sqrt(dx * dx + dy * dy) * metersPerWorldUnit;
        if (distanceToWaypointM <= 1e-4)
        {
            return false;
        }

        double heading = SimulationCombatMath.NormalizeDeg(RadiansToDegrees(Math.Atan2(dy, dx)));
        double driveScale = Math.Clamp(distanceToWaypointM / 1.4, 0.30, 1.0);
        entity.TraversalDirectionDeg = heading;
        entity.ChassisTargetYawDeg = heading;
        CacheAutoDrive(state, drive * driveScale, 0.0, heading);
        ApplyDriveControl(world, entity, drive * driveScale, 0.0, dt, heading);
        entity.AiDecision = $"{entity.AiDecision} route {Math.Max(1, state.Waypoints.Count - state.NextWaypointIndex)}";
        return true;
    }

    private static void CacheAutoDrive(NavigationPathState state, double moveForward, double moveRight, double driveYawDeg)
    {
        state.HasCachedAutoDrive = true;
        state.CachedMoveForward = Math.Clamp(moveForward, -1.25, 1.25);
        state.CachedMoveRight = Math.Clamp(moveRight, -1.25, 1.25);
        state.CachedDriveYawDeg = SimulationCombatMath.NormalizeDeg(driveYawDeg);
    }

    private NavigationPathState GetOrCreateNavigationState(string entityId, double? gameTimeSec = null)
    {
        if (!_navigationStates.TryGetValue(entityId, out NavigationPathState? state))
        {
            state = new NavigationPathState();
            _navigationStates[entityId] = state;
        }

        if (gameTimeSec.HasValue && !state.PhaseSeeded)
        {
            SeedNavigationStatePhase(state, entityId, gameTimeSec.Value);
        }

        return state;
    }

    private static void SeedNavigationStatePhase(NavigationPathState state, string entityId, double gameTimeSec)
    {
        double phase = StableUnitPhase(entityId);
        state.LastAutoDecisionSec = gameTimeSec - phase % AutoDecisionIntervalSec;
        state.LastAutoAimSec = gameTimeSec - (phase * 1.37) % AutoAimIntervalSec;
        state.AutoAimSearchPhaseSec = phase % 8.0;
        state.LastEnemyProbeSec = gameTimeSec - (phase * 1.73) % EnemyProbeIntervalSec;
        state.LastDirectProbeSec = gameTimeSec - (phase * 1.91) % NavigationDirectProbeIntervalSec;
        state.LastLookAheadProbeSec = gameTimeSec - (phase * 2.07) % NavigationLookAheadProbeIntervalSec;
        state.LastPlanQueuedSec = gameTimeSec - (phase * 2.21) % NavigationPlanQueueIntervalSec;
        state.PhaseSeeded = true;
    }

    private bool HasActiveNavigationState(string entityId)
    {
        return _navigationStates.TryGetValue(entityId, out NavigationPathState? state)
            && state.Waypoints.Count > 0
            && state.NextWaypointIndex < state.Waypoints.Count;
    }

    private bool ShouldRouteAroundObstacle(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double targetX,
        double targetY)
    {
        NavigationPathState state = GetOrCreateNavigationState(entity.Id, world.GameTimeSec);
        double targetDeltaWorld = Math.Sqrt(
            (targetX - state.LastDirectTargetX) * (targetX - state.LastDirectTargetX)
            + (targetY - state.LastDirectTargetY) * (targetY - state.LastDirectTargetY));
        double sameTargetThresholdWorld = Math.Max(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld) * 4.0;
        bool sameTarget = state.HasLastDirectTarget && targetDeltaWorld <= sameTargetThresholdWorld;
        if (sameTarget
            && state.LastDirectCorridorBlocked
            && world.GameTimeSec - state.LastFailedPlanSec < NavigationFailedRetryCooldownSec)
        {
            return true;
        }

        if (world.GameTimeSec - state.LastDirectProbeSec < NavigationDirectProbeIntervalSec)
        {
            return state.LastDirectCorridorBlocked;
        }

        state.LastDirectProbeSec = world.GameTimeSec;
        state.HasLastDirectTarget = true;
        state.LastDirectTargetX = targetX;
        state.LastDirectTargetY = targetY;
        state.LastDirectCorridorBlocked = !HasDirectNavigationCorridor(
            world,
            runtimeGrid,
            entity,
            entity.X,
            entity.Y,
            targetX,
            targetY);
        return state.LastDirectCorridorBlocked;
    }

    private static bool HasCombatLineOfSight(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity self,
        SimulationEntity target,
        SimulationEntity? alreadyVisibleEnemy)
    {
        if (alreadyVisibleEnemy is not null
            && string.Equals(alreadyVisibleEnemy.Id, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasVisibleEnemyPlate(world, runtimeGrid, self, target);
    }

    private static bool IsNavigationBlockReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.StartsWith("terrain_", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("facility_contact:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("entity_contact:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("entity_collision:", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("step_too_high", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("step_contact", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("step_alignment", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("traversal_collision", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("traversal_collision:", StringComparison.OrdinalIgnoreCase);
    }

    private void AdvanceNavigationWaypoints(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        NavigationPathState state,
        double metersPerWorldUnit,
        SimulationEntity? targetEntity)
    {
        while (state.NextWaypointIndex < state.Waypoints.Count)
        {
            (double waypointX, double waypointY) = state.Waypoints[state.NextWaypointIndex];
            double distanceM = Math.Sqrt(
                (waypointX - entity.X) * (waypointX - entity.X)
                + (waypointY - entity.Y) * (waypointY - entity.Y)) * metersPerWorldUnit;
            if (distanceM > NavigationWaypointReachM)
            {
                break;
            }

            state.NextWaypointIndex++;
        }

        if (world.GameTimeSec - state.LastLookAheadProbeSec < NavigationLookAheadProbeIntervalSec)
        {
            return;
        }

        state.LastLookAheadProbeSec = world.GameTimeSec;
        int farthestVisible = state.NextWaypointIndex;
        int lastLookAhead = Math.Min(
            state.Waypoints.Count - 1,
            state.NextWaypointIndex + NavigationMaxLookAheadWaypoints);
        for (int index = state.NextWaypointIndex + 1; index <= lastLookAhead; index++)
        {
            (double sampleX, double sampleY) = state.Waypoints[index];
            if (!HasDirectNavigationCorridor(world, runtimeGrid, entity, entity.X, entity.Y, sampleX, sampleY, targetEntity))
            {
                break;
            }

            farthestVisible = index;
        }

        state.NextWaypointIndex = Math.Min(farthestVisible, state.Waypoints.Count);
    }

    private bool TryResolveNavigationGoalCell(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double targetX,
        double targetY,
        double desiredDistanceM,
        double metersPerWorldUnit,
        SimulationEntity? targetEntity,
        out int goalCellX,
        out int goalCellY,
        out double goalWorldX,
        out double goalWorldY)
    {
        goalCellX = WorldToCellX(runtimeGrid, targetX);
        goalCellY = WorldToCellY(runtimeGrid, targetY);
        goalWorldX = CellCenterWorldX(runtimeGrid, goalCellX);
        goalWorldY = CellCenterWorldY(runtimeGrid, goalCellY);

        double desiredDistanceWorld = desiredDistanceM / Math.Max(metersPerWorldUnit, 1e-6);
        double cellSizeWorld = Math.Max(1e-6, Math.Min(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld));
        int desiredRadiusCells = Math.Max(0, (int)Math.Round(desiredDistanceWorld / cellSizeWorld));
        int maxSearchRadius = Math.Clamp(desiredRadiusCells + 5, 2, 16);
        double bestScore = double.MaxValue;
        bool found = false;
        double targetHeight = SampleTerrainHeight(runtimeGrid, targetX, targetY);
        int targetCellX = WorldToCellX(runtimeGrid, targetX);
        int targetCellY = WorldToCellY(runtimeGrid, targetY);
        sbyte[] standCache = new sbyte[runtimeGrid.WidthCells * runtimeGrid.HeightCells];
        HashSet<int> visitedCandidates = new(Math.Min(runtimeGrid.WidthCells * runtimeGrid.HeightCells, 384));
        int bestGoalCellX = goalCellX;
        int bestGoalCellY = goalCellY;
        double bestGoalWorldX = goalWorldX;
        double bestGoalWorldY = goalWorldY;

        bool TryConsiderCandidate(int cellX, int cellY)
        {
            if (cellX < 0 || cellX >= runtimeGrid.WidthCells || cellY < 0 || cellY >= runtimeGrid.HeightCells)
            {
                return false;
            }

            int candidateIndex = runtimeGrid.IndexOf(cellX, cellY);
            if (!visitedCandidates.Add(candidateIndex))
            {
                return false;
            }

            double candidateX = CellCenterWorldX(runtimeGrid, cellX);
            double candidateY = CellCenterWorldY(runtimeGrid, cellY);
            if (!CanStandAtNavigationCellCached(world, runtimeGrid, entity, cellX, cellY, targetEntity, standCache))
            {
                return false;
            }

            double distanceToTargetWorld = Math.Sqrt(
                (candidateX - targetX) * (candidateX - targetX)
                + (candidateY - targetY) * (candidateY - targetY));
            double distanceError = Math.Abs(distanceToTargetWorld - desiredDistanceWorld);
            double fromEntityWorld = Math.Sqrt(
                (candidateX - entity.X) * (candidateX - entity.X)
                + (candidateY - entity.Y) * (candidateY - entity.Y));
            double heightPenalty = Math.Abs(SampleTerrainHeight(runtimeGrid, candidateX, candidateY) - targetHeight) * 2.5;
            double score = distanceError * 3.8 + fromEntityWorld + heightPenalty;
            if (targetEntity is not null && string.Equals(targetEntity.EntityType, "outpost", StringComparison.OrdinalIgnoreCase))
            {
                score += distanceToTargetWorld * 0.12;
            }

            if (score >= bestScore)
            {
                return false;
            }

            bestScore = score;
            bestGoalCellX = cellX;
            bestGoalCellY = cellY;
            bestGoalWorldX = candidateX;
            bestGoalWorldY = candidateY;
            found = true;
            return true;
        }

        TryConsiderCandidate(targetCellX, targetCellY);
        int[] radiusOffsets =
        {
            desiredRadiusCells,
            desiredRadiusCells - 2,
            desiredRadiusCells + 2,
            desiredRadiusCells - 4,
            desiredRadiusCells + 4,
            2,
            maxSearchRadius,
        };
        int angleSamples = desiredRadiusCells >= 10 ? 32 : 24;
        foreach (int rawRadius in radiusOffsets)
        {
            int radius = Math.Clamp(rawRadius, 1, maxSearchRadius);
            for (int sample = 0; sample < angleSamples; sample++)
            {
                double angle = sample * Math.Tau / angleSamples;
                int cellX = targetCellX + (int)Math.Round(Math.Cos(angle) * radius);
                int cellY = targetCellY + (int)Math.Round(Math.Sin(angle) * radius);
                TryConsiderCandidate(cellX, cellY);
            }
        }

        if (!found)
        {
            for (int radius = 1; radius <= maxSearchRadius && !found; radius += 2)
            {
                int minX = targetCellX - radius;
                int maxX = targetCellX + radius;
                int minY = targetCellY - radius;
                int maxY = targetCellY + radius;
                for (int step = 0; step <= radius * 2; step += 2)
                {
                    TryConsiderCandidate(minX + step, minY);
                    TryConsiderCandidate(minX + step, maxY);
                    TryConsiderCandidate(minX, minY + step);
                    TryConsiderCandidate(maxX, minY + step);
                }
            }
        }

        if (found)
        {
            goalCellX = bestGoalCellX;
            goalCellY = bestGoalCellY;
            goalWorldX = bestGoalWorldX;
            goalWorldY = bestGoalWorldY;
        }

        return found;
    }

    private static double ResolveNavigationLineOfSightTargetHeightM(
        RuntimeGridData runtimeGrid,
        SimulationEntity? targetEntity,
        double targetX,
        double targetY)
    {
        double terrainHeightM = SampleTerrainHeight(runtimeGrid, targetX, targetY);
        if (targetEntity is null)
        {
            return terrainHeightM + 0.70;
        }

        return Math.Max(
            terrainHeightM + 0.55,
            targetEntity.GroundHeightM
            + targetEntity.AirborneHeightM
            + Math.Max(0.35, targetEntity.BodyHeightM * 0.68 + targetEntity.GimbalBodyHeightM * 0.30));
    }

    private static bool HasApproximateNavigationLineOfSight(
        RuntimeGridData runtimeGrid,
        double metersPerWorldUnit,
        double startX,
        double startY,
        double endX,
        double endY,
        double targetVisionHeightM)
    {
        double distanceWorld = Math.Sqrt((endX - startX) * (endX - startX) + (endY - startY) * (endY - startY));
        double distanceM = distanceWorld * Math.Max(metersPerWorldUnit, 1e-6);
        if (distanceM <= 0.10)
        {
            return true;
        }

        double startHeightM = SampleTerrainHeight(runtimeGrid, startX, startY) + 0.68;
        double endHeightM = Math.Max(SampleTerrainHeight(runtimeGrid, endX, endY) + 0.45, targetVisionHeightM);
        int samples = Math.Clamp((int)Math.Ceiling(distanceM / 0.24), 5, 56);
        for (int index = 1; index < samples; index++)
        {
            double t = index / (double)samples;
            if (t <= 0.05 || t >= 0.96)
            {
                continue;
            }

            double sampleX = Lerp(startX, endX, t);
            double sampleY = Lerp(startY, endY, t);
            double rayHeightM = Lerp(startHeightM, endHeightM, t);
            if (runtimeGrid.TrySampleCollisionSurface(sampleX, sampleY, out TerrainSurfaceSample surfaceSample)
                && surfaceSample.HeightM > 0.025f
                && rayHeightM <= surfaceSample.HeightM + 0.035)
            {
                return false;
            }

            int cellX = WorldToCellX(runtimeGrid, sampleX);
            int cellY = WorldToCellY(runtimeGrid, sampleY);
            int sampleIndex = runtimeGrid.IndexOf(cellX, cellY);
            float occlusionHeightM = runtimeGrid.SampleOcclusionHeight(sampleX, sampleY);
            if ((occlusionHeightM > 0.025f || runtimeGrid.VisionBlockMap[sampleIndex])
                && rayHeightM <= occlusionHeightM + 0.035f)
            {
                return false;
            }
        }

        return true;
    }

    private static NavigationGoalResult BuildNavigationGoalFromSnapshot(
        RuntimeGridData runtimeGrid,
        NavigationUnitSnapshot unit,
        IReadOnlyList<NavigationObstacleSnapshot> obstacles,
        string navigationKey,
        double targetX,
        double targetY,
        double desiredDistanceM,
        double metersPerWorldUnit,
        bool requireGoalLineOfSight,
        double targetVisionHeightM)
    {
        int goalCellX = WorldToCellX(runtimeGrid, targetX);
        int goalCellY = WorldToCellY(runtimeGrid, targetY);
        double goalWorldX = CellCenterWorldX(runtimeGrid, goalCellX);
        double goalWorldY = CellCenterWorldY(runtimeGrid, goalCellY);

        double desiredDistanceWorld = desiredDistanceM / Math.Max(metersPerWorldUnit, 1e-6);
        double cellSizeWorld = Math.Max(1e-6, Math.Min(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld));
        int desiredRadiusCells = Math.Max(0, (int)Math.Round(desiredDistanceWorld / cellSizeWorld));
        int maxSearchRadius = Math.Clamp(desiredRadiusCells + 5, 2, 16);
        double bestScore = double.MaxValue;
        bool found = false;
        double targetHeight = SampleTerrainHeight(runtimeGrid, targetX, targetY);
        int targetCellX = WorldToCellX(runtimeGrid, targetX);
        int targetCellY = WorldToCellY(runtimeGrid, targetY);
        sbyte[] standCache = new sbyte[runtimeGrid.WidthCells * runtimeGrid.HeightCells];
        HashSet<int> visitedCandidates = new(Math.Min(runtimeGrid.WidthCells * runtimeGrid.HeightCells, 384));

        bool TryConsiderCandidate(int cellX, int cellY)
        {
            if (cellX < 0 || cellX >= runtimeGrid.WidthCells || cellY < 0 || cellY >= runtimeGrid.HeightCells)
            {
                return false;
            }

            int candidateIndex = runtimeGrid.IndexOf(cellX, cellY);
            if (!visitedCandidates.Add(candidateIndex)
                || !CanStandAtNavigationCellCachedSnapshot(runtimeGrid, unit, obstacles, cellX, cellY, standCache))
            {
                return false;
            }

            double candidateX = CellCenterWorldX(runtimeGrid, cellX);
            double candidateY = CellCenterWorldY(runtimeGrid, cellY);
            double distanceToTargetWorld = Math.Sqrt(
                (candidateX - targetX) * (candidateX - targetX)
                + (candidateY - targetY) * (candidateY - targetY));
            double distanceError = Math.Abs(distanceToTargetWorld - desiredDistanceWorld);
            double fromEntityWorld = Math.Sqrt(
                (candidateX - unit.X) * (candidateX - unit.X)
                + (candidateY - unit.Y) * (candidateY - unit.Y));
            double heightPenalty = Math.Abs(SampleTerrainHeight(runtimeGrid, candidateX, candidateY) - targetHeight) * 2.5;
            double score = distanceError * 3.8 + fromEntityWorld + heightPenalty;
            if (requireGoalLineOfSight
                && !HasApproximateNavigationLineOfSight(runtimeGrid, metersPerWorldUnit, candidateX, candidateY, targetX, targetY, targetVisionHeightM))
            {
                score += cellSizeWorld * 10000.0;
            }

            if (score >= bestScore)
            {
                return false;
            }

            bestScore = score;
            goalCellX = cellX;
            goalCellY = cellY;
            goalWorldX = candidateX;
            goalWorldY = candidateY;
            found = true;
            return true;
        }

        TryConsiderCandidate(targetCellX, targetCellY);
        int[] radiusOffsets =
        {
            desiredRadiusCells,
            desiredRadiusCells - 2,
            desiredRadiusCells + 2,
            desiredRadiusCells - 4,
            desiredRadiusCells + 4,
            2,
            maxSearchRadius,
        };
        int angleSamples = desiredRadiusCells >= 10 ? 32 : 24;
        foreach (int rawRadius in radiusOffsets)
        {
            int radius = Math.Clamp(rawRadius, 1, maxSearchRadius);
            for (int sample = 0; sample < angleSamples; sample++)
            {
                double angle = sample * Math.Tau / angleSamples;
                TryConsiderCandidate(
                    targetCellX + (int)Math.Round(Math.Cos(angle) * radius),
                    targetCellY + (int)Math.Round(Math.Sin(angle) * radius));
            }
        }

        if (!found)
        {
            for (int radius = 1; radius <= maxSearchRadius && !found; radius += 2)
            {
                int minX = targetCellX - radius;
                int maxX = targetCellX + radius;
                int minY = targetCellY - radius;
                int maxY = targetCellY + radius;
                for (int step = 0; step <= radius * 2; step += 2)
                {
                    TryConsiderCandidate(minX + step, minY);
                    TryConsiderCandidate(minX + step, maxY);
                    TryConsiderCandidate(minX, minY + step);
                    TryConsiderCandidate(maxX, minY + step);
                }
            }
        }

        return new NavigationGoalResult(
            found,
            navigationKey,
            goalCellX,
            goalCellY,
            goalWorldX,
            goalWorldY,
            targetX,
            targetY,
            desiredDistanceM);
    }

    private static NavigationPlanResult BuildNavigationPathFromSnapshot(
        RuntimeGridData runtimeGrid,
        NavigationUnitSnapshot unit,
        IReadOnlyList<NavigationObstacleSnapshot> obstacles,
        string navigationKey,
        int goalCellX,
        int goalCellY)
    {
        if (!TryBuildNavigationPathFromSnapshot(runtimeGrid, unit, obstacles, goalCellX, goalCellY, out List<(double X, double Y)> waypoints))
        {
            return new NavigationPlanResult(false, navigationKey, goalCellX, goalCellY, new List<(double X, double Y)>());
        }

        return new NavigationPlanResult(true, navigationKey, goalCellX, goalCellY, waypoints);
    }

    private static bool TryBuildNavigationPathFromSnapshot(
        RuntimeGridData runtimeGrid,
        NavigationUnitSnapshot unit,
        IReadOnlyList<NavigationObstacleSnapshot> obstacles,
        int goalCellX,
        int goalCellY,
        out List<(double X, double Y)> waypoints)
    {
        waypoints = new List<(double X, double Y)>();
        int startCellX = WorldToCellX(runtimeGrid, unit.X);
        int startCellY = WorldToCellY(runtimeGrid, unit.Y);
        int width = runtimeGrid.WidthCells;
        int height = runtimeGrid.HeightCells;
        int cellCount = width * height;
        int startIndex = runtimeGrid.IndexOf(startCellX, startCellY);
        int goalIndex = runtimeGrid.IndexOf(goalCellX, goalCellY);
        if (startIndex == goalIndex)
        {
            waypoints.Add((CellCenterWorldX(runtimeGrid, goalCellX), CellCenterWorldY(runtimeGrid, goalCellY)));
            return true;
        }

        var frontier = new PriorityQueue<int, double>();
        double[] gScore = new double[cellCount];
        int[] cameFrom = new int[cellCount];
        bool[] closed = new bool[cellCount];
        sbyte[] standCache = new sbyte[cellCount];
        Array.Fill(gScore, double.PositiveInfinity);
        Array.Fill(cameFrom, -1);

        gScore[startIndex] = 0.0;
        frontier.Enqueue(startIndex, EstimatePathHeuristic(runtimeGrid, startCellX, startCellY, goalCellX, goalCellY));
        int expanded = 0;
        int[] neighborDx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] neighborDy = { -1, -1, -1, 0, 0, 1, 1, 1 };
        double maxStep = ResolveEffectiveTraversalStepHeightM(unit);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(unit);

        while (frontier.Count > 0 && expanded < NavigationMaxExpandedCells)
        {
            int currentIndex = frontier.Dequeue();
            if (closed[currentIndex])
            {
                continue;
            }

            closed[currentIndex] = true;
            expanded++;
            if (currentIndex == goalIndex)
            {
                break;
            }

            int currentCellX = currentIndex % width;
            int currentCellY = currentIndex / width;
            double currentWorldX = CellCenterWorldX(runtimeGrid, currentCellX);
            double currentWorldY = CellCenterWorldY(runtimeGrid, currentCellY);
            double currentHeight = SampleTerrainHeight(runtimeGrid, currentWorldX, currentWorldY);

            for (int neighborIndex = 0; neighborIndex < neighborDx.Length; neighborIndex++)
            {
                int nextCellX = currentCellX + neighborDx[neighborIndex];
                int nextCellY = currentCellY + neighborDy[neighborIndex];
                if (nextCellX < 0 || nextCellX >= width || nextCellY < 0 || nextCellY >= height)
                {
                    continue;
                }

                bool diagonal = neighborDx[neighborIndex] != 0 && neighborDy[neighborIndex] != 0;
                if (diagonal
                    && (!IsNavigationNeighborNavigable(runtimeGrid, currentCellX + neighborDx[neighborIndex], currentCellY, width, height)
                        || !IsNavigationNeighborNavigable(runtimeGrid, currentCellX, currentCellY + neighborDy[neighborIndex], width, height)))
                {
                    continue;
                }

                int nextIndex = runtimeGrid.IndexOf(nextCellX, nextCellY);
                if (closed[nextIndex]
                    || !CanStandAtNavigationCellCachedSnapshot(runtimeGrid, unit, obstacles, nextCellX, nextCellY, standCache))
                {
                    continue;
                }

                double nextWorldX = CellCenterWorldX(runtimeGrid, nextCellX);
                double nextWorldY = CellCenterWorldY(runtimeGrid, nextCellY);
                double nextHeight = SampleTerrainHeight(runtimeGrid, nextWorldX, nextWorldY);
                double rise = nextHeight - currentHeight;
                if (rise > maxStep + jumpClearance + 1e-6)
                {
                    continue;
                }

                double transitionCost = diagonal ? 1.414 : 1.0;
                transitionCost += Math.Max(0.0, rise) * 3.4;
                transitionCost += Math.Abs(nextHeight - currentHeight) * 0.9;
                if (runtimeGrid.VisionBlockMap[nextIndex])
                {
                    transitionCost += 0.08;
                }

                double tentativeScore = gScore[currentIndex] + transitionCost;
                if (tentativeScore >= gScore[nextIndex])
                {
                    continue;
                }

                cameFrom[nextIndex] = currentIndex;
                gScore[nextIndex] = tentativeScore;
                frontier.Enqueue(nextIndex, tentativeScore + EstimatePathHeuristic(runtimeGrid, nextCellX, nextCellY, goalCellX, goalCellY));
            }
        }

        if (cameFrom[goalIndex] < 0)
        {
            return false;
        }

        List<int> cellPath = new();
        int walkIndex = goalIndex;
        while (walkIndex >= 0)
        {
            cellPath.Add(walkIndex);
            if (walkIndex == startIndex)
            {
                break;
            }

            walkIndex = cameFrom[walkIndex];
        }

        if (cellPath.Count == 0 || cellPath[^1] != startIndex)
        {
            return false;
        }

        cellPath.Reverse();
        List<(double X, double Y)> worldPath = new(cellPath.Count);
        foreach (int cellIndex in cellPath)
        {
            int cellX = cellIndex % width;
            int cellY = cellIndex / width;
            worldPath.Add((CellCenterWorldX(runtimeGrid, cellX), CellCenterWorldY(runtimeGrid, cellY)));
        }

        waypoints = SimplifyNavigationPathSnapshot(worldPath);
        return waypoints.Count > 0;
    }

    private static bool CanStandAtNavigationCellCachedSnapshot(
        RuntimeGridData runtimeGrid,
        NavigationUnitSnapshot unit,
        IReadOnlyList<NavigationObstacleSnapshot> obstacles,
        int cellX,
        int cellY,
        sbyte[] standCache)
    {
        int index = runtimeGrid.IndexOf(cellX, cellY);
        if (standCache[index] == 2)
        {
            return true;
        }

        if (standCache[index] == 1)
        {
            return false;
        }

        double worldX = CellCenterWorldX(runtimeGrid, cellX);
        double worldY = CellCenterWorldY(runtimeGrid, cellY);
        bool result = CanStandAtNavigationCellSnapshot(runtimeGrid, unit, obstacles, cellX, cellY, worldX, worldY);
        standCache[index] = result ? (sbyte)2 : (sbyte)1;
        return result;
    }

    private static bool CanStandAtNavigationCellSnapshot(
        RuntimeGridData runtimeGrid,
        NavigationUnitSnapshot unit,
        IReadOnlyList<NavigationObstacleSnapshot> obstacles,
        int cellX,
        int cellY,
        double worldX,
        double worldY)
    {
        double referenceHeight = SampleTerrainHeight(runtimeGrid, worldX, worldY);
        double maxStep = ResolveEffectiveTraversalStepHeightM(unit);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(unit);
        if (runtimeGrid.IsMovementBlockedAt(worldX, worldY, referenceHeight, maxStep + jumpClearance))
        {
            return false;
        }

        return CanStandOnLocalTerrainPatch(runtimeGrid, cellX, cellY, referenceHeight, maxStep + jumpClearance)
            && !HasNavigationObstacleCollision(unit, obstacles, worldX, worldY);
    }

    private static bool HasNavigationObstacleCollision(
        NavigationUnitSnapshot unit,
        IReadOnlyList<NavigationObstacleSnapshot> obstacles,
        double worldX,
        double worldY)
    {
        double selfRadius = Math.Max(0.06, unit.CollisionRadiusWorld);
        for (int index = 0; index < obstacles.Count; index++)
        {
            NavigationObstacleSnapshot obstacle = obstacles[index];
            double radius = selfRadius + Math.Max(0.04, obstacle.RadiusWorld) + 0.02;
            double dx = worldX - obstacle.X;
            double dy = worldY - obstacle.Y;
            if (dx * dx + dy * dy <= radius * radius)
            {
                return true;
            }
        }

        return false;
    }

    private static List<(double X, double Y)> SimplifyNavigationPathSnapshot(List<(double X, double Y)> worldPath)
    {
        if (worldPath.Count <= 1)
        {
            return worldPath;
        }

        var simplified = new List<(double X, double Y)>();
        int previousDx = 0;
        int previousDy = 0;
        simplified.Add(worldPath[0]);
        for (int index = 1; index < worldPath.Count - 1; index++)
        {
            int nextDx = Math.Sign(worldPath[index + 1].X - worldPath[index].X);
            int nextDy = Math.Sign(worldPath[index + 1].Y - worldPath[index].Y);
            bool directionChanged = index == 1 || nextDx != previousDx || nextDy != previousDy;
            bool strideAnchor = index % 7 == 0;
            if (directionChanged || strideAnchor)
            {
                simplified.Add(worldPath[index]);
            }

            previousDx = nextDx;
            previousDy = nextDy;
        }

        simplified.Add(worldPath[^1]);
        return simplified;
    }

    private bool TryBuildNavigationPath(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        int goalCellX,
        int goalCellY,
        SimulationEntity? targetEntity,
        out List<(double X, double Y)> waypoints)
    {
        waypoints = new List<(double X, double Y)>();
        int startCellX = WorldToCellX(runtimeGrid, entity.X);
        int startCellY = WorldToCellY(runtimeGrid, entity.Y);
        int width = runtimeGrid.WidthCells;
        int height = runtimeGrid.HeightCells;
        int cellCount = width * height;
        int startIndex = runtimeGrid.IndexOf(startCellX, startCellY);
        int goalIndex = runtimeGrid.IndexOf(goalCellX, goalCellY);
        if (startIndex == goalIndex)
        {
            waypoints.Add((CellCenterWorldX(runtimeGrid, goalCellX), CellCenterWorldY(runtimeGrid, goalCellY)));
            return true;
        }

        var frontier = new PriorityQueue<int, double>();
        double[] gScore = new double[cellCount];
        int[] cameFrom = new int[cellCount];
        bool[] closed = new bool[cellCount];
        sbyte[] standCache = new sbyte[cellCount];
        for (int index = 0; index < cellCount; index++)
        {
            gScore[index] = double.PositiveInfinity;
            cameFrom[index] = -1;
        }

        gScore[startIndex] = 0.0;
        frontier.Enqueue(startIndex, EstimatePathHeuristic(runtimeGrid, startCellX, startCellY, goalCellX, goalCellY));
        int expanded = 0;
        int[] neighborDx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] neighborDy = { -1, -1, -1, 0, 0, 1, 1, 1 };

        while (frontier.Count > 0 && expanded < NavigationMaxExpandedCells)
        {
            int currentIndex = frontier.Dequeue();
            if (closed[currentIndex])
            {
                continue;
            }

            closed[currentIndex] = true;
            expanded++;
            if (currentIndex == goalIndex)
            {
                break;
            }

            int currentCellX = currentIndex % width;
            int currentCellY = currentIndex / width;
            double currentWorldX = CellCenterWorldX(runtimeGrid, currentCellX);
            double currentWorldY = CellCenterWorldY(runtimeGrid, currentCellY);
            double currentHeight = SampleTerrainHeight(runtimeGrid, currentWorldX, currentWorldY);

            for (int neighborIndex = 0; neighborIndex < neighborDx.Length; neighborIndex++)
            {
                int nextCellX = currentCellX + neighborDx[neighborIndex];
                int nextCellY = currentCellY + neighborDy[neighborIndex];
                if (nextCellX < 0 || nextCellX >= width || nextCellY < 0 || nextCellY >= height)
                {
                    continue;
                }

                bool diagonal = neighborDx[neighborIndex] != 0 && neighborDy[neighborIndex] != 0;
                if (diagonal)
                {
                    if (!IsNavigationNeighborNavigable(runtimeGrid, currentCellX + neighborDx[neighborIndex], currentCellY, width, height)
                        || !IsNavigationNeighborNavigable(runtimeGrid, currentCellX, currentCellY + neighborDy[neighborIndex], width, height))
                    {
                        continue;
                    }
                }

                int nextIndex = runtimeGrid.IndexOf(nextCellX, nextCellY);
                if (closed[nextIndex] || !CanStandAtNavigationCellCached(world, runtimeGrid, entity, nextCellX, nextCellY, targetEntity, standCache))
                {
                    continue;
                }

                double nextWorldX = CellCenterWorldX(runtimeGrid, nextCellX);
                double nextWorldY = CellCenterWorldY(runtimeGrid, nextCellY);
                double nextHeight = SampleTerrainHeight(runtimeGrid, nextWorldX, nextWorldY);
                double rise = nextHeight - currentHeight;
                double jumpClearance = ResolveTerrainClearanceAllowanceM(entity);
                if (rise > ResolveEffectiveTraversalStepHeightM(entity) + jumpClearance + 1e-6)
                {
                    continue;
                }

                double transitionCost = diagonal ? 1.414 : 1.0;
                transitionCost += Math.Max(0.0, rise) * 3.4;
                transitionCost += Math.Abs(nextHeight - currentHeight) * 0.9;
                if (runtimeGrid.VisionBlockMap[nextIndex])
                {
                    transitionCost += 0.08;
                }

                double tentativeScore = gScore[currentIndex] + transitionCost;
                if (tentativeScore >= gScore[nextIndex])
                {
                    continue;
                }

                cameFrom[nextIndex] = currentIndex;
                gScore[nextIndex] = tentativeScore;
                double priority = tentativeScore + EstimatePathHeuristic(runtimeGrid, nextCellX, nextCellY, goalCellX, goalCellY);
                frontier.Enqueue(nextIndex, priority);
            }
        }

        if (cameFrom[goalIndex] < 0)
        {
            return false;
        }

        List<int> cellPath = new();
        int walkIndex = goalIndex;
        while (walkIndex >= 0)
        {
            cellPath.Add(walkIndex);
            if (walkIndex == startIndex)
            {
                break;
            }

            walkIndex = cameFrom[walkIndex];
        }

        if (cellPath.Count == 0 || cellPath[^1] != startIndex)
        {
            return false;
        }

        cellPath.Reverse();
        List<(double X, double Y)> worldPath = new(cellPath.Count);
        foreach (int cellIndex in cellPath)
        {
            int cellX = cellIndex % width;
            int cellY = cellIndex / width;
            worldPath.Add((CellCenterWorldX(runtimeGrid, cellX), CellCenterWorldY(runtimeGrid, cellY)));
        }

        waypoints = SimplifyNavigationPath(world, runtimeGrid, entity, worldPath, targetEntity);
        return waypoints.Count > 0;
    }

    private List<(double X, double Y)> SimplifyNavigationPath(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        List<(double X, double Y)> worldPath,
        SimulationEntity? targetEntity)
    {
        _ = world;
        _ = runtimeGrid;
        _ = entity;
        _ = targetEntity;
        if (worldPath.Count <= 1)
        {
            return worldPath;
        }

        var simplified = new List<(double X, double Y)>();
        int previousDx = 0;
        int previousDy = 0;
        simplified.Add(worldPath[0]);
        for (int index = 1; index < worldPath.Count - 1; index++)
        {
            int nextDx = Math.Sign(worldPath[index + 1].X - worldPath[index].X);
            int nextDy = Math.Sign(worldPath[index + 1].Y - worldPath[index].Y);
            bool directionChanged = index == 1 || nextDx != previousDx || nextDy != previousDy;
            bool strideAnchor = index % 5 == 0;

            if (directionChanged || strideAnchor)
            {
                simplified.Add(worldPath[index]);
            }

            previousDx = nextDx;
            previousDy = nextDy;
        }

        simplified.Add(worldPath[^1]);
        return simplified;
    }

    private bool HasDirectNavigationCorridor(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double startX,
        double startY,
        double endX,
        double endY,
        SimulationEntity? targetEntity = null)
    {
        double distanceWorld = Math.Sqrt((endX - startX) * (endX - startX) + (endY - startY) * (endY - startY));
        double cellSizeWorld = Math.Max(1e-6, Math.Min(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld));
        int steps = Math.Clamp((int)Math.Ceiling(distanceWorld / (cellSizeWorld * 1.20)), 1, 28);
        double previousHeight = SampleTerrainHeight(runtimeGrid, startX, startY);
        double maxStep = ResolveEffectiveTraversalStepHeightM(entity);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(entity);
        for (int step = 1; step <= steps; step++)
        {
            double t = step / (double)steps;
            double sampleX = Lerp(startX, endX, t);
            double sampleY = Lerp(startY, endY, t);
            double sampleHeight = SampleTerrainHeight(runtimeGrid, sampleX, sampleY);
            if (runtimeGrid.IsMovementBlockedAt(sampleX, sampleY, previousHeight, maxStep + jumpClearance))
            {
                return false;
            }

            if (sampleHeight - previousHeight > maxStep + jumpClearance + 1e-6)
            {
                return false;
            }

            if ((step == steps || step % 3 == 0)
                && !CanOccupyTerrainFootprint(world, runtimeGrid, entity, sampleX, sampleY, sampleHeight, maxStep, jumpClearance))
            {
                return false;
            }

            if (HasStaticCollisionAt(world, runtimeGrid, entity, sampleX, sampleY))
            {
                return false;
            }

            if (HasNavigationEntityObstacleAt(world, entity, targetEntity, sampleX, sampleY))
            {
                return false;
            }

            previousHeight = sampleHeight;
        }

        return true;
    }

    private bool CanStandAtNavigationCellCached(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        int cellX,
        int cellY,
        SimulationEntity? targetEntity,
        sbyte[] standCache)
    {
        int index = runtimeGrid.IndexOf(cellX, cellY);
        if (standCache[index] == 2)
        {
            return true;
        }

        if (standCache[index] == 1)
        {
            return false;
        }

        double worldX = CellCenterWorldX(runtimeGrid, cellX);
        double worldY = CellCenterWorldY(runtimeGrid, cellY);
        bool result = CanStandAtNavigationCell(world, runtimeGrid, entity, cellX, cellY, worldX, worldY, targetEntity);
        standCache[index] = result ? (sbyte)2 : (sbyte)1;
        return result;
    }

    private bool CanStandAtNavigationCell(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        int cellX,
        int cellY,
        double worldX,
        double worldY,
        SimulationEntity? targetEntity)
    {
        double referenceHeight = SampleTerrainHeight(runtimeGrid, worldX, worldY);
        double maxStep = ResolveEffectiveTraversalStepHeightM(entity);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(entity);
        if (runtimeGrid.IsMovementBlockedAt(worldX, worldY, referenceHeight, maxStep + jumpClearance))
        {
            return false;
        }

        if (!CanStandOnLocalTerrainPatch(runtimeGrid, cellX, cellY, referenceHeight, maxStep + jumpClearance))
        {
            return false;
        }

        return !HasStaticCollisionAt(world, runtimeGrid, entity, worldX, worldY)
            && !HasNavigationEntityObstacleAt(world, entity, targetEntity, worldX, worldY);
    }

    private static bool HasNavigationEntityObstacleAt(
        SimulationWorldState world,
        SimulationEntity entity,
        SimulationEntity? targetEntity,
        double worldX,
        double worldY)
    {
        double selfRadius = entity.CollisionRadiusWorld > 1e-6
            ? entity.CollisionRadiusWorld
            : Math.Max(0.10, Math.Max(entity.BodyLengthM, entity.BodyWidthM) * 0.5);
        foreach (SimulationEntity other in world.Entities)
        {
            if (ReferenceEquals(entity, other)
                || ReferenceEquals(targetEntity, other)
                || !other.IsAlive
                || !IsNavigationObstacleEntity(other))
            {
                continue;
            }

            double otherRadius = other.CollisionRadiusWorld > 1e-6
                ? other.CollisionRadiusWorld
                : Math.Max(0.10, Math.Max(other.BodyLengthM, other.BodyWidthM) * 0.5);
            double radius = selfRadius + otherRadius + 0.04;
            double dx = worldX - other.X;
            double dy = worldY - other.Y;
            if (dx * dx + dy * dy <= radius * radius)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanStandOnLocalTerrainPatch(
        RuntimeGridData runtimeGrid,
        int cellX,
        int cellY,
        double referenceHeight,
        double allowedRiseM)
    {
        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            int sampleY = cellY + offsetY;
            if (sampleY < 0 || sampleY >= runtimeGrid.HeightCells)
            {
                continue;
            }

            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                int sampleX = cellX + offsetX;
                if (sampleX < 0 || sampleX >= runtimeGrid.WidthCells)
                {
                    continue;
                }

                double sampleWorldX = CellCenterWorldX(runtimeGrid, sampleX);
                double sampleWorldY = CellCenterWorldY(runtimeGrid, sampleY);
                if (runtimeGrid.IsMovementBlockedAt(sampleWorldX, sampleWorldY, referenceHeight, allowedRiseM))
                {
                    return false;
                }

            }
        }

        return true;
    }

    private static bool IsNavigationNeighborNavigable(RuntimeGridData runtimeGrid, int cellX, int cellY, int width, int height)
    {
        if (cellX < 0 || cellX >= width || cellY < 0 || cellY >= height)
        {
            return false;
        }

        if (runtimeGrid.CollisionSurface is not null)
        {
            return true;
        }

        return !runtimeGrid.MovementBlockMap[runtimeGrid.IndexOf(cellX, cellY)];
    }

    private static double EstimatePathHeuristic(RuntimeGridData runtimeGrid, int fromX, int fromY, int goalX, int goalY)
    {
        double dx = (goalX - fromX) * runtimeGrid.CellWidthWorld;
        double dy = (goalY - fromY) * runtimeGrid.CellHeightWorld;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double CellCenterWorldX(RuntimeGridData runtimeGrid, int cellX)
        => (cellX + 0.5) * runtimeGrid.CellWidthWorld;

    private static double CellCenterWorldY(RuntimeGridData runtimeGrid, int cellY)
        => (cellY + 0.5) * runtimeGrid.CellHeightWorld;

    private static bool HasStaticStructureCollisionAt(
        SimulationWorldState world,
        SimulationEntity entity,
        double nextX,
        double nextY)
    {
        CollisionFootprint footprint = BuildCollisionFootprint(world, entity, nextX, nextY);
        foreach (SimulationEntity other in world.Entities)
        {
            if (ReferenceEquals(entity, other)
                || !other.IsAlive
                || !SimulationCombatMath.IsStructure(other)
                || SimulationCombatMath.IsLegacyMechanismCollisionSuppressed(other))
            {
                continue;
            }

            CollisionFootprint otherFootprint = BuildCollisionFootprint(world, other, other.X, other.Y);
            double dx = nextX - other.X;
            double dy = nextY - other.Y;
            double broadPhaseDistanceSq = dx * dx + dy * dy;
            double broadPhaseRadius = Math.Max(0.08, footprint.BoundingRadiusWorld + otherFootprint.BoundingRadiusWorld + 0.02);
            if (broadPhaseDistanceSq > broadPhaseRadius * broadPhaseRadius)
            {
                continue;
            }

            if (!VerticalIntervalsOverlap(footprint, otherFootprint))
            {
                continue;
            }

            if (IntersectsFootprint(footprint, otherFootprint))
            {
                return true;
            }
        }

        return false;
    }

    private static SimulationEntity? ResolveTacticalAttackTarget(SimulationWorldState world, SimulationEntity entity)
    {
        string? targetId = entity.TacticalTargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        return world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && string.Equals(candidate.Id, targetId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.Team, entity.Team, StringComparison.OrdinalIgnoreCase));
    }

    private static SimulationEntity? ResolveStrategicAttackTarget(SimulationWorldState world, SimulationEntity entity)
    {
        string enemyTeam = string.Equals(entity.Team, "red", StringComparison.OrdinalIgnoreCase) ? "blue" : "red";
        SimulationEntity? enemyOutpost = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && string.Equals(candidate.Team, enemyTeam, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.EntityType, "outpost", StringComparison.OrdinalIgnoreCase));
        if (enemyOutpost is not null)
        {
            return enemyOutpost;
        }

        SimulationEntity? enemyBase = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && string.Equals(candidate.Team, enemyTeam, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.EntityType, "base", StringComparison.OrdinalIgnoreCase));
        return enemyBase;
    }

    private bool TryResolveStrategicMoveTarget(
        SimulationWorldState world,
        SimulationEntity entity,
        SimulationEntity attackTarget,
        double metersPerWorldUnit,
        out double targetX,
        out double targetY,
        out double desiredDistanceM,
        out double aggressionScale,
        out string navigationKey,
        out string decisionSelected,
        out string decisionText)
    {
        targetX = attackTarget.X;
        targetY = attackTarget.Y;
        desiredDistanceM = 4.0;
        aggressionScale = 0.88;
        navigationKey = attackTarget.Id;
        decisionSelected = entity.AiDecisionSelected;
        decisionText = entity.AiDecision;

        bool deploymentStructure =
            string.Equals(attackTarget.EntityType, "outpost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attackTarget.EntityType, "base", StringComparison.OrdinalIgnoreCase);
        if (!deploymentStructure)
        {
            entity.HeroDeploymentRequested = false;
            return false;
        }

        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            FacilityRegion? deployment = FindNearestFacility(
                entity.X,
                entity.Y,
                facility => string.Equals(facility.Type, "buff_hero_deployment", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(facility.Team, entity.Team, StringComparison.OrdinalIgnoreCase));
            if (deployment is null)
            {
                return false;
            }

            (targetX, targetY) = ResolveFacilityCenter(deployment);
            double distanceM = Math.Sqrt((targetX - entity.X) * (targetX - entity.X) + (targetY - entity.Y) * (targetY - entity.Y))
                * Math.Max(metersPerWorldUnit, 1e-6);
            entity.HeroDeploymentRequested = distanceM <= 1.10 || deployment.Contains(entity.X, entity.Y);
            desiredDistanceM = entity.HeroDeploymentActive ? 0.15 : 0.45;
            aggressionScale = entity.HeroDeploymentActive ? 0.0 : 0.82;
            navigationKey = $"hero_deploy:{deployment.Id}";
            bool attackingOutpost = string.Equals(attackTarget.EntityType, "outpost", StringComparison.OrdinalIgnoreCase);
            decisionSelected = attackingOutpost ? "hero_deploy_outpost" : "hero_deploy_base";
            decisionText = entity.HeroDeploymentActive
                ? (attackingOutpost ? "部署区吊射前哨站旋转装甲" : "部署区吊射基地顶部装甲")
                : (attackingOutpost ? "前往部署区吊射前哨站" : "前往部署区吊射基地");
            return !entity.HeroDeploymentActive || distanceM > desiredDistanceM;
        }

        if (string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            FacilityRegion? highland = FindNearestFacility(
                attackTarget.X,
                attackTarget.Y,
                facility => facility.Type.Contains("highland", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(facility.Team, "neutral", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(facility.Team, entity.Team, StringComparison.OrdinalIgnoreCase)));
            if (highland is null)
            {
                return false;
            }

            (double highlandX, double highlandY) = ResolveFacilityCenter(highland);
            double distanceToOutpostM = Math.Sqrt(
                (entity.X - attackTarget.X) * (entity.X - attackTarget.X)
                + (entity.Y - attackTarget.Y) * (entity.Y - attackTarget.Y)) * Math.Max(metersPerWorldUnit, 1e-6);
            if (distanceToOutpostM <= 5.2)
            {
                return false;
            }

            targetX = highlandX;
            targetY = highlandY;
            desiredDistanceM = 0.70;
            aggressionScale = string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase) ? 0.74 : 0.90;
            navigationKey = $"highland_push:{highland.Id}:{attackTarget.Id}";
            decisionSelected = "highland_outpost_push";
            decisionText = "上高地推前哨站";
            return true;
        }

        return false;
    }

    private FacilityRegion? FindNearestFacility(double x, double y, Func<FacilityRegion, bool> predicate)
    {
        FacilityRegion? nearest = null;
        double bestDistanceSq = double.MaxValue;
        foreach (FacilityRegion facility in _facilities)
        {
            if (!predicate(facility))
            {
                continue;
            }

            (double centerX, double centerY) = ResolveFacilityCenter(facility);
            double dx = centerX - x;
            double dy = centerY - y;
            double distanceSq = dx * dx + dy * dy;
            if (distanceSq >= bestDistanceSq)
            {
                continue;
            }

            bestDistanceSq = distanceSq;
            nearest = facility;
        }

        return nearest;
    }

    private static (double X, double Y) ResolveFacilityCenter(FacilityRegion facility)
    {
        if (facility.Points.Count > 0)
        {
            double sumX = 0.0;
            double sumY = 0.0;
            foreach (Point2D point in facility.Points)
            {
                sumX += point.X;
                sumY += point.Y;
            }

            return (sumX / facility.Points.Count, sumY / facility.Points.Count);
        }

        return ((facility.X1 + facility.X2) * 0.5, (facility.Y1 + facility.Y2) * 0.5);
    }

    private static double StableUnitPhase(string id)
    {
        unchecked
        {
            int hash = 17;
            foreach (char ch in id ?? string.Empty)
            {
                hash = hash * 31 + ch;
            }

            return (Math.Abs(hash) % 6283) / 1000.0;
        }
    }

    private static bool IsDuelStrafeScenario(
        SimulationWorldState world,
        SimulationEntity entity,
        SimulationEntity? tacticalTarget)
    {
        if (entity.IsPlayerControlled
            || tacticalTarget is null
            || !tacticalTarget.IsPlayerControlled)
        {
            return false;
        }

        int movableCount = 0;
        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!IsMovableEntity(candidate))
            {
                continue;
            }

            movableCount++;
            if (movableCount > 2)
            {
                return false;
            }
        }

        return movableCount == 2;
    }

    private static double ResolveDuelStrafeInput(string entityId, double gameTimeSec)
    {
        double phase = StableUnitPhase(entityId);
        return Math.Sin(gameTimeSec * 2.9 + phase) * 0.86;
    }

    private void UpdateTurretAim(SimulationWorldState world, RuntimeGridData runtimeGrid, SimulationEntity entity, double dt, bool playerControlled)
    {
        bool useAutoAim = entity.AutoAimRequested || !playerControlled;
        if (!useAutoAim)
        {
            ClearAutoAimTracking(entity.Id);
            ClearAutoAimState(entity);
            return;
        }

        double autoAimMaxDistanceM = SimulationCombatMath.IsHeroLobAutoAimMode(entity)
            ? 1000.0
            : _rules.Combat.AutoAimMaxDistanceM;

        if (TryReuseRecentAutoAimSolution(world, entity, playerControlled, out SimulationEntity? cachedTarget, out ArmorPlateTarget cachedPlate, out AutoAimSolution cachedSolution))
        {
            ApplyAutoAimSolution(world, entity, cachedTarget!, cachedPlate, cachedSolution, dt);
            return;
        }

        if (TryResolveVisibleLockedHeroLobStructureSolution(world, runtimeGrid, entity, autoAimMaxDistanceM, out SimulationEntity? lockedTarget, out ArmorPlateTarget lockedPlate, out AutoAimSolution lockedSolution)
            || TryResolveRetainedHeroLobStructureSolution(world, entity, autoAimMaxDistanceM, out lockedTarget, out lockedPlate, out lockedSolution))
        {
            ApplyAutoAimSolution(world, entity, lockedTarget!, lockedPlate, lockedSolution, dt);
            return;
        }

        if (!playerControlled
            && TryResolveShortRetainedArmorTrackingSolution(world, entity, autoAimMaxDistanceM, out SimulationEntity? retainedArmorTarget, out ArmorPlateTarget retainedArmorPlate, out AutoAimSolution retainedArmorSolution))
        {
            ApplyAutoAimSolution(world, entity, retainedArmorTarget!, retainedArmorPlate, retainedArmorSolution, dt);
            return;
        }

        if (!HasRoughAutoAimCandidate(world, entity, autoAimMaxDistanceM))
        {
            if (TryResolveRetainedHeroLobStructureSolution(world, entity, autoAimMaxDistanceM, out SimulationEntity? retainedTarget, out ArmorPlateTarget retainedPlate, out AutoAimSolution retainedSolution)
                || TryResolveRetainedArmorTrackingSolution(world, entity, autoAimMaxDistanceM, out retainedTarget, out retainedPlate, out retainedSolution))
            {
                ApplyAutoAimSolution(world, entity, retainedTarget!, retainedPlate, retainedSolution, dt);
                return;
            }

            ClearAutoAimTracking(entity.Id);
            ClearAutoAimState(entity);
            return;
        }

        if (!SimulationCombatMath.TryAcquireAutoAimTarget(
            world,
            entity,
            autoAimMaxDistanceM,
            out SimulationEntity? target,
            out ArmorPlateTarget plate,
            (candidate, candidatePlate) => AutoAimVisibility.CanSeePlate(world, runtimeGrid, entity, candidate, candidatePlate)))
        {
            if (TryResolveRetainedHeroLobStructureSolution(world, entity, autoAimMaxDistanceM, out SimulationEntity? retainedTarget, out ArmorPlateTarget retainedPlate, out AutoAimSolution retainedSolution)
                || TryResolveRetainedArmorTrackingSolution(world, entity, autoAimMaxDistanceM, out retainedTarget, out retainedPlate, out retainedSolution))
            {
                ApplyAutoAimSolution(world, entity, retainedTarget!, retainedPlate, retainedSolution, dt);
                return;
            }

            ClearAutoAimTracking(entity.Id);
            ClearAutoAimState(entity);
            return;
        }

        ArmorPlateTarget observationPlate = ResolveAutoAimObservationPlate(world, target!, plate);
        AutoAimObservedState observationState = ResolveAutoAimObservationState(world, entity, target!, plate, observationPlate);
        AutoAimSolution solution = ComputeAutoAimSolutionFromObservationState(
            world,
            entity,
            target!,
            plate,
            autoAimMaxDistanceM,
            observationState);
        StoreAutoAimSolution(entity, target!, plate, observationPlate, observationState, solution, world.GameTimeSec);
        ApplyAutoAimSolution(world, entity, target!, plate, solution, dt);
    }

    private bool TryReuseRecentAutoAimSolution(
        SimulationWorldState world,
        SimulationEntity entity,
        bool playerControlled,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        out AutoAimSolution solution)
    {
        target = null;
        plate = default;
        solution = default;

        if (!_autoAimSolveCache.TryGetValue(entity.Id, out AutoAimSolveCache? cache))
        {
            return false;
        }

        double reuseWindowSec = ResolveAutoAimReuseWindowSec(entity, playerControlled);
        if (reuseWindowSec <= 1e-6 || world.GameTimeSec - cache.GameTimeSec > reuseWindowSec)
        {
            return false;
        }

        if (string.Equals(entity.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cache.TargetKind, "energy_disk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && string.Equals(candidate.Id, cache.TargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        plate = SimulationCombatMath.GetAttackableArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, cache.PlateId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(plate.Id))
        {
            target = null;
            plate = default;
            return false;
        }

        string targetKind = SimulationCombatMath.ResolveAutoAimTargetKind(target, plate);
        bool heroLobStructureCache =
            SimulationCombatMath.IsHeroLobAutoAimMode(entity)
            && IsHeroLobStructureTargetKind(targetKind);
        double dxWorld = plate.X - entity.X;
        double dyWorld = plate.Y - entity.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        double allowedDistanceM = heroLobStructureCache ? 1000.0 : _rules.Combat.AutoAimMaxDistanceM;
        if (!SimulationCombatMath.IsAutoAimArmorTargetEligible(world, entity, target, plate, distanceM)
            || distanceM > allowedDistanceM + 0.4)
        {
            target = null;
            plate = default;
            return false;
        }

        if (!heroLobStructureCache && SimulationCombatMath.IsRotatingArmorPlate(world, target, plate))
        {
            target = null;
            plate = default;
            return false;
        }

        if (!heroLobStructureCache && !SimulationCombatMath.IsAutoAimTargetEffectivelyStatic(world, entity, target, plate))
        {
            target = null;
            plate = default;
            return false;
        }

        solution = cache.Solution;
        return true;
    }

    private static double ResolveAutoAimReuseWindowSec(SimulationEntity entity, bool playerControlled)
    {
        bool largeProjectile = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        if (!largeProjectile && !entity.TraversalActive)
        {
            return 0.0;
        }

        double reuseWindowSec = 0.0;
        if (largeProjectile && playerControlled)
        {
            reuseWindowSec = LargeProjectileAutoAimReuseSec;
        }

        if (largeProjectile
            && playerControlled
            && SimulationCombatMath.IsHeroLobAutoAimMode(entity)
            && IsHeroLobStructureTargetKind(entity.AutoAimTargetKind))
        {
            reuseWindowSec = Math.Max(reuseWindowSec, HeroLobStructureAutoAimReuseSec);
        }

        if (entity.TraversalActive)
        {
            reuseWindowSec = Math.Max(reuseWindowSec, TraversalAutoAimReuseSec);
        }

        return reuseWindowSec;
    }

    private void StoreAutoAimSolution(
        SimulationEntity entity,
        SimulationEntity target,
        ArmorPlateTarget plate,
        ArmorPlateTarget observationPlate,
        AutoAimObservedState observationState,
        AutoAimSolution solution,
        double gameTimeSec,
        bool observedThisFrame = true)
    {
        double lastObservedGameTimeSec = gameTimeSec;
        if (!observedThisFrame
            && _autoAimSolveCache.TryGetValue(entity.Id, out AutoAimSolveCache? previousCache)
            && string.Equals(previousCache.TargetId, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            lastObservedGameTimeSec = previousCache.LastObservedGameTimeSec;
        }

        _autoAimSolveCache[entity.Id] = new AutoAimSolveCache(
            gameTimeSec,
            lastObservedGameTimeSec,
            target.Id,
            plate.Id,
            SimulationCombatMath.ResolveAutoAimTargetKind(target, plate),
            observationPlate,
            observationState,
            target.X,
            target.Y,
            solution);
    }

    private AutoAimSolution ComputeAutoAimSolutionFromObservationState(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget plate,
        double maxDistanceM,
        AutoAimObservedState observationState)
    {
        return UseThirdOrderEkfAutoAimPoseChain
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

    private static bool IsRetainedRotatingObservationTarget(string targetKind, double angularVelocityRadPerSec)
        => string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(targetKind, "vehicle_armor", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(angularVelocityRadPerSec) >= 0.18);

    private static AutoAimObservedState AdvanceRetainedObservationState(
        SimulationWorldState world,
        AutoAimSolveCache cache,
        double dtSec)
    {
        AutoAimObservedState state = cache.ObservationState;
        if (dtSec <= 1e-6)
        {
            return state;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double xWorld = state.AimPointXWorld;
        double yWorld = state.AimPointYWorld;
        double heightM = state.AimPointHeightM;
        double velocityXMps = state.VelocityXMps;
        double velocityYMps = state.VelocityYMps;
        double velocityZMps = state.VelocityZMps;
        double accelerationXMps2 = state.AccelerationXMps2;
        double accelerationYMps2 = state.AccelerationYMps2;
        double accelerationZMps2 = state.AccelerationZMps2;

        if (IsRetainedRotatingObservationTarget(cache.TargetKind, state.AngularVelocityRadPerSec))
        {
            double radialXM = (xWorld - cache.TargetCenterXWorld) * metersPerWorldUnit;
            double radialYM = (yWorld - cache.TargetCenterYWorld) * metersPerWorldUnit;
            double radialLengthSq = radialXM * radialXM + radialYM * radialYM;
            if (radialLengthSq > 1e-8)
            {
                double angleRad = state.AngularVelocityRadPerSec * dtSec;
                double cos = Math.Cos(angleRad);
                double sin = Math.Sin(angleRad);
                double rotatedRadialXM = radialXM * cos - radialYM * sin;
                double rotatedRadialYM = radialXM * sin + radialYM * cos;
                xWorld = cache.TargetCenterXWorld + rotatedRadialXM / metersPerWorldUnit;
                yWorld = cache.TargetCenterYWorld + rotatedRadialYM / metersPerWorldUnit;
                velocityXMps = -rotatedRadialYM * state.AngularVelocityRadPerSec;
                velocityYMps = rotatedRadialXM * state.AngularVelocityRadPerSec;
                accelerationXMps2 = -rotatedRadialXM * state.AngularVelocityRadPerSec * state.AngularVelocityRadPerSec;
                accelerationYMps2 = -rotatedRadialYM * state.AngularVelocityRadPerSec * state.AngularVelocityRadPerSec;
            }
            else
            {
                xWorld += (velocityXMps * dtSec + 0.5 * accelerationXMps2 * dtSec * dtSec) / metersPerWorldUnit;
                yWorld += (velocityYMps * dtSec + 0.5 * accelerationYMps2 * dtSec * dtSec) / metersPerWorldUnit;
                velocityXMps += accelerationXMps2 * dtSec;
                velocityYMps += accelerationYMps2 * dtSec;
            }
        }
        else
        {
            xWorld += (velocityXMps * dtSec + 0.5 * accelerationXMps2 * dtSec * dtSec) / metersPerWorldUnit;
            yWorld += (velocityYMps * dtSec + 0.5 * accelerationYMps2 * dtSec * dtSec) / metersPerWorldUnit;
            velocityXMps += accelerationXMps2 * dtSec;
            velocityYMps += accelerationYMps2 * dtSec;
        }

        heightM += velocityZMps * dtSec + 0.5 * accelerationZMps2 * dtSec * dtSec;
        velocityZMps += accelerationZMps2 * dtSec;

        return state with
        {
            AimPointXWorld = xWorld,
            AimPointYWorld = yWorld,
            AimPointHeightM = heightM,
            VelocityXMps = velocityXMps,
            VelocityYMps = velocityYMps,
            VelocityZMps = velocityZMps,
            AccelerationXMps2 = accelerationXMps2,
            AccelerationYMps2 = accelerationYMps2,
            AccelerationZMps2 = accelerationZMps2,
        };
    }

    private static ArmorPlateTarget AdvanceRetainedObservationPlate(
        SimulationWorldState world,
        AutoAimSolveCache cache,
        AutoAimObservedState observationState,
        double dtSec)
    {
        double yawDeg = cache.ObservationPlate.YawDeg;
        if (Math.Abs(observationState.AngularVelocityRadPerSec) > 1e-5)
        {
            yawDeg = SimulationCombatMath.NormalizeDeg(
                yawDeg + observationState.AngularVelocityRadPerSec * dtSec * 180.0 / Math.PI);
        }

        return cache.ObservationPlate with
        {
            X = observationState.AimPointXWorld,
            Y = observationState.AimPointYWorld,
            HeightM = observationState.AimPointHeightM,
            YawDeg = yawDeg,
        };
    }

    private bool TryResolveVisibleLockedHeroLobStructureSolution(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        out AutoAimSolution solution)
    {
        target = null;
        plate = default;
        solution = default;

        if (!SimulationCombatMath.IsHeroLobAutoAimMode(shooter)
            || !IsHeroLobStructureTargetKind(shooter.AutoAimTargetKind)
            || string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return false;
        }

        target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && !string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null || !SimulationCombatMath.IsHeroStructureAutoAimTargetEntity(world, shooter, target))
        {
            target = null;
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        plate = SimulationCombatMath.GetAttackableArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, shooter.AutoAimPlateId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(plate.Id)
            || !SimulationCombatMath.IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate)
            || !AutoAimVisibility.CanSeePlate(world, runtimeGrid, shooter, target, plate))
        {
            target = null;
            plate = default;
            return false;
        }

        double dxWorld = plate.X - shooter.X;
        double dyWorld = plate.Y - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        if (distanceM > maxDistanceM + 0.4)
        {
            target = null;
            plate = default;
            return false;
        }

        ArmorPlateTarget observationPlate = ResolveAutoAimObservationPlate(world, target, plate);
        AutoAimObservedState observationState = ResolveAutoAimObservationState(world, shooter, target, plate, observationPlate);
        solution = ComputeAutoAimSolutionFromObservationState(
            world,
            shooter,
            target,
            plate,
            maxDistanceM,
            observationState);
        StoreAutoAimSolution(shooter, target, plate, observationPlate, observationState, solution, world.GameTimeSec);
        return true;
    }

    private bool TryResolveRetainedHeroLobStructureSolution(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        out AutoAimSolution solution)
    {
        target = null;
        plate = default;
        solution = default;

        if (!SimulationCombatMath.IsHeroLobAutoAimMode(shooter)
            || string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId)
            || !_autoAimSolveCache.TryGetValue(shooter.Id, out AutoAimSolveCache? cache))
        {
            return false;
        }

        target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && !string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null || !SimulationCombatMath.IsHeroStructureAutoAimTargetEntity(world, shooter, target))
        {
            target = null;
            return false;
        }

        double dtSec = Math.Clamp(world.GameTimeSec - cache.GameTimeSec, 0.0, AutoAimResidualPredictionClampSec);
        AutoAimObservedState retainedObservationState = AdvanceRetainedObservationState(world, cache, dtSec);
        plate = AdvanceRetainedObservationPlate(world, cache, retainedObservationState, dtSec);
        if (!string.Equals(plate.Id, shooter.AutoAimPlateId, StringComparison.OrdinalIgnoreCase)
            || !SimulationCombatMath.IsHeroStructureAutoAimTargetPlate(world, shooter, target, plate))
        {
            target = null;
            plate = default;
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double dxWorld = retainedObservationState.AimPointXWorld - shooter.X;
        double dyWorld = retainedObservationState.AimPointYWorld - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        if (distanceM > maxDistanceM + 0.4)
        {
            target = null;
            plate = default;
            return false;
        }

        solution = ComputeAutoAimSolutionFromObservationState(
            world,
            shooter,
            target,
            plate,
            maxDistanceM,
            retainedObservationState);
        StoreAutoAimSolution(shooter, target, plate, plate, retainedObservationState, solution, world.GameTimeSec, observedThisFrame: false);
        return true;
    }

    private bool TryResolveRetainedArmorTrackingSolution(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        out AutoAimSolution solution)
    {
        target = null;
        plate = default;
        solution = default;

        if (SimulationCombatMath.IsHeroLobAutoAimMode(shooter)
            || !shooter.AutoAimLocked
            || string.Equals(shooter.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            || !SimulationCombatMath.IsArmorAutoAimTargetKind(shooter.AutoAimTargetKind)
            || string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId)
            || !_autoAimSolveCache.TryGetValue(shooter.Id, out AutoAimSolveCache? cache))
        {
            return false;
        }

        target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && !string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Id, cache.TargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        if (world.GameTimeSec - cache.LastObservedGameTimeSec > AutoAimResidualMemorySec)
        {
            target = null;
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double dtSec = Math.Clamp(world.GameTimeSec - cache.GameTimeSec, 0.0, 0.30);
        AutoAimObservedState retainedObservationState = AdvanceRetainedObservationState(world, cache, dtSec);
        plate = AdvanceRetainedObservationPlate(world, cache, retainedObservationState, dtSec);
        if (!string.Equals(plate.Id, shooter.AutoAimPlateId, StringComparison.OrdinalIgnoreCase))
        {
            target = null;
            plate = default;
            return false;
        }

        double dxWorld = retainedObservationState.AimPointXWorld - shooter.X;
        double dyWorld = retainedObservationState.AimPointYWorld - shooter.Y;
        double distanceM = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld) * metersPerWorldUnit;
        if (!SimulationCombatMath.IsAutoAimArmorTargetEligible(world, shooter, target, plate, distanceM)
            || distanceM > maxDistanceM + 0.4)
        {
            target = null;
            plate = default;
            return false;
        }

        solution = ComputeAutoAimSolutionFromObservationState(
            world,
            shooter,
            target,
            plate,
            maxDistanceM,
            retainedObservationState);
        StoreAutoAimSolution(shooter, target, plate, plate, retainedObservationState, solution, world.GameTimeSec, observedThisFrame: false);
        return true;
    }

    private bool TryResolveShortRetainedArmorTrackingSolution(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate,
        out AutoAimSolution solution)
    {
        target = null;
        plate = default;
        solution = default;
        if (!_autoAimSolveCache.TryGetValue(shooter.Id, out AutoAimSolveCache? cache)
            || world.GameTimeSec - cache.LastObservedGameTimeSec > AiRetainedArmorTrackingReuseSec)
        {
            return false;
        }

        return TryResolveRetainedArmorTrackingSolution(world, shooter, maxDistanceM, out target, out plate, out solution);
    }

    private void ClearAutoAimTracking(string entityId)
    {
        _autoAimSolveCache.Remove(entityId);
        _autoAimObservationFilters.Remove(entityId);
        _autoAimObservationEkf3Filters.Remove(entityId);
    }

    private static ArmorPlateTarget ResolveAutoAimObservationPlate(
        SimulationWorldState world,
        SimulationEntity target,
        ArmorPlateTarget aimPlate)
    {
        if (!string.Equals(target.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return aimPlate;
        }

        if (!SimulationCombatMath.TryParseEnergyArmIndex(aimPlate.Id, out string team, out int armIndex))
        {
            return aimPlate;
        }

        world.Teams.TryGetValue(team, out SimulationTeamState? teamState);
        foreach (ArmorPlateTarget candidate in SimulationCombatMath.GetEnergyMechanismTargets(
                     target,
                     Math.Max(world.MetersPerWorldUnit, 1e-6),
                     world.GameTimeSec,
                     team,
                     teamState))
        {
            int ringScore = candidate.EnergyRingScore;
            if (ringScore <= 0 && !SimulationCombatMath.TryParseEnergyRingScore(candidate.Id, out ringScore))
            {
                continue;
            }

            if (ringScore == 1
                && SimulationCombatMath.TryParseEnergyArmIndex(candidate.Id, out string candidateTeam, out int candidateArm)
                && string.Equals(candidateTeam, team, StringComparison.OrdinalIgnoreCase)
                && candidateArm == armIndex)
            {
                return candidate;
            }
        }

        return aimPlate;
    }

    private AutoAimObservedState ResolveAutoAimObservationState(
        SimulationWorldState world,
        SimulationEntity shooter,
        SimulationEntity target,
        ArmorPlateTarget aimPlate,
        ArmorPlateTarget observationPlate)
    {
        return UseThirdOrderEkfAutoAimPoseChain
            ? UpdateAutoAimObservationStateThirdOrderEkf(world, shooter, target, aimPlate, observationPlate)
            : UpdateAutoAimObservationState(world, shooter, target, aimPlate, observationPlate);
    }

    // 保留旧的常速度 Kalman 链路，新的 3 阶 EKF 链路单独并行实现，避免旧链路被破坏。
    private AutoAimObservedState UpdateAutoAimObservationState(
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

        if (!_autoAimObservationFilters.TryGetValue(shooter.Id, out AutoAimObservationFilterState? filter)
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
            double initialVelocityXMps = 0.0;
            double initialVelocityYMps = 0.0;
            double initialVelocityZMps = 0.0;
            if (filter.HasLastMeasurement)
            {
                double measurementDtSec = Math.Clamp(world.GameTimeSec - filter.LastMeasurementTimeSec, 1.0 / 240.0, 0.20);
                initialVelocityXMps = (observedXWorld - filter.LastMeasurementXWorld) * metersPerWorldUnit / measurementDtSec;
                initialVelocityYMps = (observedYWorld - filter.LastMeasurementYWorld) * metersPerWorldUnit / measurementDtSec;
                initialVelocityZMps = (observedHeightM - filter.LastMeasurementHeightM) / measurementDtSec;
            }

            filter.Initialize(
                observedXWorld,
                observedYWorld,
                observedHeightM,
                initialVelocityXMps,
                initialVelocityYMps,
                initialVelocityZMps,
                metersPerWorldUnit);
        }
        else
        {
            filter.Update(
                observedXWorld,
                observedYWorld,
                observedHeightM,
                dtSec,
                metersPerWorldUnit,
                measurementNoiseM,
                accelerationNoiseMps2);
        }

        filter.LastMeasurementXWorld = observedXWorld;
        filter.LastMeasurementYWorld = observedYWorld;
        filter.LastMeasurementHeightM = observedHeightM;
        filter.LastMeasurementTimeSec = world.GameTimeSec;
        filter.HasLastMeasurement = true;
        filter.LastUpdateTimeSec = world.GameTimeSec;

        double angularVelocityRadPerSec = 0.0;
        if (TryResolveObservedPlateAngularVelocity(world, target, aimPlate, observationPlate, filter, out double resolvedOmega))
        {
            angularVelocityRadPerSec = resolvedOmega;
        }

        _autoAimObservationFilters[shooter.Id] = filter;
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

    private AutoAimObservedState UpdateAutoAimObservationStateThirdOrderEkf(
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

        if (!_autoAimObservationEkf3Filters.TryGetValue(shooter.Id, out AutoAimThirdOrderEkfPoseFilterState? filter)
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
            double initialVelocityXMps = 0.0;
            double initialVelocityYMps = 0.0;
            double initialVelocityZMps = 0.0;
            if (filter.HasLastMeasurement)
            {
                double measurementDtSec = Math.Clamp(world.GameTimeSec - filter.LastMeasurementTimeSec, 1.0 / 240.0, 0.20);
                initialVelocityXMps = (observedXWorld - filter.LastMeasurementXWorld) * metersPerWorldUnit / measurementDtSec;
                initialVelocityYMps = (observedYWorld - filter.LastMeasurementYWorld) * metersPerWorldUnit / measurementDtSec;
                initialVelocityZMps = (observedHeightM - filter.LastMeasurementHeightM) / measurementDtSec;
            }

            filter.Initialize(
                observedXWorld,
                observedYWorld,
                observedHeightM,
                initialVelocityXMps,
                initialVelocityYMps,
                initialVelocityZMps,
                metersPerWorldUnit);
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

        filter.LastMeasurementXWorld = observedXWorld;
        filter.LastMeasurementYWorld = observedYWorld;
        filter.LastMeasurementHeightM = observedHeightM;
        filter.LastMeasurementTimeSec = world.GameTimeSec;
        filter.HasLastMeasurement = true;
        filter.LastUpdateTimeSec = world.GameTimeSec;

        double angularVelocityRadPerSec = 0.0;
        if (TryResolveObservedPlateAngularVelocityFromFilteredState(
                world,
                target,
                aimPlate,
                filter.FilteredXWorld,
                filter.FilteredYWorld,
                filter.FilteredVelocityXMps,
                filter.FilteredVelocityYMps,
                out double resolvedOmega))
        {
            angularVelocityRadPerSec = resolvedOmega;
        }

        _autoAimObservationEkf3Filters[shooter.Id] = filter;
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

    private static bool TryResolveObservedPlateAngularVelocity(
        SimulationWorldState world,
        SimulationEntity target,
        ArmorPlateTarget aimPlate,
        ArmorPlateTarget observationPlate,
        AutoAimObservationFilterState filter,
        out double angularVelocityRadPerSec)
    {
        angularVelocityRadPerSec = 0.0;
        if (!filter.Initialized)
        {
            return false;
        }

        return TryResolveObservedPlateAngularVelocityFromFilteredState(
            world,
            target,
            aimPlate,
            filter.FilteredXWorld,
            filter.FilteredYWorld,
            filter.FilteredVelocityXMps,
            filter.FilteredVelocityYMps,
            out angularVelocityRadPerSec);
    }

    private static bool TryResolveObservedPlateAngularVelocityFromFilteredState(
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

    private static bool TryResolveLockedArmorTarget(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM,
        out SimulationEntity? target,
        out ArmorPlateTarget plate)
    {
        target = null;
        plate = default;
        if (!shooter.AutoAimLocked
            || !SimulationCombatMath.IsArmorAutoAimTargetKind(shooter.AutoAimTargetKind)
            || string.Equals(shooter.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(shooter.AutoAimTargetId)
            || string.IsNullOrWhiteSpace(shooter.AutoAimPlateId))
        {
            return false;
        }

        target = world.Entities.FirstOrDefault(candidate =>
            candidate.IsAlive
            && !candidate.IsSimulationSuppressed
            && !string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Id, shooter.AutoAimTargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        plate = SimulationCombatMath.GetAttackableArmorPlateTargets(target, metersPerWorldUnit, world.GameTimeSec)
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
        if (!SimulationCombatMath.IsAutoAimArmorTargetEligible(world, shooter, target, plate, distanceM)
            || distanceM > maxDistanceM + 0.4
            || !IsArmorPlateFacingShooter(shooter, plate))
        {
            target = null;
            plate = default;
            return false;
        }

        return true;
    }

    private static bool IsArmorPlateFacingShooter(SimulationEntity shooter, ArmorPlateTarget plate)
    {
        if (plate.Id.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        double toShooterYawDeg = SimulationCombatMath.NormalizeDeg(Math.Atan2(shooter.Y - plate.Y, shooter.X - plate.X) * 180.0 / Math.PI);
        double facingError = Math.Abs(SimulationCombatMath.NormalizeSignedDeg(toShooterYawDeg - plate.YawDeg));
        return facingError <= 94.0;
    }

    private static bool HasRoughAutoAimCandidate(
        SimulationWorldState world,
        SimulationEntity shooter,
        double maxDistanceM)
    {
        double effectiveMaxDistanceM = SimulationCombatMath.IsHeroNormalAutoAimMode(shooter)
            ? Math.Min(maxDistanceM, SimulationCombatMath.HeroNormalAutoAimMaxDistanceM)
            : maxDistanceM;
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double maxDistanceWorld = Math.Max(0.0, effectiveMaxDistanceM) / metersPerWorldUnit;
        double maxDistanceSquared = (maxDistanceWorld + 1.5) * (maxDistanceWorld + 1.5);
        bool energyMode = string.Equals(shooter.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(shooter.RoleKey, "hero", StringComparison.OrdinalIgnoreCase);
        bool heroLobMode = SimulationCombatMath.IsHeroLobAutoAimMode(shooter);
        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || string.Equals(candidate.Id, shooter.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool energyCandidate = string.Equals(candidate.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase);
            if (energyMode)
            {
                if (!energyCandidate)
                {
                    continue;
                }
            }
            else
            {
                if (energyCandidate || string.Equals(candidate.Team, shooter.Team, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (heroLobMode && !SimulationCombatMath.IsHeroStructureAutoAimTargetEntity(world, shooter, candidate))
                {
                    continue;
                }

                if (SimulationCombatMath.IsHeroNormalAutoAimMode(shooter) && SimulationCombatMath.IsStructure(candidate))
                {
                    continue;
                }
            }

            double dx = candidate.X - shooter.X;
            double dy = candidate.Y - shooter.Y;
            double radius = candidate.CollisionRadiusWorld > 1e-6
                ? candidate.CollisionRadiusWorld
                : Math.Max(0.15, Math.Max(candidate.BodyLengthM, candidate.BodyWidthM) * 0.5);
            double allowed = maxDistanceWorld + radius + 0.4;
            if (dx * dx + dy * dy <= Math.Min(maxDistanceSquared, allowed * allowed))
            {
                return true;
            }
        }

        return false;
    }

    private void LogSlowAutoControlIfNeeded(
        SimulationEntity entity,
        string branch,
        long controlStartTicks,
        long navigationTicks,
        long autoAimTicks)
    {
        long totalTicks = System.Diagnostics.Stopwatch.GetTimestamp() - controlStartTicks;
        double totalMs = TicksToMs(totalTicks);
        if (totalMs < 4.0)
        {
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastSlowControlLogTicks > 0
            && (nowTicks - _lastSlowControlLogTicks) / (double)System.Diagnostics.Stopwatch.Frequency < 0.35)
        {
            return;
        }

        _lastSlowControlLogTicks = nowTicks;
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} "
            + $"entity={entity.Id} role={entity.RoleKey} team={entity.Team} "
            + $"branch={branch} total={totalMs:0.00}ms "
            + $"nav={TicksToMs(navigationTicks):0.00}ms "
            + $"aim={TicksToMs(autoAimTicks):0.00}ms "
            + $"block={entity.MotionBlockReason}";
        SimulatorRuntimeLog.Append("motion_control_slow.log", line);
    }

    private static void ApplyAutoAimSolution(
        SimulationWorldState world,
        SimulationEntity entity,
        SimulationEntity target,
        ArmorPlateTarget plate,
        AutoAimSolution solution,
        double dt)
    {
        string lockKey = $"{target.Id}:{plate.Id}";
        string targetKind = SimulationCombatMath.ResolveAutoAimTargetKind(target, plate);
        bool energyDiskTarget = string.Equals(targetKind, "energy_disk", StringComparison.OrdinalIgnoreCase);
        bool heroLobStructureTarget = entity.IsPlayerControlled
            && SimulationCombatMath.ShouldUseHeroLobStructureAxisAim(world, entity, target, plate);
        bool playerHardLock = entity.IsPlayerControlled && !entity.AutoAimGuidanceOnly;
        double desiredYaw = SimulationCombatMath.NormalizeDeg(solution.YawDeg);
        double desiredPitch = Math.Clamp(solution.PitchDeg, -40.0, 40.0);
        bool lockChanged = !string.Equals(entity.AutoAimLockKey, lockKey, StringComparison.OrdinalIgnoreCase);
        if (heroLobStructureTarget)
        {
            desiredYaw = SimulationCombatMath.NormalizeDeg(Math.Atan2(target.Y - entity.Y, target.X - entity.X) * 180.0 / Math.PI);
            desiredPitch = ResolveHeroLobHeldPitch(world, entity, plate, solution, lockKey, dt, desiredPitch);
        }
        else
        {
            ClearHeroLobPitchHold(entity);
        }

        bool sameTargetSwitch = lockChanged
            && string.Equals(entity.AutoAimTargetId, target.Id, StringComparison.OrdinalIgnoreCase);
        if (!entity.AutoAimHasSmoothedAim)
        {
            entity.AutoAimSmoothedYawDeg = desiredYaw;
            entity.AutoAimSmoothedPitchDeg = desiredPitch;
            entity.AutoAimHasSmoothedAim = true;
        }
        else
        {
            double yawDeadbandDeg = energyDiskTarget ? 0.10 : 0.05;
            double pitchDeadbandDeg = energyDiskTarget ? 0.08 : 0.04;
            if (playerHardLock)
            {
                yawDeadbandDeg *= energyDiskTarget ? 1.20 : (heroLobStructureTarget ? 1.45 : 1.62);
                pitchDeadbandDeg *= energyDiskTarget ? 1.16 : (heroLobStructureTarget ? 1.36 : 1.48);
            }

            desiredYaw = ApplyYawDeadbandDeg(entity.AutoAimSmoothedYawDeg, desiredYaw, yawDeadbandDeg);
            desiredPitch = ApplyScalarDeadband(entity.AutoAimSmoothedPitchDeg, desiredPitch, pitchDeadbandDeg);

            double tau = energyDiskTarget
                ? (lockChanged ? 0.060 : 0.085)
                : (lockChanged ? (sameTargetSwitch ? 0.095 : 0.165) : 0.135);
            double maxYawRateDegPerSec = energyDiskTarget
                ? 720.0
                : (heroLobStructureTarget ? 260.0 : 420.0);
            double maxPitchRateDegPerSec = energyDiskTarget
                ? 540.0
                : (heroLobStructureTarget ? 190.0 : 320.0);
            if (playerHardLock)
            {
                tau *= energyDiskTarget ? 1.10 : (heroLobStructureTarget ? 1.22 : 1.30);
                maxYawRateDegPerSec *= energyDiskTarget ? 0.94 : (heroLobStructureTarget ? 0.82 : 0.78);
                maxPitchRateDegPerSec *= energyDiskTarget ? 0.94 : (heroLobStructureTarget ? 0.84 : 0.80);
            }

            if (lockChanged)
            {
                maxYawRateDegPerSec *= sameTargetSwitch ? 1.18 : 1.34;
                maxPitchRateDegPerSec *= sameTargetSwitch ? 1.12 : 1.24;
            }

            double limitedYaw = RateLimitYawDeg(entity.AutoAimSmoothedYawDeg, desiredYaw, maxYawRateDegPerSec * Math.Clamp(dt, 0.005, 0.08));
            double limitedPitch = RateLimitScalar(entity.AutoAimSmoothedPitchDeg, desiredPitch, maxPitchRateDegPerSec * Math.Clamp(dt, 0.005, 0.08));
            double response = 1.0 - Math.Exp(-Math.Clamp(dt, 0.005, 0.08) / tau);
            entity.AutoAimSmoothedYawDeg = SmoothYawDeg(entity.AutoAimSmoothedYawDeg, limitedYaw, response);
            entity.AutoAimSmoothedPitchDeg = Lerp(entity.AutoAimSmoothedPitchDeg, limitedPitch, response);
        }

        if (lockChanged)
        {
            entity.AutoAimLastLockChangeTimeSec = world.GameTimeSec;
        }

        if (!entity.AutoAimGuidanceOnly)
        {
            double filteredPitchDeg = Math.Clamp(entity.GimbalPitchDeg, -40.0, 40.0);
            double outputDt = Math.Clamp(dt, 0.005, 0.08);
            double outputTau = energyDiskTarget ? 0.090 : (heroLobStructureTarget ? 0.110 : 0.075);
            double outputPitchRateDegPerSec = energyDiskTarget ? 210.0 : (heroLobStructureTarget ? 150.0 : 240.0);
            double outputYawRateDegPerSec = energyDiskTarget ? 240.0 : 300.0;
            if (playerHardLock)
            {
                outputTau *= energyDiskTarget ? 1.10 : (heroLobStructureTarget ? 1.20 : 1.28);
                outputPitchRateDegPerSec *= energyDiskTarget ? 0.94 : (heroLobStructureTarget ? 0.86 : 0.80);
                outputYawRateDegPerSec *= energyDiskTarget ? 0.94 : 0.80;
            }

            double outputResponse = 1.0 - Math.Exp(-outputDt / outputTau);
            double limitedPitchOutput = RateLimitScalar(filteredPitchDeg, entity.AutoAimSmoothedPitchDeg, outputPitchRateDegPerSec * outputDt);
            filteredPitchDeg = Math.Clamp(Lerp(filteredPitchDeg, limitedPitchOutput, outputResponse), -40.0, 40.0);

            if (heroLobStructureTarget)
            {
                double centerlineYawDeg = SimulationCombatMath.NormalizeDeg(Math.Atan2(target.Y - entity.Y, target.X - entity.X) * 180.0 / Math.PI);
                entity.TurretYawDeg = centerlineYawDeg;
                entity.GimbalPitchDeg = filteredPitchDeg;
            }
            else
            {
                double filteredYawDeg = SimulationCombatMath.NormalizeDeg(entity.TurretYawDeg);
                double limitedYawOutput = RateLimitYawDeg(filteredYawDeg, entity.AutoAimSmoothedYawDeg, outputYawRateDegPerSec * outputDt);
                filteredYawDeg = SmoothYawDeg(filteredYawDeg, limitedYawOutput, outputResponse);
                entity.TurretYawDeg = SimulationCombatMath.NormalizeDeg(filteredYawDeg);
                entity.GimbalPitchDeg = filteredPitchDeg;
            }
        }

        entity.AutoAimLocked = true;
        entity.AutoAimTargetId = target.Id;
        entity.AutoAimPlateId = plate.Id;
        entity.AutoAimTargetKind = targetKind;
        entity.AutoAimPlateDirection = solution.PlateDirection;
        entity.AutoAimAccuracy = solution.Accuracy;
        entity.AutoAimDistanceCoefficient = solution.DistanceCoefficient;
        entity.AutoAimMotionCoefficient = solution.MotionCoefficient;
        entity.AutoAimLeadTimeSec = solution.LeadTimeSec;
        entity.AutoAimLeadDistanceM = solution.LeadDistanceM;
        entity.AutoAimAimPointX = solution.AimPointX;
        entity.AutoAimAimPointY = solution.AimPointY;
        entity.AutoAimAimPointHeightM = solution.AimPointHeightM;
        entity.AutoAimObservedVelocityXMps = solution.ObservedVelocityXMps;
        entity.AutoAimObservedVelocityYMps = solution.ObservedVelocityYMps;
        entity.AutoAimObservedVelocityZMps = solution.ObservedVelocityZMps;
        entity.AutoAimObservedAngularVelocityRadPerSec = solution.ObservedAngularVelocityRadPerSec;
        entity.AutoAimLockKey = lockKey;
    }

    private static double ResolveHeroLobHeldPitch(
        SimulationWorldState world,
        SimulationEntity entity,
        ArmorPlateTarget plate,
        AutoAimSolution solution,
        string lockKey,
        double dt,
        double solvedPitchDeg)
    {
        double targetHeightM = solution.AimPointHeightM > 1e-6
            ? solution.AimPointHeightM
            : plate.HeightM;
        bool resetHold = !string.Equals(entity.HeroLobPitchHoldLockKey, lockKey, StringComparison.OrdinalIgnoreCase)
            || !double.IsFinite(entity.HeroLobPitchHoldDeg)
            || Math.Abs(entity.HeroLobPitchHoldDeg) > 45.0;
        if (resetHold)
        {
            entity.HeroLobPitchHoldLockKey = lockKey;
            entity.HeroLobPitchHoldDeg = Math.Clamp(solvedPitchDeg, -40.0, 40.0);
            entity.HeroLobPitchHoldTargetHeightM = targetHeightM;
            return entity.HeroLobPitchHoldDeg;
        }

        double heldPitchDeg = Math.Clamp(entity.HeroLobPitchHoldDeg, -40.0, 40.0);
        double heightErrorM = SimulationCombatMath.EstimateProjectileHeightErrorAtPoint(
            world,
            entity,
            solution.AimPointX,
            solution.AimPointY,
            targetHeightM,
            heldPitchDeg);
        double plateHeightM = plate.HeightSpanM > 1e-6
            ? plate.HeightSpanM
            : Math.Max(0.10, plate.SideLengthM);
        double correctionToleranceM = Math.Clamp(Math.Max(0.14, plateHeightM * 0.72), 0.14, 0.24);
        if (!double.IsFinite(heightErrorM) || Math.Abs(heightErrorM) > correctionToleranceM)
        {
            double outputDt = Math.Clamp(dt, 0.005, 0.08);
            double correctionRateDegPerSec = double.IsFinite(heightErrorM)
                && Math.Abs(heightErrorM) > correctionToleranceM * 2.2
                    ? 34.0
                    : 14.0;
            heldPitchDeg = RateLimitScalar(heldPitchDeg, solvedPitchDeg, correctionRateDegPerSec * outputDt);
        }

        entity.HeroLobPitchHoldLockKey = lockKey;
        entity.HeroLobPitchHoldDeg = Math.Clamp(heldPitchDeg, -40.0, 40.0);
        entity.HeroLobPitchHoldTargetHeightM = targetHeightM;
        return entity.HeroLobPitchHoldDeg;
    }

    private static void ClearHeroLobPitchHold(SimulationEntity entity)
    {
        entity.HeroLobPitchHoldLockKey = null;
        entity.HeroLobPitchHoldDeg = 0.0;
        entity.HeroLobPitchHoldTargetHeightM = 0.0;
    }

    private static bool IsHeroLobStructureTargetKind(string? targetKind)
    {
        return string.Equals(targetKind, "outpost_armor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetKind, "base_armor", StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearAutoAimState(SimulationEntity entity)
    {
        entity.AutoAimLocked = false;
        entity.AutoAimTargetId = null;
        entity.AutoAimPlateId = null;
        entity.AutoAimTargetKind = string.Equals(entity.AutoAimTargetMode, "energy", StringComparison.OrdinalIgnoreCase)
            ? "energy_disk"
            : "vehicle_armor";
        entity.AutoAimPlateDirection = string.Empty;
        entity.AutoAimAccuracy = 0.0;
        entity.AutoAimDistanceCoefficient = 0.0;
        entity.AutoAimMotionCoefficient = 0.0;
        entity.AutoAimLeadTimeSec = 0.0;
        entity.AutoAimLeadDistanceM = 0.0;
        entity.AutoAimAimPointX = 0.0;
        entity.AutoAimAimPointY = 0.0;
        entity.AutoAimAimPointHeightM = 0.0;
        entity.AutoAimObservedVelocityXMps = 0.0;
        entity.AutoAimObservedVelocityYMps = 0.0;
        entity.AutoAimObservedVelocityZMps = 0.0;
        entity.AutoAimObservedAngularVelocityRadPerSec = 0.0;
        entity.AutoAimHasSmoothedAim = false;
        entity.AutoAimLockKey = null;
        ClearHeroLobPitchHold(entity);
    }

    private static double SmoothYawDeg(double currentYawDeg, double targetYawDeg, double amount)
    {
        double delta = SimulationCombatMath.NormalizeSignedDeg(targetYawDeg - currentYawDeg);
        return SimulationCombatMath.NormalizeDeg(currentYawDeg + delta * Math.Clamp(amount, 0.0, 1.0));
    }

    private static double RateLimitYawDeg(double currentYawDeg, double targetYawDeg, double maxStepDeg)
    {
        double delta = SimulationCombatMath.NormalizeSignedDeg(targetYawDeg - currentYawDeg);
        if (Math.Abs(delta) <= Math.Max(1e-6, maxStepDeg))
        {
            return SimulationCombatMath.NormalizeDeg(targetYawDeg);
        }

        return SimulationCombatMath.NormalizeDeg(currentYawDeg + Math.Sign(delta) * Math.Max(1e-6, maxStepDeg));
    }

    private static double RateLimitScalar(double currentValue, double targetValue, double maxStep)
    {
        double delta = targetValue - currentValue;
        if (Math.Abs(delta) <= Math.Max(1e-6, maxStep))
        {
            return targetValue;
        }

        return currentValue + Math.Sign(delta) * Math.Max(1e-6, maxStep);
    }

    private static double ApplyYawDeadbandDeg(double currentYawDeg, double targetYawDeg, double deadbandDeg)
    {
        double delta = SimulationCombatMath.NormalizeSignedDeg(targetYawDeg - currentYawDeg);
        if (Math.Abs(delta) <= Math.Max(0.0, deadbandDeg))
        {
            return currentYawDeg;
        }

        return targetYawDeg;
    }

    private static double ApplyScalarDeadband(double currentValue, double targetValue, double deadband)
    {
        return Math.Abs(targetValue - currentValue) <= Math.Max(0.0, deadband)
            ? currentValue
            : targetValue;
    }

    private static void ApplyDriveControl(
        SimulationWorldState world,
        SimulationEntity entity,
        double moveForward,
        double moveRight,
        double dt,
        double? driveYawDegOverride = null)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        moveForward = Math.Clamp(moveForward, -1.25, 1.25);
        moveRight = Math.Clamp(moveRight, -1.25, 1.25);
        entity.MoveInputForward = moveForward;
        entity.MoveInputRight = moveRight;
        double yawRad = DegreesToRadians(driveYawDegOverride ?? entity.AngleDeg);
        double currentVxMps = entity.VelocityXWorldPerSec * metersPerWorldUnit;
        double currentVyMps = entity.VelocityYWorldPerSec * metersPerWorldUnit;
        double currentSpeedMps = Math.Sqrt(currentVxMps * currentVxMps + currentVyMps * currentVyMps);

        double physicalDrivePowerLimitW = ResolvePhysicalDrivePowerLimitW(entity);
        double drivePowerLimitW = Math.Min(ResolveEffectiveDrivePowerLimitW(entity), physicalDrivePowerLimitW);
        if (entity.PowerCutTimerSec > 1e-6)
        {
            double powerlessDrag = Math.Exp(-7.0 * dt);
            entity.VelocityXWorldPerSec *= powerlessDrag;
            entity.VelocityYWorldPerSec *= powerlessDrag;
            entity.ChassisPowerDrawW = 0.0;
            entity.EffectiveDrivePowerLimitW = drivePowerLimitW;
            entity.ChassisPowerRatio = 0.0;
            entity.ChassisSpeedLimitMps = 0.0;
            entity.MotionBlockReason = "power_cut";
            return;
        }

        if (entity.AirborneHeightM > 1e-4)
        {
            ApplyAirborneInertia(entity, drivePowerLimitW, currentVxMps, currentVyMps, currentSpeedMps, metersPerWorldUnit, dt);
            return;
        }

        double nominalSpeedLimitMps = ClampWheelLinearSpeedMps(ResolveMoveSpeedMps(entity, drivePowerLimitW) * Math.Max(0.10, entity.ChassisSpeedScale));

        double forwardX = Math.Cos(yawRad);
        double forwardY = Math.Sin(yawRad);
        double rightX = Math.Cos(yawRad + Math.PI * 0.5);
        double rightY = Math.Sin(yawRad + Math.PI * 0.5);
        if (entity.SmallGyroActive && IsBalanceInfantry(entity))
        {
            moveForward = 0.0;
            moveRight = 0.0;
        }

        double desiredVxMps = (forwardX * moveForward + rightX * moveRight) * nominalSpeedLimitMps;
        double desiredVyMps = (forwardY * moveForward + rightY * moveRight) * nominalSpeedLimitMps;

        double moveMagnitude = Math.Sqrt(moveForward * moveForward + moveRight * moveRight);
        double desiredSpeedMps = Math.Sqrt(desiredVxMps * desiredVxMps + desiredVyMps * desiredVyMps);
        double directionDot = 1.0;
        if (currentSpeedMps > 0.06 && desiredSpeedMps > 0.06)
        {
            directionDot = (currentVxMps * desiredVxMps + currentVyMps * desiredVyMps)
                / Math.Max(1e-6, currentSpeedMps * desiredSpeedMps);
        }

        bool reversingCommand = currentSpeedMps > 0.12
            && desiredSpeedMps > 0.05
            && directionDot < -0.18;
        bool brakingToStop = moveMagnitude <= 0.015 && currentSpeedMps > 0.06;
        bool brakingHard = (brakingToStop || reversingCommand) && currentSpeedMps > 0.08;
        DriveMotorModel driveMotorModel = ResolveDriveMotorModel(entity);
        bool activeSuperCapDrive =
            entity.SuperCapEnabled
            && entity.SuperCapEnergyJ > driveMotorModel.CapReserveJ + 1e-6;
        bool allowOverPowerAssist = activeSuperCapDrive
            || brakingHard;
        double activeSuperCapAssistW = ResolveSuperCapAssistLimitW(entity, dt, drivePowerLimitW, allowForcedAssist: brakingHard);
        bool smallGyroTranslationActive = HasSmallGyroTranslationInput(entity, moveForward, moveRight);
        double powerSplit = smallGyroTranslationActive
            ? 0.50
            : entity.SmallGyroActive
                ? 0.0
                : 1.0;
        double translationSuperCapAssistW = activeSuperCapAssistW * powerSplit;
        double bufferHeadroomW = allowOverPowerAssist ? ResolveBufferAssistLimitW(entity, dt) : 0.0;
        double uncappedAvailableDrivePowerLimitW = allowOverPowerAssist
            ? drivePowerLimitW + bufferHeadroomW + (activeSuperCapDrive ? activeSuperCapAssistW : 0.0)
            : drivePowerLimitW;
        double totalAvailableDrivePowerLimitW = Math.Min(physicalDrivePowerLimitW, uncappedAvailableDrivePowerLimitW);
        double translationDrivePowerLimitW = Math.Max(1.0, drivePowerLimitW * Math.Max(powerSplit, 0.10));
        double availableDrivePowerLimitW = Math.Max(
            1.0,
            totalAvailableDrivePowerLimitW * powerSplit);
        entity.EffectiveDrivePowerLimitW = totalAvailableDrivePowerLimitW;
        double storedPowerRatio = availableDrivePowerLimitW <= 1e-6 ? 0.35 : Math.Clamp(availableDrivePowerLimitW / Math.Max(1.0, drivePowerLimitW), 0.35, 2.0);
        double smallGyroTranslationSpeedBoost = smallGyroTranslationActive
            ? ResolveSmallGyroTranslationSpeedBoost(entity)
            : 1.0;
        double speedLimitMps = ClampWheelLinearSpeedMps(ResolveMoveSpeedMps(entity, availableDrivePowerLimitW) * Math.Max(0.10, entity.ChassisSpeedScale) * Math.Min(storedPowerRatio, 1.65) * smallGyroTranslationSpeedBoost);
        entity.ChassisSpeedLimitMps = speedLimitMps;
        entity.ChassisPowerRatio = storedPowerRatio;
        desiredVxMps = (forwardX * moveForward + rightX * moveRight) * speedLimitMps;
        desiredVyMps = (forwardY * moveForward + rightY * moveRight) * speedLimitMps;
        desiredSpeedMps = Math.Sqrt(desiredVxMps * desiredVxMps + desiredVyMps * desiredVyMps);
        bool accelerationTrend =
            !brakingHard
            && moveMagnitude > 0.05
            && desiredSpeedMps > currentSpeedMps + 0.08;
        double directMotorAssistRatio = accelerationTrend
            ? Math.Clamp(translationSuperCapAssistW / Math.Max(1.0, drivePowerLimitW), 0.0, 1.35)
            : 0.0;
        double accelLimit = ResolveAccelerationLimitMps2(
                entity,
                availableDrivePowerLimitW,
                currentSpeedMps,
                moveMagnitude,
                directMotorAssistRatio,
                accelerationTrend)
            * (0.60 + storedPowerRatio * 0.40);

        if (brakingHard)
        {
            double brakeAccelLimit = accelLimit * (reversingCommand ? 3.10 : brakingToStop ? 3.65 : 2.20);
            double brakeDeltaSpeed = Math.Min(currentSpeedMps, brakeAccelLimit * dt);
            if (currentSpeedMps > 1e-6)
            {
                double brakeDirX = currentVxMps / currentSpeedMps;
                double brakeDirY = currentVyMps / currentSpeedMps;
                desiredVxMps = currentVxMps - brakeDirX * brakeDeltaSpeed;
                desiredVyMps = currentVyMps - brakeDirY * brakeDeltaSpeed;
                if (reversingCommand || brakingToStop)
                {
                    double reverseBias = brakingToStop ? 0.34 : 0.24;
                    desiredVxMps += (desiredVxMps >= 0.0 ? -1.0 : 1.0) * Math.Min(Math.Abs(desiredVxMps) * reverseBias, speedLimitMps * 0.20);
                    desiredVyMps += (desiredVyMps >= 0.0 ? -1.0 : 1.0) * Math.Min(Math.Abs(desiredVyMps) * reverseBias, speedLimitMps * 0.20);
                }
            }
        }

        double wheelResponseTimeSec = brakingToStop
            ? Math.Min(ResolveWheelResponseTimeSec(entity, moveMagnitude, directMotorAssistRatio, accelerationTrend), 0.040)
            : ResolveWheelResponseTimeSec(entity, moveMagnitude, directMotorAssistRatio, accelerationTrend);
        double response = activeSuperCapDrive
            ? 1.0
            : 1.0 - Math.Exp(-dt / Math.Max(0.020, wheelResponseTimeSec));
        double responseVxMps = currentVxMps + (desiredVxMps - currentVxMps) * response;
        double responseVyMps = currentVyMps + (desiredVyMps - currentVyMps) * response;
        double dvx = responseVxMps - currentVxMps;
        double dvy = responseVyMps - currentVyMps;
        double requestedAccelMps2 = Math.Sqrt(dvx * dvx + dvy * dvy) / Math.Max(dt, 1e-6);
        double maxDelta = activeSuperCapDrive
            ? accelLimit * dt
            : Math.Min(accelLimit, brakingHard ? accelLimit : WheelAccelerationRampLimitMps2) * dt;
        double deltaMagnitude = Math.Sqrt(dvx * dvx + dvy * dvy);
        if (deltaMagnitude > maxDelta && deltaMagnitude > 1e-6)
        {
            double scale = maxDelta / deltaMagnitude;
            dvx *= scale;
            dvy *= scale;
        }

        double newVxMps = currentVxMps + dvx;
        double newVyMps = currentVyMps + dvy;
        double dragPerSec = moveMagnitude <= 0.05
            ? ResolveBrakeDragPerSec(entity)
            : ResolveRollingDragPerSec(entity);
        if (reversingCommand || brakingToStop)
        {
            double brakeMultiplier = brakingToStop ? 2.35 : 1.38;
            dragPerSec = Math.Max(dragPerSec, ResolveBrakeDragPerSec(entity) * brakeMultiplier);
        }

        double dragScale = Math.Exp(-dragPerSec * dt);
        newVxMps *= dragScale;
        newVyMps *= dragScale;
        double newSpeedMps = Math.Sqrt(newVxMps * newVxMps + newVyMps * newVyMps);
        if (brakingHard && newSpeedMps <= (brakingToStop ? 0.18 : 0.12))
        {
            newVxMps = 0.0;
            newVyMps = 0.0;
            newSpeedMps = 0.0;
        }
        else if (moveMagnitude <= 0.03)
        {
            if (Math.Abs(newVxMps) <= 0.018)
            {
                newVxMps = 0.0;
            }

            if (Math.Abs(newVyMps) <= 0.018)
            {
                newVyMps = 0.0;
            }

            newSpeedMps = Math.Sqrt(newVxMps * newVxMps + newVyMps * newVyMps);
        }

        if (newSpeedMps > speedLimitMps && newSpeedMps > 1e-6)
        {
            double scale = speedLimitMps / newSpeedMps;
            newVxMps *= scale;
            newVyMps *= scale;
            newSpeedMps = speedLimitMps;
        }

        double appliedAccelMps2 = Math.Sqrt((newVxMps - currentVxMps) * (newVxMps - currentVxMps)
            + (newVyMps - currentVyMps) * (newVyMps - currentVyMps)) / Math.Max(dt, 1e-6);
        double requestedPowerW = EstimateChassisPowerDrawW(entity, currentSpeedMps, newSpeedMps, appliedAccelMps2, moveMagnitude, dt, brakingHard, reversingCommand);
        if (moveMagnitude > 0.05
            && !brakingHard
            && requestedPowerW < availableDrivePowerLimitW * 0.985
            && newSpeedMps > 1e-4)
        {
            double baseDeltaVxMps = newVxMps - currentVxMps;
            double baseDeltaVyMps = newVyMps - currentVyMps;
            double bestScale = 1.0;
            double lowScale = 1.0;
            double highScale = 2.0;
            double maxBoostedSpeedMps = ClampWheelLinearSpeedMps(Math.Max(speedLimitMps, newSpeedMps) * 1.999);
            for (int iteration = 0; iteration < 9; iteration++)
            {
                double testScale = (lowScale + highScale) * 0.5;
                double testVxMps = currentVxMps + baseDeltaVxMps * testScale;
                double testVyMps = currentVyMps + baseDeltaVyMps * testScale;
                double testSpeedMps = Math.Sqrt(testVxMps * testVxMps + testVyMps * testVyMps);
                if (testSpeedMps > maxBoostedSpeedMps && testSpeedMps > 1e-6)
                {
                    double clamp = maxBoostedSpeedMps / testSpeedMps;
                    testVxMps *= clamp;
                    testVyMps *= clamp;
                    testSpeedMps = maxBoostedSpeedMps;
                }

                double testAccelMps2 = Math.Sqrt((testVxMps - currentVxMps) * (testVxMps - currentVxMps)
                    + (testVyMps - currentVyMps) * (testVyMps - currentVyMps)) / Math.Max(dt, 1e-6);
                double testPowerW = EstimateChassisPowerDrawW(entity, currentSpeedMps, testSpeedMps, testAccelMps2, moveMagnitude, dt, brakingHard, reversingCommand);
                if (testPowerW <= availableDrivePowerLimitW * 1.000)
                {
                    bestScale = testScale;
                    lowScale = testScale;
                }
                else
                {
                    highScale = testScale;
                }
            }

            if (bestScale > 1.003)
            {
                newVxMps = currentVxMps + baseDeltaVxMps * bestScale;
                newVyMps = currentVyMps + baseDeltaVyMps * bestScale;
                newSpeedMps = Math.Sqrt(newVxMps * newVxMps + newVyMps * newVyMps);
                if (newSpeedMps > maxBoostedSpeedMps && newSpeedMps > 1e-6)
                {
                    double clamp = maxBoostedSpeedMps / newSpeedMps;
                    newVxMps *= clamp;
                    newVyMps *= clamp;
                    newSpeedMps = maxBoostedSpeedMps;
                }

                appliedAccelMps2 = Math.Sqrt((newVxMps - currentVxMps) * (newVxMps - currentVxMps)
                    + (newVyMps - currentVyMps) * (newVyMps - currentVyMps)) / Math.Max(dt, 1e-6);
                requestedPowerW = EstimateChassisPowerDrawW(entity, currentSpeedMps, newSpeedMps, appliedAccelMps2, moveMagnitude, dt, brakingHard, reversingCommand);
            }
        }

        (double powerDrawW, double bufferUseW, double superCapUseW, bool overPowerFault) = ResolveDrivePowerAllocation(entity, requestedPowerW, translationDrivePowerLimitW, translationSuperCapAssistW, dt, allowOverPowerAssist);
        if (requestedPowerW > 1e-6 && powerDrawW + 1e-6 < requestedPowerW)
        {
            double bestScale = 0.0;
            double lowScale = 0.0;
            double highScale = 1.0;
            double baseDeltaVxMps = newVxMps - currentVxMps;
            double baseDeltaVyMps = newVyMps - currentVyMps;
            for (int iteration = 0; iteration < 12; iteration++)
            {
                double testScale = (lowScale + highScale) * 0.5;
                double testVxMps = currentVxMps + baseDeltaVxMps * testScale;
                double testVyMps = currentVyMps + baseDeltaVyMps * testScale;
                double testSpeedMps = Math.Sqrt(testVxMps * testVxMps + testVyMps * testVyMps);
                double testAccelMps2 = Math.Sqrt((testVxMps - currentVxMps) * (testVxMps - currentVxMps)
                    + (testVyMps - currentVyMps) * (testVyMps - currentVyMps)) / Math.Max(dt, 1e-6);
                double testPowerW = EstimateChassisPowerDrawW(entity, currentSpeedMps, testSpeedMps, testAccelMps2, moveMagnitude, dt, brakingHard, reversingCommand);
                var testAllocation = ResolveDrivePowerAllocation(entity, testPowerW, translationDrivePowerLimitW, translationSuperCapAssistW, dt, allowOverPowerAssist);
                if (testPowerW <= testAllocation.PowerDrawW + 1e-6)
                {
                    bestScale = testScale;
                    lowScale = testScale;
                }
                else
                {
                    highScale = testScale;
                }
            }

            newVxMps = currentVxMps + baseDeltaVxMps * bestScale;
            newVyMps = currentVyMps + baseDeltaVyMps * bestScale;
            newSpeedMps = Math.Sqrt(newVxMps * newVxMps + newVyMps * newVyMps);
            appliedAccelMps2 = Math.Sqrt((newVxMps - currentVxMps) * (newVxMps - currentVxMps)
                + (newVyMps - currentVyMps) * (newVyMps - currentVyMps)) / Math.Max(dt, 1e-6);
            requestedPowerW = EstimateChassisPowerDrawW(entity, currentSpeedMps, newSpeedMps, appliedAccelMps2, moveMagnitude, dt, brakingHard, reversingCommand);
            (powerDrawW, bufferUseW, superCapUseW, overPowerFault) = ResolveDrivePowerAllocation(entity, requestedPowerW, translationDrivePowerLimitW, translationSuperCapAssistW, dt, allowOverPowerAssist);
        }

        if (overPowerFault)
        {
            TriggerOverPowerCut(entity);
            entity.VelocityXWorldPerSec = 0.0;
            entity.VelocityYWorldPerSec = 0.0;
            entity.ChassisRpm = 0.0;
            return;
        }

        if (bufferUseW > 1e-6)
        {
            entity.BufferEnergyJ = Math.Max(0.0, entity.BufferEnergyJ - bufferUseW * dt);
            entity.CurrentFrameBufferDrawW += bufferUseW;
        }

        if (superCapUseW > 1e-6)
        {
            entity.SuperCapEnergyJ = Math.Max(driveMotorModel.CapReserveJ, entity.SuperCapEnergyJ - superCapUseW * dt);
            entity.CurrentFrameSuperCapDrawW += superCapUseW;
        }

        double refereePowerDrawW = Math.Max(0.0, powerDrawW - superCapUseW);
        RechargeStoredDriveEnergy(entity, drivePowerLimitW, refereePowerDrawW, dt);

        entity.VelocityXWorldPerSec = newVxMps / metersPerWorldUnit;
        entity.VelocityYWorldPerSec = newVyMps / metersPerWorldUnit;
        entity.ChassisRpm = newSpeedMps / Math.Max(entity.WheelRadiusM, 0.03) * 9.55;
        entity.ChassisPowerDrawW = powerDrawW;
        entity.MotionBlockReason = string.Empty;
        if (entity.MaxChassisEnergy > 1e-6)
        {
            entity.ChassisEnergy = Math.Max(0.0, entity.ChassisEnergy - refereePowerDrawW * dt);
        }
    }

    private static double EstimateChassisPowerDrawW(
        SimulationEntity entity,
        double currentSpeedMps,
        double speedMps,
        double appliedAccelMps2,
        double moveMagnitude,
        double dt,
        bool brakingHard,
        bool reversingCommand)
    {
        DriveMotorModel model = ResolveDriveMotorModel(entity);
        double wheelRadiusM = Math.Clamp(entity.WheelRadiusM <= 1e-6 ? 0.08 : entity.WheelRadiusM, 0.055, 0.12);
        double wheelCount = Math.Max(1.0, model.WheelCount);
        double massKg = Math.Clamp(entity.MassKg <= 1e-6 ? 20.0 : entity.MassKg, 15.0, 25.0);
        double rollingResistanceN = ResolveRollingResistanceForceN(entity);
        double currentOmega = currentSpeedMps / Math.Max(0.01, wheelRadiusM);
        double targetOmega = speedMps / Math.Max(0.01, wheelRadiusM);
        double accelForceN = massKg * appliedAccelMps2;
        double requestedForceN = accelForceN + (moveMagnitude > 0.03 ? rollingResistanceN : rollingResistanceN * 0.15);
        double torquePerWheelNm = Math.Abs(requestedForceN) * wheelRadiusM / wheelCount;
        double brakingOmegaError = brakingHard ? Math.Abs(currentOmega - targetOmega) : 0.0;
        if (brakingHard)
        {
            double brakingTorqueNm = Math.Min(
                model.PeakTorqueNm,
                Math.Max(model.ContinuousTorqueNm, brakingOmegaError * model.MechanicalTimeConstantSec * 0.18));
            torquePerWheelNm = Math.Max(torquePerWheelNm, brakingTorqueNm);
        }

        double torqueLimitNm = moveMagnitude > 0.08 ? model.PeakTorqueNm : model.ContinuousTorqueNm;
        if (brakingHard)
        {
            torqueLimitNm = model.PeakTorqueNm;
        }

        torquePerWheelNm = Math.Clamp(torquePerWheelNm, 0.0, torqueLimitNm);

        // Keep the chassis motor power model aligned with the supplied PowerCtrl.hpp:
        // I_cmd = torqueSet / 6 * 20, then P = k1*w*I + R*I^2 + k2*w^2 + static_power.
        double commandedCurrentA = torquePerWheelNm / 6.0 * 20.0;
        if (brakingHard)
        {
            commandedCurrentA *= reversingCommand ? 1.55 : 1.30;
        }

        double omegaForPower = brakingHard ? currentOmega : Math.Max(currentOmega, targetOmega * 0.72);
        double item1 = wheelCount * omegaForPower * commandedCurrentA;
        double item2 = wheelCount * model.PhaseResistanceOhm * commandedCurrentA * commandedCurrentA;
        double item3 = wheelCount * model.DampingCoeff * omegaForPower * omegaForPower;
        double powerCtrlPredictW = model.K1 * item1 + item2 + item3 + model.StaticPowerW;
        if (moveMagnitude <= 0.03 && currentSpeedMps <= 0.025 && !brakingHard)
        {
            return model.StaticPowerW;
        }

        double physicalFloorW = model.StaticPowerW
            + Math.Max(0.0, massKg * appliedAccelMps2 * Math.Max(speedMps, 0.18) * 0.22)
            + (brakingHard ? Math.Min(70.0, 8.0 + brakingOmegaError * wheelCount * 0.10) : 0.0);
        return Math.Max(model.StaticPowerW, Math.Max(powerCtrlPredictW, physicalFloorW));
    }

    private static double EstimateTurnPowerDrawW(
        SimulationEntity entity,
        double yawRateDegPerSec,
        bool smallGyroRequested)
    {
        double yawRateAbs = Math.Abs(yawRateDegPerSec);
        if (yawRateAbs <= 1e-6)
        {
            return 0.0;
        }

        if (!smallGyroRequested)
        {
            return Math.Max(0.0, 0.00032 * yawRateAbs * yawRateAbs);
        }

        if (TryResolveStandardGyroPowerForYawRate(entity, yawRateAbs, out double mappedStandardPowerW))
        {
            return mappedStandardPowerW;
        }

        double yawRateRadPerSec = yawRateAbs * Math.PI / 180.0;
        double equivalentWheelSpeedMps = ResolveSmallGyroEquivalentWheelSpeedMps(entity, yawRateRadPerSec);
        double mappedPowerW = EstimateMappedSteadyPowerW(entity, equivalentWheelSpeedMps);
        return Math.Max(0.0, mappedPowerW * ResolveSmallGyroPowerLossMultiplier(entity));
    }

    private static bool ShouldUsePassiveSuperCapAssist(SimulationEntity entity)
    {
        if (entity.MaxSuperCapEnergyJ <= 1e-6 || entity.SuperCapEnergyJ <= 1e-6)
        {
            return false;
        }

        double switchThresholdJ = Math.Min(
            Math.Max(0.0, entity.MaxBufferEnergyJ),
            Math.Max(BufferSuperCapSwitchEnergyJ, entity.BufferReserveEnergyJ));
        return entity.BufferEnergyJ <= switchThresholdJ + 1e-6;
    }

    private static (double PowerDrawW, double BufferUseW, double SuperCapUseW, bool OverPowerFault) ResolveDrivePowerAllocation(
        SimulationEntity entity,
        double requestedPowerW,
        double drivePowerLimitW,
        double activeSuperCapAssistW,
        double dt,
        bool allowOverPowerAssist)
    {
        double clampedRequestedW = Math.Max(0.0, requestedPowerW);
        if (!allowOverPowerAssist)
        {
            return (Math.Min(clampedRequestedW, drivePowerLimitW), 0.0, 0.0, false);
        }

        DriveMotorModel model = ResolveDriveMotorModel(entity);
        double bufferSwitchJ = Math.Min(
            Math.Max(0.0, entity.MaxBufferEnergyJ),
            Math.Max(BufferSuperCapSwitchEnergyJ, entity.BufferReserveEnergyJ));
        double usableSuperCapJ = Math.Max(0.0, entity.SuperCapEnergyJ - model.CapReserveJ);
        double overPowerW = Math.Max(0.0, clampedRequestedW - drivePowerLimitW);
        double bufferUseW = 0.0;
        double highPriorityBufferJ = Math.Max(0.0, entity.BufferEnergyJ - bufferSwitchJ);
        bool forcedSuperCapAssist = activeSuperCapAssistW > 1e-6 && usableSuperCapJ > 1e-6;
        bool allowSuperCapAssist = entity.SuperCapEnabled || forcedSuperCapAssist || ShouldUsePassiveSuperCapAssist(entity);
        bool prioritizeSuperCap = forcedSuperCapAssist || (entity.SuperCapEnabled && activeSuperCapAssistW > 1e-6 && usableSuperCapJ > 1e-6);
        double superCapUseW = 0.0;
        if (prioritizeSuperCap)
        {
            superCapUseW = Math.Min(
                Math.Max(0.0, activeSuperCapAssistW),
                Math.Min(model.SuperCapDischargeLimitW, usableSuperCapJ / Math.Max(dt, 1e-6)));
            overPowerW = Math.Max(0.0, overPowerW - superCapUseW);
        }

        if (overPowerW > 1e-6 && prioritizeSuperCap && allowSuperCapAssist)
        {
            double superCapBudgetW = Math.Min(
                Math.Max(0.0, activeSuperCapAssistW),
                Math.Min(model.SuperCapDischargeLimitW, usableSuperCapJ / Math.Max(dt, 1e-6)));
            double extraSuperCapUseW = Math.Min(overPowerW, Math.Max(0.0, superCapBudgetW - superCapUseW));
            superCapUseW += extraSuperCapUseW;
            overPowerW -= extraSuperCapUseW;
        }

        if (overPowerW > 1e-6)
        {
            double bufferBoostW = highPriorityBufferJ * model.BufferAssistGain;
            double highPriorityBufferUseW = Math.Min(overPowerW, Math.Min(bufferBoostW, highPriorityBufferJ / Math.Max(dt, 1e-6)));
            bufferUseW += highPriorityBufferUseW;
            overPowerW -= highPriorityBufferUseW;
        }

        if (overPowerW > 1e-6 && !prioritizeSuperCap && allowSuperCapAssist)
        {
            double superCapBudgetW = Math.Min(
                Math.Max(0.0, activeSuperCapAssistW),
                Math.Min(model.SuperCapDischargeLimitW, usableSuperCapJ / Math.Max(dt, 1e-6)));
            double deferredSuperCapUseW = Math.Min(overPowerW, superCapBudgetW);
            superCapUseW += deferredSuperCapUseW;
            overPowerW -= deferredSuperCapUseW;
        }

        if (overPowerW > 1e-6)
        {
            double emergencyBufferJ = Math.Max(0.0, Math.Min(entity.BufferEnergyJ, bufferSwitchJ));
            double emergencyBufferUseW = Math.Min(overPowerW, emergencyBufferJ / Math.Max(dt, 1e-6));
            bufferUseW += emergencyBufferUseW;
            overPowerW -= emergencyBufferUseW;
        }

        bool overPowerFault = overPowerW > 1e-3 && entity.BufferEnergyJ <= 1e-3 + Math.Min(1.0, bufferSwitchJ * 0.05);
        double forcedInjectedPowerW = superCapUseW > 1e-6 || bufferUseW > 1e-6 ? drivePowerLimitW + bufferUseW + superCapUseW : clampedRequestedW;
        double suppliedPowerW = Math.Min(
            Math.Max(clampedRequestedW, forcedInjectedPowerW),
            drivePowerLimitW + bufferUseW + superCapUseW);
        return (suppliedPowerW, bufferUseW, superCapUseW, overPowerFault);
    }

    private static void RaiseChassisPowerDrawTo(
        SimulationEntity entity,
        double targetPowerW,
        double dt,
        bool allowOverPowerAssist = true,
        bool preferSuperCapFirst = false)
    {
        if (dt <= 1e-6)
        {
            return;
        }

        double physicalPowerLimitW = ResolvePhysicalDrivePowerLimitW(entity);
        double currentPowerW = Math.Max(0.0, entity.ChassisPowerDrawW);
        double targetW = Math.Max(currentPowerW, Math.Min(targetPowerW, physicalPowerLimitW));
        double missingW = targetW - currentPowerW;
        if (missingW <= 1e-6)
        {
            return;
        }

        double drivePowerLimitW = ResolveEffectiveDrivePowerLimitW(entity);
        DriveMotorModel model = ResolveDriveMotorModel(entity);
        double bufferSwitchJ = Math.Min(
            Math.Max(0.0, entity.MaxBufferEnergyJ),
            Math.Max(BufferSuperCapSwitchEnergyJ, entity.BufferReserveEnergyJ));
        double usableSuperCapJ = Math.Max(0.0, entity.SuperCapEnergyJ - model.CapReserveJ);
        bool prioritizeSuperCap = entity.SuperCapEnabled && usableSuperCapJ > 1e-6;
        double standardHeadroomW = Math.Max(0.0, drivePowerLimitW - currentPowerW);
        double standardUseW = 0.0;
        double bufferUseW = 0.0;
        double superCapUseW = 0.0;

        if (allowOverPowerAssist && preferSuperCapFirst && prioritizeSuperCap && missingW > 1e-6)
        {
            double remainingSuperCapLimitW = Math.Max(0.0, model.SuperCapDischargeLimitW - entity.CurrentFrameSuperCapDrawW);
            superCapUseW = Math.Min(
                missingW,
                Math.Min(remainingSuperCapLimitW, usableSuperCapJ / dt));
            missingW -= superCapUseW;
        }

        standardUseW = Math.Min(missingW, standardHeadroomW);
        missingW -= standardUseW;

        if (allowOverPowerAssist && !preferSuperCapFirst && prioritizeSuperCap && missingW > 1e-6)
        {
            double remainingSuperCapLimitW = Math.Max(0.0, model.SuperCapDischargeLimitW - entity.CurrentFrameSuperCapDrawW);
            superCapUseW = Math.Min(
                missingW,
                Math.Min(remainingSuperCapLimitW, usableSuperCapJ / dt));
            missingW -= superCapUseW;
        }

        if (allowOverPowerAssist && missingW > 1e-6)
        {
            double highPriorityBufferJ = Math.Max(0.0, entity.BufferEnergyJ - bufferSwitchJ);
            double remainingBufferLimitW = Math.Max(0.0, ResolveBufferAssistLimitW(entity, dt) - entity.CurrentFrameBufferDrawW);
            double highPriorityBufferUseW = Math.Min(
                missingW,
                Math.Min(remainingBufferLimitW, highPriorityBufferJ / dt));
            bufferUseW += highPriorityBufferUseW;
            missingW -= highPriorityBufferUseW;
        }

        bool allowSuperCapAssist = allowOverPowerAssist && (entity.SuperCapEnabled || ShouldUsePassiveSuperCapAssist(entity));
        if (allowSuperCapAssist && !prioritizeSuperCap && missingW > 1e-6)
        {
            double remainingSuperCapLimitW = Math.Max(0.0, model.SuperCapDischargeLimitW - entity.CurrentFrameSuperCapDrawW);
            double deferredSuperCapUseW = Math.Min(
                missingW,
                Math.Min(remainingSuperCapLimitW, usableSuperCapJ / dt));
            superCapUseW += deferredSuperCapUseW;
            missingW -= deferredSuperCapUseW;
        }

        if (allowOverPowerAssist && missingW > 1e-6)
        {
            double emergencyBufferJ = Math.Max(0.0, Math.Min(entity.BufferEnergyJ, bufferSwitchJ));
            double remainingEmergencyBufferW = Math.Max(0.0, emergencyBufferJ / dt);
            double emergencyBufferUseW = Math.Min(missingW, remainingEmergencyBufferW);
            bufferUseW += emergencyBufferUseW;
            missingW -= emergencyBufferUseW;
        }

        if (missingW > 1e-3 && entity.BufferEnergyJ <= 1e-3 + Math.Min(1.0, bufferSwitchJ * 0.05))
        {
            TriggerOverPowerCut(entity);
            return;
        }

        double appliedW = standardUseW + superCapUseW + bufferUseW;
        if (appliedW <= 1e-6)
        {
            return;
        }

        entity.ChassisPowerDrawW = currentPowerW + appliedW;
        entity.EffectiveDrivePowerLimitW = Math.Max(entity.EffectiveDrivePowerLimitW, entity.ChassisPowerDrawW);
        if (superCapUseW > 1e-6)
        {
            entity.SuperCapEnergyJ = Math.Max(model.CapReserveJ, entity.SuperCapEnergyJ - superCapUseW * dt);
            entity.CurrentFrameSuperCapDrawW += superCapUseW;
        }

        if (bufferUseW > 1e-6)
        {
            entity.BufferEnergyJ = Math.Max(0.0, entity.BufferEnergyJ - bufferUseW * dt);
            entity.CurrentFrameBufferDrawW += bufferUseW;
        }

        double refereePowerW = standardUseW + bufferUseW;
        if (refereePowerW > 1e-6 && entity.MaxChassisEnergy > 1e-6)
        {
            entity.ChassisEnergy = Math.Max(0.0, entity.ChassisEnergy - refereePowerW * dt);
        }
    }

    private static void RechargeStoredDriveEnergy(SimulationEntity entity, double drivePowerLimitW, double powerDrawW, double dt)
    {
        double sparePowerW = Math.Max(0.0, drivePowerLimitW - powerDrawW);
        if (sparePowerW <= 1e-6 || dt <= 1e-6)
        {
            return;
        }

        DriveMotorModel model = ResolveDriveMotorModel(entity);
        double bufferChargeW = Math.Min(sparePowerW, Math.Max(0.0, entity.MaxBufferEnergyJ - entity.BufferEnergyJ) / dt);
        if (bufferChargeW > 1e-6)
        {
            entity.BufferEnergyJ = Math.Min(entity.MaxBufferEnergyJ, entity.BufferEnergyJ + bufferChargeW * dt);
            sparePowerW -= bufferChargeW;
        }

        if (sparePowerW > 1e-6 && entity.MaxSuperCapEnergyJ > 1e-6 && !entity.SuperCapEnabled)
        {
            double superCapChargeW = Math.Min(
                Math.Min(sparePowerW, model.SuperCapDischargeLimitW),
                Math.Max(0.0, entity.MaxSuperCapEnergyJ - entity.SuperCapEnergyJ) / dt);
            entity.SuperCapEnergyJ = Math.Min(entity.MaxSuperCapEnergyJ, entity.SuperCapEnergyJ + superCapChargeW * dt);
        }
    }

    private static double ResolveDriveWheelCount(SimulationEntity entity)
    {
        if (string.Equals(entity.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase))
        {
            return 4.0;
        }

        return Math.Max(2.0, entity.WheelOffsetsM.Count);
    }

    private static double ResolvePhysicalDrivePowerLimitW(SimulationEntity entity)
        => Math.Max(MotorInstantPowerLimitW, ResolveDriveWheelCount(entity) * MotorInstantPowerLimitW);

    private static double EstimateMappedSteadyPowerW(SimulationEntity entity, double speedMps)
    {
        double speed = Math.Max(0.0, speedMps);
        if (speed <= 1e-5)
        {
            return 5.0;
        }

        if (IsBalanceInfantry(entity))
        {
            return 5.0 + 45.0 * Math.Pow(Math.Min(1.0, speed / 3.5), 2.0);
        }

        if (IsOmniInfantry(entity))
        {
            double referenceSpeed = entity.SmallGyroActive ? 2.5 : 3.0;
            return 5.0 + 45.0 * Math.Pow(Math.Min(1.0, speed / referenceSpeed), 2.0);
        }

        double speedAt50W = entity.SmallGyroActive ? 1.0 : 2.0;
        double speedAt120W = entity.SmallGyroActive ? 2.0 : 4.0;
        if (speed <= speedAt50W)
        {
            return 5.0 + 45.0 * Math.Pow(speed / Math.Max(1e-6, speedAt50W), 2.0);
        }

        double t = Math.Clamp((speed - speedAt50W) / Math.Max(1e-6, speedAt120W - speedAt50W), 0.0, 1.0);
        return 50.0 + 70.0 * t * t;
    }

    private static double ResolveMoveSpeedMps(SimulationEntity entity, double drivePowerLimitW)
    {
        double powerW = Math.Max(1.0, drivePowerLimitW);
        if (IsBalanceInfantry(entity))
        {
            return ClampWheelLinearSpeedMps(3.5 * Math.Sqrt(Math.Max(0.0, powerW) / 50.0));
        }

        if (IsOmniInfantry(entity))
        {
            double baseAt50W = entity.SmallGyroActive ? 2.5 : 3.0;
            return ClampWheelLinearSpeedMps(baseAt50W * Math.Sqrt(powerW / 50.0));
        }

        double targetAt50W = entity.SmallGyroActive ? 1.0 : 2.0;
        double targetAt120W = entity.SmallGyroActive ? 2.0 : 4.0;
        double t = Math.Clamp((powerW - 50.0) / 70.0, 0.0, 1.0);
        double targetSpeed = Lerp(targetAt50W, targetAt120W, t);
        if (powerW < 50.0)
        {
            targetSpeed = targetAt50W * Math.Sqrt(powerW / 50.0);
        }

        return ClampWheelLinearSpeedMps(targetSpeed);
    }

    private static double ClampWheelLinearSpeedMps(double speedMps)
        => Math.Clamp(speedMps, 0.0, WheelMaxLinearSpeedMps);

    private static double ResolveAccelerationLimitMps2(
        SimulationEntity entity,
        double drivePowerLimitW,
        double currentSpeedMps,
        double moveMagnitude,
        double directMotorAssistRatio = 0.0,
        bool accelerationTrend = false)
    {
        double massKg = Math.Clamp(entity.MassKg <= 1e-6 ? 20.0 : entity.MassKg, 15.0, 25.0);
        double accelCoeff = Math.Clamp(entity.ChassisDriveAccelCoeff / 0.012, 0.45, 1.80);
        double wheelRadiusM = Math.Clamp(entity.WheelRadiusM <= 1e-6 ? 0.08 : entity.WheelRadiusM, 0.055, 0.11);
        double motorAssistScale = accelerationTrend
            ? 1.0 + directMotorAssistRatio * (IsStandardMecanumPowerRole(entity) ? 0.80 : 0.40)
            : 1.0;
        double motorPeakForcePerWheelN = 28.0 * accelCoeff * (0.08 / wheelRadiusM);
        double driveWheels = string.Equals(entity.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase)
                ? 4.0
                : Math.Max(2.0, entity.WheelOffsetsM.Count);
        double peakTractionForceN = motorPeakForcePerWheelN * driveWheels * ResolveDrivetrainEfficiency(entity) * motorAssistScale;
        double powerLimitedForceN = drivePowerLimitW / Math.Max(0.35, currentSpeedMps);
        if (accelerationTrend && directMotorAssistRatio > 1e-6)
        {
            powerLimitedForceN *= 1.0 + directMotorAssistRatio * 0.22;
        }

        double usableForceN = Math.Min(peakTractionForceN, powerLimitedForceN);
        if (moveMagnitude <= 0.05)
        {
            usableForceN *= 0.92;
        }

        double rollingResistanceN = ResolveRollingResistanceForceN(entity);
        double netForceN = Math.Max(0.0, usableForceN - rollingResistanceN * (moveMagnitude > 0.05 ? 0.35 : 0.10));
        return Math.Clamp(netForceN / massKg, 0.45, 6.8);
    }

    private static double ResolveRollingDragPerSec(SimulationEntity entity)
    {
        double drag = 0.22 + entity.MassKg * 0.0048 + Math.Max(entity.ChassisDriveRpmCoeff, 0.00001) * 2050.0;
        if (HasSmallGyroTranslationInput(entity, entity.MoveInputForward, entity.MoveInputRight))
        {
            drag *= ResolveSmallGyroTranslationResistanceScale(entity);
        }

        return Math.Clamp(drag, 0.24, 0.96);
    }

    private static double ResolveBrakeDragPerSec(SimulationEntity entity)
    {
        double drag = 0.72 + entity.MassKg * 0.012 + entity.ChassisDriveAccelCoeff * 30.0;
        return Math.Clamp(drag, 1.10, 4.20);
    }

    private static double ResolveWheelResponseTimeSec(SimulationEntity entity, double moveMagnitude)
    {
        double massFactor = Math.Clamp((entity.MassKg <= 1e-6 ? 20.0 : entity.MassKg) / 20.0, 0.75, 1.30);
        double accelCoeff = Math.Clamp(entity.ChassisDriveAccelCoeff / 0.012, 0.45, 1.80);
        double baseResponse = moveMagnitude <= 0.05 ? 0.20 : 0.14;
        return Math.Clamp(baseResponse * massFactor / accelCoeff, 0.05, 0.28);
    }

    private static double ResolveWheelResponseTimeSec(
        SimulationEntity entity,
        double moveMagnitude,
        double directMotorAssistRatio,
        bool accelerationTrend)
    {
        double baseResponse = ResolveWheelResponseTimeSec(entity, moveMagnitude);
        if (!accelerationTrend || directMotorAssistRatio <= 1e-6)
        {
            return baseResponse;
        }

        double assistScale = IsStandardMecanumPowerRole(entity)
            ? 1.0 + directMotorAssistRatio * 0.65
            : 1.0 + directMotorAssistRatio * 0.35;
        return Math.Clamp(baseResponse / assistScale, 0.030, 0.28);
    }

    private static double ResolveRollingResistanceForceN(SimulationEntity entity)
    {
        double massKg = Math.Clamp(entity.MassKg <= 1e-6 ? 20.0 : entity.MassKg, 15.0, 25.0);
        double coefficient = string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase) ? 0.046 : 0.038;
        if (HasSmallGyroTranslationInput(entity, entity.MoveInputForward, entity.MoveInputRight))
        {
            coefficient *= ResolveSmallGyroTranslationResistanceScale(entity);
        }

        return massKg * 9.81 * coefficient;
    }

    private static double ResolveDrivetrainEfficiency(SimulationEntity entity)
    {
        if (string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase))
        {
            return 0.76;
        }

        if (string.Equals(entity.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase))
        {
            return 0.82;
        }

        return 0.88;
    }

    private static void ApplyRotation(SimulationEntity entity, double targetYawDeg, double dt)
    {
        if (entity.PowerCutTimerSec > 1e-6)
        {
            entity.AngularVelocityDegPerSec = 0.0;
            return;
        }

        double currentYaw = SimulationCombatMath.NormalizeDeg(entity.AngleDeg);
        double basePowerLimitW = Math.Max(1.0, ResolveEffectiveDrivePowerLimitW(entity));
        double physicalDrivePowerLimitW = ResolvePhysicalDrivePowerLimitW(entity);
        double combinedPowerLimitW = Math.Min(
            physicalDrivePowerLimitW,
            Math.Max(basePowerLimitW, entity.EffectiveDrivePowerLimitW > 1e-6 ? entity.EffectiveDrivePowerLimitW : basePowerLimitW));
        double superCapTurnBoost = entity.SuperCapEnabled
            ? Math.Clamp(Math.Sqrt(combinedPowerLimitW / basePowerLimitW), 1.0, 1.55)
            : 1.0;
        double powerScale = 0.65 + Math.Clamp(entity.ChassisPowerRatio, 0.25, 1.0) * 0.35;
        double baseTurnRate = entity.IsPlayerControlled ? 146.0 : 240.0;
        double maxTurnRate = (entity.SmallGyroActive ? ResolveSmallGyroYawRateDegPerSec(entity) : baseTurnRate) * powerScale * superCapTurnBoost;
        double diff = SimulationCombatMath.NormalizeSignedDeg(targetYawDeg - currentYaw);
        if (entity.SmallGyroActive)
        {
            double turnBudgetRatio = HasSmallGyroTranslationInput(entity, entity.MoveInputForward, entity.MoveInputRight)
                ? 0.50
                : 1.0;
            double remainingTurnBudgetW = Math.Max(1.0, combinedPowerLimitW * turnBudgetRatio);
            if (turnBudgetRatio >= 0.99)
            {
                remainingTurnBudgetW = Math.Max(1.0, remainingTurnBudgetW - Math.Max(0.0, entity.ChassisPowerDrawW));
            }
            maxTurnRate = ResolveSmallGyroYawRateForPower(entity, remainingTurnBudgetW);

            if (Math.Abs(diff) > 1e-6)
            {
                diff = Math.Sign(diff) * maxTurnRate * dt;
            }
        }

        double maxStep = maxTurnRate * dt;
        double applied = Math.Clamp(diff, -maxStep, maxStep);

        entity.AngularVelocityDegPerSec = Math.Abs(dt) > 1e-6 ? applied / dt : 0;
        entity.AngleDeg = SimulationCombatMath.NormalizeDeg(currentYaw + applied);

        double yawRateAbs = Math.Abs(entity.AngularVelocityDegPerSec);
        if (yawRateAbs > 1e-3)
        {
            double turnPowerW = EstimateTurnPowerDrawW(entity, yawRateAbs, entity.SmallGyroActive);
            RaiseChassisPowerDrawTo(
                entity,
                entity.ChassisPowerDrawW + turnPowerW,
                dt,
                allowOverPowerAssist: entity.SuperCapEnabled,
                preferSuperCapFirst: entity.SuperCapEnabled && entity.SmallGyroActive);
        }
    }

    private static void ApplyForcedSuperCapDischarge(SimulationEntity entity, double dt)
    {
        if (!entity.SuperCapEnabled || entity.PowerCutTimerSec > 1e-6 || dt <= 1e-6)
        {
            return;
        }

        DriveMotorModel model = ResolveDriveMotorModel(entity);
        double usableSuperCapJ = Math.Max(0.0, entity.SuperCapEnergyJ - model.CapReserveJ);
        if (usableSuperCapJ <= 1e-6)
        {
            return;
        }

        double physicalHeadroomW = Math.Max(0.0, ResolvePhysicalDrivePowerLimitW(entity) - Math.Max(0.0, entity.ChassisPowerDrawW));
        double remainingSuperCapFrameLimitW = Math.Max(0.0, Math.Min(model.SuperCapDischargeLimitW, SuperCapForcedDischargeW) - entity.CurrentFrameSuperCapDrawW);
        double remainingFrameLimitW = Math.Min(remainingSuperCapFrameLimitW, physicalHeadroomW);
        if (remainingFrameLimitW <= 1e-6)
        {
            return;
        }

        double forcedUseW = Math.Min(remainingFrameLimitW, usableSuperCapJ / dt);
        entity.SuperCapEnergyJ = Math.Max(model.CapReserveJ, entity.SuperCapEnergyJ - forcedUseW * dt);
        entity.CurrentFrameSuperCapDrawW += forcedUseW;
        entity.ChassisPowerDrawW += forcedUseW;
        entity.EffectiveDrivePowerLimitW = Math.Min(
            ResolvePhysicalDrivePowerLimitW(entity),
            Math.Max(entity.EffectiveDrivePowerLimitW, ResolveEffectiveDrivePowerLimitW(entity) + entity.CurrentFrameSuperCapDrawW));
    }

    private static void ApplyVerticalMotion(SimulationEntity entity, double dt)
    {
        AdvanceLandingSuspension(entity, dt);
        if (entity.JumpCrouchTimerSec > 1e-6)
        {
            entity.JumpCrouchTimerSec = Math.Max(0.0, entity.JumpCrouchTimerSec - dt);
            entity.AirborneHeightM = 0.0;
            entity.VerticalVelocityMps = 0.0;
            entity.LedgeLaunchTimerSec = 0.0;
            if (entity.JumpCrouchTimerSec <= 1e-6)
            {
                LaunchJump(entity);
            }

            return;
        }

        if (entity.AirborneHeightM <= 1e-6 && entity.VerticalVelocityMps <= 1e-6)
        {
            entity.AirborneHeightM = 0;
            entity.VerticalVelocityMps = 0;
            entity.LedgeLaunchTimerSec = 0;
            return;
        }

        double gravityScale = 1.0;
        double previousAirborneHeightM = entity.AirborneHeightM;
        entity.LedgeLaunchTimerSec = Math.Max(0.0, entity.LedgeLaunchTimerSec - dt);
        entity.VerticalVelocityMps -= GravityMps2 * gravityScale * dt;
        double impactSpeedMps = Math.Max(0.0, -entity.VerticalVelocityMps);
        entity.AirborneHeightM = Math.Max(0.0, entity.AirborneHeightM + entity.VerticalVelocityMps * dt);
        double maxJumpHeightM = ResolveMaxAirborneHeightM(entity);
        if (entity.AirborneHeightM > maxJumpHeightM)
        {
            entity.AirborneHeightM = maxJumpHeightM;
            entity.VerticalVelocityMps = Math.Min(0.0, entity.VerticalVelocityMps);
        }

        if (entity.AirborneHeightM <= 1e-6)
        {
            RegisterLandingCompression(entity, impactSpeedMps, previousAirborneHeightM);
            entity.AirborneHeightM = 0;
            entity.VerticalVelocityMps = 0;
            entity.LedgeLaunchTimerSec = 0;
        }
    }

    private static void StartJumpOrCrouch(SimulationEntity entity)
    {
        if (IsBalanceInfantry(entity))
        {
            entity.JumpCrouchDurationSec = BalanceInfantryJumpCrouchDurationSec;
            entity.JumpCrouchTimerSec = BalanceInfantryJumpCrouchDurationSec;
            entity.VerticalVelocityMps = 0.0;
            return;
        }

        LaunchJump(entity);
    }

    private static void LaunchJump(SimulationEntity entity)
    {
        double targetJumpHeightM = ResolveTargetJumpHeightM(entity);
        entity.VerticalVelocityMps = Math.Sqrt(2.0 * GravityMps2 * targetJumpHeightM);
        entity.JumpCrouchTimerSec = 0.0;
        entity.JumpCrouchDurationSec = 0.0;
    }

    private static void RegisterLandingCompression(SimulationEntity entity, double impactSpeedMps, double previousAirborneHeightM)
    {
        if (!SupportsLandingDamping(entity)
            || impactSpeedMps < 0.45
            || previousAirborneHeightM < 0.035)
        {
            return;
        }

        double compressionM = 0.008
            + Math.Clamp(impactSpeedMps * 0.012 + previousAirborneHeightM * 0.020, 0.0, LandingCompressionMaxM - 0.008);
        entity.LandingCompressionM = Math.Clamp(
            Math.Max(entity.LandingCompressionM, compressionM),
            -LandingReboundMaxM,
            LandingCompressionMaxM);
        entity.LandingCompressionVelocityMps = Math.Min(entity.LandingCompressionVelocityMps, 0.0);
    }

    private static void AdvanceLandingSuspension(SimulationEntity entity, double dt)
    {
        if (Math.Abs(entity.LandingCompressionM) <= 1e-5
            && Math.Abs(entity.LandingCompressionVelocityMps) <= 1e-4)
        {
            entity.LandingCompressionM = 0.0;
            entity.LandingCompressionVelocityMps = 0.0;
            return;
        }

        int steps = Math.Clamp((int)Math.Ceiling(Math.Max(0.0, dt) / 0.012), 1, 24);
        double step = Math.Max(0.0, dt) / steps;
        for (int index = 0; index < steps; index++)
        {
            double acceleration = -145.0 * entity.LandingCompressionM - 15.5 * entity.LandingCompressionVelocityMps;
            entity.LandingCompressionVelocityMps += acceleration * step;
            entity.LandingCompressionM += entity.LandingCompressionVelocityMps * step;
            if (entity.LandingCompressionM > LandingCompressionMaxM)
            {
                entity.LandingCompressionM = LandingCompressionMaxM;
                entity.LandingCompressionVelocityMps = Math.Min(0.0, entity.LandingCompressionVelocityMps);
            }
            else if (entity.LandingCompressionM < -LandingReboundMaxM)
            {
                entity.LandingCompressionM = -LandingReboundMaxM;
                entity.LandingCompressionVelocityMps = Math.Max(0.0, entity.LandingCompressionVelocityMps);
            }
        }

        if (Math.Abs(entity.LandingCompressionM) <= 0.0006
            && Math.Abs(entity.LandingCompressionVelocityMps) <= 0.018)
        {
            entity.LandingCompressionM = 0.0;
            entity.LandingCompressionVelocityMps = 0.0;
        }
    }

    private static bool SupportsLandingDamping(SimulationEntity entity)
        => IsMovableEntity(entity);

    private static double ResolveTargetJumpHeightM(SimulationEntity entity)
    {
        if (IsBalanceInfantry(entity))
        {
            return BalanceInfantryMaxJumpHeightM;
        }

        return string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            ? 0.80 * (2.0 / 3.0)
            : 0.80;
    }

    private static double ResolveMaxAirborneHeightM(SimulationEntity entity)
        => IsBalanceInfantry(entity) ? BalanceInfantryMaxJumpHeightM : 2.0;

    private static void UpdateAutoAimInstability(
        SimulationWorldState world,
        SimulationEntity entity,
        double dt,
        double previousSpeedMps,
        double previousAimHeightM)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double speedMps = Math.Sqrt(
            entity.VelocityXWorldPerSec * entity.VelocityXWorldPerSec
            + entity.VelocityYWorldPerSec * entity.VelocityYWorldPerSec) * metersPerWorldUnit;
        double aimHeightM = entity.GroundHeightM
            + entity.AirborneHeightM
            + Math.Max(0.08, entity.BodyClearanceM + entity.BodyHeightM * 0.55);

        entity.AutoAimInstabilityTimerSec = Math.Max(0.0, entity.AutoAimInstabilityTimerSec - dt);
        bool initialized = previousAimHeightM > 1e-6 || previousSpeedMps > 1e-6;
        if (initialized)
        {
            double decelMps2 = (previousSpeedMps - speedMps) / Math.Max(dt, 1e-6);
            double heightDeltaM = Math.Abs(aimHeightM - previousAimHeightM);
            if (decelMps2 > 4.2 || heightDeltaM > 0.12 || Math.Abs(entity.VerticalVelocityMps) > 1.6)
            {
                entity.AutoAimInstabilityTimerSec = Math.Max(entity.AutoAimInstabilityTimerSec, 0.42);
            }
        }

        entity.LastAimSpeedMps = speedMps;
        entity.LastAimHeightM = aimHeightM;
    }

    private void ApplyTranslationWithTerrain(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double dt)
    {
        ResolveCurrentPenetrationIfNeeded(world, runtimeGrid, entity);
        int substeps = ResolveTranslationSubsteps(world, runtimeGrid, entity, dt);
        double substepDt = dt / Math.Max(1, substeps);
        for (int stepIndex = 0; stepIndex < substeps; stepIndex++)
        {
            if (entity.TraversalActive)
            {
                double previousX = entity.X;
                double previousY = entity.Y;
                double previousGroundHeight = entity.GroundHeightM;
                double previousProgress = entity.TraversalProgress;
                UpdateTraversal(entity, substepDt);
                if (ResolveEntityCollisionAt(world, entity, entity.X, entity.Y, out EntityContactResolution traversalContact))
                {
                    if (TryApplyEntityContactResolution(world, runtimeGrid, entity, traversalContact, currentHeightOverrideM: previousGroundHeight))
                    {
                        entity.TraversalActive = false;
                        entity.TraversalProgress = 0.0;
                        entity.MotionBlockReason = $"traversal_collision:{traversalContact.BlockingEntity.Id}";
                    }
                    else
                    {
                        entity.X = previousX;
                        entity.Y = previousY;
                        entity.GroundHeightM = previousGroundHeight;
                        entity.TraversalProgress = previousProgress;
                        entity.TraversalActive = false;
                        entity.VelocityXWorldPerSec = 0.0;
                        entity.VelocityYWorldPerSec = 0.0;
                        entity.MotionBlockReason = $"traversal_collision:{traversalContact.BlockingEntity.Id}";
                    }
                    break;
                }

                EnforceNoTerrainPenetration(world, runtimeGrid, entity);
                continue;
            }

            if (!ApplyTranslationSubstep(world, runtimeGrid, entity, substepDt))
            {
                break;
            }
        }

        ApplyBlockedStallPowerIfNeeded(world, entity, dt);
        UpdateChassisTerrainPose(world, runtimeGrid, entity, dt);
        NormalizeUnreachableVerticalAnchor(world, runtimeGrid, entity);
        EnforceNoTerrainPenetration(world, runtimeGrid, entity);
        NormalizeUnreachableVerticalAnchor(world, runtimeGrid, entity);
    }

    private void ResolveCurrentPenetrationIfNeeded(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity)
    {
        double currentHeight = SampleTerrainHeight(runtimeGrid, entity.X, entity.Y);
        if (TryResolveStaticContactAt(world, runtimeGrid, entity, entity.X, entity.Y, currentHeight, out StaticContactResolution staticResolution))
        {
            if (TryApplyStaticContactResolution(world, runtimeGrid, entity, staticResolution, currentHeight))
            {
                entity.VelocityXWorldPerSec = 0.0;
                entity.VelocityYWorldPerSec = 0.0;
                entity.MotionBlockReason = staticResolution.Reason;
            }
        }

        if (ResolveEntityCollisionAt(world, entity, entity.X, entity.Y, out EntityContactResolution entityResolution))
        {
            if (TryApplyEntityContactResolution(world, runtimeGrid, entity, entityResolution, currentHeight))
            {
                entity.VelocityXWorldPerSec = 0.0;
                entity.VelocityYWorldPerSec = 0.0;
                entity.MotionBlockReason = $"entity_contact:{entityResolution.BlockingEntity.Id}";
            }
        }

        EnforceNoTerrainPenetration(world, runtimeGrid, entity);
    }

    private static void ApplyBlockedStallPowerIfNeeded(SimulationWorldState world, SimulationEntity entity, double dt)
    {
        if (string.IsNullOrWhiteSpace(entity.MotionBlockReason))
        {
            return;
        }

        double moveForward = Math.Clamp(entity.MoveInputForward, -1.0, 1.0);
        double moveRight = Math.Clamp(entity.MoveInputRight, -1.0, 1.0);
        double moveMagnitude = Math.Min(1.0, Math.Sqrt(moveForward * moveForward + moveRight * moveRight));
        if (moveMagnitude <= 0.05)
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double currentVxMps = entity.VelocityXWorldPerSec * metersPerWorldUnit;
        double currentVyMps = entity.VelocityYWorldPerSec * metersPerWorldUnit;
        double currentSpeedMps = Math.Sqrt(currentVxMps * currentVxMps + currentVyMps * currentVyMps);
        double expectedSpeedMps = Math.Max(
            0.20,
            Math.Max(entity.ChassisSpeedLimitMps, ResolveMoveSpeedMps(entity, Math.Max(1.0, entity.EffectiveDrivePowerLimitW))) * moveMagnitude);
        double responseTimeSec = Math.Max(0.030, ResolveWheelResponseTimeSec(entity, moveMagnitude));
        double requestedAccelMps2 = Math.Abs(expectedSpeedMps - currentSpeedMps) / responseTimeSec;
        double stallPowerW = EstimateChassisPowerDrawW(
            entity,
            currentSpeedMps,
            expectedSpeedMps,
            requestedAccelMps2,
            moveMagnitude,
            dt,
            brakingHard: false,
            reversingCommand: false);
        stallPowerW *= 1.15;
        RaiseChassisPowerDrawTo(entity, stallPowerW, dt);
    }

    private bool ApplyTranslationSubstep(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double dt)
    {
        double nextX = entity.X + entity.VelocityXWorldPerSec * dt;
        double nextY = entity.Y + entity.VelocityYWorldPerSec * dt;

        ClampToMap(runtimeGrid, ref nextX, ref nextY);
        double currentHeight = ResolveTraversalGroundHeight(world, runtimeGrid, entity, entity.X, entity.Y);
        double targetHeight = ResolveTraversalGroundHeight(world, runtimeGrid, entity, nextX, nextY);
        double heightDelta = targetHeight - currentHeight;
        double directStep = ResolveDirectTraversalStepHeightM(entity);
        double maxStep = ResolveEffectiveTraversalStepHeightM(entity);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(entity);
        double effectiveDirectStep = directStep + jumpClearance;
        double supportHeight = ResolveFootprintSupportHeight(world, runtimeGrid, entity, nextX, nextY, currentHeight, maxStep + jumpClearance);
        if (TryResolveStaticContactAt(world, runtimeGrid, entity, nextX, nextY, currentHeight, out StaticContactResolution trendContact))
        {
            if (IsIgnorableStepLipContact(runtimeGrid, trendContact, currentHeight, targetHeight, supportHeight, maxStep, jumpClearance))
            {
                trendContact = default;
            }
            else
            {
                ApplyBlockedMotionStop(entity, entity.X - nextX + trendContact.SeparationVector.X, entity.Y - nextY + trendContact.SeparationVector.Y);
                entity.MotionBlockReason = $"collision_trend:{trendContact.Reason}";
                return false;
            }
        }

        bool blocked = runtimeGrid.CollisionSurface is null
            && runtimeGrid.IsMovementBlockedAt(nextX, nextY, currentHeight, maxStep + jumpClearance);

        if (entity.AirborneHeightM > 1e-4 && entity.LedgeLaunchTimerSec > 1e-6 && targetHeight < currentHeight)
        {
            targetHeight = currentHeight;
            heightDelta = 0.0;
        }

        if (blocked || heightDelta > maxStep + jumpClearance + 1e-6)
        {
            if (TryResolveStaticContactAt(world, runtimeGrid, entity, nextX, nextY, currentHeight, out StaticContactResolution staticContact)
                && TryApplyStaticContactResolution(world, runtimeGrid, entity, staticContact, currentHeight))
            {
                entity.MotionBlockReason = staticContact.Reason;
                return false;
            }

            if (TryMoveToReachableContactPosition(world, runtimeGrid, entity, entity.X, entity.Y, nextX, nextY, currentHeight, maxStep, jumpClearance))
            {
                entity.MotionBlockReason = blocked ? "terrain_block_contact" : "step_contact";
                return false;
            }

            ApplyBlockedMotionStop(entity, entity.X - nextX, entity.Y - nextY);
            entity.MotionBlockReason = blocked ? "terrain_block" : "step_too_high";
            LogTerrainMovementBlockIfDue(
                entity,
                runtimeGrid,
                entity.MotionBlockReason,
                nextX,
                nextY,
                currentHeight,
                targetHeight,
                maxStep + jumpClearance);
            return false;
        }

        if (!CanOccupyTerrainFootprint(world, runtimeGrid, entity, nextX, nextY, currentHeight, maxStep, jumpClearance))
        {
            if (TryResolveStaticContactAt(world, runtimeGrid, entity, nextX, nextY, currentHeight, out StaticContactResolution staticContact)
                && TryApplyStaticContactResolution(world, runtimeGrid, entity, staticContact, currentHeight))
            {
                entity.MotionBlockReason = staticContact.Reason;
                return false;
            }

            if (TryMoveToReachableContactPosition(world, runtimeGrid, entity, entity.X, entity.Y, nextX, nextY, currentHeight, maxStep, jumpClearance))
            {
                entity.MotionBlockReason = "terrain_footprint_contact";
                return false;
            }

            ApplyBlockedMotionStop(entity, entity.X - nextX, entity.Y - nextY);
            entity.MotionBlockReason = "terrain_footprint";
            LogTerrainMovementBlockIfDue(
                entity,
                runtimeGrid,
                entity.MotionBlockReason,
                nextX,
                nextY,
                currentHeight,
                targetHeight,
                maxStep + jumpClearance);
            return false;
        }

        if (heightDelta > effectiveDirectStep + 1e-6)
        {
            double moveHeading = entity.TraversalDirectionDeg;
            double headingError = Math.Abs(SimulationCombatMath.NormalizeSignedDeg(moveHeading - entity.AngleDeg));
            if (headingError > 16.0)
            {
                entity.VelocityXWorldPerSec = 0;
                entity.VelocityYWorldPerSec = 0;
                entity.MotionBlockReason = "step_alignment";
                return false;
            }

            StartTraversal(entity, nextX, nextY, currentHeight, targetHeight);
            UpdateTraversal(entity, dt);
            entity.MotionBlockReason = string.Empty;
            return true;
        }

        if (ResolveEntityCollisionAt(world, entity, nextX, nextY, out EntityContactResolution contact))
        {
            if (TryApplyEntityContactResolution(world, runtimeGrid, entity, contact, currentHeight))
            {
                entity.MotionBlockReason = $"entity_contact:{contact.BlockingEntity.Id}";
                return false;
            }

            double awayX = entity.X - contact.BlockingEntity.X;
            double awayY = entity.Y - contact.BlockingEntity.Y;
            ApplyWallBounce(entity, awayX, awayY, 0.24);
            entity.MotionBlockReason = $"entity_collision:{contact.BlockingEntity.Id}";
            return false;
        }

        if (TryResolveStaticContactAt(world, runtimeGrid, entity, nextX, nextY, currentHeight, out StaticContactResolution residualStaticContact)
            && !IsIgnorableStepLipContact(runtimeGrid, residualStaticContact, currentHeight, targetHeight, supportHeight, maxStep, jumpClearance)
            && TryApplyStaticContactResolution(world, runtimeGrid, entity, residualStaticContact, currentHeight))
        {
            entity.MotionBlockReason = residualStaticContact.Reason;
            return false;
        }

        double speedMps = Math.Sqrt(
            entity.VelocityXWorldPerSec * entity.VelocityXWorldPerSec
            + entity.VelocityYWorldPerSec * entity.VelocityYWorldPerSec) * Math.Max(world.MetersPerWorldUnit, 1e-6);
        bool frontWheelDrop = TryResolveForwardDownstepHeight(
            runtimeGrid,
            entity,
            nextX,
            nextY,
            entity.VelocityXWorldPerSec,
            entity.VelocityYWorldPerSec,
            currentHeight,
            out double forwardDropHeight);
        if ((heightDelta < -TerrainSmoothHeightThresholdM || frontWheelDrop)
            && speedMps > 0.20
            && entity.AirborneHeightM <= 1e-4)
        {
            // Keep the world-space chassis height continuous when driving off a ledge;
            // gravity then owns the descent, producing the expected parabolic drop.
            targetHeight = frontWheelDrop ? Math.Min(targetHeight, forwardDropHeight) : targetHeight;
            entity.AirborneHeightM = Math.Max(entity.AirborneHeightM, currentHeight - targetHeight + 0.018);
            double launchPitchRad = DegreesToRadians(Math.Clamp(entity.ChassisPitchDeg, -24.0, 24.0));
            double launchVerticalMps = Math.Sin(launchPitchRad) * speedMps;
            entity.VerticalVelocityMps = Math.Abs(launchVerticalMps) <= 0.035 ? 0.0 : launchVerticalMps;
            entity.LedgeLaunchTimerSec = Math.Max(entity.LedgeLaunchTimerSec, 0.42);
            PreserveDownstepLaunchSpeed(entity, world.MetersPerWorldUnit, speedMps);
        }

        entity.X = nextX;
        entity.Y = nextY;
        entity.GroundHeightM = targetHeight;
        EnforceNoTerrainPenetration(world, runtimeGrid, entity);
        entity.MotionBlockReason = string.Empty;
        return true;
    }

    private static bool IsIgnorableStepLipContact(
        RuntimeGridData runtimeGrid,
        StaticContactResolution contact,
        double currentHeight,
        double targetHeight,
        double supportHeight,
        double maxStep,
        double jumpClearance)
    {
        if (runtimeGrid.CollisionSurface is null
            || !string.Equals(contact.Reason, "collision_surface_wall_contact", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        double climbAllowance = maxStep + jumpClearance;
        double heightDelta = targetHeight - currentHeight;
        return contact.PenetrationWorld <= 0.030
            && heightDelta >= -TerrainSmoothHeightThresholdM
            && heightDelta <= climbAllowance + 0.12
            && supportHeight <= currentHeight + climbAllowance + 0.12;
    }

    private static bool TryResolveForwardDownstepHeight(
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double nextX,
        double nextY,
        double velocityXWorldPerSec,
        double velocityYWorldPerSec,
        double currentWheelPlaneHeightM,
        out double forwardHeightM)
    {
        forwardHeightM = currentWheelPlaneHeightM;
        double speedWorld = Math.Sqrt(velocityXWorldPerSec * velocityXWorldPerSec + velocityYWorldPerSec * velocityYWorldPerSec);
        if (speedWorld <= 1e-6)
        {
            return false;
        }

        double dirX = velocityXWorldPerSec / speedWorld;
        double dirY = velocityYWorldPerSec / speedWorld;
        double yawRad = DegreesToRadians(entity.AngleDeg);
        double rightX = -Math.Sin(yawRad);
        double rightY = Math.Cos(yawRad);
        double halfLength = Math.Max(0.10, entity.BodyLengthM * 0.5);
        double halfWidth = Math.Max(0.08, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5);
        double frontProbeDistance = halfLength + Math.Max(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld) * 0.35;
        double sideProbe = halfWidth * 0.72;

        double minHeight = double.MaxValue;
        double[] sideOffsets = { 0.0, -sideProbe, sideProbe };
        foreach (double side in sideOffsets)
        {
            double sampleX = nextX + dirX * frontProbeDistance + rightX * side;
            double sampleY = nextY + dirY * frontProbeDistance + rightY * side;
            if (sampleX < 0.0
                || sampleY < 0.0
                || sampleX >= runtimeGrid.WidthCells * runtimeGrid.CellWidthWorld
                || sampleY >= runtimeGrid.HeightCells * runtimeGrid.CellHeightWorld)
            {
                continue;
            }

            minHeight = Math.Min(minHeight, SampleTerrainHeight(runtimeGrid, sampleX, sampleY));
        }

        if (minHeight == double.MaxValue)
        {
            return false;
        }

        forwardHeightM = minHeight;
        return currentWheelPlaneHeightM - minHeight > TerrainSmoothHeightThresholdM + 1e-6;
    }

    private static void PreserveDownstepLaunchSpeed(SimulationEntity entity, double metersPerWorldUnit, double currentSpeedMps)
    {
        double scale = Math.Max(metersPerWorldUnit, 1e-6);
        double vxMps = entity.VelocityXWorldPerSec * scale;
        double vyMps = entity.VelocityYWorldPerSec * scale;
        double speedMps = Math.Sqrt(vxMps * vxMps + vyMps * vyMps);
        double yawRad = DegreesToRadians(entity.AngleDeg);
        double inputForward = Math.Clamp(entity.MoveInputForward, -1.0, 1.0);
        double inputRight = Math.Clamp(entity.MoveInputRight, -1.0, 1.0);
        if (speedMps < 0.20)
        {
            if (Math.Sqrt(inputForward * inputForward + inputRight * inputRight) > 0.05)
            {
                double forwardX = Math.Cos(yawRad);
                double forwardY = Math.Sin(yawRad);
                double rightX = Math.Cos(yawRad + Math.PI * 0.5);
                double rightY = Math.Sin(yawRad + Math.PI * 0.5);
                vxMps = forwardX * inputForward + rightX * inputRight;
                vyMps = forwardY * inputForward + rightY * inputRight;
                speedMps = Math.Sqrt(vxMps * vxMps + vyMps * vyMps);
            }

            if (speedMps < 0.20)
            {
                vxMps = Math.Cos(yawRad);
                vyMps = Math.Sin(yawRad);
                speedMps = 1.0;
            }
        }

        double launchSpeedMps = Math.Max(speedMps, currentSpeedMps);
        entity.VelocityXWorldPerSec = vxMps / speedMps * launchSpeedMps / scale;
        entity.VelocityYWorldPerSec = vyMps / speedMps * launchSpeedMps / scale;
    }

    private static double ResolveTerrainClearanceAllowanceM(SimulationEntity entity)
        => Math.Max(0.0, entity.AirborneHeightM);

    private static double ResolveTerrainClearanceAllowanceM(NavigationUnitSnapshot entity)
        => Math.Max(0.0, entity.AirborneHeightM);

    private static void EnforceNoTerrainPenetration(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity)
    {
        if (SimulationCombatMath.IsStructure(entity))
        {
            return;
        }

        double directStep = ResolveDirectTraversalStepHeightM(entity);
        double maxStep = ResolveEffectiveTraversalStepHeightM(entity);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(entity);
        double referenceHeight = entity.GroundHeightM + Math.Max(0.0, entity.AirborneHeightM);
        double supportHeight = ResolveFootprintSupportHeight(
            world,
            runtimeGrid,
            entity,
            entity.X,
            entity.Y,
            referenceHeight,
            maxStep + jumpClearance);
        double chassisBottomHeight = entity.GroundHeightM + Math.Max(0.0, entity.AirborneHeightM);
        if (chassisBottomHeight >= supportHeight - 0.002)
        {
            return;
        }

        double supportLiftM = supportHeight - chassisBottomHeight;
        double allowedRecoveryLiftM = maxStep + jumpClearance + 0.08;
        if (supportLiftM > allowedRecoveryLiftM + 1e-6)
        {
            entity.VerticalVelocityMps = Math.Min(0.0, entity.VerticalVelocityMps);
            entity.LedgeLaunchTimerSec = 0.0;
            return;
        }

        if (entity.AirborneHeightM > 1e-4 && entity.VerticalVelocityMps <= 0.0)
        {
            entity.GroundHeightM = supportHeight;
            entity.AirborneHeightM = 0.0;
            entity.VerticalVelocityMps = 0.0;
            entity.LedgeLaunchTimerSec = 0.0;
            return;
        }

        entity.GroundHeightM = supportHeight - Math.Max(0.0, entity.AirborneHeightM);
    }

    private static double ResolveTraversalGroundHeight(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double centerX,
        double centerY)
    {
        double directStep = ResolveDirectTraversalStepHeightM(entity);
        double maxStep = ResolveEffectiveTraversalStepHeightM(entity);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(entity);
        double referenceHeight = entity.GroundHeightM + Math.Max(0.0, entity.AirborneHeightM);
        double supportHeight = ResolveFootprintSupportHeight(
            world,
            runtimeGrid,
            entity,
            centerX,
            centerY,
            referenceHeight,
            maxStep + jumpClearance);
        double centerHeight = SampleTraversalTerrainHeight(runtimeGrid, centerX, centerY, referenceHeight, maxStep + jumpClearance);
        if (runtimeGrid.CollisionSurface is null)
        {
            return Math.Max(centerHeight, supportHeight);
        }

        // Fine triangle terrain often contains narrow risers directly under the
        // chassis center while the actual support points are still on the same
        // traversable plane. Limit how far the center sample can lead the
        // support plane so robots do not get blocked by phantom "air steps".
        return Math.Max(supportHeight, Math.Min(centerHeight, supportHeight + 0.08));
    }

    private static double ResolveFootprintSupportHeight(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double centerX,
        double centerY,
        double referenceHeightM,
        double allowedRiseM)
    {
        double maxX = runtimeGrid.WidthCells * runtimeGrid.CellWidthWorld;
        double maxY = runtimeGrid.HeightCells * runtimeGrid.CellHeightWorld;
        Span<double> supportHeights = stackalloc double[24];
        Span<double> sampleHeights = stackalloc double[9];
        int supportCount = 0;

        foreach (CollisionFootprint footprint in BuildSupportCollisionFootprints(world, entity, centerX, centerY))
        {
            double insetLength = footprint.HalfLengthWorld * 0.88;
            double insetWidth = footprint.HalfWidthWorld * 0.88;
            Span<(double LocalX, double LocalY)> samples =
            [
                (0.0, 0.0),
                (insetLength, 0.0),
                (-insetLength, 0.0),
                (0.0, insetWidth),
                (0.0, -insetWidth),
                (insetLength, insetWidth),
                (insetLength, -insetWidth),
                (-insetLength, insetWidth),
                (-insetLength, -insetWidth),
            ];

            int sampleCount = 0;
            for (int index = 0; index < samples.Length; index++)
            {
                double sampleX = footprint.Center.X + footprint.Forward.X * samples[index].LocalX + footprint.Right.X * samples[index].LocalY;
                double sampleY = footprint.Center.Y + footprint.Forward.Y * samples[index].LocalX + footprint.Right.Y * samples[index].LocalY;
                if (sampleX < 0.0 || sampleY < 0.0 || sampleX >= maxX || sampleY >= maxY)
                {
                    continue;
                }

                if (TrySampleTraversalCollisionSurface(
                        runtimeGrid,
                        sampleX,
                        sampleY,
                        referenceHeightM,
                        allowedRiseM,
                        out TerrainSurfaceSample supportSample))
                {
                    sampleHeights[sampleCount++] = supportSample.HeightM <= TerrainRenderAnchorThresholdM
                        ? 0.0
                        : supportSample.HeightM;
                }
            }

            if (sampleCount > 0 && supportCount < supportHeights.Length)
            {
                supportHeights[supportCount++] = ResolveRobustSupportHeightFromSamples(
                    sampleHeights[..sampleCount],
                    ResolveFootprintSamplePercentile(entity));
            }
        }

        if (supportCount <= 0)
        {
            return runtimeGrid.CollisionSurface is null
                ? SampleTerrainHeight(runtimeGrid, centerX, centerY)
                : referenceHeightM;
        }

        return ResolveRobustSupportHeightFromSamples(
            supportHeights[..supportCount],
            ResolveOverallSupportPercentile(entity));
    }

    private static CollisionFootprint[] BuildSupportCollisionFootprints(
        SimulationWorldState world,
        SimulationEntity entity,
        double centerX,
        double centerY)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double yawRad = DegreesToRadians(entity.AngleDeg);
        Vector2 forward = new((float)Math.Cos(yawRad), (float)Math.Sin(yawRad));
        Vector2 right = new((float)Math.Cos(yawRad + Math.PI * 0.5), (float)Math.Sin(yawRad + Math.PI * 0.5));
        IReadOnlyList<EntityCollisionPart> parts = EntityCollisionModel.ResolveParts(entity);
        double baseHeight = entity.GroundHeightM + Math.Max(0.0, entity.AirborneHeightM);
        bool narrowWheelSupport = UsesLeggedSupportProfile(entity);
        var footprints = new List<CollisionFootprint>(Math.Max(1, parts.Count));
        foreach (EntityCollisionPart part in parts)
        {
            if (!IsGroundSupportCollisionPart(part.Id))
            {
                continue;
            }

            double localXWorld = part.LocalX / metersPerWorldUnit;
            double localYWorld = part.LocalY / metersPerWorldUnit;
            Vector2 partCenter = new(
                (float)(centerX + forward.X * localXWorld + right.X * localYWorld),
                (float)(centerY + forward.Y * localXWorld + right.Y * localYWorld));
            bool wheelPart = part.Id.Contains("wheel", StringComparison.OrdinalIgnoreCase);
            bool legPart = part.Id.Contains("leg", StringComparison.OrdinalIgnoreCase);
            double contactLengthScale = wheelPart
                ? (narrowWheelSupport ? 0.34 : 0.44)
                : legPart
                    ? (narrowWheelSupport ? 0.28 : 0.34)
                    : 0.38;
            double contactWidthScale = wheelPart
                ? (narrowWheelSupport ? 0.46 : 0.58)
                : legPart
                    ? (narrowWheelSupport ? 0.38 : 0.48)
                    : 0.54;
            double halfLengthWorld = Math.Max(0.008, part.LengthM * contactLengthScale * 0.5) / metersPerWorldUnit;
            double halfWidthWorld = Math.Max(0.008, part.WidthM * contactWidthScale * 0.5) / metersPerWorldUnit;
            double minHeightM = baseHeight + Math.Max(0.0, part.MinHeightM);
            double maxHeightM = minHeightM + Math.Max(0.04, part.HeightM);
            double boundingRadiusWorld = Math.Sqrt(halfLengthWorld * halfLengthWorld + halfWidthWorld * halfWidthWorld) + 0.01;
            footprints.Add(new CollisionFootprint(
                partCenter,
                forward,
                right,
                halfLengthWorld,
                halfWidthWorld,
                minHeightM,
                maxHeightM,
                boundingRadiusWorld));
        }

        if (footprints.Count == 0)
        {
            return BuildCollisionFootprints(world, entity, centerX, centerY);
        }

        return footprints.ToArray();
    }

    private static bool IsGroundSupportCollisionPart(string partId)
    {
        return partId.Contains("wheel", StringComparison.OrdinalIgnoreCase)
            || partId.Contains("leg", StringComparison.OrdinalIgnoreCase)
            || partId.Contains("climb", StringComparison.OrdinalIgnoreCase);
    }

    private void LogTerrainMovementBlockIfDue(
        SimulationEntity entity,
        RuntimeGridData runtimeGrid,
        string reason,
        double worldX,
        double worldY,
        double currentHeightM,
        double targetHeightM,
        double allowedRiseM)
    {
        if (runtimeGrid.CollisionSurface is null)
        {
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastTerrainMovementBlockLogTicks > 0
            && (nowTicks - _lastTerrainMovementBlockLogTicks) / (double)System.Diagnostics.Stopwatch.Frequency < 0.75)
        {
            return;
        }

        _lastTerrainMovementBlockLogTicks = nowTicks;
        double velocityMps = Math.Sqrt(
            entity.VelocityXWorldPerSec * entity.VelocityXWorldPerSec
            + entity.VelocityYWorldPerSec * entity.VelocityYWorldPerSec) * Math.Max(_metersPerWorldUnit, 1e-6);
        string line =
            $"{DateTime.Now:HH:mm:ss.fff} "
            + $"id={entity.Id} "
            + $"reason={reason} "
            + $"pos=({worldX:0.00},{worldY:0.00}) "
            + $"height={currentHeightM:0.000}->{targetHeightM:0.000} "
            + $"rise={targetHeightM - currentHeightM:0.000} "
            + $"allowed={allowedRiseM:0.000} "
            + $"direct_step={entity.DirectStepHeightM:0.000} "
            + $"max_step={entity.MaxStepClimbHeightM:0.000} "
            + $"airborne={entity.AirborneHeightM:0.000} "
            + $"speed={velocityMps:0.00}mps";
        SimulatorRuntimeLog.Append("terrain_movement_block.log", line);
    }

    private static void ApplyWallBounce(SimulationEntity entity, double awayX, double awayY, double impulseMps)
    {
        double length = Math.Sqrt(awayX * awayX + awayY * awayY);
        if (length <= 1e-6)
        {
            return;
        }

        double nx = awayX / length;
        double ny = awayY / length;
        entity.VelocityXWorldPerSec += nx * impulseMps;
        entity.VelocityYWorldPerSec += ny * impulseMps;
    }

    private static void ApplyBlockedMotionStop(SimulationEntity entity, double awayX, double awayY)
    {
        double length = Math.Sqrt(awayX * awayX + awayY * awayY);
        if (length <= 1e-6)
        {
            entity.VelocityXWorldPerSec = 0.0;
            entity.VelocityYWorldPerSec = 0.0;
            return;
        }

        double nx = awayX / length;
        double ny = awayY / length;
        double alongAway = entity.VelocityXWorldPerSec * nx + entity.VelocityYWorldPerSec * ny;
        if (alongAway < 0.0)
        {
            entity.VelocityXWorldPerSec -= nx * alongAway;
            entity.VelocityYWorldPerSec -= ny * alongAway;
        }
    }

    private static void ApplyContactBounceMps(
        SimulationWorldState world,
        SimulationEntity entity,
        double awayX,
        double awayY,
        double impulseMps)
    {
        double length = Math.Sqrt(awayX * awayX + awayY * awayY);
        if (length <= 1e-6)
        {
            return;
        }

        double scale = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double nx = awayX / length;
        double ny = awayY / length;
        Vector2 normal = new((float)nx, (float)ny);
        Vector2 velocity = new((float)entity.VelocityXWorldPerSec, (float)entity.VelocityYWorldPerSec);
        float intoContact = Vector2.Dot(velocity, normal);
        if (intoContact < 0f)
        {
            velocity -= normal * intoContact;
        }

        double impulseWorldPerSec = Math.Max(0.0, impulseMps) / scale;
        velocity += normal * (float)impulseWorldPerSec;
        entity.VelocityXWorldPerSec = velocity.X;
        entity.VelocityYWorldPerSec = velocity.Y;
    }

    private static void ApplyContactVelocityResponse(SimulationEntity entity, Vector2 separationVector, double outwardImpulseWorldPerSec)
    {
        if (separationVector.LengthSquared() <= 1e-10f)
        {
            return;
        }

        Vector2 normal = Vector2.Normalize(separationVector);
        Vector2 velocity = new((float)entity.VelocityXWorldPerSec, (float)entity.VelocityYWorldPerSec);
        float intoContact = Vector2.Dot(velocity, normal);
        if (intoContact < 0f)
        {
            velocity -= normal * intoContact;
        }

        velocity += normal * (float)Math.Max(0.0, outwardImpulseWorldPerSec);
        entity.VelocityXWorldPerSec = velocity.X;
        entity.VelocityYWorldPerSec = velocity.Y;
    }

    private static bool CanOccupyTerrainFootprint(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double centerX,
        double centerY,
        double referenceHeight,
        double maxStepHeightM,
        double jumpClearanceM)
    {
        double maxX = runtimeGrid.WidthCells * runtimeGrid.CellWidthWorld;
        double maxY = runtimeGrid.HeightCells * runtimeGrid.CellHeightWorld;
        double allowedRise = maxStepHeightM + jumpClearanceM + 1e-6;
        CollisionFootprint[] footprints = runtimeGrid.CollisionSurface is not null
            ? BuildSupportCollisionFootprints(world, entity, centerX, centerY)
            : BuildCollisionFootprints(world, entity, centerX, centerY);
        if (runtimeGrid.CollisionSurface is not null)
        {
            foreach (CollisionFootprint footprint in footprints)
            {
                double minBlockHeight = Math.Max(0.0, Math.Min(referenceHeight, footprint.MinHeightM) - 0.12);
                double maxBlockHeight = Math.Max(footprint.MaxHeightM, referenceHeight + ResolveCollisionHeightM(entity)) + Math.Max(0.12, entity.AirborneHeightM);
                if (!CanOccupyCollisionSurfaceFootprint(
                        runtimeGrid,
                        entity,
                        footprint,
                        referenceHeight,
                        allowedRise,
                        minBlockHeight,
                        maxBlockHeight))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (CollisionFootprint footprint in footprints)
        {
            double spacing = Math.Max(
                Math.Min(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld) * 0.55,
                Math.Min(footprint.HalfLengthWorld, footprint.HalfWidthWorld) * 0.45);
            int xSteps = Math.Clamp((int)Math.Ceiling(footprint.HalfLengthWorld * 2.0 / Math.Max(spacing, 1e-6)), 2, 5);
            int ySteps = Math.Clamp((int)Math.Ceiling(footprint.HalfWidthWorld * 2.0 / Math.Max(spacing, 1e-6)), 2, 5);

            if (!CanOccupyLocalSample(footprint, 0.0, 0.0))
            {
                return false;
            }

            for (int xi = 0; xi <= xSteps; xi++)
            {
                double localX = -footprint.HalfLengthWorld + footprint.HalfLengthWorld * 2.0 * xi / xSteps;
                if (!CanOccupyLocalSample(footprint, localX, -footprint.HalfWidthWorld)
                    || !CanOccupyLocalSample(footprint, localX, footprint.HalfWidthWorld))
                {
                    return false;
                }
            }

            for (int yi = 1; yi < ySteps; yi++)
            {
                double localY = -footprint.HalfWidthWorld + footprint.HalfWidthWorld * 2.0 * yi / ySteps;
                if (!CanOccupyLocalSample(footprint, -footprint.HalfLengthWorld, localY)
                    || !CanOccupyLocalSample(footprint, footprint.HalfLengthWorld, localY))
                {
                    return false;
                }
            }

            if (!CanOccupyLocalSample(footprint, footprint.HalfLengthWorld * 0.50, 0.0)
                || !CanOccupyLocalSample(footprint, -footprint.HalfLengthWorld * 0.50, 0.0)
                || !CanOccupyLocalSample(footprint, 0.0, footprint.HalfWidthWorld * 0.50)
                || !CanOccupyLocalSample(footprint, 0.0, -footprint.HalfWidthWorld * 0.50))
            {
                return false;
            }
        }

        return true;

        bool CanOccupyLocalSample(CollisionFootprint footprint, double localX, double localY)
        {
            double sampleX = footprint.Center.X + footprint.Forward.X * localX + footprint.Right.X * localY;
            double sampleY = footprint.Center.Y + footprint.Forward.Y * localX + footprint.Right.Y * localY;
            if (sampleX < 0.0 || sampleY < 0.0 || sampleX >= maxX || sampleY >= maxY)
            {
                return false;
            }

            if (runtimeGrid.IsMovementBlockedAt(sampleX, sampleY, referenceHeight, allowedRise))
            {
                return false;
            }

            double sampleHeight = SampleTerrainHeight(runtimeGrid, sampleX, sampleY);
            if (sampleHeight - referenceHeight > allowedRise)
            {
                return false;
            }

            return true;
        }
    }

    private static bool CanOccupyCollisionSurfaceFootprint(
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        CollisionFootprint footprint,
        double referenceHeight,
        double allowedRise,
        double minBlockHeight,
        double maxBlockHeight)
    {
        double maxX = runtimeGrid.WidthCells * runtimeGrid.CellWidthWorld;
        double maxY = runtimeGrid.HeightCells * runtimeGrid.CellHeightWorld;
        bool leggedSupport = UsesLeggedSupportProfile(entity);
        double sampleInset = leggedSupport ? 0.58 : 0.62;
        double sampleHalfLength = footprint.HalfLengthWorld * sampleInset;
        double sampleHalfWidth = footprint.HalfWidthWorld * sampleInset;
        Span<(double LocalX, double LocalY)> samples =
        [
            (0.0, 0.0),
            (sampleHalfLength, 0.0),
            (-sampleHalfLength, 0.0),
            (0.0, sampleHalfWidth),
            (0.0, -sampleHalfWidth),
            (sampleHalfLength, sampleHalfWidth),
            (sampleHalfLength, -sampleHalfWidth),
            (-sampleHalfLength, sampleHalfWidth),
            (-sampleHalfLength, -sampleHalfWidth),
        ];

        int passCount = 0;
        int hardBlockCount = 0;
        int wallOnlyCount = 0;
        bool centerPass = false;
        bool centerHardBlock = false;
        double seamTolerance = leggedSupport ? 0.095 : 0.072;
        double reachableHeight = referenceHeight + allowedRise + seamTolerance;

        for (int index = 0; index < samples.Length; index++)
        {
            double sampleX = footprint.Center.X + footprint.Forward.X * samples[index].LocalX + footprint.Right.X * samples[index].LocalY;
            double sampleY = footprint.Center.Y + footprint.Forward.Y * samples[index].LocalX + footprint.Right.Y * samples[index].LocalY;
            if (sampleX < 0.0 || sampleY < 0.0 || sampleX >= maxX || sampleY >= maxY)
            {
                if (index == 0)
                {
                    return false;
                }

                return false;
            }

            bool hasSupportSample = TrySampleTraversalCollisionSurface(
                runtimeGrid,
                sampleX,
                sampleY,
                referenceHeight,
                allowedRise,
                out TerrainSurfaceSample sample);
            if (!hasSupportSample)
            {
                if (runtimeGrid.HasCollisionSurfaceWallContact(sampleX, sampleY, minBlockHeight, maxBlockHeight, maxCellRadius: 0))
                {
                    wallOnlyCount++;
                }
                else
                {
                    passCount++;
                    centerPass |= index == 0;
                }

                continue;
            }

            if (sample.HeightM > reachableHeight)
            {
                hardBlockCount++;
                centerHardBlock |= index == 0;
                continue;
            }

            if (runtimeGrid.HasCollisionSurfaceWallContact(sampleX, sampleY, minBlockHeight, maxBlockHeight, maxCellRadius: 0)
                && sample.Normal.Y < (leggedSupport ? 0.62f : 0.55f)
                && sample.HeightM > referenceHeight + allowedRise * 0.92 + seamTolerance)
            {
                hardBlockCount++;
                centerHardBlock |= index == 0;
                continue;
            }

            passCount++;
            centerPass |= index == 0;
        }

        if (centerHardBlock)
        {
            return false;
        }

        bool stableCoreSupport = centerPass && hardBlockCount == 0 && passCount >= 3;
        if (centerPass && hardBlockCount == 0)
        {
            int supportishCount = passCount + wallOnlyCount;
            int requiredSupportish = leggedSupport ? 6 : 5;
            if (passCount >= 2 && supportishCount >= requiredSupportish)
            {
                return true;
            }
        }

        int requiredPassCount = stableCoreSupport ? 2 : centerPass ? 3 : (leggedSupport ? 3 : 4);
        int allowedHardBlocks = leggedSupport ? 3 : 2;
        int allowedWallOnly = stableCoreSupport
            ? (leggedSupport ? 6 : 5)
            : centerPass
                ? (leggedSupport ? 5 : 4)
                : (leggedSupport ? 4 : 3);
        return passCount >= requiredPassCount
            && hardBlockCount <= allowedHardBlocks
            && wallOnlyCount <= allowedWallOnly;
    }

    private static bool UsesLeggedSupportProfile(SimulationEntity entity)
    {
        return IsBalanceInfantry(entity)
            || string.Equals(entity.RearClimbAssistStyle, "balance_leg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.WheelStyle, "legged", StringComparison.OrdinalIgnoreCase);
    }

    private static double ResolveFootprintSamplePercentile(SimulationEntity entity)
        => UsesLeggedSupportProfile(entity) ? 0.56 : 0.70;

    private static double ResolveOverallSupportPercentile(SimulationEntity entity)
        => UsesLeggedSupportProfile(entity) ? 0.60 : 0.78;

    private static double ResolveMedianHeight(Span<double> heights)
        => ResolveRobustSupportHeightFromSamples(heights, 0.5);

    private static double ResolveRobustSupportHeightFromSamples(Span<double> heights, double percentile)
    {
        if (heights.Length <= 0)
        {
            return 0.0;
        }

        SortAscending(heights);
        double clampedPercentile = Math.Clamp(percentile, 0.0, 1.0);
        int index = (int)Math.Round((heights.Length - 1) * clampedPercentile, MidpointRounding.AwayFromZero);
        index = Math.Clamp(index, 0, heights.Length - 1);
        return heights[index];
    }

    private static void SortAscending(Span<double> values)
    {
        for (int index = 1; index < values.Length; index++)
        {
            double key = values[index];
            int inner = index - 1;
            while (inner >= 0 && values[inner] > key)
            {
                values[inner + 1] = values[inner];
                inner--;
            }

            values[inner + 1] = key;
        }
    }

    private static double ResolveCollisionHalfLengthM(SimulationEntity entity)
    {
        return EntityCollisionModel.ResolveConservativeHalfExtents(entity).HalfLengthM;
    }

    private static double ResolveCollisionHalfWidthM(SimulationEntity entity)
    {
        return EntityCollisionModel.ResolveConservativeHalfExtents(entity).HalfWidthM;
    }

    private bool TryApplyRespawnRecoveryNavigation(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double dt)
    {
        bool inFriendlyRecoveryZone = IsInFriendlyRecoveryZone(entity);
        double healthRatio = entity.MaxHealth > 1e-6
            ? entity.Health / entity.MaxHealth
            : 1.0;
        bool needsRecovery =
            entity.WeakTimerSec > 1e-6
            || entity.RespawnAmmoLockTimerSec > 1e-6
            || healthRatio < 0.35
            || (inFriendlyRecoveryZone && healthRatio < 0.995);
        if (!needsRecovery)
        {
            return false;
        }

        if (inFriendlyRecoveryZone)
        {
            entity.TraversalDirectionDeg = entity.AngleDeg;
            entity.ChassisTargetYawDeg = entity.AngleDeg;
            CacheAutoDrive(GetOrCreateNavigationState(entity.Id, world.GameTimeSec), 0.0, 0.0, entity.AngleDeg);
            ApplyDriveControl(world, entity, 0.0, 0.0, dt, entity.AngleDeg);
            entity.BuyAmmoRequested = false;
            entity.AiDecisionSelected = "recover";
            entity.AiDecision = "回家恢复";
            return true;
        }

        if (!TryResolveRecoveryAnchor(entity, out double targetX, out double targetY))
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        if (TryApplyPlannedNavigation(
            world,
            runtimeGrid,
            entity,
            targetX,
            targetY,
            0.75,
            0.92,
            dt,
            metersPerWorldUnit,
            $"recover:{entity.Team}",
            null))
        {
            entity.AiDecisionSelected = "recover";
            entity.AiDecision = "回家恢复";
            entity.BuyAmmoRequested = false;
            return true;
        }

        double dx = targetX - entity.X;
        double dy = targetY - entity.Y;
        entity.TraversalDirectionDeg = SimulationCombatMath.NormalizeDeg(RadiansToDegrees(Math.Atan2(dy, dx)));
        entity.ChassisTargetYawDeg = entity.TraversalDirectionDeg;
        CacheAutoDrive(GetOrCreateNavigationState(entity.Id, world.GameTimeSec), 0.88, 0.0, entity.TraversalDirectionDeg);
        ApplyDriveControl(world, entity, 0.88, 0.0, dt, entity.TraversalDirectionDeg);
        entity.AiDecisionSelected = "recover";
        entity.AiDecision = "回家恢复";
        entity.BuyAmmoRequested = false;
        return true;
    }

    private bool IsInFriendlyRecoveryZone(SimulationEntity entity)
    {
        return _facilities.Any(region =>
            (string.Equals(region.Type, "supply", StringComparison.OrdinalIgnoreCase)
                || string.Equals(region.Type, "buff_supply", StringComparison.OrdinalIgnoreCase))
            && string.Equals(region.Team, entity.Team, StringComparison.OrdinalIgnoreCase)
            && region.Contains(entity.X, entity.Y));
    }

    private bool TryResolveRecoveryAnchor(SimulationEntity entity, out double targetX, out double targetY)
    {
        FacilityRegion? facility = _facilities
            .Where(region =>
                (string.Equals(region.Type, "supply", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(region.Type, "buff_supply", StringComparison.OrdinalIgnoreCase))
                && string.Equals(region.Team, entity.Team, StringComparison.OrdinalIgnoreCase))
            .OrderBy(region =>
            {
                (double centerX, double centerY) = ResolveRegionCenter(region);
                return DistanceSquared(entity.X, entity.Y, centerX, centerY);
            })
            .FirstOrDefault();
        if (facility is not null)
        {
            (targetX, targetY) = ResolveRegionCenter(facility);
            return true;
        }

        targetX = entity.X;
        targetY = entity.Y;
        return false;
    }

    private static (double X, double Y) ResolveRegionCenter(FacilityRegion region)
    {
        if (region.Points.Count > 0)
        {
            double sumX = 0.0;
            double sumY = 0.0;
            foreach (Point2D point in region.Points)
            {
                sumX += point.X;
                sumY += point.Y;
            }

            double inv = 1.0 / region.Points.Count;
            return (sumX * inv, sumY * inv);
        }

        return ((region.X1 + region.X2) * 0.5, (region.Y1 + region.Y2) * 0.5);
    }

    private static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return dx * dx + dy * dy;
    }

    private static void UpdateChassisTerrainPose(SimulationWorldState world, RuntimeGridData runtimeGrid, SimulationEntity entity, double dt)
    {
        double yawRad = DegreesToRadians(entity.AngleDeg);
        double halfLength = Math.Max(0.10, entity.BodyLengthM * 0.5);
        double halfWidth = Math.Max(0.09, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5);
        double forwardX = Math.Cos(yawRad);
        double forwardY = Math.Sin(yawRad);
        double rightX = -forwardY;
        double rightY = forwardX;

        if (entity.AirborneHeightM > 1e-4)
        {
            if (IsBalanceInfantry(entity))
            {
                ApplyChassisAttitudeTarget(entity, 0.0, 0.0, 0.82, 0.82, dt);
                return;
            }

            double horizontalSpeedWorld = Math.Sqrt(
                entity.VelocityXWorldPerSec * entity.VelocityXWorldPerSec
                + entity.VelocityYWorldPerSec * entity.VelocityYWorldPerSec);
            double forwardSpeedWorld = entity.VelocityXWorldPerSec * forwardX + entity.VelocityYWorldPerSec * forwardY;
            double rightSpeedWorld = entity.VelocityXWorldPerSec * rightX + entity.VelocityYWorldPerSec * rightY;
            double noseDownBias = Math.Clamp(
                Math.Max(0.0, -entity.VerticalVelocityMps * 2.4 + entity.AirborneHeightM * 8.0 + horizontalSpeedWorld * 0.8),
                0.0,
                26.0);
            if (horizontalSpeedWorld <= 1e-4)
            {
                forwardSpeedWorld = 1.0;
            }

            double airbornePitchDeg = -Math.Sign(forwardSpeedWorld) * noseDownBias;
            double airborneRollDeg = Math.Clamp(-rightSpeedWorld * 12.0, -16.0, 16.0);
            ApplyChassisAttitudeTarget(entity, airbornePitchDeg, airborneRollDeg, 0.18, 0.22, dt);
            return;
        }

        if (entity.TraversalActive)
        {
            ApplyTraversalSupportPose(
                runtimeGrid,
                entity,
                forwardX,
                forwardY,
                rightX,
                rightY,
                halfLength,
                halfWidth,
                dt);
            EnsureVehicleBodyClearanceAboveTerrain(world, runtimeGrid, entity);
            return;
        }

        if (!entity.TraversalActive && entity.AirborneHeightM <= 1e-4)
        {
            bool hasContactPlane = TryResolveWheelContactPlane(
                runtimeGrid,
                entity,
                forwardX,
                forwardY,
                rightX,
                rightY,
                halfLength,
                halfWidth,
                out double planeHeightM,
                out double forwardSlope,
                out double rightSlope);
            if (hasContactPlane)
            {
                double maxReachableRise = ResolveEffectiveTraversalStepHeightM(entity) + 0.08;
                if (planeHeightM - entity.GroundHeightM > maxReachableRise + 1e-6)
                {
                    ApplyChassisAttitudeTarget(entity, 0.0, 0.0, 0.34, 0.34, dt);
                    return;
                }

                // Keep the chassis origin on the wheel contact plane. Rendering uses wheel
                // center height = wheel radius, so this keeps every wheel grounded except
                // while the explicit step traversal animation owns the pose.
                entity.GroundHeightM += (planeHeightM - entity.GroundHeightM) * 0.82;
                double targetPitchFromWheels = Math.Atan2(forwardSlope, 1.0) * 180.0 / Math.PI;
                double targetRollFromWheels = Math.Atan2(rightSlope, 1.0) * 180.0 / Math.PI;
                double pitchBlend = entity.ChassisPitchDeg < -8.0 && targetPitchFromWheels > entity.ChassisPitchDeg
                    ? 0.18
                    : 0.56;
                ApplyChassisAttitudeTarget(
                    entity,
                    Math.Clamp(targetPitchFromWheels, -26.0, 26.0),
                    Math.Clamp(targetRollFromWheels, -22.0, 22.0),
                    pitchBlend,
                    0.56,
                    dt);
                EnsureVehicleBodyClearanceAboveTerrain(world, runtimeGrid, entity);
                return;
            }
        }

        Vector3 normal = Vector3.UnitY;
        if (runtimeGrid.TrySampleFacetCollisionSurface(entity.X, entity.Y, out _, out Vector3 facetNormal, out _))
        {
            normal = facetNormal;
        }

        double frontHeight = SampleTerrainHeight(runtimeGrid, entity.X + forwardX * halfLength, entity.Y + forwardY * halfLength);
        double rearHeight = SampleTerrainHeight(runtimeGrid, entity.X - forwardX * halfLength, entity.Y - forwardY * halfLength);
        double rightHeight = SampleTerrainHeight(runtimeGrid, entity.X + rightX * halfWidth, entity.Y + rightY * halfWidth);
        double leftHeight = SampleTerrainHeight(runtimeGrid, entity.X - rightX * halfWidth, entity.Y - rightY * halfWidth);

        double pitchFromHeights = Math.Atan2(frontHeight - rearHeight, Math.Max(halfLength * 2.0, 1e-3)) * 180.0 / Math.PI;
        double rollFromHeights = Math.Atan2(rightHeight - leftHeight, Math.Max(halfWidth * 2.0, 1e-3)) * 180.0 / Math.PI;
        double normalPitch = Math.Atan2(-normal.Z, Math.Max(0.15f, normal.Y)) * 180.0 / Math.PI;
        double normalRoll = Math.Atan2(normal.X, Math.Max(0.15f, normal.Y)) * 180.0 / Math.PI;

        double targetPitchDeg = Math.Clamp(pitchFromHeights * 0.72 + normalPitch * 0.28, -22.0, 22.0);
        double targetRollDeg = Math.Clamp(rollFromHeights * 0.72 + normalRoll * 0.28, -18.0, 18.0);
        ApplyChassisAttitudeTarget(entity, targetPitchDeg, targetRollDeg, 0.34, 0.34, dt);
        EnsureVehicleBodyClearanceAboveTerrain(world, runtimeGrid, entity);
    }

    private static void ApplyTraversalSupportPose(
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double forwardX,
        double forwardY,
        double rightX,
        double rightY,
        double halfLength,
        double halfWidth,
        double dt)
    {
        double progress = Math.Clamp(entity.TraversalProgress, 0.0, 1.0);
        double startHeight = entity.TraversalStartGroundHeightM;
        double targetHeight = entity.TraversalTargetGroundHeightM;
        double rearBlend = SmoothStep(Math.Clamp((progress - 0.58) / 0.42, 0.0, 1.0));
        double frontSupportHeight = targetHeight;
        double rearSupportHeight = Lerp(startHeight, targetHeight, rearBlend);
        double supportLength = Math.Max(halfLength * 2.0, 1e-3);
        double supportCenterHeight = (frontSupportHeight + rearSupportHeight) * 0.5;
        double targetPitchDeg = Math.Clamp(
            Math.Atan2(frontSupportHeight - rearSupportHeight, supportLength) * 180.0 / Math.PI,
            -24.0,
            24.0);
        double targetRollDeg = 0.0;
        if (TryResolveWheelContactPlane(
            runtimeGrid,
            entity,
            forwardX,
            forwardY,
            rightX,
            rightY,
            halfLength,
            halfWidth,
            out double planeHeightM,
            out double forwardSlope,
            out double rightSlope))
        {
            double wheelPitchDeg = Math.Atan2(forwardSlope, 1.0) * 180.0 / Math.PI;
            double wheelRollDeg = Math.Atan2(rightSlope, 1.0) * 180.0 / Math.PI;
            supportCenterHeight = Lerp(supportCenterHeight, planeHeightM, 0.62);
            targetPitchDeg = Math.Clamp(Lerp(targetPitchDeg, wheelPitchDeg, 0.42), -26.0, 26.0);
            targetRollDeg = Math.Clamp(wheelRollDeg, -20.0, 20.0);
        }

        entity.GroundHeightM += (supportCenterHeight - entity.GroundHeightM) * 0.90;
        ApplyChassisAttitudeTarget(entity, targetPitchDeg, targetRollDeg, 0.78, 0.72, dt);
    }

    private static void ApplyChassisAttitudeTarget(
        SimulationEntity entity,
        double targetPitchDeg,
        double targetRollDeg,
        double pitchBlend,
        double rollBlend,
        double dt)
    {
        double maxPitchDelta = ResolveChassisAttitudeRateLimitDegPerSec(entity, pitch: true) * Math.Max(dt, 1.0 / 240.0);
        double maxRollDelta = ResolveChassisAttitudeRateLimitDegPerSec(entity, pitch: false) * Math.Max(dt, 1.0 / 240.0);
        double blendedPitch = entity.ChassisPitchDeg + (targetPitchDeg - entity.ChassisPitchDeg) * Math.Clamp(pitchBlend, 0.0, 1.0);
        double blendedRoll = entity.ChassisRollDeg + (targetRollDeg - entity.ChassisRollDeg) * Math.Clamp(rollBlend, 0.0, 1.0);
        entity.ChassisPitchDeg = MoveToward(entity.ChassisPitchDeg, blendedPitch, maxPitchDelta);
        entity.ChassisRollDeg = MoveToward(entity.ChassisRollDeg, blendedRoll, maxRollDelta);
    }

    private static void EnsureVehicleBodyClearanceAboveTerrain(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity)
    {
        if (SimulationCombatMath.IsStructure(entity) || entity.AirborneHeightM > 1e-4)
        {
            return;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        ResolveVehicleBodyAxes(entity.AngleDeg, entity.ChassisPitchDeg, entity.ChassisRollDeg, out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector3 chassisOriginM = new(
            (float)(entity.X * metersPerWorldUnit),
            (float)(entity.GroundHeightM + Math.Max(0.0, entity.AirborneHeightM)),
            (float)(entity.Y * metersPerWorldUnit));
        float halfLengthM = (float)Math.Max(0.10, entity.BodyLengthM * 0.5 * 0.98);
        float halfWidthM = (float)Math.Max(0.09, entity.BodyWidthM * Math.Max(0.2, entity.BodyRenderWidthScale) * 0.5 * 0.98);
        float bodyBaseM = (float)Math.Max(0.0, Math.Min(entity.BodyClearanceM, 0.42));
        const float clearanceM = 0.012f;
        Span<Vector2> samples =
        [
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(-1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(0f, -1f),
            new Vector2(1f, 1f),
            new Vector2(1f, -1f),
            new Vector2(-1f, 1f),
            new Vector2(-1f, -1f),
        ];

        float requiredLiftM = 0f;
        foreach (Vector2 sample in samples)
        {
            Vector3 bodyPointM =
                chassisOriginM
                + forward * (sample.X * halfLengthM)
                + right * (sample.Y * halfWidthM)
                + up * bodyBaseM;
            double sampleWorldX = bodyPointM.X / metersPerWorldUnit;
            double sampleWorldY = bodyPointM.Z / metersPerWorldUnit;
            double terrainHeightM = SampleTerrainHeight(runtimeGrid, sampleWorldX, sampleWorldY);
            requiredLiftM = Math.Max(requiredLiftM, (float)(terrainHeightM + clearanceM - bodyPointM.Y));
        }

        if (requiredLiftM > 1e-4f)
        {
            double allowedLiftM = ResolveEffectiveTraversalStepHeightM(entity) + 0.08;
            if (requiredLiftM > allowedLiftM + 1e-6)
            {
                return;
            }

            entity.GroundHeightM += requiredLiftM;
        }
    }

    private static void NormalizeUnreachableVerticalAnchor(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity)
    {
        if (SimulationCombatMath.IsStructure(entity))
        {
            return;
        }

        double existingAirborneHeight = Math.Max(0.0, entity.AirborneHeightM);
        double referenceHeight = SampleTerrainHeight(runtimeGrid, entity.X, entity.Y);
        double reachableGroundHeight = ResolveFootprintSupportHeight(
            world,
            runtimeGrid,
            entity,
            entity.X,
            entity.Y,
            referenceHeight,
            ResolveEffectiveTraversalStepHeightM(entity) + ResolveTerrainClearanceAllowanceM(entity));
        double allowedAnchorErrorM = ResolveEffectiveTraversalStepHeightM(entity) + 0.12;
        if (entity.GroundHeightM <= reachableGroundHeight + allowedAnchorErrorM)
        {
            return;
        }

        entity.GroundHeightM = reachableGroundHeight;
        entity.AirborneHeightM = Math.Min(ResolveMaxAirborneHeightM(entity), existingAirborneHeight);
        entity.VerticalVelocityMps = entity.AirborneHeightM > 1e-4
            ? Math.Min(0.0, entity.VerticalVelocityMps)
            : 0.0;
        entity.LedgeLaunchTimerSec = 0.0;
    }

    private static void ResolveVehicleBodyAxes(
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

    private static double ResolveChassisAttitudeRateLimitDegPerSec(SimulationEntity entity, bool pitch)
    {
        double baseRate = entity.TraversalActive || entity.AirborneHeightM > 1e-4
            ? (pitch ? 118.0 : 130.0)
            : (pitch ? 86.0 : 96.0);
        if (IsStandardMecanumPowerRole(entity))
        {
            baseRate *= entity.TraversalActive ? 1.18 : 1.12;
        }

        return IsBalanceInfantry(entity) ? baseRate * 0.62 : baseRate;
    }

    private static double MoveToward(double current, double target, double maxDelta)
    {
        double delta = target - current;
        if (Math.Abs(delta) <= maxDelta)
        {
            return target;
        }

        return current + Math.Sign(delta) * Math.Max(0.0, maxDelta);
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static bool TryResolveWheelContactPlane(
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double forwardX,
        double forwardY,
        double rightX,
        double rightY,
        double halfLength,
        double halfWidth,
        out double planeHeightM,
        out double forwardSlope,
        out double rightSlope)
    {
        planeHeightM = entity.GroundHeightM;
        forwardSlope = 0.0;
        rightSlope = 0.0;

        IReadOnlyList<(double X, double Y)> wheelOffsets = entity.WheelOffsetsM;
        int count = wheelOffsets.Count > 0 ? wheelOffsets.Count : 4;
        if (count < 2)
        {
            return false;
        }

        var contactSamples = new WheelContactSample[count];
        Span<double> rawHeights = count <= 12 ? stackalloc double[count] : new double[count];
        bool leggedSupport = UsesLeggedSupportProfile(entity);
        double sampleClampM = leggedSupport ? 0.08 : 0.12;

        for (int index = 0; index < count; index++)
        {
            double localX;
            double localY;
            if (wheelOffsets.Count > 0)
            {
                (localX, localY) = wheelOffsets[index];
                localY *= Math.Max(0.35, entity.BodyRenderWidthScale);
            }
            else
            {
                localX = (index < 2 ? 1.0 : -1.0) * halfLength * 0.78;
                localY = (index % 2 == 0 ? -1.0 : 1.0) * halfWidth * 0.86;
            }

            double sampleX = entity.X + forwardX * localX + rightX * localY;
            double sampleY = entity.Y + forwardY * localX + rightY * localY;
            double height = SampleTerrainHeight(runtimeGrid, sampleX, sampleY);
            if (runtimeGrid.CollisionSurface is not null
                && runtimeGrid.TrySampleCollisionSurface(sampleX, sampleY, out TerrainSurfaceSample surfaceSample, allowNeighborExpansion: true))
            {
                height = surfaceSample.HeightM;
            }

            contactSamples[index] = new WheelContactSample(localX, localY, height);
            rawHeights[index] = height;
        }

        double medianHeight = ResolveMedianHeight(rawHeights[..count]);
        double maxReachableRise = ResolveEffectiveTraversalStepHeightM(entity) + 0.08;
        if (medianHeight - entity.GroundHeightM > maxReachableRise + 1e-6)
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            WheelContactSample sample = contactSamples[index];
            contactSamples[index] = sample with
            {
                HeightM = Math.Clamp(sample.HeightM, medianHeight - sampleClampM, medianHeight + sampleClampM),
            };
        }

        if (TryResolveSupportPlaneFromWheelSamples(entity, contactSamples, out planeHeightM, out forwardSlope, out rightSlope))
        {
            if (leggedSupport)
            {
                planeHeightM = Math.Clamp(planeHeightM, entity.GroundHeightM - 0.05, entity.GroundHeightM + 0.08);
            }
            else
            {
                planeHeightM = Math.Clamp(planeHeightM, entity.GroundHeightM - 0.08, entity.GroundHeightM + 0.12);
            }

            return true;
        }

        return false;
    }

    private static bool TryResolveSupportPlaneFromWheelSamples(
        SimulationEntity entity,
        WheelContactSample[] samples,
        out double planeHeightM,
        out double forwardSlope,
        out double rightSlope)
    {
        planeHeightM = 0.0;
        forwardSlope = 0.0;
        rightSlope = 0.0;
        if (samples.Length <= 0)
        {
            return false;
        }

        bool leggedSupport = UsesLeggedSupportProfile(entity);
        double aboveToleranceM = leggedSupport ? 0.024 : 0.030;
        double supportToleranceM = leggedSupport ? 0.030 : 0.036;
        double hangToleranceM = leggedSupport ? 0.110 : 0.140;
        int bestSupportCount = -1;
        int bestAboveCount = int.MaxValue;
        int bestHangCount = int.MaxValue;
        double bestPenalty = double.PositiveInfinity;
        double bestArea = double.NegativeInfinity;
        bool foundStablePlane = false;

        if (samples.Length >= 3)
        {
            for (int i = 0; i < samples.Length - 2; i++)
            {
                for (int j = i + 1; j < samples.Length - 1; j++)
                {
                    for (int k = j + 1; k < samples.Length; k++)
                    {
                        if (!TryResolvePlaneFromWheelTriple(
                                samples[i],
                                samples[j],
                                samples[k],
                                out double candidateHeight,
                                out double candidateForwardSlope,
                                out double candidateRightSlope,
                                out double triangleArea))
                        {
                            continue;
                        }

                        int supportCount = 0;
                        int aboveCount = 0;
                        int hangCount = 0;
                        double penalty = 0.0;
                        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
                        {
                            WheelContactSample sample = samples[sampleIndex];
                            double predictedHeight =
                                candidateHeight
                                + candidateForwardSlope * sample.LocalX
                                + candidateRightSlope * sample.LocalY;
                            double delta = sample.HeightM - predictedHeight;
                            if (delta > aboveToleranceM)
                            {
                                aboveCount++;
                                penalty += 200.0 + (delta - aboveToleranceM) * 2400.0;
                                continue;
                            }

                            if (delta >= -supportToleranceM)
                            {
                                supportCount++;
                                penalty += Math.Abs(delta) * 24.0;
                                continue;
                            }

                            if (delta < -hangToleranceM)
                            {
                                hangCount++;
                                penalty += 12.0 + (-delta - hangToleranceM) * 180.0;
                                continue;
                            }

                            penalty += (-delta - supportToleranceM) * 30.0;
                        }

                        if (aboveCount > 1)
                        {
                            continue;
                        }

                        if (supportCount < 3 && samples.Length >= 3)
                        {
                            continue;
                        }

                        bool better =
                            supportCount > bestSupportCount
                            || (supportCount == bestSupportCount && aboveCount < bestAboveCount)
                            || (supportCount == bestSupportCount && aboveCount == bestAboveCount && hangCount < bestHangCount)
                            || (supportCount == bestSupportCount && aboveCount == bestAboveCount && hangCount == bestHangCount && penalty < bestPenalty - 1e-6)
                            || (supportCount == bestSupportCount && aboveCount == bestAboveCount && hangCount == bestHangCount && Math.Abs(penalty - bestPenalty) <= 1e-6 && triangleArea > bestArea);
                        if (!better)
                        {
                            continue;
                        }

                        bestSupportCount = supportCount;
                        bestAboveCount = aboveCount;
                        bestHangCount = hangCount;
                        bestPenalty = penalty;
                        bestArea = triangleArea;
                        planeHeightM = candidateHeight;
                        forwardSlope = candidateForwardSlope;
                        rightSlope = candidateRightSlope;
                        foundStablePlane = true;
                    }
                }
            }
        }

        if (foundStablePlane)
        {
            return true;
        }

        return TryResolveBestFitWheelPlane(samples, out planeHeightM, out forwardSlope, out rightSlope);
    }

    private static bool TryResolvePlaneFromWheelTriple(
        WheelContactSample a,
        WheelContactSample b,
        WheelContactSample c,
        out double planeHeightM,
        out double forwardSlope,
        out double rightSlope,
        out double triangleArea)
    {
        planeHeightM = 0.0;
        forwardSlope = 0.0;
        rightSlope = 0.0;
        triangleArea = Math.Abs(
            (b.LocalX - a.LocalX) * (c.LocalY - a.LocalY)
            - (b.LocalY - a.LocalY) * (c.LocalX - a.LocalX));
        if (triangleArea <= 1e-5)
        {
            return false;
        }

        Vector3 pa = new((float)a.LocalX, (float)a.LocalY, (float)a.HeightM);
        Vector3 pb = new((float)b.LocalX, (float)b.LocalY, (float)b.HeightM);
        Vector3 pc = new((float)c.LocalX, (float)c.LocalY, (float)c.HeightM);
        Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
        if (Math.Abs(normal.Z) <= 1e-6f)
        {
            return false;
        }

        float d = -Vector3.Dot(normal, pa);
        forwardSlope = -normal.X / normal.Z;
        rightSlope = -normal.Y / normal.Z;
        planeHeightM = -d / normal.Z;
        return double.IsFinite(planeHeightM)
            && double.IsFinite(forwardSlope)
            && double.IsFinite(rightSlope);
    }

    private static bool TryResolveBestFitWheelPlane(
        WheelContactSample[] samples,
        out double planeHeightM,
        out double forwardSlope,
        out double rightSlope)
    {
        planeHeightM = 0.0;
        forwardSlope = 0.0;
        rightSlope = 0.0;
        if (samples.Length <= 0)
        {
            return false;
        }

        double sumX = 0.0;
        double sumY = 0.0;
        double sumH = 0.0;
        double sumXX = 0.0;
        double sumYY = 0.0;
        double sumXY = 0.0;
        double sumXH = 0.0;
        double sumYH = 0.0;
        for (int index = 0; index < samples.Length; index++)
        {
            WheelContactSample sample = samples[index];
            sumX += sample.LocalX;
            sumY += sample.LocalY;
            sumH += sample.HeightM;
            sumXX += sample.LocalX * sample.LocalX;
            sumYY += sample.LocalY * sample.LocalY;
            sumXY += sample.LocalX * sample.LocalY;
            sumXH += sample.LocalX * sample.HeightM;
            sumYH += sample.LocalY * sample.HeightM;
        }

        double invCount = 1.0 / samples.Length;
        double meanX = sumX * invCount;
        double meanY = sumY * invCount;
        double meanH = sumH * invCount;
        double centeredXX = sumXX - sumX * meanX;
        double centeredYY = sumYY - sumY * meanY;
        double centeredXY = sumXY - sumX * meanY;
        double centeredXH = sumXH - sumX * meanH;
        double centeredYH = sumYH - sumY * meanH;
        double det = centeredXX * centeredYY - centeredXY * centeredXY;
        if (Math.Abs(det) <= 1e-8)
        {
            planeHeightM = meanH;
            return true;
        }

        forwardSlope = (centeredXH * centeredYY - centeredYH * centeredXY) / det;
        rightSlope = (centeredYH * centeredXX - centeredXH * centeredXY) / det;
        planeHeightM = meanH - forwardSlope * meanX - rightSlope * meanY;
        return double.IsFinite(planeHeightM)
            && double.IsFinite(forwardSlope)
            && double.IsFinite(rightSlope);
    }

    private static int ResolveTranslationSubsteps(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double dt)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double speedMps = Math.Sqrt(
            entity.VelocityXWorldPerSec * entity.VelocityXWorldPerSec
            + entity.VelocityYWorldPerSec * entity.VelocityYWorldPerSec) * metersPerWorldUnit;
        double verticalSpeedMps = Math.Abs(entity.VerticalVelocityMps);
        double cellSizeM = Math.Max(
            0.08,
            Math.Min(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld) * metersPerWorldUnit * 0.75);
        int substeps = (int)Math.Ceiling(speedMps * Math.Max(dt, 0.01) / cellSizeM);
        int verticalSubsteps = (int)Math.Ceiling(verticalSpeedMps * Math.Max(dt, 0.01) / 0.06);
        if (entity.TraversalActive)
        {
            return Math.Clamp(Math.Max(substeps, verticalSubsteps), 1, 3);
        }

        if (entity.AirborneHeightM > 1e-4 || entity.TraversalActive)
        {
            substeps = Math.Max(substeps, verticalSubsteps);
        }

        return Math.Clamp(substeps, 1, entity.AirborneHeightM > 1e-4 ? 16 : 10);
    }

    private static bool ShouldKeepChassisLevelDuringStep(SimulationEntity entity)
        => string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase);

    private static void StartTraversal(
        SimulationEntity entity,
        double targetX,
        double targetY,
        double startHeight,
        double targetHeight)
    {
        entity.TraversalActive = true;
        entity.TraversalProgress = 0.0;
        entity.TraversalStartX = entity.X;
        entity.TraversalStartY = entity.Y;
        entity.TraversalTargetX = targetX;
        entity.TraversalTargetY = targetY;
        entity.TraversalStartGroundHeightM = startHeight;
        entity.TraversalTargetGroundHeightM = targetHeight;
    }

    private static void UpdateTraversal(SimulationEntity entity, double dt)
    {
        double duration = Math.Max(0.35, entity.StepClimbDurationSec);
        entity.TraversalProgress = Math.Min(1.0, entity.TraversalProgress + dt / duration);
        double eased = entity.TraversalProgress < 0.5
            ? 2.0 * entity.TraversalProgress * entity.TraversalProgress
            : 1.0 - Math.Pow(-2.0 * entity.TraversalProgress + 2.0, 2.0) * 0.5;
        entity.X = Lerp(entity.TraversalStartX, entity.TraversalTargetX, eased);
        entity.Y = Lerp(entity.TraversalStartY, entity.TraversalTargetY, eased);
        entity.GroundHeightM = Lerp(entity.TraversalStartGroundHeightM, entity.TraversalTargetGroundHeightM, eased);

        if (entity.TraversalProgress >= 1.0 - 1e-6)
        {
            entity.TraversalActive = false;
            entity.TraversalProgress = 0.0;
            entity.X = entity.TraversalTargetX;
            entity.Y = entity.TraversalTargetY;
            entity.GroundHeightM = entity.TraversalTargetGroundHeightM;
        }
    }

    private static bool HasEntityCollisionAt(
        SimulationWorldState world,
        SimulationEntity entity,
        double nextX,
        double nextY,
        out SimulationEntity? blockingEntity)
    {
        if (ResolveEntityCollisionAt(world, entity, nextX, nextY, out EntityContactResolution resolution))
        {
            blockingEntity = resolution.BlockingEntity;
            return true;
        }

        blockingEntity = null;
        return false;
    }

    private static bool ResolveEntityCollisionAt(
        SimulationWorldState world,
        SimulationEntity entity,
        double nextX,
        double nextY,
        out EntityContactResolution resolution)
    {
        resolution = default;
        CollisionFootprint footprint = BuildCollisionFootprint(world, entity, nextX, nextY);
        CollisionFootprint[] partFootprints = BuildCollisionFootprints(world, entity, nextX, nextY);
        CollisionFootprint[] currentPartFootprints = BuildCollisionFootprints(world, entity, entity.X, entity.Y);
        foreach (SimulationEntity other in world.Entities)
        {
            if (ReferenceEquals(entity, other)
                || !other.IsAlive
                || !IsCollidableEntity(other))
            {
                continue;
            }

            CollisionFootprint otherFootprint = BuildCollisionFootprint(world, other, other.X, other.Y);
            double dx = nextX - other.X;
            double dy = nextY - other.Y;
            double broadPhaseDistanceSq = dx * dx + dy * dy;
            double broadPhaseRadius = Math.Max(0.08, footprint.BoundingRadiusWorld + otherFootprint.BoundingRadiusWorld + 0.02);
            if (broadPhaseDistanceSq > broadPhaseRadius * broadPhaseRadius)
            {
                continue;
            }

            CollisionFootprint[] otherPartFootprints = BuildCollisionFootprints(world, other, other.X, other.Y);
            Vector2 bestSeparation = Vector2.Zero;
            float bestPenetration = 0f;
            for (int partIndex = 0; partIndex < partFootprints.Length; partIndex++)
            {
                CollisionFootprint partFootprint = partFootprints[partIndex];
                CollisionFootprint currentPartFootprint = currentPartFootprints[Math.Min(partIndex, currentPartFootprints.Length - 1)];
                foreach (CollisionFootprint otherPartFootprint in otherPartFootprints)
                {
                    if (!VerticalIntervalsOverlap(partFootprint, otherPartFootprint)
                        || !IntersectsFootprint(partFootprint, otherPartFootprint))
                    {
                        continue;
                    }

                    if (IntersectsFootprint(currentPartFootprint, otherPartFootprint))
                    {
                        Vector2 movement = partFootprint.Center - currentPartFootprint.Center;
                        Vector2 separation = currentPartFootprint.Center - otherPartFootprint.Center;
                        double currentDistanceSq = DistanceSquared(currentPartFootprint.Center, otherPartFootprint.Center);
                        double nextDistanceSq = DistanceSquared(partFootprint.Center, otherPartFootprint.Center);
                        double outwardDot = Vector2.Dot(movement, separation);
                        if (nextDistanceSq + 1e-6 >= currentDistanceSq || outwardDot >= -1e-5)
                        {
                            continue;
                        }
                    }

                    Vector2 candidateSeparation = ComputeMinimumTranslationVector(partFootprint, otherPartFootprint);
                    float penetration = candidateSeparation.Length();
                    if (penetration > bestPenetration)
                    {
                        bestPenetration = penetration;
                        bestSeparation = candidateSeparation;
                    }
                }
            }

            if (bestPenetration <= 1e-6f)
            {
                continue;
            }

            Vector2 separationVector = bestSeparation;
            resolution = new EntityContactResolution(
                other,
                nextX + separationVector.X,
                nextY + separationVector.Y,
                separationVector,
                separationVector.Length());
            return true;
        }

        return false;
    }

    private bool HasStaticCollisionAt(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double nextX,
        double nextY)
    {
        return HasStaticStructureCollisionAt(world, entity, nextX, nextY)
            || HasBlockingFacilityContactAt(world, runtimeGrid, entity, nextX, nextY);
    }

    private bool TryApplyEntityContactResolution(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        EntityContactResolution resolution,
        double currentHeightOverrideM)
    {
        double currentHeight = currentHeightOverrideM > -1e8
            ? currentHeightOverrideM
            : ResolveTraversalGroundHeight(world, runtimeGrid, entity, entity.X, entity.Y);
        double maxStep = ResolveEffectiveTraversalStepHeightM(entity);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(entity);
        double resolvedX = resolution.ResolvedX;
        double resolvedY = resolution.ResolvedY;
        ClampToMap(runtimeGrid, ref resolvedX, ref resolvedY);

        if (!CanOccupyTerrainFootprint(world, runtimeGrid, entity, resolvedX, resolvedY, currentHeight, maxStep, jumpClearance))
        {
            if (!TryMoveToReachableContactPosition(world, runtimeGrid, entity, entity.X, entity.Y, resolvedX, resolvedY, currentHeight, maxStep, jumpClearance))
            {
                return false;
            }
        }
        else
        {
            entity.X = resolvedX;
            entity.Y = resolvedY;
            entity.GroundHeightM = ResolveTraversalGroundHeight(world, runtimeGrid, entity, resolvedX, resolvedY);
        }

        ApplyContactVelocityResponse(entity, resolution.SeparationVector, entity.AirborneHeightM > 1e-4 ? 0.004 : 0.018);
        TryApplySoftBodyPush(world, runtimeGrid, entity, resolution.BlockingEntity, resolution.SeparationVector);
        return true;
    }

    private bool TryResolveStaticContactAt(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double nextX,
        double nextY,
        double currentHeight,
        out StaticContactResolution resolution)
    {
        resolution = default;
        CollisionFootprint[] footprints = BuildCollisionFootprints(world, entity, nextX, nextY);
        Vector2 accumulated = Vector2.Zero;
        string reason = string.Empty;
        bool hit = false;
        int maxIterations = runtimeGrid.CollisionSurface is not null ? 2 : 3;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            if (!TryFindBestStaticContact(world, runtimeGrid, entity, footprints, currentHeight, out Vector2 mtv, out string contactReason))
            {
                break;
            }

            hit = true;
            accumulated += mtv;
            reason = contactReason;
            for (int index = 0; index < footprints.Length; index++)
            {
                footprints[index] = footprints[index] with { Center = footprints[index].Center + mtv };
            }
        }

        if (!hit || accumulated.LengthSquared() <= 1e-10f)
        {
            return false;
        }

        resolution = new StaticContactResolution(
            reason,
            nextX + accumulated.X,
            nextY + accumulated.Y,
            accumulated,
            accumulated.Length());
        return true;
    }

    private bool TryFindBestStaticContact(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        IReadOnlyList<CollisionFootprint> footprints,
        double currentHeight,
        out Vector2 separationVector,
        out string reason)
    {
        separationVector = Vector2.Zero;
        reason = string.Empty;
        bool found = false;
        float bestPenetration = 0f;
        foreach (CollisionFootprint footprint in footprints)
        {
            if (!TryFindStaticContact(world, runtimeGrid, entity, footprint, currentHeight, out Vector2 mtv, out string contactReason))
            {
                continue;
            }

            float penetration = mtv.Length();
            if (penetration <= bestPenetration)
            {
                continue;
            }

            bestPenetration = penetration;
            separationVector = mtv;
            reason = contactReason;
            found = true;
        }

        return found;
    }

    private bool TryApplyStaticContactResolution(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        StaticContactResolution resolution,
        double currentHeightOverrideM)
    {
        double currentHeight = currentHeightOverrideM > -1e8
            ? currentHeightOverrideM
            : ResolveTraversalGroundHeight(world, runtimeGrid, entity, entity.X, entity.Y);
        double maxStep = ResolveEffectiveTraversalStepHeightM(entity);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(entity);
        double resolvedX = resolution.ResolvedX;
        double resolvedY = resolution.ResolvedY;
        ClampToMap(runtimeGrid, ref resolvedX, ref resolvedY);

        bool reachable = CanOccupyTerrainFootprint(world, runtimeGrid, entity, resolvedX, resolvedY, currentHeight, maxStep, jumpClearance)
            && !HasEntityCollisionAt(world, entity, resolvedX, resolvedY, out _)
            && !HasStaticCollisionAt(world, runtimeGrid, entity, resolvedX, resolvedY);
        if (!reachable)
        {
            if (!TryMoveToReachableContactPosition(world, runtimeGrid, entity, entity.X, entity.Y, resolvedX, resolvedY, currentHeight, maxStep, jumpClearance))
            {
                return false;
            }
        }
        else
        {
            entity.X = resolvedX;
            entity.Y = resolvedY;
            entity.GroundHeightM = ResolveTraversalGroundHeight(world, runtimeGrid, entity, resolvedX, resolvedY);
        }

        ApplyContactVelocityResponse(entity, resolution.SeparationVector, entity.AirborneHeightM > 1e-4 ? 0.003 : 0.012);
        return true;
    }

    private bool TryMoveToReachableContactPosition(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double startX,
        double startY,
        double targetX,
        double targetY,
        double currentHeight,
        double maxStep,
        double jumpClearance)
    {
        double bestX = startX;
        double bestY = startY;
        bool found = false;
        double low = 0.0;
        double high = 1.0;
        int maxIterations = runtimeGrid.CollisionSurface is not null ? 6 : 10;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            double mid = (low + high) * 0.5;
            double sampleX = Lerp(startX, targetX, mid);
            double sampleY = Lerp(startY, targetY, mid);
            ClampToMap(runtimeGrid, ref sampleX, ref sampleY);
            bool valid = CanOccupyTerrainFootprint(world, runtimeGrid, entity, sampleX, sampleY, currentHeight, maxStep, jumpClearance)
                && !HasEntityCollisionAt(world, entity, sampleX, sampleY, out _)
                && !HasStaticCollisionAt(world, runtimeGrid, entity, sampleX, sampleY);
            if (valid)
            {
                found = true;
                low = mid;
                bestX = sampleX;
                bestY = sampleY;
            }
            else
            {
                high = mid;
            }
        }

        if (!found)
        {
            return false;
        }

        entity.X = bestX;
        entity.Y = bestY;
        entity.GroundHeightM = ResolveTraversalGroundHeight(world, runtimeGrid, entity, bestX, bestY);
        double retain = entity.AirborneHeightM > 1e-4 ? 0.86 : 0.48;
        entity.VelocityXWorldPerSec *= retain;
        entity.VelocityYWorldPerSec *= retain;
        return true;
    }

    private void TryApplySoftBodyPush(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity mover,
        SimulationEntity other,
        Vector2 separationVector)
    {
        if (!IsMovableEntity(other) || SimulationCombatMath.IsStructure(other))
        {
            return;
        }

        double currentHeight = ResolveTraversalGroundHeight(world, runtimeGrid, other, other.X, other.Y);
        double maxStep = ResolveEffectiveTraversalStepHeightM(other);
        double jumpClearance = ResolveTerrainClearanceAllowanceM(other);
        Vector2 rawPush = separationVector.LengthSquared() > 1e-8f
            ? separationVector
            : new Vector2((float)(other.X - mover.X), (float)(other.Y - mover.Y));
        Vector2 pushDir = rawPush.LengthSquared() > 1e-8f
            ? Vector2.Normalize(rawPush)
            : Vector2.UnitX;
        double pushDistance = Math.Min(0.035, Math.Max(0.004, separationVector.Length() * 0.35));
        double nextX = other.X + pushDir.X * (float)pushDistance;
        double nextY = other.Y + pushDir.Y * (float)pushDistance;
        ClampToMap(runtimeGrid, ref nextX, ref nextY);
        if (!CanOccupyTerrainFootprint(world, runtimeGrid, other, nextX, nextY, currentHeight, maxStep, jumpClearance)
            || HasEntityCollisionAt(world, other, nextX, nextY, out _)
            || HasStaticCollisionAt(world, runtimeGrid, other, nextX, nextY))
        {
            return;
        }

        other.X = nextX;
        other.Y = nextY;
        other.GroundHeightM = ResolveTraversalGroundHeight(world, runtimeGrid, other, nextX, nextY);
        other.VelocityXWorldPerSec += pushDir.X * 0.04;
        other.VelocityYWorldPerSec += pushDir.Y * 0.04;
    }

    private bool HasBlockingFacilityContactAt(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double nextX,
        double nextY)
    {
        foreach (CollisionFootprint footprint in BuildCollisionFootprints(world, entity, nextX, nextY))
        {
            if (TryFindFacilityContact(world, runtimeGrid, footprint, out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryFindStaticContact(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        CollisionFootprint footprint,
        double currentHeight,
        out Vector2 separationVector,
        out string reason)
    {
        _ = world;
        _ = entity;
        separationVector = Vector2.Zero;
        reason = string.Empty;
        bool found = false;
        float bestPenetration = 0f;

        if (runtimeGrid.CollisionSurface is not null)
        {
            double maxStep = ResolveEffectiveTraversalStepHeightM(entity);
            double jumpClearance = ResolveTerrainClearanceAllowanceM(entity);
            if (TryFindCollisionSurfaceWallContact(
                    runtimeGrid,
                    footprint,
                    currentHeight,
                    maxStep + jumpClearance,
                    UsesLeggedSupportProfile(entity),
                    out Vector2 wallMtv,
                    out float wallPenetration))
            {
                bestPenetration = wallPenetration;
                separationVector = wallMtv;
                reason = "collision_surface_wall_contact";
                found = true;
            }
        }
        else if (TryFindBlockingGridCellContact(runtimeGrid, footprint, currentHeight, out Vector2 gridMtv, out float gridPenetration))
        {
            bestPenetration = gridPenetration;
            separationVector = gridMtv;
            reason = "static_grid_contact";
            found = true;
        }

        if (TryFindFacilityContact(world, runtimeGrid, footprint, out Vector2 facilityMtv, out string facilityReason))
        {
            float penetration = facilityMtv.Length();
            if (penetration > bestPenetration)
            {
                bestPenetration = penetration;
                separationVector = facilityMtv;
                reason = facilityReason;
                found = true;
            }
        }

        return found;
    }

    private static bool TryFindCollisionSurfaceWallContact(
        RuntimeGridData runtimeGrid,
        CollisionFootprint footprint,
        double referenceHeightM,
        double allowedRiseM,
        bool leggedSupport,
        out Vector2 separationVector,
        out float bestPenetration)
    {
        separationVector = Vector2.Zero;
        bestPenetration = 0f;
        int hitCount = 0;
        bool centerHit = false;
        Vector2 accumulatedAway = Vector2.Zero;
        double insetLength = footprint.HalfLengthWorld * 0.60;
        double insetWidth = footprint.HalfWidthWorld * 0.60;
        Span<(double LocalX, double LocalY)> samples =
        [
            (0.0, 0.0),
            (insetLength, 0.0),
            (-insetLength, 0.0),
            (0.0, insetWidth),
            (0.0, -insetWidth),
            (insetLength, insetWidth),
            (insetLength, -insetWidth),
            (-insetLength, insetWidth),
            (-insetLength, -insetWidth),
        ];

        for (int index = 0; index < samples.Length; index++)
        {
            Vector2 sample = footprint.Center
                + footprint.Forward * (float)samples[index].LocalX
                + footprint.Right * (float)samples[index].LocalY;
            if (TrySampleTraversalCollisionSurface(
                    runtimeGrid,
                    sample.X,
                    sample.Y,
                    referenceHeightM,
                    allowedRiseM,
                    out TerrainSurfaceSample support)
                && (support.HeightM <= footprint.MaxHeightM + 0.12
                    || support.HeightM <= referenceHeightM + allowedRiseM + (leggedSupport ? 0.08 : 0.055)))
            {
                continue;
            }

            if (!runtimeGrid.HasCollisionSurfaceWallContact(sample.X, sample.Y, footprint.MinHeightM, footprint.MaxHeightM, maxCellRadius: 0))
            {
                continue;
            }

            hitCount++;
            centerHit |= index == 0;
            Vector2 away = footprint.Center - sample;
            if (away.LengthSquared() <= 1e-8f)
            {
                away = -footprint.Forward;
            }

            accumulatedAway += Vector2.Normalize(away);
        }

        if (hitCount == 0 || (!centerHit && hitCount < 5))
        {
            return false;
        }

        Vector2 resolvedAway = accumulatedAway.LengthSquared() > 1e-8f
            ? Vector2.Normalize(accumulatedAway)
            : -footprint.Forward;
        Vector2 mtv = resolvedAway * 0.014f;
        float penetration = mtv.Length();
        if (penetration <= bestPenetration)
        {
            return false;
        }

        bestPenetration = penetration;
        separationVector = mtv;
        return true;
    }

    private static bool TryFindBlockingGridCellContact(
        RuntimeGridData runtimeGrid,
        CollisionFootprint footprint,
        double currentHeight,
        out Vector2 separationVector,
        out float bestPenetration)
    {
        separationVector = Vector2.Zero;
        bestPenetration = 0f;
        float halfExtentX = (float)(
            Math.Abs(footprint.Forward.X) * footprint.HalfLengthWorld
            + Math.Abs(footprint.Right.X) * footprint.HalfWidthWorld);
        float halfExtentY = (float)(
            Math.Abs(footprint.Forward.Y) * footprint.HalfLengthWorld
            + Math.Abs(footprint.Right.Y) * footprint.HalfWidthWorld);
        float minX = footprint.Center.X - halfExtentX - (float)runtimeGrid.CellWidthWorld;
        float maxX = footprint.Center.X + halfExtentX + (float)runtimeGrid.CellWidthWorld;
        float minY = footprint.Center.Y - halfExtentY - (float)runtimeGrid.CellHeightWorld;
        float maxY = footprint.Center.Y + halfExtentY + (float)runtimeGrid.CellHeightWorld;
        int startCellX = Math.Clamp(WorldToCellX(runtimeGrid, minX), 0, runtimeGrid.WidthCells - 1);
        int endCellX = Math.Clamp(WorldToCellX(runtimeGrid, maxX), 0, runtimeGrid.WidthCells - 1);
        int startCellY = Math.Clamp(WorldToCellY(runtimeGrid, minY), 0, runtimeGrid.HeightCells - 1);
        int endCellY = Math.Clamp(WorldToCellY(runtimeGrid, maxY), 0, runtimeGrid.HeightCells - 1);
        double halfCellWidth = runtimeGrid.CellWidthWorld * 0.5;
        double halfCellHeight = runtimeGrid.CellHeightWorld * 0.5;
        double radius = Math.Sqrt(halfCellWidth * halfCellWidth + halfCellHeight * halfCellHeight);
        for (int cellY = startCellY; cellY <= endCellY; cellY++)
        {
            for (int cellX = startCellX; cellX <= endCellX; cellX++)
            {
                double centerX = (cellX + 0.5) * runtimeGrid.CellWidthWorld;
                double centerY = (cellY + 0.5) * runtimeGrid.CellHeightWorld;
                bool blocked;
                if (runtimeGrid.CollisionSurface is not null)
                {
                    blocked = runtimeGrid.HasCollisionSurfaceWallContact(centerX, centerY, footprint.MinHeightM, footprint.MaxHeightM, maxCellRadius: 1);
                }
                else
                {
                    double referenceHeight = SampleTerrainHeight(runtimeGrid, centerX, centerY);
                    blocked = runtimeGrid.IsMovementBlockedAt(centerX, centerY, referenceHeight, 0.03);
                }

                if (!blocked)
                {
                    continue;
                }

                CollisionFootprint obstacle = new(
                    new Vector2((float)centerX, (float)centerY),
                    Vector2.UnitX,
                    Vector2.UnitY,
                    halfCellWidth,
                    halfCellHeight,
                    currentHeight - 0.5,
                    currentHeight + 4.0,
                    radius);
                if (!IntersectsFootprint(footprint, obstacle))
                {
                    continue;
                }

                Vector2 mtv = ComputeMinimumTranslationVector(footprint, obstacle);
                float penetration = mtv.Length();
                if (penetration <= bestPenetration)
                {
                    continue;
                }

                bestPenetration = penetration;
                separationVector = mtv;
            }
        }

        return bestPenetration > 0f;
    }

    private static void ApplyAirborneInertia(
        SimulationEntity entity,
        double drivePowerLimitW,
        double currentVxMps,
        double currentVyMps,
        double currentSpeedMps,
        double metersPerWorldUnit,
        double dt)
    {
        // Wheels have no ground contact in a ledge drop, so keep horizontal
        // momentum exactly instead of letting motor braking or lateral drive
        // acceleration erase the visible projectile-like flight.
        double newVxMps = currentVxMps;
        double newVyMps = currentVyMps;
        double newSpeedMps = Math.Sqrt(newVxMps * newVxMps + newVyMps * newVyMps);
        if (newSpeedMps > WheelMaxLinearSpeedMps && newSpeedMps > 1e-6)
        {
            double speedScale = WheelMaxLinearSpeedMps / newSpeedMps;
            newVxMps *= speedScale;
            newVyMps *= speedScale;
            newSpeedMps = WheelMaxLinearSpeedMps;
        }

        entity.VelocityXWorldPerSec = newVxMps / Math.Max(metersPerWorldUnit, 1e-6);
        entity.VelocityYWorldPerSec = newVyMps / Math.Max(metersPerWorldUnit, 1e-6);
        entity.EffectiveDrivePowerLimitW = drivePowerLimitW;
        entity.ChassisPowerRatio = 1.0;
        entity.ChassisSpeedLimitMps = Math.Max(entity.ChassisSpeedLimitMps, newSpeedMps);
        entity.ChassisPowerDrawW = Math.Min(entity.ChassisPowerDrawW, 4.5);
        entity.ChassisRpm = newSpeedMps / Math.Max(entity.WheelRadiusM, 0.03) * 9.55;
        entity.MotionBlockReason = string.Empty;
    }

    private bool TryFindFacilityContact(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        CollisionFootprint footprint,
        out Vector2 separationVector,
        out string reason)
    {
        separationVector = Vector2.Zero;
        reason = string.Empty;
        bool found = false;
        float bestPenetration = 0f;
        foreach (FacilityCollisionShape shape in EnsureFacilityCollisionShapes(runtimeGrid))
        {
            float broadPhaseRadius = (float)footprint.BoundingRadiusWorld + shape.BoundingRadiusWorld + 0.03f;
            if (DistanceSquared(footprint.Center, shape.Center) > broadPhaseRadius * broadPhaseRadius)
            {
                continue;
            }

            if (shape.Polygon is { Length: >= 3 } polygon)
            {
                if (!VerticalIntervalsOverlap(footprint.MinHeightM, footprint.MaxHeightM, shape.MinHeightM, shape.MaxHeightM))
                {
                    continue;
                }

                if (!IntersectsFootprint(footprint, polygon))
                {
                    continue;
                }

                Vector2 mtv = ComputeMinimumTranslationVector(footprint, polygon);
                float penetration = mtv.Length();
                if (penetration <= bestPenetration)
                {
                    continue;
                }

                bestPenetration = penetration;
                separationVector = mtv;
                reason = shape.Reason;
                found = true;
                continue;
            }

            if (!IntersectsFootprint(footprint, shape.Footprint))
            {
                continue;
            }

            Vector2 obstacleMtv = ComputeMinimumTranslationVector(footprint, shape.Footprint);
            float obstaclePenetration = obstacleMtv.Length();
            if (obstaclePenetration <= bestPenetration)
            {
                continue;
            }

            bestPenetration = obstaclePenetration;
            separationVector = obstacleMtv;
            reason = shape.Reason;
            found = true;
        }

        if (_enableFieldCompositeInteractionTest
            && TryFindFieldCompositeInteractionContact(world, runtimeGrid, footprint, out Vector2 compositeMtv))
        {
            float compositePenetration = compositeMtv.Length();
            if (compositePenetration > bestPenetration)
            {
                separationVector = compositeMtv;
                reason = "field_composite_interaction_test";
                found = true;
            }
        }

        return found;
    }

    private static bool TryFindFieldCompositeInteractionContact(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        CollisionFootprint footprint,
        out Vector2 separationVector)
    {
        separationVector = Vector2.Zero;
        if (runtimeGrid.CollisionSurface is null)
        {
            return false;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double mapWidthWorld = runtimeGrid.WidthCells * runtimeGrid.CellWidthWorld;
        double mapHeightWorld = runtimeGrid.HeightCells * runtimeGrid.CellHeightWorld;
        double centerX = mapWidthWorld * 0.5 + 1.10 / metersPerWorldUnit;
        double centerY = mapHeightWorld * 0.5 - 1.45 / metersPerWorldUnit;
        double yawRad = world.GameTimeSec * 0.85;
        Vector2 forward = new((float)Math.Cos(yawRad), (float)Math.Sin(yawRad));
        Vector2 right = new(-forward.Y, forward.X);
        if (forward.LengthSquared() <= 1e-8f)
        {
            forward = Vector2.UnitX;
            right = Vector2.UnitY;
        }

        float bestPenetration = 0f;
        Vector2 bestSeparation = Vector2.Zero;
        bool found = false;

        void TestBox(
            double localX,
            double localY,
            double localZ,
            double lengthM,
            double widthM,
            double heightM)
        {
            double insetM = 0.018;
            double halfLengthWorld = Math.Max(0.006, lengthM * 0.5 - insetM) / metersPerWorldUnit;
            double halfWidthWorld = Math.Max(0.006, widthM * 0.5 - insetM) / metersPerWorldUnit;
            Vector2 center = new(
                (float)(centerX + (forward.X * localX + right.X * localZ) / metersPerWorldUnit),
                (float)(centerY + (forward.Y * localX + right.Y * localZ) / metersPerWorldUnit));
            double minHeightM = 0.26 + localY - Math.Max(0.02, heightM) * 0.5 - 0.012;
            double maxHeightM = 0.26 + localY + Math.Max(0.02, heightM) * 0.5 + 0.012;
            double boundingRadiusWorld = Math.Sqrt(halfLengthWorld * halfLengthWorld + halfWidthWorld * halfWidthWorld) + 0.01;
            var obstacle = new CollisionFootprint(
                center,
                forward,
                right,
                halfLengthWorld,
                halfWidthWorld,
                minHeightM,
                maxHeightM,
                boundingRadiusWorld);

            if (!VerticalIntervalsOverlap(footprint, obstacle)
                || !IntersectsFootprint(footprint, obstacle))
            {
                return;
            }

            Vector2 mtv = ComputeMinimumTranslationVector(footprint, obstacle);
            float penetration = mtv.Length();
            if (penetration <= bestPenetration)
            {
                return;
            }

            bestPenetration = penetration;
            bestSeparation = mtv;
            found = true;
        }

        // Match the visible LoadLargeTerrain composite-test pieces with a
        // slightly inset collision proxy so the model is solid without acting
        // like the old oversized square mechanism blockers.
        TestBox(0.0, 0.0, 0.0, 0.78, 0.050, 0.050);
        TestBox(0.0, 0.0, 0.13, 0.68, 0.035, 0.035);
        TestBox(0.0, 0.0, -0.13, 0.68, 0.035, 0.035);
        TestBox(0.40, 0.0, 0.0, 0.045, 0.36, 0.065);
        TestBox(-0.40, 0.0, 0.0, 0.045, 0.36, 0.065);
        TestBox(0.0, -0.19, 0.0, 0.080, 0.080, 0.38);

        separationVector = bestSeparation;
        return found;
    }

    private IReadOnlyList<FacilityCollisionShape> EnsureFacilityCollisionShapes(RuntimeGridData runtimeGrid)
    {
        if (_facilityCollisionShapes is not null
            && Math.Abs(_facilityCollisionCellWidthWorld - runtimeGrid.CellWidthWorld) <= 1e-9
            && Math.Abs(_facilityCollisionCellHeightWorld - runtimeGrid.CellHeightWorld) <= 1e-9)
        {
            return _facilityCollisionShapes;
        }

        var shapes = new List<FacilityCollisionShape>(_facilities.Count);
        float defaultFacilityInsetWorld = ResolveFacilityCollisionInsetWorld();
        foreach (FacilityRegion facility in _facilities)
        {
            if (facility.Type.Equals("dog_hole", StringComparison.OrdinalIgnoreCase))
            {
                AddDogHoleFrameCollisionShapes(runtimeGrid, facility, shapes);
                continue;
            }

            if (!FacilityBlocksMovement(facility))
            {
                continue;
            }

            string reason = $"facility_contact:{facility.Id}";
            float facilityInsetWorld = ResolveFacilityCollisionInsetWorld(facility, defaultFacilityInsetWorld);
            if (TryBuildFacilityPolygon(facility, out Vector2[] polygon))
            {
                if (TryInsetCollisionPolygon(polygon, facilityInsetWorld, out Vector2[] insetPolygon))
                {
                    polygon = insetPolygon;
                }

                Vector2 center = ComputePolygonCenter(polygon);
                float radius = 0f;
                foreach (Vector2 point in polygon)
                {
                    radius = MathF.Max(radius, Vector2.Distance(center, point));
                }

                (double minHeightM, double maxHeightM) = ResolveFacilityCollisionVerticalRange(facility);
                shapes.Add(new FacilityCollisionShape(
                    reason,
                    default,
                    polygon,
                    center,
                    minHeightM,
                    maxHeightM,
                    MathF.Max(0.05f, radius)));
                continue;
            }

            if (!TryBuildFacilityCollisionFootprint(runtimeGrid, facility, facilityInsetWorld, out CollisionFootprint footprint))
            {
                continue;
            }

            shapes.Add(new FacilityCollisionShape(
                reason,
                footprint,
                null,
                footprint.Center,
                footprint.MinHeightM,
                footprint.MaxHeightM,
                (float)Math.Max(0.05, footprint.BoundingRadiusWorld)));
        }

        _facilityCollisionCellWidthWorld = runtimeGrid.CellWidthWorld;
        _facilityCollisionCellHeightWorld = runtimeGrid.CellHeightWorld;
        _facilityCollisionShapes = shapes;
        return shapes;
    }

    private void AddDogHoleFrameCollisionShapes(
        RuntimeGridData runtimeGrid,
        FacilityRegion facility,
        List<FacilityCollisionShape> shapes)
    {
        ResolveDogHoleFrameCollisionGeometry(
            runtimeGrid,
            facility,
            out Vector2 center,
            out Vector2 forward,
            out Vector2 right,
            out double bottomM,
            out double openingWidthWorld,
            out double openingHeightM,
            out double depthWorld,
            out double frameThicknessWorld,
            out double topBeamThicknessM);

        double pillarHeightM = openingHeightM + topBeamThicknessM;
        double halfSpanWorld = openingWidthWorld * 0.5 + frameThicknessWorld * 0.5;
        string reason = $"dog_hole_frame:{facility.Id}";
        AddDogHoleBox(
            shapes,
            reason,
            center - right * (float)halfSpanWorld,
            forward,
            right,
            depthWorld * 0.5,
            frameThicknessWorld * 0.5,
            bottomM,
            bottomM + pillarHeightM);
        AddDogHoleBox(
            shapes,
            reason,
            center + right * (float)halfSpanWorld,
            forward,
            right,
            depthWorld * 0.5,
            frameThicknessWorld * 0.5,
            bottomM,
            bottomM + pillarHeightM);
        AddDogHoleBox(
            shapes,
            reason,
            center,
            forward,
            right,
            depthWorld * 0.5,
            (openingWidthWorld + frameThicknessWorld * 2.0) * 0.5,
            bottomM + openingHeightM,
            bottomM + openingHeightM + topBeamThicknessM);
    }

    private static void AddDogHoleBox(
        List<FacilityCollisionShape> shapes,
        string reason,
        Vector2 center,
        Vector2 forward,
        Vector2 right,
        double halfLengthWorld,
        double halfWidthWorld,
        double minHeightM,
        double maxHeightM)
    {
        double boundingRadiusWorld = Math.Sqrt(halfLengthWorld * halfLengthWorld + halfWidthWorld * halfWidthWorld) + 0.01;
        var footprint = new CollisionFootprint(
            center,
            forward,
            right,
            Math.Max(0.005, halfLengthWorld),
            Math.Max(0.005, halfWidthWorld),
            minHeightM,
            maxHeightM,
            boundingRadiusWorld);
        shapes.Add(new FacilityCollisionShape(
            reason,
            footprint,
            null,
            footprint.Center,
            footprint.MinHeightM,
            footprint.MaxHeightM,
            (float)Math.Max(0.05, footprint.BoundingRadiusWorld)));
    }

    private void ResolveDogHoleFrameCollisionGeometry(
        RuntimeGridData runtimeGrid,
        FacilityRegion facility,
        out Vector2 center,
        out Vector2 forward,
        out Vector2 right,
        out double bottomM,
        out double openingWidthWorld,
        out double openingHeightM,
        out double depthWorld,
        out double frameThicknessWorld,
        out double topBeamThicknessM)
    {
        double metersPerWorldUnit = Math.Max(_metersPerWorldUnit, 1e-6);
        double centerWorldX = (facility.X1 + facility.X2) * 0.5;
        double centerWorldY = (facility.Y1 + facility.Y2) * 0.5;
        if (facility.Points.Count > 0)
        {
            centerWorldX = 0.0;
            centerWorldY = 0.0;
            foreach (Point2D point in facility.Points)
            {
                centerWorldX += point.X;
                centerWorldY += point.Y;
            }

            centerWorldX /= facility.Points.Count;
            centerWorldY /= facility.Points.Count;
        }

        bool isFlySlopeDogHole = facility.Id.StartsWith("red_dog_hole", StringComparison.OrdinalIgnoreCase)
            || facility.Id.StartsWith("blue_dog_hole", StringComparison.OrdinalIgnoreCase)
            || facility.Id.Contains("fly_slope", StringComparison.OrdinalIgnoreCase);
        double defaultYawDeg = isFlySlopeDogHole ? 0.0 : 90.0;
        double defaultBottomOffset = 0.0;
        double defaultTopBeamThickness = isFlySlopeDogHole ? 0.10 : 0.05;
        double yawRad = DegreesToRadians(ResolveFacilityDouble(facility, "model_yaw_deg", defaultYawDeg, -360.0));
        double bottomOffsetM = ResolveFacilityDouble(facility, "model_bottom_offset_m", defaultBottomOffset, -2.0);
        openingWidthWorld = ResolveFacilityDouble(facility, "model_clear_width_m", 0.80, 0.05) / metersPerWorldUnit;
        openingHeightM = ResolveFacilityDouble(facility, "model_clear_height_m", 0.25, 0.05);
        depthWorld = ResolveFacilityDouble(facility, "model_depth_m", 0.25, 0.03) / metersPerWorldUnit;
        frameThicknessWorld = ResolveFacilityDouble(facility, "model_frame_thickness_m", 0.065, 0.01) / metersPerWorldUnit;
        topBeamThicknessM = ResolveFacilityDouble(facility, "model_top_beam_thickness_m", defaultTopBeamThickness, 0.01);
        center = new Vector2((float)centerWorldX, (float)centerWorldY);
        forward = new Vector2((float)Math.Cos(yawRad), (float)Math.Sin(yawRad));
        if (forward.LengthSquared() <= 1e-8f)
        {
            forward = Vector2.UnitX;
        }
        else
        {
            forward = Vector2.Normalize(forward);
        }

        right = new Vector2(-forward.Y, forward.X);
        float terrainHeight = runtimeGrid.SampleOcclusionHeight((float)centerWorldX, (float)centerWorldY);
        bottomM = terrainHeight + bottomOffsetM;
    }

    private static double ResolveFacilityDouble(FacilityRegion facility, string key, double fallback, double minValue)
    {
        if (facility.AdditionalProperties is null
            || !facility.AdditionalProperties.TryGetValue(key, out var node))
        {
            return fallback;
        }

        double value;
        return node.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when node.TryGetDouble(out value) => Math.Max(minValue, value),
            System.Text.Json.JsonValueKind.String when double.TryParse(node.GetString(), out value) => Math.Max(minValue, value),
            _ => fallback,
        };
    }

    private static bool HasFacilityAdditional(FacilityRegion facility, string key)
        => facility.AdditionalProperties is not null
            && facility.AdditionalProperties.ContainsKey(key);

    private static (double MinHeightM, double MaxHeightM) ResolveFacilityCollisionVerticalRange(FacilityRegion facility)
    {
        bool customVertical =
            HasFacilityAdditional(facility, "collision_bottom_m")
            || HasFacilityAdditional(facility, "collision_height_m");
        if (!customVertical)
        {
            return (-10.0, 10.0);
        }

        double bottomM = ResolveFacilityDouble(facility, "collision_bottom_m", 0.0, -10.0);
        double heightM = ResolveFacilityDouble(facility, "collision_height_m", Math.Max(0.05, facility.HeightM), 0.02);
        return (bottomM, bottomM + Math.Clamp(heightM, 0.02, 20.0));
    }

    private static bool TryBuildFacilityCollisionFootprint(
        RuntimeGridData runtimeGrid,
        FacilityRegion facility,
        float insetWorld,
        out CollisionFootprint footprint)
    {
        footprint = default;
        if (facility.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (facility.Shape.Equals("line", StringComparison.OrdinalIgnoreCase))
        {
            Vector2 start = new((float)facility.X1, (float)facility.Y1);
            Vector2 end = new((float)facility.X2, (float)facility.Y2);
            Vector2 direction = end - start;
            if (direction.LengthSquared() <= 1e-6f)
            {
                float radius = (float)Math.Max(
                    0.004,
                    Math.Max(facility.Thickness * 0.5, Math.Max(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld) * 0.5) - insetWorld);
                (double pointMinHeightM, double pointMaxHeightM) = ResolveFacilityCollisionVerticalRange(facility);
                footprint = new CollisionFootprint(
                    start,
                    Vector2.UnitX,
                    Vector2.UnitY,
                    radius,
                    radius,
                    pointMinHeightM,
                    pointMaxHeightM,
                    radius * MathF.Sqrt(2f));
                return true;
            }

            float length = direction.Length();
            Vector2 forward = Vector2.Normalize(direction);
            Vector2 right = new(-forward.Y, forward.X);
            float halfThickness = (float)Math.Max(
                0.004,
                Math.Max(facility.Thickness * 0.5, Math.Max(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld) * 0.5) - insetWorld);
            (double minHeightM, double maxHeightM) = ResolveFacilityCollisionVerticalRange(facility);
            footprint = new CollisionFootprint(
                (start + end) * 0.5f,
                forward,
                right,
                Math.Max(0.004f, length * 0.5f - insetWorld),
                halfThickness,
                minHeightM,
                maxHeightM,
                MathF.Sqrt(length * length + halfThickness * halfThickness));
            return true;
        }

        double minX = Math.Min(facility.X1, facility.X2);
        double maxX = Math.Max(facility.X1, facility.X2);
        double minY = Math.Min(facility.Y1, facility.Y2);
        double maxY = Math.Max(facility.Y1, facility.Y2);
        float halfLength = (float)Math.Max(0.004, (maxX - minX) * 0.5 - insetWorld);
        float halfWidth = (float)Math.Max(0.004, (maxY - minY) * 0.5 - insetWorld);
        (double rectMinHeightM, double rectMaxHeightM) = ResolveFacilityCollisionVerticalRange(facility);
        footprint = new CollisionFootprint(
            new Vector2((float)((minX + maxX) * 0.5), (float)((minY + maxY) * 0.5)),
            Vector2.UnitX,
            Vector2.UnitY,
            halfLength,
            halfWidth,
            rectMinHeightM,
            rectMaxHeightM,
            MathF.Sqrt(halfLength * halfLength + halfWidth * halfWidth));
        return true;
    }

    private static bool TryBuildFacilityPolygon(FacilityRegion facility, out Vector2[] polygon)
    {
        polygon = Array.Empty<Vector2>();
        if (!facility.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase) || facility.Points.Count < 3)
        {
            return false;
        }

        polygon = new Vector2[facility.Points.Count];
        for (int index = 0; index < facility.Points.Count; index++)
        {
            var point = facility.Points[index];
            polygon[index] = new Vector2((float)point.X, (float)point.Y);
        }

        return polygon.Length >= 3;
    }

    private static bool TryInsetCollisionPolygon(IReadOnlyList<Vector2> polygon, float insetWorld, out Vector2[] insetPolygon)
    {
        insetPolygon = polygon.ToArray();
        if (polygon.Count < 3 || Math.Abs(insetWorld) <= 1e-5f)
        {
            return false;
        }

        Vector2 center = ComputePolygonCenter(polygon);
        for (int index = 0; index < polygon.Count; index++)
        {
            Vector2 delta = polygon[index] - center;
            float length = delta.Length();
            if (length <= 1e-6f)
            {
                return false;
            }

            if (insetWorld > 0f && length <= insetWorld + 0.01f)
            {
                return false;
            }

            insetPolygon[index] = center + delta * ((length - insetWorld) / length);
        }

        return true;
    }

    private float ResolveFacilityCollisionInsetWorld()
    {
        double metersPerWorldUnit = Math.Max(_metersPerWorldUnit, 1e-6);
        return (float)Math.Clamp(FacilityCollisionInsetM / metersPerWorldUnit, 0.0, 6.0);
    }

    private float ResolveFacilityCollisionInsetWorld(FacilityRegion facility, float defaultInsetWorld)
    {
        double metersPerWorldUnit = Math.Max(_metersPerWorldUnit, 1e-6);
        double expandM = ResolveFacilityDouble(facility, "collision_expand_m", 0.0, -2.0);
        return (float)Math.Clamp(defaultInsetWorld - expandM / metersPerWorldUnit, -12.0, 12.0);
    }

    private static bool FacilityBlocksMovement(FacilityRegion facility)
    {
        if (IsLegacyMechanismFacility(facility))
        {
            return false;
        }

        if (TryGetFacilityBoolean(facility, "blocks_movement", out bool blocksMovement))
        {
            return blocksMovement;
        }

        if (facility.Type.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)
            || facility.Type.Equals("boundary", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (facility.Type.Equals("dog_hole", StringComparison.OrdinalIgnoreCase)
            || facility.Type.Equals("rugged_road", StringComparison.OrdinalIgnoreCase)
            || facility.Type.Equals("undulating_road", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return facility.HeightM > 0.25;
    }

    private static bool IsLegacyMechanismFacility(FacilityRegion facility)
    {
        return facility.Type.Equals("base", StringComparison.OrdinalIgnoreCase)
            || facility.Type.Equals("outpost", StringComparison.OrdinalIgnoreCase)
            || facility.Type.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetFacilityBoolean(FacilityRegion facility, string key, out bool value)
    {
        value = false;
        if (facility.AdditionalProperties is null
            || !facility.AdditionalProperties.TryGetValue(key, out var node))
        {
            return false;
        }

        switch (node.ValueKind)
        {
            case System.Text.Json.JsonValueKind.True:
                value = true;
                return true;
            case System.Text.Json.JsonValueKind.False:
                value = false;
                return true;
            case System.Text.Json.JsonValueKind.String:
                return bool.TryParse(node.GetString(), out value);
            case System.Text.Json.JsonValueKind.Number:
                if (node.TryGetInt32(out int intValue))
                {
                    value = intValue != 0;
                    return true;
                }

                break;
        }

        return false;
    }

    private static bool IsCollidableEntity(SimulationEntity entity)
        => IsMovableEntity(entity)
            || (SimulationCombatMath.IsStructure(entity)
                && !SimulationCombatMath.IsLegacyMechanismCollisionSuppressed(entity));

    private static CollisionFootprint BuildCollisionFootprint(
        SimulationWorldState world,
        SimulationEntity entity,
        double centerX,
        double centerY)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        (double halfLengthM, double halfWidthM) = EntityCollisionModel.ResolveConservativeHalfExtents(entity);
        double halfLengthWorld = halfLengthM / metersPerWorldUnit;
        double halfWidthWorld = halfWidthM / metersPerWorldUnit;

        double yawRad = DegreesToRadians(entity.AngleDeg);
        Vector2 forward = new((float)Math.Cos(yawRad), (float)Math.Sin(yawRad));
        Vector2 right = new((float)Math.Cos(yawRad + Math.PI * 0.5), (float)Math.Sin(yawRad + Math.PI * 0.5));
        double collisionHeightM = ResolveCollisionHeightM(entity);
        bool descendingAirborne =
            !SimulationCombatMath.IsStructure(entity)
            && entity.AirborneHeightM > 1e-4
            && entity.VerticalVelocityMps < -0.20;
        double descendingNoseDropM = descendingAirborne
            ? Math.Min(collisionHeightM * 0.84, -entity.VerticalVelocityMps * 0.12 + entity.AirborneHeightM * 0.46)
            : 0.0;
        double minHeightM = entity.GroundHeightM + Math.Max(0.0, entity.AirborneHeightM - descendingNoseDropM);
        double maxHeightM = minHeightM + collisionHeightM;
        double noseProbeWorld = descendingAirborne
            ? Math.Min(halfLengthWorld * 0.58, (-entity.VerticalVelocityMps * 0.050 + entity.AirborneHeightM * 0.22) / metersPerWorldUnit)
            : 0.0;
        Vector2 footprintCenter = new(
            (float)(centerX + forward.X * noseProbeWorld),
            (float)(centerY + forward.Y * noseProbeWorld));
        halfLengthWorld += noseProbeWorld * 0.72;
        double boundingRadiusWorld = Math.Sqrt(halfLengthWorld * halfLengthWorld + halfWidthWorld * halfWidthWorld) + 0.01;
        return new CollisionFootprint(
            footprintCenter,
            forward,
            right,
            halfLengthWorld,
            halfWidthWorld,
            minHeightM,
            maxHeightM,
            boundingRadiusWorld);
    }

    private static CollisionFootprint[] BuildCollisionFootprints(
        SimulationWorldState world,
        SimulationEntity entity,
        double centerX,
        double centerY)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double yawRad = DegreesToRadians(entity.AngleDeg);
        Vector2 forward = new((float)Math.Cos(yawRad), (float)Math.Sin(yawRad));
        Vector2 right = new((float)Math.Cos(yawRad + Math.PI * 0.5), (float)Math.Sin(yawRad + Math.PI * 0.5));
        IReadOnlyList<EntityCollisionPart> parts = EntityCollisionModel.ResolveParts(entity);
        var footprints = new CollisionFootprint[Math.Max(1, parts.Count)];
        double baseHeight = entity.GroundHeightM + Math.Max(0.0, entity.AirborneHeightM);
        if (parts.Count == 0)
        {
            footprints[0] = BuildCollisionFootprint(world, entity, centerX, centerY);
            return footprints;
        }

        for (int index = 0; index < parts.Count; index++)
        {
            EntityCollisionPart part = parts[index];
            double localXWorld = part.LocalX / metersPerWorldUnit;
            double localYWorld = part.LocalY / metersPerWorldUnit;
            Vector2 partCenter = new(
                (float)(centerX + forward.X * localXWorld + right.X * localYWorld),
                (float)(centerY + forward.Y * localXWorld + right.Y * localYWorld));
            double halfLengthWorld = Math.Max(0.01, part.LengthM * 0.5) / metersPerWorldUnit;
            double halfWidthWorld = Math.Max(0.01, part.WidthM * 0.5) / metersPerWorldUnit;
            double minHeightM = baseHeight + Math.Max(0.0, part.MinHeightM);
            double maxHeightM = minHeightM + Math.Max(0.04, part.HeightM);
            double boundingRadiusWorld = Math.Sqrt(halfLengthWorld * halfLengthWorld + halfWidthWorld * halfWidthWorld) + 0.01;
            footprints[index] = new CollisionFootprint(
                partCenter,
                forward,
                right,
                halfLengthWorld,
                halfWidthWorld,
                minHeightM,
                maxHeightM,
                boundingRadiusWorld);
        }

        return footprints;
    }

    private static double ResolveCollisionHeightM(SimulationEntity entity)
    {
        if (SimulationCombatMath.IsStructure(entity))
        {
            double structureHeight = Math.Max(0.0, entity.StructureBaseLiftM + entity.BodyHeightM + 0.20);
            return structureHeight + 0.04;
        }

        double chassisHeight = Math.Max(0.22, entity.BodyClearanceM + entity.BodyHeightM);
        return chassisHeight + 0.03;
    }

    private static bool VerticalIntervalsOverlap(CollisionFootprint a, CollisionFootprint b)
        => a.MinHeightM <= b.MaxHeightM - 1e-6 && b.MinHeightM <= a.MaxHeightM - 1e-6;

    private static bool VerticalIntervalsOverlap(double minA, double maxA, double minB, double maxB)
        => minA <= maxB - 1e-6 && minB <= maxA - 1e-6;

    private static bool IntersectsFootprint(CollisionFootprint a, CollisionFootprint b)
    {
        Vector2 delta = b.Center - a.Center;
        return !SeparatesOnAxis(delta, a.Forward, a, b)
            && !SeparatesOnAxis(delta, a.Right, a, b)
            && !SeparatesOnAxis(delta, b.Forward, a, b)
            && !SeparatesOnAxis(delta, b.Right, a, b);
    }

    private static bool IntersectsFootprint(CollisionFootprint footprint, IReadOnlyList<Vector2> polygon)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        foreach (Vector2 axis in EnumerateAxes(footprint, polygon))
        {
            if (SeparatesOnAxis(footprint, polygon, axis))
            {
                return false;
            }
        }

        return true;
    }

    private static Vector2 ComputeMinimumTranslationVector(CollisionFootprint a, CollisionFootprint b)
    {
        Vector2 delta = b.Center - a.Center;
        Vector2 bestAxis = Vector2.UnitX;
        float bestOverlap = float.PositiveInfinity;
        foreach (Vector2 axis in new[] { a.Forward, a.Right, b.Forward, b.Right })
        {
            Vector2 normalized = axis.LengthSquared() > 1e-8f ? Vector2.Normalize(axis) : Vector2.UnitX;
            float distance = MathF.Abs(Vector2.Dot(delta, normalized));
            float overlap = ProjectHalfExtent(a, normalized) + ProjectHalfExtent(b, normalized) - distance;
            if (overlap < bestOverlap)
            {
                bestOverlap = overlap;
                bestAxis = normalized;
            }
        }

        if (!float.IsFinite(bestOverlap) || bestOverlap <= 0f)
        {
            return Vector2.Zero;
        }

        float direction = Vector2.Dot(delta, bestAxis) >= 0f ? -1f : 1f;
        return bestAxis * (bestOverlap + 1e-3f) * direction;
    }

    private static Vector2 ComputeMinimumTranslationVector(CollisionFootprint footprint, IReadOnlyList<Vector2> polygon)
    {
        Vector2 polygonCenter = ComputePolygonCenter(polygon);
        Vector2 delta = polygonCenter - footprint.Center;
        Vector2 bestAxis = Vector2.UnitX;
        float bestOverlap = float.PositiveInfinity;
        foreach (Vector2 axis in EnumerateAxes(footprint, polygon))
        {
            Vector2 normalized = axis.LengthSquared() > 1e-8f ? Vector2.Normalize(axis) : Vector2.UnitX;
            ProjectPolygon(polygon, normalized, out float polygonMin, out float polygonMax);
            ProjectFootprint(footprint, normalized, out float footprintMin, out float footprintMax);
            float overlap = MathF.Min(footprintMax, polygonMax) - MathF.Max(footprintMin, polygonMin);
            if (overlap < bestOverlap)
            {
                bestOverlap = overlap;
                bestAxis = normalized;
            }
        }

        if (!float.IsFinite(bestOverlap) || bestOverlap <= 0f)
        {
            return Vector2.Zero;
        }

        float direction = Vector2.Dot(delta, bestAxis) >= 0f ? -1f : 1f;
        return bestAxis * (bestOverlap + 1e-3f) * direction;
    }

    private static bool SeparatesOnAxis(Vector2 delta, Vector2 axis, CollisionFootprint a, CollisionFootprint b)
    {
        float distance = MathF.Abs(Vector2.Dot(delta, axis));
        float projectionA = ProjectHalfExtent(a, axis);
        float projectionB = ProjectHalfExtent(b, axis);
        return distance > projectionA + projectionB - 1e-4f;
    }

    private static float ProjectHalfExtent(CollisionFootprint footprint, Vector2 axis)
    {
        return (float)(
            footprint.HalfLengthWorld * Math.Abs(Vector2.Dot(axis, footprint.Forward))
            + footprint.HalfWidthWorld * Math.Abs(Vector2.Dot(axis, footprint.Right)));
    }

    private static IEnumerable<Vector2> EnumerateAxes(CollisionFootprint footprint, IReadOnlyList<Vector2> polygon)
    {
        yield return footprint.Forward;
        yield return footprint.Right;
        for (int index = 0; index < polygon.Count; index++)
        {
            Vector2 current = polygon[index];
            Vector2 next = polygon[(index + 1) % polygon.Count];
            Vector2 edge = next - current;
            if (edge.LengthSquared() <= 1e-8f)
            {
                continue;
            }

            yield return Vector2.Normalize(new Vector2(-edge.Y, edge.X));
        }
    }

    private static bool SeparatesOnAxis(CollisionFootprint footprint, IReadOnlyList<Vector2> polygon, Vector2 axis)
    {
        Vector2 normalized = axis.LengthSquared() > 1e-8f ? Vector2.Normalize(axis) : Vector2.UnitX;
        ProjectPolygon(polygon, normalized, out float polygonMin, out float polygonMax);
        ProjectFootprint(footprint, normalized, out float footprintMin, out float footprintMax);
        return footprintMax < polygonMin - 1e-4f || polygonMax < footprintMin - 1e-4f;
    }

    private static void ProjectPolygon(IReadOnlyList<Vector2> polygon, Vector2 axis, out float min, out float max)
    {
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        foreach (Vector2 point in polygon)
        {
            float projection = Vector2.Dot(point, axis);
            min = MathF.Min(min, projection);
            max = MathF.Max(max, projection);
        }
    }

    private static void ProjectFootprint(CollisionFootprint footprint, Vector2 axis, out float min, out float max)
    {
        float center = Vector2.Dot(footprint.Center, axis);
        float halfExtent = ProjectHalfExtent(footprint, axis);
        min = center - halfExtent;
        max = center + halfExtent;
    }

    private static Vector2 ComputePolygonCenter(IReadOnlyList<Vector2> polygon)
    {
        Vector2 sum = Vector2.Zero;
        foreach (Vector2 point in polygon)
        {
            sum += point;
        }

        return polygon.Count == 0 ? Vector2.Zero : sum / polygon.Count;
    }

    private static Vector2[] GetFootprintVertices(CollisionFootprint footprint)
    {
        Vector2 forward = footprint.Forward * (float)footprint.HalfLengthWorld;
        Vector2 right = footprint.Right * (float)footprint.HalfWidthWorld;
        return new[]
        {
            footprint.Center - forward - right,
            footprint.Center + forward - right,
            footprint.Center + forward + right,
            footprint.Center - forward + right,
        };
    }

    private static void ClampToMap(RuntimeGridData runtimeGrid, ref double x, ref double y)
    {
        double maxX = runtimeGrid.WidthCells * runtimeGrid.CellWidthWorld - 1e-4;
        double maxY = runtimeGrid.HeightCells * runtimeGrid.CellHeightWorld - 1e-4;
        x = Math.Clamp(x, 0.0, Math.Max(0.0, maxX));
        y = Math.Clamp(y, 0.0, Math.Max(0.0, maxY));
    }

    private static int WorldToCellX(RuntimeGridData runtimeGrid, double x)
    {
        int cell = (int)Math.Floor(x / Math.Max(1e-6, runtimeGrid.CellWidthWorld));
        return Math.Clamp(cell, 0, runtimeGrid.WidthCells - 1);
    }

    private static int WorldToCellY(RuntimeGridData runtimeGrid, double y)
    {
        int cell = (int)Math.Floor(y / Math.Max(1e-6, runtimeGrid.CellHeightWorld));
        return Math.Clamp(cell, 0, runtimeGrid.HeightCells - 1);
    }

    private static double SampleTerrainHeight(RuntimeGridData runtimeGrid, double worldX, double worldY)
    {
        if (runtimeGrid.TrySampleCollisionSurface(worldX, worldY, out TerrainSurfaceSample surfaceSample, allowNeighborExpansion: false))
        {
            return surfaceSample.HeightM <= TerrainRenderAnchorThresholdM
                ? 0.0
                : surfaceSample.HeightM;
        }

        if (runtimeGrid.TrySampleCollisionSurface(worldX, worldY, out surfaceSample, allowNeighborExpansion: true))
        {
            return surfaceSample.HeightM <= TerrainRenderAnchorThresholdM
                ? 0.0
                : surfaceSample.HeightM;
        }

        double cellWidth = Math.Max(1e-6, runtimeGrid.CellWidthWorld);
        double cellHeight = Math.Max(1e-6, runtimeGrid.CellHeightWorld);
        double normalizedX = Math.Clamp(worldX / cellWidth, 0.0, Math.Max(0.0, runtimeGrid.WidthCells - 1e-6));
        double normalizedY = Math.Clamp(worldY / cellHeight, 0.0, Math.Max(0.0, runtimeGrid.HeightCells - 1e-6));

        int cellX0 = Math.Clamp((int)Math.Floor(normalizedX), 0, runtimeGrid.WidthCells - 1);
        int cellY0 = Math.Clamp((int)Math.Floor(normalizedY), 0, runtimeGrid.HeightCells - 1);
        int cellX1 = Math.Min(cellX0 + 1, runtimeGrid.WidthCells - 1);
        int cellY1 = Math.Min(cellY0 + 1, runtimeGrid.HeightCells - 1);

        double h00 = runtimeGrid.HeightMap[runtimeGrid.IndexOf(cellX0, cellY0)];
        if (cellX0 == cellX1 && cellY0 == cellY1)
        {
            return NormalizeAnchoredTerrainHeight(h00);
        }

        double h10 = runtimeGrid.HeightMap[runtimeGrid.IndexOf(cellX1, cellY0)];
        double h01 = runtimeGrid.HeightMap[runtimeGrid.IndexOf(cellX0, cellY1)];
        double h11 = runtimeGrid.HeightMap[runtimeGrid.IndexOf(cellX1, cellY1)];
        double baseSample;
        if (!ShouldSmoothTerrainQuad(h00, h10, h01, h11))
        {
            baseSample = runtimeGrid.HeightMap[runtimeGrid.IndexOf(
                WorldToCellX(runtimeGrid, worldX),
                WorldToCellY(runtimeGrid, worldY))];
            return ApplyFacetHeightOverlay(runtimeGrid, worldX, worldY, ApplyTerrainSecondarySmoothing(runtimeGrid, normalizedX, normalizedY, baseSample, forceSecondPass: false));
        }

        double tx = Math.Clamp(normalizedX - cellX0, 0.0, 1.0);
        double ty = Math.Clamp(normalizedY - cellY0, 0.0, 1.0);
        double top = Lerp(h00, h10, tx);
        double bottom = Lerp(h01, h11, tx);
        baseSample = Lerp(top, bottom, ty);
        return ApplyFacetHeightOverlay(runtimeGrid, worldX, worldY, ApplyTerrainSecondarySmoothing(runtimeGrid, normalizedX, normalizedY, baseSample, forceSecondPass: true));
    }

    private static bool TrySampleTraversalCollisionSurface(
        RuntimeGridData runtimeGrid,
        double worldX,
        double worldY,
        double referenceHeightM,
        double allowedRiseM,
        out TerrainSurfaceSample sample)
    {
        sample = default;
        if (runtimeGrid.CollisionSurface is null)
        {
            return false;
        }

        double dropAllowanceM = Math.Max(3.00, allowedRiseM + 0.12);
        if (runtimeGrid.TrySampleCollisionSurfaceBand(
                worldX,
                worldY,
                referenceHeightM - dropAllowanceM,
                referenceHeightM + allowedRiseM + 0.12,
                out sample,
                maxCellRadius: 2))
        {
            return true;
        }

        if (runtimeGrid.TrySampleCollisionSurface(worldX, worldY, out sample, allowNeighborExpansion: true)
            && sample.HeightM >= referenceHeightM - dropAllowanceM - 0.20
            && sample.HeightM <= referenceHeightM + allowedRiseM + 0.30)
        {
            return true;
        }

        return false;
    }

    private static double SampleTraversalTerrainHeight(
        RuntimeGridData runtimeGrid,
        double worldX,
        double worldY,
        double referenceHeightM,
        double allowedRiseM)
    {
        if (TrySampleTraversalCollisionSurface(runtimeGrid, worldX, worldY, referenceHeightM, allowedRiseM, out TerrainSurfaceSample surfaceSample))
        {
            return surfaceSample.HeightM <= TerrainRenderAnchorThresholdM
                ? 0.0
                : surfaceSample.HeightM;
        }

        return SampleTerrainHeight(runtimeGrid, worldX, worldY);
    }

    private static double ApplyFacetHeightOverlay(RuntimeGridData runtimeGrid, double worldX, double worldY, double baseHeight)
    {
        return runtimeGrid.TrySampleFacetCollisionSurface(worldX, worldY, out float facetHeight, out _, out TerrainFacetRuntime? facet)
            && TerrainFacetSupportsMotion(facet)
            ? Math.Max(baseHeight, facetHeight)
            : baseHeight;
    }

    private static bool TerrainFacetSupportsMotion(TerrainFacetRuntime? facet)
    {
        if (facet is null)
        {
            return true;
        }

        return !facet.Type.Contains("wall", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSmoothTerrainQuad(double h00, double h10, double h01, double h11)
    {
        double minHeight = Math.Min(Math.Min(h00, h10), Math.Min(h01, h11));
        double maxHeight = Math.Max(Math.Max(h00, h10), Math.Max(h01, h11));
        return maxHeight - minHeight <= TerrainSmoothHeightThresholdM + 1e-6;
    }

    private static double ApplyTerrainSecondarySmoothing(
        RuntimeGridData runtimeGrid,
        double normalizedX,
        double normalizedY,
        double baseSample,
        bool forceSecondPass)
    {
        int centerX = Math.Clamp((int)Math.Round(normalizedX), 0, runtimeGrid.WidthCells - 1);
        int centerY = Math.Clamp((int)Math.Round(normalizedY), 0, runtimeGrid.HeightCells - 1);
        double sum = 0.0;
        double min = double.MaxValue;
        double max = double.MinValue;
        int count = 0;

        for (int offsetY = -2; offsetY <= 2; offsetY++)
        {
            int sampleY = centerY + offsetY;
            if (sampleY < 0 || sampleY >= runtimeGrid.HeightCells)
            {
                continue;
            }

            for (int offsetX = -2; offsetX <= 2; offsetX++)
            {
                int sampleX = centerX + offsetX;
                if (sampleX < 0 || sampleX >= runtimeGrid.WidthCells)
                {
                    continue;
                }

                double height = runtimeGrid.HeightMap[runtimeGrid.IndexOf(sampleX, sampleY)];
                min = Math.Min(min, height);
                max = Math.Max(max, height);
                sum += height;
                count++;
            }
        }

        if (count < 9 || max - min > TerrainSmoothHeightThresholdM + 1e-6)
        {
            return baseSample;
        }

        double average = sum / count;
        double smoothed = Lerp(baseSample, average, 0.72);
        if (forceSecondPass)
        {
            smoothed = Lerp(smoothed, average, 0.46);
        }

        return NormalizeAnchoredTerrainHeight(smoothed);
    }

    private static double NormalizeAnchoredTerrainHeight(double heightM)
    {
        if (heightM <= TerrainRenderAnchorThresholdM)
        {
            return 0.0;
        }

        return Math.Round(heightM / 0.01) * 0.01;
    }

    private static SimulationEntity? FindNearestEnemy(SimulationWorldState world, SimulationEntity self)
    {
        SimulationEntity? nearest = null;
        double nearestDistanceSquared = double.MaxValue;

        foreach (SimulationEntity candidate in world.Entities)
        {
            if (!candidate.IsAlive
                || candidate.IsSimulationSuppressed
                || string.Equals(candidate.Team, self.Team, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double dx = candidate.X - self.X;
            double dy = candidate.Y - self.Y;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                nearest = candidate;
            }
        }

        return nearest;
    }

    private SimulationEntity? ResolveCachedNearestEnemy(SimulationWorldState world, SimulationEntity self)
    {
        NavigationPathState state = GetOrCreateNavigationState(self.Id, world.GameTimeSec);
        if (world.GameTimeSec - state.LastEnemyProbeSec < EnemyProbeIntervalSec
            && !string.IsNullOrWhiteSpace(state.CachedEnemyId))
        {
            SimulationEntity? cached = world.Entities.FirstOrDefault(entity =>
                entity.IsAlive
                && !entity.IsSimulationSuppressed
                && string.Equals(entity.Id, state.CachedEnemyId, StringComparison.OrdinalIgnoreCase));
            if (cached is not null)
            {
                return cached;
            }
        }

        state.LastEnemyProbeSec = world.GameTimeSec;
        SimulationEntity? nearest = FindNearestEnemy(world, self);
        state.CachedEnemyId = nearest?.Id;
        return nearest;
    }

    private SimulationEntity? ResolveCachedVisibleEnemy(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity self,
        SimulationEntity? preferredCandidate)
    {
        NavigationPathState state = GetOrCreateNavigationState(self.Id, world.GameTimeSec);
        if (world.GameTimeSec - state.LastVisibleEnemyProbeSec < VisibleEnemyProbeIntervalSec
            && !string.IsNullOrWhiteSpace(state.CachedVisibleEnemyId))
        {
            return world.Entities.FirstOrDefault(entity =>
                entity.IsAlive
                && !entity.IsSimulationSuppressed
                && string.Equals(entity.Id, state.CachedVisibleEnemyId, StringComparison.OrdinalIgnoreCase));
        }

        state.LastVisibleEnemyProbeSec = world.GameTimeSec;
        List<SimulationEntity> candidates = _visibleEnemyCandidateBuffer;
        candidates.Clear();
        if (preferredCandidate is not null
            && preferredCandidate.IsAlive
            && !preferredCandidate.IsSimulationSuppressed
            && !string.Equals(preferredCandidate.Team, self.Team, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(preferredCandidate);
        }

        for (int selected = candidates.Count; selected < 6; selected++)
        {
            SimulationEntity? nearest = null;
            double nearestDistanceSq = double.PositiveInfinity;
            foreach (SimulationEntity candidate in world.Entities)
            {
                if (!candidate.IsAlive
                    || candidate.IsSimulationSuppressed
                    || string.Equals(candidate.Team, self.Team, StringComparison.OrdinalIgnoreCase)
                    || ContainsEntityId(candidates, candidate.Id))
                {
                    continue;
                }

                double distanceSq = DistanceSquared(self.X, self.Y, candidate.X, candidate.Y);
                if (distanceSq >= nearestDistanceSq)
                {
                    continue;
                }

                nearest = candidate;
                nearestDistanceSq = distanceSq;
            }

            if (nearest is null)
            {
                break;
            }

            candidates.Add(nearest);
        }

        foreach (SimulationEntity candidate in candidates)
        {
            if (!HasVisibleEnemyPlate(world, runtimeGrid, self, candidate))
            {
                continue;
            }

            state.CachedVisibleEnemyId = candidate.Id;
            return candidate;
        }

        state.CachedVisibleEnemyId = null;
        return null;
    }

    private static bool ContainsEntityId(IReadOnlyList<SimulationEntity> entities, string id)
    {
        for (int index = 0; index < entities.Count; index++)
        {
            if (string.Equals(entities[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasVisibleEnemyPlate(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity self,
        SimulationEntity candidate)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        IReadOnlyList<ArmorPlateTarget> plates = SimulationCombatMath.GetAttackableArmorPlateTargets(candidate, metersPerWorldUnit, world.GameTimeSec);
        if (runtimeGrid.CollisionSurface is not null)
        {
            int maxCount = Math.Min(3, plates.Count);
            for (int index = 0; index < maxCount; index++)
            {
                ArmorPlateTarget plate = plates[index];
                if (AutoAimVisibility.CanSeeApproximatePoint(world, runtimeGrid, self, candidate, plate.X, plate.Y, plate.HeightM))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (ArmorPlateTarget plate in plates)
        {
            if (AutoAimVisibility.CanSeePlate(world, runtimeGrid, self, candidate, plate))
            {
                return true;
            }
        }

        return false;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static double ResolvePlayerChassisTargetYaw(
        SimulationEntity entity,
        double controlYawDeg,
        double moveForward,
        double moveRight,
        double moveNorm)
    {
        double normalizedTurretYaw = SimulationCombatMath.NormalizeDeg(controlYawDeg);
        if (moveNorm <= 1e-4)
        {
            return normalizedTurretYaw;
        }

        if (!IsBalanceInfantry(entity))
        {
            return normalizedTurretYaw;
        }

        bool backwardDominant = moveForward <= -0.42 && Math.Abs(moveForward) >= Math.Abs(moveRight) - 0.08;
        if (backwardDominant)
        {
            return normalizedTurretYaw;
        }

        bool lateralDominant = Math.Abs(moveRight) >= 0.42 && Math.Abs(moveForward) <= 0.34;
        if (lateralDominant)
        {
            return SimulationCombatMath.NormalizeDeg(normalizedTurretYaw + Math.Sign(moveRight) * 90.0);
        }

        return normalizedTurretYaw;
    }

    private static bool IsBalanceInfantry(SimulationEntity entity)
    {
        return string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            && entity.ChassisSubtype.Contains("balance", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOmniInfantry(SimulationEntity entity)
    {
        return string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            && entity.ChassisSubtype.Contains("omni", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSmallGyroTranslationInput(SimulationEntity entity, double moveForward, double moveRight)
    {
        if (!entity.SmallGyroActive || IsBalanceInfantry(entity))
        {
            return false;
        }

        return Math.Sqrt(moveForward * moveForward + moveRight * moveRight) > 0.035;
    }

    private static double ResolveSmallGyroTranslationSpeedBoost(SimulationEntity entity)
    {
        if (IsStandardMecanumPowerRole(entity))
        {
            return 3.60;
        }

        return 1.80;
    }

    private static double ResolveSmallGyroTranslationResistanceScale(SimulationEntity entity)
    {
        if (IsStandardMecanumPowerRole(entity))
        {
            return 0.18;
        }

        return 0.34;
    }

    private static double ResolveSmallGyroEquivalentWheelSpeedMps(SimulationEntity entity, double yawRateRadPerSec)
    {
        double halfLength = Math.Max(0.10, entity.BodyLengthM * 0.5);
        double halfWidth = Math.Max(0.08, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5);
        double rotationRadiusM = Math.Clamp(Math.Sqrt(halfLength * halfLength + halfWidth * halfWidth), 0.16, 0.62);
        double turnSpeedScale = ResolveSmallGyroTurnSpeedScale(entity);
        if (HasSmallGyroTranslationInput(entity, entity.MoveInputForward, entity.MoveInputRight))
        {
            turnSpeedScale *= 1.50;
        }

        return ClampWheelLinearSpeedMps(Math.Abs(yawRateRadPerSec) * rotationRadiusM / Math.Max(0.10, turnSpeedScale));
    }

    private static double ResolveSmallGyroTurnSpeedScale(SimulationEntity entity)
    {
        if (IsOmniInfantry(entity))
        {
            return 0.75;
        }

        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            return 3.00;
        }

        return 1.00;
    }

    private static double ResolveSmallGyroPowerLossMultiplier(SimulationEntity entity)
    {
        if (IsBalanceInfantry(entity))
        {
            return 1.30;
        }

        if (IsOmniInfantry(entity))
        {
            return 1.00;
        }

        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            return 2.00;
        }

        return 1.15;
    }

    private static bool TryResolveStandardGyroYawRateForPower(
        SimulationEntity entity,
        double targetPowerW,
        out double yawRateDegPerSec)
    {
        yawRateDegPerSec = 0.0;
        if (!IsStandardMecanumPowerRole(entity))
        {
            return false;
        }

        double powerW = Math.Max(0.0, targetPowerW);
        if (powerW <= 1e-6)
        {
            return true;
        }

        if (powerW <= 50.0)
        {
            yawRateDegPerSec = StandardGyroYawRateAt50WDegPerSec * Math.Sqrt(powerW / 50.0);
            return true;
        }

        if (powerW <= 120.0)
        {
            double t = Math.Clamp((powerW - 50.0) / 70.0, 0.0, 1.0);
            yawRateDegPerSec = Lerp(StandardGyroYawRateAt50WDegPerSec, StandardGyroYawRateAt120WDegPerSec, t);
            return true;
        }

        double tailRatio = 1.0 - Math.Exp(-(powerW - 120.0) / 22.0);
        yawRateDegPerSec = Lerp(StandardGyroYawRateAt120WDegPerSec, StandardGyroYawRateCapDegPerSec, tailRatio);
        yawRateDegPerSec = Math.Min(StandardGyroYawRateCapDegPerSec, yawRateDegPerSec);
        return true;
    }

    private static bool TryResolveStandardGyroPowerForYawRate(
        SimulationEntity entity,
        double yawRateDegPerSec,
        out double powerW)
    {
        powerW = 0.0;
        if (!IsStandardMecanumPowerRole(entity))
        {
            return false;
        }

        double yawRate = Math.Clamp(yawRateDegPerSec, 0.0, StandardGyroYawRateCapDegPerSec);
        if (yawRate <= 1e-6)
        {
            return true;
        }

        if (yawRate <= StandardGyroYawRateAt50WDegPerSec)
        {
            double ratio = yawRate / StandardGyroYawRateAt50WDegPerSec;
            powerW = 50.0 * ratio * ratio;
            return true;
        }

        if (yawRate <= StandardGyroYawRateAt120WDegPerSec)
        {
            double t = (yawRate - StandardGyroYawRateAt50WDegPerSec)
                / Math.Max(1e-6, StandardGyroYawRateAt120WDegPerSec - StandardGyroYawRateAt50WDegPerSec);
            powerW = 50.0 + 70.0 * t;
            return true;
        }

        double tailRatio = (yawRate - StandardGyroYawRateAt120WDegPerSec)
            / Math.Max(1e-6, StandardGyroYawRateCapDegPerSec - StandardGyroYawRateAt120WDegPerSec);
        tailRatio = Math.Clamp(tailRatio, 0.0, 0.999999);
        powerW = 120.0 - 22.0 * Math.Log(1.0 - tailRatio);
        return true;
    }

    private static double ResolveSmallGyroYawRateForPower(SimulationEntity entity, double targetPowerW)
    {
        double safeTargetPowerW = Math.Max(0.0, targetPowerW);
        if (safeTargetPowerW <= 1e-6)
        {
            return 0.0;
        }

        if (TryResolveStandardGyroYawRateForPower(entity, safeTargetPowerW, out double standardYawRateDegPerSec))
        {
            return standardYawRateDegPerSec;
        }

        double low = 0.0;
        double high = IsBalanceInfantry(entity) ? 960.0 : 1440.0;
        for (int index = 0; index < 18; index++)
        {
            double mid = (low + high) * 0.5;
            double powerW = EstimateTurnPowerDrawW(entity, mid, smallGyroRequested: true);
            if (powerW <= safeTargetPowerW)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static double ResolveSmallGyroYawRateDegPerSec(SimulationEntity entity)
    {
        if (IsBalanceInfantry(entity))
        {
            return 5.0 * 180.0 / Math.PI;
        }

        if (IsStandardMecanumPowerRole(entity))
        {
            return StandardGyroYawRateCapDegPerSec;
        }

        return 420.0;
    }

    private static double ResolveEffectiveDrivePowerLimitW(SimulationEntity entity)
    {
        double mechanicalLimitW = Math.Max(1.0, entity.ChassisDrivePowerLimitW);
        double ruleLimitW = entity.RuleDrivePowerLimitW > 1e-6 ? entity.RuleDrivePowerLimitW : mechanicalLimitW;
        double effectiveLimitW = Math.Min(mechanicalLimitW, Math.Max(1.0, ruleLimitW));

        if (entity.MaxChassisEnergy <= 1e-6)
        {
            return effectiveLimitW;
        }

        if (entity.ChassisEnergy <= 1e-6)
        {
            return Math.Min(effectiveLimitW, Math.Max(1.0, entity.ChassisEcoPowerLimitW));
        }

        if (entity.ChassisEnergy >= entity.ChassisBoostThresholdEnergy)
        {
            double boostedLimitW = Math.Min(
                Math.Max(1.0, entity.ChassisBoostPowerCapW),
                effectiveLimitW * Math.Max(1.0, entity.ChassisBoostMultiplier));
            return Math.Max(1.0, boostedLimitW);
        }

        return effectiveLimitW;
    }

    private static void UpdateHeroDeploymentState(SimulationEntity entity)
    {
        bool rangedHero =
            string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            && string.Equals(RuleSet.NormalizeHeroMode(entity.HeroPerformanceMode), "ranged_priority", StringComparison.OrdinalIgnoreCase);
        if (!rangedHero)
        {
            entity.HeroDeploymentRequested = false;
            entity.HeroDeploymentActive = false;
            entity.HeroDeploymentHoldTimerSec = 0.0;
            entity.HeroDeploymentExitHoldTimerSec = 0.0;
            entity.HeroDeploymentYawCorrectionDeg = 0.0;
            entity.HeroDeploymentPitchCorrectionDeg = 0.0;
            entity.HeroDeploymentLastPitchErrorDeg = 0.0;
            entity.HeroDeploymentCorrectionPlateId = null;
            return;
        }

        if (!entity.HeroDeploymentRequested)
        {
            entity.HeroDeploymentActive = false;
            entity.HeroDeploymentExitHoldTimerSec = 0.0;
            entity.HeroDeploymentYawCorrectionDeg = 0.0;
            entity.HeroDeploymentPitchCorrectionDeg = 0.0;
            entity.HeroDeploymentLastPitchErrorDeg = 0.0;
            entity.HeroDeploymentCorrectionPlateId = null;
            return;
        }

        if (entity.HeroDeploymentHoldTimerSec <= 1e-3)
        {
            entity.HeroDeploymentActive = false;
            return;
        }

        if (entity.HeroDeploymentHoldTimerSec >= 2.0)
        {
            entity.HeroDeploymentActive = true;
        }
    }

    private static double ResolveSuperCapAssistLimitW(SimulationEntity entity, double dt, double drivePowerLimitW, bool allowForcedAssist = false)
    {
        if (entity.SuperCapEnergyJ <= 1e-6 || dt <= 1e-6)
        {
            return 0.0;
        }

        DriveMotorModel model = ResolveDriveMotorModel(entity);
        if ((!entity.SuperCapEnabled && !allowForcedAssist) || entity.SuperCapEnergyJ <= model.CapReserveJ)
        {
            return 0.0;
        }

        if (entity.SuperCapEnabled)
        {
            return Math.Min(
                model.SuperCapDischargeLimitW,
                (entity.SuperCapEnergyJ - model.CapReserveJ) / dt);
        }

        return Math.Min(model.SuperCapDischargeLimitW, (entity.SuperCapEnergyJ - model.CapReserveJ) / dt);
    }

    private static double ResolveBufferAssistLimitW(SimulationEntity entity, double dt)
    {
        if (dt <= 1e-6)
        {
            return 0.0;
        }

        double reserveEnergyJ = Math.Max(BufferSuperCapSwitchEnergyJ, entity.BufferReserveEnergyJ);
        double usableBufferJ = Math.Max(0.0, entity.BufferEnergyJ - reserveEnergyJ);
        if (usableBufferJ <= 1e-6)
        {
            return 0.0;
        }

        return Math.Min(150.0, usableBufferJ / dt);
    }

    private static bool IsStandardMecanumPowerRole(SimulationEntity entity)
    {
        return string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesThirtyCentimeterTraversalCap(SimulationEntity entity)
        => IsStandardMecanumPowerRole(entity);

    private static double ResolveEffectiveTraversalStepHeightM(SimulationEntity entity)
        => ResolveEffectiveTraversalStepHeightM(
            Math.Max(0.02, entity.DirectStepHeightM),
            entity.MaxStepClimbHeightM,
            UsesThirtyCentimeterTraversalCap(entity));

    private static double ResolveDirectTraversalStepHeightM(SimulationEntity entity)
        => Math.Min(Math.Max(0.02, entity.DirectStepHeightM), ResolveEffectiveTraversalStepHeightM(entity));

    private static double ResolveEffectiveTraversalStepHeightM(NavigationUnitSnapshot unit)
        => ResolveEffectiveTraversalStepHeightM(
            Math.Max(0.02, unit.DirectStepHeightM),
            unit.MaxStepClimbHeightM,
            unit.RestrictStepHeightToThirtyCm);

    private static double ResolveEffectiveTraversalStepHeightM(
        double directStepHeightM,
        double maxStepClimbHeightM,
        bool restrictToThirtyCentimeters)
    {
        double resolved = Math.Max(directStepHeightM, maxStepClimbHeightM);
        return restrictToThirtyCentimeters ? Math.Min(0.30, resolved) : resolved;
    }

    private readonly record struct DriveMotorModel(
        double WheelCount,
        double StaticPowerW,
        double K1,
        double DampingCoeff,
        double PhaseResistanceOhm,
        double TorqueConstantNmPerA,
        double NoLoadCurrentA,
        double RatedSpeedRpm,
        double PeakTorqueNm,
        double ContinuousTorqueNm,
        double MechanicalTimeConstantSec,
        double NominalVoltageV,
        double CapReserveJ,
        double BufferReserveJ,
        double SuperCapDischargeLimitW,
        double BufferAssistGain);

    private static DriveMotorModel ResolveDriveMotorModel(SimulationEntity entity)
    {
        bool twoWheel = IsBalanceInfantry(entity);
        return twoWheel
            ? new DriveMotorModel(
                2.0,
                4.5,
                0.34,
                0.0,
                0.08,
                0.02,
                0.6,
                9085.0,
                0.33,
                0.16,
                0.049,
                24.0,
                300.0,
                30.0,
                300.0,
                5.0)
            : new DriveMotorModel(
                ResolveDriveWheelCount(entity),
                4.5,
                0.34,
                0.0,
                0.12,
                0.02,
                0.6,
                9085.0,
                0.33,
                0.16,
                0.049,
                24.0,
                300.0,
                30.0,
                300.0,
                5.0);
    }

    private static double DistanceSquared(Vector2 left, Vector2 right)
    {
        double dx = left.X - right.X;
        double dy = left.Y - right.Y;
        return dx * dx + dy * dy;
    }

    private static double TicksToMs(long ticks)
        => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0.0, 1.0);

    private readonly record struct CollisionFootprint(
        Vector2 Center,
        Vector2 Forward,
        Vector2 Right,
        double HalfLengthWorld,
        double HalfWidthWorld,
        double MinHeightM,
        double MaxHeightM,
        double BoundingRadiusWorld);

    private readonly record struct EntityContactResolution(
        SimulationEntity BlockingEntity,
        double ResolvedX,
        double ResolvedY,
        Vector2 SeparationVector,
        double PenetrationWorld);

    private readonly record struct StaticContactResolution(
        string Reason,
        double ResolvedX,
        double ResolvedY,
        Vector2 SeparationVector,
        double PenetrationWorld);

    private readonly record struct FacilityCollisionShape(
        string Reason,
        CollisionFootprint Footprint,
        Vector2[]? Polygon,
        Vector2 Center,
        double MinHeightM,
        double MaxHeightM,
        float BoundingRadiusWorld);

    private sealed class NavigationPathState
    {
        public string NavigationKey { get; set; } = string.Empty;

        public int GoalCellX { get; set; } = -1;

        public int GoalCellY { get; set; } = -1;

        public double PlannedAtSec { get; set; }

        public double LastPlanQueuedSec { get; set; } = -999.0;

        public int NextWaypointIndex { get; set; }

        public double LastDirectProbeSec { get; set; } = -999.0;

        public bool LastDirectCorridorBlocked { get; set; }

        public bool HasLastDirectTarget { get; set; }

        public double LastDirectTargetX { get; set; }

        public double LastDirectTargetY { get; set; }

        public double LastLookAheadProbeSec { get; set; } = -999.0;

        public double LastAutoDecisionSec { get; set; } = -999.0;

        public double LastAutoAimSec { get; set; } = -999.0;

        public double AutoAimLostSinceSec { get; set; } = -999.0;

        public double AutoAimSearchPhaseSec { get; set; }

        public double LastFallbackDirectSec { get; set; } = -999.0;

        public bool HasCachedAutoDrive { get; set; }

        public bool PhaseSeeded { get; set; }

        public double CachedMoveForward { get; set; }

        public double CachedMoveRight { get; set; }

        public double CachedDriveYawDeg { get; set; }

        public double LastEnemyProbeSec { get; set; } = -999.0;

        public string? CachedEnemyId { get; set; }

        public double LastVisibleEnemyProbeSec { get; set; } = -999.0;

        public string? CachedVisibleEnemyId { get; set; }

        public bool HasTargetResidual { get; set; }

        public string? TargetResidualTargetId { get; set; }

        public double TargetResidualStateSec { get; set; } = -999.0;

        public double TargetResidualLastObservedSec { get; set; } = -999.0;

        public double TargetResidualXWorld { get; set; }

        public double TargetResidualYWorld { get; set; }

        public double TargetResidualHeightM { get; set; }

        public double TargetResidualVelocityXMps { get; set; }

        public double TargetResidualVelocityYMps { get; set; }

        public double AiStuckTimerSec { get; set; }

        public double AiLastStuckTriggerSec { get; set; } = -999.0;

        public double AiUnstuckUntilSec { get; set; } = -999.0;

        public double AiUnstuckYawDeg { get; set; }

        public double AiUnstuckStrafeSign { get; set; } = 1.0;

        public Task<NavigationPlanResult>? PendingPlanTask { get; set; }

        public Task<NavigationGoalResult>? PendingGoalTask { get; set; }

        public string PendingNavigationKey { get; set; } = string.Empty;

        public string PendingGoalNavigationKey { get; set; } = string.Empty;

        public int PendingGoalCellX { get; set; } = -1;

        public int PendingGoalCellY { get; set; } = -1;

        public string LastFailedNavigationKey { get; set; } = string.Empty;

        public double LastFailedPlanSec { get; set; } = -999.0;

        public bool HasResolvedGoalCell { get; set; }

        public string ResolvedGoalNavigationKey { get; set; } = string.Empty;

        public int ResolvedGoalCellX { get; set; } = -1;

        public int ResolvedGoalCellY { get; set; } = -1;

        public double ResolvedGoalWorldX { get; set; }

        public double ResolvedGoalWorldY { get; set; }

        public double ResolvedTargetX { get; set; }

        public double ResolvedTargetY { get; set; }

        public double ResolvedDesiredDistanceM { get; set; }

        public double LastGoalResolveSec { get; set; } = -999.0;

        public double LastGoalResolveQueuedSec { get; set; } = -999.0;

        public List<(double X, double Y)> Waypoints { get; } = new();
    }

    private readonly record struct AutoAimObservedState(
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

    private sealed class AutoAimObservationFilterState
    {
        private const double InitialPositionVariance = 0.20 * 0.20;
        private const double InitialVelocityVariance = 4.5 * 4.5;

        public AutoAimObservationFilterState(
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

        private KalmanAxisState _x;
        private KalmanAxisState _y;
        private KalmanAxisState _z;

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

    private sealed record AutoAimSolveCache(
        double GameTimeSec,
        double LastObservedGameTimeSec,
        string TargetId,
        string PlateId,
        string TargetKind,
        ArmorPlateTarget ObservationPlate,
        AutoAimObservedState ObservationState,
        double TargetCenterXWorld,
        double TargetCenterYWorld,
        AutoAimSolution Solution);
}
