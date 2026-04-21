using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Simulator.Assets;

public sealed class RobotAppearanceRoot
{
    [JsonPropertyName("profiles")]
    public Dictionary<string, RobotAppearanceProfileDefinition> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void EnsureInitialized()
    {
        Profiles ??= new Dictionary<string, RobotAppearanceProfileDefinition>(StringComparer.OrdinalIgnoreCase);
        var normalized = new Dictionary<string, RobotAppearanceProfileDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, RobotAppearanceProfileDefinition? value) in Profiles)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            RobotAppearanceProfileDefinition profile = value ?? new RobotAppearanceProfileDefinition();
            profile.EnsureInitialized();
            if (string.IsNullOrWhiteSpace(profile.RoleKey))
            {
                profile.RoleKey = key;
            }

            normalized[key] = profile;
        }

        Profiles = normalized;
    }
}

public sealed class RobotAppearanceProfileDefinition
{
    [JsonPropertyName("body_shape")]
    [Category("Body")]
    public string BodyShape { get; set; } = "box";

    [JsonPropertyName("body_length_m")]
    [Category("Body")]
    public double BodyLengthM { get; set; }

    [JsonPropertyName("body_width_m")]
    [Category("Body")]
    public double BodyWidthM { get; set; }

    [JsonPropertyName("body_height_m")]
    [Category("Body")]
    public double BodyHeightM { get; set; }

    [JsonPropertyName("body_clearance_m")]
    [Category("Body")]
    public double BodyClearanceM { get; set; }

    [JsonPropertyName("body_render_width_scale")]
    [Category("Body")]
    public double BodyRenderWidthScale { get; set; } = 1.0;

    [JsonPropertyName("role_key")]
    [Category("Identity")]
    [ReadOnly(true)]
    public string RoleKey { get; set; } = string.Empty;

    [JsonPropertyName("wheel_style")]
    [Category("Wheel")]
    public string WheelStyle { get; set; } = "mecanum";

    [JsonPropertyName("wheel_radius_m")]
    [Category("Wheel")]
    public double WheelRadiusM { get; set; }

    [JsonPropertyName("wheel_count")]
    [Category("Wheel")]
    public int WheelCount { get; set; }

    [JsonPropertyName("rear_leg_wheel_radius_m")]
    [Category("Wheel")]
    public double RearLegWheelRadiusM { get; set; }

    [JsonPropertyName("suspension_style")]
    [Category("Wheel")]
    public string SuspensionStyle { get; set; } = "four_bar";

    [JsonPropertyName("custom_wheel_positions_m")]
    [Browsable(false)]
    public List<List<double>> CustomWheelPositionsM { get; set; } = new();

    [JsonIgnore]
    [Category("Wheel")]
    [DisplayName("CustomWheelPositions")]
    [Description("Format: x,y; x,y")]
    public string CustomWheelPositionsText
    {
        get => SerializePointPairs(CustomWheelPositionsM);
        set => CustomWheelPositionsM = ParsePointPairs(value);
    }

    [JsonPropertyName("wheel_orbit_yaws_deg")]
    [Browsable(false)]
    public List<double> WheelOrbitYawsDeg { get; set; } = new();

    [JsonIgnore]
    [Category("Wheel")]
    [DisplayName("WheelOrbitYawsDeg")]
    public string WheelOrbitYawsText
    {
        get => SerializeDoubleList(WheelOrbitYawsDeg);
        set => WheelOrbitYawsDeg = ParseDoubleList(value);
    }

    [JsonPropertyName("wheel_self_yaws_deg")]
    [Browsable(false)]
    public List<double> WheelSelfYawsDeg { get; set; } = new();

    [JsonIgnore]
    [Category("Wheel")]
    [DisplayName("WheelSelfYawsDeg")]
    public string WheelSelfYawsText
    {
        get => SerializeDoubleList(WheelSelfYawsDeg);
        set => WheelSelfYawsDeg = ParseDoubleList(value);
    }

    [JsonPropertyName("gimbal_length_m")]
    [Category("Gimbal")]
    public double GimbalLengthM { get; set; }

    [JsonPropertyName("gimbal_width_m")]
    [Category("Gimbal")]
    public double GimbalWidthM { get; set; }

    [JsonPropertyName("gimbal_body_height_m")]
    [Category("Gimbal")]
    public double GimbalBodyHeightM { get; set; }

    [JsonPropertyName("gimbal_mount_gap_m")]
    [Category("Gimbal")]
    public double GimbalMountGapM { get; set; }

    [JsonPropertyName("gimbal_mount_length_m")]
    [Category("Gimbal")]
    public double GimbalMountLengthM { get; set; }

