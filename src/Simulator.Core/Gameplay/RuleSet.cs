namespace Simulator.Core.Gameplay;

public sealed class RuleSet
{
    public double GameDurationSec { get; set; } = 420.0;

    public CombatRuleSet Combat { get; } = new();

    public HeatRuleSet Heat { get; } = new();

    public RespawnRuleSet Respawn { get; } = new();

    public FacilityRuleSet Facility { get; } = new();

    public Dictionary<string, RoleProfile> RoleProfiles { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static RuleSet CreateDefault()
    {
        var ruleSet = new RuleSet();

        ruleSet.Combat.Damage17ToRobot = 20.0;
        ruleSet.Combat.Damage42ToRobot = 200.0;
        ruleSet.Combat.Damage17ToStructure = 20.0;
        ruleSet.Combat.Damage42ToStructure = 200.0;

        ruleSet.RoleProfiles["hero"] = BuildHeroProfile();
        ruleSet.RoleProfiles["engineer"] = BuildEngineerProfile();
        ruleSet.RoleProfiles["infantry"] = BuildInfantryProfile();
        ruleSet.RoleProfiles["sentry"] = BuildSentryProfile();

        return ruleSet;
    }

    public RoleProfile ResolveRoleProfile(string roleKey)
    {
        if (RoleProfiles.TryGetValue(roleKey, out RoleProfile? profile))
        {
            return profile;
        }

        if (RoleProfiles.TryGetValue("infantry", out profile))
        {
            return profile;
        }

        return new RoleProfile();
    }

    public ResolvedRoleProfile ResolveRuntimeProfile(string roleKey)
    {
        RoleProfile profile = ResolveRoleProfile(roleKey);
        return profile.Resolve(level: profile.InitialLevel, variantIds: Array.Empty<string>());
    }

    public ResolvedRoleProfile ResolveRuntimeProfile(SimulationEntity entity)
    {
        RoleProfile profile = ResolveRoleProfile(entity.RoleKey);
        int level = Math.Clamp(entity.Level <= 0 ? profile.InitialLevel : entity.Level, 1, 10);
        ResolvedRoleProfile resolved = profile.Resolve(level, ResolveVariantIds(entity));
        ApplyExactPerformanceTables(entity, level, resolved);
        return resolved;
    }

    private static IEnumerable<string> ResolveVariantIds(SimulationEntity entity)
    {
        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            yield return NormalizeHeroMode(entity.HeroPerformanceMode);
            yield break;
        }

        if (string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            yield return NormalizeInfantryDurabilityMode(entity.InfantryDurabilityMode);
            yield return NormalizeInfantryWeaponMode(entity.InfantryWeaponMode);
            yield break;
        }

        if (string.Equals(entity.RoleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            yield return NormalizeSentryControlMode(entity.SentryControlMode);
            yield return NormalizeSentryStance(entity.SentryStance);
        }
    }

    public static string NormalizeHeroMode(string? mode)
    {
        string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "melee" or "close" or "melee_priority" => "melee_priority",
            _ => "ranged_priority",
        };
    }

