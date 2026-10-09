using Mossmint.Domain;
using Mossmint.Domain.Planning;

namespace Mossmint.Engine.CashFlow;

public sealed record CashFlowDay(
    DateOnly Date,
    Money Opening,
    Money Inflows,
    Money Outflows,
    Money Low,
    Money Closing,
    bool BelowReserve,
    bool Negative,
    Confidence Confidence,
    IReadOnlyList<CashEvent> Events);

public sealed record LowPoint(DateOnly Date, Money Amount);

public sealed record CashFlowTotals(
    Money Income,
    Money Spending,
    Money DebtPayments,
    Money SavingsTransfers,
    Money TransfersOut,
    Money TransfersIn);

public sealed record CashFlowForecast(
    IReadOnlyList<CashFlowDay> Days,
    LowPoint Lowest,
    DateOnly? FirstDayBelowReserve,
    DateOnly? FirstNegativeDay,
    CashFlowTotals Totals,
    IReadOnlyList<UnvaluedObligation> Unvalued,
    IReadOnlyList<string> Warnings,
    Confidence Confidence);

// Replays dated events day by day from a confirmed opening balance
// (ADR-0002). Within a day, outflows apply before inflows, and the savings
// contribution a paycheck funds applies after it; Low is the lowest running
// balance that day. Negative cash is reported, never floored. The same input
// always gives the same output.
public static class CashFlowSimulator
{
    public static CashFlowForecast Run(CashFlowPlan plan) => Run(plan, CashFlowPlanner.Expand(plan));

    public static CashFlowForecast Run(CashFlowPlan plan, ExpandedPlan expanded)
    {
        var currency = plan.Opening.Amount.Currency;
        var zero = Money.Zero(currency);
        var floor = plan.ReserveFloor?.Minimum ?? zero;
        var warnings = new List<string>();

        if (plan.Through < plan.Today)
        {
            throw new ArgumentException("The forecast must end on or after today.", nameof(plan));
        }

        if (plan.Opening.Provenance.AsOf < plan.Today)
        {
            warnings.Add($"The opening balance is from {plan.Opening.Provenance.AsOf:yyyy-MM-dd}; anything since then is not reflected.");
        }

        if (expanded.Unvalued.Count > 0)
        {
            warnings.Add($"{expanded.Unvalued.Count} obligation(s) in the forecast have no amount and are left out.");
        }

        var byDay = expanded.Events
            .Where(e => e.Date >= plan.Today && e.Date <= plan.Through)
            .ToLookup(e => e.Date);

        var days = new List<CashFlowDay>();
        var balance = plan.Opening.Amount;
        var confidence = plan.Opening.Provenance.Confidence;
        LowPoint? lowest = null;
        DateOnly? firstBelowReserve = null, firstNegative = null;

        for (var day = plan.Today; day <= plan.Through; day = day.AddDays(1))
        {
            var events = byDay[day]
                .OrderBy(Phase)
                .ThenBy(e => e.Kind)
                .ThenBy(e => e.Label, StringComparer.Ordinal)
                .ThenBy(e => e.Amount.Amount)
                .ToList();

            var outflows = events.Where(e => !e.IsInflow).Aggregate(zero, (sum, e) => sum + e.Amount);
            var inflows = events.Where(e => e.IsInflow).Aggregate(zero, (sum, e) => sum + e.Amount);
            var opening = balance;
            var low = opening;
            foreach (var e in events)
            {
                balance = e.IsInflow ? balance + e.Amount : balance - e.Amount;
                low = Money.Min(low, balance);
            }

            var closing = balance;

            var dayConfidence = events.Select(e => e.Confidence).Append(confidence).Min();
            var belowReserve = low < floor;
            days.Add(new CashFlowDay(day, opening, inflows, outflows, low, closing, belowReserve, low.IsNegative, dayConfidence, events));

            if (lowest is null || low < lowest.Amount)
            {
                lowest = new LowPoint(day, low);
            }

            firstBelowReserve ??= belowReserve ? day : null;
            firstNegative ??= low.IsNegative ? day : null;
        }

        if (firstNegative is { } negative)
        {
            warnings.Add($"Checking is projected to go negative on {negative:yyyy-MM-dd}.");
        }

        var all = days.SelectMany(d => d.Events).ToList();
        Money Sum(Func<CashEvent, bool> predicate) => all.Where(predicate).Aggregate(zero, (sum, e) => sum + e.Amount);
        var totals = new CashFlowTotals(
            Sum(e => e.Kind == CashEventKind.Income),
            Sum(e => e.IsSpending),
            Sum(e => e.Kind == CashEventKind.DebtPayment),
            Sum(e => e.Kind is CashEventKind.SavingsTransfer or CashEventKind.SavingsContribution),
            Sum(e => e.Kind == CashEventKind.TransferOut),
            Sum(e => e.Kind == CashEventKind.TransferIn));

        var overall = days.Select(d => d.Confidence).Append(confidence).Min();
        if (expanded.Unvalued.Count > 0)
        {
            overall = Confidence.NeedsReview;
        }

        return new CashFlowForecast(days, lowest!, firstBelowReserve, firstNegative, totals, expanded.Unvalued, warnings, overall);
    }

    private static int Phase(CashEvent e) => e.Kind switch
    {
        CashEventKind.SavingsContribution => 2,
        _ when e.IsInflow => 1,
        _ => 0,
    };
}
