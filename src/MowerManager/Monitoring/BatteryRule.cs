using MowerManager.Data;
using MowerManager.Husqvarna;

namespace MowerManager.Monitoring;

public sealed class BatteryRuleOptions
{
    public RuleScope Scope { get; set; } = RuleScope.AnyArea;
    public int ThresholdPercent { get; set; } = 20;
    // Empty in config = notify only.
    public MowerAction? Action { get; set; } = MowerAction.Pause;
}

public enum RuleOutcome { None, Notify, NotifyAndAct, Reset }

public static class BatteryRule
{
    // Mower is out on the lawn.
    private static readonly HashSet<string> Away = ["MOWING", "LEAVING", "GOING_HOME"];
    // Don't stop a mower that is already heading home.
    private static readonly HashSet<string> Actionable = ["MOWING", "LEAVING"];

    public static RuleOutcome Evaluate(RuleSettings settings, int batteryPercent, string mode, string activity, bool alreadyTriggered)
    {
        if (alreadyTriggered)
            return batteryPercent > settings.ThresholdPercent ? RuleOutcome.Reset : RuleOutcome.None;

        if (!Applies(settings.Scope, mode) || batteryPercent > settings.ThresholdPercent || !Away.Contains(activity))
            return RuleOutcome.None;

        return settings.Action is not null && Actionable.Contains(activity)
            ? RuleOutcome.NotifyAndAct
            : RuleOutcome.Notify;
    }

    private static bool Applies(RuleScope scope, string mode) => scope switch
    {
        RuleScope.AnyArea => true,
        RuleScope.SecondaryArea => mode == "SECONDARY_AREA",
        _ => false,
    };
}
