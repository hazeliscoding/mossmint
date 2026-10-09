using System.Text.Json.Serialization;
using Mossmint.Domain;

namespace Mossmint.Engine.Loans;

// Balances at the start of AsOf. Interest accrues for every day from AsOf up to,
// not including, a payment date, and a payment posts at the start of its day.
public sealed record LoanPosition(DateOnly AsOf, Money Principal, Money AccruedInterest, Money FeesDue, Provenance Provenance)
{
    [JsonIgnore]
    public Money Owed => Principal + AccruedInterest + FeesDue;
}

// A payment beyond the regular schedule. Principal-only payments skip interest
// and fees when the contract allows it.
public sealed record ExtraPayment(DateOnly Date, Money Amount, bool PrincipalOnly = true);

// Replaces the regular amount for the scheduled payment on Date (a planner row).
// A date with no scheduled payment becomes an extra payment.
public sealed record PlannedPaymentAmount(DateOnly Date, Money Amount);

public enum LoanPaymentKind
{
    Regular,
    Extra,
}

public sealed record LoanScheduleRow(
    int Number,
    DateOnly Date,
    LoanPaymentKind Kind,
    int Days,
    Money BeginPrincipal,
    Money BeginAccruedInterest,
    Money NewInterest,
    Money Payment,
    Money ToFees,
    Money ToInterest,
    Money ToPrincipal,
    Money NewFees,
    Money EndPrincipal,
    Money EndAccruedInterest,
    Money EndFees)
{
    [JsonIgnore]
    public Money EndOwed => EndPrincipal + EndAccruedInterest + EndFees;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$outcome")]
[JsonDerivedType(typeof(PaysOff), "pays-off")]
[JsonDerivedType(typeof(NeverPaysOff), "never-pays-off")]
[JsonDerivedType(typeof(InsufficientData), "insufficient-data")]
public abstract record PayoffOutcome;

// A projection, not a lender's payoff quote. Totals cover future payments only.
public sealed record PaysOff(DateOnly PayoffDate, int PaymentCount, Money TotalPaid, Money TotalInterest, Money TotalFees) : PayoffOutcome;

// The payments can't overcome accrual. There is no payoff date to show.
public sealed record NeverPaysOff(string Reason, DateOnly? FirstShortfallDate, Money? ShortfallPerPayment) : PayoffOutcome;

public sealed record InsufficientData(IReadOnlyList<string> Missing) : PayoffOutcome;

public sealed record LoanProjection(
    IReadOnlyList<LoanScheduleRow> Rows,
    PayoffOutcome Outcome,
    Confidence Confidence,
    IReadOnlyList<string> Assumptions)
{
    [JsonIgnore]
    public LoanScheduleRow? NextPayment => Rows.Count > 0 ? Rows[0] : null;
}
