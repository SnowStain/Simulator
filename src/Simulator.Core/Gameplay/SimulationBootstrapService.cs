using System.Text.Json.Nodes;
using System.Text.Json;
using Simulator.Core.Map;

namespace Simulator.Core.Gameplay;

public sealed class SimulationBootstrapService
{
    public SimulationWorldState BuildInitialWorld(
        JsonObject config,
        RuleSet ruleSet,
        MapPresetDefinition mapPreset)
    {
        var world = new SimulationWorldState
        {
            MetersPerWorldUnit = ResolveMetersPerWorldUnit(config, mapPreset),
            WorldWidth = Math.Max(1.0, mapPreset.Width),
            WorldHeight = Math.Max(1.0, mapPreset.Height),
        };
        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);

        double initialGold = ResolveInitialGold(config);
        SimulationTeamState redTeam = world.GetOrCreateTeamState("red", initialGold);
        SimulationTeamState blueTeam = world.GetOrCreateTeamState("blue", initialGold);
        int redDirection = Random.Shared.Next(0, 2) == 0 ? -1 : 1;
        redTeam.EnergyRotorDirectionSign = redDirection;
        blueTeam.EnergyRotorDirectionSign = -redDirection;

        JsonObject? entities = config["entities"] as JsonObject;
        JsonObject? initialPositions = entities?["initial_positions"] as JsonObject;
        JsonObject? robotTypes = entities?["robot_types"] as JsonObject;

        foreach (string team in new[] { "red", "blue" })
        {
            if (initialPositions?[team] is not JsonObject teamPositions)
            {
                continue;
            }

            foreach ((string key, JsonNode? valueNode) in teamPositions)
            {
                if (valueNode is not JsonObject position)
                {
                    continue;
                }

                string roleKey = ResolveRoleKey(key, robotTypes);
                string entityType = string.Equals(roleKey, "sentry", StringComparison.OrdinalIgnoreCase)
                    ? "sentry"
                    : "robot";
                (double directStepHeightM, double maxStepHeightM, double collisionRadiusM) = ResolveMobilityProfile(roleKey, entityType);
                double spawnAngle = ReadDouble(position, team == "blue" ? 180.0 : 0.0, "angle");

                var entity = new SimulationEntity
                {
                    Id = $"{team}_{key}",
                    Team = team,
                    EntityType = entityType,
                    RoleKey = roleKey,
                    Level = 1,
                    X = ReadDouble(position, 0.0, "x"),
                    Y = ReadDouble(position, 0.0, "y"),
                    AngleDeg = spawnAngle,
                    ChassisTargetYawDeg = spawnAngle,
                    TurretYawDeg = spawnAngle,
                    DirectStepHeightM = directStepHeightM,
                    MaxStepClimbHeightM = maxStepHeightM,
                    CollisionRadiusWorld = collisionRadiusM / metersPerWorldUnit,
                };

                ApplyDefaultRoleSelections(entity);
                ResolvedRoleProfile profile = ruleSet.ResolveRuntimeProfile(entity);
                entity.Level = profile.InitialLevel;
                entity.MaxLevel = profile.MaxLevel;
                entity.MaxHealth = profile.MaxHealth;
                entity.Health = profile.MaxHealth;
                entity.MaxPower = profile.MaxPower;
                entity.Power = profile.MaxPower;
                entity.MaxHeat = profile.MaxHeat;
                entity.Heat = 0;
                entity.AmmoType = profile.AmmoType;
                ApplyInitialPurchasedAmmo(entity, profile, roleKey);
                entity.RuleDrivePowerLimitW = profile.MaxPower;
                entity.MaxChassisEnergy = profile.MaxChassisEnergy;
                entity.ChassisEnergy = profile.UsesChassisEnergy ? profile.InitialChassisEnergy : 0.0;
                entity.ChassisEcoPowerLimitW = profile.EcoPowerLimitW;
                entity.ChassisBoostThresholdEnergy = profile.BoostThresholdEnergy;
                entity.ChassisBoostMultiplier = profile.BoostPowerMultiplier;
                entity.ChassisBoostPowerCapW = profile.BoostPowerCapW;
                entity.MaxBufferEnergyJ = 60.0;
                entity.BufferReserveEnergyJ = 10.0;
                entity.BufferEnergyJ = entity.MaxBufferEnergyJ;
                world.Entities.Add(entity);
            }
        }

