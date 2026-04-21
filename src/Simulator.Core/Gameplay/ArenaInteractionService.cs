using Simulator.Core.Map;

namespace Simulator.Core.Gameplay;

public sealed class ArenaInteractionService
{
    private const double BuffBaseDamageTakenMult = 0.50;
    private const double BuffCentralHighlandDamageTakenMult = 0.75;
    private const double BuffTrapezoidHighlandDamageTakenMult = 0.50;
    private const double BuffOutpostDamageTakenMult = 0.75;
    private const double BuffHeroDeploymentDamageTakenMult = 0.75;
    private const double BuffEngineerAliveDamageTakenMult = 0.50;
    private const double TerrainHighlandDamageTakenMult = 0.75;
    private const double TerrainFlySlopeDamageTakenMult = 0.75;
    private const double TerrainRoadCoolingMult = 2.00;
    private const double TerrainSlopeDamageTakenMult = 0.90;
    private const double TerrainSlopeCoolingMult = 1.20;

    private readonly RuleSet _rules;

    public ArenaInteractionService(RuleSet rules)
    {
        _rules = rules;
    }

    public IReadOnlyList<FacilityInteractionEvent> UpdateWorld(
        SimulationWorldState world,
        IReadOnlyList<FacilityRegion> facilities,
        double deltaTimeSec)
    {
        if (deltaTimeSec <= 0)
        {
            return Array.Empty<FacilityInteractionEvent>();
        }

        var events = new List<FacilityInteractionEvent>();
        var energyActiveTeams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> centralHighlandControl = ResolveCentralHighlandControl(world, facilities);
        NormalizeEnergyRotorDirections(world);

        foreach (SimulationTeamState team in world.Teams.Values)
        {
            AwardEnergyOpportunityTokens(team, world.GameTimeSec);
            team.EnergyBuffTimerSec = Math.Max(0.0, team.EnergyBuffTimerSec - deltaTimeSec);
            if (team.EnergyBuffTimerSec <= 1e-6)
            {
                team.EnergyBuffDamageDealtMult = 1.0;
                team.EnergyBuffDamageTakenMult = 1.0;
                team.EnergyBuffCoolingMult = 1.0;
                if (string.Equals(team.EnergyMechanismState, "activated", StringComparison.OrdinalIgnoreCase))
                {
                    team.EnergyMechanismState = "inactive";
                    ResetEnergyVisualState(team);
                }
            }

            TickEnergyMechanismActivation(world, team, deltaTimeSec, events);
        }

        foreach (SimulationEntity entity in world.Entities)
        {
            entity.ResetDynamicEffects();
            TickTimedBuffs(entity, deltaTimeSec);
            entity.SupplyBuyCooldownSec = Math.Max(0.0, entity.SupplyBuyCooldownSec - deltaTimeSec);
            if (!entity.IsAlive)
            {
                continue;
            }

            bool touchedDeadZone = false;
            bool touchedHeroDeployment = false;

            foreach (FacilityRegion facility in facilities)
            {
                if (!facility.Contains(entity.X, entity.Y))
                {
                    continue;
                }

                string facilityType = NormalizeFacilityType(facility.Type);
                switch (facilityType)
                {
                    case "supply":
                        ApplySupply(world, entity, facility, deltaTimeSec, events);
                        break;
                    case "fort":
                        ApplyFort(world, entity, facility);
                        break;
                    case "dead_zone":
                        touchedDeadZone = true;
                        ApplyDeadZone(world, entity, facility, deltaTimeSec, events);
                        break;
                    case "mining":
                        ApplyMining(world, entity, facility, deltaTimeSec, events);
                        break;
                    case "exchange":
                        ApplyExchange(world, entity, facility, deltaTimeSec, events);
                        break;
                    case "energy_mechanism":
                        ApplyEnergyMechanism(world, entity, facility, deltaTimeSec, energyActiveTeams, events);
                        break;
                    default:
                        if (facilityType.StartsWith("buff_", StringComparison.OrdinalIgnoreCase))
                        {
                            ApplyBuffRegion(
                                world,
                                entity,
                                facility,
                                facilityType,
                                deltaTimeSec,
                                centralHighlandControl,
                                ref touchedHeroDeployment,
                                events);
                        }

                        break;
                }
            }

            TryStartEnergyMechanismAttempt(world, entity, "energy_mechanism", energyActiveTeams, events);

            if (!touchedDeadZone)
            {
                entity.DeadZoneTimerSec = 0.0;
            }

            if (!touchedHeroDeployment)
            {
                entity.HeroDeploymentHoldTimerSec = 0.0;
            }

            ApplyTimedBuffEffects(entity);
        }

        foreach (SimulationTeamState teamState in world.Teams.Values)
        {
            if (!energyActiveTeams.Contains(teamState.Team))
            {
                teamState.EnergyActivationTimerSec = 0.0;
                teamState.EnergyVirtualHits = 0.0;
            }
        }

        foreach (SimulationEntity entity in world.Entities)
        {
            if (!entity.IsAlive)
            {
                continue;
            }

            if (world.GameTimeSec < 180.0
                && entity.IsAlive
                && string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDamageTakenBuff(entity, BuffEngineerAliveDamageTakenMult);
            }

            SimulationTeamState teamState = world.GetOrCreateTeamState(entity.Team);
            if (teamState.EnergyBuffTimerSec > 0
                && IsRobotEntity(entity))
            {
                ApplyDamageDealtBuff(entity, teamState.EnergyBuffDamageDealtMult);
                ApplyDamageTakenBuff(entity, teamState.EnergyBuffDamageTakenMult);
                ApplyCoolingBuff(entity, teamState.EnergyBuffCoolingMult);
            }
        }

        return events;
    }

