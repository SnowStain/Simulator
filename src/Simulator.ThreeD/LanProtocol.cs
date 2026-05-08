namespace Simulator.ThreeD;

internal static class LanProtocolMessageTypes
{
    // Session / handshake
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Status = "status";
    public const string Error = "error";

    // Lobby / room control
    public const string Input = "input";
    public const string SeatClaim = "seat_claim";
    public const string Roster = "roster";
    public const string PlayerInput = "player_input";
    public const string LobbySelection = "lobby_selection";
    public const string StartMatch = "start_match";

    // Match sync
    public const string Digest = "digest";
    public const string Snapshot = "snapshot";
    public const string AuthoritativeSnapshot = "authoritative_snapshot";

    // Referee / reliable callbacks
    public const string RefereeReport = "referee_report";
    public const string RefereeDecision = "referee_decision";
    public const string MatchEvent = "match_event";
    public const string TrafficReport = "traffic_report";
}

internal enum LanProtocolLayer
{
    Session,
    Room,
    Match,
    Sync,
    Referee,
    Observation,
}

internal enum LanProtocolDelivery
{
    Reliable,
    Realtime,
}

internal readonly record struct LanProtocolMessageDescriptor(
    string Type,
    LanProtocolLayer Layer,
    LanProtocolDelivery Delivery,
    int TypicalPayloadBytes,
    double TypicalRateHz);

internal readonly record struct LanBandwidthEstimate(
    int PlayerCount,
    int RobotCount,
    int ActiveProjectileCount,
    double InputKilobytesPerSecond,
    double SnapshotKilobytesPerSecondPerClient,
    double FacilityKilobytesPerSecondPerClient,
    double RefereeKilobytesPerSecondPerClient,
    double RecommendedKilobytesPerSecondPerClient,
    double RecommendedHostOutboundKilobytesPerSecond,
    double JsonSafetyMultiplier)
{
    public double RecommendedMegabitsPerSecondPerClient => RecommendedKilobytesPerSecondPerClient * 8.0 / 1000.0;

    public double RecommendedHostOutboundMegabitsPerSecond => RecommendedHostOutboundKilobytesPerSecond * 8.0 / 1000.0;

    public double JsonHostOutboundMegabitsPerSecond => RecommendedHostOutboundMegabitsPerSecond * JsonSafetyMultiplier;
}

internal static class LanProtocolMetadata
{
    public static LanProtocolMessageDescriptor Describe(string type)
        => type switch
        {
            LanProtocolMessageTypes.Hello => new(type, LanProtocolLayer.Session, LanProtocolDelivery.Reliable, 96, 0.0),
            LanProtocolMessageTypes.Welcome => new(type, LanProtocolLayer.Session, LanProtocolDelivery.Reliable, 128, 0.0),
            LanProtocolMessageTypes.Status => new(type, LanProtocolLayer.Session, LanProtocolDelivery.Reliable, 96, 0.0),
            LanProtocolMessageTypes.Error => new(type, LanProtocolLayer.Session, LanProtocolDelivery.Reliable, 128, 0.0),
            LanProtocolMessageTypes.SeatClaim => new(type, LanProtocolLayer.Room, LanProtocolDelivery.Reliable, 128, 0.0),
            LanProtocolMessageTypes.Roster => new(type, LanProtocolLayer.Room, LanProtocolDelivery.Reliable, 640, 0.0),
            LanProtocolMessageTypes.LobbySelection => new(type, LanProtocolLayer.Room, LanProtocolDelivery.Reliable, 120, 0.0),
            LanProtocolMessageTypes.StartMatch => new(type, LanProtocolLayer.Match, LanProtocolDelivery.Reliable, 420, 0.0),
            LanProtocolMessageTypes.Input => new(type, LanProtocolLayer.Sync, LanProtocolDelivery.Realtime, LanBandwidthBudget.CompactInputBytes, LanBandwidthBudget.InputRateHz),
            LanProtocolMessageTypes.PlayerInput => new(type, LanProtocolLayer.Sync, LanProtocolDelivery.Realtime, LanBandwidthBudget.CompactPlayerInputBytes, LanBandwidthBudget.InputRateHz),
            LanProtocolMessageTypes.Digest => new(type, LanProtocolLayer.Observation, LanProtocolDelivery.Reliable, 96, 2.0),
            LanProtocolMessageTypes.Snapshot => new(type, LanProtocolLayer.Sync, LanProtocolDelivery.Realtime, LanBandwidthBudget.CompactRobotPoseBytes * 2, LanBandwidthBudget.SnapshotRateHz),
            LanProtocolMessageTypes.AuthoritativeSnapshot => new(type, LanProtocolLayer.Sync, LanProtocolDelivery.Realtime, 900, LanBandwidthBudget.SnapshotRateHz),
            LanProtocolMessageTypes.RefereeReport => new(type, LanProtocolLayer.Referee, LanProtocolDelivery.Reliable, 384, 1.0),
            LanProtocolMessageTypes.RefereeDecision => new(type, LanProtocolLayer.Referee, LanProtocolDelivery.Reliable, 192, 1.0),
            LanProtocolMessageTypes.MatchEvent => new(type, LanProtocolLayer.Match, LanProtocolDelivery.Reliable, 192, 2.0),
            LanProtocolMessageTypes.TrafficReport => new(type, LanProtocolLayer.Observation, LanProtocolDelivery.Realtime, 360, 1.0),
            _ => new(type, LanProtocolLayer.Observation, LanProtocolDelivery.Reliable, 128, 0.0),
        };
}