    public static string NormalizeInfantryDurabilityMode(string? mode)
    {
        string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "power" or "power_priority" => "power_priority",
            _ => "hp_priority",
        };
    }

    public static string NormalizeInfantryWeaponMode(string? mode)
    {
        string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "burst" or "burst_priority" => "burst_priority",
            _ => "cooling_priority",
        };
    }

    public static string NormalizeSentryControlMode(string? mode)
    {
        string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "semi" or "semi_auto" => "semi_auto",
            _ => "full_auto",
        };
    }

    public static string NormalizeSentryStance(string? stance)
    {
        string normalized = (stance ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "defense" or "defence" => "defense",
            "move" or "moving" => "move",
            _ => "attack",
        };
    }

    private static RoleProfile BuildHeroProfile()
    {
        var profile = new RoleProfile
        {
            MaxHealth = 150.0,
            MaxPower = 50.0,
            MaxHeat = 100.0,
            PowerRecoveryRate = 1.0,
            HeatDissipationRate = 5.0,
            AmmoType = "42mm",
            InitialAllowedAmmo17Mm = 0,
            InitialAllowedAmmo42Mm = 0,
            UsesLeveling = true,
            InitialLevel = 1,
            BaseMaxLevel = 10,
            UsesChassisEnergy = true,
            InitialChassisEnergy = 20000.0,
            MaxChassisEnergy = 40000.0,
            EcoPowerLimitW = 35.0,
            BoostThresholdEnergy = 25000.0,
            BoostPowerMultiplier = 1.25,
            BoostPowerCapW = 200.0,
        };
        profile.Variants["ranged_priority"] = new RoleVariantProfile
        {
            MaxHealth = 150.0,
            MaxPower = 50.0,
            Level1MaxHealth = 150.0,
            Level10MaxHealth = 300.0,
            Level1MaxPower = 50.0,
            Level10MaxPower = 100.0,
            Level1MaxHeat = 100.0,
            Level10MaxHeat = 130.0,
            Level1HeatDissipationRate = 20.0,
            Level10HeatDissipationRate = 50.0,
        };
        profile.Variants["melee_priority"] = new RoleVariantProfile
        {
            MaxHealth = 200.0,
            MaxPower = 70.0,
            Level1MaxHealth = 200.0,
            Level10MaxHealth = 450.0,
            Level1MaxPower = 70.0,
            Level10MaxPower = 120.0,
            Level1MaxHeat = 140.0,
            Level10MaxHeat = 240.0,
            Level1HeatDissipationRate = 12.0,
            Level10HeatDissipationRate = 30.0,
        };
        return profile;
    }

    private static RoleProfile BuildEngineerProfile()
    {
        return new RoleProfile
        {
            MaxHealth = 250.0,
            MaxPower = 120.0,
            MaxHeat = 0.0,
            PowerRecoveryRate = 1.2,
            HeatDissipationRate = 0.0,
            AmmoType = "none",
            InitialAllowedAmmo17Mm = 0,
            InitialAllowedAmmo42Mm = 0,
            UsesLeveling = false,
            InitialLevel = 1,
            BaseMaxLevel = 1,
        };
    }

    private static RoleProfile BuildInfantryProfile()
    {
        var profile = new RoleProfile
        {
            MaxHealth = 200.0,
            MaxPower = 50.0,
            MaxHeat = 40.0,
            PowerRecoveryRate = 1.5,
            HeatDissipationRate = 12.0,
            AmmoType = "17mm",
            InitialAllowedAmmo17Mm = 0,
            InitialAllowedAmmo42Mm = 0,
            UsesLeveling = true,
            InitialLevel = 1,
            BaseMaxLevel = 10,
            UsesChassisEnergy = true,
            InitialChassisEnergy = 20000.0,
            MaxChassisEnergy = 40000.0,
            EcoPowerLimitW = 35.0,
            BoostThresholdEnergy = 25000.0,
            BoostPowerMultiplier = 1.25,
            BoostPowerCapW = 200.0,
        };
        profile.Variants["hp_priority"] = new RoleVariantProfile
        {
            MaxHealth = 200.0,
            MaxPower = 45.0,
            Level1MaxHealth = 200.0,
            Level10MaxHealth = 400.0,
            Level1MaxPower = 45.0,
            Level10MaxPower = 100.0,
        };
        profile.Variants["power_priority"] = new RoleVariantProfile
        {
            MaxHealth = 150.0,
            MaxPower = 60.0,
            Level1MaxHealth = 150.0,
            Level10MaxHealth = 400.0,
            Level1MaxPower = 60.0,
            Level10MaxPower = 100.0,
        };
        profile.Variants["cooling_priority"] = new RoleVariantProfile
        {
            MaxHeat = 40.0,
            HeatDissipationRate = 12.0,
            Level1MaxHeat = 40.0,
            Level10MaxHeat = 120.0,
            Level1HeatDissipationRate = 12.0,
            Level10HeatDissipationRate = 30.0,
        };
        profile.Variants["burst_priority"] = new RoleVariantProfile
        {
            MaxHeat = 170.0,
            HeatDissipationRate = 5.0,
            Level1MaxHeat = 170.0,
            Level10MaxHeat = 260.0,
            Level1HeatDissipationRate = 5.0,
            Level10HeatDissipationRate = 20.0,
        };
        return profile;
    }

    private static void ApplyExactPerformanceTables(SimulationEntity entity, int level, ResolvedRoleProfile resolved)
    {
        int index = Math.Clamp(level, 1, 10) - 1;

        if (string.Equals(entity.RoleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(NormalizeHeroMode(entity.HeroPerformanceMode), "melee_priority", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTableRow(
                    resolved,
                    index,
                    new[] { 200d, 225d, 250d, 275d, 300d, 325d, 350d, 375d, 400d, 450d },
                    new[] { 70d, 75d, 80d, 85d, 90d, 95d, 100d, 105d, 110d, 120d },
                    new[] { 140d, 150d, 160d, 170d, 180d, 190d, 200d, 210d, 220d, 240d },
                    new[] { 12d, 14d, 16d, 18d, 20d, 22d, 24d, 26d, 28d, 30d });
                return;
            }

            ApplyTableRow(
                resolved,
                index,
                new[] { 150d, 165d, 180d, 195d, 210d, 225d, 240d, 255d, 270d, 300d },
                new[] { 50d, 55d, 60d, 65d, 70d, 75d, 80d, 85d, 90d, 100d },
                new[] { 100d, 102d, 104d, 106d, 108d, 110d, 115d, 120d, 125d, 130d },
                new[] { 20d, 23d, 26d, 29d, 32d, 35d, 38d, 41d, 44d, 50d });
            return;
        }

        if (string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(NormalizeInfantryDurabilityMode(entity.InfantryDurabilityMode), "power_priority", StringComparison.OrdinalIgnoreCase))
            {
                resolved.MaxHealth = new[] { 150d, 175d, 200d, 225d, 250d, 275d, 300d, 325d, 350d, 400d }[index];
                resolved.MaxPower = new[] { 60d, 65d, 70d, 75d, 80d, 85d, 90d, 95d, 100d, 100d }[index];
            }
            else
            {
                resolved.MaxHealth = new[] { 200d, 225d, 250d, 275d, 300d, 325d, 350d, 375d, 400d, 400d }[index];
                resolved.MaxPower = new[] { 45d, 50d, 55d, 60d, 65d, 70d, 75d, 80d, 90d, 100d }[index];
            }

            if (string.Equals(NormalizeInfantryWeaponMode(entity.InfantryWeaponMode), "burst_priority", StringComparison.OrdinalIgnoreCase))
            {
                resolved.MaxHeat = new[] { 170d, 180d, 190d, 200d, 210d, 220d, 230d, 240d, 250d, 260d }[index];
                resolved.HeatDissipationRate = new[] { 5d, 7d, 9d, 11d, 12d, 13d, 14d, 16d, 18d, 20d }[index];
            }
            else
            {
                resolved.MaxHeat = new[] { 40d, 48d, 56d, 64d, 72d, 80d, 88d, 96d, 114d, 120d }[index];
                resolved.HeatDissipationRate = new[] { 12d, 14d, 16d, 18d, 20d, 22d, 24d, 26d, 28d, 30d }[index];
            }
        }
    }

    private static void ApplyTableRow(
        ResolvedRoleProfile resolved,
        int index,
        IReadOnlyList<double> health,
        IReadOnlyList<double> power,
        IReadOnlyList<double> heat,
        IReadOnlyList<double> cooling)
    {
        resolved.MaxHealth = health[index];
        resolved.MaxPower = power[index];
        resolved.MaxHeat = heat[index];
        resolved.HeatDissipationRate = cooling[index];
    }

    private static RoleProfile BuildSentryProfile()
    {
        var profile = new RoleProfile
        {
            MaxHealth = 400.0,
            MaxPower = 100.0,
            MaxHeat = 260.0,
            PowerRecoveryRate = 2.0,
            HeatDissipationRate = 30.0,
            AmmoType = "17mm",
            InitialAllowedAmmo17Mm = 300,
            InitialAllowedAmmo42Mm = 0,
            UsesLeveling = false,
            InitialLevel = 1,
            BaseMaxLevel = 1,
            UsesChassisEnergy = true,
            InitialChassisEnergy = 20000.0,
            MaxChassisEnergy = 40000.0,
            EcoPowerLimitW = 35.0,
            BoostThresholdEnergy = 25000.0,
            BoostPowerMultiplier = 1.25,
            BoostPowerCapW = 200.0,
        };
        profile.Variants["full_auto"] = new RoleVariantProfile
        {
            MaxHealth = 400.0,
            MaxPower = 100.0,
            MaxHeat = 260.0,
            HeatDissipationRate = 30.0,
        };
        profile.Variants["semi_auto"] = new RoleVariantProfile
        {
            MaxHealth = 200.0,
            MaxPower = 60.0,
            MaxHeat = 100.0,
            HeatDissipationRate = 10.0,
        };
        profile.Variants["attack"] = new RoleVariantProfile
        {
            DamageTakenMultiplier = 1.25,
            CoolingMultiplier = 3.0,
            PowerLimitMultiplier = 0.50,
        };
        profile.Variants["move"] = new RoleVariantProfile
        {
            DamageTakenMultiplier = 1.25,
            CoolingMultiplier = 1.0 / 3.0,
            PowerLimitMultiplier = 1.50,
        };
        profile.Variants["defense"] = new RoleVariantProfile
        {
            DamageTakenMultiplier = 0.50,
            CoolingMultiplier = 1.0 / 3.0,
            PowerLimitMultiplier = 0.50,
        };
        return profile;
    }
}

