using Mossmint.Domain;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.Cards;
using Mossmint.Engine.CashFlow;
using Mossmint.Engine.Loans;

namespace Mossmint.Samples;

// The public synthetic "Juniper" profile for screenshots, demos and docs. Every
// value is invented; nothing here comes from a real person's finances.
public static class SyntheticProfile
{
    public const string Name = "Juniper";

    public static readonly DateOnly Today = new(2026, 10, 9);

    private static readonly DateOnly LongAgo = new(2020, 1, 1);

    private static BillId Bill(int n) => new(new Guid($"5a000000-0000-0000-0000-0000000000{n:00}"));

    public static CashFlowPlan CashFlow(int days = 45, SpendingScenario scenario = SpendingScenario.Base) => new(
        Today,
        Today.AddDays(days - 1),
        new OpeningBalance(Money.Usd(1_240m), Provenance.UserConfirmed(Today.AddDays(-1))),
        [
            new IncomeSchedule(
                new IncomeId(Guid.Parse("5a000000-0000-0000-0000-000000000100")),
                "Paycheck",
                Money.Usd(2_050m),
                new Schedule(new SemiMonthlyRule(DayOfMonth.Of(15), DayOfMonth.Last), BusinessDayAdjustment.PreviousBusinessDay),
                Provenance.UserConfirmed(Today)),
        ],
        [
            Monthly(1, "Rent", AmountPolicy.Fixed(Money.Usd(1_150m)), 1),
            Monthly(2, "Electric", AmountPolicy.Estimated(Money.Usd(70m), Money.Usd(60m), Money.Usd(95m)), 12),
            Monthly(3, "Phone", AmountPolicy.Fixed(Money.Usd(45m)), 20),
            Monthly(4, "Car loan", AmountPolicy.Fixed(Money.Usd(285m)), 28, BillPurpose.DebtPayment),
            Monthly(5, "Card payment", AmountPolicy.Fixed(Money.Usd(150m)), 3, BillPurpose.DebtPayment),
            Monthly(6, "Streaming", AmountPolicy.Fixed(Money.Usd(18m)), 22),
            Monthly(7, "Internet", AmountPolicy.Fixed(Money.Usd(60m)), 17),
            Monthly(8, "Insurance", AmountPolicy.Fixed(Money.Usd(132m)), 25),
        ],
        CalendarSpec.UsFederalReserve,
        [new SpendingAllowance("Groceries", AmountPolicy.Estimated(Money.Usd(25m), Money.Usd(20m), Money.Usd(35m)))],
        new SavingsContributionTarget(Money.Usd(150m)),
        new CashReserveFloor(Money.Usd(300m)),
        scenario);

    public static SimpleInterestLoanInput CarLoan() => new(
        new LoanPosition(Today, Money.Usd(9_800m), Money.Usd(0m), Money.Usd(0m), Provenance.LenderStatement(Today.AddDays(-9))),
        new SimpleInterestLoanTerms(
            RateSchedule.Fixed(AnnualRate.FromPercent(7.4m), LongAgo),
            DayCountConvention.Actual365Fixed,
            Money.Usd(285m),
            new Schedule(new MonthlyRule(DayOfMonth.Of(28)))),
        CalendarSpec.UsFederalReserve);

    public static CreditCardInput Card() => new(
        new CardPosition(
            Today,
            [new CardBucketBalance("purchase", Money.Usd(2_300m))],
            Money.Usd(0m),
            Provenance.LenderStatement(Today.AddDays(-18)),
            new DateOnly(2026, 9, 21),
            Money.Usd(40m)),
        new CreditCardTerms(
            DayOfMonth.Of(21),
            PaymentDueRule.OnDay(DayOfMonth.Of(3)),
            [new AprBucket("purchase", BalanceBucketKind.Purchase, RateSchedule.Fixed(AnnualRate.FromPercent(24.99m), LongAgo))],
            new MinimumPaymentRule(Money.Usd(40m), 0.01m, PlusInterestAndFees: true)),
        Money.Usd(150m));

    private static RecurringBill Monthly(int n, string name, AmountPolicy amount, int day, BillPurpose purpose = BillPurpose.Expense) =>
        new(Bill(n), name, amount, new Schedule(new MonthlyRule(DayOfMonth.Of(day))), purpose, Provenance.UserConfirmed(Today));
}
