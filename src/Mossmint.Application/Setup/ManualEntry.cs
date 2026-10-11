using Mossmint.Application.Data;
using Mossmint.Application.Forecasting;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.CashFlow;

namespace Mossmint.Application.Setup;

public enum PayPattern
{
    // The 15th and the last day of the month.
    TwiceMonthly,
    EveryTwoWeeks,
    Weekly,
    MonthlyOnDay,
    LastBusinessDay,
}

// Due days: 1-31, or 0 for the last day of the month.
public sealed record PaycheckEntry(
    string Name,
    decimal NetPay,
    PayPattern Pattern,
    DateOnly? NextPayday = null,
    int? DayOfMonth = null,
    bool PayEarlyOnNonBusinessDays = true);

public sealed record BillEntry(
    string Name,
    decimal Amount,
    int DueDay,
    BillPurpose Purpose = BillPurpose.Expense,
    bool IsEstimate = false,
    decimal? Low = null,
    decimal? High = null);

public sealed record LoanEntry(string Name, decimal Balance, DateOnly AsOf, decimal AprPercent, decimal MonthlyPayment, int DueDay);

public sealed record CardEntry(
    string Name,
    decimal Balance,
    DateOnly AsOf,
    decimal AprPercent,
    decimal PlannedPayment,
    int StatementClosingDay,
    int DueDay,
    decimal MinimumFloor = 35m,
    decimal MinimumPercent = 1m);

