using System.Text.Json.Serialization;

namespace MowerManager.Husqvarna;

// Subset of the Automower Connect API JSON:API payloads.
public record MowerListResponse(List<MowerData> Data);
public record MowerData(string Id, MowerAttributes Attributes);
public record MowerAttributes(SystemInfo System, BatteryInfo Battery, MowerInfo Mower, MetadataInfo Metadata);
public record SystemInfo(string Name, string Model);
public record BatteryInfo(int BatteryPercent);
public record MowerInfo(string Mode, string Activity, string State, int ErrorCode);
public record MetadataInfo(bool Connected, long StatusTimestamp);

public record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

// Names match the API's action "type" values.
public enum MowerAction { Pause, ParkUntilNextSchedule, ParkUntilFurtherNotice, ResumeSchedule }
