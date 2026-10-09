using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using Mossmint.Domain;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.CashFlow;

namespace Mossmint.Engine.Tests.CashFlow;

public class CashFlowTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static Money Usd(decimal amount) => Money.Usd(amount);

    [Fact]
    public void Paydays_follow_the_fifteenth_and_last_business_day()
    {
        CashFlowPlanner.Expand(Juniper.Plan()).Paydays.Should().Equal(D(10, 15), D(10, 30), D(11, 13));
    }

    [Fact]
    public void The_lowest_point_is_the_morning_of_payday_not_the_month_end()
    {
        // Hand-worked: 1,240 - 25/day groceries - 70 electric (Oct 12) leaves 1,020 by Oct 14;
        // Oct 15 spends 25 before the 2,050 deposit lands, so the low is 995.
        var forecast = CashFlowSimulator.Run(Juniper.Plan());

        forecast.Lowest.Should().Be(new LowPoint(D(10, 15), Usd(995m)));
        var payday = forecast.Days.Single(d => d.Date == D(10, 15));
        (payday.Opening, payday.Low, payday.Closing).Should().Be((Usd(1_020m), Usd(995m), Usd(2_895m)));
        forecast.Days[^1].Closing.Should().Be(Usd(4_177m));
    }

    [Fact]
    public void Every_day_rolls_into_the_next()
    {
        var days = CashFlowSimulator.Run(Juniper.Plan()).Days;

        days.Zip(days.Skip(1)).Should().OnlyContain(p => p.First.Closing == p.Second.Opening);
        days.Should().OnlyContain(d => d.Closing == d.Opening + d.Inflows - d.Outflows);
    }

    [Fact]
    public void Card_and_loan_payments_are_not_counted_as_spending()
    {
        var totals = CashFlowSimulator.Run(Juniper.Plan()).Totals;

        totals.Should().Be(new CashFlowTotals(
            Income: Usd(6_150m),
            Spending: Usd(2_303m), // 38 days x 25 + rent 1,150 + electric 2 x 70 + phone 45 + streaming 18
            DebtPayments: Usd(460m),
            SavingsTransfers: Usd(450m),
            TransfersOut: Usd(0m),
            TransfersIn: Usd(0m)));
    }

    [Fact]
    public void A_gap_inside_the_period_shows_even_when_the_period_ends_positive()
    {
        // A 1,100 bill on Oct 14 overdraws checking for a day; payday restores it.
        var bill = Juniper.Rent with { Id = Juniper.Id(9), Name = "Insurance", DueDates = new Schedule(new MonthlyRule(DayOfMonth.Of(14)), EndsOn: D(10, 31)) };
        var forecast = CashFlowSimulator.Run(Juniper.Plan(bills: [Juniper.Electric, bill with { Amount = AmountPolicy.Fixed(Usd(1_100m)) }]));

        forecast.FirstNegativeDay.Should().Be(D(10, 14));
        forecast.Days.Single(d => d.Date == D(10, 14)).Low.Should().Be(Usd(-80m));
        forecast.Days[^1].Closing.IsPositive.Should().BeTrue();
        forecast.Warnings.Should().Contain(w => w.Contains("negative on 2026-10-14"));
    }

    [Fact]
    public void Savings_target_and_reserve_floor_are_reported_separately()
    {
        // Starting at 450, electric and groceries take checking to 280 on Oct 12, under the 300 floor,
        // yet every paycheck still funds its 150 contribution.
        var plan = Juniper.Plan(opening: 450m);

        var forecast = CashFlowSimulator.Run(plan);
        var funding = PaycheckFunding.Plan(plan);

        forecast.FirstDayBelowReserve.Should().Be(D(10, 12));
        funding.Where(p => p.Payday is not null).Should().OnlyContain(p => p.ContributionShortfall.IsZero);
    }

    [Fact]
    public void Each_bill_is_funded_once_by_the_latest_paycheck_before_it_is_due()
    {
        var periods = PaycheckFunding.Plan(Juniper.Plan());

        periods.Select(p => (p.Payday, p.ObligationsTotal.Amount, p.ContributionFunded.Amount, p.Leftover.Amount)).Should().Equal(
            ((DateOnly?)null, 70m, 0m, 1_170m),
            (D(10, 15), 373m, 150m, 2_697m),
            (D(10, 30), 1_370m, 150m, 3_227m),
            (D(11, 13), 0m, 150m, 5_127m));

        var funded = periods.SelectMany(p => p.Obligations).ToList();
        funded.Should().OnlyHaveUniqueItems();
        funded.Should().HaveCount(CashFlowPlanner.Expand(Juniper.Plan()).Events.Count(e => e.Kind is CashEventKind.Expense or CashEventKind.DebtPayment));
        funded.Single(o => o.Label == "Rent").DueDate.Should().Be(D(11, 1));
    }

    [Fact]
    public void A_short_paycheck_misses_its_contribution_and_says_so()
    {
        // Oct 15 leaves 1,900 after savings; Oct 30 brings 2,050 against 4,000 due Nov 1: 50 short.
        var plan = Juniper.Plan(opening: 0m, bills: [Juniper.Rent with { Amount = AmountPolicy.Fixed(Usd(4_000m)) }]);

        var octoberThirty = PaycheckFunding.Plan(plan).Single(p => p.Payday == D(10, 30));

        octoberThirty.ContributionFunded.Should().Be(Usd(0m));
        octoberThirty.ContributionShortfall.Should().Be(Usd(150m));
        octoberThirty.Leftover.Should().Be(Usd(-50m));
        octoberThirty.IsShort.Should().BeTrue();
    }

    [Theory]
    [InlineData(SpendingScenario.Base, 25)]
    [InlineData(SpendingScenario.Cautious, 30)]
    [InlineData(SpendingScenario.Stress, 35)]
    public void Scenarios_pick_allowances_from_the_entered_range(SpendingScenario scenario, int perDay)
    {
        var groceries = CashFlowPlanner.Expand(Juniper.Plan(scenario: scenario)).Events.First(e => e.Kind == CashEventKind.VariableSpending);

        groceries.Amount.Should().Be(Usd(perDay));
        groceries.Confidence.Should().Be(Confidence.Estimated);
    }

    [Fact]
    public void Stress_one_offs_apply_only_under_stress()
    {
        var oneOff = new OneOffExpense(D(10, 20), "Car repair", Usd(600m));
        var basePlan = Juniper.Plan() with { StressOneOffs = [oneOff] };

        CashFlowPlanner.Expand(basePlan).Events.Should().NotContain(e => e.Label == "Car repair");
        CashFlowPlanner.Expand(basePlan with { Scenario = SpendingScenario.Stress }).Events.Should().Contain(e => e.Label == "Car repair");
    }

    [Fact]
    public void Paid_bills_drop_out_of_the_forecast()
    {
        var plan = Juniper.Plan() with { PaidBills = [new PaidBill(Juniper.Electric.Id, D(10, 12))] };

        CashFlowPlanner.Expand(plan).Events.Should().NotContain(e => e.Label == "Electric" && e.Date == D(10, 12));
    }

    [Fact]
    public void Safe_to_spend_covers_obligations_through_the_day_before_payday()
    {
        // Electric counts at the top of its range (95): 1,240 - 6 x 25 - 95 = 995 on Oct 14; minus the 300 floor.
        var plan = Juniper.Plan();
        plan = plan with { Through = SafeToSpend.DayBeforeNextPayday(plan) };

        var result = SafeToSpend.Calculate(plan).Should().BeOfType<SafeAmount>().Subject;

        result.Through.Should().Be(D(10, 14));
        result.Amount.Should().Be(Usd(695m));
        result.Lowest.Should().Be(new LowPoint(D(10, 14), Usd(995m)));
        result.Confidence.Should().Be(Confidence.Estimated);
    }

    [Fact]
    public void A_stale_balance_needs_confirmation_instead_of_an_amount()
    {
        var plan = Juniper.Plan(openingProvenance: Provenance.UserConfirmed(Juniper.Today.AddDays(-5)));

        SafeToSpend.Calculate(plan).Should().BeOfType<NeedsConfirmation>()
            .Which.Missing.Should().ContainSingle().Which.Should().Contain("last confirmed on 2026-10-04");
    }

    [Fact]
    public void An_imported_balance_needs_confirmation()
    {
        var plan = Juniper.Plan(openingProvenance: Provenance.Imported(Juniper.Today));

        SafeToSpend.Calculate(plan).Should().BeOfType<NeedsConfirmation>();
    }

    [Fact]
    public void A_bill_with_no_amount_needs_confirmation()
    {
        var plan = Juniper.Plan(bills: [Juniper.Electric with { Amount = AmountPolicy.Unknown() }]);

        SafeToSpend.Calculate(plan).Should().BeOfType<NeedsConfirmation>()
            .Which.Missing.Should().Contain(m => m.Contains("Electric"));
        CashFlowSimulator.Run(plan).Confidence.Should().Be(Confidence.NeedsReview);
    }

    [Fact]
    public void Falling_below_the_floor_is_a_shortfall_not_a_negative_safe_amount()
    {
        var result = SafeToSpend.Calculate(Juniper.Plan(opening: 400m));

        // 400 - 25 x 6 - 95 - 25 = 130 on the morning of Oct 15: 170 under the 300 floor.
        result.Should().BeOfType<Shortfall>().Which.Amount.Should().Be(Usd(170m));
    }

    [Fact]
    public void A_plan_replays_identically_from_json()
    {
        var plan = Juniper.Plan() with { StressOneOffs = [new OneOffExpense(D(10, 20), "Car repair", Usd(600m))] };

        var replayed = JsonSerializer.Deserialize<CashFlowPlan>(JsonSerializer.Serialize(plan))!;

        JsonSerializer.Serialize(CashFlowSimulator.Run(replayed)).Should().Be(JsonSerializer.Serialize(CashFlowSimulator.Run(plan)));
        JsonSerializer.Serialize(PaycheckFunding.Plan(replayed)).Should().Be(JsonSerializer.Serialize(PaycheckFunding.Plan(plan)));
    }

    private static readonly Arbitrary<IReadOnlyList<CashEvent>> RandomEvents =
        (from count in Gen.Choose(0, 60)
         from items in Gen.ListOf(
             from day in Gen.Choose(0, 30)
             from kind in Gen.Elements(Enum.GetValues<CashEventKind>())
             from cents in Gen.Choose(1, 500_000)
             select new CashEvent(Juniper.Today.AddDays(day), kind, $"e{cents}", Usd(cents / 100m), Confidence.Scheduled), count)
         select (IReadOnlyList<CashEvent>)items.ToList()).ToArbitrary();

    [Fact]
    public void Simulation_conserves_money_and_tracks_the_low() =>
        Prop.ForAll(RandomEvents, Gen.Choose(-100_000, 1_000_000).ToArbitrary(), (events, openingCents) =>
        {
            var plan = Juniper.Plan(opening: openingCents / 100m, through: Juniper.Today.AddDays(30));
            var forecast = CashFlowSimulator.Run(plan, new ExpandedPlan(events, [], []));
            var days = forecast.Days;
            var net = events.Sum(e => e.IsInflow ? e.Amount.Amount : -e.Amount.Amount);

            return days[0].Opening == plan.Opening.Amount
                && days[^1].Closing.Amount == plan.Opening.Amount.Amount + net
                && days.All(d => d.Low <= d.Opening && d.Low <= d.Closing)
                && days.Zip(days.Skip(1)).All(p => p.First.Closing == p.Second.Opening)
                && forecast.Lowest.Amount == days.Min(d => d.Low)
                && (forecast.FirstNegativeDay is null) == days.All(d => !d.Low.IsNegative);
        }).QuickCheckThrowOnFailure();
}
