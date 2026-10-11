using Mossmint.Domain;
using Mossmint.Domain.Debts;
using Mossmint.Engine.Loans;
using Mossmint.Engine.Payoff;

namespace Mossmint.Engine.Cards;

public sealed record CardBucketBalance(string Bucket, Money Balance);

// Balances at the start of AsOf. LastStatement* describe the most recent
// statement, if known: its payment is due within the first projected cycle, and
// whether it was paid in full decides the grace period.
public sealed record CardPosition(
    DateOnly AsOf,
    IReadOnlyList<CardBucketBalance> Balances,
    Money InterestAccruedSinceStatement,
    Provenance Provenance,
    DateOnly? LastStatementDate = null,
    Money? LastStatementMinimumDue = null,
    bool LastStatementPaidInFull = false);

public sealed record CardPayment(DateOnly Date, Money Amount);

public sealed record PlannedCardPurchase(DateOnly Date, Money Amount, string? Bucket = null);

// RegularPayment is paid on every due date; when it is below the minimum due,
// the minimum is paid instead and a warning says so.
public sealed record CreditCardInput(
    CardPosition Position,
    CreditCardTerms Terms,
    Money RegularPayment,
    IReadOnlyList<CardPayment>? ExtraPayments = null,
    IReadOnlyList<PlannedCardPurchase>? Purchases = null,
    int MaxCycles = 600) : IDebtInput;

public sealed record CardCycle(
    DateOnly StatementDate,
    DateOnly DueDate,
    Money Purchases,
    Money Payments,
    Money InterestCharged,
    Money StatementBalance,
    Money MinimumDue,
    bool GraceApplied,
    IReadOnlyList<CardBucketBalance> Balances);

public sealed record CardProjection(
    IReadOnlyList<CardCycle> Cycles,
    PayoffOutcome Outcome,
    Confidence Confidence,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Warnings,
    Money ExtraPaid);

// An approximation of a card's statement cycles (docs/financial-models/cards.md).
// Interest accrues daily on each bucket's balance and is charged at statement
// close. Lender-exact results need the card's statements; until then every
// result is labelled Estimated, with its assumptions listed.
public static class CreditCard
{
    private const int StagnantCycleLimit = 3;

