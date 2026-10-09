using Mossmint.Domain;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Engine.Loans;

public sealed record SimpleInterestLoanInput(
    LoanPosition Position,
    SimpleInterestLoanTerms Terms,
    CalendarSpec Calendar,
    IReadOnlyList<PlannedPaymentAmount>? PlannedAmounts = null,
    IReadOnlyList<ExtraPayment>? ExtraPayments = null,
    int MaxPayments = 600);

// Daily simple interest (docs/financial-models/loans.md):
// - interest accrues on principal only, at the day-count daily rate, for each day
//   from the previous payment up to the payment date;
// - accrued interest is rounded to the cent only when it is charged, at a
//   payment that pays interest; a principal-only payment charges none, so an
//   extra payment never adds a rounding step;
// - unpaid interest is carried and never compounds;
// - a payment covers fees, interest and principal in the contract's order, and
//   the final payment is capped at what is owed.
public static class SimpleInterestLoan
{
    // Owed must fall at least once in this many consecutive regular payments, or
    // the loan is treated as never paying off.
    private const int StagnantPaymentLimit = 3;

    public static LoanProjection Project(SimpleInterestLoanInput input)
    {
        var (position, terms) = (input.Position, input.Terms);
        var assumptions = Assumptions(terms);
        var confidence = ProjectionConfidence(position.Provenance, terms.Rates);

        var missing = Validate(input);
        if (missing.Count > 0)
        {
            return new LoanProjection([], new InsufficientData(missing), confidence, assumptions);
        }

        var currency = position.Principal.Currency;
        var zero = Money.Zero(currency);
        if (position.Owed.IsZero)
        {
            return new LoanProjection([], new PaysOff(position.AsOf, 0, zero, zero, zero), confidence, assumptions);
        }

        var events = PaymentEvents(input);
        var rows = new List<LoanScheduleRow>();
        var (principal, accrued, fees, date) = (position.Principal, position.AccruedInterest, position.FeesDue, position.AsOf);
        var (totalPaid, totalInterest, totalFees) = (zero, zero, zero);
        var uncharged = 0m;
        var regularCount = 0;
        var stagnant = 0;
        Money? owedAfterLastRegular = null;

        foreach (var (eventDate, kind, amount, principalOnly) in events)
        {
            var exact = Interest.Accrue(principal.Amount, date, eventDate, terms.Rates, terms.DayCount);
            if (exact is null)
            {
                return new LoanProjection(rows, new InsufficientData([$"No interest rate is known for {date:yyyy-MM-dd}."]), confidence, assumptions);
            }

            uncharged += exact.Value;
            var isPrincipalOnly = principalOnly && terms.AllowsPrincipalOnlyPayments;
            var newInterest = isPrincipalOnly ? zero : new Money(uncharged, currency).Round(terms.InterestRounding);
            if (!isPrincipalOnly)
            {
                uncharged = 0m;
            }

            var (beginPrincipal, beginAccrued) = (principal, accrued);
            accrued += newInterest;

            var owed = principal + accrued + fees;
            var payment = Money.Min(amount, owed);
            Money toFees = zero, toInterest = zero, toPrincipal = zero, newFees = zero;

            if (isPrincipalOnly)
            {
                toPrincipal = Money.Min(payment, principal);
                payment = toPrincipal;
                if (terms.Penalty is { } penalty && eventDate <= penalty.AppliesUntil)
                {
                    newFees = (toPrincipal * penalty.FractionOfPrepaidPrincipal).Round(terms.InterestRounding);
                }
            }
            else
            {
                var remaining = payment;
                foreach (var target in terms.EffectiveAllocation.Order)
                {
                    var due = target switch
                    {
                        AllocationTarget.Fees => fees,
                        AllocationTarget.Interest => accrued,
                        _ => principal,
                    };
                    var applied = Money.Min(remaining, due);
                    remaining -= applied;
                    switch (target)
                    {
                        case AllocationTarget.Fees: toFees = applied; break;
                        case AllocationTarget.Interest: toInterest = applied; break;
                        default: toPrincipal = applied; break;
                    }
                }
            }

            principal -= toPrincipal;
            accrued -= toInterest;
            fees = fees - toFees + newFees;
            (totalPaid, totalInterest, totalFees) = (totalPaid + payment, totalInterest + toInterest, totalFees + toFees);

            rows.Add(new LoanScheduleRow(
                rows.Count + 1, eventDate, kind, eventDate.DayNumber - date.DayNumber,
                beginPrincipal, beginAccrued, newInterest, payment, toFees, toInterest, toPrincipal, newFees,
                principal, accrued, fees));
            date = eventDate;

            if ((principal + accrued + fees).IsZero && uncharged == 0m)
            {
                return new LoanProjection(rows, new PaysOff(eventDate, rows.Count, totalPaid, totalInterest, totalFees), confidence, assumptions);
            }

            if (kind != LoanPaymentKind.Regular)
            {
                continue;
            }

            regularCount++;
            var owedNow = principal + accrued + fees;
            stagnant = owedAfterLastRegular is { } previous && owedNow >= previous ? stagnant + 1 : 0;
            owedAfterLastRegular = owedNow;

            var nothingCanHelp = !HasExtrasAfter(input, eventDate) && !terms.Rates.ChangesAfter(eventDate).Any();
            if (stagnant >= StagnantPaymentLimit && nothingCanHelp)
            {
                var firstShortfall = rows.FirstOrDefault(r => r.Kind == LoanPaymentKind.Regular && r.Payment - r.ToFees <= r.NewInterest);
                return new LoanProjection(rows, new NeverPaysOff(
                    "The regular payment doesn't pay down more than the interest that accrues between payments.",
                    firstShortfall?.Date,
                    Money.Max(newInterest - (payment - toFees), zero)), confidence, assumptions);
            }

            if (regularCount >= input.MaxPayments)
            {
                return new LoanProjection(rows, new NeverPaysOff(
                    $"Not paid off within {input.MaxPayments} regular payments.", null, null), confidence, assumptions);
            }
        }

        return new LoanProjection(rows, new InsufficientData(["The payment schedule ends before the loan is paid off."]), confidence, assumptions);
    }

