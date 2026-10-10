using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.Loans;

namespace Mossmint.Application.Tests;

public class ForecastTests
{
    // A Friday. Paydays fall on 15 Oct, 30 Oct (31 Oct is a Saturday) and 13 Nov.
    private static readonly DateOnly Today = new(2026, 10, 9);
    private readonly InMemoryProfileData _data = new();
    private readonly ManualEntry _entry;
    private readonly Account _checking;

    public ForecastTests()
    {
        _entry = new ManualEntry(_data, new FixedClock(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero)));
        _entry.AddCashAccount("Savings", 5_000m, Today, AccountKind.Savings);
        _checking = _entry.AddCashAccount("Checking", 1_240m, Today.AddDays(-1));
        _entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), _checking.Id);
        _entry.AddBill(new BillEntry("Rent", 1_150m, 1), _checking.Id);
        _entry.SetSavings(150m, 300m);
    }

    [Fact]
    public void No_cash_account_means_no_forecast_and_says_what_to_add()
    {
        var outcome = Forecasts.Build(new InMemoryProfileData(), Today);

        outcome.Should().BeOfType<ForecastUnavailable>().Which.Missing.Should().ContainSingle().Which.Should().Contain("checking account");
    }

    [Fact]
    public void The_forecast_follows_the_account_paychecks_land_in()
    {
        var forecast = Build();

        forecast.AccountName.Should().Be("Checking");
        forecast.Opening.Should().Be(Money.Usd(1_240m));
        forecast.Days.Should().HaveCount(Forecasts.DefaultDays);
        forecast.Days[0].Date.Should().Be(Today);
        forecast.Days.Where(d => d.IsPayday).Select(d => d.Date).Should().Equal(
            new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 13));
        forecast.Cushion.Should().Be(Money.Usd(300m));
    }

    [Fact]
    public void Safe_to_spend_runs_to_the_day_before_payday_and_keeps_the_cushion()
    {
        var safe = Build().SafeToSpend;

        // Nothing is due before the 15th, so the low is the opening 1,240.00, less the 300.00 cushion.
        safe.State.Should().Be(SafeToSpendState.Safe);
        safe.Amount.Should().Be(Money.Usd(940m));
        safe.Through.Should().Be(new DateOnly(2026, 10, 14));
    }

    [Fact]
    public void Safe_to_spend_waits_for_a_confirmed_balance()
    {
        _data.RecordBalance(new BalanceSnapshot(_checking.Id, Money.Usd(1_100m), Provenance.Imported(Today)));

        var forecast = Build();

        forecast.Opening.Should().Be(Money.Usd(1_100m));
        forecast.SafeToSpend.State.Should().Be(SafeToSpendState.NeedsConfirmation);
        forecast.SafeToSpend.Amount.Should().BeNull();
        forecast.SafeToSpend.Reasons.Should().ContainSingle().Which.Should().Contain("Confirm the checking balance");
    }

    [Fact]
    public void Each_paycheck_lists_the_bills_it_funds()
    {
        _entry.AddLoan(new LoanEntry("Car loan", 9_800m, Today, 7.4m, 285m, 28), _checking.Id);

        var paychecks = Build().Paychecks;

        // Nothing is due before the 15th, so the cash on hand carries over whole.
        var onHand = paychecks[0];
        (onHand.Payday, onHand.Until, onHand.BillCount, onHand.Leftover).Should().Be(((DateOnly?)null, new DateOnly(2026, 10, 14), 0, Money.Usd(1_240m)));

        // The loan payment on the 28th is funded by the 15 Oct paycheck; rent on 1 Nov by the 30 Oct one.
        var paycheck = paychecks[1];
        paycheck.Payday.Should().Be(new DateOnly(2026, 10, 15));
        paycheck.Until.Should().Be(new DateOnly(2026, 10, 29));
        (paycheck.CarryIn, paycheck.NetPay).Should().Be((Money.Usd(1_240m), Money.Usd(2_050m)));
        paycheck.Funded.Should().ContainSingle().Which.Due.Should().Be(new DateOnly(2026, 10, 28));
        (paycheck.BillCount, paycheck.Bills).Should().Be((1, Money.Usd(285m)));
        paycheck.SavingsFunded.Should().Be(Money.Usd(150m));

        // 1,240 + 2,050 − 285 − 150. The low is after the loan payment, not the morning before pay.
        paycheck.Leftover.Should().Be(Money.Usd(2_855m));
        (paycheck.LowestDate, paycheck.Lowest).Should().Be((new DateOnly(2026, 10, 28), Money.Usd(2_855m)));
    }

    [Fact]
    public void A_payday_today_has_no_cash_on_hand_period()
    {
        var paychecks = Forecasts.Build(_data, new DateOnly(2026, 10, 15)).Should().BeOfType<ProfileForecast>().Subject.Paychecks;

        paychecks[0].Payday.Should().Be(new DateOnly(2026, 10, 15));
        paychecks[0].CarryIn.Should().Be(Money.Usd(1_240m));
    }

    [Fact]
    public void Debts_are_projected_from_their_latest_balance_and_payment()
    {
        var loan = _entry.AddLoan(new LoanEntry("Car loan", 9_800m, Today, 7.4m, 285m, 28), _checking.Id);
        _entry.AddCard(new CardEntry("Card", 2_300m, Today, 24.99m, 150m, 21, 3), _checking.Id);
        var expected = SimpleInterestLoan.Project(new SimpleInterestLoanInput(
            new LoanPosition(Today, Money.Usd(9_800m), Money.Usd(0m), Money.Usd(0m), Provenance.UserConfirmed(Today)),
            (SimpleInterestLoanTerms)loan.Terms,
            CalendarSpec.UsFederalReserve)).Outcome.Should().BeOfType<PaysOff>().Subject;

        var forecast = Build();

        var car = forecast.Debts.Single(d => d.Name == "Car loan");
        (car.PayoffDate, car.InterestLeft, car.IsApproximate).Should().Be(((DateOnly?)expected.PayoffDate, (Money?)expected.TotalInterest, false));
        var card = forecast.Debts.Single(d => d.Name == "Card");
        card.PayoffDate.Should().NotBeNull();
        card.IsApproximate.Should().BeTrue();
        forecast.DebtFree.Should().Be(new[] { car.PayoffDate!.Value, card.PayoffDate!.Value }.Max());
    }

    [Fact]
    public void A_card_without_a_planned_payment_has_no_payoff_date_and_neither_does_debt_free()
    {
        _entry.AddCard(new CardEntry("Card", 2_300m, Today, 24.99m, 0m, 21, 3), _checking.Id);

        var forecast = Build();

        var card = forecast.Debts.Single();
        card.PayoffDate.Should().BeNull();
        card.Problem.Should().Contain("planned payment");
        forecast.DebtFree.Should().BeNull();
    }

    [Fact]
    public void Marking_a_bill_paid_takes_it_off_the_balance_and_out_of_the_forecast_until_undone()
    {
        var rent = Forecasts.UpcomingBills(_data, Today).Single(b => b.Bill.Name == "Rent");
        rent.Upcoming.Select(d => d.Nominal).Should().Equal(new DateOnly(2026, 11, 1));

        var payment = _entry.MarkPaid(rent.Bill, rent.Upcoming[0].Nominal);

        // Paid today, after the 8 Oct balance: 1,240 − 1,150 leaves 90, under the 300 cushion.
        payment.Amount.Should().Be(Money.Usd(-1_150m));
        var paid = Build();
        (paid.Opening, paid.EntriesSince).Should().Be((Money.Usd(90m), 1));
        paid.Days.SelectMany(d => d.Events).Should().NotContain(e => e.Label == "Rent");
        (paid.SafeToSpend.State, paid.SafeToSpend.Amount).Should().Be((SafeToSpendState.Shortfall, Money.Usd(210m)));
        Forecasts.UpcomingBills(_data, Today).Single(b => b.Bill.Name == "Rent").Upcoming[0].Payment.Should().Be(payment);
        FluentActions.Invoking(() => _entry.MarkPaid(rent.Bill, rent.Upcoming[0].Nominal)).Should().Throw<ArgumentException>().WithMessage("*already*");

        _entry.Undo(payment);

        var undone = Build();
        (undone.Opening, undone.EntriesSince).Should().Be((Money.Usd(1_240m), 0));
        undone.Days.SelectMany(d => d.Events).Should().Contain(e => e.Label == "Rent");
    }

    [Fact]
    public void A_balance_confirmed_after_paying_already_includes_the_payment()
    {
        var rent = Forecasts.UpcomingBills(_data, Today).Single(b => b.Bill.Name == "Rent");
        _entry.MarkPaid(rent.Bill, rent.Upcoming[0].Nominal);

        _entry.ConfirmBalance(_checking.Id, 90m);

        var forecast = Build();
        (forecast.Opening, forecast.EntriesSince, forecast.OpeningProvenance.AsOf).Should().Be((Money.Usd(90m), 0, Today));
        forecast.SafeToSpend.State.Should().Be(SafeToSpendState.Shortfall);
    }

    [Fact]
    public void A_confirmed_debt_balance_restarts_its_payoff_projection()
    {
        var loan = _entry.AddLoan(new LoanEntry("Car loan", 9_800m, Today.AddDays(-40), 7.4m, 285m, 28), _checking.Id);

        _entry.ConfirmBalance(loan.Account, 9_500m, 12.40m);

        var car = Build().Debts.Single();
        (car.Account, car.Balance, car.BalanceAsOf).Should().Be((loan.Account, (Money?)Money.Usd(9_500m), (DateOnly?)Today));
        FluentActions.Invoking(() => _entry.ConfirmBalance(loan.Account, 9_500m, -1m)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_dated_goal_is_spread_over_the_paydays_before_it_rounded_up()
    {
        _entry.AddGoal("Car fund", 3_000m, 1_250.50m, new DateOnly(2027, 5, 1));
        _entry.AddGoal("Someday", 500m);
        var reached = _entry.AddGoal("Done", 100m, 100m);

        var goals = Forecasts.Goals(_data, Today).ToDictionary(g => g.Goal.Name);

        // 15 Oct to 30 Apr, moved off weekends and holidays (12 Feb for Presidents' Day): 14 paydays.
        // 1,749.50 / 14 = 124.964..., rounded up to 124.97.
        var car = goals["Car fund"];
        (car.Left, car.PaydaysLeft, car.PerPaycheck).Should().Be((Money.Usd(1_749.50m), 14, (Money?)Money.Usd(124.97m)));
        (goals["Someday"].PerPaycheck, goals["Someday"].IsReached).Should().Be(((Money?)null, false));
        goals["Done"].IsReached.Should().BeTrue();

        _entry.UpdateSaved(reached, 40m);
        Forecasts.Goals(_data, Today).Single(g => g.Goal.Name == "Done").Left.Should().Be(Money.Usd(60m));
        FluentActions.Invoking(() => _entry.AddGoal("Past", 100m, by: Today)).Should().Throw<ArgumentException>();
    }

    private ProfileForecast Build() => Forecasts.Build(_data, Today).Should().BeOfType<ProfileForecast>().Subject;
}
