using System.Text.Json;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Domain.Tests;

public class DomainRecordTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly Account Checking = new(new AccountId(Guid.Parse("00000000-0000-0000-0000-000000000001")), "Checking", AccountKind.Checking, Currency.Usd);
    private static readonly Account Card = new(new AccountId(Guid.Parse("00000000-0000-0000-0000-000000000002")), "Card", AccountKind.CreditCard, Currency.Usd);
    private static readonly TransferGroupId Group = new(Guid.Parse("00000000-0000-0000-0000-0000000000aa"));

    private static Transaction Leg(Account account, decimal amount, TransferGroupId? group = null, TransactionStatus status = TransactionStatus.Posted) =>
        new(new TransactionId(Guid.NewGuid()), account.Id, Today, Money.Usd(amount), status, "Card payment", Provenance.UserConfirmed(Today), TransferGroup: group ?? Group);

    [Fact]
    public void A_card_payment_cancels_out_across_checking_and_the_card()
    {
        // Checking goes down 100; the amount owed on the card goes down 100.
        var legs = new[] { (Checking, Leg(Checking, -100m)), (Card, Leg(Card, -100m)) };

        Transfers.Problems(legs).Should().BeEmpty();
        Transfers.NetWorthEffect(Card, Money.Usd(-100m)).Should().Be(Money.Usd(100m));
    }

    [Fact]
    public void An_unbalanced_transfer_is_reported()
    {
        var legs = new[] { (Checking, Leg(Checking, -100m)), (Card, Leg(Card, -90m)) };

        Transfers.Problems(legs).Should().ContainSingle().Which.Should().Contain("don't cancel out");
    }

    [Fact]
    public void Transfer_legs_must_share_a_group_and_not_be_void()
    {
        var other = new TransferGroupId(Guid.Parse("00000000-0000-0000-0000-0000000000bb"));
        var legs = new[] { (Checking, Leg(Checking, -100m)), (Card, Leg(Card, -100m, other, TransactionStatus.Void)) };

        Transfers.Problems(legs).Should().HaveCount(2);
    }

    [Fact]
    public void Estimates_plan_with_the_top_of_their_range()
    {
        AmountPolicy.Fixed(Money.Usd(84m)).Conservative.Should().Be(Money.Usd(84m));
        AmountPolicy.Estimated(Money.Usd(71m), Money.Usd(60m), Money.Usd(95m)).Conservative.Should().Be(Money.Usd(95m));
        AmountPolicy.Estimated(Money.Usd(71m)).Conservative.Should().Be(Money.Usd(71m));
        AmountPolicy.Unknown().Conservative.Should().BeNull();
    }

    [Fact]
    public void An_estimate_range_must_contain_its_expected_value()
    {
        var act = () => AmountPolicy.Estimated(Money.Usd(71m), high: Money.Usd(50m));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(DayCountConvention.Actual365Fixed, 2028, 0.000328767123287671232876712)]
    [InlineData(DayCountConvention.Actual360, 2027, 0.000333333333333333333333333)]
    [InlineData(DayCountConvention.ActualActual, 2027, 0.000328767123287671232876712)]
    [InlineData(DayCountConvention.ActualActual, 2028, 0.000327868852459016393442623)]
    public void Daily_rates_follow_the_day_count(DayCountConvention convention, int year, double expected)
    {
        var daily = DayCount.DailyRate(AnnualRate.FromPercent(12m), convention, new DateOnly(year, 3, 1));

        decimal.Round(daily, 15).Should().Be(decimal.Round((decimal)expected, 15));
    }

    [Fact]
    public void Rate_schedules_find_the_segment_in_force()
    {
        var rates = new RateSchedule([
            new RateSegment(new DateOnly(2026, 1, 1), AnnualRate.FromPercent(0m), RateKind.Promotional),
            new RateSegment(new DateOnly(2026, 7, 1), AnnualRate.FromPercent(24.99m), RateKind.Variable, IsAssumption: true),
        ]);

        rates.SegmentOn(new DateOnly(2025, 12, 31)).Should().BeNull();
        rates.SegmentOn(new DateOnly(2026, 6, 30))!.Rate.Should().Be(AnnualRate.Zero);
        rates.SegmentOn(new DateOnly(2026, 7, 1))!.Rate.Should().Be(AnnualRate.FromPercent(24.99m));
        rates.HasAssumptions.Should().BeTrue();
    }

    [Fact]
    public void Rate_segments_must_be_in_order()
    {
        var act = () => new RateSchedule([
            new RateSegment(new DateOnly(2026, 7, 1), AnnualRate.Zero),
            new RateSegment(new DateOnly(2026, 1, 1), AnnualRate.Zero),
        ]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Allocation_lists_each_target_once()
    {
        var act = () => new PaymentAllocation([AllocationTarget.Interest, AllocationTarget.Interest, AllocationTarget.Principal]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Due_dates_on_a_day_of_month_fall_after_the_close()
    {
        var monthEnd = PaymentDueRule.OnDay(DayOfMonth.Last);

        monthEnd.DueDateFor(new DateOnly(2026, 9, 9)).Should().Be(new DateOnly(2026, 9, 30));
        monthEnd.DueDateFor(new DateOnly(2026, 9, 30)).Should().Be(new DateOnly(2026, 10, 31));
    }

    [Fact]
    public void Due_dates_can_be_days_after_the_close()
    {
        PaymentDueRule.AfterClose(25).DueDateFor(new DateOnly(2026, 9, 9)).Should().Be(new DateOnly(2026, 10, 4));
    }

    [Fact]
    public void Planning_records_round_trip_through_json()
    {
        var bill = new RecurringBill(
            new BillId(Guid.Parse("00000000-0000-0000-0000-000000000010")),
            "Electric",
            AmountPolicy.Estimated(Money.Usd(71m), Money.Usd(60m), Money.Usd(95m)),
            new Schedule(new MonthlyRule(DayOfMonth.Of(12))),
            BillPurpose.Expense,
            Provenance.Estimated(Today));

        var copy = JsonSerializer.Deserialize<RecurringBill>(JsonSerializer.Serialize(bill))!;

        copy.Amount.Conservative.Should().Be(Money.Usd(95m));
        copy.DueDates.Rule.Should().Be(bill.DueDates.Rule);
        copy.Provenance.Should().Be(bill.Provenance);
    }
}
