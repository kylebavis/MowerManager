using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace MowerManager.Husqvarna;

public sealed class HusqvarnaAuthHandler(HusqvarnaTokenProvider tokens, IOptions<HusqvarnaOptions> options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await AuthorizeAsync(request, ct);
        var res = await base.SendAsync(request, ct);
        if (res.StatusCode != HttpStatusCode.Unauthorized)
            return res;

        // Token may have been revoked early; refresh once.
        res.Dispose();
        tokens.Invalidate();
        await AuthorizeAsync(request, ct);
        return await base.SendAsync(request, ct);
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct));
        request.Headers.Remove("X-Api-Key");
        request.Headers.Add("X-Api-Key", options.Value.AppKey);
        request.Headers.Remove("Authorization-Provider");
        request.Headers.Add("Authorization-Provider", "husqvarna");
    }
}