public sealed class RoleProfile
{
    public double MaxHealth { get; set; } = 100.0;

    public double MaxPower { get; set; } = 60.0;

    public double MaxHeat { get; set; } = 60.0;

    public double PowerRecoveryRate { get; set; } = 1.0;

    public double HeatDissipationRate { get; set; } = 5.0;

    public string AmmoType { get; set; } = "17mm";

    public int InitialAllowedAmmo17Mm { get; set; } = 100;

    public int InitialAllowedAmmo42Mm { get; set; }

    public bool UsesLeveling { get; set; }

    public int InitialLevel { get; set; } = 1;

    public int BaseMaxLevel { get; set; } = 5;

    public bool UsesChassisEnergy { get; set; }

    public double InitialChassisEnergy { get; set; }

    public double MaxChassisEnergy { get; set; }

    public double EcoPowerLimitW { get; set; } = 35.0;

    public double BoostThresholdEnergy { get; set; } = 25000.0;

    public double BoostPowerMultiplier { get; set; } = 1.25;

    public double BoostPowerCapW { get; set; } = 200.0;

    public double Level1MaxHealth { get; set; }

    public double Level10MaxHealth { get; set; }

    public double Level1MaxPower { get; set; }

    public double Level10MaxPower { get; set; }

    public double Level1MaxHeat { get; set; }

