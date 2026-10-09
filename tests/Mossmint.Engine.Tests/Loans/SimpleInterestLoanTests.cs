using FsCheck;
using FsCheck.Fluent;
using Mossmint.Domain;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.Loans;

namespace Mossmint.Engine.Tests.Loans;

public class SimpleInterestLoanTests
{
    private static readonly DateOnly AsOf = new(2027, 1, 1);

    private static Money Usd(decimal amount) => Money.Usd(amount);

    private static SimpleInterestLoanInput Loan(
        decimal principal,
        decimal aprPercent,
        decimal payment,
        DayCountConvention dayCount = DayCountConvention.Actual365Fixed,
        RateSchedule? rates = null,
        decimal accrued = 0m,
        decimal fees = 0m,
        IReadOnlyList<ExtraPayment>? extras = null,
        IReadOnlyList<PlannedPaymentAmount>? planned = null,
        PrepaymentPenalty? penalty = null,
        bool principalOnlyAllowed = true,
        DateOnly? asOf = null) =>
        new(
            new LoanPosition(asOf ?? AsOf, Usd(principal), Usd(accrued), Usd(fees), Provenance.LenderStatement(asOf ?? AsOf)),
            new SimpleInterestLoanTerms(
                rates ?? RateSchedule.Fixed(AnnualRate.FromPercent(aprPercent), new DateOnly(2020, 1, 1)),
                dayCount,
                Usd(payment),
                new Schedule(new MonthlyRule(DayOfMonth.Of(1)), StartsOn: (asOf ?? AsOf).AddDays(1)),
                AllowsPrincipalOnlyPayments: principalOnlyAllowed,
                Penalty: penalty),
            CalendarSpec.WeekendsOnly,
            planned,
            extras);

    [Fact]
    public void First_three_periods_match_hand_worked_values()
    {
        // 1,000.00 at 12% (Actual/365), 100.00 on the 1st of each month from Feb 2027.
        // Feb 1: 1000.00 x 0.12/365 x 31 = 10.1917... -> 10.19; principal 89.81 -> 910.19
        // Mar 1:  910.19 x 0.12/365 x 28 =  8.3787... ->  8.38; principal 91.62 -> 818.57
        // Apr 1:  818.57 x 0.12/365 x 31 =  8.3426... ->  8.34; principal 91.66 -> 726.91
        var rows = SimpleInterestLoan.Project(Loan(1000m, 12m, 100m)).Rows;

        rows.Take(3).Select(r => (r.Date, r.Days, r.NewInterest.Amount, r.ToPrincipal.Amount, r.EndPrincipal.Amount))
            .Should().Equal(
                (new DateOnly(2027, 2, 1), 31, 10.19m, 89.81m, 910.19m),
                (new DateOnly(2027, 3, 1), 28, 8.38m, 91.62m, 818.57m),
                (new DateOnly(2027, 4, 1), 31, 8.34m, 91.66m, 726.91m));
    }

    [Fact]
    public void Full_schedule_matches_an_independent_oracle()
    {
        var projection = SimpleInterestLoan.Project(Loan(2500m, 18.9m, 225m));

        var oracle = Oracle(principal: 2500m, apr: 0.189m, payment: 225m, firstPayment: new DateOnly(2027, 2, 1));
        projection.Rows.Select(r => (r.Date, r.NewInterest.Amount, r.Payment.Amount, r.EndPrincipal.Amount))
            .Should().Equal(oracle);
        projection.Outcome.Should().BeOfType<PaysOff>().Which.PayoffDate.Should().Be(oracle[^1].Date);
    }

    [Fact]
    public void Zero_apr_pays_off_with_a_clamped_final_payment()
    {
        var projection = SimpleInterestLoan.Project(Loan(250m, 0m, 100m));

        projection.Rows.Select(r => r.Payment.Amount).Should().Equal(100m, 100m, 50m);
        projection.Outcome.Should().Be(new PaysOff(new DateOnly(2027, 4, 1), 3, Usd(250m), Usd(0m), Usd(0m)));
    }

    [Fact]
    public void A_payment_below_the_interest_never_pays_off()
    {
        // 10,000 at 24% accrues about 197-204 a month; 150 can't keep up.
        var projection = SimpleInterestLoan.Project(Loan(10_000m, 24m, 150m));

        var outcome = projection.Outcome.Should().BeOfType<NeverPaysOff>().Subject;
        outcome.FirstShortfallDate.Should().Be(new DateOnly(2027, 2, 1));
        outcome.ShortfallPerPayment!.Value.Amount.Should().BeGreaterThan(40m);
        projection.Rows.Should().HaveCountLessThan(10);
    }

