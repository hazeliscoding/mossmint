using System.Text.Json.Serialization;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Domain.Debts;

// The contract terms of a debt; which model applies follows from the type.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$terms")]
[JsonDerivedType(typeof(SimpleInterestLoanTerms), "simple-interest-loan")]
[JsonDerivedType(typeof(AmortizingLoanTerms), "amortizing-loan")]
[JsonDerivedType(typeof(CreditCardTerms), "credit-card")]
public abstract record DebtTerms;

// A debt is a liability account plus its terms. Balances live in the account's
// snapshots, so a lender statement and a projection are never mixed up.
public sealed record DebtAccount(AccountId Account, DebtTerms Terms, Money? OriginalPrincipal = null, DateOnly? OpenedOn = null);

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
    MidpointRounding InterestRounding = MidpointRounding.AwayFromZero) : DebtTerms
{
    [JsonIgnore]
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
    MidpointRounding InterestRounding = MidpointRounding.AwayFromZero) : DebtTerms;
