using Mossmint.Domain;

namespace Mossmint.Engine.CashFlow;

public sealed record FundedObligation(DateOnly DueDate, string Label, CashEventKind Kind, Money Amount, Confidence Confidence);

// One paycheck and the obligations it funds. The first period has no paycheck:
// it is the cash on hand, which funds anything due before the first payday.
// CarryIn is the previous period's leftover (negative when it fell short).
public sealed record FundingPeriod(
    DateOnly? Payday,
    Money CarryIn,
    Money NetPay,
    IReadOnlyList<FundedObligation> Obligations,
    Money ObligationsTotal,
    Money ContributionTarget,
    Money ContributionFunded,
    Money ContributionShortfall,
    Money Leftover)
{
    public bool IsShort => Leftover.IsNegative;
}

// Groups obligations by the paycheck that pays them (docs/financial-models/cash-flow.md):
// each obligation is funded by the latest paycheck on or before its due date,
// exactly once, and its due date never moves. The savings contribution target
// is funded from what is left after obligations; missing it is reported as a
// shortfall, separately from the reserve floor the daily forecast checks.
public static class PaycheckFunding
{
    public static IReadOnlyList<FundingPeriod> Plan(CashFlowPlan plan) => Plan(plan, CashFlowPlanner.Expand(plan));

    public static IReadOnlyList<FundingPeriod> Plan(CashFlowPlan plan, ExpandedPlan expanded)
    {
        var currency = plan.Opening.Amount.Currency;
        var zero = Money.Zero(currency);
        var target = plan.SavingsTarget?.PerPaycheck ?? zero;

        var paydays = expanded.Paydays;
        var pay = expanded.Events.Where(e => e.Kind == CashEventKind.Income).ToLookup(e => e.Date);
        var obligations = expanded.Events
            .Where(e => !e.IsInflow && e.Kind is not (CashEventKind.VariableSpending or CashEventKind.SavingsContribution))
            .OrderBy(e => e.Date).ThenBy(e => e.Label, StringComparer.Ordinal)
            .ToLookup(e => FundingPayday(paydays, e.Date));

        var periods = new List<FundingPeriod>();
        var carry = plan.Opening.Amount;
        foreach (var payday in new DateOnly?[] { null }.Concat(paydays.Select(d => (DateOnly?)d)))
        {
            var netPay = payday is { } day ? pay[day].Aggregate(zero, (sum, e) => sum + e.Amount) : zero;
            var funded = obligations[payday]
                .Select(e => new FundedObligation(e.Date, e.Label, e.Kind, e.Amount, e.Confidence))
                .ToList();
            var total = funded.Aggregate(zero, (sum, o) => sum + o.Amount);

            var beforeSavings = carry + netPay - total;
            var contribution = payday is null ? zero : Money.Min(target, Money.Max(beforeSavings, zero));
            var periodTarget = payday is null ? zero : target;
            var leftover = beforeSavings - contribution;

            periods.Add(new FundingPeriod(payday, carry, netPay, funded, total, periodTarget, contribution, periodTarget - contribution, leftover));
            carry = leftover;
        }

        return periods;
    }

    private static DateOnly? FundingPayday(IReadOnlyList<DateOnly> paydays, DateOnly due)
    {
        DateOnly? funder = null;
        foreach (var payday in paydays)
        {
            if (payday > due)
            {
                break;
            }

            funder = payday;
        }

        return funder;
    }
}
