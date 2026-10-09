using System.Text.Json.Serialization;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Domain.Debts;

public enum BalanceBucketKind
{
    Purchase,
    CashAdvance,
    BalanceTransfer,
    Promotional,
}

// One APR bucket on a card. A promotional bucket moves into RevertsTo when the
// promotion ends.
public sealed record AprBucket(
    string Name,
    BalanceBucketKind Kind,
    RateSchedule Rates,
    DateOnly? PromoEndsOn = null,
    string? RevertsTo = null);

// Minimum due: the larger of Floor and a percentage of the statement balance
// (plus that cycle's interest and fees if the card says so), capped at the balance.
public sealed record MinimumPaymentRule(Money Floor, decimal FractionOfBalance, bool PlusInterestAndFees);

public sealed record PaymentDueRule
{
    [JsonConstructor]
    public PaymentDueRule(int? daysAfterClose = null, DayOfMonth? dayOfMonth = null)
    {
        if ((daysAfterClose is null) == (dayOfMonth is null))
        {
            throw new ArgumentException("Set either days after close or a day of the month.");
        }

        if (daysAfterClose is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(daysAfterClose), daysAfterClose, "Use 1 to 60 days.");
        }

        DaysAfterClose = daysAfterClose;
        DayOfMonth = dayOfMonth;
    }

    public int? DaysAfterClose { get; }

    public DayOfMonth? DayOfMonth { get; }

    public static PaymentDueRule AfterClose(int days) => new(daysAfterClose: days);

    public static PaymentDueRule OnDay(DayOfMonth day) => new(dayOfMonth: day);

    public DateOnly DueDateFor(DateOnly statementClose)
    {
        if (DaysAfterClose is { } days)
        {
            return statementClose.AddDays(days);
        }

        var due = DayOfMonth!.Value.In(statementClose.Year, statementClose.Month);
        if (due <= statementClose)
        {
            var next = statementClose.AddMonths(1);
            due = DayOfMonth.Value.In(next.Year, next.Month);
        }

        return due;
    }
}

public sealed record CreditCardTerms(
    DayOfMonth StatementClosingDay,
    PaymentDueRule PaymentDue,
    IReadOnlyList<AprBucket> Buckets,
    MinimumPaymentRule Minimum,
    DayCountConvention DayCount = DayCountConvention.Actual365Fixed,
    bool HasGracePeriod = true,
    MidpointRounding InterestRounding = MidpointRounding.AwayFromZero) : DebtTerms;
