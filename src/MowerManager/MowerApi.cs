using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MowerManager.Data;
using MowerManager.Husqvarna;
using MowerManager.Monitoring;
using MowerManager.Notifications;

namespace MowerManager;

public record RuleSettingsDto(RuleScope Scope, int ThresholdPercent, MowerAction? Action);

public static class MowerApi
{
    public static void MapMowerApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/mowers", (AppDb db, CancellationToken ct) =>
            db.Mowers.AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct));

        api.MapGet("/mowers/{id}/history", (string id, int? hours, AppDb db, TimeProvider time, CancellationToken ct) =>
        {
            var since = time.GetUtcNow().UtcDateTime.AddHours(-(hours ?? 24));
            return db.Snapshots.AsNoTracking()
                .Where(s => s.MowerId == id && s.Timestamp >= since)
                .OrderBy(s => s.Timestamp)
                .ToListAsync(ct);
        });

        api.MapPost("/mowers/{id}/actions/{action}", async (string id, MowerAction action,
            AutomowerClient client, AppDb db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await client.SendActionAsync(id, action, ct);
            db.Events.Add(new EventLogEntry { Timestamp = time.GetUtcNow().UtcDateTime, MowerId = id, Message = $"Manual {action} sent by {user.Identity?.Name}." });
            await db.SaveChangesAsync(ct);
            return Results.Accepted();
        });

        // Polls immediately; counts against the Husqvarna API quota.
        api.MapPost("/poll", async (MowerMonitor monitor, CancellationToken ct) =>
        {
            await monitor.PollAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/settings", async (AppDb db, INotifier notifier, CancellationToken ct) =>
        {
            var s = await db.GetRuleSettingsAsync(ct);
            return new { s.Scope, s.ThresholdPercent, s.Action, NotificationsEnabled = notifier.Enabled };
        });

        api.MapPut("/settings", async (RuleSettingsDto dto, AppDb db, CancellationToken ct) =>
        {
            if (dto.ThresholdPercent is < 1 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["thresholdPercent"] = ["Must be 1-100."] });
            if (dto.Action == MowerAction.ResumeSchedule)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["action"] = ["ResumeSchedule is not a stop action."] });

            var s = await db.GetRuleSettingsAsync(ct);
            s.Scope = dto.Scope;
            s.ThresholdPercent = dto.ThresholdPercent;
            s.Action = dto.Action;
            await db.SaveChangesAsync(ct);
            return Results.Ok(dto);
        });

        api.MapGet("/events", (int? limit, AppDb db, CancellationToken ct) =>
            db.Events.AsNoTracking().OrderByDescending(e => e.Timestamp).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct));

        api.MapPost("/notifications/test", async (INotifier notifier, CancellationToken ct) =>
        {
            if (!notifier.Enabled)
                return Results.Problem("Notifications are off. Set Pushover__AppToken and Pushover__UserKey to enable them.",
                    statusCode: StatusCodes.Status409Conflict);
            await notifier.SendAsync("Mower Manager", "Test notification", ct);
            return Results.NoContent();
        });
    }
}
