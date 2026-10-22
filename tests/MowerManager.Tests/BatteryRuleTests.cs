using MowerManager.Data;
using MowerManager.Husqvarna;
using MowerManager.Monitoring;

namespace MowerManager.Tests;

public class BatteryRuleTests
{
    private const string Main = "MAIN_AREA";
    private const string Secondary = "SECONDARY_AREA";

    private static RuleSettings Settings(RuleScope scope = RuleScope.AnyArea, MowerAction? action = MowerAction.Pause) =>
        new() { Scope = scope, ThresholdPercent = 20, Action = action };

    [Test]
    [Arguments(20, "MOWING", RuleOutcome.NotifyAndAct)]
    [Arguments(15, "LEAVING", RuleOutcome.NotifyAndAct)]
    [Arguments(15, "GOING_HOME", RuleOutcome.Notify)]
    [Arguments(21, "MOWING", RuleOutcome.None)]
    [Arguments(10, "CHARGING", RuleOutcome.None)]
    [Arguments(10, "PARKED_IN_CS", RuleOutcome.None)]
    [Arguments(10, "STOPPED_IN_GARDEN", RuleOutcome.None)]
    public async Task Any_area_untriggered(int battery, string activity, RuleOutcome expected) =>
        await Assert.That(BatteryRule.Evaluate(Settings(), battery, Main, activity, alreadyTriggered: false)).IsEqualTo(expected);

    [Test]
    [Arguments(Secondary, RuleOutcome.NotifyAndAct)]
    [Arguments(Main, RuleOutcome.None)]
    [Arguments("HOME", RuleOutcome.None)]
    public async Task Secondary_area_scope_only_fires_in_secondary_area(string mode, RuleOutcome expected) =>
        await Assert.That(BatteryRule.Evaluate(Settings(RuleScope.SecondaryArea), 15, mode, "MOWING", false)).IsEqualTo(expected);

    [Test]
    [Arguments(15, RuleOutcome.None)]
    [Arguments(20, RuleOutcome.None)]
    [Arguments(21, RuleOutcome.Reset)]
    public async Task Fires_once_until_battery_recovers(int battery, RuleOutcome expected) =>
        await Assert.That(BatteryRule.Evaluate(Settings(), battery, Main, "MOWING", alreadyTriggered: true)).IsEqualTo(expected);

    [Test]
    public async Task Notify_only_when_no_action() =>
        await Assert.That(BatteryRule.Evaluate(Settings(action: null), 10, Main, "MOWING", false)).IsEqualTo(RuleOutcome.Notify);

    [Test]
    [Arguments(Main)]
    [Arguments(Secondary)]
    public async Task Off_does_nothing(string mode) =>
        await Assert.That(BatteryRule.Evaluate(Settings(RuleScope.Off), 10, mode, "MOWING", false)).IsEqualTo(RuleOutcome.None);
}
