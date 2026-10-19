using MowerManager.Husqvarna;

namespace MowerManager.Data;

public sealed class Mower
{
    public required string Id { get; set; }
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public int BatteryPercent { get; set; }
    public string Mode { get; set; } = "";
    public string Activity { get; set; } = "";
    public string State { get; set; } = "";
    public int ErrorCode { get; set; }
    public bool Connected { get; set; }
    public DateTime UpdatedAt { get; set; }
    // Set when the battery rule fired; cleared once battery recovers above the threshold.
    public DateTime? RuleTriggeredAt { get; set; }
}

public sealed class MowerSnapshot
{
    public long Id { get; set; }
    public required string MowerId { get; set; }
    public DateTime Timestamp { get; set; }
    public int BatteryPercent { get; set; }
    public string Activity { get; set; } = "";
    public string State { get; set; } = "";
    public int ErrorCode { get; set; }
}

public enum RuleScope { Off, AnyArea, SecondaryArea }

public sealed class RuleSettings
{
    public int Id { get; set; } = 1;
    public RuleScope Scope { get; set; }
    public int ThresholdPercent { get; set; }
    // Null = notify only.
    public MowerAction? Action { get; set; }
}

public sealed class EventLogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string? MowerId { get; set; }
    public required string Message { get; set; }
}

public sealed class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    // Rotated on password change to invalidate existing sessions.
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; }
}