    [Fact]
    public void A_payment_equal_to_the_interest_never_pays_off()
    {
        // 36,500 at 10% (Actual/365) accrues exactly 10.00 a day, so 70.00 a week only covers interest.
        var input = Loan(36_500m, 10m, 70m);
        input = input with { Terms = input.Terms with { PaymentDates = new Schedule(new WeeklyRule(new DateOnly(2027, 1, 8)), StartsOn: AsOf.AddDays(1)) } };

        var outcome = SimpleInterestLoan.Project(input).Outcome.Should().BeOfType<NeverPaysOff>().Subject;

        outcome.ShortfallPerPayment.Should().Be(Usd(0m));
        outcome.FirstShortfallDate.Should().Be(new DateOnly(2027, 1, 8));
    }

    [Theory]
    [InlineData(DayCountConvention.Actual365Fixed, 95.34)] // 10,000 x 0.12/365 x 29
    [InlineData(DayCountConvention.ActualActual, 95.08)] // 10,000 x 0.12/366 x 29
    [InlineData(DayCountConvention.Actual360, 96.67)] // 10,000 x 0.12/360 x 29
    public void Leap_february_interest_follows_the_day_count(DayCountConvention dayCount, double expected)
    {
        var input = Loan(10_000m, 12m, 500m, dayCount, asOf: new DateOnly(2028, 2, 1));

        SimpleInterestLoan.Project(input).Rows[0].NewInterest.Amount.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData(DayCountConvention.ActualActual, 101.79)] // 17 days /365 (55.890...) + 14 days /366 (45.901...)
    [InlineData(DayCountConvention.Actual365Fixed, 101.92)] // 31 days /365
    public void Actual_actual_splits_a_period_at_the_new_year(DayCountConvention dayCount, double expected)
    {
        // Dec 15 2027 -> Jan 15 2028 on 10,000 at 12%.
        var input = Loan(10_000m, 12m, 500m, dayCount, asOf: new DateOnly(2027, 12, 15));
        input = input with { Terms = input.Terms with { PaymentDates = new Schedule(new MonthlyRule(DayOfMonth.Of(15)), StartsOn: new DateOnly(2027, 12, 16)) } };

        var first = SimpleInterestLoan.Project(input).Rows[0];

        first.Date.Should().Be(new DateOnly(2028, 1, 15));
        first.NewInterest.Amount.Should().Be((decimal)expected);
    }

    [Fact]
    public void A_rate_change_mid_period_splits_the_interest()
    {
        // 15 days at 12% (49.315...) + 16 days at 24% (105.205...) = 154.520... -> 154.52
        var rates = new RateSchedule([
            new RateSegment(new DateOnly(2020, 1, 1), AnnualRate.FromPercent(12m)),
            new RateSegment(new DateOnly(2027, 1, 16), AnnualRate.FromPercent(24m), RateKind.Variable),
        ]);

        var projection = SimpleInterestLoan.Project(Loan(10_000m, 0m, 500m, rates: rates));

        projection.Rows[0].NewInterest.Amount.Should().Be(154.52m);
    }

    [Fact]
    public void Assumed_future_rates_lower_the_confidence_and_say_so()
    {
        var rates = new RateSchedule([
            new RateSegment(new DateOnly(2020, 1, 1), AnnualRate.FromPercent(12m)),
            new RateSegment(new DateOnly(2027, 6, 1), AnnualRate.FromPercent(14m), RateKind.Variable, IsAssumption: true),
        ]);

        var projection = SimpleInterestLoan.Project(Loan(5_000m, 0m, 500m, rates: rates));

        projection.Confidence.Should().Be(Confidence.Estimated);
        projection.Assumptions.Should().Contain(a => a.Contains("assumptions"));
    }

    [Fact]
    public void A_lender_statement_projection_is_at_best_scheduled()
    {
        SimpleInterestLoan.Project(Loan(5_000m, 12m, 500m)).Confidence.Should().Be(Confidence.Scheduled);
    }

