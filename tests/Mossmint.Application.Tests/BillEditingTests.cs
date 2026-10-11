using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Application.Tests;

// The test user's questions: a price change, a subscription's real billing day,
// a cancellation, and a bill the workbook import split across both paychecks.
public class BillEditingTests
{
    // A Friday. Paydays fall on 15 Oct, 30 Oct and 13 Nov.
    private static readonly DateOnly Today = new(2026, 10, 9);
    private readonly InMemoryProfileData _data = new();
    private readonly ManualEntry _entry;
    private readonly Account _checking;

    public BillEditingTests()
    {
        _entry = new ManualEntry(_data, new FixedClock(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero)));
        _checking = _entry.AddCashAccount("Checking", 2_400m, Today);
        _entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), _checking.Id);
    }

    private IEnumerable<(DateOnly Date, Money Amount)> Events(string label, int days = Forecasts.DefaultDays) =>
        ((ProfileForecast)Forecasts.Build(_data, Today, days)).Days.SelectMany(d => d.Events.Where(e => e.Label == label).Select(e => (d.Date, e.Amount)));

    // Shaped like an imported line: on a paycheck day, moved earlier for weekends, skipping a month.
    private RecurringBill Imported(string name, decimal amount)
    {
        var bill = new RecurringBill(new BillId(Guid.NewGuid()), name, AmountPolicy.Fixed(Money.Usd(amount)),
            new Schedule(new MonthlyRule(DayOfMonth.Last), BusinessDayAdjustment.PreviousBusinessDay, new DateOnly(2026, 1, 31), new DateOnly(2027, 3, 31),
                [new SkipOccurrence(new DateOnly(2026, 11, 30))]),
            BillPurpose.Expense, Provenance.Imported(Today), _checking.Id);
        _data.SaveBill(bill);
        return bill;
    }

    [Fact]
    public void A_price_change_reaches_the_forecast_at_once_and_keeps_the_schedule()
    {
        var streaming = Imported("Streaming", 13.99m);

        var changed = _entry.UpdateBill(streaming, new BillEntry("Streaming", 15.99m, 0));

        changed.Id.Should().Be(streaming.Id);
        changed.DueDates.Should().Be(streaming.DueDates);
        Events("Streaming").Should().Equal((new DateOnly(2026, 10, 30), Money.Usd(15.99m)));
    }

    [Fact]
    public void A_new_billing_day_starts_a_plain_monthly_schedule()
    {
        var youTube = Imported("YouTube (second paycheck)", 13.99m);

        var changed = _entry.UpdateBill(youTube, new BillEntry("YouTube", 13.99m, 14));

        (changed.Name, changed.DueDates).Should().Be(("YouTube", new Schedule(new MonthlyRule(DayOfMonth.Of(14)))));
        Events("YouTube", days: 70).Select(e => e.Date).Should().Equal(new DateOnly(2026, 10, 14), new DateOnly(2026, 11, 14), new DateOnly(2026, 12, 14));
    }

    [Fact]
    public void Editing_a_loan_payment_keeps_the_loan_terms_in_step()
    {
        var loan = _entry.AddLoan(new LoanEntry("Car loan", 9_800m, Today, 7.4m, 285m, 28), _checking.Id);
        var bill = _data.Bills().Single(b => b.TargetAccount == loan.Account);
        var before = ((ProfileForecast)Forecasts.Build(_data, Today)).Debts.Single().PayoffDate;

        _entry.UpdateBill(bill, new BillEntry("Car loan", 400m, 1, BillPurpose.DebtPayment));

        var terms = _data.Debts().Single().Terms.Should().BeOfType<SimpleInterestLoanTerms>().Subject;
        (terms.RegularPayment, terms.PaymentDates).Should().Be((Money.Usd(400m), new Schedule(new MonthlyRule(DayOfMonth.Of(1)))));
        ((ProfileForecast)Forecasts.Build(_data, Today)).Debts.Single().PayoffDate.Should().BeBefore(before!.Value);
    }

    [Fact]
    public void Editing_a_card_payment_moves_the_card_due_day()
    {
        var card = _entry.AddCard(new CardEntry("Card", 2_300m, Today, 24.99m, 150m, 21, 3), _checking.Id);
        var bill = _data.Bills().Single(b => b.TargetAccount == card.Account);

        _entry.UpdateBill(bill, new BillEntry("Card", 200m, 8, BillPurpose.DebtPayment));

        _data.Debts().Single().Terms.Should().BeOfType<CreditCardTerms>().Which.PaymentDue.Should().Be(PaymentDueRule.OnDay(DayOfMonth.Of(8)));
    }

    [Fact]
    public void A_cancelled_bill_stops_from_its_date_and_keeps_its_history()
    {
        var gym = _entry.AddBill(new BillEntry("Gym", 40m, 12), _checking.Id);
        var paid = _entry.MarkPaid(gym, new DateOnly(2026, 10, 12));

        var ended = _entry.EndBill(gym, new DateOnly(2026, 11, 1));

        ended.DueDates.EndsOn.Should().Be(new DateOnly(2026, 10, 31));
        Events("Gym", days: 90).Should().BeEmpty("October is paid and nothing is due after the cancellation");
        _data.Transactions(_checking.Id).Should().Contain(paid);
        _data.Bills().Should().Contain(b => b.Id == gym.Id);
    }

    [Fact]
    public void Only_a_bill_that_was_never_paid_can_be_removed()
    {
        var typo = _entry.AddBill(new BillEntry("Gmy", 40m, 12), _checking.Id);
        var gym = _entry.AddBill(new BillEntry("Gym", 40m, 12), _checking.Id);
        _entry.MarkPaid(gym, new DateOnly(2026, 10, 12));

        _entry.RemoveBill(typo);
        var remove = () => _entry.RemoveBill(gym);

        _data.Bills().Should().NotContain(b => b.Id == typo.Id);
        remove.Should().Throw<ArgumentException>().WithMessage("*End it instead*");
        _data.Bills().Should().Contain(b => b.Id == gym.Id);
    }

    [Fact]
    public void A_debt_payment_is_changed_not_ended_or_removed()
    {
        var loan = _entry.AddLoan(new LoanEntry("Car loan", 9_800m, Today, 7.4m, 285m, 28), _checking.Id);
        var bill = _data.Bills().Single(b => b.TargetAccount == loan.Account);

        var end = () => _entry.EndBill(bill, Today);
        var remove = () => _entry.RemoveBill(bill);

        end.Should().Throw<ArgumentException>();
        remove.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("", 10, 1)]
    [InlineData("Gym", 0, 1)]
    [InlineData("Gym", 10.001, 1)]
    [InlineData("Gym", 10, 32)]
    public void An_edit_is_checked_like_a_new_bill(string name, decimal amount, int day)
    {
        var gym = _entry.AddBill(new BillEntry("Gym", 40m, 12), _checking.Id);

        var update = () => _entry.UpdateBill(gym, new BillEntry(name, amount, day));

        update.Should().Throw<ArgumentException>();
        _data.Bills().Single(b => b.Id == gym.Id).Should().Be(gym);
    }
}