    [JsonPropertyName("gimbal_mount_width_m")]
    [Category("Gimbal")]
    public double GimbalMountWidthM { get; set; }

    [JsonPropertyName("gimbal_mount_height_m")]
    [Category("Gimbal")]
    public double GimbalMountHeightM { get; set; }

    [JsonPropertyName("barrel_length_m")]
    [Category("Gimbal")]
    public double BarrelLengthM { get; set; }

    [JsonPropertyName("barrel_radius_m")]
    [Category("Gimbal")]
    public double BarrelRadiusM { get; set; }

    [JsonPropertyName("gimbal_height_m")]
    [Category("Gimbal")]
    public double GimbalHeightM { get; set; }

    [JsonPropertyName("gimbal_offset_x_m")]
    [Category("Gimbal")]
    public double GimbalOffsetXM { get; set; }

    [JsonPropertyName("gimbal_offset_y_m")]
    [Category("Gimbal")]
    public double GimbalOffsetYM { get; set; }

    [JsonPropertyName("armor_plate_width_m")]
    [Category("Armor")]
    public double ArmorPlateWidthM { get; set; }

    [JsonPropertyName("armor_plate_length_m")]
    [Category("Armor")]
    public double ArmorPlateLengthM { get; set; }

    [JsonPropertyName("armor_plate_height_m")]
    [Category("Armor")]
    public double ArmorPlateHeightM { get; set; }

    [JsonPropertyName("armor_plate_gap_m")]
    [Category("Armor")]
    public double ArmorPlateGapM { get; set; }

    [JsonPropertyName("armor_orbit_yaws_deg")]
    [Browsable(false)]
    public List<double> ArmorOrbitYawsDeg { get; set; } = new();

    [JsonIgnore]
    [Category("Armor")]
    [DisplayName("ArmorOrbitYawsDeg")]
    public string ArmorOrbitYawsText
    {
        get => SerializeDoubleList(ArmorOrbitYawsDeg);
        set => ArmorOrbitYawsDeg = ParseDoubleList(value);
    }

    [JsonPropertyName("armor_self_yaws_deg")]
    [Browsable(false)]
    public List<double> ArmorSelfYawsDeg { get; set; } = new();

    [JsonIgnore]
    [Category("Armor")]
    [DisplayName("ArmorSelfYawsDeg")]
    public string ArmorSelfYawsText
    {
        get => SerializeDoubleList(ArmorSelfYawsDeg);
        set => ArmorSelfYawsDeg = ParseDoubleList(value);
    }

    [JsonPropertyName("armor_light_length_m")]
    [Category("Armor")]
    public double ArmorLightLengthM { get; set; }

    [JsonPropertyName("armor_light_width_m")]
    [Category("Armor")]
    public double ArmorLightWidthM { get; set; }

    [JsonPropertyName("armor_light_height_m")]
    [Category("Armor")]
    public double ArmorLightHeightM { get; set; }

    [JsonPropertyName("armor_light_orbit_yaws_deg")]
    [Browsable(false)]
    public List<double> ArmorLightOrbitYawsDeg { get; set; } = new();

    [JsonIgnore]
    [Category("Armor")]
    [DisplayName("ArmorLightOrbitYawsDeg")]
    public string ArmorLightOrbitYawsText
    {
        get => SerializeDoubleList(ArmorLightOrbitYawsDeg);
        set => ArmorLightOrbitYawsDeg = ParseDoubleList(value);
    }

    [JsonPropertyName("armor_light_self_yaws_deg")]
    [Browsable(false)]
    public List<double> ArmorLightSelfYawsDeg { get; set; } = new();

    [JsonIgnore]
    [Category("Armor")]
    [DisplayName("ArmorLightSelfYawsDeg")]
    public string ArmorLightSelfYawsText
    {
        get => SerializeDoubleList(ArmorLightSelfYawsDeg);
        set => ArmorLightSelfYawsDeg = ParseDoubleList(value);
    }

    [JsonPropertyName("barrel_light_length_m")]
    [Category("Armor")]
    public double BarrelLightLengthM { get; set; }

    [JsonPropertyName("barrel_light_width_m")]
    [Category("Armor")]
    public double BarrelLightWidthM { get; set; }

    [JsonPropertyName("barrel_light_height_m")]
    [Category("Armor")]
    public double BarrelLightHeightM { get; set; }

    [JsonPropertyName("body_color_rgb")]
    [Browsable(false)]
    public List<int> BodyColorRgb { get; set; } = new();

