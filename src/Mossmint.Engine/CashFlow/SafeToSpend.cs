using System.Text.Json.Serialization;
using Mossmint.Domain;

namespace Mossmint.Engine.CashFlow;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$result")]
[JsonDerivedType(typeof(SafeAmount), "safe")]
[JsonDerivedType(typeof(Shortfall), "shortfall")]
[JsonDerivedType(typeof(NeedsConfirmation), "needs-confirmation")]
public abstract record SafeToSpendResult;

// Spending up to Amount today keeps every day through Through at or above the
// reserve floor, after all planned obligations and allowances.
public sealed record SafeAmount(
    Money Amount,
    DateOnly Through,
    LowPoint Lowest,
    Money ReserveFloor,
    Confidence Confidence,
    IReadOnlyList<string> Basis) : SafeToSpendResult;

// Even with no extra spending, cash falls below the floor by Amount.
public sealed record Shortfall(Money Amount, LowPoint Lowest, Money ReserveFloor, IReadOnlyList<string> Basis) : SafeToSpendResult;

// The inputs can't support an amount. Missing says what to confirm.
public sealed record NeedsConfirmation(IReadOnlyList<string> Missing) : SafeToSpendResult;

public sealed record SafeToSpendPolicy(int MaxOpeningBalanceAgeDays = 3)
{
    public static SafeToSpendPolicy Default { get; } = new();
}

// ADR-0002: an amount is given only with a confirmed, fresh opening balance and
// a known amount for every obligation in the horizon. Estimated bills count at
// the top of their range.
public static class SafeToSpend
{
    public static SafeToSpendResult Calculate(CashFlowPlan plan, SafeToSpendPolicy? policy = null)
    {
        policy ??= SafeToSpendPolicy.Default;
        var opening = plan.Opening.Provenance;
        var missing = new List<string>();

        if (opening.Confidence != Confidence.Confirmed)
        {
            missing.Add($"Confirm the checking balance: the current figure is {opening.Source} ({opening.Confidence}).");
        }
        else if (!opening.IsFreshOn(plan.Today, policy.MaxOpeningBalanceAgeDays))
        {
            missing.Add($"Confirm the checking balance: it was last confirmed on {opening.AsOf:yyyy-MM-dd}, more than {policy.MaxOpeningBalanceAgeDays} days ago.");
        }

        var expanded = CashFlowPlanner.Expand(plan, conservativeBills: true);
        missing.AddRange(expanded.Unvalued.Select(u => $"Enter an amount for {u.Name}, due {u.Date:yyyy-MM-dd}."));
        if (missing.Count > 0)
        {
            return new NeedsConfirmation(missing);
        }

        var forecast = CashFlowSimulator.Run(plan, expanded);
        var floor = plan.ReserveFloor?.Minimum ?? Money.Zero(plan.Opening.Amount.Currency);
        var headroom = forecast.Lowest.Amount - floor;
        var basis = new List<string>
        {
            $"Starts from {plan.Opening.Amount} confirmed on {opening.AsOf:yyyy-MM-dd}.",
            $"Covers every scheduled obligation through {plan.Through:yyyy-MM-dd}, with estimates at the top of their range.",
            $"Keeps {floor} in checking on every day; the lowest point is {forecast.Lowest.Amount} on {forecast.Lowest.Date:yyyy-MM-dd}.",
            $"Spending scenario: {plan.Scenario} (an assumption, not a probability).",
        };

        return headroom.IsNegative
            ? new Shortfall(-headroom, forecast.Lowest, floor, basis)
            : new SafeAmount(headroom, plan.Through, forecast.Lowest, floor, forecast.Confidence, basis);
    }

    // The horizon most screens want: through the day before the next paycheck.
    public static DateOnly DayBeforeNextPayday(CashFlowPlan plan) =>
        CashFlowPlanner.Expand(plan with { Through = plan.Today.AddDays(62) })
            .Paydays.Where(d => d > plan.Today).Select(d => d.AddDays(-1)).DefaultIfEmpty(plan.Through).First();
}