// Turns what a person types into domain records, for the "Start fresh" setup.
// Everything typed in is UserConfirmed as of the date given (or today); nothing
// is guessed. The payments a loan or card needs become bills, so the cash-flow
// forecast sees them; the bill is a debt payment, never spending.
public sealed class ManualEntry(IProfileData data, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private Currency Currency => data.Settings().Currency;

    public Account AddCashAccount(string name, decimal balance, DateOnly? asOf = null, AccountKind kind = AccountKind.Checking)
    {
        Require(kind is AccountKind.Checking or AccountKind.Savings or AccountKind.Cash, "A cash account is checking, savings or cash.");
        var account = new Account(new AccountId(NewId()), Clean(name), kind, Currency);
        var date = asOf ?? Today;
        data.AddAccount(account, new BalanceSnapshot(account.Id, Cents(balance), Provenance.UserConfirmed(date)));
        return account;
    }

    public IncomeSchedule AddPaycheck(PaycheckEntry entry, AccountId? depositTo = null)
    {
        Require(entry.NetPay > 0m, "Net pay must be more than zero.");
        var adjust = entry.PayEarlyOnNonBusinessDays ? BusinessDayAdjustment.PreviousBusinessDay : BusinessDayAdjustment.None;
        var rule = entry.Pattern switch
        {
            PayPattern.TwiceMonthly => (ScheduleRule)new SemiMonthlyRule(DayOfMonth.Of(15), DayOfMonth.Last),
            PayPattern.EveryTwoWeeks => new WeeklyRule(entry.NextPayday ?? throw Invalid("Enter the next payday."), 2),
            PayPattern.Weekly => new WeeklyRule(entry.NextPayday ?? throw Invalid("Enter the next payday."), 1),
            PayPattern.MonthlyOnDay => new MonthlyRule(Day(entry.DayOfMonth ?? throw Invalid("Enter the day of the month."))),
            PayPattern.LastBusinessDay => new MonthlyRule(DayOfMonth.Last),
            _ => throw Invalid("Choose how often you're paid."),
        };
        if (entry.Pattern == PayPattern.LastBusinessDay)
        {
            adjust = BusinessDayAdjustment.PreviousBusinessDay;
        }

        var income = new IncomeSchedule(new IncomeId(NewId()), Clean(entry.Name), Cents(entry.NetPay), new Schedule(rule, adjust),
            Provenance.UserConfirmed(Today), DepositAccount: depositTo);
        data.SaveIncome(income);
        return income;
    }

    public RecurringBill AddBill(BillEntry entry, AccountId? payFrom = null, AccountId? payTo = null)
    {
        var (name, amount, provenance) = (Clean(entry.Name), Amount(entry), BillProvenance(entry));
        var bill = new RecurringBill(new BillId(NewId()), name, amount, new Schedule(new MonthlyRule(Day(entry.DueDay))),
            entry.Purpose, provenance, payFrom, payTo);
        data.SaveBill(bill);
        return bill;
    }

    // A price change, a new name or a subscription's real billing day. A new day
    // starts a plain monthly schedule on it, since skips and business-day moves
    // belong to the old day; otherwise the schedule stays. A loan or card payment
    // keeps the debt's terms in step, so payoff dates and cash use the same payment.
    public RecurringBill UpdateBill(RecurringBill bill, BillEntry entry)
    {
        var (name, amount, provenance, day) = (Clean(entry.Name), Amount(entry), BillProvenance(entry), Day(entry.DueDay));
        Schedule? moved = bill.DueDates.Rule is MonthlyRule(var current) && current == day ? null : new Schedule(new MonthlyRule(day));
        var changed = bill with { Name = name, Amount = amount, DueDates = moved ?? bill.DueDates, Provenance = provenance };
        if (bill.Purpose == BillPurpose.DebtPayment && data.Debts().FirstOrDefault(d => d.Account == bill.TargetAccount) is { } debt)
        {
            data.SaveDebt(debt with { Terms = InStep(debt.Terms, amount.Expected!.Value, moved) });
        }

        data.SaveBill(changed);
        return changed;
    }

    // A cancellation: nothing is due from `from` on, and the bill's history stays.
    public RecurringBill EndBill(RecurringBill bill, DateOnly from)
    {
        Require(bill.Purpose != BillPurpose.DebtPayment, "A debt's payment stays while the debt is owed. Change its amount instead.");
        var ended = bill with { DueDates = bill.DueDates with { EndsOn = from.AddDays(-1) } };
        data.SaveBill(ended);
        return ended;
    }

    // Only a bill that was never paid, such as a typo: one with payments is ended
    // instead, so its history stays.
    public void RemoveBill(RecurringBill bill)
    {
        Require(bill.Purpose != BillPurpose.DebtPayment, "A debt's payment stays while the debt is owed. Change its amount instead.");
        var paid = data.Accounts().Any(a => data.Transactions(a.Id).Any(t => t.Status != TransactionStatus.Void && t.Pays?.Bill == bill.Id));
        Require(!paid, $"{bill.Name} has payments recorded. End it instead, so its history stays.");
        data.RemoveBill(bill.Id);
    }

    public DebtAccount AddLoan(LoanEntry entry, AccountId? payFrom = null)
    {
        Require(entry.Balance >= 0m && entry.MonthlyPayment > 0m, "Enter the balance owed and the monthly payment.");
        var account = new Account(new AccountId(NewId()), Clean(entry.Name), AccountKind.InstallmentLoan, Currency);
        var terms = new SimpleInterestLoanTerms(
            RateSchedule.Fixed(AnnualRate.FromPercent(entry.AprPercent), entry.AsOf),
            DayCountConvention.Actual365Fixed,
            Cents(entry.MonthlyPayment),
            new Schedule(new MonthlyRule(Day(entry.DueDay))));
        data.AddAccount(account, new BalanceSnapshot(account.Id, Cents(entry.Balance), Provenance.UserConfirmed(entry.AsOf)));
        var debt = new DebtAccount(account.Id, terms);
        data.SaveDebt(debt);
        AddBill(new BillEntry(entry.Name, entry.MonthlyPayment, entry.DueDay, BillPurpose.DebtPayment), payFrom, account.Id);
        return debt;
    }

    public DebtAccount AddCard(CardEntry entry, AccountId? payFrom = null)
    {
        Require(entry.Balance >= 0m && entry.PlannedPayment >= 0m, "Enter the balance and the payment you plan to make.");
        var account = new Account(new AccountId(NewId()), Clean(entry.Name), AccountKind.CreditCard, Currency);
        var terms = new CreditCardTerms(
            Day(entry.StatementClosingDay),
            PaymentDueRule.OnDay(Day(entry.DueDay)),
            [new AprBucket("purchase", BalanceBucketKind.Purchase, RateSchedule.Fixed(AnnualRate.FromPercent(entry.AprPercent), entry.AsOf))],
            new MinimumPaymentRule(Cents(entry.MinimumFloor), entry.MinimumPercent / 100m, PlusInterestAndFees: true));
        data.AddAccount(account, new BalanceSnapshot(account.Id, Cents(entry.Balance), Provenance.UserConfirmed(entry.AsOf)));
        var debt = new DebtAccount(account.Id, terms);
        data.SaveDebt(debt);
        if (entry.PlannedPayment > 0m)
        {
            AddBill(new BillEntry(entry.Name, entry.PlannedPayment, entry.DueDay, BillPurpose.DebtPayment), payFrom, account.Id);
        }

        return debt;
    }

    public PlanSettings SetSavings(decimal? perPaycheck, decimal? cushion)
    {
        Require(perPaycheck is null or >= 0m && cushion is null or >= 0m, "Amounts can't be negative.");
        var settings = data.Settings() with
        {
            SavingsTarget = perPaycheck is > 0m ? new SavingsContributionTarget(Cents(perPaycheck.Value)) : null,
            ReserveFloor = cushion is > 0m ? new CashReserveFloor(Cents(cushion.Value)) : null,
        };
        data.SaveSettings(settings);
        return settings;
    }

    // A balance read off the bank or lender today. It becomes the account's latest,
    // so the forecast or payoff projection starts from it. For a loan, unpaid
    // interest is entered apart from principal, as lenders report it.
    public BalanceSnapshot ConfirmBalance(AccountId account, decimal balance, decimal? unpaidInterest = null)
    {
        Require(unpaidInterest is null or >= 0m, "Unpaid interest can't be negative.");
        var snapshot = new BalanceSnapshot(account, Cents(balance), Provenance.UserConfirmed(Today),
            AccruedInterest: unpaidInterest is { } interest ? Cents(interest) : null, RecordedAt: clock.GetUtcNow());
        data.RecordBalance(snapshot);
        return snapshot;
    }

    // Money in (positive) or out (negative) of a cash account, as it happened.
    // Only today or earlier: what's still to come belongs in bills, not the ledger.
    public Transaction RecordEntry(AccountId account, string description, decimal amount, DateOnly date, string? category = null)
    {
        Require(amount != 0m, "Enter an amount other than zero.");
        Require(date <= Today, "Use today or an earlier date. Add a bill for money that's still to go out.");
        var entry = new Transaction(new TransactionId(NewId()), account, date, Cents(amount), TransactionStatus.Posted, Clean(description),
            Provenance.UserConfirmed(date), Category: string.IsNullOrWhiteSpace(category) ? null : category.Trim(), RecordedAt: clock.GetUtcNow());
        data.RecordTransaction(entry);
        return entry;
    }

    // What Mossmint expects the account to hold now: its latest balance plus the
    // entries recorded since. Null without a balance.
    public Money? Expected(AccountId account) =>
        data.Balances(account).FirstOrDefault() is { } latest ? RunningBalance.From(latest, data.Transactions(account)).Amount : null;

    // The balance the bank shows today. Any difference from what Mossmint expected
    // is kept on the balance as a discrepancy; nothing is deleted or rewritten.
    public (BalanceSnapshot Balance, Money? Difference) Reconcile(AccountId account, decimal balance)
    {
        var expected = Expected(account);
        var confirmed = Cents(balance);
        var difference = expected is { } e ? confirmed - e : (Money?)null;
        var snapshot = new BalanceSnapshot(account, confirmed, Provenance.UserConfirmed(Today),
            difference switch
            {
                null => ReconciliationState.Unreconciled,
                { IsZero: true } => ReconciliationState.Reconciled,
                _ => ReconciliationState.Discrepancy,
            },
            RecordedAt: clock.GetUtcNow());
        data.RecordBalance(snapshot);
        return (snapshot, difference);
    }

    public SavingsGoal AddGoal(string name, decimal target, decimal saved = 0m, DateOnly? by = null)
    {
        Require(target > 0m, "The goal amount must be more than zero.");
        Require(by is null || by > Today, "Pick a date after today, or leave it empty.");
        var goal = new SavingsGoal(new GoalId(NewId()), Clean(name), Cents(target), Saved(saved), Provenance.UserConfirmed(Today), by);
        data.SaveGoal(goal);
        return goal;
    }

    public SavingsGoal UpdateSaved(SavingsGoal goal, decimal saved)
    {
        var updated = goal with { Saved = Saved(saved), SavedProvenance = Provenance.UserConfirmed(Today) };
        data.SaveGoal(updated);
        return updated;
    }

    // Pays one bill occurrence, today, from the account the bill is funded from.
    // The payment comes off that balance until a newer one is confirmed, and the
    // occurrence leaves the forecast (docs/financial-models/cash-flow.md).
    public Transaction MarkPaid(RecurringBill bill, DateOnly nominal)
    {
        Require(bill.Amount.Expected is not null, $"Enter the amount of {bill.Name} before marking it paid.");
        var account = bill.FundingAccount ?? Forecasts.SpendingAccount(data.Accounts(), data.Income())?.Id;
        Require(account is not null, "Add the checking account bills are paid from.");
        var paid = new PaidBill(bill.Id, nominal);
        Require(!data.Transactions(account!.Value).Any(t => t.Pays == paid && t.Status != TransactionStatus.Void), $"{bill.Name} is already marked paid.");
        var payment = new Transaction(new TransactionId(NewId()), account.Value, Today, -bill.Amount.Expected!.Value, TransactionStatus.Posted,
            bill.Name, Provenance.UserConfirmed(Today), Pays: paid, RecordedAt: clock.GetUtcNow());
        data.RecordTransaction(payment);
        return payment;
    }

    public void Undo(Transaction entry) => data.VoidTransaction(entry.Id);

    private AmountPolicy Amount(BillEntry entry)
    {
        Require(entry.Amount > 0m, "The amount must be more than zero.");
        return entry.IsEstimate
            ? AmountPolicy.Estimated(Cents(entry.Amount), entry.Low is { } low ? Cents(low) : null, entry.High is { } high ? Cents(high) : null)
            : AmountPolicy.Fixed(Cents(entry.Amount));
    }

    private Provenance BillProvenance(BillEntry entry) => entry.IsEstimate ? Provenance.Estimated(Today) : Provenance.UserConfirmed(Today);

    // Dates follow the bill only when its day changed: an imported loan keeps its own schedule otherwise.
    private static DebtTerms InStep(DebtTerms terms, Money payment, Schedule? moved) => terms switch
    {
        SimpleInterestLoanTerms t => t with { RegularPayment = payment, PaymentDates = moved ?? t.PaymentDates },
        AmortizingLoanTerms t => t with { ScheduledPayment = payment, PaymentDates = moved ?? t.PaymentDates },
        CreditCardTerms t when moved?.Rule is MonthlyRule(var day) => t with { PaymentDue = PaymentDueRule.OnDay(day) },
        _ => terms,
    };

    private Money Saved(decimal saved)
    {
        Require(saved >= 0m, "The amount saved can't be negative.");
        return Cents(saved);
    }

    // Amounts are typed in currency units; anything finer than the currency's
    // minor unit is a typo, not a value to round silently.
    private Money Cents(decimal amount)
    {
        var money = new Money(amount, Currency);
        Require(money.IsWholeMinorUnits, $"{amount} has more decimal places than {Currency.Code} allows.");
        return money;
    }

    private static DayOfMonth Day(int day) =>
        day == 0 ? DayOfMonth.Last : day is >= 1 and <= 31 ? DayOfMonth.Of(day) : throw Invalid("Use a day from 1 to 31, or the last day.");

    private static string Clean(string name) =>
        string.IsNullOrWhiteSpace(name) ? throw Invalid("Give it a name.") : name.Trim();

    private Guid NewId() => Guid.CreateVersion7(clock.GetUtcNow());

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw Invalid(message);
        }
    }

    private static ArgumentException Invalid(string message) => new(message);
}
