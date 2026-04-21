using System.Text.Json.Nodes;

namespace Simulator.Core.Gameplay;

public sealed record RuleValidationIssue(string Severity, string Message);

public sealed class RuleSetLoader
{
    public RuleSet LoadFromConfig(JsonObject config)
    {
        RuleSet ruleSet = RuleSet.CreateDefault();
        JsonObject? rules = config["rules"] as JsonObject;
        if (rules is null)
        {
            return ruleSet;
        }

        ruleSet.GameDurationSec = GetDouble(rules, ruleSet.GameDurationSec, "game_duration");

        ruleSet.Combat.BaseHitProbability = GetDouble(rules, ruleSet.Combat.BaseHitProbability, "shooting", "base_hit_probability");
        ruleSet.Combat.MinHitProbability = GetDouble(rules, ruleSet.Combat.MinHitProbability, "shooting", "min_hit_probability");
        ruleSet.Combat.RangeFalloff = GetDouble(rules, ruleSet.Combat.RangeFalloff, "shooting", "range_falloff");
        ruleSet.Combat.AutoAimMaxDistanceM = GetDouble(rules, ruleSet.Combat.AutoAimMaxDistanceM, "shooting", "auto_aim_max_distance_m");
        ruleSet.Combat.FastSpinHitMultiplier = GetDouble(rules, ruleSet.Combat.FastSpinHitMultiplier, "shooting", "fast_spin_hit_multiplier");
        ruleSet.Combat.FireRateHz = GetDouble(rules, ruleSet.Combat.FireRateHz, "shooting", "fire_rate_hz");
        ruleSet.Combat.Damage17ToRobot = GetDouble(rules, ruleSet.Combat.Damage17ToRobot, "damage", "robot", "bullet_17mm");
        ruleSet.Combat.Damage42ToRobot = GetDouble(rules, ruleSet.Combat.Damage42ToRobot, "damage", "robot", "bullet_42mm");
        ruleSet.Combat.Damage17ToStructure = GetDouble(rules, ruleSet.Combat.Damage17ToStructure, "damage", "base", "bullet_17mm");
        ruleSet.Combat.Damage42ToStructure = GetDouble(rules, ruleSet.Combat.Damage42ToStructure, "damage", "base", "bullet_42mm");
        ruleSet.Combat.Damage17ToBaseFrontUpperArmor = GetDouble(rules, ruleSet.Combat.Damage17ToBaseFrontUpperArmor, "damage", "base", "bullet_17mm_front_upper");
        ruleSet.Combat.Damage17ToBaseOtherArmor = GetDouble(rules, ruleSet.Combat.Damage17ToBaseOtherArmor, "damage", "base", "bullet_17mm");
        ruleSet.Combat.Damage17ToOutpostArmor = GetDouble(rules, ruleSet.Combat.Damage17ToOutpostArmor, "damage", "outpost", "bullet_17mm");
        ruleSet.Combat.Damage42ToOutpostArmor = GetDouble(rules, ruleSet.Combat.Damage42ToOutpostArmor, "damage", "outpost", "bullet_42mm");
        ruleSet.Combat.CollisionDamageToRobot = GetDouble(rules, ruleSet.Combat.CollisionDamageToRobot, "damage", "robot", "collision");

        ruleSet.Heat.HeatDetectionHz = GetDouble(rules, ruleSet.Heat.HeatDetectionHz, "shooting", "heat_detection_hz");
        ruleSet.Heat.HeatGain17 = GetDouble(rules, ruleSet.Heat.HeatGain17, "shooting", "heat_gain_17mm");
        ruleSet.Heat.HeatGain42 = GetDouble(rules, ruleSet.Heat.HeatGain42, "shooting", "heat_gain_42mm");
        ruleSet.Heat.OverheatLockDurationSec = GetDouble(rules, ruleSet.Heat.OverheatLockDurationSec, "shooting", "overheat_lock_duration");

        ruleSet.Respawn.RobotDelaySec = GetDouble(rules, ruleSet.Respawn.RobotDelaySec, "respawn", "robot_delay");
        ruleSet.Respawn.InvalidDurationSec = GetDouble(rules, ruleSet.Respawn.InvalidDurationSec, "respawn", "invalid_duration");
        ruleSet.Respawn.InvincibleDurationSec = GetDouble(rules, ruleSet.Respawn.InvincibleDurationSec, "respawn", "invincible_duration");
        ruleSet.Respawn.WeakenDamageDealtMult = GetDouble(rules, ruleSet.Respawn.WeakenDamageDealtMult, "respawn", "weaken_damage_dealt_mult");
        ruleSet.Respawn.WeakenDamageTakenMult = GetDouble(rules, ruleSet.Respawn.WeakenDamageTakenMult, "respawn", "weaken_damage_taken_mult");

        ruleSet.Facility.SupplyIntervalSec = GetDouble(rules, ruleSet.Facility.SupplyIntervalSec, "supply", "ammo_interval");
        ruleSet.Facility.SupplyAmmoGain17Mm = GetInt(rules, ruleSet.Facility.SupplyAmmoGain17Mm, "supply", "ammo_gain_17mm");
        ruleSet.Facility.SupplyAmmoGain17Mm = GetInt(rules, ruleSet.Facility.SupplyAmmoGain17Mm, "supply", "ammo_gain");
        ruleSet.Facility.SupplyAmmoGain42Mm = GetInt(rules, ruleSet.Facility.SupplyAmmoGain42Mm, "supply", "ammo_gain_42mm");

        ruleSet.Facility.MiningDurationMinSec = GetDouble(rules, ruleSet.Facility.MiningDurationMinSec, "mining", "mine_duration_min_sec");
        ruleSet.Facility.MiningDurationMaxSec = GetDouble(rules, ruleSet.Facility.MiningDurationMaxSec, "mining", "mine_duration_max_sec");
        ruleSet.Facility.ExchangeDurationMinSec = GetDouble(rules, ruleSet.Facility.ExchangeDurationMinSec, "mining", "exchange_duration_min_sec");
        ruleSet.Facility.ExchangeDurationMaxSec = GetDouble(rules, ruleSet.Facility.ExchangeDurationMaxSec, "mining", "exchange_duration_max_sec");
        ruleSet.Facility.MineralsPerTrip = GetInt(rules, ruleSet.Facility.MineralsPerTrip, "mining", "minerals_per_trip");
        ruleSet.Facility.GoldPerMineral = GetDouble(rules, ruleSet.Facility.GoldPerMineral, "mining", "gold_per_mineral");

        ruleSet.Facility.FortDamageTakenMult = GetDouble(rules, ruleSet.Facility.FortDamageTakenMult, "fort", "damage_taken_mult");
        ruleSet.Facility.FortCoolingMult = GetDouble(rules, ruleSet.Facility.FortCoolingMult, "fort", "cooling_mult");
        ruleSet.Facility.DeadZoneEliminationSec = GetDouble(rules, ruleSet.Facility.DeadZoneEliminationSec, "dead_zone", "permanent_elimination_sec");

        ruleSet.Facility.EnergyActivationHoldSec = GetDouble(rules, ruleSet.Facility.EnergyActivationHoldSec, "energy_mechanism", "activation_hold_sec");
        ruleSet.Facility.EnergyActivationWindowSec = GetDouble(rules, ruleSet.Facility.EnergyActivationWindowSec, "energy_mechanism", "activation_window_sec");
        ruleSet.Facility.EnergySmallHitThreshold = GetInt(rules, ruleSet.Facility.EnergySmallHitThreshold, "energy_mechanism", "small_hit_threshold");
        ruleSet.Facility.EnergyLargeHitThreshold = GetInt(rules, ruleSet.Facility.EnergyLargeHitThreshold, "energy_mechanism", "large_hit_threshold");
        ruleSet.Facility.EnergyLargeWindowStartSec = GetDouble(rules, ruleSet.Facility.EnergyLargeWindowStartSec, "energy_mechanism", "large_window_start_sec");
        ruleSet.Facility.EnergyBuffDurationSec = GetDouble(rules, ruleSet.Facility.EnergyBuffDurationSec, "energy_mechanism", "buff_duration_sec");
        ruleSet.Facility.EnergySmallBuffDurationSec = GetDouble(rules, ruleSet.Facility.EnergySmallBuffDurationSec, "energy_mechanism", "small_buff_duration_sec");
        ruleSet.Facility.EnergyDamageDealtMult = GetDouble(rules, ruleSet.Facility.EnergyDamageDealtMult, "energy_mechanism", "damage_dealt_mult");
        ruleSet.Facility.EnergyCoolingMult = GetDouble(rules, ruleSet.Facility.EnergyCoolingMult, "energy_mechanism", "cooling_mult");
        ruleSet.Facility.EnergyPowerRecoveryMult = GetDouble(rules, ruleSet.Facility.EnergyPowerRecoveryMult, "energy_mechanism", "power_recovery_mult");
        ReadDoubleArray(rules, ruleSet.Facility.EnergySmallOpportunityTimesSec, "energy_mechanism", "small_opportunity_times_sec");
        ReadDoubleArray(rules, ruleSet.Facility.EnergyLargeOpportunityTimesSec, "energy_mechanism", "large_opportunity_times_sec");

        JsonNode? allowedRolesNode = GetNode(rules, "energy_mechanism", "allowed_role_keys");
        if (allowedRolesNode is JsonArray allowedRolesArray)
        {
            ruleSet.Facility.EnergyAllowedRoles.Clear();
            foreach (JsonNode? item in allowedRolesArray)
            {
                if (item is null)
                {
                    continue;
                }

                string role = NormalizeRoleKey(item.ToString());
                if (!string.IsNullOrWhiteSpace(role))
                {
                    ruleSet.Facility.EnergyAllowedRoles.Add(role);
                }
            }
        }

        JsonNode? virtualHitsNode = GetNode(rules, "energy_mechanism", "virtual_hits_per_sec");
        if (virtualHitsNode is JsonObject virtualHitsObject)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in virtualHitsObject)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                if (TryReadDouble(pair.Value, out double value))
                {
                    ruleSet.Facility.EnergyVirtualHitsPerSec[NormalizeRoleKey(pair.Key)] = value;
                }
            }
        }

        JsonNode? robotProfilesNode = GetNode(rules, "robot_profiles");
        if (robotProfilesNode is JsonObject robotProfiles)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in robotProfiles)
            {
                if (pair.Value is not JsonObject profileNode)
                {
                    continue;
                }

                string roleKey = NormalizeRoleKey(pair.Key);
                RoleProfile profile = ruleSet.ResolveRoleProfile(roleKey);
                profile.MaxHealth = GetDouble(profileNode, profile.MaxHealth, "max_health");
                profile.MaxPower = GetDouble(profileNode, profile.MaxPower, "max_power");
                profile.MaxHeat = GetDouble(profileNode, profile.MaxHeat, "max_heat");
                profile.PowerRecoveryRate = GetDouble(profileNode, profile.PowerRecoveryRate, "power_recovery_rate");
                profile.HeatDissipationRate = GetDouble(profileNode, profile.HeatDissipationRate, "heat_dissipation_rate");
                profile.AmmoType = GetString(profileNode, profile.AmmoType, "ammo_type");
                profile.InitialAllowedAmmo17Mm = GetInt(profileNode, profile.InitialAllowedAmmo17Mm, "initial_allowed_ammo_17mm");
                profile.InitialAllowedAmmo42Mm = GetInt(profileNode, profile.InitialAllowedAmmo42Mm, "initial_allowed_ammo_42mm");
                ruleSet.RoleProfiles[roleKey] = profile;
            }
        }

        string defaultSentryMode = GetString(rules, "auto", "sentry", "default_mode");
        JsonNode? selectedModeNode = GetNode(rules, "sentry", "modes", defaultSentryMode);
        if (selectedModeNode is JsonObject sentryMode)
        {
            RoleProfile profile = ruleSet.ResolveRoleProfile("sentry");
            profile.MaxHealth = GetDouble(sentryMode, profile.MaxHealth, "max_health");
            profile.MaxPower = GetDouble(sentryMode, profile.MaxPower, "max_power");
            profile.MaxHeat = GetDouble(sentryMode, profile.MaxHeat, "max_heat");
            profile.PowerRecoveryRate = GetDouble(sentryMode, profile.PowerRecoveryRate, "power_recovery_rate");
            profile.HeatDissipationRate = GetDouble(sentryMode, profile.HeatDissipationRate, "heat_dissipation_rate");
            profile.AmmoType = GetString(sentryMode, profile.AmmoType, "ammo_type");
            profile.InitialAllowedAmmo17Mm = GetInt(sentryMode, profile.InitialAllowedAmmo17Mm, "initial_allowed_ammo_17mm");
            profile.InitialAllowedAmmo42Mm = GetInt(sentryMode, profile.InitialAllowedAmmo42Mm, "initial_allowed_ammo_42mm");
            ruleSet.RoleProfiles["sentry"] = profile;
        }

        return ruleSet;
    }

    public IReadOnlyList<RuleValidationIssue> Validate(RuleSet ruleSet)
    {
        var issues = new List<RuleValidationIssue>();

        if (ruleSet.GameDurationSec <= 0)
        {
            issues.Add(new RuleValidationIssue("error", "rules.game_duration must be > 0"));
        }

        if (ruleSet.Combat.AutoAimMaxDistanceM <= 0)
        {
            issues.Add(new RuleValidationIssue("error", "shooting.auto_aim_max_distance_m must be > 0"));
        }

        if (ruleSet.Combat.BaseHitProbability <= 0 || ruleSet.Combat.BaseHitProbability > 1)
        {
            issues.Add(new RuleValidationIssue("error", "shooting.base_hit_probability must be in (0, 1]"));
        }

        if (ruleSet.Combat.MinHitProbability < 0 || ruleSet.Combat.MinHitProbability > 1)
        {
            issues.Add(new RuleValidationIssue("error", "shooting.min_hit_probability must be in [0, 1]"));
        }

        if (ruleSet.Heat.HeatDetectionHz <= 0)
        {
            issues.Add(new RuleValidationIssue("error", "shooting.heat_detection_hz must be > 0"));
        }

        if (ruleSet.Respawn.RobotDelaySec < 0)
        {
            issues.Add(new RuleValidationIssue("error", "respawn.robot_delay must be >= 0"));
        }

        if (ruleSet.Facility.SupplyIntervalSec <= 0)
        {
            issues.Add(new RuleValidationIssue("error", "supply.ammo_interval must be > 0"));
        }

        if (ruleSet.Facility.MiningDurationMinSec <= 0 || ruleSet.Facility.MiningDurationMaxSec < ruleSet.Facility.MiningDurationMinSec)
        {
            issues.Add(new RuleValidationIssue("error", "mining duration range is invalid"));
        }

        if (ruleSet.Facility.ExchangeDurationMinSec <= 0 || ruleSet.Facility.ExchangeDurationMaxSec < ruleSet.Facility.ExchangeDurationMinSec)
        {
            issues.Add(new RuleValidationIssue("error", "exchange duration range is invalid"));
        }

        if (ruleSet.Facility.EnergyActivationHoldSec <= 0)
        {
            issues.Add(new RuleValidationIssue("error", "energy_mechanism.activation_hold_sec must be > 0"));
        }

        foreach (string role in new[] { "hero", "engineer", "infantry", "sentry" })
        {
            if (!ruleSet.RoleProfiles.ContainsKey(role))
            {
                issues.Add(new RuleValidationIssue("warning", $"Missing role profile: {role}"));
                continue;
            }

            RoleProfile profile = ruleSet.RoleProfiles[role];
            if (profile.MaxHealth <= 0)
            {
                issues.Add(new RuleValidationIssue("error", $"Role '{role}' max_health must be > 0"));
            }

            if (profile.MaxPower < 0)
            {
                issues.Add(new RuleValidationIssue("error", $"Role '{role}' max_power must be >= 0"));
            }

            if (profile.MaxHeat < 0)
            {
                issues.Add(new RuleValidationIssue("error", $"Role '{role}' max_heat must be >= 0"));
            }
        }

        return issues;
    }

    private static string NormalizeRoleKey(string roleKey)
    {
        string normalized = (roleKey ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "英雄" => "hero",
            "工程" => "engineer",
            "步兵" => "infantry",
            "哨兵" => "sentry",
            _ => normalized,
        };
    }

    private static JsonNode? GetNode(JsonObject root, params string[] path)
    {
        JsonNode? current = root;
        foreach (string segment in path)
        {
            if (current is not JsonObject currentObject)
            {
                return null;
            }

            current = currentObject[segment];
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static bool TryReadDouble(JsonNode node, out double value)
    {
        value = 0;
        if (node is JsonValue valueNode)
        {
            if (valueNode.TryGetValue(out double numeric))
            {
                value = numeric;
                return true;
            }

            if (valueNode.TryGetValue(out int intNumeric))
            {
                value = intNumeric;
                return true;
            }

            if (valueNode.TryGetValue(out string? text)
                && double.TryParse(text, out numeric))
            {
                value = numeric;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadInt(JsonNode node, out int value)
    {
        value = 0;
        if (node is JsonValue valueNode)
        {
            if (valueNode.TryGetValue(out int numeric))
            {
                value = numeric;
                return true;
            }

            if (valueNode.TryGetValue(out double doubleNumeric))
            {
                value = Convert.ToInt32(doubleNumeric);
                return true;
            }

            if (valueNode.TryGetValue(out string? text)
                && int.TryParse(text, out numeric))
            {
                value = numeric;
                return true;
            }
        }

        return false;
    }

    private static void ReadDoubleArray(JsonObject root, List<double> target, params string[] path)
    {
        JsonNode? node = GetNode(root, path);
        if (node is not JsonArray array)
        {
            return;
        }

        target.Clear();
        foreach (JsonNode? item in array)
        {
            if (item is not null && TryReadDouble(item, out double value))
            {
                target.Add(value);
            }
        }

        target.Sort();
    }

    private static double GetDouble(JsonObject root, double fallback, params string[] path)
    {
        JsonNode? node = GetNode(root, path);
        return node is not null && TryReadDouble(node, out double value) ? value : fallback;
    }

    private static int GetInt(JsonObject root, int fallback, params string[] path)
    {
        JsonNode? node = GetNode(root, path);
        return node is not null && TryReadInt(node, out int value) ? value : fallback;
    }

    private static string GetString(JsonObject root, string fallback, params string[] path)
    {
        JsonNode? node = GetNode(root, path);
        return node?.ToString() ?? fallback;
    }
}
