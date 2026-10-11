using System.Text.Json.Serialization;
using Mossmint.Domain;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.Payoff;

namespace Mossmint.Engine.CashFlow;

public enum CashEventKind
{
    Income,
    Expense,
    VariableSpending,
    DebtPayment,
    SavingsTransfer,

    // The per-paycheck savings target, moved once the paycheck lands.
    SavingsContribution,
    TransferOut,
    TransferIn,

    // An extra debt payment, paid from the paycheck once it lands.
    ExtraDebtPayment,
}

// One dated movement of the spending account. Amount is always positive; the
// kind gives the direction. Only Expense and VariableSpending count as spending:
// debt payments and transfers move money between the user's own accounts.
public sealed record CashEvent(DateOnly Date, CashEventKind Kind, string Label, Money Amount, Confidence Confidence, string? Source = null)
{
    [JsonIgnore]
    public bool IsInflow => Kind is CashEventKind.Income or CashEventKind.TransferIn;

    [JsonIgnore]
    public bool IsSpending => Kind is CashEventKind.Expense or CashEventKind.VariableSpending;
}

public sealed record OpeningBalance(Money Amount, Provenance Provenance);

// A daily allowance for variable spending (groceries, fuel), entered by the
// user as an expected amount with an optional range.
public sealed record SpendingAllowance(string Category, AmountPolicy PerDay);

// Scenarios are assumptions, not probabilities: Base uses expected amounts,
// Cautious the midpoint between expected and high, Stress the high end plus any
// one-off expenses. LatePaycheckDays, a separate assumption, lands every paycheck
// due from today on that many business days late.
public enum SpendingScenario
{
    Base,
    Cautious,
    Stress,
}

public sealed record OneOffExpense(DateOnly Date, string Label, Money Amount);

// An obligation in the horizon whose amount nobody knows yet.
public sealed record UnvaluedObligation(BillId Bill, string Name, DateOnly Date);

public sealed record CashFlowPlan(
    DateOnly Today,
    DateOnly Through,
    OpeningBalance Opening,
    IReadOnlyList<IncomeSchedule> Income,
    IReadOnlyList<RecurringBill> Bills,
    CalendarSpec Calendar,
    IReadOnlyList<SpendingAllowance>? Allowances = null,
    SavingsContributionTarget? SavingsTarget = null,
    CashReserveFloor? ReserveFloor = null,
    SpendingScenario Scenario = SpendingScenario.Base,
    IReadOnlyList<OneOffExpense>? StressOneOffs = null,
    IReadOnlyList<PaidBill>? PaidBills = null,
    IReadOnlyList<PlannedPayment>? ExtraDebtPayments = null,
    int LatePaycheckDays = 0);

public sealed record ExpandedPlan(IReadOnlyList<CashEvent> Events, IReadOnlyList<UnvaluedObligation> Unvalued, IReadOnlyList<DateOnly> Paydays);