    public double Level10MaxHeat { get; set; }

    public double Level1HeatDissipationRate { get; set; }

    public double Level10HeatDissipationRate { get; set; }

    public Dictionary<string, RoleVariantProfile> Variants { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public ResolvedRoleProfile Resolve(int level, IEnumerable<string> variantIds)
    {
        var resolved = new ResolvedRoleProfile
        {
            MaxHealth = MaxHealth,
            MaxPower = MaxPower,
            MaxHeat = MaxHeat,
            PowerRecoveryRate = PowerRecoveryRate,
            HeatDissipationRate = HeatDissipationRate,
            AmmoType = AmmoType,
            InitialAllowedAmmo17Mm = InitialAllowedAmmo17Mm,
            InitialAllowedAmmo42Mm = InitialAllowedAmmo42Mm,
            UsesLeveling = UsesLeveling,
            InitialLevel = InitialLevel,
            MaxLevel = BaseMaxLevel,
            UsesChassisEnergy = UsesChassisEnergy,
            InitialChassisEnergy = InitialChassisEnergy,
            MaxChassisEnergy = MaxChassisEnergy,
            EcoPowerLimitW = EcoPowerLimitW,
            BoostThresholdEnergy = BoostThresholdEnergy,
            BoostPowerMultiplier = BoostPowerMultiplier,
            BoostPowerCapW = BoostPowerCapW,
            Level1MaxHealth = Level1MaxHealth > 0 ? Level1MaxHealth : MaxHealth,
            Level10MaxHealth = Level10MaxHealth > 0 ? Level10MaxHealth : MaxHealth,
            Level1MaxPower = Level1MaxPower > 0 ? Level1MaxPower : MaxPower,
            Level10MaxPower = Level10MaxPower > 0 ? Level10MaxPower : MaxPower,
            Level1MaxHeat = Level1MaxHeat > 0 ? Level1MaxHeat : MaxHeat,
            Level10MaxHeat = Level10MaxHeat > 0 ? Level10MaxHeat : MaxHeat,
            Level1HeatDissipationRate = Level1HeatDissipationRate > 0 ? Level1HeatDissipationRate : HeatDissipationRate,
            Level10HeatDissipationRate = Level10HeatDissipationRate > 0 ? Level10HeatDissipationRate : HeatDissipationRate,
        };

        foreach (string variantId in variantIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (Variants.TryGetValue(variantId, out RoleVariantProfile? variant))
            {
                variant.ApplyTo(resolved);
            }
        }

        int clampedLevel = Math.Clamp(level, 1, 10);
        if (resolved.UsesLeveling)
        {
            resolved.MaxHealth = LerpByLevel(resolved.Level1MaxHealth, resolved.Level10MaxHealth, clampedLevel);
            resolved.MaxPower = LerpByLevel(resolved.Level1MaxPower, resolved.Level10MaxPower, clampedLevel);
            resolved.MaxHeat = LerpByLevel(resolved.Level1MaxHeat, resolved.Level10MaxHeat, clampedLevel);
            resolved.HeatDissipationRate = LerpByLevel(resolved.Level1HeatDissipationRate, resolved.Level10HeatDissipationRate, clampedLevel);
        }

        resolved.MaxPower *= resolved.PowerLimitMultiplier;
        resolved.HeatDissipationRate *= resolved.CoolingMultiplier;
        return resolved;
    }

    private static double LerpByLevel(double level1, double level10, int level)
    {
        if (level <= 1)
        {
            return level1;
        }

        if (level >= 10)
        {
            return level10;
        }

        double ratio = (level - 1) / 9.0;
        return level1 + (level10 - level1) * ratio;
    }
}

public sealed class RoleVariantProfile
{
    public double? MaxHealth { get; set; }