    private static List<string> Validate(SimpleInterestLoanInput input)
    {
        var (position, terms) = (input.Position, input.Terms);
        var missing = new List<string>();
        if (position.Principal.IsNegative || position.AccruedInterest.IsNegative || position.FeesDue.IsNegative)
        {
            missing.Add("Balances can't be negative; a credit balance needs a statement to confirm it.");
        }

        if (!terms.RegularPayment.IsPositive && (input.PlannedAmounts ?? []).Count == 0)
        {
            missing.Add("The regular payment amount.");
        }

        if (terms.Rates.SegmentOn(position.AsOf) is null)
        {
            missing.Add($"The interest rate in force on {position.AsOf:yyyy-MM-dd}.");
        }

        var currencies = new[] { position.Principal, position.AccruedInterest, position.FeesDue, terms.RegularPayment }
            .Select(m => m.Currency).Distinct().Count();
        if (currencies > 1)
        {
            missing.Add("All amounts must use the loan's currency.");
        }

        if (input.MaxPayments < 1)
        {
            missing.Add("A positive payment limit.");
        }

        return missing;
    }

    // Regular payments come from the schedule (with planner amounts); extras are
    // merged in date order, after the regular payment on the same day.
    private static IEnumerable<(DateOnly Date, LoanPaymentKind Kind, Money Amount, bool PrincipalOnly)> PaymentEvents(SimpleInterestLoanInput input)
    {
        var (position, terms) = (input.Position, input.Terms);
        var horizon = position.AsOf.AddYears((input.MaxPayments / 12) + 2);
        var calendar = input.Calendar.Build();
        var planned = (input.PlannedAmounts ?? []).ToDictionary(p => p.Date, p => p.Amount);
        var scheduled = terms.PaymentDates.Occurrences(position.AsOf, horizon, calendar).Select(o => o.Date).ToHashSet();

        var regular = scheduled.Select(date =>
            (Date: date, Kind: LoanPaymentKind.Regular,
             Amount: planned.TryGetValue(date, out var amount) ? amount : terms.RegularPayment, PrincipalOnly: false));

        var unscheduledPlans = planned
            .Where(p => p.Key >= position.AsOf && !scheduled.Contains(p.Key))
            .Select(p => (Date: p.Key, Kind: LoanPaymentKind.Extra, Amount: p.Value, PrincipalOnly: false));

        var extras = (input.ExtraPayments ?? [])
            .Where(e => e.Date >= position.AsOf)
            .Select(e => (e.Date, Kind: LoanPaymentKind.Extra, e.Amount, e.PrincipalOnly));

        return regular.Concat(unscheduledPlans).Concat(extras)
            .Where(e => e.Amount.IsPositive)
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Kind);
    }

    private static bool HasExtrasAfter(SimpleInterestLoanInput input, DateOnly date) =>
        (input.ExtraPayments ?? []).Any(e => e.Date > date && e.Amount.IsPositive);

    internal static Confidence ProjectionConfidence(Provenance position, RateSchedule rates)
    {
        // A projection rests on scheduled future payments, so it is at best Scheduled.
        var confidence = (Confidence)Math.Min((int)position.Confidence, (int)Confidence.Scheduled);
        return rates.HasAssumptions ? (Confidence)Math.Min((int)confidence, (int)Confidence.Estimated) : confidence;
    }

    private static IReadOnlyList<string> Assumptions(SimpleInterestLoanTerms terms)
    {
        var list = new List<string>
        {
            $"Interest accrues daily on principal only ({terms.DayCount}) and never compounds.",
            $"Each period's new interest is rounded to the cent when the payment posts ({terms.InterestRounding}).",
            $"Payments apply to {string.Join(", then ", terms.EffectiveAllocation.Order).ToLowerInvariant()}.",
            "Payments post on their scheduled dates; this is a projection, not a lender payoff quote.",
        };
        if (terms.Rates.HasAssumptions)
        {
            list.Add("Some future rates are assumptions, not announced rates.");
        }

        return list;
    }
}

internal static class Interest
{
    // Exact (unrounded) simple interest on principal for each day in [start, end),
    // splitting at rate changes and, for Actual/Actual, at year boundaries.
    // Null when a day has no known rate.
    public static decimal? Accrue(decimal principal, DateOnly start, DateOnly end, RateSchedule rates, DayCountConvention dayCount)
    {
        var total = 0m;
        for (var cursor = start; cursor < end;)
        {
            var segment = rates.SegmentOn(cursor);
            if (segment is null)
            {
                return null;
            }

            var next = rates.ChangesAfter(cursor).Where(d => d < end).DefaultIfEmpty(end).Min();
            if (dayCount == DayCountConvention.ActualActual)
            {
                var yearEnd = new DateOnly(cursor.Year + 1, 1, 1);
                next = yearEnd < next ? yearEnd : next;
            }

            var days = next.DayNumber - cursor.DayNumber;
            total += principal * DayCount.DailyRate(segment.Rate, dayCount, cursor) * days;
            cursor = next;
        }

        return total;
    }
}
