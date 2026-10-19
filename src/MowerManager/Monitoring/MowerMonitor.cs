using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MowerManager.Data;
using MowerManager.Husqvarna;
using MowerManager.Notifications;

namespace MowerManager.Monitoring;

public sealed class PollingOptions
{
    public int IntervalSeconds { get; set; } = 300;
    // REST poll interval while the WebSocket stream is connected.
    public int ConnectedIntervalSeconds { get; set; } = 1800;
    public int HistoryDays { get; set; } = 30;
}

public sealed class MowerMonitor(
    AppDb db, AutomowerClient client, INotifier notifier, TimeProvider time,
    IOptions<PollingOptions> polling, ILogger<MowerMonitor> log)
{
    // Polls and stream events both run the rule; serialize them so it fires once.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task PollAsync(CancellationToken ct)
    {
        var mowers = await client.GetMowersAsync(ct);
        await Gate.WaitAsync(ct);
        try
        {
            var settings = await db.GetRuleSettingsAsync(ct);
            var now = time.GetUtcNow().UtcDateTime;

            foreach (var data in mowers)
            {
                var a = data.Attributes;
                var mower = await db.Mowers.FindAsync([data.Id], ct) ?? db.Mowers.Add(new Mower { Id = data.Id }).Entity;
                mower.Name = a.System.Name;
                mower.Model = a.System.Model;
                mower.BatteryPercent = a.Battery.BatteryPercent;
                mower.Mode = a.Mower.Mode;
                mower.Activity = a.Mower.Activity;
                mower.State = a.Mower.State;
                mower.ErrorCode = a.Mower.ErrorCode;
                mower.Connected = a.Metadata.Connected;
                mower.UpdatedAt = now;
                await RecordAsync(mower, settings, now, ct);
            }

            var cutoff = now.AddDays(-polling.Value.HistoryDays);
            await db.Snapshots.Where(s => s.Timestamp < cutoff).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task HandleEventAsync(MowerEvent ev, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // Unknown mowers are picked up by the next poll.
            if (await db.Mowers.FindAsync([ev.MowerId], ct) is not { } mower)
                return;

            mower.BatteryPercent = ev.BatteryPercent ?? mower.BatteryPercent;
            mower.Mode = ev.Mode ?? mower.Mode;
            mower.Activity = ev.Activity ?? mower.Activity;
            mower.State = ev.State ?? mower.State;
            mower.ErrorCode = ev.ErrorCode ?? mower.ErrorCode;
            db.ChangeTracker.DetectChanges();
            if (db.Entry(mower).State == EntityState.Unchanged)
                return;

            var now = time.GetUtcNow().UtcDateTime;
            mower.UpdatedAt = now;
            await RecordAsync(mower, await db.GetRuleSettingsAsync(ct), now, ct);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    // Snapshot + battery rule; shared by polls and stream events.
    private async Task RecordAsync(Mower mower, RuleSettings settings, DateTime now, CancellationToken ct)
    {
        db.Snapshots.Add(new MowerSnapshot
        {
            MowerId = mower.Id,
            Timestamp = now,
            BatteryPercent = mower.BatteryPercent,
            Activity = mower.Activity,
            State = mower.State,
            ErrorCode = mower.ErrorCode,
        });

        var outcome = BatteryRule.Evaluate(settings, mower.BatteryPercent, mower.Mode, mower.Activity, mower.RuleTriggeredAt is not null);
        if (outcome == RuleOutcome.Reset)
            mower.RuleTriggeredAt = null;
        else if (outcome is RuleOutcome.Notify or RuleOutcome.NotifyAndAct)
        {
            mower.RuleTriggeredAt = now;
            await FireRuleAsync(mower, settings, outcome == RuleOutcome.NotifyAndAct, ct);
        }
    }

    private async Task FireRuleAsync(Mower mower, RuleSettings settings, bool act, CancellationToken ct)
    {
        var message = $"{mower.Name} battery at {mower.BatteryPercent}% ({mower.Activity}).";
        if (act)
        {
            try
            {
                await client.SendActionAsync(mower.Id, settings.Action!.Value, ct);
                message += $" Sent {settings.Action}.";
            }
            catch (HttpRequestException ex)
            {
                log.LogError(ex, "Battery rule action failed for {Mower}", mower.Id);
                message += $" {settings.Action} FAILED: {ex.Message}";
            }
        }

        db.Events.Add(new EventLogEntry { Timestamp = time.GetUtcNow().UtcDateTime, MowerId = mower.Id, Message = message });
        try
        {
            await notifier.SendAsync($"{mower.Name}: low battery", message, ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogError(ex, "Notification failed");
        }
    }
}