    [JsonIgnore]
    [Category("Color")]
    public Color BodyColor
    {
        get => ToColor(BodyColorRgb, Color.FromArgb(166, 174, 186));
        set => BodyColorRgb = FromColor(value);
    }

    [JsonPropertyName("turret_color_rgb")]
    [Browsable(false)]
    public List<int> TurretColorRgb { get; set; } = new();

    [JsonIgnore]
    [Category("Color")]
    public Color TurretColor
    {
        get => ToColor(TurretColorRgb, Color.FromArgb(232, 232, 236));
        set => TurretColorRgb = FromColor(value);
    }

    [JsonPropertyName("armor_color_rgb")]
    [Browsable(false)]
    public List<int> ArmorColorRgb { get; set; } = new();

    [JsonIgnore]
    [Category("Color")]
    public Color ArmorColor
    {
        get => ToColor(ArmorColorRgb, Color.FromArgb(224, 229, 234));
        set => ArmorColorRgb = FromColor(value);
    }

    [JsonPropertyName("wheel_color_rgb")]
    [Browsable(false)]
    public List<int> WheelColorRgb { get; set; } = new();

    [JsonIgnore]
    [Category("Color")]
    public Color WheelColor
    {
        get => ToColor(WheelColorRgb, Color.FromArgb(44, 44, 44));
        set => WheelColorRgb = FromColor(value);
    }

    [JsonPropertyName("arm_style")]
    [Category("Attachment")]
    public string ArmStyle { get; set; } = "none";

    [JsonPropertyName("front_climb_assist_style")]
    [Category("Attachment")]
    public string FrontClimbAssistStyle { get; set; } = "none";

    [JsonPropertyName("rear_climb_assist_style")]
    [Category("Attachment")]
    public string RearClimbAssistStyle { get; set; } = "none";

    [JsonPropertyName("front_climb_assist_top_length_m")]
    [Category("Attachment")]
    public double FrontClimbAssistTopLengthM { get; set; }

    [JsonPropertyName("front_climb_assist_bottom_length_m")]
    [Category("Attachment")]
    public double FrontClimbAssistBottomLengthM { get; set; }

    [JsonPropertyName("front_climb_assist_plate_width_m")]
    [Category("Attachment")]
    public double FrontClimbAssistPlateWidthM { get; set; }

    [JsonPropertyName("front_climb_assist_plate_height_m")]
    [Category("Attachment")]
    public double FrontClimbAssistPlateHeightM { get; set; }

    [JsonPropertyName("front_climb_assist_forward_offset_m")]
    [Category("Attachment")]
    public double FrontClimbAssistForwardOffsetM { get; set; }

    [JsonPropertyName("front_climb_assist_inner_offset_m")]
    [Category("Attachment")]
    public double FrontClimbAssistInnerOffsetM { get; set; }

    [JsonPropertyName("rear_climb_assist_upper_length_m")]
    [Category("Attachment")]
    public double RearClimbAssistUpperLengthM { get; set; }

    [JsonPropertyName("rear_climb_assist_lower_length_m")]
    [Category("Attachment")]
    public double RearClimbAssistLowerLengthM { get; set; }

    [JsonPropertyName("rear_climb_assist_upper_width_m")]
    [Category("Attachment")]
    public double RearClimbAssistUpperWidthM { get; set; }

    [JsonPropertyName("rear_climb_assist_upper_height_m")]
    [Category("Attachment")]
    public double RearClimbAssistUpperHeightM { get; set; }

    [JsonPropertyName("rear_climb_assist_lower_width_m")]
    [Category("Attachment")]
    public double RearClimbAssistLowerWidthM { get; set; }

    [JsonPropertyName("rear_climb_assist_lower_height_m")]
    [Category("Attachment")]
    public double RearClimbAssistLowerHeightM { get; set; }

    [JsonPropertyName("rear_climb_assist_mount_offset_x_m")]
    [Category("Attachment")]
    public double RearClimbAssistMountOffsetXM { get; set; }

    [JsonPropertyName("rear_climb_assist_mount_height_m")]
    [Category("Attachment")]
    public double RearClimbAssistMountHeightM { get; set; }

    [JsonPropertyName("rear_climb_assist_inner_offset_m")]
    [Category("Attachment")]
    public double RearClimbAssistInnerOffsetM { get; set; }

    [JsonPropertyName("rear_climb_assist_upper_pair_gap_m")]
    [Category("Attachment")]
    public double RearClimbAssistUpperPairGapM { get; set; }

    [JsonPropertyName("rear_climb_assist_hinge_radius_m")]
    [Category("Attachment")]
    public double RearClimbAssistHingeRadiusM { get; set; }

