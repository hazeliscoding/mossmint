using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.Cards;
using Mossmint.Engine.Loans;
using Mossmint.Engine.Payoff;

namespace Mossmint.Engine.Tests.Payoff;

public class PayoffPlannerTests
{
    private static readonly DateOnly AsOf = new(2027, 1, 1);
    private static readonly DateOnly LongAgo = new(2020, 1, 1);

    private static AccountId Id(int n) => new(new Guid(n, 0, 0, new byte[8]));

    // Day-of-month dates from the first one on, through 2035.
    private static List<PlannedPayment> Monthly(DateOnly first, decimal amount) =>
        [.. Enumerable.Range(0, 108).Select(i => new PlannedPayment(first.AddMonths(i), Money.Usd(amount)))];

    // A loan charged APR / 12 on the 1st of each month from February. Its bill
    // pays the same amount on the same days.
    private static PayoffDebt Loan(int id, decimal principal, decimal payment, decimal aprPercent = 0m) =>
        new(
            Id(id),
            new AmortizingLoanInput(
                new LoanPosition(AsOf, Money.Usd(principal), Money.Usd(0m), Money.Usd(0m), Provenance.LenderStatement(AsOf)),
                new AmortizingLoanTerms(
                    RateSchedule.Fixed(AnnualRate.FromPercent(aprPercent), LongAgo),
                    12,
                    new Schedule(new MonthlyRule(DayOfMonth.Of(1)), StartsOn: AsOf.AddDays(1)),
                    Money.Usd(payment)),
                CalendarSpec.WeekendsOnly),
            Monthly(new DateOnly(2027, 2, 1), payment));

    private static DateOnly? PayoffOf(PayoffPlan plan, int id) =>
        plan.Debts.Single(d => d.Account == Id(id)).Outcome is PaysOff p ? p.PayoffDate : null;

    [Fact]
    public void Avalanche_takes_the_highest_rate_first_and_breaks_ties_with_the_smaller_balance()
    {
        // The card's balance sits in its 0% promo bucket, so 0% is the rate it's charged today.
        var card = new PayoffDebt(
            Id(5),
            new CreditCardInput(
                new CardPosition(AsOf, [new CardBucketBalance("promo", Money.Usd(800m))], Money.Usd(0m), Provenance.LenderStatement(AsOf)),
                new CreditCardTerms(
                    DayOfMonth.Of(9),
                    PaymentDueRule.AfterClose(25),
                    [
                        new AprBucket("purchase", BalanceBucketKind.Purchase, RateSchedule.Fixed(AnnualRate.FromPercent(24m), LongAgo)),
                        new AprBucket("promo", BalanceBucketKind.Promotional, RateSchedule.Fixed(AnnualRate.Zero, LongAgo)),
                    ],
                    new MinimumPaymentRule(Money.Usd(35m), 0.01m, PlusInterestAndFees: true)),
                Money.Usd(50m)),
            []);
        PayoffDebt[] debts = [Loan(1, 5_000m, 100m, 6m), Loan(2, 3_000m, 100m, 15m), Loan(3, 1_000m, 100m, 15m), Loan(4, 1_000m, 100m, 20m), card];

        PayoffPlanner.Avalanche(debts).Should().Equal(Id(4), Id(3), Id(2), Id(1), Id(5));
    }

    [Fact]
    public void Snowball_takes_the_smallest_balance_first_and_breaks_ties_with_the_higher_rate()
    {
        PayoffDebt[] debts = [Loan(1, 5_000m, 100m, 6m), Loan(2, 3_000m, 100m, 15m), Loan(3, 1_000m, 100m, 15m), Loan(4, 1_000m, 100m, 20m)];

        PayoffPlanner.Snowball(debts).Should().Equal(Id(4), Id(3), Id(2), Id(1));
    }

    [Fact]
    public void Todays_plan_pays_each_debt_on_its_own()
    {
        // A: 300 / 100 a month -> Feb 1, Mar 1, Apr 1. B: 1,000 / 100 -> ten payments, Feb 1 to Nov 1.
        var plan = PayoffPlanner.Baseline([Loan(1, 300m, 100m), Loan(2, 1_000m, 100m)]);

        (PayoffOf(plan, 1), PayoffOf(plan, 2), plan.DebtFree).Should().Be((new DateOnly(2027, 4, 1), new DateOnly(2027, 11, 1), new DateOnly(2027, 11, 1)));
    }

