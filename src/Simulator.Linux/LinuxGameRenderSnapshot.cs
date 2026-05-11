using Simulator.Core.Map;
using Simulator.Assets;
using Simulator.Platform.Rendering;

namespace Simulator.Linux;

internal sealed record LinuxGameRenderSnapshot(
    string MapName,
    double WorldWidth,
    double WorldHeight,
    double MetersPerModelUnit,
    double GameTimeSec,
    string LocalEntityId,
    IReadOnlyList<LinuxFacilityRenderItem> Facilities,
    IReadOnlyList<LinuxEntityRenderItem> Entities,
    IReadOnlyList<LinuxProjectileRenderItem> Projectiles,
    IReadOnlyList<InteractionSceneRenderItem> Interactions,
    IReadOnlyList<LinuxSceneInteractionRenderItem> SceneInteractions)
{
    public static LinuxGameRenderSnapshot Empty { get; } = new(
        "unknown",
        1,
        1,
        1,
        0,
        string.Empty,
        Array.Empty<LinuxFacilityRenderItem>(),
        Array.Empty<LinuxEntityRenderItem>(),
        Array.Empty<LinuxProjectileRenderItem>(),
        Array.Empty<InteractionSceneRenderItem>(),
        Array.Empty<LinuxSceneInteractionRenderItem>());
}

internal sealed record LinuxFacilityRenderItem(
    string Id,
    string Type,
    string Team,
    string Shape,
    double X1,
    double Y1,
    double X2,
    double Y2,
    double HeightM,
    IReadOnlyList<Point2D> Points);

internal sealed record LinuxEntityRenderItem(
    string Id,
    string Team,
    string EntityType,
    string RoleKey,
    double X,
    double Y,
    double AngleDeg,
    double TurretYawDeg,
    double GimbalPitchDeg,
    double ChassisPitchDeg,
    double ChassisRollDeg,
    double SceneX,
    double SceneY,
    double SceneZ,
    double SceneForwardX,
    double SceneForwardY,
    double SceneForwardZ,
    double SceneTurretForwardX,
    double SceneTurretForwardY,
    double SceneTurretForwardZ,
    double Health,
    double MaxHealth,
    double BodyWidthM,
    double BodyLengthM,
    double BodyHeightM,
    double BodyClearanceM,
    double BodyRenderWidthScale,
    double WheelRadiusM,
    double GimbalLengthM,
    double GimbalWidthM,
    double GimbalBodyHeightM,
    double GimbalHeightM,
    double GimbalMountGapM,
    double GimbalMountLengthM,
    double GimbalMountWidthM,
    double GimbalMountHeightM,
    double BarrelLengthM,
    double BarrelRadiusM,
    IReadOnlyList<(double X, double Y)> WheelOffsetsM,
    RobotAppearanceProfileDefinition? AppearanceProfile,
    bool IsAlive,
    bool IsPlayerControlled,
    bool IsSelected);

internal sealed record LinuxProjectileRenderItem(
    string Id,
    string Team,
    string AmmoType,
    double X,
    double Y,
    double HeightM);

internal sealed record LinuxSceneInteractionRenderItem(
    string Id,
    string Kind,
    string Type,
    string Team,
    double SceneX,
    double SceneY,
    double SceneZ,
    double SceneYawDeg,
    double SizeXModel,
    double SizeYModel,
    double SizeZModel,
    double RadiusModel,
    int LitMask,
    int ActivatedMask,
    int ActivatedCount,
    bool LargeEnergy,
    bool Stopped,
    double Progress,
    IReadOnlyList<(double X, double Y, double Z)> ScenePoints);