    public double? MaxPower { get; set; }

    public double? MaxHeat { get; set; }

    public double? PowerRecoveryRate { get; set; }

    public double? HeatDissipationRate { get; set; }

    public double? Level1MaxHealth { get; set; }

    public double? Level10MaxHealth { get; set; }

    public double? Level1MaxPower { get; set; }

    public double? Level10MaxPower { get; set; }

    public double? Level1MaxHeat { get; set; }

    public double? Level10MaxHeat { get; set; }

    public double? Level1HeatDissipationRate { get; set; }

    public double? Level10HeatDissipationRate { get; set; }

    public double DamageDealtMultiplier { get; set; } = 1.0;

    public double DamageTakenMultiplier { get; set; } = 1.0;

    public double CoolingMultiplier { get; set; } = 1.0;

    public double PowerLimitMultiplier { get; set; } = 1.0;

    public void ApplyTo(ResolvedRoleProfile profile)
    {
        profile.MaxHealth = MaxHealth ?? profile.MaxHealth;
        profile.MaxPower = MaxPower ?? profile.MaxPower;
        profile.MaxHeat = MaxHeat ?? profile.MaxHeat;
        profile.PowerRecoveryRate = PowerRecoveryRate ?? profile.PowerRecoveryRate;
        profile.HeatDissipationRate = HeatDissipationRate ?? profile.HeatDissipationRate;
        profile.Level1MaxHealth = Level1MaxHealth ?? profile.Level1MaxHealth;
        profile.Level10MaxHealth = Level10MaxHealth ?? profile.Level10MaxHealth;
        profile.Level1MaxPower = Level1MaxPower ?? profile.Level1MaxPower;
        profile.Level10MaxPower = Level10MaxPower ?? profile.Level10MaxPower;
        profile.Level1MaxHeat = Level1MaxHeat ?? profile.Level1MaxHeat;
        profile.Level10MaxHeat = Level10MaxHeat ?? profile.Level10MaxHeat;
        profile.Level1HeatDissipationRate = Level1HeatDissipationRate ?? profile.Level1HeatDissipationRate;
        profile.Level10HeatDissipationRate = Level10HeatDissipationRate ?? profile.Level10HeatDissipationRate;
        profile.DamageDealtMultiplier *= DamageDealtMultiplier;
        profile.DamageTakenMultiplier *= DamageTakenMultiplier;
        profile.CoolingMultiplier *= CoolingMultiplier;
        profile.PowerLimitMultiplier *= PowerLimitMultiplier;
    }
}

public sealed class ResolvedRoleProfile
{
    public double MaxHealth { get; set; } = 100.0;

