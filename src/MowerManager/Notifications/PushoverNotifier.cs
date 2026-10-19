using Microsoft.Extensions.Options;

namespace MowerManager.Notifications;

public interface INotifier
{
    // False = sends are skipped.
    bool Enabled => true;
    Task SendAsync(string title, string message, CancellationToken ct);
}

public sealed class PushoverOptions
{
    public string AppToken { get; set; } = "";
    public string UserKey { get; set; } = "";
    public string? Device { get; set; }
    public int Priority { get; set; }
}

public sealed class PushoverNotifier(HttpClient http, IOptions<PushoverOptions> options, ILogger<PushoverNotifier> log) : INotifier
{
    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.AppToken) && !string.IsNullOrWhiteSpace(options.Value.UserKey);

    public async Task SendAsync(string title, string message, CancellationToken ct)
    {
        if (!Enabled)
        {
            log.LogDebug("Notifications disabled; skipping: {Message}", message);
            return;
        }

        var o = options.Value;
        var form = new Dictionary<string, string>
        {
            ["token"] = o.AppToken,
            ["user"] = o.UserKey,
            ["title"] = title,
            ["message"] = message,
            ["priority"] = o.Priority.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(o.Device))
            form["device"] = o.Device;

        using var res = await http.PostAsync("https://api.pushover.net/1/messages.json", new FormUrlEncodedContent(form), ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Pushover failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}");
    }
}
