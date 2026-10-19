using Microsoft.Extensions.Options;

namespace MowerManager.Husqvarna;

public sealed class HusqvarnaTokenProvider(IHttpClientFactory httpFactory, IOptions<HusqvarnaOptions> options, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is null || time.GetUtcNow() >= _expires)
            {
                var o = options.Value;
                using var res = await httpFactory.CreateClient().PostAsync(o.AuthUrl, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = o.AppKey,
                    ["client_secret"] = o.AppSecret,
                }), ct);
                res.EnsureSuccessStatusCode();
                var body = await res.Content.ReadFromJsonAsync<TokenResponse>(ct)
                    ?? throw new InvalidOperationException("Empty token response");
                _token = body.AccessToken;
                _expires = time.GetUtcNow().AddSeconds(body.ExpiresIn - 60);
            }
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _token = null;
}
