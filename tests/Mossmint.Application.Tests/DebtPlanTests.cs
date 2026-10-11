using Mossmint.Application.Data;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;

namespace Mossmint.Application.Tests;

public class DebtPlanTests
{
    // A Friday. Paydays fall on 15 Oct, 30 Oct and 13 Nov, so the first paycheck of each
    // month in the default 45 days is 15 Oct and 13 Nov.
    private static readonly DateOnly Today = new(2026, 10, 9);
    private readonly InMemoryProfileData _data = new();
    private readonly DebtAccount _card;
    private readonly DebtAccount _loan;

    public DebtPlanTests()
    {
        var entry = new ManualEntry(_data, new FixedClock(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero)));
        var checking = entry.AddCashAccount("Checking", 2_400m, Today);
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), checking.Id);
        entry.AddBill(new BillEntry("Rent", 1_150m, 1), checking.Id);
        entry.SetSavings(null, 300m);
        _card = entry.AddCard(new CardEntry("Card", 5_000m, Today, 24m, 250m, 10, 5), checking.Id);
        _loan = entry.AddLoan(new LoanEntry("Car loan", 1_500m, Today, 7m, 285m, 28), checking.Id);
    }

    private static DebtPlan Plan(PayoffOrder order, decimal extra, params AccountId[] custom) => new(Money.Usd(extra), order, custom);

    private ProfileForecast Forecast(int days = Forecasts.DefaultDays) =>
        Forecasts.Build(_data, Today, days).Should().BeOfType<ProfileForecast>().Subject;

    private PayoffComparison Compare(decimal extra) =>
        Scenarios.Compare(_data, Today, Money.Usd(extra)).Should().BeOfType<PayoffComparison>().Subject;

    private static IEnumerable<(DateOnly Date, Money Amount)> Extras(ProfileForecast forecast) =>
        forecast.Days.SelectMany(d => d.Events.Where(e => e.Label == "Extra debt payment").Select(e => (d.Date, e.Amount)));

    [Fact]
    public void Without_a_plan_nothing_extra_goes_out()
    {
        Extras(Forecast()).Should().BeEmpty();
    }

    [Fact]
    public void An_applied_plan_takes_the_extra_from_the_first_paycheck_of_each_month()
    {
        Scenarios.Apply(_data, Plan(PayoffOrder.Avalanche, 200m));

        var forecast = Forecast();

        Extras(forecast).Should().Equal((new DateOnly(2026, 10, 15), Money.Usd(200m)), (new DateOnly(2026, 11, 13), Money.Usd(200m)));
        forecast.Paychecks.Single(p => p.Payday == new DateOnly(2026, 10, 15)).Funded.Should().Contain(f => f.Label == "Extra debt payment");
    }

    [Fact]
    public void Debts_follow_the_applied_plan()
    {
        var before = Forecast().DebtFree;
        var avalanche = Compare(200m).Options.Single(o => o.Order == PayoffOrder.Avalanche);

        Scenarios.Apply(_data, Plan(PayoffOrder.Avalanche, 200m));
        var forecast = Forecast();

        forecast.DebtFree.Should().Be(avalanche.DebtFree).And.BeBefore(before!.Value);
        forecast.Debts.Select(d => (d.Account, d.PayoffDate)).Should().BeEquivalentTo(avalanche.Steps.Select(s => (s.Account, s.PaidOff)));
    }

    [Fact]
    public void The_extra_stops_once_everything_is_paid_off()
    {
        Scenarios.Apply(_data, Plan(PayoffOrder.Avalanche, 1_500m));

        var forecast = Forecast(days: 365);

        Extras(forecast).Should().NotBeEmpty().And.OnlyContain(e => e.Date <= forecast.DebtFree);
    }

    [Fact]
    public void The_first_scenario_column_is_the_applied_plan()
    {
        var snowball = Compare(200m).Options.Single(o => o.Order == PayoffOrder.Snowball);
        var plan = Plan(PayoffOrder.Snowball, 200m);
        Scenarios.Apply(_data, plan);

        var comparison = Compare(50m);
        var current = comparison.Options.Single(o => o.Order == PayoffOrder.TodaysPlan);

        comparison.Applied.Should().Be(plan);
        (current.Extra, current.DebtFree).Should().Be((Money.Usd(200m), snowball.DebtFree));
    }

    [Fact]
    public void A_preview_shows_what_applying_would_change()
    {
        var preview = Scenarios.Preview(_data, Today, Plan(PayoffOrder.Avalanche, 200m))!;

        (preview.Before.Extra, preview.After.Extra).Should().Be((Money.Usd(0m), Money.Usd(200m)));
        preview.After.DebtFree.Should().BeBefore(preview.Before.DebtFree!.Value);
        preview.After.Status.Should().Be(ExtraStatus.Safe);
        preview.NextExtras.Should().Equal(new DateOnly(2026, 10, 15), new DateOnly(2026, 11, 13), new DateOnly(2026, 12, 15));
        preview.SafeBefore.State.Should().Be(SafeToSpendState.Safe);
        _data.Settings().DebtPlan.Should().BeNull("a preview never saves anything");
    }

    [Fact]
    public void Applying_returns_the_plan_it_replaces_so_undo_can_put_it_back()
    {
        var first = Plan(PayoffOrder.Custom, 100m, _loan.Account, _card.Account);
        var second = Plan(PayoffOrder.Avalanche, 300m);

        Scenarios.Apply(_data, first).Should().BeNull();
        var replaced = Scenarios.Apply(_data, second);
        replaced.Should().Be(first);

        Scenarios.Apply(_data, replaced);
        _data.Settings().DebtPlan.Should().Be(first);
        Scenarios.Apply(_data, null);
        _data.Settings().DebtPlan.Should().BeNull();
    }

    [Theory]
    [InlineData(PayoffOrder.Avalanche, -1)]
    [InlineData(PayoffOrder.Avalanche, 10.005)]
    [InlineData(PayoffOrder.TodaysPlan, 100)]
    public void A_plan_needs_a_whole_cent_extra_of_zero_or_more_and_an_order(PayoffOrder order, decimal extra)
    {
        var apply = () => Scenarios.Apply(_data, Plan(order, extra));

        apply.Should().Throw<ArgumentException>();
        _data.Settings().DebtPlan.Should().BeNull();
    }
}
