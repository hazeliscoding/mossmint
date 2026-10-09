using Mossmint.Domain;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Engine.Loans;

public sealed record AmortizingLoanInput(
    LoanPosition Position,
    AmortizingLoanTerms Terms,
    CalendarSpec Calendar,
    IReadOnlyList<ExtraPayment>? ExtraPayments = null,
    int MaxPayments = 600);

// A loan charged a periodic rate (annual rate / periods per year) once per
// scheduled payment, whatever the number of days (docs/financial-models/loans.md).
// - The rate is the one in force on the payment date.
// - Interest is rounded to the cent each period; payments go to fees, interest,
//   then principal; the final payment is capped at what is owed.
// - Without a scheduled payment, the level payment is derived from the remaining
//   number of payments, rounded up to the cent (so the last payment is the
//   smallest), and re-derived when the rate changes. The last payment of the
//   term settles whatever remains, as lenders adjust a final payment for the
//   cents that per-period rounding leaves behind.
// - Extra payments go to principal and are charged no interest of their own.
public static class AmortizingLoan
{
    public static LoanProjection Project(AmortizingLoanInput input)
    {
        var (position, terms) = (input.Position, input.Terms);
        var confidence = SimpleInterestLoan.ProjectionConfidence(position.Provenance, terms.Rates);
        var assumptions = Assumptions(terms);
        var missing = Validate(input);
        if (missing.Count > 0)
        {
            return new LoanProjection([], new InsufficientData(missing), confidence, assumptions);
        }

        var zero = Money.Zero(position.Principal.Currency);
        if (position.Owed.IsZero)
        {
            return new LoanProjection([], new PaysOff(position.AsOf, 0, zero, zero, zero), confidence, assumptions);
        }

        var calendar = input.Calendar.Build();
        var horizon = position.AsOf.AddYears((input.MaxPayments / Math.Max(terms.PeriodsPerYear, 1)) + 2);
        var events = terms.PaymentDates.Occurrences(position.AsOf, horizon, calendar)
            .Select(o => (o.Date, Kind: LoanPaymentKind.Regular, Amount: (Money?)null))
            .Concat((input.ExtraPayments ?? []).Where(e => e.Date >= position.AsOf && e.Amount.IsPositive)
                .Select(e => (e.Date, Kind: LoanPaymentKind.Extra, Amount: (Money?)e.Amount)))
            .OrderBy(e => e.Date).ThenBy(e => e.Kind);

        var rows = new List<LoanScheduleRow>();
        var (principal, accrued, fees, date) = (position.Principal, position.AccruedInterest, position.FeesDue, position.AsOf);
        var (totalPaid, totalInterest, totalFees) = (zero, zero, zero);
        var regularCount = 0;
        AnnualRate? levelRate = null;
        Money level = zero;

        foreach (var (eventDate, kind, extraAmount) in events)
        {
            var (beginPrincipal, beginAccrued) = (principal, accrued);
            Money newInterest = zero, toFees = zero, toInterest = zero, toPrincipal, payment;

            if (kind == LoanPaymentKind.Extra)
            {
                toPrincipal = Money.Min(extraAmount!.Value, principal);
                payment = toPrincipal;
            }
            else
            {
                var rate = terms.Rates.SegmentOn(eventDate)!.Rate;
                var periodic = rate.Value / terms.PeriodsPerYear;
                newInterest = (principal * periodic).Round(terms.InterestRounding);
                accrued += newInterest;

                if (terms.ScheduledPayment is { } scheduled)
                {
                    level = scheduled;
                }
                else if (levelRate != rate)
                {
                    level = LevelPayment(principal, periodic, terms.RemainingPayments!.Value - regularCount);
                    levelRate = rate;
                }

                var isLastOfTerm = terms.ScheduledPayment is null && regularCount == terms.RemainingPayments!.Value - 1;
                var owed = principal + accrued + fees;
                payment = isLastOfTerm ? owed : Money.Min(level, owed);
                toFees = Money.Min(payment, fees);
                toInterest = Money.Min(payment - toFees, accrued);
                toPrincipal = Money.Min(payment - toFees - toInterest, principal);
                regularCount++;
            }

            principal -= toPrincipal;
            accrued -= toInterest;
            fees -= toFees;
            (totalPaid, totalInterest, totalFees) = (totalPaid + payment, totalInterest + toInterest, totalFees + toFees);
            rows.Add(new LoanScheduleRow(
                rows.Count + 1, eventDate, kind, eventDate.DayNumber - date.DayNumber,
                beginPrincipal, beginAccrued, newInterest, payment, toFees, toInterest, toPrincipal, zero,
                principal, accrued, fees));
            date = eventDate;

            if ((principal + accrued + fees).IsZero)
            {
                return new LoanProjection(rows, new PaysOff(eventDate, rows.Count, totalPaid, totalInterest, totalFees), confidence, assumptions);
            }

            var cannotRecover = !(input.ExtraPayments ?? []).Any(e => e.Date > eventDate) && !terms.Rates.ChangesAfter(eventDate).Any();
            if (kind == LoanPaymentKind.Regular && payment - toFees <= newInterest && cannotRecover)
            {
                return new LoanProjection(rows, new NeverPaysOff(
                    "The scheduled payment doesn't pay down more than each period's interest.",
                    rows.First(r => r.Kind == LoanPaymentKind.Regular && r.Payment - r.ToFees <= r.NewInterest).Date,
                    newInterest - (payment - toFees)), confidence, assumptions);
            }

            if (regularCount >= input.MaxPayments)
            {
                return new LoanProjection(rows, new NeverPaysOff($"Not paid off within {input.MaxPayments} regular payments.", null, null), confidence, assumptions);
            }
        }

        return new LoanProjection(rows, new InsufficientData(["The payment schedule ends before the loan is paid off."]), confidence, assumptions);
    }