    [JsonPropertyName("rear_climb_assist_knee_min_deg")]
    [Category("Attachment")]
    public double RearClimbAssistKneeMinDeg { get; set; }

    [JsonPropertyName("rear_climb_assist_knee_max_deg")]
    [Category("Attachment")]
    public double RearClimbAssistKneeMaxDeg { get; set; }

    [JsonPropertyName("rear_climb_assist_knee_direction")]
    [Category("Attachment")]
    public string RearClimbAssistKneeDirection { get; set; } = "rear";

    [JsonPropertyName("chassis_subtype")]
    [Category("Chassis")]
    public string ChassisSubtype { get; set; } = string.Empty;

    [JsonPropertyName("default_chassis_subtype")]
    [Category("Chassis")]
    public string DefaultChassisSubtype { get; set; } = string.Empty;

    [JsonPropertyName("chassis_supports_jump")]
    [Category("Physics")]
    public bool ChassisSupportsJump { get; set; }

    [JsonPropertyName("chassis_speed_scale")]
    [Category("Physics")]
    public double ChassisSpeedScale { get; set; } = 1.0;

    [JsonPropertyName("chassis_drive_power_limit_w")]
    [Category("Physics")]
    public double ChassisDrivePowerLimitW { get; set; }

    [JsonPropertyName("chassis_drive_idle_draw_w")]
    [Category("Physics")]
    public double ChassisDriveIdleDrawW { get; set; }

    [JsonPropertyName("chassis_drive_rpm_coeff")]
    [Category("Physics")]
    public double ChassisDriveRpmCoeff { get; set; }

    [JsonPropertyName("chassis_drive_accel_coeff")]
    [Category("Physics")]
    public double ChassisDriveAccelCoeff { get; set; }

