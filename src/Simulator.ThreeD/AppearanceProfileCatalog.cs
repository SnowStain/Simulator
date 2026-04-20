using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed class AppearanceProfileCatalog
{
    private readonly Dictionary<string, RobotAppearanceProfile> _profiles;

    private AppearanceProfileCatalog(Dictionary<string, RobotAppearanceProfile> profiles)
    {
        _profiles = profiles;
    }

    public static AppearanceProfileCatalog Load(string appearancePath)
    {
        var profiles = new Dictionary<string, RobotAppearanceProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["hero"] = RobotAppearanceProfile.CreateDefault(0.65f, 0.48f, 0.19f, 0.11f, "hero"),
            ["engineer"] = RobotAppearanceProfile.CreateDefault(0.55f, 0.50f, 0.17f, 0.11f, "engineer"),
            ["infantry"] = RobotAppearanceProfile.CreateDefault(0.48f, 0.48f, 0.18f, 0.10f, "infantry"),
            ["sentry"] = RobotAppearanceProfile.CreateDefault(0.55f, 0.50f, 0.17f, 0.10f, "sentry"),
            ["outpost"] = RobotAppearanceProfile.CreateDefault(0.65f, 0.55f, 1.878f, 0.0f, "outpost"),
            ["base"] = RobotAppearanceProfile.CreateDefault(1.881f, 1.609f, 1.181f, 0.0f, "base"),
        };

        if (!File.Exists(appearancePath))
        {
            return new AppearanceProfileCatalog(profiles);
        }

        try
        {
            JsonObject? rootObject = JsonNode.Parse(File.ReadAllText(appearancePath)) as JsonObject;
            JsonObject? profileContainer = rootObject?["profiles"] as JsonObject;
            if (profileContainer is null)
            {
                return new AppearanceProfileCatalog(profiles);
            }

            foreach (string roleKey in new[] { "hero", "engineer", "infantry", "sentry", "outpost", "base" })
            {
                if (profileContainer[roleKey] is not JsonObject roleNode)
                {
                    continue;
                }

                if (string.Equals(roleKey, "infantry", StringComparison.OrdinalIgnoreCase)
                    && roleNode["subtype_profiles"] is JsonObject subtypeProfiles)
                {
                    foreach ((string subtypeKey, JsonNode? subtypeNodeValue) in subtypeProfiles)
                    {
                        if (subtypeNodeValue is not JsonObject subtypeNode)
                        {
                            continue;
                        }

                        profiles[CompositeKey(roleKey, subtypeKey)] = ParseProfile(subtypeNode, roleNode, profiles[roleKey]);
                    }
                }

                JsonObject effective = ResolveEffectiveRoleNode(roleKey, roleNode);
                profiles[roleKey] = ParseProfile(effective, roleNode, profiles[roleKey]);
            }
        }
        catch
        {
            // Keep defaults when appearance parsing fails.
        }

        return new AppearanceProfileCatalog(profiles);
    }

    public RobotAppearanceProfile Resolve(string roleKey, string? subtypeOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(subtypeOverride)
            && _profiles.TryGetValue(CompositeKey(roleKey, subtypeOverride), out RobotAppearanceProfile? subtypeProfile))
        {
            return subtypeProfile;
        }

        if (_profiles.TryGetValue(roleKey, out RobotAppearanceProfile? profile))
        {
            return profile;
        }

        if (_profiles.TryGetValue("infantry", out profile))
        {
            return profile;
        }

        return RobotAppearanceProfile.CreateDefault(0.48f, 0.48f, 0.18f, 0.10f, "infantry");
    }

    private static string CompositeKey(string roleKey, string subtype)
        => $"{roleKey}:{(subtype ?? string.Empty).Trim().ToLowerInvariant()}";

    private static JsonObject ResolveEffectiveRoleNode(string roleKey, JsonObject roleNode)
    {
        if (!string.Equals(roleKey, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            return roleNode;
        }

        if (roleNode["subtype_profiles"] is not JsonObject subtypeProfiles)
        {
            return roleNode;
        }

        string? subtype = roleNode["default_chassis_subtype"]?.ToString()
            ?? roleNode["chassis_subtype"]?.ToString();
        if (!string.IsNullOrWhiteSpace(subtype)
            && subtypeProfiles[subtype] is JsonObject subtypeNode)
        {
            return subtypeNode;
        }

        return roleNode;
    }

    private static RobotAppearanceProfile ParseProfile(JsonObject primary, JsonObject fallback, RobotAppearanceProfile defaults)
    {
        float bodyLength = ReadFloat(primary, fallback, defaults.BodyLengthM, "body_length_m");
        float bodyWidth = ReadFloat(primary, fallback, defaults.BodyWidthM, "body_width_m");
        float bodyHeight = ReadFloat(primary, fallback, defaults.BodyHeightM, "body_height_m");
        float bodyClearance = ReadFloat(primary, fallback, defaults.BodyClearanceM, "body_clearance_m");

        return defaults with
        {
            RoleKey = ReadString(primary, fallback, defaults.RoleKey, "role_key"),
            ChassisSubtype = ReadString(primary, fallback, defaults.ChassisSubtype, "chassis_subtype"),
            BodyShape = ReadString(primary, fallback, defaults.BodyShape, "body_shape"),
            WheelStyle = ReadString(primary, fallback, defaults.WheelStyle, "wheel_style"),
            SuspensionStyle = ReadString(primary, fallback, defaults.SuspensionStyle, "suspension_style"),
            ArmStyle = ReadString(primary, fallback, defaults.ArmStyle, "arm_style"),
            FrontClimbAssistStyle = ReadString(primary, fallback, defaults.FrontClimbAssistStyle, "front_climb_assist_style"),
            RearClimbAssistStyle = ReadString(primary, fallback, defaults.RearClimbAssistStyle, "rear_climb_assist_style"),
            RearClimbAssistKneeDirection = ReadString(primary, fallback, defaults.RearClimbAssistKneeDirection, "rear_climb_assist_knee_direction"),
            ChassisSupportsJump = ReadBoolean(primary, fallback, defaults.ChassisSupportsJump, "chassis_supports_jump"),
            BodyLengthM = Math.Max(0.12f, bodyLength),
            BodyWidthM = Math.Max(0.10f, bodyWidth),
            BodyHeightM = Math.Max(0.08f, bodyHeight),
            BodyClearanceM = Math.Max(0f, bodyClearance),
            BodyRenderWidthScale = Math.Clamp(ReadFloat(primary, fallback, defaults.BodyRenderWidthScale, "body_render_width_scale"), 0.4f, 1.35f),
            StructureBaseLiftM = Math.Clamp(ReadFloat(primary, fallback, defaults.StructureBaseLiftM, "structure_base_lift_m"), 0.0f, 1.20f),
            WheelRadiusM = Math.Clamp(ReadFloat(primary, fallback, defaults.WheelRadiusM, "wheel_radius_m"), 0.03f, 0.28f),
            WheelOffsetsM = ReadWheelOffsets(primary, fallback, bodyLength, bodyWidth, defaults.WheelOffsetsM),
            ArmorOrbitYawsDeg = ReadFloatArray(primary, fallback, defaults.ArmorOrbitYawsDeg, "armor_orbit_yaws_deg"),
            ArmorSelfYawsDeg = ReadFloatArray(primary, fallback, defaults.ArmorSelfYawsDeg, "armor_self_yaws_deg"),
            GimbalLengthM = Math.Max(0f, ReadFloat(primary, fallback, defaults.GimbalLengthM, "gimbal_length_m")),
            GimbalWidthM = Math.Max(0f, ReadFloat(primary, fallback, defaults.GimbalWidthM, "gimbal_width_m")),
            GimbalBodyHeightM = Math.Max(0f, ReadFloat(primary, fallback, defaults.GimbalBodyHeightM, "gimbal_body_height_m")),
            GimbalHeightM = Math.Max(0f, ReadFloat(primary, fallback, defaults.GimbalHeightM, "gimbal_height_m")),
            GimbalOffsetXM = ReadFloat(primary, fallback, defaults.GimbalOffsetXM, "gimbal_offset_x_m"),
            GimbalOffsetYM = ReadFloat(primary, fallback, defaults.GimbalOffsetYM, "gimbal_offset_y_m"),
            GimbalMountGapM = Math.Max(0f, ReadFloat(primary, fallback, defaults.GimbalMountGapM, "gimbal_mount_gap_m")),
            GimbalMountLengthM = Math.Max(0f, ReadFloat(primary, fallback, defaults.GimbalMountLengthM, "gimbal_mount_length_m")),
            GimbalMountWidthM = Math.Max(0f, ReadFloat(primary, fallback, defaults.GimbalMountWidthM, "gimbal_mount_width_m")),
            GimbalMountHeightM = Math.Max(0f, ReadFloat(primary, fallback, defaults.GimbalMountHeightM, "gimbal_mount_height_m")),
            BarrelLengthM = Math.Max(0f, ReadFloat(primary, fallback, defaults.BarrelLengthM, "barrel_length_m")),
            BarrelRadiusM = Math.Max(0f, ReadFloat(primary, fallback, defaults.BarrelRadiusM, "barrel_radius_m")),
            ArmorPlateWidthM = Math.Max(0.03f, ReadFloat(primary, fallback, defaults.ArmorPlateWidthM, "armor_plate_width_m")),
            ArmorPlateLengthM = Math.Max(0.03f, ReadFloat(primary, fallback, defaults.ArmorPlateLengthM, "armor_plate_length_m")),
            ArmorPlateHeightM = Math.Max(0.03f, ReadFloat(primary, fallback, defaults.ArmorPlateHeightM, "armor_plate_height_m")),
            ArmorPlateGapM = Math.Max(0.003f, ReadFloat(primary, fallback, defaults.ArmorPlateGapM, "armor_plate_gap_m")),
            ArmorLightLengthM = Math.Max(0.004f, ReadFloat(primary, fallback, defaults.ArmorLightLengthM, "armor_light_length_m")),
            ArmorLightWidthM = Math.Max(0.003f, ReadFloat(primary, fallback, defaults.ArmorLightWidthM, "armor_light_width_m")),
            ArmorLightHeightM = Math.Max(0.004f, ReadFloat(primary, fallback, defaults.ArmorLightHeightM, "armor_light_height_m")),
            BarrelLightLengthM = Math.Max(0.004f, ReadFloat(primary, fallback, defaults.BarrelLightLengthM, "barrel_light_length_m")),
            BarrelLightWidthM = Math.Max(0.003f, ReadFloat(primary, fallback, defaults.BarrelLightWidthM, "barrel_light_width_m")),
            BarrelLightHeightM = Math.Max(0.003f, ReadFloat(primary, fallback, defaults.BarrelLightHeightM, "barrel_light_height_m")),
            FrontClimbAssistTopLengthM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.FrontClimbAssistTopLengthM, "front_climb_assist_top_length_m")),
            FrontClimbAssistBottomLengthM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.FrontClimbAssistBottomLengthM, "front_climb_assist_bottom_length_m")),
            FrontClimbAssistPlateWidthM = Math.Max(0.008f, ReadFloat(primary, fallback, defaults.FrontClimbAssistPlateWidthM, "front_climb_assist_plate_width_m")),
            FrontClimbAssistPlateHeightM = Math.Max(0.03f, ReadFloat(primary, fallback, defaults.FrontClimbAssistPlateHeightM, "front_climb_assist_plate_height_m")),
            FrontClimbAssistForwardOffsetM = ReadFloat(primary, fallback, defaults.FrontClimbAssistForwardOffsetM, "front_climb_assist_forward_offset_m"),
            FrontClimbAssistInnerOffsetM = ReadFloat(primary, fallback, defaults.FrontClimbAssistInnerOffsetM, "front_climb_assist_inner_offset_m"),
            RearClimbAssistUpperLengthM = Math.Max(0.03f, ReadFloat(primary, fallback, defaults.RearClimbAssistUpperLengthM, "rear_climb_assist_upper_length_m")),
            RearClimbAssistLowerLengthM = Math.Max(0.03f, ReadFloat(primary, fallback, defaults.RearClimbAssistLowerLengthM, "rear_climb_assist_lower_length_m")),
            RearClimbAssistUpperWidthM = Math.Max(0.008f, ReadFloat(primary, fallback, defaults.RearClimbAssistUpperWidthM, "rear_climb_assist_upper_width_m")),
            RearClimbAssistUpperHeightM = Math.Max(0.008f, ReadFloat(primary, fallback, defaults.RearClimbAssistUpperHeightM, "rear_climb_assist_upper_height_m")),
            RearClimbAssistLowerWidthM = Math.Max(0.008f, ReadFloat(primary, fallback, defaults.RearClimbAssistLowerWidthM, "rear_climb_assist_lower_width_m")),
            RearClimbAssistLowerHeightM = Math.Max(0.008f, ReadFloat(primary, fallback, defaults.RearClimbAssistLowerHeightM, "rear_climb_assist_lower_height_m")),
            RearClimbAssistMountOffsetXM = ReadFloat(primary, fallback, defaults.RearClimbAssistMountOffsetXM, "rear_climb_assist_mount_offset_x_m"),
            RearClimbAssistMountHeightM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.RearClimbAssistMountHeightM, "rear_climb_assist_mount_height_m")),
            RearClimbAssistInnerOffsetM = ReadFloat(primary, fallback, defaults.RearClimbAssistInnerOffsetM, "rear_climb_assist_inner_offset_m"),
            RearClimbAssistUpperPairGapM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.RearClimbAssistUpperPairGapM, "rear_climb_assist_upper_pair_gap_m")),
            RearClimbAssistHingeRadiusM = Math.Max(0.004f, ReadFloat(primary, fallback, defaults.RearClimbAssistHingeRadiusM, "rear_climb_assist_hinge_radius_m")),
            RearClimbAssistKneeMinDeg = ReadFloat(primary, fallback, defaults.RearClimbAssistKneeMinDeg, "rear_climb_assist_knee_min_deg"),
            RearClimbAssistKneeMaxDeg = ReadFloat(primary, fallback, defaults.RearClimbAssistKneeMaxDeg, "rear_climb_assist_knee_max_deg"),
            ChassisSpeedScale = Math.Max(0.2f, ReadFloat(primary, fallback, defaults.ChassisSpeedScale, "chassis_speed_scale")),
            ChassisDrivePowerLimitW = Math.Max(10f, ReadFloat(primary, fallback, defaults.ChassisDrivePowerLimitW, "chassis_drive_power_limit_w")),
            ChassisDriveIdleDrawW = Math.Max(0f, ReadFloat(primary, fallback, defaults.ChassisDriveIdleDrawW, "chassis_drive_idle_draw_w")),
            ChassisDriveRpmCoeff = Math.Max(0f, ReadFloat(primary, fallback, defaults.ChassisDriveRpmCoeff, "chassis_drive_rpm_coeff")),
            ChassisDriveAccelCoeff = Math.Max(0f, ReadFloat(primary, fallback, defaults.ChassisDriveAccelCoeff, "chassis_drive_accel_coeff")),
            BodyColor = ReadColor(primary, fallback, "body_color_rgb", defaults.BodyColor),
            TurretColor = ReadColor(primary, fallback, "turret_color_rgb", defaults.TurretColor),
            ArmorColor = ReadColor(primary, fallback, "armor_color_rgb", defaults.ArmorColor),
            WheelColor = ReadColor(primary, fallback, "wheel_color_rgb", defaults.WheelColor),
        };
    }

    private static string ReadString(JsonObject primary, JsonObject fallback, string defaultValue, string key)
    {
        string? value = primary[key]?.ToString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        value = fallback[key]?.ToString();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static bool ReadBoolean(JsonObject primary, JsonObject fallback, bool defaultValue, string key)
    {
        if (TryReadBoolean(primary[key], out bool primaryValue))
        {
            return primaryValue;
        }

        if (TryReadBoolean(fallback[key], out bool fallbackValue))
        {
            return fallbackValue;
        }

        return defaultValue;
    }

    private static bool TryReadBoolean(JsonNode? node, out bool value)
    {
        value = false;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out bool boolValue))
        {
            value = boolValue;
            return true;
        }

        if (jsonValue.TryGetValue(out string? text))
        {
            if (bool.TryParse(text, out bool parsed))
            {
                value = parsed;
                return true;
            }

            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }
        }

        return false;
    }

    private static float ReadFloat(JsonObject primary, JsonObject fallback, float defaultValue, string key)
    {
        if (TryReadFloat(primary[key], out float primaryValue))
        {
            return primaryValue;
        }

        if (TryReadFloat(fallback[key], out float fallbackValue))
        {
            return fallbackValue;
        }

        return defaultValue;
    }

    private static bool TryReadFloat(JsonNode? node, out float value)
    {
        value = 0f;
        if (node is not JsonValue valueNode)
        {
            return false;
        }

        if (valueNode.TryGetValue(out float floatValue))
        {
            value = floatValue;
            return true;
        }

        if (valueNode.TryGetValue(out double doubleValue))
        {
            value = (float)doubleValue;
            return true;
        }

        if (valueNode.TryGetValue(out int intValue))
        {
            value = intValue;
            return true;
        }

        if (valueNode.TryGetValue(out string? text) && float.TryParse(text, out float parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<float> ReadFloatArray(JsonObject primary, JsonObject fallback, IReadOnlyList<float> defaultValue, string key)
    {
        List<float> values = ParseFloatArray(primary[key] as JsonArray);
        if (values.Count > 0)
        {
            return values;
        }

        values = ParseFloatArray(fallback[key] as JsonArray);
        return values.Count > 0 ? values : defaultValue;
    }

    private static List<float> ParseFloatArray(JsonArray? array)
    {
        var result = new List<float>();
        if (array is null)
        {
            return result;
        }

        foreach (JsonNode? item in array)
        {
            if (TryReadFloat(item, out float value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static IReadOnlyList<Vector2> ReadWheelOffsets(
        JsonObject primary,
        JsonObject fallback,
        float bodyLength,
        float bodyWidth,
        IReadOnlyList<Vector2> defaultValue)
    {
        List<Vector2> values = ParseWheelArray(primary["custom_wheel_positions_m"] as JsonArray);
        if (values.Count > 0)
        {
            return values;
        }

        values = ParseWheelArray(fallback["custom_wheel_positions_m"] as JsonArray);
        if (values.Count > 0)
        {
            return values;
        }

        if (defaultValue.Count > 0)
        {
            return defaultValue;
        }

        float halfLength = bodyLength * 0.5f * 0.80f;
        float halfWidth = bodyWidth * 0.5f * 1.18f;
        return new[]
        {
            new Vector2(-halfLength, -halfWidth),
            new Vector2(halfLength, -halfWidth),
            new Vector2(-halfLength, halfWidth),
            new Vector2(halfLength, halfWidth),
        };
    }

    private static List<Vector2> ParseWheelArray(JsonArray? array)
    {
        var result = new List<Vector2>();
        if (array is null)
        {
            return result;
        }

        foreach (JsonNode? item in array)
        {
            if (item is not JsonArray pair || pair.Count < 2)
            {
                continue;
            }

            if (!TryReadFloat(pair[0], out float x) || !TryReadFloat(pair[1], out float y))
            {
                continue;
            }

            result.Add(new Vector2(x, y));
        }

        return result;
    }

    private static Color ReadColor(JsonObject primary, JsonObject fallback, string key, Color defaultColor)
    {
        if (TryReadColor(primary[key] as JsonArray, out Color color))
        {
            return color;
        }

        if (TryReadColor(fallback[key] as JsonArray, out color))
        {
            return color;
        }

        return defaultColor;
    }

    private static bool TryReadColor(JsonArray? array, out Color color)
    {
        color = Color.Empty;
        if (array is null || array.Count < 3)
        {
            return false;
        }

        if (!TryReadFloat(array[0], out float r)
            || !TryReadFloat(array[1], out float g)
            || !TryReadFloat(array[2], out float b))
        {
            return false;
        }

        color = Color.FromArgb(
            Math.Clamp((int)MathF.Round(r), 0, 255),
            Math.Clamp((int)MathF.Round(g), 0, 255),
            Math.Clamp((int)MathF.Round(b), 0, 255));
        return true;
    }
}

internal sealed record RobotAppearanceProfile
{
    public string RoleKey { get; init; } = "infantry";

    public string ChassisSubtype { get; init; } = string.Empty;

    public string BodyShape { get; init; } = "box";

    public string WheelStyle { get; init; } = "standard";

    public string SuspensionStyle { get; init; } = "four_bar";

    public string ArmStyle { get; init; } = "none";

    public string FrontClimbAssistStyle { get; init; } = "none";

    public string RearClimbAssistStyle { get; init; } = "none";

    public string RearClimbAssistKneeDirection { get; init; } = "rear";

    public bool ChassisSupportsJump { get; init; }

    public float BodyLengthM { get; init; }

    public float BodyWidthM { get; init; }

    public float BodyHeightM { get; init; }

    public float BodyClearanceM { get; init; }

    public float BodyRenderWidthScale { get; init; } = 1.0f;

    public float StructureBaseLiftM { get; init; }

    public float WheelRadiusM { get; init; } = 0.08f;

    public IReadOnlyList<Vector2> WheelOffsetsM { get; init; } = Array.Empty<Vector2>();

    public IReadOnlyList<float> ArmorOrbitYawsDeg { get; init; } = Array.Empty<float>();

    public IReadOnlyList<float> ArmorSelfYawsDeg { get; init; } = Array.Empty<float>();

    public float GimbalLengthM { get; init; } = 0.26f;

    public float GimbalWidthM { get; init; } = 0.16f;

    public float GimbalBodyHeightM { get; init; } = 0.10f;

    public float GimbalHeightM { get; init; } = 0.34f;

    public float GimbalOffsetXM { get; init; }

    public float GimbalOffsetYM { get; init; }

    public float GimbalMountGapM { get; init; } = 0.10f;

    public float GimbalMountLengthM { get; init; } = 0.10f;

    public float GimbalMountWidthM { get; init; } = 0.10f;

    public float GimbalMountHeightM { get; init; } = 0.04f;

    public float BarrelLengthM { get; init; } = 0.12f;

    public float BarrelRadiusM { get; init; } = 0.016f;

    public float ArmorPlateWidthM { get; init; } = 0.16f;

    public float ArmorPlateLengthM { get; init; } = 0.16f;

    public float ArmorPlateHeightM { get; init; } = 0.16f;

    public float ArmorPlateGapM { get; init; } = 0.02f;

    public float ArmorLightLengthM { get; init; } = 0.04f;

    public float ArmorLightWidthM { get; init; } = 0.005f;

    public float ArmorLightHeightM { get; init; } = 0.08f;

    public float BarrelLightLengthM { get; init; } = 0.10f;

    public float BarrelLightWidthM { get; init; } = 0.01f;

    public float BarrelLightHeightM { get; init; } = 0.03f;

    public float FrontClimbAssistTopLengthM { get; init; } = 0.05f;

    public float FrontClimbAssistBottomLengthM { get; init; } = 0.03f;

    public float FrontClimbAssistPlateWidthM { get; init; } = 0.018f;

    public float FrontClimbAssistPlateHeightM { get; init; } = 0.18f;

    public float FrontClimbAssistForwardOffsetM { get; init; } = 0.04f;

    public float FrontClimbAssistInnerOffsetM { get; init; } = 0.06f;

    public float RearClimbAssistUpperLengthM { get; init; } = 0.09f;

    public float RearClimbAssistLowerLengthM { get; init; } = 0.08f;

    public float RearClimbAssistUpperWidthM { get; init; } = 0.016f;

    public float RearClimbAssistUpperHeightM { get; init; } = 0.016f;

    public float RearClimbAssistLowerWidthM { get; init; } = 0.016f;

    public float RearClimbAssistLowerHeightM { get; init; } = 0.016f;

    public float RearClimbAssistMountOffsetXM { get; init; } = 0.03f;

    public float RearClimbAssistMountHeightM { get; init; } = 0.22f;

    public float RearClimbAssistInnerOffsetM { get; init; } = 0.03f;

    public float RearClimbAssistUpperPairGapM { get; init; } = 0.06f;

    public float RearClimbAssistHingeRadiusM { get; init; } = 0.016f;

    public float RearClimbAssistKneeMinDeg { get; init; } = 42f;

    public float RearClimbAssistKneeMaxDeg { get; init; } = 132f;

    public float ChassisSpeedScale { get; init; } = 1.0f;

    public float ChassisDrivePowerLimitW { get; init; } = 180f;

    public float ChassisDriveIdleDrawW { get; init; } = 16f;

    public float ChassisDriveRpmCoeff { get; init; } = 0.00005f;

    public float ChassisDriveAccelCoeff { get; init; } = 0.012f;

    public Color BodyColor { get; init; } = Color.FromArgb(166, 174, 186);

    public Color TurretColor { get; init; } = Color.FromArgb(232, 232, 236);

    public Color ArmorColor { get; init; } = Color.FromArgb(224, 229, 234);

    public Color WheelColor { get; init; } = Color.FromArgb(44, 44, 44);

    public void ApplyToEntity(SimulationEntity entity, double metersPerWorldUnit)
    {
        entity.ChassisSubtype = ChassisSubtype;
        entity.BodyShape = BodyShape;
        entity.WheelStyle = ResolveWheelStyle(entity.RoleKey, ChassisSubtype, WheelStyle);
        entity.FrontClimbAssistStyle = FrontClimbAssistStyle;
        entity.RearClimbAssistStyle = RearClimbAssistStyle;
        entity.RearClimbAssistKneeDirection = RearClimbAssistKneeDirection;
        entity.BodyLengthM = BodyLengthM;
        entity.BodyWidthM = BodyWidthM;
        entity.BodyHeightM = BodyHeightM;
        entity.BodyClearanceM = BodyClearanceM;
        entity.BodyRenderWidthScale = BodyRenderWidthScale;
        entity.StructureBaseLiftM = StructureBaseLiftM;
        entity.WheelRadiusM = WheelRadiusM;
        entity.WheelOffsetsM = WheelOffsetsM.Select(offset => ((double)offset.X, (double)offset.Y)).ToArray();
        entity.ArmorOrbitYawsDeg = ArmorOrbitYawsDeg.Select(value => (double)value).ToArray();
        entity.ArmorSelfYawsDeg = ArmorSelfYawsDeg.Select(value => (double)value).ToArray();
        entity.GimbalLengthM = GimbalLengthM;
        entity.GimbalWidthM = GimbalWidthM;
        entity.GimbalBodyHeightM = GimbalBodyHeightM;
        entity.GimbalHeightM = GimbalHeightM;
        entity.GimbalOffsetXM = GimbalOffsetXM;
        entity.GimbalOffsetYM = GimbalOffsetYM;
        entity.GimbalMountGapM = GimbalMountGapM;
        entity.GimbalMountLengthM = GimbalMountLengthM;
        entity.GimbalMountWidthM = GimbalMountWidthM;
        entity.GimbalMountHeightM = GimbalMountHeightM;
        entity.BarrelLengthM = BarrelLengthM;
        entity.BarrelRadiusM = BarrelRadiusM;
        entity.ArmorPlateWidthM = ArmorPlateWidthM;
        entity.ArmorPlateLengthM = ArmorPlateLengthM;
        entity.ArmorPlateHeightM = ArmorPlateHeightM;
        entity.ArmorPlateGapM = ArmorPlateGapM;
        entity.ArmorLightLengthM = ArmorLightLengthM;
        entity.ArmorLightWidthM = ArmorLightWidthM;
        entity.ArmorLightHeightM = ArmorLightHeightM;
        entity.BarrelLightLengthM = BarrelLightLengthM;
        entity.BarrelLightWidthM = BarrelLightWidthM;
        entity.BarrelLightHeightM = BarrelLightHeightM;
        entity.FrontClimbAssistTopLengthM = FrontClimbAssistTopLengthM;
        entity.FrontClimbAssistBottomLengthM = FrontClimbAssistBottomLengthM;
        entity.FrontClimbAssistPlateWidthM = FrontClimbAssistPlateWidthM;
        entity.FrontClimbAssistPlateHeightM = FrontClimbAssistPlateHeightM;
        entity.FrontClimbAssistForwardOffsetM = FrontClimbAssistForwardOffsetM;
        entity.FrontClimbAssistInnerOffsetM = FrontClimbAssistInnerOffsetM;
        entity.RearClimbAssistUpperLengthM = RearClimbAssistUpperLengthM;
        entity.RearClimbAssistLowerLengthM = RearClimbAssistLowerLengthM;
        entity.RearClimbAssistUpperWidthM = RearClimbAssistUpperWidthM;
        entity.RearClimbAssistUpperHeightM = RearClimbAssistUpperHeightM;
        entity.RearClimbAssistLowerWidthM = RearClimbAssistLowerWidthM;
        entity.RearClimbAssistLowerHeightM = RearClimbAssistLowerHeightM;
        entity.RearClimbAssistMountOffsetXM = RearClimbAssistMountOffsetXM;
        entity.RearClimbAssistMountHeightM = RearClimbAssistMountHeightM;
        entity.RearClimbAssistInnerOffsetM = RearClimbAssistInnerOffsetM;
        entity.RearClimbAssistUpperPairGapM = RearClimbAssistUpperPairGapM;
        entity.RearClimbAssistHingeRadiusM = RearClimbAssistHingeRadiusM;
        entity.RearClimbAssistKneeMinDeg = RearClimbAssistKneeMinDeg;
        entity.RearClimbAssistKneeMaxDeg = RearClimbAssistKneeMaxDeg;
        entity.ChassisSupportsJump = ChassisSupportsJump;
        entity.ChassisSpeedScale = ChassisSpeedScale;
        entity.ChassisDrivePowerLimitW = ChassisDrivePowerLimitW;
        entity.ChassisDriveIdleDrawW = ChassisDriveIdleDrawW;
        entity.ChassisDriveRpmCoeff = ChassisDriveRpmCoeff;
        entity.ChassisDriveAccelCoeff = ChassisDriveAccelCoeff;
        entity.MassKg = 20.0;

        if (entity.RoleKey.Equals("base", StringComparison.OrdinalIgnoreCase)
            || entity.RoleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase))
        {
            double structureRadiusM = entity.RoleKey.Equals("base", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(BodyLengthM, BodyWidthM * BodyRenderWidthScale) * 0.58
                : Math.Max(BodyLengthM, BodyWidthM * BodyRenderWidthScale) * 0.62;
            entity.CollisionRadiusWorld = Math.Max(0.35, structureRadiusM) / Math.Max(metersPerWorldUnit, 1e-6);
            return;
        }

        if (entity.RoleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase))
        {
            bool omni = string.Equals(ChassisSubtype, "omni_wheel", StringComparison.OrdinalIgnoreCase);
            entity.StepClimbDurationSec = omni ? 0.75 : 1.0;
            entity.DirectStepHeightM = omni ? 0.08 : 0.06;
            entity.MaxStepClimbHeightM = omni ? 0.14 : 0.35;
        }
        else if (entity.RoleKey.Equals("engineer", StringComparison.OrdinalIgnoreCase))
        {
            entity.StepClimbDurationSec = 1.0;
            entity.DirectStepHeightM = 0.07;
            entity.MaxStepClimbHeightM = 0.35;
        }
        else if (entity.RoleKey.Equals("hero", StringComparison.OrdinalIgnoreCase))
        {
            entity.StepClimbDurationSec = 1.0;
            entity.DirectStepHeightM = 0.08;
            entity.MaxStepClimbHeightM = 0.35;
        }
        else
        {
            entity.StepClimbDurationSec = 1.0;
            entity.DirectStepHeightM = 0.05;
            entity.MaxStepClimbHeightM = 0.35;
        }

        double halfLengthM = Math.Max(BodyLengthM * 0.5, GimbalOffsetXM + GimbalLengthM * 0.5 + BarrelLengthM);
        double halfWidthM = Math.Max(BodyWidthM * BodyRenderWidthScale * 0.5, Math.Abs(GimbalOffsetYM) + GimbalWidthM * BodyRenderWidthScale * 0.5);
        halfLengthM = Math.Max(halfLengthM, BodyLengthM * 0.5 + ArmorPlateGapM + ArmorPlateWidthM * 0.5);
        halfWidthM = Math.Max(halfWidthM, BodyWidthM * BodyRenderWidthScale * 0.5 + ArmorPlateGapM + ArmorPlateWidthM * 0.5);
        foreach (Vector2 wheelOffset in WheelOffsetsM)
        {
            halfLengthM = Math.Max(halfLengthM, Math.Abs(wheelOffset.X) + WheelRadiusM);
            halfWidthM = Math.Max(halfWidthM, Math.Abs(wheelOffset.Y) * BodyRenderWidthScale + WheelRadiusM);
        }

        if (!string.Equals(FrontClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            halfLengthM = Math.Max(
                halfLengthM,
                BodyLengthM * 0.5 + FrontClimbAssistForwardOffsetM + Math.Max(FrontClimbAssistTopLengthM, FrontClimbAssistBottomLengthM));
        }

        if (!string.Equals(RearClimbAssistStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            halfLengthM = Math.Max(halfLengthM, BodyLengthM * 0.5 + RearClimbAssistUpperLengthM + RearClimbAssistLowerLengthM);
            halfWidthM = Math.Max(halfWidthM, BodyWidthM * BodyRenderWidthScale * 0.5 + RearClimbAssistHingeRadiusM);
        }

        double collisionRadiusM = Math.Max(0.14, Math.Sqrt(halfLengthM * halfLengthM + halfWidthM * halfWidthM) + 0.01);
        entity.CollisionRadiusWorld = collisionRadiusM / Math.Max(metersPerWorldUnit, 1e-6);
    }

    private static string ResolveWheelStyle(string roleKey, string chassisSubtype, string configuredWheelStyle)
    {
        if (roleKey.Equals("base", StringComparison.OrdinalIgnoreCase)
            || roleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase))
        {
            return configuredWheelStyle;
        }

        if (roleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase))
        {
            if (chassisSubtype.Contains("balance", StringComparison.OrdinalIgnoreCase))
            {
                return "legged";
            }

            if (chassisSubtype.Contains("omni", StringComparison.OrdinalIgnoreCase))
            {
                return "omni";
            }
        }

        return "mecanum";
    }

    public static RobotAppearanceProfile CreateDefault(float bodyLength, float bodyWidth, float bodyHeight, float bodyClearance, string roleKey)
    {
        if (roleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase)
            || roleKey.Equals("base", StringComparison.OrdinalIgnoreCase))
        {
            return new RobotAppearanceProfile
            {
                RoleKey = roleKey,
                BodyShape = "octagon",
                WheelStyle = "structure",
                SuspensionStyle = "none",
                FrontClimbAssistStyle = "none",
                RearClimbAssistStyle = "none",
                BodyLengthM = bodyLength,
                BodyWidthM = bodyWidth,
                BodyHeightM = bodyHeight,
                BodyClearanceM = bodyClearance,
                BodyRenderWidthScale = 1.0f,
                StructureBaseLiftM = roleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase) ? 0.40f : 0.0f,
                WheelRadiusM = 0.03f,
                WheelOffsetsM = Array.Empty<Vector2>(),
                ArmorOrbitYawsDeg = Array.Empty<float>(),
                ArmorSelfYawsDeg = Array.Empty<float>(),
                GimbalLengthM = 0f,
                GimbalWidthM = 0f,
                GimbalBodyHeightM = 0f,
                GimbalHeightM = 0f,
                GimbalMountGapM = 0f,
                GimbalMountLengthM = 0f,
                GimbalMountWidthM = 0f,
                GimbalMountHeightM = 0f,
                BarrelLengthM = 0f,
                BarrelRadiusM = 0f,
                ArmorPlateWidthM = 0.13f,
                ArmorPlateLengthM = 0.13f,
                ArmorPlateHeightM = 0.13f,
                ArmorPlateGapM = 0.035f,
                BodyColor = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(142, 148, 154)
                    : Color.FromArgb(156, 160, 166),
                TurretColor = Color.FromArgb(196, 200, 206),
                ArmorColor = Color.FromArgb(206, 212, 218),
                WheelColor = Color.FromArgb(62, 68, 78),
            };
        }

        float halfLength = bodyLength * 0.5f * 0.80f;
        float halfWidth = bodyWidth * 0.5f * 1.18f;
        return new RobotAppearanceProfile
        {
            RoleKey = roleKey,
            ChassisSubtype = roleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase) ? "omni_wheel" : string.Empty,
            BodyLengthM = bodyLength,
            BodyWidthM = bodyWidth,
            BodyHeightM = bodyHeight,
            BodyClearanceM = bodyClearance,
            WheelOffsetsM = new[]
            {
                new Vector2(-halfLength, -halfWidth),
                new Vector2(halfLength, -halfWidth),
                new Vector2(-halfLength, halfWidth),
                new Vector2(halfLength, halfWidth),
            },
            ArmorOrbitYawsDeg = new[] { 0f, 180f, 90f, 270f },
            ArmorSelfYawsDeg = new[] { 0f, 180f, 90f, 270f },
            BodyShape = roleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase) ? "octagon" : "box",
            WheelStyle = roleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase) ? "omni" : "mecanum",
            FrontClimbAssistStyle = roleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase) ? "none" : "belt_lift",
            RearClimbAssistStyle = roleKey.Equals("infantry", StringComparison.OrdinalIgnoreCase) ? "none" : "balance_leg",
            ChassisSupportsJump = false,
            ChassisDrivePowerLimitW = roleKey switch
            {
                "hero" => 180f,
                "engineer" => 150f,
                "sentry" => 120f,
                _ => 150f,
            },
        };
    }
}
