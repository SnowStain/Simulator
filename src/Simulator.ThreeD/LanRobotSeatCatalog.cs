namespace Simulator.ThreeD;

internal static class LanRobotSeatCatalog
{
    private static readonly string[] ControllableKeys =
    [
        "robot_1",
        "robot_2",
        "robot_3",
        "robot_4",
        "robot_7",
    ];

    private static readonly string[] RoomSeatKeys =
    [
        "robot_1",
        "robot_2",
        "robot_3",
        "robot_4",
        "robot_6",
        "robot_7",
    ];

    public static IReadOnlyList<string> ControllableEntityKeys => ControllableKeys;

    public static IReadOnlyList<string> RoomSeatEntityKeys => RoomSeatKeys;

    public static int ControllableSpawnPointCount => ControllableKeys.Length;

    public static bool IsPlaceholderOnly(string? entityKey)
        => string.Equals(NormalizeEntityKey(entityKey), "robot_6", StringComparison.OrdinalIgnoreCase);

    public static bool IsControllableRobot(string? entityKey)
        => ControllableKeys.Any(key => string.Equals(key, NormalizeEntityKey(entityKey), StringComparison.OrdinalIgnoreCase));

    public static string NormalizeEntityKey(string? entityKey, bool allowPlaceholder = true)
    {
        string normalized = (entityKey ?? string.Empty).Trim().ToLowerInvariant();
        normalized = normalized switch
        {
            "hero" => "robot_1",
            "engineer" => "robot_2",
            "infantry" or "infantry_1" => "robot_3",
            "infantry_2" => "robot_4",
            "gimbal" or "gunner" or "operator" or "\u4e91\u53f0\u624b" => "robot_6",
            "sentry" => "robot_7",
            _ => normalized,
        };

        if (ControllableKeys.Any(key => string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return normalized;
        }

        if (allowPlaceholder && string.Equals(normalized, "robot_6", StringComparison.OrdinalIgnoreCase))
        {
            return "robot_6";
        }

        return "robot_3";
    }

    public static int ResolveRobotNumber(string? entityKey)
        => NormalizeEntityKey(entityKey) switch
        {
            "robot_1" => 1,
            "robot_2" => 2,
            "robot_4" => 4,
            "robot_6" => 6,
            "robot_7" => 7,
            _ => 3,
        };

    public static int ResolveRoomSlotIndex(string? entityKey)
        => NormalizeEntityKey(entityKey) switch
        {
            "robot_1" => 0,
            "robot_2" => 1,
            "robot_3" => 2,
            "robot_4" => 3,
            "robot_6" => 4,
            "robot_7" => 5,
            _ => 2,
        };

    public static int ResolveDefaultSpawnPointIndex(string? entityKey)
        => NormalizeEntityKey(entityKey) switch
        {
            "robot_1" => 0,
            "robot_2" => 1,
            "robot_3" => 2,
            "robot_4" => 3,
            "robot_7" => 4,
            _ => -1,
        };

    public static string ResolveRoleKey(string? entityKey)
        => NormalizeEntityKey(entityKey) switch
        {
            "robot_1" => "hero",
            "robot_2" => "engineer",
            "robot_6" => "gimbal",
            "robot_7" => "sentry",
            _ => "infantry",
        };
}