    public double MaxPower { get; set; } = 60.0;

    public double MaxHeat { get; set; } = 60.0;

    public double PowerRecoveryRate { get; set; } = 1.0;

    public double HeatDissipationRate { get; set; } = 5.0;

    public string AmmoType { get; set; } = "17mm";

    public int InitialAllowedAmmo17Mm { get; set; } = 100;

    public int InitialAllowedAmmo42Mm { get; set; }

    public bool UsesLeveling { get; set; }

    public int InitialLevel { get; set; } = 1;

    public int MaxLevel { get; set; } = 5;

    public bool UsesChassisEnergy { get; set; }

    public double InitialChassisEnergy { get; set; }

    public double MaxChassisEnergy { get; set; }

    public double EcoPowerLimitW { get; set; } = 35.0;

    public double BoostThresholdEnergy { get; set; } = 25000.0;

    public double BoostPowerMultiplier { get; set; } = 1.25;

    public double BoostPowerCapW { get; set; } = 200.0;

    public double Level1MaxHealth { get; set; }

    public double Level10MaxHealth { get; set; }

    public double Level1MaxPower { get; set; }

    public double Level10MaxPower { get; set; }

    public double Level1MaxHeat { get; set; }

    public double Level10MaxHeat { get; set; }

    public double Level1HeatDissipationRate { get; set; }

    public double Level10HeatDissipationRate { get; set; }

    public double DamageDealtMultiplier { get; set; } = 1.0;

    public double DamageTakenMultiplier { get; set; } = 1.0;

    public double CoolingMultiplier { get; set; } = 1.0;

    public double PowerLimitMultiplier { get; set; } = 1.0;
}

public sealed class CombatRuleSet
{
    public double BaseHitProbability { get; set; } = 0.88;