    // The level payment that retires principal in n equal payments at periodic
    // rate r: P x r / (1 - (1 + r)^-n), or P / n at zero rate, rounded up to the cent.
    public static Money LevelPayment(Money principal, decimal periodicRate, int payments)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(payments, 1);
        decimal exact;
        if (periodicRate == 0m)
        {
            exact = principal.Amount / payments;
        }
        else
        {
            var growth = 1m;
            for (var i = 0; i < payments; i++)
            {
                growth *= 1m + periodicRate;
            }

            exact = principal.Amount * periodicRate / (1m - (1m / growth));
        }

        var scale = principal.Currency.MinorUnits == 0 ? 1m : principal.Currency.MinorUnits == 3 ? 1000m : 100m;
        return new Money(Math.Ceiling(exact * scale) / scale, principal.Currency);
    }

    private static List<string> Validate(AmortizingLoanInput input)
    {
        var (position, terms) = (input.Position, input.Terms);
        var missing = new List<string>();
        if (terms.PeriodsPerYear is < 1 or > 365)
        {
            missing.Add("A number of payment periods per year between 1 and 365.");
        }

        if (terms.ScheduledPayment is null && terms.RemainingPayments is null or < 1)
        {
            missing.Add("The scheduled payment, or the number of payments remaining.");
        }

        if (terms.ScheduledPayment is { IsPositive: false })
        {
            missing.Add("A positive scheduled payment.");
        }

        if (terms.Rates.SegmentOn(position.AsOf) is null)
        {
            missing.Add($"The interest rate in force on {position.AsOf:yyyy-MM-dd}.");
        }

        if (position.Principal.IsNegative || position.AccruedInterest.IsNegative || position.FeesDue.IsNegative)
        {
            missing.Add("Balances can't be negative; a credit balance needs a statement to confirm it.");
        }

        return missing;
    }

    private static IReadOnlyList<string> Assumptions(AmortizingLoanTerms terms)
    {
        var list = new List<string>
        {
            $"Interest is the annual rate / {terms.PeriodsPerYear} on principal, charged once per scheduled payment and rounded to the cent ({terms.InterestRounding}).",
            "Payments apply to fees, then interest, then principal; extra payments go to principal.",
            "Payments post on their scheduled dates; this is a projection, not a lender payoff quote.",
        };
        if (terms.ScheduledPayment is null)
        {
            list.Add("The level payment is derived from the remaining term and rounded up to the cent.");
        }

        if (terms.Rates.HasAssumptions)
        {
            list.Add("Some future rates are assumptions, not announced rates.");
        }

        return list;
    }
}
