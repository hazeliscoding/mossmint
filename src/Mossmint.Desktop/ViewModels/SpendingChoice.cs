using Mossmint.Application.Forecasting;

namespace Mossmint.Desktop.ViewModels;

// The spending assumption as the pages offer it; "Expected" reads better than "Base".
public sealed record SpendingChoice(SpendingAssumption Value, string Label)
{
    public static IReadOnlyList<SpendingChoice> All { get; } =
    [
        new(SpendingAssumption.Base, "Expected"),
        new(SpendingAssumption.Cautious, "Cautious"),
        new(SpendingAssumption.Stress, "Stress"),
    ];

    public static string LateLabel { get; } = $"Paychecks {ForecastAssumptions.LateBusinessDays} business days late";

    public static string Describe(SpendingChoice spending, bool latePaychecks) =>
        $"{spending.Label} spending" + (latePaychecks ? $", paychecks {ForecastAssumptions.LateBusinessDays} business days late" : "");
}
