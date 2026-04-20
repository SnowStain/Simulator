using Simulator.Core.Map;

namespace Simulator.Core.Gameplay;

public sealed class ArenaInteractionService
{
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

        foreach (SimulationTeamState team in world.Teams.Values)
        {
            team.EnergyBuffTimerSec = Math.Max(0.0, team.EnergyBuffTimerSec - deltaTimeSec);
        }

        foreach (SimulationEntity entity in world.Entities)
        {
            entity.ResetDynamicEffects();
            entity.SupplyBuyCooldownSec = Math.Max(0.0, entity.SupplyBuyCooldownSec - deltaTimeSec);
            if (!entity.IsAlive)
            {
                continue;
            }

            bool touchedDeadZone = false;

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
                }
            }

            if (!touchedDeadZone)
            {
                entity.DeadZoneTimerSec = 0.0;
            }
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
                entity.DynamicDamageDealtMult *= _rules.Facility.EnergyDamageDealtMult;
                entity.DynamicCoolingMult *= _rules.Facility.EnergyCoolingMult;
                entity.DynamicPowerRecoveryMult *= _rules.Facility.EnergyPowerRecoveryMult;
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

        entity.DynamicDamageTakenMult *= _rules.Facility.FortDamageTakenMult;
        entity.DynamicCoolingMult *= _rules.Facility.FortCoolingMult;
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