    [Fact]
    public void A_paid_off_debts_payment_moves_to_the_next_debt()
    {
        // A is paid off on Apr 1. From May 1 its 100 goes to B as well:
        // B 1,000 -> 900 (Feb) -> 800 (Mar) -> 700 (Apr) -> 500 (May) -> 300 (Jun) -> 100 (Jul) -> 0 (Aug 1).
        var plan = PayoffPlanner.Plan([Loan(1, 300m, 100m), Loan(2, 1_000m, 100m)], [Id(1), Id(2)], []);

        (PayoffOf(plan, 1), PayoffOf(plan, 2)).Should().Be((new DateOnly(2027, 4, 1), new DateOnly(2027, 8, 1)));
    }

    [Fact]
    public void Extras_go_to_the_first_debt_in_the_order_that_is_still_owed()
    {
        // 50 extra on the 15th of each month.
        // A: 300 -> 250 (Jan 15) -> 150 (Feb 1) -> 100 (Feb 15) -> 0 (Mar 1).
        // B: 1,000 -> 900 (Feb 1) -> 800 (Mar 1) -> 750 (Mar 15) -> 550 (Apr 1, own 100 + A's 100)
        //    -> 500 (Apr 15) -> 300 (May 1) -> 250 (May 15) -> 50 (Jun 1) -> 0 (Jun 15).
        var plan = PayoffPlanner.Plan([Loan(1, 300m, 100m), Loan(2, 1_000m, 100m)], [Id(1), Id(2)], Monthly(new DateOnly(2027, 1, 15), 50m));

        (PayoffOf(plan, 1), PayoffOf(plan, 2)).Should().Be((new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 15)));
    }

    [Fact]
    public void What_the_last_day_doesnt_need_moves_to_the_next_debt_that_day()
    {
        // A: 300 -> 50 (Jan 15, 250 extra). On Feb 1 its own 100 needs only 50, and the 200 extra
        // that day isn't needed at all, so 250 goes to B the same day.
        // B: 950 -> 850 (Feb 1, own) -> 600 (Feb 1, from A) -> 400 (Mar 1, own + A's freed 100)
        //    -> 200 (Apr 1) -> 0 (May 1). Without A's unused 50, B would last until Jun 1.
        List<PlannedPayment> extras = [new(new DateOnly(2027, 1, 15), Money.Usd(250m)), new(new DateOnly(2027, 2, 1), Money.Usd(200m))];

        var plan = PayoffPlanner.Plan([Loan(1, 300m, 100m), Loan(2, 950m, 100m)], [Id(1), Id(2)], extras);

        (PayoffOf(plan, 1), PayoffOf(plan, 2)).Should().Be((new DateOnly(2027, 2, 1), new DateOnly(2027, 5, 1)));
    }

    [Fact]
    public void A_card_paid_off_early_hands_on_what_its_payment_didnt_need()
    {
        // A 0% card closing on the 9th, due 25 days later (Feb 3, Mar 6, Apr 3, May 4, Jun 3); its bill pays 100 then.
        // Card: 300 -> 50 (Jan 15, 250 extra) -> 0 (Feb 3, its 100 needs only 50): the other 50 goes to B that day.
        // B: 950 -> 850 (Feb 1) -> 800 (Feb 3) -> 700 (Mar 1) -> 600 (Mar 6, the card's freed 100) -> 500 (Apr 1)
        //    -> 400 (Apr 3) -> 300 (May 1) -> 200 (May 4) -> 100 (Jun 1) -> 0 (Jun 3).
        var card = new PayoffDebt(
            Id(1),
            new CreditCardInput(
                new CardPosition(AsOf, [new CardBucketBalance("purchase", Money.Usd(300m))], Money.Usd(0m), Provenance.LenderStatement(AsOf)),
                new CreditCardTerms(
                    DayOfMonth.Of(9),
                    PaymentDueRule.AfterClose(25),
                    [new AprBucket("purchase", BalanceBucketKind.Purchase, RateSchedule.Fixed(AnnualRate.Zero, LongAgo))],
                    new MinimumPaymentRule(Money.Usd(35m), 0.01m, PlusInterestAndFees: true)),
                Money.Usd(100m)),
            [.. new[] { (2, 3), (3, 6), (4, 3), (5, 4), (6, 3), (7, 4) }.Select(d => new PlannedPayment(new DateOnly(2027, d.Item1, d.Item2), Money.Usd(100m)))]);

        var plan = PayoffPlanner.Plan([card, Loan(2, 950m, 100m)], [Id(1), Id(2)], [new(new DateOnly(2027, 1, 15), Money.Usd(250m))]);

        (PayoffOf(plan, 1), PayoffOf(plan, 2)).Should().Be((new DateOnly(2027, 2, 3), new DateOnly(2027, 6, 3)));
    }

    [Fact]
    public void A_debt_paid_off_on_its_own_frees_its_payment_for_the_debt_in_front()
    {
        // B is last in the order but pays off by itself on Apr 1 (300 / 100). From May 1 its 100 helps A:
        // A 1,000 -> 900, 800, 700 (Apr) -> 500 (May) -> 300 (Jun) -> 100 (Jul) -> 0 (Aug 1).
        var plan = PayoffPlanner.Plan([Loan(1, 1_000m, 100m), Loan(2, 300m, 100m)], [Id(1), Id(2)], []);

        (PayoffOf(plan, 1), PayoffOf(plan, 2)).Should().Be((new DateOnly(2027, 8, 1), new DateOnly(2027, 4, 1)));
    }

    [Fact]
    public void A_debt_that_cant_be_projected_gets_no_extras_and_leaves_no_debt_free_date()
    {
        // A has neither a scheduled payment nor a remaining term. The extras go to B instead:
        // B 1,000 -> 500 (Jan 15) -> 400 (Feb 1) -> 0 (Feb 15).
        var unknown = Loan(1, 300m, 100m) is { Input: AmortizingLoanInput a } debt
            ? debt with { Input = a with { Terms = a.Terms with { ScheduledPayment = null } } }
            : throw new InvalidOperationException();

        var plan = PayoffPlanner.Plan([unknown, Loan(2, 1_000m, 100m)], [Id(1), Id(2)], Monthly(new DateOnly(2027, 1, 15), 500m));

        plan.Debts.Single(d => d.Account == Id(1)).Outcome.Should().BeOfType<InsufficientData>();
        PayoffOf(plan, 2).Should().Be(new DateOnly(2027, 2, 15));
        plan.DebtFree.Should().BeNull();
        plan.TotalInterest.Should().BeNull();
    }

    [Fact]
    public void Avalanche_costs_less_interest_than_snowball_and_agrees_with_an_independent_model()
    {
        // The debt-management skill's worked example: card 5,000 at 22% (100 a month), student loan
        // 12,000 at 6% (200), personal loan 3,000 at 15% (75), plus 500 extra a month, all on the 1st.
        // Its month-by-month float model gives avalanche 1,945.92 and snowball 2,104.35 in interest,
        // both debt-free in month 26 (Mar 1, 2029). Mossmint rounds each month's interest to the cent,
        // so totals may differ by a few cents.
        PayoffDebt[] debts = [Loan(1, 5_000m, 100m, 22m), Loan(2, 12_000m, 200m, 6m), Loan(3, 3_000m, 75m, 15m)];
        var extras = Monthly(new DateOnly(2027, 2, 1), 500m);

        var avalanche = PayoffPlanner.Plan(debts, PayoffPlanner.Avalanche(debts), extras);
        var snowball = PayoffPlanner.Plan(debts, PayoffPlanner.Snowball(debts), extras);

        PayoffPlanner.Avalanche(debts).Should().Equal(Id(1), Id(3), Id(2));
        PayoffPlanner.Snowball(debts).Should().Equal(Id(3), Id(1), Id(2));
        (avalanche.DebtFree, snowball.DebtFree).Should().Be((new DateOnly(2029, 3, 1), new DateOnly(2029, 3, 1)));
        avalanche.TotalInterest!.Value.Amount.Should().BeApproximately(1_945.92m, 0.05m);
        snowball.TotalInterest!.Value.Amount.Should().BeApproximately(2_104.35m, 0.05m);
    }
}
