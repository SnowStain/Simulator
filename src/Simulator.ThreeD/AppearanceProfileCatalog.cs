using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using Simulator.Assets;
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
            ["outpost"] = RobotAppearanceProfile.CreateDefault(0.65f, 0.55f, 1.578f, 0.0f, "outpost"),
            ["base"] = RobotAppearanceProfile.CreateDefault(1.881f, 1.609f, 1.181f, 0.0f, "base"),
            ["energy_mechanism"] = RobotAppearanceProfile.CreateDefault(2.06f, 1.30f, 2.30f, 0.0f, "energy_mechanism"),
        };

        if (!File.Exists(appearancePath))
        {
            return new AppearanceProfileCatalog(profiles);
        }

        try
        {
            RobotAppearanceRoot root = RobotAppearanceJsonSerializer.LoadFromFile(appearancePath);
            if (root.Profiles.Count == 0)
            {
                return new AppearanceProfileCatalog(profiles);
            }

            foreach (string roleKey in new[] { "hero", "engineer", "infantry", "sentry", "outpost", "base", "energy_mechanism" })
            {
                if (!root.Profiles.TryGetValue(roleKey, out RobotAppearanceProfileDefinition? roleProfile))
                {
                    continue;
                }

                RobotAppearanceProfileDefinition effective = RobotAppearanceProjectAdapter.ResolveProfile(roleProfile);
                profiles[roleKey] = ParseProfile(effective, profiles[roleKey]);

                foreach ((string subtypeKey, RobotAppearanceProfileDefinition subtypeProfile) in roleProfile.SubtypeProfiles)
                {
                    RobotAppearanceProfileDefinition effectiveSubtype = RobotAppearanceProjectAdapter.ResolveProfile(roleProfile, subtypeKey);
                    profiles[CompositeKey(roleKey, subtypeKey)] = ParseProfile(effectiveSubtype, profiles[roleKey]);
                }
            }
        }
        catch
        {
            // Keep defaults when appearance parsing fails.
        }

        return new AppearanceProfileCatalog(profiles);
    }

    private static RobotAppearanceProfile ParseProfile(RobotAppearanceProfileDefinition source, RobotAppearanceProfile defaults)
    {
        source.EnsureInitialized();
        float bodyLength = Math.Max(0.12f, (float)source.BodyLengthM);
        float bodyWidth = Math.Max(0.10f, (float)source.BodyWidthM);
        float bodyHeight = Math.Max(0.08f, (float)source.BodyHeightM);
        float bodyClearance = Math.Max(0f, (float)source.BodyClearanceM);
        IReadOnlyList<Vector2> wheelOffsets = source.GetWheelOffsetsOrDefaults()
            .Select(offset => new Vector2((float)offset.X, (float)offset.Y))
            .ToArray();

        return defaults with
        {
            RoleKey = string.IsNullOrWhiteSpace(source.RoleKey) ? defaults.RoleKey : source.RoleKey,
            ChassisSubtype = source.ChassisSubtype ?? string.Empty,
            BodyShape = string.IsNullOrWhiteSpace(source.BodyShape) ? defaults.BodyShape : source.BodyShape,
            WheelStyle = string.IsNullOrWhiteSpace(source.WheelStyle) ? defaults.WheelStyle : source.WheelStyle,
            SuspensionStyle = string.IsNullOrWhiteSpace(source.SuspensionStyle) ? defaults.SuspensionStyle : source.SuspensionStyle,
            ArmStyle = string.IsNullOrWhiteSpace(source.ArmStyle) ? defaults.ArmStyle : source.ArmStyle,
            FrontClimbAssistStyle = string.IsNullOrWhiteSpace(source.FrontClimbAssistStyle) ? defaults.FrontClimbAssistStyle : source.FrontClimbAssistStyle,
            RearClimbAssistStyle = string.IsNullOrWhiteSpace(source.RearClimbAssistStyle) ? defaults.RearClimbAssistStyle : source.RearClimbAssistStyle,
            RearClimbAssistKneeDirection = string.IsNullOrWhiteSpace(source.RearClimbAssistKneeDirection) ? defaults.RearClimbAssistKneeDirection : source.RearClimbAssistKneeDirection,
            ChassisSupportsJump = source.ChassisSupportsJump,
            BodyLengthM = bodyLength,
            BodyWidthM = bodyWidth,
            BodyHeightM = bodyHeight,
            BodyClearanceM = bodyClearance,
            BodyRenderWidthScale = Math.Clamp((float)(source.BodyRenderWidthScale <= 0 ? defaults.BodyRenderWidthScale : source.BodyRenderWidthScale), 0.4f, 1.35f),
            StructureBaseLiftM = (float)Math.Clamp(source.StructureBaseLiftM, 0.0, 1.20),
            StructureGroundClearanceM = Math.Max(0f, (float)source.StructureGroundClearanceM),
            StructureBaseHeightM = Math.Max(0.05f, (float)(source.StructureBaseHeightM <= 0 ? defaults.StructureBaseHeightM : source.StructureBaseHeightM)),
            StructureBaseLengthM = Math.Max(0.40f, (float)(source.StructureBaseLengthM <= 0 ? defaults.StructureBaseLengthM : source.StructureBaseLengthM)),
            StructureBaseWidthM = Math.Max(0.40f, (float)(source.StructureBaseWidthM <= 0 ? defaults.StructureBaseWidthM : source.StructureBaseWidthM)),
            StructureBaseTopLengthM = Math.Max(0.20f, (float)(source.StructureBaseTopLengthM <= 0 ? defaults.StructureBaseTopLengthM : source.StructureBaseTopLengthM)),
            StructureBaseTopWidthM = Math.Max(0.20f, (float)(source.StructureBaseTopWidthM <= 0 ? defaults.StructureBaseTopWidthM : source.StructureBaseTopWidthM)),
            StructureBaseTopHeightM = Math.Max(0.02f, (float)(source.StructureBaseTopHeightM <= 0 ? defaults.StructureBaseTopHeightM : source.StructureBaseTopHeightM)),
            StructureFrameWidthM = Math.Max(0.20f, (float)(source.StructureFrameWidthM <= 0 ? defaults.StructureFrameWidthM : source.StructureFrameWidthM)),
            StructureFrameDepthM = Math.Max(0.04f, (float)(source.StructureFrameDepthM <= 0 ? defaults.StructureFrameDepthM : source.StructureFrameDepthM)),
            StructureFrameHeightM = Math.Max(0.20f, (float)(source.StructureFrameHeightM <= 0 ? defaults.StructureFrameHeightM : source.StructureFrameHeightM)),
            StructureColumnSpanM = Math.Max(0.10f, (float)(source.StructureColumnSpanM <= 0 ? defaults.StructureColumnSpanM : source.StructureColumnSpanM)),
            StructureSupportOffsetM = Math.Max(0.05f, (float)(source.StructureSupportOffsetM <= 0 ? defaults.StructureSupportOffsetM : source.StructureSupportOffsetM)),
            StructureFrameColumnWidthM = Math.Max(0.02f, (float)(source.StructureFrameColumnWidthM <= 0 ? defaults.StructureFrameColumnWidthM : source.StructureFrameColumnWidthM)),
            StructureFrameBeamHeightM = Math.Max(0.02f, (float)(source.StructureFrameBeamHeightM <= 0 ? defaults.StructureFrameBeamHeightM : source.StructureFrameBeamHeightM)),
            StructureRotorCenterHeightM = Math.Max(0.05f, (float)(source.StructureRotorCenterHeightM <= 0 ? defaults.StructureRotorCenterHeightM : source.StructureRotorCenterHeightM)),
            StructureRotorPhaseDeg = (float)(Math.Abs(source.StructureRotorPhaseDeg) <= double.Epsilon ? defaults.StructureRotorPhaseDeg : source.StructureRotorPhaseDeg),
            StructureRotorRadiusM = Math.Max(0.10f, (float)(source.StructureRotorRadiusM <= 0 ? defaults.StructureRotorRadiusM : source.StructureRotorRadiusM)),
            StructureRotorHubRadiusM = Math.Max(0.02f, (float)(source.StructureRotorHubRadiusM <= 0 ? defaults.StructureRotorHubRadiusM : source.StructureRotorHubRadiusM)),
            StructureRotorArmLengthM = Math.Max(0.02f, (float)(source.StructureRotorArmLengthM <= 0 ? defaults.StructureRotorArmLengthM : source.StructureRotorArmLengthM)),
            StructureRotorArmWidthM = Math.Max(0.01f, (float)(source.StructureRotorArmWidthM <= 0 ? defaults.StructureRotorArmWidthM : source.StructureRotorArmWidthM)),
            StructureRotorArmHeightM = Math.Max(0.01f, (float)(source.StructureRotorArmHeightM <= 0 ? defaults.StructureRotorArmHeightM : source.StructureRotorArmHeightM)),
            StructureLampLengthM = Math.Max(0.04f, (float)(source.StructureLampLengthM <= 0 ? defaults.StructureLampLengthM : source.StructureLampLengthM)),
            StructureLampWidthM = Math.Max(0.04f, (float)(source.StructureLampWidthM <= 0 ? defaults.StructureLampWidthM : source.StructureLampWidthM)),
            StructureLampHeightM = Math.Max(0.01f, (float)(source.StructureLampHeightM <= 0 ? defaults.StructureLampHeightM : source.StructureLampHeightM)),
            StructureHangerWidthM = Math.Max(0.04f, (float)(source.StructureHangerWidthM <= 0 ? defaults.StructureHangerWidthM : source.StructureHangerWidthM)),
            StructureHangerHeightM = Math.Max(0.04f, (float)(source.StructureHangerHeightM <= 0 ? defaults.StructureHangerHeightM : source.StructureHangerHeightM)),
            StructureHangerDepthM = Math.Max(0.01f, (float)(source.StructureHangerDepthM <= 0 ? defaults.StructureHangerDepthM : source.StructureHangerDepthM)),
            StructureHangerCenterHeightM = Math.Max(0.04f, (float)(source.StructureHangerCenterHeightM <= 0 ? defaults.StructureHangerCenterHeightM : source.StructureHangerCenterHeightM)),
            StructureCantileverPairGapM = Math.Max(0.10f, (float)(source.StructureCantileverPairGapM <= 0 ? defaults.StructureCantileverPairGapM : source.StructureCantileverPairGapM)),
            StructureCantileverLengthM = Math.Max(0.04f, (float)(source.StructureCantileverLengthM <= 0 ? defaults.StructureCantileverLengthM : source.StructureCantileverLengthM)),
            StructureCantileverOffsetYM = (float)source.StructureCantileverOffsetYM,
            StructureCantileverHeightM = Math.Max(0.01f, (float)(source.StructureCantileverHeightM <= 0 ? defaults.StructureCantileverHeightM : source.StructureCantileverHeightM)),
            StructureCantileverDepthM = Math.Max(0.01f, (float)(source.StructureCantileverDepthM <= 0 ? defaults.StructureCantileverDepthM : source.StructureCantileverDepthM)),
            WheelRadiusM = Math.Clamp((float)(source.WheelRadiusM <= 0 ? defaults.WheelRadiusM : source.WheelRadiusM), 0.03f, 0.28f),
            RearLegWheelRadiusM = Math.Clamp((float)(source.RearLegWheelRadiusM <= 0 ? defaults.RearLegWheelRadiusM : source.RearLegWheelRadiusM), 0.03f, 0.32f),
            WheelOffsetsM = wheelOffsets.Count > 0 ? wheelOffsets : defaults.WheelOffsetsM,
            ArmorOrbitYawsDeg = source.ArmorOrbitYawsDeg.Count > 0 ? source.ArmorOrbitYawsDeg.Select(value => (float)value).ToArray() : defaults.ArmorOrbitYawsDeg,
            ArmorSelfYawsDeg = source.ArmorSelfYawsDeg.Count > 0 ? source.ArmorSelfYawsDeg.Select(value => (float)value).ToArray() : defaults.ArmorSelfYawsDeg,
            GimbalLengthM = Math.Max(0f, (float)source.GimbalLengthM),
            GimbalWidthM = Math.Max(0f, (float)source.GimbalWidthM),
            GimbalBodyHeightM = Math.Max(0f, (float)source.GimbalBodyHeightM),
            GimbalHeightM = Math.Max(0f, (float)source.GimbalHeightM),
            GimbalOffsetXM = (float)source.GimbalOffsetXM,
            GimbalOffsetYM = (float)source.GimbalOffsetYM,
            GimbalMountGapM = Math.Max(0f, (float)source.GimbalMountGapM),
            GimbalMountLengthM = Math.Max(0f, (float)source.GimbalMountLengthM),
            GimbalMountWidthM = Math.Max(0f, (float)source.GimbalMountWidthM),
            GimbalMountHeightM = Math.Max(0f, (float)source.GimbalMountHeightM),
            BarrelLengthM = Math.Max(0f, (float)source.BarrelLengthM),
            BarrelRadiusM = Math.Max(0f, (float)source.BarrelRadiusM),
            ArmorPlateWidthM = Math.Max(0.03f, (float)(source.ArmorPlateWidthM <= 0 ? defaults.ArmorPlateWidthM : source.ArmorPlateWidthM)),
            ArmorPlateLengthM = Math.Max(0.03f, (float)(source.ArmorPlateLengthM <= 0 ? defaults.ArmorPlateLengthM : source.ArmorPlateLengthM)),
            ArmorPlateHeightM = Math.Max(0.03f, (float)(source.ArmorPlateHeightM <= 0 ? defaults.ArmorPlateHeightM : source.ArmorPlateHeightM)),
            ArmorPlateGapM = Math.Max(0.003f, (float)(source.ArmorPlateGapM <= 0 ? defaults.ArmorPlateGapM : source.ArmorPlateGapM)),
            ArmorLightLengthM = Math.Max(0.004f, (float)(source.ArmorLightLengthM <= 0 ? defaults.ArmorLightLengthM : source.ArmorLightLengthM)),
            ArmorLightWidthM = Math.Max(0.003f, (float)(source.ArmorLightWidthM <= 0 ? defaults.ArmorLightWidthM : source.ArmorLightWidthM)),
            ArmorLightHeightM = Math.Max(0.004f, (float)(source.ArmorLightHeightM <= 0 ? defaults.ArmorLightHeightM : source.ArmorLightHeightM)),
            BarrelLightLengthM = Math.Max(0.004f, (float)(source.BarrelLightLengthM <= 0 ? defaults.BarrelLightLengthM : source.BarrelLightLengthM)),
            BarrelLightWidthM = Math.Max(0.003f, (float)(source.BarrelLightWidthM <= 0 ? defaults.BarrelLightWidthM : source.BarrelLightWidthM)),
            BarrelLightHeightM = Math.Max(0.003f, (float)(source.BarrelLightHeightM <= 0 ? defaults.BarrelLightHeightM : source.BarrelLightHeightM)),
            FrontClimbAssistTopLengthM = Math.Max(0.01f, (float)(source.FrontClimbAssistTopLengthM <= 0 ? defaults.FrontClimbAssistTopLengthM : source.FrontClimbAssistTopLengthM)),
            FrontClimbAssistBottomLengthM = Math.Max(0.01f, (float)(source.FrontClimbAssistBottomLengthM <= 0 ? defaults.FrontClimbAssistBottomLengthM : source.FrontClimbAssistBottomLengthM)),
            FrontClimbAssistPlateWidthM = Math.Max(0.008f, (float)(source.FrontClimbAssistPlateWidthM <= 0 ? defaults.FrontClimbAssistPlateWidthM : source.FrontClimbAssistPlateWidthM)),
            FrontClimbAssistPlateHeightM = Math.Max(0.03f, (float)(source.FrontClimbAssistPlateHeightM <= 0 ? defaults.FrontClimbAssistPlateHeightM : source.FrontClimbAssistPlateHeightM)),
            FrontClimbAssistForwardOffsetM = (float)source.FrontClimbAssistForwardOffsetM,
            FrontClimbAssistInnerOffsetM = (float)source.FrontClimbAssistInnerOffsetM,
            RearClimbAssistUpperLengthM = Math.Max(0.03f, (float)(source.RearClimbAssistUpperLengthM <= 0 ? defaults.RearClimbAssistUpperLengthM : source.RearClimbAssistUpperLengthM)),
            RearClimbAssistLowerLengthM = Math.Max(0.03f, (float)(source.RearClimbAssistLowerLengthM <= 0 ? defaults.RearClimbAssistLowerLengthM : source.RearClimbAssistLowerLengthM)),
            RearClimbAssistUpperWidthM = Math.Max(0.008f, (float)(source.RearClimbAssistUpperWidthM <= 0 ? defaults.RearClimbAssistUpperWidthM : source.RearClimbAssistUpperWidthM)),
            RearClimbAssistUpperHeightM = Math.Max(0.008f, (float)(source.RearClimbAssistUpperHeightM <= 0 ? defaults.RearClimbAssistUpperHeightM : source.RearClimbAssistUpperHeightM)),
            RearClimbAssistLowerWidthM = Math.Max(0.008f, (float)(source.RearClimbAssistLowerWidthM <= 0 ? defaults.RearClimbAssistLowerWidthM : source.RearClimbAssistLowerWidthM)),
            RearClimbAssistLowerHeightM = Math.Max(0.008f, (float)(source.RearClimbAssistLowerHeightM <= 0 ? defaults.RearClimbAssistLowerHeightM : source.RearClimbAssistLowerHeightM)),
            RearClimbAssistMountOffsetXM = (float)source.RearClimbAssistMountOffsetXM,
            RearClimbAssistMountHeightM = Math.Max(0.01f, (float)(source.RearClimbAssistMountHeightM <= 0 ? defaults.RearClimbAssistMountHeightM : source.RearClimbAssistMountHeightM)),
            RearClimbAssistInnerOffsetM = (float)source.RearClimbAssistInnerOffsetM,
            RearClimbAssistUpperPairGapM = Math.Max(0.01f, (float)(source.RearClimbAssistUpperPairGapM <= 0 ? defaults.RearClimbAssistUpperPairGapM : source.RearClimbAssistUpperPairGapM)),
            RearClimbAssistHingeRadiusM = Math.Max(0.004f, (float)(source.RearClimbAssistHingeRadiusM <= 0 ? defaults.RearClimbAssistHingeRadiusM : source.RearClimbAssistHingeRadiusM)),
            RearClimbAssistKneeMinDeg = (float)(Math.Abs(source.RearClimbAssistKneeMinDeg) <= double.Epsilon ? defaults.RearClimbAssistKneeMinDeg : source.RearClimbAssistKneeMinDeg),
            RearClimbAssistKneeMaxDeg = (float)(Math.Abs(source.RearClimbAssistKneeMaxDeg) <= double.Epsilon ? defaults.RearClimbAssistKneeMaxDeg : source.RearClimbAssistKneeMaxDeg),
            StructureBodyTopHeightM = Math.Max(0.05f, (float)(source.StructureBodyTopHeightM <= 0 ? defaults.StructureBodyTopHeightM : source.StructureBodyTopHeightM)),
            StructureHeadBaseHeightM = Math.Max(0.05f, (float)(source.StructureHeadBaseHeightM <= 0 ? defaults.StructureHeadBaseHeightM : source.StructureHeadBaseHeightM)),
            StructureLowerShoulderHeightM = Math.Max(0.05f, (float)(source.StructureLowerShoulderHeightM <= 0 ? defaults.StructureLowerShoulderHeightM : source.StructureLowerShoulderHeightM)),
            StructureUpperShoulderHeightM = Math.Max(0.05f, (float)(source.StructureUpperShoulderHeightM <= 0 ? defaults.StructureUpperShoulderHeightM : source.StructureUpperShoulderHeightM)),
            StructureTowerRadiusM = Math.Max(0.05f, (float)(source.StructureTowerRadiusM <= 0 ? defaults.StructureTowerRadiusM : source.StructureTowerRadiusM)),
            StructureRoofHeightM = Math.Max(0.05f, (float)(source.StructureRoofHeightM <= 0 ? defaults.StructureRoofHeightM : source.StructureRoofHeightM)),
            StructureTopArmorCenterHeightM = Math.Max(0.05f, (float)(source.StructureTopArmorCenterHeightM <= 0 ? defaults.StructureTopArmorCenterHeightM : source.StructureTopArmorCenterHeightM)),
            StructureTopArmorTiltDeg = (float)(Math.Abs(source.StructureTopArmorTiltDeg) <= double.Epsilon ? defaults.StructureTopArmorTiltDeg : source.StructureTopArmorTiltDeg),
            StructureDetectorWidthM = Math.Max(0.02f, (float)(source.StructureDetectorWidthM <= 0 ? defaults.StructureDetectorWidthM : source.StructureDetectorWidthM)),
            StructureDetectorHeightM = Math.Max(0.02f, (float)(source.StructureDetectorHeightM <= 0 ? defaults.StructureDetectorHeightM : source.StructureDetectorHeightM)),
            StructureDetectorBridgeCenterHeightM = Math.Max(0.02f, (float)(source.StructureDetectorBridgeCenterHeightM <= 0 ? defaults.StructureDetectorBridgeCenterHeightM : source.StructureDetectorBridgeCenterHeightM)),
            StructureDetectorSensorCenterHeightM = Math.Max(0.02f, (float)(source.StructureDetectorSensorCenterHeightM <= 0 ? defaults.StructureDetectorSensorCenterHeightM : source.StructureDetectorSensorCenterHeightM)),
            StructureCoreColumnHeightM = Math.Max(0.02f, (float)(source.StructureCoreColumnHeightM <= 0 ? defaults.StructureCoreColumnHeightM : source.StructureCoreColumnHeightM)),
            StructureShoulderHeightM = Math.Max(0.05f, (float)(source.StructureShoulderHeightM <= 0 ? defaults.StructureShoulderHeightM : source.StructureShoulderHeightM)),
            ChassisSpeedScale = Math.Max(0.2f, (float)(source.ChassisSpeedScale <= 0 ? defaults.ChassisSpeedScale : source.ChassisSpeedScale)),
            ChassisDrivePowerLimitW = Math.Max(10f, (float)(source.ChassisDrivePowerLimitW <= 0 ? defaults.ChassisDrivePowerLimitW : source.ChassisDrivePowerLimitW)),
            ChassisDriveIdleDrawW = Math.Max(0f, (float)(source.ChassisDriveIdleDrawW <= 0 ? defaults.ChassisDriveIdleDrawW : source.ChassisDriveIdleDrawW)),
            ChassisDriveRpmCoeff = Math.Max(0f, (float)(source.ChassisDriveRpmCoeff <= 0 ? defaults.ChassisDriveRpmCoeff : source.ChassisDriveRpmCoeff)),
            ChassisDriveAccelCoeff = Math.Max(0f, (float)(source.ChassisDriveAccelCoeff <= 0 ? defaults.ChassisDriveAccelCoeff : source.ChassisDriveAccelCoeff)),
            BodyColor = source.BodyColor,
            TurretColor = source.TurretColor,
            ArmorColor = source.ArmorColor,
            WheelColor = source.WheelColor,
        };
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
            StructureGroundClearanceM = Math.Max(0f, ReadFloat(primary, fallback, defaults.StructureGroundClearanceM, "structure_ground_clearance_m")),
            StructureBaseHeightM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureBaseHeightM, "structure_base_height_m")),
            StructureBaseLengthM = Math.Max(0.40f, ReadFloat(primary, fallback, defaults.StructureBaseLengthM, "structure_base_length_m")),
            StructureBaseWidthM = Math.Max(0.40f, ReadFloat(primary, fallback, defaults.StructureBaseWidthM, "structure_base_width_m")),
            StructureBaseTopLengthM = Math.Max(0.20f, ReadFloat(primary, fallback, defaults.StructureBaseTopLengthM, "structure_base_top_length_m")),
            StructureBaseTopWidthM = Math.Max(0.20f, ReadFloat(primary, fallback, defaults.StructureBaseTopWidthM, "structure_base_top_width_m")),
            StructureBaseTopHeightM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureBaseTopHeightM, "structure_base_top_height_m")),
            StructureFrameWidthM = Math.Max(0.20f, ReadFloat(primary, fallback, defaults.StructureFrameWidthM, "structure_frame_width_m")),
            StructureFrameDepthM = Math.Max(0.04f, ReadFloat(primary, fallback, defaults.StructureFrameDepthM, "structure_frame_depth_m")),
            StructureFrameHeightM = Math.Max(0.20f, ReadFloat(primary, fallback, defaults.StructureFrameHeightM, "structure_frame_height_m")),
            StructureColumnSpanM = Math.Max(0.10f, ReadFloat(primary, fallback, defaults.StructureColumnSpanM, "structure_column_span_m")),
            StructureSupportOffsetM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureSupportOffsetM, "structure_support_offset_m")),
            StructureFrameColumnWidthM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureFrameColumnWidthM, "structure_frame_column_width_m")),
            StructureFrameBeamHeightM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureFrameBeamHeightM, "structure_frame_beam_height_m")),
            StructureRotorCenterHeightM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureRotorCenterHeightM, "structure_rotor_center_height_m")),
            StructureRotorPhaseDeg = ReadFloat(primary, fallback, defaults.StructureRotorPhaseDeg, "structure_rotor_phase_deg"),
            StructureRotorRadiusM = Math.Max(0.10f, ReadFloat(primary, fallback, defaults.StructureRotorRadiusM, "structure_rotor_radius_m")),
            StructureRotorHubRadiusM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureRotorHubRadiusM, "structure_rotor_hub_radius_m")),
            StructureRotorArmLengthM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureRotorArmLengthM, "structure_rotor_arm_length_m")),
            StructureRotorArmWidthM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.StructureRotorArmWidthM, "structure_rotor_arm_width_m")),
            StructureRotorArmHeightM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.StructureRotorArmHeightM, "structure_rotor_arm_height_m")),
            StructureLampLengthM = Math.Max(0.04f, ReadFloat(primary, fallback, defaults.StructureLampLengthM, "structure_lamp_length_m")),
            StructureLampWidthM = Math.Max(0.04f, ReadFloat(primary, fallback, defaults.StructureLampWidthM, "structure_lamp_width_m")),
            StructureLampHeightM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.StructureLampHeightM, "structure_lamp_height_m")),
            StructureHangerWidthM = Math.Max(0.04f, ReadFloat(primary, fallback, defaults.StructureHangerWidthM, "structure_hanger_width_m")),
            StructureHangerHeightM = Math.Max(0.04f, ReadFloat(primary, fallback, defaults.StructureHangerHeightM, "structure_hanger_height_m")),
            StructureHangerDepthM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.StructureHangerDepthM, "structure_hanger_depth_m")),
            StructureHangerCenterHeightM = Math.Max(0.04f, ReadFloat(primary, fallback, defaults.StructureHangerCenterHeightM, "structure_hanger_center_height_m")),
            StructureCantileverPairGapM = Math.Max(0.10f, ReadFloat(primary, fallback, defaults.StructureCantileverPairGapM, "structure_cantilever_pair_gap_m")),
            StructureCantileverLengthM = Math.Max(0.04f, ReadFloat(primary, fallback, defaults.StructureCantileverLengthM, "structure_cantilever_length_m")),
            StructureCantileverOffsetYM = ReadFloat(primary, fallback, defaults.StructureCantileverOffsetYM, "structure_cantilever_offset_y_m"),
            StructureCantileverHeightM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.StructureCantileverHeightM, "structure_cantilever_height_m")),
            StructureCantileverDepthM = Math.Max(0.01f, ReadFloat(primary, fallback, defaults.StructureCantileverDepthM, "structure_cantilever_depth_m")),
            WheelRadiusM = Math.Clamp(ReadFloat(primary, fallback, defaults.WheelRadiusM, "wheel_radius_m"), 0.03f, 0.28f),
            RearLegWheelRadiusM = Math.Clamp(ReadFloat(primary, fallback, defaults.RearLegWheelRadiusM, "rear_leg_wheel_radius_m"), 0.03f, 0.32f),
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
            StructureBodyTopHeightM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureBodyTopHeightM, "structure_body_top_height_m")),
            StructureHeadBaseHeightM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureHeadBaseHeightM, "structure_head_base_height_m")),
            StructureLowerShoulderHeightM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureLowerShoulderHeightM, "structure_lower_shoulder_height_m")),
            StructureUpperShoulderHeightM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureUpperShoulderHeightM, "structure_upper_shoulder_height_m")),
            StructureTowerRadiusM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureTowerRadiusM, "structure_tower_radius_m")),
            StructureRoofHeightM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureRoofHeightM, "structure_roof_height_m")),
            StructureTopArmorCenterHeightM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureTopArmorCenterHeightM, "structure_top_armor_center_height_m")),
            StructureTopArmorTiltDeg = ReadFloat(primary, fallback, defaults.StructureTopArmorTiltDeg, "structure_top_armor_tilt_deg"),
            StructureDetectorWidthM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureDetectorWidthM, "structure_detector_width_m")),
            StructureDetectorHeightM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureDetectorHeightM, "structure_detector_height_m")),
            StructureDetectorBridgeCenterHeightM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureDetectorBridgeCenterHeightM, "structure_detector_bridge_center_height_m")),
            StructureDetectorSensorCenterHeightM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureDetectorSensorCenterHeightM, "structure_detector_sensor_center_height_m")),
            StructureCoreColumnHeightM = Math.Max(0.02f, ReadFloat(primary, fallback, defaults.StructureCoreColumnHeightM, "structure_core_column_height_m")),
            StructureShoulderHeightM = Math.Max(0.05f, ReadFloat(primary, fallback, defaults.StructureShoulderHeightM, "structure_shoulder_height_m")),
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

    public float StructureGroundClearanceM { get; init; }

    public float StructureBaseHeightM { get; init; } = 0.30f;

    public float StructureBaseLengthM { get; init; } = 3.40f;

    public float StructureBaseWidthM { get; init; } = 3.18f;

    public float StructureBaseTopLengthM { get; init; } = 2.10f;

    public float StructureBaseTopWidthM { get; init; } = 1.08f;

    public float StructureBaseTopHeightM { get; init; } = 0.12f;

    public float StructureFrameWidthM { get; init; } = 2.06f;

    public float StructureFrameDepthM { get; init; } = 0.16f;

    public float StructureFrameHeightM { get; init; } = 2.30f;

    public float StructureColumnSpanM { get; init; } = 2.06f;

    public float StructureSupportOffsetM { get; init; } = 1.03f;

    public float StructureFrameColumnWidthM { get; init; } = 0.10f;

    public float StructureFrameBeamHeightM { get; init; } = 0.09f;

    public float StructureRotorCenterHeightM { get; init; } = 1.45f;

    public float StructureRotorPhaseDeg { get; init; } = 90f;

    public float StructureRotorRadiusM { get; init; } = 1.40f;

    public float StructureRotorHubRadiusM { get; init; } = 0.09f;

    public float StructureRotorArmLengthM { get; init; } = 1.12f;

    public float StructureRotorArmWidthM { get; init; } = 0.06f;

    public float StructureRotorArmHeightM { get; init; } = 0.04f;

    public float StructureLampLengthM { get; init; } = 0.30f;

    public float StructureLampWidthM { get; init; } = 0.30f;

    public float StructureLampHeightM { get; init; } = 0.08f;

    public float StructureHangerWidthM { get; init; } = 0.24f;

    public float StructureHangerHeightM { get; init; } = 0.24f;

    public float StructureHangerDepthM { get; init; } = 0.06f;

    public float StructureHangerCenterHeightM { get; init; } = 1.45f;

    public float StructureCantileverPairGapM { get; init; } = 2.34f;

    public float StructureCantileverLengthM { get; init; } = 0.28f;

    public float StructureCantileverOffsetYM { get; init; } = -0.02f;

    public float StructureCantileverHeightM { get; init; } = 0.08f;

    public float StructureCantileverDepthM { get; init; } = 0.08f;

    public float WheelRadiusM { get; init; } = 0.08f;

    public float RearLegWheelRadiusM { get; init; } = 0.08f;

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

    public float StructureBodyTopHeightM { get; init; } = 1.216f;

    public float StructureHeadBaseHeightM { get; init; } = 1.318f;

    public float StructureLowerShoulderHeightM { get; init; } = 0.571f;

    public float StructureUpperShoulderHeightM { get; init; } = 1.446f;

    public float StructureTowerRadiusM { get; init; } = 0.20f;

    public float StructureRoofHeightM { get; init; } = 1.03f;

    public float StructureTopArmorCenterHeightM { get; init; } = 1.15f;

    public float StructureTopArmorTiltDeg { get; init; } = 27.5f;

    public float StructureDetectorWidthM { get; init; } = 0.98f;

    public float StructureDetectorHeightM { get; init; } = 0.095f;

    public float StructureDetectorBridgeCenterHeightM { get; init; } = 1.093f;

    public float StructureDetectorSensorCenterHeightM { get; init; } = 1.136f;

    public float StructureCoreColumnHeightM { get; init; } = 0.783f;

    public float StructureShoulderHeightM { get; init; } = 0.860f;

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

        double halfLengthM = Math.Max(0.08, BodyLengthM * 0.5);
        double halfWidthM = Math.Max(0.08, BodyWidthM * BodyRenderWidthScale * 0.5);
        double collisionRadiusM = Math.Max(0.14, Math.Sqrt(halfLengthM * halfLengthM + halfWidthM * halfWidthM) + 0.015);
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
            || roleKey.Equals("base", StringComparison.OrdinalIgnoreCase)
            || roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase))
        {
            return new RobotAppearanceProfile
            {
                RoleKey = roleKey,
                BodyShape = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? "box" : "octagon",
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
                StructureGroundClearanceM = 0.0f,
                StructureBaseHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.30f : 0.0f,
                StructureBaseLengthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 3.40f : bodyLength,
                StructureBaseWidthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 3.18f : bodyWidth,
                StructureBaseTopLengthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 2.10f : bodyLength * 0.62f,
                StructureBaseTopWidthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 1.08f : bodyWidth * 0.34f,
                StructureBaseTopHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.12f : 0.0f,
                StructureFrameWidthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 2.06f : bodyLength,
                StructureFrameDepthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.16f : bodyWidth * 0.18f,
                StructureFrameHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 2.30f : bodyHeight,
                StructureColumnSpanM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 2.06f : bodyLength,
                StructureSupportOffsetM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 1.03f : bodyWidth * 0.5f,
                StructureFrameColumnWidthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.10f : 0.0f,
                StructureFrameBeamHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.09f : 0.0f,
                StructureRotorCenterHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 1.45f : 0.0f,
                StructureRotorPhaseDeg = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 90f : 0.0f,
                StructureRotorRadiusM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 1.40f : 0.0f,
                StructureRotorHubRadiusM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.09f : 0.0f,
                StructureRotorArmLengthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 1.12f : 0.0f,
                StructureRotorArmWidthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.06f : 0.0f,
                StructureRotorArmHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.04f : 0.0f,
                StructureLampLengthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.30f : 0.0f,
                StructureLampWidthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.30f : 0.0f,
                StructureLampHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.08f : 0.0f,
                StructureHangerWidthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.24f : 0.0f,
                StructureHangerHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.24f : 0.0f,
                StructureHangerDepthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.06f : 0.0f,
                StructureHangerCenterHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 1.45f : 0.0f,
                StructureCantileverPairGapM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 2.34f : 0.0f,
                StructureCantileverLengthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.28f : 0.0f,
                StructureCantileverOffsetYM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? -0.02f : 0.0f,
                StructureCantileverHeightM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.08f : 0.0f,
                StructureCantileverDepthM = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase) ? 0.08f : 0.0f,
                WheelRadiusM = 0.03f,
                RearLegWheelRadiusM = 0.03f,
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
                StructureBodyTopHeightM = roleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase) ? 1.216f : bodyHeight,
                StructureHeadBaseHeightM = roleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase) ? 1.318f : 0f,
                StructureLowerShoulderHeightM = roleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase) ? 0.571f : 0f,
                StructureUpperShoulderHeightM = roleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase) ? 1.446f : 0f,
                StructureTowerRadiusM = roleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase) ? 0.20f : 0f,
                StructureRoofHeightM = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase) ? 1.03f : bodyHeight,
                StructureTopArmorCenterHeightM = roleKey.Equals("outpost", StringComparison.OrdinalIgnoreCase) ? 1.633f : 1.15f,
                StructureTopArmorTiltDeg = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase) ? 27.5f : 45f,
                StructureDetectorWidthM = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase) ? 0.98f : 0f,
                StructureDetectorHeightM = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase) ? 0.095f : 0f,
                StructureDetectorBridgeCenterHeightM = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase) ? 1.093f : 0f,
                StructureDetectorSensorCenterHeightM = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase) ? 1.136f : 0f,
                StructureCoreColumnHeightM = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase) ? 0.783f : 0f,
                StructureShoulderHeightM = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase) ? 0.860f : 0f,
                BodyColor = roleKey.Equals("base", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(142, 148, 154)
                    : roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase)
                        ? Color.FromArgb(124, 128, 134)
                        : Color.FromArgb(156, 160, 166),
                TurretColor = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(170, 174, 180)
                    : Color.FromArgb(196, 200, 206),
                ArmorColor = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(68, 72, 78)
                    : Color.FromArgb(206, 212, 218),
                WheelColor = roleKey.Equals("energy_mechanism", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(64, 132, 255)
                    : Color.FromArgb(62, 68, 78),
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
            RearLegWheelRadiusM = roleKey switch
            {
                "hero" => 0.10f,
                "infantry" => 0.07f,
                _ => 0.08f,
            },
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
