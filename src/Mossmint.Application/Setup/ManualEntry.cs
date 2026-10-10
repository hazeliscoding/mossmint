using Mossmint.Application.Data;
using Mossmint.Application.Forecasting;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;

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
        Require(entry.Amount > 0m, "The amount must be more than zero.");
        var amount = entry.IsEstimate
            ? AmountPolicy.Estimated(Cents(entry.Amount), entry.Low is { } low ? Cents(low) : null, entry.High is { } high ? Cents(high) : null)
            : AmountPolicy.Fixed(Cents(entry.Amount));
        var bill = new RecurringBill(new BillId(NewId()), Clean(entry.Name), amount, new Schedule(new MonthlyRule(Day(entry.DueDay))),
            entry.Purpose, entry.IsEstimate ? Provenance.Estimated(Today) : Provenance.UserConfirmed(Today), payFrom, payTo);
        data.SaveBill(bill);
        return bill;
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
