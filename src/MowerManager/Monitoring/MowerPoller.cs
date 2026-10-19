using Microsoft.Extensions.Options;

namespace MowerManager.Monitoring;

public sealed class MowerPoller(
    IServiceScopeFactory scopes, IOptions<PollingOptions> options, TimeProvider time, ILogger<MowerPoller> log,
    MowerStream? stream = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var tick = TimeSpan.FromSeconds(options.Value.IntervalSeconds);
        var connectedInterval = TimeSpan.FromSeconds(options.Value.ConnectedIntervalSeconds);
        DateTimeOffset? lastPoll = null;
        using var timer = new PeriodicTimer(tick, time);
        do
        {
            // While the stream is connected, poll less often; fall back to every tick when it drops.
            var interval = stream?.Connected == true ? connectedInterval : tick;
            if (lastPoll is { } last && time.GetUtcNow() - last < interval - tick / 2)
                continue;

            lastPoll = time.GetUtcNow();
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MowerMonitor>().PollAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Mower poll failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