public static class CashFlowPlanner
{
    // Turns schedules, bills and allowances into dated events for [Today, Through].
    public static ExpandedPlan Expand(CashFlowPlan plan, bool conservativeBills = false)
    {
        var calendar = plan.Calendar.Build();
        var events = new List<CashEvent>();
        var unvalued = new List<UnvaluedObligation>();
        var paydays = new SortedSet<DateOnly>();
        var paid = (plan.PaidBills ?? []).ToHashSet();

        foreach (var income in plan.Income)
        {
            var overrides = (income.AmountOverrides ?? []).ToDictionary(o => o.Nominal, o => o.NetPay);
            foreach (var pay in income.PayDates.Occurrences(plan.Today, plan.Through, calendar))
            {
                var date = Late(pay.Date, plan.LatePaycheckDays, calendar);
                if (date > plan.Through)
                {
                    continue;
                }

                var amount = overrides.TryGetValue(pay.Nominal, out var changed) ? changed : income.NetPay;
                events.Add(new CashEvent(date, CashEventKind.Income, income.Name, amount, Weakest(income.Provenance.Confidence, Confidence.Scheduled), $"income:{income.Id.Value}"));
                paydays.Add(date);
            }
        }

        foreach (var bill in plan.Bills)
        {
            foreach (var due in bill.DueDates.Occurrences(plan.Today, plan.Through, calendar))
            {
                if (paid.Contains(new PaidBill(bill.Id, due.Nominal)))
                {
                    continue;
                }

                var amount = conservativeBills ? bill.Amount.Conservative : ForScenario(bill.Amount, plan.Scenario);
                if (amount is null)
                {
                    unvalued.Add(new UnvaluedObligation(bill.Id, bill.Name, due.Date));
                    continue;
                }

                var confidence = bill.Amount.Kind == AmountKind.Fixed ? Weakest(bill.Provenance.Confidence, Confidence.Scheduled) : Confidence.Estimated;
                events.Add(new CashEvent(due.Date, KindOf(bill.Purpose), bill.Name, amount.Value, confidence, $"bill:{bill.Id.Value}"));
            }
        }

        if (plan.SavingsTarget is { PerPaycheck.IsPositive: true } target)
        {
            events.AddRange(paydays.Select(day => new CashEvent(day, CashEventKind.SavingsContribution, "Savings contribution", target.PerPaycheck, Confidence.Scheduled, "savings")));
        }

        foreach (var allowance in plan.Allowances ?? [])
        {
            var perDay = ForScenario(allowance.PerDay, plan.Scenario);
            if (perDay is not { IsPositive: true } amount)
            {
                continue;
            }

            for (var day = plan.Today; day <= plan.Through; day = day.AddDays(1))
            {
                events.Add(new CashEvent(day, CashEventKind.VariableSpending, allowance.Category, amount, Confidence.Estimated, "allowance"));
            }
        }

        if (plan.Scenario == SpendingScenario.Stress)
        {
            events.AddRange((plan.StressOneOffs ?? [])
                .Where(o => o.Date >= plan.Today && o.Date <= plan.Through)
                .Select(o => new CashEvent(o.Date, CashEventKind.Expense, o.Label, o.Amount, Confidence.Estimated, "stress")));
        }

        events.AddRange(ExtraEvents(plan, plan.ExtraDebtPayments ?? []));
        return new ExpandedPlan(events, unvalued, [.. paydays]);
    }

    internal static IEnumerable<CashEvent> ExtraEvents(CashFlowPlan plan, IEnumerable<PlannedPayment> payments) =>
        payments
            .Where(p => p.Date >= plan.Today && p.Date <= plan.Through)
            .Select(p => new CashEvent(p.Date, CashEventKind.ExtraDebtPayment, "Extra debt payment", p.Amount, Confidence.Scheduled, "extra"));

    public static Money? ForScenario(AmountPolicy policy, SpendingScenario scenario) => (policy.Kind, scenario) switch
    {
        (AmountKind.Unknown, _) => null,
        (AmountKind.Fixed, _) => policy.Expected,
        (_, SpendingScenario.Base) => policy.Expected,
        (_, SpendingScenario.Cautious) when policy.High is { } high => ((policy.Expected!.Value + high) * 0.5m).Round(MidpointRounding.AwayFromZero),
        (_, SpendingScenario.Stress) => policy.High ?? policy.Expected,
        _ => policy.Expected,
    };

    private static DateOnly Late(DateOnly date, int businessDays, IBusinessCalendar calendar)
    {
        for (var i = 0; i < businessDays; i++)
        {
            date = BusinessDays.Adjust(date.AddDays(1), BusinessDayAdjustment.NextBusinessDay, calendar);
        }

        return date;
    }

    private static CashEventKind KindOf(BillPurpose purpose) => purpose switch
    {
        BillPurpose.Expense => CashEventKind.Expense,
        BillPurpose.DebtPayment => CashEventKind.DebtPayment,
        BillPurpose.SavingsTransfer => CashEventKind.SavingsTransfer,
        _ => CashEventKind.TransferOut,
    };

    private static Confidence Weakest(Confidence a, Confidence b) => a < b ? a : b;
}