    public static CardProjection Project(CreditCardInput input)
    {
        var (position, terms) = (input.Position, input.Terms);
        var assumptions = Assumptions(input);
        var confidence = (Confidence)Math.Min((int)position.Provenance.Confidence, (int)Confidence.Estimated);
        var warnings = new List<string>();

        var missing = Validate(input);
        if (missing.Count > 0)
        {
            return new CardProjection([], new InsufficientData(missing), confidence, assumptions, warnings, Money.Zero(input.RegularPayment.Currency));
        }

        var currency = input.RegularPayment.Currency;
        var zero = Money.Zero(currency);
        var buckets = terms.Buckets.ToDictionary(b => b.Name);
        var defaultBucket = terms.Buckets.FirstOrDefault(b => b.Kind == BalanceBucketKind.Purchase)?.Name ?? terms.Buckets[0].Name;

        var balance = terms.Buckets.ToDictionary(b => b.Name, _ => 0m);
        foreach (var b in position.Balances)
        {
            balance[b.Bucket] += b.Balance.Amount;
        }

        var pending = terms.Buckets.ToDictionary(b => b.Name, _ => 0m);
        pending[defaultBucket] += position.InterestAccruedSinceStatement.Amount;

        var purchases = (input.Purchases ?? []).Where(p => p.Date >= position.AsOf).ToLookup(p => p.Date);
        var extras = (input.ExtraPayments ?? []).Where(p => p.Date >= position.AsOf).ToLookup(p => p.Date);
        var lastExtra = (input.ExtraPayments ?? []).Select(p => p.Date).DefaultIfEmpty(DateOnly.MinValue).Max();
        var lastPurchase = (input.Purchases ?? []).Select(p => p.Date).DefaultIfEmpty(DateOnly.MinValue).Max();

        // The open statement: what's due, by when, and how much of it is still owed.
        DateOnly? dueDate = position.LastStatementDate is { } last ? terms.PaymentDue.DueDateFor(last) : null;
        var minimumOutstanding = position.LastStatementMinimumDue?.Amount ?? 0m;
        var statementOutstanding = 0m;
        var graceThisCycle = terms.HasGracePeriod && position.LastStatementPaidInFull;

        var cycles = new List<CardCycle>();
        var (cyclePurchases, cyclePayments) = (0m, 0m);
        var (totalPaid, totalInterest, extraPaid) = (0m, 0m, 0m);
        var stagnant = 0;
        decimal? balanceAfterLastCycle = null;
        var nextClose = NextClose(terms, position.AsOf);

        for (var day = position.AsOf; ; day = day.AddDays(1))
        {
            foreach (var bucket in terms.Buckets.Where(b => b.PromoEndsOn == day && b.RevertsTo is not null))
            {
                balance[bucket.RevertsTo!] += balance[bucket.Name];
                pending[bucket.RevertsTo!] += pending[bucket.Name];
                balance[bucket.Name] = 0m;
                pending[bucket.Name] = 0m;
            }

            foreach (var purchase in purchases[day])
            {
                balance[purchase.Bucket ?? defaultBucket] += purchase.Amount.Amount;
                cyclePurchases += purchase.Amount.Amount;
            }

            var regularToday = 0m;
            if (day == dueDate)
            {
                regularToday = input.RegularPayment.Amount;
                if (regularToday < minimumOutstanding)
                {
                    warnings.Add($"{day:yyyy-MM-dd}: the planned payment is below the minimum due; the minimum is used.");
                    regularToday = minimumOutstanding;
                }
            }

            var owedToday = balance.Values.Sum() + pending.Values.Sum(p => Round(p, terms));
            var paymentToday = Math.Min(extras[day].Sum(p => p.Amount.Amount) + regularToday, Math.Max(owedToday, 0m));
            if (paymentToday > 0m)
            {
                // Paying off between statements also pays the interest accrued
                // since the last one, which counts as interest charged.
                totalInterest += Allocate(paymentToday, minimumOutstanding, balance, pending, buckets, day, terms);

                // The regular payment counts first; the rest of what was paid is extra.
                extraPaid += paymentToday - Math.Min(regularToday, paymentToday);
                minimumOutstanding = Math.Max(0m, minimumOutstanding - paymentToday);
                statementOutstanding = Math.Max(0m, statementOutstanding - paymentToday);
                cyclePayments += paymentToday;
                totalPaid += paymentToday;
            }

            if (day == dueDate)
            {
                // Paying the statement balance in full by its due date keeps (or
                // restores) the grace period on new purchases for the next cycle.
                graceThisCycle = terms.HasGracePeriod && statementOutstanding == 0m;
            }

            if (day >= lastPurchase && balance.Values.Sum() == 0m && pending.Values.All(p => Round(p, terms) == 0m))
            {
                return new CardProjection(cycles, new PaysOff(day, cycles.Count, new Money(totalPaid, currency), new Money(totalInterest, currency), zero), confidence, assumptions, warnings, new Money(extraPaid, currency));
            }

            foreach (var bucket in terms.Buckets)
            {
                var graceExempt = graceThisCycle && bucket.Kind == BalanceBucketKind.Purchase;
                var rate = bucket.Rates.SegmentOn(day);
                if (rate is null)
                {
                    return new CardProjection(cycles, new InsufficientData([$"No APR is known for {bucket.Name} on {day:yyyy-MM-dd}."]), confidence, assumptions, warnings, new Money(extraPaid, currency));
                }

                if (!graceExempt && balance[bucket.Name] > 0m)
                {
                    pending[bucket.Name] += balance[bucket.Name] * DayCount.DailyRate(rate.Rate, terms.DayCount, day);
                }
            }

            if (day != nextClose)
            {
                continue;
            }

            var charged = 0m;
            foreach (var name in balance.Keys.ToList())
            {
                var interest = Round(pending[name], terms);
                balance[name] += interest;
                charged += interest;
                pending[name] = 0m;
            }

            totalInterest += charged;
            var statementBalance = balance.Values.Sum();
            var minimumDue = MinimumDue(terms.Minimum, statementBalance, charged, currency);
            dueDate = terms.PaymentDue.DueDateFor(day);
            minimumOutstanding = minimumDue;
            statementOutstanding = statementBalance;
            cycles.Add(new CardCycle(
                day, dueDate.Value, new Money(cyclePurchases, currency), new Money(cyclePayments, currency),
                new Money(charged, currency), new Money(statementBalance, currency), new Money(minimumDue, currency), graceThisCycle,
                [.. terms.Buckets.Select(b => new CardBucketBalance(b.Name, new Money(balance[b.Name], currency)))]));
            (cyclePurchases, cyclePayments) = (0m, 0m);
            nextClose = NextClose(terms, day.AddDays(1));

            var nothingCanHelp = day >= lastExtra && day >= lastPurchase && terms.Buckets.All(b => b.PromoEndsOn is null || b.PromoEndsOn <= day)
                && terms.Buckets.All(b => !b.Rates.ChangesAfter(day).Any());
            stagnant = balanceAfterLastCycle is { } previous && statementBalance >= previous ? stagnant + 1 : 0;
            balanceAfterLastCycle = statementBalance;
            if (stagnant >= StagnantCycleLimit && nothingCanHelp)
            {
                return new CardProjection(cycles, new NeverPaysOff(
                    "Payments don't cover the interest charged each cycle.", cycles[^StagnantCycleLimit].StatementDate, null), confidence, assumptions, warnings, new Money(extraPaid, currency));
            }

            if (cycles.Count >= input.MaxCycles)
            {
                return new CardProjection(cycles, new NeverPaysOff($"Not paid off within {input.MaxCycles} statement cycles.", null, null), confidence, assumptions, warnings, new Money(extraPaid, currency));
            }
        }
    }

