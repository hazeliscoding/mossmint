using Mossmint.Application.Data;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.Cards;
using Mossmint.Engine.CashFlow;
using Mossmint.Engine.Loans;

namespace Mossmint.Application.Forecasting;

// What the screens show from one forecast run. Desktop never references the
// engine (ArchitectureTests), so its results are copied into these records.
public enum SafeToSpendState
{
    Safe,
    Shortfall,
    NeedsConfirmation,
}

// Safe: spend up to Amount through Through. Shortfall: cash falls below the
// cushion by Amount even with no extra spending. NeedsConfirmation: no amount
// until Reasons are dealt with.
public sealed record SafeToSpendView(SafeToSpendState State, Money? Amount, DateOnly Through, Confidence? Confidence, IReadOnlyList<string> Reasons);

public sealed record ForecastEvent(string Label, Money Amount, bool IsInflow, Confidence Confidence);

public sealed record ForecastDay(DateOnly Date, Money Low, Money Closing, bool IsPayday, bool BelowCushion, IReadOnlyList<ForecastEvent> Events);

public sealed record FundedBill(DateOnly Due, string Label, Money Amount, Confidence Confidence);

// One paycheck and what it pays for, from Payday until the day before the next
// one (Until). Payday is null for the cash on hand, which funds what's due before
// the first paycheck. CarryIn is the last period's leftover, so Leftover adds up
// from the lines. Lowest is the lowest cash in the period, the number the cushion
// is checked against.
public sealed record PaycheckView(
    DateOnly? Payday,
    DateOnly Until,
    Money CarryIn,
    Money NetPay,
    IReadOnlyList<FundedBill> Funded,
    Money Bills,
    Money SavingsTarget,
    Money SavingsFunded,
    Money Leftover,
    DateOnly LowestDate,
    Money Lowest)
{
    public int BillCount => Funded.Count;
}

// A projection, not a lender's payoff quote. Problem says why there's no payoff date.
public sealed record DebtView(
    AccountId Account,
    string Name,
    Money? Balance,
    DateOnly? BalanceAsOf,
    DateOnly? PayoffDate,
    Money? InterestLeft,
    string? Problem,
    Confidence Confidence,
    bool IsApproximate);

// One due date in the window, with the entry that paid it, if any.
public sealed record BillDue(DateOnly Date, DateOnly Nominal, Transaction? Payment);

public sealed record BillView(RecurringBill Bill, IReadOnlyList<BillDue> Upcoming);

public abstract record ForecastOutcome;

public sealed record ForecastUnavailable(IReadOnlyList<string> Missing) : ForecastOutcome;

public sealed record ProfileForecast(
    string AccountName,
    Money Opening,
    Provenance OpeningProvenance,
    Money Cushion,
    IReadOnlyList<ForecastDay> Days,
    DateOnly LowestDate,
    Money Lowest,
    SafeToSpendView SafeToSpend,
    IReadOnlyList<PaycheckView> Paychecks,
    IReadOnlyList<DebtView> Debts,
    int EntriesSince = 0) : ForecastOutcome
{
    // Only when every debt has a payoff date; one that never pays off means no date at all.
    public DateOnly? DebtFree => Debts.Count > 0 && Debts.All(d => d.PayoffDate is not null) ? Debts.Max(d => d.PayoffDate) : null;
}

public static class Forecasts
{
    public const int DefaultDays = 45;

    // ROADMAP M1: US Federal Reserve holidays move paydays, as a stated assumption.
    private static readonly CalendarSpec Calendar = CalendarSpec.UsFederalReserve;

