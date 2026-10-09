using FsCheck;
using FsCheck.Fluent;
using Mossmint.Domain;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.Loans;

namespace Mossmint.Engine.Tests.Loans;

public class AmortizingLoanTests
{
    private static readonly DateOnly AsOf = new(2027, 1, 1);

    private static AmortizingLoanInput Loan(
        decimal principal,
        decimal aprPercent,
        decimal? payment = null,
        int? remaining = null,
        RateSchedule? rates = null,
        IReadOnlyList<ExtraPayment>? extras = null) =>
        new(
            new LoanPosition(AsOf, Money.Usd(principal), Money.Usd(0m), Money.Usd(0m), Provenance.LenderStatement(AsOf)),
            new AmortizingLoanTerms(
                rates ?? RateSchedule.Fixed(AnnualRate.FromPercent(aprPercent), new DateOnly(2020, 1, 1)),
                12,
                new Schedule(new MonthlyRule(DayOfMonth.Of(1)), StartsOn: AsOf.AddDays(1)),
                payment is null ? null : Money.Usd(payment.Value),
                remaining),
            CalendarSpec.WeekendsOnly,
            extras);

    [Fact]
    public void Level_payment_matches_the_annuity_formula()
    {
        // 10,000 at 6% over 12 months: 50 / (1 - 1.005^-12) = 860.664... -> rounded up to 860.67.
        AmortizingLoan.LevelPayment(Money.Usd(10_000m), 0.005m, 12).Should().Be(Money.Usd(860.67m));
        AmortizingLoan.LevelPayment(Money.Usd(1_000m), 0m, 3).Should().Be(Money.Usd(333.34m));
    }

    [Fact]
    public void First_periods_match_hand_worked_values()
    {
        // Period 1: 10,000.00 x 0.005 = 50.00 interest; 810.67 principal -> 9,189.33.
        // Period 2:  9,189.33 x 0.005 = 45.94665 -> 45.95; 814.72 principal -> 8,374.61.
        var rows = AmortizingLoan.Project(Loan(10_000m, 6m, remaining: 12)).Rows;

        rows.Take(2).Select(r => (r.NewInterest.Amount, r.ToPrincipal.Amount, r.EndPrincipal.Amount))
            .Should().Equal((50.00m, 810.67m, 9_189.33m), (45.95m, 814.72m, 8_374.61m));
    }

    [Fact]
    public void A_derived_level_payment_pays_off_in_the_stated_term_with_a_smaller_last_payment()
    {
        var projection = AmortizingLoan.Project(Loan(10_000m, 6m, remaining: 12));

        var paysOff = projection.Outcome.Should().BeOfType<PaysOff>().Subject;
        paysOff.PaymentCount.Should().Be(12);
        projection.Rows[^1].Payment.Should().BeLessThan(projection.Rows[0].Payment);
        projection.Rows.Sum(r => r.ToPrincipal.Amount).Should().Be(10_000m);
    }

    [Fact]
    public void A_scheduled_payment_below_the_interest_never_pays_off()
    {
        // 20,000 at 12% charges 200.00 a month; 150.00 can't keep up.
        var outcome = AmortizingLoan.Project(Loan(20_000m, 12m, payment: 150m)).Outcome;

        outcome.Should().BeOfType<NeverPaysOff>().Which.ShortfallPerPayment.Should().Be(Money.Usd(50m));
    }

    [Fact]
    public void A_rate_change_re_derives_the_level_payment()
    {
        var rates = new RateSchedule([
            new RateSegment(new DateOnly(2020, 1, 1), AnnualRate.FromPercent(6m)),
            new RateSegment(new DateOnly(2027, 7, 1), AnnualRate.FromPercent(9m), RateKind.Variable),
        ]);

        var rows = AmortizingLoan.Project(Loan(10_000m, 0m, remaining: 24, rates: rates)).Rows;

        // July 1 is the first payment at 9%.
        rows[5].Date.Should().Be(new DateOnly(2027, 7, 1));
        rows[5].Payment.Should().BeGreaterThan(rows[4].Payment);
        rows[6].Payment.Should().Be(rows[5].Payment);
        rows.Where(r => r.Kind == LoanPaymentKind.Regular).Should().HaveCount(24);
    }

    [Fact]
    public void Extra_payments_shorten_the_loan()
    {
        var baseline = (PaysOff)AmortizingLoan.Project(Loan(10_000m, 6m, payment: 500m)).Outcome;
        var withExtra = (PaysOff)AmortizingLoan.Project(Loan(10_000m, 6m, payment: 500m, extras: [new ExtraPayment(new DateOnly(2027, 3, 10), Money.Usd(2_000m))])).Outcome;

        withExtra.PayoffDate.Should().BeBefore(baseline.PayoffDate);
        withExtra.TotalInterest.Should().BeLessThan(baseline.TotalInterest);
    }

    [Fact]
    public void Missing_payment_and_term_is_insufficient_data()
    {
        AmortizingLoan.Project(Loan(10_000m, 6m)).Outcome.Should().BeOfType<InsufficientData>();
    }

    private static readonly Arbitrary<(decimal Principal, decimal Apr, int Months)> Loans =
        (from principalCents in Gen.Choose(10_000, 10_000_000)
         from aprBasisPoints in Gen.Choose(0, 2_500)
         from months in Gen.Choose(1, 360)
         select (principalCents / 100m, aprBasisPoints / 100m, months)).ToArbitrary();

    [Fact]
    public void Derived_payments_always_retire_the_principal_within_the_term() =>
        Prop.ForAll(Loans, loan =>
        {
            var projection = AmortizingLoan.Project(Loan(loan.Principal, loan.Apr, remaining: loan.Months));
            var paysOff = (PaysOff)projection.Outcome;

            return paysOff.PaymentCount <= loan.Months
                && projection.Rows.Sum(r => r.ToPrincipal.Amount) == loan.Principal
                && projection.Rows.All(r => !r.EndPrincipal.IsNegative && r.Payment == r.ToInterest + r.ToPrincipal + r.ToFees)
                && paysOff.TotalPaid == Money.Usd(loan.Principal) + paysOff.TotalInterest;
        }).QuickCheckThrowOnFailure();
}