    [Fact]
    public void Fees_are_paid_before_interest_and_principal()
    {
        var row = SimpleInterestLoan.Project(Loan(1000m, 12m, 100m, fees: 25m)).Rows[0];

        (row.ToFees.Amount, row.ToInterest.Amount, row.ToPrincipal.Amount).Should().Be((25m, 10.19m, 64.81m));
    }

    [Fact]
    public void Carried_unpaid_interest_is_paid_first_and_never_compounds()
    {
        // 40.00 of interest carried in; new interest still accrues on principal only.
        var row = SimpleInterestLoan.Project(Loan(1000m, 12m, 100m, accrued: 40m)).Rows[0];

        (row.NewInterest.Amount, row.ToInterest.Amount, row.ToPrincipal.Amount).Should().Be((10.19m, 50.19m, 49.81m));
    }

    [Fact]
    public void An_extra_principal_payment_lowers_later_interest_and_charges_none_itself()
    {
        var baseline = SimpleInterestLoan.Project(Loan(5_000m, 15m, 300m));
        var withExtra = SimpleInterestLoan.Project(Loan(5_000m, 15m, 300m, extras: [new ExtraPayment(new DateOnly(2027, 2, 15), Usd(1_000m))]));

        var extraRow = withExtra.Rows.Single(r => r.Kind == LoanPaymentKind.Extra);
        extraRow.NewInterest.Should().Be(Usd(0m));
        extraRow.ToPrincipal.Should().Be(Usd(1_000m));

        var paidOff = withExtra.Outcome.Should().BeOfType<PaysOff>().Subject;
        paidOff.TotalInterest.Should().BeLessThan(((PaysOff)baseline.Outcome).TotalInterest);
        paidOff.PayoffDate.Should().BeBefore(((PaysOff)baseline.Outcome).PayoffDate);
    }

    [Fact]
    public void A_prepayment_penalty_is_charged_as_a_fee_inside_its_window()
    {
        var penalty = new PrepaymentPenalty(0.02m, new DateOnly(2027, 6, 30));
        var projection = SimpleInterestLoan.Project(Loan(5_000m, 15m, 300m, extras: [new ExtraPayment(new DateOnly(2027, 2, 15), Usd(1_000m))], penalty: penalty));

        projection.Rows.Single(r => r.Kind == LoanPaymentKind.Extra).NewFees.Should().Be(Usd(20m));
        projection.Rows.First(r => r.Date == new DateOnly(2027, 3, 1)).ToFees.Should().Be(Usd(20m));
    }

    [Fact]
    public void Without_principal_only_payments_an_extra_pays_interest_first()
    {
        var projection = SimpleInterestLoan.Project(Loan(5_000m, 15m, 300m, extras: [new ExtraPayment(new DateOnly(2027, 2, 15), Usd(1_000m))], principalOnlyAllowed: false));

        var extra = projection.Rows.Single(r => r.Kind == LoanPaymentKind.Extra);
        extra.ToInterest.Amount.Should().BeGreaterThan(0m);
        (extra.ToInterest + extra.ToPrincipal).Should().Be(Usd(1_000m));
    }

    [Fact]
    public void Planned_amounts_replace_and_skip_regular_payments()
    {
        var planned = new[]
        {
            new PlannedPaymentAmount(new DateOnly(2027, 2, 1), Usd(400m)),
            new PlannedPaymentAmount(new DateOnly(2027, 3, 1), Usd(0m)),
        };

        var rows = SimpleInterestLoan.Project(Loan(5_000m, 15m, 300m, planned: planned)).Rows;

        rows[0].Payment.Should().Be(Usd(400m));
        rows[1].Date.Should().Be(new DateOnly(2027, 4, 1));
        rows[1].Days.Should().Be(59);
    }

    [Fact]
    public void Missing_inputs_give_insufficient_data_not_a_date()
    {
        var noPayment = SimpleInterestLoan.Project(Loan(5_000m, 15m, 0m));
        var noRate = SimpleInterestLoan.Project(Loan(5_000m, 15m, 300m, rates: RateSchedule.Fixed(AnnualRate.FromPercent(15m), new DateOnly(2027, 6, 1))));

        noPayment.Outcome.Should().BeOfType<InsufficientData>().Which.Missing.Should().Contain(m => m.Contains("regular payment"));
        noRate.Outcome.Should().BeOfType<InsufficientData>().Which.Missing.Should().Contain(m => m.Contains("interest rate"));
        noPayment.Rows.Should().BeEmpty();
    }