    // Card Act ordering: the minimum due goes to the lowest-APR balances first,
    // anything above it to the highest-APR balances first. Charged interest sits
    // in the bucket balance; uncharged (pending) interest is paid last, and the
    // amount of it paid is returned.
    private static decimal Allocate(
        decimal payment,
        decimal minimumOutstanding,
        Dictionary<string, decimal> balance,
        Dictionary<string, decimal> pending,
        Dictionary<string, AprBucket> buckets,
        DateOnly day,
        CreditCardTerms terms)
    {
        var byRate = buckets.Values.OrderBy(b => b.Rates.SegmentOn(day)?.Rate.Value ?? 0m).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
        var toMinimum = Math.Min(payment, minimumOutstanding);
        var remaining = PayDown(toMinimum, byRate, balance) + (payment - toMinimum);
        remaining = PayDown(remaining, Enumerable.Reverse(byRate), balance);

        var pendingPaid = 0m;
        foreach (var bucket in byRate.AsEnumerable().Reverse())
        {
            var charge = Round(pending[bucket.Name], terms);
            var applied = Math.Min(remaining, charge);
            pending[bucket.Name] -= applied;
            remaining -= applied;
            pendingPaid += applied;
        }

        return pendingPaid;
    }

    private static decimal PayDown(decimal amount, IEnumerable<AprBucket> order, Dictionary<string, decimal> balance)
    {
        foreach (var bucket in order)
        {
            var applied = Math.Min(amount, Math.Max(balance[bucket.Name], 0m));
            balance[bucket.Name] -= applied;
            amount -= applied;
        }

        return amount;
    }

    private static decimal MinimumDue(MinimumPaymentRule rule, decimal statementBalance, decimal interestCharged, Currency currency)
    {
        if (statementBalance <= 0m)
        {
            return 0m;
        }

        var percent = decimal.Round(statementBalance * rule.FractionOfBalance, currency.MinorUnits, MidpointRounding.AwayFromZero);
        var computed = rule.PlusInterestAndFees ? percent + interestCharged : percent;
        return Math.Min(statementBalance, Math.Max(rule.Floor.Amount, computed));
    }

    private static DateOnly NextClose(CreditCardTerms terms, DateOnly onOrAfter)
    {
        var close = terms.StatementClosingDay.In(onOrAfter.Year, onOrAfter.Month);
        if (close < onOrAfter)
        {
            var next = onOrAfter.AddMonths(1);
            close = terms.StatementClosingDay.In(next.Year, next.Month);
        }

        return close;
    }

    private static decimal Round(decimal amount, CreditCardTerms terms) =>
        decimal.Round(amount, 2, terms.InterestRounding);

    private static List<string> Validate(CreditCardInput input)
    {
        var (position, terms) = (input.Position, input.Terms);
        var missing = new List<string>();
        var names = terms.Buckets.Select(b => b.Name).ToHashSet();
        if (terms.Buckets.Count == 0)
        {
            missing.Add("At least one APR bucket.");
        }

        if (position.Balances.Any(b => !names.Contains(b.Bucket)) || (input.Purchases ?? []).Any(p => p.Bucket is not null && !names.Contains(p.Bucket)))
        {
            missing.Add("Every balance and purchase must name one of the card's APR buckets.");
        }

        if (terms.Buckets.Any(b => b.RevertsTo is not null && !names.Contains(b.RevertsTo)))
        {
            missing.Add("A promotional bucket reverts to a bucket the card doesn't have.");
        }

        if (position.Balances.Any(b => b.Balance.IsNegative))
        {
            missing.Add("A credit balance needs a statement to confirm it.");
        }

        if (input.RegularPayment.IsNegative)
        {
            missing.Add("A planned payment that isn't negative.");
        }

        return missing;
    }

    private static IReadOnlyList<string> Assumptions(CreditCardInput input)
    {
        var terms = input.Terms;
        var list = new List<string>
        {
            "Approximation: lender-exact results need the card's statements.",
            $"Interest accrues daily on each APR bucket ({terms.DayCount}) and is charged at statement close, rounded to the cent.",
            "Payments post on their dates: the minimum due goes to the lowest APR first, anything more to the highest APR first.",
            "A payment that clears the card also pays the interest accrued since the last statement.",
            (input.Purchases ?? []).Count == 0 ? "No new purchases are made on the card." : "Only the listed purchases are made on the card.",
            "No late fees, annual fees, cash-advance fees or deferred-interest charges are modelled.",
        };
        if (terms.HasGracePeriod)
        {
            list.Add("New purchases accrue no interest in a cycle that follows a statement paid in full by its due date.");
        }

        if (terms.Buckets.Any(b => b.Rates.HasAssumptions))
        {
            list.Add("Some future APRs are assumptions, not announced rates.");
        }

        return list;
    }
}