    public FacilityInteractionDescriptor DescribeFacility(FacilityRegion region)
    {
        string facilityType = NormalizeFacilityType(region.Type);
        return facilityType switch
        {
            "supply" => new FacilityInteractionDescriptor(
                facilityType,
                "Friendly unit can buy ammo here.",
                new[] { "hero", "infantry", "sentry" }),
            "fort" => new FacilityInteractionDescriptor(
                facilityType,
                "Friendly unit gains defense and cooling bonuses here.",
                new[] { "hero", "engineer", "infantry", "sentry" }),
            "dead_zone" => new FacilityInteractionDescriptor(
                facilityType,
                "Staying here too long permanently eliminates the unit.",
                new[] { "hero", "engineer", "infantry", "sentry" }),
            "mining" => new FacilityInteractionDescriptor(
                facilityType,
                "Engineers can mine minerals here over time.",
                new[] { "engineer" }),
            "exchange" => new FacilityInteractionDescriptor(
                facilityType,
                "Engineers can exchange carried minerals for team gold here.",
                new[] { "engineer" }),
            "energy_mechanism" => new FacilityInteractionDescriptor(
                facilityType,
                "Qualified roles can activate a temporary team energy buff here.",
                new[] { "hero", "infantry", "sentry" }),
            _ => new FacilityInteractionDescriptor(
                facilityType,
                "This facility is currently only a marked region.",
                Array.Empty<string>()),
        };
    }

