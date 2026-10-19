using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MowerManager.Data;
using MowerManager.Husqvarna;
using MowerManager.Monitoring;
using MowerManager.Notifications;

namespace MowerManager.Tests;

public class StreamEventsTests
{
    [Fact]
    public void Battery_event() =>
        Assert.Equal(new MowerEvent("m1", BatteryPercent: 19), StreamEvents.Parse(
            """{"id":"m1","type":"battery-event-v2","attributes":{"battery":{"batteryPercent":19}}}"""));

    [Fact]
    public void Mower_event() =>
        Assert.Equal(new MowerEvent("m1", Mode: "SECONDARY_AREA", Activity: "MOWING", State: "IN_OPERATION", ErrorCode: 0),
            StreamEvents.Parse("""
                {"id":"m1","type":"mower-event-v2","attributes":{"mower":{"mode":"SECONDARY_AREA","activity":"MOWING",
                "inactiveReason":"NONE","state":"IN_OPERATION","errorCode":0,"isErrorConfirmable":false,"errorCodeTimestamp":0}}}
                """));

    [Fact]
    public void Partial_mower_event() =>
        Assert.Equal(new MowerEvent("m1", Activity: "GOING_HOME"), StreamEvents.Parse(
            """{"id":"m1","type":"mower-event-v2","attributes":{"mower":{"activity":"GOING_HOME"}}}"""));

    [Theory]
    [InlineData("""{"id":"m1","type":"position-event-v2","attributes":{"position":{"latitude":1,"longitude":2}}}""")]
    [InlineData("""{"id":"m1","type":"battery-event-v2","attributes":{"battery":{}}}""")]
    [InlineData("""{"id":"m1","type":"battery-event-v2","attributes":{"battery":{"batteryPercent":"x"}}}""")]
    [InlineData("""{"connectionId":"abc=","type":"connected"}""")]
    [InlineData("""{"type":"battery-event-v2","attributes":{"battery":{"batteryPercent":5}}}""")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("not json")]
    public void Ignored(string json) => Assert.Null(StreamEvents.Parse(json));
}

public sealed class MonitorEventTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly List<string> _actions = [];
    private readonly FakeNotifier _notifier = new();
    private AppDb _db = null!;
    private MowerMonitor _monitor = null!;

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        await _db.Database.MigrateAsync();
        _db.RuleSettings.Add(new RuleSettings { Scope = RuleScope.SecondaryArea, ThresholdPercent = 20, Action = MowerAction.Pause });
        _db.Mowers.Add(new Mower
        {
            Id = "m1", Name = "Mowy", BatteryPercent = 25, Mode = "SECONDARY_AREA", Activity = "MOWING", State = "IN_OPERATION",
        });
        await _db.SaveChangesAsync();

        var http = new HttpClient(new RecordingHandler(_actions)) { BaseAddress = new Uri("http://automower.test/v1/") };
        _monitor = new MowerMonitor(_db, new AutomowerClient(http), _notifier, TimeProvider.System,
            Options.Create(new PollingOptions()), NullLogger<MowerMonitor>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Battery_event_fires_rule_once()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 20), default);
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 19), default);

        var mower = await _db.Mowers.SingleAsync();
        Assert.Equal(19, mower.BatteryPercent);
        Assert.NotNull(mower.RuleTriggeredAt);
        Assert.Equal(new[] { "mowers/m1/actions" }, _actions);
        Assert.Single(_notifier.Sent);
        Assert.Contains("Sent Pause", Assert.Single(await _db.Events.ToListAsync()).Message);
        Assert.Equal(new[] { 20, 19 }, await _db.Snapshots.OrderBy(s => s.Id).Select(s => s.BatteryPercent).ToListAsync());
    }

    [Fact]
    public async Task Mode_event_updates_scope_check()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", Mode: "MAIN_AREA", Activity: "MOWING"), default);
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 10), default);

        Assert.Equal("MAIN_AREA", (await _db.Mowers.SingleAsync()).Mode);
        Assert.Empty(_actions);
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task Going_home_notifies_without_action()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", Activity: "GOING_HOME"), default);
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 15), default);

        Assert.Empty(_actions);
        Assert.Single(_notifier.Sent);
    }

    [Fact]
    public async Task Recovery_resets_rule()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 18), default);
        await _monitor.HandleEventAsync(new MowerEvent("m1", Activity: "CHARGING", BatteryPercent: 40), default);

        Assert.Null((await _db.Mowers.SingleAsync()).RuleTriggeredAt);
    }

    [Fact]
    public async Task Unchanged_and_unknown_events_are_ignored()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 25, Activity: "MOWING"), default);
        await _monitor.HandleEventAsync(new MowerEvent("other", BatteryPercent: 5), default);

        Assert.Empty(await _db.Snapshots.ToListAsync());
        Assert.Equal(1, await _db.Mowers.CountAsync());
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<string> Sent { get; } = [];

        public Task SendAsync(string title, string message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(List<string> actions) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            actions.Add(request.RequestUri!.AbsolutePath["/v1/".Length..]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }
}

public class StreamRegistrationTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public void Stream_can_be_disabled() =>
        Assert.Null(app.Services.GetService(typeof(MowerStream)));
}
