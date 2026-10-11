using Mossmint.Domain;
using Mossmint.Engine.Payoff;

namespace Mossmint.Engine.CashFlow;

// How much extra can go to debt each month without cash dropping below the
// floor (docs/financial-models/payoff.md). The extra comes out of the first
// paycheck of each month, once it lands, on every month through the horizon.
public static class SafeExtra
{
    public static IReadOnlyList<PlannedPayment> Monthly(CashFlowPlan plan, Money amount) =>
        Monthly(CashFlowPlanner.Expand(plan with { ExtraDebtPayments = null }), amount);

    // The same confirmations as safe to spend, with bills at the plan's own
    // scenario. The amount is the largest whole cent that keeps every day at or
    // above the floor; one cent more would not.
    public static SafeToSpendResult Largest(CashFlowPlan plan, SafeToSpendPolicy? policy = null)
    {
        plan = plan with { ExtraDebtPayments = null };
        var expanded = CashFlowPlanner.Expand(plan);
        var missing = SafeToSpend.Unconfirmed(plan, expanded, policy ?? SafeToSpendPolicy.Default);
        if (expanded.Paydays.Count == 0)
        {
            missing.Add("Add a paycheck: an extra payment comes out of one.");
        }

        if (missing.Count > 0)
        {
            return new NeedsConfirmation(missing);
        }

        var currency = plan.Opening.Amount.Currency;
        var floor = plan.ReserveFloor?.Minimum ?? Money.Zero(currency);
        CashFlowForecast Run(long cents) => CashFlowSimulator.Run(plan, expanded with
        {
            Events = [.. expanded.Events, .. CashFlowPlanner.ExtraEvents(plan, Monthly(expanded, Money.FromMinorUnits(cents, currency)))],
        });

        var best = Run(0);
        List<string> Basis() =>
        [
            $"Starts from {plan.Opening.Amount} confirmed on {plan.Opening.Provenance.AsOf:yyyy-MM-dd}.",
            $"Paid from the first paycheck of each month, once it lands, through {plan.Through:yyyy-MM-dd}.",
            $"Keeps {floor} in checking on every day; the lowest point is {best.Lowest.Amount} on {best.Lowest.Date:yyyy-MM-dd}.",
            $"Spending scenario: {plan.Scenario} (an assumption, not a probability).",
        ];

        if (best.Lowest.Amount < floor)
        {
            return new Shortfall(floor - best.Lowest.Amount, best.Lowest, floor, Basis());
        }

        // An extra above the opening balance plus every paycheck breaks the floor
        // on its first date, so hi never fits; lo always does.
        var inflows = expanded.Events.Where(e => e.IsInflow).Aggregate(plan.Opening.Amount, (sum, e) => sum + e.Amount);
        var (lo, hi) = (0L, (inflows - floor).Round(MidpointRounding.ToPositiveInfinity).ToMinorUnits() + 1);
        while (hi - lo > 1)
        {
            var mid = lo + ((hi - lo) / 2);
            var forecast = Run(mid);
            if (forecast.Lowest.Amount >= floor)
            {
                (lo, best) = (mid, forecast);
            }
            else
            {
                hi = mid;
            }
        }

        return new SafeAmount(Money.FromMinorUnits(lo, currency), plan.Through, best.Lowest, floor, best.Confidence, Basis());
    }

    private static List<PlannedPayment> Monthly(ExpandedPlan expanded, Money amount) =>
        [.. expanded.Paydays.GroupBy(d => (d.Year, d.Month)).Select(g => new PlannedPayment(g.Min(), amount))];
}
