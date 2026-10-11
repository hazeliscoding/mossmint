using FsCheck;
using FsCheck.Fluent;
using Mossmint.Domain;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.CashFlow;
using Mossmint.Engine.Payoff;

namespace Mossmint.Engine.Tests.CashFlow;

public class SafeExtraTests
{
    private static readonly DateOnly Today = new(2027, 1, 1);

    // 500 confirmed today, paid 1,000 on the 15th, rent 900 on the 20th, a 100 floor, through March.
    private static CashFlowPlan Plan(decimal opening = 500m, decimal floor = 100m, Provenance? provenance = null, bool paid = true) =>
        new(
            Today,
            new DateOnly(2027, 3, 31),
            new OpeningBalance(Money.Usd(opening), provenance ?? Provenance.UserConfirmed(Today)),
            paid
                ? [new IncomeSchedule(new IncomeId(Guid.Empty), "Paycheck", Money.Usd(1_000m), new Schedule(new MonthlyRule(DayOfMonth.Of(15))), Provenance.UserConfirmed(Today))]
                : [],
            [new RecurringBill(new BillId(Guid.Empty), "Rent", AmountPolicy.Fixed(Money.Usd(900m)), new Schedule(new MonthlyRule(DayOfMonth.Of(20))), BillPurpose.Expense, Provenance.UserConfirmed(Today))],
            CalendarSpec.WeekendsOnly,
            ReserveFloor: new CashReserveFloor(Money.Usd(floor)));

    private static Money LowestWith(CashFlowPlan plan, Money extra) =>
        CashFlowSimulator.Run(plan with { ExtraDebtPayments = SafeExtra.Monthly(plan, extra) }).Lowest.Amount;

    [Fact]
    public void The_extra_goes_out_on_the_first_payday_of_each_month_once_the_paycheck_lands()
    {
        var plan = Plan();

        SafeExtra.Monthly(plan, Money.Usd(50m)).Select(p => p.Date)
            .Should().Equal(new DateOnly(2027, 1, 15), new DateOnly(2027, 2, 15), new DateOnly(2027, 3, 15));

        // On Jan 15 the paycheck lands first: 500 -> 1,500 -> 1,450, so the day's low stays 500.
        var jan15 = CashFlowSimulator.Run(plan with { ExtraDebtPayments = SafeExtra.Monthly(plan, Money.Usd(50m)) }).Days.Single(d => d.Date == new DateOnly(2027, 1, 15));
        (jan15.Low, jan15.Closing).Should().Be((Money.Usd(500m), Money.Usd(1_450m)));
    }

    [Fact]
    public void The_largest_safe_extra_keeps_every_day_at_or_above_the_floor()
    {
        // After rent: 600 - E (Jan 20), 700 - 2E (Feb 20), 800 - 3E (Mar 20). The floor is 100, so
        // E <= 500, 250... the tightest is 800 - 3E >= 100: E <= 233.333... -> 233.33, leaving 100.01.
        var result = SafeExtra.Largest(Plan()).Should().BeOfType<SafeAmount>().Subject;

        result.Amount.Should().Be(Money.Usd(233.33m));
        result.Lowest.Should().Be(new LowPoint(new DateOnly(2027, 3, 20), Money.Usd(100.01m)));
        result.Through.Should().Be(new DateOnly(2027, 3, 31));
    }

    [Fact]
    public void Cash_already_below_the_floor_is_a_shortfall_with_no_extra_at_all()
    {
        // The lowest point is the opening 500, before the first paycheck; a 550 floor is 50 short.
        SafeExtra.Largest(Plan(floor: 550m)).Should().BeOfType<Shortfall>().Which.Amount.Should().Be(Money.Usd(50m));
    }

    [Fact]
    public void A_stale_balance_needs_confirmation()
    {
        SafeExtra.Largest(Plan(provenance: Provenance.UserConfirmed(Today.AddDays(-10)))).Should().BeOfType<NeedsConfirmation>();
    }

    [Fact]
    public void Without_a_paycheck_there_is_nothing_to_pay_an_extra_from()
    {
        SafeExtra.Largest(Plan(paid: false)).Should().BeOfType<NeedsConfirmation>()
            .Which.Missing.Should().ContainSingle().Which.Should().Contain("paycheck");
    }

    // M5 "Done when": an extra is never called safe if the forecast under the chosen
    // assumption drops below the cushion. Positive control: one cent more always does.
    [Theory]
    [InlineData(SpendingScenario.Base)]
    [InlineData(SpendingScenario.Cautious)]
    [InlineData(SpendingScenario.Stress)]
    public void An_extra_is_safe_exactly_when_cash_stays_at_or_above_the_cushion(SpendingScenario scenario)
    {
        var plan = Juniper.Plan(opening: 2_400m, through: new DateOnly(2027, 10, 31), scenario: scenario);
        var largest = SafeExtra.Largest(plan).Should().BeOfType<SafeAmount>().Subject.Amount;
        var floor = plan.ReserveFloor!.Minimum;

        LowestWith(plan, largest).Should().BeGreaterThanOrEqualTo(floor);
        LowestWith(plan, largest + Money.Usd(0.01m)).Should().BeLessThan(floor);

        Prop.ForAll(Gen.Choose(0, (int)(largest.Amount * 300m)).ToArbitrary(), cents =>
        {
            var extra = Money.FromMinorUnits(cents, Currency.Usd);
            return (LowestWith(plan, extra) >= floor) == (extra <= largest);
        }).QuickCheckThrowOnFailure();
    }
}
