using System.Net.Http.Headers;

namespace MowerManager.Husqvarna;

public sealed class AutomowerClient(HttpClient http)
{
    private static readonly MediaTypeHeaderValue JsonApi = new("application/vnd.api+json");

    public async Task<IReadOnlyList<MowerData>> GetMowersAsync(CancellationToken ct) =>
        (await http.GetFromJsonAsync<MowerListResponse>("mowers", ct))?.Data ?? [];

    public async Task SendActionAsync(string mowerId, MowerAction action, CancellationToken ct)
    {
        var content = JsonContent.Create(new { data = new { type = action.ToString() } }, JsonApi);
        using var res = await http.PostAsync($"mowers/{Uri.EscapeDataString(mowerId)}/actions", content, ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Action {action} failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}", null, res.StatusCode);
    }
}