    private void ApplySupply(
        SimulationWorldState world,
        SimulationEntity entity,
        FacilityRegion facility,
        double deltaTimeSec,
        ICollection<FacilityInteractionEvent> events)
    {
        if (!IsFriendlyFacility(entity.Team, facility.Team))
        {
            return;
        }

        double healRatioPerSec = world.GameTimeSec >= 240.0
            && entity.FireCooldownSec <= 1e-3
            && entity.Heat <= Math.Max(1.0, entity.MaxHeat * 0.08)
            ? 0.25
            : 0.10;
        if (entity.MaxHealth > 1e-6)
        {
            entity.Health = Math.Min(entity.MaxHealth, entity.Health + entity.MaxHealth * healRatioPerSec * deltaTimeSec);
        }

        if (entity.MaxChassisEnergy > 1e-6)
        {
            entity.ChassisEnergy = Math.Min(entity.MaxChassisEnergy, entity.ChassisEnergy + 3200.0 * deltaTimeSec);
        }

        if (entity.MaxSuperCapEnergyJ > 1e-6)
        {
            entity.SuperCapEnergyJ = Math.Min(entity.MaxSuperCapEnergyJ, entity.SuperCapEnergyJ + 650.0 * deltaTimeSec);
        }

        if (entity.MaxBufferEnergyJ > 1e-6)
        {
            entity.BufferEnergyJ = Math.Min(entity.MaxBufferEnergyJ, entity.BufferEnergyJ + 90.0 * deltaTimeSec);
        }

        if (string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            entity.SupplyAccumulatorSec = 0.0;
            return;
        }

        int unitGain = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase)
            ? _rules.Facility.SupplyAmmoGain42Mm
            : _rules.Facility.SupplyAmmoGain17Mm;
        int unitCost = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase) ? 10 : 1;
        int ammoCost = Math.Max(unitCost, unitGain * unitCost);
        SimulationTeamState teamState = world.GetOrCreateTeamState(entity.Team);

        if (!entity.BuyAmmoRequested)
        {
            entity.SupplyAccumulatorSec = 0.0;
            return;
        }

        if (entity.SupplyBuyCooldownSec > 1e-6)
        {
            return;
        }

        if (teamState.Gold + 1e-6 < ammoCost)
        {
            entity.SupplyBuyCooldownSec = 0.25;
            events.Add(new FacilityInteractionEvent(
                world.GameTimeSec,
                entity.Team,
                entity.Id,
                facility.Id,
                "supply",
                $"Not enough gold: need {ammoCost}"));
            return;
        }

        teamState.Gold = Math.Max(0.0, teamState.Gold - ammoCost);
        entity.AddAllowedAmmo(unitGain);
        entity.SupplyBuyCooldownSec = 0.35;

        events.Add(new FacilityInteractionEvent(
            world.GameTimeSec,
            entity.Team,
            entity.Id,
            facility.Id,
            "supply",
            $"Bought ammo -{ammoCost} gold, +{unitGain}"));
    }

    private void ApplyFort(SimulationWorldState world, SimulationEntity entity, FacilityRegion facility)
    {
        if (!CanUseFortBuff(world, entity, facility))
        {
            return;
        }

        ApplyDamageTakenBuff(entity, _rules.Facility.FortDamageTakenMult);
        ApplyCoolingBuff(entity, _rules.Facility.FortCoolingMult);
    }

    private void ApplyBuffRegion(
        SimulationWorldState world,
        SimulationEntity entity,
        FacilityRegion facility,
        string facilityType,
        double deltaTimeSec,
        IReadOnlyDictionary<string, string> centralHighlandControl,
        ref bool touchedHeroDeployment,
        ICollection<FacilityInteractionEvent> events)
    {
        switch (facilityType)
        {
            case "buff_base":
                if (!IsFriendlyFacility(entity.Team, facility.Team) || !IsRobotEntity(entity))
                {
                    return;
                }

                if (entity.BuyAmmoRequested)
                {
                    ApplySupply(world, entity, facility, deltaTimeSec, events);
                }

                ApplyDamageTakenBuff(entity, BuffBaseDamageTakenMult);
                return;

            case "buff_outpost":
                if (!CanUseOutpostBuff(world, entity, facility))
                {
                    return;
                }

                ApplyDamageTakenBuff(entity, BuffOutpostDamageTakenMult);
                ClearWeakState(entity);
                return;

            case "buff_trapezoid_highland":
                if (!IsFriendlyFacility(entity.Team, facility.Team) || !IsRobotEntity(entity))
                {
                    return;
                }

                ApplyDamageTakenBuff(entity, BuffTrapezoidHighlandDamageTakenMult);
                return;

            case "buff_central_highland":
                if (!IsCentralHighlandRole(entity)
                    || !centralHighlandControl.TryGetValue(facility.Id, out string? controlTeam)
                    || !string.Equals(controlTeam, entity.Team, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                ApplyDamageTakenBuff(entity, BuffCentralHighlandDamageTakenMult);
                return;

            case "buff_supply":
                if (!IsFriendlyFacility(entity.Team, facility.Team) || !IsRobotEntity(entity))
                {
                    return;
                }

                ApplySupply(world, entity, facility, deltaTimeSec, events);
                if (string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyDamageTakenBuff(entity, 0.0);
                }

                return;

            case "buff_fort":
                if (!CanUseFortBuff(world, entity, facility))
                {
                    return;
                }

                ApplyDamageTakenBuff(entity, _rules.Facility.FortDamageTakenMult);
                ApplyCoolingBuff(entity, _rules.Facility.FortCoolingMult);
                return;

            case "buff_assembly":
                if (!IsFriendlyFacility(entity.Team, facility.Team)
                    || !string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                ApplyDamageTakenBuff(entity, 0.0);
                return;

            case "buff_hero_deployment":
                touchedHeroDeployment = true;
                if (!IsFriendlyFacility(entity.Team, facility.Team)
                    || !string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!entity.HeroDeploymentRequested
                    || !string.Equals(RuleSet.NormalizeHeroMode(entity.HeroPerformanceMode), "ranged_priority", StringComparison.OrdinalIgnoreCase))
                {
                    entity.HeroDeploymentHoldTimerSec = 0.0;
                    entity.HeroDeploymentActive = false;
                    return;
                }

                entity.HeroDeploymentHoldTimerSec += deltaTimeSec;
                if (entity.HeroDeploymentHoldTimerSec >= 2.0)
                {
                    entity.HeroDeploymentActive = true;
                    ApplyDamageTakenBuff(entity, BuffHeroDeploymentDamageTakenMult);
                }

                return;

            default:
                if (facilityType.StartsWith("buff_terrain_", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyTerrainSequenceBuff(world, entity, facility, facilityType, events);
                }

                return;
        }
    }

    private void ApplyDeadZone(
        SimulationWorldState world,
        SimulationEntity entity,
        FacilityRegion facility,
        double deltaTimeSec,
        ICollection<FacilityInteractionEvent> events)
    {
        entity.DeadZoneTimerSec += deltaTimeSec;
        if (entity.PermanentEliminated || entity.DeadZoneTimerSec < _rules.Facility.DeadZoneEliminationSec)
        {
            return;
        }

        entity.PermanentEliminated = true;
        entity.IsAlive = false;
        entity.State = "eliminated";
        entity.RespawnTimerSec = 0;

        events.Add(new FacilityInteractionEvent(
            world.GameTimeSec,
            entity.Team,
            entity.Id,
            facility.Id,
            "dead_zone",
            "Stayed in dead zone too long and was eliminated."));
    }

    private void ApplyMining(
        SimulationWorldState world,
        SimulationEntity entity,
        FacilityRegion facility,
        double deltaTimeSec,
        ICollection<FacilityInteractionEvent> events)
    {
        if (!IsFriendlyFacility(entity.Team, facility.Team)
            || !string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        entity.MiningProgressSec += deltaTimeSec;
        double duration = (_rules.Facility.MiningDurationMinSec + _rules.Facility.MiningDurationMaxSec) * 0.5;
        if (entity.MiningProgressSec < duration)
        {
            return;
        }

        entity.MiningProgressSec = 0.0;
        entity.CarriedMinerals += Math.Max(1, _rules.Facility.MineralsPerTrip);
        events.Add(new FacilityInteractionEvent(
            world.GameTimeSec,
            entity.Team,
            entity.Id,
            facility.Id,
            "mining",
            $"Mining completed, carried minerals {entity.CarriedMinerals}"));
    }

    private void ApplyExchange(
        SimulationWorldState world,
        SimulationEntity entity,
        FacilityRegion facility,
        double deltaTimeSec,
        ICollection<FacilityInteractionEvent> events)
    {
        if (!IsFriendlyFacility(entity.Team, facility.Team)
            || !string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (entity.CarriedMinerals <= 0)
        {
            entity.ExchangeProgressSec = 0.0;
            return;
        }

        entity.ExchangeProgressSec += deltaTimeSec;
        double duration = (_rules.Facility.ExchangeDurationMinSec + _rules.Facility.ExchangeDurationMaxSec) * 0.5;
        if (entity.ExchangeProgressSec < duration)
        {
            return;
        }

        entity.ExchangeProgressSec = 0.0;
        SimulationTeamState teamState = world.GetOrCreateTeamState(entity.Team);
        double gainedGold = entity.CarriedMinerals * _rules.Facility.GoldPerMineral;
        teamState.Gold += gainedGold;
        entity.CarriedMinerals = 0;

        events.Add(new FacilityInteractionEvent(
            world.GameTimeSec,
            entity.Team,
            entity.Id,
            facility.Id,
            "exchange",
            $"Exchange completed, team gold +{gainedGold:0.##}"));
    }

    private void ApplyEnergyMechanism(
        SimulationWorldState world,
        SimulationEntity entity,
        FacilityRegion facility,
        double deltaTimeSec,
        ISet<string> energyActiveTeams,
        ICollection<FacilityInteractionEvent> events)
    {
        if (!string.Equals(facility.Team, "neutral", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(facility.Team, entity.Team, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!CanActivateEnergy(entity))
        {
            return;
        }

        TryStartEnergyMechanismAttempt(world, entity, facility.Id, energyActiveTeams, events);
    }

    private void TryStartEnergyMechanismAttempt(
        SimulationWorldState world,
        SimulationEntity entity,
        string facilityId,
        ISet<string> energyActiveTeams,
        ICollection<FacilityInteractionEvent> events)
    {
        if (!CanActivateEnergy(entity))
        {
            return;
        }
        SimulationTeamState teamState = world.GetOrCreateTeamState(entity.Team);
        AwardEnergyOpportunityTokens(teamState, world.GameTimeSec);
        energyActiveTeams.Add(entity.Team);
        if (!entity.EnergyActivationRequested
            || string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!TryConsumeEnergyOpportunityToken(teamState, out bool large))
        {
            return;
        }

        int largeSlot = ResolveLargeEnergyAttemptSlot(world.GameTimeSec);
        StartEnergyAttempt(teamState, large, largeSlot, world.GameTimeSec);
        events.Add(new FacilityInteractionEvent(
            world.GameTimeSec,
            entity.Team,
            entity.Id,
            string.IsNullOrWhiteSpace(facilityId) ? "energy_mechanism" : facilityId,
            "energy_mechanism",
            large
                ? "\u5927\u80fd\u91cf\u673a\u5173\u5f00\u59cb\u6fc0\u6d3b\uff1a\u8bf7\u6309\u968f\u673a\u987a\u5e8f\u547d\u4e2d\u5df1\u65b9 5 \u4e2a\u5f85\u6fc0\u6d3b\u5706\u76d8\u3002"
                : "\u5c0f\u80fd\u91cf\u673a\u5173\u5f00\u59cb\u6fc0\u6d3b\uff1a\u8bf7\u6309\u968f\u673a\u987a\u5e8f\u547d\u4e2d\u5df1\u65b9 5 \u4e2a\u5f85\u6fc0\u6d3b\u5706\u76d8\u3002"));
    }
    public FacilityInteractionEvent? ApplyEnergyMechanismHit(
        SimulationWorldState world,
        SimulationEntity shooter,
        ArmorPlateTarget hitPlate,
        int ringScore)
    {
        if (!CanActivateEnergy(shooter)
            || !SimulationCombatMath.TryParseEnergyArmIndex(hitPlate.Id, out string plateTeam, out int armIndex))
        {
            return null;
        }
        SimulationTeamState teamState = world.GetOrCreateTeamState(shooter.Team);
        if (!string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (teamState.EnergyNextModuleDelaySec > 1e-6)
        {
            return null;
        }

        if (!string.Equals(plateTeam, shooter.Team, StringComparison.OrdinalIgnoreCase)
            || (teamState.EnergyCurrentLitMask & (1 << armIndex)) == 0)
        {
            double failedAverageRing = teamState.EnergyHitRingCount > 0
                ? teamState.EnergyHitRingSum / Math.Max(1.0, teamState.EnergyHitRingCount)
                : 0.0;
            ResetEnergyAttemptProgress(teamState, world.GameTimeSec, armIndex + 31);
            return new FacilityInteractionEvent(
                world.GameTimeSec,
                shooter.Team,
                shooter.Id,
                hitPlate.Id,
                "energy_mechanism",
                failedAverageRing > 0.0
                    ? $"\u80fd\u91cf\u673a\u5173\u6fc0\u6d3b\u5931\u8d25\uff1a\u8bef\u51fb\u975e\u5f53\u524d\u76ee\u6807\uff0c\u5e73\u5747\u73af\u6570 {failedAverageRing:0.0}\uff0c\u8fdb\u5ea6\u5df2\u6e05\u96f6\u5e76\u91cd\u65b0\u968f\u673a\u76ee\u6807\u3002"
                    : "\u80fd\u91cf\u673a\u5173\u6fc0\u6d3b\u5931\u8d25\uff1a\u8bef\u51fb\u975e\u5f53\u524d\u76ee\u6807\uff0c\u8fdb\u5ea6\u5df2\u6e05\u96f6\u5e76\u91cd\u65b0\u968f\u673a\u76ee\u6807\u3002");
        }
        int safeRingScore = Math.Clamp(ringScore, 1, 10);
        teamState.EnergyLastRingScore = safeRingScore;
        teamState.EnergyLastHitArmIndex = armIndex;
        teamState.EnergyLastHitFlashEndSec = world.GameTimeSec + 2.0;
        teamState.EnergyHitRingsByArm[armIndex] = Math.Max(teamState.EnergyHitRingsByArm[armIndex], safeRingScore);
        teamState.EnergyActivatedGroupCount++;
        teamState.EnergyHitRingCount++;
        teamState.EnergyHitRingSum += safeRingScore;
        if (teamState.EnergyActivatedGroupCount >= 5)
        {
            if (teamState.EnergyLargeMechanismActive)
            {
                teamState.EnergyLastLargeAttemptSlot = ResolveLargeEnergyAttemptSlot(world.GameTimeSec);
                GrantLargeEnergyBuff(teamState);
            }
            else
            {
                teamState.EnergySmallChanceUsed = true;
                GrantSmallEnergyBuff(teamState);
            }
            teamState.EnergyMechanismState = "activated";
            return new FacilityInteractionEvent(
                world.GameTimeSec,
                shooter.Team,
                shooter.Id,
                hitPlate.Id,
                "energy_mechanism",
                teamState.EnergyLargeMechanismActive
                    ? $"\u5927\u80fd\u91cf\u673a\u5173\u6fc0\u6d3b\u6210\u529f\uff1a\u5e73\u5747\u73af\u6570 {teamState.EnergyHitRingSum / Math.Max(1.0, teamState.EnergyHitRingCount):0.0}\uff0c\u589e\u76ca\u6301\u7eed {teamState.EnergyBuffTimerSec:0} \u79d2\u3002"
                    : $"\u5c0f\u80fd\u91cf\u673a\u5173\u6fc0\u6d3b\u6210\u529f\uff1a\u5168\u961f\u83b7\u5f97 25% \u9632\u5fa1\u589e\u76ca\uff0c\u6301\u7eed {teamState.EnergyBuffTimerSec:0} \u79d2\u3002");
        }
        teamState.EnergyActiveGroupIndex = ResolveCurrentEnergyActiveGroupIndex(teamState);
        teamState.EnergyCurrentLitMask = 0;
        teamState.EnergyLitModuleTimerSec = 0.0;
        teamState.EnergyNextModuleDelaySec = 1.0;
        return new FacilityInteractionEvent(
            world.GameTimeSec,
            shooter.Team,
            shooter.Id,
            hitPlate.Id,
            "energy_mechanism",
            $"\u547d\u4e2d\u80fd\u91cf\u673a\u5173\uff1a\u5df2\u5b8c\u6210 {teamState.EnergyActivatedGroupCount}/5\uff0c\u672c\u6b21\u73af\u6570 {safeRingScore}\u3002");
    }
    private static void TickEnergyMechanismActivation(
        SimulationWorldState world,
        SimulationTeamState teamState,
        double deltaTimeSec,
        ICollection<FacilityInteractionEvent> events)
    {
        if (!string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        teamState.EnergyActivationWindowTimerSec += deltaTimeSec;
        if (teamState.EnergyActivationWindowTimerSec > 20.0)
        {
            double failedAverageRing = teamState.EnergyHitRingCount > 0
                ? teamState.EnergyHitRingSum / Math.Max(1.0, teamState.EnergyHitRingCount)
                : 0.0;
            StopEnergyAttempt(world, teamState);
            events.Add(new FacilityInteractionEvent(
                world.GameTimeSec,
                teamState.Team,
                string.Empty,
                "energy_mechanism",
                "energy_mechanism",
                failedAverageRing > 0.0
                    ? $"\u80fd\u91cf\u673a\u5173\u6fc0\u6d3b\u5931\u8d25\uff1a20 \u79d2\u6fc0\u6d3b\u7a97\u53e3\u7ed3\u675f\uff0c\u5e73\u5747\u73af\u6570 {failedAverageRing:0.0}\uff0c\u5df2\u7184\u706d\u5e76\u56de\u5230\u521d\u59cb\u76f8\u4f4d\u3002"
                    : "\u80fd\u91cf\u673a\u5173\u6fc0\u6d3b\u5931\u8d25\uff1a20 \u79d2\u6fc0\u6d3b\u7a97\u53e3\u7ed3\u675f\uff0c\u5df2\u7184\u706d\u5e76\u56de\u5230\u521d\u59cb\u76f8\u4f4d\u3002"));
            return;
        }
        if (teamState.EnergyNextModuleDelaySec > 1e-6)
        {
            teamState.EnergyNextModuleDelaySec = Math.Max(0.0, teamState.EnergyNextModuleDelaySec - deltaTimeSec);
            if (teamState.EnergyNextModuleDelaySec <= 1e-6)
            {
                teamState.EnergyCurrentLitMask = ResolveEnergyLitMask(teamState);
                teamState.EnergyLitModuleTimerSec = 0.0;
            }
            return;
        }
        teamState.EnergyLitModuleTimerSec += deltaTimeSec;
        if (teamState.EnergyLitModuleTimerSec > 2.5)
        {
            double failedAverageRing = teamState.EnergyHitRingCount > 0
                ? teamState.EnergyHitRingSum / Math.Max(1.0, teamState.EnergyHitRingCount)
                : 0.0;
            ResetEnergyAttemptProgress(teamState, world.GameTimeSec, 47);
            events.Add(new FacilityInteractionEvent(
                world.GameTimeSec,
                teamState.Team,
                string.Empty,
                "energy_mechanism",
                "energy_mechanism",
                failedAverageRing > 0.0
                    ? $"\u80fd\u91cf\u673a\u5173\u6fc0\u6d3b\u5931\u8d25\uff1a\u5f53\u524d\u76ee\u6807\u8d85\u65f6\uff0c\u5e73\u5747\u73af\u6570 {failedAverageRing:0.0}\uff0c\u8fdb\u5ea6\u5df2\u6e05\u96f6\u5e76\u91cd\u65b0\u968f\u673a\u76ee\u6807\u3002"
                    : "\u80fd\u91cf\u673a\u5173\u6fc0\u6d3b\u5931\u8d25\uff1a\u5f53\u524d\u76ee\u6807\u8d85\u65f6\uff0c\u8fdb\u5ea6\u5df2\u6e05\u96f6\u5e76\u91cd\u65b0\u968f\u673a\u76ee\u6807\u3002"));
        }
    }
    private void AwardEnergyOpportunityTokens(SimulationTeamState teamState, double gameTimeSec)
    {
        while (teamState.EnergySmallOpportunityIndex < _rules.Facility.EnergySmallOpportunityTimesSec.Count
            && gameTimeSec >= _rules.Facility.EnergySmallOpportunityTimesSec[teamState.EnergySmallOpportunityIndex])
        {
            teamState.EnergySmallTokens++;
            teamState.EnergySmallOpportunityIndex++;
        }

        while (teamState.EnergyLargeOpportunityIndex < _rules.Facility.EnergyLargeOpportunityTimesSec.Count
            && gameTimeSec >= _rules.Facility.EnergyLargeOpportunityTimesSec[teamState.EnergyLargeOpportunityIndex])
        {
            teamState.EnergyLargeTokens++;
            teamState.EnergyLargeOpportunityIndex++;
        }
    }

    private static bool TryConsumeEnergyOpportunityToken(SimulationTeamState teamState, out bool large)
    {
        large = false;
        if (teamState.EnergyLargeTokens > 0)
        {
            teamState.EnergyLargeTokens--;
            teamState.EnergySmallTokens = 0;
            large = true;
            return true;
        }

        if (teamState.EnergySmallTokens > 0)
        {
            teamState.EnergySmallTokens--;
            return true;
        }

        return false;
    }

    private static void StartEnergyAttempt(SimulationTeamState teamState, bool large, int largeSlot, double gameTimeSec)
    {
        _ = largeSlot;
        teamState.EnergyMechanismState = "activating";
        teamState.EnergyStateStartTimeSec = gameTimeSec;
        teamState.EnergyLargeMechanismActive = large;
        if (large)
        {
            teamState.EnergySmallTokens = 0;
        }
        teamState.EnergyActivationTimerSec = 0.0;
        teamState.EnergyVirtualHits = 0.0;
        teamState.EnergyActivationWindowTimerSec = 0.0;
        teamState.EnergyLitModuleTimerSec = 0.0;
        teamState.EnergyNextModuleDelaySec = 0.0;
        teamState.EnergyActivatedGroupCount = 0;
        teamState.EnergyHitRingCount = 0;
        teamState.EnergyHitRingSum = 0;
        teamState.EnergyLastRingScore = 0;
        teamState.EnergyLastHitArmIndex = -1;
        teamState.EnergyLastHitFlashEndSec = 0.0;
        Array.Clear(teamState.EnergyHitRingsByArm, 0, teamState.EnergyHitRingsByArm.Length);
        PopulateEnergyActivationOrder(teamState, gameTimeSec, large ? 17 : 0);
        teamState.EnergyActiveGroupIndex = ResolveCurrentEnergyActiveGroupIndex(teamState);
        teamState.EnergyCurrentLitMask = ResolveEnergyLitMask(teamState);
    }
    private static int ResolveEnergyLitMask(SimulationTeamState teamState)
    {
        int active = ResolveCurrentEnergyActiveGroupIndex(teamState);
        return active >= 0 ? 1 << active : 0;
    }
    private static int ResolveCurrentEnergyActiveGroupIndex(SimulationTeamState teamState)
    {
        if (teamState.EnergyActivatedGroupCount >= 5)
        {
            return -1;
        }
        int orderIndex = Math.Clamp(teamState.EnergyActivatedGroupCount, 0, 4);
        int active = teamState.EnergyActivationOrder[orderIndex];
        return Math.Clamp(active, 0, 4);
    }
    private static void PopulateEnergyActivationOrder(SimulationTeamState teamState, double gameTimeSec, int salt)
    {
        for (int index = 0; index < teamState.EnergyActivationOrder.Length; index++)
        {
            teamState.EnergyActivationOrder[index] = index;
        }
        int seed = Math.Abs(
            StringComparer.OrdinalIgnoreCase.GetHashCode(teamState.Team)
            ^ ((int)Math.Round(gameTimeSec * 1000.0))
            ^ salt);
        var random = new Random(seed);
        for (int index = teamState.EnergyActivationOrder.Length - 1; index > 0; index--)
        {
            int swapIndex = random.Next(index + 1);
            (teamState.EnergyActivationOrder[index], teamState.EnergyActivationOrder[swapIndex]) =
                (teamState.EnergyActivationOrder[swapIndex], teamState.EnergyActivationOrder[index]);
        }
    }
    private static int ResolveRandomEnergyArmIndex(SimulationTeamState teamState, double gameTimeSec, int salt)
    {
        int seed = Math.Abs(
            StringComparer.OrdinalIgnoreCase.GetHashCode(teamState.Team)
            ^ ((int)Math.Round(gameTimeSec * 1000.0))
            ^ salt);
        return seed % 5;
    }

    private static int ResolveLargeEnergyAttemptSlot(double gameTimeSec)
    {
        if (gameTimeSec >= 330.0)
        {
            return 3;
        }

        if (gameTimeSec >= 245.0)
        {
            return 2;
        }

        return gameTimeSec >= 180.0 ? 1 : 0;
    }

    private static void ResetEnergyAttemptProgress(SimulationTeamState teamState, double gameTimeSec, int salt)
    {
        teamState.EnergyActivatedGroupCount = 0;
        teamState.EnergyHitRingCount = 0;
        teamState.EnergyHitRingSum = 0;
        teamState.EnergyLitModuleTimerSec = 0.0;
        teamState.EnergyNextModuleDelaySec = 0.0;
        PopulateEnergyActivationOrder(teamState, gameTimeSec, salt + teamState.EnergyLastHitArmIndex * 13);
        teamState.EnergyActiveGroupIndex = ResolveCurrentEnergyActiveGroupIndex(teamState);
        ResetEnergyVisualState(teamState);
        teamState.EnergyCurrentLitMask = ResolveEnergyLitMask(teamState);
    }
    private static void StopEnergyAttempt(SimulationWorldState world, SimulationTeamState teamState)
    {
        _ = world;
        teamState.EnergyMechanismState = "inactive";
        teamState.EnergyStateStartTimeSec = world.GameTimeSec;
        teamState.EnergyLargeMechanismActive = false;
        teamState.EnergyActivatedGroupCount = 0;
        teamState.EnergyHitRingCount = 0;
        teamState.EnergyHitRingSum = 0;
        teamState.EnergyActivationTimerSec = 0.0;
        teamState.EnergyVirtualHits = 0.0;
        teamState.EnergyActivationWindowTimerSec = 0.0;
        teamState.EnergyLitModuleTimerSec = 0.0;
        teamState.EnergyNextModuleDelaySec = 0.0;
        Array.Clear(teamState.EnergyActivationOrder, 0, teamState.EnergyActivationOrder.Length);
        ResetEnergyVisualState(teamState);
    }

    private void GrantSmallEnergyBuff(SimulationTeamState teamState)
    {
        teamState.EnergyBuffTimerSec = Math.Max(teamState.EnergyBuffTimerSec, _rules.Facility.EnergySmallBuffDurationSec);
        teamState.EnergyMechanismState = "activated";
        teamState.EnergyBuffDamageDealtMult = Math.Max(teamState.EnergyBuffDamageDealtMult, 1.0);
        teamState.EnergyBuffDamageTakenMult = Math.Min(teamState.EnergyBuffDamageTakenMult, 0.75);
        teamState.EnergyBuffCoolingMult = Math.Max(teamState.EnergyBuffCoolingMult, 1.0);
        teamState.EnergyCurrentLitMask = 0;
    }

    private static void GrantLargeEnergyBuff(SimulationTeamState teamState)
    {
        double averageRing = teamState.EnergyHitRingSum / Math.Max(1.0, teamState.EnergyHitRingCount);
        int durationRing = Math.Clamp((int)Math.Round(averageRing), 5, 10);
        double durationSec = durationRing switch
        {
            <= 5 => 30.0,
            6 => 35.0,
            7 => 40.0,
            8 => 45.0,
            9 => 50.0,
            _ => 60.0,
        };

        double damageDealtMult;
        double damageTakenMult;
        double coolingMult;
        if (averageRing >= 9.0)
        {
            damageDealtMult = 3.00;
            damageTakenMult = 0.50;
            coolingMult = 5.00;
        }
        else if (averageRing >= 8.0)
        {
            damageDealtMult = 2.00;
            damageTakenMult = 0.75;
            coolingMult = 3.00;
        }
        else if (averageRing >= 7.0)
        {
            damageDealtMult = 2.00;
            damageTakenMult = 0.75;
            coolingMult = 2.00;
        }
        else
        {
            damageDealtMult = 1.50;
            damageTakenMult = 0.75;
            coolingMult = 2.00;
        }

        teamState.EnergyBuffTimerSec = Math.Max(teamState.EnergyBuffTimerSec, durationSec);
        teamState.EnergyMechanismState = "activated";
        teamState.EnergyBuffDamageDealtMult = Math.Max(teamState.EnergyBuffDamageDealtMult, damageDealtMult);
        teamState.EnergyBuffDamageTakenMult = Math.Min(teamState.EnergyBuffDamageTakenMult, damageTakenMult);
        teamState.EnergyBuffCoolingMult = Math.Max(teamState.EnergyBuffCoolingMult, coolingMult);
        teamState.EnergyCurrentLitMask = 0;
    }

    private static void ResetEnergyVisualState(SimulationTeamState teamState)
    {
        teamState.EnergyCurrentLitMask = 0;
        teamState.EnergyLastRingScore = 0;
        teamState.EnergyLastHitArmIndex = -1;
        teamState.EnergyLastHitFlashEndSec = 0.0;
        Array.Clear(teamState.EnergyHitRingsByArm, 0, teamState.EnergyHitRingsByArm.Length);
    }

    private static void NormalizeEnergyRotorDirections(SimulationWorldState world)
    {
        SimulationTeamState red = world.GetOrCreateTeamState("red");
        SimulationTeamState blue = world.GetOrCreateTeamState("blue");
        if (red.EnergyRotorDirectionSign == 0)
        {
            red.EnergyRotorDirectionSign = 1;
        }

        red.EnergyRotorDirectionSign = red.EnergyRotorDirectionSign < 0 ? -1 : 1;
        blue.EnergyRotorDirectionSign = -red.EnergyRotorDirectionSign;
    }

    private static IReadOnlyDictionary<string, string> ResolveCentralHighlandControl(
        SimulationWorldState world,
        IReadOnlyList<FacilityRegion> facilities)
    {
        var control = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (FacilityRegion facility in facilities)
        {
            if (!string.Equals(NormalizeFacilityType(facility.Type), "buff_central_highland", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? occupyingTeam = null;
            bool contested = false;
            foreach (SimulationEntity entity in world.Entities)
            {
                if (!entity.IsAlive || !IsCentralHighlandRole(entity) || !facility.Contains(entity.X, entity.Y))
                {
                    continue;
                }

                if (occupyingTeam is null)
                {
                    occupyingTeam = entity.Team;
                    continue;
                }

                if (!string.Equals(occupyingTeam, entity.Team, StringComparison.OrdinalIgnoreCase))
                {
                    contested = true;
                    break;
                }
            }

            if (!contested && !string.IsNullOrWhiteSpace(occupyingTeam))
            {
                control[facility.Id] = occupyingTeam;
            }
        }

        return control;
    }

    private static void TickTimedBuffs(SimulationEntity entity, double deltaTimeSec)
    {
        entity.TerrainSequenceTimerSec = Math.Max(0.0, entity.TerrainSequenceTimerSec - deltaTimeSec);
        entity.TerrainRoadLockoutTimerSec = Math.Max(0.0, entity.TerrainRoadLockoutTimerSec - deltaTimeSec);
        entity.TerrainHighlandDefenseTimerSec = Math.Max(0.0, entity.TerrainHighlandDefenseTimerSec - deltaTimeSec);
        entity.TerrainFlySlopeDefenseTimerSec = Math.Max(0.0, entity.TerrainFlySlopeDefenseTimerSec - deltaTimeSec);
        entity.TerrainRoadCoolingTimerSec = Math.Max(0.0, entity.TerrainRoadCoolingTimerSec - deltaTimeSec);
        entity.TerrainSlopeDefenseTimerSec = Math.Max(0.0, entity.TerrainSlopeDefenseTimerSec - deltaTimeSec);
        entity.TerrainSlopeCoolingTimerSec = Math.Max(0.0, entity.TerrainSlopeCoolingTimerSec - deltaTimeSec);
        if (entity.TerrainSequenceTimerSec <= 1e-6)
        {
            entity.TerrainSequenceKey = string.Empty;
        }
    }

    private static void ApplyTimedBuffEffects(SimulationEntity entity)
    {
        if (entity.TerrainHighlandDefenseTimerSec > 1e-6)
        {
            ApplyDamageTakenBuff(entity, TerrainHighlandDamageTakenMult);
        }

        if (entity.TerrainFlySlopeDefenseTimerSec > 1e-6)
        {
            ApplyDamageTakenBuff(entity, TerrainFlySlopeDamageTakenMult);
        }

        if (entity.TerrainRoadCoolingTimerSec > 1e-6)
        {
            ApplyCoolingBuff(entity, TerrainRoadCoolingMult);
        }

        if (entity.TerrainSlopeDefenseTimerSec > 1e-6)
        {
            ApplyDamageTakenBuff(entity, TerrainSlopeDamageTakenMult);
        }

        if (entity.TerrainSlopeCoolingTimerSec > 1e-6)
        {
            ApplyCoolingBuff(entity, TerrainSlopeCoolingMult);
        }
    }

    private static void ApplyDamageTakenBuff(SimulationEntity entity, double multiplier)
    {
        if (multiplier <= 1e-9)
        {
            entity.DynamicDamageTakenMult = 0.0;
            return;
        }

        if (entity.DynamicDamageTakenMult <= 1e-9)
        {
            return;
        }

        entity.DynamicDamageTakenMult = Math.Min(entity.DynamicDamageTakenMult, multiplier);
    }

    private static void ApplyDamageDealtBuff(SimulationEntity entity, double multiplier)
    {
        entity.DynamicDamageDealtMult = Math.Max(entity.DynamicDamageDealtMult, Math.Max(0.0, multiplier));
    }

    private static void ApplyCoolingBuff(SimulationEntity entity, double multiplier)
    {
        entity.DynamicCoolingMult = Math.Max(entity.DynamicCoolingMult, Math.Max(0.0, multiplier));
    }

    private static void ApplyPowerRecoveryBuff(SimulationEntity entity, double multiplier)
    {
        entity.DynamicPowerRecoveryMult = Math.Max(entity.DynamicPowerRecoveryMult, Math.Max(0.0, multiplier));
    }

    private void ApplyTerrainSequenceBuff(
        SimulationWorldState world,
        SimulationEntity entity,
        FacilityRegion facility,
        string facilityType,
        ICollection<FacilityInteractionEvent> events)
    {
        if (!IsFriendlyFacility(entity.Team, facility.Team)
            || !IsTerrainBuffEligible(entity))
        {
            return;
        }

        if (facilityType.EndsWith("_start", StringComparison.OrdinalIgnoreCase))
        {
            entity.TerrainSequenceKey = ExtractTerrainSequenceKey(facilityType);
            entity.TerrainSequenceTimerSec = ResolveTerrainSequenceTimeoutSec(facilityType);
            return;
        }

        if (!facilityType.EndsWith("_end", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string sequenceKey = ExtractTerrainSequenceKey(facilityType);
        if (entity.TerrainSequenceTimerSec <= 1e-6
            || !string.Equals(entity.TerrainSequenceKey, sequenceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (sequenceKey.Contains("terrain_road", StringComparison.OrdinalIgnoreCase)
            && entity.TerrainRoadLockoutTimerSec > 1e-6)
        {
            entity.TerrainSequenceKey = string.Empty;
            entity.TerrainSequenceTimerSec = 0.0;
            return;
        }

        switch (sequenceKey)
        {
            case "terrain_highland_red":
            case "terrain_highland_blue":
                entity.TerrainHighlandDefenseTimerSec = Math.Max(entity.TerrainHighlandDefenseTimerSec, 30.0);
                break;
            case "terrain_fly_slope_red":
            case "terrain_fly_slope_blue":
                entity.TerrainFlySlopeDefenseTimerSec = Math.Max(entity.TerrainFlySlopeDefenseTimerSec, 30.0);
                break;
            case "terrain_road_red":
            case "terrain_road_blue":
                entity.TerrainRoadCoolingTimerSec = Math.Max(entity.TerrainRoadCoolingTimerSec, 5.0);
                entity.TerrainRoadLockoutTimerSec = Math.Max(entity.TerrainRoadLockoutTimerSec, 15.0);
                break;
            case "terrain_slope_red":
            case "terrain_slope_blue":
                entity.TerrainSlopeDefenseTimerSec = Math.Max(entity.TerrainSlopeDefenseTimerSec, 10.0);
                entity.TerrainSlopeCoolingTimerSec = Math.Max(entity.TerrainSlopeCoolingTimerSec, 120.0);
                break;
        }

        entity.TerrainSequenceKey = string.Empty;
        entity.TerrainSequenceTimerSec = 0.0;
        events.Add(new FacilityInteractionEvent(
            world.GameTimeSec,
            entity.Team,
            entity.Id,
            facility.Id,
            facilityType,
            $"Activated {sequenceKey}"));
    }

    private static string ExtractTerrainSequenceKey(string facilityType)
    {
        string normalized = NormalizeFacilityType(facilityType);
        string withoutPrefix = normalized.StartsWith("buff_", StringComparison.OrdinalIgnoreCase)
            ? normalized["buff_".Length..]
            : normalized;
        if (withoutPrefix.EndsWith("_start", StringComparison.OrdinalIgnoreCase))
        {
            return withoutPrefix[..^"_start".Length];
        }

        if (withoutPrefix.EndsWith("_end", StringComparison.OrdinalIgnoreCase))
        {
            return withoutPrefix[..^"_end".Length];
        }

        return withoutPrefix;
    }

    private static double ResolveTerrainSequenceTimeoutSec(string facilityType)
    {
        string key = ExtractTerrainSequenceKey(facilityType);
        if (key.Contains("terrain_highland", StringComparison.OrdinalIgnoreCase))
        {
            return 5.0;
        }

        if (key.Contains("terrain_slope", StringComparison.OrdinalIgnoreCase))
        {
            return 10.0;
        }

        return 3.0;
    }

    private static bool IsTerrainBuffEligible(SimulationEntity entity)
        => string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase);

    private static bool IsRobotEntity(SimulationEntity entity)
        => string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase);

    private static bool IsCentralHighlandRole(SimulationEntity entity)
        => string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase);

    private static void ClearWeakState(SimulationEntity entity)
    {
        entity.WeakTimerSec = 0.0;
        if (string.Equals(entity.State, "weak", StringComparison.OrdinalIgnoreCase)
            && entity.RespawnAmmoLockTimerSec <= 1e-6)
        {
            entity.State = "idle";
        }
    }

    private static bool CanUseOutpostBuff(SimulationWorldState world, SimulationEntity entity, FacilityRegion facility)
    {
        if (!IsFriendlyFacility(entity.Team, facility.Team)
            || (!string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        SimulationEntity? outpost = FindStructure(world, facility.Team, "outpost");
        return outpost is not null && outpost.IsAlive;
    }

    private bool CanUseFortBuff(SimulationWorldState world, SimulationEntity entity, FacilityRegion facility)
    {
        if (!IsFriendlyFacility(entity.Team, facility.Team)
            || (!string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        SimulationEntity? outpost = FindStructure(world, facility.Team, "outpost");
        return outpost is null || !outpost.IsAlive;
    }

    private static SimulationEntity? FindStructure(SimulationWorldState world, string team, string entityType)
    {
        foreach (SimulationEntity entity in world.Entities)
        {
            if (string.Equals(entity.Team, team, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entity.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
            {
                return entity;
            }
        }

        return null;
    }

    private bool CanActivateEnergy(SimulationEntity entity)
    {
        if (!entity.IsAlive)
        {
            return false;
        }

        if (!string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return _rules.Facility.EnergyAllowedRoles.Contains(entity.RoleKey);
    }

    private double ResolveEnergyVirtualHitRate(string roleKey)
    {
        return _rules.Facility.EnergyVirtualHitsPerSec.TryGetValue(roleKey, out double rate)
            ? rate
            : _rules.Facility.EnergyVirtualHitsPerSec.GetValueOrDefault("infantry", 0.36);
    }

    private static bool IsFriendlyFacility(string team, string facilityTeam)
    {
        if (string.IsNullOrWhiteSpace(facilityTeam)
            || string.Equals(facilityTeam, "neutral", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(team, facilityTeam, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFacilityType(string type)
    {
        string normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "supply_zone" => "supply",
            "mining_zone" => "mining",
            "exchange_zone" => "exchange",
            _ => normalized,
        };
    }
}
