using System.Text.Json;

namespace MowerManager.Husqvarna;

// Partial mower update from the WebSocket stream; null = not part of this event.
public sealed record MowerEvent(
    string MowerId, int? BatteryPercent = null, string? Mode = null, string? Activity = null, string? State = null, int? ErrorCode = null);

public static class StreamEvents
{
    // Returns null for events the battery rule doesn't care about (position, calendar, ...) and non-event messages.
    public static MowerEvent? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("type", out var type)
                || !root.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
                return null;

            return type.GetString() switch
            {
                "battery-event-v2" when Section(attrs, "battery") is { } b && Int(b, "batteryPercent") is { } pct =>
                    new MowerEvent(id.GetString()!, BatteryPercent: pct),
                "mower-event-v2" when Section(attrs, "mower") is { } m =>
                    new MowerEvent(id.GetString()!, Mode: Str(m, "mode"), Activity: Str(m, "activity"),
                        State: Str(m, "state"), ErrorCode: Int(m, "errorCode")),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Section(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
