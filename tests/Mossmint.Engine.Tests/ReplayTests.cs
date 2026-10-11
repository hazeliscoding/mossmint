using System.Text.Json;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.Cards;
using Mossmint.Engine.Loans;
using Mossmint.Engine.Payoff;

namespace Mossmint.Engine.Tests;

// ADR-0002: engine inputs and outputs serialize to JSON, and replaying the
// serialized input reproduces the serialized output byte for byte.
public class ReplayTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 9);

    private static void ReplaysIdentically<TInput, TOutput>(TInput input, Func<TInput, TOutput> run)
    {
        var json = JsonSerializer.Serialize(input);
        var replayed = JsonSerializer.Deserialize<TInput>(json)!;

        JsonSerializer.Serialize(replayed).Should().Be(json);
        JsonSerializer.Serialize(run(replayed)).Should().Be(JsonSerializer.Serialize(run(input)));
    }

    [Fact]
    public void Simple_interest_loans_replay()
    {
        var rates = new RateSchedule([
            new RateSegment(new DateOnly(2020, 1, 1), AnnualRate.FromPercent(11.5m)),
            new RateSegment(new DateOnly(2027, 3, 1), AnnualRate.FromPercent(12.25m), RateKind.Variable, IsAssumption: true),
        ]);
        var input = new SimpleInterestLoanInput(
            new LoanPosition(AsOf, Money.Usd(8_400m), Money.Usd(12.40m), Money.Usd(0m), Provenance.LenderStatement(AsOf, "statement 2026-10")),
            new SimpleInterestLoanTerms(rates, DayCountConvention.Actual365Fixed, Money.Usd(310m),
                new Schedule(new MonthlyRule(DayOfMonth.Last), StartsOn: AsOf.AddDays(1)),
                Penalty: new PrepaymentPenalty(0.01m, new DateOnly(2027, 1, 31))),
            CalendarSpec.UsFederalReserve,
            [new PlannedPaymentAmount(new DateOnly(2026, 12, 31), Money.Usd(400m))],
            [new ExtraPayment(new DateOnly(2027, 1, 15), Money.Usd(500m))]);

        ReplaysIdentically(input, SimpleInterestLoan.Project);
    }

    [Fact]
    public void Amortizing_loans_replay()
    {
        var input = new AmortizingLoanInput(
            new LoanPosition(AsOf, Money.Usd(14_000m), Money.Usd(0m), Money.Usd(0m), Provenance.UserConfirmed(AsOf)),
            new AmortizingLoanTerms(RateSchedule.Fixed(AnnualRate.FromPercent(7.9m), new DateOnly(2020, 1, 1)), 12,
                new Schedule(new MonthlyRule(DayOfMonth.Of(20))), RemainingPayments: 48),
            CalendarSpec.WeekendsOnly);

        ReplaysIdentically(input, AmortizingLoan.Project);
    }

    [Fact]
    public void Credit_cards_replay()
    {
        var input = new CreditCardInput(
            new CardPosition(AsOf, [new CardBucketBalance("purchase", Money.Usd(2_300m)), new CardBucketBalance("promo", Money.Usd(900m))],
                Money.Usd(8.15m), Provenance.LenderStatement(AsOf), new DateOnly(2026, 9, 21), Money.Usd(40m)),
            new CreditCardTerms(
                DayOfMonth.Of(21),
                PaymentDueRule.OnDay(DayOfMonth.Last),
                [
                    new AprBucket("purchase", BalanceBucketKind.Purchase, RateSchedule.Fixed(AnnualRate.FromPercent(24.99m), new DateOnly(2020, 1, 1))),
                    new AprBucket("promo", BalanceBucketKind.Promotional, RateSchedule.Fixed(AnnualRate.Zero, new DateOnly(2020, 1, 1)), new DateOnly(2027, 4, 1), "purchase"),
                ],
                new MinimumPaymentRule(Money.Usd(40m), 0.01m, true)),
            Money.Usd(275m),
            Purchases: [new PlannedCardPurchase(new DateOnly(2026, 11, 2), Money.Usd(60m))]);

        ReplaysIdentically(input, CreditCard.Project);
    }

    [Fact]
    public void Payoff_plans_replay()
    {
        var monthly = new Schedule(new MonthlyRule(DayOfMonth.Of(20)), StartsOn: AsOf.AddDays(1));
        List<PlannedPayment> Bill(decimal amount) =>
            [.. Enumerable.Range(0, 60).Select(i => new PlannedPayment(new DateOnly(2026, 10, 20).AddMonths(i), Money.Usd(amount)))];
        IReadOnlyList<PayoffDebt> debts =
        [
            new(new AccountId(new Guid(1, 0, 0, new byte[8])), new SimpleInterestLoanInput(
                new LoanPosition(AsOf, Money.Usd(4_000m), Money.Usd(0m), Money.Usd(0m), Provenance.LenderStatement(AsOf)),
                new SimpleInterestLoanTerms(RateSchedule.Fixed(AnnualRate.FromPercent(9m), new DateOnly(2020, 1, 1)), DayCountConvention.Actual365Fixed, Money.Usd(150m), monthly),
                CalendarSpec.UsFederalReserve), Bill(150m)),
            new(new AccountId(new Guid(2, 0, 0, new byte[8])), new AmortizingLoanInput(
                new LoanPosition(AsOf, Money.Usd(9_000m), Money.Usd(0m), Money.Usd(0m), Provenance.UserConfirmed(AsOf)),
                new AmortizingLoanTerms(RateSchedule.Fixed(AnnualRate.FromPercent(5.5m), new DateOnly(2020, 1, 1)), 12, monthly, Money.Usd(250m)),
                CalendarSpec.UsFederalReserve), Bill(250m)),
            new(new AccountId(new Guid(3, 0, 0, new byte[8])), new CreditCardInput(
                new CardPosition(AsOf, [new CardBucketBalance("purchase", Money.Usd(1_800m))], Money.Usd(0m), Provenance.LenderStatement(AsOf)),
                new CreditCardTerms(
                    DayOfMonth.Of(25),
                    PaymentDueRule.AfterClose(25),
                    [new AprBucket("purchase", BalanceBucketKind.Purchase, RateSchedule.Fixed(AnnualRate.FromPercent(23.99m), new DateOnly(2020, 1, 1)))],
                    new MinimumPaymentRule(Money.Usd(35m), 0.01m, true)),
                Money.Usd(90m)), Bill(90m)),
        ];
        var extras = Bill(75m);

        ReplaysIdentically(debts, d => PayoffPlanner.Plan(d, PayoffPlanner.Avalanche(d), extras));
    }
}
