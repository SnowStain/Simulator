using System.Numerics;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed class TerrainMotionService
{
    private const double GravityMps2 = 9.81;
    private const double TerrainSmoothHeightThresholdM = 0.045;
    private const double TerrainRenderAnchorThresholdM = 0.02;
    private const double NavigationReplanIntervalMovingSec = 1.25;
    private const double NavigationReplanIntervalStaticSec = 1.80;
    private const double NavigationWaypointReachM = 0.55;
    private const int NavigationMaxExpandedCells = 1200;
    private const int NavigationMaxLookAheadWaypoints = 4;
    private readonly RuleSet _rules;
    private readonly DecisionDeploymentConfig _decisionDeployment;
    private readonly Dictionary<string, NavigationPathState> _navigationStates = new(StringComparer.OrdinalIgnoreCase);

    public TerrainMotionService(RuleSet rules, DecisionDeploymentConfig? decisionDeployment = null)
    {
        _rules = rules;
        _decisionDeployment = decisionDeployment ?? DecisionDeploymentConfig.CreateDefault();
    }

    public void Step(SimulationWorldState world, RuntimeGridData? runtimeGrid, double deltaTimeSec)
    {
        if (runtimeGrid is null || !runtimeGrid.IsValid)
        {
            StepFallback(world, deltaTimeSec);
            return;
        }

        double dt = Math.Clamp(deltaTimeSec, 0.01, 0.2);
        foreach (SimulationEntity entity in world.Entities)
        {
            if (!IsMovableEntity(entity))
            {
                continue;
            }

            if (!entity.IsAlive || entity.IsSimulationSuppressed)
            {
                ClearMotion(entity);
                _navigationStates.Remove(entity.Id);
                continue;
            }

            SimulationEntity? enemy = FindNearestEnemy(world, entity);
            double previousAimSpeedMps = entity.LastAimSpeedMps;
            double previousAimHeightM = entity.LastAimHeightM;
            if (entity.IsPlayerControlled)
            {
                ApplyPlayerControl(world, runtimeGrid, entity, dt);
            }
            else
            {
                ApplyAutoControl(world, runtimeGrid, entity, enemy, dt);
            }

            double chassisTargetYaw = entity.TraversalActive
                ? entity.TraversalDirectionDeg
                : entity.SmallGyroActive
                    ? entity.AngleDeg + ResolveSmallGyroYawRateDegPerSec(entity) * dt
                    : entity.ChassisTargetYawDeg;

            ApplyRotation(entity, chassisTargetYaw, dt);
            ApplyVerticalMotion(entity, dt);
            ApplyTranslationWithTerrain(world, runtimeGrid, entity, dt);
            UpdateAutoAimInstability(world, entity, dt, previousAimSpeedMps, previousAimHeightM);

            entity.JumpRequested = false;
        }
    }

    private static void StepFallback(SimulationWorldState world, double deltaTimeSec)
    {
        double dt = Math.Clamp(deltaTimeSec, 0.01, 0.2);
        foreach (SimulationEntity entity in world.Entities)
        {
            double previousAimSpeedMps = entity.LastAimSpeedMps;
            double previousAimHeightM = entity.LastAimHeightM;
            if (!entity.IsAlive)
            {
                ClearMotion(entity);
                continue;
            }

            entity.EffectiveDrivePowerLimitW = ResolveEffectiveDrivePowerLimitW(entity);
            entity.MotionBlockReason = string.Empty;
            entity.X += entity.VelocityXWorldPerSec * dt;
            entity.Y += entity.VelocityYWorldPerSec * dt;
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
        entity.ChassisPowerDrawW = 0;
        entity.EffectiveDrivePowerLimitW = ResolveEffectiveDrivePowerLimitW(entity);
        entity.ChassisTargetYawDeg = entity.AngleDeg;
        entity.MotionBlockReason = string.Empty;
        ClearAutoAimState(entity);
        entity.TraversalActive = false;
        entity.TraversalProgress = 0;
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

        if (entity.JumpRequested && entity.ChassisSupportsJump && entity.AirborneHeightM <= 1e-3)
        {
            double targetJumpHeightM = string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
                ? 0.80 * (2.0 / 3.0)
                : 0.80;
            entity.VerticalVelocityMps = Math.Sqrt(2.0 * GravityMps2 * targetJumpHeightM);
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
        bool hasNonAttackTacticalCommand = string.Equals(entity.TacticalCommand, "defend", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.TacticalCommand, "patrol", StringComparison.OrdinalIgnoreCase);
        if (enemy is null && !hasNonAttackTacticalCommand)
        {
            _navigationStates.Remove(entity.Id);
            entity.TraversalDirectionDeg = entity.AngleDeg;
            entity.ChassisTargetYawDeg = entity.AngleDeg;
        ApplyDriveControl(world, entity, 0, 0, dt, entity.TraversalDirectionDeg);
            entity.AutoAimLocked = false;
            entity.AutoAimTargetId = null;
            entity.AutoAimPlateId = null;
            entity.AiDecisionSelected = "idle";
            entity.AiDecision = "待机";
            return;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        SimulationEntity? tacticalTarget = ResolveTacticalAttackTarget(world, entity) ?? enemy;
        if (TryApplyTacticalNavigation(world, runtimeGrid, entity, tacticalTarget, dt, metersPerWorldUnit))
        {
            return;
        }

        if (tacticalTarget is null)
        {
            entity.TraversalDirectionDeg = entity.AngleDeg;
            entity.ChassisTargetYawDeg = entity.AngleDeg;
            ApplyDriveControl(world, entity, 0, 0, dt, entity.TraversalDirectionDeg);
            ClearAutoAimState(entity);
            entity.AiDecisionSelected = "idle";
            entity.AiDecision = "待机";
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

        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            entity.SentryStance = mode switch
            {
                "hold" => "defense",
                "support" => "move",
                _ => "attack",
            };
        }

        double moveForward = 0.0;
        double moveRight = 0.0;
        bool plannedNavigationApplied = false;
        if (distanceM > desiredDistanceM + 0.25)
        {
            double heading = RadiansToDegrees(Math.Atan2(dy, dx));
            if (string.Equals(mode, "flank", StringComparison.OrdinalIgnoreCase))
            {
                heading += string.Equals(entity.Team, "red", StringComparison.OrdinalIgnoreCase) ? 34.0 : -34.0;
            }

            bool shouldUsePlannedNavigation =
                HasActiveNavigationState(entity.Id)
                || IsNavigationBlockReason(entity.MotionBlockReason);
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
                    $"auto:{mode}:{tacticalTarget.Id}",
                    tacticalTarget))
            {
                plannedNavigationApplied = true;
            }
            else
            {
                entity.TraversalDirectionDeg = SimulationCombatMath.NormalizeDeg(heading);
                moveForward = aggressionScale;
            }
        }
        else
        {
            _navigationStates.Remove(entity.Id);
            entity.TraversalDirectionDeg = entity.AngleDeg;
        }

        if (plannedNavigationApplied)
        {
            UpdateTurretAim(world, runtimeGrid, entity, dt, playerControlled: false);
            return;
        }

        entity.ChassisTargetYawDeg = entity.TraversalDirectionDeg;
        ApplyDriveControl(world, entity, moveForward, moveRight, dt, entity.TraversalDirectionDeg);
        UpdateTurretAim(world, runtimeGrid, entity, dt, playerControlled: false);
    }

    private bool TryApplyTacticalNavigation(
        SimulationWorldState world,
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
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
        if (distanceM > desiredDistanceM)
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
                $"tactical:{command}:{tacticalTarget?.Id ?? "point"}",
                tacticalTarget))
            {
                entity.TraversalDirectionDeg = SimulationCombatMath.NormalizeDeg(RadiansToDegrees(Math.Atan2(dy, dx)));
                entity.ChassisTargetYawDeg = entity.TraversalDirectionDeg;
                ApplyDriveControl(world, entity, drive, 0.0, dt, entity.TraversalDirectionDeg);
            }
        }
        else
        {
            _navigationStates.Remove(entity.Id);
            entity.ChassisTargetYawDeg = entity.AngleDeg;
            ApplyDriveControl(world, entity, 0.0, 0.0, dt, entity.AngleDeg);
        }

        UpdateTurretAim(world, runtimeGrid, entity, dt, playerControlled: false);
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
        SimulationEntity? targetEntity)
    {
        if (_navigationStates.TryGetValue(entity.Id, out NavigationPathState? existingState)
            && existingState.Waypoints.Count == 0
            && string.Equals(existingState.NavigationKey, navigationKey, StringComparison.Ordinal)
            && world.GameTimeSec - existingState.PlannedAtSec < 0.45)
        {
            return false;
        }

        double quickReuseInterval = targetEntity is null ? NavigationReplanIntervalStaticSec : NavigationReplanIntervalMovingSec;
        if (existingState is not null
            && existingState.Waypoints.Count > 0
            && string.Equals(existingState.NavigationKey, navigationKey, StringComparison.Ordinal)
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

        if (!TryResolveNavigationGoalCell(
            world,
            runtimeGrid,
            entity,
            targetX,
            targetY,
            desiredDistanceM,
            metersPerWorldUnit,
            targetEntity,
            out int goalCellX,
            out int goalCellY,
            out double goalWorldX,
            out double goalWorldY))
        {
            return false;
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

        NavigationPathState state = GetOrCreateNavigationState(entity.Id);
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
                || goalCellDelta >= 3 && elapsedSincePlan >= replanInterval * 0.55);
        bool goalMovedEnough = !samePlan && goalCellDelta >= 3 && elapsedSincePlan >= replanInterval * 0.55;
        bool blockedReplan =
            !string.IsNullOrWhiteSpace(motionBlockReason)
            && IsNavigationBlockReason(motionBlockReason)
            && elapsedSincePlan >= 0.45;
        bool needsReplan =
            emptyPlanExpired
            || goalMovedEnough
            || elapsedSincePlan >= replanInterval
            || blockedReplan
            || !sameNavigationKey;

        if (needsReplan)
        {
            if (!TryBuildNavigationPath(
                world,
                runtimeGrid,
                entity,
                goalCellX,
                goalCellY,
                targetEntity,
                out List<(double X, double Y)> waypoints))
            {
                state.Waypoints.Clear();
                state.NextWaypointIndex = 0;
                state.NavigationKey = navigationKey;
                state.GoalCellX = goalCellX;
                state.GoalCellY = goalCellY;
                state.PlannedAtSec = world.GameTimeSec;
                return false;
            }

            state.Waypoints.Clear();
            foreach ((double X, double Y) waypoint in waypoints)
            {
                state.Waypoints.Add(waypoint);
            }

            state.NextWaypointIndex = 0;
            state.NavigationKey = navigationKey;
            state.GoalCellX = goalCellX;
            state.GoalCellY = goalCellY;
            state.PlannedAtSec = world.GameTimeSec;
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
        ApplyDriveControl(world, entity, drive * driveScale, 0.0, dt, heading);
        entity.AiDecision = $"{entity.AiDecision} route {Math.Max(1, state.Waypoints.Count - state.NextWaypointIndex)}";
        return true;
    }

    private NavigationPathState GetOrCreateNavigationState(string entityId)
    {
        if (!_navigationStates.TryGetValue(entityId, out NavigationPathState? state))
        {
            state = new NavigationPathState();
            _navigationStates[entityId] = state;
        }

        return state;
    }

    private bool HasActiveNavigationState(string entityId)
    {
        return _navigationStates.TryGetValue(entityId, out NavigationPathState? state)
            && state.Waypoints.Count > 0
            && state.NextWaypointIndex < state.Waypoints.Count;
    }

    private static bool IsNavigationBlockReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.StartsWith("terrain_", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("step_too_high", StringComparison.OrdinalIgnoreCase)
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

        int farthestVisible = state.NextWaypointIndex;
        int lastLookAhead = Math.Min(
            state.Waypoints.Count - 1,
            state.NextWaypointIndex + NavigationMaxLookAheadWaypoints);
        for (int index = state.NextWaypointIndex + 1; index <= lastLookAhead; index++)
        {
            (double sampleX, double sampleY) = state.Waypoints[index];
            if (!HasDirectNavigationCorridor(world, runtimeGrid, entity, entity.X, entity.Y, sampleX, sampleY))
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
        int maxSearchRadius = Math.Clamp(desiredRadiusCells + 8, 2, 26);
        double bestScore = double.MaxValue;
        bool found = false;
        double targetHeight = SampleTerrainHeight(runtimeGrid, targetX, targetY);

        for (int radius = 0; radius <= maxSearchRadius; radius++)
        {
            int minX = Math.Max(0, WorldToCellX(runtimeGrid, targetX) - radius);
            int maxX = Math.Min(runtimeGrid.WidthCells - 1, WorldToCellX(runtimeGrid, targetX) + radius);
            int minY = Math.Max(0, WorldToCellY(runtimeGrid, targetY) - radius);
            int maxY = Math.Min(runtimeGrid.HeightCells - 1, WorldToCellY(runtimeGrid, targetY) + radius);

            for (int cellY = minY; cellY <= maxY; cellY++)
            {
                for (int cellX = minX; cellX <= maxX; cellX++)
                {
                    bool onBoundary = radius == 0
                        || cellX == minX
                        || cellX == maxX
                        || cellY == minY
                        || cellY == maxY;
                    if (!onBoundary)
                    {
                        continue;
                    }

                    double candidateX = CellCenterWorldX(runtimeGrid, cellX);
                    double candidateY = CellCenterWorldY(runtimeGrid, cellY);
                    if (!CanStandAtNavigationCell(world, runtimeGrid, entity, cellX, cellY, candidateX, candidateY, targetEntity))
                    {
                        continue;
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
                        continue;
                    }

                    bestScore = score;
                    goalCellX = cellX;
                    goalCellY = cellY;
                    goalWorldX = candidateX;
                    goalWorldY = candidateY;
                    found = true;
                }
            }

            if (found && radius >= desiredRadiusCells)
            {
                break;
            }
        }

        return found;
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
                double jumpClearance = entity.ChassisSupportsJump ? entity.AirborneHeightM : 0.0;
                if (rise > Math.Max(entity.DirectStepHeightM, entity.MaxStepClimbHeightM) + jumpClearance + 1e-6)
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
        double endY)
    {
        double distanceWorld = Math.Sqrt((endX - startX) * (endX - startX) + (endY - startY) * (endY - startY));
        double cellSizeWorld = Math.Max(1e-6, Math.Min(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld));
        int steps = Math.Clamp((int)Math.Ceiling(distanceWorld / (cellSizeWorld * 1.20)), 1, 28);
        double previousHeight = SampleTerrainHeight(runtimeGrid, startX, startY);
        double maxStep = Math.Max(entity.DirectStepHeightM, entity.MaxStepClimbHeightM);
        double jumpClearance = entity.ChassisSupportsJump ? entity.AirborneHeightM : 0.0;
        for (int step = 1; step <= steps; step++)
        {
            double t = step / (double)steps;
            double sampleX = Lerp(startX, endX, t);
            double sampleY = Lerp(startY, endY, t);
            int cellX = WorldToCellX(runtimeGrid, sampleX);
            int cellY = WorldToCellY(runtimeGrid, sampleY);
            int sampleIndex = runtimeGrid.IndexOf(cellX, cellY);
            if (runtimeGrid.MovementBlockMap[sampleIndex])
            {
                return false;
            }

            double sampleHeight = SampleTerrainHeight(runtimeGrid, sampleX, sampleY);
            if (sampleHeight - previousHeight > maxStep + jumpClearance + 1e-6)
            {
                return false;
            }

            if ((step == steps || step % 3 == 0)
                && !CanOccupyTerrainFootprint(world, runtimeGrid, entity, sampleX, sampleY, sampleHeight, maxStep, jumpClearance))
            {
                return false;
            }

            if (HasStaticStructureCollisionAt(world, entity, sampleX, sampleY))
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
        int index = runtimeGrid.IndexOf(cellX, cellY);
        if (runtimeGrid.MovementBlockMap[index])
        {
            return false;
        }

        double referenceHeight = SampleTerrainHeight(runtimeGrid, worldX, worldY);
        double maxStep = Math.Max(entity.DirectStepHeightM, entity.MaxStepClimbHeightM);
        double jumpClearance = entity.ChassisSupportsJump ? entity.AirborneHeightM : 0.0;
        if (!CanStandOnLocalTerrainPatch(runtimeGrid, cellX, cellY, referenceHeight, maxStep + jumpClearance))
        {
            return false;
        }

        return !HasStaticStructureCollisionAt(world, entity, worldX, worldY);
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

                int sampleIndex = runtimeGrid.IndexOf(sampleX, sampleY);
                if (runtimeGrid.MovementBlockMap[sampleIndex])
                {
                    return false;
                }

                if (runtimeGrid.HeightMap[sampleIndex] - referenceHeight > allowedRiseM + 1e-6)
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
                || !SimulationCombatMath.IsStructure(other))
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

    private void UpdateTurretAim(SimulationWorldState world, RuntimeGridData runtimeGrid, SimulationEntity entity, double dt, bool playerControlled)
    {
        bool useAutoAim = entity.AutoAimRequested || !playerControlled;
        if (!useAutoAim)
        {
            ClearAutoAimState(entity);
            return;
        }

        if (!SimulationCombatMath.TryAcquireAutoAimTarget(
            world,
            entity,
            _rules.Combat.AutoAimMaxDistanceM,
            out SimulationEntity? target,
            out ArmorPlateTarget plate,
            (candidate, candidatePlate) => AutoAimVisibility.CanSeePlate(world, runtimeGrid, entity, candidate, candidatePlate)))
        {
            ClearAutoAimState(entity);
            return;
        }

        AutoAimSolution solution = SimulationCombatMath.ComputeAutoAimSolution(
            world,
            entity,
            target!,
            plate,
            _rules.Combat.AutoAimMaxDistanceM);
        ApplyAutoAimSolution(entity, target!, plate, solution, dt);
    }

    private static void ApplyAutoAimSolution(
        SimulationEntity entity,
        SimulationEntity target,
        ArmorPlateTarget plate,
        AutoAimSolution solution,
        double dt)
    {
        string lockKey = $"{target.Id}:{plate.Id}";
        double desiredYaw = SimulationCombatMath.NormalizeDeg(solution.YawDeg);
        double desiredPitch = Math.Clamp(solution.PitchDeg, -40.0, 40.0);
        if (!entity.AutoAimHasSmoothedAim
            || !string.Equals(entity.AutoAimLockKey, lockKey, StringComparison.OrdinalIgnoreCase))
        {
            entity.AutoAimSmoothedYawDeg = desiredYaw;
            entity.AutoAimSmoothedPitchDeg = desiredPitch;
            entity.AutoAimHasSmoothedAim = true;
        }
        else
        {
            double response = 1.0 - Math.Exp(-Math.Clamp(dt, 0.005, 0.08) / 0.11);
            entity.AutoAimSmoothedYawDeg = SmoothYawDeg(entity.AutoAimSmoothedYawDeg, desiredYaw, response);
            entity.AutoAimSmoothedPitchDeg = Lerp(entity.AutoAimSmoothedPitchDeg, desiredPitch, response);
        }

        if (!entity.AutoAimGuidanceOnly)
        {
            entity.TurretYawDeg = SimulationCombatMath.NormalizeDeg(entity.AutoAimSmoothedYawDeg);
            entity.GimbalPitchDeg = Math.Clamp(entity.AutoAimSmoothedPitchDeg, -40.0, 40.0);
        }

        entity.AutoAimLocked = true;
        entity.AutoAimTargetId = target.Id;
        entity.AutoAimPlateId = plate.Id;
        entity.AutoAimPlateDirection = solution.PlateDirection;
        entity.AutoAimAccuracy = solution.Accuracy;
        entity.AutoAimDistanceCoefficient = solution.DistanceCoefficient;
        entity.AutoAimMotionCoefficient = solution.MotionCoefficient;
        entity.AutoAimLeadTimeSec = solution.LeadTimeSec;
        entity.AutoAimLeadDistanceM = solution.LeadDistanceM;
        entity.AutoAimLockKey = lockKey;
    }

    private static void ClearAutoAimState(SimulationEntity entity)
    {
        entity.AutoAimLocked = false;
        entity.AutoAimTargetId = null;
        entity.AutoAimPlateId = null;
        entity.AutoAimPlateDirection = string.Empty;
        entity.AutoAimAccuracy = 0.0;
        entity.AutoAimDistanceCoefficient = 0.0;
        entity.AutoAimMotionCoefficient = 0.0;
        entity.AutoAimLeadTimeSec = 0.0;
        entity.AutoAimLeadDistanceM = 0.0;
        entity.AutoAimHasSmoothedAim = false;
        entity.AutoAimLockKey = null;
    }

    private static double SmoothYawDeg(double currentYawDeg, double targetYawDeg, double amount)
    {
        double delta = SimulationCombatMath.NormalizeSignedDeg(targetYawDeg - currentYawDeg);
        return SimulationCombatMath.NormalizeDeg(currentYawDeg + delta * Math.Clamp(amount, 0.0, 1.0));
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
        double yawRad = DegreesToRadians(driveYawDegOverride ?? entity.AngleDeg);
        double currentVxMps = entity.VelocityXWorldPerSec * metersPerWorldUnit;
        double currentVyMps = entity.VelocityYWorldPerSec * metersPerWorldUnit;
        double currentSpeedMps = Math.Sqrt(currentVxMps * currentVxMps + currentVyMps * currentVyMps);

        double drivePowerLimitW = ResolveEffectiveDrivePowerLimitW(entity);
        double passiveSuperCapAssistW = ResolveSuperCapAssistLimitW(entity, dt, drivePowerLimitW);
        double bufferHeadroomW = ResolveBufferAssistLimitW(entity, dt);
        double availableDrivePowerLimitW = drivePowerLimitW + passiveSuperCapAssistW + bufferHeadroomW;
        entity.EffectiveDrivePowerLimitW = availableDrivePowerLimitW;
        double storedPowerRatio = availableDrivePowerLimitW <= 1e-6 ? 0.35 : 1.0;
        double speedLimitMps = ResolveMoveSpeedMps(entity, availableDrivePowerLimitW) * Math.Max(0.10, entity.ChassisSpeedScale) * storedPowerRatio;
        entity.ChassisSpeedLimitMps = speedLimitMps;
        entity.ChassisPowerRatio = storedPowerRatio;

        double forwardX = Math.Cos(yawRad);
        double forwardY = Math.Sin(yawRad);
        double rightX = Math.Cos(yawRad + Math.PI * 0.5);
        double rightY = Math.Sin(yawRad + Math.PI * 0.5);
        double desiredVxMps = (forwardX * moveForward + rightX * moveRight) * speedLimitMps;
        double desiredVyMps = (forwardY * moveForward + rightY * moveRight) * speedLimitMps;

        double moveMagnitude = Math.Sqrt(moveForward * moveForward + moveRight * moveRight);
        double accelLimit = ResolveAccelerationLimitMps2(entity, availableDrivePowerLimitW, currentSpeedMps, moveMagnitude) * (0.60 + storedPowerRatio * 0.40);
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
        bool brakingHard = (moveMagnitude <= 0.03 || reversingCommand) && currentSpeedMps > 0.08;
        if (brakingHard)
        {
            double brakeAccelLimit = accelLimit * (reversingCommand ? 1.95 : 1.65);
            double brakeDeltaSpeed = Math.Min(currentSpeedMps, brakeAccelLimit * dt);
            if (currentSpeedMps > 1e-6)
            {
                double brakeDirX = currentVxMps / currentSpeedMps;
                double brakeDirY = currentVyMps / currentSpeedMps;
                desiredVxMps = currentVxMps - brakeDirX * brakeDeltaSpeed;
                desiredVyMps = currentVyMps - brakeDirY * brakeDeltaSpeed;
                if (reversingCommand)
                {
                    desiredVxMps += (desiredVxMps >= 0.0 ? -1.0 : 1.0) * Math.Min(Math.Abs(desiredVxMps) * 0.18, speedLimitMps * 0.12);
                    desiredVyMps += (desiredVyMps >= 0.0 ? -1.0 : 1.0) * Math.Min(Math.Abs(desiredVyMps) * 0.18, speedLimitMps * 0.12);
                }
            }
        }

        double wheelResponseTimeSec = ResolveWheelResponseTimeSec(entity, moveMagnitude);
        double response = 1.0 - Math.Exp(-dt / Math.Max(0.035, wheelResponseTimeSec));
        double responseVxMps = currentVxMps + (desiredVxMps - currentVxMps) * response;
        double responseVyMps = currentVyMps + (desiredVyMps - currentVyMps) * response;
        double dvx = responseVxMps - currentVxMps;
        double dvy = responseVyMps - currentVyMps;
        double requestedAccelMps2 = Math.Sqrt(dvx * dvx + dvy * dvy) / Math.Max(dt, 1e-6);
        double maxDelta = accelLimit * dt;
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
        if (reversingCommand)
        {
            dragPerSec = Math.Max(dragPerSec, ResolveBrakeDragPerSec(entity) * 1.18);
        }

        double dragScale = Math.Exp(-dragPerSec * dt);
        newVxMps *= dragScale;
        newVyMps *= dragScale;
        double newSpeedMps = Math.Sqrt(newVxMps * newVxMps + newVyMps * newVyMps);
        if (brakingHard && newSpeedMps <= 0.08)
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
        (double powerDrawW, double bufferUseW, double superCapUseW) = ResolveDrivePowerAllocation(entity, requestedPowerW, drivePowerLimitW, dt);
        if (requestedPowerW > 1e-6 && powerDrawW + 1e-6 < requestedPowerW)
        {
            double kv = Math.Sqrt(Math.Max(0.0, powerDrawW / requestedPowerW));
            newVxMps = currentVxMps + (newVxMps - currentVxMps) * kv;
            newVyMps = currentVyMps + (newVyMps - currentVyMps) * kv;
            newSpeedMps = Math.Sqrt(newVxMps * newVxMps + newVyMps * newVyMps);
            appliedAccelMps2 = Math.Sqrt((newVxMps - currentVxMps) * (newVxMps - currentVxMps)
                + (newVyMps - currentVyMps) * (newVyMps - currentVyMps)) / Math.Max(dt, 1e-6);
            requestedPowerW = EstimateChassisPowerDrawW(entity, currentSpeedMps, newSpeedMps, appliedAccelMps2, moveMagnitude, dt, brakingHard, reversingCommand);
            (powerDrawW, bufferUseW, superCapUseW) = ResolveDrivePowerAllocation(entity, requestedPowerW, drivePowerLimitW, dt);
        }

        if (bufferUseW > 1e-6)
        {
            entity.BufferEnergyJ = Math.Max(0.0, entity.BufferEnergyJ - bufferUseW * dt);
        }

        if (superCapUseW > 1e-6)
        {
            entity.SuperCapEnergyJ = Math.Max(0.0, entity.SuperCapEnergyJ - superCapUseW * dt);
        }

        RechargeStoredDriveEnergy(entity, drivePowerLimitW, powerDrawW, dt);

        entity.VelocityXWorldPerSec = newVxMps / metersPerWorldUnit;
        entity.VelocityYWorldPerSec = newVyMps / metersPerWorldUnit;
        entity.ChassisRpm = newSpeedMps / Math.Max(entity.WheelRadiusM, 0.03) * 9.55;
        entity.ChassisPowerDrawW = powerDrawW;
        entity.MotionBlockReason = string.Empty;
        if (entity.MaxChassisEnergy > 1e-6)
        {
            entity.ChassisEnergy = Math.Max(0.0, entity.ChassisEnergy - powerDrawW * dt);
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
        double wheelRadiusM = Math.Clamp(entity.WheelRadiusM <= 1e-6 ? 0.08 : entity.WheelRadiusM, 0.055, 0.11);
        double wheelCount = model.WheelCount;
        double massKg = Math.Clamp(entity.MassKg <= 1e-6 ? 20.0 : entity.MassKg, 15.0, 25.0);
        double aeroDragCoeff = string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase) ? 0.42 : 0.34;
        double rollingResistanceN = ResolveRollingResistanceForceN(entity);
        double requestedForceN = massKg * appliedAccelMps2 + rollingResistanceN + aeroDragCoeff * speedMps * speedMps;
        double torquePerWheelNm = requestedForceN * wheelRadiusM / Math.Max(1.0, wheelCount);
        double torqueSlewNm = Math.Abs(speedMps - currentSpeedMps) / Math.Max(1e-6, dt) * model.MechanicalTimeConstantSec * massKg * wheelRadiusM / Math.Max(1.0, wheelCount);
        torquePerWheelNm += torqueSlewNm;
        double torqueLimitNm = moveMagnitude > 0.08 ? model.PeakTorqueNm : model.ContinuousTorqueNm;
        torquePerWheelNm = Math.Clamp(torquePerWheelNm, 0.0, torqueLimitNm);

        double wheelOmegaRadPerSec = speedMps / Math.Max(0.01, wheelRadiusM);
        double motorRpm = Math.Abs(wheelOmegaRadPerSec) * 9.5493;
        double currentA = model.NoLoadCurrentA + torquePerWheelNm / Math.Max(1e-6, model.TorqueConstantNmPerA);
        double commandedCurrentA = torquePerWheelNm / 6.0 * 20.0;
        if (brakingHard)
        {
            commandedCurrentA *= reversingCommand ? 1.55 : 1.30;
            currentA *= reversingCommand ? 1.40 : 1.22;
        }

        double item1 = wheelCount * wheelOmegaRadPerSec * commandedCurrentA;
        double item2 = wheelCount * model.PhaseResistanceOhm * commandedCurrentA * commandedCurrentA;
        double item3 = wheelCount * model.DampingCoeff * wheelOmegaRadPerSec * wheelOmegaRadPerSec;
        double copperLossW = wheelCount * currentA * currentA * model.PhaseResistanceOhm;
        double ironLossCoeff = (model.NominalVoltageV * model.NoLoadCurrentA) / Math.Max(1.0, model.RatedSpeedRpm * model.RatedSpeedRpm);
        double ironLossW = wheelCount * ironLossCoeff * motorRpm * motorRpm;
        double mechanicalPowerW = wheelCount * torquePerWheelNm * Math.Abs(wheelOmegaRadPerSec);
        double inverterLossW = wheelCount * model.NominalVoltageV * 0.012 * Math.Clamp(currentA / 10.0, 0.0, 1.6);
        double disturbanceRatio = 1.0 + 0.025 * Math.Sin((entity.X + entity.Y + speedMps) * 3.17) + 0.012 * Math.Cos(entity.AngleDeg * Math.PI / 180.0 * 2.0);
        double transientPowerW = Math.Max(0.0, massKg * appliedAccelMps2 * Math.Max(speedMps, 0.35)) * 0.12;
        if (moveMagnitude <= 0.03 && currentSpeedMps > speedMps + 0.05)
        {
            transientPowerW += massKg * Math.Abs(currentSpeedMps - speedMps) * 0.95;
        }

        if (brakingHard)
        {
            transientPowerW += reversingCommand
                ? Math.Max(18.0, currentSpeedMps * 28.0 + appliedAccelMps2 * massKg * 0.95)
                : Math.Max(10.0, currentSpeedMps * 18.0 + appliedAccelMps2 * massKg * 0.60);
        }

        double powerCtrlPredictW = model.K1 * item1 + item2 + item3 + model.StaticPowerW;
        return Math.Max(
            model.StaticPowerW,
            Math.Max(powerCtrlPredictW, (mechanicalPowerW + copperLossW + ironLossW + inverterLossW + transientPowerW) * disturbanceRatio));
    }

    private static (double PowerDrawW, double BufferUseW, double SuperCapUseW) ResolveDrivePowerAllocation(
        SimulationEntity entity,
        double requestedPowerW,
        double drivePowerLimitW,
        double dt)
    {
        double clampedRequestedW = Math.Max(0.0, requestedPowerW);
        double overPowerW = Math.Max(0.0, clampedRequestedW - drivePowerLimitW);
        DriveMotorModel model = ResolveDriveMotorModel(entity);
        double bufferUseW = 0.0;
        if (overPowerW > 1e-6)
        {
            double usableBufferJ = Math.Max(0.0, entity.BufferEnergyJ - model.BufferReserveJ);
            double bufferBoostW = Math.Max(0.0, usableBufferJ) * model.BufferAssistGain;
            bufferUseW = Math.Min(overPowerW, Math.Min(bufferBoostW, usableBufferJ / Math.Max(dt, 1e-6)));
            overPowerW -= bufferUseW;
        }

        double superCapUseW = 0.0;
        if (overPowerW > 1e-6)
        {
            double capEnergyLimitW = Math.Min(model.SuperCapDischargeLimitW, entity.SuperCapEnergyJ / Math.Max(dt, 1e-6));
            double passiveHeadroomW = Math.Min(35.0, capEnergyLimitW * 0.35);
            double activeHeadroomW = entity.SuperCapEnabled
                ? capEnergyLimitW * Math.Clamp(0.25 + entity.SuperCapEnergyJ / Math.Max(1.0, model.CapReserveJ), 0.25, 1.0)
                : 0.0;
            superCapUseW = Math.Min(overPowerW, Math.Max(passiveHeadroomW, activeHeadroomW));
        }

        return (Math.Min(clampedRequestedW, drivePowerLimitW + bufferUseW + superCapUseW), bufferUseW, superCapUseW);
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

        if (sparePowerW > 1e-6 && entity.MaxSuperCapEnergyJ > 1e-6)
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
            return Math.Max(0.0, 3.5 * Math.Sqrt(Math.Max(0.0, powerW) / 50.0));
        }

        if (IsOmniInfantry(entity))
        {
            double baseAt50W = entity.SmallGyroActive ? 2.5 : 3.0;
            return Math.Max(0.0, baseAt50W * Math.Sqrt(powerW / 50.0));
        }

        double targetAt50W = entity.SmallGyroActive ? 1.0 : 2.0;
        double targetAt120W = entity.SmallGyroActive ? 2.0 : 4.0;
        double t = Math.Clamp((powerW - 50.0) / 70.0, 0.0, 1.0);
        double targetSpeed = Lerp(targetAt50W, targetAt120W, t);
        if (powerW < 50.0)
        {
            targetSpeed = targetAt50W * Math.Sqrt(powerW / 50.0);
        }

        return Math.Max(0.0, targetSpeed);
    }

    private static double ResolveAccelerationLimitMps2(
        SimulationEntity entity,
        double drivePowerLimitW,
        double currentSpeedMps,
        double moveMagnitude)
    {
        double massKg = Math.Clamp(entity.MassKg <= 1e-6 ? 20.0 : entity.MassKg, 15.0, 25.0);
        double accelCoeff = Math.Clamp(entity.ChassisDriveAccelCoeff / 0.012, 0.45, 1.80);
        double wheelRadiusM = Math.Clamp(entity.WheelRadiusM <= 1e-6 ? 0.08 : entity.WheelRadiusM, 0.055, 0.11);
        double motorPeakForcePerWheelN = 20.0 * accelCoeff * (0.08 / wheelRadiusM);
        double driveWheels = string.Equals(entity.WheelStyle, "omni", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase)
                ? 4.0
                : Math.Max(2.0, entity.WheelOffsetsM.Count);
        double peakTractionForceN = motorPeakForcePerWheelN * driveWheels * ResolveDrivetrainEfficiency(entity);
        double powerLimitedForceN = drivePowerLimitW / Math.Max(0.35, currentSpeedMps);
        double usableForceN = Math.Min(peakTractionForceN, powerLimitedForceN);
        if (moveMagnitude <= 0.05)
        {
            usableForceN *= 0.74;
        }

        double rollingResistanceN = ResolveRollingResistanceForceN(entity);
        double netForceN = Math.Max(0.0, usableForceN - rollingResistanceN * (moveMagnitude > 0.05 ? 0.35 : 0.10));
        return Math.Clamp(netForceN / massKg, 0.35, 4.6);
    }

    private static double ResolveRollingDragPerSec(SimulationEntity entity)
    {
        double drag = 0.16 + entity.MassKg * 0.0035 + Math.Max(entity.ChassisDriveRpmCoeff, 0.00001) * 1800.0;
        return Math.Clamp(drag, 0.16, 0.72);
    }

    private static double ResolveBrakeDragPerSec(SimulationEntity entity)
    {
        double drag = 0.72 + entity.MassKg * 0.012 + entity.ChassisDriveAccelCoeff * 30.0;
        return Math.Clamp(drag, 0.85, 2.25);
    }

    private static double ResolveWheelResponseTimeSec(SimulationEntity entity, double moveMagnitude)
    {
        double massFactor = Math.Clamp((entity.MassKg <= 1e-6 ? 20.0 : entity.MassKg) / 20.0, 0.75, 1.30);
        double accelCoeff = Math.Clamp(entity.ChassisDriveAccelCoeff / 0.012, 0.45, 1.80);
        double baseResponse = moveMagnitude <= 0.05 ? 0.34 : 0.22;
        return Math.Clamp(baseResponse * massFactor / accelCoeff, 0.12, 0.55);
    }

    private static double ResolveRollingResistanceForceN(SimulationEntity entity)
    {
        double massKg = Math.Clamp(entity.MassKg <= 1e-6 ? 20.0 : entity.MassKg, 15.0, 25.0);
        double coefficient = string.Equals(entity.WheelStyle, "mecanum", StringComparison.OrdinalIgnoreCase) ? 0.030 : 0.024;
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
        double currentYaw = SimulationCombatMath.NormalizeDeg(entity.AngleDeg);
        double diff = SimulationCombatMath.NormalizeSignedDeg(targetYawDeg - currentYaw);
        double powerScale = 0.65 + Math.Clamp(entity.ChassisPowerRatio, 0.25, 1.0) * 0.35;
        double baseTurnRate = entity.IsPlayerControlled ? 146.0 : 240.0;
        double maxTurnRate = (entity.SmallGyroActive ? ResolveSmallGyroYawRateDegPerSec(entity) : baseTurnRate) * powerScale;
        double maxStep = maxTurnRate * dt;
        double applied = Math.Clamp(diff, -maxStep, maxStep);

        entity.AngularVelocityDegPerSec = Math.Abs(dt) > 1e-6 ? applied / dt : 0;
        entity.AngleDeg = SimulationCombatMath.NormalizeDeg(currentYaw + applied);

        double yawRateAbs = Math.Abs(entity.AngularVelocityDegPerSec);
        if (yawRateAbs > 1e-3)
        {
            double turnPowerW = 0.00032 * yawRateAbs * yawRateAbs;
            if (entity.SmallGyroActive)
            {
                double limitW = ResolveEffectiveDrivePowerLimitW(entity);
                if (IsBalanceInfantry(entity))
                {
                    turnPowerW = Math.Max(turnPowerW, Math.Min(limitW, 50.0));
                }
                else
                {
                    turnPowerW = Math.Max(turnPowerW + 8.0, Math.Max(8.0, limitW - entity.ChassisPowerDrawW));
                }
            }

            entity.ChassisPowerDrawW = Math.Min(
                ResolveEffectiveDrivePowerLimitW(entity),
                entity.ChassisPowerDrawW + turnPowerW);
            if (entity.MaxChassisEnergy > 1e-6)
            {
                entity.ChassisEnergy = Math.Max(0.0, entity.ChassisEnergy - turnPowerW * dt);
            }
        }
    }

    private static void ApplyVerticalMotion(SimulationEntity entity, double dt)
    {
        if (entity.AirborneHeightM <= 1e-6 && entity.VerticalVelocityMps <= 1e-6)
        {
            entity.AirborneHeightM = 0;
            entity.VerticalVelocityMps = 0;
            return;
        }

        entity.VerticalVelocityMps -= GravityMps2 * dt;
        entity.AirborneHeightM = Math.Max(0.0, entity.AirborneHeightM + entity.VerticalVelocityMps * dt);
        if (entity.AirborneHeightM <= 1e-6)
        {
            entity.AirborneHeightM = 0;
            entity.VerticalVelocityMps = 0;
        }
    }

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
                if (HasEntityCollisionAt(world, entity, entity.X, entity.Y, out SimulationEntity? blockingEntity))
                {
                    entity.X = previousX;
                    entity.Y = previousY;
                    entity.GroundHeightM = previousGroundHeight;
                    entity.TraversalProgress = previousProgress;
                    entity.TraversalActive = false;
                    entity.VelocityXWorldPerSec = 0.0;
                    entity.VelocityYWorldPerSec = 0.0;
                    entity.MotionBlockReason = blockingEntity is null
                        ? "traversal_collision"
                        : $"traversal_collision:{blockingEntity.Id}";
                    break;
                }

                continue;
            }

            if (!ApplyTranslationSubstep(world, runtimeGrid, entity, substepDt))
            {
                break;
            }
        }
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
        int targetCellX = WorldToCellX(runtimeGrid, nextX);
        int targetCellY = WorldToCellY(runtimeGrid, nextY);
        int targetIndex = runtimeGrid.IndexOf(targetCellX, targetCellY);

        bool blocked = runtimeGrid.MovementBlockMap[targetIndex];
        double currentHeight = SampleTerrainHeight(runtimeGrid, entity.X, entity.Y);
        double targetHeight = SampleTerrainHeight(runtimeGrid, nextX, nextY);
        double heightDelta = targetHeight - currentHeight;
        double directStep = Math.Max(0.02, entity.DirectStepHeightM);
        double maxStep = Math.Max(directStep, entity.MaxStepClimbHeightM);
        double jumpClearance = entity.ChassisSupportsJump ? entity.AirborneHeightM : 0.0;
        double effectiveDirectStep = directStep + jumpClearance;

        if (blocked || heightDelta > maxStep + jumpClearance + 1e-6)
        {
            ApplyWallBounce(entity, entity.X - nextX, entity.Y - nextY, 0.22);
            entity.MotionBlockReason = blocked ? "terrain_block" : "step_too_high";
            return false;
        }

        if (!CanOccupyTerrainFootprint(world, runtimeGrid, entity, nextX, nextY, currentHeight, maxStep, jumpClearance))
        {
            ApplyWallBounce(entity, entity.X - nextX, entity.Y - nextY, 0.18);
            entity.MotionBlockReason = "terrain_footprint";
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

        if (HasEntityCollisionAt(world, entity, nextX, nextY, out SimulationEntity? blockingEntity))
        {
            double awayX = entity.X - (blockingEntity?.X ?? nextX);
            double awayY = entity.Y - (blockingEntity?.Y ?? nextY);
            ApplyWallBounce(entity, awayX, awayY, 0.24);
            entity.MotionBlockReason = blockingEntity is null
                ? "entity_collision"
                : $"entity_collision:{blockingEntity.Id}";
            return false;
        }

        entity.X = nextX;
        entity.Y = nextY;
        entity.GroundHeightM = targetHeight;
        entity.MotionBlockReason = string.Empty;
        return true;
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
        foreach ((double offsetX, double offsetY) in BuildCollisionFootprintSamples(
            runtimeGrid,
            entity,
            Math.Max(world.MetersPerWorldUnit, 1e-6)))
        {
            double sampleX = centerX + offsetX;
            double sampleY = centerY + offsetY;
            if (sampleX < 0.0 || sampleY < 0.0 || sampleX >= maxX || sampleY >= maxY)
            {
                return false;
            }

            int sampleCellX = WorldToCellX(runtimeGrid, sampleX);
            int sampleCellY = WorldToCellY(runtimeGrid, sampleY);
            int sampleIndex = runtimeGrid.IndexOf(sampleCellX, sampleCellY);
            if (runtimeGrid.MovementBlockMap[sampleIndex])
            {
                return false;
            }

            double sampleHeight = SampleTerrainHeight(runtimeGrid, sampleX, sampleY);
            if (sampleHeight - referenceHeight > allowedRise)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<(double X, double Y)> BuildCollisionFootprintSamples(
        RuntimeGridData runtimeGrid,
        SimulationEntity entity,
        double metersPerWorldUnit)
    {
        double halfLengthWorld = ResolveCollisionHalfLengthM(entity) / metersPerWorldUnit;
        double halfWidthWorld = ResolveCollisionHalfWidthM(entity) / metersPerWorldUnit;
        double yawRad = DegreesToRadians(entity.AngleDeg);
        double forwardX = Math.Cos(yawRad);
        double forwardY = Math.Sin(yawRad);
        double rightX = Math.Cos(yawRad + Math.PI * 0.5);
        double rightY = Math.Sin(yawRad + Math.PI * 0.5);
        double spacing = Math.Max(
            Math.Min(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld) * 0.55,
            Math.Min(halfLengthWorld, halfWidthWorld) * 0.45);
        var samples = new List<(double X, double Y)>(20)
        {
            (0.0, 0.0),
        };

        int xSteps = Math.Clamp((int)Math.Ceiling(halfLengthWorld * 2.0 / Math.Max(spacing, 1e-6)), 2, 6);
        int ySteps = Math.Clamp((int)Math.Ceiling(halfWidthWorld * 2.0 / Math.Max(spacing, 1e-6)), 2, 6);
        for (int xi = 0; xi <= xSteps; xi++)
        {
            double localX = -halfLengthWorld + halfLengthWorld * 2.0 * xi / xSteps;
            AddLocalSample(localX, -halfWidthWorld);
            AddLocalSample(localX, halfWidthWorld);
        }

        for (int yi = 1; yi < ySteps; yi++)
        {
            double localY = -halfWidthWorld + halfWidthWorld * 2.0 * yi / ySteps;
            AddLocalSample(-halfLengthWorld, localY);
            AddLocalSample(halfLengthWorld, localY);
        }

        AddLocalSample(halfLengthWorld * 0.50, 0.0);
        AddLocalSample(-halfLengthWorld * 0.50, 0.0);
        AddLocalSample(0.0, halfWidthWorld * 0.50);
        AddLocalSample(0.0, -halfWidthWorld * 0.50);
        return samples;

        void AddLocalSample(double localX, double localY)
        {
            samples.Add((
                forwardX * localX + rightX * localY,
                forwardY * localX + rightY * localY));
        }
    }

    private static double ResolveCollisionHalfLengthM(SimulationEntity entity)
    {
        return Math.Max(0.08, entity.BodyLengthM * 0.5) + 0.010;
    }

    private static double ResolveCollisionHalfWidthM(SimulationEntity entity)
    {
        return Math.Max(0.08, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5) + 0.010;
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
        double cellSizeM = Math.Max(
            0.08,
            Math.Min(runtimeGrid.CellWidthWorld, runtimeGrid.CellHeightWorld) * metersPerWorldUnit * 0.75);
        int substeps = (int)Math.Ceiling(speedMps * Math.Max(dt, 0.01) / cellSizeM);
        return Math.Clamp(substeps, 1, 6);
    }

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
        blockingEntity = null;
        CollisionFootprint footprint = BuildCollisionFootprint(world, entity, nextX, nextY);
        CollisionFootprint currentFootprint = BuildCollisionFootprint(world, entity, entity.X, entity.Y);
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

            if (!VerticalIntervalsOverlap(footprint, otherFootprint))
            {
                continue;
            }

            if (!IntersectsFootprint(footprint, otherFootprint))
            {
                continue;
            }

            if (IntersectsFootprint(currentFootprint, otherFootprint))
            {
                Vector2 movement = footprint.Center - currentFootprint.Center;
                Vector2 separation = currentFootprint.Center - otherFootprint.Center;
                double currentDistanceSq = DistanceSquared(currentFootprint.Center, otherFootprint.Center);
                double nextDistanceSq = DistanceSquared(footprint.Center, otherFootprint.Center);
                double outwardDot = Vector2.Dot(movement, separation);
                if (nextDistanceSq + 1e-6 >= currentDistanceSq || outwardDot >= -1e-5)
                {
                    continue;
                }
            }

            blockingEntity = other;
            return true;
        }

        return false;
    }

    private static bool IsCollidableEntity(SimulationEntity entity)
        => IsMovableEntity(entity) || SimulationCombatMath.IsStructure(entity);

    private static CollisionFootprint BuildCollisionFootprint(
        SimulationWorldState world,
        SimulationEntity entity,
        double centerX,
        double centerY)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double halfLengthWorld;
        double halfWidthWorld;
        if (SimulationCombatMath.IsStructure(entity) && entity.CollisionRadiusWorld > 1e-6)
        {
            double radiusWorld = Math.Max(0.02, entity.CollisionRadiusWorld);
            double halfExtentWorld = radiusWorld / Math.Sqrt(2.0);
            halfLengthWorld = halfExtentWorld;
            halfWidthWorld = halfExtentWorld;
        }
        else
        {
            halfLengthWorld = ResolveCollisionHalfLengthM(entity) / metersPerWorldUnit;
            halfWidthWorld = ResolveCollisionHalfWidthM(entity) / metersPerWorldUnit;
        }

        double yawRad = DegreesToRadians(entity.AngleDeg);
        Vector2 forward = new((float)Math.Cos(yawRad), (float)Math.Sin(yawRad));
        Vector2 right = new((float)Math.Cos(yawRad + Math.PI * 0.5), (float)Math.Sin(yawRad + Math.PI * 0.5));
        double minHeightM = entity.GroundHeightM + Math.Max(0.0, entity.AirborneHeightM);
        double maxHeightM = minHeightM + ResolveCollisionHeightM(entity);
        double boundingRadiusWorld = Math.Sqrt(halfLengthWorld * halfLengthWorld + halfWidthWorld * halfWidthWorld) + 0.01;
        return new CollisionFootprint(
            new Vector2((float)centerX, (float)centerY),
            forward,
            right,
            halfLengthWorld,
            halfWidthWorld,
            minHeightM,
            maxHeightM,
            boundingRadiusWorld);
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

    private static bool IntersectsFootprint(CollisionFootprint a, CollisionFootprint b)
    {
        Vector2 delta = b.Center - a.Center;
        return !SeparatesOnAxis(delta, a.Forward, a, b)
            && !SeparatesOnAxis(delta, a.Right, a, b)
            && !SeparatesOnAxis(delta, b.Forward, a, b)
            && !SeparatesOnAxis(delta, b.Right, a, b);
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
            return ApplyTerrainSecondarySmoothing(runtimeGrid, normalizedX, normalizedY, baseSample);
        }

        double tx = Math.Clamp(normalizedX - cellX0, 0.0, 1.0);
        double ty = Math.Clamp(normalizedY - cellY0, 0.0, 1.0);
        double top = Lerp(h00, h10, tx);
        double bottom = Lerp(h01, h11, tx);
        baseSample = Lerp(top, bottom, ty);
        return ApplyTerrainSecondarySmoothing(runtimeGrid, normalizedX, normalizedY, baseSample);
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
        double baseSample)
    {
        int centerX = Math.Clamp((int)Math.Round(normalizedX), 0, runtimeGrid.WidthCells - 1);
        int centerY = Math.Clamp((int)Math.Round(normalizedY), 0, runtimeGrid.HeightCells - 1);
        double sum = 0.0;
        double min = double.MaxValue;
        double max = double.MinValue;
        int count = 0;

        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            int sampleY = centerY + offsetY;
            if (sampleY < 0 || sampleY >= runtimeGrid.HeightCells)
            {
                continue;
            }

            for (int offsetX = -1; offsetX <= 1; offsetX++)
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

        if (count < 4 || max - min > TerrainSmoothHeightThresholdM + 1e-6)
        {
            return baseSample;
        }

        double average = sum / count;
        return NormalizeAnchoredTerrainHeight(Lerp(baseSample, average, 0.55));
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

    private static double ResolveSmallGyroYawRateDegPerSec(SimulationEntity entity)
    {
        if (IsBalanceInfantry(entity))
        {
            return 5.0 * 180.0 / Math.PI;
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
            entity.HeroDeploymentYawCorrectionDeg = 0.0;
            entity.HeroDeploymentPitchCorrectionDeg = 0.0;
            entity.HeroDeploymentCorrectionPlateId = null;
            return;
        }

        if (!entity.HeroDeploymentRequested)
        {
            entity.HeroDeploymentActive = false;
            entity.HeroDeploymentYawCorrectionDeg = 0.0;
            entity.HeroDeploymentPitchCorrectionDeg = 0.0;
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

    private static double ResolveSuperCapAssistLimitW(SimulationEntity entity, double dt, double drivePowerLimitW)
    {
        if (entity.SuperCapEnergyJ <= 1e-6 || dt <= 1e-6)
        {
            return 0.0;
        }

        DriveMotorModel model = ResolveDriveMotorModel(entity);
        double passiveTargetPeakW = drivePowerLimitW + 25.0;
        double activeTargetPeakW = entity.SuperCapEnabled
            ? Math.Min(model.SuperCapDischargeLimitW, Math.Max(entity.ChassisBoostPowerCapW, drivePowerLimitW + 120.0))
            : passiveTargetPeakW;
        double targetPeakW = Math.Max(passiveTargetPeakW, activeTargetPeakW);
        double extraHeadroomW = Math.Max(0.0, targetPeakW - drivePowerLimitW);
        if (extraHeadroomW <= 1e-6)
        {
            return 0.0;
        }

        return Math.Min(Math.Min(extraHeadroomW, model.SuperCapDischargeLimitW), entity.SuperCapEnergyJ / dt);
    }

    private static double ResolveBufferAssistLimitW(SimulationEntity entity, double dt)
    {
        if (dt <= 1e-6)
        {
            return 0.0;
        }

        double usableBufferJ = Math.Max(0.0, entity.BufferEnergyJ - Math.Max(30.0, entity.BufferReserveEnergyJ));
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
                200.0,
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
                200.0,
                5.0);
    }

    private static double DistanceSquared(Vector2 left, Vector2 right)
    {
        double dx = left.X - right.X;
        double dy = left.Y - right.Y;
        return dx * dx + dy * dy;
    }

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

    private sealed class NavigationPathState
    {
        public string NavigationKey { get; set; } = string.Empty;

        public int GoalCellX { get; set; } = -1;

        public int GoalCellY { get; set; } = -1;

        public double PlannedAtSec { get; set; }

        public int NextWaypointIndex { get; set; }

        public List<(double X, double Y)> Waypoints { get; } = new();
    }
}
