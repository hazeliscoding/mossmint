using System.Text.Json.Serialization;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Engine.Cards;
using Mossmint.Engine.Loans;

namespace Mossmint.Engine.Payoff;

// A debt the payoff planner can project and pay extra into.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$debt")]
[JsonDerivedType(typeof(SimpleInterestLoanInput), "simple-interest-loan")]
[JsonDerivedType(typeof(AmortizingLoanInput), "amortizing-loan")]
[JsonDerivedType(typeof(CreditCardInput), "credit-card")]
public interface IDebtInput;

public sealed record PlannedPayment(DateOnly Date, Money Amount);

// One debt in a payoff plan. Payments are what the cash forecast pays toward it
// (its debt-payment bill); once the debt is paid off, they go to the next debt.
public sealed record PayoffDebt(AccountId Account, IDebtInput Input, IReadOnlyList<PlannedPayment> Payments);

public sealed record DebtPayoff(AccountId Account, PayoffOutcome Outcome, Confidence Confidence);

public sealed record PayoffPlan(IReadOnlyList<DebtPayoff> Debts)
{
    // Only when every debt pays off; one that doesn't leaves no date and no total.
    [JsonIgnore]
    public DateOnly? DebtFree => AllPayOff ? Debts.Max(d => ((PaysOff)d.Outcome).PayoffDate) : null;

    [JsonIgnore]
    public Money? TotalInterest => AllPayOff ? Debts.Select(d => ((PaysOff)d.Outcome).TotalInterest).Aggregate((a, b) => a + b) : null;

    private bool AllPayOff => Debts.Count > 0 && Debts.All(d => d.Outcome is PaysOff);
}

// Paying debts one at a time (docs/financial-models/payoff.md). Every debt keeps
// its own payments. Extra payments, and the payments of debts already paid off,
// go to the first debt in the order that is still owed; what its last day
// doesn't need goes on to the next debt that same day.
public static class PayoffPlanner
{
    // Highest rate charged today first; ties go to the smaller balance.
    public static IReadOnlyList<AccountId> Avalanche(IReadOnlyList<PayoffDebt> debts) =>
        [.. debts.OrderByDescending(d => RateToday(d.Input)).ThenBy(d => Owed(d.Input)).ThenBy(d => d.Account.Value).Select(d => d.Account)];

    // Smallest balance first; ties go to the higher rate.
    public static IReadOnlyList<AccountId> Snowball(IReadOnlyList<PayoffDebt> debts) =>
        [.. debts.OrderBy(d => Owed(d.Input)).ThenByDescending(d => RateToday(d.Input)).ThenBy(d => d.Account.Value).Select(d => d.Account)];

    // Today's plan: each debt on its own payments, and nothing moves between them.
    public static PayoffPlan Baseline(IReadOnlyList<PayoffDebt> debts) =>
        new([.. debts.Select(d => Project(d, []).Payoff)]);

    // Debts not in the order are never paid extra, but their payments still move
    // on once they are paid off. A debt that can't be projected is skipped.
    public static PayoffPlan Plan(IReadOnlyList<PayoffDebt> debts, IReadOnlyList<AccountId> order, IReadOnlyList<PlannedPayment> extras)
    {
        var results = debts.ToDictionary(d => d.Account, d => Project(d, []));

        // Until its turn comes, a debt gets nothing extra, so it pays off as it would
        // on its own; from then on its payments go to whichever debt is first.
        var pool = new List<PlannedPayment>(extras);
        foreach (var debt in debts)
        {
            if (results[debt.Account].Payoff.Outcome is PaysOff alone)
            {
                pool.AddRange(debt.Payments.Where(p => p.Date > alone.PayoffDate));
            }
        }

        foreach (var debt in order.Distinct().Select(id => debts.FirstOrDefault(d => d.Account == id)).OfType<PayoffDebt>())
        {
            var onItsOwn = results[debt.Account].Payoff.Outcome;
            if (onItsOwn is InsufficientData)
            {
                continue;
            }

            var result = Project(debt, pool);
            results[debt.Account] = result;
            if (result.Payoff.Outcome is not PaysOff paid)
            {
                // It takes everything from here on and still never pays off.
                pool = [];
                continue;
            }

            var day = paid.PayoffDate;
            pool =
            [
                .. pool.Where(p => p.Date > day),
                .. debt.Payments.Where(p => p.Date > day && (onItsOwn is not PaysOff alone || p.Date <= alone.PayoffDate)),
            ];
            if (result.Unused.IsPositive)
            {
                pool.Add(new PlannedPayment(day, result.Unused));
            }
        }

        return new PayoffPlan([.. debts.Select(d => results[d.Account].Payoff)]);
    }