    public static ForecastOutcome Build(IProfileData data, DateOnly today, int days = DefaultDays)
    {
        var accounts = data.Accounts();
        var income = data.Income();
        if (SpendingAccount(accounts, income) is not { } account)
        {
            return new ForecastUnavailable(["Add a checking account with today's balance."]);
        }

        if (data.Balances(account.Id).FirstOrDefault() is not { } balance)
        {
            return new ForecastUnavailable([$"Enter the balance of {account.Name}."]);
        }

        var bills = data.Bills();
        var settings = data.Settings();
        var entries = data.Transactions(account.Id);
        var opening = RunningBalance.From(balance, entries);
        var plan = new CashFlowPlan(
            today,
            today.AddDays(days - 1),
            opening,
            [.. income.Where(i => (i.DepositAccount ?? account.Id) == account.Id)],
            [.. bills.Where(b => (b.FundingAccount ?? account.Id) == account.Id)],
            Calendar,
            settings.Allowances,
            settings.SavingsTarget,
            settings.ReserveFloor,
            PaidBills: [.. entries.Where(e => e.Status != TransactionStatus.Void).Select(e => e.Pays).OfType<PaidBill>()]);
        var expanded = CashFlowPlanner.Expand(plan);
        var forecast = CashFlowSimulator.Run(plan, expanded);
        var paydays = expanded.Paydays.ToHashSet();
        List<ForecastDay> forecastDays = [.. forecast.Days.Select(d => new ForecastDay(
            d.Date,
            d.Low,
            d.Closing,
            paydays.Contains(d.Date),
            d.BelowReserve,
            [.. d.Events.Select(e => new ForecastEvent(e.Label, e.Amount, e.IsInflow, e.Confidence))]))];

        return new ProfileForecast(
            account.Name,
            opening.Amount,
            balance.Provenance,
            settings.ReserveFloor?.Minimum ?? Money.Zero(balance.Balance.Currency),
            forecastDays,
            forecast.Lowest.Date,
            forecast.Lowest.Amount,
            Safe(plan),
            Paychecks(plan, PaycheckFunding.Plan(plan, expanded), forecastDays),
            [.. data.Debts().Select(d => Debt(d, data, accounts, bills))],
            entries.Count(e => RunningBalance.After(balance, e)));
    }

    // Each bill's due dates in the window, so a page can mark one paid.
    public static IReadOnlyList<BillView> UpcomingBills(IProfileData data, DateOnly today, int days = DefaultDays)
    {
        var calendar = Calendar.Build();
        var payments = data.Accounts().SelectMany(a => data.Transactions(a.Id))
            .Where(t => t.Status != TransactionStatus.Void && t.Pays is not null)
            .ToLookup(t => t.Pays!);
        return [.. data.Bills().Select(b => new BillView(b, [.. b.DueDates.Occurrences(today, today.AddDays(days - 1), calendar)
            .Select(o => new BillDue(o.Date, o.Nominal, payments[new PaidBill(b.Id, o.Nominal)].FirstOrDefault()))]))];
    }

    private static List<PaycheckView> Paychecks(CashFlowPlan plan, IReadOnlyList<FundingPeriod> periods, List<ForecastDay> days)
    {
        var views = new List<PaycheckView>();
        for (var i = 0; i < periods.Count; i++)
        {
            var p = periods[i];
            var from = p.Payday ?? plan.Today;
            var until = i + 1 < periods.Count ? periods[i + 1].Payday!.Value.AddDays(-1) : plan.Through;
            if (until < from)
            {
                // Payday is today, so there's no cash-on-hand period before it.
                continue;
            }

            // On payday the period starts once the paycheck has landed. A bill that drafts
            // before it still shows in the day's low on the Cash Flow table.
            var lowest = days.Where(d => d.Date >= from && d.Date <= until)
                .Select(d => (d.Date, Amount: d.Date == p.Payday ? d.Closing : d.Low))
                .MinBy(d => d.Amount.Amount);
            views.Add(new PaycheckView(
                p.Payday,
                until,
                p.CarryIn,
                p.NetPay,
                [.. p.Obligations.Select(o => new FundedBill(o.DueDate, o.Label, o.Amount, o.Confidence))],
                p.ObligationsTotal,
                p.ContributionTarget,
                p.ContributionFunded,
                p.Leftover,
                lowest.Date,
                lowest.Amount));
        }

        return views;
    }