        AddStructureEntities(world, mapPreset);
        return world;
    }

    private static void AddStructureEntities(SimulationWorldState world, MapPresetDefinition mapPreset)
    {
        int energyIndex = 0;
        foreach (FacilityRegion region in mapPreset.Facilities)
        {
            string type = (region.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (type is not ("base" or "outpost" or "energy_mechanism"))
            {
                continue;
            }

            if ((type is "base" or "outpost")
                && string.Equals(region.Team, "neutral", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            (double cx, double cy) = ResolveRegionCenter(region);
            string entityId = ResolveStructureEntityId(region, ref energyIndex);
            bool exists = world.Entities.Any(entity =>
                string.Equals(entity.EntityType, type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entity.Team, region.Team, StringComparison.OrdinalIgnoreCase)
                && type != "energy_mechanism");
            if (string.Equals(type, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
            {
                exists = world.Entities.Any(entity =>
                    string.Equals(entity.EntityType, type, StringComparison.OrdinalIgnoreCase));
            }

            if (exists)
            {
                continue;
            }

            double structureCollisionRadiusM = ResolveStructureCollisionRadiusM(region, world.MetersPerWorldUnit, type);
            double bodyHeightM = ResolveFacilityOverride(region, "body_height_m", type == "outpost" ? 1.578 : type == "base" ? 1.181 : 2.30, 0.05);
            double bodyWidthM = ResolveFacilityOverride(region, "body_width_m", type == "outpost" ? 0.65 : type == "base" ? 1.609 : 1.30, 0.05);
            double bodyLengthM = ResolveFacilityOverride(region, "body_length_m", type == "outpost" ? 0.65 : type == "base" ? 1.881 : 2.06, 0.05);
            double maxHealth = type == "base" ? 5000.0 : type == "outpost" ? 1500.0 : 0.0;
            world.Entities.Add(new SimulationEntity
            {
                Id = entityId,
                Team = region.Team,
                EntityType = type,
                RoleKey = type,
                X = cx,
                Y = cy,
                AngleDeg = ResolveStructureYawDeg(region.Team, type),
                TurretYawDeg = ResolveStructureYawDeg(region.Team, type),
                MaxHealth = maxHealth,
                Health = maxHealth,
                MaxPower = 0,
                Power = 0,
                MaxHeat = 0,
                Heat = 0,
                AmmoType = "none",
                Ammo17Mm = 0,
                Ammo42Mm = 0,
                BodyHeightM = bodyHeightM,
                BodyWidthM = bodyWidthM,
                BodyLengthM = bodyLengthM,
                BodyRenderWidthScale = 1.0,
                CollisionRadiusWorld = structureCollisionRadiusM / Math.Max(world.MetersPerWorldUnit, 1e-6),
            });
        }
    }

    private static void ApplyInitialPurchasedAmmo(SimulationEntity entity, ResolvedRoleProfile profile, string roleKey)
    {
        entity.Ammo17Mm = 0;
        entity.Ammo42Mm = 0;
        if (IsSentryRole(roleKey))
        {
            entity.Ammo17Mm = profile.InitialAllowedAmmo17Mm;
            entity.Ammo42Mm = profile.InitialAllowedAmmo42Mm;
            return;
        }

        if (string.Equals(roleKey, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            entity.Ammo17Mm = 100;
            return;
        }

        if (string.Equals(roleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            entity.Ammo42Mm = 10;
        }
    }

    private static double ResolveFacilityOverride(
        FacilityRegion region,
        string key,
        double fallback,
        double minValue)
    {
        if (region.AdditionalProperties is null
            || !region.AdditionalProperties.TryGetValue(key, out JsonElement element))
        {
            return fallback;
        }

        double value;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out value):
                return Math.Max(minValue, value);
            case JsonValueKind.String when double.TryParse(element.GetString(), out value):
                return Math.Max(minValue, value);
            default:
                return fallback;
        }
    }

    private static string ResolveStructureEntityId(FacilityRegion region, ref int energyIndex)
    {
        string type = (region.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type == "energy_mechanism")
        {
            energyIndex++;
            if (!string.IsNullOrWhiteSpace(region.Id))
            {
                return region.Id;
            }

            string team = string.IsNullOrWhiteSpace(region.Team) ? "neutral" : region.Team;
            return $"{team}_{type}_{energyIndex}";
        }

        return $"{region.Team}_{type}";
    }

    private static double ResolveStructureYawDeg(string team, string type)
    {
        if (!string.Equals(type, "outpost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "base", StringComparison.OrdinalIgnoreCase))
        {
            return 0.0;
        }

        return string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase) ? 180.0 : 0.0;
    }

    private static double ResolveStructureCollisionRadiusM(FacilityRegion region, double metersPerWorldUnit, string type)
    {
        (double minX, double maxX, double minY, double maxY) = ResolveRegionBounds(region);
        double spanXM = Math.Max(0.04, (maxX - minX) * Math.Max(metersPerWorldUnit, 1e-6));
        double spanYM = Math.Max(0.04, (maxY - minY) * Math.Max(metersPerWorldUnit, 1e-6));
        double radiusM = Math.Max(spanXM, spanYM) * 0.5 + 0.03;
        if (string.Equals(type, "base", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(radiusM, 0.30, 0.72);
        }

        if (string.Equals(type, "energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(radiusM, 0.90, 2.20);
        }

        return Math.Clamp(radiusM, 0.18, 0.42);
    }

    private static bool IsSentryRole(string roleKey)
        => string.Equals(roleKey, "sentry", StringComparison.OrdinalIgnoreCase);

    private static (double X, double Y) ResolveRegionCenter(FacilityRegion region)
    {
        if (region.Points.Count > 0)
        {
            double sumX = 0;
            double sumY = 0;
            foreach (Point2D point in region.Points)
            {
                sumX += point.X;
                sumY += point.Y;
            }

            return (sumX / region.Points.Count, sumY / region.Points.Count);
        }

        return ((region.X1 + region.X2) * 0.5, (region.Y1 + region.Y2) * 0.5);
    }

    private static (double MinX, double MaxX, double MinY, double MaxY) ResolveRegionBounds(FacilityRegion region)
    {
        if (region.Points.Count > 0)
        {
            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;
            foreach (Point2D point in region.Points)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }

            if (!double.IsInfinity(minX) && !double.IsInfinity(minY))
            {
                return (minX, maxX, minY, maxY);
            }
        }

        return (Math.Min(region.X1, region.X2), Math.Max(region.X1, region.X2), Math.Min(region.Y1, region.Y2), Math.Max(region.Y1, region.Y2));
    }

    private static string ResolveRoleKey(string robotKey, JsonObject? robotTypes)
    {
        string? typeText = robotTypes?[robotKey]?.ToString();
        if (!string.IsNullOrWhiteSpace(typeText))
        {
            string normalized = typeText.Trim();
            return normalized switch
            {
                "英雄" => "hero",
                "工程" => "engineer",
                "步兵" => "infantry",
                "哨兵" => "sentry",
                _ => normalized.ToLowerInvariant(),
            };
        }

        if (robotKey.EndsWith("_1", StringComparison.OrdinalIgnoreCase))
        {
            return "hero";
        }

        if (robotKey.EndsWith("_2", StringComparison.OrdinalIgnoreCase))
        {
            return "engineer";
        }

        if (robotKey.EndsWith("_7", StringComparison.OrdinalIgnoreCase))
        {
            return "sentry";
        }

        return "infantry";
    }

    private static double ResolveMetersPerWorldUnit(JsonObject config, MapPresetDefinition mapPreset)
    {
        JsonObject? map = config["map"] as JsonObject;
        double fieldLengthM = ReadDouble(map, mapPreset.FieldLengthM, "field_length_m");
        double fieldWidthM = ReadDouble(map, mapPreset.FieldWidthM, "field_width_m");
        int width = ReadInt(map, mapPreset.Width, "width");
        int height = ReadInt(map, mapPreset.Height, "height");
        if (width <= 0)
        {
            width = mapPreset.Width;
        }

        if (height <= 0)
        {
            height = mapPreset.Height;
        }

        double scaleFromLength = fieldLengthM > 0 && width > 0 ? fieldLengthM / width : 0.0;
        double scaleFromWidth = fieldWidthM > 0 && height > 0 ? fieldWidthM / height : 0.0;
        if (scaleFromLength > 0 && scaleFromWidth > 0)
        {
            return (scaleFromLength + scaleFromWidth) * 0.5;
        }

        if (scaleFromLength > 0)
        {
            return scaleFromLength;
        }

        return scaleFromWidth > 0 ? scaleFromWidth : 0.0178;
    }

    private static void ApplyDefaultRoleSelections(SimulationEntity entity)
    {
        entity.HeroPerformanceMode = RuleSet.NormalizeHeroMode(entity.HeroPerformanceMode);
        entity.InfantryDurabilityMode = RuleSet.NormalizeInfantryDurabilityMode(entity.InfantryDurabilityMode);
        entity.InfantryWeaponMode = RuleSet.NormalizeInfantryWeaponMode(entity.InfantryWeaponMode);
        entity.SentryControlMode = RuleSet.NormalizeSentryControlMode(entity.SentryControlMode);
        entity.SentryStance = RuleSet.NormalizeSentryStance(entity.SentryStance);
    }

    private static double ResolveInitialGold(JsonObject config)
    {
        JsonObject? rules = config["rules"] as JsonObject;
        return ReadDouble(rules, 400.0, "economy", "initial_gold");
    }

    private static int ReadInt(JsonObject? node, int fallback, params string[] path)
    {
        JsonNode? target = Walk(node, path);
        if (target is JsonValue value)
        {
            if (value.TryGetValue(out int intValue))
            {
                return intValue;
            }

            if (value.TryGetValue(out double doubleValue))
            {
                return Convert.ToInt32(doubleValue);
            }

            if (value.TryGetValue(out string? text)
                && int.TryParse(text, out intValue))
            {
                return intValue;
            }
        }

        return fallback;
    }

    private static double ReadDouble(JsonObject? node, double fallback, params string[] path)
    {
        JsonNode? target = Walk(node, path);
        if (target is JsonValue value)
        {
            if (value.TryGetValue(out double doubleValue))
            {
                return doubleValue;
            }

            if (value.TryGetValue(out int intValue))
            {
                return intValue;
            }

            if (value.TryGetValue(out string? text)
                && double.TryParse(text, out doubleValue))
            {
                return doubleValue;
            }
        }

        return fallback;
    }

    private static JsonNode? Walk(JsonObject? node, params string[] path)
    {
        JsonNode? current = node;
        foreach (string segment in path)
        {
            if (current is not JsonObject obj)
            {
                return null;
            }

            current = obj[segment];
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static (double DirectStepHeightM, double MaxStepHeightM, double CollisionRadiusM) ResolveMobilityProfile(
        string roleKey,
        string entityType)
    {
        if (string.Equals(entityType, "sentry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(roleKey, "sentry", StringComparison.OrdinalIgnoreCase))
        {
            return (0.05, 0.24, 0.42);
        }

        if (string.Equals(roleKey, "hero", StringComparison.OrdinalIgnoreCase))
        {
            return (0.08, 0.30, 0.40);
        }

        if (string.Equals(roleKey, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            return (0.07, 0.26, 0.38);
        }

        return (0.08, 0.35, 0.35);
    }
}