    [JsonPropertyName("subtype_profiles")]
    [Browsable(false)]
    public Dictionary<string, RobotAppearanceProfileDefinition> SubtypeProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("structure_body_top_height_m")]
    [Category("Structure")]
    public double StructureBodyTopHeightM { get; set; }

    [JsonPropertyName("structure_head_base_height_m")]
    [Category("Structure")]
    public double StructureHeadBaseHeightM { get; set; }

    [JsonPropertyName("structure_lower_shoulder_height_m")]
    [Category("Structure")]
    public double StructureLowerShoulderHeightM { get; set; }

    [JsonPropertyName("structure_upper_shoulder_height_m")]
    [Category("Structure")]
    public double StructureUpperShoulderHeightM { get; set; }

    [JsonPropertyName("structure_tower_radius_m")]
    [Category("Structure")]
    public double StructureTowerRadiusM { get; set; }

    [JsonPropertyName("structure_top_armor_center_height_m")]
    [Category("Structure")]
    public double StructureTopArmorCenterHeightM { get; set; }

    [JsonPropertyName("structure_top_armor_offset_x_m")]
    [Category("Structure")]
    public double StructureTopArmorOffsetXM { get; set; }

    [JsonPropertyName("structure_top_armor_offset_z_m")]
    [Category("Structure")]
    public double StructureTopArmorOffsetZM { get; set; }

    [JsonPropertyName("structure_top_armor_tilt_deg")]
    [Category("Structure")]
    public double StructureTopArmorTiltDeg { get; set; }

    [JsonPropertyName("structure_side_armor_open_angle_deg")]
    [Category("Structure")]
    public double StructureSideArmorOpenAngleDeg { get; set; }

    [JsonPropertyName("structure_side_armor_outward_offset_m")]
    [Category("Structure")]
    public double StructureSideArmorOutwardOffsetM { get; set; }

    [JsonPropertyName("structure_base_lift_m")]
    [Category("Structure")]
    public double StructureBaseLiftM { get; set; }

    [JsonPropertyName("structure_ground_clearance_m")]
    [Category("Structure")]
    public double StructureGroundClearanceM { get; set; }

    [JsonPropertyName("structure_base_height_m")]
    [Category("Structure")]
    public double StructureBaseHeightM { get; set; }

    [JsonPropertyName("structure_base_length_m")]
    [Category("Structure")]
    public double StructureBaseLengthM { get; set; }

    [JsonPropertyName("structure_base_width_m")]
    [Category("Structure")]
    public double StructureBaseWidthM { get; set; }

    [JsonPropertyName("structure_base_top_length_m")]
    [Category("Structure")]
    public double StructureBaseTopLengthM { get; set; }

    [JsonPropertyName("structure_base_top_width_m")]
    [Category("Structure")]
    public double StructureBaseTopWidthM { get; set; }

    [JsonPropertyName("structure_base_top_height_m")]
    [Category("Structure")]
    public double StructureBaseTopHeightM { get; set; }

    [JsonPropertyName("structure_frame_width_m")]
    [Category("Structure")]
    public double StructureFrameWidthM { get; set; }

    [JsonPropertyName("structure_frame_depth_m")]
    [Category("Structure")]
    public double StructureFrameDepthM { get; set; }

    [JsonPropertyName("structure_frame_height_m")]
    [Category("Structure")]
    public double StructureFrameHeightM { get; set; }

    [JsonPropertyName("structure_column_span_m")]
    [Category("Structure")]
    public double StructureColumnSpanM { get; set; }

    [JsonPropertyName("structure_support_offset_m")]
    [Category("Structure")]
    public double StructureSupportOffsetM { get; set; }

    [JsonPropertyName("structure_frame_column_width_m")]
    [Category("Structure")]
    public double StructureFrameColumnWidthM { get; set; }

    [JsonPropertyName("structure_frame_beam_height_m")]
    [Category("Structure")]
    public double StructureFrameBeamHeightM { get; set; }

    [JsonPropertyName("structure_rotor_center_height_m")]
    [Category("Structure")]
    public double StructureRotorCenterHeightM { get; set; }

    [JsonPropertyName("structure_rotor_phase_deg")]
    [Category("Structure")]
    public double StructureRotorPhaseDeg { get; set; }

    [JsonPropertyName("structure_rotor_radius_m")]
    [Category("Structure")]
    public double StructureRotorRadiusM { get; set; }

    [JsonPropertyName("structure_rotor_hub_radius_m")]
    [Category("Structure")]
    public double StructureRotorHubRadiusM { get; set; }

    [JsonPropertyName("structure_rotor_arm_length_m")]
    [Category("Structure")]
    public double StructureRotorArmLengthM { get; set; }

    [JsonPropertyName("structure_rotor_arm_width_m")]
    [Category("Structure")]
    public double StructureRotorArmWidthM { get; set; }

    [JsonPropertyName("structure_rotor_arm_height_m")]
    [Category("Structure")]
    public double StructureRotorArmHeightM { get; set; }

    [JsonPropertyName("structure_lamp_length_m")]
    [Category("Structure")]
    public double StructureLampLengthM { get; set; }

    [JsonPropertyName("structure_lamp_width_m")]
    [Category("Structure")]
    public double StructureLampWidthM { get; set; }

    [JsonPropertyName("structure_lamp_height_m")]
    [Category("Structure")]
    public double StructureLampHeightM { get; set; }

    [JsonPropertyName("structure_hanger_width_m")]
    [Category("Structure")]
    public double StructureHangerWidthM { get; set; }

    [JsonPropertyName("structure_hanger_height_m")]
    [Category("Structure")]
    public double StructureHangerHeightM { get; set; }

    [JsonPropertyName("structure_hanger_depth_m")]
    [Category("Structure")]
    public double StructureHangerDepthM { get; set; }

    [JsonPropertyName("structure_hanger_center_height_m")]
    [Category("Structure")]
    public double StructureHangerCenterHeightM { get; set; }

    [JsonPropertyName("structure_lower_module_width_m")]
    [Category("Structure")]
    public double StructureLowerModuleWidthM { get; set; }

    [JsonPropertyName("structure_lower_module_height_m")]
    [Category("Structure")]
    public double StructureLowerModuleHeightM { get; set; }

    [JsonPropertyName("structure_lower_module_depth_m")]
    [Category("Structure")]
    public double StructureLowerModuleDepthM { get; set; }

    [JsonPropertyName("structure_lower_module_offset_x_m")]
    [Category("Structure")]
    public double StructureLowerModuleOffsetXM { get; set; }

    [JsonPropertyName("structure_lower_module_center_height_m")]
    [Category("Structure")]
    public double StructureLowerModuleCenterHeightM { get; set; }

    [JsonPropertyName("structure_cantilever_pair_gap_m")]
    [Category("Structure")]
    public double StructureCantileverPairGapM { get; set; }

    [JsonPropertyName("structure_cantilever_length_m")]
    [Category("Structure")]
    public double StructureCantileverLengthM { get; set; }

    [JsonPropertyName("structure_cantilever_offset_y_m")]
    [Category("Structure")]
    public double StructureCantileverOffsetYM { get; set; }

    [JsonPropertyName("structure_cantilever_height_m")]
    [Category("Structure")]
    public double StructureCantileverHeightM { get; set; }

    [JsonPropertyName("structure_cantilever_depth_m")]
    [Category("Structure")]
    public double StructureCantileverDepthM { get; set; }

    [JsonPropertyName("structure_roof_height_m")]
    [Category("Structure")]
    public double StructureRoofHeightM { get; set; }

    [JsonPropertyName("structure_shoulder_height_m")]
    [Category("Structure")]
    public double StructureShoulderHeightM { get; set; }

    [JsonPropertyName("structure_detector_width_m")]
    [Category("Structure")]
    public double StructureDetectorWidthM { get; set; }

    [JsonPropertyName("structure_detector_height_m")]
    [Category("Structure")]
    public double StructureDetectorHeightM { get; set; }

    [JsonPropertyName("structure_detector_bridge_center_height_m")]
    [Category("Structure")]
    public double StructureDetectorBridgeCenterHeightM { get; set; }

    [JsonPropertyName("structure_detector_sensor_center_height_m")]
    [Category("Structure")]
    public double StructureDetectorSensorCenterHeightM { get; set; }

    [JsonPropertyName("structure_core_column_height_m")]
    [Category("Structure")]
    public double StructureCoreColumnHeightM { get; set; }

    [JsonPropertyName("structure_hex_top_edge_m")]
    [Category("Structure")]
    public double StructureHexTopEdgeM { get; set; }

    public void EnsureInitialized()
    {
        BodyColorRgb = NormalizeRgbList(BodyColorRgb, Color.FromArgb(166, 174, 186));
        TurretColorRgb = NormalizeRgbList(TurretColorRgb, Color.FromArgb(232, 232, 236));
        ArmorColorRgb = NormalizeRgbList(ArmorColorRgb, Color.FromArgb(224, 229, 234));
        WheelColorRgb = NormalizeRgbList(WheelColorRgb, Color.FromArgb(44, 44, 44));
        CustomWheelPositionsM ??= new List<List<double>>();
        ArmorOrbitYawsDeg ??= new List<double>();
        ArmorSelfYawsDeg ??= new List<double>();
        ArmorLightOrbitYawsDeg ??= new List<double>();
        ArmorLightSelfYawsDeg ??= new List<double>();
        WheelOrbitYawsDeg ??= new List<double>();
        WheelSelfYawsDeg ??= new List<double>();
        SubtypeProfiles ??= new Dictionary<string, RobotAppearanceProfileDefinition>(StringComparer.OrdinalIgnoreCase);
        if (SubtypeProfiles.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            SubtypeProfiles = new Dictionary<string, RobotAppearanceProfileDefinition>(SubtypeProfiles, StringComparer.OrdinalIgnoreCase);
        }

        foreach ((string key, RobotAppearanceProfileDefinition profile) in SubtypeProfiles.ToArray())
        {
            profile.EnsureInitialized();
            if (string.IsNullOrWhiteSpace(profile.ChassisSubtype))
            {
                profile.ChassisSubtype = key;
            }

            if (string.IsNullOrWhiteSpace(profile.RoleKey))
            {
                profile.RoleKey = string.IsNullOrWhiteSpace(RoleKey) ? "infantry" : RoleKey;
            }
        }
    }

    public RobotAppearanceProfileDefinition DeepClone()
        => RobotAppearanceJsonSerializer.Clone(this);

    public IReadOnlyList<(double X, double Y)> GetWheelOffsetsOrDefaults()
    {
        if (CustomWheelPositionsM.Count > 0)
        {
            return CustomWheelPositionsM
                .Where(pair => pair.Count >= 2)
                .Select(pair => (pair[0], pair[1]))
                .ToArray();
        }

        int count = WheelCount > 0 ? WheelCount : 4;
        double halfLength = BodyLengthM * 0.5 * 0.8;
        double halfWidth = BodyWidthM * Math.Max(0.4, BodyRenderWidthScale) * 0.5 * 1.18;
        if (count <= 2)
        {
            return new[] { (0d, -halfWidth), (0d, halfWidth) };
        }

        return new[]
        {
            (-halfLength, -halfWidth),
            (halfLength, -halfWidth),
            (-halfLength, halfWidth),
            (halfLength, halfWidth),
        };
    }

    private static List<int> NormalizeRgbList(List<int>? values, Color fallback)
    {
        if (values is { Count: >= 3 })
        {
            return new List<int>
            {
                Math.Clamp(values[0], 0, 255),
                Math.Clamp(values[1], 0, 255),
                Math.Clamp(values[2], 0, 255),
            };
        }

        return new List<int> { fallback.R, fallback.G, fallback.B };
    }

    private static Color ToColor(List<int>? rgb, Color fallback)
    {
        List<int> normalized = NormalizeRgbList(rgb, fallback);
        return Color.FromArgb(normalized[0], normalized[1], normalized[2]);
    }

    private static List<int> FromColor(Color color)
        => new() { color.R, color.G, color.B };

    private static string SerializeDoubleList(IEnumerable<double>? values)
        => string.Join(", ", (values ?? Array.Empty<double>()).Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));

    private static List<double> ParseDoubleList(string? text)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (string token in text.Split(new[] { ',', ';', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static string SerializePointPairs(IEnumerable<List<double>>? pairs)
    {
        if (pairs is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (List<double> pair in pairs)
        {
            if (pair.Count < 2)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(pair[0].ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(", ");
            builder.Append(pair[1].ToString("0.###", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static List<List<double>> ParsePointPairs(string? text)
    {
        var result = new List<List<double>>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (string segment in text.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = segment.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length < 2)
            {
                continue;
            }

            if (double.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                result.Add(new List<double> { x, y });
            }
        }

        return result;
    }
}

public static class RobotAppearanceJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static RobotAppearanceRoot LoadFromFile(string path)
    {
        RobotAppearanceRoot root = CreateDefault();
        if (File.Exists(path))
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            root = Deserialize(json);
        }

        LoadSplitProfiles(root, GetSplitProfileDirectory(path));
        root.EnsureInitialized();
        return root;
    }

    public static void SaveToFile(string path, RobotAppearanceRoot root)
    {
        root.EnsureInitialized();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(path, Serialize(root), Encoding.UTF8);
        SaveSplitProfiles(root, GetSplitProfileDirectory(path));
    }

    public static RobotAppearanceRoot Deserialize(string json)
    {
        RobotAppearanceRoot root = JsonSerializer.Deserialize<RobotAppearanceRoot>(json, SerializerOptions) ?? CreateDefault();
        root.EnsureInitialized();
        return root;
    }

    public static string Serialize(RobotAppearanceRoot root)
    {
        root.EnsureInitialized();
        return JsonSerializer.Serialize(root, SerializerOptions);
    }

    public static RobotAppearanceProfileDefinition Clone(RobotAppearanceProfileDefinition profile)
    {
        string json = JsonSerializer.Serialize(profile, SerializerOptions);
        RobotAppearanceProfileDefinition clone = JsonSerializer.Deserialize<RobotAppearanceProfileDefinition>(json, SerializerOptions)
            ?? new RobotAppearanceProfileDefinition();
        clone.EnsureInitialized();
        return clone;
    }

    public static RobotAppearanceRoot CreateDefault()
    {
        var root = new RobotAppearanceRoot();
        root.EnsureInitialized();
        return root;
    }

    private static string GetSplitProfileDirectory(string path)
        => Path.Combine(Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory(), "profiles");

    private static void LoadSplitProfiles(RobotAppearanceRoot root, string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                RobotAppearanceProfileDefinition? profile = JsonSerializer.Deserialize<RobotAppearanceProfileDefinition>(json, SerializerOptions);
                if (profile is null)
                {
                    continue;
                }

                profile.EnsureInitialized();
                string roleKey = !string.IsNullOrWhiteSpace(profile.RoleKey)
                    ? profile.RoleKey
                    : Path.GetFileNameWithoutExtension(filePath);
                profile.RoleKey = roleKey;
                root.Profiles[roleKey] = profile;
            }
            catch
            {
                // Ignore malformed split profile files and keep the aggregate/surviving data.
            }
        }
    }

    private static void SaveSplitProfiles(RobotAppearanceRoot root, string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        var activeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string roleKey, RobotAppearanceProfileDefinition profile) in root.Profiles)
        {
            RobotAppearanceProfileDefinition clone = Clone(profile);
            clone.RoleKey = string.IsNullOrWhiteSpace(clone.RoleKey) ? roleKey : clone.RoleKey;
            string filePath = Path.Combine(directoryPath, $"{roleKey}.json");
            File.WriteAllText(filePath, JsonSerializer.Serialize(clone, SerializerOptions), Encoding.UTF8);
            activeFiles.Add(Path.GetFullPath(filePath));
        }

        foreach (string staleFile in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!activeFiles.Contains(Path.GetFullPath(staleFile)))
            {
                File.Delete(staleFile);
            }
        }
    }
}