    // The account paychecks land in; without one, the first checking, then cash, account.
    internal static Account? SpendingAccount(IReadOnlyList<Account> accounts, IReadOnlyList<IncomeSchedule> income) =>
        income.Select(i => i.DepositAccount).OfType<AccountId>()
            .Select(id => accounts.FirstOrDefault(a => a.Id == id)).FirstOrDefault(a => a is not null)
        ?? accounts.FirstOrDefault(a => a.Kind == AccountKind.Checking)
        ?? accounts.FirstOrDefault(a => a.Kind == AccountKind.Cash);

    private static SafeToSpendView Safe(CashFlowPlan plan)
    {
        var through = SafeToSpend.DayBeforeNextPayday(plan);
        return SafeToSpend.Calculate(plan with { Through = through }) switch
        {
            SafeAmount s => new(SafeToSpendState.Safe, s.Amount, s.Through, s.Confidence, s.Basis),
            Shortfall s => new(SafeToSpendState.Shortfall, s.Amount, through, null, s.Basis),
            NeedsConfirmation n => new(SafeToSpendState.NeedsConfirmation, null, through, null, n.Missing),
            var other => throw new InvalidOperationException($"Unknown safe-to-spend result {other.GetType().Name}."),
        };
    }

    private static DebtView Debt(DebtAccount debt, IProfileData data, IReadOnlyList<Account> accounts, IReadOnlyList<RecurringBill> bills)
    {
        var name = accounts.FirstOrDefault(a => a.Id == debt.Account)?.Name ?? "Debt";
        if (data.Balances(debt.Account).FirstOrDefault() is not { } balance)
        {
            return new DebtView(debt.Account, name, null, null, null, null, "Enter the balance owed.", Confidence.NeedsReview, debt.Terms is CreditCardTerms);
        }

        var asOf = balance.Provenance.AsOf;
        var zero = Money.Zero(balance.Balance.Currency);
        var accrued = balance.AccruedInterest ?? zero;
        var position = new LoanPosition(asOf, balance.Balance, accrued, zero, balance.Provenance);
        switch (debt.Terms)
        {
            case SimpleInterestLoanTerms terms:
                var loan = SimpleInterestLoan.Project(new SimpleInterestLoanInput(position, terms, Calendar));
                return FromOutcome(loan.Outcome, loan.Confidence, false);
            case AmortizingLoanTerms terms:
                var amortizing = AmortizingLoan.Project(new AmortizingLoanInput(position, terms, Calendar));
                return FromOutcome(amortizing.Outcome, amortizing.Confidence, false);
            case CreditCardTerms terms:
                var payment = bills.FirstOrDefault(b => b.TargetAccount == debt.Account && b.Purpose == BillPurpose.DebtPayment)?.Amount.Expected;
                if (payment is null)
                {
                    return FromOutcome(new InsufficientData([$"Enter the planned payment for {name}."]), Confidence.NeedsReview, true);
                }

                // A balance snapshot doesn't split by APR bucket, so it all goes in the first one.
                var card = new CardPosition(asOf, [new CardBucketBalance(terms.Buckets[0].Name, balance.Balance)], accrued, balance.Provenance);
                var projection = CreditCard.Project(new CreditCardInput(card, terms, payment.Value));
                return FromOutcome(projection.Outcome, projection.Confidence, true);
            default:
                return FromOutcome(new InsufficientData([$"Mossmint can't project {name} yet."]), Confidence.NeedsReview, false);
        }

        DebtView FromOutcome(PayoffOutcome outcome, Confidence confidence, bool approximate)
        {
            return outcome switch
            {
                PaysOff p => new(debt.Account, name, balance.Balance, asOf, p.PayoffDate, p.TotalInterest, null, confidence, approximate),
                NeverPaysOff n => new(debt.Account, name, balance.Balance, asOf, null, null, n.Reason, confidence, approximate),
                InsufficientData i => new(debt.Account, name, balance.Balance, asOf, null, null, string.Join(" ", i.Missing), confidence, approximate),
                _ => throw new InvalidOperationException($"Unknown payoff outcome {outcome.GetType().Name}."),
            };
        }
    }
}
