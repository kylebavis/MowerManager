using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MowerManager.Data;
using MowerManager.Husqvarna;
using MowerManager.Monitoring;
using MowerManager.Notifications;
using TUnit.Assertions.Enums;

namespace MowerManager.Tests;

public class StreamEventsTests
{
    [Test]
    public async Task Battery_event() =>
        await Assert.That(StreamEvents.Parse(
            """{"id":"m1","type":"battery-event-v2","attributes":{"battery":{"batteryPercent":19}}}"""))
            .IsEqualTo(new MowerEvent("m1", BatteryPercent: 19));

    [Test]
    public async Task Mower_event() =>
        await Assert.That(StreamEvents.Parse("""
                {"id":"m1","type":"mower-event-v2","attributes":{"mower":{"mode":"SECONDARY_AREA","activity":"MOWING",
                "inactiveReason":"NONE","state":"IN_OPERATION","errorCode":0,"isErrorConfirmable":false,"errorCodeTimestamp":0}}}
                """))
            .IsEqualTo(new MowerEvent("m1", Mode: "SECONDARY_AREA", Activity: "MOWING", State: "IN_OPERATION", ErrorCode: 0));

    [Test]
    public async Task Partial_mower_event() =>
        await Assert.That(StreamEvents.Parse(
            """{"id":"m1","type":"mower-event-v2","attributes":{"mower":{"activity":"GOING_HOME"}}}"""))
            .IsEqualTo(new MowerEvent("m1", Activity: "GOING_HOME"));

    [Test]
    [Arguments("""{"id":"m1","type":"position-event-v2","attributes":{"position":{"latitude":1,"longitude":2}}}""")]
    [Arguments("""{"id":"m1","type":"battery-event-v2","attributes":{"battery":{}}}""")]
    [Arguments("""{"id":"m1","type":"battery-event-v2","attributes":{"battery":{"batteryPercent":"x"}}}""")]
    [Arguments("""{"connectionId":"abc=","type":"connected"}""")]
    [Arguments("""{"type":"battery-event-v2","attributes":{"battery":{"batteryPercent":5}}}""")]
    [Arguments("[]")]
    [Arguments("")]
    [Arguments("not json")]
    public async Task Ignored(string json) => await Assert.That(StreamEvents.Parse(json)).IsNull();
}

public sealed class MonitorEventTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly List<string> _actions = [];
    private readonly FakeNotifier _notifier = new();
    private AppDb _db = null!;
    private MowerMonitor _monitor = null!;

    [Before(Test)]
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

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Test]
    public async Task Battery_event_fires_rule_once()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 20), default);
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 19), default);

        var mower = await _db.Mowers.SingleAsync();
        await Assert.That(mower.BatteryPercent).IsEqualTo(19);
        await Assert.That(mower.RuleTriggeredAt).IsNotNull();
        await Assert.That(_actions).IsEquivalentTo(["mowers/m1/actions"]);
        await Assert.That(_notifier.Sent).HasSingleItem();
        var ev = await Assert.That(await _db.Events.ToListAsync()).HasSingleItem();
        await Assert.That(ev!.Message).Contains("Sent Pause");
        await Assert.That(await _db.Snapshots.OrderBy(s => s.Id).Select(s => s.BatteryPercent).ToListAsync())
            .IsEquivalentTo([20, 19], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Mode_event_updates_scope_check()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", Mode: "MAIN_AREA", Activity: "MOWING"), default);
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 10), default);

        await Assert.That((await _db.Mowers.SingleAsync()).Mode).IsEqualTo("MAIN_AREA");
        await Assert.That(_actions).IsEmpty();
        await Assert.That(_notifier.Sent).IsEmpty();
    }

    [Test]
    public async Task Going_home_notifies_without_action()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", Activity: "GOING_HOME"), default);
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 15), default);

        await Assert.That(_actions).IsEmpty();
        await Assert.That(_notifier.Sent).HasSingleItem();
    }

    [Test]
    public async Task Recovery_resets_rule()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 18), default);
        await _monitor.HandleEventAsync(new MowerEvent("m1", Activity: "CHARGING", BatteryPercent: 40), default);

        await Assert.That((await _db.Mowers.SingleAsync()).RuleTriggeredAt).IsNull();
    }

    [Test]
    public async Task Unchanged_and_unknown_events_are_ignored()
    {
        await _monitor.HandleEventAsync(new MowerEvent("m1", BatteryPercent: 25, Activity: "MOWING"), default);
        await _monitor.HandleEventAsync(new MowerEvent("other", BatteryPercent: 5), default);

        await Assert.That(await _db.Snapshots.ToListAsync()).IsEmpty();
        await Assert.That(await _db.Mowers.CountAsync()).IsEqualTo(1);
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

[ClassDataSource<AppFactory>(Shared = SharedType.PerClass)]
public class StreamRegistrationTests(AppFactory app)
{
    [Test]
    public async Task Stream_can_be_disabled() =>
        await Assert.That(app.Services.GetService(typeof(MowerStream))).IsNull();
}
