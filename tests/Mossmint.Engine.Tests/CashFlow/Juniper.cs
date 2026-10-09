using Mossmint.Domain;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.CashFlow;

namespace Mossmint.Engine.Tests.CashFlow;

// The synthetic "Juniper" profile used across cash-flow tests. Every number is
// made up. Paid net 2,050.00 on the 15th and the last day (previous business
// day), six bills on irregular days, groceries 25.00 a day (20-35), 150.00 to
// savings each paycheck and a 300.00 checking floor.
internal static class Juniper
{
    public static readonly DateOnly Today = new(2026, 10, 9);

    public static BillId Id(int n) => new(new Guid($"00000000-0000-0000-0000-0000000000{n:00}"));

    public static readonly RecurringBill Rent = Bill(1, "Rent", AmountPolicy.Fixed(Money.Usd(1_150m)), 1);
    public static readonly RecurringBill Electric = Bill(2, "Electric", AmountPolicy.Estimated(Money.Usd(70m), Money.Usd(60m), Money.Usd(95m)), 12);
    public static readonly RecurringBill Phone = Bill(3, "Phone", AmountPolicy.Fixed(Money.Usd(45m)), 20);
    public static readonly RecurringBill CarPayment = Bill(4, "Car payment", AmountPolicy.Fixed(Money.Usd(310m)), 28, BillPurpose.DebtPayment);
    public static readonly RecurringBill CardPayment = Bill(5, "Card payment", AmountPolicy.Fixed(Money.Usd(150m)), 3, BillPurpose.DebtPayment);
    public static readonly RecurringBill Streaming = Bill(6, "Streaming", AmountPolicy.Fixed(Money.Usd(18m)), 22);

    public static CashFlowPlan Plan(
        decimal opening = 1_240m,
        Provenance? openingProvenance = null,
        DateOnly? through = null,
        IReadOnlyList<RecurringBill>? bills = null,
        SpendingScenario scenario = SpendingScenario.Base) =>
        new(
            Today,
            through ?? new DateOnly(2026, 11, 15),
            new OpeningBalance(Money.Usd(opening), openingProvenance ?? Provenance.UserConfirmed(Today.AddDays(-1))),
            [
                new IncomeSchedule(
                    new IncomeId(Guid.Parse("00000000-0000-0000-0000-000000000100")),
                    "Paycheck",
                    Money.Usd(2_050m),
                    new Schedule(new SemiMonthlyRule(DayOfMonth.Of(15), DayOfMonth.Last), BusinessDayAdjustment.PreviousBusinessDay),
                    Provenance.UserConfirmed(Today)),
            ],
            bills ?? [Rent, Electric, Phone, CarPayment, CardPayment, Streaming],
            CalendarSpec.UsFederalReserve,
            [new SpendingAllowance("Groceries", AmountPolicy.Estimated(Money.Usd(25m), Money.Usd(20m), Money.Usd(35m)))],
            new SavingsContributionTarget(Money.Usd(150m)),
            new CashReserveFloor(Money.Usd(300m)),
            scenario);

    private static RecurringBill Bill(int n, string name, AmountPolicy amount, int day, BillPurpose purpose = BillPurpose.Expense) =>
        new(Id(n), name, amount, new Schedule(new MonthlyRule(DayOfMonth.Of(day))), purpose, Provenance.UserConfirmed(Today));
}
