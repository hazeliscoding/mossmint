using Mossmint.Application.Data;
using Mossmint.Application.Setup;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Application.Tests;

public class ManualEntryTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private readonly InMemoryProfileData _data = new();
    private readonly ManualEntry _entry;

    public ManualEntryTests()
    {
        _entry = new ManualEntry(_data, new FixedClock(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_cash_account_starts_from_a_confirmed_balance()
    {
        var account = _entry.AddCashAccount("  Everyday  ", 1_240.55m);

        account.Name.Should().Be("Everyday");
        _data.Balances(account.Id).Single().Should().Be(new BalanceSnapshot(account.Id, Money.Usd(1_240.55m), Provenance.UserConfirmed(Today)));
    }

    [Fact]
    public void Twice_monthly_pay_lands_on_the_fifteenth_and_last_business_day()
    {
        var income = _entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly));

        income.PayDates.Occurrences(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), CalendarSpec.WeekendsOnly.Build())
            .Select(o => o.Date).Should().Equal(
                new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 30));
    }

    [Fact]
    public void Every_two_weeks_needs_the_next_payday()
    {
        var missing = () => _entry.AddPaycheck(new PaycheckEntry("Paycheck", 1_400m, PayPattern.EveryTwoWeeks));
        var income = _entry.AddPaycheck(new PaycheckEntry("Paycheck", 1_400m, PayPattern.EveryTwoWeeks, new DateOnly(2026, 10, 16)));

        missing.Should().Throw<ArgumentException>().WithMessage("*next payday*");
        income.PayDates.Rule.Should().Be(new WeeklyRule(new DateOnly(2026, 10, 16), 2));
    }

    [Fact]
    public void An_estimated_bill_keeps_its_range_and_says_it_is_an_estimate()
    {
        var bill = _entry.AddBill(new BillEntry("Electric", 70m, 12, IsEstimate: true, Low: 60m, High: 95m));

        bill.Amount.Should().Be(AmountPolicy.Estimated(Money.Usd(70m), Money.Usd(60m), Money.Usd(95m)));
        bill.Provenance.Confidence.Should().Be(Confidence.Estimated);
    }

    [Fact]
    public void A_loan_becomes_an_account_its_terms_and_a_payment_that_isnt_spending()
    {
        var debt = _entry.AddLoan(new LoanEntry("Car loan", 9_800m, Today, 7.4m, 285m, 28));

        var account = _data.Accounts().Single(a => a.Id == debt.Account);
        account.Kind.Should().Be(AccountKind.InstallmentLoan);
        debt.Terms.Should().BeOfType<SimpleInterestLoanTerms>().Which.RegularPayment.Should().Be(Money.Usd(285m));
        var payment = _data.Bills().Single();
        payment.Purpose.Should().Be(BillPurpose.DebtPayment);
        payment.TargetAccount.Should().Be(debt.Account);
    }

    [Fact]
    public void A_card_without_a_planned_payment_adds_no_bill()
    {
        var debt = _entry.AddCard(new CardEntry("Card", 2_300m, Today, 24.99m, 0m, 21, 3));

        debt.Terms.Should().BeOfType<CreditCardTerms>().Which.Minimum.FractionOfBalance.Should().Be(0.01m);
        _data.Bills().Should().BeEmpty();
    }

    [Fact]
    public void Fractions_of_a_cent_are_refused_not_rounded()
    {
        var act = () => _entry.AddCashAccount("Everyday", 10.005m);

        act.Should().Throw<ArgumentException>().WithMessage("*decimal places*");
        _data.Accounts().Should().BeEmpty();
    }

    [Theory]
    [InlineData(32)]
    [InlineData(-1)]
    public void Impossible_due_days_are_refused(int day)
    {
        var act = () => _entry.AddBill(new BillEntry("Rent", 1_150m, day));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Savings_target_and_cushion_are_stored_separately()
    {
        var settings = _entry.SetSavings(150m, 300m);

        settings.SavingsTarget.Should().Be(new SavingsContributionTarget(Money.Usd(150m)));
        settings.ReserveFloor.Should().Be(new CashReserveFloor(Money.Usd(300m)));
        _entry.SetSavings(null, 300m).SavingsTarget.Should().BeNull();
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal sealed class InMemoryProfileData : IProfileData
{
    private readonly List<Account> _accounts = [];
    private readonly List<BalanceSnapshot> _balances = [];
    private readonly Dictionary<BillId, RecurringBill> _bills = [];
    private readonly Dictionary<IncomeId, IncomeSchedule> _income = [];
    private readonly Dictionary<AccountId, DebtAccount> _debts = [];
    private readonly List<Transaction> _transactions = [];
    private readonly Dictionary<GoalId, SavingsGoal> _goals = [];
    private PlanSettings _settings = PlanSettings.Empty(Currency.Usd);

    public IReadOnlyList<Account> Accounts() => _accounts;

    public IReadOnlyList<BalanceSnapshot> Balances(AccountId account) => [.. _balances.Where(b => b.Account == account).Reverse()];

    public void AddAccount(Account account, BalanceSnapshot? opening = null)
    {
        _accounts.Add(account);
        if (opening is not null)
        {
            _balances.Add(opening);
        }
    }

    public void RecordBalance(BalanceSnapshot snapshot) => _balances.Add(snapshot);

    public IReadOnlyList<RecurringBill> Bills() => [.. _bills.Values];

    public void SaveBill(RecurringBill bill) => _bills[bill.Id] = bill;

    public IReadOnlyList<IncomeSchedule> Income() => [.. _income.Values];

    public void SaveIncome(IncomeSchedule income) => _income[income.Id] = income;

    public IReadOnlyList<DebtAccount> Debts() => [.. _debts.Values];

    public void SaveDebt(DebtAccount debt) => _debts[debt.Account] = debt;

    public IReadOnlyList<Transaction> Transactions(AccountId account) => [.. _transactions.Where(t => t.Account == account).Reverse()];

    public void RecordTransaction(Transaction transaction) => _transactions.Add(transaction);

    public void VoidTransaction(TransactionId id)
    {
        var i = _transactions.FindIndex(t => t.Id == id);
        _transactions[i] = _transactions[i] with { Status = TransactionStatus.Void };
    }

    public IReadOnlyList<SavingsGoal> Goals() => [.. _goals.Values];

    public void SaveGoal(SavingsGoal goal) => _goals[goal.Id] = goal;

    public PlanSettings Settings() => _settings;

    public void SaveSettings(PlanSettings settings) => _settings = settings;

    public ProfileCounts Counts() => new(_accounts.Count, _balances.Count, _bills.Count, _income.Count, _debts.Count, 0);
}
