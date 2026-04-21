using Simulator.Core.Map;

namespace Simulator.Core.Gameplay;

public sealed class ArenaInteractionService
{
    private const double BuffBaseDamageTakenMult = 0.50;
    private const double BuffCentralHighlandDamageTakenMult = 0.75;
    private const double BuffTrapezoidHighlandDamageTakenMult = 0.50;
    private const double BuffOutpostDamageTakenMult = 0.75;
    private const double BuffHeroDeploymentDamageTakenMult = 0.75;
    private const double BuffHeroDeploymentDamageDealtMult = 1.50;
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

        foreach (SimulationTeamState team in world.Teams.Values)
        {
            team.EnergyBuffTimerSec = Math.Max(0.0, team.EnergyBuffTimerSec - deltaTimeSec);
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
                        ApplyFort(entity, facility);
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

            SimulationTeamState teamState = world.GetOrCreateTeamState(entity.Team);
            if (teamState.EnergyBuffTimerSec > 0)
            {
                ApplyDamageDealtBuff(entity, _rules.Facility.EnergyDamageDealtMult);
                ApplyCoolingBuff(entity, _rules.Facility.EnergyCoolingMult);
                ApplyPowerRecoveryBuff(entity, _rules.Facility.EnergyPowerRecoveryMult);
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

    private void ApplyFort(SimulationEntity entity, FacilityRegion facility)
    {
        if (!IsFriendlyFacility(entity.Team, facility.Team))
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
                ClearWeakState(entity);
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
                    ApplyDamageDealtBuff(entity, BuffHeroDeploymentDamageDealtMult);
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

        SimulationTeamState teamState = world.GetOrCreateTeamState(entity.Team);
        energyActiveTeams.Add(entity.Team);

        teamState.EnergyActivationTimerSec += deltaTimeSec;
        teamState.EnergyVirtualHits += ResolveEnergyVirtualHitRate(entity.RoleKey) * deltaTimeSec;

        if (teamState.EnergyActivationTimerSec < _rules.Facility.EnergyActivationHoldSec)
        {
            return;
        }

        int requiredHits = world.GameTimeSec >= _rules.Facility.EnergyLargeWindowStartSec
            ? _rules.Facility.EnergyLargeHitThreshold
            : _rules.Facility.EnergySmallHitThreshold;

        bool activated = teamState.EnergyVirtualHits >= requiredHits;
        if (activated)
        {
            teamState.EnergyBuffTimerSec = Math.Max(teamState.EnergyBuffTimerSec, _rules.Facility.EnergyBuffDurationSec);
            events.Add(new FacilityInteractionEvent(
                world.GameTimeSec,
                entity.Team,
                entity.Id,
                facility.Id,
                "energy_mechanism",
                $"Energy mechanism activated, effective hits {teamState.EnergyVirtualHits:0.##}"));
        }
        else
        {
            events.Add(new FacilityInteractionEvent(
                world.GameTimeSec,
                entity.Team,
                entity.Id,
                facility.Id,
                "energy_mechanism",
                $"Energy mechanism failed, effective hits {teamState.EnergyVirtualHits:0.##}"));
        }

        teamState.EnergyActivationTimerSec = 0.0;
        teamState.EnergyVirtualHits = 0.0;
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

        if (string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase))
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
