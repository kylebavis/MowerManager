using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;
using MowerManager.Husqvarna;

namespace MowerManager.Monitoring;

// Listens to the Automower Connect event stream so the battery rule doesn't wait for the next poll.
public sealed class MowerStream(
    HusqvarnaTokenProvider tokens, IServiceScopeFactory scopes, IOptions<HusqvarnaOptions> options,
    TimeProvider time, ILogger<MowerStream> log) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeniedBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(60);
    // Server closes connections after 2h; reconnect first (also picks up a fresh token).
    private static readonly TimeSpan MaxSession = TimeSpan.FromMinutes(115);

    public bool Connected { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            var started = time.GetUtcNow();
            try
            {
                await RunSessionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (StreamDeniedException)
            {
                log.LogWarning("Mower event stream denied (403) for this application; using REST polling only. " +
                    "Set Husqvarna__StreamEnabled=false to stop retrying.");
                await Task.Delay(DeniedBackoff, time, ct);
                continue;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Mower event stream failed");
            }
            finally
            {
                Connected = false;
            }

            if (time.GetUtcNow() - started > TimeSpan.FromMinutes(1))
                backoff = MinBackoff;
            log.LogInformation("Reconnecting mower event stream in {Delay}", backoff);
            await Task.Delay(backoff, time, ct);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {await tokens.GetTokenAsync(ct)}");
        // Protocol-level pings; aborts the socket if no pong arrives.
        ws.Options.KeepAliveInterval = PingInterval;
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        ws.Options.CollectHttpResponseDetails = true;
        try
        {
            await ws.ConnectAsync(new Uri(options.Value.WebSocketUrl), ct);
        }
        catch (WebSocketException) when (ws.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            tokens.Invalidate();
            throw;
        }
        catch (WebSocketException) when (ws.HttpStatusCode == HttpStatusCode.Forbidden)
        {
            throw new StreamDeniedException();
        }

        Connected = true;
        log.LogInformation("Mower event stream connected");

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.CancelAfter(MaxSession);
        var pinger = PingAsync(ws, session.Token);
        try
        {
            await ReceiveAsync(ws, session.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // MaxSession reached.
        }
        finally
        {
            await session.CancelAsync();
            await pinger;
        }

        if (ws.State == WebSocketState.Open)
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }
    }

    // The API also expects application-level pings (empty text messages) to keep the connection alive.
    private async Task PingAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                await Task.Delay(PingInterval, time, ct);
                await ws.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    }

    private async Task ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var res = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (res.MessageType == WebSocketMessageType.Close)
            {
                log.LogInformation("Mower event stream closed by server: {Status} {Reason}", ws.CloseStatus, ws.CloseStatusDescription);
                return;
            }
            message.Write(buffer, 0, res.Count);
            if (!res.EndOfMessage)
                continue;

            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (StreamEvents.Parse(json) is not { } ev)
                continue;

            log.LogDebug("Mower event: {Event}", ev);
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MowerMonitor>().HandleEventAsync(ev, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Failed to apply mower event {Event}", ev);
            }
        }
    }
}

internal sealed class StreamDeniedException : Exception;