    [Fact]
    public void A_loan_with_nothing_owed_is_already_paid_off()
    {
        SimpleInterestLoan.Project(Loan(0m, 15m, 300m)).Outcome.Should().Be(new PaysOff(AsOf, 0, Usd(0m), Usd(0m), Usd(0m)));
    }

    // Loans that clearly pay off: the payment covers a month's interest with room to spare.
    private static readonly Arbitrary<(decimal Principal, decimal Apr, decimal Payment)> PayingLoans =
        (from principalCents in Gen.Choose(10_000, 5_000_000)
         from aprBasisPoints in Gen.Choose(0, 3_000)
         from extraCents in Gen.Choose(1_000, 200_000)
         let principal = principalCents / 100m
         let apr = aprBasisPoints / 100m
         let monthlyInterest = Math.Ceiling(principal * apr / 100m / 12m * 1.2m * 100m) / 100m
         let paydown = Math.Max(extraCents / 100m, Math.Ceiling(principal / 240m))
         select (principal, apr, monthlyInterest + paydown)).ToArbitrary();

    [Fact]
    public void Every_paid_off_schedule_conserves_money() =>
        Prop.ForAll(PayingLoans, loan =>
        {
            var projection = SimpleInterestLoan.Project(Loan(loan.Principal, loan.Apr, loan.Payment));
            var paysOff = (PaysOff)projection.Outcome;
            var rows = projection.Rows;

            return rows.Sum(r => r.ToPrincipal.Amount) == loan.Principal
                && rows.Sum(r => r.ToInterest.Amount) == rows.Sum(r => r.NewInterest.Amount)
                && rows.All(r => r.Payment == r.ToFees + r.ToInterest + r.ToPrincipal)
                && rows.All(r => !r.EndPrincipal.IsNegative && !r.EndAccruedInterest.IsNegative && !r.NewInterest.IsNegative)
                && rows.Zip(rows.Skip(1)).All(p => p.First.Date <= p.Second.Date)
                && rows[^1].EndOwed.IsZero
                && paysOff.TotalPaid.Amount == rows.Sum(r => r.Payment.Amount)
                && paysOff.TotalPaid == Usd(loan.Principal) + paysOff.TotalInterest;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void An_extra_principal_payment_never_raises_total_interest() =>
        Prop.ForAll(PayingLoans, Gen.Choose(1, 100_000).ToArbitrary(), Gen.Choose(0, 120).ToArbitrary(), (loan, extraCents, dayOffset) =>
        {
            var extra = new ExtraPayment(AsOf.AddDays(dayOffset), Usd(extraCents / 100m));
            var baseline = (PaysOff)SimpleInterestLoan.Project(Loan(loan.Principal, loan.Apr, loan.Payment)).Outcome;
            var withExtra = (PaysOff)SimpleInterestLoan.Project(Loan(loan.Principal, loan.Apr, loan.Payment, extras: [extra])).Outcome;

            return withExtra.TotalInterest <= baseline.TotalInterest && withExtra.PayoffDate <= baseline.PayoffDate;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void Projections_are_deterministic() =>
        Prop.ForAll(PayingLoans, loan =>
        {
            var input = Loan(loan.Principal, loan.Apr, loan.Payment);
            return SimpleInterestLoan.Project(input).Rows.SequenceEqual(SimpleInterestLoan.Project(input).Rows);
        }).QuickCheckThrowOnFailure();

    // An independent, deliberately plain implementation for monthly payments on
    // the 1st with no fees, extras or rate changes: days by DateTime subtraction,
    // interest by P x APR x days / 365 rounded half away from zero.
    private static List<(DateOnly Date, decimal Interest, decimal Payment, decimal Principal)> Oracle(
        decimal principal, decimal apr, decimal payment, DateOnly firstPayment)
    {
        var result = new List<(DateOnly, decimal, decimal, decimal)>();
        var previous = AsOf.ToDateTime(TimeOnly.MinValue);
        var due = firstPayment.ToDateTime(TimeOnly.MinValue);
        while (principal > 0m)
        {
            var days = (decimal)(due - previous).TotalDays;
            var interest = Math.Round(principal * (apr / 365m) * days, 2, MidpointRounding.AwayFromZero);
            var paid = Math.Min(payment, principal + interest);
            principal -= paid - interest;
            result.Add((DateOnly.FromDateTime(due), interest, paid, principal));
            previous = due;
            due = due.AddMonths(1);
        }

        return result;
    }
}
