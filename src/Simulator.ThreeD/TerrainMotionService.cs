using System.Numerics;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed class TerrainMotionService
{
    private const double GravityMps2 = 9.81;
    private readonly RuleSet _rules;
    private readonly DecisionDeploymentConfig _decisionDeployment;

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
        entity.ChassisTargetYawDeg = entity.AngleDeg;
        ClearAutoAimState(entity);
        entity.TraversalActive = false;
        entity.TraversalProgress = 0;
    }

    private void ApplyPlayerControl(SimulationWorldState world, RuntimeGridData runtimeGrid, SimulationEntity entity, double dt)
    {
        double moveForward = Math.Clamp(entity.MoveInputForward, -1.0, 1.0);
        double moveRight = Math.Clamp(entity.MoveInputRight, -1.0, 1.0);
        double moveNorm = Math.Sqrt(moveForward * moveForward + moveRight * moveRight);
        if (moveNorm > 1.0)
        {
            moveForward /= moveNorm;
            moveRight /= moveNorm;
        }

        UpdateTurretAim(world, runtimeGrid, entity, dt, playerControlled: true);

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

        ApplyDriveControl(world, entity, moveForward, moveRight, dt, entity.TraversalDirectionDeg);

        if (entity.JumpRequested && entity.ChassisSupportsJump && entity.AirborneHeightM <= 1e-3)
        {
            double targetJumpHeightM = string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
                ? 0.80 * (2.0 / 3.0)
                : 0.80;
            entity.VerticalVelocityMps = Math.Sqrt(2.0 * GravityMps2 * targetJumpHeightM);
        }

        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            entity.SentryStance = moveNorm > 0.18
                ? "move"
                : (entity.AutoAimRequested || entity.IsFireCommandActive ? "attack" : "defense");
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
        if (enemy is null)
        {
            entity.TraversalDirectionDeg = entity.AngleDeg;
            entity.ChassisTargetYawDeg = entity.AngleDeg;
            ApplyDriveControl(world, entity, 0, 0, dt);
            entity.AutoAimLocked = false;
            entity.AutoAimTargetId = null;
            entity.AutoAimPlateId = null;
            entity.AiDecisionSelected = "idle";
            entity.AiDecision = "待机";
            return;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double dx = enemy.X - entity.X;
        double dy = enemy.Y - entity.Y;
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
        if (distanceM > desiredDistanceM + 0.25)
        {
            double heading = RadiansToDegrees(Math.Atan2(dy, dx));
            if (string.Equals(mode, "flank", StringComparison.OrdinalIgnoreCase))
            {
                heading += string.Equals(entity.Team, "red", StringComparison.OrdinalIgnoreCase) ? 34.0 : -34.0;
            }

            entity.TraversalDirectionDeg = SimulationCombatMath.NormalizeDeg(heading);
            moveForward = aggressionScale;
        }
        else
        {
            entity.TraversalDirectionDeg = entity.AngleDeg;
        }

        entity.ChassisTargetYawDeg = entity.TraversalDirectionDeg;
        ApplyDriveControl(world, entity, moveForward, moveRight, dt);
        UpdateTurretAim(world, runtimeGrid, entity, dt, playerControlled: false);
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

        entity.TurretYawDeg = SimulationCombatMath.NormalizeDeg(entity.AutoAimSmoothedYawDeg);
        entity.GimbalPitchDeg = Math.Clamp(entity.AutoAimSmoothedPitchDeg, -40.0, 40.0);
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
        double storedPowerRatio = drivePowerLimitW <= 1e-6 ? 0.35 : 1.0;
        double speedLimitMps = ResolveMoveSpeedMps(entity, drivePowerLimitW) * storedPowerRatio;
        entity.ChassisSpeedLimitMps = speedLimitMps;
        entity.ChassisPowerRatio = storedPowerRatio;

        double forwardX = Math.Cos(yawRad);
        double forwardY = Math.Sin(yawRad);
        double rightX = Math.Cos(yawRad + Math.PI * 0.5);
        double rightY = Math.Sin(yawRad + Math.PI * 0.5);
        double desiredVxMps = (forwardX * moveForward + rightX * moveRight) * speedLimitMps;
        double desiredVyMps = (forwardY * moveForward + rightY * moveRight) * speedLimitMps;

        double moveMagnitude = Math.Sqrt(moveForward * moveForward + moveRight * moveRight);
        double accelLimit = ResolveAccelerationLimitMps2(entity, drivePowerLimitW, currentSpeedMps, moveMagnitude) * (0.60 + storedPowerRatio * 0.40);
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
        double dragScale = Math.Exp(-dragPerSec * dt);
        newVxMps *= dragScale;
        newVyMps *= dragScale;
        double newSpeedMps = Math.Sqrt(newVxMps * newVxMps + newVyMps * newVyMps);
        if (newSpeedMps > speedLimitMps && newSpeedMps > 1e-6)
        {
            double scale = speedLimitMps / newSpeedMps;
            newVxMps *= scale;
            newVyMps *= scale;
            newSpeedMps = speedLimitMps;
        }

        double appliedAccelMps2 = Math.Sqrt((newVxMps - currentVxMps) * (newVxMps - currentVxMps)
            + (newVyMps - currentVyMps) * (newVyMps - currentVyMps)) / Math.Max(dt, 1e-6);
        double powerDrawW = EstimateChassisPowerDrawW(entity, newSpeedMps, appliedAccelMps2, moveMagnitude);
        if (moveMagnitude > 0.05 && drivePowerLimitW > 5.5)
        {
            (newVxMps, newVyMps, newSpeedMps, appliedAccelMps2, powerDrawW) =
                ResolvePowerControlledVelocity(
                    entity,
                    currentVxMps,
                    currentVyMps,
                    desiredVxMps,
                    desiredVyMps,
                    speedLimitMps,
                    drivePowerLimitW,
                    moveMagnitude,
                    dt);
        }

        entity.VelocityXWorldPerSec = newVxMps / metersPerWorldUnit;
        entity.VelocityYWorldPerSec = newVyMps / metersPerWorldUnit;
        entity.ChassisRpm = newSpeedMps / Math.Max(entity.WheelRadiusM, 0.03) * 9.55;
        entity.ChassisPowerDrawW = powerDrawW;
        if (entity.MaxChassisEnergy > 1e-6)
        {
            entity.ChassisEnergy = Math.Max(0.0, entity.ChassisEnergy - powerDrawW * dt);
        }
    }

    private static (double VxMps, double VyMps, double SpeedMps, double AccelMps2, double PowerW) ResolvePowerControlledVelocity(
        SimulationEntity entity,
        double currentVxMps,
        double currentVyMps,
        double desiredVxMps,
        double desiredVyMps,
        double speedLimitMps,
        double drivePowerLimitW,
        double moveMagnitude,
        double dt)
    {
        double desiredSpeedMps = Math.Sqrt(desiredVxMps * desiredVxMps + desiredVyMps * desiredVyMps);
        if (desiredSpeedMps <= 1e-6 || speedLimitMps <= 1e-6)
        {
            double currentSpeedMps = Math.Sqrt(currentVxMps * currentVxMps + currentVyMps * currentVyMps);
            double currentPowerW = EstimateChassisPowerDrawW(entity, currentSpeedMps, 0.0, moveMagnitude);
            return (currentVxMps, currentVyMps, currentSpeedMps, 0.0, currentPowerW);
        }

        double unitX = desiredVxMps / desiredSpeedMps;
        double unitY = desiredVyMps / desiredSpeedMps;
        double targetPowerW = Math.Max(5.0, drivePowerLimitW);
        double highSpeedMps = Math.Min(speedLimitMps, desiredSpeedMps);
        double lowSpeedMps = 0.0;
        double chosenSpeedMps = highSpeedMps;
        double chosenAccelMps2 = 0.0;
        double chosenPowerW = 0.0;

        for (int iteration = 0; iteration < 18; iteration++)
        {
            double midSpeedMps = (lowSpeedMps + highSpeedMps) * 0.5;
            double candidateVxMps = unitX * midSpeedMps;
            double candidateVyMps = unitY * midSpeedMps;
            double accelMps2 = Math.Sqrt(
                (candidateVxMps - currentVxMps) * (candidateVxMps - currentVxMps)
                + (candidateVyMps - currentVyMps) * (candidateVyMps - currentVyMps)) / Math.Max(dt, 1e-6);
            double powerW = EstimateChassisPowerDrawW(entity, midSpeedMps, accelMps2, moveMagnitude);
            if (powerW > targetPowerW)
            {
                highSpeedMps = midSpeedMps;
            }
            else
            {
                lowSpeedMps = midSpeedMps;
                chosenSpeedMps = midSpeedMps;
                chosenAccelMps2 = accelMps2;
                chosenPowerW = powerW;
            }
        }

        if (chosenPowerW <= 1e-6)
        {
            double candidateVxMps = unitX * chosenSpeedMps;
            double candidateVyMps = unitY * chosenSpeedMps;
            chosenAccelMps2 = Math.Sqrt(
                (candidateVxMps - currentVxMps) * (candidateVxMps - currentVxMps)
                + (candidateVyMps - currentVyMps) * (candidateVyMps - currentVyMps)) / Math.Max(dt, 1e-6);
            chosenPowerW = EstimateChassisPowerDrawW(entity, chosenSpeedMps, chosenAccelMps2, moveMagnitude);
        }

        return (unitX * chosenSpeedMps, unitY * chosenSpeedMps, chosenSpeedMps, chosenAccelMps2, chosenPowerW);
    }

    private static double EstimateChassisPowerDrawW(
        SimulationEntity entity,
        double speedMps,
        double appliedAccelMps2,
        double moveMagnitude)
    {
        double baselinePowerW = 5.0;
        double rollingPowerW = ResolveRollingResistanceForceN(entity) * speedMps;
        double aerodynamicPowerW = 1.8 * speedMps * speedMps * speedMps;
        double accelerationPowerW = Math.Max(0.0, entity.MassKg * appliedAccelMps2 * speedMps / 0.72)
            * (moveMagnitude > 0.05 ? 1.0 : 0.28);
        return baselinePowerW + rollingPowerW + aerodynamicPowerW + accelerationPowerW;
    }

    private static double ResolveMoveSpeedMps(SimulationEntity entity, double drivePowerLimitW)
    {
        double powerW = Math.Max(1.0, drivePowerLimitW);
        if (IsBalanceInfantry(entity))
        {
            if (entity.SmallGyroActive)
            {
                return 0.0;
            }

            return Math.Max(0.0, 3.5 * Math.Sqrt(powerW / 50.0));
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
                turnPowerW += 8.0;
            }

            entity.ChassisPowerDrawW += turnPowerW;
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
                if (HasEntityCollisionAt(world, entity, entity.X, entity.Y))
                {
                    entity.X = previousX;
                    entity.Y = previousY;
                    entity.GroundHeightM = previousGroundHeight;
                    entity.TraversalProgress = previousProgress;
                    entity.TraversalActive = false;
                    entity.VelocityXWorldPerSec = 0.0;
                    entity.VelocityYWorldPerSec = 0.0;
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
        int currentCellX = WorldToCellX(runtimeGrid, entity.X);
        int currentCellY = WorldToCellY(runtimeGrid, entity.Y);
        int targetCellX = WorldToCellX(runtimeGrid, nextX);
        int targetCellY = WorldToCellY(runtimeGrid, nextY);
        int currentIndex = runtimeGrid.IndexOf(currentCellX, currentCellY);
        int targetIndex = runtimeGrid.IndexOf(targetCellX, targetCellY);

        bool blocked = runtimeGrid.MovementBlockMap[targetIndex];
        double currentHeight = runtimeGrid.HeightMap[currentIndex];
        double targetHeight = runtimeGrid.HeightMap[targetIndex];
        double heightDelta = targetHeight - currentHeight;
        double directStep = Math.Max(0.02, entity.DirectStepHeightM);
        double maxStep = Math.Max(directStep, entity.MaxStepClimbHeightM);
        double jumpClearance = entity.ChassisSupportsJump ? entity.AirborneHeightM : 0.0;
        double effectiveDirectStep = directStep + jumpClearance;

        if (blocked || heightDelta > maxStep + jumpClearance + 1e-6)
        {
            entity.VelocityXWorldPerSec = 0;
            entity.VelocityYWorldPerSec = 0;
            return false;
        }

        if (!CanOccupyTerrainFootprint(world, runtimeGrid, entity, nextX, nextY, currentHeight, maxStep, jumpClearance))
        {
            entity.VelocityXWorldPerSec = 0;
            entity.VelocityYWorldPerSec = 0;
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
                return false;
            }

            StartTraversal(entity, nextX, nextY, currentHeight, targetHeight);
            UpdateTraversal(entity, dt);
            return true;
        }

        if (HasEntityCollisionAt(world, entity, nextX, nextY))
        {
            entity.VelocityXWorldPerSec = 0;
            entity.VelocityYWorldPerSec = 0;
            return false;
        }

        entity.X = nextX;
        entity.Y = nextY;
        entity.GroundHeightM = targetHeight;
        return true;
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

            double sampleHeight = runtimeGrid.HeightMap[sampleIndex];
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
        double halfLengthM = Math.Max(0.08, entity.BodyLengthM * 0.5);
        halfLengthM = Math.Max(halfLengthM, Math.Abs(entity.GimbalOffsetXM) + entity.GimbalLengthM * 0.5 + entity.BarrelLengthM);
        halfLengthM = Math.Max(halfLengthM, entity.BodyLengthM * 0.5 + entity.ArmorPlateGapM + entity.ArmorPlateWidthM * 0.5);
        foreach ((double wheelX, _) in entity.WheelOffsetsM)
        {
            halfLengthM = Math.Max(halfLengthM, Math.Abs(wheelX) + entity.WheelRadiusM);
        }

        if (!string.Equals(entity.FrontClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            halfLengthM = Math.Max(
                halfLengthM,
                entity.BodyLengthM * 0.5 + entity.FrontClimbAssistForwardOffsetM + Math.Max(entity.FrontClimbAssistTopLengthM, entity.FrontClimbAssistBottomLengthM));
        }

        if (!string.Equals(entity.RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            halfLengthM = Math.Max(halfLengthM, entity.BodyLengthM * 0.5 + entity.RearClimbAssistUpperLengthM + entity.RearClimbAssistLowerLengthM);
        }

        return halfLengthM + 0.01;
    }

    private static double ResolveCollisionHalfWidthM(SimulationEntity entity)
    {
        double halfWidthM = Math.Max(0.08, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5);
        halfWidthM = Math.Max(halfWidthM, Math.Abs(entity.GimbalOffsetYM) + entity.GimbalWidthM * entity.BodyRenderWidthScale * 0.5);
        halfWidthM = Math.Max(halfWidthM, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5 + entity.ArmorPlateGapM + entity.ArmorPlateWidthM * 0.5);
        foreach ((_, double wheelY) in entity.WheelOffsetsM)
        {
            halfWidthM = Math.Max(halfWidthM, Math.Abs(wheelY) * entity.BodyRenderWidthScale + entity.WheelRadiusM);
        }

        if (!string.Equals(entity.RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            halfWidthM = Math.Max(halfWidthM, entity.BodyWidthM * entity.BodyRenderWidthScale * 0.5 + entity.RearClimbAssistHingeRadiusM);
        }

        return halfWidthM + 0.01;
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
        double nextY)
    {
        CollisionFootprint footprint = BuildCollisionFootprint(world, entity, nextX, nextY);
        foreach (SimulationEntity other in world.Entities)
        {
            if (ReferenceEquals(entity, other)
                || !other.IsAlive
                || !IsCollidableEntity(other))
            {
                continue;
            }

            double dx = nextX - other.X;
            double dy = nextY - other.Y;
            double broadPhaseDistanceSq = dx * dx + dy * dy;
            double broadPhaseRadius = Math.Max(0.08, footprint.BoundingRadiusWorld + other.CollisionRadiusWorld + 0.02);
            if (broadPhaseDistanceSq > broadPhaseRadius * broadPhaseRadius)
            {
                continue;
            }

            CollisionFootprint otherFootprint = BuildCollisionFootprint(world, other, other.X, other.Y);
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

    private static bool IsCollidableEntity(SimulationEntity entity)
        => IsMovableEntity(entity) || SimulationCombatMath.IsStructure(entity);

    private static CollisionFootprint BuildCollisionFootprint(
        SimulationWorldState world,
        SimulationEntity entity,
        double centerX,
        double centerY)
    {
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        double halfLengthWorld = ResolveCollisionHalfLengthM(entity) / metersPerWorldUnit;
        double halfWidthWorld = ResolveCollisionHalfWidthM(entity) / metersPerWorldUnit;
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
        double bodyHeight = Math.Max(0.28, entity.BodyClearanceM + entity.BodyHeightM);
        double gimbalHeight = Math.Max(0.0, entity.GimbalHeightM + entity.GimbalBodyHeightM * 0.5 + entity.BarrelRadiusM * 2.0);
        double structureHeight = Math.Max(0.0, entity.StructureBaseLiftM + entity.BodyHeightM + 0.20);
        return Math.Max(bodyHeight, Math.Max(gimbalHeight, structureHeight)) + 0.04;
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
}
