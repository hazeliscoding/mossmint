using FsCheck;
using FsCheck.Fluent;
using Mossmint.Domain;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.Cards;
using Mossmint.Engine.Loans;

namespace Mossmint.Engine.Tests.Cards;

public class CreditCardTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 10);
    private static readonly DateOnly LongAgo = new(2020, 1, 1);

    // 18.25% / 365 is exactly 0.0005 a day, which keeps hand-worked values exact.
    private static AprBucket Purchase(decimal apr = 18.25m) =>
        new("purchase", BalanceBucketKind.Purchase, RateSchedule.Fixed(AnnualRate.FromPercent(apr), LongAgo));

    private static AprBucket Promo(DateOnly? endsOn = null) =>
        new("promo", BalanceBucketKind.Promotional, RateSchedule.Fixed(AnnualRate.Zero, LongAgo), endsOn, endsOn is null ? null : "purchase");

    private static CreditCardTerms Terms(params AprBucket[] buckets) => new(
        DayOfMonth.Of(9),
        PaymentDueRule.AfterClose(25),
        buckets.Length == 0 ? [Purchase()] : buckets,
        new MinimumPaymentRule(Money.Usd(35m), 0.01m, PlusInterestAndFees: true));

    private static CreditCardInput Card(
        decimal balance,
        decimal regularPayment,
        CreditCardTerms? terms = null,
        IReadOnlyList<CardBucketBalance>? balances = null,
        IReadOnlyList<PlannedCardPurchase>? purchases = null,
        bool lastPaidInFull = false,
        DateOnly? lastStatement = null,
        decimal? lastMinimum = null) =>
        new(
            new CardPosition(
                AsOf,
                balances ?? [new CardBucketBalance("purchase", Money.Usd(balance))],
                Money.Usd(0m),
                Provenance.LenderStatement(AsOf),
                lastStatement,
                lastMinimum is null ? null : Money.Usd(lastMinimum.Value),
                lastPaidInFull),
            terms ?? Terms(),
            Money.Usd(regularPayment),
            Purchases: purchases);

    [Fact]
    public void First_two_cycles_match_hand_worked_values()
    {
        // Cycle to Oct 9: 30 days x 1,000.00 x 0.0005 = 15.00 -> statement 1,015.00, minimum max(35, 10.15 + 15.00) = 35.00, due Nov 3.
        // Cycle to Nov 9: 24 days x 1,015.00 x 0.0005 = 12.18, then 100.00 paid Nov 3,
        //                 7 days x 915.00 x 0.0005 = 3.2025; total 15.3825 -> 15.38 -> statement 930.38.
        var cycles = CreditCard.Project(Card(1_000m, 100m)).Cycles;

        cycles.Take(2).Select(c => (c.StatementDate, c.InterestCharged.Amount, c.StatementBalance.Amount, c.MinimumDue.Amount, c.DueDate, c.Payments.Amount))
            .Should().Equal(
                (new DateOnly(2026, 10, 9), 15.00m, 1_015.00m, 35.00m, new DateOnly(2026, 11, 3), 0m),
                (new DateOnly(2026, 11, 9), 15.38m, 930.38m, 35.00m, new DateOnly(2026, 12, 4), 100m));
    }

    [Fact]
    public void Paying_in_full_keeps_new_purchases_interest_free()
    {
        var input = Card(0m, 10_000m, purchases: [new PlannedCardPurchase(new DateOnly(2026, 9, 15), Money.Usd(500m))], lastPaidInFull: true);

        var projection = CreditCard.Project(input);

        projection.Cycles[0].GraceApplied.Should().BeTrue();
        projection.Cycles[0].InterestCharged.Should().Be(Money.Usd(0m));
        projection.Outcome.Should().Be(new PaysOff(new DateOnly(2026, 11, 3), 1, Money.Usd(500m), Money.Usd(0m), Money.Usd(0m)));
    }

    [Fact]
    public void Without_grace_new_purchases_accrue_from_the_purchase_date()
    {
        // 500.00 x 0.0005 x 25 days (Sep 15 to Oct 9) = 6.25
        var input = Card(0m, 10_000m, purchases: [new PlannedCardPurchase(new DateOnly(2026, 9, 15), Money.Usd(500m))]);

        CreditCard.Project(input).Cycles[0].InterestCharged.Should().Be(Money.Usd(6.25m));
    }

    [Fact]
    public void Minimum_goes_to_the_lowest_apr_and_the_rest_to_the_highest()
    {
        // Last statement Sep 9 is due Oct 4 with a 35.00 minimum; 300.00 is paid then.
        var input = Card(
            0m, 300m,
            Terms(Promo(), Purchase(24m)),
            balances: [new CardBucketBalance("promo", Money.Usd(500m)), new CardBucketBalance("purchase", Money.Usd(500m))],
            lastStatement: new DateOnly(2026, 9, 9),
            lastMinimum: 35m);

        var first = CreditCard.Project(input).Cycles[0];

        first.Balances[0].Should().Be(new CardBucketBalance("promo", Money.Usd(465m)));
        first.Balances[1].Balance.Amount.Should().Be(235m + first.InterestCharged.Amount);
    }

    [Fact]
    public void An_expired_promotion_starts_accruing_at_the_standard_apr()
    {
        var input = Card(
            0m, 50m,
            Terms(Promo(endsOn: new DateOnly(2026, 11, 1)), Purchase(24m)),
            balances: [new CardBucketBalance("promo", Money.Usd(1_000m))]);

        var cycles = CreditCard.Project(input).Cycles;

        cycles[0].InterestCharged.Should().Be(Money.Usd(0m));
        cycles[1].InterestCharged.Amount.Should().BeGreaterThan(0m);
        cycles[1].Balances.Single(b => b.Bucket == "promo").Balance.Should().Be(Money.Usd(0m));
    }

    [Fact]
    public void A_payment_below_the_minimum_pays_the_minimum_and_warns()
    {
        var projection = CreditCard.Project(Card(1_000m, 10m));

        projection.Cycles[1].Payments.Should().Be(Money.Usd(35m));
        projection.Warnings.Should().Contain(w => w.Contains("below the minimum"));
    }

    [Fact]
    public void A_minimum_below_the_monthly_interest_never_pays_off()
    {
        // 30% APR charges about 2.5% a month; a 2% minimum without interest can't keep up.
        var terms = Terms(Purchase(30m)) with { Minimum = new MinimumPaymentRule(Money.Usd(0m), 0.02m, PlusInterestAndFees: false) };

        CreditCard.Project(Card(5_000m, 0m, terms)).Outcome.Should().BeOfType<NeverPaysOff>();
    }

    [Fact]
    public void Card_projections_are_always_labelled_approximate()
    {
        var projection = CreditCard.Project(Card(1_000m, 100m));

        projection.Confidence.Should().Be(Confidence.Estimated);
        projection.Assumptions[0].Should().StartWith("Approximation");
    }

    [Fact]
    public void Unknown_buckets_are_insufficient_data()
    {
        var input = Card(0m, 100m, balances: [new CardBucketBalance("cash advance", Money.Usd(100m))]);

        CreditCard.Project(input).Outcome.Should().BeOfType<InsufficientData>();
    }

    private static readonly Arbitrary<(decimal Balance, decimal Apr, decimal Payment)> Cards =
        (from balanceCents in Gen.Choose(0, 2_000_000)
         from aprBasisPoints in Gen.Choose(0, 3_000)
         from paymentCents in Gen.Choose(5_000, 300_000)
         select (balanceCents / 100m, aprBasisPoints / 100m, paymentCents / 100m)).ToArbitrary();

    [Fact]
    public void Every_paid_off_card_conserves_money() =>
        Prop.ForAll(Cards, card =>
        {
            var projection = CreditCard.Project(Card(card.Balance, card.Payment, Terms(Purchase(card.Apr))));
            if (projection.Outcome is not PaysOff paysOff)
            {
                return projection.Outcome is NeverPaysOff;
            }

            return paysOff.TotalPaid.Amount == card.Balance + paysOff.TotalInterest.Amount
                && projection.Cycles.All(c => c.Balances.All(b => !b.Balance.IsNegative))
                && paysOff.TotalInterest.Amount >= projection.Cycles.Sum(c => c.InterestCharged.Amount);
        }).QuickCheckThrowOnFailure();
}