    // The planner's extras replace any extra payments already on the input.
    // Unused is what was planned through the payoff day but not needed.
    private static (DebtPayoff Payoff, Money Unused) Project(PayoffDebt debt, IReadOnlyList<PlannedPayment> extras)
    {
        var (outcome, confidence, asOf, extraPaid) = debt.Input switch
        {
            SimpleInterestLoanInput s => Loan(SimpleInterestLoan.Project(s with { ExtraPayments = [.. extras.Select(e => new ExtraPayment(e.Date, e.Amount))] }), s.Position),
            AmortizingLoanInput a => Loan(AmortizingLoan.Project(a with { ExtraPayments = [.. extras.Select(e => new ExtraPayment(e.Date, e.Amount))] }), a.Position),
            CreditCardInput c => CreditCard.Project(c with { ExtraPayments = [.. extras.Select(e => new CardPayment(e.Date, e.Amount))] }) is var card
                ? (card.Outcome, card.Confidence, c.Position.AsOf, card.ExtraPaid)
                : default,
            _ => throw new ArgumentOutOfRangeException(nameof(debt), $"Unknown debt input {debt.Input.GetType().Name}."),
        };

        var zero = Money.Zero(extraPaid.Currency);
        var payoff = new DebtPayoff(debt.Account, outcome, confidence);
        if (outcome is not PaysOff paid)
        {
            return (payoff, zero);
        }

        // Extras are tracked exactly. The debt's own payment is the bill's, so its
        // unused part is exact while the bill matches what the projection paid, and
        // never more than the bill.
        Money Through(IEnumerable<PlannedPayment> payments) =>
            payments.Where(p => p.Date >= asOf && p.Date <= paid.PayoffDate).Aggregate(zero, (sum, p) => sum + p.Amount);
        var regularUnused = Money.Max(Through(debt.Payments) - (paid.TotalPaid - extraPaid), zero);
        return (payoff, Through(extras) - extraPaid + regularUnused);

        static (PayoffOutcome, Confidence, DateOnly, Money) Loan(LoanProjection projection, LoanPosition position) =>
            (projection.Outcome, projection.Confidence, position.AsOf,
             projection.Rows.Where(r => r.Kind == LoanPaymentKind.Extra).Aggregate(Money.Zero(position.Principal.Currency), (sum, r) => sum + r.Payment));
    }

    private static decimal RateToday(IDebtInput input) => input switch
    {
        SimpleInterestLoanInput s => s.Terms.Rates.SegmentOn(s.Position.AsOf)?.Rate.Value ?? 0m,
        AmortizingLoanInput a => a.Terms.Rates.SegmentOn(a.Position.AsOf)?.Rate.Value ?? 0m,
        CreditCardInput c => c.Terms.Buckets
            .Where(b => c.Position.Balances.Any(x => x.Bucket == b.Name && x.Balance.IsPositive))
            .Select(b => b.Rates.SegmentOn(c.Position.AsOf)?.Rate.Value ?? 0m)
            .DefaultIfEmpty(0m)
            .Max(),
        _ => throw new ArgumentOutOfRangeException(nameof(input), $"Unknown debt input {input.GetType().Name}."),
    };

    private static decimal Owed(IDebtInput input) => input switch
    {
        SimpleInterestLoanInput s => s.Position.Owed.Amount,
        AmortizingLoanInput a => a.Position.Owed.Amount,
        CreditCardInput c => c.Position.Balances.Sum(b => b.Balance.Amount) + c.Position.InterestAccruedSinceStatement.Amount,
        _ => throw new ArgumentOutOfRangeException(nameof(input), $"Unknown debt input {input.GetType().Name}."),
    };
}
