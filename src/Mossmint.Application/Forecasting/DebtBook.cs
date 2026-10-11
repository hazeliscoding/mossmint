using Mossmint.Application.Data;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Planning;
using Mossmint.Engine.CashFlow;
using Mossmint.Engine.Payoff;

namespace Mossmint.Application.Forecasting;

internal sealed record KnownDebt(AccountId Account, string Name, IDebtInput Input, RecurringBill? Bill, DateOnly AsOf);

// The profile's debts, ready for the payoff planner: those it can project, why
// the others can't be, and when each is paid off on its own (Baseline).
internal sealed record DebtBook(IReadOnlyList<KnownDebt> Known, IReadOnlyList<string> Problems, PayoffPlan Baseline)
{
    // Cash is checked until today's plan is debt-free, at most this many years
    // out: past that, today's paychecks and bills say little about the future.
    private const int MaxHorizonYears = 10;

    public static DebtBook Load(IProfileData data)
    {
        var accounts = data.Accounts();
        var bills = data.Bills();
        var problems = new List<string>();
        var known = new List<KnownDebt>();
        foreach (var debt in data.Debts())
        {
            var name = Forecasts.DebtName(debt, accounts);
            var bill = Forecasts.PaymentBill(debt, bills);
            var balance = data.Balances(debt.Account).FirstOrDefault();
            var (input, problem) = balance is null ? (null, $"Enter the balance owed on {name}.") : Forecasts.DebtInput(debt, balance, name, bill);
            if (input is null)
            {
                problems.Add(problem!);
            }
            else
            {
                known.Add(new KnownDebt(debt.Account, name, input, bill, balance!.Provenance.AsOf));
            }
        }

        return new DebtBook(known, problems, PayoffPlanner.Baseline([.. known.Select(k => new PayoffDebt(k.Account, k.Input, []))]));
    }

    public DateOnly Horizon(DateOnly today)
    {
        var (earliest, latest) = (today.AddDays(Forecasts.DefaultDays - 1), today.AddYears(MaxHorizonYears));
        return Baseline.DebtFree is { } free ? (free < earliest ? earliest : free > latest ? latest : free) : latest;
    }

    // Each debt with what its bill pays, from its balance date through the plan's
    // horizon at the plan's scenario: the money that moves on once it's paid off.
    public List<PayoffDebt> Debts(CashFlowPlan plan) =>
        [.. Known.Select(k => new PayoffDebt(k.Account, k.Input, k.Bill is { } bill && CashFlowPlanner.ForScenario(bill.Amount, plan.Scenario) is { } amount
            ? [.. bill.DueDates.Occurrences(k.AsOf, plan.Through, plan.Calendar.Build()).Select(o => new PlannedPayment(o.Date, amount))]
            : []))];

    // Debts a custom order doesn't list follow it by name, so the default never
    // depends on storage order. Today's plan keeps the listed order.
    public IReadOnlyList<AccountId> Order(PayoffOrder order, IReadOnlyList<PayoffDebt> debts, IReadOnlyList<AccountId>? custom) => order switch
    {
        PayoffOrder.Avalanche => PayoffPlanner.Avalanche(debts),
        PayoffOrder.Snowball => PayoffPlanner.Snowball(debts),
        PayoffOrder.Custom => [.. (custom ?? []).Where(id => Known.Any(k => k.Account == id))
            .Concat(Known.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(k => k.Account.Value).Select(k => k.Account))
            .Distinct()],
        _ => [.. Known.Select(k => k.Account)],
    };

    // A saved plan, followed: its payoff, and its extras up to the day
    // everything is paid off, after which there's nothing left to pay extra on.
    public (PayoffPlan Payoff, List<PlannedPayment> Extras) Follow(DebtPlan plan, IReadOnlyList<PayoffDebt> debts, CashFlowPlan horizon)
    {
        var extras = plan.MonthlyExtra.IsPositive ? SafeExtra.Monthly(horizon, plan.MonthlyExtra) : [];
        var payoff = PayoffPlanner.Plan(debts, Order(plan.Order, debts, plan.Custom), extras);
        return (payoff, [.. extras.Where(e => payoff.DebtFree is not { } free || e.Date <= free)]);
    }
}
