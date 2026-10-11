using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;

namespace Mossmint.Application.Tests;

public class ScenarioTests
{
    // A Friday. Paid 2,050 twice a month into checking; rent 1,150 on the 1st; a 300 cushion.
    private static readonly DateOnly Today = new(2026, 10, 9);
    private readonly InMemoryProfileData _data = new();
    private readonly ManualEntry _entry;
    private readonly DebtAccount _card;
    private readonly DebtAccount _loan;

    public ScenarioTests()
    {
        _entry = new ManualEntry(_data, new FixedClock(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero)));
        var checking = _entry.AddCashAccount("Checking", 2_400m, Today);
        _entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), checking.Id);
        _entry.AddBill(new BillEntry("Rent", 1_150m, 1), checking.Id);
        _entry.AddBill(new BillEntry("Electric", 70m, 12, IsEstimate: true, Low: 60m, High: 95m), checking.Id);
        _entry.SetSavings(null, 300m);

        // Avalanche starts with the card (24%), snowball with the car loan (smaller balance).
        _card = _entry.AddCard(new CardEntry("Card", 5_000m, Today, 24m, 250m, 10, 5), checking.Id);
        _loan = _entry.AddLoan(new LoanEntry("Car loan", 1_500m, Today, 7m, 285m, 28), checking.Id);
    }

    private PayoffComparison Compare(decimal extra, IReadOnlyList<AccountId>? custom = null, ForecastAssumptions? assumptions = null) =>
        Scenarios.Compare(_data, Today, Money.Usd(extra), custom, assumptions).Should().BeOfType<PayoffComparison>().Subject;

    private static PayoffOption Option(PayoffComparison c, PayoffOrder order) => c.Options.Single(o => o.Order == order);

    [Fact]
    public void Compares_todays_plan_with_avalanche_snowball_and_a_custom_order()
    {
        var comparison = Compare(100m);

        comparison.Options.Select(o => o.Order).Should().Equal(PayoffOrder.TodaysPlan, PayoffOrder.Avalanche, PayoffOrder.Snowball, PayoffOrder.Custom);
        Option(comparison, PayoffOrder.Avalanche).Steps.Select(s => s.Name).Should().Equal("Card", "Car loan");
        Option(comparison, PayoffOrder.Snowball).Steps.Select(s => s.Name).Should().Equal("Car loan", "Card");
        Option(comparison, PayoffOrder.TodaysPlan).Extra.Should().Be(Money.Usd(0m));
        Option(comparison, PayoffOrder.Avalanche).Extra.Should().Be(Money.Usd(100m));
    }

    [Fact]
    public void Todays_plan_matches_the_debt_free_date_the_other_pages_show()
    {
        var forecast = Forecasts.Build(_data, Today).Should().BeOfType<ProfileForecast>().Subject;

        Option(Compare(0m), PayoffOrder.TodaysPlan).DebtFree.Should().Be(forecast.DebtFree).And.NotBeNull();
    }

    [Fact]
    public void Paying_extra_in_any_order_is_never_later_or_dearer_than_todays_plan()
    {
        var comparison = Compare(100m);
        var today = Option(comparison, PayoffOrder.TodaysPlan);

        foreach (var option in comparison.Options.Skip(1))
        {
            option.DebtFree.Should().BeOnOrBefore(today.DebtFree!.Value);
            option.Interest!.Value.Should().BeLessThan(today.Interest!.Value);
        }

        Option(comparison, PayoffOrder.Avalanche).Interest!.Value.Should().BeLessThanOrEqualTo(Option(comparison, PayoffOrder.Snowball).Interest!.Value);
    }

    [Fact]
    public void An_extra_is_labelled_safe_only_up_to_the_largest_safe_amount()
    {
        var largest = Compare(0m).LargestSafeExtra;
        largest.State.Should().Be(SafeToSpendState.Safe);

        var atLimit = Compare(largest.Amount!.Value.Amount);
        var over = Compare(largest.Amount!.Value.Amount + 0.01m);

        Option(atLimit, PayoffOrder.Avalanche).Status.Should().Be(ExtraStatus.Safe);
        Option(atLimit, PayoffOrder.Avalanche).Lowest.Should().BeGreaterThanOrEqualTo(atLimit.Cushion);
        Option(over, PayoffOrder.Avalanche).Status.Should().Be(ExtraStatus.BelowCushion);
        Option(over, PayoffOrder.Avalanche).Lowest.Should().BeLessThan(over.Cushion);
        Option(over, PayoffOrder.TodaysPlan).Status.Should().Be(ExtraStatus.Safe);
    }

    [Fact]
    public void Without_an_order_the_custom_column_starts_alphabetical()
    {
        Option(Compare(100m), PayoffOrder.Custom).Steps.Select(s => s.Name).Should().Equal("Car loan", "Card");
        Option(Compare(100m, [_card.Account]), PayoffOrder.Custom).Steps.Select(s => s.Name).Should().Equal("Card", "Car loan");
    }

    [Fact]
    public void Harsher_assumptions_leave_room_for_less_extra()
    {
        Money Largest(ForecastAssumptions assumptions) => Compare(0m, assumptions: assumptions).LargestSafeExtra.Amount!.Value;

        var expected = Largest(ForecastAssumptions.Expected);
        var stress = Largest(new(SpendingAssumption.Stress));
        var late = Largest(new(LatePaychecks: true));

        stress.Should().BeLessThan(expected);
        late.Should().BeLessThan(expected);
        Largest(ForecastAssumptions.Harshest).Should().BeLessThanOrEqualTo(Money.Min(stress, late));
    }

    [Fact]
    public void An_extra_that_wouldnt_fit_if_things_get_tight_shows_how_late_debt_free_could_be()
    {
        // Big enough that a smaller extra takes visibly longer.
        _entry.AddLoan(new LoanEntry("Student loan", 60_000m, Today, 6m, 450m, 15), _data.Accounts().Single(a => a.Name == "Checking").Id);
        var harshest = Compare(0m, assumptions: ForecastAssumptions.Harshest).LargestSafeExtra.Amount!.Value;
        var expected = Compare(0m).LargestSafeExtra.Amount!.Value;
        var between = ((harshest + expected) * 0.5m).Round(MidpointRounding.AwayFromZero);

        var tight = Option(Compare(between.Amount), PayoffOrder.Avalanche);
        var fits = Option(Compare(harshest.Amount), PayoffOrder.Avalanche);

        tight.Status.Should().Be(ExtraStatus.Safe);
        tight.ExtraIfTight.Should().Be(harshest);
        tight.DebtFreeIfTight.Should().BeAfter(tight.DebtFree!.Value);
        (fits.ExtraIfTight, fits.DebtFreeIfTight).Should().Be((null, null));
        Option(Compare(between.Amount), PayoffOrder.TodaysPlan).DebtFreeIfTight.Should().BeNull();
    }

    [Fact]
    public void The_custom_column_follows_the_order_given()
    {
        var comparison = Compare(100m, [_loan.Account, _card.Account]);

        Option(comparison, PayoffOrder.Custom).Steps.Select(s => s.Name).Should().Equal("Car loan", "Card");
        Option(comparison, PayoffOrder.Custom).DebtFree.Should().Be(Option(comparison, PayoffOrder.Snowball).DebtFree);
    }

    [Fact]
    public void A_stale_balance_means_no_extra_is_called_safe()
    {
        _data.RecordBalance(new BalanceSnapshot(_data.Accounts().Single(a => a.Name == "Checking").Id, Money.Usd(2_400m), Provenance.UserConfirmed(Today.AddDays(-10))));

        var comparison = Compare(50m);

        comparison.LargestSafeExtra.State.Should().Be(SafeToSpendState.NeedsConfirmation);
        comparison.Options.Should().OnlyContain(o => o.Status == ExtraStatus.NeedsConfirmation);
    }

    [Fact]
    public void A_debt_without_a_balance_leaves_no_debt_free_date_and_says_why()
    {
        var account = new Account(new AccountId(Guid.NewGuid()), "Store card", AccountKind.CreditCard, Currency.Usd);
        _data.AddAccount(account);
        _data.SaveDebt(new DebtAccount(account.Id, _card.Terms));

        var comparison = Compare(100m);

        comparison.Options.Should().OnlyContain(o => o.DebtFree == null && o.Problem!.Contains("Store card"));
    }

    [Fact]
    public void No_debts_means_nothing_to_compare()
    {
        var data = new InMemoryProfileData();
        var entry = new ManualEntry(data, new FixedClock(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero)));
        entry.AddCashAccount("Checking", 1_000m, Today);

        Scenarios.Compare(data, Today, Money.Usd(0m)).Should().BeOfType<ForecastUnavailable>()
            .Which.Missing.Should().ContainSingle().Which.Should().Contain("debt");
    }
}