    public double MinHitProbability { get; set; } = 0.10;

    public double RangeFalloff { get; set; } = 0.65;

    public double AutoAimMaxDistanceM { get; set; } = 8.0;

    public double FastSpinHitMultiplier { get; set; } = 0.60;

    public double FireRateHz { get; set; } = 8.0;

    public double Damage17ToRobot { get; set; } = 20.0;

    public double Damage42ToRobot { get; set; } = 200.0;

    public double Damage17ToStructure { get; set; } = 20.0;

    public double Damage42ToStructure { get; set; } = 200.0;

    public double Damage17ToBaseFrontUpperArmor { get; set; } = 5.0;

    public double Damage17ToBaseOtherArmor { get; set; } = 20.0;

    public double Damage17ToOutpostArmor { get; set; } = 20.0;

    public double Damage42ToOutpostArmor { get; set; } = 200.0;

    public double CollisionDamageToRobot { get; set; } = 2.0;
}

public sealed class HeatRuleSet
{
    public double HeatDetectionHz { get; set; } = 10.0;

    public double HeatGain17 { get; set; } = 10.0;

    public double HeatGain42 { get; set; } = 100.0;

    public double OverheatLockDurationSec { get; set; } = 5.0;
}

public sealed class RespawnRuleSet
{
    public double RobotDelaySec { get; set; } = 10.0;

    public double InvalidDurationSec { get; set; } = 30.0;

    public double InvincibleDurationSec { get; set; } = 3.0;

    public double WeakenDamageDealtMult { get; set; } = 0.75;

    public double WeakenDamageTakenMult { get; set; } = 1.25;
}

public sealed class FacilityRuleSet
{
    public double SupplyIntervalSec { get; set; } = 60.0;

    public int SupplyAmmoGain17Mm { get; set; } = 100;

    public int SupplyAmmoGain42Mm { get; set; } = 10;

    public double MiningDurationMinSec { get; set; } = 10.0;

    public double MiningDurationMaxSec { get; set; } = 15.0;

    public double ExchangeDurationMinSec { get; set; } = 10.0;

    public double ExchangeDurationMaxSec { get; set; } = 15.0;

    public int MineralsPerTrip { get; set; } = 1;

    public double GoldPerMineral { get; set; } = 120.0;

    public double FortDamageTakenMult { get; set; } = 0.80;

    public double FortCoolingMult { get; set; } = 1.25;

    public double DeadZoneEliminationSec { get; set; } = 5.0;

    public double EnergyActivationHoldSec { get; set; } = 10.0;

    public double EnergyActivationWindowSec { get; set; } = 20.0;

    public int EnergySmallHitThreshold { get; set; } = 1;

    public int EnergyLargeHitThreshold { get; set; } = 5;

    public double EnergyLargeWindowStartSec { get; set; } = 180.0;

    public double EnergyBuffDurationSec { get; set; } = 45.0;

    public double EnergySmallBuffDurationSec { get; set; } = 45.0;

    public double EnergySmallDefenseMult { get; set; } = 0.75;

    public double EnergyDamageDealtMult { get; set; } = 1.15;

    public double EnergyCoolingMult { get; set; } = 1.20;

    public double EnergyPowerRecoveryMult { get; set; } = 1.15;

    public HashSet<string> EnergyAllowedRoles { get; } =
        new(StringComparer.OrdinalIgnoreCase) { "hero", "infantry", "sentry" };

    public List<double> EnergySmallOpportunityTimesSec { get; } = new() { 0.0, 90.0 };

    public List<double> EnergyLargeOpportunityTimesSec { get; } = new() { 180.0, 255.0, 330.0 };

    public Dictionary<string, double> EnergyVirtualHitsPerSec { get; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["hero"] = 0.45,
            ["infantry"] = 0.36,
            ["sentry"] = 0.32,
        };
}
