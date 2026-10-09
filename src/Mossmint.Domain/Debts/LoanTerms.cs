using Mossmint.Domain.Scheduling;

namespace Mossmint.Domain.Debts;

// A loan whose interest accrues daily on principal only and is settled by each
// payment. Unpaid interest is carried separately and never compounds.
public sealed record SimpleInterestLoanTerms(
    RateSchedule Rates,
    DayCountConvention DayCount,
    Money RegularPayment,
    Schedule PaymentDates,
    PaymentAllocation? Allocation = null,
    bool AllowsPrincipalOnlyPayments = true,
    PrepaymentPenalty? Penalty = null,
    MidpointRounding InterestRounding = MidpointRounding.AwayFromZero)
{
    public PaymentAllocation EffectiveAllocation => Allocation ?? PaymentAllocation.Standard;
}

// A loan with a fixed periodic rate (annual rate / periods per year) charged
// once per period, whatever the number of days. Give the scheduled payment, or
// the remaining number of payments so the level payment can be derived.
public sealed record AmortizingLoanTerms(
    RateSchedule Rates,
    int PeriodsPerYear,
    Schedule PaymentDates,
    Money? ScheduledPayment = null,
    int? RemainingPayments = null,
    MidpointRounding InterestRounding = MidpointRounding.AwayFromZero);