internal static class LanBandwidthBudget
{
    public const int CompactInputBytes = 32;
    public const int CompactPlayerInputBytes = 56;
    public const int CompactRobotPoseBytes = 36;
    public const int CompactProjectileBytes = 24;
    public const int CompactFacilityBytes = 96;
    public const int CompactRefereeBytes = 192;
    public const double InputRateHz = 60.0;
    public const double SnapshotRateHz = 20.0;
    public const double JsonSafetyMultiplier = 3.0;

    public static LanBandwidthEstimate Estimate(
        int playerCount = 10,
        int robotCount = 10,
        int activeProjectileCount = 30,
        double inputRateHz = InputRateHz,
        double snapshotRateHz = SnapshotRateHz,
        double jsonSafetyMultiplier = JsonSafetyMultiplier)
    {
        int safePlayers = Math.Clamp(playerCount, 1, 10);
        int safeRobots = Math.Clamp(robotCount, 1, 16);
        int safeProjectiles = Math.Clamp(activeProjectileCount, 0, 96);
        double safeInputHz = Math.Clamp(inputRateHz, 10.0, 120.0);
        double safeSnapshotHz = Math.Clamp(snapshotRateHz, 5.0, 60.0);

        double inputKbps = safePlayers * CompactPlayerInputBytes * safeInputHz / 1000.0;
        double robotSnapshotKbps = safeRobots * CompactRobotPoseBytes * safeSnapshotHz / 1000.0;
        double projectileSnapshotKbps = safeProjectiles * CompactProjectileBytes * safeSnapshotHz / 1000.0;
        double snapshotKbps = robotSnapshotKbps + projectileSnapshotKbps;
        double facilityKbps = CompactFacilityBytes * 10.0 / 1000.0;
        double refereeKbps = CompactRefereeBytes * 4.0 / 1000.0;
        double perClientKbps = inputKbps / safePlayers + snapshotKbps + facilityKbps + refereeKbps;
        double hostOutboundKbps = perClientKbps * safePlayers;

        return new LanBandwidthEstimate(
            safePlayers,
            safeRobots,
            safeProjectiles,
            inputKbps,
            snapshotKbps,
            facilityKbps,
            refereeKbps,
            perClientKbps,
            hostOutboundKbps,
            Math.Clamp(jsonSafetyMultiplier, 1.0, 6.0));
    }
}