public static class RobotAppearanceValidator
{
    public static IReadOnlyList<string> ValidateProfile(string roleKey, string? subtypeKey, RobotAppearanceProfileDefinition profile)
    {
        var errors = new List<string>();
        string prefix = string.IsNullOrWhiteSpace(subtypeKey) ? roleKey : $"{roleKey}:{subtypeKey}";

        ValidateNonNegative(profile.BodyLengthM, $"{prefix} body_length_m", errors);
        ValidateNonNegative(profile.BodyWidthM, $"{prefix} body_width_m", errors);
        ValidateNonNegative(profile.BodyHeightM, $"{prefix} body_height_m", errors);
        ValidateNonNegative(profile.BodyClearanceM, $"{prefix} body_clearance_m", errors);
        ValidateNonNegative(profile.WheelRadiusM, $"{prefix} wheel_radius_m", errors);
        ValidateNonNegative(profile.ArmorPlateGapM, $"{prefix} armor_plate_gap_m", errors);
        ValidateAngle(profile.RearClimbAssistKneeMinDeg, $"{prefix} rear_climb_assist_knee_min_deg", errors);
        ValidateAngle(profile.RearClimbAssistKneeMaxDeg, $"{prefix} rear_climb_assist_knee_max_deg", errors);
        ValidateAngle(profile.StructureTopArmorTiltDeg, $"{prefix} structure_top_armor_tilt_deg", errors);

        ValidateRgb(profile.BodyColorRgb, $"{prefix} body_color_rgb", errors);
        ValidateRgb(profile.TurretColorRgb, $"{prefix} turret_color_rgb", errors);
        ValidateRgb(profile.ArmorColorRgb, $"{prefix} armor_color_rgb", errors);
        ValidateRgb(profile.WheelColorRgb, $"{prefix} wheel_color_rgb", errors);

        for (int index = 0; index < profile.CustomWheelPositionsM.Count; index++)
        {
            if (profile.CustomWheelPositionsM[index].Count != 2)
            {
                errors.Add($"{prefix} custom_wheel_positions_m[{index}] 必须为 [x,y]");
            }
        }

        if (string.Equals(roleKey, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(profile.DefaultChassisSubtype) && string.IsNullOrWhiteSpace(subtypeKey))
            {
                errors.Add($"{prefix} 缺少 default_chassis_subtype");
            }

            if (string.IsNullOrWhiteSpace(subtypeKey)
                && (profile.SubtypeProfiles is null || profile.SubtypeProfiles.Count == 0))
            {
                errors.Add($"{prefix} 缺少 subtype_profiles");
            }
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateRoot(RobotAppearanceRoot root)
    {
        var errors = new List<string>();
        root.EnsureInitialized();
        foreach ((string roleKey, RobotAppearanceProfileDefinition profile) in root.Profiles)
        {
            errors.AddRange(ValidateProfile(roleKey, null, profile));
            foreach ((string subtypeKey, RobotAppearanceProfileDefinition subtype) in profile.SubtypeProfiles)
            {
                errors.AddRange(ValidateProfile(roleKey, subtypeKey, subtype));
            }
        }

        return errors;
    }

    private static void ValidateNonNegative(double value, string fieldName, List<string> errors)
    {
        if (value < 0)
        {
            errors.Add($"{fieldName} 不能为负数");
        }
    }

    private static void ValidateRgb(List<int>? rgb, string fieldName, List<string> errors)
    {
        if (rgb is null || rgb.Count != 3)
        {
            errors.Add($"{fieldName} 必须是 3 个 RGB 整数");
            return;
        }

        if (rgb.Any(value => value < 0 || value > 255))
        {
            errors.Add($"{fieldName} 必须在 0..255 范围内");
        }
    }

    private static void ValidateAngle(double value, string fieldName, List<string> errors)
    {
        if (value < -360 || value > 360)
        {
            errors.Add($"{fieldName} 超出角度范围");
        }
    }
}

public static class RobotAppearanceProjectAdapter
{
    public static RobotAppearanceProfileDefinition? ResolveProfile(RobotAppearanceRoot root, string roleKey, string? subtypeOverride = null)
    {
        root.EnsureInitialized();
        return root.Profiles.TryGetValue(roleKey, out RobotAppearanceProfileDefinition? profile)
            ? ResolveProfile(profile, subtypeOverride)
            : null;
    }

    public static RobotAppearanceProfileDefinition ResolveProfile(RobotAppearanceProfileDefinition profile, string? subtypeOverride = null)
    {
        profile.EnsureInitialized();
        string? subtype = !string.IsNullOrWhiteSpace(subtypeOverride)
            ? subtypeOverride
            : (!string.IsNullOrWhiteSpace(profile.DefaultChassisSubtype) ? profile.DefaultChassisSubtype : profile.ChassisSubtype);
        if (!string.IsNullOrWhiteSpace(subtype)
            && profile.SubtypeProfiles.TryGetValue(subtype, out RobotAppearanceProfileDefinition? subtypeProfile))
        {
            subtypeProfile.EnsureInitialized();
            return subtypeProfile.DeepClone();
        }

        return profile.DeepClone();
    }
}
