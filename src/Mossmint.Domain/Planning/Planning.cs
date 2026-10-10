using System.Text.Json.Serialization;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Domain.Planning;

public readonly record struct BillId(Guid Value);

// A bill occurrence already paid, by the date its schedule produced.
public sealed record PaidBill(BillId Bill, DateOnly Nominal);

public readonly record struct IncomeId(Guid Value);

public readonly record struct GoalId(Guid Value);

public enum AmountKind
{
    Fixed,
    Estimated,
    Unknown,
}

// How sure we are of an amount. An estimate may carry a range; an unknown
// amount has none and blocks any answer that depends on it.
public sealed record AmountPolicy
{
    [JsonConstructor]
    public AmountPolicy(AmountKind kind, Money? expected = null, Money? low = null, Money? high = null)
    {
        switch (kind)
        {
            case AmountKind.Fixed when expected is null || low is not null || high is not null:
                throw new ArgumentException("A fixed amount has an expected value and no range.");
            case AmountKind.Estimated when expected is null:
                throw new ArgumentException("An estimate needs an expected value.");
            case AmountKind.Estimated when (low is { } l && l > expected) || (high is { } h && h < expected):
                throw new ArgumentException("An estimate's range must contain its expected value.");
            case AmountKind.Unknown when expected is not null || low is not null || high is not null:
                throw new ArgumentException("An unknown amount carries no values.");
        }

        Kind = kind;
        Expected = expected;
        Low = low;
        High = high;
    }

    public AmountKind Kind { get; }

    public Money? Expected { get; }

    public Money? Low { get; }

    public Money? High { get; }

    // The amount to plan with when guarding cash: the top of an estimate's range.
    [JsonIgnore]
    public Money? Conservative => Kind switch
    {
        AmountKind.Fixed => Expected,
        AmountKind.Estimated => High ?? Expected,
        _ => null,
    };

    public static AmountPolicy Fixed(Money amount) => new(AmountKind.Fixed, amount);

    public static AmountPolicy Estimated(Money expected, Money? low = null, Money? high = null) =>
        new(AmountKind.Estimated, expected, low, high);

    public static AmountPolicy Unknown() => new(AmountKind.Unknown);
}

// Reporting depends on purpose: only Expense is spending. Debt payments and
// transfers move money between the user's own accounts.
public enum BillPurpose
{
    Expense,
    DebtPayment,
    SavingsTransfer,
    Transfer,
}

public sealed record RecurringBill(
    BillId Id,
    string Name,
    AmountPolicy Amount,
    Schedule DueDates,
    BillPurpose Purpose,
    Provenance Provenance,
    AccountId? FundingAccount = null,
    AccountId? TargetAccount = null,
    bool IsAutopay = false);

// A one-off change to a single paycheck, keyed by the date the rule produced.
public sealed record PayAmountOverride(DateOnly Nominal, Money NetPay);

public sealed record IncomeSchedule(
    IncomeId Id,
    string Name,
    Money NetPay,
    Schedule PayDates,
    Provenance Provenance,
    IReadOnlyList<PayAmountOverride>? AmountOverrides = null,
    AccountId? DepositAccount = null);

// How much to set aside from each paycheck. A target, not a floor: missing it
// is reported as a shortfall, never hidden by the reserve floor.
public sealed record SavingsContributionTarget(Money PerPaycheck);

// Cash that must stay in the spending account on every day of the forecast.
public sealed record CashReserveFloor(Money Minimum);

public sealed record SavingsGoal(
    GoalId Id,
    string Name,
    Money Target,
    Money Saved,
    Provenance SavedProvenance,
    DateOnly? TargetDate = null);
