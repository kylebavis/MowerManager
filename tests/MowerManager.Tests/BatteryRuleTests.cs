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

    [Theory]
    [InlineData(20, "MOWING", RuleOutcome.NotifyAndAct)]
    [InlineData(15, "LEAVING", RuleOutcome.NotifyAndAct)]
    [InlineData(15, "GOING_HOME", RuleOutcome.Notify)]
    [InlineData(21, "MOWING", RuleOutcome.None)]
    [InlineData(10, "CHARGING", RuleOutcome.None)]
    [InlineData(10, "PARKED_IN_CS", RuleOutcome.None)]
    [InlineData(10, "STOPPED_IN_GARDEN", RuleOutcome.None)]
    public void Any_area_untriggered(int battery, string activity, RuleOutcome expected) =>
        Assert.Equal(expected, BatteryRule.Evaluate(Settings(), battery, Main, activity, alreadyTriggered: false));

    [Theory]
    [InlineData(Secondary, RuleOutcome.NotifyAndAct)]
    [InlineData(Main, RuleOutcome.None)]
    [InlineData("HOME", RuleOutcome.None)]
    public void Secondary_area_scope_only_fires_in_secondary_area(string mode, RuleOutcome expected) =>
        Assert.Equal(expected, BatteryRule.Evaluate(Settings(RuleScope.SecondaryArea), 15, mode, "MOWING", false));

    [Theory]
    [InlineData(15, RuleOutcome.None)]
    [InlineData(20, RuleOutcome.None)]
    [InlineData(21, RuleOutcome.Reset)]
    public void Fires_once_until_battery_recovers(int battery, RuleOutcome expected) =>
        Assert.Equal(expected, BatteryRule.Evaluate(Settings(), battery, Main, "MOWING", alreadyTriggered: true));

    [Fact]
    public void Notify_only_when_no_action() =>
        Assert.Equal(RuleOutcome.Notify, BatteryRule.Evaluate(Settings(action: null), 10, Main, "MOWING", false));

    [Theory]
    [InlineData(Main)]
    [InlineData(Secondary)]
    public void Off_does_nothing(string mode) =>
        Assert.Equal(RuleOutcome.None, BatteryRule.Evaluate(Settings(RuleScope.Off), 10, mode, "MOWING", false));
}
